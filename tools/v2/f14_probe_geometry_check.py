"""Offline arithmetic over one preserved F14 discovery report, not acceptance."""
import argparse
import json
from pathlib import Path

from tools.v2.f14_hole_oracle import (bore_removed_volume_mm3,
    counterbore_removed_volume_mm3, countersink_removed_volume_mm3)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    source = json.loads(args.input.read_text(encoding="utf-8"))
    expected = [bore_removed_volume_mm3(4, 10, 118), bore_removed_volume_mm3(4, 20),
                counterbore_removed_volume_mm3(4, 10, 8, 3, 118),
                counterbore_removed_volume_mm3(4, 20, 8, 3),
                countersink_removed_volume_mm3(4, 10, 8, 90, 118),
                countersink_removed_volume_mm3(4, 20, 8, 90),
                bore_removed_volume_mm3(4.2, 10, 118), bore_removed_volume_mm3(4.2, 10, 118)]
    result = {"scope": "offline arithmetic on native API discovery, not product acceptance",
              "input": str(args.input), "original_block_volume_mm3": 28000.0,
              "volume_abs_tolerance_mm3": 1e-5, "rows": [], "passed": False}
    for index, (row, removal) in enumerate(zip(source["cases"], expected), 1):
        observed = 28000.0 - row["volume_mm3"]
        rebuilt = row["after_reopen"]
        item = {"case": row["case"], "kind": row["kind"], "depth_mode": row["depth_mode"],
                "expected_removed_mm3": removal, "observed_removed_mm3": observed,
                "delta_mm3": observed - removal, "origin": rebuilt["origins"],
                "direction": rebuilt["directions"], "volume_passed": abs(observed - removal) <= 1e-5}
        item["position_direction_passed"] = (item["origin"] == [[17.0, 13.0, 20.0]] and
                                             item["direction"] == [[0.0, 0.0, -1.0]])
        item["mode_passed"] = rebuilt["HoleDepthLimitOption"] == ("0" if index % 2 else "3") if index <= 6 else rebuilt["HoleDepthLimitOption"] == "0"
        if index > 6:
            threads = [f for f in row["feature_inventory_after_reopen"] if f["feature_type"] == "SYMBOLIC_THREAD"]
            item["thread_passed"] = (len(threads) == 1 and threads[0]["Pitch"] == 0.8 and
                threads[0]["PitchExp_value"] == 0.8 and threads[0]["TapDrillDiameter"] == 4.2 and
                threads[0]["ThreadLength_value"] == 7.0 and threads[0]["IsInternalThread"] is True and
                threads[0]["ThreadStandard"] == "Metric Coarse" and threads[0]["ThreadSize"] == "M5 x 0.8" and
                threads[0]["RadialEngage"] == "0.75" and threads[0]["ThreadHandedness"] == str(index - 7))
        result["rows"].append(item)
    result["passed"] = (source.get("completed") is True and len(source["cases"]) == 8 and len(result["rows"]) == 8 and
        all(r["volume_passed"] and r["position_direction_passed"] and r["mode_passed"] and
            r.get("thread_passed", True) for r in result["rows"]))
    if args.output.exists():
        raise FileExistsError("Refusing to overwrite a prior report")
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print("F14 discovery arithmetic: " + ("PASS" if result["passed"] else "FAIL"))
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
