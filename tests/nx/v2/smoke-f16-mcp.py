"""F16 installed MCP double-edge acceptance; use only in coordinated NX window."""
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
oracle = module("f16_oracle", ROOT / "tools/v2/f16-geometry-oracle.py")
FIELDS = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id", "edge_ids", "radius_mm"}
TERMINAL = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}


def uid(label):
    return "f16-" + label + "-" + uuid.uuid4().hex


def schema_contract(tools):
    found = [x for x in tools if x.get("name") == "nx_prepare_edge_blend"]
    if len(found) != 1:
        raise AssertionError("edge blend tool must occur exactly once")
    s = found[0].get("inputSchema", {})
    edges = s.get("properties", {}).get("edge_ids", {})
    if (s.get("type") != "object" or s.get("additionalProperties") is not False or
            set(s.get("properties", {})) != FIELDS or set(s.get("required", [])) != FIELDS or
            edges.get("type") != "array" or edges.get("minItems") != 1 or edges.get("maxItems") != 32 or
            edges.get("uniqueItems") is not True or s["properties"]["radius_mm"].get("type") != "number"):
        raise AssertionError("edge blend schema differs from explicit closed contract")


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
    parser.add_argument("--expected-server-version", default="0.16.0")
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = {"test": "F16 installed MCP", "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "passed": False, "calls": [], "cases": [], "unknown_events": [], "current_case": None,
              "limitations": ["one double-edge fixture, not reliability or compliance evidence",
                              "roundtrip NX journal provides independent geometric and native readback evidence"],
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
        if allow_rejection and data.get("state") == "FAILED" and tool == "nx_prepare_edge_blend":
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
        if not path.is_relative_to(root) or not path.name.startswith("F16-mcp-fixture-") or path.suffix.lower() != ".prt":
            raise ValueError("a new F16 MCP fixture is required")
        metadata = json.loads((ROOT / "workspace/v2/evidence" / (path.stem + ".json")).read_text(encoding="utf-8"))
        if (Path(metadata["part_path"]).resolve() != path or metadata.get("saved") is not True or
                metadata.get("body_count") != 4 or metadata.get("bridge_called") is not False or
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
                                             "clientInfo": {"name": "f16-installed-smoke", "version": "1"}})
        client.notify("notifications/initialized")
        report["initialize"] = h._redact(init)
        if init.get("serverInfo", {}).get("version") != args.expected_server_version:
            raise AssertionError("installed server version mismatch")
        tools = client.request("tools/list").get("tools")
        if not isinstance(tools, list) or len(tools) != 35 or len({x.get("name") for x in tools}) != 35:
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
        if h._inspect_path(initial).resolve() != path or len(h._body_records(initial)) != 4:
            raise AssertionError("initial part or inventory mismatch")
        target = f15.fixture_body_by_bounds(initial, [40, 0, 0, 60, 12, 10])
        other = f15.fixture_body_by_bounds(initial, [80, 0, 0, 100, 12, 10])
        edges = [fixture_edge(target, [40, 0, 0], [40, 0, 10]), fixture_edge(target, [60, 12, 0], [60, 12, 10])]
        crossed = fixture_edge(target, [40, 0, 0], [60, 0, 0])
        wrong = fixture_edge(other, [80, 0, 0], [80, 0, 10])
        base = {"operation_id": uid("prepare"), **credentials, "revision_id": revision(),
                "target_body_id": target["journal_id"], "edge_ids": edges, "radius_mm": 2.0}
        row = case("I03_safe_domain_rejections")
        for label, changes in (("foreign_edge", {"edge_ids": [wrong]}),
                               ("intersecting_nonparallel_edges", {"edge_ids": [edges[0], crossed]}),
                               ("overlapping_radius", {"radius_mm": 7.0}),
                               ("stale_revision", {"revision_id": "f16-deliberately-stale"})):
            before, before_revision = h._part_signature(inspect()), revision()
            request = {**base, **changes, "operation_id": uid("reject")}
            op, result = operation("nx_prepare_edge_blend", request, allow_rejection=True)
            if op.get("state") != "FAILED" or h._part_signature(inspect()) != before or revision() != before_revision:
                raise AssertionError(label + " was not rejected without changing the part and revision")
            row.setdefault("rejections", []).append({"case": label, "state": op.get("state"), "result": h._redact(result)})
            persist()
        row["state"] = "PASS"
        row = case("I02_double_edge_execute_fresh")
        before, before_revision = inspect(), revision()
        base.update(operation_id=uid("prepare"), revision_id=before_revision)
        _, prepared = operation("nx_prepare_edge_blend", base)
        if h._part_signature(inspect()) != h._part_signature(before) or revision() != before_revision:
            raise AssertionError("prepare changed the part or revision")
        plan_id = prepared.get("plan", {}).get("plan_id")
        if not isinstance(plan_id, str) or not plan_id:
            raise AssertionError("prepare omitted plan ID")
        execute_id = uid("execute")
        executed_op, executed = operation("nx_execute_plan", {"operation_id": execute_id, **credentials, "plan_id": plan_id})
        if executed.get("mutation_state") != "APPLIED" or not isinstance(executed.get("feature_id"), str):
            raise AssertionError("execute did not provide applied native feature")
        after, after_revision = inspect(), revision()
        outputs = executed.get("body_ids")
        if not isinstance(outputs, list) or len(outputs) != 1:
            raise AssertionError("expected exactly one result body")
        if f15.other_signatures(before, {target["journal_id"]}) != f15.other_signatures(after, set(outputs)):
            raise AssertionError("non-target fixture body changed")
        body = f15.body_by_id(after, outputs[0])
        oracle.check_volume(body.get("volume_mm3"), 2)
        oracle.check_box(body.get("bbox_mm"), 40)
        queried, entry = call("nx_get_operation", {"operation_id": execute_id})
        if entry["is_error"] or queried.get("state") != "SUCCEEDED" or h._operation_result(queried) != h._operation_result(executed_op):
            unknown(execute_id, "explicit get did not return the known succeeded result")
        if h._part_signature(inspect()) != h._part_signature(after) or revision() != after_revision:
            raise AssertionError("same-ID query changed part or revision")
        _, verified = operation("nx_verify_part", {"operation_id": uid("verify"), **credentials, "execute_operation_id": execute_id})
        h._assert_verified(verified, "edge_blend")
        if verified.get("execute_operation_id") != execute_id or verified.get("plan_id") != plan_id:
            raise AssertionError("fresh verification provenance mismatch")
        definition = h._find_dict(verified, "native_definition")
        coverage = h._find_dict(verified, "coverage")
        if not isinstance(definition, dict) or not isinstance(coverage, dict):
            raise AssertionError("fresh omitted native definition or coverage")
        oracle.close(definition.get("radius_mm"), 2.0, oracle.LENGTH_TOL_MM, "native radius mm")
        native_tags, rule_tags = definition.get("edge_tags"), definition.get("rule_edge_tags")
        if (definition.get("section") != "Circular" or definition.get("radius_units") != "MilliMeter" or
                definition.get("rule_edges_match_collector") is not True or not isinstance(native_tags, list) or
                len(native_tags) != 2 or len(set(native_tags)) != 2 or not isinstance(rule_tags, list) or
                sorted(rule_tags) != sorted(native_tags) or definition.get("variable_radius_counts") != [[0, 0, 0, 0]] * 2):
            raise AssertionError("fresh NX native radius/edge rule readback mismatch")
        observed = coverage.get("edges")
        if (coverage.get("status") != "VERIFIED" or coverage.get("cylindrical_face_count") != 2 or
                coverage.get("planar_face_count") != 6 or not isinstance(observed, list) or len(observed) != 2 or
                {x.get("input_edge_id") for x in observed} != set(edges) or
                {x.get("input_edge_tag") for x in observed} != set(native_tags)):
            raise AssertionError("fresh coverage does not associate both selected edges with the native rule")
        for expected_id, corner in zip(edges, ("low", "high")):
            match = next(x for x in observed if x.get("input_edge_id") == expected_id)
            face = match.get("face")
            if (match.get("matched_faces") != 1 or not isinstance(face, dict) or
                    face.get("linear_edges") != 2 or face.get("circular_edges") != 2 or face.get("planar_adjacencies") != 4):
                raise AssertionError("selected edge lacks one bounded cylindrical face and four planar adjacencies")
            oracle.check_cylinder(face.get("radius_mm"), face.get("axis_point_mm"), face.get("axis_direction"), 40, corner)
        row.update(state="PASS", execute_operation_id=execute_id, expected_volume_mm3=oracle.expected_volume(2),
                   non_target_unchanged=True, same_id_query_unchanged=True)
        persist()
        first_feature_id = executed["feature_id"]
        row = case("I04_chained_single_edge_preserves_prior_blend")
        before, before_revision = inspect(), revision()
        single = f15.fixture_body_by_bounds(before, [0, 0, 0, 20, 12, 10])
        edge = fixture_edge(single, [0, 0, 0], [0, 0, 10])
        request = {"operation_id": uid("prepare-single"), **credentials, "revision_id": before_revision,
                   "target_body_id": single["journal_id"], "edge_ids": [edge], "radius_mm": 2.0}
        _, prepared = operation("nx_prepare_edge_blend", request)
        if h._part_signature(inspect()) != h._part_signature(before) or revision() != before_revision:
            raise AssertionError("chained prepare changed the part or revision")
        plan_id = prepared.get("plan", {}).get("plan_id")
        if not isinstance(plan_id, str) or not plan_id:
            raise AssertionError("chained prepare omitted plan ID")
        execute_id = uid("execute-single")
        _, executed = operation("nx_execute_plan", {"operation_id": execute_id, **credentials, "plan_id": plan_id})
        if (executed.get("mutation_state") != "APPLIED" or not isinstance(executed.get("feature_id"), str) or
                executed["feature_id"] == first_feature_id):
            raise AssertionError("chained execute did not provide a distinct applied native feature")
        after = inspect()
        outputs = executed.get("body_ids")
        if not isinstance(outputs, list) or len(outputs) != 1:
            raise AssertionError("chained result must have exactly one body")
        if f15.other_signatures(before, {single["journal_id"]}) != f15.other_signatures(after, set(outputs)):
            raise AssertionError("prior blended body or another non-target changed")
        result_body = f15.body_by_id(after, outputs[0])
        oracle.check_volume(result_body.get("volume_mm3"), 1)
        oracle.check_box(result_body.get("bbox_mm"), 0)
        _, verified = operation("nx_verify_part", {"operation_id": uid("verify-single"), **credentials, "execute_operation_id": execute_id})
        h._assert_verified(verified, "chained_single_edge")
        if verified.get("execute_operation_id") != execute_id or verified.get("plan_id") != plan_id:
            raise AssertionError("chained fresh verification provenance mismatch")
        definition = h._find_dict(verified, "native_definition")
        coverage = h._find_dict(verified, "coverage")
        if not isinstance(definition, dict) or not isinstance(coverage, dict):
            raise AssertionError("chained fresh omitted native definition or coverage")
        oracle.close(definition.get("radius_mm"), 2, oracle.LENGTH_TOL_MM, "chained native radius mm")
        selected = coverage.get("edges")
        if (definition.get("section") != "Circular" or definition.get("radius_units") != "MilliMeter" or
                definition.get("rule_edges_match_collector") is not True or not isinstance(selected, list) or
                len(selected) != 1 or selected[0].get("input_edge_id") != edge or selected[0].get("matched_faces") != 1 or
                definition.get("edge_tags") != [selected[0].get("input_edge_tag")] or
                definition.get("rule_edge_tags") != definition.get("edge_tags") or
                definition.get("variable_radius_counts") != [[0, 0, 0, 0]]):
            raise AssertionError("chained native rule does not match the selected single edge")
        face = selected[0].get("face")
        if not isinstance(face, dict) or face.get("linear_edges") != 2 or face.get("circular_edges") != 2 or face.get("planar_adjacencies") != 4:
            raise AssertionError("chained single edge has incomplete result face")
        oracle.check_cylinder(face.get("radius_mm"), face.get("axis_point_mm"), face.get("axis_direction"), 0, "low")
        row.update(state="PASS", execute_operation_id=execute_id, prior_feature_id=first_feature_id,
                   chained_feature_id=executed["feature_id"], prior_blended_body_unchanged=True,
                   expected_volume_mm3=oracle.expected_volume(1))
        persist()
        row = case("I06_save_copy_reopen_and_return")
        before = [h._fingerprint(x) for x in h._body_records(inspect())]
        copy = path.parent / ("F16-mcp-" + uuid.uuid4().hex + "-roundtrip.prt")
        if copy.exists():
            raise FileExistsError("SaveCopy destination exists")
        _, saved = operation("nx_save_copy", {"operation_id": uid("save"), **credentials, "target_path": str(copy), "reopen": True})
        if not isinstance(saved.get("new_ownership_id"), str) or not isinstance(saved.get("new_binding_epoch"), int):
            raise AssertionError("SaveCopy omitted rebound ownership")
        credentials.update(ownership_id=saved["new_ownership_id"], binding_epoch=saved["new_binding_epoch"])
        checks = saved.get("checks", {})
        if saved.get("saved") is not True or saved.get("reopened") is not True or set(checks) != {"associations", "expression", "geometry", "history"} or any(not isinstance(x, dict) or x.get("status") != "VERIFIED" for x in checks.values()):
            raise AssertionError("SaveCopy/reopen checks incomplete")
        after = inspect()
        if h._inspect_path(after).resolve() != copy.resolve():
            raise AssertionError("wrong reopened part")
        h._assert_roundtrip(before, [h._fingerprint(x) for x in h._body_records(after)])
        returned = sync("nx_return", credentials)
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("return did not confirm safe human ownership")
        credentials.clear()
        status = sync("nx_bridge_status")
        if status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False or str(status.get("in_flight")) != "0":
            raise AssertionError("final bridge not idle HUMAN_OWNED")
        row.update(state="PASS", saved_path=str(copy), final_status=h._redact(status))
        report["passed"] = True
        return 0
    except Exception as exc:
        report["failure"] = {"type": type(exc).__name__, "message": str(exc) if isinstance(exc, (AssertionError, ValueError, FileNotFoundError)) else "see recorded response or transport type", "case_id": report["current_case"],
                             "reproduction": "Open a new F16 fixture and rerun the recorded command and requests.",
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
        report["not_executed"] = [x for x in ("I03_safe_domain_rejections", "I02_double_edge_execute_fresh", "I04_chained_single_edge_preserves_prior_blend", "I06_save_copy_reopen_and_return") if x not in completed]
        if client:
            try:
                client.close()
            except Exception as exc:
                report["client_close_error_type"] = type(exc).__name__
        persist()
        print(json.dumps({"passed": report["passed"], "evidence": str(args.output), "case_id": report["current_case"]}))


if __name__ == "__main__":
    raise SystemExit(main())
