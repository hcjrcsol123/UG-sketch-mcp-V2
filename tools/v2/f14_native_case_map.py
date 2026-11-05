"""Map unique F14 native case IDs to retained runs; no NX calls or extra cases."""
import argparse
import json
from pathlib import Path


GROUPS = {
    "r04": ["simple-blind"],
    "r06": ["simple-through", "counterbore-blind", "counterbore-through",
            "countersink-blind-tilted-block", "countersink-through", "threaded-left-blind"],
    "r07": ["threaded-right-blind", "threaded-inch-positive-x-through",
            "threaded-right-second-same-body", "threaded-inch-positive-x-blind", "undo", "reject",
            "through_enclosed_cavity_rejection", "roundtrip", "tamper_existing_thread"],
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("r04", type=Path)
    parser.add_argument("r06", type=Path)
    parser.add_argument("r07", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--breakthrough", type=Path)
    args = parser.parse_args()
    paths = {"r04": args.r04, "r06": args.r06, "r07": args.r07}
    groups = dict(GROUPS)
    if args.breakthrough:
        paths["breakthrough"] = args.breakthrough
        groups["breakthrough"] = ["thin_wall_and_thread_depth_rejection"]
    rows = []
    for version, names in groups.items():
        source = json.loads(paths[version].read_text(encoding="utf-8"))
        for name in names:
            found = [row for row in source["cases"] if row.get("case") == name]
            row = found[0] if len(found) == 1 else {}
            entry = {"case": name, "source_run": version, "source_report": str(paths[version]),
                     "source_status": row.get("status"), "unique_source_match": len(found) == 1}
            if name == "thin_wall_and_thread_depth_rejection":
                observed = row.get("observations", [])
                entry["negative_controls"] = [item.get("label") for item in observed]
                entry["three_controls_verified"] = (len(observed) == 3 and all(item.get("preserved") is True for item in observed))
            rows.append(entry)
    report = {"scope": "unique case mapping across retained NX product runs; no additional NX execution",
              "sample_size": "one fixture model per source run; not a reliability sample", "cases": rows,
              "unique_case_count": len(rows), "all_selected_case_statuses_passed": all(
                  row["unique_source_match"] and row["source_status"] == "PASS_PARTIAL" and
                  row.get("three_controls_verified", True) for row in rows),
              "notes": ["Repeated cases in other runs are not added to the unique count.",
                        "A case status maps to its source report; raw historical FAILs are unchanged.",
                        "Installed MCP and offline assertions are separate evidence layers."]}
    if args.output.exists():
        raise FileExistsError("Refusing to overwrite existing evidence")
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("F14 native unique case mapping: " + ("PASS" if report["all_selected_case_statuses_passed"] else "INCOMPLETE"))
    return 0 if report["all_selected_case_statuses_passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
