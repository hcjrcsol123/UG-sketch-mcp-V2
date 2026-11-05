"""F14 public protocol and rejection checks; NX-free, no geometry claim."""
from pathlib import Path
import math
import sys
import unittest
from unittest.mock import Mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))
from nx_sketch_mcp.config import config_from_object
from nx_sketch_mcp.server import McpServer, TOOLS


def request(kind="simple", mode="blind"):
    value = {"operation_id": "f14-op", "ownership_id": "owner", "binding_epoch": 1,
             "revision_id": "rev", "target_body_id": "body", "position_mm": [17, 13, 20],
             "direction": [0, 0, -2], "kind": kind, "depth_mode": mode}
    value.update({"simple": {"diameter_mm": 4},
                  "counterbore": {"diameter_mm": 4, "counterbore_diameter_mm": 8, "counterbore_depth_mm": 3},
                  "countersink": {"diameter_mm": 4, "countersink_diameter_mm": 8, "countersink_angle_deg": 90},
                  "threaded": {"thread_standard": "Metric Coarse", "thread_size": "M5 x 0.8", "radial_engage": "0.75", "pitch_mm": 0.8,
                               "hand": "left", "thread_depth_mm": 7}}[kind])
    if mode == "blind":
        value["hole_depth_mm"] = 10
    return value


class F14HoleProtocolTests(unittest.TestCase):
    def setUp(self):
        self.server = McpServer(config_from_object({}))
        self.addCleanup(self.server.close)
        self.server._bridge.call = Mock(return_value={"plan_id": "plan", "kind": "create_hole"})

    def invoke(self, args):
        return self.server._tools_call(1, {"name": "nx_prepare_hole", "arguments": args})

    def test_schema_lists_all_variants_and_marks_thread_as_nonphysical_geometry(self):
        tool = next(item for item in TOOLS if item["name"] == "nx_prepare_hole")
        variants = tool["inputSchema"]["oneOf"]
        self.assertEqual({(v["properties"]["kind"]["const"], v["properties"]["depth_mode"]["const"])
                          for v in variants}, {(k, d) for k in ("simple", "counterbore", "countersink", "threaded")
                                               for d in ("blind", "through")})
        self.assertTrue(all(not v["additionalProperties"] for v in variants))
        self.assertIn("thread", tool["description"].lower())

    def test_all_eight_valid_variants_pass_exact_inputs_and_normalized_direction(self):
        for kind in ("simple", "counterbore", "countersink", "threaded"):
            for mode in ("blind", "through"):
                with self.subTest(kind=kind, mode=mode):
                    self.server._bridge.call.reset_mock()
                    args = request(kind, mode)
                    response = self.invoke(args)
                    self.assertNotIn("error", response)
                    self.server._bridge.call.assert_called_once()
                    command, sent = self.server._bridge.call.call_args.args
                    self.assertEqual(command, "operation.prepare_hole")
                    self.assertEqual(sent, {**args, "direction": [0.0, 0.0, -1.0]})

    def test_invalid_requests_rejected_before_bridge(self):
        cases = [dict(request(), direction=[0, 0, 0]),
                 dict(request(), direction=[0, math.nan, 1]),
                 dict(request(), position_mm=[0, math.inf, 1]),
                 dict(request(), diameter_mm=True), dict(request(), diameter_mm=0),
                 dict(request(), unknown=1), dict(request(mode="through"), hole_depth_mm=10),
                 dict(request("counterbore"), counterbore_diameter_mm=4),
                 dict(request("counterbore"), counterbore_depth_mm=10),
                 dict(request("countersink"), countersink_diameter_mm=4),
                 dict(request("countersink"), countersink_angle_deg=180),
                 dict(request("countersink"), countersink_angle_deg=1),
                 dict(request("threaded"), hand="clockwise"),
                 dict(request("threaded"), pitch_mm=-1),
                 dict(request("threaded"), thread_depth_mm=11),
                 dict(request("threaded"), thread_standard=" "), dict(request("threaded"), radial_engage=" "),
                 dict(request(), radial_engage="0.75")]
        for args in cases:
            with self.subTest(args=args):
                self.server._bridge.call.reset_mock()
                response = self.invoke(args)
                self.assertEqual(response.get("error", {}).get("code"), -32602, response)
                self.server._bridge.call.assert_not_called()


if __name__ == "__main__":
    unittest.main()
