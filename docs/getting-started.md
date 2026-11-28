# RimMCP getting started

Open RimWorld with the mod enabled. In **Connection → Copy MCP config**, copy the stdio settings into your MCP client and keep the token private. Start with `get_game_context` to confirm the game state, map, load ID, and build. For a loaded colony, use `get_colony_status` or `get_turn_digest` for a compact first read. Drill down only when a decision needs more detail. Try new workflows on a disposable save first.

## Keep the game moving

Use one proxy per agent against the same game and confirm the advertised tools before assigning work. Use `RIMCP_ROLE` for attribution and `RIMCP_TOOL_GROUPS` or `RIMCP_TOOLS` to set permissions. Share the load and map IDs with every assignment.

- **Main** owns the clock, dialogs, and saves. Review current risks before starting `advance_time` with `untilStopped: true` and selected watchers. Request `digest: true`, and follow a running advance with `get_advance` and `digest: true`. Respect player pause requests and blocking dialogs. Send the digest and receipts to the other roles at meaningful stops.
- **Steward** reviews work, schedules and stalled jobs from main's evidence (`list_work` sweep, `get_construction`), reads the assigned area when one fact is missing, and applies only delegated changes. Use a stable `requestId` on retries. Return the outcome and receipt to main.
- **Scribe** writes from main's digest and receipts while play continues. Zero live reads is normal. Preserve evidence ticks and gaps. When a missing event matters, use `get_events` with the last processed cursor on the same load.
- **Sentinel** triages watcher stops, checks relevant threats or alerts, and escalates urgent changes with map and tick. Main owns the run and any response.
- **Land advisor** compares candidate sites for soil, resources, access, and defensibility. Record the load, map, tick, and site constraints. Use read-only game tools.
- **Builder** previews one approved construction assignment, reports material and placement blockers, and submits approved orders when granted the tools. Verify finished work separately from placement receipts.
- **Researcher** checks current research and loaded definitions, then consults RimSage or reference material as needed. Return prerequisites and evidence to main for a project decision.

## Keep the colony alive

Dangers are defined once. The digest's `risks` count them, `get_colony_status` `topRisks` and `list_threats` `dangers` list them worst first with examples and what to read next, and the `breakRisk` and `temperature` stop conditions pause a run when one turns serious.

At each checkpoint, check the few settings that decide survival, and fix them before turning to projects:

- **Work.** Turn on manual priorities once roles differ (a 1–4 priority does it) and match each pawn's work to skills and current health. A pawn in bed rest does no work; the sweep marks it `patient`, and the `uncoveredWork` danger names vital work (firefighting, doctoring, cooking, harvesting) that is needed now and that nobody up and free can do. `list_work` with `sweep: true` shows each pawn's job, priorities and schedule in about 1 KB.
- **Schedules and allowed areas.** Keep sleep, work and recreation blocks, and use allowed areas to keep pawns out of danger (indoors during a raid or heat wave) or apart from each other.
- **Food and animals.** Watch stored days and the kind of food. Cooking every crop leaves no raw food, and taming, including wild people, needs raw food. Check pets and pens.
- **Temperature, rooms and health.** The `temperatureHarm` danger names heatstroke or hypothermia and its stage, and `unsafeTemperature` counts pawns standing somewhere hotter or colder than they can take, before harm starts. Read rooms with `get_environment`. Coolers need power and a conduit.
- **Mood.** `breakRisk` uses each pawn's own minor, major and extreme break thresholds from the game. `get_pawn` with `include: ["thoughts"]` shows why.
- **Stalled work.** Before changing priorities, ask why: `get_construction` separates missing or unusable material, obstacles builders clear themselves, and missing builders. A pawn's right-click menu (`list_pawn_actions`) gives the game's reason for a refused job, such as "Not enough food".

The host starts and coordinates the agents. Prefer one watcher-driven run and shared evidence over repeated whole-map polling by each role. Reads and delegated orders can run while the clock is moving when the game permits them.

## References

- [Install and connect RimMCP](https://github.com/silentpickle/rimcp#using-the-mod)
- [Set up separate role connections](https://github.com/silentpickle/rimcp/blob/main/docs/wave-5-roles.md)
- [Use watcher-driven time and compact reads](https://github.com/silentpickle/rimcp/blob/main/docs/wave-3-workflow.md)
- [Research versioned game code and definitions with RimSage](https://github.com/silentpickle/rimcp/blob/main/docs/source-research.md)
- [RimWorld Wiki](https://rimworldwiki.com/wiki/Main_Page) for general mechanics and [RimWorld](https://rimworldgame.com/) for official game information. If your workspace provides Rimdex Reference, it can search an offline wiki snapshot.
