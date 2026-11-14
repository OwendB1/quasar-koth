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
        private KothRuntime? runtime;
        private bool listening;
        private bool attempted;
        private volatile bool disposed;
        private string status = "Waiting for the KOTH world.";
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

        private static bool LocalPacketsOnly(bool __3) => KothRuntime.AcceptPacket(__3);
        private void OnPacket(ushort channel, byte[] message, ulong sender, bool fromServer) => runtime?.Capture(message, fromServer);

        public async Task<string> HandleQuasarCompanionRequestAsync(string operation, string payloadJson, CancellationToken token)
        {
            if (operation != "snapshot" && operation != "reset") throw new ArgumentException("Unknown KOTH operation.");
            if (payloadJson == null || payloadJson.Length > 4096) throw new ArgumentException("Invalid KOTH request size.");
            token.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var game = MySandboxGame.Static ?? throw new InvalidOperationException("Game is not available.");
            using (token.Register(() => completion.TrySetCanceled()))
            {
                game.Invoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        if (disposed) throw new ObjectDisposedException(nameof(KothCompanionPlugin));
                        var world = MySession.Static?.Name ?? "";
                        if (operation == "snapshot")
                        {
                            var request = JsonConvert.DeserializeObject<SnapshotRequest>(payloadJson, Json) ?? throw new ArgumentException("Snapshot request is missing.");
                            if (request.Version != KothProtocol.Version) throw new ArgumentException("Unsupported KOTH protocol version.");
                            var snapshot = runtime != null && session == MySession.Static
                                ? runtime.Read(request, world)
                                : new KothSnapshot { Status = status, CapturedUtc = DateTime.UtcNow, World = world };
                            completion.TrySetResult(JsonConvert.SerializeObject(snapshot, Json));
                        }
                        else
                        {
                            if (runtime == null || session != MySession.Static) throw new InvalidOperationException("KOTH is not ready.");
                            var request = JsonConvert.DeserializeObject<ResetRequest>(payloadJson, Json) ?? throw new ArgumentException("Reset request is missing.");
                            var result = runtime.Reset(request, world, Write, Read);
                            Log.Warning("KOTH scores reset after verified backup: " + result.BackupFile);
                            completion.TrySetResult(JsonConvert.SerializeObject(result, Json));
                        }
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
            listening = false; runtime = null;
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
