"""Independent analytic oracles for F08 sketch offset acceptance.

These helpers are deliberately separate from the production adapter. The native
acceptance compares NXOpen geometry readback with these signed-offset rules.
All length values are millimetres and all angles are radians unless named deg.
"""

from __future__ import annotations

import math
import unittest


Point2 = tuple[float, float]
Point3 = tuple[float, float, float]


def _add(a: Point2, b: Point2) -> Point2:
    return (a[0] + b[0], a[1] + b[1])


def _sub(a: Point2, b: Point2) -> Point2:
    return (a[0] - b[0], a[1] - b[1])


def _scale(a: Point2, scalar: float) -> Point2:
    return (a[0] * scalar, a[1] * scalar)


def _dot(a: Point2, b: Point2) -> float:
    return a[0] * b[0] + a[1] * b[1]


def _cross(a: Point2, b: Point2) -> float:
    return a[0] * b[1] - a[1] * b[0]


def _length(a: Point2) -> float:
    return math.hypot(a[0], a[1])


def _unit(a: Point2) -> Point2:
    magnitude = _length(a)
    if not math.isfinite(magnitude) or magnitude == 0.0:
        raise ValueError("curve direction must have finite nonzero length")
    return (a[0] / magnitude, a[1] / magnitude)


def offset_segment(
    start: Point2,
    end: Point2,
    distance_mm: float,
    *,
    side: str,
    direction: str = "forward",
) -> tuple[Point2, Point2]:
    """Return the parallel support segment using traversal-relative left/right."""
    if not math.isfinite(distance_mm) or distance_mm <= 0.0:
        raise ValueError("offset distance must be finite and positive")
    if side not in {"left", "right"} or direction not in {"forward", "reverse"}:
        raise ValueError("side and direction must use their explicit enums")
    tangent = _unit(_sub(end, start))
    if direction == "reverse":
        tangent = _scale(tangent, -1.0)
    left_normal = (-tangent[1], tangent[0])
    side_sign = 1.0 if side == "left" else -1.0
    shift = _scale(left_normal, side_sign * distance_mm)
    return _add(start, shift), _add(end, shift)


def line_intersection(
    a_start: Point2, a_end: Point2, b_start: Point2, b_end: Point2
) -> Point2:
    """Intersect two infinite support lines, as required by extension joins."""
    a = _sub(a_end, a_start)
    b = _sub(b_end, b_start)
    denominator = _cross(a, b)
    if denominator == 0.0:
        raise ValueError("parallel supports have no unique extension join")
    t = _cross(_sub(b_start, a_start), b) / denominator
    return _add(a_start, _scale(a, t))


def build_frame(origin: Point3, normal: Point3, requested_x: Point3) -> tuple[Point3, Point3, Point3]:
    """Build an orthonormal right-handed sketch frame from world-space vectors."""

    def norm(v: Point3) -> Point3:
        n = math.sqrt(sum(x * x for x in v))
        if n == 0.0:
            raise ValueError("frame axes must be nonzero")
        return (v[0] / n, v[1] / n, v[2] / n)

    n = norm(normal)
    projection = sum(requested_x[i] * n[i] for i in range(3))
    projected_x = tuple(requested_x[i] - projection * n[i] for i in range(3))
    x = norm(projected_x)  # type: ignore[arg-type]
    y = (
        n[1] * x[2] - n[2] * x[1],
        n[2] * x[0] - n[0] * x[2],
        n[0] * x[1] - n[1] * x[0],
    )
    return origin, x, y


def world_point(frame: tuple[Point3, Point3, Point3], local: Point2) -> Point3:
    origin, x, y = frame
    return tuple(origin[i] + local[0] * x[i] + local[1] * y[i] for i in range(3))  # type: ignore[return-value]


