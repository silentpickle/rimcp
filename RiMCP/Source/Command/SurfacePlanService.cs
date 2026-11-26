using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class SurfacePlanService
    {
        private enum Surface { Floor, SmoothFloor, SmoothWall, Roof }

        public static BridgeResponse PlaceFloor(BridgeRequest request, RouteMatch route) => Plan(request, Surface.Floor, "place_floor");
        public static BridgeResponse BuildRoof(BridgeRequest request, RouteMatch route) => Plan(request, Surface.Roof, "build_roof");
        public static BridgeResponse SmoothWalls(BridgeRequest request, RouteMatch route) => Plan(request, Surface.SmoothWall, "smooth_walls");

        private static BridgeResponse Plan(BridgeRequest request, Surface mode, string tool)
        {
            var body = CommandUtil.ReadBody<SurfaceBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            var context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            if (body.Sections != null && (mode != Surface.Floor || body.Def != null || body.Smooth || body.Rects != null))
                throw new CommandException(400, "Use sections for a mixed floor, without top-level def, smooth or rects.");
            SectionBody[] specs = body.Sections ?? new SectionBody[] { body };
            if (specs.Length == 0 || specs.Length > 50)
                throw new CommandException(400, "Give 1 to 50 floor sections.");
            var taken = new HashSet<IntVec3>();
            var plans = new List<Section>();
            var signature = new List<object>();
            int total = 0;
            foreach (var spec in specs)
            {
                if (spec?.Rects == null || spec.Rects.Length == 0 || spec.Rects.Any(r => r == null ||
                    !(r.X >= 0 && r.X <= 1000 && r.Z >= 0 && r.Z <= 1000)))
                    throw new CommandException(400, "Give rectangles with x and z between 0 and 1000.");
                List<IntVec3> cells = CommandCells.FromRects(spec.Rects, context.Map, "rects", out int outside);
                total += cells.Count + outside;
                if (total > CommandCells.MaxCells)
                    throw new CommandException(400, "Split plans larger than 3600 cells.");
                if (cells.Any(c => !taken.Add(c)))
                    throw new CommandException(400, "Floor sections overlap. Choose one finish per cell.");
                var section = new Section(mode, spec, cells, outside);
                plans.Add(section);
                signature.Add(section.Kind);
                signature.Add(spec.Def);
                signature.Add(spec.Rects);
            }
            if (body.Preview)
                return CommandEnvelope.Ok(context, false, Designate(context.Map, plans, true).Data);
            var key = RequestKey.For(tool, requestId, context.Map, signature.ToArray());
            return CommandReceipts.Once(context, key, () => Designate(context.Map, plans, false));
        }

        private sealed class Section
        {
            public readonly Surface Kind;
            public readonly List<IntVec3> Cells;
            public readonly int Outside;
            public readonly Designator Tool;
            public readonly TerrainDef Floor;
            private readonly HashSet<TerrainDef> smoothFinishes;

            public Section(Surface mode, SectionBody spec, List<IntVec3> cells, int outside)
            {
                Cells = cells;
                Outside = outside;
                if (spec.Smooth && (mode != Surface.Floor || spec.Def != null))
                    throw new CommandException(400, "Use smooth:true alone for natural stone floors.");
                Kind = spec.Smooth ? Surface.SmoothFloor : mode;
                if (Kind != Surface.Floor && spec.Def != null)
                    throw new CommandException(400, "Only constructed floors take a TerrainDef.");
                switch (Kind)
                {
                    case Surface.Floor:
                        Floor = string.IsNullOrWhiteSpace(spec.Def) ? null : DefDatabase<TerrainDef>.GetNamedSilentFail(spec.Def);
                        if (Floor == null || Floor.designationCategory != DesignationCategoryDefOf.Floors)
                            throw new CommandException(400, "Give a buildable Floors TerrainDef or smooth:true for natural stone.");
                        Tool = new Designator_Build(Floor);
                        break;
                    case Surface.SmoothFloor:
                        Tool = new Designator_SmoothFloors();
                        smoothFinishes = new HashSet<TerrainDef>(DefDatabase<TerrainDef>.AllDefs.Select(d => d.smoothedTerrain).Where(d => d != null));
                        break;
                    case Surface.SmoothWall:
                        Tool = new Designator_SmoothWalls();
                        break;
                    default:
                        Tool = new Designator_AreaBuildRoof();
                        break;
                }
            }

            public bool Satisfied(Map map, IntVec3 cell)
            {
                switch (Kind)
                {
                    case Surface.Floor:
                        return cell.GetTerrain(map) == Floor || cell.GetThingList(map).Any(t => t.def.entityDefToBuild == Floor);
                    case Surface.SmoothFloor:
                        return smoothFinishes.Contains(cell.GetTerrain(map)) || map.designationManager.DesignationAt(cell, DesignationDefOf.SmoothFloor) != null;
                    case Surface.SmoothWall:
                        return cell.GetEdifice(map)?.def.IsSmoothed == true || map.designationManager.DesignationAt(cell, DesignationDefOf.SmoothWall) != null;
                    default:
                        return map.areaManager.BuildRoof[cell] && !map.areaManager.NoRoof[cell];
                }
            }
        }

        private static CommandResult Designate(Map map, List<Section> plans, bool preview)
        {
            var outcomes = new CellOutcomes();
            var costs = new Dictionary<string, int>();
            int ready = 0;
            foreach (Section section in plans)
            {
                outcomes.Outside += section.Outside;
                int sectionReady = 0;
                foreach (var cell in section.Cells)
                {
                    if (cell.Fogged(map))
                    {
                        outcomes.Reject(cell, "Unexplored cell.");
                        continue;
                    }
                    if (section.Satisfied(map, cell))
                    {
                        outcomes.AlreadySatisfied++;
                        continue;
                    }
                    if (!section.Tool.Visible)
                    {
                        outcomes.Reject(cell, "The architect menu does not offer this order.");
                        continue;
                    }
                    if (section.Kind == Surface.Roof && !RoofCollapseUtility.WithinRangeOfRoofHolder(cell, map, true))
                    {
                        outcomes.Reject(cell, "No completed roof support in range. Finish walls or columns first.");
                        continue;
                    }
                    AcceptanceReport fit = section.Tool.CanDesignateCell(cell);
                    if (!fit.Accepted)
                    {
                        outcomes.Reject(cell, string.IsNullOrEmpty(fit.Reason) ? "Cannot designate this cell." : fit.Reason.StripTags());
                        continue;
                    }
                    if (preview)
                    {
                        sectionReady++;
                        continue;
                    }
                    section.Tool.DesignateSingleCell(cell);
                    if (section.Satisfied(map, cell)) outcomes.Applied++;
                    else outcomes.Reject(cell, "The game did not apply the designation.");
                }
                ready += sectionReady;
                if (sectionReady > 0 && section.Floor != null)
                {
                    foreach (var material in section.Floor.CostListAdjusted(null, false))
                    {
                        string name = material.thingDef.defName;
                        costs.TryGetValue(name, out int previous);
                        costs[name] = previous + sectionReady * material.count;
                    }
                }
            }
            var data = Dto.Obj(Dto.Field("ready", ready), Dto.Field("cells", outcomes.ToDto()));
            if (preview) data["materialEstimate"] = costs;
            return new CommandResult(outcomes.Applied > 0, data);
        }

        [DataContract]
        private class SectionBody
        {
            [DataMember(Name = "def")] public string Def { get; set; }
            [DataMember(Name = "smooth")] public bool Smooth { get; set; }
            [DataMember(Name = "rects")] public CellRectBody[] Rects { get; set; }
        }

        [DataContract]
        private sealed class SurfaceBody : SectionBody
        {
            [DataMember(Name = "mapId")] public string MapId { get; set; }
            [DataMember(Name = "sections")] public SectionBody[] Sections { get; set; }
            [DataMember(Name = "preview")] public bool Preview { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }
    }
}
