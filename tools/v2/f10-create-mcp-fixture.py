"""Create a saved F10 native MCP fixture in the already running NX session.

Run this file as an NX Python Journal in a designated idle NX session. It does
not start or close NX, call the MCP bridge, delete files, or save over an
existing model. It refuses an active task/Sketch or a dirty prior Work/Display
Part. The new standalone millimeter part contains one spatial source Sketch
with a Line, partial Arc, and full Circle Arc, two empty tilted target Sketches,
and solid linear/circular Edge sources. It writes a unique fixture JSON beside
the evidence folder for ``f10-mcp-smoke.py --fixture-json``.
"""
from __future__ import annotations

import datetime
import json
import math
import os
import uuid

import NXOpen
import NXOpen.UF


ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
POSITION_TOLERANCE_MM = 1.0e-6
ANGLE_TOLERANCE_RAD = 1.0e-7
TWO_PI = 2.0 * math.pi


def _utc_now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def _point(value):
    return [float(value.X), float(value.Y), float(value.Z)]


def _vector(value):
    return [float(value.X), float(value.Y), float(value.Z)]


def _add(left, right):
    return [left[i] + right[i] for i in range(3)]


def _sub(left, right):
    return [left[i] - right[i] for i in range(3)]


def _scale(value, factor):
    return [item * factor for item in value]


def _dot(left, right):
    return sum(left[i] * right[i] for i in range(3))


def _cross(left, right):
    return [left[1] * right[2] - left[2] * right[1],
            left[2] * right[0] - left[0] * right[2],
            left[0] * right[1] - left[1] * right[0]]


def _norm(value):
    return math.sqrt(_dot(value, value))


def _unit(value):
    length = _norm(value)
    if not math.isfinite(length) or length <= 0.0:
        raise ValueError("cannot normalize a zero or non-finite vector")
    return _scale(value, 1.0 / length)


def _xyz(value):
    return NXOpen.Point3d(float(value[0]), float(value[1]), float(value[2]))


def _vec(value):
    return NXOpen.Vector3d(float(value[0]), float(value[1]), float(value[2]))


def _frame(origin, normal, requested_x):
    n = _unit(normal)
    x0 = _sub(requested_x, _scale(n, _dot(requested_x, n)))
    x = _unit(x0)
    y = _unit(_cross(n, x))
    return {"origin_mm": list(origin), "normal": n, "x_axis": x, "y_axis": y}


def _frame_from_sketch(sketch):
    origin = sketch.Origin
    axes = sketch.Orientation.Element
    return {
        "origin_mm": _point(origin),
        "normal": [float(axes.Zx), float(axes.Zy), float(axes.Zz)],
        "x_axis": [float(axes.Xx), float(axes.Xy), float(axes.Xz)],
        "y_axis": [float(axes.Yx), float(axes.Yy), float(axes.Yz)],
    }


def _create_sketch(part, frame, name):
    plane = part.Planes.CreatePlane(
        _xyz(frame["origin_mm"]), _vec(frame["normal"]),
        NXOpen.SmartObject.UpdateOption.WithinModeling,
    )
    builder = part.Sketches.CreateSketchInPlaceBuilder2(NXOpen.Sketch.Null)
    try:
        builder.PlaneReference = plane
        builder.AxisReference = part.Directions.CreateDirection(
            _xyz(frame["origin_mm"]), _vec(frame["x_axis"]),
            NXOpen.SmartObject.UpdateOption.WithinModeling,
        )
        builder.SketchOrigin = part.Points.CreatePoint(_xyz(frame["origin_mm"]))
        builder.PlaneOption = NXOpen.Sketch.PlaneOption.ExistingPlane
        builder.OriginOption = NXOpen.OriginMethod.SpecifyPoint
        sketch = builder.Commit()
        if sketch is None:
            raise RuntimeError("NX did not create " + name)
        return sketch
    finally:
        builder.Destroy()


def _identifier(value, label):
    result = getattr(value, "JournalIdentifier", None)
    if not isinstance(result, str) or not result.strip():
        raise RuntimeError(label + " omitted a JournalIdentifier")
    return result


def _tag(value):
    return str(value.Tag)


def _arc_point(center, axis_x, axis_y, radius, angle):
    return [center[i] + radius * (axis_x[i] * math.cos(angle) + axis_y[i] * math.sin(angle))
            for i in range(3)]


