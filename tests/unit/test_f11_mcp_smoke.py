"""Offline helper tests for the installed F11 MCP smoke; no NX calls are made."""
from __future__ import annotations

import copy
import importlib.util
import json
from pathlib import Path
import sys
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))
SMOKE_PATH = PROJECT_ROOT / "tools" / "v2" / "f11-mcp-smoke.py"
SPEC = importlib.util.spec_from_file_location("f11_mcp_smoke", SMOKE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("could not load F11 MCP smoke helper module")
SMOKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE)


def fixture_document():
    curves = [
        {"role": "horizontal", "journal_id": "curve-h", "tag": "101", "kind": "LINE",
         "start_mm": [0, 0, 0], "end_mm": [10, 0, 0], "reference_status": "Active"},
        {"role": "vertical", "journal_id": "curve-v", "tag": "102", "kind": "LINE",
         "start_mm": [15, 20, 0], "end_mm": [15, 31, 0], "reference_status": "Active"},
        {"role": "witness", "journal_id": "curve-w", "tag": "103", "kind": "LINE",
         "start_mm": [40, 0, 0], "end_mm": [44, 0, 0], "reference_status": "Active"},
        {"role": "partial_arc", "journal_id": "curve-a", "tag": "104", "kind": "ARC",
         "center_mm": [25, 8, 0], "radius_mm": 3, "start_mm": [27.9, 8.7, 0],
         "end_mm": [22.1, 7.3, 0], "full_circle": False},
        {"role": "full_circle", "journal_id": "curve-c", "tag": "105", "kind": "ARC",
         "center_mm": [34, 8, 0], "radius_mm": 2, "start_mm": [36, 8, 0],
         "end_mm": [36, 8, 0], "full_circle": True},
    ]
    flags = {key: False for key in (
        "IsMeasurementExpression", "IsGeometricExpression", "IsNoEdit", "IsNoUpdate",
        "IsRightHandSideLockedFromEdit", "IsUserLocked", "IsUserSpecified",
    )}
    dimensions = [
        {"role": "horizontal", "journal_id": "dim-h", "tag": "201",
         "expression_tag": "expr-h", "expression_name": "F11_horizontal", "expression_rhs": "10",
         "expression_value": 10, "expression_unit": "mm", "expression_flags": flags,
         "computed_size_mm": 10, "geometry_refs": [
             {"geometry_journal_id": "curve-h", "association": "1", "association_value": 0},
             {"geometry_journal_id": "curve-h", "association": "2", "association_value": 0}],
         "constraint_type": "HorizontalDim", "dimension_state": "Driving"},
        {"role": "vertical", "journal_id": "dim-v", "tag": "202",
         "expression_tag": "expr-v", "expression_name": "F11_vertical", "expression_rhs": "8",
         "expression_value": 8, "expression_unit": "mm", "expression_flags": flags,
         "computed_size_mm": 11, "geometry_refs": [
             {"geometry_journal_id": "curve-v", "association": "1", "association_value": 0},
             {"geometry_journal_id": "curve-v", "association": "2", "association_value": 0}],
         "constraint_type": "VerticalDim", "dimension_state": "Reference"},
        {"role": "unsupported_radial", "journal_id": "dim-r", "tag": "203",
         "expression_tag": None, "expression_name": None, "expression_rhs": None,
         "expression_value": None, "expression_unit": None, "expression_flags": None,
         "computed_size_mm": 3, "geometry_refs": [
             {"geometry_journal_id": "curve-c", "association": "0", "association_value": 0}],
         "constraint_type": "RadiusDim", "dimension_state": "Reference"},
    ]
    return {
        "schema": SMOKE.FIXTURE_SCHEMA,
        "part_path": str(PROJECT_ROOT / "workspace" / "v2" / "models" / "fixture.prt"),
        "units": "Millimeters", "sketch_id": "sketch-1",
        "sketch_frame": {"origin_mm": [0, 0, 0], "normal": [0, 0, 1]},
        "curves": curves, "dimensions": dimensions,
        "fixture_conditions": {"vertical_dimension_state": "Reference",
                               "vertical_expression_value_mm": 8,
                               "vertical_computed_size_mm": 11,
                               "radial_dimension_type": "RadiusDim"},
    }


