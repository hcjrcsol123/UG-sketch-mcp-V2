"""STA/CLR lifecycle checks for F07 preview, execute, verification, and idempotency."""

from __future__ import annotations

import uuid
import queue
import threading
import unittest

import test_m2_bridge as _m2
from test_m2_bridge import record, successful_helper_result, successful_tool_result
from test_m3_bridge import M3BridgeSubsystemTests, call_tool


class F07SketchCornerLifecycleTests(M3BridgeSubsystemTests):
    @classmethod
    def setUpClass(cls):
        _m2.M2BridgeSubsystemTests.setUpClass.__func__(cls)

    def _takeover_with_corner_fixture(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        created = self.create_sketch(helper, owner, epoch, [
            {"id": "corner-line-1", "type": "line", "start_mm": [0.0, 0.0], "end_mm": [20.0, 0.0]},
            {"id": "corner-line-2", "type": "line", "start_mm": [20.0, 0.0], "end_mm": [20.0, 10.0]},
            {"id": "untouched-line", "type": "line", "start_mm": [-10.0, 5.0], "end_mm": [-6.0, 5.0]},
            {"id": "followup-line", "type": "line", "start_mm": [-6.0, 5.0], "end_mm": [-6.0, 10.0]},
        ])
        sketch_id = created["result"]["sketch_id"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        inspect = successful_helper_result(self, self.helper_call(helper, "model.inspect_sketch", {
            "ownership_id": owner, "binding_epoch": epoch, "sketch_id": sketch_id,
        }))
        geometries = inspect["sketch"]["geometry"]
        ids = {item["id"] for item in geometries}
        self.assertTrue({"corner-line-1", "corner-line-2", "untouched-line", "followup-line"} <= ids, inspect)
        common = {
            "operation_id": "f07-corner-op-" + uuid.uuid4().hex,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "revision_id": session["revision_id"],
            "sketch_id": sketch_id,
            "kind": "chamfer",
            "curve1_id": "corner-line-1",
            "curve2_id": "corner-line-2",
            "curve1_end": "end",
            "curve2_end": "start",
            "distance1_mm": 3.0,
            "distance2_mm": 3.0,
        }
        return ready, helper, host, owner, epoch, sketch_id, common

    def test_corner_preview_execute_then_verify_routes_new_plan_kind(self):
        _, helper, host, owner, epoch, sketch_id, common = self._takeover_with_corner_fixture()
        before = self.fake_counts()
        prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch_corner", common))
        plan = prepared.get("result", {}).get("plan", {})
        self.assertEqual(plan.get("kind"), "edit_sketch_corner", plan)
        self.assertEqual(plan.get("sketch_id"), sketch_id, plan)
        counts_after_prepare = self.fake_counts()
        self.assertEqual(counts_after_prepare["m3_mutation_count"], before["m3_mutation_count"], counts_after_prepare)

        execute_id = "f07-corner-execute-" + uuid.uuid4().hex
        _, accepted, executed = self.execute_plan(helper, owner, epoch, plan["plan_id"], operation_id=execute_id)
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
        self.assertEqual(executed.get("result", {}).get("kind"), "edit_sketch_corner", executed)
        self.assertEqual(executed.get("result", {}).get("mutation_state"), "APPLIED", executed)
        after_execute = self.fake_counts()
        self.assertEqual(after_execute["m3_mutation_count"], before["m3_mutation_count"] + 1, after_execute)

        verified = self.verify_plan(helper, host, owner, epoch, execute_id)
        verification = verified.get("result", {})
        self.assertIs(verification.get("verified"), True, verified)
        checks = verification.get("checks", {})
        for name in ("geometry", "sketch_solver_status", "constraints", "sketch_ownership", "non_target_geometry", "history", "bodies"):
            self.assertEqual(checks.get(name, {}).get("status"), "VERIFIED", verified)
        after_verify = self.fake_counts()
        self.assertEqual(after_verify["m3_verification_calls"], after_execute["m3_verification_calls"] + 1, after_verify)
        self.assertEqual(after_verify["m3_mutation_count"], after_execute["m3_mutation_count"], after_verify)
        record("F07-preview-execute-verify-new-kind", {
            "plan_kind": plan["kind"], "execute_state": executed["state"],
            "verified": verification["verified"], "verification_checks": checks,
        })

    def test_combined_corner_edit_repeated_operation_id_is_not_replayed(self):
        _, helper, _, _, _, _, arguments = self._takeover_with_corner_fixture()
        arguments["operation_id"] = "f07-combined-idempotent-" + uuid.uuid4().hex
        before = self.fake_counts()
        first = successful_helper_result(self, self.helper_call(helper, "operation.edit_sketch_corner", arguments, timeout=10.0))
        if first.get("state") not in {"SUCCEEDED", "FAILED", "UNKNOWN", "CANCELLED"}:
            first = self.wait_operation(helper, arguments["operation_id"], timeout=10.0)
        self.assertEqual(first.get("state"), "SUCCEEDED", first)
        first_result = first.get("result", {})
        self.assertEqual(first_result.get("kind"), "edit_sketch_corner", first)
        self.assertEqual(first_result.get("mutation_state"), "APPLIED", first)
        self.assertIs(first_result.get("verified"), True, first)
        after_first = self.fake_counts()
        self.assertEqual(after_first["m3_mutation_count"], before["m3_mutation_count"] + 1, after_first)
        self.assertEqual(after_first["m3_execution_calls"], before["m3_execution_calls"] + 1, after_first)
        self.assertEqual(after_first["m3_verification_calls"], before["m3_verification_calls"] + 1, after_first)

        repeated = successful_helper_result(self, self.helper_call(helper, "operation.edit_sketch_corner", arguments, timeout=10.0))
        self.assertEqual(repeated.get("state"), "SUCCEEDED", repeated)
        self.assertEqual(repeated.get("result"), first_result, repeated)
        after_repeat = self.fake_counts()
        for key in ("m3_mutation_count", "m3_execution_calls", "m3_verification_calls"):
            self.assertEqual(after_repeat[key], after_first[key], after_repeat)
        record("F07-combined-operation-id-idempotency", {
            "operation_id": arguments["operation_id"], "first_state": first["state"],
            "repeat_state": repeated["state"], "result_identical": repeated["result"] == first_result,
            "mutation_counts_before": {key: before[key] for key in ("m3_mutation_count", "m3_execution_calls", "m3_verification_calls")},
            "mutation_counts_after_repeat": {key: after_repeat[key] for key in ("m3_mutation_count", "m3_execution_calls", "m3_verification_calls")},
        })

    def test_combined_null_verify_result_is_unknown_without_crash(self):
        _, helper, _, _, _, _, arguments = self._takeover_with_corner_fixture()
        self.harness_command("null-next-m3-verification")
        operation = successful_helper_result(self, self.helper_call(
            helper, "operation.edit_sketch_corner", arguments, timeout=10.0))
        if operation.get("state") not in {"SUCCEEDED", "FAILED", "UNKNOWN", "CANCELLED"}:
            operation = self.wait_operation(helper, arguments["operation_id"], timeout=10.0)
        self.assertEqual(operation.get("state"), "UNKNOWN", operation)
        result = operation.get("result", {})
        self.assertEqual(result.get("mutation_state"), "APPLIED", operation)
        self.assertIs(result.get("verified"), False, operation)
        self.assertIn("error", operation, operation)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        record("F07-null-verify-is-unknown", {
            "operation_state": operation["state"], "mutation_state": result.get("mutation_state"),
            "verified": result.get("verified"), "writes_halted": status["writes_halted"],
            "operation_error": operation.get("error"),
        })

    def test_combined_verification_mismatch_is_failed_without_freezing_writes(self):
        _, helper, _, owner, epoch, sketch_id, arguments = self._takeover_with_corner_fixture()
        self.harness_command("mismatch-next-m3-verification")
        operation = successful_helper_result(self, self.helper_call(
            helper, "operation.edit_sketch_corner", arguments, timeout=10.0))
        if operation.get("state") not in {"SUCCEEDED", "FAILED", "UNKNOWN", "CANCELLED"}:
            operation = self.wait_operation(helper, arguments["operation_id"], timeout=10.0)
        self.assertEqual(operation.get("state"), "FAILED", operation)
        result = operation.get("result", {})
        self.assertEqual(result.get("mutation_state"), "APPLIED", operation)
        self.assertIs(result.get("verified"), False, operation)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), False, status)
        self.assertEqual(status.get("ownership_state"), "AI_OWNED", status)
        next_session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        followup = {
            **arguments,
            "operation_id": "f07-after-verify-mismatch-" + uuid.uuid4().hex,
            "revision_id": next_session["revision_id"],
            "sketch_id": sketch_id,
            "curve1_id": "untouched-line",
            "curve2_id": "followup-line",
            "distance1_mm": 1.0,
            "distance2_mm": 1.0,
        }
        next_plan = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch_corner", followup))
        self.assertEqual(next_plan.get("state"), "SUCCEEDED", next_plan)
        record("F07-verification-mismatch-does-not-freeze", {
            "operation_state": operation["state"], "mutation_state": result.get("mutation_state"),
            "verified": result.get("verified"), "writes_halted": status["writes_halted"],
            "followup_prepare_state": next_plan["state"],
        })

    def test_aged_unknown_corner_late_applied_result_does_not_unfreeze_or_replace_unknown(self):
        _, helper, _, _, _, _, arguments = self._takeover_with_corner_fixture()
        self.addCleanup(self.release_fixture_operation)
        observer = self.start_helper()
        self.harness_command("block-operation")
        response_queue: queue.Queue = queue.Queue()

        def submit_once():
            try:
                response_queue.put(("response", self.helper_call(
                    helper, "operation.edit_sketch_corner", arguments, timeout=20.0)))
            except Exception as exc:
                response_queue.put(("exception", exc))

        thread = threading.Thread(target=submit_once, daemon=True)
        thread.start()
        started = self.harness_command("wait-operation-start")
        self.assertIs(started.get("observed"), True, started)
        aged = self.harness_command("age-running-m3-operation")
        self.assertIs(aged.get("observed"), True, aged)
        timed_out = self.get_operation(observer, arguments["operation_id"])
        self.assertEqual(timed_out.get("state"), "UNKNOWN", timed_out)
        self.assertEqual(timed_out.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", timed_out)
        status = successful_helper_result(self, self.helper_call(observer, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)

        self.harness_command("release-operation")
        thread.join(timeout=10.0)
        self.assertFalse(thread.is_alive(), "timed-out combined edit helper call did not return after the fake NX gate was released")
        kind, response = response_queue.get_nowait()
        if kind == "exception":
            raise response
        self.assertIsInstance(response, dict)
        completed_late = self.get_operation(observer, arguments["operation_id"])
        self.assertEqual(completed_late.get("state"), "UNKNOWN", completed_late)
        self.assertEqual(completed_late.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", completed_late)
        late = completed_late.get("late_completion", {})
        self.assertEqual(late.get("mutation_state"), "APPLIED", completed_late)
        self.assertIsNot(late.get("verified"), True, completed_late)
        final_status = successful_helper_result(self, self.helper_call(observer, "bridge.status", {}))
        self.assertIs(final_status.get("writes_halted"), True, final_status)
        self.assertEqual(final_status.get("ownership_state"), "UNKNOWN_FROZEN", final_status)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_execution_calls"), 2, counts)
        self.assertEqual(counts.get("m3_mutation_count"), 2, counts)
        record("F07-timeout-late-completion-keeps-unknown", {
            "operation_state": completed_late["state"], "late_mutation_state": late.get("mutation_state"),
            "error": completed_late["error"], "writes_halted_after_late_result": final_status["writes_halted"],
            "execution_calls": counts["m3_execution_calls"], "m3_mutation_count": counts["m3_mutation_count"],
        })


if __name__ == "__main__":
    unittest.main()
