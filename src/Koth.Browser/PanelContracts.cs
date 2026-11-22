namespace Koth.Contracts;

public sealed record KothServer(string ServerId, string DisplayName, string ClusterId, string ClusterSlot);
public sealed record ServerRequest(string ServerId);
public sealed record ResetScoresRequest(string ServerId, string StreamId, string Confirmation);
public sealed record DeliveryStatus(DateTime? LastSentUtc, string Message);
public sealed record KothSettings(bool AnnouncementsEnabled = false, string DiscordChannelId = "");
