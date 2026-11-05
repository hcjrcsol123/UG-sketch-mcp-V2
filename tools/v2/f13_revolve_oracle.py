"""Independent analytic geometry oracles for F13 revolve fixtures.

This module does not import the production adapter, production geometry
matcher, NXOpen, or any MCP implementation. Inputs are a planar section in
local ``(radius_mm, axial_mm)`` coordinates and a directed world-space axis.
The frame's radial direction is the zero-angle direction; positive angles use
the right-hand rule around the normalized axis.
"""

from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Sequence

Vec2 = tuple[float, float]
Vec3 = tuple[float, float, float]
Bounds3 = tuple[Vec3, Vec3]


@dataclass(frozen=True)
class PolygonLoop:
    """One closed section boundary in (radius, axial) millimeter units."""

    points_mm: tuple[Vec2, ...]
    role: str = "outer"
    label: str = ""


@dataclass(frozen=True)
class AxisFrame:
    """Directed axis and its orthonormal right-handed section frame."""

    point_mm: Vec3
    direction: Vec3
    radial_zero: Vec3
    radial_quadrature: Vec3


def _finite_vector(values: Sequence[float], label: str, size: int) -> tuple[float, ...]:
    if not isinstance(values, (list, tuple)) or len(values) != size:
        raise ValueError(f"{label} must have {size} values")
    if any(isinstance(value, bool) or not isinstance(value, (int, float)) for value in values):
        raise ValueError(f"{label} values must be numeric (booleans are not accepted)")
    result = tuple(float(value) for value in values)
    if not all(math.isfinite(value) for value in result):
        raise ValueError(f"{label} values must be finite")
    return result


def _dot(left: Vec3, right: Vec3) -> float:
    return sum(a * b for a, b in zip(left, right))


def _cross(left: Vec3, right: Vec3) -> Vec3:
    return (left[1] * right[2] - left[2] * right[1],
            left[2] * right[0] - left[0] * right[2],
            left[0] * right[1] - left[1] * right[0])


def _scale(vector: Vec3, scalar: float) -> Vec3:
    return tuple(value * scalar for value in vector)  # type: ignore[return-value]


def _add(left: Vec3, right: Vec3) -> Vec3:
    return tuple(a + b for a, b in zip(left, right))  # type: ignore[return-value]


def _sub(left: Vec3, right: Vec3) -> Vec3:
    return tuple(a - b for a, b in zip(left, right))  # type: ignore[return-value]


def _norm(vector: Vec3) -> float:
    return math.hypot(*vector)


def normalize_axis(point_mm: Sequence[float], direction: Sequence[float]) -> tuple[Vec3, Vec3]:
    point = _finite_vector(point_mm, "axis point", 3)
    vector = _finite_vector(direction, "axis direction", 3)
    maximum = max(abs(value) for value in vector)
    if maximum == 0.0:
        raise ValueError("axis direction must be nonzero")
    scaled = tuple(value / maximum for value in vector)
    scaled_magnitude = math.hypot(*scaled)
    unit = tuple(value / scaled_magnitude for value in scaled)
    return point, unit  # type: ignore[return-value]


def make_axis_frame(point_mm: Sequence[float], direction: Sequence[float],
                    radial_seed: Sequence[float]) -> AxisFrame:
    point, unit = normalize_axis(point_mm, direction)
    seed = _finite_vector(radial_seed, "radial seed", 3)
    seed3: Vec3 = seed  # type: ignore[assignment]
    projected = _sub(seed3, _scale(unit, _dot(seed3, unit)))
    projected_norm = _norm(projected)
    if projected_norm <= 1.0e-12:
        raise ValueError("radial seed must not be parallel to the revolve axis")
    radial_zero = _scale(projected, 1.0 / projected_norm)
    radial_quadrature = _cross(unit, radial_zero)
    return AxisFrame(point, unit, radial_zero, radial_quadrature)


def rotate_point_about_axis(point_mm: Sequence[float], axis_point_mm: Sequence[float],
                            axis_direction: Sequence[float], angle_deg: float) -> Vec3:
    """Rotate one world point using Rodrigues' formula and a directed axis."""

    point = _finite_vector(point_mm, "point", 3)
    origin, unit = normalize_axis(axis_point_mm, axis_direction)
    angle = float(angle_deg)
    if not math.isfinite(angle):
        raise ValueError("rotation angle must be finite")
    relative: Vec3 = _sub(point, origin)  # type: ignore[assignment]
    radians = math.radians(angle)
    cosine, sine = math.cos(radians), math.sin(radians)
    rotated = _add(_add(_scale(relative, cosine), _scale(_cross(unit, relative), sine)),
                   _scale(unit, _dot(unit, relative) * (1.0 - cosine)))
    return _add(origin, rotated)


def validate_angle_interval(start_angle_deg: float, end_angle_deg: float) -> float:
    """Return the signed interval after checking the locked F13 angle domain."""

    start, end = float(start_angle_deg), float(end_angle_deg)
    if not math.isfinite(start) or not math.isfinite(end):
        raise ValueError("start and end angles must be finite")
    if not (-360.0 <= start <= 360.0 and -360.0 <= end <= 360.0):
        raise ValueError("angle endpoints must be inside [-360, 360] degrees")
    span = end - start
    if abs(span) <= 1.0e-9 or abs(span) > 360.0:
        raise ValueError("signed angle span magnitude must exceed 1e-9 and not exceed 360 degrees")
    return span


