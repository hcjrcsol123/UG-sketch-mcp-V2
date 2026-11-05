"""Offline F19 protocol, transport allowlist and outcome gating tests."""
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


class FeatureMirrorProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge = Mock(return_value={"state": "PREPARED"})
        self.server._bridge.call = self.bridge
        self.common = {"operation_id": "op", "ownership_id": "owner", "binding_epoch": 1,
                       "revision_id": "rev", "seed_feature_id": "feature"}
        self.math = {**self.common, "plane_origin_mm": [1, 2, 3], "plane_normal": [0, -1, 0]}
        self.datum = {**self.common, "datum_plane_id": "plane"}

    def invoke(self, request):
        return self.server._tools_call(1, {"name": "nx_prepare_feature_mirror", "arguments": request})

    def test_schema_and_both_modes(self):
        schema = next(t for t in TOOLS if t["name"] == "nx_prepare_feature_mirror")["inputSchema"]
        self.assertFalse(schema["additionalProperties"])
        self.assertEqual(len(schema["oneOf"]), 2)
        self.assertEqual(schema["properties"]["plane_normal"]["not"], {"const": [0, 0, 0]})
        for request in (self.math, self.datum):
            with self.subTest(request=request):
                result = self.invoke(request)
                self.assertNotIn("error", result)
                self.assertFalse(result["result"].get("isError", False))
                self.bridge.assert_called_with("operation.prepare_feature_mirror", request)
        within_tolerance = {**self.math, "plane_normal": [1.0000000005, 0, 0]}
        self.assertNotIn("error", self.invoke(within_tolerance))
        self.bridge.assert_called_with("operation.prepare_feature_mirror", within_tolerance)

    def test_real_jsonl_bridge_command_and_arguments(self):
        # Exercise the actual allowlist and serialization boundary without a helper or NX.
        with tempfile.TemporaryDirectory() as temp:
            token = Path(temp) / "dummy-token"
            token.write_text("test-only", encoding="utf-8")
            bridge = JsonlBridge(BridgeConfig("test-pipe", token, Path(temp) / "unused-helper"))
            bridge._ensure_process = Mock(return_value=(object(), object()))
            bridge._write_request = Mock()
            bridge._read_response = Mock(return_value={"result": {"state": "PREPARED"}})
            self.server._bridge = bridge
            for request in (self.math, self.datum):
                response = self.invoke(request)
                self.assertNotIn("error", response)
                self.assertFalse(response["result"].get("isError", False))
                sent = bridge._write_request.call_args.args[1]
                self.assertEqual(sent["command"], "operation.prepare_feature_mirror")
                self.assertEqual(sent["params"], request)
            self.assertEqual(bridge._write_request.call_count, 2)

    def test_invalid_fields_modes_vectors_and_ids(self):
        cases = []
        for request in (self.math, self.datum):
            cases.extend({k: v for k, v in request.items() if k != missing} for missing in request)
            cases.append({**request, "unexpected": 1})
        cases.extend([{**self.math, "datum_plane_id": "plane"},
                      {**self.datum, "plane_origin_mm": [0, 0, 0]},
                      {**self.datum, "plane_normal": [0, 0, 1]}])
        for field in ("plane_origin_mm", "plane_normal"):
            for value in ([], [1, 2], [1, 2, 3, 4], [True, 0, 0],
                          [float("inf"), 0, 0], [float("nan"), 0, 0], "123"):
                cases.append({**self.math, field: value})
        cases.extend({**self.math, "plane_normal": value} for value in
                     ([0, 0, 0], [0, -2, 0], [1.000000002, 0, 0], [1e-300, 0, 0], [1e300, 0, 0]))
        for field, limit in (("operation_id", 128), ("ownership_id", 128), ("revision_id", 128),
                             ("seed_feature_id", 4096), ("datum_plane_id", 4096)):
            request = self.datum if field == "datum_plane_id" else self.math
            for value in ("", "x" * (limit + 1), "\ud800"):
                cases.append({**request, field: value})
        cases.extend({**self.math, "binding_epoch": value} for value in (True, 0, -1, 1.0))
        for request in cases:
            with self.subTest(request=request):
                self.assertEqual(self.invoke(request).get("error", {}).get("code"), -32602)
        self.bridge.assert_not_called()

    def test_unknown_response_and_transport_failure_halt_following_prepares(self):
        for outcome in (BridgeFailure("BRIDGE_TIMEOUT", "response lost", outcome_unknown=True), {"state": "UNKNOWN"}):
            with self.subTest(outcome=outcome):
                self.server._unknown_operation_ids.clear()
                self.bridge.reset_mock(side_effect=True, return_value=True)
                if isinstance(outcome, Exception):
                    self.bridge.side_effect = outcome
                else:
                    self.bridge.return_value = outcome
                self.invoke(self.math)
                result = self.invoke({**self.datum, "operation_id": "op2"})
                self.assertEqual(result["result"]["structuredContent"]["error"]["code"], "CLIENT_OUTCOME_UNKNOWN")
                self.assertEqual(self.bridge.call_count, 1)

    def test_capability_status_and_local_halt(self):
        cap = self.server._capabilities()
        self.assertFalse(cap["model_feature_mirror_adapter_available"])
        self.assertNotIn("operation.prepare_feature_mirror", cap["available_operations"])
        self.server._config = replace(self.server._config, pipe_name="test-pipe")
        status = {"ownership_state": "AI_OWNED", "model_read_available": True, "model_write_enabled": True,
                  "writes_halted": False, "model_feature_mirror_adapter_available": True,
                  "model_feature_mirror_write_enabled": True}
        self.bridge.return_value = status
        cap = self.server._capabilities()
        self.assertTrue(cap["model_feature_mirror_write_enabled"])
        self.assertIn("operation.prepare_feature_mirror", cap["available_operations"])
        self.assertIn("operation.prepare_feature_mirror", cap["model_native_geometry_support"]["prepare_operations"])
        for field in ("model_feature_mirror_adapter_available", "model_feature_mirror_write_enabled", "model_write_enabled"):
            self.bridge.return_value = {**status, field: False}
            self.assertFalse(self.server._capabilities()["model_feature_mirror_write_enabled"])
        self.bridge.return_value = status
        self.server._unknown_operation_ids.add("op")
        self.assertFalse(self.server._capabilities()["model_feature_mirror_write_enabled"])
        self.assertFalse(self.server._with_local_write_halt(status)["model_feature_mirror_write_enabled"])


if __name__ == "__main__":
    unittest.main()