def _curve_primitive(curve):
    if isinstance(curve, NXOpen.Line):
        return {"kind": "LINE", "start_mm": _point(curve.StartPoint),
                "end_mm": _point(curve.EndPoint)}
    if isinstance(curve, NXOpen.Arc):
        center, axis_x, axis_y = curve.GetOrientation()
        axis_x_values = _vector(axis_x)
        axis_y_values = _vector(axis_y)
        start_angle = float(curve.StartAngle)
        end_angle = float(curve.EndAngle)
        sweep = end_angle - start_angle
        radius = float(curve.Radius)
        source_length = _length(curve)
        full_circle = (abs(abs(sweep) - TWO_PI) <= ANGLE_TOLERANCE_RAD or
                       abs(source_length - TWO_PI * radius) <= POSITION_TOLERANCE_MM)
        if full_circle and abs(abs(sweep) - TWO_PI) > ANGLE_TOLERANCE_RAD:
            sweep = TWO_PI if sweep >= 0.0 else -TWO_PI
            end_angle = start_angle + sweep
        return {
            "kind": "ARC",
            "center_mm": _point(center),
            "radius_mm": radius,
            "start_mm": _arc_point(_point(center), axis_x_values, axis_y_values,
                                   radius, start_angle),
            "end_mm": _arc_point(_point(center), axis_x_values, axis_y_values,
                                 radius, end_angle),
            "axis_x": axis_x_values,
            "axis_y": axis_y_values,
            "normal": _unit(_cross(axis_x_values, axis_y_values)),
            "start_angle_rad": start_angle,
            "end_angle_rad": end_angle,
            "sweep_rad": sweep,
            "full_circle": full_circle,
        }
    raise ValueError("unsupported source Curve object type " + type(curve).__name__)


def _length(curve):
    return float(curve.GetLength())


def _source_record(source_id, source, primitive, *, edge_type=None, body=None,
                   primitive_unavailable_reason=None):
    row = {
        "source_curve_id": source_id,
        "journal_id": _identifier(source, "source object"),
        "tag": _tag(source),
        "source_type": type(source).__name__,
        "edge_type": edge_type,
        "primitive": primitive,
        "primitive_unavailable_reason": primitive_unavailable_reason,
        "length_mm": _length(source),
    }
    if body is not None:
        row["body_id"] = _identifier(body, "source Edge body")
        row["body_tag"] = _tag(body)
        row["body_signature"] = _body_signature(body)
    if primitive is not None and primitive.get("kind") == "LINE":
        row["start_mm"] = primitive["start_mm"]
        row["end_mm"] = primitive["end_mm"]
    elif primitive is not None and primitive.get("kind") == "ARC":
        row["center_mm"] = primitive["center_mm"]
        row["radius_mm"] = primitive["radius_mm"]
        row["start_mm"] = primitive["start_mm"]
        row["end_mm"] = primitive["end_mm"]
        row["axis_x"] = primitive["axis_x"]
        row["axis_y"] = primitive["axis_y"]
    return row


def _body_signature(body):
    volume = None
    bbox = None
    try:
        bbox = [float(value) for value in body.GetBoundingBox()]
    except Exception:
        bbox = None
    try:
        volume = float(body.GetVolume())
    except Exception:
        volume = None
    return {"volume_mm3": volume, "bbox_mm": bbox}


def _returned_out_value(value, label):
    if isinstance(value, (tuple, list)):
        if not value:
            raise RuntimeError(label + " returned no out value")
        return value[-1]
    if value is None:
        raise RuntimeError(label + " returned no out value")
    return value


def _all_constraint_tags(sketch):
    tags = set()
    checked = 0
    for name in dir(NXOpen.Sketch.ConstraintType):
        if name.startswith("_") or name in ("NoCon", "LastConType"):
            continue
        constraint_type = getattr(NXOpen.Sketch.ConstraintType, name)
        if callable(constraint_type):
            continue
        values = sketch.GetAllConstraintsOfType(NXOpen.Sketch.ConstraintClass.Any, constraint_type)
        checked += 1
        for constraint in values:
            tags.add(str(constraint.Tag))
    if checked == 0:
        raise RuntimeError("NX constraint type enumeration did not expose any readable types")
    return tags


def _body_tag_map(part):
    bodies = list(part.Bodies)
    return {str(body.Tag): body for body in bodies if body is not None}


def _one_new_body(part, before_body_tags, label):
    after = _body_tag_map(part)
    added = [tag for tag in after if tag not in before_body_tags]
    if len(added) != 1:
        raise RuntimeError(f"{label} must add exactly one new Body Tag; added={added!r}")
    body = after[added[0]]
    if not body.IsSolidBody:
        raise RuntimeError(label + " returned a non-solid Body")
    return body


