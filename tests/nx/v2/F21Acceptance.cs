// F21 native product-adapter acceptance. Only designated NX operator invokes.
// Fixture setup uses existing Sketch adapter; product cases call actual Sweep adapter.
// An UNKNOWN or uncertain operation stops this run; no replay or auto-unfreeze.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NXOpen;
using NXOpen.Features;
using NxSketchMcp.M2;

public static class F21Acceptance
{
    const string Root=@"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    static Session session;static Part part;static M2OpenModelAdapter adapter;static M2ModelBinding binding;
    static string output;static Dictionary<string,object> report;static List<object> cases;static HashSet<string> selected,prerequisites;static bool stop;
    static M3PlanSnapshot twistPlan;static string twistFeatureId;
    static M3PlanSnapshot transportPlan;static string transportFeatureId;static Sketch transportSketch;
    static void Save(){File.WriteAllText(output,M2Json.CanonicalText(report),new UTF8Encoding(false));}
    static void Need(bool condition,string msg){if(!condition)throw new InvalidOperationException(msg);}
    static bool Unknown(Exception error)
    {
        for(Exception e=error;e!=null;e=e.InnerException)
        {
            M2ModelException model=e as M2ModelException;
            if(model!=null && (model.Code=="M3_ANALYSIS_STATE_UNKNOWN" || model.Code=="VERIFY_OUTCOME_UNKNOWN"))return true;
            if(e is V2SweepFactory.ReadStateUnknownException || e is V2SweepGeometry.CleanupException)return true;
            if(e.Message!=null && e.Message.IndexOf("UNKNOWN",StringComparison.OrdinalIgnoreCase)>=0)return true;
        }
        return false;
    }
    static Point3d P(double x,double y,double z){return new Point3d(x,y,z);}
    static object Private(string name,params object[] args){MethodInfo m=typeof(M2OpenModelAdapter).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static,null,new Type[]{typeof(Part)},null);Need(m!=null,"missing reader "+name);return m.Invoke(null,args);}
    static string Inventory()
    {
        bool dirtyBefore=part.IsModified;object baseInventory=Private("F19Inventory",part);
        if(part.IsModified!=dirtyBefore){stop=true;throw new InvalidOperationException("UNKNOWN inventory changed dirty state");}
        List<object> curves=new List<object>();foreach(Curve c in part.Curves.ToArray()){
            Line l=c as Line;Arc a=c as Arc;if(l!=null)curves.Add(new object[]{l.JournalIdentifier,l.Tag.ToString(),"LINE",l.StartPoint.X,l.StartPoint.Y,l.StartPoint.Z,l.EndPoint.X,l.EndPoint.Y,l.EndPoint.Z});
            else if(a!=null){Point3d center;Vector3d x,y;a.GetOrientation(out center,out x,out y);curves.Add(new object[]{a.JournalIdentifier,a.Tag.ToString(),"ARC",center.X,center.Y,center.Z,x.X,x.Y,x.Z,y.X,y.Y,y.Z,a.Radius,a.StartAngle,a.EndAngle});}}
        List<object> bodies=new List<object>();foreach(Body b in part.Bodies){bodies.Add(F21SurfaceStructureReader.Read(b));
            if(part.IsModified!=dirtyBefore){stop=true;throw new InvalidOperationException("UNKNOWN geometry inventory changed dirty state");}}
        if(part.IsModified!=dirtyBefore){stop=true;throw new InvalidOperationException("UNKNOWN geometry inventory changed dirty state");}
        return M2Json.CanonicalText(new object[]{baseInventory,curves,bodies,part.IsModified,session.ActiveSketch==null});
    }
    static M3SketchGeometryRequest Line(string id,double x1,double y1,double x2,double y2){return new M3SketchGeometryRequest{Id=id,Type="line",StartMm=new double[]{x1,y1},EndMm=new double[]{x2,y2}};}
    static List<Dictionary<string,object>> GuideElements(Section section)
    {
        List<Dictionary<string,object>> result=new List<Dictionary<string,object>>();SectionData[] entries;section.GetSectionData(out entries);
        Need(entries!=null,"guide data missing");try{foreach(SectionData entry in entries){SectionElementData[] elements;entry.GetSectionElementsData(out elements);
            Need(elements!=null,"guide elements missing");try{foreach(SectionElementData element in elements){DisplayableObject curve,start,end;Point3d a,b;
                element.GetSectionElementData1(out curve,out start,out a,out end,out b);Need(curve!=null,"missing guide curve");
                result.Add(new Dictionary<string,object>{{"id",curve.JournalIdentifier},{"tag",curve.Tag.ToString()},
                    {"start_mm",new double[]{a.X,a.Y,a.Z}},{"end_mm",new double[]{b.X,b.Y,b.Z}}});}}
            finally{foreach(SectionElementData element in elements)if(element!=null)try{element.Dispose();}catch(Exception ex){throw new InvalidOperationException("UNKNOWN guide element cleanup",ex);}}}}
        finally{foreach(SectionData entry in entries)if(entry!=null)try{entry.Dispose();}catch(Exception ex){throw new InvalidOperationException("UNKNOWN guide data cleanup",ex);}}return result;
    }
    static Sketch FixtureSketch(double offset,bool sheet)
    {
        List<M3SketchGeometryRequest> g=new List<M3SketchGeometryRequest>{Line("a",0,0,3,0),Line("b",3,0,0,2)};
        if(!sheet)g.Add(Line("c",0,2,0,0));
        M3SketchRequest request=new M3SketchRequest{RevisionId=binding.RevisionId,Plane="XY",OriginMm=new double[]{offset,0,0},ConstraintMode="geometry_only",Geometries=g};
        M3PlanSnapshot p=adapter.PrepareSketch(binding,request);M3ExecutionResult e=adapter.ExecutePlan(binding,p);
        if(e==null || e.State=="UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN fixture Sketch execute");}
        Need(e.State=="APPLIED","fixture Sketch failed "+e.ErrorCode);M3VerificationResult v=adapter.VerifyPlan(binding,p);
        if(v==null || v.ErrorCode=="VERIFY_OUTCOME_UNKNOWN" || v.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN fixture Sketch verify");}
        Need(v.Verified,"fixture Sketch verify failed");string id=(string)e.Result["sketch_id"];
        return part.Sketches.ToArray().Single(s=>s.JournalIdentifier==id);
    }
    static M3PlanSnapshot Prepare(Sketch sketch,Curve[] path,string outputType,string mode,double twist)
    {return ((IM3SweepOperationAdapter)adapter).PrepareSweep(binding,new M3SweepRequest{RevisionId=binding.RevisionId,SketchId=sketch.JournalIdentifier,
        PathCurveIds=path.Select(c=>c.JournalIdentifier).ToArray(),ProfileOutput=outputType,OrientationMode=mode,TwistAngleDeg=twist});}
    static M3ExecutionResult Execute(M3PlanSnapshot p){M3ExecutionResult e;try{e=adapter.ExecutePlan(binding,p);}catch{stop=true;throw;}
        if(e==null || e.State=="UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN sweep execute; stopped");}return e;}
    static M3VerificationResult Verify(M3PlanSnapshot p){M3VerificationResult v;try{v=adapter.VerifyPlan(binding,p);}catch{stop=true;throw;}
        if(v==null || v.ErrorCode=="VERIFY_OUTCOME_UNKNOWN" || v.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN sweep verify; stopped");}return v;}
    static void Row(string name,Action<Dictionary<string,object>> action)
    {if(stop||!selected.Contains(name))return;Dictionary<string,object> row=new Dictionary<string,object>{{"case",name},{"role",prerequisites.Contains(name)?"prerequisite":"case"},{"status","RUNNING"}};cases.Add(row);Save();
        try{action(row);string status=Object.Equals(row.ContainsKey("requires_external_oracle")?row["requires_external_oracle"]:null,true)?"CAPTURED_PENDING_ORACLE":"PASS";
            row["status"]=prerequisites.Contains(name)?"PREREQUISITE_"+status:status;}
        catch(Exception e){bool unknown=stop||Unknown(e);row["status"]=unknown?"UNKNOWN_STOPPED":"FAIL";row["error"]=e.ToString();stop=true;}Save();}
    static Body MakeSolidSentinel()
    {
        Point3d[] p={P(-120,-120,0),P(-118,-120,0),P(-118,-117,0),P(-120,-117,0)};Curve[] c=new Curve[4];
        for(int i=0;i<4;i++)c[i]=part.Curves.CreateLine(p[i],p[(i+1)%4]);Section s=part.Sections.CreateSection(.0095,.01,.5);s.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
        CurveDumbRule rule=part.ScRuleFactory.CreateRuleCurveDumb(c);s.AddToSection(new SelectionIntentRule[]{rule},c[0],null,null,p[0],Section.Mode.Create,false);
        ExtrudeBuilder b=part.Features.CreateExtrudeBuilder(null);try{b.Section=s;b.Direction=part.Directions.CreateDirection(P(0,0,0),new Vector3d(0,0,1),SmartObject.UpdateOption.WithinModeling);
            b.Limits.StartExtend.Value.RightHandSide="0";b.Limits.EndExtend.Value.RightHandSide="4";b.BooleanOperation.Type=NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Create;
            b.BooleanOperation.SetTargetBodies(new Body[]{null});Feature f=b.CommitFeature();return f.GetBodies().Single();}finally{b.Destroy();}
    }
    public static void Main(string[] args)
    {
        string run=Guid.NewGuid().ToString("N"),model=Path.Combine(Root,"workspace","v2","models","F21-native-"+run+".prt");
        output=Path.Combine(Root,"workspace","v2","evidence","f21-native-"+run+".json");cases=new List<object>();
        report=new Dictionary<string,object>{{"run_id",run},{"scope","native production adapter; independent sampled capture pending external oracle; not installed MCP"},
            {"model_path",model},{"assembly",typeof(M2OpenModelAdapter).Assembly.Location},{"cases",cases},{"passed",false},{"started_utc",DateTime.UtcNow.ToString("o")}};Save();
        try{
            string[] names={"straight_transport","legacy_write_guard","query_faces_with_swept","same_ids_reverse_direction_control","straight_twist","fresh_native_tamper","chain_transport","chain_twist","sheet","postcommit_failure_real_undo","stale","active_sketch_rejected","native_guide_reverse_control"};
            string filter=Environment.GetEnvironmentVariable("F21_CASES");selected=new HashSet<string>(String.IsNullOrWhiteSpace(filter)?names:filter.Split(','),StringComparer.Ordinal);
            Need(selected.Count>0&&selected.All(x=>names.Contains(x)),"F21_CASES must contain valid names");
            string setup=Environment.GetEnvironmentVariable("F21_PREREQUISITE_CASES");prerequisites=new HashSet<string>(String.IsNullOrWhiteSpace(setup)?new string[0]:setup.Split(','),StringComparer.Ordinal);
            string[] modeled={"straight_transport","straight_twist","chain_transport","chain_twist","sheet"};
            Need(prerequisites.All(x=>selected.Contains(x)&&modeled.Contains(x))&&prerequisites.Count<selected.Count,"prerequisites must be selected setup modeling cases with at least one counted case");
            Need(!selected.Contains("fresh_native_tamper")||selected.Contains("straight_twist"),"fresh tamper requires straight_twist in this session");
            Need((!selected.Contains("same_ids_reverse_direction_control")&&!selected.Contains("native_guide_reverse_control")&&!selected.Contains("legacy_write_guard")&&!selected.Contains("query_faces_with_swept"))||selected.Contains("straight_transport"),"direction and old-write controls require straight_transport in this session");
            report["selected_cases"]=selected.Where(x=>!prerequisites.Contains(x)).ToArray();report["prerequisite_cases"]=prerequisites.ToArray();
            session=Session.GetSession();Need(session.Parts.Work==null&&!session.IsInTaskEnvironment&&session.ActiveSketch==null,"empty session required");
            part=session.Parts.NewDisplay(model,Part.Units.Millimeters);Body sentinel=MakeSolidSentinel();
            Line old=part.Curves.CreateLine(P(-80,-80,0),P(-77,-75,2));
            Curve[][] paths=new Curve[7][];
            for(int i=0;i<7;i++){
                double x=i*40.0;Line first=part.Curves.CreateLine(P(x,0,0),P(x,0,5));
                if(i==2||i==3){Arc arc=part.Curves.CreateArc(P(x+10,0,5),new Vector3d(-1,0,0),new Vector3d(0,0,1),10,0,Math.PI/2);paths[i]=new Curve[]{first,arc};}
                else paths[i]=new Curve[]{first};
            }
            using(PartSaveStatus s=part.Save(BasePart.SaveComponents.True,BasePart.CloseAfterSave.False))Need(s.NumberUnsavedParts==0&&s.NumberUnsavedObjects==0&&!part.IsModified,"fixture save failed");
            adapter=new M2OpenModelAdapter();binding=adapter.Takeover(model,"saved_independent_copy","f21-native-"+run,"f21-load-"+run,1);
            // Every prior Sketch is built before any Swept exists: legacy writes
            // are intentionally blocked once a native SKIN is present.
            Sketch[] sketches=new Sketch[8];for(int i=0;i<sketches.Length;i++)sketches[i]=FixtureSketch(i*40.0,i==4);
            M3SpatialCurveRequest oldRequest=new M3SpatialCurveRequest{RevisionId=binding.RevisionId,CurveType="line",StartMm=new double[]{400,0,0},EndMm=new double[]{400,0,5}};
            M3PlanSnapshot oldPlan=((IM3SpatialCurveOperationAdapter)adapter).PrepareSpatialCurve(binding,oldRequest);
            string[] success={"straight_transport","straight_twist","chain_transport","chain_twist","sheet"};
            for(int i=0;i<success.Length;i++){int index=i;Row(success[i],delegate(Dictionary<string,object> row){
                bool sheet=index==4;string mode=(index==1||index==3)?"linear_twist":"transport";double twist=mode=="linear_twist"?45:0;
                Sketch sketch=sketches[index];string before=Inventory(),revision=binding.RevisionId;
                M3PlanSnapshot plan=Prepare(sketch,paths[index],sheet?"sheet":"solid",mode,twist);row["plan"]=plan.PublicView;row["before_prepare"]=before;row["after_prepare"]=Inventory();
                Need(before==(string)row["after_prepare"]&&revision==binding.RevisionId,"prepare changed state");
                M3ExecutionResult e=Execute(plan);row["execution_state"]=e.State;row["error_code"]=e.ErrorCode;row["execution_result"]=e.Result;Save();Need(e.State=="APPLIED","sweep execution failed");
                M3VerificationResult v=Verify(plan);row["verified"]=v.Verified;row["fresh_checks"]=v.Checks;Need(v.Verified,"fresh verification failed");
                if(index==0){transportPlan=plan;transportFeatureId=(string)e.Result["feature_id"];transportSketch=sketch;}
                if(index==1){twistPlan=plan;twistFeatureId=(string)e.Result["feature_id"];}
                Dictionary<string,object> definition=(Dictionary<string,object>)e.Result["native_definition"];string id=(string)definition["body_id"];
                Body body=part.Bodies.ToArray().Single(b=>b.JournalIdentifier==id);
                bool dirtyBeforeCapture=part.IsModified;string inventoryBeforeCapture=Inventory();
                if(!sheet)row["independent_capture"]=F21IndependentBodyCapture.Capture(body,index>=2?"line_arc":"straight",mode,twist,new double[]{index*40.0,0,0});
                else {Dictionary<string,object> measurement;row["independent_sheet"]=F21StraightGeometryReader.Read(part,body,out measurement);row["measurement"]=measurement;}
                if(part.IsModified!=dirtyBeforeCapture || inventoryBeforeCapture!=Inventory()){stop=true;throw new InvalidOperationException("UNKNOWN independent geometry capture changed state");}
                if(index==0){
                    string beforeReads=Inventory(),beforeRevision=binding.RevisionId;
                    Dictionary<string,object> first=adapter.InspectPart(binding);string revision1=binding.RevisionId;
                    Dictionary<string,object> second=adapter.InspectPart(binding);string revision2=binding.RevisionId;
                    row["repeat_read_revision"] = new Dictionary<string,object>{{"before",beforeRevision},{"first",revision1},{"second",revision2},
                        {"first_result_revision",first.ContainsKey("revision_id")?first["revision_id"]:null},
                        {"second_result_revision",second.ContainsKey("revision_id")?second["revision_id"]:null},
                        {"inventory_before",beforeReads},{"inventory_after",Inventory()}};
                    Need(beforeRevision==revision1&&revision1==revision2&&beforeReads==(string)((Dictionary<string,object>)row["repeat_read_revision"])["inventory_after"],
                        "repeated read changed revision or inventory");
                }
                row["independent_oracle_status"]="PENDING_EXTERNAL";row["requires_external_oracle"]=true;Save();
            });
            if(index==0)Row("legacy_write_guard",delegate(Dictionary<string,object> row){
                string before=Inventory(),revision=binding.RevisionId,code=null;
                M3ExecutionResult oldResult=Execute(oldPlan);row["old_plan_state"]=oldResult.State;row["old_plan_error"]=oldResult.ErrorCode;
                Need(oldResult.State=="UNCHANGED"&&(oldResult.ErrorCode=="STALE_PLAN"||oldResult.ErrorCode=="MODEL_READ_FAILED"),"old plan was not rejected");
                try{((IM3SpatialCurveOperationAdapter)adapter).PrepareSpatialCurve(binding,new M3SpatialCurveRequest{RevisionId=binding.RevisionId,CurveType="line",StartMm=new double[]{410,0,0},EndMm=new double[]{410,0,5}});}
                catch(M2ModelException ex){if(Unknown(ex)){stop=true;throw;}code=ex.Code;}
                row["new_old_write_error"]=code;row["before"]=before;row["after"]=Inventory();
                Need(code=="UNSUPPORTED_SWEEP_PRESERVATION"&&before==(string)row["after"]&&revision==binding.RevisionId,"legacy write guard failed");
            });
            if(index==0)Row("query_faces_with_swept",delegate(Dictionary<string,object> row){
                string before=Inventory(),revision=binding.RevisionId;
                Dictionary<string,object> result=((IM4FaceQueryAdapter)adapter).QueryFaces(binding,revision,"all_planar",sentinel.JournalIdentifier);
                row["result"]=result;row["before"]=before;row["after"]=Inventory();
                Need(Object.Equals(result["selection_status"],"all")&&Convert.ToInt32(result["candidate_count"])==6&&
                    before==(string)row["after"]&&revision==binding.RevisionId,"face query blocked, incomplete, or changed state");
            });
            if(index==0)Row("same_ids_reverse_direction_control",delegate(Dictionary<string,object> row){
                Need(transportPlan!=null,"transport prerequisite missing");
                object state=transportPlan.AdapterState;FieldInfo sf=state.GetType().GetField("F21Sweep",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
                Need(sf!=null,"sweep state missing");object sweep=sf.GetValue(state);FieldInfo pf=sweep.GetType().GetField("ProfileFingerprint",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
                Need(pf!=null,"profile fingerprint missing");string fingerprint=(string)pf.GetValue(sweep);
                Swept feature=part.Features.GetFeatures().OfType<Swept>().Single(f=>f.JournalIdentifier==transportFeatureId);
                string before=Inventory();double[][] forward={new double[]{0,0,0},new double[]{0,0,5}};
                Dictionary<string,object> good=V2SweepFactory.ReadIsolated(session,part,feature,transportSketch,paths[0],forward,fingerprint,delegate{return Inventory();});
                string failure=null;try{V2SweepFactory.ReadIsolated(session,part,feature,transportSketch,paths[0],new double[][]{forward[1],forward[0]},fingerprint,delegate{return Inventory();});}
                catch(V2SweepFactory.ReadStateUnknownException){stop=true;throw;}
                catch(InvalidOperationException ex){failure=ex.Message;}
                row["positive"]=good;row["same_path_ids"]=paths[0].Select(c=>c.JournalIdentifier).ToArray();row["reversed_points"]=new double[][]{forward[1],forward[0]};
                row["negative_error"]=failure;row["before"]=before;row["after"]=Inventory();
                Need(failure!=null&&failure.Contains("direction or continuity")&&before==(string)row["after"],"same-id reversed direction was not rejected without changes");
            });
            if(index==1)Row("fresh_native_tamper",delegate(Dictionary<string,object> row){
                Need(twistPlan!=null && Verify(twistPlan).Verified,"fresh control failed");
                Swept feature=part.Features.GetFeatures().OfType<Swept>().Single(f=>f.JournalIdentifier==twistFeatureId);
                string revision=binding.RevisionId;Session.UndoMarkId mark=session.SetUndoMark(Session.MarkVisibility.Invisible,"F21 native tamper");
                SweptBuilder builder=null;
                try{builder=part.Features.CreateSweptBuilder(feature);row["old_native_end_formula"]=builder.OrientationMethod.AngularLaw.EndValue.RightHandSide;
                    builder.OrientationMethod.AngularLaw.EndValue.RightHandSide="-30";builder.CommitFeature();row["tamper_commit_returned"]=true;Save();}
                catch{stop=true;throw;}
                finally{if(builder!=null)try{builder.Destroy();}catch{stop=true;throw;}}
                int errors;try{errors=session.UpdateManager.DoUpdate(mark);session.DeleteUndoMark(mark,"F21 native tamper");}catch{stop=true;throw;}
                if(errors!=0){stop=true;throw new InvalidOperationException("UNKNOWN native law edit update");}
                string before=Inventory();row["before_verify"]=before;row["requested_native_end_formula"]="-30";Save();
                M3VerificationResult v=Verify(twistPlan);row["verified"]=v.Verified;row["checks"]=v.Checks;
                row["error_code"]=v.ErrorCode;row["before_verify"]=before;row["after_verify"]=Inventory();row["revision_before_edit"]=revision;row["revision_after_verify"]=binding.RevisionId;
                Dictionary<string,object> geometryCheck=v.Checks==null?null:v.Checks["sweep_geometry"] as Dictionary<string,object>;
                Need(!v.Verified && v.ErrorCode=="PART_VERIFICATION_FAILED" && geometryCheck!=null && Object.Equals(geometryCheck["status"],"MISMATCH") &&
                    before==(string)row["after_verify"] && revision!=binding.RevisionId,"fresh verification did not return explicit mismatch or changed native edit");
            });}
            Row("postcommit_failure_real_undo",delegate(Dictionary<string,object> row){
                Sketch sketch=sketches[5];string before=Inventory(),revision=binding.RevisionId;M3PlanSnapshot p=Prepare(sketch,paths[5],"solid","transport",0);
                FieldInfo hook=p.AdapterState.GetType().GetField("ForcePostCommitVerificationFailureForTesting",BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance);
                Need(hook!=null,"fault hook missing");hook.SetValue(p.AdapterState,true);M3ExecutionResult e=Execute(p);
                row["state"]=e.State;row["error_code"]=e.ErrorCode;row["result"]=e.Result;row["before"]=before;row["after"]=Inventory();
                Need(e.State=="ROLLED_BACK" && e.Result!=null && Object.Equals(e.Result["undo_called"],true) && Object.Equals(e.Result["operation_exception_code"],"F21_FORCED_POST_COMMIT_FAILURE") && before==(string)row["after"] && revision!=binding.RevisionId,"real postcommit Undo mismatch");
            });
            Row("stale",delegate(Dictionary<string,object> row){
                Sketch sketch=sketches[6];M3PlanSnapshot p=Prepare(sketch,paths[6],"solid","transport",0);
                Session.UndoMarkId mark=session.SetUndoMark(Session.MarkVisibility.Invisible,"F21 external edit");int errors;
                try{old.SetEndPoint(P(-76,-75,2));errors=session.UpdateManager.DoUpdate(mark);session.DeleteUndoMark(mark,"F21 external edit");}
                catch{stop=true;throw;}if(errors!=0){stop=true;throw new InvalidOperationException("UNKNOWN external edit update");}
                string before=Inventory();M3ExecutionResult e=Execute(p);row["state"]=e.State;row["error_code"]=e.ErrorCode;row["before"]=before;row["after"]=Inventory();
                Need(e.State=="UNCHANGED"&&e.ErrorCode=="STALE_PLAN"&&before==(string)row["after"],"stale plan changed state");
            });
            Row("active_sketch_rejected",delegate(Dictionary<string,object> row){
                Sketch active=sketches[7];try{active.Activate(Sketch.ViewReorient.False);}catch{stop=true;throw;}Need(session.ActiveSketch!=null,"fixture not active");
                string before=Inventory(),revision=binding.RevisionId,code=null;try{Prepare(active,paths[0],"solid","transport",0);}
                catch(M2ModelException ex){if(Unknown(ex)){stop=true;throw;}code=ex.Code;}
                row["error_code"]=code;row["before"]=before;row["after"]=Inventory();Need(code=="NX_BUSY"&&before==(string)row["after"]&&binding.RevisionId==revision&&session.ActiveSketch!=null,"active Sketch rejection failed");
                try{active.Deactivate(Sketch.ViewReorient.False,Sketch.UpdateLevel.Model);}catch{stop=true;throw;}
            });
            // Deliberately last: the native direction mutation invalidates the
            // prior Sweep and no subsequent successful cases may depend on it.
            Row("native_guide_reverse_control",delegate(Dictionary<string,object> row){
                Need(transportPlan!=null,"transport prerequisite missing");object state=transportPlan.AdapterState;
                FieldInfo sf=state.GetType().GetField("F21Sweep",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
                Need(sf!=null,"sweep state missing");object sweep=sf.GetValue(state);FieldInfo pf=sweep.GetType().GetField("ProfileFingerprint",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
                Need(pf!=null,"profile fingerprint missing");string fingerprint=(string)pf.GetValue(sweep);
                Swept feature=part.Features.GetFeatures().OfType<Swept>().Single(f=>f.JournalIdentifier==transportFeatureId);
                string before=Inventory();row["inventory_before_edit"]=before;Save();
                SweptBuilder builder=null;Session.UndoMarkId mark=session.SetUndoMark(Session.MarkVisibility.Invisible,"F21 guide reverse control");
                try{
                    builder=part.Features.CreateSweptBuilder(feature);Section[] guides=builder.GuideList.GetContents();Need(guides!=null&&guides.Length==1,"guide missing");
                    List<Dictionary<string,object>> original=GuideElements(guides[0]);row["original_guide"]=original;
                    Need(original.Count==1 && Object.Equals(original[0]["id"],paths[0][0].JournalIdentifier),"original guide membership mismatch");
                    guides[0].ReverseDirection();List<Dictionary<string,object>> reversed=GuideElements(guides[0]);row["reversed_before_commit"]=reversed;Save();
                    Need(reversed.Count==1 && Object.Equals(original[0]["id"],reversed[0]["id"]) &&
                        M2Json.CanonicalText(original[0]["start_mm"])==M2Json.CanonicalText(reversed[0]["end_mm"]) &&
                        M2Json.CanonicalText(original[0]["end_mm"])==M2Json.CanonicalText(reversed[0]["start_mm"]),"native guide did not reverse same member");
                    builder.CommitFeature();row["commit_returned"]=true;Save();
                }catch{stop=true;throw;}finally{if(builder!=null)try{builder.Destroy();}catch{stop=true;throw;}}
                int update=session.UpdateManager.DoUpdate(mark);session.DeleteUndoMark(mark,"F21 guide reverse control");
                if(update!=0){stop=true;throw new InvalidOperationException("native guide reverse update outcome uncertain");}
                string afterCommit=Inventory();row["inventory_after_commit"]=afterCommit;Save();
                feature=part.Features.GetFeatures().OfType<Swept>().Single(f=>f.JournalIdentifier==transportFeatureId);
                builder=null;List<Dictionary<string,object>> committed;
                try{builder=part.Features.CreateSweptBuilder(feature);Section[] guides=builder.GuideList.GetContents();Need(guides!=null&&guides.Length==1,"committed guide missing");committed=GuideElements(guides[0]);}
                catch{stop=true;throw;}
                finally{if(builder!=null)try{builder.Destroy();}catch{stop=true;throw;}}
                row["committed_guide"]=committed;row["inventory_after_inspect"]=Inventory();
                if(afterCommit!=(string)row["inventory_after_inspect"]){stop=true;throw new InvalidOperationException("guide inspection changed state");}
                List<Dictionary<string,object>> originalGuide=(List<Dictionary<string,object>>)row["original_guide"];
                Need(committed.Count==1&&Object.Equals(committed[0]["id"],paths[0][0].JournalIdentifier)&&
                    M2Json.CanonicalText(originalGuide[0]["start_mm"])==M2Json.CanonicalText(committed[0]["end_mm"])&&
                    M2Json.CanonicalText(originalGuide[0]["end_mm"])==M2Json.CanonicalText(committed[0]["start_mm"]),"committed guide did not preserve reversed same member");
                string failure=null;try{V2SweepFactory.ReadIsolated(session,part,feature,transportSketch,paths[0],new double[][]{new double[]{0,0,0},new double[]{0,0,5}},fingerprint,delegate{return Inventory();});}
                catch(V2SweepFactory.ReadStateUnknownException){stop=true;throw;}catch(InvalidOperationException ex){failure=ex.Message;}
                row["reader_error"]=failure;row["inventory_after_reader"]=Inventory();
                Need(failure!=null&&failure.Contains("direction or continuity")&&afterCommit==(string)row["inventory_after_reader"],"reader missed native guide reverse or altered model");
            });
            report["capture_completed"]=!stop && cases.Count==selected.Count;report["passed"]=false;
            report["completed_utc"]=DateTime.UtcNow.ToString("o");Save();
        }catch(Exception ex){report["fatal"]=ex.ToString();report["passed"]=false;report["unknown_or_stopped"]=stop||Unknown(ex);Save();}
    }
}
