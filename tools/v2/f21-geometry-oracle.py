"""Independent straight translational sweep B-rep oracle; no NX/product imports.

Profile vertices are ordered, world-space millimetres. Closed profile describes
one simple planar polygon; open profile describes a polyline whose membership in
the requested planar Sketch must be proved separately. A single
translation vector defines the sweep; no twist or corner path is implied.
Actual NX reader output must include every vertex, edge, and face with incidence,
curve/surface types, independently measured face areas, body type, and volume
for a solid. All tolerances are explicit; unordered entity indices are allowed.
Failure to provide native geometric types or complete topology is a failure.
This module checks geometry, never feature association or persistence.
"""
from __future__ import annotations
import json
import math
import sys


def _num(value, name):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(name + ' must be numeric')
    try:
        value = float(value)
    except OverflowError as exc:
        raise ValueError(name + ' must be finite') from exc
    if not math.isfinite(value):
        raise ValueError(name + ' must be finite')
    return value


def _point(value, name):
    if not isinstance(value, list) or len(value) != 3:
        raise ValueError(name + ' must be a three-vector')
    return tuple(_num(x, name) for x in value)


def _sub(a, b): return tuple(x-y for x, y in zip(a, b))
def _add(a, b): return tuple(x+y for x, y in zip(a, b))
def _cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def _dot(a, b): return math.fsum(x*y for x, y in zip(a, b))
def _norm(a): return math.hypot(*a)


def _indices(value, length, bound, name):
    if not isinstance(value, list) or len(value) != length or any(type(x) is not int or x < 0 or x >= bound for x in value) or len(set(value)) != length:
        raise ValueError('invalid ' + name)
    return frozenset(value)


