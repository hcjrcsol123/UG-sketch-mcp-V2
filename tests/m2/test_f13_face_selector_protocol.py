"""Offline protocol checks for session-scoped face-tag disambiguation.

These checks exercise the public Python schema, validation, and bridge routing.
They do not instantiate NXOpen or claim native face-resolution evidence.
"""

from __future__ import annotations

from pathlib import Path
import sys
import unittest
from unittest.mock import Mock


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.bridge import BridgeFailure
from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class F13FaceSelectorProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={"plan_id": "face-plan-1", "kind": "create_sketch"})
        self.server._bridge.call = self.bridge_call

    @staticmethod
    def tool(name: str):
        return next(item for item in TOOLS if item["name"] == name)

    @staticmethod
    def sketch_face_request(**overrides):
        request = {
            "operation_id": "f13-face-sketch-1",
            "ownership_id": "owner-1",
            "binding_epoch": 13,
            "revision_id": "rev-13",
            "support_face_id": "FACE 9",
            "origin_mm": [1.0, 2.0, 3.0],
            "x_axis": [1.0, 0.0, 0.0],
            "geometries": [],
        }
        request.update(overrides)
        return request

    def call(self, name: str, arguments: dict):
        return self.server._tools_call(1, {"name": name, "arguments": arguments})

    def assert_invalid_without_bridge(self, name: str, arguments: dict):
        response = self.call(name, arguments)
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_face_tag_schema_is_optional_and_limited_to_explicit_face_references(self):
        sketch_schema = self.tool("nx_prepare_sketch")["inputSchema"]
        sketch_properties = sketch_schema["properties"]
        self.assertEqual(sketch_properties["support_face_tag"]["type"], "string")
        self.assertGreaterEqual(sketch_properties["support_face_tag"]["minLength"], 1)
        self.assertNotIn("support_face_tag", sketch_schema["required"])
        self.assertIs(sketch_schema["additionalProperties"], False)
        face_frame = next(
            mode for mode in sketch_schema["oneOf"]
            if "support_face_id" in mode["required"]
        )
        self.assertNotIn(
            {"required": ["support_face_tag"]},
            face_frame["not"].get("anyOf", []),
        )
        for mode in sketch_schema["oneOf"]:
            if mode is not face_frame:
                self.assertIn(
                    {"required": ["support_face_tag"]},
                    mode["not"].get("anyOf", []),
                )

        datum_schema = self.tool("nx_prepare_datum_plane")["inputSchema"]
        datum_properties = datum_schema["properties"]
        self.assertEqual(datum_properties["support_face_tag"]["type"], "string")
        self.assertNotIn("support_face_tag", datum_schema["required"])
        self.assertIs(datum_schema["additionalProperties"], False)
        face_reference_modes = [
            mode for mode in datum_schema["oneOf"]
            if "support_face_id" in mode["required"]
        ]
        self.assertEqual(len(face_reference_modes), 2)
        for mode in datum_schema["oneOf"]:
            if mode not in face_reference_modes:
                self.assertIn(
                    {"required": ["support_face_tag"]},
                    mode["not"].get("anyOf", []),
                )

        query_schema = self.tool("nx_query_faces")["inputSchema"]
        self.assertNotIn("face_tag", query_schema["properties"], "face_tag is candidate output, not a query selector")

    def test_explicit_support_face_tag_is_validated_and_forwarded_with_the_journal_id(self):
        arguments = self.sketch_face_request(support_face_tag="23041", support_body_id="BODY 2")
        response = self.call("nx_prepare_sketch", arguments)
        self.assertIs(response["result"]["isError"], False, response)
        self.bridge_call.assert_called_once_with("operation.prepare_sketch", arguments)

    def test_journal_id_only_explicit_face_request_remains_legacy_compatible(self):
        arguments = self.sketch_face_request()
        response = self.call("nx_prepare_sketch", arguments)
        self.assertIs(response["result"]["isError"], False, response)
        self.bridge_call.assert_called_once_with("operation.prepare_sketch", arguments)
        self.assertNotIn("support_face_tag", self.bridge_call.call_args.args[1])

    def test_face_tag_is_rejected_without_explicit_support_face_or_in_other_sketch_frames(self):
        common = {
            "operation_id": "f13-face-sketch-invalid-1",
            "ownership_id": "owner-1",
            "binding_epoch": 13,
            "revision_id": "rev-13",
        }
        invalid = [
            self.sketch_face_request(support_face_tag=""),
            self.sketch_face_request(support_face_tag=" \t "),
            self.sketch_face_request(support_face_tag=None),
            self.sketch_face_request(support_face_tag=23041),
            {**common, "support_face_tag": "23041", "support_face_selector": "top face"},
            {**common, "support_face_tag": "23041", "origin_mm": [0.0, 0.0, 0.0],
             "normal": [0.0, 0.0, 1.0], "x_axis": [1.0, 0.0, 0.0]},
            {**common, "support_face_tag": "23041", "plane": "XY", "origin_mm": [0.0, 0.0, 0.0]},
            {**common, "support_face_tag": "23041", "support_datum_plane_id": "DATUM_PLANE(2)",
             "x_axis": [1.0, 0.0, 0.0]},
            {**common, "support_face_tag": "23041", "origin_mm": [1.0, 2.0, 3.0],
             "x_axis": [1.0, 0.0, 0.0]},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_invalid_without_bridge("nx_prepare_sketch", arguments)

    def test_datum_plane_face_offset_and_angle_forward_tags(self):
        common = {
            "ownership_id": "owner-1",
            "binding_epoch": 13,
            "revision_id": "rev-13",
        }
        cases = [
            ({**common, "operation_id": "f13-face-offset-1", "support_face_id": "FACE 9",
              "support_face_tag": "23041", "distance_mm": -2.5},
             "operation.prepare_datum_plane"),
            ({**common, "operation_id": "f13-face-angle-1", "support_face_id": "FACE 9",
              "support_face_tag": "23041", "axis_line_id": "LINE 3", "angle_deg": 0.0},
             "operation.prepare_datum_plane"),
        ]
        for arguments, command in cases:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                response = self.call("nx_prepare_datum_plane", arguments)
                self.assertIs(response["result"]["isError"], False, response)
                self.bridge_call.assert_called_once_with(command, arguments)

    def test_datum_plane_rejects_face_tag_without_a_face_reference(self):
        common = {
            "operation_id": "f13-face-tag-invalid-datum-1",
            "ownership_id": "owner-1",
            "binding_epoch": 13,
            "revision_id": "rev-13",
        }
        invalid = [
            {**common, "support_face_tag": "23041", "support_datum_plane_id": "DATUM_PLANE(2)",
             "distance_mm": 1.0},
            {**common, "support_face_tag": "23041", "origin_mm": [0.0, 0.0, 0.0],
             "normal": [0.0, 0.0, 1.0], "x_axis": [1.0, 0.0, 0.0]},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_invalid_without_bridge("nx_prepare_datum_plane", arguments)

    def test_face_query_candidates_and_inspect_part_face_refs_are_preserved(self):
        query_arguments = {
            "ownership_id": "owner-1", "binding_epoch": 13, "revision_id": "rev-13",
            "selector": "all_planar", "body_id": "BODY 2",
        }
        query_result = {
            "selection_status": "all",
            "candidate_count": 2,
            "candidates": [
                {"face_journal_id": "FACE 9", "face_tag": "23041", "body_id": "BODY 2"},
                {"face_journal_id": "FACE 9", "face_tag": "23089", "body_id": "BODY 2"},
            ],
            "revision_id": "rev-13",
        }
        self.bridge_call.return_value = query_result
        query_response = self.call("nx_query_faces", query_arguments)
        self.assertIs(query_response["result"]["isError"], False, query_response)
        self.assertIs(query_response["result"]["structuredContent"], query_result)
        self.bridge_call.assert_called_once_with("model.query_faces", query_arguments)

        self.bridge_call.reset_mock()
        inspect_arguments = {"ownership_id": "owner-1", "binding_epoch": 13}
        inspect_result = {
            "bodies": [{
                "body_id": "BODY 2",
                "face_ids": ["FACE 9", "FACE 9", "FACE 12"],
                "face_refs": [
                    {"face_journal_id": "FACE 9", "face_tag": "23041", "body_id": "BODY 2", "body_tag": "9"},
                    {"face_journal_id": "FACE 9", "face_tag": "23089", "body_id": "BODY 2", "body_tag": "9"},
                    {"face_journal_id": "FACE 12", "face_tag": "23105", "body_id": "BODY 2", "body_tag": "9"},
                ],
            }],
        }
        self.bridge_call.return_value = inspect_result
        inspect_response = self.call("nx_inspect_part", inspect_arguments)
        self.assertIs(inspect_response["result"]["isError"], False, inspect_response)
        self.assertIs(inspect_response["result"]["structuredContent"], inspect_result)
        self.assertEqual(
            inspect_response["result"]["structuredContent"]["bodies"][0]["face_ids"],
            ["FACE 9", "FACE 9", "FACE 12"],
        )
        self.assertEqual(
            [item["face_tag"] for item in inspect_response["result"]["structuredContent"]["bodies"][0]["face_refs"]],
            ["23041", "23089", "23105"],
        )
        self.bridge_call.assert_called_once_with("model.inspect_part", inspect_arguments)

    def test_missing_tag_does_not_suppress_backend_ambiguity_rejection(self):
        arguments = self.sketch_face_request()
        self.bridge_call.side_effect = BridgeFailure(
            "SUPPORT_FACE_AMBIGUOUS",
            "support_face_id identifies more than one face; provide support_face_tag",
        )
        response = self.call("nx_prepare_sketch", arguments)
        result = response["result"]
        self.assertIs(result["isError"], True, response)
        self.assertEqual(result["structuredContent"]["error"]["code"], "SUPPORT_FACE_AMBIGUOUS")
        self.bridge_call.assert_called_once_with("operation.prepare_sketch", arguments)
        self.assertNotIn("support_face_tag", self.bridge_call.call_args.args[1])


if __name__ == "__main__":
    unittest.main()
