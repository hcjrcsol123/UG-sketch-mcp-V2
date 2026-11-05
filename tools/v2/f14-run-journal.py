"""Root-only serialized NX journal runner for F14; never kills an NX process."""

from __future__ import annotations

import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid


ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "workspace" / "v2" / "evidence"
NX_JOURNAL = Path(r"C:\0sf\ug\NXBIN\run_journal.exe")


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: python tools/v2/f14-run-journal.py <project-relative-journal.py>")
    script = Path(sys.argv[1]).resolve(strict=True)
    if not script.is_relative_to(ROOT):
        raise ValueError("Journal must be inside this project")
    if not NX_JOURNAL.is_file():
        raise FileNotFoundError(f"NX journal runner is unavailable: {NX_JOURNAL}")

    # This guard only observes. It never terminates or closes an existing
    # session; an operator must resolve any process before beginning a run.
    check = subprocess.run(
        [
            "powershell", "-NoProfile", "-Command",
            "@(Get-Process ugraf,run_journal,M2JsonlHelper -ErrorAction SilentlyContinue).Count",
        ],
        capture_output=True, text=True, check=True,
    )
    if check.stdout.strip() != "0":
        raise RuntimeError("An NX/helper process exists; inspect it before launching")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    run_id = uuid.uuid4().hex
    prefix = EVIDENCE / ("f14-batch-" + run_id)
    # Keep the exact bytes given to the interpreter; no hashes are computed.
    # F14 journals use the explicit project root rather than __file__-relative imports.
    execution_script = prefix.with_suffix(".journal.py")
    execution_script.write_bytes(script.read_bytes())
    report = {
        "run_id": run_id,
        "scope": "F14 native batch journal",
        "script": str(script),
        "executed_script_snapshot": str(execution_script),
        "operator": "/root",
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "forced_termination": False,
        "state": "STARTING",
    }
    report_path = prefix.with_suffix(".json")

    def persist() -> None:
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")

    persist()
    environment = os.environ.copy()
    environment["PYTHONUTF8"] = "1"
    with prefix.with_suffix(".stdout.txt").open("wb") as stdout, prefix.with_suffix(".stderr.txt").open("wb") as stderr:
        process = subprocess.Popen(
            [str(NX_JOURNAL), str(execution_script)],
            cwd=str(ROOT),
            env=environment,
            stdout=stdout,
            stderr=stderr,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        report.update(pid=process.pid, state="RUNNING")
        persist()
        print(json.dumps({"report": str(report_path), "pid": process.pid}), flush=True)
        report["exit_code"] = process.wait()

    report.update(state="EXITED", finished_utc=datetime.datetime.now(datetime.timezone.utc).isoformat())
    persist()
    print(json.dumps({"report": str(report_path), "exit_code": report["exit_code"]}), flush=True)
    return 0 if report["exit_code"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
