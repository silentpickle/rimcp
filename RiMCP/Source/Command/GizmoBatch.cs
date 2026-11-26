using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RiMCP.Bridge;
using RiMCP.Read;
using UnityEngine;
using Verse;

namespace RiMCP.Command
{
    internal static partial class GizmoService
    {
        public const int MaxBatchBuildings = 50;
        private const string BatchTool = "set_building_toggles";

        // Some mods turn a building into another by pressing a toggle: Hospitality's "For guests"
        // destroys the bed and spawns a guest bed in its cell. Reading the old thing then says
        // nothing changed. Report the building now in its place and its toggle state instead.
        private static bool ReportReplacement(Thing pressed, IntVec3 cell, Map map, string label, Dictionary<string, object> data)
        {
            if (pressed.Spawned || map == null || !cell.InBounds(map))
            {
                return false;
            }
            Building replacement = cell.GetThingList(map).OfType<Building>()
                .FirstOrDefault(building => building != pressed && building.Position == cell && building.def.size == pressed.def.size);
            if (replacement == null)
            {
                data["removed"] = true;
                return true;
            }
            data["replacedBy"] = SummaryDto.Thing(replacement);
            data.Remove("note");
            Command_Toggle toggle = Build(replacement).Select(entry => entry.Command).OfType<Command_Toggle>()
                .FirstOrDefault(command => ToggleLabel(command) == label);
            data["on"] = toggle == null || toggle.isActive == null ? null : (object)toggle.isActive();
            return true;
        }

        private static string ToggleLabel(Verse.Command command)
        {
            return (command.LabelCap ?? command.GetType().Name).StripTags();
        }

        // set_building_toggles: one toggle, named by its label, set on several buildings at once,
        // with the checks and mod-button rules of set_building_toggle and use_gizmo. No revision is
        // needed: the label picks the toggle on each building as it is now.
        public static BridgeResponse SetBuildingToggles(BridgeRequest request, RouteMatch route)
        {
            TogglesBody body = CommandUtil.ReadBody<TogglesBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            RequireBatch(body);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            RequestKey key = RequestKey.For(BatchTool, requestId, context.Map, string.Join(",", body.BuildingIds), body.Toggle, body.On);
            return CommandReceipts.Once(context, key, () => ToggleAll(context.Map, body));
        }

        private static void RequireBatch(TogglesBody body)
        {
            if (body.BuildingIds == null || body.BuildingIds.Length == 0 || body.BuildingIds.Any(string.IsNullOrWhiteSpace))
            {
                throw new CommandException(400, "Missing required field 'buildingIds'.");
            }
            if (body.BuildingIds.Length > MaxBatchBuildings)
            {
                throw new CommandException(400, "Give at most " + MaxBatchBuildings + " buildings per call.");
            }
            if (string.IsNullOrWhiteSpace(body.Toggle) || !body.On.HasValue)
            {
                throw new CommandException(400, "Give toggle (its label from list_gizmos) and on.");
            }
        }

        private static CommandResult ToggleAll(Map map, TogglesBody body)
        {
            List<object> results = new List<object>();
            bool changed = false;
            foreach (string id in body.BuildingIds.Distinct())
            {
                BatchOutcome outcome = ToggleChecked(map, id, body.Toggle.Trim(), body.On.Value);
                changed |= outcome.Changed;
                results.Add(outcome.Dto);
            }
            int refused = results.Count(result => ((Dictionary<string, object>)result).ContainsKey("refused"));
            return new CommandResult(changed, Dto.Obj(
                Dto.Field("toggle", body.Toggle.Trim()),
                Dto.Field("on", body.On.Value),
                Dto.Field("set", results.Count - refused),
                Dto.Field("refusedCount", refused),
                Dto.Field("results", results.ToArray())));
        }

        // A refusal comes back as a BridgeException, as the single tools report it; here it becomes
        // this building's outcome so the rest are still set.
        private static BatchOutcome ToggleChecked(Map map, string id, string label, bool on)
        {
            try
            {
                return ToggleOne(map, id, label, on);
            }
            catch (BridgeException ex)
            {
                return new BatchOutcome(false, Dto.Obj(Dto.Field("buildingId", id), Dto.Field("refused", ex.Message), Dto.Field("status", ex.StatusCode)));
            }
        }

        private static BatchOutcome ToggleOne(Map map, string id, string label, bool on)
        {
            Building building = BuildingReadService.FindBuilding(map, id);
            if (building == null)
            {
                throw new CommandException(404, "Building not found.");
            }
            GizmoEntry entry = FindToggle(building, label);
            Dictionary<string, object> data = Dto.Obj(Dto.Field("buildingId", id), Dto.Field("building", SummaryDto.Thing(building)));
            Command_Toggle toggle = (Command_Toggle)entry.Command;
            bool changed = false;
            IntVec3 cell = building.Position;
            AgentLog.About("Turn " + label + " " + (on ? "on" : "off") + " on " + AgentLog.Name(building), building);
            Press.Execute(data, entry.Origin, BatchTool, label, building, map, LocalTargetInfo.Invalid, () => changed = PressToggle(entry, toggle, on, data));
            changed |= ReportReplacement(building, cell, map, label, data);
            Pending(building, data);
            if (changed)
            {
                AgentLog.Done("Turned " + label + " " + (on ? "on" : "off") + " on " + AgentLog.Name(building), building);
            }
            return new BatchOutcome(changed, data);
        }

        // The game's own toggles are set directly; a mod's is pressed as a click, as use_gizmo does.
        private static bool PressToggle(GizmoEntry entry, Command_Toggle toggle, bool on, Dictionary<string, object> data)
        {
            if (!entry.Origin.IsMod)
            {
                return SetToggle(toggle, on, data);
            }
            if (toggle.isActive == null || toggle.isActive() == on)
            {
                data["on"] = on;
                data["alreadySatisfied"] = true;
                return false;
            }
            toggle.ProcessInput(new Event());
            data["on"] = toggle.isActive();
            return true;
        }

        private static GizmoEntry FindToggle(Building building, string label)
        {
            List<GizmoEntry> toggles = Build(building).Where(entry => entry.Kind == "toggle").ToList();
            GizmoEntry entry = toggles.FirstOrDefault(each => string.Equals(Label(each), label, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                throw new CommandException(409, "No toggle '" + label + "'. Toggles: " + string.Join(", ", toggles.Select(Label).ToArray()) + ".");
            }
            if (entry.Command.Disabled)
            {
                throw new CommandException(409, "'" + Label(entry) + "' is unavailable" + (string.IsNullOrEmpty(entry.Command.disabledReason) ? "." : ": " + entry.Command.disabledReason.StripTags()));
            }
            if (entry.Origin.Unsupported != null)
            {
                throw new CommandException(409, "'" + Label(entry) + "' is not supported: " + entry.Origin.Unsupported);
            }
            return entry;
        }

        [DataContract]
        private sealed class TogglesBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "buildingIds")]
            public string[] BuildingIds { get; set; }

            [DataMember(Name = "toggle")]
            public string Toggle { get; set; }

            [DataMember(Name = "on")]
            public bool? On { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
