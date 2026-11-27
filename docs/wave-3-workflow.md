# Wave 3 workflow and verification

## Read the turn digest

Call `get_turn_digest` after an advance, or before deciding what needs attention. It gives the selected map's pause state, blocking window types, immediate risks, ranked movers, and food. Recent letters and the latest advance summarize game-wide activity. The raw bridge reply is capped at 2,048 UTF-8 bytes and accepts a smaller `maxBytes` within the usual bounds.

Use `get_game_context` or `get_advance` for the full blocking interaction, `get_map_survey` for mover detail, `get_colony_status` for wider colony risks, and `get_events` for letters and messages. The digest uses the most recent advance in the current load. It can show an older run if the game has moved since that advance.

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

The next package uses this read for an opt-in run-between-turns mode. The current advance and pause behavior still uses the existing controls.
