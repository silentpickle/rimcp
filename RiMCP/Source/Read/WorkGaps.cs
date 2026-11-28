using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // Vital work that is needed now and that no colonist or mech can take now (ColonyDangers.
    // CanDoWorkNow). Each need is checked only while it exists, so a quiet colony reports nothing.
    internal sealed class WorkGap
    {
        public string WorkType;
        public int Needed;
        public string Need;
        public DangerLevel Level;
    }

    internal static class WorkGaps
    {
        // A simple meal's nutrition, so stored meal nutrition can be read as a count of meals.
        private const float MealNutrition = 0.9f;

        private sealed class WorkNeed
        {
            public string WorkType;
            public Func<Map, FoodSummary, int> Count;
            public string Need;
            public DangerLevel Level;
        }

        private static readonly WorkNeed[] Needs =
        {
            new WorkNeed { WorkType = "Firefighter", Count = HomeFires, Need = "fires in the home area", Level = DangerLevel.Major },
            new WorkNeed { WorkType = "Doctor", Count = NeedTending, Need = "people or animals need tending", Level = DangerLevel.Major },
            new WorkNeed { WorkType = "Cooking", Count = RawToCook, Need = "fewer meals than colonists, with raw food stored", Level = DangerLevel.Minor },
            new WorkNeed { WorkType = "Growing", Count = RipeCrops, Need = "ripe crops in growing zones", Level = DangerLevel.Minor }
        };

        public static List<WorkGap> Uncovered(Map map, FoodSummary food)
        {
            List<Pawn> workers = map.mapPawns.FreeColonistsSpawned.Concat(map.mapPawns.SpawnedColonyMechs).ToList();
            var gaps = new List<WorkGap>();
            foreach (WorkNeed need in Needs)
            {
                WorkTypeDef work = DefDatabase<WorkTypeDef>.GetNamedSilentFail(need.WorkType);
                int needed = work == null ? 0 : need.Count(map, food);
                if (needed == 0 || workers.Any(pawn => ColonyDangers.CanDoWorkNow(pawn, work)))
                {
                    continue;
                }
                gaps.Add(new WorkGap { WorkType = need.WorkType, Needed = needed, Need = need.Need, Level = need.Level });
            }
            return gaps;
        }

        private static int HomeFires(Map map, FoodSummary food)
        {
            return ThreatReadService.Fires(map).Count(fire => map.areaManager.Home[fire.Position]);
        }

        private static int NeedTending(Map map, FoodSummary food)
        {
            return map.mapPawns.AllPawnsSpawned.Count(pawn =>
                (pawn.Faction == Faction.OfPlayer || pawn.IsPrisonerOfColony) && HealthAIUtility.ShouldBeTendedNowByPlayer(pawn));
        }

        private static int RawToCook(Map map, FoodSummary food)
        {
            if (food == null || food.Stored.Raw <= 0f)
            {
                return 0;
            }
            return food.Stored.Meals < MealNutrition * map.mapPawns.FreeColonistsSpawnedCount ? 1 : 0;
        }

        private static int RipeCrops(Map map, FoodSummary food)
        {
            return map.zoneManager.AllZones.OfType<Zone_Growing>()
                .SelectMany(zone => zone.Cells)
                .Count(cell => cell.GetPlant(map)?.HarvestableNow == true);
        }
    }
}
