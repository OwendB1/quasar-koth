using System.Reflection;
using Koth.Browser.Pages;
using Koth.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Commands;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Plugin.Abstractions;

namespace Koth.Quasar;

public sealed class KothPlugin : IQuasarPlugin
{
    public string Id => KothProtocol.PluginId;
    public string DisplayName => "King of the Hill";
    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context)
    {
        services.AddSingleton(new KothStore(Path.Combine(context.InstallDirectory, "PluginData", Id)));
        services.AddSingleton<KothService>();
        services.AddHostedService(provider => provider.GetRequiredService<KothService>());
        services.AddSingleton<IQuasarChatCommand, KothScoreCommand>();
        services.AddSingleton<IQuasarChatCommand, KothResetCommand>();
        var read = new QuasarOperationMetadata { Effect = QuasarOperationEffect.Read, Area = "koth" };
        services.AddQuasarOperation<object, IReadOnlyList<KothServer>>(Id, "servers.list", [QuasarPolicyNames.CanView],
            (caller, _, _) => Task.FromResult(caller.Services.GetRequiredService<KothService>().Servers()), read);
        services.AddQuasarOperation<ServerRequest, KothSnapshot>(Id, "scores.read", [QuasarPolicyNames.CanView],
            (caller, request, token) => Checked(() => caller.Services.GetRequiredService<KothService>().ReadAsync(request.ServerId, token)), read);
        services.AddQuasarOperation<ServerRequest, DeliveryStatus>(Id, "discord.status", [QuasarPolicyNames.CanManageDiscord],
            (caller, request, _) => Task.FromResult(caller.Services.GetRequiredService<KothService>().Status(request.ServerId)), read);
        services.AddQuasarOperation<object, KothSettings>(Id, "settings.read", [QuasarPolicyNames.CanManageDiscord],
            (caller, _, _) => Task.FromResult(caller.Services.GetRequiredService<KothStore>().Settings), read);
        services.AddQuasarOperation<KothSettings, KothSettings>(Id, "settings.set", [QuasarPolicyNames.CanManageDiscord],
            (caller, request, token) => Checked(async () =>
            {
                await caller.EnsureAuthorizedAsync(token);
                caller.Services.GetRequiredService<KothStore>().SaveSettings(request);
                return request;
            }), new() { Area = "koth", AgentAccess = QuasarAgentAccess.Denied, Description = "Sets the dedicated KOTH Discord channel and enables or disables capture announcements through Quasar's existing bot." });
        services.AddQuasarOperation<object, bool>(Id, "discord.test", [QuasarPolicyNames.CanManageDiscord],
            (caller, _, token) => Checked(async () =>
            {
                await caller.EnsureAuthorizedAsync(token);
                await caller.Services.GetRequiredService<KothService>().TestDiscordAsync(token);
                return true;
            }), new() { Area = "koth", Reversal = QuasarReversal.NothingToReverse, AgentAccess = QuasarAgentAccess.Denied });
        services.AddQuasarOperation<ResetScoresRequest, ResetResult>(Id, "scores.reset", [QuasarPolicyNames.CanControlServers],
            (caller, request, token) => Checked(async () =>
            {
                await caller.EnsureAuthorizedAsync(token);
                if (caller.Undo != null) caller.Undo.Server = request.ServerId;
                return await caller.Services.GetRequiredService<KothService>().ResetAsync(request, token);
            }), new() { Area = "koth", AgentAccess = QuasarAgentAccess.Denied,
                Description = "Resets all KOTH faction scores on one standalone server after a verified XML backup. Requires the current world stream ID and confirmation RESET. Cluster reset is refused." });
    }
    private static async Task<T> Checked<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (ArgumentException error) { throw new QuasarOperationException(400, error.Message); }
        catch (InvalidOperationException error) { throw new QuasarOperationException(409, error.Message); }
    }
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context) { }
    public IEnumerable<Assembly> GetRazorAssemblies() { yield return typeof(KothPage).Assembly; }
    public IEnumerable<QuasarNavItem> GetNavItems()
    {
        yield return new("King of the Hill", "/koth", "<svg viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M5 16 3 5l5 4 4-6 4 6 5-4-2 11H5m0 2h14v3H5v-3Z\"/></svg>", QuasarNavZones.Main, 810, QuasarPolicyNames.CanView);
    }
    public IEnumerable<QuasarExtensionContribution> GetExtensions() => [];
}
