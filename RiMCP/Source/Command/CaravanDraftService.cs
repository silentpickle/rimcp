using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Util;
using Verse;
using Verse.AI.Group;

namespace RiMCP.Command
{
    internal static class CaravanDraftService
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type Dialog = typeof(Dialog_FormCaravan);
        private static readonly MethodInfo AddPawns = Dialog.GetMethod("AddPawnsToTransferables", Private);
        private static readonly MethodInfo AddItems = Dialog.GetMethod("AddItemsToTransferables", Private);
        private static readonly MethodInfo Form = Dialog.GetMethod("TryFormAndSendCaravan", Private);
        private static readonly MethodInfo Reform = Dialog.GetMethod("TryReformCaravan", Private);
        private static readonly FieldInfo StartingTile = Dialog.GetField("startingTile", Private);
        private static readonly FieldInfo DestinationTile = Dialog.GetField("destinationTile", Private);
        private static readonly PropertyInfo Food = Dialog.GetProperty("DaysWorthOfFood", Private);
        private static readonly PropertyInfo TravelTicks = Dialog.GetProperty("TicksToArrive", Private);

        public static BridgeResponse List(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            Draft draft = new Draft(context.Map);
            string view = context.Request.Get("view") ?? "pawns";
            if (view != "pawns" && view != "supplies")
            {
                throw new BridgeException(400, "Use view pawns or supplies.");
            }
            var page = new Page<TransferableOneWay>(draft.Dialog.transferables
                .Where(group => (group.AnyThing is Pawn) == (view == "pawns")).OrderBy(GroupId, StringComparer.Ordinal), context.Request);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("mode", draft.Mode),
                Dto.Field("blockedBy", draft.BlockedBy()),
                Dto.Field("rows", page.Items.Select(group => Dto.Obj(
                    Dto.Field("id", GroupId(group)),
                    Dto.Field("def", group.ThingDef.defName),
                    Dto.Field("label", group.AnyThing.LabelCap.StripTags()),
                    Dto.Field("available", group.GetMaximumToTransfer()),
                    Dto.Field("selectedByDefault", group.CountToTransfer),
                    Dto.Field("downed", group.AnyThing is Pawn pawn ? (bool?)pawn.Downed : null))).ToArray())),
                page.Truncated, page.NextCursor, "rows");
        }

        public static BridgeResponse Preview(BridgeRequest request, RouteMatch route)
        {
            DraftBody body = CommandUtil.ReadBody<DraftBody>(request);
            var context = new ReadContext(ReadRequest.FromUri(request.Uri), RequireMap(body.MapId));
            Draft draft = Prepare(context.Map, body);
            return ReadEnvelope.Ok(context, draft.Describe());
        }

        public static BridgeResponse Commit(BridgeRequest request, RouteMatch route)
        {
            DraftBody body = CommandUtil.ReadBody<DraftBody>(request);
            if (string.IsNullOrWhiteSpace(body.ExpectedLoadId) || string.IsNullOrWhiteSpace(body.ExpectedRevision) ||
                string.IsNullOrWhiteSpace(body.MapId))
            {
                throw new CommandException(400, "Pass mapId, expectedLoadId and expectedRevision from the draft preview.");
            }
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            RequestKey key = RequestKey.For("commit_caravan_draft", requestId, null, body.MapId, body.ExpectedRevision,
                body.TargetId, string.Join(",", body.PawnIds ?? Array.Empty<string>()),
                Json.Serialize((body.Supplies ?? Array.Empty<Supply>()).Select(supply =>
                    Dto.Obj(Dto.Field("id", supply?.Id), Dto.Field("count", supply?.Count))).ToArray()));
            // A reform may remove the encounter map. Resolve the map only after checking receipts.
            return CommandReceipts.Once(CommandUtil.ContextFor(null), key, () =>
            {
                Draft draft = Prepare(RequireMap(body.MapId), body);
                if (draft.Revision() != body.ExpectedRevision)
                {
                    throw new CommandException(409, "The caravan draft changed. Preview the current roster and cargo before committing.");
                }
                string blocked = draft.BlockedBy();
                if (blocked != null)
                {
                    throw new CommandException(409, blocked);
                }
                int[] existing = Find.WorldObjects.Caravans.Select(caravan => caravan.ID).ToArray();
                var result = Dto.Obj(Dto.Field("mode", draft.Mode), Dto.Field("revision", body.ExpectedRevision),
                    Dto.Field("originMapId", body.MapId), Dto.Field("pawnIds", draft.Pawns.Select(ReadUtil.ThingId).ToArray()));
                if (!(bool)(draft.Reforming ? Reform : Form).Invoke(draft.Dialog, null))
                {
                    throw new CommandException(409, "RimWorld declined the draft. Check recent game messages for its validation result.");
                }
                if (draft.Reforming)
                {
                    Caravan caravan = Find.WorldObjects.Caravans.FirstOrDefault(each =>
                        each.IsPlayerControlled && !existing.Contains(each.ID) && draft.Pawns.All(each.PawnsListForReading.Contains));
                    result["outcome"] = "reformed";
                    result["caravanId"] = caravan?.ID;
                }
                else
                {
                    Lord lord = draft.Pawns.Select(CaravanFormingUtility.GetFormAndSendCaravanLord).FirstOrDefault(each => each != null);
                    result["outcome"] = "packing";
                    result["formationId"] = lord?.loadID;
                }
                AgentLog.Done(draft.Reforming ? "Reformed the caravan" : "Started packing a caravan");
                return new CommandResult(true, result);
            });
        }

        private static Map RequireMap(string id)
        {
            return GameContext.ResolveMap(id) ?? throw new CommandException(409, "Load or select the caravan's origin map.");
        }

        private static Draft Prepare(Map map, DraftBody body)
        {
            if (body.PawnIds == null || body.PawnIds.Length < 1 || body.PawnIds.Length > 100 ||
                body.PawnIds.Any(string.IsNullOrWhiteSpace) || body.PawnIds.Distinct(StringComparer.Ordinal).Count() != body.PawnIds.Length)
            {
                throw new CommandException(400, "Pass 1 to 100 distinct pawnIds from get_caravan_candidates.");
            }
            Supply[] supplies = body.Supplies ?? Array.Empty<Supply>();
            if (supplies.Length > 100 || supplies.Any(item => item == null || string.IsNullOrWhiteSpace(item.Id) || !item.Count.HasValue || item.Count < 0)
                || supplies.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != supplies.Length)
            {
                throw new CommandException(400, "Pass up to 100 distinct supply group ids with nonnegative counts.");
            }

            Draft draft = new Draft(map);
            var groups = draft.Dialog.transferables.ToDictionary(GroupId, StringComparer.Ordinal);
            var wanted = new HashSet<string>(body.PawnIds, StringComparer.Ordinal);
            foreach (string id in wanted)
            {
                if (!groups.TryGetValue(id, out TransferableOneWay group) || !(group.AnyThing is Pawn pawn) ||
                    CaravanFormingUtility.IsFormingCaravanOrDownedPawnToBeTakenByCaravan(pawn))
                {
                    throw new CommandException(409, "Pawn " + id + " is unavailable for this draft.");
                }
            }
            foreach (TransferableOneWay group in groups.Values.Where(group => group.AnyThing is Pawn))
            {
                group.AdjustTo(wanted.Contains(GroupId(group)) ? 1 : 0);
            }
            foreach (Supply supply in supplies)
            {
                if (!groups.TryGetValue(supply.Id, out TransferableOneWay group) || group.AnyThing is Pawn ||
                    !group.CanAdjustTo(supply.Count.Value).Accepted)
                {
                    throw new CommandException(409, "Supply " + supply.Id + " or its requested count is unavailable.");
                }
                group.AdjustTo(supply.Count.Value);
            }
            draft.Pawns = TransferableUtility.GetPawnsFromTransferables(draft.Dialog.transferables);
            draft.SetDestination(body.TargetId);
            return draft;
        }

        private static string GroupId(TransferableOneWay group)
        {
            return ReadUtil.ThingId(group.AnyThing);
        }

        private sealed class Draft
        {
            private readonly Map map;
            private PlanetTile destination = PlanetTile.Invalid;
            private PlanetTile exit = PlanetTile.Invalid;
            private WorldObject target;
            private bool retainedRoute;
            public readonly Dialog_FormCaravan Dialog;
            public readonly bool Reforming;
            public List<Pawn> Pawns;
            public string Mode => Reforming ? "reform" : "form";

            public Draft(Map map)
            {
                if (AddPawns == null || AddItems == null || Form == null || Reform == null || StartingTile == null ||
                    DestinationTile == null || Food == null || TravelTicks == null)
                {
                    throw new CommandException(409, "Caravan draft hooks are unavailable on this game build.");
                }
                this.map = map;
                Reforming = !map.IsPlayerHome;
                Dialog = new Dialog_FormCaravan(map, reform: Reforming)
                {
                    transferables = new List<TransferableOneWay>()
                };
                AddPawns.Invoke(Dialog, null);
                AddItems.Invoke(Dialog, null);
            }

            public void SetDestination(int? targetId)
            {
                retainedRoute = Reforming && map.retainedCaravanData.HasDestinationTile;
                if (retainedRoute && targetId.HasValue)
                {
                    throw new CommandException(409, "RimWorld retained the previous route. Reform without targetId, then set a new route on the world map.");
                }
                if (retainedRoute)
                {
                    destination = map.retainedCaravanData.destinationTile;
                }
                if (!targetId.HasValue)
                {
                    return;
                }
                target = Find.WorldObjects.AllWorldObjects.FirstOrDefault(each => each.ID == targetId.Value && each.Spawned && each is MapParent);
                if (target == null)
                {
                    throw new CommandException(404, "No spawned destination with that id. Use list_world.");
                }
                destination = target.Tile;
                if (destination == map.Tile || !destination.Valid || !map.Tile.Valid || !destination.LayerDef.SurfaceTiles ||
                    destination.Layer != map.Tile.Layer || !Find.WorldReachability.CanReach(map.Tile, destination))
                {
                    throw new CommandException(409, "The destination must be reachable on the same surface layer and outside the origin tile.");
                }
                exit = CaravanExitMapUtility.BestExitTileToGoTo(destination, map);
                StartingTile.SetValue(Dialog, exit);
                DestinationTile.SetValue(Dialog, destination);
            }

            public string BlockedBy()
            {
                if (Reforming)
                {
                    var comp = map.Parent.GetComponent<FormCaravanComp>();
                    if (comp?.CanReformNow() != true || GenHostility.AnyHostileActiveThreatToPlayer(map, countDormantPawnsAsHostile: true))
                    {
                        return "RimWorld does not currently offer reforming on this map.";
                    }
                    if (ModsConfig.OdysseyActive && (map.listerThings.ThingsOfDef(ThingDefOf.GravEngine).Any() ||
                        map.listerThings.ThingsInGroup(ThingRequestGroup.PassengerShuttle).Any()))
                    {
                        return "Use the in-game reform dialog to confirm leaving the gravship or shuttle.";
                    }
                }
                if (Pawns == null)
                {
                    return null;
                }
                if (!Pawns.Any(pawn => CaravanUtility.IsOwner(pawn, Faction.OfPlayer) && !pawn.Downed))
                {
                    return "Choose at least one standing caravan owner.";
                }
                if ((!Reforming || map.Parent is Settlement) && !destination.Valid)
                {
                    return "Choose a destination from list_world.";
                }
                if (!Reforming && !exit.Valid)
                {
                    return "RimWorld found no exit tile toward the destination.";
                }
                return Dialog.MassUsage > Dialog.MassCapacity ? "The selected load exceeds the party's carrying capacity." : null;
            }

            public string Revision()
            {
                var selected = Dialog.transferables.Where(group => group.CountToTransfer > 0).OrderBy(GroupId, StringComparer.Ordinal)
                    .Select(group => new object[] { GroupId(group), group.CountToTransfer, group.things.Select(thing =>
                        new object[] { ReadUtil.ThingId(thing), thing.stackCount }).ToArray() }).ToArray();
                string value = Json.Serialize(new object[] { GameContext.LoadId, map.uniqueID, Mode, target?.ID,
                    ReadUtil.TileId(destination), ReadUtil.TileId(exit), Dialog.MassUsage, Dialog.MassCapacity, selected });
                using (SHA256 sha = SHA256.Create())
                {
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
                }
            }

            public object Describe()
            {
                string blocked = BlockedBy();
                // The scratch dialog can estimate a retained route. Commit rebuilds the draft and
                // leaves its actual destination unset so RimWorld restores its retained action.
                if (retainedRoute) DestinationTile.SetValue(Dialog, destination);
                var food = ((float days, float rot))Food.GetValue(Dialog);
                int? outbound = destination.Valid ? (int)TravelTicks.GetValue(Dialog) : (int?)null;
                int? returned = null;
                if (destination.Valid && destination != map.Tile && destination.Layer == map.Tile.Layer)
                {
                    using (WorldPath path = destination.Layer.Pather.FindPath(destination, map.Tile, null))
                    {
                        if (path.Found)
                        {
                            int ticksPerMove = CaravanTicksPerMoveUtility.GetTicksPerMove(new CaravanTicksPerMoveUtility.CaravanInfo(Dialog));
                            returned = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(destination, map.Tile,
                                path, 0f, ticksPerMove, Find.TickManager.TicksAbs + (outbound ?? 0));
                        }
                    }
                }
                return Dto.Obj(Dto.Field("mode", Mode), Dto.Field("accepted", blocked == null),
                    Dto.Field("blockedBy", blocked), Dto.Field("revision", Revision()),
                    Dto.Field("pawnIds", Pawns.Select(ReadUtil.ThingId).ToArray()),
                    Dto.Field("supplies", Dialog.transferables.Where(group => !(group.AnyThing is Pawn) && group.CountToTransfer > 0)
                        .Select(group => Dto.Obj(Dto.Field("id", GroupId(group)),
                            Dto.Field("def", group.ThingDef.defName), Dto.Field("count", group.CountToTransfer))).ToArray()),
                    Dto.Field("massUsed", Dialog.MassUsage), Dto.Field("massCapacity", Dialog.MassCapacity),
                    Dto.Field("targetId", target?.ID), Dto.Field("destinationTile", destination.Valid ? ReadUtil.TileId(destination) : null),
                    Dto.Field("retainedRoute", retainedRoute), Dto.Field("estimatedOutboundTicks", outbound),
                    Dto.Field("estimatedReturnTicks", returned),
                    Dto.Field("food", CaravanService.FoodEstimate(food.days, food.rot,
                        outbound.HasValue && returned.HasValue ? outbound + returned : outbound)));
            }
        }

        [DataContract]
        private sealed class DraftBody
        {
            [DataMember(Name = "mapId")] public string MapId { get; set; }
            [DataMember(Name = "pawnIds")] public string[] PawnIds { get; set; }
            [DataMember(Name = "supplies")] public Supply[] Supplies { get; set; }
            [DataMember(Name = "targetId")] public int? TargetId { get; set; }
            [DataMember(Name = "expectedLoadId")] public string ExpectedLoadId { get; set; }
            [DataMember(Name = "expectedRevision")] public string ExpectedRevision { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class Supply
        {
            [DataMember(Name = "id")] public string Id { get; set; }
            [DataMember(Name = "count")] public int? Count { get; set; }
        }
    }
}
