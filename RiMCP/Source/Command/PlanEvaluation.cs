using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class PlanEvaluation
    {
        internal static object Verdict(int entry, IntVec3 cell, string outcome, string reason = null)
        {
            return Dto.Obj(Dto.Field("entryIndex", entry), Dto.Field("cell", ReadUtil.Cell(cell)),
                Dto.Field("outcome", outcome), Dto.Field("reason", reason));
        }

        internal static IEnumerable<Thing> Pending(Map map)
        {
            return map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)
                .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
                .Where(thing => thing.Faction == Faction.OfPlayer && thing.Spawned && !thing.Position.Fogged(map));
        }

        internal static object MaterialBudget(Map map, Dictionary<string, int> costs)
        {
            IConstructible[] queued = Pending(map).OfType<IConstructible>().ToArray();
            return costs.OrderBy(pair => pair.Key).Select(pair =>
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamed(pair.Key);
                int outstanding = queued.Sum(plan => plan.ThingCountNeeded(def));
                int stock = map.listerThings.ThingsOfDef(def).Where(thing =>
                    thing.Spawned && !thing.Position.Fogged(map) && !thing.IsForbidden(Faction.OfPlayer)).Sum(thing => thing.stackCount);
                return Dto.Obj(Dto.Field("def", pair.Key), Dto.Field("plan", pair.Value), Dto.Field("queued", outstanding),
                    Dto.Field("allowedOnMap", stock), Dto.Field("remaining", stock - outstanding - pair.Value));
            }).ToArray();
        }

        internal static bool HasQueuedRoofSupport(Map map, IntVec3 cell)
        {
            for (int i = 0; i < RoofCollapseUtility.RoofSupportRadialCellsCount; i++)
            {
                IntVec3 candidate = cell + GenRadial.RadialPattern[i];
                if (candidate.InBounds(map) && !candidate.Fogged(map) && candidate.GetThingList(map).Any(thing =>
                    thing.Faction == Faction.OfPlayer && thing.def.entityDefToBuild is ThingDef def && def.holdsRoof))
                {
                    return true;
                }
            }
            return false;
        }

        internal static object PreviewPen(Map map, IntVec3 marker, Dictionary<IntVec3, List<BuildPlacement>> planned)
        {
            const int maxCells = 3600;
            var pending = new Queue<IntVec3>();
            var seen = new HashSet<IntVec3> { marker };
            pending.Enqueue(marker);
            int count = 0, growing = 0, soil = 0;
            string boundary = null;
            var pasture = new MapPastureNutritionCalculator();
            pasture.Reset(map);
            float forage = 0;
            while (pending.Count > 0 && count < maxCells)
            {
                IntVec3 cell = pending.Dequeue();
                if (!cell.InBounds(map) || cell.OnEdge(map))
                {
                    boundary = "map edge";
                    continue;
                }
                if (cell.Fogged(map))
                {
                    boundary = boundary ?? "unexplored";
                    continue;
                }
                if (cell != marker && (!cell.WalkableByFenceBlocked(map) ||
                    cell.GetThingList(map).Any(thing => thing is Building_Door door
                        ? !AnimalPenEnclosureCalculator.RoamerCanPass(door)
                        : BlocksPen(thing.def.entityDefToBuild as ThingDef ?? thing.def)) ||
                    planned.TryGetValue(cell, out List<BuildPlacement> plans) && plans.Any(plan => BlocksPen(plan.Def))))
                {
                    continue;
                }
                count++;
                if (map.fertilityGrid.FertilityAt(cell) >= GrowingSummary.ZoneMinFertility()) soil++;
                if (cell.GetRoof(map) == null) forage += pasture.GetAverageNutritionPerDayToday(cell.GetTerrain(map));
                if (map.zoneManager.ZoneAt(cell) is Zone_Growing) growing++;
                foreach (IntVec3 direction in GenAdj.CardinalDirections)
                {
                    IntVec3 next = cell + direction;
                    if (seen.Add(next)) pending.Enqueue(next);
                }
            }
            if (pending.Count > 0) boundary = boundary ?? "cell limit";
            return Dto.Obj(Dto.Field("anchor", ReadUtil.Cell(marker)), Dto.Field("enclosed", boundary == null ? (bool?)true :
                    boundary == "map edge" ? false : (bool?)null),
                Dto.Field("boundary", boundary), Dto.Field("cellsVisited", count),
                Dto.Field("forageNutritionPerDayToday", boundary == null ? (double?)System.Math.Round(forage, 2) : null),
                Dto.Field("farmableCells", soil), Dto.Field("growingZoneCells", growing));
        }

        private static bool BlocksPen(ThingDef def) => def != null &&
            (def.IsFence || def.IsDoor && !AnimalPenEnclosureCalculator.RoamerCanPass(def) || def.passability == Traversability.Impassable);
    }
}
