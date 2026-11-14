using System;
using System.Collections.Generic;

namespace Koth.Contracts
{
    public static class KothProtocol
    {
        public const string PluginId = "owendb.koth";
        public const int Version = 1;
        public const int EventCapacity = 256;
        public const int EventPageSize = 32;
    }

    public sealed class SnapshotRequest
    {
        public int Version { get; set; } = KothProtocol.Version;
        public string StreamId { get; set; } = "";
        public long AfterSequence { get; set; } = long.MaxValue;
    }

    public sealed class KothSnapshot
    {
        public int Version { get; set; } = KothProtocol.Version;
        public bool Available { get; set; }
        public string Status { get; set; } = "KOTH mod is not loaded.";
        public string World { get; set; } = "";
        public string StreamId { get; set; } = "";
        public DateTime CapturedUtc { get; set; }
        public bool ResetSupported { get; set; }
        public string ResetStatus { get; set; } = "KOTH is not ready.";
        public long OldestSequence { get; set; }
        public long LastSequence { get; set; }
        public List<KothScore> Scores { get; set; } = new List<KothScore>();
        public List<KothEvent> Events { get; set; } = new List<KothEvent>();
    }

    public sealed class KothScore
    {
        public string Planet { get; set; } = "";
        public long FactionId { get; set; }
        public string FactionName { get; set; } = "";
        public string FactionTag { get; set; } = "";
        public int Points { get; set; }
        public string GridName { get; set; } = "";
    }

    public sealed class KothEvent
    {
        public long Sequence { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class ResetRequest
    {
        public int Version { get; set; } = KothProtocol.Version;
        public string StreamId { get; set; } = "";
        public string Confirmation { get; set; } = "";
    }

    public sealed class ResetResult
    {
        public string BackupFile { get; set; } = "";
        public KothSnapshot Snapshot { get; set; } = new KothSnapshot();
    }
}
