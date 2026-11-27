using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Read
{
    // A compact picture of part of a map: one character per cell, north at the top. Each cell shows
    // the most important thing on it: a pawn, then a building or blueprint, a tree, an item, a zone,
    // and the ground. Capital letters are colonists, named in the legend. Layers are extra grids over
    // the same cells, each with its own symbols. Unexplored cells reveal nothing.
    internal static class MapViewReadService
    {
        private static readonly Dictionary<char, string> Meanings = new Dictionary<char, string>
        {
            { '@', "colonist past Z" },
            { 'h', "hostile" },
            { 'a', "animal" },
            { 'p', "other pawn, such as a prisoner, visitor, or mechanoid" },
            { '^', "natural rock" },
            { '#', "wall or impassable building" },
            { '+', "door" },
            { 'b', "other building" },
            { 'x', "blueprint or unfinished building" },
            { 't', "tree" },
            { '*', "item" },
            { '=', "stockpile zone" },
            { '%', "growing zone" },
            { 'w', "deep water" },
            { '~', "shallow water" },
            { ':', "rich soil" },
            { '.', "soil" },
            { ',', "poor soil" },
            { '_', "ground too poor to farm, or floor" }
        };

        public static BridgeResponse GetMapView(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            ReadRequest query = context.Request;
            Map map = context.Map;
            int scale = Scale(query);
            string projection = Projection(query);
            bool countsOnly = projection == MapViewShape.Counts;
            bool cellsOnly = projection == MapViewShape.Cells;
            string tile = query.Get("tile");
            CellRect bounds = tile == null ? Bounds(query, map, scale, countsOnly, cellsOnly) : SurveyTiles.Resolve(query, map.Size);
            MapLayer[] layers = MapLayerRegistry.Select(query.List("layers")).ToArray();

            if (cellsOnly)
            {
                return ReadCells(context, bounds, layers, scale);
            }

            var meanings = new Dictionary<char, string>();
            Dictionary<IntVec3, char> pawns = PawnSymbols(map, bounds, meanings);
            foreach (var pair in Meanings)
            {
                meanings[pair.Key] = pair.Value;
            }
            float farmable = GrowingSummary.ZoneMinFertility();
            MapLayerProjection view = ReadLayer(map, bounds, scale, countsOnly,
                cell => pawns.TryGetValue(cell, out char pawn) ? pawn : CellSymbol(cell, map, farmable));
            var layerDtos = layers.ToDictionary(layer => layer.Name,
                layer => ReadLayer(map, bounds, scale, countsOnly, cell => layer.Sample(cell, map)).DescribeLayer(layer, countsOnly));

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("origin", Dto.Obj(Dto.Field("x", bounds.minX), Dto.Field("z", bounds.minZ))),
                Dto.Field("tile", tile),
                Dto.Field("width", bounds.Width),
                Dto.Field("height", bounds.Height),
                Dto.Field("scale", scale > 1 ? (int?)scale : null),
                Dto.Field("rows", view.Rows),
                Dto.Field("counts", countsOnly ? view.CountsDto() : null),
                Dto.Field("legend", view.Legend(meanings)),
                Dto.Field("layers", layerDtos.Count == 0 ? null : layerDtos)));
        }

        private static BridgeResponse ReadCells(ReadContext context, CellRect bounds, MapLayer[] layers, int scale)
        {
            if (scale != 1 || bounds.Width * bounds.Height > MapViewShape.MaxDetailedCells || layers.Length == 0)
            {
                throw new BridgeException(400, "Cell reads need scale 1, one or more layers, and at most " +
                    MapViewShape.MaxDetailedCells + " cells. Use a rectangle instead of a survey tile.");
            }
            var cells = bounds.Select(cell =>
            {
                var row = Dto.Obj(Dto.Field("x", cell.x), Dto.Field("z", cell.z));
                if (cell.Fogged(context.Map))
                {
                    row["unexplored"] = true;
                }
                else
                {
                    foreach (MapLayer layer in layers)
                    {
                        row[layer.Name] = layer.Value(cell, context.Map);
                    }
                }
                return row;
            }).ToArray();
            return ReadEnvelope.Ok(context, Dto.Obj(Dto.Field("cells", cells)));
        }

        private static MapLayerProjection ReadLayer(Map map, CellRect bounds, int scale, bool countsOnly, Func<IntVec3, char> sample)
        {
            return MapLayerProjection.Read(bounds, scale, countsOnly, cell => cell.Fogged(map) ? MapLayerProjection.Unexplored : sample(cell));
        }

        private static CellRect Bounds(ReadRequest query, Map map, int scale, bool countsOnly, bool cellsOnly)
        {
            int limit = countsOnly ? MapViewShape.MaxCellSpan : GridCellLimit(scale);
            int fallback = cellsOnly ? 1 : Math.Min(MapViewShape.DefaultGridSize * scale, limit);
            int width = Math.Min(Size(query, "width", fallback, limit), map.Size.x);
            int height = Math.Min(Size(query, "height", fallback, limit), map.Size.z);
            IntVec3 centre = ColonistCentre(map);
            int x0 = query.OptionalInt("x") ?? centre.x - width / 2;
            int z0 = query.OptionalInt("z") ?? centre.z - height / 2;
            return new CellRect(Math.Max(0, Math.Min(x0, map.Size.x - width)),
                Math.Max(0, Math.Min(z0, map.Size.z - height)), width, height);
        }

        private static int GridCellLimit(int scale)
        {
            return scale == 1 ? MapViewShape.MaxFineGridSize : Math.Min(MapViewShape.MaxCellSpan, MapViewShape.MaxCoarseGridSize * scale);
        }

        private static int Scale(ReadRequest request)
        {
            return Size(request, "scale", 1, MapViewShape.MaxScale);
        }

        private static string Projection(ReadRequest request)
        {
            string projection = request.Get("as")?.ToLowerInvariant() ?? MapViewShape.Grid;
            if (projection != MapViewShape.Grid && projection != MapViewShape.Counts && projection != MapViewShape.Cells)
            {
                throw new BridgeException(400, "Field 'as' must be grid, counts, or cells.");
            }
            return projection;
        }

        private static int Size(ReadRequest query, string field, int fallback, int limit)
        {
            int size = query.OptionalInt(field) ?? fallback;
            if (size < 1 || size > limit)
            {
                throw new BridgeException(400, "Field '" + field + "' must be 1 to " + limit + ".");
            }
            return size;
        }

        // Colonists get letters by when they arrived (A is the earliest ThingID), so a colonist keeps
        // its letter from one view to the next; the legend gives each one's name, id, and job. A
        // colonist sharing a cell with another pawn wins the cell.
        internal static Dictionary<IntVec3, char> PawnSymbols(Map map, CellRect view, Dictionary<char, string> meanings)
        {
            Dictionary<IntVec3, char> symbols = new Dictionary<IntVec3, char>();
            List<Pawn> colonists = map.mapPawns.AllPawnsSpawned
                .Where(pawn => pawn.Faction == Faction.OfPlayer && pawn.RaceProps.Humanlike)
                .OrderBy(pawn => pawn.thingIDNumber)
                .ToList();
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn colonist = colonists[i];
                if (!view.Contains(colonist.Position) || colonist.Position.Fogged(map))
                {
                    continue;
                }
                char letter = i < 26 ? (char)('A' + i) : '@';
                symbols[colonist.Position] = letter;
                if (letter != '@' && meanings != null)
                {
                    meanings[letter] = Describe(colonist);
                }
            }
            foreach (Pawn other in map.mapPawns.AllPawnsSpawned)
            {
                if (colonists.Contains(other) || !view.Contains(other.Position) || other.Position.Fogged(map) || symbols.ContainsKey(other.Position))
                {
                    continue;
                }
                symbols[other.Position] = other.HostileTo(Faction.OfPlayer) ? 'h' : other.RaceProps.Animal ? 'a' : 'p';
            }
            return symbols;
        }

        private static string Describe(Pawn colonist)
        {
            string job = null;
            try
            {
                job = colonist.CurJob == null ? null : colonist.CurJob.GetReport(colonist);
            }
            catch
            {
            }
            string state = colonist.Downed ? "downed" : colonist.Drafted ? "drafted" : job;
            return ReadUtil.PawnName(colonist) + " (" + ReadUtil.ThingId(colonist) + ")" + (string.IsNullOrWhiteSpace(state) ? "" : ": " + state.StripTags());
        }

        // Ground below farmable fertility, such as sand, cannot hold a growing zone, so it is not soil.
        internal static char CellSymbol(IntVec3 cell, Map map, float farmable, bool showItems = true)
        {
            Building edifice = cell.GetEdifice(map);
            if (edifice != null)
            {
                if (edifice.def.building != null && edifice.def.building.isNaturalRock)
                {
                    return '^';
                }
                if (edifice is Frame)
                {
                    return 'x';
                }
                if (edifice is Building_Door)
                {
                    return '+';
                }
                return edifice.def.passability == Traversability.Impassable ? '#' : 'b';
            }
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Blueprint || things[i] is Frame)
                {
                    return 'x';
                }
            }
            Plant plant = cell.GetPlant(map);
            if (plant != null && plant.def.plant != null && plant.def.plant.IsTree)
            {
                return 't';
            }
            for (int i = 0; i < things.Count; i++)
            {
                if (showItems && things[i].def.EverHaulable)
                {
                    return '*';
                }
                if (things[i].def.category == ThingCategory.Building)
                {
                    return 'b';
                }
            }
            Zone zone = map.zoneManager.ZoneAt(cell);
            if (zone is Zone_Stockpile)
            {
                return '=';
            }
            if (zone is Zone_Growing)
            {
                return '%';
            }
            TerrainDef terrain = cell.GetTerrain(map);
            if (terrain.IsWater)
            {
                return terrain.passability == Traversability.Impassable ? 'w' : '~';
            }
            float fertility = cell.GetFertility(map);
            return fertility >= 1.4f ? ':' : fertility >= 0.7f ? '.' : fertility >= farmable ? ',' : '_';
        }

        private static IntVec3 ColonistCentre(Map map)
        {
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            if (colonists.Count == 0)
            {
                return map.Center;
            }
            return new IntVec3(
                (int)Math.Round(colonists.Average(pawn => pawn.Position.x)),
                0,
                (int)Math.Round(colonists.Average(pawn => pawn.Position.z)));
        }
    }
}
