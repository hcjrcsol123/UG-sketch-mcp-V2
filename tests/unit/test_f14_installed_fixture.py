"""Offline controls for resolving F14 fixture bodies in the live session."""
import importlib.util
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src"))
spec = importlib.util.spec_from_file_location("f14_installed_smoke", ROOT / "tests" / "nx" / "v2" / "smoke-f14-mcp.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class F14FixtureResolutionTests(unittest.TestCase):
    def test_finds_current_identity_by_fixture_geometry(self):
        bodies = [{"journal_id": "live-target", "is_solid": True, "bbox_mm": [0, 0, 0, 80, 40, 20]},
                  {"journal_id": "live-sentinel", "is_solid": True, "bbox_mm": [100, 0, 0, 110, 10, 10]}]
        self.assertEqual(smoke.fixture_body_by_bounds({"bodies": bodies}, [0, 0, 0, 80, 40, 20])["journal_id"], "live-target")

    def test_ambiguous_and_absent_geometry_fail_closed(self):
        body = {"journal_id": "a", "is_solid": True, "bbox_mm": [0, 0, 0, 80, 40, 20]}
        with self.assertRaises(AssertionError):
            smoke.fixture_body_by_bounds({"bodies": [body, {**body, "journal_id": "b"}]}, body["bbox_mm"])
        with self.assertRaises(AssertionError):
            smoke.fixture_body_by_bounds({"bodies": [{**body, "is_solid": False}]}, body["bbox_mm"])

    def test_compact_installed_rejections_are_invalid_and_cover_eight_variants(self):
        from nx_sketch_mcp.server import McpServer, _InvalidToolArguments
        cases = smoke.schema_invalid_requests({"ownership_id": "owner", "binding_epoch": 1}, "revision", "body")
        self.assertEqual(len(cases), 14)
        self.assertEqual({(case["kind"], case["depth_mode"]) for case in cases[:8]},
                         {(kind, mode) for kind in smoke.FIELDS for mode in ("blind", "through")})
        for case in cases:
            with self.subTest(case=case), self.assertRaises(_InvalidToolArguments):
                McpServer._validate_prepare_hole(case)


if __name__ == "__main__":
    unittest.main()