class F08OffsetExpectedValueTests(unittest.TestCase):
    def test_horizontal_line_both_sides_and_reversed_direction(self):
        self.assertEqual(offset_segment((0, 0), (10, 0), 2, side="left"), ((0, 2), (10, 2)))
        self.assertEqual(offset_segment((0, 0), (10, 0), 2, side="right"), ((0, -2), (10, -2)))
        self.assertEqual(
            offset_segment((0, 0), (10, 0), 2, side="left", direction="reverse"),
            ((0, -2), (10, -2)),
        )

    def test_l_chain_extension_join_tracks_chain_orientation(self):
        first_left = offset_segment((0, 0), (10, 0), 2, side="left")
        second_left = offset_segment((10, 0), (10, 10), 2, side="left")
        self.assertEqual(first_left, ((0, 2), (10, 2)))
        self.assertEqual(second_left, ((8, 0), (8, 10)))
        self.assertEqual(line_intersection(*first_left, *second_left), (8, 2))

        first_right = offset_segment((0, 0), (10, 0), 2, side="right")
        second_right = offset_segment((10, 0), (10, 10), 2, side="right")
        self.assertEqual(first_right, ((0, -2), (10, -2)))
        self.assertEqual(second_right, ((12, 0), (12, 10)))
        self.assertEqual(line_intersection(*first_right, *second_right), (12, -2))

        # Reverse chain order and both directions: traversal-left now coincides
        # with the forward chain's right side in world coordinates.
        reverse_vertical = offset_segment((10, 0), (10, 10), 2, side="left", direction="reverse")
        reverse_horizontal = offset_segment((0, 0), (10, 0), 2, side="left", direction="reverse")
        self.assertEqual(reverse_vertical, ((12, 0), (12, 10)))
        self.assertEqual(reverse_horizontal, ((0, -2), (10, -2)))
        self.assertEqual(line_intersection(*reverse_vertical, *reverse_horizontal), (12, -2))

    def test_mixed_stored_line_directions_follow_each_chain_entry(self):
        # The second source object is stored top-to-bottom, but the ordered
        # chain traverses it bottom-to-top via direction="reverse".
        first = offset_segment((0, 0), (10, 0), 2, side="left", direction="forward")
        second = offset_segment((10, 10), (10, 0), 2, side="left", direction="reverse")
        self.assertEqual(first, ((0, 2), (10, 2)))
        self.assertEqual(second, ((8, 10), (8, 0)))
        self.assertEqual(line_intersection(first[0], first[1], second[1], second[0]), (8, 2))

    def test_ccw_square_inside_and_outside_bounds_area_and_perimeter(self):
        # The test oracle is topology independent: the native acceptance also
        # checks all four endpoint connections and retains all four seed lines.
        side = 10.0
        distance = 2.0
        inside_side = side - 2.0 * distance
        outside_side = side + 2.0 * distance
        self.assertEqual((inside_side, inside_side**2, 4 * inside_side), (6.0, 36.0, 24.0))
        self.assertEqual((outside_side, outside_side**2, 4 * outside_side), (14.0, 196.0, 56.0))
        inside_bounds = (2.0, 2.0, 8.0, 8.0)
        outside_bounds = (-2.0, -2.0, 12.0, 12.0)
        self.assertEqual(inside_bounds, (2.0, 2.0, 2.0 + inside_side, 2.0 + inside_side))
        self.assertEqual(outside_bounds, (-2.0, -2.0, -2.0 + outside_side, -2.0 + outside_side))

    def test_reversing_closed_chain_swaps_traversal_relative_left_and_right(self):
        forward_left_bounds = (2.0, 2.0, 8.0, 8.0)
        reverse_left_bounds = (-2.0, -2.0, 12.0, 12.0)
        self.assertEqual(forward_left_bounds, (2.0, 2.0, 10.0 - 2.0, 10.0 - 2.0))
        self.assertEqual(reverse_left_bounds, (0.0 - 2.0, 0.0 - 2.0, 10.0 + 2.0, 10.0 + 2.0))

    def test_closed_capsule_arc_offsets_have_analytic_radii_and_measures(self):
        radius = 5.0
        straight_length = 20.0
        distance = 2.0
        inner_radius = radius - distance
        outer_radius = radius + distance
        self.assertEqual(inner_radius, 3.0)
        self.assertEqual(outer_radius, 7.0)
        self.assertAlmostEqual(2.0 * straight_length + 2.0 * math.pi * inner_radius,
                               40.0 + 6.0 * math.pi, places=12)
        self.assertAlmostEqual(2.0 * straight_length + 2.0 * math.pi * outer_radius,
                               40.0 + 14.0 * math.pi, places=12)
        self.assertAlmostEqual(2.0 * inner_radius * straight_length + math.pi * inner_radius**2,
                               120.0 + 9.0 * math.pi, places=12)
        self.assertAlmostEqual(2.0 * outer_radius * straight_length + math.pi * outer_radius**2,
                               280.0 + 49.0 * math.pi, places=12)

    def test_circle_offset_reverses_radius_change_with_traversal_side(self):
        source_radius = 5.0
        distance = 2.0
        self.assertEqual(source_radius - distance, 3.0)  # CCW traversal-left
        self.assertEqual(source_radius + distance, 7.0)  # CCW traversal-right
        self.assertEqual(source_radius + distance, 7.0)  # reversed traversal-left

    def test_arc_join_outer_quarter_circle_has_tangent_contacts_and_length(self):
        # The L chain turns left at C=(10,0). Its traversal-right offset adds
        # the outside quarter circle between the two parallel support lines.
        start = (10.0, -2.0)
        end = (12.0, 0.0)
        center = (10.0, 0.0)
        self.assertEqual((start, end, center), ((10.0, -2.0), (12.0, 0.0), (10.0, 0.0)))
        radius = math.dist(start, center)
        self.assertAlmostEqual(radius, 2.0, places=12)
        self.assertAlmostEqual(math.dist(end, center), radius, places=12)
        start_angle = math.atan2(start[1] - center[1], start[0] - center[0])
        end_angle = math.atan2(end[1] - center[1], end[0] - center[0])
        sweep = (end_angle - start_angle) % (2.0 * math.pi)
        self.assertAlmostEqual(sweep, math.pi / 2.0, places=12)
        self.assertAlmostEqual(radius * sweep, math.pi, places=12)
        start_tangent = (-math.sin(start_angle), math.cos(start_angle))
        end_tangent = (-math.sin(end_angle), math.cos(end_angle))
        self.assertAlmostEqual(_dot(start_tangent, (1.0, 0.0)), 1.0, places=12)
        self.assertAlmostEqual(_dot(end_tangent, (0.0, 1.0)), 1.0, places=12)

    def test_reversed_arc_join_keeps_same_outer_geometry_with_negative_sweep(self):
        center = (10.0, 0.0)
        start = (12.0, 0.0)
        end = (10.0, -2.0)
        start_angle = math.atan2(start[1] - center[1], start[0] - center[0])
        end_angle = math.atan2(end[1] - center[1], end[0] - center[0])
        clockwise_sweep = -((start_angle - end_angle) % (2.0 * math.pi))
        self.assertEqual((start, end, center), ((12.0, 0.0), (10.0, -2.0), (10.0, 0.0)))
        self.assertAlmostEqual(math.dist(start, center), 2.0, places=12)
        self.assertAlmostEqual(math.dist(end, center), 2.0, places=12)
        self.assertAlmostEqual(clockwise_sweep, -math.pi / 2.0, places=12)
        self.assertAlmostEqual(2.0 * abs(clockwise_sweep), math.pi, places=12)
        start_tangent = (math.sin(start_angle), -math.cos(start_angle))
        end_tangent = (math.sin(end_angle), -math.cos(end_angle))
        self.assertAlmostEqual(_dot(start_tangent, (0.0, -1.0)), 1.0, places=12)
        self.assertAlmostEqual(_dot(end_tangent, (-1.0, 0.0)), 1.0, places=12)

    def test_tilted_frame_maps_local_offset_points_into_world_coordinates(self):
        frame = build_frame((10.0, 20.0, 30.0), (1.0, 2.0, 3.0), (1.0, -1.0, 0.5))
        origin, x_axis, y_axis = frame
        normal = tuple(component / math.sqrt(14.0) for component in (1.0, 2.0, 3.0))
        self.assertAlmostEqual(sum(component**2 for component in x_axis), 1.0, places=12)
        self.assertAlmostEqual(sum(component**2 for component in y_axis), 1.0, places=12)
        self.assertAlmostEqual(sum(x_axis[i] * y_axis[i] for i in range(3)), 0.0, places=12)
        self.assertAlmostEqual(sum(x_axis[i] * normal[i] for i in range(3)), 0.0, places=12)
        self.assertAlmostEqual(sum(y_axis[i] * normal[i] for i in range(3)), 0.0, places=12)
        self.assertEqual(world_point(frame, (0.0, 0.0)), (10.0, 20.0, 30.0))
        left_start = world_point(frame, (0.0, 2.0))
        left_end = world_point(frame, (10.0, 2.0))
        for actual, expected in zip(left_start, (11.431083505599865, 20.894427190999917, 28.9266873708001)):
            self.assertAlmostEqual(actual, expected, places=12)
        for actual, expected in zip(left_end, (17.88531799600559, 13.723055534993554, 31.556190311335765)):
            self.assertAlmostEqual(actual, expected, places=12)

    def test_invalid_distance_and_degenerate_segment_have_no_numeric_oracle(self):
        for invalid in (0.0, -1.0, math.inf, math.nan):
            with self.subTest(distance=invalid), self.assertRaises(ValueError):
                offset_segment((0, 0), (1, 0), invalid, side="left")
        with self.assertRaises(ValueError):
            offset_segment((1, 1), (1, 1), 1.0, side="left")


if __name__ == "__main__":
    unittest.main()
