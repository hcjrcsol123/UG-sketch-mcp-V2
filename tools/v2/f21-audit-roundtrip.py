"""Audit independent F21 new-session raw data, using fixed analytic examples.

Tags are compared only within the reopened session; prior session identities are
never reused. Success covers documented samples and native association evidence,
not arbitrary continuous surfaces, reliability, or future NX versions.
"""
import argparse
import importlib.util
import json
import math
from pathlib import Path

HERE=Path(__file__).resolve().parent

def module(name,path):
    spec=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
native=module('f21_audit_native',HERE/'f21-audit-native.py');straight=native.straight
inventory=module('f21_inventory_audit',HERE/'f21-inventory-audit.py')
TOL=1e-6
EXPECTED=[('straight_transport','straight','transport',0,False),('straight_twist','straight','linear_twist',45,False),
          ('chain_transport','line_arc','transport',0,False),('chain_twist','line_arc','linear_twist',45,False),('sheet','straight','transport',0,True)]


def need(ok,message):
    if not ok:raise AssertionError(message)


def near(a,b,tol=TOL):return len(a)==3 and len(b)==3 and math.dist(a,b)<=tol


def unordered_pair(a,b,c,d):return (near(a,c) and near(b,d)) or (near(a,d) and near(b,c))


def sketch_check(sketches,offset,sheet):
    matches=[s for s in sketches if near(s['origin_mm'],[offset,0,0])]
    need(len(matches)==1,'source Sketch is not uniquely matched by origin')
    sketch=matches[0];wanted=[([offset,0,0],[offset+3,0,0]),([offset+3,0,0],[offset,2,0])]
    if not sheet:wanted.append(([offset,2,0],[offset,0,0]))
    lines=sketch['lines'];need(len(lines)==len(wanted),'source Sketch curve count differs')
    for line in lines:
        need(line['reference_status']=='Active','Sketch contains reference or unknown curve')
        hits=[i for i,(a,b) in enumerate(wanted) if unordered_pair(line['start_mm'],line['end_mm'],a,b)]
        need(len(hits)==1,'Sketch geometry mismatch');wanted.pop(hits[0])
    return sketch


def guide_check(definition,curves,offset,path):
    guide=definition['guide_elements'];points=[[offset,0,0],[offset,0,5]]
    if path=='line_arc':points.append([offset+10,0,15])
    need(len(guide)==len(points)-1,'guide element count differs')
    ordered=[]
    for a,b in zip(points,points[1:]):
        hits=[g for g in guide if near(g['start_mm'],a) and near(g['end_mm'],b)]
        need(len(hits)==1,'guide direction or continuity differs');ordered.append(hits[0])
    tags=[g['tag'] for g in ordered];need(len(set(tags))==len(tags),'guide has duplicate member')
    rule_tags=set(t for g in guide for t in g['rule_member_tags'])
    need(rule_tags==set(tags),'guide rules do not select exactly the elements')
    by_tag={c['tag']:c for c in curves};need(len(by_tag)==len(curves),'curve tags are not unique')
    for i,g in enumerate(ordered):
        need(g['tag'] in by_tag,'guide member missing from native curves');c=by_tag[g['tag']]
        if i==0:
            need(c['kind']=='LINE' and unordered_pair(c['start_mm'],c['end_mm'],points[0],points[1]),'path line differs')
        else:
            expected_mid=[offset+10*(1-2**-.5),0,5+10*2**-.5]
            need(c['kind']=='ARC' and near(c['center_mm'],[offset+10,0,5]) and abs(c['radius_mm']-10)<=TOL,'path arc center/radius differs')
            samples=c['points_mm'];need(len(samples)==3 and near(samples[1],expected_mid) and unordered_pair(samples[0],samples[2],points[1],points[2]),'path arc native samples differ')
    return {'guide_count':len(ordered),'directed_member_tags':tags}


def definition_check(row,sketch,curves,entry):
    d=row['definition'];need(d.get('feature_type')=='SKIN','not a native SKIN')
    rules=d['profile_rules'];need(rules and all(r['rule_type']=='CurveFeatureRule' and r['feature_tag']==sketch['feature_tag'] for r in rules),'profile rule not associated to source Sketch feature')
    need(sketch['feature_tag'] in d['parent_feature_tags'],'Sketch feature absent from parents')
    available=list(sketch['lines']);output=d['profile_output']
    need(len(output)==len(available),'profile Section output count differs from source Sketch')
    for edge in output:
        hits=[i for i,s in enumerate(available) if unordered_pair(edge['start_mm'],edge['end_mm'],s['start_mm'],s['end_mm'])]
        need(len(hits)==1,'profile Section output differs from source Sketch geometry');available.pop(hits[0])
    need(d.get('preserve_shape') is True and d.get('preserve_guide_shape') is False,'native preserve shape flags differ')
    need(abs(d.get('g0',-1)-0.001)<=1e-12 and abs(d.get('g1',-1)-0.5)<=1e-12,'native tolerance settings differ from predeclared G0 trial')
    if entry['mode']=='transport':need(d['native_orientation']=='Fixed','native transport differs')
    else:
        need(d['native_orientation']=='ByAngularLaw' and d['native_law']=='Linear','native linear law differs')
        need(abs(d['native_start_deg'])<=1e-9 and abs(d['native_end_deg']+entry['angle'])<=1e-9,'native law signed angles differ')
        need(d['start_unit']=='Degrees' and d['end_unit']=='Degrees','native law units differ')
    return guide_check(d,curves,entry['offset_mm'][0],entry['path'])


