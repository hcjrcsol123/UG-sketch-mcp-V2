"""Offline F12 extrusion contract checks; these never call NXOpen."""

from __future__ import annotations

from pathlib import Path
import sys
import unittest
from unittest.mock import Mock


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class F12ExtrudeProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={"plan_id": "f12-plan-1", "kind": "create_extrude"})
        self.server._bridge.call = self.bridge_call

    @staticmethod
    def request(**overrides):
        request = {
            "operation_id": "f12-extrude-1",
            "ownership_id": "owner-1",
            "binding_epoch": 12,
            "revision_id": "rev-12",
            "sketch_id": "profile-sketch-1",
            "mode": "create",
            "direction": "positive",
            "extent": "finite",
            "depth_mm": 10.0,
        }
        request.update(overrides)
        return request

    def call(self, arguments):
        return self.server._tools_call(1, {"name": "nx_prepare_extrude", "arguments": arguments})

    def assert_invalid_without_bridge(self, arguments):
        response = self.call(arguments)
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_schema_publishes_explicit_profile_mode_and_mutually_exclusive_limits(self):
        tool = next(item for item in TOOLS if item["name"] == "nx_prepare_extrude")
        schema = tool["inputSchema"]
        self.assertEqual(set(schema["required"]), {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
            "mode", "direction", "extent",
        })
        self.assertFalse(schema["additionalProperties"], schema)
        properties = schema["properties"]
        self.assertEqual(properties["profile_output"]["enum"], ["solid", "sheet"])
        for name in ("depth_mm", "start_offset_mm", "end_offset_mm"):
            self.assertEqual(properties[name]["type"], "number")
        self.assertTrue(tool["annotations"]["readOnlyHint"])
        description = tool["description"].lower()
        self.assertIn("explicit sketch", description)
        self.assertIn("all valid closed regions", description)
        self.assertIn("reference", description)
        self.assertIn("through", description)
        capability = self.server._capabilities()["model_native_geometry_support"]["extrude"]
        self.assertEqual(capability["profile_output"]["values"], ["solid", "sheet"])
        self.assertEqual(capability["profile_output"]["default"], "solid")
        self.assertIn("Line", capability["supported_profiles"][0])
        self.assertIn("Ellipse", capability["supported_profiles"][0])
        self.assertIn("Spline", capability["supported_profiles"][0])
        self.assertEqual(capability["through_all_modes"], ["unite", "subtract"])
        self.assertIn("reference curves excluded", capability["section_selection"])

    def test_default_solid_and_legacy_depth_are_normalized_without_changing_lifecycle(self):
        arguments = self.request()
        response = self.call(arguments)
        self.assertFalse(response.get("error"), response)
        normalized = self.bridge_call.call_args.args[1]
        self.assertEqual(normalized, {
            **{key: value for key, value in arguments.items() if key != "depth_mm"},
            "profile_output": "solid", "start_offset_mm": 0.0, "end_offset_mm": 10.0,
        })

    def test_finite_offset_form_requires_both_endpoints_and_keeps_signed_direction_coordinates(self):
        arguments = self.request(depth_mm=999.0)
        arguments.pop("depth_mm")
        arguments.update(start_offset_mm=-4.0, end_offset_mm=6.0, direction="negative")
        response = self.call(arguments)
        self.assertFalse(response.get("error"), response)
        self.bridge_call.assert_called_once_with("operation.prepare_extrude", {
            **arguments, "profile_output": "solid",
        })

    def test_through_all_boolean_defaults_start_and_requires_explicit_solid_target(self):
        arguments = self.request(mode="subtract", extent="through_all", target_body_id="body-2")
        arguments.pop("depth_mm")
        arguments["direction"] = "negative"
        response = self.call(arguments)
        self.assertFalse(response.get("error"), response)
        forwarded = self.bridge_call.call_args.args[1]
        self.assertEqual(forwarded["start_offset_mm"], 0.0)
        self.assertEqual(forwarded["profile_output"], "solid")
        self.assertNotIn("depth_mm", forwarded)
        self.assertNotIn("end_offset_mm", forwarded)

    def test_sheet_output_routes_only_explicit_create_with_finite_extent(self):
        arguments = self.request(profile_output="sheet")
        response = self.call(arguments)
        self.assertFalse(response.get("error"), response)
        self.assertEqual(self.bridge_call.call_args.args[1]["profile_output"], "sheet")
        self.assertEqual(self.bridge_call.call_args.args[1]["start_offset_mm"], 0.0)
        self.assertEqual(self.bridge_call.call_args.args[1]["end_offset_mm"], 10.0)

    def test_legacy_depth_and_equivalent_offset_form_normalize_identically(self):
        legacy = self.request(operation_id="f12-normalized-1")
        offsets = self.request(operation_id="f12-normalized-1")
        offsets.pop("depth_mm")
        offsets.update(start_offset_mm=0.0, end_offset_mm=10.0)
        self.assertEqual(self.server._validate_prepare_extrude(legacy),
                         self.server._validate_prepare_extrude(offsets))

    def test_invalid_limit_and_mode_combinations_fail_before_bridge(self):
        base = self.request()
        offset_base = {key: value for key, value in base.items() if key != "depth_mm"}
        invalid = [
            {key: value for key, value in base.items() if key != "depth_mm"},
            {**offset_base, "start_offset_mm": -1.0},
            {**offset_base, "end_offset_mm": 4.0},
            {**base, "start_offset_mm": -1.0, "end_offset_mm": 4.0},
            {**offset_base, "start_offset_mm": 5.0, "end_offset_mm": 5.0},
            {**offset_base, "start_offset_mm": 6.0, "end_offset_mm": 5.0},
            {**offset_base, "start_offset_mm": float("nan"), "end_offset_mm": 5.0},
            {**base, "depth_mm": 0.0},
            {**base, "depth_mm": -2.0},
            {**base, "depth_mm": float("inf")},
            {**base, "depth_mm": float("nan")},
            {**base, "mode": "create", "extent": "through_all", "target_body_id": "body-1"},
            {**base, "mode": "unite"},
            {**base, "mode": "subtract", "target_body_id": ""},
            {**base, "mode": "unite", "profile_output": "sheet", "target_body_id": "body-1"},
            {**base, "extent": "through_all", "mode": "subtract", "target_body_id": "body-1",
             "profile_output": "sheet"},
            {**offset_base, "extent": "through_all", "mode": "subtract", "target_body_id": "body-1",
             "start_offset_mm": -1.0, "end_offset_mm": 5.0},
            {**base, "profile_output": "mesh"},
            {**base, "extra": True},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_invalid_without_bridge(arguments)


if __name__ == "__main__":
    unittest.main()
