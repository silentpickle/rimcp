using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class BuildingReadService
    {
        public static BridgeResponse ListBuildings(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);

            string category = context.Request.Get("category");
            string normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.ToLowerInvariant();
            ReadRequest query = context.Request;
            var included = new HashSet<ThingDef>(query.DefList<ThingDef>("defNames"));
            var excluded = new HashSet<ThingDef>(query.DefList<ThingDef>("excludeDefs"));
            int? x = query.OptionalInt("x"), z = query.OptionalInt("z");
            int? width = query.OptionalInt("width"), height = query.OptionalInt("height");
            CellRect? rect = null;
            if (x.HasValue || z.HasValue || width.HasValue || height.HasValue)
            {
                if (!x.HasValue || !z.HasValue || !width.HasValue || !height.HasValue ||
                    x < 0 || z < 0 || x > 1000 || z > 1000 || width < 1 || height < 1 || width > 1001 || height > 1001)
                    throw new BridgeException(400, "Supply x, z, width and height for the building rectangle.");
                rect = new CellRect(x.Value, z.Value, width.Value, height.Value);
            }
            // Ordinal and numeric sort keys avoid culture-aware string comparison across every
            // colony building, walls and conduits included, on each page.
            IEnumerable<Building> source = context.Map.listerBuildings.allBuildingsColonist
                .Where(building => (included.Count == 0 || included.Contains(building.def)) && !excluded.Contains(building.def))
                .Where(building => !rect.HasValue || rect.Value.Contains(building.Position))
                .Where(building => MatchesCategory(building, normalizedCategory))
                .OrderBy(building => building.def.defName, StringComparer.Ordinal)
                .ThenBy(building => building.thingIDNumber);
            Page<Building> page = new Page<Building>(source, context.Request);
            bool includeContents = context.Request.Wants("contents");

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("category", category),
                Dto.Field("buildings", page.Items.Select(building => SerializeBuilding(building, context.Request.Detail, includeContents)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        public static BridgeResponse GetBuilding(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            string id = route["id"];

            Building building = FindBuilding(context.Map, id);
            if (building == null)
            {
                return BridgeResponse.Error(404, "Building not found.");
            }

            Dictionary<string, object> data = SerializeBuilding(building, ReadDetail.Full, true);
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
            return ReadEnvelope.Ok(context, data);
        }

        public static Building FindBuilding(Map map, string id)
        {
            if (map == null || string.IsNullOrWhiteSpace(id) || map.listerBuildings == null)
            {
                return null;
            }
            return map.listerBuildings.allBuildingsColonist
                .FirstOrDefault(building => building.ThingID == id || building.GetUniqueLoadID() == id);
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

        private static Dictionary<string, object> SerializeBuilding(Building building, ReadDetail detail, bool includeContents)
        {
            Dictionary<string, object> dto = SummaryDto.ThingWithPosition(building);
            dto["hitPoints"] = building.HitPoints;
            dto["maxHitPoints"] = building.MaxHitPoints;
            dto["faction"] = building.Faction == null ? null : building.Faction.Name;
            dto["stuff"] = building.Stuff == null ? null : ReadUtil.Def(building.Stuff);

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
