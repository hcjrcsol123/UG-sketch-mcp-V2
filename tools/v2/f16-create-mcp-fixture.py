"""Create an isolated F16 fixture; only run during the coordinated NX window."""
import datetime
import json
from pathlib import Path
import uuid

import NXOpen
import NXOpen.Features

ROOT = Path(r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp")


def main():
    session = NXOpen.Session.GetSession()
    if session.Parts.Work is not None or session.IsInTaskEnvironment or session.ActiveSketch is not None:
        raise RuntimeError("F16 fixture requires an empty independent session")
    run = uuid.uuid4().hex
    models, evidence = ROOT / "workspace/v2/models", ROOT / "workspace/v2/evidence"
    models.mkdir(parents=True, exist_ok=True)
    evidence.mkdir(parents=True, exist_ok=True)
    model = models / ("F16-mcp-fixture-" + run + ".prt")
    metadata = evidence / (model.stem + ".json")
    if model.exists() or metadata.exists():
        raise FileExistsError("fixture and evidence paths must be new")
    part = session.Parts.NewDisplay(str(model), NXOpen.Part.Units.Millimeters)
    cases = []
    for name, x, dx, dy, dz in (("single", 0, 20, 12, 10), ("double", 40, 20, 12, 10),
                                 ("reject_undo", 80, 20, 12, 10), ("sentinel", 120, 4, 4, 4)):
        builder = part.Features.CreateBlockFeatureBuilder(None)
        try:
            builder.SetOriginAndLengths(NXOpen.Point3d(float(x), 0.0, 0.0), str(dx), str(dy), str(dz))
            builder.SetBooleanOperationAndTarget(NXOpen.Features.Feature.BooleanType.Create, None)
            bodies = builder.CommitFeature().GetBodies()
            if len(bodies) != 1 or not bodies[0].IsSolidBody:
                raise AssertionError("each fixture block must create exactly one solid")
            cases.append({"case": name, "bbox_mm": [x, 0, 0, x + dx, dy, dz],
                          "creation_session_body_id": bodies[0].JournalIdentifier})
        finally:
            builder.Destroy()
    save = part.Save(NXOpen.BasePart.SaveComponents.TrueValue, NXOpen.BasePart.CloseAfterSave.FalseValue)
    try:
        data = {"created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "part_path": str(model.resolve()), "units": str(part.PartUnits), "cases": cases,
                "body_count": len(list(part.Bodies)), "saved": model.is_file(),
                "part_modified_after_save": bool(part.IsModified),
                "save_unsaved_parts": int(save.NumberUnsavedParts), "save_unsaved_objects": int(save.NumberUnsavedObjects),
                "bridge_called": False, "mcp_called": False}
        if (not data["saved"] or data["part_modified_after_save"] or data["save_unsaved_parts"] or
                data["save_unsaved_objects"] or data["body_count"] != 4):
            raise AssertionError("fixture save or body inventory failed")
        with metadata.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(data, stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")
        print(str(metadata))
    finally:
        save.Dispose()


if __name__ == "__main__":
    main()
