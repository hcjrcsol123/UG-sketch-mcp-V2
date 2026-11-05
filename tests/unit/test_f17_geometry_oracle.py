"""Hand-calculated and symmetry checks of the offline F17 box oracle."""

import importlib.util
import math
from pathlib import Path
import unittest


PATH = Path(__file__).resolve().parents[2] / "tools" / "v2" / "f17-geometry-oracle.py"
SPEC = importlib.util.spec_from_file_location("f17_geometry_oracle", PATH)
ORACLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ORACLE)


class GeometryOracleTests(unittest.TestCase):
    def calculate(self, **changes):
        args = dict(lower_mm=(0, 0, 0), upper_mm=(20, 16, 12),
                    edge_axis="z", edge_point_mm=(0, 0, 4), reference_axis="x",
                    mode="two_distances", distance_mm=4, distance2_mm=2)
        args.update(changes)
        return ORACLE.chamfer_box(**args)

    def test_unequal_intercepts_plane_face_and_volume(self):
        r = self.calculate()
        self.assertEqual(r["intercepts_mm"], (4, 2))
        self.assertEqual(r["intercept_points_mm"], ((4, 0, 0), (0, 2, 0)))
        self.assertEqual(r["face_vertices_mm"], ((4, 0, 0), (0, 2, 0),
                                                  (0, 2, 12), (4, 0, 12)))
        self.assertAlmostEqual(r["face_area_mm2"], 12 * math.sqrt(20))
        self.assertEqual(r["removed_volume_mm3"], 48)
        self.assertEqual(r["remaining_volume_mm3"], 3792)
        self.assertAlmostEqual(r["plane_normal_into_retained"][0], 1 / math.sqrt(5))
        self.assertAlmostEqual(r["plane_normal_into_retained"][1], 2 / math.sqrt(5))
        for vertex in r["face_vertices_mm"]:
            self.assertAlmostEqual(sum(r["plane_normal_into_retained"][i] *
                                       (vertex[i] - r["plane_point_mm"][i])
                                       for i in range(3)), 0)
        removed, kept = r["removed_witness"], r["retained_witness"]
        self.assertEqual(removed["point_mm"], (0.8, 0.4, 6))
        self.assertEqual(kept["point_mm"], (3, 1.5, 6))
        self.assertAlmostEqual(removed["signed_plane_distance_mm"], -2.4 / math.sqrt(5))
        self.assertAlmostEqual(kept["signed_plane_distance_mm"], 2 / math.sqrt(5))
        self.assertGreater(removed["min_plane_distance_mm"], 0)
        self.assertGreater(kept["min_plane_distance_mm"], 0)

    def test_equal_and_distance_angle_non_45(self):
        equal = self.calculate(mode="equal_distance", distance2_mm=None, distance_mm=3)
        self.assertEqual(equal["intercepts_mm"], (3, 3))
        self.assertEqual(equal["removed_volume_mm3"], 54)
        angled = self.calculate(mode="distance_angle", distance2_mm=None,
                                distance_mm=3, angle_deg=30)
        self.assertAlmostEqual(angled["intercepts_mm"][1], math.sqrt(3))
        self.assertAlmostEqual(angled["removed_volume_mm3"], 18 * math.sqrt(3))
        self.assertAlmostEqual(angled["face_area_mm2"], 24 * math.sqrt(3))

    def test_all_edge_axes_corners_and_reference_axis_swap(self):
        lo, hi = (-5, 2, 10), (15, 18, 22)
        for edge_axis in range(3):
            trans = [i for i in range(3) if i != edge_axis]
            for high_a in (False, True):
                for high_b in (False, True):
                    point = list(lo)
                    point[trans[0]] = hi[trans[0]] if high_a else lo[trans[0]]
                    point[trans[1]] = hi[trans[1]] if high_b else lo[trans[1]]
                    for a in trans:
                        with self.subTest(edge=edge_axis, high_a=high_a, high_b=high_b, ref=a):
                            b = next(i for i in trans if i != a)
                            r = self.calculate(lower_mm=lo, upper_mm=hi,
                                               edge_axis="xyz"[edge_axis], edge_point_mm=point,
                                               reference_axis="xyz"[a])
                            p1, p2 = r["intercept_points_mm"]
                            sa = 1 if point[a] == lo[a] else -1
                            sb = 1 if point[b] == lo[b] else -1
                            self.assertEqual(p1[a], point[a] + 4 * sa)
                            self.assertEqual(p2[b], point[b] + 2 * sb)
                            self.assertEqual(p1[b], point[b])
                            self.assertEqual(p2[a], point[a])
                            self.assertEqual(r["other_axis"], "xyz"[b])
                            self.assertEqual(r["removed_volume_mm3"], 4 * (hi[edge_axis] - lo[edge_axis]))
                            self.assertLess(r["removed_witness"]["signed_plane_distance_mm"], 0)
                            self.assertGreater(r["retained_witness"]["signed_plane_distance_mm"], 0)
                            self.assertGreater(r["removed_witness"]["min_plane_distance_mm"], 0)
                            self.assertGreater(r["retained_witness"]["min_plane_distance_mm"], 0)

    def test_invalid_geometry_and_mode_boundaries(self):
        cases = [
            {"lower_mm": (0, 0, 0), "upper_mm": (0, 16, 12)},
            {"upper_mm": (float("inf"), 16, 12)},
            {"edge_axis": "Z"}, {"edge_axis": "x"},
            {"edge_point_mm": (1, 0, 0)}, {"edge_point_mm": (0, 0, 13)},
            {"distance_mm": 0}, {"distance_mm": -1}, {"distance_mm": float("nan")},
            {"distance_mm": True}, {"distance_mm": 20}, {"distance2_mm": 16},
            {"distance2_mm": -1}, {"mode": "unknown"},
            {"mode": "equal_distance"},
            {"mode": "two_distances", "distance2_mm": None},
            {"mode": "distance_angle", "distance2_mm": None, "angle_deg": 0},
            {"mode": "distance_angle", "distance2_mm": None, "angle_deg": 90},
            {"mode": "distance_angle", "distance2_mm": None, "angle_deg": -30},
            {"mode": "distance_angle", "distance2_mm": None, "angle_deg": float("nan")},
            {"mode": "distance_angle", "distance2_mm": 2, "angle_deg": 30},
            {"mode": "distance_angle", "distance2_mm": None, "angle_deg": 89},
        ]
        for changes in cases:
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.calculate(**changes)

    def test_near_limit_valid_and_unrepresentable_coordinates(self):
        result = self.calculate(distance_mm=19.9, distance2_mm=15.9)
        self.assertGreater(result["remaining_volume_mm3"], 0)
        self.assertGreater(result["removed_witness"]["min_plane_distance_mm"], 0)
        with self.assertRaises(ValueError):
            self.calculate(lower_mm=(1e16, 0, 0), upper_mm=(1e16 + 20, 16, 12),
                           edge_point_mm=(1e16, 0, 0), distance_mm=0.1)


if __name__ == "__main__":
    unittest.main()
