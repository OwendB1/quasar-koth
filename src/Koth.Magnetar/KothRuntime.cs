using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Serialization;
using Koth.Contracts;

namespace Koth.Magnetar
{
    // All access to mod objects belongs on the simulation thread. No compile-time mod dependency.
    // Request threads only read the immutable KothView published in Cache.
    internal sealed class KothRuntime
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private readonly object core;
        private readonly FieldInfo planets;
        private readonly PropertyInfo sessionScores;
        private readonly XmlSerializer serializer;
        internal Type SessionType { get; }
        internal MethodInfo PacketHandler { get; }
        internal string StreamId { get; private set; } = Guid.NewGuid().ToString("N");
        internal KothSnapshotCache Cache { get; } = new KothSnapshotCache();
        private readonly Queue<KothEvent> events = new Queue<KothEvent>();
        private KothEvent[]? eventCopy;
        private long sequence;

        internal KothRuntime(object core)
        {
            this.core = core;
            var type = core.GetType();
            planets = type.GetField("Planets", Members) ?? throw new NotSupportedException("KOTH Planets field is missing.");
            if (!planets.IsStatic || !(planets.GetValue(null) is IList))
                throw new NotSupportedException("KOTH score collection is incompatible.");
            PacketHandler = type.GetMethod("PluginHandleIncomingPacket", Members, null,
                new[] { typeof(ushort), typeof(byte[]), typeof(ulong), typeof(bool) }, null)
                ?? throw new NotSupportedException("KOTH plugin message handler is incompatible.");
            if (PacketHandler.IsStatic || PacketHandler.ReturnType != typeof(void))
                throw new NotSupportedException("KOTH plugin message handler is incompatible.");
            SessionType = type.Assembly.GetType("KingOfTheHill.Descriptions.Session", true)!;
            sessionScores = SessionType.GetProperty("PlanetScores")
                ?? throw new NotSupportedException("KOTH session schema is incompatible.");
            if (sessionScores.PropertyType != planets.FieldType || !sessionScores.CanWrite)
                throw new NotSupportedException("KOTH session score collection is incompatible.");
            serializer = new XmlSerializer(SessionType);
            // Probe the complete shape before reporting availability or enabling reset.
            Build("");
        }

        internal static bool AcceptPacket(bool fromServer) => fromServer;

        internal void Capture(byte[] message, bool fromServer)
        {
            if (!AcceptPacket(fromServer) || message == null || message.Length == 0 || message.Length > 2048) return;
            var text = Encoding.ASCII.GetString(message).Trim();
            if (text.Length == 0 || text == "clear") return;
            events.Enqueue(new KothEvent { Sequence = ++sequence, TimestampUtc = DateTime.UtcNow, Text = text });
            while (events.Count > KothProtocol.EventCapacity) events.Dequeue();
            eventCopy = null;
        }

        internal KothSnapshot Read(SnapshotRequest request, string world)
        {
            KothView.Validate(request);
            return Refresh(world).For(request);
        }

        // Reads the mod once and publishes the copy for request threads.
        internal KothView Refresh(string world)
        {
            var view = Build(world);
            Cache.Publish(view);
            return view;
        }

