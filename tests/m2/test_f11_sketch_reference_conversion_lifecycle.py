"""F11 STA fake lifecycle checks; no NXOpen calls or NX process are used."""
from __future__ import annotations

import unittest
import uuid

import test_m2_bridge as _m2
from evidence_recorder import record
from test_m3_bridge import M3BridgeSubsystemTests, successful_helper_result


class F11SketchReferenceConversionLifecycleTests(M3BridgeSubsystemTests):
    @classmethod
    def setUpClass(cls):
        # Run only F11 methods from this module; shared M2 helpers are inherited.
        _m2.M2BridgeSubsystemTests.setUpClass.__func__(cls)

    @classmethod
    def tearDownClass(cls):
        _m2.M2BridgeSubsystemTests.tearDownClass.__func__(cls)

    def _fixture(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        created = self.create_sketch(helper, owner, epoch, [
            {"id": "f11-horizontal", "type": "line", "start_mm": [2.0, 1.0], "end_mm": [12.0, 1.0]},
            {"id": "f11-vertical", "type": "line", "start_mm": [15.0, 1.0], "end_mm": [15.0, 7.0]},
            {"id": "f11-circle", "type": "circle", "center_mm": [20.0, 3.0], "diameter_mm": 4.0},
            {"id": "f11-ellipse", "type": "ellipse", "center_mm": [30.0, 3.0],
             "major_radius_mm": 4.0, "minor_radius_mm": 2.0, "rotation_deg": 0.0},
        ])
        sketch_id = created["result"]["sketch_id"]
        sketch = self._inspect(helper, owner, epoch, sketch_id)
        by_name = {item["id"]: item for item in sketch["geometry"]}
        self.assertTrue({"f11-horizontal", "f11-vertical", "f11-circle", "f11-ellipse"} <= set(by_name), sketch)
        return ready, helper, host, owner, epoch, sketch_id

    def _session(self, helper, owner, epoch):
        return successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))

    def _inspect(self, helper, owner, epoch, sketch_id):
        response = successful_helper_result(self, self.helper_call(helper, "model.inspect_sketch", {
            "ownership_id": owner, "binding_epoch": epoch, "sketch_id": sketch_id,
        }))
        return response.get("sketch", response)

    def _request(self, helper, owner, epoch, sketch_id, object_ids, target_state,
                 *, operation_id=None):
        session = self._session(helper, owner, epoch)
        return {
            "operation_id": operation_id or ("f11-reference-" + uuid.uuid4().hex),
            "ownership_id": owner, "binding_epoch": epoch,
            "revision_id": session["revision_id"], "sketch_id": sketch_id,
            "object_ids": list(object_ids), "target_state": target_state,
        }

    @staticmethod
    def _geometry(sketch, geometry_id):
        return next(item for item in sketch["geometry"] if item.get("id") == geometry_id)

    @staticmethod
    def _dimension_for_geometry(sketch, geometry_id, dimension_type):
        geometry = next(item for item in sketch["geometry"] if item.get("id") == geometry_id)
        current_jid = geometry["journal_id"]
        return next(item for item in sketch["constraints"]
                    if item.get("constraint_type") == dimension_type and
                    any(str(reference).startswith(current_jid + "|")
                        for reference in item.get("geometry_refs", [])))

    def _prepare(self, helper, request):
        response = self.helper_call(helper, "operation.prepare_sketch_reference_conversion", request, timeout=10.0)
        return successful_helper_result(self, response)

    def _edit(self, helper, request):
        response = successful_helper_result(self, self.helper_call(
            helper, "operation.edit_sketch_reference_conversion", request, timeout=10.0))
        if response.get("state") not in {"SUCCEEDED", "FAILED", "UNKNOWN", "CANCELLED"}:
            response = self.wait_operation(helper, request["operation_id"], timeout=10.0)
        return response

    def test_prepare_is_read_only_and_selected_curve_dimension_converts_on_one_sta(self):
        ready, helper, host, owner, epoch, sketch_id = self._fixture()
        before = self._inspect(helper, owner, epoch, sketch_id)
        line = self._geometry(before, "f11-horizontal")
        dimension = self._dimension_for_geometry(before, "f11-horizontal", "HorizontalDim")
        request = self._request(helper, owner, epoch, sketch_id,
                                [line["journal_id"], dimension["journal_id"]], "reference")
        counts_before = self.fake_counts()
        prepared = self._prepare(helper, request)
        self.assertEqual(prepared.get("state"), "SUCCEEDED", prepared)
        plan = prepared.get("result", {}).get("plan", {})
        self.assertEqual(plan.get("kind"), "edit_sketch_reference_conversion", plan)
        self.assertEqual(plan.get("target_state"), "reference", plan)
        self.assertEqual([item.get("will_change") for item in plan.get("objects", [])], [True, True], plan)
        counts_after_prepare = self.fake_counts()
        self.assertEqual(counts_after_prepare["m3_reference_conversion_prepare_calls"],
                         counts_before["m3_reference_conversion_prepare_calls"] + 1, counts_after_prepare)
        self.assertEqual(counts_after_prepare["m3_mutation_count"], counts_before["m3_mutation_count"], counts_after_prepare)
        self.assertEqual(counts_after_prepare["m3_reference_conversion_execution_calls"],
                         counts_before["m3_reference_conversion_execution_calls"], counts_after_prepare)
        still_before = self._inspect(helper, owner, epoch, sketch_id)
        self.assertEqual(self._geometry(still_before, "f11-horizontal")["reference_status"], "Active", still_before)
        self.assertEqual(self._dimension_for_geometry(still_before, "f11-horizontal", "HorizontalDim")["dimension_state"],
                         "Driving", still_before)

        execute_id, _, executed = self.execute_plan(helper, owner, epoch, plan["plan_id"],
            operation_id="f11-execute-" + uuid.uuid4().hex)
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
        self.assertEqual(executed.get("result", {}).get("mutation_state"), "APPLIED", executed)
        verified = self.verify_plan(helper, host, owner, epoch, execute_id)
        self.assertIs(verified.get("result", {}).get("verified"), True, verified)
        checks = verified.get("result", {}).get("checks", {})
        expected_checks = {
            "reference_conversion_targets", "sketch_geometry", "sketch_constraints", "sketch_frames",
            "source_associations", "non_target_geometry", "history", "bodies",
            "sketch_solver_status", "profile_candidates", "dirty_state",
        }
        self.assertEqual(set(checks), expected_checks, checks)
        for name, check in checks.items():
            self.assertEqual(check.get("status"), "VERIFIED", {name: check})

        after = self._inspect(helper, owner, epoch, sketch_id)
        self.assertEqual(self._geometry(after, "f11-horizontal")["reference_status"], "Reference", after)
        self.assertEqual(self._geometry(after, "f11-vertical")["reference_status"], "Active", after)
        selected_dimension_after = self._dimension_for_geometry(after, "f11-horizontal", "HorizontalDim")
        self.assertEqual(selected_dimension_after["dimension_state"], "Reference", selected_dimension_after)
        self.assertTrue(selected_dimension_after["is_driving"] is False)
        counts_after = self.fake_counts()
        self.assertEqual(counts_after["m3_reference_conversion_thread_id"], int(ready["main_thread_id"]), counts_after)
        self.assertEqual(counts_after["m3_reference_conversion_thread_first_id"],
                         int(ready["main_thread_id"]), counts_after)
        self.assertEqual(counts_after["m3_reference_conversion_thread_mismatch"], 0, counts_after)
        self.assertEqual(counts_after["m3_reference_conversion_execution_calls"], 1, counts_after)
        self.assertEqual(counts_after["m3_reference_conversion_verification_calls"], 1, counts_after)
        record("F11-fake-prepare-execute-verify", {
            "execute_operation_id": execute_id, "target_state": "reference",
            "selected_current_jids": request["object_ids"], "checks": checks,
            "prepare_read_only": True, "native_nx_invoked": False,
            "adapter_sta_thread_matches_harness_main_thread": True,
        })

    def test_mixed_states_allow_noop_member_but_all_target_state_rejects_read_only(self):
        _, helper, host, owner, epoch, sketch_id = self._fixture()
        before = self._inspect(helper, owner, epoch, sketch_id)
        horizontal = self._geometry(before, "f11-horizontal")
        vertical = self._geometry(before, "f11-vertical")
        request = self._request(helper, owner, epoch, sketch_id,
                                [horizontal["journal_id"]], "reference")
        first = self._edit(helper, request)
        self.assertEqual(first.get("state"), "SUCCEEDED", first)
        self.assertIs(first.get("result", {}).get("verified"), True, first)
        mixed = self._request(helper, owner, epoch, sketch_id,
                              [horizontal["journal_id"], vertical["journal_id"]], "reference")
        before_mixed = self._inspect(helper, owner, epoch, sketch_id)
        counts_before_mixed = self.fake_counts()
        prepared = self._prepare(helper, mixed)
        plan = prepared.get("result", {}).get("plan", {})
        self.assertEqual(prepared.get("state"), "SUCCEEDED", prepared)
        will_change = {item["object_id"]: item["will_change"] for item in plan["objects"]}
        self.assertIs(will_change[horizontal["journal_id"]], False, plan)
        self.assertIs(will_change[vertical["journal_id"]], True, plan)
        self.assertEqual(self.fake_counts()["m3_mutation_count"], counts_before_mixed["m3_mutation_count"],
                         "mixed prepare must be read-only")
        execute_id, _, execution = self.execute_plan(helper, owner, epoch, plan["plan_id"],
            operation_id="f11-mixed-execute-" + uuid.uuid4().hex)
        self.assertEqual(execution.get("state"), "SUCCEEDED", execution)
        verified = self.verify_plan(helper, host, owner, epoch, execute_id)
        self.assertIs(verified.get("result", {}).get("verified"), True, verified)
        after_mixed = self._inspect(helper, owner, epoch, sketch_id)
        self.assertEqual(self._geometry(after_mixed, "f11-horizontal")["reference_status"], "Reference", after_mixed)
        self.assertEqual(self._geometry(after_mixed, "f11-vertical")["reference_status"], "Reference", after_mixed)
        self.assertEqual(before_mixed["geometry"][0]["start_mm"], after_mixed["geometry"][0]["start_mm"])

        current = self._inspect(helper, owner, epoch, sketch_id)
        all_target = self._request(helper, owner, epoch, sketch_id,
                                   [horizontal["journal_id"], vertical["journal_id"]], "reference")
        counts_before_reject = self.fake_counts()
        rejected = successful_helper_result(self, self.helper_call(
            helper, "operation.prepare_sketch_reference_conversion", all_target))
        self.assertEqual(rejected.get("state"), "FAILED", rejected)
        self.assertEqual(rejected.get("error", {}).get("code"), "INVALID_ARGUMENT", rejected)
        counts_after_reject = self.fake_counts()
        self.assertEqual(counts_after_reject["m3_mutation_count"], counts_before_reject["m3_mutation_count"], counts_after_reject)
        self.assertEqual(self._inspect(helper, owner, epoch, sketch_id)["geometry"], current["geometry"])
        record("F11-fake-mixed-and-all-target", {
            "mixed_object_ids": mixed["object_ids"], "will_change": will_change,
            "all_target_error": rejected["error"]["code"], "native_nx_invoked": False,
        })

    def test_same_operation_id_replay_is_exactly_once(self):
        _, helper, _, owner, epoch, sketch_id = self._fixture()
        sketch = self._inspect(helper, owner, epoch, sketch_id)
        line = self._geometry(sketch, "f11-horizontal")
        dimension = self._dimension_for_geometry(sketch, "f11-horizontal", "HorizontalDim")
        request = self._request(helper, owner, epoch, sketch_id,
                                [line["journal_id"], dimension["journal_id"]], "reference",
                                operation_id="f11-idempotency-" + uuid.uuid4().hex)
        before = self.fake_counts()
        first = self._edit(helper, request)
        self.assertEqual(first.get("state"), "SUCCEEDED", first)
        self.assertIs(first.get("result", {}).get("verified"), True, first)
        after_first = self.fake_counts()
        replay = successful_helper_result(self, self.helper_call(
            helper, "operation.edit_sketch_reference_conversion", request, timeout=10.0))
        self.assertEqual(replay.get("state"), "SUCCEEDED", replay)
        self.assertEqual(replay.get("result"), first.get("result"), replay)
        after_replay = self.fake_counts()
        for key in ("m3_mutation_count", "m3_reference_conversion_execution_calls",
                    "m3_reference_conversion_verification_calls"):
            self.assertEqual(after_first[key], after_replay[key], after_replay)
        self.assertEqual(after_first["m3_reference_conversion_execution_calls"],
                         before["m3_reference_conversion_execution_calls"] + 1, after_first)
        record("F11-fake-same-id-idempotency", {
            "operation_id": request["operation_id"], "same_terminal_result": True,
            "adapter_counts_unchanged_on_replay": True, "native_nx_invoked": False,
        })

    def test_stale_plan_rejects_before_adapter_execution(self):
        _, helper, _, owner, epoch, sketch_id = self._fixture()
        sketch = self._inspect(helper, owner, epoch, sketch_id)
        line = self._geometry(sketch, "f11-horizontal")
        request = self._request(helper, owner, epoch, sketch_id, [line["journal_id"]], "reference")
        plan = self._prepare(helper, request).get("result", {}).get("plan", {})
        before = self._inspect(helper, owner, epoch, sketch_id)
        self.create_sketch(helper, owner, epoch, [
            {"id": "f11-revision-advance", "type": "line", "start_mm": [40.0, 0.0], "end_mm": [45.0, 0.0]},
        ])
        counts_before = self.fake_counts()
        _, _, stale = self.execute_plan(helper, owner, epoch, plan["plan_id"],
            operation_id="f11-stale-execute-" + uuid.uuid4().hex)
        self.assertNotEqual(stale.get("state"), "SUCCEEDED", stale)
        self.assertIn(stale.get("error", {}).get("code"), {"STALE_PLAN", "STALE_REVISION"}, stale)
        after = self._inspect(helper, owner, epoch, sketch_id)
        self.assertEqual(before["geometry"], after["geometry"])
        counts_after = self.fake_counts()
        self.assertEqual(counts_after["m3_reference_conversion_execution_calls"],
                         counts_before["m3_reference_conversion_execution_calls"], counts_after)
        self.assertEqual(counts_after["m3_mutation_count"], counts_before["m3_mutation_count"], counts_after)
        record("F11-fake-stale-plan", {
            "error": stale.get("error"), "adapter_execution_not_called": True,
            "geometry_unchanged": True, "native_nx_invoked": False,
        })

    def test_fake_rollback_restores_curve_and_dimension_state(self):
        _, helper, _, owner, epoch, sketch_id = self._fixture()
        before = self._inspect(helper, owner, epoch, sketch_id)
        line = self._geometry(before, "f11-horizontal")
        dimension = self._dimension_for_geometry(before, "f11-horizontal", "HorizontalDim")
        request = self._request(helper, owner, epoch, sketch_id,
                                [line["journal_id"], dimension["journal_id"]], "reference")
        plan = self._prepare(helper, request).get("result", {}).get("plan", {})
        counts_before = self.fake_counts()
        self.harness_command("rollback-after-write")
        _, _, failed = self.execute_plan(helper, owner, epoch, plan["plan_id"],
            operation_id="f11-fake-rollback-" + uuid.uuid4().hex)
        self.assertNotEqual(failed.get("state"), "SUCCEEDED", failed)
        self.assertEqual(failed.get("result", {}).get("mutation_state"), "ROLLED_BACK", failed)
        after = self._inspect(helper, owner, epoch, sketch_id)
        self.assertEqual(before["geometry"], after["geometry"])
        self.assertEqual(before["constraints"], after["constraints"])
        counts_after = self.fake_counts()
        self.assertEqual(counts_after["rollback_count"], counts_before["rollback_count"] + 1, counts_after)
        self.assertEqual(counts_after["m3_mutation_count"], counts_before["m3_mutation_count"], counts_after)
        record("F11-fake-rollback", {
            "mutation_state": failed.get("result", {}).get("mutation_state"),
            "curve_dimension_state_restored": True, "native_undo_exercised": False,
            "native_nx_invoked": False,
        })

    def test_unknown_verification_freezes_writes_and_only_same_id_get_is_used(self):
        _, helper, _, owner, epoch, sketch_id = self._fixture()
        sketch = self._inspect(helper, owner, epoch, sketch_id)
        line = self._geometry(sketch, "f11-horizontal")
        request = self._request(helper, owner, epoch, sketch_id, [line["journal_id"]], "reference")
        self.harness_command("null-next-m3-verification")
        operation = self._edit(helper, request)
        self.assertEqual(operation.get("state"), "UNKNOWN", operation)
        self.assertEqual(operation.get("result", {}).get("mutation_state"), "APPLIED", operation)
        self.assertIs(operation.get("result", {}).get("verified"), False, operation)
        queried = self.get_operation(helper, request["operation_id"])
        self.assertEqual(queried.get("state"), "UNKNOWN", queried)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        before_blocked = self.fake_counts()
        blocked_request = dict(request)
        blocked_request["operation_id"] = "f11-write-after-unknown-" + uuid.uuid4().hex
        blocked = self.helper_call(helper, "operation.edit_sketch_reference_conversion", blocked_request)
        self.assertIn(blocked.get("error", {}).get("code"),
                      {"OPERATION_OUTCOME_UNKNOWN", "CLIENT_OUTCOME_UNKNOWN"}, blocked)
        after_blocked = self.fake_counts()
        self.assertEqual(after_blocked["m3_reference_conversion_execution_calls"],
                         before_blocked["m3_reference_conversion_execution_calls"], after_blocked)
        record("F11-fake-unknown-freeze", {
            "operation_id": request["operation_id"], "same_id_get_state": queried.get("state"),
            "writes_halted": status["writes_halted"], "blocked_new_write_code": blocked["error"]["code"],
            "replayed": False, "native_nx_invoked": False,
        })

    def test_selected_ellipse_and_non_linear_dimension_are_rejected_read_only(self):
        _, helper, _, owner, epoch, sketch_id = self._fixture()
        sketch = self._inspect(helper, owner, epoch, sketch_id)
        ellipse = self._geometry(sketch, "f11-ellipse")
        radial = next(item for item in sketch["constraints"] if item.get("constraint_type") == "DiameterDim")
        baseline = sketch["geometry"]
        counts_before = self.fake_counts()
        ellipse_request = self._request(helper, owner, epoch, sketch_id, [ellipse["journal_id"]], "reference")
        ellipse_result = successful_helper_result(self, self.helper_call(
            helper, "operation.prepare_sketch_reference_conversion", ellipse_request))
        self.assertEqual(ellipse_result.get("state"), "FAILED", ellipse_result)
        self.assertEqual(ellipse_result.get("error", {}).get("code"), "UNSUPPORTED_GEOMETRY", ellipse_result)
        radial_request = self._request(helper, owner, epoch, sketch_id, [radial["journal_id"]], "reference")
        radial_result = successful_helper_result(self, self.helper_call(
            helper, "operation.prepare_sketch_reference_conversion", radial_request))
        self.assertEqual(radial_result.get("state"), "FAILED", radial_result)
        self.assertEqual(radial_result.get("error", {}).get("code"), "UNSUPPORTED_DIMENSION", radial_result)
        after = self._inspect(helper, owner, epoch, sketch_id)
        self.assertEqual(baseline, after["geometry"])
        counts_after = self.fake_counts()
        self.assertEqual(counts_after["m3_mutation_count"], counts_before["m3_mutation_count"], counts_after)
        self.assertEqual(counts_after["m3_reference_conversion_execution_calls"],
                         counts_before["m3_reference_conversion_execution_calls"], counts_after)
        record("F11-fake-unsupported-selection", {
            "ellipse_error": ellipse_result["error"]["code"],
            "radial_dimension_error": radial_result["error"]["code"],
            "geometry_preserved": True, "native_nx_invoked": False,
        })


def _f11_test_suite():
    selected = []
    for name in sorted(dir(F11SketchReferenceConversionLifecycleTests)):
        method = getattr(F11SketchReferenceConversionLifecycleTests, name)
        if name.startswith("test_") and getattr(method, "__module__", None) == __name__:
            selected.append(F11SketchReferenceConversionLifecycleTests(name))
    return unittest.TestSuite(selected)


if __name__ == "__main__":
    unittest.TextTestRunner(verbosity=2).run(_f11_test_suite())
