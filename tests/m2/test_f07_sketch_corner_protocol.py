"""Focused protocol checks for F07 sketch corner edits."""

from __future__ import annotations

from pathlib import Path
import sys
import unittest
from unittest.mock import Mock


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class F07SketchCornerProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={"plan_id": "corner-plan-1", "kind": "edit_sketch_corner"})
        self.server._bridge.call = self.bridge_call

    @staticmethod
    def common(kind: str = "fillet") -> dict:
        return {
            "operation_id": "f07-corner-1",
            "ownership_id": "owner-1",
            "binding_epoch": 9,
            "revision_id": "rev-10",
            "sketch_id": "sketch-1",
            "kind": kind,
            "curve1_id": "line-1",
            "curve2_id": "line-2",
            "curve1_end": "end",
            "curve2_end": "start",
        }

    def call(self, name: str, arguments: dict):
        return self.server._tools_call(1, {"name": name, "arguments": arguments})

    def assert_input_error(self, name: str, arguments: dict):
        response = self.call(name, arguments)
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_two_tools_are_discoverable_and_schema_states_explicit_corner_semantics(self):
        tools = {item["name"]: item for item in TOOLS}
        self.assertEqual(len(TOOLS), len(tools))
        for name in ("nx_prepare_sketch_corner", "nx_edit_sketch_corner"):
            self.assertIn(name, tools)
            schema = tools[name]["inputSchema"]
            for field in (
                "sketch_id", "kind", "curve1_id", "curve2_id", "curve1_end", "curve2_end",
            ):
                self.assertIn(field, schema["properties"])
            self.assertEqual(schema["properties"]["kind"]["enum"], ["fillet", "chamfer"])
            self.assertEqual(schema["properties"]["curve1_end"]["enum"], ["start", "end"])
            self.assertEqual(schema["properties"]["curve2_end"]["enum"], ["start", "end"])
            self.assertIn("A→C", tools[name]["description"])
        caps = self.server._capabilities()["model_native_geometry_support"]["sketch_corner"]
        self.assertEqual(caps["fillet_curve_types"], ["LINE", "ARC"])
        self.assertEqual(caps["fillet_curve_pairs"], [["LINE", "LINE"], ["LINE", "ARC"], ["ARC", "ARC"]])
        self.assertEqual(caps["chamfer_curve_types"], ["LINE"])
        self.assertEqual(caps["chamfer_curve_pairs"], [["LINE", "LINE"]])
        self.assertEqual(caps["distance_angle_definition"]["vertex_angle"], "angle(A→C, A→B)")
        self.assertIn("Asymmetric", caps["distance_angle_definition"]["native_mapping"])
        self.assertEqual(caps["whole_profile_validity"], "NOT_CLAIMED; F07 verifies only local corner geometry and its sketch relationships")
        self.assertIn("curve IDs from nx_inspect_sketch", caps["selection"])
        self.assertEqual(caps["fillet_radius_unit"], "mm")

    def test_prepare_fillet_forwards_explicit_curve_ids_and_distinct_endpoints(self):
        arguments = {**self.common(), "radius_mm": 2.0}
        response = self.call("nx_prepare_sketch_corner", arguments)
        self.assertFalse(response["result"]["isError"], response)
        self.bridge_call.assert_called_once_with("operation.prepare_sketch_corner", arguments)

    def test_prepare_chamfer_two_distances_and_angle_are_mutually_exclusive(self):
        two_distance = {
            **self.common("chamfer"), "distance1_mm": 4.0, "distance2_mm": 2.0,
        }
        response = self.call("nx_prepare_sketch_corner", two_distance)
        self.assertFalse(response["result"]["isError"], response)
        self.bridge_call.assert_called_once_with("operation.prepare_sketch_corner", two_distance)

        self.bridge_call.reset_mock()
        angle = {
            **self.common("chamfer"), "distance1_mm": 4.0, "angle_deg": 30.0,
        }
        response = self.call("nx_prepare_sketch_corner", angle)
        self.assertFalse(response["result"]["isError"], response)
        self.bridge_call.assert_called_once_with("operation.prepare_sketch_corner", angle)

        self.bridge_call.reset_mock()
        equal = {
            **self.common("chamfer"), "distance1_mm": 3.0, "distance2_mm": 3.0,
        }
        response = self.call("nx_prepare_sketch_corner", equal)
        self.assertFalse(response["result"]["isError"], response)
        self.bridge_call.assert_called_once_with("operation.prepare_sketch_corner", equal)

    def test_combined_edit_routes_one_idempotent_request_to_serial_prepare_execute_verify(self):
        arguments = {**self.common(), "radius_mm": 2.0}
        response = self.call("nx_edit_sketch_corner", arguments)
        self.assertFalse(response["result"]["isError"], response)
        self.bridge_call.assert_called_once_with("operation.edit_sketch_corner", arguments)

    def test_compound_status_accepts_only_verified_success_and_preserves_unknown_gate(self):
        arguments = {**self.common(), "radius_mm": 2.0}
        self.bridge_call.return_value = {
            "state": "SUCCEEDED", "kind": "execute_edit_sketch_corner",
            "result": {"combined_edit": True, "verified": True, "mutation_state": "APPLIED"},
        }
        succeeded_response = self.call("nx_edit_sketch_corner", arguments)["result"]
        succeeded = succeeded_response["structuredContent"]
        self.assertIs(succeeded_response.get("isError"), False, succeeded_response)
        self.assertEqual(succeeded.get("state"), "SUCCEEDED", succeeded)
        self.assertIs(succeeded.get("success"), True, succeeded)
        self.assertIs(succeeded.get("verified"), True, succeeded)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "state": "FAILED", "kind": "execute_edit_sketch_corner",
            "result": {"verified": False, "mutation_state": "APPLIED", "evidence": {"checks": []}},
            "error": {"code": "VERIFY_MISMATCH", "message": "native readback mismatch"},
        }
        failed_response = self.call("nx_edit_sketch_corner", arguments)["result"]
        self.assertIs(failed_response.get("isError"), True, failed_response)
        failed = failed_response["structuredContent"]
        self.assertEqual(failed.get("error", {}).get("code"), "VERIFY_MISMATCH", failed)
        self.assertIs(failed.get("outcome_unknown"), False, failed)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "state": "UNKNOWN", "kind": "execute_edit_sketch_corner",
            "result": {"combined_edit": True, "verified": False, "mutation_state": "UNKNOWN"},
            "error": {"code": "OPERATION_EXECUTION_TIMEOUT", "message": "timed out"},
        }
        unknown_response = self.call("nx_edit_sketch_corner", arguments)["result"]
        unknown = unknown_response["structuredContent"]
        self.assertTrue(unknown.get("outcome_unknown"), unknown)
        self.assertEqual(unknown.get("next_action"), "nx_get_operation", unknown)
        self.assertIn(arguments["operation_id"], self.server._unknown_operation_ids)

        self.bridge_call.reset_mock()
        blocked_response = self.call("nx_edit_sketch_corner", {**arguments, "operation_id": "f07-corner-next"})["result"]
        self.assertIs(blocked_response.get("isError"), True, blocked_response)
        blocked = blocked_response["structuredContent"]
        self.assertEqual(blocked.get("error", {}).get("code"), "CLIENT_OUTCOME_UNKNOWN", blocked)
        self.bridge_call.assert_not_called()

    def test_get_operation_only_formats_verified_combined_execute_as_corner_success(self):
        self.bridge_call.return_value = {
            "state": "SUCCEEDED", "kind": "execute_edit_sketch_corner",
            "result": {"combined_edit": True, "verified": True, "mutation_state": "APPLIED"},
        }
        accepted_response = self.call("nx_get_operation", {"operation_id": "f07-poll-combined"})["result"]
        accepted = accepted_response["structuredContent"]
        self.assertIs(accepted.get("success"), True, accepted)
        self.assertIs(accepted.get("verified"), True, accepted)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "ok": True, "operation_id": "f07-poll-unknown", "kind": "execute_edit_sketch_corner",
            "state": "UNKNOWN",
            "result": {"combined_edit": True, "verified": False, "mutation_state": "UNKNOWN"},
            "error": {"code": "OPERATION_EXECUTION_TIMEOUT", "message": "outcome unresolved"},
            "writes_halted": True,
        }
        unknown_response = self.call("nx_get_operation", {"operation_id": "f07-poll-unknown"})["result"]
        unknown = unknown_response["structuredContent"]
        self.assertIs(unknown_response.get("isError"), False, unknown_response)
        self.assertEqual(unknown.get("state"), "UNKNOWN", unknown)
        self.assertIs(unknown.get("outcome_unknown"), True, unknown)
        self.assertEqual(unknown.get("next_action"), "nx_get_operation", unknown)
        self.assertEqual(unknown.get("operation", {}).get("writes_halted"), True, unknown)
        self.assertIn("f07-poll-unknown", self.server._unknown_operation_ids)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "state": "SUCCEEDED", "kind": "execute_edit_sketch_corner",
            "result": {"verified": True, "mutation_state": "APPLIED"},
        }
        preview_execute_response = self.call("nx_get_operation", {"operation_id": "f07-preview-execute"})["result"]
        preview_execute = preview_execute_response["structuredContent"]
        self.assertEqual(preview_execute.get("state"), "SUCCEEDED", preview_execute)
        self.assertNotIsInstance(preview_execute.get("success"), bool, preview_execute)

    def test_mode_specific_arguments_and_ranges_are_enforced_before_bridge_call(self):
        invalid = [
            {**self.common(), "radius_mm": 0.0},
            {**self.common(), "radius_mm": -1.0},
            {**self.common(), "radius_mm": float("inf")},
            {**self.common(), "radius_mm": 2.0, "distance1_mm": 3.0},
            {**self.common(), "radius_mm": 2.0, "unused": True},
            {**self.common(), "radius_mm": 2.0, "curve1_end": "middle"},
            {**self.common(), "radius_mm": 2.0, "curve1_id": ""},
            {**self.common("chamfer"), "distance1_mm": 3.0},
            {**self.common("chamfer"), "distance1_mm": 3.0, "distance2_mm": 0.0},
            {**self.common("chamfer"), "distance1_mm": 3.0, "angle_deg": 0.0},
            {**self.common("chamfer"), "distance1_mm": 3.0, "angle_deg": 180.0},
            {**self.common("chamfer"), "distance1_mm": 3.0, "distance2_mm": 2.0, "angle_deg": 30.0},
            {**self.common("chamfer"), "distance1_mm": 3.0, "radius_mm": 1.0, "distance2_mm": 2.0},
            {**self.common("other"), "radius_mm": 2.0},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_input_error("nx_prepare_sketch_corner", arguments)

    def test_stale_identity_fields_and_endpoint_names_are_not_silently_normalized(self):
        invalid = [
            {**self.common(), "curve1_end": "END"},
            {**self.common(), "curve2_end": "first"},
            {**self.common(), "revision_id": ""},
            {**self.common(), "sketch_id": ""},
            {**self.common(), "curve2_id": "line-1"},
            {**self.common(), "binding_epoch": 0},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_input_error("nx_prepare_sketch_corner", {**arguments, "radius_mm": 2.0})


if __name__ == "__main__":
    unittest.main()
