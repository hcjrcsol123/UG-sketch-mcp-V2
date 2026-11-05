"""Installed MCP smoke for F06 sketch geometry and profile readback.

Use ``--ready --output <new.json>`` for an installed-host diagnostic. For model
testing, create a fresh fixture with ``tools/v2/create-f06-mcp-fixture.py``,
open that saved part in the dedicated NX GUI, then pass its path with
``--part-path``. The model mode edits only a saved independent copy and records
every MCP response. This is a digital NX model-value check, not a physical
measurement or reliability claim.
"""
from __future__ import annotations

import argparse
import datetime
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools" / "v2"))
from f01_f02_mcp_client import McpStdioClient, _wait_operation

PROTOCOL_VERSION = "2025-11-25"
EXPECTED_SERVER_VERSION = "0.6.0"
EXPECTED_TOOL_COUNT = 21
POSITION_TOLERANCE_MM = 1e-5
LENGTH_TOLERANCE_MM = 1e-6
INTERSECTION_ENDPOINT_TOLERANCE_MM = 1e-6
ANGLE_TOLERANCE_RAD = 1e-6
AXIS_DOT_TOLERANCE = 1e-6
PARAMETER_TOLERANCE = 1e-9
PROFILE_ANALYSIS_STATUSES = {
    "EMPTY", "OPEN_GEOMETRY", "VALID_CLOSED_PROFILES", "INVALID_SELF_INTERSECTION",
    "INVALID_DEGENERATE", "ANALYSIS_UNAVAILABLE",
}
PROFILE_ANALYSIS_COUNT_FIELDS = (
    "curve_count", "open_chain_count", "closed_loop_count", "outer_loop_count",
    "inner_loop_count", "section_loop_count",
)
PRIVATE_KEYS = {"ownership_id", "new_ownership_id", "token", "authorization"}


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--ready", action="store_true", help="diagnose installed MCP readiness without taking ownership")
    mode.add_argument("--part-path", type=Path, help="isolated saved .prt under workspace/v2/models")
    parser.add_argument("--output", required=True, type=Path, help="new JSON evidence path")
    return parser


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = _build_parser()
    args = parser.parse_args(argv)
    if args.output.exists():
        parser.error("evidence output must be a new path")
    return args


def _redact(value: object) -> object:
    if isinstance(value, dict):
        return {key: ("[REDACTED]" if key.lower() in PRIVATE_KEYS else _redact(child))
                for key, child in value.items()}
    if isinstance(value, list):
        return [_redact(child) for child in value]
    return value


def _as_vector(value: object, label: str) -> list[float]:
    if not isinstance(value, (list, tuple)) or len(value) not in (2, 3):
        raise AssertionError(f"{label} must be a finite 2D or 3D vector")
    result = [float(component) for component in value]
    if not all(math.isfinite(component) for component in result):
        raise AssertionError(f"{label} contains a non-finite component")
    return result


def _distance(left: list[float], right: list[float]) -> float:
    if len(left) != len(right):
        raise AssertionError("vector dimensions differ")
    return math.sqrt(sum((left[i] - right[i]) ** 2 for i in range(len(left))))


def _dot(left: list[float], right: list[float]) -> float:
    if len(left) != len(right):
        raise AssertionError("vector dimensions differ")
    return sum(left[i] * right[i] for i in range(len(left)))


def _unit(vector: list[float], label: str) -> list[float]:
    magnitude = math.sqrt(_dot(vector, vector))
    if magnitude <= 0 or not math.isfinite(magnitude):
        raise AssertionError(f"{label} has zero or invalid length")
    return [component / magnitude for component in vector]


def _world_point(local: list[float], origin: list[float]) -> list[float]:
    if len(local) != 2:
        raise AssertionError("expected local 2D point")
    return [origin[0] + local[0], origin[1] + local[1], origin[2]]


def _close(actual: float, expected: float, tolerance: float, label: str) -> None:
    if not math.isfinite(actual) or abs(actual - expected) > tolerance:
        raise AssertionError(f"{label}: expected {expected!r} ± {tolerance}, got {actual!r}")


def _field(record: dict, names: tuple[str, ...], label: str) -> object:
    for name in names:
        if name in record:
            return record[name]
    raise AssertionError(f"{label} omitted numeric field; expected one of {names}")


def _kind(record: dict) -> str:
    native_kind = record.get("kind")
    return native_kind.upper() if isinstance(native_kind, str) else ""


def _is_raw_profile_analysis(value: object) -> bool:
    if not isinstance(value, dict) or value.get("status") not in PROFILE_ANALYSIS_STATUSES:
        return False
    if any(isinstance(value.get(key), bool) or not isinstance(value.get(key), int)
           or value[key] < 0 for key in PROFILE_ANALYSIS_COUNT_FIELDS):
        return False
    return isinstance(value.get("intersection_pairs"), list)


def _profile_analysis(value: object) -> dict | None:
    if isinstance(value, dict):
        if _is_raw_profile_analysis(value):
            return value
        candidate = value.get("profile_analysis")
        if _is_raw_profile_analysis(candidate):
            return candidate
        for child in value.values():
            found = _profile_analysis(child)
            if found is not None:
                return found
    elif isinstance(value, list):
        for child in value:
            found = _profile_analysis(child)
            if found is not None:
                return found
    return None


def _validated_intersection_pairs(analysis: dict, label: str) -> list[dict]:
    pairs = analysis.get("intersection_pairs")
    if not isinstance(pairs, list):
        raise AssertionError(f"{label} profile_analysis omitted intersection_pairs")
    validated: list[dict] = []
    for index, pair in enumerate(pairs):
        if not isinstance(pair, dict):
            raise AssertionError(f"{label} intersection_pairs[{index}] is not an object")
        geometry1_id = pair.get("geometry1_id")
        geometry2_id = pair.get("geometry2_id")
        count = pair.get("count")
        points = pair.get("points")
        if not isinstance(geometry1_id, str) or not geometry1_id or not isinstance(geometry2_id, str) or not geometry2_id:
            raise AssertionError(f"{label} intersection_pairs[{index}] omitted native geometry journal IDs")
        if isinstance(count, bool) or not isinstance(count, int) or count < 0:
            raise AssertionError(f"{label} intersection_pairs[{index}].count is not a nonnegative integer")
        if not isinstance(points, list) or len(points) != count:
            raise AssertionError(f"{label} intersection_pairs[{index}] count does not match its point list")
        numeric_points: list[list[float]] = []
        for point_index, point in enumerate(points):
            if not isinstance(point, (list, tuple)) or len(point) < 3:
                raise AssertionError(f"{label} intersection point {index}:{point_index} omitted XYZ coordinates")
            if any(isinstance(component, bool) or not isinstance(component, (int, float)) for component in point):
                raise AssertionError(f"{label} intersection point {index}:{point_index} contains nonnumeric data")
            try:
                numeric = [float(component) for component in point]
            except (TypeError, ValueError) as exc:
                raise AssertionError(f"{label} intersection point {index}:{point_index} contains nonnumeric data") from exc
            if not all(math.isfinite(component) for component in numeric):
                raise AssertionError(f"{label} intersection point {index}:{point_index} contains non-finite data")
            numeric_points.append(numeric)
        validated.append({"geometry1_id": geometry1_id, "geometry2_id": geometry2_id,
                          "count": count, "points": numeric_points})
    return validated


def _intersection_pair_map(analysis: dict, label: str) -> dict[tuple[str, str], dict]:
    result: dict[tuple[str, str], dict] = {}
    for pair in _validated_intersection_pairs(analysis, label):
        geometry1_id = pair["geometry1_id"]
        geometry2_id = pair["geometry2_id"]
        key = tuple(sorted((geometry1_id, geometry2_id)))
        if key in result:
            raise AssertionError(f"{label} contains duplicate intersection pair for {key}")
        reverse_parameters = geometry1_id != key[0]
        normalized_points = []
        for point in pair["points"]:
            xyz = point[:3]
            parameters = point[3:]
            if reverse_parameters and len(parameters) == 2:
                parameters = list(reversed(parameters))
            normalized_points.append(xyz + parameters)
        result[key] = {"count": pair["count"], "points": normalized_points}
    return result


