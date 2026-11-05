"""Installed MCP acceptance smoke for the locked F13 revolve contract.

The model stage requires a newly saved empty millimeter part created by
``tools/v2/f13-create-mcp-fixture.py`` and opened in the designated idle NX
session. This program starts only the configured local MCP stdio host. It does
not start, close, restart, attach to, or terminate NX. All geometry expectations
come from the independent ``f13_revolve_oracle.py`` module.

The ``--frame-control`` path is offline and consumes only a previously saved
installed ``nx_inspect_sketch`` response; it does not start MCP or call NX.
The ``--definition-control`` path validates saved Execute and fresh Verify
responses and does not start MCP or call NX.
"""
from __future__ import annotations

import argparse
from collections import Counter
import copy
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
from f01_f02_mcp_client import McpStdioClient  # noqa: E402
from f13_revolve_oracle import (  # noqa: E402
    PolygonLoop,
    make_axis_frame,
    revolved_profile_bounds_mm,
    revolved_sheet_area_mm2,
    revolved_solid_volume_mm3,
)

PROTOCOL_VERSION = "2025-11-25"
CONTRACT_VERSION = "0.13.0"
CONTRACT_TOOL_COUNT = 32
OPERATION_TIMEOUT_SECONDS = 180.0
POLL_INTERVAL_SECONDS = 0.2
MCP_RESPONSE_LINE_LIMIT_BYTES = 16 * 1024 * 1024
LENGTH_TOLERANCE_MM = 1.0e-6
ANGLE_TOLERANCE_RAD = 1.0e-7
JSON_NUMBER_TEXT = re.compile(r"-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\Z")
REVOLVE_AXIS_DIRECTION_TOLERANCE_RAD = 1.0e-6
REVOLVE_ANGLE_ENDPOINT_TOLERANCE_DEG = 1.0e-9
REVOLVE_AXIS_PLANE_TOLERANCE_MM = 1.0e-5
REVOLVE_AXIS_PLANE_DIRECTION_TOLERANCE = 1.0e-6
VOLUME_ABSOLUTE_TOLERANCE_MM3 = 1.0e-5
VOLUME_RELATIVE_TOLERANCE = 1.0e-8
SHEET_AREA_TOLERANCE_MM2 = 1.0e-5
VERIFY_MASS_PROPERTY_ACCURACY = 0.999999
VERIFY_FACE_PROPERTY_ACCURACY = 0.999999
LEGACY_INSPECT_MASS_PROPERTY_ACCURACY = 0.99
PRIVATE_KEY_PARTS = ("token", "secret", "authorization", "ownership_id", "credential",
                     "api_key", "access_key", "private_key", "password")
TERMINAL_STATES = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}

AXIS_POINT_MM = [31.0, -17.0, 23.0]
AXIS_DIRECTION_RAW = [1.0, 2.0, 3.0]
RADIAL_SEED = [1.0, 0.0, 0.0]
MAIN_LOOP = PolygonLoop(((10.0, 0.0), (14.0, 0.0), (14.0, 8.0),
                         (10.0, 8.0), (10.0, 0.0)), role="outer", label="main-annulus")
TOOL_LOOP = PolygonLoop(((12.0, 4.0), (16.0, 4.0), (16.0, 12.0),
                         (12.0, 12.0), (12.0, 4.0)), role="outer", label="unite-tool")
INTERSECTION_LOOP = PolygonLoop(((12.0, 4.0), (14.0, 4.0), (14.0, 8.0),
                                 (12.0, 8.0), (12.0, 4.0)), role="outer", label="unite-intersection")


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--ready", action="store_true", help="installed-host discovery/readiness only")
    mode.add_argument("--part-path", type=Path,
                      help="saved F13 fixture .prt inside workspace/v2/models")
    mode.add_argument("--frame-control", action="store_true",
                      help="offline frame check using a saved nx_inspect_sketch response; does not start MCP or call NX")
    mode.add_argument("--definition-control", action="store_true",
                      help="offline direct-definition check using a saved execute response; does not start MCP or call NX")
    parser.add_argument("--output", required=True, type=Path,
                        help="new installed-stage or offline-control JSON evidence path")
    parser.add_argument("--frame-evidence", type=Path, default=ROOT / "workspace" / "v2" / "evidence" /
                        "f13-mcp-installed-20261102-1.json",
                        help="saved installed JSON report used only with --frame-control")
    parser.add_argument("--definition-evidence", type=Path,
                        default=ROOT / "workspace" / "v2" / "evidence" /
                        "f13-mcp-installed-20261102-5.json",
                        help="saved installed Execute/fresh-Verify report used only with --definition-control")
    parser.add_argument("--expected-server-version", default=os.environ.get(
        "NX_SKETCH_MCP_EXPECTED_VERSION", CONTRACT_VERSION),
        help=f"expected installed host version (default: {CONTRACT_VERSION})")
    args = parser.parse_args(argv)
    if args.output.exists():
        parser.error("evidence output must be a new path; failed reports are never overwritten")
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


def _decode_payload(response: dict) -> dict:
    structured = response.get("structuredContent")
    if isinstance(structured, dict):
        return structured
    for item in response.get("content", []):
        if isinstance(item, dict) and isinstance(item.get("text"), str):
            try:
                decoded = json.loads(item["text"])
            except ValueError:
                continue
            if isinstance(decoded, dict):
                return decoded
    return {}


def _error_code(value: object) -> str | None:
    if isinstance(value, dict):
        for key in ("code", "error_code"):
            item = value.get(key)
            if isinstance(item, (str, int)):
                return str(item)
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


def _operation_result(record: dict) -> dict | None:
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


