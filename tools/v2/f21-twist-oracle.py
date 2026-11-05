"""Analytic F21 sample oracle for the documented triangle and two fixed paths.

This checks sampled section points and signed orientation; it cannot establish
continuity, all face interiors, native Section/Guide links, or persistence.
Caller must capture those separately and must provide actual section points
from independent NX reads. Units mm/radians, fixed thresholds in this module.
"""
import json
import math
import sys

POSITION_ENDPOINT = 1e-6
POSITION_INTERIOR = 0.02
ANGLE_ENDPOINT = 1e-6
ANGLE_INTERIOR = 0.01
PROFILE = ((0.0,0.0),(3.0,0.0),(0.0,2.0),
           (0.75,0.0),(1.5,0.0),(2.25,0.0),
           (2.25,0.5),(1.5,1.0),(0.75,1.5),
           (0.0,1.5),(0.0,1.0),(0.0,0.5))


def _finite(x):
    return not isinstance(x,bool) and isinstance(x,(int,float)) and math.isfinite(x)


def _dot(a,b): return sum(x*y for x,y in zip(a,b))
def _sub(a,b): return tuple(x-y for x,y in zip(a,b))
def _sum(a,b): return tuple(x+y for x,y in zip(a,b))
def _scale(a,k): return tuple(k*x for x in a)


def stations(path):
    if path=='straight': return [0.0,1.25,2.5,3.75,5.0]
    if path=='line_arc': return [0.0,2.5,5.0,5+10*math.pi/8,5+10*math.pi/4,5+30*math.pi/8,5+5*math.pi]
    raise ValueError('unsupported path')


def frame(path,s):
    length = 5.0 if path=='straight' else 5+5*math.pi
    if path=='straight' or s<=5:
        return ((0.0,0.0,s),(1.0,0.0,0.0),(0.0,1.0,0.0),length)
    a=(s-5)/10
    return ((10*(1-math.cos(a)),0.0,5+10*math.sin(a)),(math.cos(a),0.0,-math.sin(a)),(0.0,1.0,0.0),length)


def expected(path,s,angle_deg):
    origin,x,y,length=frame(path,s)
    phi=math.radians(angle_deg)*s/length
    c=math.cos(phi); sn=math.sin(phi)
    points=[]
    for u,v in PROFILE:
        points.append(_sum(origin,_sum(_scale(x,u*c-v*sn),_scale(y,u*sn+v*c))))
    return points,phi,x,y


def check(data):
    if not isinstance(data,dict) or set(data)!={'path','mode','angle_deg','samples'}: raise ValueError('wrong fields')
    path=data['path']; mode=data['mode']; angle=data['angle_deg']
    if mode not in ('transport','linear_twist') or not _finite(angle) or abs(angle)>90 or (mode=='transport' and angle!=0): raise ValueError('unsupported mode or angle')
    positions=stations(path); samples=data['samples']
    if not isinstance(samples,list) or len(samples)!=len(positions): raise AssertionError('incomplete station set')
    residuals=[]
    for index,(s,row) in enumerate(zip(positions,samples)):
        if not isinstance(row,dict) or set(row)!={'s_mm','points_mm'} or not _finite(row['s_mm']) or abs(row['s_mm']-s)>1e-9:
            raise AssertionError('station mismatch')
        actual=row['points_mm']
        if not isinstance(actual,list) or len(actual)!=len(PROFILE): raise AssertionError('station missing profile vertices or side-face samples')
        for p in actual:
            if not isinstance(p,list) or len(p)!=3 or not all(_finite(c) for c in p): raise ValueError('invalid measured point')
        target,phi,x,y=expected(path,s,angle)
        errors=[math.dist(a,b) for a,b in zip(actual,target)]
        endpoint=index in (0,len(positions)-1)
        if max(errors)>(POSITION_ENDPOINT if endpoint else POSITION_INTERIOR): raise AssertionError('sample point residual exceeds limit at station '+str(index))
        direction=_sub(actual[1],actual[0]); xx=_dot(direction,x); yy=_dot(direction,y)
        if math.hypot(xx,yy)<1e-9: raise AssertionError('degenerate measured direction')
        measured=math.atan2(yy,xx)
        diff=math.atan2(math.sin(measured-phi),math.cos(measured-phi))
        if abs(diff)>(ANGLE_ENDPOINT if endpoint else ANGLE_INTERIOR): raise AssertionError('signed direction residual exceeds limit at station '+str(index))
        residuals.append({'s_mm':s,'max_position_error_mm':max(errors),'signed_direction_error_rad':diff})
    return {'passed':True,'scope':'documented sampled sections only; not full surface proof or association', 'residuals':residuals}


def main():
    if len(sys.argv)!=2: raise SystemExit('usage: f21-twist-oracle.py INPUT.json')
    try:
        with open(sys.argv[1],encoding='utf-8-sig') as stream: result=check(json.load(stream))
        print(json.dumps(result)); return 0
    except (AssertionError,ValueError,KeyError,TypeError) as exc:
        print(json.dumps({'passed':False,'error':str(exc)})); return 1


if __name__=='__main__': sys.exit(main())
