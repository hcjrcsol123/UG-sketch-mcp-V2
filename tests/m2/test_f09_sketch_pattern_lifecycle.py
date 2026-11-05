"""F09-only STA fake lifecycle checks for sketch patterns and mirror."""

from __future__ import annotations

import importlib.util
import json
import math
import queue
import threading
import unittest
import uuid
from pathlib import Path

from evidence_recorder import record
import test_m2_bridge as _m2
from test_m3_bridge import M3BridgeSubsystemTests, successful_helper_result


PROJECT_ROOT = Path(__file__).resolve().parents[2]
SMOKE_PATH = PROJECT_ROOT / "tools" / "v2" / "f09-mcp-smoke.py"
SPEC = importlib.util.spec_from_file_location("f09_pattern_smoke_oracle", SMOKE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("could not load independent F09 transform oracle")
SMOKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE)


class F09SketchPatternLifecycleTests(M3BridgeSubsystemTests):
    @classmethod
    def setUpClass(cls):
        # This class deliberately contributes only F09 tests to run-f09-software.py.
        _m2.M2BridgeSubsystemTests.setUpClass.__func__(cls)

    def _takeover_with_pattern_fixture(self, *, source_id="seed-line"):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        created = self.create_sketch(helper, owner, epoch, [
            {"id": source_id, "type": "line", "start_mm": [2.0, 1.0], "end_mm": [4.0, 1.0]},
            {"id": "mirror-axis", "type": "line", "start_mm": [0.0, 0.0], "end_mm": [4.0, 3.0]},
            {"id": "unselected-line", "type": "line", "start_mm": [-10.0, 6.0], "end_mm": [-8.0, 6.0]},
        ])
        sketch_id = created["result"]["sketch_id"]
        inspection = successful_helper_result(self, self.helper_call(helper, "model.inspect_sketch", {
            "ownership_id": owner, "binding_epoch": epoch, "sketch_id": sketch_id,
        }))
        sketch = inspection.get("sketch", inspection)
        by_id = {item["id"]: item for item in sketch["geometry"]}
        self.assertTrue({source_id, "mirror-axis", "unselected-line"} <= set(by_id), sketch)
        return ready, helper, host, owner, epoch, sketch_id, by_id

    def _session(self, helper, owner, epoch):
        return successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))

    def _request(self, helper, owner, epoch, sketch_id, source_ids, mode, **fields):
        session = self._session(helper, owner, epoch)
        return {
            "operation_id": "f09-pattern-" + uuid.uuid4().hex,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "revision_id": session["revision_id"],
            "sketch_id": sketch_id,
            "source_curve_ids": list(source_ids),
            "mode": mode,
            **fields,
        }

    def _inspect(self, helper, owner, epoch, sketch_id):
        result = successful_helper_result(self, self.helper_call(helper, "model.inspect_sketch", {
            "ownership_id": owner, "binding_epoch": epoch, "sketch_id": sketch_id,
        }))
        return result.get("sketch", result)

    @staticmethod
    def _outputs(sketch):
        return [item for item in sketch["geometry"] if str(item.get("id", "")).startswith("f09-output-")]

    def _assert_line_set(self, actual_records, expected_records):
        self.assertEqual(len(actual_records), len(expected_records), actual_records)
        unused = list(actual_records)
        for start, end in expected_records:
            found_index = None
            for index, record_item in enumerate(unused):
                actual_start = record_item["start_mm"]
                actual_end = record_item["end_mm"]
                direct = self._point_error(actual_start, start) <= 1e-9 and self._point_error(actual_end, end) <= 1e-9
                reversed_endpoints = self._point_error(actual_start, end) <= 1e-9 and self._point_error(actual_end, start) <= 1e-9
                if direct or reversed_endpoints:
                    found_index = index
                    break
            self.assertIsNotNone(found_index, {"expected": [start, end], "remaining": unused})
            unused.pop(found_index)
        self.assertEqual(unused, [])

    @staticmethod
    def _point_error(actual, expected):
        if len(actual) != len(expected):
            return float("inf")
        return math.sqrt(sum((float(actual[index]) - float(expected[index])) ** 2 for index in range(len(actual))))

    def test_each_pattern_mode_has_read_only_preview_expected_coordinates_and_nine_verified_checks(self):
        ready, helper, host, owner, epoch, _, _ = self._takeover_with_pattern_fixture()
        cases = [
            ("linear", {
                "direction_xy": [2.0, 0.0], "spacing_mm": 3.0, "instance_count": 3,
            }, lambda: [
                (SMOKE.linear_position([2.0, 1.0], [2.0, 0.0], 3.0, index),
                 SMOKE.linear_position([4.0, 1.0], [2.0, 0.0], 3.0, index))
                for index in (1, 2)
            ]),
            ("rectangular", {
                "direction_x_xy": [1.0, 0.0], "direction_y_xy": [0.0, 1.0],
                "spacing_x_mm": 3.0, "spacing_y_mm": 4.0, "count_x": 2, "count_y": 2,
            }, lambda: [
                (SMOKE.rectangular_position([2.0, 1.0], [1.0, 0.0], [0.0, 1.0], 3.0, 4.0, x, y),
                 SMOKE.rectangular_position([4.0, 1.0], [1.0, 0.0], [0.0, 1.0], 3.0, 4.0, x, y))
                for y in range(2) for x in range(2) if x != 0 or y != 0
            ]),
            ("circular", {
                "center_xy": [0.0, 0.0], "angle_step_deg": -45.0, "instance_count": 3,
            }, lambda: [
                (SMOKE.rotate_about([2.0, 1.0], [0.0, 0.0], -45.0 * index),
                 SMOKE.rotate_about([4.0, 1.0], [0.0, 0.0], -45.0 * index))
                for index in (1, 2)
            ]),
            ("mirror", {"axis_curve_id": "mirror-axis"}, lambda: [
                (SMOKE.reflect_across_line([2.0, 1.0], [0.0, 0.0], [4.0, 3.0]),
                 SMOKE.reflect_across_line([4.0, 1.0], [0.0, 0.0], [4.0, 3.0]))
            ]),
        ]
        required_checks = {
            "pattern_instances", "source_curves", "sketch_constraints", "sketch_frames",
            "reference_status", "non_target_geometry", "history", "bodies", "sketch_solver_status",
        }
        case_results = []
        for mode, fields, expected_factory in cases:
            with self.subTest(mode=mode):
                created = self.create_sketch(helper, owner, epoch, [
                    {"id": "seed-line", "type": "line", "start_mm": [2.0, 1.0], "end_mm": [4.0, 1.0]},
                    {"id": "mirror-axis", "type": "line", "start_mm": [0.0, 0.0], "end_mm": [4.0, 3.0]},
                    {"id": "unselected-line", "type": "line", "start_mm": [-10.0, 6.0], "end_mm": [-8.0, 6.0]},
                ])
                sketch_id = created["result"]["sketch_id"]
                before = self._inspect(helper, owner, epoch, sketch_id)
                baseline = {item["id"]: item for item in before["geometry"]}
                request = self._request(helper, owner, epoch, sketch_id, ["seed-line"], mode, **fields)
                before_counts = self.fake_counts()
                prepared = successful_helper_result(self, self.helper_call(
                    helper, "operation.prepare_sketch_pattern", request))
                plan = prepared.get("result", {}).get("plan", {})
                self.assertEqual(plan.get("kind"), "edit_sketch_pattern", plan)
                positions = 2 if mode == "mirror" else (
                    fields["instance_count"] if mode in {"linear", "circular"} else fields["count_x"] * fields["count_y"]
                )
                self.assertEqual(plan.get("expected_pattern_instance_count"), positions, plan)
                expected_output_count = positions - 1
                self.assertEqual(plan.get("expected_output_curve_count"), expected_output_count, plan)
                self.assertEqual(len(plan.get("expected_instances", [])), positions, plan)
                self.assertEqual(
                    self.fake_counts()["m3_mutation_count"], before_counts["m3_mutation_count"],
                    "F09 Prepare must remain read-only",
                )
                execute_id, _, executed = self.execute_plan(
                    helper, owner, epoch, plan["plan_id"], operation_id="f09-execute-" + uuid.uuid4().hex)
                self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
                self.assertEqual(executed.get("result", {}).get("mutation_state"), "APPLIED", executed)
                self.assertEqual(executed.get("result", {}).get("expected_output_curve_count"), expected_output_count, executed)
                verified = self.verify_plan(helper, host, owner, epoch, execute_id)
                verify_result = verified.get("result", {})
                self.assertIs(verify_result.get("verified"), True, verify_result)
                checks = verify_result.get("checks", {})
                self.assertEqual(set(checks), required_checks, checks)
                for check_name in required_checks:
                    self.assertEqual(checks.get(check_name, {}).get("status"), "VERIFIED", checks)
                after = self._inspect(helper, owner, epoch, sketch_id)
                outputs = self._outputs(after)
                self.assertEqual(len(outputs), expected_output_count, after)
                self._assert_line_set(outputs, expected_factory())
                for geometry_id, record_item in baseline.items():
                    current = next((item for item in after["geometry"] if item.get("id") == geometry_id), None)
                    self.assertIsNotNone(current, after)
                    self.assertEqual(current, record_item, {"before": record_item, "after": current})
                output_ids = [item.get("id") for item in outputs]
                self.assertEqual(len(output_ids), len(set(output_ids)), outputs)
                case_results.append({
                    "mode": mode, "plan": plan, "expected_outputs": expected_factory(),
                    "verified": verify_result["verified"], "checks": checks,
                    "output_records": outputs,
                })
        record("F09-pattern-modes-prepare-execute-verify", {
            "modes": [item["mode"] for item in case_results],
            "position_tolerance_mm": 1e-9,
            "prepare_read_only": True,
            "checks_per_mode": sorted(required_checks),
            "cases": case_results,
        })

    def test_pattern_combined_edit_replays_one_operation_without_mutation(self):
        _, helper, _, owner, epoch, sketch_id, _ = self._takeover_with_pattern_fixture()
        request = self._request(helper, owner, epoch, sketch_id, ["seed-line"], "linear",
                                direction_xy=[1.0, 0.0], spacing_mm=5.0, instance_count=3)
        before = self.fake_counts()
        first = successful_helper_result(self, self.helper_call(
            helper, "operation.edit_sketch_pattern", request, timeout=10.0))
        if first.get("state") not in {"SUCCEEDED", "FAILED", "UNKNOWN", "CANCELLED"}:
            first = self.wait_operation(helper, request["operation_id"], timeout=10.0)
        self.assertEqual(first.get("state"), "SUCCEEDED", first)
        first_result = first.get("result", {})
        self.assertEqual(first_result.get("kind"), "edit_sketch_pattern", first)
        self.assertEqual(first_result.get("mutation_state"), "APPLIED", first)
        self.assertIs(first_result.get("verified"), True, first)
        after_first = self.fake_counts()
        for key in ("m3_mutation_count", "m3_execution_calls", "m3_verification_calls"):
            self.assertEqual(after_first[key], before[key] + 1, after_first)

        repeated = successful_helper_result(self, self.helper_call(
            helper, "operation.edit_sketch_pattern", request, timeout=10.0))
        self.assertEqual(repeated.get("state"), "SUCCEEDED", repeated)
        self.assertEqual(repeated.get("result"), first_result, repeated)
        after_repeat = self.fake_counts()
        for key in ("m3_mutation_count", "m3_execution_calls", "m3_verification_calls"):
            self.assertEqual(after_repeat[key], after_first[key], after_repeat)
        record("F09-pattern-combined-edit-idempotency", {
            "operation_id": request["operation_id"], "first_result": first_result,
            "replayed_result_identical": True, "adapter_counts_unchanged_on_replay": True,
        })

    def test_pattern_parser_and_numeric_preflight_reject_without_mutation_or_accept_finite_extremes(self):
        _, helper, _, owner, epoch, sketch_id, _ = self._takeover_with_pattern_fixture()
        session = self._session(helper, owner, epoch)
        base = {
            "operation_id": "f09-invalid-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch,
            "revision_id": session["revision_id"], "sketch_id": sketch_id,
            "source_curve_ids": ["seed-line"], "mode": "linear",
            "direction_xy": [1.0, 0.0], "spacing_mm": 2.0, "instance_count": 2,
        }
        shared = {key: value for key, value in base.items()
                  if key not in {"direction_xy", "spacing_mm", "instance_count"}}
        parser_invalid = [
            {**base, "direction_xy": [0.0, 0.0]},
            {**base, "spacing_mm": 0.0},
            {**base, "instance_count": 1},
            {**base, "instance_count": 257},
            {**base, "source_curve_ids": ["seed-line", "seed-line"]},
            {**shared, "mode": "rectangular", "direction_x_xy": [1.0, 0.0],
             "direction_y_xy": [1.0, 0.0], "spacing_x_mm": 2.0, "spacing_y_mm": 2.0,
             "count_x": 2, "count_y": 2},
            {**shared, "mode": "rectangular", "direction_x_xy": [1.0, 0.0],
             "direction_y_xy": [0.0, 1.0], "spacing_x_mm": 2.0, "spacing_y_mm": 2.0,
             "count_x": 256, "count_y": 2},
            {**shared, "mode": "circular", "center_xy": [0.0, 0.0],
             "angle_step_deg": 360.0, "instance_count": 2},
            {**shared, "mode": "circular", "center_xy": [0.0, 0.0],
             "angle_step_deg": 450.0, "instance_count": 2},
            {**shared, "mode": "circular", "center_xy": [0.0, 0.0],
             "angle_step_deg": 1.0e308, "instance_count": 2},
            {**shared, "mode": "mirror", "axis_curve_id": "seed-line"},
        ]
        before = self.fake_counts()
        for index, arguments in enumerate(parser_invalid):
            with self.subTest(request=index):
                arguments = {**arguments, "operation_id": "f09-invalid-" + uuid.uuid4().hex}
                response = self.helper_call(helper, "operation.prepare_sketch_pattern", arguments)
                self.assertIn("error", response, response)
                self.assertEqual(response["error"].get("code"), "INVALID_REQUEST", response)
        after = self.fake_counts()
        self.assertEqual(after["m3_pattern_prepare_calls"], before["m3_pattern_prepare_calls"], after)
        self.assertEqual(after["m3_mutation_count"], before["m3_mutation_count"], after)

        adapter_invalid = [
            {**base, "operation_id": "f09-missing-" + uuid.uuid4().hex,
             "source_curve_ids": ["missing-curve"]},
            {
                key: value for key, value in {
                    **base, "operation_id": "f09-missing-axis-" + uuid.uuid4().hex,
                    "mode": "mirror", "axis_curve_id": "missing-axis",
                }.items() if key not in {"direction_xy", "spacing_mm", "instance_count"}
            },
        ]
        dynamic_before = self.fake_counts()
        for arguments in adapter_invalid:
            response = self.helper_call(helper, "operation.prepare_sketch_pattern", arguments)
            result = response.get("result", {})
            self.assertEqual(result.get("state"), "FAILED", response)
            self.assertIn(result.get("error", {}).get("code"), {
                "CURVE_NOT_FOUND", "SKETCH_TARGET_MISMATCH", "UNSUPPORTED_CURVE_TYPE",
            }, response)
        dynamic_after = self.fake_counts()
        self.assertGreater(dynamic_after["m3_pattern_prepare_calls"], dynamic_before["m3_pattern_prepare_calls"])
        self.assertEqual(dynamic_after["m3_mutation_count"], dynamic_before["m3_mutation_count"])

        # Extremely large finite vectors may be rejected by the real adapter or
        # normalized safely; the NX-free fake uses stable normalization.
        huge_direction = {**base, "operation_id": "f09-huge-direction-" + uuid.uuid4().hex,
                          "direction_xy": [1.0e308, 1.0e308], "spacing_mm": 2.0}
        huge_plan = successful_helper_result(self, self.helper_call(
            helper, "operation.prepare_sketch_pattern", huge_direction)).get("result", {}).get("plan", {})
        final_instances = huge_plan.get("expected_instances", [])
        self.assertTrue(final_instances, huge_plan)
        self.assertTrue(all(math.isfinite(number)
                            for number in self._numbers_in(final_instances)), huge_plan)

        overflow = {**base, "operation_id": "f09-overflow-" + uuid.uuid4().hex,
                    "spacing_mm": 1.0e308, "instance_count": 3}
        overflow_before = self.fake_counts()
        rejected = self.helper_call(helper, "operation.prepare_sketch_pattern", overflow)
        overflow_result = rejected.get("result", {})
        self.assertEqual(overflow_result.get("state"), "FAILED", rejected)
        self.assertIn(overflow_result.get("error", {}).get("code"), {
            "INVALID_REQUEST", "MODEL_READ_FAILED",
        }, rejected)
        overflow_after = self.fake_counts()
        self.assertEqual(overflow_after["m3_mutation_count"], overflow_before["m3_mutation_count"], overflow_after)
        record("F09-pattern-preflight-boundaries", {
            "parser_invalid_count": len(parser_invalid),
            "adapter_invalid_count": len(adapter_invalid),
            "huge_finite_direction": "stably normalized to finite transforms",
            "derived_coordinate_overflow": "read-only prepare rejected; mutation count unchanged",
        })

    @staticmethod
    def _numbers_in(value):
        if isinstance(value, bool):
            return []
        if isinstance(value, (int, float)):
            return [float(value)]
        if isinstance(value, dict):
            return [number for child in value.values() for number in F09SketchPatternLifecycleTests._numbers_in(child)]
        if isinstance(value, (list, tuple)):
            return [number for child in value for number in F09SketchPatternLifecycleTests._numbers_in(child)]
        return []

    def test_stale_pattern_plan_rejects_without_native_fake_mutation(self):
        _, helper, _, owner, epoch, sketch_id, _ = self._takeover_with_pattern_fixture()
        request = self._request(helper, owner, epoch, sketch_id, ["seed-line"], "linear",
                                direction_xy=[1.0, 0.0], spacing_mm=3.0, instance_count=2)
        prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch_pattern", request))
        plan = prepared["result"]["plan"]
        before = self._inspect(helper, owner, epoch, sketch_id)
        self.create_sketch(helper, owner, epoch, [
            {"id": "revision-advance", "type": "line", "start_mm": [30.0, 30.0], "end_mm": [31.0, 30.0]},
        ])
        counts_before_stale = self.fake_counts()
        execute_id, _, stale = self.execute_plan(
            helper, owner, epoch, plan["plan_id"], operation_id="f09-stale-execute-" + uuid.uuid4().hex)
        self.assertNotEqual(stale.get("state"), "SUCCEEDED", stale)
        self.assertIn(stale.get("error", {}).get("code"), {"STALE_PLAN", "STALE_REVISION"}, stale)
        after = self._inspect(helper, owner, epoch, sketch_id)
        self.assertEqual(before["geometry"], after["geometry"])
        counts_after = self.fake_counts()
        self.assertEqual(counts_after["m3_mutation_count"], counts_before_stale["m3_mutation_count"], counts_after)
        record("F09-stale-pattern-plan", {
            "execute_operation_id": execute_id, "state": stale.get("state"),
            "error": stale.get("error"), "geometry_unchanged": True,
            "mutation_count_unchanged": True,
        })

    def test_pattern_rollback_returns_known_failure_and_preserves_geometry(self):
        _, helper, _, owner, epoch, sketch_id, _ = self._takeover_with_pattern_fixture()
        request = self._request(helper, owner, epoch, sketch_id, ["seed-line"], "linear",
                                direction_xy=[1.0, 0.0], spacing_mm=3.0, instance_count=2)
        plan = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch_pattern", request))["result"]["plan"]
        before = self._inspect(helper, owner, epoch, sketch_id)
        counts_before = self.fake_counts()
        self.harness_command("rollback-after-write")
        execute_id, _, rolled_back = self.execute_plan(
            helper, owner, epoch, plan["plan_id"], operation_id="f09-rollback-" + uuid.uuid4().hex)
        self.assertNotEqual(rolled_back.get("state"), "SUCCEEDED", rolled_back)
        result = rolled_back.get("result", {})
        self.assertEqual(result.get("mutation_state"), "ROLLED_BACK", rolled_back)
        self.assertIs(result.get("changed"), False, rolled_back)
        after = self._inspect(helper, owner, epoch, sketch_id)
        self.assertEqual(before["geometry"], after["geometry"])
        counts_after = self.fake_counts()
        self.assertEqual(counts_after["rollback_count"], counts_before["rollback_count"] + 1, counts_after)
        self.assertEqual(counts_after["m3_mutation_count"], counts_before["m3_mutation_count"], counts_after)
        record("F09-pattern-known-rollback", {
            "execute_operation_id": execute_id, "state": rolled_back.get("state"),
            "mutation_state": result.get("mutation_state"), "geometry_restored": True,
            "fake_rollback_count": counts_after["rollback_count"],
        })

    def test_null_pattern_verification_is_unknown_and_freezes_writes(self):
        _, helper, _, owner, epoch, sketch_id, _ = self._takeover_with_pattern_fixture()
        request = self._request(helper, owner, epoch, sketch_id, ["seed-line"], "mirror", axis_curve_id="mirror-axis")
        self.harness_command("null-next-m3-verification")
        operation = successful_helper_result(self, self.helper_call(
            helper, "operation.edit_sketch_pattern", request, timeout=10.0))
        if operation.get("state") not in {"SUCCEEDED", "FAILED", "UNKNOWN", "CANCELLED"}:
            operation = self.wait_operation(helper, request["operation_id"], timeout=10.0)
        self.assertEqual(operation.get("state"), "UNKNOWN", operation)
        self.assertEqual(operation.get("result", {}).get("mutation_state"), "APPLIED", operation)
        self.assertIs(operation.get("result", {}).get("verified"), False, operation)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        blocked_request = dict(request)
        blocked_request["operation_id"] = "f09-write-after-null-verify"
        blocked = self.helper_call(helper, "operation.edit_sketch_pattern", blocked_request)
        self.assertIn("error", blocked, blocked)
        self.assertIn(blocked["error"].get("code"), {
            "CLIENT_OUTCOME_UNKNOWN", "OPERATION_OUTCOME_UNKNOWN",
        }, blocked)
        record("F09-pattern-null-verify-frozen", {
            "operation_state": operation["state"], "mutation_state": operation.get("result", {}).get("mutation_state"),
            "writes_halted": status["writes_halted"], "blocked_followup_code": blocked["error"]["code"],
        })

    def test_late_pattern_completion_does_not_replace_unknown_or_unfreeze(self):
        _, helper, _, owner, epoch, sketch_id, _ = self._takeover_with_pattern_fixture()
        self.addCleanup(self.release_fixture_operation)
        observer = self.start_helper()
        request = self._request(helper, owner, epoch, sketch_id, ["seed-line"], "linear",
                                direction_xy=[1.0, 0.0], spacing_mm=3.0, instance_count=2)
        self.harness_command("block-operation")
        response_queue: queue.Queue = queue.Queue()

        def submit_once():
            try:
                response_queue.put(("response", self.helper_call(
                    helper, "operation.edit_sketch_pattern", request, timeout=20.0)))
            except Exception as exc:
                response_queue.put(("exception", exc))

        thread = threading.Thread(target=submit_once, daemon=True)
        thread.start()
        self.assertIs(self.harness_command("wait-operation-start").get("observed"), True)
        self.assertIs(self.harness_command("age-running-m3-operation").get("observed"), True)
        timed_out = self.get_operation(observer, request["operation_id"])
        self.assertEqual(timed_out.get("state"), "UNKNOWN", timed_out)
        self.assertEqual(timed_out.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", timed_out)
        status = successful_helper_result(self, self.helper_call(observer, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)

        self.harness_command("release-operation")
        thread.join(timeout=10.0)
        self.assertFalse(thread.is_alive(), "late F09 helper call did not return after releasing the fake adapter")
        kind, result = response_queue.get_nowait()
        if kind == "exception":
            raise result
        late = self.get_operation(observer, request["operation_id"])
        self.assertEqual(late.get("state"), "UNKNOWN", late)
        self.assertEqual(late.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", late)
        late_result = late.get("late_completion", {})
        self.assertEqual(late_result.get("mutation_state"), "APPLIED", late)
        self.assertIsNot(late_result.get("verified"), True, late)
        final_status = successful_helper_result(self, self.helper_call(observer, "bridge.status", {}))
        self.assertIs(final_status.get("writes_halted"), True, final_status)
        self.assertEqual(final_status.get("ownership_state"), "UNKNOWN_FROZEN", final_status)
        record("F09-pattern-late-unknown-freeze", {
            "state": late["state"], "late_mutation_state": late_result.get("mutation_state"),
            "writes_halted": final_status["writes_halted"], "late_completion": late_result,
        })


if __name__ == "__main__":
    unittest.main()
