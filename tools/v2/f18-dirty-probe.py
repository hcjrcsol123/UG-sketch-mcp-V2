"""Observe read-builder side effects on a disposable copy of an F18 probe part."""
import datetime, json, os, shutil, sys, traceback, uuid
import NXOpen, NXOpen.Features
ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
session=NXOpen.Session.GetSession()
if session.Parts.Work is not None: raise RuntimeError("requires empty session")
source=os.path.join(ROOT,"workspace","v2","models","F18-extrude-probe-d7a66b69b9fc47d6bba8e50b84dfaa3d.prt")
run=uuid.uuid4().hex
model=os.path.join(ROOT,"workspace","v2","models","F18-dirty-probe-"+run+".prt")
output=os.path.join(ROOT,"workspace","v2","evidence","f18-dirty-probe-"+run+".json")
shutil.copyfile(source,model)
r={"run_id":run,"scope":"disposable API side effect exploration","source":source,"copy":model,"stages":[],"stage":"start"}
part=builder=None
def persist():
    with open(output,"w",encoding="utf-8") as f: json.dump(r,f,indent=2)
def stage(name):
    r["stages"].append({"name":name,"modified":bool(part.IsModified),"directions":len(list(part.Directions)),"points":len(list(part.Points)),"axes":len(list(part.Axes)),"expressions":len(list(part.Expressions))}); persist()
persist()
try:
    part,load=session.Parts.OpenDisplay(model); load.Dispose()
    stage("open")
    patterns=[f for f in part.Features if isinstance(f,NXOpen.Features.PatternFeature)]
    stage("find_pattern")
    mark=session.SetUndoMark(NXOpen.Session.MarkVisibility.Invisible,"F18 disposable read")
    builder=part.Features.CreatePatternFeatureBuilder(patterns[0]); stage("create_builder")
    rect=builder.PatternService.RectangularDefinition; stage("rect_definition")
    _=rect.XDirection.Vector; _=rect.YDirection.Vector; stage("direction_getters")
    _=builder.FeatureList.GetArray(); stage("seed_getter")
    _=patterns[0].GetAllContainedFeatures(); _=patterns[0].GetAssociatedBodies(); stage("containment_getters")
    builder.Destroy(); builder=None; stage("destroy_builder")
    session.UndoToMarkWithStatus(mark,"F18 disposable read"); session.UpdateManager.DoUpdate(mark); stage("undo_read")
    r["stage"]="complete"
except Exception: r["error"]=traceback.format_exc()
finally:
    if builder is not None:
        try: builder.Destroy()
        except Exception: r["destroy_error"]=traceback.format_exc()
    if part is not None:
        try: part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue,NXOpen.BasePart.CloseModified.CloseModified,None)
        except Exception: r["close_error"]=traceback.format_exc()
    persist()
