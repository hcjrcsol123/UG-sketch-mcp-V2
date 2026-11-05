"""Create only a new, isolated, saved F15 millimetre acceptance fixture."""
import datetime
import json
from pathlib import Path
import uuid

import NXOpen
import NXOpen.Features

ROOT = Path(r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp")


def main():
    session = NXOpen.Session.GetSession()
    if session.Parts.Work is not None or session.IsInTaskEnvironment:
        raise RuntimeError("F15 fixture requires an empty independent session")
    run = uuid.uuid4().hex
    models, evidence = ROOT / "workspace/v2/models", ROOT / "workspace/v2/evidence"
    models.mkdir(parents=True, exist_ok=True)
    evidence.mkdir(parents=True, exist_ok=True)
    model = models / ("F15-mcp-fixture-" + run + ".prt")
    metadata = evidence / (model.stem + ".json")
    if model.exists() or metadata.exists():
        raise FileExistsError("F15 fixture paths must be new")
    part = session.Parts.NewDisplay(str(model), NXOpen.Part.Units.Millimeters)
    cases = []
    for index, (op, retain) in enumerate((op, retain) for op in ("unite", "subtract", "intersect") for retain in (False, True)):
        x = index * 40
        cases.append({"case": op + "_" + str(retain).lower(), "operation": op, "retain_tools": retain,
                      "boxes": [[x, 0, 0, 10, 10, 10], [x + 5, 0, 0, 10, 10, 10]]})
    cases.extend([
        {"case": "split", "operation": "subtract", "retain_tools": False,
         "boxes": [[240, 0, 0, 10, 10, 10], [243, 0, 0, 4, 10, 10]]},
        {"case": "multi_intersect", "operation": "intersect", "retain_tools": True,
         "boxes": [[280, 0, 0, 10, 10, 10], [280, 0, 0, 6, 10, 10], [283, 0, 0, 5, 10, 10]]},
        {"case": "disjoint_subtract", "operation": "subtract", "retain_tools": False,
         "boxes": [[320, 0, 0, 10, 10, 10], [340, 0, 0, 10, 10, 10]]},
        {"case": "disjoint_intersect", "operation": "intersect", "retain_tools": False,
         "boxes": [[360, 0, 0, 10, 10, 10], [380, 0, 0, 10, 10, 10]]},
        {"case": "sentinel", "boxes": [[420, 0, 0, 4, 4, 4]]},
    ])
    for case in cases:
        case["creation_session_body_ids"] = []
        case["bbox_mm"] = []
        for x, y, z, dx, dy, dz in case["boxes"]:
            builder = part.Features.CreateBlockFeatureBuilder(None)
            try:
                builder.SetOriginAndLengths(NXOpen.Point3d(float(x), float(y), float(z)), str(dx), str(dy), str(dz))
                builder.SetBooleanOperationAndTarget(NXOpen.Features.Feature.BooleanType.Create, None)
                bodies = builder.CommitFeature().GetBodies()
                if len(bodies) != 1 or not bodies[0].IsSolidBody:
                    raise AssertionError("fixture block must create exactly one solid")
                case["creation_session_body_ids"].append(bodies[0].JournalIdentifier)
                case["bbox_mm"].append([x, y, z, x + dx, y + dy, z + dz])
            finally:
                builder.Destroy()
    save = part.Save(NXOpen.BasePart.SaveComponents.TrueValue, NXOpen.BasePart.CloseAfterSave.FalseValue)
    try:
        count = sum(len(c["boxes"]) for c in cases)
        data = {"created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "part_path": str(model.resolve()), "units": str(part.PartUnits), "cases": cases,
                "body_count": len(list(part.Bodies)), "expected_body_count": count,
                "saved": model.is_file(), "part_modified_after_save": bool(part.IsModified),
                "save_unsaved_parts": int(save.NumberUnsavedParts), "save_unsaved_objects": int(save.NumberUnsavedObjects),
                "bridge_called": False, "mcp_called": False}
        if (not data["saved"] or data["part_modified_after_save"] or data["save_unsaved_parts"] or
                data["save_unsaved_objects"] or data["body_count"] != count):
            raise AssertionError("fixture save or body inventory failed")
        with metadata.open("x", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, ensure_ascii=False, allow_nan=False, indent=2)
            f.write("\n")
        print(str(metadata))
    finally:
        save.Dispose()


if __name__ == "__main__":
    main()
