"""Create and save one empty isolated part for the installed F05 smoke."""
import datetime
import json
import os
import uuid
import NXOpen

ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"


def main():
    run = uuid.uuid4().hex
    model = os.path.join(ROOT, "workspace", "v2", "models", "F05-mcp-fixture-" + run + ".prt")
    evidence = os.path.join(ROOT, "workspace", "v2", "evidence", "F05-mcp-fixture-" + run + ".json")
    session = NXOpen.Session.GetSession()
    assert session.Parts.Work is None, "Use an independent batch NX session"
    assert not os.path.exists(model)
    part = session.Parts.NewDisplay(model, NXOpen.Part.Units.Millimeters)
    status = part.Save(NXOpen.BasePart.SaveComponents.TrueValue, NXOpen.BasePart.CloseAfterSave.FalseValue)
    try:
        assert status.NumberUnsavedParts == 0 and status.NumberUnsavedObjects == 0
        report = {"created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "part_path": model,
                  "units": str(part.PartUnits), "feature_count": len(list(part.Features)),
                  "sketch_count": len(list(part.Sketches)), "body_count": len(list(part.Bodies)),
                  "curve_count": len(list(part.Curves)), "part_modified_after_save": part.IsModified,
                  "saved": os.path.isfile(model)}
        assert report["saved"] and not report["part_modified_after_save"]
        assert all(report[key] == 0 for key in ("feature_count", "sketch_count", "body_count", "curve_count"))
        with open(evidence, "w", encoding="utf-8") as stream:
            json.dump(report, stream, indent=2)
        print(evidence)
    finally:
        status.Dispose()


if __name__ == "__main__":
    main()
