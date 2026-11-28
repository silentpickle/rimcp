using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using UnityEngine;
using Verse;

namespace RiMCP.Read
{
    internal static class GameReadService
    {
        private const int MaxMoodExamples = 5;
        private const int MaxMoodCauses = 3;
        // Kept longest when the reply is over budget: the dangers are what the dashboard is for.
        private static readonly string[] StatusPriorityFields = { "topRisks" };

        public static BridgeResponse GetGameContext(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            object maps = Find.Maps == null
                ? new object[0]
                : Find.Maps.Select(ReadUtil.MapSummary).ToArray();

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("programState", Current.ProgramState.ToString()),
                Dto.Field("world", SerializeWorldInfo()),
                Dto.Field("time", SerializeTime(context.Tick, context.Map)),
                Dto.Field("playMetrics", PlayTime.Describe()),
                Dto.Field("build", BuildIdentity.Describe()),
                Dto.Field("currentMap", ReadUtil.MapSummary(context.Map)),
                Dto.Field("maps", maps),
                Dto.Field("storyteller", SerializeStoryteller()),
                Dto.Field("mods", SerializeMods(context.Request.Include.Contains("mods")))));
        }

        public static BridgeResponse GetColonyStatus(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);

            List<PawnReadService.PawnRole> pawns = PawnReadService.PawnsForFilter(context.Map, "all").ToList();
            List<Pawn> core = pawns.Where(role => PawnReadService.IsCoreRole(role.Role)).Select(role => role.Pawn).ToList();
            List<ResourceReadService.ResourceGroup> resources = ResourceReadService.CollectResources(context.Map).ToList();
            FoodSummary food = context.Map.IsPlayerHome ? FoodSummary.Of(context.Map) : null;
            DangerSnapshot dangers = DangerSnapshot.Of(context.Map, core, food);
            List<Dictionary<string, object>> risks = BuildRisks(context.Map, dangers);

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("map", ReadUtil.MapSummary(context.Map)),
                Dto.Field("time", SerializeTime(context.Tick, context.Map)),
                Dto.Field("topRisks", risks),
                Dto.Field("pawns", CountPawns(pawns)),
                Dto.Field("resources", ResourceReadService.SummarizeResources(context.Map, resources)),
                Dto.Field("food", food == null ? null : food.ToDto(context.Map)),
                Dto.Field("medical", MedicalSummary(core)),
                Dto.Field("mood", MoodSummary(core, dangers)),
                Dto.Field("threats", dangers.Threats.ToDto()),
                Dto.Field("environment", EnvironmentReadService.SummarizeEnvironment(context.Map)),
                Dto.Field("power", PowerReadService.SummarizePower(context.Map)),
                Dto.Field("currentResearch", ResearchReadService.SerializeProject(ResearchReadService.CurrentProject(), ReadDetail.Summary)),
                Dto.Field("recentEvents", RecentEvents(8))), detailSection: "recentEvents", priorityFields: StatusPriorityFields);
        }

        public static object SerializeTime(int ticks, Map map)
        {
            TickManager tickManager = Current.Game == null ? null : Find.TickManager;
            if (Current.ProgramState != ProgramState.Playing || tickManager == null || Find.World == null || Find.World.info == null)
            {
                return null;
            }

            Vector2 longLat = map == null || Find.WorldGrid == null ? Vector2.zero : Find.WorldGrid.LongLatOf(map.Tile);
            int absTicks = GenDate.TickGameToAbs(ticks);
            int day = GenDate.DayOfSeason(absTicks, longLat.x);
            return Dto.Obj(
                Dto.Field("ticksGame", ticks),
                Dto.Field("paused", tickManager.Paused),
                Dto.Field("blockedBy", RiMCP.Command.BlockingWindows.Describe()),
                Dto.Field("timeSpeed", tickManager.CurTimeSpeed.ToString()),
                Dto.Field("dayOfSeason", day),
                Dto.Field("quadrum", GenDate.Quadrum(absTicks, longLat.x).ToString()),
                Dto.Field("hourOfDay", GenDate.HourOfDay(absTicks, longLat.x)),
                Dto.Field("season", GenDate.Season(absTicks, longLat).ToString()));
        }

        public static object RecentEvents(int limit)
        {
            List<object> alerts = new List<object>();
            try
            {
                List<Letter> letters = Find.LetterStack == null ? new List<Letter>() : Find.LetterStack.LettersListForReading;
                foreach (Letter letter in letters.Skip(Math.Max(0, letters.Count - limit)))
                {
                    alerts.Add(Dto.Obj(
                        Dto.Field("kind", "letter"),
                        Dto.Field("label", letter.Label.ToString()),
                        Dto.Field("text", LetterText(letter)),
                        Dto.Field("defName", letter.def == null ? null : letter.def.defName)));
                }
            }
            catch
            {
            }
            return alerts;
        }

        private static object SerializeWorldInfo()
        {
            WorldInfo worldInfo = Find.World == null ? null : Find.World.info;
            return Dto.Obj(
                Dto.Field("name", worldInfo == null ? null : worldInfo.name),
                Dto.Field("seedString", worldInfo == null ? null : worldInfo.seedString));
        }

        private static object SerializeStoryteller()
        {
            Storyteller storyteller = Find.Storyteller;
            Difficulty difficulty = storyteller == null ? null : storyteller.difficulty;
            DifficultyDef difficultyDef = storyteller == null ? null : storyteller.difficultyDef;
            return Dto.Obj(
                Dto.Field("defName", storyteller == null || storyteller.def == null ? null : storyteller.def.defName),
                Dto.Field("label", ReadUtil.DefLabel(storyteller == null ? null : storyteller.def)),
                Dto.Field("difficulty", Dto.Obj(
                    Dto.Field("defName", difficultyDef == null ? null : difficultyDef.defName),
                    Dto.Field("label", ReadUtil.DefLabel(difficultyDef)),
                    Dto.Field("threatScale", difficulty == null ? 0f : difficulty.threatScale))));
        }

        // The game and its expansions by name, and the other mods only counted unless asked for: a
        // heavily modded game's list is many kilobytes, and list_mods reads it in full.
        private static object SerializeMods(bool all)
        {
            List<ModContentPack> mods = new List<ModContentPack>();
            try
            {
                mods.AddRange(LoadedModManager.RunningModsListForReading);
            }
            catch
            {
            }
            Func<ModContentPack, object> summary = mod => Dto.Obj(Dto.Field("name", mod.Name), Dto.Field("packageId", mod.PackageId));
            List<ModContentPack> official = mods.Where(mod => mod.PackageId != null && mod.PackageId.StartsWith("ludeon.", StringComparison.OrdinalIgnoreCase)).ToList();
            return Dto.Obj(
                Dto.Field("count", mods.Count),
                Dto.Field("official", official.Select(mod => mod.Name).ToArray()),
                Dto.Field("others", mods.Count - official.Count),
                Dto.Field("loaded", all ? mods.Select(summary).ToArray() : null));
        }

        private static object CountPawns(IEnumerable<PawnReadService.PawnRole> pawns)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (PawnReadService.PawnRole pawn in pawns)
            {
                int count;
                counts.TryGetValue(pawn.Role, out count);
                counts[pawn.Role] = count + 1;
            }
            counts["core"] = counts.Where(pair => PawnReadService.IsCoreRole(pair.Key)).Sum(pair => pair.Value);
            return counts;
        }

        private static object MedicalSummary(IEnumerable<Pawn> core)
        {
            List<Pawn> downed = core.Where(pawn => pawn.Downed).ToList();
            List<Pawn> bleeding = core.Where(ColonyDangers.Bleeding).ToList();
            return Dto.Obj(
                Dto.Field("downed", downed.Count),
                Dto.Field("bleeding", bleeding.Count),
                Dto.Field("examples", downed.Concat(bleeding).Distinct().Take(5).Select(pawn => Dto.Obj(
                    Dto.Field("id", ReadUtil.ThingId(pawn)),
                    Dto.Field("name", ReadUtil.PawnName(pawn)),
                    Dto.Field("downed", pawn.Downed))).ToArray()));
        }

        private static object MoodSummary(IEnumerable<Pawn> core, DangerSnapshot dangers)
        {
            List<Pawn> moodPawns = core.Where(pawn => pawn.needs != null && pawn.needs.mood != null).ToList();
            return Dto.Obj(
                Dto.Field("average", moodPawns.Count == 0 ? 0f : moodPawns.Average(pawn => pawn.needs.mood.CurLevelPercentage)),
                Dto.Field("breakRisk", dangers.BreakRiskCounts()),
                Dto.Field("examples", ColonyDangers.ByBreakRisk(moodPawns).Take(MaxMoodExamples).Select(pawn => Dto.Obj(
                    Dto.Field("id", ReadUtil.ThingId(pawn)),
                    Dto.Field("name", ReadUtil.PawnName(pawn)),
                    Dto.Field("mood", pawn.needs.mood.CurLevelPercentage),
                    Dto.Field("breakRisk", ColonyDangers.LevelName(dangers.BreakRiskOf(pawn))),
                    Dto.Field("causes", PawnDetails.TopPenalties(pawn, MaxMoodCauses)))).ToArray()));
        }

        // The colony's dangers, then blocked bills, worst first.
        private static List<Dictionary<string, object>> BuildRisks(Map map, DangerSnapshot dangers)
        {
            List<Dictionary<string, object>> risks = dangers.Rows();
            // Only whether each bill is blocked: one shared scan, and counting stops at enough.
            BillInputs inputs = map == null ? null : new BillInputs(map);
            int blockedBills = map == null ? 0 : ProductionReadService.BillRecords(map)
                .Count(record => BillStatus.Of(record.Bill, record.Workbench, record.Giver, inputs, false).Blocking.Count > 0);
            if (blockedBills > 0)
            {
                risks.Add(DangerSnapshot.Row("production", "blockedBills", DangerLevel.Minor, "Active bills cannot be done: missing ingredients, workers, fuel, or power.", blockedBills, "list_production", null));
            }
            foreach (Dictionary<string, object> risk in risks)
            {
                risk.Remove("examples");
            }
            return DangerSnapshot.Sorted(risks);
        }

        private static string LetterText(Letter letter)
        {
            StandardLetter standard = letter as StandardLetter;
            if (standard != null)
            {
                return standard.Text.ToString();
            }
            ChoiceLetter choice = letter as ChoiceLetter;
            if (choice != null)
            {
                return choice.Text.ToString();
            }
            return null;
        }
    }
}
