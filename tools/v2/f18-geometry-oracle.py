"""Offline F18 rigid-pattern geometry and topology oracle.

JSON contract (coordinates in mm, angles in degrees):
  {"seed": BODY, "pattern": PATTERN, "instances": [BODY, ...],
   "tolerance_mm": 0.000001}
BODY is {"vertices_mm": [[x,y,z], ...], "edges": [[v0,v1], ...],
         "faces": [[e0,e1,e2,e3], ...]}; indexes are local, zero based.
Exactly 8 vertices, 12 straight edges and 6 quadrilateral faces are required;
edge endpoints and face edge order are unoriented. Each BODY is one separate
instance, including the seed instance. The seed must be a rectangular box.
PATTERN uses the frozen F18 mode/count/direction/pitch/axis/angle fields only;
count includes the seed. Angles are signed right-hand rotation steps about the
world-space axis through axis_origin_mm. Counts describe the full grid.
Bodies and their local indexes may be arbitrarily ordered. One-to-one matching
checks every vertex, every edge endpoint and every face-edge adjacency. A seed
or transformed instance with coincident vertices within tolerance is rejected.
An indistinguishable pair of expected instances is rejected as ambiguous;
duplicate actual instances are rejected. Unit directions are normalized after
the stated 1e-9 input check.
Because an unlabelled box is symmetric, geometry cannot distinguish rotations
that leave the complete box invariant. Such frame distinctions require separate
oriented provenance evidence; this oracle makes no claim to establish them.

Usage: python tools/v2/f18-geometry-oracle.py input.json
Prints JSON: {"ok": true, "matches": [actual_index,...]} in expected order;
exit 0 on match, 1 on mismatch, 2 on malformed input. No NX access is made.
"""
import json
import math
import sys


def _number(value, name):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(name + " must be a finite number")
    try:
        value = float(value)
    except OverflowError as exc:
        raise ValueError(name + " must be a finite number") from exc
    if not math.isfinite(value):
        raise ValueError(name + " must be a finite number")
    return value


def _point(value, name):
    if not isinstance(value, list) or len(value) != 3:
        raise ValueError(name + " must be an array of three numbers")
    return tuple(_number(v, name) for v in value)


def _sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def _dist(a, b):
    return math.dist(a, b)


def _dot(a, b):
    return math.fsum(x * y for x, y in zip(a, b))


def _body(raw, tol, name):
    if not isinstance(raw, dict) or set(raw) != {"vertices_mm", "edges", "faces"}:
        raise ValueError(name + " must contain exactly vertices_mm, edges and faces")
    vertices, edges, faces = raw["vertices_mm"], raw["edges"], raw["faces"]
    if not isinstance(vertices, list) or len(vertices) != 8:
        raise ValueError(name + " must have 8 vertices")
    points = [_point(v, name + ".vertices_mm") for v in vertices]
    if any(_dist(points[i], points[j]) <= tol for i in range(8) for j in range(i)):
        raise ValueError(name + " vertices must be distinct beyond tolerance")

    def indices(values, length, upper, field):
        if not isinstance(values, list) or len(values) != length:
            raise ValueError(name + "." + field + " has the wrong length")
        if any(isinstance(v, bool) or not isinstance(v, int) or not 0 <= v < upper for v in values):
            raise ValueError(name + "." + field + " has an invalid index")
        if len(set(values)) != length:
            raise ValueError(name + "." + field + " repeats an index")
        return frozenset(values)

    if not isinstance(edges, list) or len(edges) != 12:
        raise ValueError(name + " must have 12 edges")
    edge_sets = [indices(edge, 2, 8, "edges") for edge in edges]
    if len(set(edge_sets)) != 12 or any(sum(v in e for e in edge_sets) != 3 for v in range(8)):
        raise ValueError(name + " edge graph is not a box")
    if not isinstance(faces, list) or len(faces) != 6:
        raise ValueError(name + " must have 6 faces")
    face_sets = [indices(face, 4, 12, "faces") for face in faces]
    if len(set(face_sets)) != 6 or any(sum(e in face for face in face_sets) != 2 for e in range(12)):
        raise ValueError(name + " faces must use each edge twice")
    for face in face_sets:
        degrees = [sum(v in edge_sets[e] for e in face) for v in range(8)]
        if sorted(d for d in degrees if d) != [2, 2, 2, 2]:
            raise ValueError(name + " face edges must form a four-edge cycle")
    # Represent faces directly by their constituent vertex-pair edges.
    face_edges = frozenset(frozenset(edge_sets[e] for e in face) for face in face_sets)
    return points, frozenset(edge_sets), face_edges


