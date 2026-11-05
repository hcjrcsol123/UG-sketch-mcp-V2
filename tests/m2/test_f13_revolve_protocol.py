"""Offline F13 protocol checks; these never start an NX session."""

from __future__ import annotations

from pathlib import Path
import sys
import unittest
from unittest.mock import Mock


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class F13RevolveProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={"plan_id": "f13-plan-1", "kind": "create_revolve"})
        self.server._bridge.call = self.bridge_call

    @staticmethod
    def request(**overrides):
        request = {
            "operation_id": "f13-revolve-1",
            "ownership_id": "owner-1",
            "binding_epoch": 13,
            "revision_id": "rev-13",
            "sketch_id": "profile-sketch-1",
            "mode": "create",
            "axis_point_mm": [0.0, 0.0, 0.0],
            "axis_direction": [0.0, 0.0, 4.0],
            "start_angle_deg": 0.0,
            "end_angle_deg": 90.0,
        }
        request.update(overrides)
        return request

    def call(self, arguments):
        return self.server._tools_call(1, {"name": "nx_prepare_revolve", "arguments": arguments})

    def assert_invalid_without_bridge(self, arguments):
        response = self.call(arguments)
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_schema_and_description_publish_exact_f13_contract(self):
        tool = next(item for item in TOOLS if item["name"] == "nx_prepare_revolve")
        schema = tool["inputSchema"]
        self.assertEqual(set(schema["required"]), {
            "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id", "mode",
            "axis_point_mm", "axis_direction", "start_angle_deg", "end_angle_deg",
        })
        self.assertFalse(schema["additionalProperties"], schema)
        properties = schema["properties"]
        self.assertEqual(properties["mode"]["enum"], ["create", "unite", "subtract"])
        self.assertEqual(properties["profile_output"], {
            "type": "string", "enum": ["solid", "sheet"], "default": "solid",
        })
        for name in ("axis_point_mm", "axis_direction"):
            self.assertEqual(properties[name]["type"], "array")
            self.assertEqual(properties[name]["minItems"], 3)
            self.assertEqual(properties[name]["maxItems"], 3)
            self.assertEqual(properties[name]["items"]["type"], "number")
        self.assertEqual(properties["axis_point_mm"]["items"]["minimum"], -1_000_000)
        self.assertEqual(properties["axis_point_mm"]["items"]["maximum"], 1_000_000)
        for name in ("start_angle_deg", "end_angle_deg"):
            self.assertEqual(properties[name]["type"], "number")
            self.assertEqual(properties[name]["minimum"], -360)
            self.assertEqual(properties[name]["maximum"], 360)

        conditions = schema["allOf"]
        self.assertEqual(len(conditions), 3)
        self.assertEqual(conditions[0]["if"]["properties"]["mode"]["const"], "create")
        self.assertEqual(conditions[0]["then"]["not"]["required"], ["target_body_id"])
        self.assertEqual(conditions[1]["if"]["properties"]["mode"]["enum"], ["unite", "subtract"])
        self.assertEqual(conditions[1]["then"]["required"], ["target_body_id"])
        self.assertEqual(conditions[2]["if"]["properties"]["profile_output"]["const"], "sheet")
        self.assertEqual(conditions[2]["then"]["properties"]["mode"]["const"], "create")
        self.assertTrue(tool["annotations"]["readOnlyHint"])
        description = tool["description"].lower()
        for phrase in (
            "explicit sketch", "active-curve section", "whole-sketch", "in-plane", "right-hand-rule",
            "negative spans", "sheet output", "one explicit solid target", "nx_execute_plan", "nx_verify_part",
        ):
            self.assertIn(phrase, description)
        capability = self.server._capabilities()["model_native_geometry_support"]["revolve"]
        self.assertEqual(capability["profile_output"]["values"], ["solid", "sheet"])
        self.assertEqual(capability["profile_output"]["default"], "solid")
        self.assertEqual(capability["modes"], ["create", "unite", "subtract"])

    def test_default_solid_axis_normalization_and_create_routing(self):
        arguments = self.request(axis_direction=[2.0, -2.0, 1.0], axis_point_mm=[-1_000_000, 1_000_000, 0])
        response = self.call(arguments)
        self.assertFalse(response.get("error"), response)
        self.bridge_call.assert_called_once_with("operation.prepare_revolve", {
            "operation_id": arguments["operation_id"],
            "ownership_id": arguments["ownership_id"],
            "binding_epoch": arguments["binding_epoch"],
            "revision_id": arguments["revision_id"],
            "sketch_id": arguments["sketch_id"],
            "mode": "create",
            "profile_output": "solid",
            "axis_point_mm": [-1_000_000.0, 1_000_000.0, 0.0],
            "axis_direction": [2.0 / 3.0, -2.0 / 3.0, 1.0 / 3.0],
            "start_angle_deg": 0.0,
            "end_angle_deg": 90.0,
        })

    def test_axis_normalization_scales_finite_large_vectors_without_overflow(self):
        response = self.call(self.request(axis_direction=[1e308, 1e308, 1e308]))
        self.assertFalse(response.get("error"), response)
        normalized = self.bridge_call.call_args.args[1]["axis_direction"]
        self.assertEqual(len(normalized), 3)
        expected = 1.0 / (3.0 ** 0.5)
        for component in normalized:
            self.assertAlmostEqual(component, expected, places=15)

    def test_finite_nonzero_signed_angle_spans_include_just_above_threshold_and_full_turn(self):
        valid = [
            (0.0, 1.0001e-9),
            (10.0, 10.000000002),
            (90.0, -90.0),
            (-180.0, 180.0),
            (-200.0, 160.0),
            (0.0, 360.0),
            (0.0, -360.0),
            (-360.0, 0.0),
            (360.0, 0.0),
        ]
        for start, end in valid:
            with self.subTest(start=start, end=end):
                self.bridge_call.reset_mock()
                response = self.call(self.request(start_angle_deg=start, end_angle_deg=end))
                self.assertFalse(response.get("error"), response)
                self.assertEqual(self.bridge_call.call_args.args[0], "operation.prepare_revolve")
                forwarded = self.bridge_call.call_args.args[1]
                self.assertEqual(forwarded["start_angle_deg"], float(start))
                self.assertEqual(forwarded["end_angle_deg"], float(end))
                self.assertEqual(forwarded["axis_direction"], [0.0, 0.0, 1.0])

    def test_unite_and_subtract_require_explicit_solid_target(self):
        for mode in ("unite", "subtract"):
            with self.subTest(mode=mode):
                arguments = self.request(mode=mode, target_body_id="body-2", profile_output="solid")
                response = self.call(arguments)
                self.assertFalse(response.get("error"), response)
                forwarded = self.bridge_call.call_args.args[1]
                self.assertEqual(self.bridge_call.call_args.args[0], "operation.prepare_revolve")
                self.assertEqual(forwarded["mode"], mode)
                self.assertEqual(forwarded["profile_output"], "solid")
                self.assertEqual(forwarded["target_body_id"], "body-2")

    def test_explicit_sheet_output_routes_only_for_create(self):
        arguments = self.request(profile_output="sheet")
        response = self.call(arguments)
        self.assertFalse(response.get("error"), response)
        self.assertEqual(self.bridge_call.call_args.args[0], "operation.prepare_revolve")
        self.assertEqual(self.bridge_call.call_args.args[1]["profile_output"], "sheet")

    def test_invalid_fields_axes_modes_targets_and_spans_fail_before_bridge(self):
        base = self.request()
        invalid = [
            {key: value for key, value in base.items() if key != "axis_direction"},
            {key: value for key, value in base.items() if key != "start_angle_deg"},
            {key: value for key, value in base.items() if key != "end_angle_deg"},
            {**base, "unknown": True},
            {**base, "mode": "replace"},
            {**base, "mode": "create", "target_body_id": "body-1"},
            {**base, "mode": "unite"},
            {**base, "mode": "subtract", "target_body_id": ""},
            {**base, "mode": "unite", "target_body_id": "body-1", "profile_output": "sheet"},
            {**base, "profile_output": "mesh"},
            {**base, "axis_point_mm": [0.0, 0.0]},
            {**base, "axis_point_mm": (0.0, 0.0, 0.0)},
            {**base, "axis_point_mm": [0.0, True, 0.0]},
            {**base, "axis_point_mm": [float("nan"), 0.0, 0.0]},
            {**base, "axis_point_mm": [0.0, float("inf"), 0.0]},
            {**base, "axis_point_mm": [1_000_000.1, 0.0, 0.0]},
            {**base, "axis_direction": [0.0, 0.0]},
            {**base, "axis_direction": [0.0, 0.0, 0.0]},
            {**base, "axis_direction": [False, 0.0, 1.0]},
            {**base, "axis_direction": [float("nan"), 0.0, 1.0]},
            {**base, "axis_direction": [0.0, float("inf"), 1.0]},
            {**base, "start_angle_deg": 1.0, "end_angle_deg": 1.0},
            {**base, "start_angle_deg": 0.0, "end_angle_deg": 1e-9},
            {**base, "start_angle_deg": -200.0, "end_angle_deg": 160.0001},
            {**base, "start_angle_deg": float("nan")},
            {**base, "end_angle_deg": float("inf")},
            {**base, "start_angle_deg": -360.0001},
            {**base, "end_angle_deg": 360.0001},
            {**base, "start_angle_deg": True},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_invalid_without_bridge(arguments)

    def test_f12_extrude_execute_and_fresh_verify_routes_remain_unchanged(self):
        self.bridge_call.side_effect = [
            {"plan_id": "f12-plan-1", "kind": "create_extrude"},
            {"state": "SUCCEEDED", "plan_id": "f12-plan-1"},
            {"state": "SUCCEEDED", "verified": True},
        ]
        extrude_arguments = {
            "operation_id": "f12-extrude-route-1", "ownership_id": "owner-1", "binding_epoch": 12,
            "revision_id": "rev-12", "sketch_id": "profile-sketch-1", "mode": "create",
            "direction": "positive", "extent": "finite", "depth_mm": 8.0,
        }
        response = self.server._tools_call(1, {
            "name": "nx_prepare_extrude", "arguments": extrude_arguments,
        })
        self.assertFalse(response.get("error"), response)
        self.assertEqual(self.bridge_call.call_args_list[-1].args[0], "operation.prepare_extrude")

        execute_arguments = {
            "operation_id": "f12-execute-route-1", "ownership_id": "owner-1",
            "binding_epoch": 12, "plan_id": "f12-plan-1",
        }
        response = self.server._tools_call(2, {"name": "nx_execute_plan", "arguments": execute_arguments})
        self.assertFalse(response.get("error"), response)
        self.assertEqual(self.bridge_call.call_args_list[-1], unittest.mock.call("operation.execute_plan", execute_arguments))

        verify_arguments = {
            "operation_id": "f12-verify-route-1", "ownership_id": "owner-1", "binding_epoch": 12,
            "execute_operation_id": "f12-execute-route-1",
        }
        response = self.server._tools_call(3, {"name": "nx_verify_part", "arguments": verify_arguments})
        self.assertFalse(response.get("error"), response)
        self.assertEqual(self.bridge_call.call_args_list[-1], unittest.mock.call("operation.verify_part", verify_arguments))
        self.assertEqual([call.args[0] for call in self.bridge_call.call_args_list], [
            "operation.prepare_extrude", "operation.execute_plan", "operation.verify_part",
        ])


if __name__ == "__main__":
    unittest.main()
