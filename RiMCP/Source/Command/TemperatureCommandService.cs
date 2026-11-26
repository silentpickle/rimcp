using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // Sets a thermostat's target directly, instead of nudging it with the gizmos' relative steps.
    internal static class TemperatureCommandService
    {
        // CompTempControl.InterfaceChangeTargetTemperature clamps to this range.
        public const float MinTargetC = -273.15f;
        public const float MaxTargetC = 1000f;
        private const string Tool = "set_target_temperature";

        public static BridgeResponse SetTargetTemperature(BridgeRequest request, RouteMatch route)
        {
            TemperatureBody body = CommandUtil.ReadBody<TemperatureBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            float target = RequireTarget(body.TargetC);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            RequestKey key = RequestKey.For(Tool, requestId, context.Map, route["buildingId"], target);
            return CommandReceipts.Once(context, key, () => Apply(context.Map, route["buildingId"], target));
        }

        private static float RequireTarget(float? targetC)
        {
            if (!targetC.HasValue)
            {
                throw new CommandException(400, "Missing required field 'targetC'.");
            }
            float value = targetC.Value;
            if (float.IsNaN(value) || value < MinTargetC || value > MaxTargetC)
            {
                throw new CommandException(400, "Field 'targetC' must be from " + MinTargetC.ToString(CultureInfo.InvariantCulture) + " to " + MaxTargetC.ToString(CultureInfo.InvariantCulture) + ".");
            }
            return value;
        }

        private static CommandResult Apply(Map map, string buildingId, float target)
        {
            Building building = BuildingReadService.FindBuilding(map, buildingId);
            if (building == null)
            {
                throw new CommandException(404, "Building not found.");
            }
            CompTempControl control = building.TryGetComp<CompTempControl>();
            if (control == null)
            {
                throw new CommandException(409, building.LabelCap + " has no target temperature.");
            }
            float previous = control.TargetTemperature;
            string label = target.ToStringTemperature("F0");
            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("building", SummaryDto.Thing(building)),
                Dto.Field("previousC", previous));
            if (previous == target)
            {
                AgentLog.NoChange(AgentLog.Name(building) + " was already set to " + label, building);
                data["temperatureControl"] = TemperatureControlRead.Describe(building);
                return new CommandResult(false, data);
            }
            control.TargetTemperature = target;
            AgentLog.Done("Set " + AgentLog.Name(building) + " to " + label, building);
            data["temperatureControl"] = TemperatureControlRead.Describe(building);
            return new CommandResult(true, data);
        }

        [DataContract]
        private sealed class TemperatureBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "targetC")]
            public float? TargetC { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