def _validate_box(body, tol):
    points, edges, faces = body
    neighbors = [next(iter(e - {0})) for e in edges if 0 in e]
    vectors = [_sub(points[n], points[0]) for n in neighbors]
    lengths = [math.hypot(*v) for v in vectors]
    if any(not math.isfinite(v) or v <= tol for v in lengths):
        raise ValueError("seed edges must have finite nonzero lengths")
    unit_vectors = [tuple(component / length for component in vector)
                    for vector, length in zip(vectors, lengths)]
    if any(abs(_dot(unit_vectors[i], unit_vectors[j])) > 1e-9
           for i in range(3) for j in range(i)):
        raise ValueError("seed edges must meet at right angles")
    masks = {}
    for mask in range(8):
        expected = tuple(points[0][k] + sum(vectors[j][k] for j in range(3) if mask & (1 << j))
                         for k in range(3))
        candidates = [i for i, p in enumerate(points) if _dist(p, expected) <= tol]
        if len(candidates) != 1 or candidates[0] in masks.values():
            raise ValueError("seed vertices do not form a rectangular box")
        masks[mask] = candidates[0]
    expected_edges = frozenset(frozenset((masks[mask], masks[mask ^ (1 << j)]))
                               for mask in range(8) for j in range(3))
    if edges != expected_edges:
        raise ValueError("seed edge topology does not form a rectangular box")
    expected_faces = frozenset(
        frozenset(e for e in edges if all(bool(mask & (1 << axis)) == bool(side)
                                          for mask, vertex in masks.items() if vertex in e))
        for axis in range(3) for side in (0, 1))
    if faces != expected_faces:
        raise ValueError("seed face topology does not form a rectangular box")


def _pattern(pattern):
    if not isinstance(pattern, dict):
        raise ValueError("pattern must be an object")
    mode = pattern.get("mode")
    fields = {"linear": {"direction1", "pitch1_mm"},
              "rectangular": {"direction1", "pitch1_mm", "direction2", "pitch2_mm", "count2"},
              "circular": {"axis_origin_mm", "axis_direction", "angle_deg"}}
    if not isinstance(mode, str) or mode not in fields or set(pattern) != {"mode", "count1"} | fields[mode]:
        raise ValueError("pattern has unsupported mode or fields")

    def count(name):
        n = pattern[name]
        if isinstance(n, bool) or not isinstance(n, int) or not 2 <= n <= 32:
            raise ValueError(name + " must be an integer in 2..32")
        return n

    def unit(name):
        v = _point(pattern[name], name)
        length = math.hypot(*v)
        if abs(length - 1.0) > 1e-9:
            raise ValueError(name + " must be a unit vector within 1e-9")
        return tuple(component / length for component in v)

    n = count("count1")
    if mode == "circular":
        origin, axis = _point(pattern["axis_origin_mm"], "axis_origin_mm"), unit("axis_direction")
        angle = _number(pattern["angle_deg"], "angle_deg")
        if angle == 0 or abs(angle) * (n - 1) >= 360:
            raise ValueError("angle step must be nonzero and span less than 360 degrees")
        return [("rotate", origin, axis, math.radians(i * angle)) for i in range(n)]
    u, pitch = unit("direction1"), _number(pattern["pitch1_mm"], "pitch1_mm")
    if pitch <= 0:
        raise ValueError("pitch1_mm must be positive")
    if mode == "linear":
        return [("translate", tuple(i * pitch * x for x in u)) for i in range(n)]
    v, pitch2, n2 = unit("direction2"), _number(pattern["pitch2_mm"], "pitch2_mm"), count("count2")
    if pitch2 <= 0 or n * n2 > 32 or abs(_dot(u, v)) > 1e-9:
        raise ValueError("rectangular pitch, count product or orthogonality is invalid")
    return [("translate", tuple(i * pitch * u[k] + j * pitch2 * v[k] for k in range(3)))
            for i in range(n) for j in range(n2)]


