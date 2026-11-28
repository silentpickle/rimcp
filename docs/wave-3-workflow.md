# Wave 3 workflow and verification

## Read the turn digest

Call `get_turn_digest` after an advance, or before deciding what needs attention. It gives the selected map's pause state, blocking window types, immediate risks, ranked movers, and food. Recent letters and the latest advance summarize game-wide activity. The raw bridge reply is capped at 2,048 UTF-8 bytes and accepts a smaller `maxBytes` within the usual bounds.

Both `get_turn_digest` and `advance_time` with `digest: true` include blocking windows in `data.blockingDetails`. Each entry identifies the window and whether the player needs to close it. For an answerable prompt, use the interaction ID, page, and selected option ID from `dialog` with `answer_dialog`. If `budget.omittedFields` lists `blockingDetails`, use `get_game_context` or `get_advance` for the full prompt.

Use `get_map_survey` for mover detail, `get_colony_status` for wider colony risks, and `get_events` for letters and messages. The digest uses the most recent advance in the current load. It can show an older run if the game has moved since that advance.

## Follow changes to a particular read

On `get_map_survey`, `list_pawns`, `list_buildings`, `find_things`, `list_resources`, `list_work`, or `list_zones`, request `trackRevision: true`. Save the returned `revision` with the reply. Make the same query again, passing that token as `since`. If using pages, each page has its own revision. Match the original selectors, projection, map, offset, and limit.

- `delta: true` means merge changed fields in `data` and discard any names in `removedFields`. On a survey, each supplied part is a replacement. On a list, the full reply's `rowIds` align with its rows, including projections that omit IDs.
- `listDelta` identifies the affected list with `field`. Apply `upsert` by ID, drop `removed` IDs, and use `order` when supplied. An empty `data` and no other changes mean the view stayed the same. Keep the new revision for the next call.
- `reset: true` means replace the baseline with the full reply. This can happen after a reload, eviction from the 48-revision cache, or a query change. A reply cut by the byte budget has no new revision. Narrow the query or increase `maxBytes` if needed.

## Verify a build

1. Rebuild the mod and proxy. Check the live DLL hash with `get_game_context` and check that `tools/list` advertises `get_turn_digest`, `trackRevision`, and `since`.
2. On a disposable save, query `list_pawns` with `fields: ["name", "drafted", "position"]` and track a revision. Confirm an empty delta. Draft one pawn while paused and check that the delta upserts only that pawn even though the projection omitted its ID.
3. Track a survey with `overview: false` and `include: ["movers", "notable"]`. Let time run and compare it with the earlier revision. Read the digest and compare its run ID with the advance. Reload a save and confirm a previous revision yields a reset.
4. Reuse the Wave 2 research fixture and stop at research completion. Check the digest for the blocking window and finished project. Measure the raw bridge JSON bytes before and after the modal.

## Recorded results

The November 27 isolated RimWorld 1.6.4871 trial produced raw replies of 839 bytes paused, 1,121 bytes after an advance, and 1,291 bytes at a research-complete dialog. The digest showed the blocking window and finished project. Drafting one pawn upserted one projected row, and reloading reset the revision. All seven revision-enabled reads returned empty deltas when repeated on a paused fixture.

## Keep the game moving between turns

Start `advance_time` with `untilStopped: true` and omit `hours` to watch until a selected event, manual time control, access loss, a blocking window, or a game change. With `hours`, the clock also has a finite endpoint. A run needs a time limit or at least one watcher. Calls with `digest: true` return the compact turn view and put the run ID and status under `data.advance`. The raw command reply has a 2 KiB cap. Use `get_advance` for the full timeline and dialog options.

A `waitSeconds` timeout returns the current state and leaves the run in place. While it runs, send reads or orders through the same bridge. One run owns time per loaded game. Pause with `set_game_speed` when you need manual control. Read the current risks before starting a run because watchers are triggered by changes.

## Verify dialogues and asynchronous stops

Answerable prompts appear in `data.blockingDetails` of `get_turn_digest`, `advance_time(digest: true)` and `get_advance(digest: true)`. Each has the interaction ID, page, question, options and, for naming windows, the displayed names under `fields`. Answer with `answer_dialog` and a fresh `requestId`; a retry with the same ID returns the first result without acting again. A closed or changed page, a changed load, or a player's answer is refused. Windows without an adapter stay `needsPlayer: true`.

