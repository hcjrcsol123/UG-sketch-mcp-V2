"""Offline MCP protocol checks for F11 sketch reference-state conversion; no NX calls."""
from __future__ import annotations

import unittest
from pathlib import Path
from unittest.mock import Mock
import sys


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class F11SketchReferenceConversionProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={
            "plan_id": "reference-conversion-plan-1",
            "kind": "edit_sketch_reference_conversion",
        })
        self.server._bridge.call = self.bridge_call

    @staticmethod
    def request(**overrides):
        request = {
            "operation_id": "f11-reference-conversion-1",
            "ownership_id": "owner-1",
            "binding_epoch": 11,
            "revision_id": "rev-11",
            "sketch_id": "active-sketch-1",
            "object_ids": ["line-1", "sketch-dimension-1"],
            "target_state": "reference",
        }
        request.update(overrides)
        return request

    def call(self, name, arguments):
        return self.server._tools_call(1, {"name": name, "arguments": arguments})

    def assert_input_error(self, name, arguments):
        response = self.call(name, arguments)
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_schema_and_capability_publish_only_explicit_target_state_conversion(self):
        by_name = {item["name"]: item for item in TOOLS}
        self.assertEqual(len(TOOLS), len(by_name))
        prepare_name = "nx_prepare_sketch_reference_conversion"
        edit_name = "nx_edit_sketch_reference_conversion"
        self.assertIn(prepare_name, by_name)
        self.assertIn(edit_name, by_name)
        prepare, edit = by_name[prepare_name], by_name[edit_name]
        required = {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "object_ids", "target_state",
        }
        for tool in (prepare, edit):
            schema = tool["inputSchema"]
            self.assertEqual(set(schema["required"]), required, schema)
            self.assertFalse(schema["additionalProperties"], schema)
            self.assertEqual(schema["properties"]["object_ids"]["type"], "array")
            self.assertEqual(schema["properties"]["object_ids"]["minItems"], 1)
            self.assertEqual(schema["properties"]["object_ids"]["maxItems"], 256)
            self.assertEqual(schema["properties"]["object_ids"]["uniqueItems"], True)
            self.assertEqual(schema["properties"]["target_state"]["enum"], ["reference", "active"])
        self.assertIs(prepare["annotations"]["readOnlyHint"], True)
        self.assertIs(edit["annotations"]["destructiveHint"], True)

        capability = self.server._capabilities()["model_native_geometry_support"][
            "sketch_reference_conversion"
        ]
        self.assertEqual(capability["prepare_operation"],
                         "operation.prepare_sketch_reference_conversion")
        self.assertEqual(capability["edit_operation"],
                         "operation.edit_sketch_reference_conversion")
        self.assertEqual(set(capability["request_fields"]), required)
        self.assertIn("1 through 256 unique current Journal IDs", capability["selection"])
        self.assertEqual(capability["supported_dimension_types"], ["HorizontalDim", "VerticalDim"])
        self.assertEqual(capability["dimension_unit"], "mm; unsupported dimensions are rejected until their native unit mapping is verified")
        self.assertEqual(capability["supported_curve_types"], ["Line", "Arc"])
        self.assertIn("Ellipse", capability["unsupported_selected_curve_types"])
        self.assertIn("Spline", capability["unsupported_selected_curve_types"])
        self.assertEqual(capability["target_state"]["reference"]["curve"],
                         "NX UF Sketch reference status Reference (0)")
        self.assertIn("measurement only", capability["target_state"]["reference"]["dimension"])
        self.assertEqual(capability["target_state"]["active"]["curve"],
                         "NX UF Sketch reference status Active (1)")
        self.assertIn("Driving", capability["target_state"]["active"]["dimension"])
        self.assertIn("may still participate in geometric constraints", capability["curve_semantics"])
        self.assertIn("numeric expression RHS/value", capability["dimension_semantics"])
        self.assertIn("ComputedSize", capability["dimension_semantics"])
        self.assertIn("EntireSketch", capability["solver_evaluation"])
        self.assertIn("not guaranteed to be restored", capability["work_region_scope"])
        self.assertIn("does not create a Section", capability["profile_candidates"])
        self.assertIn("every object is already", capability["mixed_state_selection"])

    def test_prepare_forwards_exact_selected_ids_and_requested_state_without_defaults(self):
        arguments = self.request(object_ids=["line-1", "sketch-dimension-1"], target_state="active")
        response = self.call("nx_prepare_sketch_reference_conversion", arguments)
        self.assertFalse(response.get("error"), response)
        self.bridge_call.assert_called_once()
        command, forwarded = self.bridge_call.call_args.args
        self.assertEqual(command, "operation.prepare_sketch_reference_conversion")
        self.assertEqual(forwarded["object_ids"], ["line-1", "sketch-dimension-1"])
        self.assertEqual(forwarded["target_state"], "active")
        self.assertEqual(forwarded["sketch_id"], "active-sketch-1")
        self.assertEqual(set(forwarded), set(arguments))

    def test_edit_reports_known_failure_and_unknown_without_claiming_verification(self):
        arguments = self.request(target_state="active")
        self.bridge_call.return_value = {
            "state": "SUCCEEDED", "kind": "execute_edit_sketch_reference_conversion",
            "result": {"combined_edit": True, "verified": True, "mutation_state": "APPLIED"},
        }
        succeeded = self.call("nx_edit_sketch_reference_conversion", arguments)[
            "result"]["structuredContent"]
        self.assertEqual(self.bridge_call.call_args.args[0],
                         "operation.edit_sketch_reference_conversion")
        self.assertEqual(succeeded.get("state"), "SUCCEEDED", succeeded)
        self.assertTrue(succeeded.get("success"), succeeded)
        self.assertTrue(succeeded.get("verified"), succeeded)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "state": "FAILED", "kind": "execute_edit_sketch_reference_conversion",
            "result": {"verified": False, "mutation_state": "ROLLED_BACK"},
            "error": {"code": "VERIFY_MISMATCH", "message": "state readback mismatch"},
        }
        failed = self.call("nx_edit_sketch_reference_conversion", arguments)[
            "result"]["structuredContent"]
        self.assertEqual(failed.get("error", {}).get("code"), "VERIFY_MISMATCH", failed)
        self.assertFalse(failed.get("outcome_unknown"), failed)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "state": "UNKNOWN", "kind": "execute_edit_sketch_reference_conversion",
            "result": {"verified": False, "mutation_state": "UNKNOWN"},
            "error": {"code": "OPERATION_EXECUTION_TIMEOUT", "message": "timed out"},
        }
        unknown = self.call("nx_edit_sketch_reference_conversion", arguments)[
            "result"]["structuredContent"]
        self.assertTrue(unknown.get("outcome_unknown"), unknown)
        self.assertEqual(unknown.get("next_action"), "nx_get_operation", unknown)
        self.assertIn(arguments["operation_id"], self.server._unknown_operation_ids)

        self.bridge_call.reset_mock()
        blocked = self.call("nx_edit_sketch_reference_conversion", self.request(
            operation_id="f11-write-after-unknown"))["result"]["structuredContent"]
        self.assertEqual(blocked.get("error", {}).get("code"), "CLIENT_OUTCOME_UNKNOWN", blocked)
        self.bridge_call.assert_not_called()

    def test_invalid_ids_selections_states_and_extra_fields_fail_before_bridge(self):
        invalid = [
            {**self.request(), "object_ids": []},
            {**self.request(), "object_ids": ["same", "same"]},
            {**self.request(), "object_ids": [""]},
            {**self.request(), "object_ids": ["object-" + str(index) for index in range(257)]},
            {**self.request(), "object_ids": "line-1"},
            {**self.request(), "object_ids": ["line-1", 3]},
            {**self.request(), "target_state": "Reference"},
            {**self.request(), "target_state": "suppressed"},
            {**self.request(), "target_state": None},
            {**self.request(), "unexpected": True},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_input_error("nx_prepare_sketch_reference_conversion", arguments)


if __name__ == "__main__":
    unittest.main()
