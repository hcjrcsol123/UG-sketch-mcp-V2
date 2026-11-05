"""Minimal MCP 2025-11-25 stdio server for local bridge diagnostics."""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from typing import Any

from . import __version__
from .bridge import BridgeFailure, JsonlBridge
from .config import BridgeConfig, ConfigError, load_config
from .jsonutil import dumps_line, loads_unique
from . import journal
from .readiness import NxReadiness


PROTOCOL_VERSION = "2025-11-25"
SERVER_NAME = "nx-sketch-mcp"
MAX_MCP_LINE_BYTES = 1_048_576
_OPERATION_ID_SCHEMA = {"type": "string", "minLength": 1, "maxLength": 128}
_POSITIVE_INTEGER_SCHEMA = {"type": "integer", "minimum": 1}
_NONEMPTY_STRING_SCHEMA = {"type": "string", "minLength": 1}
_NUMBER_SCHEMA = {"type": "number"}
_POINT_2_SCHEMA = {"type": "array", "items": _NUMBER_SCHEMA, "minItems": 2, "maxItems": 2}
_ORIGIN_3_SCHEMA = {"type": "array", "items": _NUMBER_SCHEMA, "minItems": 3, "maxItems": 3}
_VECTOR_3_SCHEMA = {"type": "array", "items": _NUMBER_SCHEMA, "minItems": 3, "maxItems": 3}
_AXIS_SCHEMA = {"type": "string", "enum": ["X", "Y", "Z"]}
_HANDEDNESS_SCHEMA = {"type": "string", "enum": ["right", "left"]}
_HELICAL_IDENTIFIER_SCHEMA = {"type": "string", "minLength": 1, "maxLength": 128}
_HELICAL_TARGET_BODY_SCHEMA = {"type": "string", "minLength": 1, "maxLength": 4096}
_HELICAL_COORDINATE_SCHEMA = {"type": "number", "minimum": -1_000_000, "maximum": 1_000_000}
_HELICAL_POSITIVE_MM_SCHEMA = {"type": "number", "exclusiveMinimum": 0, "maximum": 1_000_000}
_HELICAL_ORIGIN_3_SCHEMA = {
    "type": "array", "items": _HELICAL_COORDINATE_SCHEMA, "minItems": 3, "maxItems": 3,
}
_REVOLVE_ANGLE_SCHEMA = {"type": "number", "minimum": -360, "maximum": 360}
_DECIMAL_PATTERN = r"^[+-]?(?:(?:0|[1-9][0-9]*)(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$"
_FINITE_DECIMAL = re.compile(_DECIMAL_PATTERN, re.ASCII)

_NATIVE_GEOMETRY_SCHEMA = {
    "anyOf": [
        {
            "type": "object",
            "properties": {
                "id": {"type": "string", "minLength": 1, "maxLength": 128},
                "type": {"const": "line"},
                "start_mm": dict(_POINT_2_SCHEMA),
                "end_mm": dict(_POINT_2_SCHEMA),
            },
            "required": ["id", "type", "start_mm", "end_mm"],
            "additionalProperties": False,
        },
        {
            "type": "object",
            "properties": {
                "id": {"type": "string", "minLength": 1, "maxLength": 128},
                "type": {"const": "rectangle"},
                "corner_mm": dict(_POINT_2_SCHEMA),
                "width_mm": {"type": "number", "exclusiveMinimum": 0},
                "height_mm": {"type": "number", "exclusiveMinimum": 0},
            },
            "required": ["id", "type", "corner_mm", "width_mm", "height_mm"],
            "additionalProperties": False,
        },
        {
            "type": "object",
            "properties": {
                "id": {"type": "string", "minLength": 1, "maxLength": 128},
                "type": {"const": "circle"},
                "center_mm": dict(_POINT_2_SCHEMA),
                "diameter_mm": {"type": "number", "exclusiveMinimum": 0},
            },
            "required": ["id", "type", "center_mm", "diameter_mm"],
            "additionalProperties": False,
        },
        {
            "type": "object",
            "properties": {
                "id": {"type": "string", "minLength": 1, "maxLength": 128},
                "type": {"const": "arc"},
                "center_mm": dict(_POINT_2_SCHEMA),
                "radius_mm": {"type": "number", "exclusiveMinimum": 0},
                "start_angle_deg": {"type": "number", "minimum": 0, "exclusiveMaximum": 360},
                "sweep_deg": {"type": "number", "exclusiveMinimum": -360, "exclusiveMaximum": 360, "not": {"const": 0}},
            },
            "required": ["id", "type", "center_mm", "radius_mm", "start_angle_deg", "sweep_deg"],
            "additionalProperties": False,
        },
        {
            "type": "object",
            "properties": {
                "id": {"type": "string", "minLength": 1, "maxLength": 128},
                "type": {"const": "ellipse"},
                "center_mm": dict(_POINT_2_SCHEMA),
                "major_radius_mm": {"type": "number", "exclusiveMinimum": 0},
                "minor_radius_mm": {"type": "number", "exclusiveMinimum": 0},
                "rotation_deg": {"type": "number", "minimum": 0, "exclusiveMaximum": 360},
            },
            "required": ["id", "type", "center_mm", "major_radius_mm", "minor_radius_mm", "rotation_deg"],
            "additionalProperties": False,
        },
        {
            "type": "object",
            "properties": {
                "id": {"type": "string", "minLength": 1, "maxLength": 128},
                "type": {"const": "elliptical_arc"},
                "center_mm": dict(_POINT_2_SCHEMA),
                "major_radius_mm": {"type": "number", "exclusiveMinimum": 0},
                "minor_radius_mm": {"type": "number", "exclusiveMinimum": 0},
                "rotation_deg": {"type": "number", "minimum": 0, "exclusiveMaximum": 360},
                "start_angle_deg": {"type": "number", "minimum": 0, "exclusiveMaximum": 360},
                "sweep_deg": {"type": "number", "exclusiveMinimum": -360, "exclusiveMaximum": 360, "not": {"const": 0}},
            },
            "required": ["id", "type", "center_mm", "major_radius_mm", "minor_radius_mm", "rotation_deg", "start_angle_deg", "sweep_deg"],
            "additionalProperties": False,
        },
        {
            "type": "object",
            "properties": {
                "id": {"type": "string", "minLength": 1, "maxLength": 128},
                "type": {"const": "spline"},
                "interpolation_points_mm": {
                    "type": "array", "items": dict(_POINT_2_SCHEMA), "minItems": 4, "maxItems": 32,
                },
                "degree": {"const": 3},
                "endpoint_condition": {"const": "unconstrained"},
            },
            "required": ["id", "type", "interpolation_points_mm", "degree", "endpoint_condition"],
            "additionalProperties": False,
        },
    ]
}


class _InvalidToolArguments(ValueError):
    """An MCP tool argument did not satisfy its strict input contract."""


def _empty_schema() -> dict[str, Any]:
    return {"type": "object", "properties": {}, "additionalProperties": False}


def _hole_schema() -> dict[str, Any]:
    # Each variant has its own properties so additionalProperties excludes fields
    # belonging to other kinds and depth modes.
    identifier = {"type": "string", "minLength": 1, "maxLength": 128, "pattern": r"\S"}
    positive = dict(_HELICAL_POSITIVE_MM_SCHEMA)
    common = {
        "operation_id": dict(_OPERATION_ID_SCHEMA), "ownership_id": identifier,
        "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA), "revision_id": identifier,
        "target_body_id": {"type": "string", "minLength": 1, "maxLength": 4096, "pattern": r"\S"},
        "position_mm": dict(_HELICAL_ORIGIN_3_SCHEMA),
        "direction": {**_VECTOR_3_SCHEMA, "not": {"const": [0, 0, 0]}},
    }
    kinds = {
        "simple": {"diameter_mm": positive},
        "counterbore": {"diameter_mm": positive, "counterbore_diameter_mm": positive, "counterbore_depth_mm": positive},
        "countersink": {"diameter_mm": positive, "countersink_diameter_mm": positive,
                        "countersink_angle_deg": {"type": "number", "exclusiveMinimum": 0, "exclusiveMaximum": 180}},
        "threaded": {"thread_standard": identifier, "thread_size": identifier, "radial_engage": identifier, "pitch_mm": positive,
                     "hand": dict(_HANDEDNESS_SCHEMA), "thread_depth_mm": positive},
    }
    variants = []
    for kind, fields in kinds.items():
        for depth_mode in ("blind", "through"):
            properties = {**common, "kind": {"const": kind}, "depth_mode": {"const": depth_mode}, **fields}
            if depth_mode == "blind":
                properties["hole_depth_mm"] = positive
            variants.append({"type": "object", "properties": properties,
                             "required": list(properties), "additionalProperties": False})
    return {"type": "object", "oneOf": variants}


def _sketch_corner_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "operation_id": dict(_OPERATION_ID_SCHEMA),
            "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
            "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
            "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
            "sketch_id": dict(_NONEMPTY_STRING_SCHEMA),
            "kind": {"type": "string", "enum": ["fillet", "chamfer"]},
            "curve1_id": {"type": "string", "minLength": 1, "maxLength": 128},
            "curve2_id": {"type": "string", "minLength": 1, "maxLength": 128},
            "curve1_end": {"type": "string", "enum": ["start", "end"]},
            "curve2_end": {"type": "string", "enum": ["start", "end"]},
            "radius_mm": {"type": "number", "exclusiveMinimum": 0},
            "distance1_mm": {"type": "number", "exclusiveMinimum": 0},
            "distance2_mm": {"type": "number", "exclusiveMinimum": 0},
            "angle_deg": {"type": "number", "exclusiveMinimum": 0, "exclusiveMaximum": 180},
        },
        "required": [
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "kind", "curve1_id", "curve2_id", "curve1_end", "curve2_end",
        ],
        "allOf": [
            {
                "if": {"properties": {"kind": {"const": "fillet"}}, "required": ["kind"]},
                "then": {
                    "required": ["radius_mm"],
                    "not": {"anyOf": [
                        {"required": ["distance1_mm"]},
                        {"required": ["distance2_mm"]},
                        {"required": ["angle_deg"]},
                    ]},
                },
            },
            {
                "if": {"properties": {"kind": {"const": "chamfer"}}, "required": ["kind"]},
                "then": {
                    "required": ["distance1_mm"],
                    "oneOf": [
                        {"required": ["distance2_mm"], "not": {"required": ["angle_deg"]}},
                        {"required": ["angle_deg"], "not": {"required": ["distance2_mm"]}},
                    ],
                    "not": {"required": ["radius_mm"]},
                },
            },
        ],
        "additionalProperties": False,
    }


def _sketch_offset_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "operation_id": dict(_OPERATION_ID_SCHEMA),
            "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
            "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
            "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
            "sketch_id": dict(_NONEMPTY_STRING_SCHEMA),
            "curve_chain": {
                "type": "array",
                "minItems": 1,
                "maxItems": 128,
                "items": {
                    "type": "object",
                    "properties": {
                        "curve_id": {"type": "string", "minLength": 1, "maxLength": 4096},
                        "direction": {"type": "string", "enum": ["forward", "reverse"]},
                    },
                    "required": ["curve_id", "direction"],
                    "additionalProperties": False,
                },
            },
            "distance_mm": {"type": "number", "exclusiveMinimum": 0},
            "side": {"type": "string", "enum": ["left", "right"]},
            "retain_original": {"type": "boolean", "const": True},
            "join_mode": {"type": "string", "enum": ["extension", "arc"]},
        },
        "required": [
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "curve_chain", "distance_mm", "side", "retain_original", "join_mode",
        ],
        "additionalProperties": False,
    }


def _sketch_pattern_schema() -> dict[str, Any]:
    mode_fields = {
        "linear": ["direction_xy", "spacing_mm", "instance_count"],
        "rectangular": ["direction_x_xy", "direction_y_xy", "spacing_x_mm", "spacing_y_mm", "count_x", "count_y"],
        "circular": ["center_xy", "angle_step_deg", "instance_count"],
        "mirror": ["axis_curve_id"],
    }
    all_mode_fields = {field for fields in mode_fields.values() for field in fields}
    properties: dict[str, Any] = {
        "operation_id": dict(_OPERATION_ID_SCHEMA),
        "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
        "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
        "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
        "sketch_id": dict(_NONEMPTY_STRING_SCHEMA),
        "source_curve_ids": {
            "type": "array", "minItems": 1, "maxItems": 256,
            "items": {"type": "string", "minLength": 1, "maxLength": 4096},
            "uniqueItems": True,
        },
        "mode": {"type": "string", "enum": list(mode_fields)},
        "direction_xy": dict(_POINT_2_SCHEMA),
        "spacing_mm": {"type": "number", "exclusiveMinimum": 0},
        "instance_count": {"type": "integer", "minimum": 2, "maximum": 256},
        "direction_x_xy": dict(_POINT_2_SCHEMA),
        "direction_y_xy": dict(_POINT_2_SCHEMA),
        "spacing_x_mm": {"type": "number", "exclusiveMinimum": 0},
        "spacing_y_mm": {"type": "number", "exclusiveMinimum": 0},
        "count_x": {"type": "integer", "minimum": 2, "maximum": 256},
        "count_y": {"type": "integer", "minimum": 2, "maximum": 256},
        "center_xy": dict(_POINT_2_SCHEMA),
        "angle_step_deg": {
            "type": "number", "exclusiveMinimum": -360, "exclusiveMaximum": 360,
            "not": {"const": 0},
        },
        "axis_curve_id": {"type": "string", "minLength": 1, "maxLength": 4096},
    }
    all_of: list[dict[str, Any]] = []
    for mode, fields in mode_fields.items():
        unused_fields = sorted(all_mode_fields - set(fields))
        all_of.append({
            "if": {"properties": {"mode": {"const": mode}}, "required": ["mode"]},
            "then": {
                "required": fields,
                "not": {"anyOf": [{"required": [field]} for field in unused_fields]},
            },
        })
    return {
        "type": "object",
        "properties": properties,
        "required": [
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "source_curve_ids", "mode",
        ],
        "allOf": all_of,
        "additionalProperties": False,
    }


def _sketch_projection_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "operation_id": dict(_OPERATION_ID_SCHEMA),
            "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
            "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
            "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
            "sketch_id": dict(_NONEMPTY_STRING_SCHEMA),
            "source_curve_ids": {
                "type": "array", "minItems": 1, "maxItems": 128,
                "items": dict(_NONEMPTY_STRING_SCHEMA),
                "uniqueItems": True,
            },
            "direction_xyz": dict(_VECTOR_3_SCHEMA),
            "associative": {"type": "boolean"},
        },
        "required": [
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "source_curve_ids", "direction_xyz", "associative",
        ],
        "additionalProperties": False,
    }


def _sketch_reference_conversion_schema() -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {
            "operation_id": dict(_OPERATION_ID_SCHEMA),
            "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
            "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
            "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
            "sketch_id": dict(_NONEMPTY_STRING_SCHEMA),
            "object_ids": {
                "type": "array", "minItems": 1, "maxItems": 256,
                "items": dict(_NONEMPTY_STRING_SCHEMA),
                "uniqueItems": True,
            },
            "target_state": {"type": "string", "enum": ["reference", "active"]},
        },
        "required": [
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "object_ids", "target_state",
        ],
        "additionalProperties": False,
    }


