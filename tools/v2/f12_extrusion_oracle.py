"""Independent 2D profile and swept-volume calculations for F12 fixtures.

This module is deliberately independent of NXOpen, the production profile
analyzer, and adapter-reported body measurements. It supports deterministic
fixture calculations and convergence checks over point samples read directly
from native curves.
"""

from __future__ import annotations

from dataclasses import dataclass
import json
import math
from typing import Iterable, Sequence

Point2 = tuple[float, float]
Point3 = tuple[float, float, float]
Box3 = tuple[Point3, Point3]


@dataclass(frozen=True)
class Loop:
    """One closed planar boundary, sampled in traversal order.

    ``exact_area_mm2`` can supply the analytic area for a full conic. The
    points are still required for bounds and nesting. Set it to ``None`` for
    polygons or native curves whose area is being numerically integrated.
    """

    points_mm: tuple[Point2, ...]
    exact_area_mm2: float | None = None
    label: str = ""


def signed_polygon_area_mm2(points_mm: Sequence[Point2]) -> float:
    """Return signed Green/shoelace area for a closed sampled contour."""

    if len(points_mm) < 4 or points_mm[0] != points_mm[-1]:
        raise ValueError("a closed loop needs at least three vertices and a repeated endpoint")
    total = 0.0
    for first, second in zip(points_mm, points_mm[1:]):
        total += first[0] * second[1] - second[0] * first[1]
    return 0.5 * total


def loop_area_mm2(loop: Loop) -> float:
    area = loop.exact_area_mm2
    if area is None:
        area = abs(signed_polygon_area_mm2(loop.points_mm))
    if not math.isfinite(area) or area <= 0.0:
        raise ValueError("loop area must be finite and greater than zero")
    return float(area)


def point_in_loop(point: Point2, loop: Loop) -> bool:
    """Even/odd point-in-polygon check on the independently sampled contour."""

    x, y = point
    inside = False
    points = loop.points_mm
    if len(points) < 4 or points[0] != points[-1]:
        raise ValueError("containment requires a closed sampled contour")
    for first, second in zip(points, points[1:]):
        x1, y1 = first
        x2, y2 = second
        if (y1 > y) != (y2 > y):
            crossing_x = x1 + (y - y1) * (x2 - x1) / (y2 - y1)
            if x < crossing_x:
                inside = not inside
    return inside


def loop_nesting_depths(loops: Sequence[Loop]) -> list[int]:
    """Classify disjoint/nested simple loops independently of winding order."""

    if not loops:
        return []
    depths: list[int] = []
    for index, loop in enumerate(loops):
        witness = loop.points_mm[0]
        containers = [
            other_index
            for other_index, other in enumerate(loops)
            if other_index != index and loop_area_mm2(other) > loop_area_mm2(loop)
            and point_in_loop(witness, other)
        ]
        # Simple non-overlapping contours form a containment tree. The loop's
        # depth is the number of enclosing boundaries, regardless of traversal.
        depths.append(len(containers))
    return depths


def net_profile_area_mm2(loops: Sequence[Loop]) -> float:
    """Return outer/island area minus holes using nesting parity."""

    depths = loop_nesting_depths(loops)
    result = sum((1.0 if depth % 2 == 0 else -1.0) * loop_area_mm2(loop)
                 for loop, depth in zip(loops, depths))
    if not math.isfinite(result) or result <= 0.0:
        raise ValueError("profile has no positive net area")
    return result


def ellipse_loop(center_mm: Point2, major_radius_mm: float, minor_radius_mm: float,
                 rotation_deg: float = 0.0, samples: int = 720, label: str = "ellipse") -> Loop:
    """Create an independently sampled full ellipse with exact analytic area."""

    if not all(math.isfinite(value) for value in (*center_mm, major_radius_mm,
                                                   minor_radius_mm, rotation_deg)):
        raise ValueError("ellipse inputs must be finite")
    if major_radius_mm <= 0.0 or minor_radius_mm <= 0.0 or samples < 16:
        raise ValueError("ellipse radii must be positive and at least 16 samples are required")
    angle = math.radians(rotation_deg)
    cosine, sine = math.cos(angle), math.sin(angle)
    points = []
    for index in range(samples):
        theta = 2.0 * math.pi * index / samples
        x = major_radius_mm * math.cos(theta)
        y = minor_radius_mm * math.sin(theta)
        points.append((center_mm[0] + x * cosine - y * sine,
                       center_mm[1] + x * sine + y * cosine))
    points.append(points[0])
    return Loop(tuple(points), math.pi * major_radius_mm * minor_radius_mm, label)


def net_area_convergence_mm2(coarse: Sequence[Loop], fine: Sequence[Loop]) -> dict[str, float]:
    """Compare independent native-curve sample sets at successive resolutions."""

    coarse_area = net_profile_area_mm2(coarse)
    fine_area = net_profile_area_mm2(fine)
    return {
        "coarse_area_mm2": coarse_area,
        "fine_area_mm2": fine_area,
        "absolute_delta_mm2": abs(fine_area - coarse_area),
        "relative_delta": abs(fine_area - coarse_area) / max(abs(fine_area), 1.0e-300),
    }


