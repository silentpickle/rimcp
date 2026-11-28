using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class ThreatReadService
    {
        public static BridgeResponse ListThreats(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);

            IEnumerable<Thing> threats = context.Map.mapPawns.AllPawnsSpawned
                .Where(pawn => ColonyDangers.IsThreatPawn(pawn) && PawnReadService.Visible(context.Map, pawn))
                .OrderBy(pawn => pawn.LabelShortCap)
                .Cast<Thing>()
                .Concat(Fires(context.Map).OrderBy(fire => fire.Position.x).ThenBy(fire => fire.Position.z));

            Page<Thing> page = new Page<Thing>(threats, context.Request);
            Pawn[] core = PawnReadService.PawnsForFilter(context.Map, "core").Select(role => role.Pawn).ToArray();
            FoodSummary food = context.Map.IsPlayerHome ? FoodSummary.Of(context.Map) : null;
            DangerSnapshot dangers = DangerSnapshot.Of(context.Map, core, food);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("summary", dangers.Threats.ToDto()),
                Dto.Field("dangers", dangers.Rows()),
                Dto.Field("threats", page.Items.Select(thing => thing is Pawn
                    ? SerializeThreatPawn(context.Map, (Pawn)thing, context.Request.Detail)
                    : SerializeFire(context.Map, thing)).ToArray())), page.Truncated, page.NextCursor, "threats");
        }

        // Visible threats by kind. A pawn can be both hostile and a manhunter; total counts it once.
        internal sealed class ThreatCounts
        {
            public int Hostiles;
            public int Manhunters;
            public int Predators;
            public int ThreatPawns;
            public int Fires;

            public int Total
            {
                get { return ThreatPawns + Fires; }
            }

            public object ToDto()
            {
                return Dto.Obj(
                    Dto.Field("hostiles", Hostiles),
                    Dto.Field("manhunters", Manhunters),
                    Dto.Field("predators", Predators),
                    Dto.Field("fires", Fires),
                    Dto.Field("total", Total));
            }
        }

        public static object SummarizeThreats(Map map)
        {
            return map == null ? null : CountThreats(map).ToDto();
        }

        internal static ThreatCounts CountThreats(Map map)
        {
            var counts = new ThreatCounts();
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned.Where(pawn => PawnReadService.Visible(map, pawn)))
            {
                bool hostile = pawn.HostileTo(Faction.OfPlayer);
                bool manhunter = ColonyDangers.IsManhunter(pawn);
                bool predator = ColonyDangers.IsPredator(pawn);
                counts.Hostiles += hostile ? 1 : 0;
                counts.Manhunters += manhunter ? 1 : 0;
                counts.Predators += predator ? 1 : 0;
                counts.ThreatPawns += hostile || manhunter || predator ? 1 : 0;
            }
            counts.Fires = Fires(map).Count();
            return counts;
        }

        private static object SerializeThreatPawn(Map map, Pawn pawn, ReadDetail detail)
        {
            Dictionary<string, object> dto = SummaryDto.Pawn(pawn);
            dto["kind"] = "pawn";
            dto["threatKind"] = ThreatKind(pawn);
            dto["def"] = ReadUtil.Def(pawn.def);
            dto["faction"] = SummaryDto.FactionIdentity(pawn.Faction);
            dto["position"] = ReadUtil.Cell(pawn.Position);
            dto["downed"] = pawn.Downed;
            dto["mentalState"] = pawn.MentalStateDef == null ? null : pawn.MentalStateDef.defName;
            dto["combat"] = detail == ReadDetail.Summary ? null : Dto.Obj(
                Dto.Field("equipment", pawn.equipment == null ? new object[0] : pawn.equipment.AllEquipmentListForReading.Select(t => Dto.Obj(
                    Dto.Field("def", ReadUtil.Def(t.def)),
                    Dto.Field("label", t.LabelCap))).ToArray()),
                Dto.Field("currentJob", PawnReadService.SerializeCurrentJob(pawn)));
            return dto;
        }

        private static object SerializeFire(Map map, Thing fire)
        {
            return Dto.Obj(
                Dto.Field("kind", "fire"),
                Dto.Field("id", ReadUtil.StableSessionId("fire", map.uniqueID, fire.Position, fire.def.defName)),
                Dto.Field("def", ReadUtil.Def(fire.def)),
                Dto.Field("position", ReadUtil.Cell(fire.Position)));
        }

        private static string ThreatKind(Pawn pawn)
        {
            if (ColonyDangers.IsManhunter(pawn))
            {
                return "manhunter";
            }
            if (pawn.RaceProps != null && pawn.RaceProps.Animal)
            {
                return "animal";
            }
            if (pawn.RaceProps != null && pawn.RaceProps.Humanlike)
            {
                return "humanlike";
            }
            return "other";
        }

        // Fires the player can see; one under fog is hidden like anything else there.
        internal static IEnumerable<Thing> Fires(Map map)
        {
            if (map == null || map.listerThings == null)
            {
                return Enumerable.Empty<Thing>();
            }
            return map.listerThings.ThingsOfDef(ThingDefOf.Fire).Where(fire => !fire.Position.Fogged(map));
        }
    }
}
