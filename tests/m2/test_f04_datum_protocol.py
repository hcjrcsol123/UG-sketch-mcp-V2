"""Focused Python protocol tests for F04 angle datum-plane requests."""

from __future__ import annotations

from pathlib import Path
import sys
import unittest
from unittest.mock import Mock


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.bridge import BridgeFailure, JsonlBridge
from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class F04DatumProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={"prepared": True})
        self.server._bridge.call = self.bridge_call

    @staticmethod
    def tool(name: str):
        return next(tool for tool in TOOLS if tool["name"] == name)

    @staticmethod
    def common():
        return {
            "operation_id": "f04-angle-1",
            "ownership_id": "owner-1",
            "binding_epoch": 9,
            "revision_id": "rev-10",
        }

    def assert_tool_input_error(self, arguments: dict):
        response = self.server._tools_call(1, {
            "name": "nx_prepare_datum_plane", "arguments": arguments,
        })
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_schema_and_capabilities_describe_angle_contract(self):
        tool = self.tool("nx_prepare_datum_plane")
        schema = tool["inputSchema"]
        self.assertEqual(len(schema["oneOf"]), 6)
        self.assertIn("axis_line_id", schema["properties"])
        self.assertIn("angle_deg", schema["properties"])
        self.assertIs(schema["additionalProperties"], False)
        self.assertIs(tool["annotations"]["readOnlyHint"], True)
        self.assertIn("right-hand rule", tool["description"])
        self.assertIn("-180 through 180", tool["description"])

        capabilities = self.server._capabilities()
        angle = capabilities["model_native_geometry_support"]["datum_plane"]["modes"]["angle"]
        self.assertEqual(angle["required"], [
            "angle_deg", "axis_line_id", "support_datum_plane_id OR support_face_id",
        ])
        self.assertIn("native_expression_angle_deg", angle["definition_readback"])
        self.assertIn("native_parent_ids", angle["definition_readback"])

    def test_angle_requests_route_both_support_kinds_and_preserve_signed_values(self):
        cases = [
            ({**self.common(), "support_datum_plane_id": "DATUM_PLANE(4)",
              "axis_line_id": "LINE 17", "angle_deg": -30.0},
             {**self.common(), "support_datum_plane_id": "DATUM_PLANE(4)",
              "axis_line_id": "LINE 17", "angle_deg": -30.0}),
            ({**self.common(), "support_face_id": "FACE 19",
              "axis_line_id": "LINE 23", "angle_deg": 0},
             {**self.common(), "support_face_id": "FACE 19",
              "axis_line_id": "LINE 23", "angle_deg": 0}),
            ({**self.common(), "support_face_id": "FACE 20",
              "axis_line_id": "LINE 24", "angle_deg": 180},
             {**self.common(), "support_face_id": "FACE 20",
              "axis_line_id": "LINE 24", "angle_deg": 180}),
        ]
        for request_id, (arguments, expected) in enumerate(cases, start=2):
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                response = self.server._tools_call(request_id, {
                    "name": "nx_prepare_datum_plane", "arguments": arguments,
                })
                self.assertIs(response["result"]["isError"], False, response)
                self.bridge_call.assert_called_once_with("operation.prepare_datum_plane", expected)

    def test_angle_rejects_invalid_range_and_nonfinite_or_boolean_numbers(self):
        base = {
            **self.common(), "support_datum_plane_id": "DATUM_PLANE(4)",
            "axis_line_id": "LINE 17", "angle_deg": 15.0,
        }
        for angle in (180.00001, -180.00001, float("nan"), float("inf"), True):
            with self.subTest(angle=angle):
                self.bridge_call.reset_mock()
                self.assert_tool_input_error({**base, "angle_deg": angle})

    def test_angle_rejects_missing_ambiguous_and_mixed_mode_fields(self):
        base = {
            **self.common(), "support_datum_plane_id": "DATUM_PLANE(4)",
            "axis_line_id": "LINE 17", "angle_deg": 15.0,
        }
        invalid = [
            {key: value for key, value in base.items() if key != "axis_line_id"},
            {key: value for key, value in base.items() if key != "angle_deg"},
            {**base, "support_face_id": "FACE 19"},
            {**base, "distance_mm": 2.0},
            {**base, "origin_mm": [0, 0, 0]},
            {**base, "mode": "angle"},
            {**base, "extra": True},
            {**self.common(), "axis_line_id": "LINE 17", "angle_deg": 15.0},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_tool_input_error(arguments)

    def test_bridge_recognizes_prepare_command(self):
        bridge = JsonlBridge(config_from_object({}))
        self.addCleanup(bridge.close)
        with self.assertRaises(BridgeFailure) as raised:
            bridge.call("operation.prepare_datum_plane", {})
        self.assertEqual(raised.exception.code, "BRIDGE_UNCONFIGURED")


if __name__ == "__main__":
    unittest.main()