def swept_volume_mm3(area_mm2: float, start_offset_mm: float, end_offset_mm: float) -> float:
    if not all(math.isfinite(value) for value in (area_mm2, start_offset_mm, end_offset_mm)):
        raise ValueError("area and finite extrusion limits must be finite")
    if area_mm2 <= 0.0 or end_offset_mm <= start_offset_mm:
        raise ValueError("area must be positive and end offset must exceed start offset")
    return area_mm2 * (end_offset_mm - start_offset_mm)


def legacy_depth_volume_mm3(area_mm2: float, depth_mm: float) -> float:
    if not math.isfinite(depth_mm) or depth_mm <= 0.0:
        raise ValueError("legacy finite depth must be finite and positive")
    return swept_volume_mm3(area_mm2, 0.0, depth_mm)


def swept_world_bounds_mm(profile_bounds_mm: tuple[float, float, float, float],
                          origin_mm: Point3, x_axis: Point3, y_axis: Point3,
                          normal: Point3, start_offset_mm: float, end_offset_mm: float,
                          direction: str) -> Box3:
    """Calculate a conservative world AABB from a sketch-frame profile box."""

    if direction not in {"positive", "negative"}:
        raise ValueError("direction must be positive or negative")
    if end_offset_mm <= start_offset_mm:
        raise ValueError("end offset must exceed start offset")
    sign = 1.0 if direction == "positive" else -1.0
    x_min, x_max, y_min, y_max = profile_bounds_mm
    points: list[Point3] = []
    for x in (x_min, x_max):
        for y in (y_min, y_max):
            for offset in (start_offset_mm, end_offset_mm):
                points.append(tuple(
                    origin_mm[axis] + x * x_axis[axis] + y * y_axis[axis]
                    + sign * offset * normal[axis]
                    for axis in range(3)
                ))
    return (
        tuple(min(point[axis] for point in points) for axis in range(3)),
        tuple(max(point[axis] for point in points) for axis in range(3)),
    )  # type: ignore[return-value]


def box_volume_mm3(box: Box3) -> float:
    lower, upper = box
    dimensions = tuple(upper[index] - lower[index] for index in range(3))
    if any(value < 0.0 or not math.isfinite(value) for value in dimensions):
        raise ValueError("box bounds must be finite and ordered")
    return dimensions[0] * dimensions[1] * dimensions[2]


def box_intersection_mm3(first: Box3, second: Box3) -> float:
    first_min, first_max = first
    second_min, second_max = second
    overlap = tuple(max(0.0, min(first_max[index], second_max[index])
                        - max(first_min[index], second_min[index])) for index in range(3))
    return overlap[0] * overlap[1] * overlap[2]


def boolean_box_volume_mm3(mode: str, target: Box3, tool: Box3) -> float:
    target_volume = box_volume_mm3(target)
    tool_volume = box_volume_mm3(tool)
    intersection = box_intersection_mm3(target, tool)
    if mode == "unite":
        return target_volume + tool_volume - intersection
    if mode == "subtract":
        return target_volume - intersection
    raise ValueError("box Boolean oracle supports unite or subtract")


def fixture_expectations() -> dict[str, object]:
    outer = Loop(((0.0, 0.0), (20.0, 0.0), (20.0, 20.0), (0.0, 20.0), (0.0, 0.0)), label="outer")
    hole = ellipse_loop((8.0, 6.0), 2.0, 2.0, label="circular_hole")
    island = Loop(((30.0, 0.0), (40.0, 0.0), (40.0, 10.0), (30.0, 10.0), (30.0, 0.0)), label="island")
    ellipse = ellipse_loop((50.0, 0.0), 6.0, 2.5, 27.0, label="full_ellipse")
    multi_region_area = net_profile_area_mm2((outer, hole, island))
    ellipse_area = net_profile_area_mm2((ellipse,))
    return {
        "units": {"length": "mm", "area": "mm^2", "volume": "mm^3"},
        "multi_region_with_hole": {
            "loop_areas_mm2": [loop_area_mm2(loop) for loop in (outer, hole, island)],
            "nesting_depths": loop_nesting_depths((outer, hole, island)),
            "net_area_mm2": multi_region_area,
            "volume_start_minus_2_end_8_mm3": swept_volume_mm3(multi_region_area, -2.0, 8.0),
        },
        "rotated_ellipse": {
            "analytic_area_mm2": ellipse_area,
            "volume_depth_7_mm3": legacy_depth_volume_mm3(ellipse_area, 7.0),
        },
    }


def main() -> int:
    print(json.dumps(fixture_expectations(), sort_keys=True, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
