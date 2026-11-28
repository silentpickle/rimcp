using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RiMCP.Read
{
    // How bad a danger is. A temperature stage the game calls serious is Major.
    internal enum DangerLevel { None, Minor, Major, Extreme }

    // Heatstroke or hypothermia: which one, how far along, and the game's stage label.
    internal sealed class TemperatureHarm
    {
        public string Kind;
        public float Severity;
        public string Stage;
        public DangerLevel Level;

        public string Describe()
        {
            return Kind + " " + Stage;
        }
    }

    // Every danger to the colony is defined here once. The digest, colony status, threat list and
    // the stop conditions on time advances all ask these, so they cannot disagree about who is in
    // danger.
    internal static class ColonyDangers
    {
        // The minimum severities of the minor, serious and extreme stages of both the Heatstroke
        // and the Hypothermia hediff defs.
        public const float HarmMinorSeverity = 0.2f;
        public const float HarmSeriousSeverity = 0.35f;
        public const float HarmExtremeSeverity = 0.62f;

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

        // The game's own mental break risk: the break its MentalBreaker counts as imminent, which
        // uses the pawn's own thresholds (traits, ideology and so on change them).
        public static DangerLevel BreakRisk(Pawn pawn)
        {
            MentalBreaker breaker = pawn.mindState?.mentalBreaker;
            if (breaker == null || pawn.needs?.mood == null)
            {
                return DangerLevel.None;
            }
            return BreakLevel(breaker.BreakExtremeIsImminent, breaker.BreakMajorIsImminent, breaker.BreakMinorIsImminent);
        }

        internal static DangerLevel BreakLevel(bool extreme, bool major, bool minor)
        {
            if (extreme)
            {
                return DangerLevel.Extreme;
            }
            if (major)
            {
                return DangerLevel.Major;
            }
            return minor ? DangerLevel.Minor : DangerLevel.None;
        }

        // The worse of the pawn's heatstroke and hypothermia, or null when it has neither visibly.
        public static TemperatureHarm TemperatureHarmOf(Pawn pawn)
        {
            if (pawn.health?.hediffSet == null)
            {
                return null;
            }
            TemperatureHarm heat = Harm(pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Heatstroke));
            TemperatureHarm cold = Harm(pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Hypothermia));
            if (heat == null || (cold != null && cold.Severity > heat.Severity))
            {
                return cold;
            }
            return heat;
        }

        private static TemperatureHarm Harm(Hediff hediff)
        {
            if (hediff == null || !hediff.Visible)
            {
                return null;
            }
            return new TemperatureHarm
            {
                Kind = hediff.def.defName,
                Severity = hediff.Severity,
                Stage = hediff.CurStage?.label,
                Level = HarmLevel(hediff.Severity)
            };
        }

        internal static DangerLevel HarmLevel(float severity)
        {
            if (severity >= HarmExtremeSeverity)
            {
                return DangerLevel.Extreme;
            }
            if (severity >= HarmSeriousSeverity)
            {
                return DangerLevel.Major;
            }
            return severity >= HarmMinorSeverity ? DangerLevel.Minor : DangerLevel.None;
        }

        // Where the pawn stands is hotter or colder than its clothes and body can take, so
        // heatstroke or hypothermia will build.
        public static bool OutsideSafeTemperature(Pawn pawn)
        {
            return pawn.Spawned && !pawn.SafeTemperatureRange().Includes(pawn.AmbientTemperature);
        }

        // In bed to recover, as the game sends a pawn to medical rest, rather than asleep.
        public static bool IsPatient(Pawn pawn)
        {
            return pawn.health != null && pawn.Spawned && pawn.InBed() && HealthAIUtility.ShouldSeekMedicalRest(pawn);
        }

        // Alert_LowFood's test on a home map, given its stored food, without the alert's grace
        // period at the start of a game.
        public static bool LowFood(Map map, float storedNutrition)
        {
            return map.IsPlayerHome && map.mapPawns.AnyColonistSpawned
                && storedNutrition < FoodSummary.LowPerColonist * map.mapPawns.FreeColonistsSpawnedCount;
        }

        // A colonist (or mech) who could take this work as the game's menus check it: not downed
        // and with the work type on.
        public static bool WorkActive(Pawn pawn, WorkTypeDef work)
        {
            return !pawn.Downed && pawn.workSettings != null && pawn.workSettings.WorkIsActive(work);
        }

        // Would actually take this work now: also not drafted, not in a mental state, and not in
        // bed rest.
        public static bool CanDoWorkNow(Pawn pawn, WorkTypeDef work)
        {
            return WorkActive(pawn, work) && !pawn.Drafted && !pawn.InMentalState && !IsPatient(pawn);
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

        public static string LevelName(DangerLevel level)
        {
            return level == DangerLevel.None ? null : level.ToString().ToLowerInvariant();
        }

        public static IEnumerable<Pawn> ByBreakRisk(IEnumerable<Pawn> core)
        {
            return core.Select(pawn => new { pawn, level = BreakRisk(pawn) })
                .Where(entry => entry.level != DangerLevel.None)
                .OrderByDescending(entry => entry.level)
                .Select(entry => entry.pawn);
        }
    }
}
