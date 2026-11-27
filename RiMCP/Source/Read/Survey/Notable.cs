using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    internal static class SurveyNotable
    {
        private const int ClusterRadius = 6;
        private const string Ruin = "ruin";
        private const string Ore = "ore";
        private const string Feature = "feature";

        private sealed class Site
        {
            public Thing Thing;
            public string Kind;
            public ThingDef Def;
        }

        public static object Describe(Map map, IntVec3 colony, int limit)
        {
            var sites = map.listerThings.AllThings.Where(thing => Visible(thing, map))
                .Select(thing => Classify(thing, map)).Where(site => site != null)
                .OrderBy(site => site.Thing.thingIDNumber);
            var clusters = sites.GroupBy(site => new { site.Kind, Def = site.Kind == Ruin ? null : site.Def })
                .SelectMany(group => SurveyGeometry.Cluster(group, site => site.Thing.Position, ClusterRadius))
                .Select(group => Row(group, colony)).OrderBy(row => (int)row["distance"])
                .ThenByDescending(row => (int)row["count"]).ThenBy(row => (string)row["id"], StringComparer.Ordinal).ToArray();
            return Dto.Obj(Dto.Field("groups", clusters.Take(limit).ToArray()),
                Dto.Field("omittedGroups", clusters.Length > limit ? (int?)(clusters.Length - limit) : null));
        }

        internal static bool VisibleBuilding(Building building, Map map)
        {
            return Visible(building, map) && (!IsNaturalRock(building) || IsExposedOre(building, map));
        }

        private static bool Visible(Thing thing, Map map)
        {
            return thing.Spawned && !thing.Position.Fogged(map);
        }

        private static bool IsNaturalRock(Thing thing) => thing.def.building?.isNaturalRock == true;

        private static Site Classify(Thing thing, Map map)
        {
            if (thing.Faction == Faction.OfPlayer)
            {
                return null;
            }
            if (IsNaturalRock(thing))
            {
                return IsExposedOre(thing, map) ? Of(thing, Ore, thing.def.building.mineableThing) : null;
            }
            if (thing is Building)
            {
                bool ruin = thing.Faction == null && (thing.def.building?.isWall == true || thing is Building_Door);
                return Of(thing, ruin ? Ruin : Feature, thing.def);
            }
            return thing.def.category == ThingCategory.Item && thing.TryGetComp<CompUsable>() != null &&
                !map.areaManager.Home[thing.Position] && !thing.IsInValidStorage()
                ? Of(thing, Feature, thing.def) : null;
        }

        private static bool IsExposedOre(Thing thing, Map map)
        {
            if (thing.def.building?.mineableThing == null || !thing.def.building.isResourceRock)
            {
                return false;
            }
            foreach (IntVec3 direction in GenAdj.CardinalDirections)
            {
                IntVec3 neighbor = thing.Position + direction;
                if (neighbor.InBounds(map) && !neighbor.Fogged(map) && neighbor.GetEdifice(map)?.def.building?.isNaturalRock != true)
                {
                    return true;
                }
            }
            return false;
        }

        private static Site Of(Thing thing, string kind, ThingDef def)
        {
            return new Site { Thing = thing, Kind = kind, Def = def };
        }

        private static Dictionary<string, object> Row(List<Site> group, IntVec3 colony)
        {
            Site first = group.OrderBy(site => site.Thing.thingIDNumber).First();
            var positions = group.Select(site => site.Thing.Position).ToArray();
            IntVec3 center = SurveyGeometry.Centre(positions);
            string[] sources = group.Select(site => site.Thing.def.modContentPack?.PackageId)
                .Where(id => id != null).Distinct().OrderBy(id => id, StringComparer.Ordinal).ToArray();
            return Dto.Obj(
                Dto.Field("id", first.Kind + "-" + first.Thing.thingIDNumber),
                Dto.Field("kind", first.Kind),
                Dto.Field("def", first.Kind == Ruin ? null : first.Def.defName),
                Dto.Field("label", first.Kind == Ruin ? "Unclaimed walls and doors" : ReadUtil.DefLabel(first.Def)),
                Dto.Field("defs", first.Kind == Ruin ? group.GroupBy(site => site.Def.defName).ToDictionary(def => def.Key, def => def.Count()) : null),
                Dto.Field("sampleId", ReadUtil.ThingId(first.Thing)),
                Dto.Field("sampleDef", first.Kind == Ore ? first.Thing.def.defName : null),
                Dto.Field("count", group.Count),
                Dto.Field("bounds", SurveyGeometry.Bounds(positions)),
                Dto.Field("distance", SurveyGeometry.Distance(colony, center)),
                Dto.Field("bearing", SurveyGeometry.Bearing(colony, center)),
                Dto.Field("sourceMod", sources.Length == 1 ? sources[0] : null),
                Dto.Field("sourceMods", sources.Length > 1 ? sources : null));
        }
    }
}
