"""Installed MCP smoke and independent numeric helpers for F09.

Model mode requires a unique, saved, clean, standalone millimeter .prt under
``workspace/v2/models`` and the same part open as the NX Work Part. It creates
an isolated LINE/partial-ARC/full-circle sketch, checks four read-only pattern
previews, performs one integrated edit per mode, inspects native geometry, then
saves and reopens a new copy. It never starts or terminates NX or saves over the
supplied part. ``--ready`` checks the installed MCP and cached bridge status
without taking ownership or calling NX readiness/launch logic.

The production ``native_engine`` for linear, rectangular, and circular modes
is ``NXOpen.CurveCollection.CreateLine/CreateArc +
Sketch.AddGeometry(InferNoConstraints)`` (geometry-only copies, no associative
pattern feature); mirror uses ``NXOpen.SketchMirrorPatternBuilder``. Integrated
operation evidence retains the engine record for each mode.

Expected transforms below are calculated from the fixture request and sketch
frame; the production preview is evidence only and is never used as the oracle.
The smoke uses digital model values and does not establish physical accuracy,
reliability, or standards compliance.
"""
from __future__ import annotations

import argparse
import datetime
import importlib.util
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
EXPECTED_SERVER_VERSION = "0.9.0"
EXPECTED_TOOL_COUNT = 27
POSITION_TOLERANCE_MM = 1.0e-6
LENGTH_TOLERANCE_MM = 1.0e-6
ANGLE_TOLERANCE_RAD = 1.0e-7
OPERATION_TIMEOUT_SECONDS = 180.0
POLL_INTERVAL_SECONDS = 0.2
PRIVATE_KEY_PARTS = (
    "token", "secret", "authorization", "ownership_id", "credential",
    "api_key", "access_key", "private_key", "password",
)
TERMINAL_STATES = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
WRITE_OPERATION_TOOLS = {"nx_execute_plan", "nx_edit_sketch_pattern", "nx_save_copy"}
EXPECTED_TOOLS = {
    "nx_ensure_ready", "nx_get_capabilities", "nx_bridge_status", "nx_bridge_ping",
    "nx_takeover", "nx_return", "nx_get_session", "nx_inspect_part", "nx_query_faces",
    "nx_inspect_sketch", "nx_get_operation", "nx_cancel_operation", "nx_prepare_datum_plane",
    "nx_prepare_sketch", "nx_prepare_sketch_corner", "nx_edit_sketch_corner",
    "nx_prepare_sketch_offset", "nx_edit_sketch_offset", "nx_prepare_sketch_pattern",
    "nx_edit_sketch_pattern", "nx_prepare_extrude", "nx_prepare_thread",
    "nx_prepare_helical_groove", "nx_prepare_operation", "nx_execute_plan",
    "nx_verify_part", "nx_save_copy",
}
SKETCH_FRAME = {
    "origin_mm": [0.0, 0.0, 0.0],
    "normal": [0.0, 0.0, 1.0],
    "requested_x_axis": [1.0, 0.0, 0.0],
}
SEED_GEOMETRIES = [
    {"id": "f09-pattern-line", "type": "line", "start_mm": [20.0, 10.0], "end_mm": [26.0, 10.0]},
    {"id": "f09-pattern-partial-arc", "type": "arc", "center_mm": [20.0, 18.0],
     "radius_mm": 2.5, "start_angle_deg": 30.0, "sweep_deg": 100.0},
    {"id": "f09-pattern-full-circle", "type": "circle", "center_mm": [31.0, 17.0],
     "diameter_mm": 3.6},
    {"id": "f09-mirror-axis", "type": "line", "start_mm": [0.0, -20.0], "end_mm": [0.0, 60.0]},
    {"id": "f09-nontarget-line", "type": "line", "start_mm": [-50.0, 40.0], "end_mm": [-44.0, 40.0]},
    {"id": "f09-nontarget-arc", "type": "arc", "center_mm": [-50.0, 30.0],
     "radius_mm": 2.0, "start_angle_deg": 45.0, "sweep_deg": 80.0},
]
SOURCE_NAMES = ["f09-pattern-line", "f09-pattern-partial-arc", "f09-pattern-full-circle"]
MIRROR_AXIS_NAME = "f09-mirror-axis"
NON_TARGET_NAMES = ["f09-nontarget-line", "f09-nontarget-arc"]
PATTERN_CASES = [
    ("linear", {"direction_xy": [0.0, 1.0], "spacing_mm": 20.0, "instance_count": 3}),
    ("rectangular", {"direction_x_xy": [1.0, 0.0], "direction_y_xy": [0.0, 1.0],
                      "spacing_x_mm": 24.0, "spacing_y_mm": 13.0, "count_x": 2, "count_y": 2}),
    ("circular", {"center_xy": [0.0, 0.0], "angle_step_deg": -30.0, "instance_count": 3}),
    ("mirror", {"axis_curve_id": None}),
]
PATTERN_VERIFICATION_CHECKS = {
    "pattern_instances", "source_curves", "sketch_constraints", "sketch_frames",
    "reference_status", "non_target_geometry", "history", "bodies", "sketch_solver_status",
}


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


def _status_state(record: dict) -> str | None:
    state = record.get("state")
    if isinstance(state, str):
        return state
    operation = record.get("operation")
    if isinstance(operation, dict) and isinstance(operation.get("state"), str):
        return operation["state"]
    return None


def _operation_result(record: dict) -> dict | None:
    operation = record.get("operation") if isinstance(record.get("operation"), dict) else record
    result = operation.get("result") if isinstance(operation, dict) else None
    if isinstance(result, dict):
        return result
    result = record.get("result")
    return result if isinstance(result, dict) else None


def _canonical_path(path: str | Path) -> str:
    return Path(path).resolve(strict=False).as_posix().casefold()


def _file_metadata(path: Path) -> dict:
    stat = path.stat()
    return {
        "size_bytes": stat.st_size,
        "modified_time_utc": datetime.datetime.fromtimestamp(
            stat.st_mtime, datetime.timezone.utc).isoformat(),
    }


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
    if (not part_path.is_file() or part_path.suffix.casefold() != ".prt"
            or not part_path.is_relative_to(model_root)):
        raise ValueError("--part-path must be an existing saved .prt inside workspace/v2/models")
    if _canonical_path(part_path) == _canonical_path(output_path):
        raise ValueError("part path and evidence path must be different")
    report["part_path"] = str(part_path)
    report["source_part_file"] = {"before_save_copy": _file_metadata(part_path)}
    return part_path, output_path


def _int_value(value: object, label: str) -> int:
    if isinstance(value, bool):
        raise AssertionError(f"{label} must be an integer")
    try:
        return int(value)
    except (TypeError, ValueError, OverflowError) as exc:
        raise AssertionError(f"{label} must be an integer") from exc


def _tool_names_and_version(client: McpStdioClient, report: dict) -> list:
    initialize = client.request("initialize", {
        "protocolVersion": PROTOCOL_VERSION,
        "capabilities": {},
        "clientInfo": {"name": "f09-installed-mcp-smoke", "version": "1"},
    })
    report["initialize"] = _redact(initialize)
    client.notify("notifications/initialized")
    server_info = initialize.get("serverInfo")
    if not isinstance(server_info, dict) or server_info.get("version") != EXPECTED_SERVER_VERSION:
        actual = server_info.get("version") if isinstance(server_info, dict) else None
        raise AssertionError(f"installed MCP version must be {EXPECTED_SERVER_VERSION}; got {actual!r}")
    report["server_version"] = server_info["version"]
    listed = client.request("tools/list")
    tools = listed.get("tools")
    if not isinstance(tools, list):
        raise AssertionError("MCP tools/list omitted its tools array")
    names = [item.get("name") for item in tools if isinstance(item, dict)]
    if len(names) != EXPECTED_TOOL_COUNT or len(set(names)) != EXPECTED_TOOL_COUNT:
        raise AssertionError(
            f"installed MCP must expose exactly {EXPECTED_TOOL_COUNT} unique tools; got {len(set(names))}")
    if set(names) != EXPECTED_TOOLS:
        missing = sorted(EXPECTED_TOOLS - set(names))
        unexpected = sorted(set(names) - EXPECTED_TOOLS)
        raise AssertionError(f"installed F09 tool set differs; missing={missing!r}, unexpected={unexpected!r}")
    report["tool_count"] = len(names)
    report["tool_names"] = sorted(names)
    report["pattern_tool_schemas"] = _redact(_check_pattern_schemas(tools))
    report["tools_list"] = _redact(tools)
    return tools


def _seed_specs() -> dict[str, dict]:
    return {item["id"]: item for item in SEED_GEOMETRIES}


def _world_point(frame: dict, local_xy: object) -> list[float]:
    return point_to_world(frame, local_xy)


def _expected_seed_geometry(name: str, frame: dict) -> dict:
    spec = _seed_specs()[name]
    geometry_type = spec["type"]
    if geometry_type == "line":
        return {
            "kind": "LINE",
            "start_mm": _world_point(frame, spec["start_mm"]),
            "end_mm": _world_point(frame, spec["end_mm"]),
        }
    if geometry_type == "circle":
        center_xy = spec["center_mm"]
        radius = float(spec["diameter_mm"]) / 2.0
        start_deg, sweep_deg, full_circle = 0.0, 360.0, True
    else:
        center_xy = spec["center_mm"]
        radius = float(spec["radius_mm"])
        start_deg = float(spec["start_angle_deg"])
        sweep_deg = float(spec["sweep_deg"])
        full_circle = False
    samples = arc_sample_points(center_xy, radius, start_deg, sweep_deg)
    return {
        "kind": "ARC",
        "center_mm": _world_point(frame, center_xy),
        "radius_mm": radius,
        "start_mm": _world_point(frame, samples["start"]),
        "end_mm": _world_point(frame, samples["end"]),
        "midpoint_mm": _world_point(frame, samples["mid"]),
        "sweep_rad": math.radians(sweep_deg),
        "normal": frame["normal"],
        "full_circle": full_circle,
    }


def _local_transform(point: object, mode: str, fields: dict, position: tuple[int, int] | int) -> list[float]:
    if mode == "linear":
        return linear_position(point, fields["direction_xy"], fields["spacing_mm"], int(position))
    if mode == "rectangular":
        index_x, index_y = position if isinstance(position, tuple) else (int(position), 0)
        return rectangular_position(
            point, fields["direction_x_xy"], fields["direction_y_xy"],
            fields["spacing_x_mm"], fields["spacing_y_mm"], index_x, index_y,
        )
    if mode == "circular":
        return rotate_about(point, fields["center_xy"], float(fields["angle_step_deg"]) * int(position))
    axis = _seed_specs()[MIRROR_AXIS_NAME]
    axis_direction = [axis["end_mm"][index] - axis["start_mm"][index] for index in range(2)]
    return reflect_across_line(point, axis["start_mm"], axis_direction)


