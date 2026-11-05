"""Focused Python protocol checks for F09 sketch pattern and mirror tools."""

from __future__ import annotations

import unittest
from pathlib import Path
import sys
from unittest.mock import Mock

PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class F09SketchPatternProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={"plan_id": "pattern-plan-1", "kind": "edit_sketch_pattern"})
        self.server._bridge.call = self.bridge_call

    @staticmethod
    def common(mode: str = "linear") -> dict:
        request = {
            "operation_id": "f09-pattern-1",
            "ownership_id": "owner-1",
            "binding_epoch": 12,
            "revision_id": "rev-20",
            "sketch_id": "sketch-1",
            "source_curve_ids": ["line-1"],
            "mode": mode,
        }
        if mode == "linear":
            request.update({"direction_xy": [1.0, 0.0], "spacing_mm": 5.0, "instance_count": 3})
        elif mode == "rectangular":
            request.update({
                "direction_x_xy": [1.0, 0.0], "direction_y_xy": [0.0, 1.0],
                "spacing_x_mm": 5.0, "spacing_y_mm": 4.0,
                "count_x": 2, "count_y": 3,
            })
        elif mode == "circular":
            request.update({"center_xy": [2.0, -1.0], "angle_step_deg": -45.0, "instance_count": 5})
        elif mode == "mirror":
            request.update({"axis_curve_id": "axis-line-1"})
        else:
            raise ValueError(mode)
        return request

    def call(self, name: str, arguments: dict):
        return self.server._tools_call(1, {"name": name, "arguments": arguments})

    def assert_input_error(self, name: str, arguments: dict):
        response = self.call(name, arguments)
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_tools_schema_and_capabilities_publish_the_complete_f09_surface(self):
        tools = {item["name"]: item for item in TOOLS}
        self.assertEqual(len(TOOLS), len(tools))
        for name in ("nx_prepare_sketch_pattern", "nx_edit_sketch_pattern"):
            self.assertIn(name, tools)
            schema = tools[name]["inputSchema"]
            required = set(schema["required"])
            self.assertTrue({
                "operation_id", "ownership_id", "binding_epoch", "revision_id",
                "sketch_id", "source_curve_ids", "mode",
            } <= required, schema)
            self.assertFalse(schema["additionalProperties"], schema)
            self.assertEqual(schema["properties"]["mode"]["enum"],
                             ["linear", "rectangular", "circular", "mirror"])
            self.assertEqual(schema["properties"]["source_curve_ids"]["minItems"], 1)
            self.assertTrue(schema["properties"]["source_curve_ids"].get("uniqueItems"), schema)
        self.assertIs(tools["nx_prepare_sketch_pattern"]["annotations"]["readOnlyHint"], True)
        self.assertIs(tools["nx_edit_sketch_pattern"]["annotations"]["destructiveHint"], True)

        capabilities = self.server._capabilities()["model_native_geometry_support"]["sketch_pattern"]
        self.assertEqual(capabilities["prepare_operation"], "operation.prepare_sketch_pattern")
        self.assertEqual(capabilities["edit_operation"], "operation.edit_sketch_pattern")
        self.assertEqual(capabilities["modes"], ["linear", "rectangular", "circular", "mirror"])
        self.assertIn("LINE", capabilities["supported_curve_types"])
        self.assertIn("ARC", capabilities["supported_curve_types"])
        self.assertNotIn("ELLIPSE", capabilities["supported_curve_types"])
        self.assertNotIn("SPLINE", capabilities["supported_curve_types"])

    def test_prepare_forwards_each_mode_without_changing_the_request(self):
        for mode in ("linear", "rectangular", "circular", "mirror"):
            with self.subTest(mode=mode):
                self.bridge_call.reset_mock()
                arguments = self.common(mode)
                response = self.call("nx_prepare_sketch_pattern", arguments)
                self.assertFalse(response["result"]["isError"], response)
                self.bridge_call.assert_called_once_with("operation.prepare_sketch_pattern", arguments)

    def test_combined_edit_formats_verified_failure_and_unknown_write_gate(self):
        arguments = self.common("rectangular")
        self.bridge_call.return_value = {
            "state": "SUCCEEDED", "kind": "execute_edit_sketch_pattern",
            "result": {"combined_edit": True, "verified": True, "mutation_state": "APPLIED"},
        }
        succeeded_response = self.call("nx_edit_sketch_pattern", arguments)["result"]
        self.bridge_call.assert_called_once_with("operation.edit_sketch_pattern", arguments)
        succeeded = succeeded_response["structuredContent"]
        self.assertIs(succeeded_response.get("isError"), False, succeeded_response)
        self.assertEqual(succeeded.get("state"), "SUCCEEDED", succeeded)
        self.assertIs(succeeded.get("success"), True, succeeded)
        self.assertIs(succeeded.get("verified"), True, succeeded)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "state": "FAILED", "kind": "execute_edit_sketch_pattern",
            "result": {"verified": False, "mutation_state": "ROLLED_BACK", "evidence": {"checks": []}},
            "error": {"code": "VERIFY_MISMATCH", "message": "native pattern readback mismatch"},
        }
        failed_response = self.call("nx_edit_sketch_pattern", arguments)["result"]
        self.assertIs(failed_response.get("isError"), True, failed_response)
        failed = failed_response["structuredContent"]
        self.assertEqual(failed.get("error", {}).get("code"), "VERIFY_MISMATCH", failed)
        self.assertIs(failed.get("outcome_unknown"), False, failed)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "state": "UNKNOWN", "kind": "execute_edit_sketch_pattern",
            "result": {"combined_edit": True, "verified": False, "mutation_state": "UNKNOWN"},
            "error": {"code": "OPERATION_EXECUTION_TIMEOUT", "message": "timed out"},
        }
        unknown_response = self.call("nx_edit_sketch_pattern", arguments)["result"]
        unknown = unknown_response["structuredContent"]
        self.assertTrue(unknown.get("outcome_unknown"), unknown)
        self.assertEqual(unknown.get("next_action"), "nx_get_operation", unknown)
        self.assertIn(arguments["operation_id"], self.server._unknown_operation_ids)

        self.bridge_call.reset_mock()
        blocked = self.call("nx_edit_sketch_pattern", {
            **arguments, "operation_id": "f09-next-write",
        })["result"]
        blocked_value = blocked["structuredContent"]
        self.assertEqual(blocked_value.get("error", {}).get("code"), "CLIENT_OUTCOME_UNKNOWN", blocked_value)
        self.bridge_call.assert_not_called()

    def test_mode_specific_bounds_and_mirror_axis_are_checked_before_bridge(self):
        invalid = [
            {**self.common("linear"), "direction_xy": [0.0, 0.0]},
            {**self.common("linear"), "spacing_mm": 0.0},
            {**self.common("linear"), "spacing_mm": -1.0},
            {**self.common("linear"), "spacing_mm": float("inf")},
            {**self.common("linear"), "instance_count": 1},
            {**self.common("linear"), "instance_count": 257},
            {**self.common("rectangular"), "direction_y_xy": [1.0, 0.0]},
            {**self.common("rectangular"), "count_x": 1},
            {**self.common("rectangular"), "count_x": 129, "count_y": 2},
            {**self.common("circular"), "angle_step_deg": 0.0},
            {**self.common("circular"), "angle_step_deg": 360.0, "instance_count": 2},
            {**self.common("circular"), "angle_step_deg": 450.0},
            {**self.common("circular"), "angle_step_deg": -450.0},
            {**self.common("circular"), "angle_step_deg": 1.0e308},
            {**self.common("circular"), "instance_count": 1},
            {**self.common("mirror"), "axis_curve_id": "line-1"},
            {**self.common("mirror"), "axis_curve_id": ""},
            {**self.common("linear"), "mode": "pattern"},
            {**self.common("linear"), "source_curve_ids": ["line-1", "line-1"]},
            {**self.common("linear"), "unexpected": True},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_input_error("nx_prepare_sketch_pattern", arguments)


if __name__ == "__main__":
    unittest.main()
