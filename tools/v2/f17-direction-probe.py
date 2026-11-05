"""Map native first offset to face order on each axis-aligned block edge."""
import datetime, json, os, traceback, uuid
import NXOpen, NXOpen.Features, NXOpen.UF
ROOT=r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
session=NXOpen.Session.GetSession()
if session.Parts.Work is not None: raise RuntimeError("Expected empty session")
uf=NXOpen.UF.UFSession.GetUFSession()
run=uuid.uuid4().hex
path=os.path.join(ROOT,"workspace","v2","evidence","f17-direction-probe-"+run+".json")
report={"run_id":run,"utc":datetime.datetime.now(datetime.timezone.utc).isoformat(),"complete":False,"cases":[],"scope":"exploratory API, not product acceptance"}
def persist():
    with open(path,"w",encoding="utf-8") as f: json.dump(report,f,indent=2,allow_nan=False)
def edge(e):
    a,b=e.GetVertices()
    return {"id":e.JournalIdentifier,"tag":str(e.Tag),"vertices":[[a.X,a.Y,a.Z],[b.X,b.Y,b.Z]]}
def face(f):
    kind,p,d,box,r,rd,n=uf.Modeling.AskFaceData(f.Tag)
    return {"id":f.JournalIdentifier,"tag":str(f.Tag),"type":kind,"point":list(p),"direction":list(d),"normal_sign":n,"edges":[edge(e) for e in f.GetEdges()]}
persist()
for axis in range(3):
    transverse=[i for i in range(3) if i!=axis]
    lengths=[20.0,16.0,12.0]
    for high1 in (False,True):
        for high2 in (False,True):
            row={"axis":axis,"high1":high1,"high2":high2,"stage":"new_part"}; report["cases"].append(row); persist()
            part=builder=None
            try:
                model=os.path.join(ROOT,"workspace","v2","models","F17-dir-"+run+"-"+str(axis)+str(int(high1))+str(int(high2))+".prt")
                part=session.Parts.NewDisplay(model,NXOpen.Part.Units.Millimeters)
                bb=part.Features.CreateBlockFeatureBuilder(None)
                bb.SetOriginAndLengths(NXOpen.Point3d(0.0,0.0,0.0),"20","16","12")
                bb.SetBooleanOperationAndTarget(NXOpen.Features.Feature.BooleanType.Create,None)
                body=bb.CommitFeature().GetBodies()[0]; bb.Destroy()
                chosen=[]
                fixed=[lengths[transverse[0]] if high1 else 0.0,lengths[transverse[1]] if high2 else 0.0]
                for e in body.GetEdges():
                    a,b=edge(e)["vertices"]
                    if abs(abs(a[axis]-b[axis])-lengths[axis])<1e-8 and all(abs(a[t]-v)<1e-8 and abs(b[t]-v)<1e-8 for t,v in zip(transverse,fixed)): chosen.append(e)
                if len(chosen)!=1: raise RuntimeError("Expected unique edge")
                row["selected"]=edge(chosen[0]); row["input_faces_ordered"]=[face(f) for f in chosen[0].GetFaces()]
                mark=session.SetUndoMark(NXOpen.Session.MarkVisibility.Invisible,"F17 direction probe")
                builder=part.Features.CreateChamferBuilder(None)
                collector=part.ScCollectors.CreateCollector(); collector.ReplaceRules([part.ScRuleFactory.CreateRuleEdgeDumb(chosen)],False)
                builder.SmartCollector=collector; builder.Option=NXOpen.Features.ChamferBuilder.ChamferOption.TwoOffsets
                builder.Method=NXOpen.Features.ChamferBuilder.OffsetMethod.EdgesAlongFaces; builder.ReverseOffsets=False
                builder.FirstOffsetExp.RightHandSide="2"; builder.SecondOffsetExp.RightHandSide="4"
                row["stage"]="commit"; persist(); feature=builder.CommitFeature(); builder.Destroy(); builder=None
                row["update_errors"]=session.UpdateManager.DoUpdate(mark)
                row["faces"]=[face(f) for f in feature.GetBodies()[0].GetFaces()]; row["stage"]="complete"
            except Exception: row["error"]=traceback.format_exc()
            finally:
                if builder is not None:
                    try: builder.Destroy()
                    except Exception: row["destroy_error"]=traceback.format_exc()
                if part is not None:
                    try: part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue,NXOpen.BasePart.CloseModified.CloseModified,None)
                    except Exception: row["close_error"]=traceback.format_exc()
                persist()
report["complete"]=True; persist(); print(path)