def _transformed_expected_geometry(name: str, mode: str, fields: dict,
                                   position: tuple[int, int] | int, frame: dict) -> dict:
    spec = _seed_specs()[name]
    if spec["type"] == "line":
        start = _local_transform(spec["start_mm"], mode, fields, position)
        end = _local_transform(spec["end_mm"], mode, fields, position)
        return {"kind": "LINE", "start_mm": _world_point(frame, start), "end_mm": _world_point(frame, end)}

    if spec["type"] == "circle":
        center = spec["center_mm"]
        radius = float(spec["diameter_mm"]) / 2.0
        start_deg, sweep_deg, full_circle = 0.0, 360.0, True
    else:
        center = spec["center_mm"]
        radius = float(spec["radius_mm"])
        start_deg = float(spec["start_angle_deg"])
        sweep_deg = float(spec["sweep_deg"])
        full_circle = False
    samples = arc_sample_points(center, radius, start_deg, sweep_deg)
    transformed_center = _local_transform(center, mode, fields, position)
    transformed_start = _local_transform(samples["start"], mode, fields, position)
    transformed_end = _local_transform(samples["end"], mode, fields, position)
    transformed_mid = _local_transform(samples["mid"], mode, fields, position)
    if mode == "mirror":
        sweep_deg = -sweep_deg
    return {
        "kind": "ARC",
        "center_mm": _world_point(frame, transformed_center),
        "radius_mm": radius,
        "start_mm": _world_point(frame, transformed_start),
        "end_mm": _world_point(frame, transformed_end),
        "midpoint_mm": _world_point(frame, transformed_mid),
        "sweep_rad": math.radians(sweep_deg),
        "normal": frame["normal"],
        "full_circle": full_circle,
    }


def _pattern_positions(mode: str, fields: dict) -> list[tuple[str, tuple[int, int] | int]]:
    if mode == "linear":
        return [(str(index), index) for index in range(1, int(fields["instance_count"]))]
    if mode == "rectangular":
        return [
            (f"{index_x},{index_y}", (index_x, index_y))
            for index_y in range(int(fields["count_y"]))
            for index_x in range(int(fields["count_x"]))
            if index_x != 0 or index_y != 0
        ]
    if mode == "circular":
        return [(str(index), index) for index in range(1, int(fields["instance_count"]))]
    return [("mirror", 1)]


def _pattern_expected_outputs(mode: str, fields: dict, frame: dict) -> list[dict]:
    expected = []
    for position_label, position in _pattern_positions(mode, fields):
        for source_name in SOURCE_NAMES:
            item = _transformed_expected_geometry(source_name, mode, fields, position, frame)
            item["source_name"] = source_name
            item["position"] = position_label
            expected.append(item)
    return expected


def _geometry_by_journal_id(sketch: dict, label: str) -> dict[str, dict]:
    records = sketch.get("geometry")
    if not isinstance(records, list):
        raise AssertionError(f"{label} nx_inspect_sketch omitted geometry")
    found: dict[str, dict] = {}
    for index, record in enumerate(records):
        if not isinstance(record, dict):
            raise AssertionError(f"{label} geometry record {index} is not an object")
        journal_id = record.get("journal_id")
        if not isinstance(journal_id, str) or not journal_id:
            raise AssertionError(f"{label} geometry record {index} omitted journal_id")
        if journal_id in found:
            raise AssertionError(f"{label} contains duplicate journal_id {journal_id!r}")
        found[journal_id] = record
    return found


def _check_sketch_frame_and_units(sketch: dict, frame: dict, label: str) -> None:
    if str(sketch.get("units", "")).casefold() not in {"millimeters", "millimeter", "mm"}:
        raise AssertionError(f"{label} sketch units must be millimeters")
    for key, expected in (("origin", frame["origin_mm"]), ("normal", frame["normal"]),
                          ("x_axis", frame["x_axis"]), ("y_axis", frame["y_axis"])):
        actual = _finite_vector(sketch.get(key), 3, f"{label} {key}")
        if _distance3(actual, expected) > POSITION_TOLERANCE_MM:
            raise AssertionError(f"{label} {key} differs from the requested sketch frame")


def _check_sketch_geometry(sketch: dict, frame: dict, expected_records: list[dict],
                           label: str) -> dict[str, dict]:
    _check_sketch_frame_and_units(sketch, frame, label)
    actual = sketch.get("geometry")
    if not isinstance(actual, list):
        raise AssertionError(f"{label} nx_inspect_sketch omitted geometry")
    match = match_geometry_records(actual, expected_records)
    by_id = _geometry_by_journal_id(sketch, label)
    return {"geometry_match": match, "geometry_by_id": by_id}


def _expected_from_native_geometry(record: dict, label: str) -> dict:
    if not isinstance(record, dict):
        raise AssertionError(f"{label} geometry record is not an object")
    kind = str(record.get("kind", "")).upper()
    expected = {
        "kind": kind,
        "start_mm": record.get("start"),
        "end_mm": record.get("end"),
    }
    if kind == "LINE":
        expected["normal"] = record.get("normal")
        return expected
    if kind != "ARC":
        raise AssertionError(f"{label} has unsupported existing geometry kind {kind!r}")
    try:
        sweep = float(record["end_angle_rad"]) - float(record["start_angle_rad"])
        expected.update({
            "center_mm": record["center"],
            "radius_mm": record["radius_mm"],
            "sweep_rad": sweep,
            "normal": record["normal"],
            "full_circle": abs(abs(sweep) - 2.0 * math.pi) <= ANGLE_TOLERANCE_RAD,
        })
        expected["midpoint_mm"] = _actual_arc_midpoint(record)
    except (KeyError, TypeError, ValueError, OverflowError) as exc:
        raise AssertionError(f"{label} Arc record omitted finite native extent data") from exc
    return expected


def _match_existing_geometry(before: dict, after: dict, label: str) -> dict:
    before_records = before.get("geometry")
    after_records = after.get("geometry")
    if not isinstance(before_records, list) or not isinstance(after_records, list):
        raise AssertionError(f"{label} inspection omitted geometry records")
    if len(after_records) < len(before_records):
        raise AssertionError(f"{label} removed existing native geometry")
    before_ids = _geometry_by_journal_id(before, label + " before")
    _geometry_by_journal_id(after, label + " after")

    preserved = []
    matched_after = set()
    for before_index, prior in enumerate(before_records):
        expected = _expected_from_native_geometry(prior, f"{label} before")
        candidates = [
            index for index, current in enumerate(after_records)
            if geometry_record_matches(current, expected)
        ]
        if not candidates:
            raise AssertionError(
                f"{label} changed or removed existing native geometry {prior.get('journal_id')!r}"
            )
        if len(candidates) != 1:
            raise AssertionError(
                f"{label} has ambiguous geometric identity for existing native geometry "
                f"{prior.get('journal_id')!r}: {len(candidates)} candidates"
            )
        after_index = candidates[0]
        if after_index in matched_after:
            raise AssertionError(f"{label} reused one native record for multiple existing curves")
        matched_after.add(after_index)
        current = after_records[after_index]
        before_id = prior["journal_id"]
        after_id = current["journal_id"]
        preserved.append({
            "before_journal_id": before_id,
            "after_journal_id": after_id,
            "before_geometry_index": before_index,
            "after_geometry_index": after_index,
        })

    new_geometry = [record for index, record in enumerate(after_records) if index not in matched_after]
    return {
        "preserved_geometry_journal_id_mapping": preserved,
        "new_geometry": new_geometry,
        "old_geometry_count": len(before_ids),
        "current_geometry_count": len(after_records),
    }


def _assert_same_existing_geometry(before: dict, after: dict, label: str,
                                   allow_journal_id_reassignment: bool = False) -> dict:
    before_by_id = _geometry_by_journal_id(before, label + " before")
    after_by_id = _geometry_by_journal_id(after, label + " after")
    if allow_journal_id_reassignment:
        return _match_existing_geometry(before, after, label)

    missing = sorted(set(before_by_id) - set(after_by_id))
    added = sorted(set(after_by_id) - set(before_by_id))
    if missing or added:
        raise AssertionError(
            f"{label} changed JournalIdentifiers during a read-only comparison; "
            f"missing={missing!r}, added={added!r}"
        )
    preserved = []
    for journal_id, prior in before_by_id.items():
        current = after_by_id[journal_id]
        if not geometry_record_matches(current, _expected_from_native_geometry(prior, label + " before")):
            raise AssertionError(f"{label} changed existing native geometry {journal_id!r}")
        preserved.append({"before_journal_id": journal_id, "after_journal_id": journal_id})
    return {
        "preserved_geometry_journal_id_mapping": preserved,
        "new_geometry": [],
        "old_geometry_count": len(before_by_id),
        "current_geometry_count": len(after_by_id),
    }


def _resolve_current_seed_journal_ids(sketch: dict, geometry_match: dict,
                                     seed_names: list[str], label: str) -> dict[str, str]:
    actual_records = sketch.get("geometry")
    expected_to_actual = geometry_match.get("expected_to_actual")
    candidate_matches = geometry_match.get("candidate_matches")
    if (not isinstance(actual_records, list) or not isinstance(expected_to_actual, dict)
            or not isinstance(candidate_matches, list)):
        raise AssertionError(f"{label} omitted one-to-one geometry identity mapping")
    resolved = {}
    used_ids = set()
    for expected_index, seed_name in enumerate(seed_names):
        actual_index = expected_to_actual.get(expected_index)
        candidates = candidate_matches[expected_index] if expected_index < len(candidate_matches) else None
        if not isinstance(candidates, list) or len(candidates) != 1:
            raise AssertionError(f"{label} has ambiguous geometric identity for seed {seed_name!r}")
        if not isinstance(actual_index, int) or actual_index < 0 or actual_index >= len(actual_records):
            raise AssertionError(f"{label} could not geometrically resolve seed {seed_name!r}")
        if actual_index != candidates[0]:
            raise AssertionError(f"{label} one-to-one assignment disagrees for seed {seed_name!r}")
        journal_id = actual_records[actual_index].get("journal_id")
        if not isinstance(journal_id, str) or not journal_id:
            raise AssertionError(f"{label} seed {seed_name!r} omitted its current JournalIdentifier")
        if journal_id in used_ids:
            raise AssertionError(f"{label} resolved multiple seeds to JournalIdentifier {journal_id!r}")
        used_ids.add(journal_id)
        resolved[seed_name] = journal_id
    return resolved


