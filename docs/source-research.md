# Local game and mod source cache

Use the cache when a live read or command needs the exact game rule behind an overlay, dialog, or order. Read `get_game_context`, `list_mods`, and the test fixture's game version first. Use the corresponding installed DLL and data definitions.

The cache script uses an installed `ilspycmd`, available from the .NET tool ecosystem. Set `--ilspycmd` if it is outside PATH. Match the runtime to that tool release, or set `DOTNET_ROLL_FORWARD=Major` locally when relying on a newer installed runtime. The script expects Python 3.11 or higher.

```sh
python3 scripts/cache-game-source.py \
  --game-build '1.6.4871 rev597' \
  --package ludeon.rimworld \
  --assembly '/path/to/RimWorld/Data/Managed/Assembly-CSharp.dll'
```

For a mod, pass its package ID and its actual assembly for the loaded game version. Use `--reference-dir` for a directory of matching dependency assemblies. The printed path contains the source and a manifest with the build, package ID, assembly SHA-256, and decompiler version. It defaults to `~/.cache/rimcp/source`, with `--cache` available for other machines or tests. An exact rerun reuses the entry.

Search the cache with `rg` for the def, method or UI action. Follow the call into validation and observe the same path on a disposable save. Keep decompiled output in the local cache. Record the relevant source symbol and live outcome in the repo's handoff.

## Use RimSage with RimMCP

[RimSage](https://github.com/realloon/RimSage) gives agents indexed C# symbol and shipped Def XML reads over MCP. During the campaign work, source inspection established that `Plant.HarvestableNow` and `Plant.CanYieldNow()` answer different questions, and identified the game's per-action caravan arrival checks. Those distinctions informed RimMCP's plant reads and use of native route validation. For current status, use RimMCP against the loaded game. Its live definition reads and exports reflect the active modlist. [Wave 4 campaign verification](wave-4-campaign.md)

Use a RimSage checkout with the local stdio and `get_source_provenance` support described below. It needs Bun, ripgrep, installed game data, and C# decompiled from the corresponding game assembly. The following example uses sibling `rimcp` and `RimSage` checkouts and starts in the `rimcp` repo. Set the installation paths to the game you intend to investigate. On macOS, the importer root is the directory with both `Version.txt` and `Data/*/Defs`. The managed assembly can be under `Contents/Resources/Data/Managed` in the same app bundle.

```sh
rimworld_install='/path/to/RimWorldMac.app'
rimworld_assembly="$rimworld_install/Contents/Resources/Data/Managed/Assembly-CSharp.dll"
rimworld_build=$(tr -d '\r\n' < "$rimworld_install/Version.txt")
rimworld_source_cache=$(python3 scripts/cache-game-source.py \
  --game-build "$rimworld_build" \
  --package ludeon.rimworld \
  --assembly "$rimworld_assembly")

cd ../RimSage
bun install
bun run import:defs "$rimworld_install"
bun run import:csharp "$rimworld_source_cache/source" "$rimworld_assembly" "$rimworld_install/Version.txt"
bun run build
```

The importers replace the checkout's local snapshots. Rebuild after importing and allow the running stdio server to restart before checking the new index. Configure a separate stdio server named `rimsage` with command `bun` and arguments `["run", "/absolute/path/to/RimSage/src/stdio.ts"]`. RimMCP keeps its own proxy configuration and game token.

1. Call RimSage's `get_source_provenance`. Compare the Def version and C# version with the target installation's `Version.txt`, and the C# SHA-256 with the cache manifest. For a disposable clone, use that clone's files. Compare the game assembly hash here with the original game DLL, separate from the RimMCP mod hash.
2. Call `read_csharp_symbol` with `{"typeName":"Plant","memberName":"CanYieldNow"}`, then `get_def_details` with `{"defName":"Plant_Rice","defType":"ThingDef","inheritance":"raw"}`. For arrival validation, read `CaravanArrivalAction_Enter.CanEnter`. Confirm the tool returns the implementation or XML.
3. Pair the source result with a live read or disposable test before recording a gameplay finding. Record the source version, symbol, and observed outcome. The shipped XML index represents installed definitions before runtime mod patches.

On November 27, the local RimSage stdio server returned the two C# methods and `Plant_Rice` XML. Both index versions were **1.6.4871 rev595**, and the C# hash matched the rev595 Steam installation. The disposable Wave 3 and 4 tests used **rev597**. To research that build, refresh the index from the rev597 installation and repeat the checks.
