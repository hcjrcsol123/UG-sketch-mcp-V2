"""Disposable native EXTRUDE/mirror expression ownership probe, not acceptance."""
import datetime, json, os, traceback, uuid
import NXOpen, NXOpen.Features, NXOpen.GeometricUtilities
ROOT=r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
s=NXOpen.Session.GetSession()
if s.Parts.Work is not None: raise RuntimeError("requires empty session")
run=uuid.uuid4().hex
path=os.path.join(ROOT,"workspace","v2","evidence","f19-expression-probe-"+run+".json")
r={"run_id":run,"utc":datetime.datetime.now(datetime.timezone.utc).isoformat(),"stage":"start","scope":"exploration only"}
def save():
    with open(path,"w",encoding="utf-8") as f: json.dump(r,f,indent=2,allow_nan=False)
def p(t): return NXOpen.Point3d(*map(float,t))
def v(t): return NXOpen.Vector3d(*map(float,t))
def rec(f):
    if f is None: return None
    return {"tag":str(f.Tag),"id":f.JournalIdentifier,"type":f.FeatureType,
            "parents":[str(x.Tag) for x in f.GetParents()],"children":[str(x.Tag) for x in f.GetChildren()],
            "bodies":[str(x.Tag) for x in f.GetBodies()]}
def expressions(part,seed):
    rows=[]
    for e in part.Expressions:
        row={"tag":str(e.Tag),"id":e.JournalIdentifier,"name":e.Name,"rhs":e.RightHandSide,
             "value":e.Value,"units":e.Units.JournalIdentifier if e.Units else None}
        try: row["owner"]=rec(e.GetOwningFeature())
        except Exception: row["owner_error"]=traceback.format_exc()
        try: row["users"]=[rec(x) for x in e.GetUsingFeatures()]
        except Exception: row["users_error"]=traceback.format_exc()
        rows.append(row)
    return {"expressions":rows,"seed_expressions":[str(x.Tag) for x in seed.GetExpressions()],"dirty":part.IsModified}
part=builder=None;save()
try:
    part=s.Parts.NewDisplay(os.path.join(ROOT,"workspace","v2","models","F19-expression-probe-"+run+".prt"),NXOpen.Part.Units.Millimeters)
    pts=[(0,0,1),(2,0,1),(2,3,1),(0,3,1)]
    curves=[part.Curves.CreateLine(p(a),p(b)) for a,b in zip(pts,pts[1:]+pts[:1])]
    sec=part.Sections.CreateSection(0.0095,0.01,0.5);sec.SetAllowedEntityTypes(NXOpen.Section.AllowTypes.OnlyCurves)
    rule=part.ScRuleFactory.CreateRuleCurveDumb(curves);sec.AddToSection([rule],curves[0],None,None,p(pts[0]),NXOpen.Section.Mode.Create,False)
    builder=part.Features.CreateExtrudeBuilder(None);builder.Section=sec
    builder.Direction=part.Directions.CreateDirection(p((0,0,0)),v((0,0,1)),NXOpen.SmartObject.UpdateOption.WithinModeling)
    builder.Limits.StartExtend.Value.RightHandSide="0";builder.Limits.EndExtend.Value.RightHandSide="4"
    builder.BooleanOperation.Type=NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Create;builder.BooleanOperation.SetTargetBodies([None])
    seed=builder.CommitFeature();builder.Destroy();builder=None
    r["seed_before"]=rec(seed);r["before"]=expressions(part,seed);r["stage"]="mirror";save()
    mark=s.SetUndoMark(NXOpen.Session.MarkVisibility.Invisible,"F19 expression probe")
    builder=part.Features.CreateMirrorFeatureBuilder(None);builder.FeatureSet.Add(seed)
    builder.PlaneOption=NXOpen.Features.MirrorFeatureBuilder.PlaneOptions.New
    builder.PlaneConstructor=part.Planes.CreatePlane(p((40,0,0)),v((2/7,-3/7,6/7)),NXOpen.SmartObject.UpdateOption.WithinModeling)
    mirror=builder.CommitFeature();builder.Destroy();builder=None
    r["update_errors"]=s.UpdateManager.DoUpdate(mark);r["mirror"]=rec(mirror)
    r["features_after"]=[rec(f) for f in part.Features];r["after"]=expressions(part,seed);r["stage"]="undo";save()
    s.UndoToMark(mark,None);r["undo_update_errors"]=s.UpdateManager.DoUpdate(mark);s.DeleteUndoMark(mark,None)
    r["after_undo"]=expressions(part,seed);r["stage"]="complete"
except Exception: r["error"]=traceback.format_exc()
finally:
    if builder is not None:
        try: builder.Destroy()
        except Exception: r["destroy_error"]=traceback.format_exc()
    if part is not None:
        try: part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue,NXOpen.BasePart.CloseModified.CloseModified,None)
        except Exception: r["close_error"]=traceback.format_exc()
    save();print(path)
