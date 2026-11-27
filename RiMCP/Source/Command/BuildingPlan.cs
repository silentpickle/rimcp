using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static partial class BuildService
    {
        private const int MaxExpandedPlacements = 256;
        private const int MinDoorApproaches = 2;
        // Walls hold the roof up to RoofCollapseUtility's support radius (6.9 cells), so an interior
        // no deeper than this on either axis is fully supported without columns.
        private const int MaxInteriorWithoutColumns = 12;

        private static PlacementBody[] Expand(PlacementBody[] entries)
        {
            var cells = new List<PlacementBody>();
            for (int index = 0; index < entries.Length; index++)
            {
                PlacementBody entry = entries[index];
                if (entry == null || !entry.X.HasValue || !entry.Z.HasValue)
                    throw new CommandException(400, "placements[" + index + "] needs x and z.");
                entry.EntryIndex = index;
                if (entry.Shape == null)
                {
                    if (entry.Width.HasValue || entry.Height.HasValue || entry.Openings != null || entry.Sides != null || entry.Columns)
                        throw new CommandException(400, "Dimensions, openings and columns require a shape.");
                    cells.Add(entry);
                }
                else
                {
                    try
                    {
                        ExpandShape(entry, cells);
                    }
                    catch (CommandException exception)
                    {
                        throw new CommandException(400, "placements[" + index + "]: " + exception.Message);
                    }
                }
                if (cells.Count > MaxExpandedPlacements)
                    throw new CommandException(400, "Split plans larger than 256 expanded placements.");
            }
            if (cells.Count == 0)
                throw new CommandException(400, "The plan leaves no placements.");
            return cells.ToArray();
        }

        private static void ExpandShape(PlacementBody entry, List<PlacementBody> cells)
        {
            int x = entry.X.Value, z = entry.Z.Value;
            int width = entry.Width ?? 1, height = entry.Height ?? 1;
            if (x < 0 || x > 1000 || z < 0 || z > 1000 || width < 1 || width > 60 || height < 1 || height > 60)
                throw new CommandException(400, "Shape origin must be 0–1000 and dimensions 1–60.");
            bool shell = entry.Shape == "perimeter";
            if (shell ? width < 3 || height < 3 :
                entry.Shape != "line" || (width > 1 && height > 1) || entry.Openings != null || entry.Sides != null || entry.Columns)
                throw new CommandException(400, "A perimeter needs at least 3x3. A line needs width or height 1 and cannot have openings or columns.");
            var bounds = new CellRect(x, z, width, height);
            var holes = new HashSet<IntVec3>();
            foreach (var opening in entry.Openings ?? new CellRectBody[0])
            {
                if (opening == null || !opening.X.HasValue || !opening.Z.HasValue || opening.Width.HasValue || opening.Height.HasValue)
                    throw new CommandException(400, "Each opening needs one absolute x and z.");
                var cell = new IntVec3(opening.X.Value, 0, opening.Z.Value);
                if (!bounds.Contains(cell) || !bounds.IsOnEdge(cell) || !holes.Add(cell))
                    throw new CommandException(400, "Openings must be unique cells on the perimeter.");
            }
            var sides = new Dictionary<string, SideBody>();
            foreach (SideBody side in entry.Sides ?? new SideBody[0])
            {
                if (side == null || !new[] { "north", "south", "east", "west" }.Contains(side.Edge)
                    || sides.ContainsKey(side.Edge) || (string.IsNullOrWhiteSpace(side.Def) && string.IsNullOrWhiteSpace(side.Stuff)))
                    throw new CommandException(400, "Each side needs a unique compass edge and a def or stuff override.");
                sides.Add(side.Edge, side);
            }
            foreach (var cell in bounds)
            {
                if ((shell && !bounds.IsOnEdge(cell)) || holes.Contains(cell)) continue;
                SideBody side = null;
                if (cell.z == bounds.maxZ) sides.TryGetValue("north", out side);
                if (cell.z == bounds.minZ) sides.TryGetValue("south", out side);
                if (side == null && cell.x == bounds.maxX) sides.TryGetValue("east", out side);
                if (side == null && cell.x == bounds.minX) sides.TryGetValue("west", out side);
                bool changedDef = side?.Def != null && side.Def != entry.Def;
                cells.Add(new PlacementBody { EntryIndex = entry.EntryIndex, Def = side?.Def ?? entry.Def,
                    Stuff = side?.Stuff ?? (changedDef ? null : entry.Stuff), Rotation = changedDef ? null : entry.Rotation, X = cell.x, Z = cell.z });
            }
            if (entry.Columns)
            {
                if (entry.Def != "Wall")
                    throw new CommandException(400, "Automatic columns require a Wall perimeter.");
                if (width - 2 <= MaxInteriorWithoutColumns || height - 2 <= MaxInteriorWithoutColumns)
                    return;
                // Cover the entire interior independently of missing perimeter segments. Each
                // tile is at most 9x9, with its furthest corner inside the game's support radius.
                int nx = (width - 2 + 8) / 9, nz = (height - 2 + 8) / 9;
                for (int ix = 0; ix < nx; ix++)
                    for (int iz = 0; iz < nz; iz++)
                        cells.Add(new PlacementBody { EntryIndex = entry.EntryIndex, Def = "Column", Stuff = entry.Stuff,
                            X = x + 1 + (2 * ix + 1) * (width - 2) / (2 * nx),
                            Z = z + 1 + (2 * iz + 1) * (height - 2) / (2 * nz) });
            }
        }

        private sealed class BuildingPlan
        {
            private readonly BuildPlacement[] buildings;
            private readonly PlacementBody[] entries;
            private readonly IntVec3[] anchors;
            private readonly CellRect[] footprints;
            private readonly HashSet<IntVec3> impassable = new HashSet<IntVec3>();

            public BuildingPlan(BuildPlacement[] buildings, PlacementBody[] entries)
            {
                this.buildings = buildings;
                this.entries = entries;
                anchors = entries.Select(p => new IntVec3(p.X.Value, 0, p.Z.Value)).ToArray();
                footprints = buildings.Select((b, i) => GenAdj.OccupiedRect(anchors[i], b.Rotation, b.Def.Size)).ToArray();
                for (int i = 0; i < buildings.Length; i++)
                    if (BlocksApproach(buildings[i].Def))
                        impassable.UnionWith(footprints[i]);
            }

            private static bool BlocksApproach(ThingDef def)
            {
                return def != null && !def.IsDoor && def.passability == Traversability.Impassable;
            }

            public string DoorBlocker(int index, Map map)
            {
                if (!buildings[index].Def.IsDoor || buildings[index].Def.Size != IntVec2.One)
                    return null;
                var blocked = new HashSet<IntVec3>();
                foreach (var direction in GenAdj.CardinalDirections)
                {
                    var cell = anchors[index] + direction;
                    if (cell.InBounds(map) && !cell.Fogged(map) && cell.Walkable(map) && !impassable.Contains(cell)
                        && !cell.GetThingList(map).Any(t => BlocksApproach(t.def.entityDefToBuild as ThingDef)))
                        continue;
                    blocked.Add(cell);
                }
                // The game accepts doors at bends, tunnel mouths and fog edges, so only a door this
                // request itself leaves without a way in and a way out is refused.
                bool planBlocks = blocked.Any(impassable.Contains);
                bool twoApproaches = GenAdj.CardinalDirections.Length - blocked.Count >= MinDoorApproaches;
                return twoApproaches || !planBlocks ? null : "This plan leaves the door fewer than two walkable approaches. Blocked or unavailable: " + string.Join(" / ", blocked.Select(CellLabel).ToArray()) + ".";
            }

            private static string CellLabel(IntVec3 cell) => cell.x + "," + cell.z;

            // Which side each planned cooler will cool, so a backwards cooler is caught before it
            // is built. Null when the plan has no cooler.
            private object[] CoolerSides()
            {
                var coolers = new List<object>();
                for (int i = 0; i < buildings.Length; i++)
                {
                    if (!typeof(Building_Cooler).IsAssignableFrom(buildings[i].Def.thingClass))
                    {
                        continue;
                    }
                    coolers.Add(Dto.Obj(
                        Dto.Field("x", anchors[i].x),
                        Dto.Field("z", anchors[i].z),
                        Dto.Field("rotation", BuildPlacement.RotationName(buildings[i].Rotation)),
                        Dto.Field("cooledCell", Position(TemperatureControlRead.CooledCell(anchors[i], buildings[i].Rotation))),
                        Dto.Field("exhaustCell", Position(TemperatureControlRead.ExhaustCell(anchors[i], buildings[i].Rotation)))));
                }
                return coolers.Count == 0 ? null : coolers.ToArray();
            }

            // Radial coverage after construction. The game also needs a connected roof and
            // finished supports. Blueprints from unrelated projects are never assumed complete.
            private static object RoofCoverage(PlacementBody room, HashSet<IntVec3> supports, Map map)
            {
                var interior = new CellRect(room.X.Value + 1, room.Z.Value + 1, room.Width.Value - 2, room.Height.Value - 2);
                var missing = new List<IntVec3>();
                foreach (IntVec3 cell in interior)
                {
                    bool covered = false;
                    for (int i = 0; i < RoofCollapseUtility.RoofSupportRadialCellsCount; i++)
                    {
                        var near = cell + GenRadial.RadialPattern[i];
                        if (supports.Contains(near) || (near.InBounds(map) && !near.Fogged(map) && near.GetEdifice(map)?.def.holdsRoof == true))
                        {
                            covered = true;
                            break;
                        }
                    }
                    if (!covered) missing.Add(cell);
                }
                return Dto.Obj(Dto.Field("x", interior.minX), Dto.Field("z", interior.minZ),
                    Dto.Field("interiorWidth", interior.Width), Dto.Field("interiorHeight", interior.Height),
                    Dto.Field("outsideSupportRadius", missing.Count), Dto.Field("examples", missing.Take(8).Select(Position).ToArray()),
                    Dto.Field("note", "Projected after construction. Finish supports before roofing; roof connectivity and build order still matter."));
            }

            public object SubmittedRoofCoverage(Map map, PlacementBody[] rooms)
            {
                var supports = new HashSet<IntVec3>();
                for (int i = 0; i < buildings.Length; i++)
                {
                    if (buildings[i].Def.holdsRoof && anchors[i].InBounds(map) && !anchors[i].Fogged(map)
                        && buildings[i].Existing(anchors[i], map) != null)
                        supports.UnionWith(footprints[i]);
                }
                return rooms.Select(room => RoofCoverage(room, supports, map)).ToArray();
            }

            public object Preview(Map map, PlacementBody[] rooms, bool includeVerdicts, bool includeEffects)
            {
                var costs = new Dictionary<string, int>();
                var rejected = new List<object>();
                var warnings = new List<object>();
                var verdicts = includeVerdicts ? new List<object>() : null;
                var penMarkers = new List<IntVec3>();
                var accepted = new Dictionary<IntVec3, List<BuildPlacement>>();
                var supports = new HashSet<IntVec3>();
                var plannedAnchors = new HashSet<System.Tuple<IntVec3, ThingDef, ThingDef, Rot4>>();
                int ready = 0, satisfied = 0;
                for (int i = 0; i < buildings.Length; i++)
                {
                    BuildPlacement b = buildings[i];
                    IntVec3 cell = anchors[i];
                    var identity = Tuple.Create(cell, b.Def, b.Stuff, b.Rotation);
                    string reason = null;
                    Building wall = cell.InBounds(map) && !cell.Fogged(map) ? cell.GetEdifice(map) : null;
                    if (b.Def.building?.isWall == true && wall?.def.building?.isWall == true && wall.Stuff != b.Stuff)
                    {
                        warnings.Add(PlanEvaluation.Verdict(entries[i].EntryIndex, cell, "differentWallMaterial",
                            "Existing " + wall.LabelNoCount + " uses another material."));
                    }
                    if (!cell.InBounds(map))
                        reason = "Outside the map.";
                    else if (plannedAnchors.Contains(identity) || (!cell.Fogged(map) && b.Existing(cell, map) != null))
                    {
                        if (b.Def.holdsRoof) supports.UnionWith(footprints[i]);
                        verdicts?.Add(PlanEvaluation.Verdict(entries[i].EntryIndex, cell, "alreadySatisfied"));
                        if (b.Def.HasComp(typeof(CompAnimalPenMarker))) penMarkers.Add(cell);
                        satisfied++;
                        continue;
                    }
                    else
                        reason = b.Unavailable(map) ?? b.CannotPlaceAt(cell) ?? DoorBlocker(i, map);
                    if (reason == null && footprints[i].Any(c => accepted.ContainsKey(c) && accepted[c].Any(
                        other => !GenConstruct.CanPlaceBlueprintOver(b.Def, other.Def.blueprintDef ?? other.Def, b.Stuff, other.Stuff))))
                        reason = "Overlaps an earlier placement in this request.";
                    if (reason != null)
                    {
                        rejected.Add(Dto.Obj(Dto.Field("entryIndex", entries[i].EntryIndex), Dto.Field("def", b.Def.defName), Dto.Field("x", cell.x), Dto.Field("z", cell.z), Dto.Field("reason", reason)));
                        verdicts?.Add(PlanEvaluation.Verdict(entries[i].EntryIndex, cell, "rejected", reason));
                        continue;
                    }
                    verdicts?.Add(PlanEvaluation.Verdict(entries[i].EntryIndex, cell, "ready"));
                    if (b.Def.HasComp(typeof(CompAnimalPenMarker))) penMarkers.Add(cell);
                    foreach (var c in footprints[i])
                    {
                        if (!accepted.ContainsKey(c)) accepted[c] = new List<BuildPlacement>();
                        accepted[c].Add(b);
                    }
                    if (b.Def.holdsRoof) supports.UnionWith(footprints[i]);
                    plannedAnchors.Add(identity);
                    ready++;
                    foreach (var material in b.Cost())
                    {
                        string name = material.thingDef.defName;
                        costs.TryGetValue(name, out int count);
                        costs[name] = count + material.count;
                    }
                }
                int[] effectIndices = includeEffects ? Enumerable.Range(0, buildings.Length).Where(index =>
                    anchors[index].InBounds(map) && !anchors[index].Fogged(map) && EffectCapture.Supports(buildings[index].Def)).ToArray() : null;
                return Dto.Obj(Dto.Field("ready", ready), Dto.Field("alreadySatisfied", satisfied), Dto.Field("rejectedCount", rejected.Count),
                    Dto.Field("results", rejected.ToArray()), Dto.Field("materialEstimate", costs),
                    Dto.Field("materialBudget", PlanEvaluation.MaterialBudget(map, costs)),
                    Dto.Field("verdicts", verdicts?.ToArray()),
                    Dto.Field("effects", effectIndices?.Take(4).Select(index => Dto.Obj(
                        Dto.Field("entryIndex", entries[index].EntryIndex), Dto.Field("anchor", ReadUtil.Cell(anchors[index])),
                        Dto.Field("groups", EffectCapture.Describe(EffectCapture.Placement(map, buildings[index].Def,
                            anchors[index], buildings[index].Rotation))))).ToArray()),
                    Dto.Field("omittedEffectPlacements", effectIndices?.Length > 4 ? (int?)(effectIndices.Length - 4) : null),
                    Dto.Field("warnings", warnings.Count == 0 ? null : warnings.ToArray()),
                    Dto.Field("pens", penMarkers.Count == 0 ? null : penMarkers.Distinct().Take(4)
                        .Select(marker => PlanEvaluation.PreviewPen(map, marker, accepted)).ToArray()),
                    Dto.Field("roofSupport", rooms.Select(room => RoofCoverage(room, supports, map)).ToArray()),
                    Dto.Field("columns", anchors.Where((c, i) => buildings[i].Def.defName == "Column").Select(Position).ToArray()),
                    Dto.Field("coolers", CoolerSides()),
                    Dto.Field("footprint", Dto.Obj(Dto.Field("minX", footprints.Min(r => r.minX)), Dto.Field("minZ", footprints.Min(r => r.minZ)),
                        Dto.Field("maxX", footprints.Max(r => r.maxX)), Dto.Field("maxZ", footprints.Max(r => r.maxZ)))));
            }
        }
    }
}
