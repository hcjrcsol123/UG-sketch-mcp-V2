"""F21 installed MCP capture; only authorized operator in dedicated NX fixture.

All Sketches/path curves are prebuilt in the dedicated native fixture before
takeover. Raw protocol capture is not a full F21 pass: separate
native re-open and independent geometry/association audits are mandatory.
UNKNOWN or unresolved async response stops all further calls and is never replayed.
"""
import argparse
import datetime
import importlib.util
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import uuid

ROOT=Path(__file__).resolve().parents[3]
def module(name,path):
    spec=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
base=module('f21_lifecycle',Path(__file__).with_name('smoke-f20-mcp.py'))
h=base.h
FIELDS={'operation_id','ownership_id','binding_epoch','revision_id','sketch_id','path_curve_ids','profile_output','orientation_mode','twist_angle_deg'}
CASES=[('straight_transport','straight','transport',0,False),('straight_twist','straight','linear_twist',45,False),
       ('chain_transport','line_arc','transport',0,False),('chain_twist','line_arc','linear_twist',45,False),('sheet','straight','transport',0,True)]


def uid(label):return 'f21-'+label+'-'+uuid.uuid4().hex

def schema(tools):
    rows=[t for t in tools if t.get('name')=='nx_prepare_sweep']
    if len(rows)!=1:raise AssertionError('one sweep tool required')
    s=rows[0]['inputSchema']
    if s.get('additionalProperties') is not False or set(s.get('required',[]))!=FIELDS or set(s.get('properties',{}))!=FIELDS:
        raise AssertionError('sweep schema differs')
    if set(s['properties']['orientation_mode'].get('enum',[]))!={'transport','linear_twist'}:raise AssertionError('orientation schema differs')
    if set(s['properties']['profile_output'].get('enum',[]))!={'solid','sheet'}:raise AssertionError('body schema differs')


def near(actual,expected):
    try:
        return isinstance(actual,(list,tuple)) and len(actual)==len(expected) and all(math.isfinite(float(x)) and abs(float(x)-y)<=1e-6 for x,y in zip(actual,expected))
    except (ValueError,TypeError,OverflowError):return False


def same_pair(a,b,c,d):return (near(a,c) and near(b,d)) or (near(a,d) and near(b,c))


def resolve_fixture(inspection,entry):
    """Re-resolve current JIDs after every model revision, by full unique geometry."""
    x=entry['offset_mm'][0]; wanted=[([x,0,0],[x+3,0,0]),([x+3,0,0],[x,2,0])]
    if not entry['sheet']:wanted.append(([x,2,0],[x,0,0]))
    sketches=[]
    for sketch in inspection.get('sketches',[]):
        if not near(sketch.get('origin'),[x,0,0]):continue
        remaining=list(wanted);geometry=sketch.get('geometry',[])
        if len(geometry)!=len(remaining):continue
        for line in geometry:
            matches=[i for i,(a,b) in enumerate(remaining) if line.get('kind')=='LINE' and line.get('reference_status')=='Active' and same_pair(line.get('start'),line.get('end'),a,b)]
            if len(matches)!=1:break
            remaining.pop(matches[0])
        if not remaining:sketches.append(sketch)
    if len(sketches)!=1 or not isinstance(sketches[0].get('journal_id'),str):raise AssertionError('Sketch cannot be uniquely re-resolved')
    curves=inspection.get('spatial_curves')
    if not isinstance(curves,list):raise AssertionError('missing current spatial-curve inventory')
    lines=[c for c in curves if c.get('native_type')=='Line' and c.get('detail_status')=='SUPPORTED' and
           same_pair(c.get('start'),c.get('end'),[x,0,0],[x,0,5])]
    if len(lines)!=1:raise AssertionError('path line cannot be uniquely re-resolved')
    ids=[lines[0].get('journal_id')]
    if entry['path']=='line_arc':
        arcs=[c for c in curves if c.get('native_type')=='Arc' and c.get('detail_status')=='SUPPORTED' and
              near(c.get('center'),[x+10,0,5]) and abs(float(c.get('radius_mm',-1))-10)<=1e-6 and
              near(c.get('start'),[x,0,5]) and near(c.get('end'),[x+10,0,15]) and near(c.get('normal'),[0,1,0])]
        if len(arcs)!=1:raise AssertionError('path arc cannot be uniquely re-resolved')
        ids.append(arcs[0].get('journal_id'))
    if any(not isinstance(v,str) or not v for v in ids) or len(set(ids))!=len(ids):raise AssertionError('invalid current path IDs')
    return sketches[0]['journal_id'],ids


