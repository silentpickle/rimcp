using System;
using System.Collections.Generic;
using System.Linq;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal enum LegacyArea
    {
        Buildings,
        Things,
        Plants,
        Placement
    }

    internal sealed class ReadArea
    {
        private const int MaxCoordinate = 1000;
        private const int MaxRectangleSide = MaxCoordinate + 1;
        public const int MaxPlacementSide = 20;
        private static readonly string[] Geometry = { "x", "z", "width", "height", "radius" };
        private static readonly string[] Named = { "room", "zone", "map" };

        public IntVec3? Centre { get; }
        public CellRect? Rectangle { get; }
        private readonly int? radius;
        private readonly HashSet<IntVec3> cells;

        private ReadArea(CellRect? rectangle = null, IntVec3? centre = null, int? radius = null, IEnumerable<IntVec3> cells = null)
        {
            Rectangle = rectangle;
            Centre = centre;
            this.radius = radius;
            this.cells = cells == null ? null : new HashSet<IntVec3>(cells);
        }

        public bool Contains(IntVec3 cell)
        {
            return (!Rectangle.HasValue || Rectangle.Value.Contains(cell))
                && (!radius.HasValue || cell.DistanceTo(Centre.Value) <= radius.Value)
                && (cells == null || cells.Contains(cell));
        }

        public static ReadArea Parse(ReadContext context, LegacyArea kind)
        {
            string[] keys = kind == LegacyArea.Placement ? Geometry.Take(Geometry.Length - 1).ToArray() : Geometry.Concat(Named).ToArray();
            ReadRequest nested = context.Request.Section("area", keys, Geometry);
            if (nested == null)
            {
                return ParseGeometry(context.Request, context.Map, kind);
            }
            if (Named.Any(key => nested.Get(key) != null))
            {
                return NamedArea(nested, context.Map);
            }
            if (nested.Get("width") != null || nested.Get("height") != null || kind == LegacyArea.Placement)
            {
                if (nested.Get("radius") != null)
                {
                    throw new BridgeException(400, "Choose a rectangle or a radius in area.");
                }
                return RectangleArea(nested, kind == LegacyArea.Placement);
            }
            ReadArea near = NearArea(nested, context.Map, LegacyArea.Plants);
            if (kind == LegacyArea.Buildings && nested.Get("radius") == null && near.Centre.HasValue)
            {
                return new ReadArea(centre: near.Centre, radius: 0);
            }
            return near;
        }

        private static ReadArea ParseGeometry(ReadRequest request, Map map, LegacyArea kind)
        {
            return kind == LegacyArea.Buildings || kind == LegacyArea.Placement
                ? RectangleArea(request, kind == LegacyArea.Placement)
                : NearArea(request, map, kind);
        }

        private static ReadArea RectangleArea(ReadRequest request, bool placement)
        {
            int? x = request.OptionalInt("x"), z = request.OptionalInt("z");
            if (placement)
            {
                if (!x.HasValue || !z.HasValue)
                {
                    throw new BridgeException(400, "Give x and z: the position to check, or the south-west corner of an area of positions.");
                }
                return new ReadArea(new CellRect(x.Value, z.Value, PlacementSide(request, "width"), PlacementSide(request, "height")));
            }

            int? width = request.OptionalInt("width"), height = request.OptionalInt("height");
            if (!x.HasValue && !z.HasValue && !width.HasValue && !height.HasValue)
            {
                return new ReadArea();
            }
            if (!x.HasValue || !z.HasValue || !width.HasValue || !height.HasValue ||
                x < 0 || z < 0 || x > MaxCoordinate || z > MaxCoordinate ||
                width < 1 || height < 1 || width > MaxRectangleSide || height > MaxRectangleSide)
            {
                throw new BridgeException(400, "Supply x, z, width and height for the building rectangle.");
            }
            return new ReadArea(new CellRect(x.Value, z.Value, width.Value, height.Value));
        }

        private static int PlacementSide(ReadRequest request, string name)
        {
            int side = request.OptionalInt(name) ?? 1;
            if (side < 1 || side > MaxPlacementSide)
            {
                throw new BridgeException(400, "Field '" + name + "' must be 1 to " + MaxPlacementSide + ".");
            }
            return side;
        }

        private static ReadArea NearArea(ReadRequest request, Map map, LegacyArea kind)
        {
            int? x = request.OptionalInt("x"), z = request.OptionalInt("z"), radius = request.OptionalInt("radius");
            IntVec3? centre = x.HasValue && z.HasValue ? new IntVec3(x.Value, 0, z.Value) : (IntVec3?)null;
            bool incomplete = x.HasValue != z.HasValue || (radius.HasValue && !centre.HasValue);
            if (kind == LegacyArea.Plants && (incomplete || (centre.HasValue && !centre.Value.InBounds(map)) || radius < 0))
            {
                throw new BridgeException(400, "Give x and z together as a map cell, and use a nonnegative radius only with a cell.");
            }
            if (incomplete)
            {
                throw new BridgeException(400, "Give x and z together; radius needs them too.");
            }
            return new ReadArea(centre: centre, radius: radius);
        }

        private static ReadArea NamedArea(ReadRequest request, Map map)
        {
            if (request.Query.Count != 1)
            {
                throw new BridgeException(400, "Choose one of map, room or zone without coordinate fields.");
            }
            if (request.OptionalBool("map") == true)
            {
                return new ReadArea();
            }
            string zoneId = request.Get("zone");
            if (zoneId != null)
            {
                Zone zone = ZoneReadService.FindZone(map, zoneId);
                return zone == null ? throw new BridgeException(404, "Zone not found on the selected map.") : new ReadArea(cells: zone.Cells);
            }
            string roomId = request.Get("room");
            if (roomId != null)
            {
                Room room = EnvironmentReadService.FindRoom(map, roomId);
                return room == null ? throw new BridgeException(404, "Room not found on the selected map.") : new ReadArea(cells: room.Cells);
            }
            throw new BridgeException(400, "Use map: true to select the whole map.");
        }
    }
}
