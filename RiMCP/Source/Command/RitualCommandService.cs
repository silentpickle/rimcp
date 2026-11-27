using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RiMCP.Command
{
    internal static class RitualCommandService
    {
        public static BridgeResponse Preview(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            ReadRequest query = context.Request;
            var input = new RitualInput
            {
                TargetId = query.Get("targetId"),
                X = query.OptionalInt("x"),
                Z = query.OptionalInt("z"),
                PawnId = query.Get("pawnId"),
                RoleId = query.Get("roleId"),
                RolePawns = query.List("rolePawns"),
                SpectatorIds = query.List("spectatorIds")
            };
            return ReadEnvelope.Ok(context, Prepare(context.Map, route["ritualId"], input).Describe());
        }

        public static BridgeResponse Start(BridgeRequest request, RouteMatch route)
        {
            RitualInput input = CommandUtil.ReadBody<RitualInput>(request);
            CommandContext context = CommandUtil.ContextFor(input.MapId);
            CommandUtil.RequireMap(context);
            string ritualId = route["ritualId"];
            RequestKey key = RequestKey.For("start_ritual", CommandUtil.ValidateRequestId(input.RequestId), context.Map,
                ritualId, input.TargetId, input.X, input.Z, input.PawnId, input.RoleId,
                string.Join(",", input.RolePawns ?? Array.Empty<string>()),
                string.Join(",", input.SpectatorIds ?? Array.Empty<string>()));
            return CommandReceipts.Once(context, key, () => Launch(context.Map, ritualId, input));
        }

        private static CommandResult Launch(Map map, string ritualId, RitualInput input)
        {
            RitualPlan plan = Prepare(map, ritualId, input);
            if (plan.Blockers.Count > 0)
            {
                AgentLog.NoChange("Ritual could not start: " + plan.Blockers[0], plan.Target.Thing);
                return new CommandResult(false, plan.Describe());
            }

            // The game uses these counts during the ceremony. Preview uses separate comp data.
            if (plan.Ritual.outcomeEffect != null)
            {
                foreach (RitualOutcomeComp comp in plan.Ritual.outcomeEffect.def.comps)
                {
                    comp.Notify_AssignmentsChanged(plan.Assignments, plan.Ritual.outcomeEffect.DataForComp(comp));
                }
            }
            AgentLog.About("Begin " + plan.Ritual.Label.StripTags(), plan.Target.Thing);
            plan.Ritual.behavior.TryExecuteOn(plan.Target, plan.Organizer, plan.Ritual,
                plan.Obligation, plan.Assignments, playerForced: true);
            LordJob_Ritual active = Find.IdeoManager.GetActiveRituals(map)
                .FirstOrDefault(job => ReferenceEquals(job.assignments, plan.Assignments));
            if (active == null)
            {
                plan.Blockers.Add("The game did not start the ritual.");
                return new CommandResult(false, plan.Describe());
            }

            AgentLog.Done("Started " + plan.Ritual.Label.StripTags(), plan.Target.Thing);
            return new CommandResult(true, plan.Describe(active.lord.GetUniqueLoadID()));
        }

        private static RitualPlan Prepare(Map map, string ritualId, RitualInput input)
        {
            if (!ModsConfig.IdeologyActive || Find.IdeoManager == null)
            {
                throw new CommandException(409, "Ideology expansion is not active.");
            }
            Precept_Ritual ritual = Faction.OfPlayer.ideos.AllIdeos
                .SelectMany(ideo => ideo.PreceptsListForReading.OfType<Precept_Ritual>())
                .FirstOrDefault(item => item.GetUniqueLoadID() == ritualId);
            if (ritual == null)
            {
                throw new CommandException(404, "Player ideoligion ritual not found.");
            }
            LocalTargetInfo target = Targets.Parse(map, input.TargetId, input.X, input.Z, "target");
            if (!target.IsValid)
            {
                throw new CommandException(400, "Give targetId or both x and z.");
            }

            var plan = new RitualPlan(ritual, target.ToTargetInfo(map));
            if (ritual.GetType() != typeof(Precept_Ritual) || ritual.behavior?.def == null || ritual.obligationTargetFilter == null)
            {
                plan.Blockers.Add("This ritual uses a different start flow.");
                return plan;
            }

            // Building commands use the first obligation the game accepts at the target.
            plan.Obligation = ritual.activeObligations?.FirstOrDefault(o => o.StillValid && ritual.CanUseTarget(plan.Target, o).canUse);
            if (plan.Obligation == null && (!ritual.isAnytime || !ritual.activeObligations.NullOrEmpty()))
            {
                plan.Blockers.Add("This ritual has no eligible obligation at this target.");
            }

            RitualTargetUseReport targetReport = ritual.CanUseTarget(plan.Target, plan.Obligation);
            if (!targetReport.canUse || !ritual.behavior.CanExecuteOn(plan.Target, plan.Obligation))
            {
                plan.Blockers.Add(targetReport.failReason ?? "The game does not allow this ritual at that target.");
            }

            // Command_Ritual handles cooldowns and the source role's ability gate.
            var command = new Command_Ritual(ritual, plan.Target, plan.Obligation);
            if (command.Disabled)
            {
                plan.Blockers.Add(command.disabledReason ?? "The game has disabled this ritual.");
            }
            if (plan.Blockers.Count > 0)
            {
                return plan;
            }

            Dictionary<string, Pawn> forced = SetRoleChange(plan, input);
            if (plan.Blockers.Count > 0)
            {
                return plan;
            }

            Rand.PushState();
            try
            {
                // Same candidate filter and assignment defaults as the ritual window.
                Dialog_BeginRitual.PawnFilter filter = (pawn, voluntary, allowOtherIdeos) =>
                    pawn.GetLord() == null && !pawn.IsSubhuman &&
                    (!pawn.RaceProps.Animal || (ritual.behavior.def.roles?.Any(role =>
                        role.AppliesToPawn(pawn, out _, plan.Target, skipReason: true)) ?? false)) &&
                    (!ritual.ritualOnlyForIdeoMembers || ritual.def.allowSpectatorsFromOtherIdeos ||
                        pawn.Ideo == ritual.ideo || !voluntary || allowOtherIdeos ||
                        pawn.IsPrisonerOfColony || pawn.RaceProps.Animal || (forced?.ContainsValue(pawn) ?? false));
                List<Pawn> required = plan.Organizer == null ? null : new List<Pawn> { plan.Organizer };
                plan.Assignments = Dialog_BeginRitual.CreateRitualRoleAssignments(ritual, plan.Target, map, filter, required, forced, null);
                plan.Assignments.FillPawns(filter, plan.Target);
                ApplyCast(plan, input, filter);
                if (plan.Organizer != null)
                {
                    plan.Assignments.SetRoleChangeSelection(plan.NewRole);
                }
                plan.Blockers.AddRange(BlockingIssues(plan));
            }
            finally
            {
                Rand.PopState();
            }
            return plan;
        }

        private static Dictionary<string, Pawn> SetRoleChange(RitualPlan plan, RitualInput input)
        {
            if (plan.Ritual.def != PreceptDefOf.RoleChange)
            {
                if (input.PawnId != null || input.RoleId != null)
                {
                    throw new CommandException(400, "pawnId and roleId are for role change rituals.");
                }
                return null;
            }
            if (string.IsNullOrWhiteSpace(input.PawnId) || string.IsNullOrWhiteSpace(input.RoleId))
            {
                throw new CommandException(400, "A role change needs pawnId and roleId. Use roleId none to remove a role.");
            }

            Pawn pawn = VisiblePawn(plan.Target.Map, input.PawnId);
            if (!pawn.IsFreeNonSlaveColonist || pawn.Ideo != plan.Ritual.ideo || pawn.GetLord() != null)
            {
                plan.Blockers.Add("The role change needs an available free colonist from this ideoligion.");
                return null;
            }

            plan.OldRole = pawn.Ideo.GetRole(pawn);
            if (input.RoleId != "none")
            {
                plan.NewRole = RitualUtility.AllRolesForPawn(pawn).FirstOrDefault(role => role.GetUniqueLoadID() == input.RoleId);
                if (plan.NewRole == null || (plan.NewRole.def.leaderRole && pawn.Ideo != Faction.OfPlayer.ideos.PrimaryIdeo))
                {
                    throw new CommandException(404, "That role is not available to this pawn's ideoligion.");
                }
                if (!plan.NewRole.Active || !plan.NewRole.RequirementsMet(pawn))
                {
                    plan.Blockers.Add("The role is inactive or the pawn does not meet its requirements.");
                    return null;
                }

                plan.RoleChangeWarning = RitualUtility.RoleChangeConfirmation(pawn, plan.OldRole, plan.NewRole);
            }

            plan.Organizer = pawn;
            return new Dictionary<string, Pawn> { { "role_changer", pawn } };
        }

        private static void ApplyCast(RitualPlan plan, RitualInput input, Dialog_BeginRitual.PawnFilter filter)
        {
            RitualRoleAssignments assignments = plan.Assignments;
            var overrides = new List<KeyValuePair<RitualRole, Pawn>>();
            foreach (string entry in input.RolePawns ?? Array.Empty<string>())
            {
                string[] pair = entry?.Split('=');
                if (pair == null || pair.Length != 2 || pair.Any(string.IsNullOrWhiteSpace))
                {
                    throw new CommandException(400, "Each rolePawns entry must be a roleId=pawnId pair.");
                }

                RitualRole role = assignments.AllRolesForReading.FirstOrDefault(item => item.id == pair[0]);
                if (role == null || (assignments.ForcedRolesForReading?.ContainsKey(role.id) ?? false))
                {
                    throw new CommandException(400, "Unknown or fixed ritual role '" + pair[0] + "'.");
                }
                Pawn pawn = VisiblePawn(plan.Target.Map, pair[1]);
                if (overrides.Any(slot => slot.Value == pawn))
                {
                    throw new CommandException(400, "A pawn can only be given one ritual role.");
                }
                overrides.Add(new KeyValuePair<RitualRole, Pawn>(role, pawn));
            }

            // Clear all overridden slots first so two pawns can swap roles.
            foreach (RitualRole role in overrides.Select(slot => slot.Key).Distinct())
            {
                foreach (Pawn pawn in assignments.AssignedPawns(role).ToArray())
                {
                    assignments.TryUnassignAnyRole(pawn);
                }
            }
            foreach (var slot in overrides)
            {
                Pawn pawn = slot.Value;
                RitualRole role = slot.Key;
                if (!assignments.AllCandidatePawns.Contains(pawn) ||
                    !filter(pawn, !(role is RitualRoleForced), role.allowOtherIdeos) ||
                    !assignments.TryAssign(pawn, role, out _))
                {
                    string reason = assignments.PawnNotAssignableReason(pawn, role);
                    plan.Blockers.Add(reason ?? ReadUtil.PawnName(pawn) + " cannot fill " + role.Label.Resolve() + ".");
                }
            }

            if (!input.SpectatorIds.NullOrEmpty())
            {
                assignments.SpectatorsForReading.Clear();
                foreach (Pawn pawn in input.SpectatorIds.Select(id => VisiblePawn(plan.Target.Map, id)).Distinct())
                {
                    if (!assignments.AllCandidatePawns.Contains(pawn) || assignments.RoleForPawn(pawn) != null ||
                        !assignments.TryAssignSpectate(pawn))
                    {
                        plan.Blockers.Add(ReadUtil.PawnName(pawn) + " cannot spectate this ritual with the selected cast.");
                    }
                }
            }
        }

        private static IEnumerable<string> BlockingIssues(RitualPlan plan)
        {
            Precept_Ritual ritual = plan.Ritual;
            RitualRoleAssignments assignments = plan.Assignments;
            if (assignments.Participants.Count == 0)
            {
                yield return "MessageRitualNeedsAtLeastOnePerson".Translate();
            }

            if (ritual.behavior.SpectatorsRequired() && assignments.SpectatorsForReading.Count == 0)
            {
                yield return "MessageRitualNeedsAtLeastOneSpectator".Translate();
            }

            if (!ritual.ignoreExtremeTemperatures && assignments.Participants.Any(pawn =>
                !pawn.IsPrisoner && !pawn.SafeTemperatureRange().IncludesEpsilon(plan.Target.Cell.GetTemperature(plan.Target.Map))))
            {
                yield return "CantJoinRitualInExtremeWeather".Translate();
            }

            if (ritual.ritualOnlyForIdeoMembers && !assignments.Participants.Any(pawn => pawn.Ideo == ritual.ideo))
            {
                yield return "MessageNeedAtLeastOneParticipantOfIdeo".Translate(ritual.ideo.memberName);
            }

            foreach (var group in assignments.AllRolesForReading.GroupBy(role => role.mergeId ?? role.id))
            {
                int needed = group.Count(role => role.required);
                Pawn[] assigned = group.SelectMany(assignments.AssignedPawns).ToArray();
                if (assigned.Length < needed)
                {
                    yield return "The ritual requires " + needed + " pawn(s) for " + group.First().Label.Resolve() + ".";
                }
                foreach (RitualRole role in group)
                {
                    foreach (Pawn pawn in assignments.AssignedPawns(role))
                    {
                        string reason = assignments.PawnNotAssignableReason(pawn, role);
                        if (reason != null)
                        {
                            yield return reason;
                        }
                    }
                }
            }

            foreach (string issue in ritual.obligationTargetFilter.GetBlockingIssues(plan.Target, assignments))
            {
                yield return issue;
            }

            if (ritual.outcomeEffect is RitualOutcomeEffectWorker_RoleChange)
            {
                // The native role-change blocker reads this count from mutable ritual comp data.
                // Calculate it in temporary data so preview cannot affect a ceremony.
                var comp = ritual.outcomeEffect.def.comps.OfType<RitualOutcomeComp_RoleChangeParticipants>().First();
                var data = (RitualOutcomeComp_DataRoleChangeParticipants)comp.MakeData();
                comp.Notify_AssignmentsChanged(assignments, data);
                if (comp.GetQualityFactor(ritual, plan.Target, plan.Obligation, assignments, data).quality < 0.5f)
                {
                    yield return "MessageNotEnoughSpectators".Translate(data.desiredParticipantCount, ritual.Label);
                }
                if (plan.NewRole == plan.OldRole)
                {
                    yield return "MessageRoleChangeChooseDifferentRole".Translate(plan.Organizer.Named("PAWN"));
                }
            }
            else if (ritual.outcomeEffect != null)
            {
                foreach (string issue in ritual.outcomeEffect.BlockingIssues(ritual, plan.Target, assignments))
                {
                    yield return issue;
                }
            }
        }

        private static Pawn VisiblePawn(Map map, string id)
        {
            Pawn pawn = string.IsNullOrWhiteSpace(id) ? null : Targets.FindVisibleThing(map, id) as Pawn;
            if (pawn == null || pawn.Dead)
            {
                throw new CommandException(404, "No living visible pawn '" + id + "' on this map.");
            }
            return pawn;
        }

        private sealed class RitualPlan
        {
            public readonly Precept_Ritual Ritual;
            public readonly TargetInfo Target;
            public readonly List<string> Blockers = new List<string>();
            public RitualObligation Obligation;
            public RitualRoleAssignments Assignments;
            public Pawn Organizer;
            public Precept_Role OldRole;
            public Precept_Role NewRole;
            public string RoleChangeWarning;

            public RitualPlan(Precept_Ritual ritual, TargetInfo target)
            {
                Ritual = ritual;
                Target = target;
            }

            public object Describe(string activeId = null)
            {
                return Dto.Obj(
                    Dto.Field("ritualId", Ritual.GetUniqueLoadID()),
                    Dto.Field("label", Ritual.Label.StripTags()),
                    Dto.Field("target", Targets.Describe((LocalTargetInfo)Target)),
                    Dto.Field("ready", Blockers.Count == 0),
                    Dto.Field("status", activeId != null ? "started" : Blockers.Count > 0 ? "blocked" : "ready"),
                    Dto.Field("activeId", activeId),
                    Dto.Field("blockers", Blockers.Select(issue => issue.StripTags()).Distinct().ToArray()),
                    Dto.Field("roleChange", Organizer == null ? null : Dto.Obj(
                        Dto.Field("pawn", SummaryDto.Pawn(Organizer)),
                        Dto.Field("fromRoleId", OldRole?.GetUniqueLoadID()),
                        Dto.Field("toRoleId", NewRole?.GetUniqueLoadID() ?? "none"),
                        Dto.Field("warning", RoleChangeWarning.NullOrEmpty() ? null : RoleChangeWarning.StripTags()))),
                    Dto.Field("roles", Assignments?.AllRolesForReading.Select(role => Dto.Obj(
                        Dto.Field("id", role.id),
                        Dto.Field("label", role.Label.Resolve().StripTags()),
                        Dto.Field("required", role.required),
                        Dto.Field("pawns", Assignments.AssignedPawns(role).Select(SummaryDto.Pawn).ToArray()))).ToArray()),
                    Dto.Field("spectators", Assignments?.SpectatorsForReading.Select(SummaryDto.Pawn).ToArray()));
            }
        }

        [DataContract]
        private sealed class RitualInput
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }
            [DataMember(Name = "targetId")]
            public string TargetId { get; set; }
            [DataMember(Name = "x")]
            public int? X { get; set; }
            [DataMember(Name = "z")]
            public int? Z { get; set; }
            [DataMember(Name = "pawnId")]
            public string PawnId { get; set; }
            [DataMember(Name = "roleId")]
            public string RoleId { get; set; }
            [DataMember(Name = "rolePawns")]
            public string[] RolePawns { get; set; }
            [DataMember(Name = "spectatorIds")]
            public string[] SpectatorIds { get; set; }
            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
