using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // Thermostat buildings (heaters, coolers, vents with a target): the target the gizmos nudge, and
    // for a cooler which neighbouring cell it cools and which it heats, as Building_Cooler.TickRare
    // picks them from its rotation.
    internal static class TemperatureControlRead
    {
        private const int Decimals = 1;

        public static object Describe(Building building)
        {
            CompTempControl control = building.TryGetComp<CompTempControl>();
            if (control == null)
            {
                return null;
            }
            Dictionary<string, object> dto = Dto.Obj(Dto.Field("targetC", Round(control.TargetTemperature)));
            if (building is Building_Cooler && building.Spawned)
            {
                dto["cooledCell"] = Cell(building, CooledCell(building.Position, building.Rotation));
                dto["exhaustCell"] = Cell(building, ExhaustCell(building.Position, building.Rotation));
            }
            return dto;
        }

        public static IntVec3 CooledCell(IntVec3 position, Rot4 rotation)
        {
            return position + IntVec3.South.RotatedBy(rotation);
        }

        public static IntVec3 ExhaustCell(IntVec3 position, Rot4 rotation)
        {
            return position + IntVec3.North.RotatedBy(rotation);
        }

        private static object Cell(Building building, IntVec3 cell)
        {
            Map map = building.Map;
            if (!cell.InBounds(map))
            {
                return Dto.Obj(Dto.Field("x", cell.x), Dto.Field("z", cell.z), Dto.Field("inBounds", false));
            }
            return Dto.Obj(
                Dto.Field("x", cell.x),
                Dto.Field("z", cell.z),
                Dto.Field("temperatureC", Round(cell.GetTemperature(map))),
                Dto.Field("impassable", cell.Impassable(map)));
        }

        private static double Round(float value)
        {
            return Math.Round(value, Decimals);
        }
    }
}
