using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;

namespace RiMCP.Read
{
    internal sealed class MapLayerProjection
    {
        public const char Unexplored = '?';
        public readonly Dictionary<char, int> Counts = new Dictionary<char, int>();
        private readonly HashSet<char> shown = new HashSet<char>();
        public string[] Rows { get; private set; }

        public static MapLayerProjection Read(CellRect bounds, int scale, bool countsOnly, Func<IntVec3, char> sample)
        {
            var projection = new MapLayerProjection();
            if (countsOnly)
            {
                projection.Count(bounds, sample);
            }
            else
            {
                projection.Draw(bounds, scale, sample);
            }
            return projection;
        }

        private void Count(CellRect bounds, Func<IntVec3, char> sample)
        {
            foreach (IntVec3 cell in bounds)
            {
                char symbol = sample(cell);
                Increment(Counts, symbol);
                shown.Add(symbol);
            }
        }

        private void Draw(CellRect bounds, int scale, Func<IntVec3, char> sample)
        {
            int columns = (bounds.Width + scale - 1) / scale;
            int height = (bounds.Height + scale - 1) / scale;
            Rows = new string[height];
            var tile = new Dictionary<char, int>();
            for (int row = 0; row < height; row++)
            {
                int z = bounds.minZ + (height - 1 - row) * scale;
                Rows[row] = DrawRow(bounds, z, scale, columns, sample, tile);
            }
        }

        private string DrawRow(CellRect bounds, int z, int scale, int columns, Func<IntVec3, char> sample, Dictionary<char, int> tile)
        {
            var row = new StringBuilder(columns);
            for (int x = bounds.minX; x <= bounds.maxX; x += scale)
            {
                char symbol = scale == 1 ? SampleCell(sample, new IntVec3(x, 0, z)) :
                    ReadTile(CellRect.FromLimits(x, z, Math.Min(bounds.maxX, x + scale - 1), Math.Min(bounds.maxZ, z + scale - 1)), sample, tile);
                shown.Add(symbol);
                row.Append(symbol);
            }
            return row.ToString();
        }

        private char ReadTile(CellRect bounds, Func<IntVec3, char> sample, Dictionary<char, int> tile)
        {
            tile.Clear();
            char winner = Unexplored;
            int highest = 0;
            foreach (IntVec3 cell in bounds)
            {
                char symbol = SampleCell(sample, cell);
                int count = Increment(tile, symbol);
                if (count > highest || (count == highest && symbol < winner))
                {
                    highest = count;
                    winner = symbol;
                }
            }
            return winner;
        }

        private char SampleCell(Func<IntVec3, char> sample, IntVec3 cell)
        {
            char symbol = sample(cell);
            Increment(Counts, symbol);
            return symbol;
        }

        private static int Increment(Dictionary<char, int> counts, char symbol)
        {
            counts.TryGetValue(symbol, out int count);
            counts[symbol] = count + 1;
            return count + 1;
        }

        public object CountsDto()
        {
            return Counts.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);
        }

        public Dictionary<string, object> Legend(Dictionary<char, string> meanings)
        {
            var legend = new Dictionary<string, object>();
            if (shown.Contains(Unexplored))
            {
                legend[Unexplored.ToString()] = "unexplored";
            }
            foreach (KeyValuePair<char, string> pair in meanings)
            {
                if (shown.Contains(pair.Key))
                {
                    legend[pair.Key.ToString()] = pair.Value;
                }
            }
            return legend;
        }

        public object DescribeLayer(MapLayer layer, bool countsOnly)
        {
            int marked = Counts.Where(pair => pair.Key != layer.Blank && pair.Key != Unexplored).Sum(pair => pair.Value);
            return Dto.Obj(
                Dto.Field("markedCells", marked),
                Dto.Field("rows", !countsOnly && marked != 0 ? Rows : null),
                Dto.Field("counts", countsOnly ? CountsDto() : null),
                Dto.Field("legend", countsOnly || marked != 0 ? Legend(layer.Meanings) : null));
        }
    }
}
