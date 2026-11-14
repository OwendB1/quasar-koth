using System.Globalization;
using System.Text.Json;

namespace Koth.Quasar;

public sealed record KothSettings(bool AnnouncementsEnabled = false, string DiscordChannelId = "");
internal sealed record DeliveryCursor(string StreamId, long Sequence, DateTime UpdatedUtc);

internal sealed class KothStore
{
    private sealed class State
    {
        public KothSettings Settings { get; set; } = new();
        public Dictionary<string, DeliveryCursor> Cursors { get; set; } = new(StringComparer.Ordinal);
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private readonly string path;
    private State state;

    internal KothStore(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "settings.json");
        state = File.Exists(path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("KOTH settings are invalid.")
            : new State();
        if (state.Settings == null || state.Cursors == null) throw new InvalidDataException("KOTH settings are invalid.");
        Validate(state.Settings);
        if (state.Cursors.Any(p => p.Value == null || p.Value.StreamId == null || p.Value.Sequence < 0))
            throw new InvalidDataException("KOTH delivery cursors are invalid.");
    }
    internal KothSettings Settings { get { lock (gate) return state.Settings; } }
    internal DeliveryCursor? Cursor(string serverId) { lock (gate) return state.Cursors.GetValueOrDefault(serverId); }

    internal void SaveSettings(KothSettings settings)
    {
        Validate(settings);
        lock (gate) Save(new State { Settings = settings,
            Cursors = !state.Settings.AnnouncementsEnabled && settings.AnnouncementsEnabled ? new(StringComparer.Ordinal) : new(state.Cursors, StringComparer.Ordinal) });
    }
    internal void SaveCursor(string serverId, string streamId, long sequence)
    {
        lock (gate)
        {
            if (state.Cursors.TryGetValue(serverId, out var previous) && previous.StreamId == streamId && previous.Sequence == sequence) return;
            var cursors = state.Cursors.Where(p => p.Value.UpdatedUtc > DateTime.UtcNow.AddDays(-30))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            cursors[serverId] = new(streamId, sequence, DateTime.UtcNow);
            Save(new State { Settings = state.Settings, Cursors = cursors });
        }
    }
    internal static void Validate(KothSettings settings)
    {
        if (settings == null || settings.DiscordChannelId == null) throw new ArgumentException("KOTH settings and channel ID are required.");
        if (settings.DiscordChannelId.Length > 20 || (settings.DiscordChannelId.Length != 0 &&
            (!ulong.TryParse(settings.DiscordChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)))
            throw new ArgumentException("Discord channel ID must be a positive numeric ID.");
        if (settings.AnnouncementsEnabled && settings.DiscordChannelId.Length == 0)
            throw new ArgumentException("Set a KOTH Discord channel before enabling announcements.");
    }
    private void Save(State next)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, Json));
            File.Move(temporary, path, overwrite: true);
            state = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
