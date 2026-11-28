using System;
using System.Globalization;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Command;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Read
{
    internal static class DigestReadService
    {
        private const int DigestBytes = 2048;
        // Lowest to highest retention priority, after ordinary digest fields.
        internal static readonly string[] PriorityFields = { "advance", "blockingDetails", "blockedBy", "paused" };

        public static BridgeResponse GetTurnDigest(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            int maxBytes = Math.Min(DigestBytes, ReadReplyBudget.MaxBytes(context.Request));
            context.Request.Query[ReplyLimits.Argument] = maxBytes.ToString(CultureInfo.InvariantCulture);
            return ReadEnvelope.Ok(context, Describe(context.Map, AdvanceRunner.LatestInLoadedGame), detailSection: "recentLetters", priorityFields: PriorityFields);
        }

        internal static ReadRequest Budget()
        {
            return ReadRequest.FromUri(new Uri("http://localhost/?maxBytes=" + DigestBytes.ToString(CultureInfo.InvariantCulture)));
        }

        internal static object Describe(Map map, AdvanceRun run)
        {
            Pawn[] core = map == null ? Array.Empty<Pawn>() : PawnReadService.PawnsForFilter(map, "core").Select(role => role.Pawn).ToArray();
            FoodSummary food = map?.IsPlayerHome == true ? FoodSummary.Of(map) : null;
            IntVec3[] colonists = map == null ? Array.Empty<IntVec3>() : SurveyMovers.VisibleColonists(map);
            IntVec3 origin = colonists.Length == 0 ? map?.Center ?? IntVec3.Invalid : SurveyGeometry.Centre(colonists);
            var movers = map == null ? null : new SurveyMovers(map, origin, colonists);
            TickManager clock = Find.TickManager;
            var windows = BlockingWindows.Now();
            return Dto.Obj(
                Dto.Field("paused", clock.Paused),
                Dto.Field("speed", clock.CurTimeSpeed.ToString()),
                Dto.Field("blockedBy", windows.Take(3).Select(window => Bounded(window.GetType().Name)).ToArray()),
                Dto.Field("blockingDetails", BlockingWindows.Describe(windows)),
                Dto.Field("risks", map == null ? null : Dto.Obj(
                    Dto.Field("downed", core.Count(pawn => pawn.Downed)),
                    Dto.Field("urgentlyHungry", core.Count(pawn =>
                        pawn.needs?.food != null && pawn.needs.food.CurCategory >= HungerCategory.UrgentlyHungry)),
                    Dto.Field("lowMood", core.Count(pawn =>
                        pawn.needs?.mood != null && pawn.needs.mood.CurLevelPercentage < 0.35f)),
                    Dto.Field("threats", ThreatReadService.SummarizeThreats(map)))),
                Dto.Field("food", food == null ? null : Dto.Obj(
                    Dto.Field("low", map.mapPawns.AnyColonistSpawned &&
                        food.Stored.Total < FoodSummary.LowPerColonist * map.mapPawns.FreeColonistsSpawnedCount),
                    Dto.Field("stored", Math.Round(food.Stored.Total, 1)),
                    Dto.Field("looseAllowed", Math.Round(food.LooseAllowed.Total, 1)),
                    Dto.Field("looseForbidden", Math.Round(food.LooseForbidden.Total, 1)),
                    Dto.Field("daysLeft", food.EatenPerDay > 0f ? (double?)Math.Round(food.Stored.Total / food.EatenPerDay, 1) : null))),
                Dto.Field("movers", movers?.DescribeDigest(3)),
                Dto.Field("recentLetters", Find.LetterStack?.LettersListForReading.AsEnumerable().Reverse().Take(2).Select(letter => Dto.Obj(
                    Dto.Field("id", letter.GetUniqueLoadID()),
                    Dto.Field("label", Bounded(letter.Label.ToString())),
                    Dto.Field("def", letter.def?.defName))).ToArray()),
                Dto.Field("advance", DescribeAdvance(run)));
        }

        private static object DescribeAdvance(AdvanceRun run)
        {
            if (run == null)
            {
                return null;
            }

            return Dto.Obj(
                Dto.Field("id", run.Id),
                Dto.Field("status", run.Status),
                Dto.Field("endReason", run.EndReason),
                Dto.Field("startTick", run.StartTick),
                Dto.Field("targetTick", run.TargetTick),
                Dto.Field("endTick", run.EndTick < 0 ? (int?)null : run.EndTick),
                Dto.Field("droppedEvents", run.DroppedEvents > 0 ? (int?)run.DroppedEvents : null),
                Dto.Field("summary", run.Summary.Describe(researchLimit: 2)),
                Dto.Field("recentEvents", run.Events.Skip(Math.Max(0, run.Events.Count - 3)).Select(change => Dto.Obj(
                    Dto.Field("kind", change.Kind),
                    Dto.Field("tick", change.Tick),
                    Dto.Field("pawnId", change.PawnId),
                    Dto.Field("detail", Bounded(change.Detail)))).ToArray()));
        }

        private static string Bounded(string text)
        {
            string plain = text?.StripTags();
            const int limit = 70;
            if (plain == null || plain.Length <= limit)
            {
                return plain;
            }
            int length = char.IsHighSurrogate(plain[limit - 1]) ? limit - 1 : limit;
            return plain.Substring(0, length) + "…";
        }
    }
}