To follow a run without the full timeline, call `get_advance` with `digest: true` and `waitSeconds`. In its `advance` section, `endReason`, `endTick` and `blockedAtStop` describe how the run ended. The top-level `blockedBy` and `blockingDetails` describe the game at the reply's tick, so a prompt that cleared after the stop, or a new one, is shown as it is now. If `budget.omittedFields` lists `blockingDetails`, read `get_game_context`.

### Recorded results

November 27, RimWorld 1.6.4871, fresh stdio proxy (105 tools), DLL `8b3f9757…`. Byte counts are UTF-8 bridge payloads as the client received them.

- **Research-complete OK** (previous build `9281bd81…`, copy of Tedshobrad at tick 384049). The compact `advance_time` reply carried the Battery prompt, both options, `dialog-1`, page 1 and `needsPlayer: false`. The answer cleared the window and the game stayed paused. The same request ID returned `changed: false`; a fresh ID on the answered page was refused. Decision: two calls.
- **Naming window** (fixture loaded at tick 150000, stopped at 258200). `get_advance(digest: true)` returned 2,018 bytes with `kind: "naming"`, both generated names, the `accept` option and `blockedAtStop`. The answer (260 bytes) named the faction and settlement: `list_world` showed the player faction name and the map label changed. Retry and stale-page checks behaved as above. Decision: two calls, 2,278 bytes.
- **Asynchronous follow-up.** `advance_time` with a 5-second wait returned a running digest of 1,101 bytes. `get_advance(digest: true, waitSeconds: 85)` returned 1,174 bytes for the same run.
- **Work sweep.** For three colonists, `list_work` was 4,055 bytes by default and 6,614 bytes with schedules. `sweep: true` was 1,453 bytes, and 967 bytes with four chosen work types.
- **Construction.** After placing 12 steel walls and one plasteel wall, `get_construction` reported `ready: 9, clearing: 3, needsMaterial: 1` and a steel shortfall of 60 remaining, 37 usable, 1,050 on the map (mostly forbidden). Two in-game hours later, 7 walls were built and the remaining 5 read `materialUnusable` (25 remaining, 2 usable) in 596 bytes.
- A multi-choice prompt was not reproduced: this colony's earlier Hospitality answer stops that prompt recurring. The naming and research windows use the same serializer and page rules.

## Plan on the map

- Use `check_placement(effects: true)` on one explored cell. Cooler, lamp, and turret placement capture uses the game's stock overlay code. Each group gives a name, an RGBA hex color, and cell runs in `[z, startX, length]` order. `place_blueprint` previews can request overlays for up to four supported anchors.
- For a visible installed source, use `get_map_view` with `layers: ["effects"]` and `effectThingId`. It uses the selected turret overlay or the placement geometry for cooler and lamp. The effects layer needs Harmony and the game must display the source map. Coverage is limited to the stock hooks the fork has checked.
- `get_map_view` adds `soilPatch`, `ore`, `buildSite`, `reach`, and `chokepoints`. Read `derivedTick` for freshness and use `refreshDerived: true` after a paused change. The bridge samples explored cells and native region links on the main thread. Patch grouping and graph analysis run on a worker. Reach is explored region connectivity from visible colonists. Chokepoints mark regions whose removal splits that graph. Check the actual cell and building placement before committing.
- Use `preview: true, verdicts: true` on blueprint and surface orders to get cell verdicts with zero-based input entry or section indices. Costs include the plan, outstanding queued costs, un-forbidden stock on the map, and their difference. Roof preview can report `awaitingSupport` when the support is queued. A boundary with a pen marker estimates enclosure, forage, and growing-zone overlap. Use a small area or raise `maxBytes` when requesting many verdicts. Reuse the previewed selection for the write.

## Reuse reference and work reads

Run `export_reference` to write the building catalog, research tree, and setup lists to a new directory under the game's save-data folder. The manifest identifies the game build and active mod order. For a routine work and schedule review, `list_work` with `sweep: true` gives each pawn's job, priorities and schedule blocks; add `workTypes` to narrow it. For a stalled build, `get_construction` names the outstanding site, its verdict, and material that is on the map but not usable there. Pass `include: ["suggestions"]` to `list_work` for skill- and passion-ranked entries compatible with `set_work_priorities`. Review workload and the manual-priority setting before applying a selection. The suggestions do not change the game.

For local inspection of game and mod code, use the [source cache guide](source-research.md).

The November 27 live check captured stock cooler, lamp, and turret placements and a built turret overlay. A full 250 × 250 layer read took 108.1 ms end to end on refresh and 37.0 ms on a repeat. A compact running reply measured 1,075 bytes. A suggested Mining order applied during the run and the explicit pause stopped it.
