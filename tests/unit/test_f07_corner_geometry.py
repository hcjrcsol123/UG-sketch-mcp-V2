"""Independent analytical expected values for F07 sketch corner operations.

These helpers deliberately use only the requested corner geometry. They do not
read production plans or NX records, so the real-NX acceptance can compare its
native readback against an independent construction.
"""

from __future__ import annotations

import math
import unittest


Point2 = tuple[float, float]


def _sub(a: Point2, b: Point2) -> Point2:
    return (a[0] - b[0], a[1] - b[1])


def _add(a: Point2, b: Point2) -> Point2:
    return (a[0] + b[0], a[1] + b[1])


def _scale(v: Point2, factor: float) -> Point2:
    return (v[0] * factor, v[1] * factor)


def _dot(a: Point2, b: Point2) -> float:
    return a[0] * b[0] + a[1] * b[1]


def _cross(a: Point2, b: Point2) -> float:
    return a[0] * b[1] - a[1] * b[0]


def _length(v: Point2) -> float:
    return math.hypot(v[0], v[1])


def _unit(v: Point2) -> Point2:
    length = _length(v)
    if length <= 0.0:
        raise ValueError("corner rays must have nonzero length")
    return (v[0] / length, v[1] / length)


def corner_frame(corner: Point2, far1: Point2, far2: Point2) -> tuple[float, Point2, Point2]:
    """Return θ and unit rays from the selected corner into both kept curves."""
    e1 = _unit(_sub(far1, corner))
    e2 = _unit(_sub(far2, corner))
    theta = math.atan2(abs(_cross(e1, e2)), _dot(e1, e2))
    if theta <= 0.0 or theta >= math.pi:
        raise ValueError("corner rays must form an angle strictly between 0 and 180 degrees")
    return theta, e1, e2


def expected_fillet(corner: Point2, far1: Point2, far2: Point2, radius: float) -> dict[str, object]:
    """Independent tangent points, center, trim, and minor fillet arc length."""
    if not math.isfinite(radius) or radius <= 0.0:
        raise ValueError("radius must be finite and positive")
    theta, e1, e2 = corner_frame(corner, far1, far2)
    tangent_distance = radius / math.tan(theta / 2.0)
    bisector = _unit(_add(e1, e2))
    center_distance = radius / math.sin(theta / 2.0)
    p1 = _add(corner, _scale(e1, tangent_distance))
    p2 = _add(corner, _scale(e2, tangent_distance))
    center = _add(corner, _scale(bisector, center_distance))
    # The included corner angle is θ; the tangent fillet's minor sweep is π−θ.
    sweep = math.pi - theta
    return {
        "corner_angle_rad": theta,
        "curve1_ray": e1,
        "curve2_ray": e2,
        "tangent_distance_mm": tangent_distance,
        "point1_mm": p1,
        "point2_mm": p2,
        "center_mm": center,
        "radius_mm": radius,
        "minor_sweep_rad": sweep,
        "arc_length_mm": radius * sweep,
    }


def expected_chamfer(
    corner: Point2,
    far1: Point2,
    far2: Point2,
    distance1: float,
    *,
    distance2: float | None = None,
    angle_deg: float | None = None,
) -> dict[str, object]:
    """Return trim points and chord length for F07's first-curve angle rule.

    For distance-angle mode, δ is the angle at the first trim point A between
    A→corner and A→second trim point B. Equivalently, it is the angle between
    the curve1 ray and B→A. It is not the angle between curve1 and A→B.
    """
    if not math.isfinite(distance1) or distance1 <= 0.0:
        raise ValueError("distance1 must be finite and positive")
    theta, e1, e2 = corner_frame(corner, far1, far2)
    if angle_deg is not None:
        if distance2 is not None:
            raise ValueError("specify distance2 or angle_deg, not both")
        delta = math.radians(angle_deg)
        if not math.isfinite(delta) or delta <= 0.0 or delta >= math.pi - theta:
            raise ValueError("angle must satisfy 0 < δ < 180°−θ")
        distance2 = distance1 * math.sin(delta) / math.sin(theta + delta)
    elif distance2 is None:
        distance2 = distance1

    if not math.isfinite(distance2) or distance2 <= 0.0:
        raise ValueError("distance2 must be finite and positive")
    point1 = _add(corner, _scale(e1, distance1))
    point2 = _add(corner, _scale(e2, distance2))
    chord = _sub(point1, point2)
    measured_delta = math.atan2(abs(_cross(e1, chord)), _dot(e1, chord))
    if angle_deg is not None and abs(measured_delta - math.radians(angle_deg)) > 1e-12:
        raise ArithmeticError("distance-angle construction did not reproduce the requested angle")
    return {
        "corner_angle_rad": theta,
        "curve1_ray": e1,
        "curve2_ray": e2,
        "distance1_mm": distance1,
        "distance2_mm": distance2,
        "angle_deg": angle_deg,
        "point1_mm": point1,
        "point2_mm": point2,
        "segment_length_mm": _length(chord),
        "measured_angle_rad": measured_delta,
    }


