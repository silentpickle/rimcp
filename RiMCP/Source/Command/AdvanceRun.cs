using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using RimWorld;
using RiMCP.Read;
using Verse;
using Verse.AI;

namespace RiMCP.Command
{
    internal sealed class ColonistState
    {
        public string Id;
        public string Name;
        public bool Dead;
        public bool Downed;
        public bool Idle;
        public bool Bleeding;
        public int Injuries;
        public string MentalState;
    }

    internal sealed class LetterState
    {
        public int Id;
        public string Label;
        public string DefName;
        public bool Threat;
    }

    internal sealed class AdvanceObservation
    {
        public object Game;
        public bool Playing;
        public int Tick;
        public TimeSpeed Speed;
        // Label of a visible active threat to the colony, or null.
        public string Threat;
        // Label of a home map whose stored food is below the Low food threshold, or null.
        public string LowFood;
        public float StoredFood;
        public readonly Dictionary<string, BuildWork> Pending = new Dictionary<string, BuildWork>();
        public readonly List<BuildWork> Built = new List<BuildWork>();
        public readonly HashSet<ResearchProjectDef> ActiveResearch = new HashSet<ResearchProjectDef>();
        public readonly Dictionary<ResearchProjectDef, ResearchWork> Research = new Dictionary<ResearchProjectDef, ResearchWork>();
        public readonly Dictionary<int, CaravanTravel> Traveling = new Dictionary<int, CaravanTravel>();
        public readonly Dictionary<int, string> Arrivals = new Dictionary<int, string>();
        // Type names of open windows that force a pause (see BlockingWindows); empty when none.
        public readonly List<string> Blocking = new List<string>();
        public readonly Dictionary<string, ColonistState> Colonists = new Dictionary<string, ColonistState>();
        public readonly Dictionary<int, LetterState> Letters = new Dictionary<int, LetterState>();
        public readonly Dictionary<int, string> HungryCaravans = new Dictionary<int, string>();
        public readonly Dictionary<int, string> ImmobilizedCaravans = new Dictionary<int, string>();
        public readonly Dictionary<int, string> PausedFormations = new Dictionary<int, string>();
        public readonly Dictionary<int, string> MovingCaravans = new Dictionary<int, string>();
    }

    internal sealed class AdvanceEvent
    {
        public string Kind;
        public int Tick;
        public string PawnId;
        public string PawnName;
        public string Detail;
        public BuildWork Construction;
    }

    // One advance_time request. Step only compares observations, so the lifecycle can be tested
    // without a running game; AdvanceRunner gathers observations and applies side effects.
    internal sealed class AdvanceRun
    {
        public const string Running = "running";
        public const string DialogReason = "dialog";
        public const int MaxEvents = 50;
        public static readonly string[] Conditions = AdvanceWatchers.Conditions;
        public static readonly string[] DefaultStopOn = AdvanceWatchers.DefaultStopOn;

        public readonly int Id;
        public readonly string RequestId;
        public readonly object Game;
        public readonly TimeSpeed Speed;
        public readonly int StartTick;
        public readonly int? TargetTick;
        public readonly float? Hours;
        public readonly HashSet<string> StopOn;
        public readonly AdvanceWatch Watch;
        public readonly AdvanceSummary Summary;
        public readonly List<AdvanceEvent> Events = new List<AdvanceEvent>();
        public readonly ManualResetEventSlim Ended = new ManualResetEventSlim(false);
        public int DroppedEvents;
        public string Status = Running;
        public string EndReason;
        public AdvanceEvent StoppedBy;
        public int EndTick = -1;
        public AdvanceObservation Previous;
        public int Frames;
        public double FrameMsTotal;
        public double FrameMsMax;
        public bool Failed;
        public bool Revoked;

        public AdvanceRun(int id, string requestId, object game, TimeSpeed speed, int startTick, int? targetTick, float? hours, IEnumerable<string> stopOn, AdvanceObservation baseline, AdvanceWatch watch = null)
        {
            Id = id;
            RequestId = requestId;
            Game = game;
            Speed = speed;
            StartTick = startTick;
            TargetTick = targetTick;
            Hours = hours;
            StopOn = new HashSet<string>(stopOn);
            Previous = baseline;
            Watch = watch;
            Summary = new AdvanceSummary(baseline);
        }

