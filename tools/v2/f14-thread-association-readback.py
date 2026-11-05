"""Root-only read of additional native Thread association fields on retained parts."""
import importlib.util
import json
import os
import uuid
import NXOpen
import NXOpen.Features
import NXOpen.UF

ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
spec = importlib.util.spec_from_file_location("f14_readback_helpers", os.path.join(ROOT, "tools/v2/f14-readback-probe.py"))
helpers = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helpers)


def main():
    session = NXOpen.Session.GetSession()
    if session.Parts.Work is not None:
        raise RuntimeError("Requires empty session")
    path = os.path.join(ROOT, "workspace/v2/evidence", "f14-thread-association-readback-" + uuid.uuid4().hex + ".json")
    report = {"scope": "read-only native Thread additional associations; no creation/save", "parts": []}
    uf = NXOpen.UF.UFSession.GetUFSession()
    for index in (6, 7):
        model = os.path.join(ROOT, "workspace/v2/models", "F14-api-2d5d8fbf020744f4b287e88a40fb3e02-" + str(index) + ".prt")
        row = {"model": model, "completed": False}
        report["parts"].append(row)
        part = None
        builder = None
        try:
            part, status = session.Parts.OpenDisplay(model)
            status.Dispose()
            feature = next(f for f in part.Features if isinstance(f, NXOpen.Features.Thread))
            builder = part.Features.CreateThreadBuilder(feature)
            row["thread"] = helpers.ref(feature)
            for name in ("ThreadLimit", "ReverseThreadDirection", "ThreadHandedness", "ThreadType", "IsInternalThread"):
                helpers.observe(row, name, lambda key=name: getattr(builder, key))
            for name in ("CylindricalFace", "StartObject"):
                data = {}
                row[name] = data
                selection = getattr(builder, name)
                data["selector_type"] = type(selection).__name__
                helpers.observe(data, "selected", lambda: selection.Value)
                value = selection.Value
                if value is not None:
                    if isinstance(value, NXOpen.Face):
                        helpers.observe(data, "body", value.GetBody)
                        helpers.observe(data, "face_data", lambda: uf.Modl.AskFaceData(value.Tag))
                    if isinstance(value, NXOpen.Edge):
                        helpers.observe(data, "vertices", value.GetVertices)
                        helpers.observe(data, "edge_type", lambda: value.SolidEdgeType)
                    if isinstance(value, NXOpen.Point):
                        helpers.observe(data, "coordinates", lambda: value.Coordinates)
            row["completed"] = True
        except Exception:
            import traceback
            row["error"] = traceback.format_exc()
        finally:
            if builder is not None:
                helpers.observe(row, "builder_destroyed", builder.Destroy)
            if part is not None:
                helpers.observe(row, "closed", lambda: part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue, NXOpen.BasePart.CloseModified.CloseModified, None))
            with open(path, "w", encoding="utf-8") as stream:
                json.dump(report, stream, indent=2, allow_nan=False)
        if "builder_destroyed_error" in row or "closed_error" in row:
            break
    print(path)


if __name__ == "__main__":
    main()
