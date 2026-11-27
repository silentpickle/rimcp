using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class IdeoligionReadService
    {
        public static BridgeResponse ListIdeoligions(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("active", ModsConfig.IdeologyActive),
                Dto.Field("ideoligions", !ModsConfig.IdeologyActive ? new object[0] : Ideos()
                    .OrderBy(ideo => ideo.name)
                    .Select(SerializeIdeoSummary)
                    .ToArray())));
        }

        public static BridgeResponse GetIdeoligion(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            if (!ModsConfig.IdeologyActive)
            {
                return BridgeResponse.Error(409, "Ideology expansion is not active.");
            }

            string id = route["id"];
            Ideo ideo = Ideos().FirstOrDefault(item => IdeoIdMatches(item, id));
            if (ideo == null)
            {
                return BridgeResponse.Error(404, "Ideoligion not found.");
            }

            string view = context.Request.Get("view");
            if (view != null && view != "rituals")
            {
                return BridgeResponse.Error(400, "Field 'view' must be rituals when supplied.");
            }
            return ReadEnvelope.Ok(context, view == "rituals" ? SerializeRituals(ideo, context.Map) : SerializeIdeoDetail(ideo));
        }

        public static object SerializePawnIdeoligionSummary(Pawn pawn)
        {
            if (!ModsConfig.IdeologyActive || pawn == null || pawn.ideo == null)
            {
                return null;
            }

            Ideo ideo = pawn.Ideo;
            if (ideo == null)
            {
                return null;
            }

            return Dto.Obj(
                Dto.Field("id", ideo.GetUniqueLoadID()),
                Dto.Field("numericId", ideo.id),
                Dto.Field("name", ideo.name));
        }

        public static object SerializeIdeoSummary(Ideo ideo)
        {
            if (ideo == null)
            {
                return null;
            }

            return Dto.Obj(
                Dto.Field("id", ideo.GetUniqueLoadID()),
                Dto.Field("numericId", ideo.id),
                Dto.Field("name", ideo.name),
                Dto.Field("adjective", ideo.adjective),
                Dto.Field("memberNamePlural", ideo.MemberNamePlural),
                Dto.Field("fluid", ideo.Fluid),
                Dto.Field("classicMode", ideo.classicMode),
                Dto.Field("initialPlayerIdeo", ideo.initialPlayerIdeo),
                Dto.Field("hidden", ideo.hidden),
                Dto.Field("culture", ReadUtil.Def(ideo.culture)),
                Dto.Field("memes", ideo.memes == null ? new object[0] : ideo.memes.Select(ReadUtil.Def).ToArray()));
        }

        private static object SerializeIdeoDetail(Ideo ideo)
        {
            Dictionary<string, object> dto = (Dictionary<string, object>)SerializeIdeoSummary(ideo);
            dto["description"] = ideo.description;
            dto["precepts"] = ideo.PreceptsListForReading == null
                ? new object[0]
                : ideo.PreceptsListForReading
                    .OrderBy(precept => precept.def == null ? "" : precept.def.defName)
                    .ThenBy(precept => precept.Label)
                    .Select(SerializePrecept)
                    .ToArray();
            return dto;
        }

        private static object SerializeRituals(Ideo ideo, Map map)
        {
            var dto = Dto.Obj(Dto.Field("id", ideo.GetUniqueLoadID()), Dto.Field("name", ideo.name));
            dto["roles"] = ideo.RolesListForReading.Select(role => Dto.Obj(
                Dto.Field("id", role.GetUniqueLoadID()),
                Dto.Field("label", role.Label),
                Dto.Field("active", role.Active),
                Dto.Field("leader", role.def.leaderRole),
                Dto.Field("holders", role.ChosenPawns().Select(SummaryDto.Pawn).ToArray()))).ToArray();
            dto["rituals"] = ideo.PreceptsListForReading.OfType<Precept_Ritual>().Select(ritual => Dto.Obj(
                Dto.Field("id", ritual.GetUniqueLoadID()),
                Dto.Field("label", ritual.Label),
                Dto.Field("def", ritual.def.defName),
                Dto.Field("roleChange", ritual.def == PreceptDefOf.RoleChange),
                Dto.Field("anytime", ritual.isAnytime))).ToArray();
            dto["activeRituals"] = Find.IdeoManager.GetActiveRituals(map)
                .Where(job => job.Ritual?.ideo == ideo && job.selectedTarget.IsValid && job.selectedTarget.Cell.InBounds(map) && !job.selectedTarget.Cell.Fogged(map))
                .Select(job => Dto.Obj(
                    Dto.Field("activeId", job.lord.GetUniqueLoadID()),
                    Dto.Field("ritualId", job.Ritual.GetUniqueLoadID()),
                    Dto.Field("label", job.RitualLabel.StripTags()),
                    Dto.Field("target", Command.Targets.Describe((LocalTargetInfo)job.selectedTarget))))
                .ToArray();
            return dto;
        }

        private static object SerializePrecept(Precept precept)
        {
            if (precept == null)
            {
                return null;
            }

            return Dto.Obj(
                Dto.Field("id", precept.GetUniqueLoadID()),
                Dto.Field("numericId", precept.Id),
                Dto.Field("def", ReadUtil.Def(precept.def)),
                Dto.Field("label", precept.Label),
                Dto.Field("labelCap", precept.LabelCap));
        }

        private static IEnumerable<Ideo> Ideos()
        {
            return Find.World == null || Find.IdeoManager == null
                ? Enumerable.Empty<Ideo>() : Find.IdeoManager.IdeosListForReading;
        }

        private static bool IdeoIdMatches(Ideo ideo, string id)
        {
            return ideo != null &&
                (ideo.GetUniqueLoadID() == id ||
                 ideo.id.ToString() == id ||
                 ideo.name == id);
        }
    }
}
