using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Read
{
    internal sealed class MapLayer
    {
        public readonly string Name;
        public readonly Func<IntVec3, Map, char> Sample;
        public readonly char Blank;
        public readonly Dictionary<char, string> Meanings;
        public readonly Func<IntVec3, Map, object> Value;

        public MapLayer(string name, Func<IntVec3, Map, char> sample, char blank, Dictionary<char, string> meanings, Func<IntVec3, Map, object> value = null)
        {
            Name = name;
            Sample = sample;
            Blank = blank;
            Meanings = meanings;
            Value = value ?? ((cell, map) => meanings[sample(cell, map)]);
        }
    }

    // These inexpensive layers read live game state on each request. Fog is checked by the
    // caller before the registry reads a cell. Derived layers can own a longer-lived cache.
    internal static class MapLayerRegistry
    {
        private const char Unmarked = '-';
        // When a cell has several orders, the first symbol in this list wins.
        private const string DesignationPrecedence = "dmchuo-";

        private static readonly MapLayer[] Layers =
        {
            new MapLayer(MapViewShape.Roof, Roof, Unmarked, new Dictionary<char, string>
            {
                { 'R', "thick rock roof" },
                { 'r', "thin rock roof" },
                { 'b', "built roof" },
                { Unmarked, "no roof" }
            }, (cell, map) => map.roofGrid.RoofAt(cell)?.defName),
            new MapLayer(MapViewShape.Designations, Designations, Unmarked, new Dictionary<char, string>
            {
                { 'd', "deconstruct" },
                { 'm', "mine" },
                { 'c', "cut plant" },
                { 'h', "harvest or chop wood" },
                { 'u', "haul" },
                { 'o', "other order" },
                { Unmarked, "no order" }
            }, (cell, map) => map.designationManager.AllDesignationsAt(cell)
                .Select(designation => designation.def.defName).Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray()),
            new MapLayer("terrain", Terrain, Unmarked, new Dictionary<char, string>
            {
                { 's', "soil" }, { 'w', "water" }, { 'f', "constructed floor" }, { 'g', "other terrain" }
            }, (cell, map) => map.terrainGrid.TerrainAt(cell).defName),
            new MapLayer("fertility", Fertility, Unmarked, new Dictionary<char, string>
            {
                { Unmarked, "zero fertility" }, { 'l', "below 100%" }, { 'n', "100%" }, { 'h', "above 100%" }
            }, (cell, map) => map.fertilityGrid.FertilityAt(cell)),
            Flag("passable", (cell, map) => cell.Walkable(map), "walkable", "impassable"),
            new MapLayer("zone", Zone, Unmarked, new Dictionary<char, string>
            {
                { Unmarked, "no zone" }, { 's', "stockpile" }, { 'g', "growing zone" }, { 'o', "other zone" }
            }, (cell, map) => map.zoneManager.ZoneAt(cell) is Zone zone ? ZoneReadService.ZoneId(map, zone) : null),
            Flag("area", (cell, map) => Areas(cell, map).Any(), "in an area", "outside all areas",
                (cell, map) => Areas(cell, map).Select(area => PawnReadService.AllowedAreaId(area, map)).ToArray()),
            Flag("room", (cell, map) => cell.GetRoom(map)?.PsychologicallyOutdoors == false, "indoors", "outdoors or no room",
                (cell, map) => cell.GetRoom(map)?.ID),
            new MapLayer("temperature", Temperature, Unmarked, new Dictionary<char, string>
            {
                { 'f', "below 0 C" }, { 'c', "0 to under 10 C" }, { 'm', "10 to under 30 C" }, { 'h', "30 C or higher" }
            }, (cell, map) => Math.Round(GenTemperature.GetTemperatureForCell(cell, map), 1)),
            Things("building", thing => thing is Building && !(thing is Frame)),
            Things("blueprint", thing => thing is Blueprint || thing is Frame),
            Things("item", thing => thing.def.category == ThingCategory.Item),
            Things("plant", thing => thing is Plant),
            Things("pawn", thing => thing is Pawn)
        };
        private static readonly Dictionary<string, MapLayer> ByName = Layers.ToDictionary(layer => layer.Name, StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<MapLayer> Select(IEnumerable<string> names)
        {
            var seen = new HashSet<MapLayer>();
            foreach (string name in names)
            {
                if (!ByName.TryGetValue(name, out MapLayer layer))
                {
                    throw new BridgeException(400, "Unknown layer '" + name + "'. Supported layers: " + string.Join(", ", Layers.Select(candidate => candidate.Name)) + ".");
                }
                if (seen.Add(layer))
                {
                    yield return layer;
                }
            }
        }

        private static MapLayer Flag(string name, Func<IntVec3, Map, bool> marked, string on, string off,
            Func<IntVec3, Map, object> value = null)
        {
            return new MapLayer(name, (cell, map) => marked(cell, map) ? '+' : Unmarked, Unmarked,
                new Dictionary<char, string> { { '+', on }, { Unmarked, off } }, value ?? ((cell, map) => marked(cell, map)));
        }

        private static MapLayer Things(string name, Func<Thing, bool> matches)
        {
            return Flag(name, (cell, map) => cell.GetThingList(map).Any(thing => matches(thing) && !thing.Position.Fogged(map)), name + " present", "empty",
                (cell, map) => cell.GetThingList(map).Where(matches).Where(thing => !thing.Position.Fogged(map))
                    .OrderBy(thing => thing.thingIDNumber).Select(ReadUtil.ThingId).ToArray());
        }

        private static IEnumerable<Area> Areas(IntVec3 cell, Map map)
        {
            return map.areaManager.AllAreas.Where(area => area[cell]);
        }

        private static char Terrain(IntVec3 cell, Map map)
        {
            TerrainDef terrain = map.terrainGrid.TerrainAt(cell);
            return terrain.IsWater ? 'w' : terrain.designationCategory == DesignationCategoryDefOf.Floors ? 'f' :
                terrain.fertility > 0f ? 's' : 'g';
        }

        private static char Fertility(IntVec3 cell, Map map)
        {
            float fertility = map.fertilityGrid.FertilityAt(cell);
            return fertility <= 0f ? Unmarked : fertility < 1f ? 'l' : fertility == 1f ? 'n' : 'h';
        }

        private static char Zone(IntVec3 cell, Map map)
        {
            Zone zone = map.zoneManager.ZoneAt(cell);
            return zone is Zone_Stockpile ? 's' : zone is Zone_Growing ? 'g' : zone == null ? Unmarked : 'o';
        }

        private static char Temperature(IntVec3 cell, Map map)
        {
            float celsius = GenTemperature.GetTemperatureForCell(cell, map);
            return celsius < 0f ? 'f' : celsius < 10f ? 'c' : celsius < 30f ? 'm' : 'h';
        }

        private static char Roof(IntVec3 cell, Map map)
        {
            RoofDef roof = map.roofGrid.RoofAt(cell);
            return roof == null ? Unmarked : roof.isThickRoof ? 'R' : roof.isNatural ? 'r' : 'b';
        }

        private static char Designations(IntVec3 cell, Map map)
        {
            char shown = Unmarked;
            foreach (Designation designation in map.designationManager.AllDesignationsAt(cell))
            {
                char symbol = DesignationChar(designation.def);
                if (DesignationPrecedence.IndexOf(symbol) < DesignationPrecedence.IndexOf(shown))
                {
                    shown = symbol;
                }
            }
            return shown;
        }

        private static char DesignationChar(DesignationDef def)
        {
            if (def == DesignationDefOf.Deconstruct || def == DesignationDefOf.Uninstall)
            {
                return 'd';
            }
            if (def == DesignationDefOf.Mine || def == DesignationDefOf.MineVein)
            {
                return 'm';
            }
            if (def == DesignationDefOf.CutPlant)
            {
                return 'c';
            }
            if (def == DesignationDefOf.HarvestPlant)
            {
                return 'h';
            }
            return def == DesignationDefOf.Haul ? 'u' : 'o';
        }
    }
}
