"""Independent Euclidean oracle for F10 orthographic projection checks.

This helper is test-only. It does not import NXOpen or production adapters.
All positions use millimetres; directions are dimensionless.
"""
from __future__ import annotations

import math
from collections.abc import Sequence


def finite_vector(value: object, size: int, label: str) -> list[float]:
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)) or len(value) != size:
        raise ValueError(label + " must contain exactly " + str(size) + " numbers")
    result: list[float] = []
    for component in value:
        if isinstance(component, bool):
            raise ValueError(label + " components must be finite numbers")
        try:
            number = float(component)
        except (TypeError, ValueError, OverflowError) as error:
            raise ValueError(label + " components must be finite numbers") from error
        if not math.isfinite(number):
            raise ValueError(label + " components must be finite numbers")
        result.append(number)
    return result


def dot(a: Sequence[float], b: Sequence[float]) -> float:
    if len(a) != len(b):
        raise ValueError("dot product vectors must have equal length")
    return math.fsum(float(a[i]) * float(b[i]) for i in range(len(a)))


def cross3(a: Sequence[float], b: Sequence[float]) -> list[float]:
    if len(a) != 3 or len(b) != 3:
        raise ValueError("cross product requires three-dimensional vectors")
    return [float(a[1]) * float(b[2]) - float(a[2]) * float(b[1]),
            float(a[2]) * float(b[0]) - float(a[0]) * float(b[2]),
            float(a[0]) * float(b[1]) - float(a[1]) * float(b[0])]


def norm(value: Sequence[float]) -> float:
    scale = max((abs(float(component)) for component in value), default=0.0)
    if not math.isfinite(scale):
        return math.inf
    if scale == 0.0:
        return 0.0
    return scale * math.sqrt(math.fsum((float(component) / scale) ** 2 for component in value))


def normalize(value: Sequence[float], label: str = "vector") -> list[float]:
    values = finite_vector(value, len(value), label)
    scale = max(abs(component) for component in values)
    if scale == 0.0:
        raise ValueError(label + " must be non-zero")
    scaled = [component / scale for component in values]
    scaled_norm = math.sqrt(math.fsum(component * component for component in scaled))
    return [component / scaled_norm for component in scaled]


def direction_is_normal(direction: Sequence[float], plane_normal: Sequence[float],
                        angular_tolerance_rad: float = 1.0e-10) -> bool:
    """Accept either sign of a direction parallel to the target plane normal."""
    if not math.isfinite(angular_tolerance_rad) or angular_tolerance_rad < 0.0:
        raise ValueError("angular tolerance must be finite and non-negative")
    d = normalize(direction, "direction")
    n = normalize(plane_normal, "plane normal")
    return norm(cross3(d, n)) <= math.sin(angular_tolerance_rad)


def project_point_along_direction(point: Sequence[float], plane_origin: Sequence[float],
                                  plane_normal: Sequence[float], direction: Sequence[float],
                                  parallel_tolerance: float = 1.0e-12) -> list[float]:
    """Intersect point + t*direction with the plane through origin with normal n."""
    p = finite_vector(point, 3, "point")
    origin = finite_vector(plane_origin, 3, "plane origin")
    n = normalize(plane_normal, "plane normal")
    d = normalize(direction, "direction")
    denominator = dot(n, d)
    if abs(denominator) <= parallel_tolerance:
        raise ValueError("projection direction is parallel to the target plane")
    offset = [p[i] - origin[i] for i in range(3)]
    distance_along_direction = -dot(n, offset) / denominator
    projected = [p[i] + distance_along_direction * d[i] for i in range(3)]
    if not all(math.isfinite(component) for component in projected):
        raise ValueError("projection produced non-finite coordinates")
    return projected


def project_line_along_direction(start: Sequence[float], end: Sequence[float],
                                 plane_origin: Sequence[float], plane_normal: Sequence[float],
                                 direction: Sequence[float]) -> tuple[list[float], list[float]]:
    return (project_point_along_direction(start, plane_origin, plane_normal, direction),
            project_point_along_direction(end, plane_origin, plane_normal, direction))


def distance3(a: Sequence[float], b: Sequence[float]) -> float:
    left = finite_vector(a, 3, "left point")
    right = finite_vector(b, 3, "right point")
    return norm([left[i] - right[i] for i in range(3)])


def line_matches(actual_start: Sequence[float], actual_end: Sequence[float],
                 expected_start: Sequence[float], expected_end: Sequence[float],
                 position_tolerance_mm: float) -> dict:
    if not math.isfinite(position_tolerance_mm) or position_tolerance_mm < 0.0:
        raise ValueError("position tolerance must be finite and non-negative")
    direct = (distance3(actual_start, expected_start), distance3(actual_end, expected_end))
    reversed_ends = (distance3(actual_start, expected_end), distance3(actual_end, expected_start))
    use_reversed = max(reversed_ends) < max(direct)
    residuals = reversed_ends if use_reversed else direct
    return {"matches": max(residuals) <= position_tolerance_mm,
            "endpoint_residuals_mm": list(residuals),
            "max_residual_mm": max(residuals),
            "endpoint_order_reversed": use_reversed,
            "position_tolerance_mm": position_tolerance_mm}
