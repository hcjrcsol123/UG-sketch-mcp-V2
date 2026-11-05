"""Installed MCP smoke for F12 multi-profile extrusion.

Use ``--ready --output <new.json>`` for read-only installed-host discovery.
For model validation, create a fresh part with
``tools/v2/create-f12-mcp-fixture.py``, open that saved part in the dedicated
NX session, then run this script with ``--part-path``. The script starts only
the configured local MCP stdio host; it never starts or closes NX. Model mode
is a system/MCP integration experiment and must be run by the designated NX
operator on the isolated fixture. The expected values are independent digital
geometry calculations, not physical measurements or reliability evidence.
"""
from __future__ import annotations

import argparse
import datetime
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools" / "v2"))
from f01_f02_mcp_client import McpStdioClient
from f12_extrusion_oracle import Loop, net_profile_area_mm2, swept_volume_mm3

PROTOCOL_VERSION = "2025-11-25"
OPERATION_TIMEOUT_SECONDS = 180.0
POLL_INTERVAL_SECONDS = 0.2
MCP_RESPONSE_LINE_LIMIT_BYTES = 16 * 1024 * 1024
LENGTH_TOLERANCE_MM = 1.0e-5
VOLUME_ABSOLUTE_TOLERANCE_MM3 = 1.0e-5
VOLUME_RELATIVE_TOLERANCE = 1.0e-8
PRIVATE_KEY_PARTS = ("token", "secret", "authorization", "ownership_id", "credential",
                     "api_key", "access_key", "private_key", "password")
TERMINAL_STATES = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--ready", action="store_true", help="check installed MCP discovery/readiness only")
    mode.add_argument("--part-path", type=Path, help="saved F12 fixture under workspace/v2/models")
    parser.add_argument("--output", required=True, type=Path, help="new JSON evidence path")
    parser.add_argument("--expected-server-version", default=os.environ.get("NX_SKETCH_MCP_EXPECTED_VERSION"),
                        help="expected installed MCP version; may also be set with NX_SKETCH_MCP_EXPECTED_VERSION")
    args = parser.parse_args(argv)
    if args.output.exists():
        parser.error("evidence output must be a new path")
    return args


def _redact(value: object) -> object:
    if isinstance(value, dict):
        return {key: ("[REDACTED]" if any(part in str(key).casefold() for part in PRIVATE_KEY_PARTS)
                      else _redact(child)) for key, child in value.items()}
    if isinstance(value, (list, tuple)):
        return [_redact(child) for child in value]
    if isinstance(value, str):
        try:
            decoded = json.loads(value)
        except (TypeError, ValueError):
            return value
        if isinstance(decoded, (dict, list)):
            return json.dumps(_redact(decoded), ensure_ascii=False, separators=(",", ":"))
    return value


def _payload(response: dict, tool: str) -> dict:
    if response.get("isError") is True:
        raise RuntimeError(f"{tool} returned MCP error {_error_code(response) or 'UNKNOWN'}")
    content = response.get("structuredContent")
    if isinstance(content, dict):
        return content
    for item in response.get("content", []):
        if isinstance(item, dict) and isinstance(item.get("text"), str):
            try:
                decoded = json.loads(item["text"])
            except ValueError:
                continue
            if isinstance(decoded, dict):
                return decoded
    raise AssertionError(f"{tool} returned no structured result")


def _error_code(value: object) -> str | None:
    if isinstance(value, dict):
        for key in ("code", "error_code"):
            item = value.get(key)
            if isinstance(item, str):
                return item
        for item in value.values():
            result = _error_code(item)
            if result:
                return result
    elif isinstance(value, list):
        for item in value:
            result = _error_code(item)
            if result:
                return result
    elif isinstance(value, str):
        try:
            return _error_code(json.loads(value))
        except (TypeError, ValueError):
            return None
    return None


def _vector(value: object, label: str, length: int) -> list[float]:
    if not isinstance(value, (list, tuple)) or len(value) != length:
        raise AssertionError(f"{label} must contain {length} values")
    result = [_finite_number(item, f"{label}[{index}]") for index, item in enumerate(value)]
    if not all(math.isfinite(item) for item in result):
        raise AssertionError(f"{label} contains a non-finite value")
    return result


def _finite_number(value: object, label: str) -> float:
    numeric_text = isinstance(value, str) and re.fullmatch(
        r"[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[Ee][+-]?[0-9]+)?", value) is not None
    if isinstance(value, bool) or not (isinstance(value, (int, float)) or numeric_text):
        raise AssertionError(f"{label} must be numeric")
    result = float(value)
    if not math.isfinite(result):
        raise AssertionError(f"{label} must be finite")
    return result


def _close(actual: object, expected: float, *, label: str,
           absolute: float = LENGTH_TOLERANCE_MM,
           relative: float = 0.0) -> None:
    value = _finite_number(actual, label)
    if abs(value - expected) > max(absolute, relative * abs(expected)):
        raise AssertionError(f"{label}: expected {expected:.12g} ± max({absolute:g}, {relative:g} relative), got {value:.12g}")


def _distance(left: list[float], right: list[float]) -> float:
    if len(left) != len(right):
        raise AssertionError("native point dimensions differ")
    return math.sqrt(sum((left[index] - right[index]) ** 2 for index in range(len(left))))


def _world_point(local_xy: list[float], origin: list[float]) -> list[float]:
    return [origin[0] + float(local_xy[0]), origin[1] + float(local_xy[1]), origin[2]]


def _native_geometry_matches(requested: dict, native: dict, origin: list[float]) -> bool:
    kind = str(native.get("kind", "")).upper()
    geometry_type = requested.get("type")
    if geometry_type == "line":
        if kind != "LINE":
            return False
        actual_start = native.get("start")
        actual_end = native.get("end")
        if not isinstance(actual_start, (list, tuple)) or not isinstance(actual_end, (list, tuple)):
            return False
        actual_start = _vector(actual_start, "native line start", 3)
        actual_end = _vector(actual_end, "native line end", 3)
        expected_start = _world_point(requested["start_mm"], origin)
        expected_end = _world_point(requested["end_mm"], origin)
        direct = max(_distance(actual_start, expected_start), _distance(actual_end, expected_end))
        reversed_order = max(_distance(actual_start, expected_end), _distance(actual_end, expected_start))
        return min(direct, reversed_order) <= LENGTH_TOLERANCE_MM
    if geometry_type in {"circle", "arc"}:
        if kind != "ARC":
            return False
        actual_center = native.get("center")
        radius = native.get("radius_mm")
        if not isinstance(actual_center, (list, tuple)) or radius is None:
            return False
        actual_center = _vector(actual_center, "native conic center", 3)
        radius = _finite_number(radius, "native conic radius_mm")
        if _distance(actual_center,
                     _world_point(requested["center_mm"], origin)) > LENGTH_TOLERANCE_MM:
            return False
        expected_radius = float(requested["diameter_mm"]) / 2.0 if geometry_type == "circle" else float(requested["radius_mm"])
        if abs(radius - expected_radius) > LENGTH_TOLERANCE_MM:
            return False
        if geometry_type == "arc":
            start = math.radians(float(requested["start_angle_deg"]))
            end = start + math.radians(float(requested["sweep_deg"]))
            center = _world_point(requested["center_mm"], origin)
            expected_start = [center[0] + expected_radius * math.cos(start),
                              center[1] + expected_radius * math.sin(start), center[2]]
            expected_end = [center[0] + expected_radius * math.cos(end),
                            center[1] + expected_radius * math.sin(end), center[2]]
            actual_start, actual_end = native.get("start"), native.get("end")
            if not isinstance(actual_start, (list, tuple)) or not isinstance(actual_end, (list, tuple)):
                return False
            actual_start = _vector(actual_start, "native arc start", 3)
            actual_end = _vector(actual_end, "native arc end", 3)
            direct = max(_distance(actual_start, expected_start),
                         _distance(actual_end, expected_end))
            reverse = max(_distance(actual_start, expected_end),
                          _distance(actual_end, expected_start))
            return min(direct, reverse) <= LENGTH_TOLERANCE_MM
        return True
    if geometry_type in {"ellipse", "elliptical_arc"}:
        if kind != "ELLIPSE" and not (geometry_type == "elliptical_arc" and kind in {"ELLIPSE", "ELLIPTICAL_ARC"}):
            return False
        center = native.get("center")
        major = native.get("major_radius_mm")
        minor = native.get("minor_radius_mm")
        if not isinstance(center, (list, tuple)) or major is None or minor is None:
            return False
        center = _vector(center, "native ellipse center", 3)
        major = _finite_number(major, "native ellipse major_radius_mm")
        minor = _finite_number(minor, "native ellipse minor_radius_mm")
        expected_center = _world_point(requested["center_mm"], origin)
        if _distance(center, expected_center) > LENGTH_TOLERANCE_MM:
            return False
        if abs(major - float(requested["major_radius_mm"])) > LENGTH_TOLERANCE_MM:
            return False
        if abs(minor - float(requested["minor_radius_mm"])) > LENGTH_TOLERANCE_MM:
            return False
        axis = native.get("major_axis_direction")
        if not isinstance(axis, (list, tuple)) or len(axis) != 3:
            return False
        expected_rotation = math.radians(float(requested["rotation_deg"]))
        expected_axis = [math.cos(expected_rotation), math.sin(expected_rotation), 0.0]
        actual_axis = _vector(axis, "native ellipse major_axis_direction", 3)
        if _distance(actual_axis, expected_axis) > 1.0e-5 and _distance(actual_axis, [-v for v in expected_axis]) > 1.0e-5:
            return False
        return True
    return False


def _line(identifier: str, start: list[float], end: list[float]) -> dict:
    return {"id": identifier, "type": "line", "start_mm": start, "end_mm": end}


def _polygon(prefix: str, points: list[list[float]]) -> list[dict]:
    if len(points) < 3:
        raise ValueError("a polygon fixture requires at least three vertices")
    return [_line(f"{prefix}-{index}", start, points[(index + 1) % len(points)])
            for index, start in enumerate(points)]


def _multi_profile() -> tuple[list[dict], list[Loop], tuple[float, float, float, float]]:
    # L-shaped outer loop (area 312), a 4x5 mm hole (area 20), and a disjoint
    # 5x4 mm outer island (area 20). The net area is exactly 312 mm^2.
    outer = [[0, 0], [20, 0], [20, 12], [12, 12], [12, 18], [0, 18]]
    hole = [[2, 2], [2, 7], [6, 7], [6, 2]]
    island = [[24, 0], [29, 0], [29, 4], [24, 4]]
    geometry = _polygon("outer", outer) + _polygon("hole", hole) + _polygon("island", island)
    loops = (
        Loop(tuple((float(x), float(y)) for x, y in outer + [outer[0]]), label="concave_outer"),
        Loop(tuple((float(x), float(y)) for x, y in hole + [hole[0]]), label="inner_hole"),
        Loop(tuple((float(x), float(y)) for x, y in island + [island[0]]), label="disjoint_outer"),
    )
    return geometry, list(loops), (0.0, 29.0, 0.0, 18.0)


def _rectangle_sketch(name: str, origin: list[float], x0: float, y0: float,
                      x1: float, y1: float) -> tuple[list[dict], float, tuple[float, float, float, float]]:
    points = [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]
    area = abs((x1 - x0) * (y1 - y0))
    return _polygon(name, points), area, (min(x0, x1), max(x0, x1), min(y0, y1), max(y0, y1))


def _sketch_profile(name: str, origin_mm: list[float], geometries: list[dict],
                    area_mm2: float, bounds_2d: tuple[float, float, float, float],
                    expected_curve_count: int, expected_loop_counts: dict) -> dict:
    return {
        "name": name,
        "request": {"plane": "XY", "origin_mm": origin_mm,
                    "constraint_mode": "geometry_only", "geometries": geometries},
        "independent_oracle": {"profile_area_mm2": area_mm2, "bounds_local_mm": list(bounds_2d),
                               "curve_count": expected_curve_count, "loop_counts": expected_loop_counts},
    }


