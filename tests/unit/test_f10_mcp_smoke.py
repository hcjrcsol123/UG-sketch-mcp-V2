"""Offline helper tests for the installed F10 MCP smoke; no NX calls are made."""

from __future__ import annotations

import importlib.util
import copy
import json
import math
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))
SMOKE_PATH = PROJECT_ROOT / "tools" / "v2" / "f10-mcp-smoke.py"
SPEC = importlib.util.spec_from_file_location("f10_mcp_smoke", SMOKE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("could not load F10 MCP smoke helpers")
SMOKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE)


class F10McpSmokeHelperTests(unittest.TestCase):
    @staticmethod
    def close_stdio_client(client):
        client.close()
        client._reader.join(timeout=2)
        client._stderr_reader.join(timeout=2)
        for stream in (client.process.stdout, client.process.stderr):
            if stream is not None:
                stream.close()

    @staticmethod
    def response_child(payload_length):
        child_code = (
            "import json,sys\n"
            "request=json.loads(sys.stdin.buffer.readline())\n"
            f"response={{'jsonrpc':'2.0','id':request['id'],'result':{{'payload':'x'*{payload_length}}}}}\n"
            "sys.stdout.buffer.write(json.dumps(response,separators=(',',':')).encode('utf-8')+b'\\n')\n"
            "sys.stdout.buffer.flush()\n"
        )
        return subprocess.Popen(
            [sys.executable, "-c", child_code], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0,
        )

    @staticmethod
    def source_snapshot_rows(fixture, current_ids=None, live_tags=None, body_ids=None):
        current_ids = current_ids or {}
        live_tags = live_tags or {}
        body_ids = body_ids or {}
        rows = []
        for index, source in enumerate(fixture["source_records"]):
            source_id = source["source_curve_id"]
            primitive = source["numeric_primitive"]
            row = {
                "journal_id": current_ids.get(source_id, "current-" + str(index)),
                "tag": live_tags.get(source_id, "live-tag-" + str(index)),
                "object_type": source["source_class"].title(),
                "geometry_type": (primitive["kind"].title() if primitive else source["source_class"].title()),
                "body_id": body_ids.get(source_id),
            }
            if primitive is not None and primitive["kind"] == "LINE":
                row.update(start_world_mm=primitive["start_mm"], end_world_mm=primitive["end_mm"])
            elif primitive is not None:
                row.update(
                    center_world_mm=primitive["center_mm"], radius_mm=primitive["radius_mm"],
                    start_world_mm=primitive["start_mm"], end_world_mm=primitive["end_mm"],
                    plane_normal_world=primitive["normal"],
                    start_angle_rad=primitive["start_angle_rad"],
                    end_angle_rad=primitive["end_angle_rad"],
                )
            rows.append(row)
        return rows

    @staticmethod
    def fixture_document():
        normal = SMOKE.normalize_vector([1.0, 2.0, 3.0])
        x_raw = [1.0, -1.0, 0.5]
        projection = sum(x_raw[i] * normal[i] for i in range(3))
        x_axis = SMOKE.normalize_vector([x_raw[i] - projection * normal[i] for i in range(3)])
        y_axis = SMOKE.normalize_vector(SMOKE._cross(normal, x_axis))
        origin = [3.0, -4.0, 5.0]
        true_frame = {"origin_mm": origin, "normal": normal, "x_axis": x_axis, "y_axis": y_axis}
        false_frame = {**true_frame, "origin_mm": [origin[i] + 25.0 * y_axis[i] for i in range(3)]}
        source_origin = [origin[i] + 8.0 * normal[i] for i in range(3)]
        source_frame = {**true_frame, "origin_mm": source_origin}

        def local_point(x, y):
            return [source_origin[i] + x * x_axis[i] + y * y_axis[i] for i in range(3)]

        start_angle = 0.25
        sweep = 1.75 * math.pi
        arc_center = local_point(5.0, 8.0)
        arc_radius = 3.0
        arc_point = lambda angle: [
            arc_center[i] + arc_radius * (x_axis[i] * math.cos(angle) + y_axis[i] * math.sin(angle))
            for i in range(3)
        ]
        circle_center = local_point(14.0, 8.0)
        circle_radius = 2.25
        circle_point = lambda angle: [
            circle_center[i] + circle_radius * (x_axis[i] * math.cos(angle) + y_axis[i] * math.sin(angle))
            for i in range(3)
        ]
        records = [
            {
                "source_curve_id": "curve-line-old", "journal_id": "curve-line-old",
                "tag": "source-tag-line", "source_type": "Line", "edge_type": None,
                "primitive_unavailable_reason": None,
                "primitive": {"kind": "LINE", "start_mm": local_point(2.0, 3.0),
                              "end_mm": local_point(10.0, 6.0)},
            },
            {
                "source_curve_id": "curve-arc-old", "journal_id": "curve-arc-old",
                "tag": "source-tag-arc", "source_type": "Arc", "edge_type": None,
                "primitive_unavailable_reason": None,
                "primitive": {"kind": "ARC", "center_mm": arc_center, "radius_mm": arc_radius,
                              "axis_x": x_axis, "axis_y": y_axis, "normal": normal,
                              "start_mm": arc_point(start_angle), "end_mm": arc_point(start_angle + sweep),
                              "start_angle_rad": start_angle, "end_angle_rad": start_angle + sweep,
                              "sweep_rad": sweep, "full_circle": False},
            },
            {
                "source_curve_id": "curve-circle-old", "journal_id": "curve-circle-old",
                "tag": "source-tag-circle", "source_type": "Arc", "edge_type": None,
                "primitive_unavailable_reason": None,
                "primitive": {"kind": "ARC", "center_mm": circle_center, "radius_mm": circle_radius,
                              "axis_x": x_axis, "axis_y": y_axis, "normal": normal,
                              "start_mm": circle_point(0.0), "end_mm": circle_point(2.0 * math.pi),
                              "start_angle_rad": 0.0, "end_angle_rad": 2.0 * math.pi,
                              "sweep_rad": 2.0 * math.pi, "full_circle": True},
            },
        ]
        source_geometry = [
            {"journal_id": records[0]["journal_id"], "kind": "LINE",
             "start": records[0]["primitive"]["start_mm"], "end": records[0]["primitive"]["end_mm"]},
            {"journal_id": records[1]["journal_id"], "kind": "ARC",
             "center": arc_center, "radius_mm": arc_radius, "start": arc_point(start_angle),
             "end": arc_point(start_angle + sweep), "normal": normal,
             "start_angle_rad": start_angle, "end_angle_rad": start_angle + sweep},
            {"journal_id": records[2]["journal_id"], "kind": "ARC",
             "center": circle_center, "radius_mm": circle_radius,
             "start": circle_point(0.0), "end": circle_point(2.0 * math.pi),
             "normal": normal, "start_angle_rad": 0.0, "end_angle_rad": 2.0 * math.pi},
        ]
        return ({
            "schema": SMOKE.FIXTURE_SCHEMA,
            "part_path": str(PROJECT_ROOT / "workspace" / "v2" / "models" / "synthetic.prt"),
            "units": "Millimeters",
            "target_sketch_ids": {"associative_true": "target-true", "associative_false": "target-false"},
            "target_frames": {"target-true": true_frame, "target-false": false_frame},
            "source_sketch_id": "source-sketch-old",
            "source_sketch_frame": source_frame,
            "source_curve_ids": [item["source_curve_id"] for item in records],
            "source_records": records,
        }, source_geometry)

    def test_installed_schema_check_reads_exact_projection_tools(self):
        from nx_sketch_mcp.server import TOOLS

        checked = SMOKE._check_projection_schemas(TOOLS)
        self.assertEqual(checked["checked_tools"], [
            "nx_prepare_sketch_projection", "nx_edit_sketch_projection",
        ])
        names = [tool["name"] for tool in TOOLS]
        self.assertEqual(len(names), len(set(names)))
        self.assertLessEqual(SMOKE.EXPECTED_TOOLS, set(names))

    def test_capability_check_requires_both_normal_signs_and_explicit_contract(self):
        capability = {
            "model_native_geometry_support": {
                "sketch_projection": {
                    "prepare_operation": "operation.prepare_sketch_projection",
                    "edit_operation": "operation.edit_sketch_projection",
                    "request_fields": [
                        "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                        "source_curve_ids", "direction_xyz", "associative",
                    ],
                    "supported_direction": "parallel or antiparallel to the target sketch normal; reject oblique",
                },
            },
        }
        result = SMOKE._check_projection_capability(capability)
        self.assertEqual(result["edit_operation"], "operation.edit_sketch_projection")
        capability["model_native_geometry_support"]["sketch_projection"]["supported_direction"] = "positive normal only"
        with self.assertRaisesRegex(AssertionError, "normal sign"):
            SMOKE._check_projection_capability(capability)

    def test_report_redaction_scrubs_nested_credentials_and_json_content(self):
        response = {
            "ownership_id": "synthetic-private-owner",
            "calls": [{"token_file": "synthetic-token-path"}],
            "content": [{"type": "text", "text": json.dumps({
                "authorization": "synthetic-private-token",
                "nested": {"credential": "synthetic-private-credential"},
                "ordinary": "kept",
            })}],
        }
        sanitized = SMOKE._redact(response)
        serialized = json.dumps(sanitized, ensure_ascii=False)
        for secret in ("synthetic-private-owner", "synthetic-token-path",
                       "synthetic-private-token", "synthetic-private-credential"):
            self.assertNotIn(secret, serialized)
        self.assertEqual(sanitized["ownership_id"], "[REDACTED]")
        content = json.loads(sanitized["content"][0]["text"])
        self.assertEqual(content["ordinary"], "kept")

    def test_normalization_is_stable_for_positive_negative_and_extreme_vectors(self):
        positive = SMOKE.normalize_vector([1.0, 2.0, 3.0])
        negative = SMOKE.normalize_vector([-1.0, -2.0, -3.0])
        extreme = SMOKE.normalize_vector([3.0e307, 6.0e307, 9.0e307])
        self.assertAlmostEqual(math.sqrt(math.fsum(item * item for item in positive)), 1.0)
        self.assertTrue(SMOKE._vector_close(negative, [-item for item in positive]))
        self.assertTrue(SMOKE._vector_close(extreme, positive))
        self.assertFalse(SMOKE._vector_close(SMOKE.normalize_vector([1.0, 0.0, 1.0]), positive))

    def test_direction_normalization_rejects_zero_nonfinite_and_wrong_shape(self):
        for invalid in ([0.0, 0.0, 0.0], [math.nan, 0.0, 1.0], [math.inf, 0.0, 1.0], [1.0, 2.0]):
            with self.subTest(invalid=invalid):
                with self.assertRaises(AssertionError):
                    SMOKE.normalize_vector(invalid)

    def test_plan_validator_requires_exact_target_selection_direction_and_associativity(self):
        request = {
            "operation_id": "f10-test-operation",
            "revision_id": "revision-1",
            "sketch_id": "target-1",
            "source_curve_ids": ["source-1", "source-2"],
            "direction_xyz": [-2.0, 0.0, 0.0],
            "associative": False,
        }
        plan = {
            "plan_id": "plan-1",
            "kind": "edit_sketch_projection",
            "revision_id": "revision-1",
            "sketch_id": "target-1",
            "source_curve_ids": ["source-1", "source-2"],
            "direction_xyz": [-1.0, 0.0, 0.0],
            "associative": False,
        }
        self.assertEqual(SMOKE._validate_plan({"plan": plan}, request, [-1.0, 0.0, 0.0], "offline"), plan)
        with self.assertRaisesRegex(AssertionError, "source selection"):
            SMOKE._validate_plan({"plan": {**plan, "source_curve_ids": ["source-2"]}},
                                 request, [-1.0, 0.0, 0.0], "offline")
        with self.assertRaisesRegex(AssertionError, "direction"):
            SMOKE._validate_plan({"plan": {**plan, "direction_xyz": [0.0, 1.0, 0.0]}},
                                 request, [-1.0, 0.0, 0.0], "offline")

    def test_combined_prepare_plan_copies_and_injects_the_top_level_plan_id(self):
        result = {"plan_id": "combined-plan-1", "prepare_plan": {"kind": "edit_sketch_projection"}}
        copied = SMOKE._combined_prepare_plan(result, "combined edit")
        self.assertEqual(copied["plan_id"], "combined-plan-1")
        self.assertNotIn("plan_id", result["prepare_plan"])
        with self.assertRaisesRegex(AssertionError, "differ"):
            SMOKE._combined_prepare_plan({"plan_id": "outer", "prepare_plan": {"plan_id": "inner"}},
                                         "combined edit")

    def test_first_prepare_establishes_dynamic_source_tags_and_body_identity_from_checked_jids(self):
        document, _ = self.fixture_document()
        edge = {
            "source_curve_id": "edge-fixture-old", "journal_id": "edge-fixture-old",
            "tag": "fixture-edge-tag", "source_type": "Edge", "edge_type": "Linear",
            "body_id": "fixture-body-old", "primitive_unavailable_reason": None,
            "primitive": {"kind": "LINE", "start_mm": [20.0, 2.0, 3.0],
                          "end_mm": [23.0, 2.0, 3.0]},
        }
        document["source_records"].append(edge)
        document["source_curve_ids"].append(edge["source_curve_id"])
        fixture = SMOKE.validate_fixture_document(document)
        request_ids = ["current-curve-1", "current-curve-2", "current-curve-3", "current-edge"]
        live_tags = {source["source_curve_id"]: "session-tag-" + str(index)
                     for index, source in enumerate(fixture["source_records"])}
        body_ids = {edge["source_curve_id"]: "current-body-jid"}
        rows = self.source_snapshot_rows(fixture, dict(zip(fixture["source_curve_ids"], request_ids)),
                                         live_tags, body_ids)
        plan = {"preview": {"source_snapshot": {"count": len(rows), "sources": rows}}}
        checked = SMOKE._check_plan_source_identity(plan, fixture, request_ids, "first prepare")
        self.assertEqual(checked["live_tags_by_source_id"], live_tags)
        self.assertEqual(checked["source_ids_by_source_id"],
                         dict(zip(fixture["source_curve_ids"], request_ids)))
        self.assertEqual(checked["body_ids_by_source_id"], body_ids)
        self.assertTrue(checked["fixture_tags_used_as_provenance_only"])
        with self.assertRaisesRegex(AssertionError, "Body identity changed"):
            SMOKE._check_plan_source_identity(
                plan, fixture, request_ids, "later prepare", live_tags,
                {edge["source_curve_id"]: "different-body"},
            )

    def test_raw_installed_prepare_replays_offline_against_fixture_geometry(self):
        report_path = PROJECT_ROOT / "workspace" / "v2" / "evidence" / "f10-mcp-installed-20261025-3.json"
        report = json.loads(report_path.read_text(encoding="utf-8"))
        fixture_path = Path(report["fixture_json_path"])
        fixture = SMOKE.load_fixture_json(fixture_path)
        prepare_calls = [item for item in report["calls"]
                         if item.get("tool") == "nx_prepare_sketch_projection"]
        self.assertEqual(len(prepare_calls), 1)
        entry = prepare_calls[0]
        payload = SMOKE._extract_payload(entry["mcp_response"], entry["tool"])
        self.assertEqual(payload, entry["payload"])
        self.assertEqual(SMOKE._status_state(payload), "SUCCEEDED")
        self.assertEqual(SMOKE._operation_identifier(payload), entry["arguments"]["operation_id"])
        request = entry["arguments"]
        expected_direction = SMOKE.normalize_vector(request["direction_xyz"])
        plan = SMOKE._validate_plan(payload, request, expected_direction, "recorded prepare")
        identity = SMOKE._check_plan_source_identity(
            plan, fixture, request["source_curve_ids"], "recorded prepare",
        )
        self.assertEqual(len(identity["source_records"]), len(fixture["source_records"]))
        self.assertTrue(identity["resolved_by_request_journal_id_and_independent_source_geometry"])
        for source in fixture["source_records"]:
            self.assertNotEqual(identity["live_tags_by_source_id"][source["source_curve_id"]], source["tag"])

    def test_operation_result_and_state_helpers_accept_wrapped_records(self):
        result = {"combined_edit": True, "verified": True}
        response = {
            "state": "SUCCEEDED",
            "operation": {"operation_id": "op-1", "state": "SUCCEEDED", "result": result},
        }
        self.assertEqual(SMOKE._status_state(response), "SUCCEEDED")
        self.assertIs(SMOKE._operation_result(response), result)
        self.assertEqual(SMOKE._error_code({"error": {"code": "UNSUPPORTED_PROJECTION_DIRECTION"}}),
                         "UNSUPPORTED_PROJECTION_DIRECTION")

    def test_fixture_schema_requires_complete_unique_source_map_and_arc_primitives(self):
        document, _ = self.fixture_document()
        fixture = SMOKE.validate_fixture_document(document)
        self.assertEqual(fixture["numeric_coverage"], {"LINE": 1, "PARTIAL_ARC": 1, "FULL_CIRCLE": 1})
        duplicate_id = copy.deepcopy(document)
        duplicate_id["source_curve_ids"][1] = duplicate_id["source_curve_ids"][0]
        with self.assertRaisesRegex(ValueError, "unique"):
            SMOKE.validate_fixture_document(duplicate_id)
        missing_record = copy.deepcopy(document)
        missing_record["source_records"].pop()
        with self.assertRaisesRegex(ValueError, "one-to-one"):
            SMOKE.validate_fixture_document(missing_record)
        malformed_arc = copy.deepcopy(document)
        malformed_arc["source_records"][1]["primitive"]["axis_y"] = [0.0, 0.0, 0.0]
        with self.assertRaisesRegex(AssertionError, "nonzero"):
            SMOKE.validate_fixture_document(malformed_arc)

    def test_arc_sweep_must_match_directed_parameter_limits_and_allows_reverse_domain(self):
        document, _ = self.fixture_document()
        inconsistent = copy.deepcopy(document)
        inconsistent["source_records"][1]["primitive"]["sweep_rad"] += 0.25
        with self.assertRaisesRegex(ValueError, "sweep_rad must equal"):
            SMOKE.validate_fixture_document(inconsistent)

        reverse = copy.deepcopy(document)
        primitive = reverse["source_records"][1]["primitive"]
        primitive["start_angle_rad"], primitive["end_angle_rad"] = (
            primitive["end_angle_rad"], primitive["start_angle_rad"],
        )
        primitive["sweep_rad"] = primitive["end_angle_rad"] - primitive["start_angle_rad"]
        primitive["start_mm"] = SMOKE._point_on_source_arc(primitive, primitive["start_angle_rad"])
        primitive["end_mm"] = SMOKE._point_on_source_arc(primitive, primitive["end_angle_rad"])
        checked = SMOKE.validate_fixture_document(reverse)
        self.assertLess(checked["source_records"][1]["numeric_primitive"]["sweep_rad"], 0.0)

    def test_edge_without_numeric_primitive_is_reported_and_not_claimed_as_numeric(self):
        document, _ = self.fixture_document()
        edge = {
            "source_curve_id": "edge-linear-1", "journal_id": "edge-linear-1",
            "tag": "edge-tag-1", "source_type": "Edge", "edge_type": "Linear",
            "body_id": "body-1", "primitive": None,
            "primitive_unavailable_reason": "synthetic unavailable edge geometry",
        }
        document["source_records"].append(edge)
        document["source_curve_ids"].append(edge["source_curve_id"])
        fixture = SMOKE.validate_fixture_document(document)
        self.assertEqual(fixture["unverified_edge_source_ids"], ["edge-linear-1"])
        self.assertIsNone(fixture["source_records"][-1]["numeric_primitive"])
        no_supported = copy.deepcopy(document)
        for record in no_supported["source_records"][:-1]:
            record["primitive"] = None
            record["source_type"] = "Edge"
            record["edge_type"] = "Linear"
            record["body_id"] = "body-1"
            record["primitive_unavailable_reason"] = "synthetic missing primitive"
        with self.assertRaisesRegex(ValueError, "at least one directly inspectable"):
            SMOKE.validate_fixture_document(no_supported)

    def test_line_partial_arc_and_circle_match_one_to_one_for_both_normal_signs(self):
        document, source_geometry = self.fixture_document()
        fixture = SMOKE.validate_fixture_document(document)
        frame = fixture["target_frames"]["associative_true"]
        source_records = fixture["source_records"]
        for direction in (frame["normal"], [-value for value in frame["normal"]]):
            expected = [SMOKE.expected_projection_record(source, frame, direction)
                        for source in source_records]
            actual = []
            for index, row in enumerate(expected):
                if row["kind"] == "LINE":
                    actual.append({"journal_id": "out-line", "kind": "LINE",
                                   "start": row["start_mm"], "end": row["end_mm"]})
                else:
                    primitive = source_records[index]["numeric_primitive"]
                    actual.append({"journal_id": "out-arc-" + str(index), "kind": "ARC",
                                   "center": row["center_mm"], "radius_mm": row["radius_mm"],
                                   "start": row["start_mm"], "end": row["end_mm"],
                                   "normal": row["normal"],
                                   "start_angle_rad": primitive["start_angle_rad"],
                                   "end_angle_rad": primitive["end_angle_rad"]})
            match = SMOKE.match_projection_records(actual, expected, label="synthetic direct readback")
            self.assertTrue(match["one_to_one"])
            self.assertEqual(match["numeric_sources_verified"], 3)
            self.assertFalse(match["unverified_edge_output_ids"])

    def test_partial_arc_complement_is_rejected_even_when_its_endpoints_match(self):
        document, _ = self.fixture_document()
        fixture = SMOKE.validate_fixture_document(document)
        source = fixture["source_records"][1]
        frame = fixture["target_frames"]["associative_true"]
        expected = SMOKE.expected_projection_record(source, frame, frame["normal"])
        primitive = source["numeric_primitive"]
        complement_sweep = 2.0 * math.pi - primitive["sweep_rad"]
        actual = {
            "journal_id": "complement-arc", "kind": "ARC",
            "center": expected["center_mm"], "radius_mm": expected["radius_mm"],
            "start": expected["end_mm"], "end": expected["start_mm"],
            "normal": expected["normal"],
            "start_angle_rad": primitive["end_angle_rad"],
            "end_angle_rad": primitive["end_angle_rad"] + complement_sweep,
        }
        self.assertIsNone(SMOKE.projection_geometry_match(actual, expected))

    def test_full_circle_start_point_radial_residual_is_gated_by_position_tolerance(self):
        document, _ = self.fixture_document()
        fixture = SMOKE.validate_fixture_document(document)
        source = fixture["source_records"][2]
        frame = fixture["target_frames"]["associative_true"]
        expected = SMOKE.expected_projection_record(source, frame, frame["normal"])
        primitive = source["numeric_primitive"]
        radial = SMOKE.normalize_vector([
            expected["start_mm"][i] - expected["center_mm"][i] for i in range(3)
        ])
        off_circle = [expected["center_mm"][i] +
                      (expected["radius_mm"] + 2.0 * SMOKE.POSITION_TOLERANCE_MM) * radial[i]
                      for i in range(3)]
        actual = {
            "kind": "ARC", "center": expected["center_mm"],
            "radius_mm": expected["radius_mm"], "start": off_circle, "end": off_circle,
            "normal": expected["normal"],
            "start_angle_rad": primitive["start_angle_rad"],
            "end_angle_rad": primitive["end_angle_rad"],
        }
        self.assertIsNone(SMOKE.projection_geometry_match(actual, expected))

    def test_one_to_one_match_rejects_missing_duplicate_and_unexpected_outputs(self):
        document, _ = self.fixture_document()
        fixture = SMOKE.validate_fixture_document(document)
        frame = fixture["target_frames"]["associative_true"]
        expected = [SMOKE.expected_projection_record(item, frame, frame["normal"])
                    for item in fixture["source_records"]]
        actual = []
        for index, row in enumerate(expected):
            if row["kind"] == "LINE":
                actual.append({"journal_id": "out-line", "kind": "LINE",
                               "start": row["start_mm"], "end": row["end_mm"]})
            else:
                primitive = fixture["source_records"][index]["numeric_primitive"]
                actual.append({"journal_id": f"out-arc-{index}", "kind": "ARC",
                               "center": row["center_mm"], "radius_mm": row["radius_mm"],
                               "start": row["start_mm"], "end": row["end_mm"],
                               "normal": row["normal"],
                               "start_angle_rad": primitive["start_angle_rad"],
                               "end_angle_rad": primitive["end_angle_rad"]})
        with self.assertRaisesRegex(AssertionError, "got 2"):
            SMOKE.match_projection_records(actual[:2], expected, label="missing")
        duplicate = [actual[0], actual[0], actual[2]]
        with self.assertRaises(AssertionError):
            SMOKE.match_projection_records(duplicate, expected, label="duplicate")
        unexpected = actual + [{"journal_id": "extra", "kind": "LINE",
                                "start": [200.0, 200.0, 200.0], "end": [201.0, 200.0, 200.0]}]
        with self.assertRaises(AssertionError):
            SMOKE.match_projection_records(unexpected, expected, label="unexpected")

    def test_source_sketch_resolution_and_old_geometry_mapping_survive_jid_reindex(self):
        document, source_geometry = self.fixture_document()
        fixture = SMOKE.validate_fixture_document(document)
        original = {**fixture["source_records"][0], "journal_id": "curve-line-old"}
        source_sketch = {
            "journal_id": "source-sketch-renumbered",
            "origin": fixture["source_sketch_frame"]["origin_mm"],
            "normal": fixture["source_sketch_frame"]["normal"],
            "x_axis": fixture["source_sketch_frame"]["x_axis"],
            "y_axis": fixture["source_sketch_frame"]["y_axis"],
            "geometry": [
                {**source_geometry[0], "journal_id": "curve-line-current"},
                *source_geometry[1:],
            ],
        }
        part_snapshot = {"sketches": [source_sketch]}
        resolved = SMOKE.resolve_source_sketch(part_snapshot, fixture)
        self.assertEqual(resolved["sketch_id"], "source-sketch-renumbered")
        self.assertEqual(resolved["mapping"][0]["current_journal_id"], "curve-line-current")
        old = {"journal_id": "old-line", "kind": "LINE", "start": [0.0, 0.0, 0.0],
               "end": [2.0, 0.0, 0.0]}
        new = {"journal_id": "reindexed-line", "kind": "LINE", "start": [2.0, 0.0, 0.0],
               "end": [0.0, 0.0, 0.0]}
        mapped = SMOKE.preserved_geometry_mapping([old], [new], "old geometry")
        self.assertEqual(mapped["mapping"][0]["before_journal_id"], "old-line")
        self.assertEqual(mapped["mapping"][0]["after_journal_id"], "reindexed-line")

    def test_edit_source_snapshot_re_resolves_current_jids_by_stable_tag_and_geometry(self):
        document, _ = self.fixture_document()
        fixture = SMOKE.validate_fixture_document(document)
        current_ids = {source["source_curve_id"]: "renumbered-" + str(index)
                       for index, source in enumerate(fixture["source_records"])}
        live_tags = {source["source_curve_id"]: "current-session-tag-" + str(index)
                     for index, source in enumerate(fixture["source_records"])}
        rows = self.source_snapshot_rows(fixture, current_ids, live_tags)
        snapshot = {"count": len(rows), "sources": rows}
        result = {"verification": {"evidence": {"evidence": {"measured": {
            "source_snapshot_after": snapshot,
        }}}}}
        evidence = SMOKE._check_source_snapshot_after_edit(result, fixture, live_tags, {})
        self.assertTrue(evidence["all_sources_re_resolved_by_current_session_live_tag"])
        self.assertTrue(evidence["source_records"][0]["journal_id_changed"])
        self.assertEqual(evidence["source_ids_by_source_id"], current_ids)
        self.assertEqual(evidence["source_ids_by_tag"][live_tags[fixture["source_curve_ids"][0]]],
                         current_ids[fixture["source_curve_ids"][0]])
        self.assertNotEqual(live_tags[fixture["source_curve_ids"][0]], fixture["source_records"][0]["tag"])
        with self.assertRaisesRegex(AssertionError, "source_snapshot_after"):
            SMOKE._check_source_snapshot_after_edit(
                {"verification": {"evidence": {"source_snapshot_after": snapshot}}},
                fixture, live_tags, {},
            )

    def test_operation_poll_confirms_same_id_until_terminal_and_rejection_checks_terminal_error(self):
        operation_id = "f10-oblique-test"
        initial = {"operation_id": operation_id, "state": "FAILED", "error": {"code": "DIRECTION"}}
        terminal = {"operation_id": operation_id, "state": "FAILED",
                    "error": {"code": "UNSUPPORTED_PROJECTION_DIRECTION"}}
        requested_ids = []

        def query(requested_operation_id):
            requested_ids.append(requested_operation_id)
            return terminal, {"mcp_response": {"content": []}}

        polled, _ = SMOKE._wait_for_operation(
            initial, operation_id, "oblique test", query, force_query=True,
        )
        self.assertIs(polled, terminal)
        self.assertEqual(requested_ids, [operation_id])
        self.assertEqual(SMOKE._validate_oblique_prepare_rejection(
            initial, terminal, operation_id, "oblique test"), "UNSUPPORTED_PROJECTION_DIRECTION")
        with self.assertRaisesRegex(AssertionError, "direction-specific"):
            SMOKE._validate_oblique_prepare_rejection(
                initial, {"operation_id": operation_id, "state": "FAILED", "error": {"code": "INVALID"}},
                operation_id, "oblique test",
            )

    def test_stdio_client_reads_opt_in_large_line_and_default_still_rejects_it(self):
        payload_length = 1_100_000
        expanded_limit = 2 * 1024 * 1024

        expanded_client = SMOKE.McpStdioClient(
            self.response_child(payload_length), timeout_seconds=5,
            max_line_bytes=expanded_limit,
        )
        try:
            expanded = expanded_client.request("offline/test")
            self.assertEqual(len(expanded["payload"]), payload_length)
            self.assertEqual(expanded_client.max_line_bytes, expanded_limit)
        finally:
            self.close_stdio_client(expanded_client)

        default_client = SMOKE.McpStdioClient(
            self.response_child(payload_length), timeout_seconds=5,
        )
        try:
            self.assertEqual(default_client.max_line_bytes, 1_048_576)
            with self.assertRaisesRegex(
                    SMOKE.McpClientError,
                    r"max_line_bytes=1048576, observed_at_least_bytes=1048577"):
                default_client.request("offline/test")
        finally:
            self.close_stdio_client(default_client)

    def test_transport_error_detail_reports_size_without_echoing_untrusted_text(self):
        description = SMOKE._safe_transport_error_description(
            SMOKE.McpClientError("oversized JSON-RPC line; token=must-not-appear"),
            SimpleNamespace(max_line_bytes=16 * 1024 * 1024),
        )
        self.assertIn("max_line_bytes=16777216", description)
        self.assertIn("observed at least 16777217 bytes", description)
        self.assertNotIn("must-not-appear", description)


if __name__ == "__main__":
    unittest.main()
