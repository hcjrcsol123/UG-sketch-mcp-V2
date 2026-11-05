"""Exercise the installed F05 two-line datum-plane MCP flow.

Run ``--ready --output <new.json>`` for an installed-host readiness probe.
For modeling, open an isolated saved part under ``workspace/v2/models`` in the
dedicated NX GUI first, then run ``--part-path <part.prt> --output <new.json>``.
The host uses the normal installed configuration and this client never reads
or emits its private credentials.
"""
from __future__ import annotations

import argparse
import datetime
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
from f01_f02_mcp_client import McpStdioClient, _wait_operation

PROTOCOL_VERSION = "2025-11-25"
EXPECTED_SERVER_VERSION = "0.5.0"
EXPECTED_TOOL_COUNT = 21
PLANE_POSITION_TOLERANCE_MM = 1e-6
UNIT_VECTOR_TOLERANCE = 1e-7
PARALLEL_DIRECTION_CROSS_TOLERANCE = 1e-10
MINIMUM_LINE_LENGTH_MM = 1e-6


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--ready", action="store_true", help="start the installed STDIO host and ensure NX readiness")
    mode.add_argument("--part-path", type=Path, help="isolated saved part under workspace/v2/models")
    parser.add_argument("--output", required=True, type=Path, help="new JSON evidence path")
    return parser


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = _build_parser()
    args = parser.parse_args(argv)
    if args.output.exists():
        parser.error("evidence output must be a new path")
    return args


def _vector(value: object, label: str) -> list[float]:
    if not isinstance(value, list) or len(value) != 3:
        raise AssertionError(f"{label} must be a three-component vector")
    result = [float(component) for component in value]
    if not all(math.isfinite(component) for component in result):
        raise AssertionError(f"{label} contains a non-finite component")
    return result


def _subtract(left: list[float], right: list[float]) -> list[float]:
    return [left[index] - right[index] for index in range(3)]


def _dot(left: list[float], right: list[float]) -> float:
    return sum(left[index] * right[index] for index in range(3))


def _cross(left: list[float], right: list[float]) -> list[float]:
    return [
        left[1] * right[2] - left[2] * right[1],
        left[2] * right[0] - left[0] * right[2],
        left[0] * right[1] - left[1] * right[0],
    ]


def _length(value: list[float]) -> float:
    return math.sqrt(_dot(value, value))


def _unit(value: list[float], label: str) -> list[float]:
    magnitude = _length(value)
    if not math.isfinite(magnitude) or magnitude <= 0.0:
        raise AssertionError(f"{label} has zero or invalid length")
    return [component / magnitude for component in value]


def _distance(left: list[float], right: list[float]) -> float:
    return _length(_subtract(left, right))


def _endpoint_residuals(points: list[list[float]], origin: list[float], normal: list[float]) -> list[float]:
    return [_dot(_subtract(point, origin), normal) for point in points]


def _independent_plane(line1: dict, line2: dict) -> dict:
    p0 = _vector(line1["start"], "line1 start")
    p1 = _vector(line1["end"], "line1 end")
    q0 = _vector(line2["start"], "line2 start")
    q1 = _vector(line2["end"], "line2 end")
    d1 = _subtract(p1, p0)
    d2 = _subtract(q1, q0)
    length1 = _length(d1)
    length2 = _length(d2)
    if length1 <= MINIMUM_LINE_LENGTH_MM or length2 <= MINIMUM_LINE_LENGTH_MM:
        raise AssertionError("independently inspected line is at or below the 1e-6 mm minimum length")
    u1 = _unit(d1, "line1 direction")
    u2 = _unit(d2, "line2 direction")
    direction_cross = _cross(u1, u2)
    cross_norm = _length(direction_cross)
    if cross_norm <= PARALLEL_DIRECTION_CROSS_TOLERANCE:
        between_starts = _subtract(q0, p0)
        along = _dot(between_starts, u1)
        separation = [between_starts[i] - along * u1[i] for i in range(3)]
        separation_mm = _length(separation)
        if separation_mm <= PLANE_POSITION_TOLERANCE_MM:
            raise AssertionError("independently inspected parallel lines do not define a unique plane")
        normal = _unit(_cross(u1, separation), "parallel-line plane normal")
        classification = "parallel"
    else:
        normal = [component / cross_norm for component in direction_cross]
        separation_mm = None
        classification = "intersecting_or_skew"
    points = [p0, p1, q0, q1]
    residuals = _endpoint_residuals(points, p0, normal)
    return {
        "classification": classification,
        "line1_length_mm": length1,
        "line2_length_mm": length2,
        "direction_cross_norm": cross_norm,
        "parallel_separation_mm": separation_mm,
        "origin_mm": p0,
        "normal": normal,
        "endpoint_residuals_mm": residuals,
    }


