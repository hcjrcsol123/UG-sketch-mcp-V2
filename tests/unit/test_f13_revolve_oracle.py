"""F13 independent revolve geometry calculations; no NX or production matcher."""

from __future__ import annotations

import math
import unittest

from tools.v2.f13_revolve_oracle import (
    PolygonLoop,
    full_revolve_cylinder_bounds_mm,
    full_revolved_ellipse_bounds_mm,
    make_axis_frame,
    net_radial_first_moment_mm3,
    net_section_area_mm2,
    normalize_axis,
    polygon_area_mm2,
    polygon_radial_first_moment_mm3,
    revolved_profile_bounds_mm,
    revolved_sheet_area_mm2,
    revolved_solid_volume_mm3,
    rotate_point_about_axis,
    validate_angle_interval,
    validate_profile_output,
)


def rect(r0: float, r1: float, h0: float, h1: float, *, role: str = "outer",
         label: str = "rectangle", reverse: bool = False) -> PolygonLoop:
    points = ((r0, h0), (r1, h0), (r1, h1), (r0, h1), (r0, h0))
    if reverse:
        points = tuple(reversed(points))
    return PolygonLoop(points, role, label)


class F13RevolveOracleTests(unittest.TestCase):
    def test_axis_direction_is_normalized_independently_of_input_magnitude(self):
        point, direction = normalize_axis((2.0, -3.0, 4.0), (2.0, 4.0, 4.0))
        self.assertEqual(point, (2.0, -3.0, 4.0))
        self.assertAlmostEqual(math.sqrt(sum(value * value for value in direction)), 1.0, places=14)
        self.assertAlmostEqual(direction[0], 1.0 / 3.0, places=14)
        self.assertAlmostEqual(direction[1], 2.0 / 3.0, places=14)
        self.assertAlmostEqual(direction[2], 2.0 / 3.0, places=14)
        huge_point, huge_direction = normalize_axis((0.0, 0.0, 0.0), (1.0e308, 0.0, 0.0))
        self.assertEqual(huge_point, (0.0, 0.0, 0.0))
        self.assertEqual(huge_direction, (1.0, 0.0, 0.0))
        _, huge_diagonal = normalize_axis((0.0, 0.0, 0.0), (1.0e308, 1.0e308, 1.0e308))
        self.assertAlmostEqual(math.sqrt(sum(value * value for value in huge_diagonal)), 1.0, places=14)
        _, tiny_direction = normalize_axis((0.0, 0.0, 0.0), (1.0e-300, 0.0, 0.0))
        self.assertEqual(tiny_direction, (1.0, 0.0, 0.0))

    def test_zero_or_nonfinite_axis_and_point_are_rejected(self):
        for point, direction in [((0, 0, 0), (0, 0, 0)),
                                 ((float("nan"), 0, 0), (1, 0, 0)),
                                 ((0, 0, 0), (float("inf"), 0, 0))]:
            with self.subTest(point=point, direction=direction), self.assertRaises(ValueError):
                normalize_axis(point, direction)

    def test_boolean_axis_components_are_not_accepted_as_numeric_one(self):
        with self.assertRaisesRegex(ValueError, "booleans"):
            normalize_axis((0, 0, 0), (True, 0, 0))

    def test_frame_is_orthonormal_and_right_handed_for_a_tilted_axis(self):
        frame = make_axis_frame((7, -2, 5), (1, 2, 3), (0, 0, 1))
        self.assertAlmostEqual(sum(x * y for x, y in zip(frame.direction, frame.radial_zero)), 0.0, places=14)
        self.assertAlmostEqual(sum(x * y for x, y in zip(frame.direction, frame.radial_quadrature)), 0.0, places=14)
        self.assertAlmostEqual(sum(x * y for x, y in zip(frame.radial_zero, frame.radial_quadrature)), 0.0, places=14)
        cross = (frame.direction[1] * frame.radial_zero[2] - frame.direction[2] * frame.radial_zero[1],
                 frame.direction[2] * frame.radial_zero[0] - frame.direction[0] * frame.radial_zero[2],
                 frame.direction[0] * frame.radial_zero[1] - frame.direction[1] * frame.radial_zero[0])
        for actual, expected in zip(cross, frame.radial_quadrature):
            self.assertAlmostEqual(actual, expected, places=14)

    def test_radial_seed_parallel_to_axis_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "parallel"):
            make_axis_frame((0, 0, 0), (1, 2, 3), (2, 4, 6))

    def test_rodrigues_rotation_observes_right_hand_sign_and_preserves_radius(self):
        axis_point, axis = (1.0, -2.0, 3.0), (1.0, 2.0, 3.0)
        point = (4.0, 5.0, -1.0)
        positive = rotate_point_about_axis(point, axis_point, axis, 47.0)
        negative = rotate_point_about_axis(point, axis_point, axis, -47.0)
        unit = tuple(value / math.sqrt(14.0) for value in axis)
        before = tuple(point[i] - axis_point[i] for i in range(3))
        after = tuple(positive[i] - axis_point[i] for i in range(3))
        axial_before = sum(before[i] * unit[i] for i in range(3))
        axial_after = sum(after[i] * unit[i] for i in range(3))
        radial_before = math.sqrt(sum((before[i] - axial_before * unit[i]) ** 2 for i in range(3)))
        radial_after = math.sqrt(sum((after[i] - axial_after * unit[i]) ** 2 for i in range(3)))
        self.assertAlmostEqual(axial_before, axial_after, places=13)
        self.assertAlmostEqual(radial_before, radial_after, places=13)
        self.assertNotEqual(positive, negative)
        self.assertEqual(rotate_point_about_axis(axis_point, axis_point, axis, -123.0), axis_point)

    def test_rotation_by_negative_span_is_inverse_of_positive_span(self):
        point, origin, axis = (9.0, 2.0, 1.0), (1.0, -4.0, 2.0), (1.0, 2.0, 3.0)
        rotated = rotate_point_about_axis(point, origin, axis, 86.5)
        recovered = rotate_point_about_axis(rotated, origin, axis, -86.5)
        for actual, expected in zip(recovered, point):
            self.assertAlmostEqual(actual, expected, places=13)

    def test_angle_domain_preserves_signed_negative_span_and_full_rotation(self):
        self.assertEqual(validate_angle_interval(-30.0, 210.0), 240.0)
        self.assertEqual(validate_angle_interval(210.0, -30.0), -240.0)
        self.assertEqual(validate_angle_interval(0.0, 360.0), 360.0)
        self.assertEqual(validate_angle_interval(-180.0, 180.0), 360.0)

    def test_angle_domain_rejects_equal_outside_and_nonfinite_endpoints(self):
        for start, end in [(0, 0), (0, 1.0e-10), (-361, 0), (0, 361), (-300, 100),
                           (0, float("nan")), (float("inf"), 1)]:
            with self.subTest(start=start, end=end), self.assertRaises(ValueError):
                validate_angle_interval(start, end)

    def test_profile_output_mode_rules_require_explicit_boolean_target(self):
        validate_profile_output("create", "solid", None)
        validate_profile_output("create", "sheet", None)
        validate_profile_output("unite", "solid", "body-1")
        validate_profile_output("subtract", "solid", "body-2")
        for args in [("unite", "sheet", "body-1"), ("subtract", "solid", None),
                     ("unite", "solid", ""), ("create", "solid", "body-1"),
                     ("create", "wire", None)]:
            with self.subTest(args=args), self.assertRaises(ValueError):
                validate_profile_output(*args)

    def test_rectangle_area_and_radial_first_moment_have_expected_units_and_values(self):
        section = rect(2, 6, -1, 4)
        self.assertEqual(polygon_area_mm2(section), 20.0)
        self.assertEqual(net_section_area_mm2([section]), 20.0)
        # Integral(radius dA) = height * (r1^2 - r0^2) / 2.
        self.assertAlmostEqual(polygon_radial_first_moment_mm3(section), 80.0)
        self.assertAlmostEqual(net_radial_first_moment_mm3([section]), 80.0)

    def test_polygon_orientation_does_not_change_independent_area_or_volume(self):
        ccw, cw = rect(2, 5, 0, 3), rect(2, 5, 0, 3, reverse=True)
        self.assertAlmostEqual(net_section_area_mm2([ccw]), net_section_area_mm2([cw]))
        self.assertAlmostEqual(net_radial_first_moment_mm3([ccw]), net_radial_first_moment_mm3([cw]))
        self.assertAlmostEqual(revolved_solid_volume_mm3([ccw], 0, 360),
                               revolved_solid_volume_mm3([cw], 0, 360), places=12)

    def test_multiple_outer_regions_and_hole_are_summed_without_production_classifier(self):
        outer = rect(2, 6, 0, 5)
        hole = rect(3, 4, 1, 2, role="hole")
        island = rect(8, 9, 0, 1)
        self.assertAlmostEqual(net_section_area_mm2([outer, hole, island]), 20.0 - 1.0 + 1.0)
        self.assertAlmostEqual(net_radial_first_moment_mm3([outer, hole, island]), 80.0 - 3.5 + 8.5)
        self.assertAlmostEqual(revolved_solid_volume_mm3([outer, hole, island], 0, 360), 2.0 * math.pi * 85.0)

    def test_signed_partial_and_full_annular_revolve_volumes_are_analytic(self):
        section = [rect(10, 20, -3, 5)]
        full = revolved_solid_volume_mm3(section, 0, 360)
        self.assertAlmostEqual(full, math.pi * (20**2 - 10**2) * 8.0, places=12)
        partial = revolved_solid_volume_mm3(section, -30, 210)
        self.assertAlmostEqual(partial, math.radians(240) * (20**2 - 10**2) * 8.0 / 2.0, places=12)
        reverse = revolved_solid_volume_mm3(section, 210, -30)
        self.assertAlmostEqual(reverse, partial, places=12)

    def test_same_span_different_angle_phase_has_same_volume_but_different_bounds(self):
        section = [rect(2, 5, 0, 3)]
        self.assertAlmostEqual(revolved_solid_volume_mm3(section, 30, 150),
                               revolved_solid_volume_mm3(section, 0, 120), places=12)
        frame = make_axis_frame((0, 0, 0), (0, 0, 1), (1, 0, 0))
        first = revolved_profile_bounds_mm(section, frame, 30, 150)
        second = revolved_profile_bounds_mm(section, frame, 0, 120)
        self.assertNotEqual(first, second)

    def test_partial_sweep_bounds_include_interior_extrema_exactly(self):
        section = [rect(2, 5, -1, 4)]
        frame = make_axis_frame((0, 0, 0), (0, 0, 1), (1, 0, 0))
        bounds = revolved_profile_bounds_mm(section, frame, -30, 210)
        expected = ((-5.0, -2.5, -1.0), (5.0, 5.0, 4.0))
        for actual_point, expected_point in zip(bounds, expected):
            for actual, value in zip(actual_point, expected_point):
                self.assertAlmostEqual(actual, value, places=12)

    def test_full_cylinder_bounds_for_oblique_axis_use_closed_form_support(self):
        point, direction = (3.0, -4.0, 7.0), (1.0, 2.0, 3.0)
        low, high = full_revolve_cylinder_bounds_mm(point, direction, 5.0, -2.0, 6.0)
        unit = tuple(value / math.sqrt(14.0) for value in direction)
        endpoints = [tuple(point[i] + t * unit[i] for i in range(3)) for t in (-2.0, 6.0)]
        for coordinate in range(3):
            reach = 5.0 * math.sqrt(1.0 - unit[coordinate] ** 2)
            self.assertAlmostEqual(low[coordinate], min(item[coordinate] for item in endpoints) - reach, places=13)
            self.assertAlmostEqual(high[coordinate], max(item[coordinate] for item in endpoints) + reach, places=13)

    def test_full_ellipse_revolve_bounds_use_exact_oblique_support_not_outer_cylinder(self):
        frame = make_axis_frame((3.0, -4.0, 7.0), (1.0, 2.0, 3.0), (0.0, 0.0, 1.0))
        low, high = full_revolved_ellipse_bounds_mm(frame, 40.0, 4.0, 4.0, 2.0)
        for coordinate, axis_component in enumerate(frame.direction):
            transverse = math.sqrt(1.0 - axis_component**2)
            center = frame.point_mm[coordinate] + 4.0 * axis_component
            reach = 40.0 * transverse + math.hypot(4.0 * transverse, 2.0 * axis_component)
            self.assertAlmostEqual(low[coordinate], center - reach, places=13)
            self.assertAlmostEqual(high[coordinate], center + reach, places=13)
        outer_cylinder = full_revolve_cylinder_bounds_mm(frame.point_mm, frame.direction, 44.0, 2.0, 6.0)
        self.assertNotEqual((low, high), outer_cylinder)

    def test_sheet_surface_area_covers_open_line_partial_and_full_revolution(self):
        open_axial_line = ((7.5, -2.0), (7.5, 4.0))
        self.assertAlmostEqual(revolved_sheet_area_mm2(open_axial_line, -30, 210),
                               math.radians(240) * 7.5 * 6.0, places=12)
        self.assertAlmostEqual(revolved_sheet_area_mm2(open_axial_line, 0, 360),
                               2.0 * math.pi * 7.5 * 6.0, places=12)

    def test_slanted_sheet_section_uses_integral_of_radius_over_segment_length(self):
        section = ((2.0, 1.0), (6.0, 4.0))
        expected = 1.5 * math.hypot(4.0, 3.0) * ((2.0 + 6.0) / 2.0)
        self.assertAlmostEqual(revolved_sheet_area_mm2(section, 10, 10 + math.degrees(1.5)), expected, places=11)

    def test_invalid_open_loop_and_negative_radial_oracle_fixture_are_rejected(self):
        with self.assertRaises(ValueError):
            polygon_area_mm2(PolygonLoop(((2, 0), (4, 0), (4, 1)), "outer"))
        with self.assertRaises(ValueError):
            revolved_solid_volume_mm3([rect(-2, 4, 0, 1)], 0, 90)


if __name__ == "__main__":
    unittest.main()
