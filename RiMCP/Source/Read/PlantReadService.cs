using System;
using System.Linq;
using RiMCP.Bridge;
using RiMCP.Shared;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    internal static class PlantReadService
    {
        private static readonly string[] FilterKeys = { "def", "harvestable", "canYield", "tree", "stump" };

        public static BridgeResponse ListPlants(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            ReadRequest query = context.Request;
            Map map = context.Map;
            ReadRequest filter = query.Section("filter", FilterKeys, FilterKeys);
            var defs = (filter ?? query).MatchDefs(filter != null);
            bool? harvestable = (filter ?? query).OptionalBool("harvestable");
            bool? canYield = (filter ?? query).OptionalBool("canYield");
            bool? tree = (filter ?? query).OptionalBool("tree");
            bool? stump = (filter ?? query).OptionalBool("stump");
            ReadArea area = ReadArea.Parse(context, LegacyArea.Plants);
            IntVec3? near = area.Centre;

            var matches = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>()
                .Where(plant => plant.Spawned && !plant.Position.Fogged(map))
                .Where(plant => defs.Count == 0 || defs.Contains(plant.def))
                .Where(plant => !harvestable.HasValue || plant.HarvestableNow == harvestable.Value)
                .Where(plant => !canYield.HasValue || plant.CanYieldNow() == canYield.Value)
                .Where(plant => !tree.HasValue || plant.def.plant.IsTree == tree.Value)
                .Where(plant => !stump.HasValue || plant.def.plant.isStump == stump.Value)
                .Where(plant => area.Contains(plant.Position));
            var ordered = near.HasValue
                ? matches.OrderBy(plant => plant.Position.DistanceToSquared(near.Value)).ThenBy(plant => plant.thingIDNumber)
                : matches.OrderBy(plant => plant.def.defName, StringComparer.Ordinal).ThenBy(plant => plant.thingIDNumber);
            ReadProjection fields = ReadProjection.Parse(query, ReadShape.PlantFields);
            Page<Plant> page = new Page<Plant>(ordered, query);
            object[] rows = page.Items.Select(plant =>
                query.IdsOnly ? (object)ReadUtil.ThingId(plant) : ReadProjection.Serialize(fields, plant, () => Serialize(plant, map))).ToArray();
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
