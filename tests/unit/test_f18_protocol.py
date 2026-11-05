"""F18 validation, capability reporting, and outcome gating with a fake bridge."""
from dataclasses import replace
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))
from nx_sketch_mcp.bridge import BridgeFailure, JsonlBridge
from nx_sketch_mcp.config import BridgeConfig, config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class FeaturePatternProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge = Mock(return_value={"state": "PREPARED"})
        self.server._bridge.call = self.bridge
        self.common = {"operation_id": "op", "ownership_id": "owner", "binding_epoch": 1,
                       "revision_id": "rev", "seed_feature_id": "feature", "count1": 3}
        self.linear = {**self.common, "mode": "linear", "direction1": [1, 0, 0], "pitch1_mm": 2}
        self.rectangular = {**self.linear, "mode": "rectangular", "direction2": [0, 1, 0],
                            "pitch2_mm": 3, "count2": 4}
        self.circular = {**self.common, "mode": "circular", "axis_origin_mm": [4, 5, 6],
                         "axis_direction": [0, 0, 1], "angle_deg": -45}

    def invoke(self, req):
        return self.server._tools_call(1, {"name": "nx_prepare_feature_pattern", "arguments": req})

    def assert_invalid(self, req):
        with self.subTest(req=req):
            self.assertEqual(self.invoke(req).get("error", {}).get("code"), -32602)

    def test_schema_and_all_modes_route_unchanged(self):
        schema = next(tool for tool in TOOLS if tool["name"] == "nx_prepare_feature_pattern")["inputSchema"]
        self.assertFalse(schema["additionalProperties"])
        self.assertEqual(len(schema["oneOf"]), 3)
        self.assertEqual(schema["properties"]["count1"]["maximum"], 32)
        for req in (self.linear, self.rectangular, self.circular,
                    {**self.rectangular, "count1": 8, "count2": 4},
                    {**self.circular, "angle_deg": 179.999}):
            with self.subTest(req=req):
                response = self.invoke(req)
                self.assertNotIn("error", response)
                self.assertFalse(response["result"].get("isError", False))
                self.bridge.assert_called_with("operation.prepare_feature_pattern", req)

    def test_real_jsonl_bridge_dispatches_f18_command(self):
        # Exercise the real command allowlist and request construction. The
        # helper I/O itself is mocked; no NX or socket is used by this test.
        with tempfile.TemporaryDirectory() as temp:
            token = Path(temp) / "dummy-token"
            token.write_text("test-only", encoding="utf-8")
            bridge = JsonlBridge(BridgeConfig("test-pipe", token, Path(temp) / "unused-helper"))
            bridge._ensure_process = Mock(return_value=(object(), object()))
            bridge._write_request = Mock()
            bridge._read_response = Mock(return_value={"result": {"state": "SUCCEEDED", "operation_id": "op"}})
            self.server._bridge = bridge
            response = self.invoke(self.linear)
            self.assertNotIn("error", response)
            self.assertFalse(response["result"].get("isError", False))
            self.assertEqual(response["result"]["structuredContent"]["state"], "SUCCEEDED")
            bridge._ensure_process.assert_called_once()
            bridge._write_request.assert_called_once()
            sent = bridge._write_request.call_args.args[1]
            self.assertEqual(sent["command"], "operation.prepare_feature_pattern")
            self.assertEqual(sent["params"], self.linear)

    def test_missing_conflicting_and_extra_fields(self):
        cases = []
        for req in (self.linear, self.rectangular, self.circular):
            cases.extend({key: value for key, value in req.items() if key != missing} for missing in req)
            cases.append({**req, "unexpected": 1})
        cases += [{**self.linear, "axis_direction": [0, 0, 1]},
                  {**self.circular, "direction1": [1, 0, 0]},
                  {**self.rectangular, "angle_deg": 1}, {**self.linear, "mode": []},
                  {**self.linear, "mode": "bad"}]
        for case in cases:
            self.assert_invalid(case)
        self.bridge.assert_not_called()

    def test_numeric_vectors_angles_and_count_boundaries(self):
        cases = [{**self.linear, "count1": value} for value in (True, 1, 33, 2.0, "2")]
        cases += [{**self.rectangular, "count2": value} for value in (False, 1, 33, 2.0)]
        cases += [{**self.rectangular, "count1": 11, "count2": 3}]
        cases += [{**self.linear, "pitch1_mm": value} for value in (0, -1, True, float("inf"), float("nan"))]
        cases += [{**self.rectangular, "pitch2_mm": value} for value in (0, -1, True, float("inf"))]
        for field, req in (("direction1", self.linear), ("direction2", self.rectangular),
                           ("axis_direction", self.circular), ("axis_origin_mm", self.circular)):
            for value in ([], [1, 2], [1, 2, 3, 4], [True, 0, 0], [float("inf"), 0, 0], [float("nan"), 0, 0]):
                cases.append({**req, field: value})
        cases += [{**self.linear, "direction1": [0, 0, 0]},
                  {**self.linear, "direction1": [1.000000002, 0, 0]},
                  {**self.rectangular, "direction2": [1, 0, 0]},
                  {**self.circular, "axis_direction": [0, 0, 2]}]
        cases += [{**self.circular, "angle_deg": value} for value in (0, 180, -180, 181, True, float("nan"), float("inf"))]
        for case in cases:
            self.assert_invalid(case)
        self.bridge.assert_not_called()
        for req in ({**self.linear, "direction1": [1.0000000005, 0, 0]},
                    {**self.rectangular, "direction2": [5e-10, 1, 0]},
                    {**self.circular, "angle_deg": -179.999}):
            self.assertNotIn("error", self.invoke(req))

    def test_id_bounds_and_epoch(self):
        for field, maximum in (("operation_id", 128), ("ownership_id", 128),
                               ("revision_id", 128), ("seed_feature_id", 4096)):
            for value in ("", "x" * (maximum + 1), "\ud800"):
                self.assert_invalid({**self.linear, field: value})
        for value in (True, 0, -1, 1.0):
            self.assert_invalid({**self.linear, "binding_epoch": value})
        self.bridge.assert_not_called()

    def test_unknown_bridge_failure_and_unknown_response_halt(self):
        for outcome in (BridgeFailure("BRIDGE_TIMEOUT", "response lost", outcome_unknown=True), {"state": "UNKNOWN"}):
            with self.subTest(outcome=outcome):
                self.server._unknown_operation_ids.clear()
                self.bridge.reset_mock(side_effect=True, return_value=True)
                if isinstance(outcome, Exception):
                    self.bridge.side_effect = outcome
                else:
                    self.bridge.return_value = outcome
                self.invoke(self.linear)
                response = self.invoke({**self.linear, "operation_id": "op2"})
                self.assertEqual(response["result"]["structuredContent"]["error"]["code"], "CLIENT_OUTCOME_UNKNOWN")
                self.assertEqual(self.bridge.call_count, 1)

    def test_unknown_remains_sticky_after_terminal_status_and_bridge_unhalt(self):
        self.server._unknown_operation_ids.add("op")
        query = {"name": "nx_get_operation", "arguments": {"operation_id": "op"}}
        for response in ({"state": "UNKNOWN", "writes_halted": False},
                         {"state": "SUCCEEDED", "writes_halted": True}):
            self.bridge.return_value = response
            self.server._tools_call(1, query)
            self.assertIn("op", self.server._unknown_operation_ids)
        self.bridge.return_value = {"state": "SUCCEEDED", "writes_halted": False}
        self.server._tools_call(1, query)
        self.assertIn("op", self.server._unknown_operation_ids)
        self.bridge.return_value = {"state": "PREPARED"}
        blocked = self.invoke({**self.linear, "operation_id": "op2"})
        self.assertEqual(blocked["result"]["structuredContent"]["error"]["code"], "CLIENT_OUTCOME_UNKNOWN")
        self.assertEqual([call.args[0] for call in self.bridge.call_args_list], ["operation.get"] * 3)

    def test_capabilities_require_adapter_write_and_global_write(self):
        cap = self.server._capabilities()
        self.assertFalse(cap["model_feature_pattern_adapter_available"])
        self.assertNotIn("operation.prepare_feature_pattern", cap["available_operations"])
        self.server._config = replace(self.server._config, pipe_name="test-pipe")
        status = {"ownership_state": "AI_OWNED", "model_read_available": True, "model_write_enabled": True,
                  "writes_halted": False, "model_feature_pattern_adapter_available": True,
                  "model_feature_pattern_write_enabled": True}
        self.bridge.return_value = status
        cap = self.server._capabilities()
        self.assertTrue(cap["model_feature_pattern_adapter_available"])
        self.assertTrue(cap["model_feature_pattern_write_enabled"])
        self.assertIn("operation.prepare_feature_pattern", cap["available_operations"])
        self.assertIn("operation.prepare_feature_pattern", cap["model_native_geometry_support"]["prepare_operations"])
        for field in ("model_feature_pattern_adapter_available", "model_feature_pattern_write_enabled", "model_write_enabled"):
            self.bridge.return_value = {**status, field: False}
            self.assertFalse(self.server._capabilities()["model_feature_pattern_write_enabled"])
        self.bridge.return_value = status
        self.server._unknown_operation_ids.add("op")
        self.assertFalse(self.server._capabilities()["model_feature_pattern_write_enabled"])
        self.assertFalse(self.server._with_local_write_halt(status)["model_feature_pattern_write_enabled"])


if __name__ == "__main__":
    unittest.main()
