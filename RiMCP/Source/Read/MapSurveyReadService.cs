using System;
using System.Collections.Generic;
using System.Linq;
using RiMCP.Bridge;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Read
{
    internal static class MapSurveyReadService
    {
        internal const int OverviewSide = 45;
        private const int CombinedOverviewSide = 32;

        public static BridgeResponse GetMapSurvey(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            Map map = context.Map;
            ReadRequest query = context.Request;
            bool includeMovers = query.Include.Contains("movers");
            bool includeNotable = query.Include.Contains("notable");
            bool extras = includeMovers || includeNotable;
            int perClass = Limit(query, "regionsPerClass", extras ? 0 : MapViewShape.DefaultRegionsPerClass, MapViewShape.MaxRegionsPerClass);
            int moverLimit = Limit(query, "moverLimit", MapViewShape.DefaultSurveyGroups, MapViewShape.MaxSurveyGroups);
            int notableLimit = Limit(query, "notableLimit", MapViewShape.DefaultSurveyGroups, MapViewShape.MaxSurveyGroups);
            IntVec3[] colonists = extras ? SurveyMovers.VisibleColonists(map) : Array.Empty<IntVec3>();
            IntVec3 colony = colonists.Length == 0 ? map.Center : SurveyGeometry.Centre(colonists);
            var movers = includeMovers ? new SurveyMovers(map, colony, colonists) : null;
            var data = query.OptionalBool("overview") == false ? new Dictionary<string, object>() :
                Overview(map, perClass, movers, extras ? CombinedOverviewSide : OverviewSide);
            data["movers"] = movers?.Describe(moverLimit);
            data["notable"] = includeNotable ? SurveyNotable.Describe(map, colony, notableLimit) : null;
            data["tiles"] = SurveyTiles.Describe(map.Size);
            return ReadEnvelope.Ok(context, data, detailSection: "regions", revisionScope: "survey");
        }

        private static int Limit(ReadRequest query, string name, int fallback, int maximum)
        {
            int result = query.OptionalInt(name) ?? fallback;
            if (result < 0 || result > maximum)
            {
                throw new BridgeException(400, "Field '" + name + "' must be 0 to " + maximum + ".");
            }
            return result;
        }

        private static Dictionary<string, object> Overview(Map map, int perClass, SurveyMovers movers, int side)
        {
            var bounds = new CellRect(0, 0, map.Size.x, map.Size.z);
            var counts = new Dictionary<char, int>();
            char[] terrain = ReadTerrain(map, bounds, counts);
            Dictionary<IntVec3, char> pawns = MapViewReadService.PawnSymbols(map, bounds, null);
            var marks = movers?.Overlay();
            int scale = (Math.Max(bounds.Width, bounds.Height) + side - 1) / side;
            MapLayerProjection overview = MapLayerProjection.ReadOverview(bounds, scale,
                cell => Overlay(terrain[cell.z * bounds.Width + cell.x], cell, pawns, marks), SurveySymbols.Priority);
            var data = SurveyRegions.Describe(terrain, map.Size, counts, perClass);
            data["width"] = bounds.Width;
            data["height"] = bounds.Height;
            data["overview"] = Dto.Obj(
                Dto.Field("scale", scale),
                Dto.Field("rows", overview.Rows),
                Dto.Field("rowZ", Enumerable.Range(0, overview.Rows.Length).Select(row => (overview.Rows.Length - 1 - row) * scale).ToArray()),
                Dto.Field("legend", overview.Legend(SurveySymbols.Classes)));
            return data;
        }

        private static char[] ReadTerrain(Map map, CellRect bounds, Dictionary<char, int> counts)
        {
            var terrain = new char[bounds.Width * bounds.Height];
            float farmable = GrowingSummary.ZoneMinFertility();
            foreach (IntVec3 cell in bounds)
            {
                char symbol = cell.Fogged(map) ? MapLayerProjection.Unexplored :
                    SurveySymbols.Terrain(MapViewReadService.CellSymbol(cell, map, farmable, showItems: false));
                terrain[cell.z * bounds.Width + cell.x] = symbol;
                counts.TryGetValue(symbol, out int count);
                counts[symbol] = count + 1;
            }
            return terrain;
        }

        private static char Overlay(char terrain, IntVec3 cell, Dictionary<IntVec3, char> pawns, Dictionary<IntVec3, char> marks)
        {
            if (terrain == MapLayerProjection.Unexplored)
            {
                return terrain;
            }
            if (pawns.TryGetValue(cell, out char pawn) && SurveySymbols.IsColonist(pawn))
            {
                return SurveySymbols.Colonist;
            }
            if (marks == null)
            {
                return pawn == 'h' ? SurveySymbols.Hostile : terrain;
            }
            return marks.TryGetValue(cell, out char group) ? group : terrain;
        }
    }
}
