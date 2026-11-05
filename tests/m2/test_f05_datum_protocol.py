"""Focused Python protocol tests for F05 two-line datum-plane requests."""

from __future__ import annotations

from pathlib import Path
import sys
import unittest
from unittest.mock import Mock


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class F05DatumProtocolTests(unittest.TestCase):
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
            "operation_id": "f05-two-lines-1",
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

    def test_schema_and_capabilities_describe_two_line_contract_without_external_mode(self):
        tool = self.tool("nx_prepare_datum_plane")
        schema = tool["inputSchema"]
        self.assertEqual(len(schema["oneOf"]), 6)
        self.assertIn("line1_id", schema["properties"])
        self.assertIn("line2_id", schema["properties"])
        self.assertIs(schema["additionalProperties"], False)
        self.assertIs(tool["annotations"]["readOnlyHint"], True)
        self.assertIn("infinite lines", tool["description"])
        self.assertIn("coincident", tool["description"])

        modes = self.server._capabilities()["model_native_geometry_support"]["datum_plane"]["modes"]
        two_lines = modes["two_lines"]
        self.assertEqual(two_lines["required"], ["line1_id", "line2_id"])
        self.assertIn("native_alternate", two_lines["definition_readback"])
        self.assertIn("x_axis_source", two_lines["definition_readback"])
        self.assertIn("line1_endpoint_plane_residuals_mm", two_lines["definition_readback"])
        self.assertIn("parent_reference_matches", two_lines["definition_readback"])

    def test_two_line_request_routes_both_ids_without_public_mode(self):
        arguments = {
            **self.common(), "line1_id": "LINE 17", "line2_id": "LINE 23",
        }
        response = self.server._tools_call(2, {
            "name": "nx_prepare_datum_plane", "arguments": arguments,
        })
        self.assertIs(response["result"]["isError"], False, response)
        self.bridge_call.assert_called_once_with("operation.prepare_datum_plane", arguments)
        self.assertNotIn("mode", self.bridge_call.call_args.args[1])

    def test_two_line_request_rejects_missing_blank_or_non_string_ids(self):
        base = {**self.common(), "line1_id": "LINE 17", "line2_id": "LINE 23"}
        invalid = [
            {key: value for key, value in base.items() if key != "line1_id"},
            {key: value for key, value in base.items() if key != "line2_id"},
            {**base, "line1_id": "  "},
            {**base, "line2_id": ""},
            {**base, "line1_id": 17},
            {**base, "line2_id": None},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_tool_input_error(arguments)

    def test_two_line_request_rejects_repeated_id_and_mixed_modes(self):
        base = {**self.common(), "line1_id": "LINE 17", "line2_id": "LINE 23"}
        invalid = [
            {**base, "line2_id": "LINE 17"},
            {**base, "origin_mm": [0, 0, 0]},
            {**base, "normal": [0, 0, 1], "x_axis": [1, 0, 0]},
            {**base, "support_datum_plane_id": "DATUM_PLANE(4)", "distance_mm": 3.0},
            {**base, "axis_line_id": "LINE 31", "angle_deg": 20.0},
            {**base, "mode": "two_lines"},
            {**self.common(), "line1_id": "LINE 17"},
            {**self.common(), "line2_id": "LINE 23"},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_tool_input_error(arguments)


if __name__ == "__main__":
    unittest.main()
