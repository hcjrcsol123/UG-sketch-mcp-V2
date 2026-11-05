"""F16 analytical oracle controls; these are not NX or product results."""
import importlib.util
import math
from pathlib import Path
import unittest

PATH = Path(__file__).resolve().parents[2] / "tools/v2/f16-geometry-oracle.py"
SPEC = importlib.util.spec_from_file_location("f16_geometry_oracle", PATH)
oracle = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(oracle)


class GeometryOracleTests(unittest.TestCase):
    def test_volume_analytical_reference_and_false_results(self):
        self.assertAlmostEqual(oracle.expected_volume(1), 2391.415926535898, places=9)
        self.assertAlmostEqual(oracle.expected_volume(2), 2382.831853071796, places=9)
        oracle.check_volume(2391.415926535898, 1)
        for bad in (2400.0, float("nan"), True):
            with self.assertRaises(AssertionError):
                oracle.check_volume(bad, 1)
        with self.assertRaises(ValueError):
            oracle.expected_volume(3)

    def test_cylinder_location_orientation_radius(self):
        oracle.check_cylinder(2, [42, 2, -90], [0, 0, -1], 40, "low")
        oracle.check_cylinder(2, [58, 10, 5], [0, 0, 1], 40, "high")
        for radius, point, axis in ((3, [42, 2, 0], [0, 0, 1]),
                                    (2, [58, 10, 0], [0, 0, 1]),
                                    (2, [42, 2, 0], [1, 0, 0]),
                                    (2, [42, 2, 0], [0, 0, 0])):
            with self.assertRaises(AssertionError):
                oracle.check_cylinder(radius, point, axis, 40, "low")

    def test_bbox_and_witness_must_be_valid(self):
        oracle.check_box([0, 0, 0, 20, 12, 10], 0)
        oracle.check_containment(1, 1)
        oracle.check_containment(2, 2)
        with self.assertRaises(AssertionError):
            oracle.check_box([0, 0, 0, 19, 12, 10], 0)
        for actual in (True, 3, 1):
            with self.assertRaises(AssertionError):
                oracle.check_containment(actual, 2)


if __name__ == "__main__":
    unittest.main()
