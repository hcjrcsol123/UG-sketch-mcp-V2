"""Offline launcher copy test; dummy executable, private root, no NX launch."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))
from nx_sketch_mcp import journal


@unittest.skipUnless(sys.platform == "win32", "Windows launcher contract")
class LauncherGateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.gate_root = self.root / "gate"
        self.gate_root.mkdir()
        source = Path(__file__).resolve().parents[2] / "tools" / "start-nx-global.ps1"
        text = source.read_text(encoding="utf-8")
        original = "Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex/nx-sketch-mcp/journal-coordination'"
        self.assertEqual(text.count(original), 1, "launcher root expression changed; review fixture")
        escaped = str(self.gate_root).replace("'", "''")
        self.launcher = self.root / "start-nx-test-copy.ps1"
        self.launcher.write_text(text.replace(original, "'" + escaped + "'"), encoding="utf-8")
        self.nxexe = self.root / "ugraf.exe"
        self.helper = self.root / "M2JsonlHelper.exe"
        self.startup = self.root / "startup"
        self.startup.mkdir()
        for path in (self.nxexe, self.helper, self.root / "M2Entry.dll", self.root / "M2Bridge.dll",
                     self.startup / "M2Startup.dll"):
            path.write_bytes(b"not an executable or DLL")
        self.config = self.root / "config.json"
        self.config.write_text(json.dumps({"pipe_name": "f22-offline-fixture", "token_file": str(self.root / "unused-token"),
                                           "helper_path": str(self.helper)}), encoding="utf-8")

    def invoke(self):
        result = subprocess.run(["pwsh", "-NoProfile", "-File", str(self.launcher), "-Config", str(self.config),
                                 "-NxExe", str(self.nxexe), "-StartupDirectory", str(self.startup), "-AsJson"],
                                capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def test_busy_gate_rejects_before_launch(self):
        kernel, handle = journal._gate(self.gate_root)
        try:
            result = self.invoke()
        finally:
            kernel.CloseHandle(handle)
        self.assertEqual(result["code"], "JOURNAL_COORDINATION_BUSY")
        self.assertFalse(result["ready"])

    def test_persistent_marker_rejects_without_modification(self):
        marker = self.gate_root / "active.json"
        marker.write_text('{"case":"fake-active"}', encoding="utf-8")
        result = self.invoke()
        self.assertEqual(result["code"], "JOURNAL_OUTCOME_UNRESOLVED")
        self.assertFalse(result["ready"])
        self.assertEqual(marker.read_text(encoding="utf-8"), '{"case":"fake-active"}')


if __name__ == "__main__":
    unittest.main()
