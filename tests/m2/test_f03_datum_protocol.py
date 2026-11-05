"""Focused Python protocol tests for F03 datum-plane offset requests."""

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


class F03DatumProtocolTests(unittest.TestCase):
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
            "operation_id": "f03-offset-1",
            "ownership_id": "owner-1",
            "binding_epoch": 7,
            "revision_id": "rev-8",
        }

    def assert_tool_input_error(self, arguments: dict):
        response = self.server._tools_call(1, {
            "name": "nx_prepare_datum_plane", "arguments": arguments,
        })
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_schema_describes_fixed_and_exactly_one_signed_offset_reference(self):
        tool = self.tool("nx_prepare_datum_plane")
        schema = tool["inputSchema"]
        self.assertEqual(schema["required"], [
            "operation_id", "ownership_id", "binding_epoch", "revision_id",
        ])
        self.assertEqual(set(schema["properties"]), set(schema["required"]) | {
            "origin_mm", "normal", "x_axis", "support_datum_plane_id", "support_face_id", "support_face_tag", "distance_mm",
            "axis_line_id", "angle_deg", "line1_id", "line2_id",
        })
        self.assertIs(schema["additionalProperties"], False)
        self.assertEqual(len(schema["oneOf"]), 6)
        self.assertIs(tool["annotations"]["readOnlyHint"], True)
        self.assertIn("zero and negative distances are allowed", tool["description"])
        self.assertIn("native normal", tool["description"])

    def test_datum_reference_routes_negative_distance_without_fixed_frame_fields(self):
        arguments = {
            **self.common(), "support_datum_plane_id": "DATUM_PLANE(4)", "distance_mm": -7.25,
        }
        response = self.server._tools_call(2, {
            "name": "nx_prepare_datum_plane", "arguments": arguments,
        })
        self.assertIs(response["result"]["isError"], False, response)
        self.bridge_call.assert_called_once_with("operation.prepare_datum_plane", arguments)

    def test_face_reference_routes_zero_distance_unchanged(self):
        arguments = {
            **self.common(), "support_face_id": "FACE 19", "distance_mm": 0.0,
        }
        response = self.server._tools_call(3, {
            "name": "nx_prepare_datum_plane", "arguments": arguments,
        })
        self.assertIs(response["result"]["isError"], False, response)
        self.bridge_call.assert_called_once_with("operation.prepare_datum_plane", arguments)

    def test_fixed_frame_request_keeps_legacy_payload(self):
        arguments = {
            **self.common(), "origin_mm": [10, 20, 30], "normal": [1, 2, 3], "x_axis": [1, 0, 0],
        }
        response = self.server._tools_call(4, {
            "name": "nx_prepare_datum_plane", "arguments": arguments,
        })
        self.assertIs(response["result"]["isError"], False, response)
        self.bridge_call.assert_called_once_with("operation.prepare_datum_plane", arguments)

    def test_mixed_ambiguous_missing_and_nonfinite_requests_fail_before_bridge(self):
        invalid = [
            {**self.common(), "support_datum_plane_id": "DATUM_PLANE(4)",
             "support_face_id": "FACE 19", "distance_mm": 1.0},
            {**self.common(), "support_datum_plane_id": "DATUM_PLANE(4)",
             "distance_mm": 1.0, "origin_mm": [0, 0, 0]},
            {**self.common(), "support_face_id": "FACE 19"},
            {**self.common(), "distance_mm": 1.0},
            {**self.common(), "support_face_id": "FACE 19", "distance_mm": float("nan")},
            {**self.common(), "support_datum_plane_id": "FACE 19", "distance_mm": float("inf")},
            {**self.common(), "support_face_id": "   ", "distance_mm": 1.0},
            {**self.common(), "support_face_id": "FACE 19", "distance_mm": 1.0, "extra": True},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_tool_input_error(arguments)

    def test_bridge_recognizes_datum_offset_prepare_command(self):
        bridge = JsonlBridge(config_from_object({}))
        self.addCleanup(bridge.close)
        with self.assertRaises(BridgeFailure) as raised:
            bridge.call("operation.prepare_datum_plane", {})
        self.assertEqual(raised.exception.code, "BRIDGE_UNCONFIGURED")


if __name__ == "__main__":
    unittest.main()
