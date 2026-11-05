"""Installed F19 MCP smoke; use only an already-open, isolated F19 fixture.

This launches the installed stdio server, not NX. Run only in a coordinated NX
window. Every protocol response is recorded with credentials redacted; any
unknown async outcome stops all subsequent calls, including return and replay.
Independent measured-area/volume oracle evidence may require the separate
save/reopen capture; a protocol pass does not claim that evidence exists.
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
sys.path.insert(0, str(ROOT / "tools/v2"))
from f01_f02_mcp_client import McpStdioClient


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


f18 = module("f18_smoke_helpers", Path(__file__).with_name("smoke-f18-mcp.py"))
h, f15 = f18.h, f18.f15
oracle = module("f19_oracle", ROOT / "tools/v2/f19-geometry-oracle.py")
sharing_audit = module("f19_native_sharing_audit", ROOT / "tools/v2/f19-audit-native.py")
COMMON = {"operation_id", "ownership_id", "binding_epoch", "revision_id", "seed_feature_id"}
FIELDS = COMMON | {"plane_origin_mm", "plane_normal", "datum_plane_id"}
TERMINAL = {"SUCCEEDED", "FAILED", "CANCELLED", "UNKNOWN"}
BOUNDS = {"explicit": [100, 0, 0, 104, 6, 8], "datum": [200, 100, 0, 205, 107, 9],
          "pattern": [320, 0, 0, 324, 6, 10], "sentinel": [-200, -200, 0, -197, -196, 5]}
K = 2 ** -0.5
PLANES = {"explicit": ([180, 0, 0], [2/7, -3/7, 6/7]), "datum": ([220, 120, 0], [K, K, 0])}
PATTERN = {"mode": "linear", "count1": 2, "direction1": [1, 0, 0], "pitch1_mm": 20}


def uid(label):
    return "f19-" + label + "-" + uuid.uuid4().hex


def contains_unknown(value):
    if isinstance(value, dict):
        for key, item in value.items():
            if key in {"state", "mutation_state", "verification_state"} and item == "UNKNOWN":
                return True
            if key in {"code", "error_code"} and isinstance(item, str) and item in {"M3_ANALYSIS_STATE_UNKNOWN", "VERIFY_OUTCOME_UNKNOWN"}:
                return True
            if contains_unknown(item):
                return True
    elif isinstance(value, list):
        return any(contains_unknown(item) for item in value)
    return False


def schema_contract(tools):
    matches = [x for x in tools if x.get("name") == "nx_prepare_feature_mirror"]
    if len(matches) != 1:
        raise AssertionError("mirror tool must occur exactly once")
    s = matches[0].get("inputSchema", {})
    if (s.get("type") != "object" or s.get("additionalProperties") is not False or
            set(s.get("properties", {})) != FIELDS or set(s.get("required", [])) != COMMON):
        raise AssertionError("installed mirror schema differs from field contract")
    branches = s.get("oneOf")
    if not isinstance(branches, list) or len(branches) != 2:
        raise AssertionError("mirror schema must declare exactly two exclusive branches")
    explicit = [b for b in branches if set(b.get("required", [])) == {"plane_origin_mm", "plane_normal"}]
    datum = [b for b in branches if set(b.get("required", [])) == {"datum_plane_id"}]
    if (len(explicit) != 1 or len(datum) != 1 or
            explicit[0].get("not", {}).get("required") != ["datum_plane_id"] or
            {tuple(x.get("required", [])) for x in datum[0].get("not", {}).get("anyOf", [])} !=
            {("plane_origin_mm",), ("plane_normal",)}):
        raise AssertionError("mirror schema does not exclude mixed plane modes")
    for key in ("plane_origin_mm", "plane_normal"):
        v = s["properties"][key]
        if (v.get("type") != "array" or v.get("minItems") != 3 or v.get("maxItems") != 3 or
                v.get("items", {}).get("type") != "number"):
            raise AssertionError("mirror vector schema mismatch: " + key)


def datum_id(inspection):
    rows = inspection.get("features")
    if not isinstance(rows, list):
        raise AssertionError("inspection omitted features")
    found = [f for f in rows if isinstance(f, dict) and f.get("feature_type") == "ABSOLUTE_DATUM_PLANE"
             and f.get("suppressed") is False and isinstance(f.get("journal_id"), str)
             and f["journal_id"].startswith("FIXED_DATUM_PLANE(")]
    if len(found) != 1:
        raise AssertionError("fixture must have exactly one current unsuppressed fixed datum feature")
    return found[0]["journal_id"]


def feature_record(inspection, feature_id):
    found = [f for f in inspection.get("features", []) if f.get("journal_id") == feature_id]
    if len(found) != 1:
        raise AssertionError("existing feature missing or duplicated: " + feature_id)
    return found[0]


def mirror_evidence(execution):
    evidence = execution.get("result") if isinstance(execution.get("result"), dict) else execution
    if (evidence.get("kind") != "create_feature_mirror" or
            not isinstance(evidence.get("feature_id"), str) or not evidence["feature_id"] or
            not isinstance(evidence.get("native_definition"), dict) or
            not isinstance(evidence.get("geometry"), dict) or
            not isinstance(evidence.get("model_preservation"), dict)):
        raise AssertionError("mirror execution evidence is missing or contract changed")
    if evidence["geometry"].get("status") != "VERIFIED" or evidence["model_preservation"].get("status") != "VERIFIED":
        raise AssertionError("mirror geometry or preservation was not VERIFIED")
    return evidence


def assert_mirror_definition(definition, feature_id, seed, name, datum):
    expected = {"feature_id": feature_id, "seed_feature_id": seed,
                "plane_mode": "numeric" if name == "explicit" else "datum"}
    if name == "datum":
        expected["datum_plane_id"] = datum
    for key, value in expected.items():
        if definition.get(key) != value:
            raise AssertionError("native mirror definition mismatch: " + key)
    for key, value in zip(("plane_origin_mm", "plane_normal"), PLANES[name]):
        actual = definition.get(key)
        if (not isinstance(actual, list) or len(actual) != 3 or
                any(not isinstance(x, (int, float)) or not math.isfinite(x) or abs(x-y) > 1e-8
                    for x, y in zip(actual, value))):
            raise AssertionError("native mirror plane mismatch: " + key)
    for key in ("body_id", "body_tag", "output_feature_id", "output_feature_tag"):
        if not isinstance(definition.get(key), str) or not definition[key]:
            raise AssertionError("native mirror definition omitted " + key)


def oracle_if_measured(geometry, plane, report, output, name, stage, persist):
    seed, instances = geometry.get("seed"), geometry.get("instances")
    if not isinstance(seed, dict) or not isinstance(instances, list) or len(instances) != 1:
        raise AssertionError("mirror geometry lacks one seed and one reflected instance")
    instance = instances[0]
    if not isinstance(instance, dict) or not isinstance(instance.get("shape"), dict):
        raise AssertionError("mirror instance shape is missing")
    native = instance.get("native_geometry")
    if not isinstance(native, dict) or not isinstance(native.get("faces"), list) or len(native["faces"]) != 6:
        return {"status": "PENDING", "reason": "native face measurements unavailable; capture after reopen"}
    areas = [f.get("area_mm2") if isinstance(f, dict) else None for f in native["faces"]]
    volume = native.get("volume_mm3")
    if any(isinstance(x, bool) or not isinstance(x, (int, float)) or not math.isfinite(x) or x <= 0
           for x in areas + [volume]):
        return {"status": "PENDING", "reason": "native measured face area or volume unavailable; capture after reopen"}
    if geometry.get("status") != "VERIFIED":
        raise AssertionError("native straight-edge/planar-face coverage was not verified")
    actual = dict(instance["shape"])
    actual.update(face_areas_mm2=areas, volume_mm3=volume)
    document = {"seed": seed, "actual": actual, "plane_origin_mm": plane[0], "plane_normal": plane[1],
                "tolerance_mm": 1e-6, "tolerance_mm2": 1e-4, "tolerance_mm3": 1e-4}
    path = output.parent / (output.stem + "-" + name + "-" + stage + "-oracle.json")
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(document, stream, ensure_ascii=False, allow_nan=False, indent=2)
        stream.write("\n")
    report.setdefault("oracle_files", []).append(str(path)); persist()
    result = oracle.check(document)
    if result.get("ok") is not True:
        raise AssertionError("independent mirror oracle rejected: " + str(result))
    return {"status": "PASS", "input": str(path), "result": result}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--part-path", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-server-version", required=True)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    report = {"test": "F19 installed MCP", "passed": False, "calls": [], "cases": [], "unknown_events": [],
              "current_case": None, "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
              "configuration": {"expected_server_version": args.expected_server_version, "units": "mm"},
              "limitations": ["one fixture and one example per mirror mode; no reliability or compliance claim",
                              "credentials redacted from protocol evidence",
                              "independent oracle may require measured post-reopen capture",
                              "mirror response field contract remains subject to implementation review"]}
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream)
    client, credentials, frozen, active_id = None, {}, False, None

    def persist():
        with args.output.open("w", encoding="utf-8", newline="\n") as stream:
            json.dump(h._redact(report), stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")

    def request(method, params=None):
        if frozen:
            raise RuntimeError("UNKNOWN gate: all MCP calls prohibited")
        row = {"method": method, "params": h._redact(params), "case_id": report["current_case"]}
        report["calls"].append(row)
        try:
            response = client.request(method, params)
            row.update(response=h._redact(response), raw_protocol_response=h._redact(client.last_response))
        except Exception as exc:
            row.update(raw_protocol_response=h._redact(client.last_response), exception_type=type(exc).__name__)
            persist()
            raise
        persist()
        return response

    def call(tool, arguments):
        try:
            result = request("tools/call", {"name": tool, "arguments": arguments})
        except Exception:
            if client.last_response is None:
                raise
            return {}, {"is_error": True, "protocol_response": client.last_response}
        return h._decode_payload(result), {"is_error": result.get("isError") is True}

    def sync(tool, arguments=None):
        data, row = call(tool, arguments or {})
        if row["is_error"]:
            raise AssertionError(tool + " returned an MCP error")
        return data

    def unknown(op_id, reason):
        nonlocal frozen, active_id
        frozen, active_id = True, op_id
        report["unknown_events"].append({"operation_id": op_id, "reason": reason,
                                         "action": "stop; no query, replay, return, or thaw"})
        persist()
        raise RuntimeError("UNKNOWN outcome; stopped")

    def operation(tool, arguments):
        op_id = arguments["operation_id"]
        try:
            data, row = call(tool, arguments)
        except Exception:
            unknown(op_id, "initial transport failure")
        if row["is_error"] or data.get("operation_id") != op_id:
            unknown(op_id, "initial async error or mismatched operation ID")
        if contains_unknown(data):
            unknown(op_id, "initial response reports unknown outcome")
        deadline = time.monotonic() + 180
        while data.get("state") not in TERMINAL:
            if time.monotonic() >= deadline:
                unknown(op_id, "poll deadline")
            time.sleep(0.2)
            try:
                data, row = call("nx_get_operation", {"operation_id": op_id})
            except Exception:
                unknown(op_id, "query transport failure")
            if row["is_error"] or data.get("operation_id") != op_id:
                unknown(op_id, "query error or mismatched operation ID")
            if contains_unknown(data):
                unknown(op_id, "query reports unknown outcome")
        result = h._operation_result(data)
        if (data.get("state") == "UNKNOWN" or isinstance(result, dict) and
                (result.get("mutation_state") == "UNKNOWN" or result.get("verification_state") == "UNKNOWN")):
            unknown(op_id, "operation, mutation or verification UNKNOWN")
        if data.get("state") != "SUCCEEDED" or not isinstance(result, dict):
            raise AssertionError(tool + " did not succeed")
        return data, result

    def revision():
        value = sync("nx_get_session", credentials).get("revision_id")
        if not isinstance(value, str) or not value:
            raise AssertionError("missing current revision")
        return value

    def case(name):
        report["current_case"] = name
        row = {"case_id": name, "state": "RUNNING"}
        report["cases"].append(row); persist()
        return row

    def verify(execute_id, kind, feature_id, check_names):
        _, verified = operation("nx_verify_part", {"operation_id": uid("verify"), **credentials,
                                                   "execute_operation_id": execute_id})
        h._assert_verified(verified, kind)
        checks = verified.get("checks", {})
        if not check_names.issubset(checks) or any(checks[x].get("status") != "VERIFIED" for x in check_names):
            raise AssertionError("fresh verification omitted or failed required checks")
        evidence = verified.get("evidence")
        if not isinstance(evidence, dict) or evidence.get("kind") != kind or evidence.get("feature_id") != feature_id:
            raise AssertionError("fresh evidence identifies a different feature")
        return verified, evidence

    try:
        path = args.part_path.resolve(strict=True)
        root = (ROOT / "workspace/v2/models").resolve(strict=True)
        if not path.is_relative_to(root) or not path.name.startswith("F19-mcp-fixture-") or path.suffix.lower() != ".prt":
            raise ValueError("a new isolated F19 fixture is required")
        metadata = json.loads((ROOT / "workspace/v2/evidence" / (path.stem + ".json")).read_text(encoding="utf-8"))
        if (Path(metadata["part_path"]).resolve() != path or metadata.get("stage") != "complete" or
                metadata.get("saved") is not True or metadata.get("reopened") is not True or
                metadata.get("body_count") != 4 or metadata.get("bridge_called") is not False or
                metadata.get("mcp_called") is not False or metadata.get("part_modified_after_save") is not False or
                metadata.get("reopened_modified") is not False or metadata.get("save_unsaved_parts") != 0 or
                metadata.get("save_unsaved_objects") != 0):
            raise AssertionError("fixture metadata does not prove a clean saved and reopened part")
        fixture = {r["case"]: r for r in metadata["cases"]}
        if len(metadata["cases"]) != 4 or set(fixture) != set(BOUNDS) or any(fixture[k].get("bbox_mm") != v for k, v in BOUNDS.items()):
            raise AssertionError("fixture case bounds mismatch")
        if metadata.get("datum_origin_mm") != PLANES["datum"][0] or metadata.get("datum_normal") != PLANES["datum"][1]:
            raise AssertionError("fixture datum metadata differs from planned plane")
        report["fixture"] = metadata; persist()
        install = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy()
        env.update(PYTHONPATH=str(install / "app/src"), PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen([sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install / "mcp-config.json")],
                                   cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
        client = McpStdioClient(process, 60, max_line_bytes=16 * 1024 * 1024)
        init = request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                      "clientInfo": {"name": "f19-installed-smoke", "version": "1"}})
        client.notify("notifications/initialized")
        if init.get("serverInfo", {}).get("version") != args.expected_server_version:
            raise AssertionError("installed server version mismatch")
        tools = request("tools/list").get("tools")
        if not isinstance(tools, list) or len(tools) != 38 or len({x.get("name") for x in tools}) != 38:
            raise AssertionError("installed tool count or uniqueness mismatch")
        schema_contract(tools)
        status = sync("nx_bridge_status")
        if status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False or str(status.get("in_flight")) != "0":
            raise AssertionError("bridge must be idle and HUMAN_OWNED")
        takeover = sync("nx_takeover", {"expected_pid": status["pid"], "expected_part_path": str(path),
                                       "artifact_scope": "saved_independent_copy", "authorize": True})
        if not isinstance(takeover.get("ownership_id"), str) or not isinstance(takeover.get("binding_epoch"), int):
            raise AssertionError("takeover omitted credentials")
        credentials.update(ownership_id=takeover["ownership_id"], binding_epoch=takeover["binding_epoch"])
        capability = sync("nx_get_capabilities")
        for name in ("feature_mirror", "feature_pattern"):
            if (capability.get("model_" + name + "_adapter_available") is not True or
                    capability.get("model_" + name + "_write_enabled") is not True or
                    "operation.prepare_" + name not in capability.get("available_operations", [])):
                raise AssertionError(name + " adapter unavailable or disabled")
        inspect = lambda: sync("nx_inspect_part", credentials)
        initial = inspect()
        if h._inspect_path(initial).resolve() != path or len(h._body_records(initial)) != 4:
            raise AssertionError("initial part or body count mismatch")
        for key in ("explicit", "datum", "pattern"):
            f18.seed_feature(initial, BOUNDS[key])
        f15.fixture_body_by_bounds(initial, BOUNDS["sentinel"])
        datum = datum_id(initial)

        row = case("mode_exclusion")
        seed, _ = f18.seed_feature(initial, BOUNDS["explicit"])
        origin, normal = PLANES["explicit"]
        for label, fields in (("both_modes", {"datum_plane_id": datum, "plane_origin_mm": origin, "plane_normal": normal}),
                              ("datum_plus_origin", {"datum_plane_id": datum, "plane_origin_mm": origin}),
                              ("datum_plus_normal", {"datum_plane_id": datum, "plane_normal": normal}),
                              ("origin_only", {"plane_origin_mm": origin}), ("normal_only", {"plane_normal": normal}),
                              ("neither", {})):
            before, rev = h._part_signature(inspect()), revision()
            data, entry = call("nx_prepare_feature_mirror", {"operation_id": uid("invalid"), **credentials,
                "revision_id": rev, "seed_feature_id": seed, **fields})
            if not entry["is_error"] or entry.get("protocol_response", {}).get("error", {}).get("code") != -32602:
                raise AssertionError(label + " did not fail installed protocol argument validation")
            if h._part_signature(inspect()) != before or revision() != rev:
                raise AssertionError(label + " changed model or revision")
            row.setdefault("rejected", []).append(label); persist()
        row["state"] = "PASS"; persist()

        row = case("existing_pattern_setup")
        before, rev = inspect(), revision()
        seed, _ = f18.seed_feature(before, BOUNDS["pattern"])
        _, prepared = operation("nx_prepare_feature_pattern", {"operation_id": uid("pattern-prepare"), **credentials,
            "revision_id": rev, "seed_feature_id": seed, **PATTERN})
        if h._part_signature(inspect()) != h._part_signature(before) or revision() != rev:
            raise AssertionError("pattern prepare modified model")
        plan_id = prepared.get("plan", {}).get("plan_id")
        if not isinstance(plan_id, str) or not plan_id:
            raise AssertionError("pattern prepare omitted plan")
        pattern_exec = {"operation_id": uid("pattern-execute"), **credentials, "plan_id": plan_id}
        _, executed = operation("nx_execute_plan", pattern_exec)
        if executed.get("mutation_state") != "APPLIED" or executed.get("plan_id") != plan_id:
            raise AssertionError("pattern setup not applied")
        pattern = f18.pattern_evidence(executed)
        f18.assert_native_definition(pattern["native_definition"], seed, pattern["feature_id"], PATTERN)
        verified, fresh = verify(pattern_exec["operation_id"], "create_feature_pattern", pattern["feature_id"],
            {"pattern_definition", "pattern_instances", "model_preservation", "body_effect", "geometry", "bodies", "history", "support_associations"})
        if fresh.get("native_definition") != pattern["native_definition"]:
            raise AssertionError("fresh pattern definition differs")
        pattern_record = feature_record(inspect(), pattern["feature_id"])
        row.update(state="PASS", feature_id=pattern["feature_id"], verified=verified); persist()

        mirror_ids, first_record, old_pattern_definition, first_mirror_definition = [], None, None, None
        for name in ("explicit", "datum"):
            row = case(name + "_mirror")
            before, rev = inspect(), revision()
            seed, _ = f18.seed_feature(before, BOUNDS[name])
            fields = ({"plane_origin_mm": PLANES[name][0], "plane_normal": PLANES[name][1]} if name == "explicit"
                      else {"datum_plane_id": datum})
            _, prepared = operation("nx_prepare_feature_mirror", {"operation_id": uid("mirror-prepare"), **credentials,
                "revision_id": rev, "seed_feature_id": seed, **fields})
            if h._part_signature(inspect()) != h._part_signature(before) or revision() != rev:
                raise AssertionError("mirror prepare changed model or revision")
            plan_id = prepared.get("plan", {}).get("plan_id")
            if not isinstance(plan_id, str) or not plan_id:
                raise AssertionError("mirror prepare omitted plan")
            execute_request = {"operation_id": uid("mirror-execute"), **credentials, "plan_id": plan_id}
            terminal, executed = operation("nx_execute_plan", execute_request)
            if executed.get("mutation_state") != "APPLIED" or executed.get("plan_id") != plan_id:
                raise AssertionError("mirror execute did not apply plan")
            evidence = mirror_evidence(executed)
            assert_mirror_definition(evidence["native_definition"], evidence["feature_id"], seed, name, datum)
            f18.assert_seed_bounds(evidence["geometry"], BOUNDS[name])
            if evidence["geometry"].get("seed_feature_id") != seed:
                raise AssertionError("mirror geometry refers to a different seed")
            preservation = evidence["model_preservation"]
            row["expression_sharing_audit"] = sharing_audit.audit_expression_sharing(evidence)
            patterns = preservation.get("existing_pattern_definitions")
            mirrors = preservation.get("existing_mirror_definitions")
            if (preservation.get("prior_native_definitions") is not True or not isinstance(patterns, dict) or
                    pattern["feature_id"] not in patterns or not isinstance(mirrors, dict)):
                raise AssertionError("preservation did not demonstrate existing native Pattern definitions")
            if name == "explicit":
                old_pattern_definition = patterns[pattern["feature_id"]]
                f18.assert_native_definition(old_pattern_definition, pattern["native_definition"]["seed_feature_id"],
                                             pattern["feature_id"], PATTERN)
            else:
                if patterns[pattern["feature_id"]] != old_pattern_definition:
                    raise AssertionError("existing Pattern native definition changed")
                if mirrors.get(mirror_ids[0]) != first_mirror_definition:
                    raise AssertionError("existing Mirror native definition changed or is missing")
            if evidence["feature_id"] in mirror_ids or evidence["feature_id"] == pattern["feature_id"]:
                raise AssertionError("new feature ID is not unique")
            after, after_revision = inspect(), revision()
            if len(h._body_records(after)) != len(h._body_records(before)) + 1:
                raise AssertionError("mirror must add one fixture solid")
            if feature_record(after, pattern["feature_id"]) != pattern_record:
                raise AssertionError("pre-existing pattern feature inventory changed")
            if first_record is not None and feature_record(after, mirror_ids[0]) != first_record:
                raise AssertionError("pre-existing mirror feature inventory changed")
            queried = sync("nx_get_operation", {"operation_id":execute_request["operation_id"]})
            if contains_unknown(queried):
                unknown(execute_request["operation_id"], "explicit operation query returned UNKNOWN")
            if queried.get("state") != "SUCCEEDED" or h._operation_result(queried) != h._operation_result(terminal):
                raise AssertionError("successful execute query differs from terminal result")
            row["terminal_query_matches"] = True
            if name == "explicit":
                replay, repeated = operation("nx_execute_plan", execute_request)
                if (replay.get("state") != "SUCCEEDED" or h._operation_result(replay) != h._operation_result(terminal) or
                        repeated != executed or h._part_signature(inspect()) != h._part_signature(after) or revision() != after_revision):
                    raise AssertionError("same successful operation ID replay was not idempotent")
                row["same_id_success_replay_unchanged"] = True
            checked, fresh = verify(execute_request["operation_id"], "create_feature_mirror", evidence["feature_id"],
                {"mirror_definition", "mirror_geometry", "model_preservation", "body_effect", "geometry", "bodies", "history", "support_associations"})
            if (fresh.get("native_definition") != evidence["native_definition"] or
                    not isinstance(fresh.get("geometry"), dict) or fresh["geometry"].get("status") != "VERIFIED" or
                    fresh["geometry"].get("seed_feature_id") != seed):
                raise AssertionError("fresh mirror evidence differs from execution")
            row.update(feature_id=evidence["feature_id"], previous_pattern_id=pattern["feature_id"],
                       previous_mirror_ids=list(mirror_ids), execution=evidence, verified=checked,
                       execute_oracle=oracle_if_measured(evidence["geometry"], PLANES[name], report, args.output, name, "execute", persist),
                       fresh_oracle=oracle_if_measured(fresh["geometry"], PLANES[name], report, args.output, name, "fresh", persist))
            mirror_ids.append(evidence["feature_id"])
            if name == "explicit":
                first_record = feature_record(after, mirror_ids[0])
                first_mirror_definition = dict(evidence["native_definition"])
                first_mirror_definition.pop("body_tag", None)
                first_mirror_definition.pop("output_feature_tag", None)
            row["state"] = "PASS"; persist()

        row = case("save_copy_reopen_return")
        before = [h._fingerprint(x) for x in h._body_records(inspect())]
        copy = path.parent / ("F19-mcp-" + uuid.uuid4().hex + "-roundtrip.prt")
        if copy.exists():
            raise FileExistsError("SaveCopy destination exists")
        _, saved = operation("nx_save_copy", {"operation_id": uid("save"), **credentials, "target_path": str(copy), "reopen": True})
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
        h._assert_roundtrip(before, [h._fingerprint(x) for x in h._body_records(after)])
        for key in ("explicit", "datum", "pattern"):
            f18.seed_feature(after, BOUNDS[key])
        f15.fixture_body_by_bounds(after, BOUNDS["sentinel"])
        datum_id(after)
        # A separate coordinated read-only NX audit consumes this exact SaveCopy.
        # Values are predetermined from the fixture and plane, not copied from
        # the implementation's geometry prediction.
        manifest_path = args.output.parent / (args.output.stem + "-roundtrip-manifest.json")
        manifest = {"model_path": str(copy), "cases": [
            {"name":"explicit_mirror", "plane_mode":"explicit", "seed_bbox_mm":BOUNDS["explicit"],
             "mirror_bbox_mm":[764/7,-960/49,1640/49,5776/49,-438/49,2136/49], "plane_origin_mm":PLANES["explicit"][0],
             "plane_normal":PLANES["explicit"][1], "tolerance_mm":1e-6, "tolerance_mm2":1e-4, "tolerance_mm3":1e-4},
            {"name":"datum_mirror", "plane_mode":"datum", "seed_bbox_mm":BOUNDS["datum"],
             "mirror_bbox_mm":[233,135,0,240,140,9], "plane_origin_mm":PLANES["datum"][0],
             "plane_normal":PLANES["datum"][1], "tolerance_mm":1e-6, "tolerance_mm2":1e-4, "tolerance_mm3":1e-4}]}
        for item in manifest["cases"]:
            source = next(r for r in report["cases"] if r["case_id"] == item["name"])
            changes = source["execution"]["model_preservation"]["expression_sharing"]["changes"]
            item["shared_expressions"] = [{k:change["after"][k] for k in ("name","type","rhs","value","units")}
                                          for change in changes]
        manifest["old_pattern"] = {"seed_bbox_mm":BOUNDS["pattern"],
            "instance_bbox_mm":[340,0,0,344,6,10], "direction1":[1,0,0],
            "pitch1_mm":20, "count1":2, "tolerance_mm":1e-6,
            "reference_point_mm":pattern["native_definition"]["reference_point_mm"],
            "reference_inferred":pattern["native_definition"]["reference_inferred"]}
        with manifest_path.open("x", encoding="utf-8") as stream:
            json.dump(manifest, stream, indent=2, allow_nan=False)
        row["roundtrip_manifest"] = str(manifest_path); persist()
        returned = sync("nx_return", credentials)
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("safe return failed")
        credentials.clear()
        status = sync("nx_bridge_status")
        if status.get("ownership_state") != "HUMAN_OWNED" or status.get("writes_halted") is not False or str(status.get("in_flight")) != "0":
            raise AssertionError("bridge not idle after return")
        row.update(state="PASS", saved_path=str(copy), final_status=h._redact(status)); persist()
        report["independent_geometry_complete"] = all(r.get(stage, {}).get("status") == "PASS"
            for r in report["cases"] if r["case_id"] in ("explicit_mirror", "datum_mirror")
            for stage in ("execute_oracle", "fresh_oracle"))
        report["passed"] = True
        return 0
    except Exception as exc:
        report["failure"] = {"type": type(exc).__name__,
                             "message": str(exc) if isinstance(exc, (AssertionError, ValueError, FileNotFoundError)) else "see recorded protocol response or exception type",
                             "case_id": report["current_case"],
                             "active_operation_id": active_id, "action": "stop; preserve evidence; no automatic return or further writes"}
        if report["cases"] and report["cases"][-1]["state"] == "RUNNING":
            report["cases"][-1]["state"] = "FAIL"
        persist()
        return 1
    finally:
        report["completed_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        done = {r["case_id"] for r in report["cases"]}
        report["not_executed"] = [x for x in ("mode_exclusion", "existing_pattern_setup", "explicit_mirror", "datum_mirror", "save_copy_reopen_return") if x not in done]
        if client:
            try:
                client.close()
            except Exception as exc:
                report["client_close_error_type"] = type(exc).__name__
        persist()
        print(json.dumps({"passed": report["passed"], "evidence": str(args.output), "case_id": report["current_case"]}))


if __name__ == "__main__":
    raise SystemExit(main())
