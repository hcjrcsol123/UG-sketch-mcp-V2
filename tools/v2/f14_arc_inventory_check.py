"""Offline validation of stored F14 native Arc inventories; no NX calls.

This checks the existing product run; it does not add an NX case, independently
prove Thread.GetEntities, or replace native readback and independent review.
"""
import argparse
import json
from pathlib import Path
from tools.v2.f14_hole_oracle import bore_removed_volume_mm3


def index_by_tag(rows):
    indexed = {}
    for row in rows:
        tag = row.get("tag")
        if not isinstance(tag, str) or tag in indexed:
            raise ValueError("missing or duplicate Arc tag")
        indexed[tag] = row
    return indexed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    source = json.loads(args.input.read_text(encoding="utf-8"))
    expected = {"threaded-right-blind", "threaded-right-second-same-body"}
    selected = [row for row in source["cases"] if row.get("case") in expected]
    report = {"scope": "offline Arc and volume completion of existing native run; not a replacement for case PASS or independent review",
              "input": str(args.input), "cases": [], "passed": False}
    for row in selected:
        before, after = index_by_tag(row["arc_inventory_before"]), index_by_tag(row["arc_inventory_after"])
        added = set(after) - set(before)
        owned = row["thread_feature"]["arc_entity_tags"]
        def geometry(record):
            return {key: value for key, value in record.items() if key != "id"}
        recorded_delta = row.get("new_arc_tags")
        expected_removed = bore_removed_volume_mm3(4.2, 10, 118) * (2 if row["case"] == "threaded-right-second-same-body" else 1)
        observed_volume = row["output_body"]["volume_mm3"]
        expected_volume = 28000.0 - expected_removed
        conditions = {
            "all_prior_arc_geometry_and_session_tags_preserved": all(tag in after and geometry(after[tag]) == geometry(old) for tag, old in before.items()),
            "two_unique_thread_arc_entities": len(owned) == 2 and len(set(owned)) == 2,
            "new_arcs_exactly_equal_thread_entities": added == set(owned),
            "shared_expression_evidence_present": bool(row["thread_feature"]["shared_expression_tags"]),
            "matched_timestamps": row["thread_feature"]["hole_timestamp"] == row["thread_feature"]["package_timestamp"],
            "fresh_product_verify_passed": row.get("verified") is True,
            "volume_matches_analytic_fixture": abs(observed_volume - expected_volume) <= 1e-5,
        }
        if recorded_delta is not None:
            conditions["recorded_delta_matches"] = added == set(recorded_delta)
        report["cases"].append({"case": row["case"], "source_case_status": row.get("status"), "checks": conditions,
                                "before_count": len(before), "after_count": len(after),
                                "added_tags": sorted(added), "thread_entity_tags": sorted(owned),
                                "expected_volume_mm3": expected_volume, "observed_volume_mm3": observed_volume,
                                "recorded_delta_status": "present" if recorded_delta is not None else "not reached in original run",
                                "limitations": (["Original second case failed in its Arc assertion; later UF face/area/containment and direct non-target comparisons were not executed."]
                                                if row.get("status") != "PASS_PARTIAL" else [])})
    report["passed"] = (len(selected) == 2 and {row["case"] for row in selected} == expected and
                        all(all(row["checks"].values()) for row in report["cases"]))
    if args.output.exists():
        raise FileExistsError("Refusing to overwrite prior evidence")
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("F14 Arc inventory offline check: " + ("PASS" if report["passed"] else "FAIL"))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