def sentinels(raw):
    doc={'profile_mm':[[-200,-200,0],[-197,-200,0],[-197,-196,0],[-200,-196,0]],'closed':True,'translation_mm':[0,0,5],
         'actual':raw['sentinel_geometry'],'tolerance_mm':1e-6,'tolerance_mm2':1e-4,'tolerance_mm3':1e-4}
    solid=straight.check(doc)
    curves=raw['curves'];lines=[c for c in curves if c['kind']=='LINE' and unordered_pair(c['start_mm'],c['end_mm'],[-100,-100,5],[-96,-94,13])]
    arcs=[c for c in curves if c['kind']=='ARC' and near(c['center_mm'],[-150,-140,17]) and abs(c['radius_mm']-7)<=TOL]
    need(len(lines)==1 and len(arcs)==1,'old curve sentinels not unique')
    center=[-150,-140,17]
    def point(degrees):
        a=math.radians(degrees);return [center[0]+7*math.cos(a),center[1]+7*math.sin(a),center[2]]
    p=arcs[0]['points_mm'];need(len(p)==3 and near(p[1],point(70)) and unordered_pair(p[0],p[2],point(15),point(125)),'old arc domain differs')
    return {'solid':solid,'old_line':'PASS','old_arc':'PASS'}


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--capture',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args()
    if args.output.exists():raise FileExistsError('refusing to replace audit')
    capture=json.loads(args.capture.read_text(encoding='utf-8-sig'));results=[];report={'capture':str(args.capture),'passed':False,'cases':results}
    try:
        need(capture.get('capture_completed') is True and capture.get('closed_without_save') is True and capture.get('dirty_before') is False and capture.get('dirty_after') is False,'roundtrip capture incomplete')
        manifest=json.loads(Path(capture['manifest_path']).read_text(encoding='utf-8-sig'));raw=json.loads(Path(capture['raw_path']).read_text(encoding='utf-8-sig'))
        need(raw.get('capture_completed') is True and raw.get('dirty_before') is False and raw.get('dirty_after') is False,'native reader changed state')
        report['inventory_audit']=inventory.audit(raw)
        need(Path(raw['model_path']).resolve()==Path(manifest['model_path']).resolve(),'wrong model path')
        report['sentinels']=sentinels(raw);cases=manifest['cases'];rows=raw['cases'];need(1<=len(cases)==len(rows)<=len(EXPECTED),'missing cases')
        need(len({entry['name'] for entry in cases})==len(cases),'duplicate cases')
        for entry,row in zip(cases,rows):
            choices=[(i,e) for i,e in enumerate(EXPECTED) if e[0]==entry['name']];need(len(choices)==1,'unknown example')
            i,expected=choices[0];name,path,mode,angle,sheet=expected
            need(entry.get('role') in {'case','prerequisite'},'missing case role')
            result={'case':name,'role':entry['role'],'status':'RUNNING'};results.append(result)
            need((entry['name'],entry['path'],entry['mode'],entry['angle'],entry['sheet'])==expected and entry['offset_mm']==[40*i,0,0],'manifest example mismatch')
            need(row['name']==name and row['status']=='CAPTURED_PENDING_ORACLE','raw case missing')
            sketch=sketch_check(raw['sketches'],40*i,sheet);result['association']=definition_check(row,sketch,raw['curves'],entry)
            if sheet:
                result['geometry']=straight.check({'profile_mm':[[160,0,0],[163,0,0],[160,2,0]],'closed':False,'translation_mm':[0,0,5],
                    'actual':row['sheet_geometry'],'tolerance_mm':1e-6,'tolerance_mm2':1e-4,'tolerance_mm3':1e-4})
            else:result['geometry']=native.audit_capture(row['body_capture'],entry)
            result['status']='PASS'
        report.update(passed=True,scope='recorded geometry/association samples, no arbitrary-surface or reliability claim')
    except (AssertionError,ValueError,KeyError,TypeError,IndexError,OverflowError,OSError) as exc:
        report['error']=str(exc)
        if results and results[-1]['status']=='RUNNING':results[-1]['status']='FAIL'
    args.output.parent.mkdir(parents=True,exist_ok=True)
    with args.output.open('x',encoding='utf-8') as f:json.dump(report,f,indent=2,allow_nan=False);f.write('\n')
    print(json.dumps({'output':str(args.output),'passed':report['passed'],'case_count':len(results)}))
    if not report['passed']:raise SystemExit(1)


if __name__=='__main__':main()
