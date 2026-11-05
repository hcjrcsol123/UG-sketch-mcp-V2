"""Run only F11 offline protocol, fake lifecycle, oracle, and helper checks; never starts NX."""
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


def _test_methods(module: object) -> list[unittest.TestCase]:
    result: list[unittest.TestCase] = []
    module_name = getattr(module, "__name__", "")
    for cls in vars(module).values():
        if (not isinstance(cls, type) or not issubclass(cls, unittest.TestCase)
                or cls.__module__ != module_name):
            continue
        for method_name in sorted(cls.__dict__):
            if method_name.startswith("test_") and callable(getattr(cls, method_name)):
                result.append(cls(method_name))
    return result


def main() -> int:
    os.chdir(ROOT)
    sys.path[:0] = [str(ROOT / "tests" / "m2"), str(ROOT / "tests" / "unit"),
                    str(ROOT / "src")]
    output = ROOT / "workspace" / "v2" / "evidence" / ("f11-software-" + uuid.uuid4().hex)
    output.mkdir(parents=True, exist_ok=False)
    os.environ["M2_PUBLIC_EVIDENCE_PATH"] = str(output / "details.jsonl")
    os.environ["PYTHONDONTWRITEBYTECODE"] = "1"
    os.environ["PYTHONUTF8"] = "1"

    software_modules = [
        "test_f11_sketch_reference_conversion_protocol",
        "test_f11_sketch_reference_conversion_lifecycle",
        "test_f11_reference_conversion_geometry",
        "test_f11_mcp_smoke",
    ]
    suite = unittest.TestSuite()
    group_members: dict[str, list[str]] = {}
    ids: list[str] = []
    for module_name in software_modules:
        module = importlib.import_module(module_name)
        cases = _test_methods(module)
        group_members[module_name] = [case.id() for case in cases]
        ids.extend(group_members[module_name])
        suite.addTests(cases)

    # Only the existing discovery/routing checks directly affected by adding F11
    # tools and capabilities are selected; no F01-F10 regression suite is run.
    module = importlib.import_module("test_mcp_stdio")
    selected_stdio = {
        "test_static_capabilities_are_discoverable_without_bridge_config",
        "test_ownership_and_model_tools_route_arguments_to_bridge_commands",
    }
    stdio_ids: list[str] = []
    for cls in vars(module).values():
        if (not isinstance(cls, type) or not issubclass(cls, unittest.TestCase)
                or cls.__module__ != module.__name__):
            continue
        for method_name in sorted(selected_stdio.intersection(cls.__dict__)):
            case = cls(method_name)
            stdio_ids.append(case.id())
            suite.addTest(case)
    group_members["directly_affected_stdio"] = stdio_ids
    ids.extend(stdio_ids)

    if not ids:
        raise RuntimeError("No F11 no-NX test methods were selected")
    started = utc_now()
    test_log = output / "unittest.txt"
    with test_log.open("x", encoding="utf-8") as stream:
        result = unittest.TextTestRunner(stream=stream, verbosity=2).run(suite)

    summary = {
        "started_utc": started,
        "finished_utc": utc_now(),
        "scope": (
            "F11 Python protocol/schema, STA fake lifecycle, independent reference-state oracle, "
            "installed smoke helpers, and directly affected MCP stdio checks"
        ),
        "evidence_layer": "offline software only; no NX, installed MCP, native Journal, or model was run",
        "nx_started": False,
        "nx_model_validation": False,
        "tests_run": result.testsRun,
        "failures": len(result.failures),
        "errors": len(result.errors),
        "skipped": len(result.skipped),
        "passed": result.wasSuccessful() and not result.skipped,
        "group_test_ids": group_members,
        "test_ids": ids,
        "output_directory": str(output),
        "test_log": str(test_log),
        "runtime_details": str(output / "details.jsonl"),
        "native_probe_and_acceptance_counted": False,
        "legacy_acceptance_methods_selected": False,
    }
    (output / "summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8",
    )
    print(json.dumps({key: value for key, value in summary.items()
                      if key not in {"test_ids", "group_test_ids"}}, ensure_ascii=False))
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