class F07CornerExpectedValueTests(unittest.TestCase):
    def test_right_angle_fillet_expected_tangency_center_and_length(self):
        result = expected_fillet((20.0, 0.0), (0.0, 0.0), (20.0, 10.0), 2.0)
        self.assertAlmostEqual(result["corner_angle_rad"], math.pi / 2.0, places=12)
        self.assertAlmostEqual(result["tangent_distance_mm"], 2.0, places=12)
        self.assertAlmostEqual(result["point1_mm"][0], 18.0, places=12)
        self.assertAlmostEqual(result["point1_mm"][1], 0.0, places=12)
        self.assertAlmostEqual(result["point2_mm"][0], 20.0, places=12)
        self.assertAlmostEqual(result["point2_mm"][1], 2.0, places=12)
        self.assertAlmostEqual(result["center_mm"][0], 18.0, places=12)
        self.assertAlmostEqual(result["center_mm"][1], 2.0, places=12)
        self.assertAlmostEqual(result["minor_sweep_rad"], math.pi / 2.0, places=12)
        self.assertAlmostEqual(result["arc_length_mm"], math.pi, places=12)

    def test_nonaxial_135_degree_fillet_has_correct_external_sweep(self):
        result = expected_fillet((30.0, 20.0), (10.0, 10.0), (35.0, 35.0), 1.5)
        self.assertAlmostEqual(math.degrees(result["corner_angle_rad"]), 135.0, places=12)
        self.assertAlmostEqual(result["tangent_distance_mm"], 1.5 * (math.sqrt(2.0) - 1.0), places=12)
        self.assertAlmostEqual(result["point1_mm"][0], 29.444274190398847, places=12)
        self.assertAlmostEqual(result["point1_mm"][1], 19.72213709519942, places=12)
        self.assertAlmostEqual(result["point2_mm"][0], 30.19647874422468, places=12)
        self.assertAlmostEqual(result["point2_mm"][1], 20.58943623267404, places=12)
        self.assertAlmostEqual(result["arc_length_mm"], 3.0 * math.pi / 8.0, places=12)

    def test_right_angle_equal_and_two_distance_chamfer_values(self):
        equal = expected_chamfer((20.0, 0.0), (0.0, 0.0), (20.0, 10.0), 3.0)
        self.assertEqual(equal["point1_mm"], (17.0, 0.0))
        self.assertEqual(equal["point2_mm"], (20.0, 3.0))
        self.assertAlmostEqual(equal["segment_length_mm"], math.sqrt(18.0), places=12)

        two_distance = expected_chamfer(
            (20.0, 0.0), (0.0, 0.0), (20.0, 10.0), 4.0, distance2=2.0
        )
        self.assertEqual(two_distance["point1_mm"], (16.0, 0.0))
        self.assertEqual(two_distance["point2_mm"], (20.0, 2.0))
        self.assertAlmostEqual(two_distance["segment_length_mm"], math.sqrt(20.0), places=12)

    def test_distance_angle_uses_first_trim_point_orientation(self):
        result = expected_chamfer(
            (20.0, 0.0), (0.0, 0.0), (20.0, 10.0), 4.0, angle_deg=30.0
        )
        self.assertAlmostEqual(result["distance2_mm"], 4.0 / math.sqrt(3.0), places=12)
        self.assertEqual(result["point1_mm"], (16.0, 0.0))
        self.assertAlmostEqual(result["point2_mm"][0], 20.0, places=12)
        self.assertAlmostEqual(result["point2_mm"][1], 4.0 / math.sqrt(3.0), places=12)
        self.assertAlmostEqual(result["segment_length_mm"], 8.0 / math.sqrt(3.0), places=12)
        self.assertAlmostEqual(math.degrees(result["measured_angle_rad"]), 30.0, places=12)

    def test_invalid_angle_domain_and_degenerate_corner_are_rejected_by_oracle(self):
        with self.assertRaises(ValueError):
            expected_fillet((0.0, 0.0), (1.0, 0.0), (2.0, 0.0), 1.0)
        with self.assertRaises(ValueError):
            expected_chamfer((20.0, 0.0), (0.0, 0.0), (20.0, 10.0), 4.0, angle_deg=90.0)
        with self.assertRaises(ValueError):
            expected_chamfer((20.0, 0.0), (0.0, 0.0), (20.0, 10.0), 4.0, angle_deg=0.0)


if __name__ == "__main__":
    unittest.main()
