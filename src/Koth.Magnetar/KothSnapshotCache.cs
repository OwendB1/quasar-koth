using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Koth.Contracts;

namespace Koth.Magnetar
{
    // Immutable copy of one game-thread read. Request threads filter it without touching mod objects.
    internal sealed class KothView
    {
        private readonly KothSnapshot state;
        private readonly KothEvent[] events;

        internal KothView(KothSnapshot state, KothEvent[] events)
        {
            this.state = state;
            this.events = events;
        }

        internal DateTime CapturedUtc => state.CapturedUtc;
        internal string StreamId => state.StreamId;
        internal bool ResetSupported => state.ResetSupported;

        internal static void Validate(SnapshotRequest request)
        {
            if (request.Version != KothProtocol.Version || request.AfterSequence < 0)
                throw new ArgumentException("Unsupported KOTH request version or cursor.");
        }

        internal KothSnapshot For(SnapshotRequest request)
        {
            Validate(request);
            return new KothSnapshot
            {
                Available = state.Available, Status = state.Status, World = state.World, StreamId = state.StreamId,
                CapturedUtc = state.CapturedUtc, ResetSupported = state.ResetSupported, ResetStatus = state.ResetStatus,
                OldestSequence = state.OldestSequence, LastSequence = state.LastSequence,
                Scores = new List<KothScore>(state.Scores),
                Events = request.StreamId == state.StreamId
                    ? events.Where(e => e.Sequence > request.AfterSequence).Take(KothProtocol.EventPageSize).ToList()
                    : new List<KothEvent>()
            };
        }
    }

    // Lets snapshot requests skip the game thread. Update() refreshes the copy about once per
    // second while Quasar polls, and drops it after 30 idle seconds so a later poll never sees old data.
    internal sealed class KothSnapshotCache
    {
        internal const int RefreshTicks = 60;
        internal static readonly TimeSpan KeepWarm = TimeSpan.FromSeconds(30);
        private KothView? view;
        private long wantedUntilTicks;
        private int due; // Game thread only.

        internal KothView? Current => Volatile.Read(ref view);

        // Request threads.
        internal void Want(DateTime nowUtc) => Interlocked.Exchange(ref wantedUntilTicks, nowUtc.Add(KeepWarm).Ticks);
        internal KothSnapshot? Answer(SnapshotRequest request, DateTime nowUtc)
        {
            Want(nowUtc);
            return Current?.For(request);
        }

        // Game thread.
        internal void Publish(KothView next)
        {
            Volatile.Write(ref view, next);
            due = RefreshTicks;
        }
        internal void Clear() => Volatile.Write(ref view, null);

        // Returns true when it read the mod. A failed read clears the copy and retries a second later.
        internal bool Tick(DateTime nowUtc, Action refresh)
        {
            if (nowUtc.Ticks >= Interlocked.Read(ref wantedUntilTicks))
            {
                if (Current != null) Clear();
                due = 0;
                return false;
            }
            if (--due > 0) return false;
            due = RefreshTicks;
            try { refresh(); }
            catch { Clear(); throw; }
            return true;
        }
    }
}
