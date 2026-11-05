"""F14 installed MCP smoke. Only the operator may start NX and open the fixture.

Creates no NX process and never installs or replaces product binaries. A pass
requires model evidence; --ready is discovery only and is not acceptance.
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
import uuid

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools" / "v2"))
from f01_f02_mcp_client import McpStdioClient
from f14_hole_oracle import bore_removed_volume_mm3, counterbore_removed_volume_mm3

spec = importlib.util.spec_from_file_location("f13_smoke_helpers", Path(__file__).with_name("smoke-f13-mcp.py"))
helpers = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helpers)

VERSION = "0.14.0"
COUNT = 33
TERMINAL = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
COMMON = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id", "position_mm", "direction", "kind", "depth_mode"}
FIELDS = {"simple": {"diameter_mm"}, "counterbore": {"diameter_mm", "counterbore_diameter_mm", "counterbore_depth_mm"},
          "countersink": {"diameter_mm", "countersink_diameter_mm", "countersink_angle_deg"},
          "threaded": {"thread_standard", "thread_size", "radial_engage", "pitch_mm", "hand", "thread_depth_mm"}}
VARIANTS = {
    "simple": {"diameter_mm": 4}, "counterbore": {"diameter_mm": 4, "counterbore_diameter_mm": 8, "counterbore_depth_mm": 3},
    "countersink": {"diameter_mm": 4, "countersink_diameter_mm": 8, "countersink_angle_deg": 90},
    "threaded": {"thread_standard": "Metric Coarse", "thread_size": "M5 x 0.8", "radial_engage": "0.75", "pitch_mm": 0.8, "hand": "left", "thread_depth_mm": 7}}


def uid(label):
    return "f14-" + label + "-" + uuid.uuid4().hex


def schema_invalid_requests(credentials, revision_id, target_body_id):
    invalid, bases = [], {}
    for kind, fields in VARIANTS.items():
        for mode in ("blind", "through"):
            base = {"operation_id": uid("invalid"), **credentials, "revision_id": revision_id,
                    "target_body_id": target_body_id, "position_mm": [15, 15, 20], "direction": [0, 0, -1],
                    "kind": kind, "depth_mode": mode, **fields}
            if mode == "blind": base["hole_depth_mm"] = 10
            bases[(kind, mode)] = base
            required_field = "pitch_mm" if kind == "threaded" else "diameter_mm"
            invalid.append({k: v for k, v in base.items() if k != required_field})
    simple = bases[("simple", "through")]
    invalid.extend([{**simple, "direction": [0, 0, 0]}, {**simple, "diameter_mm": True},
                    {**simple, "hole_depth_mm": 10}, {**simple, "thread_depth_mm": 7},
                    {**bases[("countersink", "blind")], "countersink_angle_deg": 180},
                    {**bases[("threaded", "blind")], "hand": "bad"}])
    for request in invalid:
        request["operation_id"] = uid("invalid")
    return invalid


def verify_schema(tools):
    matches = [x for x in tools if isinstance(x, dict) and x.get("name") == "nx_prepare_hole"]
    if len(matches) != 1:
        raise AssertionError("nx_prepare_hole must occur exactly once")
    schema = matches[0].get("inputSchema", {})
    variants = schema.get("oneOf")
    if schema.get("type") != "object" or not isinstance(variants, list) or len(variants) != 8:
        raise AssertionError("hole schema must contain eight object variants")
    seen = set()
    for variant in variants:
        props = variant.get("properties", {})
        kind, mode = props.get("kind", {}).get("const"), props.get("depth_mode", {}).get("const")
        if kind not in FIELDS or mode not in ("blind", "through") or (kind, mode) in seen:
            raise AssertionError("hole schema contains invalid or duplicate variant")
        expected = COMMON | FIELDS[kind] | ({"hole_depth_mm"} if mode == "blind" else set())
        if variant.get("additionalProperties") is not False or set(props) != expected or set(variant.get("required", [])) != expected:
            raise AssertionError("hole variant fields, required set, or additionalProperties differs")
        seen.add((kind, mode))
    return {"variants": sorted([list(x) for x in seen]), "required_and_closed": True}


def body_by_id(inspection, body_id):
    found = [b for b in helpers._body_records(inspection) if b.get("journal_id") == body_id]
    if len(found) != 1:
        raise AssertionError("fixture body ID must resolve exactly once")
    return found[0]


def fixture_body_by_bounds(inspection, bounds):
    matches = []
    for body in helpers._body_records(inspection):
        try:
            helpers._assert_bbox(body.get("bbox_mm"), bounds, "fixture body")
        except (AssertionError, TypeError, ValueError):
            continue
        if body.get("is_solid") is True and isinstance(body.get("journal_id"), str):
            matches.append(body)
    if len(matches) != 1:
        raise AssertionError("fixture bounds must identify exactly one current solid body")
    return matches[0]


def assert_definition(definition, request):
    if not isinstance(definition, dict):
        raise AssertionError("fresh Verify omitted direct native hole definition")
    for key in ("kind", "depth_mode", "thread_standard", "thread_size", "radial_engage", "hand"):
        if key in request and definition.get(key) != request[key]:
            raise AssertionError("native definition mismatch: " + key)
    for key in ("diameter_mm", "hole_depth_mm", "counterbore_diameter_mm", "counterbore_depth_mm", "pitch_mm", "thread_depth_mm"):
        if key in request:
            helpers._close(definition.get(key), request[key], label=key, absolute=1e-6)
    for key in ("position_mm", "direction"):
        actual = helpers._vector(definition.get(key), key, 3)
        for i, expected in enumerate(request[key]):
            helpers._close(actual[i], expected, label=key, absolute=1e-6)
    if definition.get("boolean_type") != "Subtract":
        raise AssertionError("native definition does not show subtract")
    if request["kind"] == "threaded" and (definition.get("thread_type") != "Symbolic" or definition.get("thread_internal") is not True):
        raise AssertionError("native thread is not an internal symbolic thread")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--ready", action="store_true")
    group.add_argument("--part-path", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-server-version", default=VERSION)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = {"test": "F14 installed MCP", "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "passed": False, "mode": "ready" if args.ready else "modeling", "cases": [], "calls": [],
              "limitations": ["single fixture; no reliability or compliance claim", "thread readback is symbolic and does not establish physical thread geometry", "threaded volume has no independent oracle in this smoke", "sentinel inspection compares identity, topology, bounds and legacy measurements, not a full geometric equivalence proof", "CAD thresholds are not physical measurement uncertainty"],
              "configuration": {"expected_server_version": args.expected_server_version, "expected_tool_count": COUNT,
                                "units": "mm, degrees, mm^3", "sample_size": 1,
                                "physical_instrumentation": False, "calibration": "not applicable to digital NX readback",
                                "nx_started_or_closed_by_harness": False},
              "unknown_events": [], "execute_ids": [], "current_case": None}
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream)
    client = None
    creds = {}
    frozen = False
    active_id = None

    def persist():
        with args.output.open("w", encoding="utf-8", newline="\n") as stream:
            json.dump(helpers._redact(report), stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")

    def call(tool, arguments):
        nonlocal frozen
        if frozen and not (tool == "nx_get_operation" and arguments.get("operation_id") == active_id):
            raise RuntimeError("UNKNOWN gate suppresses all calls except original operation query")
        row = {"tool": tool, "arguments": helpers._redact(arguments), "case_id": report["current_case"]}
        report["calls"].append(row)
        try:
            response = client.request("tools/call", {"name": tool, "arguments": arguments})
        except Exception as exc:
            response = client.last_response
            if response is None:
                row["transport_error_type"] = type(exc).__name__
                persist()
                raise
            row["protocol_response"] = helpers._redact(response)
            row["is_error"] = True
            persist()
            return {}, row
        row["response"] = helpers._redact(response)
        row["is_error"] = response.get("isError") is True
        row["payload"] = helpers._redact(helpers._decode_payload(response))
        persist()
        return helpers._decode_payload(response), row

    def sync(tool, arguments=None):
        data, row = call(tool, arguments or {})
        if row["is_error"]:
            raise AssertionError(tool + " returned an MCP error; see recorded response")
        return data

    def unknown(op_id, reason):
        nonlocal frozen, active_id
        frozen, active_id = True, op_id
        event = {"operation_id": op_id, "reason": reason, "action": "query original ID only; freeze all subsequent writes"}
        report["unknown_events"].append(event)
        persist()
        try:
            data, row = call("nx_get_operation", {"operation_id": op_id})
            event["resolution_response"] = helpers._redact(data)
            event["resolution_is_error"] = row["is_error"]
        except Exception as exc:
            event["resolution_error_type"] = type(exc).__name__
        persist()
        raise RuntimeError("operation outcome UNKNOWN; all subsequent writes frozen")

    def operation(tool, arguments):
        op_id = arguments["operation_id"]
        try:
            data, row = call(tool, arguments)
        except Exception:
            unknown(op_id, "initial transport failure")
        if row["is_error"]:
            unknown(op_id, "asynchronous request error does not prove the operation outcome")
        if data.get("operation_id") != op_id:
            unknown(op_id, "missing or mismatched operation ID")
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
        if data.get("state") == "UNKNOWN":
            unknown(op_id, "operation reported UNKNOWN")
        result = helpers._operation_result(data)
        if isinstance(result, dict) and result.get("mutation_state") == "UNKNOWN":
            unknown(op_id, "operation reported UNKNOWN mutation state")
        if data.get("state") != "SUCCEEDED":
            raise AssertionError(tool + " terminal state " + str(data.get("state")))
        if not isinstance(result, dict):
            raise AssertionError(tool + " omitted operation result")
        return data, result

    def revision():
        value = sync("nx_get_session", creds).get("revision_id")
        if not isinstance(value, str) or not value:
            raise AssertionError("missing revision_id")
        return value

    def inspect():
        return sync("nx_inspect_part", creds)

    def set_case(name):
        report["current_case"] = name
        row = {"case_id": name, "state": "RUNNING", "retest_criterion": "Correct the reported failure and rerun on a new saved F14 fixture; preserve this report."}
        report["cases"].append(row)
        persist()
        return row

    try:
        metadata = None
        if args.part_path:
            path = args.part_path.resolve(strict=True)
            root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
            if not path.is_relative_to(root) or not path.name.startswith("F14-mcp-fixture-") or path.suffix.lower() != ".prt":
                raise ValueError("fixture must be a new F14 part under workspace/v2/models")
            meta_path = ROOT / "workspace" / "v2" / "evidence" / (path.stem + ".json")
            metadata = json.loads(meta_path.read_text(encoding="utf-8"))
            if (Path(metadata["part_path"]).resolve() != path or metadata.get("saved") is not True or
                    metadata.get("bridge_called") is not False or metadata.get("body_count") != 2):
                raise AssertionError("fixture metadata does not establish an isolated saved part")
            report["fixture"] = metadata
        install = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy()
        env.update(PYTHONPATH=str(install / "app" / "src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        command = [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install / "mcp-config.json")]
        client = McpStdioClient(subprocess.Popen(command, cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0), 60, max_line_bytes=16*1024*1024)
        init = client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "f14-installed-smoke", "version": "1"}})
        client.notify("notifications/initialized")
        report["initialize"] = helpers._redact(init)
        if init.get("serverInfo", {}).get("version") != args.expected_server_version:
            raise AssertionError("installed server version mismatch")
        tools = client.request("tools/list").get("tools")
        if not isinstance(tools, list) or len(tools) != COUNT or len({x.get("name") for x in tools}) != COUNT:
            raise AssertionError("installed tool count or unique names mismatch")
        report["schema_contract"] = verify_schema(tools)
        report["tools_list"] = helpers._redact(tools)
        ready = sync("nx_ensure_ready")
        report["ready"] = helpers._redact(ready)
        if ready.get("state") != "READY":
            raise AssertionError("bridge is not READY")
        status = sync("nx_bridge_status")
        report["initial_status"] = helpers._redact(status)
        if status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False or str(status.get("in_flight")) != "0":
            raise AssertionError("bridge must initially be idle and HUMAN_OWNED")
        if args.ready:
            report["diagnostic_only"] = True
            report["passed"] = True
            return 0
        takeover = sync("nx_takeover", {"expected_pid": status["pid"], "expected_part_path": str(path), "artifact_scope": "saved_independent_copy", "authorize": True})
        if not isinstance(takeover.get("ownership_id"), str) or not isinstance(takeover.get("binding_epoch"), int):
            raise AssertionError("takeover omitted ownership credentials")
        creds.update(ownership_id=takeover["ownership_id"], binding_epoch=takeover["binding_epoch"])
        caps = sync("nx_get_capabilities")
        report["capabilities"] = helpers._redact(caps)
        if caps.get("model_hole_write_enabled") is not True or "operation.prepare_hole" not in caps.get("available_operations", []):
            raise AssertionError("installed hole adapter is unavailable")
        initial = inspect()
        if helpers._inspect_path(initial).resolve() != path or len(helpers._body_records(initial)) != 2:
            raise AssertionError("initial part or body count mismatch")
        # Re-resolve in the live loaded part. Metadata JIDs belong to the
        # fixture-creation session and are not assumed stable across loading.
        target = fixture_body_by_bounds(initial, metadata["target_bbox_mm"])["journal_id"]
        sentinel = fixture_body_by_bounds(initial, metadata["sentinel_bbox_mm"])["journal_id"]
        report["current_fixture_body_ids"] = {"target": target, "sentinel": sentinel}
        for body_id, bbox in ((target, metadata["target_bbox_mm"]), (sentinel, metadata["sentinel_bbox_mm"])):
            body = body_by_id(initial, body_id)
            if body.get("is_solid") is not True:
                raise AssertionError("fixture body is not solid")
            helpers._assert_bbox(body.get("bbox_mm"), bbox, "fixture bounds")
        sentinel_record = body_by_id(initial, sentinel)
        sentinel_before = helpers._structural_body_signature(sentinel_record)
        sentinel_identity = {key: sentinel_record.get(key) for key in ("face_ids", "edge_ids", "volume_mm3", "surface_area_mm2")}

        schema_case = set_case("F14_schema_rejections")
        rejected = 0
        before = helpers._part_signature(inspect())
        for request in schema_invalid_requests(creds, revision(), target):
            result, entry = call("nx_prepare_hole", request)
            if (not entry["is_error"] or
                    entry.get("protocol_response", {}).get("error", {}).get("code") != -32602):
                raise AssertionError("invalid request did not receive JSON-RPC invalid-arguments rejection")
            rejected += 1
        if before != helpers._part_signature(inspect()):
            raise AssertionError("invalid requests changed part inventory")
        schema_case.update(state="PASS", rejected_count=rejected, part_unchanged=True)
        persist()

        total = 80 * 40 * 20
        cases = [
            ("simple_through", {"kind": "simple", "depth_mode": "through", "position_mm": [15, 15, 20], "diameter_mm": 4}, bore_removed_volume_mm3(4, 20)),
            ("counterbore_through", {"kind": "counterbore", "depth_mode": "through", "position_mm": [40, 15, 20], "diameter_mm": 4, "counterbore_diameter_mm": 8, "counterbore_depth_mm": 3}, counterbore_removed_volume_mm3(4, 20, 8, 3)),
            ("threaded_through", {"kind": "threaded", "depth_mode": "through", "position_mm": [65, 15, 20], **VARIANTS["threaded"]}, None)]
        for name, fields, removed in cases:
            case = set_case("F14_" + name)
            request = {"operation_id": uid("prepare"), **creds, "revision_id": revision(), "target_body_id": target, "direction": [0, 0, -1], **fields}
            _, prepared = operation("nx_prepare_hole", request)
            plan = prepared.get("plan", {})
            plan_id = plan.get("plan_id")
            if not isinstance(plan_id, str) or not plan_id:
                raise AssertionError("prepare omitted plan_id")
            execute_id = uid("execute")
            if execute_id in report["execute_ids"]:
                raise AssertionError("execute ID reused")
            report["execute_ids"].append(execute_id)
            executed_op, executed = operation("nx_execute_plan", {"operation_id": execute_id, **creds, "plan_id": plan_id})
            if executed.get("mutation_state") != "APPLIED" or executed.get("plan_id") != plan_id:
                raise AssertionError("execute did not apply the prepared plan")
            affected = executed.get("body_ids")
            if not isinstance(affected, list) or len(affected) != 1 or not isinstance(affected[0], str):
                raise AssertionError("execute omitted its unique current affected body ID")
            target = affected[0]
            for _ in range(2):
                try:
                    queried, entry = call("nx_get_operation", {"operation_id": execute_id})
                except Exception:
                    unknown(execute_id, "explicit get transport failure")
                if entry["is_error"] or queried.get("operation_id") != execute_id or queried.get("state") == "UNKNOWN":
                    unknown(execute_id, "explicit get returned error, UNKNOWN, or wrong ID")
                if queried.get("operation_id") != execute_id or queried.get("state") != "SUCCEEDED" or helpers._operation_result(queried) != helpers._operation_result(executed_op):
                    raise AssertionError("explicit get differs from execute result")
            verify_id = uid("verify")
            _, verified = operation("nx_verify_part", {"operation_id": verify_id, **creds, "execute_operation_id": execute_id})
            helpers._assert_verified(verified, name)
            if verified.get("execute_operation_id") != execute_id or verified.get("plan_id") != plan_id:
                raise AssertionError("fresh verification provenance mismatch")
            evidence = verified.get("evidence")
            if (not isinstance(evidence, dict) or not isinstance(executed.get("feature_id"), str) or
                    evidence.get("feature_id") != executed.get("feature_id") or
                    not isinstance(evidence.get("body_ids"), list) or target not in evidence["body_ids"]):
                raise AssertionError("fresh verification omitted matching native feature and target provenance")
            definition = helpers._find_dict(verified, "native_definition")
            assert_definition(definition, request)
            if removed is not None:
                total -= removed
                bodies = evidence.get("bodies", [])
                matches = [b for b in bodies if b.get("journal_id") == target]
                if len(matches) != 1 or matches[0].get("measurement_status") != "MEASURED":
                    raise AssertionError("fresh verification omitted measured target body")
                helpers._close(matches[0].get("volume_mm3"), total, label=name + " independent volume", absolute=1e-5, relative=1e-8)
                helpers._assert_bbox(matches[0].get("bbox_mm"), metadata["target_bbox_mm"], name + " bounds")
            inspection = inspect()
            sentinel_current = body_by_id(inspection, sentinel)
            if (helpers._structural_body_signature(sentinel_current) != sentinel_before or
                    {key: sentinel_current.get(key) for key in sentinel_identity} != sentinel_identity):
                raise AssertionError("non-target sentinel structure changed")
            case.update(state="PASS", execute_operation_id=execute_id, verify_operation_id=verify_id,
                        expected_volume_mm3=total if removed is not None else None,
                        native_definition=definition, non_target_unchanged=True,
                        thread_claim="symbolic standard parameters only" if fields["kind"] == "threaded" else None)
            persist()

        case = set_case("F14_save_copy_reopen_and_safe_return")
        before = [helpers._fingerprint(x) for x in helpers._body_records(inspect())]
        copy = path.parent / ("F14-mcp-" + uuid.uuid4().hex + "-roundtrip.prt")
        if copy.exists(): raise FileExistsError("SaveCopy destination exists")
        _, saved = operation("nx_save_copy", {"operation_id": uid("save"), **creds, "target_path": str(copy), "reopen": True})
        if not isinstance(saved.get("new_ownership_id"), str) or not isinstance(saved.get("new_binding_epoch"), int):
            raise AssertionError("SaveCopy omitted rebound ownership")
        creds.update(ownership_id=saved["new_ownership_id"], binding_epoch=saved["new_binding_epoch"])
        checks = saved.get("checks", {})
        if saved.get("saved") is not True or saved.get("reopened") is not True or set(checks) != {"associations", "expression", "geometry", "history"} or any(not isinstance(x, dict) or x.get("status") != "VERIFIED" for x in checks.values()):
            raise AssertionError("SaveCopy/reopen checks are incomplete")
        after = inspect()
        if helpers._inspect_path(after).resolve() != copy.resolve(): raise AssertionError("wrong reopened part")
        helpers._assert_roundtrip(before, [helpers._fingerprint(x) for x in helpers._body_records(after)])
        returned = sync("nx_return", creds)
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("return did not confirm safe human ownership")
        creds.clear()
        status = sync("nx_bridge_status")
        if status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False or str(status.get("in_flight")) != "0":
            raise AssertionError("final bridge state is not idle HUMAN_OWNED")
        case.update(state="PASS", saved_path=str(copy), final_status=helpers._redact(status))
        report["passed"] = True
        return 0
    except Exception as exc:
        report["failure"] = {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, (AssertionError, ValueError, FileNotFoundError)) else "see recorded response or transport error type", "case_id": report["current_case"],
                             "reproduction": "Open a new F14 fixture and rerun the same installed smoke command; inspect recorded request and response.",
                             "retest": "Correct the defect and rerun on a new fixture; require all cases and safe return to pass."}
        if report["cases"] and report["cases"][-1]["state"] == "RUNNING":
            report["cases"][-1]["state"] = "FAIL"
        persist()
        if creds and client and not frozen:
            try:
                status = sync("nx_bridge_status")
                if status.get("ownership_state") == "AI_OWNED" and status.get("writes_halted") is False and str(status.get("in_flight")) == "0":
                    report["failure_safe_return"] = helpers._redact(sync("nx_return", creds))
                else:
                    report["failure_safe_return_skipped"] = "bridge not known idle and AI_OWNED"
            except Exception as return_error:
                report["failure_return_error_type"] = type(return_error).__name__
        elif frozen:
            report["failure_safe_return_skipped"] = "UNKNOWN: no subsequent writes"
        return 1
    finally:
        report["completed_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        expected = ["F14_schema_rejections", "F14_simple_through", "F14_counterbore_through", "F14_threaded_through", "F14_save_copy_reopen_and_safe_return"]
        if not args.ready:
            completed = {row["case_id"] for row in report["cases"]}
            report["not_executed"] = [name for name in expected if name not in completed]
        if client:
            try: client.close()
            except Exception as exc: report["client_close_error_type"] = type(exc).__name__
        persist()
        print(json.dumps({"passed": report["passed"], "mode": report["mode"], "evidence": str(args.output), "case_id": report["current_case"]}, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
