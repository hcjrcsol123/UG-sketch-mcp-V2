"""Targeted M3 bridge/helper/stdio checks against the NX-free STA fixture."""

from __future__ import annotations

import tempfile
import time
import unittest
import uuid
from pathlib import Path

from evidence_recorder import record
import test_m2_bridge as _m2
from test_m2_bridge import (
    call_tool,
    successful_helper_result,
    successful_tool_result,
)


class M3BridgeSubsystemTests(unittest.TestCase):
    """Reuse the M2 process helpers while keeping the M3 cases isolated."""

    @classmethod
    def setUpClass(cls):
        _m2.M2BridgeSubsystemTests.setUpClass.__func__(cls)

    @classmethod
    def tearDownClass(cls):
        _m2.M2BridgeSubsystemTests.tearDownClass.__func__(cls)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="nx-m3-run-")
        self.temp_path = Path(self.temp.name)
        self.pipe_name = "nx-m3-test-" + uuid.uuid4().hex
        self.token_file = self.temp_path / "server-token.txt"
        self.wrong_token_file = self.temp_path / "wrong-token.txt"
        self.harness = None
        self.addCleanup(self.temp.cleanup)

    start_harness = _m2.M2BridgeSubsystemTests.start_harness
    start_helper = _m2.M2BridgeSubsystemTests.start_helper
    start_host = _m2.M2BridgeSubsystemTests.start_host
    helper_call = _m2.M2BridgeSubsystemTests.helper_call
    harness_command = _m2.M2BridgeSubsystemTests.harness_command
    fake_counts = _m2.M2BridgeSubsystemTests.fake_counts
    get_operation = _m2.M2BridgeSubsystemTests.get_operation
    wait_operation = _m2.M2BridgeSubsystemTests.wait_operation
    release_fixture_operation = _m2.M2BridgeSubsystemTests.release_fixture_operation
    _cleanup_harness = _m2.M2BridgeSubsystemTests._cleanup_harness
    _cleanup_child = staticmethod(_m2.M2BridgeSubsystemTests._cleanup_child)
    _stop_harness = _m2.M2BridgeSubsystemTests._stop_harness

    def takeover(self, ready, *, host=None, helper=None):
        args = {
            "expected_pid": int(ready["pid"]),
            "expected_part_path": "C:\\M2Fake\\model.prt",
            "artifact_scope": "saved_independent_copy",
            "authorize": True,
        }
        if host is not None:
            return successful_tool_result(self, call_tool(host, "nx_takeover", args))
        return successful_helper_result(self, self.helper_call(helper, "ownership.takeover", args))

    def prepare_sketch_args(self, operation_id, owner, epoch, revision_id, geometries,
                            *, plane="XY", origin_mm=(0.0, 0.0, 0.0), constraint_mode=None):
        arguments = {
            "operation_id": operation_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "revision_id": revision_id,
            "plane": plane,
            "origin_mm": list(origin_mm),
            "geometries": geometries,
        }
        if constraint_mode is not None:
            arguments["constraint_mode"] = constraint_mode
        return arguments

    def prepare_extrude_args(self, operation_id, owner, epoch, revision_id, sketch_id,
                             *, mode, direction, extent, depth_mm=None, target_body_id=None,
                             profile_output=None, start_offset_mm=None, end_offset_mm=None):
        args = {
            "operation_id": operation_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "revision_id": revision_id,
            "sketch_id": sketch_id,
            "mode": mode,
            "direction": direction,
            "extent": extent,
        }
        if depth_mm is not None:
            args["depth_mm"] = depth_mm
        if target_body_id is not None:
            args["target_body_id"] = target_body_id
        if profile_output is not None:
            args["profile_output"] = profile_output
        if start_offset_mm is not None:
            args["start_offset_mm"] = start_offset_mm
        if end_offset_mm is not None:
            args["end_offset_mm"] = end_offset_mm
        return args

    def create_sketch(self, helper, owner, epoch, geometries, *, plane="XY", origin_mm=(0, 0, 0)):
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch",
            self.prepare_sketch_args(
                "m3-sketch-prepare-" + uuid.uuid4().hex, owner, epoch,
                session["revision_id"], geometries, plane=plane, origin_mm=origin_mm,
            )))
        plan_id = prepared["result"]["plan"]["plan_id"]
        execute_id = "m3-sketch-execute-" + uuid.uuid4().hex
        self.helper_call(helper, "operation.execute_plan", {
            "operation_id": execute_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": plan_id,
        })
        completed = self.wait_operation(helper, execute_id)
        self.assertEqual(completed.get("state"), "SUCCEEDED", completed)
        return completed

    def prepare_extrude(self, helper, owner, epoch, sketch_id, *, mode, direction, extent,
                        depth_mm=None, target_body_id=None, operation_id=None,
                        profile_output=None, start_offset_mm=None, end_offset_mm=None):
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        args = self.prepare_extrude_args(
            operation_id or ("m3-extrude-prepare-" + uuid.uuid4().hex),
            owner, epoch, session["revision_id"], sketch_id,
            mode=mode, direction=direction, extent=extent,
            depth_mm=depth_mm, target_body_id=target_body_id,
            profile_output=profile_output, start_offset_mm=start_offset_mm, end_offset_mm=end_offset_mm,
        )
        return session, args, successful_helper_result(self, self.helper_call(helper, "operation.prepare_extrude", args))

    def execute_plan(self, helper, owner, epoch, plan_id, *, operation_id=None):
        operation_id = operation_id or ("m3-execute-" + uuid.uuid4().hex)
        accepted = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", {
            "operation_id": operation_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": plan_id,
        }))
        return operation_id, accepted, self.wait_operation(helper, operation_id)

    def verify_plan(self, helper, host, owner, epoch, execute_operation_id, *, operation_id=None):
        operation_id = operation_id or ("m3-verify-" + uuid.uuid4().hex)
        args = {
            "operation_id": operation_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "execute_operation_id": execute_operation_id,
        }
        accepted = successful_tool_result(self, call_tool(host, "nx_verify_part", args))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, accepted)
        completed = self.wait_operation(helper, operation_id)
        self.assertEqual(completed.get("state"), "SUCCEEDED", completed)
        self.assertIs(completed.get("result", {}).get("verified"), True, completed)
        self.assertEqual(completed.get("result", {}).get("execute_operation_id"), execute_operation_id, completed)
        return completed

    def test_geometry_only_line_profile_has_no_dimensions_and_verifies_underconstrained(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        points = [(0.0, 0.0), (20.0, 0.0), (20.0, 5.0), (8.0, 5.0), (8.0, 20.0), (0.0, 20.0), (0.0, 0.0)]
        geometries = [{
            "id": "geometry-only-edge-" + str(index),
            "type": "line",
            "start_mm": list(points[index]),
            "end_mm": list(points[index + 1]),
        } for index in range(len(points) - 1)]
        prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch",
            self.prepare_sketch_args("geometry-only-prepare-" + uuid.uuid4().hex, owner, epoch,
                session["revision_id"], geometries, constraint_mode="geometry_only")))
        plan = prepared.get("result", {}).get("plan", {})
        self.assertEqual(plan.get("constraint_mode"), "geometry_only", plan)
        self.assertEqual(plan.get("driving_dimensions"), [], plan)

        execute_id, _, executed = self.execute_plan(helper, owner, epoch, plan["plan_id"])
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
        result = executed.get("result", {})
        self.assertEqual(result.get("constraint_mode"), "geometry_only", executed)
        self.assertEqual(result.get("sketch_status"), "UnderConstrained", executed)
        self.assertGreater(result.get("degrees_of_freedom", 0), 0, executed)
        self.assertEqual(result.get("constraint_ids"), [], executed)
        self.assertEqual(result.get("driving_dimensions"), [], executed)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_constraint_count"), 0, counts)

        verified = self.verify_plan(helper, host, owner, epoch, execute_id)
        self.assertIs(verified.get("result", {}).get("verified"), True, verified)
        self.assertEqual(verified.get("result", {}).get("checks", {}).get("constraints", {}).get("status"),
                         "VERIFIED", verified)
        record("M3-geometry-only-line-profile", {
            "constraint_mode": result["constraint_mode"],
            "native_line_count": len(geometries),
            "sketch_status": result["sketch_status"],
            "degrees_of_freedom": result["degrees_of_freedom"],
            "native_constraint_count": counts["m3_constraint_count"],
            "verified": verified["result"]["verified"],
        })

    def test_native_sketch_plan_execute_verify_and_driving_dimension_edit(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, host=host)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))

        geometries = [
            {"id": "line-a", "type": "line", "start_mm": [0.0, 0.0], "end_mm": [12.0, 9.0]},
            {"id": "rectangle-a", "type": "rectangle", "corner_mm": [20.0, 30.0],
             "width_mm": 24.0, "height_mm": 10.0},
            {"id": "circle-a", "type": "circle", "center_mm": [-25.0, -18.0], "diameter_mm": 16.0},
        ]
        prepare_id = "m3-native-sketch-prepare-" + uuid.uuid4().hex
        prepare_args = self.prepare_sketch_args(
            prepare_id, owner, epoch, session["revision_id"], geometries,
        )
        prepared = successful_tool_result(self, call_tool(host, "nx_prepare_sketch", prepare_args))
        self.assertEqual(prepared.get("state"), "SUCCEEDED", prepared)
        plan = prepared.get("result", {}).get("plan", {})
        self.assertEqual(plan.get("kind"), "create_sketch", plan)
        self.assertNotIn("constraint_mode", plan)
        self.assertEqual(plan.get("plane"), "XY", plan)
        self.assertEqual(plan.get("origin_mm"), [0.0, 0.0, 0.0], plan)
        self.assertEqual([item.get("type") for item in plan.get("geometries", [])],
                         ["line", "rectangle", "circle"], plan)
        dimensions = plan.get("driving_dimensions", [])
        self.assertTrue(dimensions, plan)
        self.assertTrue(all(item.get("unit") == "mm" for item in dimensions), dimensions)
        planned_values = {(item.get("geometry_id"), item.get("role")): item.get("value")
                          for item in dimensions}
        self.assertIn(("line-a", "length_x"), planned_values, dimensions)
        self.assertIn(("line-a", "length_y"), planned_values, dimensions)
        self.assertIn(("rectangle-a", "width"), planned_values, dimensions)
        self.assertIn(("rectangle-a", "height"), planned_values, dimensions)
        self.assertIn(("circle-a", "diameter"), planned_values, dimensions)
        self.assertEqual(float(planned_values[("rectangle-a", "width")]), 24.0, dimensions)
        self.assertEqual(float(planned_values[("rectangle-a", "height")]), 10.0, dimensions)
        self.assertEqual(float(planned_values[("circle-a", "diameter")]), 16.0, dimensions)
        self.assertEqual(float(planned_values[("line-a", "length_x")]), 12.0, dimensions)
        self.assertEqual(float(planned_values[("line-a", "length_y")]), 9.0, dimensions)

        retry_args = dict(reversed(list(prepare_args.items())))
        retried = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch", retry_args))
        self.assertEqual(retried.get("result"), prepared.get("result"), retried)
        conflict_args = dict(prepare_args)
        conflict_args["geometries"] = [dict(item) for item in geometries]
        conflict_args["geometries"][2]["diameter_mm"] = 17.0
        conflict = self.helper_call(helper, "operation.prepare_sketch", conflict_args)
        self.assertEqual(conflict.get("error", {}).get("code"), "OPERATION_ID_CONFLICT", conflict)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_sketch_prepare_calls"), 1, counts)

        execute_id = "m3-native-sketch-execute-" + uuid.uuid4().hex
        execute_args = {
            "operation_id": execute_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": plan["plan_id"],
        }
        altered = dict(execute_args)
        altered["geometries"] = geometries
        rejected_override = self.helper_call(helper, "operation.execute_plan", altered)
        self.assertEqual(rejected_override.get("error", {}).get("code"), "INVALID_REQUEST", rejected_override)
        accepted = successful_tool_result(self, call_tool(host, "nx_execute_plan", execute_args))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, accepted)
        executed = self.wait_operation(helper, execute_id)
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
        execution_result = executed.get("result", {})
        self.assertEqual(execution_result.get("kind"), "create_sketch", executed)
        self.assertTrue(execution_result.get("sketch_id"), executed)
        self.assertTrue(execution_result.get("feature_id"), executed)
        self.assertEqual(execution_result.get("sketch_status"), "FullyConstrained", executed)
        self.assertEqual(execution_result.get("degrees_of_freedom"), 0, executed)
        self.assertTrue(execution_result.get("constraint_ids"), executed)
        created_dimensions = execution_result.get("driving_dimensions", [])
        self.assertGreaterEqual(len(created_dimensions), len(dimensions), executed)
        self.assertTrue(all(item.get("is_driving") is True for item in created_dimensions), executed)
        self.assertTrue(all(item.get("constraint_id") for item in created_dimensions), executed)
        self.assertTrue(set(item["constraint_id"] for item in created_dimensions).issubset(
            set(execution_result["constraint_ids"])), executed)

        verify_id = "m3-native-sketch-verify-" + uuid.uuid4().hex
        verify_args = {
            "operation_id": verify_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "execute_operation_id": execute_id,
        }
        verify_accepted = successful_tool_result(self, call_tool(host, "nx_verify_part", verify_args))
        self.assertIn(verify_accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, verify_accepted)
        verified = self.wait_operation(helper, verify_id)
        self.assertEqual(verified.get("state"), "SUCCEEDED", verified)
        verification_result = verified.get("result", {})
        self.assertIs(verification_result.get("verified"), True, verified)
        self.assertEqual(verification_result.get("execute_operation_id"), execute_id, verified)
        checks = verification_result.get("checks", {})
        self.assertEqual(set(checks), {"geometry", "constraints", "history", "bodies", "non_target_bodies"}, checks)
        self.assertTrue(all(check.get("status") == "VERIFIED" for check in checks.values()), checks)

        horizontal_projection = next(item for item in created_dimensions
                                     if item.get("geometry_id") == "line-a" and item.get("role") == "length_x")
        current_session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        edit_id = "m3-line-projection-edit-prepare-" + uuid.uuid4().hex
        edit_args = {
            "operation_id": edit_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "revision_id": current_session["revision_id"],
            "sketch_id": execution_result["sketch_id"],
            "constraint_id": horizontal_projection["constraint_id"],
            "value": "16",
            "unit": "mm",
        }
        edit_plan = successful_tool_result(self, call_tool(host, "nx_prepare_operation", edit_args))
        self.assertEqual(edit_plan.get("state"), "SUCCEEDED", edit_plan)
        parameter_plan = edit_plan.get("result", {}).get("plan", {})
        self.assertEqual(parameter_plan.get("target", {}).get("dimension_type"), "HorizontalDim", parameter_plan)
        self.assertEqual(parameter_plan.get("target", {}).get("constraint_id"), horizontal_projection["constraint_id"], parameter_plan)
        parameter_geometry_refs = parameter_plan.get("target", {}).get("geometry_refs", [])
        self.assertTrue(parameter_geometry_refs, parameter_plan)

        edit_execute_id = "m3-line-projection-edit-execute-" + uuid.uuid4().hex
        self.helper_call(helper, "operation.execute_plan", {
            "operation_id": edit_execute_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": parameter_plan["plan_id"],
        })
        edited = self.wait_operation(helper, edit_execute_id)
        self.assertEqual(edited.get("state"), "SUCCEEDED", edited)
        edit_result = edited.get("result", {})
        self.assertEqual(edit_result.get("after", {}).get("value"), "16", edited)
        self.assertNotEqual(edit_result.get("geometry_before"), edit_result.get("geometry_after"), edited)

        observed = successful_helper_result(self, self.helper_call(helper, "model.inspect_sketch", {
            "ownership_id": owner,
            "binding_epoch": epoch,
            "sketch_id": execution_result["sketch_id"],
        }))
        observed_constraints = observed.get("sketch", {}).get("constraints", [])
        observed_line_dimension = next(item for item in observed_constraints
                                       if item.get("journal_id") == horizontal_projection["constraint_id"])
        self.assertEqual(observed_line_dimension.get("dimension_state"), "Driving", observed_line_dimension)
        self.assertAlmostEqual(float(observed_line_dimension.get("value")), 16.0, places=8, msg=observed_line_dimension)
        observed_line = next(item for item in observed["sketch"]["geometry"] if item.get("id") == "line-a")
        self.assertEqual(observed_line.get("end_mm"), [16.0, 9.0], observed_line)
        parameter_verification = self.verify_plan(helper, host, owner, epoch, edit_execute_id)
        self.assertEqual(set(parameter_verification.get("result", {}).get("checks", {})),
                         {"expression", "geometry", "history", "associations"}, parameter_verification)

        record("M3-sketch-create-drive-verify", {
            "geometry_types": ["line", "rectangle", "circle"],
            "planned_driving_dimensions": len(dimensions),
            "created_driving_constraints": len(created_dimensions),
            "execute_operation_id": execute_id,
            "verified_checks": sorted(checks),
            "edited_constraint_id": horizontal_projection["constraint_id"],
            "edited_value_mm": 16.0,
            "edited_component": "horizontal projection (length_x)",
            "same_sketch_id": execution_result["sketch_id"],
        })

    def test_m4_query_and_face_sketch_contract_reaches_bridge_adapter(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        revision = session["revision_id"]

        selector = "最外侧正X面"
        query = successful_helper_result(self, self.helper_call(helper, "model.query_faces", {
            "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
            "selector": selector, "body_id": "body-scope-1",
        }))
        self.assertEqual(query.get("selection_status"), "unique", query)
        self.assertEqual(query.get("candidate_count"), 1, query)
        self.assertEqual(query.get("body_id"), "body-scope-1", query)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m4_face_query_calls"), 1, counts)
        self.assertEqual(counts.get("m4_last_selector"), selector, counts)
        self.assertEqual(counts.get("m4_last_body_id"), "body-scope-1", counts)

        common = {
            "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
            "geometries": [{"id": "support-circle", "type": "circle",
                            "center_mm": [20, 3], "diameter_mm": 2}],
        }
        face_request = {
            "operation_id": "m4-face-default-" + uuid.uuid4().hex,
            **common,
            "support_face_selector": "outermost positive X side",
        }
        prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch", face_request))
        plan = prepared.get("result", {}).get("plan", {})
        self.assertEqual(plan.get("support_face_selector"), "outermost positive X side", plan)
        self.assertIsNone(plan.get("support_body_id"), plan)
        self.assertEqual(plan.get("face_origin_offset_mm"), [0, 0], plan)

        explicit = dict(face_request)
        explicit["operation_id"] = "m4-face-explicit-" + uuid.uuid4().hex
        explicit["support_body_id"] = "body-scope-2"
        explicit["face_origin_offset_mm"] = [-1.25, 2.5]
        planned = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch", explicit))
        explicit_plan = planned.get("result", {}).get("plan", {})
        self.assertEqual(explicit_plan.get("support_body_id"), "body-scope-2", explicit_plan)
        self.assertEqual(explicit_plan.get("face_origin_offset_mm"), [-1.25, 2.5], explicit_plan)

        invalid = dict(face_request)
        invalid["operation_id"] = "m4-face-mixed-" + uuid.uuid4().hex
        invalid["plane"] = "XY"
        invalid["origin_mm"] = [0, 0, 0]
        rejected = self.helper_call(helper, "operation.prepare_sketch", invalid)
        self.assertEqual(rejected.get("error", {}).get("code"), "INVALID_REQUEST", rejected)
        invalid = dict(face_request)
        invalid["operation_id"] = "m4-face-candidate-" + uuid.uuid4().hex
        invalid["candidate_id"] = "candidate-001"
        rejected = self.helper_call(helper, "operation.prepare_sketch", invalid)
        self.assertEqual(rejected.get("error", {}).get("code"), "INVALID_REQUEST", rejected)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_sketch_prepare_calls"), 2, counts)

    def test_extrude_create_unite_subtract_direction_target_and_non_target(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, host=host)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]

        target_sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "target-profile", "type": "rectangle", "corner_mm": [10.0, 10.0],
            "width_mm": 20.0, "height_mm": 10.0,
        }], origin_mm=(0.0, 0.0, -2.0))
        target_sketch_id = target_sketch["result"]["sketch_id"]
        _, create_args, create_prepared = self.prepare_extrude(
            helper, owner, epoch, target_sketch_id,
            mode="create", direction="positive", extent="finite", depth_mm=7.0,
        )
        create_plan = create_prepared["result"]["plan"]
        self.assertEqual(create_plan.get("kind"), "create_extrude", create_plan)
        self.assertEqual(create_plan.get("mode"), "create", create_plan)
        self.assertEqual(create_plan.get("extent"), "finite", create_plan)
        self.assertEqual(create_plan.get("depth_mm"), 7.0, create_plan)
        self.assertIsNone(create_plan.get("target_body_id"), create_plan)
        create_id, _, created = self.execute_plan(helper, owner, epoch, create_plan["plan_id"])
        target_body_id = created.get("result", {}).get("body_id")
        self.assertEqual(created.get("state"), "SUCCEEDED", created)
        self.assertTrue(target_body_id, created)
        target_before = next(body for body in created["result"]["body_inventory"]
                             if body.get("journal_id") == target_body_id)
        self.assertIs(target_before.get("is_solid"), True, target_before)
        self.assertAlmostEqual(float(target_before.get("volume_mm3")), 1400.0, places=7, msg=target_before)
        self.assertEqual(len(target_before.get("bbox_mm", [])), 6, target_before)

        boolean_sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "boolean-profile", "type": "rectangle", "corner_mm": [15.0, 12.0],
            "width_mm": 5.0, "height_mm": 4.0,
        }])
        boolean_sketch_id = boolean_sketch["result"]["sketch_id"]
        non_target_sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "non-target-profile", "type": "rectangle", "corner_mm": [60.0, 50.0],
            "width_mm": 3.0, "height_mm": 3.0,
        }])
        non_target_sketch_id = non_target_sketch["result"]["sketch_id"]
        _, other_create_args, other_create_prepared = self.prepare_extrude(
            helper, owner, epoch, non_target_sketch_id,
            mode="create", direction="positive", extent="finite", depth_mm=2.0,
        )
        other_create_id, _, other_created = self.execute_plan(
            helper, owner, epoch, other_create_prepared["result"]["plan"]["plan_id"],
        )
        self.assertEqual(other_created.get("state"), "SUCCEEDED", other_created)
        non_target_body_id = other_created["result"]["body_id"]
        body_before_boolean = {body["journal_id"]: body for body in other_created["result"]["body_inventory"]}
        non_target_before = body_before_boolean[non_target_body_id]

        union_id = "m3-unite-prepare-" + uuid.uuid4().hex
        union_session = successful_tool_result(self, call_tool(host, "nx_get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        union_args = self.prepare_extrude_args(
            union_id, owner, epoch, union_session["revision_id"], boolean_sketch_id,
            mode="unite", direction="negative", extent="finite", depth_mm=4.0,
            target_body_id=target_body_id,
        )
        union_prepared = successful_tool_result(self, call_tool(host, "nx_prepare_extrude", union_args))
        union_plan = union_prepared["result"]["plan"]
        self.assertEqual(union_plan.get("mode"), "unite", union_plan)
        self.assertEqual(union_plan.get("direction"), "negative", union_plan)
        self.assertEqual(union_plan.get("extent"), "finite", union_plan)
        self.assertEqual(union_plan.get("target_body_id"), target_body_id, union_plan)
        self.assertEqual(union_plan.get("target_body_before", {}).get("journal_id"), target_body_id, union_plan)
        self.assertEqual([body.get("journal_id") for body in union_plan.get("non_target_bodies_before", [])],
                         [non_target_body_id], union_plan)
        union_execute_id = "m3-unite-execute-" + uuid.uuid4().hex
        union_accepted = successful_tool_result(self, call_tool(host, "nx_execute_plan", {
            "operation_id": union_execute_id, "ownership_id": owner,
            "binding_epoch": epoch, "plan_id": union_plan["plan_id"],
        }))
        self.assertIn(union_accepted.get("state"), {"QUEUED", "RUNNING", "SUCCEEDED"}, union_accepted)
        union_completed = self.wait_operation(helper, union_execute_id)
        self.assertEqual(union_completed.get("state"), "SUCCEEDED", union_completed)
        union_bodies = {body["journal_id"]: body for body in union_completed["result"]["body_inventory"]}
        self.assertEqual(set(union_bodies), {target_body_id, non_target_body_id}, union_bodies)
        self.assertEqual(union_bodies[non_target_body_id], non_target_before, union_bodies)
        self.assertGreater(float(union_bodies[target_body_id]["volume_mm3"]),
                           float(target_before["volume_mm3"]), union_bodies)
        union_verified = self.verify_plan(helper, host, owner, epoch, union_execute_id)
        union_checks = union_verified["result"]["checks"]
        self.assertTrue(all(check.get("status") == "VERIFIED" for check in union_checks.values()), union_checks)

        subtract_session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        subtract_args = self.prepare_extrude_args(
            "m3-subtract-prepare-" + uuid.uuid4().hex, owner, epoch, subtract_session["revision_id"],
            boolean_sketch_id, mode="subtract", direction="positive", extent="through_all",
            target_body_id=target_body_id,
        )
        self.assertNotIn("depth_mm", subtract_args)
        subtract_prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_extrude", subtract_args))
        subtract_plan = subtract_prepared["result"]["plan"]
        self.assertEqual(subtract_plan.get("mode"), "subtract", subtract_plan)
        self.assertEqual(subtract_plan.get("direction"), "positive", subtract_plan)
        self.assertEqual(subtract_plan.get("extent"), "through_all", subtract_plan)
        self.assertEqual(subtract_plan.get("target_body_id"), target_body_id, subtract_plan)
        self.assertIsNone(subtract_plan.get("depth_mm"), subtract_plan)
        subtract_execute_id, _, subtracted = self.execute_plan(
            helper, owner, epoch, subtract_plan["plan_id"],
        )
        self.assertEqual(subtracted.get("state"), "SUCCEEDED", subtracted)
        subtract_bodies = {body["journal_id"]: body for body in subtracted["result"]["body_inventory"]}
        self.assertEqual(set(subtract_bodies), {target_body_id, non_target_body_id}, subtract_bodies)
        self.assertEqual(subtract_bodies[non_target_body_id], non_target_before, subtract_bodies)
        self.assertLess(float(subtract_bodies[target_body_id]["volume_mm3"]),
                        float(union_bodies[target_body_id]["volume_mm3"]), subtract_bodies)
        subtract_verified = self.verify_plan(helper, host, owner, epoch, subtract_execute_id)
        subtract_checks = subtract_verified["result"]["checks"]
        self.assertTrue(all(check.get("status") == "VERIFIED" for check in subtract_checks.values()), subtract_checks)
        non_target_evidence = subtract_checks["non_target_bodies"].get("evidence", {})
        self.assertEqual(non_target_evidence.get("before"), non_target_evidence.get("after"), non_target_evidence)

        record("M3-extrude-create-unite-subtract", {
            "create": {"direction": "positive", "extent": "finite", "depth_mm": 7.0},
            "unite": {"direction": "negative", "extent": "finite", "depth_mm": 4.0,
                      "target_body_id": target_body_id},
            "subtract": {"direction": "positive", "extent": "through_all",
                         "target_body_id": target_body_id},
            "body_count_after_create": 1,
            "body_count_after_booleans": len(subtract_bodies),
            "non_target_body_unchanged": True,
            "verified_checks": sorted(subtract_checks),
        })

    def test_f12_legacy_depth_and_offsets_share_normalized_operation_key(self):
        ready = self.start_harness()
        helper = self.start_helper()
        host, _ = self.start_host(timeout_seconds=10.0)
        grant = self.takeover(ready, host=host)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "f12-normalize-profile", "type": "rectangle", "corner_mm": [0, 0],
            "width_mm": 8, "height_mm": 5,
        }])
        sketch_id = sketch["result"]["sketch_id"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        operation_id = "f12-normalized-alias-" + uuid.uuid4().hex
        legacy = self.prepare_extrude_args(
            operation_id, owner, epoch, session["revision_id"], sketch_id,
            mode="create", direction="positive", extent="finite", depth_mm=5.0,
        )
        legacy_response = successful_helper_result(self, self.helper_call(helper, "operation.prepare_extrude", legacy))
        legacy_plan = legacy_response["result"]["plan"]
        counts_after_legacy = self.fake_counts()
        aliases = self.prepare_extrude_args(
            operation_id, owner, epoch, session["revision_id"], sketch_id,
            mode="create", direction="positive", extent="finite",
            start_offset_mm=0.0, end_offset_mm=5.0,
        )
        alias_response = successful_tool_result(self, call_tool(host, "nx_prepare_extrude", aliases))
        alias_plan = alias_response["result"]["plan"]
        counts_after_alias = self.fake_counts()
        self.assertEqual(legacy_plan["plan_id"], alias_plan["plan_id"], alias_plan)
        self.assertEqual(alias_plan.get("profile_output"), "solid", alias_plan)
        self.assertEqual(alias_plan.get("start_offset_mm"), 0.0, alias_plan)
        self.assertEqual(alias_plan.get("end_offset_mm"), 5.0, alias_plan)
        self.assertEqual(counts_after_alias.get("m3_extrude_prepare_calls"),
                         counts_after_legacy.get("m3_extrude_prepare_calls"), counts_after_alias)

        conflict = self.helper_call(helper, "operation.prepare_extrude", self.prepare_extrude_args(
            operation_id, owner, epoch, session["revision_id"], sketch_id,
            mode="create", direction="positive", extent="finite",
            start_offset_mm=0.0, end_offset_mm=6.0,
        ))
        self.assertEqual(conflict.get("error", {}).get("code"), "OPERATION_ID_CONFLICT", conflict)
        self.assertEqual(self.fake_counts().get("m3_extrude_prepare_calls"),
                         counts_after_alias.get("m3_extrude_prepare_calls"), self.fake_counts())

        execute_id, _, executed = self.execute_plan(helper, owner, epoch, legacy_plan["plan_id"])
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)
        self.assertEqual(executed.get("result", {}).get("mutation_state"), "APPLIED", executed)
        verified = self.verify_plan(helper, host, owner, epoch, execute_id)
        self.assertTrue(all(check.get("status") == "VERIFIED" for check in verified["result"]["checks"].values()), verified)
        record("F12-legacy-offset-normalized-operation-key", {
            "legacy_depth_mm": 5.0, "offset_interval_mm": [0.0, 5.0],
            "normalized_profile_output": alias_plan.get("profile_output"),
            "same_operation_id_reused_same_plan": True,
            "adapter_prepare_calls_not_duplicated": True,
            "conflicting_interval_error": conflict["error"]["code"],
            "execute_state": executed["state"], "verified_checks": sorted(verified["result"]["checks"]),
        })

    def test_f12_offset_rollback_and_unknown_lifecycle_do_not_replay_mutations(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "f12-lifecycle-profile", "type": "rectangle", "corner_mm": [0, 0],
            "width_mm": 10, "height_mm": 6,
        }])
        sketch_id = sketch["result"]["sketch_id"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        _, _, prepared = self.prepare_extrude(
            helper, owner, epoch, sketch_id, mode="create", direction="positive", extent="finite",
            start_offset_mm=-2.0, end_offset_mm=4.0, profile_output="solid",
        )
        counts_before_rollback = self.fake_counts()
        self.harness_command("rollback-after-write")
        rollback_id, _, rolled_back = self.execute_plan(
            helper, owner, epoch, prepared["result"]["plan"]["plan_id"],
        )
        self.assertEqual(rolled_back.get("state"), "FAILED", rolled_back)
        self.assertEqual(rolled_back.get("error", {}).get("code"), "EDIT_NOT_VERIFIED", rolled_back)
        self.assertEqual(rolled_back.get("result", {}).get("mutation_state"), "ROLLED_BACK", rolled_back)
        self.assertEqual(rolled_back.get("result", {}).get("body_inventory"), [], rolled_back)
        replay_rollback = self.get_operation(helper, rollback_id)
        self.assertEqual(replay_rollback.get("state"), "FAILED", replay_rollback)
        self.assertEqual(self.fake_counts().get("m3_execution_calls"), counts_before_rollback.get("m3_execution_calls") + 1,
                         self.fake_counts())

        _, _, unknown_prepared = self.prepare_extrude(
            helper, owner, epoch, sketch_id, mode="create", direction="negative", extent="finite",
            start_offset_mm=-1.0, end_offset_mm=3.0, profile_output="solid",
        )
        before_unknown = self.fake_counts()["m3_execution_calls"]
        self.harness_command("unknown-after-mutation")
        unknown_id, _, unknown = self.execute_plan(
            helper, owner, epoch, unknown_prepared["result"]["plan"]["plan_id"],
        )
        self.assertEqual(unknown.get("state"), "UNKNOWN", unknown)
        self.assertEqual(unknown.get("result", {}).get("mutation_state"), "UNKNOWN", unknown)
        replay_unknown = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", {
            "operation_id": unknown_id, "ownership_id": owner, "binding_epoch": epoch,
            "plan_id": unknown_prepared["result"]["plan"]["plan_id"],
        }))
        self.assertEqual(replay_unknown.get("state"), "UNKNOWN", replay_unknown)
        blocked = self.helper_call(helper, "operation.execute_plan", {
            "operation_id": "f12-blocked-after-unknown-" + uuid.uuid4().hex,
            "ownership_id": owner, "binding_epoch": epoch,
            "plan_id": unknown_prepared["result"]["plan"]["plan_id"],
        })
        self.assertIn(blocked.get("error", {}).get("code"), {"OPERATION_OUTCOME_UNKNOWN", "CLIENT_OUTCOME_UNKNOWN"}, blocked)
        self.assertEqual(self.fake_counts().get("m3_execution_calls"), before_unknown + 1, self.fake_counts())
        record("F12-offset-rollback-unknown-no-replay", {
            "known_rollback_state": rolled_back["result"]["mutation_state"],
            "same_id_rollback_query_state": replay_rollback["state"],
            "unknown_state": unknown["state"], "same_id_unknown_query_state": replay_unknown["state"],
            "new_write_blocked_error": blocked["error"]["code"], "m3_execution_calls_for_unknown": 1,
        })

    def test_f12_prepare_unknown_freezes_writes_and_same_id_is_query_only(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "f12-prepare-unknown-profile", "type": "rectangle", "corner_mm": [0, 0],
            "width_mm": 12, "height_mm": 8,
        }])
        sketch_id = sketch["result"]["sketch_id"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        operation_id = "f12-prepare-unknown-" + uuid.uuid4().hex
        args = self.prepare_extrude_args(
            operation_id, owner, epoch, session["revision_id"], sketch_id,
            mode="create", direction="positive", extent="finite", depth_mm=5.0,
            profile_output="solid",
        )
        before = self.fake_counts()
        self.assertEqual(before.get("m3_extrude_prepare_calls"), 0, before)
        injected = self.harness_command("unknown-next-m3-prepare")
        self.assertEqual(injected.get("event"), "unknown-next-m3-prepare", injected)

        prepare = successful_helper_result(self, self.helper_call(helper, "operation.prepare_extrude", args))
        self.assertEqual(prepare.get("state"), "UNKNOWN", prepare)
        self.assertEqual(prepare.get("error", {}).get("code"), "M3_ANALYSIS_STATE_UNKNOWN", prepare)
        self.assertIs(prepare.get("writes_halted"), True, prepare)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertIs(status.get("model_write_enabled"), False, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        self.assertEqual(status.get("write_halt_reason"), "M3_ANALYSIS_STATE_UNKNOWN", status)

        queried = self.get_operation(helper, operation_id)
        self.assertEqual(queried.get("state"), "UNKNOWN", queried)
        self.assertEqual(queried.get("error", {}).get("code"), "M3_ANALYSIS_STATE_UNKNOWN", queried)
        replay = successful_helper_result(self, self.helper_call(helper, "operation.prepare_extrude", args))
        self.assertEqual(replay.get("state"), "UNKNOWN", replay)
        self.assertEqual(replay.get("error", {}).get("code"), "M3_ANALYSIS_STATE_UNKNOWN", replay)

        blocked_args = dict(args)
        blocked_args["operation_id"] = "f12-prepare-blocked-" + uuid.uuid4().hex
        blocked = self.helper_call(helper, "operation.prepare_extrude", blocked_args)
        self.assertEqual(blocked.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked)
        after = self.fake_counts()
        self.assertEqual(after.get("m3_extrude_prepare_calls"), before.get("m3_extrude_prepare_calls") + 1, after)
        self.assertEqual(after.get("m3_execution_calls"), before.get("m3_execution_calls"), after)
        self.assertEqual(after.get("m3_mutation_count"), before.get("m3_mutation_count"), after)
        self.assertEqual(after.get("m3_sketch_count"), before.get("m3_sketch_count"), after)
        self.assertEqual(after.get("m3_body_count"), before.get("m3_body_count"), after)
        self.assertEqual(after.get("revision_id"), before.get("revision_id"), after)
        record("F12-prepare-unknown-freeze-and-query-only", {
            "operation_id": operation_id,
            "operation_state": prepare["state"],
            "error_code": prepare["error"]["code"],
            "writes_halted": status["writes_halted"],
            "ownership_state": status["ownership_state"],
            "same_id_operation_get_state": queried["state"],
            "same_id_prepare_replay_state": replay["state"],
            "new_write_rejection": blocked["error"]["code"],
            "adapter_prepare_calls_for_unknown": after["m3_extrude_prepare_calls"] - before["m3_extrude_prepare_calls"],
            "adapter_execute_calls": after["m3_execution_calls"],
            "mutation_count_unchanged": after["m3_mutation_count"] == before["m3_mutation_count"],
            "revision_unchanged": after["revision_id"] == before["revision_id"],
        })

    def test_f12_invalid_extent_and_output_combinations_reject_before_adapter(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        base = {
            "operation_id": "f12-invalid-", "ownership_id": owner, "binding_epoch": epoch,
            "revision_id": session["revision_id"], "sketch_id": "unresolved",
            "mode": "create", "direction": "positive", "extent": "finite",
        }
        invalid = [
            {**base, "operation_id": "f12-sheet-unite-" + uuid.uuid4().hex, "mode": "unite",
             "target_body_id": "body-1", "profile_output": "sheet", "depth_mm": 5.0},
            {**base, "operation_id": "f12-through-create-" + uuid.uuid4().hex, "extent": "through_all",
             "target_body_id": "body-1"},
            {**base, "operation_id": "f12-offset-start-only-" + uuid.uuid4().hex, "start_offset_mm": -1.0},
            {**base, "operation_id": "f12-offset-end-only-" + uuid.uuid4().hex, "end_offset_mm": 5.0},
            {**base, "operation_id": "f12-depth-and-offset-" + uuid.uuid4().hex, "depth_mm": 5.0,
             "start_offset_mm": 0.0, "end_offset_mm": 5.0},
            {**base, "operation_id": "f12-reversed-offset-" + uuid.uuid4().hex,
             "start_offset_mm": 6.0, "end_offset_mm": 5.0},
            {**base, "operation_id": "f12-zero-depth-" + uuid.uuid4().hex, "depth_mm": 0.0},
            {**base, "operation_id": "f12-through-sheet-" + uuid.uuid4().hex, "mode": "subtract",
             "extent": "through_all", "target_body_id": "body-1", "profile_output": "sheet"},
        ]
        for args in invalid:
            response = self.helper_call(helper, "operation.prepare_extrude", args)
            self.assertEqual(response.get("error", {}).get("code"), "INVALID_REQUEST", response)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_extrude_prepare_calls"), 0, counts)
        self.assertEqual(counts.get("m3_execution_calls"), 0, counts)
        record("F12-invalid-offset-output-extent-before-adapter", {
            "invalid_case_count": len(invalid), "adapter_prepare_calls": 0, "execute_calls": 0,
            "invalid_categories": ["sheet_boolean", "through_all_create", "incomplete_offsets",
                                   "mixed_depth_and_offset", "reversed_interval", "zero_depth", "sheet_through_all"],
        })

    def test_direct_helper_rejects_invalid_sketch_and_extrude_requests_before_adapter(self):
        ready = self.start_harness()
        helper = self.start_helper()
        geometries = [{
            "id": "line-valid", "type": "line", "start_mm": [0.0, 0.0], "end_mm": [10.0, 0.0],
        }]
        unowned = self.helper_call(helper, "operation.prepare_sketch", self.prepare_sketch_args(
            "m3-unowned-" + uuid.uuid4().hex, "no-current-owner", 1, "1", geometries,
        ))
        self.assertEqual(unowned.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", unowned)

        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        revision = session["revision_id"]

        stale_epoch = self.helper_call(helper, "operation.prepare_sketch", self.prepare_sketch_args(
            "m3-stale-epoch-" + uuid.uuid4().hex, owner, epoch + 1, revision, geometries,
        ))
        self.assertEqual(stale_epoch.get("error", {}).get("code"), "AUTHORIZATION_REQUIRED", stale_epoch)

        stale = self.helper_call(helper, "operation.prepare_sketch", self.prepare_sketch_args(
            "m3-stale-revision-" + uuid.uuid4().hex, owner, epoch, "stale-revision", geometries,
        ))
        self.assertEqual(stale.get("result", {}).get("state"), "FAILED", stale)
        self.assertEqual(stale.get("result", {}).get("error", {}).get("code"), "STALE_REVISION", stale)

        invalid_requests = [
            self.prepare_sketch_args("m3-invalid-plane-" + uuid.uuid4().hex, owner, epoch, revision,
                                     geometries, plane="AB"),
            self.prepare_sketch_args("m3-invalid-constraint-mode-" + uuid.uuid4().hex, owner, epoch,
                                     revision, geometries, constraint_mode="infer"),
            self.prepare_sketch_args("m3-zero-line-" + uuid.uuid4().hex, owner, epoch, revision, [{
                "id": "zero", "type": "line", "start_mm": [2.0, 3.0], "end_mm": [2.0, 3.0],
            }]),
            self.prepare_sketch_args("m3-duplicate-id-" + uuid.uuid4().hex, owner, epoch, revision, [
                {"id": "same", "type": "circle", "center_mm": [0.0, 0.0], "diameter_mm": 5.0},
                {"id": "same", "type": "circle", "center_mm": [10.0, 0.0], "diameter_mm": 7.0},
            ]),
            self.prepare_sketch_args("m3-zero-width-" + uuid.uuid4().hex, owner, epoch, revision, [{
                "id": "zero-width", "type": "rectangle", "corner_mm": [0.0, 0.0],
                "width_mm": 0.0, "height_mm": 5.0,
            }]),
            self.prepare_sketch_args("m3-zero-diameter-" + uuid.uuid4().hex, owner, epoch, revision, [{
                "id": "zero-diameter", "type": "circle", "center_mm": [0.0, 0.0], "diameter_mm": 0.0,
            }]),
        ]
        for args in invalid_requests:
            response = self.helper_call(helper, "operation.prepare_sketch", args)
            self.assertEqual(response.get("error", {}).get("code"), "INVALID_REQUEST", response)

        invalid_extrusions = [
            {
                "operation_id": "m3-create-through-all-" + uuid.uuid4().hex,
                "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
                "sketch_id": "unresolved", "mode": "create", "direction": "positive", "extent": "through_all",
            },
            {
                "operation_id": "m3-boolean-without-target-" + uuid.uuid4().hex,
                "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
                "sketch_id": "unresolved", "mode": "unite", "direction": "positive",
                "extent": "finite", "depth_mm": 5.0,
            },
            {
                "operation_id": "m3-create-with-target-" + uuid.uuid4().hex,
                "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
                "sketch_id": "unresolved", "mode": "create", "direction": "positive",
                "extent": "finite", "depth_mm": 5.0, "target_body_id": "body-1",
            },
            {
                "operation_id": "m3-zero-depth-" + uuid.uuid4().hex,
                "ownership_id": owner, "binding_epoch": epoch, "revision_id": revision,
                "sketch_id": "unresolved", "mode": "create", "direction": "positive",
                "extent": "finite", "depth_mm": 0.0,
            },
        ]
        for args in invalid_extrusions:
            response = self.helper_call(helper, "operation.prepare_extrude", args)
            self.assertEqual(response.get("error", {}).get("code"), "INVALID_REQUEST", response)

        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_sketch_prepare_calls"), 0, counts)
        self.assertEqual(counts.get("m3_extrude_prepare_calls"), 0, counts)
        self.assertEqual(counts.get("m3_execution_calls"), 0, counts)
        record("M3-helper-validation", {
            "unowned_error": unowned["error"]["code"],
            "stale_binding_epoch_error": stale_epoch["error"]["code"],
            "stale_revision_error": stale["result"]["error"]["code"],
            "invalid_sketch_cases": len(invalid_requests),
            "invalid_extrude_cases": len(invalid_extrusions),
            "adapter_prepare_calls_after_rejections": 0,
        })

    def test_fixture_rollback_leaves_no_extrude_body_and_allows_retest(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "rollback-profile", "type": "rectangle", "corner_mm": [0.0, 0.0],
            "width_mm": 12.0, "height_mm": 8.0,
        }])
        sketch_id = sketch["result"]["sketch_id"]
        _, _, prepared = self.prepare_extrude(
            helper, owner, epoch, sketch_id, mode="create", direction="positive",
            extent="finite", depth_mm=6.0,
        )
        plan_id = prepared["result"]["plan"]["plan_id"]
        self.harness_command("rollback-after-write")
        failed_id, _, failed = self.execute_plan(helper, owner, epoch, plan_id)
        self.assertEqual(failed.get("state"), "FAILED", failed)
        self.assertEqual(failed.get("error", {}).get("code"), "EDIT_NOT_VERIFIED", failed)
        self.assertEqual(failed.get("result", {}).get("mutation_state"), "ROLLED_BACK", failed)
        self.assertIs(failed.get("result", {}).get("changed"), False, failed)
        self.assertEqual(failed.get("result", {}).get("body_inventory"), [], failed)

        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), False, status)
        self.assertEqual(status.get("ownership_state"), "AI_OWNED", status)
        after_failure = self.fake_counts()
        self.assertEqual(after_failure.get("m3_body_count"), 0, after_failure)
        self.assertEqual(after_failure.get("rollback_count"), 1, after_failure)

        _, _, retry_prepared = self.prepare_extrude(
            helper, owner, epoch, sketch_id, mode="create", direction="positive",
            extent="finite", depth_mm=6.0,
        )
        retry_id, _, retry = self.execute_plan(
            helper, owner, epoch, retry_prepared["result"]["plan"]["plan_id"],
        )
        self.assertEqual(retry.get("state"), "SUCCEEDED", retry)
        self.assertEqual(retry.get("result", {}).get("mutation_state"), "APPLIED", retry)
        self.assertEqual(self.fake_counts().get("m3_body_count"), 1)
        record("M3-rollback-retest", {
            "injection": "rollback-after-write",
            "failed_execute_operation_id": failed_id,
            "failure_error": failed["error"]["code"],
            "body_count_after_rollback": after_failure["m3_body_count"],
            "writes_halted_after_fixture_rollback_state": status["writes_halted"],
            "fresh_execute_operation_id": retry_id,
            "retest_state": retry["state"],
        })

    def test_unknown_m3_profile_verification_freezes_future_writes(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch",
            self.prepare_sketch_args("unknown-verify-prepare-" + uuid.uuid4().hex, owner, epoch,
                session["revision_id"], [{
                    "id": "unknown-verify-profile", "type": "rectangle", "corner_mm": [0.0, 0.0],
                    "width_mm": 8.0, "height_mm": 5.0,
                }])))
        execute_id, _, executed = self.execute_plan(helper, owner, epoch,
            prepared["result"]["plan"]["plan_id"])
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)

        next_session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        blocked_plan = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch",
            self.prepare_sketch_args("unknown-verify-followup-" + uuid.uuid4().hex, owner, epoch,
                next_session["revision_id"], [{
                    "id": "followup-profile", "type": "line", "start_mm": [20.0, 0.0], "end_mm": [24.0, 0.0],
                }])))
        before_verify = self.fake_counts()
        self.harness_command("unknown-next-m3-verification")
        verify_id = "m3-unknown-verify-" + uuid.uuid4().hex
        verify_args = {
            "operation_id": verify_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "execute_operation_id": execute_id,
        }
        verify_accepted = successful_helper_result(self, self.helper_call(helper, "operation.verify_part", verify_args))
        self.assertIn(verify_accepted.get("state"), {"QUEUED", "RUNNING", "UNKNOWN"}, verify_accepted)
        unknown = self.wait_operation(helper, verify_id)
        self.assertEqual(unknown.get("state"), "UNKNOWN", unknown)
        self.assertEqual(unknown.get("error", {}).get("code"), "M3_ANALYSIS_STATE_UNKNOWN", unknown)
        self.assertEqual(unknown.get("result", {}).get("verification_state"), "UNKNOWN", unknown)

        after_unknown = self.fake_counts()
        self.assertEqual(after_unknown.get("m3_verification_calls"),
                         before_verify.get("m3_verification_calls", 0) + 1, after_unknown)
        self.assertEqual(after_unknown.get("m3_mutation_count"), before_verify.get("m3_mutation_count"), after_unknown)
        same_id_replay = successful_helper_result(
            self, self.helper_call(helper, "operation.verify_part", verify_args))
        self.assertEqual(same_id_replay.get("state"), "UNKNOWN", same_id_replay)
        self.assertEqual(same_id_replay.get("error", {}).get("code"), "M3_ANALYSIS_STATE_UNKNOWN", same_id_replay)
        queried = self.get_operation(helper, verify_id)
        self.assertEqual(queried.get("state"), "UNKNOWN", queried)
        self.assertEqual(queried.get("error", {}).get("code"), "M3_ANALYSIS_STATE_UNKNOWN", queried)
        after_same_id_queries = self.fake_counts()
        self.assertEqual(after_same_id_queries.get("m3_verification_calls"),
                         after_unknown.get("m3_verification_calls"), after_same_id_queries)
        self.assertEqual(after_same_id_queries.get("m3_mutation_count"),
                         after_unknown.get("m3_mutation_count"), after_same_id_queries)

        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        blocked = self.helper_call(helper, "operation.execute_plan", {
            "operation_id": "m3-blocked-after-unknown-verify-" + uuid.uuid4().hex,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": blocked_plan["result"]["plan"]["plan_id"],
        })
        self.assertEqual(blocked.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_execution_calls"), 1, counts)
        self.assertEqual(counts.get("m3_verification_calls"), after_unknown.get("m3_verification_calls"), counts)
        self.assertEqual(counts.get("m3_mutation_count"), before_verify.get("m3_mutation_count"), counts)
        record("M3-profile-verify-unknown-freeze", {
            "verify_operation_id": verify_id,
            "verify_state": unknown["state"],
            "verify_error": unknown["error"]["code"],
            "same_id_verify_replay_state": same_id_replay["state"],
            "same_id_verify_replay_error": same_id_replay["error"]["code"],
            "same_id_operation_get_state": queried["state"],
            "same_id_queries_replayed_verification": False,
            "mutation_count_unchanged_by_unknown_read": counts["m3_mutation_count"] == before_verify["m3_mutation_count"],
            "writes_halted": status["writes_halted"],
            "blocked_execute_error": blocked["error"]["code"],
            "execution_calls": counts["m3_execution_calls"],
            "verification_calls": counts["m3_verification_calls"],
        })

    def test_m3_profile_verification_mismatch_does_not_freeze_future_writes(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch",
            self.prepare_sketch_args("mismatch-verify-prepare-" + uuid.uuid4().hex, owner, epoch,
                session["revision_id"], [{
                    "id": "mismatch-verify-profile", "type": "rectangle", "corner_mm": [0.0, 0.0],
                    "width_mm": 8.0, "height_mm": 5.0,
                }])))
        execute_id, _, executed = self.execute_plan(helper, owner, epoch,
            prepared["result"]["plan"]["plan_id"])
        self.assertEqual(executed.get("state"), "SUCCEEDED", executed)

        self.harness_command("mismatch-next-m3-verification")
        verify_id = "m3-mismatch-verify-" + uuid.uuid4().hex
        accepted = successful_helper_result(self, self.helper_call(helper, "operation.verify_part", {
            "operation_id": verify_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "execute_operation_id": execute_id,
        }))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING"}, accepted)
        mismatch = self.wait_operation(helper, verify_id)
        self.assertEqual(mismatch.get("state"), "FAILED", mismatch)
        self.assertEqual(mismatch.get("error", {}).get("code"), "PART_VERIFICATION_FAILED", mismatch)
        self.assertIs(mismatch.get("result", {}).get("verified"), False, mismatch)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), False, status)

        next_session = successful_helper_result(self, self.helper_call(helper, "model.get_session", {
            "ownership_id": owner, "binding_epoch": epoch,
        }))
        next_prepared = successful_helper_result(self, self.helper_call(helper, "operation.prepare_sketch",
            self.prepare_sketch_args("mismatch-verify-followup-" + uuid.uuid4().hex, owner, epoch,
                next_session["revision_id"], [{
                    "id": "followup-profile", "type": "line", "start_mm": [20.0, 0.0], "end_mm": [24.0, 0.0],
                }])))
        _, _, retry = self.execute_plan(helper, owner, epoch,
            next_prepared["result"]["plan"]["plan_id"])
        self.assertEqual(retry.get("state"), "SUCCEEDED", retry)
        final_status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(final_status.get("writes_halted"), False, final_status)
        record("M3-profile-verify-mismatch-write-enabled", {
            "verify_operation_id": verify_id,
            "verify_state": mismatch["state"],
            "verify_error": mismatch["error"]["code"],
            "writes_halted_after_mismatch": status["writes_halted"],
            "followup_execute_state": retry["state"],
            "writes_halted_after_followup": final_status["writes_halted"],
        })

    def test_unknown_m3_result_freezes_writes_and_ownership(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "unknown-profile", "type": "rectangle", "corner_mm": [0.0, 0.0],
            "width_mm": 10.0, "height_mm": 5.0,
        }])
        sketch_id = sketch["result"]["sketch_id"]
        _, _, first_prepared = self.prepare_extrude(
            helper, owner, epoch, sketch_id, mode="create", direction="positive",
            extent="finite", depth_mm=3.0,
        )
        _, _, second_prepared = self.prepare_extrude(
            helper, owner, epoch, sketch_id, mode="create", direction="negative",
            extent="finite", depth_mm=4.0,
        )
        executions_before = self.fake_counts()["m3_execution_calls"]
        self.harness_command("unknown-after-mutation")
        unknown_id, _, unknown = self.execute_plan(
            helper, owner, epoch, first_prepared["result"]["plan"]["plan_id"],
        )
        self.assertEqual(unknown.get("state"), "UNKNOWN", unknown)
        self.assertEqual(unknown.get("error", {}).get("code"), "UNKNOWN_RESULT", unknown)
        self.assertEqual(unknown.get("result", {}).get("mutation_state"), "UNKNOWN", unknown)
        self.assertIsNone(unknown.get("result", {}).get("changed"), unknown)
        replay = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", {
            "operation_id": unknown_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": first_prepared["result"]["plan"]["plan_id"],
        }))
        self.assertEqual(replay.get("state"), "UNKNOWN", replay)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        self.assertIs(status.get("model_write_enabled"), False, status)

        blocked = self.helper_call(helper, "operation.execute_plan", {
            "operation_id": "m3-blocked-execute-" + uuid.uuid4().hex,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": second_prepared["result"]["plan"]["plan_id"],
        })
        self.assertEqual(blocked.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked)
        returned = self.helper_call(helper, "ownership.return", {
            "ownership_id": owner, "binding_epoch": epoch,
        })
        self.assertEqual(returned.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", returned)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_execution_calls"), executions_before + 1, counts)
        self.assertEqual(counts.get("m3_body_count"), 1, counts)
        record("M3-unknown-freeze", {
            "injection": "unknown-after-mutation",
            "operation_id": unknown_id,
            "state": unknown["state"],
            "query_replay_state": replay["state"],
            "writes_halted": status["writes_halted"],
            "blocked_execute_error": blocked["error"]["code"],
            "return_error": returned["error"]["code"],
            "adapter_execute_calls_for_unknown_and_blocked": counts["m3_execution_calls"] - executions_before,
        })

    def test_timed_out_m3_operation_keeps_unknown_after_late_completion(self):
        ready = self.start_harness()
        helper = self.start_helper()
        grant = self.takeover(ready, helper=helper)
        owner, epoch = grant["ownership_id"], grant["binding_epoch"]
        self.addCleanup(self.release_fixture_operation)
        sketch = self.create_sketch(helper, owner, epoch, [{
            "id": "late-profile", "type": "rectangle", "corner_mm": [0.0, 0.0],
            "width_mm": 14.0, "height_mm": 6.0,
        }])
        _, _, prepared = self.prepare_extrude(
            helper, owner, epoch, sketch["result"]["sketch_id"], mode="create",
            direction="positive", extent="finite", depth_mm=5.0,
        )
        operation_id = "m3-late-execute-" + uuid.uuid4().hex
        self.harness_command("block-operation")
        accepted = successful_helper_result(self, self.helper_call(helper, "operation.execute_plan", {
            "operation_id": operation_id,
            "ownership_id": owner,
            "binding_epoch": epoch,
            "plan_id": prepared["result"]["plan"]["plan_id"],
        }))
        self.assertIn(accepted.get("state"), {"QUEUED", "RUNNING"}, accepted)
        started = self.harness_command("wait-operation-start")
        self.assertIs(started.get("observed"), True, started)

        aged = self.harness_command("age-running-m3-operation")
        self.assertIs(aged.get("observed"), True, aged)
        status = successful_helper_result(self, self.helper_call(helper, "bridge.status", {}))
        self.assertIs(status.get("writes_halted"), True, status)
        self.assertEqual(status.get("ownership_state"), "UNKNOWN_FROZEN", status)
        timed_out = self.get_operation(helper, operation_id)
        self.assertEqual(timed_out.get("state"), "UNKNOWN", timed_out)
        self.assertEqual(timed_out.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", timed_out)

        self.harness_command("release-operation")
        completed_late = None
        deadline = time.monotonic() + 5.0
        while time.monotonic() < deadline:
            completed_late = self.get_operation(helper, operation_id)
            if "late_completion" in completed_late:
                break
            time.sleep(0.02)
        self.assertIsInstance(completed_late, dict)
        self.assertEqual(completed_late.get("state"), "UNKNOWN", completed_late)
        self.assertEqual(completed_late.get("error", {}).get("code"), "OPERATION_EXECUTION_TIMEOUT", completed_late)
        late_result = completed_late.get("late_completion", {})
        self.assertEqual(late_result.get("mutation_state"), "APPLIED", completed_late)
        self.assertTrue(late_result.get("body_id"), completed_late)
        blocked_return = self.helper_call(helper, "ownership.return", {
            "ownership_id": owner, "binding_epoch": epoch,
        })
        self.assertEqual(blocked_return.get("error", {}).get("code"), "OPERATION_OUTCOME_UNKNOWN", blocked_return)
        counts = self.fake_counts()
        self.assertEqual(counts.get("m3_execution_calls"), 2, counts)
        self.assertEqual(counts.get("m3_body_count"), 1, counts)
        record("M3-timeout-late-completion", {
            "operation_id": operation_id,
            "timestamp_aged_by_harness": True,
            "timeout_state_after_late_completion": completed_late["state"],
            "late_adapter_state": late_result["mutation_state"],
            "writes_remain_halted": status["writes_halted"],
            "return_error": blocked_return["error"]["code"],
        })
