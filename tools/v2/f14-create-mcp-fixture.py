"""Root-operated NX journal: create an isolated saved F14 MCP fixture.

The journal runner snapshots this file, so ROOT is deliberately explicit.
Requires an empty, idle NX session; never starts or closes NX or loads MCP.
"""
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
        raise RuntimeError("F14 fixture requires an empty NX session outside a task environment")
    run_id = uuid.uuid4().hex
    models = ROOT / "workspace" / "v2" / "models"
    evidence = ROOT / "workspace" / "v2" / "evidence"
    models.mkdir(parents=True, exist_ok=True)
    evidence.mkdir(parents=True, exist_ok=True)
    model = models / ("F14-mcp-fixture-" + run_id + ".prt")
    metadata = evidence / (model.stem + ".json")
    if model.exists() or metadata.exists():
        raise FileExistsError("F14 fixture path already exists")
    part = session.Parts.NewDisplay(str(model), NXOpen.Part.Units.Millimeters)
    bodies = []
    for origin, lengths in (((0, 0, 0), (80, 40, 20)), ((100, 0, 0), (10, 10, 10))):
        builder = part.Features.CreateBlockFeatureBuilder(None)
        try:
            builder.SetOriginAndLengths(NXOpen.Point3d(*map(float, origin)), *map(str, lengths))
            builder.SetBooleanOperationAndTarget(NXOpen.Features.Feature.BooleanType.Create, None)
            feature = builder.CommitFeature()
            created = feature.GetBodies()
            if len(created) != 1 or not created[0].IsSolidBody:
                raise AssertionError("each fixture block must create exactly one solid")
            bodies.append(created[0].JournalIdentifier)
        finally:
            builder.Destroy()
    save = part.Save(NXOpen.BasePart.SaveComponents.TrueValue, NXOpen.BasePart.CloseAfterSave.FalseValue)
    try:
        data = {"created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "part_path": str(model.resolve()), "units": str(part.PartUnits),
                "target_body_id": bodies[0], "sentinel_body_id": bodies[1],
                "target_bbox_mm": [0, 0, 0, 80, 40, 20],
                "sentinel_bbox_mm": [100, 0, 0, 110, 10, 10],
                "body_count": len(list(part.Bodies)), "saved": model.is_file(),
                "part_modified_after_save": bool(part.IsModified),
                "save_unsaved_parts": int(save.NumberUnsavedParts),
                "save_unsaved_objects": int(save.NumberUnsavedObjects),
                "bridge_loaded": None, "bridge_loaded_note": "not inspected by this journal",
                "bridge_called": False, "mcp_called": False,
                "nx_started_by_fixture": False, "nx_closed_by_fixture": False}
        if (not data["saved"] or data["part_modified_after_save"] or
                data["save_unsaved_parts"] or data["save_unsaved_objects"] or
                data["body_count"] != 2 or bodies[0] == bodies[1]):
            raise AssertionError("F14 fixture save or solid inventory is invalid")
        with metadata.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(data, stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")
        print(str(metadata))
    finally:
        save.Dispose()


if __name__ == "__main__":
    main()
