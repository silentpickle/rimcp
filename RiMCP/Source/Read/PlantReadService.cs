using System;
using System.Linq;
using RiMCP.Bridge;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    internal static class PlantReadService
    {
        public static BridgeResponse ListPlants(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            ReadRequest query = context.Request;
            Map map = context.Map;
            ThingDef def = query.OptionalDef<ThingDef>("def");
            bool? harvestable = query.OptionalBool("harvestable");
            bool? canYield = query.OptionalBool("canYield");
            bool? tree = query.OptionalBool("tree");
            bool? stump = query.OptionalBool("stump");
            int? x = query.OptionalInt("x");
            int? z = query.OptionalInt("z");
            int? radius = query.OptionalInt("radius");
            IntVec3? near = x.HasValue && z.HasValue ? new IntVec3(x.Value, 0, z.Value) : (IntVec3?)null;
            if (x.HasValue != z.HasValue || (near.HasValue && !near.Value.InBounds(map))
                || (radius.HasValue && (!near.HasValue || radius.Value < 0)))
            {
                throw new BridgeException(400, "Give x and z together as a map cell, and use a nonnegative radius only with a cell.");
            }

            var matches = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>()
                .Where(plant => plant.Spawned && !plant.Position.Fogged(map))
                .Where(plant => def == null || plant.def == def)
                .Where(plant => !harvestable.HasValue || plant.HarvestableNow == harvestable.Value)
                .Where(plant => !canYield.HasValue || plant.CanYieldNow() == canYield.Value)
                .Where(plant => !tree.HasValue || plant.def.plant.IsTree == tree.Value)
                .Where(plant => !stump.HasValue || plant.def.plant.isStump == stump.Value)
                .Where(plant => !radius.HasValue || plant.Position.DistanceTo(near.Value) <= radius.Value);
            var ordered = near.HasValue
                ? matches.OrderBy(plant => plant.Position.DistanceToSquared(near.Value)).ThenBy(plant => plant.thingIDNumber)
                : matches.OrderBy(plant => plant.def.defName, StringComparer.Ordinal).ThenBy(plant => plant.thingIDNumber);
            Page<Plant> page = new Page<Plant>(ordered, query);
            object[] rows = page.Items.Select(plant =>
                query.IdsOnly ? (object)ReadUtil.ThingId(plant) : Serialize(plant, map)).ToArray();
            return ReadEnvelope.Ok(context, Dto.Obj(Dto.Field("plants", rows)), page.Truncated, page.NextCursor, "plants");
        }

        private static object Serialize(Plant plant, Map map)
        {
            return Dto.Obj(
                Dto.Field("id", ReadUtil.ThingId(plant)),
                Dto.Field("def", plant.def.defName),
                Dto.Field("label", plant.LabelCapNoCount),
                Dto.Field("x", plant.Position.x),
                Dto.Field("z", plant.Position.z),
                Dto.Field("growth", plant.Growth),
                Dto.Field("harvestable", plant.HarvestableNow),
                Dto.Field("canYield", plant.CanYieldNow()),
                Dto.Field("isTree", plant.def.plant.IsTree),
                Dto.Field("isStump", plant.def.plant.isStump),
                Dto.Field("harvestProduct", plant.def.plant.harvestedThingDef?.defName),
                Dto.Field("baseHarvestYield", plant.def.plant.harvestYield),
                Dto.Field("designations", map.designationManager.AllDesignationsOn(plant)
                    .Select(designation => designation.def.defName).ToArray()));
        }
    }
}
