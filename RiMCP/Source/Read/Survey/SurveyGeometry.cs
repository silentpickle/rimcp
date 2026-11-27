using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RiMCP.Read
{
    internal static class SurveyGeometry
    {
        private static readonly IntVec3[] NearbyBuckets =
        {
            new IntVec3(-1, 0, -1), new IntVec3(0, 0, -1), new IntVec3(1, 0, -1),
            new IntVec3(-1, 0, 0), new IntVec3(0, 0, 0), new IntVec3(1, 0, 0),
            new IntVec3(-1, 0, 1), new IntVec3(0, 0, 1), new IntVec3(1, 0, 1)
        };
        private static readonly string[] Bearings = { "E", "NE", "N", "NW", "W", "SW", "S", "SE" };

        public static IntVec3 Centre(IEnumerable<IntVec3> source)
        {
            long x = 0, z = 0;
            int count = 0;
            foreach (IntVec3 cell in source)
            {
                x += cell.x;
                z += cell.z;
                count++;
            }
            return new IntVec3((int)((x + count / 2) / count), 0, (int)((z + count / 2) / count));
        }

        public static string Bearing(IntVec3 from, IntVec3 to)
        {
            if (from == to)
            {
                return null;
            }
            double eighth = Math.Atan2(to.z - from.z, to.x - from.x) * Bearings.Length / (Math.PI + Math.PI);
            return Bearings[((int)Math.Round(eighth) + Bearings.Length) % Bearings.Length];
        }

        public static string RelativeHeading(IntVec3 from, IntVec3 goal, IntVec3 colony)
        {
            int change = goal.DistanceToSquared(colony).CompareTo(from.DistanceToSquared(colony));
            return change < 0 ? "toward" : change > 0 ? "away" : "level";
        }

        public static int Distance(IntVec3 from, IntVec3 to)
        {
            return (int)Math.Round(Math.Sqrt(from.DistanceToSquared(to)));
        }

        public static int[] Cell(IntVec3 cell) => new[] { cell.x, cell.z };

        public static int[] Bounds(IEnumerable<IntVec3> source)
        {
            IntVec3[] cells = source.ToArray();
            int west = cells.Min(cell => cell.x);
            int south = cells.Min(cell => cell.z);
            return new[] { west, south, cells.Max(cell => cell.x) - west + 1, cells.Max(cell => cell.z) - south + 1 };
        }

        public static IEnumerable<List<T>> Cluster<T>(IEnumerable<T> source, Func<T, IntVec3> position, int radius)
        {
            T[] items = source.ToArray();
            IntVec3[] cells = items.Select(position).ToArray();
            var buckets = Enumerable.Range(0, cells.Length).ToLookup(i => Bucket(cells[i], radius));
            var seen = new bool[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                if (!seen[i])
                {
                    yield return Grow(items, cells, buckets, seen, i, radius);
                }
            }
        }

        private static List<T> Grow<T>(T[] items, IntVec3[] cells, ILookup<IntVec3, int> buckets, bool[] seen, int start, int radius)
        {
            var group = new List<T>();
            var queue = new Queue<int>();
            Enqueue(queue, seen, start);
            while (queue.Count != 0)
            {
                int current = queue.Dequeue();
                group.Add(items[current]);
                foreach (int candidate in Neighbors(buckets, Bucket(cells[current], radius)))
                {
                    if (!seen[candidate] && cells[current].DistanceToSquared(cells[candidate]) <= radius * radius)
                    {
                        Enqueue(queue, seen, candidate);
                    }
                }
            }
            return group;
        }

        private static IEnumerable<int> Neighbors(ILookup<IntVec3, int> buckets, IntVec3 center)
        {
            foreach (IntVec3 offset in NearbyBuckets)
            {
                foreach (int index in buckets[center + offset])
                {
                    yield return index;
                }
            }
        }

        private static void Enqueue(Queue<int> queue, bool[] seen, int index)
        {
            seen[index] = true;
            queue.Enqueue(index);
        }

        private static IntVec3 Bucket(IntVec3 cell, int radius)
        {
            return new IntVec3(cell.x / radius, 0, cell.z / radius);
        }
    }
}
