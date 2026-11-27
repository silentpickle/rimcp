using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    [DataContract]
    internal sealed class AdvanceWatchBody
    {
        [DataMember(Name = "mapId")] public string MapId { get; set; }
        [DataMember(Name = "blueprintIds")] public string[] BlueprintIds { get; set; }
        [DataMember(Name = "rect")] public CellRectBody Rect { get; set; }
    }

    internal sealed class AdvanceWatch
    {
        private readonly int mapId;
        private readonly HashSet<IntVec3> cells;
        private readonly HashSet<string> ids;

        public AdvanceWatch(AdvanceWatchBody body)
        {
            Map map = GameContext.ResolveMap(body.MapId);
            if (map == null)
            {
                throw new CommandException(409, "Select a loaded map to watch construction.");
            }
            mapId = map.uniqueID;
            if (body.BlueprintIds != null && (body.BlueprintIds.Length < 1 || body.BlueprintIds.Length > 256 ||
                body.BlueprintIds.Any(string.IsNullOrWhiteSpace)))
            {
                throw new CommandException(400, "Give 1 to 256 construction IDs in watch.blueprintIds.");
            }
            ids = new HashSet<string>(body.BlueprintIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (body.Rect != null)
            {
                if (!body.Rect.X.HasValue || !body.Rect.Z.HasValue ||
                    !new IntVec3(body.Rect.X.Value, 0, body.Rect.Z.Value).InBounds(map))
                {
                    throw new CommandException(400, "Give a valid starting cell in watch.rect.");
                }
                cells = new HashSet<IntVec3>(CommandCells.FromRects(new[] { body.Rect }, map, "watch.rect", out int outside));
                if (outside > 0)
                {
                    throw new CommandException(400, "Keep watch.rect within the map.");
                }
            }
        }

        public bool Matches(BuildWork work)
        {
            return work.Map.uniqueID == mapId && (cells == null || cells.Contains(work.Cell)) &&
                (ids.Count == 0 || ids.Overlaps(work.Ids));
        }

        public static bool Equivalent(AdvanceWatch left, AdvanceWatch right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }
            return left.mapId == right.mapId && left.ids.SetEquals(right.ids) &&
                (left.cells == null ? right.cells == null : right.cells != null && left.cells.SetEquals(right.cells));
        }
    }

    internal sealed class BuildWork
    {
        public readonly Map Map;
        public readonly IntVec3 Cell;
        public readonly BuildableDef Def;
        private readonly ThingDef stuff;
        private readonly Rot4 rotation;
        public readonly string Key;
        public readonly HashSet<string> Ids;
        public readonly bool IsFrame;

        public BuildWork(Thing thing, Map map)
        {
            Map = map;
            Cell = thing.Position;
            Def = thing.def.entityDefToBuild;
            stuff = (thing as IConstructible)?.EntityToBuildStuff();
            rotation = thing.Rotation;
            IsFrame = thing is Frame;
            Key = map.uniqueID + ":" + Cell.x + ":" + Cell.z + ":" + Def.defName + ":" + stuff?.defName + ":" + rotation.AsInt;
            Ids = new HashSet<string>(StringComparer.Ordinal) { thing.ThingID, thing.GetUniqueLoadID() };
        }

        public bool Completed()
        {
            if (!Find.Maps.Contains(Map) || Cell.Fogged(Map))
            {
                return false;
            }
            if (Def is TerrainDef terrain)
            {
                return Map.terrainGrid.TerrainAt(Cell) == terrain;
            }
            return Cell.GetThingList(Map).Any(thing => thing.Position == Cell && thing.def == Def &&
                thing.Stuff == stuff && (thing.Rotation == rotation || !thing.def.rotatable) && thing.Faction == Faction.OfPlayer);
        }
    }

    internal sealed class ResearchWork
    {
        public readonly ResearchProjectDef Project;
        public readonly float Progress;
        public readonly bool Finished;

        public ResearchWork(ResearchManager manager, ResearchProjectDef project)
        {
            Project = project;
            Progress = manager.GetProgress(project);
            Finished = project.IsFinished;
        }
    }

    internal sealed class CaravanTravel
    {
        public readonly Caravan Caravan;
        public readonly PlanetTile Destination;

        public CaravanTravel(Caravan caravan)
        {
            Caravan = caravan;
            Destination = caravan.pather.Destination;
        }
    }

    internal static class AdvanceWork
    {
        public static void Capture(AdvanceObservation now, AdvanceObservation before)
        {
            foreach (Map map in Find.Maps)
            {
                var pending = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)
                    .Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame));
                foreach (Thing thing in pending)
                {
                    if (thing.Faction != Faction.OfPlayer || thing.def.entityDefToBuild == null || thing.Position.Fogged(map))
                    {
                        continue;
                    }
                    var work = new BuildWork(thing, map);
                    if (before != null && before.Pending.TryGetValue(work.Key, out BuildWork earlier) &&
                        (work.Ids.Overlaps(earlier.Ids) || work.IsFrame && !earlier.IsFrame))
                    {
                        work.Ids.UnionWith(earlier.Ids);
                    }
                    now.Pending[work.Key] = work;
                }
            }

            ResearchManager manager = Find.ResearchManager;
            ResearchProjectDef current = ResearchReadService.CurrentProject();
            IEnumerable<ResearchProjectDef> projects = current == null ? Enumerable.Empty<ResearchProjectDef>() : new[] { current };
            if (ModsConfig.AnomalyActive)
            {
                projects = projects.Concat(manager.CurrentAnomalyKnowledgeProjects.Select(entry => entry.project).Where(project => project != null));
            }
            now.ActiveResearch.UnionWith(projects);
            IEnumerable<ResearchProjectDef> observed = before == null
                ? now.ActiveResearch : now.ActiveResearch.Concat(before.ActiveResearch).Distinct();
            foreach (ResearchProjectDef project in observed)
            {
                now.Research[project] = new ResearchWork(manager, project);
            }

            if (before == null)
            {
                return;
            }
            foreach (BuildWork work in before.Pending.Values)
            {
                if (!now.Pending.ContainsKey(work.Key) && work.Completed())
                {
                    now.Built.Add(work);
                }
            }
            foreach (CaravanTravel travel in before.Traveling.Values)
            {
                Caravan caravan = travel.Caravan;
                if (!now.Traveling.ContainsKey(caravan.ID) && caravan.Tile == travel.Destination)
                {
                    now.Arrivals[caravan.ID] = before.MovingCaravans[caravan.ID];
                }
            }
        }
    }

    // Counts are independent of the bounded event timeline. Progress uses snapshots, so a
    // completed project remains visible even after the game clears the current research slot.
    internal sealed class AdvanceSummary
    {
        private readonly float startFood;
        private readonly Dictionary<string, ResearchChange> research = new Dictionary<string, ResearchChange>();
        private readonly HashSet<string> downed = new HashSet<string>();
        private readonly HashSet<string> idle = new HashSet<string>();
        private float endFood;
        private int blueprints;
        private int letters;

        public AdvanceSummary(AdvanceObservation baseline)
        {
            startFood = endFood = baseline.StoredFood;
        }

        public void Observe(AdvanceObservation before, AdvanceObservation now)
        {
            endFood = now.StoredFood;
            foreach (ResearchWork work in now.Research.Values)
            {
                if (!before.Research.TryGetValue(work.Project, out ResearchWork previous) ||
                    work.Progress == previous.Progress && work.Finished == previous.Finished)
                {
                    continue;
                }
                string def = work.Project.defName;
                if (!research.TryGetValue(def, out ResearchChange change))
                {
                    research[def] = change = new ResearchChange { Start = previous.Progress };
                }
                change.End = work.Progress;
                change.Finished = work.Finished;
            }
        }

        public void Record(AdvanceEvent change)
        {
            switch (change.Kind)
            {
                case "built": blueprints++; break;
                case "letter":
                case "threatLetter": letters++; break;
                case "downed": downed.Add(change.PawnId); break;
                case "pawnIdle": idle.Add(change.PawnId); break;
            }
        }

        public void FinishFood(float nutrition) => endFood = nutrition;

        public object ToDto()
        {
            return Dto.Obj(
                Dto.Field("blueprintsCompleted", blueprints),
                Dto.Field("research", research.Take(5).Select(pair => Dto.Obj(
                    Dto.Field("defName", pair.Key),
                    Dto.Field("progressBefore", Math.Round(pair.Value.Start, 3)),
                    Dto.Field("progressAfter", Math.Round(pair.Value.End, 3)),
                    Dto.Field("finished", pair.Value.Finished))).ToArray()),
                Dto.Field("researchProjectsChanged", research.Count),
                Dto.Field("storedNutritionChange", Math.Round(endFood - startFood, 1)),
                Dto.Field("newLetters", letters),
                Dto.Field("colonistsDowned", downed.Count),
                Dto.Field("colonistsBecameIdle", idle.Count));
        }

        private sealed class ResearchChange
        {
            public float Start;
            public float End;
            public bool Finished;
        }
    }
}