def _assert_intersection_pair_data_matches(left: dict, right: dict, label: str) -> None:
    left_pairs = _intersection_pair_map(left, label + " execute")
    right_pairs = _intersection_pair_map(right, label + " verify")
    if set(left_pairs) != set(right_pairs):
        raise AssertionError(f"{label} execute/verify intersection geometry pairs differ")
    for key, left_pair in left_pairs.items():
        right_pair = right_pairs[key]
        if left_pair["count"] != right_pair["count"]:
            raise AssertionError(f"{label} execute/verify intersection count differs for {key}")
        unmatched = list(right_pair["points"])
        for left_point in left_pair["points"]:
            matched_index = None
            for index, right_point in enumerate(unmatched):
                if len(left_point) != len(right_point):
                    continue
                if _distance(left_point[:3], right_point[:3]) > INTERSECTION_ENDPOINT_TOLERANCE_MM:
                    continue
                if any(abs(left_point[i] - right_point[i]) > PARAMETER_TOLERANCE
                       for i in range(3, len(left_point))):
                    continue
                matched_index = index
                break
            if matched_index is None:
                raise AssertionError(f"{label} execute/verify intersection point data differs for {key}")
            unmatched.pop(matched_index)
        if unmatched:
            raise AssertionError(f"{label} verify returned unmatched intersection point data for {key}")


def _native_curve_is_closed(record: dict) -> bool:
    kind = _kind(record)
    if kind in {"CIRCLE", "ELLIPSE"}:
        return True
    if kind == "ARC" and "start_angle_rad" in record and "end_angle_rad" in record:
        start_angle = float(record["start_angle_rad"])
        end_angle = float(record["end_angle_rad"])
        if math.isfinite(start_angle) and math.isfinite(end_angle) and abs(abs(end_angle - start_angle) - 2.0 * math.pi) <= ANGLE_TOLERANCE_RAD:
            return True
    if "start" in record and "end" in record:
        start = _as_vector(record["start"], "native curve start")
        end = _as_vector(record["end"], "native curve end")
        return _distance(start, end) <= INTERSECTION_ENDPOINT_TOLERANCE_MM
    return False


def _assert_intersection_points_are_shared_open_endpoints(analysis: dict, sketch: dict, label: str) -> list[dict]:
    geometries = sketch.get("geometry")
    if not isinstance(geometries, list):
        raise AssertionError(f"{label} nx_inspect_sketch omitted its native geometry list")
    by_journal_id: dict[str, dict] = {}
    for geometry in geometries:
        if not isinstance(geometry, dict) or not isinstance(geometry.get("journal_id"), str):
            raise AssertionError(f"{label} native geometry omitted journal_id")
        journal_id = geometry["journal_id"]
        if journal_id in by_journal_id:
            raise AssertionError(f"{label} native geometry journal_id is duplicated: {journal_id}")
        by_journal_id[journal_id] = geometry

    evidence: list[dict] = []
    for pair in _validated_intersection_pairs(analysis, label):
        geometry1_id = pair["geometry1_id"]
        geometry2_id = pair["geometry2_id"]
        geometry1 = by_journal_id.get(geometry1_id)
        geometry2 = by_journal_id.get(geometry2_id)
        if geometry1 is None or geometry2 is None:
            raise AssertionError(f"{label} intersection pair does not map to inspected native curves: {geometry1_id}, {geometry2_id}")
        if pair["count"] == 0:
            evidence.append({"geometry1_id": geometry1_id, "geometry2_id": geometry2_id,
                             "count": 0, "shared_open_endpoints_verified": True})
            continue
        if _native_curve_is_closed(geometry1) or _native_curve_is_closed(geometry2):
            raise AssertionError(f"{label} reports an intersection involving a closed circle/ellipse, which has no open endpoint")
        endpoints1 = [
            _as_vector(_field(geometry1, ("start",), label + " first curve start"), label + " first curve start"),
            _as_vector(_field(geometry1, ("end",), label + " first curve end"), label + " first curve end"),
        ]
        endpoints2 = [
            _as_vector(_field(geometry2, ("start",), label + " second curve start"), label + " second curve start"),
            _as_vector(_field(geometry2, ("end",), label + " second curve end"), label + " second curve end"),
        ]
        for point_index, point in enumerate(pair["points"]):
            xyz = point[:3]
            distance1 = min(_distance(xyz, endpoint) for endpoint in endpoints1)
            distance2 = min(_distance(xyz, endpoint) for endpoint in endpoints2)
            if distance1 > INTERSECTION_ENDPOINT_TOLERANCE_MM or distance2 > INTERSECTION_ENDPOINT_TOLERANCE_MM:
                raise AssertionError(
                    f"{label} native intersection {geometry1_id}/{geometry2_id} point {point_index} "
                    f"is not a shared open endpoint (distances {distance1} mm, {distance2} mm)"
                )
        evidence.append({"geometry1_id": geometry1_id, "geometry2_id": geometry2_id,
                         "count": pair["count"], "shared_open_endpoints_verified": True})
    return evidence


def _assert_profile_intersections_against_inspection(executed: dict, verified: dict,
                                                      sketch: dict, label: str) -> dict:
    execute_analysis = _profile_analysis(executed)
    verify_analysis = _profile_analysis(verified)
    if not isinstance(execute_analysis, dict) or not isinstance(verify_analysis, dict):
        raise AssertionError(f"{label} execute/verify omitted profile_analysis for endpoint validation")
    return {
        "execute": _assert_intersection_points_are_shared_open_endpoints(execute_analysis, sketch, label + " execute"),
        "verify": _assert_intersection_points_are_shared_open_endpoints(verify_analysis, sketch, label + " verify"),
    }


def _assert_profile_analysis(value: object, expected: dict, label: str) -> dict:
    analysis = _profile_analysis(value)
    if not isinstance(analysis, dict):
        raise AssertionError(f"{label} omitted profile_analysis")
    for key in PROFILE_ANALYSIS_COUNT_FIELDS:
        count = analysis.get(key)
        if isinstance(count, bool) or not isinstance(count, int) or count < 0:
            raise AssertionError(f"{label} profile_analysis.{key} is not a nonnegative integer")
    for key, wanted in expected.items():
        actual = analysis.get(key)
        if actual != wanted:
            raise AssertionError(f"{label} profile_analysis.{key}: expected {wanted!r}, got {actual!r}")
    if analysis.get("native_intersection_check") not in {"VERIFIED", "NOT_REQUIRED"}:
        raise AssertionError(f"{label} native intersection check was not conclusive and clear")
    if analysis.get("spline_self_intersection_check") not in {"VERIFIED", "NOT_APPLICABLE"}:
        raise AssertionError(f"{label} spline self-intersection check was not conclusive and clear")
    _validated_intersection_pairs(analysis, label)
    if "notes" not in analysis:
        raise AssertionError(f"{label} profile_analysis omitted notes")
    return analysis


