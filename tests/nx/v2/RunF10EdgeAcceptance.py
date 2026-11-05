"""NX Python Journal entry for the native F10 solid-Edge acceptance."""
from __future__ import annotations

import datetime
import json
import os
import traceback
import uuid

import NXOpen


PROJECT_ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
WORKSPACE_ROOT = os.path.join(PROJECT_ROOT, "workspace", "v2")
EVIDENCE_DIR = os.path.join(WORKSPACE_ROOT, "evidence")
WRAPPER_DLL = os.path.join(EVIDENCE_DIR, "RunF10EdgeAcceptanceJournal.dll")


def utc_now() -> str:
    return datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def write_json(path: str, value: dict) -> None:
    with open(path, "w", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, sort_keys=True, indent=2)
        stream.write("\n")


def main() -> None:
    os.makedirs(EVIDENCE_DIR, exist_ok=True)
    run_id = uuid.uuid4().hex
    marker_path = os.path.join(EVIDENCE_DIR, "F10-edge-python-journal-start-" + run_id + ".json")
    marker = {
        "entrypoint": "RunF10EdgeAcceptance.py",
        "test": "F10 native solid Edge projection acceptance",
        "passed": False,
        "run_id": run_id,
        "started_utc": utc_now(),
        "nx_process_id": os.getpid(),
        "csharp_assembly": WRAPPER_DLL,
        "csharp_entry_class": "NXJournal",
        "csharp_entry_method": "Run",
        "nx_started_by_script": False,
        "nx_restarted_by_script": False,
        "nx_closed_by_script": False,
    }
    write_json(marker_path, marker)
    try:
        if not os.path.isfile(WRAPPER_DLL):
            raise IOError("Compile tests/nx/v2/build-f10-edge-acceptance.ps1 before running this Journal: " + WRAPPER_DLL)
        session = NXOpen.Session.GetSession()
        marker["nx_session_available"] = session is not None
        marker["invocation_started_utc"] = utc_now()
        write_json(marker_path, marker)
        result = session.Execute(WRAPPER_DLL, "NXJournal", "Run", [])
        marker["execute_result"] = str(result)
        marker["completed_utc"] = utc_now()
        marker["passed"] = True
        write_json(marker_path, marker)
    except Exception:
        marker["error"] = traceback.format_exc()
        marker["failed_utc"] = utc_now()
        write_json(marker_path, marker)
        failure = dict(marker)
        failure["test"] = "F10 Edge NX Python Journal wrapper"
        failure["reproduction_steps"] = [
            "Build the production M2 assemblies and RunF10EdgeAcceptanceJournal.dll offline.",
            "Run RunF10EdgeAcceptance.py in the already-open designated idle NX2506 session with a clean saved Work Part.",
        ]
        failure["retest_criterion"] = (
            "Retain this wrapper failure evidence, correct the wrapper or assembly load fault, "
            "then rerun in a fresh idle NX session using a new unique model and report path."
        )
        write_json(os.path.join(EVIDENCE_DIR, "F10-edge-python-journal-failure-" + run_id + ".json"), failure)
        raise


if __name__ == "__main__":
    main()
