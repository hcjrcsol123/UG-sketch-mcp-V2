using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

public sealed partial class M2OpenModelAdapter
{
    private static string F18Inventory(Part part)
    { return F18Inventory(part, null); }

    private static string F18Inventory(Part part, string excludedCurveTag)
    { return F18Inventory(part, excludedCurveTag, true); }

    private static string F18Inventory(Part part, string excludedCurveTag, bool includeSketchSupport)
    {
        Dictionary<string, object> records = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (Expression e in part.Expressions)
            records.Add("E:" + e.Tag, new object[] { e.JournalIdentifier, e.Name, e.Type, e.RightHandSide, e.Value,
                e.Units == null ? null : e.Units.JournalIdentifier });
        foreach (KeyValuePair<string, string> row in F14CaptureHelpers(part)) records.Add(row.Key, row.Value);
        foreach (Axis axis in part.Axes.ToArray())
            records.Add("A:" + axis.Tag, new object[] { axis.JournalIdentifier,
                axis.Point == null ? null : axis.Point.Tag.ToString(), axis.Direction == null ? null : axis.Direction.Tag.ToString() });
        foreach (Feature feature in part.Features)
        {
            List<string> parents = new List<string>(); foreach (Feature p in feature.GetParents()) parents.Add(p.Tag.ToString());
            List<string> children = new List<string>(); foreach (Feature c in feature.GetChildren()) children.Add(c.Tag.ToString());
            parents.Sort(StringComparer.Ordinal);children.Sort(StringComparer.Ordinal);
            records.Add("F:" + feature.Tag, new object[] { feature.JournalIdentifier, feature.FeatureType, parents.ToArray(), children.ToArray() });
        }
        foreach (Body body in part.Bodies)
        {
            records.Add("B:"+body.Tag,new object[]{body.JournalIdentifier,body.IsSolidBody});
            foreach(Edge edge in body.GetEdges())
            {
                Point3d a,b;edge.GetVertices(out a,out b);List<string> faces=new List<string>();
                foreach(Face face in edge.GetFaces())faces.Add(face.Tag.ToString());faces.Sort(StringComparer.Ordinal);
                records.Add("G:"+edge.Tag,new object[]{body.Tag.ToString(),edge.JournalIdentifier,edge.SolidEdgeType.ToString(),F16Point(a),F16Point(b),faces.ToArray()});
            }
            foreach(Face face in body.GetFaces())
            {
                int type,norm;double radius,rd;double[] point=new double[3],direction=new double[3],bounds=new double[6];
                UFSession.GetUFSession().Modl.AskFaceData(face.Tag,out type,point,direction,bounds,out radius,out rd,out norm);
                List<string> edges=new List<string>();foreach(Edge edge in face.GetEdges())edges.Add(edge.Tag.ToString());edges.Sort(StringComparer.Ordinal);
                records.Add("S:"+face.Tag,new object[]{body.Tag.ToString(),face.JournalIdentifier,type,point,direction,bounds,radius,rd,norm,edges.ToArray()});
            }
        }
        records.Add("geometry", includeSketchSupport ? CaptureFullGeometry(part) : CaptureFullGeometryWithoutSupport(part));
        Dictionary<string, M3SketchProjectionCurveState> curves=CaptureM3SketchProjectionCurveInventory(part);
        if(excludedCurveTag!=null && !curves.Remove(excludedCurveTag))throw new M2ModelException("MODEL_READ_FAILED","excluded curve is missing from inventory");
        records.Add("curves", M3SketchProjectionCurveInventoryText(curves));
        return M2Json.CanonicalText(records);
    }

    private static Dictionary<string, object> F18Read(Part part, PatternFeature feature)
    {
        try { return V2FeaturePatternFactory.ReadIsolated(Session.GetSession(), part, feature, delegate { return F18Inventory(part); }); }
        catch (V2FeaturePatternFactory.ReadStateUnknownException ex) { throw M3AnalysisStateUnknown("feature pattern read state could not be restored", ex); }
    }

    private static void F18RequireCreateSeed(Part part, Feature seed)
    {
        Dictionary<string,object> definition;
        try {definition=V2FeaturePatternFactory.ReadSeedIsolated(Session.GetSession(),part,seed,delegate{return F18Inventory(part);});}
        catch(V2FeaturePatternFactory.ReadStateUnknownException ex){throw M3AnalysisStateUnknown("seed definition read could not restore state",ex);}
        if(!Object.Equals(definition["boolean"],"Create"))throw new M2ModelException("UNSUPPORTED_PATTERN_SEED","seed must be a native create feature");
    }

    private static Dictionary<string, object> F18CaptureDefinitions(Part part, string excluded)
    {return F18CaptureDefinitions(part,excluded,null);}

