"""Read-only independent NX session capture of an F21 installed SaveCopy.

The caller must be the explicitly authorized sole NX operator. This captures raw
native data and does not by itself grant acceptance. Any uncertain reader or
restoration stops immediately; no write, replay, automatic thaw, or forced exit.
"""
import datetime
import json
import os
from pathlib import Path
import traceback
import uuid
import NXOpen

ROOT=Path(r'C:\0sf\1PROJECT\AI\design\nx-sketch-mcp')
EXPECTED=[('straight_transport','straight','transport',0,False),('straight_twist','straight','linear_twist',45,False),
          ('chain_transport','line_arc','transport',0,False),('chain_twist','line_arc','linear_twist',45,False),('sheet','straight','transport',0,True)]


def main():
    evidence=(ROOT/'workspace/v2/evidence').resolve(strict=True);models=(ROOT/'workspace/v2/models').resolve(strict=True)
    manifest_path=Path(os.environ['F21_ROUNDTRIP_MANIFEST']).resolve(strict=True)
    if not manifest_path.is_relative_to(evidence):raise ValueError('manifest must be in evidence')
    manifest=json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    source=Path(manifest['installed_report']).resolve(strict=True)
    if not source.is_relative_to(evidence):raise ValueError('installed report must be in evidence')
    installed=json.loads(source.read_text(encoding='utf-8-sig'))
    if installed.get('protocol_capture_completed') is not True or installed.get('unknown_events') or installed.get('not_executed'):
        raise ValueError('installed protocol did not complete cleanly')
    model=Path(manifest['model_path']).resolve(strict=True)
    if not model.is_relative_to(models) or not model.name.startswith('F21-mcp-') or not model.name.endswith('-roundtrip.prt'):
        raise ValueError('requires dedicated F21 installed SaveCopy')
    saved=[r for r in installed['cases'] if r.get('case_id')=='save_copy_reopen_return']
    if len(saved)!=1 or saved[0].get('state')!='PASS' or Path(saved[0]['saved_path']).resolve()!=model:
        raise ValueError('SaveCopy lacks installed report provenance')
    cases=manifest['cases']
    if not isinstance(cases,list) or not 1<=len(cases)<=len(EXPECTED) or len({c.get('name') for c in cases})!=len(cases):raise ValueError('wrong or duplicate expected cases')
    for case in cases:
        choices=[(i,e) for i,e in enumerate(EXPECTED) if e[0]==case.get('name')]
        if len(choices)!=1:raise ValueError('unknown manifest example')
        i,expected=choices[0]
        if (case.get('name'),case.get('path'),case.get('mode'),case.get('angle'),case.get('sheet'))!=expected or case.get('offset_mm')!=[i*40,0,0] or case.get('role') not in {'case','prerequisite'}:
            raise ValueError('manifest differs from fixed predeclared examples')
        corresponding=[r for r in installed['cases'] if r.get('case_id')==case['name']]
        wanted='PREREQUISITE_CAPTURED_PENDING_REOPEN' if case['role']=='prerequisite' else 'CAPTURED_PENDING_REOPEN'
        if len(corresponding)!=1 or corresponding[0].get('state')!=wanted or corresponding[0].get('role')!=case['role']:
            raise ValueError('manifest case does not match installed capture')
    dll=Path(os.environ['F21_ROUNDTRIP_READER_DLL']).resolve(strict=True)
    if not dll.is_relative_to(evidence) or dll.name!='F21RoundtripReader.dll':raise ValueError('unique offline-built reader required')
    run=uuid.uuid4().hex;output=evidence/('f21-roundtrip-'+run+'.json');raw=evidence/('f21-roundtrip-'+run+'-raw.json')
    report={'run_id':run,'started_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'scope':'independent new-session native capture; external oracle pending','model_path':str(model),
            'manifest_path':str(manifest_path),'reader_dll':str(dll),'installed_report':str(source),'passed':False}
    with output.open('x',encoding='utf-8') as f:json.dump(report,f)
    def persist():output.write_text(json.dumps(report,indent=2,allow_nan=False)+'\n',encoding='utf-8')
    session=NXOpen.Session.GetSession();part=None
    try:
        if session.Parts.Work is not None or session.IsInTaskEnvironment or session.ActiveSketch is not None:raise RuntimeError('requires empty independent session')
        part,load=session.Parts.OpenDisplay(str(model))
        try:unloaded=int(load.NumberUnloadedParts)
        finally:load.Dispose()
        report.update(unloaded_parts=unloaded,dirty_before=bool(part.IsModified),units=str(part.PartUnits));persist()
        if unloaded or part.IsModified or part.PartUnits!=NXOpen.BasePart.Units.Millimeters:raise AssertionError('loaded part is not clean millimeters')
        os.environ['F21_REOPEN_RAW']=str(raw)
        try:code=session.Execute(str(dll),'F21RoundtripReader','Run',[])
        except Exception:
            report['unknown_or_stopped']=True;raise
        report.update(reader_return_code=int(code),raw_path=str(raw));persist()
        if code!=0 or not raw.is_file():
            report['unknown_or_stopped']=True;raise RuntimeError('collector failed or could not prove restoration')
        data=json.loads(raw.read_text(encoding='utf-8-sig'))
        if data.get('capture_completed') is not True or data.get('unknown_or_stopped') or data.get('dirty_after') is not False:
            report['unknown_or_stopped']=True;raise RuntimeError('collector did not complete cleanly')
        report['dirty_after']=bool(part.IsModified);persist()
        if report['dirty_after']:
            report['unknown_or_stopped']=True;raise RuntimeError('collector left part modified')
        part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue,NXOpen.BasePart.CloseModified.CloseModified,None);part=None
        report.update(capture_completed=True,closed_without_save=True,oracle_status='PENDING_EXTERNAL');persist()
        return 0
    except Exception:
        report['failure']=traceback.format_exc();report['left_open_for_inspection']=part is not None;persist();return 1
    finally:
        report['ended_utc']=datetime.datetime.now(datetime.timezone.utc).isoformat();persist()


if __name__=='__main__':main()
