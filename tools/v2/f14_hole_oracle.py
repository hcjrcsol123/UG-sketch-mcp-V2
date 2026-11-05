"""Analytical fixture oracle, independent of NX and production hole code.

Volumes assume an unbounded planar entry, a cylindrical bore entirely inside
the target, and no overlaps with any other cuts. Depth is to cylinder bottom;
the optional conical drill tip extends beyond it. Through length is the actual
stock thickness along the axis, not a nominal feature depth.
"""

import math


def _positive(value, name):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
        raise ValueError(name + " must be finite and positive")
    return float(value)


def _angle(value, name):
    value = _positive(value, name)
    if value >= 180:
        raise ValueError(name + " must be less than 180 degrees")
    return math.radians(value)


def bore_removed_volume_mm3(diameter_mm, cylinder_length_mm, tip_angle_deg=None):
    """Bore cylinder plus optional full conical drill tip at the bottom."""
    radius = _positive(diameter_mm, "diameter") / 2
    length = _positive(cylinder_length_mm, "cylinder length")
    volume = math.pi * radius * radius * length
    if tip_angle_deg is not None:
        tip_height = radius / math.tan(_angle(tip_angle_deg, "tip angle") / 2)
        volume += math.pi * radius * radius * tip_height / 3
    return volume


def counterbore_removed_volume_mm3(diameter_mm, cylinder_length_mm,
                                   counterbore_diameter_mm, counterbore_depth_mm,
                                   tip_angle_deg=None):
    diameter = _positive(diameter_mm, "diameter")
    length = _positive(cylinder_length_mm, "cylinder length")
    upper_diameter = _positive(counterbore_diameter_mm, "counterbore diameter")
    upper_depth = _positive(counterbore_depth_mm, "counterbore depth")
    if upper_diameter <= diameter or upper_depth >= length:
        raise ValueError("counterbore must be wider and shallower than the cylindrical bore")
    return (bore_removed_volume_mm3(diameter, length, tip_angle_deg)
            + math.pi * (upper_diameter ** 2 - diameter ** 2) * upper_depth / 4)


def countersink_removed_volume_mm3(diameter_mm, cylinder_length_mm,
                                   countersink_diameter_mm, included_angle_deg,
                                   tip_angle_deg=None):
    diameter = _positive(diameter_mm, "diameter")
    length = _positive(cylinder_length_mm, "cylinder length")
    upper_diameter = _positive(countersink_diameter_mm, "countersink diameter")
    if upper_diameter <= diameter:
        raise ValueError("countersink must be wider than the bore")
    r, upper_r = diameter / 2, upper_diameter / 2
    depth = (upper_r - r) / math.tan(_angle(included_angle_deg, "countersink angle") / 2)
    if depth >= length:
        raise ValueError("countersink must be shallower than the cylindrical bore")
    extra = math.pi * depth * (upper_r * upper_r + upper_r * r - 2 * r * r) / 3
    return bore_removed_volume_mm3(diameter, length, tip_angle_deg) + extra
