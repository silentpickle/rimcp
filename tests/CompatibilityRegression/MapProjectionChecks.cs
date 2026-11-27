using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

internal static partial class Program
{
    private const int TestTileScale = 2;
    private const int TestGridWidth = 5;
    private const int TestGridHeight = 3;
    private const int RockCells = 2;
    private const int GroundCells = 3;
    private const int FogCells = 2;

    private static readonly string[] TestMap = { "RR...", "Ab--?", "bA--?" };

    private static object SampleMap(bool countsOnly)
    {
        Func<IntVec3, char> sample = cell => TestMap[TestGridHeight - 1 - cell.z][cell.x];
        return Invoke("RiMCP.Read.MapLayerProjection", "Read", new CellRect(0, 0, TestGridWidth, TestGridHeight),
            TestTileScale, countsOnly, sample);
    }

    private static void CheckScaledMapTiles()
    {
        object grid = SampleMap(false);
        string[] rows = (string[])grid.GetType().GetProperty("Rows").GetValue(grid);
        Check(rows.SequenceEqual(new[] { "R..", "A-?" }), "partial edges, north-first rows, and ties must be stable");
    }

    private static void CheckMapSymbolCounts()
    {
        object grid = SampleMap(true);
        Check(grid.GetType().GetProperty("Rows").GetValue(grid) == null, "counts must omit grid rows");
        var counts = (Dictionary<char, int>)grid.GetType().GetField("Counts").GetValue(grid);
        Check(counts.Values.Sum() == TestGridWidth * TestGridHeight, "each cell must count once");
        Check(counts['R'] == RockCells && counts['.'] == GroundCells && counts['?'] == FogCells,
            "counts must preserve the original symbols including fog, rather than count scaled tiles");
    }
}
