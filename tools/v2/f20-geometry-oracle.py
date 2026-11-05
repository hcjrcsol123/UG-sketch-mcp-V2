"""Independent spatial-curve geometry check. Input: expected request, native NX readings.

All tolerances are predeclared absolute limits; this is not an NX test or a
manufacturing-accuracy claim. Do not supply product-predicted values as readings.
"""
import json
import math
import sys

POS = 1e-6
ANGLE = 1e-7
FRACTIONS = (0.0, 0.25, 0.5, 0.75, 1.0)

def vec(v):
    if not isinstance(v, (tuple, list)) or len(v) != 3 or any(isinstance(x, bool) or not isinstance(x, (float, int)) or not math.isfinite(x) for x in v):
        raise ValueError('expected finite 3-vector')
    return tuple(float(x) for x in v)

def add(a, b): return tuple(x+y for x,y in zip(a,b))
def sub(a, b): return tuple(x-y for x,y in zip(a,b))
def mul(a, n): return tuple(x*n for x in a)
def dot(a, b): return sum(x*y for x,y in zip(a,b))
def cross(a,b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def norm(a): return math.sqrt(dot(a,a))
def unit(a):
    d=norm(a)
    if d < 1e-12: raise ValueError('degenerate vector')
    return mul(a,1/d)
def dist(a,b): return norm(sub(a,b))
def point(c,x,y,r,a): return add(c,mul(add(mul(x,math.cos(a)),mul(y,math.sin(a))),r))

def expected(q):
    kind=q['curve_type']
    if kind == 'line':
        a,b=vec(q['start_mm']),vec(q['end_mm'])
        return {'kind':'LINE','points':[add(a,mul(sub(b,a),f)) for f in FRACTIONS], 'length_mm':dist(a,b)}
    if kind not in ('arc','circle'): raise ValueError('unsupported curve type')
    if q.get('definition_mode') == 'three_points':
        a,b,d=vec(q['start_mm']),vec(q['through_mm']),vec(q['end_mm'])
        u,v=sub(b,a),sub(d,a); w=cross(u,v); w2=dot(w,w)
        if w2 < 1e-24: raise ValueError('collinear points')
        c=add(a,mul(add(mul(cross(v,w),dot(u,u)),mul(cross(w,u),dot(v,v))),1/(2*w2)))
        x=unit(sub(a,c)); n=unit(w); y=cross(n,x); r=dist(a,c)
        def theta(p):
            z=sub(p,c); return math.atan2(dot(z,y),dot(z,x)) % (2*math.pi)
        middle,end=theta(b),theta(d)
        if middle > end: n=mul(n,-1); y=mul(y,-1); end=theta(d)
        start=0.; sweep=end
    else:
        c=vec(q['center_mm']); n=vec(q['normal']); x=vec(q['x_axis']); r=float(q['radius_mm'])
        if abs(norm(n)-1)>1e-8 or abs(norm(x)-1)>1e-8 or abs(dot(n,x))>1e-8: raise ValueError('axes must be orthonormal')
        y=cross(n,x)
        start=math.radians(float(q.get('start_angle_deg',0)))
        sweep=2*math.pi if kind=='circle' else math.radians(float(q['end_angle_deg'])-float(q['start_angle_deg']))
    if not math.isfinite(r) or r <= 0 or not 0 < abs(sweep) <= 2*math.pi: raise ValueError('invalid radius/sweep')
    return {'kind':'ARC','center_mm':c,'normal':n,'radius_mm':r,'sweep_rad':sweep,
            'length_mm':r*abs(sweep),'full_circle':kind=='circle',
            'points':[point(c,x,y,r,start+sweep*f) for f in FRACTIONS]}

def check(request, actual):
    e=expected(request); errors={}
    if actual.get('kind') != e['kind']: raise ValueError('native type mismatch')
    length=float(actual['length_mm'])
    if not math.isfinite(length): raise ValueError('nonfinite native length')
    errors['length_mm']=abs(length-e['length_mm'])
    points=[vec(x) for x in actual['points_mm']]
    if len(points)!=len(FRACTIONS): raise ValueError('five native evaluated samples required')
    errors['max_point_mm']=max(dist(a,b) for a,b in zip(e['points'],points))
    if e['kind']=='ARC':
        c=vec(actual['center_mm']); x=vec(actual['axis_x']); y=vec(actual['axis_y']); n=unit(cross(x,y))
        if abs(norm(x)-1)>ANGLE or abs(norm(y)-1)>ANGLE or abs(dot(x,y))>ANGLE: raise ValueError('invalid native arc frame')
        r=float(actual['radius_mm']); start=float(actual['start_angle_rad']); sweep=float(actual['sweep_rad'])
        if not all(math.isfinite(v) for v in (r,start,sweep)) or r <= 0: raise ValueError('invalid native arc measurements')
        errors.update(center_mm=dist(e['center_mm'],c),radius_mm=abs(e['radius_mm']-r),
                      plane_angle_rad=math.acos(max(-1.,min(1.,abs(dot(e['normal'],n))))),
                      sweep_abs_rad=abs(abs(e['sweep_rad'])-abs(sweep)))
        # Native evaluated samples catch wrong phase, traversal and minor/major arc.
    failures=[k for k,v in errors.items() if v > (ANGLE if k.endswith('_rad') else POS)]
    return {'passed':not failures,'errors':errors,'failures':failures,'limits':{'mm':POS,'rad':ANGLE},'expected':e}

def main():
    if len(sys.argv)!=2: raise SystemExit('usage: python f20-geometry-oracle.py input.json')
    with open(sys.argv[1],encoding='utf-8-sig') as f: doc=json.load(f)
    result=check(doc['request'],doc['native']); print(json.dumps(result,indent=2,allow_nan=False))
    return 0 if result['passed'] else 1

if __name__=='__main__': raise SystemExit(main())
