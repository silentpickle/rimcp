using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Command;
using Verse;

namespace RiMCP.Read
{
    internal static class InfoCardReadService
    {
        // These are the info card's data producers. Calling them directly leaves its shared UI
        // selection, cache, and scroll state alone.
        private static readonly MethodInfo DefStats = StatsMethod(typeof(Def), typeof(ThingDef));
        private static readonly MethodInfo AbilityStats = StatsMethod(typeof(AbilityDef));
        private static readonly MethodInfo ThingStats = StatsMethod(typeof(Thing));

        private static MethodInfo StatsMethod(params Type[] types)
        {
            return typeof(StatsReportUtility).GetMethod("StatsToDraw", BindingFlags.Static | BindingFlags.NonPublic,
                null, types, null);
        }

        public static BridgeResponse GetInfo(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            string thingId = context.Request.Get("thingId");
            string defName = context.Request.Get("defName");
            string kind = context.Request.Get("kind");
            string stuffName = context.Request.Get("stuff");
            if (string.IsNullOrWhiteSpace(thingId) == string.IsNullOrWhiteSpace(defName) ||
                thingId != null && (kind != null || stuffName != null))
            {
                throw new BridgeException(400, "Give thingId or defName. Only defName accepts kind and stuff.");
            }

            Thing thing = null;
            Def def;
            ThingDef stuff = null;
            if (!string.IsNullOrWhiteSpace(thingId))
            {
                context = ReadContext.FromMap(request);
                thing = Targets.FindVisibleThing(context.Map, thingId);
                def = thing?.def;
            }
            else
            {
                def = DefReadService.FindDef(defName, kind);
                if (!string.IsNullOrWhiteSpace(stuffName))
                {
                    stuff = DefDatabase<ThingDef>.GetNamedSilentFail(stuffName);
                    if (stuff == null || !(def is ThingDef materialTarget) || !GenStuff.AllowedStuffsFor(materialTarget).Contains(stuff))
                    {
                        throw new BridgeException(400, "Give a material allowed for this ThingDef.");
                    }
                }
            }
            if (def == null)
            {
                return BridgeResponse.Error(404, "Def or visible thing not found.");
            }

            StatRequest statRequest = thing != null ? StatRequest.For(thing) :
                def is BuildableDef buildable ? StatRequest.For(buildable, stuff) : StatRequest.ForEmpty();
            string search = context.Request.Get("query");
            IEnumerable<StatDrawEntry> entries = Entries(def, thing, stuff, statRequest);
            if (!string.IsNullOrWhiteSpace(search))
            {
                entries = entries.Where(entry => Contains(entry.stat?.defName, search) || Contains(entry.LabelCap, search));
            }
            Page<StatDrawEntry> page = new Page<StatDrawEntry>(entries, context.Request);
            bool explain = context.Request.Wants("explanations");
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("defName", def.defName),
                Dto.Field("label", ReadUtil.DefLabel(def)),
                Dto.Field("description", def.description?.StripTags()),
                Dto.Field("sourceMod", def.modContentPack?.PackageId),
                Dto.Field("thingId", ReadUtil.ThingId(thing)),
                Dto.Field("inspectText", thing != null && context.Request.Wants("inspectText") ? ReadProjection.InspectText(thing) : null),
                Dto.Field("stuff", (thing?.Stuff ?? stuff)?.defName),
                Dto.Field("prerequisites", Prerequisites(def)),
                Dto.Field("parts", Parts(def, thing, context.Request.Wants("buttons"))),
                Dto.Field("stats", page.Items.Select(entry => Describe(entry, statRequest, explain)).ToArray())),
                page.Truncated, page.NextCursor, "stats");
        }

        private static IEnumerable<StatDrawEntry> Entries(Def def, Thing thing, ThingDef stuff, StatRequest request)
        {
            MethodInfo method = thing != null ? ThingStats : def is AbilityDef ? AbilityStats : DefStats;
            if (method == null)
            {
                throw new BridgeException(501, "This game version does not expose the expected info-card data.");
            }
            object[] args = thing != null ? new object[] { thing } : def is AbilityDef ? new object[] { def } : new object[] { def, stuff };
            IEnumerable<StatDrawEntry> entries = (IEnumerable<StatDrawEntry>)method.Invoke(null, args);
            IEnumerable<StatDrawEntry> special = def.SpecialDisplayStats(request);
            IEnumerable<StatDrawEntry> visible = thing == null
                ? special.Concat(entries.Where(entry => entry.ShouldDisplay()))
                : special.Concat(entries).Where(entry => (entry.stat == null || entry.stat.showNonAbstract) && entry.ShouldDisplay(thing));
            return visible.OrderBy(entry => entry.category?.displayOrder ?? 0)
                .ThenByDescending(entry => entry.DisplayPriorityWithinCategory).ThenBy(entry => entry.LabelCap);
        }

        private static object Describe(StatDrawEntry entry, StatRequest request, bool explain)
        {
            return Dto.Obj(
                Dto.Field("defName", entry.stat?.defName),
                Dto.Field("category", entry.category?.defName),
                Dto.Field("label", entry.LabelCap.StripTags()),
                Dto.Field("value", entry.ValueString.StripTags()),
                Dto.Field("explanation", explain ? entry.GetExplanationText(request)?.StripTags() : null));
        }

        private static string[] Prerequisites(Def def)
        {
            IEnumerable<ResearchProjectDef> projects = (def as BuildableDef)?.researchPrerequisites ??
                (def as ResearchProjectDef)?.prerequisites;
            if (def is RecipeDef recipe)
            {
                projects = (recipe.researchPrerequisites ?? Enumerable.Empty<ResearchProjectDef>())
                    .Concat(recipe.researchPrerequisite == null ? Enumerable.Empty<ResearchProjectDef>() : new[] { recipe.researchPrerequisite });
            }
            return projects?.Select(project => project.defName).Distinct().ToArray() ?? Array.Empty<string>();
        }

        private static object Parts(Def def, Thing live, bool includeButtons)
        {
            ThingDef thing = def as ThingDef;
            BuildableDef buildable = def as BuildableDef;
            IEnumerable<Type> tabs = live != null
                ? live.GetInspectTabs()?.Where(tab => tab != null).Select(tab => tab.GetType())
                : thing?.inspectorTabs;
            object[] buttons = includeButtons && live != null
                ? GizmoService.Build(live).Select(entry => GameActions.PressedBy(entry.Command)).Distinct().Select(Part).ToArray()
                : null;
            return Dto.Obj(
                Dto.Field("thingClass", Part(thing?.thingClass)),
                Dto.Field("inspectTabs", tabs?.Distinct().Select(Part).ToArray()),
                Dto.Field("buttonActions", buttons),
                Dto.Field("comps", thing?.comps?.Select(comp => Part(comp.compClass)).Where(part => part != null).ToArray()),
                Dto.Field("placeWorkers", buildable?.placeWorkers?.Select(Part).Where(part => part != null).ToArray()));
        }

        private static object Part(Type type)
        {
            return type == null ? null : Dto.Obj(Dto.Field("class", type.FullName),
                Dto.Field("mod", GameActions.IsGame(type.Assembly) ? "RimWorld" : GameActions.ModName(type.Assembly)));
        }

        private static bool Contains(string text, string query)
        {
            return text != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
