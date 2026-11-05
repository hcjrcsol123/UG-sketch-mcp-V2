"""Offline controls for the F15 installed smoke; these tests do not start NX."""
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("f15_smoke", ROOT / "tests/nx/v2/smoke-f15-mcp.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


def schema():
    props = {key: {} for key in smoke.FIELDS}
    props.update(operation={"type": "string", "enum": ["unite", "subtract", "intersect"]},
                 retain_tools={"type": "boolean"},
                 tool_body_ids={"type": "array", "minItems": 1, "maxItems": 32,
                                "uniqueItems": True, "items": {"type": "string"}})
    return [{"name": "nx_prepare_boolean", "inputSchema": {"type": "object", "properties": props,
             "required": list(smoke.FIELDS), "additionalProperties": False}}]


def outcome(volumes):
    ids = ["output-" + str(i) for i in range(len(volumes))]
    definition = {"operation": "intersect", "retain_tools": True,
                  "target_body_tags": ["1"], "tool_body_tags": ["2", "3"]}
    evidence = {"feature_id": "feature", "body_ids": ids,
                "bodies": [{"journal_id": identity, "measurement_status": "MEASURED", "volume_mm3": volume, "bbox_mm": [i * 3, 0, 0, i * 3 + 3, 10, 10]}
                           for i, (identity, volume) in enumerate(zip(ids, volumes))], "native_definition": definition}
    return ({"verified": True, "checks": {"geometry": {"status": "VERIFIED"}}, "evidence": evidence},
            {"feature_id": "feature", "body_ids": ids},
            {"operation": "intersect", "retain_tools": True, "tool_body_ids": ["a", "b"]})


class F15SmokeControls(unittest.TestCase):
    def test_closed_schema_and_required_tool_constraints(self):
        good = schema()
        smoke.verify_schema(good)
        good[0]["inputSchema"]["properties"]["tool_body_ids"]["uniqueItems"] = False
        with self.assertRaises(AssertionError):
            smoke.verify_schema(good)

    def test_invalid_requests_cover_contract_boundaries(self):
        requests = smoke.schema_invalid_requests({"ownership_id": "x", "binding_epoch": 1}, "r", "target", "tool")
        self.assertTrue(any("retain_tools" not in x for x in requests))
        self.assertTrue(any(x.get("tool_body_ids") == [] for x in requests))
        self.assertTrue(any(x.get("tool_body_ids") == ["tool", "tool"] for x in requests))
        self.assertTrue(any(x.get("tool_body_ids") == ["target"] for x in requests))
        self.assertTrue(any(x.get("retain_tools") == 1 and type(x.get("retain_tools")) is int for x in requests))

    def test_multi_tool_union_volume_and_common_intersection_negative_control(self):
        verified, executed, request = outcome([800])
        smoke.assert_result(verified, executed, request, 800, 1, ["1", "2", "3"], [[0, 0, 0, 3, 10, 10]])
        verified["evidence"]["native_definition"]["tool_body_tags"] = ["2", "4"]
        with self.assertRaises(AssertionError):
            smoke.assert_result(verified, executed, request, 800, 1, ["1", "2", "3"], [[0, 0, 0, 3, 10, 10]])
        verified["evidence"]["native_definition"]["tool_body_tags"] = ["2", "3"]
        verified["evidence"]["bodies"][0]["volume_mm3"] = 300
        with self.assertRaises(AssertionError):
            smoke.assert_result(verified, executed, request, 800, 1, ["1", "2", "3"], [[0, 0, 0, 3, 10, 10]])

    def test_split_requires_two_measured_outputs_and_matching_feature(self):
        verified, executed, request = outcome([300, 300])
        smoke.assert_result(verified, executed, request, 600, 2, ["1", "2", "3"], [[3, 0, 0, 6, 10, 10], [0, 0, 0, 3, 10, 10]])
        verified["evidence"]["feature_id"] = "different"
        with self.assertRaises(AssertionError):
            smoke.assert_result(verified, executed, request, 600, 2, ["1", "2", "3"], [[3, 0, 0, 6, 10, 10], [0, 0, 0, 3, 10, 10]])
        verified["evidence"]["feature_id"] = "feature"
        verified["evidence"]["bodies"][0]["measurement_status"] = "UNAVAILABLE"
        with self.assertRaises(AssertionError):
            smoke.assert_result(verified, executed, request, 600, 2, ["1", "2", "3"], [[3, 0, 0, 6, 10, 10], [0, 0, 0, 3, 10, 10]])

    def test_bounds_reject_equal_volume_wrong_location(self):
        bodies = [{"bbox_mm": [280, 0, 0, 288, 10, 10]}]
        smoke.assert_bounds_unordered(bodies, smoke.EXPECTED_BOUNDS_MM["multi_intersect"])
        bodies[0]["bbox_mm"] = [281, 0, 0, 289, 10, 10]
        with self.assertRaises(AssertionError):
            smoke.assert_bounds_unordered(bodies, smoke.EXPECTED_BOUNDS_MM["multi_intersect"])

    def test_rollback_requires_advanced_revision_and_complete_undo_evidence(self):
        before = {"revision_id": "5", "bodies": [], "features": [], "sketches": []}
        after = {**before, "revision_id": "6"}
        flags = ("undo_called", "dirty_state_restored", "active_sketch_restored", "semantic_restored",
                 "geometry_restored", "history_restored", "bodies_restored", "full_body_geometry_restored",
                 "full_part_curve_inventory_restored", "point_and_direction_inventory_restored",
                 "existing_hole_and_boolean_definitions_restored", "dirty_state_after_boolean_readback_restored")
        result = {key: True for key in flags}
        result.update(mutation_state="ROLLED_BACK", revision_id="6", undo_update_error_count=0,
                      rollback_revision_advance={"prepared_revision_id": "5", "revision_id_after_rollback": "6",
                                                 "revision_advanced_past_prepared": True, "snapshot_state_synchronized": True})
        smoke.assert_failed_restored(before, after, "5", "6", result)
        result["undo_called"] = False
        with self.assertRaises(AssertionError):
            smoke.assert_failed_restored(before, after, "5", "6", result)
        result["undo_called"] = True
        with self.assertRaises(AssertionError):
            smoke.assert_failed_restored(before, before, "5", "5", {**result, "revision_id": "5"})
        with self.assertRaises(AssertionError):
            smoke.assert_failed_restored(before, {**after, "features": [{"journal_id": "changed"}]}, "5", "6", result)

    def test_unchanged_failure_requires_unchanged_revision(self):
        part = {"revision_id": "5", "bodies": [], "features": [], "sketches": []}
        smoke.assert_failed_restored(part, part, "5", "5", {"mutation_state": "UNCHANGED", "revision_id": "5"})
        with self.assertRaises(AssertionError):
            smoke.assert_failed_restored(part, {**part, "revision_id": "6"}, "5", "6",
                                         {"mutation_state": "UNCHANGED", "revision_id": "6"})

    def test_repeated_execute_requires_identical_result_snapshot_and_revision(self):
        op = {"operation_id": "execute-1", "state": "SUCCEEDED"}
        result = {"plan_id": "plan-1", "body_ids": ["body"], "mutation_state": "APPLIED"}
        part = {"revision_id": "5", "bodies": [], "features": [], "sketches": []}
        smoke.assert_same_execute_replay(op, result, dict(op), dict(result), part, dict(part), "5", "5")
        with self.assertRaises(AssertionError):
            smoke.assert_same_execute_replay(op, result, op, {**result, "body_ids": ["other"]}, part, part, "5", "5")
        with self.assertRaises(AssertionError):
            smoke.assert_same_execute_replay(op, result, op, result, part, {**part, "features": [{"journal_id": "extra"}]}, "5", "5")
        with self.assertRaises(AssertionError):
            smoke.assert_same_execute_replay(op, result, op, result, part, part, "5", "6")
        with self.assertRaises(AssertionError):
            smoke.assert_same_execute_replay(op, result, {**op, "operation_id": "execute-2"}, result, part, part, "5", "5")

    def test_non_target_identity_change_is_visible(self):
        body = {"journal_id": "sentinel", "kind": "solid", "is_solid": True,
                "bbox_mm": [0, 0, 0, 1, 1, 1], "face_ids": ["f"], "edge_ids": ["e"],
                "measurement_status": "MEASURED", "volume_mm3": 1, "surface_area_mm2": 6}
        before = smoke.other_signatures({"bodies": [body]}, set())
        after = smoke.other_signatures({"bodies": [{**body, "face_ids": ["g"]}]}, set())
        self.assertNotEqual(before, after)


if __name__ == "__main__":
    unittest.main()
