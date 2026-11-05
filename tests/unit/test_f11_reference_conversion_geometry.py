"""Independent F11 state and Section-output oracles; synthetic fixtures only."""
from __future__ import annotations

import importlib.util
from pathlib import Path
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
ORACLE_PATH = PROJECT_ROOT / "tools" / "v2" / "f11-reference-conversion-oracle.py"
SPEC = importlib.util.spec_from_file_location("f11_reference_conversion_oracle", ORACLE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("could not load F11 reference conversion oracle")
ORACLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ORACLE)


class F11ReferenceConversionGeometryTests(unittest.TestCase):
    @staticmethod
    def line(tag: str, jid: str, start, end, reference_status=1):
        return {
            "tag": tag, "journal_id": jid, "object_kind": "curve",
            "reference_status": reference_status,
            "geometry": {"kind": "LINE", "endpoints_world_mm": [list(start), list(end)]},
        }

    @staticmethod
    def dimension(tag="dim-tag", jid="dim-jid", state=0, rhs="10", computed_size=10.0):
        return {
            "tag": tag, "journal_id": jid, "object_kind": "dimension", "dimension_state": state,
            "expression_tag": "expression-tag-1", "expression_name": "p1", "expression_rhs": rhs,
            "dimension_value": computed_size, "dimension_unit": "mm",
            "expression_flags": {"IsNoEdit": False, "IsMeasurementExpression": False},
            "geometry_refs": ["line-jid"], "constraint_type": "HorizontalDim",
        }

    def test_selected_curve_and_dimension_states_change_without_geometry_or_expression_drift(self):
        before = [
            self.line("line-tag", "line-jid", [0, 0, 0], [10, 0, 0]),
            self.line("other-tag", "other-jid", [0, 3, 0], [4, 3, 0]),
            self.dimension(),
        ]
        after = [
            # Journal IDs can change during NX updates; Tag remains the identity.
            self.line("line-tag", "line-jid-current", [0, 0, 0], [10, 0, 0], reference_status=0),
            self.line("other-tag", "other-jid-current", [0, 3, 0], [4, 3, 0]),
            self.dimension(jid="dim-jid-current", state=1),
        ]
        evidence = ORACLE.verify_object_transition(
            before, after, ["line-jid", "dim-jid"], "reference",
        )
        self.assertTrue(evidence["passed"])
        self.assertEqual(evidence["selected_tags"], ["dim-tag", "line-tag"])
        self.assertTrue(all(item["geometry_preserved"] for item in evidence["objects"]))

    def test_reference_to_active_selected_linear_dimension_can_sync_numeric_rhs_to_computed_size(self):
        before = [self.dimension(state=1, rhs="10", computed_size=14.0)]
        after = [self.dimension(state=0, rhs="14", computed_size=14.0)]
        evidence = ORACLE.verify_object_transition(
            before, after, ["dim-jid"], "active",
            allowed_expression_rhs_updates={"dim-tag": 14.0},
        )
        self.assertTrue(evidence["passed"])
        self.assertEqual(evidence["allowed_expression_rhs_updates"], {"dim-tag": 14.0})
        self.assertEqual(evidence["objects"][0]["expression_rhs_sync_to_computed_size"], 14.0)

        with self.assertRaisesRegex(ORACLE.OracleError, "expression_rhs changed"):
            ORACLE.verify_object_transition(before, after, ["dim-jid"], "active")
        with self.assertRaisesRegex(ORACLE.OracleError, "ComputedSize"):
            ORACLE.verify_object_transition(
                before, after, ["dim-jid"], "active",
                allowed_expression_rhs_updates={"dim-tag": 13.0},
            )
        non_linear = self.dimension(state=1, rhs="10", computed_size=14.0)
        non_linear["constraint_type"] = "DiameterDim"
        with self.assertRaisesRegex(ORACLE.OracleError, "HorizontalDim/VerticalDim"):
            ORACLE.verify_object_transition(
                [non_linear], [self.dimension(state=0, rhs="14", computed_size=14.0)],
                ["dim-jid"], "active", allowed_expression_rhs_updates={"dim-tag": 14.0},
            )

    def test_unselected_state_and_geometry_changes_are_rejected(self):
        before = [self.line("line-tag", "line-jid", [0, 0, 0], [10, 0, 0]),
                  self.line("other-tag", "other-jid", [0, 3, 0], [4, 3, 0])]
        wrong_state = [before[0], self.line("other-tag", "other-jid", [0, 3, 0], [4, 3, 0], 0)]
        with self.assertRaisesRegex(ORACLE.OracleError, "state mismatch"):
            ORACLE.verify_object_transition(before, wrong_state, ["line-jid"], "reference")
        wrong_geometry = [self.line("line-tag", "line-jid", [0, 0, 0], [10.1, 0, 0], 0), before[1]]
        with self.assertRaisesRegex(ORACLE.OracleError, "geometry"):
            ORACLE.verify_object_transition(before, wrong_geometry, ["line-jid"], "reference")

    def test_duplicate_or_missing_tags_and_stale_jids_are_rejected(self):
        before = [self.line("line-tag", "line-jid", [0, 0, 0], [10, 0, 0])]
        duplicate_after = [before[0], before[0]]
        with self.assertRaisesRegex(ORACLE.OracleError, "duplicate Tag"):
            ORACLE.verify_object_transition(before, duplicate_after, ["line-jid"], "reference")
        with self.assertRaisesRegex(ORACLE.OracleError, "current before-snapshot JIDs"):
            ORACLE.verify_object_transition(before, before, ["stale-jid"], "reference")

    def test_missing_geometry_dimension_operands_and_bad_identity_types_are_rejected(self):
        valid = self.line("line-tag", "line-jid", [0, 0, 0], [10, 0, 0])
        missing_geometry = {key: value for key, value in valid.items() if key != "geometry"}
        with self.assertRaisesRegex(ORACLE.OracleError, "missing required field 'geometry'"):
            ORACLE.verify_object_transition([missing_geometry], [missing_geometry], ["line-jid"], "active")

        incomplete_dimension = self.dimension()
        incomplete_dimension.pop("geometry_refs")
        with self.assertRaisesRegex(ORACLE.OracleError, "missing required field 'geometry_refs'"):
            ORACLE.verify_object_transition([incomplete_dimension], [incomplete_dimension], ["dim-jid"], "active")

        null_tag = {**valid, "tag": None}
        with self.assertRaisesRegex(ORACLE.OracleError, "Tag must be a nonempty string"):
            ORACLE.verify_object_transition([null_tag], [null_tag], ["line-jid"], "active")

        empty_jid = {**valid, "journal_id": ""}
        with self.assertRaisesRegex(ORACLE.OracleError, "Journal ID must be a nonempty string"):
            ORACLE.verify_object_transition([empty_jid], [empty_jid], ["line-jid"], "active")

        boolean_status = {**valid, "reference_status": True}
        with self.assertRaisesRegex(ORACLE.OracleError, "reference_status is not UF"):
            ORACLE.verify_object_transition([boolean_status], [boolean_status], ["line-jid"], "active")

    @staticmethod
    def section_line(tag: str, start, end):
        return {"tag": tag, "geometry": {
            "kind": "LINE", "endpoints_world_mm": [list(start), list(end)],
        }}

    def test_whole_sketch_section_contains_active_lines_and_excludes_reference_lines(self):
        sketch = [
            self.line("l0", "j0", [0, 0, 0], [10, 0, 0], 1),
            self.line("l1", "j1", [10, 0, 0], [10, 8, 0], 1),
            self.line("l2", "j2", [10, 8, 0], [0, 8, 0], 1),
            self.line("l3", "j3", [0, 8, 0], [0, 0, 0], 0),
        ]
        outputs = [
            self.section_line("section-copy-0", [10, 0, 0], [0, 0, 0]),
            self.section_line("section-copy-1", [10, 0, 0], [10, 8, 0]),
            self.section_line("section-copy-2", [0, 8, 0], [10, 8, 0]),
        ]
        evidence = ORACLE.verify_section_partition(sketch, outputs)
        self.assertEqual(evidence["active_curve_count"], 3)
        self.assertEqual(evidence["reference_curve_count"], 1)
        self.assertEqual(evidence["section_output_count"], 3)
        self.assertTrue(all(not row["section_tag_is_source_identity"]
                            for row in evidence["matched_active_curves"]))

        with_reference_output = outputs + [self.section_line("ref-copy", [0, 0, 0], [0, 8, 0])]
        with self.assertRaisesRegex(ORACLE.OracleError, "output count mismatch"):
            ORACLE.verify_section_partition(sketch, with_reference_output)

    def test_arc_match_uses_midpoint_to_reject_complementary_semicircle(self):
        source = {
            "kind": "ARC", "center_world_mm": [0, 0, 0], "radius_mm": 5,
            "normal_world": [0, 0, 1], "full_circle": False,
            "endpoints_world_mm": [[5, 0, 0], [-5, 0, 0]],
            "midpoint_world_mm": [0, 5, 0], "sweep_rad": 3.141592653589793,
        }
        same = {"tag": "copy", "geometry": {**source, "normal_world": [0, 0, -1],
                                                "endpoints_world_mm": [[-5, 0, 0], [5, 0, 0]]}}
        complementary = {"tag": "complement", "geometry": {**source,
                        "midpoint_world_mm": [0, -5, 0], "normal_world": [0, 0, -1]}}
        self.assertIsNotNone(ORACLE.curve_geometry_match(source, same["geometry"]))
        self.assertIsNone(ORACLE.curve_geometry_match(source, complementary["geometry"]))

    def test_circle_match_does_not_require_output_copy_tag_or_parameter_phase(self):
        source = {
            "kind": "ARC", "center_world_mm": [2, 3, 4], "radius_mm": 2,
            "normal_world": [0, 0, 1], "full_circle": True,
        }
        output = {"kind": "ARC", "center_world_mm": [2, 3, 4], "radius_mm": 2,
                  "normal_world": [0, 0, -1], "full_circle": True}
        self.assertIsNotNone(ORACLE.curve_geometry_match(source, output))


if __name__ == "__main__":
    unittest.main()