        public void Step(AdvanceObservation current)
        {
            if (Status != Running)
            {
                return;
            }
            if (!current.Playing || !ReferenceEquals(current.Game, Game))
            {
                End("interrupted", "gameUnloaded", Previous.Tick);
                return;
            }

            Summary.Observe(Previous, current);
            List<AdvanceEvent> changes = AdvanceWatchers.Detect(Previous, current);
            foreach (AdvanceEvent change in changes)
            {
                Record(change);
            }
            Previous = current;

            // A stop condition outranks reaching the target and a pause in the same frame: the
            // game pauses itself for some letters, and the agent needs the cause.
            AdvanceEvent stop = changes.FirstOrDefault(StopsRun);
            if (stop != null)
            {
                StoppedBy = stop;
                End("stopped", stop.Kind, current.Tick);
            }
            else if (TargetTick.HasValue && current.Tick >= TargetTick.Value)
            {
                End("completed", "completed", current.Tick);
            }
            else if (current.Blocking.Count > 0)
            {
                // The clock stopped for a window while the speed stayed selected; without this the
                // run would report running until the window closed.
                End("interrupted", DialogReason, current.Tick);
            }
            else if (current.Speed != Speed)
            {
                End("interrupted", current.Speed == TimeSpeed.Paused ? "paused" : "speedChanged", current.Tick);
            }
        }

        public bool SameRequest(float? hours, TimeSpeed speed, ICollection<string> stopOn, AdvanceWatch watch = null)
        {
            return AdvanceWatch.Equivalent(Watch, watch) && Hours == hours && Speed == speed && StopOn.SetEquals(stopOn);
        }

        public void RecordFrame(double milliseconds)
        {
            Frames++;
            FrameMsTotal += milliseconds;
            FrameMsMax = Math.Max(FrameMsMax, milliseconds);
        }

        // The run owns time until it ends: completing, stopping, or failing pauses the game. A pause
        // or speed change by someone else is left alone.
        public bool PausesGameAtEnd
        {
            // A blocking window is paused for too: when it closes, the game should not run on unwatched.
            get { return Failed || Revoked || Status == "stopped" || Status == "completed" || EndReason == DialogReason; }
        }

        public void Revoke(string reason, int tick)
        {
            Revoked = true;
            End("interrupted", reason, tick);
        }

        public void Fail(string message, int tick)
        {
            Failed = true;
            End("interrupted", "error: " + message, tick);
        }

        public void End(string status, string reason, int tick)
        {
            Status = status;
            EndReason = reason;
            EndTick = tick;
            int elapsed = Math.Max(0, tick - StartTick);
            string line = "Advanced " + Duration(elapsed) + (status == "stopped" ? "; stopped for " + Reason(reason) : status == "completed" ? "" : "; " + Reason(reason));
            AgentLog.Finish(LogKey(Id), elapsed == 0 && status != "completed" ? Outcomes.Cancelled : Outcomes.Done, line);
        }

        public static string LogKey(int id)
        {
            return "advance-" + id;
        }

        public static string Duration(int ticks)
        {
            float hours = ticks / (float)GenDate.TicksPerHour;
            if (hours < 1f)
            {
                int minutes = (int)Math.Round(hours * 60f);
                return minutes == 1 ? "1 minute" : minutes + " minutes";
            }
            return hours == 1f ? "1 hour" : hours.ToString("0.#") + " hours";
        }

        private static string Reason(string reason)
        {
            string summary = AdvanceWatchers.StopSummary(reason);
            if (summary != null)
            {
                return summary;
            }
            switch (reason)
            {
                case DialogReason: return "a window paused the game";
                case "paused": return "the game was paused";
                case "speedChanged": return "the speed was changed";
                case "gameUnloaded": return "the game was unloaded";
                default: return reason ?? "interrupted";
            }
        }

        private bool StopsRun(AdvanceEvent change)
        {
            return (change.Construction == null || Watch == null || Watch.Matches(change.Construction)) &&
                (StopOn.Contains(change.Kind) || AdvanceWatchers.MatchesAlias(change.Kind, StopOn));
        }

        private void Record(AdvanceEvent change)
        {
            Summary.Record(change);
            if (Events.Count >= MaxEvents)
            {
                DroppedEvents++;
                return;
            }
            Events.Add(change);
        }

    }

