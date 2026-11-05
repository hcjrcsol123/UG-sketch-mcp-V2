"""Root-only read of retained simple-hole Section after r03 definition mismatch."""
import json
import os
import sys
import uuid
import NXOpen
import NXOpen.Features
import NXOpen.UF

ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
sys.path.insert(0, os.path.join(ROOT, "tools", "v2"))
import importlib.util
spec = importlib.util.spec_from_file_location("f14_readback_helpers", os.path.join(ROOT, "tools/v2/f14-readback-probe.py"))
helpers = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helpers)


def main():
    session = NXOpen.Session.GetSession()
    if session.Parts.Work is not None:
        raise RuntimeError("Requires empty session")
    path = os.path.join(ROOT, "workspace/v2/evidence", "f14-section-readback-" + uuid.uuid4().hex + ".json")
    model = os.path.join(ROOT, "workspace/v2/models/F14-api-2d5d8fbf020744f4b287e88a40fb3e02-0.prt")
    report = {"scope": "read-only Section mapping, no model creation/save", "model": model, "completed": False}
    part = None
    builder = None
    try:
        part, status = session.Parts.OpenDisplay(model)
        status.Dispose()
        feature = next(f for f in part.Features if isinstance(f, NXOpen.Features.HolePackage))
        builder = part.Features.CreateHolePackageBuilder(feature)
        section = builder.HolePosition
        helpers.observe(report, "ask_rules_and_collected_raw", section.AskRulesAndCollectedEntities)
        collected, rules, indices = section.AskRulesAndCollectedEntities()
        helpers.observe(report, "collected_coordinates", lambda: [p.Coordinates for p in collected])
        helpers.observe(report, "section_output", section.GetOutputCurves)
        helpers.observe(report, "point_coordinates", lambda: [p.Coordinates for p in section.GetOutputCurves()])
        uf = NXOpen.UF.UFSession.GetUFSession()
        report["smart_parent_evidence"] = []
        for point in section.GetOutputCurves():
            parent_row = {"output": helpers.ref(point)}
            report["smart_parent_evidence"].append(parent_row)
            helpers.observe(parent_row, "all_direct_parents", lambda: uf.So.AskParents(point.Tag, 8))
            helpers.observe(parent_row, "all_recursive_parents", lambda: uf.So.AskParents(point.Tag, 24))
        data = section.GetSectionData()
        report["section_data_count"] = len(data)
        report["rules"] = []
        for entry in data:
            for rule in entry.GetRules():
                row = {"runtime_type": type(rule).__name__}
                report["rules"].append(row)
                helpers.observe(row, "type", lambda: rule.Type)
                helpers.observe(row, "curve_dumb_equal", lambda: rule.Type == NXOpen.SelectionIntentRule.RuleType.CurveDumb)
        report["completed"] = True
    except Exception:
        import traceback
        report["error"] = traceback.format_exc()
    finally:
        if builder is not None:
            helpers.observe(report, "builder_destroyed", builder.Destroy)
        if part is not None:
            helpers.observe(report, "closed", lambda: part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue, NXOpen.BasePart.CloseModified.CloseModified, None))
        with open(path, "w", encoding="utf-8") as stream:
            json.dump(report, stream, indent=2, allow_nan=False)
    print(path)


if __name__ == "__main__":
    main()
