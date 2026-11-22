using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using KingOfTheHill;
using Koth.Contracts;
using Koth.Magnetar;
using Koth.Quasar;
using Koth.Browser.Pages;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Plugin.Abstractions.Commands;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Discord;
using Quasar.Plugin.Abstractions.Manifests;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    count++;
}
void Refuses(Action action, string name)
{
    try { action(); } catch { count++; return; }
    throw new Exception("FAILED: " + name);
}

Core.Seed();
var files = new Dictionary<string, string>();
Core.Write = (name, text) => files[name] = text;
var runtime = new KothRuntime(new Core());
var snapshot = runtime.Read(new(), "World");
Check(snapshot.Available && snapshot.Scores.Count == 2 && snapshot.Scores[0].Points == 24, "live scores preserve and order factions");
Check(snapshot.ResetSupported, "standalone reset supported");
NexusAPIns.NexusAPI.Running = true;
Check(!runtime.Read(new(), "World").ResetSupported, "Nexus reset disabled");
Refuses(() => runtime.Reset(new() { StreamId = snapshot.StreamId, Confirmation = "RESET" }, "World", Core.Write, name => files[name]), "Nexus reset refused");
Check(files.Count == 0, "Nexus refusal performs no writes");
NexusAPIns.NexusAPI.Running = false;
Refuses(() => runtime.Read(new() { Version = 999 }, "World"), "protocol mismatch refused");
runtime.Capture(Encoding.ASCII.GetBytes("spoofed"), false);
runtime.Capture(Encoding.ASCII.GetBytes("clear"), true);
runtime.Capture(new byte[3000], true);
runtime.Capture(Encoding.ASCII.GetBytes(" "), true);
Check(runtime.Read(new(), "World").LastSequence == 0, "untrusted, reset, empty, oversized messages ignored");
for (var i = 0; i < 300; i++) runtime.Capture(Encoding.ASCII.GetBytes("Faction scored " + i), true);
var page = runtime.Read(new() { StreamId = snapshot.StreamId, AfterSequence = 0 }, "World");
Check(page.OldestSequence == 45 && page.LastSequence == 300 && page.Events.Count == 32, "bounded event queue and pagination");
Check(runtime.Read(new() { StreamId = "old-world", AfterSequence = 0 }, "World").Events.Count == 0, "old world cursor cannot replay events");

// Exercise a real Harmony prefix on the fixture's mod handler, using its positional fourth argument.
var harmony = new Harmony("koth.tests");
harmony.Patch(runtime.PacketHandler, prefix: new HarmonyMethod(typeof(PacketGuard), nameof(PacketGuard.Prefix)));
runtime.PacketHandler.Invoke(new Core(), [(ushort)8008, Encoding.ASCII.GetBytes("clear"), 1UL, false]);
Check(Core.Resets == 0 && runtime.Read(new(), "World").Scores.Count == 2, "Harmony guard blocks client clear before mod mutation");
Refuses(() => runtime.Reset(new() { StreamId = "stale", Confirmation = "RESET" }, "World", Core.Write, name => files[name]), "stale reset refused");
Refuses(() => runtime.Reset(new() { StreamId = snapshot.StreamId, Confirmation = "reset" }, "World", Core.Write, name => files[name]), "reset needs exact confirmation");
Refuses(() => runtime.Reset(new() { StreamId = snapshot.StreamId, Confirmation = "RESET" }, "World", (_, _) => throw new IOException("full"), name => files[name]), "backup failure aborts reset");
Check(Core.Resets == 0, "backup failure cannot mutate scores");
Core.FailSave = true;
files["Scores.data"] = "old or corrupt persisted scores";
Refuses(() => runtime.Reset(new() { StreamId = snapshot.StreamId, Confirmation = "RESET" }, "World", Core.Write, name => files[name]), "swallowed mod save failure detected");
Check(runtime.Read(new(), "World").Scores.Count == 2 && files["Scores.data"].Contains("Faction"), "failed reset restores live and persisted scores");
Core.FailSave = false;
var reset = runtime.Reset(new() { StreamId = snapshot.StreamId, Confirmation = "RESET" }, "World", Core.Write, name => files[name]);
Check(reset.Snapshot.Scores.Count == 0 && files[reset.BackupFile].Contains("Faction"), "reset backs up live scores and verifies persisted clear");
Check(reset.Snapshot.StreamId != snapshot.StreamId, "reset invalidates old confirmations");
var beforeRetry = Core.Resets;
Refuses(() => runtime.Reset(new() { StreamId = snapshot.StreamId, Confirmation = "RESET" }, "World", Core.Write, name => files[name]), "replayed reset refused");
Check(Core.Resets == beforeRetry, "replayed reset does not clear later scores");
harmony.UnpatchAll("koth.tests");