    // Owns advance runs. Everything here runs on the main thread; HTTP threads only wait on a
    // run's Ended event.
    internal static class AdvanceRunner
    {
        // Retries are recognised for this many recent runs of the loaded game.
        public const int MaxHistory = 32;

        // Runs of the loaded game, oldest first.
        private static readonly List<AdvanceRun> history = new List<AdvanceRun>();
        private static AdvanceRun latest;
        private static int nextId;
        private static List<Pawn> tracked = new List<Pawn>();
        // Summing stored food, and finding colonists carried or in a container, cost too much for
        // every frame, so both are checked every 250 ticks and carried between checks.
        private const int FoodCheckInterval = GenTicks.TickRareInterval;
        private static int foodCheckedTick = int.MinValue;
        private static string lowFood;
        private static float storedFood;
        private static AdvanceObservation lastCapture;
        private static List<Pawn> held = new List<Pawn>();

        // Replaceable so tests can simulate an observation failure.
        internal static Func<AdvanceObservation> Observer = Observe;

        // Whether an advance is running now.
        public static bool Running
        {
            get { return latest != null && latest.Status == AdvanceRun.Running; }
        }

        public static AdvanceRun LatestInLoadedGame
        {
            get { return latest != null && ReferenceEquals(latest.Game, Current.Game) ? latest : null; }
        }

        public static AdvanceRun Recall(string requestId)
        {
            return requestId == null ? null : history.LastOrDefault(run => run.RequestId == requestId && ReferenceEquals(run.Game, Current.Game));
        }

        public static AdvanceRun RunById(int id)
        {
            return history.FirstOrDefault(run => run.Id == id && ReferenceEquals(run.Game, Current.Game));
        }

        public static AdvanceRun Start(float? hours, TimeSpeed speed, ICollection<string> stopOn, string requestId, out bool created, AdvanceWatch watch = null)
        {
            history.RemoveAll(run => !ReferenceEquals(run.Game, Current.Game));
            AdvanceRun receipt = FindReceipt(history, Current.Game, requestId, hours, speed, stopOn, watch);
            if (receipt != null)
            {
                created = false;
                return receipt;
            }
            if (latest != null && latest.Status == AdvanceRun.Running && ReferenceEquals(latest.Game, Current.Game))
            {
                throw new CommandException(409, "An advance is already running (run " + latest.Id + "). Use get_advance to wait for it, or set_game_speed to stop it.");
            }

            // Take the starting snapshot before unpausing, so a failure here leaves the game as it was.
            tracked = new List<Pawn>();
            held = new List<Pawn>();
            foodCheckedTick = int.MinValue;
            lastCapture = null;
            AdvanceObservation baseline = Observe();
            TickManager ticks = Find.TickManager;
            ticks.CurTimeSpeed = speed;
            if (ticks.CurTimeSpeed != speed)
            {
                throw new CommandException(409, "RimWorld is not accepting time control right now.");
            }

            int? targetTick = hours.HasValue ? baseline.Tick + (int)Math.Ceiling(hours.Value * GenDate.TicksPerHour) : (int?)null;
            AdvanceRun run = new AdvanceRun(++nextId, requestId, Current.Game, speed, baseline.Tick, targetTick, hours, stopOn, baseline, watch);
            history.Add(run);
            if (history.Count > MaxHistory)
            {
                history.RemoveAt(0);
            }
            latest = run;
            created = true;
            return run;
        }

        // A repeated requestId returns its run; the same id with different arguments is a client error.
        public static AdvanceRun FindReceipt(IEnumerable<AdvanceRun> runs, object game, string requestId, float? hours, TimeSpeed speed, ICollection<string> stopOn, AdvanceWatch watch = null)
        {
            if (requestId == null)
            {
                return null;
            }
            AdvanceRun run = runs.LastOrDefault(item => item.RequestId == requestId && ReferenceEquals(item.Game, game));
            if (run != null && !run.SameRequest(hours, speed, stopOn, watch))
            {
                throw new CommandException(409, "requestId '" + requestId + "' was already used for run " + run.Id + " with different arguments.");
            }
            return run;
        }

        public static AdvanceRun Interrupt(string reason)
        {
            AdvanceRun run = latest;
            if (run == null || run.Status != AdvanceRun.Running)
            {
                return null;
            }
            run.End("interrupted", reason, Current.Game == null ? run.Previous.Tick : Find.TickManager.TicksGame);
            run.Ended.Set();
            return run;
        }