def check(data):
    required = {'profile_mm', 'closed', 'translation_mm', 'actual', 'tolerance_mm', 'tolerance_mm2', 'tolerance_mm3'}
    if not isinstance(data, dict) or set(data) != required:
        raise ValueError('oracle input fields differ')
    tol, area_tol, volume_tol = (_num(data[k], k) for k in ('tolerance_mm','tolerance_mm2','tolerance_mm3'))
    if min(tol, area_tol, volume_tol) <= 0: raise ValueError('tolerances must be positive')
    closed = data['closed']
    if type(closed) is not bool: raise ValueError('closed must be boolean')
    raw = data['profile_mm']
    if not isinstance(raw, list) or len(raw) < (3 if closed else 2): raise ValueError('insufficient profile vertices')
    profile = [_point(p, 'profile vertex') for p in raw]
    n = len(profile)
    if any(math.dist(profile[i], profile[j]) <= tol for i in range(n) for j in range(i)):
        raise ValueError('duplicate or ambiguous profile vertices')
    v = _point(data['translation_mm'], 'translation')
    if _norm(v) <= tol: raise ValueError('degenerate translation')
    count = n if closed else n-1
    segments = [(i, (i+1)%n) for i in range(count)]
    if any(_norm(_cross(_sub(profile[j], profile[i]), v)) <= area_tol for i,j in segments):
        raise ValueError('degenerate side face')
    # Contract restricts closed fixtures to simple convex polygons; this makes
    # area and face loops independently unambiguous and fails closed on concavity.
    polygon_area = 0.0
    volume = None
    if closed:
        normal = _cross(_sub(profile[1], profile[0]), _sub(profile[2], profile[1]))
        size = _norm(normal)
        if size <= area_tol: raise ValueError('collinear initial polygon vertices')
        normal = tuple(x/size for x in normal)
        if any(abs(_dot(_sub(p, profile[0]),normal)) > tol for p in profile): raise ValueError('profile not planar')
        turns = [_dot(_cross(_sub(profile[(i+1)%n],profile[i]), _sub(profile[(i+2)%n],profile[(i+1)%n])), normal) for i in range(n)]
        if any(t <= area_tol for t in turns): raise ValueError('profile must be strictly convex and ordered')
        # Every vertex must be strictly on the inner side of each edge. This
        # excludes self-intersecting star polygons that have equal local turns.
        for i,j in segments:
            edge = _sub(profile[j],profile[i])
            if any(_dot(_cross(edge,_sub(profile[k],profile[i])), normal) <= area_tol for k in range(n) if k not in (i,j)):
                raise ValueError('profile must be a simple convex polygon')
        area_vector = (0.0,0.0,0.0)
        for i,j in segments:
            c = _cross(_sub(profile[i],profile[0]),_sub(profile[j],profile[0]))
            area_vector = _add(area_vector,c)
        polygon_area = _norm(area_vector)/2
        volume = abs(_dot(area_vector,v))/2
        if volume <= volume_tol: raise ValueError('degenerate sweep volume')
    expected_vertices = profile + [_add(p,v) for p in profile]
    if any(math.dist(expected_vertices[i],expected_vertices[j]) <= tol for i in range(2*n) for j in range(i)):
        raise ValueError('sweep vertices ambiguous')
    expected_edges = [frozenset((i,j)) for i,j in segments] + [frozenset((i+n,j+n)) for i,j in segments] + [frozenset((i,i+n)) for i in range(n)]
    edge_map = {e:i for i,e in enumerate(expected_edges)}
    expected_faces = []
    for i,j in segments:
        edges = [frozenset((i,j)),frozenset((i+n,j+n)),frozenset((i,i+n)),frozenset((j,j+n))]
        expected_faces.append((frozenset(edge_map[e] for e in edges), _norm(_cross(_sub(profile[j],profile[i]),v))))
    if closed:
        expected_faces.extend([(frozenset(range(count)),polygon_area), (frozenset(range(count,2*count)),polygon_area)])
    actual = data['actual']
    keys = {'body_type','vertices_mm','edges','faces','volume_mm3'}
    if not isinstance(actual, dict) or set(actual) != keys: raise ValueError('actual fields differ')
    if actual['body_type'] != ('solid' if closed else 'sheet'): raise AssertionError('wrong body type')
    av = actual['vertices_mm']; ae = actual['edges']; af = actual['faces']
    if not isinstance(av,list) or len(av)!=2*n or not isinstance(ae,list) or len(ae)!=len(expected_edges) or not isinstance(af,list) or len(af)!=len(expected_faces):
        raise AssertionError('incomplete or extra topology')
    av = [_point(p,'actual vertex') for p in av]
    mapping = []
    for p in av:
        hits = [i for i,q in enumerate(expected_vertices) if math.dist(p,q)<=tol]
        if len(hits)!=1 or hits[0] in mapping: raise AssertionError('vertex mismatch or ambiguous matching')
        mapping.append(hits[0])
    edge_indices = []
    for e in ae:
        if not isinstance(e,dict) or set(e)!={'vertices','curve_type'}: raise ValueError('edge fields differ')
        if e['curve_type'] != 'LINE': raise AssertionError('edge is not native LINE')
        idx = _indices(e['vertices'],2,len(av),'edge vertices')
        mapped = frozenset(mapping[i] for i in idx)
        if mapped not in edge_map or edge_map[mapped] in edge_indices: raise AssertionError('edge incidence mismatch')
        edge_indices.append(edge_map[mapped])
    unmatched = list(expected_faces)
    for f in af:
        if not isinstance(f,dict) or set(f)!={'edges','surface_type','area_mm2','plane_origin_mm','plane_normal'}: raise ValueError('face fields differ')
        if f['surface_type'] != 'PLANE': raise AssertionError('face is not native PLANE')
        if not isinstance(f['edges'],list): raise ValueError('face edges must be list')
        idx = _indices(f['edges'],len(f['edges']),len(ae),'face edges')
        mapped = frozenset(edge_indices[i] for i in idx)
        matches = [i for i,(edges,_) in enumerate(unmatched) if edges==mapped]
        if len(matches)!=1: raise AssertionError('face incidence mismatch')
        _, expected_area = unmatched.pop(matches[0])
        origin = _point(f['plane_origin_mm'],'plane origin')
        normal = _point(f['plane_normal'],'plane normal')
        length = _norm(normal)
        if length < 1e-12: raise AssertionError('invalid native plane normal')
        for edge_index in idx:
            for vertex_index in ae[edge_index]['vertices']:
                if abs(_dot(_sub(av[vertex_index],origin),normal))/length > tol:
                    raise AssertionError('native plane does not contain boundary')
        if abs(_num(f['area_mm2'],'face area')-expected_area)>area_tol: raise AssertionError('face area mismatch')
    if closed:
        if abs(_num(actual['volume_mm3'],'volume')-volume)>volume_tol: raise AssertionError('volume mismatch')
    elif actual['volume_mm3'] is not None:
        raise ValueError('sheet volume must be null')
    return {'passed':True,'scope':'straight translational sweep geometry only','vertex_count':2*n,'edge_count':len(expected_edges),'face_count':len(expected_faces),'expected_volume_mm3':volume}


def main():
    if len(sys.argv)!=2: raise SystemExit('usage: f21-geometry-oracle.py INPUT.json')
    try:
        with open(sys.argv[1],encoding='utf-8-sig') as stream: result=check(json.load(stream))
        print(json.dumps(result)); return 0
    except (ValueError,AssertionError,KeyError,TypeError) as exc:
        print(json.dumps({'passed':False,'error':str(exc)})); return 1


if __name__=='__main__': sys.exit(main())
