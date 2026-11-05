using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.Features;
using NxSketchMcp.M2;

public sealed partial class M2OpenModelAdapter
{
    private static string F19Inventory(Part part)
    { return M2Json.CanonicalText(new object[] { F18Inventory(part), F19PlaneInventory(part), F19DatumInventory(part) }); }

    private static string F19Inventory(Part part, string excludedCurveTag)
    { return M2Json.CanonicalText(new object[] { F18Inventory(part,excludedCurveTag), F19PlaneInventory(part), F19DatumInventory(part) }); }

    private static string F19DatumInventory(Part part)
    {
        Dictionary<string,object> datums=new Dictionary<string,object>(StringComparer.Ordinal);
        foreach(Feature feature in part.Features)
        {
            DatumPlaneFeature f=feature as DatumPlaneFeature; if(f==null)continue;
            DatumPlane d=f.DatumPlane;
            if(d==null)throw new InvalidOperationException("datum plane feature has no plane");
            datums.Add(f.Tag.ToString(),new object[]{f.JournalIdentifier,d.Tag.ToString(),d.JournalIdentifier,
                F19Components(d.Origin),F19Components(d.Normal)});
        }
        return M2Json.CanonicalText(datums);
    }

    private static string F19PlaneInventory(Part part)
    {
        List<object> planes = new List<object>();
        foreach (Plane p in part.Planes.ToArray())
        {
            List<object> references=new List<object>(); NXObject[] geometry=p.GetGeometry();
            if(geometry==null)throw new InvalidOperationException("plane geometry references unavailable");
            foreach(NXObject item in geometry)
            {
                if(item==null)throw new InvalidOperationException("plane geometry reference is null");
                references.Add(new object[]{item.Tag.ToString(),item.JournalIdentifier,item.GetType().FullName});
            }
            Expression expression=p.Expression, offset=p.OffsetExpression;
            planes.Add(new object[] { p.Tag.ToString(), p.JournalIdentifier, F16Point(p.Origin),
                new double[] { p.Normal.X, p.Normal.Y, p.Normal.Z }, p.Method.ToString(), p.Flip, p.OffsetFlip,
                references, F19ExpressionInventory(expression), F19ExpressionInventory(offset) });
        }
        return M2Json.CanonicalText(planes);
    }

    private static object F19ExpressionInventory(Expression e)
    { return e==null?null:new object[]{e.Tag.ToString(),e.JournalIdentifier,e.Name,e.RightHandSide,e.Value,e.Units==null?null:e.Units.JournalIdentifier}; }

    private static Dictionary<string,object> F19ExpressionStates(Part part)
    {
        Dictionary<string,object> result=new Dictionary<string,object>(StringComparer.Ordinal);
        foreach(Expression e in part.Expressions)
        {
            Feature owner=e.GetOwningFeature(); Feature[] usingFeatures=e.GetUsingFeatures();
            if(usingFeatures==null)throw new InvalidOperationException("expression users unavailable");
            List<string> users=new List<string>(); foreach(Feature f in usingFeatures)
            {if(f==null)throw new InvalidOperationException("null expression user");users.Add(f.Tag.ToString());}
            users.Sort(StringComparer.Ordinal);
            if(new HashSet<string>(users,StringComparer.Ordinal).Count!=users.Count)throw new InvalidOperationException("duplicate expression user");
            result.Add(e.Tag.ToString(),new Dictionary<string,object>{{"tag",e.Tag.ToString()},{"id",e.JournalIdentifier},{"name",e.Name},
                {"type",e.Type},{"rhs",e.RightHandSide},{"value",e.Value},{"units",e.Units==null?null:e.Units.JournalIdentifier},
                {"owner_tag",owner==null?null:owner.Tag.ToString()},{"user_tags",users}});
        }
        return result;
    }

