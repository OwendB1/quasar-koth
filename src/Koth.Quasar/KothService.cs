using System.Collections.Concurrent;
using Koth.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Discord;

namespace Koth.Quasar;

internal sealed class KothService(IQuasarCompanionChannel companion, IQuasarDiscordChannels discord,
    KothStore store, ILogger<KothService> logger) : BackgroundService
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, DeliveryStatus> delivery = new(StringComparer.Ordinal);
    internal IReadOnlyList<KothServer> Servers() => companion.GetConnectedServers()
        .Select(s => new KothServer(s.ServerId, s.DisplayName, s.ClusterId, s.ClusterSlot)).OrderBy(s => s.DisplayName).ToArray();
    internal DeliveryStatus Status(string serverId) => delivery.GetValueOrDefault(serverId) ?? new(null, "No announcements sent yet.");
    internal QuasarCompanionServer Resolve(string serverId) => companion.GetConnectedServers().SingleOrDefault(s => s.ServerId == serverId)
        ?? throw new ArgumentException("Select a connected server or node from the KOTH server list.");
    internal Task<KothSnapshot> ReadAsync(string serverId, CancellationToken token)
    {
        Resolve(serverId);
        return ReadAsync(serverId, new SnapshotRequest(), token);
    }
    private async Task<KothSnapshot> ReadAsync(string serverId, SnapshotRequest request, CancellationToken token)
    {
        var result = await companion.SendAsync<SnapshotRequest, KothSnapshot>(serverId, KothProtocol.PluginId, "snapshot", request, TimeSpan.FromSeconds(10), token);
        if (result.Version != KothProtocol.Version) throw new InvalidOperationException("Update the KOTH UI and companion together.");
        // The companion answers from a copy refreshed on the game thread. An old copy means the thread is stalled.
        result.Status = StaleMessage(result) ?? result.Status;
        return result;
    }
    internal static string? StaleMessage(KothSnapshot snapshot)
    {
        var age = DateTime.UtcNow - snapshot.CapturedUtc;
        return snapshot.Available && age > StaleAfter ? $"Scores are {(long)age.TotalSeconds} s old; the server's game thread is busy." : null;
    }
    internal Task<ResetResult> ResetAsync(ResetScoresRequest request, CancellationToken token)
    {
        var server = Resolve(request.ServerId);
        if (server.ClusterId.Length != 0)
            throw new ArgumentException("Cluster score reset is not supported. KOTH's Nexus replication needs a separate score authority.");
        if (request.Confirmation != "RESET" || request.StreamId?.Length != 32)
            throw new ArgumentException("Refresh scores and confirm by typing RESET.");
        return companion.SendAsync<ResetRequest, ResetResult>(server.ServerId, KothProtocol.PluginId, "reset",
            new ResetRequest { StreamId = request.StreamId, Confirmation = request.Confirmation }, token);
    }
    internal async Task TestDiscordAsync(CancellationToken token)
    {
        var settings = store.Settings;
        if (settings.DiscordChannelId.Length == 0) throw new ArgumentException("Save a KOTH channel ID first.");
        if (!await discord.PostAsync(settings.DiscordChannelId, "KOTH announcements are connected through Quasar's bot.", token))
            throw new InvalidOperationException("Quasar's bot could not post. Check the saved channel ID, guild, bot connection, and View Channel / Send Messages permissions.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Servers poll independently: a stalled server keeps its own poll and never delays the others.
        // Only this loop touches the table, so one server never has two polls at once.
        var running = new Dictionary<string, Task>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                var servers = companion.GetConnectedServers();
                foreach (var server in servers)
                {
                    if (running.TryGetValue(server.ServerId, out var busy) && !busy.IsCompleted) continue;
                    running[server.ServerId] = PollSafelyAsync(server, stoppingToken);
                }
                var connected = servers.Select(s => s.ServerId).ToHashSet(StringComparer.Ordinal);
                foreach (var key in running.Where(p => p.Value.IsCompleted && !connected.Contains(p.Key)).Select(p => p.Key).ToArray()) running.Remove(key);
                foreach (var key in delivery.Keys.Where(k => !connected.Contains(k))) delivery.TryRemove(key, out _);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(running.Values); }
    }

    internal async Task PollSafelyAsync(QuasarCompanionServer server, CancellationToken token)
    {
        try { await PollAsync(server, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            var message = "Announcement collection failed: " + error.Message;
            if (Status(server.ServerId).Message != message) logger.LogWarning(error, "KOTH collection failed for {Server}", server.DisplayName);
            delivery[server.ServerId] = Status(server.ServerId) with { Message = message };
        }
    }

    internal async Task PollAsync(QuasarCompanionServer server, CancellationToken token)
    {
        var cursor = store.Cursor(server.ServerId);
        var snapshot = await ReadAsync(server.ServerId, new SnapshotRequest
        {
            StreamId = cursor?.StreamId ?? "", AfterSequence = cursor?.Sequence ?? long.MaxValue
        }, token);
        if (!snapshot.Available)
        {
            delivery[server.ServerId] = new(null, snapshot.Status);
            return;
        }
        // Buffered events in an old copy are still valid; report the delay and keep delivering.
        var stale = StaleMessage(snapshot);
        if (stale != null) delivery[server.ServerId] = Status(server.ServerId) with { Message = stale };
        var settings = store.Settings;
        if (!settings.AnnouncementsEnabled || cursor == null || cursor.StreamId != snapshot.StreamId)
        {
            // First observation establishes a baseline. Enabling delivery never replays old captures.
            store.SaveCursor(server.ServerId, snapshot.StreamId, snapshot.LastSequence);
            delivery[server.ServerId] = Status(server.ServerId) with { Message = stale ?? (settings.AnnouncementsEnabled ? "Waiting for new KOTH announcements." : "Discord announcements are disabled.") };
            return;
        }
        if (cursor.Sequence < snapshot.OldestSequence - 1)
        {
            logger.LogWarning("KOTH announcement buffer overflow on {Server}; missed events cannot be recovered", server.DisplayName);
            delivery[server.ServerId] = Status(server.ServerId) with { Message = "Some announcements expired from the server buffer." };
        }
        foreach (var item in snapshot.Events)
        {
            if (item.Sequence <= cursor.Sequence) continue;
            // Re-read before each send so a disabled or changed channel takes effect during a batch.
            if (store.Settings != settings) return;
            var text = $"**KOTH · {Escape(server.DisplayName)}**\n{Escape(item.Text)}";
            if (!await discord.PostAsync(settings.DiscordChannelId, text, token))
            {
                delivery[server.ServerId] = Status(server.ServerId) with { Message = "Bot could not post to the KOTH channel; retrying while events remain buffered." };
                return;
            }
            store.SaveCursor(server.ServerId, snapshot.StreamId, item.Sequence);
            cursor = new(snapshot.StreamId, item.Sequence, DateTime.UtcNow);
            delivery[server.ServerId] = new(DateTime.UtcNow, "KOTH announcement delivered.");
        }
    }
    internal static string Escape(string text) => text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_")
        .Replace("`", "\\`").Replace("~", "\\~").Replace("|", "\\|").Replace(">", "\\>")
        .Replace("<", "\\<").Replace("[", "\\[").Replace("]", "\\]");
}
