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

        public MapLayer(string name, Func<IntVec3, Map, char> sample, char blank, Dictionary<char, string> meanings)
        {
            Name = name;
            Sample = sample;
            Blank = blank;
            Meanings = meanings;
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
            }),
            new MapLayer(MapViewShape.Designations, Designations, Unmarked, new Dictionary<char, string>
            {
                { 'd', "deconstruct" },
                { 'm', "mine" },
                { 'c', "cut plant" },
                { 'h', "harvest or chop wood" },
                { 'u', "haul" },
                { 'o', "other order" },
                { Unmarked, "no order" }
            })
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
