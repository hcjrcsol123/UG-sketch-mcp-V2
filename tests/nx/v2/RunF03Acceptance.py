# NX Python journal wrapper. Session.Execute loads the compiled C# wrapper;
# the acceptance itself exercises the production M2OpenModelAdapter.
import datetime
import json
import os
import traceback
import uuid

import NXOpen


PROJECT_ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
EVIDENCE_DIR = os.path.join(PROJECT_ROOT, "workspace", "v2", "evidence")
WRAPPER_DLL = os.path.join(EVIDENCE_DIR, "RunF03AcceptanceJournal.dll")


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def write_json(path, value):
    with open(path, "w") as stream:
        json.dump(value, stream, sort_keys=True, indent=2)
        stream.write("\n")


def main():
    if not os.path.isdir(EVIDENCE_DIR):
        os.makedirs(EVIDENCE_DIR)
    run_id = uuid.uuid4().hex
    marker_path = os.path.join(EVIDENCE_DIR, "F03-python-journal-start-" + run_id + ".json")
    marker = {
        "entrypoint": "RunF03Acceptance.py",
        "passed": False,
        "run_id": run_id,
        "python_main_started": True,
        "started_utc": utc_now(),
        "csharp_assembly": WRAPPER_DLL,
        "csharp_entry_class": "NXJournal",
        "csharp_entry_method": "Run",
        "csharp_invocation_started": False,
        "csharp_invocation_returned": False,
    }
    write_json(marker_path, marker)

    try:
        if not os.path.isfile(WRAPPER_DLL):
            raise IOError("Compile tests/nx/v2/build-f03-acceptance.ps1 before running this journal: " + WRAPPER_DLL)
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
        failure_path = os.path.join(EVIDENCE_DIR, "F03-python-journal-failure-" + run_id + ".json")
        write_json(failure_path, marker)
        raise


if __name__ == "__main__":
    main()
