"""Installed MCP smoke for F10 native sketch projection.

``--ready`` checks only the installed MCP protocol, tool schemas, projection
capability, and cached idle bridge status. Model mode requires a new saved,
clean standalone millimeter part under ``workspace/v2/models`` containing two
empty target sketches and caller-selected same-part source Edge/Curve IDs.
One sketch is used for an associative projection along its positive normal;
the other is used for a non-associative projection along its negative normal.
The smoke also verifies that an oblique prepare is rejected without a write.

This script does not start or control NX. It starts only the installed MCP
stdio child, never calls ``nx_ensure_ready``, never saves over the supplied
part, and uses a unique Save Copy path. Its independent evidence is limited to
the installed protocol/capability, prepare invariance, operation lifecycle,
adapter verification, and direct target-sketch readback. It does not establish
physical accuracy, reliability, or standards compliance.
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
from f01_f02_mcp_client import McpClientError, McpStdioClient  # noqa: E402

_ORACLE_PATH = ROOT / "tools" / "v2" / "f10-projection-oracle.py"
_ORACLE_SPEC = importlib.util.spec_from_file_location("f10_projection_oracle", _ORACLE_PATH)
if _ORACLE_SPEC is None or _ORACLE_SPEC.loader is None:
    raise RuntimeError("could not load independent F10 projection oracle")
F10_ORACLE = importlib.util.module_from_spec(_ORACLE_SPEC)
_ORACLE_SPEC.loader.exec_module(F10_ORACLE)

PROTOCOL_VERSION = "2025-11-25"
EXPECTED_SERVER_VERSION = "0.10.0"
EXPECTED_TOOL_COUNT = 29
POSITION_TOLERANCE_MM = 1.0e-6
ANGLE_TOLERANCE_RAD = 1.0e-7
LENGTH_TOLERANCE_MM = 1.0e-6
OPERATION_TIMEOUT_SECONDS = 180.0
POLL_INTERVAL_SECONDS = 0.2
MCP_RESPONSE_LINE_LIMIT_BYTES = 16 * 1024 * 1024
PRIVATE_KEY_PARTS = (
    "token", "secret", "authorization", "ownership_id", "credential",
    "api_key", "access_key", "private_key", "password",
)
TERMINAL_STATES = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
EXPECTED_TOOLS = {
    "nx_ensure_ready", "nx_get_capabilities", "nx_bridge_status", "nx_bridge_ping",
    "nx_takeover", "nx_return", "nx_get_session", "nx_inspect_part", "nx_query_faces",
    "nx_inspect_sketch", "nx_get_operation", "nx_cancel_operation", "nx_prepare_datum_plane",
    "nx_prepare_sketch", "nx_prepare_sketch_corner", "nx_edit_sketch_corner",
    "nx_prepare_sketch_offset", "nx_edit_sketch_offset", "nx_prepare_sketch_pattern",
    "nx_edit_sketch_pattern", "nx_prepare_sketch_projection", "nx_edit_sketch_projection",
    "nx_prepare_extrude", "nx_prepare_thread", "nx_prepare_helical_groove",
    "nx_prepare_operation", "nx_execute_plan", "nx_verify_part", "nx_save_copy",
}
PROJECTION_REQUIRED = {
    "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
    "source_curve_ids", "direction_xyz", "associative",
}
FIXTURE_SCHEMA = "f10-native-mcp-fixture-v1"
WRITE_TOOLS = {"nx_edit_sketch_projection", "nx_save_copy"}
AMBIGUOUS_WRITE_CODES = {
    "OPERATION_OUTCOME_UNKNOWN", "CLIENT_OUTCOME_UNKNOWN", "UNKNOWN_OPERATION_OUTCOME",
    "VERIFY_OUTCOME_UNKNOWN", "VERIFY_RESULT_MISSING", "UNKNOWN",
}


def _redact(value: object) -> object:
    """Redact credential-like keys, including nested JSON encoded as text."""
    if isinstance(value, dict):
        cleaned = {}
        for key, child in value.items():
            lowered = str(key).casefold()
            cleaned[key] = ("[REDACTED]" if any(part in lowered for part in PRIVATE_KEY_PARTS)
                            else _redact(child))
        return cleaned
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


def _in_flight_count(value: object) -> int:
    if isinstance(value, str) and value.isascii() and value.isdecimal() and (value == "0" or not value.startswith("0")):
        value = int(value)
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise AssertionError("bridge status omitted a nonnegative in_flight count")
    return value


def _read_effect_after(record: dict, label: str, expected_before: bool) -> bool:
    before = record.get("is_modified_before_read")
    after = record.get("is_modified_after_read")
    changed = record.get("dirty_changed_by_read")
    if not all(isinstance(value, bool) for value in (before, after, changed)):
        raise AssertionError(f"{label} omitted boolean read-effect evidence")
    if before is not expected_before or changed is not (before != after) or (before and not after):
        raise AssertionError(f"{label} has inconsistent dirty/read-effect evidence")
    return after


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


def _operation_identifier(record: dict) -> str | None:
    operation = record.get("operation") if isinstance(record.get("operation"), dict) else {}
    for candidate in (record.get("operation_id"), operation.get("operation_id"), operation.get("id")):
        if isinstance(candidate, str) and candidate:
            return candidate
    return None


def _wait_for_operation(initial: dict, operation_id: str, label: str, query,
                        *, force_query: bool = False,
                        timeout_seconds: float = OPERATION_TIMEOUT_SECONDS,
                        poll_interval_seconds: float = POLL_INTERVAL_SECONDS,
                        sleep_fn=time.sleep) -> tuple[dict, dict | None]:
    """Poll a single operation ID to a terminal state, optionally confirming a terminal reply."""
    current = initial
    last_query = None
    deadline = time.monotonic() + timeout_seconds
    must_query = force_query
    while must_query or _status_state(current) not in TERMINAL_STATES:
        if time.monotonic() >= deadline:
            raise TimeoutError(f"{label} did not reach a terminal state")
        if not must_query and poll_interval_seconds > 0:
            sleep_fn(poll_interval_seconds)
        must_query = False
        current, last_query = query(operation_id)
        if not isinstance(current, dict) or _operation_identifier(current) != operation_id:
            raise AssertionError(f"{label} poll returned a different or missing operation_id")
    return current, last_query


def _contains_plan(value: object) -> bool:
    if isinstance(value, dict):
        if isinstance(value.get("plan"), dict):
            return True
        return any(_contains_plan(child) for child in value.values())
    if isinstance(value, list):
        return any(_contains_plan(child) for child in value)
    return False


def _error_text(value: object) -> str:
    if isinstance(value, dict):
        return " ".join(_error_text(child) for child in value.values())
    if isinstance(value, (list, tuple)):
        return " ".join(_error_text(child) for child in value)
    if isinstance(value, str):
        try:
            decoded = json.loads(value)
        except (TypeError, ValueError):
            return value
        if isinstance(decoded, (dict, list)):
            return _error_text(decoded)
    return ""


def _validate_oblique_prepare_rejection(initial: dict, terminal: dict, operation_id: str,
                                        label: str, terminal_entry: dict | None = None) -> str:
    """Require a same-ID FAILED operation and a direction-specific error with no plan."""
    if _operation_identifier(terminal) != operation_id:
        raise AssertionError(f"{label} terminal result has a different operation_id")
    if _status_state(terminal) != "FAILED":
        raise AssertionError(f"{label} operation ended in {_status_state(terminal) or 'UNKNOWN'}, not FAILED")
    if _contains_plan(initial) or _contains_plan(terminal):
        raise AssertionError(f"{label} oblique direction rejection unexpectedly returned a plan")
    error_code = _error_code(terminal)
    if not error_code and terminal_entry is not None:
        error_code = _error_code(terminal_entry.get("mcp_response"))
    error_text = (_error_text(terminal) + " " +
                  (_error_text(terminal_entry.get("mcp_response")) if terminal_entry else "")).casefold()
    if "direction" not in (error_code or "").casefold() + error_text:
        raise AssertionError(f"{label} rejection omitted a direction-specific error code or message")
    return error_code or "DIRECTION_ERROR"


def _canonical_json(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, allow_nan=False,
                      separators=(",", ":"))


def _canonical_path(path: str | Path) -> str:
    return Path(path).resolve(strict=False).as_posix().casefold()


def _safe_transport_error_description(exc: Exception, client: McpStdioClient | None) -> str:
    """Describe transport failures without persisting untrusted exception text."""
    if isinstance(exc, McpClientError):
        message = str(exc)
        if "oversized JSON-RPC line" in message:
            limit = client.max_line_bytes if client is not None else MCP_RESPONSE_LINE_LIMIT_BYTES
            return (f"stdio JSON-RPC response line exceeded max_line_bytes={limit}; "
                    f"reader observed at least {limit + 1} bytes")
        if "unterminated JSON-RPC line" in message:
            limit = client.max_line_bytes if client is not None else MCP_RESPONSE_LINE_LIMIT_BYTES
            return f"stdio JSON-RPC response line was unterminated within max_line_bytes={limit}"
        for fragment, description in (
            ("timed out", "stdio request timed out"),
            ("closed stdout", "stdio child closed stdout before responding"),
            ("stdin is unavailable", "stdio child has no writable stdin"),
            ("stopped accepting requests", "stdio child stopped accepting requests"),
            ("invalid JSON-RPC", "stdio child returned invalid JSON-RPC"),
            ("response id did not match", "stdio response ID did not match the request"),
        ):
            if fragment in message:
                return description
        return "MCP stdio protocol/client error; exception details omitted"
    return f"{type(exc).__name__} during MCP stdio request; exception details omitted"


def _finite_vector(value: object, label: str) -> list[float]:
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        raise AssertionError(f"{label} must contain three finite numbers")
    result = []
    for component in value:
        if isinstance(component, bool):
            raise AssertionError(f"{label} must contain three finite numbers")
        try:
            number = float(component)
        except (TypeError, ValueError, OverflowError) as exc:
            raise AssertionError(f"{label} must contain three finite numbers") from exc
        if not math.isfinite(number):
            raise AssertionError(f"{label} must contain three finite numbers")
        result.append(number)
    return result


def normalize_vector(value: object, label: str = "vector") -> list[float]:
    values = _finite_vector(value, label)
    scale = max(abs(component) for component in values)
    if scale == 0.0:
        raise AssertionError(f"{label} must be nonzero")
    scaled = [component / scale for component in values]
    length = math.sqrt(math.fsum(component * component for component in scaled))
    return [component / length for component in scaled]


def _vector_close(actual: object, expected: object, tolerance: float = ANGLE_TOLERANCE_RAD) -> bool:
    try:
        left, right = _finite_vector(actual, "actual vector"), _finite_vector(expected, "expected vector")
    except AssertionError:
        return False
    return math.sqrt(math.fsum((left[i] - right[i]) ** 2 for i in range(3))) <= tolerance


def _dot(left: list[float], right: list[float]) -> float:
    return math.fsum(left[i] * right[i] for i in range(3))


def _add(left: list[float], right: list[float]) -> list[float]:
    return [left[i] + right[i] for i in range(3)]


def _scale(value: list[float], factor: float) -> list[float]:
    return [component * factor for component in value]


def _cross(left: list[float], right: list[float]) -> list[float]:
    return [left[1] * right[2] - left[2] * right[1],
            left[2] * right[0] - left[0] * right[2],
            left[0] * right[1] - left[1] * right[0]]


def _distance3(left: object, right: object) -> float:
    a = _finite_vector(left, "left point")
    b = _finite_vector(right, "right point")
    return math.sqrt(math.fsum((a[index] - b[index]) ** 2 for index in range(3)))


def _angle_between_unoriented(left: object, right: object) -> float:
    a = normalize_vector(left, "left normal")
    b = normalize_vector(right, "right normal")
    cosine = max(-1.0, min(1.0, abs(_dot(a, b))))
    return math.acos(cosine)


def _json_no_duplicate_keys(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"fixture JSON contains duplicate key {key!r}")
        result[key] = value
    return result


def _reject_json_constant(value: str) -> object:
    raise ValueError(f"fixture JSON contains unsupported non-finite number {value}")


def _number(value: object, label: str) -> float:
    if isinstance(value, bool):
        raise ValueError(f"{label} must be a finite number")
    try:
        result = float(value)
    except (TypeError, ValueError, OverflowError) as exc:
        raise ValueError(f"{label} must be a finite number") from exc
    if not math.isfinite(result):
        raise ValueError(f"{label} must be a finite number")
    return result


def _point_on_source_arc(primitive: dict, angle: float) -> list[float]:
    center = _finite_vector(primitive.get("center_mm"), "arc center_mm")
    axis_x = normalize_vector(primitive.get("axis_x"), "arc axis_x")
    axis_y = normalize_vector(primitive.get("axis_y"), "arc axis_y")
    radius = _number(primitive.get("radius_mm"), "arc radius_mm")
    return [center[i] + radius * (axis_x[i] * math.cos(angle) + axis_y[i] * math.sin(angle))
            for i in range(3)]


def _validate_frame(frame: object, label: str) -> dict:
    if not isinstance(frame, dict):
        raise ValueError(f"{label} must be an object")
    checked = {
        "origin_mm": _finite_vector(frame.get("origin_mm"), label + " origin_mm"),
        "normal": normalize_vector(frame.get("normal"), label + " normal"),
        "x_axis": normalize_vector(frame.get("x_axis"), label + " x_axis"),
        "y_axis": normalize_vector(frame.get("y_axis"), label + " y_axis"),
    }
    if (abs(_dot(checked["normal"], checked["x_axis"])) > ANGLE_TOLERANCE_RAD
            or abs(_dot(checked["normal"], checked["y_axis"])) > ANGLE_TOLERANCE_RAD
            or abs(_dot(checked["x_axis"], checked["y_axis"])) > ANGLE_TOLERANCE_RAD):
        raise ValueError(f"{label} axes must be mutually orthogonal")
    expected_y = normalize_vector(_cross(checked["normal"], checked["x_axis"]), label + " cross-product Y")
    if _distance3(expected_y, checked["y_axis"]) > ANGLE_TOLERANCE_RAD:
        raise ValueError(f"{label} y_axis must equal normal cross x_axis")
    return checked


def _validate_source_record(record: object, label: str) -> dict:
    if not isinstance(record, dict):
        raise ValueError(f"{label} must be an object")
    source_id = record.get("source_curve_id")
    journal_id = record.get("journal_id")
    tag = record.get("tag")
    source_type = record.get("source_type")
    if not isinstance(source_id, str) or not source_id.strip():
        raise ValueError(f"{label} omitted source_curve_id")
    if not isinstance(journal_id, str) or not journal_id.strip() or journal_id != source_id:
        raise ValueError(f"{label} journal_id must equal its initial source_curve_id")
    if not isinstance(tag, str) or not tag.strip():
        raise ValueError(f"{label} omitted stable source Tag")
    if not isinstance(source_type, str) or not source_type.strip():
        raise ValueError(f"{label} omitted source_type")
    source_class = source_type.rsplit(".", 1)[-1].casefold()
    edge_type = record.get("edge_type")
    primitive = record.get("primitive")
    if source_class not in {"line", "arc", "edge"}:
        raise ValueError(f"{label} has unsupported source_type {source_type!r}")
    if source_class == "edge":
        if edge_type not in {"Linear", "Circular"}:
            raise ValueError(f"{label} Edge must identify Linear or Circular edge_type")
        if primitive is None:
            reason = record.get("primitive_unavailable_reason")
            if not isinstance(reason, str) or not reason.strip():
                raise ValueError(f"{label} Edge without a geometry primitive must record why it is unavailable")
            return {**record, "source_class": source_class, "numeric_primitive": None,
                    "numeric_oracle_unavailable": True,
                    "numeric_oracle_limitation": reason}
    elif not isinstance(primitive, dict):
        raise ValueError(f"{label} native Curve must include a geometry primitive")

    if not isinstance(primitive, dict):
        raise ValueError(f"{label} geometry primitive must be an object")
    kind = str(primitive.get("kind", "")).upper()
    expected_kind = "LINE" if source_class == "line" or source_class == "edge" and edge_type == "Linear" else "ARC"
    if kind != expected_kind:
        raise ValueError(f"{label} source type {source_type!r} cannot use primitive kind {kind!r}")
    if kind == "LINE":
        start = _finite_vector(primitive.get("start_mm"), label + " line start_mm")
        end = _finite_vector(primitive.get("end_mm"), label + " line end_mm")
        length = _distance3(start, end)
        if not math.isfinite(length) or length <= POSITION_TOLERANCE_MM:
            raise ValueError(f"{label} line must have finite length greater than the digital tolerance")
        checked_primitive = {"kind": "LINE", "start_mm": start, "end_mm": end,
                             "length_mm": length}
    else:
        center = _finite_vector(primitive.get("center_mm"), label + " arc center_mm")
        axis_x = normalize_vector(primitive.get("axis_x"), label + " arc axis_x")
        axis_y = normalize_vector(primitive.get("axis_y"), label + " arc axis_y")
        if abs(_dot(axis_x, axis_y)) > ANGLE_TOLERANCE_RAD:
            raise ValueError(f"{label} arc plane axes must be orthogonal")
        normal = normalize_vector(_cross(axis_x, axis_y), label + " arc plane normal")
        supplied_normal = primitive.get("normal")
        if supplied_normal is not None and _angle_between_unoriented(supplied_normal, normal) > ANGLE_TOLERANCE_RAD:
            raise ValueError(f"{label} arc normal disagrees with its plane axes")
        radius = _number(primitive.get("radius_mm"), label + " arc radius_mm")
        if radius <= POSITION_TOLERANCE_MM:
            raise ValueError(f"{label} arc radius must exceed the digital tolerance")
        start_angle = _number(primitive.get("start_angle_rad"), label + " arc start_angle_rad")
        end_angle = _number(primitive.get("end_angle_rad"), label + " arc end_angle_rad")
        sweep = _number(primitive.get("sweep_rad", end_angle - start_angle), label + " arc sweep_rad")
        if abs((end_angle - start_angle) - sweep) > ANGLE_TOLERANCE_RAD:
            raise ValueError(f"{label} arc sweep_rad must equal end_angle_rad - start_angle_rad")
        if abs(sweep) <= ANGLE_TOLERANCE_RAD or abs(sweep) > 2.0 * math.pi + ANGLE_TOLERANCE_RAD:
            raise ValueError(f"{label} arc sweep must be nonzero and no greater than one revolution")
        start = _finite_vector(primitive.get("start_mm"), label + " arc start_mm")
        end = _finite_vector(primitive.get("end_mm"), label + " arc end_mm")
        calculated_start = _point_on_source_arc({**primitive, "center_mm": center, "axis_x": axis_x,
                                                 "axis_y": axis_y, "radius_mm": radius}, start_angle)
        calculated_end = _point_on_source_arc({**primitive, "center_mm": center, "axis_x": axis_x,
                                               "axis_y": axis_y, "radius_mm": radius}, end_angle)
        if (_distance3(start, calculated_start) > POSITION_TOLERANCE_MM
                or _distance3(end, calculated_end) > POSITION_TOLERANCE_MM):
            raise ValueError(f"{label} arc endpoints disagree with its center, axes, radius, and parameter limits")
        full_circle = primitive.get("full_circle") is True
        if full_circle != (abs(abs(sweep) - 2.0 * math.pi) <= ANGLE_TOLERANCE_RAD):
            raise ValueError(f"{label} full_circle flag disagrees with its arc sweep")
        if source_class == "edge" and edge_type == "Circular" and primitive.get("full_circle") is not None:
            if not isinstance(primitive.get("full_circle"), bool):
                raise ValueError(f"{label} circular Edge full_circle must be a boolean")
        checked_primitive = {
            "kind": "ARC", "center_mm": center, "radius_mm": radius,
            "axis_x": axis_x, "axis_y": axis_y, "normal": normal,
            "start_mm": start, "end_mm": end,
            "start_angle_rad": start_angle, "end_angle_rad": end_angle,
            "sweep_rad": sweep, "full_circle": full_circle,
            "midpoint_mm": _point_on_source_arc(
                {**primitive, "center_mm": center, "axis_x": axis_x, "axis_y": axis_y,
                 "radius_mm": radius}, start_angle + sweep / 2.0),
            "length_mm": radius * abs(sweep),
        }
    return {**record, "source_class": source_class, "numeric_primitive": checked_primitive,
            "numeric_oracle_unavailable": False, "numeric_oracle_limitation": None}


def validate_fixture_document(document: object) -> dict:
    """Validate the native journal fixture and fail closed on ambiguous source IDs."""
    if not isinstance(document, dict) or document.get("schema") != FIXTURE_SCHEMA:
        raise ValueError(f"fixture schema must be {FIXTURE_SCHEMA!r}")
    part_path = document.get("part_path")
    units = str(document.get("units", "")).casefold()
    if not isinstance(part_path, str) or not Path(part_path).is_absolute():
        raise ValueError("fixture part_path must be an absolute path")
    if units not in {"millimeter", "millimeters", "mm"}:
        raise ValueError("fixture units must be millimeters")
    target_ids = document.get("target_sketch_ids")
    target_frames = document.get("target_frames")
    if not isinstance(target_ids, dict) or set(target_ids) != {"associative_true", "associative_false"}:
        raise ValueError("fixture target_sketch_ids must identify associative_true and associative_false")
    true_id, false_id = target_ids["associative_true"], target_ids["associative_false"]
    if (not isinstance(true_id, str) or not true_id.strip() or not isinstance(false_id, str)
            or not false_id.strip() or true_id == false_id):
        raise ValueError("fixture target Sketch IDs must be distinct nonempty strings")
    if not isinstance(target_frames, dict) or true_id not in target_frames or false_id not in target_frames:
        raise ValueError("fixture target_frames must contain a frame for each target Sketch ID")
    normalized_frames = {
        "associative_true": _validate_frame(target_frames[true_id], "associative_true target frame"),
        "associative_false": _validate_frame(target_frames[false_id], "associative_false target frame"),
    }
    source_sketch_id = document.get("source_sketch_id")
    source_sketch_frame = document.get("source_sketch_frame")
    if not isinstance(source_sketch_id, str) or not source_sketch_id.strip():
        raise ValueError("fixture source_sketch_id is required for post-edit geometry remapping")
    if source_sketch_id in {true_id, false_id}:
        raise ValueError("source Sketch must differ from both target Sketches")
    normalized_source_frame = _validate_frame(source_sketch_frame, "source Sketch frame")
    source_ids = document.get("source_curve_ids")
    source_records = document.get("source_records")
    if not isinstance(source_ids, list) or not 1 <= len(source_ids) <= 128:
        raise ValueError("fixture source_curve_ids must contain one through 128 IDs")
    if (any(not isinstance(item, str) or not item.strip() for item in source_ids)
            or len(source_ids) != len(set(source_ids))):
        raise ValueError("fixture source_curve_ids must be unique nonempty strings")
    if not isinstance(source_records, list) or len(source_records) != len(source_ids):
        raise ValueError("fixture source_records must map one-to-one to source_curve_ids")
    checked_records = [_validate_source_record(record, f"source_records[{index}]")
                       for index, record in enumerate(source_records)]
    record_ids = [item["source_curve_id"] for item in checked_records]
    record_tags = [item["tag"] for item in checked_records]
    if record_ids != source_ids:
        raise ValueError("fixture source_records must match source_curve_ids in exact order and without omissions")
    if len(record_tags) != len(set(record_tags)):
        raise ValueError("fixture source records contain duplicate stable Tags")
    curves = [item for item in checked_records if item["source_class"] in {"line", "arc"}]
    if not curves:
        raise ValueError("fixture needs at least one directly inspectable Line/Arc primitive")
    coverage = {"LINE": 0, "PARTIAL_ARC": 0, "FULL_CIRCLE": 0}
    for item in checked_records:
        primitive = item["numeric_primitive"]
        if primitive is None:
            continue
        if primitive["kind"] == "LINE":
            coverage["LINE"] += 1
        elif primitive["full_circle"]:
            coverage["FULL_CIRCLE"] += 1
        else:
            coverage["PARTIAL_ARC"] += 1
    if not any(coverage.values()):
        raise ValueError("fixture must provide at least one line/arc/circle numeric primitive")
    for label, frame in normalized_frames.items():
        for item in checked_records:
            primitive = item["numeric_primitive"]
            if primitive is None:
                continue
            if primitive["kind"] == "LINE":
                start, end = F10_ORACLE.project_line_along_direction(
                    primitive["start_mm"], primitive["end_mm"], frame["origin_mm"],
                    frame["normal"], frame["normal"],
                )
                if _distance3(start, end) <= POSITION_TOLERANCE_MM:
                    raise ValueError(f"{label} collapses source {item['source_curve_id']} to a point")
            else:
                if not F10_ORACLE.direction_is_normal(primitive["normal"], frame["normal"], ANGLE_TOLERANCE_RAD):
                    raise ValueError(f"source Arc {item['source_curve_id']} plane must be parallel to both target Sketch planes")
    return {
        "schema": FIXTURE_SCHEMA,
        "part_path": part_path,
        "units": "Millimeters",
        "target_sketch_ids": {"associative_true": true_id, "associative_false": false_id},
        "target_frames": normalized_frames,
        "source_sketch_id": source_sketch_id,
        "source_sketch_frame": normalized_source_frame,
        "source_curve_ids": list(source_ids),
        "source_records": checked_records,
        "numeric_coverage": coverage,
        "unverified_edge_source_ids": [item["source_curve_id"] for item in checked_records
                                        if item["numeric_oracle_unavailable"]],
    }


def load_fixture_json(path: str | Path) -> dict:
    with Path(path).open("r", encoding="utf-8") as stream:
        document = json.load(stream, object_pairs_hook=_json_no_duplicate_keys,
                             parse_constant=_reject_json_constant)
    return validate_fixture_document(document)


def expected_projection_record(source: dict, frame: dict, direction: list[float]) -> dict:
    primitive = source.get("numeric_primitive")
    if not isinstance(primitive, dict):
        raise ValueError(f"source {source.get('source_curve_id')} has no independent numeric primitive")
    common = {"source_curve_id": source["source_curve_id"], "kind": primitive["kind"]}
    if primitive["kind"] == "LINE":
        expected_start, expected_end = F10_ORACLE.project_line_along_direction(
            primitive["start_mm"], primitive["end_mm"], frame["origin_mm"], frame["normal"], direction,
        )
        return {**common, "start_mm": expected_start, "end_mm": expected_end,
                "length_mm": _distance3(expected_start, expected_end)}
    center = F10_ORACLE.project_point_along_direction(
        primitive["center_mm"], frame["origin_mm"], frame["normal"], direction,
    )
    start = F10_ORACLE.project_point_along_direction(
        primitive["start_mm"], frame["origin_mm"], frame["normal"], direction,
    )
    end = F10_ORACLE.project_point_along_direction(
        primitive["end_mm"], frame["origin_mm"], frame["normal"], direction,
    )
    midpoint = F10_ORACLE.project_point_along_direction(
        primitive["midpoint_mm"], frame["origin_mm"], frame["normal"], direction,
    )
    return {
        **common, "center_mm": center, "radius_mm": primitive["radius_mm"],
        "start_mm": start, "end_mm": end, "midpoint_mm": midpoint,
        "normal": frame["normal"], "sweep_rad": primitive["sweep_rad"],
        "full_circle": primitive["full_circle"], "length_mm": primitive["length_mm"],
    }


def _native_arc_midpoint(actual: dict) -> list[float]:
    center = _finite_vector(actual.get("center"), "actual arc center")
    start = _finite_vector(actual.get("start"), "actual arc start")
    normal = normalize_vector(actual.get("normal"), "actual arc normal")
    radius = _number(actual.get("radius_mm"), "actual arc radius")
    start_angle = _number(actual.get("start_angle_rad"), "actual arc start angle")
    end_angle = _number(actual.get("end_angle_rad"), "actual arc end angle")
    sweep = end_angle - start_angle
    radial = normalize_vector([start[i] - center[i] for i in range(3)], "actual arc radial")
    tangent = _cross(normal, radial)
    half = sweep / 2.0
    return [center[i] + radius * (math.cos(half) * radial[i] + math.sin(half) * tangent[i])
            for i in range(3)]


def projection_geometry_match(actual: dict, expected: dict,
                              position_tolerance_mm: float = POSITION_TOLERANCE_MM,
                              length_tolerance_mm: float = LENGTH_TOLERANCE_MM,
                              angle_tolerance_rad: float = ANGLE_TOLERANCE_RAD) -> dict | None:
    """Independent direct-readback match for a projected Line, partial Arc, or full Circle."""
    if not isinstance(actual, dict) or not isinstance(expected, dict):
        return None
    kind = str(actual.get("kind", "")).upper()
    if kind != expected.get("kind"):
        return None
    try:
        if kind == "LINE":
            actual_start = _finite_vector(actual.get("start"), "actual Line start")
            actual_end = _finite_vector(actual.get("end"), "actual Line end")
            expected_start = _finite_vector(expected.get("start_mm"), "expected Line start")
            expected_end = _finite_vector(expected.get("end_mm"), "expected Line end")
            direct = (_distance3(actual_start, expected_start), _distance3(actual_end, expected_end))
            reverse = (_distance3(actual_start, expected_end), _distance3(actual_end, expected_start))
            residuals = reverse if max(reverse) < max(direct) else direct
            actual_length = _distance3(actual_start, actual_end)
            expected_length = _number(expected.get("length_mm"), "expected Line length")
            if (max(residuals) > position_tolerance_mm
                    or abs(actual_length - expected_length) > length_tolerance_mm):
                return None
            return {"matches": True, "kind": kind, "endpoint_residuals_mm": list(residuals),
                    "length_residual_mm": abs(actual_length - expected_length),
                    "endpoint_order_reversed": residuals is reverse}
        if kind != "ARC":
            return None
        center = _finite_vector(actual.get("center"), "actual Arc center")
        expected_center = _finite_vector(expected.get("center_mm"), "expected Arc center")
        actual_start = _finite_vector(actual.get("start"), "actual Arc start")
        actual_end = _finite_vector(actual.get("end"), "actual Arc end")
        expected_start = _finite_vector(expected.get("start_mm"), "expected Arc start")
        expected_end = _finite_vector(expected.get("end_mm"), "expected Arc end")
        radius = _number(actual.get("radius_mm"), "actual Arc radius")
        expected_radius = _number(expected.get("radius_mm"), "expected Arc radius")
        start_angle = _number(actual.get("start_angle_rad"), "actual Arc start angle")
        end_angle = _number(actual.get("end_angle_rad"), "actual Arc end angle")
        actual_sweep = end_angle - start_angle
        expected_sweep = _number(expected.get("sweep_rad"), "expected Arc sweep")
        if (abs(_distance3(center, expected_center)) > position_tolerance_mm
                or abs(radius - expected_radius) > position_tolerance_mm
                or abs(abs(actual_sweep) - abs(expected_sweep)) > angle_tolerance_rad
                or not F10_ORACLE.direction_is_normal(actual.get("normal"), expected.get("normal"), angle_tolerance_rad)):
            return None
        actual_length = radius * abs(actual_sweep)
        expected_length = _number(expected.get("length_mm"), "expected Arc length")
        if abs(actual_length - expected_length) > length_tolerance_mm:
            return None
        full_circle = expected.get("full_circle") is True
        if full_circle:
            if abs(abs(actual_sweep) - 2.0 * math.pi) > angle_tolerance_rad:
                return None
            if _distance3(actual_start, actual_end) > position_tolerance_mm:
                return None
            endpoint_residuals = [abs(_distance3(actual_start, center) - radius)]
            if endpoint_residuals[0] > position_tolerance_mm:
                return None
            midpoint_residual = 0.0
        else:
            direct = (_distance3(actual_start, expected_start), _distance3(actual_end, expected_end))
            reverse = (_distance3(actual_start, expected_end), _distance3(actual_end, expected_start))
            endpoint_residuals = list(reverse if max(reverse) < max(direct) else direct)
            if max(endpoint_residuals) > position_tolerance_mm:
                return None
            actual_midpoint = _native_arc_midpoint(actual)
            expected_midpoint = _finite_vector(expected.get("midpoint_mm"), "expected Arc midpoint")
            midpoint_residual = _distance3(actual_midpoint, expected_midpoint)
            if midpoint_residual > position_tolerance_mm:
                return None
        return {"matches": True, "kind": kind,
                "center_residual_mm": _distance3(center, expected_center),
                "radius_residual_mm": abs(radius - expected_radius),
                "endpoint_residuals_mm": endpoint_residuals,
                "sweep_residual_rad": abs(abs(actual_sweep) - abs(expected_sweep)),
                "length_residual_mm": abs(actual_length - expected_length),
                "midpoint_residual_mm": midpoint_residual,
                "full_circle": full_circle}
    except (KeyError, TypeError, ValueError, OverflowError):
        return None


def match_projection_records(actual_records: list, expected_records: list,
                             unverified_source_count: int = 0, label: str = "projection") -> dict:
    """Require a one-to-one numeric map and account explicitly for unmodeled Edge sources."""
    if not isinstance(actual_records, list) or not isinstance(expected_records, list):
        raise AssertionError(f"{label} geometry inventories must be arrays")
    actual_ids = []
    for index, record in enumerate(actual_records):
        journal_id = record.get("journal_id") if isinstance(record, dict) else None
        if not isinstance(journal_id, str) or not journal_id:
            raise AssertionError(f"{label} output[{index}] omitted journal_id")
        actual_ids.append(journal_id)
    if len(actual_ids) != len(set(actual_ids)):
        raise AssertionError(f"{label} contains duplicate output journal IDs")
    if len(actual_records) != len(expected_records) + unverified_source_count:
        raise AssertionError(
            f"{label} expected {len(expected_records)} numerically modeled outputs plus "
            f"{unverified_source_count} unverified Edge outputs; got {len(actual_records)}"
        )
    matches = []
    used = set()
    for expected in expected_records:
        candidates = []
        for index, actual in enumerate(actual_records):
            evidence = projection_geometry_match(actual, expected)
            if evidence is not None:
                candidates.append((index, evidence))
        if len(candidates) != 1:
            raise AssertionError(
                f"{label} source {expected.get('source_curve_id')} maps to {len(candidates)} outputs; expected exactly one"
            )
        index, evidence = candidates[0]
        if index in used:
            raise AssertionError(f"{label} maps multiple sources to one output curve")
        used.add(index)
        matches.append({"source_curve_id": expected["source_curve_id"],
                        "output_journal_id": actual_ids[index], "residuals": evidence})
    unverified = [actual_ids[index] for index in range(len(actual_ids)) if index not in used]
    if len(unverified) != unverified_source_count:
        raise AssertionError(f"{label} has unmatched curves outside the explicitly unverified Edge allowance")
    return {"expected_to_actual": matches,
            "unverified_edge_output_ids": unverified,
            "numeric_sources_verified": len(matches),
            "unverified_edge_source_count": unverified_source_count,
            "one_to_one": True}


def _expected_from_native_record(record: dict) -> dict:
    kind = str(record.get("kind", "")).upper()
    if kind == "LINE":
        start = _finite_vector(record.get("start"), "existing Line start")
        end = _finite_vector(record.get("end"), "existing Line end")
        return {"kind": "LINE", "start_mm": start, "end_mm": end,
                "length_mm": _distance3(start, end)}
    if kind == "ARC":
        center = _finite_vector(record.get("center"), "existing Arc center")
        start = _finite_vector(record.get("start"), "existing Arc start")
        end = _finite_vector(record.get("end"), "existing Arc end")
        radius = _number(record.get("radius_mm"), "existing Arc radius")
        start_angle = _number(record.get("start_angle_rad"), "existing Arc start angle")
        end_angle = _number(record.get("end_angle_rad"), "existing Arc end angle")
        sweep = end_angle - start_angle
        midpoint = _native_arc_midpoint(record)
        return {"kind": "ARC", "center_mm": center, "start_mm": start, "end_mm": end,
                "radius_mm": radius, "normal": normalize_vector(record.get("normal"), "existing Arc normal"),
                "sweep_rad": sweep, "full_circle": abs(abs(sweep) - 2.0 * math.pi) <= ANGLE_TOLERANCE_RAD,
                "midpoint_mm": midpoint, "length_mm": radius * abs(sweep)}
    raise AssertionError(f"unsupported pre-existing sketch geometry kind {kind!r}")


def preserved_geometry_mapping(before_records: list, after_records: list, label: str) -> dict:
    """Map old Sketch geometry to post-edit IDs by direct geometry, never by stale ID alone."""
    if not isinstance(before_records, list) or not isinstance(after_records, list):
        raise AssertionError(f"{label} sketch geometry must be arrays")
    before_ids = [item.get("journal_id") if isinstance(item, dict) else None for item in before_records]
    after_ids = [item.get("journal_id") if isinstance(item, dict) else None for item in after_records]
    if (any(not isinstance(item, str) or not item for item in before_ids + after_ids)
            or len(before_ids) != len(set(before_ids)) or len(after_ids) != len(set(after_ids))):
        raise AssertionError(f"{label} contains missing or duplicate geometry journal IDs")
    used = set()
    mapping = []
    for old in before_records:
        expected = _expected_from_native_record(old)
        candidates = [(index, actual) for index, actual in enumerate(after_records)
                      if projection_geometry_match(actual, expected) is not None]
        if len(candidates) != 1:
            raise AssertionError(f"{label} old geometry {old['journal_id']} maps to {len(candidates)} current curves")
        index, actual = candidates[0]
        if index in used:
            raise AssertionError(f"{label} old Sketch geometry mapping is not one-to-one")
        used.add(index)
        mapping.append({"before_journal_id": old["journal_id"],
                        "after_journal_id": actual["journal_id"],
                        "geometry_preserved_by_numeric_match": True})
    new_records = [actual for index, actual in enumerate(after_records) if index not in used]
    return {"mapping": mapping, "new_geometry": new_records,
            "preserved_count": len(mapping), "new_count": len(new_records)}


def _native_sketch_frame(sketch: dict) -> dict:
    return {
        "origin_mm": _finite_vector(sketch.get("origin"), "inspected Sketch origin"),
        "normal": normalize_vector(sketch.get("normal"), "inspected Sketch normal"),
        "x_axis": normalize_vector(sketch.get("x_axis"), "inspected Sketch x_axis"),
        "y_axis": normalize_vector(sketch.get("y_axis"), "inspected Sketch y_axis"),
    }


def _frame_matches(actual_sketch: dict, expected_frame: dict) -> bool:
    try:
        actual = _native_sketch_frame(actual_sketch)
    except (AssertionError, TypeError, ValueError):
        return False
    return (_distance3(actual["origin_mm"], expected_frame["origin_mm"]) <= POSITION_TOLERANCE_MM
            and _distance3(actual["normal"], expected_frame["normal"]) <= ANGLE_TOLERANCE_RAD
            and _distance3(actual["x_axis"], expected_frame["x_axis"]) <= ANGLE_TOLERANCE_RAD
            and _distance3(actual["y_axis"], expected_frame["y_axis"]) <= ANGLE_TOLERANCE_RAD)


def _expected_source_record(source: dict) -> dict | None:
    primitive = source.get("numeric_primitive")
    if not isinstance(primitive, dict):
        return None
    if primitive["kind"] == "LINE":
        return {"source_curve_id": source["source_curve_id"], "kind": "LINE",
                "start_mm": primitive["start_mm"], "end_mm": primitive["end_mm"],
                "length_mm": primitive["length_mm"]}
    return {"source_curve_id": source["source_curve_id"], "kind": "ARC",
            "center_mm": primitive["center_mm"], "radius_mm": primitive["radius_mm"],
            "start_mm": primitive["start_mm"], "end_mm": primitive["end_mm"],
            "normal": primitive["normal"], "sweep_rad": primitive["sweep_rad"],
            "full_circle": primitive["full_circle"], "midpoint_mm": primitive["midpoint_mm"],
            "length_mm": primitive["length_mm"]}


def _match_source_sketch_records(sketch: dict, fixture: dict) -> dict | None:
    actual = sketch.get("geometry")
    if not isinstance(actual, list):
        return None
    source_records = [item for item in fixture["source_records"] if item["source_class"] in {"line", "arc"}]
    if len(actual) != len(source_records):
        return None
    mapping = []
    used = set()
    for source in source_records:
        expected = _expected_source_record(source)
        candidates = []
        for index, record in enumerate(actual):
            match = projection_geometry_match(record, expected) if expected is not None else None
            if match is not None:
                candidates.append((index, record, match))
        if len(candidates) != 1:
            return None
        index, record, evidence = candidates[0]
        if index in used:
            return None
        used.add(index)
        mapping.append({"fixture_source_curve_id": source["source_curve_id"],
                        "fixture_source_tag": source["tag"],
                        "current_journal_id": record["journal_id"],
                        "geometry_match": evidence})
    return {"sketch_id": sketch.get("journal_id"), "mapping": mapping,
            "geometry": actual}


def resolve_source_sketch(part_snapshot: dict, fixture: dict) -> dict:
    sketches = part_snapshot.get("sketches") if isinstance(part_snapshot, dict) else None
    if not isinstance(sketches, list):
        raise AssertionError("nx_inspect_part omitted the Sketch inventory needed to re-resolve source JIDs")
    matches = []
    for sketch in sketches:
        if isinstance(sketch, dict) and _frame_matches(sketch, fixture["source_sketch_frame"]):
            matched = _match_source_sketch_records(sketch, fixture)
            if matched is not None:
                matches.append(matched)
    if len(matches) != 1:
        raise AssertionError(f"source geometry resolves to {len(matches)} Sketches; expected exactly one")
    return matches[0]


def resolve_target_sketch(part_snapshot: dict, expected_frame: dict, label: str) -> dict:
    sketches = part_snapshot.get("sketches") if isinstance(part_snapshot, dict) else None
    if not isinstance(sketches, list):
        raise AssertionError("nx_inspect_part omitted Sketch inventory needed to resolve a target frame")
    matches = [item for item in sketches if isinstance(item, dict) and _frame_matches(item, expected_frame)]
    if len(matches) != 1:
        raise AssertionError(f"{label} frame resolves to {len(matches)} Sketches; expected exactly one")
    return matches[0]


def resolve_source_ids_from_snapshot(part_snapshot: dict, fixture: dict,
                                     edge_ids_by_source_id: dict[str, str] | None = None,
                                     body_ids_by_source_id: dict[str, str] | None = None) -> dict:
    source_sketch = resolve_source_sketch(part_snapshot, fixture)
    curve_by_fixture_id = {item["fixture_source_curve_id"]: item["current_journal_id"]
                           for item in source_sketch["mapping"]}
    current_ids = []
    edge_references = []
    bodies = part_snapshot.get("bodies") if isinstance(part_snapshot.get("bodies"), list) else []
    for source in fixture["source_records"]:
        if source["source_class"] in {"line", "arc"}:
            current_id = curve_by_fixture_id.get(source["source_curve_id"])
            if not current_id:
                raise AssertionError(f"source geometry {source['source_curve_id']} did not resolve by direct Sketch geometry")
            current_ids.append(current_id)
            continue
        source_id = source["source_curve_id"]
        body_id = ((body_ids_by_source_id or {}).get(source_id) or source.get("body_id"))
        body = next((item for item in bodies if isinstance(item, dict) and item.get("journal_id") == body_id), None)
        edge_inventory = body.get("edge_ids") if isinstance(body, dict) else None
        if not isinstance(edge_inventory, list):
            raise AssertionError(f"Edge source {source['source_curve_id']} body inventory cannot be read")
        if edge_ids_by_source_id and source_id in edge_ids_by_source_id:
            current_id = edge_ids_by_source_id[source_id]
            if current_id not in edge_inventory:
                raise AssertionError(f"Edge source {source_id} current live-Tag JID is absent from its Body")
            edge_references.append({"fixture_source_curve_id": source_id, "current_journal_id": current_id,
                                    "resolved_by_operation_tag_and_body_inventory": True})
        elif source["source_curve_id"] in edge_inventory:
            current_id = source["source_curve_id"]
            edge_references.append({"fixture_source_curve_id": source_id, "current_journal_id": current_id,
                                    "resolved_by_fixture_journal_id_in_body_inventory": True})
        else:
            raise AssertionError(
                f"Edge source {source['source_curve_id']} cannot be safely re-resolved from geometry or current Tag evidence"
            )
        current_ids.append(current_id)
    if len(current_ids) != len(set(current_ids)):
        raise AssertionError("re-resolved source IDs are ambiguous or duplicated")
    return {"source_sketch": source_sketch, "source_curve_ids": current_ids,
            "edge_references": edge_references}


def _source_snapshot_geometry_match(row: dict, source: dict) -> dict | None:
    primitive = source.get("numeric_primitive")
    if not isinstance(primitive, dict):
        return None
    expected = _expected_source_record(source)
    if primitive["kind"] == "LINE":
        actual = {"kind": "LINE", "start": row.get("start_world_mm"),
                  "end": row.get("end_world_mm")}
    else:
        actual = {
            "kind": "ARC", "center": row.get("center_world_mm"),
            "radius_mm": row.get("radius_mm"), "start": row.get("start_world_mm"),
            "end": row.get("end_world_mm"), "normal": row.get("plane_normal_world"),
            "start_angle_rad": row.get("start_angle_rad"),
            "end_angle_rad": row.get("end_angle_rad"),
        }
    return projection_geometry_match(actual, expected)


def _check_source_snapshot_after_edit(result: dict, fixture: dict,
                                      live_tags_by_source_id: dict[str, str],
                                      body_ids_by_source_id: dict[str, str]) -> dict:
    verification = result.get("verification") if isinstance(result.get("verification"), dict) else {}
    evidence = verification.get("evidence") if isinstance(verification.get("evidence"), dict) else {}
    nested_evidence = evidence.get("evidence") if isinstance(evidence.get("evidence"), dict) else {}
    measured = nested_evidence.get("measured") if isinstance(nested_evidence.get("measured"), dict) else {}
    snapshot = measured.get("source_snapshot_after")
    rows = snapshot.get("sources") if isinstance(snapshot, dict) else None
    if (not isinstance(rows, list) or isinstance(snapshot.get("count"), bool)
            or not isinstance(snapshot.get("count"), int)
            or len(rows) != len(fixture["source_records"])
            or snapshot.get("count") != len(fixture["source_records"])):
        raise AssertionError("verified projection result omitted complete direct source_snapshot_after records")
    by_tag = {}
    for row in rows:
        tag = row.get("tag") if isinstance(row, dict) else None
        if not isinstance(tag, str) or not tag or tag in by_tag:
            raise AssertionError("source_snapshot_after contains missing or duplicate source Tags")
        by_tag[tag] = row
    if set(by_tag) != set(live_tags_by_source_id.values()):
        raise AssertionError("source_snapshot_after Tags differ from the current-session live Tag map")
    current_ids_by_tag = {}
    current_ids_by_source_id = {}
    current_body_ids_by_source_id = {}
    current_body_ids_by_tag = {}
    evidence_rows = []
    for source in fixture["source_records"]:
        source_id = source["source_curve_id"]
        live_tag = live_tags_by_source_id.get(source_id)
        if not isinstance(live_tag, str) or not live_tag:
            raise AssertionError(f"source {source_id} has no current-session live Tag mapping")
        row = by_tag[live_tag]
        current_id = row.get("journal_id")
        if not isinstance(current_id, str) or not current_id:
            raise AssertionError(f"source live Tag {live_tag} omitted its current JournalIdentifier")
        if str(row.get("object_type", "")).rsplit(".", 1)[-1].casefold() != source["source_class"]:
            raise AssertionError(f"source live Tag {live_tag} changed native object type")
        primitive = source.get("numeric_primitive")
        if primitive is not None:
            match = _source_snapshot_geometry_match(row, source)
            if match is None:
                raise AssertionError(f"source live Tag {live_tag} geometry changed after the projection")
        else:
            match = {"numeric_status": "NOT_AVAILABLE_FROM_FIXTURE",
                     "limitation": source["numeric_oracle_limitation"]}
        current_ids_by_tag[live_tag] = current_id
        current_ids_by_source_id[source_id] = current_id
        if source["source_class"] == "edge":
            body_id = row.get("body_id")
            if not isinstance(body_id, str) or not body_id:
                raise AssertionError(f"Edge source live Tag {live_tag} omitted its current body ID")
            expected_body_id = body_ids_by_source_id.get(source_id)
            if not isinstance(expected_body_id, str) or not expected_body_id:
                raise AssertionError(f"Edge source {source_id} has no first-prepare body identity")
            if body_id != expected_body_id:
                raise AssertionError(f"Edge source live Tag {live_tag} changed its current Body identity")
            current_body_ids_by_tag[live_tag] = body_id
            current_body_ids_by_source_id[source_id] = body_id
        evidence_rows.append({"fixture_source_curve_id": source_id,
                              "fixture_tag_provenance": source["tag"],
                              "live_tag": live_tag,
                              "source_journal_id_after_edit": current_id,
                              "journal_id_changed": current_id != source_id,
                              "geometry_evidence": match})
    return {"source_ids_by_tag": current_ids_by_tag,
            "source_ids_by_source_id": current_ids_by_source_id,
            "body_ids_by_tag": current_body_ids_by_tag,
            "body_ids_by_source_id": current_body_ids_by_source_id,
            "source_records": evidence_rows,
            "fixture_tags_used_as_provenance_only": True,
            "all_sources_re_resolved_by_current_session_live_tag": True,
            "body_ids_checked_against_first_prepare": True}


def _check_projection_schemas(tools: list) -> dict:
    by_name = {item.get("name"): item for item in tools if isinstance(item, dict)}
    checked = []
    for name in ("nx_prepare_sketch_projection", "nx_edit_sketch_projection"):
        tool = by_name.get(name)
        if tool is None:
            raise AssertionError(f"installed MCP omitted {name}")
        schema = tool.get("inputSchema")
        if not isinstance(schema, dict) or set(schema.get("required", [])) != PROJECTION_REQUIRED:
            raise AssertionError(f"{name} must require exactly the explicit F10 projection fields")
        if schema.get("additionalProperties") is not False:
            raise AssertionError(f"{name} must reject hidden or unrecognized request fields")
        properties = schema.get("properties")
        if not isinstance(properties, dict) or set(properties) != PROJECTION_REQUIRED:
            raise AssertionError(f"{name} schema fields differ from the exact F10 request")
        ids_schema = properties["source_curve_ids"]
        if (ids_schema.get("type") != "array" or ids_schema.get("minItems") != 1
                or ids_schema.get("maxItems") != 128 or ids_schema.get("uniqueItems") is not True):
            raise AssertionError(f"{name} must require one through 128 unique explicit source IDs")
        if ids_schema.get("items", {}).get("type") != "string":
            raise AssertionError(f"{name} source_curve_ids items must be strings")
        direction_schema = properties["direction_xyz"]
        if (direction_schema.get("type") != "array" or direction_schema.get("minItems") != 3
                or direction_schema.get("maxItems") != 3
                or direction_schema.get("items", {}).get("type") != "number"):
            raise AssertionError(f"{name} direction_xyz must be an explicit world XYZ vector")
        if properties["sketch_id"].get("type") != "string":
            raise AssertionError(f"{name} sketch_id must be an explicit string")
        if properties["associative"].get("type") != "boolean":
            raise AssertionError(f"{name} associative must be an explicit boolean")
        annotation_key = "readOnlyHint" if name == "nx_prepare_sketch_projection" else "destructiveHint"
        expected_hint = True
        if tool.get("annotations", {}).get(annotation_key) is not expected_hint:
            raise AssertionError(f"{name} is missing its {annotation_key} annotation")
        checked.append(name)
    return {"checked_tools": checked, "required_fields": sorted(PROJECTION_REQUIRED)}


def _check_projection_capability(capabilities: dict) -> dict:
    native = capabilities.get("model_native_geometry_support")
    projection = native.get("sketch_projection") if isinstance(native, dict) else None
    if not isinstance(projection, dict):
        raise AssertionError("nx_get_capabilities omitted model_native_geometry_support.sketch_projection")
    expected_fields = [
        "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
        "source_curve_ids", "direction_xyz", "associative",
    ]
    if projection.get("prepare_operation") != "operation.prepare_sketch_projection":
        raise AssertionError("projection capability has the wrong prepare operation")
    if projection.get("edit_operation") != "operation.edit_sketch_projection":
        raise AssertionError("projection capability has the wrong edit operation")
    if projection.get("request_fields") != expected_fields:
        raise AssertionError("projection capability request_fields differ from the explicit F10 contract")
    supported = str(projection.get("supported_direction", "")).casefold()
    for required_word in ("parallel", "antiparallel", "reject"):
        if required_word not in supported:
            raise AssertionError("projection capability must accept either normal sign and reject other directions")
    return {"prepare_operation": projection["prepare_operation"],
            "edit_operation": projection["edit_operation"],
            "request_fields": expected_fields,
            "supported_direction": projection["supported_direction"]}


def _geometry_ids(sketch: dict, label: str) -> list[str]:
    geometry = sketch.get("geometry")
    if not isinstance(geometry, list):
        raise AssertionError(f"{label} sketch inspection omitted geometry")
    ids = []
    for item in geometry:
        journal_id = item.get("journal_id") if isinstance(item, dict) else None
        if not isinstance(journal_id, str) or not journal_id:
            raise AssertionError(f"{label} sketch geometry omitted journal_id")
        ids.append(journal_id)
    if len(ids) != len(set(ids)):
        raise AssertionError(f"{label} sketch contains duplicate geometry journal IDs")
    return ids


def _sketch_signature(sketch: dict) -> dict:
    """Fields expected to remain unchanged across a read-only prepare."""
    return {
        "journal_id": sketch.get("journal_id"),
        "origin": sketch.get("origin"),
        "normal": sketch.get("normal"),
        "x_axis": sketch.get("x_axis"),
        "y_axis": sketch.get("y_axis"),
        "geometry": sketch.get("geometry"),
        "constraints": sketch.get("constraints"),
        "support_association": sketch.get("support_association"),
        "references": sketch.get("references"),
    }


def _validate_plan(payload: dict, request: dict, expected_direction: list[float], label: str) -> dict:
    if _status_state(payload) is not None:
        if _status_state(payload) != "SUCCEEDED":
            raise AssertionError(f"{label} prepare operation did not succeed")
        payload = _operation_result(payload)
    if not isinstance(payload, dict):
        raise AssertionError(f"{label} prepare omitted its operation result")
    plan = payload.get("plan")
    if not isinstance(plan, dict):
        raise AssertionError(f"{label} prepare omitted plan")
    if not isinstance(plan.get("plan_id"), str) or not plan["plan_id"]:
        raise AssertionError(f"{label} prepare omitted plan.plan_id")
    if plan.get("kind") != "edit_sketch_projection":
        raise AssertionError(f"{label} prepare returned the wrong plan kind")
    if plan.get("revision_id") != request["revision_id"]:
        raise AssertionError(f"{label} plan targets a different model revision")
    if plan.get("sketch_id") != request["sketch_id"]:
        raise AssertionError(f"{label} plan changed the explicit target sketch")
    if plan.get("source_curve_ids") != request["source_curve_ids"]:
        raise AssertionError(f"{label} plan changed the explicit source selection")
    if plan.get("associative") is not request["associative"]:
        raise AssertionError(f"{label} plan changed the explicit associativity setting")
    if not _vector_close(plan.get("direction_xyz"), expected_direction):
        raise AssertionError(f"{label} plan direction differs from the normalized explicit direction")
    return plan


def _combined_prepare_plan(result: dict, label: str) -> dict:
    nested = result.get("prepare_plan")
    plan_id = result.get("plan_id")
    if not isinstance(nested, dict):
        raise AssertionError(f"{label} result omitted its combined prepare plan")
    if not isinstance(plan_id, str) or not plan_id:
        raise AssertionError(f"{label} result omitted its top-level plan_id")
    if nested.get("plan_id") not in (None, plan_id):
        raise AssertionError(f"{label} top-level and nested plan IDs differ")
    copied = dict(nested)
    copied["plan_id"] = plan_id
    return copied


def _check_plan_source_identity(plan: dict, fixture: dict, request_source_ids: list[str], label: str,
                                live_tags_by_source_id: dict[str, str] | None = None,
                                body_ids_by_source_id: dict[str, str] | None = None) -> dict:
    """Initialize/check current-session live Tags using fixture geometry and requested current JIDs.

    Fixture Tags are only provenance labels: they are not stable across loading a saved part.
    The preview is used to identify the selected native source objects; its projected geometry
    is never used as the numeric oracle.
    """
    preview = plan.get("preview") if isinstance(plan.get("preview"), dict) else {}
    snapshot = preview.get("source_snapshot") if isinstance(preview.get("source_snapshot"), dict) else {}
    rows = snapshot.get("sources")
    if not isinstance(rows, list) or len(rows) != len(fixture["source_records"]):
        raise AssertionError(f"{label} plan omitted its explicit native source identity inventory")
    if len(request_source_ids) != len(fixture["source_records"]):
        raise AssertionError(f"{label} request source IDs do not cover the fixture source list")
    current_tags = {}
    current_source_ids = {}
    current_body_ids = {}
    evidence_rows = []
    for source, request_id, row in zip(fixture["source_records"], request_source_ids, rows):
        if not isinstance(row, dict):
            raise AssertionError(f"{label} source preview contains a malformed record")
        tag, journal_id = row.get("tag"), row.get("journal_id")
        if not isinstance(tag, str) or not tag or tag in current_tags.values():
            raise AssertionError(f"{label} source preview contains missing or duplicate live Tags")
        if not isinstance(journal_id, str) or not journal_id:
            raise AssertionError(f"{label} source preview omitted current JournalIdentifier")
        if journal_id != request_id:
            raise AssertionError(f"{label} request JID does not resolve to its selected source record")
        current_source_ids[source["source_curve_id"]] = journal_id
        if str(row.get("object_type", "")).rsplit(".", 1)[-1].casefold() != source["source_class"]:
            raise AssertionError(f"{label} selected a different native source type for {request_id}")
        if source["numeric_primitive"] is not None:
            match = _source_snapshot_geometry_match(row, source)
            if match is None:
                raise AssertionError(f"{label} request JID {request_id} does not match the independent fixture source geometry")
        else:
            match = {"numeric_status": "NOT_AVAILABLE_FROM_FIXTURE",
                     "limitation": source["numeric_oracle_limitation"]}
        if live_tags_by_source_id is not None:
            established_tag = live_tags_by_source_id.get(source["source_curve_id"])
            if established_tag is None or established_tag != tag:
                raise AssertionError(f"{label} source live Tag changed within the active loaded part")
        if source["source_class"] == "edge":
            body_id = row.get("body_id")
            if not isinstance(body_id, str) or not body_id:
                raise AssertionError(f"{label} Edge source preview omitted its current Body identity")
            if body_ids_by_source_id is not None:
                established_body_id = body_ids_by_source_id.get(source["source_curve_id"])
                if established_body_id is None or established_body_id != body_id:
                    raise AssertionError(f"{label} Edge source Body identity changed within the active loaded part")
            current_body_ids[source["source_curve_id"]] = body_id
        current_tags[source["source_curve_id"]] = tag
        evidence_rows.append({
            "fixture_source_curve_id": source["source_curve_id"],
            "fixture_tag_provenance": source["tag"],
            "live_tag": tag,
            "request_journal_id": request_id,
            "source_geometry_match": match,
        })
    return {"live_tags_by_source_id": current_tags,
            "source_ids_by_source_id": current_source_ids,
            "body_ids_by_source_id": current_body_ids,
            "source_records": evidence_rows,
            "fixture_tags_used_as_provenance_only": True,
            "resolved_by_request_journal_id_and_independent_source_geometry": True,
            "preview_used_as_projection_oracle": False,
            "body_identity_captured_from_current_prepare": True}


def _validate_paths(args: argparse.Namespace, report: dict) -> tuple[dict | None, Path]:
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
    fixture_path = args.fixture_json.resolve(strict=True)
    if not fixture_path.is_file() or not fixture_path.is_relative_to(evidence_root):
        raise ValueError("--fixture-json must be an existing native F10 fixture .json under workspace/v2/evidence")
    fixture = load_fixture_json(fixture_path)
    part_path = Path(fixture["part_path"]).resolve(strict=True)
    if (not part_path.is_file() or part_path.suffix.casefold() != ".prt"
            or not part_path.is_relative_to(model_root)):
        raise ValueError("fixture part_path must be an existing saved .prt inside workspace/v2/models")
    if _canonical_path(part_path) == _canonical_path(output_path):
        raise ValueError("part path and evidence path must be different")
    report["part_path"] = str(part_path)
    report["fixture_json_path"] = str(fixture_path)
    stat = part_path.stat()
    report["source_part_file"] = {
        "size_bytes": stat.st_size,
        "modified_time_utc": datetime.datetime.fromtimestamp(
            stat.st_mtime, datetime.timezone.utc).isoformat(),
    }
    report["fixture_selection"] = {
        "associative_true_sketch_id": fixture["target_sketch_ids"]["associative_true"],
        "associative_false_sketch_id": fixture["target_sketch_ids"]["associative_false"],
        "fixture_source_curve_ids": list(fixture["source_curve_ids"]),
        "numeric_coverage": fixture["numeric_coverage"],
        "unverified_edge_source_ids": fixture["unverified_edge_source_ids"],
    }
    return fixture, output_path


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--ready", action="store_true",
                      help="check installed F10 MCP schemas/capabilities and idle bridge status")
    mode.add_argument("--fixture-json", type=Path,
                      help="native fixture JSON from f10-create-mcp-fixture.py under workspace/v2/evidence")
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


def _tool_names_and_version(client: McpStdioClient, report: dict) -> list:
    initialize = client.request("initialize", {
        "protocolVersion": PROTOCOL_VERSION,
        "capabilities": {},
        "clientInfo": {"name": "f10-installed-mcp-smoke", "version": "1"},
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
        raise AssertionError(f"installed MCP must expose exactly {EXPECTED_TOOL_COUNT} unique tools")
    if set(names) != EXPECTED_TOOLS:
        raise AssertionError("installed MCP tool inventory differs from the expected F10 tool set")
    report["tool_count"] = len(names)
    report["tool_names"] = sorted(names)
    report["projection_schemas"] = _redact(_check_projection_schemas(tools))
    report["tools_list"] = _redact(tools)
    return tools


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    run_id = uuid.uuid4().hex
    mode = "ready" if args.ready else "fixture"
    report: dict = {
        "test": "F10_installed_mcp_smoke",
        "run_id": run_id,
        "mode": mode,
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "passed": False,
        "objective": (
            "Verify installed F10 projection schemas/capability; for a saved disposable fixture, prove both signs "
            "of the target sketch normal prepare without mutation, oblique prepare rejection, one associative and "
            "one non-associative combined edit, same-operation-ID lookup equivalence, an independent one-to-one "
            "Line/partial Arc/full Circle oracle, Tag/geometry-based source re-resolution, direct old-geometry "
            "preservation, Save Copy/reopen, and safe ownership return."
        ),
        "traceability": [
            "F10 nx_prepare_sketch_projection and nx_edit_sketch_projection exact required-field schema",
            "F10 direction contract: parallel or antiparallel to the target normal; oblique direction rejects during prepare",
            "F10 associative=true and associative=false integrated edit behavior",
            "F10 same-operation-ID nx_get_operation confirmation and unknown-outcome no-replay rule",
            "F10 independent Euclidean Line/Arc/Circle geometry oracle with one-to-one output matching",
            "F10 source Tag and geometry conservation across native edits; post-reopen source Sketch remapping",
            "F10 direct target sketch inspection and Save Copy/reopen",
            "F10 ownership return from a known idle, unhalted bridge state",
        ],
        "test_class": "installed MCP integration and NX sketch subsystem smoke validation",
        "configuration": {
            "expected_mcp_version": EXPECTED_SERVER_VERSION,
            "expected_tool_count": EXPECTED_TOOL_COUNT,
            "mcp_stdio_max_response_line_bytes": MCP_RESPONSE_LINE_LIMIT_BYTES,
            "sample_size": 1,
            "position_tolerance_mm": POSITION_TOLERANCE_MM,
            "length_tolerance_mm": LENGTH_TOLERANCE_MM,
            "angle_tolerance_rad": ANGLE_TOLERANCE_RAD,
            "measurement_uncertainty": "digital NX model inspection; no physical instrument used",
            "reliability_or_compliance_inference": False,
        },
        "safety_controls": {
            "NX_launch_or_control": "not invoked; this script starts only the installed MCP stdio child",
            "ownership_scope": "caller supplied saved independent fixture; exact Work Part path and clean state gated by nx_takeover",
            "source_part_save": "never saves over supplied path; Save Copy uses a new unique roundtrip path",
            "write_count": "one nx_edit_sketch_projection call per explicit associative case; unique operation_id per edit",
            "unknown_edit_outcome": "query the same nx_get_operation ID; never replay; skip return while outcome is unknown",
            "ownership_return": "only from known idle, unhalted AI_OWNED bridge status",
        },
        "instrumentation": "installed MCP operation records, direct NX sketch inspection, fixture source Tag/geometry, and an independent Euclidean projection oracle",
        "calibration": "no physical instrument; CAD model-value limits are digital comparison thresholds",
        "calls": [],
        "cases": [],
    }
    client: McpStdioClient | None = None
    process: subprocess.Popen[bytes] | None = None
    credentials: dict[str, object] = {}
    fixture: dict | None = None
    part_path: Path | None = None
    output_path: Path | None = None
    active_case: dict | None = None
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
            entry["transport_error_description"] = _safe_transport_error_description(exc, client)
            persist()
            raise RuntimeError(f"{name} MCP transport failed ({type(exc).__name__})") from None
        entry["mcp_response"] = _redact(response)
        payload = _extract_payload(response, name)
        entry["payload"] = _redact(payload)
        entry["is_error"] = response.get("isError") is True
        persist()
        if response.get("isError") is True and not allow_tool_error:
            code = _error_code(response) or _error_code(payload) or "UNKNOWN"
            raise RuntimeError(f"{name} returned MCP tool error {code}")
        return payload, entry

    def case(case_id: str, request: dict, retest_criterion: str) -> dict:
        nonlocal active_case
        entry = {
            "case_id": case_id,
            "state": "RUNNING",
            "request": _redact(request),
            "reproduction_steps": [
                "Use a new clean saved millimeter .prt and matching native fixture JSON created by f10-create-mcp-fixture.py.",
                "Run this script with that --fixture-json and a new evidence path under workspace/v2/evidence.",
                f"Repeat {case_id} on a fresh unique fixture and compare the MCP result with the independent numeric oracle and direct Sketch inspection.",
            ],
            "retest_criterion": retest_criterion,
        }
        report["cases"].append(entry)
        active_case = entry
        persist()
        return entry

    def current_session(expected_path: Path, label: str) -> dict:
        if not credentials:
            raise RuntimeError("cannot read the NX session without current ownership credentials")
        session = call_raw("nx_get_session", credentials)[0]
        returned_path = session.get("part_path")
        if not isinstance(returned_path, str) or _canonical_path(returned_path) != _canonical_path(expected_path):
            raise AssertionError(f"{label} session path differs from the expected Work Part")
        revision = session.get("revision_id")
        if not isinstance(revision, str) or not revision:
            raise AssertionError(f"{label} session omitted revision_id")
        report.setdefault("session_observations", []).append({
            "stage": label,
            "revision_id": revision,
            "part_path": returned_path,
            "part_units": session.get("part_units"),
            "is_modified_before_read": session.get("is_modified_before_read"),
            "is_modified_after_read": session.get("is_modified_after_read"),
            "dirty_changed_by_read": session.get("dirty_changed_by_read"),
        })
        persist()
        return session

    def inspect_sketch(sketch_id: str, label: str) -> dict:
        inspected = call_raw("nx_inspect_sketch", {**credentials, "sketch_id": sketch_id})[0]
        sketch = inspected.get("sketch") if isinstance(inspected.get("sketch"), dict) else inspected
        if not isinstance(sketch, dict) or sketch.get("journal_id") != sketch_id:
            raise AssertionError(f"{label} inspection returned a different sketch journal_id")
        _geometry_ids(sketch, label)
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
            result = {"skipped": "F10 write outcome is unknown; ownership remains with the bridge"}
            report["ownership_return"] = result
            persist()
            return result
        status = call_raw("nx_bridge_status", {})[0]
        in_flight = _in_flight_count(status.get("in_flight", status.get("active_operations", -1)))
        if (status.get("state") != "loaded" or status.get("ownership_state") != "AI_OWNED"
                or status.get("writes_halted") is not False or in_flight != 0):
            result = {"skipped": "bridge is not a known idle, unhalted AI_OWNED state",
                      "ownership_state": status.get("ownership_state"),
                      "writes_halted": status.get("writes_halted"), "in_flight": in_flight}
            report["ownership_return"] = result
            persist()
            return result
        ownership_return_attempted = True
        returned = call_raw("nx_return", credentials)[0]
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("nx_return did not confirm safe HUMAN_OWNED state")
        credentials.clear()
        final_status = call_raw("nx_bridge_status", {})[0]
        final_flight = _in_flight_count(final_status.get("in_flight", final_status.get("active_operations", -1)))
        if (final_status.get("state") != "loaded" or final_status.get("ownership_state") != "HUMAN_OWNED"
                or final_status.get("writes_halted") is not False or final_flight != 0):
            raise AssertionError("final bridge status is not safe for human ownership")
        result = {"return": _redact(returned), "final_status": _redact(final_status)}
        report["ownership_return"] = result
        persist()
        return result

    def wait_for_operation(initial: dict, operation_id: str, label: str,
                           *, force_query: bool = False) -> tuple[dict, dict | None]:
        def query_same_operation(requested_operation_id: str):
            current, poll_entry = call_raw(
                "nx_get_operation", {"operation_id": requested_operation_id}, allow_tool_error=True,
            )
            poll_entry["poll_for_operation_id"] = requested_operation_id
            return current, poll_entry

        return _wait_for_operation(initial, operation_id, label, query_same_operation,
                                   force_query=force_query)

    def submit_edit(request: dict, label: str) -> dict:
        nonlocal unknown_write_outcome
        unknown_write_outcome = True
        initial, entry = call_raw("nx_edit_sketch_projection", request, allow_tool_error=True)
        state = _status_state(initial)
        error_code = _error_code(initial)
        if entry.get("is_error") is True and state is None:
            if error_code in AMBIGUOUS_WRITE_CODES:
                queried, query_entry = call_raw("nx_get_operation", {"operation_id": request["operation_id"]},
                                                allow_tool_error=True)
                query_entry["unknown_outcome_query_for"] = request["operation_id"]
                entry["unknown_outcome_query"] = _redact(queried)
                state = _status_state(queried)
                if state not in {"SUCCEEDED", "FAILED", "CANCELLED"}:
                    raise RuntimeError(f"{label} outcome remains UNKNOWN after same-ID operation query; do not retry")
                initial = queried
                operation_query = queried
            else:
                unknown_write_outcome = False
                raise RuntimeError(f"{label} was rejected before returning an operation record ({error_code or 'UNKNOWN'})")
        elif state == "UNKNOWN" or initial.get("outcome_unknown") is True:
            queried, query_entry = call_raw("nx_get_operation", {"operation_id": request["operation_id"]},
                                            allow_tool_error=True)
            query_entry["unknown_outcome_query_for"] = request["operation_id"]
            entry["unknown_outcome_query"] = _redact(queried)
            state = _status_state(queried)
            if state not in {"SUCCEEDED", "FAILED", "CANCELLED"}:
                raise RuntimeError(f"{label} outcome remains UNKNOWN after same-ID operation query; do not retry")
            initial = queried
            operation_query = queried
        else:
            initial, operation_query = wait_for_operation(initial, request["operation_id"], label)
            state = _status_state(initial)
            if state == "UNKNOWN":
                if operation_query is None:
                    queried, query_entry = call_raw("nx_get_operation", {"operation_id": request["operation_id"]},
                                                    allow_tool_error=True)
                    query_entry["unknown_outcome_query_for"] = request["operation_id"]
                    operation_query = queried
                    state = _status_state(queried)
                    initial = queried
                if state not in {"SUCCEEDED", "FAILED", "CANCELLED"}:
                    raise RuntimeError(f"{label} remains UNKNOWN after same-ID operation query; do not retry")
        if state != "SUCCEEDED":
            unknown_write_outcome = False
            raise AssertionError(f"{label} operation ended in {state or 'UNKNOWN'}")
        if initial.get("verified") is not True or initial.get("success") is not True:
            unknown_write_outcome = False
            raise AssertionError(f"{label} did not return success=true and verified=true")
        result = _operation_result(initial)
        if not isinstance(result, dict) or result.get("verified") is not True:
            unknown_write_outcome = False
            raise AssertionError(f"{label} operation record omitted a verified result")
        if result.get("combined_edit") is not True or result.get("mutation_state") != "APPLIED":
            unknown_write_outcome = False
            raise AssertionError(f"{label} did not confirm one applied combined edit")
        combined_plan = _combined_prepare_plan(result, label)
        _validate_plan({"plan": combined_plan}, request,
                       normalize_vector(request["direction_xyz"], "edit direction"), label)

        if operation_query is None:
            queried, query_entry = call_raw(
                "nx_get_operation", {"operation_id": request["operation_id"]}, allow_tool_error=True,
            )
            query_entry["explicit_same_operation_id_confirmation"] = True
        else:
            queried = operation_query
        returned_operation = queried.get("operation") if isinstance(queried.get("operation"), dict) else {}
        returned_id = queried.get("operation_id") or returned_operation.get("operation_id") or returned_operation.get("id")
        if returned_id != request["operation_id"]:
            raise AssertionError(f"{label} nx_get_operation returned a different operation_id")
        if _status_state(queried) != "SUCCEEDED":
            raise AssertionError(f"{label} nx_get_operation did not confirm SUCCEEDED")
        queried_result = _operation_result(queried)
        if not isinstance(queried_result, dict) or _canonical_json(queried_result) != _canonical_json(result):
            raise AssertionError(f"{label} same-ID operation result differs from the edit response")
        unknown_write_outcome = False
        return {"edit_response": _redact(initial), "operation_record": _redact(queried),
                "same_operation_id": request["operation_id"], "result_equivalent": True,
                "result": _redact(result)}

    try:
        evidence_candidate = args.output.resolve(strict=False)
        evidence_root = (ROOT / "workspace" / "v2" / "evidence").resolve(strict=False)
        if evidence_candidate.is_relative_to(evidence_root) and not evidence_candidate.exists():
            output_path = evidence_candidate
        fixture, output_path = _validate_paths(args, report)
        part_path = Path(fixture["part_path"]).resolve(strict=True) if fixture is not None else None

        install_root = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy()
        env.update(PYTHONPATH=str(install_root / "app" / "src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen(
            [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install_root / "mcp-config.json")],
            cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, bufsize=0,
        )
        client = McpStdioClient(process, 60, max_line_bytes=MCP_RESPONSE_LINE_LIMIT_BYTES)
        tools = _tool_names_and_version(client, report)
        capabilities = call_raw("nx_get_capabilities", {})[0]
        report["projection_capability"] = _redact(_check_projection_capability(capabilities))
        report["capabilities"] = _redact(capabilities)

        initial_status = call_raw("nx_bridge_status", {})[0]
        report["initial_status"] = _redact(initial_status)
        if initial_status.get("state") != "loaded" or initial_status.get("writes_halted") is not False:
            raise AssertionError("installed NX bridge must be loaded with its write gate open")
        in_flight = _in_flight_count(initial_status.get("in_flight", initial_status.get("active_operations", -1)))
        if args.ready:
            if initial_status.get("ownership_state") != "HUMAN_OWNED" or in_flight != 0:
                raise AssertionError("--ready requires an idle HUMAN_OWNED bridge")
            report.update(passed=True, diagnostic_only=True,
                          ready_result="installed F10 schemas/capability and idle bridge status verified")
            return 0

        if initial_status.get("ownership_state") != "HUMAN_OWNED" or in_flight != 0:
            raise AssertionError("model mode requires idle HUMAN_OWNED bridge status before takeover")
        if fixture is None or part_path is None:
            raise AssertionError("model mode requires --fixture-json")
        nx_pid = initial_status.get("pid")
        if isinstance(nx_pid, bool) or not isinstance(nx_pid, int) or nx_pid <= 0:
            raise AssertionError("bridge status omitted a valid NX pid")
        takeover_case = case(
            "F10_saved_fixture_takeover_gate",
            {"expected_pid": nx_pid, "expected_part_path": str(part_path),
             "artifact_scope": "saved_independent_copy", "authorize": True},
            "Use a new saved clean standalone millimeter fixture, require exact Work Part path identity and clean-state checks, then repeat both association cases, the direction domain checks, and Save Copy/reopen.",
        )
        takeover = call_raw("nx_takeover", {
            "expected_pid": nx_pid,
            "expected_part_path": str(part_path),
            "artifact_scope": "saved_independent_copy",
            "authorize": True,
        })[0]
        owner, epoch = takeover.get("ownership_id"), takeover.get("binding_epoch")
        if (not isinstance(owner, str) or not owner or isinstance(epoch, bool)
                or not isinstance(epoch, int) or epoch < 1):
            raise AssertionError("nx_takeover omitted current ownership credentials")
        credentials.update(ownership_id=owner, binding_epoch=epoch)
        if takeover.get("ownership_state") != "AI_OWNED":
            raise AssertionError("nx_takeover did not establish AI_OWNED state")
        if _canonical_path(takeover.get("part_path", "")) != _canonical_path(part_path):
            raise AssertionError("nx_takeover identity differs from the caller-supplied fixture")
        if (takeover.get("artifact_scope") != "saved_independent_copy"
                or takeover.get("artifact_kind") != "standalone_part"):
            raise AssertionError("nx_takeover did not confirm a saved standalone-part scope")
        takeover_dirty = _read_effect_after(takeover, "takeover", False)
        if takeover.get("is_modified") is not takeover_dirty:
            raise AssertionError("takeover current dirty state differs from its actual read result")
        if takeover_dirty and takeover.get("read_may_mark_modified") is not True:
            raise AssertionError("takeover dirtied the fixture without declaring the native getter side effect")
        takeover_case.update(state="PASS", path_match=True, clean_before_read=True,
                             dirty_after_native_read=takeover_dirty,
                             dirty_changed_by_read=takeover.get("dirty_changed_by_read"))
        report["takeover_gate_evidence"] = _redact(takeover)
        persist()

        initial_session = current_session(part_path, "after takeover")
        if str(initial_session.get("part_units", "")).casefold() not in {"millimeter", "millimeters", "mm"}:
            raise AssertionError("F10 fixture must use millimeter part units")
        session_dirty = _read_effect_after(initial_session, "post-takeover session", takeover_dirty)

        initial_part = call_raw("nx_inspect_part", credentials)[0]
        part_record = initial_part.get("part") if isinstance(initial_part.get("part"), dict) else {}
        identity = initial_part.get("identity") if isinstance(initial_part.get("identity"), dict) else {}
        inspected_path = part_record.get("path") or identity.get("part_path")
        if not isinstance(inspected_path, str) or _canonical_path(inspected_path) != _canonical_path(part_path):
            raise AssertionError("nx_inspect_part returned a different Work Part path")
        read_effects = initial_part.get("read_effects") if isinstance(initial_part.get("read_effects"), dict) else {}
        inspection_dirty = _read_effect_after(read_effects, "initial part inspection", session_dirty)
        if part_record.get("is_modified") is not inspection_dirty:
            raise AssertionError("initial inspection current dirty state disagrees with native read effects")
        report["initial_part_inspection"] = _redact(initial_part)
        report["configuration"].update({
            "nx_build": takeover.get("nx_build"),
            "nxopen_assembly_version": takeover.get("nxopen_assembly_version"),
            "nxopen_file_version": takeover.get("nxopen_file_version"),
            "part_units": initial_session.get("part_units"),
        })

        if fixture is None or part_path is None:
            raise AssertionError("model mode requires a validated --fixture-json")

        target_roles = ("associative_true", "associative_false")
        target_frames = fixture["target_frames"]
        target_current_ids = {}
        target_baselines = {}
        for role in target_roles:
            target_record = resolve_target_sketch(initial_part, target_frames[role], role + " target")
            target_id = target_record.get("journal_id")
            if not isinstance(target_id, str) or not target_id:
                raise AssertionError(f"{role} target Sketch omitted journal_id")
            sketch = inspect_sketch(target_id, role + " target")
            if not _frame_matches(sketch, target_frames[role]):
                raise AssertionError(f"{role} target Sketch frame differs from the fixture frame")
            if _geometry_ids(sketch, role + " target"):
                raise AssertionError(f"{role} target Sketch must be empty before projection")
            if not isinstance(sketch.get("constraints"), list) or sketch["constraints"]:
                raise AssertionError(f"{role} target Sketch must have an empty constraint inventory")
            target_current_ids[role] = target_id
            target_baselines[role] = sketch

        source_resolution = resolve_source_ids_from_snapshot(initial_part, fixture)
        source_ids = source_resolution["source_curve_ids"]
        source_sketch_id = source_resolution["source_sketch"]["sketch_id"]
        source_sketch_before = inspect_sketch(source_sketch_id, "initial source Sketch")
        if not _frame_matches(source_sketch_before, fixture["source_sketch_frame"]):
            raise AssertionError("initial source Sketch frame differs from the fixture")
        if preserved_geometry_mapping([], source_sketch_before.get("geometry", []), "source Sketch baseline")["new_count"] != len(source_ids) - sum(
                1 for item in fixture["source_records"] if item["source_class"] == "edge"):
            raise AssertionError("initial source Sketch geometry does not map one-to-one to native Line/Arc fixture sources")
        report["source_resolution_before_edit"] = {
            "fixture_source_ids": fixture["source_curve_ids"],
            "current_source_ids": source_ids,
            "source_sketch_id_fixture": fixture["source_sketch_id"],
            "source_sketch_id_current": source_sketch_id,
            "source_journal_id_remaps": [
                {"source_tag": record["tag"], "fixture_journal_id": record["source_curve_id"],
                 "current_journal_id": current_id,
                 "changed": record["source_curve_id"] != current_id}
                for record, current_id in zip(fixture["source_records"], source_ids)
            ],
            "edge_source_resolution": source_resolution["edge_references"],
            "source_sketch_geometry": _redact(source_sketch_before),
        }
        report["fixture_configuration"] = {
            "schema": fixture["schema"],
            "source_sketch_id": source_sketch_id,
            "source_curve_ids": source_ids,
            "source_curve_tags": [item["tag"] for item in fixture["source_records"]],
            "source_types": [item["source_type"] for item in fixture["source_records"]],
            "numeric_coverage": fixture["numeric_coverage"],
            "numeric_oracle_unavailable_sources": [
                {"source_curve_id": item["source_curve_id"],
                 "source_tag": item["tag"],
                 "reason": item["numeric_oracle_limitation"]}
                for item in fixture["source_records"] if item["numeric_oracle_unavailable"]
            ],
        }
        if fixture["unverified_edge_source_ids"]:
            report.setdefault("scope_limitations", []).append(
                "The listed Edge source(s) have no fixture geometry primitive and are accounted for only by source identity/readback and output count; they are not claimed as numerically verified."
            )
        report["scope_limitations"] = report.get("scope_limitations", []) + [
            "After Save Copy/reopen, the installed MCP exposes source Sketch geometry by JID but does not expose source Edge Tag plus direct geometry. Edge relation persistence is reported as an interface limitation unless the fixture provides a directly inspectable source Sketch primitive."
        ]
        persist()
        latest_source_ids_by_source_id = None
        latest_body_ids_by_source_id = None
        current_live_tags_by_source_id = None

        def inspect_part_snapshot(label: str) -> dict:
            payload = call_raw("nx_inspect_part", credentials)[0]
            part_obj = payload.get("part") if isinstance(payload.get("part"), dict) else {}
            identity_obj = payload.get("identity") if isinstance(payload.get("identity"), dict) else {}
            path_value = part_obj.get("path") or identity_obj.get("part_path")
            expected_path = part_path
            if label.startswith("reopened"):
                expected_path = Path(report.get("saved_path", part_path))
            if (expected_path is None or not isinstance(path_value, str)
                    or _canonical_path(path_value) != _canonical_path(expected_path)):
                raise AssertionError(f"{label} nx_inspect_part path differs from the expected Work Part")
            return payload

        def inspect_current_target(part_snapshot: dict, role: str, label: str) -> dict:
            record = resolve_target_sketch(part_snapshot, target_frames[role], label)
            sketch_id = record.get("journal_id")
            if not isinstance(sketch_id, str) or not sketch_id:
                raise AssertionError(f"{label} target frame record omitted journal_id")
            direct = inspect_sketch(sketch_id, label)
            if not _frame_matches(direct, target_frames[role]):
                raise AssertionError(f"{label} direct target frame differs from the fixture")
            target_current_ids[role] = sketch_id
            return direct

        def prepare_case(case_id: str, role: str, direction: list[float], associative: bool,
                         label: str) -> dict:
            nonlocal latest_source_ids_by_source_id, latest_body_ids_by_source_id
            nonlocal current_live_tags_by_source_id
            before_session = current_session(part_path, f"before {label} prepare")
            before_part = inspect_part_snapshot(f"before {label} prepare")
            before_sketch = inspect_current_target(before_part, role, f"before {label} prepare")
            request = {
                "operation_id": "f10-prepare-" + uuid.uuid4().hex,
                **credentials,
                "revision_id": before_session["revision_id"],
                "sketch_id": target_current_ids[role],
                "source_curve_ids": list(source_ids),
                "direction_xyz": direction,
                "associative": associative,
            }
            item = case(case_id, {key: value for key, value in request.items() if key not in credentials},
                        "On a fresh native fixture, repeat the read-only prepare with the same Tag/geometry-resolved sources; require explicit normalized direction, unchanged source and target geometry mappings, unchanged revision, and no plan for oblique input.")
            prepared_payload = call_raw("nx_prepare_sketch_projection", request)[0]
            if _status_state(prepared_payload) is not None:
                prepared_payload, _ = wait_for_operation(prepared_payload, request["operation_id"], label + " prepare")
            expected_direction = normalize_vector(direction, "requested direction")
            plan = _validate_plan(prepared_payload, request, expected_direction, label)
            plan_sources = _check_plan_source_identity(
                plan, fixture, source_ids, label, current_live_tags_by_source_id,
                latest_body_ids_by_source_id,
            )
            if current_live_tags_by_source_id is None:
                current_live_tags_by_source_id = plan_sources["live_tags_by_source_id"]
                latest_source_ids_by_source_id = plan_sources["source_ids_by_source_id"]
                latest_body_ids_by_source_id = plan_sources["body_ids_by_source_id"]
            after_session = current_session(part_path, f"after {label} prepare")
            after_part = inspect_part_snapshot(f"after {label} prepare")
            after_sketch = inspect_current_target(after_part, role, f"after {label} prepare")
            source_after = resolve_source_ids_from_snapshot(
                after_part, fixture, latest_source_ids_by_source_id, latest_body_ids_by_source_id,
            )
            if source_after["source_curve_ids"] != source_ids:
                raise AssertionError(f"{label} prepare changed source JID mapping or source geometry")
            if after_session.get("revision_id") != before_session.get("revision_id"):
                raise AssertionError(f"{label} prepare changed model revision")
            for field in ("is_modified_before_read", "is_modified_after_read", "dirty_changed_by_read"):
                if after_session.get(field) != before_session.get(field):
                    raise AssertionError(f"{label} prepare changed session dirty-state evidence")
            preserved = preserved_geometry_mapping(before_sketch.get("geometry", []),
                                                    after_sketch.get("geometry", []), label + " target prepare")
            source_preserved = preserved_geometry_mapping(source_sketch_before.get("geometry", []),
                                                           source_after["source_sketch"]["geometry"],
                                                           label + " source Sketch prepare")
            if preserved["new_count"] or source_preserved["new_count"]:
                raise AssertionError(f"{label} prepare changed old target/source Sketch geometry")
            if after_session.get("dirty_changed_by_read") is not False:
                raise AssertionError(f"{label} session reads changed or failed to preserve dirty state")
            item.update(state="PASS", normalized_direction_xyz=expected_direction,
                        plan_id=plan["plan_id"], plan=_redact(plan),
                        source_tag_selection=plan_sources,
                        source_sketch_preservation=source_preserved,
                        target_old_geometry_mapping=preserved["mapping"],
                        revision_unchanged=True, target_sketch_unchanged=True)
            persist()
            return {"request": request, "plan": plan, "case": item}

        true_role, false_role = "associative_true", "associative_false"
        true_sketch_id = target_current_ids[true_role]
        false_sketch_id = target_current_ids[false_role]
        true_normal = fixture["target_frames"][true_role]["normal"]
        false_normal = fixture["target_frames"][false_role]["normal"]
        positive = prepare_case("F10_positive_normal_prepare", true_role, true_normal, True,
                                "positive-normal associative")

        oblique_direction = normalize_vector(_add(true_normal, fixture["target_frames"][true_role]["x_axis"]),
                                             "oblique direction")
        before_oblique_session = current_session(part_path, "before oblique prepare")
        before_oblique_part = inspect_part_snapshot("before oblique prepare")
        oblique_target = inspect_current_target(before_oblique_part, true_role, "before oblique prepare")
        oblique_request = {
            "operation_id": "f10-oblique-prepare-" + uuid.uuid4().hex,
            **credentials,
            "revision_id": before_oblique_session["revision_id"],
            "sketch_id": target_current_ids[true_role],
            "source_curve_ids": list(source_ids),
            "direction_xyz": oblique_direction,
            "associative": True,
        }
        oblique_case = case(
            "F10_oblique_prepare_rejection",
            {key: value for key, value in oblique_request.items() if key not in credentials},
            "On a fresh matching fixture submit a finite oblique direction once; require a direction-specific error, no plan, and unchanged source/target geometry mapping, revision, and dirty state.",
        )
        oblique_initial, oblique_entry = call_raw("nx_prepare_sketch_projection", oblique_request,
                                                   allow_tool_error=True)
        oblique_payload, oblique_poll_entry = wait_for_operation(
            oblique_initial, oblique_request["operation_id"], "oblique prepare", force_query=True,
        )
        oblique_code = _validate_oblique_prepare_rejection(
            oblique_initial, oblique_payload, oblique_request["operation_id"],
            "oblique prepare", oblique_poll_entry,
        )
        after_oblique_session = current_session(part_path, "after oblique prepare")
        after_oblique_part = inspect_part_snapshot("after oblique prepare")
        after_oblique_sketch = inspect_current_target(after_oblique_part, true_role, "after oblique prepare")
        oblique_source_after = resolve_source_ids_from_snapshot(
            after_oblique_part, fixture, latest_source_ids_by_source_id,
            latest_body_ids_by_source_id,
        )
        if oblique_source_after["source_curve_ids"] != source_ids:
            raise AssertionError("oblique rejection changed source JID or Body identity mapping")
        source_preserved = preserved_geometry_mapping(source_sketch_before.get("geometry", []),
                                                       oblique_source_after["source_sketch"]["geometry"],
                                                       "oblique rejection source Sketch")
        target_preserved = preserved_geometry_mapping(oblique_target.get("geometry", []),
                                                       after_oblique_sketch.get("geometry", []),
                                                       "oblique rejection target Sketch")
        if after_oblique_session.get("revision_id") != before_oblique_session.get("revision_id"):
            raise AssertionError("oblique rejection changed model revision")
        if (after_oblique_session.get("is_modified_after_read") != before_oblique_session.get("is_modified_after_read")
                or after_oblique_session.get("dirty_changed_by_read") is not False):
            raise AssertionError("oblique rejection changed or failed to preserve dirty state")
        if source_preserved["new_count"] or target_preserved["new_count"]:
            raise AssertionError("oblique rejection changed old source or target Sketch geometry")
        oblique_case.update(state="PASS", error_code=oblique_code, rejected=True,
                            initial_response=_redact(oblique_initial),
                            terminal_operation=_redact(oblique_payload),
                            terminal_poll_for_operation_id=oblique_request["operation_id"],
                            plan_returned=False, source_sketch_mapping=source_preserved["mapping"],
                            target_sketch_mapping=target_preserved["mapping"], revision_unchanged=True)
        persist()

        expected_by_role = {}
        edit_evidence_by_role = {}
        for associative, role, direction, prepared, label in (
            (True, true_role, true_normal, positive, "associative true positive-normal edit"),
            (False, false_role, _scale(false_normal, -1.0), None, "associative false negative-normal edit"),
        ):
            if prepared is None:
                prepared = prepare_case("F10_negative_normal_prepare", role, direction, associative,
                                        "negative-normal non-associative")
            before_session = current_session(part_path, f"before {label}")
            before_part = inspect_part_snapshot(f"before {label}")
            target_before = inspect_current_target(before_part, role, f"before {label}")
            old_source = resolve_source_ids_from_snapshot(before_part, fixture,
                                                          latest_source_ids_by_source_id,
                                                          latest_body_ids_by_source_id)
            if old_source["source_curve_ids"] != source_ids:
                raise AssertionError(f"{label} did not use freshly re-resolved source IDs")
            if _geometry_ids(target_before, label):
                raise AssertionError(f"{label} target Sketch should have no old geometry before its first edit")
            source_before = old_source["source_sketch"]
            request = {
                "operation_id": "f10-edit-" + uuid.uuid4().hex,
                **credentials,
                "revision_id": before_session["revision_id"],
                "sketch_id": target_current_ids[role],
                "source_curve_ids": list(source_ids),
                "direction_xyz": direction,
                "associative": associative,
            }
            edit_case = case(
                "F10_associative_edit" if associative else "F10_non_associative_edit",
                {key: value for key, value in request.items() if key not in credentials},
                f"On a fresh fixture, perform one {label}, require state=SUCCEEDED and verified=true, query the same operation_id, re-resolve sources by Tag/direct geometry, and one-to-one match every provided Line/Arc/Circle primitive to direct target Sketch output.",
            )
            operation = submit_edit(request, label)
            combined_result = operation.get("result")
            if not isinstance(combined_result, dict):
                raise AssertionError(f"{label} operation record omitted its direct result")
            source_tag_evidence = _check_source_snapshot_after_edit(
                combined_result, fixture, current_live_tags_by_source_id,
                latest_body_ids_by_source_id,
            )
            after_session = current_session(part_path, f"after {label}")
            after_part = inspect_part_snapshot(f"after {label}")
            source_ids_by_source_id = source_tag_evidence["source_ids_by_source_id"]
            body_ids_by_source_id = source_tag_evidence["body_ids_by_source_id"]
            source_after = resolve_source_ids_from_snapshot(after_part, fixture,
                                                            source_ids_by_source_id, body_ids_by_source_id)
            if source_after["source_curve_ids"] != [source_ids_by_source_id[item["source_curve_id"]]
                                                      for item in fixture["source_records"]]:
                raise AssertionError(f"{label} Tag-based source records and direct source Sketch IDs disagree")
            source_preserved = preserved_geometry_mapping(source_before["geometry"],
                                                           source_after["source_sketch"]["geometry"],
                                                           label + " source Sketch conservation")
            if source_preserved["new_count"]:
                raise AssertionError(f"{label} projection changed or added source Sketch geometry")
            target_after = inspect_current_target(after_part, role, f"direct readback after {label}")
            target_preserved = preserved_geometry_mapping(target_before.get("geometry", []),
                                                           target_after.get("geometry", []),
                                                           label + " target old geometry")
            if target_preserved["preserved_count"] != len(target_before.get("geometry", [])):
                raise AssertionError(f"{label} target edit changed pre-existing Sketch geometry")
            expected_records = [expected_projection_record(item, target_frames[role], direction)
                                for item in fixture["source_records"] if item["numeric_primitive"] is not None]
            unverified_edges = sum(1 for item in fixture["source_records"]
                                   if item["numeric_primitive"] is None)
            numeric_match = match_projection_records(target_preserved["new_geometry"], expected_records,
                                                     unverified_edges, label)
            other_role = false_role if role == true_role else true_role
            other_before_record = resolve_target_sketch(before_part, target_frames[other_role],
                                                        label + " untouched target before")
            other_after = inspect_current_target(after_part, other_role, label + " untouched target after")
            untouched = preserved_geometry_mapping(other_before_record.get("geometry", []),
                                                    other_after.get("geometry", []),
                                                    label + " untouched target")
            if untouched["new_count"] or untouched["preserved_count"] != len(other_before_record.get("geometry", [])):
                raise AssertionError(f"{label} changed the other target Sketch")
            if after_session.get("revision_id") == before_session.get("revision_id"):
                raise AssertionError(f"{label} did not advance the model revision after its write")
            source_ids_after = source_after["source_curve_ids"]
            source_jid_remaps = [
                {"live_source_tag": current_live_tags_by_source_id[record["source_curve_id"]],
                 "fixture_tag_provenance": record["tag"],
                 "before_request_journal_id": before_id,
                 "after_edit_journal_id": after_id, "changed": before_id != after_id}
                for record, before_id, after_id in zip(fixture["source_records"], source_ids, source_ids_after)
            ]
            edit_case.update(
                state="PASS", operation_id=request["operation_id"],
                operation=operation, source_tag_readback=source_tag_evidence,
                source_journal_id_remaps=source_jid_remaps,
                source_sketch_preserved_geometry_mapping=source_preserved["mapping"],
                target_old_geometry_mapping=target_preserved["mapping"],
                numeric_projection_oracle=numeric_match,
                target_geometry_count_before=len(target_before.get("geometry", [])),
                target_geometry_count_after=len(target_after.get("geometry", [])),
                target_geometry_readback=_redact(target_after),
                untouched_target_geometry_mapping=untouched["mapping"],
                state_succeeded=True, verified=True,
            )
            expected_by_role[role] = expected_records
            edit_evidence_by_role[role] = {
                "associative": associative, "direction_xyz": direction,
                "numeric_match": numeric_match,
                "source_ids_by_source_id_after_edit": source_ids_by_source_id,
                "body_ids_by_source_id_after_edit": body_ids_by_source_id,
                "target_sketch_id_after_edit": target_current_ids[role],
                "target_geometry_after_edit": _redact(target_after),
            }
            source_ids = source_ids_after
            latest_source_ids_by_source_id = source_ids_by_source_id
            latest_body_ids_by_source_id = body_ids_by_source_id
            source_sketch_before = source_after["source_sketch"]
            persist()

        pre_save_part = inspect_part_snapshot("before F10 Save Copy")
        source_before_save = resolve_source_ids_from_snapshot(
            pre_save_part, fixture, latest_source_ids_by_source_id, latest_body_ids_by_source_id,
        )
        if source_before_save["source_curve_ids"] != source_ids:
            raise AssertionError("source IDs were not freshly resolved before Save Copy")
        source_geometry_before_save = source_before_save["source_sketch"]["geometry"]
        target_geometry_before_save = {
            role: inspect_current_target(pre_save_part, role, "pre-Save Copy " + role)
            for role in target_roles
        }
        report["source_resolution_before_save_copy"] = {
            "current_source_ids": source_ids,
            "source_journal_ids_by_source_id": latest_source_ids_by_source_id,
            "body_journal_ids_by_source_id": latest_body_ids_by_source_id,
            "source_sketch_id": source_before_save["source_sketch"]["sketch_id"],
        }

        model_root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
        roundtrip_path = model_root / f"F10-mcp-{run_id}-roundtrip.prt"
        if roundtrip_path.exists() or _canonical_path(roundtrip_path) == _canonical_path(part_path):
            raise FileExistsError("F10 unique Save Copy path already exists or collides with the source")
        save_case = case(
            "F10_save_reopen_roundtrip",
            {"target_path": str(roundtrip_path), "reopen": True},
            "On a new fixture repeat both verified association cases, then Save Copy to a new path, reopen, and directly inspect both target sketches with the same journal IDs and geometry counts.",
        )
        save_request = {
            "operation_id": "f10-save-" + uuid.uuid4().hex,
            **credentials,
            "target_path": str(roundtrip_path),
            "reopen": True,
        }
        unknown_write_outcome = True
        saved, save_entry = call_raw("nx_save_copy", save_request, allow_tool_error=True)
        save_state = _status_state(saved)
        save_query = None
        if save_entry.get("is_error") is True and save_state is None:
            save_code = _error_code(saved)
            if save_code in AMBIGUOUS_WRITE_CODES:
                saved, _ = call_raw("nx_get_operation", {"operation_id": save_request["operation_id"]},
                                    allow_tool_error=True)
                save_query = saved
                save_state = _status_state(saved)
                if save_state not in {"SUCCEEDED", "FAILED", "CANCELLED"}:
                    raise RuntimeError("F10 Save Copy outcome remains UNKNOWN after same-ID operation query; do not retry")
            else:
                unknown_write_outcome = False
                raise AssertionError(f"nx_save_copy was rejected before returning an operation record ({save_code or 'UNKNOWN'})")
        elif save_state is not None:
            saved, save_query = wait_for_operation(saved, save_request["operation_id"], "F10 Save Copy")
            save_state = _status_state(saved)
            if save_state == "UNKNOWN":
                if save_query is None:
                    saved, _ = call_raw("nx_get_operation", {"operation_id": save_request["operation_id"]},
                                        allow_tool_error=True)
                    save_query = saved
                    save_state = _status_state(saved)
                if save_state not in {"SUCCEEDED", "FAILED", "CANCELLED"}:
                    raise RuntimeError("F10 Save Copy remains UNKNOWN after same-ID operation query; do not retry")
        if save_state not in (None, "SUCCEEDED"):
            unknown_write_outcome = False
            raise AssertionError(f"F10 Save Copy operation ended in {save_state}")
        save_result = _operation_result(saved)
        if not isinstance(save_result, dict):
            save_result = saved
        if save_result.get("saved") is not True or save_result.get("reopened") is not True:
            unknown_write_outcome = False
            raise AssertionError("nx_save_copy did not confirm saved=true and reopened=true")
        save_checks = save_result.get("checks")
        required_save_checks = {"associations", "expression", "geometry", "history"}
        if (not isinstance(save_checks, dict) or set(save_checks) != required_save_checks
                or any(not isinstance(item, dict) or item.get("status") != "VERIFIED"
                       for item in save_checks.values())):
            raise AssertionError("nx_save_copy omitted a VERIFIED association/expression/geometry/history check")
        new_owner = save_result.get("new_ownership_id")
        new_epoch = save_result.get("new_binding_epoch")
        if (not isinstance(new_owner, str) or not new_owner or isinstance(new_epoch, bool)
                or not isinstance(new_epoch, int) or new_epoch < 1):
            raise AssertionError("nx_save_copy omitted rotated ownership credentials")
        unknown_write_outcome = False
        credentials.update(ownership_id=new_owner, binding_epoch=new_epoch)
        saved_path = save_result.get("saved_path") or str(roundtrip_path)
        if _canonical_path(saved_path) != _canonical_path(roundtrip_path):
            raise AssertionError("nx_save_copy returned a different path from the unique roundtrip path")
        report["saved_path"] = str(roundtrip_path)
        save_case["save_copy"] = _redact(save_result)
        reopen_session = current_session(roundtrip_path, "after F10 Save Copy reopen")
        if str(reopen_session.get("part_units", "")).casefold() not in {"millimeter", "millimeters", "mm"}:
            raise AssertionError("reopened F10 copy is not in millimeter units")
        reopened_part = inspect_part_snapshot("reopened F10 Save Copy")
        reopened_identity = reopened_part.get("identity") if isinstance(reopened_part.get("identity"), dict) else {}
        reopened_record = reopened_part.get("part") if isinstance(reopened_part.get("part"), dict) else {}
        reopened_inspected_path = reopened_record.get("path") or reopened_identity.get("part_path")
        if not isinstance(reopened_inspected_path, str) or _canonical_path(reopened_inspected_path) != _canonical_path(roundtrip_path):
            raise AssertionError("reopened nx_inspect_part path differs from the roundtrip copy")
        reopened_source = resolve_source_sketch(reopened_part, fixture)
        reopened_source_sketch = inspect_sketch(reopened_source["sketch_id"], "reopened source Sketch")
        source_roundtrip_mapping = preserved_geometry_mapping(
            source_geometry_before_save, reopened_source_sketch.get("geometry", []),
            "source Sketch Save Copy/reopen geometry conservation")
        if (source_roundtrip_mapping["new_count"]
                or source_roundtrip_mapping["preserved_count"] != len(source_geometry_before_save)):
            raise AssertionError("Save Copy/reopen did not preserve the source Sketch geometry one-to-one")
        reopened_curve_ids_by_tag = {
            item["fixture_source_tag"]: item["current_journal_id"]
            for item in reopened_source["mapping"]
        }
        reopened_target_results = {}
        for role in target_roles:
            reopened_target = inspect_current_target(reopened_part, role, "reopened " + role + " target")
            old_geometry_mapping = preserved_geometry_mapping(
                [], reopened_target.get("geometry", []), "reopened " + role + " target outputs",
            )
            expected = expected_by_role.get(role)
            if not isinstance(expected, list):
                raise AssertionError(f"missing independent expected geometry for reopened {role} target")
            unverified_edges = sum(1 for item in fixture["source_records"]
                                   if item["numeric_primitive"] is None)
            numeric_match = match_projection_records(old_geometry_mapping["new_geometry"], expected,
                                                     unverified_edges, "reopened " + role + " projection")
            reopened_target_results[role] = {
                "reopened_sketch_id": target_current_ids[role],
                "frame_matched_to_fixture": True,
                "numeric_projection_oracle": numeric_match,
                "target_geometry_readback": _redact(reopened_target),
            }

        reopened_edge_evidence = []
        reopened_bodies = reopened_part.get("bodies") if isinstance(reopened_part.get("bodies"), list) else []
        for source in fixture["source_records"]:
            if source["source_class"] != "edge":
                continue
            old_current_id = (latest_source_ids_by_source_id or {}).get(source["source_curve_id"])
            reopened_edge_evidence.append({
                "live_source_tag_before_save_copy": (current_live_tags_by_source_id or {}).get(source["source_curve_id"]),
                "fixture_tag_provenance": source["tag"],
                "source_journal_id_before_save_copy": old_current_id,
                "numeric_primitive_available": source["numeric_primitive"] is not None,
                "reopened_body_edge_inventory_readable": any(
                    isinstance(body, dict) and isinstance(body.get("edge_ids"), list)
                    for body in reopened_bodies
                ),
                "source_tag_or_geometry_resolution_after_reopen": "NOT_EXPOSED_BY_CURRENT_MCP",
                "relation_persistence_gap": (
                    "The reopened MCP inspection exposes solid Body edge_ids but not Edge Tags or direct Edge geometry; "
                    "the native Save Copy checks cover relation/history persistence, but this smoke cannot independently "
                    "re-identify this selected Edge by stable Tag after reopen."
                ),
            })
        report["source_conservation_after_reopen"] = {
            "source_sketch_id_before": source_before_save["source_sketch"]["sketch_id"],
            "source_sketch_id_after": reopened_source["sketch_id"],
            "source_sketch_journal_id_changed": source_before_save["source_sketch"]["sketch_id"] != reopened_source["sketch_id"],
            "source_curve_ids_after_reopen_by_geometry": reopened_curve_ids_by_tag,
            "source_sketch_geometry_mapping": source_roundtrip_mapping["mapping"],
            "source_sketch_geometry_preserved": True,
            "solid_edge_sources": reopened_edge_evidence,
            "edge_relation_persistence_gap_reported": bool(reopened_edge_evidence),
        }
        save_case.update(state="PASS", saved_path=str(roundtrip_path), reopened=True,
                         source_sketch_geometry_mapping=source_roundtrip_mapping["mapping"],
                         target_projection_oracles=reopened_target_results,
                         edge_relation_persistence_gap_reported=bool(reopened_edge_evidence))
        report.update(saved_path=str(roundtrip_path), roundtrip_reopened=True,
                      roundtrip_target_results=reopened_target_results)
        final_source_stat = part_path.stat()
        before_source_stat = report["source_part_file"]
        if (final_source_stat.st_size != before_source_stat["size_bytes"]
                or datetime.datetime.fromtimestamp(final_source_stat.st_mtime, datetime.timezone.utc).isoformat()
                != before_source_stat["modified_time_utc"]):
            raise AssertionError("supplied source fixture file changed during smoke")
        report["source_part_file_unchanged"] = True
        persist()

        ownership = return_ownership()
        if ownership.get("skipped"):
            raise AssertionError("F10 completed but ownership did not return safely")
        report.update(passed=True, diagnostic_only=False,
                      result="installed F10 projection interface and two isolated model smoke cases verified")
        return 0
    except Exception as exc:
        message = f"{type(exc).__name__}: {exc}"
        report["passed"] = False
        report["error"] = message
        generic_retest = (
            "Use a new clean saved millimeter fixture, new evidence path, the same explicit target/source selections, "
            "and repeat the failed prepare/edit once. Require the recorded pass criterion for that case, preserve "
            "the failed report, and confirm the installed bridge is idle and unhalted before returning ownership."
        )
        failure = {
            "case_id": active_case.get("case_id") if isinstance(active_case, dict) else "F10_setup_or_protocol",
            "failure": message,
            "reproduction_steps": active_case.get("reproduction_steps") if isinstance(active_case, dict) else [
                "Confirm the installed MCP and cached NX bridge status without invoking NX readiness or launch logic.",
                "Use a new clean saved millimeter .prt and matching fixture JSON under workspace/v2/evidence.",
                f"Rerun this script in {mode} mode with a new evidence path and repeat {active_case.get('case_id') if isinstance(active_case, dict) else 'the failing setup step'}.",
            ],
            "retest_criterion": active_case.get("retest_criterion") if isinstance(active_case, dict) else generic_retest,
        }
        report["failure_record"] = failure
        if isinstance(active_case, dict):
            active_case.update(state="FAIL", failure=message,
                               reproduction_steps=failure["reproduction_steps"],
                               retest_criterion=failure["retest_criterion"])
        if credentials and client is not None:
            try:
                return_ownership()
            except Exception as return_error:
                report["failure_return_error"] = type(return_error).__name__
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
