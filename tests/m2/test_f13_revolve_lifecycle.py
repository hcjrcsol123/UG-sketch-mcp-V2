"""F13 command lifecycle checks on the NX-free M2 STA fixture.

The fixture exercises bridge ownership, operation records, execution dispatch,
and explicit verification routing. Its model is synthetic and is not evidence
of NX Revolve geometry or native builder behavior.
"""

from __future__ import annotations

import unittest
import uuid

from evidence_recorder import record
import test_m2_bridge as _m2
from test_m2_bridge import call_tool, successful_helper_result, successful_tool_result
from test_m3_bridge import M3BridgeSubsystemTests
from test_mcp_stdio import extract_structured_result


class F13RevolveLifecycleTests(M3BridgeSubsystemTests):
    @classmethod
    def setUpClass(cls):
        # Build and run only against the existing NX-free M2 STA test fixture.
        _m2.M2BridgeSubsystemTests.setUpClass.__func__(cls)

    def _takeover_with_revolve_fixture(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        sketch = self.create_sketch(helper, owner, epoch, [
            {"id": "f13-torus-profile", "type": "circle", "center_mm": [25.0, 0.0], "diameter_mm": 10.0},
        ])
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        return ready, helper, host, owner, epoch, session["revision_id"], sketch["result"]["sketch_id"]

    @staticmethod
    def _prepare_arguments(owner, epoch, revision_id, sketch_id, *, operation_id=None, **overrides):
        arguments = {
            "operation_id": operation_id or "f13-revolve-prepare-" + uuid.uuid4().hex,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "revision_id": revision_id,
            "sketch_id": sketch_id,
            "mode": "create",
            "axis_point_mm": [0.0, 0.0, 0.0],
            "axis_direction": [0.0, 1.0, 0.0],
            "start_angle_deg": 0.0,
            "end_angle_deg": 90.0,
        }
        arguments.update(overrides)
        return arguments

    def _prepare_revolve(self, helper, host, owner, epoch, revision_id, sketch_id, **overrides):
        arguments = self._prepare_arguments(owner, epoch, revision_id, sketch_id, **overrides)
        response = call_tool(host, "nx_prepare_revolve", arguments)
        if response.get("result", {}).get("isError") is True:
            stderr = b"".join(host.drain_stderr()).decode("utf-8", errors="replace")
            self.fail(f"nx_prepare_revolve failed: {response!r}; host stderr: {stderr}")
        accepted = successful_tool_result(self, response)
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, accepted)
        prepared = self.wait_operation(helper, arguments["operation_id"])
        self.assertEqual(prepared.get("state"), "SUCCEEDED", prepared)
        plan = prepared.get("result", {}).get("plan", {})
        self.assertEqual(plan.get("kind"), "create_revolve", plan)
        self.assertEqual(plan.get("profile_output"), "solid", plan)
        return arguments, plan

    def _execute_revolve(self, helper, host, owner, epoch, plan_id, *, operation_id=None):
        operation_id = operation_id or "f13-revolve-execute-" + uuid.uuid4().hex
        arguments = {
            "operation_id": operation_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": plan_id,
        }
        accepted = successful_tool_result(self, call_tool(host, "nx_execute_plan", arguments))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, accepted)
        return arguments, self.wait_operation(helper, operation_id)

    def test_execute_once_same_id_queries_and_explicit_fresh_verify_do_not_replay(self):
        _, helper, host, owner, epoch, revision_id, sketch_id = self._takeover_with_revolve_fixture()
        _, plan = self._prepare_revolve(helper, host, owner, epoch, revision_id, sketch_id)
        counts_before = self.fake_counts()

        execute_arguments, executed = self._execute_revolve(helper, host, owner, epoch, plan["plan_id"])
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
        execution_result = executed.get("result", {})
        self.assertEqual(execution_result.get("kind"), "create_revolve", executed)
        self.assertEqual(execution_result.get("mutation_state"), "APPLIED", executed)
        self.assertEqual(execution_result.get("plan_id"), plan["plan_id"], executed)
        counts_after_execute = self.fake_counts()
        self.assertEqual(counts_after_execute["m3_execution_calls"], counts_before["m3_execution_calls"] + 1)
        self.assertEqual(counts_after_execute["m3_mutation_count"], counts_before["m3_mutation_count"] + 1)

        repeated_execute = successful_tool_result(
            self, call_tool(host, "nx_execute_plan", execute_arguments),
        )
        self.assertEqual(repeated_execute.get("state"), "SUCCEEDED", repeated_execute)
        queried = self.get_operation(helper, execute_arguments["operation_id"])
        self.assertEqual(queried.get("state"), "SUCCEEDED", queried)
        self.assertEqual(queried.get("result"), execution_result, queried)
        counts_after_replay_and_get = self.fake_counts()
        self.assertEqual(counts_after_replay_and_get["m3_execution_calls"], counts_after_execute["m3_execution_calls"])
        self.assertEqual(counts_after_replay_and_get["m3_mutation_count"], counts_after_execute["m3_mutation_count"])

        verify_arguments = {
            "operation_id": "f13-revolve-verify-" + uuid.uuid4().hex,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "execute_operation_id": execute_arguments["operation_id"],
        }
        verify_accepted = successful_tool_result(self, call_tool(host, "nx_verify_part", verify_arguments))
        self.assertIn(verify_accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, verify_accepted)
        verified = self.wait_operation(helper, verify_arguments["operation_id"])
        self.assertEqual(verified.get("state"), "SUCCEEDED", verified)
        verification_result = verified.get("result", {})
        self.assertIs(verification_result.get("verified"), True, verified)
        self.assertEqual(verification_result.get("execute_operation_id"), execute_arguments["operation_id"], verified)
        checks = verification_result.get("checks", {})
        for name in ("geometry", "constraints", "history", "bodies", "non_target_bodies"):
            self.assertEqual(checks.get(name, {}).get("status"), "VERIFIED", verified)
        counts_after_fresh_verify = self.fake_counts()
        self.assertEqual(
            counts_after_fresh_verify["m3_verification_calls"],
            counts_after_replay_and_get["m3_verification_calls"] + 1,
            "nx_verify_part must dispatch a fresh helper verification read",
        )

        repeated_verify = successful_tool_result(
            self, call_tool(host, "nx_verify_part", verify_arguments),
        )
        self.assertEqual(repeated_verify.get("state"), "SUCCEEDED", repeated_verify)
        queried_verify = self.get_operation(helper, verify_arguments["operation_id"])
        self.assertEqual(queried_verify.get("state"), "SUCCEEDED", queried_verify)
        counts_after_verify_replay = self.fake_counts()
        self.assertEqual(
            counts_after_verify_replay["m3_verification_calls"],
            counts_after_fresh_verify["m3_verification_calls"],
            "same-ID verify and operation.get must return the operation record without another adapter call",
        )

    def test_known_rollback_is_queryable_and_does_not_freeze_future_writes(self):
        _, helper, host, owner, epoch, revision_id, sketch_id = self._takeover_with_revolve_fixture()
        _, plan = self._prepare_revolve(helper, host, owner, epoch, revision_id, sketch_id)
        counts_before = self.fake_counts()
        self.harness_command("rollback-after-write")

        execute_arguments, rolled_back = self._execute_revolve(helper, host, owner, epoch, plan["plan_id"])
        self.assertEqual(rolled_back.get("state"), "FAILED", rolled_back)
        self.assertEqual(rolled_back.get("error", {}).get("code"), "EDIT_NOT_VERIFIED", rolled_back)
        self.assertEqual(rolled_back.get("result", {}).get("mutation_state"), "ROLLED_BACK", rolled_back)
        self.assertIs(rolled_back.get("result", {}).get("changed"), False, rolled_back)
        rollback_revision = rolled_back.get("result", {}).get("revision_id")
        self.assertGreater(int(rollback_revision), int(revision_id), rolled_back)
        session_after_rollback = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        self.assertEqual(session_after_rollback.get("revision_id"), rollback_revision, session_after_rollback)
        self.assertEqual(self.get_operation(helper, execute_arguments["operation_id"]), rolled_back)

        counts_after = self.fake_counts()
        self.assertEqual(counts_after["m3_execution_calls"], counts_before["m3_execution_calls"] + 1)
        self.assertEqual(counts_after["m3_mutation_count"], counts_before["m3_mutation_count"])
        self.assertEqual(counts_after["rollback_count"], counts_before["rollback_count"] + 1)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), False, status)
        self.assertIs(status.get("model_write_enabled"), True, status)

    def test_known_rollback_advances_revision_and_rejects_an_old_prepared_plan(self):
        _, helper, host, owner, epoch, revision_id, sketch_id = self._takeover_with_revolve_fixture()
        before = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        old_revision = before["revision_id"]

        _, rollback_plan = self._prepare_revolve(
            helper, host, owner, epoch, old_revision, sketch_id,
            operation_id="f13-revolve-rollback-plan-" + uuid.uuid4().hex,
        )
        _, stale_plan = self._prepare_revolve(
            helper, host, owner, epoch, old_revision, sketch_id,
            operation_id="f13-revolve-stale-plan-" + uuid.uuid4().hex,
        )
        self.assertNotEqual(rollback_plan["plan_id"], stale_plan["plan_id"])
        counts_before_rollback = self.fake_counts()

        self.harness_command("rollback-after-write")
        execute_arguments, rolled_back = self._execute_revolve(
            helper, host, owner, epoch, rollback_plan["plan_id"],
            operation_id="f13-revolve-rollback-execute-" + uuid.uuid4().hex,
        )
        self.assertEqual(rolled_back.get("state"), "FAILED", rolled_back)
        self.assertEqual(rolled_back.get("error", {}).get("code"), "EDIT_NOT_VERIFIED", rolled_back)
        rollback_result = rolled_back.get("result", {})
        self.assertEqual(rollback_result.get("mutation_state"), "ROLLED_BACK", rolled_back)
        self.assertIs(rollback_result.get("changed"), False, rolled_back)
        new_revision = rollback_result.get("revision_id")
        self.assertIsInstance(new_revision, str, rolled_back)
        self.assertGreater(int(new_revision), int(old_revision), rolled_back)

        after_rollback = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        self.assertEqual(after_rollback.get("revision_id"), new_revision, after_rollback)
        counts_after_rollback = self.fake_counts()
        self.assertEqual(
            counts_after_rollback["m3_execution_calls"],
            counts_before_rollback["m3_execution_calls"] + 1,
            counts_after_rollback,
        )
        self.assertEqual(
            counts_after_rollback["rollback_count"],
            counts_before_rollback["rollback_count"] + 1,
            counts_after_rollback,
        )
        self.assertEqual(
            counts_after_rollback["m3_mutation_count"],
            counts_before_rollback["m3_mutation_count"],
            counts_after_rollback,
        )

        stale_execute_arguments, stale_execution = self._execute_revolve(
            helper, host, owner, epoch, stale_plan["plan_id"],
            operation_id="f13-revolve-stale-execute-" + uuid.uuid4().hex,
        )
        self.assertEqual(stale_execution.get("state"), "FAILED", stale_execution)
        self.assertEqual(stale_execution.get("error", {}).get("code"), "STALE_PLAN", stale_execution)
        counts_after_stale_rejection = self.fake_counts()
        self.assertEqual(counts_after_stale_rejection["m3_execution_calls"], counts_after_rollback["m3_execution_calls"])
        self.assertEqual(counts_after_stale_rejection["rollback_count"], counts_after_rollback["rollback_count"])
        self.assertEqual(counts_after_stale_rejection["m3_mutation_count"], counts_after_rollback["m3_mutation_count"])

        same_id_replay = successful_helper_result(
            self,
            self.helper_call(helper, "operation.execute_plan", execute_arguments),
        )
        queried = self.get_operation(helper, execute_arguments["operation_id"])
        self.assertEqual(same_id_replay, rolled_back)
        self.assertEqual(queried, rolled_back)
        after_replay = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        self.assertEqual(after_replay.get("revision_id"), new_revision, after_replay)
        counts_after_replay = self.fake_counts()
        self.assertEqual(counts_after_replay["m3_execution_calls"], counts_after_stale_rejection["m3_execution_calls"])
        self.assertEqual(counts_after_replay["rollback_count"], counts_after_stale_rejection["rollback_count"])
        self.assertEqual(counts_after_replay["m3_mutation_count"], counts_after_stale_rejection["m3_mutation_count"])

        record("F13-known-rollback-revision-monotonicity", {
            "old_revision": old_revision,
            "rollback_result_revision": new_revision,
            "session_revision_after_rollback_and_replay": after_replay["revision_id"],
            "stale_plan_error": stale_execution["error"]["code"],
            "rollback_execute_operation_id": execute_arguments["operation_id"],
            "stale_execute_operation_id": stale_execute_arguments["operation_id"],
            "m3_execution_calls_for_rollback_and_stale_attempt": counts_after_replay["m3_execution_calls"] - counts_before_rollback["m3_execution_calls"],
            "rollback_count_for_rollback_and_stale_attempt": counts_after_replay["rollback_count"] - counts_before_rollback["rollback_count"],
        })

    def test_unknown_outcome_is_query_only_and_rejects_fresh_prepare_or_execute(self):
        _, helper, host, owner, epoch, revision_id, sketch_id = self._takeover_with_revolve_fixture()
        prepare_arguments, plan = self._prepare_revolve(helper, host, owner, epoch, revision_id, sketch_id)
        counts_before = self.fake_counts()
        self.harness_command("unknown-after-mutation")

        execute_arguments, unknown = self._execute_revolve(helper, host, owner, epoch, plan["plan_id"])
        self.assertEqual(unknown.get("state"), "UNKNOWN", unknown)
        self.assertEqual(unknown.get("result", {}).get("mutation_state"), "UNKNOWN", unknown)
        self.assertIsNot(unknown.get("result", {}).get("verified"), True, unknown)
        counts_after_unknown = self.fake_counts()
        self.assertEqual(counts_after_unknown["m3_execution_calls"], counts_before["m3_execution_calls"] + 1)
        self.assertEqual(counts_after_unknown["m3_mutation_count"], counts_before["m3_mutation_count"] + 1)

        query = self.get_operation(helper, execute_arguments["operation_id"])
        self.assertEqual(query.get("state"), "UNKNOWN", query)
        same_id = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", execute_arguments))
        self.assertEqual(same_id.get("state"), "UNKNOWN", same_id)
        self.assertEqual(same_id, query)

        new_execute = dict(execute_arguments, operation_id="f13-revolve-no-retry-" + uuid.uuid4().hex)
        blocked_execute = self.helper_call(helper, "operation.execute_plan", new_execute)
        self.assertEqual(blocked_execute.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked_execute)

        new_prepare = self._prepare_arguments(
            owner, epoch, revision_id, sketch_id,
            operation_id="f13-revolve-no-new-plan-" + uuid.uuid4().hex,
        )
        blocked_prepare = self.helper_call(helper, "operation.prepare_revolve", new_prepare)
        self.assertEqual(blocked_prepare.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked_prepare)
        counts_after_blocked_writes = self.fake_counts()
        self.assertEqual(counts_after_blocked_writes["m3_execution_calls"], counts_after_unknown["m3_execution_calls"])
        self.assertEqual(counts_after_blocked_writes["m3_revolve_prepare_calls"], counts_after_unknown["m3_revolve_prepare_calls"])
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertFalse(status.get("model_revolve_write_enabled"), status)
        self.assertEqual(self.get_operation(helper, prepare_arguments["operation_id"]).get("state"), "SUCCEEDED")


def load_tests(loader, tests, pattern):
    """Exclude inherited F01-F12/M3 fixture regressions from this F13 run."""
    suite = unittest.TestSuite()
    for name, member in F13RevolveLifecycleTests.__dict__.items():
        if name.startswith("test_") and callable(member):
            suite.addTest(F13RevolveLifecycleTests(name))
    return suite


if __name__ == "__main__":
    unittest.main()
