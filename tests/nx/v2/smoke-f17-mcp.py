"""F17 installed MCP four single-edge configurations; coordinated NX window only."""
from __future__ import annotations
import argparse
import datetime
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/v2"))
from f01_f02_mcp_client import McpStdioClient

def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result

h = module("f13_smoke_helpers", Path(__file__).with_name("smoke-f13-mcp.py"))
f15 = module("f15_smoke_helpers", Path(__file__).with_name("smoke-f15-mcp.py"))
oracle = module("f17_oracle", ROOT / "tools/v2/f17-geometry-oracle.py")
FIELDS = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id", "edge_ids", "mode", "reference_axis", "distance_mm", "second_distance_mm", "angle_deg"}
TERMINAL = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}


def uid(label):
    return "f17-" + label + "-" + uuid.uuid4().hex


def schema_contract(tools):
    found = [x for x in tools if x.get("name") == "nx_prepare_chamfer"]
    if len(found) != 1: raise AssertionError("chamfer tool must occur once")
    s = found[0].get("inputSchema", {})
    if s.get("additionalProperties") is not False or set(s.get("properties", {})) != FIELDS or s["properties"]["edge_ids"].get("maxItems") != 1 or len(s.get("oneOf", [])) != 3:
        raise AssertionError("chamfer schema mismatch")


