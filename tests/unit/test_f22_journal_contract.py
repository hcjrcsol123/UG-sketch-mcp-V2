"""F22 offline controls; patched subprocesses never launch NX or a journal."""
from pathlib import Path
import json
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))
from nx_sketch_mcp import journal


class JournalContractTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.observed = self.root / "observed"
        self.observed.mkdir()
        self.request = {"operation_id": "controlled-1", "language": "python",
                        "script_content": "print('中文')", "args": ["", "a b", "中文", r"x\y"],
                        "observation_root": str(self.observed), "timeout_seconds": 1}

    def test_validation_preserves_args_and_freezes_file_bytes(self):
        script = self.root / "file with spaces.py"
        script.write_bytes(b"print('before')\n")
        request = dict(self.request)
        del request["script_content"]
        request["script_path"] = str(script)
        args, content = journal._validate(request)
        script.write_bytes(b"print('after')\n")
        self.assertEqual(content, b"print('before')\n")
        self.assertEqual(args["args"], ["", "a b", "中文", r"x\y"])

    def test_invalid_inputs_never_launch_supervisor(self):
        invalid = []
        for changes in ({"operation_id": "../escape"}, {"operation_id": "CON"},
                        {"args": ["ok", 1]}, {"args": ["nul\0char"]},
                        {"timeout_seconds": True}, {"timeout_seconds": float("nan")},
                        {"timeout_seconds": 0}, {"observation_root": "relative"},
                        {"script_path": str(self.root / "missing.py")}, {"extra": 1}):
            invalid.append({**self.request, **changes})
        with patch.object(journal, "_root", return_value=self.root), patch.object(journal.subprocess, "Popen") as popen:
            for request in invalid:
                with self.subTest(request=request), self.assertRaises((ValueError, OSError)):
                    journal.submit(request)
            popen.assert_not_called()

    def test_reserved_id_cannot_replay_even_if_request_changes(self):
        with patch.object(journal, "_root", return_value=self.root), patch.object(journal.subprocess, "Popen") as popen:
            state = journal.submit(self.request)
            self.assertEqual(state["status"], "STARTING")
            self.assertEqual((self.root / "operations" / "controlled-1" / "journal.py").read_bytes(),
                             self.request["script_content"].encode("utf-8"))
            with self.assertRaises(ValueError):
                journal.submit({**self.request, "script_content": "print('different')"})
            self.assertEqual(popen.call_count, 1)

    def test_timeout_stays_unknown_after_fake_runner_finishes(self):
        process = Mock(pid=12345)
        process.wait.side_effect = [subprocess.TimeoutExpired("fake", 1), 0]
        gate_kernel = Mock()
        with patch.object(journal, "_root", return_value=self.root), \
             patch.object(journal, "_gate", return_value=(gate_kernel, 123)), \
             patch.object(journal, "_processes", return_value=[]), \
             patch.object(journal.subprocess, "Popen", return_value=process):
            journal.submit(self.request)
            journal._supervise("controlled-1")
            record = journal.get("controlled-1")
            self.assertEqual(record["status"], "UNKNOWN")
            self.assertTrue(record["dispatched"])
            self.assertTrue((self.root / "active.json").exists())
            self.assertEqual(process.wait.call_count, 2)
            self.assertFalse((self.root / "operations" / "controlled-1" / "observation-after.json").exists())
            with self.assertRaises(ValueError):
                journal.submit(self.request)
            gate_kernel.CloseHandle.assert_called_once_with(123)

    def test_persistent_active_marker_rejects_without_dispatch(self):
        gate_kernel = Mock()
        with patch.object(journal, "_root", return_value=self.root), \
             patch.object(journal, "_gate", return_value=(gate_kernel, 123)), \
             patch.object(journal, "_processes") as processes, \
             patch.object(journal.subprocess, "Popen") as popen:
            journal.submit(self.request)
            marker = self.root / "active.json"
            marker.write_text('{"operation_id":"previous-unknown"}', encoding="utf-8")
            journal._supervise("controlled-1")
            record = journal.get("controlled-1")
            self.assertEqual(record["status"], "REJECTED")
            self.assertFalse(record["dispatched"])
            self.assertEqual(marker.read_text(encoding="utf-8"), '{"operation_id":"previous-unknown"}')
            processes.assert_not_called()
            self.assertEqual(popen.call_count, 1)  # submit's fake supervisor only
            gate_kernel.CloseHandle.assert_called_once_with(123)

    def test_observation_loss_after_dispatch_freezes_unknown(self):
        process = Mock(pid=12345)
        process.wait.side_effect = [RuntimeError("simulated observation loss"), 0]
        process.poll.return_value = None
        gate_kernel = Mock()
        with patch.object(journal, "_root", return_value=self.root), \
             patch.object(journal, "_gate", return_value=(gate_kernel, 123)), \
             patch.object(journal, "_processes", return_value=[]), \
             patch.object(journal.subprocess, "Popen", return_value=process):
            journal.submit(self.request)
            journal._supervise("controlled-1")
            record = journal.get("controlled-1")
            self.assertEqual(record["status"], "UNKNOWN")
            self.assertTrue(record["dispatched"])
            self.assertTrue((self.root / "active.json").exists())
            self.assertEqual(process.wait.call_count, 2)
            process.terminate.assert_not_called()
            process.kill.assert_not_called()

    def test_completion_requires_consistent_complete_model_observation(self):
        snapshot = {"complete": True, "parts": [], "errors": []}
        changes = {"added": [], "removed": [], "modified": []}
        returned = {"script_outcome": "RETURNED", "before": snapshot, "after": snapshot, "changes": changes}
        raised = {**returned, "script_outcome": "RAISED"}
        cases = [("missing", None, 0, "UNKNOWN", True),
                 ("partial", {"script_outcome": "RETURNED"}, 0, "UNKNOWN", True),
                 ("contradiction", raised, 0, "UNKNOWN", True),
                 ("success", returned, 0, "SUCCEEDED", False),
                 ("exception", raised, 1, "FAILED", False)]
        for name, observation, code, status, marker_expected in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                request = {**self.request, "operation_id": name}
                process = Mock(pid=12345)
                def finish(timeout=None):
                    if observation is not None:
                        (root / "operations" / name / "model-observation.json").write_text(
                            json.dumps(observation), encoding="utf-8")
                    return code
                process.wait.side_effect = finish
                with patch.object(journal, "_root", return_value=root), \
                     patch.object(journal, "_gate", return_value=(Mock(), 123)), \
                     patch.object(journal, "_processes", return_value=[]), \
                     patch.object(journal.subprocess, "Popen", return_value=process) as popen:
                    journal.submit(request)
                    journal._supervise(name)
                    self.assertEqual(journal.get(name)["status"], status)
                    self.assertEqual((root / "active.json").exists(), marker_expected)
                    self.assertEqual(popen.call_count, 2)  # fake supervisor and fake runner
                    journal._supervise(name)  # Claim is one-shot even for a direct repeat.
                    self.assertEqual(popen.call_count, 2)

    @unittest.skipUnless(sys.platform == "win32", "Windows FileShare.None contract")
    def test_gate_rejects_other_process_until_owner_releases(self):
        # The parent holds the real gate before starting the contender; no sleep
        # or NX process is needed. The child reports only acquisition outcome.
        kernel, handle = journal._gate(self.root)
        source = (
            "import sys; from pathlib import Path; "
            "from nx_sketch_mcp import journal; "
            "k,h=journal._gate(Path(sys.argv[1])); k.CloseHandle(h)"
        )
        env = dict(__import__("os").environ)
        env["PYTHONPATH"] = str(Path(__file__).resolve().parents[2] / "src")
        try:
            blocked = subprocess.run([sys.executable, "-c", source, str(self.root)],
                                     env=env, capture_output=True, text=True)
            self.assertNotEqual(blocked.returncode, 0)
            self.assertIn("PermissionError", blocked.stderr)
        finally:
            kernel.CloseHandle(handle)
        admitted = subprocess.run([sys.executable, "-c", source, str(self.root)],
                                  env=env, capture_output=True, text=True)
        self.assertEqual(admitted.returncode, 0, admitted.stderr)


if __name__ == "__main__":
    unittest.main()