def inspected_sketch_for_fixture(fixture, *, radial_journal_id="reindexed-dim-radial", radial_operand_role="full_circle"):
    geometry_by_fixture_jid = {item["journal_id"]: item for item in fixture["curves"]}
    current_curve_jids = {item["role"]: "reindexed-curve-" + item["role"] for item in fixture["curves"]}
    geometry = []
    for item in fixture["curves"]:
        role = item["role"]
        if item["kind"] == "LINE":
            geometry.append({"journal_id": current_curve_jids[role], "object_type": "Line", "kind": "LINE",
                             "start": item["start_mm"], "end": item["end_mm"],
                             "reference_status": "Active"})
        else:
            geometry.append({"journal_id": current_curve_jids[role], "object_type": "Arc", "kind": "ARC",
                             "center": item["center_mm"], "radius_mm": item["radius_mm"],
                             "start": item["start_mm"], "end": item["end_mm"],
                             "start_angle_rad": 0.0 if item["full_circle"] else 0.25,
                             "end_angle_rad": 2 * 3.141592653589793 if item["full_circle"] else 2.1,
                             "reference_status": "Active"})
    dims = []
    association_names = {"0": "None", "1": "StartPoint", "2": "EndPoint"}
    for item in fixture["dimensions"]:
        role = item["role"]
        operands = []
        for reference in item["geometry_refs"]:
            source = geometry_by_fixture_jid[reference["geometry_journal_id"]]
            if role == "unsupported_radial" and source["role"] != radial_operand_role:
                source = next(curve for curve in fixture["curves"] if curve["role"] == radial_operand_role)
            operands.append(current_curve_jids[source["role"]] + ":" + association_names[reference["association"]])
        radial = role == "unsupported_radial"
        dims.append({
            "journal_id": radial_journal_id if radial else "reindexed-dim-" + role,
            "class": "DIMENSIONAL", "constraint_type": item["constraint_type"],
            "dimension_state": item["dimension_state"], "is_driving": item["dimension_state"] == "Driving",
            "expression_name": item.get("expression_name"),
            "expression_value": item.get("expression_value"), "value": item.get("expression_value"),
            "computed_size": item["computed_size_mm"], "expression_flags": item.get("expression_flags"),
            "geometry_refs": operands,
        })
    return {"journal_id": "reindexed-sketch", "origin": fixture["sketch_frame"]["origin_mm"],
            "normal": fixture["sketch_frame"]["normal"], "geometry": geometry, "constraints": dims}


def _transition_inventory():
    curve_roles = ("horizontal", "vertical", "witness", "partial_arc", "full_circle")
    geometry = {}
    ids = {}
    for index, role in enumerate(curve_roles):
        jid = "curve-jid-" + role
        ids[role] = jid
        geometry[role] = {"journal_id": jid, "kind": "LINE", "reference_status": "Active",
                          "reference_status_value": 1, "reference_curve_solver_semantics": "active_sketch_curve",
                          "start": [index, 0, 0], "end": [index + 1, 0, 0]}
    dimensions = {}
    for index, (role, state, value, measured, expression_name) in enumerate((
            ("horizontal", "Driving", "10", "10", "H_EXPR"),
            ("vertical", "Reference", "8", "11", "V_EXPR"),
            ("unsupported_radial", "Reference", None, "3", None))):
        jid = "dimension-jid-" + role
        ids["dimension:" + role] = jid
        dimensions[role] = {
            "journal_id": jid, "class": "DIMENSIONAL",
            "constraint_type": ("HorizontalDim" if role == "horizontal" else
                                 "VerticalDim" if role == "vertical" else "RadiusDim"),
            "dimension_state": state, "is_driving": state == "Driving",
            "solver_participation": "DRIVES_GEOMETRY" if state == "Driving" else "MEASUREMENT_ONLY",
            "expression_name": expression_name, "value": value, "expression_value": value,
            "computed_size": measured,
            "expression_flags": None if role == "unsupported_radial" else
                {"IsMeasurementExpression": False, "IsNoEdit": False},
            "geometry_refs": ([ids["full_circle"] + ":None"] if role == "unsupported_radial" else
                              [ids[role] + ":StartPoint"]),
        }
    return {"geometry": geometry, "dimensions": dimensions, "ids_by_role": ids}


