"""F20 installed MCP acceptance; only an already-open isolated F20 fixture.

The sole operator starts NX separately. Any unresolved/UNKNOWN asynchronous
outcome stops all subsequent calls. Only a known SUCCEEDED execute is repeated.
Actual curve evaluation in a separate reopened session supplies independent
NX geometry evidence; response assertions alone do not replace it.
"""
from __future__ import annotations

import argparse
import datetime
import importlib.util
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time
import traceback
import uuid

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/v2"))
from f01_f02_mcp_client import McpStdioClient


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


h = module("f20_neutral_protocol_helpers", Path(__file__).with_name("smoke-f13-mcp.py"))
old = module("f20_neutral_unknown_guard", Path(__file__).with_name("smoke-f19-mcp.py"))
oracle = module("f20_independent_oracle", ROOT / "tools/v2/f20-geometry-oracle.py")
TERMINAL = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
COMMON = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "curve_type"}
FIELDS = COMMON | {"definition_mode", "start_mm", "through_mm", "end_mm", "center_mm", "normal", "x_axis",
                   "radius_mm", "start_angle_deg", "end_angle_deg"}
CASES = (
    ("world_line", {"curve_type":"line", "start_mm":[10,20,30], "end_mm":[14,26,38]}),
    ("negative_major_center_arc", {"curve_type":"arc", "definition_mode":"center", "center_mm":[40,-10,12],
        "normal":[2/3,-1/3,2/3], "x_axis":[1/math.sqrt(5),2/math.sqrt(5),0], "radius_mm":8,
        "start_angle_deg":35, "end_angle_deg":-225}),
    ("three_point_major_arc", {"curve_type":"arc", "definition_mode":"three_points",
        "start_mm":[75,30,10], "through_mm":[65,30,10], "end_mm":[70,26,7]}),
    ("inclined_circle", {"curve_type":"circle", "center_mm":[110,-20,30], "normal":[2/7,-3/7,6/7],
        "x_axis":[3/math.sqrt(13),2/math.sqrt(13),0], "radius_mm":4.25}),
)


def uid(label):
    return "f20-" + label + "-" + uuid.uuid4().hex


def signature(value):
    result = h._part_signature(value)
    result["spatial_curves"] = value.get("spatial_curves")
    return result


def schema_contract(tools):
    found = [t for t in tools if t.get("name") == "nx_prepare_spatial_curve"]
    if len(found) != 1:
        raise AssertionError("spatial curve tool must occur once")
    schema = found[0].get("inputSchema", {})
    if (schema.get("additionalProperties") is not False or set(schema.get("properties", {})) != FIELDS or
            set(schema.get("required", [])) != COMMON or len(schema.get("oneOf", [])) != 4):
        raise AssertionError("installed spatial-curve schema differs from contract")
    if schema["properties"]["curve_type"].get("enum") != ["line", "arc", "circle"]:
        raise AssertionError("installed curve type enum differs")


def geometry_check(request, evidence):
    if evidence.get("kind") != "create_spatial_curve" or not isinstance(evidence.get("curve_id"), str):
        raise AssertionError("missing actual spatial curve identity")
    g = evidence.get("geometry", {})
    p = evidence.get("model_preservation", {})
    if g.get("status") != "VERIFIED" or p.get("status") != "VERIFIED":
        raise AssertionError("geometry/preservation not verified")
    if any(p.get(k) is not True for k in ("prior_curves", "unique_new_curve", "independent_of_sketch",
                                         "other_semantic_state", "inventory", "body_geometry", "sketch_geometry")):
        raise AssertionError("missing required preservation check")
    expected = oracle.expected(request)
    def point(name, target):
        actual = g.get(name)
        if not isinstance(actual, list) or len(actual) != 3:
            raise AssertionError("actual " + name + " is not a three-vector")
        actual = [float(v) for v in actual]
        if not all(math.isfinite(v) for v in actual) or math.dist(actual, target) > 1e-6:
            raise AssertionError("actual " + name + " differs from independent request geometry")
    point("start_mm", expected["points"][0]); point("end_mm", expected["points"][-1])
    native_type = "Line" if request["curve_type"] == "line" else "Arc"
    if evidence.get("native_curve_type") != native_type:
        raise AssertionError("unexpected NX native curve type")
    if native_type == "Arc":
        point("center_mm", expected["center_mm"]); point("mid_mm", expected["points"][2])
        if not all(math.isfinite(float(g.get(k, float("nan")))) for k in
                   ("radius_mm", "requested_signed_sweep_rad", "native_sweep_rad")):
            raise AssertionError("nonfinite native radius or sweep")
        if abs(float(g.get("radius_mm", -1))-expected["radius_mm"]) > 1e-6:
            raise AssertionError("measured radius differs")
        if abs(float(g.get("requested_signed_sweep_rad", 0))-expected["sweep_rad"]) > 1e-7:
            raise AssertionError("directed sweep differs")
        if abs(float(g.get("native_sweep_rad", 0))-abs(expected["sweep_rad"])) > 1e-7:
            raise AssertionError("native parameter sweep differs")
        sign = -1 if expected["sweep_rad"] < 0 else 1
        native_normal = [float(v) for v in g.get("native_normal", [])]
        if len(native_normal) != 3 or not all(math.isfinite(v) for v in native_normal) or math.dist(native_normal, [sign*v for v in expected["normal"]]) > 1e-7:
            raise AssertionError("native directed normal differs")
        mapping = "reversed_native_normal_for_negative_sweep" if sign < 0 else "same_normal"
        if g.get("native_direction_mapping") != mapping:
            raise AssertionError("native direction mapping differs")
        if request.get("definition_mode") == "three_points":
            point("through_mm", request["through_mm"])
    return {"passed":True, "scope":"response readback vs independent request formula; NX evaluation separately reopened",
            "position_limit_mm":1e-6, "angle_limit_rad":1e-7}


