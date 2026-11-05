"""Create a unique saved F21 fixture, with old spatial line/arc and solid.

Only run as the coordinated operator in an empty NX batch session. This is
fixture setup, not an F21 product test or evidence of installed acceptance.
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

ROOT=Path(r'C:\0sf\1PROJECT\AI\design\nx-sketch-mcp')
spec=importlib.util.spec_from_file_location('f19_fixture',ROOT/'tools/v2/f19-create-mcp-fixture.py')
base=importlib.util.module_from_spec(spec); spec.loader.exec_module(base)
LINE={'curve_type':'line','start_mm':[-100,-100,5],'end_mm':[-96,-94,13]}
ARC={'curve_type':'arc','definition_mode':'center','center_mm':[-150,-140,17],
     'normal':[0,0,1],'x_axis':[1,0,0],'radius_mm':7,'start_angle_deg':15,'end_angle_deg':125}

def main():
    session=NXOpen.Session.GetSession()
    if session.Parts.Work is not None or session.IsInTaskEnvironment or session.ActiveSketch is not None:
        raise RuntimeError('fixture requires empty independent NX session')
    run=uuid.uuid4().hex; models=ROOT/'workspace/v2/models'; evidence=ROOT/'workspace/v2/evidence'
    models.mkdir(parents=True,exist_ok=True); evidence.mkdir(parents=True,exist_ok=True)
    model=models/('F21-mcp-fixture-'+run+'.prt'); report_path=evidence/('f21-fixture-'+run+'.json')
    report={'run_id':run,'utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'part_path':str(model),'stage':'STARTED','saved':False,'reopened':False,
            'scope':'fixture setup only; not product acceptance',
            'sentinels':[{'name':'old_line','request':LINE},{'name':'old_arc','request':ARC}]}
    with report_path.open('x',encoding='utf-8') as f: json.dump(report,f)
    def persist():
        with report_path.open('w',encoding='utf-8') as f: json.dump(report,f,indent=2,allow_nan=False)
    part=None
    try:
        if model.exists(): raise FileExistsError('fixture must be unique')
        part=session.Parts.NewDisplay(str(model),NXOpen.Part.Units.Millimeters)
        feature,body=base.block(part,(-200,-200,0),(3,4,5))
        line=part.Curves.CreateLine(base.point(*LINE['start_mm']),base.point(*LINE['end_mm']))
        arc=part.Curves.CreateArc(base.point(*ARC['center_mm']),NXOpen.Vector3d(1.0,0.0,0.0),NXOpen.Vector3d(0.0,1.0,0.0),
                                  float(ARC['radius_mm']),math.radians(ARC['start_angle_deg']),math.radians(ARC['end_angle_deg']))
        # All independent source curves precede every Sketch.  No F01/F20 MCP
        # operation is invoked as setup in the later installed acceptance.
        paths=[]
        for i in range(5):
            x=i*40.0
            source=part.Curves.CreateLine(base.point(x,0,0),base.point(x,0,5))
            path_ids=[source.JournalIdentifier]
            if i in (2,3):
                bend=part.Curves.CreateArc(base.point(x+10,0,5),NXOpen.Vector3d(-1.0,0.0,0.0),NXOpen.Vector3d(0.0,0.0,1.0),10.0,0.0,math.pi/2)
                path_ids.append(bend.JournalIdentifier)
            paths.append(path_ids)
        report['creation_path_ids_historical']=paths;report['stage']='PATHS_CREATED';persist()
        dll=Path(os.environ['F21_FIXTURE_SKETCH_DLL']).resolve(strict=True)
        if not dll.is_relative_to(evidence.resolve()) or dll.name!='F21McpFixtureSketches.dll':raise ValueError('unique offline-compiled fixture Sketch helper required')
        report['sketch_helper']=str(dll);persist()
        if session.Execute(str(dll),'F21McpFixtureSketches','Run',[])!=0:raise RuntimeError('native fixture Sketch helper failed')
        if session.ActiveSketch is not None or len(list(part.Sketches))!=5:raise AssertionError('five inactive fixture Sketches required')
        report.update(creation_curve_ids_historical=[line.JournalIdentifier,arc.JournalIdentifier],
                      solid_feature_id=feature.JournalIdentifier,solid_body_id=body.JournalIdentifier,
                      solid_bbox_mm=[-200,-200,0,-197,-196,5],units=str(part.PartUnits))
        if len(list(part.Bodies))!=1 or not body.IsSolidBody: raise AssertionError('missing sentinel solid')
        save=part.Save(NXOpen.BasePart.SaveComponents.TrueValue,NXOpen.BasePart.CloseAfterSave.FalseValue)
        try: report.update(unsaved_parts=int(save.NumberUnsavedParts),unsaved_objects=int(save.NumberUnsavedObjects))
        finally: save.Dispose()
        if report['unsaved_parts'] or report['unsaved_objects'] or part.IsModified or not model.is_file():
            raise AssertionError('fixture save failed')
        report.update(saved=True,stage='SAVED'); persist()
        part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue,NXOpen.BasePart.CloseModified.CloseModified,None); part=None
        part,load=session.Parts.OpenDisplay(str(model))
        try: unloaded=int(load.NumberUnloadedParts)
        finally: load.Dispose()
        curves=list(part.Curves);sketches=list(part.Sketches)
        sketch_tags={str(item.Tag) for sketch in sketches for item in sketch.GetAllGeometry()}
        independent=[c for c in curves if str(c.Tag) not in sketch_tags]
        if unloaded or part.IsModified or len(list(part.Bodies))!=1 or len(sketches)!=5 or len(independent)!=9 or len([c for c in independent if isinstance(c,NXOpen.Line)])!=6 or len([c for c in independent if isinstance(c,NXOpen.Arc)])!=3:
            raise AssertionError('reopened fixture inventory mismatch')
        origins=sorted(round(float(s.Origin.X),6) for s in sketches)
        if origins!=[0.0,40.0,80.0,120.0,160.0] or sorted(len(s.GetAllGeometry()) for s in sketches)!=[2,3,3,3,3]:
            raise AssertionError('fixture Sketch origins or membership counts differ')
        report.update(reopened=True,stage='COMPLETE',reopened_independent_curve_count=len(independent),reopened_sketch_count=len(sketches),
                      reopened_curve_ids_historical=[c.JournalIdentifier for c in independent],reopened_dirty=bool(part.IsModified)); persist()
        part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue,NXOpen.BasePart.CloseModified.CloseModified,None); part=None
        report['closed_without_save']=True; persist(); print(str(report_path))
    except Exception:
        report['error']=traceback.format_exc(); report['left_open_for_inspection']=part is not None; persist(); raise

if __name__=='__main__': main()
