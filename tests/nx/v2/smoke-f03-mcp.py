"""Exercise F03 through the installed STDIO host in a dedicated NX GUI.

Run --ready first, open the isolated fixture in the returned NX PID, then
run with --part-path. No journal is executed in the bridge-owned GUI session.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools" / "v2"))
from f01_f02_mcp_client import McpStdioClient, _tool_payload, _wait_operation


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ready", action="store_true")
    parser.add_argument("--part-path", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if not args.ready and args.part_path is None:
        parser.error("--part-path is required for modeling")
    if args.output.exists():
        parser.error("evidence output must be a new path")
    install = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
    env = os.environ.copy()
    env.update(PYTHONPATH=str(install / "app" / "src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
    process = subprocess.Popen(
        [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install / "mcp-config.json")],
        cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, bufsize=0,
    )
    client = McpStdioClient(process, 60)
    report = {"passed": False, "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "calls": []}
    credentials = {}

    def persist():
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, allow_nan=False, indent=2) + "\n", encoding="utf-8")

    def call(name, arguments):
        payload = _tool_payload(client, name, arguments)
        entry = {"tool": name, "arguments": arguments, "payload": payload}
        report["calls"].append(entry)
        if "operation_id" in payload:
            payload = _wait_operation(client, payload, 90, 0.2)
            entry["completed"] = payload
            persist()
            return payload["result"]
        persist()
        return payload

    def step(name, arguments):
        prepared = call(name, {
            **credentials, "operation_id": "f03-prepare-" + uuid.uuid4().hex,
            "revision_id": report["revision_id"], **arguments,
        })
        execute_id = "f03-execute-" + uuid.uuid4().hex
        applied = call("nx_execute_plan", {
            **credentials, "operation_id": execute_id, "plan_id": prepared["plan"]["plan_id"],
        })
        assert applied["mutation_state"] == "APPLIED", applied
        verified = call("nx_verify_part", {
            **credentials, "operation_id": "f03-verify-" + uuid.uuid4().hex,
            "execute_operation_id": execute_id,
        })
        assert verified["verified"] is True, verified
        report["revision_id"] = verified["revision_id"]
        return applied

    try:
        report["initialize"] = client.request("initialize", {
            "protocolVersion": "2025-11-25", "capabilities": {},
            "clientInfo": {"name": "f03-installed-acceptance", "version": "1"},
        })
        client.notify("notifications/initialized")
        report["tools"] = client.request("tools/list")["tools"]
        assert report["initialize"]["serverInfo"]["version"] == "0.4.1", report["initialize"]
        assert len(report["tools"]) == 21
        datum_tool = next(tool for tool in report["tools"] if tool["name"] == "nx_prepare_datum_plane")
        assert "distance_mm" in datum_tool["inputSchema"]["properties"]
        ready = call("nx_ensure_ready", {})
        for _ in range(3):
            if ready.get("state") != "STARTING":
                break
            time.sleep(1)
            ready = call("nx_ensure_ready", {})
        report["ready"] = ready
        assert ready["state"] == "READY", ready
        status = call("nx_bridge_status", {})
        assert status["ownership_state"] == "HUMAN_OWNED", status
        report["nx_pid"] = status["pid"]
        if args.ready:
            report["passed"] = True
            return 0

        path = args.part_path.resolve(strict=True)
        assert path.is_relative_to((ROOT / "workspace" / "v2" / "models").resolve())
        report["part_path"] = str(path)
        owned = call("nx_takeover", {
            "expected_pid": status["pid"], "expected_part_path": str(path),
            "artifact_scope": "saved_independent_copy", "authorize": True,
        })
        credentials.update(ownership_id=owned["ownership_id"], binding_epoch=owned["binding_epoch"])
        report["revision_id"] = owned["revision_id"]
        report["capabilities"] = call("nx_get_capabilities", {})
        fixed = step("nx_prepare_datum_plane", {
            "origin_mm": [10, 20, 30], "normal": [1, 1, 1], "x_axis": [1, -1, 0],
        })
        parent_id = fixed["datum_plane_id"]
        children = []
        for distance in (12.5, -7.25, 0.0):
            child = step("nx_prepare_datum_plane", {
                "support_datum_plane_id": parent_id, "distance_mm": distance,
            })
            children.append(child["datum_plane_id"])
        query = call("nx_query_faces", {
            **credentials, "revision_id": report["revision_id"], "selector": "all_planar",
        })
        faces = [candidate for candidate in query["candidates"]
                 if all(component > 0.1 for component in candidate["normal_world"])]
        assert len(faces) == 1, query
        face = faces[0]
        face_id = face["face_journal_id"]
        for distance in (4.5, -3.0, 0.0):
            step("nx_prepare_datum_plane", {"support_face_id": face_id, "distance_mm": distance})
        step("nx_prepare_sketch", {
            "support_datum_plane_id": children[0], "x_axis": [1, -1, 0],
            "geometries": [{"id": "offset-rectangle", "type": "rectangle", "corner_mm": [0, 0], "width_mm": 8, "height_mm": 5}],
        })
        target = path.parent / ("F03-mcp-" + uuid.uuid4().hex + "-roundtrip.prt")
        saved = call("nx_save_copy", {
            **credentials, "operation_id": "f03-save-" + uuid.uuid4().hex,
            "target_path": str(target), "reopen": True,
        })
        credentials.update(ownership_id=saved["new_ownership_id"], binding_epoch=saved["new_binding_epoch"])
        assert saved["saved"] is True and saved["reopened"] is True, saved
        assert set(saved["checks"]) == {"associations", "expression", "geometry", "history"}, saved["checks"]
        assert all(check["status"] == "VERIFIED" for check in saved["checks"].values()), saved["checks"]
        report["saved_path"] = str(target)
        report["inspection_after_reopen"] = call("nx_inspect_part", credentials)
        returned = call("nx_return", credentials)
        credentials.clear()
        assert returned["ownership_state"] == "HUMAN_OWNED" and returned["safe_for_human"] is True, returned
        report["passed"] = True
        return 0
    except Exception as exc:
        report["error"] = repr(exc)
        if credentials:
            try:
                report["failure_return"] = call("nx_return", credentials)
            except Exception as return_error:
                report["failure_return_error"] = repr(return_error)
        return 1
    finally:
        persist()
        client.close()
        print(json.dumps({key: report[key] for key in ("passed", "nx_pid", "saved_path", "error") if key in report}, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