def _contains_value(value: object, expected: object) -> bool:
    if value == expected:
        return True
    if isinstance(value, dict):
        return any(_contains_value(child, expected) for child in value.values())
    if isinstance(value, list):
        return any(_contains_value(child, expected) for child in value)
    return False


def _assert_verified(result: dict, label: str) -> dict:
    if result.get("verified") is not True:
        raise AssertionError(f"{label} did not return verified=true")
    checks = result.get("checks")
    if not isinstance(checks, dict) or not checks:
        raise AssertionError(f"{label} omitted production verification checks")
    bad = {name: check for name, check in checks.items()
           if not isinstance(check, dict) or check.get("status") != "VERIFIED"}
    if bad:
        raise AssertionError(f"{label} contains non-VERIFIED production checks: {bad}")
    return result


def _current_line(inspection: dict, expected_sketch_id: str) -> dict:
    sketch = inspection.get("sketch")
    if not isinstance(sketch, dict):
        raise AssertionError("nx_inspect_sketch omitted sketch record")
    if sketch.get("journal_id") != expected_sketch_id:
        raise AssertionError("nx_inspect_sketch returned a different sketch journal_id")
    geometry = sketch.get("geometry")
    if not isinstance(geometry, list):
        raise AssertionError("nx_inspect_sketch omitted geometry list")
    lines = [item for item in geometry if isinstance(item, dict) and item.get("kind") == "LINE"]
    if len(lines) != 1 or len(geometry) != 1:
        raise AssertionError("fixture sketch must contain exactly one inspected LINE")
    line = lines[0]
    line_id = line.get("journal_id")
    feature_id = sketch.get("feature_journal_id")
    if not isinstance(line_id, str) or not line_id or not isinstance(feature_id, str) or not feature_id:
        raise AssertionError("inspected line or source Sketch feature omitted its journal ID")
    _vector(line.get("start"), "inspected line start")
    _vector(line.get("end"), "inspected line end")
    return {"sketch_id": expected_sketch_id, "source_feature_id": feature_id, "line": line}


def _same_inspected_line(actual: dict, expected: dict) -> bool:
    actual_start = _vector(actual.get("start"), "reopened line start")
    actual_end = _vector(actual.get("end"), "reopened line end")
    expected_start = _vector(expected.get("start"), "baseline line start")
    expected_end = _vector(expected.get("end"), "baseline line end")
    forward = max(_distance(actual_start, expected_start), _distance(actual_end, expected_end))
    reverse = max(_distance(actual_start, expected_end), _distance(actual_end, expected_start))
    return min(forward, reverse) <= PLANE_POSITION_TOLERANCE_MM


