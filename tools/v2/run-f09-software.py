"""Run the F09 software suite and only directly affected MCP contract checks."""
from __future__ import annotations

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
    os.chdir(ROOT)
    sys.path[:0] = [
        str(ROOT / "tests" / "m2"),
        str(ROOT / "tests" / "unit"),
        str(ROOT / "src"),
    ]
    output = ROOT / "workspace" / "v2" / "evidence" / ("f09-software-" + uuid.uuid4().hex)
    output.mkdir(parents=True, exist_ok=False)
    os.environ["M2_PUBLIC_EVIDENCE_PATH"] = str(output / "details.jsonl")
    os.environ["PYTHONDONTWRITEBYTECODE"] = "1"
    os.environ["PYTHONUTF8"] = "1"

    local_modules = [
        "test_f09_sketch_pattern_protocol",
        "test_f09_sketch_pattern_lifecycle",
        "test_f09_pattern_geometry",
        "test_f09_mcp_smoke",
        "test_f09_helper_serialization",
    ]
    suite = unittest.TestSuite()
    ids: list[str] = []
    for module_name in local_modules:
        module = importlib.import_module(module_name)
        for cls in vars(module).values():
            if (not isinstance(cls, type) or not issubclass(cls, unittest.TestCase)
                    or cls.__module__ != module_name):
                continue
            for method_name in sorted(cls.__dict__):
                if method_name.startswith("test_") and callable(getattr(cls, method_name)):
                    case = cls(method_name)
                    ids.append(case.id())
                    suite.addTest(case)

    # These existing stdio tests changed only because F09 adds two tools,
    # capabilities and two operation routes. Other M2/M3 behavior is excluded.
    module = importlib.import_module("test_mcp_stdio")
    selected_stdio = {
        "test_static_capabilities_are_discoverable_without_bridge_config",
        "test_m2_tools_publish_confirmation_and_required_argument_schemas",
        "test_ownership_and_model_tools_route_arguments_to_bridge_commands",
    }
    for cls in vars(module).values():
        if isinstance(cls, type) and issubclass(cls, unittest.TestCase) and cls.__module__ == module.__name__:
            for method_name in sorted(selected_stdio.intersection(cls.__dict__)):
                case = cls(method_name)
                ids.append(case.id())
                suite.addTest(case)

    if not ids:
        raise RuntimeError("No F09 test methods were selected")
    started = utc_now()
    test_log = output / "unittest.txt"
    with test_log.open("x", encoding="utf-8") as stream:
        result = unittest.TextTestRunner(stream=stream, verbosity=2).run(suite)
    summary = {
        "started_utc": started,
        "finished_utc": utc_now(),
        "scope": "F09 protocol, STA lifecycle, independent geometry/oracle helpers, MCP smoke helpers, and directly affected stdio checks",
        "nx_started": False,
        "nx_model_validation": False,
        "tests_run": result.testsRun,
        "failures": len(result.failures),
        "errors": len(result.errors),
        "skipped": len(result.skipped),
        "passed": result.wasSuccessful() and not result.skipped,
        "test_ids": ids,
        "output_directory": str(output),
        "test_log": str(test_log),
        "runtime_details": str(output / "details.jsonl"),
        "legacy_acceptance_methods_selected": False,
    }
    (output / "summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8",
    )
    print(json.dumps({key: value for key, value in summary.items() if key != "test_ids"}, ensure_ascii=False))
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
