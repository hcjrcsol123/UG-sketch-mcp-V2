"""Offline helper checks for the installed F09 MCP smoke."""

from __future__ import annotations

import importlib.util
import json
import math
from pathlib import Path
import sys
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(PROJECT_ROOT / "src"))
SMOKE_PATH = PROJECT_ROOT / "tools" / "v2" / "f09-mcp-smoke.py"
SPEC = importlib.util.spec_from_file_location("f09_mcp_smoke", SMOKE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("could not load F09 MCP smoke helpers")
SMOKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE)


class F09McpSmokeHelperTests(unittest.TestCase):
    def test_installed_schema_check_reads_real_f09_tool_definitions(self):
        from nx_sketch_mcp.server import TOOLS

        result = SMOKE._check_pattern_schemas(TOOLS)
        self.assertEqual(result["checked_tools"], ["nx_prepare_sketch_pattern", "nx_edit_sketch_pattern"])
        names = [tool["name"] for tool in TOOLS]
        self.assertEqual(len(names), len(set(names)))
        self.assertLessEqual(SMOKE.EXPECTED_TOOLS, set(names))

    def test_report_redaction_covers_nested_credentials_without_exposing_values(self):
        report = {
            "ownership_id": "private-owner",
            "calls": [{"token_file": "secret-path", "result": {"authorization": "private-token"}}],
            "ordinary": "retained",
        }
        sanitized = SMOKE._redact(report)
        self.assertEqual(sanitized["ownership_id"], "[REDACTED]")
        self.assertEqual(sanitized["calls"][0]["token_file"], "[REDACTED]")
        self.assertEqual(sanitized["calls"][0]["result"]["authorization"], "[REDACTED]")
        self.assertEqual(sanitized["ordinary"], "retained")

    def test_report_redaction_scrubs_credentials_inside_json_content_text(self):
        response = {
            "content": [{
                "type": "text",
                "text": json.dumps({
                    "ownership_id": "synthetic-private-owner",
                    "nested": {"authorization": "synthetic-private-token"},
                    "ordinary": "retained",
                }),
            }],
        }

        sanitized = SMOKE._redact(response)
        sanitized_text = sanitized["content"][0]["text"]
        decoded_text = json.loads(sanitized_text)
        self.assertNotIn("synthetic-private-owner", sanitized_text)
        self.assertNotIn("synthetic-private-token", sanitized_text)
        self.assertEqual(decoded_text["ownership_id"], "[REDACTED]")
        self.assertEqual(decoded_text["nested"]["authorization"], "[REDACTED]")
        self.assertEqual(decoded_text["ordinary"], "retained")

    @staticmethod
    def expected_arc() -> dict:
        return {
            "kind": "ARC",
            "center_mm": [0.0, 0.0, 0.0],
            "radius_mm": 2.0,
            "start_mm": [2.0, 0.0, 0.0],
            "end_mm": [0.0, -2.0, 0.0],
            "midpoint_mm": [math.sqrt(2.0), -math.sqrt(2.0), 0.0],
            "sweep_rad": math.pi / 2.0,
            "normal": [0.0, 0.0, 1.0],
            "full_circle": False,
        }

    @staticmethod
    def actual_minor_arc() -> dict:
        return {
            "kind": "ARC",
            "center": [0.0, 0.0, 0.0],
            "radius_mm": 2.0,
            "start": [0.0, -2.0, 0.0],
            "end": [2.0, 0.0, 0.0],
            "start_angle_rad": 3.0 * math.pi / 2.0,
            "end_angle_rad": 2.0 * math.pi,
            "normal": [0.0, 0.0, 1.0],
        }

    def test_arc_geometry_matches_by_plane_extent_midpoint_and_intrinsic_length(self):
        expected, actual = self.expected_arc(), self.actual_minor_arc()
        self.assertTrue(SMOKE.geometry_record_matches(actual, expected))
        length = SMOKE.intrinsic_length_evidence(actual, expected)
        self.assertAlmostEqual(length["actual_length_mm"], math.pi, places=12)
        self.assertAlmostEqual(length["expected_length_mm"], math.pi, places=12)
        self.assertAlmostEqual(length["residual_mm"], 0.0, places=12)

    def test_arc_matcher_rejects_complement_wrong_center_radius_and_plane(self):
        expected = self.expected_arc()
        complement = {
            **self.actual_minor_arc(),
            "start": [2.0, 0.0, 0.0], "end": [0.0, -2.0, 0.0],
            "start_angle_rad": 0.0, "end_angle_rad": 3.0 * math.pi / 2.0,
        }
        self.assertFalse(SMOKE.geometry_record_matches(complement, expected))
        self.assertFalse(SMOKE.geometry_record_matches(
            {**self.actual_minor_arc(), "center": [0.1, 0.0, 0.0]}, expected))
        self.assertFalse(SMOKE.geometry_record_matches(
            {**self.actual_minor_arc(), "radius_mm": 2.1}, expected))
        self.assertFalse(SMOKE.geometry_record_matches(
            {**self.actual_minor_arc(), "normal": [0.0, 1.0, 0.0]}, expected))

    def test_full_circle_requires_a_full_turn_but_ignores_parameter_start_phase(self):
        expected = {
            "kind": "ARC", "center_mm": [1.0, 2.0, 3.0], "radius_mm": 4.0,
            "start_mm": [5.0, 2.0, 3.0], "end_mm": [5.0, 2.0, 3.0],
            "midpoint_mm": [-3.0, 2.0, 3.0], "sweep_rad": 2.0 * math.pi,
            "normal": [0.0, 0.0, 1.0], "full_circle": True,
        }
        actual = {
            "kind": "ARC", "center": [1.0, 2.0, 3.0], "radius_mm": 4.0,
            "start": [1.0, 6.0, 3.0], "end": [1.0, 6.0, 3.0],
            "start_angle_rad": math.pi / 2.0, "end_angle_rad": 5.0 * math.pi / 2.0,
            "normal": [0.0, 0.0, 1.0],
        }
        self.assertTrue(SMOKE.geometry_record_matches(actual, expected))
        self.assertFalse(SMOKE.geometry_record_matches(
            {**actual, "end_angle_rad": math.pi / 2.0 + 359.0 * math.pi / 180.0}, expected))

    def test_one_to_one_curve_match_rejects_missing_and_duplicate_actual_objects(self):
        expected = [
            {"kind": "LINE", "start_mm": [0.0, 0.0, 0.0], "end_mm": [1.0, 0.0, 0.0]},
            {"kind": "LINE", "start_mm": [0.0, 2.0, 0.0], "end_mm": [1.0, 2.0, 0.0]},
        ]
        actual = [
            {"kind": "LINE", "start": [1.0, 0.0, 0.0], "end": [0.0, 0.0, 0.0]},
            {"kind": "LINE", "start": [1.0, 2.0, 0.0], "end": [0.0, 2.0, 0.0]},
        ]
        match = SMOKE.match_geometry_records(actual, expected)
        self.assertEqual(match["expected_to_actual"], {0: 0, 1: 1})
        with self.assertRaises(AssertionError):
            SMOKE.match_geometry_records(actual[:1], expected)
        with self.assertRaises(AssertionError):
            SMOKE.match_geometry_records([actual[0], actual[0]], expected)
        with self.assertRaises(AssertionError):
            SMOKE.match_geometry_records([
                actual[0], {"kind": "LINE", "start": [4.0, 2.0, 0.0], "end": [5.0, 2.0, 0.0]},
            ], expected)

    def test_edit_preservation_matches_geometry_across_journal_id_reindex_and_reuse(self):
        before_line = {
            "journal_id": "ENTITY 3 1 1", "kind": "LINE",
            "start": [-50.0, 40.0, 0.0], "end": [-44.0, 40.0, 0.0],
        }
        arc_samples = SMOKE.arc_sample_points([0.0, 0.0], 2.0, 45.0, 80.0)
        before_arc = {
            "journal_id": "ENTITY 3 2 1", "kind": "ARC",
            "center": [0.0, 0.0, 0.0], "radius_mm": 2.0,
            "start": [arc_samples["start"][0], arc_samples["start"][1], 0.0],
            "end": [arc_samples["end"][0], arc_samples["end"][1], 0.0],
            "start_angle_rad": math.radians(45.0),
            "end_angle_rad": math.radians(125.0), "normal": [0.0, 0.0, 1.0],
        }
        after_line = {**before_line, "journal_id": "ENTITY 3 3 1"}
        after_arc = {**before_arc, "journal_id": "ENTITY 3 4 1"}
        # NX reused the old Line identifier for a genuinely new translated curve.
        new_output = {
            "journal_id": "ENTITY 3 1 1", "kind": "LINE",
            "start": [20.0, 50.0, 0.0], "end": [26.0, 50.0, 0.0],
        }
        before = {"geometry": [before_line, before_arc]}
        after = {"geometry": [new_output, after_arc, after_line]}

        with self.assertRaises(AssertionError):
            SMOKE._assert_same_existing_geometry(before, after, "read-only preview")

        result = SMOKE._assert_same_existing_geometry(
            before, after, "integrated edit", allow_journal_id_reassignment=True)
        mapping = {item["before_journal_id"]: item["after_journal_id"]
                   for item in result["preserved_geometry_journal_id_mapping"]}
        self.assertEqual(mapping, {
            "ENTITY 3 1 1": "ENTITY 3 3 1",
            "ENTITY 3 2 1": "ENTITY 3 4 1",
        })
        self.assertEqual(result["new_geometry"], [new_output])

    def test_edit_preservation_rejects_changed_geometry_after_journal_id_reindex(self):
        before = {"geometry": [{
            "journal_id": "ENTITY 3 1 1", "kind": "LINE",
            "start": [0.0, 0.0, 0.0], "end": [1.0, 0.0, 0.0],
        }]}
        after = {"geometry": [{
            "journal_id": "ENTITY 3 2 1", "kind": "LINE",
            "start": [0.0, 0.0, 0.0], "end": [1.01, 0.0, 0.0],
        }]}
        with self.assertRaisesRegex(AssertionError, "changed or removed existing"):
            SMOKE._assert_same_existing_geometry(
                before, after, "integrated edit", allow_journal_id_reassignment=True)

    def test_current_seed_selection_uses_geometry_to_refresh_journal_ids(self):
        frame = {
            "origin_mm": [0.0, 0.0, 0.0], "normal": [0.0, 0.0, 1.0],
            "x_axis": [1.0, 0.0, 0.0], "y_axis": [0.0, 1.0, 0.0],
        }
        seed_names = ["source-line", "mirror-axis"]
        expected = [
            {"kind": "LINE", "start_mm": [0.0, 0.0, 0.0], "end_mm": [2.0, 0.0, 0.0]},
            {"kind": "LINE", "start_mm": [0.0, -1.0, 0.0], "end_mm": [0.0, 1.0, 0.0]},
        ]
        sketch = {
            "units": "Millimeters", "origin": [0.0, 0.0, 0.0],
            "normal": [0.0, 0.0, 1.0], "x_axis": [1.0, 0.0, 0.0], "y_axis": [0.0, 1.0, 0.0],
            "geometry": [
                {"journal_id": "ENTITY 3 5 1", "kind": "LINE",
                 "start": [0.0, -1.0, 0.0], "end": [0.0, 1.0, 0.0]},
                {"journal_id": "ENTITY 3 6 1", "kind": "LINE",
                 "start": [0.0, 0.0, 0.0], "end": [2.0, 0.0, 0.0]},
            ],
        }
        checked = SMOKE._check_sketch_geometry(sketch, frame, expected, "reindexed seed fixture")
        resolved = SMOKE._resolve_current_seed_journal_ids(
            sketch, checked["geometry_match"], seed_names, "reindexed seed fixture")
        self.assertEqual(resolved, {"source-line": "ENTITY 3 6 1", "mirror-axis": "ENTITY 3 5 1"})


if __name__ == "__main__":
    unittest.main()
