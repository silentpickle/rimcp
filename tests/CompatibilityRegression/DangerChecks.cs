using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;

// The danger model: mental break levels, heatstroke and hypothermia stages, the stop conditions
// they raise during a time advance, and the worst-first order of danger rows.
internal static partial class Program
{
    private const float BelowMinorHarm = 0.19f;
    private const float MinorHarm = 0.2f;
    private const float BelowSeriousHarm = 0.34f;
    private const float SeriousHarm = 0.35f;
    private const float BelowExtremeHarm = 0.61f;
    private const float ExtremeHarm = 0.62f;
    private const int DangerStart = 100;
    private const int DangerFrame = DangerStart + 10;

    private static void RegisterDangerChecks()
    {
        Test("T1_break_levels", CheckBreakLevels);
        Test("T2_harm_stages", CheckHarmStages);
        Test("T3_danger_stops", CheckDangerStops);
        Test("T4_danger_order", CheckDangerOrder);
    }

    private static Type DangerLevelType()
    {
        return mod.GetType("RiMCP.Read.DangerLevel", true);
    }

    private static object Level(string name)
    {
        return Enum.Parse(DangerLevelType(), name);
    }

    private static string LevelOf(string method, params object[] arguments)
    {
        return mod.GetType("RiMCP.Read.ColonyDangers", true).GetMethod(method, Static).Invoke(null, arguments).ToString();
    }

    private static void CheckBreakLevels()
    {
        Check(LevelOf("BreakLevel", false, false, false) == "None", "no imminent break is no risk");
        Check(LevelOf("BreakLevel", false, false, true) == "Minor", "a minor break alone is minor");
        Check(LevelOf("BreakLevel", false, true, true) == "Major", "a major break outranks a minor one");
        Check(LevelOf("BreakLevel", true, true, true) == "Extreme", "an extreme break outranks the others");
        Check(LevelOf("BreakLevel", true, false, false) == "Extreme", "an extreme break counts on its own");
    }

    private static void CheckHarmStages()
    {
        var expected = new[]
        {
            new { severity = BelowMinorHarm, level = "None" },
            new { severity = MinorHarm, level = "Minor" },
            new { severity = BelowSeriousHarm, level = "Minor" },
            new { severity = SeriousHarm, level = "Major" },
            new { severity = BelowExtremeHarm, level = "Major" },
            new { severity = ExtremeHarm, level = "Extreme" }
        };
        foreach (var stage in expected)
        {
            string level = LevelOf("HarmLevel", stage.severity);
            Check(level == stage.level, "severity " + stage.severity + " must be " + stage.level + ", was " + level);
        }
    }

    private static object DangerColonist(string id, string breakRisk, string temperature)
    {
        return New("ColonistState", "Id", id, "Name", id, "BreakRisk", Level(breakRisk), "Temperature", Level(temperature), "TemperatureDetail", "Heatstroke " + temperature);
    }

    private static void CheckDangerStops()
    {
        object game = new object();
        object before = Observation(game, DangerStart, TimeSpeed.Superfast, colonists: new[]
        {
            DangerColonist("rising", "Major", "Minor"),
            DangerColonist("already", "Extreme", "Major"),
            DangerColonist("easing", "Minor", "Minor")
        });
        object now = Observation(game, DangerFrame, TimeSpeed.Superfast, colonists: new[]
        {
            DangerColonist("rising", "Extreme", "Major"),
            DangerColonist("already", "Extreme", "Extreme"),
            DangerColonist("easing", "Major", "Minor")
        });
        object run = AdvanceRun(game, before);
        Step(run, now);
        string[] events = Field<IEnumerable>(run, "Events").Cast<object>().Select(WatchEvent).ToArray();
        string[] expected = { "breakRisk|rising|rising|extreme break risk", "temperature|rising|rising|Heatstroke Major" };
        Check(events.SequenceEqual(expected), "only dangers that rise to their level stop a run: " + string.Join(", ", events));
        Check(Field<string>(run, "EndReason") == "breakRisk", "an extreme break risk stops a default run before heatstroke");
    }

    private static void CheckDangerOrder()
    {
        Type snapshot = mod.GetType("RiMCP.Read.DangerSnapshot", true);
        MethodInfo row = snapshot.GetMethod("Row", Static);
        Func<string, string, object> make = (code, level) => row.Invoke(null, new object[] { "test", code, Level(level), code, 1, "list_threats", null });
        IList rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(typeof(Dictionary<string, object>)));
        foreach (object entry in new[] { make("minorFirst", "Minor"), make("majorA", "Major"), make("extreme", "Extreme"), make("majorB", "Major") })
        {
            rows.Add(entry);
        }
        IEnumerable sorted = (IEnumerable)snapshot.GetMethod("Sorted", Static).Invoke(null, new object[] { rows });
        string order = string.Join(" ", sorted.Cast<Dictionary<string, object>>().Select(entry => (string)entry["code"]));
        Check(order == "extreme majorA majorB minorFirst", "dangers sort worst first and keep their order within a level: " + order);
        Dictionary<string, object> first = sorted.Cast<Dictionary<string, object>>().First();
        Check((string)first["severity"] == "extreme" && !first.ContainsKey("examples"), "a row names its severity and leaves out empty examples");
    }
}