    private static Dictionary<string, object> F18CaptureDefinitions(Part part, string excluded,Dictionary<string,string> referenceTags)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        List<string> ids = new List<string>();
        foreach (Feature f in part.Features) if (f is PatternFeature && !String.Equals(f.JournalIdentifier, excluded, StringComparison.Ordinal)) ids.Add(f.JournalIdentifier);
        ids.Sort(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            PatternFeature feature = FindFeature(part, id) as PatternFeature;
            if (feature == null) throw new M2ModelException("UNSUPPORTED_PATTERN_PRESERVATION", "pattern could not be resolved uniquely");
            Dictionary<string, object> row;
            try { row = F18Read(part, feature); }
            catch (M2ModelException) { throw; }
            catch (Exception ex) { throw new M2ModelException("UNSUPPORTED_PATTERN_PRESERVATION", "pattern definition cannot be read (" + ex.GetType().Name + ")"); }
            try { F18CheckExistingPattern(part,id,row); }
            catch (M2ModelException ex) { if (ex.Code=="M3_ANALYSIS_STATE_UNKNOWN") throw; throw new M2ModelException("UNSUPPORTED_PATTERN_PRESERVATION", "existing pattern is outside the supported preservation domain"); }
            catch (Exception) { throw new M2ModelException("UNSUPPORTED_PATTERN_PRESERVATION", "existing pattern is outside the supported preservation domain"); }
            if(referenceTags!=null)
            {
                string referenceTag=row["reference_point_tag"] as string;
                if(String.IsNullOrEmpty(referenceTag))throw new M2ModelException("UNSUPPORTED_PATTERN_PRESERVATION","pattern reference point is unavailable");
                referenceTags.Add(id,referenceTag);
            }
            // Tags are session-local, and SaveCopy compares across reopen.
            List<Dictionary<string, object>> instances = (List<Dictionary<string, object>>)row["instances"];
            foreach (Dictionary<string, object> item in instances) { item.Remove("instance_tag"); item.Remove("output_feature_tag"); item.Remove("body_tag"); }
            foreach(string key in new string[]{"reference_point_tag","x_direction_tag","y_direction_tag","axis_tag","axis_point_tag","axis_direction_tag"})row.Remove(key);
            instances.Sort(delegate(Dictionary<string,object> a, Dictionary<string,object> b) { return String.CompareOrdinal((string)a["instance_id"], (string)b["instance_id"]); });
            result.Add(id, row);
        }
        return result;
    }

    private static int F18Count(object value)
    {
        double number=(double)value;
        if (!IsFinite(number) || number!=Math.Floor(number) || number<2 || number>32) throw new InvalidOperationException("unsupported instance count");
        return (int)number;
    }

    private static void F18CheckExistingPattern(Part part,string id,Dictionary<string,object> row)
    {
        bool circular=Object.Equals(row["pattern_type"],"Circular"),linear=Object.Equals(row["pattern_type"],"Linear");
        if(!circular && !linear) throw new InvalidOperationException("unsupported native pattern type");
        M3FeaturePatternRequest r=new M3FeaturePatternRequest{RevisionId="preservation",SeedFeatureId=(string)row["seed_feature_id"]};
        if(circular)
        {
            r.Mode="circular";r.Count1=F18Count(row["count"]);r.AngleDeg=(double)row["angle"];
            r.AxisOriginMm=(double[])row["axis_origin"];r.AxisDirection=(double[])row["axis_direction"];
        }
        else
        {
            r.Mode=(bool)row["use_y"]?"rectangular":"linear";r.Count1=F18Count(row["x_count"]);
            r.Pitch1Mm=(double)row["x_pitch"];r.Direction1=(double[])row["x_direction"];
            if(r.Mode=="rectangular") {r.Count2=F18Count(row["y_count"]);r.Pitch2Mm=(double)row["y_pitch"];r.Direction2=(double[])row["y_direction"];}
        }
        F18Validate(r);
        Feature seed=FindFeature(part,r.SeedFeatureId);
        if(!(seed is Block) && !(seed is Extrude))throw new InvalidOperationException("unsupported seed");
        F18RequireCreateSeed(part,seed);seed=FindFeature(part,r.SeedFeatureId);
        Body[] bodies=seed.GetBodies();if(bodies==null || bodies.Length!=1 || !bodies[0].IsSolidBody)throw new InvalidOperationException("unsupported seed body");
        double[] box;F16Preflight(bodies[0],new string[0],0,out box);
        M3PlanState probe=new M3PlanState {FeaturePatternRequest=r,FeatureId=id,F18SeedTag=seed.Tag.ToString(),F18SeedBox=box};
        Dictionary<string,object> geometry;
        if(!F18DefinitionMatches(probe,row) || !F18Coverage(part,probe,row,out geometry))throw new InvalidOperationException("native pattern or instances differ");
    }

    private static bool F18Unit(double[] vector)
    {
        if (vector == null || vector.Length != 3) return false;
        double square=0; foreach (double v in vector) { if (!IsFinite(v)) return false; square += v*v; }
        return IsFinite(square) && Math.Abs(Math.Sqrt(square)-1) <= 1e-9;
    }

    private static void F18Validate(M3FeaturePatternRequest r)
    {
        if (r == null || String.IsNullOrWhiteSpace(r.RevisionId) || r.RevisionId.Length > 128 ||
            String.IsNullOrWhiteSpace(r.SeedFeatureId) || r.SeedFeatureId.Length > 4096 || r.Count1 < 2 || r.Count1 > 32)
            throw new M2ModelException("INVALID_REQUEST", "invalid pattern request");
        if (r.Mode == "linear" || r.Mode == "rectangular")
        {
            if (!F18Unit(r.Direction1) || !IsFinite(r.Pitch1Mm) || r.Pitch1Mm <= 0)
                throw new M2ModelException("INVALID_REQUEST", "direction1 must be unit and pitch1_mm must be positive and finite");
            if (r.Mode == "rectangular")
            {
                double dot=0; if (!F18Unit(r.Direction2)) throw new M2ModelException("INVALID_REQUEST", "direction2 must be unit");
                for (int i=0;i<3;i++) dot+=r.Direction1[i]*r.Direction2[i];
                if (Math.Abs(dot)>1e-9 || !IsFinite(r.Pitch2Mm) || r.Pitch2Mm<=0 || r.Count2<2 || r.Count2>32 || r.Count1*r.Count2>32)
                    throw new M2ModelException("INVALID_REQUEST", "invalid rectangular pattern spacing, count or directions");
            }
        }
        else if (r.Mode == "circular")
        {
            if (!F18Unit(r.AxisDirection) || r.AxisOriginMm==null || r.AxisOriginMm.Length!=3 ||
                !IsFinite(r.AngleDeg) || r.AngleDeg==0 || Math.Abs(r.AngleDeg)*(r.Count1-1)>=360)
                throw new M2ModelException("INVALID_REQUEST", "invalid circular axis or signed step angle");
            foreach (double v in r.AxisOriginMm) if (!IsFinite(v)) throw new M2ModelException("INVALID_REQUEST", "axis origin must be finite");
        }
        else throw new M2ModelException("INVALID_REQUEST", "unsupported pattern mode");
    }

    private static M3FeaturePatternRequest F18CopyRequest(M3FeaturePatternRequest r)
    {
        return new M3FeaturePatternRequest {RevisionId=r.RevisionId,SeedFeatureId=r.SeedFeatureId,Mode=r.Mode,
            Count1=r.Count1,Count2=r.Count2,Pitch1Mm=r.Pitch1Mm,Pitch2Mm=r.Pitch2Mm,AngleDeg=r.AngleDeg,
            Direction1=r.Direction1==null?null:(double[])r.Direction1.Clone(),Direction2=r.Direction2==null?null:(double[])r.Direction2.Clone(),
            AxisOriginMm=r.AxisOriginMm==null?null:(double[])r.AxisOriginMm.Clone(),AxisDirection=r.AxisDirection==null?null:(double[])r.AxisDirection.Clone()};
    }

    private static Dictionary<string,string> F18StableInventory(Part part,string seedTag)
    {
        Dictionary<string,string> result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(NXOpen.Point p in part.Points.ToArray())
        {
            // NX may renumber the ENTITY journal identifier when a second
            // reference point is inserted; the same-session Tag and coordinates
            // remain the identity/geometry, and pattern reference coordinates are
            // independently captured in the native definition.
            Point3d v=p.Coordinates;result.Add("P:"+p.Tag,M2Json.CanonicalText(new object[]{v.X,v.Y,v.Z}));
        }
        foreach(Direction d in part.Directions.ToArray())
        {
            Point3d p=d.Origin;Vector3d v=d.Vector;result.Add("D:"+d.Tag,M2Json.CanonicalText(new object[]{d.JournalIdentifier,p.X,p.Y,p.Z,v.X,v.Y,v.Z}));
        }
        foreach(Expression e in part.Expressions) result.Add("E:"+e.Tag,M2Json.CanonicalText(new object[]{e.JournalIdentifier,e.Name,e.Type,e.RightHandSide,e.Value,e.Units==null?null:e.Units.JournalIdentifier}));
        foreach(Axis a in part.Axes.ToArray()) result.Add("A:"+a.Tag,M2Json.CanonicalText(new object[]{a.JournalIdentifier,a.Point==null?null:a.Point.Tag.ToString(),a.Direction==null?null:a.Direction.Tag.ToString()}));
        foreach(Feature f in part.Features)
        {
            List<string> parents=new List<string>();foreach(Feature p in f.GetParents())parents.Add(p.Tag.ToString());parents.Sort(StringComparer.Ordinal);
            result.Add("F:"+f.Tag,M2Json.CanonicalText(new object[]{f.JournalIdentifier,f.FeatureType,parents.ToArray()}));
            if(f.Tag.ToString()!=seedTag) {List<string> children=new List<string>();foreach(Feature child in f.GetChildren())children.Add(child.Tag.ToString());children.Sort(StringComparer.Ordinal);result.Add("C:"+f.Tag,M2Json.CanonicalText(children.ToArray()));}
        }
        return result;
    }

    private static bool F18StableSubset(Dictionary<string,string> before,Dictionary<string,string> after,out List<object> differences)
    {
        differences=new List<object>();
        foreach(KeyValuePair<string,string> row in before)
        {
            string value;bool found=after.TryGetValue(row.Key,out value);
            if(!found || value!=row.Value)differences.Add(new Dictionary<string,object>{{"key",row.Key},{"before",row.Value},{"after",found?value:null}});
        }
        return differences.Count==0;
    }

    public M3PlanSnapshot PrepareFeaturePattern(M2ModelBinding binding, M3FeaturePatternRequest source)
    {
        F18Validate(source);
        BindingState state=RequireState(binding);bool dirtyBefore=state.WorkPart.IsModified;string inventoryBefore;
        try {inventoryBefore=F18Inventory(state.WorkPart);if(state.WorkPart.IsModified!=dirtyBefore)throw new InvalidOperationException("initial inventory changed dirty state");}
        catch(Exception ex){throw M3AnalysisStateUnknown("feature pattern initial inventory could not be proven read-only",ex);}
        RequireWritableSimplePart(state.WorkPart);RequireMillimeterPartAndUnit(state.WorkPart,"mm","mm");
        if(state.Session.ActiveSketch!=null)throw new M2ModelException("NX_BUSY","end the active sketch before preparing a feature pattern");
        SnapshotResult capture=Capture(binding,state);
        if(!String.Equals(source.RevisionId,binding.RevisionId,StringComparison.Ordinal))throw new M2ModelException("STALE_REVISION","revision changed");
        Feature seed=FindFeature(state.WorkPart,source.SeedFeatureId);
        if(!(seed is Block) && !(seed is Extrude)) throw new M2ModelException("UNSUPPORTED_PATTERN_SEED","seed must be a native block or extrude");
        F18RequireCreateSeed(state.WorkPart,seed);seed=FindFeature(state.WorkPart,source.SeedFeatureId);
        Body[] bodies=seed.GetBodies(); Feature[] children=seed.GetChildren();
        if(bodies==null || bodies.Length!=1 || bodies[0]==null || !bodies[0].IsSolidBody || children==null || children.Length!=0)
            throw new M2ModelException("UNSUPPORTED_PATTERN_SEED","seed must own one unmodified solid without downstream features");
        double[] box;F16Preflight(bodies[0],new string[0],0,out box);
        List<double[][]> expected=F18Expected(box,source);F18PreflightClearance(state.WorkPart,bodies[0],expected);
        M3PlanState plan=CreateM3PlanState(binding,state,"create_feature_pattern",capture,F12MassPropertyAccuracy);
        plan.FeaturePatternRequest=F18CopyRequest(source);plan.TargetBody=bodies[0];plan.TargetBodyId=RequiredJournalId(bodies[0],"seed body");
        plan.F18SeedTag=seed.Tag.ToString();plan.F18SeedBox=box;plan.F12SemanticBefore=capture.Snapshot;
        plan.F12CurveInventoryBefore=M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart));
        plan.HoleHelpersBefore=F14CaptureHelpers(state.WorkPart);plan.HoleDefinitionsBefore=F14CapturePersistentHoleDefinitions(state.WorkPart,null);
        plan.BooleanDefinitionsBefore=F15CaptureDefinitions(state.WorkPart,null);plan.EdgeBlendDefinitionsBefore=F16CaptureDefinitions(state.WorkPart,null);
        plan.ChamferDefinitionsBefore=F17CaptureDefinitions(state.WorkPart,null);
        plan.F18PatternReferenceTagsBefore=new Dictionary<string,string>(StringComparer.Ordinal);
        plan.F18PatternsBefore=F18CaptureDefinitions(state.WorkPart,null,plan.F18PatternReferenceTagsBefore);
        plan.F18StableBefore=F18StableInventory(state.WorkPart,plan.F18SeedTag);plan.F18InventoryBefore=inventoryBefore;
        try {plan.BodyFingerprintsBefore=CaptureF12BodyFingerprints(state.WorkPart,plan.BodiesBefore);}
        catch(Exception ex){ThrowIfF12AnalysisStateUnknown(ex,"feature pattern prepare fingerprint cleanup failed");throw;}
        string inventoryAfter=F18Inventory(state.WorkPart);
        if(state.WorkPart.IsModified!=dirtyBefore || plan.WorkPartModifiedBefore!=dirtyBefore || !String.Equals(inventoryBefore,inventoryAfter,StringComparison.Ordinal))
            throw M3AnalysisStateUnknown("feature pattern prepare changed the captured model state",null);
        Dictionary<string,object> view=new Dictionary<string,object>{{"kind","create_feature_pattern"},{"revision_id",binding.RevisionId},
            {"seed_feature_id",source.SeedFeatureId},{"seed_body_id",plan.TargetBodyId},{"mode",source.Mode},
            {"count1",source.Count1},{"count2",source.Mode=="rectangular"?(object)source.Count2:null},
            {"pitch1_mm",source.Mode=="circular"?null:(object)source.Pitch1Mm},{"pitch2_mm",source.Mode=="rectangular"?(object)source.Pitch2Mm:null},
            {"direction1",source.Direction1},{"direction2",source.Direction2},{"axis_origin_mm",source.AxisOriginMm},{"axis_direction",source.AxisDirection},
            {"angle_deg",source.Mode=="circular"?(object)source.AngleDeg:null},{"seed_bounds_mm",box},{"count_includes_seed",true}};
        return new M3PlanSnapshot{Kind="create_feature_pattern",RevisionId=binding.RevisionId,PublicView=view,AdapterState=plan};
    }

    private static bool F18VectorMatches(object value,double[] expected)
    {
        double[] actual=value as double[];if(actual==null || expected==null || actual.Length!=3)return false;
        for(int i=0;i<3;i++)if(Math.Abs(actual[i]-expected[i])>1e-8)return false;return true;
    }

    private static bool F18DefinitionMatches(M3PlanState plan,Dictionary<string,object> d)
    {
        M3FeaturePatternRequest r=plan.FeaturePatternRequest;
        if(d==null || !Object.Equals(d["feature_id"],plan.FeatureId) || !Object.Equals(d["seed_feature_id"],r.SeedFeatureId) ||
            !Object.Equals(d["method"],"Simple") || !Object.Equals(d["output_option"],"PatternFeature") ||
            !Object.Equals(d["expression_option"],"CreateNew") ||
            !Object.Equals(d["seed_only"],false) || !Object.Equals(d["frame_only"],false) || !Object.Equals(d["stagger"],"None") ||
            !Object.Equals(d["reference_inferred"],true) || !(d["reference_point_mm"] is double[])) return false;
        if(r.Mode=="circular") return Object.Equals(d["pattern_type"],"Circular") && Object.Equals(d["orientation"],"FollowPattern") && Object.Equals(d["include_seed"],true) &&
            Object.Equals(d["flip"],false) && Object.Equals(d["use_radial"],false) && Object.Equals(d["space_type"],"Offset") &&
            Object.Equals(d["pitch_option"],"Angle") && F16Near((double)d["count"],r.Count1) && F16Near((double)d["angle"],r.AngleDeg) &&
            F18ExpressionUnits(d["count_expression"],null) && F18ExpressionUnits(d["angle_expression"],"Degrees") &&
            F18VectorMatches(d["axis_origin"],r.AxisOriginMm) && F18VectorMatches(d["axis_direction"],r.AxisDirection);
        bool ok=Object.Equals(d["pattern_type"],"Linear") && Object.Equals(d["orientation"],"Fixed") && Object.Equals(d["use_y"],r.Mode=="rectangular") &&
            Object.Equals(d["x_flip"],false) && Object.Equals(d["x_symmetry"],false) && Object.Equals(d["x_space_type"],"Offset") &&
            F18ExpressionUnits(d["x_count_expression"],null) && F18ExpressionUnits(d["x_pitch_expression"],"MilliMeter") &&
            F16Near((double)d["x_count"],r.Count1) && F16Near((double)d["x_pitch"],r.Pitch1Mm) && F18VectorMatches(d["x_direction"],r.Direction1);
        if(r.Mode=="rectangular")ok=ok && Object.Equals(d["y_flip"],false) && Object.Equals(d["y_symmetry"],false) && Object.Equals(d["y_space_type"],"Offset") &&
            F18ExpressionUnits(d["y_count_expression"],null) && F18ExpressionUnits(d["y_pitch_expression"],"MilliMeter") &&
            F16Near((double)d["y_count"],r.Count2) && F16Near((double)d["y_pitch"],r.Pitch2Mm) && F18VectorMatches(d["y_direction"],r.Direction2);
        return ok;
    }

    private static bool F18ExpressionUnits(object value,string expected)
    {Dictionary<string,object> e=value as Dictionary<string,object>;return e!=null && e.ContainsKey("units") && Object.Equals(e["units"],expected);}

    private static bool F18RelatedDefinitionsMatch(Part part,M3PlanState plan,string excluded)
    {
        Dictionary<string,string> currentReferences=new Dictionary<string,string>(StringComparer.Ordinal);
        Dictionary<string,object> currentPatterns=F18CaptureDefinitions(part,excluded,currentReferences);
        bool references=plan.F18PatternReferenceTagsBefore!=null && plan.F18PatternReferenceTagsBefore.Count==currentReferences.Count;
        if(references)foreach(KeyValuePair<string,string> row in plan.F18PatternReferenceTagsBefore)
        {string tag;if(!currentReferences.TryGetValue(row.Key,out tag) || tag!=row.Value)references=false;}
        return references && F14PersistentHoleDefinitionsEqual(plan.HoleDefinitionsBefore,F14CapturePersistentHoleDefinitions(part,null)) &&
            F15DefinitionsEqual(plan.BooleanDefinitionsBefore,F15CaptureDefinitions(part,null)) &&
            F16DefinitionsEqual(plan.EdgeBlendDefinitionsBefore,F16CaptureDefinitions(part,null)) &&
            F16DefinitionsEqual(plan.ChamferDefinitionsBefore,F17CaptureDefinitions(part,null)) &&
            F16DefinitionsEqual(plan.F18PatternsBefore,currentPatterns);
    }

    private static bool F18Coverage(Part part,M3PlanState plan,Dictionary<string,object> definition,out Dictionary<string,object> evidence)
    {return F18Coverage(part,plan,definition,null,null,out evidence);}

    private static bool F18Coverage(Part part,M3PlanState plan,Dictionary<string,object> definition,
        Dictionary<string,Dictionary<string,object>> details,List<M3BodyMeasure> measures,out Dictionary<string,object> evidence)
    {
        List<double[][]> expected=F18Expected(plan.F18SeedBox,plan.FeaturePatternRequest);
        List<Dictionary<string,object>> instances=definition["instances"] as List<Dictionary<string,object>>;
        List<object> rows=new List<object>();bool ok=instances!=null && instances.Count==expected.Count-1;
        Feature seed=FindFeature(part,plan.FeaturePatternRequest.SeedFeatureId);Body[] seedBodies=seed==null?null:seed.GetBodies();
        Dictionary<string,object> seedShape=null,seedNative=null;
        if(seed==null || seed.Tag.ToString()!=plan.F18SeedTag || seedBodies==null || seedBodies.Length!=1 || !F18MeasuredShape(seedBodies[0],expected[0],details,measures,out seedShape,out seedNative))ok=false;
        HashSet<string> used=new HashSet<string>(StringComparer.Ordinal);List<object> shapes=new List<object>();shapes.Add(seedShape);
        for(int n=1;n<expected.Count;n++)
        {
            int matches=0;Dictionary<string,object> chosen=null,shape=null;
            if(instances!=null) foreach(Dictionary<string,object> row in instances)
            {
                string bodyId=row["body_id"] as string;Body body=FindBody(part,bodyId);Dictionary<string,object> candidate;
                if(body!=null && !used.Contains(body.Tag.ToString()) && F18BodyMatches(body,expected[n],out candidate)) {matches++;chosen=row;shape=candidate;}
            }
            if(matches!=1) {ok=false;rows.Add(new Dictionary<string,object>{{"expected_index",n},{"matches",matches}});shapes.Add(null);continue;}
            Body chosenBody=FindBody(part,(string)chosen["body_id"]);used.Add(chosenBody.Tag.ToString());
            Dictionary<string,object> native;
            if(!F18MeasuredShape(chosenBody,expected[n],details,measures,out shape,out native))ok=false;
            int ny=plan.FeaturePatternRequest.Mode=="rectangular"?plan.FeaturePatternRequest.Count2:1;
            rows.Add(new Dictionary<string,object>{{"expected_index",n},{"index1",n/ny},{"index2",n%ny},
                {"instance_id",chosen["instance_id"]},{"output_feature_id",chosen["output_feature_id"]},{"body_id",chosen["body_id"]},{"shape",shape},{"native_geometry",native}});
            shapes.Add(shape);
        }
        if(instances==null || used.Count!=instances.Count)ok=false;
        evidence=new Dictionary<string,object>{{"status",ok?"VERIFIED":"MISMATCH"},{"seed",seedShape},{"seed_native_geometry",seedNative},{"instances",rows},
            {"oracle_bodies_in_expected_order",shapes},{"count_includes_seed",true},{"seed_feature_id",plan.FeaturePatternRequest.SeedFeatureId}};
        return ok;
    }

    private static bool F18MeasuredShape(Body body,double[][] expected,Dictionary<string,Dictionary<string,object>> details,
        List<M3BodyMeasure> measures,out Dictionary<string,object> shape,out Dictionary<string,object> native)
    {
        Dictionary<string,object> detail=null;double? volume=null;
        if(details!=null && !details.TryGetValue(body.Tag.ToString(),out detail)){shape=null;native=null;return false;}
        if(measures!=null)
        {
            foreach(M3BodyMeasure m in measures)if(m.Body.Tag.Equals(body.Tag))volume=m.VolumeMm3;
            if(!volume.HasValue){shape=null;native=null;return false;}
        }
        return F18BodyMatches(body,expected,detail,volume,out shape,out native);
    }

    private static bool F18AllowedNewObjects(Part part,M3PlanState plan,Dictionary<string,object> definition,out Dictionary<string,object> evidence)
    {
        PatternFeature pattern=FindFeature(part,plan.FeatureId) as PatternFeature;Feature seed=FindFeature(part,plan.FeaturePatternRequest.SeedFeatureId);
        List<string> violations=new List<string>();HashSet<string> permittedFeatures=new HashSet<string>(StringComparer.Ordinal);
        if(pattern==null || seed==null) {evidence=new Dictionary<string,object>{{"violations",new string[]{"missing pattern or seed"}}};return false;}
        permittedFeatures.Add(pattern.Tag.ToString());
        HashSet<string> expectedContained=new HashSet<string>(StringComparer.Ordinal),actualContained=new HashSet<string>(StringComparer.Ordinal);
        List<Dictionary<string,object>> instanceRows=definition["instances"] as List<Dictionary<string,object>>;
        if(instanceRows==null)violations.Add("missing instance ownership records");
        else foreach(Dictionary<string,object> row in instanceRows)
        {
            string instanceTag=row["instance_tag"] as string,outputTag=row["output_feature_tag"] as string;
            if(String.IsNullOrEmpty(instanceTag) || String.IsNullOrEmpty(outputTag) || !expectedContained.Add(instanceTag) || !expectedContained.Add(outputTag))violations.Add("invalid instance/output identity");
        }
        foreach(Feature child in pattern.GetAllContainedFeatures())
        {
            if(child==null || !permittedFeatures.Add(child.Tag.ToString())){violations.Add("invalid contained feature");continue;}
            actualContained.Add(child.Tag.ToString());
            Feature container=child.ContainerFeature as Feature;
            Feature outer=container==null?null:container.ContainerFeature as Feature;
            if(container==null || !(container.Tag.Equals(pattern.Tag) || (outer!=null && outer.Tag.Equals(pattern.Tag))))
                violations.Add("feature container is not the new pattern: "+child.JournalIdentifier);
        }
        if(!actualContained.SetEquals(expectedContained))violations.Add("pattern contained features differ from the enumerated instances and outputs");
        int masters=0;
        foreach(Feature f in part.Features)
        {
            if(plan.F18StableBefore.ContainsKey("F:"+f.Tag))continue;
            Feature container=f.ContainerFeature as Feature;
            if(f.FeatureType=="MASTER SNAPSHOT BODY" && container!=null && container.Tag.Equals(pattern.Tag)) {masters++;permittedFeatures.Add(f.Tag.ToString());}
        }
        if(masters!=1)violations.Add("expected one pattern-owned master snapshot");
        HashSet<string> actualNewFeatures=new HashSet<string>(StringComparer.Ordinal);
        foreach(Feature f in part.Features)if(!plan.F18StableBefore.ContainsKey("F:"+f.Tag))actualNewFeatures.Add(f.Tag.ToString());
        if(!permittedFeatures.SetEquals(actualNewFeatures))violations.Add("new feature set differs from pattern containment");
        HashSet<string> seedChildren=new HashSet<string>(StringComparer.Ordinal);foreach(Feature child in seed.GetChildren())seedChildren.Add(child.Tag.ToString());
        if(!seedChildren.SetEquals(permittedFeatures))violations.Add("seed children differ from new pattern-owned features");
        HashSet<string> expressionTags=new HashSet<string>(StringComparer.Ordinal);
        foreach(Expression e in part.Expressions)
        {
            expressionTags.Add(e.Tag.ToString());if(plan.F18StableBefore.ContainsKey("E:"+e.Tag))continue;
            Feature owner=e.GetOwningFeature();Feature[] users=e.GetUsingFeatures();
            if(owner==null || !permittedFeatures.Contains(owner.Tag.ToString()) || users==null || users.Length==0)
                {violations.Add("new expression lacks a pattern owner/user: "+e.JournalIdentifier);continue;}
            foreach(Feature user in users)if(user==null || !permittedFeatures.Contains(user.Tag.ToString()))violations.Add("new expression has an unrelated user: "+e.JournalIdentifier);
        }
        foreach(Expression e in pattern.GetExpressions())
            if(e==null || !expressionTags.Contains(e.Tag.ToString()) || e.GetOwningFeature()==null || !permittedFeatures.Contains(e.GetOwningFeature().Tag.ToString()))
                violations.Add("listed pattern expression lacks a permitted owner");
        HashSet<string> helpers=new HashSet<string>(StringComparer.Ordinal);
        foreach(string key in new string[]{"reference_point_tag","x_direction_tag","y_direction_tag","axis_tag","axis_point_tag","axis_direction_tag"})
            if(definition.ContainsKey(key) && definition[key] is string)helpers.Add((string)definition[key]);
        foreach(NXOpen.Point p in part.Points.ToArray())if(!plan.F18StableBefore.ContainsKey("P:"+p.Tag) && !helpers.Contains(p.Tag.ToString()))violations.Add("unreferenced new point: "+p.JournalIdentifier);
        foreach(Direction d in part.Directions.ToArray())if(!plan.F18StableBefore.ContainsKey("D:"+d.Tag) && !helpers.Contains(d.Tag.ToString()))violations.Add("unreferenced new direction: "+d.JournalIdentifier);
        foreach(Axis a in part.Axes.ToArray())if(!plan.F18StableBefore.ContainsKey("A:"+a.Tag) && !helpers.Contains(a.Tag.ToString()))violations.Add("unreferenced new axis: "+a.JournalIdentifier);
        evidence=new Dictionary<string,object>{{"status",violations.Count==0?"VERIFIED":"MISMATCH"},{"violations",violations},
            {"pattern_owned_feature_tags",new List<string>(permittedFeatures)},{"builder_referenced_helper_tags",new List<string>(helpers)}};
        return violations.Count==0;
    }

    private static bool F18Preserved(Part part,M3PlanState plan,Dictionary<string,object> snapshot,List<M3BodyMeasure> after,
        Dictionary<string,string> afterPrints,Dictionary<string,object> definition,out Dictionary<string,object> evidence)
    {
        Dictionary<string,string> oldAfter=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string tag in plan.BodyFingerprintsBefore.Keys) {string value;if(afterPrints.TryGetValue(tag,out value))oldAfter.Add(tag,value);}
        Dictionary<string,object> bodyComparison;bool bodies=F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore,oldAfter,out bodyComparison);
        int expectedCount=plan.FeaturePatternRequest.Count1*(plan.FeaturePatternRequest.Mode=="rectangular"?plan.FeaturePatternRequest.Count2:1);
        bool count=after.Count==plan.BodiesBefore.Count+expectedCount-1;
        List<object> stableDifferences;
        bool stable=F18StableSubset(plan.F18StableBefore,F18StableInventory(part,plan.F18SeedTag),out stableDifferences);
        bool curves=String.Equals(plan.F12CurveInventoryBefore,M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(part)),StringComparison.Ordinal);
        bool sketches=ExactProjection(plan.F12SemanticBefore,snapshot,"sketches",null) && ExactProjection(plan.F12SemanticBefore,snapshot,"native_helical_definitions",null);
        bool definitions=F18RelatedDefinitionsMatch(part,plan,plan.FeatureId);
        Dictionary<string,object> newObjects;bool owned=F18AllowedNewObjects(part,plan,definition,out newObjects);
        evidence=new Dictionary<string,object>{{"status",bodies && count && stable && curves && sketches && definitions && owned?"VERIFIED":"MISMATCH"},
            {"prior_body_fingerprints",bodyComparison},{"body_count_matches",count},{"prior_expressions_helpers_feature_parents",stable},{"prior_inventory_differences",stableDifferences},
            {"curve_inventory",curves},{"sketches_and_helical",sketches},{"prior_native_definitions",definitions},{"new_native_ownership",newObjects}};
        return bodies && count && stable && curves && sketches && definitions && owned;
    }

    private static V2FeaturePatternFactory.Definition F18NativeRequest(M3FeaturePatternRequest r)
    {
        return new V2FeaturePatternFactory.Definition{Mode=r.Mode,Count1=r.Count1,Count2=r.Count2,Pitch1=r.Pitch1Mm,Pitch2=r.Pitch2Mm,
            AngleDeg=r.AngleDeg,Direction1=r.Direction1,Direction2=r.Direction2,AxisOrigin=r.AxisOriginMm,AxisDirection=r.AxisDirection};
    }

    private M3ExecutionResult ExecuteF18FeaturePatternPlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        if(binding==null || prepared==null || plan==null || plan.FeaturePatternRequest==null || prepared.Kind!="create_feature_pattern")
            return M3Execution("UNCHANGED",binding==null?null:binding.RevisionId,null,"INVALID_PLAN","pattern plan unavailable");
        BindingState state;try{state=RequireState(binding);}catch(M2ModelException ex){return M3Execution("UNCHANGED",binding.RevisionId,null,ex.Code,ex.Message);}
        string reason;if(!Object.ReferenceEquals(state,plan.BindingState) || !String.Equals(prepared.RevisionId,plan.RevisionId,StringComparison.Ordinal) ||
            !IsBindingCurrent(binding,out reason) || !String.Equals(binding.RevisionId,plan.RevisionId,StringComparison.Ordinal))
            return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","binding or revision changed");
        try
        {
            RequireWritableSimplePart(state.WorkPart);RequireMillimeterPartAndUnit(state.WorkPart,"mm","mm");
            if(state.Session.ActiveSketch!=null)throw new M2ModelException("NX_BUSY","end the active sketch");
            SnapshotResult current=Capture(binding,state);List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy);Dictionary<string,object> ignored;
            if(state.WorkPart.IsModified!=plan.WorkPartModifiedBefore || !String.Equals(plan.F18InventoryBefore,F18Inventory(state.WorkPart),StringComparison.Ordinal) ||
                !String.Equals(plan.SemanticBefore,CanonicalSemanticSnapshot(current.Snapshot),StringComparison.Ordinal) ||
                !BodyMeasuresEqual(plan.BodiesBefore,bodies,out ignored) || !F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore,CaptureF12BodyFingerprints(state.WorkPart,bodies),out ignored) ||
                !F18RelatedDefinitionsMatch(state.WorkPart,plan,null))
                return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","pattern inputs or model state changed");
        }
        catch(Exception ex){M2ModelException unknown=F12AnalysisStateUnknownOrNull(ex,"pattern precondition cleanup failed");return unknown!=null?M3Execution("UNKNOWN",binding.RevisionId,null,unknown.Code,unknown.Message):M3Execution("UNCHANGED",binding.RevisionId,null,"MODEL_READ_FAILED","pattern precondition read failed ("+ex.GetType().Name+")");}
        Session.UndoMarkId mark;try{mark=state.Session.SetUndoMark(Session.MarkVisibility.Invisible,"F18 native feature pattern transaction");}
        catch(Exception ex){return M3Execution("UNCHANGED",binding.RevisionId,null,"UPDATE_FAILED","undo mark failed ("+ex.GetType().Name+")");}
        try
        {
            Feature seed=FindFeature(state.WorkPart,plan.FeaturePatternRequest.SeedFeatureId);
            PatternFeature feature=V2FeaturePatternFactory.Create(state.WorkPart,seed,F18NativeRequest(plan.FeaturePatternRequest));
            plan.FeatureId=RequiredJournalId(feature,"pattern feature");
            if(state.Session.UpdateManager.DoUpdate(mark)!=0)throw new M2ModelException("M3_UPDATE_FAILED","pattern update failed");
            Dictionary<string,object> definition=F18Read(state.WorkPart,feature);
            feature=FindFeature(state.WorkPart,plan.FeatureId) as PatternFeature;
            if(feature==null || !F18DefinitionMatches(plan,definition))throw new M2ModelException("F18_DEFINITION_MISMATCH","native pattern definition differs");
            SnapshotResult after=Capture(binding,state);plan.BodiesAfter=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy);
            plan.F18BodyDetailsAfter=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
            plan.BodyFingerprintsAfter=CaptureF12BodyFingerprints(state.WorkPart,plan.BodiesAfter,plan.F18BodyDetailsAfter);
            Dictionary<string,object> geometry,preservation;List<object> support;
            bool coverage=F18Coverage(state.WorkPart,plan,definition,plan.F18BodyDetailsAfter,plan.BodiesAfter,out geometry);
            bool preserved=F18Preserved(state.WorkPart,plan,after.Snapshot,plan.BodiesAfter,plan.BodyFingerprintsAfter,definition,out preservation);
            bool supportOk=ValidateM4SupportTracks(state,out support,null);
            if(!coverage || !preserved || !supportOk) {plan.ExecutionFailureEvidence=new Dictionary<string,object>{{"geometry",geometry},{"preservation",preservation},{"support",support}};throw new M2ModelException("F18_POSTCONDITION_FAILED","pattern geometry, association or preservation failed");}
            plan.ExecutionFailureEvidence=new Dictionary<string,object>{{"native_definition",definition},{"geometry",geometry},
                {"model_preservation",preservation},{"support_associations",support}};
            if(plan.ForcePostCommitVerificationFailureForTesting)throw new M2ModelException("F18_FORCED_POST_COMMIT_FAILURE","test-only failure after pattern verification");
            plan.F18Definition=definition;plan.F18Geometry=geometry;plan.SemanticAfter=CanonicalSemanticSnapshot(after.Snapshot);
            plan.GeometryAfter=CaptureFullGeometry(state.WorkPart);plan.HistoryAfter=HistoryProjection(after.Snapshot);
            Dictionary<string,object> result=new Dictionary<string,object>{{"kind","create_feature_pattern"},{"feature_id",plan.FeatureId},
                {"native_definition",definition},{"geometry",geometry},{"model_preservation",preservation},{"revision_id",binding.RevisionId}};
            plan.Execution=M3Execution("APPLIED",binding.RevisionId,result,null,null);return plan.Execution;
        }
        catch(Exception ex)
        {
            Exception failure=ex is V2FeaturePatternFactory.ReadStateUnknownException?(Exception)new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN","pattern builder cleanup failed"):ex;
            plan.Execution=RestoreM3State(binding,state,plan,mark,failure);return plan.Execution;
        }
        finally
        {
            try {state.Session.DeleteUndoMark(mark, "F18 native feature pattern transaction");}
            catch(Exception ex) {throw M3AnalysisStateUnknown("feature pattern transaction mark cleanup failed",ex);}
        }
    }

    private M3VerificationResult VerifyF18FeaturePatternPlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        if(binding==null || prepared==null || plan==null || plan.FeaturePatternRequest==null || plan.Execution==null || plan.Execution.State!="APPLIED")
            return M3VerificationFailure(binding==null?null:binding.RevisionId,"fresh pattern verification requires an applied plan");
        BindingState state=RequireState(binding);string reason;
        if(!Object.ReferenceEquals(state,plan.BindingState) || !IsBindingCurrent(binding,out reason))return M3VerificationFailure(binding.RevisionId,"pattern binding changed");
        PatternFeature feature=FindFeature(state.WorkPart,plan.FeatureId) as PatternFeature;
        if(feature==null)return M3VerificationFailure(binding.RevisionId,"pattern feature is missing");
        Dictionary<string,object> definition=F18Read(state.WorkPart,feature);
        SnapshotResult capture=Capture(binding,state);List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy);
        Dictionary<string,Dictionary<string,object>> details=new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
        Dictionary<string,string> prints=CaptureF12BodyFingerprints(state.WorkPart,bodies,details);Dictionary<string,object> geometry,preservation,bodyEvidence,printEvidence;
        bool native=F18DefinitionMatches(plan,definition) && String.Equals(M2Json.CanonicalText(plan.F18Definition),M2Json.CanonicalText(definition),StringComparison.Ordinal);
        bool coverage=F18Coverage(state.WorkPart,plan,definition,details,bodies,out geometry);
        bool preserved=F18Preserved(state.WorkPart,plan,capture.Snapshot,bodies,prints,definition,out preservation);
        bool sameBodies=BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesAfter,null),BodyMeasureRecords(bodies,null),out bodyEvidence) &&
            F12BodyFingerprintsEqual(plan.BodyFingerprintsAfter,prints,out printEvidence);
        bool sameGeometry=String.Equals(M2Json.CanonicalText(plan.F18Geometry),M2Json.CanonicalText(geometry),StringComparison.Ordinal);
        bool sameHistory=String.Equals(plan.SemanticAfter,CanonicalSemanticSnapshot(capture.Snapshot),StringComparison.Ordinal) &&
            String.Equals(plan.GeometryAfter,CaptureFullGeometry(state.WorkPart),StringComparison.Ordinal) && SemanticValuesEqual(plan.HistoryAfter,HistoryProjection(capture.Snapshot),"history");
        List<object> support;bool supportOk=ValidateM4SupportTracks(state,out support,null);
        bool verified=native && coverage && preserved && sameBodies && sameGeometry && sameHistory && supportOk;
        Dictionary<string,object> checks=new Dictionary<string,object>{
            {"pattern_definition",VerificationCheck(native?"VERIFIED":"MISMATCH",definition)},
            {"pattern_instances",VerificationCheck(coverage?"VERIFIED":"MISMATCH",geometry)},
            {"model_preservation",VerificationCheck(preserved?"VERIFIED":"MISMATCH",preservation)},
            {"body_effect",VerificationCheck(coverage && preserved?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"count_and_instances",coverage && preserved}})},
            {"geometry",VerificationCheck(sameGeometry?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"matches_execute",sameGeometry}})},
            {"bodies",VerificationCheck(sameBodies?"VERIFIED":"MISMATCH",bodyEvidence)},
            {"history",VerificationCheck(sameHistory?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"matches_execute",sameHistory}})},
            {"support_associations",VerificationCheck(supportOk?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"associations",support}})}};
        return new M3VerificationResult{Verified=verified,RevisionId=binding.RevisionId,Checks=checks,
            Result=new Dictionary<string,object>{{"kind","create_feature_pattern"},{"feature_id",plan.FeatureId},{"native_definition",definition},{"geometry",geometry}},
            ErrorCode=verified?null:"PART_VERIFICATION_FAILED",ErrorMessage=verified?null:"Fresh pattern definition, geometry or preservation differs."};
    }
}
