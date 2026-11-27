using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Shared;
using UnityEngine;
using Verse;

namespace RiMCP.Command
{
    internal static partial class GizmoService
    {
        private const string StepperTool = "set_stepper";
        private static readonly Regex NumberAndUnit = new Regex(
            @"^([+\-−]?\s*\d{1,9}(?:[.,]\d{1,6})?)\s*([^\d]*)$", RegexOptions.CultureInvariant);

        private sealed class StepButton
        {
            public GizmoEntry Entry;
            public decimal Delta;
        }

        private sealed class Stepper
        {
            public string Id;
            public string Unit;
            public ThingComp Comp;
            public GizmoEntry Reset;
            public StepButton[] Buttons;

            public Dictionary<string, decimal> Read()
            {
                var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string text;
                try
                {
                    text = Comp.CompInspectStringExtra()?.StripTags() ?? "";
                }
                catch (Exception ex)
                {
                    BridgeRouter.LogFailure(ex);
                    return values;
                }
                foreach (string line in text.Split('\n'))
                {
                    int separator = line.IndexOf(':');
                    if (separator <= 0 || !TryNumber(line.Substring(separator + 1), out decimal value, out string unit) || unit != Unit)
                    {
                        continue;
                    }
                    string name = line.Substring(0, separator).Trim();
                    if (values.ContainsKey(name))
                    {
                        ambiguous.Add(name);
                    }
                    values[name] = value;
                }
                foreach (string key in ambiguous)
                {
                    values.Remove(key);
                }
                return values;
            }

            public object Describe()
            {
                Dictionary<string, decimal> values = Read();
                return Dto.Obj(
                    Dto.Field("id", Id),
                    Dto.Field("component", Comp.GetType().FullName),
                    Dto.Field("unit", Unit),
                    Dto.Field("inspectValues", values),
                    Dto.Field("steps", Buttons.Select(button => Dto.Obj(
                        Dto.Field("gizmo", button.Entry.Id), Dto.Field("delta", button.Delta))).ToArray()),
                    Dto.Field("resetGizmo", Reset?.Id));
            }
        }

        private static IEnumerable<Stepper> Steppers(Thing thing, List<GizmoEntry> gizmos)
        {
            if (!(thing is ThingWithComps owner))
            {
                yield break;
            }
            var actions = gizmos.Where(entry => entry.Command is Command_Action && entry.Origin.Unsupported == null)
                .Select(entry => new { Entry = entry, Comp = OwnerComp(owner, (Command_Action)entry.Command) })
                .Where(action => action.Comp != null)
                .GroupBy(action => action.Comp);
            foreach (var group in actions)
            {
                var offsets = new Dictionary<string, List<StepButton>>(StringComparer.Ordinal);
                foreach (var action in group)
                {
                    string label = Label(action.Entry).Trim();
                    if (!(label.StartsWith("+", StringComparison.Ordinal) || label.StartsWith("-", StringComparison.Ordinal) ||
                        label.StartsWith("−", StringComparison.Ordinal)) ||
                        !TryNumber(label, out decimal delta, out string unit) || delta == 0)
                    {
                        continue;
                    }
                    if (!offsets.TryGetValue(unit, out List<StepButton> buttons))
                    {
                        offsets[unit] = buttons = new List<StepButton>();
                    }
                    buttons.Add(new StepButton { Entry = action.Entry, Delta = delta });
                }
                foreach (var pair in offsets)
                {
                    if (!pair.Value.Any(button => button.Delta < 0) || !pair.Value.Any(button => button.Delta > 0))
                    {
                        continue;
                    }
                    yield return new Stepper
                    {
                        Id = pair.Value[0].Entry.Id,
                        Comp = group.Key,
                        Unit = pair.Key,
                        Buttons = pair.Value.ToArray(),
                        Reset = group.Select(action => action.Entry).FirstOrDefault(entry =>
                            Label(entry).Equals("Reset", StringComparison.OrdinalIgnoreCase) ||
                            Label(entry).Equals("CommandResetTemp".Translate().ToString(), StringComparison.OrdinalIgnoreCase))
                    };
                }
            }
        }

        private static ThingComp OwnerComp(ThingWithComps owner, Command_Action command)
        {
            Delegate action = command.action;
            if (action?.Target == null || action.GetInvocationList().Length != 1)
            {
                return null;
            }
            if (action.Target is ThingComp direct)
            {
                return direct.parent == owner ? direct : null;
            }
            if (action.Method.DeclaringType?.DeclaringType == null)
            {
                return null;
            }
            ThingComp[] captured = action.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(field => typeof(ThingComp).IsAssignableFrom(field.FieldType))
                .Select(field => field.GetValue(action.Target) as ThingComp)
                .Where(comp => comp?.parent == owner).Distinct().Take(2).ToArray();
            return captured.Length == 1 ? captured[0] : null;
        }

