"""Known analytical volumes and constraint checks; does not execute NX."""

import math
import unittest

from tools.v2.f14_hole_oracle import (bore_removed_volume_mm3,
    counterbore_removed_volume_mm3, countersink_removed_volume_mm3)


class F14HoleOracleTests(unittest.TestCase):
    def test_cylinder_and_conical_tip(self):
        self.assertAlmostEqual(bore_removed_volume_mm3(4, 10), 40 * math.pi)
        # 90 degree included tip has height equal to the 2 mm radius.
        self.assertAlmostEqual(bore_removed_volume_mm3(4, 10, 90), 40 * math.pi + 8 * math.pi / 3)

    def test_counterbore_adds_annulus_without_double_counting_bore(self):
        self.assertAlmostEqual(counterbore_removed_volume_mm3(4, 10, 8, 3), 76 * math.pi)

    def test_countersink_adds_frustum_without_double_counting_bore(self):
        # Entry radii 4 and 2; 90-degree included angle yields 2 mm depth.
        self.assertAlmostEqual(countersink_removed_volume_mm3(4, 10, 8, 90), 40 * math.pi + 32 * math.pi / 3)

    def test_rejects_nonfinite_impossible_and_degenerate_fixture_parameters(self):
        invalid = [lambda: bore_removed_volume_mm3(True, 10),
                   lambda: bore_removed_volume_mm3(float('nan'), 10),
                   lambda: bore_removed_volume_mm3(4, 10, 180),
                   lambda: counterbore_removed_volume_mm3(4, 10, 4, 3),
                   lambda: counterbore_removed_volume_mm3(4, 10, 8, 10),
                   lambda: countersink_removed_volume_mm3(4, 1, 8, 90),
                   lambda: countersink_removed_volume_mm3(4, 10, 8, 0)]
        for case in invalid:
            with self.subTest(case=case), self.assertRaises(ValueError):
                case()


if __name__ == '__main__':
    unittest.main()
