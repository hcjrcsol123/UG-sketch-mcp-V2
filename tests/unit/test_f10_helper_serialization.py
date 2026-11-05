"""F10-shaped payload regression through production M2Json serialization."""
from __future__ import annotations

import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
M2_OUTPUT = PROJECT_ROOT / "workspace" / "build" / "current" / "M2"
HARNESS_SOURCE = Path(__file__).with_name("F10HelperSerializationHarness.cs")


def _compiler() -> Path:
    candidates = [
        Path(r"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
        Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    ]
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise RuntimeError("no offline C# compiler found for the F10 serialization harness")


class F10HelperSerializationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        bridge_path = M2_OUTPUT / "M2Bridge.dll"
        if not bridge_path.is_file():
            raise RuntimeError("build the production M2Bridge.dll before this serialization regression")
        if not HARNESS_SOURCE.is_file():
            raise RuntimeError(f"F10 serialization harness not found: {HARNESS_SOURCE}")

        cls._temporary = tempfile.TemporaryDirectory(prefix="f10-helper-serialization-")
        cls._directory = Path(cls._temporary.name)
        cls._exe = cls._directory / "F10HelperSerializationHarness.exe"
        shutil.copy2(bridge_path, cls._directory / "M2Bridge.dll")
        framework = Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319")
        references = [framework / "System.dll", framework / "System.Core.dll",
                      cls._directory / "M2Bridge.dll"]
        command = [
            str(_compiler()), "/nologo", "/langversion:5", "/target:exe", "/platform:x64",
            f"/out:{cls._exe}", *(f"/reference:{reference}" for reference in references),
            str(HARNESS_SOURCE),
        ]
        completed = subprocess.run(command, cwd=PROJECT_ROOT, capture_output=True,
                                   text=True, timeout=60, check=False)
        if completed.returncode != 0:
            raise RuntimeError("F10 serialization harness compile failed:\n" +
                               completed.stdout + completed.stderr)

    @classmethod
    def tearDownClass(cls) -> None:
        temporary = getattr(cls, "_temporary", None)
        if temporary is not None:
            temporary.cleanup()

    def test_f10_plan_execution_verification_and_source_relation_serialize(self):
        completed = subprocess.run([str(self._exe)], cwd=self._directory,
                                   capture_output=True, text=True, timeout=30, check=False)
        self.assertEqual(completed.returncode, 0, completed.stderr or completed.stdout)
        payload = json.loads(completed.stdout)
        self.assertEqual(payload["request"]["source_curve_ids"], ["ENTITY 1 1 1", "ENTITY 2 1 1"])
        self.assertEqual(payload["request"]["direction_xyz"], [
            0.2672612419124244, 0.5345224838248488, 0.8017837257372732,
        ])
        self.assertIs(payload["request"]["associative"], True)
        self.assertEqual(payload["plan"]["kind"], "edit_sketch_projection")
        self.assertEqual(payload["execution"]["output_curve_tags"], ["O-300", "O-301"])
        relation = payload["execution"]["source_relation"]
        self.assertEqual(relation["feature_type"], "CPROJ")
        self.assertEqual(relation["source_tag"], "S-200")
        self.assertEqual(relation["source_journal_id_after_edit"], "ENTITY 1 2 1")
        self.assertIs(relation["source_id_reindexed"], True)
        self.assertIs(payload["verification"]["all_checks_verified"], True)
        self.assertNotIn("NaN", completed.stdout)
        self.assertNotIn("Infinity", completed.stdout)


if __name__ == "__main__":
    unittest.main()
