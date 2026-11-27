# RimMCP fork roadmap

The goal is a repeatable play loop in which an MCP client reads a compact account of what changed, sends a batch of orders, and lets RimWorld run until a meaningful condition stops it. The bridge should use the game's own checks, respect fog and access settings, and give callers a way to recover omitted detail.

This page tracks implementation in the fork. The colony workspace owns the detailed package order, playtest backlog, and save-specific handoffs. In adjacent checkouts, its plan is at `../rimworld/plan/mcp-roadmap.md` relative to the fork root.

## Landed through November 26, 2026

| Work | What it enables | Fork commit |
| --- | --- | --- |
| Wave 0. Play metrics, build identity, and reply budgets | Compare wall-time running share and reply bytes per in-game hour. Identify the mod and proxy under test. Page large reads under a 4 KiB default budget. | `73da9d2`, `94ac216` |
| W1.A. Selectors, field projections, and batched reads | Ask for a room, zone, rectangle, or radius and return the fields needed for the next decision. Read several IDs in one call. | `29bd0f1` |
| W1.B. Advance watcher table | Observe shared game state once per sample and keep existing event order and stop behavior. Extend it with new stop conditions and run summaries. | `bfcf73c` |
| W1.C and W2.4. Map layers and thin survey | See a whole-map overview, zoom to a named tile, and use exact symbol counts and roof or designation overlays when planning. | `d031382`, `c4d74b9` |
| W2.5–W2.6. Movers and notable things | Find visible parties, wildlife, landmarks and ore, then inspect an actual thing ID or narrow a map-wide building read. | `65188cd` |

The latest disposable-save checks measured a **3,453-byte** overview with movers and notable things. A 17-member trade party appeared as one group, and a surveyed component vein opened through `get_building`. The hostile fields were exercised with a synthetic hostile trader checkpoint. A live raid stage still needs verification. [Survey workflow and checks](survey-workflow.md)

## Build-out direction

| Direction | Planned work and dependency |
| --- | --- |
| Summarize an advance | **W2.2 is next.** Use the watcher table to report completed blueprints, research progress, food change, new letters, and downed or idle colonists. **W2.1** adds stop conditions for construction, research, idle pawns and caravan arrival using the watcher and selector infrastructure. |
| Make turns smaller and keep time moving | **W3.1** combines advance summaries and mover data into a digest targeted at 2 KiB or less, with revisions and deltas for follow-up reads. **W3.2** uses that digest and the expanded watchers to keep the game running between turns and pause on a condition. |
| Plan against the map | **W2.3** extends the layer registry with terrain, use and thing layers. That feeds the shared preview evaluator, derived maps such as build sites and reachability, and reusable room templates. |
| Explain unfamiliar things | Build on notable finds and their source mods with inspect text, info-card details, reference exports and thing notes. Agents can then move from a survey result to the right detail or game action. |
| Extend play to campaigns and multiple agents | Use the live loop and digest for caravan formation, travel supplies and a verified round trip. Put time and decisions under one controller, with narrower tool access for maintenance agents. |

The survey and watcher table are the two inputs to the planned digest. Tool schemas enter through the shared manifest and the game bridge routes them to the native handlers. [Tool manifest](../Shared/ToolManifest.cs) · [Bridge routes](../RiMCP/Source/Bridge/BridgeRouter.cs) · [Survey](../RiMCP/Source/Read/MapSurveyReadService.cs) · [Watchers](../RiMCP/Source/Command/AdvanceWatchers.cs)

For new packages, run the build, proxy tests and compatibility harness, then verify the normal MCP path on a disposable save. When changing a read, record the old and new payload sizes on comparable calls. [Verification workflow](survey-workflow.md#verify-a-build)
