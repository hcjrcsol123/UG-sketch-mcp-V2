"""Create a uniquely named, saved native part for the F09 installed-MCP smoke.

Run this journal in its own NX process. It creates an empty millimetre part and
does not load the MCP bridge or modify an existing Work Part. The part can then
be opened in the designated NX GUI for the installed pattern/mirror smoke.
"""
from __future__ import annotations

import datetime
import json
import os
import uuid

import NXOpen


ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"


def main() -> None:
    run_id = uuid.uuid4().hex
    model_root = os.path.join(ROOT, "workspace", "v2", "models")
    evidence_root = os.path.join(ROOT, "workspace", "v2", "evidence")
    os.makedirs(model_root, exist_ok=True)
    os.makedirs(evidence_root, exist_ok=True)
    model_path = os.path.join(model_root, "F09-mcp-fixture-" + run_id + ".prt")
    evidence_path = os.path.join(evidence_root, "F09-mcp-fixture-" + run_id + ".json")
    if os.path.exists(model_path) or os.path.exists(evidence_path):
        raise FileExistsError("unique F09 fixture path already exists")

    session = NXOpen.Session.GetSession()
    if session.Parts.Work is not None:
        raise RuntimeError("use an independent NX journal process with no Work Part")
    part = session.Parts.NewDisplay(model_path, NXOpen.Part.Units.Millimeters)
    save_status = part.Save(
        NXOpen.BasePart.SaveComponents.TrueValue,
        NXOpen.BasePart.CloseAfterSave.FalseValue,
    )
    try:
        counts = {
            "feature_count": len(list(part.Features)),
            "sketch_count": len(list(part.Sketches)),
            "body_count": len(list(part.Bodies)),
            "curve_count": len(list(part.Curves)),
        }
        evidence = {
            "created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "purpose": "empty isolated F09 installed-MCP fixture",
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
            raise AssertionError("NX did not leave the F09 fixture saved and unmodified")
        if save_status.NumberUnsavedParts != 0 or save_status.NumberUnsavedObjects != 0:
            raise AssertionError("NX reported unsaved parts or objects after fixture save")
        if any(counts[name] != 0 for name in (
            "feature_count", "sketch_count", "body_count", "curve_count",
        )):
            raise AssertionError("F09 fixture must start with no features, sketches, bodies, or curves")
        with open(evidence_path, "x", encoding="utf-8") as stream:
            json.dump(evidence, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        print(evidence_path)
    finally:
        save_status.Dispose()


if __name__ == "__main__":
    main()
