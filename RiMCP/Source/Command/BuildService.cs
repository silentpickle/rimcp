using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // Buildings: checking where one fits, which changes nothing, and placing blueprints for
    // colonists to build. Both use the game's build tool through BuildPlacement, which works on the
    // map shown in game.
    internal static partial class BuildService
    {
        private const int MaxPlacements = 50;
        private const int MaxListedFits = 50;
        private const int MaxListedPerReason = 10;

        public static BridgeResponse CheckPlacement(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            ReadRequest query = context.Request;
            Map map = context.Map;
            if (map != Find.CurrentMap)
            {
                throw new BridgeException(409, "check_placement uses the game's build tool, which works on the map shown in game (map " + Find.CurrentMap.uniqueID + "). Switch to map " + map.uniqueID + " in game first.");
            }
            BuildPlacement building = BuildPlacement.Resolve(query.Get("def"), query.Get("stuff"), query.Get("rotation"), "");
            CellRect rect = ReadArea.Parse(context, LegacyArea.Placement).Rectangle.Value;

            // Positions nearest the middle of the area come first, so the first fit is the most central.
            IntVec3 centre = new IntVec3(rect.minX + rect.Width / 2, 0, rect.minZ + rect.Height / 2);
            List<IntVec3> positions = new List<IntVec3>();
            int outside = 0;
            for (int dx = 0; dx < rect.Width; dx++)
            {
                for (int dz = 0; dz < rect.Height; dz++)
                {
                    IntVec3 cell = new IntVec3(rect.minX + dx, 0, rect.minZ + dz);
                    if (cell.InBounds(map))
                    {
                        positions.Add(cell);
                    }
                    else
                    {
                        outside++;
                    }
                }
            }
            positions = positions.OrderBy(cell => cell.DistanceToSquared(centre)).ThenBy(cell => cell.x).ThenBy(cell => cell.z).ToList();

            string unavailable = building.Unavailable(map);
            List<IntVec3> fits = new List<IntVec3>();
            Dictionary<string, List<IntVec3>> rejected = new Dictionary<string, List<IntVec3>>();
            if (unavailable == null)
            {
                foreach (IntVec3 cell in positions)
                {
                    string reason = building.CannotPlaceAt(cell);
                    if (reason == null)
                    {
                        fits.Add(cell);
                        continue;
                    }
                    List<IntVec3> cells;
                    if (!rejected.TryGetValue(reason, out cells))
                    {
                        rejected[reason] = cells = new List<IntVec3>();
                    }
                    cells.Add(cell);
                }
            }

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("building", building.Describe()),
                Dto.Field("available", unavailable == null),
                Dto.Field("reason", unavailable),
                Dto.Field("fitsCount", fits.Count),
                Dto.Field("fits", fits.Take(MaxListedFits).Select(Position).ToArray()),
                Dto.Field("rejected", rejected
                    .OrderByDescending(pair => pair.Value.Count)
                    .Select(pair => (object)Dto.Obj(
                        Dto.Field("reason", pair.Key),
                        Dto.Field("count", pair.Value.Count),
                        Dto.Field("positions", pair.Value.Take(MaxListedPerReason).Select(Position).ToArray())))
                    .ToArray()),
                Dto.Field("outsideMap", outside > 0 ? (object)outside : null),
                Dto.Field("blockers", unavailable == null ? Blockers.ToArray(BuildPlacement.BuildBlockers(map, new[] { building })) : null)));
        }

        // Each placement is checked and placed in order, so a later one cannot overlap an earlier
        // one. A placement that is already there, as a blueprint, frame or building, is satisfied.
        public static BridgeResponse PlaceBlueprint(BridgeRequest request, RouteMatch route)
        {
            PlaceBlueprintBody body = CommandUtil.ReadBody<PlaceBlueprintBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            if (body.Placements == null || body.Placements.Length == 0)
            {
                throw new CommandException(400, "Missing required field 'placements'.");
            }
            if (body.Placements.Length > MaxPlacements)
            {
                throw new CommandException(400, "At most " + MaxPlacements + " placements per call.");
            }
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            Map map = context.Map;

            bool compact = body.Placements.Any(p => p != null && p.Shape != null);
            var rooms = body.Placements.Where(p => p != null && p.Shape == "perimeter").ToArray();
            body.Placements = Expand(body.Placements);
            // Every placement is read before any is placed, so a malformed one changes nothing.
            BuildPlacement[] buildings = new BuildPlacement[body.Placements.Length];
            foreach (var group in body.Placements.Select((p, index) => new { p, index })
                .GroupBy(item => new { item.p.Def, item.p.Stuff, item.p.Rotation }))
            {
                var first = group.First();
                var resolved = BuildPlacement.Resolve(group.Key.Def, group.Key.Stuff, group.Key.Rotation, "placements[" + first.index + "].");
                foreach (var item in group) buildings[item.index] = resolved;
            }
            var plan = new BuildingPlan(buildings, body.Placements);
            if (body.Preview)
                return CommandEnvelope.Ok(context, false, plan.Preview(map, rooms));
            // The material counts only when given: the game's default depends on what the colony has.
            RequestKey key = RequestKey.For("place_blueprint", requestId, map, (compact ? "shape:" : "") + string.Join(";", buildings.Select((building, i) =>
                building.Def.defName + "," + (string.IsNullOrWhiteSpace(body.Placements[i].Stuff) ? "" : building.Stuff.defName) + "," + BuildPlacement.RotationName(building.Rotation) + "," + body.Placements[i].X + "," + body.Placements[i].Z).ToArray()));
            return CommandReceipts.Once(context, key, () => Place(buildings, body.Placements, map, plan, compact, rooms));
        }

        private static CommandResult Place(BuildPlacement[] buildings, PlacementBody[] placements, Map map, BuildingPlan plan, bool compact, PlacementBody[] rooms)
        {
            List<object> results = new List<object>();
            List<BuildPlacement> placed = new List<BuildPlacement>();
            int applied = 0, satisfied = 0, rejectedCount = 0;
            List<Thing> placedThings = new List<Thing>();
            List<ThingDef> placedKinds = new List<ThingDef>();
            List<string> refusals = new List<string>();
            List<ThingDef> kinds = buildings.Select(building => building.Def).Distinct().ToList();
            string asked = "Place " + (kinds.Count == 1 ? kinds[0].label : "buildings");
            AgentLog.About(asked);
            for (int i = 0; i < buildings.Length; i++)
            {
                BuildPlacement building = buildings[i];
                IntVec3 cell = new IntVec3(placements[i].X.Value, 0, placements[i].Z.Value);
                string outcome;
                string reason = null;
                Thing thing = null;
                if (!cell.InBounds(map))
                {
                    outcome = "rejected";
                    reason = "Outside the map.";
                }
                else if (!cell.Fogged(map) && (thing = building.Existing(cell, map)) != null)
                {
                    outcome = "alreadySatisfied";
                }
                else if ((reason = building.Unavailable(map) ?? building.CannotPlaceAt(cell) ?? plan.DoorBlocker(i, map)) != null)
                {
                    outcome = "rejected";
                }
                else if ((thing = building.Place(cell, map)) != null)
                {
                    outcome = "applied";
                    // In god mode the build tool builds at once, and nothing blocks a finished building.
                    if (BuildPlacement.StateOf(thing) != "built")
                    {
                        placed.Add(building);
                    }
                }
                else
                {
                    outcome = "rejected";
                    reason = "The game did not place it.";
                }
                if (outcome == "applied")
                {
                    placedThings.Add(thing);
                    placedKinds.Add(building.Def);
                }
                else if (outcome == "rejected" && reason != null && refusals.Count < 3 && !refusals.Contains(reason))
                {
                    refusals.Add(reason);
                }
                applied += outcome == "applied" ? 1 : 0;
                satisfied += outcome == "alreadySatisfied" ? 1 : 0;
                rejectedCount += outcome == "rejected" ? 1 : 0;
                if (compact && outcome != "rejected")
                    continue;
                results.Add(Dto.Obj(
                    Dto.Field("def", building.Def.defName),
                    Dto.Field("stuff", building.Stuff == null ? null : building.Stuff.defName),
                    Dto.Field("x", cell.x),
                    Dto.Field("z", cell.z),
                    Dto.Field("rotation", BuildPlacement.RotationName(building.Rotation)),
                    Dto.Field("outcome", outcome),
                    Dto.Field("reason", reason),
                    Dto.Field("id", thing == null ? null : ReadUtil.ThingId(thing)),
                    Dto.Field("state", thing == null ? null : BuildPlacement.StateOf(thing)),
                    Dto.Field("occupies", outcome == "rejected" ? null : building.Occupies(cell))));
            }

            bool built = placedThings.Count > 0 && placedThings.All(thing => BuildPlacement.StateOf(thing) == "built");
            string made = kinds.Count == 1
                ? AgentLog.Count(applied, kinds[0].label + (built ? "" : " blueprint"))
                : AgentLog.Count(applied, built ? "building" : "blueprint") + (applied > 0 ? " (" + Kinds(placedKinds) + ")" : "");
            AgentLog.Counted((built ? "Built " : "Placed ") + made, asked, applied, satisfied, rejectedCount, string.Join("; ", refusals.ToArray()), placedThings);
            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("applied", applied),
                Dto.Field("alreadySatisfied", satisfied),
                Dto.Field("rejectedCount", rejectedCount),
                Dto.Field("results", results.ToArray()),
                Dto.Field("blockers", Blockers.ToArray(BuildPlacement.BuildBlockers(map, placed))));
            if (rooms.Length > 0)
                data["roofSupport"] = plan.SubmittedRoofCoverage(map, rooms);
            return new CommandResult(applied > 0, data);
        }

        // "3 beds, 1 door"
        private static string Kinds(List<ThingDef> defs)
        {
            return string.Join(", ", defs.GroupBy(def => def).OrderByDescending(group => group.Count())
                .Select(group => AgentLog.Count(group.Count(), group.Key.label, Find.ActiveLanguageWorker.Pluralize(group.Key.label, group.Count()))).ToArray());
        }

        private static object Position(IntVec3 cell)
        {
            return Dto.Obj(Dto.Field("x", cell.x), Dto.Field("z", cell.z));
        }

        [DataContract]
        private sealed class PlaceBlueprintBody
        {
            [DataMember(Name = "preview")] public bool Preview { get; set; }

            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "placements")]
            public PlacementBody[] Placements { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class SideBody
        {
            [DataMember(Name = "edge")] public string Edge { get; set; }
            [DataMember(Name = "def")] public string Def { get; set; }
            [DataMember(Name = "stuff")] public string Stuff { get; set; }
        }

        [DataContract]
        private sealed class PlacementBody
        {
            [DataMember(Name = "shape")] public string Shape { get; set; }
            [DataMember(Name = "width")] public int? Width { get; set; }
            [DataMember(Name = "height")] public int? Height { get; set; }
            [DataMember(Name = "openings")] public CellRectBody[] Openings { get; set; }
            [DataMember(Name = "sides")] public SideBody[] Sides { get; set; }
            [DataMember(Name = "columns")] public bool Columns { get; set; }

            [DataMember(Name = "def")]
            public string Def { get; set; }

            [DataMember(Name = "stuff")]
            public string Stuff { get; set; }

            [DataMember(Name = "x")]
            public int? X { get; set; }

            [DataMember(Name = "z")]
            public int? Z { get; set; }

            [DataMember(Name = "rotation")]
            public string Rotation { get; set; }
        }
    }
}