def _finite_number(value: object, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise AssertionError(f"{label} must be a JSON number")
    result = float(value)
    if not math.isfinite(result):
        raise AssertionError(f"{label} must be finite")
    return result


def _vector(value: object, label: str, size: int) -> list[float]:
    if not isinstance(value, (list, tuple)) or len(value) != size:
        raise AssertionError(f"{label} must contain exactly {size} coordinates")
    return [_finite_number(item, f"{label}[{index}]") for index, item in enumerate(value)]


def _numeric_string_vector(value: object, label: str, size: int) -> list[float]:
    if not isinstance(value, (list, tuple)) or len(value) != size:
        raise AssertionError(f"{label} must contain exactly {size} coordinate strings")
    result: list[float] = []
    for index, item in enumerate(value):
        if not isinstance(item, str) or JSON_NUMBER_TEXT.fullmatch(item) is None:
            raise AssertionError(f"{label}[{index}] must be a numeric string")
        try:
            number = float(item)
        except ValueError as exc:
            raise AssertionError(f"{label}[{index}] must be a numeric string") from exc
        if not math.isfinite(number):
            raise AssertionError(f"{label}[{index}] must be finite")
        result.append(number)
    return result


def _close(actual: object, expected: float, *, label: str,
           absolute: float, relative: float = 0.0) -> float:
    value = _finite_number(actual, label)
    threshold = max(absolute, relative * abs(expected))
    if abs(value - expected) > threshold:
        raise AssertionError(f"{label}: expected {expected:.12g} within {threshold:.6g}, got {value:.12g}")
    return value


def _normalize(vector: list[float]) -> list[float]:
    magnitude = math.sqrt(sum(value * value for value in vector))
    if not math.isfinite(magnitude) or magnitude == 0:
        raise ValueError("axis direction must be finite and nonzero")
    return [value / magnitude for value in vector]


def _dot(left: list[float], right: list[float]) -> float:
    return sum(a * b for a, b in zip(left, right))


def _cross(left: list[float], right: list[float]) -> list[float]:
    return [left[1] * right[2] - left[2] * right[1],
            left[2] * right[0] - left[0] * right[2],
            left[0] * right[1] - left[1] * right[0]]


def _flat_bounds(bounds: tuple[tuple[float, float, float], tuple[float, float, float]]) -> list[float]:
    low, high = bounds
    return [*low, *high]


def _assert_bbox(actual: object, expected: list[float], label: str) -> list[float]:
    values = _vector(actual, label, 6)
    for index, wanted in enumerate(expected):
        _close(values[index], wanted, label=f"{label}[{index}]", absolute=LENGTH_TOLERANCE_MM)
    return values


def _assert_sketch_frame(sketch: object, expected_origin_mm: list[float],
                         expected_normal: list[float], expected_x_axis: list[float],
                         label: str) -> dict:
    if not isinstance(sketch, dict):
        raise AssertionError(f"{label} direct Sketch inspection must be an object")
    frame = sketch.get("support_frame")
    if not isinstance(frame, dict):
        raise AssertionError(f"{label} direct Sketch inspection omitted support_frame")

    observed_origin = _vector(frame.get("origin_mm"), label + " support_frame.origin_mm", 3)
    for index, wanted in enumerate(expected_origin_mm):
        _close(observed_origin[index], wanted, label=f"{label} Sketch origin[{index}]",
               absolute=LENGTH_TOLERANCE_MM)

    observed_normal = _normalize(_vector(frame.get("normal_world"),
                                         label + " support_frame.normal_world", 3))
    expected_normal_unit = _normalize(_vector(expected_normal, label + " expected normal", 3))
    normal_error = math.acos(max(-1.0, min(1.0, _dot(observed_normal, expected_normal_unit))))
    if normal_error > ANGLE_TOLERANCE_RAD:
        raise AssertionError(f"{label} direct Sketch support-frame normal differs from the independent frame")

    observed_x_axis = _normalize(_vector(frame.get("x_axis_world"),
                                         label + " support_frame.x_axis_world", 3))
    expected_x_axis_unit = _normalize(_vector(expected_x_axis, label + " expected x_axis", 3))
    x_axis_error = math.acos(max(-1.0, min(1.0, _dot(observed_x_axis, expected_x_axis_unit))))
    if x_axis_error > ANGLE_TOLERANCE_RAD:
        raise AssertionError(f"{label} direct Sketch support-frame x axis differs from the independent frame")

    return {
        "source_field": "sketch.support_frame",
        "origin_mm": observed_origin,
        "normal_world": observed_normal,
        "x_axis_world": observed_x_axis,
        "origin_tolerance_mm": LENGTH_TOLERANCE_MM,
        "direction_tolerance_rad": ANGLE_TOLERANCE_RAD,
        "normal_error_rad": normal_error,
        "x_axis_error_rad": x_axis_error,
    }


def _run_offline_frame_control(evidence_path: Path, output_path: Path,
                               expected_normal: list[float], expected_x_axis: list[float]) -> int:
    if not evidence_path.is_file():
        raise FileNotFoundError(f"saved frame-control report does not exist: {evidence_path}")
    source = json.loads(evidence_path.read_text(encoding="utf-8"))
    if not isinstance(source, dict) or not isinstance(source.get("calls"), list):
        raise AssertionError("saved installed report omitted its calls list")
    inspection_payloads = [
        call.get("payload") for call in source["calls"]
        if isinstance(call, dict) and call.get("tool") == "nx_inspect_sketch" and
        call.get("case_id") == "F13_installed_tilted_solid_section" and isinstance(call.get("payload"), dict)
    ]
    if len(inspection_payloads) != 1:
        raise AssertionError(f"expected one saved four-line sketch inspection response, found {len(inspection_payloads)}")
    sketch = inspection_payloads[0].get("sketch")
    if not isinstance(sketch, dict):
        raise AssertionError("saved nx_inspect_sketch response omitted sketch")
    geometry = sketch.get("geometry")
    if not isinstance(geometry, list) or len(geometry) != 4:
        raise AssertionError("saved four-line sketch response did not contain exactly four geometry records")
    for index, curve in enumerate(geometry):
        if not isinstance(curve, dict) or curve.get("kind") != "LINE" or not isinstance(curve.get("journal_id"), str):
            raise AssertionError(f"saved geometry[{index}] does not match the direct four-line sketch response shape")
        _numeric_string_vector(curve.get("start"), f"saved geometry[{index}].start", 3)
        _numeric_string_vector(curve.get("end"), f"saved geometry[{index}].end", 3)

    positive = _assert_sketch_frame(sketch, AXIS_POINT_MM, expected_normal, expected_x_axis,
                                    "saved four-line Sketch")

    wrong_origin = copy.deepcopy(sketch)
    wrong_origin["support_frame"]["origin_mm"][0] += 1.0
    try:
        _assert_sketch_frame(wrong_origin, AXIS_POINT_MM, expected_normal, expected_x_axis,
                             "wrong-origin control")
    except AssertionError as exc:
        wrong_origin_rejection = str(exc)
    else:
        raise AssertionError("offline frame control accepted a wrong Sketch origin")

    wrong_normal = copy.deepcopy(sketch)
    wrong_normal["support_frame"]["normal_world"] = [0.0, 0.0, 1.0]
    try:
        _assert_sketch_frame(wrong_normal, AXIS_POINT_MM, expected_normal, expected_x_axis,
                             "wrong-direction control")
    except AssertionError as exc:
        wrong_direction_rejection = str(exc)
    else:
        raise AssertionError("offline frame control accepted a wrong Sketch frame direction")

    missing_frame = copy.deepcopy(sketch)
    missing_frame.pop("support_frame", None)
    try:
        _assert_sketch_frame(missing_frame, AXIS_POINT_MM, expected_normal, expected_x_axis,
                             "missing-frame control")
    except AssertionError as exc:
        missing_frame_rejection = str(exc)
    else:
        raise AssertionError("offline frame control silently accepted a missing support_frame")

    result = {
        "test": "F13 installed Sketch frame field control",
        "result": "PASSED",
        "control_mode": "offline saved-response only",
        "nx_or_mcp_calls": 0,
        "source_report_path": str(evidence_path.resolve()),
        "source_report_passed": source.get("passed"),
        "source_case_id": "F13_installed_tilted_solid_section",
        "source_geometry_count": len(geometry),
        "positive_actual_saved_response": positive,
        "wrong_origin_rejected": wrong_origin_rejection,
        "wrong_direction_rejected": wrong_direction_rejection,
        "missing_support_frame_rejected": missing_frame_rejection,
        "locked_tolerances": {"length_mm": LENGTH_TOLERANCE_MM,
                              "direction_rad": ANGLE_TOLERANCE_RAD},
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(result, ensure_ascii=False, allow_nan=False, indent=2) + "\n",
                           encoding="utf-8", newline="\n")
    print(json.dumps({"passed": True, "mode": "offline-frame-control",
                      "source_report": str(evidence_path), "output": str(output_path)},
                     ensure_ascii=False))
    return 0


def _run_offline_definition_control(evidence_path: Path, output_path: Path) -> int:
    if not evidence_path.is_file():
        raise FileNotFoundError(f"saved definition-control report does not exist: {evidence_path}")
    source = json.loads(evidence_path.read_text(encoding="utf-8"))
    if not isinstance(source, dict) or not isinstance(source.get("cases"), list):
        raise AssertionError("saved installed report omitted its cases list")
    matches = [case for case in source["cases"]
               if isinstance(case, dict) and case.get("case_id") == "F13_signed_partial_solid_create"]
    if len(matches) != 1:
        raise AssertionError(f"expected one saved signed-solid execute case, found {len(matches)}")
    case = matches[0]
    request = case.get("prepare_request")
    execution = case.get("execute_result")
    if not isinstance(request, dict) or not isinstance(execution, dict):
        raise AssertionError("saved signed-solid case omitted prepare_request or execute_result")
    definition = execution.get("revolve_definition")
    if not isinstance(definition, dict):
        raise AssertionError("saved execute_result omitted revolve_definition")
    if execution.get("mutation_state") != "APPLIED":
        raise AssertionError("saved execute_result was not APPLIED")
    if not isinstance(request.get("axis_point_mm"), list):
        raise AssertionError("saved prepare_request omitted numeric axis_point_mm")
    requested_point = _vector(request["axis_point_mm"], "saved prepare_request.axis_point_mm", 3)
    actual_point = _numeric_string_vector(definition.get("axis_point_mm"),
                                          "saved execute_result.revolve_definition.axis_point_mm", 3)
    validation = _assert_direct_definition(definition, request, "saved APPLIED execute definition",
                                           expected_feature_id=execution.get("feature_id"))

    verified = case.get("verify_result")
    if not isinstance(verified, dict):
        raise AssertionError("saved run5 case omitted verify_result")
    _assert_verified(verified, "saved run5 signed-solid case")
    if verified.get("execute_operation_id") != case.get("execute_operation_id"):
        raise AssertionError("saved fresh Verify refers to another execute operation")
    if verified.get("plan_id") != execution.get("plan_id"):
        raise AssertionError("saved fresh Verify refers to another plan")
    verification_evidence = verified.get("evidence")
    if not isinstance(verification_evidence, dict):
        raise AssertionError("saved fresh Verify omitted direct evidence")
    if verification_evidence.get("feature_id") != execution.get("feature_id"):
        raise AssertionError("saved fresh Verify feature provenance differs from Execute")
    fresh_definition = _verified_revolve_definition(verified, "saved run5 fresh Verify")
    fresh_validation = _assert_direct_definition(
        fresh_definition, request, "saved run5 fresh Verify definition",
        expected_feature_id=verification_evidence.get("feature_id"))

    execution_body_ids = execution.get("body_ids")
    verified_body_ids = verification_evidence.get("body_ids")
    verified_bodies = verification_evidence.get("bodies")
    if not isinstance(execution_body_ids, list) or not execution_body_ids:
        raise AssertionError("saved APPLIED execute result omitted body_ids")
    if not isinstance(verified_body_ids, list) or set(verified_body_ids) != set(execution_body_ids):
        raise AssertionError("saved fresh Verify body_ids differ from Execute")
    if not isinstance(verified_bodies, list):
        raise AssertionError("saved fresh Verify evidence omitted body records")
    body_map = {body.get("journal_id"): body for body in verified_bodies
                if isinstance(body, dict) and isinstance(body.get("journal_id"), str)}
    if len(body_map) != len(verified_bodies) or set(body_map) != set(verified_body_ids):
        raise AssertionError("saved fresh Verify body records do not map one-to-one to body_ids")
    body_id = execution_body_ids[0]
    verified_body = body_map.get(body_id)
    if not isinstance(verified_body, dict) or verified_body.get("is_solid") is not True:
        raise AssertionError("saved fresh Verify omitted the expected solid body record")
    if verified_body.get("measurement_status") != "MEASURED":
        raise AssertionError("saved fresh Verify body measurement is not MEASURED")
    expected = case.get("independent_expected")
    if not isinstance(expected, dict):
        raise AssertionError("saved case omitted its independent expected volume/bounds")
    expected_volume = _finite_number(expected.get("expected_volume_mm3"),
                                     "saved independent expected_volume_mm3")
    expected_box = _vector(expected.get("expected_bbox_mm"), "saved independent expected_bbox_mm", 6)
    fresh_volume = _close(verified_body.get("volume_mm3"), expected_volume,
                          label="saved fresh Verify body volume",
                          absolute=VOLUME_ABSOLUTE_TOLERANCE_MM3,
                          relative=VOLUME_RELATIVE_TOLERANCE)
    fresh_bbox = _assert_bbox(verified_body.get("bbox_mm"), expected_box,
                              "saved fresh Verify body bbox")

    wrong_nested = copy.deepcopy(verified)
    wrong_geometry_evidence = wrong_nested["checks"]["geometry"]["evidence"]
    moved_definition = wrong_geometry_evidence.pop("revolve_definition")
    wrong_geometry_evidence["unexpected_nested_wrapper"] = {"revolve_definition": moved_definition}
    try:
        _verified_revolve_definition(wrong_nested, "wrong nested wrapper control")
    except AssertionError as exc:
        wrong_nested_rejection = str(exc)
    else:
        raise AssertionError("fresh Verify definition helper accepted an unexpected nested wrapper")

    bool_number_mismatch = copy.deepcopy(verified)
    geometry_definition_copy = bool_number_mismatch["checks"]["geometry"]["evidence"]["revolve_definition"]
    if geometry_definition_copy.get("axis_point_matches") is not True:
        raise AssertionError("run5 positive response did not contain the expected boolean axis_point_matches field")
    geometry_definition_copy["axis_point_matches"] = 1
    try:
        _verified_revolve_definition(bool_number_mismatch, "bool/number mismatch control")
    except AssertionError as exc:
        bool_number_mismatch_rejection = str(exc)
    else:
        raise AssertionError("fresh Verify definition comparison treated JSON true as numeric 1")

    missing_field = copy.deepcopy(verified)
    missing_field["checks"]["revolve_definition"]["evidence"].pop("axis_point_mm", None)
    missing_field["checks"]["geometry"]["evidence"]["revolve_definition"].pop("axis_point_mm", None)
    try:
        missing_definition = _verified_revolve_definition(missing_field, "missing definition field control")
        _assert_direct_definition(missing_definition, request, "missing definition field control",
                                  expected_feature_id=verification_evidence.get("feature_id"))
    except AssertionError as exc:
        missing_field_rejection = str(exc)
    else:
        raise AssertionError("fresh Verify definition helper accepted a missing required axis_point_mm field")

    bad_numeric_text = copy.deepcopy(definition)
    bad_numeric_text["axis_point_mm"][1] = "NaN"
    try:
        _assert_direct_definition(bad_numeric_text, request, "bad numeric-text control",
                                  expected_feature_id=execution.get("feature_id"))
    except AssertionError as exc:
        bad_numeric_text_rejection = str(exc)
    else:
        raise AssertionError("definition control accepted a non-finite native axis-point string")

    wrong_point = copy.deepcopy(definition)
    wrong_point["axis_point_mm"][0] = format(actual_point[0] + 1.0, ".17g")
    try:
        _assert_direct_definition(wrong_point, request, "wrong native point control",
                                  expected_feature_id=execution.get("feature_id"))
    except AssertionError as exc:
        wrong_point_rejection = str(exc)
    else:
        raise AssertionError("definition control accepted a wrong native axis point")

    string_request = copy.deepcopy(request)
    string_request["axis_point_mm"][0] = str(requested_point[0])
    try:
        _assert_direct_definition(definition, string_request, "request-number strictness control",
                                  expected_feature_id=execution.get("feature_id"))
    except AssertionError as exc:
        string_request_rejection = str(exc)
    else:
        raise AssertionError("definition control accepted a string in the numeric request axis_point_mm")

    result = {
        "test": "F13 installed direct Revolve definition numeric-field control",
        "result": "PASSED",
        "control_mode": "offline saved-response only",
        "nx_or_mcp_calls": 0,
        "source_report_path": str(evidence_path.resolve()),
        "source_report_passed": source.get("passed"),
        "source_case_id": case.get("case_id"),
        "source_case_state": case.get("state"),
        "execute_mutation_state": execution.get("mutation_state"),
        "request_axis_point_json_numbers": requested_point,
        "native_response_axis_point_raw": definition["axis_point_mm"],
        "native_response_axis_point_parsed_mm": actual_point,
        "positive_actual_saved_response": validation,
        "fresh_verify_direct_definition": fresh_validation,
        "fresh_verify_provenance": {
            "execute_operation_id": verified.get("execute_operation_id"),
            "plan_id": verified.get("plan_id"),
            "feature_id": verification_evidence.get("feature_id"),
            "body_ids": verified_body_ids,
            "volume_mm3": fresh_volume,
            "bbox_mm": fresh_bbox,
        },
        "wrong_nested_wrapper_rejected": wrong_nested_rejection,
        "bool_number_mismatch_rejected": bool_number_mismatch_rejection,
        "missing_required_definition_field_rejected": missing_field_rejection,
        "bad_numeric_string_rejected": bad_numeric_text_rejection,
        "wrong_native_point_rejected": wrong_point_rejection,
        "string_request_point_rejected": string_request_rejection,
        "axis_point_tolerance_mm": LENGTH_TOLERANCE_MM,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(result, ensure_ascii=False, allow_nan=False, indent=2) + "\n",
                           encoding="utf-8", newline="\n")
    print(json.dumps({"passed": True, "mode": "offline-definition-control",
                      "source_report": str(evidence_path), "output": str(output_path)},
                     ensure_ascii=False))
    return 0


def _tool_contract(tools: list) -> dict:
    item = next((tool for tool in tools if isinstance(tool, dict)
                 and tool.get("name") == "nx_prepare_revolve"), None)
    if not isinstance(item, dict):
        raise AssertionError("installed tools/list omitted nx_prepare_revolve")
    schema = item.get("inputSchema")
    if not isinstance(schema, dict):
        raise AssertionError("nx_prepare_revolve omitted inputSchema")
    properties = schema.get("properties")
    if not isinstance(properties, dict):
        raise AssertionError("nx_prepare_revolve schema omitted properties")
    required = set(schema.get("required", []))
    expected_required = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                         "mode", "axis_point_mm", "axis_direction", "start_angle_deg", "end_angle_deg"}
    if required != expected_required:
        raise AssertionError(f"nx_prepare_revolve required fields changed: {sorted(required)}")
    expected_properties = expected_required | {"profile_output", "target_body_id"}
    if set(properties) != expected_properties:
        raise AssertionError(f"nx_prepare_revolve properties changed: {sorted(properties)}")
    if schema.get("additionalProperties") is not False:
        raise AssertionError("nx_prepare_revolve must reject unsupported fields")
    if set(properties["mode"].get("enum", [])) != {"create", "unite", "subtract"}:
        raise AssertionError("nx_prepare_revolve mode enum differs from locked contract")
    if set(properties["profile_output"].get("enum", [])) != {"solid", "sheet"}:
        raise AssertionError("nx_prepare_revolve profile_output enum differs from locked contract")
    if "default" not in properties["profile_output"] or properties["profile_output"]["default"] != "solid":
        raise AssertionError("nx_prepare_revolve profile_output default must be solid")
    if item.get("annotations", {}).get("readOnlyHint") is not True:
        raise AssertionError("nx_prepare_revolve must advertise a read-only prepare")
    if not schema.get("allOf"):
        raise AssertionError("nx_prepare_revolve schema omitted conditional create/Boolean/sheet rules")
    description = item.get("description", "")
    for phrase in ("right-hand-rule", "[-360, 360]", "explicit solid target", "fresh native readback"):
        if phrase not in description:
            raise AssertionError(f"nx_prepare_revolve description omitted locked behavior {phrase!r}")
    return {"name": item.get("name"), "title": item.get("title"), "description": description,
            "inputSchema": schema, "annotations": item.get("annotations")}


def _optional_face_tag_contract(tools: list) -> dict:
    """Record installed Sketch/Datum schemas exposing optional explicit-face Tag fields."""
    contracts: dict[str, dict] = {}
    for name in ("nx_prepare_sketch", "nx_prepare_datum_plane"):
        item = next((tool for tool in tools if isinstance(tool, dict) and tool.get("name") == name), None)
        if not isinstance(item, dict):
            raise AssertionError(f"installed tools/list omitted {name}")
        schema = item.get("inputSchema")
        properties = schema.get("properties") if isinstance(schema, dict) else None
        if not isinstance(properties, dict):
            raise AssertionError(f"{name} omitted inputSchema.properties")
        face_tag = properties.get("support_face_tag")
        if not isinstance(face_tag, dict) or face_tag.get("type") != "string":
            raise AssertionError(f"{name} does not expose support_face_tag as a string")
        if int(face_tag.get("minLength", 0)) < 1:
            raise AssertionError(f"{name}.support_face_tag must reject empty strings")
        if "support_face_tag" in set(schema.get("required", [])):
            raise AssertionError(f"{name}.support_face_tag must remain optional")
        one_of = schema.get("oneOf", [])
        if isinstance(one_of, list) and any(
                isinstance(mode, dict) and "support_face_tag" in set(mode.get("required", []))
                for mode in one_of):
            raise AssertionError(f"{name}.support_face_tag must not be conditionally required")
        face_reference_modes = [mode for mode in one_of if isinstance(mode, dict)
                                and "support_face_id" in set(mode.get("required", []))]
        if not face_reference_modes:
            raise AssertionError(f"{name} schema has no explicit support_face_id mode for support_face_tag")
        for mode in one_of:
            if not isinstance(mode, dict):
                continue
            required_mode = set(mode.get("required", []))
            not_rule = mode.get("not")
            forbidden_modes = not_rule.get("anyOf", []) if isinstance(not_rule, dict) else []
            tag_forbidden = {"required": ["support_face_tag"]} in forbidden_modes
            if "support_face_id" in required_mode and tag_forbidden:
                raise AssertionError(f"{name} explicit-face schema incorrectly forbids support_face_tag")
            if "support_face_id" not in required_mode and not tag_forbidden:
                raise AssertionError(f"{name} schema allows support_face_tag outside explicit support_face_id mode")
        if schema.get("additionalProperties") is not False:
            raise AssertionError(f"{name} must reject unsupported input properties")
        contracts[name] = {
            "name": name,
            "support_face_tag_schema": face_tag,
            "optional": True,
            "additional_properties_rejected": True,
        }
    return contracts


def _inspect_path(inspection: dict) -> Path:
    part = inspection.get("part")
    identity = inspection.get("identity")
    part_path = part.get("path") if isinstance(part, dict) else None
    identity_path = identity.get("part_path") if isinstance(identity, dict) else None
    if not isinstance(part_path, str) or not isinstance(identity_path, str):
        raise AssertionError("nx_inspect_part omitted direct part and identity paths")
    actual = Path(part_path).resolve()
    if actual.as_posix().casefold() != Path(identity_path).resolve().as_posix().casefold():
        raise AssertionError("nx_inspect_part part and identity paths disagree")
    return actual


def _body_records(inspection: dict) -> list[dict]:
    records = inspection.get("bodies")
    if not isinstance(records, list):
        raise AssertionError("nx_inspect_part omitted the direct body inventory")
    return [item for item in records if isinstance(item, dict)]


def _body_face_identity_signature(inspection: dict, body_id: str) -> dict:
    matches = [body for body in _body_records(inspection) if body.get("journal_id") == body_id]
    if len(matches) != 1:
        raise AssertionError(f"nx_inspect_part returned {len(matches)} bodies for face identity {body_id!r}")
    body = matches[0]
    return {"face_ids": body.get("face_ids"), "face_refs": body.get("face_refs")}


