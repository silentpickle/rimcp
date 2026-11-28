using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using RimWorld;
using Verse;

// The naming adapter, a run's stop history in the compact digest, and list_work's schedule blocks.
internal static partial class Program
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string FactionName = "Unium Individualistic Alliance";
    private const string SettlementName = "Tedshobrad";
    private const string RefusalKey = "PlayerFactionNameIsInvalid";
    private const int NameLimit = 64;
    private const int RunStartTick = 100;
    private const int RunStopTick = 150;
    private const int Midnight = 0;
    private const int SleepFrom = 22;
    private const int SleepTo = 6;
    private const int HoursPerDay = 24;
    private const int LastHour = HoursPerDay - 1;

    // A naming window whose naming is recorded instead of applied to a game.
    private sealed class RegressionNaming : Dialog_GiveName
    {
        public string Rejected = "";
        public string GivenFirst;
        public string GivenSecond;

        protected override bool IsValidName(string s) => !string.IsNullOrEmpty(s) && s != Rejected;

        protected override bool IsValidSecondName(string s) => !string.IsNullOrEmpty(s);

        protected override void Named(string s) => GivenFirst = s;

        protected override void NamedSecond(string s) => GivenSecond = s;
    }

    private sealed class NamingStage
    {
        public RegressionNaming Dialog;
        public bool Open = true;
        public List<string> Notices = new List<string>();
        public object Interaction;
    }

    private static void RegisterHarnessFollowUpChecks()
    {
        Test("D1_naming_accept", CheckNamingAccept);
        Test("D1_naming_refusal", CheckNamingRefusal);
        Test("D1_naming_edit", CheckNamingEdit);
        Test("D2_stop_history", CheckStopHistory);
        Test("D3_schedule_blocks", CheckScheduleBlocks);
    }

    private static NamingStage StageNaming(string first, string second)
    {
        var dialog = (RegressionNaming)FormatterServices.GetUninitializedObject(typeof(RegressionNaming));
        dialog.Rejected = "";
        Action<string, object> set = (name, value) => typeof(Dialog_GiveName).GetField(name, Members).SetValue(dialog, value);
        set("curName", first);
        set("curSecondName", second);
        set("useSecondName", second != null);
        set("invalidNameMessageKey", RefusalKey);
        set("gainedNameMessageKey", "PlayerFactionAndBaseGainsName");
        var stage = new NamingStage { Dialog = dialog };
        Type naming = mod.GetType("RiMCP.Command.NamingDialog", true);
        Type hostType = naming.GetNestedType("Host", Members);
        object host = Activator.CreateInstance(hostType, true);
        Action<string, object> hostSet = (name, value) => hostType.GetField(name).SetValue(host, value);
        hostSet("IsOpen", (Func<bool>)(() => stage.Open));
        hostSet("Covered", (Func<bool>)(() => false));
        hostSet("Close", (Action)(() => stage.Open = false));
        hostSet("Notify", (Action<string>)(text => stage.Notices.Add(text)));
        hostSet("Translate", (Func<string, object[], string>)((key, args) => key));
        MethodInfo build = naming.GetMethods(Static).Single(method => method.Name == "For" && method.GetParameters().Length == 2);
        object adapter = build.Invoke(null, new[] { dialog, host });
        Type runner = mod.GetType("RiMCP.Command.Interactions", true);
        runner.GetMethod("Reset", Static).Invoke(null, null);
        stage.Interaction = runner.GetMethod("Start", Static).Invoke(null, new object[] { "advance_time", "the game", adapter, null, null });
        return stage;
    }

    private static Dictionary<string, object> DescribeInteraction(object interaction)
    {
        return (Dictionary<string, object>)mod.GetType("RiMCP.Command.Interactions", true).GetMethod("Describe", Static).Invoke(null, new[] { interaction });
    }

    private static void CheckNamingAccept()
    {
        NamingStage stage = StageNaming("  " + FactionName + " ", SettlementName);
        Dictionary<string, object> shown = DescribeInteraction(stage.Interaction);
        var fields = ((IEnumerable)shown["fields"]).Cast<Dictionary<string, object>>().ToList();
        var option = ((IEnumerable)shown["options"]).Cast<Dictionary<string, object>>().Single();
        Check((string)shown["kind"] == "naming" && !(bool)shown["canCancel"], "a naming window is answerable and cannot be escaped");
        Check(fields.Select(field => (string)field["value"]).SequenceEqual(new[] { FactionName, SettlementName }), "the displayed names are reported trimmed");
        Check((int)fields[0]["maxLength"] == NameLimit && (string)fields[1]["role"] == "secondName", "each name has its limit and role");
        Check((string)option["id"] == "accept" && ((string)option["text"]).Contains(FactionName) && ((string)option["text"]).Contains(SettlementName), "the accept option names what it accepts");

        AnswerPage(stage.Interaction, Field<int>(stage.Interaction, "Page"), "accept", null);
        Check(stage.Dialog.GivenFirst == FactionName && stage.Dialog.GivenSecond == SettlementName, "accepting gives both names through the window's own naming");
        Check(!stage.Open && stage.Notices.Count == 1, "accepting closes the window and sends the game's message once");
        Check(Field<string>(stage.Interaction, "State") == "answered", "the interaction ends as answered");
        ExpectCommandError(409, () => Invoke("RiMCP.Command.Interactions", "Cancel", stage.Interaction, Field<int>(stage.Interaction, "Page")));
    }

    private static void CheckNamingRefusal()
    {
        NamingStage stage = StageNaming(FactionName, SettlementName);
        stage.Dialog.Rejected = FactionName;
        int page = Field<int>(stage.Interaction, "Page");
        ExpectCommandError(409, () => AnswerPage(stage.Interaction, page, "accept", null));
        Check(stage.Dialog.GivenFirst == null && stage.Dialog.GivenSecond == null && stage.Open, "a name the window's validator refuses names nothing and leaves it open");
        ExpectCommandError(409, () => Invoke("RiMCP.Command.Interactions", "Cancel", stage.Interaction, page));
        Check(Field<string>(stage.Interaction, "State") == "open", "a refused or escaped naming window stays with the agent");
    }

    private static void CheckNamingEdit()
    {
        NamingStage stage = StageNaming(FactionName, SettlementName);
        int read = Field<int>(stage.Interaction, "Page");
        typeof(Dialog_GiveName).GetField("curSecondName", Members).SetValue(stage.Dialog, "Elsewhere");
        mod.GetType("RiMCP.Command.Interactions", true).GetMethod("Update", Static).Invoke(null, null);
        int now = Field<int>(stage.Interaction, "Page");
        Check(now > read, "a name the player edits makes a new page");
        ExpectCommandError(409, () => AnswerPage(stage.Interaction, read, "accept", null));
        Check(stage.Dialog.GivenFirst == null, "an answer read before the edit accepts nothing");
        AnswerPage(stage.Interaction, now, "accept", null);
        Check(stage.Dialog.GivenSecond == "Elsewhere", "the new page accepts the names now shown");
        mod.GetType("RiMCP.Command.Interactions", true).GetMethod("Reset", Static).Invoke(null, null);
    }

    private static void CheckStopHistory()
    {
        object run = AdvanceRun(null, Observation(null, RunStartTick, TimeSpeed.Superfast));
        MethodInfo describe = mod.GetType("RiMCP.Read.DigestReadService", true).GetMethod("DescribeAdvance", Static);
        var running = (Dictionary<string, object>)describe.Invoke(null, new[] { run });
        Check(!running.ContainsKey("blockedAtStop"), "a running run has no stop history");
        Step(run, Blocking(Observation(null, RunStopTick, TimeSpeed.Superfast), "Dialog_NodeTree"));
        var stopped = (Dictionary<string, object>)describe.Invoke(null, new[] { run });
        Check(((IEnumerable)stopped["blockedAtStop"]).Cast<string>().SequenceEqual(new[] { "Dialog_NodeTree" }), "the window that ended the run is kept with the run");
        Check((int)stopped["endTick"] == RunStopTick && (string)stopped["endReason"] == "dialog", "the stop tick and reason describe the run");
    }

    private static void CheckScheduleBlocks()
    {
        Func<string[], string> blocks = hours =>
        {
            IEnumerable result = (IEnumerable)mod.GetType("RiMCP.Read.WorkSweep", true).GetMethod("Compress", Static).Invoke(null, new object[] { hours });
            return string.Join(" ", result.Cast<object>().Select(block =>
                Field<string>(block, "Assignment") + ":" + Field<int>(block, "From") + "-" + Field<int>(block, "To")));
        };
        string[] day = Enumerable.Range(0, HoursPerDay).Select(hour => hour >= SleepFrom || hour < SleepTo ? "Sleep" : "Anything").ToArray();
        Check(blocks(day) == "Anything:6-22 Sleep:22-6", "sleep across midnight is one evening block: " + blocks(day));
        Check(blocks(Enumerable.Repeat("Anything", HoursPerDay).ToArray()) == "Anything:0-0", "one assignment all day is a single 0 to 0 block");
        string[] edges = Enumerable.Range(0, HoursPerDay).Select(hour => hour < SleepTo ? "Sleep" : hour == SleepTo ? "Joy" : "Work").ToArray();
        Check(blocks(edges) == "Sleep:0-6 Joy:6-7 Work:7-0", "blocks end where the next starts, and the day ends at 0: " + blocks(edges));
        string[] lastHour = Enumerable.Range(0, HoursPerDay).Select(hour => hour == LastHour ? "Sleep" : "Work").ToArray();
        Check(blocks(lastHour) == "Work:0-23 Sleep:23-" + Midnight, "a one-hour block at the day's end: " + blocks(lastHour));
    }
}
