"""Independent reflected rectangular-box B-rep oracle; no NX or product imports.

Input JSON: seed and actual each have vertices_mm (8 points), edges (12 pairs
of zero-based vertex indices), faces (6 lists of 4 edge indices). actual also
has face_areas_mm2 (6 independent native measurements, face order) and
volume_mm3 (independent native measurement). plane_origin_mm, plane_normal,
tolerance_mm, tolerance_mm2 and tolerance_mm3 are required. Edges must be
straight and faces planar trimmed quadrilaterals, established by the caller.
The oracle tests all coordinates, adjacency, per-face area and body volume.
No identity, feature provenance, mirror plane association, or undo claim follows
from geometrical equivalence. Input indices and ordering are arbitrary.
CLI: python f19-geometry-oracle.py INPUT.json; exit 0 pass, 1 mismatch, 2 bad input.
"""
import json
import math
import sys


def number(x, name):
    if isinstance(x, bool) or not isinstance(x, (int, float)):
        raise ValueError(name + ' must be numeric')
    try:
        x = float(x)
    except OverflowError as exc:
        raise ValueError(name + ' must be finite') from exc
    if not math.isfinite(x):
        raise ValueError(name + ' must be finite')
    return x


def point(x, name):
    if not isinstance(x, list) or len(x) != 3:
        raise ValueError(name + ' must contain three coordinates')
    return tuple(number(v, name) for v in x)


def sub(a, b):
    return tuple(x-y for x, y in zip(a, b))


def dot(a, b):
    return math.fsum(x*y for x, y in zip(a, b))


def body(raw, measured, tol):
    keys = {'vertices_mm', 'edges', 'faces'} | ({'face_areas_mm2', 'volume_mm3'} if measured else set())
    if not isinstance(raw, dict) or set(raw) != keys:
        raise ValueError('body fields do not match contract')
    vs = raw['vertices_mm']
    if not isinstance(vs, list) or len(vs) != 8:
        raise ValueError('body requires eight vertices')
    vs = [point(v, 'vertex') for v in vs]
    if any(math.dist(vs[i], vs[j]) <= tol for i in range(8) for j in range(i)):
        raise ValueError('coincident or ambiguous vertices')
    def index_set(x, count, bound):
        if not isinstance(x, list) or len(x) != count or any(type(i) is not int or i < 0 or i >= bound for i in x) or len(set(x)) != count:
            raise ValueError('invalid topology index')
        return frozenset(x)
    es, fs = raw['edges'], raw['faces']
    if not isinstance(es, list) or len(es) != 12 or not isinstance(fs, list) or len(fs) != 6:
        raise ValueError('body requires twelve edges and six faces')
    es = [index_set(e, 2, 8) for e in es]
    fs = [index_set(f, 4, 12) for f in fs]
    if len(set(es)) != 12 or len(set(fs)) != 6:
        raise ValueError('duplicate edge or face')
    if any(sum(v in e for e in es) != 3 for v in range(8)) or any(sum(i in f for f in fs) != 2 for i in range(12)):
        raise ValueError('bad edge or face incidence')
    for f in fs:
        if sorted(sum(v in es[i] for i in f) for v in range(8)) != [0, 0, 0, 0, 2, 2, 2, 2]:
            raise ValueError('face is not a four-edge cycle')
    areas = None
    volume = None
    if measured:
        if not isinstance(raw['face_areas_mm2'], list) or len(raw['face_areas_mm2']) != 6:
            raise ValueError('six measured face areas required')
        areas = [number(a, 'area') for a in raw['face_areas_mm2']]
        volume = number(raw['volume_mm3'], 'volume')
        if min(areas) <= 0 or volume <= 0:
            raise ValueError('positive area and volume required')
    return vs, es, fs, areas, volume


