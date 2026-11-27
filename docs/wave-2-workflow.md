# Wave 2 workflow and verification

Wave 2 connects whole-map observations to individual things and game-native controls. The run summary gives the planned turn digest a bounded record of what changed. The additional stop conditions let a caller wait for particular work while the game runs. [Detailed roadmap](roadmap-detail.md)

## Wait for work and read what changed

Place a blueprint, take its ID from `results[].id`, then pass it to `advance_time`. Use a recent `expectedLoadId` and a fresh `requestId` for a new advance. This example waits for the specified blueprint while keeping the existing emergency stops.

```json
{
  "hours": 8,
  "stopOn": ["built", "death", "downed", "injured", "mentalBreak", "hostiles", "lowFood", "threatLetter"],
  "watch": {"mapId": "0", "blueprintIds": ["<blueprint ID>"]},
  "waitSeconds": 60
}
```

A `watch.rect` takes `x`, `z`, `width`, and `height`. When IDs and a rectangle are both supplied, construction has to match both to stop the run. The rectangle uses the building anchor cell. Without `watch`, `built` can stop for construction on any loaded map. The bridge tracks a blueprint as it becomes a frame. The other new stops are `researchDone` for an active project completing, `pawnIdle` when an undrafted colonist becomes idle, and `caravanArrived` when a player caravan reaches its route destination. Explicit `stopOn` replaces the default list. New stops are opt-in.

Read `summary` on the advance result or `data.run.summary` from `get_advance`. It counts completed construction, new letters, and unique colonists who went down or became idle. It reports stored nutrition change summed over loaded home maps and the before and after progress for up to five changed research projects. Those counts cover the run even when the event timeline reaches its 50-entry limit. The construction selector affects when the run stops. The summary covers observed changes across loaded maps.

A force-pause window can end the run on its own. When research completion also matches `researchDone`, the run returns that event along with `blockedBy`. Read the interaction's `interactionId`, `page`, and option ID from `blockedBy[].dialog`, then pass them to `answer_dialog`. Use a fresh `requestId` to make the answer safe to retry. If the window is still becoming interactive, wait briefly and retry. Start a new advance after the dialog clears.

## Inspect a place and its controls

Use `get_map_survey` to find a tile, then `get_map_view` for its grids or counts. For exact terrain, local temperature, and the things on individual cells, ask for `as: "cells"` on a rectangle of up to 64 cells at scale 1. Use a small rectangle when asking for many layers.

```json
{
  "x": 152, "z": 105, "width": 2, "height": 1,
  "as": "cells",
  "layers": ["terrain", "fertility", "passable", "zone", "area", "room", "temperature", "building", "blueprint", "item", "plant", "pawn"]
}
```

Thing layers give IDs, temperatures are in Celsius, and fogged cells return only coordinates and an unexplored flag. `passable` reports walkability at the cell. Route reachability also depends on the pawn and its path. Add `roof` and `designations` for roof and designation def names. A `room` number is local to the loaded game's room grid.

- Request `fields: ["id", "inspectText"]` on building, pawn, or item lists. For one visible thing, call `get_info` with `thingId` and `include: ["inspectText"]`.
- Call `get_info` with a `thingId` or a `defName`. Use `query` to filter the stat rows, `include: ["explanations"]` for the game's stat reports, and the cursor to page them. The reply includes research prerequisites and class names with their owners. For a live thing, `include: ["buttons"]` adds button action classes.
- Call `list_gizmos` to find an untargeted action or a stepper. `use_gizmo` accepts `times` from 1 to 20 and reports inspect text when available. `set_stepper` takes `thingId`, `revision`, `stepper`, and `target` in the displayed units. It uses the shortest available sequence of signed steps within the press limit and checks the inspect value after every click. If several numeric inspect fields match, give the `inspectLabel`. A detected Reset button can be used directly with `use_gizmo`.
- Check the three nutrition buckets in `get_colony_status`. When loose food is forbidden, `food.unforbid` gives a `designate` call for the first batch. Reread the colony after applying it.
- Use `preview: true` with `place_blueprint`, `place_floor`, `build_roof`, or `smooth_walls` to preview under the existing tool permissions. Previews leave the agent log alone and work during a game hold while the map remains readable.

