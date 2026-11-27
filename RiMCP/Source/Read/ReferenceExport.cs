using System;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Command;
using RiMCP.Util;
using Verse;

namespace RiMCP.Read
{
    internal static class ReferenceExport
    {
        public static BridgeResponse Export(BridgeRequest request, RouteMatch route)
        {
            var buildings = DefDatabase<ThingDef>.AllDefs.Where(def => def.BuildableByPlayer)
                .OrderBy(def => def.defName, StringComparer.Ordinal).Select(Building).ToArray();
            var research = DefDatabase<ResearchProjectDef>.AllDefs.OrderBy(project => project.defName, StringComparer.Ordinal)
                .Select(project => ResearchReadService.SerializeProject(project, ReadDetail.Full)).ToArray();
            var scenarios = ScenarioLister.AllScenarios().Where(scenario => scenario.showInUI)
                .Select(SetupCommandService.DescribeScenario).ToArray();
            var storytellers = DefDatabase<StorytellerDef>.AllDefs.Where(def => def.listVisible)
                .Select(ReadUtil.Def).ToArray();
            var difficulty = DefDatabase<DifficultyDef>.AllDefs.Select(ReadUtil.Def).ToArray();
            var identity = Dto.Obj(Dto.Field("schemaVersion", "1"),
                Dto.Field("gameVersion", VersionControl.CurrentVersionStringWithRev),
                Dto.Field("activePackageIds", ModsConfig.ActiveModsInLoadOrder.Select(mod => mod.PackageId).ToArray()));
            var setup = Dto.Obj(Dto.Field("scenarios", scenarios), Dto.Field("storytellers", storytellers), Dto.Field("difficulties", difficulty));
            string[] files = { "manifest.json", "buildings.json", "research.json", "setup.json" };
            object[] contents = { identity, buildings, research, setup };
            string root = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimMCP", "reference");
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            string final = Path.Combine(root, id);
            string staging = final + ".partial";
            try
            {
                Directory.CreateDirectory(staging);
                for (int i = 0; i < files.Length; i++)
                {
                    File.WriteAllText(Path.Combine(staging, files[i]), Json.Serialize(contents[i]) + "\n", new UTF8Encoding(false));
                }
                Directory.Move(staging, final);
            }
            catch (IOException exception)
            {
                throw new BridgeException(409, "Could not write the reference export: " + exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new BridgeException(409, "Could not write the reference export: " + exception.Message);
            }
            return ReadEnvelope.Ok(ReadContext.From(request), Dto.Obj(
                Dto.Field("directory", final), Dto.Field("files", files), Dto.Field("buildings", buildings.Length),
                Dto.Field("research", research.Length), Dto.Field("scenarios", scenarios.Length)));
        }
        private static object Building(ThingDef def)
        {
            ThingDef wall = DefDatabase<ThingDef>.GetNamedSilentFail("Wall");
            ThingDef[] materials = def.MadeFromStuff ? GenStuff.AllowedStuffsFor(def)
                .OrderBy(stuff => stuff.defName, StringComparer.Ordinal).ToArray() : new ThingDef[] { null };
            return Dto.Obj(Dto.Field("defName", def.defName), Dto.Field("label", ReadUtil.DefLabel(def)),
                Dto.Field("packageId", def.modContentPack?.PackageId),
                Dto.Field("category", def.designationCategory?.defName),
                Dto.Field("width", def.Size.x), Dto.Field("height", def.Size.z),
                Dto.Field("rotatable", def.rotatable),
                Dto.Field("defaultRotation", BuildPlacement.RotationName(def.defaultPlacingRot)),
                Dto.Field("terrainAffordance", def.terrainAffordanceNeeded?.defName),
                Dto.Field("placeWorkers", def.PlaceWorkers?.Select(worker => worker.GetType().FullName).ToArray()),
                Dto.Field("research", def.researchPrerequisites?.Select(project => project.defName).ToArray()),
                Dto.Field("materials", materials.Select(stuff => Dto.Obj(Dto.Field("stuff", stuff?.defName),
                    Dto.Field("cost", def.CostListAdjusted(stuff, false).Select(cost => Dto.Obj(
                        Dto.Field("def", cost.thingDef.defName), Dto.Field("count", cost.count))).ToArray()),
                    Dto.Field("canPlaceOverBuiltWoodWall", wall == null ? (bool?)null :
                        GenConstruct.CanPlaceBlueprintOver(def, wall, stuff, ThingDefOf.WoodLog)))).ToArray()),
                Dto.Field("coolerSides", typeof(Building_Cooler).IsAssignableFrom(def.thingClass) ?
                    Enumerable.Range(0, 4).Select(index =>
                    {
                        var rotation = new Rot4(index);
                        return Dto.Obj(Dto.Field("rotation", BuildPlacement.RotationName(rotation)),
                            Dto.Field("cooledOffset", ReadUtil.Cell(TemperatureControlRead.CooledCell(IntVec3.Zero, rotation))),
                            Dto.Field("exhaustOffset", ReadUtil.Cell(TemperatureControlRead.ExhaustCell(IntVec3.Zero, rotation))));
                    }).ToArray() : null));
        }
    }
}