def resolve_sentinel(inspection):
    matches=[b for b in h._body_records(inspection) if near(b.get('bbox_mm'),[-200,-200,0,-197,-196,5])]
    if len(matches)!=1 or not isinstance(matches[0].get('journal_id'),str):raise AssertionError('sentinel body cannot be uniquely re-resolved')
    return matches[0]['journal_id']


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--part-path',type=Path,required=True);parser.add_argument('--fixture-report',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--expected-server-version',default='0.21.0')
    parser.add_argument('--cases',help='comma-separated F21 cases; omitted means all cases')
    parser.add_argument('--prerequisite-cases',default='',help='comma-separated setup modeling/query cases not counted')
    args=parser.parse_args()
    modeled={x[0] for x in CASES};possible=modeled|{'query_faces_with_swept','save_copy_reopen_return'}
    selected=set(args.cases.split(',')) if args.cases else set(possible)
    prerequisites=set(args.prerequisite_cases.split(',')) if args.prerequisite_cases else set()
    if not selected or not selected<=possible or not prerequisites<=(modeled|{'query_faces_with_swept'}) or selected&prerequisites:
        parser.error('invalid or overlapping selected/prerequisite cases')
    if not (selected|prerequisites)&modeled:
        parser.error('at least one modeling case must be selected or supplied as prerequisite')
    active_cases=[x for x in CASES if x[0] in selected|prerequisites]
    args.output.parent.mkdir(parents=True,exist_ok=True);run=base.Run(args.output);run.report['test']='F21 installed MCP';run.report['protocol_capture_completed']=False
    run.report['selected_cases']=sorted(selected);run.report['prerequisite_cases']=sorted(prerequisites)
    run.report['dependency_mapping']={'query_faces_with_swept':'at least one selected or prerequisite Sweep',
        'save_copy_reopen_return':'at least one selected or prerequisite Sweep',
        'independent_reopen':'completed clean SaveCopy; all source Sketches and path curves are built before the first Sweep'}
    try:
        path=args.part_path.resolve(strict=True);models=(ROOT/'workspace/v2/models').resolve()
        if not path.is_relative_to(models) or not path.name.startswith('F21-mcp-fixture-'):raise ValueError('dedicated F21 fixture required')
        meta=json.loads(args.fixture_report.read_text(encoding='utf-8-sig'))
        if (Path(meta['part_path']).resolve()!=path or meta.get('stage')!='COMPLETE' or meta.get('saved') is not True or
            meta.get('reopened') is not True or meta.get('reopened_dirty') is not False or
            meta.get('reopened_independent_curve_count')!=9 or meta.get('reopened_sketch_count')!=5):
            raise AssertionError('fixture must be saved, reopened and clean')
        run.report['fixture']=meta
        install=Path.home()/'.codex/nx-sketch-mcp';env=os.environ.copy();env.update(PYTHONPATH=str(install/'app/src'),PYTHONUTF8='1',PYTHONDONTWRITEBYTECODE='1')
        process=subprocess.Popen([sys.executable,'-B','-m','nx_sketch_mcp','--config',str(install/'mcp-config.json')],cwd=str(ROOT),env=env,
                                 stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,bufsize=0)
        run.client=base.McpStdioClient(process,60,max_line_bytes=16*1024*1024)
        initialized=run.request('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'f21-installed-acceptance','version':'1'}})
        run.client.notify('notifications/initialized')
        if initialized.get('serverInfo',{}).get('version')!=args.expected_server_version:raise AssertionError('installed version differs')
        tools=run.request('tools/list').get('tools',[])
        if len(tools)!=40 or len({t.get('name') for t in tools})!=40:raise AssertionError('unexpected installed tool count')
        schema(tools);status=run.call('nx_bridge_status')
        if status.get('ownership_state')!='HUMAN_OWNED' or status.get('writes_halted') is not False or str(status.get('in_flight'))!='0':raise AssertionError('bridge not idle and human owned')
        takeover=run.call('nx_takeover',{'expected_pid':status['pid'],'expected_part_path':str(path),'artifact_scope':'saved_independent_copy','authorize':True})
        run.credentials.update(ownership_id=takeover['ownership_id'],binding_epoch=takeover['binding_epoch'])
        caps=run.call('nx_get_capabilities')
        if caps.get('model_sweep_adapter_available') is not True or caps.get('model_sweep_write_enabled') is not True:raise AssertionError('sweep unavailable')
        initial_bodies=h._body_records(run.inspect())
        if len(initial_bodies)!=1 or not isinstance(initial_bodies[0].get('journal_id'),str):raise AssertionError('sentinel body absent')
        row=run.case('fixture_preconditions');row['role']='prerequisite';fixture=[];current=run.inspect()
        for name,path_kind,mode,angle,sheet in active_cases:
            x=next(i for i,item in enumerate(CASES) if item[0]==name)*40
            entry={'name':name,'path':path_kind,'mode':mode,'angle':angle,'sheet':sheet,'offset_mm':[x,0,0],
                   'role':'prerequisite' if name in prerequisites else 'case'}
            sketch_id,path_ids=resolve_fixture(current,entry)
            entry['fixture_inspection_ids_historical']={'sketch_id':sketch_id,'path_curve_ids':path_ids}
            fixture.append(entry)
            row['setup']=fixture;run.persist()
        row.update(state='PASS',scope='fixture setup only; not F21 case');run.persist()
        for index,entry in enumerate(fixture):
            row=run.case(entry['name']);row['role']=entry['role'];before=run.inspect();rev=run.revision()
            current_sketch,current_paths=resolve_fixture(before,entry)
            row['resolved_at_revision']={'revision_id':rev,'sketch_id':current_sketch,'path_curve_ids':current_paths}
            fields={'sketch_id':current_sketch,'path_curve_ids':current_paths,
                    'profile_output':'sheet' if entry['sheet'] else 'solid','orientation_mode':entry['mode'],'twist_angle_deg':entry['angle']}
            _,prepared=run.operation('nx_prepare_sweep',{'operation_id':uid('prepare'),**run.credentials,'revision_id':rev,**fields})
            if base.signature(run.inspect())!=base.signature(before) or run.revision()!=rev:raise AssertionError('prepare changed model')
            plan=prepared.get('plan',{}).get('plan_id')
            if not isinstance(plan,str):raise AssertionError('sweep plan missing')
            op=uid('execute');terminal,e=run.operation('nx_execute_plan',{'operation_id':op,**run.credentials,'plan_id':plan})
            if e.get('mutation_state')!='APPLIED':raise AssertionError('sweep did not apply')
            result=e.get('result') if isinstance(e.get('result'),dict) else e
            definition=result.get('native_definition',{});geometry=result.get('geometry',{});preservation=result.get('model_preservation',{})
            if (result.get('kind')!='create_sweep' or definition.get('profile_output')!=fields['profile_output'] or
                definition.get('orientation_mode')!=entry['mode'] or definition.get('path_curve_ids')!=current_paths or
                definition.get('requested_twist_deg')!=entry['angle'] or definition.get('preserve_shape') is not True or
                abs(float(definition.get('g0_tolerance',-1))-0.001)>1e-12 or
                geometry.get('status')!='SAMPLED_GEOMETRY_AND_TOPOLOGY_MATCH' or preservation.get('status')!='VERIFIED'):
                raise AssertionError('native sweep response lacks requested association/geometry/preservation')
            if entry['mode']=='linear_twist' and definition.get('native_end_deg')!=-entry['angle']:
                raise AssertionError('native angle is not the inverse of the public right-hand angle')
            queried=run.call('nx_get_operation',{'operation_id':op})
            if queried.get('state')!='SUCCEEDED' or h._operation_result(queried)!=h._operation_result(terminal):raise AssertionError('terminal operation lookup differs')
            if index==0:
                stable=run.inspect();stable_revision=run.revision()
                _,repeated=run.operation('nx_execute_plan',{'operation_id':op,**run.credentials,'plan_id':plan})
                if repeated!=e or base.signature(run.inspect())!=base.signature(stable) or run.revision()!=stable_revision:
                    raise AssertionError('known successful same-ID execute changed state')
                row['same_id_success_replay_unchanged']=True
            _,verified=run.operation('nx_verify_part',{'operation_id':uid('verify'),**run.credentials,'execute_operation_id':op});h._assert_verified(verified,entry['name'])
            row.update(state='PREREQUISITE_CAPTURED_PENDING_REOPEN' if entry['role']=='prerequisite' else 'CAPTURED_PENDING_REOPEN',
                       request=fields,fixture=entry,execution=result,fresh=verified,terminal_query_matches=True);run.persist()
            if index==0 and 'query_faces_with_swept' in selected|prerequisites:
                row=run.case('query_faces_with_swept');before=run.inspect();revision=run.revision();sentinel_body_id=resolve_sentinel(before)
                row['role']='prerequisite' if 'query_faces_with_swept' in prerequisites else 'case'
                faces=run.call('nx_query_faces',{**run.credentials,'revision_id':revision,'selector':'all_planar','body_id':sentinel_body_id})
                if faces.get('selection_status')!='all' or faces.get('candidate_count')!=6 or base.signature(run.inspect())!=base.signature(before) or run.revision()!=revision:
                    raise AssertionError('face query blocked, incomplete or modified model')
                row.update(state='PASS',result=faces);run.persist()
        row=run.case('save_copy_reopen_return');row['role']='case' if 'save_copy_reopen_return' in selected else 'prerequisite'
        copy=path.parent/('F21-mcp-'+uuid.uuid4().hex+'-roundtrip.prt');before=run.inspect()
        _,saved=run.operation('nx_save_copy',{'operation_id':uid('save'),**run.credentials,'target_path':str(copy),'reopen':True})
        run.credentials.update(ownership_id=saved['new_ownership_id'],binding_epoch=saved['new_binding_epoch'])
        checks=saved.get('checks',{})
        if saved.get('saved') is not True or saved.get('reopened') is not True or set(checks)!={'associations','expression','geometry','history'} or any(c.get('status')!='VERIFIED' for c in checks.values()):raise AssertionError('SaveCopy checks incomplete')
        after=run.inspect()
        if h._inspect_path(after).resolve()!=copy.resolve():raise AssertionError('wrong reopened copy')
        manifest={'model_path':str(copy),'installed_report':str(args.output.resolve()),'cases':fixture,'sentinels':meta['sentinels'],'solid_bbox_mm':meta['solid_bbox_mm']}
        manifest_path=args.output.parent/(args.output.stem+'-roundtrip-manifest.json')
        with manifest_path.open('x',encoding='utf-8') as f:json.dump(manifest,f,indent=2,allow_nan=False)
        row.update(saved_path=str(copy),roundtrip_manifest=str(manifest_path),inspect_before=before,inspect_after=after)
        returned=run.call('nx_return',run.credentials)
        if returned.get('ownership_state')!='HUMAN_OWNED' or returned.get('safe_for_human') is not True:raise AssertionError('return not human safe')
        run.credentials.clear();status=run.call('nx_bridge_status')
        if status.get('ownership_state')!='HUMAN_OWNED' or status.get('writes_halted') is not False or str(status.get('in_flight'))!='0' or status.get('active_operations')!=0:
            raise AssertionError('final bridge not idle')
        row.update(state='PASS',final_status=status);run.report['protocol_capture_completed']=True
        # The independent new-session oracle decides final case status.
        run.report['passed']=False;return 0
    except Exception as exc:
        run.report['failure']={'type':type(exc).__name__,'message':str(exc) if isinstance(exc,(AssertionError,ValueError,FileNotFoundError)) else 'inspect recorded response',
                               'case_id':run.report['current_case'],'action':'stop; preserve; no auto writes, replay, return or thaw'}
        if run.report['cases'] and run.report['cases'][-1]['state']=='RUNNING':run.report['cases'][-1]['state']='UNKNOWN_STOPPED' if run.frozen else 'FAIL'
        return 1
    finally:
        run.report['completed_utc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
        done={r['case_id'] for r in run.report['cases']};required=selected|prerequisites|{'fixture_preconditions','save_copy_reopen_return'}
        run.report['not_executed']=sorted(required-done)
        if run.client:
            try:run.client.close()
            except Exception as exc:run.report['client_close_error_type']=type(exc).__name__
        run.persist()


if __name__=='__main__':raise SystemExit(main())
