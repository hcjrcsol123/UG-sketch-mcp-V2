"""Create a unique saved empty millimeter part for the F13 installed MCP run.

Run this script as a journal in an independent NX session with no Work Part.
It creates only a new part under workspace/v2/models, writes fixture metadata
under workspace/v2/evidence, and does not load or call the MCP bridge.
"""
from __future__ import annotations

import datetime
import json
from pathlib import Path
import uuid

import NXOpen


ROOT = Path(__file__).resolve().parents[2]


def main() -> None:
    run_id = uuid.uuid4().hex
    model_directory = ROOT / "workspace" / "v2" / "models"
    evidence_directory = ROOT / "workspace" / "v2" / "evidence"
    model_directory.mkdir(parents=True, exist_ok=True)
    evidence_directory.mkdir(parents=True, exist_ok=True)
    model_path = model_directory / f"F13-mcp-fixture-{run_id}.prt"
    evidence_path = evidence_directory / f"{model_path.stem}.json"
    if model_path.exists() or evidence_path.exists():
        raise FileExistsError("unique F13 fixture output path already exists")

    session = NXOpen.Session.GetSession()
    if session.Parts.Work is not None:
        raise RuntimeError("fixture creation requires an independent NX session with no Work Part")
    if session.IsInTaskEnvironment:
        raise RuntimeError("fixture creation requires NX outside a task environment")

    part = session.Parts.NewDisplay(str(model_path), NXOpen.Part.Units.Millimeters)
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
            "purpose": "empty isolated F13 installed-MCP fixture; tilted Sketches and revolve features are created through MCP",
            "part_path": str(model_path.resolve()),
            "units": str(part.PartUnits),
            **counts,
            "save_unsaved_parts": int(save_status.NumberUnsavedParts),
            "save_unsaved_objects": int(save_status.NumberUnsavedObjects),
            "part_modified_after_save": bool(part.IsModified),
            "saved": model_path.is_file(),
            "bridge_loaded": False,
            "mcp_called": False,
            "nx_started_by_fixture": False,
            "nx_closed_by_fixture": False,
        }
        if not evidence["saved"] or evidence["part_modified_after_save"]:
            raise AssertionError("NX did not leave the F13 fixture saved and unmodified")
        if save_status.NumberUnsavedParts != 0 or save_status.NumberUnsavedObjects != 0:
            raise AssertionError("NX reported unsaved parts or objects after the F13 fixture save")
        if any(counts[name] != 0 for name in ("feature_count", "sketch_count", "body_count", "curve_count")):
            raise AssertionError("F13 fixture must start without features, sketches, bodies, or curves")
        with evidence_path.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(evidence, stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")
        print(str(evidence_path))
    finally:
        save_status.Dispose()


if __name__ == "__main__":
    main()
