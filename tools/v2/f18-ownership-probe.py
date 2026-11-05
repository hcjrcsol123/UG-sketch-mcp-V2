"""Explore pattern-owned expressions/features/helpers on a disposable saved copy."""
import json, os, shutil, traceback, uuid
import NXOpen, NXOpen.Features, NXOpen.GeometricUtilities
ROOT=r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
s=NXOpen.Session.GetSession()
if s.Parts.Work is not None: raise RuntimeError("requires empty session")
run=uuid.uuid4().hex
source=os.path.join(ROOT,"workspace","v2","models","F18-extrude-probe-d7a66b69b9fc47d6bba8e50b84dfaa3d.prt")
copy=os.path.join(ROOT,"workspace","v2","models","F18-ownership-probe-"+run+".prt")
path=os.path.join(ROOT,"workspace","v2","evidence","f18-ownership-probe-"+run+".json")
shutil.copyfile(source,copy)
r={"scope":"API ownership exploration; not product acceptance","run_id":run,"copy":copy,"stage":"open"}
def persist():
    with open(path,"w",encoding="utf-8") as f:json.dump(r,f,indent=2,allow_nan=False)
def tag(x):return None if x is None else str(x.Tag)
def feature(f):
    return {"tag":tag(f),"id":f.JournalIdentifier,"type":f.FeatureType,"container":tag(f.ContainerFeature),
            "parents":[tag(x) for x in f.GetParents()],"children":[tag(x) for x in f.GetChildren()],
            "expressions":[tag(x) for x in f.GetExpressions()],"entities":[tag(x) for x in f.GetEntities()]}
def expression(e):
    return {"tag":tag(e),"id":e.JournalIdentifier,"name":e.Name,"rhs":e.RightHandSide,
            "owner":tag(e.GetOwningFeature()),"users":[tag(x) for x in e.GetUsingFeatures()]}
def inventory(part):
    return {"features":[feature(x) for x in part.Features],"expressions":[expression(x) for x in part.Expressions],
            "points":[{"tag":tag(x),"id":x.JournalIdentifier} for x in part.Points],
            "directions":[{"tag":tag(x),"id":x.JournalIdentifier} for x in part.Directions],
            "axes":[{"tag":tag(x),"id":x.JournalIdentifier,"point":tag(x.Point),"direction":tag(x.Direction)} for x in part.Axes]}
def P(x,y,z):return NXOpen.Point3d(float(x),float(y),float(z))
def V(x,y,z):return NXOpen.Vector3d(float(x),float(y),float(z))
part=b=None
persist()
try:
    part,load=s.Parts.OpenDisplay(copy);load.Dispose()
    bb=part.Features.CreateBlockFeatureBuilder(None)
    bb.SetOriginAndLengths(P(100,3,1),"2","3","4")
    bb.SetBooleanOperationAndTarget(NXOpen.Features.Feature.BooleanType.Create,None)
    seed=bb.CommitFeature();bb.Destroy()
    r["seed"]=feature(seed);r["before"]=inventory(part);r["stage"]="pattern";persist()
    b=part.Features.CreatePatternFeatureBuilder(None)
    b.FeatureList.Add([seed]);b.PatternMethod=NXOpen.Features.PatternFeatureBuilder.PatternMethodOptions.Simple
    b.PatternService.PatternType=NXOpen.GeometricUtilities.PatternDefinition.PatternEnum.Circular
    c=b.PatternService.CircularDefinition;c.AngularSpacing.NCopies.SetFormula("3");c.AngularSpacing.PitchAngle.SetFormula("-70")
    point=part.Points.CreatePoint(P(90,3,0));direction=part.Directions.CreateDirection(P(90,3,0),V(0,0,1),NXOpen.SmartObject.UpdateOption.WithinModeling)
    c.RotationAxis=part.Axes.CreateAxis(point,direction,NXOpen.SmartObject.UpdateOption.WithinModeling)
    pattern=b.CommitFeature();b.Destroy();b=None
    r["after"]=inventory(part);r["pattern"]=feature(pattern)
    r["contained"]=[feature(x) for x in pattern.GetAllContainedFeatures()]
    b=part.Features.CreatePatternFeatureBuilder(pattern)
    c=b.PatternService.CircularDefinition
    r["builder"]={"axis":tag(c.RotationAxis),"axis_point":tag(c.RotationAxis.Point),"axis_direction":tag(c.RotationAxis.Direction),
        "reference_point":tag(b.ReferencePointService.Point),"reference_inferred":b.ReferencePointService.IsReferencePointInferred,
        "spacing_count_expr":tag(c.AngularSpacing.NCopies),"spacing_angle_expr":tag(c.AngularSpacing.PitchAngle),
        "angle":c.AngularSpacing.PitchAngle.Value,"type":str(c.AngularSpacing.SpaceType),"pitch_option":str(c.AngularSpacing.UsePitchOption),
        "orientation":str(b.PatternService.PatternOrientation.CircularOrientationOption),"include_seed":c.IncludeSeedToggle,"flip":c.Flip,"use_radial":c.UseRadialDirectionToggle}
    b.Destroy();b=None;r["stage"]="complete"
except Exception:r["error"]=traceback.format_exc()
finally:
    if b is not None:
        try:b.Destroy()
        except Exception:r["destroy_error"]=traceback.format_exc()
    if part is not None:
        try:part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue,NXOpen.BasePart.CloseModified.CloseModified,None)
        except Exception:r["close_error"]=traceback.format_exc()
    persist()