def _create_solid_edges(part, frame, session):
    uf = NXOpen.UF.UFSession.GetUFSession()
    block_origin = [80.0, 35.0, 22.0]
    block_bodies_before = set(_body_tag_map(part))
    block_mark = session.SetUndoMark(NXOpen.Session.MarkVisibility.Invisible,
                                     "F10 fixture UF block")
    try:
        block_tag = _returned_out_value(
            uf.ModlFeatures.CreateBlock1(NXOpen.UF.Modl.FeatureSigns.NULLSIGN, block_origin,
                                         ["12", "9", "7"]),
            "UF ModlFeatures.CreateBlock1",
        )
        block_errors = int(session.UpdateManager.DoUpdate(block_mark))
    finally:
        try:
            session.DeleteUndoMark(block_mark, None)
        except Exception:
            pass
    if block_errors != 0:
        raise RuntimeError("NX update reported errors after UF block creation")
    block_body = _one_new_body(part, block_bodies_before, "UF block")
    block_edges = list(block_body.GetEdges())
    linear_edges = [edge for edge in block_edges
                    if edge.SolidEdgeType == NXOpen.Edge.EdgeType.Linear]
    if not linear_edges:
        raise RuntimeError("UF block did not provide a native Linear solid Edge")
    linear_edge = linear_edges[0]

    cylinder_origin = _add(_add(frame["origin_mm"], _scale(frame["x_axis"], 32.0)),
                           _scale(frame["y_axis"], 16.0))
    cylinder_origin = _add(cylinder_origin, _scale(frame["normal"], 9.0))
    cylinder_bodies_before = set(_body_tag_map(part))
    cylinder_mark = session.SetUndoMark(NXOpen.Session.MarkVisibility.Invisible,
                                        "F10 fixture UF cylinder")
    try:
        cylinder_tag = _returned_out_value(
            uf.ModlFeatures.CreateCyl1(NXOpen.UF.Modl.FeatureSigns.NULLSIGN,
                                       cylinder_origin, "8", "6", frame["normal"]),
            "UF ModlFeatures.CreateCyl1",
        )
        cylinder_errors = int(session.UpdateManager.DoUpdate(cylinder_mark))
    finally:
        try:
            session.DeleteUndoMark(cylinder_mark, None)
        except Exception:
            pass
    if cylinder_errors != 0:
        raise RuntimeError("NX update reported errors after UF cylinder creation")
    cylinder_body = _one_new_body(part, cylinder_bodies_before, "UF cylinder")
    cylinder_edges = list(cylinder_body.GetEdges())
    circular_edges = [edge for edge in cylinder_edges
                      if edge.SolidEdgeType == NXOpen.Edge.EdgeType.Circular]
    if not circular_edges:
        raise RuntimeError("UF tilted cylinder did not provide a native Circular solid Edge")
    circular_edge = circular_edges[0]
    if not block_body.IsSolidBody or not cylinder_body.IsSolidBody:
        raise RuntimeError("fixture source Edge bodies must be solid")
    return {
        "block_feature_tag": str(block_tag), "block_body": block_body,
        "linear_edge": linear_edge, "cylinder_feature_tag": str(cylinder_tag),
        "cylinder_body": cylinder_body, "circular_edge": circular_edge,
    }


def _linear_edge_primitive(edge):
    result = edge.GetVertices()
    if not isinstance(result, (tuple, list)) or len(result) < 2:
        raise RuntimeError("NX Edge.GetVertices returned no endpoint pair")
    first, second = result[0], result[1]
    return {"kind": "LINE", "start_mm": _point(first), "end_mm": _point(second)}


def _circular_edge_primitive(edge):
    uf = NXOpen.UF.UFSession.GetUFSession()
    evaluator = None
    try:
        evaluator = _returned_out_value(uf.Eval.Initialize2(edge.Tag), "UF Eval.Initialize2")
        data = _returned_out_value(uf.Eval.AskArc(evaluator), "UF Eval.AskArc")
        center = [float(value) for value in data.center[:3]]
        axis_x = _unit([float(value) for value in data.x_axis[:3]])
        axis_y = _unit([float(value) for value in data.y_axis[:3]])
        radius = float(data.radius)
        start_angle = float(data.limits[0])
        end_angle = float(data.limits[1])
        periodic = bool(data.is_periodic)
        if periodic:
            start_angle, end_angle = 0.0, TWO_PI
        sweep = end_angle - start_angle
        return {
            "kind": "ARC", "center_mm": center, "radius_mm": radius,
            "start_mm": _arc_point(center, axis_x, axis_y, radius, start_angle),
            "end_mm": _arc_point(center, axis_x, axis_y, radius, end_angle),
            "axis_x": axis_x, "axis_y": axis_y,
            "normal": _unit(_cross(axis_x, axis_y)),
            "start_angle_rad": start_angle, "end_angle_rad": end_angle,
            "sweep_rad": sweep,
            "full_circle": periodic or abs(abs(sweep) - TWO_PI) <= ANGLE_TOLERANCE_RAD,
        }
    finally:
        if evaluator is not None:
            try:
                uf.Eval.Free(evaluator)
            except Exception:
                pass


