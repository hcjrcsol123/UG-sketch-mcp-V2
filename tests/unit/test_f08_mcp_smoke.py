"""Offline contract checks for the F08 installed-MCP smoke helpers."""

from __future__ import annotations

import importlib.util
from copy import deepcopy
from pathlib import Path
import sys
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
SMOKE_PATH = PROJECT_ROOT / "tools" / "v2" / "f08-mcp-smoke.py"
SPEC = importlib.util.spec_from_file_location("f08_mcp_smoke", SMOKE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("could not load F08 MCP smoke helpers")
SMOKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE)


def _records() -> list[dict]:
    return [
        {
            "journal_id": "offset-horizontal",
            "tag": "1001",
            "object_type": "Line",
            "start_mm": [0.0, 2.0, 0.0],
            "end_mm": [8.0, 2.0, 0.0],
        },
        {
            "journal_id": "offset-vertical",
            "tag": "1002",
            "object_type": "Line",
            "start_mm": [8.0, 2.0, 0.0],
            "end_mm": [8.0, 10.0, 0.0],
        },
    ]


def _native_evidence() -> dict:
    records = _records()
    offset_geometry = {
        "valid": True,
        "output_closed": False,
        "requested_closed": False,
        "requested_side": "left",
        "distance_mm": 2.0,
        "output_curve_records": records,
        "per_source_curve": [
            {
                "source_curve_id": "source-horizontal",
                "source_curve_tag": "2001",
                "unique_output_match": True,
                "output_curve_index": 0,
                "measured_signed_offset_mm": 2.0,
                "offset_residual_mm": 0.0,
            },
            {
                "source_curve_id": "source-vertical",
                "source_curve_tag": "2002",
                "unique_output_match": True,
                "output_curve_index": 1,
                "measured_signed_offset_mm": 2.0,
                "offset_residual_mm": 0.0,
            },
        ],
        "topology": {
            "valid_chain": True,
            "closed": False,
            "open_end_count": 2,
            "component_count": 1,
            "self_intersection_count": 0,
        },
    }
    return {
        "offset_geometry": offset_geometry,
        "output_closed": False,
        "output_curve_records": records,
        "checks": {"offset_geometry": offset_geometry},
    }


def _compound_status(operation_id: str = "f08-offline-operation") -> dict:
    live_geometry = _native_evidence()
    verify_plan_result = {
        "kind": "edit_sketch_offset",
        "output_closed": False,
        "output_curve_records": _records(),
        "evidence": {"live_geometry": live_geometry},
    }
    required_checks = {
        "offset_geometry", "source_curves", "sketch_solver_status", "sketch_constraints",
        "sketch_frames", "reference_status", "non_target_geometry", "history", "bodies",
    }
    checks = {
        name: {"status": "VERIFIED", "evidence": live_geometry if name == "offset_geometry" else {}}
        for name in required_checks
    }
    result = {
        "operation_id": operation_id,
        "kind": "edit_sketch_offset",
        "plan_id": "f08-offline-plan",
        "sketch_id": "f08-offline-sketch",
        "mutation_state": "APPLIED",
        "combined_edit": True,
        "verified": True,
        "output_closed": False,
        "output_curve_records": _records(),
        "verification": {"verified": True, "checks": checks, "evidence": verify_plan_result},
        "evidence": verify_plan_result,
    }
    operation = {"operation_id": operation_id, "state": "SUCCEEDED", "result": result}
    return {
        "operation_id": operation_id,
        "state": "SUCCEEDED",
        "success": True,
        "verified": True,
        "operation": operation,
    }


