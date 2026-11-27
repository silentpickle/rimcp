using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RiMCP.Bridge;
using UnityEngine;
using Verse;

namespace RiMCP.Read
{
    // The dispatcher calls this on the Unity thread, with a short capture scope per source.
    // Outside that scope the prefixes allow the renderer to proceed normally.
    internal static class EffectCapture
    {
        private static List<Effect> active;
        private static bool installed;
        private static string installFailure;

        internal sealed class Effect
        {
            public string Label;
            public string Color;
            public HashSet<IntVec3> Cells;
        }

        internal static Effect[] Placement(Map map, ThingDef def, IntVec3 cell, Rot4 rotation)
        {
            return Capture(map, () => Ghost(def, cell, rotation, null));
        }

        internal static MapLayer Layer(Map map, string id, CellRect bounds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new BridgeException(400, "The effects layer requires effectThingId.");
            }
            Building building = map.listerThings.AllThings.OfType<Building>().FirstOrDefault(thing =>
                ReadUtil.ThingId(thing) == id && thing.Spawned && !thing.Position.Fogged(map));
            if (building == null)
            {
                throw new BridgeException(404, "The effects source is not an explored, built thing on this map.");
            }
            Effect[] effects = Capture(map, () =>
            {
                if (building.GetType() == typeof(Building_TurretGun) &&
                    building.AllComps.All(comp => comp.GetType().GetMethod(nameof(ThingComp.PostDrawExtraSelectionOverlays))?.DeclaringType == typeof(ThingComp)))
                {
                    building.DrawExtraSelectionOverlays();
                }
                else
                {
                    // A placed cooler and lamp use the same geometry as their placement overlay.
                    Ghost(building.def, building.Position, building.Rotation, null);
                }
            });
            var cells = new Dictionary<IntVec3, List<int>>();
            for (int i = 0; i < effects.Length; i++)
            {
                foreach (IntVec3 cell in effects[i].Cells.Where(bounds.Contains))
                {
                    if (!cells.TryGetValue(cell, out List<int> groups))
                    {
                        groups = new List<int>();
                        cells.Add(cell, groups);
                    }
                    groups.Add(i);
                }
            }
            var legend = new Dictionary<char, string> { { '-', "no effect" }, { '+', "overlapping effects" } };
            for (int i = 0; i < effects.Length; i++)
            {
                legend[(char)('A' + i)] = effects[i].Label + " " + effects[i].Color;
            }
            return new MapLayer("effects", (cell, _) => !cells.TryGetValue(cell, out List<int> groups) ? '-' :
                groups.Count == 1 ? (char)('A' + groups[0]) : '+', '-', legend,
                (cell, _) => cells.TryGetValue(cell, out List<int> groups) ? groups.Select(i => Dto.Obj(
                    Dto.Field("label", effects[i].Label), Dto.Field("color", effects[i].Color))).ToArray() : Array.Empty<object>());
        }

        internal static object Describe(Effect[] effects)
        {
            return effects.Select(effect => Dto.Obj(Dto.Field("label", effect.Label), Dto.Field("color", effect.Color),
                Dto.Field("cellCount", effect.Cells.Count),
                Dto.Field("runs", CellRuns(effect.Cells)))).ToArray();
        }

        private static int[][] CellRuns(IEnumerable<IntVec3> cells)
        {
            var result = new List<int[]>();
            foreach (var row in cells.GroupBy(cell => cell.z).OrderBy(group => group.Key))
            {
                int[] last = null;
                foreach (int x in row.Select(cell => cell.x).OrderBy(x => x))
                {
                    if (last != null && x == last[1] + last[2])
                    {
                        last[2]++;
                    }
                    else
                    {
                        last = new[] { row.Key, x, 1 };
                        result.Add(last);
                    }
                }
            }
            return result.ToArray();
        }

        internal static bool Supports(ThingDef def) => def.PlaceWorkers?.Any(worker => Supported(worker.GetType())) == true;
        private static bool Supported(Type type) => type == typeof(PlaceWorker_Cooler) || type == typeof(PlaceWorker_CoolerSimple) ||
            type == typeof(PlaceWorker_GlowRadius) || type == typeof(PlaceWorker_ShowTurretRadius);