var directory = Path.Combine(Path.GetTempPath(), "koth-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new KothStore(directory);
    Refuses(() => store.SaveSettings(new(true, "")), "enabled delivery requires channel");
    Refuses(() => store.SaveSettings(new(true, "../token")), "invalid channel rejected");
    Refuses(() => store.SaveSettings(new(true, "0")), "zero channel rejected");
    store.SaveSettings(new(true, "123456789012345678"));
    Check(new KothStore(directory).Settings == store.Settings, "dedicated channel survives restart");
    var companion = new FakeCompanion();
    var discord = new FakeDiscord();
    var service = new KothService(companion, discord, store, NullLogger<KothService>.Instance);
    var server = companion.GetConnectedServers()[0];
    await service.PollAsync(server, default);
    Check(discord.Messages.Count == 0, "initial baseline does not announce historical events");
    companion.Add("Capture awarded!");
    await service.PollAsync(server, default);
    await service.PollAsync(server, default);
    Check(discord.Messages.Count == 1 && discord.Messages[0].Channel == store.Settings.DiscordChannelId, "dedicated channel used once per event");
    var restarted = new KothService(companion, discord, new KothStore(directory), NullLogger<KothService>.Instance);
    await restarted.PollAsync(server, default);
    Check(discord.Messages.Count == 1, "persisted cursor prevents replay after Quasar restart");
    companion.Add("Next capture"); discord.Fail = true;
    await service.PollAsync(server, default);
    Check(store.Cursor(server.ServerId)!.Sequence == 1 && service.Status(server.ServerId).Message.Contains("retrying"), "failed post retains cursor and reports delivery failure");
    discord.Fail = false;
    await service.PollAsync(server, default);
    Check(discord.Messages.Count == 2 && store.Cursor(server.ServerId)!.Sequence == 2, "failed post retries successfully");
    store.SaveSettings(new(false, store.Settings.DiscordChannelId));
    companion.Add("Disabled capture"); await service.PollAsync(server, default);
    Check(discord.Messages.Count == 2, "disabled delivery does not post");
    store.SaveSettings(new(true, "999999999999999999"));
    await service.PollAsync(server, default);
    companion.Add("Changed channel"); await service.PollAsync(server, default);
    Check(discord.Messages.Count == 3 && discord.Messages[^1].Channel == "999999999999999999", "new channel used without replaying disabled events");
    await service.TestDiscordAsync(default);
    Check(discord.Messages[^1].Text.Contains("Quasar's bot"), "channel test reuses existing bot contract");
    companion.ClusterId = "cluster";
    Refuses(() => service.ResetAsync(new(server.ServerId, snapshot.StreamId, "RESET"), default).GetAwaiter().GetResult(), "cluster reset refused before transport");
    Check(companion.ResetCalls == 0, "cluster reset sends no companion request");
    companion.ClusterId = "";
    companion.StreamId = "replacement-world"; companion.Add("New world capture");
    await service.PollAsync(server, default);
    Check(store.Cursor(server.ServerId)!.StreamId == "replacement-world", "new world establishes independent cursor");

    // Real plugin registration: destructive calls deny agents, use shared policies, and expose bot commands.
    var registrations = new ServiceCollection();
    var plugin = new KothPlugin();
    var manifestJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "quasar-plugin.json"));
    var manifest = JsonSerializer.Deserialize<QuasarPluginManifest>(manifestJson, JsonSerializerOptions.Web)!;
    using var manifestDocument = JsonDocument.Parse(manifestJson);
    var browser = plugin.GetRazorAssemblies().Single();
    Check(browser == typeof(KothPage).Assembly && manifestDocument.RootElement.GetProperty("browserAssembly").GetString() == Path.GetFileName(browser.Location),
        "manifest delivers the registered Razor assembly");
    Check(browser != typeof(KothPlugin).Assembly && browser.GetReferencedAssemblies().All(a =>
        a.Name!.StartsWith("System.") || a.Name.StartsWith("Microsoft.AspNetCore.") || a.Name == "Microsoft.JSInterop"),
        "browser assembly depends only on framework components");
    Check(typeof(KothPage).GetCustomAttribute<RouteAttribute>()?.Template == "/koth" &&
        typeof(KothPage).GetCustomAttribute<AuthorizeAttribute>()?.Policy == "CanView", "KOTH browser route requires CanView");
    plugin.ConfigureServices(registrations, new QuasarPluginContext
    {
        InstallDirectory = directory, PluginDirectory = directory, CacheDirectory = directory,
        Manifest = manifest,
        Configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
        Environment = new FakeEnvironment()
    });
    var operations = registrations.Where(d => d.ServiceType == typeof(QuasarOperation)).Select(d => (QuasarOperation)d.ImplementationInstance!).ToArray();
    var resetOperation = operations.Single(o => o.OperationId == "scores.reset");
    Check(resetOperation.DefaultPolicies.SequenceEqual(["CanControlServers"]) && resetOperation.Metadata.AgentAccess == QuasarAgentAccess.Denied, "reset policy and agent denial registered");
    Check(operations.Single(o => o.OperationId == "settings.set").DefaultPolicies.SequenceEqual(["CanManageDiscord"]), "channel changes require Discord management permission");
    Check(registrations.Count(d => d.ServiceType == typeof(IQuasarChatCommand)) == 2, "commands extend existing bot through DI");
    using (var emptyProvider = new ServiceCollection().BuildServiceProvider())
    {
        var authorizationChecks = 0;
        var deniedContext = new QuasarOperationContext(new ClaimsPrincipal(), emptyProvider)
        { VerifyAccess = _ => { authorizationChecks++; throw new QuasarOperationException(403, "Refused"); } };
        var payload = JsonSerializer.SerializeToElement(new ResetScoresRequest("server", snapshot.StreamId, "RESET"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Refuses(() => resetOperation.InvokeAsync(deniedContext, payload, default).GetAwaiter().GetResult(), "reset rechecks authorization before resolving game service");
        var settingsOperation = operations.Single(o => o.OperationId == "settings.set");
        Refuses(() => settingsOperation.InvokeAsync(deniedContext, JsonSerializer.SerializeToElement(new KothSettings(true, "123")), default).GetAwaiter().GetResult(), "channel write rechecks authorization before resolving storage");
        Check(authorizationChecks == 2, "both writes reached shared authorization boundary");
    }

    // Render actual Razor page with viewer permissions. No Quasar process or browser required.
    var services = new ServiceCollection();
    services.AddLogging();
    var panelSdk = new PanelSdk();
    services.AddSingleton<IJSRuntime>(panelSdk);
    await using var provider = services.BuildServiceProvider();
    await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
    KothPage pageComponent = default!;
    var pageOutput = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<RenderFragmentHost>(
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["ChildContent"] = (RenderFragment)(builder =>
            {
                builder.OpenComponent<KothPage>(0);
                builder.AddComponentReferenceCapture(1, component => pageComponent = (KothPage)component);
                builder.CloseComponent();
            })
        })));
    var html = await renderer.Dispatcher.InvokeAsync(pageOutput.ToHtmlString);
    Check(html.Contains("King of the Hill") && html.Contains("No game servers are connected"), "Razor empty state renders");
    Check(!html.Contains("koth-channel\"") && !html.Contains("koth-reset-confirm"), "viewer rendering hides privileged controls");
    var firstServer = new KothServer("first", "First server", "", "");
    var secondServer = new KothServer("second", "Second server", "", "");
    panelSdk.Servers.AddRange([firstServer, secondServer]);
    await renderer.Dispatcher.InvokeAsync(() => InvokeUi(pageComponent, "LoadServers"));
    html = await renderer.Dispatcher.InvokeAsync(pageOutput.ToHtmlString);
    Check(html.Contains("First server") && html.Contains("Second server") && !html.Contains("<select"), "connected servers render expansion panels instead of a dropdown");
    Check(html.Contains("quasar-page-heading") && !html.Contains(">Refresh<"), "page uses host heading and needs no manual refresh");
    Check(panelSdk.ScoreRequests.Count == 0, "collapsed panels do not request scores");
    panelSdk.Servers.Remove(firstServer);
    await renderer.Dispatcher.InvokeAsync(() => InvokeUi(pageComponent, "LoadServers"));
    html = await renderer.Dispatcher.InvokeAsync(pageOutput.ToHtmlString);
    Check(!html.Contains("First server") && html.Contains("Second server"), "discovery removes disconnected server panels");

    // Exercise independent panel requests and confirmations through real component event handling.
    KothServerPanel firstPanel = default!, secondPanel = default!;
    var panelsOutput = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<RenderFragmentHost>(
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["ChildContent"] = (RenderFragment)(builder =>
            {
                builder.OpenComponent<KothServerPanel>(0); builder.AddAttribute(1, "Server", firstServer);
                builder.AddAttribute(2, "CanControlServers", true);
                builder.AddComponentReferenceCapture(3, component => firstPanel = (KothServerPanel)component); builder.CloseComponent();
                builder.OpenComponent<KothServerPanel>(4); builder.AddAttribute(5, "Server", secondServer);
                builder.AddAttribute(6, "CanControlServers", true);
                builder.AddComponentReferenceCapture(7, component => secondPanel = (KothServerPanel)component); builder.CloseComponent();
            })
        })));
    var slowRead = new TaskCompletionSource<KothSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    panelSdk.PendingScores["first"] = slowRead;
    Task firstOpen = Task.CompletedTask;
    await renderer.Dispatcher.InvokeAsync(() => { firstOpen = InvokeUi(firstPanel, "Toggle"); });
    await renderer.Dispatcher.InvokeAsync(() => InvokeUi(secondPanel, "Toggle"));
    Check(!firstOpen.IsCompleted && panelSdk.ScoreRequests.SequenceEqual(["first", "second"]), "slow server does not block another panel's request");
    var firstSnapshot = PanelSdk.Snapshot("first");
    panelSdk.PendingScores.Remove("first");
    panelSdk.Snapshots["first"] = firstSnapshot;
    slowRead.SetResult(firstSnapshot);
    await firstOpen;
    html = await renderer.Dispatcher.InvokeAsync(panelsOutput.ToHtmlString);
    Check(html.Contains("World-first") && html.Contains("World-second"), "expanded panels retain their own world data");
    Check(html.Split("id=\"koth-reset-confirm-").Length == 3, "each expanded server has its own reset confirmation input");

    typeof(KothServerPanel).GetField("confirmation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(firstPanel, "RESET");
    typeof(KothServerPanel).GetField("confirmation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(secondPanel, "RESET");
    panelSdk.Snapshots["first"] = PanelSdk.Snapshot("replacement");
    await renderer.Dispatcher.InvokeAsync(() => InvokeUi(firstPanel, "LoadScores"));
    string Confirmation(KothServerPanel panel) => (string)typeof(KothServerPanel).GetField("confirmation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
    Check(Confirmation(firstPanel) == "" && Confirmation(secondPanel) == "RESET", "world changes invalidate only the affected panel's confirmation");
    await renderer.Dispatcher.InvokeAsync(() => InvokeUi(secondPanel, "Reset"));
    Check(panelSdk.LastReset?.ServerId == "second" && panelSdk.LastReset.Confirmation == "RESET", "reset stays bound to its own server panel");
    await renderer.Dispatcher.InvokeAsync(() => InvokeUi(firstPanel, "Toggle"));
    html = await renderer.Dispatcher.InvokeAsync(panelsOutput.ToHtmlString);
    Check(!html.Contains("World-replacement") && html.Contains("World-second"), "collapsing one panel leaves the other panel open");

    panelSdk.PendingScores["first"] = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await renderer.Dispatcher.InvokeAsync(() => { firstOpen = InvokeUi(firstPanel, "Toggle"); });
    var controller = panelSdk.LastController!;
    await renderer.Dispatcher.InvokeAsync(() => renderer.DisposeAsync().AsTask());
    await firstOpen;
    Check(controller.Aborted, "removing a panel aborts its outstanding SDK request");
}
finally { Directory.Delete(directory, recursive: true); }
Console.WriteLine($"PASS: {count} KOTH checks");

static Task InvokeUi(ComponentBase component, string method) => EventCallback.Factory.Create(component,
    (Func<Task>)(() => (Task)component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null)!)).InvokeAsync();

public static class PacketGuard { public static bool Prefix(bool __3) => KothRuntime.AcceptPacket(__3); }
sealed class FakeCompanion : IQuasarCompanionChannel
{
    public string ClusterId = "", StreamId = "world-stream";
    public int ResetCalls;
    private readonly List<KothEvent> events = [];
    public void Add(string text) => events.Add(new() { Sequence = events.Count + 1, Text = text, TimestampUtc = DateTime.UtcNow });
    public IReadOnlyList<QuasarCompanionServer> GetConnectedServers() => [new("server", "agent", "Test server") { ClusterId = ClusterId }];
    public Task<TResponse> SendAsync<TRequest, TResponse>(string serverId, string pluginId, string operation, TRequest request, CancellationToken token)
    {
        if (operation == "reset") { ResetCalls++; throw new Exception("Unexpected reset"); }
        var read = (SnapshotRequest)(object)request!;
        return Task.FromResult((TResponse)(object)new KothSnapshot { Available = true, StreamId = StreamId, LastSequence = events.Count,
            OldestSequence = events.Count == 0 ? 1 : events[0].Sequence,
            Events = read.StreamId == StreamId ? events.Where(e => e.Sequence > read.AfterSequence).ToList() : [] });
    }
}
sealed class FakeDiscord : IQuasarDiscordChannels
{
    public bool Fail;
    public readonly List<(string Channel, string Text)> Messages = [];
    public Task<bool> PostAsync(string channelId, string text, CancellationToken token)
    {
        if (Fail) return Task.FromResult(false);
        Messages.Add((channelId, text)); return Task.FromResult(true);
    }
}
sealed class FakeEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Koth.Tests";
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}
sealed class RenderFragmentHost : ComponentBase
{
    [Parameter] public RenderFragment? ChildContent { get; set; }
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
        builder.AddContent(0, ChildContent);
}
sealed class PanelSdk : IJSRuntime
{
    public TestAbortController? LastController;
    public ValueTask<IJSObjectReference> InvokeConstructorAsync(string identifier, object?[]? args)
    {
        if (identifier != "AbortController") throw new InvalidOperationException("Unexpected browser object.");
        LastController = new TestAbortController();
        return ValueTask.FromResult<IJSObjectReference>(LastController);
    }
    public readonly List<KothServer> Servers = [];
    public readonly List<string> ScoreRequests = [];
    public readonly Dictionary<string, KothSnapshot> Snapshots = [];
    public readonly Dictionary<string, TaskCompletionSource<KothSnapshot>> PendingScores = [];
    public ResetScoresRequest? LastReset;
    public static KothSnapshot Snapshot(string server) => new()
    {
        Available = true, World = "World-" + server, StreamId = Guid.NewGuid().ToString("N"), ResetSupported = true,
        CapturedUtc = DateTime.UtcNow, Status = "Live scores"
    };
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
    public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
    {
        if (identifier != "quasarSdk.request" || args is not [string plugin, string operationId, var request, TestAbortController])
            throw new InvalidOperationException("Unexpected panel SDK call.");
        object value;
        if (plugin == "quasar.core" && operationId == "dashboard.permission") value = false;
        else if (plugin != KothProtocol.PluginId) throw new InvalidOperationException("Wrong operation owner.");
        else if (operationId == "servers.list") value = Servers.ToArray();
        else if (operationId == "settings.read") value = new KothSettings();
        else if (operationId == "discord.status") value = new DeliveryStatus(null, "No announcements yet.");
        else if (operationId == "scores.read")
        {
            var server = ((ServerRequest)request!).ServerId;
            ScoreRequests.Add(server);
            if (PendingScores.TryGetValue(server, out var pending)) return (T)(object)await pending.Task.WaitAsync(token);
            if (!Snapshots.ContainsKey(server)) Snapshots[server] = Snapshot(server);
            value = Snapshots[server];
        }
        else if (operationId == "scores.reset")
        {
            LastReset = (ResetScoresRequest)request!;
            value = new ResetResult { BackupFile = "backup.xml", Snapshot = Snapshot(LastReset.ServerId) };
        }
        else throw new InvalidOperationException("Unexpected render operation: " + operationId);
        return (T)value;
    }
}

sealed class TestAbortController : IJSObjectReference
{
    public bool Aborted;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
    {
        if (identifier != "abort") throw new InvalidOperationException();
        Aborted = true;
        return ValueTask.FromResult(default(T)!);
    }
}