        private KothView Build(string world)
        {
            var result = new KothSnapshot
            {
                Available = true, Status = "Live scores; client reset packets blocked.", World = world,
                StreamId = StreamId, CapturedUtc = DateTime.UtcNow, LastSequence = sequence,
                OldestSequence = events.Count == 0 ? sequence + 1 : events.Peek().Sequence
            };
            var nexus = core.GetType().Assembly.GetType("NexusAPIns.NexusAPI");
            var nexusCheck = nexus?.GetMethod("IsRunningNexus", BindingFlags.Public | BindingFlags.Static);
            result.ResetSupported = nexus == null || (nexusCheck?.ReturnType == typeof(bool) && (bool)nexusCheck.Invoke(null, null)! == false);
            result.ResetStatus = result.ResetSupported ? "Standalone score reset is available." : "Nexus score replication is active or unknown; reset is unavailable.";
            foreach (var planet in (IList)planets.GetValue(null)!)
            {
                if (planet == null) throw new NotSupportedException("KOTH has a null planet entry.");
                var name = Text(planet, "Name");
                foreach (var score in (IEnumerable)Get(planet, "Scores"))
                {
                    if (result.Scores.Count >= 10000) throw new NotSupportedException("KOTH exceeds the 10,000 score entry limit.");
                    result.Scores.Add(new KothScore
                    {
                        Planet = name, FactionId = (long)Get(score, "FactionId"),
                        FactionName = Text(score, "FactionName"), FactionTag = Text(score, "FactionTag"),
                        Points = (int)Get(score, "Points"), GridName = Text(score, "GridName")
                    });
                }
            }
            result.Scores = result.Scores.OrderBy(s => s.Planet, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.Points).ThenBy(s => s.FactionTag, StringComparer.OrdinalIgnoreCase).ToList();
            // The buffer copy is shared by every view until the next capture or reset.
            return new KothView(result, eventCopy ??= events.ToArray());
        }

        internal ResetResult Reset(ResetRequest request, string world, Action<string, string> write, Func<string, string> read)
        {
            if (request.Version != KothProtocol.Version || request.StreamId != StreamId || request.Confirmation != "RESET")
                throw new ArgumentException("Refresh this world and confirm by typing RESET.");
            if (!Build(world).ResetSupported)
                throw new InvalidOperationException("Nexus score replication prevents a safe standalone reset.");
            var session = Activator.CreateInstance(SessionType)!;
            sessionScores.SetValue(session, planets.GetValue(null), null);
            string original;
            using (var writer = new StringWriter()) { serializer.Serialize(writer, session); original = writer.ToString(); }
            var backup = "QuasarKoth-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Guid.NewGuid().ToString("N") + ".xml";
            write(backup, original);
            if (read(backup) != original) throw new IOException("KOTH backup verification failed; scores were not reset.");
            try
            {
                // Preserve the mod's own save and Nexus clear propagation, without sending a network packet.
                PacketHandler.Invoke(core, new object[] { (ushort)8008, Encoding.ASCII.GetBytes("clear"), 0UL, true });
                if (((IList)planets.GetValue(null)!).Count != 0) throw new IOException("KOTH did not clear live scores.");
                object saved;
                using (var reader = new StringReader(read("Scores.data"))) saved = serializer.Deserialize(reader)!;
                if (((IList)sessionScores.GetValue(saved, null)!).Count != 0)
                    throw new IOException("KOTH did not persist the cleared scores.");
            }
            catch (Exception failure)
            {
                // The mod swallows save exceptions. Restore live and on-disk scores before reporting failure.
                using (var reader = new StringReader(original))
                    planets.SetValue(null, sessionScores.GetValue(serializer.Deserialize(reader)!, null));
                try { write("Scores.data", original); }
                catch (Exception recovery)
                { throw new IOException("KOTH reset and saved-score recovery failed. Keep backup " + backup + " and restore it before restarting the world.", new AggregateException(failure, recovery)); }
                throw new IOException("KOTH reset failed; original scores restored. Backup: " + backup, failure);
            }
            // Invalidate stale confirmations and retries before accepting another reset.
            StreamId = Guid.NewGuid().ToString("N");
            events.Clear(); sequence = 0; eventCopy = null;
            // Read() republishes the cache, so polls see the new stream ID at once.
            return new ResetResult { BackupFile = backup, Snapshot = Read(new SnapshotRequest(), world) };
        }

        private static object Get(object value, string member) => value.GetType().GetProperty(member)?.GetValue(value, null)
            ?? throw new NotSupportedException("KOTH schema member is missing: " + member);
        private static string Text(object value, string member)
        {
            var property = value.GetType().GetProperty(member);
            if (property == null || property.PropertyType != typeof(string))
                throw new NotSupportedException("KOTH text member is incompatible: " + member);
            var text = property.GetValue(value, null) as string ?? "";
            return text.Length <= 256 ? text : text.Substring(0, 256);
        }
    }
}
