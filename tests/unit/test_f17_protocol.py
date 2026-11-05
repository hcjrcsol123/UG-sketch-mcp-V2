"""F17 MCP validation and unknown-outcome gating, with a fake bridge only."""
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))
from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.bridge import BridgeFailure
from nx_sketch_mcp.server import McpServer, TOOLS


class F17ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge = Mock(return_value={"state": "SUCCEEDED"})
        self.server._bridge.call = self.bridge
        self.base = {"operation_id":"op", "ownership_id":"owner", "binding_epoch":1,
                     "revision_id":"rev", "target_body_id":"solid", "edge_ids":["edge"],
                     "mode":"equal_distance", "reference_axis":"x", "distance_mm":2}

    def invoke(self, req):
        return self.server._tools_call(1, {"name":"nx_prepare_chamfer", "arguments":req})

    def test_schema_and_three_valid_modes_route(self):
        schema = next(t for t in TOOLS if t["name"] == "nx_prepare_chamfer")["inputSchema"]
        self.assertFalse(schema["additionalProperties"])
        self.assertEqual(schema["properties"]["edge_ids"]["maxItems"], 1)
        cases = [self.base, {**self.base, "mode":"two_distances", "second_distance_mm":4},
                 {**self.base, "mode":"distance_angle", "angle_deg":30}]
        for item in cases:
            self.assertNotIn("error", self.invoke(item))
            self.bridge.assert_called_with("operation.prepare_chamfer", item)

    def test_invalid_and_conflicting_modes_never_reach_bridge(self):
        cases = [{key:value for key,value in self.base.items() if key != name} for name in self.base]
        cases += [{**self.base, "extra":1}, {**self.base, "edge_ids":[]},
                  {**self.base, "edge_ids":["a", "b"]}, {**self.base, "edge_ids":[""]},
                  {**self.base, "mode":"two_distances"}, {**self.base, "mode":"distance_angle"},
                  {**self.base, "second_distance_mm":1}, {**self.base, "angle_deg":30},
                  {**self.base, "mode":"bad"}, {**self.base, "reference_axis":"X"},
                  {**self.base, "mode":"two_distances", "second_distance_mm":2, "angle_deg":30}]
        cases += [{**self.base, "distance_mm":n} for n in (0, -1, True, "2", float("nan"), float("inf"))]
        cases += [{**self.base, "mode":"distance_angle", "angle_deg":n} for n in (0, -1, 90, 180, True, float("nan"), float("inf"))]
        for req in cases:
            with self.subTest(req=req):
                self.assertEqual(self.invoke(req).get("error", {}).get("code"), -32602)
        self.bridge.assert_not_called()

    def test_unknown_outcome_freezes_followup(self):
        self.bridge.side_effect = BridgeFailure("BRIDGE_TIMEOUT", "response lost", outcome_unknown=True)
        self.assertTrue(self.invoke(self.base)["result"]["isError"])
        response = self.invoke({**self.base, "operation_id":"op2"})
        self.assertEqual(response["result"]["structuredContent"]["error"]["code"], "CLIENT_OUTCOME_UNKNOWN")
        self.assertEqual(self.bridge.call_count, 1)

if __name__ == "__main__":
    unittest.main()