def _assert_face_tag_inventory(query_result: dict, inspection: dict, body_id: str) -> dict:
    """Check actual query face Tags against the same body's preserved face_refs."""
    matching_bodies = [body for body in _body_records(inspection)
                       if body.get("journal_id") == body_id]
    if len(matching_bodies) != 1:
        raise AssertionError(f"nx_inspect_part returned {len(matching_bodies)} records for body {body_id!r}")
    body = matching_bodies[0]
    face_ids = body.get("face_ids")
    face_refs = body.get("face_refs")
    if not isinstance(face_ids, list) or any(not isinstance(face_id, str) for face_id in face_ids):
        raise AssertionError(f"body {body_id!r} omitted its ordered string face_ids")
    if not isinstance(face_refs, list) or any(not isinstance(face_ref, dict) for face_ref in face_refs):
        raise AssertionError(f"body {body_id!r} omitted its per-face face_refs records")
    if len(face_ids) != len(face_refs):
        raise AssertionError(f"body {body_id!r} face_ids/face_refs counts differ: {len(face_ids)} vs {len(face_refs)}")

    reference_ids: list[str] = []
    reference_pairs: list[tuple[str, str]] = []
    face_tags: list[str] = []
    body_tags: list[str] = []
    for index, face_ref in enumerate(face_refs):
        face_journal_id = face_ref.get("face_journal_id")
        face_tag = face_ref.get("face_tag")
        face_body_id = face_ref.get("body_id")
        face_body_tag = face_ref.get("body_tag")
        if not isinstance(face_journal_id, str) or not face_journal_id:
            raise AssertionError(f"body {body_id!r} face_refs[{index}] omitted face_journal_id")
        if not isinstance(face_tag, str) or not face_tag:
            raise AssertionError(f"body {body_id!r} face_refs[{index}] omitted current-session face_tag")
        if face_body_id != body_id:
            raise AssertionError(f"body {body_id!r} face_refs[{index}] body_id is {face_body_id!r}")
        if not isinstance(face_body_tag, str) or not face_body_tag:
            raise AssertionError(f"body {body_id!r} face_refs[{index}] omitted body_tag")
        reference_ids.append(face_journal_id)
        reference_pairs.append((face_journal_id, face_tag))
        face_tags.append(face_tag)
        body_tags.append(face_body_tag)

    if face_ids != reference_ids:
        raise AssertionError(f"body {body_id!r} face_ids order/multiset differs from face_refs journal IDs")
    if Counter(face_ids) != Counter(reference_ids):
        raise AssertionError(f"body {body_id!r} face_ids multiset differs from face_refs")
    if reference_pairs != sorted(reference_pairs):
        raise AssertionError(f"body {body_id!r} face_refs are not ordered by JID then Tag")
    if len(face_tags) != len(set(face_tags)):
        raise AssertionError(f"body {body_id!r} contains a duplicate current-session face_tag")
    if len(set(body_tags)) > 1:
        raise AssertionError(f"body {body_id!r} face_refs disagree on body_tag")

    candidates = query_result.get("candidates")
    if not isinstance(candidates, list) or any(not isinstance(candidate, dict) for candidate in candidates):
        raise AssertionError("nx_query_faces omitted its face candidate list")
    if query_result.get("candidate_count") != len(candidates):
        raise AssertionError("nx_query_faces candidate_count differs from the returned candidates length")
    if not candidates:
        raise AssertionError(f"nx_query_faces returned no planar face candidates for Boolean body {body_id!r}")
    if query_result.get("selection_status") != "all":
        raise AssertionError(f"nx_query_faces all_planar status is {query_result.get('selection_status')!r}")

    reference_set = {(face_ref["face_journal_id"], face_ref["face_tag"], face_ref["body_id"])
                     for face_ref in face_refs}
    query_pairs: list[tuple[str, str]] = []
    for index, candidate in enumerate(candidates):
        face_journal_id = candidate.get("face_journal_id")
        face_tag = candidate.get("face_tag")
        candidate_body_id = candidate.get("body_id")
        if candidate.get("surface_type") != "planar":
            raise AssertionError(f"nx_query_faces candidate {index} is not planar")
        if not isinstance(face_journal_id, str) or not isinstance(face_tag, str) or not face_tag:
            raise AssertionError(f"nx_query_faces candidate {index} omitted face_journal_id/face_tag")
        if candidate_body_id != body_id:
            raise AssertionError(f"nx_query_faces candidate {index} escaped body scope: {candidate_body_id!r}")
        if (face_journal_id, face_tag, candidate_body_id) not in reference_set:
            raise AssertionError(f"nx_query_faces candidate {index} does not map to body.face_refs")
        query_pairs.append((face_journal_id, face_tag))

    duplicate_journal_ids = sorted(face_id for face_id, count in Counter(face_ids).items() if count > 1)
    return {
        "body_id": body_id,
        "body_tag": body_tags[0] if body_tags else None,
        "face_ids_count": len(face_ids),
        "face_refs_count": len(face_refs),
        "face_ids_match_face_refs_order_and_multiset": True,
        "face_refs_ordered_by_jid_then_tag": True,
        "body_face_tags_unique": True,
        "query_candidate_count": len(candidates),
        "query_candidates_map_to_face_refs": True,
        "same_body_duplicate_face_journal_ids": duplicate_journal_ids,
        "query_candidates": query_pairs,
        "interpretation": "Real installed nx_query_faces and nx_inspect_part records agree by current-session face Tag; tags are session-scoped and are not used as SaveCopy/reopen identities.",
    }


def _fingerprint(body: dict) -> dict:
    box = body.get("bbox_mm")
    return {
        "journal_id": body.get("journal_id"),
        "kind": body.get("kind"),
        "is_solid": body.get("is_solid"),
        "bbox_mm": list(box) if isinstance(box, (list, tuple)) else None,
        "face_count": len(body.get("face_ids", [])) if isinstance(body.get("face_ids"), list) else None,
        "edge_count": len(body.get("edge_ids", [])) if isinstance(body.get("edge_ids"), list) else None,
        "measurement_status": body.get("measurement_status"),
        "legacy_inspect_measurements_diagnostic_only": {
            "volume_mm3": body.get("volume_mm3"),
            "surface_area_mm2": body.get("surface_area_mm2"),
            "volume_mass_property_accuracy": LEGACY_INSPECT_MASS_PROPERTY_ACCURACY,
            "surface_area_accuracy": None,
            "oracle_comparison_eligible": False,
        },
    }


def _structural_body_signature(body: dict) -> dict:
    result = _fingerprint(body)
    result.pop("legacy_inspect_measurements_diagnostic_only", None)
    return result


def _assert_roundtrip(before: list[dict], after: list[dict]) -> None:
    if len(before) != len(after):
        raise AssertionError("SaveCopy/reopen changed the direct body count")
    unmatched = list(after)
    for expected in before:
        match = None
        for index, actual in enumerate(unmatched):
            if expected.get("kind") != actual.get("kind") or expected.get("is_solid") != actual.get("is_solid"):
                continue
            if expected.get("face_count") != actual.get("face_count") or expected.get("edge_count") != actual.get("edge_count"):
                continue
            box_expected, box_actual = expected.get("bbox_mm"), actual.get("bbox_mm")
            if not isinstance(box_expected, list) or not isinstance(box_actual, list):
                continue
            try:
                _assert_bbox(box_actual, box_expected, "SaveCopy body bounds")
            except (AssertionError, TypeError, ValueError):
                continue
            match = index
            break
        if match is None:
            raise AssertionError("SaveCopy/reopen body did not map one-to-one by type, topology, and bounds")
        unmatched.pop(match)
    if unmatched:
        raise AssertionError("SaveCopy/reopen left unmatched body signatures")


def _find_dict(value: object, key: str) -> dict | None:
    if isinstance(value, dict):
        candidate = value.get(key)
        if isinstance(candidate, dict):
            return candidate
        for child in value.values():
            result = _find_dict(child, key)
            if result is not None:
                return result
    elif isinstance(value, list):
        for child in value:
            result = _find_dict(child, key)
            if result is not None:
                return result
    return None


def _measurement_accuracy_fields(value: object) -> list[dict]:
    recognized = {"volume_measurement_accuracy", "mass_property_accuracy", "mass_properties_accuracy",
                  "face_measurement_accuracy", "face_property_accuracy", "face_properties_accuracy",
                  "area_measurement_accuracy", "surface_area_measurement_accuracy"}
    found: list[dict] = []
    if isinstance(value, dict):
        for key, child in value.items():
            if str(key).casefold() in recognized and isinstance(child, (int, float)) and not isinstance(child, bool):
                found.append({"field": key, "value": float(child)})
            found.extend(_measurement_accuracy_fields(child))
    elif isinstance(value, list):
        for child in value:
            found.extend(_measurement_accuracy_fields(child))
    return found


def _assert_profile_selection(source: dict, expected_curve_count: int, label: str,
                              *, sheet: bool = False) -> dict:
    profile = _find_dict(source, "profile_analysis")
    if not isinstance(profile, dict):
        raise AssertionError(f"{label} omitted direct profile_analysis and selected-curve counts")
    expected_counts = {"source_curve_count": expected_curve_count,
                       "input_curve_count": expected_curve_count,
                       "excluded_reference_curve_count": 0}
    if sheet:
        # section_loop_count follows the native Section loop count, which includes
        # an open Section chain. Closed-profile exclusion is independently gated
        # by valid_closed_component_count and the outer/inner loop counts.
        expected_counts.update(open_component_count=1, valid_closed_component_count=0,
                               outer_loop_count=0, inner_loop_count=0, section_loop_count=1)
    else:
        expected_counts.update(open_component_count=0, valid_closed_component_count=1,
                               outer_loop_count=1, inner_loop_count=0, section_loop_count=1)
    for key, expected in expected_counts.items():
        if profile.get(key) != expected:
            raise AssertionError(f"{label} profile_analysis.{key}: expected {expected}, got {profile.get(key)!r}")
    return profile


def _assert_verified(result: dict, label: str) -> None:
    if result.get("verified") is not True:
        raise AssertionError(f"{label} fresh nx_verify_part did not report verified=true")
    checks = result.get("checks")
    if not isinstance(checks, dict) or not checks:
        raise AssertionError(f"{label} fresh nx_verify_part omitted its check records")
    failures = {name: check for name, check in checks.items()
                if not isinstance(check, dict) or check.get("status") != "VERIFIED"}
    if failures:
        raise AssertionError(f"{label} fresh nx_verify_part has failed/unavailable checks: {failures}")


def _json_values_semantically_equal(left: object, right: object) -> bool:
    if isinstance(left, bool) or isinstance(right, bool):
        return isinstance(left, bool) and isinstance(right, bool) and left is right
    if isinstance(left, (int, float)) or isinstance(right, (int, float)):
        if (not isinstance(left, (int, float)) or isinstance(left, bool) or
                not isinstance(right, (int, float)) or isinstance(right, bool)):
            return False
        try:
            if not math.isfinite(float(left)) or not math.isfinite(float(right)):
                return False
        except (OverflowError, TypeError, ValueError):
            return False
        return left == right
    if left is None or right is None:
        return left is None and right is None
    if isinstance(left, str) or isinstance(right, str):
        return isinstance(left, str) and isinstance(right, str) and left == right
    if isinstance(left, dict) or isinstance(right, dict):
        if not isinstance(left, dict) or not isinstance(right, dict) or left.keys() != right.keys():
            return False
        return all(_json_values_semantically_equal(left[key], right[key]) for key in left)
    if isinstance(left, list) or isinstance(right, list):
        if not isinstance(left, list) or not isinstance(right, list) or len(left) != len(right):
            return False
        return all(_json_values_semantically_equal(a, b) for a, b in zip(left, right))
    return False


def _verified_revolve_definition(verified: object, label: str) -> dict:
    """Read the two explicit Verify check paths and require their definitions to agree."""
    if not isinstance(verified, dict):
        raise AssertionError(f"{label} Verify result must be an object")
    checks = verified.get("checks")
    if not isinstance(checks, dict):
        raise AssertionError(f"{label} Verify result omitted checks")

    revolve_check = checks.get("revolve_definition")
    if not isinstance(revolve_check, dict) or revolve_check.get("status") != "VERIFIED":
        raise AssertionError(f"{label} checks.revolve_definition wrapper is missing or not VERIFIED")
    revolve_definition = revolve_check.get("evidence")
    if not isinstance(revolve_definition, dict):
        raise AssertionError(f"{label} checks.revolve_definition.evidence is missing or not an object")

    geometry_check = checks.get("geometry")
    if not isinstance(geometry_check, dict) or geometry_check.get("status") != "VERIFIED":
        raise AssertionError(f"{label} checks.geometry wrapper is missing or not VERIFIED")
    geometry_evidence = geometry_check.get("evidence")
    if not isinstance(geometry_evidence, dict):
        raise AssertionError(f"{label} checks.geometry.evidence is missing or not an object")
    geometry_definition = geometry_evidence.get("revolve_definition")
    if not isinstance(geometry_definition, dict):
        raise AssertionError(f"{label} checks.geometry.evidence.revolve_definition is missing or not an object")
    if not _json_values_semantically_equal(revolve_definition, geometry_definition):
        raise AssertionError(f"{label} Revolve definitions in the two VERIFIED checks disagree")

    summary_evidence = verified.get("evidence")
    if not isinstance(summary_evidence, dict):
        raise AssertionError(f"{label} Verify result omitted its direct evidence object")
    if "revolve_definition" in summary_evidence:
        summary_definition = summary_evidence["revolve_definition"]
        if (not isinstance(summary_definition, dict) or
                not _json_values_semantically_equal(summary_definition, revolve_definition)):
            raise AssertionError(f"{label} evidence.revolve_definition disagrees with the VERIFIED check definitions")
    return revolve_definition