        private static bool TryNumber(string text, out decimal value, out string unit)
        {
            Match match = NumberAndUnit.Match(text.Trim());
            unit = match.Groups[2].Value.Trim();
            value = 0;
            return match.Success && decimal.TryParse(match.Groups[1].Value.Replace("−", "-").Replace(" ", "").Replace(',', '.'),
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        public static BridgeResponse SetStepper(BridgeRequest request, RouteMatch route)
        {
            StepperBody body = CommandUtil.ReadBody<StepperBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            if (string.IsNullOrWhiteSpace(body.Revision) || string.IsNullOrWhiteSpace(body.Stepper) || !body.Target.HasValue ||
                Math.Abs(body.Target.Value) > 1000000000m)
            {
                throw new CommandException(400, "Give revision and stepper from list_gizmos, and a numeric target within ±1000000000.");
            }
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            string id = route["thingId"];
            RequestKey key = RequestKey.For(StepperTool, requestId, context.Map, id, body.Revision, body.Stepper, body.Target, body.InspectLabel);
            return CommandReceipts.Once(context, key, () => MoveStepper(context.Map, id, body));
        }

        private static CommandResult MoveStepper(Map map, string thingId, StepperBody body)
        {
            Thing thing = Owner(map, thingId);
            List<GizmoEntry> gizmos = Build(thing);
            if (Revision(thing, gizmos) != body.Revision)
            {
                throw new CommandException(409, "The buttons changed. Read list_gizmos again.");
            }
            Stepper stepper = Steppers(thing, gizmos).FirstOrDefault(candidate => candidate.Id == body.Stepper);
            if (stepper == null)
            {
                throw new CommandException(404, "No such stepper on this thing.");
            }
            Dictionary<string, decimal> values = stepper.Read();
            string label = body.InspectLabel ?? (values.Count == 1 ? values.Keys.Single() : null);
            if (label == null || !values.TryGetValue(label, out decimal start))
            {
                throw new CommandException(409, "Give an unambiguous inspectLabel from the stepper's inspectValues.");
            }
            decimal target = body.Target.Value;
            StepButton[] buttons = stepper.Buttons.Where(button => !button.Entry.Command.Disabled).ToArray();
            List<StepButton> plan = PlanSteps(target - start, buttons);
            if (plan == null)
            {
                throw new CommandException(409, "The available buttons cannot reach that target within " + GizmoShape.MaxTimes + " presses.");
            }
            foreach (StepButton button in plan.Distinct())
            {
                Press.Check(button.Entry.Origin);
            }

            string subject = label + " on " + AgentLog.Name(thing);
            var data = Dto.Obj(Dto.Field("thing", SummaryDto.Thing(thing)), Dto.Field("inspectLabel", label),
                Dto.Field("unit", stepper.Unit), Dto.Field("before", start), Dto.Field("target", target));
            AgentLog.About("Set " + subject, thing);
            decimal? current = start;
            int pressed = 0;
            foreach (StepButton button in plan)
            {
                pressed++;
                decimal expected = current.Value + button.Delta;
                if (!PressRepeated(data, button.Entry, StepperTool, thing, map))
                {
                    current = null;
                    break;
                }
                current = stepper.Read().TryGetValue(label, out decimal actual) ? actual : (decimal?)null;
                if (current != expected)
                {
                    data["stopped"] = "The reported value did not move by the button's advertised step.";
                    break;
                }
            }
            data["presses"] = pressed;
            data["value"] = current;
            data["reached"] = current == target;
            if (pressed == 0)
            {
                AgentLog.NoChange(subject + " already matched the target", thing);
            }
            else if (current == target)
            {
                Press.Report(data, "Set " + subject, thing);
            }
            else
            {
                AgentLog.Partly("Pressed " + subject, data["stopped"] as string, thing);
            }
            return new CommandResult(pressed > 0, data);
        }

        // Breadth first search gives a shortest exact sequence. Bound both the depth and state
        // count so a mod with many different offsets cannot monopolize the game thread.
        private static List<StepButton> PlanSteps(decimal delta, StepButton[] buttons)
        {
            var trace = new Dictionary<decimal, StepButton> { { 0, null } };
            var queue = new Queue<(decimal offset, int depth)>();
            queue.Enqueue((0, 0));
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current.offset == delta)
                {
                    var plan = new List<StepButton>();
                    for (decimal offset = delta; trace[offset] is StepButton step; offset -= step.Delta)
                    {
                        plan.Add(step);
                    }
                    plan.Reverse();
                    return plan;
                }
                if (current.depth == GizmoShape.MaxTimes)
                {
                    continue;
                }
                foreach (StepButton button in buttons)
                {
                    decimal next = current.offset + button.Delta;
                    if (trace.ContainsKey(next))
                    {
                        continue;
                    }
                    if (trace.Count >= 12000)
                    {
                        return null;
                    }
                    trace.Add(next, button);
                    queue.Enqueue((next, current.depth + 1));
                }
            }
            return null;
        }

        private static string GizmoInspectText(Thing thing)
        {
            try
            {
                return ReadProjection.InspectText(thing);
            }
            catch (Exception ex)
            {
                BridgeRouter.LogFailure(ex);
                return null;
            }
        }

        private static bool PressRepeated(Dictionary<string, object> data, GizmoEntry entry, string tool, Thing thing, Map map)
        {
            try
            {
                Press.Execute(data, entry.Origin, tool, Label(entry), thing, map, LocalTargetInfo.Invalid,
                    () => entry.Command.ProcessInput(new Event()));
            }
            catch (Exception ex)
            {
                // A click may already have taken effect before throwing. Return a receipt so a
                // retry does not click it again.
                if (!(ex is BridgeException))
                {
                    BridgeRouter.LogFailure(ex);
                }
                data["stopped"] = ex is BridgeException ? ex.Message : "The button threw " + ex.GetType().Name + ".";
                return false;
            }
            if (data.ContainsKey("targeting") || data.ContainsKey("dialogs") || data.ContainsKey("windowsLeftForPlayer") || !thing.Spawned)
            {
                data["stopped"] = "The button opened an interaction or removed the selected thing.";
                return false;
            }
            return true;
        }

        [DataContract]
        private sealed class StepperBody
        {
            [DataMember(Name = "mapId")] public string MapId { get; set; }
            [DataMember(Name = "revision")] public string Revision { get; set; }
            [DataMember(Name = "stepper")] public string Stepper { get; set; }
            [DataMember(Name = "target")] public decimal? Target { get; set; }
            [DataMember(Name = "inspectLabel")] public string InspectLabel { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }
    }
}
