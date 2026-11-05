"""Run only the deterministic, no-NX F12 software checks and retain evidence."""

from __future__ import annotations

import datetime
import json
from pathlib import Path
import re
import subprocess
import sys
import uuid


ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "workspace" / "v2" / "evidence"
COMMANDS = [
    ["python", "-B", "-m", "unittest", "tests.m2.test_f12_extrude_protocol",
     "tests.unit.test_f12_extrusion_oracle"],
    ["python", "-B", "-m", "unittest", "tests.m2.test_f12_profile_contract"],
    ["python", "-B", "-m", "unittest", "discover", "-s", "tests/m2", "-p", "test_m3_bridge.py"],
    ["python", "-B", "-c",
     "import sys,unittest;sys.path.insert(0,'tests/m2');suite=unittest.defaultTestLoader.loadTestsFromName('test_mcp_stdio.McpStdioProcessTests.test_ownership_and_model_tools_route_arguments_to_bridge_commands');result=unittest.TextTestRunner(verbosity=2).run(suite);raise SystemExit(not result.wasSuccessful())"],
]


def main() -> int:
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    run_id = uuid.uuid4().hex
    report_path = EVIDENCE / f"f12-software-{run_id}.json"
    text_path = EVIDENCE / f"f12-software-{run_id}.txt"
    report = {
        "test": "F12 isolated offline software checks",
        "run_id": run_id,
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "layer": "offline_protocol_and_independent_oracle",
        "nx_started": False,
        "installed_mcp_called": False,
        "commands": [],
        "selected_test_case_count": 0,
        "passed_test_case_count": 0,
        "failed_test_case_count": 0,
        "skipped_test_case_count": 0,
        "passed": False,
    }
    lines = []
    for command in COMMANDS:
        record = {
            "command": command, "status": "RUNNING", "selected_test_case_count": 0,
            "passed_test_case_count": 0, "failed_test_case_count": 0, "skipped_test_case_count": 0,
        }
        report["commands"].append(record)
        completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                   encoding="utf-8", errors="replace", timeout=240)
        record.update(
            exit_code=completed.returncode,
            stdout=completed.stdout,
            stderr=completed.stderr,
            status="PASS" if completed.returncode == 0 else "FAIL",
        )
        command_output = completed.stdout + "\n" + completed.stderr
        summary = re.search(r"Ran (\d+) tests?", command_output)
        record["selected_test_case_count"] = int(summary.group(1)) if summary else 0
        failed_summary = re.search(r"FAILED \(([^)]*)\)", command_output)
        if completed.returncode == 0:
            record["passed_test_case_count"] = record["selected_test_case_count"]
        elif failed_summary:
            fields = failed_summary.group(1)
            failures = re.search(r"failures=(\d+)", fields)
            errors = re.search(r"errors=(\d+)", fields)
            skipped = re.search(r"skipped=(\d+)", fields)
            record["failed_test_case_count"] = (int(failures.group(1)) if failures else 0) + (int(errors.group(1)) if errors else 0)
            record["skipped_test_case_count"] = int(skipped.group(1)) if skipped else 0
            record["passed_test_case_count"] = max(0, record["selected_test_case_count"] - record["failed_test_case_count"] - record["skipped_test_case_count"])
        for key in ("selected_test_case_count", "passed_test_case_count", "failed_test_case_count", "skipped_test_case_count"):
            report[key] += record[key]
        lines.extend(["$ " + " ".join(command), completed.stdout, completed.stderr])
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    report["finished_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    report["passed"] = bool(report["commands"]) and all(item["status"] == "PASS" for item in report["commands"])
    report["selected_command_count"] = len(COMMANDS)
    report["passed_command_count"] = sum(item["status"] == "PASS" for item in report["commands"])
    report["failed_command_count"] = sum(item["status"] == "FAIL" for item in report["commands"])
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    text_path.write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({"report": str(report_path), "text": str(text_path),
                      "passed": report["passed"], "passed_command_count": report["passed_command_count"],
                      "failed_command_count": report["failed_command_count"],
                      "selected_test_case_count": report["selected_test_case_count"],
                      "passed_test_case_count": report["passed_test_case_count"],
                      "failed_test_case_count": report["failed_test_case_count"]}, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