        private static void Ghost(ThingDef def, IntVec3 cell, Rot4 rotation, Thing thing)
        {
            foreach (PlaceWorker worker in def.PlaceWorkers ?? Enumerable.Empty<PlaceWorker>())
            {
                Type type = worker.GetType();
                if (type == typeof(PlaceWorker_ShowTurretRadius))
                {
                    // The native turret worker draws range from its placement check.
                    worker.AllowsPlacing(def, cell, rotation, Find.CurrentMap, thing, thing);
                }
                else if (Supported(type))
                {
                    worker.DrawGhost(def, cell, rotation, Color.white, thing);
                }
            }
        }

        private static Effect[] Capture(Map map, Action draw)
        {
            if (map != Find.CurrentMap)
            {
                throw new BridgeException(409, "Display the effects source's map in RimWorld first.");
            }
            EnsureInstalled();
            active = new List<Effect>();
            try
            {
                draw();
                foreach (Effect effect in active)
                {
                    effect.Cells.RemoveWhere(cell => !cell.InBounds(map) || cell.Fogged(map));
                }
                return active.Where(effect => effect.Cells.Count > 0).ToArray();
            }
            finally
            {
                active = null;
            }
        }

        private static bool FieldEdges(List<IntVec3> __0, Color __1)
        {
            if (active == null)
            {
                return true;
            }
            if (active.Count < 26)
            {
                active.Add(new Effect { Label = Label(__1), Color = "#" + ColorUtility.ToHtmlStringRGBA(__1),
                    Cells = new HashSet<IntVec3>(__0) });
            }
            return false;
        }

        private static string Label(Color color)
        {
            if (color.Equals(GenTemperature.ColorSpotCold)) return "cold cell";
            if (color.Equals(GenTemperature.ColorSpotHot)) return "hot cell";
            if (color.Equals(GenTemperature.ColorRoomCold)) return "cold room";
            if (color.Equals(GenTemperature.ColorRoomHot)) return "hot room";
            if (color.Equals(new Color(1f, 0.7f, 0f, 0.5f))) return "both sides in same room";
            if (color.Equals(new Color(0.8f, 0.8f, 0.4f))) return "light radius";
            return "range";
        }

        private static bool RenderOnly() => active == null;

        private static void EnsureInstalled()
        {
            if (!installed && installFailure == null)
            {
                // Bind Harmony supplied by RimWorld's mod loader. The core bridge and the
                // stand-alone compatibility checks can still run without Harmony.
                Type harmony = AccessToolsType("Harmony");
                Type harmonyMethod = AccessToolsType("HarmonyMethod");
                if (harmony == null || harmonyMethod == null)
                {
                    throw new BridgeException(409, "The effects layer needs the Harmony mod enabled.");
                }
                try
                {
                    object owner = Activator.CreateInstance(harmony, "silentpickle.rimcp.effects");
                    MethodInfo patch = harmony.GetMethods().Single(method => method.Name == "Patch" &&
                        method.GetParameters().Length >= 5 && method.GetParameters()[1].ParameterType == harmonyMethod);
                    void Prefix(string method, Type[] args, string prefix)
                    {
                        MethodInfo original = typeof(GenDraw).GetMethod(method, args) ?? throw new MissingMethodException("GenDraw", method);
                        MethodInfo interceptor = typeof(EffectCapture).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic);
                        var arguments = new object[patch.GetParameters().Length];
                        arguments[0] = original;
                        arguments[1] = Activator.CreateInstance(harmonyMethod, interceptor);
                        patch.Invoke(owner, arguments);
                    }
                    Prefix(nameof(GenDraw.DrawFieldEdges), new[] { typeof(List<IntVec3>), typeof(Color), typeof(float?), typeof(HashSet<IntVec3>), typeof(int) }, nameof(FieldEdges));
                    Prefix(nameof(GenDraw.DrawAimPie), new[] { typeof(Thing), typeof(LocalTargetInfo), typeof(int), typeof(float) }, nameof(RenderOnly));
                    Prefix(nameof(GenDraw.DrawLineBetween), new[] { typeof(Vector3), typeof(Vector3), typeof(Material), typeof(float) }, nameof(RenderOnly));
                    installed = true;
                }
                catch (Exception exception)
                {
                    installFailure = exception.GetBaseException().Message;
                }
            }
            if (!installed)
            {
                throw new BridgeException(409, "Could not bind the game's effects capture: " + installFailure);
            }
        }

        private static Type AccessToolsType(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("HarmonyLib." + name))
                .FirstOrDefault(type => type != null);
        }
    }
}