def _assert_datum_definition(definition: dict, requested_ids: list[str],
                             source_feature_ids: list[str], line_records: dict[str, dict]) -> dict:
    if not isinstance(definition, dict):
        raise AssertionError("nx_verify_part omitted datum_plane definition readback")
    if len(requested_ids) != 2 or requested_ids[0] == requested_ids[1]:
        raise AssertionError("F05 request must reference two distinct inspected native lines")
    exact = {
        "mode": "two_lines",
        "native_method": "TwoLines",
        "native_alternate": "Three",
        "parent_reference_matches": True,
        "all_line_endpoints_on_native_plane": True,
        "normal_matches_plane_unoriented": True,
        "native_reference_geometry": True,
    }
    for key, expected in exact.items():
        if definition.get(key) != expected:
            raise AssertionError(f"datum_plane.{key} expected {expected!r}, got {definition.get(key)!r}")
    if definition.get("line1_id") != requested_ids[0] or definition.get("line2_id") != requested_ids[1]:
        raise AssertionError("datum_plane definition line IDs differ from the request ordering")
    native_geometry_ids = definition.get("native_geometry_line_ids")
    if not isinstance(native_geometry_ids, list) or len(native_geometry_ids) != 2 or set(native_geometry_ids) != set(requested_ids):
        raise AssertionError("native Plane.GetGeometry readback does not contain exactly the two requested lines")
    parent_ids = definition.get("native_parent_ids")
    if not isinstance(parent_ids, list):
        raise AssertionError("datum_plane omitted native_parent_ids")

    source_fields = ("line1_source_feature_ids", "line2_source_feature_ids")
    for position, field in enumerate(source_fields):
        expected_sources = [source_feature_ids[position]]
        actual_sources = definition.get(field)
        if not isinstance(actual_sources, list) or actual_sources != expected_sources:
            raise AssertionError(f"datum_plane.{field} differs from inspected sketch ancestry")
        if expected_sources[0] not in parent_ids:
            raise AssertionError(f"native parent IDs omit source sketch feature {expected_sources[0]}")

    first = line_records[requested_ids[0]]["line"]
    second = line_records[requested_ids[1]]["line"]
    oracle = _independent_plane(first, second)
    if max(abs(value) for value in oracle["endpoint_residuals_mm"]) > PLANE_POSITION_TOLERANCE_MM:
        raise AssertionError("independent endpoint residual exceeds 1e-6 mm")
    actual_normal = _unit(_vector(definition.get("normal"), "datum_plane normal"), "datum_plane normal")
    positive_error = _distance(actual_normal, oracle["normal"])
    negative_normal = [-component for component in oracle["normal"]]
    negative_error = _distance(actual_normal, negative_normal)
    normal_error = min(positive_error, negative_error)
    aligned_expected_normal = oracle["normal"] if positive_error <= negative_error else negative_normal
    normal_max_component_error = max(
        abs(actual_normal[index] - aligned_expected_normal[index]) for index in range(3)
    )
    if normal_error > UNIT_VECTOR_TOLERANCE:
        raise AssertionError("native and independently calculated unoriented normals differ by more than 1e-7")
    if normal_max_component_error > UNIT_VECTOR_TOLERANCE:
        raise AssertionError("native and independently calculated unoriented normal component differs by more than 1e-7")
    actual_origin = _vector(definition.get("origin"), "datum_plane origin")
    origin_signed_distance = _dot(_subtract(actual_origin, oracle["origin_mm"]), oracle["normal"])
    if abs(origin_signed_distance) > PLANE_POSITION_TOLERANCE_MM:
        raise AssertionError("returned DatumPlane origin is more than 1e-6 mm from the independently calculated plane")
    native_residuals = _endpoint_residuals(
        [
            _vector(first["start"], "line1 start"), _vector(first["end"], "line1 end"),
            _vector(second["start"], "line2 start"), _vector(second["end"], "line2 end"),
        ],
        actual_origin, actual_normal,
    )
    if max(abs(value) for value in native_residuals) > PLANE_POSITION_TOLERANCE_MM:
        raise AssertionError("inspect-derived endpoints exceed 1e-6 mm on the returned native DatumPlane")
    for field in ("line1_endpoint_plane_residuals_mm", "line2_endpoint_plane_residuals_mm"):
        values = definition.get(field)
        if not isinstance(values, list) or len(values) != 2 or max(abs(float(value)) for value in values) > PLANE_POSITION_TOLERANCE_MM:
            raise AssertionError(f"datum_plane.{field} exceeds 1e-6 mm or is malformed")

    for index, line_id in enumerate(requested_ids, start=1):
        record = line_records[line_id]["line"]
        reported_start = _vector(definition.get(f"line{index}_start_mm"), f"datum_plane line{index} start")
        reported_end = _vector(definition.get(f"line{index}_end_mm"), f"datum_plane line{index} end")
        if _distance(reported_start, _vector(record["start"], "inspected line start")) > PLANE_POSITION_TOLERANCE_MM:
            raise AssertionError(f"datum_plane line{index} start differs from fresh sketch inspection")
        if _distance(reported_end, _vector(record["end"], "inspected line end")) > PLANE_POSITION_TOLERANCE_MM:
            raise AssertionError(f"datum_plane line{index} end differs from fresh sketch inspection")

    return {
        "independent_oracle": oracle,
        "returned_native_origin_mm": actual_origin,
        "returned_native_normal": actual_normal,
        "origin_signed_distance_from_independent_plane_mm": origin_signed_distance,
        "unoriented_unit_normal_error": normal_error,
        "unoriented_unit_normal_max_component_error": normal_max_component_error,
        "native_plane_endpoint_residuals_mm_from_inspected_geometry": native_residuals,
        "tolerances": {
            "endpoint_residual_mm": PLANE_POSITION_TOLERANCE_MM,
            "unoriented_unit_normal_component": UNIT_VECTOR_TOLERANCE,
        },
    }


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv)
    report = {
        "test": "F05 installed MCP two-line datum-plane smoke",
        "test_version": "0.1.0",
        "started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "passed": False,
        "mode": "ready" if args.ready else "modeling",
        "objective": "Verify installed F05 two-line datum creation, line-order invariance, F01 support, and save/reopen associations through the installed STDIO MCP host.",
        "configuration": {
            "test_layer": "installed MCP integration and NX model subsystem validation",
            "traceability": "docs/F05.md; docs/V2.md F05",
            "units": "mm",
            "sample_count": 1,
            "nx_started_by_harness": False,
            "physical_measurement": False,
            "calibration": "not applicable; NX digital model-value evidence",
            "reliability_claim": False,
        },
        "calls": [],
        "steps": [],
        "cases": [],
        "tolerances": {
            "position_mm": PLANE_POSITION_TOLERANCE_MM,
            "unit_vector_component": UNIT_VECTOR_TOLERANCE,
            "parallel_direction_cross_norm": PARALLEL_DIRECTION_CROSS_TOLERANCE,
            "minimum_line_length_mm": MINIMUM_LINE_LENGTH_MM,
            "physical_measurement_uncertainty": "not applicable; NX digital model-value comparison only",
        },
    }
    credentials: dict[str, object] = {}
    process: subprocess.Popen[bytes] | None = None
    client: McpStdioClient | None = None
    active_case: dict | None = None

    def persist() -> None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, allow_nan=False, indent=2) + "\n", encoding="utf-8")

    def call(name: str, arguments: dict) -> dict:
        if client is None:
            raise RuntimeError("installed MCP client is not initialized")
        entry = {"tool": name, "arguments": arguments}
        report["calls"].append(entry)
        try:
            response = client.request("tools/call", {"name": name, "arguments": arguments})
        except Exception as exc:
            entry["transport_error"] = repr(exc)
            persist()
            raise
        entry["mcp_response"] = response
        structured = response.get("structuredContent")
        if response.get("isError") is True:
            persist()
            if isinstance(structured, dict):
                error = structured.get("error")
                if isinstance(error, dict):
                    raise RuntimeError(f"{name} tool error {error.get('code')}: {error.get('message')}")
            raise RuntimeError(f"{name} returned a tool error")
        payload = structured if isinstance(structured, dict) else None
        if payload is None:
            content = response.get("content")
            if isinstance(content, list):
                for item in content:
                    if isinstance(item, dict) and isinstance(item.get("text"), str):
                        try:
                            parsed = json.loads(item["text"])
                        except ValueError:
                            continue
                        if isinstance(parsed, dict):
                            payload = parsed
                            break
        if not isinstance(payload, dict):
            persist()
            raise AssertionError(f"{name} returned no structured result")
        entry["payload"] = payload
        if "operation_id" in payload:
            completed = _wait_operation(client, payload, 120, 0.2)
            entry["completed"] = completed
            persist()
            result = completed.get("result")
            if not isinstance(result, dict):
                raise AssertionError(f"{name} completed without a result object")
            return result
        persist()
        return payload

    def step(tool: str, arguments: dict, label: str) -> tuple[dict, dict, dict]:
        case = {"case_id": label, "state": "RUNNING", "prepare": None, "execute": None, "verify": None}
        report["steps"].append(case)
        persist()
        try:
            prepared = call(tool, {
                **credentials,
                "operation_id": "f05-prepare-" + uuid.uuid4().hex,
                "revision_id": report["revision_id"],
                **arguments,
            })
            case["prepare"] = prepared
            plan = prepared.get("plan")
            if not isinstance(plan, dict) or not isinstance(plan.get("plan_id"), str):
                raise AssertionError(f"{label} prepare omitted plan.plan_id")
            execute_id = "f05-execute-" + uuid.uuid4().hex
            executed = call("nx_execute_plan", {
                **credentials,
                "operation_id": execute_id,
                "plan_id": plan["plan_id"],
            })
            case["execute"] = executed
            if executed.get("mutation_state") != "APPLIED":
                raise AssertionError(f"{label} execute mutation_state was not APPLIED")
            verified = call("nx_verify_part", {
                **credentials,
                "operation_id": "f05-verify-" + uuid.uuid4().hex,
                "execute_operation_id": execute_id,
            })
            case["verify"] = verified
            verify_result = _assert_verified(verified, label + " nx_verify_part")
            report["revision_id"] = verify_result.get("revision_id") or executed.get("revision_id") or report["revision_id"]
            case["state"] = "PASS"
            persist()
            return prepared, executed, verified
        except Exception as exc:
            case["state"] = "FAIL"
            case["failure"] = repr(exc)
            case["reproduction_steps"] = [
                "Open a fresh isolated saved fixture with the same blank-part configuration under workspace/v2/models.",
                "Run smoke-f05-mcp.py with that fresh --part-path and a new --output path.",
                f"Repeat case {label} using the request and MCP payloads retained in this report.",
            ]
            case["retest_criterion"] = "The same case completes prepare, execute=APPLIED, nx_verify_part verified=true with every check VERIFIED, and its independent F05 oracle passes."
            persist()
            raise

    def record_case(case_id: str, evidence: dict) -> dict:
        case = {"case_id": case_id, "state": "RUNNING", **evidence}
        report["cases"].append(case)
        persist()
        return case

    try:
        if args.part_path is not None:
            part_path = args.part_path.resolve(strict=True)
            model_root = (ROOT / "workspace" / "v2" / "models").resolve(strict=True)
            if not part_path.is_relative_to(model_root) or part_path.suffix.lower() != ".prt":
                raise ValueError("--part-path must be a saved .prt inside workspace/v2/models")
            report["part_path"] = str(part_path)

        install = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "nx-sketch-mcp"
        env = os.environ.copy()
        env.update(
            PYTHONPATH=str(install / "app" / "src"),
            PYTHONUTF8="1",
            PYTHONDONTWRITEBYTECODE="1",
        )
        process = subprocess.Popen(
            [sys.executable, "-B", "-m", "nx_sketch_mcp", "--config", str(install / "mcp-config.json")],
            cwd=str(ROOT), env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, bufsize=0,
        )
        client = McpStdioClient(process, 60)
        report["initialize"] = client.request("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "f05-installed-acceptance", "version": "1"},
        })
        client.notify("notifications/initialized")
        server_info = report["initialize"].get("serverInfo")
        if not isinstance(server_info, dict):
            raise AssertionError("MCP initialize omitted serverInfo")
        report["server_version"] = server_info.get("version")
        if report["server_version"] != EXPECTED_SERVER_VERSION:
            raise AssertionError(f"installed MCP version must be {EXPECTED_SERVER_VERSION}")
        tools = client.request("tools/list").get("tools")
        if not isinstance(tools, list):
            raise AssertionError("MCP tools/list omitted tools")
        tool_names = {item.get("name") for item in tools if isinstance(item, dict)}
        report["tool_count"] = len(tool_names)
        report["tool_names"] = sorted(name for name in tool_names if isinstance(name, str))
        if len(tool_names) != EXPECTED_TOOL_COUNT:
            raise AssertionError(f"installed MCP must expose {EXPECTED_TOOL_COUNT} tools")
        datum_tool = next((tool for tool in tools if isinstance(tool, dict) and tool.get("name") == "nx_prepare_datum_plane"), None)
        if not isinstance(datum_tool, dict):
            raise AssertionError("tools/list omitted nx_prepare_datum_plane")
        properties = datum_tool.get("inputSchema", {}).get("properties", {})
        if not {"line1_id", "line2_id"} <= set(properties) or "mode" in properties:
            raise AssertionError("F05 installed schema must expose line1_id/line2_id and no public mode")
        report["datum_plane_input_schema"] = datum_tool["inputSchema"]

        ready = call("nx_ensure_ready", {})
        for _ in range(3):
            if ready.get("state") != "STARTING":
                break
            time.sleep(1)
            ready = call("nx_ensure_ready", {})
        report["ready"] = ready
        if ready.get("state") != "READY":
            raise AssertionError(f"nx_ensure_ready did not reach READY: {ready}")
        status = call("nx_bridge_status", {})
        report["initial_status"] = status
        if status.get("ownership_state") != "HUMAN_OWNED":
            raise AssertionError("readiness must leave the NX session HUMAN_OWNED")
        report["nx_pid"] = status.get("pid")
        if args.ready:
            report["passed"] = True
            return 0

        owner = call("nx_takeover", {
            "expected_pid": status["pid"],
            "expected_part_path": report["part_path"],
            "artifact_scope": "saved_independent_copy",
            "authorize": True,
        })
        credentials.update(ownership_id=owner["ownership_id"], binding_epoch=owner["binding_epoch"])
        report["revision_id"] = owner["revision_id"]
        report["capabilities"] = call("nx_get_capabilities", {})

        fixed_case = record_case("F05_tilted_fixed_support_datum", {
            "input": {"origin_mm": [10, 20, 30], "normal": [1, 1, 1], "x_axis": [1, -1, 0]},
            "objective": "Create a tilted F02 datum support for three F01 line sketches.",
        })
        active_case = fixed_case
        fixed_prepare, fixed_execute, fixed_verify = step("nx_prepare_datum_plane", {
            "origin_mm": [10, 20, 30], "normal": [1, 1, 1], "x_axis": [1, -1, 0],
        }, "F05_tilted_fixed_support_datum")
        fixed_result = _assert_verified(fixed_verify, "tilted support datum")
        support_id = fixed_execute.get("datum_plane_id") or fixed_result.get("datum_plane_id")
        if not isinstance(support_id, str) or not support_id:
            raise AssertionError("verified F02 support datum omitted datum_plane_id")
        fixed_case.update({"state": "PASS", "datum_plane_id": support_id})

        sketch_specs = [
            ("parallel-line-1", [0, 0], [20, 0]),
            ("parallel-line-2", [0, 5], [20, 5]),
            ("crossing-line", [0, 0], [0, 20]),
        ]
        sketch_ids: dict[str, str] = {}
        for name, start, end in sketch_specs:
            sketch_case = record_case("F05_create_" + name, {
                "support_datum_plane_id": support_id,
                "geometry_start_local_mm": start,
                "geometry_end_local_mm": end,
            })
            active_case = sketch_case
            _, executed, verified = step("nx_prepare_sketch", {
                "support_datum_plane_id": support_id,
                "x_axis": [1, -1, 0],
                "geometries": [{"id": name, "type": "line", "start_mm": start, "end_mm": end}],
            }, sketch_case["case_id"])
            sketch_result = _assert_verified(verified, name + " sketch")
            sketch_id = executed.get("sketch_id") or sketch_result.get("sketch_id")
            if not isinstance(sketch_id, str) or not sketch_id:
                raise AssertionError(f"{name} F01 execution omitted sketch_id")
            sketch_ids[name] = sketch_id
            sketch_case.update({"state": "PASS", "sketch_id": sketch_id})

        # Refresh all three journal IDs after every fixture feature has been inserted.
        refreshed: dict[str, dict] = {}
        for name, sketch_id in sketch_ids.items():
            inspection = call("nx_inspect_sketch", {**credentials, "sketch_id": sketch_id})
            refreshed[name] = _current_line(inspection, sketch_id)
            report.setdefault("fixture_inspections_after_creation", {})[name] = inspection
        line_records: dict[str, dict] = {}
        for record in refreshed.values():
            line_records[record["line"]["journal_id"]] = record
        required_fixture_names = {"parallel-line-1", "parallel-line-2", "crossing-line"}
        if set(refreshed) != required_fixture_names or len(line_records) != 3:
            raise AssertionError("fixture line journal IDs must resolve uniquely after all sketch creation")

        plane_cases = [
            ("F05_parallel_lines", "parallel-line-1", "parallel-line-2"),
            ("F05_parallel_lines_swapped", "parallel-line-2", "parallel-line-1"),
            ("F05_intersecting_lines", "parallel-line-1", "crossing-line"),
            ("F05_intersecting_lines_swapped", "crossing-line", "parallel-line-1"),
        ]
        plane_results: dict[str, dict] = {}
        for case_id, first_name, second_name in plane_cases:
            case = record_case(case_id, {"classification": "parallel" if "parallel" in case_id else "intersecting"})
            active_case = case
            # A feature insertion may change NX JournalIdentifiers; re-inspect both inputs just before prepare.
            fresh_first = _current_line(call("nx_inspect_sketch", {
                **credentials, "sketch_id": sketch_ids[first_name],
            }), sketch_ids[first_name])
            fresh_second = _current_line(call("nx_inspect_sketch", {
                **credentials, "sketch_id": sketch_ids[second_name],
            }), sketch_ids[second_name])
            line_records[fresh_first["line"]["journal_id"]] = fresh_first
            line_records[fresh_second["line"]["journal_id"]] = fresh_second
            line1_id = fresh_first["line"]["journal_id"]
            line2_id = fresh_second["line"]["journal_id"]
            case["fresh_line_inspections"] = {
                first_name: fresh_first,
                second_name: fresh_second,
            }
            case["requested_line_ids"] = [line1_id, line2_id]
            case["requested_source_feature_ids"] = [fresh_first["source_feature_id"], fresh_second["source_feature_id"]]
            prepared, executed, verified = step("nx_prepare_datum_plane", {
                "line1_id": line1_id,
                "line2_id": line2_id,
            }, case_id)
            verification = _assert_verified(verified, case_id)
            verification_evidence = verification.get("evidence")
            definition = verification_evidence.get("datum_plane") if isinstance(verification_evidence, dict) else None
            plane_id = executed.get("datum_plane_id") or verification.get("datum_plane_id")
            if not isinstance(plane_id, str) or not plane_id:
                raise AssertionError(f"{case_id} result omitted datum_plane_id")
            independent = _assert_datum_definition(
                definition,
                [line1_id, line2_id],
                [fresh_first["source_feature_id"], fresh_second["source_feature_id"]],
                line_records,
            )
            expected_classification = "parallel" if case["classification"] == "parallel" else "intersecting_or_skew"
            if independent["independent_oracle"]["classification"] != expected_classification:
                raise AssertionError(f"{case_id} did not match its independently calculated line classification")
            case.update({
                "state": "PASS",
                "datum_plane_id": plane_id,
                "prepare_plan": prepared.get("plan"),
                "datum_plane_definition": definition,
                "independent_checks": independent,
            })
            plane_results[case_id] = {
                "datum_plane_id": plane_id,
                "source_sketch_names": [first_name, second_name],
                "line1_id": line1_id,
                "line2_id": line2_id,
                "source_feature_ids": [fresh_first["source_feature_id"], fresh_second["source_feature_id"]],
                "definition": definition,
            }

        support_plane = plane_results["F05_intersecting_lines"]
        support_sketch_case = record_case("F05_F01_rectangle_supported_by_crossing_datum", {
            "support_datum_plane_id": support_plane["datum_plane_id"],
            "geometry": {"type": "rectangle", "corner_mm": [0, 0], "width_mm": 8, "height_mm": 5},
        })
        active_case = support_sketch_case
        _, rectangle_execute, rectangle_verify = step("nx_prepare_sketch", {
            "support_datum_plane_id": support_plane["datum_plane_id"],
            "x_axis": [1, -1, 0],
            "geometries": [{"id": "f05-supported-rectangle", "type": "rectangle",
                            "corner_mm": [0, 0], "width_mm": 8, "height_mm": 5}],
        }, support_sketch_case["case_id"])
        rectangle_result = _assert_verified(rectangle_verify, "F05 supported rectangle")
        rectangle_sketch_id = rectangle_execute.get("sketch_id") or rectangle_result.get("sketch_id")
        if not isinstance(rectangle_sketch_id, str) or not rectangle_sketch_id:
            raise AssertionError("F01 rectangle execution omitted sketch_id")
        rectangle_inspection = call("nx_inspect_sketch", {**credentials, "sketch_id": rectangle_sketch_id})
        support_association = rectangle_inspection.get("support_association")
        if support_association is None and isinstance(rectangle_inspection.get("sketch"), dict):
            support_association = rectangle_inspection["sketch"].get("support_association")
        if not _contains_value(support_association, support_plane["datum_plane_id"]):
            raise AssertionError("F01 rectangle inspection did not retain the exact F05 support datum association")
        support_sketch_case.update({
            "state": "PASS",
            "sketch_id": rectangle_sketch_id,
            "inspection": rectangle_inspection,
            "support_association": support_association,
        })

        target = report["part_path"]
        target_path = Path(target).parent / ("F05-mcp-" + uuid.uuid4().hex + "-roundtrip.prt")
        if target_path.exists():
            raise FileExistsError("unique F05 roundtrip part path already exists")
        roundtrip_case = record_case("F05_save_reopen_definition_roundtrip", {
            "target_path": str(target_path),
            "expected_line_source_pairs": [item["source_sketch_names"] for item in plane_results.values()],
        })
        active_case = roundtrip_case
        saved = call("nx_save_copy", {
            **credentials,
            "operation_id": "f05-save-" + uuid.uuid4().hex,
            "target_path": str(target_path),
            "reopen": True,
        })
        new_ownership_id = saved.get("new_ownership_id")
        new_binding_epoch = saved.get("new_binding_epoch")
        if isinstance(new_ownership_id, str) and isinstance(new_binding_epoch, int) and not isinstance(new_binding_epoch, bool):
            credentials.update(ownership_id=new_ownership_id, binding_epoch=new_binding_epoch)
        else:
            raise AssertionError("nx_save_copy omitted new ownership credentials")
        if saved.get("saved") is not True or saved.get("reopened") is not True:
            raise AssertionError("nx_save_copy did not report saved=true and reopened=true")
        checks = saved.get("checks")
        if not isinstance(checks, dict) or set(checks) != {"associations", "expression", "geometry", "history"}:
            raise AssertionError("nx_save_copy omitted the four required roundtrip checks")
        if any(not isinstance(check, dict) or check.get("status") != "VERIFIED" for check in checks.values()):
            raise AssertionError("nx_save_copy roundtrip check was not VERIFIED")
        report["saved_path"] = saved.get("saved_path") or str(target_path)
        reopened_part = call("nx_inspect_part", credentials)
        reopened_sketches = reopened_part.get("sketches")
        reopened_features = reopened_part.get("features")
        if not isinstance(reopened_sketches, list) or not isinstance(reopened_features, list):
            raise AssertionError("new-owner nx_inspect_part omitted sketches or features")
        # JournalIdentifiers can be reassigned by NX at reopen. Resolve each
        # fixture line again from its measured endpoints before checking sources.
        reopened_fixture_lines: dict[str, dict] = {}
        reopened_line_records: dict[str, dict] = {}
        for name, baseline in refreshed.items():
            matches = []
            for sketch in reopened_sketches:
                if not isinstance(sketch, dict) or not isinstance(sketch.get("geometry"), list):
                    continue
                geometry = sketch["geometry"]
                lines = [item for item in geometry if isinstance(item, dict) and item.get("kind") == "LINE"]
                if len(lines) == 1 and len(geometry) == 1 and _same_inspected_line(lines[0], baseline["line"]):
                    matches.append((sketch, lines[0]))
            if len(matches) != 1:
                raise AssertionError(f"reopened part did not resolve fixture sketch {name} uniquely by inspected line endpoints")
            sketch, line = matches[0]
            feature_id = sketch.get("feature_journal_id")
            line_id = line.get("journal_id")
            if not isinstance(feature_id, str) or not isinstance(line_id, str):
                raise AssertionError(f"reopened fixture sketch {name} omitted current feature or line journal ID")
            reopened_fixture_lines[name] = {
                "sketch": sketch, "source_feature_id": feature_id, "line": line,
            }
            reopened_line_records[line_id] = reopened_fixture_lines[name]
        retained_definitions = []
        reopened_plane_ids: dict[str, str] = {}
        for case_id, plane_case in plane_results.items():
            source_sketch_names = plane_case["source_sketch_names"]
            expected_source_ids = [reopened_fixture_lines[name]["source_feature_id"] for name in source_sketch_names]
            expected_ids = [reopened_fixture_lines[name]["line"]["journal_id"] for name in source_sketch_names]
            matching = [feature for feature in reopened_features if isinstance(feature, dict)
                        and isinstance(feature.get("datum_plane"), dict)
                        and feature["datum_plane"].get("mode") == "two_lines"
                        and [feature["datum_plane"].get("line1_id"), feature["datum_plane"].get("line2_id")] == expected_ids
                        and feature["datum_plane"].get("line1_source_feature_ids") == [expected_source_ids[0]]
                        and feature["datum_plane"].get("line2_source_feature_ids") == [expected_source_ids[1]]]
            if len(matching) != 1:
                raise AssertionError(f"reopened part did not resolve one F05 plane for source sketches {source_sketch_names}")
            feature = matching[0]
            current_plane_id = feature.get("journal_id")
            if not isinstance(current_plane_id, str) or not current_plane_id:
                raise AssertionError("reopened F05 feature omitted its current journal_id")
            reopened_plane_ids[case_id] = current_plane_id
            reopened_definition = feature.get("datum_plane")
            if not isinstance(reopened_definition, dict):
                raise AssertionError(f"reopened F05 feature {current_plane_id} omitted native datum_plane definition")
            roundtrip_oracle = _assert_datum_definition(
                reopened_definition, expected_ids, expected_source_ids, reopened_line_records,
            )
            retained_definitions.append({
                "original_feature_id": plane_case["datum_plane_id"],
                "feature_id": current_plane_id,
                "case_id": case_id,
                "source_sketch_names": source_sketch_names,
                "datum_plane": reopened_definition,
                "independent_checks": roundtrip_oracle,
            })
        crossing_plane_id = reopened_plane_ids["F05_intersecting_lines"]
        rectangle_matches = [item for item in reopened_sketches if isinstance(item, dict)
                             and isinstance(item.get("geometry"), list)
                             and len([geometry for geometry in item["geometry"]
                                      if isinstance(geometry, dict) and geometry.get("kind") == "LINE"]) == 4
                             and _contains_value(item.get("support_association"), crossing_plane_id)]
        if len(rectangle_matches) != 1:
            raise AssertionError("reopened F01 rectangle sketch lost its F05 datum support association")
        reopened_rectangle = rectangle_matches[0]
        roundtrip_case.update({
            "state": "PASS",
            "save_copy": saved,
            "inspection_after_reopen": reopened_part,
            "retained_f05_definitions": retained_definitions,
            "reopened_rectangle_sketch": reopened_rectangle,
        })

        returned = call("nx_return", credentials)
        credentials.clear()
        if returned.get("ownership_state") != "HUMAN_OWNED" or returned.get("safe_for_human") is not True:
            raise AssertionError("nx_return did not return a safe HUMAN_OWNED session")
        report["returned"] = returned
        final_status = call("nx_bridge_status", {})
        report["final_status"] = final_status
        in_flight = final_status.get("in_flight")
        if final_status.get("ownership_state") != "HUMAN_OWNED" or str(in_flight) != "0" or final_status.get("writes_halted") is not False:
            raise AssertionError("final bridge status must be HUMAN_OWNED, in_flight=0, writes_halted=false")
        roundtrip_case["ownership_return"] = returned
        roundtrip_case["final_status"] = final_status
        report["passed"] = True
        return 0
    except Exception as exc:
        report["error"] = repr(exc)
        failed_case_id = active_case.get("case_id") if active_case is not None else "F05_installed_harness"
        report["failure_record"] = {
            "case_id": failed_case_id,
            "failure": repr(exc),
            "reproduction_steps": [
                "Ensure NX is idle and open a fresh saved isolated fixture under workspace/v2/models.",
                "Rerun this script in the same mode with the fresh part path and a new --output path.",
                f"Repeat {failed_case_id} and inspect all raw request/response evidence retained in calls.",
            ],
            "retest_criterion": "The installed host reaches READY; each affected operation reports APPLIED and production verified=true; all F05 independent geometry checks, roundtrip checks, ownership return, and final status criteria pass.",
        }
        if active_case is not None and active_case.get("state") == "RUNNING":
            active_case["state"] = "FAIL"
            active_case["failure"] = repr(exc)
            active_case["reproduction_steps"] = [
                "Open the reported isolated saved part in the dedicated NX GUI.",
                "Rerun this script using the same --part-path and a new --output path.",
                f"Repeat failing case {active_case.get('case_id')} and review its retained MCP payloads.",
            ]
            active_case["retest_criterion"] = "The affected case and all four installed F05 plane cases pass with fresh inspected IDs, APPLIED execution, verified production checks, and independent residual/normal limits."
        if credentials and client is not None:
            try:
                report["failure_return"] = call("nx_return", credentials)
                credentials.clear()
            except Exception as return_error:
                report["failure_return_error"] = repr(return_error)
        return 1
    finally:
        report["completed_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        if client is not None:
            try:
                client.close()
            except Exception as close_error:
                report["client_close_error"] = repr(close_error)
        persist()
        summary = {key: report[key] for key in ("passed", "mode", "nx_pid", "saved_path", "error") if key in report}
        print(json.dumps(summary, ensure_ascii=False))


if __name__ == "__main__":
    raise SystemExit(main())
