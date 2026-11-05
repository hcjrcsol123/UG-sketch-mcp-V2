"""Run the F12 pure geometry-domain matcher without opening an NX session."""

from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
HARNESS_SOURCE = PROJECT_ROOT / "tests" / "m2" / "F12ProfileContractHarness.cs"
PROFILE_SOURCE = PROJECT_ROOT / "src" / "bridge" / "V2SketchProfileAnalyzer.cs"
NX_MANAGED = Path(r"C:\0sf\ug\NXBIN\managed")
COMPILERS = (
    Path(r"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
    Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
)


class F12ProfileContractTests(unittest.TestCase):
    def test_native_curve_geometry_matcher_is_order_and_orientation_independent(self):
        if not NX_MANAGED.is_dir():
            self.skipTest("local NX managed assemblies are required to compile the production geometry helper")
        compiler = next((candidate for candidate in COMPILERS if candidate.is_file()), None)
        if compiler is None:
            self.skipTest("local C# compiler is unavailable")
        references = [
            NX_MANAGED / "NXOpen.dll",
            NX_MANAGED / "NXOpen.UF.dll",
            NX_MANAGED / "NXOpen.Utilities.dll",
        ]
        if any(not path.is_file() for path in references):
            self.skipTest("local NXOpen managed references are incomplete")

        with tempfile.TemporaryDirectory(prefix="nx-f12-profile-contract-") as temporary:
            executable = Path(temporary) / "F12ProfileContractHarness.exe"
            command = [
                str(compiler), "/nologo", "/langversion:5", "/target:exe", "/platform:x64",
                f"/out:{executable}",
                *(f"/reference:{reference}" for reference in references),
                str(PROFILE_SOURCE), str(HARNESS_SOURCE),
            ]
            compiled = subprocess.run(command, cwd=PROJECT_ROOT, capture_output=True,
                                      text=True, encoding="utf-8", errors="replace", timeout=60)
            self.assertEqual(compiled.returncode, 0, compiled.stdout + compiled.stderr)
            executed = subprocess.run([str(executable), str(NX_MANAGED)], cwd=PROJECT_ROOT,
                                      capture_output=True, text=True, encoding="utf-8",
                                      errors="replace", timeout=30,
                                      env={**os.environ, "UGII_BASE_DIR": os.environ.get("UGII_BASE_DIR", r"C:\0sf\ug")})
            self.assertEqual(executed.returncode, 0, executed.stdout + executed.stderr)
            self.assertIn("f12_profile_contract_checks=", executed.stdout)
            self.assertIn(" failures=0", executed.stdout)


if __name__ == "__main__":
    unittest.main()
