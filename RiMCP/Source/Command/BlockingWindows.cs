using System.Collections.Generic;
using System.Linq;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // Windows that stop the clock whatever speed is selected: TickManager.Paused is true while any
    // open window has forcePause, so research-complete letters and mods' prompts pause the game
    // without changing CurTimeSpeed.
    internal static class BlockingWindows
    {
        public const string Tool = "advance_time";
        private const string Subject = "the game";

        public static List<Window> Now()
        {
            WindowStack stack = Find.WindowStack;
            return stack == null ? new List<Window>() : stack.Windows.Where(window => window.forcePause).ToList();
        }

        public static List<string> Names()
        {
            return Now().Select(window => window.GetType().Name).ToList();
        }

        // Hands each blocking window answer_dialog can drive to it, once; the rest need the player.
        public static void Adopt()
        {
            Adopt(Now());
        }

        private static void Adopt(List<Window> windows)
        {
            foreach (Window window in windows.Where(each => !Interactions.Owns(each)))
            {
                DialogAdapter dialog = DialogAdapter.For(window);
                if (dialog == null)
                {
                    continue;
                }
                Interaction interaction = Interactions.Start(Tool, Subject, dialog);
                AgentLog.WaitFor(interaction.Id);
            }
        }

        // Null when nothing blocks the clock, so callers can omit the field.
        public static object[] Describe()
        {
            return Describe(Now());
        }

        internal static object[] Describe(List<Window> windows)
        {
            if (windows.Count == 0)
            {
                return null;
            }
            Adopt(windows);
            return windows.Select(DescribeOne).ToArray();
        }

        private static object DescribeOne(Window window)
        {
            Interaction owner = Interactions.OwnerOf(window);
            if (owner == null)
            {
                return Dto.Obj(
                    Dto.Field("window", window.GetType().Name),
                    Dto.Field("needsPlayer", true));
            }
            return Dto.Obj(
                Dto.Field("window", window.GetType().Name),
                Dto.Field("needsPlayer", false),
                Dto.Field("dialog", Interactions.Describe(owner)));
        }
    }
}
