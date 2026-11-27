using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Read
{
    internal static class BuildingReadService
    {
        private static readonly string[] FilterKeys = { "category", "def", "excludeDefs" };
        private static readonly string[] FlatFilterKeys = { "category", "defNames", "excludeDefs" };

        public static BridgeResponse ListBuildings(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);

            ReadRequest query = context.Request;
            string scope = query.Get("scope");
            bool whole = IsMapScope(scope);
            ReadRequest filter = query.Section("filter", FilterKeys, FlatFilterKeys);
            string category = (filter ?? query).Get("category");
            string normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.ToLowerInvariant();
            var included = filter == null ? new HashSet<ThingDef>(query.DefList<ThingDef>("defNames")) : filter.MatchDefs(true);
            var excluded = new HashSet<ThingDef>((filter ?? query).DefList<ThingDef>("excludeDefs"));
            ReadArea area = ReadArea.Parse(context, LegacyArea.Buildings);
            ReadProjection fields = ReadProjection.Parse(query, ReadShape.BuildingFields);
            // Ordinal and numeric sort keys avoid culture-aware string comparison across every
            // colony building, walls and conduits included, on each page.
            IEnumerable<Building> source = (whole ? MapBuildings(context.Map) : context.Map.listerBuildings.allBuildingsColonist)
                .Where(building => (included.Count == 0 || included.Contains(building.def)) && !excluded.Contains(building.def))
                .Where(building => area.Contains(building.Position))
                .Where(building => MatchesCategory(building, normalizedCategory))
                .OrderBy(building => building.def.defName, StringComparer.Ordinal)
                .ThenBy(building => building.thingIDNumber);
            Page<Building> page = new Page<Building>(source, context.Request);
            bool includeContents = context.Request.Wants("contents");

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("category", category),
                Dto.Field("scope", scope),
                Dto.Field("buildings", page.Items.Select(building => ReadProjection.Serialize(fields, building, () => SerializeBuilding(building, query.Detail, includeContents && (!whole || building.Faction == Faction.OfPlayer), query.Wants("inspectText")))).ToArray())),
                page.Truncated,
                page.NextCursor, "buildings");
        }

        public static BridgeResponse GetBuilding(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            string id = route["id"];

            ReadProjection fields = ReadProjection.Parse(context.Request, ReadShape.BuildingFields);
            if (ReadBatch.IsBatch(context.Request, id))
            {
                return ReadBatch.Reply(context, "buildings", requested => DescribeId(context, requested, fields));
            }
            object data = DescribeId(context, id, fields);
            return data == null ? BridgeResponse.Error(404, "Building not found.") : ReadEnvelope.Ok(context, data);
        }

        private static object DescribeId(ReadContext context, string id, ReadProjection fields)
        {
            Map map = context.Map;
            bool inspect = context.Request.Wants("inspectText");
            Building colonyBuilding = FindBuilding(map, id);
            if (colonyBuilding != null)
            {
                return ReadProjection.Serialize(fields, colonyBuilding, () => DescribeBuilding(colonyBuilding, inspect));
            }
            Building building = map.listerThings.AllThings.OfType<Building>()
                .FirstOrDefault(candidate => MatchesId(candidate, id) && SurveyNotable.VisibleBuilding(candidate, map));
            return building == null ? null :
                ReadProjection.Serialize(fields, building, () => SerializeBuilding(building, ReadDetail.Summary, false, inspect));
        }

        private static object DescribeBuilding(Building building, bool inspect)
        {
            Dictionary<string, object> data = SerializeBuilding(building, ReadDetail.Full, true, inspect);
            CompAnimalPenMarker marker = building.TryGetComp<CompAnimalPenMarker>();
            if (marker != null && building.Spawned)
            {
                PenMarkerState state = marker.PenState;
                PenFoodCalculator food = marker.PenFoodCalculator;
                data["pen"] = Dto.Obj(
                    Dto.Field("enclosed", state.Enclosed),
                    Dto.Field("outsideAccess", state.HasOutsideAccess),
                    Dto.Field("passableDoors", state.PassableDoors),
                    Dto.Field("cells", food.numCells),
                    Dto.Field("soilCells", food.numCellsSoil),
                    Dto.Field("stockpiledNutrition", food.sumStockpiledNutritionAvailableNow),
                    Dto.Field("forageNutritionPerDayToday", food.NutritionPerDayToday),
                    Dto.Field("animalNutritionPerDay", food.SumNutritionConsumptionPerDay),
                    Dto.Field("animals", food.ActualAnimalInfos.Where(info => info.TotalCount > 0).Select(info => Dto.Obj(
                        Dto.Field("def", info.animalDef.defName),
                        Dto.Field("count", info.TotalCount),
                        Dto.Field("nutritionPerDay", info.TotalNutritionConsumptionPerDay))).ToArray()));
            }
            return data;
        }

        public static Building FindBuilding(Map map, string id)
        {
            if (map == null || string.IsNullOrWhiteSpace(id) || map.listerBuildings == null)
            {
                return null;
            }
            return map.listerBuildings.allBuildingsColonist
                .FirstOrDefault(building => MatchesId(building, id));
        }

        private static bool IsMapScope(string scope)
        {
            if (scope == null || string.Equals(scope, "colony", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (string.Equals(scope, "map", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            throw new BridgeException(400, "Field 'scope' must be colony or map.");
        }

        private static IEnumerable<Building> MapBuildings(Map map)
        {
            return map.listerThings.AllThings.OfType<Building>().Where(building => SurveyNotable.VisibleBuilding(building, map));
        }

        private static bool MatchesId(Building building, string id)
        {
            return building.ThingID == id || building.GetUniqueLoadID() == id;
        }

        private static bool MatchesCategory(Building building, string normalized)
        {
            if (normalized == null)
            {
                return true;
            }
            if (normalized == "production")
            {
                return building is IBillGiver;
            }
            if (normalized == "power")
            {
                return building.TryGetComp<CompPowerTrader>() != null ||
                       building.TryGetComp<CompPowerBattery>() != null ||
                       building.TryGetComp<CompRefuelable>() != null;
            }
            if (normalized == "bed")
            {
                return building is Building_Bed;
            }
            if (normalized == "storage")
            {
                return building is ISlotGroupParent;
            }
            return building.def.defName.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Dictionary<string, object> SerializeBuilding(Building building, ReadDetail detail, bool includeContents, bool inspect = false)
        {
            Dictionary<string, object> dto = SummaryDto.ThingWithPosition(building);
            dto["hitPoints"] = building.HitPoints;
            dto["maxHitPoints"] = building.MaxHitPoints;
            dto["faction"] = building.Faction == null ? null : building.Faction.Name;
            dto["stuff"] = building.Stuff == null ? null : ReadUtil.Def(building.Stuff);

            if (inspect)
            {
                dto["inspectText"] = ReadProjection.InspectText(building);
            }
            if (detail != ReadDetail.Summary)
            {
                CompPowerTrader power = building.TryGetComp<CompPowerTrader>();
                CompPowerBattery battery = building.TryGetComp<CompPowerBattery>();
                CompRefuelable fuel = building.TryGetComp<CompRefuelable>();
                dto["size"] = Dto.Obj(Dto.Field("x", building.def.size.x), Dto.Field("z", building.def.size.z));
                dto["passability"] = building.def.passability.ToString();
                dto["power"] = power == null ? null : Dto.Obj(
                    Dto.Field("powerOn", power.PowerOn),
                    Dto.Field("powerOutput", power.PowerOutput));
                dto["battery"] = battery == null ? null : Dto.Obj(
                    Dto.Field("storedEnergy", battery.StoredEnergy));
                dto["fuel"] = fuel == null ? null : Dto.Obj(
                    Dto.Field("fuel", fuel.Fuel),
                    Dto.Field("targetFuelLevel", fuel.TargetFuelLevel));
                dto["billGiver"] = building is IBillGiver;
                dto["rotation"] = building.def.rotatable ? RiMCP.Command.BuildPlacement.RotationName(building.Rotation) : null;
                dto["temperatureControl"] = TemperatureControlRead.Describe(building);
            }
            if (includeContents)
            {
                dto["contents"] = SerializeContents(building);
            }
            return dto;
        }

        private static object SerializeContents(Building building)
        {
            ISlotGroupParent storage = building as ISlotGroupParent;
            if (storage == null)
            {
                return Dto.Obj(
                    Dto.Field("supported", false),
                    Dto.Field("items", new object[0]));
            }

            SlotGroup slotGroup = storage.GetSlotGroup();
            object[] items = slotGroup == null
                ? new object[0]
                : slotGroup.HeldThings
                    .Where(thing => thing != null)
                    .OrderBy(thing => thing.def == null ? "" : thing.def.defName, StringComparer.Ordinal)
                    .ThenBy(thing => thing.thingIDNumber)
                    .Select(SerializeContainedItem)
                    .ToArray();

            return Dto.Obj(
                Dto.Field("supported", true),
                Dto.Field("items", items));
        }

        private static object SerializeContainedItem(Thing thing)
        {
            Dictionary<string, object> dto = SummaryDto.ThingWithPosition(thing);
            dto["stackCount"] = thing.stackCount;
            dto["hitPoints"] = thing.HitPoints;
            dto["forbidden"] = thing.IsForbidden(Faction.OfPlayer);
            dto["quality"] = ReadUtil.QualityLabel(thing);
            return dto;
        }
    }
}
