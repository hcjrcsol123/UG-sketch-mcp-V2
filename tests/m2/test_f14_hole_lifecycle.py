"""F14 fake-STA lifecycle evidence only; this does not exercise NX geometry."""
import os
from pathlib import Path
import unittest
import uuid

import test_m2_bridge as _m2
from test_m2_bridge import call_tool, successful_helper_result, successful_tool_result
from test_m3_bridge import M3BridgeSubsystemTests


class F14HoleLifecycleTests(M3BridgeSubsystemTests):
    @classmethod
    def setUpClass(cls):
        candidate = os.environ.get("F14_TEST_BUILD_DIR")
        if candidate:
            _m2.BRIDGE_DLL = Path(candidate).resolve() / "M2Bridge.dll"
            _m2.HELPER_EXE = Path(candidate).resolve() / "M2JsonlHelper.exe"
        _m2.M2BridgeSubsystemTests.setUpClass.__func__(cls)

    def fixture(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        sketch = self.create_sketch(helper, owner, epoch, [
            {"id": "f14-profile", "type": "circle", "center_mm": [0.0, 0.0], "diameter_mm": 20.0}])
        _, _, prepared = self.prepare_extrude(helper, owner, epoch, sketch["result"]["sketch_id"],
                                              mode="create", direction="positive", extent="finite", depth_mm=20.0)
        _, _, executed = self.execute_plan(helper, owner, epoch, prepared["result"]["plan"]["plan_id"])
        self.assertEqual(executed["state"], "SUCCEEDED", executed)
        body = executed["result"]["body_id"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch}))
        return helper, host, owner, epoch, session["revision_id"], body

    @staticmethod
    def args(owner, epoch, revision, body):
        return {"operation_id": "f14-prepare-" + uuid.uuid4().hex, "ownership_id": owner,
                "binding_epoch": epoch, "revision_id": revision, "target_body_id": body,
                "position_mm": [0, 0, 20], "direction": [0, 0, -1], "kind": "simple",
                "depth_mode": "blind", "diameter_mm": 4, "hole_depth_mm": 10}

    def prepare(self, helper, host, args):
        successful_tool_result(self, call_tool(host, "nx_prepare_hole", args))
        return self.wait_operation(helper, args["operation_id"])

    def execute(self, helper, host, owner, epoch, plan):
        args = {"operation_id": "f14-execute-" + uuid.uuid4().hex, "ownership_id": owner,
                "binding_epoch": epoch, "plan_id": plan}
        successful_tool_result(self, call_tool(host, "nx_execute_plan", args))
        return args, self.wait_operation(helper, args["operation_id"])

    def test_same_id_does_not_reexecute_and_fresh_verify_dispatches_once(self):
        helper, host, owner, epoch, revision, body = self.fixture()
        before = self.fake_counts()
        prepared = self.prepare(helper, host, self.args(owner, epoch, revision, body))
        self.assertEqual(prepared["state"], "SUCCEEDED", prepared)
        self.assertEqual(self.fake_counts()["m3_hole_prepare_calls"], before["m3_hole_prepare_calls"] + 1)
        args, executed = self.execute(helper, host, owner, epoch, prepared["result"]["plan"]["plan_id"])
        self.assertEqual(executed["state"], "SUCCEEDED", executed)
        counts = self.fake_counts()
        self.assertEqual(counts["m3_execution_calls"], before["m3_execution_calls"] + 1)
        successful_tool_result(self, call_tool(host, "nx_execute_plan", args))
        self.assertEqual(self.get_operation(helper, args["operation_id"]), executed)
        self.assertEqual(self.fake_counts()["m3_execution_calls"], counts["m3_execution_calls"])
        verified = self.verify_plan(helper, host, owner, epoch, args["operation_id"])
        self.assertTrue(verified["result"]["verified"])
        self.assertEqual(self.fake_counts()["m3_verification_calls"], counts["m3_verification_calls"] + 1)

    def test_unknown_execute_is_query_only_and_freezes_later_writes(self):
        helper, host, owner, epoch, revision, body = self.fixture()
        prepared = self.prepare(helper, host, self.args(owner, epoch, revision, body))
        self.assertEqual(prepared["state"], "SUCCEEDED", prepared)
        self.harness_command("unknown-after-mutation")
        args, unknown = self.execute(helper, host, owner, epoch, prepared["result"]["plan"]["plan_id"])
        self.assertEqual(unknown["state"], "UNKNOWN", unknown)
        counts = self.fake_counts()
        self.assertEqual(self.get_operation(helper, args["operation_id"]), unknown)
        repeated = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", args))
        self.assertEqual(repeated, unknown)
        blocked = self.helper_call(helper, "operation.prepare_hole", self.args(owner, epoch, revision, body))
        self.assertEqual(blocked.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked)
        self.assertEqual(self.fake_counts()["m3_execution_calls"], counts["m3_execution_calls"])
        self.assertEqual(self.fake_counts()["m3_hole_prepare_calls"], counts["m3_hole_prepare_calls"])

    def test_unknown_prepare_is_query_only_and_freezes_later_writes(self):
        helper, host, owner, epoch, revision, body = self.fixture()
        args = self.args(owner, epoch, revision, body)
        before = self.fake_counts()
        self.harness_command("unknown-next-m3-prepare")
        unknown = successful_helper_result(self, self.helper_call(helper, "operation.prepare_hole", args))
        self.assertEqual(unknown["state"], "UNKNOWN", unknown)
        self.assertEqual(unknown["error"]["code"], "M3_ANALYSIS_STATE_UNKNOWN", unknown)
        self.assertEqual(self.get_operation(helper, args["operation_id"]), unknown)
        self.assertEqual(successful_helper_result(self, self.helper_call(helper, "operation.prepare_hole", args)), unknown)
        blocked = self.helper_call(helper, "operation.prepare_hole", self.args(owner, epoch, revision, body))
        self.assertEqual(blocked.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked)
        after = self.fake_counts()
        self.assertEqual(after["m3_hole_prepare_calls"], before["m3_hole_prepare_calls"] + 1)
        self.assertEqual(after["m3_mutation_count"], before["m3_mutation_count"])
        self.assertEqual(after["revision_id"], before["revision_id"])

    def test_unknown_fresh_verify_is_query_only_and_freezes_later_writes(self):
        helper, host, owner, epoch, revision, body = self.fixture()
        prepared = self.prepare(helper, host, self.args(owner, epoch, revision, body))
        args, executed = self.execute(helper, host, owner, epoch, prepared["result"]["plan"]["plan_id"])
        self.assertEqual(executed["state"], "SUCCEEDED", executed)
        before = self.fake_counts()
        self.harness_command("unknown-next-m3-verification")
        verify = {"operation_id": "f14-verify-" + uuid.uuid4().hex, "ownership_id": owner,
                  "binding_epoch": epoch, "execute_operation_id": args["operation_id"]}
        successful_helper_result(self, self.helper_call(helper, "operation.verify_part", verify))
        unknown = self.wait_operation(helper, verify["operation_id"])
        self.assertEqual(unknown["state"], "UNKNOWN", unknown)
        self.assertEqual(unknown["error"]["code"], "M3_ANALYSIS_STATE_UNKNOWN", unknown)
        self.assertEqual(self.get_operation(helper, verify["operation_id"]), unknown)
        self.assertEqual(successful_helper_result(self, self.helper_call(helper, "operation.verify_part", verify)), unknown)
        blocked = self.helper_call(helper, "operation.prepare_hole", self.args(owner, epoch, revision, body))
        self.assertEqual(blocked.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked)
        after = self.fake_counts()
        self.assertEqual(after["m3_verification_calls"], before["m3_verification_calls"] + 1)
        self.assertEqual(after["m3_mutation_count"], before["m3_mutation_count"])


if __name__ == "__main__":
    unittest.main()
