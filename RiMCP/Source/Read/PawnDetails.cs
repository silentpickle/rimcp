using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // What the pawn inspector's Needs, Social, Gear and Bio tabs show that get_pawn would
    // otherwise leave out: every mood thought as the Needs tab groups it, opinions and a
    // prisoner's recruitment, temperature ranges and worn-out apparel, backstories and the work a
    // pawn can never do.
    internal static class PawnDetails
    {
        // ThoughtWorker_ApparelDamaged: below these hit point fractions apparel is frayed or
        // tattered, and the wearer minds.
        public const float FrayedBelow = 0.5f;
        public const float TatteredBelow = 0.2f;
        private const int MoodDigits = 1;
        private static readonly List<Thought> groups = new List<Thought>();
        private static readonly List<Thought> members = new List<Thought>();

        internal sealed class MoodThought
        {
            public string DefName;
            public string Label;
            public float Mood;
            public int Count;

            public string Describe()
            {
                string count = Count > 1 ? " x" + Count : "";
                return Label + count + " " + Mood.ToString("+0;-0");
            }
        }

        // Every thought that moves the pawn's mood, grouped and summed as the Needs tab shows
        // them (memories and situational thoughts alike), worst first.
        public static List<MoodThought> MoodThoughts(Pawn pawn)
        {
            ThoughtHandler thoughts = pawn.needs?.mood?.thoughts;
            if (thoughts == null)
            {
                return new List<MoodThought>();
            }
            groups.Clear();
            thoughts.GetDistinctMoodThoughtGroups(groups);
            List<MoodThought> result = groups.Select(group => Group(thoughts, group)).OrderBy(thought => thought.Mood).ToList();
            groups.Clear();
            return result;
        }

        private static MoodThought Group(ThoughtHandler thoughts, Thought group)
        {
            members.Clear();
            thoughts.GetMoodThoughts(group, members);
            var thought = new MoodThought
            {
                DefName = group.def?.defName,
                Label = group.LabelCap.ToString().StripTags(),
                Mood = (float)Math.Round(thoughts.MoodOffsetOfGroup(group), MoodDigits),
                Count = members.Count
            };
            members.Clear();
            return thought;
        }

        // The biggest mood penalties, for saying why a pawn is near a break.
        public static string[] TopPenalties(Pawn pawn, int limit)
        {
            return MoodThoughts(pawn).Where(thought => thought.Mood < 0f).Take(limit).Select(thought => thought.Describe()).ToArray();
        }

        public static object SerializeMoodThoughts(Pawn pawn)
        {
            return MoodThoughts(pawn).Select(thought => Dto.Obj(
                Dto.Field("defName", thought.DefName),
                Dto.Field("label", thought.Label),
                Dto.Field("moodOffset", thought.Mood),
                Dto.Field("count", thought.Count > 1 ? (int?)thought.Count : null))).ToArray();
        }

        // This pawn's opinion of each other colonist, slave or prisoner on its map, lowest first.
        public static object Opinions(Pawn pawn)
        {
            if (pawn.relations == null || pawn.MapHeld == null)
            {
                return null;
            }
            return PawnReadService.PawnsForFilter(pawn.MapHeld, "core")
                .Select(role => role.Pawn)
                .Where(other => other != pawn)
                .Select(other => new { other, opinion = pawn.relations.OpinionOf(other) })
                .OrderBy(entry => entry.opinion)
                .Select(entry => Dto.Obj(
                    Dto.Field("pawnId", ReadUtil.ThingId(entry.other)),
                    Dto.Field("name", ReadUtil.PawnName(entry.other)),
                    Dto.Field("opinion", entry.opinion),
                    Dto.Field("ofThisPawn", entry.other.relations == null ? (int?)null : entry.other.relations.OpinionOf(pawn))))
                .ToArray();
        }

        // A prisoner's recruitment state: what the warden is set to do, and how far along it is.
        public static object Prisoner(Pawn pawn)
        {
            if (!pawn.IsPrisonerOfColony || pawn.guest == null)
            {
                return null;
            }
            return Dto.Obj(
                Dto.Field("interactionMode", pawn.guest.ExclusiveInteractionMode?.defName),
                Dto.Field("resistance", pawn.guest.resistance < 0f ? (double?)null : Math.Round(pawn.guest.resistance, MoodDigits)),
                Dto.Field("will", pawn.guest.will < 0f ? (double?)null : Math.Round(pawn.guest.will, MoodDigits)));
        }

        // Where the pawn's body and clothes keep it free of heatstroke and hypothermia (safe), and
        // free of mood penalties (comfortable).
        public static object TemperatureRanges(Pawn pawn)
        {
            FloatRange safe = pawn.SafeTemperatureRange();
            FloatRange comfortable = pawn.ComfortableTemperatureRange();
            return Dto.Obj(
                Dto.Field("safeC", Range(safe)),
                Dto.Field("comfortableC", Range(comfortable)));
        }

        private static object Range(FloatRange range)
        {
            return Dto.Obj(Dto.Field("min", Math.Round(range.min, MoodDigits)), Dto.Field("max", Math.Round(range.max, MoodDigits)));
        }

        // frayed or tattered when the wearer minds the damage, as the game's apparel thought does.
        public static string Condition(Apparel apparel)
        {
            if (!apparel.def.useHitPoints || apparel.def.apparel == null || !apparel.def.apparel.careIfDamaged)
            {
                return null;
            }
            return ConditionAt((float)apparel.HitPoints / apparel.MaxHitPoints);
        }

        internal static string ConditionAt(float fraction)
        {
            if (fraction < TatteredBelow)
            {
                return "tattered";
            }
            return fraction < FrayedBelow ? "frayed" : null;
        }

        public static object Backstory(Pawn pawn)
        {
            if (pawn.story == null)
            {
                return null;
            }
            return Dto.Obj(
                Dto.Field("childhood", pawn.story.Childhood?.TitleCapFor(pawn.gender)),
                Dto.Field("adulthood", pawn.story.Adulthood?.TitleCapFor(pawn.gender)));
        }

        // Work types the pawn can never do, from its backstories, traits and genes.
        public static string[] Incapable(Pawn pawn)
        {
            if (pawn.story == null)
            {
                return null;
            }
            string[] disabled = pawn.GetDisabledWorkTypes(permanentOnly: true).Select(work => work.defName).OrderBy(name => name).ToArray();
            return disabled.Length == 0 ? null : disabled;
        }
    }
}
