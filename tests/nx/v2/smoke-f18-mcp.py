"""Installed F18 MCP smoke for a new F18 fixture; coordinated NX window only.

Requires the already-open F18 fixture and an idle HUMAN_OWNED bridge. Does not
launch NX. Every request and response is saved to an exclusive new report;
oracle inputs are also written as exclusive new files. Any unanticipated
failure stops all subsequent operations; UNKNOWN also forbids query and replay.
The sole same-ID execute replay occurs only after confirmed SUCCEEDED.
"""
from __future__ import annotations
import argparse
import datetime
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/v2"))
from f01_f02_mcp_client import McpStdioClient


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


h = module("f13_helpers", Path(__file__).with_name("smoke-f13-mcp.py"))
f15 = module("f15_helpers", Path(__file__).with_name("smoke-f15-mcp.py"))
oracle = module("f18_oracle", ROOT / "tools/v2/f18-geometry-oracle.py")
FIELDS = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "seed_feature_id", "mode", "count1",
          "count2", "direction1", "direction2", "pitch1_mm", "pitch2_mm", "axis_origin_mm", "axis_direction", "angle_deg"}
COMMON = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "seed_feature_id", "mode", "count1"}
TERMINAL = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
CASES = (
    ("linear_negative_x", "linear", {"mode": "linear", "count1": 3, "direction1": [-1, 0, 0], "pitch1_mm": 20}),
    ("rectangular_unequal", "rectangular", {"mode": "rectangular", "count1": 2, "count2": 3,
     "direction1": [-1, 0, 0], "direction2": [0, 1, 0], "pitch1_mm": 20, "pitch2_mm": 18}),
    ("circular_signed_nonorigin", "circular", {"mode": "circular", "count1": 3,
     "axis_origin_mm": [300, 0, 0], "axis_direction": [0, 0, 1], "angle_deg": -90}),
)


def uid(label):
    return "f18-" + label + "-" + uuid.uuid4().hex


def schema_contract(tools):
    found = [entry for entry in tools if entry.get("name") == "nx_prepare_feature_pattern"]
    if len(found) != 1:
        raise AssertionError("feature pattern tool must occur exactly once")
    schema = found[0].get("inputSchema", {})
    if (schema.get("additionalProperties") is not False or set(schema.get("properties", {})) != FIELDS or
            set(schema.get("required", [])) != COMMON or len(schema.get("oneOf", [])) != 3 or
            schema["properties"]["mode"].get("enum") != ["linear", "rectangular", "circular"]):
        raise AssertionError("installed pattern schema differs from frozen field/mode contract")
    for count in ("count1", "count2"):
        item = schema["properties"][count]
        if item.get("type") != "integer" or item.get("minimum") != 2 or item.get("maximum") != 32:
            raise AssertionError("installed count schema mismatch")
    mode_fields = {
        "linear": {"direction1", "pitch1_mm"},
        "rectangular": {"direction1", "pitch1_mm", "direction2", "pitch2_mm", "count2"},
        "circular": {"axis_origin_mm", "axis_direction", "angle_deg"},
    }
    optional = set.union(*mode_fields.values())
    branches = {}
    for branch in schema["oneOf"]:
        mode = branch.get("properties", {}).get("mode", {}).get("const")
        if mode in branches or mode not in mode_fields:
            raise AssertionError("installed mode schema is ambiguous")
        prohibited = branch.get("not", {}).get("anyOf", [])
        if any(not isinstance(item, dict) or len(item.get("required", [])) != 1 for item in prohibited):
            raise AssertionError("installed mode exclusion schema malformed")
        forbidden = {item["required"][0] for item in prohibited}
        if set(branch.get("required", [])) != mode_fields[mode] or forbidden != optional - mode_fields[mode]:
            raise AssertionError("installed per-mode required/excluded fields mismatch")
        branches[mode] = branch
    for field in ("direction1", "direction2", "axis_origin_mm", "axis_direction"):
        vector = schema["properties"][field]
        if (vector.get("type") != "array" or vector.get("minItems") != 3 or vector.get("maxItems") != 3 or
                vector.get("items", {}).get("type") != "number"):
            raise AssertionError("installed vector schema mismatch: " + field)
    for field in ("pitch1_mm", "pitch2_mm"):
        if schema["properties"][field].get("exclusiveMinimum") != 0:
            raise AssertionError("installed pitch schema mismatch: " + field)


