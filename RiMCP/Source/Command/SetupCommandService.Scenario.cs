using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static partial class SetupCommandService
    {
        public static BridgeResponse PreviewScenario(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            Scenario scenario = FindScenario(context.Request.Get("scenario"), context.Request.Get("category"));
            ScenPart[] parts = scenario.AllParts.ToArray();
            var preview = DescribeScenario(scenario);
            preview["description"] = scenario.description;
            preview["startingPawns"] = parts.OfType<ScenPart_ConfigPage_ConfigureStartingPawnsBase>()
                .Select(DescribePawnStart)
                .ToArray();
            preview["startingSupplies"] = parts.OfType<ScenPart_ThingCount>()
                .Where(part => part is ScenPart_StartingThing_Defined || part is ScenPart_ScatterThingsNearPlayerStart)
                .Select(part => Dto.Obj(
                    Dto.Field("def", ReadUtil.Def(SetupController.Get(part, "thingDef") as ThingDef)),
                    Dto.Field("stuff", ReadUtil.Def(SetupController.Get(part, "stuff") as ThingDef)),
                    Dto.Field("count", SetupController.Get(part, "count")),
                    Dto.Field("quality", SetupController.Get(part, "quality")?.ToString()),
                    Dto.Field("placement", part is ScenPart_StartingThing_Defined ? "withPawns" : "nearStart")))
                .ToArray();
            preview["startingAnimals"] = parts.OfType<ScenPart_StartingAnimal>().Select(part =>
            {
                PawnKindDef kind = SetupController.Get(part, "animalKind") as PawnKindDef;
                return Dto.Obj(
                    Dto.Field("kind", ReadUtil.Def(kind)),
                    Dto.Field("random", kind == null),
                    Dto.Field("count", kind == null ? null : SetupController.Get(part, "count")));
            }).ToArray();
            preview["startingMechs"] = parts.OfType<ScenPart_StartingMech>()
                .Select(part => ReadUtil.Def(SetupController.Get(part, "mechKind") as PawnKindDef)).ToArray();
            preview["arrival"] = parts.OfType<ScenPart_PlayerPawnsArriveMethod>()
                .Select(part => SetupController.Get(part, "method")?.ToString()).FirstOrDefault();
            return ReadEnvelope.Ok(context, preview, detailSection: "startingSupplies");
        }

        private static Dictionary<string, object> DescribeScenario(Scenario scenario)
        {
            string[] manual = UnsupportedScreens(scenario);
            return Dto.Obj(
                Dto.Field("name", scenario.name),
                Dto.Field("category", scenario.Category.ToString()),
                Dto.Field("summary", scenario.summary),
                Dto.Field("supported", manual.Length == 0),
                Dto.Field("manualScreens", manual.Length == 0 ? null : manual));
        }

        private static object DescribePawnStart(ScenPart_ConfigPage_ConfigureStartingPawnsBase part)
        {
            int? count = null;
            switch (part)
            {
                case ScenPart_ConfigPage_ConfigureStartingPawns standard:
                    count = standard.pawnCount;
                    break;
                case ScenPart_ConfigPage_ConfigureStartingPawns_KindDefs kinds:
                    count = kinds.kindCounts.Sum(item => item.count);
                    break;
                case ScenPart_ConfigPage_ConfigureStartingPawns_Xenotypes xenotypes:
                    count = xenotypes.xenotypeCounts.Sum(item => item.count);
                    break;
                case ScenPart_ConfigPage_ConfigureStartingPawns_Mutants mutants:
                    count = mutants.mutantCounts.Sum(item => item.count);
                    break;
            }

            return Dto.Obj(
                Dto.Field("part", part.def.defName),
                Dto.Field("count", count),
                Dto.Field("choices", part.pawnChoiceCount));
        }
    }
}