def _verify_native_record(record: dict, requested: dict, origin: list[float], label: str) -> dict:
    kind = _kind(record)
    geometry_type = requested["type"]
    if geometry_type == "line":
        if kind != "LINE":
            raise AssertionError(f"{label} expected native LINE, got {kind}")
        actual_start = _as_vector(record.get("start"), label + " start")
        actual_end = _as_vector(record.get("end"), label + " end")
        expected_start = _world_point(requested["start_mm"], origin)
        expected_end = _world_point(requested["end_mm"], origin)
        forward = max(_distance(actual_start, expected_start), _distance(actual_end, expected_end))
        reverse = max(_distance(actual_start, expected_end), _distance(actual_end, expected_start))
        if min(forward, reverse) > POSITION_TOLERANCE_MM:
            raise AssertionError(f"{label} endpoint readback differs from requested points by {min(forward, reverse)} mm")
        return {"kind": kind, "start": actual_start, "end": actual_end}

    if geometry_type == "rectangle":
        if kind != "LINE":
            raise AssertionError(f"{label} rectangle edge expected native LINE, got {kind}")
        return {"kind": kind}

    if geometry_type == "circle":
        expected_center = _world_point(requested["center_mm"], origin)
        if kind != "ARC":
            raise AssertionError(f"{label} expected native full-circle ARC, got {kind}")
        center = _as_vector(_field(record, ("center",), label + " center"), label + " center")
        radius = float(_field(record, ("radius_mm",), label + " radius"))
        if _distance(center, expected_center) > POSITION_TOLERANCE_MM:
            raise AssertionError(f"{label} center readback differs from requested center")
        _close(radius, float(requested["diameter_mm"]) / 2.0, LENGTH_TOLERANCE_MM, label + " radius_mm")
        start = float(_field(record, ("start_angle_rad",), label + " start angle"))
        end = float(_field(record, ("end_angle_rad",), label + " end angle"))
        _close(abs(end - start), 2.0 * math.pi, ANGLE_TOLERANCE_RAD, label + " full-circle span")
        return {"kind": kind, "center": center, "radius_mm": radius}

    if geometry_type == "arc":
        if kind != "ARC":
            raise AssertionError(f"{label} expected native ARC, got {kind}")
        center = _as_vector(_field(record, ("center",), label + " center"), label + " center")
        radius = float(_field(record, ("radius_mm",), label + " radius"))
        if _distance(center, _world_point(requested["center_mm"], origin)) > POSITION_TOLERANCE_MM:
            raise AssertionError(f"{label} center readback differs from requested center")
        _close(radius, float(requested["radius_mm"]), LENGTH_TOLERANCE_MM, label + " radius_mm")
        start = float(_field(record, ("start_angle_rad",), label + " start angle"))
        end = float(_field(record, ("end_angle_rad",), label + " end angle"))
        requested_sweep = math.radians(float(requested["sweep_deg"]))
        normal = _unit(_as_vector(_field(record, ("normal",), label + " normal"), label + " normal"), label + " normal")
        if len(normal) != 3 or abs(abs(normal[2]) - 1.0) > AXIS_DOT_TOLERANCE:
            raise AssertionError(f"{label} normal is not aligned with the sketch normal")
        expected_normal_z = 1.0 if requested_sweep > 0 else -1.0
        _close(normal[2], expected_normal_z, AXIS_DOT_TOLERANCE, label + " signed-sweep normal")
        oriented_sweep = (end - start) * normal[2]
        _close(oriented_sweep, requested_sweep, ANGLE_TOLERANCE_RAD, label + " signed CCW sweep")
        actual_start = _as_vector(_field(record, ("start",), label + " start point"), label + " start point")
        actual_end = _as_vector(_field(record, ("end",), label + " end point"), label + " end point")
        expected_center = _world_point(requested["center_mm"], origin)
        start_angle = math.radians(float(requested["start_angle_deg"]))
        end_angle = start_angle + requested_sweep
        expected_start = [expected_center[0] + radius * math.cos(start_angle),
                          expected_center[1] + radius * math.sin(start_angle), expected_center[2]]
        expected_end = [expected_center[0] + radius * math.cos(end_angle),
                        expected_center[1] + radius * math.sin(end_angle), expected_center[2]]
        if _distance(actual_start, expected_start) > POSITION_TOLERANCE_MM or _distance(actual_end, expected_end) > POSITION_TOLERANCE_MM:
            raise AssertionError(f"{label} native arc endpoints differ from requested angles")
        actual_x = [1.0, 0.0, 0.0]
        actual_y = [0.0, normal[2], 0.0]
        midpoint_parameter = (start + end) / 2.0
        actual_midpoint = [center[i] + radius * (math.cos(midpoint_parameter) * actual_x[i]
                                                 + math.sin(midpoint_parameter) * actual_y[i])
                           for i in range(3)]
        expected_midpoint_angle = start_angle + requested_sweep / 2.0
        expected_midpoint = [expected_center[0] + radius * math.cos(expected_midpoint_angle),
                             expected_center[1] + radius * math.sin(expected_midpoint_angle), expected_center[2]]
        if _distance(actual_midpoint, expected_midpoint) > POSITION_TOLERANCE_MM:
            raise AssertionError(f"{label} native arc midpoint lies on the wrong half of the circle")
        return {"kind": kind, "center": center, "radius_mm": radius,
                "start_angle_rad": start, "end_angle_rad": end, "start": actual_start,
                "end": actual_end, "midpoint": actual_midpoint, "normal": normal}

    if geometry_type in ("ellipse", "elliptical_arc"):
        expected_kind = "ELLIPSE" if geometry_type == "ellipse" else "ELLIPTICAL_ARC"
        if kind != expected_kind:
            raise AssertionError(f"{label} expected native {expected_kind}, got {kind}")
        center = _as_vector(_field(record, ("center",), label + " center"), label + " center")
        if _distance(center, _world_point(requested["center_mm"], origin)) > POSITION_TOLERANCE_MM:
            raise AssertionError(f"{label} center readback differs from requested center")
        major = float(_field(record, ("major_radius_mm",), label + " major radius"))
        minor = float(_field(record, ("minor_radius_mm",), label + " minor radius"))
        _close(major, float(requested["major_radius_mm"]), LENGTH_TOLERANCE_MM, label + " major radius_mm")
        _close(minor, float(requested["minor_radius_mm"]), LENGTH_TOLERANCE_MM, label + " minor radius_mm")
        rotation = math.radians(float(requested["rotation_deg"]))
        expected_major_axis = [math.cos(rotation), math.sin(rotation), 0.0]
        major_axis = _as_vector(_field(record, ("major_axis_direction",), label + " major axis"), label + " major axis")
        if abs(_dot(_unit(major_axis, label + " major axis"), expected_major_axis)) < 1.0 - AXIS_DOT_TOLERANCE:
            raise AssertionError(f"{label} major-axis orientation differs from the requested rotation")
        if geometry_type == "elliptical_arc":
            if _dot(_unit(major_axis, label + " major axis"), expected_major_axis) < 1.0 - AXIS_DOT_TOLERANCE:
                raise AssertionError(f"{label} major-axis sign differs from the requested parameter frame")
        normal = _unit(_as_vector(_field(record, ("normal",), label + " normal"), label + " normal"), label + " normal")
        if len(normal) != 3:
            raise AssertionError(f"{label} normal does not contain three components")
        start = float(_field(record, ("start_angle_rad",), label + " start parameter"))
        end = float(_field(record, ("end_angle_rad",), label + " end parameter"))
        actual_start = _as_vector(_field(record, ("start",), label + " start point"), label + " start point")
        actual_end = _as_vector(_field(record, ("end",), label + " end point"), label + " end point")
        native_polar_start = float(_field(record, ("native_polar_start_angle_rad",), label + " native polar start angle"))
        native_polar_end = float(_field(record, ("native_polar_end_angle_rad",), label + " native polar end angle"))
        if not math.isfinite(native_polar_start) or not math.isfinite(native_polar_end):
            raise AssertionError(f"{label} native polar-angle readback is non-finite")
        parameter_orientation = 1.0
        if geometry_type == "ellipse":
            if _distance(normal, [0.0, 0.0, 1.0]) > AXIS_DOT_TOLERANCE:
                raise AssertionError(f"{label} full ellipse normal is not the expected +Z sketch normal")
            _close(abs(end - start), 2.0 * math.pi, ANGLE_TOLERANCE_RAD, label + " full-ellipse span")
            expected_start_angle = start
            expected_end_angle = end
        else:
            expected_sweep = math.radians(float(requested["sweep_deg"]))
            parameter_orientation = 1.0 if expected_sweep > 0.0 else -1.0
            expected_normal = [0.0, 0.0, parameter_orientation]
            if _distance(normal, expected_normal) > AXIS_DOT_TOLERANCE:
                raise AssertionError(f"{label} ellipse-arc normal does not preserve the signed schema sweep")
            expected_start_angle = math.radians(float(requested["start_angle_deg"]))
            expected_end_angle = expected_start_angle + expected_sweep
            start_parameter_error = math.atan2(
                math.sin(start - parameter_orientation * expected_start_angle),
                math.cos(start - parameter_orientation * expected_start_angle),
            )
            _close(start_parameter_error, 0.0, ANGLE_TOLERANCE_RAD,
                   label + " native evaluator start parameter")
            _close((end - start) * parameter_orientation, expected_sweep,
                   ANGLE_TOLERANCE_RAD, label + " signed ellipse parameter sweep")

        expected_center = _world_point(requested["center_mm"], origin)
        if geometry_type == "elliptical_arc":
            expected_start = _ellipse_endpoint(expected_center, major, minor, requested["rotation_deg"],
                                               math.degrees(expected_start_angle))
            expected_end = _ellipse_endpoint(expected_center, major, minor, requested["rotation_deg"],
                                             math.degrees(expected_end_angle))
            expected_midpoint = _ellipse_endpoint(expected_center, major, minor, requested["rotation_deg"],
                                                  math.degrees((expected_start_angle + expected_end_angle) / 2.0))
            if _distance(actual_start, expected_start) > POSITION_TOLERANCE_MM or _distance(actual_end, expected_end) > POSITION_TOLERANCE_MM:
                raise AssertionError(f"{label} evaluator endpoints differ from independently computed schema-parameter points")
        else:
            expected_start = [center[i] + major * math.cos(start) * major_axis[i]
                              + minor * math.sin(start) * ([-major_axis[1], major_axis[0], 0.0][i])
                              for i in range(3)]
            expected_end = [center[i] + major * math.cos(end) * major_axis[i]
                            + minor * math.sin(end) * ([-major_axis[1], major_axis[0], 0.0][i])
                            for i in range(3)]
            expected_midpoint = None
        minor_unit = [-major_axis[1], major_axis[0], 0.0]
        if parameter_orientation < 0.0:
            minor_unit = [-minor_unit[0], -minor_unit[1], -minor_unit[2]]
        parameter_midpoint = (start + end) / 2.0
        measured_midpoint = [center[i] + major * math.cos(parameter_midpoint) * major_axis[i]
                             + minor * math.sin(parameter_midpoint) * minor_unit[i] for i in range(3)]
        if geometry_type == "elliptical_arc" and _distance(measured_midpoint, expected_midpoint) > POSITION_TOLERANCE_MM:
            raise AssertionError(f"{label} parameter midpoint differs from the requested signed-sweep midpoint")
        major_unit = _unit(major_axis, label + " major axis")
        if _distance(actual_start, expected_start) > POSITION_TOLERANCE_MM or _distance(actual_end, expected_end) > POSITION_TOLERANCE_MM:
            raise AssertionError(f"{label} native ellipse endpoints disagree with its axes and parameters")
        result = {"kind": kind, "center": center, "major_radius_mm": major, "minor_radius_mm": minor,
                  "major_axis_direction": major_axis,
                  "start_angle_rad": start, "end_angle_rad": end,
                  "native_polar_start_angle_rad": native_polar_start,
                  "native_polar_end_angle_rad": native_polar_end,
                  "start": actual_start, "end": actual_end, "normal": normal,
                  "parameter_midpoint": measured_midpoint}
        return result

    if geometry_type == "spline":
        if kind != "SPLINE":
            raise AssertionError(f"{label} expected native Spline, got {kind}")
        degree = int(_field(record, ("degree",), label + " degree"))
        if degree != int(requested["degree"]):
            raise AssertionError(f"{label} degree expected {requested['degree']}, got {degree}")
        points = _field(record, ("interpolation_points",), label + " interpolation points")
        if not isinstance(points, list) or len(points) != len(requested["interpolation_points_mm"]):
            raise AssertionError(f"{label} interpolation point count changed")
        periodic = record.get("periodic")
        if periodic not in (0, False):
            raise AssertionError(f"{label} spline must be nonperiodic; got periodic={periodic!r}")
        point_count = int(_field(record, ("interpolation_point_count",), label + " interpolation_point_count"))
        if point_count != len(points):
            raise AssertionError(f"{label} interpolation_point_count disagrees with the point list")
        actual_points = [_as_vector(point, label + " interpolation point") for point in points]
        wanted_points = [_world_point(point, origin) for point in requested["interpolation_points_mm"]]
        forward_error = max(_distance(a, b) for a, b in zip(actual_points, wanted_points))
        if forward_error > POSITION_TOLERANCE_MM:
            raise AssertionError(f"{label} interpolated point readback differs by {forward_error} mm")
        parameters = _field(record, ("interpolation_parameters",), label + " interpolation parameters")
        if not isinstance(parameters, list) or len(parameters) != point_count:
            raise AssertionError(f"{label} interpolation parameter count changed")
        chords = [_distance(wanted_points[index - 1], wanted_points[index]) for index in range(1, point_count)]
        total_chord = sum(chords)
        expected_parameters = [0.0]
        for chord in chords:
            expected_parameters.append(expected_parameters[-1] + chord / total_chord)
        actual_parameters = [float(value) for value in parameters]
        if any(abs(actual_parameters[i] - expected_parameters[i]) > PARAMETER_TOLERANCE
               for i in range(point_count)):
            raise AssertionError(f"{label} interpolation parameters are not cumulative chord length")
        actual_start = _as_vector(_field(record, ("start",), label + " start point"), label + " start point")
        actual_end = _as_vector(_field(record, ("end",), label + " end point"), label + " end point")
        if _distance(actual_start, wanted_points[0]) > POSITION_TOLERANCE_MM or _distance(actual_end, wanted_points[-1]) > POSITION_TOLERANCE_MM:
            raise AssertionError(f"{label} native spline endpoints differ from interpolation endpoints")
        return {"kind": kind, "degree": degree, "periodic": periodic,
                "interpolation_point_count": point_count, "interpolation_points": actual_points,
                "interpolation_parameters": actual_parameters, "start": actual_start, "end": actual_end}

    raise AssertionError(f"unsupported request type in independent oracle: {geometry_type}")


