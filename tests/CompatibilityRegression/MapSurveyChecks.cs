using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

internal static partial class Program
{
    private const int TileTestMapWidth = 65;
    private const int TileTestMapHeight = 34;
    private const int TileTestWest = 64;
    private const int TileTestSouth = 32;
    private const int TileTestHeight = 2;
    private const int RegionWidth = 8;
    private const int RegionHeight = 6;
    private const int RegionLimit = 2;
    private const int RockRegionSize = 8;
    private const int QuarterCoverage = 4;
    private const int TileCells = 16;
    private const int StrongGround = TileCells - 1;

    private static void CheckSurveyTileEdges()
    {
        var size = new IntVec3(TileTestMapWidth, 0, TileTestMapHeight);
        const string type = "RiMCP.Read.SurveyTiles";
        CellRect tile = (CellRect)Invoke(type, "ResolveName", "x2z1", size);
        Check(tile.minX == TileTestWest && tile.minZ == TileTestSouth && tile.Width == 1 && tile.Height == TileTestHeight,
            "edge tile must crop in place, preserving its stable origin");
        ExpectBridgeError(404, () => Invoke(type, "ResolveName", "x3z1", size));
        ExpectBridgeError(400, () => Invoke(type, "ResolveName", "x-1z1", size));
    }

    private static void CheckSurveyPriority()
    {
        const string type = "RiMCP.Read.SurveySymbols";
        var counts = new Dictionary<char, int> { { '.', StrongGround }, { '#', 1 } };
        Check((char)Invoke(type, "Priority", counts, TileCells, '.') == '#', "one structure must beat the ground majority");
        counts['H'] = 1;
        Check((char)Invoke(type, "Priority", counts, TileCells, '.') == 'H', "hostile must outrank structure");
        counts['C'] = 1;
        Check((char)Invoke(type, "Priority", counts, TileCells, '.') == 'C', "colonist must outrank hostile");
        counts = new Dictionary<char, int> { { '.', TileCells - QuarterCoverage }, { '~', QuarterCoverage } };
        Check((char)Invoke(type, "Priority", counts, TileCells, '.') == '~', "one quarter water must beat ground");
        counts['~']--;
        counts['.']++;
        Check((char)Invoke(type, "Priority", counts, TileCells, '.') == '.', "water below the threshold falls back to the majority");
    }

    private static void CheckSurveyRegions()
    {
        string[] rows = { "........", "..^^^...", "..^?^...", "..^^^...", ".......~", "......~." };
        char[] cells = string.Concat(rows).ToCharArray();
        Dictionary<char, int> counts = cells.GroupBy(cell => cell).ToDictionary(group => group.Key, group => group.Count());
        var data = (Dictionary<string, object>)Invoke("RiMCP.Read.SurveyRegions", "Describe", cells,
            new IntVec3(RegionWidth, 0, RegionHeight), counts, RegionLimit);
        var background = (Dictionary<string, object>)data["background"];
        var regions = (Dictionary<string, object[]>)data["regions"];
        Check((string)background["class"] == "soil" && !regions.ContainsKey("soil"), "the visible majority belongs in background");
        var rock = (Dictionary<string, object>)regions["rock"].Single();
        Check((int)rock["cells"] == RockRegionSize, "the rock ring must be one connected region");
        var fog = (Dictionary<string, object>)regions["unexplored"].Single();
        Check((bool)fog["likelyMountainInterior"], "fog enclosed by observed rock must be marked as likely mountain");
        Check(regions["water"].Length == RegionLimit && regions["water"].All(row => (int)((Dictionary<string, object>)row)["cells"] == 1),
            "diagonally touching water must remain in separate regions");
    }
}
