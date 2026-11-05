"""F15 installed MCP smoke. Only the operator may start NX and open the fixture.

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

spec = importlib.util.spec_from_file_location("f13_smoke_helpers", Path(__file__).with_name("smoke-f13-mcp.py"))
helpers = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helpers)

VERSION = "0.15.0"
COUNT = 34
TERMINAL = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
FIELDS = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "operation", "target_body_id", "tool_body_ids", "retain_tools"}


def uid(label):
    return "f15-" + label + "-" + uuid.uuid4().hex


def schema_invalid_requests(credentials, revision_id, target_body_id, tool_body_id):
    base = {"operation_id": uid("invalid"), **credentials, "revision_id": revision_id,
            "operation": "subtract", "target_body_id": target_body_id,
            "tool_body_ids": [tool_body_id], "retain_tools": False}
    invalid = [{k: v for k, v in base.items() if k != field} for field in FIELDS]
    invalid += [{**base, "operation": "union"}, {**base, "tool_body_ids": []},
                {**base, "tool_body_ids": [tool_body_id, tool_body_id]},
                {**base, "tool_body_ids": [target_body_id]}, {**base, "tool_body_ids": [""]},
                {**base, "tool_body_ids": ["x"] * 33}, {**base, "retain_tools": 1},
                {**base, "extra": True}]
    for request in invalid:
        if "operation_id" in request:
            request["operation_id"] = uid("invalid")
    return invalid


def verify_schema(tools):
    matches = [x for x in tools if isinstance(x, dict) and x.get("name") == "nx_prepare_boolean"]
    if len(matches) != 1:
        raise AssertionError("nx_prepare_boolean must occur exactly once")
    schema = matches[0].get("inputSchema", {})
    props = schema.get("properties", {})
    arr = props.get("tool_body_ids", {})
    if (schema.get("type") != "object" or schema.get("additionalProperties") is not False or
            set(props) != FIELDS or set(schema.get("required", [])) != FIELDS or
            set(props.get("operation", {}).get("enum", [])) != {"unite", "subtract", "intersect"} or
            props.get("retain_tools", {}).get("type") != "boolean" or
            arr.get("type") != "array" or arr.get("minItems") != 1 or arr.get("maxItems") != 32 or
            arr.get("uniqueItems") is not True or arr.get("items", {}).get("type") != "string"):
        raise AssertionError("Boolean schema does not match the closed required contract")
    return {"required_and_closed": True, "unique_tools": True}


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


def assert_definition(definition, request, input_tags):
    if not isinstance(definition, dict):
        raise AssertionError("fresh verification omitted native Boolean definition")
    for key in ("operation", "retain_tools"):
        if definition.get(key) != request[key]:
            raise AssertionError("native definition mismatch: " + key)
    if (definition.get("target_body_tags") != [input_tags[0]] or
            sorted(definition.get("tool_body_tags", [])) != sorted(input_tags[1:])):
        raise AssertionError("native definition target/tool tags differ from live input bodies")


def live_body_tag(body):
    refs = body.get("face_refs")
    if not isinstance(refs, list) or not refs:
        raise AssertionError("live input body omitted face references")
    tags = {ref.get("body_tag") for ref in refs if isinstance(ref, dict)}
    if len(tags) != 1 or any(not isinstance(tag, str) or not tag for tag in tags) or len(refs) != sum(isinstance(ref, dict) for ref in refs):
        raise AssertionError("live input body has inconsistent native tags")
    return next(iter(tags))


def body_signature(body):
    return {"structure": helpers._structural_body_signature(body),
            "face_ids": body.get("face_ids"), "edge_ids": body.get("edge_ids"),
            "volume_mm3": body.get("volume_mm3"), "surface_area_mm2": body.get("surface_area_mm2")}


def other_signatures(inspection, excluded):
    return {body["journal_id"]: body_signature(body) for body in helpers._body_records(inspection)
            if body.get("journal_id") not in excluded}


EXPECTED_BOUNDS_MM = {
    "unite_false": [[0, 0, 0, 15, 10, 10]],
    "unite_true": [[40, 0, 0, 55, 10, 10]],
    "subtract_false": [[80, 0, 0, 85, 10, 10]],
    "subtract_true": [[120, 0, 0, 125, 10, 10]],
    "intersect_false": [[165, 0, 0, 170, 10, 10]],
    "intersect_true": [[205, 0, 0, 210, 10, 10]],
    "chained_subtract": [[210, 0, 0, 215, 10, 10]],
    "split": [[240, 0, 0, 243, 10, 10], [247, 0, 0, 250, 10, 10]],
    "multi_intersect": [[280, 0, 0, 288, 10, 10]],
}


def assert_bounds_unordered(bodies, expected_bounds):
    if len(bodies) != len(expected_bounds):
        raise AssertionError("output bounds count mismatch")
    unmatched = list(expected_bounds)
    for body in bodies:
        for i, expected in enumerate(unmatched):
            try:
                helpers._assert_bbox(body.get("bbox_mm"), expected, "independent output bounds")
            except (AssertionError, TypeError, ValueError):
                continue
            unmatched.pop(i)
            break
        else:
            raise AssertionError("output bounds do not match independent fixture geometry")


def assert_failed_restored(before, after, before_revision, after_revision, result):
    before_signature = helpers._part_signature(before)
    after_signature = helpers._part_signature(after)
    before_signature.pop("revision_id", None)
    after_signature.pop("revision_id", None)
    if before_signature != after_signature:
        raise AssertionError("failed Boolean did not restore part snapshot")
    mutation = result.get("mutation_state")
    if result.get("revision_id") != after_revision:
        raise AssertionError("failure result revision differs from current revision")
    if mutation == "UNCHANGED":
        if after_revision != before_revision:
            raise AssertionError("unchanged failure advanced revision")
    elif mutation == "ROLLED_BACK":
        if after_revision == before_revision:
            raise AssertionError("rollback did not advance revision")
        advance = result.get("rollback_revision_advance")
        required = ("undo_called", "dirty_state_restored", "active_sketch_restored", "semantic_restored",
                    "geometry_restored", "history_restored", "bodies_restored", "full_body_geometry_restored",
                    "full_part_curve_inventory_restored", "point_and_direction_inventory_restored",
                    "existing_hole_and_boolean_definitions_restored", "dirty_state_after_boolean_readback_restored")
        if (any(result.get(key) is not True for key in required) or result.get("undo_update_error_count") != 0 or
                not isinstance(advance, dict) or advance.get("prepared_revision_id") != before_revision or
                advance.get("revision_id_after_rollback") != after_revision or
                advance.get("revision_advanced_past_prepared") is not True or
                advance.get("snapshot_state_synchronized") is not True):
            raise AssertionError("Undo or rollback restoration/revision evidence incomplete")
    else:
        raise AssertionError("failed Boolean has unrecognized mutation state")


def assert_same_execute_replay(original_op, original_result, replay_op, replay_result,
                               before_inspection, after_inspection, before_revision, after_revision):
    if (original_op.get("state") != "SUCCEEDED" or replay_op.get("state") != "SUCCEEDED" or
            original_op.get("operation_id") != replay_op.get("operation_id") or
            original_result != replay_result):
        raise AssertionError("same execute ID returned a different terminal result")
    if (helpers._part_signature(before_inspection) != helpers._part_signature(after_inspection) or
            before_revision != after_revision):
        raise AssertionError("repeated execute changed part snapshot or revision")


def assert_result(verified, executed, request, expected_volume, expected_count, input_tags, expected_bounds):
    helpers._assert_verified(verified, request["operation"])
    evidence = verified.get("evidence", {})
    ids = executed.get("body_ids")
    if (not isinstance(ids, list) or len(ids) != expected_count or len(set(ids)) != len(ids) or
            any(not isinstance(x, str) or not x for x in ids) or
            evidence.get("feature_id") != executed.get("feature_id") or
            set(evidence.get("body_ids", [])) != set(ids)):
        raise AssertionError("fresh verification missing matching feature or output bodies")
    bodies = evidence.get("bodies")
    if not isinstance(bodies, list) or len(bodies) != expected_count or {b.get("journal_id") for b in bodies} != set(ids):
        raise AssertionError("fresh verification missing measured output inventory")
    if any(b.get("measurement_status") != "MEASURED" for b in bodies):
        raise AssertionError("output volume was not measured")
    volumes = [b.get("volume_mm3") for b in bodies]
    if any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in volumes):
        raise AssertionError("invalid output volume")
    helpers._close(sum(volumes), expected_volume, label="independent output volume mm3", absolute=1e-5, relative=1e-8)
    assert_bounds_unordered(bodies, expected_bounds)
    assert_definition(helpers._find_dict(verified, "native_definition"), request, input_tags)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--ready", action="store_true")
    group.add_argument("--part-path", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-server-version", default=VERSION)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = {"test": "F15 installed MCP", "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "passed": False, "mode": "ready" if args.ready else "modeling", "cases": [], "calls": [],
              "limitations": ["single fixture; no reliability or compliance claim", "prior Boolean result used as a later target and split is not covered by this fixture", "sentinel inspection compares identity, topology, bounds and legacy measurements, not a full geometric equivalence proof", "MCP smoke has no independent point-in-solid witnesses; volume and bounds do not establish complete geometric equivalence", "CAD thresholds are not physical measurement uncertainty"],
              "configuration": {"expected_server_version": args.expected_server_version, "expected_tool_count": COUNT,
                                "units": "mm, mm^3", "sample_size": 1,
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
        event = {"operation_id": op_id, "reason": reason, "action": "freeze all subsequent writes; no automatic query or retry"}
        report["unknown_events"].append(event)
        persist()
        raise RuntimeError("operation outcome UNKNOWN; all subsequent writes frozen")

    def operation(tool, arguments, expect_rollback=False, allow_safe_failure=False):
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
        if allow_safe_failure and data.get("state") == "FAILED":
            if tool == "nx_prepare_boolean" and result is None:
                return data, {}
            if isinstance(result, dict) and result.get("mutation_state") in ("UNCHANGED", "ROLLED_BACK"):
                return data, result
        if expect_rollback:
            if data.get("state") != "FAILED" or not isinstance(result, dict) or result.get("mutation_state") != "ROLLED_BACK":
                raise AssertionError(tool + " did not fail with verified Undo rollback")
            return data, result
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
        row = {"case_id": name, "state": "RUNNING", "retest_criterion": "Correct the reported failure and rerun on a new saved F15 fixture; preserve this report."}
        report["cases"].append(row)
        persist()
        return row

    try:
        metadata = None
        if args.part_path:
            path = args.part_path.resolve(strict=True)
            root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
            if not path.is_relative_to(root) or not path.name.startswith("F15-mcp-fixture-") or path.suffix.lower() != ".prt":
                raise ValueError("fixture must be a new F15 part under workspace/v2/models")
            meta_path = ROOT / "workspace" / "v2" / "evidence" / (path.stem + ".json")
            metadata = json.loads(meta_path.read_text(encoding="utf-8"))
            if (Path(metadata["part_path"]).resolve() != path or metadata.get("saved") is not True or
                    metadata.get("bridge_called") is not False or metadata.get("mcp_called") is not False or
                    metadata.get("part_modified_after_save") is not False or metadata.get("body_count") != 22 or
                    metadata.get("expected_body_count") != 22 or metadata.get("save_unsaved_parts") != 0 or
                    metadata.get("save_unsaved_objects") != 0):
                raise AssertionError("fixture metadata does not establish an isolated saved part")
            report["fixture"] = metadata
        install = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy()
        env.update(PYTHONPATH=str(install / "app" / "src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        command = [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install / "mcp-config.json")]
        client = McpStdioClient(subprocess.Popen(command, cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0), 60, max_line_bytes=16*1024*1024)
        init = client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "f15-installed-smoke", "version": "1"}})
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
        if caps.get("model_boolean_write_enabled") is not True or "operation.prepare_boolean" not in caps.get("available_operations", []):
            raise AssertionError("installed Boolean adapter is unavailable")
        initial = inspect()
        if helpers._inspect_path(initial).resolve() != path or len(helpers._body_records(initial)) != 22:
            raise AssertionError("initial part or body count mismatch")
        fixture_cases = {c["case"]: c for c in metadata["cases"]}
        expected_cases = {op + "_" + str(retain).lower() for op in ("unite", "subtract", "intersect") for retain in (False, True)} | {"split", "multi_intersect", "disjoint_subtract", "disjoint_intersect", "sentinel"}
        if set(fixture_cases) != expected_cases or len(metadata["cases"]) != len(expected_cases):
            raise AssertionError("fixture case inventory mismatch")
        # Session-specific IDs are resolved from fixture geometry before each operation.
        for fixture in fixture_cases.values():
            for bounds in fixture["bbox_mm"]:
                fixture_body_by_bounds(initial, bounds)
        first = fixture_cases["unite_false"]["bbox_mm"]
        target = fixture_body_by_bounds(initial, first[0])["journal_id"]
        tool = fixture_body_by_bounds(initial, first[1])["journal_id"]
        schema_case = set_case("F15_schema_rejections")
        before = helpers._part_signature(inspect())
        before_revision = revision()
        invalid = schema_invalid_requests(creds, before_revision, target, tool)
        for request in invalid:
            _, entry = call("nx_prepare_boolean", request)
            if not entry["is_error"] or entry.get("protocol_response", {}).get("error", {}).get("code") != -32602:
                raise AssertionError("invalid Boolean input did not receive JSON-RPC invalid-arguments rejection")
        if before != helpers._part_signature(inspect()) or before_revision != revision():
            raise AssertionError("invalid Boolean input changed part or revision")
        schema_case.update(state="PASS", rejected_count=len(invalid), part_unchanged=True)
        persist()

        valid_cases = [(op + "_" + str(retain).lower(), {"unite": 1500, "subtract": 500, "intersect": 500}[op], 1)
                       for op in ("unite", "subtract", "intersect") for retain in (False, True)]
        valid_cases += [("chained_subtract", 500, 1), ("split", 600, 2), ("multi_intersect", 800, 1)]
        chained_ids = None
        for name, expected_volume, output_count in valid_cases + [("disjoint_subtract", None, 0), ("disjoint_intersect", None, 0)]:
            case = set_case("F15_" + name)
            before_inspection = inspect()
            if name == "chained_subtract":
                if chained_ids is None:
                    raise AssertionError("prior intersect result unavailable for chained Boolean")
                fixture = {"operation": "subtract", "retain_tools": False}
                ids = chained_ids
                for body_id in ids:
                    body_by_id(before_inspection, body_id)
            else:
                fixture = fixture_cases[name]
                ids = [fixture_body_by_bounds(before_inspection, bounds)["journal_id"] for bounds in fixture["bbox_mm"]]
            if len(set(ids)) != len(ids):
                raise AssertionError("fixture inputs must be distinct")
            input_tags = [live_body_tag(body_by_id(before_inspection, body_id)) for body_id in ids]
            before_revision = revision()
            request = {"operation_id": uid("prepare"), **creds, "revision_id": before_revision,
                       "operation": fixture["operation"], "target_body_id": ids[0],
                       "tool_body_ids": ids[1:], "retain_tools": fixture["retain_tools"]}
            prepared_op, prepared = operation("nx_prepare_boolean", request, allow_safe_failure=name == "chained_subtract")
            if prepared_op.get("state") == "FAILED":
                if prepared.get("mutation_state"):
                    assert_failed_restored(before_inspection, inspect(), before_revision, revision(), prepared)
                elif helpers._part_signature(before_inspection) != helpers._part_signature(inspect()) or revision() != before_revision:
                    raise AssertionError("chained prepare rejection changed part or revision")
                case.update(state="LIMITATION", reason="chained input safely rejected during prepare", part_unchanged=True)
                report["limitations"].append("chained Boolean was rejected; linked-result support unverified")
                persist()
                continue
            plan_id = prepared.get("plan", {}).get("plan_id")
            if not isinstance(plan_id, str) or not plan_id:
                raise AssertionError("prepare omitted plan_id")
            execute_id = uid("execute")
            report["execute_ids"].append(execute_id)
            failed_geometry = expected_volume is None
            execute_request = {"operation_id": execute_id, **creds, "plan_id": plan_id}
            executed_op, executed = operation("nx_execute_plan", execute_request, expect_rollback=failed_geometry, allow_safe_failure=name == "chained_subtract")
            if name == "chained_subtract" and executed_op.get("state") == "FAILED":
                assert_failed_restored(before_inspection, inspect(), before_revision, revision(), executed)
                case.update(state="LIMITATION", reason="chained input safely rejected during execute", part_unchanged=True)
                report["limitations"].append("chained Boolean was rejected; linked-result support unverified")
                persist()
                continue
            if failed_geometry:
                assert_failed_restored(before_inspection, inspect(), before_revision, revision(), executed)
                case.update(state="PASS", execute_operation_id=execute_id, rollback_verified=True, part_unchanged=True)
                persist()
                continue
            if executed.get("mutation_state") != "APPLIED" or executed.get("plan_id") != plan_id or not isinstance(executed.get("feature_id"), str):
                raise AssertionError("execute omitted applied plan or feature")
            after_execute = inspect()
            after_revision = revision()
            excluded = {ids[0]} | (set(ids[1:]) if not fixture["retain_tools"] else set())
            expected_other = other_signatures(before_inspection, excluded)
            result_ids = executed.get("body_ids")
            if not isinstance(result_ids, list):
                raise AssertionError("execute omitted output IDs")
            if expected_other != other_signatures(after_execute, set(result_ids)):
                raise AssertionError("non-target or retained tool signature changed")
            pre_query = helpers._part_signature(after_execute)
            for _ in range(2):
                try:
                    queried, entry = call("nx_get_operation", {"operation_id": execute_id})
                except Exception:
                    unknown(execute_id, "explicit get transport failure")
                if entry["is_error"] or queried.get("operation_id") != execute_id or queried.get("state") == "UNKNOWN":
                    unknown(execute_id, "explicit get returned error, UNKNOWN, or wrong ID")
                if queried.get("state") != "SUCCEEDED" or helpers._operation_result(queried) != helpers._operation_result(executed_op):
                    raise AssertionError("explicit get differs from execute result")
            if pre_query != helpers._part_signature(inspect()) or after_revision != revision():
                raise AssertionError("same execute ID query changed bodies, features or revision")
            if name == "unite_false":
                # Intentional duplicate only after a known SUCCEEDED outcome, never an UNKNOWN retry.
                before_repeat = inspect()
                before_repeat_revision = revision()
                replay_op, replay_result = operation("nx_execute_plan", execute_request)
                assert_same_execute_replay(executed_op, executed, replay_op, replay_result,
                                           before_repeat, inspect(), before_repeat_revision, revision())
                case.update(same_execute_request_repeated=True, same_execute_result=True,
                            repeat_left_part_and_revision_unchanged=True)
                persist()
            verify_id = uid("verify")
            _, verified = operation("nx_verify_part", {"operation_id": verify_id, **creds, "execute_operation_id": execute_id})
            if verified.get("execute_operation_id") != execute_id or verified.get("plan_id") != plan_id:
                raise AssertionError("fresh verification provenance mismatch")
            assert_result(verified, executed, request, expected_volume, output_count, input_tags, EXPECTED_BOUNDS_MM[name])
            if name == "intersect_true":
                chained_ids = [ids[1], result_ids[0]]
            case.update(state="PASS", execute_operation_id=execute_id, verify_operation_id=verify_id,
                        expected_volume_mm3=expected_volume, expected_bounds_mm=EXPECTED_BOUNDS_MM[name], output_count=output_count,
                        non_target_and_retained_unchanged=True, same_id_query_no_change=True)
            persist()

        case = set_case("F15_save_copy_reopen_and_safe_return")
        before = [helpers._fingerprint(x) for x in helpers._body_records(inspect())]
        copy = path.parent / ("F15-mcp-" + uuid.uuid4().hex + "-roundtrip.prt")
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
                             "reproduction": "Open a new F15 fixture and rerun the same installed smoke command; inspect recorded request and response.",
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
        expected = ["F15_schema_rejections"] + ["F15_" + op + "_" + str(retain).lower() for op in ("unite", "subtract", "intersect") for retain in (False, True)] + ["F15_chained_subtract", "F15_split", "F15_multi_intersect", "F15_disjoint_subtract", "F15_disjoint_intersect", "F15_save_copy_reopen_and_safe_return"]
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
