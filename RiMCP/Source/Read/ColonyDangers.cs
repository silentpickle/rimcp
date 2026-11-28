using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // Every danger to the colony is defined here once. The digest, colony status, threat list and
    // the stop conditions on time advances all ask these, so they cannot disagree about who is in
    // danger.
    internal static class ColonyDangers
    {
        // Below this mood a pawn counts as low in mood.
        public const float LowMoodLevel = 0.35f;

        public static bool Downed(Pawn pawn)
        {
            return pawn.Downed;
        }

        // Losing blood now, as the game's blood loss counts it.
        public static bool Bleeding(Pawn pawn)
        {
            return pawn.health?.hediffSet != null && pawn.health.hediffSet.BleedRateTotal > 0f;
        }

        public static bool UrgentlyHungry(Pawn pawn)
        {
            return pawn.needs?.food != null && pawn.needs.food.CurCategory >= HungerCategory.UrgentlyHungry;
        }

        public static bool LowMood(Pawn pawn)
        {
            return pawn.needs?.mood != null && pawn.needs.mood.CurLevelPercentage < LowMoodLevel;
        }

        // Alert_LowFood's test on a home map, given its stored food, without the alert's grace
        // period at the start of a game.
        public static bool LowFood(Map map, float storedNutrition)
        {
            return map.IsPlayerHome && map.mapPawns.AnyColonistSpawned
                && storedNutrition < FoodSummary.LowPerColonist * map.mapPawns.FreeColonistsSpawnedCount;
        }

        // A colonist (or mech) who could take this work now as the game's menus check it: not
        // downed and with the work type on.
        public static bool WorkActive(Pawn pawn, WorkTypeDef work)
        {
            return !pawn.Downed && pawn.workSettings != null && pawn.workSettings.WorkIsActive(work);
        }

        public static bool IsThreatPawn(Pawn pawn)
        {
            return pawn != null && (pawn.HostileTo(Faction.OfPlayer) || IsManhunter(pawn) || IsPredator(pawn));
        }

        public static bool IsManhunter(Pawn pawn)
        {
            return pawn != null && pawn.MentalStateDef != null && pawn.MentalStateDef.defName.IndexOf("Manhunter", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // A predator only threatens the colony while it hunts one of its people or animals.
        public static bool IsPredator(Pawn pawn)
        {
            if (pawn == null || pawn.RaceProps == null || !pawn.RaceProps.predator || pawn.Faction == Faction.OfPlayer || pawn.CurJobDef != JobDefOf.PredatorHunt)
            {
                return false;
            }
            Pawn prey = pawn.CurJob.targetA.Thing as Pawn;
            return prey != null && prey.Faction == Faction.OfPlayer;
        }

        // How many of the colony's core pawns are in each pawn danger.
        internal sealed class PawnCounts
        {
            public int Downed;
            public int UrgentlyHungry;
            public int LowMood;

            public static PawnCounts Of(IEnumerable<Pawn> core)
            {
                var counts = new PawnCounts();
                foreach (Pawn pawn in core)
                {
                    counts.Downed += Downed(pawn) ? 1 : 0;
                    counts.UrgentlyHungry += UrgentlyHungry(pawn) ? 1 : 0;
                    counts.LowMood += LowMood(pawn) ? 1 : 0;
                }
                return counts;
            }
        }

        public static IEnumerable<Pawn> LowMoodPawns(IEnumerable<Pawn> core)
        {
            return core.Where(LowMood).OrderBy(pawn => pawn.needs.mood.CurLevelPercentage);
        }
    }
}
