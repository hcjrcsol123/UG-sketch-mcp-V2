"""Independent analytic geometry for one chamfer on an axis-aligned box.

All coordinates and lengths are millimetres, angles degrees and volumes mm^3.
This is a mathematical oracle, not a model reader or product acceptance test.
"""

import math


_AXES = {"x": 0, "y": 1, "z": 2}


def _number(value, name):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(name + " must be a finite number")
    try:
        result = float(value)
    except OverflowError as exc:
        raise ValueError(name + " must be a finite number") from exc
    if not math.isfinite(result):
        raise ValueError(name + " must be a finite number")
    return result


def _point(value, name):
    try:
        if len(value) != 3:
            raise ValueError(name + " must contain three numbers")
        return tuple(_number(v, name) for v in value)
    except TypeError as exc:
        raise ValueError(name + " must contain three numbers") from exc


def chamfer_box(lower_mm, upper_mm, edge_axis, edge_point_mm, reference_axis,
                mode, distance_mm, *, distance2_mm=None, angle_deg=None):
    """Calculate the single-edge chamfer and strictly interior witness points.

    lower_mm/upper_mm define the world-axis box. edge_axis and reference_axis
    are lowercase x/y/z; edge_point_mm identifies an entire box edge by its
    two transverse boundary coordinates. The point's edge-axis coordinate may
    be anywhere in the inclusive edge span. reference_axis is the first
    *inward* transverse distance direction, never the edge axis.

    mode is equal_distance, two_distances, or distance_angle. Only the latter
    accepts angle_deg (0 < angle < 90); its other intercept is distance*tan.
    Only two_distances accepts distance2_mm. Intercepts must be strictly inside
    their box sides. Returned normal points into the retained material; the
    signed plane distance dot(normal, point - plane_point_mm) is positive there.
    Witness margins are minimum distances to the six original box planes and
    the chamfer plane; they are conservative distances to actual surfaces.
    A ValueError also rejects geometry not representable with positive margins.
    """
    lo, hi, edge = (_point(lower_mm, "lower_mm"), _point(upper_mm, "upper_mm"),
                    _point(edge_point_mm, "edge_point_mm"))
    if edge_axis not in _AXES or reference_axis not in _AXES:
        raise ValueError("axes must be lowercase x, y or z")
    e, a = _AXES[edge_axis], _AXES[reference_axis]
    if e == a:
        raise ValueError("reference axis must be transverse to the edge")
    b = next(i for i in range(3) if i not in (e, a))
    sizes = tuple(hi[i] - lo[i] for i in range(3))
    if any(not math.isfinite(x) or x <= 0 for x in sizes):
        raise ValueError("box dimensions must be finite and strictly positive")
    if not lo[e] <= edge[e] <= hi[e]:
        raise ValueError("edge point lies outside the edge span")
    if any(edge[i] != lo[i] and edge[i] != hi[i] for i in (a, b)):
        raise ValueError("edge point must lie on two box boundary planes")
    sign = {i: 1.0 if edge[i] == lo[i] else -1.0 for i in (a, b)}
    d1 = _number(distance_mm, "distance_mm")
    if d1 <= 0:
        raise ValueError("distance_mm must be positive")
    if mode == "equal_distance" and distance2_mm is None and angle_deg is None:
        d2 = d1
    elif mode == "two_distances" and distance2_mm is not None and angle_deg is None:
        d2 = _number(distance2_mm, "distance2_mm")
    elif mode == "distance_angle" and distance2_mm is None and angle_deg is not None:
        angle = _number(angle_deg, "angle_deg")
        if not 0 < angle < 90:
            raise ValueError("angle_deg must be strictly between 0 and 90")
        d2 = d1 * math.tan(math.radians(angle))
    else:
        raise ValueError("mode and optional parameters are inconsistent")
    if not math.isfinite(d2) or d2 <= 0 or d1 >= sizes[a] or d2 >= sizes[b]:
        raise ValueError("intercepts must be finite, positive and inside box sides")

    corner = list(edge)
    corner[e] = lo[e]
    p1, p2 = list(corner), list(corner)
    p1[a] += sign[a] * d1
    p2[b] += sign[b] * d2
    if not lo[a] < p1[a] < hi[a] or not lo[b] < p2[b] < hi[b]:
        raise ValueError("intercepts are not representable inside the box")
    slope_length = math.hypot(d1, d2)
    normal = [0.0, 0.0, 0.0]
    normal[a] = sign[a] * (d2 / slope_length)
    normal[b] = sign[b] * (d1 / slope_length)
    removed_volume = (d1 * 0.5) * d2 * sizes[e]
    box_volume = sizes[0] * sizes[1] * sizes[2]
    face_area = slope_length * sizes[e]
    remaining_volume = box_volume - removed_volume
    if not all(math.isfinite(v) and v > 0 for v in
               (slope_length, removed_volume, box_volume, face_area, remaining_volume)):
        raise ValueError("derived geometry is not finite and positive")

    def witness(fraction):
        p = list(corner)
        p[e] = lo[e] + sizes[e] * 0.5
        p[a] += sign[a] * d1 * fraction
        p[b] += sign[b] * d2 * fraction
        signed = math.fsum(normal[i] * (p[i] - p1[i]) for i in range(3))
        margin = min(*(p[i] - lo[i] for i in range(3)),
                     *(hi[i] - p[i] for i in range(3)), abs(signed))
        if not math.isfinite(margin) or margin <= 0:
            raise ValueError("witness is not representable away from all planes")
        return {"point_mm": tuple(p), "signed_plane_distance_mm": signed,
                "min_plane_distance_mm": margin}

    removed, retained = witness(0.2), witness(0.75)
    if not removed["signed_plane_distance_mm"] < 0 < retained["signed_plane_distance_mm"]:
        raise ValueError("witness classification is not numerically representable")
    p1_far, p2_far = list(p1), list(p2)
    p1_far[e] = p2_far[e] = hi[e]
    return {
        "reference_axis": reference_axis,
        "other_axis": "xyz"[b],
        "intercepts_mm": (d1, d2),
        "intercept_points_mm": (tuple(p1), tuple(p2)),
        "plane_point_mm": tuple(p1),
        "plane_normal_into_retained": tuple(normal),
        "face_vertices_mm": (tuple(p1), tuple(p2), tuple(p2_far), tuple(p1_far)),
        "face_area_mm2": face_area,
        "removed_volume_mm3": removed_volume,
        "remaining_volume_mm3": remaining_volume,
        "removed_witness": removed,
        "retained_witness": retained,
    }