def seed_feature(inspection, bounds):
    body = f15.fixture_body_by_bounds(inspection, bounds)
    body_id = body.get("journal_id")
    features = inspection.get("features")
    if not isinstance(features, list):
        raise AssertionError("inspection omitted features")
    found = [f for f in features if isinstance(f, dict) and f.get("feature_type") == "EXTRUDE"
             and f.get("body_ids") == [body_id] and f.get("suppressed") is False]
    if len(found) != 1 or not isinstance(found[0].get("journal_id"), str) or not found[0]["journal_id"]:
        raise AssertionError("fixture body must map uniquely to a current unsuppressed EXTRUDE")
    return found[0]["journal_id"], body


def pattern_evidence(execution):
    # Some bridge envelopes nest the feature evidence under result; other revisions flatten it.
    evidence = execution.get("result") if isinstance(execution.get("result"), dict) else execution
    if (evidence.get("kind") != "create_feature_pattern" or not isinstance(evidence.get("feature_id"), str)
            or not evidence["feature_id"] or not isinstance(evidence.get("native_definition"), dict)
            or not isinstance(evidence.get("geometry"), dict) or not isinstance(evidence.get("model_preservation"), dict)):
        raise AssertionError("execute omitted F18 feature definition, geometry or preservation")
    if evidence["geometry"].get("status") != "VERIFIED" or evidence["model_preservation"].get("status") != "VERIFIED":
        raise AssertionError("execute pattern geometry or preservation was not VERIFIED")
    return evidence


def assert_native_definition(definition, seed_id, feature_id, pattern):
    required = {"feature_id": feature_id, "seed_feature_id": seed_id, "method": "Simple",
                "output_option": "PatternFeature", "expression_option": "CreateNew",
                "seed_only": False, "frame_only": False, "stagger": "None"}
    if pattern["mode"] == "circular":
        required.update(pattern_type="Circular", orientation="FollowPattern", include_seed=True,
                        flip=False, use_radial=False, space_type="Offset", pitch_option="Angle",
                        count=pattern["count1"], angle=pattern["angle_deg"],
                        axis_origin=pattern["axis_origin_mm"], axis_direction=pattern["axis_direction"])
    else:
        required.update(pattern_type="Linear", orientation="Fixed", use_y=pattern["mode"] == "rectangular",
                        x_flip=False, x_symmetry=False, x_space_type="Offset", x_count=pattern["count1"],
                        x_pitch=pattern["pitch1_mm"], x_direction=pattern["direction1"])
        if pattern["mode"] == "rectangular":
            required.update(y_flip=False, y_symmetry=False, y_space_type="Offset", y_count=pattern["count2"],
                            y_pitch=pattern["pitch2_mm"], y_direction=pattern["direction2"])
    for key, expected in required.items():
        actual = definition.get(key)
        if isinstance(expected, list):
            if not isinstance(actual, list) or len(actual) != 3 or any(abs(float(a) - b) > 1e-8 for a, b in zip(actual, expected)):
                raise AssertionError("native definition vector mismatch: " + key)
        elif actual != expected:
            raise AssertionError("native definition mismatch: " + key)


def assert_seed_bounds(geometry, bounds):
    points = geometry.get("seed", {}).get("vertices_mm")
    if not isinstance(points, list) or len(points) != 8:
        raise AssertionError("seed vertices are missing")
    expected = {(bounds[i], bounds[1 + j], bounds[2 + k])
                for i in (0, 3) for j in (0, 3) for k in (0, 3)}
    # Independently compare the raw seed corners to the fixture's known bounds.
    matched = set()
    for point in points:
        candidates = [corner for corner in expected if isinstance(point, list) and len(point) == 3
                      and all(abs(float(a) - float(b)) <= 1e-6 for a, b in zip(point, corner))]
        if len(candidates) != 1 or candidates[0] in matched:
            raise AssertionError("seed geometry differs from fixture corners")
        matched.add(candidates[0])


