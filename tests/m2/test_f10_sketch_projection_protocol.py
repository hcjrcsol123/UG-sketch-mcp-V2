"""Focused STDIO schema and request checks for F10 sketch projection."""
from __future__ import annotations

import math
import unittest
from pathlib import Path
from unittest.mock import Mock
import sys


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class F10SketchProjectionProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={
            "plan_id": "projection-plan-1", "kind": "edit_sketch_projection",
        })
        self.server._bridge.call = self.bridge_call

    @staticmethod
    def request(**overrides):
        value = {
            "operation_id": "f10-projection-1",
            "ownership_id": "owner-1",
            "binding_epoch": 9,
            "revision_id": "rev-10",
            "sketch_id": "sketch-tilted-1",
            "source_curve_ids": ["edge-1", "space-curve-1"],
            "direction_xyz": [1.0, 2.0, 3.0],
            "associative": True,
        }
        value.update(overrides)
        return value

    def call(self, name, arguments):
        return self.server._tools_call(1, {"name": name, "arguments": arguments})

    def assert_input_error(self, name, arguments):
        response = self.call(name, arguments)
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_schema_and_capabilities_publish_the_exact_f10_surface(self):
        by_name = {item["name"]: item for item in TOOLS}
        self.assertEqual(len(TOOLS), len(by_name))
        prepare = by_name["nx_prepare_sketch_projection"]
        edit = by_name["nx_edit_sketch_projection"]
        required = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "source_curve_ids", "direction_xyz", "associative",
        }
        for tool in (prepare, edit):
            schema = tool["inputSchema"]
            self.assertEqual(set(schema["required"]), required, schema)
            self.assertFalse(schema["additionalProperties"], schema)
            self.assertEqual(schema["properties"]["direction_xyz"]["type"], "array")
            self.assertEqual(schema["properties"]["direction_xyz"]["minItems"], 3)
            self.assertEqual(schema["properties"]["direction_xyz"]["maxItems"], 3)
            self.assertEqual(schema["properties"]["source_curve_ids"]["minItems"], 1)
            self.assertEqual(schema["properties"]["source_curve_ids"]["maxItems"], 128)
            self.assertTrue(schema["properties"]["source_curve_ids"]["uniqueItems"])
            self.assertEqual(schema["properties"]["associative"]["type"], "boolean")
        self.assertIs(prepare["annotations"]["readOnlyHint"], True)
        self.assertIs(edit["annotations"]["destructiveHint"], True)

        capability = self.server._capabilities()["model_native_geometry_support"]["sketch_projection"]
        self.assertEqual(capability["prepare_operation"], "operation.prepare_sketch_projection")
        self.assertEqual(capability["edit_operation"], "operation.edit_sketch_projection")
        self.assertEqual(capability["request_fields"], sorted(required,
                         key=["operation_id", "ownership_id", "binding_epoch", "revision_id",
                              "sketch_id", "source_curve_ids", "direction_xyz", "associative"].index))
        self.assertIn("±target sketch normal", capability["supported_direction"])
        self.assertIn("reject", capability["supported_direction"])

    def test_prepare_normalizes_direction_and_forwards_edge_and_curve_ids_unchanged(self):
        arguments = self.request(direction_xyz=[-2.0, -4.0, -6.0])
        response = self.call("nx_prepare_sketch_projection", arguments)
        self.assertFalse(response.get("error"), response)
        self.bridge_call.assert_called_once()
        command, forwarded = self.bridge_call.call_args.args
        self.assertEqual(command, "operation.prepare_sketch_projection")
        self.assertEqual(forwarded["source_curve_ids"], ["edge-1", "space-curve-1"])
        self.assertTrue(forwarded["associative"])
        self.assertEqual(forwarded["sketch_id"], arguments["sketch_id"])
        self.assertAlmostEqual(math.sqrt(sum(value * value for value in forwarded["direction_xyz"])), 1.0)
        self.assertEqual(forwarded["direction_xyz"], [
            -1.0 / math.sqrt(14.0), -2.0 / math.sqrt(14.0), -3.0 / math.sqrt(14.0),
        ])

    def test_edit_requires_verified_success_and_unknown_write_gate(self):
        arguments = self.request(associative=False)
        self.bridge_call.return_value = {
            "state": "SUCCEEDED", "kind": "execute_edit_sketch_projection",
            "result": {"combined_edit": True, "verified": True, "mutation_state": "APPLIED"},
        }
        succeeded = self.call("nx_edit_sketch_projection", arguments)["result"]["structuredContent"]
        self.assertEqual(self.bridge_call.call_args.args[0], "operation.edit_sketch_projection")
        self.assertEqual(succeeded.get("state"), "SUCCEEDED", succeeded)
        self.assertTrue(succeeded.get("success"), succeeded)
        self.assertTrue(succeeded.get("verified"), succeeded)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "state": "FAILED", "kind": "execute_edit_sketch_projection",
            "result": {"verified": False, "mutation_state": "ROLLED_BACK"},
            "error": {"code": "VERIFY_MISMATCH", "message": "projection readback mismatch"},
        }
        failed = self.call("nx_edit_sketch_projection", arguments)["result"]["structuredContent"]
        self.assertEqual(failed.get("error", {}).get("code"), "VERIFY_MISMATCH", failed)
        self.assertFalse(failed.get("outcome_unknown"), failed)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "state": "UNKNOWN", "kind": "execute_edit_sketch_projection",
            "result": {"verified": False, "mutation_state": "UNKNOWN"},
            "error": {"code": "OPERATION_EXECUTION_TIMEOUT", "message": "timed out"},
        }
        unknown = self.call("nx_edit_sketch_projection", arguments)["result"]["structuredContent"]
        self.assertTrue(unknown.get("outcome_unknown"), unknown)
        self.assertEqual(unknown.get("next_action"), "nx_get_operation", unknown)
        self.assertIn(arguments["operation_id"], self.server._unknown_operation_ids)

        self.bridge_call.reset_mock()
        blocked = self.call("nx_edit_sketch_projection", self.request(
            operation_id="f10-next-write"))["result"]["structuredContent"]
        self.assertEqual(blocked.get("error", {}).get("code"), "CLIENT_OUTCOME_UNKNOWN", blocked)
        self.bridge_call.assert_not_called()

    def test_invalid_request_shapes_values_and_nonfinite_directions_fail_before_bridge(self):
        invalid = [
            {**self.request(), "source_curve_ids": []},
            {**self.request(), "source_curve_ids": ["same", "same"]},
            {**self.request(), "source_curve_ids": [""]},
            {**self.request(), "source_curve_ids": ["curve-" + str(index) for index in range(129)]},
            {**self.request(), "direction_xyz": [0.0, 0.0, 0.0]},
            {**self.request(), "direction_xyz": [float("nan"), 0.0, 1.0]},
            {**self.request(), "direction_xyz": [float("inf"), 0.0, 1.0]},
            {**self.request(), "direction_xyz": [1.0, 2.0]},
            {**self.request(), "direction_xyz": [1.0, 2.0, 3.0, 4.0]},
            {**self.request(), "associative": 1},
            {**self.request(), "unexpected": True},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_input_error("nx_prepare_sketch_projection", arguments)

    def test_oblique_finite_direction_passes_protocol_normalization_for_adapter_rejection(self):
        arguments = self.request(direction_xyz=[1.0, 0.0, 1.0])
        response = self.call("nx_prepare_sketch_projection", arguments)
        self.assertFalse(response.get("error"), response)
        self.bridge_call.assert_called_once()
        self.assertEqual(self.bridge_call.call_args.args[0], "operation.prepare_sketch_projection")
        normalized = self.bridge_call.call_args.args[1]["direction_xyz"]
        self.assertAlmostEqual(normalized[0], 1.0 / math.sqrt(2.0))
        self.assertAlmostEqual(normalized[2], 1.0 / math.sqrt(2.0))

    def test_extreme_finite_direction_normalizes_without_overflow(self):
        arguments = self.request(direction_xyz=[3.0e307, 6.0e307, 9.0e307])
        response = self.call("nx_prepare_sketch_projection", arguments)
        self.assertFalse(response.get("error"), response)
        normalized = self.bridge_call.call_args.args[1]["direction_xyz"]
        self.assertTrue(all(math.isfinite(value) for value in normalized))
        self.assertAlmostEqual(normalized[0], 1.0 / math.sqrt(14.0))
        self.assertAlmostEqual(normalized[1], 2.0 / math.sqrt(14.0))
        self.assertAlmostEqual(normalized[2], 3.0 / math.sqrt(14.0))


if __name__ == "__main__":
    unittest.main()