class F11McpSmokeHelperTests(unittest.TestCase):
    def test_null_expression_radial_resolves_by_type_and_operands_not_null_name_or_old_jid(self):
        fixture = SMOKE.validate_fixture_document(fixture_document())
        sketch = inspected_sketch_for_fixture(fixture)
        snap = SMOKE._snapshot_sketch(sketch, fixture, "offline null-expression radial")
        radial = snap["dimensions"]["unsupported_radial"]
        self.assertEqual(radial["constraint_type"], "RadiusDim")
        self.assertIsNone(radial.get("expression_name"))
        self.assertEqual(radial["journal_id"], "reindexed-dim-radial")
        self.assertNotEqual(radial["journal_id"], fixture["dimensions"][2]["journal_id"])

        wrong_operand = inspected_sketch_for_fixture(fixture, radial_operand_role="witness")
        with self.assertRaisesRegex(AssertionError, "type/operands/expression"):
            SMOKE._snapshot_sketch(wrong_operand, fixture, "wrong radial operand")

    def test_idle_flight_accepts_integer_or_string_zero_but_rejects_bool_busy_and_ambiguous(self):
        self.assertTrue(SMOKE._in_flight_is_idle(0))
        self.assertTrue(SMOKE._in_flight_is_idle("0"))
        for value in (False, True, 1, "1", "", "zero", None, 0.0, [], {}):
            with self.subTest(value=value):
                self.assertFalse(SMOKE._in_flight_is_idle(value))

    def test_installed_schema_check_reads_exact_f11_tools(self):
        from nx_sketch_mcp.server import TOOLS

        checked = SMOKE._check_reference_conversion_schemas(TOOLS)
        self.assertEqual(checked["checked_tools"], [
            "nx_prepare_sketch_reference_conversion", "nx_edit_sketch_reference_conversion",
        ])
        names = [tool["name"] for tool in TOOLS]
        self.assertEqual(len(names), len(set(names)))
        self.assertLessEqual(SMOKE.EXPECTED_TOOLS, set(names))
        malformed = copy.deepcopy(TOOLS)
        edit = next(item for item in malformed if item["name"] == "nx_edit_sketch_reference_conversion")
        edit["inputSchema"]["properties"]["object_ids"]["maxItems"] = 255
        with self.assertRaisesRegex(AssertionError, "1-256 unique"):
            SMOKE._check_reference_conversion_schemas(malformed)

    def test_capability_check_requires_frozen_request_and_dimension_contract(self):
        feature = {
            "prepare_operation": "operation.prepare_sketch_reference_conversion",
            "edit_operation": "operation.edit_sketch_reference_conversion",
            "request_fields": ["operation_id", "ownership_id", "binding_epoch", "revision_id",
                               "sketch_id", "object_ids", "target_state"],
            "selection": "1 through 256 unique current Journal IDs from one Sketch; curves from geometry[] and dimensions from constraints[]",
            "supported_dimension_types": ["HorizontalDim", "VerticalDim"],
            "dimension_unit": "mm; unsupported dimensions are rejected until their native unit mapping is verified",
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
            "dimension_semantics": "Reference dimensions do not drive; ComputedSize can differ from expression RHS/value.",
            "mixed_state_selection": "Already-target objects are no-ops; other selected objects convert.",
        }
        result = SMOKE._check_reference_conversion_capability({
            "model_native_geometry_support": {"sketch_reference_conversion": feature},
        })
        self.assertEqual(result["supported_dimension_types"], ["HorizontalDim", "VerticalDim"])
        feature["request_fields"] = feature["request_fields"][:-1]
        with self.assertRaisesRegex(AssertionError, "request_fields"):
            SMOKE._check_reference_conversion_capability({
                "model_native_geometry_support": {"sketch_reference_conversion": feature},
            })

    def test_fixture_contract_requires_millimetre_stale_reference_dimension(self):
        fixture = SMOKE.validate_fixture_document(fixture_document())
        self.assertEqual(fixture["schema"], SMOKE.FIXTURE_SCHEMA)
        bad = fixture_document()
        bad["fixture_conditions"]["vertical_computed_size_mm"] = 8
        with self.assertRaisesRegex(ValueError, "ComputedSize 11 mm"):
            SMOKE.validate_fixture_document(bad)

    def test_numeric_rhs_parser_accepts_mm_and_rejects_boolean_or_nonfinite(self):
        self.assertEqual(SMOKE._numeric_text("11.25 mm", "rhs"), 11.25)
        with self.assertRaises(AssertionError):
            SMOKE._numeric_text(True, "rhs")
        with self.assertRaises(AssertionError):
            SMOKE._numeric_text("NaN mm", "rhs")

    def test_transition_allows_only_selected_state_change_and_computed_size_sync(self):
        before = _transition_inventory()
        after = copy.deepcopy(before)
        dim = after["dimensions"]["vertical"]
        dim.update(dimension_state="Driving", is_driving=True, solver_participation="DRIVES_GEOMETRY",
                   value="11", expression_value="11")
        result = SMOKE.verify_object_transition(before, after, ["dimension:vertical"], "active",
                                                sync_dimension_roles=["vertical"])
        self.assertTrue(result["passed"])
        self.assertTrue(next(row for row in result["objects"] if row["role"] == "vertical" and row["kind"] == "dimension")
                        ["expression_rhs_sync_to_prestate_computed_size"])
        altered = copy.deepcopy(after)
        altered["dimensions"]["vertical"]["expression_flags"]["IsNoEdit"] = True
        with self.assertRaisesRegex(AssertionError, "non-state evidence changed"):
            SMOKE.verify_object_transition(before, altered, ["dimension:vertical"], "active",
                                           sync_dimension_roles=["vertical"])

    def test_transition_rejects_unselected_geometry_changes(self):
        before = _transition_inventory()
        after = copy.deepcopy(before)
        after["geometry"]["witness"]["end"] = [99, 0, 0]
        with self.assertRaisesRegex(AssertionError, "curve witness geometry"):
            SMOKE.verify_object_transition(before, after, [], "reference")

    def test_operation_sync_evidence_requires_rhs_value_and_expression_identity(self):
        operation_result = {
            "objects": [{"kind": "dimension", "tag": "202", "expression_name": "V_EXPR",
                         "computed_size_before": 11, "expression_tag": "exp-1", "expression_unit": "mm",
                         "expression_right_hand_side_before": "8", "expression_value_before": 8}],
            "objects_after": [{"kind": "dimension", "tag": "202", "expression_name": "V_EXPR",
                               "computed_size_after": 11, "expression_tag": "exp-1", "expression_unit": "mm",
                               "expression_right_hand_side_after": "11", "expression_value_after": 11}],
        }
        evidence = SMOKE.verify_operation_sync_evidence(operation_result, "V_EXPR", 11)
        self.assertTrue(evidence["passed"])
        operation_result["objects_after"][0]["expression_right_hand_side_after"] = "12"
        with self.assertRaisesRegex(AssertionError, "expression RHS"):
            SMOKE.verify_operation_sync_evidence(operation_result, "V_EXPR", 11)

    def test_nested_redaction_and_same_id_operation_helpers(self):
        secret = {"ownership_id": "owner-secret", "nested": {"text": json.dumps({"token": "token-secret", "kept": 1})}}
        serialized = json.dumps(SMOKE._redact(secret))
        self.assertNotIn("owner-secret", serialized)
        self.assertNotIn("token-secret", serialized)
        self.assertEqual(SMOKE._operation_state({"operation": {"state": "UNKNOWN"}}), "UNKNOWN")
        self.assertEqual(SMOKE._operation_id({"operation_id": "f11-op"}), "f11-op")


if __name__ == "__main__":
    unittest.main()
