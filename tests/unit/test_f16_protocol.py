"""F16 protocol checks against the actual server; fake bridge, never NX."""
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, call

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))
from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.bridge import BridgeFailure
from nx_sketch_mcp.server import McpServer, TOOLS


class F16ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge = Mock()
        self.server._bridge.call = self.bridge
        self.prepare = {"operation_id": "prepare", "ownership_id": "owner", "binding_epoch": 1,
                        "revision_id": "12", "target_body_id": "body", "edge_ids": ["edge-a", "edge-b"],
                        "radius_mm": 2.0}

    def invoke(self, name, arguments):
        return self.server._tools_call(1, {"name": name, "arguments": arguments})

    def test_schema_is_closed_and_explicit(self):
        matches = [item for item in TOOLS if item["name"] == "nx_prepare_edge_blend"]
        self.assertEqual(len(matches), 1)
        schema = matches[0]["inputSchema"]
        self.assertEqual(set(schema["properties"]), set(self.prepare))
        self.assertEqual(set(schema["required"]), set(self.prepare))
        self.assertIs(schema["additionalProperties"], False)
        edges = schema["properties"]["edge_ids"]
        self.assertEqual((edges["minItems"], edges["maxItems"], edges["uniqueItems"]), (1, 32, True))
        self.assertEqual(schema["properties"]["radius_mm"]["type"], "number")

    def test_invalid_requests_fail_before_bridge(self):
        base = self.prepare
        invalid = [{key: value for key, value in base.items() if key != missing} for missing in base]
        invalid += [{**base, "extra": True}, {**base, "target_body_id": ""},
                    {**base, "binding_epoch": True}, {**base, "revision_id": " "},
                    {**base, "edge_ids": []}, {**base, "edge_ids": "edge"},
                    {**base, "edge_ids": ["edge", "edge"]}, {**base, "edge_ids": [""]},
                    {**base, "edge_ids": [f"edge-{i}" for i in range(33)]}]
        invalid += [{**base, "radius_mm": radius} for radius in (0, -1, True, "2", float("nan"),
                                                                   float("inf"), -float("inf"))]
        for request in invalid:
            with self.subTest(request=request):
                response = self.invoke("nx_prepare_edge_blend", request)
                self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge.assert_not_called()

    def test_prepare_single_and_multiple_edges_route_without_rewriting(self):
        self.bridge.return_value = {"operation_id": "prepare", "state": "SUCCEEDED"}
        for edges in (["edge-a"], ["edge-a", "edge-b"]):
            request = {**self.prepare, "edge_ids": edges}
            self.assertNotIn("error", self.invoke("nx_prepare_edge_blend", request))
            self.bridge.assert_called_with("operation.prepare_edge_blend", request)
        self.assertEqual(self.bridge.call_count, 2)

    def test_prepare_unknown_blocks_new_model_writes_without_replay(self):
        self.bridge.side_effect = BridgeFailure("BRIDGE_TIMEOUT", "response lost", outcome_unknown=True)
        first = self.invoke("nx_prepare_edge_blend", self.prepare)
        self.assertTrue(first["result"].get("isError"), first)
        for request in (self.prepare, {**self.prepare, "operation_id": "new"}):
            response = self.invoke("nx_prepare_edge_blend", request)
            self.assertTrue(response["result"].get("isError"), response)
            self.assertEqual(response["result"]["structuredContent"]["error"]["code"], "CLIENT_OUTCOME_UNKNOWN")
        self.assertEqual(self.bridge.call_args_list, [call("operation.prepare_edge_blend", self.prepare)])


if __name__ == "__main__":
    unittest.main()
