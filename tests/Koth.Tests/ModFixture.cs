using System.Runtime.CompilerServices;
using System.Xml.Serialization;

namespace KingOfTheHill.Descriptions
{
    // Runtime shape of Workshop 2388326362; deliberately no game DLL dependency.
    public sealed class Session
    {
        public List<PlanetDescription> PlanetScores { get; set; } = [];
    }
    public sealed class PlanetDescription
    {
        public string Name { get; set; } = "Earth";
        public List<ScoreDescription> Scores { get; set; } = [];
    }
    public sealed class ScoreDescription
    {
        public long FactionId { get; set; } = 42;
        public string FactionName { get; set; } = "Faction";
        public string FactionTag { get; set; } = "FAC";
        public int Points { get; set; } = 12;
        public string GridName { get; set; } = "Arena";
    }
}
namespace KingOfTheHill
{
    public sealed class Core
    {
        private static List<Descriptions.PlanetDescription> Planets = [];
        internal static Action<string, string> Write = (_, _) => { };
        internal static bool FailSave;
        internal static int Resets;
        internal static void Seed()
        {
            Planets = [new() { Scores = [new(), new() { FactionId = 84, Points = 24, FactionTag = "TOP" }] }];
            FailSave = false; Resets = 0; NexusAPIns.NexusAPI.Running = false;
        }
        // Any mod read now throws, so an answer proves it came from the cached copy.
        internal static void Break() => Planets = null!;
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PluginHandleIncomingPacket(ushort comId, byte[] msg, ulong id, bool reliable)
        {
            if (System.Text.Encoding.ASCII.GetString(msg) != "clear") return;
            Planets.Clear(); Resets++;
            if (FailSave) return; // The supplied mod catches and logs persistence failures.
            using var writer = new StringWriter();
            new XmlSerializer(typeof(Descriptions.Session)).Serialize(writer, new Descriptions.Session { PlanetScores = Planets });
            Write("Scores.data", writer.ToString());
        }
    }
}
namespace NexusAPIns
{
    public static class NexusAPI
    {
        internal static bool Running;
        public static bool IsRunningNexus() => Running;
    }
}
