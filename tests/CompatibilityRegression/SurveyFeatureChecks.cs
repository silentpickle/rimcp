using System;
using System.Collections;
using System.Linq;
using Verse;

internal static partial class Program
{
    private const string SurveyGeometryType = "RiMCP.Read.SurveyGeometry";
    private const int NearbyDistance = 6;
    private const int Middle = NearbyDistance;
    private const int End = Middle + NearbyDistance;
    private const int Separate = End + NearbyDistance + 1;
    private const int ConnectedCount = 3;

    private static void CheckSurveyClusters()
    {
        Type type = mod.GetType(SurveyGeometryType, true);
        var cells = new[] { new IntVec3(0, 0, 0), new IntVec3(Middle, 0, 0),
            new IntVec3(End, 0, 0), new IntVec3(Separate, 0, 0) };
        Func<IntVec3, IntVec3> position = cell => cell;
        var sets = (IEnumerable)type.GetMethod("Cluster", Static).MakeGenericMethod(typeof(IntVec3))
            .Invoke(null, new object[] { cells, position, NearbyDistance });
        int[] counts = sets.Cast<IEnumerable>().Select(group => group.Cast<IntVec3>().Count()).ToArray();
        Check(counts.SequenceEqual(new[] { ConnectedCount, 1 }),
            "a chain at the exact cluster radius must join, and one cell beyond must stay separate");
    }

    private static void CheckSurveyHeading()
    {
        var origin = new IntVec3(0, 0, 0);
        var east = new IntVec3(Middle, 0, 0);
        var northeast = new IntVec3(Middle, 0, Middle);
        Check((string)Invoke(SurveyGeometryType, "Bearing", origin, northeast) == "NE", "north-east must use increasing x and z");
        Check((string)Invoke(SurveyGeometryType, "RelativeHeading", northeast, east, origin) == "toward",
            "a closer path destination is toward the colony");
        Check((string)Invoke(SurveyGeometryType, "RelativeHeading", east, northeast, origin) == "away",
            "a farther path destination is away from the colony");
    }
}
