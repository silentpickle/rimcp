# Survey workflow and verification

Use the survey to decide **where to look**, named tiles to inspect the local map, and filtered lists or ID reads to get details for a decision. The Wave 3 turn digest reuses the mover data and the watcher table's record of what changed during an advance. [Fork roadmap](roadmap.md)

## Try the workflow

Start a fresh MCP client with the fork's mod DLL and stdio proxy built from the same checkout. Work on a disposable save for repeatable checks. Before advancing a game, confirm the intended map and `loadId` in `get_game_context`. Use `expectedLoadId` and a unique `requestId` on actions that accept them. [Build instructions](../README.md#building-from-source)

| Call | What to look for |
| --- | --- |
| `get_map_survey {}` | Whole-map overview, `rowZ` coordinates, largest connected regions, and the tile lattice. |
| `get_map_survey {"include":["movers","notable"]}` | Visible parties, herds and landmarks. Use heading only when the reply reports an active path. Group limits can omit lower-ranked results. |
| `get_map_survey {"include":["notable"],"overview":false,"notableLimit":12,"maxBytes":8192}` | A parts-only read with room for more feature groups. Note a representative `sampleId` and the group's `bounds`. |
| `get_map_view {"tile":"x4z2","layers":["roof","designations"]}` | A local grid at the named tile's origin if it exists on your map. Edge tiles crop in place. Add `"as":"counts"` for exact underlying symbol counts. |
| `list_buildings {"scope":"map","area":{"x":128,"z":64,"width":32,"height":32},"fields":["id","def","cell"]}` | Visible buildings and exposed ore in the same area. Replace the rectangle with your tile or landmark coordinates. |
| `get_building {"id":"<building sampleId>","fields":["id","def","cell"]}` | The anchor of a surveyed building or exposed ore vein. Use the actual ID from the prior read. |

Use `nextCursor` with the same filters when a list declares more rows. If the reply reports a byte-budget omission, narrow the area, request fewer fields, or increase `maxBytes`. The default budget is 4,096 UTF-8 bytes for the game payload. Measure JSON payload bytes consistently when comparing runs.

## Verify a build

1. On macOS, run `scripts/check-rimcp.sh` from the fork root. It builds the DLL against RimWorld 1.6 assemblies, tests the proxy, and runs the compatibility harness under Mono. At `65188cd` the recorded result was 21 proxy tests and 95 passing Mono checks, with the three expected Unity-native failures. [Check script](../scripts/check-rimcp.sh) · [Harness notes](../tests/CompatibilityRegression/README.md)
2. In the MCP client, run `tools/list` and then `get_game_context`. Confirm `get_map_survey` is advertised with the new include options. Compare `data.build.modSha256` with `shasum -a 256 RiMCP/Assemblies/RiMCP.dll`. Check `proxyVersion` and `enabledGroups` for the intended helper and access. A proxy rebuild can retain the same version string. Record the save name, `loadId`, and tick. If tools are missing, check the client's proxy path and the mod's access settings.
3. Run the survey, tile, and building calls above. Check that the named tile matches the same explicit rectangle and that a surveyed building's ID round-trips through `get_building`. On a fully fogged tile, a matching `scope:"map"` building read should return no rows. Keep the game paused when comparing output sizes.
4. To replay the pinned legacy fixtures, load **Room planner - paused handoff** at tick **150001** and pass the path to the built stdio proxy. The scripts use `RIMCP_URL` and `RIMCP_TOKEN` from the environment.

   ```sh
   python3 -B tests/Selectors/check.py "/path/to/rimcp-proxy"
   python3 -B tests/MapLayers/check.py "/path/to/rimcp-proxy"
   ```

   Expect 23 selector replies and eight map-layer replies to match. Other saves are suitable for the smoke checks, and the pinned fixtures need the exact save and tick.
