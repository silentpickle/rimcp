using System;
using System.Collections.Generic;
using System.Linq;
using RiMCP.Read;

namespace RiMCP.Command
{
    // Rows are in stop priority order. A shared source is sampled once per frame even when
    // several rows use it. Rows still record events that are not stopping conditions for this run.
    internal static class AdvanceWatchers
    {
        private delegate void Compare(AdvanceObservation before, AdvanceObservation now, List<AdvanceEvent> events);

        private enum SourceOrder { Threat, Food, PawnsAndCaravans, Letters }

        private sealed class Source
        {
            public readonly SourceOrder Order;
            public readonly Action<AdvanceObservation> Capture;

            public Source(SourceOrder order, Action<AdvanceObservation> capture)
            {
                Order = order;
                Capture = capture;
            }
        }

        private sealed class Watcher
        {
            public readonly string Kind;
            public readonly string StopSummary;
            public readonly Source Sample;
            public readonly Compare Detect;
            public readonly bool DefaultStop;
            public readonly string StopAlias;

            public Watcher(string kind, string summary, Source sample, Compare detect, bool defaultStop = true, string stopAlias = null)
            {
                Kind = kind;
                StopSummary = summary;
                Sample = sample;
                Detect = detect;
                DefaultStop = defaultStop;
                StopAlias = stopAlias;
            }
        }

        private static readonly Source Threats = new Source(SourceOrder.Threat, AdvanceRunner.CaptureThreats);
        private static readonly Source Food = new Source(SourceOrder.Food, AdvanceRunner.CaptureFood);
        private static readonly Source Pawns = new Source(SourceOrder.PawnsAndCaravans, AdvanceRunner.CapturePawnsAndCaravans);
        private static readonly Source Letters = new Source(SourceOrder.Letters, AdvanceRunner.CaptureLetters);

        private static readonly Watcher[] Rows =
        {
            Pawn("death", "a death", (before, now) => now.Dead, now => "died"),
            Pawn("downed", "a colonist down", (before, now) => !now.Dead && now.Downed && !before.Downed, now => "downed"),
            Pawn("injured", "an injury", NewInjury, now => now.Bleeding ? "bleeding" : "new injury"),
            Pawn("mentalBreak", "a mental break", (before, now) => !now.Dead && now.MentalState != null && before.MentalState == null, now => now.MentalState),
            Pawn("breakRisk", "an extreme mental break risk", (before, now) => !now.Dead && Reached(now.BreakRisk, before.BreakRisk, DangerLevel.Extreme), now => "extreme break risk"),
            Pawn("temperature", "serious heatstroke or hypothermia", (before, now) => !now.Dead && Reached(now.Temperature, before.Temperature, DangerLevel.Major), now => now.TemperatureDetail),
            Appeared("hostiles", "hostiles", Threats, observation => observation.Threat),
            Appeared("lowFood", "low food", Food, observation => observation.LowFood),
            Letter("threatLetter", "a threat letter", threat: true, stopAlias: "letter"),
            CaravanSignal("caravanHungry", "urgent caravan hunger", observation => observation.HungryCaravans, "caravan"),
            CaravanSignal("caravanImmobilized", "an immobilized caravan", observation => observation.ImmobilizedCaravans, "caravan"),
            CaravanSignal("caravanDepartureBlocked", "paused caravan packing", observation => observation.PausedFormations, "formation"),
            new Watcher("caravanRouteEnded", "a caravan route ending", Pawns, EndedCaravans, defaultStop: false),
            Letter("letter", "a letter", threat: false),
            new Watcher("built", "construction completed", Pawns, Built, defaultStop: false),
            new Watcher("researchDone", "research completed", Pawns, ResearchDone, defaultStop: false),
            Pawn("pawnIdle", "a colonist becoming idle", (before, now) => now.Idle && !before.Idle, now => "idle", defaultStop: false),
            new Watcher("caravanArrived", "a caravan arrival", Pawns, Arrived, defaultStop: false)
        };

        private static readonly Source[] Sources = Rows.Select(row => row.Sample).Distinct().OrderBy(source => source.Order).ToArray();
        private static readonly Dictionary<string, Watcher> ByKind = Rows.ToDictionary(row => row.Kind, StringComparer.Ordinal);
        public static readonly string[] Conditions = Rows.Select(row => row.Kind).ToArray();
        public static readonly string[] DefaultStopOn = Rows.Where(row => row.DefaultStop).Select(row => row.Kind).ToArray();

        public static void Capture(AdvanceObservation observation)
        {
            foreach (Source source in Sources)
            {
                source.Capture(observation);
            }
        }

        public static List<AdvanceEvent> Detect(AdvanceObservation before, AdvanceObservation now)
        {
            var events = new List<AdvanceEvent>();
            foreach (Watcher row in Rows)
            {
                row.Detect(before, now, events);
            }
            return events;
        }

