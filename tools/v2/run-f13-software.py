"""Run isolated, no-NX F13 protocol/oracle/lifecycle checks and retain evidence."""

from __future__ import annotations

import datetime
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys
import uuid


ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "workspace" / "v2" / "evidence"
COMMANDS = [
    [sys.executable, "-B", "-m", "unittest", "tests.unit.test_f13_revolve_oracle"],
    [sys.executable, "-B", "-m", "unittest", "discover", "-s", "tests/m2", "-p", "test_f13_revolve*.py"],
    # The extrude contract shares the request dispatcher and operation-plan
    # route, so retain this narrow compatibility check without rerunning the
    # unrelated F12 native or installed matrices.
    [sys.executable, "-B", "-m", "unittest", "tests.m2.test_f12_extrude_protocol"],
]

F13_ONLY_COMMANDS = [COMMANDS[1]]


def _counts(output: str) -> tuple[int, int, int, int]:
    summary = re.search(r"Ran (\d+) tests?", output)
    selected = int(summary.group(1)) if summary else 0
    failed_summary = re.search(r"FAILED \(([^)]*)\)", output)
    if not failed_summary:
        passed_summary = re.search(r"OK(?: \(([^)]*)\))?", output)
        if not passed_summary:
            return selected, 0, selected, 0
        fields = passed_summary.group(1) or ""
        skipped_match = re.search(r"skipped=(\d+)", fields)
        skipped = int(skipped_match.group(1)) if skipped_match else 0
        return selected, max(0, selected - skipped), 0, skipped
    fields = failed_summary.group(1)
    failure_match = re.search(r"failures=(\d+)", fields)
    error_match = re.search(r"errors=(\d+)", fields)
    skipped_match = re.search(r"skipped=(\d+)", fields)
    failed = (int(failure_match.group(1)) if failure_match else 0) + (int(error_match.group(1)) if error_match else 0)
    skipped = int(skipped_match.group(1)) if skipped_match else 0
    return selected, max(0, selected - failed - skipped), failed, skipped


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--f13-only", action="store_true",
                        help="run only the F13 protocol and NX-free STA lifecycle modules; leave unchanged oracle and F12 regression runs separate")
    args = parser.parse_args()
    selected_commands = F13_ONLY_COMMANDS if args.f13_only else COMMANDS
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    run_id = uuid.uuid4().hex
    report_path = EVIDENCE / f"f13-software-{run_id}.json"
    text_path = EVIDENCE / f"f13-software-{run_id}.txt"
    report: dict[str, object] = {
        "test": "F13 isolated no-NX software checks",
        "run_id": run_id,
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "layers": ["offline_protocol", "nx_free_sta_fake"] if args.f13_only else
                  ["offline_protocol", "independent_geometry_oracle", "nx_free_sta_fake", "targeted_compatibility"],
        "execution_scope": "F13 protocol and STA lifecycle only" if args.f13_only else "full F13 no-NX suite plus narrow F12 compatibility",
        "unselected_commands": [command for command in COMMANDS if command not in selected_commands],
        "nx_started": False,
        "installed_mcp_called": False,
        "commands": [],
        "selected_test_case_count": 0,
        "passed_test_case_count": 0,
        "failed_test_case_count": 0,
        "skipped_test_case_count": 0,
        "passed": False,
    }
    output_lines: list[str] = []
    records: list[dict[str, object]] = []
    report["commands"] = records
    for command in selected_commands:
        record: dict[str, object] = {
            "command": command,
            "status": "RUNNING",
            "selected_test_case_count": 0,
            "passed_test_case_count": 0,
            "failed_test_case_count": 0,
            "skipped_test_case_count": 0,
        }
        records.append(record)
        try:
            completed = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                       encoding="utf-8", errors="replace", timeout=300)
            combined = completed.stdout + "\n" + completed.stderr
            selected, passed, failed, skipped = _counts(combined)
            record.update(
                exit_code=completed.returncode,
                stdout=completed.stdout,
                stderr=completed.stderr,
                selected_test_case_count=selected,
                passed_test_case_count=passed,
                failed_test_case_count=failed,
                skipped_test_case_count=skipped,
                status="PASS" if completed.returncode == 0 and selected > 0 and passed + skipped == selected else "FAIL",
            )
            output_lines.extend(["$ " + " ".join(command), completed.stdout, completed.stderr])
        except subprocess.TimeoutExpired as exc:
            stdout = exc.stdout.decode("utf-8", "replace") if isinstance(exc.stdout, bytes) else (exc.stdout or "")
            stderr = exc.stderr.decode("utf-8", "replace") if isinstance(exc.stderr, bytes) else (exc.stderr or "")
            record.update(status="FAIL", error="subprocess timeout", stdout=stdout, stderr=stderr)
            output_lines.extend(["$ " + " ".join(command), stdout, stderr, "TIMEOUT"])
        report["selected_test_case_count"] = int(report["selected_test_case_count"]) + int(record.get("selected_test_case_count", 0))
        report["passed_test_case_count"] = int(report["passed_test_case_count"]) + int(record.get("passed_test_case_count", 0))
        report["failed_test_case_count"] = int(report["failed_test_case_count"]) + int(record.get("failed_test_case_count", 0))
        report["skipped_test_case_count"] = int(report["skipped_test_case_count"]) + int(record.get("skipped_test_case_count", 0))
        report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    report["finished_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    report["passed_command_count"] = sum(item.get("status") == "PASS" for item in records)
    report["failed_command_count"] = sum(item.get("status") == "FAIL" for item in records)
    report["passed"] = bool(records) and all(item.get("status") == "PASS" for item in records)
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    text_path.write_text("\n".join(output_lines), encoding="utf-8")
    print(json.dumps({
        "report": str(report_path),
        "text": str(text_path),
        "passed": report["passed"],
        "passed_command_count": report["passed_command_count"],
        "failed_command_count": report["failed_command_count"],
        "selected_test_case_count": report["selected_test_case_count"],
        "passed_test_case_count": report["passed_test_case_count"],
        "failed_test_case_count": report["failed_test_case_count"],
        "skipped_test_case_count": report["skipped_test_case_count"],
    }, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