def _require_verified_pattern_result(result: dict, mode: str, sketch_id: str,
                                     source_curve_ids: list[str], position_count: int,
                                     expected_output_count: int) -> dict:
    if result.get("kind") != "edit_sketch_pattern":
        raise AssertionError("nx_edit_sketch_pattern returned an unexpected operation kind")
    if result.get("combined_edit") is not True or result.get("mutation_state") != "APPLIED":
        raise AssertionError("nx_edit_sketch_pattern did not confirm one applied combined edit")
    if result.get("verified") is not True:
        raise AssertionError("nx_edit_sketch_pattern operation result is not verified")
    if result.get("mode") != mode:
        raise AssertionError("nx_edit_sketch_pattern result mode differs from the request")
    if result.get("sketch_id") != sketch_id or result.get("source_curve_ids") != source_curve_ids:
        raise AssertionError("nx_edit_sketch_pattern result changed its sketch or source selection")
    if (result.get("expected_pattern_instance_count") != position_count
            or result.get("expected_output_curve_count") != expected_output_count):
        raise AssertionError("nx_edit_sketch_pattern result output count differs from the request")
    verification = result.get("verification")
    if not isinstance(verification, dict) or verification.get("verified") is not True:
        raise AssertionError("nx_edit_sketch_pattern omitted successful independent verification")
    checks = verification.get("checks")
    if not isinstance(checks, dict) or set(checks) != PATTERN_VERIFICATION_CHECKS:
        raise AssertionError("nx_edit_sketch_pattern omitted required pattern verification checks")
    bad_checks = [name for name in PATTERN_VERIFICATION_CHECKS
                  if not isinstance(checks.get(name), dict) or checks[name].get("status") != "VERIFIED"]
    if bad_checks:
        raise AssertionError(f"nx_edit_sketch_pattern has non-VERIFIED checks: {sorted(bad_checks)!r}")
    return checks


def _finite_vector(value: object, size: int, label: str) -> list[float]:
    if not isinstance(value, (list, tuple)) or len(value) != size:
        raise ValueError(f"{label} must contain {size} coordinates")
    try:
        result = [float(component) for component in value]
    except (TypeError, ValueError, OverflowError) as exc:
        raise ValueError(f"{label} must contain finite numbers") from exc
    if not all(math.isfinite(component) for component in result):
        raise ValueError(f"{label} must contain finite numbers")
    return result


def _dot(left: list[float], right: list[float]) -> float:
    return sum(a * b for a, b in zip(left, right))


def _cross(left: list[float], right: list[float]) -> list[float]:
    return [
        left[1] * right[2] - left[2] * right[1],
        left[2] * right[0] - left[0] * right[2],
        left[0] * right[1] - left[1] * right[0],
    ]


def _normalize(value: list[float], label: str) -> list[float]:
    scale = max(abs(component) for component in value)
    if not math.isfinite(scale) or scale <= 0.0:
        raise ValueError(f"{label} must be nonzero and finite")
    scaled = [component / scale for component in value]
    scaled_norm = math.sqrt(_dot(scaled, scaled))
    if not math.isfinite(scaled_norm) or scaled_norm <= 0.0:
        raise ValueError(f"{label} must be nonzero and finite")
    return [component / scaled_norm for component in scaled]


def build_frame(origin_mm: object, normal: object, requested_x_axis: object) -> dict:
    """Build a right-handed orthonormal local sketch frame independently."""
    origin = _finite_vector(origin_mm, 3, "origin_mm")
    n = _normalize(_finite_vector(normal, 3, "normal"), "normal")
    requested_x = _finite_vector(requested_x_axis, 3, "requested_x_axis")
    projection = _dot(requested_x, n)
    projected_x = [requested_x[index] - projection * n[index] for index in range(3)]
    x_axis = _normalize(projected_x, "requested_x_axis projected into the sketch plane")
    y_axis = _normalize(_cross(n, x_axis), "sketch y axis")
    return {"origin_mm": origin, "normal": n, "x_axis": x_axis, "y_axis": y_axis}


def point_to_world(frame: dict, local_xy: object) -> list[float]:
    """Convert a local millimetre point to world coordinates from the saved frame."""
    x, y = _finite_vector(local_xy, 2, "local point")
    origin = _finite_vector(frame["origin_mm"], 3, "frame origin")
    axis_x = _finite_vector(frame["x_axis"], 3, "frame x axis")
    axis_y = _finite_vector(frame["y_axis"], 3, "frame y axis")
    return [origin[k] + x * axis_x[k] + y * axis_y[k] for k in range(3)]


def linear_position(local_xy: object, direction_xy: object, spacing_mm: float, index: int) -> list[float]:
    point = _finite_vector(local_xy, 2, "local point")
    direction = _normalize(_finite_vector(direction_xy, 2, "direction_xy"), "direction_xy")
    spacing = float(spacing_mm)
    if not math.isfinite(spacing) or spacing <= 0.0 or index < 0:
        raise ValueError("linear spacing must be positive and index must be nonnegative")
    return _finite_vector([point[k] + direction[k] * spacing * index for k in range(2)], 2,
                          "derived linear instance")


def rectangular_position(local_xy: object, direction_x_xy: object, direction_y_xy: object,
                         spacing_x_mm: float, spacing_y_mm: float, index_x: int, index_y: int) -> list[float]:
    point = _finite_vector(local_xy, 2, "local point")
    dx = _normalize(_finite_vector(direction_x_xy, 2, "direction_x_xy"), "direction_x_xy")
    dy = _normalize(_finite_vector(direction_y_xy, 2, "direction_y_xy"), "direction_y_xy")
    if abs(dx[0] * dy[0] + dx[1] * dy[1]) > 1.0e-12:
        raise ValueError("rectangular directions must be orthogonal")
    sx, sy = float(spacing_x_mm), float(spacing_y_mm)
    if not math.isfinite(sx) or not math.isfinite(sy) or sx <= 0.0 or sy <= 0.0:
        raise ValueError("rectangular spacings must be positive and finite")
    if index_x < 0 or index_y < 0:
        raise ValueError("rectangular indices must be nonnegative")
    return _finite_vector(
        [point[k] + dx[k] * sx * index_x + dy[k] * sy * index_y for k in range(2)], 2,
        "derived rectangular instance",
    )


def rotate_about(local_xy: object, center_xy: object, angle_deg: float) -> list[float]:
    point = _finite_vector(local_xy, 2, "local point")
    center = _finite_vector(center_xy, 2, "center_xy")
    degrees = float(angle_deg)
    if not math.isfinite(degrees):
        raise ValueError("angle must be finite")
    angle = math.radians(math.fmod(degrees, 360.0))
    cosine, sine = math.cos(angle), math.sin(angle)
    x, y = point[0] - center[0], point[1] - center[1]
    return [center[0] + cosine * x - sine * y, center[1] + sine * x + cosine * y]


def reflect_across_line(local_xy: object, axis_point_xy: object, axis_direction_xy: object) -> list[float]:
    point = _finite_vector(local_xy, 2, "local point")
    axis_point = _finite_vector(axis_point_xy, 2, "axis point")
    axis = _normalize(_finite_vector(axis_direction_xy, 2, "axis direction"), "axis direction")
    relative = [point[k] - axis_point[k] for k in range(2)]
    along = relative[0] * axis[0] + relative[1] * axis[1]
    projection = [axis[k] * along for k in range(2)]
    return [axis_point[k] + 2.0 * projection[k] - relative[k] for k in range(2)]


def arc_sample_points(center_xy: object, radius_mm: float, start_angle_deg: float,
                      sweep_deg: float) -> dict[str, list[float]]:
    center = _finite_vector(center_xy, 2, "arc center")
    radius, start, sweep = float(radius_mm), float(start_angle_deg), float(sweep_deg)
    if not all(math.isfinite(value) for value in (radius, start, sweep)) or radius <= 0.0:
        raise ValueError("arc radius and angles must be finite; radius must be positive")
    def at(degrees: float) -> list[float]:
        angle = math.radians(degrees)
        return [center[0] + radius * math.cos(angle), center[1] + radius * math.sin(angle)]
    return {"start": at(start), "mid": at(start + sweep / 2.0), "end": at(start + sweep)}


def arc_signature_matches(candidate: dict, expected: dict,
                          position_tolerance_mm: float = POSITION_TOLERANCE_MM,
                          angle_tolerance_rad: float = ANGLE_TOLERANCE_RAD) -> bool:
    """Match an Arc by center, radius, endpoints, and an interior point.

    Checking an interior point distinguishes the requested arc from its
    complementary arc when center, radius, and endpoints happen to match.
    `candidate` and `expected` use local XY coordinates and degree angles.
    """
    try:
        center = _finite_vector(candidate["center_xy"], 2, "candidate center")
        expected_center = _finite_vector(expected["center_xy"], 2, "expected center")
        radius = float(candidate["radius_mm"])
        expected_radius = float(expected["radius_mm"])
        start = float(candidate["start_angle_deg"])
        sweep = float(candidate["sweep_deg"])
        expected_sweep = float(expected["sweep_deg"])
        samples = expected["sample_points_xy"]
    except (KeyError, TypeError, ValueError, OverflowError):
        return False
    if not all(math.isfinite(value) for value in (radius, expected_radius, start, sweep, expected_sweep)):
        return False
    angular_tolerance_deg = math.degrees(angle_tolerance_rad)
    if (radius <= 0.0 or expected_radius <= 0.0 or abs(sweep) <= angular_tolerance_deg
            or abs(expected_sweep) <= angular_tolerance_deg
            or abs(sweep) > 360.0 + angular_tolerance_deg
            or abs(expected_sweep) > 360.0 + angular_tolerance_deg
            or abs(abs(sweep) - abs(expected_sweep)) > angular_tolerance_deg):
        return False
    if _distance2(center, expected_center) > position_tolerance_mm:
        return False
    if abs(radius - expected_radius) > position_tolerance_mm:
        return False
    if not all(_point_on_arc(point, center, radius, start, sweep, position_tolerance_mm,
                             angle_tolerance_rad) for point in samples.values()):
        return False
    if abs(expected_sweep) >= 360.0 - angular_tolerance_deg:
        return abs(sweep) >= 360.0 - angular_tolerance_deg
    candidate_ends = [arc_sample_points(center, radius, start, sweep)[key]
                      for key in ("start", "end")]
    expected_ends = [samples["start"], samples["end"]]
    return (
        (_distance2(candidate_ends[0], expected_ends[0]) <= position_tolerance_mm and
         _distance2(candidate_ends[1], expected_ends[1]) <= position_tolerance_mm) or
        (_distance2(candidate_ends[0], expected_ends[1]) <= position_tolerance_mm and
         _distance2(candidate_ends[1], expected_ends[0]) <= position_tolerance_mm)
    )