def _profiles() -> dict[str, dict]:
    multi_geometry, loops, multi_bounds = _multi_profile()
    multi_area = net_profile_area_mm2(loops)
    assert multi_area == 312.0
    multi = _sketch_profile(
        "multi_region_with_hole", [100.0, 0.0, 0.0], multi_geometry, multi_area, multi_bounds, 14,
        {"closed_loop_count": 3, "outer_loop_count": 2, "inner_loop_count": 1},
    )
    ellipse_origin = [150.0, 100.0, 0.0]
    major, minor, rotation = 6.0, 2.5, math.radians(27.0)
    extent_x = math.sqrt((major * math.cos(rotation)) ** 2 + (minor * math.sin(rotation)) ** 2)
    extent_y = math.sqrt((major * math.sin(rotation)) ** 2 + (minor * math.cos(rotation)) ** 2)
    ellipse = _sketch_profile(
        "rotated_ellipse", ellipse_origin,
        [{"id": "ellipse", "type": "ellipse", "center_mm": [0.0, 0.0],
          "major_radius_mm": major, "minor_radius_mm": minor, "rotation_deg": 27.0}],
        math.pi * major * minor,
        (-extent_x, extent_x, -extent_y, extent_y), 1,
        {"closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0},
    )
    negative_geometry, negative_area, negative_bounds = _rectangle_sketch("negative_direction", [180.0, 0.0, 0.0],
                                                                          0.0, 0.0, 2.0, 3.0)
    negative = _sketch_profile(
        "negative_direction_offset_profile", [180.0, 0.0, 0.0], negative_geometry, negative_area,
        negative_bounds, 4,
        {"closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0},
    )
    block_geometry, block_area, block_bounds = _rectangle_sketch("block", [0.0, 0.0, 0.0], 0.0, 0.0, 40.0, 30.0)
    block = _sketch_profile(
        "boolean_target_block", [0.0, 0.0, 0.0], block_geometry, block_area, block_bounds, 4,
        {"closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0},
    )
    split_geometry, split_area, split_bounds = _rectangle_sketch("split_tool", [0.0, 0.0, 0.0], 19.0, -1.0, 21.0, 31.0)
    split = _sketch_profile(
        "through_all_split_tool", [0.0, 0.0, 0.0], split_geometry, split_area, split_bounds, 4,
        {"closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0},
    )
    unite_geometry, unite_area, unite_bounds = _rectangle_sketch("unite_tool", [0.0, 0.0, 0.0], 35.0, 10.0, 45.0, 20.0)
    unite = _sketch_profile(
        "finite_unite_tool", [0.0, 0.0, 0.0], unite_geometry, unite_area, unite_bounds, 4,
        {"closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0},
    )
    sheet_geometry = [
        {"id": "open-semicircle", "type": "arc", "center_mm": [0.0, 0.0],
         "radius_mm": 5.0, "start_angle_deg": 0.0, "sweep_deg": 180.0},
        _line("open-chain-tail", [-5.0, 0.0], [-10.0, 0.0]),
    ]
    sheet_curve_length = 5.0 * math.pi + 5.0
    sheet_depth = 4.0
    sheet = {
        "name": "sheet_open_line_arc_chain",
        "request": {"plane": "XY", "origin_mm": [230.0, 0.0, 0.0],
                    "constraint_mode": "geometry_only", "geometries": sheet_geometry},
        "independent_oracle": {"profile_area_mm2": None, "curve_length_mm": sheet_curve_length,
                               "analytic_surface_area_mm2": sheet_curve_length * sheet_depth,
                               "bounds_local_mm": [-10.0, 5.0, 0.0, 5.0], "curve_count": 2,
                               "loop_counts": {"open_chain_count": 1, "closed_loop_count": 0,
                                               "outer_loop_count": 0, "inner_loop_count": 0},
                               "depth_mm": sheet_depth},
    }
    active_reference_geometry, active_reference_area, active_reference_bounds = _rectangle_sketch(
        "active-reference-rectangle", [250.0, 0.0, 0.0], 0.0, 0.0, 10.0, 8.0)
    active_reference_geometry.append(
        {"id": "reference-circle", "type": "circle", "center_mm": [5.0, 4.0], "diameter_mm": 2.0}
    )
    active_reference = _sketch_profile(
        "active_profile_with_reference_circle", [250.0, 0.0, 0.0], active_reference_geometry,
        active_reference_area, active_reference_bounds, 5,
        {"closed_loop_count": 1, "outer_loop_count": 1, "inner_loop_count": 0},
    )
    open_profile = _sketch_profile(
        "open_profile_rejection", [300.0, 0.0, 0.0], [_line("open", [0.0, 0.0], [5.0, 0.0])],
        0.0, (0.0, 5.0, 0.0, 0.0), 1,
        {"closed_loop_count": 0, "outer_loop_count": 0, "inner_loop_count": 0},
    )
    return {item["name"]: item for item in
            (multi, ellipse, negative, block, split, unite, sheet, active_reference, open_profile)}


def _expected_box(profile: dict, start: float, end: float) -> list[float]:
    x_min, x_max, y_min, y_max = profile["independent_oracle"]["bounds_local_mm"]
    ox, oy, oz = profile["request"]["origin_mm"]
    return [ox + x_min, oy + y_min, oz + start, ox + x_max, oy + y_max, oz + end]


def _assert_bbox(actual: object, expected: list[float], label: str) -> list[float]:
    values = _vector(actual, label, 6)
    for index, value in enumerate(expected):
        _close(values[index], value, label=f"{label}[{index}]")
    return values


def _inspected_part_path(inspection: dict) -> Path:
    part = inspection.get("part")
    identity = inspection.get("identity")
    part_path = part.get("path") if isinstance(part, dict) else None
    identity_path = identity.get("part_path") if isinstance(identity, dict) else None
    if not isinstance(part_path, str) or not part_path or not isinstance(identity_path, str) or not identity_path:
        raise AssertionError("part inspection omitted its actual part/identity paths")
    actual = Path(part_path).resolve()
    if actual.as_posix().casefold() != Path(identity_path).resolve().as_posix().casefold():
        raise AssertionError("part inspection and binding identity paths disagree")
    return actual


def _body_fingerprint(body: dict) -> dict:
    box = body.get("bbox_mm")
    return {
        "kind": body.get("kind"), "is_solid": body.get("is_solid"),
        "volume_mm3": body.get("volume_mm3"),
        "bbox_mm": list(box) if isinstance(box, (list, tuple)) else None,
        "face_count": len(body.get("face_ids", [])) if isinstance(body.get("face_ids"), list) else None,
        "edge_count": len(body.get("edge_ids", [])) if isinstance(body.get("edge_ids"), list) else None,
        "consistency_fault_count": body.get("body_consistency_fault_count"),
    }


def _boxes_match(actual: object, expected: list[float]) -> bool:
    if not isinstance(actual, (list, tuple)) or len(actual) != len(expected):
        return False
    try:
        values = [float(value) for value in actual]
    except (TypeError, ValueError):
        return False
    return all(math.isfinite(value) and abs(value - expected[index]) <= LENGTH_TOLERANCE_MM
               for index, value in enumerate(values))


def _assert_body_signatures_roundtrip(before: list[dict], after: list[dict]) -> None:
    if len(before) != len(after):
        raise AssertionError("SaveCopy/reopen changed the direct body count")
    unmatched = list(after)
    for before_index, expected in enumerate(before):
        match_index = None
        for index, actual in enumerate(unmatched):
            if expected.get("kind") != actual.get("kind") or expected.get("is_solid") != actual.get("is_solid"):
                continue
            if expected.get("face_count") != actual.get("face_count") or expected.get("edge_count") != actual.get("edge_count"):
                continue
            expected_box = expected.get("bbox_mm")
            if not isinstance(expected_box, list) or not _boxes_match(actual.get("bbox_mm"), expected_box):
                continue
            expected_volume = expected.get("volume_mm3")
            actual_volume = actual.get("volume_mm3")
            if expected_volume is None:
                if actual_volume is not None:
                    continue
            elif actual_volume is None or abs(float(actual_volume) - float(expected_volume)) > max(
                    VOLUME_ABSOLUTE_TOLERANCE_MM3,
                    VOLUME_RELATIVE_TOLERANCE * abs(float(expected_volume))):
                continue
            match_index = index
            break
        if match_index is None:
            raise AssertionError(f"SaveCopy/reopen body {before_index} did not map one-to-one by type, topology counts, volume, and bounds")
        unmatched.pop(match_index)
    if unmatched:
        raise AssertionError("SaveCopy/reopen left unmatched body signatures")


def _resolve_solid_body(bodies: list, expected_box: list[float], expected_volume_mm3: float,
                        label: str) -> dict:
    matches = []
    for body in bodies:
        if not isinstance(body, dict) or body.get("is_solid") is not True:
            continue
        if not _boxes_match(body.get("bbox_mm"), expected_box) or body.get("volume_mm3") is None:
            continue
        if abs(float(body["volume_mm3"]) - expected_volume_mm3) <= max(
                VOLUME_ABSOLUTE_TOLERANCE_MM3, VOLUME_RELATIVE_TOLERANCE * abs(expected_volume_mm3)):
            matches.append(body)
    if len(matches) != 1:
        raise AssertionError(f"{label} expected one current solid body by geometry/volume, found {len(matches)}")
    if not isinstance(matches[0].get("journal_id"), str):
        raise AssertionError(f"{label} direct body readback omitted current JournalIdentifier")
    return matches[0]


def _assert_verified(operation_result: dict, label: str) -> None:
    if operation_result.get("verified") is not True:
        raise AssertionError(f"{label} verify did not report verified=true")
    checks = operation_result.get("checks")
    if not isinstance(checks, dict) or not checks:
        raise AssertionError(f"{label} verify omitted production checks")
    failures = {name: row for name, row in checks.items()
                if not isinstance(row, dict) or row.get("status") != "VERIFIED"}
    if failures:
        raise AssertionError(f"{label} verify has non-VERIFIED checks: {failures}")


def _assert_extrude_definition(execution: dict, request: dict, label: str) -> dict:
    definition = execution.get("extrude_definition")
    if not isinstance(definition, dict) or definition.get("status") != "VERIFIED":
        raise AssertionError(f"{label} omitted a verified live Extrude definition")
    if definition.get("extent") != request.get("extent"):
        raise AssertionError(f"{label} live Extent differs from the request")
    if definition.get("direction_matches") is not True:
        raise AssertionError(f"{label} live direction differs from the request")
    if definition.get("boolean_matches") is not True:
        raise AssertionError(f"{label} live Boolean type differs from the request")
    expected_boolean = {"create": "Create", "unite": "Unite", "subtract": "Subtract"}[request["mode"]]
    if definition.get("actual_boolean_type") != expected_boolean:
        raise AssertionError(f"{label} live Boolean type is {definition.get('actual_boolean_type')!r}, expected {expected_boolean!r}")
    if request["mode"] != "create":
        if definition.get("targets_match") is not True:
            raise AssertionError(f"{label} native Boolean target readback does not match the explicit target")
        if definition.get("expected_target_body_id") != request.get("target_body_id"):
            raise AssertionError(f"{label} native target JournalIdentifier differs from the request")
    expected_start = request.get("start_offset_mm", 0.0)
    if definition.get("start_trim_type") != "Value" or definition.get("start_matches") is not True:
        raise AssertionError(f"{label} live NX start limit is not the requested finite Value")
    _close(definition.get("expected_start_offset_mm"), float(expected_start),
           label=f"{label} Extrude expected start offset")
    _close(definition.get("start_value_mm"), float(expected_start),
           label=f"{label} Extrude actual start value")
    if request["extent"] == "through_all":
        if definition.get("end_trim_type") != "ThroughAll" or definition.get("extent_matches") is not True:
            raise AssertionError(f"{label} live NX end limit is not exactly ThroughAll")
        if definition.get("actual_end_offset_mm") is not None:
            raise AssertionError(f"{label} ThroughAll unexpectedly reports a finite end offset")
    elif request["extent"] == "finite":
        expected_end = request.get("end_offset_mm", request.get("depth_mm"))
        if expected_end is None:
            raise AssertionError(f"{label} finite request has no requested end offset or legacy depth")
        if definition.get("end_trim_type") != "Value" or definition.get("extent_matches") is not True:
            raise AssertionError(f"{label} live NX end limit is not the requested finite Value")
        _close(definition.get("expected_end_offset_mm"), float(expected_end),
               label=f"{label} Extrude expected end offset")
        _close(definition.get("actual_end_offset_mm"), float(expected_end),
               label=f"{label} Extrude actual end value")
    else:
        raise AssertionError(f"{label} request has unsupported extent {request.get('extent')!r}")
    if (definition.get("draft_option") != "NoDraft"
            or definition.get("draft_option_matches_no_draft") is not True):
        raise AssertionError(f"{label} live NX draft option is not verified NoDraft")
    if (definition.get("offset_option") != "NoOffset"
            or definition.get("offset_option_matches_no_offset") is not True):
        raise AssertionError(f"{label} live NX offset option is not verified NoOffset")
    requested_output = request.get("profile_output", "solid")
    observed_output = definition.get("profile_output")
    if observed_output != requested_output:
        raise AssertionError(f"{label} live output type {observed_output!r} differs from requested {requested_output!r}")
    return definition


def _assert_extrude_body_mapping(execution: dict, label: str) -> dict:
    body_ids = execution.get("body_ids")
    affected_records = execution.get("bodies")
    inventory_records = execution.get("body_inventory")
    if not isinstance(body_ids, list) or not body_ids:
        raise AssertionError(f"{label} execution omitted nonempty body_ids")
    if any(not isinstance(body_id, str) or not body_id for body_id in body_ids):
        raise AssertionError(f"{label} body_ids contains an invalid body identifier")
    if len(set(body_ids)) != len(body_ids):
        raise AssertionError(f"{label} body_ids contains duplicate identifiers")

    def record_map(value: object, field: str) -> dict[str, dict]:
        if not isinstance(value, list):
            raise AssertionError(f"{label} execution omitted {field}")
        records: dict[str, dict] = {}
        for record in value:
            if not isinstance(record, dict):
                raise AssertionError(f"{label} {field} contains a non-object record")
            journal_id = record.get("journal_id")
            if not isinstance(journal_id, str) or not journal_id:
                raise AssertionError(f"{label} {field} record omitted journal_id")
            if journal_id in records:
                raise AssertionError(f"{label} {field} contains duplicate journal_id {journal_id!r}")
            records[journal_id] = record
        return records

    affected = record_map(affected_records, "bodies")
    inventory = record_map(inventory_records, "body_inventory")
    if len(affected) != len(body_ids) or set(affected) != set(body_ids):
        raise AssertionError(f"{label} body_ids do not map one-to-one to bodies records")
    if any(body_id not in inventory for body_id in body_ids):
        raise AssertionError(f"{label} body_ids do not map one-to-one into body_inventory")

    for body_id in body_ids:
        direct = affected[body_id]
        current = inventory[body_id]
        for field in ("is_solid", "measurement_status"):
            if field not in direct or direct.get(field) != current.get(field):
                raise AssertionError(f"{label} {field} differs between bodies and body_inventory for {body_id}")
        if direct.get("measurement_status") != "MEASURED":
            raise AssertionError(f"{label} body {body_id} has no verified direct measurement")
        for field in ("volume_mm3",):
            direct_value, inventory_value = direct.get(field), current.get(field)
            if direct_value is None or inventory_value is None:
                if direct_value is not None or inventory_value is not None:
                    raise AssertionError(f"{label} {field} nullability differs for {body_id}")
            else:
                _close(inventory_value, _finite_number(direct_value, f"{label} bodies.{field}"),
                       label=f"{label} body {body_id} inventory {field}",
                       absolute=VOLUME_ABSOLUTE_TOLERANCE_MM3,
                       relative=VOLUME_RELATIVE_TOLERANCE)
        direct_box = _vector(direct.get("bbox_mm"), f"{label} bodies.bbox_mm", 6)
        inventory_box = _vector(current.get("bbox_mm"), f"{label} body_inventory.bbox_mm", 6)
        for axis, (direct_value, inventory_value) in enumerate(zip(direct_box, inventory_box)):
            _close(inventory_value, direct_value,
                   label=f"{label} body {body_id} inventory bbox_mm[{axis}]")

    scalar_body_id = execution.get("body_id")
    if scalar_body_id is not None and scalar_body_id not in body_ids:
        raise AssertionError(f"{label} compatibility body_id is absent from body_ids")
    return {"body_ids": list(body_ids), "affected_body_count": len(affected),
            "body_inventory_count": len(inventory),
            "mapping": "body_ids -> bodies -> body_inventory, unique JournalIdentifier at each step"}


def _f12_profile_expectation(source_curves: int, active_curves: int, open_components: int,
                             valid_closed_components: int, outer_loops: int,
                             inner_loops: int, section_loops: int) -> dict:
    return {
        "status": "VERIFIED",
        "source_curve_count": source_curves,
        "input_curve_count": active_curves,
        "excluded_reference_curve_count": source_curves - active_curves,
        "open_component_count": open_components,
        "valid_closed_component_count": valid_closed_components,
        "outer_loop_count": outer_loops,
        "inner_loop_count": inner_loops,
        "section_loop_count": section_loops,
    }


def _assert_sheet_surface_area(execution: dict, expected_area_mm2: float, label: str) -> dict:
    tolerance = max(1.0e-4, expected_area_mm2 * 1.0e-5)
    actual_area = _finite_number(execution.get("surface_area_mm2"), f"{label} surface_area_mm2")
    reported_expected = _finite_number(execution.get("expected_surface_area_mm2"),
                                       f"{label} expected_surface_area_mm2")
    _close(actual_area, expected_area_mm2, label=f"{label} direct native sheet face area",
           absolute=tolerance)
    _close(reported_expected, expected_area_mm2, label=f"{label} reported analytic sheet area",
           absolute=tolerance)
    evidence = execution.get("sheet_surface_area_evidence")
    if not isinstance(evidence, dict) or evidence.get("status") != "VERIFIED":
        raise AssertionError(f"{label} omitted verified per-face sheet surface-area evidence")
    if evidence.get("area_oracle") != (
            "sum(active Section curve lengths) * finite extrusion span; native face area independently measured"):
        raise AssertionError(f"{label} sheet area evidence does not identify the direct-face method")
    _close(evidence.get("expected_open_or_closed_profile_surface_area_mm2"), expected_area_mm2,
           label=f"{label} evidence analytic sheet area", absolute=tolerance)
    _close(evidence.get("actual_native_sheet_face_area_mm2"), actual_area,
           label=f"{label} evidence native face area", absolute=1.0e-8)
    _close(evidence.get("tolerance_mm2"), tolerance,
           label=f"{label} production sheet area tolerance", absolute=1.0e-12)
    return {"surface_area_mm2": actual_area,
            "expected_surface_area_mm2": reported_expected,
            "production_tolerance_mm2": tolerance,
            "measurement_method": "sum of direct feature-output body face areas via production F12 per-face measurement",
            "new_mass_properties_area_used_as_oracle": False}


def _operation_result(record: dict) -> dict | None:
    """Read the direct operation envelope or the documented combined-edit envelope."""
    direct = record.get("result")
    nested = record.get("operation")
    nested_result = nested.get("result") if isinstance(nested, dict) else None
    if isinstance(nested, dict):
        if (record.get("operation_id") is not None and nested.get("operation_id") is not None
                and record["operation_id"] != nested["operation_id"]):
            raise AssertionError("operation envelope IDs disagree")
    if isinstance(direct, dict) and isinstance(nested_result, dict) and direct != nested_result:
        raise AssertionError("operation envelope results disagree")
    if isinstance(direct, dict):
        return direct
    return nested_result if isinstance(nested_result, dict) else None


def _profile_analysis(execution: dict, expected: dict, label: str) -> dict:
    analysis = execution.get("profile_analysis")
    if not isinstance(analysis, dict):
        raise AssertionError(f"{label} execution omitted profile_analysis")
    expected_status = expected.get("status", "VERIFIED")
    if analysis.get("status") != expected_status:
        raise AssertionError(f"{label} profile status: expected {expected_status!r}, got {analysis.get('status')!r}")
    for field, wanted in expected.items():
        actual = analysis.get(field)
        if actual != wanted:
            raise AssertionError(f"{label} profile_analysis.{field}: expected {wanted!r}, got {actual!r}")
    return analysis


def _tool_schema(tools: list) -> dict:
    item = next((tool for tool in tools if isinstance(tool, dict) and tool.get("name") == "nx_prepare_extrude"), None)
    if not isinstance(item, dict):
        raise AssertionError("installed tools/list omitted nx_prepare_extrude")
    schema = item.get("inputSchema")
    if not isinstance(schema, dict):
        raise AssertionError("nx_prepare_extrude omitted inputSchema")
    properties = schema.get("properties")
    required = set(schema.get("required", []))
    if not isinstance(properties, dict):
        raise AssertionError("nx_prepare_extrude schema omitted properties")
    required_fields = {"operation_id", "ownership_id", "binding_epoch", "revision_id",
                       "sketch_id", "mode", "direction", "extent"}
    if not required_fields <= required:
        raise AssertionError(f"nx_prepare_extrude missing required fields: {sorted(required_fields - required)}")
    missing = {"depth_mm", "start_offset_mm", "end_offset_mm", "profile_output"} - set(properties)
    if missing:
        raise AssertionError(f"nx_prepare_extrude schema omitted F12 fields: {sorted(missing)}")
    if "target_body_id" not in properties:
        raise AssertionError("nx_prepare_extrude schema omitted the explicit Boolean target field")
    profile_values = set(properties["profile_output"].get("enum", []))
    for branch in properties["profile_output"].get("anyOf", []):
        if isinstance(branch, dict) and isinstance(branch.get("const"), str):
            profile_values.add(branch["const"])
    if not {"solid", "sheet"} <= profile_values:
        raise AssertionError(f"profile_output schema omits solid/sheet choices: {sorted(profile_values)}")
    if {"depth_mm", "start_offset_mm", "end_offset_mm", "profile_output"} & required:
        raise AssertionError("F12 optional/default input fields were unexpectedly made globally required")
    for name, values in (("mode", {"create", "unite", "subtract"}),
                         ("direction", {"positive", "negative"}),
                         ("extent", {"finite", "through_all"})):
        actual = set(properties[name].get("enum", []))
        if not values <= actual:
            raise AssertionError(f"nx_prepare_extrude schema {name} omitted {sorted(values - actual)}")
    return {"name": item.get("name"), "description": item.get("description"),
            "inputSchema": schema, "profile_output_values": sorted(profile_values)}


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    report: dict = {
        "test": "F12 installed MCP extrusion smoke",
        "test_version": "0.1.0",
        "run_id": uuid.uuid4().hex,
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "passed": False,
        "mode": "ready" if args.ready else "modeling",
        "objective": "Exercise F12 through the installed stdio MCP endpoint with independent profile, Boolean, limit, and output-mode oracles.",
        "traceability": "docs/F12.md F12-I01; docs/V2.md F12",
        "configuration": {
            "test_layer": "installed MCP integration and NX model subsystem smoke",
            "expected_server_version": args.expected_server_version,
            "units": {"length": "mm", "area": "mm^2", "volume": "mm^3"},
            "sample_size": 1,
            "physical_instrumentation": False,
            "calibration": "not applicable; direct NX digital-model values only",
            "measurement_uncertainty": "CAD comparison thresholds are not physical metrology uncertainty.",
            "reliability_or_compliance_claim": False,
            "nx_started_by_harness": False,
            "nx_closed_by_harness": False,
        },
        "calls": [], "cases": [], "steps": [], "defects": [],
        "tolerances": {"length_mm": LENGTH_TOLERANCE_MM,
                       "volume_absolute_mm3": VOLUME_ABSOLUTE_TOLERANCE_MM3,
                       "volume_relative": VOLUME_RELATIVE_TOLERANCE},
        "limitations": [
            "Installed MCP responses are system-integration evidence, not direct independent NXOpen inspection.",
            "The dedicated native acceptance must independently inspect Section membership, sheet surface area, and any split Boolean bodies.",
            "One fixture run is one observation per case and is not a reliability or cross-version sample.",
        ],
        "current_sketch_id_resolutions": [],
    }
    if args.expected_server_version:
        report["configuration"]["expected_server_version"] = args.expected_server_version

    credentials: dict[str, object] = {}
    process: subprocess.Popen[bytes] | None = None
    client: McpStdioClient | None = None
    active_case: dict | None = None
    lifecycle = {"writes_halted": False, "unknown_operation_id": None, "execute_calls_by_id": {}}
    revision_id: str | None = None
    profiles = _profiles()

    def persist() -> None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(_redact(report), ensure_ascii=False,
                                          allow_nan=False, indent=2) + "\n", encoding="utf-8")

    def raw_call(tool: str, arguments: dict) -> tuple[dict, dict]:
        if client is None:
            raise RuntimeError("installed MCP client is not initialized")
        entry = {"tool": tool, "arguments": _redact(arguments)}
        report["calls"].append(entry)
        try:
            response = client.request("tools/call", {"name": tool, "arguments": arguments})
        except Exception as exc:
            protocol_response = client.last_response
            if isinstance(protocol_response, dict) and isinstance(protocol_response.get("error"), dict):
                entry["mcp_response"] = _redact(protocol_response)
                entry["is_error"] = True
                entry["error_code"] = protocol_response["error"].get("code")
                persist()
                return protocol_response, entry
            entry["transport_error"] = repr(exc)
            persist()
            raise
        entry["mcp_response"] = _redact(response)
        entry["is_error"] = response.get("isError") is True
        if response.get("isError") is True:
            entry["error_code"] = _error_code(response)
            persist()
            return response, entry
        payload = _payload(response, tool)
        entry["payload"] = _redact(payload)
        persist()
        return payload, entry

    def poll_operation(initial: dict, label: str) -> dict:
        operation_id = initial.get("operation_id")
        if not isinstance(operation_id, str) or not operation_id:
            raise AssertionError(f"{label} omitted operation_id")
        deadline = time.monotonic() + OPERATION_TIMEOUT_SECONDS
        current = initial
        while current.get("state") not in TERMINAL_STATES:
            if time.monotonic() >= deadline:
                raise TimeoutError(f"{label} operation {operation_id} did not reach terminal state")
            time.sleep(POLL_INTERVAL_SECONDS)
            current, _ = raw_call("nx_get_operation", {"operation_id": operation_id})
            if current.get("operation_id") not in (None, operation_id):
                raise AssertionError(f"{label} nx_get_operation returned the wrong operation ID")
        if current.get("state") == "UNKNOWN":
            lifecycle["writes_halted"] = True
            lifecycle["unknown_operation_id"] = operation_id
        return current

    def operation_call(tool: str, arguments: dict, label: str) -> tuple[dict, dict, dict]:
        if lifecycle["writes_halted"]:
            raise RuntimeError("F12 smoke stopped writes after an UNKNOWN operation")
        initial, entry = raw_call(tool, arguments)
        if entry.get("is_error"):
            return initial, {}, entry
        if not isinstance(initial.get("operation_id"), str):
            raise AssertionError(f"{label} {tool} did not return an async operation")
        terminal = poll_operation(initial, label)
        entry["terminal_operation"] = _redact(terminal)
        persist()
        result = _operation_result(terminal)
        if terminal.get("state") == "SUCCEEDED":
            if not isinstance(result, dict):
                raise AssertionError(f"{label} terminal result is not an object")
            return terminal, result, entry
        if terminal.get("state") == "UNKNOWN":
            raise RuntimeError(f"{label} reached UNKNOWN; query only the same operation ID and stop writes")
        raise RuntimeError(f"{label} ended {terminal.get('state')}/{_error_code(terminal) or 'NO_ERROR_CODE'}")

    def sync_call(tool: str, arguments: dict) -> dict:
        payload, entry = raw_call(tool, arguments)
        if entry.get("is_error"):
            raise RuntimeError(f"{tool} failed: {_error_code(payload) or payload.get('error')}")
        if isinstance(payload.get("operation_id"), str):
            raise AssertionError(f"{tool} unexpectedly returned an async operation")
        return payload

    def current_session() -> dict:
        return sync_call("nx_get_session", credentials)

    def refresh_revision() -> str:
        nonlocal revision_id
        session = current_session()
        value = session.get("revision_id")
        if not isinstance(value, str) or not value:
            raise AssertionError("nx_get_session omitted revision_id")
        revision_id = value
        return value

    def inspect_part() -> dict:
        return sync_call("nx_inspect_part", credentials)

    def add_case(case_id: str, objective: str, expected: dict) -> dict:
        nonlocal active_case
        case = {"case_id": case_id, "state": "RUNNING", "objective": objective,
                "independent_expected": expected}
        report["cases"].append(case)
        active_case = case
        persist()
        return case

    def record_failure(case: dict, exc: Exception) -> None:
        case.update(state="FAIL", failure=repr(exc), reproduction_steps=[
            "Create a new blank fixture with tools/v2/create-f12-mcp-fixture.py and retain its JSON metadata.",
            "Open that saved fixture in the designated idle NX session and rerun this smoke with --part-path plus a new --output path.",
            f"Repeat {case.get('case_id')} using the exact request and response records retained in this report.",
        ], retest_criterion=(
            "The same request completes without UNKNOWN, execute reports APPLIED, verify reports verified=true with all checks VERIFIED, "
            "the independent area/volume/bounds and direct body inventory match within stated tolerances, and the required save/reopen and safe-return checks pass."
        ))
        report["defects"].append({"case_id": case.get("case_id"), "failure": repr(exc),
                                  "reproduction_steps": case["reproduction_steps"],
                                  "retest_criterion": case["retest_criterion"]})
        persist()

    def create_sketch(profile: dict, case_id: str) -> tuple[str, dict]:
        case = add_case(case_id, "Create and inspect a uniquely identified native Sketch before extrusion.",
                        profile["independent_oracle"])
        try:
            request = {"operation_id": "f12-sketch-" + uuid.uuid4().hex,
                       **credentials, "revision_id": refresh_revision(), **profile["request"]}
            case["sketch_request"] = _redact(request)
            prepared_op, prepared, _ = operation_call("nx_prepare_sketch", request, case_id + " prepare sketch")
            plan = prepared.get("plan")
            if not isinstance(plan, dict) or not isinstance(plan.get("plan_id"), str):
                raise AssertionError("nx_prepare_sketch omitted plan.plan_id")
            execute_id = "f12-sketch-execute-" + uuid.uuid4().hex
            execute_request = {"operation_id": execute_id, **credentials, "plan_id": plan["plan_id"]}
            executed_op, executed, _ = operation_call("nx_execute_plan", execute_request, case_id + " execute sketch")
            if executed.get("mutation_state") != "APPLIED":
                raise AssertionError("Sketch execution did not report APPLIED")
            verified_op, verified, _ = operation_call("nx_verify_part", {
                "operation_id": "f12-sketch-verify-" + uuid.uuid4().hex, **credentials,
                "execute_operation_id": execute_id,
            }, case_id + " verify sketch")
            _assert_verified(verified, case_id + " Sketch")
            sketch_id = executed.get("sketch_id") or verified.get("sketch_id")
            if not isinstance(sketch_id, str) or not sketch_id:
                raise AssertionError("Sketch operation omitted sketch_id")
            inspection = sync_call("nx_inspect_sketch", {**credentials, "sketch_id": sketch_id})
            sketch = inspection.get("sketch")
            if not isinstance(sketch, dict) or sketch.get("journal_id") != sketch_id:
                raise AssertionError("nx_inspect_sketch returned a different or missing Sketch")
            geometry = sketch.get("geometry")
            expected_count = profile["independent_oracle"]["curve_count"]
            if not isinstance(geometry, list) or len(geometry) != expected_count:
                raise AssertionError(f"{case_id} expected {expected_count} direct curve records, got {len(geometry) if isinstance(geometry, list) else 'missing'}")
            kinds = [item.get("kind") for item in geometry if isinstance(item, dict)]
            if profile["name"] == "rotated_ellipse" and kinds != ["ELLIPSE"]:
                raise AssertionError(f"ellipse Sketch native type mismatch: {kinds}")
            if profile["name"] in {"multi_region_with_hole", "boolean_target_block",
                                    "through_all_split_tool", "finite_unite_tool"}:
                if any(kind != "LINE" for kind in kinds) or len(kinds) != expected_count:
                    raise AssertionError(f"{case_id} expected all native LINE curves, got {kinds}")
            if profile["name"] == "sheet_open_line_arc_chain" and set(kinds) != {"LINE", "ARC"}:
                raise AssertionError(f"{case_id} expected one open native Line/Arc chain, got {kinds}")
            case.update(state="PASS", sketch_id=sketch_id, sketch_inspection=_redact(inspection),
                        prepare_operation_id=prepared_op.get("operation_id"),
                        execute_operation_id=executed_op.get("operation_id"),
                        verify_operation_id=verified_op.get("operation_id"))
            persist()
            return sketch_id, sketch
        except Exception as exc:
            record_failure(case, exc)
            raise

    def resolve_current_sketch_id(profile: dict, initial_id: str, label: str) -> str:
        part = inspect_part()
        sketches = part.get("sketches")
        if not isinstance(sketches, list):
            raise AssertionError("nx_inspect_part omitted current Sketch inventory")
        expected_geometries = profile["request"]["geometries"]
        origin = profile["request"]["origin_mm"]
        matches = []
        mismatches = []
        for candidate in sketches:
            if not isinstance(candidate, dict) or not isinstance(candidate.get("geometry"), list):
                continue
            native = candidate["geometry"]
            if len(native) != len(expected_geometries):
                continue
            unmatched = list(native)
            for requested in expected_geometries:
                possible = [index for index, record in enumerate(unmatched)
                            if isinstance(record, dict) and _native_geometry_matches(requested, record, origin)]
                if len(possible) != 1:
                    break
                unmatched.pop(possible[0])
            else:
                if not unmatched and isinstance(candidate.get("journal_id"), str):
                    matches.append(candidate)
                    continue
            mismatches.append({"journal_id": candidate.get("journal_id"),
                               "geometry_count": len(native),
                               "reason": "curve count or one-to-one native geometry match differed"})
        if len(matches) != 1:
            raise AssertionError(f"{label} current sketch could not be resolved uniquely from direct curve geometry; "
                                 f"matches={[row.get('journal_id') for row in matches]!r}, candidates={mismatches[:8]!r}")
        current_id = matches[0]["journal_id"]
        report["current_sketch_id_resolutions"].append({
            "case_id": label, "initial_sketch_id": initial_id,
            "current_sketch_id": current_id, "geometry_match_count": len(expected_geometries),
            "resolution_method": "one-to-one native curve type/parameter/endpoint readback",
        })
        persist()
        return current_id

    def query_same_operation(operation_id: str, expected_terminal: dict, label: str) -> dict:
        payload, entry = raw_call("nx_get_operation", {"operation_id": operation_id})
        if entry.get("is_error"):
            raise AssertionError(f"{label} nx_get_operation query failed")
        if payload.get("operation_id") != operation_id or payload.get("state") != "SUCCEEDED":
            raise AssertionError(f"{label} same-ID query returned a different ID or terminal state")
        if _operation_result(payload) != _operation_result(expected_terminal):
            raise AssertionError(f"{label} same-ID query result differs from the terminal result")
        return payload

    def extrude(case_id: str, sketch_id: str, arguments: dict,
                expected_volume_mm3: float | None, expected_bbox_mm: list[float] | None,
                expected_body_kind: str = "SOLID_BODY", expected_body_count: int | None = None,
                expected_profile_counts: dict | None = None,
                expected_target_volume_delta_mm3: float | None = None,
                expect_split_boxes: list[list[float]] | None = None,
                expected_created_output_sets: list[list[dict]] | None = None,
                profile_spec: dict | None = None) -> tuple[dict, dict, dict, dict]:
        nonlocal revision_id
        if profile_spec is not None:
            sketch_id = resolve_current_sketch_id(profile_spec, sketch_id, case_id)
        case = add_case(case_id, "Prepare, execute once, query the same operation ID, verify, and compare independent geometry values.",
                        {"sketch_id": sketch_id, **arguments,
                         "expected_volume_mm3": expected_volume_mm3,
                         "expected_bbox_mm": expected_bbox_mm})
        try:
            before_part = inspect_part()
            before_bodies = before_part.get("bodies")
            if not isinstance(before_bodies, list):
                raise AssertionError("nx_inspect_part omitted direct body inventory")
            case["body_inventory_before"] = _redact(before_bodies)
            request = {"operation_id": "f12-extrude-prepare-" + uuid.uuid4().hex,
                       **credentials, "revision_id": refresh_revision(), "sketch_id": sketch_id, **arguments}
            case["request"] = _redact(request)
            prepared_op, prepared, _ = operation_call("nx_prepare_extrude", request, case_id + " prepare")
            plan = prepared.get("plan")
            if not isinstance(plan, dict) or not isinstance(plan.get("plan_id"), str):
                raise AssertionError("nx_prepare_extrude omitted plan.plan_id")
            case["plan"] = _redact(plan)

            execute_id = "f12-extrude-execute-" + uuid.uuid4().hex
            lifecycle["execute_calls_by_id"][execute_id] = 0
            lifecycle["execute_calls_by_id"][execute_id] += 1
            execute_request = {"operation_id": execute_id, **credentials, "plan_id": plan["plan_id"]}
            executed_op, executed, _ = operation_call("nx_execute_plan", execute_request, case_id + " execute")
            if executed.get("mutation_state") != "APPLIED":
                raise AssertionError(f"execution mutation_state is {executed.get('mutation_state')!r}")
            definition = _assert_extrude_definition(executed, arguments, case_id)
            body_mapping = _assert_extrude_body_mapping(executed, case_id)
            if lifecycle["execute_calls_by_id"][execute_id] != 1:
                raise AssertionError("execution operation ID was submitted more than once")
            queried = query_same_operation(execute_id, executed_op, case_id)
            case["same_id_operation_get"] = _redact(queried)
            case["execute_call_count_for_operation_id"] = lifecycle["execute_calls_by_id"][execute_id]

            verified_op, verified, _ = operation_call("nx_verify_part", {
                "operation_id": "f12-extrude-verify-" + uuid.uuid4().hex, **credentials,
                "execute_operation_id": execute_id,
            }, case_id + " verify")
            _assert_verified(verified, case_id)
            if verified.get("execute_operation_id") != execute_id or verified.get("plan_id") != executed.get("plan_id"):
                raise AssertionError("fresh verification refers to a different execution or plan")
            verification_evidence = verified.get("evidence")
            if not isinstance(verification_evidence, dict):
                raise AssertionError("fresh verification omitted native evidence")
            if verification_evidence.get("feature_id") != executed.get("feature_id"):
                raise AssertionError("fresh verification resolved a different Extrude feature")
            verification_mapping = _assert_extrude_body_mapping(verification_evidence, case_id + " fresh verification")
            if set(verification_evidence["body_ids"]) != set(executed["body_ids"]):
                raise AssertionError("fresh verification resolved a different output body set")
            case["execution"] = _redact(executed)
            case["extrude_definition"] = _redact(definition)
            case["execution_body_mapping"] = body_mapping
            case["verification_body_mapping"] = verification_mapping
            case["verification"] = _redact(verified)
            case["verification_native_evidence"] = _redact(verification_evidence)
            if expected_profile_counts is not None:
                case["profile_analysis"] = _redact(
                    _profile_analysis(executed, expected_profile_counts, case_id))

            part_after = inspect_part()
            bodies_after = part_after.get("bodies")
            if not isinstance(bodies_after, list):
                raise AssertionError("post-execution nx_inspect_part omitted body inventory")
            case["body_inventory_after"] = _redact(bodies_after)
            case["part_revision_after"] = part_after.get("revision_id")
            case["feature_inventory_after"] = _redact(part_after.get("features"))
            mode = arguments.get("mode")
            affected_before_ids = ({arguments["target_body_id"]}
                                   if mode in {"unite", "subtract"}
                                   and isinstance(arguments.get("target_body_id"), str) else set())
            _same_body_state_before_and_after(before_bodies, bodies_after, affected_before_ids,
                                              case_id + " non-target body preservation")

            if expected_body_count is not None and len(bodies_after) != expected_body_count:
                raise AssertionError(f"expected {expected_body_count} bodies after {case_id}, got {len(bodies_after)}")
            if expect_split_boxes is not None:
                solid_bodies = [body for body in bodies_after if isinstance(body, dict) and body.get("is_solid") is True
                                and any(_boxes_match(body.get("bbox_mm"), box) for box in expect_split_boxes)]
                if len(solid_bodies) != len(expect_split_boxes):
                    raise AssertionError(f"split subtract expected {len(expect_split_boxes)} exact fragment boxes, got {len(solid_bodies)}")
                expected_fragment_volume = expected_volume_mm3
                if expected_fragment_volume is None:
                    raise AssertionError("split oracle omitted expected fragment volume")
                for index, box in enumerate(expect_split_boxes):
                    body = next(item for item in solid_bodies if _boxes_match(item.get("bbox_mm"), box))
                    _close(body.get("volume_mm3"), expected_fragment_volume, label=f"{case_id} fragment {index} volume",
                           absolute=VOLUME_ABSOLUTE_TOLERANCE_MM3, relative=VOLUME_RELATIVE_TOLERANCE)
                actual_sum = sum(float(body.get("volume_mm3")) for body in solid_bodies)
                case["independent_total_fragment_volume_mm3"] = actual_sum
                _close(actual_sum, expected_fragment_volume * len(expect_split_boxes),
                       label=case_id + " total split volume", absolute=VOLUME_ABSOLUTE_TOLERANCE_MM3,
                       relative=VOLUME_RELATIVE_TOLERANCE)
            elif expected_created_output_sets is not None:
                if arguments.get("mode") != "create":
                    raise AssertionError("expected_created_output_sets is valid only for create mode")
                before_ids = {body.get("journal_id") for body in before_bodies if isinstance(body, dict)}
                additions = [body for body in bodies_after if isinstance(body, dict)
                             and body.get("journal_id") not in before_ids]
                matched_set = None
                for output_set in expected_created_output_sets:
                    if len(output_set) != len(additions):
                        continue
                    unmatched = list(additions)
                    for expected in output_set:
                        match_index = next((index for index, body in enumerate(unmatched)
                                            if body.get("kind") == "SOLID_BODY"
                                            and _boxes_match(body.get("bbox_mm"), expected["bbox_mm"])
                                            and body.get("volume_mm3") is not None
                                            and abs(float(body["volume_mm3"]) - float(expected["volume_mm3"])) <= max(
                                                VOLUME_ABSOLUTE_TOLERANCE_MM3,
                                                VOLUME_RELATIVE_TOLERANCE * abs(float(expected["volume_mm3"])))), None)
                        if match_index is None:
                            break
                        unmatched.pop(match_index)
                    else:
                        if not unmatched:
                            matched_set = output_set
                            break
                if matched_set is None:
                    raise AssertionError("created multi-region body inventory did not match any independent whole-body or per-region signature")
                actual_volume = sum(float(body["volume_mm3"]) for body in additions)
                _close(actual_volume, expected_volume_mm3, label=case_id + " total created volume",
                       absolute=VOLUME_ABSOLUTE_TOLERANCE_MM3, relative=VOLUME_RELATIVE_TOLERANCE)
                case["created_body_signatures"] = [_redact(_body_fingerprint(body)) for body in additions]
                case["matched_created_output_signature"] = _redact(matched_set)
            elif expected_volume_mm3 is not None or expected_bbox_mm is not None:
                output_id = executed.get("body_id")
                inventory_record = None
                if isinstance(output_id, str):
                    inventory_record = next((body for body in bodies_after if isinstance(body, dict)
                                             and body.get("journal_id") == output_id), None)
                if inventory_record is None and arguments.get("mode") in {"unite", "subtract"}:
                    target_id = arguments.get("target_body_id")
                    inventory_record = next((body for body in bodies_after if isinstance(body, dict)
                                             and body.get("journal_id") == target_id), None)
                if inventory_record is None and arguments.get("mode") == "create":
                    before_ids = {body.get("journal_id") for body in before_bodies if isinstance(body, dict)}
                    additions = [body for body in bodies_after if isinstance(body, dict)
                                 and body.get("journal_id") not in before_ids]
                    if len(additions) == 1:
                        inventory_record = additions[0]
                if not isinstance(inventory_record, dict):
                    raise AssertionError("could not resolve the affected output Body from current direct inventory")
                case["affected_body_direct_readback"] = _redact(inventory_record)
                if expected_body_kind is not None and inventory_record.get("kind") != expected_body_kind:
                    raise AssertionError(f"expected {expected_body_kind}, got {inventory_record.get('kind')!r}")
                if expected_volume_mm3 is not None:
                    live_measurements = verification_evidence.get("bodies")
                    measured = next((body for body in live_measurements if isinstance(body, dict)
                                     and body.get("journal_id") == inventory_record.get("journal_id")), None) if isinstance(live_measurements, list) else None
                    if measured is None:
                        raise AssertionError("fresh F12 verification omitted the affected body measurement")
                    case["affected_body_verification_measurement"] = _redact(measured)
                    case["volume_oracle_source"] = "fresh F12 VerifyPlan body measurement at accuracy 0.999999; general nx_inspect_part legacy accuracy 0.99 is retained separately"
                    _close(measured.get("volume_mm3"), expected_volume_mm3,
                           label=case_id + " fresh verified body volume", absolute=VOLUME_ABSOLUTE_TOLERANCE_MM3,
                           relative=VOLUME_RELATIVE_TOLERANCE)
                if expected_bbox_mm is not None:
                    _assert_bbox(inventory_record.get("bbox_mm"), expected_bbox_mm, case_id + " direct body bbox")
                if expected_target_volume_delta_mm3 is not None:
                    target_id = arguments.get("target_body_id")
                    before_target = next((body for body in before_bodies if isinstance(body, dict)
                                          and body.get("journal_id") == target_id), None)
                    if before_target is None:
                        raise AssertionError("explicit Boolean target was absent before operation")
                    _close(float(inventory_record.get("volume_mm3")) - float(before_target.get("volume_mm3")),
                           expected_target_volume_delta_mm3, label=case_id + " independent target volume delta",
                           absolute=VOLUME_ABSOLUTE_TOLERANCE_MM3, relative=VOLUME_RELATIVE_TOLERANCE)
            revision_id = part_after.get("revision_id") or verified.get("revision_id") or revision_id
            case.update(state="PASS", prepare_operation_id=prepared_op.get("operation_id"),
                        execute_operation_id=execute_id, verify_operation_id=verified_op.get("operation_id"))
            persist()
            return prepared, executed, verified, part_after
        except Exception as exc:
            record_failure(case, exc)
            raise

    def expect_prepare_rejection(case_id: str, sketch_id: str, params: dict,
                                 expected_codes: set[str], expected_mcp_invalid_argument: bool = False,
                                 profile_spec: dict | None = None) -> None:
        if profile_spec is not None:
            sketch_id = resolve_current_sketch_id(profile_spec, sketch_id, case_id)
        case = add_case(case_id, "Reject an invalid F12 input during prepare with no plan, native mutation, revision change, or body inventory change.",
                        {"expected_error_codes": sorted(expected_codes), "request_fields": sorted(params)})
        try:
            before = inspect_part()
            before_bodies = [_body_fingerprint(item) for item in before.get("bodies", []) if isinstance(item, dict)]
            before_revision = before.get("revision_id") or refresh_revision()
            request = {"operation_id": "f12-invalid-" + uuid.uuid4().hex,
                       **credentials, "revision_id": before_revision, "sketch_id": sketch_id, **params}
            case["request"] = _redact(request)
            initial, entry = raw_call("nx_prepare_extrude", request)
            if entry.get("is_error"):
                direct_code = _error_code(initial)
                numeric_error = initial.get("error") if isinstance(initial, dict) else None
                protocol_invalid = isinstance(numeric_error, dict) and numeric_error.get("code") == -32602
                code = direct_code or ("INVALID_ARGUMENT" if expected_mcp_invalid_argument and protocol_invalid else None)
                if expected_codes and code not in expected_codes:
                    raise AssertionError(f"invalid request expected one of {sorted(expected_codes)}, got {direct_code or 'MCP_PROTOCOL_ERROR'}")
                case["direct_mcp_error"] = _redact(initial)
                case["observed_error_code"] = code
            else:
                if not isinstance(initial.get("operation_id"), str):
                    raise AssertionError("invalid prepare returned neither a direct error nor an operation record")
                terminal = poll_operation(initial, case_id)
                code = _error_code(terminal)
                case["terminal_operation"] = _redact(terminal)
                case["observed_error_code"] = code
                if terminal.get("state") != "FAILED" or (expected_codes and code not in expected_codes):
                    raise AssertionError(f"invalid prepare expected FAILED/{sorted(expected_codes)}, got {terminal.get('state')}/{code}")
                result = _operation_result(terminal)
                if result not in (None, {}) or terminal.get("plan_id") is not None:
                    raise AssertionError("invalid prepare unexpectedly returned an executable plan/result")
            after = inspect_part()
            after_bodies = [_body_fingerprint(item) for item in after.get("bodies", []) if isinstance(item, dict)]
            if before_revision != after.get("revision_id"):
                raise AssertionError("invalid prepare changed the part revision")
            if len(before_bodies) != len(after_bodies):
                raise AssertionError("invalid prepare changed body inventory")
            _same_body_state_before_and_after(before.get("bodies", []), after.get("bodies", []),
                                              set(), case_id + " read-only rejection")
            for inventory_name in ("features", "sketches"):
                before_records = before.get(inventory_name)
                after_records = after.get(inventory_name)
                if not isinstance(before_records, list) or not isinstance(after_records, list):
                    raise AssertionError(f"invalid prepare inspection omitted {inventory_name} inventory")
                before_ids = [item.get("journal_id") for item in before_records if isinstance(item, dict)]
                after_ids = [item.get("journal_id") for item in after_records if isinstance(item, dict)]
                if before_ids != after_ids:
                    raise AssertionError(f"invalid prepare changed the {inventory_name} JournalIdentifier inventory")
            status = sync_call("nx_bridge_status", {})
            if status.get("writes_halted") is not False or str(status.get("in_flight")) != "0":
                raise AssertionError("known invalid prepare changed the write gate or left an operation in flight")
            case.update(state="PASS", body_inventory_before=before_bodies,
                        body_inventory_after=after_bodies, revision_before=before_revision,
                        revision_after=after.get("revision_id"), bridge_status_after=_redact(status),
                        reproduction_steps=["Create the reported fresh F12 fixture.",
                                            "Submit the exact invalid nx_prepare_extrude request once.",
                                            "Compare the retained revision and direct body inventory before and after."],
                        retest_criterion="The same invalid request returns the documented error before execution, no plan, unchanged revision and body inventory, writes_halted=false, and in_flight=0.")
            persist()
        except Exception as exc:
            record_failure(case, exc)
            raise

    def _same_body_state_before_and_after(before_bodies: list, after_bodies: list,
                                          affected_before_ids: set[str], label: str) -> None:
        before_map = {item.get("journal_id"): _body_fingerprint(item) for item in before_bodies
                      if isinstance(item, dict) and item.get("journal_id") not in affected_before_ids}
        after_map = {item.get("journal_id"): _body_fingerprint(item) for item in after_bodies
                     if isinstance(item, dict)}
        for body_id, before_record in before_map.items():
            current_record = after_map.get(body_id)
            if not isinstance(current_record, dict):
                raise AssertionError(f"{label} lost non-target body {body_id!r}")
            old_geometry = {key: value for key, value in before_record.items() if key != "volume_mm3"}
            new_geometry = {key: value for key, value in current_record.items() if key != "volume_mm3"}
            if old_geometry != new_geometry:
                raise AssertionError(f"{label} changed non-target body {body_id!r} identity/type/volume/bounds/topology inventory")
            old_volume, new_volume = before_record.get("volume_mm3"), current_record.get("volume_mm3")
            if old_volume is None or new_volume is None:
                if old_volume is not None or new_volume is not None:
                    raise AssertionError(f"{label} changed non-target body volume availability")
            else:
                _close(new_volume, _finite_number(old_volume, label + " prior volume"),
                       label=label + " preserved native volume", absolute=VOLUME_ABSOLUTE_TOLERANCE_MM3,
                       relative=VOLUME_RELATIVE_TOLERANCE)

    try:
        if args.part_path is not None:
            part_path = args.part_path.resolve(strict=True)
            model_root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
            if not part_path.is_relative_to(model_root) or part_path.suffix.lower() != ".prt":
                raise ValueError("--part-path must be a saved .prt inside workspace/v2/models")
            metadata_path = ROOT / "workspace" / "v2" / "evidence" / (part_path.stem + ".json")
            if not metadata_path.is_file():
                raise FileNotFoundError(f"fixture metadata is missing: {metadata_path}")
            metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
            if metadata.get("part_path", "").casefold() != str(part_path).casefold():
                raise AssertionError("fixture metadata path differs from --part-path")
            if metadata.get("saved") is not True or metadata.get("bridge_loaded") is not False:
                raise AssertionError("fixture metadata does not prove a saved isolated part without a loaded bridge")
            report["part_path"] = str(part_path)
            report["fixture_metadata"] = metadata

        install_root = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy()
        env.update(PYTHONPATH=str(install_root / "app" / "src"),
                   PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen(
            [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install_root / "mcp-config.json")],
            cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, bufsize=0,
        )
        client = McpStdioClient(process, 60, max_line_bytes=MCP_RESPONSE_LINE_LIMIT_BYTES)
        report["configuration"]["mcp_response_line_limit_bytes"] = MCP_RESPONSE_LINE_LIMIT_BYTES
        report["initialize"] = _redact(client.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION, "capabilities": {},
            "clientInfo": {"name": "f12-installed-acceptance", "version": "1"},
        }))
        client.notify("notifications/initialized")
        server_info = report["initialize"].get("serverInfo")
        if not isinstance(server_info, dict) or not isinstance(server_info.get("version"), str):
            raise AssertionError("installed initialize omitted serverInfo.version")
        report["server_version"] = server_info["version"]
        if args.expected_server_version and report["server_version"] != args.expected_server_version:
            raise AssertionError(f"installed MCP version must be {args.expected_server_version}, got {report['server_version']}")
        tools = client.request("tools/list").get("tools")
        if not isinstance(tools, list):
            raise AssertionError("installed tools/list omitted tools")
        names = {item.get("name") for item in tools if isinstance(item, dict)}
        required_tools = {"nx_ensure_ready", "nx_bridge_status", "nx_takeover", "nx_return",
                          "nx_get_session", "nx_inspect_part", "nx_inspect_sketch", "nx_get_operation",
                          "nx_prepare_sketch", "nx_prepare_sketch_reference_conversion",
                          "nx_edit_sketch_reference_conversion", "nx_prepare_extrude",
                          "nx_execute_plan", "nx_verify_part", "nx_save_copy"}
        if not required_tools <= names:
            raise AssertionError(f"installed tools/list omitted: {sorted(required_tools - names)}")
        report["tool_count"] = len(names)
        report["tool_names"] = sorted(names)
        report["extrude_tool_schema"] = _tool_schema(tools)

        ready, _ = raw_call("nx_ensure_ready", {})
        for _ in range(3):
            if ready.get("state") != "STARTING":
                break
            time.sleep(1.0)
            ready, _ = raw_call("nx_ensure_ready", {})
        report["ready"] = _redact(ready)
        if ready.get("state") != "READY":
            raise AssertionError(f"nx_ensure_ready did not report READY: {ready}")
        status, _ = raw_call("nx_bridge_status", {})
        report["initial_bridge_status"] = _redact(status)
        if status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False:
            raise AssertionError("initial installed NX session must be human-owned and writes-unhalted")
        if str(status.get("in_flight")) != "0":
            raise AssertionError("initial installed NX session has an in-flight operation")
        report["nx_pid"] = status.get("pid")
        if args.ready:
            report["passed"] = True
            report["diagnostic_only"] = True
            return 0

        target_part = report["part_path"]
        takeover_request = {
            "expected_pid": status["pid"], "expected_part_path": target_part,
            "artifact_scope": "saved_independent_copy", "authorize": True,
        }
        for attempt in range(10):
            takeover, takeover_entry = raw_call("nx_takeover", takeover_request)
            # NX_BUSY here explicitly means that no model read started and
            # ownership was not taken. Never retry a write or unknown outcome.
            if not takeover_entry.get("is_error") or _error_code(takeover) != "NX_BUSY":
                break
            report["takeover_busy_readiness_rejections"] = attempt + 1
            time.sleep(0.4)
        if not isinstance(takeover.get("ownership_id"), str) or not isinstance(takeover.get("binding_epoch"), int):
            raise AssertionError("nx_takeover omitted ownership credentials")
        credentials.update(ownership_id=takeover["ownership_id"], binding_epoch=takeover["binding_epoch"])
        revision_id = takeover.get("revision_id")
        report["takeover"] = _redact(takeover)
        report["installed_capabilities"] = _redact(sync_call("nx_get_capabilities", {}))
        part = inspect_part()
        if part.get("part_path") and Path(part["part_path"]).resolve().as_posix().casefold() != Path(target_part).resolve().as_posix().casefold():
            raise AssertionError("nx_inspect_part did not return the isolated fixture path")
        if any(part.get(name) for name in ("features", "sketches", "bodies")):
            raise AssertionError("F12 smoke requires a fresh fixture with no features, sketches, or bodies")
        report["initial_part_inspection"] = _redact(part)

        # An explicit non-reference scope probe: a Reference full circle sits
        # outside an active rectangle. The solid must include only the active
        # rectangle; an accidental all-curves Section changes both volume and
        # the independently measured body bounds.
        ref_profile = profiles["active_profile_with_reference_circle"]
        ref_sketch_id, ref_sketch = create_sketch(ref_profile, "F12_active_geometry_excludes_reference_curve")
        circle_candidates = [item for item in ref_sketch["geometry"] if isinstance(item, dict)
                             and item.get("kind") == "ARC"
                             and abs(float(item.get("radius_mm", item.get("radius", -1))) - 1.0) <= LENGTH_TOLERANCE_MM]
        if len(circle_candidates) != 1:
            raise AssertionError("could not identify the explicit reference-circle candidate by native readback")
        circle_id = circle_candidates[0].get("journal_id")
        if not isinstance(circle_id, str) or not circle_id:
            raise AssertionError("reference-circle inspection omitted its current JournalIdentifier")
        before_reference_edit = inspect_part()
        before_revision = refresh_revision()
        edit_request = {"operation_id": "f12-reference-circle-" + uuid.uuid4().hex,
                        **credentials, "revision_id": before_revision, "sketch_id": ref_sketch_id,
                        "object_ids": [circle_id], "target_state": "reference"}
        ref_case = add_case("F12_reference_curve_exclusion", "Make an unselected full circle Reference, then extrude the active rectangle only.",
                            {"active_profile_area_mm2": 80.0, "ignored_reference_circle_area_mm2": math.pi,
                             "expected_volume_mm3": 320.0, "expected_bbox_mm": [250.0, 0.0, 0.0, 260.0, 8.0, 4.0]})
        ref_case["reference_edit_request"] = _redact(edit_request)
        ref_terminal, ref_result, _ = operation_call("nx_edit_sketch_reference_conversion", edit_request,
                                                       "set explicit circle Reference for F12 scope probe")
        if ref_result.get("verified") is not True:
            raise AssertionError("Reference-state edit did not confirm verified=true")
        ref_case["reference_edit_terminal"] = _redact(ref_terminal)
        after_reference_edit = inspect_part()
        if before_reference_edit.get("revision_id") == after_reference_edit.get("revision_id"):
            raise AssertionError("Reference-state edit did not advance the model revision")
        ref_state_readback = sync_call("nx_inspect_sketch", {**credentials, "sketch_id": ref_sketch_id})
        ref_state_sketch = ref_state_readback.get("sketch")
        ref_state_records = ref_state_sketch.get("geometry") if isinstance(ref_state_sketch, dict) else None
        ref_state_record = next((item for item in ref_state_records if isinstance(item, dict)
                                 and item.get("journal_id") == circle_id), None) if isinstance(ref_state_records, list) else None
        if not isinstance(ref_state_record, dict) or ref_state_record.get("reference_status") != "Reference":
            raise AssertionError("direct sketch readback did not confirm the selected circle is Reference")
        ref_case["reference_state_native_readback"] = _redact(ref_state_record)
        ref_extrude = {"mode": "create", "direction": "positive", "extent": "finite",
                       "depth_mm": 4.0, "profile_output": "solid"}
        ref_volume = swept_volume_mm3(80.0, 0.0, 4.0)
        ref_profile_counts = _f12_profile_expectation(5, 4, 0, 1, 1, 0, 1)
        _, ref_exec, _, _ = extrude("F12_reference_curve_is_excluded_from_solid", ref_sketch_id,
                                    ref_extrude, ref_volume,
                                    [250.0, 0.0, 0.0, 260.0, 8.0, 4.0],
                                    expected_body_count=1, expected_profile_counts=ref_profile_counts,
                                    profile_spec=ref_profile)
        active_case = ref_case
        ref_case.update(state="PASS", reference_object_id=circle_id,
                        reference_state_edit=_redact(ref_result), execution=_redact(ref_exec),
                        revision_before=before_revision, revision_after=after_reference_edit.get("revision_id"))
        persist()

        # Legacy finite create establishes a direct target Body for both
        # ThroughAll subtraction (including a split output) and finite unite.
        block = profiles["boolean_target_block"]
        block_sketch_id, _ = create_sketch(block, "F12_boolean_target_profile")
        block_volume = swept_volume_mm3(1200.0, 0.0, 10.0)
        block_counts = _f12_profile_expectation(4, 4, 0, 1, 1, 0, 1)
        _, block_exec, _, block_part = extrude(
            "F12_create_legacy_finite_target", block_sketch_id,
            {"mode": "create", "direction": "positive", "extent": "finite", "depth_mm": 10.0},
            block_volume, [0.0, 0.0, 0.0, 40.0, 30.0, 10.0],
            expected_body_count=2, expected_profile_counts=block_counts, profile_spec=block,
        )
        target_body = _resolve_solid_body(block_part.get("bodies", []),
                                          [0.0, 0.0, 0.0, 40.0, 30.0, 10.0], 12000.0,
                                          "legacy target-block create")
        target_id = target_body["journal_id"]

        multi = profiles["multi_region_with_hole"]
        multi_sketch_id, _ = create_sketch(multi, "F12_multi_region_hole_and_island_profile")
        multi_area = float(multi["independent_oracle"]["profile_area_mm2"])
        multi_volume = swept_volume_mm3(multi_area, -3.0, 5.0)
        multi_expected_box = [100.0, 0.0, -3.0, 129.0, 18.0, 5.0]
        multi_counts = _f12_profile_expectation(14, 14, 0, 3, 2, 1, 3)
        multi_before_count = len(inspect_part().get("bodies", []))
        multi_shapes = [
            [{"volume_mm3": 2496.0, "bbox_mm": [100.0, 0.0, -3.0, 129.0, 18.0, 5.0]}],
            [{"volume_mm3": 2336.0, "bbox_mm": [100.0, 0.0, -3.0, 120.0, 18.0, 5.0]},
             {"volume_mm3": 160.0, "bbox_mm": [124.0, 0.0, -3.0, 129.0, 4.0, 5.0]}],
        ]
        _, multi_exec, _, multi_part = extrude(
            "F12_solid_multi_region_with_hole_and_signed_finite_offsets", multi_sketch_id,
            {"mode": "create", "direction": "positive", "extent": "finite",
             "start_offset_mm": -3.0, "end_offset_mm": 5.0, "profile_output": "solid"},
            multi_volume, None, expected_body_count=None,
            expected_profile_counts=multi_counts,
            expected_created_output_sets=multi_shapes,
            profile_spec=multi,
        )
        multi_case = report["cases"][-1]
        multi_case["oracle_detail"] = {"outer_l_area_mm2": 312.0, "hole_area_mm2": 20.0,
                                        "island_area_mm2": 20.0, "net_area_mm2": multi_area,
                                        "length_mm": 8.0, "volume_mm3": multi_volume}
        multi_case["extrude_definition"] = _redact(multi_exec.get("extrude_definition"))
        persist()

        ellipse = profiles["rotated_ellipse"]
        ellipse_sketch_id, _ = create_sketch(ellipse, "F12_full_rotated_ellipse_profile")
        ellipse_area = float(ellipse["independent_oracle"]["profile_area_mm2"])
        ellipse_volume = swept_volume_mm3(ellipse_area, 0.0, 7.0)
        bounds = ellipse["independent_oracle"]["bounds_local_mm"]
        ellipse_box = [ellipse["request"]["origin_mm"][0] + bounds[0],
                       ellipse["request"]["origin_mm"][1] + bounds[2], 0.0,
                       ellipse["request"]["origin_mm"][0] + bounds[1],
                       ellipse["request"]["origin_mm"][1] + bounds[3], 7.0]
        ellipse_counts = _f12_profile_expectation(1, 1, 0, 1, 1, 0, 1)
        ellipse_before_count = len(multi_part.get("bodies", []))
        _, ellipse_exec, _, ellipse_part = extrude(
            "F12_full_ellipse_solid_create", ellipse_sketch_id,
            {"mode": "create", "direction": "positive", "extent": "finite",
             "depth_mm": 7.0, "profile_output": "solid"},
            ellipse_volume, ellipse_box, expected_body_count=ellipse_before_count + 1,
            expected_profile_counts=ellipse_counts, profile_spec=ellipse,
        )
        ellipse_case = report["cases"][-1]
        ellipse_case["oracle_detail"] = {"analytic_area_mm2": ellipse_area,
                                          "analytic_volume_mm3": ellipse_volume,
                                          "extrude_definition": _redact(ellipse_exec.get("extrude_definition"))}
        persist()

        negative = profiles["negative_direction_offset_profile"]
        negative_sketch_id, _ = create_sketch(negative, "F12_negative_direction_nonzero_offset_profile")
        negative_area = float(negative["independent_oracle"]["profile_area_mm2"])
        negative_volume = swept_volume_mm3(negative_area, 1.0, 4.0)
        negative_before_count = len(ellipse_part.get("bodies", []))
        _, negative_exec, _, negative_part = extrude(
            "F12_negative_direction_nonzero_finite_offsets", negative_sketch_id,
            {"mode": "create", "direction": "negative", "extent": "finite",
             "start_offset_mm": 1.0, "end_offset_mm": 4.0, "profile_output": "solid"},
            negative_volume, [180.0, 0.0, -4.0, 182.0, 3.0, -1.0],
            expected_body_count=negative_before_count + 1,
            expected_profile_counts=_f12_profile_expectation(4, 4, 0, 1, 1, 0, 1),
            profile_spec=negative,
        )
        negative_case = report["cases"][-1]
        negative_case["oracle_detail"] = {"profile_area_mm2": negative_area,
                                            "signed_direction": "negative sketch normal",
                                            "offset_interval_mm": [1.0, 4.0],
                                            "world_axial_interval_mm": [-4.0, -1.0],
                                            "volume_mm3": negative_volume,
                                            "extrude_definition": _redact(negative_exec.get("extrude_definition"))}
        persist()

        # ThroughAll subtract cuts a full-width slot through a target block and
        # must return two directly measurable solid fragments.
        split = profiles["through_all_split_tool"]
        split_sketch_id, _ = create_sketch(split, "F12_through_all_split_profile")
        before_cut = inspect_part()
        base_target = _resolve_solid_body(before_cut.get("bodies", []),
                                          [0.0, 0.0, 0.0, 40.0, 30.0, 10.0], 12000.0,
                                          "ThroughAll target refresh")
        target_id = base_target["journal_id"]
        split_boxes = [[0.0, 0.0, 0.0, 19.0, 30.0, 10.0],
                       [21.0, 0.0, 0.0, 40.0, 30.0, 10.0]]
        cut_volume_total = 2.0 * 30.0 * 10.0
        target_volume_after_cut = 12000.0 - cut_volume_total
        split_case = add_case("F12_through_all_subtract_splits_target_into_two_bodies",
                              "Use a native ThroughAll cut that crosses the full target width and directly verify both returned solid fragments.",
                              {"target_volume_before_mm3": 12000.0,
                               "removed_volume_mm3": cut_volume_total,
                               "fragment_volume_mm3_each": 5700.0,
                               "fragment_boxes_mm": split_boxes})
        split_expected_count = len(before_cut.get("bodies", [])) + 1
        _, split_exec, _, split_part = extrude(
            "F12_through_all_subtract_split_geometry", split_sketch_id,
            {"mode": "subtract", "direction": "positive", "extent": "through_all",
             "start_offset_mm": -1.0, "target_body_id": target_id},
            5700.0, None, expected_body_count=split_expected_count,
            expected_profile_counts=_f12_profile_expectation(4, 4, 0, 1, 1, 0, 1),
            expect_split_boxes=split_boxes,
            profile_spec=split,
        )
        active_case = split_case
        after_cut_bodies = split_part.get("bodies", [])
        _same_body_state_before_and_after(before_cut.get("bodies", []), after_cut_bodies,
                                          {target_id},
                                          "ThroughAll split subtract non-target preservation")
        solid_fragments = [body for body in after_cut_bodies if isinstance(body, dict)
                           and body.get("is_solid") is True
                           and any(_boxes_match(body.get("bbox_mm"), box) for box in split_boxes)]
        if len(solid_fragments) != 2 or abs(sum(float(body["volume_mm3"]) for body in solid_fragments)
                                             - target_volume_after_cut) > VOLUME_ABSOLUTE_TOLERANCE_MM3:
            raise AssertionError("ThroughAll subtract fragment volumes do not sum to the independent target result")
        split_case.update(state="PASS", execution=_redact(split_exec), body_inventory_after=_redact(after_cut_bodies),
                          total_fragment_volume_mm3=sum(float(body["volume_mm3"]) for body in solid_fragments),
                          native_trim_type=split_exec.get("extrude_definition"))
        persist()

        # Unite a finite tool with the right fragment. Its independent overlap
        # is 5 x 10 x 5 = 250 mm^3; tool volume is 500 mm^3.
        right_fragment = _resolve_solid_body(after_cut_bodies, split_boxes[1], 5700.0,
                                             "right split fragment before finite Unite")
        unite = profiles["finite_unite_tool"]
        unite_sketch_id, _ = create_sketch(unite, "F12_finite_unite_profile")
        unite_target_id = right_fragment.get("journal_id")
        if not isinstance(unite_target_id, str) or not unite_target_id:
            raise AssertionError("right fragment omitted target_body_id")
        unite_expected_target_volume = 5700.0 + 500.0 - 250.0
        unite_case = add_case("F12_finite_unite_explicit_target", "Unite a finite offset prism with exactly the chosen split fragment.",
                              {"tool_volume_mm3": 500.0, "intersection_volume_mm3": 250.0,
                               "target_volume_after_mm3": unite_expected_target_volume,
                               "non_target_fragment_volume_mm3": 5700.0})
        before_unite = inspect_part()
        right_fragment = _resolve_solid_body(before_unite.get("bodies", []), split_boxes[1], 5700.0,
                                             "right split fragment target refresh before finite Unite")
        unite_target_id = right_fragment["journal_id"]
        unite_before_count = len(before_unite.get("bodies", []))
        _, unite_exec, _, after_unite = extrude(
            "F12_finite_unite_operation", unite_sketch_id,
            {"mode": "unite", "direction": "positive", "extent": "finite",
             "start_offset_mm": 0.0, "end_offset_mm": 5.0,
             "target_body_id": unite_target_id, "profile_output": "solid"},
            unite_expected_target_volume, [21.0, 0.0, 0.0, 45.0, 30.0, 10.0],
            expected_body_count=unite_before_count,
            expected_profile_counts=_f12_profile_expectation(4, 4, 0, 1, 1, 0, 1),
            expected_target_volume_delta_mm3=250.0,
            profile_spec=unite,
        )
        active_case = unite_case
        _same_body_state_before_and_after(before_unite.get("bodies", []), after_unite.get("bodies", []),
                                          {unite_target_id}, "finite unite non-target preservation")
        unite_case.update(state="PASS", execution=_redact(unite_exec),
                          body_inventory_after=_redact(after_unite.get("bodies")))
        persist()
        united_target = _resolve_solid_body(after_unite.get("bodies", []),
                                            [21.0, 0.0, 0.0, 45.0, 30.0, 10.0],
                                            unite_expected_target_volume,
                                            "united target after finite Unite")
        unite_target_id = united_target["journal_id"]
        target_id = unite_target_id

        sheet = profiles["sheet_open_line_arc_chain"]
        sheet_sketch_id, _ = create_sketch(sheet, "F12_sheet_output_profile")
        sheet_oracle = sheet["independent_oracle"]
        sheet_case = add_case("F12_explicit_sheet_from_open_line_arc_chain",
                              "Create a sheet by sweeping a connected open Arc/Line chain and directly check kind, bounds, edges, and solid flag.",
                              {"curve_length_mm": sheet_oracle["curve_length_mm"],
                               "analytic_surface_area_mm2": sheet_oracle["analytic_surface_area_mm2"],
                               "extrusion_length_mm": sheet_oracle["depth_mm"],
                               "expected_bbox_mm": [220.0, 0.0, 0.0, 235.0, 5.0, 4.0],
                               "volume": "not applicable for an open sheet body"})
        sheet_before_count = len(inspect_part().get("bodies", []))
        _, sheet_exec, _, sheet_part = extrude(
            "F12_sheet_output_open_chain_finite_create", sheet_sketch_id,
            {"mode": "create", "direction": "positive", "extent": "finite",
             "start_offset_mm": 0.0, "end_offset_mm": 4.0,
             "profile_output": "sheet"},
            None, [220.0, 0.0, 0.0, 235.0, 5.0, 4.0], expected_body_kind="SHEET_BODY",
            expected_body_count=sheet_before_count + 1,
            expected_profile_counts=_f12_profile_expectation(2, 2, 1, 0, 0, 0, 1),
            profile_spec=sheet,
        )
        active_case = sheet_case
        sheet_body_ids = sheet_exec.get("body_ids")
        if not isinstance(sheet_body_ids, list) or len(sheet_body_ids) != 1:
            raise AssertionError("explicit sheet request did not return exactly one body_id")
        sheet_id = sheet_body_ids[0]
        sheet_body = next((body for body in sheet_part.get("bodies", []) if isinstance(body, dict)
                           and body.get("journal_id") == sheet_id), None)
        if sheet_body is None or sheet_body.get("is_solid") is not False:
            raise AssertionError("explicit sheet request did not produce a non-solid SHEET_BODY")
        if not sheet_body.get("face_ids") or not sheet_body.get("edge_ids"):
            raise AssertionError("sheet body omitted direct face/edge inventory")
        if int(sheet_body.get("body_consistency_fault_count", -1)) != 0:
            raise AssertionError("NX reports body consistency faults on the sheet result")
        if sheet_body.get("volume_mm3") not in (None, 0, 0.0):
            raise AssertionError("sheet output reports nonzero solid volume")
        sheet_area_evidence = _assert_sheet_surface_area(
            sheet_exec, float(sheet_oracle["analytic_surface_area_mm2"]), "F12 sheet output")
        sheet_case.update(state="PASS", execution=_redact(sheet_exec),
                          sheet_body_direct_readback=_redact(sheet_body),
                          surface_area_measurement=sheet_area_evidence)
        persist()

        # Prepare failures are read-only and must create neither plan nor edit.
        open_sketch_id, _ = create_sketch(profiles["open_profile_rejection"], "F12_open_profile_created_for_rejection")
        expect_prepare_rejection("F12_open_solid_profile_rejected_read_only", open_sketch_id,
                                 {"mode": "create", "direction": "positive", "extent": "finite",
                                  "depth_mm": 3.0}, {"UNSUPPORTED_PROFILE"},
                                 profile_spec=profiles["open_profile_rejection"])
        expect_prepare_rejection("F12_sheet_boolean_combination_rejected_read_only", block_sketch_id,
                                 {"mode": "unite", "direction": "positive", "extent": "finite",
                                  "start_offset_mm": 0.0, "end_offset_mm": 2.0,
                                  "profile_output": "sheet", "target_body_id": unite_target_id},
                                 {"INVALID_REQUEST", "INVALID_ARGUMENT"}, expected_mcp_invalid_argument=True,
                                 profile_spec=block)
        expect_prepare_rejection("F12_depth_and_offset_conflict_rejected_read_only", block_sketch_id,
                                 {"mode": "create", "direction": "positive", "extent": "finite",
                                  "depth_mm": 2.0, "start_offset_mm": 0.0, "end_offset_mm": 2.0,
                                  "profile_output": "solid"},
                                 {"INVALID_REQUEST", "INVALID_ARGUMENT"}, expected_mcp_invalid_argument=True,
                                 profile_spec=block)
        expect_prepare_rejection("F12_through_all_create_rejected_read_only", block_sketch_id,
                                 {"mode": "create", "direction": "positive", "extent": "through_all",
                                  "profile_output": "solid"},
                                 {"INVALID_REQUEST", "INVALID_ARGUMENT"}, expected_mcp_invalid_argument=True,
                                 profile_spec=block)
        expect_prepare_rejection("F12_sheet_through_all_rejected_read_only", block_sketch_id,
                                 {"mode": "create", "direction": "positive", "extent": "through_all",
                                  "profile_output": "sheet"},
                                 {"INVALID_REQUEST", "INVALID_ARGUMENT"}, expected_mcp_invalid_argument=True,
                                 profile_spec=block)
        expect_prepare_rejection("F12_through_all_end_offset_rejected_read_only", block_sketch_id,
                                 {"mode": "subtract", "direction": "positive", "extent": "through_all",
                                  "start_offset_mm": 0.0, "end_offset_mm": 100.0,
                                  "target_body_id": target_id, "profile_output": "solid"},
                                 {"INVALID_REQUEST", "INVALID_ARGUMENT"}, expected_mcp_invalid_argument=True,
                                 profile_spec=block)
        expect_prepare_rejection("F12_nonincreasing_finite_offset_rejected_read_only", block_sketch_id,
                                 {"mode": "create", "direction": "positive", "extent": "finite",
                                  "start_offset_mm": 4.0, "end_offset_mm": 4.0,
                                  "profile_output": "solid"},
                                 {"INVALID_REQUEST", "INVALID_ARGUMENT"}, expected_mcp_invalid_argument=True,
                                 profile_spec=block)

        # SaveCopy/reopen exercises the installed path. Journal IDs may change,
        # so verify a one-to-one body signature mapping by kind, volume and box.
        before_save = inspect_part()
        before_signature = [_body_fingerprint(body) for body in before_save.get("bodies", [])
                            if isinstance(body, dict)]
        copy_path = Path(target_part).parent / ("F12-mcp-" + uuid.uuid4().hex + "-roundtrip.prt")
        if copy_path.exists():
            raise FileExistsError("unique F12 SaveCopy target already exists")
        save_case = add_case("F12_save_copy_reopen", "Save a copy, reopen only the copy, and compare direct body signatures and safe ownership state.",
                             {"source_part": target_part, "target_path": str(copy_path),
                              "body_count": len(before_signature)})
        save_terminal, saved, _ = operation_call("nx_save_copy", {
            "operation_id": "f12-save-" + uuid.uuid4().hex, **credentials,
            "target_path": str(copy_path), "reopen": True,
        }, "F12 SaveCopy/reopen")
        new_owner = saved.get("new_ownership_id")
        new_epoch = saved.get("new_binding_epoch")
        if not isinstance(new_owner, str) or not isinstance(new_epoch, int):
            raise AssertionError("nx_save_copy omitted new ownership credentials")
        credentials.update(ownership_id=new_owner, binding_epoch=new_epoch)
        if saved.get("saved") is not True or saved.get("reopened") is not True:
            raise AssertionError("nx_save_copy did not confirm saved=true and reopened=true")
        checks = saved.get("checks")
        if not isinstance(checks, dict) or set(checks) != {"associations", "expression", "geometry", "history"}:
            raise AssertionError("nx_save_copy omitted the four required roundtrip checks")
        if any(not isinstance(row, dict) or row.get("status") != "VERIFIED" for row in checks.values()):
            raise AssertionError("nx_save_copy contains a non-VERIFIED check")
        reopened = inspect_part()
        if _inspected_part_path(reopened).as_posix().casefold() != copy_path.resolve().as_posix().casefold():
            raise AssertionError("new binding did not reopen the requested saved-copy path")
        after_signature = [_body_fingerprint(body) for body in reopened.get("bodies", [])
                           if isinstance(body, dict)]
        _assert_body_signatures_roundtrip(before_signature, after_signature)
        save_case.update(state="PASS", save_copy_terminal=_redact(save_terminal),
                         save_copy_result=_redact(saved), reopened_part_inspection=_redact(reopened),
                         body_signatures_before=before_signature, body_signatures_after=after_signature)
        report["saved_path"] = saved.get("saved_path") or str(copy_path)
        persist()

        returned, _ = raw_call("nx_return", credentials)
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("nx_return did not confirm safe HUMAN_OWNED state")
        credentials.clear()
        status, _ = raw_call("nx_bridge_status", {})
        if (status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False
                or str(status.get("in_flight")) != "0"):
            raise AssertionError("final bridge status is not human-owned, idle, and writes-unhalted")
        report["safe_return"] = _redact(returned)
        report["final_bridge_status"] = _redact(status)
        save_case["safe_return"] = _redact(returned)
        save_case["final_bridge_status"] = _redact(status)
        report["passed"] = True
        return 0
    except Exception as exc:
        report["error"] = repr(exc)
        if active_case is None or active_case.get("state") != "RUNNING":
            active_case = {"case_id": "F12_installed_harness", "state": "RUNNING"}
            report["cases"].append(active_case)
        failed_case_id = active_case.get("case_id")
        record_failure(active_case, exc)
        for pending_case in report["cases"]:
            if pending_case is not active_case and pending_case.get("state") == "RUNNING":
                pending_case.update(state="SKIPPED", skip_reason=f"Harness stopped after failure in {failed_case_id}.",
                                    retest_criterion="Rerun this case on a new unique fixture after the blocking failure has been corrected.")
        persist()
        if credentials and client is not None and not lifecycle["writes_halted"]:
            try:
                status, _ = raw_call("nx_bridge_status", {})
                report["failure_status"] = _redact(status)
                if (status.get("ownership_state") == "AI_OWNED" and status.get("writes_halted") is False
                        and str(status.get("in_flight")) == "0"):
                    returned, _ = raw_call("nx_return", credentials)
                    report["failure_safe_return"] = _redact(returned)
                    credentials.clear()
                else:
                    report["failure_safe_return_skipped"] = "ownership/write gate is not a known idle AI_OWNED state"
            except Exception as return_error:
                report["failure_return_error"] = repr(return_error)
        elif lifecycle["writes_halted"]:
            report["failure_safe_return_skipped"] = "operation outcome UNKNOWN; no further ownership or write operation was attempted"
        return 1
    finally:
        report["completed_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        if client is not None:
            try:
                client.close()
            except Exception as close_error:
                report["client_close_error"] = repr(close_error)
        persist()
        print(json.dumps({key: report[key] for key in
                          ("passed", "mode", "server_version", "nx_pid", "part_path", "saved_path", "error")
                          if key in report}, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