def box_dimensions(b, tol):
    vs, es, fs, _, _ = b
    neighbors = [next(iter(e - {0})) for e in es if 0 in e]
    vectors = [sub(vs[i], vs[0]) for i in neighbors]
    lengths = [math.hypot(*v) for v in vectors]
    if any(not math.isfinite(x) or x <= tol for x in lengths):
        raise ValueError('degenerate box')
    unit = [tuple(x / length for x in vector) for vector, length in zip(vectors, lengths)]
    if any(abs(dot(unit[i], unit[j])) > 1e-9 for i in range(3) for j in range(i)):
        raise ValueError('seed is not rectangular')
    masks = {}
    for m in range(8):
        p = tuple(vs[0][k] + sum(vectors[j][k] for j in range(3) if m & (1 << j)) for k in range(3))
        hits = [i for i, q in enumerate(vs) if math.dist(p, q) <= tol]
        if len(hits) != 1 or hits[0] in masks.values():
            raise ValueError('seed is not a rectangular box')
        masks[m] = hits[0]
    expected_edges = {frozenset((masks[m], masks[m ^ (1 << j)])) for m in range(8) for j in range(3)}
    if set(es) != expected_edges:
        raise ValueError('seed edge graph is not a box')
    expected_faces = {frozenset(i for i, e in enumerate(es) if all(bool(m & (1 << a)) == bool(s) for m, v in masks.items() if v in e)) for a in range(3) for s in (0, 1)}
    if set(fs) != expected_faces:
        raise ValueError('seed face graph is not a box')
    return lengths


def check(data):
    required = {'seed', 'actual', 'plane_origin_mm', 'plane_normal', 'tolerance_mm', 'tolerance_mm2', 'tolerance_mm3'}
    if not isinstance(data, dict) or set(data) != required:
        raise ValueError('input fields do not match contract')
    t, ta, tv = [number(data[k], k) for k in ('tolerance_mm', 'tolerance_mm2', 'tolerance_mm3')]
    if min(t, ta, tv) <= 0:
        raise ValueError('tolerances must be positive')
    seed, actual = body(data['seed'], False, t), body(data['actual'], True, t)
    lengths = box_dimensions(seed, t)
    p, n = point(data['plane_origin_mm'], 'plane origin'), point(data['plane_normal'], 'plane normal')
    norm = math.hypot(*n)
    if not math.isfinite(norm) or norm == 0:
        raise ValueError('nonzero finite normal required')
    n = tuple(x / norm for x in n)
    expected = [tuple(v[k] - 2 * n[k] * dot(n, sub(v, p)) for k in range(3)) for v in seed[0]]
    if not all(math.isfinite(x) for v in expected for x in v):
        raise ValueError('reflection overflow')
    matches = [[j for j, q in enumerate(actual[0]) if math.dist(v, q) <= t] for v in expected]
    if any(len(m) != 1 for m in matches) or len({m[0] for m in matches}) != 8:
        return {'ok': False, 'reason': 'vertices do not uniquely match reflection'}
    mapping = [m[0] for m in matches]
    ae = {e: i for i, e in enumerate(actual[1])}
    edge_map = []
    for e in seed[1]:
        transformed = frozenset(mapping[i] for i in e)
        if transformed not in ae:
            return {'ok': False, 'reason': 'edge endpoints mismatch'}
        edge_map.append(ae[transformed])
    af = {f: i for i, f in enumerate(actual[2])}
    face_map = []
    for face in seed[2]:
        transformed = frozenset(edge_map[i] for i in face)
        if transformed not in af:
            return {'ok': False, 'reason': 'face-edge adjacency mismatch'}
        face_map.append(af[transformed])
    for i, face in enumerate(seed[2]):
        face_lengths = [math.dist(seed[0][a], seed[0][b]) for a, b in (tuple(seed[1][e]) for e in face)]
        area = min(face_lengths) * max(face_lengths)
        if not math.isfinite(area) or abs(actual[3][face_map[i]] - area) > ta:
            return {'ok': False, 'reason': 'face area mismatch', 'seed_face': i, 'expected': area, 'actual': actual[3][face_map[i]]}
    volume = math.prod(lengths)
    if not math.isfinite(volume) or abs(actual[4] - volume) > tv:
        return {'ok': False, 'reason': 'body volume mismatch', 'expected': volume, 'actual': actual[4]}
    return {'ok': True, 'vertex_map': mapping, 'edge_map': edge_map, 'face_map': face_map, 'volume_mm3': volume}


def main():
    try:
        if len(sys.argv) != 2:
            raise ValueError('supply one input JSON path')
        with open(sys.argv[1], encoding='utf-8') as f:
            result = check(json.load(f))
        print(json.dumps(result, allow_nan=False))
        return 0 if result['ok'] else 1
    except (ValueError, OSError, KeyError, TypeError) as exc:
        print(json.dumps({'ok': False, 'error': str(exc)}))
        return 2


if __name__ == '__main__':
    sys.exit(main())
