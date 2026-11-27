using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class DerivedMapLayers
    {
        internal static readonly string[] Names = { "soilPatch", "ore", "buildSite", "reach", "chokepoints" };
        private static readonly Dictionary<int, Snapshot> byMap = new Dictionary<int, Snapshot>();
        private static string loadId;

        internal static Snapshot Get(Map map, bool refresh)
        {
            string current = GameContext.LoadId;
            if (loadId != current)
            {
                byMap.Clear();
                loadId = current;
            }
            if (!byMap.TryGetValue(map.uniqueID, out Snapshot snapshot) ||
                snapshot.Ready.IsSet && (refresh || Find.TickManager.TicksGame - snapshot.Tick >= GenTicks.TickRareInterval))
            {
                byMap[map.uniqueID] = snapshot = new Snapshot(map);
            }
            return snapshot;
        }

        internal sealed class Snapshot
        {
            private readonly int width;
            private readonly int height;
            private readonly string[] ores;
            private readonly bool[] soil;
            private readonly bool[] building;
            private readonly int[] regionAt;
            private readonly List<int>[] graph;
            private readonly int[] roots;
            private readonly int[] soilSize;
            private readonly int[] buildSize;
            private bool[] reachable;
            private bool[] choke;
            private Exception error;

            internal readonly int Tick;
            internal readonly ManualResetEventSlim Ready = new ManualResetEventSlim();

            internal Snapshot(Map map)
            {
                Tick = Find.TickManager.TicksGame;
                width = map.Size.x;
                height = map.Size.z;
                int cells = width * height;
                ores = new string[cells];
                soil = new bool[cells];
                building = new bool[cells];
                soilSize = new int[cells];
                buildSize = new int[cells];
                regionAt = Enumerable.Repeat(-1, cells).ToArray();
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                var regions = new Dictionary<Region, int>();
                float minimum = GrowingSummary.ZoneMinFertility();
                foreach (IntVec3 cell in map.AllCells)
                {
                    if (cell.Fogged(map)) continue;
                    int i = Index(cell);
                    Building edifice = cell.GetEdifice(map);
                    if (edifice != null && SurveyNotable.IsExposedOre(edifice, map))
                    {
                        ores[i] = edifice.def.building.mineableThing.defName;
                    }
                    soil[i] = edifice == null && cell.Walkable(map) && map.fertilityGrid.FertilityAt(cell) >= minimum;
                    building[i] = cell.x >= 10 && cell.z >= 10 && cell.x < width - 10 && cell.z < height - 10 && edifice == null && cell.Walkable(map) &&
                        cell.GetAffordances(map).Contains(TerrainAffordanceDefOf.Heavy) &&
                        !cell.GetThingList(map).Any(thing => thing is Blueprint || thing is Frame);
                    Region region = map.regionGrid.GetRegionAt_NoRebuild_InvalidAllowed(cell);
                    if (region == null || !region.valid || !region.type.Passable()) continue;
                    if (!regions.TryGetValue(region, out int id))
                    {
                        regions.Add(region, id = regions.Count);
                    }
                    regionAt[i] = id;
                }

                graph = regions.Keys.Select(_ => new List<int>()).ToArray();
                // Only keep a region link when the shared boundary reaches explored cells on both sides.
                var contacts = new HashSet<(int left, int right)>();
                for (int i = 0; i < cells; i++)
                {
                    if (regionAt[i] < 0) continue;
                    if (i % width + 1 < width) RecordContact(contacts, i, i + 1);
                    if (i / width + 1 < height) RecordContact(contacts, i, i + width);
                }
                foreach (var pair in regions)
                {
                    foreach (RegionLink link in pair.Key.links)
                    {
                        Region other = link.GetOtherRegion(pair.Key);
                        if (other != null && regions.TryGetValue(other, out int id) &&
                            contacts.Contains((Math.Min(pair.Value, id), Math.Max(pair.Value, id))) && !graph[pair.Value].Contains(id))
                        {
                            graph[pair.Value].Add(id);
                        }
                    }
                }
                roots = SurveyMovers.VisibleColonists(map).Select(cell => regionAt[Index(cell)])
                    .Where(id => id >= 0).Distinct().ToArray();
                Task.Run(Analyze);
            }

            private void RecordContact(HashSet<(int left, int right)> contacts, int from, int to)
            {
                int left = regionAt[from], right = regionAt[to];
                if (right >= 0 && left != right)
                {
                    contacts.Add((Math.Min(left, right), Math.Max(left, right)));
                }
            }

            private int Index(IntVec3 cell) => cell.z * width + cell.x;

            private void Analyze()
            {
                try
                {
                    SizePatches(soil, soilSize);
                    SizePatches(building, buildSize);
                    reachable = new bool[graph.Length];
                    var pending = new Queue<int>(roots);
                    foreach (int root in roots) reachable[root] = true;
                    while (pending.Count > 0)
                    {
                        foreach (int next in graph[pending.Dequeue()])
                        {
                            if (reachable[next]) continue;
                            reachable[next] = true;
                            pending.Enqueue(next);
                        }
                    }
                    choke = ArticulationRegions();
                }
                catch (Exception exception)
                {
                    error = exception;
                }
                finally
                {
                    Ready.Set();
                }
            }

            private void SizePatches(bool[] eligible, int[] sizes)
            {
                var group = new List<int>();
                for (int root = 0; root < eligible.Length; root++)
                {
                    if (!eligible[root] || sizes[root] != 0) continue;
                    group.Clear();
                    Add(root);
                    for (int next = 0; next < group.Count; next++)
                    {
                        int cell = group[next];
                        if (cell % width > 0) Add(cell - 1);
                        if (cell % width < width - 1) Add(cell + 1);
                        if (cell / width > 0) Add(cell - width);
                        if (cell / width < height - 1) Add(cell + width);
                    }
                    foreach (int cell in group) sizes[cell] = group.Count;
                }

                void Add(int cell)
                {
                    if (!eligible[cell] || sizes[cell] != 0) return;
                    sizes[cell] = -1;
                    group.Add(cell);
                }
            }

            // Iterative depth-first traversal marks a region when removing it would split its
            // explored component. A region can be wider than one cell.
            private bool[] ArticulationRegions()
            {
                var cut = new bool[graph.Length];
                var enter = new int[graph.Length];
                var low = new int[graph.Length];
                var parent = Enumerable.Repeat(-1, graph.Length).ToArray();
                var edge = new int[graph.Length];
                var children = new int[graph.Length];
                var stack = new Stack<int>();
                int order = 0;
                for (int root = 0; root < graph.Length; root++)
                {
                    if (enter[root] != 0) continue;
                    enter[root] = low[root] = ++order;
                    stack.Push(root);
                    while (stack.Count > 0)
                    {
                        int node = stack.Peek();
                        if (edge[node] < graph[node].Count)
                        {
                            int next = graph[node][edge[node]++];
                            if (next == parent[node]) continue;
                            if (enter[next] == 0)
                            {
                                parent[next] = node;
                                children[node]++;
                                enter[next] = low[next] = ++order;
                                stack.Push(next);
                            }
                            else
                            {
                                low[node] = Math.Min(low[node], enter[next]);
                            }
                            continue;
                        }
                        stack.Pop();
                        int previous = parent[node];
                        if (previous < 0)
                        {
                            cut[node] = children[node] > 1;
                        }
                        else
                        {
                            low[previous] = Math.Min(low[previous], low[node]);
                            if (parent[previous] >= 0 && low[node] >= enter[previous]) cut[previous] = true;
                        }
                    }
                }
                return cut;
            }

            internal MapLayer Layer(string name)
            {
                if (error != null) throw new BridgeException(409, "Could not analyze the derived map snapshot: " + error.Message);
                switch (name.ToLowerInvariant())
                {
                    case "soilpatch":
                        return Flag("soilPatch", i => soilSize[i] > 0, "farmable patch",
                            "no farmable patch", i => soilSize[i]);
                    case "ore":
                        return Flag("ore", i => ores[i] != null, "exposed ore", "no exposed ore", i => ores[i]);
                    case "buildsite":
                        return Flag("buildSite", i => buildSize[i] > 0, "open heavy-support ground",
                            "no open heavy-support ground", i => buildSize[i]);
                    case "reach":
                        return Flag("reach", i => regionAt[i] >= 0 && reachable[regionAt[i]],
                            "connected to a visible colonist", "no explored connection");
                    default:
                        return Flag("chokepoints", i => regionAt[i] >= 0 && choke[regionAt[i]],
                            "cuts the explored region graph", "no graph cut");
                }
            }

            private MapLayer Flag(string name, Func<int, bool> test, string on, string off, Func<int, object> value = null)
            {
                return new MapLayer(name, (cell, _) => test(Index(cell)) ? '+' : '-', '-',
                    new Dictionary<char, string> { { '+', on }, { '-', off } },
                    (cell, _) => value != null ? value(Index(cell)) : test(Index(cell)));
            }
        }
    }
}
