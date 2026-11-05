"""F22 directly affected bridge pre-dispatch gates; no helper or NX started."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))
from nx_sketch_mcp import journal
from nx_sketch_mcp.bridge import BridgeFailure, JsonlBridge
from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer


@unittest.skipUnless(sys.platform == "win32", "Windows coordination gate")
class BridgeGateTests(unittest.TestCase):
    def test_gate_and_marker_block_before_any_helper_and_preserve_unknown_set(self):
        for blocking in ("gate", "marker"):
            with self.subTest(blocking=blocking), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                gate = None
                if blocking == "gate":
                    gate = journal._gate(root)
                else:
                    (root / "active.json").write_text('{"operation_id":"fake"}', encoding="utf-8")
                try:
                    with patch.object(journal, "_root", return_value=root):
                        server = McpServer(config_from_object({}))
                        self.addCleanup(server.close)
                        server._bridge._call_locked = Mock()
                        server._bridge._ensure_process = Mock()
                        for command in ("bridge.status", "ownership.return", "operation.execute_plan"):
                            with self.assertRaises(BridgeFailure) as caught:
                                server._bridge.call(command, {})
                            self.assertEqual(caught.exception.code, "JOURNAL_COORDINATION_BUSY" if blocking == "gate"
                                             else "JOURNAL_OUTCOME_UNRESOLVED")
                            self.assertFalse(caught.exception.outcome_unknown)
                        requests = [("nx_bridge_status", {}),
                                    ("nx_return", {"ownership_id": "test-owner", "binding_epoch": 1}),
                                    ("nx_execute_plan", {"operation_id": "test-op", "ownership_id": "test-owner",
                                                         "binding_epoch": 1, "plan_id": "test-plan"})]
                        for name, arguments in requests:
                            response = server._tools_call(1, {"name": name, "arguments": arguments})
                            self.assertTrue(response["result"]["isError"])
                            self.assertEqual(response["result"]["structuredContent"]["error"]["code"],
                                             "JOURNAL_COORDINATION_BUSY" if blocking == "gate"
                                             else "JOURNAL_OUTCOME_UNRESOLVED")
                        self.assertEqual(server._unknown_operation_ids, set())
                        server._bridge._call_locked.assert_not_called()
                        server._bridge._ensure_process.assert_not_called()
                        self.assertIsNone(server._bridge._process)
                finally:
                    if gate is not None:
                        gate[0].CloseHandle(gate[1])

    def test_get_journal_remains_readable_during_active_marker(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(journal, "_root", return_value=Path(temp)):
            root = Path(temp)
            (root / "active.json").write_text('{"operation_id":"known"}', encoding="utf-8")
            operation = root / "operations" / "known"
            operation.mkdir(parents=True)
            (operation / "state.json").write_text(json.dumps({"operation_id": "known", "status": "UNKNOWN"}), encoding="utf-8")
            server = McpServer(config_from_object({}))
            self.addCleanup(server.close)
            server._bridge._call_locked = Mock()
            result = server._tools_call(1, {"name": "nx_get_journal", "arguments": {"operation_id": "known"}})
            self.assertFalse(result["result"].get("isError", False))
            self.assertEqual(result["result"]["structuredContent"]["status"], "UNKNOWN")
            self.assertEqual(server._unknown_operation_ids, set())
            server._bridge._call_locked.assert_not_called()


if __name__ == "__main__":
    unittest.main()