def oracle_input(pattern, geometry):
    if geometry.get("count_includes_seed") is not True:
        raise AssertionError("geometry must state seed-inclusive count")
    seed = geometry.get("seed")
    instances = geometry.get("instances")
    shapes = geometry.get("oracle_bodies_in_expected_order")
    count = pattern["count1"] * (pattern["count2"] if pattern["mode"] == "rectangular" else 1)
    if (not isinstance(seed, dict) or not isinstance(geometry.get("seed_native_geometry"), dict) or
            not isinstance(instances, list) or len(instances) != count - 1 or
            not isinstance(shapes, list) or len(shapes) != count or shapes[0] != seed):
        raise AssertionError("geometry did not provide complete seed and instance evidence")
    body_ids, instance_ids, feature_ids = set(), set(), set()
    for n, row in enumerate(instances, 1):
        ny = pattern["count2"] if pattern["mode"] == "rectangular" else 1
        if (not isinstance(row, dict) or row.get("expected_index") != n or row.get("index1") != n // ny or
                row.get("index2") != n % ny or row.get("shape") != shapes[n] or
                not isinstance(row.get("native_geometry"), dict)):
            raise AssertionError("instance geometric evidence or indexes do not agree")
        for key, used in (("body_id", body_ids), ("instance_id", instance_ids), ("output_feature_id", feature_ids)):
            value = row.get(key)
            if not isinstance(value, str) or not value or value in used:
                raise AssertionError("instance identifiers are missing or duplicated: " + key)
            used.add(value)
    return {"seed": seed, "pattern": pattern, "instances": shapes, "tolerance_mm": 1e-6}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--part-path", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-server-version", default="0.18.0")
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = {"test": "F18 installed MCP", "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "passed": False, "calls": [], "cases": [], "unknown_events": [], "current_case": None,
              "limitations": ["one sample per mode, not reliability or standards evidence",
                  "unlabelled box symmetry requires independent oriented native-definition evidence",
                  "reopened bodies are identified geometrically, not by prior session Tags"],
              "configuration": {"expected_server_version": args.expected_server_version, "units": "mm and degrees"}}
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream)
    client, credentials, frozen, active_id = None, {}, False, None

    def persist():
        with args.output.open("w", encoding="utf-8", newline="\n") as stream:
            json.dump(h._redact(report), stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")

    def call(tool, arguments):
        if frozen:
            raise RuntimeError("UNKNOWN gate: no calls or replay")
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
        row.update(response=h._redact(response), raw_protocol_response=h._redact(client.last_response),
                   is_error=response.get("isError") is True,
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
            data, entry = call(tool, arguments)
        except Exception:
            unknown(op_id, "initial transport failure")
        if entry["is_error"] or data.get("operation_id") != op_id:
            unknown(op_id, "initial async error or mismatched ID")
        deadline = time.monotonic() + 180
        while data.get("state") not in TERMINAL:
            if time.monotonic() >= deadline:
                unknown(op_id, "poll deadline")
            time.sleep(0.2)
            try:
                data, entry = call("nx_get_operation", {"operation_id": op_id})
            except Exception:
                unknown(op_id, "query transport failure")
            if entry["is_error"] or data.get("operation_id") != op_id:
                unknown(op_id, "query error or mismatched ID")
        result = h._operation_result(data)
        if (data.get("state") == "UNKNOWN" or isinstance(result, dict) and
                (result.get("mutation_state") == "UNKNOWN" or result.get("verification_state") == "UNKNOWN")):
            unknown(op_id, "operation, mutation or verification outcome UNKNOWN")
        if allow_rejection and tool == "nx_prepare_feature_pattern" and data.get("state") == "FAILED":
            return data, result or {}
        if data.get("state") != "SUCCEEDED" or not isinstance(result, dict):
            raise AssertionError(tool + " did not succeed")
        return data, result

    def revision():
        rev = sync("nx_get_session", credentials).get("revision_id")
        if not isinstance(rev, str) or not rev:
            raise AssertionError("missing current revision")
        return rev

    def case(name):
        report["current_case"] = name
        row = {"case_id": name, "state": "RUNNING"}
        report["cases"].append(row)
        persist()
        return row

    def write_oracle(name, stage, pattern, geometry, bounds):
        assert_seed_bounds(geometry, bounds)
        document = oracle_input(pattern, geometry)
        path = args.output.parent / (args.output.stem + "-" + name + "-" + stage + "-oracle.json")
        with path.open("x", encoding="utf-8", newline="\n") as stream:
            json.dump(document, stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")
        report.setdefault("oracle_files", []).append({"case_id": name, "stage": stage, "path": str(path)})
        persist()
        result = oracle.verify(document)
        if result.get("ok") is not True:
            raise AssertionError("independent geometry oracle rejected: " + str(result))
        return {"input": str(path), "result": result}

    try:
        path = args.part_path.resolve(strict=True)
        root = (ROOT / "workspace/v2/models").resolve(strict=True)
        if not path.is_relative_to(root) or not path.name.startswith("F18-mcp-fixture-") or path.suffix.lower() != ".prt":
            raise ValueError("a new F18 MCP fixture is required")
        metadata = json.loads((ROOT / "workspace/v2/evidence" / (path.stem + ".json")).read_text(encoding="utf-8"))
        if (Path(metadata["part_path"]).resolve() != path or metadata.get("stage") != "complete" or
                metadata.get("saved") is not True or metadata.get("reopened") is not True or
                metadata.get("body_count") != 4 or metadata.get("bridge_called") is not False or
                metadata.get("mcp_called") is not False or metadata.get("part_modified_after_save") is not False or
                metadata.get("reopened_modified") is not False or metadata.get("save_unsaved_parts") != 0 or
                metadata.get("save_unsaved_objects") != 0):
            raise AssertionError("fixture metadata does not prove a clean isolated saved/reopened part")
        fixture = {row["case"]: row for row in metadata["cases"]}
        if len(metadata["cases"]) != 4 or set(fixture) != {"linear", "rectangular", "circular", "sentinel"}:
            raise AssertionError("fixture cases missing or duplicated")
        expected_bounds = {"linear": [100, 0, 0, 104, 6, 8], "rectangular": [200, 100, 0, 205, 107, 9],
                           "circular": [320, 0, 0, 324, 6, 10], "sentinel": [-200, -200, 0, -197, -196, 5]}
        if any(fixture[key].get("bbox_mm") != bounds for key, bounds in expected_bounds.items()):
            raise AssertionError("fixture bounds differ from planned, separated cases")
        report["fixture"] = metadata
        install = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy()
        env.update(PYTHONPATH=str(install / "app/src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen([sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install / "mcp-config.json")],
                                   cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
        client = McpStdioClient(process, 60, max_line_bytes=16*1024*1024)
        init = client.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                             "clientInfo": {"name": "f18-installed-smoke", "version": "1"}})
        client.notify("notifications/initialized")
        report["initialize"] = h._redact(init); persist()
        if init.get("serverInfo", {}).get("version") != args.expected_server_version:
            raise AssertionError("installed server version mismatch")
        tools = client.request("tools/list").get("tools")
        if not isinstance(tools, list) or len(tools) != 37 or len({x.get("name") for x in tools}) != 37:
            raise AssertionError("installed tool count or uniqueness mismatch")
        schema_contract(tools)
        report["tools_list"] = h._redact(tools); persist()
        status = sync("nx_bridge_status")
        if status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False or str(status.get("in_flight")) != "0":
            raise AssertionError("bridge must be idle and HUMAN_OWNED")
        takeover = sync("nx_takeover", {"expected_pid": status["pid"], "expected_part_path": str(path),
                                       "artifact_scope": "saved_independent_copy", "authorize": True})
        if not isinstance(takeover.get("ownership_id"), str) or not isinstance(takeover.get("binding_epoch"), int):
            raise AssertionError("takeover omitted credentials")
        credentials.update(ownership_id=takeover["ownership_id"], binding_epoch=takeover["binding_epoch"])
        capability = sync("nx_get_capabilities")
        if (capability.get("model_feature_pattern_adapter_available") is not True or
                capability.get("model_feature_pattern_write_enabled") is not True or
                "operation.prepare_feature_pattern" not in capability.get("available_operations", [])):
            raise AssertionError("live feature pattern adapter is unavailable or disabled")
        inspect = lambda: sync("nx_inspect_part", credentials)
        initial = inspect()
        if h._inspect_path(initial).resolve() != path or len(h._body_records(initial)) != 4:
            raise AssertionError("initial part or body inventory mismatch")
        for key in ("linear", "rectangular", "circular"):
            seed_feature(initial, fixture[key]["bbox_mm"])
        f15.fixture_body_by_bounds(initial, fixture["sentinel"]["bbox_mm"])

        row = case("safe_rejections")
        seed, _ = seed_feature(initial, fixture["linear"]["bbox_mm"])
        for label, changes in (("overlap", {"pitch1_mm": 2}), ("stale_revision", {"revision_id": "deliberately-stale"})):
            before, rev = h._part_signature(inspect()), revision()
            request = {"operation_id": uid("reject"), **credentials, "revision_id": rev,
                       "seed_feature_id": seed, **CASES[0][2], **changes}
            op, result = operation("nx_prepare_feature_pattern", request, allow_rejection=True)
            expected_code = {"overlap": "UNSUPPORTED_PATTERN_OVERLAP", "stale_revision": "STALE_REVISION"}[label]
            if (op.get("state") != "FAILED" or h._error_code(op) != expected_code or
                    h._part_signature(inspect()) != before or revision() != rev):
                raise AssertionError(label + " did not safely reject")
            row.setdefault("rejections", []).append({"label": label, "operation": h._redact(op), "result": h._redact(result)})
            persist()
        for label, changes in (("count_above_32", {"count1": 33}), ("cross_mode_field", {"axis_direction": [0, 0, 1]})):
            before, rev = h._part_signature(inspect()), revision()
            request = {"operation_id": uid("invalid"), **credentials, "revision_id": rev,
                       "seed_feature_id": seed, **CASES[0][2], **changes}
            data, entry = call("nx_prepare_feature_pattern", request)
            response = entry.get("protocol_response", {})
            if not entry["is_error"] or response.get("error", {}).get("code") != -32602:
                raise AssertionError(label + " was not rejected by installed Python schema validation")
            if h._part_signature(inspect()) != before or revision() != rev:
                raise AssertionError(label + " changed model")
            row.setdefault("rejections", []).append({"label": label, "protocol_response": response})
            persist()
        row["state"] = "PASS"; persist()
        feature_ids = []
        for index, (name, fixture_name, pattern) in enumerate(CASES):
            row = case(name); before = inspect(); rev = revision()
            seed, seed_body = seed_feature(before, fixture[fixture_name]["bbox_mm"])
            request = {"operation_id": uid("prepare"), **credentials, "revision_id": rev,
                       "seed_feature_id": seed, **pattern}
            _, prepared = operation("nx_prepare_feature_pattern", request)
            if h._part_signature(inspect()) != h._part_signature(before) or revision() != rev:
                raise AssertionError("prepare changed the model or revision")
            plan_id = prepared.get("plan", {}).get("plan_id")
            if not isinstance(plan_id, str) or not plan_id:
                raise AssertionError("prepare omitted plan id")
            execute_request = {"operation_id": uid("execute"), **credentials, "plan_id": plan_id}
            executed_op, executed = operation("nx_execute_plan", execute_request)
            if executed.get("mutation_state") != "APPLIED" or executed.get("plan_id") != plan_id:
                raise AssertionError("execute did not apply the plan")
            evidence = pattern_evidence(executed)
            if evidence["feature_id"] in feature_ids:
                raise AssertionError("pattern feature ID is not unique")
            geometry = evidence["geometry"]
            if geometry.get("seed_feature_id") != seed:
                raise AssertionError("execution geometry resolved a different seed")
            assert_native_definition(evidence["native_definition"], seed, evidence["feature_id"], pattern)
            executed_oracle = write_oracle(name, "execute", pattern, geometry, fixture[fixture_name]["bbox_mm"])
            after = inspect(); after_revision = revision()
            expected_bodies = len(h._body_records(before)) + len(geometry["instances"])
            if len(h._body_records(after)) != expected_bodies:
                raise AssertionError("wrong body count after pattern")
            queried = sync("nx_get_operation", {"operation_id": execute_request["operation_id"]})
            if queried.get("state") == "UNKNOWN":
                unknown(execute_request["operation_id"], "explicit get returned UNKNOWN")
            if queried.get("state") != "SUCCEEDED" or h._operation_result(queried) != h._operation_result(executed_op):
                raise AssertionError("execute query differs from terminal result")
            if index == 0:
                replay_op, replay = operation("nx_execute_plan", execute_request)
                if (replay_op.get("state") != "SUCCEEDED" or replay != executed or
                        h._part_signature(inspect()) != h._part_signature(after) or revision() != after_revision):
                    raise AssertionError("same successful operation ID was not idempotent")
                row["same_id_success_replay_unchanged"] = True
            _, verified = operation("nx_verify_part", {"operation_id": uid("verify"), **credentials,
                                                        "execute_operation_id": execute_request["operation_id"]})
            h._assert_verified(verified, name)
            required_checks = {"pattern_definition", "pattern_instances", "model_preservation", "body_effect",
                               "geometry", "bodies", "history", "support_associations"}
            if not required_checks.issubset(verified.get("checks", {})):
                raise AssertionError("fresh verification omitted required pattern checks")
            fresh = verified.get("evidence")
            if (not isinstance(fresh, dict) or fresh.get("kind") != "create_feature_pattern" or
                    fresh.get("feature_id") != evidence["feature_id"] or
                    fresh.get("native_definition") != evidence["native_definition"] or
                    not isinstance(fresh.get("geometry"), dict) or fresh["geometry"].get("status") != "VERIFIED"):
                raise AssertionError("fresh evidence does not match pattern definition")
            assert_native_definition(fresh["native_definition"], seed, evidence["feature_id"], pattern)
            fresh_oracle = write_oracle(name, "fresh", pattern, fresh["geometry"], fixture[fixture_name]["bbox_mm"])
            row.update(state="PASS", seed_feature_id=seed, seed_body_id=seed_body["journal_id"],
                       previous_pattern_ids=list(feature_ids), execute_operation_id=execute_request["operation_id"],
                       feature_id=evidence["feature_id"], execution=evidence, verified=verified,
                       execute_oracle=executed_oracle, fresh_oracle=fresh_oracle)
            feature_ids.append(evidence["feature_id"]); persist()

        row = case("save_copy_reopen_return")
        before = [h._fingerprint(item) for item in h._body_records(inspect())]
        copy = path.parent / ("F18-mcp-" + uuid.uuid4().hex + "-roundtrip.prt")
        if copy.exists():
            raise FileExistsError("SaveCopy destination exists")
        _, saved = operation("nx_save_copy", {"operation_id": uid("save"), **credentials,
                                               "target_path": str(copy), "reopen": True})
        if not isinstance(saved.get("new_ownership_id"), str) or not isinstance(saved.get("new_binding_epoch"), int):
            raise AssertionError("SaveCopy omitted rebound ownership")
        credentials.update(ownership_id=saved["new_ownership_id"], binding_epoch=saved["new_binding_epoch"])
        checks = saved.get("checks", {})
        if (saved.get("saved") is not True or saved.get("reopened") is not True or
                set(checks) != {"associations", "expression", "geometry", "history"} or
                any(not isinstance(v, dict) or v.get("status") != "VERIFIED" for v in checks.values())):
            raise AssertionError("SaveCopy checks incomplete")
        after = inspect()
        if h._inspect_path(after).resolve() != copy.resolve():
            raise AssertionError("incorrect reopened copy")
        h._assert_roundtrip(before, [h._fingerprint(item) for item in h._body_records(after)])
        for key in ("linear", "rectangular", "circular"):
            seed_feature(after, fixture[key]["bbox_mm"])
        f15.fixture_body_by_bounds(after, fixture["sentinel"]["bbox_mm"])
        returned = sync("nx_return", credentials)
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("safe return failed")
        credentials.clear(); status = sync("nx_bridge_status")
        if status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False or str(status.get("in_flight")) != "0":
            raise AssertionError("bridge not idle")
        row.update(state="PASS", saved_path=str(copy), prior_session_pattern_ids=feature_ids, final_status=h._redact(status))
        report["passed"] = True
        return 0
    except Exception as exc:
        report["failure"] = {"type": type(exc).__name__,
                             "message": str(exc) if isinstance(exc, (AssertionError, ValueError, FileNotFoundError)) else "see recorded response or transport type",
                             "case_id": report["current_case"], "active_operation_id": active_id,
                             "action": "stop; preserve evidence and obtain human review; no automatic return or further model writes"}
        if report["cases"] and report["cases"][-1]["state"] == "RUNNING":
            report["cases"][-1]["state"] = "FAIL"
        persist()
        return 1
    finally:
        report["completed_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        done = {row["case_id"] for row in report["cases"]}
        report["not_executed"] = [name for name in (["safe_rejections"] + [c[0] for c in CASES] + ["save_copy_reopen_return"]) if name not in done]
        if client:
            try:
                client.close()
            except Exception as exc:
                report["client_close_error_type"] = type(exc).__name__
        persist()
        print(json.dumps({"passed": report["passed"], "evidence": str(args.output), "case_id": report["current_case"]}))


if __name__ == "__main__":
    raise SystemExit(main())
