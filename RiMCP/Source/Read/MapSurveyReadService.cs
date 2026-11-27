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

        public static BridgeResponse GetMapSurvey(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            Map map = context.Map;
            int perClass = context.Request.OptionalInt("regionsPerClass") ?? MapViewShape.DefaultRegionsPerClass;
            if (perClass < 0 || perClass > MapViewShape.MaxRegionsPerClass)
            {
                throw new BridgeException(400, "Field 'regionsPerClass' must be 0 to " + MapViewShape.MaxRegionsPerClass + ".");
            }

            var bounds = new CellRect(0, 0, map.Size.x, map.Size.z);
            var counts = new Dictionary<char, int>();
            char[] terrain = ReadTerrain(map, bounds, counts);
            Dictionary<IntVec3, char> pawns = MapViewReadService.PawnSymbols(map, bounds, null);
            int scale = (Math.Max(bounds.Width, bounds.Height) + OverviewSide - 1) / OverviewSide;
            MapLayerProjection overview = MapLayerProjection.ReadOverview(bounds, scale,
                cell => Overlay(terrain[cell.z * bounds.Width + cell.x], cell, pawns), SurveySymbols.Priority);
            var data = SurveyRegions.Describe(terrain, map.Size, counts, perClass);
            data["width"] = bounds.Width;
            data["height"] = bounds.Height;
            data["overview"] = Dto.Obj(
                Dto.Field("scale", scale),
                Dto.Field("rows", overview.Rows),
                Dto.Field("rowZ", Enumerable.Range(0, overview.Rows.Length).Select(row => (overview.Rows.Length - 1 - row) * scale).ToArray()),
                Dto.Field("legend", overview.Legend(SurveySymbols.Classes)));
            data["tiles"] = SurveyTiles.Describe(map.Size);
            return ReadEnvelope.Ok(context, data, detailSection: "regions");
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

        private static char Overlay(char terrain, IntVec3 cell, Dictionary<IntVec3, char> pawns)
        {
            if (terrain == MapLayerProjection.Unexplored || !pawns.TryGetValue(cell, out char pawn))
            {
                return terrain;
            }
            if (SurveySymbols.IsColonist(pawn))
            {
                return SurveySymbols.Colonist;
            }
            return pawn == 'h' ? SurveySymbols.Hostile : terrain;
        }
    }
}
