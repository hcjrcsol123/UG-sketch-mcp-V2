"""Create a unique F15 evidence map from recorded runs; never invokes NX."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "workspace/v2/evidence"
NATIVE = "f15-native-d7bf14a6282c4d1facf888de64500b68.json"
MCP = "f15-mcp-installed-20261103-2.json"
OUTPUT = EVIDENCE / "f15-validation-map-20261103.json"


def main():
    native = json.loads((EVIDENCE / NATIVE).read_text(encoding="utf-8"))
    installed = json.loads((EVIDENCE / MCP).read_text(encoding="utf-8"))
    assert native["passed"] is True and installed["passed"] is True
    native_cases = [{"case": row["case"], "status": row["status"]} for row in native["cases"]]
    installed_cases = [{"case": row["case_id"], "status": row["state"],
                        "reason": row.get("reason")} for row in installed["cases"]]
    assert len(native_cases) == 3 and all(row["status"] == "PASS" for row in native_cases)
    assert sum(row["status"] == "PASS" for row in installed_cases) == 12
    assert sum(row["status"] == "LIMITATION" for row in installed_cases) == 1
    output = {
        "scope": "F15 targeted evidence map, grouped by test layer; no reliability or compliance inference",
        "installed": {"source": MCP, "unique_pass_cases": 12, "limitations": 1,
                      "cases": installed_cases,
                      "independent_repeated_execute": "F15_unite_false; SUCCEEDED ID submitted again, result, part and revision unchanged",
                      "geometry_limit": "Per-output volume and box checks; no separate point containment via MCP"},
        "native_product": {"source": NATIVE, "unique_pass_cases": 3, "cases": native_cases,
                           "not_six_case_matrix": True},
        "offline": {"source": "f15-software-validation-20261103.log", "unique_tests_passed": 13,
                    "scope": "new F15 smoke controls and Python fake bridge protocol/UNKNOWN gating; no NX"},
        "api_exploration": {"source": "f15-api-probe-977446e4c13043579ef23b5a1dd8a9d6.json",
                            "classification": "native API exploration only, not product acceptance",
                            "flag_alias_probe": "f15-api-probe-3d33fe577a8641e18d4ceb1040c4bc48.json"},
        "prior_failures_preserved": ["f15-native-5c8bda844ba04546bfdbe197b47fa0cb.json",
                                     "f15-native-c15ca4b4c8be41509085fe4d4854e027.json",
                                     "f15-mcp-installed-20261103-1.json",
                                     "f15-api-probe-5325e8a4ef8f4d3598025d31d0f7cfa9.json",
                                     "f15-api-probe-75d6cf803853415ea232d71c56bcde00.json"],
        "shutdown": "f15-normal-exit-20261103-1.json",
        "sample_and_limits": "One installed fixture in NX2506; tolerances are CAD comparison thresholds, not physical calibration or reliability evidence. Existing Boolean result as later tool was safely rejected; prior result as later target and split untested."
    }
    with OUTPUT.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(output, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(str(OUTPUT))


if __name__ == "__main__":
    main()