def _point_on_arc(point: object, center: list[float], radius: float, start_deg: float,
                  sweep_deg: float, position_tolerance_mm: float, angle_tolerance_rad: float) -> bool:
    target = _finite_vector(point, 2, "expected arc sample")
    if abs(_distance2(target, center) - radius) > position_tolerance_mm:
        return False
    if abs(sweep_deg) >= 360.0 - math.degrees(angle_tolerance_rad):
        return True
    theta = math.degrees(math.atan2(target[1] - center[1], target[0] - center[0]))
    angular_tolerance_deg = math.degrees(angle_tolerance_rad)
    if sweep_deg >= 0.0:
        delta = (theta - start_deg) % 360.0
        if min(delta, 360.0 - delta) <= angular_tolerance_deg:
            delta = 0.0
        return delta <= sweep_deg + angular_tolerance_deg
    delta = -((start_deg - theta) % 360.0)
    if min(abs(delta), 360.0 - abs(delta)) <= angular_tolerance_deg:
        delta = 0.0
    return delta >= sweep_deg - angular_tolerance_deg


def _distance2(first: object, second: object) -> float:
    a = _finite_vector(first, 2, "first point")
    b = _finite_vector(second, 2, "second point")
    return math.hypot(a[0] - b[0], a[1] - b[1])


def geometry_record_matches(actual: dict, expected: dict,
                            position_tolerance_mm: float = POSITION_TOLERANCE_MM,
                            angle_tolerance_rad: float = ANGLE_TOLERANCE_RAD) -> bool:
    """Compare one native sketch record with an independently built world-space oracle.

    Expected record keys are `kind`, `start_mm`, `end_mm`, optional `center_mm`,
    `radius_mm`, `normal`, `sweep_rad`, `midpoint_mm`, and `full_circle`. Actual
    records follow nx_inspect_sketch (`kind`, `start`, `end`, `center`, angle
    limits, radius, and normal). Geometry type, plane, arc extent and interior
    point all participate; endpoint equality alone cannot accept a complement.
    """
    if not isinstance(actual, dict) or not isinstance(expected, dict):
        return False
    actual_kind = str(actual.get("kind", "")).upper()
    expected_kind = str(expected.get("kind", "")).upper()
    if actual_kind != expected_kind:
        return False
    try:
        if expected_kind == "LINE":
            a0 = _finite_vector(actual.get("start"), 3, "actual line start")
            a1 = _finite_vector(actual.get("end"), 3, "actual line end")
            e0 = _finite_vector(expected.get("start_mm"), 3, "expected line start")
            e1 = _finite_vector(expected.get("end_mm"), 3, "expected line end")
            if abs(_distance3(a0, a1) - _distance3(e0, e1)) > LENGTH_TOLERANCE_MM:
                return False
            has_normal = actual.get("normal") is not None or expected.get("normal") is not None
            normal_matches = (not has_normal or
                              _same_plane_normal(actual.get("normal"), expected.get("normal"), angle_tolerance_rad))
            return (
                (_distance3(a0, e0) <= position_tolerance_mm and _distance3(a1, e1) <= position_tolerance_mm)
                or (_distance3(a0, e1) <= position_tolerance_mm and _distance3(a1, e0) <= position_tolerance_mm)
            ) and normal_matches
        if expected_kind != "ARC":
            return False
        actual_center = _finite_vector(actual.get("center"), 3, "actual arc center")
        expected_center = _finite_vector(expected.get("center_mm"), 3, "expected arc center")
        actual_radius = float(actual["radius_mm"])
        expected_radius = float(expected["radius_mm"])
        actual_start = _finite_vector(actual.get("start"), 3, "actual arc start")
        actual_end = _finite_vector(actual.get("end"), 3, "actual arc end")
        expected_start = _finite_vector(expected.get("start_mm"), 3, "expected arc start")
        expected_end = _finite_vector(expected.get("end_mm"), 3, "expected arc end")
        start_angle = float(actual["start_angle_rad"])
        end_angle = float(actual["end_angle_rad"])
        actual_sweep = end_angle - start_angle
        expected_sweep = float(expected["sweep_rad"])
        if not all(math.isfinite(value) for value in (
            actual_radius, expected_radius, start_angle, end_angle, actual_sweep, expected_sweep,
        )):
            return False
        if actual_radius <= 0.0 or expected_radius <= 0.0:
            return False
        if abs(actual_sweep) <= angle_tolerance_rad or abs(actual_sweep) > 2.0 * math.pi + angle_tolerance_rad:
            return False
        if abs(expected_sweep) <= angle_tolerance_rad or abs(expected_sweep) > 2.0 * math.pi + angle_tolerance_rad:
            return False
        if (_distance3(actual_center, expected_center) > position_tolerance_mm
                or abs(actual_radius - expected_radius) > position_tolerance_mm
                or abs(abs(actual_sweep) - abs(expected_sweep)) > angle_tolerance_rad
                or not _same_plane_normal(actual.get("normal"), expected.get("normal"), angle_tolerance_rad)):
            return False
        actual_length = actual_radius * abs(actual_sweep)
        expected_length = expected_radius * abs(expected_sweep)
        if abs(actual_length - expected_length) > LENGTH_TOLERANCE_MM:
            return False
        full_circle = bool(expected.get("full_circle"))
        if full_circle:
            return abs(abs(actual_sweep) - 2.0 * math.pi) <= angle_tolerance_rad
        if not ((_distance3(actual_start, expected_start) <= position_tolerance_mm
                 and _distance3(actual_end, expected_end) <= position_tolerance_mm)
                or (_distance3(actual_start, expected_end) <= position_tolerance_mm
                    and _distance3(actual_end, expected_start) <= position_tolerance_mm)):
            return False
        midpoint = _finite_vector(expected.get("midpoint_mm"), 3, "expected arc midpoint")
        if _distance3(_actual_arc_midpoint(actual), midpoint) > position_tolerance_mm:
            return False
        return _native_arc_contains(actual, midpoint, position_tolerance_mm, angle_tolerance_rad)
    except (KeyError, TypeError, ValueError, OverflowError):
        return False


def intrinsic_length_evidence(actual: dict, expected: dict) -> dict:
    """Return independently calculated curve length and residual in millimetres."""
    kind = str(expected.get("kind", "")).upper()
    if kind == "LINE":
        actual_length = _distance3(actual["start"], actual["end"])
        expected_length = _distance3(expected["start_mm"], expected["end_mm"])
    elif kind == "ARC":
        actual_radius = float(actual["radius_mm"])
        actual_sweep = float(actual["end_angle_rad"]) - float(actual["start_angle_rad"])
        expected_radius = float(expected["radius_mm"])
        expected_sweep = float(expected["sweep_rad"])
        actual_length = actual_radius * abs(actual_sweep)
        expected_length = expected_radius * abs(expected_sweep)
    else:
        raise ValueError("intrinsic length is defined only for Line and circular Arc records")
    return {
        "actual_length_mm": actual_length,
        "expected_length_mm": expected_length,
        "residual_mm": actual_length - expected_length,
    }


def match_geometry_records(actual_records: list, expected_records: list,
                           position_tolerance_mm: float = POSITION_TOLERANCE_MM,
                           angle_tolerance_rad: float = ANGLE_TOLERANCE_RAD) -> dict:
    """Require a one-to-one geometry match; no actual object may be reused."""
    if not isinstance(actual_records, list) or not isinstance(expected_records, list):
        raise AssertionError("actual and expected geometry must both be lists")
    if len(actual_records) != len(expected_records):
        raise AssertionError(
            f"geometry count mismatch: expected {len(expected_records)}, got {len(actual_records)}"
        )
    candidates = [
        [index for index, actual in enumerate(actual_records)
         if geometry_record_matches(actual, expected, position_tolerance_mm, angle_tolerance_rad)]
        for expected in expected_records
    ]
    assigned_actual: dict[int, int] = {}

    def assign(expected_index: int, seen: set[int]) -> bool:
        for actual_index in candidates[expected_index]:
            if actual_index in seen:
                continue
            seen.add(actual_index)
            if actual_index not in assigned_actual or assign(assigned_actual[actual_index], seen):
                assigned_actual[actual_index] = expected_index
                return True
        return False

    for expected_index in range(len(expected_records)):
        if not assign(expected_index, set()):
            raise AssertionError(f"expected geometry item {expected_index} has no unique native match")
    expected_to_actual = {expected_index: actual_index for actual_index, expected_index in assigned_actual.items()}
    return {"expected_to_actual": expected_to_actual, "candidate_matches": candidates}


def _distance3(first: object, second: object) -> float:
    a = _finite_vector(first, 3, "first world point")
    b = _finite_vector(second, 3, "second world point")
    return math.sqrt(sum((a[index] - b[index]) ** 2 for index in range(3)))


def _same_plane_normal(actual: object, expected: object, angle_tolerance_rad: float) -> bool:
    try:
        a = _normalize(_finite_vector(actual, 3, "actual plane normal"), "actual plane normal")
        e = _normalize(_finite_vector(expected, 3, "expected plane normal"), "expected plane normal")
    except (TypeError, ValueError, OverflowError):
        return False
    cosine = min(1.0, max(-1.0, abs(_dot(a, e))))
    sine = math.sqrt(sum(value * value for value in _cross(a, e)))
    return math.atan2(sine, cosine) <= angle_tolerance_rad


def _actual_arc_midpoint(record: dict) -> list[float]:
    center = _finite_vector(record["center"], 3, "arc center")
    start = _finite_vector(record["start"], 3, "arc start")
    normal = _normalize(_finite_vector(record["normal"], 3, "arc normal"), "arc normal")
    radius = float(record["radius_mm"])
    sweep = float(record["end_angle_rad"]) - float(record["start_angle_rad"])
    radial = _normalize([start[index] - center[index] for index in range(3)], "arc radial vector")
    tangent = _cross(normal, radial)
    half = sweep / 2.0
    return [center[index] + radius * (math.cos(half) * radial[index] + math.sin(half) * tangent[index])
            for index in range(3)]