        public static string StopSummary(string kind)
        {
            return kind != null && ByKind.TryGetValue(kind, out Watcher row) ? row.StopSummary : null;
        }

        public static bool MatchesAlias(string kind, HashSet<string> stopOn)
        {
            return ByKind.TryGetValue(kind, out Watcher row) && row.StopAlias != null && stopOn.Contains(row.StopAlias);
        }

        // A danger rose to the level that matters from below it.
        private static bool Reached(DangerLevel now, DangerLevel before, DangerLevel level)
        {
            return now >= level && before < level;
        }

        private static Watcher Pawn(string kind, string summary, Func<ColonistState, ColonistState, bool> changed, Func<ColonistState, string> detail, bool defaultStop = true)
        {
            return new Watcher(kind, summary, Pawns, (before, now, events) =>
            {
                foreach (ColonistState pawn in now.Colonists.Values)
                {
                    if (!before.Colonists.TryGetValue(pawn.Id, out ColonistState previous) || previous.Dead || !changed(previous, pawn))
                    {
                        continue;
                    }
                    events.Add(new AdvanceEvent { Kind = kind, Tick = now.Tick, PawnId = pawn.Id, PawnName = pawn.Name, Detail = detail(pawn) });
                }
            }, defaultStop);
        }

        private static Watcher CaravanSignal(string kind, string summary,
            Func<AdvanceObservation, Dictionary<int, string>> select, string subject)
        {
            return new Watcher(kind, summary, Pawns, (before, now, events) =>
            {
                Dictionary<int, string> earlier = select(before);
                foreach (var signal in select(now).OrderBy(pair => pair.Key))
                {
                    if (!earlier.ContainsKey(signal.Key))
                    {
                        events.Add(Event(kind, now.Tick, subject + " " + signal.Key + ": " + signal.Value));
                    }
                }
            }, defaultStop: false);
        }

        private static void Built(AdvanceObservation before, AdvanceObservation now, List<AdvanceEvent> events)
        {
            foreach (BuildWork work in now.Built)
            {
                AdvanceEvent change = Event("built", now.Tick, work.Def.defName + " at " + work.Cell.x + "," + work.Cell.z);
                change.Construction = work;
                events.Add(change);
            }
        }

        private static void ResearchDone(AdvanceObservation before, AdvanceObservation now, List<AdvanceEvent> events)
        {
            foreach (ResearchWork work in now.Research.Values)
            {
                if (work.Finished && before.Research.TryGetValue(work.Project, out ResearchWork earlier) && !earlier.Finished)
                {
                    events.Add(Event("researchDone", now.Tick, work.Project.defName));
                }
            }
        }

        private static void Arrived(AdvanceObservation before, AdvanceObservation now, List<AdvanceEvent> events)
        {
            foreach (var caravan in now.Arrivals.OrderBy(pair => pair.Key))
            {
                events.Add(Event("caravanArrived", now.Tick, caravan.Value + " (world object " + caravan.Key + ")"));
            }
        }

        private static bool NewInjury(ColonistState before, ColonistState now)
        {
            return !now.Dead && (now.Injuries > before.Injuries || now.Bleeding && !before.Bleeding);
        }

        private static Watcher Appeared(string kind, string summary, Source source, Func<AdvanceObservation, string> label)
        {
            return new Watcher(kind, summary, source, (before, now, events) =>
            {
                string detail = label(now);
                if (detail != null && label(before) == null)
                {
                    events.Add(Event(kind, now.Tick, detail));
                }
            });
        }

        private static Watcher Letter(string kind, string summary, bool threat, string stopAlias = null)
        {
            return new Watcher(kind, summary, Letters, (before, now, events) =>
            {
                var newLetters = now.Letters.Values
                    .Where(letter => letter.Threat == threat && !before.Letters.ContainsKey(letter.Id))
                    .OrderBy(letter => letter.Id);
                foreach (LetterState letter in newLetters)
                {
                    events.Add(Event(kind, now.Tick, letter.Label));
                }
            }, defaultStop: threat, stopAlias: stopAlias);
        }

        private static void EndedCaravans(AdvanceObservation before, AdvanceObservation now, List<AdvanceEvent> events)
        {
            var ended = before.MovingCaravans.Where(caravan => !now.MovingCaravans.ContainsKey(caravan.Key)).OrderBy(caravan => caravan.Key);
            foreach (var caravan in ended)
            {
                events.Add(Event("caravanRouteEnded", now.Tick, caravan.Value + " (world object " + caravan.Key + ")"));
            }
        }

        private static AdvanceEvent Event(string kind, int tick, string detail)
        {
            return new AdvanceEvent { Kind = kind, Tick = tick, Detail = detail };
        }
    }
}
