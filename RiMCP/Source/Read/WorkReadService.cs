using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class WorkReadService
    {
        public static BridgeResponse ListWork(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);

            var pawns = PawnReadService.PawnsForFilter(context.Map, "core")
                .OrderBy(role => role.Pawn.LabelShortCap);
            Page<PawnReadService.PawnRole> page = new Page<PawnReadService.PawnRole>(pawns, context.Request);

            object suggestions = context.Request.Wants("suggestions") ? Suggestions(context.Map) : null;
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("manualPriorities", Find.PlaySettings == null ? false : Find.PlaySettings.useWorkPriorities),
                Dto.Field("suggestedEntries", suggestions),
                Dto.Field("workTypes", DefDatabase<WorkTypeDef>.AllDefsListForReading
                    .OrderBy(def => def.naturalPriority)
                    .Select(def => Dto.Obj(
                        Dto.Field("defName", def.defName),
                        Dto.Field("label", ReadUtil.DefLabel(def)),
                        Dto.Field("naturalPriority", def.naturalPriority)))
                    .ToArray()),
                Dto.Field("pawns", page.Items.Select(role => SerializeWorkPawn(role.Pawn, role.Role, context.Request)).ToArray())),
                page.Truncated,
                page.NextCursor, "pawns", revisionScope: "work", rowIds: page.Items.Select(role => ReadUtil.ThingId(role.Pawn)));
        }

        private static object Suggestions(Map map)
        {
            Pawn[] workers = map.mapPawns.FreeColonistsSpawned.Where(pawn =>
                !pawn.Downed && !pawn.Drafted && !pawn.InMentalState && pawn.skills != null && pawn.workSettings?.EverWork == true).ToArray();
            return DefDatabase<WorkTypeDef>.AllDefsListForReading
                .Where(work => work.relevantSkills.Count > 0 && !workers.Any(pawn =>
                    !pawn.WorkTypeIsDisabled(work) && IsPriorityCoverage(pawn.workSettings.GetPriority(work))))
                .OrderByDescending(work => work.naturalPriority).ThenBy(work => work.defName)
                .Select(work => workers.Where(pawn => !pawn.WorkTypeIsDisabled(work))
                    .OrderByDescending(pawn => work.relevantSkills.Average(skill => pawn.skills.GetSkill(skill).Level))
                    .ThenByDescending(pawn => work.relevantSkills.Sum(skill => (int)pawn.skills.GetSkill(skill).passion))
                    .ThenBy(pawn => pawn.thingIDNumber)
                    .Take(1)
                    .Select(pawn => Dto.Obj(Dto.Field("pawnId", ReadUtil.ThingId(pawn)),
                        Dto.Field("workType", work.defName), Dto.Field("priority", 2)))
                    .FirstOrDefault())
                .Where(entry => entry != null).Take(100).ToArray();
        }

        private static bool IsPriorityCoverage(int priority) => priority > 0 && priority <= 2;

        private static object SerializeWorkPawn(Pawn pawn, string role, ReadRequest request)
        {
            return Dto.Obj(
                Dto.Field("id", ReadUtil.ThingId(pawn)),
                Dto.Field("name", ReadUtil.PawnName(pawn)),
                Dto.Field("role", role),
                Dto.Field("drafted", pawn.Drafted),
                Dto.Field("downed", pawn.Downed),
                Dto.Field("currentJob", PawnReadService.SerializeCurrentJob(pawn)),
                Dto.Field("work", request.Detail == ReadDetail.Summary ? CompactWork(pawn) : PawnReadService.SerializePawnWork(pawn)),
                Dto.Field("schedule", request.Detail == ReadDetail.Summary && !request.Wants("schedule") ? null : PawnReadService.SerializeSchedule(pawn)));
        }

        // Every active work type, highest priority first. Leaving any out would hide work a pawn
        // will do, such as the only builder's Construction.
        private static object CompactWork(Pawn pawn)
        {
            // Prisoners are core pawns but usually have no initialized work settings; see SerializePawnWork.
            if (pawn.workSettings == null || !pawn.workSettings.EverWork)
            {
                return new object[0];
            }
            return DefDatabase<WorkTypeDef>.AllDefsListForReading
                .OrderBy(def => pawn.workSettings.GetPriority(def) == 0 ? 99 : pawn.workSettings.GetPriority(def))
                .ThenBy(def => def.naturalPriority)
                .Where(def =>
                {
                    try
                    {
                        return !pawn.WorkTypeIsDisabled(def) && pawn.workSettings.WorkIsActive(def);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Select(def => Dto.Obj(
                    Dto.Field("defName", def.defName),
                    Dto.Field("priority", pawn.workSettings.GetPriority(def))))
                .ToArray();
        }
    }
}