def _native_arc_contains(record: dict, point: list[float], position_tolerance_mm: float,
                         angle_tolerance_rad: float) -> bool:
    center = _finite_vector(record["center"], 3, "arc center")
    start = _finite_vector(record["start"], 3, "arc start")
    normal = _normalize(_finite_vector(record["normal"], 3, "arc normal"), "arc normal")
    radius = float(record["radius_mm"])
    sweep = float(record["end_angle_rad"]) - float(record["start_angle_rad"])
    radial_start = _normalize([start[index] - center[index] for index in range(3)], "arc radial vector")
    radial_target = [point[index] - center[index] for index in range(3)]
    target_radius = math.sqrt(_dot(radial_target, radial_target))
    if abs(target_radius - radius) > position_tolerance_mm:
        return False
    radial_target = _normalize(radial_target, "target radial vector")
    cross = _cross(radial_start, radial_target)
    delta = math.atan2(_dot(normal, cross), _dot(radial_start, radial_target))
    if sweep >= 0.0:
        if delta < -angle_tolerance_rad:
            delta += 2.0 * math.pi
        return delta <= sweep + angle_tolerance_rad
    if delta > angle_tolerance_rad:
        delta -= 2.0 * math.pi
    return delta >= sweep - angle_tolerance_rad


def _redact(value: object) -> object:
    if isinstance(value, dict):
        sanitized = {}
        for key, child in value.items():
            lowered = str(key).casefold()
            sanitized[key] = ("[REDACTED]" if any(part in lowered for part in PRIVATE_KEY_PARTS)
                              else _redact(child))
        return sanitized
    if isinstance(value, list):
        return [_redact(child) for child in value]
    if isinstance(value, tuple):
        return [_redact(child) for child in value]
    if isinstance(value, str):
        try:
            decoded = json.loads(value)
        except (TypeError, ValueError):
            return value
        if isinstance(decoded, (dict, list)):
            return json.dumps(_redact(decoded), ensure_ascii=False, separators=(",", ":"))
    return value


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


