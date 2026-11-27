#!/usr/bin/env python3
"""Compare the four legacy selector endpoints on the paused room-planner save."""

import argparse
import hashlib
import json
import subprocess
from pathlib import Path

FIXTURE = Path(__file__).with_name("legacy.json")
ANCHOR = {"x": 132, "z": 90}
SQUARE = {"width": 4, "height": 4}
CASES = {
    "W1A_building_page": ("list_buildings", {"limit": 4}),
    "W1A_building_filter": ("list_buildings", {
        **ANCHOR, **SQUARE, "category": "Wall", "defNames": ["Wall", "Door"],
        "excludeDefs": ["Door"], "limit": 8,
    }),
    "W1A_building_edge": ("list_buildings", {"x": 1000, "z": 90, **SQUARE}),
    "W1A_building_outside": ("list_buildings", {"x": 1001, "z": 90, **SQUARE}),
    "W1A_building_incomplete": ("list_buildings", ANCHOR),
    "W1A_building_unknown": ("list_buildings", {"defNames": ["SelectorMissingDef"]}),
    "W1A_thing_page": ("find_things", {"limit": 4}),
    "W1A_thing_nearest": ("find_things", {**ANCHOR, "limit": 4}),
    "W1A_thing_radius": ("find_things", {**ANCHOR, "radius": 75, "limit": 4}),
    "W1A_thing_filter": ("find_things", {
        "def": "Steel", "category": "ResourcesRaw", "excludeCategory": ["Foods"],
        "query": "steel", "forbidden": False, "inStorage": True, "limit": 4,
    }),
    "W1A_thing_halfpair": ("find_things", {"x": 132}),
    "W1A_thing_missingcenter": ("find_things", {"radius": 9}),
    "W1A_plant_page": ("list_plants", {"limit": 4}),
    "W1A_plant_nearest": ("list_plants", {**ANCHOR, "limit": 4}),
    "W1A_plant_singlecell": ("list_plants", {**ANCHOR, "radius": 0}),
    "W1A_plant_negative": ("list_plants", {**ANCHOR, "radius": -1}),
    "W1A_plant_filter": ("list_plants", {
        **ANCHOR, "radius": 75, "def": "Plant_Rice", "tree": False,
        "stump": False, "canYield": True, "harvestable": True, "limit": 4,
    }),
    "W1A_plant_missingcenter": ("list_plants", {"radius": 9}),
    "W1A_placement_cell": ("check_placement", {"def": "Wall", **ANCHOR}),
    "W1A_placement_rectangle": ("check_placement", {
        "def": "Door", "stuff": "WoodLog", "rotation": "South", **ANCHOR, **SQUARE,
    }),
    "W1A_placement_edge": ("check_placement", {
        "def": "Wall", "x": -1, "z": 90, "width": 20, "height": 1,
    }),
    "W1A_placement_toowide": ("check_placement", {"def": "Wall", **ANCHOR, "width": 21}),
    "W1A_placement_incomplete": ("check_placement", {"def": "Wall", "x": 132}),
}


def snapshot(process):
    """Hash the full result minus the load ID and game tick."""
    results = {}
    for request_id, (case, (tool, args)) in enumerate(CASES.items(), start=1):
        request = {"jsonrpc": "2.0", "id": request_id, "method": "tools/call",
                   "params": {"name": tool, "arguments": args}}
        process.stdin.write(json.dumps(request) + "\n")
        process.stdin.flush()
        response = json.loads(process.stdout.readline())
        result = response["result"]
        body = result["structuredContent"]
        body.pop("loadId", None)
        body.pop("tick", None)
        canonical = json.dumps({"isError": result["isError"], "body": body},
                               ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        results[case] = (hashlib.sha256(canonical.encode()).hexdigest(), body)
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("proxy", help="Path to the compiled RimMCP stdio proxy")
    parser.add_argument("--capture", action="store_true", help="Record the current replies as the baseline")
    args = parser.parse_args()
    with subprocess.Popen([args.proxy], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          text=True) as process:
        actual = snapshot(process)
        process.stdin.close()
    if args.capture:
        FIXTURE.write_text(json.dumps({key: value[0] for key, value in actual.items()}, indent=2) + "\n")
        return
    expected = json.loads(FIXTURE.read_text())
    for case, (digest, body) in actual.items():
        if digest != expected.get(case):
            raise SystemExit(f"{case} changed: {json.dumps(body, ensure_ascii=False)}")
    if expected.keys() != actual.keys():
        raise SystemExit("The fixture and case list differ")
    print(f"{len(actual)} selector replies matched")


if __name__ == "__main__":
    main()
