"""Focused protocol tests for F06 native sketch geometry records."""

from __future__ import annotations

from pathlib import Path
import sys
import unittest
from unittest.mock import Mock


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class F06SketchProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={"prepared": True})
        self.server._bridge.call = self.bridge_call

    @staticmethod
    def common():
        return {
            "operation_id": "f06-sketch-1",
            "ownership_id": "owner-1",
            "binding_epoch": 9,
            "revision_id": "rev-10",
            "plane": "XY",
            "origin_mm": [0.0, 0.0, 0.0],
        }

    def call(self, arguments: dict):
        return self.server._tools_call(1, {"name": "nx_prepare_sketch", "arguments": arguments})

    def assert_input_error(self, arguments: dict):
        response = self.call(arguments)
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_schema_describes_f06_with_f08_tools(self):
        tool_names = {item["name"] for item in TOOLS}
        self.assertEqual(len(TOOLS), len(tool_names))
        self.assertIn("nx_prepare_sketch_corner", tool_names)
        self.assertIn("nx_edit_sketch_corner", tool_names)
        tool = next(item for item in TOOLS if item["name"] == "nx_prepare_sketch")
        geometry_schema = tool["inputSchema"]["properties"]["geometries"]["items"]["anyOf"]
        types = {variant["properties"]["type"]["const"] for variant in geometry_schema}
        self.assertEqual(types, {"line", "rectangle", "circle", "arc", "ellipse", "elliptical_arc", "spline"})
        ellipse_schema = next(item for item in geometry_schema if item["properties"]["type"]["const"] == "ellipse")
        self.assertEqual(ellipse_schema["properties"]["rotation_deg"]["minimum"], 0)
        self.assertEqual(ellipse_schema["properties"]["rotation_deg"]["exclusiveMaximum"], 360)
        self.assertIn("unconstrained", tool["description"])
        self.assertIn("endpoint", tool["description"])
        constraint_mode = tool["inputSchema"]["properties"]["constraint_mode"]
        self.assertEqual(constraint_mode["enum"], ["legacy_driving", "geometry_only"])
        sketch_modes = self.server._capabilities()["model_native_geometry_support"]["sketch"]["constraint_modes"]
        self.assertIn("Driving dimensions", sketch_modes["legacy_driving"])
        self.assertIn("without Driving dimensions", sketch_modes["geometry_only"])

        support = self.server._capabilities()["model_native_geometry_support"]["sketch"]
        spline = next(item for item in support["geometries"] if item["type"] == "spline")
        self.assertIn("normalized chord-length", spline["interpolation"])
        self.assertIn("endpoint slope", spline["interpolation"])
        validity = support["profile_analysis"]
        self.assertEqual(validity["result_field"], "profile_analysis")
        self.assertIn("VALID_CLOSED_PROFILES", validity["statuses"])
        self.assertIn("Section loop count alone does not prove profile validity", validity["validity_rule"])

    def test_legacy_shapes_and_all_f06_shapes_route_together(self):
        geometries = [
            {"id": "line", "type": "line", "start_mm": [0, 0], "end_mm": [4, 0]},
            {"id": "rectangle", "type": "rectangle", "corner_mm": [0, 0], "width_mm": 8, "height_mm": 5},
            {"id": "circle", "type": "circle", "center_mm": [12, 0], "diameter_mm": 3},
            {"id": "arc", "type": "arc", "center_mm": [0, 0], "radius_mm": 4, "start_angle_deg": 0, "sweep_deg": -90},
            {"id": "ellipse", "type": "ellipse", "center_mm": [2, 3], "major_radius_mm": 5, "minor_radius_mm": 2, "rotation_deg": 342},
            {"id": "elliptical_arc", "type": "elliptical_arc", "center_mm": [5, 7], "major_radius_mm": 6, "minor_radius_mm": 3, "rotation_deg": 15, "start_angle_deg": 20, "sweep_deg": 120},
            {"id": "spline", "type": "spline", "interpolation_points_mm": [[0, 0], [1, 2], [3, 2], [4, 0]], "degree": 3, "endpoint_condition": "unconstrained"},
        ]
        response = self.call({**self.common(), "geometries": geometries})
        self.assertIs(response["result"]["isError"], False, response)
        normalized = self.bridge_call.call_args.args[1]
        self.assertEqual([item["type"] for item in normalized["geometries"]],
                         ["line", "rectangle", "circle", "arc", "ellipse", "elliptical_arc", "spline"])
        self.assertEqual(self.bridge_call.call_args.args[0], "operation.prepare_sketch")

    def test_spline_contract_requires_cubic_unconstrained_chord_length_inputs(self):
        spline = {"id": "s", "type": "spline", "interpolation_points_mm": [[0, 0], [1, 2], [3, 2], [4, 0]], "degree": 3, "endpoint_condition": "unconstrained"}
        response = self.call({**self.common(), "geometries": [spline]})
        self.assertIs(response["result"]["isError"], False, response)
        normalized = self.bridge_call.call_args.args[1]["geometries"][0]
        self.assertEqual(normalized["interpolation_points_mm"], spline["interpolation_points_mm"])
        self.assertEqual(normalized["degree"], 3)
        self.assertEqual(normalized["endpoint_condition"], "unconstrained")

        invalid = [
            {**spline, "degree": 2},
            {**spline, "endpoint_condition": "tangent"},
            {**spline, "interpolation_points_mm": [[0, 0], [1, 2], [3, 2]]},
            {**spline, "interpolation_points_mm": [[0, 0], [1, 2], [1, 2], [4, 0]]},
            {**spline, "extra": True},
        ]
        for geometry in invalid:
            with self.subTest(geometry=geometry):
                self.bridge_call.reset_mock()
                self.assert_input_error({**self.common(), "geometries": [geometry]})
        self.bridge_call.reset_mock()
        self.assert_input_error({**self.common(), "geometries": [{"id": "bad", "type": []}]})

    def test_arc_and_ellipse_ranges_and_exact_fields_are_enforced(self):
        invalid = [
            {"id": "a", "type": "arc", "center_mm": [0, 0], "radius_mm": 2, "start_angle_deg": 360, "sweep_deg": 90},
            {"id": "a", "type": "arc", "center_mm": [0, 0], "radius_mm": 2, "start_angle_deg": 0, "sweep_deg": 0},
            {"id": "a", "type": "arc", "center_mm": [0, 0], "radius_mm": 2, "start_angle_deg": 0, "sweep_deg": 360},
            {"id": "e", "type": "ellipse", "center_mm": [0, 0], "major_radius_mm": 2, "minor_radius_mm": 3, "rotation_deg": 0},
            {"id": "e", "type": "ellipse", "center_mm": [0, 0], "major_radius_mm": 2, "minor_radius_mm": 1, "rotation_deg": 360},
            {"id": "e", "type": "elliptical_arc", "center_mm": [0, 0], "major_radius_mm": 2, "minor_radius_mm": 1, "rotation_deg": 0, "start_angle_deg": 0, "sweep_deg": -360},
            {"id": "e", "type": "ellipse", "center_mm": [0, 0], "major_radius_mm": 2, "minor_radius_mm": 1, "rotation_deg": 0, "extra": 1},
        ]
        for geometry in invalid:
            with self.subTest(geometry=geometry):
                self.bridge_call.reset_mock()
                self.assert_input_error({**self.common(), "geometries": [geometry]})

    def test_empty_and_open_geometry_requests_remain_supported(self):
        empty = self.call(self.common())
        self.assertIs(empty["result"]["isError"], False, empty)
        self.assertEqual(self.bridge_call.call_args.args[1]["geometries"], [])
        self.bridge_call.reset_mock()
        opened = self.call({**self.common(), "geometries": [
            {"id": "line", "type": "line", "start_mm": [0, 0], "end_mm": [3, 4]},
        ]})
        self.assertIs(opened["result"]["isError"], False, opened)

    def test_geometry_only_mode_routes_and_legacy_default_stays_canonical(self):
        response = self.call({**self.common(), "geometries": [], "constraint_mode": "geometry_only"})
        self.assertIs(response["result"]["isError"], False, response)
        normalized = self.bridge_call.call_args.args[1]
        self.assertEqual(normalized.get("constraint_mode"), "geometry_only", normalized)

        self.bridge_call.reset_mock()
        explicit_legacy = self.call({**self.common(), "constraint_mode": "legacy_driving"})
        self.assertIs(explicit_legacy["result"]["isError"], False, explicit_legacy)
        self.assertNotIn("constraint_mode", self.bridge_call.call_args.args[1])

        for invalid in ("infer", "fully_constrained", 1, None):
            with self.subTest(constraint_mode=invalid):
                self.bridge_call.reset_mock()
                self.assert_input_error({**self.common(), "constraint_mode": invalid})


if __name__ == "__main__":
    unittest.main()
