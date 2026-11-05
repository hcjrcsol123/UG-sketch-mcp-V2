"""Unit checks for the independent F10 projection oracle (no NX dependency)."""
from __future__ import annotations

import importlib.util
import math
import unittest
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
ORACLE_PATH = PROJECT_ROOT / "tools" / "v2" / "f10-projection-oracle.py"
SPEC = importlib.util.spec_from_file_location("f10_projection_oracle", ORACLE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("could not load independent F10 projection oracle")
ORACLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ORACLE)


class F10ProjectionGeometryOracleTests(unittest.TestCase):
    def setUp(self):
        self.origin = [3.0, -4.0, 5.0]
        self.normal = ORACLE.normalize([1.0, 2.0, 3.0])
        self.x_axis = ORACLE.normalize([
            1.0 - self.normal[0] * self.normal[0],
            -self.normal[0] * self.normal[1],
            -self.normal[0] * self.normal[2],
        ])
        self.y_axis = ORACLE.normalize(ORACLE.cross3(self.normal, self.x_axis))

    def test_projection_along_both_normal_signs_hits_the_same_tilted_plane(self):
        points = [
            [12.5, -6.0, 14.0],
            [-2.0, 8.0, -3.0],
        ]
        for point in points:
            with self.subTest(point=point):
                projected_plus = ORACLE.project_point_along_direction(
                    point, self.origin, self.normal, self.normal)
                projected_minus = ORACLE.project_point_along_direction(
                    point, self.origin, self.normal, [-value for value in self.normal])
                self.assertLess(ORACLE.distance3(projected_plus, projected_minus), 1e-12)
                signed_distance = ORACLE.dot(
                    self.normal, [projected_plus[index] - self.origin[index] for index in range(3)])
                self.assertAlmostEqual(signed_distance, 0.0, places=12)

    def test_projection_formula_handles_oblique_direction_but_contract_rejects_it(self):
        point = [7.0, 6.0, 12.0]
        oblique = ORACLE.normalize([1.0, 0.0, 1.0])
        projected = ORACLE.project_point_along_direction(
            point, self.origin, self.normal, oblique)
        plane_residual = ORACLE.dot(
            self.normal, [projected[index] - self.origin[index] for index in range(3)])
        self.assertAlmostEqual(plane_residual, 0.0, places=12)
        self.assertFalse(ORACLE.direction_is_normal(oblique, self.normal))

    def test_direction_validation_accepts_parallel_normal_and_rejects_bad_domain(self):
        self.assertTrue(ORACLE.direction_is_normal(self.normal, self.normal))
        self.assertTrue(ORACLE.direction_is_normal([-value for value in self.normal], self.normal))
        self.assertFalse(ORACLE.direction_is_normal(self.x_axis, self.normal))
        self.assertFalse(ORACLE.direction_is_normal([1.0, 1.0, 1.0], self.normal))
        for bad in ([0.0, 0.0, 0.0], [float("nan"), 0.0, 1.0],
                    [float("inf"), 0.0, 1.0], [1.0, 2.0], [1.0, 2.0, 3.0, 4.0]):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                ORACLE.direction_is_normal(bad, self.normal)

    def test_stable_normalization_handles_large_finite_direction(self):
        normalized = ORACLE.normalize([1.0e308, -1.0e308, 1.0e308])
        self.assertTrue(all(math.isfinite(component) for component in normalized))
        self.assertAlmostEqual(ORACLE.norm(normalized), 1.0, places=15)
        self.assertTrue(ORACLE.direction_is_normal(
            [3.0e307, 6.0e307, 9.0e307],
            [1.0, 2.0, 3.0]))

    def test_line_match_is_one_to_one_with_endpoint_order_independent_tolerance(self):
        expected_start = [1.0, 2.0, 3.0]
        expected_end = [4.0, 6.0, 3.0]
        matched = ORACLE.line_matches(expected_end, expected_start,
                                      expected_start, expected_end, 1.0e-6)
        self.assertTrue(matched["matches"], matched)
        self.assertTrue(matched["endpoint_order_reversed"], matched)
        missed = ORACLE.line_matches([4.0, 6.0, 3.001], expected_start,
                                     expected_start, expected_end, 1.0e-6)
        self.assertFalse(missed["matches"], missed)


if __name__ == "__main__":
    unittest.main()
