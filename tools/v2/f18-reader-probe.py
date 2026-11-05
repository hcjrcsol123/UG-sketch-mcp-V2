"""Run the C# nested-reader probe on a disposable copy, then close without saving."""
import json, os, shutil, traceback, uuid
import NXOpen
ROOT=r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
session=NXOpen.Session.GetSession()
if session.Parts.Work is not None: raise RuntimeError("requires an empty session")
run=uuid.uuid4().hex
source=os.path.join(ROOT,"workspace","v2","models","F18-extrude-probe-d7a66b69b9fc47d6bba8e50b84dfaa3d.prt")
copy=os.path.join(ROOT,"workspace","v2","models","F18-reader-probe-"+run+".prt")
path=os.path.join(ROOT,"workspace","v2","evidence","f18-reader-probe-"+run+".json")
wrapper=os.path.join(ROOT,"workspace","v2","F18-reader-probe.dll")
shutil.copyfile(source,copy)
os.environ["F18_READER_REPORT"]=path
part=None
try:
    part,load=session.Parts.OpenDisplay(copy); load.Dispose()
    result=session.Execute(wrapper,"NXJournal","Run",[])
    print("C# reader probe return:",result,"report:",path)
finally:
    if part is not None:
        part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue,NXOpen.BasePart.CloseModified.CloseModified,None)
