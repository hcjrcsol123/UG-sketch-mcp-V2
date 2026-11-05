"""Audit saved F21 native capture; never reads expected data from product output.

Counts here are unique selected cases, not assertions. A sample pass is limited to
recorded witnesses, complete readback structure and native rule evidence. It is
not a mathematical proof over arbitrary continuous surfaces or NX versions.
"""
import argparse
import importlib.util
import json
import math
from pathlib import Path

HERE=Path(__file__).resolve().parent

def module(name,path):
    spec=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m

twist=module('f21_twist',HERE/'f21-twist-oracle.py')
straight=module('f21_straight',HERE/'f21-geometry-oracle.py')


def need(x,msg):
    if not x: raise AssertionError(msg)


def straight_edge(edge,a,b):
    ends=edge.get('vertices_mm',[])
    if len(ends)!=2 or not ((math.dist(ends[0],a)<=1e-6 and math.dist(ends[1],b)<=1e-6) or
                            (math.dist(ends[1],a)<=1e-6 and math.dist(ends[0],b)<=1e-6)):return False
    limits=edge.get('limit_points_mm',[])
    if len(limits)!=2 or not ((math.dist(limits[0],a)<=1e-6 and math.dist(limits[1],b)<=1e-6) or
                              (math.dist(limits[1],a)<=1e-6 and math.dist(limits[0],b)<=1e-6)):return False
    if edge.get('evaluator_type')=='LINE':return True
    if edge.get('evaluator_type')!='SPLINE':return False
    # UF_EVAL_initialize_2 does not approximate procedural edges. Its spline
    # control points are homogeneous (wx, wy, wz, w), per uf_eval.h. Positive
    # weights and a continuous knot vector bound the whole curve in the convex
    # hull of its control points; endpoints and collinear bounded poles prove
    # the recorded edge covers the specified straight segment within 1e-6 mm.
    d=edge.get('native_definition',{});n=d.get('control_point_count');p=d.get('control_points');k=d.get('knots')
    if type(n) is not int or n<2 or not isinstance(p,list) or len(p)!=4*n or not isinstance(k,list) or d.get('knot_count')!=len(k):return False
    if any(isinstance(z,bool) or not isinstance(z,(int,float)) or not math.isfinite(z) for z in p+k):return False
    order=len(k)-n
    if order<2 or order>n or any(k[i]>k[i+1] for i in range(len(k)-1)):return False
    if any(k.count(v)>=order for v in set(k) if k[0]<v<k[-1]):return False
    vector=[b[i]-a[i] for i in range(3)];length2=sum(v*v for v in vector)
    if length2<=1e-12:return False
    for j in range(n):
        w=p[4*j+3]
        if w<=0:return False
        point=[p[4*j+i]/w for i in range(3)];t=sum((point[i]-a[i])*vector[i] for i in range(3))/length2
        nearest=[a[i]+t*vector[i] for i in range(3)]
        if t < -1e-6/math.sqrt(length2) or t > 1+1e-6/math.sqrt(length2) or math.dist(point,nearest)>1e-6:return False
    return True


def cap_matches(face,edges,points):
    if face.get('is_planar') is not True or len(face.get('edge_tags',[]))!=3: return False
    wanted=[(points[0],points[1]),(points[1],points[2]),(points[2],points[0])]
    for edge_id in face['edge_tags']:
        edge=edges[edge_id]; ends=edge.get('vertices_mm',[])
        if len(ends)!=2:return False
        hits=[i for i,(a,b) in enumerate(wanted) if
              (math.dist(ends[0],a)<=1e-6 and math.dist(ends[1],b)<=1e-6) or
              (math.dist(ends[1],a)<=1e-6 and math.dist(ends[0],b)<=1e-6)]
        if len(hits)!=1:return False
        if not straight_edge(edge,*wanted[hits[0]]):return False
        wanted.pop(hits[0])
    origin=face['origin_mm'];normal=face['direction']; norm=math.hypot(*normal)
    return norm>1e-12 and all(abs(sum((p[i]-origin[i])*normal[i] for i in range(3)))/norm<=1e-6 for p in points) and not wanted