class Run:
    def __init__(self, output):
        self.output = output
        self.client = None
        self.credentials = {}
        self.frozen = False
        self.report = {"test":"F20 installed MCP", "passed":False, "calls":[], "cases":[], "unknown_events":[],
                       "current_case":None, "started_utc":datetime.datetime.now(datetime.timezone.utc).isoformat(),
                       "limitations":["one sample per input path; no reliability/manufacturing accuracy claim",
                                      "independent NX evaluation is a separate read-only reopened run"]}
        with output.open("x", encoding="utf-8") as stream:
            json.dump(self.report, stream)

    def persist(self):
        self.output.write_text(json.dumps(h._redact(self.report), ensure_ascii=False, allow_nan=False, indent=2)+"\n", encoding="utf-8")

    def unknown(self, operation_id, reason):
        self.frozen = True
        self.report["unknown_events"].append({"operation_id":operation_id, "reason":reason,
            "action":"stop all calls; no query, replay, return or thaw"})
        self.persist()
        raise RuntimeError("UNKNOWN outcome")

    def request(self, method, params=None):
        if self.frozen:
            raise RuntimeError("UNKNOWN gate prohibits all calls")
        row = {"method":method, "params":h._redact(params), "case_id":self.report["current_case"]}
        self.report["calls"].append(row)
        try:
            value = self.client.request(method, params)
            row["response"] = h._redact(value)
            return value
        finally:
            row["raw_protocol_response"] = h._redact(self.client.last_response)
            self.persist()

    def call(self, name, arguments=None):
        response = self.request("tools/call", {"name":name, "arguments":arguments or {}})
        if response.get("isError") is True:
            raise AssertionError(name+" returned MCP error")
        value = h._decode_payload(response)
        if old.contains_unknown(value):
            self.unknown((arguments or {}).get("operation_id"), "response reports UNKNOWN")
        return value

    def operation(self, name, arguments):
        op_id = arguments["operation_id"]
        try:
            value = self.call(name, arguments)
        except Exception:
            if self.frozen:
                raise
            self.unknown(op_id, "initial asynchronous response unresolved")
        deadline = time.monotonic()+120
        while True:
            if value.get("operation_id") != op_id:
                self.unknown(op_id, "mismatched operation identity")
            if value.get("state") in TERMINAL:
                break
            if time.monotonic() > deadline:
                self.unknown(op_id, "query deadline")
            time.sleep(0.2)
            try:
                value = self.call("nx_get_operation", {"operation_id":op_id})
            except Exception:
                if self.frozen:
                    raise
                self.unknown(op_id, "operation query unresolved")
        result = h._operation_result(value)
        if value.get("state") != "SUCCEEDED" or not isinstance(result, dict):
            raise AssertionError(name+" failed; preserve original response")
        return value, result

    def case(self, name):
        self.report["current_case"] = name
        row = {"case_id":name, "state":"RUNNING"}
        self.report["cases"].append(row); self.persist()
        return row

    def inspect(self):
        return self.call("nx_inspect_part", self.credentials)

    def revision(self):
        value = self.call("nx_get_session", self.credentials).get("revision_id")
        if not isinstance(value, str) or not value:
            raise AssertionError("no current revision")
        return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--part-path", type=Path, required=True)
    parser.add_argument("--fixture-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-server-version", default="0.20.0")
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    run = Run(args.output)
    try:
        path = args.part_path.resolve(strict=True)
        if not path.is_relative_to((ROOT / "workspace/v2/models").resolve()) or not path.name.startswith("F20-mcp-fixture-"):
            raise ValueError("requires dedicated F20 fixture")
        metadata = json.loads(args.fixture_report.read_text(encoding="utf-8-sig"))
        if (Path(metadata["part_path"]).resolve() != path or metadata.get("stage") != "COMPLETE" or
                metadata.get("saved") is not True or metadata.get("reopened") is not True or metadata.get("reopened_dirty") is not False):
            raise AssertionError("fixture not saved, reopened and clean")
        run.report["fixture"] = metadata
        install = Path(os.environ.get("CODEX_HOME", str(Path.home()/".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy(); env.update(PYTHONPATH=str(install/"app/src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen([sys.executable,"-B","-m","nx_sketch_mcp","--config",str(install/"mcp-config.json")],
            cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
        run.client = McpStdioClient(process, 60, max_line_bytes=16*1024*1024)
        initialized = run.request("initialize", {"protocolVersion":"2025-11-25", "capabilities":{},
            "clientInfo":{"name":"f20-installed-acceptance", "version":"1"}})
        run.client.notify("notifications/initialized")
        if initialized.get("serverInfo",{}).get("version") != args.expected_server_version:
            raise AssertionError("installed version differs")
        tools = run.request("tools/list").get("tools",[])
        if len(tools) != 39 or len({t.get("name") for t in tools}) != 39:
            raise AssertionError("installed tool count/uniqueness differs")
        schema_contract(tools)
        status = run.call("nx_bridge_status")
        if status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False or str(status.get("in_flight")) != "0":
            raise AssertionError("bridge not idle and human owned")
        takeover = run.call("nx_takeover", {"expected_pid":status["pid"], "expected_part_path":str(path),
            "artifact_scope":"saved_independent_copy", "authorize":True})
        run.credentials.update(ownership_id=takeover["ownership_id"], binding_epoch=takeover["binding_epoch"])
        capabilities = run.call("nx_get_capabilities")
        if (capabilities.get("model_spatial_curve_adapter_available") is not True or capabilities.get("model_spatial_curve_write_enabled") is not True or
                "operation.prepare_spatial_curve" not in capabilities.get("available_operations", [])):
            raise AssertionError("spatial curve adapter unavailable")
        initial = run.inspect()
        if h._inspect_path(initial).resolve() != path or len(h._body_records(initial)) != 1:
            raise AssertionError("initial fixture differs")
        run.report["initial_inspect"] = initial

        row = run.case("strict_input_rejection")
        invalid = (
            {"curve_type":"line", "start_mm":[0,0,0], "end_mm":[0,0,0]},
            {"curve_type":"line", "start_mm":[0,0,0], "end_mm":[1,0,0], "radius_mm":1},
            {"curve_type":"arc", "definition_mode":"three_points", "start_mm":[0,0,0], "through_mm":[1,0,0], "end_mm":[2,0,0]},
            {"curve_type":"circle", "center_mm":[0,0,0], "normal":[0,0,2], "x_axis":[1,0,0], "radius_mm":2},
        )
        for fields in invalid:
            before, rev = signature(run.inspect()), run.revision()
            arguments = {"operation_id":uid("invalid"), **run.credentials, "revision_id":rev, **fields}
            try:
                run.request("tools/call", {"name":"nx_prepare_spatial_curve", "arguments":arguments})
            except Exception:
                raw = run.client.last_response
                if not isinstance(raw, dict) or raw.get("error",{}).get("code") != -32602:
                    raise
            else:
                raise AssertionError("invalid input accepted")
            if signature(run.inspect()) != before or run.revision() != rev:
                raise AssertionError("invalid input mutated model")
        row.update(state="PASS", rejected_count=len(invalid)); run.persist()

        for name, fields in CASES:
            row = run.case(name)
            before, rev = run.inspect(), run.revision()
            _, prepared = run.operation("nx_prepare_spatial_curve", {"operation_id":uid("prepare"), **run.credentials,
                "revision_id":rev, **fields})
            if signature(run.inspect()) != signature(before) or run.revision() != rev:
                raise AssertionError("prepare changed model")
            plan_id = prepared.get("plan",{}).get("plan_id")
            if not isinstance(plan_id, str) or not plan_id:
                raise AssertionError("no prepared plan ID")
            execute_args = {"operation_id":uid("execute"), **run.credentials, "plan_id":plan_id}
            terminal, executed = run.operation("nx_execute_plan", execute_args)
            if executed.get("mutation_state") != "APPLIED":
                raise AssertionError("execute not applied")
            evidence = executed.get("result") if isinstance(executed.get("result"),dict) else executed
            checked = geometry_check(fields, evidence)
            after, after_revision = run.inspect(), run.revision()
            queried = run.call("nx_get_operation", {"operation_id":execute_args["operation_id"]})
            if queried.get("state") != "SUCCEEDED" or h._operation_result(queried) != h._operation_result(terminal):
                raise AssertionError("terminal query differs")
            if name == "world_line":
                repeated, result = run.operation("nx_execute_plan", execute_args)
                if result != executed or signature(run.inspect()) != signature(after) or run.revision() != after_revision:
                    raise AssertionError("known success same-ID execute not idempotent")
                row["same_id_success_replay_unchanged"] = True
            _, verified = run.operation("nx_verify_part", {"operation_id":uid("verify"), **run.credentials,
                "execute_operation_id":execute_args["operation_id"]})
            h._assert_verified(verified, name)
            fresh = verified.get("evidence", {})
            geometry_check(fields, fresh)
            if fresh.get("curve_id") != evidence["curve_id"]:
                raise AssertionError("fresh identifies different curve")
            row.update(state="PASS", request=fields, execution=evidence, fresh=verified, response_oracle=checked,
                terminal_query_matches=True, inspect_after=after)
            run.persist()

        row = run.case("save_copy_reopen_return")
        before = run.inspect()
        copy = path.parent / ("F20-mcp-"+uuid.uuid4().hex+"-roundtrip.prt")
        _, saved = run.operation("nx_save_copy", {"operation_id":uid("save"), **run.credentials, "target_path":str(copy), "reopen":True})
        run.credentials.update(ownership_id=saved["new_ownership_id"], binding_epoch=saved["new_binding_epoch"])
        checks = saved.get("checks",{})
        if saved.get("saved") is not True or saved.get("reopened") is not True or set(checks) != {"associations","expression","geometry","history"} or any(c.get("status") != "VERIFIED" for c in checks.values()):
            raise AssertionError("SaveCopy checks incomplete")
        after = run.inspect()
        if h._inspect_path(after).resolve() != copy.resolve() or len(h._body_records(after)) != 1:
            raise AssertionError("reopened wrong part or body count")
        h._assert_roundtrip([h._fingerprint(b) for b in h._body_records(before)], [h._fingerprint(b) for b in h._body_records(after)])
        manifest = {"model_path":str(copy), "installed_report":str(args.output.resolve()),
            "cases":[{"name":name,"request":fields} for name,fields in CASES]+metadata["sentinels"],
            "solid_bbox_mm":metadata["solid_bbox_mm"]}
        manifest_path = args.output.parent / (args.output.stem+"-roundtrip-manifest.json")
        with manifest_path.open("x", encoding="utf-8") as stream:
            json.dump(manifest,stream,indent=2,allow_nan=False)
        row.update(saved_path=str(copy), roundtrip_manifest=str(manifest_path), inspect_before=before, inspect_after=after)
        returned = run.call("nx_return", run.credentials)
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("return did not prove human safe")
        run.credentials.clear()
        status = run.call("nx_bridge_status")
        if (status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False or
                str(status.get("in_flight")) != "0" or status.get("active_operations") != 0):
            raise AssertionError("final bridge not idle")
        row.update(state="PASS", final_status=status)
        run.report["passed"] = True
        return 0
    except Exception as exc:
        run.report["failure"] = {"type":type(exc).__name__, "message":str(exc) if isinstance(exc,(AssertionError,ValueError,FileNotFoundError)) else "inspect recorded response",
            "case_id":run.report["current_case"], "action":"stop; preserve evidence; no automatic writes or return"}
        if run.report["cases"] and run.report["cases"][-1]["state"] == "RUNNING":
            run.report["cases"][-1]["state"] = "FAIL"
        return 1
    finally:
        run.report["completed_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        done = {r["case_id"] for r in run.report["cases"]}
        run.report["not_executed"] = [n for n in ["strict_input_rejection"]+[n for n,_ in CASES]+["save_copy_reopen_return"] if n not in done]
        if run.client:
            try:
                run.client.close()
            except Exception as exc:
                run.report["client_close_error_type"] = type(exc).__name__
        run.persist()
        print(json.dumps({"passed":run.report["passed"],"evidence":str(args.output),"case_id":run.report["current_case"]}))


if __name__ == "__main__":
    raise SystemExit(main())