def _assert_direct_definition(definition: dict | None, request: dict, label: str,
                              *, expected_feature_id: str | None) -> dict:
    """Independently validate the complete direct NX Revolve definition readback."""
    if not isinstance(definition, dict):
        raise AssertionError(f"{label} omitted the direct native revolve_definition")
    required_fields = (
        "status", "feature_id", "native_feature_type", "section_domain",
        "section_domain_matches_prepare", "axis_point_mm", "axis_direction",
        "expected_native_axis_direction", "axis_point_matches", "axis_direction_matches",
        "axis_tag", "axis_point_tag", "axis_direction_tag", "axis_helpers_match_committed_feature",
        "requested_start_angle_deg", "requested_end_angle_deg", "requested_signed_span_deg",
        "expected_native_start_angle_deg", "expected_native_end_angle_deg",
        "actual_native_start_angle_deg", "actual_native_end_angle_deg",
        "start_angle_expression_unit", "end_angle_expression_unit",
        "start_limit_matches_request", "end_limit_matches_request",
        "symmetric_option_is_disabled", "revolve_tolerance_matches", "offset_option_is_disabled",
        "requested_profile_output", "actual_body_type", "body_type_matches",
        "requested_boolean_mode", "actual_boolean_mode", "boolean_mode_matches",
        "target_body_ids", "target_body_matches", "parent_feature_ids",
        "source_sketch_is_feature_parent", "axis_in_sketch_plane", "axis_plane_evidence",
    )
    missing = [key for key in required_fields if key not in definition or definition[key] is None]
    if missing:
        raise AssertionError(f"{label} native revolve_definition omitted required fields: {missing}")
    if definition["status"] != "VERIFIED":
        raise AssertionError(f"{label} native revolve_definition status is {definition['status']!r}")
    if not isinstance(definition["native_feature_type"], str) or not definition["native_feature_type"]:
        raise AssertionError(f"{label} native revolve_definition omitted native_feature_type")
    if not isinstance(expected_feature_id, str) or not expected_feature_id:
        raise AssertionError(f"{label} has no execution feature ID to validate native provenance")
    if definition["feature_id"] != expected_feature_id:
        raise AssertionError(
            f"{label} native feature_id {definition['feature_id']!r} differs from execution feature {expected_feature_id!r}")

    validated: list[str] = ["status", "feature_id", "native_feature_type"]
    requested_point = _vector(request.get("axis_point_mm"), label + " requested axis_point_mm", 3)
    actual_point = _numeric_string_vector(definition["axis_point_mm"], label + " native axis_point_mm", 3)
    point_residuals = [actual - expected for actual, expected in zip(actual_point, requested_point)]
    point_max_residual = max(abs(value) for value in point_residuals)
    if point_max_residual > LENGTH_TOLERANCE_MM:
        raise AssertionError(
            f"{label} native axis point residual {point_residuals!r} mm exceeds {LENGTH_TOLERANCE_MM:g} mm")
    if definition["axis_point_matches"] is not True:
        raise AssertionError(f"{label} production axis_point_matches is not true")
    validated.extend(("axis_point_mm", "axis_point_matches"))

    requested_direction_raw = _vector(request.get("axis_direction"), label + " requested axis_direction", 3)
    requested_direction = _normalize(requested_direction_raw)
    requested_start = _finite_number(request.get("start_angle_deg"), label + " requested start_angle_deg")
    requested_end = _finite_number(request.get("end_angle_deg"), label + " requested end_angle_deg")
    requested_span = requested_end - requested_start
    if (not math.isfinite(requested_span) or abs(requested_span) <= REVOLVE_ANGLE_ENDPOINT_TOLERANCE_DEG
            or abs(requested_span) > 360.0 or abs(requested_start) > 360.0 or abs(requested_end) > 360.0):
        raise AssertionError(f"{label} request has an invalid signed Revolve span {requested_span!r} deg")
    native_axis_sign = 1.0 if requested_span > 0.0 else -1.0
    native_axis_reversed = native_axis_sign < 0.0
    expected_native_direction = [component * native_axis_sign for component in requested_direction]
    actual_direction = _normalize(_vector(definition["axis_direction"], label + " native axis_direction", 3))
    reported_expected_direction = _normalize(_vector(
        definition["expected_native_axis_direction"], label + " expected_native_axis_direction", 3))

    def direction_error(left: list[float], right: list[float]) -> float:
        dot = max(-1.0, min(1.0, _dot(left, right)))
        return math.acos(dot)

    actual_direction_residual = direction_error(actual_direction, expected_native_direction)
    expected_direction_residual = direction_error(reported_expected_direction, expected_native_direction)
    if actual_direction_residual > REVOLVE_AXIS_DIRECTION_TOLERANCE_RAD:
        raise AssertionError(
            f"{label} native axis direction residual {actual_direction_residual:.12g} rad exceeds "
            f"{REVOLVE_AXIS_DIRECTION_TOLERANCE_RAD:g} rad for signed span {requested_span:g} deg")
    if expected_direction_residual > REVOLVE_AXIS_DIRECTION_TOLERANCE_RAD:
        raise AssertionError(f"{label} reported expected_native_axis_direction disagrees with signed-span mapping")
    if definition["axis_direction_matches"] is not True:
        raise AssertionError(f"{label} production axis_direction_matches is not true")
    for key in ("axis_tag", "axis_point_tag", "axis_direction_tag"):
        if not isinstance(definition[key], str) or not definition[key]:
            raise AssertionError(f"{label} native revolve_definition.{key} is not a non-empty identity")
    if definition["axis_helpers_match_committed_feature"] is not True:
        raise AssertionError(f"{label} axis helper identities do not match the committed feature")
    validated.extend(("axis_direction", "expected_native_axis_direction", "axis_direction_matches",
                      "axis_tag", "axis_point_tag", "axis_direction_tag",
                      "axis_helpers_match_committed_feature"))

    requested_echoes = (
        ("requested_start_angle_deg", requested_start),
        ("requested_end_angle_deg", requested_end),
        ("requested_signed_span_deg", requested_span),
    )
    for key, expected in requested_echoes:
        actual = _finite_number(definition[key], label + " " + key)
        residual = actual - expected
        if abs(residual) > REVOLVE_ANGLE_ENDPOINT_TOLERANCE_DEG:
            raise AssertionError(f"{label} {key} residual {residual:.12g} deg differs from request")
        validated.append(key)

    expected_native_start = requested_start * native_axis_sign
    expected_native_end = requested_end * native_axis_sign
    reported_expected_start = _finite_number(
        definition["expected_native_start_angle_deg"], label + " expected_native_start_angle_deg")
    reported_expected_end = _finite_number(
        definition["expected_native_end_angle_deg"], label + " expected_native_end_angle_deg")
    if abs(reported_expected_start - expected_native_start) > REVOLVE_ANGLE_ENDPOINT_TOLERANCE_DEG:
        raise AssertionError(f"{label} reported expected native start angle disagrees with signed-span mapping")
    if abs(reported_expected_end - expected_native_end) > REVOLVE_ANGLE_ENDPOINT_TOLERANCE_DEG:
        raise AssertionError(f"{label} reported expected native end angle disagrees with signed-span mapping")
    validated.extend(("expected_native_start_angle_deg", "expected_native_end_angle_deg"))
    actual_native_start = _finite_number(
        definition["actual_native_start_angle_deg"], label + " actual_native_start_angle_deg")
    actual_native_end = _finite_number(
        definition["actual_native_end_angle_deg"], label + " actual_native_end_angle_deg")
    start_residual = actual_native_start - expected_native_start
    end_residual = actual_native_end - expected_native_end
    if abs(start_residual) > REVOLVE_ANGLE_ENDPOINT_TOLERANCE_DEG:
        raise AssertionError(
            f"{label} actual native start angle residual {start_residual:.12g} deg exceeds "
            f"{REVOLVE_ANGLE_ENDPOINT_TOLERANCE_DEG:g} deg")
    if abs(end_residual) > REVOLVE_ANGLE_ENDPOINT_TOLERANCE_DEG:
        raise AssertionError(
            f"{label} actual native end angle residual {end_residual:.12g} deg exceeds "
            f"{REVOLVE_ANGLE_ENDPOINT_TOLERANCE_DEG:g} deg")
    for key in ("start_angle_expression_unit", "end_angle_expression_unit"):
        if str(definition[key]).casefold() != "degrees":
            raise AssertionError(f"{label} {key} must identify degrees, got {definition[key]!r}")
    for key in ("start_limit_matches_request", "end_limit_matches_request"):
        if definition[key] is not True:
            raise AssertionError(f"{label} production {key} is not true")
    span_in_request_frame = (actual_native_end - actual_native_start) * native_axis_sign
    signed_span_residual = span_in_request_frame - requested_span
    signed_span_tolerance = 2.0 * REVOLVE_ANGLE_ENDPOINT_TOLERANCE_DEG
    if abs(signed_span_residual) > signed_span_tolerance:
        raise AssertionError(f"{label} native signed span residual {signed_span_residual:.12g} deg is too large")
    validated.extend(("requested_signed_span_deg", "actual_native_start_angle_deg",
                      "actual_native_end_angle_deg", "start_angle_expression_unit",
                      "end_angle_expression_unit", "start_limit_matches_request",
                      "end_limit_matches_request"))

    for key in ("symmetric_option_is_disabled", "revolve_tolerance_matches", "offset_option_is_disabled"):
        if definition[key] is not True:
            raise AssertionError(f"{label} native Revolve option {key} is not true")
    validated.extend(("symmetric_option_is_disabled", "revolve_tolerance_matches", "offset_option_is_disabled"))

    mode = request.get("mode")
    expected_modes = {"create": "Create", "unite": "Unite", "subtract": "Subtract"}
    if not isinstance(mode, str) or mode.casefold() not in expected_modes:
        raise AssertionError(f"{label} request has unsupported Boolean mode {mode!r}")
    expected_mode = expected_modes[mode.casefold()]
    if str(definition["requested_boolean_mode"]).casefold() != mode.casefold():
        raise AssertionError(f"{label} native definition requested_boolean_mode differs from the request")
    if str(definition["actual_boolean_mode"]).casefold() != expected_mode.casefold():
        raise AssertionError(
            f"{label} actual native Boolean mode {definition['actual_boolean_mode']!r} differs from {expected_mode!r}")
    if definition["boolean_mode_matches"] is not True:
        raise AssertionError(f"{label} production boolean_mode_matches is not true")
    profile_output = request.get("profile_output", "solid")
    if profile_output not in ("solid", "sheet"):
        raise AssertionError(f"{label} request has unsupported profile_output {profile_output!r}")
    if profile_output == "sheet" and mode.casefold() != "create":
        raise AssertionError(f"{label} sheet output is allowed only for create mode")
    expected_body_type = "Sheet" if profile_output == "sheet" else "Solid"
    if str(definition["requested_profile_output"]).casefold() != str(profile_output).casefold():
        raise AssertionError(f"{label} native definition requested_profile_output differs from the request")
    if str(definition["actual_body_type"]).casefold() != expected_body_type.casefold():
        raise AssertionError(
            f"{label} actual native body type {definition['actual_body_type']!r} differs from {expected_body_type!r}")
    if definition["body_type_matches"] is not True:
        raise AssertionError(f"{label} production body_type_matches is not true")
    validated.extend(("requested_boolean_mode", "actual_boolean_mode", "boolean_mode_matches",
                      "requested_profile_output", "actual_body_type", "body_type_matches"))

    requested_target = request.get("target_body_id")
    if mode.casefold() == "create":
        if requested_target is not None:
            raise AssertionError(f"{label} create request unexpectedly identifies a Boolean target")
        expected_targets: list[str] = []
    else:
        if not isinstance(requested_target, str) or not requested_target:
            raise AssertionError(f"{label} Boolean request omitted its explicit target_body_id")
        expected_targets = [requested_target]
    actual_targets = definition["target_body_ids"]
    if not isinstance(actual_targets, list) or any(not isinstance(value, str) for value in actual_targets):
        raise AssertionError(f"{label} native target_body_ids must be a string list")
    if actual_targets != expected_targets:
        raise AssertionError(
            f"{label} native target body identities {actual_targets!r} differ from explicit request {expected_targets!r}")
    if definition["target_body_matches"] is not True:
        raise AssertionError(f"{label} production target_body_matches is not true")
    if definition["source_sketch_is_feature_parent"] is not True:
        raise AssertionError(f"{label} Revolve feature is not reported as parented by the selected Sketch")
    if definition["section_domain_matches_prepare"] is not True:
        raise AssertionError(f"{label} native Section domain differs from the prepared whole-Sketch profile")
    if not isinstance(definition["section_domain"], dict):
        raise AssertionError(f"{label} native Section domain evidence is not an object")
    validated.extend(("target_body_ids", "target_body_matches", "source_sketch_is_feature_parent",
                      "section_domain_matches_prepare"))

    axis_plane = definition["axis_plane_evidence"]
    if not isinstance(axis_plane, dict):
        raise AssertionError(f"{label} axis_plane_evidence is not an object")
    plane_required = ("status", "axis_plane_residual_mm", "axis_direction_normal_dot",
                      "point_tolerance_mm", "direction_tolerance")
    missing_plane = [key for key in plane_required if key not in axis_plane or axis_plane[key] is None]
    if missing_plane:
        raise AssertionError(f"{label} axis_plane_evidence omitted required fields: {missing_plane}")
    if axis_plane["status"] != "VERIFIED" or definition["axis_in_sketch_plane"] is not True:
        raise AssertionError(f"{label} native Revolve axis is not verified in the selected Sketch plane")
    plane_residual = _finite_number(axis_plane["axis_plane_residual_mm"], label + " axis-plane residual")
    plane_direction_residual = _finite_number(
        axis_plane["axis_direction_normal_dot"], label + " axis-plane direction residual")
    point_tolerance = _finite_number(axis_plane["point_tolerance_mm"], label + " axis-plane point tolerance")
    direction_tolerance = _finite_number(
        axis_plane["direction_tolerance"], label + " axis-plane direction tolerance")
    if abs(point_tolerance - REVOLVE_AXIS_PLANE_TOLERANCE_MM) > 1.0e-12:
        raise AssertionError(f"{label} axis-plane point tolerance differs from the F13 contract")
    if abs(direction_tolerance - REVOLVE_AXIS_PLANE_DIRECTION_TOLERANCE) > 1.0e-12:
        raise AssertionError(f"{label} axis-plane direction tolerance differs from the F13 contract")
    if abs(plane_residual) > point_tolerance:
        raise AssertionError(f"{label} axis-plane point residual {plane_residual:g} mm exceeds {point_tolerance:g} mm")
    if abs(plane_direction_residual) > direction_tolerance:
        raise AssertionError(
            f"{label} axis-plane direction residual {plane_direction_residual:g} exceeds {direction_tolerance:g}")
    validated.extend(("axis_in_sketch_plane", "axis_plane_evidence"))

    return {
        "available": True,
        "feature_id_matches_execute": True,
        "validated_fields": validated,
        "native_axis_reversed_with_signed_limits": native_axis_reversed,
        "tolerances": {
            "axis_point_mm": LENGTH_TOLERANCE_MM,
            "axis_direction_rad": REVOLVE_AXIS_DIRECTION_TOLERANCE_RAD,
            "angle_endpoint_deg": REVOLVE_ANGLE_ENDPOINT_TOLERANCE_DEG,
            "signed_span_deg": signed_span_tolerance,
            "axis_plane_point_mm": point_tolerance,
            "axis_plane_direction": direction_tolerance,
        },
        "residuals": {
            "axis_point_component_mm": point_residuals,
            "axis_point_max_abs_mm": point_max_residual,
            "axis_direction_rad": actual_direction_residual,
            "reported_expected_axis_direction_rad": expected_direction_residual,
            "start_angle_deg": start_residual,
            "end_angle_deg": end_residual,
            "signed_span_deg": signed_span_residual,
            "axis_plane_point_mm": plane_residual,
            "axis_plane_direction": plane_direction_residual,
        },
        "expected_native": {
            "axis_direction": expected_native_direction,
            "start_angle_deg": expected_native_start,
            "end_angle_deg": expected_native_end,
            "boolean_mode": expected_mode,
            "target_body_ids": expected_targets,
            "body_type": expected_body_type,
        },
        "actual_native": {
            "axis_point_mm": actual_point,
            "axis_direction": actual_direction,
            "start_angle_deg": actual_native_start,
            "end_angle_deg": actual_native_end,
            "boolean_mode": definition["actual_boolean_mode"],
            "target_body_ids": actual_targets,
            "body_type": definition["actual_body_type"],
        },
    }