    private static bool F19ExpressionPreservation(Part part,M3PlanState plan,Feature seed,Feature output,
        Dictionary<string,string> stableAfter,out Dictionary<string,object> evidence)
    {
        Dictionary<string,object> after=F19ExpressionStates(part);
        HashSet<string> outputExpressions=new HashSet<string>(StringComparer.Ordinal);
        if(output!=null) foreach(Expression e in output.GetExpressions()) if(e!=null)outputExpressions.Add(e.Tag.ToString());
        HashSet<string> seedExpressions=new HashSet<string>(StringComparer.Ordinal);
        if(seed!=null)foreach(Expression e in seed.GetExpressions())if(e!=null)seedExpressions.Add(e.Tag.ToString());
        List<string> seedExpressionRows=new List<string>(seedExpressions),outputExpressionRows=new List<string>(outputExpressions);
        seedExpressionRows.Sort(StringComparer.Ordinal);outputExpressionRows.Sort(StringComparer.Ordinal);
        List<object> changes=new List<object>(); bool ok=after.Count==plan.F19ExpressionsBefore.Count && seedExpressions.SetEquals(plan.F19SeedExpressionTags);
        foreach(KeyValuePair<string,object> entry in plan.F19ExpressionsBefore)
        {
            object currentValue; if(!after.TryGetValue(entry.Key,out currentValue)){ok=false;continue;}
            Dictionary<string,object> current=currentValue as Dictionary<string,object>,before=entry.Value as Dictionary<string,object>;
            if(before==null || current==null){ok=false;continue;}
            if(M2Json.CanonicalText(before)==M2Json.CanonicalText(current))continue;
            bool unchangedCore=true;
            foreach(string key in new string[]{"tag","id","name","type","value","units"})
                if(!Object.Equals(before[key],current[key])) unchangedCore=false;
            List<string> oldUsers=before["user_tags"] as List<string>,newUsers=current["user_tags"] as List<string>;
            bool allowed=seed!=null && output!=null && unchangedCore && plan.F19SeedExpressionTags.Contains(entry.Key) &&
                seedExpressions.Contains(entry.Key) && outputExpressions.Contains(entry.Key) && Object.Equals(before["owner_tag"],seed.Tag.ToString()) && current["owner_tag"]==null &&
                oldUsers!=null && oldUsers.Count==1 && oldUsers[0]==seed.Tag.ToString() &&
                newUsers!=null && newUsers.Count==2 && new HashSet<string>(newUsers,StringComparer.Ordinal).SetEquals(new string[]{seed.Tag.ToString(),output.Tag.ToString()}) &&
                before["rhs"] is string && Object.Equals(current["rhs"],(string)before["rhs"]+" // Used By ...");
            changes.Add(new Dictionary<string,object>{{"tag",entry.Key},{"allowed_shared_seed_expression",allowed},{"before",before},{"after",current},
                {"output_references_same_expression",outputExpressions.Contains(entry.Key)}});
            if(allowed)
            {
                string prior;if(plan.F18StableBefore.TryGetValue("E:"+entry.Key,out prior))stableAfter["E:"+entry.Key]=prior;
                else ok=false;
            }
            else ok=false;
        }
        evidence=new Dictionary<string,object>{{"status",ok?"VERIFIED":"MISMATCH"},{"before",plan.F19ExpressionsBefore},{"after",after},
            {"seed_expression_tags",seedExpressionRows},{"output_expression_tags",outputExpressionRows},{"changes",changes}};
        return ok;
    }

    private static Dictionary<string, object> F19Read(Part part, MirrorFeature feature)
    {
        try { return V2FeatureMirrorFactory.ReadIsolated(Session.GetSession(), part, feature, delegate { return F19Inventory(part); }); }
        catch (V2FeatureMirrorFactory.ReadStateUnknownException ex) { throw M3AnalysisStateUnknown("mirror reader could not restore model state", ex); }
    }

