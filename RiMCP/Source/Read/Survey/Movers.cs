using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RiMCP.Read
{
    internal sealed class SurveyMovers
    {
        private const int HerdRadius = 10;
        private const int EdgeBand = 8;
        private const int SmallGameKinds = 6;
        private const float LargeBody = 2f;
        private const int TimePrecision = 2;
        private static readonly string[] Kinds = { "hostiles", "animalThreat", "visitors", "colonyAnimals", "wildlife" };
        private static readonly string[] Edges = { "W", "E", "S", "N" };
        private static readonly char[] Markers = { 'H', 'P', 'V', 'D', 'W' };
        private enum Rank { Hostile, AnimalThreat, Visitor, ColonyAnimal, Wildlife, SmallGame, Ignore }

        private sealed class Seen
        {
            public Pawn Pawn;
            public Rank Rank;
            public Lord Lord;
        }

        private sealed class Group
        {
            public List<Seen> Members;
            public Rank Rank => Members[0].Rank;
            public Lord Lord => Members[0].Lord;
            public IntVec3 Centre;
            public IntVec3 Anchor;
            public int Distance;
            public int Key => Members.Min(member => member.Pawn.thingIDNumber);
        }

        private readonly Group[] groups;
        private readonly IGrouping<string, Seen>[] smallGame;
        private readonly Map map;
        private readonly IntVec3 colony;
        private readonly IntVec3[] colonists;

        public static IntVec3[] VisibleColonists(Map map)
        {
            return map.mapPawns.FreeColonistsSpawned.Where(pawn => PawnReadService.Visible(map, pawn))
                .Select(pawn => pawn.Position).ToArray();
        }

        public SurveyMovers(Map map, IntVec3 colony, IntVec3[] colonists)
        {
            this.map = map;
            this.colony = colony;
            this.colonists = colonists;
            var visible = map.mapPawns.AllPawnsSpawned.Where(pawn => PawnReadService.Visible(map, pawn))
                .OrderBy(pawn => pawn.thingIDNumber).Select(Classify).ToArray();
            var movers = visible.Where(seen => seen.Rank < Rank.SmallGame).ToArray();
            var parties = movers.Where(seen => seen.Lord != null)
                .GroupBy(seen => new { seen.Rank, seen.Lord }).Select(group => group.ToList());
            var herds = movers.Where(seen => seen.Lord == null)
                .GroupBy(seen => new { seen.Rank, seen.Pawn.def, seen.Pawn.Faction })
                .SelectMany(group => SurveyGeometry.Cluster(group, seen => seen.Pawn.Position, HerdRadius));
            groups = parties.Concat(herds).Select(MakeGroup)
                .OrderBy(group => group.Rank).ThenBy(group => group.Distance).ThenBy(group => group.Key).ToArray();
            smallGame = visible.Where(seen => seen.Rank == Rank.SmallGame).GroupBy(seen => seen.Pawn.def.defName)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
        }

        public Dictionary<IntVec3, char> Overlay()
        {
            var marks = new Dictionary<IntVec3, char>();
            foreach (Group group in groups)
            {
                if (!marks.ContainsKey(group.Anchor))
                {
                    marks.Add(group.Anchor, Markers[(int)group.Rank]);
                }
            }
            return marks;
        }

        public object Describe(int limit)
        {
            return Dto.Obj(
                Dto.Field("origin", SurveyGeometry.Cell(colony)),
                Dto.Field("groups", groups.Take(limit).Select(DescribeGroup).ToArray()),
                Dto.Field("omittedGroups", groups.Length > limit ? (int?)(groups.Length - limit) : null),
                Dto.Field("smallGame", smallGame.Take(SmallGameKinds).ToDictionary(group => group.Key, group => group.Count())),
                Dto.Field("omittedSmallGameKinds", smallGame.Length > SmallGameKinds ? (int?)(smallGame.Length - SmallGameKinds) : null));
        }

        internal object DescribeDigest(int limit)
        {
            return groups.Take(limit).Select(group => Dto.Obj(
                Dto.Field("id", GroupId(group)),
                Dto.Field("kind", Kinds[(int)group.Rank]),
                Dto.Field("count", group.Members.Count),
                Dto.Field("distance", group.Distance),
                Dto.Field("bearing", SurveyGeometry.Bearing(colony, group.Centre)),
                Dto.Field("job", MainJob(group)))).ToArray();
        }

        private static string GroupId(Group group)
        {
            string kind = Kinds[(int)group.Rank];
            return group.Lord == null ? kind + "-" + group.Key : kind + "-" + group.Lord.GetUniqueLoadID();
        }

        private Seen Classify(Pawn pawn)
        {
            Rank rank = RankOf(pawn);
            Lord lord = rank <= Rank.Visitor || (pawn.RaceProps.Animal && pawn.Faction != null && pawn.Faction != Faction.OfPlayer)
                ? pawn.GetLord() : null;
            if (lord != null && (rank == Rank.Wildlife || rank == Rank.SmallGame))
            {
                rank = Rank.Visitor;
            }
            return new Seen { Pawn = pawn, Rank = rank, Lord = rank <= Rank.Visitor ? lord : null };
        }

        private Rank RankOf(Pawn pawn)
        {
            if (pawn.Dead || pawn.IsPrisonerOfColony || pawn.IsSlaveOfColony)
            {
                return Rank.Ignore;
            }
            if (pawn.RaceProps.Animal)
            {
                return AnimalRank(pawn);
            }
            if (pawn.Faction == Faction.OfPlayer)
            {
                return Rank.Ignore;
            }
            return pawn.HostileTo(Faction.OfPlayer) ? Rank.Hostile : Rank.Visitor;
        }

        private Rank AnimalRank(Pawn pawn)
        {
            if (ColonyDangers.IsManhunter(pawn) || ColonyDangers.IsPredator(pawn))
            {
                return Rank.AnimalThreat;
            }
            if (pawn.Faction == Faction.OfPlayer)
            {
                return map.areaManager.Home[pawn.Position] ? Rank.Ignore : Rank.ColonyAnimal;
            }
            if (pawn.Faction != null && pawn.HostileTo(Faction.OfPlayer))
            {
                return Rank.Hostile;
            }
            return Traits(pawn).Length != 0 ? Rank.Wildlife : Rank.SmallGame;
        }

        private static string[] Traits(Pawn pawn)
        {
            var traits = new List<string>();
            RaceProperties race = pawn.RaceProps;
            Type action = race.deathAction?.workerClass;
            if (race.predator) traits.Add("predator");
            if (race.baseBodySize >= LargeBody) traits.Add("large");
            if (race.packAnimal) traits.Add("packAnimal");
            if (action != null && (typeof(DeathActionWorker_SmallExplosion).IsAssignableFrom(action) ||
                typeof(DeathActionWorker_BigExplosion).IsAssignableFrom(action))) traits.Add("explodesOnDeath");
            if (race.petness > 0 && (pawn.TryGetComp<CompHasGatherableBodyResource>() != null || pawn.TryGetComp<CompEggLayer>() != null))
                traits.Add("tameableProduct");
            return traits.ToArray();
        }

        private Group MakeGroup(List<Seen> members)
        {
            IntVec3[] cells = members.Select(member => member.Pawn.Position).ToArray();
            IntVec3 centre = SurveyGeometry.Centre(cells);
            return new Group
            {
                Members = members, Centre = centre, Distance = SurveyGeometry.Distance(colony, centre),
                Anchor = cells.OrderBy(cell => cell.DistanceToSquared(centre)).First()
            };
        }

        private object DescribeGroup(Group group)
        {
            Pawn first = group.Members[0].Pawn;
            string kind = Kinds[(int)group.Rank];
            bool hostile = group.Rank == Rank.Hostile;
            return Dto.Obj(
                Dto.Field("id", GroupId(group)),
                Dto.Field("kind", kind),
                Dto.Field("def", group.Lord == null || group.Rank == Rank.AnimalThreat ? first.def.defName : null),
                Dto.Field("faction", first.Faction?.Name),
                Dto.Field("count", group.Members.Count),
                Dto.Field("center", SurveyGeometry.Cell(group.Centre)),
                Dto.Field("spread", group.Members.Max(member => SurveyGeometry.Distance(group.Centre, member.Pawn.Position))),
                Dto.Field("distance", group.Distance),
                Dto.Field("bearing", SurveyGeometry.Bearing(colony, group.Centre)),
                Dto.Field("job", MainJob(group)),
                Dto.Field("party", group.Lord?.LordJob?.GetType().Name),
                Dto.Field("stage", group.Lord?.CurLordToil?.GetType().Name),
                Dto.Field("heading", Heading(group)),
                Dto.Field("traits", first.RaceProps.Animal && group.Rank == Rank.Wildlife ? Traits(first) : null),
                Dto.Field("arms", hostile ? Arms(group) : null),
                Dto.Field("nearEdge", hostile ? NearbyEdge(group.Anchor) : null),
                Dto.Field("walkHours", hostile ? WalkHours(group) : null));
        }

        private static string MainJob(Group group)
        {
            return group.Members.Select(member => member.Pawn.CurJobDef?.defName).Where(name => name != null)
                .GroupBy(name => name).OrderByDescending(jobs => jobs.Count()).ThenBy(jobs => jobs.Key, StringComparer.Ordinal)
                .Select(jobs => jobs.Key).FirstOrDefault();
        }

        private static object Arms(Group group)
        {
            int ranged = group.Members.Count(member => member.Pawn.equipment?.Primary?.def.IsRangedWeapon == true);
            int melee = group.Members.Count(member => member.Pawn.equipment?.Primary?.def.IsMeleeWeapon == true);
            return Dto.Obj(Dto.Field("ranged", ranged), Dto.Field("melee", melee),
                Dto.Field("noClassifiedWeapon", group.Members.Count - ranged - melee));
        }

        private object Heading(Group group)
        {
            var paths = group.Members.Select(member => new { Position = member.Pawn.Position, Goal = Destination(member.Pawn) })
                .Where(path => path.Goal.IsValid).ToArray();
            if (paths.Length == 0)
            {
                return null;
            }
            IntVec3 from = SurveyGeometry.Centre(paths.Select(path => path.Position));
            IntVec3 goal = SurveyGeometry.Centre(paths.Select(path => path.Goal));
            return Dto.Obj(
                Dto.Field("bearing", SurveyGeometry.Bearing(from, goal)),
                Dto.Field("relative", SurveyGeometry.RelativeHeading(from, goal, colony)),
                Dto.Field("paths", paths.Length));
        }

        private IntVec3 Destination(Pawn pawn)
        {
            if (pawn.pather?.Moving != true)
            {
                return IntVec3.Invalid;
            }
            IntVec3 goal = pawn.pather.Destination.Cell;
            return goal.InBounds(map) && !goal.Fogged(map) ? goal : IntVec3.Invalid;
        }

        private string NearbyEdge(IntVec3 cell)
        {
            int[] edges = { cell.x, map.Size.x - 1 - cell.x, cell.z, map.Size.z - 1 - cell.z };
            int nearest = edges.Min();
            return nearest <= EdgeBand ? Edges[Array.IndexOf(edges, nearest)] : null;
        }

        private double? WalkHours(Group group)
        {
            if (colonists.Length == 0)
            {
                return null;
            }
            double fastest = double.PositiveInfinity;
            foreach (Seen member in group.Members)
            {
                float speed = member.Pawn.Downed ? 0f : member.Pawn.GetStatValue(StatDefOf.MoveSpeed);
                if (speed <= 0f)
                {
                    continue;
                }
                double distance = Math.Sqrt(colonists.Min(cell => cell.DistanceToSquared(member.Pawn.Position)));
                fastest = Math.Min(fastest, distance * GenTicks.TicksPerRealSecond / (speed * GenDate.TicksPerHour));
            }
            return double.IsPositiveInfinity(fastest) ? (double?)null : Math.Round(fastest, TimePrecision);
        }
    }
}
