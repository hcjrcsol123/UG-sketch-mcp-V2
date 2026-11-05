"""F08-only STA/CLR lifecycle checks for offset plan and compound operations."""

from __future__ import annotations

import unittest
import uuid
import queue
import threading

import test_m2_bridge as _m2
from test_m2_bridge import record, successful_helper_result
from test_m3_bridge import M3BridgeSubsystemTests


class F08SketchOffsetLifecycleTests(M3BridgeSubsystemTests):
    @classmethod
    def setUpClass(cls):
        # Reuse the established bridge fixture, but load_tests below deliberately
        # excludes inherited F01-F07/M3 regression methods from this F08 run.
        _m2.M2BridgeSubsystemTests.setUpClass.__func__(cls)

    def _takeover_with_offset_fixture(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        created = self.create_sketch(helper, owner, epoch, [
            {"id": "offset-line-1", "type": "line", "start_mm": [0.0, 0.0], "end_mm": [10.0, 0.0]},
            {"id": "offset-line-2", "type": "line", "start_mm": [10.0, 0.0], "end_mm": [10.0, 10.0]},
            {"id": "offset-unselected", "type": "line", "start_mm": [-10.0, 5.0], "end_mm": [-6.0, 5.0]},
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
        self.assertTrue({"offset-line-1", "offset-line-2", "offset-unselected"} <= ids, inspect)
        common = {
            "operation_id": "f08-offset-op-" + uuid.uuid4().hex,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "revision_id": session["revision_id"],
            "sketch_id": sketch_id,
            "curve_chain": [
                {"curve_id": "offset-line-1", "direction": "forward"},
                {"curve_id": "offset-line-2", "direction": "forward"},
            ],
            "distance_mm": 2.0,
            "side": "left",
            "retain_original": True,
            "join_mode": "extension",
        }
        return ready, helper, host, owner, epoch, sketch_id, common

    def test_preview_execute_and_independent_verify_preserve_chain_and_closure(self):
        _, helper, host, owner, epoch, sketch_id, arguments = self._takeover_with_offset_fixture()
        before = self.fake_counts()
        prepared = successful_helper_result(self, self.helper_call(
            helper, "operation.prepare_sketch_offset", arguments))
        plan = prepared.get("result", {}).get("plan", {})
        self.assertEqual(plan.get("kind"), "edit_sketch_offset", plan)
        self.assertEqual(plan.get("sketch_id"), sketch_id, plan)
        self.assertEqual(plan.get("side"), "left", plan)
        self.assertEqual(plan.get("distance_mm"), 2.0, plan)
        self.assertEqual(plan.get("join_mode"), "extension", plan)
        self.assertEqual(
            self.fake_counts()["m3_mutation_count"], before["m3_mutation_count"],
            "preparing an offset must not mutate native state",
        )

        execute_id = "f08-offset-execute-" + uuid.uuid4().hex
        _, _, executed = self.execute_plan(helper, owner, epoch, plan["plan_id"], operation_id=execute_id)
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
        self.assertEqual(executed.get("result", {}).get("kind"), "edit_sketch_offset", executed)
        self.assertEqual(executed.get("result", {}).get("mutation_state"), "APPLIED", executed)
        self.assertEqual(self.fake_counts()["m3_mutation_count"], before["m3_mutation_count"] + 1)

        verified = self.verify_plan(helper, host, owner, epoch, execute_id)
        verification = verified.get("result", {})
        self.assertIs(verification.get("verified"), True, verified)
        checks = verification.get("checks", {})
        for name in (
            "offset_geometry", "source_curves", "sketch_constraints", "sketch_frames",
            "reference_status", "non_target_geometry", "history", "bodies", "sketch_solver_status",
        ):
            self.assertEqual(checks.get(name, {}).get("status"), "VERIFIED", verified)
        native = successful_helper_result(self, self.helper_call(helper, "model.inspect_sketch", {
            "ownership_id": owner, "binding_epoch": epoch, "sketch_id": sketch_id,
        }))
        geometry = native["sketch"]["geometry"]
        self.assertEqual(len(geometry), 5, native)
        by_id = {item["id"]: item for item in geometry}
        self.assertEqual(by_id["offset-line-1"]["start_mm"], [0.0, 0.0], native)
        self.assertEqual(by_id["offset-line-1"]["end_mm"], [10.0, 0.0], native)
        self.assertEqual(by_id["offset-line-2"]["start_mm"], [10.0, 0.0], native)
        self.assertEqual(by_id["offset-line-2"]["end_mm"], [10.0, 10.0], native)
        self.assertEqual(by_id["offset-unselected"]["start_mm"], [-10.0, 5.0], native)
        offsets = [item for item in geometry if str(item.get("id", "")).startswith("f08-output-line-")]
        self.assertEqual(len(offsets), 2, native)
        self.assertAlmostEqual(offsets[0]["start_mm"][1], 2.0, places=9)
        self.assertAlmostEqual(offsets[0]["end_mm"][0], 8.0, places=9)
        self.assertAlmostEqual(offsets[0]["end_mm"][1], 2.0, places=9)
        self.assertEqual(offsets[0]["end_mm"], offsets[1]["start_mm"])
        self.assertAlmostEqual(offsets[1]["end_mm"][0], 8.0, places=9)
        record("F08-preview-execute-verify-open-chain", {
            "plan_kind": plan["kind"], "execute_state": executed["state"],
            "verified": verification["verified"], "checks": checks,
            "output_chain": offsets,
        })

    def test_compound_offset_edit_repeated_operation_id_is_not_replayed(self):
        _, helper, _, _, _, _, arguments = self._takeover_with_offset_fixture()
        arguments["operation_id"] = "f08-combined-idempotent-" + uuid.uuid4().hex
        before = self.fake_counts()
        first = successful_helper_result(self, self.helper_call(
            helper, "operation.edit_sketch_offset", arguments, timeout=10.0))
        if first.get("state") not in {"SUCCEEDED", "FAILED", "UNKNOWN", "CANCELLED"}:
            first = self.wait_operation(helper, arguments["operation_id"], timeout=10.0)
        self.assertEqual(first.get("state"), "SUCCEEDED", first)
        first_result = first.get("result", {})
        self.assertEqual(first_result.get("kind"), "edit_sketch_offset", first)
        self.assertEqual(first_result.get("mutation_state"), "APPLIED", first)
        self.assertIs(first_result.get("verified"), True, first)
        after_first = self.fake_counts()
        for key in ("m3_mutation_count", "m3_execution_calls", "m3_verification_calls"):
            self.assertEqual(after_first[key], before[key] + 1, after_first)

        repeated = successful_helper_result(self, self.helper_call(
            helper, "operation.edit_sketch_offset", arguments, timeout=10.0))
        self.assertEqual(repeated.get("state"), "SUCCEEDED", repeated)
        self.assertEqual(repeated.get("result"), first_result, repeated)
        after_repeat = self.fake_counts()
        for key in ("m3_mutation_count", "m3_execution_calls", "m3_verification_calls"):
            self.assertEqual(after_repeat[key], after_first[key], after_repeat)
        record("F08-compound-operation-id-idempotency", {
            "operation_id": arguments["operation_id"], "state": first["state"],
            "repeat_result_identical": repeated["result"] == first_result,
            "execution_counts_after_repeat": {
                key: after_repeat[key] for key in (
                    "m3_mutation_count", "m3_execution_calls", "m3_verification_calls",
                )
            },
        })

    def test_stale_offset_plan_is_rejected_after_a_new_offset(self):
        ready = self.start_harness()
        helper = self.start_helper()
        self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        created = self.create_sketch(helper, owner, epoch, [
            {"id": "old-1", "type": "line", "start_mm": [0.0, 0.0], "end_mm": [10.0, 0.0]},
            {"id": "old-2", "type": "line", "start_mm": [10.0, 0.0], "end_mm": [10.0, 10.0]},
            {"id": "new-1", "type": "line", "start_mm": [20.0, 0.0], "end_mm": [30.0, 0.0]},
            {"id": "new-2", "type": "line", "start_mm": [30.0, 0.0], "end_mm": [30.0, 10.0]},
        ])
        sketch_id = created["result"]["sketch_id"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        common = {
            "operation_id": "f08-stale-offset-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch, "revision_id": session["revision_id"],
            "sketch_id": sketch_id, "distance_mm": 1.0, "side": "left",
            "retain_original": True, "join_mode": "extension",
        }
        stale = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch_offset", {
            **common, "curve_chain": [
                {"curve_id": "old-1", "direction": "forward"},
                {"curve_id": "old-2", "direction": "forward"},
            ],
        }))
        stale_plan = stale["result"]["plan"]
        fresh = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch_offset", {
            **common, "operation_id": "f08-fresh-offset-" + uuid.uuid4().hex,
            "curve_chain": [
                {"curve_id": "new-1", "direction": "forward"},
                {"curve_id": "new-2", "direction": "forward"},
            ],
        }))
        fresh_plan = fresh["result"]["plan"]
        fresh_operation_id = "f08-fresh-execute-" + uuid.uuid4().hex
        _, _, fresh_execution = self.execute_plan(
            helper, owner, epoch, fresh_plan["plan_id"], operation_id=fresh_operation_id)
        self.assertEqual(fresh_execution.get("state"), "SUCCEEDED", fresh_execution)
        before_stale = successful_helper_result(self, self.helper_call(helper, "model.inspect_sketch", {
            "ownership_id": owner, "binding_epoch": epoch, "sketch_id": sketch_id,
        }))
        counts_before_stale = self.fake_counts()
        _, _, stale_execution = self.execute_plan(
            helper, owner, epoch, stale_plan["plan_id"], operation_id="f08-stale-execute-" + uuid.uuid4().hex)
        self.assertNotEqual(stale_execution.get("state"), "SUCCEEDED", stale_execution)
        self.assertIn(stale_execution.get("error", {}).get("code"), {"STALE_PLAN", "STALE_REVISION"}, stale_execution)
        after_stale = successful_helper_result(self, self.helper_call(helper, "model.inspect_sketch", {
            "ownership_id": owner, "binding_epoch": epoch, "sketch_id": sketch_id,
        }))
        self.assertEqual(before_stale["sketch"]["geometry"], after_stale["sketch"]["geometry"])
        counts_after_stale = self.fake_counts()
        self.assertEqual(counts_after_stale["m3_mutation_count"], counts_before_stale["m3_mutation_count"])
        record("F08-stale-plan-rejects-without-mutation", {
            "stale_state": stale_execution.get("state"), "stale_error": stale_execution.get("error"),
            "geometry_unchanged": True, "mutation_count_unchanged": True,
        })

    def test_clr_parser_rejects_bad_offset_contract_before_adapter_call(self):
        _, helper, _, owner, epoch, sketch_id, valid = self._takeover_with_offset_fixture()
        before = self.fake_counts()
        invalid_requests = [
            {**valid, "operation_id": "f08-clr-retain-" + uuid.uuid4().hex, "retain_original": False},
            {**valid, "operation_id": "f08-clr-direction-" + uuid.uuid4().hex,
             "curve_chain": [{"curve_id": "offset-line-1", "direction": "sideways"}]},
            {key: value for key, value in valid.items() if key != "distance_mm"},
            {**valid, "operation_id": "f08-clr-duplicate-" + uuid.uuid4().hex,
             "curve_chain": [
                 {"curve_id": "offset-line-1", "direction": "forward"},
                 {"curve_id": "offset-line-1", "direction": "reverse"},
             ]},
            {**valid, "operation_id": "f08-clr-negative-" + uuid.uuid4().hex, "distance_mm": -1.0},
        ]
        for request in invalid_requests:
            request["ownership_id"] = owner
            request["binding_epoch"] = epoch
            request["sketch_id"] = sketch_id
            with self.subTest(request=request):
                response = self.helper_call(helper, "operation.prepare_sketch_offset", request)
                self.assertIn("error", response, response)
        after = self.fake_counts()
        self.assertEqual(after["m3_offset_prepare_calls"], before["m3_offset_prepare_calls"], after)
        self.assertEqual(after["m3_mutation_count"], before["m3_mutation_count"], after)
        record("F08-CLR-offset-invalid-requests-never-call-adapter", {
            "invalid_request_count": len(invalid_requests),
            "adapter_prepare_calls_before": before["m3_offset_prepare_calls"],
            "adapter_prepare_calls_after": after["m3_offset_prepare_calls"],
            "m3_mutations": after["m3_mutation_count"],
        })

    def test_compound_offset_verification_mismatch_is_failed_without_write_freeze(self):
        _, helper, _, owner, epoch, _, arguments = self._takeover_with_offset_fixture()
        self.harness_command("mismatch-next-m3-verification")
        operation = successful_helper_result(self, self.helper_call(
            helper, "operation.edit_sketch_offset", arguments, timeout=10.0))
        if operation.get("state") not in {"SUCCEEDED", "FAILED", "UNKNOWN", "CANCELLED"}:
            operation = self.wait_operation(helper, arguments["operation_id"], timeout=10.0)
        self.assertEqual(operation.get("state"), "FAILED", operation)
        self.assertEqual(operation.get("result", {}).get("mutation_state"), "APPLIED", operation)
        self.assertIs(operation.get("result", {}).get("verified"), False, operation)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), False, status)
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        next_request = {
            **arguments,
            "operation_id": "f08-after-mismatch-" + uuid.uuid4().hex,
            "revision_id": session["revision_id"],
            "curve_chain": [
                {"curve_id": "offset-unselected", "direction": "forward"},
            ],
        }
        followup = self.helper_call(helper, "operation.prepare_sketch_offset", next_request)
        self.assertNotIn("error", followup, followup)
        record("F08-offset-verification-mismatch-does-not-freeze", {
            "operation_state": operation["state"],
            "mutation_state": operation["result"].get("mutation_state"),
            "verified": operation["result"].get("verified"),
            "writes_halted": status["writes_halted"],
            "followup_prepare_state": followup.get("state"),
        })

    def test_compound_offset_null_verification_is_unknown_and_freezes_writes(self):
        _, helper, _, _, _, _, arguments = self._takeover_with_offset_fixture()
        self.harness_command("null-next-m3-verification")
        operation = successful_helper_result(self, self.helper_call(
            helper, "operation.edit_sketch_offset", arguments, timeout=10.0))
        if operation.get("state") not in {"SUCCEEDED", "FAILED", "UNKNOWN", "CANCELLED"}:
            operation = self.wait_operation(helper, arguments["operation_id"], timeout=10.0)
        self.assertEqual(operation.get("state"), "UNKNOWN", operation)
        self.assertEqual(operation.get("result", {}).get("mutation_state"), "APPLIED", operation)
        self.assertIs(operation.get("result", {}).get("verified"), False, operation)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        record("F08-offset-null-verification-stays-unknown", {
            "operation_state": operation["state"], "mutation_state": operation["result"].get("mutation_state"),
            "writes_halted": status["writes_halted"], "error": operation.get("error"),
        })

    def test_late_offset_completion_does_not_replace_unknown_or_unfreeze(self):
        _, helper, _, _, _, _, arguments = self._takeover_with_offset_fixture()
        self.addCleanup(self.release_fixture_operation)
        observer = self.start_helper()
        self.harness_command("block-operation")
        response_queue: queue.Queue = queue.Queue()

        def submit_once():
            try:
                response_queue.put(("response", self.helper_call(
                    helper, "operation.edit_sketch_offset", arguments, timeout=20.0)))
            except Exception as exc:
                response_queue.put(("exception", exc))

        thread = threading.Thread(target=submit_once, daemon=True)
        thread.start()
        self.assertIs(self.harness_command("wait-operation-start").get("observed"), True)
        self.assertIs(self.harness_command("age-running-m3-operation").get("observed"), True)
        timed_out = self.get_operation(observer, arguments["operation_id"])
        self.assertEqual(timed_out.get("state"), "UNKNOWN", timed_out)
        self.assertEqual(timed_out.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", timed_out)
        status = successful_helper_result(self, self.helper_call(observer, "bridge.status", {}))
        self.assertTrue(status.get("writes_halted"), status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)

        self.harness_command("release-operation")
        thread.join(timeout=10.0)
        self.assertFalse(thread.is_alive(), "late F08 helper call did not return after releasing the fake adapter")
        kind, result = response_queue.get_nowait()
        if kind == "exception":
            raise result
        late = self.get_operation(observer, arguments["operation_id"])
        self.assertEqual(late.get("state"), "UNKNOWN", late)
        self.assertEqual(late.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", late)
        late_result = late.get("late_completion", {})
        self.assertEqual(late_result.get("mutation_state"), "APPLIED", late)
        self.assertIsNot(late_result.get("verified"), True, late)
        final_status = successful_helper_result(self, self.helper_call(observer, "bridge.status", {}))
        self.assertTrue(final_status.get("writes_halted"), final_status)
        self.assertEqual(final_status.get("ownership_state"), "UNKNOWN_FROZEN", final_status)
        record("F08-offset-late-result-keeps-unknown", {
            "operation_state": late["state"], "late_mutation_state": late_result.get("mutation_state"),
            "writes_halted": final_status["writes_halted"], "late_completion": late_result,
        })


def load_tests(loader, standard_tests, pattern):
    """Run only F08 methods; inherited legacy tests remain out of this delta."""
    suite = unittest.TestSuite()
    for test_name in sorted(name for name in F08SketchOffsetLifecycleTests.__dict__ if name.startswith("test_")):
        suite.addTest(F08SketchOffsetLifecycleTests(test_name))
    return suite


if __name__ == "__main__":
    unittest.main()
