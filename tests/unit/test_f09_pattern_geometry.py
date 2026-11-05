"""Independent arithmetic checks for F09 expected-geometry calculations."""

from __future__ import annotations

import importlib.util
import math
from pathlib import Path
import unittest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
SMOKE_PATH = PROJECT_ROOT / "tools" / "v2" / "f09-mcp-smoke.py"
SPEC = importlib.util.spec_from_file_location("f09_mcp_smoke", SMOKE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("could not load F09 independent geometry helpers")
SMOKE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SMOKE)


class F09PatternGeometryOracleTests(unittest.TestCase):
    def test_linear_and_rectangular_positions_use_inclusive_seed_indexing(self):
        self.assertEqual(SMOKE.linear_position([2.0, 3.0], [3.0, 4.0], 5.0, 0), [2.0, 3.0])
        self.assertEqual(SMOKE.linear_position([2.0, 3.0], [3.0, 4.0], 5.0, 2), [8.0, 11.0])
        self.assertEqual(
            SMOKE.rectangular_position([1.0, -1.0], [2.0, 0.0], [0.0, 3.0], 4.0, 2.0, 2, 3),
            [9.0, 5.0],
        )

    def test_circular_position_rotates_about_the_requested_nonorigin_center(self):
        actual = SMOKE.rotate_about([5.0, -1.0], [2.0, -1.0], -90.0)
        self.assertAlmostEqual(actual[0], 2.0, places=12)
        self.assertAlmostEqual(actual[1], -4.0, places=12)

    def test_oblique_mirror_uses_the_named_axis_line_not_world_axes(self):
        actual = SMOKE.reflect_across_line([3.0, 1.0], [1.0, 1.0], [3.0, 4.0])
        self.assertAlmostEqual(actual[0], 0.44, places=12)
        self.assertAlmostEqual(actual[1], 2.92, places=12)

    def test_tilted_frame_is_orthonormal_and_maps_local_coordinates_to_world(self):
        frame = SMOKE.build_frame([10.0, 20.0, 30.0], [1.0, 2.0, 3.0], [1.0, -1.0, 0.5])
        x_axis, y_axis, normal = frame["x_axis"], frame["y_axis"], frame["normal"]
        self.assertAlmostEqual(sum(value * value for value in x_axis), 1.0, places=12)
        self.assertAlmostEqual(sum(value * value for value in y_axis), 1.0, places=12)
        self.assertAlmostEqual(sum(value * value for value in normal), 1.0, places=12)
        self.assertAlmostEqual(sum(x_axis[i] * y_axis[i] for i in range(3)), 0.0, places=12)
        self.assertAlmostEqual(sum(x_axis[i] * normal[i] for i in range(3)), 0.0, places=12)
        world = SMOKE.point_to_world(frame, [4.0, -3.0])
        delta = [world[i] - frame["origin_mm"][i] for i in range(3)]
        self.assertAlmostEqual(math.sqrt(sum(value * value for value in delta)), 5.0, places=12)

    def test_arc_oracle_rejects_complement_with_same_center_radius_and_endpoints(self):
        source = SMOKE.arc_sample_points([0.0, 0.0], 2.0, 0.0, 90.0)
        reflected = {
            name: [point[0], -point[1]]
            for name, point in source.items()
        }
        expected = {
            "center_xy": [0.0, 0.0],
            "radius_mm": 2.0,
            "sweep_deg": -90.0,
            "sample_points_xy": reflected,
        }
        correct = {
            "center_xy": [0.0, 0.0], "radius_mm": 2.0,
            "start_angle_deg": 270.0, "sweep_deg": 90.0,
        }
        complementary = {
            "center_xy": [0.0, 0.0], "radius_mm": 2.0,
            "start_angle_deg": 0.0, "sweep_deg": 270.0,
        }
        self.assertTrue(SMOKE.arc_signature_matches(correct, expected))
        self.assertFalse(SMOKE.arc_signature_matches(complementary, expected))
        self.assertFalse(SMOKE.arc_signature_matches({**correct, "sweep_deg": 450.0}, expected))
        self.assertFalse(SMOKE.arc_signature_matches({**correct, "sweep_deg": 0.0}, expected))

    def test_full_circle_is_a_separate_valid_arc_extent(self):
        samples = SMOKE.arc_sample_points([1.0, 2.0], 3.0, 0.0, 360.0)
        expected = {
            "center_xy": [1.0, 2.0], "radius_mm": 3.0,
            "sweep_deg": 360.0, "sample_points_xy": samples,
        }
        full_circle = {
            "center_xy": [1.0, 2.0], "radius_mm": 3.0,
            "start_angle_deg": 17.0, "sweep_deg": -360.0,
        }
        partial_arc = {**full_circle, "sweep_deg": -359.0}
        self.assertTrue(SMOKE.arc_signature_matches(full_circle, expected))
        self.assertFalse(SMOKE.arc_signature_matches(partial_arc, expected))

    def test_oracles_reject_degenerate_axes_and_nonfinite_values(self):
        with self.assertRaises(ValueError):
            SMOKE.linear_position([0.0, 0.0], [0.0, 0.0], 5.0, 1)
        with self.assertRaises(ValueError):
            SMOKE.rectangular_position([0.0, 0.0], [1.0, 0.0], [1.0, 0.0], 2.0, 2.0, 1, 1)
        with self.assertRaises(ValueError):
            SMOKE.rotate_about([1.0, 0.0], [0.0, 0.0], float("inf"))

    def test_extreme_finite_direction_is_normalized_without_overflow_or_silent_zero(self):
        actual = SMOKE.linear_position([0.0, 0.0], [1.0e308, 1.0e308], 2.0, 1)
        self.assertAlmostEqual(actual[0], math.sqrt(2.0), places=12)
        self.assertAlmostEqual(actual[1], math.sqrt(2.0), places=12)
        with self.assertRaises(ValueError):
            SMOKE.linear_position([0.0, 0.0], [1.0, 0.0], 1.0e308, 2)

    def test_large_finite_circular_step_is_reduced_before_trigonometry(self):
        step = 1.0e308
        reduced = math.fmod(step, 360.0)
        expected = [math.cos(math.radians(reduced)), math.sin(math.radians(reduced))]
        actual = SMOKE.rotate_about([1.0, 0.0], [0.0, 0.0], step)
        self.assertAlmostEqual(actual[0], expected[0], places=12)
        self.assertAlmostEqual(actual[1], expected[1], places=12)


if __name__ == "__main__":
    unittest.main()
