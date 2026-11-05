"""F10 fake M2 projection lifecycle checks; no native NX calls are made."""

from __future__ import annotations

import math
import unittest
import uuid
from pathlib import Path

from evidence_recorder import record
import test_m2_bridge as _m2
from test_m3_bridge import M3BridgeSubsystemTests as _M3BridgeSubsystemTests
from test_m3_bridge import successful_helper_result


PROJECT_ROOT = Path(__file__).resolve().parents[2]


class F10SketchProjectionLifecycleTests(_M3BridgeSubsystemTests):
    """Exercise bridge planning, execution, verification, and fault states on its STA fake."""

    @classmethod
    def setUpClass(cls):
        # This class deliberately adds only F10 cases to run-f10-software.py.
        _m2.M2BridgeSubsystemTests.setUpClass.__func__(cls)

    def _takeover_with_projection_fixture(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]

        source_created = self.create_sketch(helper, owner, epoch, [
            {"id": "source-line", "type": "line", "start_mm": [2.0, 1.0], "end_mm": [4.0, 1.0]},
            {"id": "source-circle", "type": "circle", "center_mm": [6.0, 2.0], "diameter_mm": 2.0},
        ], origin_mm=(0.0, 0.0, 3.0))
        source_sketch_id = source_created["result"]["sketch_id"]
        target_created = self.create_sketch(helper, owner, epoch, [
            {"id": "target-unselected-line", "type": "line", "start_mm": [-10.0, 6.0], "end_mm": [-8.0, 6.0]},
        ], origin_mm=(0.0, 0.0, 0.0))
        target_sketch_id = target_created["result"]["sketch_id"]
        source = self._inspect(helper, owner, epoch, source_sketch_id)
        target = self._inspect(helper, owner, epoch, target_sketch_id)
        source_by_id = {item["id"]: item for item in source["geometry"]}
        target_by_id = {item["id"]: item for item in target["geometry"]}
        self.assertTrue({"source-line", "source-circle"} <= set(source_by_id), source)
        self.assertIn("target-unselected-line", target_by_id, target)
        return ready, helper, host, owner, epoch, source_sketch_id, target_sketch_id, source_by_id, target_by_id

    def _session(self, helper, owner, epoch):
        return successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))

    def _request(self, helper, owner, epoch, target_sketch_id, source_ids, direction, associative,
                 *, operation_id=None):
        session = self._session(helper, owner, epoch)
        return {
            "operation_id": operation_id or ("f10-projection-" + uuid.uuid4().hex),
            "ownership_id": owner,
            "binding_epoch": epoch,
            "revision_id": session["revision_id"],
            "sketch_id": target_sketch_id,
            "source_curve_ids": list(source_ids),
            "direction_xyz": list(direction),
            "associative": associative,
        }

    def _inspect(self, helper, owner, epoch, sketch_id):
        response = successful_helper_result(self, self.helper_call(helper, "model.inspect_sketch", {
            "ownership_id": owner, "binding_epoch": epoch, "sketch_id": sketch_id,
        }))
        return response.get("sketch", response)

    def _prepare(self, helper, request):
        response = self.helper_call(helper, "operation.prepare_sketch_projection", request, timeout=10.0)
        value = successful_helper_result(self, response)
        if value.get("state") not in {"SUCCEEDED", "FAILED", "UNKNOWN", "CANCELLED"}:
            value = self.wait_operation(helper, request["operation_id"], timeout=10.0)
        return value

    def _edit(self, helper, request):
        response = self.helper_call(helper, "operation.edit_sketch_projection", request, timeout=10.0)
        value = successful_helper_result(self, response)
        if value.get("state") not in {"SUCCEEDED", "FAILED", "UNKNOWN", "CANCELLED"}:
            value = self.wait_operation(helper, request["operation_id"], timeout=10.0)
        return value

    @staticmethod
    def _outputs(sketch):
        return [item for item in sketch["geometry"] if str(item.get("id", "")).startswith("f10-output-")]

    @staticmethod
    def _expected_xy_projection(source_record, source_origin_z=3.0, target_origin_z=0.0):
        """Independent XY-to-XY line calculation for this fake fixture, in millimetres."""
        dz = float(source_origin_z) - float(target_origin_z)
        start = list(source_record["start_mm"])
        end = list(source_record["end_mm"])
        # The input direction is +Z or -Z. Intersecting either ray with the target
        # plane removes only dz, so the target-local XY coordinates are preserved.
        return ([start[0], start[1]], [end[0], end[1]], dz)

    @staticmethod
    def _assert_point_close(test, actual, expected, tolerance_mm=1e-9):
        test.assertEqual(len(actual), len(expected))
        error = math.sqrt(sum((float(actual[index]) - float(expected[index])) ** 2
                              for index in range(len(actual))))
        test.assertLessEqual(error, tolerance_mm, {"actual_mm": actual, "expected_mm": expected, "error_mm": error})

    def _assert_projection_checks(self, verification):
        checks = verification.get("result", {}).get("checks", {})
        required = {
            "projection_geometry", "source_curves", "sketch_constraints", "sketch_frames",
            "reference_status", "non_target_geometry", "history", "bodies", "sketch_solver_status",
        }
        self.assertEqual(set(checks), required, checks)
        for check_name in required:
            self.assertEqual(checks.get(check_name, {}).get("status"), "VERIFIED", checks)
        return checks

    def test_prepare_is_read_only_and_positive_normal_projects_explicit_curve_ids(self):
        _, helper, host, owner, epoch, source_sketch_id, target_sketch_id, source_before, target_before = \
            self._takeover_with_projection_fixture()
        request = self._request(helper, owner, epoch, target_sketch_id,
                                ["source-line", "source-circle"], [0.0, 0.0, 9.0], True)
        counts_before = self.fake_counts()
        prepared = self._prepare(helper, request)
        self.assertEqual(prepared.get("state"), "SUCCEEDED", prepared)
        plan = prepared.get("result", {}).get("plan", {})
        self.assertEqual(plan.get("kind"), "edit_sketch_projection", plan)
        self.assertEqual(plan.get("sketch_id"), target_sketch_id, plan)
        self.assertEqual(plan.get("source_curve_ids"), ["source-line", "source-circle"], plan)
        self.assertEqual(plan.get("source_count"), 2, plan)
        self.assertEqual(plan.get("expected_output_curve_count"), 2, plan)
        self.assertIs(plan.get("associative"), True, plan)
        self.assertEqual(plan.get("direction_xyz"), [0.0, 0.0, 1.0], plan)
        self.assertEqual(len(plan.get("expected_output_curve_records", [])), 2, plan)
        counts_after_prepare = self.fake_counts()
        self.assertEqual(counts_after_prepare["m3_projection_prepare_calls"], counts_before["m3_projection_prepare_calls"] + 1)
        self.assertEqual(counts_after_prepare["m3_mutation_count"], counts_before["m3_mutation_count"], counts_after_prepare)
        self.assertEqual(counts_after_prepare["m3_sketch_count"], counts_before["m3_sketch_count"], counts_after_prepare)
        self.assertEqual(self._inspect(helper, owner, epoch, source_sketch_id)["geometry"], list(source_before.values()))
        self.assertEqual(self._inspect(helper, owner, epoch, target_sketch_id)["geometry"], list(target_before.values()))

        execute_id, _, executed = self.execute_plan(helper, owner, epoch, plan["plan_id"],
            operation_id="f10-projection-execute-" + uuid.uuid4().hex)
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
        self.assertEqual(executed.get("result", {}).get("mutation_state"), "APPLIED", executed)
        self.assertEqual(executed.get("result", {}).get("expected_output_curve_count"), 2, executed)
        self.assertIs(executed.get("result", {}).get("native_nx_invoked"), False, executed)
        verified = self.verify_plan(helper, host, owner, epoch, execute_id)
        verification = verified.get("result", {})
        self.assertIs(verification.get("verified"), True, verification)
        self._assert_projection_checks(verified)

        target_after = self._inspect(helper, owner, epoch, target_sketch_id)
        outputs = self._outputs(target_after)
        self.assertEqual(len(outputs), 2, target_after)
        by_source = {item.get("source_curve_id"): item for item in executed["result"].get("output_curve_identities", [])}
        self.assertEqual(set(by_source), {"source-line", "source-circle"}, by_source)
        line_output = next(item for item in outputs if item.get("type") == "line")
        expected_start, expected_end, dz = self._expected_xy_projection(source_before["source-line"])
        self.assertEqual(dz, 3.0)
        self._assert_point_close(self, line_output["start_mm"], expected_start)
        self._assert_point_close(self, line_output["end_mm"], expected_end)
        circle_output = next(item for item in outputs if item.get("type") == "circle")
        self._assert_point_close(self, circle_output["center_mm"], source_before["source-circle"]["center_mm"])
        self.assertAlmostEqual(circle_output["diameter_mm"], source_before["source-circle"]["diameter_mm"], places=9)
        target_original = [item for item in target_after["geometry"] if item.get("id") == "target-unselected-line"]
        self.assertEqual(target_original, [target_before["target-unselected-line"]])
        self.assertEqual(self._inspect(helper, owner, epoch, source_sketch_id)["geometry"], list(source_before.values()))
        counts_after = self.fake_counts()
        self.assertEqual(counts_after["m3_projection_execution_calls"], counts_before["m3_projection_execution_calls"] + 1)
        self.assertEqual(counts_after["m3_projection_verification_calls"], counts_before["m3_projection_verification_calls"] + 1)
        record("F10-fake-projection-prepare-execute-verify", {
            "operation_id": execute_id, "source_curve_ids": ["source-line", "source-circle"],
            "target_sketch_id": target_sketch_id, "direction_xyz": [0.0, 0.0, 1.0],
            "associative": True, "prepare_read_only": True, "output_count": len(outputs),
            "line_projection_residual_tolerance_mm": 1e-9,
            "verified_checks": sorted(verification["checks"]), "native_nx_invoked": False,
        })

    def test_negative_normal_is_accepted_and_has_same_projection_for_xy_target(self):
        _, helper, host, owner, epoch, _, target_sketch_id, source_before, _ = self._takeover_with_projection_fixture()
        request = self._request(helper, owner, epoch, target_sketch_id, ["source-line"], [0.0, 0.0, -4.0], False)
        prepared = self._prepare(helper, request)
        self.assertEqual(prepared.get("state"), "SUCCEEDED", prepared)
        plan = prepared["result"]["plan"]
        self.assertEqual(plan.get("direction_xyz"), [0.0, 0.0, -1.0], plan)
        execute_id, _, executed = self.execute_plan(helper, owner, epoch, plan["plan_id"],
            operation_id="f10-projection-negative-" + uuid.uuid4().hex)
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
        verified = self.verify_plan(helper, host, owner, epoch, execute_id)
        self.assertIs(verified.get("result", {}).get("verified"), True, verified)
        self._assert_projection_checks(verified)
        output = self._outputs(self._inspect(helper, owner, epoch, target_sketch_id))[0]
        expected_start, expected_end, _ = self._expected_xy_projection(source_before["source-line"])
        self._assert_point_close(self, output["start_mm"], expected_start)
        self._assert_point_close(self, output["end_mm"], expected_end)
        record("F10-fake-projection-negative-normal", {
            "operation_id": execute_id, "direction_xyz": [0.0, 0.0, -1.0],
            "same_xy_projection_as_positive_normal": True, "native_nx_invoked": False,
        })

    def _assert_association_behavior(self, associative):
        _, helper, _, owner, epoch, source_sketch_id, target_sketch_id, source_before, _ = \
            self._takeover_with_projection_fixture()
        request = self._request(helper, owner, epoch, target_sketch_id, ["source-line"], [0.0, 0.0, 1.0], associative)
        before_counts = self.fake_counts()
        operation = self._edit(helper, request)
        self.assertEqual(operation.get("state"), "SUCCEEDED", operation)
        result = operation.get("result", {})
        self.assertEqual(result.get("kind"), "edit_sketch_projection", operation)
        self.assertEqual(result.get("mutation_state"), "APPLIED", operation)
        self.assertIs(result.get("verified"), True, operation)
        self.assertTrue(result.get("combined_edit"), operation)
        outputs_before_move = self._outputs(self._inspect(helper, owner, epoch, target_sketch_id))
        self.assertEqual(len(outputs_before_move), 1, outputs_before_move)
        output_before = dict(outputs_before_move[0])
        source_old = dict(source_before["source-line"])

        replay = self._edit(helper, request)
        self.assertEqual(replay.get("state"), "SUCCEEDED", replay)
        self.assertEqual(replay.get("result"), result, replay)
        replay_counts = self.fake_counts()
        for key in ("m3_projection_execution_calls", "m3_projection_verification_calls", "m3_mutation_count"):
            self.assertEqual(replay_counts[key], before_counts[key] + 1, replay_counts)

        moved = self.harness_command("move-f10-source")
        self.assertIs(moved.get("observed"), True, moved)
        source_after = self._inspect(helper, owner, epoch, source_sketch_id)
        source_line = next(item for item in source_after["geometry"] if item.get("id") == "source-line")
        self._assert_point_close(self, source_line["start_mm"], [source_old["start_mm"][0] + 2.0, source_old["start_mm"][1]])
        self._assert_point_close(self, source_line["end_mm"], [source_old["end_mm"][0] + 2.0, source_old["end_mm"][1]])
        outputs_after_move = self._outputs(self._inspect(helper, owner, epoch, target_sketch_id))
        self.assertEqual(len(outputs_after_move), 1, outputs_after_move)
        output_after = outputs_after_move[0]
        if associative:
            self._assert_point_close(self, output_after["start_mm"], [output_before["start_mm"][0] + 2.0,
                                                                        output_before["start_mm"][1]])
            self._assert_point_close(self, output_after["end_mm"], [output_before["end_mm"][0] + 2.0,
                                                                      output_before["end_mm"][1]])
        else:
            self.assertEqual(output_after, output_before)
        record("F10-fake-associativity-" + str(associative).lower(), {
            "operation_id": request["operation_id"], "associative": associative,
            "source_move_mm": [2.0, 0.0], "source_changed": True,
            "output_followed_source": associative,
            "adapter_counts_unchanged_on_replay": True, "native_nx_invoked": False,
        })

    def test_associative_projection_updates_when_fake_source_moves(self):
        self._assert_association_behavior(True)

    def test_nonassociative_projection_stays_independent_when_fake_source_moves(self):
        self._assert_association_behavior(False)

    def test_invalid_ids_and_oblique_or_in_plane_directions_fail_without_mutation(self):
        _, helper, _, owner, epoch, _, target_sketch_id, _, target_before = self._takeover_with_projection_fixture()
        base = self._request(helper, owner, epoch, target_sketch_id, ["source-line"], [0.0, 0.0, 1.0], True)
        counts_before = self.fake_counts()
        invalid_requests = [
            {**base, "operation_id": "f10-missing-source-" + uuid.uuid4().hex,
             "source_curve_ids": ["missing-curve"]},
            {**base, "operation_id": "f10-oblique-" + uuid.uuid4().hex,
             "direction_xyz": [1.0, 0.0, 1.0]},
            {**base, "operation_id": "f10-in-plane-" + uuid.uuid4().hex,
             "direction_xyz": [1.0, 0.0, 0.0]},
            {**base, "operation_id": "f10-missing-sketch-" + uuid.uuid4().hex,
             "sketch_id": "missing-sketch"},
        ]
        expected_errors = [
            {"CURVE_NOT_FOUND"}, {"UNSUPPORTED_PROJECTION_DIRECTION"},
            {"UNSUPPORTED_PROJECTION_DIRECTION"}, {"SKETCH_NOT_FOUND"},
        ]
        for request, allowed in zip(invalid_requests, expected_errors):
            with self.subTest(operation_id=request["operation_id"]):
                failed = self._prepare(helper, request)
                self.assertEqual(failed.get("state"), "FAILED", failed)
                self.assertIn(failed.get("error", {}).get("code"), allowed, failed)
        after_adapter_rejections = self.fake_counts()
        self.assertEqual(after_adapter_rejections["m3_projection_prepare_calls"],
                         counts_before["m3_projection_prepare_calls"] + len(invalid_requests), after_adapter_rejections)
        self.assertEqual(after_adapter_rejections["m3_mutation_count"], counts_before["m3_mutation_count"], after_adapter_rejections)
        self.assertEqual(self._inspect(helper, owner, epoch, target_sketch_id)["geometry"], list(target_before.values()))

        parser_invalid = [
            {**base, "operation_id": "f10-empty-sources-" + uuid.uuid4().hex, "source_curve_ids": []},
            {**base, "operation_id": "f10-duplicate-sources-" + uuid.uuid4().hex,
             "source_curve_ids": ["source-line", "source-line"]},
            {**base, "operation_id": "f10-zero-direction-" + uuid.uuid4().hex,
             "direction_xyz": [0.0, 0.0, 0.0]},
        ]
        parser_count_before = self.fake_counts()["m3_projection_prepare_calls"]
        for request in parser_invalid:
            with self.subTest(operation_id=request["operation_id"]):
                response = self.helper_call(helper, "operation.prepare_sketch_projection", request)
                self.assertEqual(response.get("error", {}).get("code"), "INVALID_REQUEST", response)
        self.assertEqual(self.fake_counts()["m3_projection_prepare_calls"], parser_count_before)
        record("F10-fake-preflight-rejections", {
            "adapter_rejection_count": len(invalid_requests),
            "parser_rejection_count": len(parser_invalid), "mutation_count_unchanged": True,
            "directions_rejected": [[1.0, 0.0, 1.0], [1.0, 0.0, 0.0], [0.0, 0.0, 0.0]],
            "source_id_rejected": "missing-curve", "native_nx_invoked": False,
        })

    def test_stale_projection_plan_is_rejected_before_fake_adapter_execution(self):
        _, helper, _, owner, epoch, _, target_sketch_id, _, target_before = self._takeover_with_projection_fixture()
        request = self._request(helper, owner, epoch, target_sketch_id, ["source-line"], [0.0, 0.0, 1.0], True)
        prepared = self._prepare(helper, request)
        self.assertEqual(prepared.get("state"), "SUCCEEDED", prepared)
        plan = prepared["result"]["plan"]
        self.create_sketch(helper, owner, epoch, [
            {"id": "revision-advance", "type": "line", "start_mm": [30.0, 30.0], "end_mm": [31.0, 30.0]},
        ])
        counts_before_stale = self.fake_counts()
        execute_id, _, stale = self.execute_plan(helper, owner, epoch, plan["plan_id"],
            operation_id="f10-stale-execute-" + uuid.uuid4().hex)
        self.assertNotEqual(stale.get("state"), "SUCCEEDED", stale)
        self.assertIn(stale.get("error", {}).get("code"), {"STALE_PLAN", "STALE_REVISION"}, stale)
        self.assertEqual(self._inspect(helper, owner, epoch, target_sketch_id)["geometry"], list(target_before.values()))
        counts_after_stale = self.fake_counts()
        self.assertEqual(counts_after_stale["m3_projection_execution_calls"],
                         counts_before_stale["m3_projection_execution_calls"], counts_after_stale)
        self.assertEqual(counts_after_stale["m3_mutation_count"], counts_before_stale["m3_mutation_count"], counts_after_stale)
        record("F10-fake-stale-plan", {
            "execute_operation_id": execute_id, "state": stale.get("state"),
            "error": stale.get("error"), "geometry_unchanged": True,
            "adapter_execution_not_called": True, "native_nx_invoked": False,
        })

    def test_fake_rollback_restores_projection_target_and_returns_known_failure(self):
        _, helper, _, owner, epoch, source_sketch_id, target_sketch_id, source_before, target_before = \
            self._takeover_with_projection_fixture()
        request = self._request(helper, owner, epoch, target_sketch_id, ["source-line"], [0.0, 0.0, 1.0], True)
        prepared = self._prepare(helper, request)
        self.assertEqual(prepared.get("state"), "SUCCEEDED", prepared)
        plan = prepared["result"]["plan"]
        counts_before = self.fake_counts()
        self.harness_command("rollback-after-write")
        execute_id, _, rolled_back = self.execute_plan(helper, owner, epoch, plan["plan_id"],
            operation_id="f10-rollback-" + uuid.uuid4().hex)
        self.assertEqual(rolled_back.get("state"), "FAILED", rolled_back)
        result = rolled_back.get("result", {})
        self.assertEqual(result.get("mutation_state"), "ROLLED_BACK", rolled_back)
        self.assertIs(result.get("changed"), False, rolled_back)
        self.assertEqual(self._inspect(helper, owner, epoch, target_sketch_id)["geometry"], list(target_before.values()))
        self.assertEqual(self._inspect(helper, owner, epoch, source_sketch_id)["geometry"], list(source_before.values()))
        counts_after = self.fake_counts()
        self.assertEqual(counts_after["rollback_count"], counts_before["rollback_count"] + 1, counts_after)
        self.assertEqual(counts_after["m3_mutation_count"], counts_before["m3_mutation_count"], counts_after)
        record("F10-fake-known-rollback", {
            "execute_operation_id": execute_id, "state": rolled_back["state"],
            "mutation_state": result["mutation_state"], "fake_target_restored": True,
            "native_undo_exercised": False, "native_nx_invoked": False,
        })

    def test_null_projection_verification_is_unknown_and_freezes_future_writes(self):
        _, helper, _, owner, epoch, _, target_sketch_id, _, _ = self._takeover_with_projection_fixture()
        request = self._request(helper, owner, epoch, target_sketch_id, ["source-line"], [0.0, 0.0, 1.0], True)
        self.harness_command("null-next-m3-verification")
        operation = self._edit(helper, request)
        self.assertEqual(operation.get("state"), "UNKNOWN", operation)
        self.assertEqual(operation.get("result", {}).get("mutation_state"), "APPLIED", operation)
        self.assertIs(operation.get("result", {}).get("verified"), False, operation)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        blocked_request = dict(request)
        blocked_request["operation_id"] = "f10-write-after-unknown-" + uuid.uuid4().hex
        blocked = self.helper_call(helper, "operation.edit_sketch_projection", blocked_request)
        self.assertEqual(blocked.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked)
        counts = self.fake_counts()
        self.assertEqual(counts["m3_projection_execution_calls"], 1, counts)
        self.assertEqual(counts["m3_projection_verification_calls"], 1, counts)
        self.assertEqual(counts["m3_projection_prepare_calls"], 1, counts)
        record("F10-fake-null-verification-freeze", {
            "operation_id": request["operation_id"], "state": operation["state"],
            "mutation_state": operation["result"]["mutation_state"],
            "writes_halted": status["writes_halted"], "blocked_write_code": blocked["error"]["code"],
            "native_nx_invoked": False,
        })


def _f10_test_suite():
    # The lifecycle class inherits M3 process helpers, but direct execution of
    # this module must contribute only its own F10 tests, not inherited M3 cases.
    selected = []
    for name in sorted(dir(F10SketchProjectionLifecycleTests)):
        method = getattr(F10SketchProjectionLifecycleTests, name)
        if name.startswith("test_") and getattr(method, "__module__", None) == __name__:
            selected.append(F10SketchProjectionLifecycleTests(name))
    return unittest.TestSuite(selected)


if __name__ == "__main__":
    unittest.TextTestRunner(verbosity=2).run(_f10_test_suite())
