"""Installed MCP smoke for F11 sketch reference-state conversion.

The smoke starts only the installed MCP stdio child. It never calls
``nx_ensure_ready``, starts/stops NX, or saves over the supplied fixture. Model
mode requires a fresh standalone millimetre part and matching fixture JSON
created by ``f11-create-mcp-fixture.py``. Each write uses a unique operation ID;
an UNKNOWN outcome is queried only by that same ID and is never replayed.

This single installed-session sample is functional evidence for the tested
NX/MCP build only. It does not establish reliability, metrology accuracy, or
cross-version behavior.
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

ROOT = Path(__file__).resolve().parents[2]
TOOLS_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS_DIR))
from f01_f02_mcp_client import McpStdioClient  # noqa: E402

PROTOCOL_VERSION = "2025-11-25"
EXPECTED_SERVER_VERSION = "0.11.0"
EXPECTED_TOOL_COUNT = 31
EXPECTED_TOOLS = {
    "nx_ensure_ready", "nx_get_capabilities", "nx_bridge_status", "nx_bridge_ping",
    "nx_takeover", "nx_return", "nx_get_session", "nx_inspect_part", "nx_query_faces",
    "nx_inspect_sketch", "nx_get_operation", "nx_cancel_operation", "nx_prepare_datum_plane",
    "nx_prepare_sketch", "nx_prepare_sketch_corner", "nx_edit_sketch_corner",
    "nx_prepare_sketch_offset", "nx_edit_sketch_offset", "nx_prepare_sketch_pattern",
    "nx_edit_sketch_pattern", "nx_prepare_sketch_projection", "nx_edit_sketch_projection",
    "nx_prepare_sketch_reference_conversion", "nx_edit_sketch_reference_conversion",
    "nx_prepare_extrude", "nx_prepare_thread", "nx_prepare_helical_groove",
    "nx_prepare_operation", "nx_execute_plan", "nx_verify_part", "nx_save_copy",
}
REFERENCE_CONVERSION_TOOLS = (
    "nx_prepare_sketch_reference_conversion", "nx_edit_sketch_reference_conversion",
)
REFERENCE_CONVERSION_FIELDS = {
    "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
    "object_ids", "target_state",
}
FIXTURE_SCHEMA = "f11-native-mcp-fixture-v1"
POSITION_TOLERANCE_MM = 1.0e-6
NUMERIC_TOLERANCE = 1.0e-9
OPERATION_TIMEOUT_SECONDS = 180.0
POLL_INTERVAL_SECONDS = 0.2
MCP_RESPONSE_LINE_LIMIT_BYTES = 16 * 1024 * 1024
PRIVATE_KEY_PARTS = (
    "token", "secret", "authorization", "ownership_id", "credential",
    "api_key", "access_key", "private_key", "password",
)
TERMINAL_STATES = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
STATE_KEYS = {"reference_status", "reference_status_value", "reference_curve_solver_semantics",
              "dimension_state", "is_driving", "solver_participation"}
IDENTITY_KEYS = {"journal_id", "semantic_key"}


def _redact(value: object) -> object:
    if isinstance(value, dict):
        return {
            key: ("[REDACTED]" if any(part in str(key).casefold() for part in PRIVATE_KEY_PARTS)
                  else _redact(child))
            for key, child in value.items()
        }
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


def _json_no_duplicate_keys(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"fixture JSON contains duplicate key {key!r}")
        result[key] = value
    return result


def _reject_json_constant(value: str) -> object:
    raise ValueError(f"fixture JSON contains unsupported non-finite number {value}")


def _canonical_path(value: str | Path) -> str:
    return Path(value).resolve(strict=False).as_posix().casefold()


def _finite_number(value: object, label: str) -> float:
    if isinstance(value, bool):
        raise AssertionError(f"{label} must be a finite number")
    try:
        result = float(value)
    except (TypeError, ValueError, OverflowError) as exc:
        raise AssertionError(f"{label} must be a finite number") from exc
    if not math.isfinite(result):
        raise AssertionError(f"{label} must be a finite number")
    return result


def _numeric_text(value: object, label: str) -> float:
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return _finite_number(value, label)
    if not isinstance(value, str):
        raise AssertionError(f"{label} must be a numeric value or RHS string")
    match = re.fullmatch(r"\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)\s*(?:mm)?\s*", value,
                         flags=re.IGNORECASE)
    if match is None:
        raise AssertionError(f"{label} is not an explicitly numeric millimetre value")
    return _finite_number(match.group(1), label)


def _in_flight_is_idle(value: object) -> bool:
    """Accept the native integer and string encodings observed for an idle bridge only."""
    if type(value) is int:
        return value == 0
    if type(value) is str:
        return value == "0"
    return False


def _close(left: object, right: object, tolerance: float = NUMERIC_TOLERANCE) -> bool:
    try:
        return abs(_finite_number(left, "left value") - _finite_number(right, "right value")) <= tolerance
    except AssertionError:
        return False


def _payload(response: dict, label: str) -> dict:
    value = response.get("structuredContent")
    if isinstance(value, dict):
        return value
    for item in response.get("content", []):
        if isinstance(item, dict) and isinstance(item.get("text"), str):
            try:
                decoded = json.loads(item["text"])
            except ValueError:
                continue
            if isinstance(decoded, dict):
                return decoded
    raise AssertionError(f"{label} returned no structured payload")


def _walk(value: object):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from _walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from _walk(child)
    elif isinstance(value, str):
        try:
            decoded = json.loads(value)
        except (TypeError, ValueError):
            return
        if isinstance(decoded, (dict, list)):
            yield from _walk(decoded)


def _error_code(value: object) -> str | None:
    for item in _walk(value):
        for key in ("code", "error_code"):
            if isinstance(item.get(key), str):
                return item[key]
    return None


def _operation_state(record: dict) -> str | None:
    if isinstance(record.get("state"), str):
        return record["state"]
    operation = record.get("operation")
    if isinstance(operation, dict) and isinstance(operation.get("state"), str):
        return operation["state"]
    return None


def _operation_id(record: dict) -> str | None:
    operation = record.get("operation") if isinstance(record.get("operation"), dict) else {}
    for value in (record.get("operation_id"), operation.get("operation_id"), operation.get("id")):
        if isinstance(value, str) and value:
            return value
    return None


def _operation_result(record: dict) -> dict:
    operation = record.get("operation") if isinstance(record.get("operation"), dict) else record
    result = operation.get("result") if isinstance(operation, dict) else None
    if isinstance(result, dict):
        return result
    result = record.get("result")
    return result if isinstance(result, dict) else {}


def _check_reference_conversion_schemas(tools: list[dict]) -> dict:
    by_name = {item.get("name"): item for item in tools if isinstance(item, dict)}
    checked = []
    properties_by_name = {}
    for name in REFERENCE_CONVERSION_TOOLS:
        tool = by_name.get(name)
        if not isinstance(tool, dict):
            raise AssertionError(f"installed MCP omitted {name}")
        schema = tool.get("inputSchema")
        if (not isinstance(schema, dict) or schema.get("type") != "object"
                or set(schema.get("required", [])) != REFERENCE_CONVERSION_FIELDS
                or schema.get("additionalProperties") is not False):
            raise AssertionError(f"{name} does not enforce the exact F11 request fields")
        properties = schema.get("properties")
        if not isinstance(properties, dict) or set(properties) != REFERENCE_CONVERSION_FIELDS:
            raise AssertionError(f"{name} schema properties differ from the exact F11 request")
        exact_string_schemas = {
            "operation_id": {"type": "string", "minLength": 1, "maxLength": 128},
            "ownership_id": {"type": "string", "minLength": 1},
            "revision_id": {"type": "string", "minLength": 1},
            "sketch_id": {"type": "string", "minLength": 1},
        }
        for field, expected_schema in exact_string_schemas.items():
            if properties.get(field) != expected_schema:
                raise AssertionError(f"{name} {field} schema differs from the frozen F11 field contract")
        if properties.get("binding_epoch") != {"type": "integer", "minimum": 1}:
            raise AssertionError(f"{name} binding_epoch must use the exact positive-integer schema")
        object_ids = properties.get("object_ids")
        if (not isinstance(object_ids, dict) or object_ids.get("type") != "array"
                or object_ids.get("minItems") != 1 or object_ids.get("maxItems") != 256
                or object_ids.get("uniqueItems") is not True
                or object_ids.get("items", {}).get("type") != "string"
                or object_ids.get("items", {}).get("minLength") != 1):
            raise AssertionError(f"{name} object_ids must be 1-256 unique nonempty strings")
        if properties.get("target_state", {}).get("enum") != ["reference", "active"]:
            raise AssertionError(f"{name} target_state must be exactly reference or active")
        if properties.get("object_ids", {}).get("items") != {"type": "string", "minLength": 1}:
            raise AssertionError(f"{name} object_ids items schema differs from the exact nonempty-string contract")
        if properties.get("target_state") != {"type": "string", "enum": ["reference", "active"]}:
            raise AssertionError(f"{name} target_state schema differs from the exact F11 contract")
        annotation_key = "readOnlyHint" if name.startswith("nx_prepare_") else "destructiveHint"
        if tool.get("annotations", {}).get(annotation_key) is not True:
            raise AssertionError(f"{name} omitted its {annotation_key} annotation")
        checked.append(name)
        properties_by_name[name] = properties
    if properties_by_name[REFERENCE_CONVERSION_TOOLS[0]] != properties_by_name[REFERENCE_CONVERSION_TOOLS[1]]:
        raise AssertionError("prepare and edit F11 schemas differ")
    return {"checked_tools": checked, "required_fields": sorted(REFERENCE_CONVERSION_FIELDS),
            "additional_properties": False, "max_object_ids": 256}


def _check_reference_conversion_capability(capabilities: dict) -> dict:
    native = capabilities.get("model_native_geometry_support")
    feature = native.get("sketch_reference_conversion") if isinstance(native, dict) else None
    if not isinstance(feature, dict):
        raise AssertionError("capabilities omitted model_native_geometry_support.sketch_reference_conversion")
    expected_fields = [
        "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
        "object_ids", "target_state",
    ]
    if (feature.get("prepare_operation") != "operation.prepare_sketch_reference_conversion"
            or feature.get("edit_operation") != "operation.edit_sketch_reference_conversion"):
        raise AssertionError("F11 capability operation routes differ from the frozen contract")
    if feature.get("request_fields") != expected_fields:
        raise AssertionError("F11 capability request_fields differ from the frozen contract")
    if feature.get("supported_dimension_types") != ["HorizontalDim", "VerticalDim"]:
        raise AssertionError("F11 capability must list only HorizontalDim and VerticalDim")
    if str(feature.get("dimension_unit", "")).casefold() != "mm; unsupported dimensions are rejected until their native unit mapping is verified".casefold():
        raise AssertionError("F11 capability dimension unit is not the frozen millimetre scope")
    target_states = feature.get("target_state")
    if target_states != {
            "reference": {"curve": "NX UF Sketch reference status Reference (0)",
                          "dimension": "SketchDimensionalConstraint.DimensionState Reference; measurement only, not driving"},
            "active": {"curve": "NX UF Sketch reference status Active (1)",
                       "dimension": "SketchDimensionalConstraint.DimensionState Driving"},
    }:
        raise AssertionError("F11 capability target-state semantics differ from the frozen NX state mapping")
    selection = str(feature.get("selection", "")).casefold()
    if not all(token in selection for token in ("1 through 256", "unique", "journal", "geometry[]", "constraints[]")):
        raise AssertionError("F11 capability must explain current unique curve/constraint Journal IDs")
    unsupported = str(feature.get("dimension_semantics", "")).casefold()
    if "computedsize" not in unsupported or "expression" not in unsupported:
        raise AssertionError("F11 capability omitted computed-size/expression synchronization semantics")
    if "mixed_state_selection" not in feature:
        raise AssertionError("F11 capability omitted mixed already-target selection semantics")
    return {"prepare_operation": feature["prepare_operation"], "edit_operation": feature["edit_operation"],
            "request_fields": expected_fields, "supported_dimension_types": feature["supported_dimension_types"],
            "dimension_unit": feature["dimension_unit"],
            "mixed_state_selection": feature["mixed_state_selection"],
            "dimension_semantics": feature["dimension_semantics"]}


def validate_fixture_document(value: object) -> dict:
    if not isinstance(value, dict) or value.get("schema") != FIXTURE_SCHEMA:
        raise ValueError("fixture must use the f11-native-mcp-fixture-v1 schema")
    path = value.get("part_path")
    if not isinstance(path, str) or not path.strip():
        raise ValueError("fixture omitted part_path")
    if str(value.get("units", "")).casefold() not in {"millimeter", "millimeters", "mm"}:
        raise ValueError("F11 smoke fixture must be millimetres")
    sketch_id = value.get("sketch_id")
    if not isinstance(sketch_id, str) or not sketch_id.strip():
        raise ValueError("fixture omitted sketch_id")
    frame = value.get("sketch_frame")
    if not isinstance(frame, dict) or not isinstance(frame.get("origin_mm"), list):
        raise ValueError("fixture omitted its native Sketch frame")
    curves = value.get("curves")
    dimensions = value.get("dimensions")
    if not isinstance(curves, list) or not isinstance(dimensions, list):
        raise ValueError("fixture curves and dimensions must be arrays")
    if {item.get("role") for item in curves if isinstance(item, dict)} != {
            "horizontal", "vertical", "witness", "partial_arc", "full_circle"}:
        raise ValueError("fixture curve role inventory is incomplete")
    if {item.get("role") for item in dimensions if isinstance(item, dict)} != {
            "horizontal", "vertical", "unsupported_radial"}:
        raise ValueError("fixture dimension role inventory is incomplete")
    identities = []
    for kind, rows in (("curve", curves), ("dimension", dimensions)):
        for index, row in enumerate(rows):
            if not isinstance(row, dict):
                raise ValueError(f"fixture {kind}[{index}] must be an object")
            jid, tag = row.get("journal_id"), row.get("tag")
            if not isinstance(jid, str) or not jid.strip() or not isinstance(tag, str) or not tag.strip():
                raise ValueError(f"fixture {kind}[{index}] omitted Journal ID or Tag")
            identities.append(jid)
    if len(identities) != len(set(identities)):
        raise ValueError("fixture object Journal IDs must be unique")
    dimensions_by_role = {item["role"]: item for item in dimensions}
    expression_names = set()
    for role in ("horizontal", "vertical"):
        dimension = dimensions_by_role[role]
        for field in ("expression_tag", "expression_name", "expression_rhs", "expression_unit"):
            if not isinstance(dimension.get(field), str) or not dimension[field].strip():
                raise ValueError(f"fixture {role} supported dimension omitted {field}")
        if dimension.get("expression_unit") != "mm":
            raise ValueError(f"fixture {role} supported dimension must use mm")
        if dimension["expression_name"] in expression_names:
            raise ValueError("fixture supported dimensions must use unique expressions")
        expression_names.add(dimension["expression_name"])
        flags = dimension.get("expression_flags")
        if not isinstance(flags, dict) or not flags or any(type(flag) is not bool for flag in flags.values()):
            raise ValueError(f"fixture {role} supported dimension omitted boolean expression_flags")
        _finite_number(dimension.get("computed_size_mm"), f"fixture {role} ComputedSize")
    unsupported_radial = dimensions_by_role["unsupported_radial"]
    if (unsupported_radial.get("constraint_type") != "RadiusDim"
            or unsupported_radial.get("dimension_state") != "Reference"):
        raise ValueError("fixture unsupported_radial must be a Reference RadiusDim")
    if any(unsupported_radial.get(field) is not None for field in (
            "expression_tag", "expression_name", "expression_rhs", "expression_value", "expression_unit",
            "expression_flags")):
        raise ValueError("fixture unsupported_radial must explicitly report its absent reference expression/flags as null")
    if not isinstance(unsupported_radial.get("geometry_refs"), list) or not unsupported_radial["geometry_refs"]:
        raise ValueError("fixture unsupported_radial must retain its geometry operand association")
    _finite_number(unsupported_radial.get("computed_size_mm"), "fixture unsupported_radial ComputedSize")
    conditions = value.get("fixture_conditions")
    if (not isinstance(conditions, dict) or conditions.get("vertical_dimension_state") != "Reference"
            or not _close(conditions.get("vertical_expression_value_mm"), 8.0)
            or not _close(conditions.get("vertical_computed_size_mm"), 11.0)
            or conditions.get("radial_dimension_type") != "RadiusDim"):
        raise ValueError("fixture must contain a Reference VerticalDim with RHS 8 mm and ComputedSize 11 mm")
    return value


def load_fixture_json(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as stream:
        value = json.load(stream, object_pairs_hook=_json_no_duplicate_keys,
                          parse_constant=_reject_json_constant)
    return validate_fixture_document(value)


def _finite_vector(value: object, label: str) -> list[float]:
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        raise AssertionError(f"{label} must contain three finite numbers")
    return [_finite_number(component, label) for component in value]


def _distance(left: object, right: object, label: str) -> float:
    a, b = _finite_vector(left, label + " left"), _finite_vector(right, label + " right")
    return math.sqrt(math.fsum((a[i] - b[i]) ** 2 for i in range(3)))


def _curve_match(actual: dict, expected: dict) -> bool:
    if str(actual.get("kind", "")).upper() != str(expected.get("kind", "")).upper():
        return False
    try:
        if str(expected["kind"]).upper() == "LINE":
            direct = max(_distance(actual.get("start"), expected.get("start_mm"), "line start"),
                         _distance(actual.get("end"), expected.get("end_mm"), "line end"))
            reverse = max(_distance(actual.get("start"), expected.get("end_mm"), "line start"),
                          _distance(actual.get("end"), expected.get("start_mm"), "line end"))
            return min(direct, reverse) <= POSITION_TOLERANCE_MM
        center = _distance(actual.get("center"), expected.get("center_mm"), "arc center")
        radius = abs(_finite_number(actual.get("radius_mm"), "actual radius") -
                     _finite_number(expected.get("radius_mm"), "fixture radius"))
        if center > POSITION_TOLERANCE_MM or radius > POSITION_TOLERANCE_MM:
            return False
        actual_start, actual_end = actual.get("start"), actual.get("end")
        expected_start, expected_end = expected.get("start_mm"), expected.get("end_mm")
        direct = max(_distance(actual_start, expected_start, "arc start"),
                     _distance(actual_end, expected_end, "arc end"))
        reverse = max(_distance(actual_start, expected_end, "arc start"),
                      _distance(actual_end, expected_start, "arc end"))
        if min(direct, reverse) > POSITION_TOLERANCE_MM:
            return False
        full_actual = (actual.get("kind") == "ARC" and
                       abs(_finite_number(actual.get("end_angle_rad"), "arc end angle") -
                           _finite_number(actual.get("start_angle_rad"), "arc start angle")) >= 2.0 * math.pi - 1.0e-7)
        return full_actual is bool(expected.get("full_circle"))
    except (KeyError, TypeError, ValueError, AssertionError):
        return False


def _resolve_sketch(snapshot: dict, fixture: dict, label: str) -> dict:
    sketches = snapshot.get("sketches")
    if not isinstance(sketches, list):
        raise AssertionError(f"{label} nx_inspect_part omitted its Sketch inventory")
    matches = []
    for sketch in sketches:
        if not isinstance(sketch, dict):
            continue
        origin_ok = _distance(sketch.get("origin"), fixture["sketch_frame"].get("origin_mm"), label + " frame") <= POSITION_TOLERANCE_MM
        normal_ok = _distance(sketch.get("normal"), fixture["sketch_frame"].get("normal"), label + " normal") <= POSITION_TOLERANCE_MM
        if origin_ok and normal_ok:
            matches.append(sketch)
    if len(matches) != 1:
        raise AssertionError(f"{label} fixture frame resolved to {len(matches)} sketches; expected one")
    return matches[0]


_ASSOCIATION_NAME_BY_VALUE = {
    "0": "None", "1": "StartPoint", "2": "EndPoint",
}
_ASSOCIATION_ALIASES = {
    "0": "None", "None": "None", "NoneValue": "None",
    "1": "StartPoint", "StartPoint": "StartPoint",
    "2": "EndPoint", "EndPoint": "EndPoint",
}


def _fixture_dimension_operand_signature(expected: dict, fixture: dict) -> tuple:
    curves_by_jid = {curve["journal_id"]: curve for curve in fixture["curves"]}
    signature = []
    for operand in expected.get("geometry_refs", []):
        curve = curves_by_jid.get(operand.get("geometry_journal_id"))
        if curve is None:
            raise AssertionError(f"fixture dimension {expected.get('role')} operand does not map to a fixture curve")
        assoc_value = str(operand.get("association"))
        association = _ASSOCIATION_NAME_BY_VALUE.get(assoc_value)
        if association is None or operand.get("association_value") != 0:
            raise AssertionError(f"fixture dimension {expected.get('role')} has an unsupported operand association")
        signature.append((curve["role"], association))
    if not signature:
        raise AssertionError(f"fixture dimension {expected.get('role')} omitted operands")
    return tuple(sorted(signature))


def _inspected_dimension_operand_signature(candidate: dict, role_by_current_jid: dict[str, str]) -> tuple | None:
    references = candidate.get("geometry_refs")
    if not isinstance(references, list) or not references:
        return None
    signature = []
    for reference in references:
        if not isinstance(reference, str) or ":" not in reference:
            return None
        geometry_jid, raw_association = reference.rsplit(":", 1)
        role = role_by_current_jid.get(geometry_jid)
        association = _ASSOCIATION_ALIASES.get(raw_association)
        if role is None or association is None:
            return None
        signature.append((role, association))
    return tuple(sorted(signature))


def _snapshot_sketch(sketch: dict, fixture: dict, label: str) -> dict:
    geometry = sketch.get("geometry")
    constraints = sketch.get("constraints")
    if not isinstance(geometry, list) or not isinstance(constraints, list):
        raise AssertionError(f"{label} direct inspection omitted geometry or constraints")
    if len(geometry) != len(fixture["curves"]) or len(constraints) != len(fixture["dimensions"]):
        raise AssertionError(f"{label} object inventory differs from the isolated fixture")
    by_role: dict[str, dict] = {}
    ids_by_role: dict[str, str] = {}
    for expected in fixture["curves"]:
        matches = [item for item in geometry if isinstance(item, dict) and _curve_match(item, expected)]
        if len(matches) != 1:
            raise AssertionError(f"{label} curve {expected['role']} resolved to {len(matches)} records")
        by_role[expected["role"]] = matches[0]
        ids_by_role[expected["role"]] = matches[0].get("journal_id")
    role_by_current_jid = {journal_id: role for role, journal_id in ids_by_role.items()
                           if not role.startswith("dimension:")}
    dimension_by_role: dict[str, dict] = {}
    for expected in fixture["dimensions"]:
        expected_operands = _fixture_dimension_operand_signature(expected, fixture)
        matches = [item for item in constraints if isinstance(item, dict)
                   and item.get("class") == "DIMENSIONAL"
                   and item.get("constraint_type") == expected.get("constraint_type")
                   and _inspected_dimension_operand_signature(item, role_by_current_jid) == expected_operands
                   and (expected.get("expression_name") is None
                        or item.get("expression_name") == expected.get("expression_name"))]
        if len(matches) != 1:
            raise AssertionError(
                f"{label} dimension {expected['role']} resolved by type/operands/expression to {len(matches)} records"
            )
        if matches[0].get("constraint_type") != expected.get("constraint_type"):
            raise AssertionError(f"{label} dimension {expected['role']} changed constraint type")
        if expected.get("expression_name") is None:
            if expected["role"] != "unsupported_radial":
                raise AssertionError("only the unsupported reference radial fixture may have no AssociatedExpression")
            if any(matches[0].get(field) is not None for field in ("expression_name", "expression_value", "value", "expression_flags")):
                raise AssertionError("unsupported radial's absent AssociatedExpression was not preserved as absent")
        dimension_by_role[expected["role"]] = matches[0]
        ids_by_role["dimension:" + expected["role"]] = matches[0].get("journal_id")
    all_ids = list(ids_by_role.values())
    if any(not isinstance(item, str) or not item for item in all_ids) or len(all_ids) != len(set(all_ids)):
        raise AssertionError(f"{label} direct inspection did not produce unique current object JIDs")
    return {"sketch_id": sketch.get("journal_id"), "geometry": by_role,
            "dimensions": dimension_by_role, "ids_by_role": ids_by_role,
            "raw_geometry": geometry, "raw_constraints": constraints}


def _normalized_refs(value: object, ids_by_role: dict) -> object:
    if isinstance(value, list):
        return [_normalized_refs(item, ids_by_role) for item in value]
    if isinstance(value, dict):
        return {key: _normalized_refs(child, ids_by_role) for key, child in value.items()
                if key not in IDENTITY_KEYS and key not in STATE_KEYS}
    if isinstance(value, str) and ":" in value:
        object_id, suffix = value.split(":", 1)
        for role, journal_id in ids_by_role.items():
            if role.startswith("dimension:"):
                continue
            if journal_id == object_id:
                return role + ":" + suffix
    return value


def _object_payload(record: dict, ids_by_role: dict) -> dict:
    return _normalized_refs(record, ids_by_role)


def verify_object_transition(before: dict, after: dict, selected_roles: list[str], target_state: str,
                             *, sync_dimension_roles: list[str] | None = None) -> dict:
    """Check only the requested state fields and the single allowed promotion sync."""
    if target_state not in {"reference", "active"}:
        raise AssertionError("target_state must be reference or active")
    sync_dimension_roles = sync_dimension_roles or []
    selected = set(selected_roles)
    allowed_sync = set(sync_dimension_roles)
    valid_names = {"horizontal", "vertical", "witness", "partial_arc", "full_circle", "unsupported_radial"}
    normalized_selected = set()
    for role in selected:
        if role.startswith("dimension:"):
            normalized = role.split(":", 1)[1]
            if normalized not in {"horizontal", "vertical"}:
                raise AssertionError("selected dimension role is invalid")
            normalized_selected.add("dimension:" + normalized)
        elif role.startswith("curve:"):
            normalized = role.split(":", 1)[1]
            if normalized not in {"horizontal", "vertical", "witness", "partial_arc", "full_circle"}:
                raise AssertionError("selected curve role is invalid")
            normalized_selected.add("curve:" + normalized)
        elif role in valid_names:
            # Bare roles refer to curve geometry; dimension selection is explicit.
            normalized_selected.add("curve:" + role)
        else:
            raise AssertionError("selected object role is invalid")
    if not allowed_sync.issubset({role.split(":", 1)[1] for role in normalized_selected
                                 if role.startswith("dimension:")}) or not allowed_sync.issubset({
                                     "horizontal", "vertical"}):
        raise AssertionError("selected or sync dimension role is invalid")
    evidence = {"target_state": target_state, "objects": []}
    for role in before["geometry"]:
        old, new = before["geometry"][role], after["geometry"].get(role)
        if not isinstance(new, dict):
            raise AssertionError(f"curve {role} disappeared")
        if old.get("reference_status") not in {"Reference", "Active"} or new.get("reference_status") not in {"Reference", "Active"}:
            raise AssertionError(f"curve {role} has an unclassified native Reference status")
        old_state = "reference" if old.get("reference_status") == "Reference" else "active"
        new_state = "reference" if new.get("reference_status") == "Reference" else "active"
        expected_state = target_state if "curve:" + role in normalized_selected else old_state
        if new_state != expected_state:
            raise AssertionError(f"curve {role} state mismatch: expected {expected_state}, observed {new_state}")
        if _object_payload(old, before["ids_by_role"]) != _object_payload(new, after["ids_by_role"]):
            raise AssertionError(f"curve {role} geometry or non-state evidence changed")
        evidence["objects"].append({"role": role, "kind": "curve", "selected": "curve:" + role in normalized_selected,
                                    "state_before": old_state, "state_after": new_state,
                                    "geometry_preserved": True})
    for role in before["dimensions"]:
        old, new = before["dimensions"][role], after["dimensions"].get(role)
        if not isinstance(new, dict):
            raise AssertionError(f"dimension {role} disappeared")
        if role == "unsupported_radial":
            if ("dimension:unsupported_radial" in normalized_selected or role in allowed_sync):
                raise AssertionError("unsupported_radial cannot be treated as a supported conversion target")
            absent_fields = ("expression_tag", "expression_name", "expression_rhs", "expression_value",
                             "expression_unit", "expression_flags")
            if any(old.get(field) is not None or new.get(field) is not None for field in absent_fields):
                raise AssertionError("unsupported radial AssociatedExpression must remain absent/null")
        if old.get("dimension_state") not in {"Driving", "Reference"} or new.get("dimension_state") not in {"Driving", "Reference"}:
            raise AssertionError(f"dimension {role} has an unclassified native DimensionState")
        old_state = "active" if old.get("dimension_state") == "Driving" else "reference"
        new_state = "active" if new.get("dimension_state") == "Driving" else "reference"
        if old.get("is_driving") is not (old_state == "active") or new.get("is_driving") is not (new_state == "active"):
            raise AssertionError(f"dimension {role} is_driving readback disagrees with its DimensionState")
        expected_state = target_state if "dimension:" + role in normalized_selected else old_state
        if new_state != expected_state:
            raise AssertionError(f"dimension {role} state mismatch: expected {expected_state}, observed {new_state}")
        old_payload = _object_payload(old, before["ids_by_role"])
        new_payload = _object_payload(new, after["ids_by_role"])
        if role in allowed_sync:
            expected_size = _numeric_text(old.get("computed_size"), f"{role} pre-conversion ComputedSize")
            for field in ("value", "expression_value"):
                if not _close(_numeric_text(new.get(field), f"{role} {field}"), expected_size):
                    raise AssertionError(f"dimension {role} {field} did not synchronize to pre-conversion ComputedSize")
                old_payload.pop(field, None)
                new_payload.pop(field, None)
            old_payload.pop("computed_size", None)
            new_payload.pop("computed_size", None)
        if old_payload != new_payload:
            raise AssertionError(f"dimension {role} expression, operand association, or non-state evidence changed")
        evidence["objects"].append({"role": role, "kind": "dimension", "selected": "dimension:" + role in normalized_selected,
                                    "state_before": old_state, "state_after": new_state,
                                    "expression_rhs_sync_to_prestate_computed_size": role in allowed_sync})
    return {"passed": True, "selected_roles": sorted(normalized_selected), "sync_dimension_roles": sorted(allowed_sync),
            **evidence}


def verify_operation_sync_evidence(operation_result: dict, dimension_role: str,
                                   precomputed_size: object) -> dict:
    before_rows = operation_result.get("objects")
    after_rows = operation_result.get("objects_after")
    if not isinstance(before_rows, list) or not isinstance(after_rows, list):
        raise AssertionError("F11 edit result omitted per-object before/after evidence")
    before = [item for item in before_rows if isinstance(item, dict)
              and item.get("kind") == "dimension" and item.get("expression_name")]
    after_by_tag = {item.get("tag"): item for item in after_rows if isinstance(item, dict)}
    expected = _finite_number(precomputed_size, "pre-conversion ComputedSize")
    matched = [item for item in before if item.get("expression_name") == dimension_role]
    if len(matched) != 1:
        # Caller can pass the exact expression name, not the semantic role.
        raise AssertionError(f"operation evidence did not contain exactly one selected {dimension_role} expression")
    old = matched[0]
    new = after_by_tag.get(old.get("tag"))
    if not isinstance(new, dict):
        raise AssertionError("operation evidence lost the selected dimensional constraint identity")
    for side, record in (("before", old), ("after", new)):
        if not _close(_finite_number(record.get("computed_size_" + side), side + " ComputedSize"), expected):
            raise AssertionError("operation evidence ComputedSize differs from the recorded pre-conversion value")
    if not _close(_numeric_text(new.get("expression_right_hand_side_after"), "post-conversion expression RHS"), expected):
        raise AssertionError("selected dimension expression RHS did not become pre-conversion ComputedSize")
    if not _close(_finite_number(new.get("expression_value_after"), "post-conversion expression Value"), expected):
        raise AssertionError("selected dimension expression Value did not become pre-conversion ComputedSize")
    for field in ("expression_tag", "expression_name", "expression_unit"):
        if old.get(field) != new.get(field):
            raise AssertionError(f"selected dimension {field} changed during Reference-to-Active conversion")
    return {"passed": True, "expression_name": old.get("expression_name"),
            "computed_size_before_mm": expected,
            "expression_rhs_before": old.get("expression_right_hand_side_before"),
            "expression_rhs_after": new.get("expression_right_hand_side_after"),
            "expression_value_before": old.get("expression_value_before"),
            "expression_value_after": new.get("expression_value_after"),
            "expression_identity_preserved": True}


def _read_paths(args: argparse.Namespace) -> tuple[dict, Path, Path]:
    evidence_root = (ROOT / "workspace" / "v2" / "evidence").resolve(strict=True)
    model_root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
    fixture_path = args.fixture_json.resolve(strict=True)
    if not fixture_path.is_file() or not fixture_path.is_relative_to(evidence_root):
        raise ValueError("--fixture-json must be a file under workspace/v2/evidence")
    fixture = load_fixture_json(fixture_path)
    part_path = Path(fixture["part_path"]).resolve(strict=True)
    if not part_path.is_file() or not part_path.is_relative_to(model_root):
        raise ValueError("fixture part_path must be an existing .prt under workspace/v2/models")
    if part_path.suffix.casefold() != ".prt":
        raise ValueError("fixture part_path must identify a .prt model")
    output = args.output.resolve(strict=False)
    if output.exists() or output.suffix.casefold() != ".json" or not output.is_relative_to(evidence_root):
        raise ValueError("--output must be a new .json path under workspace/v2/evidence")
    return fixture, part_path, output


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture-json", required=True, type=Path,
                        help="fresh fixture JSON from f11-create-mcp-fixture.py")
    parser.add_argument("--output", required=True, type=Path,
                        help="new JSON evidence path under workspace/v2/evidence")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    run_id = uuid.uuid4().hex
    report: dict = {
        "test": "F11_installed_mcp_smoke", "run_id": run_id,
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "passed": False, "diagnostic_only": False,
        "objective": "Verify installed 0.11.0 MCP contract, explicit F11 prepare/edit lifecycle, mixed selected state conversion, object/expression preservation, Save Copy/reopen, and safe ownership return.",
        "traceability": ["F11 exact prepare/edit schema and capability", "F11 current Sketch curve/dimensional-constraint JIDs and unique selection",
                         "F11 Reference/Active bidirectional conversion", "F11 reference dimensions are measurement-only",
                         "F11 only selected supported dimension RHS/Value may synchronize to pre-conversion ComputedSize",
                         "F11 unsupported dimension rejection and all-target INVALID_ARGUMENT", "same-ID nx_get_operation and UNKNOWN no-replay",
                         "Save Copy/reopen conservation and safe ownership return"],
        "configuration": {"expected_mcp_version": EXPECTED_SERVER_VERSION,
                          "expected_tool_count": EXPECTED_TOOL_COUNT, "sample_size": 1,
                          "position_tolerance_mm": POSITION_TOLERANCE_MM,
                          "dimension_numeric_tolerance": NUMERIC_TOLERANCE,
                          "measurement_uncertainty": "digital NX model readback; no physical instrument used",
                          "calibration": "not applicable; no physical measurement instrument",
                          "reliability_or_compliance_inference": False},
        "safety_controls": {"NX_start_close_or_termination": "not invoked",
                            "readiness_call": "nx_ensure_ready is never called",
                            "model_scope": "only the supplied saved standalone fixture is taken over",
                            "source_save": "never saves over the supplied fixture; Save Copy uses a new path",
                            "unknown_outcome": "query only the same operation_id; do not replay; do not return ownership while unresolved"},
        "calls": [], "cases": [],
    }
    client: McpStdioClient | None = None
    process: subprocess.Popen[bytes] | None = None
    output_path: Path | None = None
    fixture: dict | None = None
    part_path: Path | None = None
    source_part_stat: tuple[int, int] | None = None
    credentials: dict[str, object] = {}
    unknown_write_outcome = False
    ownership_return_attempted = False

    def persist() -> None:
        if output_path is None:
            return
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(json.dumps(_redact(report), ensure_ascii=False, allow_nan=False, indent=2) + "\n",
                               encoding="utf-8")

    def call(name: str, arguments: dict | None = None, *, allow_error: bool = False) -> tuple[dict, dict]:
        if client is None:
            raise RuntimeError("installed MCP client is not initialized")
        arguments = arguments or {}
        entry = {"tool": name, "arguments": _redact(arguments)}
        report["calls"].append(entry)
        try:
            response = client.request("tools/call", {"name": name, "arguments": arguments})
        except Exception as exc:
            entry["transport_error_type"] = type(exc).__name__
            persist()
            raise
        entry["mcp_response"] = _redact(response)
        entry["is_error"] = response.get("isError") is True
        payload = _payload(response, name)
        entry["payload"] = _redact(payload)
        persist()
        if response.get("isError") is True and not allow_error:
            raise AssertionError(f"{name} returned MCP tool error {_error_code(response) or 'UNKNOWN'}")
        return payload, entry

    def case(case_id: str, request: dict, retest: str) -> dict:
        entry = {"case_id": case_id, "state": "RUNNING", "request": _redact(request),
                 "reproduction_steps": [
                     "Create a new isolated millimetre fixture with f11-create-mcp-fixture.py.",
                     "Run this smoke against the same idle NX2506 session using a new evidence path.",
                     f"Repeat {case_id} with the recorded selection and inspect before/after object evidence."],
                 "retest_criterion": retest}
        report["cases"].append(entry)
        persist()
        return entry

    def call_session() -> dict:
        payload, _ = call("nx_get_session", credentials)
        if not isinstance(payload.get("revision_id"), str) or not payload["revision_id"]:
            raise AssertionError("nx_get_session omitted revision_id")
        return payload

    def inspect_part(expected_path: Path, label: str) -> dict:
        payload, _ = call("nx_inspect_part", credentials)
        part_record = payload.get("part") if isinstance(payload.get("part"), dict) else {}
        identity = payload.get("identity") if isinstance(payload.get("identity"), dict) else {}
        actual_path = part_record.get("path") or identity.get("part_path")
        if not isinstance(actual_path, str) or _canonical_path(actual_path) != _canonical_path(expected_path):
            raise AssertionError(f"{label} inspected path does not match the expected Work Part")
        return payload

    def inspect_sketch(sketch_id: str, label: str) -> dict:
        payload, _ = call("nx_inspect_sketch", {**credentials, "sketch_id": sketch_id})
        sketch = payload.get("sketch") if isinstance(payload.get("sketch"), dict) else payload
        if not isinstance(sketch, dict) or sketch.get("journal_id") != sketch_id:
            raise AssertionError(f"{label} direct inspection returned a different sketch ID")
        return sketch

    def read_inventory(label: str, sketch_id: str | None = None) -> tuple[dict, dict]:
        nonlocal fixture
        if fixture is None:
            raise RuntimeError("fixture is unavailable")
        if sketch_id is None:
            part_snapshot = inspect_part(part_path if label.startswith("initial") else Path(report["saved_path"]), label)
            sketch_record = _resolve_sketch(part_snapshot, fixture, label)
            sketch_id = sketch_record.get("journal_id")
        direct = inspect_sketch(sketch_id, label)
        return direct, _snapshot_sketch(direct, fixture, label)

    def request_for(operation_id: str, revision: str, object_ids: list[str], target_state: str) -> dict:
        return {"operation_id": operation_id, **credentials, "revision_id": revision,
                "sketch_id": report["current_sketch_id"], "object_ids": object_ids,
                "target_state": target_state}

    def prepare(operation_id: str, revision: str, object_ids: list[str], target_state: str,
                *, allow_error: bool = False) -> tuple[dict, dict]:
        request = request_for(operation_id, revision, object_ids, target_state)
        before_session = call_session()
        if before_session.get("revision_id") != revision:
            raise AssertionError("F11 prepare request revision is not current before dispatch")
        payload, entry = call("nx_prepare_sketch_reference_conversion", request, allow_error=allow_error)
        entry["request_record"] = _redact(request)
        state = _operation_state(payload)
        if state is None:
            raise AssertionError("F11 prepare did not return an operation record")
        if state not in TERMINAL_STATES:
            payload = wait_operation(payload, operation_id, "F11 read-only prepare")
            state = _operation_state(payload)
        entry["terminal_operation"] = _redact(payload)
        if _operation_id(payload) != operation_id:
            raise AssertionError("F11 prepare result operation_id differs from its request")
        if state == "SUCCEEDED":
            result = _operation_result(payload)
            plan = result.get("plan")
            if not isinstance(plan, dict):
                raise AssertionError("F11 prepare operation omitted its explicit plan")
            if (plan.get("kind") != "edit_sketch_reference_conversion"
                    or plan.get("revision_id") != revision or plan.get("sketch_id") != request["sketch_id"]
                    or plan.get("target_state") != target_state):
                raise AssertionError("F11 prepare plan changed its operation, revision, Sketch, or requested state")
            rows = plan.get("objects")
            if not isinstance(rows, list) or {item.get("object_id") for item in rows if isinstance(item, dict)} != set(object_ids):
                raise AssertionError("F11 prepare plan object IDs differ from the explicit current-JID selection")
        elif not allow_error:
            raise AssertionError(f"F11 read-only prepare ended in {state}")
        after_session = call_session()
        if after_session.get("revision_id") != revision:
            raise AssertionError("F11 read-only prepare changed the model revision")
        if (before_session.get("is_modified_after_read") != after_session.get("is_modified_after_read")
                or after_session.get("dirty_changed_by_read") is True):
            raise AssertionError("F11 read-only prepare changed or failed to preserve dirty-state evidence")
        entry["revision_unchanged"] = True
        entry["dirty_state_unchanged"] = True
        return payload, entry

    def get_operation(operation_id: str) -> dict:
        payload, entry = call("nx_get_operation", {"operation_id": operation_id}, allow_error=True)
        entry["queried_operation_id"] = operation_id
        if _operation_id(payload) != operation_id:
            raise AssertionError("nx_get_operation returned a different operation ID")
        return payload

    def write_call(name: str, request: dict, label: str) -> tuple[dict, dict]:
        """Submit once, and on ambiguity query only the same operation ID."""
        nonlocal unknown_write_outcome
        unknown_write_outcome = True
        try:
            initial, entry = call(name, request, allow_error=True)
        except Exception as transport_error:
            report.setdefault("transport_ambiguities", []).append({
                "tool": name, "operation_id": request["operation_id"],
                "transport_error_type": type(transport_error).__name__,
                "recovery": "query same operation_id only; no write replay",
            })
            persist()
            queried = get_operation(request["operation_id"])
            return queried, {"transport_error_type": type(transport_error).__name__,
                             "same_id_query_after_transport_error": _redact(queried)}
        state = _operation_state(initial)
        code = _error_code(initial) or _error_code(entry.get("mcp_response"))
        if state == "UNKNOWN" or (entry.get("is_error") and state is None):
            known_rejections = {"INVALID_ARGUMENT", "UNSUPPORTED_DIMENSION", "STALE_REVISION", "STALE_BINDING",
                                "AUTHORIZATION_REQUIRED", "OPERATION_ID_CONFLICT", "OPERATION_ADAPTER_UNAVAILABLE"}
            if code in known_rejections and state is None:
                unknown_write_outcome = False
                raise AssertionError(f"{label} was rejected before an operation record: {code}")
            initial = get_operation(request["operation_id"])
            entry["same_id_query_after_ambiguous_reply"] = _redact(initial)
        return initial, entry

    def wait_operation(initial: dict, operation_id: str, label: str) -> dict:
        nonlocal unknown_write_outcome
        current = initial
        deadline = time.monotonic() + OPERATION_TIMEOUT_SECONDS
        # Force at least one same-ID query, even when the edit response is already terminal.
        while True:
            if time.monotonic() >= deadline:
                unknown_write_outcome = True
                raise TimeoutError(f"{label} did not reach a terminal state")
            if _operation_state(current) in TERMINAL_STATES and current is not initial:
                break
            if current is not initial and _operation_state(current) not in TERMINAL_STATES:
                time.sleep(POLL_INTERVAL_SECONDS)
            current = get_operation(operation_id)
            if _operation_state(current) == "UNKNOWN":
                unknown_write_outcome = True
                report.setdefault("unknown_operations", []).append({"operation_id": operation_id,
                                                                        "rule": "same-ID query only; write replay and ownership return blocked"})
                persist()
                raise RuntimeError(f"{label} remains UNKNOWN after same-ID query; no replay will be attempted")
            if _operation_state(current) in TERMINAL_STATES:
                break
            time.sleep(POLL_INTERVAL_SECONDS)
        if _operation_id(current) != operation_id:
            raise AssertionError(f"{label} operation result ID differs from the request")
        if _operation_state(current) != "SUCCEEDED":
            unknown_write_outcome = False
            raise AssertionError(f"{label} operation ended in {_operation_state(current) or 'UNKNOWN'}")
        unknown_write_outcome = False
        result = _operation_result(current)
        if result and result.get("verified") is False:
            raise AssertionError(f"{label} terminal operation did not verify its result")
        return current

    def edit(case_id: str, roles: list[str], target_state: str,
             *, sync_dimension_roles: list[str] | None = None) -> tuple[dict, dict, dict, dict]:
        nonlocal unknown_write_outcome
        before_direct, before = read_inventory(case_id + " before", report["current_sketch_id"])
        ids = [before["ids_by_role"][role] for role in roles]
        session = call_session()
        request = request_for("f11-edit-" + uuid.uuid4().hex, session["revision_id"], ids, target_state)
        case_entry = case(case_id, request, "Use a fresh F11 fixture and require the same selected-state, preservation, and same-ID operation checks to pass.")
        # Explicit read-only prepare is captured before the edit. The edit performs its own
        # prepare under the edit operation ID, so the plan is not inferred from this preview.
        preview, preview_entry = prepare("f11-preview-" + uuid.uuid4().hex,
                                        session["revision_id"], ids, target_state)
        case_entry["prepare_preview"] = _redact(preview)
        if preview_entry.get("is_error"):
            raise AssertionError(f"{case_id} read-only prepare unexpectedly failed")
        initial, edit_entry = write_call("nx_edit_sketch_reference_conversion", request, case_id)
        terminal = wait_operation(initial, request["operation_id"], case_id)
        operation_result = _operation_result(terminal)
        if operation_result.get("verified") is not True:
            raise AssertionError(f"{case_id} operation result did not report verified=true")
        after_direct, after = read_inventory(case_id + " after", report["current_sketch_id"])
        transition = verify_object_transition(before, after, roles, target_state,
                                             sync_dimension_roles=sync_dimension_roles or [])
        if sync_dimension_roles:
            for role in sync_dimension_roles:
                expected = fixture_dimension(role)["expression_name"]
                transition["expression_sync_evidence"] = verify_operation_sync_evidence(
                    operation_result, expected, before["dimensions"][role].get("computed_size"),
                )
        case_entry.update(state="PASS", operation_id=request["operation_id"],
                          operation_result=_redact(operation_result),
                          before_snapshot=_redact(before_direct), after_snapshot=_redact(after_direct),
                          preservation=transition)
        report["current_sketch_id"] = after["sketch_id"] or report["current_sketch_id"]
        report["last_inventory"] = after
        persist()
        return terminal, before, after, transition

    def fixture_dimension(role: str) -> dict:
        return next(item for item in fixture["dimensions"] if item["role"] == role)

    def dimension_id(snapshot: dict, role: str) -> str:
        return snapshot["ids_by_role"]["dimension:" + role]

    def safe_return() -> dict:
        nonlocal ownership_return_attempted
        if not credentials:
            return {"skipped": "no current ownership credentials"}
        if unknown_write_outcome:
            result = {"skipped": "write outcome is UNKNOWN; ownership return is blocked"}
            report["ownership_return"] = result
            return result
        if ownership_return_attempted:
            result = {"skipped": "ownership return was already attempted; it is not replayed"}
            report["ownership_return"] = result
            return result
        status, _ = call("nx_bridge_status", {})
        flight = status.get("in_flight", status.get("active_operations", -1))
        if (status.get("state") != "loaded" or status.get("ownership_state") != "AI_OWNED"
                or status.get("writes_halted") is not False or not _in_flight_is_idle(flight)):
            result = {"skipped": "bridge is not known idle, unhalted, and AI_OWNED",
                      "ownership_state": status.get("ownership_state"), "writes_halted": status.get("writes_halted"),
                      "in_flight": flight}
            report["ownership_return"] = result
            return result
        ownership_return_attempted = True
        returned, _ = call("nx_return", credentials)
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("nx_return did not confirm safe HUMAN_OWNED state")
        credentials.clear()
        final_status, _ = call("nx_bridge_status", {})
        final_flight = final_status.get("in_flight", final_status.get("active_operations", -1))
        if (final_status.get("ownership_state") != "HUMAN_OWNED" or final_status.get("writes_halted") is not False
                or not _in_flight_is_idle(final_flight)):
            raise AssertionError("final bridge status is not safe for human ownership")
        result = {"return": _redact(returned), "final_status": _redact(final_status)}
        report["ownership_return"] = result
        return result

    try:
        fixture, part_path, output_path = _read_paths(args)
        report["fixture_json"] = str(args.fixture_json.resolve(strict=True))
        report["part_path"] = str(part_path)
        initial_part_stat = part_path.stat()
        source_part_stat = (initial_part_stat.st_size, initial_part_stat.st_mtime_ns)
        report["source_fixture_file"] = {"size_bytes": initial_part_stat.st_size,
                                         "modified_time_ns": initial_part_stat.st_mtime_ns}
        report["fixture_schema"] = fixture["schema"]
        install_root = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        host_module = install_root / "app" / "src" / "nx_sketch_mcp" / "__main__.py"
        config = install_root / "mcp-config.json"
        if not config.is_file() or not host_module.is_file():
            raise FileNotFoundError("configured local NX Sketch MCP installation is missing")
        environment = os.environ.copy()
        environment.update(PYTHONPATH=str(install_root / "app" / "src"),
                           PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen(
            [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(config)],
            cwd=str(ROOT), env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, bufsize=0,
        )
        client = McpStdioClient(process, 60, max_line_bytes=MCP_RESPONSE_LINE_LIMIT_BYTES)
        initialized = client.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION, "capabilities": {},
            "clientInfo": {"name": "f11-installed-mcp-smoke", "version": "1"},
        })
        client.notify("notifications/initialized")
        report["initialize"] = _redact(initialized)
        server_info = initialized.get("serverInfo")
        if not isinstance(server_info, dict) or server_info.get("version") != EXPECTED_SERVER_VERSION:
            raise AssertionError(f"installed MCP version must be {EXPECTED_SERVER_VERSION}")
        report["server_version"] = server_info["version"]
        listing = client.request("tools/list")
        tools = listing.get("tools")
        if not isinstance(tools, list):
            raise AssertionError("tools/list omitted tools")
        names = [item.get("name") for item in tools if isinstance(item, dict)]
        if len(names) != EXPECTED_TOOL_COUNT or len(set(names)) != EXPECTED_TOOL_COUNT or set(names) != EXPECTED_TOOLS:
            raise AssertionError("installed MCP tool inventory differs from the exact F11 31-tool contract")
        report["tool_count"] = len(names)
        report["tool_names"] = sorted(names)
        report["reference_conversion_schemas"] = _check_reference_conversion_schemas(tools)
        report["tools_list"] = _redact(tools)
        capabilities, _ = call("nx_get_capabilities", {})
        report["reference_conversion_capability"] = _redact(_check_reference_conversion_capability(capabilities))
        report["capabilities"] = _redact(capabilities)
        status, _ = call("nx_bridge_status", {})
        report["initial_bridge_status"] = _redact(status)
        initial_flight = status.get("in_flight", status.get("active_operations", -1))
        if (status.get("state") != "loaded" or status.get("ownership_state") != "HUMAN_OWNED"
                or status.get("writes_halted") is not False or not _in_flight_is_idle(initial_flight)):
            raise AssertionError("F11 fixture smoke requires an already loaded, idle, unhalted HUMAN_OWNED bridge")
        if isinstance(status.get("pid"), bool) or not isinstance(status.get("pid"), int) or status["pid"] <= 0:
            raise AssertionError("bridge status omitted a valid NX PID")

        takeover_case = case("F11_saved_fixture_takeover", {"part_path": str(part_path)},
                             "Use a new clean standalone millimetre fixture, match the expected PID/path and saved-copy scope, and repeat the full smoke.")
        takeover, _ = call("nx_takeover", {"expected_pid": status["pid"], "expected_part_path": str(part_path),
                                           "artifact_scope": "saved_independent_copy", "authorize": True})
        owner, epoch = takeover.get("ownership_id"), takeover.get("binding_epoch")
        if (not isinstance(owner, str) or not owner or isinstance(epoch, bool)
                or not isinstance(epoch, int) or epoch < 1):
            raise AssertionError("nx_takeover omitted current ownership credentials")
        credentials.update(ownership_id=owner, binding_epoch=epoch)
        if (takeover.get("ownership_state") != "AI_OWNED"
                or _canonical_path(takeover.get("part_path", "")) != _canonical_path(part_path)
                or takeover.get("artifact_scope") != "saved_independent_copy"
                or takeover.get("artifact_kind") != "standalone_part"):
            raise AssertionError("nx_takeover did not bind the exact saved independent fixture")
        takeover_case.update(state="PASS", evidence=_redact(takeover))
        session = call_session()
        if (_canonical_path(session.get("part_path", "")) != _canonical_path(part_path)
                or str(session.get("part_units", "")).casefold() not in {"millimeter", "millimeters", "mm"}):
            raise AssertionError("owned Work Part path or unit differs from the fixture")
        report["initial_revision_id"] = session["revision_id"]
        part_snapshot = inspect_part(part_path, "initial fixture")
        sketch_record = _resolve_sketch(part_snapshot, fixture, "initial fixture")
        report["current_sketch_id"] = sketch_record.get("journal_id")
        direct_before, before = read_inventory("initial fixture inspection", report["current_sketch_id"])
        report["initial_direct_snapshot"] = _redact(direct_before)
        report["initial_current_jids"] = before["ids_by_role"]
        report["fixture_jid_remaps"] = {
            role: {"fixture_journal_id": row.get("journal_id"),
                   "current_journal_id": before["ids_by_role"].get(role)}
            for role, row in (
                [(item["role"], item) for item in fixture["curves"]]
                + [("dimension:" + item["role"], item) for item in fixture["dimensions"]]
            )
        }

        # Unsupported dimensions must fail before mutation with the explicit F11 code.
        unsupported_id = dimension_id(before, "unsupported_radial")
        session = call_session()
        unsupported_case = case("F11_unsupported_radial_dimension", {"object_id": unsupported_id,
                                                                       "target_state": "active"},
                                "Repeat with a fresh fixture and require UNSUPPORTED_DIMENSION before any state, revision, or dirty-state change.")
        unsupported, unsupported_entry = prepare("f11-unsupported-" + uuid.uuid4().hex,
                                                 session["revision_id"], [unsupported_id], "active", allow_error=True)
        unsupported_code = _error_code(unsupported) or _error_code(unsupported_entry.get("mcp_response"))
        after_unsupported_direct, after_unsupported = read_inventory("after unsupported prepare", report["current_sketch_id"])
        if unsupported_code != "UNSUPPORTED_DIMENSION":
            raise AssertionError(f"radial dimension prepare returned {unsupported_code or 'no error code'}, expected UNSUPPORTED_DIMENSION")
        if verify_object_transition(before, after_unsupported, [], "active").get("passed") is not True:
            raise AssertionError("unsupported dimension prepare mutated the Sketch")
        unsupported_case.update(state="PASS", error_code=unsupported_code,
                                after_snapshot=_redact(after_unsupported_direct))

        # All-target selection is invalid at prepare; confirm no model state change.
        session = call_session()
        all_target_id = before["ids_by_role"]["horizontal"]
        all_target_case = case("F11_active_all_target_prepare_rejection", {"object_id": all_target_id,
                                                                            "target_state": "active"},
                               "Repeat with an Active object already at the requested state and require INVALID_ARGUMENT without a model or revision change.")
        before_initial_all_direct, before_initial_all = read_inventory("initial Active all-target before",
                                                                        report["current_sketch_id"])
        all_target, all_target_entry = prepare("f11-all-target-" + uuid.uuid4().hex,
                                               session["revision_id"], [all_target_id], "active", allow_error=True)
        all_target_code = _error_code(all_target) or _error_code(all_target_entry.get("mcp_response"))
        after_initial_all_direct, after_initial_all = read_inventory("initial Active all-target after",
                                                                      report["current_sketch_id"])
        if all_target_code != "INVALID_ARGUMENT":
            raise AssertionError(f"Active all-target prepare returned {all_target_code or 'no error code'}, expected INVALID_ARGUMENT")
        verify_object_transition(before_initial_all, after_initial_all, [], "active")
        all_target_case.update(state="PASS", error_code=all_target_code,
                               before_snapshot=_redact(before_initial_all_direct),
                               after_snapshot=_redact(after_initial_all_direct))

        session = call_session()
        edit("F11_horizontal_curve_active_to_reference", ["horizontal"], "reference")
        # Now one selected object is already at target while another is not. A mixed-state
        # selection is legal and the edit changes only the still-Active witness curve.
        mixed_case = case("F11_mixed_already_target_and_active",
                          {"roles": ["horizontal", "witness"], "target_state": "reference"},
                          "Repeat on a fresh fixture; prepare must accept one already-target and one Active current JID, then only the Active one changes.")
        current = report["last_inventory"]
        ids = [current["ids_by_role"][role] for role in ("horizontal", "witness")]
        current_session = call_session()
        mixed_request = request_for("f11-edit-" + uuid.uuid4().hex, current_session["revision_id"], ids, "reference")
        preview, preview_entry = prepare("f11-preview-" + uuid.uuid4().hex, current_session["revision_id"], ids, "reference")
        if preview_entry.get("is_error"):
            raise AssertionError("mixed already-target/Active prepare was rejected")
        mixed_case["prepare_preview"] = _redact(preview)
        before_mixed_direct, before_mixed = read_inventory("mixed selection before", report["current_sketch_id"])
        mixed_initial, mixed_entry = write_call("nx_edit_sketch_reference_conversion", mixed_request,
                                                "mixed already-target/Active conversion")
        mixed_terminal = wait_operation(mixed_initial, mixed_request["operation_id"], "mixed already-target/Active conversion")
        mixed_result = _operation_result(mixed_terminal)
        if mixed_result.get("verified") is not True:
            raise AssertionError("mixed-state operation result did not report verified=true")
        after_mixed_direct, after_mixed = read_inventory("mixed selection after", report["current_sketch_id"])
        mixed_evidence = verify_object_transition(before_mixed, after_mixed, ["horizontal", "witness"], "reference")
        # Horizontal is already Reference while witness remains Active. The operation's
        # changed_count directly proves only the not-at-target object changed.
        if mixed_result.get("changed_count") != 1:
            raise AssertionError("mixed-state F11 edit did not change exactly the one not-at-target object")
        mixed_case.update(state="PASS", operation_id=mixed_request["operation_id"],
                          operation_result=_redact(mixed_result), preservation=mixed_evidence,
                          before_snapshot=_redact(before_mixed_direct), after_snapshot=_redact(after_mixed_direct))
        report["current_sketch_id"] = after_mixed["sketch_id"] or report["current_sketch_id"]
        report["last_inventory"] = after_mixed

        # Now the same single-object request is all-target and must be rejected at prepare.
        current_session = call_session()
        all_target_id = after_mixed["ids_by_role"]["horizontal"]
        before_all_direct, before_all = read_inventory("all-target before", report["current_sketch_id"])
        reference_all_target_case = case("F11_reference_all_target_prepare_rejection",
                                         {"object_id": all_target_id, "target_state": "reference"},
                                         "Repeat with a Reference object already at the requested state and require INVALID_ARGUMENT without a model or revision change.")
        all_target, all_target_entry = prepare("f11-all-target-" + uuid.uuid4().hex,
                                               current_session["revision_id"], [all_target_id], "reference",
                                               allow_error=True)
        all_target_code = _error_code(all_target) or _error_code(all_target_entry.get("mcp_response"))
        after_all_direct, after_all = read_inventory("all-target after", report["current_sketch_id"])
        if all_target_code != "INVALID_ARGUMENT":
            raise AssertionError(f"all-target prepare returned {all_target_code or 'no error code'}, expected INVALID_ARGUMENT")
        verify_object_transition(before_all, after_all, [], "reference")
        reference_all_target_case.update(state="PASS", error_code=all_target_code,
                                         before_snapshot=_redact(before_all_direct),
                                         after_snapshot=_redact(after_all_direct))

        # Promote the Reference VerticalDim: its 8 mm expression value must become its
        # recorded 11 mm pre-conversion ComputedSize, with identity/flags/operands preserved.
        vertical_before = after_all["dimensions"]["vertical"]
        if vertical_before.get("dimension_state") != "Reference":
            raise AssertionError("fixture VerticalDim is not in Reference state")
        if not _close(_numeric_text(vertical_before.get("expression_value"), "VerticalDim expression Value"), 8.0):
            raise AssertionError("fixture VerticalDim expression value is not the deliberate 8 mm stale RHS")
        if not _close(_numeric_text(vertical_before.get("computed_size"), "VerticalDim ComputedSize"), 11.0):
            raise AssertionError("fixture VerticalDim ComputedSize is not 11 mm")
        promotion_terminal, promotion_before, promotion_after, promotion_transition = edit(
            "F11_vertical_reference_to_active_rhs_sync", ["dimension:vertical"], "active",
            sync_dimension_roles=["vertical"],
        )
        promotion_case = report["cases"][-1]
        promotion_case["preconversion_computed_size_mm"] = 11.0
        report["vertical_rhs_sync"] = promotion_transition.get("expression_sync_evidence")

        # Reverse curves and both supported dimensions. The dimension reverse verifies the
        # other state direction; its now-numeric 11 mm expression must remain unchanged.
        reverse_roles = ["horizontal", "witness"]
        edit("F11_curves_reference_to_active", reverse_roles, "active")
        edit("F11_horizontal_dimension_active_to_reference", ["dimension:horizontal"], "reference")
        edit("F11_horizontal_dimension_reference_to_active", ["dimension:horizontal"], "active")
        edit("F11_vertical_dimension_active_to_reference", ["dimension:vertical"], "reference")
        edit("F11_vertical_dimension_reference_to_active", ["dimension:vertical"], "active")
        final_session = call_session()
        final_before_direct, final_before = read_inventory("pre-Save Copy final state", report["current_sketch_id"])
        report["pre_save_copy_snapshot"] = _redact(final_before_direct)
        model_root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
        saved_path = model_root / f"F11-mcp-{run_id}-roundtrip.prt"
        if saved_path.exists() or _canonical_path(saved_path) == _canonical_path(part_path):
            raise FileExistsError("F11 unique Save Copy target exists or collides with source fixture")
        save_case = case("F11_save_copy_reopen", {"target_path": str(saved_path), "reopen": True},
                         "Use a new fixture and unique Save Copy path; require verified saved/reopened flags, conserved Sketch state, and safe ownership return.")
        save_request = {"operation_id": "f11-save-" + uuid.uuid4().hex, **credentials,
                        "target_path": str(saved_path), "reopen": True}
        save_initial, save_entry = write_call("nx_save_copy", save_request, "F11 Save Copy/reopen")
        save_terminal = wait_operation(save_initial, save_request["operation_id"], "F11 Save Copy/reopen")
        save_result = _operation_result(save_terminal)
        if save_result.get("saved") is not True or save_result.get("reopened") is not True:
            raise AssertionError("nx_save_copy did not confirm saved=true and reopened=true")
        new_owner, new_epoch = save_result.get("new_ownership_id"), save_result.get("new_binding_epoch")
        if not isinstance(new_owner, str) or not new_owner or isinstance(new_epoch, bool) or not isinstance(new_epoch, int) or new_epoch < 1:
            raise AssertionError("F11 Save Copy omitted rotated ownership credentials")
        returned_path = save_result.get("saved_path") or save_result.get("target_path")
        if not isinstance(returned_path, str) or _canonical_path(returned_path) != _canonical_path(saved_path):
            raise AssertionError("nx_save_copy returned a different target path")
        unknown_write_outcome = False
        credentials.update(ownership_id=new_owner, binding_epoch=new_epoch)
        report["saved_path"] = str(saved_path)
        reopen_session = call_session()
        if (_canonical_path(reopen_session.get("part_path", "")) != _canonical_path(saved_path)
                or str(reopen_session.get("part_units", "")).casefold() not in {"millimeter", "millimeters", "mm"}):
            raise AssertionError("Save Copy reopen session path or millimetre units do not match")
        reopened_part = inspect_part(saved_path, "reopened F11 Save Copy")
        reopened_sketch = _resolve_sketch(reopened_part, fixture, "reopened F11 Save Copy")
        report["current_sketch_id"] = reopened_sketch.get("journal_id")
        reopened_direct = inspect_sketch(report["current_sketch_id"], "reopened F11 fixture Sketch")
        reopened = _snapshot_sketch(reopened_direct, fixture, "reopened F11 fixture Sketch")
        roundtrip = verify_object_transition(final_before, reopened, [], "active")
        if not isinstance(save_result.get("checks"), dict):
            raise AssertionError("Save Copy result omitted native round-trip checks")
        if set(save_result["checks"]) != {"associations", "expression", "geometry", "history"}:
            raise AssertionError("Save Copy returned an incomplete round-trip check inventory")
        bad_checks = {key: value for key, value in save_result["checks"].items()
                      if not isinstance(value, dict) or value.get("status") != "VERIFIED"}
        if bad_checks:
            raise AssertionError("Save Copy native round-trip checks were not all VERIFIED")
        save_case.update(state="PASS", save_result=_redact(save_result),
                         reopened_sketch_id=report["current_sketch_id"], roundtrip_preservation=roundtrip,
                         reopened_snapshot=_redact(reopened_direct))
        report["roundtrip_reopened"] = True
        final_part_stat = part_path.stat()
        if source_part_stat != (final_part_stat.st_size, final_part_stat.st_mtime_ns):
            raise AssertionError("source F11 fixture file changed during the installed MCP smoke")
        report["source_fixture_file_unchanged"] = True
        ownership_result = safe_return()
        if ownership_result.get("skipped"):
            raise AssertionError("F11 completed but ownership did not return safely")
        report["passed"] = True
        report["result"] = "Installed F11 MCP contract, reference-state conversion, preservation, Save Copy/reopen, and safe ownership return verified for this single fixture/build."
    except Exception as exc:
        report["passed"] = False
        report["error_type"] = type(exc).__name__
        report["error"] = f"{type(exc).__name__}: {exc}"
        case_record = report["cases"][-1] if report.get("cases") else None
        failure = {
            "case_id": case_record.get("case_id") if isinstance(case_record, dict) else "F11_setup_or_protocol",
            "failure": report["error"],
            "reproduction_steps": case_record.get("reproduction_steps") if isinstance(case_record, dict) else [
                "Create a new unique millimetre F11 fixture with f11-create-mcp-fixture.py.",
                "Run f11-mcp-smoke.py with the matching fixture JSON and a new evidence output path.",
                "Repeat the recorded failed setup or operation once while preserving the first report."],
            "retest_criterion": case_record.get("retest_criterion") if isinstance(case_record, dict) else
                "The same operation reaches its expected state, all unrelated object/expression records remain unchanged, and same-ID operation.get confirms SUCCEEDED.",
        }
        report["failure_record"] = failure
    finally:
        if credentials and not ownership_return_attempted:
            try:
                safe_return()
            except Exception as return_error:
                report["ownership_return_error_type"] = type(return_error).__name__
        report["completed_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        if client is not None:
            try:
                client.close()
            except Exception as close_error:
                report["mcp_child_close_error_type"] = type(close_error).__name__
        if output_path is None:
            candidate = args.output.resolve(strict=False)
            evidence_root = ROOT / "workspace" / "v2" / "evidence"
            if candidate.is_relative_to(evidence_root.resolve(strict=False)) and not candidate.exists():
                output_path = candidate
        if output_path is not None:
            persist()
    print(json.dumps(_redact({key: report[key] for key in (
        "passed", "server_version", "tool_count", "part_path", "saved_path", "error", "failure_record",
    ) if key in report}), ensure_ascii=False))
    return 0 if report.get("passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