TOOLS: list[dict[str, Any]] = [
    {
        "name": "nx_ensure_ready",
        "title": "Prepare NX connection",
        "description": (
            "Explicitly prepares the configured NX connection with a startup/probe budget up to "
            "30 seconds, plus bounded helper cleanup. "
            "Reuses a connected NX session; otherwise the optional launch profile starts a dedicated "
            "NX GUI with the installed startup bridge. Returns READY, STARTING, NEEDS_MANUAL_ACTION, "
            "or FAILED. Existing user NX sessions and license dialogs are never closed. This does "
            "not take ownership, open a model, or replay model writes. Call before nx_takeover."
        ),
        "inputSchema": _empty_schema(),
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_get_capabilities",
        "title": "NX bridge capabilities",
        "description": (
            "Returns bridge capabilities, active-operation count, and write-halt state. Model "
            "writes require model_write_enabled=true for an AI_OWNED session with "
            "writes_halted=false; supported operations, geometry, units, and constraints are "
            "listed in model_write_support and model_native_geometry_support. "
            "model_helical_adapter_available and model_helical_write_enabled report whether the "
            "loaded bridge supports and currently permits helical thread/groove preparation. "
            "model_hole_adapter_available and model_hole_write_enabled report hole preparation status. "
            "model_datum_plane_adapter_available and model_datum_plane_write_enabled report whether "
                "the separate datum-plane adapter supports and currently permits datum-plane preparation. "
                "model_sketch_pattern_adapter_available and model_sketch_pattern_write_enabled report whether "
                "the optional sketch-pattern adapter supports and currently permits pattern operations. "
                "model_sketch_projection_adapter_available and model_sketch_projection_write_enabled report whether "
                "the sketch-projection adapter supports and currently permits projection operations. "
                "model_native_geometry_support.sketch_corner describes explicit sketch fillet and chamfer support. "
            "Ownership takeover requires explicit human confirmation."
        ),
        "inputSchema": _empty_schema(),
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_bridge_status",
        "title": "Local bridge status",
        "description": (
            "Returns cached local bridge status only. It includes model_write_enabled, "
            "writes_halted, write_halt_reason, and active_operations so write freezes and "
            "unknown outcomes are visible. It does not read or change NX model data."
        ),
        "inputSchema": _empty_schema(),
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_bridge_ping",
        "title": "Local bridge diagnostic ping",
        "description": (
            "Sends a diagnostic ping with the caller-provided nonce. The ping does not read or "
            "change NX model data. Nonce length must be 1 through 128 characters."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "nonce": {"type": "string", "minLength": 1, "maxLength": 128},
            },
            "required": ["nonce"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_takeover",
        "title": "Take NX session ownership",
        "description": (
            "Requests AI ownership of the identified NX session. Call this only after a human has "
            "explicitly confirmed the takeover, and pass authorize=true. Use saved_independent_copy "
            "for a saved standalone part, or current_work_part to inspect and make supported edits "
            "to the active in-memory part while preserving its identity. current_work_part cannot "
            "be saved with nx_save_copy. The NX adapter checks the live part structure and reports "
            "its supported operations and dependencies."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "expected_pid": {"type": "integer", "minimum": 1},
                "expected_part_path": {"type": "string", "maxLength": 4096},
                "artifact_scope": {"type": "string", "enum": ["saved_independent_copy", "current_work_part"]},
                "authorize": {"type": "boolean", "const": True},
            },
            "required": ["expected_pid", "expected_part_path", "artifact_scope", "authorize"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_return",
        "title": "Return NX session ownership",
        "description": "Returns AI ownership of the NX session identified by ownership_id and binding_epoch.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "ownership_id": {"type": "string", "minLength": 1},
                "binding_epoch": {"type": "integer", "minimum": 1},
            },
            "required": ["ownership_id", "binding_epoch"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_get_session",
        "title": "Get NX session",
        "description": "Reads session metadata for ownership_id and binding_epoch. Model reads are enabled only while the session is AI_OWNED.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "ownership_id": {"type": "string", "minLength": 1},
                "binding_epoch": {"type": "integer", "minimum": 1},
            },
            "required": ["ownership_id", "binding_epoch"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_inspect_part",
        "title": "Inspect NX part",
        "description": "Reads the owned NX part identified by ownership_id and binding_epoch. This model read is enabled only while the session is AI_OWNED.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "ownership_id": {"type": "string", "minLength": 1},
                "binding_epoch": {"type": "integer", "minimum": 1},
            },
            "required": ["ownership_id", "binding_epoch"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_query_faces",
        "title": "Query NX faces",
        "description": (
            "Searches faces in the owned NX part using a natural-language selector, or pass the exact "
            "selector 'all_planar' to enumerate planar face candidates. Enumeration returns candidate "
            "records including face_journal_id and the current-session face_tag for explicit support-face "
            "selection in nx_prepare_sketch. When a JournalIdentifier repeats, pass both as support_face_id "
            "and support_face_tag in the same session and revision; face_tag is not persistent across reopen. "
            "Other selectors are passed to the model query unchanged; ambiguous and unsupported selectors "
            "are reported by the backend for clarification. Optional body_id scopes the search to one "
            "solid body. This is a read-only model query."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "selector": dict(_NONEMPTY_STRING_SCHEMA),
                "body_id": dict(_NONEMPTY_STRING_SCHEMA),
            },
            "required": ["ownership_id", "binding_epoch", "revision_id", "selector"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_inspect_sketch",
        "title": "Inspect NX sketch",
        "description": "Reads sketch_id from the owned NX part identified by ownership_id and binding_epoch. This model read is enabled only while the session is AI_OWNED.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "ownership_id": {"type": "string", "minLength": 1},
                "binding_epoch": {"type": "integer", "minimum": 1},
                "sketch_id": {"type": "string", "minLength": 1},
            },
            "required": ["ownership_id", "binding_epoch", "sketch_id"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_get_operation",
        "title": "Get NX operation",
        "description": "Returns the current record for a caller-supplied operation_id, including terminal or unknown status.",
        "inputSchema": {
            "type": "object",
            "properties": {"operation_id": dict(_OPERATION_ID_SCHEMA)},
            "required": ["operation_id"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_cancel_operation",
        "title": "Cancel NX operation",
        "description": "Requests cancellation of a queued cancellable operation by operation_id and returns its operation record.",
        "inputSchema": {
            "type": "object",
            "properties": {"operation_id": dict(_OPERATION_ID_SCHEMA)},
            "required": ["operation_id"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": False,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_datum_plane",
        "title": "Prepare NX datum plane",
        "description": (
            "Prepares a reusable datum plane from the legacy world-coordinate origin_mm, normal, and "
            "x_axis frame or as an associative signed offset from exactly one support_datum_plane_id "
            "or planar support_face_id. support_face_tag may be paired with support_face_id to disambiguate "
            "a repeated face JournalIdentifier in the current NX session; it is session-scoped and must not "
            "be reused after reopening the part. Offset distance_mm is signed millimeters along the reference's "
            "native normal; zero and negative distances are allowed. It can also create an associative angle "
            "plane from exactly one support datum plane or planar face, using axis_line_id to identify a native "
            "NX Line and angle_deg as a signed angle in degrees. The axis line must lie in the reference plane; "
            "positive angles follow the right-hand rule around Line.StartPoint to Line.EndPoint. Angle values "
            "from -180 through 180 degrees, including zero, are accepted. It can also create an associative "
            "plane from exactly two current-part native "
            "NX Lines selected by line1_id and line2_id. Nonparallel lines must intersect as infinite lines; "
            "parallel lines are accepted only when distinct and coplanar. Repeated, coincident, degenerate, "
            "skew, or non-Line objects are rejected. Fixed, offset, angle, and two-line inputs cannot be mixed. "
            "This returns a plan; it does not execute the plan or change the model. Use "
            "nx_execute_plan only after reviewing the returned plan."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "origin_mm": dict(_ORIGIN_3_SCHEMA),
                "normal": dict(_VECTOR_3_SCHEMA),
                "x_axis": dict(_VECTOR_3_SCHEMA),
                "support_datum_plane_id": dict(_NONEMPTY_STRING_SCHEMA),
                "support_face_id": dict(_NONEMPTY_STRING_SCHEMA),
                "support_face_tag": dict(_NONEMPTY_STRING_SCHEMA),
                "distance_mm": dict(_NUMBER_SCHEMA),
                "axis_line_id": dict(_NONEMPTY_STRING_SCHEMA),
                "angle_deg": dict(_NUMBER_SCHEMA),
                "line1_id": dict(_NONEMPTY_STRING_SCHEMA),
                "line2_id": dict(_NONEMPTY_STRING_SCHEMA),
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "revision_id"],
            "oneOf": [
                {
                    "required": ["origin_mm", "normal", "x_axis"],
                    "not": {"anyOf": [
                        {"required": ["support_datum_plane_id"]},
                        {"required": ["support_face_id"]},
                        {"required": ["support_face_tag"]},
                        {"required": ["distance_mm"]},
                        {"required": ["axis_line_id"]},
                        {"required": ["angle_deg"]},
                        {"required": ["line1_id"]},
                        {"required": ["line2_id"]},
                    ]},
                },
                {
                    "required": ["support_datum_plane_id", "distance_mm"],
                    "not": {"anyOf": [
                        {"required": ["support_face_id"]},
                        {"required": ["support_face_tag"]},
                        {"required": ["origin_mm"]},
                        {"required": ["normal"]},
                        {"required": ["x_axis"]},
                        {"required": ["axis_line_id"]},
                        {"required": ["angle_deg"]},
                        {"required": ["line1_id"]},
                        {"required": ["line2_id"]},
                    ]},
                },
                {
                    "required": ["support_face_id", "distance_mm"],
                    "not": {"anyOf": [
                        {"required": ["support_datum_plane_id"]},
                        {"required": ["origin_mm"]},
                        {"required": ["normal"]},
                        {"required": ["x_axis"]},
                        {"required": ["axis_line_id"]},
                        {"required": ["angle_deg"]},
                        {"required": ["line1_id"]},
                        {"required": ["line2_id"]},
                    ]},
                },
                {
                    "required": ["support_datum_plane_id", "axis_line_id", "angle_deg"],
                    "not": {"anyOf": [
                        {"required": ["support_face_id"]},
                        {"required": ["support_face_tag"]},
                        {"required": ["distance_mm"]},
                        {"required": ["origin_mm"]},
                        {"required": ["normal"]},
                        {"required": ["x_axis"]},
                        {"required": ["line1_id"]},
                        {"required": ["line2_id"]},
                    ]},
                },
                {
                    "required": ["support_face_id", "axis_line_id", "angle_deg"],
                    "not": {"anyOf": [
                        {"required": ["support_datum_plane_id"]},
                        {"required": ["distance_mm"]},
                        {"required": ["origin_mm"]},
                        {"required": ["normal"]},
                        {"required": ["x_axis"]},
                        {"required": ["line1_id"]},
                        {"required": ["line2_id"]},
                    ]},
                },
                {
                    "required": ["line1_id", "line2_id"],
                    "not": {"anyOf": [
                        {"required": ["origin_mm"]},
                        {"required": ["normal"]},
                        {"required": ["x_axis"]},
                        {"required": ["support_datum_plane_id"]},
                        {"required": ["support_face_id"]},
                        {"required": ["support_face_tag"]},
                        {"required": ["distance_mm"]},
                        {"required": ["axis_line_id"]},
                        {"required": ["angle_deg"]},
                    ]},
                },
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_sketch",
        "title": "Prepare NX native sketch",
        "description": (
            "Prepares a sketch in a free frame, on an explicitly identified support face, or on an "
            "identified datum plane using local millimeter coordinates. A free frame uses origin_mm, "
            "normal, and x_axis. Explicit face support uses support_face_id, origin_mm, and x_axis, with "
            "optional support_face_tag to disambiguate a repeated face JournalIdentifier in the current "
            "NX session; the tag is session-scoped and must not be reused after reopening the part. "
            "its normal comes from the face and may be scoped by support_body_id. Datum support uses "
            "support_datum_plane_id and x_axis; its normal comes from the datum, and optional origin_mm "
            "defaults to the datum origin. Legacy XY/XZ/YZ plus origin_mm and selector-based "
            "support_face_selector modes remain available, including face_origin_offset_mm. "
            "geometries may be omitted or empty to prepare an empty sketch; otherwise supports line segments, "
            "rectangles, circles, circular arcs, ellipses, elliptical arcs, and cubic interpolating splines. "
            "Arc angles use the sketch-local +X axis and signed counterclockwise sweep; ellipse angles use the "
            "ellipse parameter angle. Spline input contains 4 through 32 interpolation points, uses a nonperiodic "
            "degree-3 chord-length fit, and uses endpoint_condition='unconstrained' with no endpoint tangent or curvature condition. Multiple closed "
            "contours can select constraint_mode='geometry_only' to create native geometry without Sketch constraints; "
            "the default 'legacy_driving' mode preserves existing Driving dimensions for lines, rectangles, and circles. "
            "NX solver errors and OverConstrained sketches are always rejected. Multiple closed contours, holes, and "
            "open sketch geometry are checked against NX geometry and section behavior after "
            "execution; adding curves alone does not claim a valid region. This does not execute the plan."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "plane": {"type": "string", "enum": ["XY", "XZ", "YZ"]},
                "origin_mm": dict(_ORIGIN_3_SCHEMA),
                "normal": dict(_VECTOR_3_SCHEMA),
                "x_axis": dict(_VECTOR_3_SCHEMA),
                "support_face_selector": dict(_NONEMPTY_STRING_SCHEMA),
                "support_face_id": dict(_NONEMPTY_STRING_SCHEMA),
                "support_face_tag": dict(_NONEMPTY_STRING_SCHEMA),
                "support_datum_plane_id": dict(_NONEMPTY_STRING_SCHEMA),
                "support_body_id": dict(_NONEMPTY_STRING_SCHEMA),
                "face_origin_offset_mm": {**dict(_POINT_2_SCHEMA), "default": [0, 0]},
                "constraint_mode": {"type": "string", "enum": ["legacy_driving", "geometry_only"], "default": "legacy_driving"},
                "geometries": {
                    "type": "array",
                    "items": _NATIVE_GEOMETRY_SCHEMA,
                    "minItems": 0,
                    "maxItems": 32,
                },
            },
            "required": [
                "operation_id", "ownership_id", "binding_epoch", "revision_id",
            ],
            "oneOf": [
                {
                    "required": ["plane", "origin_mm"],
                    "not": {"anyOf": [
                        {"required": ["normal"]},
                        {"required": ["x_axis"]},
                        {"required": ["support_face_selector"]},
                        {"required": ["support_face_id"]},
                        {"required": ["support_face_tag"]},
                        {"required": ["support_datum_plane_id"]},
                        {"required": ["face_origin_offset_mm"]},
                        {"required": ["support_body_id"]},
                    ]},
                },
                {
                    "required": ["support_face_selector"],
                    "not": {"anyOf": [
                        {"required": ["plane"]},
                        {"required": ["origin_mm"]},
                        {"required": ["normal"]},
                        {"required": ["x_axis"]},
                        {"required": ["support_face_id"]},
                        {"required": ["support_face_tag"]},
                        {"required": ["support_datum_plane_id"]},
                    ]},
                },
                {
                    "required": ["origin_mm", "normal", "x_axis"],
                    "not": {"anyOf": [
                        {"required": ["plane"]},
                        {"required": ["support_face_selector"]},
                        {"required": ["support_face_id"]},
                        {"required": ["support_face_tag"]},
                        {"required": ["support_datum_plane_id"]},
                        {"required": ["support_body_id"]},
                        {"required": ["face_origin_offset_mm"]},
                    ]},
                },
                {
                    "required": ["support_face_id", "origin_mm", "x_axis"],
                    "not": {"anyOf": [
                        {"required": ["plane"]},
                        {"required": ["support_face_selector"]},
                        {"required": ["support_datum_plane_id"]},
                        {"required": ["normal"]},
                        {"required": ["face_origin_offset_mm"]},
                    ]},
                },
                {
                    "required": ["support_datum_plane_id", "x_axis"],
                    "not": {"anyOf": [
                        {"required": ["plane"]},
                        {"required": ["support_face_selector"]},
                        {"required": ["support_face_id"]},
                        {"required": ["support_face_tag"]},
                        {"required": ["support_body_id"]},
                        {"required": ["normal"]},
                        {"required": ["face_origin_offset_mm"]},
                    ]},
                },
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_sketch_corner",
        "title": "Prepare NX sketch fillet or chamfer",
        "description": (
            "Prepares a preview plan for a fillet or chamfer between two explicitly selected sketch curves. "
            "Use curve IDs from nx_inspect_sketch and select each curve's start or end explicitly. "
            "Fillets use radius_mm. Chamfers use distance1_mm and exactly one of distance2_mm or angle_deg; "
            "Fillets support Line-Line, Line-Arc, and Arc-Arc, while chamfers support Line-Line only. "
            "The angle is measured at the first trim point between A→C and A→B, where C is the original "
            "corner and A and B are the two trim points. Linear values are millimeters and angles are degrees. "
            "This returns a plan and does not execute it."
        ),
        "inputSchema": _sketch_corner_schema(),
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_edit_sketch_corner",
        "title": "Edit and verify NX sketch fillet or chamfer",
        "description": (
            "Runs prepare, execute, and independent verification for the selected sketch corner under one "
            "operation_id. The returned operation record is successful only when state is SUCCEEDED and "
            "verified is true. If the outcome is UNKNOWN, query it with nx_get_operation before making "
            "another write; this tool never replays an uncertain operation. Fillets use radius_mm. Chamfers "
            "use distance1_mm and exactly one of distance2_mm or angle_deg, measured at the first trim point "
            "between A→C and A→B. Fillets support Line-Line, Line-Arc, and Arc-Arc; chamfers support Line-Line. "
            "Linear values are millimeters and angles are degrees."
        ),
        "inputSchema": _sketch_corner_schema(),
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": True,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_sketch_offset",
        "title": "Prepare NX sketch offset",
        "description": (
            "Prepares a read-only preview for an explicitly ordered chain of native Line or circular Arc curves "
            "in one sketch. Each curve entry gives its traversal direction. distance_mm is positive; side is "
            "left or right in the sketch plane relative to that traversal. retain_original must be true. join_mode "
            "selects the native SketchOffsetBuilder extension or arc cap. Unsupported curves, disconnected or "
            "ambiguous chains, and unverified conditions are rejected before mutation. This tool does not edit the model."
        ),
        "inputSchema": _sketch_offset_schema(),
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_edit_sketch_offset",
        "title": "Offset and verify NX sketch curves",
        "description": (
            "Runs native prepare, offset execution, and independent verification under one operation_id. "
            "The result retains the source curves and reports measured offset geometry and chain closure. "
            "Success requires state=SUCCEEDED and verified=true. If the outcome is UNKNOWN, query nx_get_operation "
            "before another write; this tool never replays an uncertain operation. distance_mm is positive and "
            "side is relative to the ordered curve traversal in the sketch plane. join_mode is extension or arc."
        ),
        "inputSchema": _sketch_offset_schema(),
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": True,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_sketch_pattern",
        "title": "Prepare NX sketch pattern",
        "description": (
            "Prepares a read-only preview for copying explicitly selected native Line and circular Arc curves "
            "in one sketch. Modes are linear, rectangular, circular, and mirror. Counts include the source "
            "position; linear and rectangular directions are sketch-local vectors, circular angle steps are "
            "signed degrees, and mirror uses a separate native axis curve. The preview reports expected "
            "instances and transforms. Linear, rectangular, and circular edits create independent geometry-only "
            "curves; mirror uses NX's sketch mirror builder. No associative array feature is created. This tool "
            "does not edit the model."
        ),
        "inputSchema": _sketch_pattern_schema(),
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_edit_sketch_pattern",
        "title": "Pattern and verify NX sketch curves",
        "description": (
            "Runs pattern preparation, execution, and independent verification under one operation_id. "
            "Success requires state=SUCCEEDED and verified=true. If the outcome is UNKNOWN, query "
            "nx_get_operation before another write; this tool never replays an uncertain operation. "
            "Counts include the source position and the preview reports expected instances and transforms. "
            "Linear, rectangular, and circular outputs are independent geometry-only curves, not an associative "
            "SketchPattern feature; mirror uses NX's sketch mirror builder."
        ),
        "inputSchema": _sketch_pattern_schema(),
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": True,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_sketch_projection",
        "title": "Prepare NX sketch projection",
        "description": (
            "Prepares a read-only plan to project explicitly selected source curves into the target sketch. "
            "direction_xyz is a nonzero world XYZ vector and is normalized by the server; the NX adapter "
            "supports only directions parallel or antiparallel to the target sketch normal (±target sketch normal) "
            "and rejects other directions during prepare. associative selects whether the projected geometry "
            "maintains an associative relationship to its source. This tool does not edit the model."
        ),
        "inputSchema": _sketch_projection_schema(),
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_edit_sketch_projection",
        "title": "Project and verify NX sketch curves",
        "description": (
            "Runs sketch projection preparation, execution, and independent verification under one operation_id. "
            "Success requires state=SUCCEEDED and verified=true. If the outcome is UNKNOWN, query nx_get_operation "
            "before another write; this tool never replays an uncertain operation. direction_xyz is a nonzero "
            "world XYZ vector normalized by the server; only directions parallel or antiparallel to the target "
            "sketch normal (±target sketch normal) are supported, and the NX adapter rejects other directions "
            "during prepare. associative selects whether the projected geometry maintains an associative "
            "relationship to its source."
        ),
        "inputSchema": _sketch_projection_schema(),
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": True,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_sketch_reference_conversion",
        "title": "Prepare NX sketch reference conversion",
        "description": (
            "Prepares a read-only plan for explicitly selected Line/Arc curves (including full circles) and horizontal/vertical length dimensions in one sketch. "
            "target_state='reference' makes curves reference geometry and dimensions reference dimensions; "
            "target_state='active' maps to active curves and Driving dimensions (the NX 'Convert from Reference' behavior). "
            "Reference curves may still participate in geometric constraints on other sketch geometry. "
            "Other curve and dimension types are explicitly unsupported until their native conversion/unit mapping is verified. "
            "Use current object IDs from nx_inspect_sketch; this does not execute the conversion."
        ),
        "inputSchema": _sketch_reference_conversion_schema(),
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_edit_sketch_reference_conversion",
        "title": "Convert NX sketch reference states",
        "description": (
            "Runs Line/Arc curve or horizontal/vertical length dimension conversion and independent verification under one operation_id. "
            "The only target_state values are reference and active; active makes dimensions Driving. "
            "Ellipse, spline and other curve types, plus other dimension types, are explicitly unsupported until their native conversion/unit mapping is verified. "
            "NX controls whether newly Driving dimensions remain consistent with other sketch constraints; if the "
            "solver reports a new overconstrained or inconsistent state, the edit is undone and verified. "
            "For evaluated solver status, the edit sets the Sketch work-region scope to EntireSketch; a prior local "
            "selected-object work-region UI preference is not guaranteed to be restored. The edit also temporarily "
            "enables NX's FindMovableObjects sketch preference for CalculateStatus, then restores and verifies its "
            "original session value; it does not save user preferences. "
            "Existing source associations and unselected constraints are preserved. If the outcome is UNKNOWN, "
            "query nx_get_operation before another write; this tool never replays an uncertain operation."
        ),
        "inputSchema": _sketch_reference_conversion_schema(),
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": True,
            "idempotentHint": True,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_extrude",
        "title": "Prepare NX extrusion",
        "description": (
            "Prepares a finite or through-all create, unite, or subtract extrusion from an existing "
            "explicit Sketch. The solid profile uses all valid closed regions and nested loops "
            "formed by non-reference Line, Arc, Ellipse, and Spline curves in that Sketch. "
            "Finite limits accept either legacy depth_mm or a signed start_offset_mm/end_offset_mm "
            "pair. Open geometry is never treated as a solid; sheet output must be requested "
            "explicitly with profile_output='sheet', and the NX native builder must validate it. "
            "Through-all is limited to solid Boolean operations with an explicit target body. "
            "This tool prepares a plan and does not execute it."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "sketch_id": dict(_NONEMPTY_STRING_SCHEMA),
                "mode": {"type": "string", "enum": ["create", "unite", "subtract"]},
                "direction": {"type": "string", "enum": ["positive", "negative"]},
                "extent": {"type": "string", "enum": ["finite", "through_all"]},
                "profile_output": {"type": "string", "enum": ["solid", "sheet"], "default": "solid"},
                "depth_mm": {"type": "number", "exclusiveMinimum": 0},
                "start_offset_mm": {"type": "number"},
                "end_offset_mm": {"type": "number"},
                "target_body_id": dict(_NONEMPTY_STRING_SCHEMA),
            },
            "required": [
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                "mode", "direction", "extent",
            ],
            "allOf": [
                {
                    "if": {"properties": {"mode": {"const": "create"}}},
                    "then": {
                        "properties": {"extent": {"const": "finite"}},
                        "not": {"required": ["target_body_id"]},
                    },
                },
                {
                    "if": {"properties": {"mode": {"enum": ["unite", "subtract"]}}},
                    "then": {"required": ["target_body_id"]},
                },
                {
                    "if": {"properties": {"extent": {"const": "finite"}}},
                    "then": {
                        "oneOf": [
                            {
                                "required": ["depth_mm"],
                                "not": {"anyOf": [{"required": ["start_offset_mm"]}, {"required": ["end_offset_mm"]}]},
                            },
                            {
                                "required": ["start_offset_mm", "end_offset_mm"],
                                "not": {"required": ["depth_mm"]},
                            },
                        ]
                    },
                    "else": {
                        "not": {"anyOf": [{"required": ["depth_mm"]}, {"required": ["end_offset_mm"]}]},
                    },
                },
                {
                    "if": {
                        "properties": {"profile_output": {"const": "sheet"}},
                        "required": ["profile_output"],
                    },
                    "then": {
                        "properties": {
                            "mode": {"const": "create"},
                            "extent": {"const": "finite"},
                        }
                    },
                },
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_sweep",
        "title": "Prepare NX sweep",
        "description": (
            "Prepare a create-only sweep from an explicit Sketch and ordered path curve IDs. "
            "transport uses native Fixed orientation and requires twist_angle_deg=0. linear_twist uses "
            "native ByAngularLaw Linear for the requested right-hand end angle. Verification assumes twist "
            "proportional to normalized path arc length at finite stations and rejects samples outside tolerance; "
            "it does not prove that progression over the entire path. Angles are in [-90, 90] degrees. "
            "Execute with nx_execute_plan and verify with nx_verify_part."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA), "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA), "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "sketch_id": dict(_NONEMPTY_STRING_SCHEMA),
                "path_curve_ids": {"type": "array", "minItems": 1, "maxItems": 32, "uniqueItems": True, "items": dict(_NONEMPTY_STRING_SCHEMA)},
                "profile_output": {"type": "string", "enum": ["solid", "sheet"]},
                "orientation_mode": {"type": "string", "enum": ["transport", "linear_twist"]},
                "twist_angle_deg": {"type": "number", "minimum": -90, "maximum": 90},
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id", "path_curve_ids", "profile_output", "orientation_mode", "twist_angle_deg"],
            "allOf": [{"if": {"properties": {"orientation_mode": {"const": "transport"}}, "required": ["orientation_mode"]},
                       "then": {"properties": {"twist_angle_deg": {"const": 0}}}}],
            "additionalProperties": False,
        },
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": False, "openWorldHint": False},
    },
    {
        "name": "nx_prepare_revolve",
        "title": "Prepare NX revolve",
        "description": (
            "Prepares a solid or sheet revolve from the complete active-curve Section of an explicit Sketch. "
            "axis_point_mm and axis_direction describe a world-space line in millimeters and XYZ; the line must "
            "lie in the Sketch plane, so arbitrarily oriented Sketches and in-plane axes are supported while "
            "off-plane axes are rejected. start_angle_deg and end_angle_deg are signed right-hand-rule endpoints "
            "about axis_direction; each is in [-360, 360], the signed span is nonzero and at most 360 degrees, "
            "and negative spans are preserved by reversing the native axis direction. Solid output requires "
            "valid closed whole-Sketch regions; sheet output is explicit, create-only, and must pass NX native "
            "builder validation. Unite and subtract require one explicit solid target body. This prepares a plan "
            "only; execute it with nx_execute_plan, then call nx_verify_part for a fresh native readback."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "sketch_id": dict(_NONEMPTY_STRING_SCHEMA),
                "mode": {"type": "string", "enum": ["create", "unite", "subtract"]},
                "profile_output": {"type": "string", "enum": ["solid", "sheet"], "default": "solid"},
                "axis_point_mm": dict(_HELICAL_ORIGIN_3_SCHEMA),
                "axis_direction": dict(_VECTOR_3_SCHEMA),
                "start_angle_deg": dict(_REVOLVE_ANGLE_SCHEMA),
                "end_angle_deg": dict(_REVOLVE_ANGLE_SCHEMA),
                "target_body_id": dict(_NONEMPTY_STRING_SCHEMA),
            },
            "required": [
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id", "mode",
                "axis_point_mm", "axis_direction", "start_angle_deg", "end_angle_deg",
            ],
            "allOf": [
                {
                    "if": {"properties": {"mode": {"const": "create"}}, "required": ["mode"]},
                    "then": {"not": {"required": ["target_body_id"]}},
                },
                {
                    "if": {"properties": {"mode": {"enum": ["unite", "subtract"]}}, "required": ["mode"]},
                    "then": {"required": ["target_body_id"]},
                },
                {
                    "if": {"properties": {"profile_output": {"const": "sheet"}}, "required": ["profile_output"]},
                    "then": {"properties": {"mode": {"const": "create"}}},
                },
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_boolean",
        "title": "Prepare solid Boolean",
        "description": "Prepare a native Boolean on one explicit target and 1 to 32 distinct tool solid bodies. Intersect computes the target intersected with the union of all tools. retain_tools is mandatory; unchanged or empty results and results whose preservation cannot be proven are rejected. Execute with nx_execute_plan and verify with nx_verify_part.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA), "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA), "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "operation": {"type": "string", "enum": ["unite", "subtract", "intersect"]},
                "target_body_id": dict(_NONEMPTY_STRING_SCHEMA),
                "tool_body_ids": {"type": "array", "minItems": 1, "maxItems": 32, "uniqueItems": True, "items": dict(_NONEMPTY_STRING_SCHEMA)},
                "retain_tools": {"type": "boolean"},
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "revision_id", "operation", "target_body_id", "tool_body_ids", "retain_tools"],
            "additionalProperties": False,
        },
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": False, "openWorldHint": False},
    },
    {
        "name": "nx_prepare_spatial_curve",
        "title": "Prepare spatial curve",
        "description": "Prepare a native world XYZ spatial line, circular arc or full circle, independent of any sketch. Coordinates and radius are mm; angles are degrees, with signed sweep end minus start in the right-handed normal/x_axis frame. normal and x_axis must be unit and perpendicular within 1e-9. Execute using nx_execute_plan.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA), "ownership_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA), "revision_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "curve_type": {"type": "string", "enum": ["line", "arc", "circle"]},
                "definition_mode": {"type": "string", "enum": ["center", "three_points"]},
                "start_mm": dict(_VECTOR_3_SCHEMA), "through_mm": dict(_VECTOR_3_SCHEMA), "end_mm": dict(_VECTOR_3_SCHEMA),
                "center_mm": dict(_VECTOR_3_SCHEMA), "normal": dict(_VECTOR_3_SCHEMA), "x_axis": dict(_VECTOR_3_SCHEMA),
                "radius_mm": {"type": "number", "exclusiveMinimum": 0},
                "start_angle_deg": {"type": "number"}, "end_angle_deg": {"type": "number"},
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "revision_id", "curve_type"],
            "oneOf": [
                {"properties": {"curve_type": {"const": "line"}}, "required": ["start_mm", "end_mm"], "not": {"anyOf": [{"required": [k]} for k in ("definition_mode", "through_mm", "center_mm", "normal", "x_axis", "radius_mm", "start_angle_deg", "end_angle_deg")]}},
                {"properties": {"curve_type": {"const": "arc"}, "definition_mode": {"const": "three_points"}}, "required": ["definition_mode", "start_mm", "through_mm", "end_mm"], "not": {"anyOf": [{"required": [k]} for k in ("center_mm", "normal", "x_axis", "radius_mm", "start_angle_deg", "end_angle_deg")]}},
                {"properties": {"curve_type": {"const": "arc"}, "definition_mode": {"const": "center"}}, "required": ["definition_mode", "center_mm", "normal", "x_axis", "radius_mm", "start_angle_deg", "end_angle_deg"], "not": {"anyOf": [{"required": [k]} for k in ("start_mm", "through_mm", "end_mm")]}},
                {"properties": {"curve_type": {"const": "circle"}}, "required": ["center_mm", "normal", "x_axis", "radius_mm"], "not": {"anyOf": [{"required": [k]} for k in ("definition_mode", "start_mm", "through_mm", "end_mm", "start_angle_deg", "end_angle_deg")]}},
            ], "additionalProperties": False,
        },
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": False, "openWorldHint": False},
    },
    {
        "name": "nx_prepare_feature_mirror",
        "title": "Prepare feature mirror",
        "description": "Prepare a native feature mirror using either a mathematical plane (origin in mm and normal of unit length within 1e-9) or an explicit datum plane ID. The normal is not normalized automatically. Execute the returned plan with nx_execute_plan.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "seed_feature_id": dict(_HELICAL_TARGET_BODY_SCHEMA),
                "plane_origin_mm": dict(_ORIGIN_3_SCHEMA),
                "plane_normal": {**_VECTOR_3_SCHEMA, "not": {"const": [0, 0, 0]}},
                "datum_plane_id": dict(_HELICAL_TARGET_BODY_SCHEMA),
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "revision_id", "seed_feature_id"],
            "oneOf": [
                {"required": ["plane_origin_mm", "plane_normal"], "not": {"required": ["datum_plane_id"]}},
                {"required": ["datum_plane_id"], "not": {"anyOf": [{"required": ["plane_origin_mm"]}, {"required": ["plane_normal"]}]}},
            ],
            "additionalProperties": False,
        },
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": False, "openWorldHint": False},
    },
    {
        "name": "nx_prepare_feature_pattern",
        "title": "Prepare feature pattern",
        "description": (
            "Prepare a linear, rectangular or circular native feature pattern. Directions must be unit "
            "vectors within 1e-9 and rectangular directions orthogonal within 1e-9. Each count is "
            "2 to 32 and the rectangular count product is at most 32. Circular angle_deg is the signed step; "
            "its magnitude times count1 minus one must be less than 360 degrees. Prepare does not "
            "modify the part; execute the returned plan with nx_execute_plan."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "seed_feature_id": dict(_HELICAL_TARGET_BODY_SCHEMA),
                "mode": {"type": "string", "enum": ["linear", "rectangular", "circular"]},
                "count1": {"type": "integer", "minimum": 2, "maximum": 32},
                "count2": {"type": "integer", "minimum": 2, "maximum": 32},
                "direction1": dict(_VECTOR_3_SCHEMA),
                "direction2": dict(_VECTOR_3_SCHEMA),
                "pitch1_mm": {"type": "number", "exclusiveMinimum": 0},
                "pitch2_mm": {"type": "number", "exclusiveMinimum": 0},
                "axis_origin_mm": dict(_VECTOR_3_SCHEMA),
                "axis_direction": dict(_VECTOR_3_SCHEMA),
                "angle_deg": {"type": "number", "not": {"const": 0}},
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "revision_id", "seed_feature_id", "mode", "count1"],
            "oneOf": [
                {"properties": {"mode": {"const": "linear"}}, "required": ["direction1", "pitch1_mm"],
                 "not": {"anyOf": [{"required": [field]} for field in ("direction2", "pitch2_mm", "count2", "axis_origin_mm", "axis_direction", "angle_deg")]}},
                {"properties": {"mode": {"const": "rectangular"}}, "required": ["direction1", "pitch1_mm", "direction2", "pitch2_mm", "count2"],
                 "not": {"anyOf": [{"required": [field]} for field in ("axis_origin_mm", "axis_direction", "angle_deg")]}},
                {"properties": {"mode": {"const": "circular"}}, "required": ["axis_origin_mm", "axis_direction", "angle_deg"],
                 "not": {"anyOf": [{"required": [field]} for field in ("direction1", "pitch1_mm", "direction2", "pitch2_mm", "count2")]}},
            ],
            "additionalProperties": False,
        },
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": False, "openWorldHint": False},
    },
    {
        "name": "nx_prepare_chamfer",
        "title": "Prepare solid chamfer",
        "description": (
            "Prepare a native chamfer on one explicit edge of an intact world-axis-aligned solid box. "
            "reference_axis (x, y or z) is a transverse world axis; distance_mm measures inward "
            "from the edge along that axis. two_distances requires second_distance_mm along the "
            "other transverse axis. distance_angle requires angle_deg = atan2(second distance, "
            "distance_mm), strictly between 0 and 90 degrees. equal_distance uses equal offsets. "
            "Only one edge is supported. Prepare does not modify the part."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "target_body_id": dict(_HELICAL_TARGET_BODY_SCHEMA),
                "edge_ids": {"type": "array", "minItems": 1, "maxItems": 1, "uniqueItems": True,
                             "items": dict(_HELICAL_TARGET_BODY_SCHEMA)},
                "mode": {"type": "string", "enum": ["equal_distance", "two_distances", "distance_angle"]},
                "reference_axis": {"type": "string", "enum": ["x", "y", "z"]},
                "distance_mm": {"type": "number", "exclusiveMinimum": 0},
                "second_distance_mm": {"type": "number", "exclusiveMinimum": 0},
                "angle_deg": {"type": "number", "exclusiveMinimum": 0, "exclusiveMaximum": 90},
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
                         "edge_ids", "mode", "reference_axis", "distance_mm"],
            "oneOf": [
                {"properties": {"mode": {"const": "equal_distance"}}, "not": {"anyOf": [{"required": ["second_distance_mm"]}, {"required": ["angle_deg"]}]}},
                {"properties": {"mode": {"const": "two_distances"}}, "required": ["second_distance_mm"], "not": {"required": ["angle_deg"]}},
                {"properties": {"mode": {"const": "distance_angle"}}, "required": ["angle_deg"], "not": {"required": ["second_distance_mm"]}},
            ],
            "additionalProperties": False,
        },
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": False, "openWorldHint": False},
    },
    {
        "name": "nx_prepare_edge_blend",
        "title": "Prepare edge blend",
        "description": (
            "Prepares a constant-radius edge blend on an explicit target body and 1 to 32 distinct "
            "edge IDs from the current revision. Availability and write enablement are reported by "
            "model_edge_blend_adapter_available and model_edge_blend_write_enabled. The supported "
            "geometry is an intact, unmodified, world-axis-aligned rectangular block with selected "
            "nonadjacent parallel straight edges; radius must be less than half of each transverse "
            "dimension. This domain permits at most four edges, although the protocol accepts up to "
            "32 IDs. The bridge validates topology. This only prepares a plan; review it before "
            "executing with nx_execute_plan."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "target_body_id": dict(_HELICAL_TARGET_BODY_SCHEMA),
                "edge_ids": {"type": "array", "minItems": 1, "maxItems": 32, "uniqueItems": True,
                             "items": dict(_HELICAL_TARGET_BODY_SCHEMA)},
                "radius_mm": {"type": "number", "exclusiveMinimum": 0},
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "revision_id",
                         "target_body_id", "edge_ids", "radius_mm"],
            "additionalProperties": False,
        },
        "annotations": {"readOnlyHint": True, "destructiveHint": False,
                        "idempotentHint": False, "openWorldHint": False},
    },
    {
        "name": "nx_prepare_hole",
        "title": "Prepare hole",
        "description": (
            "Prepares a hole plan only when the bridge reports that its hole adapter is available "
            "and writes are enabled. position_mm is a world XYZ point; direction is a nonzero "
            "world vector pointing into the target body and is normalized. The position must lie "
            "inside one planar face, and direction must be normal to that face. "
            "The 0.01 mm inward/outward entry witness can reject thin or locally complex bodies; "
            "holes whose complete wall geometry cannot be proven are rejected. "
            "Coverage currently requires a single analytic face per wall segment, radius at least "
            "0.05 mm and wall segments at least 0.1 mm; split or intersected walls are rejected. "
            "For through holes, the target must not extend past the verified exit plane along the axis. "
            "Position coordinates are limited to +/-1000000 mm and positive linear dimensions to 1000000 mm. "
            "Blind hole_depth_mm measures to the cylindrical bottom; through holes "
            "exclude it. Counterbore and countersink diameters must exceed diameter_mm; their "
            "depth must be less than the blind hole depth. Countersink angle is included angle "
            "in degrees, strictly between 0 and 180. Threaded holes specify thread metadata, "
            "including the required radial_engage string, and "
            "thread_depth_mm, which must not exceed blind hole depth; no physical thread geometry "
            "is created or promised. All numbers must be finite and all dimensions positive."
        ),
        "inputSchema": _hole_schema(),
        "annotations": {"readOnlyHint": True, "destructiveHint": False,
                        "idempotentHint": False, "openWorldHint": False},
    },
    {
        "name": "nx_prepare_thread",
        "title": "Prepare external solid thread",
        "description": (
            "Prepares a single-start, external solid thread with a fixed 60-degree profile on an "
            "existing target body when model_helical_adapter_available and "
            "model_helical_write_enabled are true; it does not execute the plan. The caller must provide the "
            "major diameter, minor diameter, and pitch explicitly because no drawing or model "
            "assumptions are made. Axis is X, Y, or Z; axis_origin_mm is a world XYZ point and "
            "start_mm is the signed axial offset from it. direction selects the positive or negative "
            "axis, and handedness selects the helix sense. Linear inputs are limited to an absolute "
            "value of 1,000,000 mm. length_mm / pitch_mm must not exceed 500."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "target_body_id": dict(_HELICAL_TARGET_BODY_SCHEMA),
                "axis": dict(_AXIS_SCHEMA),
                "axis_origin_mm": dict(_HELICAL_ORIGIN_3_SCHEMA),
                "start_mm": dict(_HELICAL_COORDINATE_SCHEMA),
                "length_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "handedness": dict(_HANDEDNESS_SCHEMA),
                "direction": {"type": "string", "enum": ["positive", "negative"]},
                "major_diameter_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "minor_diameter_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "pitch_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
            },
            "required": [
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
                "axis", "axis_origin_mm", "start_mm", "length_mm", "handedness", "direction",
                "major_diameter_mm", "minor_diameter_mm", "pitch_mm",
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_helical_groove",
        "title": "Prepare helical groove",
        "description": (
            "Prepares a subtractive helical groove whose axis advances along the positive selected "
            "axis; axis_origin_mm is a world XYZ point and start_mm is its signed axial offset. "
            "handedness determines the sign of angular travel. The operation requires "
            "model_helical_adapter_available and model_helical_write_enabled to be true. The caller must provide the "
            "root, reference, and outer diameters and the groove space width explicitly; no drawing "
            "assumptions are made. space_width_mm is the axial groove width at the reference "
            "diameter. The flank half-width varies linearly with radius using flank_angle_deg. "
            "length_mm / lead_mm must not exceed 50. The prepared profile uses a fixed 96-point-per- "
            "turn interpolated spline and 0.001 mm distance tolerance; linear inputs are limited to "
            "an absolute value of 1,000,000 mm. The profile must remain "
            "positive at the root and avoid overlap between adjacent turns. This only prepares a "
            "plan and does not execute it."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_HELICAL_IDENTIFIER_SCHEMA),
                "target_body_id": dict(_HELICAL_TARGET_BODY_SCHEMA),
                "axis": dict(_AXIS_SCHEMA),
                "axis_origin_mm": dict(_HELICAL_ORIGIN_3_SCHEMA),
                "start_mm": dict(_HELICAL_COORDINATE_SCHEMA),
                "length_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "handedness": dict(_HANDEDNESS_SCHEMA),
                "lead_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "root_diameter_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "reference_diameter_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "outer_diameter_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "space_width_mm": dict(_HELICAL_POSITIVE_MM_SCHEMA),
                "flank_angle_deg": {"type": "number", "exclusiveMinimum": 0, "exclusiveMaximum": 45},
                "phase_deg": {"type": "number", "minimum": -360, "maximum": 360},
            },
            "required": [
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
                "axis", "axis_origin_mm", "start_mm", "length_mm", "handedness", "lead_mm",
                "root_diameter_mm", "reference_diameter_mm", "outer_diameter_mm", "space_width_mm",
                "flank_angle_deg", "phase_deg",
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_prepare_operation",
        "title": "Prepare NX sketch operation",
        "description": (
            "Dynamically checks the identified Driving constraint, unique expression relation, "
            "Millimeters Work Part, exact mm expression unit, and current geometry before "
            "preparing an immutable plan for a positive finite value. Supported signatures are "
            "DiameterDim on one full Arc; HorizontalDim on ArcCenter plus Point for X offset; "
            "and HorizontalDim or VerticalDim on both endpoints of one Line for the corresponding "
            "projected length. Ambiguous, changed, or unsupported targets are refused. This does "
            "not execute the plan."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "revision_id": dict(_NONEMPTY_STRING_SCHEMA),
                "sketch_id": dict(_NONEMPTY_STRING_SCHEMA),
                "constraint_id": dict(_NONEMPTY_STRING_SCHEMA),
                "value": {"type": "string", "minLength": 1, "maxLength": 128, "pattern": _DECIMAL_PATTERN},
                "unit": dict(_NONEMPTY_STRING_SCHEMA),
            },
            "required": [
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                "constraint_id", "value", "unit",
            ],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_execute_plan",
        "title": "Execute NX plan",
        "description": (
            "Enqueues a previously prepared sketch, extrusion, or parameter-edit plan when "
            "model_write_enabled is true and writes are not halted, then promptly returns its "
            "queryable operation record."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "plan_id": dict(_NONEMPTY_STRING_SCHEMA),
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "plan_id"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": True,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_verify_part",
        "title": "Verify NX parameter edit",
        "description": (
            "Reads the live NX expression, measured sketch geometry, sketch feature history, and "
            "dimension associations for a succeeded execute operation. The verification itself is "
            "queryable and deduplicated by operation_id."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "execute_operation_id": dict(_OPERATION_ID_SCHEMA),
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "execute_operation_id"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": True,
            "destructiveHint": False,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_save_copy",
        "title": "Save and reopen NX copy",
        "description": (
            "Explicitly saves the currently owned Work Part to a new, nonexistent .prt path, then "
            "reopens that copy and compares actual expressions, sketch geometry, feature history, "
            "and dimensional associations. reopen=true is required for the round-trip evidence. "
            "The active binding and ownership credentials rotate after success; use the returned "
            "new_ownership_id and new_binding_epoch for later tools."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": dict(_OPERATION_ID_SCHEMA),
                "ownership_id": dict(_NONEMPTY_STRING_SCHEMA),
                "binding_epoch": dict(_POSITIVE_INTEGER_SCHEMA),
                "target_path": {"type": "string", "minLength": 1, "maxLength": 4096},
                "reopen": {"type": "boolean", "const": True},
            },
            "required": ["operation_id", "ownership_id", "binding_epoch", "target_path", "reopen"],
            "additionalProperties": False,
        },
        "annotations": {
            "readOnlyHint": False,
            "destructiveHint": True,
            "idempotentHint": False,
            "openWorldHint": False,
        },
    },
    {
        "name": "nx_run_journal",
        "description": "Start an explicitly supplied local Python Journal in a separate NX batch session. A running NX or bridge helper blocks it; this does not attach to the bridge work part. It executes arbitrary user code with external side effects that NX Undo cannot guarantee. Poll nx_get_journal; UNKNOWN is never replayed or automatically cleared.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "operation_id": {"type": "string", "pattern": "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$"},
                "language": {"const": "python"},
                "script_path": {"type": "string", "minLength": 1},
                "script_content": {"type": "string"},
                "args": {"type": "array", "items": {"type": "string"}},
                "observation_root": {"type": "string", "minLength": 1},
                "timeout_seconds": {"type": "number", "minimum": 1, "maximum": 86400}
            },
            "required": ["operation_id", "language", "args", "observation_root", "timeout_seconds"],
            "oneOf": [{"required": ["script_path"]}, {"required": ["script_content"]}],
            "additionalProperties": False
        },
        "annotations": {"readOnlyHint": False, "destructiveHint": True, "idempotentHint": False, "openWorldHint": True}
    },
    {
        "name": "nx_get_journal",
        "description": "Read the last durable Journal state, real stdout/stderr and bounded model and file observations. A recorded RUNNING state does not prove supervisor health; UNKNOWN never auto-recovers.",
        "inputSchema": {"type": "object", "properties": {"operation_id": {"type": "string", "pattern": "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$"}}, "required": ["operation_id"], "additionalProperties": False},
        "annotations": {"readOnlyHint": True, "destructiveHint": False, "idempotentHint": True, "openWorldHint": False}
    },
]


