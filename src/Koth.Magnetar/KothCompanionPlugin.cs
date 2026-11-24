using System;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Koth.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PluginSdk.Logging;
using Sandbox;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Plugins;

namespace Koth.Magnetar
{
    public sealed class KothCompanionPlugin : IPlugin
    {
        private static readonly Logger Log = Logger.Create("KOTH");
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver(), MaxDepth = 32 };
        private readonly Harmony harmony = new Harmony(KothProtocol.PluginId);
        private MySession? session;
        // Written on the game thread; request threads read runtime and status to answer off-thread.
        private volatile KothRuntime? runtime;
        private bool listening;
        private bool attempted;
        private volatile bool disposed;
        private volatile string status = "Waiting for the KOTH world.";
        private string? refreshError;
        private int ticks;

        // Quasar's documented reflection contract avoids shipping Magnetar.Protocol with a hub companion.
        public string QuasarCompanionPluginId => KothProtocol.PluginId;
        public void Init(object gameInstance) { }

        public void Update()
        {
            if (disposed) return;
            if (session != MySession.Static)
            {
                Detach(); session = MySession.Static; attempted = false; ticks = 0;
                status = "KOTH mod is not loaded.";
            }
            var current = runtime;
            if (current != null) { Refresh(current); return; }
            if (session == null || attempted || MyAPIGateway.Session == null) return;
            if (session.ScriptManager == null) return;
            if (++ticks % 60 != 1) return;
            foreach (var assembly in session.ScriptManager.Scripts.Values)
            {
                var type = assembly.GetType("KingOfTheHill.Core", false);
                if (type == null) continue;
                var getComponent = typeof(MySession).GetMethods().Single(m => m.Name == "GetComponent"
                    && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
                var core = getComponent.MakeGenericMethod(type).Invoke(session, null) as MySessionComponentBase;
                if (core == null) continue;
                attempted = true;
                try
                {
                    if (!MyAPIGateway.Multiplayer.IsServer) return;
                    runtime = new KothRuntime(core);
                    harmony.Patch(runtime.PacketHandler, prefix: new HarmonyMethod(typeof(KothCompanionPlugin), nameof(LocalPacketsOnly)));
                    MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(8008, OnPacket);
                    listening = true;
                    status = "KOTH connected.";
                    Log.Info("KOTH companion attached; client-sent plugin messages are blocked.");
                }
                catch (Exception error)
                {
                    Detach(); status = "Unsupported KOTH version: " + error.GetBaseException().Message;
                    Log.Error(status, error);
                }
                break;
            }
        }

        // Keeps the snapshot copy fresh while Quasar polls. Cheap: one score read per second at most.
        private void Refresh(KothRuntime current)
        {
            try
            {
                if (current.Cache.Tick(DateTime.UtcNow, () => current.Refresh(MySession.Static?.Name ?? ""))) refreshError = null;
            }
            catch (Exception error)
            {
                var message = error.GetBaseException().Message;
                if (message != refreshError) Log.Warning("KOTH score refresh failed: " + message);
                refreshError = message;
            }
        }

        private static bool LocalPacketsOnly(bool __3) => KothRuntime.AcceptPacket(__3);
        private void OnPacket(ushort channel, byte[] message, ulong sender, bool fromServer) => runtime?.Capture(message, fromServer);

        public async Task<string> HandleQuasarCompanionRequestAsync(string operation, string payloadJson, CancellationToken token)
        {
            if (operation != "snapshot" && operation != "reset") throw new ArgumentException("Unknown KOTH operation.");
            if (payloadJson == null || payloadJson.Length > 4096) throw new ArgumentException("Invalid KOTH request size.");
            token.ThrowIfCancellationRequested();
            if (disposed) throw new ObjectDisposedException(nameof(KothCompanionPlugin));
            if (operation == "reset") return await OnGameThreadAsync(() =>
            {
                var current = runtime;
                if (current == null || session != MySession.Static) throw new InvalidOperationException("KOTH is not ready.");
                var request = JsonConvert.DeserializeObject<ResetRequest>(payloadJson, Json) ?? throw new ArgumentException("Reset request is missing.");
                // Reset republishes the cached snapshot with the new stream ID.
                var result = current.Reset(request, MySession.Static?.Name ?? "", Write, Read);
                Log.Warning("KOTH scores reset after verified backup: " + result.BackupFile);
                return JsonConvert.SerializeObject(result, Json);
            }, token).ConfigureAwait(false);

            var snapshotRequest = JsonConvert.DeserializeObject<SnapshotRequest>(payloadJson, Json) ?? throw new ArgumentException("Snapshot request is missing.");
            if (snapshotRequest.Version != KothProtocol.Version) throw new ArgumentException("Unsupported KOTH protocol version.");
            var attached = runtime;
            if (attached == null) return JsonConvert.SerializeObject(StatusSnapshot(), Json);
            // Serve the copy Update() keeps fresh. A busy game thread makes it older, not late.
            var cached = attached.Cache.Answer(snapshotRequest, DateTime.UtcNow);
            if (cached != null) return JsonConvert.SerializeObject(cached, Json);
            // No copy yet (just attached or idle): read once on the game thread, which also fills the cache.
            return await OnGameThreadAsync(() =>
            {
                var current = runtime;
                var snapshot = current != null && session == MySession.Static
                    ? current.Cache.Current?.For(snapshotRequest) ?? current.Read(snapshotRequest, MySession.Static?.Name ?? "")
                    : StatusSnapshot();
                return JsonConvert.SerializeObject(snapshot, Json);
            }, token).ConfigureAwait(false);
        }

        private KothSnapshot StatusSnapshot() => new KothSnapshot { Status = status, CapturedUtc = DateTime.UtcNow, World = MySession.Static?.Name ?? "" };

        private async Task<string> OnGameThreadAsync(Func<string> work, CancellationToken token)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var game = MySandboxGame.Static ?? throw new InvalidOperationException("Game is not available.");
            using (token.Register(() => completion.TrySetCanceled()))
            {
                game.Invoke(() =>
                {
                    // An abandoned request must not do work when the game thread catches up.
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        if (disposed) throw new ObjectDisposedException(nameof(KothCompanionPlugin));
                        completion.TrySetResult(work());
                    }
                    catch (Exception error) { completion.TrySetException(error.GetBaseException()); }
                }, "KOTH:Companion");
                return await completion.Task.ConfigureAwait(false);
            }
        }

        private string Read(string name)
        {
            using (var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(name, runtime!.SessionType)) return reader.ReadToEnd();
        }
        private void Write(string name, string text)
        {
            using (var writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(name, runtime!.SessionType)) writer.Write(text);
        }
        private void Detach()
        {
            if (listening && MyAPIGateway.Multiplayer != null) MyAPIGateway.Multiplayer.UnregisterSecureMessageHandler(8008, OnPacket);
            listening = false;
            runtime?.Cache.Clear(); runtime = null;
            harmony.UnpatchAll(KothProtocol.PluginId);
        }
        public void Dispose()
        {
            disposed = true;
            var game = MySandboxGame.Static;
            if (game != null) game.Invoke(Detach, "KOTH:Detach");
            else harmony.UnpatchAll(KothProtocol.PluginId);
        }
    }
}
