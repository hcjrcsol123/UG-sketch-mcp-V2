"""Offline F20 protocol and transport checks; no NX or helper is started."""
from dataclasses import replace
from pathlib import Path
import math
import sys
import tempfile
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))
from nx_sketch_mcp.bridge import BridgeFailure, JsonlBridge
from nx_sketch_mcp.config import BridgeConfig, config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


class SpatialCurveProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge = Mock(return_value={"state": "PREPARED"})
        self.server._bridge.call = self.bridge
        self.common = {"operation_id":"op", "ownership_id":"owner", "binding_epoch":1, "revision_id":"1"}
        self.line = {**self.common, "curve_type":"line", "start_mm":[1,2,3], "end_mm":[4,6,8]}
        self.center = {**self.common, "curve_type":"arc", "definition_mode":"center", "center_mm":[2,3,4],
                       "normal":[0,0,1], "x_axis":[1,0,0], "radius_mm":4, "start_angle_deg":20, "end_angle_deg":-230}
        self.three = {**self.common, "curve_type":"arc", "definition_mode":"three_points", "start_mm":[1,0,0],
                      "through_mm":[0,-1,0], "end_mm":[0,1,0]}
        self.circle = {**self.common, "curve_type":"circle", "center_mm":[2,3,4], "normal":[0,0,1], "x_axis":[1,0,0], "radius_mm":4}

    def invoke(self, args):
        return self.server._tools_call(1, {"name":"nx_prepare_spatial_curve", "arguments":args})

    def test_schema_and_all_four_modes(self):
        schema = next(t for t in TOOLS if t["name"] == "nx_prepare_spatial_curve")["inputSchema"]
        self.assertFalse(schema["additionalProperties"])
        self.assertEqual(len(schema["oneOf"]), 4)
        for request in (self.line, self.center, self.three, self.circle):
            with self.subTest(kind=request["curve_type"], mode=request.get("definition_mode")):
                self.assertFalse(self.invoke(request)["result"].get("isError", False))
                self.bridge.assert_called_with("operation.prepare_spatial_curve", request)

    def test_actual_jsonl_allowlist_and_arguments(self):
        with tempfile.TemporaryDirectory() as temp:
            token_path = Path(temp) / "test-token"
            token_path.write_text("local-test", encoding="utf-8")
            bridge = JsonlBridge(BridgeConfig("test-pipe", token_path, Path(temp) / "unused-helper"))
            bridge._ensure_process = Mock(return_value=(object(), object()))
            bridge._write_request = Mock()
            bridge._read_response = Mock(return_value={"result":{"state":"PREPARED"}})
            self.server._bridge = bridge
            for request in (self.line, self.center, self.three, self.circle):
                self.assertFalse(self.invoke(request)["result"].get("isError", False))
                sent = bridge._write_request.call_args.args[1]
                self.assertEqual(sent["command"], "operation.prepare_spatial_curve")
                self.assertEqual(sent["params"], request)
            self.assertEqual(bridge._write_request.call_count, 4)

    def test_rejects_missing_mixed_numerical_and_degenerate_inputs(self):
        bad = []
        for request in (self.line, self.center, self.three, self.circle):
            bad.extend({k:v for k,v in request.items() if k != missing} for missing in request)
            bad.append({**request, "unexpected":1})
        bad += [{**self.line, "center_mm":[0,0,0]}, {**self.line, "definition_mode":"center"},
                {**self.center, "start_mm":[0,0,0]}, {**self.three, "radius_mm":1},
                {**self.circle, "start_angle_deg":0}, {**self.circle, "definition_mode":"center"},
                {**self.line, "end_mm":[1,2,3]}, {**self.three, "through_mm":[0.5,0.5,0]},
                {**self.three, "through_mm":[1,0,0]}, {**self.center, "normal":[0,0,2]},
                {**self.center, "x_axis":[0,0,1]}, {**self.center, "radius_mm":0},
                {**self.center, "radius_mm":math.inf}, {**self.center, "start_angle_deg":math.nan},
                {**self.center, "end_angle_deg":20}, {**self.center, "end_angle_deg":380},
                {**self.line, "start_mm":[True,0,0]}, {**self.line, "end_mm":[1e10,0,0]},
                {**self.line, "binding_epoch":True}]
        for request in bad:
            with self.subTest(fields=tuple(request)):
                self.assertEqual(self.invoke(request).get("error", {}).get("code"), -32602)
        self.bridge.assert_not_called()

    def test_unknown_gate_blocks_next_call(self):
        for outcome in (BridgeFailure("BRIDGE_TIMEOUT", "lost", outcome_unknown=True), {"state":"UNKNOWN"}):
            self.server._unknown_operation_ids.clear()
            self.bridge.reset_mock(side_effect=True, return_value=True)
            if isinstance(outcome, Exception): self.bridge.side_effect=outcome
            else: self.bridge.return_value=outcome
            self.invoke(self.line)
            result=self.invoke({**self.circle, "operation_id":"next"})
            self.assertEqual(result["result"]["structuredContent"]["error"]["code"], "CLIENT_OUTCOME_UNKNOWN")
            self.assertEqual(self.bridge.call_count,1)

    def test_capability_and_local_halt(self):
        self.assertFalse(self.server._capabilities()["model_spatial_curve_adapter_available"])
        self.server._config=replace(self.server._config, pipe_name="test-pipe")
        status={"ownership_state":"AI_OWNED", "model_read_available":True, "model_write_enabled":True,
                "writes_halted":False, "model_spatial_curve_adapter_available":True, "model_spatial_curve_write_enabled":True}
        self.bridge.return_value=status
        cap=self.server._capabilities()
        self.assertTrue(cap["model_spatial_curve_write_enabled"])
        self.assertIn("operation.prepare_spatial_curve",cap["available_operations"])
        self.server._unknown_operation_ids.add("op")
        self.assertFalse(self.server._capabilities()["model_spatial_curve_write_enabled"])
        self.assertFalse(self.server._with_local_write_halt(status)["model_spatial_curve_write_enabled"])


if __name__ == "__main__": unittest.main()
