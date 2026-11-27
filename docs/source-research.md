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
