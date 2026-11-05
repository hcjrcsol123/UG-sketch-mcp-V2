"""Focused Python protocol checks for F08 sketch offset tools."""

from __future__ import annotations

import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS
from stdio_client import StdioMcpProcess


class F08SketchOffsetProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge_call = Mock(return_value={"plan_id": "offset-plan-1", "kind": "edit_sketch_offset"})
        self.server._bridge.call = self.bridge_call

    @staticmethod
    def common() -> dict:
        return {
            "operation_id": "f08-offset-1",
            "ownership_id": "owner-1",
            "binding_epoch": 12,
            "revision_id": "rev-20",
            "sketch_id": "sketch-1",
            "curve_chain": [
                {"curve_id": "line-1", "direction": "forward"},
                {"curve_id": "line-2", "direction": "reverse"},
            ],
            "distance_mm": 2.0,
            "side": "left",
            "retain_original": True,
            "join_mode": "extension",
        }

    def call(self, name: str, arguments: dict):
        return self.server._tools_call(1, {"name": name, "arguments": arguments})

    def assert_input_error(self, name: str, arguments: dict):
        response = self.call(name, arguments)
        self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge_call.assert_not_called()

    def test_tools_schema_and_capabilities_freeze_ordered_side_semantics(self):
        tools = {item["name"]: item for item in TOOLS}
        self.assertEqual(len(TOOLS), len(tools))
        for name in ("nx_prepare_sketch_offset", "nx_edit_sketch_offset"):
            self.assertIn(name, tools)
            schema = tools[name]["inputSchema"]
            self.assertEqual(set(schema["required"]), {
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                "curve_chain", "distance_mm", "side", "retain_original", "join_mode",
            })
            self.assertFalse(schema["additionalProperties"])
            self.assertEqual(schema["properties"]["side"]["enum"], ["left", "right"])
            self.assertEqual(schema["properties"]["join_mode"]["enum"], ["extension", "arc"])
            chain = schema["properties"]["curve_chain"]
            self.assertEqual(chain["minItems"], 1)
            self.assertEqual(chain["items"]["properties"]["direction"]["enum"], ["forward", "reverse"])
            self.assertIs(schema["properties"]["retain_original"]["const"], True)
            self.assertIn("ordered", tools[name]["description"])
            self.assertIn("sketch plane", tools[name]["description"])
        caps = self.server._capabilities()["model_native_geometry_support"]["sketch_offset"]
        self.assertEqual(caps["prepare_operation"], "operation.prepare_sketch_offset")
        self.assertEqual(caps["edit_operation"], "operation.edit_sketch_offset")
        self.assertEqual(caps["supported_curve_types"], ["LINE", "ARC"])
        self.assertEqual(caps["distance_unit"], "mm; positive finite only")
        self.assertIn("each entry specifies forward or reverse", caps["selection"])
        self.assertIn("degenerate native connector arcs are rejected", caps["join_modes"]["arc"])
        self.assertEqual(tools["nx_prepare_sketch_offset"]["annotations"]["readOnlyHint"], True)
        self.assertEqual(tools["nx_edit_sketch_offset"]["annotations"]["destructiveHint"], True)

    def test_prepare_forwards_ordered_chain_and_explicit_join_contract(self):
        arguments = self.common()
        response = self.call("nx_prepare_sketch_offset", arguments)
        self.assertFalse(response["result"]["isError"], response)
        self.bridge_call.assert_called_once_with("operation.prepare_sketch_offset", arguments)

    def test_combined_edit_forwards_one_request_and_only_verified_compound_result_is_success(self):
        arguments = self.common()
        self.bridge_call.return_value = {
            "state": "SUCCEEDED", "kind": "execute_edit_sketch_offset",
            "result": {"combined_edit": True, "verified": True, "mutation_state": "APPLIED"},
        }
        succeeded_response = self.call("nx_edit_sketch_offset", arguments)["result"]
        self.bridge_call.assert_called_once_with("operation.edit_sketch_offset", arguments)
        succeeded = succeeded_response["structuredContent"]
        self.assertIs(succeeded_response.get("isError"), False, succeeded_response)
        self.assertEqual(succeeded.get("state"), "SUCCEEDED", succeeded)
        self.assertIs(succeeded.get("success"), True, succeeded)
        self.assertIs(succeeded.get("verified"), True, succeeded)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "state": "FAILED", "kind": "execute_edit_sketch_offset",
            "result": {"verified": False, "mutation_state": "APPLIED", "evidence": {"checks": []}},
            "error": {"code": "VERIFY_MISMATCH", "message": "native offset readback mismatch"},
        }
        failed_response = self.call("nx_edit_sketch_offset", arguments)["result"]
        self.assertIs(failed_response.get("isError"), True, failed_response)
        failed = failed_response["structuredContent"]
        self.assertEqual(failed.get("error", {}).get("code"), "VERIFY_MISMATCH", failed)
        self.assertIs(failed.get("outcome_unknown"), False, failed)

        self.bridge_call.reset_mock()
        self.bridge_call.return_value = {
            "state": "UNKNOWN", "kind": "execute_edit_sketch_offset",
            "result": {"combined_edit": True, "verified": False, "mutation_state": "UNKNOWN"},
            "error": {"code": "OPERATION_EXECUTION_TIMEOUT", "message": "timed out"},
        }
        unknown_response = self.call("nx_edit_sketch_offset", arguments)["result"]
        unknown = unknown_response["structuredContent"]
        self.assertTrue(unknown.get("outcome_unknown"), unknown)
        self.assertEqual(unknown.get("next_action"), "nx_get_operation", unknown)
        self.assertIn(arguments["operation_id"], self.server._unknown_operation_ids)

        self.bridge_call.reset_mock()
        blocked = self.call("nx_edit_sketch_offset", {**arguments, "operation_id": "f08-next-write"})["result"]
        blocked_value = blocked["structuredContent"]
        self.assertEqual(blocked_value.get("error", {}).get("code"), "CLIENT_OUTCOME_UNKNOWN", blocked_value)
        self.bridge_call.assert_not_called()

    def test_operation_poll_formats_offset_unknown_without_unfreezing(self):
        self.bridge_call.return_value = {
            "ok": True, "operation_id": "f08-poll-unknown", "kind": "execute_edit_sketch_offset",
            "state": "UNKNOWN", "result": {"verified": False, "mutation_state": "UNKNOWN"},
            "error": {"code": "OPERATION_EXECUTION_TIMEOUT", "message": "unresolved"},
            "writes_halted": True,
        }
        response = self.call("nx_get_operation", {"operation_id": "f08-poll-unknown"})["result"]
        value = response["structuredContent"]
        self.assertIs(response.get("isError"), False, response)
        self.assertEqual(value.get("state"), "UNKNOWN", value)
        self.assertTrue(value.get("outcome_unknown"), value)
        self.assertEqual(value.get("next_action"), "nx_get_operation", value)
        self.assertTrue(value.get("operation", {}).get("writes_halted"), value)
        self.assertIn("f08-poll-unknown", self.server._unknown_operation_ids)

    def test_exact_fields_ranges_and_chain_shape_are_validated_before_bridge(self):
        valid = self.common()
        invalid = [
            {**valid, "retain_original": False},
            {**valid, "distance_mm": 0.0},
            {**valid, "distance_mm": -1.0},
            {**valid, "distance_mm": float("inf")},
            {**valid, "distance_mm": float("nan")},
            {**valid, "side": "inside"},
            {**valid, "join_mode": "miter"},
            {**valid, "curve_chain": []},
            {**valid, "curve_chain": [{"curve_id": "line-1", "direction": "FORWARD"}]},
            {**valid, "curve_chain": [{"curve_id": "line-1", "direction": "forward", "extra": 1}]},
            {**valid, "curve_chain": [{"curve_id": "", "direction": "forward"}]},
            {**valid, "curve_chain": [
                {"curve_id": "line-1", "direction": "forward"},
                {"curve_id": "line-1", "direction": "reverse"},
            ]},
            {**valid, "unexpected": True},
            {key: value for key, value in valid.items() if key != "curve_chain"},
        ]
        for arguments in invalid:
            with self.subTest(arguments=arguments):
                self.bridge_call.reset_mock()
                self.assert_input_error("nx_prepare_sketch_offset", arguments)

    def test_nonfinite_json_parse_error_uses_null_id_and_negative_number_stops_before_bridge(self):
        with tempfile.TemporaryDirectory(prefix="f08-mcp-protocol-") as temp_dir:
            config_path = Path(temp_dir) / "empty-config.json"
            config_path.write_text("{}\n", encoding="utf-8")
            env = os.environ.copy()
            paths = [str(PROJECT_ROOT / "src"), str(PROJECT_ROOT)]
            existing = env.get("PYTHONPATH", "")
            if existing:
                paths.append(existing)
            env["PYTHONPATH"] = os.pathsep.join(paths)
            env["PYTHONUTF8"] = "1"
            child = StdioMcpProcess(
                [sys.executable, "-m", "nx_sketch_mcp", "--config", str(config_path)],
                cwd=str(PROJECT_ROOT), env=env,
            )
            try:
                child.request("initialize", {
                    "protocolVersion": "2025-11-25",
                    "capabilities": {},
                    "clientInfo": {"name": "f08-raw-json-test", "version": "1"},
                })
                child.notification("notifications/initialized")

                malformed_arguments = self.common()
                malformed_arguments["distance_mm"] = float("inf")
                malformed = {
                    "jsonrpc": "2.0", "id": 77, "method": "tools/call",
                    "params": {"name": "nx_prepare_sketch_offset", "arguments": malformed_arguments},
                }
                parse_error = child.exchange(malformed, expected_id=None)
                self.assertEqual(parse_error.get("error", {}).get("code"), -32700, parse_error)
                self.assertIsNone(parse_error.get("id"), parse_error)

                finite_negative = self.common()
                finite_negative["distance_mm"] = -1.0
                rejected = child.request("tools/call", {
                    "name": "nx_prepare_sketch_offset", "arguments": finite_negative,
                })
                self.assertEqual(rejected.get("error", {}).get("code"), -32602, rejected)
                self.assertNotIn("BRIDGE_NOT_CONFIGURED", str(rejected))
            finally:
                child.terminate_own_process()


if __name__ == "__main__":
    unittest.main()