def validate_profile_output(mode: str, profile_output: str, target_body_id: str | None) -> None:
    """Check the public F13 mode rules independently of production code."""

    if mode not in {"create", "unite", "subtract"}:
        raise ValueError("mode must be create, unite, or subtract")
    if profile_output not in {"solid", "sheet"}:
        raise ValueError("profile_output must be solid or sheet")
    if profile_output == "sheet" and mode != "create":
        raise ValueError("sheet output supports create only")
    if mode == "create" and target_body_id is not None:
        raise ValueError("create forbids a target body")
    if mode in {"unite", "subtract"} and not target_body_id:
        raise ValueError("unite and subtract require an explicit target body")


def _closed_points(loop: PolygonLoop) -> tuple[Vec2, ...]:
    if loop.role not in {"outer", "hole"}:
        raise ValueError("loop role must be outer or hole")
    points = tuple(_finite_vector(point, "section point", 2) for point in loop.points_mm)
    if len(points) < 4 or points[0] != points[-1]:
        raise ValueError("a closed polygon needs at least three vertices and a repeated endpoint")
    if min(point[0] for point in points) < -1.0e-12:
        raise ValueError("oracle fixtures must stay on the nonnegative radial side")
    return points  # type: ignore[return-value]


def polygon_area_mm2(loop: PolygonLoop) -> float:
    points = _closed_points(loop)
    doubled = sum(first[0] * second[1] - second[0] * first[1]
                  for first, second in zip(points, points[1:]))
    return 0.5 * doubled


def polygon_radial_first_moment_mm3(loop: PolygonLoop) -> float:
    """Return signed integral of radius over the polygon area (mm^3)."""

    points = _closed_points(loop)
    total = 0.0
    for (r0, h0), (r1, h1) in zip(points, points[1:]):
        total += (r0 * r0 + r0 * r1 + r1 * r1) * (h1 - h0) / 6.0
    return total


def net_section_area_mm2(loops: Sequence[PolygonLoop]) -> float:
    """Sum explicitly labeled outer contours and holes, independent of winding."""

    if not loops:
        raise ValueError("at least one section loop is required")
    area = 0.0
    for loop in loops:
        signed = polygon_area_mm2(loop)
        if abs(signed) <= 1.0e-12:
            raise ValueError("section loop area must be nonzero")
        area += abs(signed) if loop.role == "outer" else -abs(signed)
    if not math.isfinite(area) or area <= 0.0:
        raise ValueError("net section area must be positive")
    return area


def net_radial_first_moment_mm3(loops: Sequence[PolygonLoop]) -> float:
    """Return ``integral(section radius dA)`` after subtracting labeled holes."""

    if not loops:
        raise ValueError("at least one section loop is required")
    moment = 0.0
    for loop in loops:
        signed = polygon_radial_first_moment_mm3(loop)
        if abs(signed) <= 1.0e-12:
            raise ValueError("section radial first moment must be nonzero")
        moment += abs(signed) if loop.role == "outer" else -abs(signed)
    if not math.isfinite(moment) or moment <= 0.0:
        raise ValueError("net radial first moment must be positive")
    return moment


def revolved_solid_volume_mm3(loops: Sequence[PolygonLoop],
                              start_angle_deg: float, end_angle_deg: float) -> float:
    """Analytic volume from ``Δθ × integral(radius dA)`` for a valid section."""

    span_deg = validate_angle_interval(start_angle_deg, end_angle_deg)
    result = abs(math.radians(span_deg)) * net_radial_first_moment_mm3(loops)
    if not math.isfinite(result) or result <= 0.0:
        raise ValueError("revolved volume must be finite and positive")
    return result


def revolved_sheet_area_mm2(polyline_mm: Sequence[Sequence[float]],
                            start_angle_deg: float, end_angle_deg: float) -> float:
    """Area of the surface swept by an open polyline in the axis-section plane.

    Each line segment contributes ``abs(Δθ) × integral(radius ds)``. For a
    straight section segment, radius varies linearly with arc length.
    """

    points = tuple(_finite_vector(point, "sheet section point", 2) for point in polyline_mm)
    if len(points) < 2:
        raise ValueError("a sheet section needs at least two points")
    if min(point[0] for point in points) < -1.0e-12:
        raise ValueError("oracle fixtures must stay on the nonnegative radial side")
    span = abs(math.radians(validate_angle_interval(start_angle_deg, end_angle_deg)))
    integral = 0.0
    for (r0, h0), (r1, h1) in zip(points, points[1:]):
        segment_length = math.hypot(r1 - r0, h1 - h0)
        integral += segment_length * (r0 + r1) * 0.5
    result = span * integral
    if not math.isfinite(result) or result <= 0.0:
        raise ValueError("sheet area must be finite and positive")
    return result


