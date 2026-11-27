using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RiMCP.Read
{
    internal sealed class SurveyRegions
    {
        private readonly char[] terrain;
        private readonly int width;
        private readonly int height;
        private readonly int limit;
        private readonly bool[] seen;
        private readonly int[] queue;
        private readonly char background;
        private readonly Dictionary<char, List<Region>> largest = new Dictionary<char, List<Region>>();

        private SurveyRegions(char[] terrain, IntVec3 size, int limit, char background)
        {
            this.terrain = terrain;
            width = size.x;
            height = size.z;
            this.limit = limit;
            this.background = background;
            seen = new bool[terrain.Length];
            queue = new int[terrain.Length];
        }

        public static Dictionary<string, object> Describe(char[] terrain, IntVec3 size, IReadOnlyDictionary<char, int> counts, int limit)
        {
            char fog = MapLayerProjection.Unexplored;
            counts.TryGetValue(fog, out int unknown);
            int explored = terrain.Length - unknown;
            var dominant = counts.FirstOrDefault(pair => pair.Key != fog && pair.Value > explored / 2);
            var regions = new SurveyRegions(terrain, size, limit, dominant.Key);
            if (limit > 0)
            {
                regions.Scan();
            }
            return Dto.Obj(
                Dto.Field("background", dominant.Value == 0 ? null :
                    Dto.Obj(Dto.Field("class", SurveySymbols.Classes[dominant.Key]), Dto.Field("cells", dominant.Value))),
                Dto.Field("regions", regions.ToDto()));
        }

        private void Scan()
        {
            for (int i = 0; i < terrain.Length; i++)
            {
                char kind = terrain[i];
                if (seen[i] || kind == background)
                {
                    continue;
                }
                Remember(kind, Flood(i, kind));
            }
        }

        private Region Flood(int start, char kind)
        {
            var region = new Region();
            int tail = 0;
            Enqueue(start, ref tail);
            for (int head = 0; head < tail; head++)
            {
                int i = queue[head];
                int x = i % width;
                int z = i / width;
                region.Add(x, z);
                if (x > 0) Visit(i - 1, kind, region, ref tail);
                if (x + 1 < width) Visit(i + 1, kind, region, ref tail);
                if (z > 0) Visit(i - width, kind, region, ref tail);
                if (z + 1 < height) Visit(i + width, kind, region, ref tail);
            }
            return region;
        }

        private void Visit(int i, char kind, Region region, ref int tail)
        {
            char other = terrain[i];
            if (other != kind)
            {
                if (kind == MapLayerProjection.Unexplored)
                {
                    region.VisibleEdges++;
                    region.RockEdges += other == SurveySymbols.Rock ? 1 : 0;
                }
                return;
            }
            if (!seen[i])
            {
                Enqueue(i, ref tail);
            }
        }

        private void Enqueue(int i, ref int tail)
        {
            seen[i] = true;
            queue[tail++] = i;
        }

        private void Remember(char kind, Region region)
        {
            if (!largest.TryGetValue(kind, out List<Region> rows))
            {
                rows = new List<Region>();
                largest.Add(kind, rows);
            }
            int position = rows.FindIndex(previous => region.Cells > previous.Cells);
            if (position < 0)
            {
                position = rows.Count;
            }
            if (position >= limit)
            {
                return;
            }
            rows.Insert(position, region);
            if (rows.Count > limit)
            {
                rows.RemoveAt(limit);
            }
        }

        private object ToDto()
        {
            return largest.OrderBy(group => group.Key).ToDictionary(
                group => SurveySymbols.Classes[group.Key],
                group => group.Value.Select(region => region.ToDto()).ToArray());
        }

        private sealed class Region
        {
            private const int MinimumRockEdges = 4;
            public int Cells { get; private set; }
            public int VisibleEdges;
            public int RockEdges;
            private int minX = int.MaxValue;
            private int minZ = int.MaxValue;
            private int maxX = int.MinValue;
            private int maxZ = int.MinValue;
            private long sumX;
            private long sumZ;

            public void Add(int x, int z)
            {
                Cells++;
                minX = Math.Min(minX, x);
                minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x);
                maxZ = Math.Max(maxZ, z);
                sumX += x;
                sumZ += z;
            }

            public object ToDto()
            {
                return Dto.Obj(
                    Dto.Field("cells", Cells),
                    Dto.Field("bounds", new[] { minX, minZ, maxX - minX + 1, maxZ - minZ + 1 }),
                    Dto.Field("center", new[] { Center(sumX), Center(sumZ) }),
                    Dto.Field("likelyMountainInterior", RockEdges >= MinimumRockEdges && RockEdges >= VisibleEdges - RockEdges ? (object)true : null));
            }

            private int Center(long sum)
            {
                return (int)((sum + Cells / 2) / Cells);
            }
        }
    }
}