def _part_signature(part: dict) -> dict:
    return {
        "revision_id": part.get("revision_id"),
        "features": part.get("features"),
        "sketches": part.get("sketches"),
        "bodies": [_structural_body_signature(body) for body in _body_records(part)],
        "curves": part.get("curve_count"),
        "part_is_modified": part.get("part_is_modified", part.get("is_modified")),
    }


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    frame = make_axis_frame(AXIS_POINT_MM, AXIS_DIRECTION_RAW, RADIAL_SEED)
    normal = _cross(list(frame.radial_zero), list(frame.direction))
    normal = _normalize(normal)
    x_axis = list(frame.radial_zero)
    axis_direction = list(frame.direction)
    if args.frame_control:
        return _run_offline_frame_control(args.frame_evidence, args.output, normal, x_axis)
    if args.definition_control:
        return _run_offline_definition_control(args.definition_evidence, args.output)
    expected_main_volume = revolved_solid_volume_mm3([MAIN_LOOP], 30.0, -150.0)
    expected_tool_volume = revolved_solid_volume_mm3([TOOL_LOOP], 30.0, -150.0)
    expected_intersection_volume = revolved_solid_volume_mm3([INTERSECTION_LOOP], 30.0, -150.0)
    expected_union_volume = expected_main_volume + expected_tool_volume - expected_intersection_volume
    expected_main_bounds = _flat_bounds(revolved_profile_bounds_mm([MAIN_LOOP], frame, 30.0, -150.0))
    target_bounds = revolved_profile_bounds_mm([MAIN_LOOP], frame, 30.0, -150.0)
    tool_bounds = revolved_profile_bounds_mm([TOOL_LOOP], frame, 30.0, -150.0)
    expected_union_bounds = _flat_bounds((
        tuple(min(target_bounds[0][index], tool_bounds[0][index]) for index in range(3)),
        tuple(max(target_bounds[1][index], tool_bounds[1][index]) for index in range(3)),
    ))
    expected_sheet_area = revolved_sheet_area_mm2([(18.0, 2.0), (18.0, 8.0)], 0.0, 90.0)

    report: dict = {
        "test": "F13 installed MCP revolve acceptance",
        "test_version": "0.1.0",
        "run_id": uuid.uuid4().hex,
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "passed": False,
        "mode": "ready" if args.ready else "modeling",
        "objective": "Exercise the locked F13 nx_prepare_revolve public contract through the configured installed stdio host.",
        "traceability": "docs/F13.md F13-I01; docs/F13.md public interface contract",
        "configuration": {
            "test_layer": "installed MCP integration and NX model subsystem smoke",
            "expected_server_version": args.expected_server_version,
            "expected_tool_count": CONTRACT_TOOL_COUNT,
            "units": {"length": "mm", "angle_request": "deg", "angle_oracle": "rad",
                      "area": "mm^2", "volume": "mm^3"},
            "sample_size": 1,
            "physical_instrumentation": False,
            "calibration": "not applicable; installed NX direct digital model readbacks only",
            "measurement_uncertainty": "CAD comparison limits are software acceptance thresholds, not physical uncertainty.",
            "strict_volume_source": "matched affected body in explicit fresh nx_verify_part evidence",
            "strict_volume_mass_property_accuracy": VERIFY_MASS_PROPERTY_ACCURACY,
            "strict_sheet_area_source": "matched body and per-face areas in fresh Verify result_geometry",
            "strict_sheet_face_property_accuracy": VERIFY_FACE_PROPERTY_ACCURACY,
            "legacy_inspect_mass_property_accuracy": LEGACY_INSPECT_MASS_PROPERTY_ACCURACY,
            "legacy_inspect_volume_role": "diagnostic only; never compared to the independent oracle",
            "legacy_inspect_area_role": "diagnostic only; never compared to the independent oracle",
            "reliability_or_compliance_claim": False,
            "nx_started_by_harness": False,
            "nx_closed_by_harness": False,
            "host_started_by_harness": True,
        },
        "axis_frame": {"point_mm": AXIS_POINT_MM, "direction_input": AXIS_DIRECTION_RAW,
                       "direction_unit": axis_direction, "radial_zero": x_axis,
                       "sketch_normal": normal, "section_axial_axis": axis_direction,
                       "positive_rotation_rule": "right-hand rule about axis_direction"},
        "independent_oracle": {
            "main_solid": {"loop": list(MAIN_LOOP.points_mm), "start_angle_deg": 30.0,
                           "end_angle_deg": -150.0, "signed_span_deg": -180.0,
                           "volume_mm3": expected_main_volume, "bbox_mm": expected_main_bounds},
            "sheet": {"polyline_mm": [[18.0, 2.0], [18.0, 8.0]],
                      "start_angle_deg": 0.0, "end_angle_deg": 90.0,
                      "surface_area_mm2": expected_sheet_area},
            "unite": {"target_loop": list(MAIN_LOOP.points_mm), "tool_loop": list(TOOL_LOOP.points_mm),
                      "intersection_loop": list(INTERSECTION_LOOP.points_mm),
                      "expected_target_volume_mm3": expected_main_volume,
                      "expected_tool_volume_mm3": expected_tool_volume,
                      "expected_intersection_volume_mm3": expected_intersection_volume,
                      "expected_union_volume_mm3": expected_union_volume,
                      "expected_union_bbox_mm": expected_union_bounds},
        },
        "tolerances": {"length_mm": LENGTH_TOLERANCE_MM,
                       "axis_direction_rad": ANGLE_TOLERANCE_RAD,
                       "angle_endpoint_rad": ANGLE_TOLERANCE_RAD,
                       "volume_absolute_mm3": VOLUME_ABSOLUTE_TOLERANCE_MM3,
                       "volume_relative": VOLUME_RELATIVE_TOLERANCE,
                       "sheet_area_mm2": SHEET_AREA_TOLERANCE_MM2,
                       "verify_mass_property_accuracy": VERIFY_MASS_PROPERTY_ACCURACY,
                       "verify_face_property_accuracy": VERIFY_FACE_PROPERTY_ACCURACY,
                       "legacy_inspect_mass_property_accuracy": LEGACY_INSPECT_MASS_PROPERTY_ACCURACY},
        "calls": [], "cases": [], "defects": [], "unknown_events": [],
        "lifecycle": {"execute_calls_by_operation_id": {}, "replay_attempts": 0,
                      "writes_attempted_after_unknown": 0, "unknown_fault_injected": False,
                      "unknown_policy": "If an operation returns UNKNOWN, query only its same operation ID, freeze all further writes, and preserve the failure report."},
        "limitations": [
            "Installed MCP results are system integration evidence; the NXOpen batch acceptance remains the direct native definition and topology authority.",
            "One deterministic installed fixture run is one observation and does not establish reliability or support for other NX releases.",
            "UNKNOWN injection is intentionally exercised in the NX-free fake lifecycle suite; this installed run records and freezes any naturally observed UNKNOWN without replay.",
            "Legacy nx_inspect_part volume is recorded at the configured 0.99 accuracy as a diagnostic; oracle volume acceptance uses the matching affected-body record in explicit fresh Verify evidence at 0.999999.",
            "Fresh Verify face areas come from direct face records at 0.999999; any legacy inspection area remains diagnostic only.",
        ],
    }

    credentials: dict[str, object] = {}
    process: subprocess.Popen[bytes] | None = None
    client: McpStdioClient | None = None
    active_case: dict | None = None
    lifecycle = {"writes_halted": False, "unknown_operation_id": None}

    def persist() -> None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open("w", encoding="utf-8", newline="\n") as stream:
            json.dump(_redact(report), stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")

    def raw_call(tool: str, arguments: dict) -> tuple[dict, dict]:
        if client is None:
            raise RuntimeError("installed MCP client is not initialized")
        if lifecycle["writes_halted"] and tool in {
                "nx_prepare_sketch", "nx_prepare_revolve", "nx_execute_plan",
                "nx_verify_part", "nx_save_copy", "nx_return"}:
            report["lifecycle"]["writes_attempted_after_unknown"] += 1
            persist()
            raise RuntimeError(f"suppressed {tool} after operation UNKNOWN")
        entry = {"tool": tool, "arguments": _redact(arguments)}
        if isinstance(active_case, dict):
            entry["case_id"] = active_case.get("case_id")
        report["calls"].append(entry)
        try:
            response = client.request("tools/call", {"name": tool, "arguments": arguments})
        except Exception as exc:
            protocol_response = client.last_response
            if isinstance(protocol_response, dict):
                entry["mcp_response"] = _redact(protocol_response)
                protocol_result = protocol_response.get("result")
                entry["is_error"] = ("error" in protocol_response or
                                     (isinstance(protocol_result, dict) and protocol_result.get("isError") is True))
                entry["error_code"] = _error_code(protocol_response)
                payload = _decode_payload(protocol_result) if isinstance(protocol_result, dict) else {}
                entry["payload"] = _redact(payload)
                persist()
                return payload, entry
            entry["transport_error"] = repr(exc)
            persist()
            raise
        entry["mcp_response"] = _redact(response)
        entry["is_error"] = response.get("isError") is True
        payload = _decode_payload(response)
        if entry["is_error"]:
            entry["error_code"] = _error_code(response)
        entry["payload"] = _redact(payload)
        persist()
        return payload, entry

    def sync_call(tool: str, arguments: dict | None = None) -> dict:
        payload, entry = raw_call(tool, arguments or {})
        if entry.get("is_error"):
            raise RuntimeError(f"{tool} failed: {_error_code(payload) or payload.get('error') or entry.get('error_code')}")
        if payload.get("operation_id"):
            raise AssertionError(f"{tool} unexpectedly returned an asynchronous operation")
        return payload

    def poll_operation(initial: dict, label: str) -> dict:
        operation_id = initial.get("operation_id")
        if not isinstance(operation_id, str) or not operation_id:
            raise AssertionError(f"{label} omitted operation_id")
        deadline = time.monotonic() + OPERATION_TIMEOUT_SECONDS
        current = initial
        while current.get("state") not in TERMINAL_STATES:
            if time.monotonic() >= deadline:
                raise TimeoutError(f"{label} operation {operation_id} did not reach a terminal state")
            time.sleep(POLL_INTERVAL_SECONDS)
            current, entry = raw_call("nx_get_operation", {"operation_id": operation_id})
            entry["polled_for"] = label
            if entry.get("is_error"):
                lifecycle["writes_halted"] = True
                lifecycle["unknown_operation_id"] = operation_id
                report["unknown_events"].append({"operation_id": operation_id, "label": label,
                                                 "case_id": active_case.get("case_id") if isinstance(active_case, dict) else None,
                                                 "state": "UNKNOWN", "action": "operation query failed; writes frozen"})
                persist()
                raise RuntimeError(f"{label} outcome is UNKNOWN because nx_get_operation failed")
            if current.get("operation_id") not in (None, operation_id):
                raise AssertionError(f"{label} nx_get_operation returned another operation ID")
        observed_unknown = current.get("state") == "UNKNOWN" or initial.get("state") == "UNKNOWN"
        if current.get("state") == "UNKNOWN" and initial.get("state") == "UNKNOWN":
            current, entry = raw_call("nx_get_operation", {"operation_id": operation_id})
            entry["polled_for"] = label + " UNKNOWN same-ID resolution query"
            if current.get("operation_id") not in (None, operation_id):
                raise AssertionError(f"{label} UNKNOWN query returned another operation ID")
        if observed_unknown or current.get("state") == "UNKNOWN":
            lifecycle["writes_halted"] = True
            lifecycle["unknown_operation_id"] = operation_id
            report["unknown_events"].append({"operation_id": operation_id, "label": label,
                                             "case_id": active_case.get("case_id") if isinstance(active_case, dict) else None,
                                             "state": "UNKNOWN", "resolution_query_state": current.get("state"),
                                             "action": "query-only; no replay or later write"})
            report["lifecycle"]["unknown_fault_injected"] = False
            persist()
        return current

    def operation_call(tool: str, arguments: dict, label: str) -> tuple[dict, dict, dict]:
        if lifecycle["writes_halted"]:
            report["lifecycle"]["writes_attempted_after_unknown"] += 1
            raise RuntimeError("F13 installed harness suppressed a new operation after UNKNOWN")
        initial, entry = raw_call(tool, arguments)
        if entry.get("is_error"):
            raise RuntimeError(f"{label} {tool} failed: {_error_code(initial) or entry.get('error_code') or initial}")
        if not isinstance(initial.get("operation_id"), str):
            raise AssertionError(f"{label} {tool} did not return an asynchronous operation")
        terminal = poll_operation(initial, label)
        entry["terminal_operation"] = _redact(terminal)
        persist()
        if terminal.get("state") == "UNKNOWN":
            # The last nx_get_operation response is the required same-ID query.
            raise RuntimeError(f"{label} reached UNKNOWN; no replay or further writes are allowed")
        if terminal.get("state") != "SUCCEEDED":
            raise RuntimeError(f"{label} ended {terminal.get('state')}/{_error_code(terminal) or 'NO_ERROR_CODE'}")
        result = _operation_result(terminal)
        if not isinstance(result, dict):
            raise AssertionError(f"{label} terminal result is not an object")
        return terminal, result, entry

    def execute_plan_once(operation_id: str, plan_id: str, label: str) -> tuple[dict, dict, dict]:
        counts = report["lifecycle"]["execute_calls_by_operation_id"]
        counts[operation_id] = counts.get(operation_id, 0) + 1
        if counts[operation_id] > 1:
            report["lifecycle"]["replay_attempts"] += 1
            raise AssertionError(f"{label} attempted execute replay for {operation_id}")
        return operation_call("nx_execute_plan", {
            "operation_id": operation_id, **credentials, "plan_id": plan_id}, label)

    def refresh_revision() -> str:
        session = sync_call("nx_get_session", credentials)
        revision = session.get("revision_id")
        if not isinstance(revision, str) or not revision:
            raise AssertionError("nx_get_session omitted revision_id")
        report["current_revision_id"] = revision
        return revision

    def inspect_part() -> dict:
        return sync_call("nx_inspect_part", credentials)

    def add_case(case_id: str, objective: str, expected: dict) -> dict:
        nonlocal active_case
        case = {"case_id": case_id, "layer": "installed-MCP integration",
                "state": "RUNNING", "objective": objective,
                "independent_expected": expected}
        report["cases"].append(case)
        active_case = case
        persist()
        return case

    def record_failure(case: dict, exc: Exception) -> None:
        case.update(state="FAIL", failure=repr(exc), reproduction_steps=[
            "Create a new empty saved fixture with tools/v2/f13-create-mcp-fixture.py and retain its JSON metadata.",
            "Open only that saved fixture in the designated idle NX session; rerun with a new --output path.",
            f"Repeat {case.get('case_id')} with the exact arguments and raw MCP responses retained in this report.",
        ], retest_criterion=(
            "The same operation completes without UNKNOWN, one execute per operation ID reports APPLIED, explicit fresh Verify passes, "
            "selected curve counts match the direct Sketch inventory, and independent volume/area/bounds meet the numeric tolerances."
        ))
        report["defects"].append({"case_id": case.get("case_id"), "failure": repr(exc),
                                  "raw_call_indexes": [index for index, call in enumerate(report["calls"])
                                                       if call.get("case_id") == case.get("case_id")],
                                  "reproduction_steps": case["reproduction_steps"],
                                  "retest_criterion": case["retest_criterion"]})
        persist()

    def prepare_sketch(case_id: str, origin_mm: list[float], geometries: list[dict],
                       expected_count: int) -> tuple[str, dict]:
        nonlocal active_case
        case = add_case(case_id, "Create, execute, explicitly verify, and inspect a uniquely identified F13 section Sketch.",
                        {"direct_curve_count": expected_count, "frame_origin_mm": origin_mm,
                         "normal": normal, "x_axis": x_axis})
        try:
            request = {"operation_id": "f13-sketch-prepare-" + uuid.uuid4().hex,
                       **credentials, "revision_id": refresh_revision(), "origin_mm": origin_mm,
                       "normal": normal, "x_axis": x_axis, "constraint_mode": "geometry_only",
                       "geometries": geometries}
            case["prepare_request"] = _redact(request)
            prepared_op, prepared, _ = operation_call("nx_prepare_sketch", request, case_id + " prepare sketch")
            plan = prepared.get("plan")
            if not isinstance(plan, dict) or not isinstance(plan.get("plan_id"), str):
                raise AssertionError("nx_prepare_sketch omitted plan.plan_id")
            execute_id = "f13-sketch-execute-" + uuid.uuid4().hex
            executed_op, executed, _ = execute_plan_once(execute_id, plan["plan_id"], case_id + " execute sketch")
            if executed.get("mutation_state") != "APPLIED":
                raise AssertionError("Sketch plan did not report mutation_state=APPLIED")
            verified_op, verified, _ = operation_call("nx_verify_part", {
                "operation_id": "f13-sketch-verify-" + uuid.uuid4().hex, **credentials,
                "execute_operation_id": execute_id}, case_id + " fresh Sketch verify")
            _assert_verified(verified, case_id + " Sketch")
            sketch_id = executed.get("sketch_id") or verified.get("sketch_id")
            if not isinstance(sketch_id, str) or not sketch_id:
                raise AssertionError("Sketch operation omitted sketch_id")
            inspected = sync_call("nx_inspect_sketch", {**credentials, "sketch_id": sketch_id})
            sketch = inspected.get("sketch")
            geometry = sketch.get("geometry") if isinstance(sketch, dict) else None
            if not isinstance(geometry, list) or len(geometry) != expected_count:
                raise AssertionError(f"{case_id} expected {expected_count} direct curves, got {len(geometry) if isinstance(geometry, list) else 'missing'}")
            frame_check = _assert_sketch_frame(sketch, origin_mm, normal, x_axis, case_id)
            case.update(state="PASS", sketch_id=sketch_id, direct_curve_count=len(geometry),
                        direct_geometry=_redact(geometry), sketch_inspection=_redact(inspected),
                        direct_frame_check=frame_check,
                        prepare_operation_id=prepared_op.get("operation_id"),
                        execute_operation_id=executed_op.get("operation_id"),
                        verify_operation_id=verified_op.get("operation_id"))
            persist()
            return sketch_id, sketch
        except Exception as exc:
            record_failure(case, exc)
            raise

    def operation_get_twice(execute_id: str, original: dict, label: str) -> dict:
        before = inspect_part()
        before_signature = _part_signature(before)
        save_copy_calls_before = sum(call.get("tool") == "nx_save_copy" for call in report["calls"])
        first, _ = raw_call("nx_get_operation", {"operation_id": execute_id})
        second, _ = raw_call("nx_get_operation", {"operation_id": execute_id})
        for query in (first, second):
            if query.get("operation_id") != execute_id or query.get("state") != "SUCCEEDED":
                raise AssertionError(f"{label} nx_get_operation did not return the terminal operation")
            result = _operation_result(query)
            if not isinstance(result, dict) or result.get("mutation_state") != "APPLIED":
                raise AssertionError(f"{label} query omitted original APPLIED outcome")
        original_result = _operation_result(original)
        queried_result = _operation_result(first)
        if original_result is not None and queried_result != original_result:
            raise AssertionError(f"{label} operation.get changed the stored execute result")
        if first.get("state") != second.get("state") or _operation_result(first) != _operation_result(second):
            raise AssertionError(f"{label} repeated operation.get results were not stable")
        after = inspect_part()
        after_signature = _part_signature(after)
        if before_signature != after_signature:
            raise AssertionError(f"{label} operation.get changed inspected model inventory")
        save_copy_calls_after = sum(call.get("tool") == "nx_save_copy" for call in report["calls"])
        if save_copy_calls_after != save_copy_calls_before:
            raise AssertionError(f"{label} operation.get caused another SaveCopy call")
        if report["lifecycle"]["execute_calls_by_operation_id"].get(execute_id) != 1:
            raise AssertionError(f"{label} execute call count for {execute_id} was not exactly one")
        summary = {"operation_id": execute_id, "query_count": 2, "execute_call_count": 1,
                   "save_copy_calls_before": save_copy_calls_before,
                   "save_copy_calls_after": save_copy_calls_after,
                   "state": first.get("state"), "model_signature_unchanged": True,
                   "result": _redact(_operation_result(first))}
        report.setdefault("operation_query_evidence", []).append(summary)
        return first

    def assert_direct_solid(case: dict, execution: dict, verification: dict,
                            verification_evidence: dict, inspection: dict,
                            expected_volume: float, expected_box: list[float],
                            *, execute_id: str, verify_id: str, plan_id: str,
                            target_id: str | None = None) -> str:
        body_ids = execution.get("body_ids")
        if not isinstance(body_ids, list) or not body_ids:
            raise AssertionError(f"{case['case_id']} execution omitted body_ids")
        if len(set(body_ids)) != len(body_ids):
            raise AssertionError(f"{case['case_id']} execution body_ids contain duplicates")
        if target_id is None and len(body_ids) != 1:
            raise AssertionError(f"{case['case_id']} expected one created solid body, got {len(body_ids)}")
        if target_id is not None and target_id not in body_ids:
            raise AssertionError(f"{case['case_id']} Boolean body_ids omitted the explicit target")
        if target_id is not None and execution.get("target_body_id") not in (None, target_id):
            raise AssertionError(f"{case['case_id']} direct Boolean target differs from the explicit target")
        if (execution.get("mode") is not None and
                str(execution.get("mode")).casefold() != str(case["independent_expected"].get("mode")).casefold()):
            raise AssertionError(f"{case['case_id']} execution mode differs from the requested mode")
        if verification.get("execute_operation_id") != execute_id or verification.get("plan_id") != plan_id:
            raise AssertionError(f"{case['case_id']} strict volume evidence is not tied to the executed plan")
        if verification_evidence.get("feature_id") != execution.get("feature_id"):
            raise AssertionError(f"{case['case_id']} fresh Verify evidence refers to a different feature")
        if not isinstance(execution.get("feature_id"), str) or not execution.get("feature_id"):
            raise AssertionError(f"{case['case_id']} Execute omitted the native feature ID")
        verified_ids = verification_evidence.get("body_ids")
        if not isinstance(verified_ids, list) or set(verified_ids) != set(body_ids):
            raise AssertionError(f"{case['case_id']} fresh Verify output body IDs differ from Execute")
        verified_bodies = verification_evidence.get("bodies")
        if not isinstance(verified_bodies, list):
            raise AssertionError(f"{case['case_id']} fresh Verify evidence omitted affected body records")
        verified_map = {body.get("journal_id"): body for body in verified_bodies
                        if isinstance(body, dict) and isinstance(body.get("journal_id"), str)}
        if len(verified_map) != len(verified_bodies) or set(verified_map) != set(verified_ids):
            raise AssertionError(f"{case['case_id']} fresh Verify body records are not a one-to-one body ID mapping")
        body_id = target_id or body_ids[0]
        verified_body = verified_map.get(body_id)
        if not isinstance(verified_body, dict):
            raise AssertionError(f"{case['case_id']} fresh Verify evidence omitted affected body {body_id}")
        if verified_body.get("is_solid") is not True:
            raise AssertionError(f"{case['case_id']} fresh Verify body is not solid")
        if verified_body.get("measurement_status") != "MEASURED":
            raise AssertionError(f"{case['case_id']} fresh Verify body measurement is {verified_body.get('measurement_status')!r}")
        verified_volume = verified_body.get("volume_mm3")
        _close(verified_volume, expected_volume, label=case["case_id"] + " fresh Verify body volume",
               absolute=VOLUME_ABSOLUTE_TOLERANCE_MM3, relative=VOLUME_RELATIVE_TOLERANCE)
        verified_box = verified_body.get("bbox_mm")
        _assert_bbox(verified_box, expected_box, case["case_id"] + " fresh Verify exact body bbox")
        reported_accuracy = _measurement_accuracy_fields(verification)
        for accuracy in reported_accuracy:
            _close(accuracy["value"], VERIFY_MASS_PROPERTY_ACCURACY,
                   label=case["case_id"] + " reported fresh Verify mass-property accuracy",
                   absolute=1.0e-12)

        body_map = {body.get("journal_id"): body for body in _body_records(inspection)
                    if isinstance(body.get("journal_id"), str)}
        body = body_map.get(body_id)
        if not isinstance(body, dict):
            raise AssertionError(f"{case['case_id']} nx_inspect_part omitted output body {body_id}")
        if body.get("is_solid") is not True:
            raise AssertionError(f"{case['case_id']} direct body is not solid")
        inspect_box = _assert_bbox(body.get("bbox_mm"), expected_box,
                                   case["case_id"] + " nx_inspect_part exact bbox diagnostic")
        case["direct_measurement"] = {
            "body_id": body_id,
            "source": "explicit fresh nx_verify_part evidence.bodies",
            "execute_operation_id": execute_id,
            "verify_operation_id": verify_id,
            "plan_id": plan_id,
            "feature_id": execution.get("feature_id"),
            "measurement_status": verified_body.get("measurement_status"),
            "volume_mm3": verified_volume,
            "bbox_mm": verified_box,
            "mass_property_accuracy": VERIFY_MASS_PROPERTY_ACCURACY,
            "accuracy_source": "F13 VerifyM3RevolvePlan uses CaptureM3BodyMeasures(..., F12MassPropertyAccuracy=0.999999)",
            "accuracy_explicitly_serialized_by_installed_response": bool(reported_accuracy),
            "reported_accuracy_fields": reported_accuracy,
        }
        case["nx_inspect_part_body_diagnostic_only"] = {
            "body_id": body_id,
            "is_solid": body.get("is_solid"),
            "measurement_status": body.get("measurement_status"),
            "legacy_volume_mm3_diagnostic_only": body.get("volume_mm3"),
            "legacy_volume_accuracy": LEGACY_INSPECT_MASS_PROPERTY_ACCURACY,
            "legacy_volume_used_for_oracle": False,
            "exact_bbox_mm": inspect_box,
            "face_count": len(body.get("face_ids", [])) if isinstance(body.get("face_ids"), list) else None,
            "edge_count": len(body.get("edge_ids", [])) if isinstance(body.get("edge_ids"), list) else None,
        }
        return body_id

    def assert_direct_sheet(case: dict, execution: dict, verification: dict,
                            verification_evidence: dict, inspection: dict,
                            expected_area: float, *, execute_id: str,
                            verify_id: str, plan_id: str) -> str:
        body_ids = execution.get("body_ids")
        if not isinstance(body_ids, list) or len(body_ids) != 1:
            raise AssertionError(f"{case['case_id']} expected one direct sheet body ID")
        if verification.get("execute_operation_id") != execute_id or verification.get("plan_id") != plan_id:
            raise AssertionError(f"{case['case_id']} sheet area evidence is not tied to the executed plan")
        if verification_evidence.get("feature_id") != execution.get("feature_id"):
            raise AssertionError(f"{case['case_id']} fresh Verify evidence refers to a different feature")
        if not isinstance(execution.get("feature_id"), str) or not execution.get("feature_id"):
            raise AssertionError(f"{case['case_id']} Execute omitted the native feature ID")
        verified_ids = verification_evidence.get("body_ids")
        if not isinstance(verified_ids, list) or set(verified_ids) != set(body_ids):
            raise AssertionError(f"{case['case_id']} fresh Verify sheet body IDs differ from Execute")
        geometry = verification_evidence.get("result_geometry")
        if not isinstance(geometry, dict) or geometry.get("status") != "VERIFIED":
            raise AssertionError(f"{case['case_id']} fresh Verify omitted verified direct face geometry")
        geometry_bodies = geometry.get("bodies")
        if not isinstance(geometry_bodies, list):
            raise AssertionError(f"{case['case_id']} fresh Verify result_geometry omitted bodies")
        body_id = body_ids[0]
        direct_geometry_body = next((body for body in geometry_bodies if isinstance(body, dict)
                                     and body.get("body_id") == body_id), None)
        if not isinstance(direct_geometry_body, dict):
            raise AssertionError(f"{case['case_id']} fresh Verify result_geometry omitted sheet body {body_id}")
        if direct_geometry_body.get("solid") is not False:
            raise AssertionError(f"{case['case_id']} fresh Verify result body is not a sheet")
        faces = direct_geometry_body.get("faces")
        if not isinstance(faces, list) or not faces:
            raise AssertionError(f"{case['case_id']} fresh Verify sheet body omitted direct faces")
        per_face_areas = [_finite_number(face.get("area_mm2"), f"{case['case_id']} face area")
                          for face in faces if isinstance(face, dict)]
        if len(per_face_areas) != len(faces) or any(area <= 0.0 for area in per_face_areas):
            raise AssertionError(f"{case['case_id']} fresh Verify includes an invalid face area")
        measured_area = sum(per_face_areas)
        if len(faces) != direct_geometry_body.get("face_count"):
            raise AssertionError(f"{case['case_id']} direct face count differs from the face records")
        _close(direct_geometry_body.get("surface_area_mm2"), expected_area,
               label=case['case_id'] + " fresh Verify direct face area",
               absolute=SHEET_AREA_TOLERANCE_MM2)
        _close(measured_area, expected_area, label=case['case_id'] + " summed fresh Verify face areas",
               absolute=SHEET_AREA_TOLERANCE_MM2)
        _close(verification_evidence.get("surface_area_mm2"), expected_area,
               label=case['case_id'] + " fresh Verify aggregate sheet area",
               absolute=SHEET_AREA_TOLERANCE_MM2)
        reported_accuracy = _measurement_accuracy_fields(verification)
        for accuracy in reported_accuracy:
            _close(accuracy["value"], VERIFY_FACE_PROPERTY_ACCURACY,
                   label=case['case_id'] + " reported fresh Verify face-area accuracy",
                   absolute=1.0e-12)

        inspect_map = {body.get("journal_id"): body for body in _body_records(inspection)
                       if isinstance(body.get("journal_id"), str)}
        inspect_body = inspect_map.get(body_id)
        if not isinstance(inspect_body, dict) or inspect_body.get("is_solid") is not False:
            raise AssertionError(f"{case['case_id']} nx_inspect_part did not read back the expected sheet body")
        case["direct_measurement"] = {
            "body_id": body_id,
            "source": "explicit fresh nx_verify_part evidence.result_geometry.bodies.faces",
            "execute_operation_id": execute_id,
            "verify_operation_id": verify_id,
            "plan_id": plan_id,
            "feature_id": execution.get("feature_id"),
            "surface_area_mm2": measured_area,
            "face_count": len(faces),
            "face_property_accuracy": VERIFY_FACE_PROPERTY_ACCURACY,
            "accuracy_source": "F13 Verify captures audited F12 face records using NewFaceProperties accuracy 0.999999",
            "accuracy_explicitly_serialized_by_installed_response": bool(reported_accuracy),
            "reported_accuracy_fields": reported_accuracy,
            "per_face_area_mm2": per_face_areas,
        }
        case["nx_inspect_part_body_diagnostic_only"] = {
            "body_id": body_id,
            "is_solid": inspect_body.get("is_solid"),
            "measurement_status": inspect_body.get("measurement_status"),
            "legacy_volume_mm3_diagnostic_only": inspect_body.get("volume_mm3"),
            "legacy_area_mm2_diagnostic_only": inspect_body.get("surface_area_mm2"),
            "legacy_volume_mass_property_accuracy": LEGACY_INSPECT_MASS_PROPERTY_ACCURACY,
            "legacy_area_accuracy": None,
            "legacy_measurements_used_for_oracle": False,
            "bbox_mm": inspect_body.get("bbox_mm"),
            "face_count": len(inspect_body.get("face_ids", [])) if isinstance(inspect_body.get("face_ids"), list) else None,
            "edge_count": len(inspect_body.get("edge_ids", [])) if isinstance(inspect_body.get("edge_ids"), list) else None,
        }
        return body_id

    def run_revolve(case_id: str, sketch_id: str, arguments: dict, expected_curve_count: int,
                    *, expected_volume: float | None = None, expected_box: list[float] | None = None,
                    expected_sheet_area: float | None = None,
                    target_id: str | None = None) -> tuple[dict, str | None, dict]:
        case = add_case(case_id, "Prepare F13 revolve, execute exactly once, call explicit fresh nx_verify_part, query the same execute ID, and compare direct values with the independent oracle.",
                        {**arguments, "expected_volume_mm3": expected_volume,
                         "expected_bbox_mm": expected_box, "expected_sheet_area_mm2": expected_sheet_area,
                         "selected_curve_count": expected_curve_count})
        try:
            request = {"operation_id": "f13-revolve-prepare-" + uuid.uuid4().hex,
                       **credentials, "revision_id": refresh_revision(), "sketch_id": sketch_id,
                       **arguments}
            case["prepare_request"] = _redact(request)
            prepared_op, prepared, _ = operation_call("nx_prepare_revolve", request, case_id + " prepare")
            plan = prepared.get("plan")
            if not isinstance(plan, dict) or not isinstance(plan.get("plan_id"), str):
                raise AssertionError("nx_prepare_revolve omitted plan.plan_id")
            case["prepare_profile_analysis"] = _redact(_find_dict(prepared, "profile_analysis"))
            execute_id = "f13-revolve-execute-" + uuid.uuid4().hex
            executed_op, executed, _ = execute_plan_once(execute_id, plan["plan_id"], case_id + " execute")
            if executed_op.get("operation_id") != execute_id or executed.get("plan_id") != plan["plan_id"]:
                raise AssertionError(f"{case_id} execute operation/plan provenance mismatch")
            case["execute_operation_id"] = executed_op.get("operation_id")
            case["execute_result"] = _redact(executed)
            case["direct_native_definition"] = _redact(
                _find_dict(executed, "revolve_definition") or
                _find_dict(executed, "revolve_feature_definition") or
                _find_dict(executed, "feature_definition"))
            native_definition = (_find_dict(executed, "revolve_definition") or
                                 _find_dict(executed, "revolve_feature_definition") or
                                 _find_dict(executed, "feature_definition"))
            case["direct_native_definition_validation"] = _assert_direct_definition(
                native_definition, request, case_id + " execute definition",
                expected_feature_id=executed.get("feature_id"))
            requested_mode = arguments.get("mode")
            requested_output = arguments.get("profile_output", "solid")
            if (executed.get("mode") is not None and
                    str(executed.get("mode")).casefold() != str(requested_mode).casefold()):
                raise AssertionError(f"{case_id} result mode differs from the request")
            if (executed.get("profile_output") is not None and
                    str(executed.get("profile_output")).casefold() != str(requested_output).casefold()):
                raise AssertionError(f"{case_id} result profile_output differs from the request")
            if executed.get("mutation_state") != "APPLIED":
                raise AssertionError(f"{case_id} execute did not report mutation_state=APPLIED")
            case["selected_counts"] = _redact(_assert_profile_selection(executed, expected_curve_count,
                                                                          case_id, sheet=expected_sheet_area is not None))
            verify_id = "f13-revolve-verify-" + uuid.uuid4().hex
            case["fresh_verify_execute_operation_id"] = execute_id
            verified_op, verified, _ = operation_call("nx_verify_part", {
                "operation_id": verify_id, **credentials, "execute_operation_id": execute_id},
                case_id + " explicit fresh Verify")
            if verified_op.get("operation_id") != verify_id:
                raise AssertionError(f"{case_id} fresh Verify operation ID mismatch")
            case["verify_result"] = _redact(verified)
            _assert_verified(verified, case_id)
            if verified.get("execute_operation_id") != execute_id:
                raise AssertionError(f"{case_id} fresh Verify refers to another execute operation")
            if verified.get("plan_id") != plan["plan_id"]:
                raise AssertionError(f"{case_id} fresh Verify refers to another plan")
            verification_evidence = verified.get("evidence")
            if not isinstance(verification_evidence, dict):
                raise AssertionError(f"{case_id} fresh Verify omitted direct evidence")
            case["verification_provenance"] = {
                "execute_operation_id": execute_id,
                "verify_operation_id": verified_op.get("operation_id"),
                "plan_id": plan["plan_id"],
                "feature_id": verification_evidence.get("feature_id"),
                "body_ids": verification_evidence.get("body_ids"),
                "strict_volume_accuracy": VERIFY_MASS_PROPERTY_ACCURACY,
                "strict_face_area_accuracy": VERIFY_FACE_PROPERTY_ACCURACY,
            }
            fresh_definition = _verified_revolve_definition(verified, case_id + " fresh Verify")
            case["fresh_verify_native_definition"] = _redact(fresh_definition)
            case["fresh_verify_native_definition_validation"] = _assert_direct_definition(
                fresh_definition, request, case_id + " fresh Verify definition",
                expected_feature_id=verification_evidence.get("feature_id"))
            case["verify_operation_id"] = verified_op.get("operation_id")
            inspection = inspect_part()
            case["direct_part_inspection"] = _redact(inspection)
            if expected_sheet_area is None:
                body_id = assert_direct_solid(
                    case, executed, verified, verification_evidence, inspection,
                    float(expected_volume), list(expected_box or []),
                    execute_id=execute_id, verify_id=str(verified_op.get("operation_id")),
                    plan_id=plan["plan_id"], target_id=target_id)
            else:
                body_id = assert_direct_sheet(
                    case, executed, verified, verification_evidence, inspection,
                    float(expected_sheet_area), execute_id=execute_id,
                    verify_id=str(verified_op.get("operation_id")), plan_id=plan["plan_id"])
                case["execute_surface_area_mm2_diagnostic"] = executed.get("surface_area_mm2")
                case["execute_expected_surface_area_mm2_diagnostic"] = executed.get("expected_surface_area_mm2")
            query = operation_get_twice(execute_id, executed_op, case_id)
            case["same_operation_queries"] = _redact(query)
            case.update(state="PASS", direct_body_id=body_id,
                        prepare_operation_id=prepared_op.get("operation_id"),
                        post_operation_inspection=_redact(inspection))
            persist()
            return executed, body_id, case
        except Exception as exc:
            record_failure(case, exc)
            raise

    def expect_rejection(case_id: str, tool: str, request: dict,
                         *, expected_error_fragments: tuple[str, ...] = ()) -> None:
        case = add_case(case_id, "Reject an invalid/stale F13 prepare read-only and prove direct part inventory is unchanged.",
                        {"request": _redact(request), "mutates": False})
        try:
            before = inspect_part()
            case["before_signature"] = _redact(_part_signature(before))
            payload, entry = raw_call(tool, request)
            entry["case_id"] = case_id
            error_code = entry.get("error_code") or _error_code(payload)
            failure_state = None
            if entry.get("is_error"):
                failure_state = "MCP_ERROR"
            elif isinstance(payload.get("operation_id"), str):
                terminal = poll_operation(payload, case_id + " rejection")
                case["terminal_operation"] = _redact(terminal)
                failure_state = terminal.get("state")
            rejected = entry.get("is_error") or failure_state in {"FAILED", "CANCELLED"}
            if not rejected:
                raise AssertionError(f"{case_id} expected a read-only rejection, observed state={failure_state!r}")
            if expected_error_fragments:
                rendered_error = json.dumps(_redact({"payload": payload,
                                                     "mcp_response": entry.get("mcp_response")}),
                                            ensure_ascii=False).casefold()
                if not any(fragment.casefold() in rendered_error for fragment in expected_error_fragments):
                    raise AssertionError(f"{case_id} error did not contain any expected reason {expected_error_fragments!r}")
            after = inspect_part()
            case["after_signature"] = _redact(_part_signature(after))
            if _part_signature(before) != _part_signature(after):
                raise AssertionError(f"{case_id} rejected prepare changed model inventory")
            case.update(state="PASS", error_code=error_code, raw_failing_response=_redact(entry.get("mcp_response")),
                        part_unchanged=True)
            persist()
        except Exception as exc:
            record_failure(case, exc)
            raise

    try:
        if args.part_path is not None:
            part_path = args.part_path.resolve(strict=True)
            model_root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
            if not part_path.is_relative_to(model_root) or part_path.suffix.casefold() != ".prt":
                raise ValueError("--part-path must be a saved .prt inside workspace/v2/models")
            metadata_path = ROOT / "workspace" / "v2" / "evidence" / (part_path.stem + ".json")
            if not metadata_path.is_file():
                raise FileNotFoundError(f"F13 fixture metadata is missing: {metadata_path}")
            metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
            if str(metadata.get("part_path", "")).casefold() != str(part_path).casefold():
                raise AssertionError("F13 fixture metadata path differs from --part-path")
            if metadata.get("saved") is not True or metadata.get("bridge_loaded") is not False:
                raise AssertionError("fixture must be saved and created without loading the bridge")
            report["part_path"] = str(part_path)
            report["fixture_metadata_path"] = str(metadata_path)
            report["fixture_metadata"] = metadata

        install_root = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        config_path = install_root / "mcp-config.json"
        env = os.environ.copy()
        env.update(PYTHONPATH=str(install_root / "app" / "src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        host_command = [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(config_path)]
        report["installed_host"] = {"install_root": str(install_root), "config_path": str(config_path),
                                     "command": host_command}
        process = subprocess.Popen(host_command, cwd=str(ROOT), env=env, stdin=subprocess.PIPE,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
        client = McpStdioClient(process, 60, max_line_bytes=MCP_RESPONSE_LINE_LIMIT_BYTES)
        report["configuration"]["mcp_response_line_limit_bytes"] = MCP_RESPONSE_LINE_LIMIT_BYTES
        report["initialize"] = _redact(client.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION, "capabilities": {},
            "clientInfo": {"name": "f13-installed-acceptance", "version": "1"}}))
        client.notify("notifications/initialized")
        server_info = report["initialize"].get("serverInfo")
        if not isinstance(server_info, dict) or not isinstance(server_info.get("version"), str):
            raise AssertionError("installed initialize omitted serverInfo.version")
        report["server_version"] = server_info["version"]
        if args.expected_server_version and report["server_version"] != args.expected_server_version:
            raise AssertionError(f"installed MCP version must be {args.expected_server_version}, got {report['server_version']}")

        tools_response = client.request("tools/list")
        tools = tools_response.get("tools")
        report["tools_list"] = _redact(tools_response)
        if not isinstance(tools, list):
            raise AssertionError("installed tools/list omitted tools")
        names = {item.get("name") for item in tools if isinstance(item, dict)}
        report["tool_names"] = sorted(names)
        report["tool_count"] = len(names)
        if len(names) != CONTRACT_TOOL_COUNT:
            raise AssertionError(f"locked F13 host should expose {CONTRACT_TOOL_COUNT} tools, got {len(names)}")
        required_tools = {"nx_ensure_ready", "nx_bridge_status", "nx_takeover", "nx_return",
                          "nx_get_capabilities", "nx_get_session", "nx_inspect_part", "nx_query_faces", "nx_inspect_sketch",
                          "nx_get_operation", "nx_prepare_sketch", "nx_prepare_revolve", "nx_execute_plan",
                          "nx_prepare_datum_plane", "nx_verify_part", "nx_save_copy"}
        if not required_tools <= names:
            raise AssertionError(f"installed tools/list omitted: {sorted(required_tools - names)}")
        report["revolve_tool_contract"] = _tool_contract(tools)
        report["face_tag_tool_contracts"] = _optional_face_tag_contract(tools)

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
            raise AssertionError("initial installed NX session must be HUMAN_OWNED with writes unhalted")
        if str(status.get("in_flight")) != "0":
            raise AssertionError("initial installed NX session has an in-flight operation")
        report["nx_pid"] = status.get("pid")
        if args.ready:
            report["passed"] = True
            report["diagnostic_only"] = True
            return 0

        target_path = report["part_path"]
        takeover_request = {"expected_pid": status["pid"], "expected_part_path": target_path,
                            "artifact_scope": "saved_independent_copy", "authorize": True}
        for attempt in range(10):
            takeover, entry = raw_call("nx_takeover", takeover_request)
            if not entry.get("is_error") or _error_code(takeover) != "NX_BUSY":
                break
            report["takeover_busy_readiness_rejections"] = attempt + 1
            time.sleep(0.4)
        if not isinstance(takeover.get("ownership_id"), str) or not isinstance(takeover.get("binding_epoch"), int):
            raise AssertionError("nx_takeover omitted ownership credentials")
        credentials.update(ownership_id=takeover["ownership_id"], binding_epoch=takeover["binding_epoch"])
        report["takeover"] = _redact(takeover)
        capabilities = sync_call("nx_get_capabilities", {})
        report["installed_capabilities"] = _redact(capabilities)
        if capabilities.get("model_revolve_adapter_available") is not True:
            raise AssertionError("installed capabilities do not report a loaded Revolve adapter")
        if capabilities.get("model_revolve_write_enabled") is not True:
            raise AssertionError("installed capabilities do not enable Revolve writes")
        if "operation.prepare_revolve" not in capabilities.get("available_operations", []):
            raise AssertionError("installed capabilities omitted operation.prepare_revolve")
        initial_part = inspect_part()
        if _inspect_path(initial_part).as_posix().casefold() != Path(target_path).resolve().as_posix().casefold():
            raise AssertionError("nx_inspect_part did not bind the isolated F13 fixture")
        for field in ("features", "sketches", "bodies"):
            if initial_part.get(field):
                raise AssertionError(f"F13 fixture must start empty; {field}={initial_part.get(field)!r}")
        report["initial_part_inspection"] = _redact(initial_part)

        # A free tilted Sketch frame is chosen so local +X is radial and local
        # +Y lies along the oblique world axis. Thus the requested axis line is
        # exactly in the sketch plane and the oracle uses the same independent
        # orthonormal frame inputs.
        main_geometry = [
            {"id": "main-radial-low", "type": "line", "start_mm": [10.0, 0.0], "end_mm": [14.0, 0.0]},
            {"id": "main-axial-high", "type": "line", "start_mm": [14.0, 0.0], "end_mm": [14.0, 8.0]},
            {"id": "main-radial-high", "type": "line", "start_mm": [14.0, 8.0], "end_mm": [10.0, 8.0]},
            {"id": "main-axial-low", "type": "line", "start_mm": [10.0, 8.0], "end_mm": [10.0, 0.0]},
        ]
        main_sketch_id, _ = prepare_sketch("F13_installed_tilted_solid_section",
                                          AXIS_POINT_MM, main_geometry, 4)

        stale_request = {"operation_id": "f13-stale-" + uuid.uuid4().hex, **credentials,
                         "revision_id": "f13-stale-revision-" + uuid.uuid4().hex,
                         "sketch_id": main_sketch_id, "mode": "create",
                         "axis_point_mm": AXIS_POINT_MM, "axis_direction": AXIS_DIRECTION_RAW,
                         "start_angle_deg": 30.0, "end_angle_deg": -150.0}
        expect_rejection("F13_stale_revision_rejected_read_only", "nx_prepare_revolve", stale_request,
                         expected_error_fragments=("stale", "revision"))

        invalid_angle = {"operation_id": "f13-invalid-angle-" + uuid.uuid4().hex, **credentials,
                         "revision_id": refresh_revision(), "sketch_id": main_sketch_id,
                         "mode": "create", "axis_point_mm": AXIS_POINT_MM,
                         "axis_direction": AXIS_DIRECTION_RAW, "start_angle_deg": 30.0,
                         "end_angle_deg": 30.0}
        expect_rejection("F13_equal_angle_rejected_read_only", "nx_prepare_revolve", invalid_angle,
                         expected_error_fragments=("angle", "span"))
        invalid_mode = {"operation_id": "f13-invalid-mode-" + uuid.uuid4().hex, **credentials,
                        "revision_id": refresh_revision(), "sketch_id": main_sketch_id,
                        "mode": "intersect", "axis_point_mm": AXIS_POINT_MM,
                        "axis_direction": AXIS_DIRECTION_RAW, "start_angle_deg": 30.0,
                        "end_angle_deg": -150.0}
        expect_rejection("F13_invalid_mode_rejected_read_only", "nx_prepare_revolve", invalid_mode)
        sheet_boolean = {"operation_id": "f13-invalid-sheet-boolean-" + uuid.uuid4().hex, **credentials,
                         "revision_id": refresh_revision(), "sketch_id": main_sketch_id,
                         "mode": "unite", "profile_output": "sheet", "target_body_id": "unused-target",
                         "axis_point_mm": AXIS_POINT_MM, "axis_direction": AXIS_DIRECTION_RAW,
                         "start_angle_deg": 30.0, "end_angle_deg": -150.0}
        expect_rejection("F13_sheet_boolean_rejected_read_only", "nx_prepare_revolve", sheet_boolean)

        main_execute, main_body_id, main_case = run_revolve(
            "F13_signed_partial_solid_create", main_sketch_id,
            {"mode": "create", "axis_point_mm": AXIS_POINT_MM,
             "axis_direction": AXIS_DIRECTION_RAW, "start_angle_deg": 30.0,
             "end_angle_deg": -150.0}, 4,
            expected_volume=expected_main_volume, expected_box=expected_main_bounds)
        if not isinstance(main_body_id, str) or not main_body_id:
            raise AssertionError("signed partial solid did not return a direct target body ID")

        sheet_geometry = [{"id": "sheet-open-line", "type": "line",
                           "start_mm": [18.0, 2.0], "end_mm": [18.0, 8.0]}]
        sheet_sketch_id, _ = prepare_sketch("F13_installed_open_sheet_section", AXIS_POINT_MM,
                                            sheet_geometry, 1)
        _, sheet_body_id, _ = run_revolve(
            "F13_explicit_sheet_create", sheet_sketch_id,
            {"mode": "create", "profile_output": "sheet", "axis_point_mm": AXIS_POINT_MM,
             "axis_direction": AXIS_DIRECTION_RAW, "start_angle_deg": 0.0,
             "end_angle_deg": 90.0}, 1, expected_sheet_area=expected_sheet_area)

        tool_geometry = [
            {"id": "unite-tool-radial-low", "type": "line", "start_mm": [12.0, 4.0], "end_mm": [16.0, 4.0]},
            {"id": "unite-tool-axial-high", "type": "line", "start_mm": [16.0, 4.0], "end_mm": [16.0, 12.0]},
            {"id": "unite-tool-radial-high", "type": "line", "start_mm": [16.0, 12.0], "end_mm": [12.0, 12.0]},
            {"id": "unite-tool-axial-low", "type": "line", "start_mm": [12.0, 12.0], "end_mm": [12.0, 4.0]},
        ]
        tool_sketch_id, _ = prepare_sketch("F13_installed_boolean_tool_section",
                                          AXIS_POINT_MM, tool_geometry, 4)
        before_unite = inspect_part()
        before_non_target = {_structural_body_signature(body).get("journal_id"): _structural_body_signature(body)
                             for body in _body_records(before_unite)
                             if body.get("journal_id") != main_body_id}
        _, _, unite_case = run_revolve(
            "F13_explicit_unite_target", tool_sketch_id,
            {"mode": "unite", "profile_output": "solid", "target_body_id": main_body_id,
             "axis_point_mm": AXIS_POINT_MM, "axis_direction": AXIS_DIRECTION_RAW,
             "start_angle_deg": 30.0, "end_angle_deg": -150.0}, 4,
            expected_volume=expected_union_volume, expected_box=expected_union_bounds,
            target_id=main_body_id)
        after_unite = unite_case.get("direct_part_inspection")
        if not isinstance(after_unite, dict):
            raise AssertionError("Boolean fresh inspection evidence is missing")
        after_non_target = {_structural_body_signature(body).get("journal_id"): _structural_body_signature(body)
                            for body in _body_records(after_unite)
                            if body.get("journal_id") != main_body_id}
        if before_non_target != after_non_target:
            raise AssertionError("explicit Boolean Unite changed an unrelated sheet/body signature")
        unite_case["non_target_preservation"] = {
            "target_body_id": main_body_id,
            "preserved_non_target_count": len(before_non_target),
            "before": list(before_non_target.values()),
            "after": list(after_non_target.values()),
            "identical": True,
        }

        # Installed face-tag coverage is read-only. Native G01 owns actual
        # support_face_tag submission/execution; this MCP case only verifies
        # that tools/list, nx_query_faces, and nx_inspect_part expose the
        # current session Tags and preserve the body's duplicate-capable
        # JournalIdentifier inventory without losing or reordering records.
        face_case = add_case(
            "F13_installed_face_tag_query_inventory",
            "Verify installed face-tag schemas and read-only face candidate/body-reference results after the successful Boolean Unite.",
            {"body_id": main_body_id, "selector": "all_planar", "mutates": False,
             "support_face_tag_submitted": False},
        )
        try:
            before_face_signature = _part_signature(after_unite)
            before_face_identity = _body_face_identity_signature(after_unite, main_body_id)
            face_case["body_face_identity_before_query"] = _redact(before_face_identity)
            query_arguments = {**credentials, "revision_id": refresh_revision(),
                               "selector": "all_planar", "body_id": main_body_id}
            query_result, query_entry = raw_call("nx_query_faces", query_arguments)
            face_case["nx_query_faces_case_id"] = query_entry.get("case_id")
            face_case["nx_query_faces_mcp_response"] = _redact(query_entry.get("mcp_response"))
            face_case["nx_query_faces_result"] = _redact(query_result)
            if query_entry.get("is_error"):
                raise AssertionError(f"nx_query_faces failed: {_error_code(query_result) or query_entry.get('error_code') or query_result}")

            inspection_result, inspect_entry = raw_call("nx_inspect_part", credentials)
            face_case["nx_inspect_part_case_id"] = inspect_entry.get("case_id")
            face_case["nx_inspect_part_mcp_response"] = _redact(inspect_entry.get("mcp_response"))
            face_case["nx_inspect_part_result"] = _redact(inspection_result)
            if inspect_entry.get("is_error"):
                raise AssertionError(f"nx_inspect_part failed: {_error_code(inspection_result) or inspect_entry.get('error_code') or inspection_result}")

            face_case["body_face_tag_inventory"] = _redact(
                _assert_face_tag_inventory(query_result, inspection_result, main_body_id))
            after_face_identity = _body_face_identity_signature(inspection_result, main_body_id)
            face_case["body_face_identity_after_query"] = _redact(after_face_identity)
            if before_face_identity != after_face_identity:
                raise AssertionError("nx_query_faces/nx_inspect_part changed body face_ids or face_refs")
            after_face_signature = _part_signature(inspection_result)
            face_case["model_signature_before_read_only_face_query"] = _redact(before_face_signature)
            face_case["model_signature_after_read_only_face_query"] = _redact(after_face_signature)
            if before_face_signature != after_face_signature:
                raise AssertionError("nx_query_faces/nx_inspect_part changed the direct structural model signature")
            face_case.update(state="PASS", model_signature_unchanged=True,
                             support_face_tag_submitted=False,
                             note="Current-session face Tags are read-only inventory evidence; native G01 covers Tag-based Prepare/Execute and SaveCopy refusal.")
            persist()
        except Exception as exc:
            record_failure(face_case, exc)
            raise

        # SaveCopy/reopen changes the owned binding. Compare one-to-one body
        # type, topology counts, and exact bounds; legacy inspected volume/area
        # values remain in the report as diagnostics and are not roundtrip oracles.
        before_save = inspect_part()
        before_bodies = [_fingerprint(body) for body in _body_records(before_save)]
        copy_path = Path(target_path).parent / ("F13-mcp-" + uuid.uuid4().hex + "-roundtrip.prt")
        if copy_path.exists():
            raise FileExistsError("unique F13 SaveCopy target already exists")
        save_case = add_case("F13_save_copy_reopen", "SaveCopy and reopen only the owned copy, then compare body type, topology, and exact bounds.",
                             {"source_part": target_path, "target_path": str(copy_path),
                              "body_count": len(before_bodies)})
        save_terminal, saved, _ = operation_call("nx_save_copy", {
            "operation_id": "f13-save-" + uuid.uuid4().hex, **credentials,
            "target_path": str(copy_path), "reopen": True}, "F13 SaveCopy/reopen")
        new_owner = saved.get("new_ownership_id")
        new_epoch = saved.get("new_binding_epoch")
        if not isinstance(new_owner, str) or not isinstance(new_epoch, int):
            raise AssertionError("nx_save_copy omitted new ownership credentials")
        credentials.update(ownership_id=new_owner, binding_epoch=new_epoch)
        if saved.get("saved") is not True or saved.get("reopened") is not True:
            raise AssertionError("nx_save_copy did not confirm saved=true and reopened=true")
        checks = saved.get("checks")
        if not isinstance(checks, dict) or set(checks) != {"associations", "expression", "geometry", "history"}:
            raise AssertionError("nx_save_copy omitted its four roundtrip checks")
        if any(not isinstance(item, dict) or item.get("status") != "VERIFIED" for item in checks.values()):
            raise AssertionError("nx_save_copy contains a non-VERIFIED roundtrip check")
        reopened = inspect_part()
        if _inspect_path(reopened).as_posix().casefold() != copy_path.resolve().as_posix().casefold():
            raise AssertionError("new binding did not reopen the requested SaveCopy path")
        after_bodies = [_fingerprint(body) for body in _body_records(reopened)]
        _assert_roundtrip(before_bodies, after_bodies)
        save_case.update(state="PASS", save_terminal=_redact(save_terminal), save_result=_redact(saved),
                         reopened_part_inspection=_redact(reopened), body_signatures_before=before_bodies,
                         body_signatures_after=after_bodies)
        report["saved_path"] = saved.get("saved_path") or str(copy_path)
        persist()

        returned, _ = raw_call("nx_return", credentials)
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("nx_return did not confirm safe HUMAN_OWNED state")
        credentials.clear()
        final_status, _ = raw_call("nx_bridge_status", {})
        if (final_status.get("ownership_state") != "HUMAN_OWNED"
                or final_status.get("writes_halted") is not False
                or str(final_status.get("in_flight")) != "0"):
            raise AssertionError("final bridge status is not HUMAN_OWNED, idle, and writes-unhalted")
        report["safe_return"] = _redact(returned)
        report["final_bridge_status"] = _redact(final_status)
        save_case["safe_return"] = _redact(returned)
        save_case["final_bridge_status"] = _redact(final_status)
        report["passed"] = True
        return 0
    except Exception as exc:
        report["error"] = repr(exc)
        if active_case is None or active_case.get("state") != "RUNNING":
            active_case = {"case_id": "F13_installed_harness", "state": "RUNNING"}
            report["cases"].append(active_case)
        failed_case_id = active_case.get("case_id")
        record_failure(active_case, exc)
        for pending_case in report["cases"]:
            if pending_case is not active_case and pending_case.get("state") == "RUNNING":
                pending_case.update(state="SKIPPED", skip_reason=f"Harness stopped after failure in {failed_case_id}.",
                                    retest_criterion="Rerun this case on a new unique F13 fixture after the blocking failure is corrected.")
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
            report["failure_safe_return_skipped"] = "operation outcome UNKNOWN; no ownership or model write was attempted"
        return 1
    finally:
        report["completed_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        if client is not None:
            try:
                client.close()
            except Exception as close_error:
                report["client_close_error"] = repr(close_error)
        counts = {}
        for case in report["cases"]:
            key = case.get("state", "UNKNOWN")
            counts[key] = counts.get(key, 0) + 1
        report["case_counts"] = {
            "selected": len(report["cases"]),
            "passed": counts.get("PASS", 0),
            "failed": counts.get("FAIL", 0),
            "skipped": counts.get("SKIPPED", 0),
            "UNKNOWN": len({event.get("case_id", event.get("label"))
                            for event in report["unknown_events"]}),
            "by_state": counts,
            "by_layer": {"installed-MCP integration": dict(counts)},
        }
        persist()
        print(json.dumps({key: report[key] for key in
                          ("passed", "mode", "server_version", "nx_pid", "part_path", "saved_path", "error")
                          if key in report}, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
