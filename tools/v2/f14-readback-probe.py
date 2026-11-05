"""Root-only native readback of retained F14 probe parts; no model creation/save."""
import datetime
import json
import os
import traceback
import uuid
import NXOpen
import NXOpen.Features

ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
SOURCE = "2d5d8fbf020744f4b287e88a40fb3e02"


def ref(obj):
    if obj is None:
        return None
    result = {"tag": int(obj.Tag), "runtime_type": type(obj).__name__}
    try:
        result["id"] = obj.JournalIdentifier
    except Exception:
        pass
    return result


def encode(obj):
    if obj is None or isinstance(obj, (str, int, float, bool)):
        return obj
    if isinstance(obj, (list, tuple)):
        return [encode(v) for v in obj]
    if all(hasattr(obj, key) for key in ("X", "Y", "Z")):
        return [obj.X, obj.Y, obj.Z]
    if hasattr(obj, "Tag"):
        return ref(obj)
    return str(obj)


def observe(row, key, getter):
    try:
        row[key] = encode(getter())
    except Exception:
        row[key + "_error"] = traceback.format_exc()


def expression(e):
    row = ref(e)
    for key in ("Name", "Type", "RightHandSide", "Equation", "Units"):
        observe(row, key, lambda k=key: getattr(e, k))
    if row.get("Type") == "Number":
        observe(row, "value", lambda: e.Value)
    elif row.get("Type") == "String":
        observe(row, "value", lambda: e.StringValue)
    observe(row, "owner", e.GetOwningFeature)
    observe(row, "users", e.GetUsingFeatures)
    return row


def main():
    session = NXOpen.Session.GetSession()
    if session.Parts.Work is not None:
        raise RuntimeError("Requires an independent empty NX session")
    run_id = uuid.uuid4().hex
    path = os.path.join(ROOT, "workspace/v2/evidence", "f14-readback-probe-" + run_id + ".json")
    report = {"scope": "F14 retained API parts readback only, not product acceptance",
              "run_id": run_id, "source_probe": SOURCE, "completed": False,
              "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "parts": []}

    def persist():
        with open(path, "w", encoding="utf-8") as stream:
            json.dump(report, stream, indent=2, allow_nan=False)

    observe(report, "nx_standard_directory", lambda: session.GetEnvironmentVariableValue("UGII_THREADED_HOLE_STANDARD_DIR"))
    observe(report, "nx_base_directory", lambda: session.GetEnvironmentVariableValue("UGII_BASE_DIR"))
    persist()
    for index in (0, 1, 6):
        model = os.path.join(ROOT, "workspace/v2/models", "F14-api-" + SOURCE + "-" + str(index) + ".prt")
        row = {"model": model, "source_index": index, "features": [], "completed": False}
        report["parts"].append(row)
        part = None
        builder = None
        try:
            part, status = session.Parts.OpenDisplay(model)
            status.Dispose()
            observe(row, "modified_on_open", lambda: part.IsModified)
            for feature in part.Features:
                item = ref(feature)
                row["features"].append(item)
                for key in ("FeatureType", "ContainerFeature", "IsContainedFeature", "IsInternal", "Timestamp"):
                    observe(item, key, lambda k=key: getattr(feature, k))
                for key, getter in (("parents", feature.GetParents), ("children", feature.GetChildren),
                                    ("all_children", feature.GetAllChildren), ("entities", feature.GetEntities),
                                    ("bodies", feature.GetBodies), ("expressions", feature.GetExpressions)):
                    observe(item, key, getter)
                if isinstance(feature, NXOpen.Features.HolePackage):
                    builder = part.Features.CreateHolePackageBuilder(feature)
                    observe(item, "targets", builder.BooleanOperation.GetTargetBodies)
                    observe(item, "boolean_type", lambda: builder.BooleanOperation.Type)
                    observe(item, "position_section", builder.HolePosition.GetOutputCurves)
                    observe(item, "project_vector_ref", lambda: builder.ProjectionDirection.ProjectVector)
                    observe(item, "project_vector", lambda: builder.ProjectionDirection.ProjectVector.Vector)
                    observe(item, "project_vector_origin", lambda: builder.ProjectionDirection.ProjectVector.Origin)
                    observe(item, "depth_limit", lambda: builder.HoleDepthLimitOption)
                    observe(item, "depth_option", lambda: builder.DepthOption)
                    builder.Destroy()
                    builder = None
            row["expressions"] = [expression(e) for e in part.Expressions]
            for collection in ("Points", "Directions", "Axes"):
                observe(row, collection.lower(), lambda name=collection: list(getattr(part, name)))
            observe(row, "modified_after_getters", lambda: part.IsModified)
            row["completed"] = True
        except Exception:
            row["error"] = traceback.format_exc()
        finally:
            if builder is not None:
                try:
                    builder.Destroy()
                except Exception:
                    row["builder_cleanup_error"] = traceback.format_exc()
            if part is not None:
                try:
                    part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue, NXOpen.BasePart.CloseModified.CloseModified, None)
                except Exception:
                    row["close_error"] = traceback.format_exc()
            persist()
        if row.get("builder_cleanup_error") or row.get("close_error"):
            break
    report["completed"] = len(report["parts"]) == 3 and all(
        row["completed"] and not row.get("builder_cleanup_error") and not row.get("close_error") for row in report["parts"])
    report["finished_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    persist()
    print(path)


if __name__ == "__main__":
    main()