class McpServer:
    def __init__(self, config: BridgeConfig) -> None:
        self._config = config
        self._bridge = JsonlBridge(config)
        self._unknown_operation_ids: set[str] = set()
        self._initialization_started = False
        self._initialized = False

    def close(self) -> None:
        self._bridge.close()

    def handle_message(self, message: Any) -> dict[str, Any] | None:
        if not isinstance(message, dict):
            return self._error(None, -32600, "Invalid Request")

        request_id = message.get("id")
        valid_id = "id" not in message or self._valid_id(request_id)
        if message.get("jsonrpc") != "2.0" or not isinstance(message.get("method"), str):
            return self._error(request_id if valid_id else None, -32600, "Invalid Request")
        if set(message) - {"jsonrpc", "id", "method", "params"}:
            return self._error(request_id if valid_id else None, -32600, "Invalid Request")
        if not valid_id:
            return self._error(None, -32600, "Invalid Request")

        # A notification has no id and never receives a JSON-RPC response.
        if "id" not in message:
            self._handle_notification(message)
            return None

        method = message["method"]
        params = message.get("params", {})
        if not isinstance(params, dict):
            return self._error(request_id, -32602, "Invalid params")

        if method == "ping":
            if params:
                return self._error(request_id, -32602, "Invalid params")
            return self._result(request_id, {})
        if method == "initialize":
            return self._initialize(request_id, params)
        if method.startswith("notifications/"):
            return self._error(request_id, -32600, "Notifications must not include an id")
        if not self._initialized:
            return self._error(request_id, -32002, "Server not initialized")
        if method == "tools/list":
            return self._tools_list(request_id, params)
        if method == "tools/call":
            return self._tools_call(request_id, params)
        return self._error(request_id, -32601, "Method not found")

    @staticmethod
    def _valid_id(value: Any) -> bool:
        if isinstance(value, bool):
            return False
        if isinstance(value, str):
            return True
        if isinstance(value, int):
            return True
        if isinstance(value, float):
            return math.isfinite(value)
        return False

    def _handle_notification(self, message: dict[str, Any]) -> None:
        if message.get("jsonrpc") != "2.0" or not isinstance(message.get("method"), str):
            return
        method = message["method"]
        if method == "notifications/initialized":
            params = message.get("params", {})
            if self._initialization_started and isinstance(params, dict) and not params:
                self._initialized = True
                return
        # Other notifications are intentionally ignored. JSON-RPC notifications have no response.

    def _initialize(self, request_id: Any, params: dict[str, Any]) -> dict[str, Any]:
        if self._initialization_started:
            return self._error(request_id, -32600, "Already initialized")
        protocol_version = params.get("protocolVersion")
        if not isinstance(protocol_version, str) or not protocol_version:
            return self._error(request_id, -32602, "Invalid params")
        if not isinstance(params.get("capabilities"), dict):
            return self._error(request_id, -32602, "Invalid params")
        client_info = params.get("clientInfo")
        if (
            not isinstance(client_info, dict)
            or not isinstance(client_info.get("name"), str)
            or not isinstance(client_info.get("version"), str)
        ):
            return self._error(request_id, -32602, "Invalid params")
        self._initialization_started = True
        return self._result(
            request_id,
            {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {"tools": {}},
                "serverInfo": {"name": SERVER_NAME, "version": __version__},
                "instructions": (
                    "Call nx_ensure_ready to prepare or reuse the NX connection; STARTING means retry "
                    "after NX startup or resolving a license dialog. This does not take ownership. "
                    "Bridge status and diagnostic ping use local bridge data without launching NX. Model reads require "
                    "AI_OWNED session state. Call nx_takeover only after explicit human confirmation "
                    "using authorize=true and an artifact_scope supported by the live NX adapter. "
                    "saved_independent_copy selects a saved standalone part; current_work_part retains "
                    "the active in-memory part and does not support nx_save_copy. Prepare reusable "
                    "datum planes with nx_prepare_datum_plane and native sketches with nx_prepare_sketch. "
                    "Sketch frames can use origin_mm, normal, and x_axis directly, or an explicit "
                    "face_journal_id from nx_query_faces(selector='all_planar'), or a datum plane. "
                    "Legacy principal-plane and selector-based face modes remain supported. Prepare "
                    "extrusions with nx_prepare_extrude, solid or sheet revolves with nx_prepare_revolve, external "
                    "single-start 60-degree solid threads with nx_prepare_thread, and helical grooves "
                    "with nx_prepare_helical_groove, or supported parameter changes with "
                    "nx_prepare_operation; execute only a returned plan with "
                    "nx_execute_plan. Writes require an AI_OWNED session, "
                    "the parameter-edit adapter, and an unhalted write gate. Supported targets are "
                    "DiameterDim on one full Arc, HorizontalDim ArcCenter plus Point X offset, and "
                    "HorizontalDim or VerticalDim between both endpoints of one Line. Use the "
                    "operation_id to query or cancel queued work. After a successful execute, call "
                    "nx_verify_part with its execute_operation_id to read back the live expression, "
                    "geometry, feature history, and association. Then nx_save_copy explicitly saves "
                    "and reopens a new, nonexistent .prt path; it rotates ownership_id and "
                    "binding_epoch, so use the credentials returned by that operation. Inspect "
                    "write_halt_reason and active_operations when writes are frozen or an outcome is unknown."
                ),
            },
        )

    def _tools_list(self, request_id: Any, params: dict[str, Any]) -> dict[str, Any]:
        if set(params) - {"cursor", "_meta"}:
            return self._error(request_id, -32602, "Invalid params")
        if "cursor" in params:
            return self._error(request_id, -32602, "Pagination is not supported")
        return self._result(request_id, {"tools": TOOLS})

    def _tools_call(self, request_id: Any, params: dict[str, Any]) -> dict[str, Any]:
        if set(params) - {"name", "arguments", "_meta"}:
            return self._error(request_id, -32602, "Invalid params")
        name = params.get("name")
        arguments = params.get("arguments", {})
        if not isinstance(name, str) or not isinstance(arguments, dict):
            return self._error(request_id, -32602, "Invalid params")

        try:
            if name == "nx_run_journal":
                try:
                    result = journal.submit(arguments)
                except (ValueError, OSError) as exc:
                    return self._error(request_id, -32602, str(exc))
            elif name == "nx_get_journal":
                if set(arguments) != {"operation_id"}:
                    return self._error(request_id, -32602, "nx_get_journal requires only operation_id")
                try:
                    result = journal.get(arguments["operation_id"])
                except ValueError as exc:
                    return self._error(request_id, -32602, str(exc))
            elif name == "nx_ensure_ready":
                if arguments:
                    return self._error(request_id, -32602, "nx_ensure_ready accepts no arguments")
                result = NxReadiness(self._config, self._bridge).ensure_ready()
                if isinstance(result.get("bridge_status"), dict):
                    result = {**result, "bridge_status": self._with_local_write_halt(result["bridge_status"])}
            elif name == "nx_get_capabilities":
                if arguments:
                    return self._error(request_id, -32602, "nx_get_capabilities accepts no arguments")
                result = self._capabilities()
            elif name == "nx_bridge_status":
                if arguments:
                    return self._error(request_id, -32602, "nx_bridge_status accepts no arguments")
                result = self._with_local_write_halt(self._bridge.call("bridge.status", {}))
            elif name == "nx_bridge_ping":
                if set(arguments) != {"nonce"}:
                    return self._error(request_id, -32602, "nx_bridge_ping requires only nonce")
                nonce = arguments["nonce"]
                if not isinstance(nonce, str) or not (1 <= len(nonce) <= 128):
                    return self._error(request_id, -32602, "nonce must be a string of length 1 through 128")
                try:
                    nonce.encode("utf-8", errors="strict")
                except UnicodeEncodeError:
                    return self._error(request_id, -32602, "nonce must contain valid Unicode text")
                result = self._bridge.call("bridge.dispatch_probe", {"nonce": nonce})
            elif name == "nx_takeover":
                if set(arguments) != {"expected_pid", "expected_part_path", "artifact_scope", "authorize"}:
                    return self._error(request_id, -32602, "nx_takeover requires expected_pid, expected_part_path, artifact_scope, and authorize")
                expected_pid = arguments["expected_pid"]
                expected_part_path = arguments["expected_part_path"]
                artifact_scope = arguments["artifact_scope"]
                authorize = arguments["authorize"]
                if isinstance(expected_pid, bool) or not isinstance(expected_pid, int) or expected_pid <= 0:
                    return self._error(request_id, -32602, "expected_pid must be a positive integer")
                if not isinstance(expected_part_path, str) or len(expected_part_path) > 4096:
                    return self._error(request_id, -32602, "expected_part_path must be a string of at most 4096 characters")
                if artifact_scope not in ("saved_independent_copy", "current_work_part"):
                    return self._error(request_id, -32602, "artifact_scope must be saved_independent_copy or current_work_part")
                if not expected_part_path.strip() and artifact_scope != "current_work_part":
                    return self._error(request_id, -32602, "expected_part_path may be empty only for current_work_part")
                if authorize is not True:
                    return self._error(request_id, -32602, "authorize must be true after explicit human confirmation")
                result = self._bridge.call("ownership.takeover", {
                    "expected_pid": expected_pid,
                    "expected_part_path": expected_part_path,
                    "artifact_scope": artifact_scope,
                    "authorize": authorize,
                })
            elif name == "nx_return":
                if set(arguments) != {"ownership_id", "binding_epoch"}:
                    return self._error(request_id, -32602, "nx_return requires ownership_id and binding_epoch")
                ownership_id = arguments["ownership_id"]
                binding_epoch = arguments["binding_epoch"]
                if not isinstance(ownership_id, str) or not ownership_id.strip():
                    return self._error(request_id, -32602, "ownership_id must be a non-empty string")
                if isinstance(binding_epoch, bool) or not isinstance(binding_epoch, int) or binding_epoch < 1:
                    return self._error(request_id, -32602, "binding_epoch must be a positive integer")
                result = self._bridge.call("ownership.return", {
                    "ownership_id": ownership_id,
                    "binding_epoch": binding_epoch,
                })
            elif name == "nx_get_session":
                if set(arguments) != {"ownership_id", "binding_epoch"}:
                    return self._error(request_id, -32602, "nx_get_session requires ownership_id and binding_epoch")
                ownership_id = arguments["ownership_id"]
                binding_epoch = arguments["binding_epoch"]
                if not isinstance(ownership_id, str) or not ownership_id.strip():
                    return self._error(request_id, -32602, "ownership_id must be a non-empty string")
                if isinstance(binding_epoch, bool) or not isinstance(binding_epoch, int) or binding_epoch < 1:
                    return self._error(request_id, -32602, "binding_epoch must be a positive integer")
                result = self._bridge.call("model.get_session", {
                    "ownership_id": ownership_id,
                    "binding_epoch": binding_epoch,
                })
            elif name == "nx_inspect_part":
                if set(arguments) != {"ownership_id", "binding_epoch"}:
                    return self._error(request_id, -32602, "nx_inspect_part requires ownership_id and binding_epoch")
                ownership_id = arguments["ownership_id"]
                binding_epoch = arguments["binding_epoch"]
                if not isinstance(ownership_id, str) or not ownership_id.strip():
                    return self._error(request_id, -32602, "ownership_id must be a non-empty string")
                if isinstance(binding_epoch, bool) or not isinstance(binding_epoch, int) or binding_epoch < 1:
                    return self._error(request_id, -32602, "binding_epoch must be a positive integer")
                result = self._bridge.call("model.inspect_part", {
                    "ownership_id": ownership_id,
                    "binding_epoch": binding_epoch,
                })
            elif name == "nx_query_faces":
                expected = {"ownership_id", "binding_epoch", "revision_id", "selector"}
                if set(arguments) not in (expected, expected | {"body_id"}):
                    return self._error(request_id, -32602, "nx_query_faces requires ownership_id, binding_epoch, revision_id, selector, and optional body_id")
                request = {
                    "ownership_id": self._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
                    "binding_epoch": self._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
                    "revision_id": self._validated_nonempty_string(arguments["revision_id"], "revision_id"),
                    "selector": self._validated_nonempty_string(arguments["selector"], "selector"),
                }
                if "body_id" in arguments:
                    request["body_id"] = self._validated_nonempty_string(arguments["body_id"], "body_id")
                result = self._bridge.call("model.query_faces", request)
            elif name == "nx_inspect_sketch":
                if set(arguments) != {"ownership_id", "binding_epoch", "sketch_id"}:
                    return self._error(request_id, -32602, "nx_inspect_sketch requires ownership_id, binding_epoch, and sketch_id")
                ownership_id = arguments["ownership_id"]
                binding_epoch = arguments["binding_epoch"]
                sketch_id = arguments["sketch_id"]
                if not isinstance(ownership_id, str) or not ownership_id.strip():
                    return self._error(request_id, -32602, "ownership_id must be a non-empty string")
                if isinstance(binding_epoch, bool) or not isinstance(binding_epoch, int) or binding_epoch < 1:
                    return self._error(request_id, -32602, "binding_epoch must be a positive integer")
                if not isinstance(sketch_id, str) or not sketch_id.strip():
                    return self._error(request_id, -32602, "sketch_id must be a non-empty string")
                result = self._bridge.call("model.inspect_sketch", {
                    "ownership_id": ownership_id,
                    "binding_epoch": binding_epoch,
                    "sketch_id": sketch_id,
                })
            elif name == "nx_get_operation":
                if set(arguments) != {"operation_id"}:
                    return self._error(request_id, -32602, "nx_get_operation requires only operation_id")
                operation_id = self._validated_operation_id(arguments["operation_id"])
                try:
                    result = self._bridge.call("operation.get", {"operation_id": operation_id})
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(operation_id)
                    raise
                operation_state = result.get("state") if isinstance(result, dict) else None
                if operation_state == "UNKNOWN":
                    self._unknown_operation_ids.add(operation_id)
                operation_kind = result.get("kind") if isinstance(result, dict) else None
                operation_result = result.get("result") if isinstance(result, dict) else None
                combined_edit = isinstance(operation_result, dict) and operation_result.get("combined_edit") is True
                is_sketch_corner_operation = (
                    operation_kind == "edit_sketch_corner"
                    or operation_kind == "execute_edit_sketch_corner" and (
                        operation_state in {"QUEUED", "RUNNING", "FAILED", "UNKNOWN"} or combined_edit
                    )
                )
                is_sketch_offset_operation = (
                    operation_kind == "edit_sketch_offset"
                    or operation_kind == "execute_edit_sketch_offset" and (
                        operation_state in {"QUEUED", "RUNNING", "FAILED", "UNKNOWN"} or combined_edit
                    )
                )
                is_sketch_pattern_operation = (
                    operation_kind == "edit_sketch_pattern"
                    or operation_kind == "execute_edit_sketch_pattern" and (
                        operation_state in {"QUEUED", "RUNNING", "FAILED", "UNKNOWN"} or combined_edit
                    )
                )
                is_sketch_projection_operation = (
                    operation_kind == "edit_sketch_projection"
                    or operation_kind == "execute_edit_sketch_projection" and (
                        operation_state in {"QUEUED", "RUNNING", "FAILED", "UNKNOWN"} or combined_edit
                    )
                )
                is_sketch_reference_conversion_operation = (
                    operation_kind == "edit_sketch_reference_conversion"
                    or operation_kind == "execute_edit_sketch_reference_conversion" and (
                        operation_state in {"QUEUED", "RUNNING", "FAILED", "UNKNOWN"} or combined_edit
                    )
                )
                if (
                    isinstance(result, dict)
                    and (
                        is_sketch_corner_operation or is_sketch_offset_operation
                        or is_sketch_pattern_operation or is_sketch_projection_operation
                        or is_sketch_reference_conversion_operation
                    )
                ):
                    status_reader = (
                        self._sketch_reference_conversion_operation_status if is_sketch_reference_conversion_operation
                        else self._sketch_projection_operation_status if is_sketch_projection_operation
                        else self._sketch_pattern_operation_status if is_sketch_pattern_operation
                        else self._sketch_offset_operation_status if is_sketch_offset_operation
                        else self._sketch_corner_operation_status
                    )
                    operation_status = status_reader(operation_id, result)
                    if operation_status["structuredContent"].get("outcome_unknown") is True:
                        self._unknown_operation_ids.add(operation_id)
                    return self._result(request_id, operation_status)
            elif name == "nx_cancel_operation":
                if set(arguments) != {"operation_id"}:
                    return self._error(request_id, -32602, "nx_cancel_operation requires only operation_id")
                operation_id = self._validated_operation_id(arguments["operation_id"])
                result = self._bridge.call("operation.cancel", {"operation_id": operation_id})
            elif name == "nx_prepare_sketch":
                request = self._validate_prepare_sketch(arguments)
                result = self._bridge.call("operation.prepare_sketch", request)
            elif name == "nx_prepare_sketch_corner":
                request = self._validate_prepare_sketch_corner(arguments)
                result = self._bridge.call("operation.prepare_sketch_corner", request)
            elif name == "nx_edit_sketch_corner":
                request = self._validate_prepare_sketch_corner(arguments)
                if self._unknown_operation_ids:
                    return self._result(
                        request_id,
                        self._tool_error(
                            "CLIENT_OUTCOME_UNKNOWN",
                            "A previous write outcome is unknown; query its operation_id before submitting another write.",
                        ),
                    )
                try:
                    result = self._bridge.call("operation.edit_sketch_corner", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                        return self._result(
                            request_id,
                            self._sketch_corner_operation_status(
                                request["operation_id"], None, bridge_failure=exc,
                            ),
                        )
                    raise
                operation_status = self._sketch_corner_operation_status(request["operation_id"], result)
                if operation_status["structuredContent"].get("outcome_unknown") is True:
                    self._unknown_operation_ids.add(request["operation_id"])
                return self._result(request_id, operation_status)
            elif name == "nx_prepare_sketch_offset":
                request = self._validate_prepare_sketch_offset(arguments)
                result = self._bridge.call("operation.prepare_sketch_offset", request)
            elif name == "nx_edit_sketch_offset":
                request = self._validate_prepare_sketch_offset(arguments)
                if self._unknown_operation_ids:
                    return self._result(
                        request_id,
                        self._tool_error(
                            "CLIENT_OUTCOME_UNKNOWN",
                            "A previous write outcome is unknown; query its operation_id before submitting another write.",
                        ),
                    )
                try:
                    result = self._bridge.call("operation.edit_sketch_offset", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                        return self._result(
                            request_id,
                            self._sketch_offset_operation_status(
                                request["operation_id"], None, bridge_failure=exc,
                            ),
                        )
                    raise
                operation_status = self._sketch_offset_operation_status(request["operation_id"], result)
                if operation_status["structuredContent"].get("outcome_unknown") is True:
                    self._unknown_operation_ids.add(request["operation_id"])
                return self._result(request_id, operation_status)
            elif name == "nx_prepare_sketch_pattern":
                request = self._validate_prepare_sketch_pattern(arguments)
                result = self._bridge.call("operation.prepare_sketch_pattern", request)
            elif name == "nx_edit_sketch_pattern":
                request = self._validate_prepare_sketch_pattern(arguments)
                if self._unknown_operation_ids:
                    return self._result(
                        request_id,
                        self._tool_error(
                            "CLIENT_OUTCOME_UNKNOWN",
                            "A previous write outcome is unknown; query its operation_id before submitting another write.",
                        ),
                    )
                try:
                    result = self._bridge.call("operation.edit_sketch_pattern", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                        return self._result(
                            request_id,
                            self._sketch_pattern_operation_status(
                                request["operation_id"], None, bridge_failure=exc,
                            ),
                        )
                    raise
                operation_status = self._sketch_pattern_operation_status(request["operation_id"], result)
                if operation_status["structuredContent"].get("outcome_unknown") is True:
                    self._unknown_operation_ids.add(request["operation_id"])
                return self._result(request_id, operation_status)
            elif name == "nx_prepare_sketch_projection":
                request = self._validate_prepare_sketch_projection(arguments)
                result = self._bridge.call("operation.prepare_sketch_projection", request)
            elif name == "nx_edit_sketch_projection":
                request = self._validate_prepare_sketch_projection(arguments)
                if self._unknown_operation_ids:
                    return self._result(
                        request_id,
                        self._tool_error(
                            "CLIENT_OUTCOME_UNKNOWN",
                            "A previous write outcome is unknown; query its operation_id before submitting another write.",
                        ),
                    )
                try:
                    result = self._bridge.call("operation.edit_sketch_projection", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                        return self._result(
                            request_id,
                            self._sketch_projection_operation_status(
                                request["operation_id"], None, bridge_failure=exc,
                            ),
                        )
                    raise
                operation_status = self._sketch_projection_operation_status(request["operation_id"], result)
                if operation_status["structuredContent"].get("outcome_unknown") is True:
                    self._unknown_operation_ids.add(request["operation_id"])
                return self._result(request_id, operation_status)
            elif name == "nx_prepare_sketch_reference_conversion":
                request = self._validate_prepare_sketch_reference_conversion(arguments)
                result = self._bridge.call("operation.prepare_sketch_reference_conversion", request)
            elif name == "nx_edit_sketch_reference_conversion":
                request = self._validate_prepare_sketch_reference_conversion(arguments)
                if self._unknown_operation_ids:
                    return self._result(
                        request_id,
                        self._tool_error(
                            "CLIENT_OUTCOME_UNKNOWN",
                            "A previous write outcome is unknown; query its operation_id before submitting another write.",
                        ),
                    )
                try:
                    result = self._bridge.call("operation.edit_sketch_reference_conversion", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                        return self._result(
                            request_id,
                            self._sketch_reference_conversion_operation_status(
                                request["operation_id"], None, bridge_failure=exc,
                            ),
                        )
                    raise
                operation_status = self._sketch_reference_conversion_operation_status(request["operation_id"], result)
                if operation_status["structuredContent"].get("outcome_unknown") is True:
                    self._unknown_operation_ids.add(request["operation_id"])
                return self._result(request_id, operation_status)
            elif name == "nx_prepare_datum_plane":
                request = self._validate_prepare_datum_plane(arguments)
                result = self._bridge.call("operation.prepare_datum_plane", request)
            elif name == "nx_prepare_extrude":
                request = self._validate_prepare_extrude(arguments)
                result = self._bridge.call("operation.prepare_extrude", request)
            elif name == "nx_prepare_sweep":
                request = self._validate_prepare_sweep(arguments)
                if self._unknown_operation_ids:
                    return self._result(request_id, self._tool_error(
                        "CLIENT_OUTCOME_UNKNOWN",
                        "A previous operation outcome is unknown; query its operation_id before submitting another write.",
                    ))
                try:
                    result = self._bridge.call("operation.prepare_sweep", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                    raise
                if result.get("state") == "UNKNOWN":
                    self._unknown_operation_ids.add(request["operation_id"])
            elif name == "nx_prepare_revolve":
                request = self._validate_prepare_revolve(arguments)
                result = self._bridge.call("operation.prepare_revolve", request)
            elif name == "nx_prepare_boolean":
                request = self._validate_prepare_boolean(arguments)
                result = self._bridge.call("operation.prepare_boolean", request)
            elif name == "nx_prepare_spatial_curve":
                request = self._validate_prepare_spatial_curve(arguments)
                if self._unknown_operation_ids:
                    return self._result(request_id, self._tool_error("CLIENT_OUTCOME_UNKNOWN", "A previous operation outcome is unknown; query its operation_id before submitting another write."))
                try:
                    result = self._bridge.call("operation.prepare_spatial_curve", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                    raise
                if result.get("state") == "UNKNOWN":
                    self._unknown_operation_ids.add(request["operation_id"])
            elif name == "nx_prepare_feature_mirror":
                request = self._validate_prepare_feature_mirror(arguments)
                if self._unknown_operation_ids:
                    return self._result(request_id, self._tool_error(
                        "CLIENT_OUTCOME_UNKNOWN",
                        "A previous operation outcome is unknown; query its operation_id before submitting another write.",
                    ))
                try:
                    result = self._bridge.call("operation.prepare_feature_mirror", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                    raise
                if result.get("state") == "UNKNOWN":
                    self._unknown_operation_ids.add(request["operation_id"])
            elif name == "nx_prepare_feature_pattern":
                request = self._validate_prepare_feature_pattern(arguments)
                if self._unknown_operation_ids:
                    return self._result(request_id, self._tool_error(
                        "CLIENT_OUTCOME_UNKNOWN",
                        "A previous operation outcome is unknown; query its operation_id before submitting another write.",
                    ))
                try:
                    result = self._bridge.call("operation.prepare_feature_pattern", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                    raise
                if result.get("state") == "UNKNOWN":
                    self._unknown_operation_ids.add(request["operation_id"])
            elif name == "nx_prepare_chamfer":
                request = self._validate_prepare_chamfer(arguments)
                if self._unknown_operation_ids:
                    return self._result(request_id, self._tool_error(
                        "CLIENT_OUTCOME_UNKNOWN",
                        "A previous operation outcome is unknown; query its operation_id before submitting another write.",
                    ))
                try:
                    result = self._bridge.call("operation.prepare_chamfer", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                    raise
                if result.get("state") == "UNKNOWN":
                    self._unknown_operation_ids.add(request["operation_id"])
            elif name == "nx_prepare_edge_blend":
                request = self._validate_prepare_edge_blend(arguments)
                if self._unknown_operation_ids:
                    return self._result(request_id, self._tool_error(
                        "CLIENT_OUTCOME_UNKNOWN",
                        "A previous operation outcome is unknown; query its operation_id before submitting another write.",
                    ))
                try:
                    result = self._bridge.call("operation.prepare_edge_blend", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                    raise
                if result.get("state") == "UNKNOWN":
                    self._unknown_operation_ids.add(request["operation_id"])
            elif name == "nx_prepare_hole":
                request = self._validate_prepare_hole(arguments)
                result = self._bridge.call("operation.prepare_hole", request)
            elif name == "nx_prepare_thread":
                request = self._validate_prepare_thread(arguments)
                result = self._bridge.call("operation.prepare_thread", request)
            elif name == "nx_prepare_helical_groove":
                request = self._validate_prepare_helical_groove(arguments)
                result = self._bridge.call("operation.prepare_helical_groove", request)
            elif name == "nx_prepare_operation":
                expected = {
                    "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                    "constraint_id", "value", "unit",
                }
                if set(arguments) != expected:
                    return self._error(request_id, -32602, "nx_prepare_operation requires exactly operation_id, ownership_id, binding_epoch, revision_id, sketch_id, constraint_id, value, and unit")
                request = self._validate_operation_prepare(arguments)
                result = self._bridge.call("operation.prepare", request)
            elif name == "nx_execute_plan":
                expected = {"operation_id", "ownership_id", "binding_epoch", "plan_id"}
                if set(arguments) != expected:
                    return self._error(request_id, -32602, "nx_execute_plan requires exactly operation_id, ownership_id, binding_epoch, and plan_id")
                request = self._validate_operation_execute(arguments)
                if self._unknown_operation_ids:
                    return self._result(
                        request_id,
                        self._tool_error(
                            "CLIENT_OUTCOME_UNKNOWN",
                            "A previous execute_plan outcome is unknown; query its operation_id before submitting more writes.",
                        ),
                    )
                try:
                    result = self._bridge.call("operation.execute_plan", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                    raise
            elif name == "nx_verify_part":
                expected = {"operation_id", "ownership_id", "binding_epoch", "execute_operation_id"}
                if set(arguments) != expected:
                    return self._error(request_id, -32602, "nx_verify_part requires exactly operation_id, ownership_id, binding_epoch, and execute_operation_id")
                request = self._validate_operation_verify(arguments)
                try:
                    result = self._bridge.call("operation.verify_part", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                    raise
                if result.get("state") == "UNKNOWN":
                    self._unknown_operation_ids.add(request["operation_id"])
            elif name == "nx_save_copy":
                expected = {"operation_id", "ownership_id", "binding_epoch", "target_path", "reopen"}
                if set(arguments) != expected:
                    return self._error(request_id, -32602, "nx_save_copy requires exactly operation_id, ownership_id, binding_epoch, target_path, and reopen")
                request = self._validate_save_copy(arguments)
                if self._unknown_operation_ids:
                    return self._result(
                        request_id,
                        self._tool_error(
                            "CLIENT_OUTCOME_UNKNOWN",
                            "A previous write or save outcome is unknown; query its operation_id before submitting another write.",
                        ),
                    )
                try:
                    result = self._bridge.call("operation.save_copy", request)
                except BridgeFailure as exc:
                    if exc.outcome_unknown:
                        self._unknown_operation_ids.add(request["operation_id"])
                    raise
                if result.get("state") == "UNKNOWN":
                    self._unknown_operation_ids.add(request["operation_id"])
            else:
                return self._error(request_id, -32602, f"Unknown tool: {name}")
        except _InvalidToolArguments as exc:
            return self._error(request_id, -32602, str(exc))
        except BridgeFailure as exc:
            if name == "nx_save_copy" and exc.outcome_unknown:
                operation_id = arguments.get("operation_id") if isinstance(arguments, dict) else None
                if isinstance(operation_id, str) and 1 <= len(operation_id) <= 128:
                    self._unknown_operation_ids.add(operation_id)
            return self._result(request_id, self._tool_error(exc.code, exc.message))
        except Exception as exc:  # Keep protocol output safe; diagnostic details go only to stderr.
            print(f"bridge call failed ({type(exc).__name__})", file=sys.stderr, flush=True)
            return self._result(
                request_id,
                self._tool_error("BRIDGE_INTERNAL_ERROR", "The local bridge request failed."),
            )
        return self._result(request_id, self._tool_success(result))

    def _capabilities(self) -> dict[str, Any]:
        state = "UNKNOWN"
        read_enabled = False
        status: dict[str, Any] = {}
        unavailable_reason: dict[str, str] | None = None
        if not self._config.configured:
            unavailable_reason = {
                "code": "BRIDGE_UNCONFIGURED",
                "message": "No NX bridge session is configured.",
            }
        else:
            try:
                cached_status = self._with_local_write_halt(self._bridge.call("bridge.status", {}))
                status = cached_status
                state_value = cached_status.get("ownership_state")
                if isinstance(state_value, str):
                    state = state_value
                read_enabled = cached_status.get("model_read_available") is True
                if not read_enabled:
                    code = "OWNERSHIP_REQUIRED" if state == "HUMAN_OWNED" else "MODEL_READ_UNAVAILABLE"
                    message = (
                        "Model reads require an explicit takeover of the saved independent copy."
                        if state == "HUMAN_OWNED"
                        else f"Model reads are unavailable while ownership state is {state}."
                    )
                    unavailable_reason = {"code": code, "message": message}
            except BridgeFailure as exc:
                unavailable_reason = {"code": exc.code, "message": exc.message[:1000]}
        status = self._with_local_write_halt(status)
        binding_identity = status.get("binding_identity")
        if not isinstance(binding_identity, dict):
            binding_identity = {}
        artifact_scope = binding_identity.get("artifact_scope")
        write_enabled = (
            status.get("model_write_enabled") is True
            and state == "AI_OWNED"
            and status.get("writes_halted") is False
        )
        helical_adapter_available = status.get("model_helical_adapter_available") is True
        helical_write_enabled = (
            status.get("model_helical_write_enabled") is True
            and helical_adapter_available
            and state == "AI_OWNED"
            and status.get("writes_halted") is False
            and not self._unknown_operation_ids
        )
        datum_plane_adapter_available = status.get("model_datum_plane_adapter_available") is True
        datum_plane_write_enabled = (
            datum_plane_adapter_available
            and write_enabled
            and not self._unknown_operation_ids
        )
        sketch_offset_adapter_available = status.get("model_sketch_offset_adapter_available") is True
        sketch_offset_write_enabled = (
            sketch_offset_adapter_available
            and write_enabled
            and not self._unknown_operation_ids
        )
        sketch_pattern_adapter_available = status.get("model_sketch_pattern_adapter_available") is True
        sketch_pattern_write_enabled = (
            sketch_pattern_adapter_available
            and write_enabled
            and not self._unknown_operation_ids
        )
        sketch_projection_adapter_available = status.get("model_sketch_projection_adapter_available") is True
        sketch_projection_write_enabled = (
            sketch_projection_adapter_available
            and write_enabled
            and not self._unknown_operation_ids
        )
        sketch_reference_conversion_adapter_available = status.get("model_sketch_reference_conversion_adapter_available") is True
        sketch_reference_conversion_write_enabled = (
            sketch_reference_conversion_adapter_available
            and write_enabled
            and not self._unknown_operation_ids
        )
        sweep_adapter_available = status.get("model_sweep_adapter_available") is True
        sweep_write_enabled = (status.get("model_sweep_write_enabled") is True and sweep_adapter_available and write_enabled and not self._unknown_operation_ids)
        revolve_adapter_available = status.get("model_revolve_adapter_available") is True
        revolve_write_enabled = (
            revolve_adapter_available
            and write_enabled
            and not self._unknown_operation_ids
        )
        boolean_adapter_available = status.get("model_boolean_adapter_available") is True
        boolean_write_enabled = (status.get("model_boolean_write_enabled") is True and boolean_adapter_available and write_enabled and not self._unknown_operation_ids)
        feature_pattern_adapter_available = status.get("model_feature_pattern_adapter_available") is True
        feature_pattern_write_enabled = (status.get("model_feature_pattern_write_enabled") is True and
                                         feature_pattern_adapter_available and write_enabled and not self._unknown_operation_ids)
        feature_mirror_adapter_available = status.get("model_feature_mirror_adapter_available") is True
        feature_mirror_write_enabled = (status.get("model_feature_mirror_write_enabled") is True and
                                        feature_mirror_adapter_available and write_enabled and not self._unknown_operation_ids)
        spatial_curve_adapter_available = status.get("model_spatial_curve_adapter_available") is True
        spatial_curve_write_enabled = (status.get("model_spatial_curve_write_enabled") is True and
                                       spatial_curve_adapter_available and write_enabled and not self._unknown_operation_ids)
        chamfer_adapter_available = status.get("model_chamfer_adapter_available") is True
        chamfer_write_enabled = (status.get("model_chamfer_write_enabled") is True and
                                  chamfer_adapter_available and write_enabled and not self._unknown_operation_ids)
        edge_blend_adapter_available = status.get("model_edge_blend_adapter_available") is True
        edge_blend_write_enabled = (status.get("model_edge_blend_write_enabled") is True and
                                    edge_blend_adapter_available and write_enabled and not self._unknown_operation_ids)
        hole_adapter_available = status.get("model_hole_adapter_available") is True
        hole_write_enabled = (
            status.get("model_hole_write_enabled") is True
            and hole_adapter_available and write_enabled and not self._unknown_operation_ids
        )
        capabilities = {
            "protocol_version": PROTOCOL_VERSION,
            "bridge_configured": self._config.configured,
            "connection_preparation": {
                "tool": "nx_ensure_ready",
                "automatic_launch_configured": self._config.launch is not None,
                "startup_probe_budget_seconds": self._config.launch.ready_timeout_seconds if self._config.launch else 2.0,
                "bounded_helper_cleanup_additional": True,
                "changes_ownership": False,
                "diagnostics_launch_nx": False,
            },
            "available_operations": [
                "bridge.status", "bridge.dispatch_probe", "ownership.takeover", "ownership.return",
                "model.get_session", "model.inspect_part", "model.inspect_sketch", "model.query_faces",
                "operation.get", "operation.cancel", "operation.prepare", "operation.prepare_sketch",
                "operation.prepare_sketch_corner", "operation.edit_sketch_corner",
                "operation.prepare_sketch_offset", "operation.edit_sketch_offset",
                "operation.prepare_sketch_pattern", "operation.edit_sketch_pattern",
                "operation.prepare_sketch_projection", "operation.edit_sketch_projection",
                "operation.prepare_sketch_reference_conversion", "operation.edit_sketch_reference_conversion",
                "operation.prepare_datum_plane",
                "operation.prepare_extrude", "operation.prepare_revolve", "operation.prepare_thread", "operation.prepare_helical_groove",
                "operation.execute_plan",
                "operation.verify_part", "operation.save_copy",
            ],
            "standalone_journal": {
                "implemented_languages": ["python"],
                "execution": "independent NX batch; existing NX/helper blocks dispatch",
                "tools": ["nx_run_journal", "nx_get_journal"],
                "availability": "determined at dispatch by shared gate, persistent marker, processes, and local NX runner",
                "model_observation": "bounded open-part and Curves/Bodies/Features inventory at two instants",
                "unknown": "never replayed or automatically cleared"
            },
            "ownership_state": state,
            "model_access": {"read": read_enabled, "write": write_enabled, "control": False},
            "model_read_implemented": True,
            "model_read_enabled": read_enabled,
            "model_read_available": read_enabled,
            "model_helical_adapter_available": helical_adapter_available,
            "model_helical_write_enabled": helical_write_enabled,
            "model_datum_plane_adapter_available": datum_plane_adapter_available,
            "model_datum_plane_write_enabled": datum_plane_write_enabled,
            "model_sketch_offset_adapter_available": sketch_offset_adapter_available,
            "model_sketch_offset_write_enabled": sketch_offset_write_enabled,
            "model_sketch_pattern_adapter_available": sketch_pattern_adapter_available,
            "model_sketch_pattern_write_enabled": sketch_pattern_write_enabled,
            "model_sketch_projection_adapter_available": sketch_projection_adapter_available,
            "model_sketch_projection_write_enabled": sketch_projection_write_enabled,
            "model_sketch_reference_conversion_adapter_available": sketch_reference_conversion_adapter_available,
            "model_sketch_reference_conversion_write_enabled": sketch_reference_conversion_write_enabled,
            "model_sweep_adapter_available": sweep_adapter_available,
            "model_sweep_write_enabled": sweep_write_enabled,
            "model_revolve_adapter_available": revolve_adapter_available,
            "model_revolve_write_enabled": revolve_write_enabled,
            "model_boolean_adapter_available": boolean_adapter_available,
            "model_boolean_write_enabled": boolean_write_enabled,
            "model_feature_pattern_adapter_available": feature_pattern_adapter_available,
            "model_feature_pattern_write_enabled": feature_pattern_write_enabled,
            "model_feature_mirror_adapter_available": feature_mirror_adapter_available,
            "model_feature_mirror_write_enabled": feature_mirror_write_enabled,
            "model_spatial_curve_adapter_available": spatial_curve_adapter_available,
            "model_spatial_curve_write_enabled": spatial_curve_write_enabled,
            "model_chamfer_adapter_available": chamfer_adapter_available,
            "model_chamfer_write_enabled": chamfer_write_enabled,
            "model_edge_blend_adapter_available": edge_blend_adapter_available,
            "model_edge_blend_write_enabled": edge_blend_write_enabled,
            "model_hole_adapter_available": hole_adapter_available,
            "model_hole_write_enabled": hole_write_enabled,
            "model_read_requires": ["AI_OWNED", "ownership_id", "binding_epoch"],
            "artifact_scope": artifact_scope,
            "default_artifact_scope": "saved_independent_copy",
            "supported_artifact_scopes": ["saved_independent_copy", "current_work_part"],
            "revision_schema": "m2-semantic-v1",
            "revision_source": "monotonic revision_id with canonical semantic snapshot comparison; not a digest",
            "read_support": {
                "session": ["NX_FULL_VERSION", "Session epoch", "load instance", "active Work Part identity"],
                "part": ["units", "part inventory", "body/face/edge identities", "feature graph", "sketches", "expressions", "dependencies"],
                "sketch_geometry_detail": ["LINE", "ARC", "ELLIPSE", "ELLIPTICAL_ARC", "SPLINE"],
                "sketch_geometry_reference_fields": ["reference_status", "reference_status_value", "reference_curve_solver_semantics"],
                "sketch_dimension_fields": ["dimension_state", "solver_participation", "computed_size", "expression_value", "expression_flags"],
                "arc_geometry_fields": ["center", "radius_mm", "start_angle_rad", "end_angle_rad", "start", "end", "normal"],
                "ellipse_geometry_fields": ["center", "major_radius_mm", "minor_radius_mm", "major_axis_direction", "start_angle_rad", "end_angle_rad", "start", "end", "normal"],
                "spline_geometry_fields": ["degree", "periodic", "interpolation_point_count", "interpolation_points", "interpolation_parameters", "start", "end"],
                "spline_nurbs_definition_fields": ["degree", "order", "pole_count", "rational", "knots", "poles", "weights", "start_parameter", "end_parameter"],
                "feature_parameters": "metadata and created expressions; type-specific parameters are reported unsupported unless implemented",
            },
            "model_native_geometry_support": {
                "prepare_operations": [
                    "operation.prepare_datum_plane", "operation.prepare_sketch", "operation.prepare_extrude", "operation.prepare_revolve",
                    "operation.prepare_sketch_corner", "operation.prepare_sketch_offset", "operation.prepare_sketch_pattern",
                    "operation.prepare_sketch_projection", "operation.prepare_sketch_reference_conversion",
                    "operation.prepare_thread", "operation.prepare_helical_groove",
                ],
                "sweep": {
                    "prepare_operation": "operation.prepare_sweep",
                    "adapter_available": sweep_adapter_available,
                    "write_enabled": sweep_write_enabled,
                    "orientation_modes": {
                        "transport": "zero twist; native Fixed orientation",
                        "linear_twist": "native ByAngularLaw Linear; finite stations are checked against a right-hand angle proportional to normalized arc length and rejected when outside tolerance; full-path progression is not proven",
                    },
                    "twist_angle_deg": "explicit finite angle in [-90, 90]; transport requires zero",
                },
                "boolean": {
                    "prepare_operation": "operation.prepare_boolean",
                    "adapter_available": boolean_adapter_available,
                    "write_enabled": boolean_write_enabled,
                    "operations": ["unite", "subtract", "intersect"],
                    "intersect_semantics": "target intersected with the union of all tools",
                    "tool_count": "1 to 32 distinct solid bodies; explicit retain_tools boolean",
                    "unchanged_or_empty_result": "rejected and rolled back",
                },
                "feature_mirror": {
                    "prepare_operation": "operation.prepare_feature_mirror",
                    "adapter_available": feature_mirror_adapter_available,
                    "write_enabled": feature_mirror_write_enabled,
                    "plane_inputs": ["plane_origin_mm and plane_normal", "datum_plane_id"],
                },
                "spatial_curve": {
                    "prepare_operation": "operation.prepare_spatial_curve",
                    "adapter_available": spatial_curve_adapter_available,
                    "write_enabled": spatial_curve_write_enabled,
                    "types": ["line", "arc", "circle"],
                    "frame": "world XYZ; mm; arc angles in degrees; right-handed normal/x_axis frame",
                },
                "feature_pattern": {
                    "prepare_operation": "operation.prepare_feature_pattern",
                    "adapter_available": feature_pattern_adapter_available,
                    "write_enabled": feature_pattern_write_enabled,
                    "modes": ["linear", "rectangular", "circular"],
                    "instance_count": "each count 2 to 32; rectangular product at most 32",
                    "directions": "unit vectors within 1e-9; rectangular directions orthogonal within 1e-9",
                    "circular_angle": "signed nonzero step; absolute step times count1 minus one strictly less than 360 degrees",
                },
                "chamfer": {
                    "prepare_operation": "operation.prepare_chamfer",
                    "adapter_available": chamfer_adapter_available,
                    "write_enabled": chamfer_write_enabled,
                    "edge_count": "one explicit straight edge",
                    "geometry_scope": "intact unmodified world-axis-aligned rectangular solid box",
                    "reference_axis": "first inward offset along the specified transverse world axis",
                    "angle": "atan2(second inward offset, first inward offset), degrees",
                },
                "edge_blend": {
                    "prepare_operation": "operation.prepare_edge_blend",
                    "adapter_available": edge_blend_adapter_available,
                    "write_enabled": edge_blend_write_enabled,
                    "edge_count": "1 to 32 distinct edge IDs",
                    "radius": "positive finite number in mm",
                    "geometry_scope": "intact unmodified world-axis-aligned rectangular block; nonadjacent parallel straight edges (at most four); radius less than half of each transverse dimension",
                },
                "hole": {
                    "prepare_operation": "operation.prepare_hole",
                    "adapter_available": hole_adapter_available,
                    "write_enabled": hole_write_enabled,
                    "entry_scope": "one planar face interior with an inward normal world direction; 0.01 mm containment witness",
                    "coverage_scope": "operations must prove complete native hole wall coverage; intersections or other unproved geometry are rejected",
                    "thread_geometry": "thread metadata only; no physical thread geometry",
                },
                "sketch_corner": {
                    "prepare_operation": "operation.prepare_sketch_corner",
                    "edit_operation": "operation.edit_sketch_corner",
                    "selection": "curve IDs from nx_inspect_sketch with an explicit start or end for each curve",
                    "fillet_curve_types": ["LINE", "ARC"],
                    "fillet_curve_pairs": [["LINE", "LINE"], ["LINE", "ARC"], ["ARC", "ARC"]],
                    "fillet_radius_unit": "mm",
                    "chamfer_curve_types": ["LINE"],
                    "chamfer_curve_pairs": [["LINE", "LINE"]],
                    "closed_curves": "unsupported because they do not have one unique selected start/end",
                    "mirror_constrained_fillet_targets": "rejected because native Sketch.Fillet propagates to mirror curves",
                    "distance_angle_definition": {
                        "distance1_unit": "mm",
                        "distance2_unit": "mm",
                        "angle_unit": "deg",
                        "vertex_angle": "angle(A→C, A→B)",
                        "native_mapping": "distance2_mm = distance1_mm * sin(angle_deg) / sin(interior_angle + angle_deg); native chamfer mode Asymmetric; no native angle dimension is stored",
                    },
                    "execution": {
                        "prepare": "preview plan; use nx_execute_plan and nx_verify_part",
                        "edit": "prepare, execute, and verify under one operation_id",
                        "success_requires": "state=SUCCEEDED and verified=true",
                        "unknown_outcome": "query with nx_get_operation; do not replay; writes remain halted until resolved",
                    },
                    "whole_profile_validity": "NOT_CLAIMED; F07 verifies only local corner geometry and its sketch relationships",
                },
                "sketch_offset": {
                    "availability_requires": [
                        "model_sketch_offset_adapter_available", "model_sketch_offset_write_enabled",
                    ],
                    "prepare_operation": "operation.prepare_sketch_offset",
                    "edit_operation": "operation.edit_sketch_offset",
                    "selection": "an explicitly ordered curve_chain of IDs in one Sketch; each entry specifies forward or reverse traversal",
                    "supported_curve_types": ["LINE", "ARC"],
                    "distance_unit": "mm; positive finite only",
                    "side": "left or right relative to sketch-plane normal crossed with the requested traversal tangent",
                    "retain_original": "must be true; source curves are not deleted or converted to reference geometry",
                    "join_modes": {
                        "extension": "native SketchOffset.CapType.Extension",
                        "arc": "native SketchOffset.CapType.Arc; degenerate native connector arcs are rejected",
                    },
                    "native_builder": "SketchOffsetBuilder; ConvertToReference, CreateConstraint, and CreateDimension are false",
                    "verification": [
                        "requested side and distance from live native geometry",
                        "live output curve identity and closure state",
                        "source geometry and reference status preserved",
                        "all Sketch constraints and support frames preserved",
                        "non-target geometry, history, bodies, dirty state, and save/reopen readback",
                    ],
                    "execution": {
                        "prepare": "read-only chain preview; use nx_execute_plan and nx_verify_part",
                        "edit": "prepare, execute, and verify under one operation_id",
                        "success_requires": "state=SUCCEEDED and verified=true",
                        "unknown_outcome": "query with nx_get_operation; do not replay; writes remain halted until resolved",
                    },
                },
                "sketch_pattern": {
                    "availability_requires": [
                        "model_sketch_pattern_adapter_available", "model_sketch_pattern_write_enabled",
                    ],
                    "prepare_operation": "operation.prepare_sketch_pattern",
                    "edit_operation": "operation.edit_sketch_pattern",
                    "selection": "unique explicit source_curve_ids in one sketch; mirror axis_curve_id is a separate curve",
                    "supported_curve_types": ["LINE", "ARC"],
                    "arc_scope": "partial arcs and full circles",
                    "modes": ["linear", "rectangular", "circular", "mirror"],
                    "local_frame": "directions and center are expressed in the sketch local XY frame",
                    "native_engine": {
                        "linear_rectangular_circular": "NXOpen.CurveCollection.CreateLine/CreateArc followed by Sketch.AddGeometry(InferNoConstraints); transforms preserve native Line endpoints and Arc basis/radius/angle interval",
                        "mirror": "NXOpen.SketchMirrorPatternBuilder with constraint and source-reference conversion disabled",
                        "associativity": "geometry-only; no associative SketchPattern feature, pattern constraints, or spacing expressions are created",
                    },
                    "output_policy": "new native curves must be owned by the target Sketch and match the global Part.Curves Tag delta",
                    "counts": "linear instance_count, circular instance_count, and rectangular count_x * count_y are total positions including the source position; mirror has two positions",
                    "angle_step": "signed degrees with 0 < abs(angle_step_deg) < 360; negative steps are allowed and duplicate circular positions are rejected",
                    "limits": {
                        "source_curves": 256,
                        "total_positions": 256,
                        "total_positions_times_source_curves": 4096,
                    },
                    "preview": ["expected_pattern_instance_count", "expected_output_curve_count", "expected_instances with transforms"],
                    "execution": {
                        "prepare": "read-only preview; use nx_execute_plan and nx_verify_part",
                        "edit": "prepare, execute, and verify under one operation_id",
                        "success_requires": "state=SUCCEEDED and verified=true",
                        "unknown_outcome": "query with nx_get_operation; do not replay; writes remain halted until resolved",
                    },
                },
                "sketch_projection": {
                    "availability_requires": [
                        "model_sketch_projection_adapter_available", "model_sketch_projection_write_enabled",
                    ],
                    "prepare_operation": "operation.prepare_sketch_projection",
                    "edit_operation": "operation.edit_sketch_projection",
                    "request_fields": [
                        "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                        "source_curve_ids", "direction_xyz", "associative",
                    ],
                    "selection": "1 through 128 unique explicit source curve IDs",
                    "direction": "finite nonzero world XYZ vector; the server normalizes it before dispatch",
                    "supported_direction": "parallel or antiparallel to the target sketch normal (±target sketch normal); the NX adapter rejects other directions during prepare",
                    "associative": "boolean request selecting whether projected geometry maintains an associative relationship to its source",
                    "execution": {
                        "prepare": "read-only preview plan",
                        "edit": "prepare, execute, and verify under one operation_id",
                        "success_requires": "state=SUCCEEDED and verified=true",
                        "unknown_outcome": "query with nx_get_operation; do not replay; writes remain halted until resolved",
                    },
                },
                "sketch_reference_conversion": {
                    "availability_requires": [
                        "model_sketch_reference_conversion_adapter_available",
                        "model_sketch_reference_conversion_write_enabled",
                    ],
                    "prepare_operation": "operation.prepare_sketch_reference_conversion",
                    "edit_operation": "operation.edit_sketch_reference_conversion",
                    "request_fields": [
                        "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                        "object_ids", "target_state",
                    ],
                    "selection": "1 through 256 unique current Journal IDs from one Sketch; curve IDs come from geometry[].journal_id and supported dimensions from constraints[].journal_id in nx_inspect_sketch",
                    "supported_dimension_types": ["HorizontalDim", "VerticalDim"],
                    "dimension_unit": "mm; unsupported dimensions are rejected until their native unit mapping is verified",
                    "supported_curve_types": ["Line", "Arc"],
                    "full_circle_arc": "supported",
                    "unsupported_selected_curve_types": ["Ellipse", "EllipticalArc", "Spline", "other"],
                    "target_state": {
                        "reference": {
                            "curve": "NX UF Sketch reference status Reference (0)",
                            "dimension": "SketchDimensionalConstraint.DimensionState Reference; measurement only, not driving",
                        },
                        "active": {
                            "curve": "NX UF Sketch reference status Active (1)",
                            "dimension": "SketchDimensionalConstraint.DimensionState Driving",
                        },
                    },
                    "curve_semantics": "Reference curves may still participate in geometric constraints on other sketch geometry; reference status does not by itself mean the curve is outside the solver.",
                    "dimension_semantics": "Reference dimensions do not drive geometry; report ComputedSize separately from any associated expression value because the expression may not equal the measured size. Converting a reference horizontal/vertical dimension to Driving may normalize only its own numeric expression RHS/value to its current ComputedSize; the expression identity, unit, flags and other expressions must be preserved.",
                    "association_preservation": "Existing source associations and feature history are preserved; this conversion does not delete or recreate projected curves.",
                    "profile_candidates": "NX whole-sketch Section selection filters reference curves when allow-reference-curves is false. Product prepare reports a UF-status-based candidate set and does not create a Section; formal native acceptance verifies the NX Section behavior.",
                    "mixed_state_selection": "Already-target objects are no-ops; other selected objects convert. A request in which every object is already at target_state is rejected before a write transaction.",
                    "solver_state": "Prepare records cached GetStatus (which may be NotEvaluated and is not a solve result). Edit calls CalculateStatus with the Sketch active and EntireSketch scope; only evaluated UnderConstrained or WellConstrained post-status can succeed, while overconstrained, inconsistent, failed, or unevaluated post-status is rejected and rolled back.",
                    "solver_evaluation": "During edit, while the target Sketch is active, the adapter commits WorkRegionBuilder.Scope=EntireSketch, temporarily sets Session.Preferences.Sketch.FindMovableObjects=true, calls Sketch.CalculateStatus, and restores/readback-verifies the original preference in finally. It does not save user preferences; prepare does not evaluate or create a Section.",
                    "work_region_scope": "Edits explicitly normalize to EntireSketch to evaluate the full Sketch. Prior selected-object work-region UI preference is not observable as model state and is not guaranteed to be restored.",
                    "execution": {
                        "prepare": "read-only state and transaction plan",
                        "edit": "prepare, native conversion, independent verification, and real-Undo recovery under one operation_id",
                        "success_requires": "state=SUCCEEDED and verified=true",
                        "unknown_outcome": "query with nx_get_operation; do not replay; writes remain halted until resolved",
                    },
                },
                "datum_plane": {
                    "operation": "operation.prepare_datum_plane",
                    "availability_requires": [
                        "model_datum_plane_adapter_available", "model_datum_plane_write_enabled",
                    ],
                    "modes": {
                        "fixed": {
                            "required": ["origin_mm", "normal", "x_axis"],
                            "origin": "world-coordinate XYZ millimeters",
                            "orientation_arguments": ["normal", "x_axis"],
                        },
                        "offset": {
                            "required": ["distance_mm", "support_datum_plane_id OR support_face_id"],
                            "distance": "signed millimeters along the native reference normal; finite values including zero are accepted",
                            "reference_normal": "read from the native DatumPlane or planar Face",
                            "definition_readback": [
                                "mode", "distance_mm", "support_kind", "support_datum_plane_id or support_face_id",
                                "reference_normal", "native_method", "native_parent_ids",
                            ],
                        },
                        "angle": {
                            "required": ["angle_deg", "axis_line_id", "support_datum_plane_id OR support_face_id"],
                            "axis_line_id": "journal ID of a native NX Line; its StartPoint to EndPoint direction defines the positive rotation axis",
                            "axis_validation": "the nondegenerate Line must lie in the support plane",
                            "angle": "signed degrees in the closed interval [-180, 180]; positive follows the right-hand rule around the axis Line",
                            "reference_geometry": "one native DatumPlane or planar Face plus the selected native Line",
                            "definition_readback": [
                                "mode", "requested_angle_deg", "angle_deg", "native_expression_angle_deg",
                                "native_angle_expression", "support_kind", "support_datum_plane_id or support_face_id",
                                "axis_line_id", "native_method", "native_reference_geometry", "native_parent_ids",
                                "axis_in_result_plane", "axis_start_result_plane_residual_mm", "axis_end_result_plane_residual_mm",
                            ],
                        },
                        "two_lines": {
                            "required": ["line1_id", "line2_id"],
                            "line_ids": "journal IDs of two distinct native NX Lines in the current Work Part; use nx_inspect_sketch geometry IDs",
                            "geometry": "nonparallel infinite lines must intersect; parallel lines must be distinct and coplanar",
                            "units": "endpoints and residuals in millimeters; directions are dimensionless",
                            "tolerances": {
                                "minimum_line_length_mm": 1e-6,
                                "parallel_unit_direction_cross_norm": 1e-10,
                                "coplanarity_mm": 1e-6,
                                "normal_unit_component_error": 1e-7,
                            },
                            "definition_readback": [
                                "mode", "line1_id", "line2_id", "native_method", "native_alternate",
                                "native_reference_geometry", "native_geometry_line_ids", "line1_start_mm",
                                "line1_end_mm", "line2_start_mm", "line2_end_mm",
                                "line1_source_feature_ids", "line2_source_feature_ids", "parent_reference_matches",
                                "x_axis_source",
                                "line1_endpoint_plane_residuals_mm", "line2_endpoint_plane_residuals_mm",
                                "normal_matches_plane_unoriented",
                            ],
                        },
                    },
                    "execution": "prepare only; execute the reviewed returned plan with nx_execute_plan",
                },
                "sketch": {
                    "constraint_modes": {
                        "legacy_driving": "default; retains existing Driving dimensions for line, rectangle, and circle geometry",
                        "geometry_only": "creates requested native geometry without Driving dimensions or inferred Sketch constraints",
                    },
                    "frame_modes": {
                        "free": ["origin_mm", "normal", "x_axis"],
                        "support_face": {
                            "required": ["support_face_id", "origin_mm", "x_axis"],
                            "optional": ["support_body_id"],
                            "normal_source": "selected face",
                        },
                        "support_datum_plane": {
                            "required": ["support_datum_plane_id", "x_axis"],
                            "optional": ["origin_mm"],
                            "origin_default": "native datum plane origin",
                            "normal_source": "native datum plane",
                        },
                        "legacy_principal_plane": ["plane", "origin_mm"],
                        "legacy_selector_face": ["support_face_selector"],
                    },
                    "planar_face_enumeration": "nx_query_faces selector='all_planar' returns candidate records with face_journal_id",
                    "empty_sketch": "geometries may be omitted or an empty array; up to 32 supported geometry records are accepted",
                    "profile_analysis": {
                        "result_field": "profile_analysis",
                        "statuses": ["EMPTY", "OPEN_GEOMETRY", "VALID_CLOSED_PROFILES", "INVALID_SELF_INTERSECTION", "INVALID_DEGENERATE", "ANALYSIS_UNAVAILABLE"],
                        "checks": {
                            "native_intersection_check": ["VERIFIED", "FAILED", "UNAVAILABLE", "NOT_REQUIRED"],
                            "spline_self_intersection_check": ["VERIFIED", "FAILED", "UNAVAILABLE", "NOT_APPLICABLE"],
                        },
                        "counts": ["curve_count", "open_chain_count", "closed_loop_count", "outer_loop_count", "inner_loop_count", "section_loop_count"],
                        "validity_rule": "Only verified native intersections, spline self-intersection checks, a closed endpoint graph, and successful NX section classification may report VALID_CLOSED_PROFILES. Section loop count alone does not prove profile validity.",
                    },
                    "planes": ["XY", "XZ", "YZ"],
                    "face_mode": {
                        "argument": "support_face_selector",
                        "optional_arguments": ["support_body_id", "face_origin_offset_mm"],
                        "selection": "unique planar Face selected by top/bottom, signed X/Y/Z direction, outermost, or an explicit axis coordinate in millimeters",
                        "local_frame": "2D millimeter coordinates on the selected face; optional [u,v] origin offset defaults to [0,0]",
                        "profile_domain": "line and circular boundaries are read with 0.001 mm chord tolerance and 0.002 mm clearance; supported profiles are circles and complete axis-aligned rectangles",
                    },
                    "local_coordinates": "2D millimeter coordinates on the selected plane",
                    "origin": "world-coordinate XYZ millimeters",
                    "geometries": [
                        {
                            "type": "line",
                            "allowed_orientation": "any nonzero 2D segment",
                            "measured_length": "Euclidean distance between endpoints",
                            "driving_dimensions": [
                                "HorizontalDim for the endpoint horizontal projection",
                                "VerticalDim for the endpoint vertical projection",
                            ],
                            "parameter_edit_limit": "M2 edits one projected component per operation; it does not set Euclidean line length directly",
                        },
                        {"type": "rectangle", "constraints": ["driving width", "driving height"]},
                        {"type": "circle", "constraint": "driving diameter"},
                        {"type": "arc", "parameters": ["center_mm", "radius_mm", "start_angle_deg", "sweep_deg"], "sweep": "signed; positive counterclockwise from sketch-local +X; magnitude strictly between 0 and 360 degrees"},
                        {"type": "ellipse", "parameters": ["center_mm", "major_radius_mm", "minor_radius_mm", "rotation_deg"], "rotation": "major-axis direction in degrees in [0,360) relative to sketch-local +X, counterclockwise"},
                        {"type": "elliptical_arc", "parameters": ["center_mm", "major_radius_mm", "minor_radius_mm", "rotation_deg", "start_angle_deg", "sweep_deg"], "angles": "rotation_deg is in [0,360); start and sweep are ellipse parameter angles with signed sweep magnitude strictly between 0 and 360 degrees"},
                        {"type": "spline", "parameters": ["interpolation_points_mm", "degree", "endpoint_condition"], "interpolation": "4 through 32 points, nonperiodic cubic, normalized chord-length parameters; endpoint_condition='unconstrained' leaves endpoint slope and curvature unset"},
                    ],
                    "driving_constraint_units": "mm",
                },
                "extrude": {
                    "supported_profiles": ["valid closed whole-sketch domains from non-reference Line, Arc, Ellipse, and Spline curves", "valid open or closed curve domains with explicit finite create sheet output"],
                    "profiles_per_sketch": "all valid closed regions and nested loops for solid output; all valid active open/closed curve domains for explicit sheet output",
                    "unsupported_profile_error": "UNSUPPORTED_PROFILE",
                    "native_acceptance_status": "NX2506 documented F12 profile, limit, Boolean and output cases verified; unproven native outcomes are rejected",
                    "section_selection": "whole-sketch native curve feature rule with reference curves excluded; no implicit first-region selection",
                    "profile_output": {
                        "values": ["solid", "sheet"],
                        "default": "solid",
                        "sheet_native_verification": "finite create returns native sheet bodies and verifies surface area and geometry; open profiles require explicit sheet output",
                    },
                    "modes": ["create", "unite", "subtract"],
                    "directions": ["positive", "negative"],
                    "extents": ["finite", "through_all"],
                    "through_all_modes": ["unite", "subtract"],
                    "finite_limits": "legacy depth_mm means [0, depth]; alternatively provide signed start_offset_mm and end_offset_mm with end > start; forms are mutually exclusive",
                    "through_all_start": "optional signed start_offset_mm, default 0; depth_mm and end_offset_mm are forbidden",
                    "through_all_dependency_boundary": "native limit dependencies must be provable from the source Sketch and explicit target body lineage; additional unproven limit parents cause rejection and verified rollback",
                    "boolean_target": "explicit target_body_id required for unite and subtract",
                },
                "revolve": {
                    "availability_requires": ["model_revolve_adapter_available", "model_revolve_write_enabled"],
                    "operation": "operation.prepare_revolve",
                    "native_engine": "NXOpen.Features.RevolveBuilder with an explicit Sketch Section and point-direction Axis",
                    "section_selection": "all active non-reference curves from the explicit Sketch; whole-sketch F12 Section validation is reused",
                    "axis_point_mm": "world-space point on the requested axis, millimeters",
                    "axis_direction": "finite nonzero world XYZ vector; normalized before dispatch; positive angles follow the right-hand rule",
                    "axis_plane_rule": "the axis line must lie in the Sketch plane; an arbitrarily oriented Sketch and any compatible in-plane world axis are supported; off-plane axis is rejected",
                    "signed_angle_interval": "start_angle_deg and end_angle_deg are each in [-360,360]; end-start is signed, nonzero, and at most 360 degrees in magnitude; negative spans reverse the native axis and negate native endpoints to preserve the requested right-hand geometry",
                    "profile_output": {"values": ["solid", "sheet"], "default": "solid", "sheet_mode": "create only; NX native builder must accept the selected curve Section"},
                    "solid_profile": "valid closed whole-sketch regions; branches, self-intersections, invalid or ambiguous Section domains are rejected",
                    "modes": ["create", "unite", "subtract"],
                    "boolean_target": "one explicit solid target_body_id required for unite and subtract; create forbids a target; intersection is unsupported",
                    "execution": {
                        "prepare": "read-only preview; execute with nx_execute_plan",
                        "verify": "call nx_verify_part for a fresh native RevolveBuilder/Axis/limits readback, output body geometry, feature history and non-target preservation",
                        "unknown_outcome": "query nx_get_operation; do not replay; writes remain halted until resolved",
                    },
                },
                "thread": {
                    "operation": "operation.prepare_thread",
                    "availability_requires": ["model_helical_adapter_available", "model_helical_write_enabled"],
                    "supported_profile": "external solid, single-start, 60-degree thread",
                    "axis_choices": ["X", "Y", "Z"],
                    "start_mm_definition": "signed axial offset from axis_origin_mm",
                    "explicit_inputs": ["major_diameter_mm", "minor_diameter_mm", "pitch_mm"],
                    "minor_diameter_requirement": "positive and less than major_diameter_mm",
                    "maximum_length_to_pitch_ratio": 500,
                    "linear_dimensions_absolute_limit_mm": 1000000,
                    "axis_direction": "positive or negative selected axis",
                    "handedness": ["right", "left"],
                },
                "helical_groove": {
                    "operation": "operation.prepare_helical_groove",
                    "availability_requires": ["model_helical_adapter_available", "model_helical_write_enabled"],
                    "axis_choices": ["X", "Y", "Z"],
                    "start_mm_definition": "signed axial offset from axis_origin_mm",
                    "explicit_inputs": [
                        "root_diameter_mm", "reference_diameter_mm", "outer_diameter_mm", "space_width_mm",
                    ],
                    "diameter_order": "0 < root_diameter_mm < reference_diameter_mm < outer_diameter_mm",
                    "space_width_definition": "axial groove width at reference_diameter_mm",
                    "half_width_profile": "w(r)=space_width_mm/2+(r-reference_diameter_mm/2)*tan(flank_angle_deg)",
                    "axis_direction": "positive selected axis; handedness determines angular travel sign",
                    "maximum_length_to_lead_ratio": 50,
                    "linear_dimensions_absolute_limit_mm": 1000000,
                    "samples_per_turn": 96,
                    "interpolation": "interpolated spline",
                    "distance_tolerance_mm": 0.001,
                    "profile_constraints": [
                        "w(root_diameter_mm/2) > 0",
                        "2*w(outer_diameter_mm/2) < lead_mm",
                    ],
                    "phase_range_deg": [-360, 360],
                },
                "limitations": [
                    "a line segment must have distinct endpoints",
                    "open profiles cannot be extruded as solid profiles; sheet output must be explicitly requested and accepted by NX native validation",
                    "closed profiles must form valid whole-sketch regions; branches, self-intersections, degenerate or ambiguous Section geometry return UNSUPPORTED_PROFILE",
                    "reference curves are excluded from extrusion Section input",
                    "face mode requires a unique planar support Face and a provable supported profile domain",
                    "with M4 support markers present, Driving edits to any source Sketch of an existing Subtract targeting the same M4 support body return M4_SUBTRACT_CONTOUR_EDIT_UNSUPPORTED",
                ],
            },
            "model_write_enabled": write_enabled,
            "model_save_copy_enabled": status.get("model_save_copy_available") is True and state == "AI_OWNED" and
                status.get("writes_halted") is False and artifact_scope == "saved_independent_copy" and
                binding_identity.get("is_read_only") is not True,
            "writes_halted": status.get("writes_halted"),
            "write_halt_reason": status.get("write_halt_reason"),
            "active_operations": status.get("active_operations", 0),
            "model_write_support": {
                "operation": "set_driving_dimension_value",
                "availability_requires": [
                    "ownership_state=AI_OWNED", "parameter-edit adapter loaded", "writes_halted=false",
                ],
                "work_part_units": ["Millimeters"],
                "expression_unit_symbol": "mm",
                "requested_value": "finite positive decimal string",
                "supported_geometry_signatures": [
                    {"dimension_type": "DiameterDim", "geometry": "one full Arc; measured value is diameter"},
                    {"dimension_type": "HorizontalDim", "geometry": "ArcCenter plus Point; absolute X offset"},
                    {"dimension_type": "HorizontalDim", "geometry": "one Line with StartPoint and EndPoint; horizontal endpoint projection"},
                    {"dimension_type": "VerticalDim", "geometry": "one Line with StartPoint and EndPoint; vertical endpoint projection"},
                ],
                "line_length": "The measured line length is the Euclidean distance between endpoints; M2 parameter edits can change one horizontal or vertical endpoint projection per operation, not Euclidean length directly.",
                "validation": "Prepare resolves the live Driving constraint, unique expression relation, unit, and geometry; execute revalidates the prepared target.",
            },
            "model_datum_plane_write_support": {
                "operation": "operation.prepare_datum_plane",
                "availability_requires": [
                    "model_datum_plane_adapter_available", "ownership_state=AI_OWNED", "writes_halted=false",
                ],
                "request_fields": [
                    "origin_mm, normal, x_axis", "support_datum_plane_id or support_face_id, distance_mm",
                    "support_datum_plane_id or support_face_id, axis_line_id, angle_deg", "line1_id, line2_id",
                ],
                "execution": "prepare only; execute the reviewed returned plan with nx_execute_plan",
            },
            "model_verify_support": {
                "operation": "verify_successful_parameter_edit",
                "checks": ["expression_rhs_and_value", "measured_geometry", "feature_history", "dimension_associations"],
                "operation_id_requires": "a SUCCEEDED execute_parameter_edit operation in this bridge load",
            },
            "model_save_copy_support": {
                "operation": "nxopen_saveas_new_prt",
                "target_requires": "absolute .prt path that does not exist and differs from the takeover source path",
                "reopen_required": True,
                "current_work_part": "REJECTED_BEFORE_SAVEAS_OR_REOPEN_TO_PRESERVE_IN_MEMORY_PART_IDENTITY",
                "checks": ["expressions", "sketch_geometry", "feature_history", "dimension_associations"],
                "success_rotates": ["ownership_id", "binding_epoch"],
            },
            "model_control_enabled": False,
            "unavailable_reason": unavailable_reason,
        }
        if sweep_adapter_available:
            capabilities["available_operations"].append("operation.prepare_sweep")
            capabilities["model_native_geometry_support"]["prepare_operations"].append("operation.prepare_sweep")
        if boolean_adapter_available:
            capabilities["available_operations"].append("operation.prepare_boolean")
            capabilities["model_native_geometry_support"]["prepare_operations"].append("operation.prepare_boolean")
        if feature_pattern_adapter_available:
            capabilities["available_operations"].append("operation.prepare_feature_pattern")
            capabilities["model_native_geometry_support"]["prepare_operations"].append("operation.prepare_feature_pattern")
        if feature_mirror_adapter_available:
            capabilities["available_operations"].append("operation.prepare_feature_mirror")
            capabilities["model_native_geometry_support"]["prepare_operations"].append("operation.prepare_feature_mirror")
        if spatial_curve_adapter_available:
            capabilities["available_operations"].append("operation.prepare_spatial_curve")
            capabilities["model_native_geometry_support"]["prepare_operations"].append("operation.prepare_spatial_curve")
        if chamfer_adapter_available:
            capabilities["available_operations"].append("operation.prepare_chamfer")
            capabilities["model_native_geometry_support"]["prepare_operations"].append("operation.prepare_chamfer")
        if edge_blend_adapter_available:
            capabilities["available_operations"].append("operation.prepare_edge_blend")
            capabilities["model_native_geometry_support"]["prepare_operations"].append("operation.prepare_edge_blend")
        if hole_adapter_available:
            capabilities["available_operations"].append("operation.prepare_hole")
            capabilities["model_native_geometry_support"]["prepare_operations"].append("operation.prepare_hole")
        if "unknown_operation_ids" in status:
            capabilities["unknown_operation_ids"] = status["unknown_operation_ids"]
        return capabilities

    def _with_local_write_halt(self, status: dict[str, Any]) -> dict[str, Any]:
        if not self._unknown_operation_ids:
            return status
        result = dict(status)
        result["model_write_enabled"] = False
        result["model_feature_pattern_write_enabled"] = False
        result["model_feature_mirror_write_enabled"] = False
        result["model_spatial_curve_write_enabled"] = False
        result["model_sweep_write_enabled"] = False
        result["model_chamfer_write_enabled"] = False
        result["model_edge_blend_write_enabled"] = False
        result["writes_halted"] = True
        result["write_halt_reason"] = "CLIENT_OUTCOME_UNKNOWN"
        result["unknown_operation_ids"] = sorted(self._unknown_operation_ids)
        return result

    @staticmethod
    def _validated_operation_id(value: Any) -> str:
        if not isinstance(value, str) or not 1 <= len(value) <= 128:
            raise _InvalidToolArguments("operation_id must be a string of length 1 through 128")
        try:
            value.encode("utf-8", errors="strict")
        except UnicodeEncodeError as exc:
            raise _InvalidToolArguments("operation_id must contain valid Unicode text") from exc
        return value

    @classmethod
    def _validate_operation_prepare(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        request = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
            "sketch_id": cls._validated_nonempty_string(arguments["sketch_id"], "sketch_id"),
            "constraint_id": cls._validated_nonempty_string(arguments["constraint_id"], "constraint_id"),
            "value": cls._validated_decimal_string(arguments["value"]),
            "unit": cls._validated_unit_symbol(arguments["unit"]),
        }
        return request

    @classmethod
    def _validate_prepare_datum_plane(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        common = {"operation_id", "ownership_id", "binding_epoch", "revision_id"}
        fixed = {"origin_mm", "normal", "x_axis"}
        datum_offset = {"support_datum_plane_id", "distance_mm"}
        face_offset = {"support_face_id", "distance_mm"}
        datum_angle = {"support_datum_plane_id", "axis_line_id", "angle_deg"}
        face_angle = {"support_face_id", "axis_line_id", "angle_deg"}
        two_lines = {"line1_id", "line2_id"}
        keys = set(arguments)
        if not common <= keys:
            raise _InvalidToolArguments("nx_prepare_datum_plane requires operation_id, ownership_id, binding_epoch, and revision_id")
        if keys == common | fixed:
            mode = "fixed"
        elif keys == common | datum_offset:
            mode = "offset_datum"
        elif (common | face_offset) <= keys <= (common | face_offset | {"support_face_tag"}):
            mode = "offset_face"
        elif keys == common | datum_angle:
            mode = "angle_datum"
        elif (common | face_angle) <= keys <= (common | face_angle | {"support_face_tag"}):
            mode = "angle_face"
        elif keys == common | two_lines:
            mode = "two_lines"
        else:
            raise _InvalidToolArguments(
                "nx_prepare_datum_plane requires exactly one fixed frame, offset reference and distance, "
                "angle reference with axis_line_id and angle_deg, or line1_id and line2_id; modes cannot be mixed"
            )
        request = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
        }
        if mode == "fixed":
            request.update({
                "origin_mm": cls._validated_number_array(arguments["origin_mm"], 3, "origin_mm"),
                "normal": cls._validated_number_array(arguments["normal"], 3, "normal"),
                "x_axis": cls._validated_number_array(arguments["x_axis"], 3, "x_axis"),
            })
        elif mode.startswith("offset_"):
            reference_key = "support_datum_plane_id" if mode == "offset_datum" else "support_face_id"
            request[reference_key] = cls._validated_nonempty_string(arguments[reference_key], reference_key)
            if mode == "offset_face" and "support_face_tag" in arguments:
                request["support_face_tag"] = cls._validated_nonempty_string(
                    arguments["support_face_tag"], "support_face_tag",
                )
            request["distance_mm"] = cls._validated_finite_number(arguments["distance_mm"], "distance_mm")
        elif mode == "two_lines":
            request["line1_id"] = cls._validated_nonempty_string(arguments["line1_id"], "line1_id")
            request["line2_id"] = cls._validated_nonempty_string(arguments["line2_id"], "line2_id")
            if request["line1_id"] == request["line2_id"]:
                raise _InvalidToolArguments("line1_id and line2_id must identify distinct native Lines")
        else:
            reference_key = "support_datum_plane_id" if mode == "angle_datum" else "support_face_id"
            request[reference_key] = cls._validated_nonempty_string(arguments[reference_key], reference_key)
            if mode == "angle_face" and "support_face_tag" in arguments:
                request["support_face_tag"] = cls._validated_nonempty_string(
                    arguments["support_face_tag"], "support_face_tag",
                )
            request["axis_line_id"] = cls._validated_nonempty_string(arguments["axis_line_id"], "axis_line_id")
            angle = cls._validated_finite_number(arguments["angle_deg"], "angle_deg")
            if not -180 <= angle <= 180:
                raise _InvalidToolArguments("angle_deg must be between -180 and 180 degrees, inclusive")
            request["angle_deg"] = angle
        return request

    @classmethod
    def _validate_prepare_sketch(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        required_base = {"operation_id", "ownership_id", "binding_epoch", "revision_id"}
        argument_keys = set(arguments)
        mode_keys = argument_keys - {"geometries", "constraint_mode"}
        if not required_base <= mode_keys:
            raise _InvalidToolArguments("nx_prepare_sketch requires operation_id, ownership_id, binding_epoch, and revision_id")
        constraint_mode = arguments.get("constraint_mode", "legacy_driving")
        if not isinstance(constraint_mode, str) or constraint_mode not in {"legacy_driving", "geometry_only"}:
            raise _InvalidToolArguments("constraint_mode must be legacy_driving or geometry_only")

        principal_keys = required_base | {"plane", "origin_mm"}
        selector_keys = required_base | {"support_face_selector"}
        free_frame_keys = required_base | {"origin_mm", "normal", "x_axis"}
        face_frame_keys = required_base | {"support_face_id", "origin_mm", "x_axis"}
        datum_frame_keys = required_base | {"support_datum_plane_id", "x_axis"}

        if mode_keys == principal_keys:
            mode = "principal"
        elif selector_keys <= mode_keys <= selector_keys | {"face_origin_offset_mm", "support_body_id"}:
            mode = "selector_face"
        elif mode_keys == free_frame_keys:
            mode = "free_frame"
        elif face_frame_keys <= mode_keys <= face_frame_keys | {"support_body_id", "support_face_tag"}:
            mode = "face_frame"
        elif mode_keys == datum_frame_keys or mode_keys == datum_frame_keys | {"origin_mm"}:
            mode = "datum_frame"
        else:
            raise _InvalidToolArguments(
                "nx_prepare_sketch requires a legacy principal plane, legacy face selector, free frame, "
                "explicit support face frame, or datum-plane frame"
            )

        common = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
        }
        if mode == "principal":
            plane = arguments["plane"]
            if not isinstance(plane, str) or plane not in {"XY", "XZ", "YZ"}:
                raise _InvalidToolArguments("plane must be XY, XZ, or YZ")
            origin = cls._validated_number_array(arguments["origin_mm"], 3, "origin_mm")
        elif mode == "selector_face":
            selector = cls._validated_nonempty_string(arguments["support_face_selector"], "support_face_selector")
            face_origin_offset = cls._validated_number_array(
                arguments.get("face_origin_offset_mm", [0, 0]), 2, "face_origin_offset_mm",
            )
            support_body_id = None
            if "support_body_id" in arguments:
                support_body_id = cls._validated_nonempty_string(arguments["support_body_id"], "support_body_id")
        elif mode == "free_frame":
            origin = cls._validated_number_array(arguments["origin_mm"], 3, "origin_mm")
            normal = cls._validated_number_array(arguments["normal"], 3, "normal")
            x_axis = cls._validated_number_array(arguments["x_axis"], 3, "x_axis")
        elif mode == "face_frame":
            origin = cls._validated_number_array(arguments["origin_mm"], 3, "origin_mm")
            x_axis = cls._validated_number_array(arguments["x_axis"], 3, "x_axis")
            support_body_id = None
            if "support_body_id" in arguments:
                support_body_id = cls._validated_nonempty_string(arguments["support_body_id"], "support_body_id")
        else:
            x_axis = cls._validated_number_array(arguments["x_axis"], 3, "x_axis")
            origin = None
            if "origin_mm" in arguments:
                origin = cls._validated_number_array(arguments["origin_mm"], 3, "origin_mm")

        geometries = arguments.get("geometries", [])
        if not isinstance(geometries, list) or len(geometries) > 32:
            raise _InvalidToolArguments("geometries must contain between 0 and 32 geometry records")

        normalized_geometries: list[dict[str, Any]] = []
        seen_ids: set[str] = set()
        for index, geometry in enumerate(geometries):
            if not isinstance(geometry, dict):
                raise _InvalidToolArguments(f"geometries[{index}] must be an object")
            geometry_type = geometry.get("type")
            geometry_id = cls._validated_geometry_id(geometry.get("id"))
            if geometry_id in seen_ids:
                raise _InvalidToolArguments("geometry ids must be unique within the sketch request")
            seen_ids.add(geometry_id)

            if geometry_type == "line":
                if set(geometry) != {"id", "type", "start_mm", "end_mm"}:
                    raise _InvalidToolArguments("line geometry requires exactly id, type, start_mm, and end_mm")
                start = cls._validated_number_array(geometry["start_mm"], 2, "start_mm")
                end = cls._validated_number_array(geometry["end_mm"], 2, "end_mm")
                if start == end:
                    raise _InvalidToolArguments("line start_mm and end_mm must differ")
                normalized_geometries.append({"id": geometry_id, "type": "line", "start_mm": start, "end_mm": end})
            elif geometry_type == "rectangle":
                if set(geometry) != {"id", "type", "corner_mm", "width_mm", "height_mm"}:
                    raise _InvalidToolArguments("rectangle geometry requires exactly id, type, corner_mm, width_mm, and height_mm")
                normalized_geometries.append({
                    "id": geometry_id,
                    "type": "rectangle",
                    "corner_mm": cls._validated_number_array(geometry["corner_mm"], 2, "corner_mm"),
                    "width_mm": cls._validated_positive_number(geometry["width_mm"], "width_mm"),
                    "height_mm": cls._validated_positive_number(geometry["height_mm"], "height_mm"),
                })
            elif geometry_type == "circle":
                if set(geometry) != {"id", "type", "center_mm", "diameter_mm"}:
                    raise _InvalidToolArguments("circle geometry requires exactly id, type, center_mm, and diameter_mm")
                normalized_geometries.append({
                    "id": geometry_id,
                    "type": "circle",
                    "center_mm": cls._validated_number_array(geometry["center_mm"], 2, "center_mm"),
                    "diameter_mm": cls._validated_positive_number(geometry["diameter_mm"], "diameter_mm"),
                })
            elif geometry_type == "arc":
                if set(geometry) != {"id", "type", "center_mm", "radius_mm", "start_angle_deg", "sweep_deg"}:
                    raise _InvalidToolArguments("arc geometry requires exactly id, type, center_mm, radius_mm, start_angle_deg, and sweep_deg")
                start_angle = cls._validated_finite_number(geometry["start_angle_deg"], "start_angle_deg")
                sweep = cls._validated_finite_number(geometry["sweep_deg"], "sweep_deg")
                cls._validate_arc_angles(start_angle, sweep, "arc")
                normalized_geometries.append({
                    "id": geometry_id,
                    "type": "arc",
                    "center_mm": cls._validated_number_array(geometry["center_mm"], 2, "center_mm"),
                    "radius_mm": cls._validated_positive_number(geometry["radius_mm"], "radius_mm"),
                    "start_angle_deg": start_angle,
                    "sweep_deg": sweep,
                })
            elif geometry_type == "ellipse" or geometry_type == "elliptical_arc":
                base = {"id", "type", "center_mm", "major_radius_mm", "minor_radius_mm", "rotation_deg"}
                expected = base if geometry_type == "ellipse" else base | {"start_angle_deg", "sweep_deg"}
                if set(geometry) != expected:
                    raise _InvalidToolArguments(f"{geometry_type} geometry requires exactly its documented fields")
                major = cls._validated_positive_number(geometry["major_radius_mm"], "major_radius_mm")
                minor = cls._validated_positive_number(geometry["minor_radius_mm"], "minor_radius_mm")
                rotation = cls._validated_finite_number(geometry["rotation_deg"], "rotation_deg")
                if major < minor:
                    raise _InvalidToolArguments("major_radius_mm must be greater than or equal to minor_radius_mm")
                if not 0.0 <= rotation < 360.0:
                    raise _InvalidToolArguments("rotation_deg must be in [0, 360)")
                normalized = {
                    "id": geometry_id,
                    "type": geometry_type,
                    "center_mm": cls._validated_number_array(geometry["center_mm"], 2, "center_mm"),
                    "major_radius_mm": major,
                    "minor_radius_mm": minor,
                    "rotation_deg": rotation,
                }
                if geometry_type == "elliptical_arc":
                    start_angle = cls._validated_finite_number(geometry["start_angle_deg"], "start_angle_deg")
                    sweep = cls._validated_finite_number(geometry["sweep_deg"], "sweep_deg")
                    cls._validate_arc_angles(start_angle, sweep, "elliptical_arc")
                    normalized.update({"start_angle_deg": start_angle, "sweep_deg": sweep})
                normalized_geometries.append(normalized)
            elif geometry_type == "spline":
                if set(geometry) != {"id", "type", "interpolation_points_mm", "degree", "endpoint_condition"}:
                    raise _InvalidToolArguments("spline geometry requires exactly id, type, interpolation_points_mm, degree, and endpoint_condition")
                points = geometry["interpolation_points_mm"]
                if not isinstance(points, list) or not 4 <= len(points) <= 32:
                    raise _InvalidToolArguments("spline interpolation_points_mm must contain 4 through 32 points")
                degree = geometry["degree"]
                if isinstance(degree, bool) or degree != 3:
                    raise _InvalidToolArguments("spline degree must be 3")
                if geometry["endpoint_condition"] != "unconstrained":
                    raise _InvalidToolArguments("spline endpoint_condition must be unconstrained")
                normalized_points = [
                    cls._validated_number_array(point, 2, f"interpolation_points_mm[{point_index}]")
                    for point_index, point in enumerate(points)
                ]
                if any(math.dist(left, right) <= 1e-9 for left, right in zip(normalized_points, normalized_points[1:])):
                    raise _InvalidToolArguments("consecutive spline interpolation points must differ")
                normalized_geometries.append({
                    "id": geometry_id,
                    "type": "spline",
                    "interpolation_points_mm": normalized_points,
                    "degree": 3,
                    "endpoint_condition": "unconstrained",
                })
            else:
                raise _InvalidToolArguments("geometry type must be line, rectangle, circle, arc, ellipse, elliptical_arc, or spline")

        request = {**common, "geometries": normalized_geometries}
        if constraint_mode == "geometry_only":
            request["constraint_mode"] = constraint_mode
        if mode == "principal":
            request.update({"plane": plane, "origin_mm": origin})
        elif mode == "selector_face":
            request.update({
                "support_face_selector": selector,
                "face_origin_offset_mm": face_origin_offset,
            })
            if support_body_id is not None:
                request["support_body_id"] = support_body_id
        elif mode == "free_frame":
            request.update({"origin_mm": origin, "normal": normal, "x_axis": x_axis})
        elif mode == "face_frame":
            request.update({
                "support_face_id": cls._validated_nonempty_string(arguments["support_face_id"], "support_face_id"),
                "origin_mm": origin,
                "x_axis": x_axis,
            })
            if "support_face_tag" in arguments:
                request["support_face_tag"] = cls._validated_nonempty_string(
                    arguments["support_face_tag"], "support_face_tag",
                )
            if support_body_id is not None:
                request["support_body_id"] = support_body_id
        else:
            request.update({
                "support_datum_plane_id": cls._validated_nonempty_string(
                    arguments["support_datum_plane_id"], "support_datum_plane_id",
                ),
                "x_axis": x_axis,
            })
            if origin is not None:
                request["origin_mm"] = origin
        return request

    @classmethod
    def _validate_prepare_sketch_corner(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        common = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id", "kind",
            "curve1_id", "curve2_id", "curve1_end", "curve2_end",
        }
        kind = arguments.get("kind")
        if kind == "fillet":
            expected = common | {"radius_mm"}
        elif kind == "chamfer":
            has_distance2 = "distance2_mm" in arguments
            has_angle = "angle_deg" in arguments
            if has_distance2 == has_angle:
                raise _InvalidToolArguments(
                    "chamfer requires distance1_mm and exactly one of distance2_mm or angle_deg"
                )
            expected = common | {"distance1_mm", "distance2_mm" if has_distance2 else "angle_deg"}
        else:
            raise _InvalidToolArguments("kind must be fillet or chamfer")
        if set(arguments) != expected:
            if kind == "fillet":
                raise _InvalidToolArguments("fillet requires exactly its common fields and radius_mm")
            raise _InvalidToolArguments(
                "chamfer requires exactly its common fields, distance1_mm, and one of distance2_mm or angle_deg"
            )

        curve1_id = cls._validated_bounded_nonempty_string(arguments["curve1_id"], "curve1_id", 128)
        curve2_id = cls._validated_bounded_nonempty_string(arguments["curve2_id"], "curve2_id", 128)
        if curve1_id == curve2_id:
            raise _InvalidToolArguments("curve1_id and curve2_id must identify different sketch curves")
        curve1_end = arguments["curve1_end"]
        curve2_end = arguments["curve2_end"]
        if not isinstance(curve1_end, str) or curve1_end not in {"start", "end"}:
            raise _InvalidToolArguments("curve1_end must be exactly start or end")
        if not isinstance(curve2_end, str) or curve2_end not in {"start", "end"}:
            raise _InvalidToolArguments("curve2_end must be exactly start or end")

        request: dict[str, Any] = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
            "sketch_id": cls._validated_nonempty_string(arguments["sketch_id"], "sketch_id"),
            "kind": kind,
            "curve1_id": curve1_id,
            "curve2_id": curve2_id,
            "curve1_end": curve1_end,
            "curve2_end": curve2_end,
        }
        if kind == "fillet":
            request["radius_mm"] = cls._validated_positive_number(arguments["radius_mm"], "radius_mm")
        else:
            request["distance1_mm"] = cls._validated_positive_number(arguments["distance1_mm"], "distance1_mm")
            if "distance2_mm" in arguments:
                request["distance2_mm"] = cls._validated_positive_number(arguments["distance2_mm"], "distance2_mm")
            else:
                angle = cls._validated_finite_number(arguments["angle_deg"], "angle_deg")
                if not 0 < angle < 180:
                    raise _InvalidToolArguments("angle_deg must be greater than 0 and less than 180 degrees")
                request["angle_deg"] = angle
        return request

    @classmethod
    def _validate_prepare_sketch_offset(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        expected = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "curve_chain", "distance_mm", "side", "retain_original", "join_mode",
        }
        if set(arguments) != expected:
            raise _InvalidToolArguments("sketch offset requires exactly the documented operation, chain, distance, side, retention, and join fields")
        if arguments["retain_original"] is not True:
            raise _InvalidToolArguments("retain_original must be the boolean true")
        side = arguments["side"]
        if not isinstance(side, str) or side not in {"left", "right"}:
            raise _InvalidToolArguments("side must be exactly left or right")
        join_mode = arguments["join_mode"]
        if not isinstance(join_mode, str) or join_mode not in {"extension", "arc"}:
            raise _InvalidToolArguments("join_mode must be exactly extension or arc")
        raw_chain = arguments["curve_chain"]
        if not isinstance(raw_chain, list) or not 1 <= len(raw_chain) <= 128:
            raise _InvalidToolArguments("curve_chain must contain 1 through 128 ordered curves")
        chain: list[dict[str, str]] = []
        seen_ids: set[str] = set()
        for index, item in enumerate(raw_chain):
            if not isinstance(item, dict) or set(item) != {"curve_id", "direction"}:
                raise _InvalidToolArguments(f"curve_chain[{index}] requires exactly curve_id and direction")
            curve_id = cls._validated_bounded_nonempty_string(item["curve_id"], f"curve_chain[{index}].curve_id", 4096)
            direction = item["direction"]
            if not isinstance(direction, str) or direction not in {"forward", "reverse"}:
                raise _InvalidToolArguments(f"curve_chain[{index}].direction must be exactly forward or reverse")
            if curve_id in seen_ids:
                raise _InvalidToolArguments("curve_chain cannot contain duplicate curve_id values")
            seen_ids.add(curve_id)
            chain.append({"curve_id": curve_id, "direction": direction})
        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
            "sketch_id": cls._validated_nonempty_string(arguments["sketch_id"], "sketch_id"),
            "curve_chain": chain,
            "distance_mm": cls._validated_positive_number(arguments["distance_mm"], "distance_mm"),
            "side": side,
            "retain_original": True,
            "join_mode": join_mode,
        }

    @classmethod
    def _validate_prepare_sketch_pattern(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        common = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "source_curve_ids", "mode",
        }
        mode_fields = {
            "linear": {"direction_xy", "spacing_mm", "instance_count"},
            "rectangular": {
                "direction_x_xy", "direction_y_xy", "spacing_x_mm", "spacing_y_mm", "count_x", "count_y",
            },
            "circular": {"center_xy", "angle_step_deg", "instance_count"},
            "mirror": {"axis_curve_id"},
        }
        mode = arguments.get("mode")
        if not isinstance(mode, str) or mode not in mode_fields:
            raise _InvalidToolArguments("mode must be linear, rectangular, circular, or mirror")
        if set(arguments) != common | mode_fields[mode]:
            raise _InvalidToolArguments(f"{mode} sketch pattern requires exactly its documented fields")

        source_ids = arguments["source_curve_ids"]
        if not isinstance(source_ids, list) or not 1 <= len(source_ids) <= 256:
            raise _InvalidToolArguments("source_curve_ids must contain 1 through 256 explicit curve IDs")
        checked_source_ids = [
            cls._validated_bounded_nonempty_string(value, f"source_curve_ids[{index}]", 4096)
            for index, value in enumerate(source_ids)
        ]
        if len(set(checked_source_ids)) != len(checked_source_ids):
            raise _InvalidToolArguments("source_curve_ids must be unique")

        request: dict[str, Any] = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
            "sketch_id": cls._validated_nonempty_string(arguments["sketch_id"], "sketch_id"),
            "source_curve_ids": checked_source_ids,
            "mode": mode,
        }
        if mode == "linear":
            direction = cls._validated_number_array(arguments["direction_xy"], 2, "direction_xy")
            if direction[0] == 0 and direction[1] == 0:
                raise _InvalidToolArguments("direction_xy must be nonzero")
            request["direction_xy"] = direction
            request["spacing_mm"] = cls._validated_positive_number(arguments["spacing_mm"], "spacing_mm")
            count = cls._validated_positive_int(arguments["instance_count"], "instance_count")
            if not 2 <= count <= 256:
                raise _InvalidToolArguments("instance_count must be between 2 and 256, including the source position")
            request["instance_count"] = count
            total_positions = count
        elif mode == "rectangular":
            direction_x = cls._validated_number_array(arguments["direction_x_xy"], 2, "direction_x_xy")
            direction_y = cls._validated_number_array(arguments["direction_y_xy"], 2, "direction_y_xy")
            if direction_x[0] == 0 and direction_x[1] == 0:
                raise _InvalidToolArguments("direction_x_xy must be nonzero")
            if direction_y[0] == 0 and direction_y[1] == 0:
                raise _InvalidToolArguments("direction_y_xy must be nonzero")
            scale_x = max(abs(direction_x[0]), abs(direction_x[1]))
            scale_y = max(abs(direction_y[0]), abs(direction_y[1]))
            scaled_x = (direction_x[0] / scale_x, direction_x[1] / scale_x)
            scaled_y = (direction_y[0] / scale_y, direction_y[1] / scale_y)
            norm_x = math.hypot(*scaled_x)
            norm_y = math.hypot(*scaled_y)
            normalized_dot = (
                scaled_x[0] / norm_x * (scaled_y[0] / norm_y)
                + scaled_x[1] / norm_x * (scaled_y[1] / norm_y)
            )
            if abs(normalized_dot) > 1e-9:
                raise _InvalidToolArguments("direction_x_xy and direction_y_xy must be orthogonal")
            request["direction_x_xy"] = direction_x
            request["direction_y_xy"] = direction_y
            request["spacing_x_mm"] = cls._validated_positive_number(arguments["spacing_x_mm"], "spacing_x_mm")
            request["spacing_y_mm"] = cls._validated_positive_number(arguments["spacing_y_mm"], "spacing_y_mm")
            count_x = cls._validated_positive_int(arguments["count_x"], "count_x")
            count_y = cls._validated_positive_int(arguments["count_y"], "count_y")
            if count_x < 2 or count_y < 2:
                raise _InvalidToolArguments("count_x and count_y must each be at least 2, including the source position")
            total_positions = count_x * count_y
            if total_positions > 256:
                raise _InvalidToolArguments("count_x * count_y must not exceed 256 total positions")
            request["count_x"] = count_x
            request["count_y"] = count_y
        elif mode == "circular":
            request["center_xy"] = cls._validated_number_array(arguments["center_xy"], 2, "center_xy")
            angle_step = cls._validated_finite_number(arguments["angle_step_deg"], "angle_step_deg")
            if angle_step == 0:
                raise _InvalidToolArguments("angle_step_deg must be nonzero")
            if abs(angle_step) >= 360.0:
                raise _InvalidToolArguments("angle_step_deg must satisfy 0 < abs(angle_step_deg) < 360 degrees")
            count = cls._validated_positive_int(arguments["instance_count"], "instance_count")
            if not 2 <= count <= 256:
                raise _InvalidToolArguments("instance_count must be between 2 and 256, including the source position")
            normalized_step = angle_step % 360.0
            seen_angles: list[float] = []
            for index in range(count):
                angle = (normalized_step * index) % 360.0
                if any(min(abs(angle - seen), 360.0 - abs(angle - seen)) <= 1e-9 for seen in seen_angles):
                    raise _InvalidToolArguments("angle_step_deg and instance_count produce duplicate circular positions")
                seen_angles.append(angle)
            request["angle_step_deg"] = angle_step
            request["instance_count"] = count
            total_positions = count
        else:
            axis_curve_id = cls._validated_bounded_nonempty_string(arguments["axis_curve_id"], "axis_curve_id", 4096)
            if axis_curve_id in checked_source_ids:
                raise _InvalidToolArguments("axis_curve_id must not occur among source_curve_ids")
            request["axis_curve_id"] = axis_curve_id
            total_positions = 2

        if total_positions * len(checked_source_ids) > 4096:
            raise _InvalidToolArguments("total pattern positions times source_curve_ids count must not exceed 4096")
        return request

    @classmethod
    def _validate_prepare_sketch_projection(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        expected = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "source_curve_ids", "direction_xyz", "associative",
        }
        if set(arguments) != expected:
            raise _InvalidToolArguments(
                "sketch projection requires exactly operation_id, ownership_id, binding_epoch, revision_id, "
                "sketch_id, source_curve_ids, direction_xyz, and associative"
            )

        source_ids = arguments["source_curve_ids"]
        if not isinstance(source_ids, list) or not 1 <= len(source_ids) <= 128:
            raise _InvalidToolArguments("source_curve_ids must contain 1 through 128 explicit curve IDs")
        checked_source_ids = [
            cls._validated_nonempty_string(value, f"source_curve_ids[{index}]")
            for index, value in enumerate(source_ids)
        ]
        if len(set(checked_source_ids)) != len(checked_source_ids):
            raise _InvalidToolArguments("source_curve_ids must be unique")

        direction = cls._validated_number_array(arguments["direction_xyz"], 3, "direction_xyz")
        scale = max(abs(component) for component in direction)
        if scale == 0:
            raise _InvalidToolArguments("direction_xyz must be nonzero")
        scaled_direction = [component / scale for component in direction]
        scaled_norm = math.hypot(*scaled_direction)
        normalized_direction = [component / scaled_norm for component in scaled_direction]

        associative = arguments["associative"]
        if not isinstance(associative, bool):
            raise _InvalidToolArguments("associative must be a boolean")

        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
            "sketch_id": cls._validated_nonempty_string(arguments["sketch_id"], "sketch_id"),
            "source_curve_ids": checked_source_ids,
            "direction_xyz": normalized_direction,
            "associative": associative,
        }

    @classmethod
    def _validate_prepare_sketch_reference_conversion(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        expected = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "object_ids", "target_state",
        }
        if set(arguments) != expected:
            raise _InvalidToolArguments(
                "sketch reference conversion requires exactly operation_id, ownership_id, binding_epoch, "
                "revision_id, sketch_id, object_ids, and target_state"
            )
        object_ids = arguments["object_ids"]
        if not isinstance(object_ids, list) or not 1 <= len(object_ids) <= 256:
            raise _InvalidToolArguments("object_ids must contain 1 through 256 explicit curve or dimension IDs")
        checked_object_ids = [
            cls._validated_bounded_nonempty_string(value, f"object_ids[{index}]", 4096)
            for index, value in enumerate(object_ids)
        ]
        if len(set(checked_object_ids)) != len(checked_object_ids):
            raise _InvalidToolArguments("object_ids must be unique")
        target_state = cls._validated_nonempty_string(arguments["target_state"], "target_state")
        if target_state not in {"reference", "active"}:
            raise _InvalidToolArguments("target_state must be reference or active")
        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
            "sketch_id": cls._validated_bounded_nonempty_string(arguments["sketch_id"], "sketch_id", 4096),
            "object_ids": checked_object_ids,
            "target_state": target_state,
        }

    @classmethod
    def _validate_prepare_extrude(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        mode = arguments.get("mode")
        direction = arguments.get("direction")
        extent = arguments.get("extent")
        profile_output = arguments.get("profile_output", "solid")
        if not isinstance(mode, str) or mode not in {"create", "unite", "subtract"}:
            raise _InvalidToolArguments("mode must be create, unite, or subtract")
        if not isinstance(direction, str) or direction not in {"positive", "negative"}:
            raise _InvalidToolArguments("direction must be positive or negative")
        if not isinstance(extent, str) or extent not in {"finite", "through_all"}:
            raise _InvalidToolArguments("extent must be finite or through_all")
        if not isinstance(profile_output, str) or profile_output not in {"solid", "sheet"}:
            raise _InvalidToolArguments("profile_output must be solid or sheet")
        if mode == "create" and extent != "finite":
            raise _InvalidToolArguments("create extrusion supports only finite extent")
        if profile_output == "sheet" and (mode != "create" or extent != "finite"):
            raise _InvalidToolArguments("sheet output is supported only for finite create extrusion")

        base_fields = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "mode", "direction", "extent",
        }
        allowed_fields = set(base_fields)
        if "profile_output" in arguments:
            allowed_fields.add("profile_output")
        has_depth = "depth_mm" in arguments
        has_start = "start_offset_mm" in arguments
        has_end = "end_offset_mm" in arguments
        if has_depth:
            allowed_fields.add("depth_mm")
        if has_start:
            allowed_fields.add("start_offset_mm")
        if has_end:
            allowed_fields.add("end_offset_mm")
        if mode != "create":
            allowed_fields.add("target_body_id")
        if not base_fields.issubset(arguments) or not set(arguments).issubset(allowed_fields):
            raise _InvalidToolArguments("nx_prepare_extrude received missing or unsupported fields")
        if mode != "create" and "target_body_id" not in arguments:
            raise _InvalidToolArguments("target_body_id is required for unite and subtract")
        if mode == "create" and "target_body_id" in arguments:
            raise _InvalidToolArguments("target_body_id is not allowed for create")

        if extent == "finite":
            if has_depth and (has_start or has_end):
                raise _InvalidToolArguments("depth_mm cannot be combined with start_offset_mm or end_offset_mm")
            if has_depth:
                start_mm = 0
                end_mm = cls._validated_positive_number(arguments["depth_mm"], "depth_mm")
            elif has_start and has_end:
                start_mm = cls._validated_finite_number(arguments["start_offset_mm"], "start_offset_mm")
                end_mm = cls._validated_finite_number(arguments["end_offset_mm"], "end_offset_mm")
                span_mm = end_mm - start_mm
                if not math.isfinite(span_mm) or span_mm <= 0:
                    raise _InvalidToolArguments("end_offset_mm must be greater than start_offset_mm with a finite span")
            else:
                raise _InvalidToolArguments("finite extent requires depth_mm or both start_offset_mm and end_offset_mm")
        else:
            if has_depth or has_end:
                raise _InvalidToolArguments("through_all extent does not accept depth_mm or end_offset_mm")
            if profile_output != "solid":
                raise _InvalidToolArguments("through_all extent supports solid output only")
            start_mm = (cls._validated_finite_number(arguments["start_offset_mm"], "start_offset_mm")
                        if has_start else 0)

        request: dict[str, Any] = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
            "sketch_id": cls._validated_nonempty_string(arguments["sketch_id"], "sketch_id"),
            "mode": mode,
            "direction": direction,
            "extent": extent,
            "profile_output": profile_output,
            "start_offset_mm": start_mm,
        }
        if extent == "finite":
            request["end_offset_mm"] = end_mm
        if mode != "create":
            request["target_body_id"] = cls._validated_nonempty_string(arguments["target_body_id"], "target_body_id")
        return request

    @classmethod
    def _validate_prepare_sweep(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        required = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                    "path_curve_ids", "profile_output", "orientation_mode", "twist_angle_deg"}
        if set(arguments) != required:
            raise _InvalidToolArguments("nx_prepare_sweep received missing or unsupported fields")
        raw = arguments["path_curve_ids"]
        if not isinstance(raw, list) or not 1 <= len(raw) <= 32:
            raise _InvalidToolArguments("path_curve_ids must contain 1 to 32 curve IDs")
        ids = [cls._validated_bounded_nonempty_string(value, f"path_curve_ids[{index}]", 4096)
               for index, value in enumerate(raw)]
        if len(set(ids)) != len(ids):
            raise _InvalidToolArguments("path_curve_ids must be unique")
        profile = arguments["profile_output"]
        if not isinstance(profile, str) or profile not in {"solid", "sheet"}:
            raise _InvalidToolArguments("profile_output must be solid or sheet")
        orientation = arguments["orientation_mode"]
        if not isinstance(orientation, str) or orientation not in {"transport", "linear_twist"}:
            raise _InvalidToolArguments("orientation_mode must be transport or linear_twist")
        twist = cls._validated_finite_number(arguments["twist_angle_deg"], "twist_angle_deg")
        if not -90 <= twist <= 90:
            raise _InvalidToolArguments("twist_angle_deg must be in [-90, 90]")
        if orientation == "transport" and twist != 0:
            raise _InvalidToolArguments("transport orientation requires twist_angle_deg to be zero")
        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
            "sketch_id": cls._validated_bounded_nonempty_string(arguments["sketch_id"], "sketch_id", 4096),
            "path_curve_ids": ids, "profile_output": profile, "orientation_mode": orientation,
            "twist_angle_deg": twist,
        }

    @classmethod
    def _validate_prepare_revolve(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        required = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id", "mode",
            "axis_point_mm", "axis_direction", "start_angle_deg", "end_angle_deg",
        }
        allowed = set(required) | {"profile_output", "target_body_id"}
        if not required.issubset(arguments) or not set(arguments).issubset(allowed):
            raise _InvalidToolArguments("nx_prepare_revolve received missing or unsupported fields")

        mode = arguments["mode"]
        if not isinstance(mode, str) or mode not in {"create", "unite", "subtract"}:
            raise _InvalidToolArguments("mode must be create, unite, or subtract")
        profile_output = arguments.get("profile_output", "solid")
        if not isinstance(profile_output, str) or profile_output not in {"solid", "sheet"}:
            raise _InvalidToolArguments("profile_output must be solid or sheet")
        if profile_output == "sheet" and mode != "create":
            raise _InvalidToolArguments("sheet output is supported only for create revolve operations")
        if mode == "create" and "target_body_id" in arguments:
            raise _InvalidToolArguments("target_body_id is not allowed for create mode")
        if mode != "create" and "target_body_id" not in arguments:
            raise _InvalidToolArguments("target_body_id is required for unite and subtract")

        raw_point = arguments["axis_point_mm"]
        if not isinstance(raw_point, list) or len(raw_point) != 3:
            raise _InvalidToolArguments("axis_point_mm must contain exactly 3 finite coordinates")
        point = [cls._validated_finite_number(value, f"axis_point_mm[{index}]") for index, value in enumerate(raw_point)]
        if any(abs(value) > 1_000_000 for value in point):
            raise _InvalidToolArguments("axis_point_mm coordinates must be within ±1000000 mm")

        raw_direction = arguments["axis_direction"]
        if not isinstance(raw_direction, list) or len(raw_direction) != 3:
            raise _InvalidToolArguments("axis_direction must contain exactly 3 finite numbers")
        vector = [cls._validated_finite_number(value, f"axis_direction[{index}]") for index, value in enumerate(raw_direction)]
        scale = max(abs(value) for value in vector)
        if scale == 0:
            raise _InvalidToolArguments("axis_direction must be nonzero")
        scaled = [value / scale for value in vector]
        length = math.sqrt(sum(value * value for value in scaled))
        if not math.isfinite(length) or length <= 0:
            raise _InvalidToolArguments("axis_direction must be a finite nonzero world vector")
        direction = [value / length for value in scaled]

        start = cls._validated_finite_number(arguments["start_angle_deg"], "start_angle_deg")
        end = cls._validated_finite_number(arguments["end_angle_deg"], "end_angle_deg")
        if not -360 <= start <= 360 or not -360 <= end <= 360:
            raise _InvalidToolArguments("start_angle_deg and end_angle_deg must be in [-360, 360]")
        span = end - start
        if not math.isfinite(span) or abs(span) <= 1e-9 or abs(span) > 360:
            raise _InvalidToolArguments("signed angle span must be nonzero and have magnitude at most 360 degrees")

        request: dict[str, Any] = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_nonempty_string(arguments["revision_id"], "revision_id"),
            "sketch_id": cls._validated_bounded_nonempty_string(arguments["sketch_id"], "sketch_id", 4096),
            "mode": mode,
            "profile_output": profile_output,
            "axis_point_mm": point,
            "axis_direction": direction,
            "start_angle_deg": start,
            "end_angle_deg": end,
        }
        if mode != "create":
            request["target_body_id"] = cls._validated_bounded_nonempty_string(
                arguments["target_body_id"], "target_body_id", 4096,
            )
        return request

    @classmethod
    def _validate_prepare_spatial_curve(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        kind, mode = arguments.get("curve_type"), arguments.get("definition_mode")
        common = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "curve_type"}
        if kind == "line":
            fields = {"start_mm", "end_mm"}
        elif kind == "arc" and mode == "three_points":
            fields = {"definition_mode", "start_mm", "through_mm", "end_mm"}
        elif kind == "arc" and mode == "center":
            fields = {"definition_mode", "center_mm", "normal", "x_axis", "radius_mm", "start_angle_deg", "end_angle_deg"}
        elif kind == "circle":
            fields = {"center_mm", "normal", "x_axis", "radius_mm"}
        else:
            raise _InvalidToolArguments("unsupported spatial curve type or definition mode")
        if set(arguments) != common | fields:
            raise _InvalidToolArguments("spatial curve received missing, conflicting or unsupported fields")
        request = {"operation_id": cls._validated_operation_id(arguments["operation_id"]),
                   "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
                   "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
                   "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
                   "curve_type": kind}
        if "definition_mode" in fields:
            request["definition_mode"] = mode
        for key in fields & {"start_mm", "through_mm", "end_mm", "center_mm", "normal", "x_axis"}:
            request[key] = cls._validated_number_array(arguments[key], 3, key)
        for key in fields & {"radius_mm", "start_angle_deg", "end_angle_deg"}:
            request[key] = cls._validated_finite_number(arguments[key], key)
        for key in fields & {"start_mm", "through_mm", "end_mm", "center_mm"}:
            if any(abs(v) > 1e9 for v in request[key]):
                raise _InvalidToolArguments("coordinates must be within +/-1e9 mm")
        if "radius_mm" in fields:
            if not 1e-6 < request["radius_mm"] <= 1e9:
                raise _InvalidToolArguments("radius_mm must be greater than 1e-6 and at most 1e9 mm")
            for key in ("normal", "x_axis"):
                if abs(math.hypot(*request[key]) - 1) > 1e-9:
                    raise _InvalidToolArguments(f"{key} must be unit within 1e-9")
            if abs(sum(a*b for a,b in zip(request["normal"], request["x_axis"]))) > 1e-9:
                raise _InvalidToolArguments("normal and x_axis must be perpendicular within 1e-9")
        if "start_angle_deg" in fields:
            start, end = request["start_angle_deg"], request["end_angle_deg"]
            if abs(start) > 1e9 or abs(end) > 1e9 or not 1e-7 < abs(end-start) < 360:
                raise _InvalidToolArguments("angles must be within +/-1e9 degrees and sweep magnitude greater than 1e-7 and less than 360 degrees")
        if "start_mm" in fields:
            p, q = request["start_mm"], request["end_mm"]
            if math.dist(p, q) <= 1e-6:
                raise _InvalidToolArguments("curve endpoints must be more than 1e-6 mm apart")
            if "through_mm" in fields:
                m = request["through_mm"]
                a = [m[i]-p[i] for i in range(3)]
                b = [q[i]-p[i] for i in range(3)]
                cross = [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]
                if math.dist(m, p) <= 1e-6 or math.dist(m, q) <= 1e-6 or math.hypot(*cross) <= 1e-8 * math.hypot(*a) * math.hypot(*b):
                    raise _InvalidToolArguments("three points must be distinct and not nearly collinear")
        return request

    @classmethod
    def _validate_prepare_feature_mirror(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        common = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "seed_feature_id"}
        fields = set(arguments)
        if fields not in (common | {"plane_origin_mm", "plane_normal"}, common | {"datum_plane_id"}):
            raise _InvalidToolArguments("nx_prepare_feature_mirror received missing, conflicting or unsupported fields")
        request = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
            "seed_feature_id": cls._validated_bounded_nonempty_string(arguments["seed_feature_id"], "seed_feature_id", 4096),
        }
        if "datum_plane_id" in fields:
            request["datum_plane_id"] = cls._validated_bounded_nonempty_string(arguments["datum_plane_id"], "datum_plane_id", 4096)
        else:
            request["plane_origin_mm"] = cls._validated_number_array(arguments["plane_origin_mm"], 3, "plane_origin_mm")
            normal = cls._validated_number_array(arguments["plane_normal"], 3, "plane_normal")
            if abs(math.hypot(*normal) - 1.0) > 1e-9:
                raise _InvalidToolArguments("plane_normal must be a unit vector within 1e-9")
            request["plane_normal"] = normal
        return request

    @classmethod
    def _validate_prepare_feature_pattern(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        mode = arguments.get("mode")
        mode_fields = {
            "linear": {"direction1", "pitch1_mm"},
            "rectangular": {"direction1", "pitch1_mm", "direction2", "pitch2_mm", "count2"},
            "circular": {"axis_origin_mm", "axis_direction", "angle_deg"},
        }
        if not isinstance(mode, str) or mode not in mode_fields:
            raise _InvalidToolArguments("unsupported feature pattern mode")
        common = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "seed_feature_id", "mode", "count1"}
        if set(arguments) != common | mode_fields[mode]:
            raise _InvalidToolArguments("nx_prepare_feature_pattern received missing or unsupported fields")

        def count(field: str) -> int:
            value = arguments[field]
            if isinstance(value, bool) or not isinstance(value, int) or not 2 <= value <= 32:
                raise _InvalidToolArguments(f"{field} must be an integer from 2 through 32")
            return value

        def vector(field: str, unit: bool = False) -> list[int | float]:
            value = arguments[field]
            if not isinstance(value, list) or len(value) != 3:
                raise _InvalidToolArguments(f"{field} must contain exactly 3 finite numbers")
            result = [cls._validated_finite_number(item, f"{field}[{i}]") for i, item in enumerate(value)]
            if unit and abs(math.hypot(*result) - 1.0) > 1e-9:
                raise _InvalidToolArguments(f"{field} must be a unit vector within 1e-9")
            return result

        request = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
            "seed_feature_id": cls._validated_bounded_nonempty_string(arguments["seed_feature_id"], "seed_feature_id", 4096),
            "mode": mode, "count1": count("count1"),
        }
        if mode in ("linear", "rectangular"):
            request["direction1"] = vector("direction1", unit=True)
            request["pitch1_mm"] = cls._validated_positive_number(arguments["pitch1_mm"], "pitch1_mm")
        if mode == "rectangular":
            request["direction2"] = vector("direction2", unit=True)
            request["pitch2_mm"] = cls._validated_positive_number(arguments["pitch2_mm"], "pitch2_mm")
            request["count2"] = count("count2")
            if request["count1"] * request["count2"] > 32:
                raise _InvalidToolArguments("rectangular count product must be at most 32")
            if abs(sum(a * b for a, b in zip(request["direction1"], request["direction2"]))) > 1e-9:
                raise _InvalidToolArguments("rectangular directions must be orthogonal within 1e-9")
        elif mode == "circular":
            request["axis_origin_mm"] = vector("axis_origin_mm")
            request["axis_direction"] = vector("axis_direction", unit=True)
            angle = cls._validated_finite_number(arguments["angle_deg"], "angle_deg")
            if angle == 0 or abs(angle) * (request["count1"] - 1) >= 360:
                raise _InvalidToolArguments("angle_deg must be nonzero with total span strictly less than 360 degrees")
            request["angle_deg"] = angle
        return request

    @classmethod
    def _validate_prepare_chamfer(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        mode = arguments.get("mode")
        fields = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
                  "edge_ids", "mode", "reference_axis", "distance_mm"}
        if mode == "two_distances":
            fields.add("second_distance_mm")
        elif mode == "distance_angle":
            fields.add("angle_deg")
        elif mode != "equal_distance":
            raise _InvalidToolArguments("unsupported chamfer mode")
        if set(arguments) != fields:
            raise _InvalidToolArguments("nx_prepare_chamfer received missing or unsupported fields")
        raw = arguments["edge_ids"]
        if not isinstance(raw, list) or len(raw) != 1:
            raise _InvalidToolArguments("edge_ids must contain exactly one edge ID")
        axis = arguments["reference_axis"]
        if axis not in ("x", "y", "z"):
            raise _InvalidToolArguments("reference_axis must be x, y or z")
        request = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
            "target_body_id": cls._validated_bounded_nonempty_string(arguments["target_body_id"], "target_body_id", 4096),
            "edge_ids": [cls._validated_bounded_nonempty_string(raw[0], "edge_ids", 4096)],
            "mode": mode, "reference_axis": axis,
            "distance_mm": cls._validated_positive_number(arguments["distance_mm"], "distance_mm"),
        }
        if mode == "two_distances":
            request["second_distance_mm"] = cls._validated_positive_number(arguments["second_distance_mm"], "second_distance_mm")
        elif mode == "distance_angle":
            angle = cls._validated_positive_number(arguments["angle_deg"], "angle_deg")
            if angle >= 90:
                raise _InvalidToolArguments("angle_deg must be strictly less than 90")
            request["angle_deg"] = angle
        return request

    @classmethod
    def _validate_prepare_edge_blend(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        fields = {"operation_id", "ownership_id", "binding_epoch", "revision_id",
                  "target_body_id", "edge_ids", "radius_mm"}
        if set(arguments) != fields:
            raise _InvalidToolArguments("nx_prepare_edge_blend received missing or unsupported fields")
        raw = arguments["edge_ids"]
        if not isinstance(raw, list) or not 1 <= len(raw) <= 32:
            raise _InvalidToolArguments("edge_ids must contain 1 to 32 IDs")
        ids = [cls._validated_bounded_nonempty_string(value, "edge_ids", 4096) for value in raw]
        if len(set(ids)) != len(ids):
            raise _InvalidToolArguments("edge_ids must be unique")
        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
            "target_body_id": cls._validated_bounded_nonempty_string(arguments["target_body_id"], "target_body_id", 4096),
            "edge_ids": ids,
            "radius_mm": cls._validated_positive_number(arguments["radius_mm"], "radius_mm"),
        }

    @classmethod
    def _validate_prepare_boolean(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        fields = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "operation", "target_body_id", "tool_body_ids", "retain_tools"}
        if set(arguments) != fields:
            raise _InvalidToolArguments("nx_prepare_boolean received missing or unsupported fields")
        operation = arguments["operation"]
        if not isinstance(operation, str) or operation not in {"unite", "subtract", "intersect"}:
            raise _InvalidToolArguments("operation must be unite, subtract or intersect")
        target = cls._validated_bounded_nonempty_string(arguments["target_body_id"], "target_body_id", 4096)
        raw = arguments["tool_body_ids"]
        if not isinstance(raw, list) or not 1 <= len(raw) <= 32:
            raise _InvalidToolArguments("tool_body_ids must contain 1 to 32 IDs")
        ids = [cls._validated_bounded_nonempty_string(value, "tool_body_ids", 4096) for value in raw]
        if len(set(ids)) != len(ids) or target in ids:
            raise _InvalidToolArguments("tool body IDs must be unique and distinct from target")
        if not isinstance(arguments["retain_tools"], bool):
            raise _InvalidToolArguments("retain_tools must be a boolean")
        return {"operation_id": cls._validated_operation_id(arguments["operation_id"]),
                "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
                "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
                "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
                "operation": operation, "target_body_id": target, "tool_body_ids": ids, "retain_tools": arguments["retain_tools"]}

    @classmethod
    def _validate_prepare_hole(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        common = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
                  "position_mm", "direction", "kind", "depth_mode"}
        kind = arguments.get("kind")
        depth_mode = arguments.get("depth_mode")
        fields = {
            "simple": {"diameter_mm"},
            "counterbore": {"diameter_mm", "counterbore_diameter_mm", "counterbore_depth_mm"},
            "countersink": {"diameter_mm", "countersink_diameter_mm", "countersink_angle_deg"},
            "threaded": {"thread_standard", "thread_size", "radial_engage", "pitch_mm", "hand", "thread_depth_mm"},
        }
        if not isinstance(kind, str) or kind not in fields:
            raise _InvalidToolArguments("kind must be simple, counterbore, countersink, or threaded")
        if not isinstance(depth_mode, str) or depth_mode not in {"blind", "through"}:
            raise _InvalidToolArguments("depth_mode must be blind or through")
        expected = common | fields[kind] | ({"hole_depth_mm"} if depth_mode == "blind" else set())
        if set(arguments) != expected:
            raise _InvalidToolArguments("nx_prepare_hole received missing or unsupported fields for kind and depth_mode")

        vector = cls._validated_number_array(arguments["direction"], 3, "direction")
        scale = max(abs(value) for value in vector)
        if not scale:
            raise _InvalidToolArguments("direction must be nonzero")
        scaled = [value / scale for value in vector]
        length = math.sqrt(sum(value * value for value in scaled))
        request: dict[str, Any] = {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
            "target_body_id": cls._validated_bounded_nonempty_string(arguments["target_body_id"], "target_body_id", 4096),
            "position_mm": cls._validated_bounded_number_array(arguments["position_mm"], 3, "position_mm"),
            "direction": [value / length for value in scaled], "kind": kind, "depth_mode": depth_mode,
        }
        numeric = fields[kind] - {"thread_standard", "thread_size", "radial_engage", "hand"}
        if depth_mode == "blind":
            numeric = numeric | {"hole_depth_mm"}
        for field in numeric:
            request[field] = (cls._validated_positive_number(arguments[field], field)
                              if field == "countersink_angle_deg"
                              else cls._validated_bounded_positive_mm(arguments[field], field))
        if kind == "threaded":
            for field in ("thread_standard", "thread_size", "radial_engage"):
                request[field] = cls._validated_bounded_nonempty_string(arguments[field], field, 128)
            hand = arguments["hand"]
            if not isinstance(hand, str) or hand not in {"right", "left"}:
                raise _InvalidToolArguments("hand must be right or left")
            request["hand"] = hand
            if depth_mode == "blind" and request["thread_depth_mm"] > request["hole_depth_mm"]:
                raise _InvalidToolArguments("thread_depth_mm must not exceed hole_depth_mm")
        elif kind == "counterbore":
            if request["counterbore_diameter_mm"] <= request["diameter_mm"]:
                raise _InvalidToolArguments("counterbore_diameter_mm must exceed diameter_mm")
            if depth_mode == "blind" and request["counterbore_depth_mm"] >= request["hole_depth_mm"]:
                raise _InvalidToolArguments("counterbore_depth_mm must be less than hole_depth_mm")
        elif kind == "countersink":
            if request["countersink_diameter_mm"] <= request["diameter_mm"]:
                raise _InvalidToolArguments("countersink_diameter_mm must exceed diameter_mm")
            angle = request["countersink_angle_deg"]
            if angle >= 180:
                raise _InvalidToolArguments("countersink_angle_deg must be less than 180")
            # atan2 compares cone depth without a division by a nearly-zero tangent.
            if depth_mode == "blind" and math.atan2(
                request["countersink_diameter_mm"] / 2 - request["diameter_mm"] / 2,
                request["hole_depth_mm"],
            ) >= math.radians(angle) / 2:
                raise _InvalidToolArguments("countersink depth must be less than hole_depth_mm")
        return request

    @classmethod
    def _validate_prepare_thread(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        expected = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
            "axis", "axis_origin_mm", "start_mm", "length_mm", "handedness", "direction",
            "major_diameter_mm", "minor_diameter_mm", "pitch_mm",
        }
        if set(arguments) != expected:
            raise _InvalidToolArguments("nx_prepare_thread requires exactly its documented fields")

        axis = arguments["axis"]
        handedness = arguments["handedness"]
        direction = arguments["direction"]
        if not isinstance(axis, str) or axis not in {"X", "Y", "Z"}:
            raise _InvalidToolArguments("axis must be X, Y, or Z")
        if not isinstance(handedness, str) or handedness not in {"right", "left"}:
            raise _InvalidToolArguments("handedness must be right or left")
        if not isinstance(direction, str) or direction not in {"positive", "negative"}:
            raise _InvalidToolArguments("direction must be positive or negative")

        major_diameter = cls._validated_bounded_positive_mm(arguments["major_diameter_mm"], "major_diameter_mm")
        minor_diameter = cls._validated_bounded_positive_mm(arguments["minor_diameter_mm"], "minor_diameter_mm")
        pitch = cls._validated_bounded_positive_mm(arguments["pitch_mm"], "pitch_mm")
        if minor_diameter >= major_diameter:
            raise _InvalidToolArguments("minor_diameter_mm must be less than major_diameter_mm")
        length = cls._validated_bounded_positive_mm(arguments["length_mm"], "length_mm")
        if length > 500 * pitch:
            raise _InvalidToolArguments("length_mm / pitch_mm must not exceed 500")

        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
            "target_body_id": cls._validated_bounded_nonempty_string(arguments["target_body_id"], "target_body_id", 4096),
            "axis": axis,
            "axis_origin_mm": cls._validated_bounded_number_array(arguments["axis_origin_mm"], 3, "axis_origin_mm"),
            "start_mm": cls._validated_bounded_finite_mm(arguments["start_mm"], "start_mm"),
            "length_mm": length,
            "handedness": handedness,
            "direction": direction,
            "major_diameter_mm": major_diameter,
            "minor_diameter_mm": minor_diameter,
            "pitch_mm": pitch,
        }

    @classmethod
    def _validate_prepare_helical_groove(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        expected = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
            "axis", "axis_origin_mm", "start_mm", "length_mm", "handedness", "lead_mm",
            "root_diameter_mm", "reference_diameter_mm", "outer_diameter_mm", "space_width_mm",
            "flank_angle_deg", "phase_deg",
        }
        if set(arguments) != expected:
            raise _InvalidToolArguments("nx_prepare_helical_groove requires exactly its documented fields")

        axis = arguments["axis"]
        handedness = arguments["handedness"]
        if not isinstance(axis, str) or axis not in {"X", "Y", "Z"}:
            raise _InvalidToolArguments("axis must be X, Y, or Z")
        if not isinstance(handedness, str) or handedness not in {"right", "left"}:
            raise _InvalidToolArguments("handedness must be right or left")

        lead = cls._validated_bounded_positive_mm(arguments["lead_mm"], "lead_mm")
        length = cls._validated_bounded_positive_mm(arguments["length_mm"], "length_mm")
        if length > 50 * lead:
            raise _InvalidToolArguments("length_mm / lead_mm must not exceed 50")
        root_diameter = cls._validated_bounded_positive_mm(arguments["root_diameter_mm"], "root_diameter_mm")
        reference_diameter = cls._validated_bounded_positive_mm(
            arguments["reference_diameter_mm"], "reference_diameter_mm",
        )
        outer_diameter = cls._validated_bounded_positive_mm(arguments["outer_diameter_mm"], "outer_diameter_mm")
        if not root_diameter < reference_diameter < outer_diameter:
            raise _InvalidToolArguments(
                "diameters must satisfy root_diameter_mm < reference_diameter_mm < outer_diameter_mm"
            )
        space_width = cls._validated_bounded_positive_mm(arguments["space_width_mm"], "space_width_mm")
        flank_angle = cls._validated_positive_number(arguments["flank_angle_deg"], "flank_angle_deg")
        if flank_angle >= 45:
            raise _InvalidToolArguments("flank_angle_deg must be less than 45 degrees")
        phase = cls._validated_finite_number(arguments["phase_deg"], "phase_deg")
        if not -360 <= phase <= 360:
            raise _InvalidToolArguments("phase_deg must be between -360 and 360 degrees")

        half_width_at_reference = space_width / 2
        flank_slope = math.tan(math.radians(flank_angle))
        root_half_width = half_width_at_reference + (root_diameter - reference_diameter) / 2 * flank_slope
        outer_half_width = half_width_at_reference + (outer_diameter - reference_diameter) / 2 * flank_slope
        if not math.isfinite(root_half_width) or root_half_width <= 0:
            raise _InvalidToolArguments("groove half-width at the root diameter must be positive and finite")
        if not math.isfinite(outer_half_width) or 2 * outer_half_width >= lead:
            raise _InvalidToolArguments("groove width at outer_diameter_mm must be less than lead_mm")

        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_bounded_nonempty_string(arguments["ownership_id"], "ownership_id", 128),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "revision_id": cls._validated_bounded_nonempty_string(arguments["revision_id"], "revision_id", 128),
            "target_body_id": cls._validated_bounded_nonempty_string(arguments["target_body_id"], "target_body_id", 4096),
            "axis": axis,
            "axis_origin_mm": cls._validated_bounded_number_array(arguments["axis_origin_mm"], 3, "axis_origin_mm"),
            "start_mm": cls._validated_bounded_finite_mm(arguments["start_mm"], "start_mm"),
            "length_mm": length,
            "handedness": handedness,
            "lead_mm": lead,
            "root_diameter_mm": root_diameter,
            "reference_diameter_mm": reference_diameter,
            "outer_diameter_mm": outer_diameter,
            "space_width_mm": space_width,
            "flank_angle_deg": flank_angle,
            "phase_deg": phase,
        }

    @classmethod
    def _validated_geometry_id(cls, value: Any) -> str:
        geometry_id = cls._validated_nonempty_string(value, "geometry id")
        if len(geometry_id) > 128:
            raise _InvalidToolArguments("geometry id must be at most 128 characters")
        return geometry_id

    @classmethod
    def _validated_number_array(cls, value: Any, length: int, field: str) -> list[int | float]:
        if not isinstance(value, list) or len(value) != length:
            raise _InvalidToolArguments(f"{field} must be an array of exactly {length} finite numbers")
        return [cls._validated_finite_number(item, field) for item in value]

    @classmethod
    def _validated_bounded_number_array(cls, value: Any, length: int, field: str) -> list[int | float]:
        if not isinstance(value, list) or len(value) != length:
            raise _InvalidToolArguments(f"{field} must be an array of exactly {length} bounded finite numbers")
        return [cls._validated_bounded_finite_mm(item, field) for item in value]

    @classmethod
    def _validated_bounded_finite_mm(cls, value: Any, field: str) -> int | float:
        number = cls._validated_finite_number(value, field)
        if abs(number) > 1_000_000:
            raise _InvalidToolArguments(f"{field} absolute value must not exceed 1000000 mm")
        return number

    @classmethod
    def _validated_bounded_positive_mm(cls, value: Any, field: str) -> int | float:
        number = cls._validated_positive_number(value, field)
        if number > 1_000_000:
            raise _InvalidToolArguments(f"{field} must not exceed 1000000 mm")
        return number

    @classmethod
    def _validated_positive_number(cls, value: Any, field: str) -> int | float:
        number = cls._validated_finite_number(value, field)
        if number <= 0:
            raise _InvalidToolArguments(f"{field} must be a positive finite number")
        return number

    @staticmethod
    def _validated_finite_number(value: Any, field: str) -> int | float:
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise _InvalidToolArguments(f"{field} must be a finite JSON number")
        try:
            finite = math.isfinite(value)
        except (OverflowError, TypeError, ValueError):
            finite = False
        if not finite:
            raise _InvalidToolArguments(f"{field} must be a finite JSON number")
        return value

    @staticmethod
    def _validate_arc_angles(start_angle_deg: float, sweep_deg: float, field: str) -> None:
        if not 0.0 <= start_angle_deg < 360.0:
            raise _InvalidToolArguments(f"{field} start_angle_deg must be in [0, 360)")
        if abs(sweep_deg) <= 1e-9 or abs(sweep_deg) >= 360.0:
            raise _InvalidToolArguments(f"{field} sweep_deg must be nonzero and have magnitude less than 360 degrees")

    @classmethod
    def _validate_operation_execute(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "plan_id": cls._validated_nonempty_string(arguments["plan_id"], "plan_id"),
        }

    @classmethod
    def _validate_operation_verify(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "execute_operation_id": cls._validated_operation_id(arguments["execute_operation_id"]),
        }

    @classmethod
    def _validate_save_copy(cls, arguments: dict[str, Any]) -> dict[str, Any]:
        reopen = arguments["reopen"]
        if not isinstance(reopen, bool) or not reopen:
            raise _InvalidToolArguments("reopen must be true for NX save-copy round-trip verification")
        return {
            "operation_id": cls._validated_operation_id(arguments["operation_id"]),
            "ownership_id": cls._validated_nonempty_string(arguments["ownership_id"], "ownership_id"),
            "binding_epoch": cls._validated_positive_int(arguments["binding_epoch"], "binding_epoch"),
            "target_path": cls._validated_nonempty_string(arguments["target_path"], "target_path"),
            "reopen": True,
        }

    @staticmethod
    def _validated_nonempty_string(value: Any, field: str) -> str:
        if not isinstance(value, str) or not value.strip():
            raise _InvalidToolArguments(f"{field} must be a non-empty string")
        try:
            value.encode("utf-8", errors="strict")
        except UnicodeEncodeError as exc:
            raise _InvalidToolArguments(f"{field} must contain valid Unicode text") from exc
        return value

    @classmethod
    def _validated_bounded_nonempty_string(cls, value: Any, field: str, maximum: int) -> str:
        string = cls._validated_nonempty_string(value, field)
        if len(string) > maximum:
            raise _InvalidToolArguments(f"{field} must be at most {maximum} characters")
        return string

    @staticmethod
    def _validated_positive_int(value: Any, field: str) -> int:
        if isinstance(value, bool) or not isinstance(value, int) or value < 1:
            raise _InvalidToolArguments(f"{field} must be a positive integer")
        return value

    @staticmethod
    def _validated_decimal_string(value: Any) -> str:
        if not isinstance(value, str) or not 1 <= len(value) <= 128 or not _FINITE_DECIMAL.fullmatch(value):
            raise _InvalidToolArguments("value must be a finite decimal string of at most 128 characters")
        try:
            numeric = float(value)
        except (OverflowError, ValueError) as exc:
            raise _InvalidToolArguments("value must be a finite decimal string") from exc
        if not math.isfinite(numeric):
            raise _InvalidToolArguments("value must be a finite decimal string")
        return value

    @classmethod
    def _validated_unit_symbol(cls, value: Any) -> str:
        return cls._validated_nonempty_string(value, "unit")

    @classmethod
    def _sketch_corner_operation_status(
        cls,
        operation_id: str,
        operation: Any,
        *,
        bridge_failure: BridgeFailure | None = None,
        operation_label: str = "sketch corner",
    ) -> dict[str, Any]:
        record = operation if isinstance(operation, dict) else None
        nested_result = record.get("result") if record is not None else None
        nested_result = nested_result if isinstance(nested_result, dict) else None
        state = record.get("state") if record is not None else None
        verified_value = record.get("verified") if record is not None else None
        if verified_value is None and nested_result is not None:
            verified_value = nested_result.get("verified")
        verified = verified_value if isinstance(verified_value, bool) else None

        status: dict[str, Any] = {
            "operation_id": operation_id,
            "state": state if isinstance(state, str) else "UNKNOWN",
            "success": False,
            "verified": verified,
            "outcome_unknown": False,
        }
        if record is not None:
            status["operation"] = record
        evidence = record.get("evidence") if record is not None else None
        if evidence is None and nested_result is not None:
            evidence = nested_result.get("evidence")

        is_error = False
        if bridge_failure is not None:
            status.update({
                "state": "UNKNOWN",
                "outcome_unknown": True,
                "next_action": "nx_get_operation",
                "error": {"code": bridge_failure.code, "message": bridge_failure.message[:1000]},
            })
        elif status["state"] == "SUCCEEDED":
            status["success"] = verified is True
            if verified is not True:
                is_error = True
                status["error"] = {
                    "code": "OPERATION_UNVERIFIED",
                    "message": "The operation state is SUCCEEDED but verified is not true; the edit is not accepted as successful.",
                }
                status["evidence"] = evidence if evidence is not None else []
        elif status["state"] == "FAILED":
            is_error = True
            raw_error = record.get("error") if record is not None else None
            if isinstance(raw_error, dict):
                status["error"] = raw_error
            else:
                code = record.get("error_code") if record is not None else None
                message = record.get("error_message") if record is not None else None
                if isinstance(raw_error, str) and raw_error:
                    message = raw_error
                status["error"] = {
                    "code": code if isinstance(code, str) and code else "OPERATION_FAILED",
                    "message": message if isinstance(message, str) and message else f"The {operation_label} operation failed.",
                }
            status["evidence"] = evidence if evidence is not None else []
        elif status["state"] == "UNKNOWN":
            status["outcome_unknown"] = True
            status["next_action"] = "nx_get_operation"
            if evidence is not None:
                status["evidence"] = evidence
            if not isinstance(status.get("error"), dict):
                raw_error = record.get("error") if record is not None else None
                if isinstance(raw_error, dict):
                    status["error"] = raw_error
                else:
                    status["error"] = {
                        "code": "OPERATION_OUTCOME_UNKNOWN",
                        "message": "The operation outcome is unknown; query operation_id before any retry.",
                    }
        elif status["state"] in {"QUEUED", "RUNNING"}:
            status["next_action"] = "nx_get_operation"
        elif status["state"] == "CANCELLED":
            is_error = True
            status["error"] = {
                "code": "OPERATION_CANCELLED",
                "message": f"The {operation_label} operation was cancelled before verified completion.",
            }
            status["evidence"] = evidence if evidence is not None else []
        else:
            status.update({
                "state": "UNKNOWN",
                "outcome_unknown": True,
                "next_action": "nx_get_operation",
                "error": {
                    "code": "OPERATION_STATUS_INVALID",
                    "message": "The bridge returned an unrecognized operation state; query operation_id before any retry.",
                },
            })

        tool_response = cls._tool_success(status)
        tool_response["isError"] = is_error
        return tool_response

    @classmethod
    def _sketch_offset_operation_status(
        cls,
        operation_id: str,
        operation: Any,
        *,
        bridge_failure: BridgeFailure | None = None,
    ) -> dict[str, Any]:
        return cls._sketch_corner_operation_status(
            operation_id, operation, bridge_failure=bridge_failure, operation_label="sketch offset",
        )

    @classmethod
    def _sketch_pattern_operation_status(
        cls,
        operation_id: str,
        operation: Any,
        *,
        bridge_failure: BridgeFailure | None = None,
    ) -> dict[str, Any]:
        return cls._sketch_corner_operation_status(
            operation_id, operation, bridge_failure=bridge_failure, operation_label="sketch pattern",
        )

    @classmethod
    def _sketch_projection_operation_status(
        cls,
        operation_id: str,
        operation: Any,
        *,
        bridge_failure: BridgeFailure | None = None,
    ) -> dict[str, Any]:
        return cls._sketch_corner_operation_status(
            operation_id, operation, bridge_failure=bridge_failure, operation_label="sketch projection",
        )

    @classmethod
    def _sketch_reference_conversion_operation_status(
        cls,
        operation_id: str,
        operation: Any,
        *,
        bridge_failure: BridgeFailure | None = None,
    ) -> dict[str, Any]:
        return cls._sketch_corner_operation_status(
            operation_id, operation, bridge_failure=bridge_failure,
            operation_label="sketch reference conversion",
        )

    @staticmethod
    def _tool_success(value: dict[str, Any]) -> dict[str, Any]:
        text = json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
        return {
            "content": [{"type": "text", "text": text}],
            "structuredContent": value,
            "isError": False,
        }

    @staticmethod
    def _tool_error(code: str, message: str) -> dict[str, Any]:
        safe_message = message[:1000]
        text = f"{code}: {safe_message}"
        return {
            "content": [{"type": "text", "text": text}],
            "structuredContent": {"error": {"code": code, "message": safe_message}},
            "isError": True,
        }

    @staticmethod
    def _result(request_id: Any, value: dict[str, Any]) -> dict[str, Any]:
        return {"jsonrpc": "2.0", "id": request_id, "result": value}

    @staticmethod
    def _error(
        request_id: Any,
        code: int,
        message: str,
        data: Any | None = None,
    ) -> dict[str, Any]:
        error: dict[str, Any] = {"code": code, "message": message}
        if data is not None:
            error["data"] = data
        return {"jsonrpc": "2.0", "id": request_id, "error": error}