    private static Dictionary<string, object> F19CaptureDefinitions(Part part, string excluded)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        List<string> ids = new List<string>();
        foreach (Feature f in part.Features) if (f is MirrorFeature && f.JournalIdentifier != excluded) ids.Add(f.JournalIdentifier);
        ids.Sort(StringComparer.Ordinal);
        List<M3BodyMeasure> measures = null; Dictionary<string,Dictionary<string,object>> details = null;
        if (ids.Count != 0)
        {
            measures=CaptureM3BodyMeasures(part,F12MassPropertyAccuracy);
            details=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            CaptureF12BodyFingerprints(part,measures,details);
        }
        foreach (string id in ids)
        {
            try
            {
                MirrorFeature mirror = FindFeature(part, id) as MirrorFeature;
                Dictionary<string, object> row = F19Read(part, mirror);
                Feature seed = FindFeature(part, (string)row["seed_feature_id"]);
                if (!(seed is Block) && !(seed is Extrude)) throw new InvalidOperationException("unsupported mirror seed");
                F18RequireCreateSeed(part, seed); seed = FindFeature(part, (string)row["seed_feature_id"]);
                Body[] seedBodies = seed.GetBodies();
                if (seedBodies == null || seedBodies.Length != 1 || !seedBodies[0].IsSolidBody) throw new InvalidOperationException("invalid seed body");
                double[] box; F16Preflight(seedBodies[0], new string[0], 0, out box);
                Body output = FindBody(part, (string)row["body_id"]);
                Dictionary<string, object> shape, native, detail=null; double? volume=null;
                if(output!=null) {details.TryGetValue(output.Tag.ToString(),out detail); foreach(M3BodyMeasure m in measures)if(m.Body.Tag.Equals(output.Tag))volume=m.VolumeMm3;}
                if (output == null || !F19HealthyBody(detail) || !volume.HasValue || !F19BodyMatches(output, F19Expected(box, (double[])row["plane_origin_mm"], (double[])row["plane_normal"]), detail, volume, out shape, out native))
                    throw new InvalidOperationException("existing mirror geometry differs");
                row.Remove("body_tag"); row.Remove("output_feature_tag");
                result.Add(id, row);
            }
            catch (M2ModelException ex) { if (ex.Code == "M3_ANALYSIS_STATE_UNKNOWN") throw; throw new M2ModelException("UNSUPPORTED_MIRROR_PRESERVATION", "existing mirror cannot be proven supported"); }
            catch (Exception) { throw new M2ModelException("UNSUPPORTED_MIRROR_PRESERVATION", "existing mirror cannot be proven supported"); }
        }
        return result;
    }

    private static double[] F19Components(Point3d p) { return new double[] { p.X, p.Y, p.Z }; }
    private static double[] F19Components(Vector3d p) { return new double[] { p.X, p.Y, p.Z }; }
    private static bool F19DefinitionsMatch(Part part, M3PlanState plan)
    { return F18RelatedDefinitionsMatch(part, plan, null) && F16DefinitionsEqual(plan.F19MirrorsBefore, F19CaptureDefinitions(part, plan.FeatureId)); }

    public M3PlanSnapshot PrepareFeatureMirror(M2ModelBinding binding, M3FeatureMirrorRequest source)
    {
        if (source == null || String.IsNullOrWhiteSpace(source.RevisionId) || String.IsNullOrWhiteSpace(source.SeedFeatureId) || source.SeedFeatureId.Length > 4096)
            throw new M2ModelException("INVALID_REQUEST", "invalid mirror request");
        bool datumMode = !String.IsNullOrWhiteSpace(source.DatumPlaneId);
        if (datumMode ? (source.DatumPlaneId.Length > 4096 || source.PlaneOriginMm != null || source.PlaneNormal != null) :
            (source.PlaneOriginMm == null || source.PlaneOriginMm.Length != 3 || !F18Unit(source.PlaneNormal)))
            throw new M2ModelException("INVALID_REQUEST", "provide one native datum or an origin and unit normal");
        if (!datumMode) foreach (double x in source.PlaneOriginMm) if (!IsFinite(x)) throw new M2ModelException("INVALID_REQUEST", "plane origin must be finite");
        BindingState state = RequireState(binding); bool dirty = state.WorkPart.IsModified; string inventory;
        try { inventory = F19Inventory(state.WorkPart); if (dirty != state.WorkPart.IsModified) throw new InvalidOperationException(); }
        catch (Exception ex) { throw M3AnalysisStateUnknown("mirror inventory changed model state", ex); }
        RequireWritableSimplePart(state.WorkPart); RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null) throw new M2ModelException("NX_BUSY", "end active sketch");
        SnapshotResult capture = Capture(binding, state);
        if (source.RevisionId != binding.RevisionId) throw new M2ModelException("STALE_REVISION", "revision changed");
        Feature seed = FindFeature(state.WorkPart, source.SeedFeatureId);
        if (!(seed is Block) && !(seed is Extrude)) throw new M2ModelException("UNSUPPORTED_MIRROR_SEED", "seed must be a native block or extrude");
        F18RequireCreateSeed(state.WorkPart, seed); seed = FindFeature(state.WorkPart, source.SeedFeatureId);
        Body[] bodies = seed.GetBodies(); Feature[] children = seed.GetChildren();
        if (bodies == null || bodies.Length != 1 || bodies[0] == null || !bodies[0].IsSolidBody || children == null || children.Length != 0)
            throw new M2ModelException("UNSUPPORTED_MIRROR_SEED", "seed must own one unmodified solid without children");
        DatumPlaneFeature datum = datumMode ? FindFeature(state.WorkPart, source.DatumPlaneId) as DatumPlaneFeature : null;
        if (datumMode && (datum == null || datum.IsOccurrence || datum.OwningPart == null || !datum.OwningPart.Tag.Equals(state.WorkPart.Tag) || datum.DatumPlane == null))
            throw new M2ModelException("INVALID_DATUM_PLANE", "datum must be a native work-part DatumPlaneFeature");
        double[] origin = datumMode ? F19Components(datum.DatumPlane.Origin) : (double[])source.PlaneOriginMm.Clone();
        double[] normal = datumMode ? F19Components(datum.DatumPlane.Normal) : (double[])source.PlaneNormal.Clone();
        if (!F18Unit(normal)) throw new M2ModelException("UNSUPPORTED_MIRROR_PLANE", "native datum normal must be unit");
        double[] box; F16Preflight(bodies[0], new string[0], 0, out box);
        List<double[][]> expected = new List<double[][]>(); double[][] original = new double[8][];
        for (int i = 0; i < 8; i++) original[i] = F18Corner(box, i);
        expected.Add(original); expected.Add(F19Expected(box, origin, normal)); F18PreflightClearance(state.WorkPart, bodies[0], expected);
        M3PlanState plan = CreateM3PlanState(binding, state, "create_feature_mirror", capture, F12MassPropertyAccuracy);
        plan.FeatureMirrorRequest = new M3FeatureMirrorRequest { RevisionId=source.RevisionId, SeedFeatureId=source.SeedFeatureId,
            DatumPlaneId=source.DatumPlaneId, PlaneOriginMm=datumMode?null:(double[])origin.Clone(), PlaneNormal=datumMode?null:(double[])normal.Clone() };
        plan.F19Origin=origin; plan.F19Normal=normal; plan.F19DatumTag=datum==null?null:datum.Tag.ToString();
        plan.F19DatumChildrenBefore=new List<string>(); if (datum != null) foreach (Feature f in datum.GetChildren()) plan.F19DatumChildrenBefore.Add(f.Tag.ToString());
        plan.TargetBody=bodies[0]; plan.TargetBodyId=RequiredJournalId(bodies[0], "seed body"); plan.F18SeedTag=seed.Tag.ToString(); plan.F18SeedBox=box;
        plan.F12SemanticBefore=capture.Snapshot; plan.F12CurveInventoryBefore=M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart));
        plan.HoleHelpersBefore=F14CaptureHelpers(state.WorkPart); plan.HoleDefinitionsBefore=F14CapturePersistentHoleDefinitions(state.WorkPart,null);
        plan.BooleanDefinitionsBefore=F15CaptureDefinitions(state.WorkPart,null); plan.EdgeBlendDefinitionsBefore=F16CaptureDefinitions(state.WorkPart,null);
        plan.ChamferDefinitionsBefore=F17CaptureDefinitions(state.WorkPart,null); plan.F18PatternReferenceTagsBefore=new Dictionary<string,string>(StringComparer.Ordinal);
        plan.F18PatternsBefore=F18CaptureDefinitions(state.WorkPart,null,plan.F18PatternReferenceTagsBefore);
        plan.F19MirrorsBefore=F19CaptureDefinitions(state.WorkPart,null);
        plan.F19OldMirrorBodyTags=new HashSet<string>(StringComparer.Ordinal);
        foreach(object value in plan.F19MirrorsBefore.Values)
        {
            Dictionary<string,object> row=value as Dictionary<string,object>;
            Body mirrorBody=row==null?null:FindBody(state.WorkPart,row["body_id"] as string);
            if(mirrorBody==null || !plan.F19OldMirrorBodyTags.Add(mirrorBody.Tag.ToString()))
                throw new M2ModelException("UNSUPPORTED_MIRROR_PRESERVATION","prior mirror output cannot be uniquely resolved");
        }
        plan.F19PlanesBefore=F19PlaneInventory(state.WorkPart);
        plan.F19DatumsBefore=F19DatumInventory(state.WorkPart);
        plan.F19ExpressionsBefore=F19ExpressionStates(state.WorkPart);
        plan.F19SeedExpressionTags=new HashSet<string>(StringComparer.Ordinal);
        foreach(Expression expression in seed.GetExpressions()) {if(expression==null || !plan.F19SeedExpressionTags.Add(expression.Tag.ToString()))throw new InvalidOperationException("invalid seed expression list");}
        plan.F18StableBefore=F18StableInventory(state.WorkPart,plan.F18SeedTag); plan.F18InventoryBefore=inventory;
        plan.F19BodyDetailsBefore=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
        plan.BodyFingerprintsBefore=CaptureF12BodyFingerprints(state.WorkPart,plan.BodiesBefore,plan.F19BodyDetailsBefore);
        if (state.WorkPart.IsModified != dirty || plan.WorkPartModifiedBefore != dirty || inventory != F19Inventory(state.WorkPart))
            throw M3AnalysisStateUnknown("mirror preparation changed model state", null);
        Dictionary<string,object> view=new Dictionary<string,object>{{"kind","create_feature_mirror"},{"revision_id",binding.RevisionId},
            {"seed_feature_id",source.SeedFeatureId},{"seed_body_id",plan.TargetBodyId},{"plane_mode",datumMode?"datum":"numeric"},
            {"datum_plane_id",source.DatumPlaneId},{"plane_origin_mm",origin},{"plane_normal",normal},{"seed_bounds_mm",box}};
        return new M3PlanSnapshot {Kind="create_feature_mirror",RevisionId=binding.RevisionId,PublicView=view,AdapterState=plan};
    }

    private static bool F19DefinitionMatches(M3PlanState plan, Dictionary<string, object> d)
    {
        M3FeatureMirrorRequest r=plan.FeatureMirrorRequest;
        return d!=null && Object.Equals(d["feature_id"],plan.FeatureId) && Object.Equals(d["seed_feature_id"],r.SeedFeatureId) &&
            Object.Equals(d["plane_mode"],r.DatumPlaneId==null?"numeric":"datum") && Object.Equals(d["datum_plane_id"],r.DatumPlaneId) &&
            F18VectorMatches(d["plane_origin_mm"],plan.F19Origin) && F18VectorMatches(d["plane_normal"],plan.F19Normal);
    }

    private static bool F19Geometry(Part part,M3PlanState plan,Dictionary<string,object> definition,
        Dictionary<string,Dictionary<string,object>> details,List<M3BodyMeasure> measures,out Dictionary<string,object> evidence)
    {
        Feature seed=FindFeature(part,plan.FeatureMirrorRequest.SeedFeatureId); Body[] source=seed==null?null:seed.GetBodies();
        Body output=FindBody(part,(string)definition["body_id"]); Dictionary<string,object> seedShape=null,seedNative=null,shape=null,native=null,detail=null;
        double? volume=null; if(output!=null && details!=null) details.TryGetValue(output.Tag.ToString(),out detail);
        if(output!=null && measures!=null) foreach(M3BodyMeasure m in measures) if(m.Body.Tag.Equals(output.Tag)) volume=m.VolumeMm3;
        double[][] expectedSeed=new double[8][];for(int i=0;i<8;i++)expectedSeed[i]=F18Corner(plan.F18SeedBox,i);
        bool ok=seed!=null && seed.Tag.ToString()==plan.F18SeedTag && source!=null && source.Length==1 &&
            F18MeasuredShape(source[0],expectedSeed,details,measures,out seedShape,out seedNative) && output!=null &&
            output.Tag.ToString()==(string)definition["body_tag"] && F19HealthyBody(detail) && volume.HasValue &&
            F19BodyMatches(output,F19Expected(plan.F18SeedBox,plan.F19Origin,plan.F19Normal),detail,volume,out shape,out native);
        List<object> rows=new List<object>{new Dictionary<string,object>{{"body_id",definition["body_id"]},{"output_feature_id",definition["output_feature_id"]},{"shape",shape},{"native_geometry",native}}};
        evidence=new Dictionary<string,object>{{"status",ok?"VERIFIED":"MISMATCH"},{"seed",seedShape},{"seed_native_geometry",seedNative},
            {"instances",rows},{"oracle_bodies_in_expected_order",new List<object>{seedShape,shape}},{"seed_feature_id",plan.FeatureMirrorRequest.SeedFeatureId}};
        return ok;
    }

    private static bool F19Preserved(Part part,M3PlanState plan,Dictionary<string,object> snapshot,List<M3BodyMeasure> after,
        Dictionary<string,string> prints,Dictionary<string,Dictionary<string,object>> details,Dictionary<string,object> definition,out Dictionary<string,object> evidence)
    {
        Dictionary<string,string> oldPrints=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string tag in plan.BodyFingerprintsBefore.Keys) {string v;if(prints.TryGetValue(tag,out v))oldPrints.Add(tag,v);}
        Dictionary<string,object> bodies; bool priorBodies=F19BodyPrintsCompare(plan.BodyFingerprintsBefore,oldPrints,
            plan.F19BodyDetailsBefore,details,plan.F19OldMirrorBodyTags,out bodies);
        Dictionary<string,string> stableAfter=F18StableInventory(part,plan.F18SeedTag), stableBefore=new Dictionary<string,string>(plan.F18StableBefore,StringComparer.Ordinal);
        bool datumChildren=true;
        if(plan.F19DatumTag!=null)
        {
            stableBefore.Remove("C:"+plan.F19DatumTag); stableAfter.Remove("C:"+plan.F19DatumTag);
            DatumPlaneFeature datum=FindFeature(part,plan.FeatureMirrorRequest.DatumPlaneId) as DatumPlaneFeature;
            HashSet<string> expected=new HashSet<string>(plan.F19DatumChildrenBefore,StringComparer.Ordinal), actual=new HashSet<string>(StringComparer.Ordinal);
            MirrorFeature mirror=FindFeature(part,plan.FeatureId) as MirrorFeature;
            if(datum==null || mirror==null || datum.Tag.ToString()!=plan.F19DatumTag) datumChildren=false;
            else {expected.Add(mirror.Tag.ToString());foreach(Feature child in datum.GetChildren())actual.Add(child.Tag.ToString());datumChildren=expected.SetEquals(actual);}
        }
        Feature seed=FindFeature(part,plan.FeatureMirrorRequest.SeedFeatureId);MirrorFeature feature=FindFeature(part,plan.FeatureId) as MirrorFeature;
        Feature output=FindFeature(part,(string)definition["output_feature_id"]);
        Dictionary<string,object> expressionEvidence;bool expressions=F19ExpressionPreservation(part,plan,seed,output,stableAfter,out expressionEvidence);
        List<object> diff; bool stable=F18StableSubset(stableBefore,stableAfter,out diff);
        HashSet<string> newFeatures=new HashSet<string>(StringComparer.Ordinal);
        foreach(Feature f in part.Features) if(!plan.F18StableBefore.ContainsKey("F:"+f.Tag))newFeatures.Add(f.Tag.ToString());
        bool owned=feature!=null && seed!=null && output!=null && newFeatures.SetEquals(new string[]{feature.Tag.ToString(),output.Tag.ToString()});
        if(seed!=null && feature!=null) {Feature[] children=seed.GetChildren();owned=owned && children!=null && children.Length==1 && children[0].Tag.Equals(feature.Tag);}
        foreach(Expression e in part.Expressions) if(!plan.F18StableBefore.ContainsKey("E:"+e.Tag))owned=false;
        foreach(NXOpen.Point p in part.Points.ToArray()) if(!plan.F18StableBefore.ContainsKey("P:"+p.Tag))owned=false;
        foreach(Direction d in part.Directions.ToArray()) if(!plan.F18StableBefore.ContainsKey("D:"+d.Tag))owned=false;
        foreach(Axis a in part.Axes.ToArray()) if(!plan.F18StableBefore.ContainsKey("A:"+a.Tag))owned=false;
        bool curves=String.Equals(plan.F12CurveInventoryBefore,M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(part)),StringComparison.Ordinal);
        bool sketches=ExactProjection(plan.F12SemanticBefore,snapshot,"sketches",null) && ExactProjection(plan.F12SemanticBefore,snapshot,"native_helical_definitions",null);
        bool definitions=F19DefinitionsMatch(part,plan);bool count=after.Count==plan.BodiesBefore.Count+1;
        bool planes=String.Equals(plan.F19PlanesBefore,F19PlaneInventory(part),StringComparison.Ordinal);
        bool datums=String.Equals(plan.F19DatumsBefore,F19DatumInventory(part),StringComparison.Ordinal);
        bool ok=priorBodies && count && stable && datumChildren && owned && curves && sketches && definitions && planes && datums && expressions;
        evidence=new Dictionary<string,object>{{"status",ok?"VERIFIED":"MISMATCH"},{"prior_body_fingerprints",bodies},{"body_count_matches",count},
            {"prior_inventory",stable},{"prior_inventory_differences",diff},{"datum_children",datumChildren},{"new_feature_ownership",owned},
            {"curve_inventory",curves},{"sketches_and_helical",sketches},{"prior_native_definitions",definitions},{"plane_inventory",planes},{"datum_inventory",datums},
            {"existing_mirror_definitions",plan.F19MirrorsBefore},{"existing_pattern_definitions",plan.F18PatternsBefore},{"expression_sharing",expressionEvidence}};
        return ok;
    }

    private M3ExecutionResult ExecuteF19FeatureMirrorPlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        if(binding==null || prepared==null || plan==null || plan.FeatureMirrorRequest==null || prepared.Kind!="create_feature_mirror")
            return M3Execution("UNCHANGED",binding==null?null:binding.RevisionId,null,"INVALID_PLAN","mirror plan unavailable");
        BindingState state;try {state=RequireState(binding);} catch(M2ModelException ex){return M3Execution("UNCHANGED",binding.RevisionId,null,ex.Code,ex.Message);}
        string reason;if(!Object.ReferenceEquals(state,plan.BindingState) || prepared.RevisionId!=plan.RevisionId || !IsBindingCurrent(binding,out reason) || binding.RevisionId!=plan.RevisionId)
            return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","binding or revision changed");
        try
        {
            RequireWritableSimplePart(state.WorkPart);RequireMillimeterPartAndUnit(state.WorkPart,"mm","mm");
            if(state.Session.ActiveSketch!=null)throw new M2ModelException("NX_BUSY","end active sketch");
            SnapshotResult current=Capture(binding,state);List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy);Dictionary<string,object> ignored;
            Dictionary<string,Dictionary<string,object>> currentDetails=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            Dictionary<string,string> currentPrints=CaptureF12BodyFingerprints(state.WorkPart,bodies,currentDetails);
            if(state.WorkPart.IsModified!=plan.WorkPartModifiedBefore || plan.F18InventoryBefore!=F19Inventory(state.WorkPart) ||
                M2Json.CanonicalText(plan.F19ExpressionsBefore)!=M2Json.CanonicalText(F19ExpressionStates(state.WorkPart)) ||
                plan.SemanticBefore!=CanonicalSemanticSnapshot(current.Snapshot) || !BodyMeasuresEqual(plan.BodiesBefore,bodies,out ignored) ||
                !F19BodyPrintsCompare(plan.BodyFingerprintsBefore,currentPrints,plan.F19BodyDetailsBefore,currentDetails,plan.F19OldMirrorBodyTags,out ignored) || !F19DefinitionsMatch(state.WorkPart,plan))
                return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","mirror inputs or model state changed");
        }
        catch(Exception ex){M2ModelException unknown=F12AnalysisStateUnknownOrNull(ex,"mirror precondition cleanup failed");return unknown!=null?M3Execution("UNKNOWN",binding.RevisionId,null,unknown.Code,unknown.Message):M3Execution("UNCHANGED",binding.RevisionId,null,"MODEL_READ_FAILED","mirror precondition read failed ("+ex.GetType().Name+")");}
        Session.UndoMarkId mark;try{mark=state.Session.SetUndoMark(Session.MarkVisibility.Invisible,"F19 native feature mirror transaction");}
        catch(Exception ex){return M3Execution("UNCHANGED",binding.RevisionId,null,"UPDATE_FAILED","undo mark failed ("+ex.GetType().Name+")");}
        try
        {
            Feature seed=FindFeature(state.WorkPart,plan.FeatureMirrorRequest.SeedFeatureId);
            DatumPlaneFeature datum=plan.FeatureMirrorRequest.DatumPlaneId==null?null:FindFeature(state.WorkPart,plan.FeatureMirrorRequest.DatumPlaneId) as DatumPlaneFeature;
            MirrorFeature feature=V2FeatureMirrorFactory.Create(state.WorkPart,seed,datum==null?plan.F19Origin:null,datum==null?plan.F19Normal:null,datum);
            plan.FeatureId=RequiredJournalId(feature,"mirror feature");
            if(state.Session.UpdateManager.DoUpdate(mark)!=0)throw new M2ModelException("M3_UPDATE_FAILED","mirror update failed");
            Dictionary<string,object> definition=F19Read(state.WorkPart,feature);
            if(!F19DefinitionMatches(plan,definition))throw new M2ModelException("F19_DEFINITION_MISMATCH","native mirror differs");
            SnapshotResult after=Capture(binding,state);plan.BodiesAfter=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy);
            plan.F18BodyDetailsAfter=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            plan.BodyFingerprintsAfter=CaptureF12BodyFingerprints(state.WorkPart,plan.BodiesAfter,plan.F18BodyDetailsAfter);
            Dictionary<string,object> geometry,preservation;List<object> support;
            bool coverage=F19Geometry(state.WorkPart,plan,definition,plan.F18BodyDetailsAfter,plan.BodiesAfter,out geometry);
            bool preserved=F19Preserved(state.WorkPart,plan,after.Snapshot,plan.BodiesAfter,plan.BodyFingerprintsAfter,plan.F18BodyDetailsAfter,definition,out preservation);
            bool supportOk=ValidateM4SupportTracks(state,out support,null);
            plan.ExecutionFailureEvidence=new Dictionary<string,object>{{"native_definition",definition},{"geometry",geometry},{"model_preservation",preservation},{"support_associations",support}};
            if(!coverage || !preserved || !supportOk)throw new M2ModelException("F19_POSTCONDITION_FAILED","mirror geometry, association or preservation failed");
            if(plan.ForcePostCommitVerificationFailureForTesting)throw new M2ModelException("F19_FORCED_POST_COMMIT_FAILURE","test-only failure after mirror verification");
            plan.F19Definition=definition;plan.F19Geometry=geometry;plan.SemanticAfter=CanonicalSemanticSnapshot(after.Snapshot);
            plan.F19ExpressionsAfter=F19ExpressionStates(state.WorkPart);
            plan.GeometryAfter=CaptureFullGeometry(state.WorkPart);plan.HistoryAfter=HistoryProjection(after.Snapshot);
            Dictionary<string,object> result=new Dictionary<string,object>{{"kind","create_feature_mirror"},{"feature_id",plan.FeatureId},{"native_definition",definition},
                {"geometry",geometry},{"model_preservation",preservation},{"revision_id",binding.RevisionId}};
            plan.Execution=M3Execution("APPLIED",binding.RevisionId,result,null,null);return plan.Execution;
        }
        catch(Exception ex){Exception failure=ex is V2FeatureMirrorFactory.ReadStateUnknownException?(Exception)new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN","mirror builder cleanup failed"):ex;plan.Execution=RestoreM3State(binding,state,plan,mark,failure);return plan.Execution;}
        finally{try{state.Session.DeleteUndoMark(mark,"F19 native feature mirror transaction");}catch(Exception ex){throw M3AnalysisStateUnknown("mirror transaction mark cleanup failed",ex);}}
    }

    private M3VerificationResult VerifyF19FeatureMirrorPlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        if(binding==null || prepared==null || plan==null || plan.FeatureMirrorRequest==null || plan.Execution==null || plan.Execution.State!="APPLIED")
            return M3VerificationFailure(binding==null?null:binding.RevisionId,"fresh mirror verification requires an applied plan");
        BindingState state=RequireState(binding);string reason;
        if(!Object.ReferenceEquals(state,plan.BindingState) || !IsBindingCurrent(binding,out reason))return M3VerificationFailure(binding.RevisionId,"mirror binding changed");
        MirrorFeature feature=FindFeature(state.WorkPart,plan.FeatureId) as MirrorFeature;
        if(feature==null)return M3VerificationFailure(binding.RevisionId,"mirror feature is missing");
        Dictionary<string,object> definition=F19Read(state.WorkPart,feature);SnapshotResult capture=Capture(binding,state);
        List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy);
        Dictionary<string,Dictionary<string,object>> details=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
        Dictionary<string,string> prints=CaptureF12BodyFingerprints(state.WorkPart,bodies,details);Dictionary<string,object> geometry,preservation,bodyEvidence,printEvidence;
        bool native=F19DefinitionMatches(plan,definition) && M2Json.CanonicalText(plan.F19Definition)==M2Json.CanonicalText(definition);
        bool coverage=F19Geometry(state.WorkPart,plan,definition,details,bodies,out geometry);
        bool preserved=F19Preserved(state.WorkPart,plan,capture.Snapshot,bodies,prints,details,definition,out preservation);
        bool bodyMeasurements=BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesAfter,null),BodyMeasureRecords(bodies,null),out bodyEvidence);
        HashSet<string> allowed=new HashSet<string>(plan.F19OldMirrorBodyTags,StringComparer.Ordinal);allowed.Add((string)definition["body_tag"]);
        bool bodyPrints=F19BodyPrintsCompare(plan.BodyFingerprintsAfter,prints,plan.F18BodyDetailsAfter,details,allowed,out printEvidence);
        bool sameBodies=bodyMeasurements && bodyPrints;
        List<object> geometryDifferences=new List<object>();bool sameGeometry=F19GeometryCompare(plan.F19Geometry,geometry,"",geometryDifferences);
        bool sameExpressions=M2Json.CanonicalText(plan.F19ExpressionsAfter)==M2Json.CanonicalText(F19ExpressionStates(state.WorkPart));
        bool sameHistory=plan.SemanticAfter==CanonicalSemanticSnapshot(capture.Snapshot) && plan.GeometryAfter==CaptureFullGeometry(state.WorkPart) && SemanticValuesEqual(plan.HistoryAfter,HistoryProjection(capture.Snapshot),"history");
        List<object> support;bool supportOk=ValidateM4SupportTracks(state,out support,null);bool verified=native && coverage && preserved && sameBodies && sameGeometry && sameHistory && sameExpressions && supportOk;
        Dictionary<string,object> checks=new Dictionary<string,object>{{"mirror_definition",VerificationCheck(native?"VERIFIED":"MISMATCH",definition)},
            {"mirror_geometry",VerificationCheck(coverage?"VERIFIED":"MISMATCH",geometry)},{"model_preservation",VerificationCheck(preserved?"VERIFIED":"MISMATCH",preservation)},
            {"body_effect",VerificationCheck(coverage && preserved?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"matches",coverage && preserved}})},
            {"geometry",VerificationCheck(sameGeometry && sameExpressions?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"matches_execute",sameGeometry},{"expression_state_matches_execute",sameExpressions},{"area_and_differences",geometryDifferences}})},
            {"bodies",VerificationCheck(sameBodies?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"measurements",bodyEvidence},{"fingerprints",printEvidence}})},{"history",VerificationCheck(sameHistory?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"matches_execute",sameHistory}})},
            {"support_associations",VerificationCheck(supportOk?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"associations",support}})}};
        return new M3VerificationResult{Verified=verified,RevisionId=binding.RevisionId,Checks=checks,Result=new Dictionary<string,object>{{"kind","create_feature_mirror"},{"feature_id",plan.FeatureId},{"native_definition",definition},{"geometry",geometry}},
            ErrorCode=verified?null:"PART_VERIFICATION_FAILED",ErrorMessage=verified?null:"Fresh mirror definition, geometry or preservation differs."};
    }
}
