using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RiMCP.Bridge;
using RiMCP.Read;

namespace RiMCP.Command
{
    // order_pawns: several order_pawn calls in one request, given in order with the same checks.
    // Each entry gets order_pawn's own result, or the reason it was refused; one refusal does not
    // stop the rest. An item claimed by an earlier equip or wear entry is refused for later ones.
    internal static partial class PawnOrderCommandService
    {
        public const int MaxBatchOrders = 20;
        private const string BatchTool = "order_pawns";
        private const string RejectedOutcome = "rejected";
        private static readonly HashSet<string> ClaimingOrders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "equip", "wear" };

        public static BridgeResponse OrderPawns(BridgeRequest request, RouteMatch route)
        {
            OrderPawnsBody body = CommandUtil.ReadBody<OrderPawnsBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            RequireBatch(body.Orders);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            RequestKey key = RequestKey.For(BatchTool, requestId, context.Map, body.Orders.Select(Fingerprint).ToArray());
            return CommandReceipts.Once(context, key, () => GiveAll(context, body.MapId, body.Orders));
        }

        private static void RequireBatch(OrderEntryBody[] orders)
        {
            if (orders == null || orders.Length == 0)
            {
                throw new CommandException(400, "Missing required field 'orders'.");
            }
            if (orders.Length > MaxBatchOrders)
            {
                throw new CommandException(400, "Give at most " + MaxBatchOrders + " orders per call.");
            }
            if (orders.Any(entry => entry == null || string.IsNullOrWhiteSpace(entry.PawnId) || string.IsNullOrWhiteSpace(entry.Order)))
            {
                throw new CommandException(400, "Every entry in 'orders' needs pawnId and order.");
            }
        }

        private static string Fingerprint(OrderEntryBody entry)
        {
            return string.Join(",", new[] { entry.PawnId, entry.Order, entry.TargetId, entry.X.ToString(), entry.Z.ToString(), (entry.Queue == true).ToString() });
        }

        private static CommandResult GiveAll(CommandContext context, string mapId, OrderEntryBody[] orders)
        {
            Dictionary<string, int> claims = new Dictionary<string, int>();
            List<object> results = new List<object>();
            bool changed = false;
            for (int index = 0; index < orders.Length; index++)
            {
                BatchOutcome outcome = GiveEntry(context, mapId, orders[index], index, claims);
                changed |= outcome.Changed;
                results.Add(outcome.Dto);
            }
            return new CommandResult(changed, Dto.Obj(
                Dto.Field("given", results.Count(IsGiven)),
                Dto.Field("refused", results.Count - results.Count(IsGiven)),
                Dto.Field("results", results.ToArray())));
        }

        // Give reports some refusals, such as a target out of range, as outcome "rejected" in its
        // result rather than by throwing, so both count as not given.
        private static bool IsGiven(object result)
        {
            Dictionary<string, object> dto = (Dictionary<string, object>)result;
            if (dto.ContainsKey("refused"))
            {
                return false;
            }
            object inner;
            dto.TryGetValue("result", out inner);
            Dictionary<string, object> data = inner as Dictionary<string, object>;
            object outcome = null;
            if (data != null)
            {
                data.TryGetValue("outcome", out outcome);
            }
            return (outcome as string) != RejectedOutcome;
        }

        private static BatchOutcome GiveEntry(CommandContext context, string mapId, OrderEntryBody entry, int index, Dictionary<string, int> claims)
        {
            string conflict = ClaimConflict(entry, claims);
            if (conflict != null)
            {
                return BatchOutcome.Refused(index, entry.PawnId, 409, conflict);
            }
            PawnOrder order = Orders.FirstOrDefault(candidate => string.Equals(candidate.Name, entry.Order.Trim(), StringComparison.OrdinalIgnoreCase));
            if (order == null)
            {
                return BatchOutcome.Refused(index, entry.PawnId, 400, "order must be one of: " + string.Join(", ", Orders.Select(candidate => candidate.Name).ToArray()) + ".");
            }
            bool queue = entry.Queue == true;
            if (queue && order.QueuedJob == null)
            {
                return BatchOutcome.Refused(index, entry.PawnId, 400, "A " + order.Name + " order cannot be queued.");
            }
            BatchOutcome given = GiveChecked(context, order, ToBody(mapId, entry), entry.PawnId, queue, index);
            if (IsGiven(given.Dto))
            {
                Claim(entry, index, claims);
            }
            return given;
        }

        // Give throws a BridgeException for a refusal, as order_pawn reports it; here it becomes
        // this entry's outcome so the remaining orders are still given.
        private static BatchOutcome GiveChecked(CommandContext context, PawnOrder order, OrderPawnBody body, string pawnId, bool queue, int index)
        {
            try
            {
                CommandResult result = Give(context, order, body, pawnId, queue);
                return new BatchOutcome(result.Changed, Dto.Obj(Dto.Field("index", index), Dto.Field("pawnId", pawnId), Dto.Field("result", result.Data)));
            }
            catch (BridgeException ex)
            {
                return BatchOutcome.Refused(index, pawnId, ex.StatusCode, ex.Message);
            }
        }

        private static string ClaimConflict(OrderEntryBody entry, Dictionary<string, int> claims)
        {
            int earlier;
            if (!ClaimingOrders.Contains(entry.Order.Trim()) || entry.TargetId == null || !claims.TryGetValue(entry.TargetId, out earlier))
            {
                return null;
            }
            return "Item " + entry.TargetId + " is already claimed by orders[" + earlier + "] in this call.";
        }

        private static void Claim(OrderEntryBody entry, int index, Dictionary<string, int> claims)
        {
            if (ClaimingOrders.Contains(entry.Order.Trim()) && entry.TargetId != null && !claims.ContainsKey(entry.TargetId))
            {
                claims[entry.TargetId] = index;
            }
        }

        private static OrderPawnBody ToBody(string mapId, OrderEntryBody entry)
        {
            return new OrderPawnBody { MapId = mapId, Order = entry.Order, TargetId = entry.TargetId, X = entry.X, Z = entry.Z, Queue = entry.Queue };
        }

        [DataContract]
        private sealed class OrderPawnsBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "orders")]
            public OrderEntryBody[] Orders { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class OrderEntryBody
        {
            [DataMember(Name = "pawnId")]
            public string PawnId { get; set; }

            [DataMember(Name = "order")]
            public string Order { get; set; }

            [DataMember(Name = "targetId")]
            public string TargetId { get; set; }

            [DataMember(Name = "x")]
            public int? X { get; set; }

            [DataMember(Name = "z")]
            public int? Z { get; set; }

            [DataMember(Name = "queue")]
            public bool? Queue { get; set; }
        }
    }

    // One entry's outcome in a batch call: its dto, and whether it changed the game.
    internal sealed class BatchOutcome
    {
        public readonly bool Changed;
        public readonly Dictionary<string, object> Dto;

        public BatchOutcome(bool changed, Dictionary<string, object> dto)
        {
            Changed = changed;
            Dto = dto;
        }

        public static BatchOutcome Refused(int index, string pawnId, int status, string reason)
        {
            return new BatchOutcome(false, RiMCP.Read.Dto.Obj(
                RiMCP.Read.Dto.Field("index", index),
                RiMCP.Read.Dto.Field("pawnId", pawnId),
                RiMCP.Read.Dto.Field("refused", reason),
                RiMCP.Read.Dto.Field("status", status)));
        }
    }
}