def _inspect_edge_primitive(edge):
    if edge.SolidEdgeType == NXOpen.Edge.EdgeType.Linear:
        edge_type = "Linear"
        try:
            return "Linear", _linear_edge_primitive(edge), None
        except Exception as error:
            return "Linear", None, "direct Linear Edge endpoints unavailable (" + type(error).__name__ + ")"
    if edge.SolidEdgeType == NXOpen.Edge.EdgeType.Circular:
        edge_type = "Circular"
        try:
            return "Circular", _circular_edge_primitive(edge), None
        except Exception as error:
            return "Circular", None, "direct Circular Edge evaluator unavailable (" + type(error).__name__ + ")"
    edge_type = str(edge.SolidEdgeType)
    return edge_type, None, "fixture selected an Edge type without an analytic Line/Arc primitive"


def main():
    run_id = uuid.uuid4().hex
    model_root = os.path.join(ROOT, "workspace", "v2", "models")
    evidence_root = os.path.join(ROOT, "workspace", "v2", "evidence")
    os.makedirs(model_root, exist_ok=True)
    os.makedirs(evidence_root, exist_ok=True)
    model_path = os.path.join(model_root, "F10-mcp-fixture-" + run_id + ".prt")
    fixture_path = os.path.join(evidence_root, "F10-mcp-fixture-" + run_id + ".json")
    if os.path.exists(model_path) or os.path.exists(fixture_path):
        raise FileExistsError("unique F10 fixture path already exists; no file will be overwritten")

    report = {
        "schema": "f10-native-mcp-fixture-v1",
        "created_utc": _utc_now(),
        "purpose": "saved independent F10 installed MCP projection fixture",
        "part_path": model_path,
        "fixture_json_path": fixture_path,
        "units": "Millimeters",
        "bridge_loaded_or_called": False,
        "nx_started_or_closed_by_journal": False,
        "existing_model_modified_by_journal": False,
        "sample_size": 1,
        "numeric_fixture_coverage": ["spatial Line", "partial Arc", "full Circle Arc",
                                      "solid linear Edge", "solid circular Edge"],
        "instrument_calibration": "Not applicable: no physical instrument is used. All geometric values are NX model values.",
    }
    save_status = None
    completed = False
    try:
        session = NXOpen.Session.GetSession()
        old_work = session.Parts.Work
        old_display = session.Parts.Display
        report["prior_work_part"] = None if old_work is None else {
            "path": old_work.FullPath, "tag": str(old_work.Tag), "is_modified": bool(old_work.IsModified),
        }
        report["prior_display_part"] = None if old_display is None else {
            "path": old_display.FullPath, "tag": str(old_display.Tag), "is_modified": bool(old_display.IsModified),
        }
        if bool(session.IsInTaskEnvironment):
            raise RuntimeError("fixture creation requires an idle NX session outside task environment")
        if session.ActiveSketch is not None:
            raise RuntimeError("fixture creation refuses an active Sketch")
        if old_work is not None and bool(old_work.IsModified):
            raise RuntimeError("fixture creation refuses a dirty prior Work Part")
        if old_display is not None and bool(old_display.IsModified):
            raise RuntimeError("fixture creation refuses a dirty prior Display Part")

        part = session.Parts.NewDisplay(model_path, NXOpen.Part.Units.Millimeters)
        if part is None or part.PartUnits != NXOpen.BasePart.Units.Millimeters:
            raise RuntimeError("NX did not create a millimeter standalone part")
        report["new_part_tag"] = str(part.Tag)

        frame = _frame([18.0, -11.0, 9.0], [1.0, 2.0, 3.0], [1.0, -1.0, 0.5])
        source_frame = dict(frame)
        source_frame["origin_mm"] = _add(frame["origin_mm"], _scale(frame["normal"], 8.0))
        true_frame = dict(frame)
        false_frame = dict(frame)
        false_frame["origin_mm"] = _add(frame["origin_mm"], _scale(frame["y_axis"], 34.0))

        source_sketch = _create_sketch(part, source_frame, "source Sketch")
        source_frame_actual = _frame_from_sketch(source_sketch)

        origin = source_frame_actual["origin_mm"]
        normal = source_frame_actual["normal"]
        x_axis = source_frame_actual["x_axis"]
        y_axis = source_frame_actual["y_axis"]
        line_start = _add(_add(origin, _scale(x_axis, 2.0)), _scale(y_axis, 3.0))
        line_end = _add(_add(origin, _scale(x_axis, 10.0)), _scale(y_axis, 6.0))
        line = part.Curves.CreateLine(_xyz(line_start), _xyz(line_end))
        arc_center = _add(_add(origin, _scale(x_axis, 5.0)), _scale(y_axis, 8.0))
        partial_arc = part.Curves.CreateArc(_xyz(arc_center), _vec(x_axis), _vec(y_axis),
                                            3.0, 0.25, 0.25 + 1.75 * math.pi)
        circle_center = _add(_add(origin, _scale(x_axis, 14.0)), _scale(y_axis, 8.0))
        full_circle = part.Curves.CreateArc(_xyz(circle_center), _vec(x_axis), _vec(y_axis),
                                            2.25, 0.0, TWO_PI)
        source_mark = session.SetUndoMark(NXOpen.Session.MarkVisibility.Invisible,
                                          "F10 MCP fixture source Sketch geometry")
        source_active = False
        try:
            source_sketch.Activate(NXOpen.Sketch.ViewReorient.FalseValue)
            source_active = True
            infer_mode = NXOpen.Sketch.InferConstraintsOption.InferNoConstraints
            source_sketch.AddGeometry(line, infer_mode)
            source_sketch.AddGeometry(partial_arc, infer_mode)
            source_sketch.AddGeometry(full_circle, infer_mode)
            source_sketch.Update()
            source_sketch.Deactivate(NXOpen.Sketch.ViewReorient.FalseValue,
                                     NXOpen.Sketch.UpdateLevel.Model)
            source_active = False
            update_errors = int(session.UpdateManager.DoUpdate(source_mark))
            if update_errors != 0:
                raise RuntimeError("NX update reported errors after adding source Sketch geometry")
        finally:
            if source_active:
                try:
                    source_sketch.Deactivate(NXOpen.Sketch.ViewReorient.FalseValue,
                                             NXOpen.Sketch.UpdateLevel.Model)
                except Exception:
                    pass
            try:
                session.DeleteUndoMark(source_mark, None)
            except Exception:
                pass

        solid_sources = _create_solid_edges(part, frame, session)
        session.UpdateManager.DoUpdate(session.NewestVisibleUndoMark)

        # Keep every associative projection source earlier in native feature history
        # than either target Sketch. In particular, the selected solid Edges must not
        # belong to Bodies created after their target Sketch features.
        true_sketch = _create_sketch(part, true_frame, "associative true target Sketch")
        false_sketch = _create_sketch(part, false_frame, "associative false target Sketch")
        true_frame_actual = _frame_from_sketch(true_sketch)
        false_frame_actual = _frame_from_sketch(false_sketch)
        if len(list(true_sketch.GetAllGeometry())) or len(list(false_sketch.GetAllGeometry())):
            raise RuntimeError("fixture target Sketches must start empty")

        source_geometry = [line, partial_arc, full_circle]
        source_records = []
        for curve in source_geometry:
            source_id = _identifier(curve, "source Sketch curve")
            primitive = _curve_primitive(curve)
            source_records.append(_source_record(source_id, curve, primitive))

        edge_sources = [
            (solid_sources["linear_edge"], solid_sources["block_body"]),
            (solid_sources["circular_edge"], solid_sources["cylinder_body"]),
        ]
        for edge, body in edge_sources:
            edge_type, primitive, limitation = _inspect_edge_primitive(edge)
            source_id = _identifier(edge, "solid source Edge")
            source_records.append(_source_record(source_id, edge, primitive,
                                                 edge_type=edge_type, body=body,
                                                 primitive_unavailable_reason=limitation))

        if len({row["source_curve_id"] for row in source_records}) != len(source_records):
            raise RuntimeError("source Curves/Edges did not receive unique JournalIdentifiers")
        if len({row["tag"] for row in source_records}) != len(source_records):
            raise RuntimeError("source Curves/Edges did not receive unique NX Tags")
        if len(list(source_sketch.GetAllGeometry())) != 3:
            raise RuntimeError("source Sketch must contain exactly the Line, partial Arc, and full Circle Arc")
        if _all_constraint_tags(source_sketch):
            raise RuntimeError("source Sketch fixture geometry must not have inferred constraints")

        all_sketches = list(part.Sketches)
        if len(all_sketches) != 3:
            raise RuntimeError("fixture must contain one source Sketch and exactly two target Sketches")
        if len(list(part.Bodies)) != 2:
            raise RuntimeError("fixture must contain the block and tilted cylinder solid bodies")
        if not source_sketch.Feature or not true_sketch.Feature or not false_sketch.Feature:
            raise RuntimeError("fixture Sketches must have native NX features")

        save_status = part.Save(NXOpen.BasePart.SaveComponents.TrueValue,
                                NXOpen.BasePart.CloseAfterSave.FalseValue)
        if save_status is None:
            raise RuntimeError("NX Save did not return a PartSaveStatus")
        save_part_count = int(save_status.NumberUnsavedParts)
        save_object_count = int(save_status.NumberUnsavedObjects)
        modified_after_save = bool(part.IsModified)
        if save_part_count != 0 or save_object_count != 0 or modified_after_save:
            raise RuntimeError("NX did not leave the new F10 fixture saved and clean")
        save_status.Dispose()
        save_status = None
        if not os.path.isfile(model_path) or os.path.getsize(model_path) <= 0:
            raise RuntimeError("NX did not create the unique saved F10 part")

        target_ids = {
            "associative_true": _identifier(true_sketch, "associative true target Sketch"),
            "associative_false": _identifier(false_sketch, "associative false target Sketch"),
        }
        native_feature_ordering = {
            "creation_sequence": [
                "source Sketch with three native curves",
                "block and tilted cylinder solid Bodies with two selected Edges",
                "empty associative-true and associative-false target Sketches",
            ],
            "all_projection_source_features_precede_target_sketches": True,
            "source_sketch_feature_id": _identifier(source_sketch.Feature, "source Sketch feature"),
            "solid_source_body_ids": [row["body_id"] for row in source_records if "body_id" in row],
            "target_sketch_feature_ids": {
                "associative_true": _identifier(true_sketch.Feature, "associative true target Sketch feature"),
                "associative_false": _identifier(false_sketch.Feature, "associative false target Sketch feature"),
            },
            "rationale": "Associative projection targets follow all source curve and Edge features in native history.",
        }
        fixture = {
            "schema": "f10-native-mcp-fixture-v1",
            "created_utc": _utc_now(),
            "part_path": model_path,
            "units": "Millimeters",
            "target_sketch_ids": target_ids,
            "target_frames": {
                target_ids["associative_true"]: true_frame_actual,
                target_ids["associative_false"]: false_frame_actual,
            },
            "source_sketch_id": _identifier(source_sketch, "source Sketch"),
            "source_sketch_frame": source_frame_actual,
            "source_curve_ids": [row["source_curve_id"] for row in source_records],
            "source_records": source_records,
            "native_feature_ordering": native_feature_ordering,
        }
        report.update({
            "fixture_json": fixture,
            "native_feature_ordering": native_feature_ordering,
            "source_part_modified_after_save": modified_after_save,
            "save_unsaved_parts": save_part_count,
            "save_unsaved_objects": save_object_count,
            "source_sketch_geometry_count": len(source_geometry),
            "target_sketch_geometry_counts": {"associative_true": 0, "associative_false": 0},
            "body_count": len(list(part.Bodies)),
            "feature_count": len(part.Features.GetFeatures()),
            "bridge_loaded": False,
            "passed": True,
        })
        completed = True
    except Exception as error:
        report["passed"] = False
        report["error_type"] = type(error).__name__
        report["error"] = str(error)
        raise
    finally:
        if save_status is not None:
            try:
                save_status.Dispose()
            except Exception:
                pass
        report["completed_utc"] = _utc_now()
        report["completed"] = completed
        mode = "x" if not os.path.exists(fixture_path) else None
        if mode is not None:
            with open(fixture_path, mode, encoding="utf-8") as stream:
                json.dump(report.get("fixture_json", report), stream, ensure_ascii=False,
                          sort_keys=True, indent=2, allow_nan=False)
                stream.write("\n")
        print(fixture_path)


if __name__ == "__main__":
    main()