def fixture_edge(body, start, end):
    """Parse current-session NX journal IDs and fail closed on missing/ambiguous edge geometry."""
    candidates = []
    for edge in body.get("edge_ids", []):
        if not isinstance(edge, str):
            continue
        segment = edge.partition("{")[2].partition("}")[0]
        points = re.findall(r"\(([^()]*)\)", segment)
        if len(points) < 3:
            continue
        try:
            first, last = ([float(v.strip()) for v in points[i].split(",")] for i in (0, 2))
        except ValueError:
            continue
        def same(a, b):
            return len(a) == 3 and all(abs(x - y) <= 1e-6 for x, y in zip(a, b))
        if (same(first, start) and same(last, end)) or (same(first, end) and same(last, start)):
            candidates.append(edge)
    if len(candidates) != 1:
        raise AssertionError("expected one current-session edge matching each endpoint pair")
    return candidates[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--part-path", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-server-version", default="0.17.0")
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = {"test": "F17 installed MCP", "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "passed": False, "calls": [], "cases": [], "unknown_events": [], "current_case": None,
              "limitations": ["one sample per single-edge configuration, not reliability or compliance evidence",
                              "public inspection signatures are not full BRep; roundtrip NX audit supplies additional geometry and native readback"],
              "configuration": {"expected_server_version": args.expected_server_version, "units": "mm, mm^3",
                                "sample_size": 1, "physical_instrumentation": False, "calibration": "not applicable"}}
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream)
    client, credentials, frozen, active_id = None, {}, False, None

    def persist():
        with args.output.open("w", encoding="utf-8", newline="\n") as stream:
            json.dump(h._redact(report), stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")

    def call(tool, arguments):
        if frozen:
            raise RuntimeError("UNKNOWN gate: no automatic calls or replay")
        row = {"tool": tool, "arguments": h._redact(arguments), "case_id": report["current_case"]}
        report["calls"].append(row)
        try:
            response = client.request("tools/call", {"name": tool, "arguments": arguments})
        except Exception:
            response = client.last_response
            if response is None:
                persist()
                raise
            row.update(protocol_response=h._redact(response), is_error=True)
            persist()
            return {}, row
        row.update(response=h._redact(response), is_error=response.get("isError") is True,
                   payload=h._redact(h._decode_payload(response)))
        persist()
        return h._decode_payload(response), row

    def sync(tool, arguments=None):
        data, row = call(tool, arguments or {})
        if row["is_error"]:
            raise AssertionError(tool + " returned an MCP error")
        return data

    def unknown(op_id, reason):
        nonlocal frozen, active_id
        frozen, active_id = True, op_id
        report["unknown_events"].append({"operation_id": op_id, "reason": reason,
                                         "action": "stop all automatic calls and writes; no replay or thaw"})
        persist()
        raise RuntimeError("UNKNOWN outcome; stopped")

    def operation(tool, arguments, allow_rejection=False):
        op_id = arguments["operation_id"]
        try:
            data, row = call(tool, arguments)
        except Exception:
            unknown(op_id, "initial transport failure")
        if row["is_error"] or data.get("operation_id") != op_id:
            unknown(op_id, "initial async error or mismatched operation ID")
        deadline = time.monotonic() + 180
        while data.get("state") not in TERMINAL:
            if time.monotonic() >= deadline:
                unknown(op_id, "poll deadline")
            time.sleep(0.2)
            try:
                data, row = call("nx_get_operation", {"operation_id": op_id})
            except Exception:
                unknown(op_id, "operation query transport failure")
            if row["is_error"] or data.get("operation_id") != op_id:
                unknown(op_id, "operation query error or mismatched ID")
        result = h._operation_result(data)
        if data.get("state") == "UNKNOWN" or isinstance(result, dict) and result.get("mutation_state") == "UNKNOWN":
            unknown(op_id, "operation or mutation state UNKNOWN")
        if allow_rejection and data.get("state") == "FAILED" and tool == "nx_prepare_chamfer":
            return data, result or {}
        if data.get("state") != "SUCCEEDED" or not isinstance(result, dict):
            raise AssertionError(tool + " did not succeed")
        return data, result

    def revision():
        result = sync("nx_get_session", credentials).get("revision_id")
        if not isinstance(result, str) or not result:
            raise AssertionError("missing current revision")
        return result

    def case(name):
        report["current_case"] = name
        row = {"case_id": name, "state": "RUNNING", "retest": "Preserve report; fix and rerun on a new saved fixture."}
        report["cases"].append(row)
        persist()
        return row

    try:
        path = args.part_path.resolve(strict=True)
        root = (ROOT / "workspace/v2/models").resolve(strict=True)
        if not path.is_relative_to(root) or not path.name.startswith("F17-mcp-fixture-") or path.suffix.lower() != ".prt":
            raise ValueError("a new F17 MCP fixture is required")
        metadata = json.loads((ROOT / "workspace/v2/evidence" / (path.stem + ".json")).read_text(encoding="utf-8"))
        if (Path(metadata["part_path"]).resolve() != path or metadata.get("saved") is not True or
                metadata.get("body_count") != 5 or metadata.get("bridge_called") is not False or
                metadata.get("mcp_called") is not False or metadata.get("part_modified_after_save") is not False or
                metadata.get("save_unsaved_parts") != 0 or metadata.get("save_unsaved_objects") != 0):
            raise AssertionError("fixture metadata does not establish isolated saved fixture")
        report["fixture"] = metadata
        install = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy()
        env.update(PYTHONPATH=str(install / "app/src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen([sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install / "mcp-config.json")],
                                   cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
        client = McpStdioClient(process, 60, max_line_bytes=16*1024*1024)
        init = client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                             "clientInfo": {"name": "f17-installed-smoke", "version": "1"}})
        client.notify("notifications/initialized")
        report["initialize"] = h._redact(init)
        if init.get("serverInfo", {}).get("version") != args.expected_server_version:
            raise AssertionError("installed server version mismatch")
        tools = client.request("tools/list").get("tools")
        if not isinstance(tools, list) or len(tools) != 36 or len({x.get("name") for x in tools}) != 36:
            raise AssertionError("installed tool count or uniqueness mismatch")
        schema_contract(tools)
        report["tools_list"] = h._redact(tools)
        if sync("nx_ensure_ready").get("state") != "READY":
            raise AssertionError("bridge is not READY")
        status = sync("nx_bridge_status")
        if status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False or str(status.get("in_flight")) != "0":
            raise AssertionError("bridge must be idle and HUMAN_OWNED")
        takeover = sync("nx_takeover", {"expected_pid": status["pid"], "expected_part_path": str(path),
                                        "artifact_scope": "saved_independent_copy", "authorize": True})
        if not isinstance(takeover.get("ownership_id"), str) or not isinstance(takeover.get("binding_epoch"), int):
            raise AssertionError("takeover omitted credentials")
        credentials.update(ownership_id=takeover["ownership_id"], binding_epoch=takeover["binding_epoch"])
        inspect = lambda: sync("nx_inspect_part", credentials)
        initial = inspect()
        if h._inspect_path(initial).resolve() != path or len(h._body_records(initial)) != 5:
            raise AssertionError("initial part or inventory mismatch")
        target = f15.fixture_body_by_bounds(initial, [0, 0, 0, 20, 12, 10])
        foreign = f15.fixture_body_by_bounds(initial, [40, 0, 0, 60, 12, 10])
        good = fixture_edge(target, [0, 0, 0], [0, 0, 10])
        wrong = fixture_edge(foreign, [40, 0, 0], [40, 0, 10])
        row = case("I02_safe_rejections")
        for label, changes in (("foreign_edge", {"edge_ids": [wrong]}), ("stale_revision", {"revision_id": "deliberately-stale"}), ("edge_axis_reference", {"reference_axis": "z"})):
            before, rev = h._part_signature(inspect()), revision()
            request = {"operation_id":uid("reject"), **credentials, "revision_id":rev, "target_body_id":target["journal_id"], "edge_ids":[good], "mode":"two_distances", "reference_axis":"x", "distance_mm":2.0, "second_distance_mm":3.0, **changes}
            op, result = operation("nx_prepare_chamfer", request, allow_rejection=True)
            if op.get("state") != "FAILED" or h._part_signature(inspect()) != before or revision() != rev: raise AssertionError(label + " did not safely reject")
            row.setdefault("rejections", []).append({"label":label,"state":op.get("state"),"result":h._redact(result)})
        row["state"]="PASS"; persist()
        modes = (("equal_distance","x",{}),("two_distances","x",{"second_distance_mm":3.0}),("distance_angle","y",{"angle_deg":30.0}),("two_distances","x",{"second_distance_mm":3.0}))
        feature_ids=[]
        for i,(mode,axis,extra) in enumerate(modes):
            row=case("I01_"+mode+("_high_corner" if i==3 else "")); before=inspect(); rev=revision(); x=40*i; y=12 if i==3 else 0
            target=f15.fixture_body_by_bounds(before,[x,0,0,x+20,12,10]); edge=fixture_edge(target,[x,y,0],[x,y,10])
            req={"operation_id":uid("prepare"),**credentials,"revision_id":rev,"target_body_id":target["journal_id"],"edge_ids":[edge],"mode":mode,"reference_axis":axis,"distance_mm":2.0,**extra}
            _, prepared=operation("nx_prepare_chamfer",req)
            if h._part_signature(inspect())!=h._part_signature(before) or revision()!=rev: raise AssertionError("prepare modified model")
            plan_id=prepared.get("plan",{}).get("plan_id")
            if not isinstance(plan_id,str) or not plan_id: raise AssertionError("missing plan id")
            execute_id=uid("execute"); executed_op,executed=operation("nx_execute_plan",{"operation_id":execute_id,**credentials,"plan_id":plan_id})
            if executed.get("mutation_state")!="APPLIED" or not isinstance(executed.get("feature_id"),str): raise AssertionError("execute not applied")
            if i==1 and (executed.get("correction_count")!=1 or executed.get("reverse_before")==executed.get("reverse_after") or not isinstance(executed.get("pre_correction_geometry"),dict) or not isinstance(executed.get("pre_correction_definition"),dict) or not isinstance(executed.get("swapped_pre_correction_coverage"),dict) or executed["swapped_pre_correction_coverage"].get("status")!="VERIFIED"): raise AssertionError("missing corrected native geometry or verified swapped coverage")
            after=inspect(); outputs=executed.get("body_ids")
            if not isinstance(outputs,list) or len(outputs)!=1 or f15.other_signatures(before,{target["journal_id"]})!=f15.other_signatures(after,set(outputs)): raise AssertionError("non-target body changed")
            queried, entry=call("nx_get_operation",{"operation_id":execute_id})
            if entry["is_error"] or queried.get("state")!="SUCCEEDED" or h._operation_result(queried)!=h._operation_result(executed_op): unknown(execute_id,"same ID get mismatch")
            _,verified=operation("nx_verify_part",{"operation_id":uid("verify"),**credentials,"execute_operation_id":execute_id}); h._assert_verified(verified,"chamfer")
            definition=h._find_dict(verified,"native_definition");coverage=h._find_dict(verified,"coverage")
            if not isinstance(definition,dict) or definition.get("option")!={"equal_distance":"SymmetricOffsets","two_distances":"TwoOffsets","distance_angle":"OffsetAndAngle"}[mode] or definition.get("rule_edges_match_collector") is not True or not isinstance(coverage,dict) or coverage.get("status")!="VERIFIED": raise AssertionError("native definition or coverage mismatch")
            row.update(state="PASS",execute_operation_id=execute_id,feature_id=executed["feature_id"],native_definition=definition,coverage=coverage,execution=executed,verified=verified);feature_ids.append(executed["feature_id"]);persist()
        row=case("I03_save_copy_reopen_return"); before=[h._fingerprint(x) for x in h._body_records(inspect())]
        copy=path.parent/("F17-mcp-"+uuid.uuid4().hex+"-roundtrip.prt")
        _,saved=operation("nx_save_copy",{"operation_id":uid("save"),**credentials,"target_path":str(copy),"reopen":True})
        if not isinstance(saved.get("new_ownership_id"),str) or not isinstance(saved.get("new_binding_epoch"),int): raise AssertionError("SaveCopy omitted ownership")
        credentials.update(ownership_id=saved["new_ownership_id"],binding_epoch=saved["new_binding_epoch"])
        checks=saved.get("checks",{})
        if saved.get("saved") is not True or saved.get("reopened") is not True or set(checks)!={"associations","expression","geometry","history"} or any(not isinstance(v,dict) or v.get("status")!="VERIFIED" for v in checks.values()): raise AssertionError("SaveCopy checks incomplete")
        after=inspect()
        if h._inspect_path(after).resolve()!=copy.resolve(): raise AssertionError("wrong reopened copy")
        h._assert_roundtrip(before,[h._fingerprint(x) for x in h._body_records(after)])
        returned=sync("nx_return",credentials)
        if returned.get("ownership_state")!="HUMAN_OWNED" or returned.get("safe_for_human") is not True: raise AssertionError("safe return failed")
        credentials.clear();status=sync("nx_bridge_status")
        if status.get("ownership_state")!="HUMAN_OWNED" or status.get("writes_halted") is not False or str(status.get("in_flight"))!="0": raise AssertionError("bridge not idle")
        row.update(state="PASS",saved_path=str(copy),feature_ids=feature_ids,final_status=h._redact(status));report["passed"]=True;return 0
    except Exception as exc:
        report["failure"] = {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, (AssertionError, ValueError, FileNotFoundError)) else "see recorded response or transport type", "case_id": report["current_case"],
                             "reproduction": "Open a new F17 fixture and rerun the recorded command and requests.",
                             "retest": "Correct the defect and rerun on a new fixture; require all cases and safe return."}
        if report["cases"] and report["cases"][-1]["state"] == "RUNNING":
            report["cases"][-1]["state"] = "FAIL"
        persist()
        if credentials and client and not frozen:
            try:
                status = sync("nx_bridge_status")
                if status.get("ownership_state") == "AI_OWNED" and status.get("writes_halted") is False and str(status.get("in_flight")) == "0":
                    report["failure_safe_return"] = h._redact(sync("nx_return", credentials))
                else:
                    report["failure_safe_return_skipped"] = "bridge not known idle and AI_OWNED"
            except Exception as return_error:
                report["failure_return_error_type"] = type(return_error).__name__
        elif frozen:
            report["failure_safe_return_skipped"] = "UNKNOWN: no subsequent calls or writes"
        return 1
    finally:
        report["completed_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        completed = {row["case_id"] for row in report["cases"]}
        report["not_executed"] = [x for x in ("I02_safe_rejections", "I01_equal_distance", "I01_two_distances", "I01_distance_angle", "I01_two_distances_high_corner", "I03_save_copy_reopen_return") if x not in completed]
        if client:
            try:
                client.close()
            except Exception as exc:
                report["client_close_error_type"] = type(exc).__name__
        persist()
        print(json.dumps({"passed": report["passed"], "evidence": str(args.output), "case_id": report["current_case"]}))


if __name__ == "__main__":
    raise SystemExit(main())
