"""Read-only NX reopen of the installed F20 SaveCopy; no model edits or saves.

F20_ROUNDTRIP_MANIFEST must identify model_path and cases [{name, request}].
The caller must provide manifest provenance from the successful installed run.
"""
import datetime
import importlib.util
import json
import math
import os
from pathlib import Path
import traceback
import uuid
import NXOpen
import NXOpen.UF

ROOT = Path(r'C:\0sf\1PROJECT\AI\design\nx-sketch-mcp')
spec = importlib.util.spec_from_file_location('f20_oracle', ROOT / 'tools/v2/f20-geometry-oracle.py')
oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(oracle)

def xyz(p): return [float(p.X), float(p.Y), float(p.Z)]
def tag(obj): return str(obj.Tag) if obj else None
def inventory(part,session):
    return {
        'features': sorted((tag(f),f.JournalIdentifier,f.FeatureType,sorted(tag(p) for p in f.GetParents()),sorted(tag(c) for c in f.GetChildren()),tag(f.ContainerFeature)) for f in part.Features),
        'bodies': sorted((tag(b),b.JournalIdentifier,bool(b.IsSolidBody)) for b in part.Bodies),
        'expressions': sorted((tag(e),e.JournalIdentifier,e.Name,e.RightHandSide,e.Value,e.Units.JournalIdentifier if e.Units else None) for e in part.Expressions),
        'points': sorted((tag(p),p.JournalIdentifier,xyz(p.Coordinates)) for p in part.Points),
        'directions': sorted((tag(d),d.JournalIdentifier,xyz(d.Origin),xyz(d.Vector)) for d in part.Directions),
        'axes': sorted((tag(a),a.JournalIdentifier,tag(a.Point),tag(a.Direction)) for a in part.Axes),
        'planes': sorted((tag(p),p.JournalIdentifier,xyz(p.Origin),xyz(p.Normal)) for p in part.Planes),
        'curves': sorted((tag(c),c.JournalIdentifier,type(c).__name__) for c in part.Curves),
        'sketches': sorted((tag(s),s.JournalIdentifier,sorted(tag(g) for g in s.GetAllGeometry())) for s in part.Sketches),
        'active_sketch':tag(session.ActiveSketch),'dirty':bool(part.IsModified)}

def collect(session,output,phase,report):
    dll=Path(os.environ['F20_ROUNDTRIP_READER_DLL']).resolve(strict=True)
    evidence=(ROOT/'workspace/v2/evidence').resolve(strict=True)
    if not dll.is_relative_to(evidence) or dll.name!='F20RoundtripReader.dll':
        raise ValueError('reader must be the unique offline-built F20RoundtripReader.dll')
    raw=output.with_name(output.stem+'-'+phase+'-raw.json')
    if raw.exists(): raise FileExistsError('raw NX reader output already exists')
    os.environ['F20_NX_RAW_OUTPUT']=str(raw)
    report['reader_dll']=str(dll)
    try:
        code=session.Execute(str(dll),'F20RoundtripReader','Run',[])
    except Exception:
        report['reader_cleanup_unknown']=True
        raise
    if code!=0 or not raw.is_file():
        report['reader_cleanup_unknown']=True
        raise RuntimeError('native collector did not complete and prove evaluator cleanup')
    report[phase+'_raw_path']=str(raw)
    data=json.loads(raw.read_text(encoding='utf-8-sig'))
    if not isinstance(data,list): raise ValueError('invalid native reader result')
    return data