        // A dialog pauses the game without changing its speed, so a run would resume once it closed.
        public static AdvanceRun EndForDialog(string reason)
        {
            AdvanceRun run = Interrupt(reason);
            if (run != null && Current.Game != null && ReferenceEquals(Current.Game, run.Game))
            {
                Find.TickManager.Pause();
            }
            return run;
        }

        // The agent lost time control while its run owned time: end the run and pause.
        public static AdvanceRun Revoke(string reason)
        {
            AdvanceRun run = latest;
            if (run == null || run.Status != AdvanceRun.Running)
            {
                return null;
            }
            bool loaded = Current.Game != null && ReferenceEquals(Current.Game, run.Game);
            run.Revoke(reason, loaded ? Find.TickManager.TicksGame : run.Previous.Tick);
            if (loaded)
            {
                Find.TickManager.Pause();
            }
            run.Ended.Set();
            return run;
        }

        // Finished runs of other games, and the colonists they watched.
        public static void ForgetOtherGames()
        {
            history.RemoveAll(run => !ReferenceEquals(run.Game, Current.Game) && run.Status != AdvanceRun.Running);
            if (latest != null && !ReferenceEquals(latest.Game, Current.Game) && latest.Status != AdvanceRun.Running)
            {
                latest = null;
                tracked = new List<Pawn>();
                held = new List<Pawn>();
                lastCapture = null;
            }
        }

        public static void Update()
        {
            AdvanceRun run = latest;
            if (run == null || run.Status != AdvanceRun.Running)
            {
                return;
            }

            Stopwatch frame = Stopwatch.StartNew();
            try
            {
                run.Step(Observer());
            }
            catch (Exception ex)
            {
                run.Fail(ex.GetType().Name + ": " + ex.Message, run.Previous.Tick);
            }
            run.RecordFrame(frame.Elapsed.TotalMilliseconds);
            if (run.Status == AdvanceRun.Running)
            {
                return;
            }

            try
            {
                if (Current.Game == null || !ReferenceEquals(Current.Game, run.Game))
                {
                    return;
                }
                if (run.PausesGameAtEnd)
                {
                    Find.TickManager.Pause();
                }
                if (!run.Failed)
                {
                    run.Summary.FinishFood(TotalStoredFood());
                    if (run.Previous.Blocking.Count > 0)
                    {
                        BlockingWindows.Adopt();
                    }
                }
            }
            catch (Exception ex)
            {
                RiMCP.Bridge.BridgeRouter.LogFailure(ex);
            }
            finally
            {
                run.Ended.Set();
            }
        }

        private static AdvanceObservation Observe()
        {
            AdvanceObservation observation = new AdvanceObservation
            {
                Game = Current.Game,
                Playing = Current.Game != null && Current.ProgramState == ProgramState.Playing && Find.TickManager != null
            };
            if (!observation.Playing)
            {
                return observation;
            }

            observation.Tick = Find.TickManager.TicksGame;
            observation.Speed = Find.TickManager.CurTimeSpeed;
            observation.Blocking.AddRange(BlockingWindows.Names());
            AdvanceWatchers.Capture(observation);
            AdvanceWork.Capture(observation, lastCapture);
            lastCapture = observation;
            return observation;
        }

        internal static void CaptureThreats(AdvanceObservation observation)
        {
            foreach (Map map in Find.Maps)
            {
                if (!GenHostility.AnyHostileActiveThreatTo(map, Faction.OfPlayer, out IAttackTarget threat))
                {
                    continue;
                }
                observation.Threat = ThreatLabel(threat.Thing);
                return;
            }
        }

        internal static void CaptureFood(AdvanceObservation observation)
        {
            if (observation.Tick < foodCheckedTick || observation.Tick >= foodCheckedTick + FoodCheckInterval)
            {
                foodCheckedTick = observation.Tick;
                Map low = null;
                storedFood = 0f;
                foreach (Map home in Find.Maps.Where(map => map.IsPlayerHome))
                {
                    float nutrition = FoodSample.Take(home).Total;
                    storedFood += nutrition;
                    if (low == null && ColonyDangers.LowFood(home, nutrition))
                    {
                        low = home;
                    }
                }
                lowFood = low == null ? null : "stored food on " + (low.Parent == null ? "map " + low.uniqueID : low.Parent.LabelCap) + " is below " + FoodSummary.LowPerColonist + " nutrition per colonist";
                held = Find.Maps.SelectMany(map => map.mapPawns.FreeColonists).Where(pawn => !pawn.Spawned).ToList();
            }
            observation.LowFood = lowFood;
            observation.StoredFood = storedFood;
        }