def _expand_requests(geometries: list[dict]) -> list[dict]:
    expanded: list[dict] = []
    for geometry in geometries:
        if geometry["type"] == "rectangle":
            x, y = geometry["corner_mm"]
            w, h = geometry["width_mm"], geometry["height_mm"]
            points = [[x, y], [x + w, y], [x + w, y + h], [x, y + h]]
            for index in range(4):
                expanded.append({"id": f"{geometry['id']}-edge-{index}", "type": "line",
                                 "start_mm": points[index], "end_mm": points[(index + 1) % 4]})
        else:
            expanded.append(geometry)
    return expanded


def _inspect_sketch_record(inspection: dict, expected_id: str | None = None) -> dict:
    sketch = inspection.get("sketch") if isinstance(inspection.get("sketch"), dict) else inspection
    if not isinstance(sketch, dict) or not isinstance(sketch.get("geometry"), list):
        raise AssertionError("nx_inspect_sketch or nx_inspect_part omitted sketch geometry")
    if expected_id is not None and sketch.get("journal_id") != expected_id:
        raise AssertionError("nx_inspect_sketch returned a different sketch journal_id")
    return sketch


def _independent_geometry_readback(sketch: dict, geometries: list[dict], origin: list[float], label: str) -> list[dict]:
    actual = sketch["geometry"]
    expected = _expand_requests(geometries)
    if len(actual) != len(expected):
        raise AssertionError(f"{label} geometry count expected {len(expected)}, got {len(actual)}")
    unmatched = list(actual)
    evidence: list[dict] = []
    for request in expected:
        matches: list[tuple[dict, dict]] = []
        candidate_mismatches: list[dict] = []
        for candidate in unmatched:
            if not isinstance(candidate, dict):
                candidate_mismatches.append({"journal_id": None, "kind": None,
                                             "reason": "native geometry candidate is not an object"})
                continue
            try:
                readback = _verify_native_record(candidate, request, origin, label + "/" + request["id"])
            except (AssertionError, TypeError, ValueError, KeyError) as exc:
                candidate_mismatches.append({"journal_id": candidate.get("journal_id"),
                                             "kind": candidate.get("kind"), "reason": str(exc)})
                continue
            matches.append((candidate, readback))
        if len(matches) != 1:
            raise AssertionError(
                f"{label} could not resolve {request['id']} uniquely from numeric native readback; "
                f"matches={len(matches)}; matching_journal_ids={[item[0].get('journal_id') for item in matches]!r}; "
                f"candidate_mismatches={candidate_mismatches[:12]!r}; "
                f"omitted_candidate_mismatch_count={max(0, len(candidate_mismatches) - 12)}"
            )
        record, readback = matches[0]
        unmatched.remove(record)
        if record.get("detail_status") not in (None, "SUPPORTED"):
            raise AssertionError(f"{label}/{request['id']} native parameter detail is not supported")
        evidence.append({"request_id": request["id"], "journal_id": record.get("journal_id"),
                         "requested": request, "actual": readback})
    return evidence


def _resolve_sketch_by_geometry(sketches: list, spec: dict, label: str) -> tuple[dict, list[dict]]:
    matches: list[tuple[dict, list[dict]]] = []
    candidate_mismatches: list[dict] = []
    for candidate in sketches:
        if not isinstance(candidate, dict):
            candidate_mismatches.append({"sketch_id": None, "reason": "sketch candidate is not an object"})
            continue
        try:
            numeric = _independent_geometry_readback(
                candidate, spec["geometries"], spec["origin_mm"], label,
            )
        except (AssertionError, TypeError, ValueError, KeyError) as exc:
            candidate_mismatches.append({"sketch_id": candidate.get("journal_id"), "reason": str(exc)})
            continue
        matches.append((candidate, numeric))
    if len(matches) != 1:
        raise AssertionError(
            f"{label} did not resolve uniquely from current native numeric readback; matches={len(matches)}; "
            f"matching_sketch_ids={[item[0].get('journal_id') for item in matches]!r}; "
            f"candidate_mismatches={candidate_mismatches[:12]!r}; "
            f"omitted_candidate_mismatch_count={max(0, len(candidate_mismatches) - 12)}"
        )
    sketch, numeric = matches[0]
    if not isinstance(sketch.get("journal_id"), str) or not sketch["journal_id"]:
        raise AssertionError(f"{label} current sketch omitted journal_id")
    return sketch, numeric


