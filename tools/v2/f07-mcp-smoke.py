"""Installed MCP smoke for representative F07 sketch fillet and chamfer calls.

Model mode requires a unique, saved, clean, standalone millimeter .prt under
``workspace/v2/models``. It builds a four-line, right-angle fixture through the
installed MCP, prepares and edits a 2 mm fillet, edits a 4 mm / 30 degree
distance-angle chamfer, independently checks native geometry readback, saves
and reopens a new copy, and returns ownership when the bridge is in a known
idle state. It never starts NX and never saves over the supplied source part.

``--ready`` diagnoses the installed MCP version, tool set, and cached local
bridge status without taking ownership or invoking NX readiness/launch logic.
All checks are digital NX model-value checks; they do not establish physical
accuracy or population-wide reliability.
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

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "v2"))
from f01_f02_mcp_client import McpStdioClient  # noqa: E402

PROTOCOL_VERSION = "2025-11-25"
EXPECTED_SERVER_VERSION = "0.7.0"
EXPECTED_TOOL_COUNT = 23
POSITION_TOLERANCE_MM = 1.0e-6
LENGTH_TOLERANCE_MM = 1.0e-6
ANGLE_TOLERANCE_RAD = 1.0e-7
FRAME_TOLERANCE = 1.0e-7
OPERATION_TIMEOUT_SECONDS = 180.0
POLL_INTERVAL_SECONDS = 0.2
PRIVATE_KEYS = {
    "ownership_id", "new_ownership_id", "token", "authorization", "auth_token",
    "bridge_token", "secret",
}
CORNER_RESULT_FIELDS = {
    "plan_id", "kind", "sketch_id", "mutation_state", "verified",
    "verification", "evidence",
}

FILLET_INPUT = {
    "fillet-line-1": {"start_mm": [0.0, 0.0], "end_mm": [20.0, 0.0]},
    "fillet-line-2": {"start_mm": [20.0, 0.0], "end_mm": [20.0, 20.0]},
}
CHAMFER_INPUT = {
    "chamfer-line-1": {"start_mm": [40.0, 0.0], "end_mm": [60.0, 0.0]},
    "chamfer-line-2": {"start_mm": [60.0, 0.0], "end_mm": [60.0, 20.0]},
}
ALL_INPUT_LINES = {**FILLET_INPUT, **CHAMFER_INPUT}
CORNER = [20.0, 0.0, 0.0]
FILLET_TANGENT_1 = [18.0, 0.0, 0.0]
FILLET_TANGENT_2 = [20.0, 2.0, 0.0]
FILLET_CENTER = [18.0, 2.0, 0.0]
CHAMFER_CORNER = [60.0, 0.0, 0.0]
CHAMFER_TANGENT_1 = [56.0, 0.0, 0.0]
CHAMFER_TANGENT_2 = [60.0, 4.0 * math.sin(math.radians(30.0)) /
                     math.sin(math.radians(120.0)), 0.0]


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--ready", action="store_true",
                      help="inspect installed MCP and cached bridge status without ownership or NX calls")
    mode.add_argument("--part-path", type=Path,
                      help="unique saved clean .prt under workspace/v2/models; the active Work Part must match")
    parser.add_argument("--output", required=True, type=Path,
                        help="new JSON evidence path under workspace/v2/evidence")
    return parser


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = _build_parser()
    args = parser.parse_args(argv)
    if args.output.exists():
        parser.error("evidence output must be a new path; prior evidence is never overwritten")
    if args.output.suffix.lower() != ".json":
        parser.error("--output must have a .json extension")
    return args


def _redact(value: object) -> object:
    if isinstance(value, dict):
        return {key: ("[REDACTED]" if key.lower() in PRIVATE_KEYS else _redact(child))
                for key, child in value.items()}
    if isinstance(value, list):
        return [_redact(child) for child in value]
    return value


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
        except (TypeError, ValueError):
            return None
    return None


def _extract_payload(response: dict, tool: str) -> dict:
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


def _finite_vector(value: object, label: str, dimensions: tuple[int, ...] = (3,)) -> list[float]:
    if not isinstance(value, (list, tuple)) or len(value) not in dimensions:
        raise AssertionError(f"{label} must be a {dimensions}D finite vector")
    result = [float(component) for component in value]
    if not all(math.isfinite(component) for component in result):
        raise AssertionError(f"{label} contains a non-finite component")
    return result


def _distance(left: list[float], right: list[float]) -> float:
    if len(left) != len(right):
        raise AssertionError("point dimensions differ")
    return math.sqrt(sum((left[index] - right[index]) ** 2 for index in range(len(left))))


def _close(actual: float, expected: float, tolerance: float, label: str) -> None:
    if not math.isfinite(actual) or abs(actual - expected) > tolerance:
        raise AssertionError(f"{label}: expected {expected:.12g}, got {actual:.12g}; tolerance={tolerance:.3g}")


def _assert_point(actual: object, expected: list[float], label: str,
                  tolerance: float = POSITION_TOLERANCE_MM) -> list[float]:
    point = _finite_vector(actual, label)
    error = _distance(point, expected)
    if error > tolerance:
        raise AssertionError(f"{label} differs from expected by {error:.12g} mm; tolerance={tolerance:.3g} mm")
    return point


def _segment_matches(record: dict, first: list[float], second: list[float]) -> bool:
    try:
        start = _finite_vector(record.get("start"), "line start")
        end = _finite_vector(record.get("end"), "line end")
    except (AssertionError, TypeError, ValueError):
        return False
    return min(max(_distance(start, first), _distance(end, second)),
               max(_distance(start, second), _distance(end, first))) <= POSITION_TOLERANCE_MM


def _one_segment(geometry: list, first: list[float], second: list[float], label: str) -> dict:
    matches = [item for item in geometry
               if isinstance(item, dict) and item.get("kind") == "LINE"
               and _segment_matches(item, first, second)]
    if len(matches) != 1:
        raise AssertionError(f"{label} expected one uniquely matching native LINE, found {len(matches)}")
    record = matches[0]
    if not isinstance(record.get("journal_id"), str) or not record["journal_id"]:
        raise AssertionError(f"{label} omitted native journal_id")
    return record


def _line_world_points(spec: dict) -> tuple[list[float], list[float]]:
    start = spec["start_mm"]
    end = spec["end_mm"]
    return ([float(start[0]), float(start[1]), 0.0],
            [float(end[0]), float(end[1]), 0.0])


def _check_frame(sketch: dict, label: str) -> None:
    _assert_point(sketch.get("origin"), [0.0, 0.0, 0.0], label + " origin", FRAME_TOLERANCE)
    _assert_point(sketch.get("normal"), [0.0, 0.0, 1.0], label + " normal", FRAME_TOLERANCE)
    _assert_point(sketch.get("x_axis"), [1.0, 0.0, 0.0], label + " x_axis", FRAME_TOLERANCE)
    _assert_point(sketch.get("y_axis"), [0.0, 1.0, 0.0], label + " y_axis", FRAME_TOLERANCE)


def _check_initial_fixture(sketch: dict) -> dict[str, dict]:
    if str(sketch.get("units", "")).casefold() not in {"millimeters", "millimeter", "mm"}:
        raise AssertionError(f"F07 fixture sketch units must be millimeters, got {sketch.get('units')!r}")
    _check_frame(sketch, "initial F07 fixture sketch")
    geometry = sketch.get("geometry")
    if not isinstance(geometry, list):
        raise AssertionError("nx_inspect_sketch omitted geometry")
    lines = [item for item in geometry if isinstance(item, dict) and item.get("kind") == "LINE"]
    if len(lines) != 4:
        raise AssertionError(f"F07 starting sketch must contain four LINEs, got {len(lines)}")
    resolved: dict[str, dict] = {}
    for input_id, spec in ALL_INPUT_LINES.items():
        start, end = _line_world_points(spec)
        resolved[input_id] = _one_segment(geometry, start, end, "initial " + input_id)
    if len({item["journal_id"] for item in resolved.values()}) != 4:
        raise AssertionError("F07 fixture line IDs are not distinct")
    constraints = sketch.get("constraints")
    if not isinstance(constraints, list):
        raise AssertionError("nx_inspect_sketch omitted constraint inventory")
    if constraints:
        raise AssertionError(f"geometry_only F07 fixture unexpectedly contains constraints: {constraints!r}")
    return resolved


def _check_fillet_geometry(sketch: dict) -> dict:
    geometry = sketch.get("geometry")
    if not isinstance(geometry, list):
        raise AssertionError("fillet readback omitted geometry list")
    first_line = _one_segment(geometry, [0.0, 0.0, 0.0], FILLET_TANGENT_1, "fillet retained line 1")
    second_line = _one_segment(geometry, FILLET_TANGENT_2, [20.0, 20.0, 0.0], "fillet retained line 2")
    arcs = [item for item in geometry if isinstance(item, dict) and item.get("kind") == "ARC"]
    if len(arcs) != 1:
        raise AssertionError(f"fillet must add one native ARC, found {len(arcs)}")
    arc = arcs[0]
    center = _assert_point(arc.get("center"), FILLET_CENTER, "fillet arc center")
    radius = float(arc.get("radius_mm"))
    _close(radius, 2.0, LENGTH_TOLERANCE_MM, "fillet arc radius_mm")
    arc_start = _finite_vector(arc.get("start"), "fillet arc start")
    arc_end = _finite_vector(arc.get("end"), "fillet arc end")
    endpoint_pair_error = min(max(_distance(arc_start, FILLET_TANGENT_1), _distance(arc_end, FILLET_TANGENT_2)),
                              max(_distance(arc_start, FILLET_TANGENT_2), _distance(arc_end, FILLET_TANGENT_1)))
    if endpoint_pair_error > POSITION_TOLERANCE_MM:
        raise AssertionError(f"fillet arc endpoints miss expected tangent points by {endpoint_pair_error:.12g} mm")
    normal = _finite_vector(arc.get("normal"), "fillet arc normal")
    _assert_point(normal, [0.0, 0.0, 1.0], "fillet arc normal", FRAME_TOLERANCE)
    start_angle = float(arc.get("start_angle_rad"))
    end_angle = float(arc.get("end_angle_rad"))
    _close(abs(end_angle - start_angle), math.pi / 2.0, ANGLE_TOLERANCE_RAD,
           "fillet arc sweep magnitude")
    for name, angle_key in (("start", "start_angle_rad"), ("end", "end_angle_rad")):
        radial = [arc_start[i] - center[i] for i in range(3)] if name == "start" else [arc_end[i] - center[i] for i in range(3)]
        _close(math.sqrt(sum(component * component for component in radial)), radius,
               LENGTH_TOLERANCE_MM, "fillet " + name + " radial length")
        angle = float(arc.get(angle_key))
        if not math.isfinite(angle):
            raise AssertionError(f"fillet {angle_key} is not finite")
    line1_start = _finite_vector(first_line.get("start"), "fillet retained line 1 start")
    line1_end = _finite_vector(first_line.get("end"), "fillet retained line 1 end")
    line2_start = _finite_vector(second_line.get("start"), "fillet retained line 2 start")
    line2_end = _finite_vector(second_line.get("end"), "fillet retained line 2 end")
    _close(min(_distance(line1_start, FILLET_TANGENT_1), _distance(line1_end, FILLET_TANGENT_1)),
           0.0, POSITION_TOLERANCE_MM, "fillet line 1 tangent endpoint")
    _close(min(_distance(line2_start, FILLET_TANGENT_2), _distance(line2_end, FILLET_TANGENT_2)),
           0.0, POSITION_TOLERANCE_MM, "fillet line 2 tangent endpoint")

    def tangent_dot(line_start: list[float], line_end: list[float], arc_end: list[float], label: str) -> float:
        direction = [line_end[i] - line_start[i] for i in range(3)]
        tangent_point = arc_end
        radial_vector = [tangent_point[i] - center[i] for i in range(3)]
        line_norm = math.sqrt(sum(value * value for value in direction))
        radial_norm = math.sqrt(sum(value * value for value in radial_vector))
        dot = abs(sum(direction[i] * radial_vector[i] for i in range(3)) / (line_norm * radial_norm))
        if dot > ANGLE_TOLERANCE_RAD:
            raise AssertionError(f"{label} radial/line direction dot {dot:.12g} exceeds tangent tolerance")
        return dot

    # Match radial endpoints to their line tangencies independent of native arc orientation.
    tangent1 = arc_start if _distance(arc_start, FILLET_TANGENT_1) <= _distance(arc_end, FILLET_TANGENT_1) else arc_end
    tangent2 = arc_start if _distance(arc_start, FILLET_TANGENT_2) <= _distance(arc_end, FILLET_TANGENT_2) else arc_end
    dot1 = tangent_dot(line1_start, line1_end, tangent1, "fillet tangent 1")
    dot2 = tangent_dot(line2_start, line2_end, tangent2, "fillet tangent 2")
    return {
        "sketch_id": sketch.get("journal_id"),
        "fillet_arc_id": arc.get("journal_id"),
        "retained_line_ids": [first_line.get("journal_id"), second_line.get("journal_id")],
        "center_mm": center, "radius_mm": radius,
        "arc_sweep_rad": abs(end_angle - start_angle),
        "arc_endpoints_mm": [arc_start, arc_end],
        "trim_points_mm": [FILLET_TANGENT_1, FILLET_TANGENT_2],
        "tangent_radial_dot_abs": [dot1, dot2],
    }


def _angle_between(left: list[float], right: list[float]) -> float:
    left_norm = math.sqrt(sum(value * value for value in left))
    right_norm = math.sqrt(sum(value * value for value in right))
    if left_norm <= 0.0 or right_norm <= 0.0:
        raise AssertionError("cannot measure angle for a zero-length vector")
    cosine = sum(left[i] * right[i] for i in range(3)) / (left_norm * right_norm)
    return math.acos(max(-1.0, min(1.0, cosine)))


def _native_endpoint_near(record: dict, expected: list[float], label: str) -> list[float]:
    start = _finite_vector(record.get("start"), label + " start")
    end = _finite_vector(record.get("end"), label + " end")
    point = start if _distance(start, expected) <= _distance(end, expected) else end
    return _assert_point(point, expected, label)


def _check_chamfer_geometry(sketch: dict) -> dict:
    geometry = sketch.get("geometry")
    if not isinstance(geometry, list):
        raise AssertionError("chamfer readback omitted geometry list")
    line1 = _one_segment(geometry, [40.0, 0.0, 0.0], CHAMFER_TANGENT_1, "chamfer retained line 1")
    line2 = _one_segment(geometry, CHAMFER_TANGENT_2, [60.0, 20.0, 0.0], "chamfer retained line 2")
    chamfer_segment = _one_segment(geometry, CHAMFER_TANGENT_1, CHAMFER_TANGENT_2,
                                   "chamfer connector line")
    actual_a = _native_endpoint_near(line1, CHAMFER_TANGENT_1, "chamfer first cut endpoint A")
    actual_b = _native_endpoint_near(line2, CHAMFER_TANGENT_2, "chamfer second cut endpoint B")
    measured_distance1 = _distance(CHAMFER_CORNER, actual_a)
    measured_distance2 = _distance(CHAMFER_CORNER, actual_b)
    _close(measured_distance1, 4.0, LENGTH_TOLERANCE_MM, "chamfer measured distance1_mm")
    expected_distance2 = 4.0 * math.sin(math.radians(30.0)) / math.sin(math.radians(120.0))
    _close(measured_distance2, expected_distance2, LENGTH_TOLERANCE_MM, "chamfer measured derived distance2_mm")
    a_to_corner = [CHAMFER_CORNER[i] - actual_a[i] for i in range(3)]
    a_to_b = [actual_b[i] - actual_a[i] for i in range(3)]
    measured_angle_rad = _angle_between(a_to_corner, a_to_b)
    _close(measured_angle_rad, math.radians(30.0), ANGLE_TOLERANCE_RAD,
           "chamfer measured A→C to A→B angle")
    segment_start = _finite_vector(chamfer_segment.get("start"), "chamfer connector start")
    segment_end = _finite_vector(chamfer_segment.get("end"), "chamfer connector end")
    connector_endpoint_error = min(max(_distance(segment_start, actual_a), _distance(segment_end, actual_b)),
                                   max(_distance(segment_start, actual_b), _distance(segment_end, actual_a)))
    if connector_endpoint_error > POSITION_TOLERANCE_MM:
        raise AssertionError(f"chamfer connector endpoints differ from inspected cut points by {connector_endpoint_error:.12g} mm")
    connector_length = _distance(segment_start, segment_end)
    expected_connector_length = _distance(CHAMFER_TANGENT_1, CHAMFER_TANGENT_2)
    _close(connector_length, expected_connector_length, LENGTH_TOLERANCE_MM, "chamfer connector length")
    return {
        "sketch_id": sketch.get("journal_id"),
        "trimmed_line_ids": [line1.get("journal_id"), line2.get("journal_id")],
        "chamfer_segment_id": chamfer_segment.get("journal_id"),
        "trim_points_mm": [actual_a, actual_b],
        "requested_distance1_mm": 4.0,
        "derived_distance2_mm": expected_distance2,
        "measured_distance1_mm": measured_distance1,
        "measured_distance2_mm": measured_distance2,
        "requested_angle_deg": 30.0,
        "measured_angle_deg": math.degrees(measured_angle_rad),
        "connector_length_mm": connector_length,
    }


def _assert_distance_angle_evidence(result: dict) -> dict:
    verification_result = result.get("evidence")
    verification = result.get("verification")
    verification_evidence = verification.get("evidence") if isinstance(verification, dict) else None
    if not isinstance(verification_result, dict) or not isinstance(verification_evidence, dict):
        raise AssertionError("distance-angle chamfer result omitted its verification result envelope")
    for label, envelope in (("result", result), ("evidence", verification_result),
                            ("verification.evidence", verification_evidence)):
        if (envelope.get("kind") != "edit_sketch_corner" or envelope.get("operation") != "chamfer"
                or not isinstance(envelope.get("sketch_id"), str) or not envelope["sketch_id"]
                or envelope["sketch_id"] != result.get("sketch_id")
                or envelope.get("requested_definition") != "distance_angle"
                or envelope.get("native_mode") != "Asymmetric"):
            raise AssertionError(f"chamfer {label} envelope metadata does not match the requested edit")
    # F07 combined-edit evidence is the verification Result; its evidence field
    # contains the native proof. Follow this one defined level, without searching.
    evidence = verification_result.get("evidence")
    if not isinstance(evidence, dict) or verification_evidence.get("evidence") != evidence:
        raise AssertionError("distance-angle chamfer verification envelopes disagree or omit native proof")
    if result.get("requested_definition") != "distance_angle":
        raise AssertionError(f"chamfer requested_definition must be distance_angle, got {result.get('requested_definition')!r}")
    if result.get("native_mode") != "Asymmetric":
        raise AssertionError(f"chamfer native_mode must be Asymmetric, got {result.get('native_mode')!r}")
    if evidence.get("requested_definition") != "distance_angle" or evidence.get("native_mode") != "Asymmetric":
        raise AssertionError("chamfer evidence requested_definition/native_mode do not match the distance-angle mapping")

    measured = result.get("measured_geometry")
    if not isinstance(measured, dict):
        raise AssertionError("chamfer result omitted measured_geometry")
    expected = 4.0 * math.sin(math.radians(30.0)) / math.sin(math.radians(120.0))
    _assert_point(measured.get("curve1_cut_point_mm"), CHAMFER_TANGENT_1,
                  "production measured chamfer curve1 cut point")
    _assert_point(measured.get("curve2_cut_point_mm"), CHAMFER_TANGENT_2,
                  "production measured chamfer curve2 cut point")
    _close(float(measured.get("distance1_mm")), 4.0, LENGTH_TOLERANCE_MM,
           "production measured chamfer distance1_mm")
    _close(float(measured.get("distance2_mm")), expected, LENGTH_TOLERANCE_MM,
           "production measured chamfer distance2_mm")
    _close(float(measured.get("calculated_distance2_mm")), expected, LENGTH_TOLERANCE_MM,
           "production calculated chamfer distance2_mm")
    _close(float(measured.get("angle_deg")), 30.0, math.degrees(ANGLE_TOLERANCE_RAD),
           "production measured chamfer angle_deg")
    _close(float(measured.get("requested_angle_deg")), 30.0, 1.0e-12,
           "production requested chamfer angle_deg")

    native_operation = evidence.get("native_operation")
    if not isinstance(native_operation, dict):
        raise AssertionError("chamfer evidence omitted native_operation")
    if native_operation.get("native_operation_called") is not True:
        raise AssertionError("chamfer evidence does not prove the native builder was called")
    if native_operation.get("requested_definition") != "distance_angle" or native_operation.get("native_mode") != "Asymmetric":
        raise AssertionError("native chamfer operation evidence disagrees with the distance-angle to asymmetric mapping")
    required_native_fields = {
        "distance1_rhs_before_commit", "distance1_rhs_after_commit",
        "distance2_rhs_before_commit", "distance2_rhs_after_commit",
        "angle_rhs_before_commit", "angle_rhs_after_commit",
    }
    if not required_native_fields <= set(native_operation):
        raise AssertionError(f"native chamfer evidence omitted builder RHS fields: {sorted(required_native_fields - set(native_operation))!r}")

    def rhs_number(field: str) -> float:
        try:
            value = float(native_operation.get(field))
        except (TypeError, ValueError, OverflowError) as exc:
            raise AssertionError(f"native chamfer {field} is not a numeric RHS") from exc
        if not math.isfinite(value):
            raise AssertionError(f"native chamfer {field} is not finite")
        return value

    native_distance1 = [rhs_number("distance1_rhs_before_commit"), rhs_number("distance1_rhs_after_commit")]
    native_distance2 = [rhs_number("distance2_rhs_before_commit"), rhs_number("distance2_rhs_after_commit")]
    for value in native_distance1:
        _close(value, 4.0, LENGTH_TOLERANCE_MM, "native chamfer distance1 RHS")
    for value in native_distance2:
        _close(value, expected, LENGTH_TOLERANCE_MM, "native chamfer distance2 RHS")
    # Record angle RHS fields, but do not interpret them as an OffsetAndAngle input:
    # the accepted native mode is Asymmetric with the derived second distance.
    return {
        "requested_definition": result["requested_definition"],
        "native_mode": result["native_mode"],
        "measured_geometry": measured,
        "native_operation_called": native_operation["native_operation_called"],
        "native_distance1_rhs_mm": native_distance1,
        "native_distance2_rhs_mm": native_distance2,
        "native_angle_rhs_before_commit": native_operation.get("angle_rhs_before_commit"),
        "native_angle_rhs_after_commit": native_operation.get("angle_rhs_after_commit"),
    }


def _require_verified(value: dict, label: str) -> None:
    if value.get("verified") is not True:
        raise AssertionError(f"{label} did not return verified=true")
    checks = value.get("checks")
    if not isinstance(checks, dict) or not checks:
        raise AssertionError(f"{label} omitted production verification checks")
    bad = {name: check for name, check in checks.items()
           if not isinstance(check, dict) or check.get("status") != "VERIFIED"}
    if bad:
        raise AssertionError(f"{label} contains non-VERIFIED checks: {bad!r}")


def _status_state(record: dict) -> str | None:
    state = record.get("state")
    if isinstance(state, str):
        return state
    operation = record.get("operation")
    if isinstance(operation, dict) and isinstance(operation.get("state"), str):
        return operation["state"]
    return None


def _verified_from_operation(record: dict) -> bool | None:
    verified = record.get("verified")
    if isinstance(verified, bool):
        return verified
    result = record.get("result")
    if isinstance(result, dict) and isinstance(result.get("verified"), bool):
        return result["verified"]
    return None


def _int_value(value: object, label: str) -> int:
    if isinstance(value, bool):
        raise AssertionError(f"{label} must be an integer")
    try:
        return int(value)
    except (TypeError, ValueError, OverflowError) as exc:
        raise AssertionError(f"{label} must be an integer") from exc


def _file_metadata(path: Path) -> dict:
    stat = path.stat()
    return {
        "size_bytes": stat.st_size,
        "modified_time_utc": datetime.datetime.fromtimestamp(
            stat.st_mtime, datetime.timezone.utc).isoformat(),
    }


def _canonical_path(path: str | Path) -> str:
    return Path(path).resolve(strict=False).as_posix().casefold()


def _require_inspected_part_path(payload: dict, expected_path: str | Path, stage: str) -> str:
    part = payload.get("part")
    identity = payload.get("identity")
    part_path = part.get("path") if isinstance(part, dict) else None
    identity_path = identity.get("part_path") if isinstance(identity, dict) else None
    if (not isinstance(part_path, str) or not part_path.strip()
            or not isinstance(identity_path, str) or not identity_path.strip()):
        raise AssertionError(f"{stage} nx_inspect_part omitted part.path or identity.part_path")
    if _canonical_path(part_path) != _canonical_path(identity_path):
        raise AssertionError(f"{stage} nx_inspect_part part.path and identity.part_path differ")
    if _canonical_path(part_path) != _canonical_path(expected_path):
        raise AssertionError(f"{stage} nx_inspect_part returned an unexpected part path")
    return part_path


def _validate_paths(args: argparse.Namespace, report: dict) -> tuple[Path | None, Path]:
    evidence_root = (ROOT / "workspace" / "v2" / "evidence").resolve(strict=False)
    output_path = args.output.resolve(strict=False)
    if not output_path.is_relative_to(evidence_root):
        raise ValueError("--output must be a new JSON path under workspace/v2/evidence")
    if output_path.exists():
        raise FileExistsError("evidence output path already exists; refusing to overwrite it")
    report["evidence_path"] = str(output_path)
    if args.ready:
        return None, output_path

    model_root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
    part_path = args.part_path.resolve(strict=True)
    if not part_path.is_file() or part_path.suffix.lower() != ".prt" or not part_path.is_relative_to(model_root):
        raise ValueError("--part-path must be an existing saved .prt file inside workspace/v2/models")
    if _canonical_path(part_path) == _canonical_path(output_path):
        raise ValueError("part path and evidence path must be different")
    report["part_path"] = str(part_path)
    report["source_part_file"] = {"before_save_copy": _file_metadata(part_path)}
    return part_path, output_path


def _validate_corner_schemas(tools: list) -> dict:
    by_name = {item.get("name"): item for item in tools if isinstance(item, dict)}
    common = {
        "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
        "kind", "curve1_id", "curve2_id", "curve1_end", "curve2_end",
    }
    found = {}
    for name in ("nx_prepare_sketch_corner", "nx_edit_sketch_corner"):
        tool = by_name.get(name)
        if not isinstance(tool, dict):
            raise AssertionError(f"installed tools/list omitted {name}")
        schema = tool.get("inputSchema")
        if not isinstance(schema, dict):
            raise AssertionError(f"installed {name} omitted inputSchema")
        properties = schema.get("properties")
        required = set(schema.get("required", []))
        if not isinstance(properties, dict) or not common <= set(properties) or not common <= required:
            raise AssertionError(f"installed {name} schema omitted common explicit-corner fields")
        if properties.get("kind", {}).get("enum") != ["fillet", "chamfer"]:
            raise AssertionError(f"installed {name} schema has an unexpected kind enum")
        if "radius_mm" not in properties or "distance1_mm" not in properties:
            raise AssertionError(f"installed {name} schema omitted radius or chamfer distance fields")
        if "distance2_mm" not in properties or "angle_deg" not in properties:
            raise AssertionError(f"installed {name} schema omitted a chamfer mode field")
        if schema.get("additionalProperties") is not False:
            raise AssertionError(f"installed {name} must reject unknown fields")
        found[name] = schema
    return found


def _tool_names_and_version(client: McpStdioClient, report: dict) -> tuple[list, dict]:
    initialize = client.request("initialize", {
        "protocolVersion": PROTOCOL_VERSION,
        "capabilities": {},
        "clientInfo": {"name": "f07-installed-mcp-smoke", "version": "1"},
    })
    report["initialize"] = _redact(initialize)
    client.notify("notifications/initialized")
    server_info = initialize.get("serverInfo")
    if not isinstance(server_info, dict) or server_info.get("version") != EXPECTED_SERVER_VERSION:
        raise AssertionError(f"installed MCP version must be {EXPECTED_SERVER_VERSION}: {server_info!r}")
    report["server_version"] = server_info["version"]
    listed = client.request("tools/list")
    tools = listed.get("tools")
    if not isinstance(tools, list):
        raise AssertionError("MCP tools/list omitted its tools array")
    names = [item.get("name") for item in tools if isinstance(item, dict)]
    if len(names) != EXPECTED_TOOL_COUNT or len(set(names)) != EXPECTED_TOOL_COUNT:
        raise AssertionError(f"installed MCP must expose exactly {EXPECTED_TOOL_COUNT} unique tools; got {len(set(names))}")
    required = {
        "nx_bridge_status", "nx_takeover", "nx_return", "nx_get_session", "nx_inspect_part",
        "nx_inspect_sketch", "nx_get_operation", "nx_prepare_sketch", "nx_prepare_sketch_corner",
        "nx_edit_sketch_corner", "nx_execute_plan", "nx_verify_part", "nx_save_copy",
    }
    if not required <= set(names):
        raise AssertionError(f"installed MCP omitted required tools: {sorted(required - set(names))!r}")
    schemas = _validate_corner_schemas(tools)
    report["tool_count"] = len(names)
    report["tool_names"] = sorted(names)
    report["corner_tool_schemas"] = _redact(schemas)
    report["tools_list"] = _redact(tools)
    return tools, schemas


def _verify_corner_status(status: dict, operation_id: str, kind: str, sketch_id: str) -> dict:
    state = _status_state(status)
    operation = status.get("operation") if isinstance(status.get("operation"), dict) else {}
    if not operation:
        operation = status
    result = operation.get("result") if isinstance(operation.get("result"), dict) else None
    if result is None and isinstance(status.get("result"), dict):
        result = status["result"]
    if result is None and CORNER_RESULT_FIELDS <= set(status):
        result = status
    if not isinstance(result, dict):
        raise AssertionError(f"nx_edit_sketch_corner {operation_id} omitted its result object")
    missing = CORNER_RESULT_FIELDS - set(result)
    if missing:
        raise AssertionError(f"nx_edit_sketch_corner result omitted fields: {sorted(missing)!r}")
    if state != "SUCCEEDED" or status.get("success") is not True:
        error = status.get("error") or operation.get("error")
        raise AssertionError(f"nx_edit_sketch_corner must be SUCCEEDED/success=true, got {state!r}: {error!r}")
    if status.get("verified") is not True or result.get("verified") is not True:
        raise AssertionError("nx_edit_sketch_corner SUCCEEDED only counts when verified=true")
    if result.get("mutation_state") != "APPLIED":
        raise AssertionError(f"nx_edit_sketch_corner mutation_state must be APPLIED, got {result.get('mutation_state')!r}")
    if result.get("kind") not in {kind, "edit_sketch_corner"}:
        raise AssertionError(f"nx_edit_sketch_corner result kind does not match request {kind!r}")
    if result.get("sketch_id") != sketch_id:
        raise AssertionError("nx_edit_sketch_corner result sketch_id differs from selected sketch")
    if not isinstance(result.get("plan_id"), str) or not result["plan_id"]:
        raise AssertionError("nx_edit_sketch_corner result omitted plan_id")
    if not isinstance(result.get("verification"), dict):
        raise AssertionError("nx_edit_sketch_corner result omitted verification object")
    if not isinstance(result.get("evidence"), (dict, list)):
        raise AssertionError("nx_edit_sketch_corner result omitted evidence object")
    return result


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    run_id = uuid.uuid4().hex
    mode = "ready" if args.ready else "part"
    report: dict = {
        "test": "F07_installed_mcp_smoke",
        "run_id": run_id,
        "mode": mode,
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "passed": False,
        "objective": "Verify installed F07 explicit sketch-corner preparation/edit calls, native geometry values, save/reopen, and safe ownership return.",
        "traceability": [
            "F07 explicit curve and endpoint selection",
            "F07 right-angle 2 mm fillet and tangent points",
            "F07 right-angle distance-angle chamfer (4 mm, 30 deg)",
            "F07 installed MCP version/tool count",
            "F07 save/reopen and ownership return",
        ],
        "test_class": "installed MCP integration and NX model subsystem validation",
        "configuration": {
            "expected_mcp_version": EXPECTED_SERVER_VERSION,
            "expected_tool_count": EXPECTED_TOOL_COUNT,
            "position_tolerance_mm": POSITION_TOLERANCE_MM,
            "length_tolerance_mm": LENGTH_TOLERANCE_MM,
            "angle_tolerance_rad": ANGLE_TOLERANCE_RAD,
            "frame_component_tolerance": FRAME_TOLERANCE,
            "sample_size": 1,
            "measurement_uncertainty": "not applicable; independent calculations use digital NX model values, no physical instrument",
            "reliability_or_compliance_inference": False,
        },
        "safety_controls": {
            "NX_launch": "not invoked; this harness does not call nx_ensure_ready",
            "ownership_scope": "saved_independent_copy only; caller supplies the unique .prt path",
            "path_and_dirty_gate": "nx_takeover atomically checks active Work Part path and IsModified before granting AI_OWNED; the returned identity is checked before any model write",
            "source_part_save": "never saves over supplied path; roundtrip uses a new unique Save Copy path",
            "unknown_edit_outcome": "query nx_get_operation only; never replay the corner edit; do not return ownership if writes are frozen",
        },
        "instrumentation": "NX native geometry and feature inspection through installed MCP, plus independent Python geometry calculations",
        "calibration": "no physical instrument; CAD model comparison tolerances are recorded above",
        "calls": [], "cases": [],
    }
    client: McpStdioClient | None = None
    process: subprocess.Popen[bytes] | None = None
    credentials: dict[str, object] = {}
    active_case: dict | None = None
    part_path: Path | None = None
    output_path: Path | None = None
    target_part_path: Path | None = None
    revision_id: str | None = None
    model_root = (ROOT / "workspace" / "v2" / "models").resolve(strict=False)

    def persist() -> None:
        if output_path is None:
            return
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(json.dumps(_redact(report), ensure_ascii=False,
                                          allow_nan=False, indent=2) + "\n", encoding="utf-8")

    def call_raw(name: str, arguments: dict | None = None, *, allow_tool_error: bool = False) -> tuple[dict, dict]:
        if client is None:
            raise RuntimeError("installed MCP client is not initialized")
        request_arguments = arguments or {}
        entry = {"tool": name, "arguments": _redact(request_arguments)}
        report["calls"].append(entry)
        try:
            response = client.request("tools/call", {"name": name, "arguments": request_arguments})
        except Exception as exc:
            entry["transport_error"] = repr(exc)
            persist()
            raise
        entry["mcp_response"] = _redact(response)
        payload = _extract_payload(response, name)
        entry["payload"] = _redact(payload)
        entry["is_error"] = response.get("isError") is True
        persist()
        if response.get("isError") is True and not allow_tool_error:
            raise RuntimeError(f"{name} returned MCP tool error {_error_code(response) or _error_code(payload) or 'UNKNOWN'}")
        return payload, entry

    def wait_for_operation(initial: dict, label: str) -> dict:
        operation_id = initial.get("operation_id")
        if not isinstance(operation_id, str) or not operation_id:
            raise AssertionError(f"{label} operation response omitted operation_id")
        current = initial
        deadline = time.monotonic() + OPERATION_TIMEOUT_SECONDS
        while _status_state(current) not in {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}:
            if time.monotonic() >= deadline:
                raise TimeoutError(f"{label} operation {operation_id} did not reach a terminal state")
            time.sleep(POLL_INTERVAL_SECONDS)
            current, poll_entry = call_raw("nx_get_operation", {"operation_id": operation_id},
                                           allow_tool_error=True)
            poll_entry["poll_for_operation_id"] = operation_id
        return current

    def call_operation(name: str, arguments: dict) -> dict:
        initial, entry = call_raw(name, arguments)
        if not isinstance(initial.get("operation_id"), str):
            return initial
        completed = wait_for_operation(initial, name)
        entry["completed"] = _redact(completed)
        state = _status_state(completed)
        if state == "UNKNOWN":
            # Collect one read-only status snapshot for reproduction. Never
            # submit the operation again after an uncertain outcome.
            unknown_id = completed.get("operation_id") or initial.get("operation_id")
            if isinstance(unknown_id, str) and unknown_id:
                query, query_entry = call_raw("nx_get_operation", {"operation_id": unknown_id},
                                              allow_tool_error=True)
                query_entry["unknown_outcome_query_for"] = unknown_id
                entry["unknown_outcome_query"] = _redact(query)
                completed = query
                state = _status_state(completed)
                entry["completed_after_unknown_query"] = _redact(completed)
        if state != "SUCCEEDED":
            raise RuntimeError(f"{name} operation ended in {state or 'UNKNOWN'}: {completed.get('error')!r}")
        result = completed.get("result")
        if not isinstance(result, dict):
            raise AssertionError(f"{name} operation SUCCEEDED without a result object")
        persist()
        return result

    def record_case(case_id: str, request: dict, retest_criterion: str) -> dict:
        nonlocal active_case
        case = {"case_id": case_id, "state": "RUNNING", "request": _redact(request),
                "reproduction_steps": [
                    "Use a new clean, saved .prt under workspace/v2/models and open it as NX Work Part.",
                    "Run this script with that exact --part-path and a new --output evidence path.",
                    f"Repeat {case_id} using the recorded call arguments, native inspection, and expected coordinates.",
                ],
                "retest_criterion": retest_criterion}
        report["cases"].append(case)
        active_case = case
        persist()
        return case

    def update_revision(label: str) -> dict:
        nonlocal revision_id
        if not credentials:
            raise RuntimeError("cannot read revision without ownership credentials")
        session = call_raw("nx_get_session", credentials)[0]
        current_path = session.get("part_path")
        if not isinstance(current_path, str) or _canonical_path(current_path) != _canonical_path(target_part_path):
            raise AssertionError(f"{label} nx_get_session path differs from current owned Work Part")
        current_revision = session.get("revision_id")
        if not isinstance(current_revision, str) or not current_revision:
            raise AssertionError(f"{label} nx_get_session omitted revision_id")
        revision_id = current_revision
        report.setdefault("revision_observations", []).append({
            "stage": label, "revision_id": current_revision, "part_path": current_path,
            "is_modified_before_read": session.get("is_modified_before_read"),
            "is_modified_after_read": session.get("is_modified_after_read"),
            "dirty_changed_by_read": session.get("dirty_changed_by_read"),
        })
        persist()
        return session

    def inspect_sketch(sketch_id: str, label: str) -> dict:
        response = call_raw("nx_inspect_sketch", {**credentials, "sketch_id": sketch_id})[0]
        sketch = response.get("sketch") if isinstance(response.get("sketch"), dict) else response
        if not isinstance(sketch, dict) or not isinstance(sketch.get("geometry"), list):
            raise AssertionError(f"{label} nx_inspect_sketch omitted geometry")
        if sketch.get("journal_id") != sketch_id:
            raise AssertionError(f"{label} inspection returned a different sketch journal_id")
        return sketch

    def resolve_current_sketch(stage: str) -> dict:
        part = call_raw("nx_inspect_part", credentials)[0]
        _require_inspected_part_path(part, target_part_path, stage)
        sketches = part.get("sketches")
        if not isinstance(sketches, list) or len(sketches) != 1:
            raise AssertionError(f"{stage} expected exactly one F07 test sketch")
        sketch_id = sketches[0].get("journal_id") if isinstance(sketches[0], dict) else None
        if not isinstance(sketch_id, str) or not sketch_id:
            raise AssertionError(f"{stage} sketch omitted journal_id")
        return inspect_sketch(sketch_id, stage)

    def prepare_corner(kind: str, sketch: dict, curves: dict[str, dict], request_values: dict) -> dict:
        operation_id = "f07-corner-prepare-" + uuid.uuid4().hex
        arguments = {
            "operation_id": operation_id, **credentials,
            "revision_id": revision_id, "sketch_id": sketch["journal_id"], "kind": kind,
            "curve1_id": curves["curve1"]["journal_id"], "curve2_id": curves["curve2"]["journal_id"],
            "curve1_end": "end", "curve2_end": "start", **request_values,
        }
        plan_result = call_operation("nx_prepare_sketch_corner", arguments)
        plan = plan_result.get("plan")
        if not isinstance(plan, dict) or not isinstance(plan.get("plan_id"), str) or not plan["plan_id"]:
            raise AssertionError("nx_prepare_sketch_corner omitted plan.plan_id")
        if plan.get("sketch_id") not in (None, sketch["journal_id"]):
            raise AssertionError("prepared sketch-corner plan targets a different sketch")
        return {"arguments": arguments, "result": plan_result, "plan": plan}

    def edit_corner(kind: str, sketch: dict, curves: dict[str, dict], request_values: dict) -> tuple[dict, dict]:
        operation_id = "f07-corner-edit-" + uuid.uuid4().hex
        arguments = {
            "operation_id": operation_id, **credentials,
            "revision_id": revision_id, "sketch_id": sketch["journal_id"], "kind": kind,
            "curve1_id": curves["curve1"]["journal_id"], "curve2_id": curves["curve2"]["journal_id"],
            "curve1_end": "end", "curve2_end": "start", **request_values,
        }
        status, entry = call_raw("nx_edit_sketch_corner", arguments, allow_tool_error=True)
        state = _status_state(status)
        operation_id_from_status = status.get("operation_id") or operation_id
        operation = status.get("operation") if isinstance(status.get("operation"), dict) else None
        if state in {"QUEUED", "RUNNING"}:
            current = dict(operation) if isinstance(operation, dict) else dict(status)
            current.setdefault("operation_id", operation_id_from_status)
            current = wait_for_operation(current, "nx_edit_sketch_corner")
            status = {**status, "state": current.get("state"), "operation": current,
                      "verified": _verified_from_operation(current),
                      "success": current.get("state") == "SUCCEEDED" and _verified_from_operation(current) is True}
            entry["completed_operation"] = _redact(current)
        elif state == "UNKNOWN" or status.get("outcome_unknown") is True:
            # A single read-only query is permitted to resolve the operation record.
            # This never resubmits or retries nx_edit_sketch_corner.
            current, query_entry = call_raw("nx_get_operation", {"operation_id": operation_id_from_status},
                                            allow_tool_error=True)
            query_entry["unknown_outcome_query_for"] = operation_id_from_status
            entry["unknown_outcome_query"] = _redact(current)
            if _status_state(current) in {"SUCCEEDED", "FAILED", "CANCELLED"}:
                status = {**status, "state": current.get("state"), "operation": current,
                          "verified": _verified_from_operation(current),
                          "success": current.get("state") == "SUCCEEDED" and
                                    _verified_from_operation(current) is True}
            else:
                status = {**status, "state": "UNKNOWN", "operation": current,
                          "outcome_unknown": True}
        result = _verify_corner_status(status, operation_id, kind, sketch["journal_id"])
        entry["validated_result"] = _redact(result)
        persist()
        return result, status

    def return_ownership() -> dict:
        if not credentials:
            return {"skipped": "no current ownership credentials"}
        status = call_raw("nx_bridge_status", {})[0]
        report["status_before_return"] = _redact(status)
        in_flight = _int_value(status.get("in_flight", -1), "bridge in_flight")
        if (status.get("ownership_state") != "AI_OWNED" or status.get("writes_halted") is not False
                or in_flight != 0):
            result = {"skipped": "bridge is not a known idle, unhalted AI_OWNED state",
                      "ownership_state": status.get("ownership_state"),
                      "writes_halted": status.get("writes_halted"), "in_flight": in_flight}
            report["ownership_return"] = result
            persist()
            return result
        returned = call_raw("nx_return", credentials)[0]
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError(f"nx_return did not confirm safe HUMAN_OWNED state: {returned!r}")
        credentials.clear()
        final = call_raw("nx_bridge_status", {})[0]
        final_in_flight = _int_value(final.get("in_flight", -1), "final bridge in_flight")
        if (final.get("ownership_state") != "HUMAN_OWNED" or final.get("writes_halted") is not False
                or final_in_flight != 0):
            raise AssertionError(f"final bridge status is not safe for human ownership: {final!r}")
        result = {"return": _redact(returned), "final_status": _redact(final)}
        report["ownership_return"] = result
        persist()
        return result

    try:
        # Retain a usable evidence destination even if a later model-path
        # precondition fails, while refusing to write outside the evidence tree.
        evidence_candidate = args.output.resolve(strict=False)
        evidence_root = (ROOT / "workspace" / "v2" / "evidence").resolve(strict=False)
        if evidence_candidate.is_relative_to(evidence_root) and not evidence_candidate.exists():
            output_path = evidence_candidate
        part_path, output_path = _validate_paths(args, report)
        install = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy()
        env.update(PYTHONPATH=str(install / "app" / "src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen(
            [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install / "mcp-config.json")],
            cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, bufsize=0,
        )
        client = McpStdioClient(process, 60)
        tools, _ = _tool_names_and_version(client, report)

        # Cached bridge status uses local IPC only and does not launch or inspect NX.
        bridge_status = call_raw("nx_bridge_status", {})[0]
        report["initial_status"] = _redact(bridge_status)
        if bridge_status.get("state") != "loaded":
            raise AssertionError(f"installed NX bridge is not loaded: {bridge_status.get('state')!r}")
        if bridge_status.get("writes_halted") is not False:
            raise AssertionError(f"bridge write gate is halted: {bridge_status.get('write_halt_reason')!r}")
        try:
            in_flight = _int_value(bridge_status.get("in_flight"), "initial bridge in_flight")
        except AssertionError:
            in_flight = _int_value(bridge_status.get("active_operations"), "initial bridge active_operations")

        if args.ready:
            if bridge_status.get("ownership_state") != "HUMAN_OWNED" or in_flight != 0:
                raise AssertionError("--ready requires idle HUMAN_OWNED bridge status")
            report.update(passed=True, diagnostic_only=True,
                          ready_result="installed MCP and cached local bridge status verified; no NX API, launch, or ownership call")
            return 0

        if bridge_status.get("ownership_state") != "HUMAN_OWNED" or in_flight != 0:
            raise AssertionError("model mode requires an idle HUMAN_OWNED NX session before takeover")
        nx_pid = _int_value(bridge_status.get("pid"), "bridge NX pid")
        if part_path is None:
            raise AssertionError("model mode requires --part-path")
        target_part_path = part_path
        report["nx_pid"] = nx_pid

        case = record_case("F07_saved_copy_takeover_gate", {
            "expected_pid": nx_pid, "expected_part_path": str(part_path),
            "artifact_scope": "saved_independent_copy", "authorize": True,
            "required_preconditions": ["path matches active Work Part", "saved .prt exists", "IsModified=false"],
        }, "The NX adapter compares the active Work Part path with the supplied saved copy and confirms IsModified=false before AI_OWNED is granted; the returned identity and session path remain exact and clean.")
        active_case = case
        takeover = call_raw("nx_takeover", {
            "expected_pid": nx_pid, "expected_part_path": str(part_path),
            "artifact_scope": "saved_independent_copy", "authorize": True,
        })[0]
        owner = takeover.get("ownership_id")
        epoch = takeover.get("binding_epoch")
        if isinstance(owner, str) and owner and isinstance(epoch, int) and not isinstance(epoch, bool):
            credentials.update(ownership_id=owner, binding_epoch=epoch)
        if not credentials:
            raise AssertionError("nx_takeover omitted ownership credentials")
        if takeover.get("ownership_state") != "AI_OWNED":
            raise AssertionError("nx_takeover did not establish AI_OWNED state")
        if _canonical_path(takeover.get("part_path", "")) != _canonical_path(part_path):
            raise AssertionError("nx_takeover identity does not match supplied Work Part path")
        if takeover.get("is_modified") is not False or takeover.get("is_modified_before_read") is not False:
            raise AssertionError("nx_takeover did not prove a clean Work Part before granting ownership")
        if takeover.get("is_modified_after_read") is not False or takeover.get("dirty_changed_by_read") is not False:
            raise AssertionError("NX takeover inspection changed or could not confirm Work Part dirty state")
        if takeover.get("artifact_scope") != "saved_independent_copy" or takeover.get("artifact_kind") != "standalone_part":
            raise AssertionError("nx_takeover did not confirm saved standalone part scope")
        report["takeover_gate_evidence"] = _redact(takeover)
        case.update(state="PASS", path_match=True, is_modified_before_read=False,
                    is_modified_after_read=False, dirty_changed_by_read=False)
        persist()
        session = update_revision("after takeover")
        if _canonical_path(session.get("part_path", "")) != _canonical_path(part_path):
            raise AssertionError("post-takeover session path differs from supplied part")
        if (session.get("is_modified_before_read") is not False
                or session.get("is_modified_after_read") is not False
                or session.get("dirty_changed_by_read") is not False):
            raise AssertionError("post-takeover session did not confirm a clean Work Part before the first model write")
        if str(session.get("part_units", "")).casefold() not in {"millimeters", "millimeter", "mm"}:
            raise AssertionError(f"supplied Work Part must use millimeters; got {session.get('part_units')!r}")
        report["configuration"].update({
            "nx_build": takeover.get("nx_build"),
            "nxopen_assembly_version": takeover.get("nxopen_assembly_version"),
            "nxopen_file_version": takeover.get("nxopen_file_version"),
            "part_units": session.get("part_units"),
            "sample_size": 1,
        })
        initial_part = call_raw("nx_inspect_part", credentials)[0]
        _require_inspected_part_path(initial_part, part_path, "initial blank fixture")
        initial_part_record = initial_part.get("part") if isinstance(initial_part.get("part"), dict) else {}
        initial_read_effects = initial_part.get("read_effects") if isinstance(initial_part.get("read_effects"), dict) else {}
        if (initial_part_record.get("is_modified") is not False
                or initial_read_effects.get("is_modified_before_read") is not False
                or initial_read_effects.get("is_modified_after_read") is not False
                or initial_read_effects.get("dirty_changed_by_read") is not False):
            raise AssertionError("initial part inspection did not preserve a clean Work Part before the first model write")
        existing_sketches = initial_part.get("sketches")
        existing_bodies = initial_part.get("bodies")
        if not isinstance(existing_sketches, list) or existing_sketches:
            raise AssertionError("F07 smoke requires a clean blank saved part with no existing sketches")
        if isinstance(existing_bodies, list) and existing_bodies:
            raise AssertionError("F07 smoke requires a blank saved part with no existing solid bodies")
        report["initial_part_inspection"] = _redact(initial_part)

        sketch_case = record_case("F07_create_right_angle_fixture", {
            "plane": "XY", "origin_mm": [0.0, 0.0, 0.0],
            "geometries": [{"id": key, "type": "line", **value} for key, value in ALL_INPUT_LINES.items()],
        }, "nx_prepare_sketch and nx_execute_plan return APPLIED, nx_verify_part is VERIFIED, and direct sketch inspection uniquely matches all four lines within 1e-6 mm.")
        geometry_requests = [
            {"id": key, "type": "line", "start_mm": value["start_mm"], "end_mm": value["end_mm"]}
            for key, value in ALL_INPUT_LINES.items()
        ]
        prepared_sketch = call_operation("nx_prepare_sketch", {
            "operation_id": "f07-sketch-prepare-" + uuid.uuid4().hex,
            **credentials, "revision_id": revision_id,
            "plane": "XY", "origin_mm": [0.0, 0.0, 0.0],
            "constraint_mode": "geometry_only", "geometries": geometry_requests,
        })
        sketch_plan = prepared_sketch.get("plan")
        if not isinstance(sketch_plan, dict) or not isinstance(sketch_plan.get("plan_id"), str):
            raise AssertionError("nx_prepare_sketch omitted plan.plan_id")
        sketch_case["prepare_result"] = _redact(prepared_sketch)
        fixture_execute_id = "f07-sketch-execute-" + uuid.uuid4().hex
        fixture_executed = call_operation("nx_execute_plan", {
            "operation_id": fixture_execute_id, **credentials, "plan_id": sketch_plan["plan_id"],
        })
        if fixture_executed.get("mutation_state") != "APPLIED":
            raise AssertionError("four-line fixture sketch did not return mutation_state=APPLIED")
        sketch_case["execute_result"] = _redact(fixture_executed)
        fixture_verified = call_operation("nx_verify_part", {
            "operation_id": "f07-sketch-verify-" + uuid.uuid4().hex,
            **credentials, "execute_operation_id": fixture_execute_id,
        })
        _require_verified(fixture_verified, "F07 fixture sketch verification")
        sketch_case["verification"] = _redact(fixture_verified)
        session = update_revision("after fixture creation")
        sketch_id = fixture_executed.get("sketch_id") or fixture_verified.get("sketch_id")
        if not isinstance(sketch_id, str) or not sketch_id:
            raise AssertionError("F07 fixture creation omitted sketch_id")
        initial_sketch = inspect_sketch(sketch_id, "initial F07 fixture")
        initial_curves = _check_initial_fixture(initial_sketch)
        sketch_case.update(state="PASS", sketch_id=sketch_id,
                           independently_resolved_curve_ids={key: value["journal_id"] for key, value in initial_curves.items()},
                           initial_native_readback=_redact(initial_sketch), revision_id=session.get("revision_id"))
        persist()

        # Prepare is explicitly exercised as a read-only preview; confirm the
        # revision and native endpoints stayed fixed before the combined edit.
        fillet_sketch = resolve_current_sketch("before fillet prepare")
        fillet_revision_before_prepare = update_revision("before fillet preview prepare").get("revision_id")
        fillet_curves = {
            "curve1": _one_segment(fillet_sketch["geometry"], [0.0, 0.0, 0.0], [20.0, 0.0, 0.0], "fillet curve1"),
            "curve2": _one_segment(fillet_sketch["geometry"], [20.0, 0.0, 0.0], [20.0, 20.0, 0.0], "fillet curve2"),
        }
        if not _segment_matches(fillet_curves["curve1"], [0, 0, 0], CORNER) or not _segment_matches(
                fillet_curves["curve2"], CORNER, [20, 20, 0]):
            raise AssertionError("explicit fillet selection does not use curve1=end and curve2=start at the same corner")
        corner_prepare_case = record_case("F07_prepare_fillet_preview", {
            "kind": "fillet", "curve1_end": "end", "curve2_end": "start", "radius_mm": 2.0,
            "curve1_id": fillet_curves["curve1"]["journal_id"],
            "curve2_id": fillet_curves["curve2"]["journal_id"],
        }, "nx_prepare_sketch_corner returns a plan for the selected sketch and leaves the revision and inspected native endpoints unchanged.")
        prepared_corner = prepare_corner("fillet", fillet_sketch, fillet_curves, {"radius_mm": 2.0})
        corner_prepare_case["prepare"] = _redact(prepared_corner)
        after_prepare_revision = update_revision("after fillet preview prepare")
        if after_prepare_revision.get("revision_id") != fillet_revision_before_prepare:
            raise AssertionError("read-only F07 fillet prepare changed the model revision")
        after_preview_sketch = resolve_current_sketch("after fillet prepare")
        _check_initial_fixture(after_preview_sketch)
        corner_prepare_case.update(state="PASS", revision_unchanged=True,
                                   native_geometry_after_prepare=_redact(after_preview_sketch))
        persist()

        fillet_case = record_case("F07_combined_right_angle_fillet", {
            "kind": "fillet", "curve1_end": "end", "curve2_end": "start", "radius_mm": 2.0,
            "curve1_id": fillet_curves["curve1"]["journal_id"],
            "curve2_id": fillet_curves["curve2"]["journal_id"],
            "expected_tangent_points_mm": [FILLET_TANGENT_1, FILLET_TANGENT_2],
            "expected_center_mm": FILLET_CENTER,
        }, "nx_edit_sketch_corner returns SUCCEEDED, mutation_state=APPLIED, verified=true; independently inspected trimmed endpoints, 2 mm radius, arc endpoints, and tangent continuity match within recorded tolerances.")
        fillet_sketch = resolve_current_sketch("before fillet edit")
        update_revision("before combined fillet edit")
        fillet_curves = {
            "curve1": _one_segment(fillet_sketch["geometry"], [0.0, 0.0, 0.0], [20.0, 0.0, 0.0], "fillet curve1 before edit"),
            "curve2": _one_segment(fillet_sketch["geometry"], [20.0, 0.0, 0.0], [20.0, 20.0, 0.0], "fillet curve2 before edit"),
        }
        fillet_case["request"].update({
            "curve1_id": fillet_curves["curve1"]["journal_id"],
            "curve2_id": fillet_curves["curve2"]["journal_id"],
        })
        result_fillet, status_fillet = edit_corner("fillet", fillet_sketch, fillet_curves, {"radius_mm": 2.0})
        fillet_case["combined_edit"] = _redact(status_fillet)
        fillet_case["operation_result"] = _redact(result_fillet)
        session = update_revision("after fillet edit")
        fillet_readback = resolve_current_sketch("independent fillet geometry readback")
        fillet_numeric = _check_fillet_geometry(fillet_readback)
        fillet_case.update(state="PASS", independent_geometry_readback=_redact(fillet_readback),
                           independent_numeric_check=fillet_numeric, revision_id=session.get("revision_id"))
        persist()

        chamfer_sketch = resolve_current_sketch("before chamfer edit")
        update_revision("before combined chamfer edit")
        chamfer_curves = {
            "curve1": _one_segment(chamfer_sketch["geometry"], [40.0, 0.0, 0.0], [60.0, 0.0, 0.0], "chamfer curve1"),
            "curve2": _one_segment(chamfer_sketch["geometry"], [60.0, 0.0, 0.0], [60.0, 20.0, 0.0], "chamfer curve2"),
        }
        if not _segment_matches(chamfer_curves["curve1"], [40, 0, 0], CHAMFER_CORNER) or not _segment_matches(
                chamfer_curves["curve2"], CHAMFER_CORNER, [60, 20, 0]):
            raise AssertionError("explicit chamfer selection does not use curve1=end and curve2=start at the same corner")
        chamfer_case = record_case("F07_combined_right_angle_distance_angle_chamfer", {
            "kind": "chamfer", "curve1_end": "end", "curve2_end": "start",
            "distance1_mm": 4.0, "angle_deg": 30.0,
            "curve1_id": chamfer_curves["curve1"]["journal_id"],
            "curve2_id": chamfer_curves["curve2"]["journal_id"],
            "distance_angle_definition": "angle(A→C, A→B)",
            "expected_trim_points_mm": [CHAMFER_TANGENT_1, CHAMFER_TANGENT_2],
        }, "nx_edit_sketch_corner returns SUCCEEDED, mutation_state=APPLIED, verified=true; inspected first trim is 4 mm, derived second trim is d1*sin(30 deg)/sin(120 deg), and measured angle(A→C,A→B) is 30 deg within 1e-7 rad.")
        result_chamfer, status_chamfer = edit_corner("chamfer", chamfer_sketch, chamfer_curves,
                                                      {"distance1_mm": 4.0, "angle_deg": 30.0})
        chamfer_case["combined_edit"] = _redact(status_chamfer)
        chamfer_case["operation_result"] = _redact(result_chamfer)
        chamfer_case["production_angle_mode_evidence"] = _assert_distance_angle_evidence(result_chamfer)
        session = update_revision("after chamfer edit")
        chamfer_readback = resolve_current_sketch("independent chamfer geometry readback")
        chamfer_numeric = _check_chamfer_geometry(chamfer_readback)
        # Ensure the prior fillet is still present after the second edit.
        retained_fillet_numeric = _check_fillet_geometry(chamfer_readback)
        chamfer_case.update(state="PASS", independent_geometry_readback=_redact(chamfer_readback),
                            independent_numeric_check=chamfer_numeric,
                            retained_fillet_numeric_check=retained_fillet_numeric,
                            revision_id=session.get("revision_id"))
        persist()

        roundtrip_path = model_root / f"F07-mcp-{run_id}-roundtrip.prt"
        if roundtrip_path.exists():
            raise FileExistsError("unique F07 roundtrip part path already exists")
        if _canonical_path(roundtrip_path) == _canonical_path(part_path):
            raise AssertionError("roundtrip output path collides with supplied source part")
        roundtrip_case = record_case("F07_save_reopen_and_inspect", {
            "target_path": str(roundtrip_path), "reopen": True,
        }, "nx_save_copy reports saved=true and reopened=true with all association/expression/geometry/history checks VERIFIED; reopened NX sketch inspection reproduces both F07 geometric oracles and the supplied source .prt size and modification time are unchanged.")
        saved = call_operation("nx_save_copy", {
            "operation_id": "f07-save-" + uuid.uuid4().hex,
            **credentials, "target_path": str(roundtrip_path), "reopen": True,
        })
        if saved.get("saved") is not True or saved.get("reopened") is not True:
            raise AssertionError("nx_save_copy did not report saved=true and reopened=true")
        save_checks = saved.get("checks")
        required_save_checks = {"associations", "expression", "geometry", "history"}
        if not isinstance(save_checks, dict) or set(save_checks) != required_save_checks:
            raise AssertionError("nx_save_copy omitted required association, expression, geometry, or history checks")
        bad_save_checks = {key: value for key, value in save_checks.items()
                           if not isinstance(value, dict) or value.get("status") != "VERIFIED"}
        if bad_save_checks:
            raise AssertionError(f"nx_save_copy contains non-VERIFIED roundtrip checks: {bad_save_checks!r}")
        new_owner = saved.get("new_ownership_id")
        new_epoch = saved.get("new_binding_epoch")
        if not isinstance(new_owner, str) or not new_owner or isinstance(new_epoch, bool) or not isinstance(new_epoch, int):
            raise AssertionError("nx_save_copy omitted rotated ownership credentials")
        credentials.update(ownership_id=new_owner, binding_epoch=new_epoch)
        reopened_path = saved.get("saved_path") or str(roundtrip_path)
        if _canonical_path(reopened_path) != _canonical_path(roundtrip_path):
            raise AssertionError("nx_save_copy saved path differs from requested unique roundtrip path")
        roundtrip_case["save_copy"] = _redact(saved)
        reopened_part = call_raw("nx_inspect_part", credentials)[0]
        _require_inspected_part_path(reopened_part, roundtrip_path, "reopened part inspection")
        reopened_sketches = reopened_part.get("sketches")
        if not isinstance(reopened_sketches, list) or len(reopened_sketches) != 1:
            raise AssertionError("reopened F07 test part must contain exactly one sketch")
        reopened_sketch_id = reopened_sketches[0].get("journal_id") if isinstance(reopened_sketches[0], dict) else None
        if not isinstance(reopened_sketch_id, str) or not reopened_sketch_id:
            raise AssertionError("reopened F07 sketch omitted journal_id")
        reopened_sketch = inspect_sketch(reopened_sketch_id, "roundtrip F07 native readback")
        reopened_fillet = _check_fillet_geometry(reopened_sketch)
        reopened_chamfer = _check_chamfer_geometry(reopened_sketch)
        source_metadata_after = _file_metadata(part_path)
        source_metadata_before = report["source_part_file"]["before_save_copy"]
        metadata_unchanged = source_metadata_after == source_metadata_before
        report["source_part_file"].update(after_save_copy=source_metadata_after,
                                          size_and_mtime_unchanged=metadata_unchanged)
        if not metadata_unchanged:
            raise AssertionError("Save Copy changed supplied source .prt size or modification time")
        roundtrip_case.update(state="PASS", reopened_part_path=str(roundtrip_path),
                              reopened_inspection=_redact(reopened_part),
                              reopened_sketch_readback=_redact(reopened_sketch),
                              reopened_fillet_numeric_check=reopened_fillet,
                              reopened_chamfer_numeric_check=reopened_chamfer,
                              source_part_file_metadata=_redact(report["source_part_file"]))
        persist()

        returned = return_ownership()
        if returned.get("skipped"):
            raise AssertionError(f"ownership return was skipped: {returned['skipped']}")
        roundtrip_case["ownership_return"] = returned
        roundtrip_case["state"] = "PASS"
        report["saved_path"] = str(roundtrip_path)
        report["passed"] = True
        persist()
        return 0
    except Exception as exc:
        report["error"] = repr(exc)
        report["passed"] = False
        failure_case_id = active_case.get("case_id") if isinstance(active_case, dict) else "F07_installed_harness"
        generic_retest = (
            "Rebuild and reinstall the current 0.7.0 package, run with a fresh clean saved part and a new evidence path, "
            "require both combined edits to return SUCCEEDED/APPLIED/verified=true, independently match endpoints/radius/"
            "chamfer distance and angle within the recorded tolerances, verify save/reopen checks, and safely return ownership."
        )
        failure = {
            "case_id": failure_case_id,
            "failure": repr(exc),
            "reproduction_steps": (active_case.get("reproduction_steps") if isinstance(active_case, dict) else [
                "Verify the installed MCP package and local NX bridge are available.",
                "Use a new clean saved .prt under workspace/v2/models and a new evidence output path.",
                f"Rerun this script in {mode} mode and repeat {failure_case_id} using the saved calls and readbacks.",
            ]),
            "retest_criterion": (active_case.get("retest_criterion") if isinstance(active_case, dict)
                                 else generic_retest),
        }
        report["failure_record"] = failure
        if isinstance(active_case, dict):
            active_case.update(state="FAIL", failure=repr(exc),
                               reproduction_steps=failure["reproduction_steps"],
                               retest_criterion=failure["retest_criterion"])
        if credentials and client is not None:
            try:
                # Only release ownership if the bridge confirms a known idle,
                # unhalted state; UNKNOWN outcomes deliberately block this path.
                return_ownership()
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
        summary = {key: report[key] for key in (
            "passed", "mode", "server_version", "tool_count", "part_path", "saved_path", "error",
        ) if key in report}
        print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