class F08McpSmokeHelperTests(unittest.TestCase):
    def test_real_tool_schema_uses_numeric_exclusive_zero_bound(self):
        sys.path.insert(0, str(PROJECT_ROOT / "src"))
        from nx_sketch_mcp.server import TOOLS

        self.assertEqual(len(SMOKE._check_offset_schemas(TOOLS)), 2)
        for invalid in (None, False, -1):
            with self.subTest(exclusive_minimum=invalid):
                tools = deepcopy(TOOLS)
                for tool in tools:
                    if tool["name"] in {"nx_prepare_sketch_offset", "nx_edit_sketch_offset"}:
                        tool["inputSchema"]["properties"]["distance_mm"]["exclusiveMinimum"] = invalid
                with self.assertRaises(AssertionError):
                    SMOKE._check_offset_schemas(tools)

    def test_prepare_preview_uses_counted_native_source_records(self):
        curve_ids = {"horizontal": "source-horizontal", "vertical": "source-vertical"}
        plan = {
            "plan_id": "f08-offline-plan",
            "sketch_id": "f08-offline-sketch",
            "revision_id": "f08-offline-revision",
            "curve_chain": [
                {"curve_id": curve_ids["horizontal"], "direction": "forward"},
                {"curve_id": curve_ids["vertical"], "direction": "forward"},
            ],
            "distance_mm": 2.0,
            "side": "left",
            "retain_original": True,
            "join_mode": "extension",
            "source_chain_closed": False,
            "preview": {
                "preview_type": "source_chain_and_requested_offset",
                "source_chain": {
                    "count": 2,
                    "curves": [
                        {"journal_id": curve_ids["horizontal"], "direction": "forward",
                         "object_type": "Line", "start_mm": [0, 0, 0], "end_mm": [10, 0, 0]},
                        {"journal_id": curve_ids["vertical"], "direction": "forward",
                         "object_type": "Line", "start_mm": [10, 0, 0], "end_mm": [10, 10, 0]},
                    ],
                },
                "source_chain_closed": False,
                "distance_mm": 2.0,
                "side": "left",
                "retain_original": True,
                "join_mode": "extension",
                "native_geometry_evaluation": "deferred_until_atomic_execute",
            },
        }

        def prepare(name, arguments):
            self.assertEqual(name, "nx_prepare_sketch_offset")
            self.assertEqual(arguments["curve_chain"], plan["curve_chain"])
            return {"plan": plan}

        prepared = SMOKE._prepare_sketch_chain(
            prepare, {"ownership_id": "owner", "binding_epoch": 1},
            "f08-offline-revision", "f08-offline-sketch", curve_ids,
        )
        self.assertEqual(prepared["plan"], plan)

    def test_prepare_preview_rejects_legacy_generic_source_chain(self):
        curve_ids = {"horizontal": "source-horizontal", "vertical": "source-vertical"}
        plan = {
            "plan_id": "f08-offline-plan", "sketch_id": "f08-offline-sketch",
            "revision_id": "f08-offline-revision",
            "curve_chain": [
                {"curve_id": curve_ids["horizontal"], "direction": "forward"},
                {"curve_id": curve_ids["vertical"], "direction": "forward"},
            ],
            "distance_mm": 2.0, "side": "left", "retain_original": True,
            "join_mode": "extension", "source_chain_closed": False,
            "preview": {
                "preview_type": "source_chain_and_requested_offset",
                "source_chain": [
                    {"curve_id": curve_ids["horizontal"], "direction": "forward"},
                    {"curve_id": curve_ids["vertical"], "direction": "forward"},
                ],
                "source_chain_closed": False, "distance_mm": 2.0, "side": "left",
                "retain_original": True, "join_mode": "extension",
                "native_geometry_evaluation": "deferred_until_atomic_execute",
            },
        }

        with self.assertRaisesRegex(AssertionError, "counted native-curve record"):
            SMOKE._prepare_sketch_chain(
                lambda *_: {"plan": plan},
                {"ownership_id": "owner", "binding_epoch": 1},
                "f08-offline-revision", "f08-offline-sketch", curve_ids,
            )

    def test_queued_to_wrapped_success_unwraps_once_and_checks_real_compound_evidence(self):
        operation_id = "f08-queued-to-success"
        initial = {"operation_id": operation_id, "state": "QUEUED", "operation": {"state": "QUEUED"}}
        completed = _compound_status(operation_id)
        status = SMOKE._completed_edit_status(initial, completed)

        self.assertEqual(status["operation"], completed["operation"])
        self.assertNotIn("operation", status["operation"])
        self.assertEqual(status["state"], "SUCCEEDED")
        self.assertTrue(status["success"])
        validated = SMOKE._check_edit_status(status, operation_id, "f08-offline-sketch")
        self.assertEqual(validated["output_curve_records"], _records())

    def test_unknown_query_to_wrapped_known_terminal_unwraps_once(self):
        operation_id = "f08-unknown-query-success"
        initial = {
            "operation_id": operation_id,
            "state": "UNKNOWN",
            "outcome_unknown": True,
            "operation": {"state": "UNKNOWN", "result": {"verified": False}},
        }
        queried = _compound_status(operation_id)
        status = SMOKE._completed_edit_status(initial, queried)

        self.assertEqual(status["operation"], queried["operation"])
        self.assertNotIn("operation", status["operation"])
        self.assertEqual(status["state"], "SUCCEEDED")
        self.assertTrue(status["success"])
        SMOKE._check_edit_status(status, operation_id, "f08-offline-sketch")

    def test_native_execution_and_verifyplan_evidence_shapes_are_checked(self):
        direct_execution_evidence = _native_evidence()
        offset = SMOKE._check_native_offset_evidence(direct_execution_evidence, "execute evidence")
        self.assertIs(offset, direct_execution_evidence["offset_geometry"])

        status = _compound_status()
        result = status["operation"]["result"]
        verify_evidence = result["verification"]["evidence"]["evidence"]["live_geometry"]
        self.assertEqual(verify_evidence, direct_execution_evidence)
        SMOKE._check_edit_status(status, "f08-offline-operation", "f08-offline-sketch")


if __name__ == "__main__":
    unittest.main()
