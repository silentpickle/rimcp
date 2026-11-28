# RimMCP getting started

Open RimWorld with the mod enabled. In **Connection → Copy MCP config**, copy the stdio settings into your MCP client and keep the token private. Start with `get_game_context` to confirm the game state, map, load ID, and build. For a loaded colony, use `get_colony_status` or `get_turn_digest` for a compact first read. Drill down only when a decision needs more detail. Try new workflows on a disposable save first.

## Keep the game moving

Use one proxy per agent against the same game and confirm the advertised tools before assigning work. Use `RIMCP_ROLE` for attribution and `RIMCP_TOOL_GROUPS` or `RIMCP_TOOLS` to set permissions. Share the load and map IDs with every assignment.

- **Main** owns the clock, dialogs, and saves. Review current risks before starting `advance_time` with `untilStopped: true` and selected watchers. Request `digest: true` and follow the run ID as needed. Respect player pause requests and blocking dialogs. Send the digest and receipts to the other roles at meaningful stops.
- **Steward** reads the assigned area and applies only delegated changes. Use a stable `requestId` on retries. Return the outcome and receipt to main.
- **Scribe** writes from main's digest and receipts while play continues. Zero live reads is normal. Preserve evidence ticks and gaps. When a missing event matters, use `get_events` with the last processed cursor on the same load.
- **Sentinel** triages watcher stops, checks relevant threats or alerts, and escalates urgent changes with map and tick. Main owns the run and any response.
- **Land advisor** compares candidate sites for soil, resources, access, and defensibility. Record the load, map, tick, and site constraints. Use read-only game tools.
- **Builder** previews one approved construction assignment, reports material and placement blockers, and submits approved orders when granted the tools. Verify finished work separately from placement receipts.
- **Researcher** checks current research and loaded definitions, then consults RimSage or reference material as needed. Return prerequisites and evidence to main for a project decision.

The host starts and coordinates the agents. Prefer one watcher-driven run and shared evidence over repeated whole-map polling by each role. Reads and delegated orders can run while the clock is moving when the game permits them.

## References

- [Install and connect RimMCP](https://github.com/silentpickle/rimcp#using-the-mod)
- [Set up separate role connections](https://github.com/silentpickle/rimcp/blob/main/docs/wave-5-roles.md)
- [Use watcher-driven time and compact reads](https://github.com/silentpickle/rimcp/blob/main/docs/wave-3-workflow.md)
- [Research versioned game code and definitions with RimSage](https://github.com/silentpickle/rimcp/blob/main/docs/source-research.md)
- [RimWorld Wiki](https://rimworldwiki.com/wiki/Main_Page) for general mechanics and [RimWorld](https://rimworldgame.com/) for official game information. If your workspace provides Rimdex Reference, it can search an offline wiki snapshot.