def _candidate_angles(start_rad: float, end_rad: float, cosine_coefficient: float,
                      sine_coefficient: float) -> tuple[float, ...]:
    """Endpoints plus every in-range stationary angle for a*cos(t)+b*sin(t)."""

    low, high = sorted((start_rad, end_rad))
    candidates = [start_rad, end_rad]
    base = math.atan2(sine_coefficient, cosine_coefficient)
    first_k = math.floor((low - base) / math.pi) - 1
    last_k = math.ceil((high - base) / math.pi) + 1
    for multiple in range(first_k, last_k + 1):
        value = base + multiple * math.pi
        if low <= value <= high:
            candidates.append(value)
    return tuple(candidates)


def revolved_profile_bounds_mm(loops: Sequence[PolygonLoop], frame: AxisFrame,
                               start_angle_deg: float, end_angle_deg: float) -> Bounds3:
    """Exact axis-aligned bounds for polygonal section boundaries over a sweep.

    For each boundary vertex and world coordinate, extrema occur at a sweep
    endpoint or when that coordinate's sinusoidal radial component is
    stationary. Polygon interiors cannot exceed boundary extrema for a linear
    coordinate at fixed angle.
    """

    validate_angle_interval(start_angle_deg, end_angle_deg)
    if not loops:
        raise ValueError("at least one section loop is required")
    start_rad, end_rad = math.radians(start_angle_deg), math.radians(end_angle_deg)
    values: list[list[float]] = [[], [], []]
    for loop in loops:
        for radius, axial in _closed_points(loop):
            center = _add(frame.point_mm, _scale(frame.direction, axial))
            for coordinate in range(3):
                coefficient_cos = radius * frame.radial_zero[coordinate]
                coefficient_sin = radius * frame.radial_quadrature[coordinate]
                angles = _candidate_angles(start_rad, end_rad, coefficient_cos, coefficient_sin)
                values[coordinate].extend(
                    center[coordinate] + coefficient_cos * math.cos(theta) +
                    coefficient_sin * math.sin(theta) for theta in angles)
    low: Vec3 = tuple(min(axis_values) for axis_values in values)  # type: ignore[assignment]
    high: Vec3 = tuple(max(axis_values) for axis_values in values)  # type: ignore[assignment]
    return low, high


def full_revolve_cylinder_bounds_mm(axis_point_mm: Sequence[float], axis_direction: Sequence[float],
                                    radius_mm: float, axial_start_mm: float,
                                    axial_end_mm: float) -> Bounds3:
    """Closed-form bounds of a finite solid cylinder on an arbitrary axis."""

    point, axis = normalize_axis(axis_point_mm, axis_direction)
    radius, start, end = float(radius_mm), float(axial_start_mm), float(axial_end_mm)
    if not all(math.isfinite(value) for value in (radius, start, end)) or radius <= 0.0 or start == end:
        raise ValueError("cylinder radius must be positive and finite, with distinct finite axial limits")
    end_points = (_add(point, _scale(axis, start)), _add(point, _scale(axis, end)))
    lows, highs = [], []
    for coordinate in range(3):
        radial_reach = radius * math.sqrt(max(0.0, 1.0 - axis[coordinate] ** 2))
        lows.append(min(item[coordinate] for item in end_points) - radial_reach)
        highs.append(max(item[coordinate] for item in end_points) + radial_reach)
    return tuple(lows), tuple(highs)  # type: ignore[return-value]


def full_revolved_ellipse_bounds_mm(frame: AxisFrame, center_radius_mm: float,
                                    major_radius_mm: float, axial_center_mm: float,
                                    minor_radius_mm: float) -> Bounds3:
    """Exact world AABB of a full revolve of a radial/axial ellipse section.

    The ellipse major axis is radial and its minor axis is parallel to the
    revolve axis. Maximizing over the ellipse parameter and full azimuth gives
    a per-coordinate radial reach of ``R*sqrt(1-u_j²) +
    sqrt((a*sqrt(1-u_j²))² + (b*u_j)²)``. Treating the whole section as an
    outer cylinder would overestimate the exact support for an oblique axis.
    """

    center_radius = float(center_radius_mm)
    major = float(major_radius_mm)
    axial_center = float(axial_center_mm)
    minor = float(minor_radius_mm)
    if not all(math.isfinite(value) for value in (center_radius, major, axial_center, minor)):
        raise ValueError("ellipse inputs must be finite")
    if center_radius <= major or major <= 0.0 or minor <= 0.0:
        raise ValueError("ellipse must have positive radii and stay strictly on the positive radial side")
    low, high = [], []
    for coordinate, axis_component in enumerate(frame.direction):
        transverse = math.sqrt(max(0.0, 1.0 - axis_component * axis_component))
        center = frame.point_mm[coordinate] + axial_center * axis_component
        reach = center_radius * transverse + math.hypot(major * transverse, minor * axis_component)
        low.append(center - reach)
        high.append(center + reach)
    return tuple(low), tuple(high)  # type: ignore[return-value]


def box_volume_mm3(bounds: Bounds3) -> float:
    low, high = bounds
    return math.prod(max(0.0, high[index] - low[index]) for index in range(3))
