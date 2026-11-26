using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Util;

namespace RiMCP.Command
{
    // set_work_priorities: a Work-tab update for several pawns and work types in one request. Each
    // entry runs through set_pawn_work_priority's own handler, so checks, manual-mode switching and
    // the agent log match a single call; a refused entry does not stop the rest.
    internal static partial class PawnCommandService
    {
        public const int MaxBatchPriorities = 100;
        private const string BatchTool = "set_work_priorities";

        public static BridgeResponse SetWorkPriorities(BridgeRequest request, RouteMatch route)
        {
            WorkPrioritiesBody body = CommandUtil.ReadBody<WorkPrioritiesBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            RequireBatch(body.Entries);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            RequestKey key = RequestKey.For(BatchTool, requestId, context.Map, body.Entries.Select(Fingerprint).ToArray());
            return CommandReceipts.Once(context, key, () => ApplyAll(request, body));
        }

        private static void RequireBatch(PriorityEntryBody[] entries)
        {
            if (entries == null || entries.Length == 0)
            {
                throw new CommandException(400, "Missing required field 'entries'.");
            }
            if (entries.Length > MaxBatchPriorities)
            {
                throw new CommandException(400, "Give at most " + MaxBatchPriorities + " entries per call.");
            }
            if (entries.Any(entry => entry == null || string.IsNullOrWhiteSpace(entry.PawnId) || string.IsNullOrWhiteSpace(entry.WorkType) || !entry.Priority.HasValue))
            {
                throw new CommandException(400, "Every entry needs pawnId, workType and priority.");
            }
        }

        private static string Fingerprint(PriorityEntryBody entry)
        {
            return entry.PawnId + "," + entry.WorkType + "," + entry.Priority;
        }

        private static CommandResult ApplyAll(BridgeRequest request, WorkPrioritiesBody body)
        {
            List<object> results = new List<object>();
            int applied = 0, unchanged = 0;
            for (int index = 0; index < body.Entries.Length; index++)
            {
                PriorityEntryBody entry = body.Entries[index];
                BridgeResponse response = ApplyEntry(request, body.MapId, entry);
                string outcome = Outcome(response);
                applied += outcome == "applied" ? 1 : 0;
                unchanged += outcome == "alreadySatisfied" ? 1 : 0;
                results.Add(Dto.Obj(
                    Dto.Field("index", index),
                    Dto.Field("pawnId", entry.PawnId),
                    Dto.Field("workType", entry.WorkType),
                    Dto.Field("priority", entry.Priority),
                    Dto.Field("outcome", outcome),
                    Dto.Field("reason", response.ErrorMessage)));
            }
            return new CommandResult(applied > 0, Dto.Obj(
                Dto.Field("applied", applied),
                Dto.Field("alreadySatisfied", unchanged),
                Dto.Field("rejectedCount", results.Count - applied - unchanged),
                Dto.Field("results", results.ToArray())));
        }

        private static string Outcome(BridgeResponse response)
        {
            if (response.ErrorMessage != null)
            {
                return "rejected";
            }
            return response.Changed == true ? "applied" : "alreadySatisfied";
        }

        // SetWorkPriority throws a BridgeException for a refused entry, as the single tool reports
        // it; the batch turns it into that entry's outcome.
        private static BridgeResponse ApplyEntry(BridgeRequest request, string mapId, PriorityEntryBody entry)
        {
            string json = Json.Serialize(Dto.Obj(Dto.Field("mapId", mapId), Dto.Field("priority", entry.Priority)));
            BridgeRequest single = new BridgeRequest("PUT", request.Uri, json, request.Client);
            RouteMatch match = new RouteMatch(new Dictionary<string, string> { { "pawnId", entry.PawnId }, { "workTypeDefName", entry.WorkType } });
            try
            {
                return SetWorkPriority(single, match);
            }
            catch (BridgeException ex)
            {
                return BridgeResponse.Error(ex.StatusCode, ex.Message);
            }
        }

        [DataContract]
        private sealed class WorkPrioritiesBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "entries")]
            public PriorityEntryBody[] Entries { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class PriorityEntryBody
        {
            [DataMember(Name = "pawnId")]
            public string PawnId { get; set; }

            [DataMember(Name = "workType")]
            public string WorkType { get; set; }

            [DataMember(Name = "priority")]
            public int? Priority { get; set; }
        }
    }
}
