"""F15 protocol and fake-bridge controls; no NX, helper, or model is launched."""
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock, call

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))
from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.bridge import BridgeFailure
from nx_sketch_mcp.server import McpServer, TOOLS


class F15ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.bridge = Mock()
        self.server._bridge.call = self.bridge
        self.prepare = {"operation_id": "prepare", "ownership_id": "owner", "binding_epoch": 1,
                        "revision_id": "12", "operation": "intersect", "target_body_id": "target",
                        "tool_body_ids": ["tool-a", "tool-b"], "retain_tools": True}
        self.execute = {"operation_id": "execute", "ownership_id": "owner", "binding_epoch": 1, "plan_id": "plan"}
        self.verify = {"operation_id": "verify", "ownership_id": "owner", "binding_epoch": 1,
                       "execute_operation_id": "execute"}

    def invoke(self, name, arguments):
        return self.server._tools_call(1, {"name": name, "arguments": arguments})

    def test_actual_schema_is_closed_and_requires_unique_nonempty_tools_and_bool(self):
        tool = next(item for item in TOOLS if item["name"] == "nx_prepare_boolean")
        schema = tool["inputSchema"]
        self.assertEqual(set(schema["properties"]), set(self.prepare))
        self.assertEqual(set(schema["required"]), set(self.prepare))
        self.assertIs(schema["additionalProperties"], False)
        self.assertEqual(set(schema["properties"]["operation"]["enum"]), {"unite", "subtract", "intersect"})
        self.assertEqual(schema["properties"]["retain_tools"]["type"], "boolean")
        tools = schema["properties"]["tool_body_ids"]
        self.assertEqual((tools["minItems"], tools["maxItems"], tools["uniqueItems"]), (1, 32, True))

    def test_invalid_inputs_rejected_before_bridge(self):
        base = self.prepare
        invalid = [{key: value for key, value in base.items() if key != missing} for missing in base]
        invalid += [{**base, "extra": 1}, {**base, "operation": "union"}, {**base, "operation": True},
                    {**base, "retain_tools": 1}, {**base, "retain_tools": "false"},
                    {**base, "binding_epoch": True}, {**base, "revision_id": " "},
                    {**base, "target_body_id": ""}, {**base, "tool_body_ids": []},
                    {**base, "tool_body_ids": "tool-a"}, {**base, "tool_body_ids": ["tool-a", "tool-a"]},
                    {**base, "tool_body_ids": ["target"]}, {**base, "tool_body_ids": [""]},
                    {**base, "tool_body_ids": [f"tool-{i}" for i in range(33)]}]
        for request in invalid:
            with self.subTest(request=request):
                response = self.invoke("nx_prepare_boolean", request)
                self.assertEqual(response.get("error", {}).get("code"), -32602, response)
        self.bridge.assert_not_called()

    def test_prepare_execute_and_fresh_verify_are_routed_with_exact_arguments(self):
        replies = [{"operation_id": "prepare", "state": "SUCCEEDED", "result": {"plan": {"plan_id": "plan"}}},
                   {"operation_id": "execute", "state": "SUCCEEDED", "result": {"plan_id": "plan", "mutation_state": "APPLIED"}},
                   {"operation_id": "verify", "state": "SUCCEEDED", "result": {"execute_operation_id": "execute", "verified": True}}]
        self.bridge.side_effect = replies
        for name, args in (("nx_prepare_boolean", self.prepare), ("nx_execute_plan", self.execute), ("nx_verify_part", self.verify)):
            response = self.invoke(name, args)
            self.assertNotIn("error", response)
            self.assertFalse(response["result"].get("isError", False))
        self.assertEqual(self.bridge.call_args_list, [call("operation.prepare_boolean", self.prepare),
                         call("operation.execute_plan", self.execute), call("operation.verify_part", self.verify)])
        for op in ("unite", "subtract", "intersect"):
            for retain in (False, True):
                self.bridge.reset_mock(side_effect=True, return_value=True)
                self.bridge.return_value = {"operation_id": "prepare", "state": "SUCCEEDED"}
                request = {**self.prepare, "operation": op, "retain_tools": retain}
                self.assertNotIn("error", self.invoke("nx_prepare_boolean", request))
                self.bridge.assert_called_once_with("operation.prepare_boolean", request)

    def test_unknown_transport_remains_blocked_after_terminal_query(self):
        self.bridge.side_effect = BridgeFailure("BRIDGE_TIMEOUT", "response lost", outcome_unknown=True)
        first = self.invoke("nx_execute_plan", self.execute)
        self.assertTrue(first["result"].get("isError"), first)
        self.assertIn("execute", self.server._unknown_operation_ids)
        for request in (self.execute, {**self.execute, "operation_id": "new-execute"}):
            blocked = self.invoke("nx_execute_plan", request)
            self.assertTrue(blocked["result"].get("isError"), blocked)
            self.assertEqual(blocked["result"]["structuredContent"]["error"]["code"], "CLIENT_OUTCOME_UNKNOWN")
        self.bridge.assert_called_once_with("operation.execute_plan", self.execute)
        self.bridge.side_effect = None
        self.bridge.return_value = {"operation_id": "execute", "state": "UNKNOWN", "writes_halted": True}
        self.invoke("nx_get_operation", {"operation_id": "execute"})
        self.assertIn("execute", self.server._unknown_operation_ids)
        self.bridge.return_value = {"operation_id": "execute", "state": "SUCCEEDED", "writes_halted": False}
        self.invoke("nx_get_operation", {"operation_id": "execute"})
        self.assertIn("execute", self.server._unknown_operation_ids)
        blocked = self.invoke("nx_execute_plan", {**self.execute, "operation_id": "after-terminal"})
        self.assertEqual(blocked["result"]["structuredContent"]["error"]["code"], "CLIENT_OUTCOME_UNKNOWN")
        self.assertEqual([entry.args[0] for entry in self.bridge.call_args_list],
                         ["operation.execute_plan", "operation.get", "operation.get"])


if __name__ == "__main__":
    unittest.main()
