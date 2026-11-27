#!/usr/bin/env python3
"""Compare legacy map grids on the paused room-planner save."""

import importlib.util
from pathlib import Path

HERE = Path(__file__).parent
BASELINE = importlib.util.spec_from_file_location("rimcp_live_baseline", HERE.parent / "Selectors" / "check.py")
assert BASELINE is not None and BASELINE.loader is not None
runner = importlib.util.module_from_spec(BASELINE)
BASELINE.loader.exec_module(runner)
runner.__doc__ = __doc__
runner.FIXTURE = HERE / "legacy.json"

HALL = {"x": 116, "z": 76, "width": 40, "height": 40, "maxBytes": 32768}
CASES = {
    "W1C_default_grid": {},
    "W1C_combined_layers": {**HALL, "layers": ["roof", "designations"]},
    "W1C_repeated_layers": {**HALL, "layers": ["DESIGNATIONS", "roof", "designations"]},
    "W1C_fog_edge": {"x": 0, "z": 0, "width": 4, "height": 3, "layers": ["roof", "designations"]},
    "W1C_clipped_origin": {"x": 1000, "z": 1000, "width": 4, "height": 3, "layers": ["roof"]},
    "W1C_single_cell": {"x": 132, "z": 90, "width": 1, "height": 1, "layers": ["designations"]},
    "W1C_width_limit": {"width": 41},
    "W1C_unknown_layer": {"layers": ["SelectorMissingLayer"]},
}
runner.CASES = {name: ("get_map_view", args) for name, args in CASES.items()}

if __name__ == "__main__":
    runner.main()
