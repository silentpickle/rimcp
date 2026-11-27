using System;
using System.Globalization;
using System.Linq;
using RiMCP.Bridge;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Read
{
    internal static class SurveyTiles
    {
        private static readonly string[] Coordinates = { "x", "z", "width", "height" };

        public static object Describe(IntVec3 size)
        {
            return Dto.Obj(
                Dto.Field("size", MapViewShape.SurveyTileSize),
                Dto.Field("columns", TileCount(size.x)),
                Dto.Field("rows", TileCount(size.z)),
                Dto.Field("first", "x0z0"),
                Dto.Field("last", "x" + (TileCount(size.x) - 1) + "z" + (TileCount(size.z) - 1)));
        }

        public static CellRect Resolve(ReadRequest request, IntVec3 size)
        {
            if (Coordinates.Any(key => request.Get(key) != null))
            {
                throw new BridgeException(400, "Use tile or x, z, width and height.");
            }
            return ResolveName(request.Get("tile"), size);
        }

        internal static CellRect ResolveName(string name, IntVec3 size)
        {
            if (!TryCoordinates(name, out int x, out int z))
            {
                throw new BridgeException(400, "Give a tile name such as x0z0 from get_map_survey.");
            }
            if (x >= TileCount(size.x) || z >= TileCount(size.z))
            {
                throw new BridgeException(404, "That tile is outside the selected map.");
            }
            int west = x * MapViewShape.SurveyTileSize;
            int south = z * MapViewShape.SurveyTileSize;
            return new CellRect(west, south,
                Math.Min(MapViewShape.SurveyTileSize, size.x - west),
                Math.Min(MapViewShape.SurveyTileSize, size.z - south));
        }

        private static bool TryCoordinates(string name, out int x, out int z)
        {
            x = 0;
            z = 0;
            if (string.IsNullOrEmpty(name) || name[0] != 'x')
            {
                return false;
            }
            int split = name.IndexOf('z');
            return split > 1
                && int.TryParse(name.Substring(1, split - 1), NumberStyles.None, CultureInfo.InvariantCulture, out x)
                && int.TryParse(name.Substring(split + 1), NumberStyles.None, CultureInfo.InvariantCulture, out z);
        }

        private static int TileCount(int size)
        {
            return (size + MapViewShape.SurveyTileSize - 1) / MapViewShape.SurveyTileSize;
        }
    }
}
