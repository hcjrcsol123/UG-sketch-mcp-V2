"""Installed MCP smoke for one representative F08 sketch-offset operation.

Model mode expects a unique, saved, clean, standalone millimeter .prt under
``workspace/v2/models`` and the same part open as the NX Work Part. It creates
an open two-Line L chain through the installed MCP, checks the read-only offset
preview, performs one combined edit, independently checks native geometry,
saves and reopens a new copy, and returns ownership only from a known idle
bridge state. This script never launches NX or saves over the supplied part.

``--ready`` checks the installed MCP version, tool set, and cached bridge status
without taking ownership or calling NX readiness/launch logic. These checks
use digital NX model values and do not establish physical accuracy or
population-wide reliability.
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
EXPECTED_SERVER_VERSION = "0.8.0"
EXPECTED_TOOL_COUNT = 25
POSITION_TOLERANCE_MM = 1.0e-6
LENGTH_TOLERANCE_MM = 1.0e-6
OPERATION_TIMEOUT_SECONDS = 180.0
POLL_INTERVAL_SECONDS = 0.2
PRIVATE_KEY_PARTS = ("token", "secret", "authorization", "ownership_id", "credential")
TERMINAL_STATES = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
WRITE_OPERATION_TOOLS = {"nx_execute_plan", "nx_save_copy"}
EXPECTED_TOOLS = {
    "nx_ensure_ready", "nx_get_capabilities", "nx_bridge_status", "nx_bridge_ping",
    "nx_takeover", "nx_return", "nx_get_session", "nx_inspect_part", "nx_query_faces",
    "nx_inspect_sketch", "nx_get_operation", "nx_cancel_operation", "nx_prepare_datum_plane",
    "nx_prepare_sketch", "nx_prepare_sketch_corner", "nx_edit_sketch_corner",
    "nx_prepare_sketch_offset", "nx_edit_sketch_offset", "nx_prepare_extrude",
    "nx_prepare_thread", "nx_prepare_helical_groove", "nx_prepare_operation",
    "nx_execute_plan", "nx_verify_part", "nx_save_copy",
}
BASE_LINES = {
    "horizontal": ([0.0, 0.0, 0.0], [10.0, 0.0, 0.0]),
    "vertical": ([10.0, 0.0, 0.0], [10.0, 10.0, 0.0]),
}
EXPECTED_OFFSET_LINES = {
    "horizontal": ([0.0, 2.0, 0.0], [8.0, 2.0, 0.0]),
    "vertical": ([8.0, 2.0, 0.0], [8.0, 10.0, 0.0]),
}


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--ready", action="store_true",
                      help="inspect installed MCP and cached bridge status without ownership or NX calls")
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


def _operation_record(record: dict) -> dict:
    """Flatten one MCP status wrapper to the underlying bridge operation record."""
    nested = record.get("operation")
    return nested if isinstance(nested, dict) else record


def _completed_edit_status(initial: dict, completed: dict) -> dict:
    """Keep the MCP status wrapper while unwrapping its bridge operation once."""
    state = _status_state(completed)
    operation_result = _operation_result(completed)
    return {
        **initial,
        "state": state,
        "operation": _operation_record(completed),
        "verified": operation_result.get("verified") if isinstance(operation_result, dict) else False,
        "success": state == "SUCCEEDED" and isinstance(operation_result, dict)
                   and operation_result.get("verified") is True,
    }


def _finite_vector(value: object, label: str) -> list[float]:
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        raise AssertionError(f"{label} must be a 3D finite vector")
    try:
        result = [float(component) for component in value]
    except (TypeError, ValueError, OverflowError) as exc:
        raise AssertionError(f"{label} must be a 3D finite vector") from exc
    if not all(math.isfinite(component) for component in result):
        raise AssertionError(f"{label} contains a non-finite component")
    return result


def _distance(left: list[float], right: list[float]) -> float:
    return math.sqrt(sum((left[index] - right[index]) ** 2 for index in range(3)))


def _assert_close(actual: float, expected: float, tolerance: float, label: str) -> None:
    if not math.isfinite(actual) or abs(actual - expected) > tolerance:
        raise AssertionError(
            f"{label}: expected {expected:.12g}, got {actual:.12g}; tolerance={tolerance:.3g}"
        )


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
    same = max(_distance(start, first), _distance(end, second))
    reversed_order = max(_distance(start, second), _distance(end, first))
    return min(same, reversed_order) <= POSITION_TOLERANCE_MM


def _one_segment(geometry: list, first: list[float], second: list[float], label: str) -> dict:
    matches = [item for item in geometry if isinstance(item, dict)
               and item.get("kind") == "LINE" and _segment_matches(item, first, second)]
    if len(matches) != 1:
        raise AssertionError(f"{label} expected one uniquely matching native LINE, found {len(matches)}")
    record = matches[0]
    if not isinstance(record.get("journal_id"), str) or not record["journal_id"]:
        raise AssertionError(f"{label} omitted native journal_id")
    return record


def _check_frame_and_units(sketch: dict, stage: str) -> None:
    units = str(sketch.get("units", "")).casefold()
    if units not in {"millimeters", "millimeter", "mm"}:
        raise AssertionError(f"{stage} sketch units must be millimeters, got {sketch.get('units')!r}")
    for key, expected in (("origin", [0.0, 0.0, 0.0]),
                          ("normal", [0.0, 0.0, 1.0]),
                          ("x_axis", [1.0, 0.0, 0.0]),
                          ("y_axis", [0.0, 1.0, 0.0])):
        _assert_point(sketch.get(key), expected, f"{stage} {key}", POSITION_TOLERANCE_MM)


def _check_initial_geometry(sketch: dict) -> dict[str, dict]:
    _check_frame_and_units(sketch, "initial F08 fixture")
    geometry = sketch.get("geometry")
    if not isinstance(geometry, list):
        raise AssertionError("nx_inspect_sketch omitted geometry")
    lines = [item for item in geometry if isinstance(item, dict) and item.get("kind") == "LINE"]
    if len(lines) != 2:
        raise AssertionError(f"F08 fixture must contain exactly two native LINEs, got {len(lines)}")
    found = {
        name: _one_segment(geometry, endpoints[0], endpoints[1], "initial " + name)
        for name, endpoints in BASE_LINES.items()
    }
    if len({item["journal_id"] for item in found.values()}) != 2:
        raise AssertionError("F08 fixture line IDs are not distinct")
    constraints = sketch.get("constraints")
    if not isinstance(constraints, list):
        raise AssertionError("nx_inspect_sketch omitted constraint inventory")
    if constraints:
        raise AssertionError("geometry_only F08 fixture unexpectedly contains constraints")
    return found


def _check_offset_geometry(sketch: dict, label: str) -> dict:
    _check_frame_and_units(sketch, label)
    geometry = sketch.get("geometry")
    if not isinstance(geometry, list):
        raise AssertionError(f"{label} omitted geometry list")
    lines = [item for item in geometry if isinstance(item, dict) and item.get("kind") == "LINE"]
    if len(lines) != 4:
        raise AssertionError(f"{label} must contain two retained source and two offset LINEs, got {len(lines)}")

    source_records = {}
    for name, endpoints in BASE_LINES.items():
        source = _one_segment(geometry, endpoints[0], endpoints[1], label + " retained source " + name)
        source_records[name] = source

    output_records = {
        name: _one_segment(geometry, endpoints[0], endpoints[1], label + " offset " + name)
        for name, endpoints in EXPECTED_OFFSET_LINES.items()
    }
    horizontal_source = source_records["horizontal"]
    vertical_source = source_records["vertical"]
    horizontal_output = output_records["horizontal"]
    vertical_output = output_records["vertical"]
    horizontal_start = _finite_vector(horizontal_output.get("start"), label + " offset horizontal start")
    horizontal_end = _finite_vector(horizontal_output.get("end"), label + " offset horizontal end")
    vertical_start = _finite_vector(vertical_output.get("start"), label + " offset vertical start")
    vertical_end = _finite_vector(vertical_output.get("end"), label + " offset vertical end")
    _assert_close(abs(horizontal_start[1] - 0.0), 2.0, LENGTH_TOLERANCE_MM,
                  label + " horizontal support distance")
    _assert_close(abs(vertical_start[0] - 10.0), 2.0, LENGTH_TOLERANCE_MM,
                  label + " vertical support distance")
    join_candidates = [
        (_distance(left_point, right_point), left_point, right_point)
        for left_point in (horizontal_start, horizontal_end)
        for right_point in (vertical_start, vertical_end)
    ]
    join_error, join_horizontal, join_vertical = min(join_candidates, key=lambda item: item[0])
    if join_error > POSITION_TOLERANCE_MM:
        raise AssertionError(f"{label} output chain is disconnected at its L join by {join_error:.12g} mm")
    join_point = [(join_horizontal[index] + join_vertical[index]) / 2.0 for index in range(3)]
    _assert_point(join_point, [8.0, 2.0, 0.0], label + " offset join")

    # Check that the preserved native sources still have their original endpoints.
    for name, endpoints in BASE_LINES.items():
        record = source_records[name]
        if not _segment_matches(record, endpoints[0], endpoints[1]):
            raise AssertionError(f"{label} retained source {name} endpoints changed")
    return {
        "retained_source_curve_ids_from_readback": {name: item["journal_id"] for name, item in source_records.items()},
        "offset_curve_ids_from_readback": {name: item["journal_id"] for name, item in output_records.items()},
        "horizontal_support_distance_mm": abs(horizontal_start[1]),
        "vertical_support_distance_mm": abs(vertical_start[0] - 10.0),
        "join_gap_mm": join_error,
        "expected_join_mm": [8.0, 2.0, 0.0],
        "measured_join_mm": join_point,
    }


def _check_offset_schemas(tools: list) -> dict:
    by_name = {item.get("name"): item for item in tools if isinstance(item, dict)}
    common = {
        "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
        "curve_chain", "distance_mm", "side", "retain_original", "join_mode",
    }
    found = {}
    for name in ("nx_prepare_sketch_offset", "nx_edit_sketch_offset"):
        tool = by_name.get(name)
        if not isinstance(tool, dict):
            raise AssertionError(f"installed tools/list omitted {name}")
        schema = tool.get("inputSchema")
        if not isinstance(schema, dict):
            raise AssertionError(f"installed {name} omitted inputSchema")
        properties = schema.get("properties")
        required = set(schema.get("required", []))
        if not isinstance(properties, dict) or not common <= set(properties) or not common <= required:
            raise AssertionError(f"installed {name} schema omitted required F08 fields")
        if properties.get("side", {}).get("enum") != ["left", "right"]:
            raise AssertionError(f"installed {name} schema has an unexpected side enum")
        if properties.get("join_mode", {}).get("enum") != ["extension", "arc"]:
            raise AssertionError(f"installed {name} schema has an unexpected join_mode enum")
        if properties.get("retain_original", {}).get("const") is not True:
            raise AssertionError(f"installed {name} schema does not require retain_original=true")
        distance_schema = properties.get("distance_mm", {})
        if (not isinstance(distance_schema, dict)
                or distance_schema.get("type") != "number"
                or type(distance_schema.get("exclusiveMinimum")) not in (int, float)
                or distance_schema["exclusiveMinimum"] != 0):
            raise AssertionError(f"installed {name} schema does not require a positive distance_mm")
        chain_schema = properties.get("curve_chain", {})
        item_schema = chain_schema.get("items", {}) if isinstance(chain_schema, dict) else {}
        item_properties = item_schema.get("properties", {}) if isinstance(item_schema, dict) else {}
        direction_schema = item_properties.get("direction", {}) if isinstance(item_properties, dict) else {}
        if (not isinstance(chain_schema, dict) or chain_schema.get("minItems", 0) < 1
                or chain_schema.get("maxItems") != 128
                or not isinstance(item_properties, dict)
                or not {"curve_id", "direction"} <= set(item_properties)
                or not {"curve_id", "direction"} <= set(item_schema.get("required", []))
                or direction_schema.get("enum") != ["forward", "reverse"]
                or item_schema.get("additionalProperties") is not False):
            raise AssertionError(f"installed {name} schema has an unexpected ordered curve_chain contract")
        if schema.get("additionalProperties") is not False:
            raise AssertionError(f"installed {name} must reject unknown fields")
        found[name] = schema
    return found


def _tool_names_and_version(client: McpStdioClient, report: dict) -> tuple[list, dict]:
    initialize = client.request("initialize", {
        "protocolVersion": PROTOCOL_VERSION,
        "capabilities": {},
        "clientInfo": {"name": "f08-installed-mcp-smoke", "version": "1"},
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
        raise AssertionError(f"installed MCP must expose exactly {EXPECTED_TOOL_COUNT} unique tools; got {len(set(names))}")
    if set(names) != EXPECTED_TOOLS:
        missing = sorted(EXPECTED_TOOLS - set(names))
        unexpected = sorted(set(names) - EXPECTED_TOOLS)
        raise AssertionError(f"installed F08 tool set differs; missing={missing!r}, unexpected={unexpected!r}")
    schemas = _check_offset_schemas(tools)
    report["tool_count"] = len(names)
    report["tool_names"] = sorted(names)
    report["offset_tool_schemas"] = _redact(schemas)
    report["tools_list"] = _redact(tools)
    return tools, schemas


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


def _prepare_sketch_chain(call_operation, credentials: dict,
                          revision_id: str, sketch_id: str, curve_ids: dict[str, str]) -> dict:
    arguments = {
        "operation_id": "f08-offset-prepare-" + uuid.uuid4().hex,
        **credentials,
        "revision_id": revision_id,
        "sketch_id": sketch_id,
        "curve_chain": [
            {"curve_id": curve_ids["horizontal"], "direction": "forward"},
            {"curve_id": curve_ids["vertical"], "direction": "forward"},
        ],
        "distance_mm": 2.0,
        "side": "left",
        "retain_original": True,
        "join_mode": "extension",
    }
    result = call_operation("nx_prepare_sketch_offset", arguments)
    plan = result.get("plan")
    if not isinstance(plan, dict) or not isinstance(plan.get("plan_id"), str) or not plan["plan_id"]:
        raise AssertionError("nx_prepare_sketch_offset omitted plan.plan_id")
    if plan.get("sketch_id") != sketch_id:
        raise AssertionError("prepared F08 offset plan targets a different sketch")
    if plan.get("revision_id") != revision_id:
        raise AssertionError("prepared F08 offset plan targets a different revision")
    expected_chain = [
        {"curve_id": curve_ids["horizontal"], "direction": "forward"},
        {"curve_id": curve_ids["vertical"], "direction": "forward"},
    ]
    if plan.get("side") != "left":
        raise AssertionError("F08 prepare plan side must be left")
    plan_distance = plan.get("distance_mm")
    if (isinstance(plan_distance, bool) or not isinstance(plan_distance, (int, float))
            or not math.isfinite(float(plan_distance))):
        raise AssertionError("F08 prepare plan distance_mm must be finite")
    _assert_close(float(plan_distance), 2.0, 1.0e-12, "F08 prepare plan distance_mm")
    if plan.get("join_mode") != "extension" or plan.get("retain_original") is not True:
        raise AssertionError("F08 prepare plan must expose extension and retain_original=true")
    plan_chain = plan.get("curve_chain")
    if (not isinstance(plan_chain, list) or len(plan_chain) != len(expected_chain)
            or any(not isinstance(actual, dict)
                   or actual.get("curve_id") != expected["curve_id"]
                   or actual.get("direction") != expected["direction"]
                   for actual, expected in zip(plan_chain, expected_chain))):
        raise AssertionError("F08 prepare plan did not preserve the ordered forward curve chain")
    if plan.get("source_chain_closed") is not False:
        raise AssertionError("F08 prepare plan must identify the selected source chain as open")
    preview = plan.get("preview")
    if not isinstance(preview, dict):
        raise AssertionError("F08 prepare plan omitted its preview object")
    if preview.get("preview_type") != "source_chain_and_requested_offset":
        raise AssertionError("F08 prepare preview_type does not identify the source chain and requested offset")
    source_chain = preview.get("source_chain")
    if not isinstance(source_chain, dict) or source_chain.get("count") != len(expected_chain):
        raise AssertionError("F08 prepare preview source_chain must be a counted native-curve record")
    source_records = source_chain.get("curves")
    if not isinstance(source_records, list) or len(source_records) != len(expected_chain):
        raise AssertionError("F08 prepare preview source_chain omitted its native curves array")
    for index, (record, expected) in enumerate(zip(source_records, expected_chain)):
        if (not isinstance(record, dict)
                or record.get("journal_id") != expected["curve_id"]
                or record.get("direction") != expected["direction"]):
            raise AssertionError(f"F08 preview source curve {index} differs from the selected native curve")
    if preview.get("source_chain_closed") is not False:
        raise AssertionError("F08 prepare preview must identify the source chain as open")
    if (preview.get("distance_mm") != 2.0 or preview.get("side") != "left"
            or preview.get("retain_original") is not True or preview.get("join_mode") != "extension"):
        raise AssertionError("F08 prepare preview omitted or changed the requested offset inputs")
    if preview.get("native_geometry_evaluation") != "deferred_until_atomic_execute":
        raise AssertionError("F08 prepare preview must defer native geometry evaluation until the compound edit")
    return {"arguments": arguments, "result": result, "plan": plan}


def _check_edit_status(status: dict, operation_id: str, sketch_id: str) -> dict:
    state = _status_state(status)
    result = _operation_result(status)
    if result is None:
        raise AssertionError("nx_edit_sketch_offset omitted its operation result")
    if status.get("operation_id") not in (None, operation_id):
        raise AssertionError("nx_edit_sketch_offset returned a different operation_id")
    if state != "SUCCEEDED" or status.get("success") is not True:
        raise AssertionError(f"nx_edit_sketch_offset must be SUCCEEDED/success=true, got {state!r}")
    if status.get("verified") is not True or result.get("verified") is not True:
        raise AssertionError("nx_edit_sketch_offset SUCCEEDED only counts when verified=true")
    if result.get("mutation_state") != "APPLIED":
        raise AssertionError("nx_edit_sketch_offset mutation_state must be APPLIED")
    if result.get("combined_edit") is not True:
        raise AssertionError("nx_edit_sketch_offset did not identify its prepare/execute/verify compound")
    if result.get("kind") not in {"edit_sketch_offset", "execute_edit_sketch_offset"}:
        raise AssertionError("nx_edit_sketch_offset result kind does not match F08")
    if result.get("sketch_id") != sketch_id:
        raise AssertionError("nx_edit_sketch_offset result sketch_id differs from the selected sketch")
    if not isinstance(result.get("plan_id"), str) or not result["plan_id"]:
        raise AssertionError("nx_edit_sketch_offset result omitted plan_id")
    if not isinstance(result.get("verification"), dict):
        raise AssertionError("nx_edit_sketch_offset result omitted independent verification")
    if not isinstance(result.get("evidence"), (dict, list)):
        raise AssertionError("nx_edit_sketch_offset result omitted evidence")
    verification = result["verification"]
    checks = verification.get("checks")
    required_checks = {
        "offset_geometry", "source_curves", "sketch_solver_status", "sketch_constraints",
        "sketch_frames", "reference_status", "non_target_geometry", "history", "bodies",
    }
    if not isinstance(checks, dict) or not required_checks <= set(checks):
        raise AssertionError("F08 verification omitted one or more required check records")
    bad_checks = {name for name in required_checks
                  if not isinstance(checks[name], dict) or checks[name].get("status") != "VERIFIED"}
    if bad_checks:
        raise AssertionError("F08 verification contains a non-VERIFIED required check")
    if result.get("output_closed") is not False:
        raise AssertionError("F08 compound edit must report the open output chain")
    _check_reported_output_geometry(result.get("output_curve_records"), "F08 top-level output geometry")

    verification_result = verification.get("evidence")
    if not isinstance(verification_result, dict):
        raise AssertionError("F08 VerifyPlan evidence omitted its adapter result")
    if result.get("evidence") != verification_result:
        raise AssertionError("F08 compound evidence differs from the VerifyPlan result")
    measured = verification_result.get("evidence")
    live_geometry = measured.get("live_geometry") if isinstance(measured, dict) else None
    if not isinstance(live_geometry, dict):
        raise AssertionError("F08 VerifyPlan result evidence omitted live_geometry")
    _check_native_offset_evidence(live_geometry, "F08 VerifyPlan live geometry")
    offset_check = checks["offset_geometry"]
    if offset_check.get("evidence") != live_geometry:
        raise AssertionError("F08 VerifyPlan offset check does not carry the live geometry checker evidence")
    measured_offset = live_geometry.get("offset_geometry")
    if not isinstance(measured_offset, dict):
        raise AssertionError("F08 live_geometry omitted offset_geometry")
    if (measured_offset.get("valid") is not True
            or measured_offset.get("output_closed") is not False
            or measured_offset.get("requested_closed") is not False
            or measured_offset.get("requested_side") != "left"):
        raise AssertionError("F08 measured offset geometry validity, closure, or side differs from the request")
    offset_distance = measured_offset.get("distance_mm")
    if (isinstance(offset_distance, bool) or not isinstance(offset_distance, (int, float))
            or not math.isfinite(float(offset_distance))):
        raise AssertionError("F08 measured offset geometry omitted finite distance_mm")
    _assert_close(float(offset_distance), 2.0, LENGTH_TOLERANCE_MM, "F08 measured offset distance_mm")
    _check_reported_output_geometry(measured_offset.get("output_curve_records"),
                                    "F08 measured live output geometry")
    _check_per_source_geometry(measured_offset.get("per_source_curve"))
    topology = measured_offset.get("topology")
    if not isinstance(topology, dict):
        raise AssertionError("F08 measured offset geometry omitted topology")
    if (topology.get("valid_chain") is not True or topology.get("closed") is not False
            or topology.get("open_end_count") != 2 or topology.get("component_count") != 1
            or topology.get("self_intersection_count") != 0):
        raise AssertionError("F08 measured offset topology does not describe one connected open chain")
    return result


def _check_native_offset_evidence(evidence: object, label: str) -> dict:
    """Check the direct execution/VerifyPlan evidence shape from the native adapter."""
    if not isinstance(evidence, dict):
        raise AssertionError(f"{label} must be an object")
    offset_geometry = evidence.get("offset_geometry")
    if not isinstance(offset_geometry, dict):
        raise AssertionError(f"{label} omitted offset_geometry")
    if evidence.get("output_closed") is not False:
        raise AssertionError(f"{label} must report the requested open output chain")
    output_records = evidence.get("output_curve_records")
    _check_reported_output_geometry(output_records, label + " output geometry")
    if offset_geometry.get("output_closed") is not False:
        raise AssertionError(f"{label} offset_geometry closure differs from its top-level evidence")
    if offset_geometry.get("output_curve_records") != output_records:
        raise AssertionError(f"{label} top-level and offset_geometry curve records differ")
    checks = evidence.get("checks")
    if not isinstance(checks, dict) or checks.get("offset_geometry") != offset_geometry:
        raise AssertionError(f"{label} checks.offset_geometry differs from its checker evidence")
    return offset_geometry


def _check_reported_output_geometry(records: object, label: str) -> dict:
    if not isinstance(records, list) or len(records) != 2:
        raise AssertionError(f"{label} must contain exactly two output curve records")
    normalized = []
    for index, record in enumerate(records):
        if not isinstance(record, dict):
            raise AssertionError(f"{label} record {index} is not an object")
        object_type = str(record.get("object_type", "")).casefold()
        if object_type not in {"line", "nxopen.line"}:
            raise AssertionError(f"{label} record {index} is not a native Line")
        journal_id = record.get("journal_id")
        if not isinstance(journal_id, str) or not journal_id:
            raise AssertionError(f"{label} record {index} omitted journal_id")
        if record.get("tag") is None:
            raise AssertionError(f"{label} record {index} omitted native tag evidence")
        start = _finite_vector(record.get("start_mm"), f"{label} record {index} start_mm")
        end = _finite_vector(record.get("end_mm"), f"{label} record {index} end_mm")
        normalized.append({"kind": "LINE", "journal_id": journal_id, "start": start, "end": end})
    if len({record["journal_id"] for record in normalized}) != 2:
        raise AssertionError(f"{label} did not identify two distinct output curves")
    for name, endpoints in EXPECTED_OFFSET_LINES.items():
        _one_segment(normalized, endpoints[0], endpoints[1], label + " " + name)
    return {"output_curve_count": len(normalized), "coordinates_verified": True}


def _check_per_source_geometry(records: object) -> dict:
    if not isinstance(records, list) or len(records) != 2:
        raise AssertionError("F08 per_source_curve evidence must contain both selected curves")
    indexes = set()
    measured_offsets = []
    residuals = []
    for index, record in enumerate(records):
        if not isinstance(record, dict):
            raise AssertionError(f"F08 per_source_curve record {index} is not an object")
        if not isinstance(record.get("source_curve_id"), str) or not record["source_curve_id"]:
            raise AssertionError("F08 per_source_curve omitted source_curve_id")
        if record.get("source_curve_tag") is None or record.get("unique_output_match") is not True:
            raise AssertionError("F08 per_source_curve did not uniquely match a native source tag")
        matched_index = record.get("output_curve_index")
        if isinstance(matched_index, bool) or not isinstance(matched_index, int) or matched_index not in {0, 1}:
            raise AssertionError("F08 per_source_curve omitted a valid output_curve_index")
        indexes.add(matched_index)
        offset = record.get("measured_signed_offset_mm")
        residual = record.get("offset_residual_mm")
        if (isinstance(offset, bool) or not isinstance(offset, (int, float))
                or not math.isfinite(float(offset))):
            raise AssertionError("F08 per_source_curve omitted a finite measured signed offset")
        if (isinstance(residual, bool) or not isinstance(residual, (int, float))
                or not math.isfinite(float(residual))):
            raise AssertionError("F08 per_source_curve omitted a finite offset residual")
        _assert_close(float(offset), 2.0, LENGTH_TOLERANCE_MM,
                      f"F08 source curve {index} signed left offset")
        if abs(float(residual)) > LENGTH_TOLERANCE_MM:
            raise AssertionError(f"F08 source curve {index} offset residual exceeds tolerance")
        measured_offsets.append(float(offset))
        residuals.append(float(residual))
    if indexes != {0, 1}:
        raise AssertionError("F08 per_source_curve records do not map to both output curves")
    return {"measured_signed_offsets_mm": measured_offsets, "residuals_mm": residuals}


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    run_id = uuid.uuid4().hex
    mode = "ready" if args.ready else "part"
    report: dict = {
        "test": "F08_installed_mcp_smoke",
        "run_id": run_id,
        "mode": mode,
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "passed": False,
        "objective": "Verify installed F08 prepare preview, one compound offset edit, native geometry, save/reopen, and safe ownership return.",
        "traceability": [
            "F08 positive distance, traversal direction, side, retain_original, and join_mode request contract",
            "F08 left 2 mm extension offset of an open two-Line L chain",
            "F08 combined prepare/execute/verify result and independent native geometry readback",
            "F08 installed MCP version/tool count and save/reopen roundtrip",
            "F08 safe ownership return",
        ],
        "test_class": "installed MCP integration and NX sketch subsystem validation",
        "configuration": {
            "expected_mcp_version": EXPECTED_SERVER_VERSION,
            "expected_tool_count": EXPECTED_TOOL_COUNT,
            "position_tolerance_mm": POSITION_TOLERANCE_MM,
            "length_tolerance_mm": LENGTH_TOLERANCE_MM,
            "sample_size": 1,
            "measurement_uncertainty": "digital NX model values; no physical instrument used",
            "reliability_or_compliance_inference": False,
        },
        "safety_controls": {
            "NX_launch": "not invoked; this harness does not call nx_ensure_ready",
            "ownership_scope": "saved independent fixture copy; caller supplies the unique .prt path",
            "path_and_dirty_gate": "nx_takeover must confirm the active Work Part matches the supplied saved part and is clean before model writes",
            "source_part_save": "never saves over supplied path; Save Copy uses a new unique roundtrip path",
            "unknown_edit_outcome": "query nx_get_operation only; never replay the F08 edit; do not return ownership when writes are halted or outcome is unknown",
        },
        "instrumentation": "installed MCP results and native NX sketch inspection, plus independent Python coordinate and distance checks",
        "calibration": "no physical instrument; digital CAD tolerances recorded above",
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
            # Until the operation reaches a known terminal state, do not release
            # ownership even if the bridge status looks momentarily idle.
            unknown_write_outcome = True
        initial, entry = call_raw(name, arguments, allow_tool_error=tracks_write)
        if entry.get("is_error") is True:
            code = _error_code(initial) or "UNKNOWN"
            unknown_write_outcome = tracks_write and code in {
                "OPERATION_OUTCOME_UNKNOWN", "CLIENT_OUTCOME_UNKNOWN", "UNKNOWN_OPERATION_OUTCOME",
            }
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
            # One read-only query is allowed. A prepare or mutation is never resubmitted.
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
            raise AssertionError(f"{name} operation SUCCEEDED without a result object")
        persist()
        return result

    def record_case(case_id: str, request: dict, retest_criterion: str) -> dict:
        nonlocal active_case
        case = {
            "case_id": case_id,
            "state": "RUNNING",
            "request": _redact(request),
            "reproduction_steps": [
                "Use a new clean, saved millimeter .prt under workspace/v2/models and open it as the NX Work Part.",
                "Run this script with that exact --part-path and a new evidence path under workspace/v2/evidence.",
                f"Repeat {case_id} with the recorded arguments and compare the native sketch readback to its expected coordinates.",
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

    def edit_offset(arguments: dict, sketch_id: str) -> tuple[dict, dict]:
        nonlocal unknown_write_outcome
        if client is None:
            raise RuntimeError("installed MCP client is not initialized")
        operation_id = arguments["operation_id"]
        unknown_write_outcome = True
        status, entry = call_raw("nx_edit_sketch_offset", arguments, allow_tool_error=True)
        state = _status_state(status)
        found_id = status.get("operation_id") or operation_id
        operation = status.get("operation") if isinstance(status.get("operation"), dict) else None
        if state in {"QUEUED", "RUNNING"}:
            current = dict(operation) if isinstance(operation, dict) else dict(status)
            current.setdefault("operation_id", found_id)
            current = wait_for_operation(current, "nx_edit_sketch_offset")
            current_state = _status_state(current)
            unknown_write_outcome = current_state not in {"SUCCEEDED", "FAILED", "CANCELLED"}
            status = _completed_edit_status(status, current)
            entry["completed_operation"] = _redact(current)
        elif state == "UNKNOWN" or status.get("outcome_unknown") is True:
            # Query the same operation id once. Never replay an uncertain model write.
            unknown_write_outcome = True
            queried, query_entry = call_raw("nx_get_operation", {"operation_id": found_id},
                                            allow_tool_error=True)
            query_entry["unknown_outcome_query_for"] = found_id
            entry["unknown_outcome_query"] = _redact(queried)
            queried_state = _status_state(queried)
            if queried_state in {"SUCCEEDED", "FAILED", "CANCELLED"}:
                unknown_write_outcome = False
                status = _completed_edit_status(status, queried)
            else:
                unknown_write_outcome = True
                status = {**status, "state": "UNKNOWN", "operation": _operation_record(queried),
                          "outcome_unknown": True, "success": False, "verified": False}
        elif _error_code(status) in {
                "OPERATION_OUTCOME_UNKNOWN", "CLIENT_OUTCOME_UNKNOWN", "UNKNOWN_OPERATION_OUTCOME"}:
            unknown_write_outcome = True
        elif state in {"SUCCEEDED", "FAILED", "CANCELLED"}:
            unknown_write_outcome = False
        elif entry.get("is_error") is True:
            # A structured precondition/tool error is a known non-mutation; an
            # ambiguous transport failure would have raised before this point.
            unknown_write_outcome = False
        result = _check_edit_status(status, operation_id, sketch_id)
        entry["validated_result"] = _redact(result)
        persist()
        return result, status

    def return_ownership() -> dict:
        if not credentials:
            return {"skipped": "no current ownership credentials"}
        if unknown_write_outcome:
            result = {"skipped": "F08 write outcome is unknown; ownership must remain with the bridge"}
            report["ownership_return"] = result
            persist()
            return result
        status = call_raw("nx_bridge_status", {})[0]
        in_flight = _int_value(status.get("in_flight", status.get("active_operations", -1)),
                               "bridge in_flight")
        if (status.get("ownership_state") != "AI_OWNED" or status.get("writes_halted") is not False
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
        returned = call_raw("nx_return", credentials)[0]
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("nx_return did not confirm safe HUMAN_OWNED state")
        credentials.clear()
        final = call_raw("nx_bridge_status", {})[0]
        final_in_flight = _int_value(final.get("in_flight", final.get("active_operations", -1)),
                                     "final bridge in_flight")
        if (final.get("ownership_state") != "HUMAN_OWNED" or final.get("writes_halted") is not False
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
        tools, _ = _tool_names_and_version(client, report)

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
            report.update(passed=True, diagnostic_only=True,
                          ready_result="installed 0.8.0 MCP, 25 tools, and cached bridge status verified without NX launch or ownership call")
            return 0

        if initial_status.get("ownership_state") != "HUMAN_OWNED" or in_flight != 0:
            raise AssertionError("model mode requires an idle HUMAN_OWNED NX session before takeover")
        if part_path is None:
            raise AssertionError("model mode requires --part-path")
        nx_pid = _int_value(initial_status.get("pid"), "bridge NX pid")

        takeover_case = record_case(
            "F08_saved_fixture_takeover_gate",
            {"expected_pid": nx_pid, "expected_part_path": str(part_path),
             "artifact_scope": "saved_independent_copy", "authorize": True},
            "Using a fresh clean saved millimeter fixture, nx_takeover must verify exact path identity and IsModified=false; then rerun the smoke against the same newly generated fixture and require every F08 geometry and save/reopen check to pass.",
        )
        takeover = call_raw("nx_takeover", {
            "expected_pid": nx_pid,
            "expected_part_path": str(part_path),
            "artifact_scope": "saved_independent_copy",
            "authorize": True,
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
        _require_inspected_part_path(initial_part, part_path, "initial blank fixture")
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
            raise AssertionError("F08 smoke requires a blank saved part without existing sketches")
        if not isinstance(existing_bodies, list) or existing_bodies:
            raise AssertionError("F08 smoke requires a blank saved part without existing bodies")
        report["initial_part_inspection"] = _redact(initial_part)

        fixture_case = record_case(
            "F08_create_open_L_fixture",
            {"plane": "XY", "origin_mm": [0.0, 0.0, 0.0],
             "geometries": [{"id": "horizontal", "type": "line", "start_mm": [0.0, 0.0], "end_mm": [10.0, 0.0]},
                            {"id": "vertical", "type": "line", "start_mm": [10.0, 0.0], "end_mm": [10.0, 10.0]}]},
            "Create the two-line geometry_only XY sketch on a new blank fixture; nx_verify_part must be verified and independent inspection must match both source lines within 1e-6 mm.",
        )
        geometries = [
            {"id": "f08-horizontal", "type": "line", "start_mm": [0.0, 0.0], "end_mm": [10.0, 0.0]},
            {"id": "f08-vertical", "type": "line", "start_mm": [10.0, 0.0], "end_mm": [10.0, 10.0]},
        ]
        sketch_prepared = call_operation("nx_prepare_sketch", {
            "operation_id": "f08-sketch-prepare-" + uuid.uuid4().hex,
            **credentials,
            "revision_id": session["revision_id"],
            "plane": "XY",
            "origin_mm": [0.0, 0.0, 0.0],
            "constraint_mode": "geometry_only",
            "geometries": geometries,
        })
        sketch_plan = sketch_prepared.get("plan")
        if not isinstance(sketch_plan, dict) or not isinstance(sketch_plan.get("plan_id"), str):
            raise AssertionError("nx_prepare_sketch omitted plan.plan_id")
        execute_id = "f08-sketch-execute-" + uuid.uuid4().hex
        sketch_executed = call_operation("nx_execute_plan", {
            "operation_id": execute_id, **credentials, "plan_id": sketch_plan["plan_id"],
        })
        if sketch_executed.get("mutation_state") != "APPLIED":
            raise AssertionError("F08 two-line sketch did not return mutation_state=APPLIED")
        sketch_verified = call_operation("nx_verify_part", {
            "operation_id": "f08-sketch-verify-" + uuid.uuid4().hex,
            **credentials,
            "execute_operation_id": execute_id,
        })
        if sketch_verified.get("verified") is not True:
            raise AssertionError("F08 source sketch did not pass nx_verify_part")
        sketch_id = sketch_executed.get("sketch_id") or sketch_verified.get("sketch_id")
        if not isinstance(sketch_id, str) or not sketch_id:
            raise AssertionError("F08 fixture sketch creation omitted sketch_id")
        session = update_revision("after fixture sketch creation", part_path)
        initial_sketch = inspect_sketch(sketch_id, "initial F08 fixture")
        source_curves = _check_initial_geometry(initial_sketch)
        curve_ids = {name: item["journal_id"] for name, item in source_curves.items()}
        fixture_case.update(state="PASS", sketch_id=sketch_id,
                            source_curve_ids=curve_ids,
                            native_geometry=_redact(initial_sketch),
                            verification=_redact(sketch_verified))
        persist()

        offset_case = record_case(
            "F08_left_2mm_open_L_extension",
            {"side": "left", "curve_chain": [
                {"curve_id": curve_ids["horizontal"], "direction": "forward"},
                {"curve_id": curve_ids["vertical"], "direction": "forward"}],
             "distance_mm": 2.0, "retain_original": True, "join_mode": "extension",
             "expected_offset_mm": [[0.0, 2.0, 0.0], [8.0, 2.0, 0.0], [8.0, 10.0, 0.0]]},
            "On a fresh matching fixture, require prepare to expose the ordered forward chain and requested left/2 mm/extension/retain-original inputs without changing revision or native geometry; require one SUCCEEDED/APPLIED/verified compound edit and direct inspection to show two retained originals plus the connected offset chain. Save/reopen and rerun the same native geometry oracle before marking the retest PASS.",
        )

        # Prepare must be a read-only preview: compare both revision and native
        # source coordinates immediately before and after it.
        before_preview_session = update_revision("before F08 prepare preview", part_path)
        before_preview_sketch = inspect_sketch(sketch_id, "before F08 prepare preview")
        _check_initial_geometry(before_preview_sketch)
        prepared_offset = _prepare_sketch_chain(
            call_operation, credentials, before_preview_session["revision_id"], sketch_id, curve_ids,
        )
        after_preview_session = update_revision("after F08 prepare preview", part_path)
        after_preview_sketch = inspect_sketch(sketch_id, "after F08 prepare preview")
        _check_initial_geometry(after_preview_sketch)
        if after_preview_session.get("revision_id") != before_preview_session.get("revision_id"):
            raise AssertionError("read-only F08 prepare changed the model revision")
        offset_case["prepare_preview"] = _redact(prepared_offset)
        offset_case["preview_revision_unchanged"] = True
        offset_case["native_geometry_after_prepare"] = _redact(after_preview_sketch)
        persist()

        # The combined edit receives a fresh revision and is submitted exactly once.
        before_edit_session = update_revision("before F08 compound edit", part_path)
        before_edit_sketch = inspect_sketch(sketch_id, "before F08 compound edit")
        _check_initial_geometry(before_edit_sketch)
        edit_arguments = {
            "operation_id": "f08-offset-edit-" + uuid.uuid4().hex,
            **credentials,
            "revision_id": before_edit_session["revision_id"],
            "sketch_id": sketch_id,
            "curve_chain": [
                {"curve_id": curve_ids["horizontal"], "direction": "forward"},
                {"curve_id": curve_ids["vertical"], "direction": "forward"},
            ],
            "distance_mm": 2.0,
            "side": "left",
            "retain_original": True,
            "join_mode": "extension",
        }
        edit_result, edit_status = edit_offset(edit_arguments, sketch_id)
        offset_case["compound_operation"] = _redact(edit_status)
        offset_case["operation_result"] = _redact(edit_result)
        after_edit_session = update_revision("after F08 compound edit", part_path)
        edited_sketch = inspect_sketch(sketch_id, "independent F08 geometry readback")
        geometry_result = _check_offset_geometry(edited_sketch, "F08 post-edit readback")
        offset_case.update(state="PASS", independent_geometry_readback=_redact(edited_sketch),
                           independent_numeric_check=geometry_result,
                           revision_id=after_edit_session["revision_id"])
        persist()

        model_root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
        roundtrip_path = model_root / f"F08-mcp-{run_id}-roundtrip.prt"
        if roundtrip_path.exists() or _canonical_path(roundtrip_path) == _canonical_path(part_path):
            raise FileExistsError("F08 unique Save Copy path already exists or collides with source")
        roundtrip_case = record_case(
            "F08_save_reopen_and_inspect",
            {"target_path": str(roundtrip_path), "reopen": True},
            "nx_save_copy must report saved=true and reopened=true with associations, expression, geometry, and history all VERIFIED; direct inspection after reopening must reproduce the two retained source lines and 2 mm connected offset chain within 1e-6 mm; then return ownership safely.",
        )
        saved = call_operation("nx_save_copy", {
            "operation_id": "f08-save-" + uuid.uuid4().hex,
            **credentials,
            "target_path": str(roundtrip_path),
            "reopen": True,
        })
        if saved.get("saved") is not True or saved.get("reopened") is not True:
            raise AssertionError("nx_save_copy did not report saved=true and reopened=true")
        save_checks = saved.get("checks")
        required_save_checks = {"associations", "expression", "geometry", "history"}
        if not isinstance(save_checks, dict) or set(save_checks) != required_save_checks:
            raise AssertionError("nx_save_copy omitted required association, expression, geometry, or history checks")
        bad_checks = {key for key, value in save_checks.items()
                      if not isinstance(value, dict) or value.get("status") != "VERIFIED"}
        if bad_checks:
            raise AssertionError("nx_save_copy contained a non-VERIFIED roundtrip check")
        new_owner = saved.get("new_ownership_id")
        new_epoch = saved.get("new_binding_epoch")
        if (not isinstance(new_owner, str) or not new_owner or isinstance(new_epoch, bool)
                or not isinstance(new_epoch, int) or new_epoch < 1):
            raise AssertionError("nx_save_copy omitted rotated ownership credentials")
        credentials.update(ownership_id=new_owner, binding_epoch=new_epoch)
        reopened_path = saved.get("saved_path") or str(roundtrip_path)
        if _canonical_path(reopened_path) != _canonical_path(roundtrip_path):
            raise AssertionError("nx_save_copy saved path differs from requested unique roundtrip path")
        roundtrip_case["save_copy"] = _redact(saved)

        reopened_part = call_raw("nx_inspect_part", credentials)[0]
        _require_inspected_part_path(reopened_part, roundtrip_path, "reopened F08 part inspection")
        reopened_sketches = reopened_part.get("sketches")
        if not isinstance(reopened_sketches, list) or len(reopened_sketches) != 1:
            raise AssertionError("reopened F08 test part must contain exactly one sketch")
        reopened_sketch_id = reopened_sketches[0].get("journal_id") if isinstance(reopened_sketches[0], dict) else None
        if not isinstance(reopened_sketch_id, str) or not reopened_sketch_id:
            raise AssertionError("reopened F08 sketch omitted journal_id")
        reopened_sketch = inspect_sketch(reopened_sketch_id, "roundtrip F08 native readback")
        reopened_numeric = _check_offset_geometry(reopened_sketch, "F08 reopened readback")
        source_after = _file_metadata(part_path)
        source_before = report["source_part_file"]["before_save_copy"]
        source_unchanged = source_after == source_before
        report["source_part_file"].update(after_save_copy=source_after,
                                          size_and_mtime_unchanged=source_unchanged)
        if not source_unchanged:
            raise AssertionError("Save Copy changed supplied source .prt size or modification time")
        roundtrip_case.update(state="PASS", reopened_part_path=str(roundtrip_path),
                              reopened_inspection=_redact(reopened_part),
                              reopened_sketch_readback=_redact(reopened_sketch),
                              reopened_numeric_check=reopened_numeric,
                              source_part_file_metadata=_redact(report["source_part_file"]))
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
        failure_case_id = active_case.get("case_id") if isinstance(active_case, dict) else "F08_installed_harness"
        generic_retest = (
            "Use a new unique clean saved millimeter fixture and new evidence path; require installed version 0.8.0 and 25 tools, preview the ordered L chain on left at 2 mm with extension and retained source, execute once and query only if outcome is uncertain, independently verify both native offset segments and the (8,2) connection, confirm Save Copy/reopen checks, then safely return ownership."
        )
        failure = {
            "case_id": failure_case_id,
            "failure": f"{type(exc).__name__}: {exc}",
            "reproduction_steps": (active_case.get("reproduction_steps") if isinstance(active_case, dict) else [
                "Confirm the installed MCP and local NX bridge are available without launching NX.",
                "Use a new clean saved millimeter .prt under workspace/v2/models and a new evidence output path.",
                f"Rerun this script in {mode} mode and repeat {failure_case_id} from the recorded calls and readbacks.",
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
                # Never return ownership while a write is unknown or the bridge is halted.
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
