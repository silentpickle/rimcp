using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // The game's naming windows (faction, settlement, both, or a gravship). Their OK button lives in
    // IMGUI drawing code, so accepting repeats what that button does: trim the displayed names, ask
    // the window's own validators, then call its own Named and NamedSecond and close it. Editing or
    // randomizing names is not offered; the player can still do both.
    internal static class NamingDialog
    {
        public const string Kind = "naming";
        public const string AcceptId = "accept";

        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type Base = typeof(Dialog_GiveName);
        private static readonly FieldInfo CurName = Base.GetField("curName", Instance);
        private static readonly FieldInfo CurSecondName = Base.GetField("curSecondName", Instance);
        private static readonly FieldInfo UseSecondName = Base.GetField("useSecondName", Instance);
        private static readonly FieldInfo SuggestingPawn = Base.GetField("suggestingPawn", Instance);
        private static readonly FieldInfo NameMessageKey = Base.GetField("nameMessageKey", Instance);
        private static readonly FieldInfo SecondNameMessageKey = Base.GetField("secondNameMessageKey", Instance);
        private static readonly FieldInfo InvalidNameMessageKey = Base.GetField("invalidNameMessageKey", Instance);
        private static readonly FieldInfo InvalidSecondNameMessageKey = Base.GetField("invalidSecondNameMessageKey", Instance);
        private static readonly FieldInfo GainedNameMessageKey = Base.GetField("gainedNameMessageKey", Instance);
        private static readonly PropertyInfo FirstCharLimit = Base.GetProperty("FirstCharLimit", Instance);
        private static readonly PropertyInfo SecondCharLimit = Base.GetProperty("SecondCharLimit", Instance);
        private static readonly MethodInfo IsValidName = Base.GetMethod("IsValidName", Instance);
        private static readonly MethodInfo IsValidSecondName = Base.GetMethod("IsValidSecondName", Instance);
        private static readonly MethodInfo Named = Base.GetMethod("Named", Instance);
        private static readonly MethodInfo NamedSecond = Base.GetMethod("NamedSecond", Instance);

        // What each window's two names are, by the window's type name.
        private static readonly Dictionary<string, string[]> Roles = new Dictionary<string, string[]>
        {
            { "Dialog_NamePlayerFactionAndSettlement", new[] { "faction", "settlement" } },
            { "Dialog_NamePlayerFaction", new[] { "faction", null } },
            { "Dialog_NamePlayerSettlement", new[] { "settlement", null } },
            { "Dialog_NamePlayerGravship", new[] { "gravship", null } }
        };
        private static readonly string[] GenericRoles = { "name", "secondName" };

        // Everything outside the window itself, so tests can stand in for the window stack.
        internal sealed class Host
        {
            public Func<bool> IsOpen;
            public Func<bool> Covered;
            public Action Close;
            public Action<string> Notify;
            public Func<string, object[], string> Translate;
        }

        public static bool Supports(Window window)
        {
            return window is Dialog_GiveName && ReflectionComplete();
        }

        public static DialogAdapter For(Dialog_GiveName dialog)
        {
            return For(dialog, GameHost(dialog));
        }

        internal static DialogAdapter For(Dialog_GiveName dialog, Host host)
        {
            return new DialogAdapter
            {
                Kind = Kind,
                Window = dialog,
                IsOpen = host.IsOpen,
                PageKey = () => dialog,
                Title = () => null,
                Question = () => Prompt(dialog, host),
                Options = () => new List<DialogOption> { AcceptOption(dialog) },
                NotReady = option => Refusal(dialog, host),
                CanCancel = () => false,
                Choose = option => Accept(dialog, host),
                Cancel = () => { },
                Covered = host.Covered,
                Fields = () => Describe(dialog)
            };
        }

        // The names shown now, with their roles and the window's length limits.
        internal static object[] Describe(Dialog_GiveName dialog)
        {
            string[] roles = RolesOf(dialog);
            object first = NameField(roles[0], Current(dialog), (int)FirstCharLimit.GetValue(dialog, null));
            if (!UsesSecond(dialog))
            {
                return new[] { first };
            }
            return new[] { first, NameField(roles[1], CurrentSecond(dialog), (int)SecondCharLimit.GetValue(dialog, null)) };
        }

        private static object NameField(string role, string value, int maxLength)
        {
            return Dto.Obj(Dto.Field("role", role), Dto.Field("value", value), Dto.Field("maxLength", maxLength));
        }

        // The names are part of the option's text, so a name the player edits makes a new page and an
        // answer read from the old one is refused.
        private static DialogOption AcceptOption(Dialog_GiveName dialog)
        {
            string text = UsesSecond(dialog)
                ? "OK: accept \"" + Current(dialog) + "\" and \"" + CurrentSecond(dialog) + "\""
                : "OK: accept \"" + Current(dialog) + "\"";
            return new DialogOption(AcceptId, 0, text, null);
        }

        // Null when the game would take the names shown; otherwise its own refusal message.
        private static string Refusal(Dialog_GiveName dialog, Host host)
        {
            if (!Valid(IsValidName, dialog, Current(dialog)))
            {
                return Message(host, InvalidNameMessageKey, dialog, "The game refuses this name.");
            }
            if (UsesSecond(dialog) && !Valid(IsValidSecondName, dialog, CurrentSecond(dialog)))
            {
                return Message(host, InvalidSecondNameMessageKey, dialog, "The game refuses the second name.");
            }
            return null;
        }

        private static void Accept(Dialog_GiveName dialog, Host host)
        {
            string refusal = Refusal(dialog, host);
            if (refusal != null)
            {
                throw new CommandException(409, refusal);
            }
            string first = Current(dialog);
            Call(Named, dialog, first);
            if (UsesSecond(dialog))
            {
                string second = CurrentSecond(dialog);
                Call(NamedSecond, dialog, second);
                host.Notify(Translated(host, GainedNameMessageKey, dialog, first, second));
            }
            else
            {
                host.Notify(Translated(host, GainedNameMessageKey, dialog, first));
            }
            host.Close();
        }

        private static string Prompt(Dialog_GiveName dialog, Host host)
        {
            Pawn pawn = SuggestingPawn.GetValue(dialog) as Pawn;
            if (pawn == null)
            {
                return null;
            }
            string first = Translated(host, NameMessageKey, dialog, pawn.LabelShort, pawn);
            if (!UsesSecond(dialog))
            {
                return Clean(first);
            }
            return Clean(first + "\n\n" + Translated(host, SecondNameMessageKey, dialog, pawn.LabelShort, pawn));
        }

        private static string Message(Host host, FieldInfo key, Dialog_GiveName dialog, string fallback)
        {
            string text = Translated(host, key, dialog);
            return string.IsNullOrEmpty(text) ? fallback : Clean(text);
        }

        private static string Translated(Host host, FieldInfo key, Dialog_GiveName dialog, params object[] args)
        {
            string name = key.GetValue(dialog) as string;
            return string.IsNullOrEmpty(name) ? null : host.Translate(name, args);
        }

        private static bool Valid(MethodInfo check, Dialog_GiveName dialog, string name)
        {
            return (bool)Call(check, dialog, name);
        }

        // As the OK button does, names are trimmed before they are checked and given.
        private static string Current(Dialog_GiveName dialog)
        {
            return (CurName.GetValue(dialog) as string)?.Trim();
        }

        private static string CurrentSecond(Dialog_GiveName dialog)
        {
            return (CurSecondName.GetValue(dialog) as string)?.Trim();
        }

        private static bool UsesSecond(Dialog_GiveName dialog)
        {
            return (bool)UseSecondName.GetValue(dialog);
        }

        private static string[] RolesOf(Dialog_GiveName dialog)
        {
            string[] roles;
            return Roles.TryGetValue(dialog.GetType().Name, out roles) ? roles : GenericRoles;
        }

        private static object Call(MethodInfo method, object target, params object[] args)
        {
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private static Host GameHost(Dialog_GiveName dialog)
        {
            return new Host
            {
                IsOpen = () => Find.WindowStack.IsOpen(dialog),
                Covered = () => DialogAdapter.Covers(dialog),
                Close = () => Find.WindowStack.TryRemove(dialog),
                Notify = text => Messages.Message(text, MessageTypeDefOf.TaskCompletion, historical: false),
                Translate = (key, args) => TranslateKey(key, args)
            };
        }

        private static string TranslateKey(string key, object[] args)
        {
            // Positional, as the window's own implicit conversions pass them.
            NamedArgument[] named = Array.ConvertAll(args, arg => new NamedArgument(arg, null));
            return key.Translate(named).CapitalizeFirst().ToString();
        }

        private static string Clean(string text)
        {
            return string.IsNullOrEmpty(text) ? text : text.StripTags().Trim();
        }

        private static bool ReflectionComplete()
        {
            return CurName != null && CurSecondName != null && UseSecondName != null && SuggestingPawn != null
                && FirstCharLimit != null && SecondCharLimit != null && IsValidName != null && IsValidSecondName != null
                && Named != null && NamedSecond != null && NameMessageKey != null && SecondNameMessageKey != null
                && InvalidNameMessageKey != null && InvalidSecondNameMessageKey != null && GainedNameMessageKey != null;
        }
    }
}
