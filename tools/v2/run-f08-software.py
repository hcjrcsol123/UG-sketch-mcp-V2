"""Run F08 tests plus directly affected protocol/lifecycle checks, without NX."""
from __future__ import annotations

import argparse
import datetime
import importlib
import json
import os
from pathlib import Path
import sys
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]


def utc_now() -> str:
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--f08-only", action="store_true", help="skip the directly affected existing checks")
    args = parser.parse_args()
    os.chdir(ROOT)
    sys.path[:0] = [str(ROOT / "tests" / "m2"), str(ROOT / "tests" / "unit"), str(ROOT / "src")]
    out = ROOT / "workspace" / "v2" / "evidence" / ("f08-software-" + uuid.uuid4().hex)
    out.mkdir(parents=True, exist_ok=False)
    os.environ["M2_PUBLIC_EVIDENCE_PATH"] = str(out / "details.jsonl")
    os.environ["PYTHONDONTWRITEBYTECODE"] = "1"
    os.environ["PYTHONUTF8"] = "1"
    modules = ["test_f08_sketch_offset_protocol", "test_f08_sketch_offset_lifecycle", "test_f08_offset_geometry", "test_f08_mcp_smoke"]
    if not args.f08_only:
        modules += ["test_f07_sketch_corner_protocol", "test_f07_sketch_corner_lifecycle"]
    suite = unittest.TestSuite()
    ids: list[str] = []
    # Deliberately select only methods declared by local test classes. The old
    # lifecycle helper base contains unrelated test methods, which are excluded.
    for name in modules:
        module = importlib.import_module(name)
        for cls in vars(module).values():
            if not isinstance(cls, type) or not issubclass(cls, unittest.TestCase) or cls.__module__ != name:
                continue
            for method in sorted(cls.__dict__):
                if method.startswith("test_") and callable(getattr(cls, method)):
                    case = cls(method)
                    ids.append(case.id())
                    suite.addTest(case)
    if not args.f08_only:
        module = importlib.import_module("test_mcp_stdio")
        selected = {
            "test_static_capabilities_are_discoverable_without_bridge_config",
            "test_m2_tools_publish_confirmation_and_required_argument_schemas",
            "test_legacy_operation_poll_preserves_records_and_uses_bridge_unknown_gate",
            "test_execute_unknown_client_gate_waits_for_queried_terminal_result",
        }
        for cls in vars(module).values():
            if isinstance(cls, type) and issubclass(cls, unittest.TestCase) and cls.__module__ == module.__name__:
                for method in sorted(selected.intersection(cls.__dict__)):
                    case = cls(method)
                    ids.append(case.id())
                    suite.addTest(case)
    if not ids:
        raise RuntimeError("No F08 test methods were selected")
    start = utc_now()
    with (out / "unittest.txt").open("x", encoding="utf-8") as stream:
        result = unittest.TextTestRunner(stream=stream, verbosity=2).run(suite)
    summary = {
        "started_utc": start, "finished_utc": utc_now(),
        "scope": "F08 plus directly affected protocol/lifecycle" if not args.f08_only else "F08 only",
        "nx_started": False, "nx_model_validation": False,
        "tests_run": result.testsRun, "failures": len(result.failures),
        "errors": len(result.errors), "skipped": len(result.skipped),
        "passed": result.wasSuccessful() and not result.skipped,
        "test_ids": ids, "output_directory": str(out),
        "test_log": str(out / "unittest.txt"), "runtime_details": str(out / "details.jsonl"),
    }
    (out / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in summary.items() if key != "test_ids"}, ensure_ascii=False))
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