def audit_capture(capture,expected):
    data=capture['oracle_input']; baseline=twist.check(data); offset=capture['offset_mm']
    need(data['path']==expected['path'] and data['mode']==expected['mode'] and data['angle_deg']==expected['angle'] and
         offset==expected['offset_mm'],'capture inputs differ from predeclared case')
    native=capture['native_reads']; structure=capture['body_structure']
    need(structure.get('solid') is True and structure.get('sheet') is False,'expected solid structure')
    face_list=structure['faces'];edge_list=structure['edges']
    faces={f['face_tag']:f for f in face_list};edges={e['edge_tag']:e for e in edge_list}
    need(len(faces)==len(face_list) and len(edges)==len(edge_list) and faces and edges,'missing or duplicated topology')
    need(len(faces)==5 and len(edges)==9,'triangular solid must have exactly five faces and nine edges')
    for f in face_list:
        need(f.get('edge_tags') and len(set(f['edge_tags']))==len(f['edge_tags']),'incomplete face boundary')
        if f.get('face_type')=='Parametric':need(isinstance(f.get('native_bsurface'),dict) and bool(f['native_bsurface']),'missing native B-surface definition')
        for tag in f['edge_tags']: need(tag in edges and f['face_tag'] in edges[tag]['face_tags'],'face-edge reciprocity')
    for e in edge_list:
        need(e.get('native_definition') is not None and len(e.get('vertices_mm',[]))==2,'missing native edge definition')
        need(len(e['face_tags'])==2 and len(set(e['face_tags']))==2,'solid edge adjacency mismatch')
        for tag in e['face_tags']: need(tag in faces and e['edge_tag'] in faces[tag]['edge_tags'],'edge-face reciprocity')
    need(len(native)==len(data['samples']),'incomplete raw station reads')
    sampled_faces=set()
    for si,(station,raw) in enumerate(zip(data['samples'],native)):
        need(abs(station['s_mm']-raw['s_mm'])<=1e-9,'raw station mismatch')
        reads=raw['native_reads'];need(len(reads)==len(station['points_mm']),'missing native raw sample')
        for pi,(local,record) in enumerate(zip(station['points_mm'],reads)):
            selected=record['selected'];actual=selected['point_mm']
            need(math.dist([actual[k]-offset[k] for k in range(3)],local)<=1e-9,'oracle point is not native evaluated point')
            need(selected['evaluation_projection_error_mm']<=1e-6,'native projection/evaluation mismatch')
            if 'face_tag' in selected:
                need(selected['face_tag'] in faces and selected['face_containment']==1 and len(selected['uv'])==2,'face point lacks valid trimmed-face evidence')
                if 0<si<len(native)-1 and pi>=3:sampled_faces.add(selected['face_tag'])
            else:
                need(selected.get('edge_tag') in edges and 'parameter' in selected,'edge point lacks evaluator evidence')
    start,_,_,_=twist.expected(data['path'],0,data['angle_deg'])
    end_s=twist.stations(data['path'])[-1];end,_,_,_=twist.expected(data['path'],end_s,data['angle_deg'])
    endpoints=[[[p[i]+offset[i] for i in range(3)] for p in group[:3]] for group in (start,end)]
    caps=[f for f in face_list if any(cap_matches(f,edges,group) for group in endpoints)]
    need(len(caps)==2,'missing or nonunique full planar end caps')
    for face in face_list:
        need(len(face['edge_tags'])==(3 if face in caps else 4),'unexpected side/cap boundary topology')
    expected_pairs=[(endpoints[0][i],endpoints[0][(i+1)%3]) for i in range(3)]+\
                   [(endpoints[1][i],endpoints[1][(i+1)%3]) for i in range(3)]+\
                   [(endpoints[0][i],endpoints[1][i]) for i in range(3)]
    assigned={}
    for edge in edge_list:
        vertices=edge['vertices_mm']
        hits=[i for i,(a,b) in enumerate(expected_pairs) if
              (math.dist(vertices[0],a)<=1e-6 and math.dist(vertices[1],b)<=1e-6) or
              (math.dist(vertices[1],a)<=1e-6 and math.dist(vertices[0],b)<=1e-6)]
        need(len(hits)==1 and hits[0] not in assigned,'edge endpoint graph differs from expected triangle sweep');assigned[hits[0]]=edge['edge_tag']
    need(len(assigned)==9,'missing edge in triangle sweep graph')
    expected_faces=[{assigned[i] for i in (0,1,2)},{assigned[i] for i in (3,4,5)}]
    expected_faces += [{assigned[i],assigned[i+3],assigned[6+i],assigned[6+(i+1)%3]} for i in range(3)]
    actual_faces=[set(f['edge_tags']) for f in face_list]
    for expected_face in expected_faces:
        need(sum(face==expected_face for face in actual_faces)==1,'face adjacency differs from expected triangle sweep')
    uncovered=set(faces)-sampled_faces-{f['face_tag'] for f in caps}
    need(not uncovered,'faces without independent interior samples: '+str(sorted(uncovered)))
    return {'status':'PASS','sample_result':baseline,'sampled_face_count':len(sampled_faces),'face_count':len(faces),
            'edge_count':len(edges),'cap_count':len(caps),
            'scope':'recorded sample set, native structure and cap boundaries; not mathematical full-surface proof'}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--input',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--expected-g0',type=float,choices=[0.01,0.001],default=0.01,help='predeclared native G0 configuration, not a geometry tolerance')
    args=parser.parse_args()
    if args.output.exists():raise FileExistsError('refusing to replace audit')
    source=json.loads(args.input.read_text(encoding='utf-8-sig'));results=[];seen=set()
    for row in source['cases']:
        name=row['case'];need(name not in seen,'duplicate case');seen.add(name)
        prerequisite=row.get('role')=='prerequisite'
        if row['status'] in ('CAPTURED_PENDING_ORACLE','PREREQUISITE_CAPTURED_PENDING_ORACLE'):
            try:
                definition=row['execution_result']['native_definition']
                need(abs(float(definition['g0_tolerance'])-args.expected_g0)<=1e-12 and
                     definition['preserve_shape'] is True and definition['preserve_guide_shape'] is False,
                     'native G0 or preserve settings differ from predeclared configuration')
                if name=='sheet':
                    offset=160
                    doc={'profile_mm':[[offset,0,0],[offset+3,0,0],[offset,2,0]],'closed':False,'translation_mm':[0,0,5],
                         'actual':row['independent_sheet'],'tolerance_mm':1e-6,'tolerance_mm2':1e-4,'tolerance_mm3':1e-4}
                    outcome={'status':'PASS','geometry':straight.check(doc)}
                else:
                    examples={'straight_transport':('straight','transport',0,0),'straight_twist':('straight','linear_twist',45,40),
                              'chain_transport':('line_arc','transport',0,80),'chain_twist':('line_arc','linear_twist',45,120)}
                    path,mode,angle,x=examples[name]
                    outcome=audit_capture(row['independent_capture'],{'path':path,'mode':mode,'angle':angle,'offset_mm':[x,0,0]})
            except (AssertionError,ValueError,KeyError,TypeError,IndexError,OverflowError) as ex:outcome={'status':'FAIL','error':str(ex)}
        else:outcome={'status':row['status'],'scope':'raw control result; no extra oracle'}
        results.append({'case':name,'role':'prerequisite' if prerequisite else 'case',**outcome})
    selected=source.get('selected_cases',[])
    prerequisites=source.get('prerequisite_cases',[])
    audit={'source':str(args.input),'scope':'unique native cases; no installation or reopen claim','expected_native_g0':args.expected_g0,
           'cases':results,'passed':source.get('capture_completed') is True and isinstance(selected,list) and
           isinstance(prerequisites,list) and len(selected)==len(set(selected)) and len(prerequisites)==len(set(prerequisites)) and
           not (set(selected)&set(prerequisites)) and len(selected)+len(prerequisites)==len(results) and
           set(selected)|set(prerequisites)==seen and all(x['status']=='PASS' for x in results)}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    with args.output.open('x',encoding='utf-8') as f:json.dump(audit,f,indent=2,ensure_ascii=False);f.write('\n')
    print(json.dumps({'output':str(args.output),'passed':audit['passed'],'case_count':len(results)}))
    if not audit['passed']:raise SystemExit(1)


if __name__=='__main__': main()