def _transform(point, transform):
    if transform[0] == "translate":
        return tuple(x + d for x, d in zip(point, transform[1]))
    _, origin, axis, angle = transform
    x, y, z = _sub(point, origin)
    a, b, c = axis
    cosine, sine = math.cos(angle), math.sin(angle)
    dot = a * x + b * y + c * z
    return tuple(origin[k] + value for k, value in enumerate((
        x * cosine + (b * z - c * y) * sine + a * dot * (1 - cosine),
        y * cosine + (c * x - a * z) * sine + b * dot * (1 - cosine),
        z * cosine + (a * y - b * x) * sine + c * dot * (1 - cosine))))


def _matches(expected, actual, tolerance):
    ep, ee, ef = expected
    ap, ae, af = actual
    candidates = [[j for j, actual_point in enumerate(ap) if _dist(point, actual_point) <= tolerance]
                  for point in ep]
    if any(not c for c in candidates):
        return False
    order = sorted(range(8), key=lambda i: len(candidates[i]))
    mapping = {}

    def search(depth):
        if depth == 8:
            mapped_edges = frozenset(frozenset(mapping[v] for v in edge) for edge in ee)
            if mapped_edges != ae:
                return False
            mapped_faces = frozenset(frozenset(frozenset(mapping[v] for v in edge) for edge in face)
                                     for face in ef)
            return mapped_faces == af
        i = order[depth]
        for j in candidates[i]:
            if j in mapping.values():
                continue
            mapping[i] = j
            if search(depth + 1):
                return True
            del mapping[i]
        return False
    return search(0)


def verify(document):
    """Validate and compare JSON-compatible input; return match result or raise ValueError."""
    if not isinstance(document, dict) or not {"seed", "pattern", "instances"} <= set(document) or set(document) - {"seed", "pattern", "instances", "tolerance_mm"}:
        raise ValueError("document must contain seed, pattern, instances and optional tolerance_mm")
    tol = _number(document.get("tolerance_mm", 1e-6), "tolerance_mm")
    if tol <= 0:
        raise ValueError("tolerance_mm must be positive")
    seed = _body(document["seed"], tol, "seed")
    _validate_box(seed, tol)
    transforms = _pattern(document["pattern"])
    instances = document["instances"]
    if not isinstance(instances, list):
        raise ValueError("instances must be an array")
    if len(instances) != len(transforms):
        return {"ok": False, "reason": "instance_count_mismatch", "expected": len(transforms), "actual": len(instances)}
    actual = [_body(body, tol, "instances[" + str(i) + "]") for i, body in enumerate(instances)]
    expected = [([_transform(p, transform) for p in seed[0]], seed[1], seed[2]) for transform in transforms]
    if any(not all(math.isfinite(x) for p in body[0] for x in p) for body in expected):
        raise ValueError("transformed coordinates must be finite")
    if any(_dist(body[0][i], body[0][j]) <= tol for body in expected for i in range(8) for j in range(i)):
        raise ValueError("transformed vertices must remain distinct beyond tolerance")
    if any(_matches(expected[i], expected[j], tol) for i in range(len(expected)) for j in range(i)):
        raise ValueError("expected instances are indistinguishable within tolerance")
    if any(_matches(actual[i], actual[j], tol) for i in range(len(actual)) for j in range(i)):
        return {"ok": False, "reason": "duplicate_instances"}
    choices = [[i for i, body in enumerate(actual) if _matches(exp, body, tol)] for exp in expected]
    # Bipartite augmenting paths ensure every expected and actual instance is used exactly once.
    assigned = {}
    def augment(e, seen):
        for a in choices[e]:
            if a in seen:
                continue
            seen.add(a)
            if a not in assigned or augment(assigned[a], seen):
                assigned[a] = e
                return True
        return False
    for e in range(len(expected)):
        if not augment(e, set()):
            return {"ok": False, "reason": "instance_geometry_or_topology_mismatch", "expected_index": e}
    result = [None] * len(expected)
    for a, e in assigned.items():
        result[e] = a
    return {"ok": True, "matches": result}


def main(argv):
    if len(argv) != 2:
        print(json.dumps({"ok": False, "error": "usage: f18-geometry-oracle.py input.json"}))
        return 2
    try:
        with open(argv[1], encoding="utf-8") as file:
            result = verify(json.load(file))
    except (ValueError, OSError, OverflowError) as exc:
        print(json.dumps({"ok": False, "error": str(exc)}, allow_nan=False))
        return 2
    print(json.dumps(result, allow_nan=False))
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