## Preview and start an Ideology ritual

1. Get the ideoligion ID from `list_ideoligions`. Call `get_ideoligion` with that ID and `view: "rituals"` to read the role holders, ritual precepts, and active rituals on the selected map.
2. Call `preview_ritual` with a ritual precept ID and a visible `targetId` or both `x` and `z`. The game checks what location each ritual accepts. For role change, give the colonist's `pawnId` and the destination role precept's `roleId`. Use `roleId: "none"` to remove the current role.
3. Read `blockers`, the cast, and any role-change warning. To choose the cast, give `rolePawns` as entries such as `"speaker=Human285"` using the preview's ritual slot IDs. Use `spectatorIds` to choose a spectator list. Omit either selection for the game's defaults.
4. When the preview is ready, pass the same arguments to `start_ritual` with a fresh `requestId` and the current `expectedLoadId`. A successful start returns an `activeId` that appears in the ideoligion's rituals view. After advancing time, reread the role holders to see whether the ceremony succeeded.

This entry point uses the standard player Ideology ritual flow. Royalty ceremonies, Anomaly rituals, and rituals with a separate start flow have their own controls.

## Preview a new-game scenario

Run `list_scenarios`, then `preview_scenario` with the exact name and, when needed, its category. The preview lists starting human pawn counts and choices, declared supplies and where they appear, declared animals and mechs, arrival method, and any manual setup screens. A random animal entry describes a game roll. Mod parts can add gear or effects beyond the standard declarations. The preview works before setup and while a colony is loaded.

## Verify a build

1. Run `scripts/check-rimcp.sh` against RimWorld 1.6 managed assemblies. Rebuild the proxy as well as the DLL. On a disposable copy, verify both via `tools/list` and `get_game_context`.
2. On the furnished-room checkpoint, use a map-cell read at the cooler, compare its ID with `get_building`, and query `get_info` for Power. Inspect its stepper and change the target by one large and one small step. Confirm the displayed value and replay the request ID. Use the food unforbid hint and confirm the nutrition moves from forbidden to allowed.
3. Place construction blueprints and advance with `stopOn: ["built"]` and a scoped `watch`. Compare the returned construction and the cell's new building ID. The compatibility harness checks previews while a held operation blocks matching placement orders.
4. On a copied save with an active project near completion, run once with `stopOn: []` and once with `stopOn: ["researchDone"]` from the original copy. Use the native `OK` option through `answer_dialog`, then verify the clock can advance again. A 1.6.4871 trial used Solar Panels at 599.99 of 600 points and let a colonist finish it at a normal research bench.
5. On a disposable Ideology save, preview a role change at an ideogram, try an invalid target or cast, then launch the valid cast. Replay the same request ID and compare the active ID. Advance until the role holder changes, then preview and start a leader speech.
6. At the main menu, preview Crashlanded and Naked Brutality. Compare their reported starting counts and supplies. Repeat a preview while a disposable colony is paused and compare the game context before and after.

## Recorded results

The November 27 trial used disposable RimWorld 1.6.4871 saves with the rebuilt bridge and proxy. A scoped stool blueprint stopped the advance after it became a building. The summary retained its completion and the new letter. An idle stop beyond the event limit and preview access during a held operation were exercised in the compatibility harness.

Live calls also verified exact cells and fog masking, a cooler's info-card explanation, a two-click Fahrenheit stepper, repeated actions, the food unforbid hint, and close-range melee selection with a native minimum-range weapon. The four previews left the agent log unchanged. The research dialog adopted and answered successfully both with and without the explicit research stop. After each answer the next advance moved the clock. A live caravan arrival remains to be checked.

The copied Benevolina save verified role-change blockers, an exact retry, active ceremony readback, and the leader role taking effect after the ceremony. The new leader then started a speech with a selected cast, and an overlapping start was blocked. Main-menu scenario previews reported three of eight candidates and declared supplies for Crashlanded, and one pawn with an empty supply list for Naked Brutality. With the colony loaded, the same Crashlanded preview left the clock unchanged.
