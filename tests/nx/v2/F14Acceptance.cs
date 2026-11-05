// F14 production-adapter native diagnostic harness. Compile offline; execute
// only from the root-owned NX journal. This partial matrix is NOT acceptance.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

public static class F14Acceptance
{
    const string Root = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    static Dictionary<string,object> report;
    static List<object> cases;
    static string reportPath;
    static bool stop;
    static bool persistenceFailed;
    static Session session;
    static Part part;
    static M2OpenModelAdapter adapter;
    static M2ModelBinding binding;
    static List<string> expectedCases;

    static void Persist()
    {
        if (persistenceFailed) return;
        try { File.WriteAllText(reportPath, M2Json.CanonicalText(report), new UTF8Encoding(false)); }
        catch (Exception ex) {
            stop=true; persistenceFailed=true;
            try { File.WriteAllText(reportPath+".persistence-failure.json",M2Json.CanonicalText(new Dictionary<string,object> {
                {"status","UNKNOWN"},{"failure_type",ex.GetType().FullName},{"report_path",reportPath},
                {"writes_halted",true},{"further_nx_calls_permitted",false},{"report_may_be_partial",true} }),new UTF8Encoding(false)); }
            catch { }
            throw;
        }
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static double Abs(double x) { return Math.Abs(x); }
    static void Near(double actual, double expected, double tolerance, string label)
    { Require(!Double.IsNaN(actual) && !Double.IsInfinity(actual) && Abs(actual-expected) <= tolerance,
        label + ": " + actual.ToString("R",CultureInfo.InvariantCulture) + " versus " + expected.ToString("R",CultureInfo.InvariantCulture)); }

    public static void Main(string[] args)
    {
        string run = Guid.NewGuid().ToString("N");
        string model = Path.Combine(Root, "workspace", "v2", "models", "F14-native-" + run + ".prt");
        reportPath = Path.Combine(Root, "workspace", "v2", "evidence", "F14-native-partial-" + run + ".json");
        cases = new List<object>();
        report = new Dictionary<string, object> {
            {"scope", "F14 production-adapter partial geometry diagnostics; not complete acceptance"},
            {"run_id", run}, {"started_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)},
            {"model_path",model}, {"cases",cases}, {"acceptance_complete",false}, {"passed",false}, {"selected_cases_completed",false},
            {"known_missing",new string[]{"full system acceptance and unexecuted selected cases"}} };
        string selection=Environment.GetEnvironmentVariable("F14_NATIVE_CASES");
        string[] names = {"simple-blind","simple-through","counterbore-blind","counterbore-through",
            "countersink-blind-tilted-block","countersink-through","threaded-right-blind","threaded-left-blind",
            "threaded-inch-positive-x-through","threaded-right-second-same-body","threaded-inch-positive-x-blind"};
        Dictionary<string,string> aliases=new Dictionary<string,string>(StringComparer.Ordinal) {
            {"undo","undo"},{"reject","reject"},{"cavity","through_enclosed_cavity_rejection"},
            {"roundtrip","roundtrip"},{"tamper","tamper_existing_thread"},{"breakthrough","thin_wall_and_thread_depth_rejection"} };
        foreach(string name in names) aliases.Add(name,name);
        expectedCases=new List<string>();
        string[] selected=String.IsNullOrEmpty(selection) ? aliases.Keys.ToArray() : selection.Split(',');
        string invalidSelection=null;
        foreach(string requested in selected) {
            string canonical;
            if(!aliases.TryGetValue(requested,out canonical) || expectedCases.Contains(canonical)) {
                invalidSelection=requested; break;
            }
            expectedCases.Add(canonical);
        }
        report["case_selection"]=selection; report["selected_case_ids"]=expectedCases;
        report["candidate_build_dir"]=Environment.GetEnvironmentVariable("F14_ADAPTER_BUILD_DIR");
        report["harness_assembly_path"]=typeof(F14Acceptance).Assembly.Location;
        report["adapter_assembly_path"]=typeof(M2OpenModelAdapter).Assembly.Location;
        report["bridge_assembly_path"]=typeof(M2Json).Assembly.Location;
        Persist();
        if(invalidSelection!=null) {
            report["fatal"]="Unknown or duplicate F14_NATIVE_CASES value: "+invalidSelection;
            report["writes_halted"]=true; report["selected_cases_completed"]=false;
            report["ended_utc"]=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture); Persist(); return;
        }
        try
        {
            session = Session.GetSession();
            Require(session.Parts.Work == null && !session.IsInTaskEnvironment && session.ActiveSketch == null,
                "Requires empty idle NX session; no existing part is modified.");
            part = session.Parts.NewDisplay(model, Part.Units.Millimeters);
            List<Body> bodies = new List<Body>();
            // Shift the rotated block 10 mm to keep it clear of both neighbours.
            for (int i=0; i<13; i++) bodies.Add(CreateBlock(i*50.0+(i==4?10.0:0.0),i==4));
            if (String.IsNullOrEmpty(selection) || selection.Split(',').Contains("cavity")) bodies[11]=CreateEnclosedCavity(bodies[11]);
            using (PartSaveStatus status = part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False)) { }
            adapter = new M2OpenModelAdapter();
            binding = adapter.Takeover(model, "saved_independent_copy", "f14-native-"+run, "f14-load-"+run, 1);
            IM3HoleOperationAdapter holeAdapter = ((object)adapter) as IM3HoleOperationAdapter;
            Require(holeAdapter != null, "Production adapter does not expose IM3HoleOperationAdapter.");
            for (int i=0; i<names.Length && !stop; i++)
            {
                if (!String.IsNullOrEmpty(selection) && !selection.Split(',').Contains(names[i])) continue;
                Dictionary<string,object> row = new Dictionary<string,object> { {"case",names[i]}, {"status","RUNNING"} };
                cases.Add(row); Persist();
                try {
                    if (i==9) Require(cases.Cast<Dictionary<string,object>>().Any(c=>
                        Convert.ToString(c["case"],CultureInfo.InvariantCulture)=="threaded-right-blind" &&
                        Convert.ToString(c["status"],CultureInfo.InvariantCulture)=="PASS_PARTIAL"),
                        "Same-body case requires threaded-right-blind in the same run.");
                    int targetIndex=i==9 ? 6 : i==10 ? 12 : i;
                    bodies[targetIndex]=RunCase(holeAdapter,bodies[targetIndex],bodies[9],i,names[i],row);
                    row["status"]="PASS_PARTIAL";
                }
                catch (Exception ex) { row["status"] = stop ? "UNKNOWN_STOPPED" : "FAIL"; row["exception"] = ex.ToString(); stop=true; }
                Persist();
            }
            if (!stop && (String.IsNullOrEmpty(selection) || selection.Split(',').Contains("undo"))) {
                Dictionary<string,object> row=new Dictionary<string,object>{{"case","undo"},{"status","RUNNING"}};
                cases.Add(row); Persist();
                try { RunUndo(holeAdapter,bodies[10],row); row["status"]="PASS_PARTIAL"; }
                catch(Exception ex) { row["status"]=stop?"UNKNOWN_STOPPED":"FAIL"; row["exception"]=ex.ToString(); stop=true; }
                Persist();
            }
            if (!stop && (String.IsNullOrEmpty(selection) || selection.Split(',').Contains("reject"))) {
                Dictionary<string,object> row=new Dictionary<string,object>{{"case","reject"},{"status","RUNNING"}};
                cases.Add(row); Persist();
                try { RunRejections(holeAdapter,bodies[10],bodies[6],row); row["status"]="PASS_PARTIAL"; }
                catch(Exception ex) { row["status"]=stop?"UNKNOWN_STOPPED":"FAIL"; row["exception"]=ex.ToString(); stop=true; }
                Persist();
            }
            if (!stop && (String.IsNullOrEmpty(selection) || selection.Split(',').Contains("breakthrough"))) {
                Dictionary<string,object> row=new Dictionary<string,object>{{"case","thin_wall_and_thread_depth_rejection"},{"status","RUNNING"}};
                cases.Add(row); Persist();
                try { RunBreakthroughRejections(holeAdapter,bodies[10],row); row["status"]="PASS_PARTIAL"; }
                catch(Exception ex) { row["status"]=stop?"UNKNOWN_STOPPED":"FAIL"; row["exception"]=ex.ToString(); stop=true; }
                Persist();
            }
            if (!stop && (String.IsNullOrEmpty(selection) || selection.Split(',').Contains("cavity"))) {
                Dictionary<string,object> row=new Dictionary<string,object>{{"case","through_enclosed_cavity_rejection"},{"status","RUNNING"}};
                cases.Add(row); Persist();
                try { RunCavityRejection(holeAdapter,bodies[11],row); row["status"]="PASS_PARTIAL"; }
                catch(Exception ex) { row["status"]=stop?"UNKNOWN_STOPPED":"FAIL"; row["exception"]=ex.ToString(); stop=true; }
                Persist();
            }
            if (!stop && (String.IsNullOrEmpty(selection) || selection.Split(',').Contains("roundtrip"))) {
                Dictionary<string,object> row=new Dictionary<string,object>{{"case","roundtrip"},{"status","RUNNING"}};
                cases.Add(row); Persist();
                try { RunRoundtrip(model.Replace(".prt","-roundtrip.prt"),row); row["status"]="PASS_PARTIAL"; }
                catch(Exception ex) { row["status"]=stop?"UNKNOWN_STOPPED":"FAIL"; row["exception"]=ex.ToString(); stop=true; }
                Persist();
            }
            if (!stop && (String.IsNullOrEmpty(selection) || selection.Split(',').Contains("tamper"))) {
                Dictionary<string,object> row=new Dictionary<string,object>{{"case","tamper_existing_thread"},{"status","RUNNING"}};
                cases.Add(row); Persist();
                try { RunTamper(holeAdapter,row); row["status"]="PASS_PARTIAL"; }
                catch(Exception ex) { row["status"]=stop?"UNKNOWN_STOPPED":"FAIL"; row["exception"]=ex.ToString(); stop=true; }
                Persist();
            }
            List<string> observed=cases.Cast<Dictionary<string,object>>().Select(c=>Convert.ToString(c["case"],CultureInfo.InvariantCulture)).ToList();
            report["unexecuted_selected_case_ids"]=expectedCases.Where(n=>!observed.Contains(n)).ToArray();
            report["selected_cases_completed"] = !stop && expectedCases.Count>0 && expectedCases.All(observed.Contains) &&
                cases.Cast<Dictionary<string,object>>().All(c=>Convert.ToString(c["status"],CultureInfo.InvariantCulture)=="PASS_PARTIAL");
        }
        catch (Exception ex) { report["fatal"] = ex.ToString(); stop=true; }
        finally
        {
            report["writes_halted"] = stop;
            List<string> observed=cases.Cast<Dictionary<string,object>>().Select(c=>Convert.ToString(c["case"],CultureInfo.InvariantCulture)).ToList();
            report["unexecuted_selected_case_ids"]=expectedCases.Where(n=>!observed.Contains(n)).ToArray();
            report["ended_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Persist();
        }
    }

    static Body CreateBlock(double x,bool tilted)
    {
        BlockFeatureBuilder b=part.Features.CreateBlockFeatureBuilder(null);
        try {
            b.SetOriginAndLengths(new Point3d(x,0,0),"40","35","20");
            if (tilted) b.SetOrientation(new Vector3d(0.8,0,0.6),new Vector3d(0,1,0));
            b.SetBooleanOperationAndTarget(Feature.BooleanType.Create,null);
            return b.CommitFeature().GetBodies()[0];
        } finally { b.Destroy(); }
    }
    static Body CreateEnclosedCavity(Body target)
    {
        BlockFeatureBuilder builder=part.Features.CreateBlockFeatureBuilder(null);
        try {
            builder.SetOriginAndLengths(new Point3d(565,8,7),"10","12","6");
            builder.SetBooleanOperationAndTarget(Feature.BooleanType.Subtract,target);
            return builder.CommitFeature().GetBodies().Single();
        } finally { builder.Destroy(); }
    }

    static Body RunCase(IM3HoleOperationAdapter holes, Body target, Body sentinel, int index, string name, Dictionary<string,object> row)
    {
        bool thread = index >= 6, through = index==1 || index==3 || index==5 || index==8;
        string kind = thread ? "threaded" : index<2 ? "simple" : index<4 ? "counterbore" : "countersink";
        bool inch = index==8 || index==10, tilted=index==4;
        double x=index==9 ? 328.0 : index==10 ? 600.0 : inch ? 400.0 : index*50.0+17.0;
        M3HoleRequest req = new M3HoleRequest { RevisionId=binding.RevisionId, TargetBodyId=target.JournalIdentifier,
            PositionMm=inch ? new double[]{x,17,10} : tilted ? new double[]{211.6,13,26.2} : new double[]{x,13,20},
            Direction=inch ? new double[]{1,0,0} : tilted ? new double[]{0.6,0,-0.8} : new double[]{0,0,-1}, Kind=kind,
            DepthMode=through ? "through" : "blind", DiameterMm=4, HoleDepthMm=10,
            CounterboreDiameterMm=8, CounterboreDepthMm=3, CountersinkDiameterMm=8, CountersinkAngleDeg=90,
            ThreadStandard=inch ? "Inch UNC" : "Metric Coarse", ThreadSize=inch ? "1/4-20" : "M5 x 0.8",
            RadialEngage="0.75", PitchMm=inch ? 1.27 : 0.8,
            Hand=index==7 ? "left" : "right", ThreadDepthMm=7 };
        Dictionary<string,object> sentinelBefore=BodyRecord(sentinel);
        List<object> allOtherBodiesBefore=OtherBodyRecords(target.Tag);
        List<object> arcsBefore=ArcInventory(); row["arc_inventory_before"]=arcsBefore;
        row["request"] = new Dictionary<string,object> { {"kind",kind},{"depth_mode",req.DepthMode},
            {"position_mm",req.PositionMm},{"direction",req.Direction},{"target_body_id",req.TargetBodyId},
            {"diameter_mm",req.DiameterMm},{"hole_depth_mm",req.HoleDepthMm},{"hand",req.Hand},
            {"thread_standard",req.ThreadStandard},{"thread_size",req.ThreadSize},{"radial_engage",req.RadialEngage},
            {"pitch_mm",req.PitchMm},{"thread_depth_mm",req.ThreadDepthMm},
            {"counterbore_diameter_mm",req.CounterboreDiameterMm},{"counterbore_depth_mm",req.CounterboreDepthMm},
            {"countersink_diameter_mm",req.CountersinkDiameterMm},{"countersink_angle_deg",req.CountersinkAngleDeg} };
        row["sentinel_before"] = sentinelBefore;
        row["non_target_bodies_before"] = allOtherBodiesBefore;
        double beforeVolume=Volume(target);
        row["target_before"] = BodyRecord(target); Persist();
        M3PlanSnapshot plan;
        try { plan=holes.PrepareHole(binding,req); }
        catch (M2ModelException ex) { if (ex.Code=="M3_ANALYSIS_STATE_UNKNOWN") stop=true; throw; }
        Require(plan!=null && plan.Kind=="create_hole" && plan.AdapterState!=null,"Invalid prepared hole plan.");
        row["plan"] = plan.PublicView; Persist();
        M3ExecutionResult executed;
        try { executed=adapter.ExecutePlan(binding,plan); }
        catch { stop=true; throw; }
        if (executed==null || executed.State=="UNKNOWN") { stop=true; throw new InvalidOperationException("UNKNOWN execution; no further NX calls"); }
        row["execution"] = executed.Result;
        row["execution_state"] = executed.State; Persist();
        Require(executed.State=="APPLIED","Product did not apply hole: "+executed.ErrorCode+" "+executed.ErrorMessage);
        M3VerificationResult verify;
        try { verify=adapter.VerifyPlan(binding,plan); }
        catch { stop=true; throw; }
        if (verify==null || verify.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN" || verify.ErrorCode=="VERIFY_OUTCOME_UNKNOWN")
        { stop=true; throw new InvalidOperationException("UNKNOWN fresh verification; no further NX calls"); }
        row["verify_checks"]=verify.Checks; row["verified"]=verify.Verified; Persist();
        Require(verify.Verified,"Product fresh verification failed: "+verify.ErrorCode);
        string featureId=Convert.ToString(executed.Result["feature_id"],CultureInfo.InvariantCulture);
        row["feature_id"]=featureId;
        HolePackage feature=part.Features.GetFeatures().OfType<HolePackage>().Single(f=>f.JournalIdentifier==featureId);
        if (thread) VerifyThreadFeature(feature,req,row);
        HolePackageBuilder reader=part.Features.CreateHolePackageBuilder(feature);
        try {
            row["native_definition"] = new Dictionary<string,object> {
                {"type_of_hole",reader.TypeOfHole.ToString()},
                {"depth_option",reader.DepthOption.ToString()}, {"limit",reader.HoleDepthLimitOption.ToString()},
                {"thread_standard",reader.ThreadStandard}, {"thread_size",reader.ThreadSize},
                {"radial_engage",reader.RadialEngageOption}, {"thread_rotation",reader.ThreadRotation.ToString()},
                {"thread_depth_mm",reader.ThreadDepth.Value}, {"threaded_hole_depth_mm",reader.ThreadedHoleDepth.Value},
                {"tap_drill_diameter_mm",reader.TapDrillDiameter.Value} };
            row["section_mapping"] = SectionMapping(reader.HolePosition);
            Require(reader.HoleDepthLimitOption == (through ? HolePackageBuilder.HoleDepthLimitOptions.ThroughBody :
                HolePackageBuilder.HoleDepthLimitOptions.Value),"Native depth limit differs.");
            if (thread) {
                Require(reader.ThreadStandard==req.ThreadStandard && reader.ThreadSize==req.ThreadSize &&
                    reader.RadialEngageOption==req.RadialEngage,"Native thread standard/size/engagement differ.");
                Require(reader.ThreadRotation == (req.Hand=="left" ? HolePackageBuilder.ThreadRotationOptions.Left :
                    HolePackageBuilder.ThreadRotationOptions.Right),"Native thread hand differs.");
                Near(reader.ThreadDepth.Value,7,1e-6,"thread depth");
                Near(reader.TapDrillDiameter.Value,inch ? 0.201*25.4 : 4.2,1e-6,"tap drill");
                if (!through) Near(reader.ThreadedHoleDepth.Value,10,1e-6,"threaded cylinder depth");
            } else {
                double diameter = kind=="simple" ? reader.GeneralSimpleHoleDiameter.Value : kind=="counterbore" ?
                    reader.GeneralCounterboreHoleDiameter.Value : reader.GeneralCountersinkHoleDiameter.Value;
                double depth = kind=="simple" ? reader.GeneralSimpleHoleDepth.Value : kind=="counterbore" ?
                    reader.GeneralCounterboreHoleDepth.Value : reader.GeneralCountersinkHoleDepth.Value;
                Near(diameter,4,1e-6,"native diameter");
                if (!through) Near(depth,10,1e-6,"native cylinder depth");
                if (kind=="counterbore") { Near(reader.GeneralCounterboreDiameter.Value,8,1e-6,"counterbore diameter");
                    Near(reader.GeneralCounterboreDepth.Value,3,1e-6,"counterbore depth"); }
                if (kind=="countersink") { Near(reader.GeneralCountersinkDiameter.Value,8,1e-6,"countersink diameter");
                    Near(reader.GeneralCountersinkAngle.Value,90,1e-5,"countersink angle"); }
            }
        } finally { reader.Destroy(); }
        Point3d[] origins; Vector3d[] directions; feature.GetOrigins(out origins); feature.GetDirections(out directions);
        Require(origins.Length==1 && directions.Length==1,"Expected one position and direction.");
        Near(origins[0].X,req.PositionMm[0],1e-6,"origin x"); Near(origins[0].Y,req.PositionMm[1],1e-6,"origin y"); Near(origins[0].Z,req.PositionMm[2],1e-6,"origin z");
        Near(directions[0].X,req.Direction[0],1e-7,"direction x"); Near(directions[0].Y,0,1e-7,"direction y"); Near(directions[0].Z,req.Direction[2],1e-7,"direction z");
        Body output=feature.GetBodies().Single();
        row["output_body"] = BodyRecord(output);
        row["expressions"] = ExpressionInventory();
        List<object> arcsAfter=ArcInventory(); row["arc_inventory_after"]=arcsAfter;
        HashSet<string> priorTags=new HashSet<string>(arcsBefore.Cast<Dictionary<string,object>>().Select(a=>(string)a["tag"]),StringComparer.Ordinal);
        HashSet<string> afterTags=new HashSet<string>(arcsAfter.Cast<Dictionary<string,object>>().Select(a=>(string)a["tag"]),StringComparer.Ordinal);
        foreach(Dictionary<string,object> oldArc in arcsBefore.Cast<Dictionary<string,object>>()) {
            Dictionary<string,object> current=arcsAfter.Cast<Dictionary<string,object>>().Single(a=>(string)a["tag"]==(string)oldArc["tag"]);
            Dictionary<string,object> oldGeometry=new Dictionary<string,object>(oldArc,StringComparer.Ordinal);
            Dictionary<string,object> currentGeometry=new Dictionary<string,object>(current,StringComparer.Ordinal);
            oldGeometry.Remove("id"); currentGeometry.Remove("id");
            Require(M2Json.CanonicalText(oldGeometry)==M2Json.CanonicalText(currentGeometry),"Existing Arc geometry or session Tag changed during hole operation");
        }
        afterTags.ExceptWith(priorTags); row["new_arc_tags"]=afterTags.OrderBy(s=>s,StringComparer.Ordinal).ToArray();
        if(thread) {
            Dictionary<string,object> threadData=(Dictionary<string,object>)row["thread_feature"];
            HashSet<string> owned=new HashSet<string>((string[])threadData["arc_entity_tags"],StringComparer.Ordinal);
            Require(owned.Count==2 && afterTags.SetEquals(owned),"New Arc set is not exactly the two entities of the uniquely associated Thread");
        } else Require(afterTags.Count==0,"General hole created unexpected standalone Arcs");
        double r=thread ? (inch ? 0.201*25.4/2.0 : 2.1) : 2.0;
        VerifyFaces(output,req.PositionMm,req.Direction,kind,through,r,row);
        double removed=beforeVolume-Volume(output);
        double length=through ? (inch ? 40.0 : 20.0) : 10.0;
        double expected=Math.PI*r*r*length;
        if (!through) expected+=Math.PI*r*r*r/(3*Math.Tan(118.0*Math.PI/360.0));
        if (kind=="counterbore") expected+=Math.PI*(16-4)*3;
        if (kind=="countersink") expected+=Math.PI*2*(16+8-8)/3;
        row["volume"] = new Dictionary<string,object> { {"observed_removed_mm3",removed},{"expected_removed_mm3",expected},{"delta_mm3",removed-expected} };
        Near(removed,expected,Math.Max(1e-5,1e-6*expected),"removed volume");
        Witness(output,inch ? new double[]{x+1,17,10} : tilted ? new double[]{212.2,13,25.4} : new double[]{req.PositionMm[0],13,19},2,row,"entry bore");
        Witness(output,inch ? new double[]{x+39,17,10} : tilted ? new double[]{223.0,13,11.0} : new double[]{req.PositionMm[0],13,1},through ? 2 : 1,row,"through or blind bottom");
        Witness(output,inch ? new double[]{x+1,23,10} : tilted ? new double[]{217.0,13,29.0} : new double[]{req.PositionMm[0]+6,13,19},1,row,"outside bore");
        Dictionary<string,object> sentinelAfter=BodyRecord(sentinel); row["sentinel_after"]=sentinelAfter;
        Require(EqualBody(sentinelBefore,sentinelAfter),"sentinel body changed");
        List<object> allOtherBodiesAfter=OtherBodyRecords(output.Tag);
        row["non_target_bodies_after"]=allOtherBodiesAfter;
        Require(EqualBodyInventories(allOtherBodiesBefore,allOtherBodiesAfter),"non-target body inventory changed");
        row["origin_mm"]=new double[]{origins[0].X,origins[0].Y,origins[0].Z};
        row["direction"]=new double[]{directions[0].X,directions[0].Y,directions[0].Z};
        return output;
    }

    static void VerifyThreadFeature(HolePackage package,M3HoleRequest request,Dictionary<string,object> row)
    {
        Expression[] packageExpressions=package.GetExpressions();
        HashSet<Tag> packageTags=new HashSet<Tag>(packageExpressions.Select(e=>e.Tag));
        HashSet<Tag> packageBodies=new HashSet<Tag>(package.GetBodies().Select(b=>b.Tag));
        HashSet<Tag> packageParents=new HashSet<Tag>(package.GetParents().Select(f=>f.Tag));
        List<Feature> holes=new List<Feature>();
        foreach(Feature candidate in part.Features.GetFeatures()) {
            if (!candidate.IsInternal || candidate.FeatureType!="SIMPLE HOLE" || candidate.Timestamp!=package.Timestamp) continue;
            if (!packageBodies.SetEquals(candidate.GetBodies().Select(b=>b.Tag)) || !packageParents.SetEquals(candidate.GetParents().Select(f=>f.Tag))) continue;
            Expression[] expressions=candidate.GetExpressions();
            if (!expressions.Any(e=>packageTags.Contains(e.Tag))) continue;
            if (!expressions.All(e=> { Feature[] users=e.GetUsingFeatures(); return users.Length==1 && users[0].Tag==package.Tag; })) continue;
            holes.Add(candidate);
        }
        Require(holes.Count==1,"Expected one internal hole with same timestamp, body, parents, shared expressions and exclusive package users.");
        Feature hole=holes[0];
        List<NXOpen.Features.Thread> matches=hole.GetChildren().OfType<NXOpen.Features.Thread>().ToList();
        Require(matches.Count==1,"Expected exactly one internal thread child of the matched hole.");
        NXOpen.Features.Thread associated=matches[0];
        Require(associated.IsInternal && associated.FeatureType=="SYMBOLIC_THREAD" && associated.Timestamp==package.Timestamp &&
            associated.GetParents().Length==1 && associated.GetParents()[0].Tag==hole.Tag && associated.GetBodies().Length==0,
            "Thread identity, timestamp, hole parent or body association mismatch.");
        Expression[] threadExpressions=associated.GetExpressions();
        Require(threadExpressions.Length>0 && threadExpressions.Any(e=>packageTags.Contains(e.Tag)) && threadExpressions.All(e=> {
            Feature[] users=e.GetUsingFeatures(); return users.Length==1 && users[0].Tag==package.Tag; }),
            "Thread expression sharing and exclusive package users do not prove ownership.");
        ThreadBuilder thread=part.Features.CreateThreadBuilder(associated);
        try {
            row["thread_feature"] = new Dictionary<string,object> { {"id",associated.JournalIdentifier},
                {"parents",associated.GetParents().Select(p=>p.JournalIdentifier).ToArray()},
                {"hole_id",hole.JournalIdentifier},{"hole_timestamp",hole.Timestamp},{"package_timestamp",package.Timestamp},
                {"shared_expression_tags",threadExpressions.Where(e=>packageTags.Contains(e.Tag)).Select(e=>e.Tag.ToString()).ToArray()},
                {"arc_entity_tags",associated.GetEntities().OfType<Arc>().Select(a=>a.Tag.ToString()).OrderBy(s=>s,StringComparer.Ordinal).ToArray()},
                {"standard",thread.ThreadStandard},{"size",thread.ThreadSize},{"radial_engage",thread.RadialEngage},
                {"pitch_mm",thread.Pitch},{"pitch_expression_mm",thread.PitchExp.Value},
                {"tap_drill_diameter_mm",thread.TapDrillDiameter},{"thread_depth_mm",thread.ThreadLength.Value},
                {"hand",thread.ThreadHandedness.ToString()},{"internal",thread.IsInternalThread} };
            Require(thread.ThreadStandard==request.ThreadStandard && thread.ThreadSize==request.ThreadSize &&
                thread.RadialEngage==request.RadialEngage && thread.IsInternalThread,"ThreadBuilder standard or internal state differs");
            Require(thread.ThreadHandedness == (request.Hand=="left" ? ThreadBuilder.Handedness.LeftHand :
                ThreadBuilder.Handedness.RightHand),"ThreadBuilder hand differs");
            Near(thread.Pitch,request.PitchMm,1e-6,"native thread pitch");
            Near(thread.PitchExp.Value,request.PitchMm,1e-6,"native thread pitch expression");
            Near(thread.ThreadLength.Value,request.ThreadDepthMm,1e-6,"native thread length");
        } finally { thread.Destroy(); }
    }

    static void VerifyFaces(Body body,double[] origin,double[] axis,string kind,bool through,double boreRadius,Dictionary<string,object> row)
    {
        List<object> faces=new List<object>(); int small=0, large=0, tip=0, sink=0;
        HashSet<Tag> wallEdges=new HashSet<Tag>(),tipEdges=new HashSet<Tag>(),sinkEdges=new HashSet<Tag>();
        foreach (Face face in body.GetFaces()) {
            int type,norm; double[] p=new double[3],dir=new double[3],box=new double[6]; double radius,radData;
            UFSession.GetUFSession().Modl.AskFaceData(face.Tag,out type,p,dir,box,out radius,out radData,out norm);
            if (type!=16 && type!=17) continue;
            Dictionary<string,object> f=new Dictionary<string,object> { {"type",type},{"axis_point",p},
                {"axis_direction",dir},{"radius_mm",radius},{"half_angle_rad",radData},{"approx_box",box} };
            faces.Add(f);
            double dx=p[0]-origin[0],dy=p[1]-origin[1],dz=p[2]-origin[2];
            double along=dx*axis[0]+dy*axis[1]+dz*axis[2];
            double radial=Math.Sqrt((dx-along*axis[0])*(dx-along*axis[0])+(dy-along*axis[1])*(dy-along*axis[1])+(dz-along*axis[2])*(dz-along*axis[2]));
            double alignment=dir[0]*axis[0]+dir[1]*axis[1]+dir[2]*axis[2];
            double crossX=dir[1]*axis[2]-dir[2]*axis[1],crossY=dir[2]*axis[0]-dir[0]*axis[2],crossZ=dir[0]*axis[1]-dir[1]*axis[0];
            double axisError=Math.Atan2(Math.Sqrt(crossX*crossX+crossY*crossY+crossZ*crossZ),Abs(alignment));
            f["axis_line_offset_mm"]=radial; f["axis_angle_error_rad"]=axisError;
            if (radial>1e-6 || axisError>1e-7) continue;
            if (type==16 && Abs(radius-boreRadius)<=1e-6) {
                small++;
                double start=kind=="counterbore" ? 3.0 : kind=="countersink" ? 2.0 : 0.0;
                double end=through ? (Abs(axis[0])>0.999999 ? 40.0 : 20.0) : 10.0;
                VerifyCylinderTrim(face,origin,axis,boreRadius,start,end,f);
                foreach(Edge edge in face.GetEdges()) wallEdges.Add(edge.Tag);
            }
            if (type==16 && Abs(radius-4.0)<=1e-6) { large++; VerifyCylinderTrim(face,origin,axis,4.0,0.0,3.0,f); }
            if (type==17 && Abs(Abs(radData)-59*Math.PI/180.0)<=1e-7) {
                tip++; double h=boreRadius/Math.Tan(59*Math.PI/180.0);
                double expected=Math.PI*boreRadius*Math.Sqrt(boreRadius*boreRadius+h*h);
                double area=FaceArea(face); f["area_mm2"]=area; f["expected_area_mm2"]=expected;
                Near(area,expected,Math.Max(1e-5,expected*1e-6),"blind tip trimmed area");
                foreach(Edge edge in face.GetEdges()) tipEdges.Add(edge.Tag);
            }
            if (type==17 && Abs(Abs(radData)-45*Math.PI/180.0)<=1e-7) {
                sink++; double expected=Math.PI*(4+2)*Math.Sqrt(8.0);
                double area=FaceArea(face); f["area_mm2"]=area; f["expected_area_mm2"]=expected;
                Near(area,expected,Math.Max(1e-5,expected*1e-6),"countersink trimmed area");
                foreach(Edge edge in face.GetEdges()) sinkEdges.Add(edge.Tag);
            }
        }
        row["cylindrical_and_conical_faces"]=faces;
        Require(small>=1,"No cylindrical bore face with expected axis and radius.");
        if (kind=="counterbore") Require(large>=1,"No counterbore cylinder with expected axis and radius.");
        if (kind=="countersink") Require(sink>=1,"No countersink cone with expected axis and half angle.");
        if (!through) Require(tip>=1,"No blind drill-tip cone with expected axis and half angle.");
        if (!through) Require(wallEdges.Overlaps(tipEdges),"Blind drill-tip cone does not share the bore's bottom edge.");
        if (kind=="countersink") Require(wallEdges.Overlaps(sinkEdges),"Countersink cone does not share the bore's entry edge.");
    }

    static void VerifyCylinderTrim(Face face,double[] origin,double[] axis,double radius,double start,double end,Dictionary<string,object> record)
    {
        double area=FaceArea(face), expectedArea=2*Math.PI*radius*(end-start);
        record["area_mm2"]=area; record["expected_area_mm2"]=expectedArea;
        Near(area,expectedArea,Math.Max(1e-5,expectedArea*1e-6),"trimmed cylindrical face area");
        List<object> rings=new List<object>(); bool startFound=false,endFound=false;
        foreach(Edge edge in face.GetEdges()) {
            double length=edge.GetLength();
            if (Abs(length-2*Math.PI*radius)>1e-5) continue;
            Point3d a,b; edge.GetVertices(out a,out b);
            double ta=(a.X-origin[0])*axis[0]+(a.Y-origin[1])*axis[1]+(a.Z-origin[2])*axis[2];
            double tb=(b.X-origin[0])*axis[0]+(b.Y-origin[1])*axis[1]+(b.Z-origin[2])*axis[2];
            if(Abs(ta-tb)>1e-6) continue;
            rings.Add(new Dictionary<string,object>{{"edge_id",edge.JournalIdentifier},{"length_mm",length},{"axis_position_mm",ta}});
            if(Abs(ta-start)<=1e-6) startFound=true;
            if(Abs(ta-end)<=1e-6) endFound=true;
        }
        record["full_circumference_rings"]=rings; record["expected_axis_start_mm"]=start; record["expected_axis_end_mm"]=end;
        Require(startFound && endFound,"Trimmed cylinder is missing full-circumference start/end rings at expected depths");
    }
    static double FaceArea(Face face)
    {
        MeasureFaces m=part.MeasureManager.NewFaceProperties(part.UnitCollection.FindObject("SquareMilliMeter"),
            part.UnitCollection.FindObject("MilliMeter"),0.999999,new IParameterizedSurface[]{face});
        try { return m.Area; } finally { m.Dispose(); }
    }

    static void Witness(Body b,double[] point,int expected,Dictionary<string,object> row,string label)
    {
        int actual; UFSession.GetUFSession().Modl.AskPointContainment(point,b.Tag,out actual);
        if (!row.ContainsKey("witnesses")) row["witnesses"]=new List<object>();
        ((List<object>)row["witnesses"]).Add(new Dictionary<string,object>{{"label",label},{"point_mm",point},{"actual",actual},{"expected",expected}});
        Require(actual==expected,"Containment mismatch: "+label);
    }
    static void RunUndo(IM3HoleOperationAdapter holes,Body target,Dictionary<string,object> row)
    {
        string beforeRevision=binding.RevisionId;
        Dictionary<string,object> before=BodyRecord(target);
        List<object> others=OtherBodyRecords(target.Tag);
        List<object> historyBefore=HistoryRecord();
        M3HoleRequest request=new M3HoleRequest {RevisionId=beforeRevision,TargetBodyId=target.JournalIdentifier,
            PositionMm=new double[]{517,13,20},Direction=new double[]{0,0,-1},Kind="simple",DepthMode="blind",
            DiameterMm=4,HoleDepthMm=10};
        row["before"]=before; row["non_targets_before"]=others; row["history_before"]=historyBefore;
        row["revision_before"]=beforeRevision; Persist();
        M3PlanSnapshot plan;
        try { plan=holes.PrepareHole(binding,request); }
        catch(M2ModelException ex) { if(ex.Code=="M3_ANALYSIS_STATE_UNKNOWN") stop=true; throw; }
        Require(plan!=null && plan.AdapterState!=null,"Missing plan for product Undo control");
        FieldInfo field=plan.AdapterState.GetType().GetField("ForcePostCommitVerificationFailureForTesting",
            BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
        Require(field!=null && field.FieldType==typeof(bool),"Product post-commit failure injection hook is unavailable");
        field.SetValue(plan.AdapterState,true); row["forced_post_commit_verification_failure"]=true; Persist();
        M3ExecutionResult result;
        try { result=adapter.ExecutePlan(binding,plan); }
        catch { stop=true; throw; }
        if(result==null || result.State=="UNKNOWN") { stop=true; throw new InvalidOperationException("UNKNOWN Undo control; no further NX calls"); }
        row["execution_state"]=result.State; row["execution_result"]=result.Result;
        row["revision_after"]=binding.RevisionId; Persist();
        Require(result.State=="ROLLED_BACK","Product did not perform a verified rollback");
        Require(binding.RevisionId!=beforeRevision,"Rollback did not advance revision");
        Dictionary<string,object> after=BodyRecord(target); List<object> othersAfter=OtherBodyRecords(target.Tag);
        List<object> historyAfter=HistoryRecord();
        row["after"]=after; row["non_targets_after"]=othersAfter; row["history_after"]=historyAfter;
        Require(EqualBody(before,after),"Undo did not restore target fixture");
        Require(EqualBodyInventories(others,othersAfter),"Undo altered non-target fixture");
        Require(M2Json.CanonicalText(historyBefore)==M2Json.CanonicalText(historyAfter),"Undo left changed feature history");
    }
    static void RunRoundtrip(string path,Dictionary<string,object> row)
    {
        Require(!File.Exists(path),"SaveCopy path already exists");
        List<Dictionary<string,object>> created=cases.Cast<Dictionary<string,object>>().Where(c=>
            c.ContainsKey("feature_id") && Convert.ToString(c["status"],CultureInfo.InvariantCulture)=="PASS_PARTIAL").ToList();
        Require(created.Count>0,"No successful hole cases to roundtrip");
        List<object> before=new List<object>();
        foreach(Dictionary<string,object> c in created) {
            string id=Convert.ToString(c["feature_id"],CultureInfo.InvariantCulture);
            HolePackage feature=part.Features.GetFeatures().OfType<HolePackage>().Single(f=>f.JournalIdentifier==id);
            before.Add(ActiveNativeDefinition(feature,(Dictionary<string,object>)c["request"]));
        }
        List<object> bodiesBefore=RoundtripBodySummaries();
        row["native_definitions_before"]=before; row["bodies_before"]=bodiesBefore; row["path"]=path; Persist();
        M2SaveCopyResult result;
        try { result=adapter.SaveCopy(binding,path,true); }
        catch { stop=true; throw; }
        if(result==null || result.State=="UNKNOWN" || result.ErrorCode=="SAVE_RESULT_UNKNOWN" ||
            (result.State=="SAVED" && !result.Reopened)) { stop=true; throw new InvalidOperationException("UNKNOWN SaveCopy outcome; no further NX calls"); }
        row["save"] = new Dictionary<string,object> { {"state",result.State},{"reopened",result.Reopened},
            {"path",result.PartPath},{"checks",result.Checks},{"error_code",result.ErrorCode}, {"evidence",result.Evidence} }; Persist();
        Require(result.State=="SAVED" && result.Reopened && String.IsNullOrEmpty(result.ErrorCode),"SaveCopy did not verify a saved reopened copy");
        foreach(string key in new string[]{"expression","geometry","history","associations"}) {
            object value; Dictionary<string,object> check=result.Checks!=null && result.Checks.TryGetValue(key,out value) ? value as Dictionary<string,object> : null;
            Require(check!=null && Convert.ToString(check["status"],CultureInfo.InvariantCulture)=="VERIFIED","SaveCopy check failed: "+key);
        }
        part=session.Parts.Work;
        Require(part!=null && String.Equals(part.FullPath,path,StringComparison.OrdinalIgnoreCase),"Reopened work part path mismatch");
        List<object> after=new List<object>(); HashSet<Tag> matched=new HashSet<Tag>();
        foreach(Dictionary<string,object> c in created) {
            Dictionary<string,object> req=(Dictionary<string,object>)c["request"];
            double[] position=(double[])req["position_mm"];
            List<HolePackage> candidates=new List<HolePackage>();
            foreach(HolePackage feature in part.Features.GetFeatures().OfType<HolePackage>()) {
                Point3d[] origins; feature.GetOrigins(out origins);
                if(origins.Length==1 && Abs(origins[0].X-position[0])<=1e-6 && Abs(origins[0].Y-position[1])<=1e-6 && Abs(origins[0].Z-position[2])<=1e-6) candidates.Add(feature);
            }
            Require(candidates.Count==1 && matched.Add(candidates[0].Tag),"Hole position does not match one-to-one after reload");
            after.Add(ActiveNativeDefinition(candidates[0],req));
        }
        row["native_definitions_after"]=after;
        List<object> bodiesAfter=RoundtripBodySummaries(); row["bodies_after"]=bodiesAfter; Persist();
        Require(M2Json.CanonicalText(before)==M2Json.CanonicalText(after),"Active hole definitions differ after reload");
        Require(bodiesBefore.Count==bodiesAfter.Count,"Body count differs after reload");
        for(int i=0;i<bodiesBefore.Count;i++) {
            Dictionary<string,object> a=(Dictionary<string,object>)bodiesBefore[i], b=(Dictionary<string,object>)bodiesAfter[i];
            double[] aa=(double[])a["bounds_mm"],bb=(double[])b["bounds_mm"];
            for(int j=0;j<6;j++) Near(bb[j],aa[j],1e-6,"reloaded body bounds");
            Near((double)b["volume_mm3"],(double)a["volume_mm3"],1e-5,"reloaded body volume");
            Require(Object.Equals(a["face_count"],b["face_count"]) && Object.Equals(a["edge_count"],b["edge_count"]),"Reloaded body topology count differs");
        }
    }

    static void RunRejections(IM3HoleOperationAdapter holes,Body target,Body crossedTarget,Dictionary<string,object> row)
    {
        double[][] points={new double[]{517,13,21},new double[]{517,13,19},new double[]{517,13,20},
            new double[]{517,13,20},new double[]{500,13,20},new double[]{517,13,20}};
        double[][] directions={new double[]{0,0,-1},new double[]{0,0,-1},new double[]{0,0,1},
            new double[]{1,0,0},new double[]{0,0,-1},new double[]{0.6,0,-0.8}};
        string[] labels={"air","interior","outward","tangent","edge","oblique_to_entry"};
        Dictionary<string,object> before=BodyRecord(target); List<object> others=OtherBodyRecords(target.Tag);
        string revision=binding.RevisionId;
        List<object> features=HistoryRecord();
        List<object> observations=new List<object>(); row["observations"]=observations;
        row["test_distance_mm"]=0.01; row["target_before"]=before; Persist();
        for(int i=0;i<labels.Length;i++) {
            M3HoleRequest req=new M3HoleRequest { RevisionId=revision,TargetBodyId=target.JournalIdentifier,Kind="simple",
                DepthMode="blind",DiameterMm=4,HoleDepthMm=10,PositionMm=points[i],Direction=directions[i] };
            Dictionary<string,object> item=new Dictionary<string,object>{{"label",labels[i]},{"position_mm",points[i]},{"direction",directions[i]}};
            observations.Add(item); Persist();
            try { M3PlanSnapshot plan=holes.PrepareHole(binding,req); item["unexpected_plan"]=plan==null?null:plan.PublicView;
                throw new InvalidOperationException("Invalid hole request was not rejected: "+labels[i]); }
            catch(M2ModelException ex) { if(ex.Code=="M3_ANALYSIS_STATE_UNKNOWN") { stop=true; throw; }
                item["error_code"]=ex.Code; item["message"]=ex.Message; }
            Dictionary<string,object> after=BodyRecord(target); List<object> othersAfter=OtherBodyRecords(target.Tag);
            List<object> afterFeatures=HistoryRecord();
            Require(revision==binding.RevisionId && EqualBody(before,after) &&
                EqualBodyInventories(others,othersAfter) && M2Json.CanonicalText(features)==M2Json.CanonicalText(afterFeatures),
                "Rejected input changed fixture, feature history, or revision: "+labels[i]);
            item["preserved"]=true; Persist();
        }
        for(int i=0;i<3;i++) {
            M3HoleRequest threaded=new M3HoleRequest { RevisionId=revision,TargetBodyId=target.JournalIdentifier,
                Kind="threaded",DepthMode="blind",PositionMm=new double[]{517,13,20},Direction=new double[]{0,0,-1},
                HoleDepthMm=10,ThreadStandard="Metric Coarse",ThreadSize=i==1 ? "NONEXISTENT" : "M5 x 0.8",
                RadialEngage=i==2 ? "NONEXISTENT" : "0.75",PitchMm=i==0 ? 0.9 : 0.8,Hand="right",ThreadDepthMm=7 };
            Dictionary<string,object> item=new Dictionary<string,object> {{"label",i==0?"pitch_mismatch":i==1?"unknown_size":"unknown_engagement"}};
            observations.Add(item); Persist();
            try { M3PlanSnapshot plan=holes.PrepareHole(binding,threaded); item["unexpected_plan"]=plan==null?null:plan.PublicView;
                throw new InvalidOperationException("Invalid standard thread was not rejected: "+item["label"]); }
            catch(M2ModelException ex) { if(ex.Code=="M3_ANALYSIS_STATE_UNKNOWN") { stop=true; throw; }
                item["error_code"]=ex.Code; item["message"]=ex.Message; }
            Require(revision==binding.RevisionId && EqualBody(before,BodyRecord(target)) &&
                EqualBodyInventories(others,OtherBodyRecords(target.Tag)),
                "Rejected thread standard mismatch changed geometry or revision");
            item["preserved"]=true; Persist();
        }
        bool existing=cases.Cast<Dictionary<string,object>>().Any(c=>
            Convert.ToString(c["case"],CultureInfo.InvariantCulture)=="threaded-right-blind" &&
            Convert.ToString(c["status"],CultureInfo.InvariantCulture)=="PASS_PARTIAL");
        if (!existing) { row["crossing_control"]="NOT_RUN: requires threaded-right-blind in same run"; return; }
        Dictionary<string,object> crossBefore=BodyRecord(crossedTarget); List<object> crossOthers=OtherBodyRecords(crossedTarget.Tag);
        string crossRevision=binding.RevisionId;
        M3HoleRequest cross=new M3HoleRequest {RevisionId=crossRevision,TargetBodyId=crossedTarget.JournalIdentifier,
            PositionMm=new double[]{300,13,15},Direction=new double[]{1,0,0},Kind="simple",DepthMode="through",DiameterMm=4};
        Dictionary<string,object> crossing=new Dictionary<string,object> {{"label","crosses_existing_vertical_blind_hole"},
            {"position_mm",cross.PositionMm},{"direction",cross.Direction}}; row["crossing_control"]=crossing; Persist();
        M3PlanSnapshot crossPlan=null;
        try { crossPlan=holes.PrepareHole(binding,cross); }
        catch(M2ModelException ex) { if(ex.Code=="M3_ANALYSIS_STATE_UNKNOWN") { stop=true; throw; }
            crossing["reject_phase"]="prepare"; crossing["error_code"]=ex.Code; crossing["message"]=ex.Message; }
        if (crossPlan!=null) {
            crossing["plan"]=crossPlan.PublicView; Persist();
            M3ExecutionResult result;
            try { result=adapter.ExecutePlan(binding,crossPlan); } catch { stop=true; throw; }
            if(result==null || result.State=="UNKNOWN") { stop=true; throw new InvalidOperationException("UNKNOWN cross-hole outcome"); }
            crossing["reject_phase"]="execute"; crossing["execution_state"]=result.State;
            crossing["error_code"]=result.ErrorCode; crossing["result"]=result.Result; Persist();
            Require(result.State=="UNCHANGED" || result.State=="ROLLED_BACK","Cross-hole material coverage was not rejected");
            if(result.State=="ROLLED_BACK") Require(result.Result!=null && Object.Equals(result.Result["undo_called"],true) &&
                Object.Equals(result.Result["geometry_restored"],true) && Object.Equals(result.Result["history_restored"],true),
                "Cross-hole rollback lacked product Undo/restoration evidence");
        } else Require(crossing.ContainsKey("error_code"),"Cross-hole returned null plan without a refusal");
        Require(EqualBody(crossBefore,BodyRecord(crossedTarget)) &&
            EqualBodyInventories(crossOthers,OtherBodyRecords(crossedTarget.Tag)),"Cross-hole rejection changed body or revision");
        crossing["revision_before"]=crossRevision; crossing["revision_after"]=binding.RevisionId;
        crossing["preserved"]=true; Persist();
    }

    static void RunTamper(IM3HoleOperationAdapter holes,Dictionary<string,object> row)
    {
        List<HolePackage> matches=part.Features.GetFeatures().OfType<HolePackage>().Where(delegate(HolePackage feature) {
            Point3d[] origins; feature.GetOrigins(out origins);
            return origins.Length==1 && Abs(origins[0].X-317)<1e-6 && Abs(origins[0].Y-13)<1e-6 && Abs(origins[0].Z-20)<1e-6;
        }).ToList();
        Require(matches.Count==1,"Tamper control requires the earlier M5 hole at [317,13,20]");
        HolePackage old=matches[0]; Body target=old.GetBodies().Single();
        List<object> attempts=new List<object>(); row["attempts"]=attempts;
        for(int i=0;i<2;i++) {
            M3HoleRequest req=new M3HoleRequest { RevisionId=binding.RevisionId,TargetBodyId=target.JournalIdentifier,
                PositionMm=new double[]{317,25,20},Direction=new double[]{0,0,-1},Kind="threaded",DepthMode="blind",
                HoleDepthMm=10,ThreadStandard="Metric Coarse",ThreadSize="M5 x 0.8",RadialEngage="0.75",
                PitchMm=0.8,Hand="right",ThreadDepthMm=7 };
            M3PlanSnapshot plan;
            try { plan=holes.PrepareHole(binding,req); }
            catch(M2ModelException ex) { if(ex.Code=="M3_ANALYSIS_STATE_UNKNOWN") stop=true; throw; }
            Require(plan!=null && plan.AdapterState!=null,"Tamper control could not prepare new hole");
            Dictionary<string,object> record=new Dictionary<string,object>{{"case",i==0?"hand":"thread_depth"},
                {"plan",plan.PublicView},{"manual_edit_on_disposable_fixture",true}};
            attempts.Add(record); Persist();
            HolePackageBuilder editor=part.Features.CreateHolePackageBuilder(old);
            try {
                if(i==0) editor.ThreadRotation=HolePackageBuilder.ThreadRotationOptions.Left;
                else editor.ThreadDepth.RightHandSide="6";
                try { editor.CommitFeature(); } catch { stop=true; throw; }
            } finally { editor.Destroy(); }
            HolePackageBuilder check=part.Features.CreateHolePackageBuilder(old);
            try {
                record["observed_hand_after_tamper"]=check.ThreadRotation.ToString();
                record["observed_thread_depth_after_tamper"]=check.ThreadDepth.Value;
                Require(i==0 ? check.ThreadRotation==HolePackageBuilder.ThreadRotationOptions.Left : Abs(check.ThreadDepth.Value-6)<1e-6,
                    "Manual test perturbation did not take effect");
            } finally { check.Destroy(); }
            Dictionary<string,object> geometry=BodyRecord(target); List<object> others=OtherBodyRecords(target.Tag);
            List<object> features=HistoryRecord();
            record["geometry_after_tamper"]=geometry; record["features_after_tamper"]=features; Persist();
            M3ExecutionResult result;
            try { result=adapter.ExecutePlan(binding,plan); } catch { stop=true; throw; }
            if(result==null || result.State=="UNKNOWN") { stop=true; throw new InvalidOperationException("UNKNOWN tamper control outcome"); }
            record["execution_state"]=result.State; record["error_code"]=result.ErrorCode; record["result"]=result.Result; Persist();
            Require((result.State=="UNCHANGED" || result.State=="ROLLED_BACK") &&
                (result.ErrorCode=="STALE_PLAN" || result.ErrorCode=="MODEL_READ_FAILED"),
                "Changed old hole was not refused/rolled back with a known stale/read error");
            Require(EqualBody(geometry,BodyRecord(target)) &&
                EqualBodyInventories(others,OtherBodyRecords(target.Tag)) &&
                M2Json.CanonicalText(features)==M2Json.CanonicalText(HistoryRecord()),
                "Stale execution changed geometry, non-targets or history");
            record["no_new_hole_or_geometry_change"]=true; Persist();
        }
    }

    static void RunCavityRejection(IM3HoleOperationAdapter holes,Body target,Dictionary<string,object> row)
    {
        // The separate target has a fully enclosed 10x12x6 cavity at z=7..13;
        // the intended through axis crosses two material layers with a gap.
        Dictionary<string,object> before=BodyRecord(target); List<object> others=OtherBodyRecords(target.Tag);
        List<object> features=HistoryRecord();
        row["target_before"]=before; row["cavity_bounds_mm"]=new double[]{565,8,7,575,20,13};
        Witness(target,new double[]{570,13,17},1,row,"upper material");
        Witness(target,new double[]{570,13,10},2,row,"enclosed cavity");
        Witness(target,new double[]{570,13,3},1,row,"lower material");
        M3HoleRequest request=new M3HoleRequest {RevisionId=binding.RevisionId,TargetBodyId=target.JournalIdentifier,
            PositionMm=new double[]{570,13,20},Direction=new double[]{0,0,-1},Kind="simple",DepthMode="through",DiameterMm=4};
        row["revision_before"]=binding.RevisionId; Persist();
        M3PlanSnapshot plan=null;
        try { plan=holes.PrepareHole(binding,request); }
        catch(M2ModelException ex) { if(ex.Code=="M3_ANALYSIS_STATE_UNKNOWN") { stop=true; throw; }
            row["reject_phase"]="prepare"; row["error_code"]=ex.Code; row["message"]=ex.Message; }
        if(plan!=null) {
            row["plan"]=plan.PublicView; Persist();
            M3ExecutionResult result;
            try { result=adapter.ExecutePlan(binding,plan); } catch { stop=true; throw; }
            if(result==null || result.State=="UNKNOWN") { stop=true; throw new InvalidOperationException("UNKNOWN cavity control outcome"); }
            row["reject_phase"]="execute"; row["execution_state"]=result.State; row["result"]=result.Result;
            row["error_code"]=result.ErrorCode; Persist();
            Require(result.State=="UNCHANGED" || result.State=="ROLLED_BACK","Through hole incorrectly accepted interrupted material");
            if(result.State=="ROLLED_BACK") Require(result.Result!=null && Object.Equals(result.Result["undo_called"],true) &&
                Object.Equals(result.Result["geometry_restored"],true) && Object.Equals(result.Result["history_restored"],true),
                "Cavity rollback lacks verified product Undo");
        } else Require(row.ContainsKey("error_code"),"Cavity control produced null plan without refusal");
        row["target_after"]=BodyRecord(target); row["revision_after"]=binding.RevisionId;
        Require(EqualBody(before,(Dictionary<string,object>)row["target_after"]) &&
            EqualBodyInventories(others,OtherBodyRecords(target.Tag)) &&
            M2Json.CanonicalText(features)==M2Json.CanonicalText(HistoryRecord()),
            "Rejected cavity hole changed target, non-targets or history");
        row["preserved"]=true; Persist();
    }

    static void RunBreakthroughRejections(IM3HoleOperationAdapter holes,Body target,Dictionary<string,object> row)
    {
        // 20 mm stock: cylinder 21 breaks through; cylinder 19.5 plus a
        // 4 mm drill's 118 degree tip (~1.202 mm) breaks the back wall; a
        // through thread length 21 exceeds its provable 20 mm wall.
        string[] labels={"blind_cylinder_breakthrough","blind_tip_breakthrough","through_thread_depth_exceeds_wall"};
        List<object> observations=new List<object>(); row["observations"]=observations;
        for(int i=0;i<labels.Length;i++) {
            string revision=binding.RevisionId; Dictionary<string,object> before=BodyRecord(target);
            List<object> others=OtherBodyRecords(target.Tag), history=HistoryRecord();
            M3HoleRequest request=new M3HoleRequest {RevisionId=revision,TargetBodyId=target.JournalIdentifier,
                PositionMm=new double[]{517,13,20},Direction=new double[]{0,0,-1},Kind=i==2?"threaded":"simple",
                DepthMode=i==2?"through":"blind",DiameterMm=4,HoleDepthMm=i==0?21:19.5,
                ThreadStandard="Metric Coarse",ThreadSize="M5 x 0.8",RadialEngage="0.75",PitchMm=0.8,Hand="right",ThreadDepthMm=21};
            Dictionary<string,object> item=new Dictionary<string,object> {{"label",labels[i]},{"stock_thickness_mm",20},
                {"diameter_mm",i==2?(object)null:4},{"hole_depth_mm",i==2?(object)null:request.HoleDepthMm},
                {"thread_depth_mm",i==2?(object)21:null},{"tip_included_angle_deg",118},{"revision_before",revision}};
            observations.Add(item); Persist();
            M3PlanSnapshot plan=null;
            try { plan=holes.PrepareHole(binding,request); }
            catch(M2ModelException ex) { if(ex.Code=="M3_ANALYSIS_STATE_UNKNOWN") { stop=true; throw; }
                item["reject_phase"]="prepare"; item["error_code"]=ex.Code; item["message"]=ex.Message; }
            if(plan!=null) {
                item["plan"]=plan.PublicView; Persist(); M3ExecutionResult result;
                try { result=adapter.ExecutePlan(binding,plan); } catch { stop=true; throw; }
                if(result==null || result.State=="UNKNOWN") { stop=true; throw new InvalidOperationException("UNKNOWN breakthrough control outcome"); }
                item["reject_phase"]="execute"; item["execution_state"]=result.State; item["error_code"]=result.ErrorCode; item["result"]=result.Result; Persist();
                Require(result.State=="UNCHANGED" || result.State=="ROLLED_BACK","Out-of-wall cut was incorrectly accepted: "+labels[i]);
                if(result.State=="ROLLED_BACK") Require(result.Result!=null && Object.Equals(result.Result["undo_called"],true) &&
                    Object.Equals(result.Result["geometry_restored"],true) && Object.Equals(result.Result["history_restored"],true),
                    "Breakthrough control lacked verified product Undo");
            } else Require(item.ContainsKey("error_code"),"Null plan without refusal for "+labels[i]);
            item["revision_after"]=binding.RevisionId;
            Require(EqualBody(before,BodyRecord(target)) && EqualBodyInventories(others,OtherBodyRecords(target.Tag)) &&
                M2Json.CanonicalText(history)==M2Json.CanonicalText(HistoryRecord()),"Breakthrough rejection failed to preserve model");
            item["preserved"]=true; Persist();
        }
    }

    static Dictionary<string,object> ActiveNativeDefinition(HolePackage feature,Dictionary<string,object> request)
    {
        string kind=(string)request["kind"], mode=(string)request["depth_mode"];
        Point3d[] origins; Vector3d[] vectors; feature.GetOrigins(out origins); feature.GetDirections(out vectors);
        Require(origins.Length==1 && vectors.Length==1,"Expected one native origin/direction");
        Dictionary<string,object> row=new Dictionary<string,object> { {"kind",kind},{"depth_mode",mode},
            {"position_mm",new double[]{origins[0].X,origins[0].Y,origins[0].Z}},
            {"direction",new double[]{vectors[0].X,vectors[0].Y,vectors[0].Z}} };
        HolePackageBuilder b=part.Features.CreateHolePackageBuilder(feature);
        try {
            row["native_type"]=b.TypeOfHole.ToString(); row["depth_limit"]=b.HoleDepthLimitOption.ToString();
            row["diameter_mm"]=kind=="simple"?b.GeneralSimpleHoleDiameter.Value:kind=="counterbore"?b.GeneralCounterboreHoleDiameter.Value:
                kind=="countersink"?b.GeneralCountersinkHoleDiameter.Value:b.TapDrillDiameter.Value;
            if(mode=="blind") row["hole_depth_mm"]=kind=="simple"?b.GeneralSimpleHoleDepth.Value:kind=="counterbore"?b.GeneralCounterboreHoleDepth.Value:
                kind=="countersink"?b.GeneralCountersinkHoleDepth.Value:b.ThreadedHoleDepth.Value;
            if(kind=="counterbore") { row["counterbore_diameter_mm"]=b.GeneralCounterboreDiameter.Value; row["counterbore_depth_mm"]=b.GeneralCounterboreDepth.Value; }
            if(kind=="countersink") { row["countersink_diameter_mm"]=b.GeneralCountersinkDiameter.Value; row["countersink_angle_deg"]=b.GeneralCountersinkAngle.Value; }
            if(kind=="threaded") {
                row["thread_standard"]=b.ThreadStandard; row["thread_size"]=b.ThreadSize; row["radial_engage"]=b.RadialEngageOption;
                row["thread_rotation"]=b.ThreadRotation.ToString(); row["thread_depth_mm"]=b.ThreadDepth.Value;
                M3HoleRequest req=new M3HoleRequest {ThreadStandard=(string)request["thread_standard"],ThreadSize=(string)request["thread_size"],
                    RadialEngage=(string)request["radial_engage"],PitchMm=(double)request["pitch_mm"],
                    ThreadDepthMm=(double)request["thread_depth_mm"],Hand=(string)request["hand"]};
                Dictionary<string,object> threadRow=new Dictionary<string,object>(); VerifyThreadFeature(feature,req,threadRow);
                Dictionary<string,object> thread=(Dictionary<string,object>)threadRow["thread_feature"];
                thread.Remove("id"); thread.Remove("parents"); thread.Remove("hole_id");
                thread.Remove("hole_timestamp"); thread.Remove("package_timestamp"); thread.Remove("shared_expression_tags");
                thread.Remove("arc_entity_tags"); row["thread"]=thread;
            }
        } finally { b.Destroy(); }
        return row;
    }
    static List<object> RoundtripBodySummaries()
    {
        List<object> rows=new List<object>();
        foreach(Body body in part.Bodies) {
            double[] bounds=new double[6]; UFSession.GetUFSession().Modl.AskBoundingBox(body.Tag,bounds);
            rows.Add(new Dictionary<string,object> { {"bounds_mm",bounds},{"volume_mm3",Volume(body)},
                {"face_count",body.GetFaces().Length},{"edge_count",body.GetEdges().Length} });
        }
        return rows.OrderBy(v=>((double[])((Dictionary<string,object>)v)["bounds_mm"])[0]).ToList();
    }
    static double Volume(Body body)
    {
        Unit[] units={part.UnitCollection.FindObject("Kilogram"),part.UnitCollection.FindObject("MilliMeter"),
            part.UnitCollection.FindObject("SquareMilliMeter"),part.UnitCollection.FindObject("CubicMilliMeter")};
        MeasureBodies m=part.MeasureManager.NewMassProperties(units,0.999999,new Body[]{body});
        try { return m.Volume; } finally { m.Dispose(); }
    }
    static Dictionary<string,object> BodyRecord(Body body)
    {
        double[] bounds=new double[6]; UFSession.GetUFSession().Modl.AskBoundingBox(body.Tag,bounds);
        List<object> faceRows=new List<object>(), edgeRows=new List<object>();
        foreach (Face face in body.GetFaces()) {
            int type,norm; double[] point=new double[3],direction=new double[3],box=new double[6]; double radius,radiusData;
            UFSession.GetUFSession().Modl.AskFaceData(face.Tag,out type,point,direction,box,out radius,out radiusData,out norm);
            faceRows.Add(new Dictionary<string,object> { {"id",face.JournalIdentifier},{"tag",face.Tag.ToString()},
                {"type",type},{"point",point},{"direction",direction},{"bounds",box},
                {"radius",radius},{"radius_data",radiusData},{"normal_sign",norm} });
        }
        foreach (Edge edge in body.GetEdges()) {
            Point3d a,b; edge.GetVertices(out a,out b);
            edgeRows.Add(new Dictionary<string,object> { {"id",edge.JournalIdentifier},{"tag",edge.Tag.ToString()},
                {"type",edge.SolidEdgeType.ToString()},{"length_mm",edge.GetLength()},
                {"start",new double[]{a.X,a.Y,a.Z}},{"end",new double[]{b.X,b.Y,b.Z}} });
        }
        faceRows=faceRows.OrderBy(v=>M2Json.CanonicalText(v),StringComparer.Ordinal).ToList();
        edgeRows=edgeRows.OrderBy(v=>M2Json.CanonicalText(v),StringComparer.Ordinal).ToList();
        return new Dictionary<string,object> { {"body_id",body.JournalIdentifier}, {"body_tag",body.Tag.ToString()}, {"volume_mm3",Volume(body)},
            {"bounds_mm",bounds}, {"faces",faceRows}, {"edges",edgeRows} };
    }
    static List<object> OtherBodyRecords(Tag target)
    {
        List<object> rows=new List<object>();
        foreach (Body body in part.Bodies) if (body.Tag!=target) rows.Add(BodyRecord(body));
        return rows.OrderBy(v=>M2Json.CanonicalText(v),StringComparer.Ordinal).ToList();
    }
    static Dictionary<string,object> ComparableBody(Dictionary<string,object> source)
    {
        Dictionary<string,object> value=new Dictionary<string,object>(source,StringComparer.Ordinal); value.Remove("body_id");
        foreach(string key in new string[]{"faces","edges"}) {
            List<object> normalized=new List<object>();
            foreach(Dictionary<string,object> original in ((List<object>)source[key]).Cast<Dictionary<string,object>>()) {
                Dictionary<string,object> item=new Dictionary<string,object>(original,StringComparer.Ordinal); item.Remove("id"); normalized.Add(item);
            }
            value[key]=normalized.OrderBy(v=>M2Json.CanonicalText(v),StringComparer.Ordinal).ToList();
        }
        return value;
    }
    static bool EqualBody(Dictionary<string,object> a,Dictionary<string,object> b)
    { return M2Json.CanonicalText(ComparableBody(a))==M2Json.CanonicalText(ComparableBody(b)); }
    static bool EqualBodyInventories(List<object> a,List<object> b)
    {
        List<string> x=a.Cast<Dictionary<string,object>>().Select(v=>M2Json.CanonicalText(ComparableBody(v))).OrderBy(v=>v,StringComparer.Ordinal).ToList();
        List<string> y=b.Cast<Dictionary<string,object>>().Select(v=>M2Json.CanonicalText(ComparableBody(v))).OrderBy(v=>v,StringComparer.Ordinal).ToList();
        return x.SequenceEqual(y,StringComparer.Ordinal);
    }
    static List<object> HistoryRecord()
    {
        return part.Features.GetFeatures().Select(f=>(object)new Dictionary<string,object> {
            {"tag",f.Tag.ToString()},{"type",f.FeatureType},{"timestamp",f.Timestamp},
            {"parent_tags",f.GetParents().Select(p=>p.Tag.ToString()).OrderBy(s=>s,StringComparer.Ordinal).ToArray()},
            {"child_tags",f.GetChildren().Select(c=>c.Tag.ToString()).OrderBy(s=>s,StringComparer.Ordinal).ToArray()} })
            .OrderBy(v=>M2Json.CanonicalText(v),StringComparer.Ordinal).ToList();
    }
    static List<object> ExpressionInventory()
    {
        List<object> rows=new List<object>();
        foreach (NXOpen.Expression expression in part.Expressions) {
            Feature owner=expression.GetOwningFeature();
            rows.Add(new Dictionary<string,object> { {"name",expression.Name},{"type",expression.Type.ToString()},
                {"rhs",expression.RightHandSide},{"owner",owner==null ? null : owner.JournalIdentifier},
                {"users",expression.GetUsingFeatures().Select(f=>f.JournalIdentifier).ToArray()} });
        }
        return rows;
    }
    static List<object> ArcInventory()
    {
        return part.Curves.OfType<Arc>().Select(a=>(object)new Dictionary<string,object> {
            {"id",a.JournalIdentifier},{"tag",a.Tag.ToString()},{"radius_mm",a.Radius},
            {"center_mm",new double[]{a.CenterPoint.X,a.CenterPoint.Y,a.CenterPoint.Z}},
            {"start_angle_rad",a.StartAngle},{"end_angle_rad",a.EndAngle} }).OrderBy(a=>M2Json.CanonicalText(a),StringComparer.Ordinal).ToList();
    }
    static Dictionary<string,object> SectionMapping(Section section)
    {
        SectionData[] data; section.GetSectionData(out data);
        List<object> dataRows=new List<object>();
        for(int index=0;index<data.Length;index++) {
            SelectionIntentRule[] dataRules; data[index].GetRules(out dataRules);
            dataRows.Add(new Dictionary<string,object> { {"section_index",index},
                {"rules",dataRules.Select(r=>(object)new Dictionary<string,object> {
                    {"type",r.Type.ToString()},{"handle",r.Handle.ToString()} }).ToList()} });
        }
        SelectionIntentRule[] rules; int[] ruleIndices;
        TaggedObject[] objects=section.AskRulesAndCollectedEntities(out rules,out ruleIndices);
        List<object> collected=new List<object>();
        foreach (TaggedObject item in objects) {
            NXObject nx=item as NXObject; NXOpen.Point point=item as NXOpen.Point;
            Dictionary<string,object> record=new Dictionary<string,object> { {"tag",item.Tag.ToString()},
                {"runtime_type",item.GetType().FullName},{"id",nx==null ? null : nx.JournalIdentifier} };
            if (point!=null) record["coordinates_mm"]=new double[]{point.Coordinates.X,point.Coordinates.Y,point.Coordinates.Z};
            collected.Add(record);
        }
        return new Dictionary<string,object> { {"section_data",dataRows},
            {"collected_rules",rules.Select(r=>(object)new Dictionary<string,object> {
                {"type",r.Type.ToString()},{"handle",r.Handle.ToString()} }).ToList()},
            {"rule_object_array_count",ruleIndices},{"collected_entities",collected} };
    }
}
