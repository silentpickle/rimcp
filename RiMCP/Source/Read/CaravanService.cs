using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using RiMCP.Bridge;
using RiMCP.Command;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RiMCP.Read
{
    internal static class CaravanService
    {
        public static BridgeResponse List(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            var caravans = Find.World == null ? Enumerable.Empty<Caravan>() : Find.WorldObjects.Caravans
                .Where(caravan => caravan.IsPlayerControlled).OrderBy(caravan => caravan.ID);
            Page<Caravan> page = new Page<Caravan>(caravans, context.Request);
            return ReadEnvelope.Ok(context, Dto.Obj(Dto.Field("caravans",
                page.Items.Select(caravan => Serialize(caravan, context.Request.Wants("inventory"))).ToArray()),
                Dto.Field("forming", Formations().Take(20).Select(lord =>
                {
                    var job = (LordJob_FormAndSendCaravan)lord.LordJob;
                    return Dto.Obj(Dto.Field("id", lord.loadID), Dto.Field("mapId", lord.Map.uniqueID.ToString()),
                        Dto.Field("status", job.Status), Dto.Field("paused", lord.CurLordToil is LordToil_PrepareCaravan_Pause),
                        Dto.Field("pawnIds", lord.ownedPawns.Concat(job.downedPawns).Select(ReadUtil.ThingId).ToArray()));
                }).ToArray())),
                page.Truncated, page.NextCursor, "caravans");
        }

        public static BridgeResponse Preview(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            Caravan caravan = RequireCaravan(route.Values["id"]);
            WorldObject target = RequireTarget(context.Request.OptionalInt("targetId"));
            string action = context.Request.Get("action");
            Arrival(caravan, target, action, out string reason);
            RouteEstimate outcome = reason == null ? EstimateRoute(caravan, target.Tile) : null;
            reason = reason ?? (outcome != null ? null : "No caravan route to that world object.");
            return ReadEnvelope.Ok(context, RouteResult(caravan, target, action, outcome, reason));
        }

        public static BridgeResponse SetRoute(BridgeRequest request, RouteMatch route)
        {
            RouteBody body = CommandUtil.ReadBody<RouteBody>(request);
            if (string.IsNullOrWhiteSpace(body.ExpectedLoadId))
            {
                throw new CommandException(400, "Pass expectedLoadId from preview_caravan_route.");
            }
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            RequestKey key = RequestKey.For("set_caravan_route", requestId, null, route["id"], body.TargetId, body.Action);
            return CommandReceipts.Once(CommandUtil.ContextFor(null), key, () => ApplyRoute(route["id"], body));
        }

        private static CommandResult ApplyRoute(string caravanId, RouteBody body)
        {
            Caravan caravan = RequireCaravan(caravanId);
            WorldObject target = RequireTarget(body.TargetId);
            CaravanArrivalAction arrival = Arrival(caravan, target, body.Action, out string reason);
            RouteEstimate outcome = reason == null ? EstimateRoute(caravan, target.Tile) : null;
            if (outcome == null)
            {
                throw new CommandException(409, reason ?? "No caravan route to that world object.");
            }
            var result = RouteResult(caravan, target, body.Action, outcome, null);
            string log = "Ordered " + caravan.LabelCap + " to " + arrival.Label;
            if (!caravan.pather.StartPath(target.Tile, arrival, repathImmediately: true))
            {
                throw new CommandException(409, "The game declined the caravan route.");
            }
            AgentLog.Done(log);
            result["caravanOnWorldMap"] = caravan.Spawned;
            return new CommandResult(true, result);
        }

        private static Caravan RequireCaravan(string value)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            {
                throw new BridgeException(400, "Use a numeric caravan id from list_caravans.");
            }
            Caravan caravan = Find.World == null ? null : Find.WorldObjects.Caravans
                .FirstOrDefault(each => each.ID == id && each.Spawned && each.IsPlayerControlled);
            return caravan ?? throw new BridgeException(404, "No active player caravan with that id.");
        }

        private static WorldObject RequireTarget(int? id)
        {
            if (!id.HasValue)
            {
                throw new BridgeException(400, "Give a targetId from list_world.");
            }
            WorldObject target = Find.WorldObjects.AllWorldObjects.FirstOrDefault(each =>
                each.ID == id.Value && each.Spawned && (each is MapParent));
            return target ?? throw new BridgeException(404, "No spawned map destination with that id.");
        }

        private static CaravanArrivalAction Arrival(Caravan caravan, WorldObject target, string action, out string reason)
        {
            Settlement settlement = target as Settlement;
            CaravanArrivalAction arrival;
            switch (action)
            {
                case "enter":
                    arrival = new CaravanArrivalAction_Enter((MapParent)target);
                    break;
                case "visit":
                    arrival = settlement == null ? null : new CaravanArrivalAction_VisitSettlement(settlement);
                    break;
                case "visitSite":
                    arrival = target is Site site ? new CaravanArrivalAction_VisitSite(site) : null;
                    break;
                case "attack":
                    arrival = settlement?.Faction?.HostileTo(Faction.OfPlayer) == true
                        ? new CaravanArrivalAction_AttackSettlement(settlement) : null;
                    break;
                default:
                    throw new BridgeException(400, "Field 'action' must be enter, visit, visitSite, or attack.");
            }
            if (arrival == null)
            {
                reason = action == "attack" ? "Attack requires an already-hostile settlement." : "This destination does not support that action.";
                return null;
            }
            FloatMenuAcceptanceReport acceptance = arrival.StillValid(caravan, target.Tile);
            reason = acceptance.Accepted ? null : acceptance.FailReason ?? acceptance.FailMessage ?? "The game does not offer that action here.";
            return arrival;
        }

        internal static IEnumerable<Verse.AI.Group.Lord> Formations()
        {
            return Find.Maps == null ? Enumerable.Empty<Verse.AI.Group.Lord>() :
                Find.Maps.SelectMany(map => map.lordManager.lords).Where(lord => lord.LordJob is LordJob_FormAndSendCaravan);
        }

        internal static int Hungry(Caravan caravan)
        {
            return caravan.PawnsListForReading.Count(pawn => pawn.needs?.food?.CurCategory >= HungerCategory.UrgentlyHungry);
        }

        internal static string Immobilized(Caravan caravan)
        {
            if (caravan.ImmobilizedByMass) return "overweight";
            if (caravan.AllOwnersDowned) return "allOwnersDowned";
            if (caravan.AllOwnersHaveMentalBreak) return "allOwnersInMentalBreak";
            return null;
        }

        internal static object FoodEstimate(float days, float rot, int? ticks)
        {
            double? travelDays = ticks / (double)GenDate.TicksPerDay;
            bool foodCapped = days >= DaysWorthOfFoodCalculator.InfiniteDaysWorthOfFood;
            bool rotCapped = rot >= DaysUntilRotCalculator.InfiniteDaysUntilRot;
            return Dto.Obj(Dto.Field("estimatedFoodDays", days), Dto.Field("foodEstimateCapped", foodCapped),
                Dto.Field("estimatedDaysUntilMostFoodRots", rotCapped ? (float?)null : rot),
                Dto.Field("travelDays", travelDays),
                Dto.Field("foodMayRunOut", travelDays.HasValue && !foodCapped ? (bool?)(days < travelDays) : null),
                Dto.Field("mostFoodMayRotBeforeArrival", travelDays.HasValue && !rotCapped ? (bool?)(rot < travelDays) : null));
        }

        private sealed class RouteEstimate
        {
            public int Ticks;
            public object Food;
        }

        private static RouteEstimate EstimateRoute(Caravan caravan, PlanetTile tile)
        {
            if (!caravan.Tile.Valid || !tile.Valid || !caravan.Tile.LayerDef.SurfaceTiles
                || caravan.Tile.Layer != tile.Layer || !caravan.CanReach(tile))
            {
                return null;
            }
            if (caravan.Tile == tile)
            {
                var current = caravan.DaysWorthOfFood;
                return new RouteEstimate { Food = FoodEstimate(current.days, current.tillRot, 0) };
            }
            using (WorldPath path = caravan.Tile.Layer.Pather.FindPath(caravan.Tile, tile, caravan))
            {
                if (!path.Found)
                {
                    return null;
                }
                int perMove = caravan.TicksPerMove;
                int ticks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan.Tile, tile, path, 0f, perMove, Find.TickManager.TicksAbs);
                var transfers = caravan.PawnsListForReading.Select(pawn =>
                {
                    var group = new TransferableOneWay();
                    group.things.Add(pawn);
                    group.AdjustTo(1);
                    return group;
                }).ToList();
                float days = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(transfers, caravan.Tile, IgnorePawnsInventoryMode.DontIgnore,
                    caravan.Faction, path, 0f, perMove);
                float rot = DaysUntilRotCalculator.ApproxDaysUntilRot(CaravanInventoryUtility.AllInventoryItems(caravan), caravan.Tile, path, 0f, perMove);
                return new RouteEstimate { Ticks = ticks, Food = FoodEstimate(days, rot, ticks) };
            }
        }

        private static Dictionary<string, object> RouteResult(Caravan caravan, WorldObject target, string action, RouteEstimate estimate, string reason)
        {
            return Dto.Obj(
                Dto.Field("caravanId", caravan.ID),
                Dto.Field("targetId", target.ID),
                Dto.Field("target", target.LabelCap),
                Dto.Field("targetTile", ReadUtil.TileId(target.Tile)),
                Dto.Field("action", action),
                Dto.Field("accepted", reason == null),
                Dto.Field("reason", reason),
                Dto.Field("estimatedTravelTicks", estimate?.Ticks),
                Dto.Field("food", estimate?.Food),
                Dto.Field("immobilizedByMass", caravan.ImmobilizedByMass));
        }

        private static object Serialize(Caravan caravan, bool inventory)
        {
            var food = caravan.DaysWorthOfFood;
            var pather = caravan.pather;
            int? travel = pather.Moving && pather.curPath != null
                ? CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan, allowCaching: true) : (int?)null;
            var dto = Dto.Obj(
                Dto.Field("id", caravan.ID),
                Dto.Field("label", caravan.LabelCap),
                Dto.Field("tile", ReadUtil.TileId(caravan.Tile)),
                Dto.Field("destinationTile", pather.Moving ? ReadUtil.TileId(pather.Destination) : null),
                Dto.Field("arrivalAction", pather.ArrivalAction?.Label),
                Dto.Field("hasRoute", pather.Moving),
                Dto.Field("movingNow", pather.MovingNow),
                Dto.Field("paused", pather.Paused),
                Dto.Field("nightResting", caravan.NightResting),
                Dto.Field("allOwnersDowned", caravan.AllOwnersDowned),
                Dto.Field("allOwnersHaveMentalBreak", caravan.AllOwnersHaveMentalBreak),
                Dto.Field("immobilizedByMass", caravan.ImmobilizedByMass),
                Dto.Field("massUsed", caravan.MassUsage),
                Dto.Field("massCapacity", caravan.MassCapacity),
                Dto.Field("foodEstimateCapped", food.days >= DaysWorthOfFoodCalculator.InfiniteDaysWorthOfFood),
                Dto.Field("estimatedFoodDays", food.days),
                Dto.Field("estimatedTicksToArrive", travel),
                Dto.Field("urgentlyHungry", Hungry(caravan)),
                Dto.Field("immobilizedBy", Immobilized(caravan)),
                Dto.Field("food", FoodEstimate(food.days, food.tillRot, travel)),
                Dto.Field("pawns", caravan.PawnsListForReading.Select(pawn => Dto.Obj(
                    Dto.Field("id", ReadUtil.ThingId(pawn)),
                    Dto.Field("name", ReadUtil.PawnName(pawn)),
                    Dto.Field("downed", pawn.Downed))).ToArray()));
            if (inventory)
            {
                dto["inventory"] = CaravanInventoryUtility.AllInventoryItems(caravan).GroupBy(thing => thing.def.defName)
                    .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => Dto.Obj(
                        Dto.Field("def", group.Key), Dto.Field("count", group.Sum(thing => thing.stackCount)))).ToArray();
            }
            return dto;
        }

        [DataContract]
        private sealed class RouteBody
        {
            [DataMember(Name = "targetId")] public int? TargetId { get; set; }
            [DataMember(Name = "action")] public string Action { get; set; }
            [DataMember(Name = "expectedLoadId")] public string ExpectedLoadId { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }
    }
}
