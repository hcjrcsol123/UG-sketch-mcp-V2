"""Create and reopen the isolated F18 millimetre fixture; coordinated NX window only.

Three differently sized, separate native Create EXTRUDE solids support the
installed smoke: negative-X linear, unequal-spacing rectangle, and negative
signed circular about a non-origin Z axis. Bounding boxes in metadata identify
geometry, not durable NX IDs. Creation and reopen journal IDs are evidence for
their own sessions only; the smoke must resolve fresh IDs by live readback.
No bridge/MCP calls are made. Failures leave the part available for inspection.
"""
import datetime
import json
from pathlib import Path
import traceback
import uuid

import NXOpen
import NXOpen.Features
import NXOpen.GeometricUtilities
import NXOpen.UF

ROOT = Path(r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp")
CASES = (
    ("linear", (100, 0, 0), (4, 6, 8)),
    ("rectangular", (200, 100, 0), (5, 7, 9)),
    ("circular", (320, 0, 0), (4, 6, 10)),
)
SENTINEL = ("sentinel", (-200, -200, 0), (3, 4, 5))


def point(x, y, z):
    return NXOpen.Point3d(float(x), float(y), float(z))


def bbox(origin, dimensions):
    return list(origin) + [origin[i] + dimensions[i] for i in range(3)]


def extrude(part, origin, dimensions):
    x, y, z = origin
    dx, dy, dz = dimensions
    corners = [point(x, y, z), point(x + dx, y, z),
               point(x + dx, y + dy, z), point(x, y + dy, z)]
    curves = [part.Curves.CreateLine(a, b) for a, b in zip(corners, corners[1:] + corners[:1])]
    section = part.Sections.CreateSection(0.0095, 0.01, 0.5)
    section.SetAllowedEntityTypes(NXOpen.Section.AllowTypes.OnlyCurves)
    rule = part.ScRuleFactory.CreateRuleCurveDumb(curves)
    section.AddToSection([rule], curves[0], None, None, corners[0], NXOpen.Section.Mode.Create, False)
    builder = part.Features.CreateExtrudeBuilder(None)
    try:
        builder.Section = section
        builder.Direction = part.Directions.CreateDirection(point(0, 0, 0), NXOpen.Vector3d(0.0, 0.0, 1.0),
                                                             NXOpen.SmartObject.UpdateOption.WithinModeling)
        builder.Limits.StartExtend.Value.RightHandSide = "0"
        builder.Limits.EndExtend.Value.RightHandSide = str(dz)
        builder.BooleanOperation.Type = NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Create
        builder.BooleanOperation.SetTargetBodies([None])
        feature = builder.CommitFeature()
        bodies = feature.GetBodies()
        if feature.FeatureType != "EXTRUDE" or len(bodies) != 1 or not bodies[0].IsSolidBody:
            raise AssertionError("fixture seed must be a native EXTRUDE with exactly one solid")
        return feature, bodies[0]
    finally:
        builder.Destroy()


def block(part, origin, dimensions):
    builder = part.Features.CreateBlockFeatureBuilder(None)
    try:
        builder.SetOriginAndLengths(point(*origin), *(str(n) for n in dimensions))
        builder.SetBooleanOperationAndTarget(NXOpen.Features.Feature.BooleanType.Create, None)
        feature = builder.CommitFeature()
        bodies = feature.GetBodies()
        if len(bodies) != 1 or not bodies[0].IsSolidBody:
            raise AssertionError("sentinel must be a separate solid")
        return feature, bodies[0]
    finally:
        builder.Destroy()


def readback(part, row):
    uf = NXOpen.UF.UFSession.GetUFSession()
    matches = []
    for body in part.Bodies:
        bounds = list(uf.ModlGeneral.AskBoundingBox(body.Tag))
        if len(bounds) == 6 and all(abs(float(a) - float(b)) <= 1e-6 for a, b in zip(bounds, row["bbox_mm"])):
            matches.append(body)
    if len(matches) != 1 or not matches[0].IsSolidBody:
        raise AssertionError("fixture box must resolve uniquely after reopen: " + row["case"])
    body = matches[0]
    features = []
    for feature in part.Features.GetFeatures():
        if row["case"] != "sentinel" and feature.FeatureType != "EXTRUDE":
            continue
        bodies = feature.GetBodies()
        if bodies is not None and any(candidate.Tag == body.Tag for candidate in bodies):
            features.append(feature)
    if len(features) != 1:
        raise AssertionError("fixture feature must resolve uniquely after reopen: " + row["case"])
    return {"body_id": body.JournalIdentifier, "feature_id": features[0].JournalIdentifier,
            "feature_type": features[0].FeatureType, "bbox_mm": row["bbox_mm"]}


def main():
    session = NXOpen.Session.GetSession()
    if session.Parts.Work is not None or session.IsInTaskEnvironment or session.ActiveSketch is not None:
        raise RuntimeError("F18 fixture requires an empty independent session")
    run = uuid.uuid4().hex
    models, evidence = ROOT / "workspace/v2/models", ROOT / "workspace/v2/evidence"
    models.mkdir(parents=True, exist_ok=True)
    evidence.mkdir(parents=True, exist_ok=True)
    model = models / ("F18-mcp-fixture-" + run + ".prt")
    metadata = evidence / (model.stem + ".json")
    if model.exists():
        raise FileExistsError("fixture path must be new")
    data = {"created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "part_path": str(model.resolve()),
            "stage": "creating", "cases": [], "saved": False, "reopened": False,
            "bridge_called": False, "mcp_called": False}
    with metadata.open("x", encoding="utf-8", newline="\n") as out:
        json.dump(data, out, indent=2)

    def persist():
        with metadata.open("w", encoding="utf-8", newline="\n") as out:
            json.dump(data, out, ensure_ascii=False, allow_nan=False, indent=2)
            out.write("\n")

    try:
        part = session.Parts.NewDisplay(str(model), NXOpen.Part.Units.Millimeters)
        for name, origin, dimensions in CASES + (SENTINEL,):
            feature, body = (block(part, origin, dimensions) if name == "sentinel"
                             else extrude(part, origin, dimensions))
            data["cases"].append({"case": name, "bbox_mm": bbox(origin, dimensions),
                                  "creation_session_feature_id": feature.JournalIdentifier,
                                  "creation_session_body_id": body.JournalIdentifier,
                                  "creation_feature_type": feature.FeatureType})
            persist()
        data["units"] = str(part.PartUnits)
        data["body_count"] = len(list(part.Bodies))
        if data["body_count"] != 4:
            raise AssertionError("fixture must contain exactly four separate solids")
        save = part.Save(NXOpen.BasePart.SaveComponents.TrueValue, NXOpen.BasePart.CloseAfterSave.FalseValue)
        try:
            data.update(saved=model.is_file(), part_modified_after_save=bool(part.IsModified),
                        save_unsaved_parts=int(save.NumberUnsavedParts), save_unsaved_objects=int(save.NumberUnsavedObjects))
        finally:
            save.Dispose()
        if not data["saved"] or data["part_modified_after_save"] or data["save_unsaved_parts"] or data["save_unsaved_objects"]:
            raise AssertionError("fixture save failed")
        data["stage"] = "reopening"; persist()
        part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue, NXOpen.BasePart.CloseModified.CloseModified, None)
        part, load = session.Parts.OpenDisplay(str(model))
        load.Dispose()
        if len(list(part.Bodies)) != 4 or part.IsModified:
            raise AssertionError("reopened fixture inventory or clean state mismatch")
        for row in data["cases"]:
            row["reopen_readback"] = readback(part, row)
        data.update(reopened=True, stage="complete", reopened_modified=bool(part.IsModified))
        persist()
        print(str(metadata))
    except Exception:
        data["failure"] = traceback.format_exc()
        persist()
        raise


if __name__ == "__main__":
    main()
