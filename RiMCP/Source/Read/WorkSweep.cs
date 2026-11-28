using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    // list_work's sweep view: what a routine work and schedule review needs per pawn, and nothing
    // else. The current job, the chosen work types' priorities, the work types the pawn cannot do,
    // and the timetable as contiguous blocks instead of 24 hourly rows.
    internal static class WorkSweep
    {
        public const string Flag = "sweep";
        public const string WorkTypesArgument = "workTypes";
        public const int HoursPerDay = 24;
        // A pawn's priority for work it is not assigned to.
        private const int Unassigned = 0;

        internal sealed class Block
        {
            public string Assignment;
            public int From;
            // Exclusive; smaller than From when the block runs past midnight.
            public int To;
        }

        public static object Describe(Map map, IEnumerable<PawnReadService.PawnRole> pawns, ReadRequest request)
        {
            WorkTypeDef[] chosen = Chosen(request);
            return Dto.Obj(
                Dto.Field("manualPriorities", Find.PlaySettings != null && Find.PlaySettings.useWorkPriorities),
                Dto.Field("pawns", pawns.Select(role => Pawn(role.Pawn, chosen)).ToArray()));
        }

        // Null when no work types were named: every active work type is reported.
        private static WorkTypeDef[] Chosen(ReadRequest request)
        {
            string[] names = request.List(WorkTypesArgument);
            if (names == null || names.Length == 0)
            {
                return null;
            }
            return names.Select(Named).ToArray();
        }

        private static WorkTypeDef Named(string name)
        {
            WorkTypeDef def = DefDatabase<WorkTypeDef>.GetNamedSilentFail(name);
            if (def == null)
            {
                throw new BridgeException(400, "Unknown work type '" + name + "'. list_work lists them in workTypes.");
            }
            return def;
        }

        private static object Pawn(Pawn pawn, WorkTypeDef[] chosen)
        {
            return Dto.Obj(
                Dto.Field("id", ReadUtil.ThingId(pawn)),
                Dto.Field("name", ReadUtil.PawnName(pawn)),
                Dto.Field("drafted", pawn.Drafted ? (bool?)true : null),
                Dto.Field("downed", pawn.Downed ? (bool?)true : null),
                Dto.Field("patient", ColonyDangers.IsPatient(pawn) ? (bool?)true : null),
                Dto.Field("breakRisk", ColonyDangers.LevelName(ColonyDangers.BreakRisk(pawn))),
                Dto.Field("job", Job(pawn)),
                Dto.Field("work", Work(pawn, chosen)),
                Dto.Field("incapable", Incapable(pawn, chosen)),
                Dto.Field("schedule", Schedule(pawn)),
                Dto.Field("allowedArea", AllowedArea(pawn)));
        }

        private static object Job(Pawn pawn)
        {
            JobDef def = pawn.CurJobDef;
            if (def == null)
            {
                return null;
            }
            var job = (Dictionary<string, object>)PawnReadService.SerializeCurrentJob(pawn);
            object report;
            job.TryGetValue("report", out report);
            return Dto.Obj(Dto.Field("def", def.defName), Dto.Field("report", report));
        }

        // defName to priority: the chosen types, or every active one. 0 is unassigned.
        private static Dictionary<string, object> Work(Pawn pawn, WorkTypeDef[] chosen)
        {
            if (!Works(pawn))
            {
                return null;
            }
            IEnumerable<WorkTypeDef> capable = (chosen ?? DefDatabase<WorkTypeDef>.AllDefsListForReading.ToArray())
                .Where(def => !pawn.WorkTypeIsDisabled(def));
            IEnumerable<WorkTypeDef> types = chosen == null ? capable.Where(pawn.workSettings.WorkIsActive) : capable;
            var work = new Dictionary<string, object>();
            foreach (WorkTypeDef def in types.OrderBy(def => def.naturalPriority))
            {
                work[def.defName] = pawn.workSettings.WorkIsActive(def) ? pawn.workSettings.GetPriority(def) : Unassigned;
            }
            return work;
        }

        // Only among the chosen types: listing everything a pawn cannot do would rebuild the full read.
        private static string[] Incapable(Pawn pawn, WorkTypeDef[] chosen)
        {
            if (chosen == null || !Works(pawn))
            {
                return null;
            }
            string[] incapable = chosen.Where(pawn.WorkTypeIsDisabled).Select(def => def.defName).ToArray();
            return incapable.Length == 0 ? null : incapable;
        }

        private static bool Works(Pawn pawn)
        {
            return pawn.workSettings != null && pawn.workSettings.EverWork;
        }

        private static object[] Schedule(Pawn pawn)
        {
            if (pawn.timetable == null)
            {
                return null;
            }
            string[] hours = Enumerable.Range(0, HoursPerDay).Select(hour => pawn.timetable.GetAssignment(hour)?.defName).ToArray();
            return Compress(hours).Select(block => (object)Dto.Obj(
                Dto.Field("assignment", block.Assignment),
                Dto.Field("from", block.From),
                Dto.Field("to", block.To))).ToArray();
        }

        private static string AllowedArea(Pawn pawn)
        {
            Area area = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;
            return area == null ? null : area.Label;
        }

        // Runs of the same assignment, in hour order. A run that crosses midnight is one block
        // starting at its evening hour. One assignment all day is a single block from 0 to 0.
        internal static List<Block> Compress(string[] hours)
        {
            var blocks = new List<Block>();
            for (int hour = 0; hour < hours.Length; hour++)
            {
                Block last = blocks.LastOrDefault();
                if (last != null && last.Assignment == hours[hour])
                {
                    last.To = hour + 1;
                    continue;
                }
                blocks.Add(new Block { Assignment = hours[hour], From = hour, To = hour + 1 });
            }
            return JoinAcrossMidnight(blocks, hours.Length);
        }

        private static List<Block> JoinAcrossMidnight(List<Block> blocks, int length)
        {
            if (blocks.Count < 2 || blocks[0].Assignment != blocks[blocks.Count - 1].Assignment)
            {
                return Wrap(blocks, length);
            }
            Block morning = blocks[0];
            Block evening = blocks[blocks.Count - 1];
            evening.To = morning.To;
            blocks.RemoveAt(0);
            return Wrap(blocks, length);
        }

        // The day's end is written as hour 0 of the next.
        private static List<Block> Wrap(List<Block> blocks, int length)
        {
            foreach (Block block in blocks.Where(each => each.To == length))
            {
                block.To = 0;
            }
            return blocks;
        }
    }
}
