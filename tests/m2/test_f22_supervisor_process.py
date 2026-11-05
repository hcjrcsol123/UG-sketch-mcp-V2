"""Real process lifecycle with an isolated source copy and a fake runner, never NX."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))
from nx_sketch_mcp import journal

PRODUCTION = Path(__file__).resolve().parents[2] / "src" / "nx_sketch_mcp" / "journal.py"


def wait_until(predicate, seconds=20):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.03)  # A deadline only; file/gate conditions establish the event ordering.
    raise AssertionError("fixture event did not occur within deadline")


@unittest.skipUnless(sys.platform == "win32", "Windows detached supervisor and file gate")
class SupervisorProcessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.coordination = self.root / "coordination"
        self.coordination.mkdir()
        self.observed = self.root / "observed"
        self.observed.mkdir()
        self.copy = self.root / "journal.py"
        text = PRODUCTION.read_text(encoding="utf-8")
        start, end = text.index("def _root() -> Path:"), text.index("\ndef _id(")
        text = text[:start] + "def _root() -> Path:\n    return Path(" + repr(str(self.coordination)) + ")\n\n" + text[end:]
        start, end = text.index("def _processes()"), text.index("\ndef _snapshot(")
        text = text[:start] + "def _processes() -> list:\n    return []  # isolated fake process list\n\n" + text[end:]
        old = "command = [RUN_JOURNAL, str(wrapper),"
        self.assertEqual(text.count(old), 1, "runner construction changed; review the fixture")
        text = text.replace(old, 'command = [sys.executable, os.environ["F22_FAKE_RUNNER"], str(wrapper),')
        self.text = text
        (self.root / "journal_wrapper.py").write_text("# placeholder; never executed\n", encoding="utf-8")
        self.runner = self.root / "fake_runner.py"
        self.runner.write_text('''import json, os, pathlib, sys, time
root = pathlib.Path(os.environ["F22_CONTROL"])
(root / "ready").write_text("started", encoding="utf-8")
deadline = time.monotonic() + 30
while not (root / "release").exists():
    if time.monotonic() > deadline:
        raise RuntimeError("fixture release deadline expired")
    time.sleep(.03)
snapshot = {"complete": True, "parts": [], "errors": []}
payload = {"script_outcome": "RETURNED", "before": snapshot, "after": snapshot,
           "changes": {"added": [], "removed": [], "modified": []}}
pathlib.Path(sys.argv[4]).write_text(json.dumps(payload), encoding="utf-8")
''', encoding="utf-8")
        self.env = dict(os.environ, F22_FAKE_RUNNER=str(self.runner), F22_CONTROL=str(self.root))

    def prepare(self, inject_crash=False):
        text = self.text
        if inject_crash:
            old = "marked = True\n        busy = _processes()"
            self.assertEqual(text.count(old), 1, "supervisor marker code changed; review fixture")
            text = text.replace(old, 'marked = True\n        raise SystemExit("injected supervisor exit")\n        busy = _processes()')
        self.copy.write_text(text, encoding="utf-8")

    def submit_from_short_lived_host(self, operation_id):
        source = '''import importlib.util, json, sys
s = importlib.util.spec_from_file_location("f22_fixture", sys.argv[1])
m = importlib.util.module_from_spec(s); s.loader.exec_module(m)
request = {"operation_id": sys.argv[2], "language": "python", "script_content": "# unused",
           "args": [], "observation_root": sys.argv[3], "timeout_seconds": 20}
print(json.dumps(m.submit(request)))
'''
        result = subprocess.run([sys.executable, "-c", source, str(self.copy), operation_id, str(self.observed)],
                                env=self.env, capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["status"], "STARTING")

    def state(self, operation_id):
        try:
            return json.loads((self.coordination / "operations" / operation_id / "state.json").read_text(encoding="utf-8"))
        except (FileNotFoundError, json.JSONDecodeError):
            return {}

    def gate_is_available(self):
        try:
            kernel, handle = journal._gate(self.coordination)
        except OSError:
            return False
        kernel.CloseHandle(handle)
        return True

    def test_host_exits_supervisor_holds_gate_until_runner_exits(self):
        self.prepare()
        self.submit_from_short_lived_host("lifecycle")
        try:
            wait_until(lambda: (self.root / "ready").exists())
            self.assertTrue((self.coordination / "active.json").exists())
            self.assertFalse(self.gate_is_available())
            self.assertTrue(self.state("lifecycle")["dispatched"])
        finally:
            (self.root / "release").touch()
        wait_until(lambda: self.state("lifecycle").get("status") == "SUCCEEDED")
        wait_until(self.gate_is_available)
        self.assertFalse((self.coordination / "active.json").exists())

    def test_supervisor_natural_exception_leaves_marker_and_blocks_next(self):
        self.prepare(inject_crash=True)
        self.submit_from_short_lived_host("crashed")
        marker = self.coordination / "active.json"
        wait_until(marker.exists)
        wait_until(self.gate_is_available)
        self.assertFalse((self.root / "ready").exists())
        self.prepare()  # Subsequent copied supervisor uses ordinary production control flow.
        self.submit_from_short_lived_host("next")
        wait_until(lambda: self.state("next").get("status") == "REJECTED")
        self.assertTrue(marker.exists())
        self.assertFalse(self.state("next")["dispatched"])
        self.assertFalse((self.root / "ready").exists())


if __name__ == "__main__":
    unittest.main()
