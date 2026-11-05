"""Regression checks for production F09 helper-inventory JSON serialization.

This test reflects the built NX adapter's real clone and helper-cleanup methods,
then sends their result through the production M2Json.CanonicalText writer. It
does not start NX or instantiate a model session.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
M2_OUTPUT = PROJECT_ROOT / "workspace" / "build" / "current" / "M2"
HARNESS_SOURCE = Path(__file__).with_name("F09HelperSerializationHarness.cs")
NX_BASE = Path(os.environ.get("UGII_BASE_DIR", r"C:\0sf\ug"))
NX_MANAGED = NX_BASE / "NXBIN" / "managed"


def _compiler() -> Path:
    candidates = [
        Path(r"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    ]
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise RuntimeError("no offline C# compiler found for the F09 serialization harness")


class F09HelperSerializationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not (M2_OUTPUT / "M2Entry.dll").is_file() or not (M2_OUTPUT / "M2Bridge.dll").is_file():
            raise RuntimeError("build the production M2Entry.dll and M2Bridge.dll before this regression")
        if not NX_MANAGED.is_dir():
            raise RuntimeError(f"NX managed assemblies not found: {NX_MANAGED}")
        if not HARNESS_SOURCE.is_file():
            raise RuntimeError(f"F09 serialization harness not found: {HARNESS_SOURCE}")

        cls._temporary = tempfile.TemporaryDirectory(prefix="f09-helper-serialization-")
        cls._directory = Path(cls._temporary.name)
        cls._exe = cls._directory / "F09HelperSerializationHarness.exe"
        shutil.copy2(M2_OUTPUT / "M2Bridge.dll", cls._directory / "M2Bridge.dll")
        framework = Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319")
        references = [
            framework / "System.dll",
            framework / "System.Core.dll",
            cls._directory / "M2Bridge.dll",
        ]
        command = [
            str(_compiler()),
            "/nologo",
            "/langversion:5",
            "/target:exe",
            "/platform:x64",
            f"/out:{cls._exe}",
            *(f"/reference:{reference}" for reference in references),
            str(HARNESS_SOURCE),
        ]
        completed = subprocess.run(
            command,
            cwd=PROJECT_ROOT,
            capture_output=True,
            text=True,
            timeout=60,
            check=False,
        )
        if completed.returncode != 0:
            raise RuntimeError("C# serialization harness compile failed:\n" + completed.stdout + completed.stderr)

    @classmethod
    def tearDownClass(cls) -> None:
        temporary = getattr(cls, "_temporary", None)
        if temporary is not None:
            temporary.cleanup()

    def _run_case(self, name: str) -> dict[str, object]:
        command = [
            str(self._exe),
            str(M2_OUTPUT / "M2Entry.dll"),
            str(NX_MANAGED),
            name,
        ]
        completed = subprocess.run(
            command,
            cwd=self._directory,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        self.assertEqual(completed.returncode, 0, completed.stderr or completed.stdout)
        return json.loads(completed.stdout)

    def test_empty_helper_inventory_serializes_through_production_clone(self):
        result = self._run_case("empty")
        self.assertTrue(result["success"])
        self.assertEqual(result["case"], "empty")
        cleanup = result["cleanup"]["checks"]["pattern_instances"]["evidence"]["helper_tags_before"]
        self.assertEqual(cleanup, {"axes": [], "directions": [], "points": []})

    def test_nonempty_helper_inventory_serializes_through_production_clone(self):
        result = self._run_case("nonempty")
        self.assertTrue(result["success"])
        evidence = result["cleanup"]["checks"]["pattern_instances"]["evidence"]
        self.assertEqual(evidence["helper_tags_before"], {
            "axes": ["A0"], "directions": ["D0"], "points": ["P0"],
        })
        self.assertEqual(evidence["helper_tags_created"], {
            "axes": ["A1"], "directions": ["D1"], "points": ["P1"],
        })
        self.assertTrue(evidence["every_created_tag_scheduled_for_delete"])

    def test_rollback_shape_with_helper_tags_serializes(self):
        result = self._run_case("rollback")
        self.assertTrue(result["success"])
        rollback = result["rollback"]["rollback"]
        self.assertEqual(rollback["state"], "ROLLED_BACK")
        self.assertTrue(rollback["helper_tags_restored"])
        self.assertEqual(rollback["helper_tags_before"], rollback["helper_tags_after"])


if __name__ == "__main__":
    unittest.main()