def _assert_verified(result: dict, label: str) -> dict:
    if result.get("verified") is not True:
        raise AssertionError(f"{label} did not return verified=true")
    checks = result.get("checks")
    if not isinstance(checks, dict) or not checks:
        raise AssertionError(f"{label} omitted production verification checks")
    bad = {name: check for name, check in checks.items()
           if not isinstance(check, dict) or check.get("status") != "VERIFIED"}
    if bad:
        raise AssertionError(f"{label} contains non-VERIFIED production checks: {bad}")
    return result


def _error_code(value: object) -> str | None:
    if isinstance(value, dict):
        for key in ("code", "error_code"):
            if isinstance(value.get(key), str):
                return value[key]
        for child in value.values():
            found = _error_code(child)
            if found:
                return found
    elif isinstance(value, list):
        for child in value:
            found = _error_code(child)
            if found:
                return found
    elif isinstance(value, str):
        try:
            return _error_code(json.loads(value))
        except (ValueError, TypeError):
            return None
    return None


def _mcp_payload(response: dict, tool: str) -> dict:
    if response.get("isError") is True:
        raise RuntimeError(f"{tool} returned MCP tool error { _error_code(response) or 'UNKNOWN' }")
    payload = response.get("structuredContent")
    if isinstance(payload, dict):
        return payload
    for item in response.get("content", []):
        if isinstance(item, dict) and isinstance(item.get("text"), str):
            try:
                decoded = json.loads(item["text"])
            except ValueError:
                continue
            if isinstance(decoded, dict):
                return decoded
    raise AssertionError(f"{tool} returned no structured result")


def _validate_expected_failed_operation(operation: object, expected_code: str, label: str) -> dict:
    if not isinstance(operation, dict):
        raise AssertionError(f"{label} did not return an operation record")
    state = operation.get("state")
    actual_code = _error_code(operation)
    if state != "FAILED" or actual_code != expected_code:
        raise AssertionError(f"{label} expected FAILED/{expected_code}, got {state}/{actual_code or 'NO_ERROR_CODE'}")
    if operation.get("result") is not None or operation.get("plan") is not None or operation.get("plan_id") is not None:
        raise AssertionError(f"{label} failed operation unexpectedly returned a result or plan")
    if operation.get("writes_halted") is not False:
        raise AssertionError(f"{label} failed operation did not preserve writes_halted=false")
    return operation


def _wait_for_operation_terminal(client: McpStdioClient, operation: dict, label: str,
                                 timeout_seconds: float = 180.0,
                                 poll_interval_seconds: float = 0.2) -> dict:
    operation_id = operation.get("operation_id")
    if not isinstance(operation_id, str) or not operation_id:
        raise AssertionError(f"{label} async operation response omitted operation_id")
    terminal_states = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
    deadline = time.monotonic() + timeout_seconds
    current = operation
    while current.get("state") not in terminal_states:
        if time.monotonic() >= deadline:
            raise AssertionError(f"{label} operation {operation_id} did not reach a terminal state")
        time.sleep(poll_interval_seconds)
        response = client.request("tools/call", {
            "name": "nx_get_operation", "arguments": {"operation_id": operation_id},
        })
        if response.get("isError") is True:
            raise AssertionError(f"{label} nx_get_operation poll failed: {_error_code(response) or 'UNKNOWN'}")
        current = _mcp_payload(response, "nx_get_operation")
        returned_id = current.get("operation_id")
        if returned_id is not None and returned_id != operation_id:
            raise AssertionError(f"{label} nx_get_operation returned a different operation_id")
    return current


def _case_request(spec: dict) -> dict:
    request = {"plane": "XY", "origin_mm": spec["origin_mm"], "geometries": spec["geometries"]}
    if "constraint_mode" in spec:
        request["constraint_mode"] = spec["constraint_mode"]
    return request


def _line(identifier: str, start: list[float], end: list[float]) -> dict:
    return {"id": identifier, "type": "line", "start_mm": start, "end_mm": end}


def _ellipse_endpoint(center: list[float], major: float, minor: float, rotation_deg: float, parameter_deg: float) -> list[float]:
    theta, rotation = math.radians(parameter_deg), math.radians(rotation_deg)
    x, y = major * math.cos(theta), minor * math.sin(theta)
    point = [center[0] + x * math.cos(rotation) - y * math.sin(rotation),
             center[1] + x * math.sin(rotation) + y * math.cos(rotation)]
    if len(center) == 3:
        point.append(center[2])
    elif len(center) != 2:
        raise AssertionError("ellipse endpoint center must contain two local or three world components")
    return point