def main():
    run=uuid.uuid4().hex
    output=ROOT / 'workspace/v2/evidence' / ('f20-roundtrip-'+run+'.json')
    report={'run_id':run,'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'scope':'reopened native geometry, independent oracle; installed provenance separate','cases':[], 'passed':False}
    output.parent.mkdir(parents=True,exist_ok=True)
    with output.open('x',encoding='utf-8') as f: json.dump(report,f)
    def persist():
        with output.open('w',encoding='utf-8') as f: json.dump(report,f,indent=2,allow_nan=False)
    part=None
    try:
        manifest_path=Path(os.environ['F20_ROUNDTRIP_MANIFEST']).resolve(strict=True)
        manifest=json.loads(manifest_path.read_text(encoding='utf-8-sig'))
        model=Path(manifest['model_path']).resolve(strict=True)
        if not model.is_relative_to((ROOT/'workspace/v2/models').resolve(strict=True)) or not model.name.startswith('F20-mcp-') or not model.name.endswith('-roundtrip.prt'):
            raise ValueError('model must be an F20 installed SaveCopy in project models')
        cases=manifest['cases']
        if not isinstance(cases,list) or len(cases)!=6 or len({x['name'] for x in cases})!=6:
            raise ValueError('manifest must contain four installed cases plus two unique sentinel cases')
        session=NXOpen.Session.GetSession()
        if session.Parts.Work is not None or session.IsInTaskEnvironment or session.ActiveSketch is not None:
            raise RuntimeError('requires a separate empty NX session')
        part,load=session.Parts.OpenDisplay(str(model))
        try: unloaded=int(load.NumberUnloadedParts)
        finally: load.Dispose()
        report.update(model_path=str(model),manifest_path=str(manifest_path),unloaded_parts=unloaded,dirty_before=bool(part.IsModified),part_units=str(part.PartUnits))
        if unloaded or part.IsModified or part.PartUnits != NXOpen.BasePart.Units.Millimeters:
            raise AssertionError('reopened part must be fully loaded, clean and in millimeters')
        report['inventory_before']=inventory(part,session)
        if report['inventory_before']['sketches'] or len(list(part.Curves))!=6:
            raise AssertionError('fixture must contain six independent curves and no sketch membership')
        uf=NXOpen.UF.UFSession.GetUFSession(); bodies=list(part.Bodies)
        if len(bodies)!=1 or not bodies[0].IsSolidBody:
            raise AssertionError('fixture sentinel solid is missing or duplicated')
        report['sentinel_solid_bounds_mm']=list(uf.ModlGeneral.AskBoundingBox(bodies[0].Tag))
        if len(report['sentinel_solid_bounds_mm'])!=6 or any(not math.isfinite(a) or abs(a-b)>1e-6 for a,b in zip(report['sentinel_solid_bounds_mm'],[-200,-200,0,-197,-196,5])):
            raise AssertionError('fixture sentinel solid bounds changed')
        readings=collect(session,output,'before',report)
        if len(readings)!=6: raise AssertionError('fixture contains unsupported or missing curve type')
        report['native_curve_readings']=readings
        used=set()
        for case in cases:
            row={'case':case['name'],'status':'RUNNING','request':case['request']}; report['cases'].append(row); persist()
            candidates=[]
            for i,native in enumerate(readings):
                try: result=oracle.check(case['request'],native)
                except (ValueError,KeyError,TypeError): continue
                if result['passed']: candidates.append((i,result))
            row['matching_indices']=[i for i,_ in candidates]
            if len(candidates)!=1 or candidates[0][0] in used:
                raise AssertionError('each expected curve must match exactly one distinct native curve')
            index,result=candidates[0]; used.add(index)
            row.update(native=readings[index],oracle=result,status='PASS'); persist()
        if len(used)!=len(readings): raise AssertionError('unaccounted native curve')
        after=collect(session,output,'after',report)
        report.update(dirty_after=bool(part.IsModified),curve_inventory_unchanged=readings==after,inventory_after=inventory(part,session))
        if part.IsModified or readings!=after or report['inventory_before']!=report['inventory_after']:
            raise RuntimeError('read-only reader changed model')
        report['passed']=True
    except Exception:
        if report['cases'] and report['cases'][-1]['status']=='RUNNING': report['cases'][-1]['status']='FAIL'
        report['error']=traceback.format_exc()
    finally:
        if part is not None and not part.IsModified and not report.get('reader_cleanup_unknown'):
            try:
                part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue,NXOpen.BasePart.CloseModified.CloseModified,None)
                report['closed_without_save']=True
            except Exception:
                report['close_error']=traceback.format_exc(); report['passed']=False
        elif part is not None:
            report['left_open_due_to_dirty_or_unknown']=True; report['passed']=False
        persist(); print(str(output))

if __name__=='__main__': main()
