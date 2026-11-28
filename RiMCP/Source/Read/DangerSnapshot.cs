using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RiMCP.Read
{
    // One read of every danger on a map, from ColonyDangers. The digest shows its counts, and
    // get_colony_status and list_threats show its rows, so the three always agree.
    internal sealed class DangerSnapshot
    {
        private const int MaxExamples = 5;
        // The biggest mood penalties named for each pawn near a break.
        private const int MaxCauses = 3;

        private sealed class Harmed
        {
            public Pawn Pawn;
            public TemperatureHarm Harm;
        }

        private sealed class AtRisk
        {
            public Pawn Pawn;
            public DangerLevel Level;
        }

        public int Downed;
        public int UrgentlyHungry;
        public int Exposed;
        public bool LowFood;
        public FoodSummary Food;
        public ThreatReadService.ThreatCounts Threats;
        public List<WorkGap> Work = new List<WorkGap>();
        private readonly List<AtRisk> breakRisk = new List<AtRisk>();
        private readonly List<Harmed> harmed = new List<Harmed>();
        private Map map;

        public static DangerSnapshot Of(Map map, IEnumerable<Pawn> core, FoodSummary food)
        {
            var snapshot = new DangerSnapshot { map = map, Food = food };
            foreach (Pawn pawn in core)
            {
                snapshot.AddPawn(pawn);
            }
            snapshot.breakRisk.Sort((left, right) => right.Level.CompareTo(left.Level));
            snapshot.harmed.Sort((left, right) => right.Harm.Severity.CompareTo(left.Harm.Severity));
            if (map == null)
            {
                return snapshot;
            }
            snapshot.Threats = ThreatReadService.CountThreats(map);
            snapshot.LowFood = ColonyDangers.LowFood(map, food != null ? food.Stored.Total : FoodSample.Take(map).Total);
            snapshot.Work = map.IsPlayerHome ? WorkGaps.Uncovered(map, food) : snapshot.Work;
            return snapshot;
        }

        private void AddPawn(Pawn pawn)
        {
            Downed += ColonyDangers.Downed(pawn) ? 1 : 0;
            UrgentlyHungry += ColonyDangers.UrgentlyHungry(pawn) ? 1 : 0;
            DangerLevel risk = ColonyDangers.BreakRisk(pawn);
            if (risk != DangerLevel.None)
            {
                breakRisk.Add(new AtRisk { Pawn = pawn, Level = risk });
            }
            TemperatureHarm harm = ColonyDangers.TemperatureHarmOf(pawn);
            if (harm != null && harm.Level != DangerLevel.None)
            {
                harmed.Add(new Harmed { Pawn = pawn, Harm = harm });
            }
            Exposed += ColonyDangers.OutsideSafeTemperature(pawn) ? 1 : 0;
        }

        public int BreakRiskAt(DangerLevel level)
        {
            return breakRisk.Count(entry => entry.Level == level);
        }

        public DangerLevel BreakRiskOf(Pawn pawn)
        {
            AtRisk entry = breakRisk.FirstOrDefault(risk => risk.Pawn == pawn);
            return entry == null ? DangerLevel.None : entry.Level;
        }

        // Nonzero break-risk counts by level, or null when nobody is at risk.
        public object BreakRiskCounts()
        {
            if (breakRisk.Count == 0)
            {
                return null;
            }
            return Dto.Obj(
                Dto.Field("extreme", Nonzero(BreakRiskAt(DangerLevel.Extreme))),
                Dto.Field("major", Nonzero(BreakRiskAt(DangerLevel.Major))),
                Dto.Field("minor", Nonzero(BreakRiskAt(DangerLevel.Minor))));
        }

        // The compact temperature danger for the digest, or null when nobody is harmed or exposed.
        public object TemperatureSummary()
        {
            if (harmed.Count == 0 && Exposed == 0)
            {
                return null;
            }
            return Dto.Obj(
                Dto.Field("harmed", Nonzero(harmed.Count)),
                Dto.Field("worst", harmed.Count == 0 ? null : harmed[0].Harm.Describe()),
                Dto.Field("exposed", Nonzero(Exposed)));
        }

        public string[] WorkGapNames()
        {
            return Work.Count == 0 ? null : Work.Select(gap => gap.WorkType).ToArray();
        }

        // Danger rows, worst first. Each names what to read next.
        public List<Dictionary<string, object>> Rows()
        {
            var rows = new List<Dictionary<string, object>>();
            AddPawnRows(rows);
            AddThreatRows(rows);
            AddFoodRows(rows);
            AddWorkRow(rows);
            return Sorted(rows);
        }

        public static List<Dictionary<string, object>> Sorted(IEnumerable<Dictionary<string, object>> rows)
        {
            return rows.OrderByDescending(row => Rank((string)row["severity"])).ToList();
        }

        private static int Rank(string severity)
        {
            return severity == null ? (int)DangerLevel.None : (int)(DangerLevel)System.Enum.Parse(typeof(DangerLevel), severity, true);
        }

        private void AddPawnRows(List<Dictionary<string, object>> rows)
        {
            Add(rows, Downed, "medical", "downedCorePawns", DangerLevel.Major, "Downed core pawns need attention.", "list_pawns?filter=core&include=health");
            if (breakRisk.Count > 0)
            {
                rows.Add(Row("mood", "breakRisk", breakRisk[0].Level, "Core pawns are near a mental break by the game's own thresholds for each pawn.",
                    breakRisk.Count, "list_pawns?filter=core&include=needs",
                    breakRisk.Take(MaxExamples).Select(entry => PawnExample(entry.Pawn,
                        Dto.Field("breakRisk", ColonyDangers.LevelName(entry.Level)),
                        Dto.Field("causes", PawnDetails.TopPenalties(entry.Pawn, MaxCauses))))));
            }
            if (harmed.Count > 0)
            {
                rows.Add(Row("temperature", "temperatureHarm", harmed[0].Harm.Level, "Core pawns have heatstroke or hypothermia; move them somewhere temperate.",
                    harmed.Count, "get_environment",
                    harmed.Take(MaxExamples).Select(entry => PawnExample(entry.Pawn,
                        Dto.Field("hediff", entry.Harm.Kind), Dto.Field("stage", entry.Harm.Stage), Dto.Field("severity", System.Math.Round(entry.Harm.Severity, 2))))));
            }
            Add(rows, Exposed, "temperature", "unsafeTemperature", DangerLevel.Minor, "Core pawns stand somewhere hotter or colder than they can take, so heatstroke or hypothermia will build.", "get_environment");
            Add(rows, UrgentlyHungry, "resources", "hungryPawns", DangerLevel.Major, "Core pawns are urgently hungry or starving.", "list_pawns?filter=core&include=needs");
        }

        private void AddThreatRows(List<Dictionary<string, object>> rows)
        {
            if (Threats == null)
            {
                return;
            }
            Add(rows, Threats.Hostiles, "threat", "hostilesPresent", DangerLevel.Major, "Hostile pawns are on the map.", "list_threats");
            Add(rows, Threats.Manhunters + Threats.Predators, "threat", "animalThreats", DangerLevel.Major, "Manhunters, or predators hunting the colony's people or animals.", "list_threats");
            Add(rows, Threats.Fires, "fire", "fires", DangerLevel.Major, "Fires are burning.", "list_threats");
        }

        private void AddFoodRows(List<Dictionary<string, object>> rows)
        {
            if (LowFood)
            {
                rows.Add(Row("resources", "lowFood", DangerLevel.Major, "Stored human-edible nutrition is below the game's Low food threshold of 4 per colonist.",
                    (int)ResourceReadService.StoredHumanNutrition(map), "list_resources", null));
            }
            if (Food != null && Food.Wasting)
            {
                rows.Add(Row("resources", "foodRotting", DangerLevel.Minor, "More stored food will rot within a day than the colonists eat in a day; cook it, or keep it cold.",
                    (int)Food.RotsWithinDay, "list_resources", null));
            }
        }

        private void AddWorkRow(List<Dictionary<string, object>> rows)
        {
            if (Work.Count == 0)
            {
                return;
            }
            rows.Add(Row("work", "uncoveredWork", Work.Max(gap => gap.Level), "Vital work is needed and nobody can do it now: turn it on for someone up and free.",
                Work.Count, "list_work?sweep=true", Work.Select(gap => Dto.Obj(
                    Dto.Field("workType", gap.WorkType), Dto.Field("needed", gap.Needed), Dto.Field("need", gap.Need)))));
        }

        private static void Add(List<Dictionary<string, object>> rows, int count, string category, string code, DangerLevel level, string label, string nextRead)
        {
            if (count > 0)
            {
                rows.Add(Row(category, code, level, label, count, nextRead, null));
            }
        }

        public static Dictionary<string, object> Row(string category, string code, DangerLevel level, string label, int count, string nextRead, IEnumerable<object> examples)
        {
            return Dto.Obj(
                Dto.Field("category", category),
                Dto.Field("code", code),
                Dto.Field("severity", ColonyDangers.LevelName(level)),
                Dto.Field("label", label),
                Dto.Field("count", count),
                Dto.Field("nextRead", nextRead),
                Dto.Field("examples", examples?.ToArray()));
        }

        private static object PawnExample(Pawn pawn, params Dto.JsonField[] fields)
        {
            Dictionary<string, object> example = Dto.Obj(Dto.Field("id", ReadUtil.ThingId(pawn)), Dto.Field("name", ReadUtil.PawnName(pawn)));
            foreach (Dto.JsonField field in fields.Where(field => field.Value != null))
            {
                example[field.Name] = field.Value;
            }
            return example;
        }

        private static int? Nonzero(int count)
        {
            return count == 0 ? (int?)null : count;
        }
    }
}
