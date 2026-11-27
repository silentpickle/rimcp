using System.Collections;
using System.Linq;
using Verse;

internal static partial class Program
{
    private const int WatchStart = 100;
    private const int WatchStep = 10;
    private const int WatchFirstFrame = WatchStart + WatchStep;
    private const int WatchSecondFrame = WatchFirstFrame + WatchStep;
    private const int EarlyId = 3;
    private const int LaterId = 7;
    private const int ExistingLetterId = 9;
    private const int AdvanceEventLimit = 50;

    private static void CheckAdvanceWatcherPriority()
    {
        object game = new object();
        object before = Observation(game, WatchStart, TimeSpeed.Superfast, null,
            new[] { Colonist("dead"), Colonist("downed"), Colonist("hurt"), Colonist("mental"),
                New("ColonistState", "Id", "alreadyDead", "Dead", true) },
            new[] { Letter(ExistingLetterId, "Old", false) });
        MovingCaravan(before, LaterId, "Later caravan");
        MovingCaravan(before, EarlyId, "Early caravan");

        object current = LowFood(Observation(game, WatchFirstFrame, TimeSpeed.Paused, "Hostile group",
            new[] {
                New("ColonistState", "Id", "dead", "Name", "Dead pawn", "Dead", true, "Downed", true, "Injuries", 1),
                Colonist("downed", downed: true),
                Colonist("hurt", bleeding: true),
                New("ColonistState", "Id", "mental", "Name", "Mental pawn", "MentalState", "Daze"),
                Colonist("alreadyDead", downed: true),
                Colonist("new", downed: true, injuries: 1)
            },
            new[] { Letter(LaterId, "Threat", true), Letter(EarlyId, "Normal", false),
                Letter(ExistingLetterId, "Still old", true) }), "Low pantry");
        string[] expected =
        {
            "death|dead|Dead pawn|died",
            "downed|downed|downed|downed",
            "injured|hurt|hurt|bleeding",
            "mentalBreak|mental|Mental pawn|Daze",
            "hostiles|||Hostile group",
            "lowFood|||Low pantry",
            "threatLetter|||Threat",
            "caravanRouteEnded|||Early caravan (world object " + EarlyId + ")",
            "caravanRouteEnded|||Later caravan (world object " + LaterId + ")",
            "letter|||Normal"
        };

        foreach (string kind in expected.Select(entry => entry.Split('|')[0]).Distinct())
        {
            object run = AdvanceRun(game, before, new[] { kind });
            Step(run, current);
            string stop = kind == "letter" ? "threatLetter" : kind;
            Check(Field<string>(run, "Status") == "stopped" && Field<string>(run, "EndReason") == stop,
                kind + " must stop before a speed change, with letter also matching threatLetter");
            object[] changes = Field<IEnumerable>(run, "Events").Cast<object>().ToArray();
            Check(changes.All(change => Field<int>(change, "Tick") == WatchFirstFrame),
                "every new event must use the observation tick");
            Check(changes.Select(WatchEvent).SequenceEqual(expected),
                "simultaneous watchers must preserve event fields, priority, and stable order");
        }
    }

    private static string WatchEvent(object change)
    {
        return string.Join("|", Field<string>(change, "Kind"), Field<string>(change, "PawnId"),
            Field<string>(change, "PawnName"), Field<string>(change, "Detail"));
    }

    private static void MovingCaravan(object observation, int id, string label)
    {
        Field<IDictionary>(observation, "MovingCaravans")[id] = label;
    }

    private static void CheckAdvanceWatcherLimit()
    {
        object game = new object();
        object start = Observation(game, WatchStart, TimeSpeed.Superfast, colonists: new[] { Colonist("injury", injuries: 1) });
        object run = AdvanceRun(game, start, new[] { "hostiles" });
        object[] letters = Enumerable.Range(1, AdvanceEventLimit - 1).Select(id => Letter(id, "Letter " + id, false)).ToArray();
        object first = Observation(game, WatchFirstFrame, TimeSpeed.Superfast,
            colonists: new[] { Colonist("injury", injuries: EarlyId) }, letters: letters);
        Step(run, first);
        var events = Field<IList>(run, "Events");
        Check(events.Count == AdvanceEventLimit && Field<int>(run, "DroppedEvents") == 0, "record the last available slot");
        Check(WatchEvent(events[0]) == "injured|injury|injury|new injury", "count a new injury without bleeding");

        object second = Observation(game, WatchSecondFrame, TimeSpeed.Superfast, "New threat",
            new[] { Colonist("injury", injuries: EarlyId) }, letters);
        Step(run, second);
        Check(events.Count == AdvanceEventLimit && Field<int>(run, "DroppedEvents") == 1, "do not record repeated events, count a new event over the limit");
        Check(Field<string>(run, "EndReason") == "hostiles" && WatchEvent(Field<object>(run, "StoppedBy")) == "hostiles|||New threat",
            "an event dropped from history still controls the stop");
    }
}
