"""NX Python Journal entry for the F10 real-NX projection acceptance skeleton."""
from __future__ import annotations

import datetime
import json
import os
import traceback
import uuid

import NXOpen


PROJECT_ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
EVIDENCE_DIR = os.path.join(PROJECT_ROOT, "workspace", "v2", "evidence")
WRAPPER_DLL = os.path.join(EVIDENCE_DIR, "RunF10AcceptanceJournal.dll")


def utc_now() -> str:
    return datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def write_json(path: str, value: dict) -> None:
    with open(path, "w", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, sort_keys=True, indent=2)
        stream.write("\n")


def main() -> None:
    os.makedirs(EVIDENCE_DIR, exist_ok=True)
    run_id = uuid.uuid4().hex
    marker_path = os.path.join(EVIDENCE_DIR, "F10-python-journal-start-" + run_id + ".json")
    marker = {
        "entrypoint": "RunF10Acceptance.py",
        "passed": False,
        "run_id": run_id,
        "python_main_started": True,
        "started_utc": utc_now(),
        "nx_process_id": os.getpid(),
        "csharp_assembly": WRAPPER_DLL,
        "csharp_entry_class": "NXJournal",
        "csharp_entry_method": "Run",
        "csharp_invocation_started": False,
        "csharp_invocation_returned": False,
        "nx_started_by_script": False,
        "nx_restarted_by_script": False,
        "nx_closed_by_script": False,
    }
    write_json(marker_path, marker)
    try:
        if not os.path.isfile(WRAPPER_DLL):
            raise IOError("Compile tests/nx/v2/build-f10-acceptance.ps1 before running this Journal: " + WRAPPER_DLL)
        session = NXOpen.Session.GetSession()
        marker["nx_session_available"] = session is not None
        marker["csharp_invocation_started"] = True
        marker["invocation_started_utc"] = utc_now()
        write_json(marker_path, marker)
        result = session.Execute(WRAPPER_DLL, "NXJournal", "Run", [])
        marker["csharp_invocation_returned"] = True
        marker["execute_result"] = str(result)
        marker["completed_utc"] = utc_now()
        write_json(marker_path, marker)
    except Exception:
        marker["error"] = traceback.format_exc()
        marker["failed_utc"] = utc_now()
        write_json(marker_path, marker)
        failure = dict(marker)
        failure["test"] = "F10 NX Python Journal wrapper"
        failure["reproduction_steps"] = [
            "Build RunF10AcceptanceJournal.dll and F10Acceptance.dll offline.",
            "Run RunF10Acceptance.py in the already-open designated idle NX2506 session.",
        ]
        failure["retest_criterion"] = (
            "Retain this failure evidence, correct the wrapper or assembly load problem, "
            "then rerun F10 in a fresh idle NX session using a new unique model and report path."
        )
        write_json(os.path.join(EVIDENCE_DIR, "F10-python-journal-failure-" + run_id + ".json"), failure)
        raise


if __name__ == "__main__":
    main()
