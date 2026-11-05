"""NX run_journal entry: execute a frozen Python journal and observe a bounded native inventory.

This file is run by NX's Python, not imported into the MCP host. Observation is best
-effort and never substitutes for the script's own result or persistence.
"""
import json
import os
import runpy
import sys
import traceback

import NXOpen


def _xyz(value):
    return [float(value.X), float(value.Y), float(value.Z)]


def _item(obj):
    row = {"type": type(obj).__name__, "tag": str(obj.Tag),
           "journal_id": str(obj.JournalIdentifier), "name": str(obj.Name)}
    if isinstance(obj, NXOpen.Line):
        row["start"] = _xyz(obj.StartPoint)
        row["end"] = _xyz(obj.EndPoint)
    elif isinstance(obj, NXOpen.Arc):
        row["center"] = _xyz(obj.CenterPoint)
        row["radius"] = float(obj.Radius)
        row["start_angle"] = float(obj.StartAngle)
        row["end_angle"] = float(obj.EndAngle)
    return row


def _snapshot():
    result = {"parts": [], "errors": [], "complete": True}
    try:
        session = NXOpen.Session.GetSession()
        parts = list(session.Parts)
    except Exception as exc:
        result.update(complete=False, errors=["parts: " + repr(exc)])
        return result
    for part in parts:
        try:
            units = part.PartUnits
            unit_label = "mm" if units == NXOpen.BasePart.Units.Millimeters else (
                "inch" if units == NXOpen.BasePart.Units.Inches else "unknown")
            row = {"tag": str(part.Tag), "path": str(part.FullPath),
                   "dirty": bool(part.IsModified), "part_units": unit_label,
                   "part_units_native": str(units), "collections": {}}
            result["parts"].append(row)
        except Exception as exc:
            result["complete"] = False
            result["errors"].append("part metadata: " + repr(exc))
            continue
        for name in ("Curves", "Bodies", "Features"):
            records = []
            row["collections"][name] = records
            try:
                for obj in getattr(part, name):
                    try:
                        records.append(_item(obj))
                    except Exception as exc:
                        result["complete"] = False
                        result["errors"].append(name + " item: " + repr(exc))
            except Exception as exc:
                result["complete"] = False
                result["errors"].append(name + ": " + repr(exc))
    return result


def _diff(before, after):
    def flatten(snapshot):
        out = {}
        for part in snapshot["parts"]:
            for collection, records in part["collections"].items():
                for record in records:
                    # Tags are meaningful only within this single NX process.
                    out[(part["tag"], collection, record["tag"])] = record
        return out
    a, b = flatten(before), flatten(after)
    return {"added": [{"part_tag": key[0], "collection": key[1], "object": b[key]}
                      for key in b.keys() - a.keys()],
            "removed": [{"part_tag": key[0], "collection": key[1], "object": a[key]}
                        for key in a.keys() - b.keys()],
            "modified": [{"part_tag": key[0], "collection": key[1], "before": a[key], "after": b[key]}
                         for key in a.keys() & b.keys() if a[key] != b[key]]}


def _write(path, data):
    with open(path, "w", encoding="utf-8") as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)


def main():
    if len(sys.argv) < 3:
        raise ValueError("wrapper requires frozen journal and observation path")
    source, observation = sys.argv[1:3]
    args = sys.argv[3:]
    before = _snapshot()
    report = {"coverage": "Open parts and accessible Curves, Bodies and Features at two instants; only listed fields are compared. Newly opened parts contribute added observations, not proof of creation. Closed or transient parts, other collections, and unobserved geometry are not covered. Line coordinates and Arc radius use each part units; angles are NX native radians. Tags are session-scoped.",
              "before": before, "after": None, "changes": None,
              "script_outcome": "RUNNING"}
    _write(observation, report)
    sys.argv = [source] + args
    # Match ordinary script execution from the frozen script's directory.
    sys.path.insert(0, os.path.dirname(source))
    try:
        runpy.run_path(source, run_name="__main__")
        report["script_outcome"] = "RETURNED"
    except BaseException:
        report["script_outcome"] = "RAISED"
        raise
    finally:
        report["after"] = _snapshot()
        report["changes"] = _diff(before, report["after"])
        _write(observation, report)


if __name__ == "__main__":
    main()
