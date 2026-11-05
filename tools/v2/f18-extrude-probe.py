"""One NX2506 extrude-seed pattern and reopen API exploration."""
import datetime, json, os, traceback, uuid
import NXOpen, NXOpen.Features, NXOpen.GeometricUtilities, NXOpen.UF
ROOT = r"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp"
s = NXOpen.Session.GetSession()
if s.Parts.Work is not None: raise RuntimeError("requires an empty session")
run = uuid.uuid4().hex
model = os.path.join(ROOT, "workspace", "v2", "models", "F18-extrude-probe-" + run + ".prt")
path = os.path.join(ROOT, "workspace", "v2", "evidence", "f18-extrude-probe-" + run + ".json")
r = {"scope":"NX API exploration, not product acceptance", "run_id":run, "model":model,
     "utc":datetime.datetime.now(datetime.timezone.utc).isoformat(), "stage":"start"}
def save():
    with open(path,"w",encoding="utf-8") as out: json.dump(r,out,indent=2,allow_nan=False)
def p(t): return NXOpen.Point3d(*map(float,t))
def v(t): return NXOpen.Vector3d(*map(float,t))
def feat(f): return {"id":f.JournalIdentifier,"type":f.FeatureType,"class":type(f).__name__,"tag":str(f.Tag),
    "parents":[x.JournalIdentifier for x in f.GetParents()],"bodies":[str(x.Tag) for x in f.GetBodies()]}
def inspect(part, pattern):
    b = part.Features.CreatePatternFeatureBuilder(pattern)
    try:
        rect=b.PatternService.RectangularDefinition
        instances=[]
        for f in pattern.GetImmediateContainedFeatures():
            row=feat(f)
            if hasattr(f,"GetAllContainedFeatures"): row["contained"]=[feat(x) for x in f.GetAllContainedFeatures()]
            instances.append(row)
        return {"pattern":feat(pattern),"seeds":[feat(f) for f in b.FeatureList.GetArray()],
          "instances":instances,"all_contained":[feat(x) for x in pattern.GetAllContainedFeatures()],
          "associated_bodies":[str(x.Tag) for x in pattern.GetAssociatedBodies()],
          "type":str(b.PatternService.PatternType),"method":str(b.PatternMethod),
          "x_count":rect.XSpacing.NCopies.Value,"x_pitch":rect.XSpacing.PitchDistance.Value,
          "y_count":rect.YSpacing.NCopies.Value,"y_pitch":rect.YSpacing.PitchDistance.Value,
          "use_y":rect.UseYDirectionToggle,
          "x_direction":[rect.XDirection.Vector.X,rect.XDirection.Vector.Y,rect.XDirection.Vector.Z],
          "y_direction":[rect.YDirection.Vector.X,rect.YDirection.Vector.Y,rect.YDirection.Vector.Z],
          "boxes":[{"id":x.JournalIdentifier,"tag":str(x.Tag),"bounds":list(NXOpen.UF.UFSession.GetUFSession().ModlGeneral.AskBoundingBox(x.Tag))} for x in part.Bodies],
          "modified":part.IsModified}
    finally: b.Destroy()
part=builder=None
save()
try:
    part=s.Parts.NewDisplay(model,NXOpen.Part.Units.Millimeters)
    points=[(12,3,1),(14,3,1),(14,6,1),(12,6,1)]
    curves=[part.Curves.CreateLine(p(a),p(b)) for a,b in zip(points,points[1:]+points[:1])]
    section=part.Sections.CreateSection(0.0095,0.01,0.5)
    section.SetAllowedEntityTypes(NXOpen.Section.AllowTypes.OnlyCurves)
    rule=part.ScRuleFactory.CreateRuleCurveDumb(curves)
    section.AddToSection([rule],curves[0],None,None,p(points[0]),NXOpen.Section.Mode.Create,False)
    builder=part.Features.CreateExtrudeBuilder(None)
    builder.Section=section
    builder.Direction=part.Directions.CreateDirection(p((0,0,0)),v((0,0,1)),NXOpen.SmartObject.UpdateOption.WithinModeling)
    builder.Limits.StartExtend.Value.RightHandSide="0"
    builder.Limits.EndExtend.Value.RightHandSide="4"
    builder.BooleanOperation.Type=NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Create
    builder.BooleanOperation.SetTargetBodies([None])
    r["stage"]="extrude_commit"; save()
    seed=builder.CommitFeature(); builder.Destroy(); builder=None
    r["seed"]=feat(seed)
    builder=part.Features.CreatePatternFeatureBuilder(None)
    builder.FeatureList.Add([seed])
    builder.PatternMethod=NXOpen.Features.PatternFeatureBuilder.PatternMethodOptions.Simple
    builder.PatternService.PatternType=NXOpen.GeometricUtilities.PatternDefinition.PatternEnum.Linear
    rect=builder.PatternService.RectangularDefinition
    rect.UseYDirectionToggle=True
    rect.XSpacing.NCopies.SetFormula("3"); rect.XSpacing.PitchDistance.SetFormula("8")
    rect.YSpacing.NCopies.SetFormula("2"); rect.YSpacing.PitchDistance.SetFormula("11")
    rect.XDirection=part.Directions.CreateDirection(p((0,0,0)),v((-1,0,0)),NXOpen.SmartObject.UpdateOption.WithinModeling)
    rect.YDirection=part.Directions.CreateDirection(p((0,0,0)),v((0,1,0)),NXOpen.SmartObject.UpdateOption.WithinModeling)
    r["validate"]=builder.Validate(); r["stage"]="pattern_commit"; save()
    pattern=builder.CommitFeature(); builder.Destroy(); builder=None
    r["before_save"]=inspect(part,pattern)
    result=part.Save(NXOpen.BasePart.SaveComponents.TrueValue,NXOpen.BasePart.CloseAfterSave.FalseValue)
    r["unsaved_parts"]=result.NumberUnsavedParts; result.Dispose()
    part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue,NXOpen.BasePart.CloseModified.CloseModified,None); part=None
    r["stage"]="reopen"; save()
    part,load=s.Parts.OpenDisplay(model); load.Dispose()
    patterns=[f for f in part.Features if isinstance(f,NXOpen.Features.PatternFeature)]
    if len(patterns)!=1: raise RuntimeError("expected one pattern after reopen")
    r["after_reopen"]=inspect(part,patterns[0]); r["stage"]="complete"
except Exception:
    r["error"]=traceback.format_exc()
finally:
    if builder is not None:
        try: builder.Destroy()
        except Exception: r["destroy_error"]=traceback.format_exc()
    if part is not None:
        try: part.Close(NXOpen.BasePart.CloseWholeTree.FalseValue,NXOpen.BasePart.CloseModified.CloseModified,None)
        except Exception: r["close_error"]=traceback.format_exc()
    save()