        private static float TotalStoredFood()
        {
            return Find.Maps.Where(map => map.IsPlayerHome).Sum(map => FoodSample.Take(map).Total);
        }

        internal static void CapturePawnsAndCaravans(AdvanceObservation observation)
        {
            List<Pawn> pawns = Find.Maps.SelectMany(map => map.mapPawns.FreeColonistsSpawned).ToList();
            // Colonists who are not spawned: carried or in a container, on a caravan, watched since
            // they were spawned, and those who died since the last frame.
            var caravans = Find.WorldObjects.Caravans.Where(caravan => caravan.IsPlayerControlled).ToList();
            foreach (var caravan in caravans)
            {
                if (caravan.pather.Moving)
                {
                    observation.MovingCaravans[caravan.ID] = caravan.LabelCap.StripTags();
                    observation.Traveling[caravan.ID] = new CaravanTravel(caravan);
                }
                int hungry = CaravanService.Hungry(caravan);
                if (hungry > 0)
                {
                    observation.HungryCaravans[caravan.ID] = hungry + " urgently hungry";
                }
                string immobilized = CaravanService.Immobilized(caravan);
                if (immobilized != null)
                {
                    observation.ImmobilizedCaravans[caravan.ID] = immobilized;
                }
            }
            foreach (var lord in CaravanService.Formations().Where(lord => lord.CurLordToil is LordToil_PrepareCaravan_Pause))
            {
                observation.PausedFormations[lord.loadID] = ((LordJob_FormAndSendCaravan)lord.LordJob).Status;
            }
            IEnumerable<Pawn> travelers = caravans.SelectMany(caravan => caravan.PawnsListForReading).Where(pawn => pawn.IsFreeColonist);
            pawns.AddRange(held.Concat(travelers).Concat(tracked.Where(pawn => pawn.Dead || pawn.MapHeld != null)).Distinct().Where(pawn => !pawns.Contains(pawn)).ToList());
            tracked = pawns.Where(pawn => !pawn.Dead).ToList();

            foreach (Pawn pawn in pawns)
            {
                observation.Colonists[pawn.ThingID] = Colonist(pawn);
            }
        }

        internal static void CaptureLetters(AdvanceObservation observation)
        {
            foreach (Letter letter in Find.LetterStack.LettersListForReading)
            {
                observation.Letters[letter.ID] = new LetterState
                {
                    Id = letter.ID,
                    Label = letter.Label.ToString().StripTags(),
                    DefName = letter.def == null ? null : letter.def.defName,
                    Threat = letter.def != null && (letter.def == LetterDefOf.ThreatBig || letter.def == LetterDefOf.ThreatSmall)
                };
            }
        }

        // Labels carry rich-text markup, such as the coloured title after a raider's name.
        private static string ThreatLabel(Thing thing)
        {
            if (thing == null)
            {
                return "hostiles";
            }
            Pawn pawn = thing as Pawn;
            return (pawn != null ? pawn.LabelShortCap : thing.LabelCap).StripTags();
        }

        private static ColonistState Colonist(Pawn pawn)
        {
            ColonistState state = new ColonistState
            {
                Id = pawn.ThingID,
                Name = ReadUtil.PawnName(pawn),
                Dead = pawn.Dead
            };
            if (pawn.Dead || pawn.health == null || pawn.health.hediffSet == null)
            {
                return state;
            }
            state.Downed = pawn.Downed;
            state.Idle = pawn.Spawned && !pawn.Downed && !pawn.Drafted && pawn.mindState?.IsIdle == true;
            state.Bleeding = pawn.health.hediffSet.BleedRateTotal > 0f;
            // Fresh injuries only: scars are permanent, and diseases needing re-tending are not new harm.
            state.Injuries = pawn.health.hediffSet.hediffs.Count(hediff => hediff is Hediff_Injury && !hediff.IsPermanent());
            state.MentalState = pawn.MentalStateDef == null ? null : ReadUtil.DefLabel(pawn.MentalStateDef);
            return state;
        }
    }
}
