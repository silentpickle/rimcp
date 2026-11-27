using System;
using System.Diagnostics;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    // Main thread only. Attribute elapsed wall time to the state seen at the previous sample.
    internal sealed class PlayTime
    {
        private const int MetricPrecision = 3;
        private static readonly PlayTime current = new PlayTime();
        private string loadId;
        private long lastSample;
        private bool wasRunning;
        private long wall;
        private long running;

        public static void Sample()
        {
            TickManager ticks = Current.ProgramState == ProgramState.Playing && Current.Game != null
                ? Find.TickManager
                : null;
            current.Observe(ticks == null ? null : GameContext.LoadId, Stopwatch.GetTimestamp(),
                ticks != null && BridgeRuntime.MainThreadAvailable && !ticks.Paused);
        }

        public static object Describe()
        {
            Sample();
            return current.loadId == null ? null : Dto.Obj(
                Dto.Field("wallSeconds", Math.Round((double)current.wall / Stopwatch.Frequency, MetricPrecision)),
                Dto.Field("runningWallShare", current.RunningShare));
        }

        internal double? RunningShare
        {
            get { return wall == 0 ? (double?)null : Math.Round((double)running / wall, MetricPrecision); }
        }

        internal void Observe(string load, long timestamp, bool isRunning)
        {
            if (load != loadId)
            {
                loadId = load;
                wall = 0;
                running = 0;
            }
            else if (load != null)
            {
                long elapsed = timestamp - lastSample;
                wall += elapsed;
                running += wasRunning ? elapsed : 0;
            }
            lastSample = timestamp;
            wasRunning = isRunning;
        }
    }
}
