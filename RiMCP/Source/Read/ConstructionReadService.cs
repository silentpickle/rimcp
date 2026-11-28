using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;
using Verse.AI;

namespace RiMCP.Read
{
    // Why the colony's outstanding blueprints and frames are or are not moving, one short verdict
    // each. Builders are asked with the game's own GenConstruct.CanConstruct, so its reasons (no
    // path, reserved, skill too low, something in the way) are the game's. Material is counted twice:
    // on the map (explored, any state) and usable at the site (not forbidden and reachable from it).
    internal static class ConstructionReadService
    {
        public const string Building = "building";
        public const string Ready = "ready";
        public const string NeedsMaterial = "needsMaterial";
        public const string MaterialUnusable = "materialUnusable";
        public const string NoBuilder = "noBuilder";
        public const string Blocked = "blocked";
        // An item or plant is in the way; builders haul it aside or cut it first, as the game does.
        public const string Clearing = "clearing";
        public const string Forbidden = "forbidden";

        private const int DefaultLimit = 10;
        private const int MaxReasons = 3;
        // Sites given a verdict for the summary; past this, the reply says how many were summarized.
        private const int MaxSummarized = 200;

        public static BridgeResponse GetConstruction(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            string[] ids = context.Request.List("ids");
            List<Thing> sites = ids != null && ids.Length > 0 ? Named(context.Map, ids) : Outstanding(context.Map).ToList();
            if (context.Request.Get("limit") == null)
            {
                context.Request.Limit = DefaultLimit;
            }
            List<Dictionary<string, object>> described = sites.Take(MaxSummarized).Select(site => Describe(context.Map, site)).ToList();
            Page<Dictionary<string, object>> page = new Page<Dictionary<string, object>>(described, context.Request);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("outstanding", sites.Count),
                Dto.Field("summarized", sites.Count > MaxSummarized ? (int?)MaxSummarized : null),
                Dto.Field("byVerdict", ByVerdict(described)),
                Dto.Field("shortfalls", Shortfalls(context.Map, sites.Take(MaxSummarized))),
                Dto.Field("sites", page.Items.ToArray())),
                page.Truncated, page.NextCursor, "sites");
        }

        // How many sites have each verdict, so a long wall reads as one line.
        private static Dictionary<string, object> ByVerdict(List<Dictionary<string, object>> described)
        {
            if (described.Count == 0)
            {
                return null;
            }
            return described.GroupBy(site => (string)site["verdict"]).ToDictionary(group => group.Key, group => (object)group.Count());
        }

        // Material the outstanding sites still need in total but cannot get: what is left across them,
        // what is usable from the first site needing it, and what is on the map at all.
        private static object[] Shortfalls(Map map, IEnumerable<Thing> sites)
        {
            var remaining = new Dictionary<ThingDef, int>();
            var firstSite = new Dictionary<ThingDef, Thing>();
            foreach (Thing site in sites)
            {
                AddRemaining((IConstructible)site, site, remaining, firstSite);
            }
            object[] shortfalls = remaining
                .Select(need => new { def = need.Key, left = need.Value, usable = Usable(map, firstSite[need.Key], need.Key, need.Value) })
                .Where(need => need.usable < need.left)
                .Select(need => (object)Dto.Obj(
                    Dto.Field("def", need.def.defName),
                    Dto.Field("remaining", need.left),
                    Dto.Field("usable", need.usable),
                    Dto.Field("onMap", OnMap(map, need.def))))
                .ToArray();
            return shortfalls.Length == 0 ? null : shortfalls;
        }

        private static void AddRemaining(IConstructible constructible, Thing site, Dictionary<ThingDef, int> remaining, Dictionary<ThingDef, Thing> firstSite)
        {
            foreach (ThingDefCountClass cost in constructible.TotalMaterialCost())
            {
                int left = constructible.ThingCountNeeded(cost.thingDef);
                if (left <= 0)
                {
                    continue;
                }
                int sum;
                remaining.TryGetValue(cost.thingDef, out sum);
                remaining[cost.thingDef] = sum + left;
                if (!firstSite.ContainsKey(cost.thingDef))
                {
                    firstSite[cost.thingDef] = site;
                }
            }
        }

        // Frames first, as work already started; then blueprints, oldest first.
        private static IEnumerable<Thing> Outstanding(Map map)
        {
            return map.listerThings.ThingsInGroup(ThingRequestGroup.Construction)
                .Where(thing => thing.Faction == Faction.OfPlayer && thing is IConstructible)
                .OrderBy(thing => thing is Frame ? 0 : 1)
                .ThenBy(thing => thing.thingIDNumber);
        }

        private static List<Thing> Named(Map map, string[] ids)
        {
            var found = new List<Thing>();
            foreach (string id in ids)
            {
                Thing thing = map.listerThings.ThingsInGroup(ThingRequestGroup.Construction).FirstOrDefault(each => each.ThingID == id || each.GetUniqueLoadID() == id);
                if (thing == null)
                {
                    throw new BridgeException(404, "No blueprint or frame '" + id + "' on this map. Omit ids to list the outstanding ones.");
                }
                found.Add(thing);
            }
            return found;
        }

        private static Dictionary<string, object> Describe(Map map, Thing site)
        {
            object[] needs = Needs(map, site);
            Pawn worker = Worker(map, site);
            BuilderCheck builders = Builders(map, site);
            Thing blocking = GenConstruct.FirstBlockingThing(site, null);
            string verdict = Verdict(site, worker, builders, blocking, needs);
            Frame frame = site as Frame;
            return Dto.Obj(
                Dto.Field("id", ReadUtil.ThingId(site)),
                Dto.Field("def", BuiltDef(site)?.defName),
                Dto.Field("stage", frame != null ? "frame" : "blueprint"),
                Dto.Field("cell", ReadUtil.Cell(site.Position)),
                Dto.Field("stuff", ((IConstructible)site).EntityToBuildStuff()?.defName),
                Dto.Field("workLeft", frame == null ? null : (int?)frame.WorkLeft),
                Dto.Field("verdict", verdict),
                Dto.Field("worker", worker == null ? null : Dto.Obj(
                    Dto.Field("id", ReadUtil.ThingId(worker)),
                    Dto.Field("job", worker.CurJobDef?.defName))),
                Dto.Field("needs", needs.Length == 0 ? null : needs),
                Dto.Field("blockedBy", blocking == null ? null : Dto.Obj(
                    Dto.Field("id", ReadUtil.ThingId(blocking)),
                    Dto.Field("def", blocking.def.defName))),
                Dto.Field("builders", builders.Able),
                Dto.Field("refusals", builders.Reasons.Count == 0 || builders.Able > 0 || blocking != null ? null : builders.Reasons.ToArray()));
        }

        // One word an agent can act on, most specific first.
        private static string Verdict(Thing site, Pawn worker, BuilderCheck builders, Thing blocking, object[] needs)
        {
            if (worker != null)
            {
                return Building;
            }
            if (site.IsForbidden(Faction.OfPlayer))
            {
                return Forbidden;
            }
            if (blocking != null && !ClearedByBuilders(blocking))
            {
                return Blocked;
            }
            if (needs.Cast<Dictionary<string, object>>().Any(need => (int)need["usable"] < (int)need["remaining"]))
            {
                return needs.Cast<Dictionary<string, object>>().Any(need => (int)need["onMap"] >= (int)need["remaining"]) ? MaterialUnusable : NeedsMaterial;
            }
            if (blocking != null)
            {
                return builders.Constructors > 0 ? Clearing : NoBuilder;
            }
            return builders.Able > 0 ? Ready : NoBuilder;
        }

        // GenConstruct.HandleBlockingThingJob hauls items aside and cuts plants without an order.
        private static bool ClearedByBuilders(Thing blocking)
        {
            return blocking.def.EverHaulable || blocking.def.category == ThingCategory.Plant;
        }

        // Material still to deliver, per def: what is left, what is usable at the site, and what is on
        // the map at all. Usable is a lower bound: counting stops once it covers what is left.
        private static object[] Needs(Map map, Thing site)
        {
            IConstructible constructible = (IConstructible)site;
            return constructible.TotalMaterialCost()
                .Select(cost => new { def = cost.thingDef, remaining = constructible.ThingCountNeeded(cost.thingDef) })
                .Where(need => need.remaining > 0)
                .Select(need => (object)Dto.Obj(
                    Dto.Field("def", need.def.defName),
                    Dto.Field("remaining", need.remaining),
                    Dto.Field("usable", Usable(map, site, need.def, need.remaining)),
                    Dto.Field("onMap", OnMap(map, need.def))))
                .ToArray();
        }

        private static int Usable(Map map, Thing site, ThingDef def, int enough)
        {
            int usable = 0;
            TraverseParms traverse = TraverseParms.For(TraverseMode.PassDoors);
            foreach (Thing stack in map.listerThings.ThingsOfDef(def).Where(each => Visible(map, each) && !each.IsForbidden(Faction.OfPlayer)))
            {
                if (usable >= enough)
                {
                    break;
                }
                if (map.reachability.CanReach(stack.PositionHeld, site, PathEndMode.Touch, traverse))
                {
                    usable += stack.stackCount;
                }
            }
            return usable;
        }

        private static int OnMap(Map map, ThingDef def)
        {
            return map.listerThings.ThingsOfDef(def).Where(each => Visible(map, each)).Sum(each => each.stackCount);
        }

        private static bool Visible(Map map, Thing thing)
        {
            return thing.Spawned && !thing.Position.Fogged(map);
        }

        // A colonist whose current job is on this site: building it or bringing it material.
        private static Pawn Worker(Map map, Thing site)
        {
            return map.mapPawns.FreeColonistsSpawned.FirstOrDefault(pawn => pawn.CurJob != null && pawn.CurJob.AnyTargetIs(site));
        }

        private sealed class BuilderCheck
        {
            public int Able;
            public int Constructors;
            public readonly List<object> Reasons = new List<object>();
        }

        // Each colonist with Construction on, asked as the work giver asks.
        private static BuilderCheck Builders(Map map, Thing site)
        {
            var check = new BuilderCheck();
            var seen = new HashSet<string>();
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned.Where(Constructs))
            {
                check.Constructors++;
                string reason = Refusal(pawn, site);
                if (reason == null)
                {
                    check.Able++;
                    continue;
                }
                if (check.Reasons.Count < MaxReasons && seen.Add(reason))
                {
                    check.Reasons.Add(Dto.Obj(Dto.Field("pawnId", ReadUtil.ThingId(pawn)), Dto.Field("reason", reason)));
                }
            }
            if (check.Able == 0 && check.Reasons.Count == 0)
            {
                check.Reasons.Add(Dto.Obj(Dto.Field("reason", "No colonist who can act has Construction on.")));
            }
            return check;
        }

        private static bool Constructs(Pawn pawn)
        {
            return !pawn.Downed && !pawn.Drafted && pawn.workSettings != null && pawn.workSettings.EverWork
                && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Construction) && pawn.workSettings.WorkIsActive(WorkTypeDefOf.Construction);
        }

        // Null when the game would let this pawn work on the site now.
        private static string Refusal(Pawn pawn, Thing site)
        {
            JobFailReason.Clear();
            bool able = GenConstruct.CanConstruct(site, pawn, WorkTypeDefOf.Construction);
            string reason = able ? null : JobFailReason.Reason ?? "The game gives no reason (for example, it cannot reserve or reach the site).";
            JobFailReason.Clear();
            return reason == null ? null : reason.StripTags();
        }

        private static ThingDef BuiltDef(Thing site)
        {
            return site.def.entityDefToBuild as ThingDef ?? site.def;
        }
    }
}