def _f06_specs() -> dict[str, dict]:
    arc_center = [2.0, 3.0]
    ellipse_arc_center = [2.0, 1.0]
    ellipse_arc_end = _ellipse_endpoint(ellipse_arc_center, 8.0, 3.0, 27.0, 270.0)
    ellipse_arc_start = _ellipse_endpoint(ellipse_arc_center, 8.0, 3.0, 27.0, 0.0)
    multi_region: list[dict] = []
    l_shape = [[0, 0], [20, 0], [20, 8], [8, 8], [8, 18], [0, 18]]
    hole = [[2, 2], [2, 6], [6, 6], [6, 2]]
    island = [[26, 0], [34, 0], [34, 5], [26, 5]]
    for prefix, points in (("concave", l_shape), ("hole", hole), ("region", island)):
        for index, start in enumerate(points):
            multi_region.append(_line(f"{prefix}-{index}", start, points[(index + 1) % len(points)]))
    return {
        "arc_closed": {
            "constraint_mode": "geometry_only",
            "origin_mm": [0.0, 0.0, 0.0],
            "geometries": [
                {"id": "semicircle", "type": "arc", "center_mm": arc_center, "radius_mm": 5,
                 "start_angle_deg": 0, "sweep_deg": -180},
                _line("diameter", [arc_center[0] - 5, arc_center[1]], [arc_center[0] + 5, arc_center[1]]),
            ],
            "profile": {"status": "VALID_CLOSED_PROFILES", "curve_count": 2, "open_chain_count": 0,
                        "closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0,
                        "section_loop_count": 1},
        },
        "rotated_ellipse": {
            "constraint_mode": "geometry_only",
            "origin_mm": [20.0, 0.0, 0.0],
            "geometries": [
                {"id": "tilted-ellipse", "type": "ellipse", "center_mm": [3, 2],
                 "major_radius_mm": 8, "minor_radius_mm": 4, "rotation_deg": 31},
            ],
            "profile": {"status": "VALID_CLOSED_PROFILES", "curve_count": 1, "open_chain_count": 0,
                        "closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0,
                        "section_loop_count": 1},
        },
        "elliptical_arc_closed": {
            "constraint_mode": "geometry_only",
            "origin_mm": [45.0, 0.0, 0.0],
            "geometries": [
                {"id": "elliptic-arc", "type": "elliptical_arc", "center_mm": ellipse_arc_center,
                 "major_radius_mm": 8, "minor_radius_mm": 3, "rotation_deg": 27,
                 "start_angle_deg": 0, "sweep_deg": 270},
                _line("elliptic-arc-chord", ellipse_arc_end, ellipse_arc_start),
            ],
            "profile": {"status": "VALID_CLOSED_PROFILES", "curve_count": 2, "open_chain_count": 0,
                        "closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0,
                        "section_loop_count": 1},
        },
        "elliptical_arc_negative_open": {
            "constraint_mode": "geometry_only",
            "origin_mm": [60.0, 0.0, 0.0],
            "geometries": [
                {"id": "elliptic-arc-negative", "type": "elliptical_arc", "center_mm": [3.0, -2.0],
                 "major_radius_mm": 9.0, "minor_radius_mm": 3.0, "rotation_deg": 31.0,
                 "start_angle_deg": 210.0, "sweep_deg": -195.0},
            ],
            "profile": {"status": "OPEN_GEOMETRY", "curve_count": 1, "open_chain_count": 1,
                        "closed_loop_count": 0, "outer_loop_count": 0, "inner_loop_count": 0,
                        "section_loop_count": 0},
        },
        "spline_closed": {
            "constraint_mode": "geometry_only",
            "origin_mm": [70.0, 0.0, 0.0],
            "geometries": [
                {"id": "fit-spline", "type": "spline",
                 "interpolation_points_mm": [[-5, -2], [-2, 4], [2, 4], [5, -2]],
                 "degree": 3, "endpoint_condition": "unconstrained"},
                _line("spline-close-1", [5, -2], [3, -4]),
                _line("spline-close-2", [3, -4], [0, -5]),
                _line("spline-close-3", [0, -5], [-3, -4]),
                _line("spline-close-4", [-3, -4], [-5, -2]),
            ],
            "profile": {"status": "VALID_CLOSED_PROFILES", "curve_count": 5, "open_chain_count": 0,
                        "closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0,
                        "section_loop_count": 1, "spline_self_intersection_check": "VERIFIED"},
        },
        "concave_hole_and_region": {
            "constraint_mode": "geometry_only",
            "origin_mm": [100.0, 0.0, 0.0], "geometries": multi_region,
            "profile": {"status": "VALID_CLOSED_PROFILES", "curve_count": 14, "open_chain_count": 0,
                        "closed_loop_count": 3, "outer_loop_count": 2, "inner_loop_count": 1,
                        "section_loop_count": 3},
        },
        "legacy_rectangle": {
            "origin_mm": [140.0, 0.0, 0.0],
            "geometries": [{"id": "legacy-rectangle", "type": "rectangle", "corner_mm": [0, 0],
                            "width_mm": 10, "height_mm": 6}],
            "profile": {"status": "VALID_CLOSED_PROFILES", "curve_count": 4, "open_chain_count": 0,
                        "closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0,
                        "section_loop_count": 1},
        },
        "legacy_circle": {
            "origin_mm": [160.0, 0.0, 0.0],
            "geometries": [{"id": "legacy-circle", "type": "circle", "center_mm": [0, 0], "diameter_mm": 8}],
            "profile": {"status": "VALID_CLOSED_PROFILES", "curve_count": 1, "open_chain_count": 0,
                        "closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0,
                        "section_loop_count": 1},
        },
    }


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    mode = "ready" if args.ready else "part"
    report: dict = {
        "test": "F06_installed_mcp_smoke", "mode": mode,
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "configuration": {"expected_mcp_version": EXPECTED_SERVER_VERSION,
                          "expected_tool_count": EXPECTED_TOOL_COUNT,
                          "NX_geometry_tolerances": {"position_mm": POSITION_TOLERANCE_MM,
                                                      "length_mm": LENGTH_TOLERANCE_MM,
                                                      "angle_rad": ANGLE_TOLERANCE_RAD,
                                                      "axis_dot": AXIS_DOT_TOLERANCE,
                                                      "spline_parameter": PARAMETER_TOLERANCE},
                          "measurement_uncertainty": "not applicable; digital NX model values, no physical instrument",
                          "sample_size": 1, "reliability_claim": False},
        "calls": [], "steps": [], "cases": [],
    }
    report["test_scope"] = {
        "sample_size": 1,
        "fixture_type": "fresh empty saved independent part",
        "instrumentation": "NX native geometry and feature readback via installed MCP",
        "calibration": "no physical instrument; tolerances apply to NX model values",
        "reliability_inference": False,
    }
    credentials: dict[str, object] = {}
    client: McpStdioClient | None = None
    revision_id: str | None = None

    def persist() -> None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(_redact(report), ensure_ascii=False, allow_nan=False, indent=2) + "\n",
                               encoding="utf-8")

    def call(name: str, arguments: dict) -> dict:
        if client is None:
            raise RuntimeError("installed MCP client is not initialized")
        entry = {"tool": name, "arguments": _redact(arguments)}
        report["calls"].append(entry)
        try:
            response = client.request("tools/call", {"name": name, "arguments": arguments})
        except Exception as exc:
            entry["transport_error"] = repr(exc)
            persist()
            raise
        entry["mcp_response"] = _redact(response)
        if response.get("isError") is True:
            persist()
            raise RuntimeError(f"{name} returned MCP tool error {_error_code(response) or 'UNKNOWN'}")
        payload = _mcp_payload(response, name)
        entry["payload"] = _redact(payload)
        if "operation_id" in payload:
            try:
                completed = _wait_operation(client, payload, 180, 0.2)
            except Exception as exc:
                entry["operation_wait_error"] = repr(exc)
                persist()
                raise
            entry["completed"] = _redact(completed)
            persist()
            result = completed.get("result")
            if not isinstance(result, dict):
                raise AssertionError(f"{name} completed without a result object")
            return result
        persist()
        return payload

    def expect_tool_error(name: str, arguments: dict, expected_code: str) -> dict:
        if client is None:
            raise RuntimeError("installed MCP client is not initialized")
        entry = {"tool": name, "arguments": _redact(arguments), "expected_error_code": expected_code}
        report["calls"].append(entry)
        try:
            response = client.request("tools/call", {"name": name, "arguments": arguments})
        except Exception as exc:
            entry["transport_error"] = repr(exc)
            persist()
            raise
        entry["mcp_response"] = _redact(response)
        if response.get("isError") is True:
            actual_code = _error_code(response)
            entry["observed_error_code"] = actual_code
            if actual_code != expected_code:
                entry["unexpected_payload"] = _redact(response)
                persist()
                raise AssertionError(f"{name} expected MCP tool error {expected_code}, got {actual_code or 'UNKNOWN'}")
            entry["expected_failure_mode"] = "direct_mcp_tool_error"
            persist()
            return response

        try:
            payload = _mcp_payload(response, name)
            entry["payload"] = _redact(payload)
            if isinstance(payload.get("operation_id"), str):
                terminal = _wait_for_operation_terminal(client, payload, name)
                entry["completed"] = _redact(terminal)
                entry["terminal_state"] = terminal.get("state")
                entry["observed_error_code"] = _error_code(terminal)
                result = _validate_expected_failed_operation(terminal, expected_code, name)
                entry["expected_failure_mode"] = "async_operation_failed"
            else:
                result = _validate_expected_failed_operation(payload, expected_code, name)
                entry["expected_failure_mode"] = "sync_operation_failed"
            entry["observed_error_code"] = _error_code(result)
            persist()
            return result
        except Exception as exc:
            entry["unexpected_payload"] = entry.get("completed", entry.get("payload", _redact(response)))
            entry["validation_error"] = repr(exc)
            persist()
            raise

    def step(tool: str, arguments: dict, label: str, profile_expected: dict | None = None) -> tuple[dict, dict, dict]:
        nonlocal revision_id
        case = {"case_id": label, "state": "RUNNING", "request": arguments,
                "prepare": None, "execute": None, "verify": None}
        report["steps"].append(case)
        active_case_holder[0] = case
        persist()
        try:
            prepared = call(tool, {**credentials, "operation_id": "f06-prepare-" + uuid.uuid4().hex,
                                   "revision_id": revision_id, **arguments})
            case["prepare"] = _redact(prepared)
            plan = prepared.get("plan")
            if not isinstance(plan, dict) or not isinstance(plan.get("plan_id"), str):
                raise AssertionError(f"{label} prepare omitted plan.plan_id")
            execute_id = "f06-execute-" + uuid.uuid4().hex
            executed = call("nx_execute_plan", {**credentials, "operation_id": execute_id, "plan_id": plan["plan_id"]})
            case["execute"] = _redact(executed)
            if executed.get("mutation_state") != "APPLIED":
                raise AssertionError(f"{label} execute mutation_state was not APPLIED")
            execute_profile = None
            if profile_expected is not None:
                execute_profile = _assert_profile_analysis(executed, profile_expected, label + " execute")
                case["execute_profile_analysis"] = execute_profile
            verified = call("nx_verify_part", {**credentials, "operation_id": "f06-verify-" + uuid.uuid4().hex,
                                                "execute_operation_id": execute_id})
            case["verify"] = _redact(verified)
            _assert_verified(verified, label + " nx_verify_part")
            if profile_expected is not None:
                verify_profile = _assert_profile_analysis(verified, profile_expected, label + " verify")
                case["verify_profile_analysis"] = verify_profile
                _assert_intersection_pair_data_matches(execute_profile, verify_profile, label)
                case["intersection_pair_data_matches_execute_verify"] = True
            revision_id = verified.get("revision_id") or executed.get("revision_id") or revision_id
            case["state"] = "PASS"
            persist()
            return prepared, executed, verified
        except Exception as exc:
            case["state"] = "FAIL"
            case["failure"] = repr(exc)
            case["reproduction_steps"] = [
                "Create a fresh fixture with tools/v2/create-f06-mcp-fixture.py and open it in the dedicated NX GUI.",
                "Rerun tests/nx/v2/smoke-f06-mcp.py with that --part-path and a new --output path.",
                f"Repeat {label} using the request and raw MCP payloads retained in this evidence file.",
            ]
            case["retest_criterion"] = "The same operation returns APPLIED, nx_verify_part returns verified=true with all checks VERIFIED and the expected profile_analysis, and independent numeric inspection matches the requested geometry within recorded tolerances."
            persist()
            raise

    def record_case(case_id: str, evidence: dict) -> dict:
        case = {"case_id": case_id, "state": "RUNNING", **evidence}
        report["cases"].append(case)
        active_case_holder[0] = case
        persist()
        return case

    active_case_holder: list[dict | None] = [None]
    try:
        if args.part_path is not None:
            part_path = args.part_path.resolve(strict=True)
            model_root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
            if not part_path.is_relative_to(model_root) or part_path.suffix.lower() != ".prt":
                raise ValueError("--part-path must be a saved .prt inside workspace/v2/models")
            report["part_path"] = str(part_path)

        install = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy()
        env.update(PYTHONPATH=str(install / "app" / "src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen(
            [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install / "mcp-config.json")],
            cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0,
        )
        client = McpStdioClient(process, 60)
        report["initialize"] = _redact(client.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION, "capabilities": {},
            "clientInfo": {"name": "f06-installed-acceptance", "version": "1"},
        }))
        client.notify("notifications/initialized")
        server_info = report["initialize"].get("serverInfo")
        if not isinstance(server_info, dict) or server_info.get("version") != EXPECTED_SERVER_VERSION:
            raise AssertionError(f"installed MCP version must be {EXPECTED_SERVER_VERSION}: {server_info}")
        report["server_version"] = server_info["version"]
        tools = client.request("tools/list").get("tools")
        if not isinstance(tools, list):
            raise AssertionError("MCP tools/list omitted tools")
        tool_names = {item.get("name") for item in tools if isinstance(item, dict)}
        report["tool_count"] = len(tool_names)
        report["tool_names"] = sorted(name for name in tool_names if isinstance(name, str))
        if len(tool_names) != EXPECTED_TOOL_COUNT:
            raise AssertionError(f"installed MCP must expose {EXPECTED_TOOL_COUNT} unique tools")
        required_tools = {"nx_ensure_ready", "nx_bridge_status", "nx_takeover", "nx_return", "nx_get_capabilities",
                          "nx_get_session", "nx_inspect_part", "nx_inspect_sketch", "nx_prepare_sketch",
                          "nx_prepare_extrude", "nx_execute_plan", "nx_verify_part", "nx_save_copy"}
        if not required_tools <= tool_names:
            raise AssertionError(f"MCP tools/list omitted required tools: {sorted(required_tools - tool_names)}")
        sketch_tool = next(item for item in tools if isinstance(item, dict) and item.get("name") == "nx_prepare_sketch")
        sketch_input_schema = sketch_tool.get("inputSchema", {})
        sketch_properties = sketch_input_schema.get("properties", {})
        geometry_schema = sketch_properties.get("geometries", {}).get("items", {})
        alternatives = geometry_schema.get("anyOf", [])
        schema_types = {
            item.get("properties", {}).get("type", {}).get("const")
            for item in alternatives if isinstance(item, dict)
        }
        expected_geometry_types = {"line", "rectangle", "circle", "arc", "ellipse", "elliptical_arc", "spline"}
        if not expected_geometry_types <= schema_types:
            raise AssertionError(f"installed nx_prepare_sketch schema omitted geometry types: {sorted(expected_geometry_types - schema_types)}")
        report["installed_sketch_geometry_schema_types"] = sorted(schema_types)
        constraint_schema = sketch_properties.get("constraint_mode", {})
        constraint_modes = set(constraint_schema.get("enum", [])) if isinstance(constraint_schema, dict) else set()
        for choice in constraint_schema.get("anyOf", []) if isinstance(constraint_schema, dict) else []:
            if isinstance(choice, dict) and isinstance(choice.get("const"), str):
                constraint_modes.add(choice["const"])
        if not {"legacy_driving", "geometry_only"} <= constraint_modes:
            raise AssertionError(f"installed nx_prepare_sketch schema omitted constraint modes: {sorted(constraint_modes)}")
        if "constraint_mode" in sketch_input_schema.get("required", []):
            raise AssertionError("nx_prepare_sketch constraint_mode must remain optional for legacy requests")
        report["installed_sketch_constraint_modes"] = sorted(constraint_modes)
        report["tools_list"] = _redact(tools)

        ready = call("nx_ensure_ready", {})
        for _ in range(3):
            if ready.get("state") != "STARTING":
                break
            time.sleep(1)
            ready = call("nx_ensure_ready", {})
        report["ready"] = _redact(ready)
        if ready.get("state") != "READY":
            raise AssertionError(f"nx_ensure_ready did not reach READY: {ready}")
        status = call("nx_bridge_status", {})
        report["initial_status"] = _redact(status)
        report["nx_pid"] = status.get("pid")
        if status.get("ownership_state") != "HUMAN_OWNED":
            raise AssertionError(f"readiness must leave NX HUMAN_OWNED: {status}")
        try:
            initial_in_flight = int(status.get("in_flight"))
        except (TypeError, ValueError):
            initial_in_flight = -1
        if status.get("writes_halted") is not False or initial_in_flight != 0:
            raise AssertionError(f"initial bridge status must be unhalted with no in-flight operation: {status}")
        if args.ready:
            report["passed"] = True
            report["diagnostic_only"] = True
            return 0

        target_part = report["part_path"]
        takeover = call("nx_takeover", {"expected_pid": status["pid"], "expected_part_path": target_part,
                                         "artifact_scope": "saved_independent_copy", "authorize": True})
        if not isinstance(takeover.get("ownership_id"), str) or not isinstance(takeover.get("binding_epoch"), int):
            raise AssertionError("nx_takeover omitted ownership credentials")
        credentials.update(ownership_id=takeover["ownership_id"], binding_epoch=takeover["binding_epoch"])
        revision_id = takeover.get("revision_id")
        report["revision_id"] = revision_id
        report["takeover"] = _redact(takeover)
        report["capabilities"] = _redact(call("nx_get_capabilities", {}))
        session = call("nx_get_session", credentials)
        if int(session.get("process_id", -1)) != int(status["pid"]):
            raise AssertionError("nx_get_session process_id differs from the expected NX PID")
        if (not isinstance(session.get("part_path"), str)
                or Path(session["part_path"]).resolve().as_posix().casefold()
                != Path(target_part).resolve().as_posix().casefold()):
            raise AssertionError("nx_get_session part_path differs from the expected isolated fixture")
        report["session_after_takeover"] = _redact(session)
        initial_part = call("nx_inspect_part", credentials)
        if (initial_part.get("part_path")
                and Path(initial_part["part_path"]).resolve().as_posix().casefold()
                != Path(target_part).resolve().as_posix().casefold()):
            raise AssertionError("initial nx_inspect_part returned a different part path")
        report["initial_part_inspection"] = _redact(initial_part)

        specs = _f06_specs()
        sketch_ids: dict[str, str] = {}
        report["fresh_id_resolutions"] = []

        def refresh_sketch_id(name: str, stage: str) -> str:
            part = call("nx_inspect_part", credentials)
            if (part.get("part_path")
                    and Path(part["part_path"]).resolve().as_posix().casefold()
                    != Path(target_part).resolve().as_posix().casefold()):
                raise AssertionError(f"{stage} inspect returned a different part path")
            part_sketches = part.get("sketches")
            if not isinstance(part_sketches, list):
                raise AssertionError(f"{stage} nx_inspect_part omitted sketches")
            sketch, numeric = _resolve_sketch_by_geometry(part_sketches, specs[name], name + " " + stage)
            sketch_ids[name] = sketch["journal_id"]
            report["fresh_id_resolutions"].append({
                "case_id": name, "stage": stage, "sketch_id": sketch["journal_id"],
                "inspection": _redact(sketch), "independent_numeric_readback": numeric,
            })
            persist()
            return sketch["journal_id"]
        # The native curve families include both signs of ellipse-arc sweep;
        # a separate sketch checks concavity, a hole, and a disconnected region.
        f06_names = ("arc_closed", "rotated_ellipse", "elliptical_arc_closed", "elliptical_arc_negative_open", "spline_closed",
                     "concave_hole_and_region")
        for name in f06_names:
            spec = specs[name]
            case = record_case("F06_" + name, {"request": _case_request(spec), "profile_expected": spec["profile"]})
            active_case_holder[0] = case
            _, executed, verified = step("nx_prepare_sketch", _case_request(spec), case["case_id"], spec["profile"])
            active_case_holder[0] = case
            sketch_id = executed.get("sketch_id") or verified.get("sketch_id")
            if not isinstance(sketch_id, str) or not sketch_id:
                raise AssertionError(f"{name} execution omitted sketch_id")
            sketch_ids[name] = sketch_id
            active_case_holder[0] = case
            inspection = call("nx_inspect_sketch", {**credentials, "sketch_id": sketch_id})
            sketch = _inspect_sketch_record(inspection, sketch_id)
            numeric = _independent_geometry_readback(sketch, spec["geometries"], spec["origin_mm"], name)
            intersection_endpoints = _assert_profile_intersections_against_inspection(executed, verified, sketch, name)
            case.update(state="PASS", sketch_id=sketch_id, inspection=_redact(inspection),
                        independent_numeric_readback=numeric, intersection_endpoint_checks=intersection_endpoints)
            persist()

        for name in ("legacy_rectangle", "legacy_circle"):
            spec = specs[name]
            case = record_case("F06_" + name, {"request": _case_request(spec), "profile_expected": spec["profile"]})
            active_case_holder[0] = case
            _, executed, verified = step("nx_prepare_sketch", _case_request(spec), case["case_id"], spec["profile"])
            active_case_holder[0] = case
            sketch_id = executed.get("sketch_id") or verified.get("sketch_id")
            if not isinstance(sketch_id, str) or not sketch_id:
                raise AssertionError(f"{name} execution omitted sketch_id")
            sketch_ids[name] = sketch_id
            active_case_holder[0] = case
            inspection = call("nx_inspect_sketch", {**credentials, "sketch_id": sketch_id})
            sketch = _inspect_sketch_record(inspection, sketch_id)
            numeric = _independent_geometry_readback(sketch, spec["geometries"], spec["origin_mm"], name)
            intersection_endpoints = _assert_profile_intersections_against_inspection(executed, verified, sketch, name)
            case.update(state="PASS", sketch_id=sketch_id, inspection=_redact(inspection),
                        independent_numeric_readback=numeric, intersection_endpoint_checks=intersection_endpoints)
            persist()

        # NX JournalIdentifiers can change as later features are appended.
        # Resolve every sketch from current native geometry before using IDs in
        # the extrusion probes.
        refresh_case = record_case("F06_refresh_current_sketch_ids_before_extrusion", {
            "resolution_method": "unique native geometry numeric readback; JournalIdentifier values are not reused",
            "sketch_names": list(specs),
        })
        for name in specs:
            refresh_sketch_id(name, "before_profile_extrusion_probes")
        refresh_case["state"] = "PASS"
        refresh_case["resolved_sketch_ids"] = {name: sketch_ids[name] for name in specs}
        active_case_holder[0] = refresh_case
        persist()

        # The previously supported rectangle and full-circle extrusion flows
        # must continue to prepare, execute, and verify successfully.
        for name in ("legacy_rectangle", "legacy_circle"):
            case = record_case("F06_" + name + "_legacy_extrude", {
                "sketch_id": None, "mode": "create", "direction": "positive",
                "extent": "finite", "depth_mm": 5.0,
            })
            active_case_holder[0] = case
            current_sketch_id = refresh_sketch_id(name, "before_legacy_extrusion")
            case["sketch_id"] = current_sketch_id
            _, executed, verified = step("nx_prepare_extrude", {
                "sketch_id": current_sketch_id, "mode": "create", "direction": "positive",
                "extent": "finite", "depth_mm": 5.0,
            }, case["case_id"])
            active_case_holder[0] = case
            case.update(state="PASS", execution=_redact(executed), verification=_redact(verified))
            persist()

        target_copy = Path(target_part).parent / ("F06-mcp-" + uuid.uuid4().hex + "-roundtrip.prt")
        if target_copy.exists():
            raise FileExistsError("unique F06 roundtrip part path already exists")
        roundtrip = record_case("F06_save_reopen_all_geometry", {"target_path": str(target_copy),
                                                                  "sketch_cases": list(specs)})
        active_case_holder[0] = roundtrip
        saved = call("nx_save_copy", {**credentials, "operation_id": "f06-save-" + uuid.uuid4().hex,
                                       "target_path": str(target_copy), "reopen": True})
        if isinstance(saved.get("new_ownership_id"), str) and isinstance(saved.get("new_binding_epoch"), int):
            credentials.update(ownership_id=saved["new_ownership_id"], binding_epoch=saved["new_binding_epoch"])
        else:
            raise AssertionError("nx_save_copy omitted new ownership credentials")
        if saved.get("saved") is not True or saved.get("reopened") is not True:
            raise AssertionError("nx_save_copy did not report saved=true and reopened=true")
        save_checks = saved.get("checks")
        required_save_checks = {"associations", "expression", "geometry", "history"}
        if not isinstance(save_checks, dict) or set(save_checks) != required_save_checks:
            raise AssertionError("nx_save_copy omitted one or more required roundtrip checks")
        if any(not isinstance(check, dict) or check.get("status") != "VERIFIED" for check in save_checks.values()):
            raise AssertionError("nx_save_copy contains a non-VERIFIED roundtrip check")
        report["saved_path"] = saved.get("saved_path") or str(target_copy)
        reopened = call("nx_inspect_part", credentials)
        if (reopened.get("part_path")
                and Path(reopened["part_path"]).resolve().as_posix().casefold()
                != Path(report["saved_path"]).resolve().as_posix().casefold()):
            raise AssertionError("reopened nx_inspect_part returned a different saved-copy path")
        reopened_sketches = reopened.get("sketches")
        if not isinstance(reopened_sketches, list):
            raise AssertionError("reopened nx_inspect_part omitted sketches")
        reopened_numeric: dict[str, dict] = {}
        for name, spec in specs.items():
            sketch, numeric = _resolve_sketch_by_geometry(reopened_sketches, spec, name + " reopened")
            reopened_numeric[name] = {"sketch_id_after_reopen": sketch["journal_id"],
                                      "inspection": _redact(sketch),
                                      "independent_numeric_readback": numeric}
        roundtrip.update(state="PASS", save_copy=_redact(saved), inspection_after_reopen=_redact(reopened),
                         reopened_geometry_readbacks=reopened_numeric)
        persist()

        returned = call("nx_return", credentials)
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("nx_return did not return a safe HUMAN_OWNED session")
        credentials.clear()
        report["returned"] = _redact(returned)
        final_status = call("nx_bridge_status", {})
        report["final_status"] = _redact(final_status)
        try:
            in_flight = int(final_status.get("in_flight"))
        except (TypeError, ValueError):
            in_flight = -1
        if final_status.get("ownership_state") != "HUMAN_OWNED" or in_flight != 0 or final_status.get("writes_halted") is not False:
            raise AssertionError("final bridge status must be HUMAN_OWNED, in_flight=0, writes_halted=false")
        roundtrip["ownership_return"] = _redact(returned)
        roundtrip["final_status"] = _redact(final_status)
        report["passed"] = True
        return 0
    except Exception as exc:
        report["error"] = repr(exc)
        failed_case = active_case_holder[0]
        case_id = failed_case.get("case_id") if isinstance(failed_case, dict) else "F06_installed_harness"
        generic_retest = "All affected prepare/execute/verify checks pass with APPLIED execution, VERIFIED production checks, expected profile_analysis, numeric readback within stated tolerances, successful save/reopen checks, and safe ownership return."
        retest_criterion = (failed_case.get("retest_criterion")
                            if isinstance(failed_case, dict) and failed_case.get("retest_criterion")
                            else generic_retest)
        report["failure_record"] = {
            "case_id": case_id, "failure": repr(exc),
            "reproduction_steps": [
                "Use a fresh saved fixture in workspace/v2/models and ensure the dedicated NX session is idle.",
                "Rerun --part-path with the same fixture and a new evidence output path.",
                f"Repeat {case_id}; use the saved requests, responses, and numeric inspection values in this report.",
            ],
            "retest_criterion": retest_criterion,
        }
        if isinstance(failed_case, dict) and failed_case.get("state") == "RUNNING":
            failed_case.update(state="FAIL", failure=repr(exc),
                               reproduction_steps=report["failure_record"]["reproduction_steps"],
                               retest_criterion=retest_criterion)
        if credentials and client is not None:
            try:
                recovery_status = call("nx_bridge_status", {})
                report["failure_status"] = _redact(recovery_status)
                inflight = int(recovery_status.get("in_flight", -1))
                if (recovery_status.get("ownership_state") == "AI_OWNED"
                        and recovery_status.get("writes_halted") is False and inflight == 0):
                    report["failure_return"] = _redact(call("nx_return", credentials))
                    credentials.clear()
                else:
                    report["failure_return_skipped"] = "status is not a known idle AI_OWNED state; no recovery write was attempted"
            except Exception as return_error:
                report["failure_return_error"] = repr(return_error)
        return 1
    finally:
        report["completed_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        if client is not None:
            try:
                client.close()
            except Exception as close_error:
                report["client_close_error"] = repr(close_error)
        persist()
        summary = {key: report[key] for key in ("passed", "mode", "nx_pid", "saved_path", "error") if key in report}
        print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
