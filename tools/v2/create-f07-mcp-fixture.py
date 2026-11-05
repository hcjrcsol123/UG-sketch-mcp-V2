"""Create a uniquely named, saved, empty native part for the F07 MCP smoke.

Run this journal in its own NX process. It does not load the MCP bridge or
modify an existing Work Part; the emitted part can then be opened in the
dedicated NX GUI for the installed smoke test.
"""
import datetime
import json
import os
import uuid

import NXOpen


ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"


def main():
    run_id = uuid.uuid4().hex
    model_path = os.path.join(ROOT, "workspace", "v2", "models", "F07-mcp-fixture-" + run_id + ".prt")
    evidence_path = os.path.join(ROOT, "workspace", "v2", "evidence", "F07-mcp-fixture-" + run_id + ".json")
    if os.path.exists(model_path) or os.path.exists(evidence_path):
        raise FileExistsError("unique F07 fixture path already exists")

    session = NXOpen.Session.GetSession()
    if session.Parts.Work is not None:
        raise RuntimeError("use an independent NX journal process with no Work Part")
    part = session.Parts.NewDisplay(model_path, NXOpen.Part.Units.Millimeters)
    save_status = part.Save(NXOpen.BasePart.SaveComponents.TrueValue,
                            NXOpen.BasePart.CloseAfterSave.FalseValue)
    try:
        counts = {
            "feature_count": len(list(part.Features)),
            "sketch_count": len(list(part.Sketches)),
            "body_count": len(list(part.Bodies)),
            "curve_count": len(list(part.Curves)),
        }
        evidence = {
            "created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "purpose": "empty isolated saved F07 installed-MCP fixture",
            "part_path": model_path,
            "units": str(part.PartUnits),
            **counts,
            "save_unsaved_parts": save_status.NumberUnsavedParts,
            "save_unsaved_objects": save_status.NumberUnsavedObjects,
            "part_modified_after_save": part.IsModified,
            "saved": os.path.isfile(model_path),
            "bridge_loaded": False,
        }
        if not evidence["saved"] or evidence["part_modified_after_save"]:
            raise AssertionError("NX did not leave the F07 fixture saved and unmodified")
        if save_status.NumberUnsavedParts != 0 or save_status.NumberUnsavedObjects != 0:
            raise AssertionError("NX reported unsaved parts or objects after fixture save")
        if any(counts[name] != 0 for name in ("feature_count", "sketch_count", "body_count", "curve_count")):
            raise AssertionError("F07 fixture must start with no features, sketches, bodies, or curves")
        with open(evidence_path, "x", encoding="utf-8") as stream:
            json.dump(evidence, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        print(evidence_path)
    finally:
        save_status.Dispose()


if __name__ == "__main__":
    main()