def _serve(config: BridgeConfig) -> int:
    server = McpServer(config)
    try:
        source = sys.stdin.buffer
        destination = sys.stdout.buffer
        while True:
            raw = source.readline(MAX_MCP_LINE_BYTES + 1)
            if not raw:
                return 0
            if len(raw) > MAX_MCP_LINE_BYTES:
                destination.write(dumps_line(server._error(None, -32700, "Parse error: message too large")))
                destination.flush()
                print("MCP input line exceeded the size limit", file=sys.stderr, flush=True)
                return 1
            if raw.endswith(b"\n"):
                raw = raw[:-1]
            if raw.endswith(b"\r"):
                raw = raw[:-1]
            try:
                message = loads_unique(raw.decode("utf-8", errors="strict"))
            except (ValueError, UnicodeError):
                response = server._error(None, -32700, "Parse error")
            else:
                try:
                    response = server.handle_message(message)
                except Exception as exc:
                    print(f"MCP request failed ({type(exc).__name__})", file=sys.stderr, flush=True)
                    if isinstance(message, dict) and "id" not in message:
                        response = None
                    else:
                        fallback_id = message.get("id") if isinstance(message, dict) else None
                        if not server._valid_id(fallback_id):
                            fallback_id = None
                        response = server._error(fallback_id, -32603, "Internal error")
            if response is not None:
                try:
                    destination.write(dumps_line(response))
                    destination.flush()
                except (BrokenPipeError, OSError):
                    return 0
    finally:
        server.close()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Local NX Sketch MCP stdio host")
    parser.add_argument("--config", required=True, help="absolute or working-directory-relative local JSON config")
    args = parser.parse_args(argv)
    try:
        config = load_config(args.config)
    except ConfigError as exc:
        print(f"configuration error: {exc}", file=sys.stderr, flush=True)
        return 2
    except Exception as exc:
        print(f"configuration error ({type(exc).__name__})", file=sys.stderr, flush=True)
        return 2
    return _serve(config)