def _check_pattern_schemas(tools: list) -> dict:
    by_name = {item.get("name"): item for item in tools if isinstance(item, dict)}
    checked = []
    expected_required = {
        "operation_id", "ownership_id", "binding_epoch", "revision_id",
        "sketch_id", "source_curve_ids", "mode",
    }
    for name in ("nx_prepare_sketch_pattern", "nx_edit_sketch_pattern"):
        tool = by_name.get(name)
        if tool is None:
            raise AssertionError(f"installed MCP omitted {name}")
        schema = tool.get("inputSchema")
        if not isinstance(schema, dict) or not expected_required <= set(schema.get("required", [])):
            raise AssertionError(f"{name} has an incomplete required input schema")
        if schema.get("additionalProperties") is not False:
            raise AssertionError(f"{name} must reject unknown input fields")
        properties = schema.get("properties", {})
        if properties.get("mode", {}).get("enum") != ["linear", "rectangular", "circular", "mirror"]:
            raise AssertionError(f"{name} mode enum differs from the F09 contract")
        source_schema = properties.get("source_curve_ids", {})
        if source_schema.get("minItems") != 1 or source_schema.get("uniqueItems") is not True:
            raise AssertionError(f"{name} must require unique nonempty source_curve_ids")
        if name == "nx_prepare_sketch_pattern" and tool.get("annotations", {}).get("readOnlyHint") is not True:
            raise AssertionError(f"{name} must be marked read-only")
        if name == "nx_edit_sketch_pattern" and tool.get("annotations", {}).get("destructiveHint") is not True:
            raise AssertionError(f"{name} must be marked as a write operation")
        checked.append(name)
    return {"checked_tools": checked}


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--ready", action="store_true",
                      help="inspect installed MCP schema and cached bridge status without ownership or NX calls")
    mode.add_argument("--part-path", type=Path,
                      help="unique saved clean .prt under workspace/v2/models; active Work Part must match")
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


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    run_id = uuid.uuid4().hex
    mode = "ready" if args.ready else "part"
    report: dict = {
        "test": "F09_installed_mcp_smoke",
        "run_id": run_id,
        "mode": mode,
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "passed": False,
        "objective": (
            "Verify the installed F09 pattern interface for linear, rectangular, circular, and mirror modes "
            "using independent native LINE/partial ARC/full-circle geometry checks, read-only previews, "
            "one integrated edit plus same-ID operation.get per mode, direct Save Copy/reopen inspection, and the declared native_engine "
            "(geometry-only NXOpen curve copies for translation/rotation; SketchMirrorPatternBuilder for mirror)."
        ),
        "traceability": [
            "F09 mode-specific input schema and installed MCP 0.9.0 / 27-tool interface",
            "F09 local-frame transforms for linear, rectangular, circular, and mirror patterns",
            "F09 supported native LINE, partial ARC, and full-circle geometry",
            "F09 native_engine per mode: NXOpen curve creation for linear/rectangular/circular geometry copies; NXOpen.SketchMirrorPatternBuilder for mirror",
            "F09 prepare read-only behavior, verified combined edit, source/constraint/non-target preservation",
            "F09 operation.get query and result equivalence for every successful integrated edit",
            "F09 native geometry one-to-one oracle and Save Copy/reopen roundtrip",
            "F09 ownership return from known idle, unhalted bridge status",
        ],
        "test_class": "installed MCP integration, NX sketch subsystem validation, and saved-part roundtrip",
        "configuration": {
            "expected_mcp_version": EXPECTED_SERVER_VERSION,
            "expected_tool_count": EXPECTED_TOOL_COUNT,
            "native_curve_coverage": ["LINE", "partial ARC", "full-circle ARC"],
            "position_tolerance_mm": POSITION_TOLERANCE_MM,
            "length_tolerance_mm": LENGTH_TOLERANCE_MM,
            "angle_tolerance_rad": ANGLE_TOLERANCE_RAD,
            "sample_size": 1,
            "measurement_uncertainty": "digital NX model values; no physical instrument used",
            "reliability_or_compliance_inference": False,
        },
        "safety_controls": {
            "NX_launch": "not invoked; the harness does not call nx_ensure_ready",
            "ownership_scope": "caller supplied saved independent fixture; exact Work Part path and clean state gated by nx_takeover",
            "source_part_save": "never saves over supplied path; Save Copy uses a new unique roundtrip path",
            "write_count": "one nx_edit_sketch_pattern call per mode; each request has one unique operation_id",
            "successful_edit_confirmation": "query nx_get_operation once with the same operation_id after each successful integrated edit; never replay the edit",
            "unknown_edit_outcome": "query the same nx_get_operation ID once; never replay and never return ownership while outcome is unknown",
            "ownership_return": "only from known idle, unhalted AI_OWNED bridge status",
        },
        "instrumentation": "installed MCP operation records and native NX sketch inspection; independent Python frame and curve-coordinate oracle",
        "calibration": "no physical instrument; CAD values checked at 1e-6 mm and 1e-7 rad digital tolerances",
        "preview_as_oracle": False,
        "calls": [],
        "cases": [],
    }
    client: McpStdioClient | None = None
    process: subprocess.Popen[bytes] | None = None
    credentials: dict[str, object] = {}
    active_case: dict | None = None
    part_path: Path | None = None
    output_path: Path | None = None
    unknown_write_outcome = False
    ownership_return_attempted = False

    def persist() -> None:
        if output_path is None:
            return
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(json.dumps(_redact(report), ensure_ascii=False,
                                          allow_nan=False, indent=2) + "\n", encoding="utf-8")

    def call_raw(name: str, arguments: dict | None = None,
                 *, allow_tool_error: bool = False) -> tuple[dict, dict]:
        if client is None:
            raise RuntimeError("installed MCP client is not initialized")
        request_arguments = arguments or {}
        entry = {"tool": name, "arguments": _redact(request_arguments)}
        report["calls"].append(entry)
        try:
            response = client.request("tools/call", {"name": name, "arguments": request_arguments})
        except Exception as exc:
            entry["transport_error"] = type(exc).__name__
            persist()
            raise
        entry["mcp_response"] = _redact(response)
        payload = _extract_payload(response, name)
        entry["payload"] = _redact(payload)
        entry["is_error"] = response.get("isError") is True
        persist()
        if response.get("isError") is True and not allow_tool_error:
            code = _error_code(response) or _error_code(payload) or "UNKNOWN"
            raise RuntimeError(f"{name} returned MCP tool error {code}")
        return payload, entry

    def wait_for_operation(initial: dict, label: str) -> dict:
        operation_id = initial.get("operation_id")
        if not isinstance(operation_id, str) or not operation_id:
            raise AssertionError(f"{label} operation response omitted operation_id")
        current = initial
        deadline = time.monotonic() + OPERATION_TIMEOUT_SECONDS
        while _status_state(current) not in TERMINAL_STATES:
            if time.monotonic() >= deadline:
                raise TimeoutError(f"{label} operation did not reach a terminal state")
            time.sleep(POLL_INTERVAL_SECONDS)
            current, poll_entry = call_raw("nx_get_operation", {"operation_id": operation_id},
                                           allow_tool_error=True)
            poll_entry["poll_for_operation_id"] = operation_id
        return current

    def call_operation(name: str, arguments: dict) -> dict:
        nonlocal unknown_write_outcome
        tracks_write = name in WRITE_OPERATION_TOOLS
        if tracks_write:
            unknown_write_outcome = True
        initial, entry = call_raw(name, arguments, allow_tool_error=tracks_write)
        if entry.get("is_error") is True:
            code = _error_code(initial) or "UNKNOWN"
            ambiguous = tracks_write and (
                _status_state(initial) == "UNKNOWN" or initial.get("outcome_unknown") is True or code in {
                    "OPERATION_OUTCOME_UNKNOWN", "CLIENT_OUTCOME_UNKNOWN", "UNKNOWN_OPERATION_OUTCOME",
                    "VERIFY_OUTCOME_UNKNOWN", "VERIFY_RESULT_MISSING", "UNKNOWN",
                }
            )
            unknown_write_outcome = ambiguous
            if ambiguous:
                operation_id = initial.get("operation_id") or arguments.get("operation_id")
                if isinstance(operation_id, str) and operation_id:
                    queried, query_entry = call_raw("nx_get_operation", {"operation_id": operation_id},
                                                    allow_tool_error=True)
                    query_entry["unknown_outcome_query_for"] = operation_id
                    entry["unknown_outcome_query"] = _redact(queried)
                    queried_state = _status_state(queried)
                    if queried_state in {"SUCCEEDED", "FAILED", "CANCELLED"}:
                        unknown_write_outcome = False
                        if queried_state == "SUCCEEDED":
                            queried_result = _operation_result(queried)
                            if isinstance(queried_result, dict):
                                persist()
                                return queried_result
            raise RuntimeError(f"{name} returned MCP tool error {code}")
        operation_id = initial.get("operation_id")
        if not isinstance(operation_id, str) or not operation_id:
            if tracks_write and _status_state(initial) in {"SUCCEEDED", "FAILED", "CANCELLED"}:
                unknown_write_outcome = False
            return initial
        completed = wait_for_operation(initial, name)
        entry["completed"] = _redact(completed)
        state = _status_state(completed)
        if state == "UNKNOWN":
            queried, query_entry = call_raw("nx_get_operation", {"operation_id": operation_id},
                                            allow_tool_error=True)
            query_entry["unknown_outcome_query_for"] = operation_id
            entry["unknown_outcome_query"] = _redact(queried)
            completed = queried
            state = _status_state(completed)
        if tracks_write:
            unknown_write_outcome = state not in {"SUCCEEDED", "FAILED", "CANCELLED"}
        if state != "SUCCEEDED":
            raise RuntimeError(f"{name} operation ended in {state or 'UNKNOWN'}")
        result = _operation_result(completed)
        if not isinstance(result, dict):
            raise AssertionError(f"{name} SUCCEEDED without a result object")
        persist()
        return result

    def record_case(case_id: str, request: dict, retest_criterion: str) -> dict:
        nonlocal active_case
        case = {
            "case_id": case_id,
            "state": "RUNNING",
            "request": _redact(request),
            "reproduction_steps": [
                "Use a new clean saved millimeter .prt under workspace/v2/models and open it as the NX Work Part.",
                "Run this script with that exact --part-path and a new evidence path under workspace/v2/evidence.",
                f"Repeat {case_id} once using the recorded calls, then compare native inspection with the independent oracle.",
            ],
            "retest_criterion": retest_criterion,
        }
        report["cases"].append(case)
        active_case = case
        persist()
        return case

    def update_revision(label: str, expected_path: Path) -> dict:
        if not credentials:
            raise RuntimeError("cannot read revision without ownership credentials")
        session = call_raw("nx_get_session", credentials)[0]
        session_path = session.get("part_path")
        if not isinstance(session_path, str) or _canonical_path(session_path) != _canonical_path(expected_path):
            raise AssertionError(f"{label} nx_get_session path differs from the expected current Work Part")
        revision_id = session.get("revision_id")
        if not isinstance(revision_id, str) or not revision_id:
            raise AssertionError(f"{label} nx_get_session omitted revision_id")
        report.setdefault("revision_observations", []).append({
            "stage": label,
            "revision_id": revision_id,
            "part_path": session_path,
            "part_units": session.get("part_units"),
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

    def return_ownership() -> dict:
        nonlocal ownership_return_attempted
        if not credentials:
            return {"skipped": "no current ownership credentials"}
        if ownership_return_attempted:
            result = {"skipped": "ownership return was already attempted; it will not be replayed"}
            report["ownership_return"] = result
            persist()
            return result
        if unknown_write_outcome:
            result = {"skipped": "F09 write outcome is unknown; ownership must remain with the bridge"}
            report["ownership_return"] = result
            persist()
            return result
        status = call_raw("nx_bridge_status", {})[0]
        in_flight = _int_value(status.get("in_flight", status.get("active_operations", -1)),
                               "bridge in_flight")
        if (status.get("state") != "loaded" or status.get("ownership_state") != "AI_OWNED"
                or status.get("writes_halted") is not False
                or in_flight != 0):
            result = {
                "skipped": "bridge is not a known idle, unhalted AI_OWNED state",
                "ownership_state": status.get("ownership_state"),
                "writes_halted": status.get("writes_halted"),
                "in_flight": in_flight,
            }
            report["ownership_return"] = result
            persist()
            return result
        ownership_return_attempted = True
        returned = call_raw("nx_return", credentials)[0]
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("nx_return did not confirm safe HUMAN_OWNED state")
        credentials.clear()
        final = call_raw("nx_bridge_status", {})[0]
        final_in_flight = _int_value(final.get("in_flight", final.get("active_operations", -1)),
                                     "final bridge in_flight")
        if (final.get("state") != "loaded" or final.get("ownership_state") != "HUMAN_OWNED"
                or final.get("writes_halted") is not False
                or final_in_flight != 0):
            raise AssertionError("final bridge status is not safe for human ownership")
        result = {"return": _redact(returned), "final_status": _redact(final)}
        report["ownership_return"] = result
        persist()
        return result

    try:
        evidence_candidate = args.output.resolve(strict=False)
        evidence_root = (ROOT / "workspace" / "v2" / "evidence").resolve(strict=False)
        if evidence_candidate.is_relative_to(evidence_root) and not evidence_candidate.exists():
            output_path = evidence_candidate
        part_path, output_path = _validate_paths(args, report)

        install_root = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy()
        env.update(PYTHONPATH=str(install_root / "app" / "src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen(
            [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install_root / "mcp-config.json")],
            cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, bufsize=0,
        )
        client = McpStdioClient(process, 60)
        _tool_names_and_version(client, report)

        initial_status = call_raw("nx_bridge_status", {})[0]
        report["initial_status"] = _redact(initial_status)
        if initial_status.get("state") != "loaded":
            raise AssertionError("installed NX bridge is not loaded")
        if initial_status.get("writes_halted") is not False:
            raise AssertionError("bridge write gate is halted")
        in_flight = _int_value(initial_status.get("in_flight", initial_status.get("active_operations", -1)),
                               "initial bridge in_flight")
        if args.ready:
            if initial_status.get("ownership_state") != "HUMAN_OWNED" or in_flight != 0:
                raise AssertionError("--ready requires idle HUMAN_OWNED bridge status")
            report.update(
                passed=True,
                diagnostic_only=True,
                ready_result="installed 0.9.0 MCP, 27 tools, F09 schemas, and cached idle bridge status verified",
            )
            return 0

        if initial_status.get("ownership_state") != "HUMAN_OWNED" or in_flight != 0:
            raise AssertionError("model mode requires an idle HUMAN_OWNED NX session before takeover")
        if part_path is None:
            raise AssertionError("model mode requires --part-path")
        nx_pid = _int_value(initial_status.get("pid"), "bridge NX pid")
        takeover_case = record_case(
            "F09_saved_fixture_takeover_gate",
            {"expected_pid": nx_pid, "expected_part_path": str(part_path),
             "artifact_scope": "saved_independent_copy", "authorize": True},
            "On a new clean saved millimeter fixture, require exact path identity and clean-state checks before any write; then repeat all four modes and the Save Copy/reopen oracle.",
        )
        takeover = call_raw("nx_takeover", {
            "expected_pid": nx_pid,
            "expected_part_path": str(part_path),
            "artifact_scope": "saved_independent_copy",
            "authorize": True,
        })[0]
        owner = takeover.get("ownership_id")
        epoch = takeover.get("binding_epoch")
        if (isinstance(owner, str) and owner and isinstance(epoch, int) and not isinstance(epoch, bool)
                and epoch >= 1):
            credentials.update(ownership_id=owner, binding_epoch=epoch)
        if not credentials:
            raise AssertionError("nx_takeover omitted ownership credentials")
        if takeover.get("ownership_state") != "AI_OWNED":
            raise AssertionError("nx_takeover did not establish AI_OWNED state")
        if _canonical_path(takeover.get("part_path", "")) != _canonical_path(part_path):
            raise AssertionError("nx_takeover identity does not match supplied Work Part path")
        if (takeover.get("is_modified") is not False or takeover.get("is_modified_before_read") is not False
                or takeover.get("is_modified_after_read") is not False
                or takeover.get("dirty_changed_by_read") is not False):
            raise AssertionError("nx_takeover did not confirm a clean Work Part without dirty-state change")
        if (takeover.get("artifact_scope") != "saved_independent_copy"
                or takeover.get("artifact_kind") != "standalone_part"):
            raise AssertionError("nx_takeover did not confirm saved standalone part scope")
        takeover_case.update(state="PASS", path_match=True, clean_before_and_after_read=True)
        report["takeover_gate_evidence"] = _redact(takeover)
        persist()

        session = update_revision("after takeover", part_path)
        if (session.get("is_modified_before_read") is not False
                or session.get("is_modified_after_read") is not False
                or session.get("dirty_changed_by_read") is not False):
            raise AssertionError("post-takeover session did not confirm a clean Work Part")
        if str(session.get("part_units", "")).casefold() not in {"millimeters", "millimeter", "mm"}:
            raise AssertionError("supplied Work Part must use millimeters")
        report["configuration"].update({
            "nx_build": takeover.get("nx_build"),
            "nxopen_assembly_version": takeover.get("nxopen_assembly_version"),
            "nxopen_file_version": takeover.get("nxopen_file_version"),
            "part_units": session.get("part_units"),
        })

        initial_part = call_raw("nx_inspect_part", credentials)[0]
        _require_inspected_part_path(initial_part, part_path, "initial F09 fixture")
        part_record = initial_part.get("part") if isinstance(initial_part.get("part"), dict) else {}
        read_effects = initial_part.get("read_effects") if isinstance(initial_part.get("read_effects"), dict) else {}
        if (part_record.get("is_modified") is not False
                or read_effects.get("is_modified_before_read") is not False
                or read_effects.get("is_modified_after_read") is not False
                or read_effects.get("dirty_changed_by_read") is not False):
            raise AssertionError("initial part inspection did not preserve a clean Work Part")
        existing_sketches = initial_part.get("sketches")
        existing_bodies = initial_part.get("bodies")
        if not isinstance(existing_sketches, list) or existing_sketches:
            raise AssertionError("F09 smoke requires a blank saved part without existing sketches")
        if not isinstance(existing_bodies, list) or existing_bodies:
            raise AssertionError("F09 smoke requires a blank saved part without existing bodies")
        report["initial_part_inspection"] = _redact(initial_part)

        frame = build_frame(SKETCH_FRAME["origin_mm"], SKETCH_FRAME["normal"],
                            SKETCH_FRAME["requested_x_axis"])
        seed_names = list(_seed_specs())
        baseline_expected = [_expected_seed_geometry(name, frame) for name in seed_names]
        setup_case = record_case(
            "F09_create_LINE_partial_ARC_full_circle_fixture",
            {"plane": "XY", "origin_mm": SKETCH_FRAME["origin_mm"],
             "constraint_mode": "geometry_only", "geometries": SEED_GEOMETRIES},
            "On a new blank fixture, create the six specified native curves; verify one LINE, a partial ARC, a full circle, a separate mirror-axis LINE, two non-target curves, millimeter units, and an empty constraint inventory by direct sketch inspection.",
        )
        sketch_prepared = call_operation("nx_prepare_sketch", {
            "operation_id": "f09-sketch-prepare-" + uuid.uuid4().hex,
            **credentials,
            "revision_id": session["revision_id"],
            "plane": "XY",
            "origin_mm": list(SKETCH_FRAME["origin_mm"]),
            "constraint_mode": "geometry_only",
            "geometries": SEED_GEOMETRIES,
        })
        sketch_plan = sketch_prepared.get("plan")
        if not isinstance(sketch_plan, dict) or not isinstance(sketch_plan.get("plan_id"), str):
            raise AssertionError("nx_prepare_sketch omitted plan.plan_id")
        if sketch_plan.get("revision_id") not in (None, session["revision_id"]):
            raise AssertionError("F09 source sketch plan targets a different revision")
        setup_execute_id = "f09-sketch-execute-" + uuid.uuid4().hex
        sketch_executed = call_operation("nx_execute_plan", {
            "operation_id": setup_execute_id, **credentials, "plan_id": sketch_plan["plan_id"],
        })
        if sketch_executed.get("mutation_state") != "APPLIED":
            raise AssertionError("F09 source sketch creation did not return mutation_state=APPLIED")
        sketch_verified = call_operation("nx_verify_part", {
            "operation_id": "f09-sketch-verify-" + uuid.uuid4().hex,
            **credentials,
            "execute_operation_id": setup_execute_id,
        })
        if sketch_verified.get("verified") is not True:
            raise AssertionError("F09 source sketch did not pass nx_verify_part")
        sketch_id = sketch_executed.get("sketch_id") or sketch_verified.get("sketch_id")
        if not isinstance(sketch_id, str) or not sketch_id:
            raise AssertionError("F09 source sketch creation omitted sketch_id")
        session = update_revision("after F09 source sketch creation", part_path)
        baseline_sketch = inspect_sketch(sketch_id, "initial F09 source geometry")
        baseline_match = _check_sketch_geometry(baseline_sketch, frame, baseline_expected,
                                                "initial F09 source geometry")
        if len(baseline_sketch["geometry"]) != len(SEED_GEOMETRIES):
            raise AssertionError("F09 source sketch contains extra or missing geometry")
        constraints = baseline_sketch.get("constraints")
        if not isinstance(constraints, list) or constraints:
            raise AssertionError("geometry_only F09 fixture unexpectedly contains sketch constraints")
        baseline_ids = {}
        actual_geometry = baseline_sketch["geometry"]
        for expected_index, seed in enumerate(seed_names):
            actual_index = baseline_match["geometry_match"]["expected_to_actual"][expected_index]
            baseline_ids[seed] = actual_geometry[actual_index]["journal_id"]
        if len(set(baseline_ids.values())) != len(baseline_ids):
            raise AssertionError("F09 seed geometry did not receive unique native journal IDs")
        source_curve_ids = [baseline_ids[name] for name in SOURCE_NAMES]
        mirror_axis_id = baseline_ids[MIRROR_AXIS_NAME]
        setup_case.update(
            state="PASS",
            sketch_id=sketch_id,
            source_curve_ids=source_curve_ids,
            mirror_axis_curve_id=mirror_axis_id,
            non_target_curve_ids=[baseline_ids[name] for name in NON_TARGET_NAMES],
            native_geometry_readback=_redact(baseline_sketch),
            verification=_redact(sketch_verified),
        )
        report["source_sketch_id"] = sketch_id
        report["source_curve_ids"] = source_curve_ids
        report["mirror_axis_curve_id"] = mirror_axis_id
        report["non_target_curve_ids"] = [baseline_ids[name] for name in NON_TARGET_NAMES]
        persist()

        current_expected = list(baseline_expected)
        current_sketch = baseline_sketch
        mode_results = []
        for pattern_mode, configured_fields in PATTERN_CASES:
            fields = dict(configured_fields)
            current_geometry_check = _check_sketch_geometry(
                current_sketch, frame, current_expected, f"current F09 {pattern_mode} source geometry"
            )
            current_ids_by_seed = _resolve_current_seed_journal_ids(
                current_sketch, current_geometry_check["geometry_match"], seed_names,
                f"current F09 {pattern_mode} source geometry",
            )
            mode_source_curve_ids = [current_ids_by_seed[name] for name in SOURCE_NAMES]
            mode_mirror_axis_id = current_ids_by_seed[MIRROR_AXIS_NAME]
            if pattern_mode == "mirror":
                fields["axis_curve_id"] = mode_mirror_axis_id
            expected_outputs = _pattern_expected_outputs(pattern_mode, fields, frame)
            position_count = len(_pattern_positions(pattern_mode, fields)) + 1
            expected_output_count = len(expected_outputs)
            test_request = {"mode": pattern_mode, **fields, "source_curve_ids": mode_source_curve_ids}
            case = record_case(
                f"F09_{pattern_mode}_prepare_edit_inspect",
                test_request,
                f"On a fresh matching blank fixture, run one read-only {pattern_mode} prepare and prove revision/native geometry unchanged; submit one integrated edit exactly once, require SUCCEEDED/APPLIED/verified with all nine checks VERIFIED, and directly inspect the complete expected output set within 1e-6 mm and 1e-7 rad while preserving sources, constraints, and non-targets.",
            )

            before_preview_session = update_revision(f"before F09 {pattern_mode} prepare", part_path)
            before_preview_sketch = inspect_sketch(sketch_id, f"before F09 {pattern_mode} prepare")
            _check_sketch_geometry(before_preview_sketch, frame, current_expected,
                                   f"before F09 {pattern_mode} prepare")
            _assert_same_existing_geometry(current_sketch, before_preview_sketch,
                                           f"before F09 {pattern_mode} prepare")
            if before_preview_sketch.get("constraints") != constraints:
                raise AssertionError(f"F09 {pattern_mode} pre-prepare constraints changed")
            prepare_arguments = {
                "operation_id": f"f09-{pattern_mode}-prepare-" + uuid.uuid4().hex,
                **credentials,
                "revision_id": before_preview_session["revision_id"],
                "sketch_id": sketch_id,
                "source_curve_ids": mode_source_curve_ids,
                "mode": pattern_mode,
                **fields,
            }
            prepared = call_operation("nx_prepare_sketch_pattern", prepare_arguments)
            plan = prepared.get("plan")
            if not isinstance(plan, dict) or not isinstance(plan.get("plan_id"), str) or not plan["plan_id"]:
                raise AssertionError(f"nx_prepare_sketch_pattern {pattern_mode} omitted plan.plan_id")
            if (plan.get("kind") != "edit_sketch_pattern" or plan.get("mode") != pattern_mode
                    or plan.get("sketch_id") != sketch_id
                    or plan.get("revision_id") != before_preview_session["revision_id"]
                    or plan.get("source_curve_ids") != mode_source_curve_ids):
                raise AssertionError(f"F09 {pattern_mode} plan does not preserve requested target, mode, and revision")
            if pattern_mode == "mirror" and plan.get("axis_curve_id") != mode_mirror_axis_id:
                raise AssertionError("F09 mirror plan does not identify the separate axis curve")
            if plan.get("expected_pattern_instance_count") != position_count:
                raise AssertionError(f"F09 {pattern_mode} plan has an incorrect pattern position count")
            if plan.get("expected_output_curve_count") != expected_output_count:
                raise AssertionError(f"F09 {pattern_mode} plan has an incorrect output curve count")
            preview_instances = plan.get("expected_instances")
            if not isinstance(preview_instances, list) or len(preview_instances) != position_count * len(mode_source_curve_ids):
                raise AssertionError(f"F09 {pattern_mode} plan omitted the expected instance inventory")

            after_preview_session = update_revision(f"after F09 {pattern_mode} prepare", part_path)
            after_preview_sketch = inspect_sketch(sketch_id, f"after F09 {pattern_mode} prepare")
            _check_sketch_geometry(after_preview_sketch, frame, current_expected,
                                   f"after F09 {pattern_mode} prepare")
            _assert_same_existing_geometry(before_preview_sketch, after_preview_sketch,
                                           f"F09 {pattern_mode} read-only prepare")
            if after_preview_session.get("revision_id") != before_preview_session.get("revision_id"):
                raise AssertionError(f"F09 {pattern_mode} prepare changed model revision")
            for field in ("is_modified_before_read", "is_modified_after_read", "dirty_changed_by_read"):
                if after_preview_session.get(field) != before_preview_session.get(field):
                    raise AssertionError(f"F09 {pattern_mode} prepare changed session dirty-state evidence")
            if (before_preview_session.get("dirty_changed_by_read") is not False
                    or after_preview_session.get("dirty_changed_by_read") is not False):
                raise AssertionError(f"F09 {pattern_mode} session read changed the dirty state")
            if after_preview_sketch.get("constraints") != before_preview_sketch.get("constraints"):
                raise AssertionError(f"F09 {pattern_mode} prepare changed the sketch constraints")
            case["prepare_preview"] = _redact(prepared)
            case["preview_geometry_after"] = _redact(after_preview_sketch)
            case["preview_revision_unchanged"] = True
            case["preview_used_as_geometry_oracle"] = False
            persist()

            before_edit_session = update_revision(f"before F09 {pattern_mode} integrated edit", part_path)
            if before_edit_session.get("revision_id") != after_preview_session.get("revision_id"):
                raise AssertionError(f"F09 {pattern_mode} model revision changed after the read-only prepare")
            before_edit_sketch = inspect_sketch(sketch_id, f"before F09 {pattern_mode} integrated edit")
            _check_sketch_geometry(before_edit_sketch, frame, current_expected,
                                   f"before F09 {pattern_mode} integrated edit")
            _assert_same_existing_geometry(after_preview_sketch, before_edit_sketch,
                                           f"before F09 {pattern_mode} integrated edit")
            edit_arguments = {
                **prepare_arguments,
                "operation_id": f"f09-{pattern_mode}-edit-" + uuid.uuid4().hex,
                "revision_id": before_edit_session["revision_id"],
            }
            edit_result = call_operation("nx_edit_sketch_pattern", edit_arguments)
            queried_edit, _ = call_raw("nx_get_operation", {
                "operation_id": edit_arguments["operation_id"],
            }, allow_tool_error=True)
            queried_state = _status_state(queried_edit)
            queried_operation = queried_edit.get("operation") if isinstance(queried_edit.get("operation"), dict) else {}
            returned_operation_id = queried_edit.get("operation_id") or queried_operation.get("operation_id")
            case["operation_get"] = {
                "queried_operation_id": edit_arguments["operation_id"],
                "returned_operation_id": returned_operation_id,
                "state": queried_state,
                "result_equivalent_to_edit_response": False,
                "operation_record": _redact(queried_edit),
            }
            persist()
            if returned_operation_id is not None and returned_operation_id != edit_arguments["operation_id"]:
                raise AssertionError("nx_get_operation returned a different operation_id for the successful integrated edit")
            if queried_state != "SUCCEEDED":
                raise AssertionError(f"nx_get_operation did not confirm the successful {pattern_mode} edit")
            queried_edit_result = _operation_result(queried_edit)
            if not isinstance(queried_edit_result, dict):
                raise AssertionError(f"nx_get_operation omitted the successful {pattern_mode} result object")
            edit_result_json = json.dumps(edit_result, ensure_ascii=False, sort_keys=True,
                                          allow_nan=False, separators=(",", ":"))
            queried_result_json = json.dumps(queried_edit_result, ensure_ascii=False, sort_keys=True,
                                             allow_nan=False, separators=(",", ":"))
            if queried_result_json != edit_result_json:
                raise AssertionError(f"nx_get_operation result differs from the successful {pattern_mode} edit response")
            case["operation_get"]["result_equivalent_to_edit_response"] = True
            persist()
            operation_get_checks = _require_verified_pattern_result(
                queried_edit_result, pattern_mode, sketch_id, mode_source_curve_ids,
                position_count, expected_output_count,
            )
            case["operation_get"].update(
                result_equivalent_to_edit_response=True,
                verified=True,
                verification_checks=_redact(operation_get_checks),
            )
            persist()
            checks = _require_verified_pattern_result(
                edit_result, pattern_mode, sketch_id, mode_source_curve_ids, position_count, expected_output_count,
            )
            after_edit_session = update_revision(f"after F09 {pattern_mode} integrated edit", part_path)
            edited_sketch = inspect_sketch(sketch_id, f"independent F09 {pattern_mode} geometry readback")
            cumulative_expected = current_expected + expected_outputs
            _check_sketch_geometry(edited_sketch, frame, cumulative_expected,
                                   f"independent F09 {pattern_mode} geometry readback")
            preservation = _assert_same_existing_geometry(
                before_edit_sketch, edited_sketch,
                f"F09 {pattern_mode} source and non-target preservation",
                allow_journal_id_reassignment=True,
            )
            if edited_sketch.get("constraints") != constraints:
                raise AssertionError(f"F09 {pattern_mode} edit changed the original constraint inventory")
            newly_created = preservation["new_geometry"]
            if len(newly_created) != expected_output_count:
                raise AssertionError(
                    f"F09 {pattern_mode} expected {expected_output_count} newly created curves, got {len(newly_created)}")
            new_ids = sorted(record["journal_id"] for record in newly_created)
            output_match = match_geometry_records(newly_created, expected_outputs)
            case.update(
                state="PASS",
                integrated_edit=_redact(edit_result),
                verification_checks=_redact(checks),
                expected_output_count=expected_output_count,
                new_native_curve_ids=sorted(new_ids),
                new_native_geometry_readback=_redact(newly_created),
                preserved_geometry_journal_id_mapping=preservation["preserved_geometry_journal_id_mapping"],
                independent_output_match=output_match,
                full_native_geometry_readback=_redact(edited_sketch),
                revision_id=after_edit_session["revision_id"],
                source_and_non_target_geometry_preserved=True,
                constraints_preserved=True,
            )
            persist()
            mode_results.append({
                "mode": pattern_mode,
                "operation_id": edit_arguments["operation_id"],
                "source_curve_ids": mode_source_curve_ids,
                "mirror_axis_curve_id": mode_mirror_axis_id if pattern_mode == "mirror" else None,
                "position_count": position_count,
                "expected_output_count": expected_output_count,
                "new_native_curve_ids": sorted(new_ids),
                "preserved_geometry_journal_id_mapping": preservation["preserved_geometry_journal_id_mapping"],
                "one_to_one_output_match": output_match,
                "verification_checks": checks,
            })
            current_expected = cumulative_expected
            current_sketch = edited_sketch

        report["mode_results"] = _redact(mode_results)
        model_root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
        roundtrip_path = model_root / f"F09-mcp-{run_id}-roundtrip.prt"
        if roundtrip_path.exists() or _canonical_path(roundtrip_path) == _canonical_path(part_path):
            raise FileExistsError("F09 unique Save Copy path already exists or collides with the source")
        roundtrip_case = record_case(
            "F09_save_reopen_and_direct_inspection",
            {"target_path": str(roundtrip_path), "reopen": True},
            "On a new fixture, require Save Copy saved/reopened=true and all four roundtrip checks VERIFIED; inspect the reopened sketch directly and one-to-one match every original, non-target, and pattern LINE/ARC/full-circle record to the independent oracle within 1e-6 mm and 1e-7 rad; confirm the supplied source file metadata is unchanged and return ownership safely.",
        )
        saved = call_operation("nx_save_copy", {
            "operation_id": "f09-save-" + uuid.uuid4().hex,
            **credentials,
            "target_path": str(roundtrip_path),
            "reopen": True,
        })
        if saved.get("saved") is not True or saved.get("reopened") is not True:
            raise AssertionError("nx_save_copy did not report saved=true and reopened=true")
        save_checks = saved.get("checks")
        required_save_checks = {"associations", "expression", "geometry", "history"}
        if not isinstance(save_checks, dict) or set(save_checks) != required_save_checks:
            raise AssertionError("nx_save_copy omitted a required association, expression, geometry, or history check")
        bad_save_checks = {key for key, value in save_checks.items()
                           if not isinstance(value, dict) or value.get("status") != "VERIFIED"}
        if bad_save_checks:
            raise AssertionError(f"nx_save_copy contained non-VERIFIED checks: {sorted(bad_save_checks)!r}")
        new_owner = saved.get("new_ownership_id")
        new_epoch = saved.get("new_binding_epoch")
        if (not isinstance(new_owner, str) or not new_owner or isinstance(new_epoch, bool)
                or not isinstance(new_epoch, int) or new_epoch < 1):
            raise AssertionError("nx_save_copy omitted rotated ownership credentials")
        credentials.update(ownership_id=new_owner, binding_epoch=new_epoch)
        reopened_path = saved.get("saved_path") or str(roundtrip_path)
        if _canonical_path(reopened_path) != _canonical_path(roundtrip_path):
            raise AssertionError("nx_save_copy path differs from the requested unique roundtrip path")
        roundtrip_case["save_copy"] = _redact(saved)
        reopened_session = update_revision("after F09 Save Copy reopen", roundtrip_path)
        if str(reopened_session.get("part_units", "")).casefold() not in {"millimeters", "millimeter", "mm"}:
            raise AssertionError("reopened F09 Work Part units are not millimeters")
        reopened_part = call_raw("nx_inspect_part", credentials)[0]
        _require_inspected_part_path(reopened_part, roundtrip_path, "reopened F09 part inspection")
        reopened_sketches = reopened_part.get("sketches")
        if not isinstance(reopened_sketches, list) or len(reopened_sketches) != 1:
            raise AssertionError("reopened F09 test part must contain exactly one sketch")
        reopened_sketch_id = (
            reopened_sketches[0].get("journal_id") if isinstance(reopened_sketches[0], dict) else None
        )
        if not isinstance(reopened_sketch_id, str) or not reopened_sketch_id:
            raise AssertionError("reopened F09 sketch omitted journal_id")
        reopened_sketch = inspect_sketch(reopened_sketch_id, "roundtrip F09 native geometry")
        reopened_match = _check_sketch_geometry(reopened_sketch, frame, current_expected,
                                                "roundtrip F09 native geometry")
        if reopened_sketch.get("constraints") != constraints:
            raise AssertionError("Save Copy/reopen changed the F09 sketch constraint inventory")
        reopened_ids = _geometry_by_journal_id(reopened_sketch, "roundtrip F09 native geometry")
        if len(reopened_ids) != len(current_expected):
            raise AssertionError("Save Copy/reopen contains extra or missing F09 geometry")
        source_after = _file_metadata(part_path)
        source_before = report["source_part_file"]["before_save_copy"]
        source_unchanged = source_after == source_before
        report["source_part_file"].update(after_save_copy=source_after,
                                          size_and_mtime_unchanged=source_unchanged)
        if not source_unchanged:
            raise AssertionError("Save Copy changed the supplied source .prt size or modification time")
        roundtrip_case.update(
            state="PASS",
            reopened_part_path=str(roundtrip_path),
            reopened_sketch_id=reopened_sketch_id,
            reopened_inspection=_redact(reopened_part),
            reopened_sketch_readback=_redact(reopened_sketch),
            reopened_independent_match=reopened_match["geometry_match"],
            reopened_geometry_count=len(reopened_ids),
            source_part_file_metadata=_redact(report["source_part_file"]),
        )
        persist()

        returned = return_ownership()
        if returned.get("skipped"):
            raise AssertionError("ownership return was skipped because bridge was not known idle and safe")
        roundtrip_case["ownership_return"] = returned
        roundtrip_case["state"] = "PASS"
        report["saved_path"] = str(roundtrip_path)
        report["passed"] = True
        persist()
        return 0
    except Exception as exc:
        report["error"] = f"{type(exc).__name__}: {exc}"
        report["passed"] = False
        failure_case_id = active_case.get("case_id") if isinstance(active_case, dict) else "F09_installed_harness"
        generic_retest = (
            "Use a new unique clean saved millimeter fixture and evidence path; verify installed MCP 0.9.0/27 tools; "
            "create the documented LINE/partial-ARC/full-circle geometry; repeat each prepare once and each integrated "
            "edit once, querying only the same operation ID if uncertain; independently inspect all four outputs and "
            "source/constraint/non-target preservation; verify Save Copy/reopen and safely return ownership."
        )
        failure = {
            "case_id": failure_case_id,
            "failure": f"{type(exc).__name__}: {exc}",
            "reproduction_steps": (active_case.get("reproduction_steps") if isinstance(active_case, dict) else [
                "Confirm the installed MCP and local NX bridge are available without launching NX.",
                "Use a new clean saved millimeter .prt under workspace/v2/models and a new evidence output path.",
                f"Rerun this script in {mode} mode and repeat {failure_case_id} using the recorded calls and inspections.",
            ]),
            "retest_criterion": (active_case.get("retest_criterion") if isinstance(active_case, dict)
                                 else generic_retest),
        }
        report["failure_record"] = failure
        if isinstance(active_case, dict):
            active_case.update(state="FAIL", failure=failure["failure"],
                               reproduction_steps=failure["reproduction_steps"],
                               retest_criterion=failure["retest_criterion"])
        if credentials and client is not None:
            try:
                return_ownership()
            except Exception as return_error:
                report["failure_return_error"] = f"{type(return_error).__name__}: {return_error}"
        return 1
    finally:
        report["completed_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        if client is not None:
            try:
                client.close()
            except Exception as close_error:
                report["client_close_error"] = type(close_error).__name__
        persist()
        summary = {key: report[key] for key in (
            "passed", "mode", "server_version", "tool_count", "part_path", "saved_path", "error",
        ) if key in report}
        print(json.dumps(_redact(summary), ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
