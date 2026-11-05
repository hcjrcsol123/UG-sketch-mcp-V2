"""Create a fresh, isolated millimetre F11 sketch fixture in an existing NX session.

Run this file as an NX Python Journal in an idle NX session. It never starts or
closes NX, calls the MCP bridge, deletes a file, or saves over an existing
model. It refuses a dirty prior Work/Display Part or an active Sketch/task.
The generated part is a new standalone model, and its matching fixture JSON is
written under workspace/v2/evidence for ``f11-mcp-smoke.py``.
"""
from __future__ import annotations

import datetime
import json
import math
import os
from pathlib import Path
import uuid

import NXOpen


ROOT = Path(r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp")
TOLERANCE_MM = 1.0e-6
TWO_PI = 2.0 * math.pi
EXPRESSION_FLAGS = (
    "IsMeasurementExpression", "IsGeometricExpression", "IsNoEdit", "IsNoUpdate",
    "IsRightHandSideLockedFromEdit", "IsUserLocked", "IsUserSpecified",
)


def _utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def _xyz(values):
    return NXOpen.Point3d(float(values[0]), float(values[1]), float(values[2]))


def _point(value):
    return [float(value.X), float(value.Y), float(value.Z)]


def _arc_point(center, axis_x, axis_y, radius, angle):
    return [
        float(center.X) + float(radius) * (float(axis_x.X) * math.cos(angle) + float(axis_y.X) * math.sin(angle)),
        float(center.Y) + float(radius) * (float(axis_x.Y) * math.cos(angle) + float(axis_y.Y) * math.sin(angle)),
        float(center.Z) + float(radius) * (float(axis_x.Z) * math.cos(angle) + float(axis_y.Z) * math.sin(angle)),
    ]


def _identifier(value, label):
    result = getattr(value, "JournalIdentifier", None)
    if not isinstance(result, str) or not result.strip():
        raise RuntimeError(label + " omitted a JournalIdentifier")
    return result


def _dimension_state_name(dimension):
    state = dimension.DimensionState
    if state == NXOpen.SketchDimensionalConstraint.DimensionStateType.Driving:
        return "Driving"
    if state == NXOpen.SketchDimensionalConstraint.DimensionStateType.Reference:
        return "Reference"
    return "UNSUPPORTED_" + str(state)


def _constraint_type_name(dimension):
    value = dimension.ConstraintType
    for name in dir(NXOpen.Sketch.ConstraintType):
        if name.startswith("_") or name in ("NoCon", "LastConType"):
            continue
        candidate = getattr(NXOpen.Sketch.ConstraintType, name)
        if not callable(candidate) and value == candidate:
            return name
    return "UNMAPPED_" + str(value)


def _sketch_frame(sketch):
    axes = sketch.Orientation.Element
    return {
        "origin_mm": _point(sketch.Origin),
        "normal": [float(axes.Zx), float(axes.Zy), float(axes.Zz)],
        "x_axis": [float(axes.Xx), float(axes.Xy), float(axes.Xz)],
        "y_axis": [float(axes.Yx), float(axes.Yy), float(axes.Yz)],
    }


def _create_sketch(part):
    origin = NXOpen.Point3d(0.0, 0.0, 0.0)
    normal = NXOpen.Vector3d(0.0, 0.0, 1.0)
    x_axis = NXOpen.Vector3d(1.0, 0.0, 0.0)
    plane = part.Planes.CreatePlane(origin, normal, NXOpen.SmartObject.UpdateOption.WithinModeling)
    builder = part.Sketches.CreateSketchInPlaceBuilder2(NXOpen.Sketch.Null)
    try:
        builder.PlaneReference = plane
        builder.AxisReference = part.Directions.CreateDirection(
            origin, x_axis, NXOpen.SmartObject.UpdateOption.WithinModeling,
        )
        builder.SketchOrigin = part.Points.CreatePoint(origin)
        builder.PlaneOption = NXOpen.Sketch.PlaneOption.ExistingPlane
        builder.OriginOption = NXOpen.OriginMethod.SpecifyPoint
        sketch = builder.Commit()
        if sketch is None:
            raise RuntimeError("NX did not create the F11 reference-conversion Sketch")
        return sketch
    finally:
        builder.Destroy()


def _add_geometry(session, sketch, curves):
    sketch.Activate(NXOpen.Sketch.ViewReorient.FalseValue)
    try:
        infer_mode = NXOpen.Sketch.InferConstraintsOption.InferNoConstraints
        for curve in curves:
            sketch.AddGeometry(curve, infer_mode)
        sketch.Update()
    finally:
        if session.ActiveSketch is not None:
            sketch.Deactivate(NXOpen.Sketch.ViewReorient.FalseValue, NXOpen.Sketch.UpdateLevel.Model)


def _create_linear_dimension(session, sketch, line, constraint_type, placement, name):
    sketch.Activate(NXOpen.Sketch.ViewReorient.FalseValue)
    try:
        first = NXOpen.Sketch.DimensionGeometry()
        first.Geometry = line
        first.AssocType = NXOpen.Sketch.AssocType.StartPoint
        second = NXOpen.Sketch.DimensionGeometry()
        second.Geometry = line
        second.AssocType = NXOpen.Sketch.AssocType.EndPoint
        dimension = sketch.CreateDimension(
            constraint_type, first, second, _xyz(placement), NXOpen.Expression.Null,
            NXOpen.Sketch.DimensionOption.CreateAsDriving,
        )
        if dimension is None or dimension.AssociatedExpression is None:
            raise RuntimeError("NX did not create a dimensional constraint for " + name)
        dimension.SetName(name)
        sketch.Update()
        sketch.CalculateStatus()
        return dimension
    finally:
        if session.ActiveSketch is not None:
            sketch.Deactivate(NXOpen.Sketch.ViewReorient.FalseValue, NXOpen.Sketch.UpdateLevel.Model)


def _create_radial_reference(session, sketch, arc):
    sketch.Activate(NXOpen.Sketch.ViewReorient.FalseValue)
    try:
        geometry = NXOpen.Sketch.DimensionGeometry()
        geometry.Geometry = arc
        # Keep DimensionGeometry's native default association for a radial
        # dimension; this Python wrapper exposes no None/NoneValue enum member.
        geometry.AssocValue = 0
        dimension = sketch.CreateRadialDimension(
            geometry, NXOpen.Point3d(25.0, 8.0, 0.0), NXOpen.Expression.Null,
            NXOpen.Sketch.DimensionOption.CreateAsReference,
        )
        if dimension is None:
            raise RuntimeError("NX did not create the unsupported radial Reference dimension")
        dimension.SetName("F11_UNSUPPORTED_RADIAL_DIMENSION")
        sketch.Update()
        return dimension
    finally:
        if session.ActiveSketch is not None:
            sketch.Deactivate(NXOpen.Sketch.ViewReorient.FalseValue, NXOpen.Sketch.UpdateLevel.Model)


def _convert_dimension_to_reference(session, part, sketch, dimension):
    sketch.Activate(NXOpen.Sketch.ViewReorient.FalseValue)
    builder = part.Sketches.CreateConvertToFromReferenceBuilder()
    try:
        builder.SelectAllProjectFeatureCurves = False
        builder.OutputState = NXOpen.ConvertToFromReferenceBuilder.OutputType.Reference
        added = builder.InputObjects.Add([dimension.AssociatedDimension])
        if added is False:
            raise RuntimeError("NX rejected the selected VerticalDim annotation during conversion")
        if not builder.Validate():
            raise RuntimeError("NX rejected the fixture VerticalDim reference conversion")
        builder.Commit()
    finally:
        builder.Destroy()
        if session.ActiveSketch is not None:
            sketch.Deactivate(NXOpen.Sketch.ViewReorient.FalseValue, NXOpen.Sketch.UpdateLevel.Model)


def _curve_record(role, curve, kind):
    record = {
        "role": role,
        "journal_id": _identifier(curve, role + " curve"),
        "tag": str(curve.Tag),
        "object_type": type(curve).__name__,
        "kind": kind,
        "reference_status": "Active",
    }
    if isinstance(curve, NXOpen.Line):
        record.update(start_mm=_point(curve.StartPoint), end_mm=_point(curve.EndPoint))
    elif isinstance(curve, NXOpen.Arc):
        center, axis_x, axis_y = curve.GetOrientation()
        start_angle, end_angle = float(curve.StartAngle), float(curve.EndAngle)
        record.update(
            center_mm=_point(center), radius_mm=float(curve.Radius),
            start_mm=_arc_point(center, axis_x, axis_y, curve.Radius, start_angle),
            end_mm=_arc_point(center, axis_x, axis_y, curve.Radius, end_angle),
            normal=[float(axis_x.Y * axis_y.Z - axis_x.Z * axis_y.Y),
                    float(axis_x.Z * axis_y.X - axis_x.X * axis_y.Z),
                    float(axis_x.X * axis_y.Y - axis_x.Y * axis_y.X)],
            start_angle_rad=start_angle, end_angle_rad=end_angle,
            full_circle=abs(abs(end_angle - start_angle) - TWO_PI) <= 1.0e-7,
        )
    else:
        raise ValueError("fixture supports only native Line and Arc geometry")
    return record


def _dimension_record(role, dimension):
    expression = dimension.AssociatedExpression
    associated = dimension.AssociatedDimension
    operands = []
    for item in dimension.GetDimensionGeometry():
        operands.append({
            "geometry_tag": None if item.Geometry is None else str(item.Geometry.Tag),
            "geometry_journal_id": None if item.Geometry is None else _identifier(item.Geometry, role + " operand"),
            "association": str(item.AssocType),
            "association_value": int(item.AssocValue),
        })
    units = None
    try:
        units = expression.Units.Symbol if expression.Units is not None else None
    except Exception:
        units = None
    return {
        "role": role,
        "journal_id": _identifier(dimension, role + " dimension constraint"),
        "tag": str(dimension.Tag),
        "constraint_name": str(dimension.Name),
        "constraint_type": _constraint_type_name(dimension),
        "constraint_type_raw": str(dimension.ConstraintType),
        "dimension_state": _dimension_state_name(dimension),
        "dimension_state_raw": str(dimension.DimensionState),
        "expression_tag": None if expression is None else str(expression.Tag),
        "expression_name": None if expression is None else str(expression.Name),
        "expression_rhs": None if expression is None else str(expression.RightHandSide),
        "expression_value": None if expression is None else float(expression.Value),
        "expression_unit": units,
        "expression_flags": None if expression is None else {
            name: bool(getattr(expression, name)) for name in EXPRESSION_FLAGS
        },
        "computed_size_mm": None if associated is None else float(associated.ComputedSize),
        "geometry_refs": operands,
    }


def _save_new_part(part, path):
    status = part.Save(NXOpen.BasePart.SaveComponents.TrueValue,
                       NXOpen.BasePart.CloseAfterSave.FalseValue)
    if status is None:
        raise RuntimeError("NX Save did not return a PartSaveStatus")
    try:
        if int(status.NumberUnsavedParts) != 0 or int(status.NumberUnsavedObjects) != 0 or bool(part.IsModified):
            raise RuntimeError("new F11 fixture did not save cleanly")
    finally:
        status.Dispose()
    if not os.path.isfile(path) or os.path.getsize(path) <= 0:
        raise RuntimeError("NX did not create the new F11 fixture part")


def main():
    run_id = uuid.uuid4().hex
    model_root = ROOT / "workspace" / "v2" / "models"
    evidence_root = ROOT / "workspace" / "v2" / "evidence"
    model_root.mkdir(parents=True, exist_ok=True)
    evidence_root.mkdir(parents=True, exist_ok=True)
    part_path = model_root / ("F11-mcp-fixture-" + run_id + ".prt")
    fixture_path = evidence_root / ("F11-mcp-fixture-" + run_id + ".json")
    if part_path.exists() or fixture_path.exists():
        raise FileExistsError("unique F11 fixture path already exists; no file will be overwritten")

    session = NXOpen.Session.GetSession()
    old_work, old_display = session.Parts.Work, session.Parts.Display
    if bool(session.IsInTaskEnvironment):
        raise RuntimeError("fixture creation requires an idle NX session outside task environment")
    if session.ActiveSketch is not None:
        raise RuntimeError("fixture creation refuses an active Sketch")
    if old_work is not None and bool(old_work.IsModified):
        raise RuntimeError("fixture creation refuses a dirty prior Work Part")
    if old_display is not None and bool(old_display.IsModified):
        raise RuntimeError("fixture creation refuses a dirty prior Display Part")

    part = session.Parts.NewDisplay(str(part_path), NXOpen.Part.Units.Millimeters)
    if part is None or part.PartUnits != NXOpen.BasePart.Units.Millimeters:
        raise RuntimeError("NX did not create a standalone millimetre part")
    sketch = _create_sketch(part)
    sketch.Feature.SetName("F11_MCP_REFERENCE_SKETCH")
    frame = _sketch_frame(sketch)

    horizontal = part.Curves.CreateLine(NXOpen.Point3d(0.0, 0.0, 0.0), NXOpen.Point3d(10.0, 0.0, 0.0))
    vertical = part.Curves.CreateLine(NXOpen.Point3d(15.0, 20.0, 0.0), NXOpen.Point3d(15.0, 28.0, 0.0))
    witness = part.Curves.CreateLine(NXOpen.Point3d(40.0, 0.0, 0.0), NXOpen.Point3d(44.0, 0.0, 0.0))
    partial_arc = part.Curves.CreateArc(NXOpen.Point3d(25.0, 8.0, 0.0),
                                        NXOpen.Vector3d(1.0, 0.0, 0.0),
                                        NXOpen.Vector3d(0.0, 1.0, 0.0), 3.0, 0.25, 0.25 + 1.5 * math.pi)
    full_circle = part.Curves.CreateArc(NXOpen.Point3d(34.0, 8.0, 0.0),
                                        NXOpen.Vector3d(1.0, 0.0, 0.0),
                                        NXOpen.Vector3d(0.0, 1.0, 0.0), 2.0, 0.0, TWO_PI)
    _add_geometry(session, sketch, [horizontal, vertical, witness, partial_arc, full_circle])
    horizontal.SetName("F11_HORIZONTAL_ACTIVE_LINE")
    vertical.SetName("F11_VERTICAL_REFERENCE_LINE")
    witness.SetName("F11_WITNESS_ACTIVE_LINE")
    partial_arc.SetName("F11_PARTIAL_ARC")
    full_circle.SetName("F11_FULL_CIRCLE")

    horizontal_dimension = _create_linear_dimension(
        session, sketch, horizontal, NXOpen.Sketch.ConstraintType.HorizontalDim,
        [5.0, -2.0, 0.0], "F11_HORIZONTAL_DIMENSION",
    )
    vertical_dimension = _create_linear_dimension(
        session, sketch, vertical, NXOpen.Sketch.ConstraintType.VerticalDim,
        [18.0, 24.0, 0.0], "F11_VERTICAL_DIMENSION_WITH_STALE_RHS",
    )
    radial_dimension = _create_radial_reference(session, sketch, partial_arc)

    _convert_dimension_to_reference(session, part, sketch, vertical_dimension)
    sketch.Activate(NXOpen.Sketch.ViewReorient.FalseValue)
    try:
        vertical.SetEndpoints(NXOpen.Point3d(15.0, 20.0, 0.0), NXOpen.Point3d(15.0, 31.0, 0.0))
        sketch.Update()
        sketch.CalculateStatus()
    finally:
        if session.ActiveSketch is not None:
            sketch.Deactivate(NXOpen.Sketch.ViewReorient.FalseValue, NXOpen.Sketch.UpdateLevel.Model)
    if vertical_dimension.DimensionState != NXOpen.SketchDimensionalConstraint.DimensionStateType.Reference:
        raise RuntimeError("fixture VerticalDim did not become Reference")
    if abs(float(vertical_dimension.AssociatedDimension.ComputedSize) - 11.0) > TOLERANCE_MM:
        raise RuntimeError("fixture vertical Reference dimension must measure 11 mm")
    if abs(float(vertical_dimension.AssociatedExpression.Value) - 8.0) > TOLERANCE_MM:
        raise RuntimeError("fixture vertical Reference expression RHS must remain 8 mm")

    curves = [
        _curve_record("horizontal", horizontal, "LINE"),
        _curve_record("vertical", vertical, "LINE"),
        _curve_record("witness", witness, "LINE"),
        _curve_record("partial_arc", partial_arc, "ARC"),
        _curve_record("full_circle", full_circle, "ARC"),
    ]
    dimensions = [
        _dimension_record("horizontal", horizontal_dimension),
        _dimension_record("vertical", vertical_dimension),
        _dimension_record("unsupported_radial", radial_dimension),
    ]
    _save_new_part(part, str(part_path))

    fixture = {
        "schema": "f11-native-mcp-fixture-v1",
        "created_utc": _utc_now(),
        "part_path": str(part_path),
        "units": "Millimeters",
        "sample_size": 1,
        "sketch_id": _identifier(sketch, "F11 fixture Sketch"),
        "sketch_tag": str(sketch.Tag),
        "sketch_feature_id": _identifier(sketch.Feature, "F11 fixture Sketch feature"),
        "sketch_frame": frame,
        "curves": curves,
        "dimensions": dimensions,
        "object_ids": [row["journal_id"] for row in curves + dimensions],
        "coverage": ["active Line", "reference Linear HorizontalDim", "reference Linear VerticalDim",
                     "unsupported radial dimension", "partial Arc", "full-circle Arc",
                     "selected Reference dimension ComputedSize/RHS mismatch"],
        "fixture_conditions": {
            "horizontal_dimension_state": _dimension_state_name(horizontal_dimension),
            "horizontal_computed_size_mm": float(horizontal_dimension.AssociatedDimension.ComputedSize),
            "vertical_dimension_state": _dimension_state_name(vertical_dimension),
            "vertical_expression_value_mm": float(vertical_dimension.AssociatedExpression.Value),
            "vertical_computed_size_mm": float(vertical_dimension.AssociatedDimension.ComputedSize),
            "vertical_reference_is_non_driving": True,
            "radial_dimension_type": _constraint_type_name(radial_dimension),
            "radial_dimension_type_raw": str(radial_dimension.ConstraintType),
        },
        "existing_model_modified_by_journal": False,
        "bridge_loaded_or_called": False,
        "nx_started_or_closed_by_journal": False,
        "instrument_calibration": "Not applicable; no physical instrument is used. Values are native NX model readbacks.",
    }
    with fixture_path.open("x", encoding="utf-8") as output:
        json.dump(fixture, output, ensure_ascii=False, allow_nan=False, indent=2)
        output.write("\n")
    print(json.dumps({"passed": True, "part_path": str(part_path), "fixture_json": str(fixture_path)},
                     ensure_ascii=False))


if __name__ == "__main__":
    main()
