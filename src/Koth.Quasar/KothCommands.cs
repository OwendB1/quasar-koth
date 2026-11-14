using System.Text.Json;
using Koth.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions.Commands;
using Quasar.Plugin.Abstractions.Operations;

namespace Koth.Quasar;

internal sealed class KothScoreCommand(KothService service) : IQuasarChatCommand
{
    public QuasarChatCommandInfo Info { get; } = new("koth", "Shows King of the Hill scores", "server", "Server ID, list, or score for the current game server");
    public async Task HandleAsync(QuasarChatCommandRequest request, CancellationToken token)
    {
        var servers = service.Servers();
        var name = request.Text.Trim();
        if (name == "list")
        {
            await request.ReplyAsync(servers.Count == 0 ? "No game servers are connected." :
                string.Join("\n", servers.Select(s => $"{s.DisplayName}: {s.ServerId}")), token);
            return;
        }
        if (name == "score") name = request.Source.Server ?? (servers.Count == 1 ? servers[0].ServerId : "");
        var server = servers.SingleOrDefault(s => s.ServerId == name);
        if (server == null) { await request.ReplyAsync("Choose a server ID. Use koth with server = list to see connected servers.", token); return; }
        try
        {
            var snapshot = await service.ReadAsync(server.ServerId, token);
            var text = !snapshot.Available ? snapshot.Status : snapshot.Scores.Count == 0 ? "No KOTH scores yet." :
                string.Join("\n", snapshot.Scores.Take(50).Select(s => $"{s.Planet}: [{s.FactionTag}] {s.FactionName}: {s.Points}")) +
                (snapshot.Scores.Count > 50 ? "\nMore entries are available on Quasar's KOTH page." : "");
            await request.ReplyAsync(KothService.Escape($"{server.DisplayName} · {snapshot.World}\n{text}"), token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { await request.ReplyAsync("KOTH scores are unavailable: " + KothService.Escape(error.Message), token); }
    }
}

internal sealed class KothResetCommand(IServiceScopeFactory scopes) : IQuasarChatCommand
{
    public QuasarChatCommandInfo Info { get; } = new("koth-reset", "Backs up and resets KOTH scores on a standalone server", "confirmation",
        "server-id | world-stream-id | RESET (get the stream ID from Quasar's KOTH page)", QuasarChatCommandScope.Admin);
    public async Task HandleAsync(QuasarChatCommandRequest request, CancellationToken token)
    {
        if (request.Caller == null) { await request.ReplyAsync("A mapped Quasar administrator is required.", token); return; }
        var parts = request.Text.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || parts[2] != "RESET")
        {
            await request.ReplyAsync("Reset ALL scores on one standalone server: server-id | world-stream-id | RESET. Copy IDs from the KOTH page. A verified XML backup is kept in the mod's world storage.", token);
            return;
        }
        try
        {
            using var scope = scopes.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IQuasarOperationDispatcher>();
            var result = await dispatcher.DispatchAsync(new(request.Caller, scope.ServiceProvider), KothProtocol.PluginId, "scores.reset",
                JsonSerializer.SerializeToElement(new ResetScoresRequest(parts[0], parts[1], parts[2]), new JsonSerializerOptions(JsonSerializerDefaults.Web)), token);
            await request.ReplyAsync(result.StatusCode == 200 && result.Value is ResetResult reset
                ? "KOTH scores reset. Backup: " + reset.BackupFile : result.Error ?? "KOTH reset was refused.", token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { await request.ReplyAsync("KOTH reset failed: " + KothService.Escape(error.Message), token); }
    }
}
