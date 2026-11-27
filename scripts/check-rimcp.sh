#!/usr/bin/env bash
set -euo pipefail

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
default_managed="$HOME/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Contents/Resources/Data/Managed"
managed=${1:-${RimWorldManagedPath:-$default_managed}}
if [[ ! -f "$managed/Assembly-CSharp.dll" ]]; then
    printf 'Usage: %s /path/to/RimWorld/Data/Managed\n' "$0" >&2
    exit 1
fi
managed=$(cd -- "$managed" && pwd)
cd -- "$repo"

dotnet build RiMCP/Source/RiMCP.csproj -c Release "-p:RimWorldManagedPath=$managed"
cargo test --manifest-path McpProxy/Cargo.toml
dotnet build tests/CompatibilityRegression/CompatibilityRegression.csproj -c Release "-p:RimWorldManagedPath=$managed"

harness_log=$(mktemp)
trap 'rm -f -- "$harness_log"' EXIT
harness_failed=1
expected_unity_failures=3
status=0
mono tests/CompatibilityRegression/bin/Release/net472/CompatibilityRegression.exe \
    "$managed" RiMCP/Assemblies/RiMCP.dll >"$harness_log" 2>&1 || status=$?
cat -- "$harness_log"
if [[ "$status" == 0 ]]; then
    exit 0
fi

failures=$(rg -c '^FAIL ' "$harness_log" || true)
unity_failures=$(rg -c "^FAIL (options and buttons are the game's only when their code is|the audit tables name launching|game buttons and options are refused by the code they compose).*TypeInitializationException: The type initializer for 'Verse.Command' threw an exception[.]$" "$harness_log" || true)
if [[ "$status" == "$harness_failed" && "$failures" == "$expected_unity_failures" && "$unity_failures" == "$expected_unity_failures" ]]; then
    printf 'Compatibility checks accepted with the three expected Unity-native failures under Mono.\n'
    exit 0
fi
exit "$status"
