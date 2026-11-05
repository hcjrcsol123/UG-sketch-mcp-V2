"""Independent analytical checks for the rectangular F16 fixture (no NX calls)."""
import math

VOLUME_TOL_MM3 = 1e-5
LENGTH_TOL_MM = 1e-6
AXIS_TOL = 1e-9


def finite_number(value, name):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise AssertionError(name + " must be a finite number")
    return float(value)


def close(actual, expected, tolerance, name):
    value = finite_number(actual, name)
    if abs(value - expected) > tolerance:
        raise AssertionError(f"{name}: {value} differs from {expected} by more than {tolerance}")


def expected_volume(edge_count):
    if isinstance(edge_count, bool) or edge_count not in (1, 2):
        raise ValueError("fixture covers one or two nonadjacent edges")
    return 20.0 * 12.0 * 10.0 - edge_count * 10.0 * 2.0**2 * (1.0 - math.pi / 4.0)


def check_volume(actual, edge_count):
    close(actual, expected_volume(edge_count), VOLUME_TOL_MM3, "volume mm^3")


def check_box(actual, x_origin):
    if not isinstance(actual, (list, tuple)) or len(actual) != 6:
        raise AssertionError("bounding box must have six coordinates")
    expected = [x_origin, 0.0, 0.0, x_origin + 20.0, 12.0, 10.0]
    for index, (value, reference) in enumerate(zip(actual, expected)):
        close(value, reference, LENGTH_TOL_MM, f"bbox[{index}] mm")


def check_cylinder(radius, axis_point, axis_direction, x_origin, corner):
    """Check infinite cylindrical support; trimmed face and adjacency require separate evidence."""
    close(radius, 2.0, LENGTH_TOL_MM, "cylinder radius mm")
    if corner not in ("low", "high"):
        raise ValueError("unknown fixture corner")
    if not isinstance(axis_point, (list, tuple)) or len(axis_point) != 3:
        raise AssertionError("axis point must have three coordinates")
    if not isinstance(axis_direction, (list, tuple)) or len(axis_direction) != 3:
        raise AssertionError("axis direction must have three coordinates")
    point = [finite_number(v, "axis point") for v in axis_point]
    direction = [finite_number(v, "axis direction") for v in axis_direction]
    norm = math.sqrt(sum(v * v for v in direction))
    if norm == 0 or not math.isfinite(norm):
        raise AssertionError("axis direction is zero or unrepresentable")
    if math.hypot(direction[0], direction[1]) / norm > AXIS_TOL:
        raise AssertionError("cylinder axis is not parallel to Z")
    # Check where the infinite axis crosses z=5, allowing a point anywhere on it.
    t = (5.0 - point[2]) / direction[2]
    expected = (x_origin + 2.0, 2.0) if corner == "low" else (x_origin + 18.0, 10.0)
    for index in range(2):
        close(point[index] + t * direction[index], expected[index], LENGTH_TOL_MM, "cylinder axis position mm")


def check_containment(actual, expected):
    """UF point containment: 1 inside, 2 outside. Boundary is never accepted."""
    if isinstance(actual, bool) or actual not in (1, 2) or actual != expected:
        raise AssertionError(f"point containment {actual!r}; expected {expected!r}")
