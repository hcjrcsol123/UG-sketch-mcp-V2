"""Check the independent F12 profile/Boolean oracle against fixed math cases."""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
ORACLE_PATH = PROJECT_ROOT / "tools" / "v2" / "f12_extrusion_oracle.py"
SPEC = importlib.util.spec_from_file_location("f12_extrusion_oracle", ORACLE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"cannot import F12 oracle at {ORACLE_PATH}")
ORACLE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = ORACLE
SPEC.loader.exec_module(ORACLE)


class F12ExtrusionOracleTests(unittest.TestCase):
    def test_concave_outer_hole_and_disjoint_island_ignore_winding(self):
        outer = ORACLE.Loop(((0, 0), (20, 0), (20, 12), (12, 12), (12, 18), (0, 18), (0, 0)))
        hole = ORACLE.Loop(((2, 2), (6, 2), (6, 7), (2, 7), (2, 2)))
        island = ORACLE.Loop(((24, 0), (29, 0), (29, 4), (24, 4), (24, 0)))
        loops = (outer, hole, island)
        reversed_loops = tuple(
            ORACLE.Loop(tuple(reversed(loop.points_mm)), loop.exact_area_mm2, loop.label)
            for loop in reversed(loops)
        )

        self.assertEqual(ORACLE.loop_nesting_depths(loops), [0, 1, 0])
        self.assertEqual(ORACLE.net_profile_area_mm2(loops), 312.0)
        self.assertEqual(ORACLE.net_profile_area_mm2(reversed_loops), 312.0)

    def test_rotated_ellipse_uses_exact_area_not_sampled_polygon_area(self):
        ellipse = ORACLE.ellipse_loop((17.0, -4.0), 6.0, 2.5, 37.0)
        expected = 15.0 * 3.141592653589793
        self.assertAlmostEqual(ORACLE.loop_area_mm2(ellipse), expected, places=12)
        self.assertAlmostEqual(ORACLE.net_profile_area_mm2((ellipse,)), expected, places=12)
        # The sample polyline approximates the analytic ellipse from the inside.
        self.assertLess(abs(ORACLE.signed_polygon_area_mm2(ellipse.points_mm)), expected)

    def test_ellipse_with_inner_loop_uses_closed_endpoint_and_subtracts_hole(self):
        ellipse = ORACLE.ellipse_loop((0.0, 0.0), 5.0, 4.0, 19.0, samples=64)
        hole = ORACLE.Loop(((-1.0, -1.0), (1.0, -1.0), (1.0, 1.0),
                            (-1.0, 1.0), (-1.0, -1.0)))
        self.assertEqual(ellipse.points_mm[0], ellipse.points_mm[-1])
        self.assertEqual(ORACLE.loop_nesting_depths((ellipse, hole)), [0, 1])
        self.assertAlmostEqual(ORACLE.net_profile_area_mm2((ellipse, hole)),
                               20.0 * 3.141592653589793 - 4.0, places=12)

    def test_sampled_closed_spline_area_is_orientation_invariant(self):
        # A native spline acceptance can provide UF curve samples at a fixed
        # resolution; this synthetic ellipse exercises signed Green-area math.
        loop = ORACLE.ellipse_loop((0.0, 0.0), 4.0, 2.0, 0.0, samples=720)
        signed = ORACLE.signed_polygon_area_mm2(loop.points_mm)
        reversed_signed = ORACLE.signed_polygon_area_mm2(tuple(reversed(loop.points_mm)))
        self.assertGreater(signed, 0)
        self.assertAlmostEqual(signed, -reversed_signed, places=10)
        self.assertLess(abs(abs(signed) - ORACLE.loop_area_mm2(loop)), 0.0004)

    def test_signed_finite_interval_and_legacy_depth_have_consistent_units(self):
        self.assertEqual(ORACLE.swept_volume_mm3(100.0, -3.0, 5.0), 800.0)
        self.assertEqual(ORACLE.legacy_depth_volume_mm3(100.0, 8.0), 800.0)
        with self.assertRaises(ValueError):
            ORACLE.swept_volume_mm3(100.0, 5.0, 5.0)

    def test_direction_and_frame_change_world_bounds_not_volume(self):
        profile_bounds = (0.0, 4.0, 0.0, 2.0)
        common = dict(
            profile_bounds_mm=profile_bounds,
            origin_mm=(10.0, 20.0, 30.0),
            x_axis=(1.0, 0.0, 0.0),
            y_axis=(0.0, 1.0, 0.0),
            normal=(0.0, 0.0, 1.0),
            start_offset_mm=-2.0,
            end_offset_mm=3.0,
        )
        positive = ORACLE.swept_world_bounds_mm(direction="positive", **common)
        negative = ORACLE.swept_world_bounds_mm(direction="negative", **common)
        self.assertEqual(positive, ((10.0, 20.0, 28.0), (14.0, 22.0, 33.0)))
        self.assertEqual(negative, ((10.0, 20.0, 27.0), (14.0, 22.0, 32.0)))
        self.assertEqual(ORACLE.box_volume_mm3(positive), ORACLE.box_volume_mm3(negative))

    def test_boolean_box_oracle_counts_intersection_and_split_remainder(self):
        target = ((0.0, 0.0, 0.0), (20.0, 4.0, 4.0))
        split_slot = ((9.0, -1.0, 0.0), (11.0, 5.0, 4.0))
        self.assertEqual(ORACLE.box_volume_mm3(target), 320.0)
        self.assertEqual(ORACLE.box_intersection_mm3(target, split_slot), 32.0)
        self.assertEqual(ORACLE.boolean_box_volume_mm3("subtract", target, split_slot), 288.0)
        self.assertEqual(ORACLE.boolean_box_volume_mm3("unite", target, split_slot), 336.0)


if __name__ == "__main__":
    unittest.main()
