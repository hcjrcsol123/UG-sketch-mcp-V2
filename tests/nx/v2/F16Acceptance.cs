// F16 production adapter controls: one geometric witness, forced post-commit
// rollback, and definition tamper. Other F16 cases are covered by MCP smoke.
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

public static class F16Acceptance
{
    const string Root = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    static Dictionary<string,object> report;
    static List<object> cases;
    static string reportPath;
    static Session session;
    static Part part;
    static M2OpenModelAdapter adapter;
    static M2ModelBinding binding;
    static bool stop;
    static void Persist() { File.WriteAllText(reportPath, M2Json.CanonicalText(report), new UTF8Encoding(false)); }
    static void Require(bool value,string reason) { if(!value) throw new InvalidOperationException(reason); }
    static Body Block(double x,double length)
    {
        BlockFeatureBuilder b=part.Features.CreateBlockFeatureBuilder(null);
        try { b.SetOriginAndLengths(new Point3d(x,0,0),length.ToString(CultureInfo.InvariantCulture),"12","10");
            b.SetBooleanOperationAndTarget(Feature.BooleanType.Create,null); return b.CommitFeature().GetBodies().Single(); }
        finally { b.Destroy(); }
    }
    static Dictionary<string,object> BodyData(Body b)
    {
        double[] bounds=new double[6]; UFSession.GetUFSession().Modl.AskBoundingBox(b.Tag,bounds);
        Unit[] units=new Unit[]{part.UnitCollection.FindObject("Kilogram"),part.UnitCollection.FindObject("MilliMeter"),
            part.UnitCollection.FindObject("SquareMilliMeter"),part.UnitCollection.FindObject("CubicMilliMeter")};
        double volume; using(MeasureBodies m=part.MeasureManager.NewMassProperties(units,0.999999,new Body[]{b})) volume=m.Volume;
        return new Dictionary<string,object>{{"id",b.JournalIdentifier},{"tag",b.Tag.ToString()}, {"volume_mm3",volume},
            {"bbox_mm",bounds},{"faces",b.GetFaces().Length},{"edges",b.GetEdges().Length}};
    }
    static Dictionary<string,object> Snapshot()
    {
        return new Dictionary<string,object>{{"bodies",part.Bodies.ToArray().OrderBy(b=>b.JournalIdentifier,StringComparer.Ordinal).Select(b=>(object)BodyData(b)).ToArray()},
            {"features",part.Features.GetFeatures().Select(f=>(object)new Dictionary<string,object>{{"id",f.JournalIdentifier},{"type",f.FeatureType}}).ToArray()}};
    }
    static Edge SelectEdge(Body body,double x,double y)
    {
        List<Edge> found=new List<Edge>();
        foreach(Edge e in body.GetEdges()) {
            Point3d a,b; e.GetVertices(out a,out b);
            if(Math.Abs(a.X-x)<1e-6 && Math.Abs(b.X-x)<1e-6 && Math.Abs(a.Y-y)<1e-6 && Math.Abs(b.Y-y)<1e-6 &&
               Math.Abs(Math.Abs(a.Z-b.Z)-10)<1e-6) found.Add(e);
        }
        Require(found.Count==1,"expected unique selected Z edge"); return found[0];
    }
    static M3PlanSnapshot Prepare(Body target, Edge edge)
    { return adapter.PrepareEdgeBlend(binding,new M3EdgeBlendRequest{RevisionId=binding.RevisionId,
        TargetBodyId=target.JournalIdentifier, EdgeIds=new string[]{edge.JournalIdentifier}, RadiusMm=2.0}); }
    static M3ExecutionResult Execute(M3PlanSnapshot plan)
    {
        M3ExecutionResult r;
        try { r=adapter.ExecutePlan(binding,plan); } catch { stop=true; throw; }
        if(r==null || r.State=="UNKNOWN") { stop=true; throw new InvalidOperationException("UNKNOWN execution; no further NX calls"); }
        return r;
    }
    static void Row(string name,Action<Dictionary<string,object>> action)
    {
        if(stop) return;
        string selection=Environment.GetEnvironmentVariable("F16_NATIVE_CASES");
        if(!String.IsNullOrWhiteSpace(selection) && !selection.Split(',').Contains(name)) return;
        Dictionary<string,object> row=new Dictionary<string,object>{{"case",name},{"status","RUNNING"},
            {"reproduction","Run this harness with the recorded candidate in a fresh disposable part."},
            {"retest","Correct the failure, retain this report, then rerun this case on a newly created fixture."}};
        cases.Add(row); Persist();
        try { action(row); row["status"]="PASS"; }
        catch(Exception ex) { row["status"]=stop?"UNKNOWN_STOPPED":"FAIL"; row["error"]=ex.ToString(); stop=true; }
        Persist();
    }
    public static void Main(string[] args)
    {
        string run=Guid.NewGuid().ToString("N");
        string path=Path.Combine(Root,"workspace","v2","models","F16-native-"+run+".prt");
        reportPath=Path.Combine(Root,"workspace","v2","evidence","f16-native-"+run+".json");
        cases=new List<object>(); report=new Dictionary<string,object>{{"scope","F16 production adapter single-edge, true Undo and fresh negative; not installed acceptance"},
            {"model_path",path},{"cases",cases},{"run_id",run},{"started_utc",DateTime.UtcNow.ToString("o")},
            {"adapter_assembly",typeof(M2OpenModelAdapter).Assembly.Location},{"selected_cases",Environment.GetEnvironmentVariable("F16_NATIVE_CASES")},{"passed",false}};
        Persist();
        try {
            session=Session.GetSession(); Require(session.Parts.Work==null && !session.IsInTaskEnvironment && session.ActiveSketch==null,"Requires empty session");
            part=session.Parts.NewDisplay(path,Part.Units.Millimeters);
            Body target=Block(0,20), undoTarget=Block(40,20), sentinel=Block(80,4);
            Edge edge=SelectEdge(target,0,0), undoEdge=SelectEdge(undoTarget,40,0);
            string selectedTag=edge.Tag.ToString(), selectedId=edge.JournalIdentifier;
            using(PartSaveStatus save=part.Save(BasePart.SaveComponents.True,BasePart.CloseAfterSave.False)) { }
            adapter=new M2OpenModelAdapter(); binding=adapter.Takeover(path,"saved_independent_copy","f16-native-"+run,"f16-load-"+run,1);
            M3PlanSnapshot applied=null; string appliedFeature=null;
            Row("native_single_edge_and_fresh",delegate(Dictionary<string,object> row) {
                row["before"]=Snapshot(); row["input_edge_id"]=selectedId; row["input_edge_tag"]=selectedTag;
                row["input_endpoints_mm"]=new double[][]{new double[]{0,0,0},new double[]{0,0,10}};
                applied=Prepare(target,edge); row["plan"]=applied.PublicView; Persist();
                M3ExecutionResult r=Execute(applied); row["execution_state"]=r.State; row["execution"]=r.Result; row["error_code"]=r.ErrorCode; row["error_message"]=r.ErrorMessage; Persist();
                Require(r.State=="APPLIED","Expected APPLIED");
                M3VerificationResult v; try {v=adapter.VerifyPlan(binding,applied);} catch {stop=true;throw;}
                if(v==null || v.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN" || v.ErrorCode=="VERIFY_OUTCOME_UNKNOWN") {stop=true;throw new InvalidOperationException("UNKNOWN verification");}
                row["verified"]=v.Verified;row["verify_checks"]=v.Checks;row["verify_result"]=v.Result;Require(v.Verified,"fresh verification failed");
                appliedFeature=Convert.ToString(r.Result["feature_id"],CultureInfo.InvariantCulture);
                EdgeBlend feature=part.Features.GetFeatures().OfType<EdgeBlend>().Single(f=>f.JournalIdentifier==appliedFeature);
                Dictionary<string,object> definition=V2EdgeBlendFactory.Read(part,feature); row["native_readback"]=definition;
                Require(Math.Abs((double)definition["radius_mm"]-2.0)<1e-6 && Object.Equals(definition["radius_units"],"MilliMeter") && Object.Equals(definition["section"],"Circular"),"native radius or section mismatch");
                string[] tags=definition["edge_tags"] as string[];Require(tags!=null && tags.Length==1 && tags[0]==selectedTag && Object.Equals(definition["rule_edges_match_collector"],true),"native rule does not retain the selected input edge");
                Body result=feature.GetBodies().Single(); row["result"]=BodyData(result);row["after"]=Snapshot();
                double volume=Convert.ToDouble(((Dictionary<string,object>)row["result"])["volume_mm3"],CultureInfo.InvariantCulture);
                double expected=2400.0-10.0*(4.0-Math.PI);row["expected_volume_mm3"]=expected;
                Require(Math.Abs(volume-expected)<1e-5,"single-edge analytic volume mismatch");
                int outside,inside;UFSession.GetUFSession().Modl.AskPointContainment(new double[]{0.2,0.2,5},result.Tag,out outside);
                UFSession.GetUFSession().Modl.AskPointContainment(new double[]{1,1,5},result.Tag,out inside);
                row["point_containment"]=new int[]{outside,inside};Require(outside==2 && inside==1,"corner witness mismatch");
                int found=0; foreach(Face face in result.GetFaces()) {int type,n;double radius,rd;double[] p=new double[3],d=new double[3],b=new double[6];
                    UFSession.GetUFSession().Modl.AskFaceData(face.Tag,out type,p,d,b,out radius,out rd,out n);
                    if(type==UFConstants.UF_MODL_CYLINDRICAL_FACE && Math.Abs(radius-2)<1e-6 && Math.Abs(p[0]-2)<1e-6 && Math.Abs(p[1]-2)<1e-6 && Math.Abs(Math.Abs(d[2])-1)<1e-9) {
                        found++;row["cylinder"]=new Dictionary<string,object>{{"radius_mm",radius},{"axis_point_mm",p},{"axis_direction",d},{"bounds_mm",b},{"edge_count",face.GetEdges().Length}};
                        Require(face.GetEdges().Length==4,"cylinder trimming boundaries differ"); }
                } Require(found==1,"expected one cylinder at selected corner"); row["sentinel"]=BodyData(sentinel);
            });
            Row("forced_post_commit_real_undo",delegate(Dictionary<string,object> row) {
                Dictionary<string,object> before=Snapshot();string rev=binding.RevisionId;row["before"]=before;row["revision_before"]=rev;
                M3PlanSnapshot p=Prepare(undoTarget,undoEdge);
                FieldInfo hook=p.AdapterState.GetType().GetField("ForcePostCommitVerificationFailureForTesting",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
                Require(hook!=null && hook.FieldType==typeof(bool),"missing failure injection hook");hook.SetValue(p.AdapterState,true);
                row["forced_post_commit_failure"]=true;Persist(); M3ExecutionResult r=Execute(p);
                row["state"]=r.State;row["error_code"]=r.ErrorCode;row["result"]=r.Result;row["revision_after"]=binding.RevisionId;Persist();
                Require(r.State=="ROLLED_BACK" && binding.RevisionId!=rev,"expected verified rollback and advanced revision");
                Dictionary<string,object> after=Snapshot();row["after"]=after;
                Require(M2Json.CanonicalText(before)==M2Json.CanonicalText(after),"Undo failed to restore body and history snapshot");
                Require(r.Result!=null && r.Result.ContainsKey("undo_called") && Object.Equals(r.Result["undo_called"],true),"actual Undo not evidenced");
            });
            Row("tamper_native_radius_fresh_negative",delegate(Dictionary<string,object> row) {
                M3VerificationResult control;try{control=adapter.VerifyPlan(binding,applied);}catch{stop=true;throw;}
                if(control==null || control.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN" || control.ErrorCode=="VERIFY_OUTCOME_UNKNOWN") {stop=true;throw new InvalidOperationException("UNKNOWN control verification");}
                row["pre_tamper_verified"]=control.Verified;row["pre_tamper_checks"]=control.Checks;Require(control.Verified,"fresh positive control failed");
                EdgeBlend f=part.Features.GetFeatures().OfType<EdgeBlend>().Single(x=>x.JournalIdentifier==appliedFeature);
                EdgeBlendBuilder edit=part.Features.CreateEdgeBlendBuilder(f);
                try {ScCollector collector;EdgeBlendBuilder.Section section;EdgeBlendBuilder.Conic conic;EdgeBlendBuilder.Rhotype rho;Expression radius,center,rhoValue;
                    edit.GetChainsetAndSectionValue(0,out collector,out section,out conic,out rho,out radius,out center,out rhoValue);
                    row["radius_before_mm"]=radius.Value;radius.RightHandSide="2.5";edit.CommitFeature();}
                finally {edit.Destroy();}
                Dictionary<string,object> changed=V2EdgeBlendFactory.Read(part,f);row["edited_native_definition"]=changed;
                Require(Math.Abs((double)changed["radius_mm"]-2.5)<1e-6,"radius tamper did not persist");
                M3VerificationResult v;try{v=adapter.VerifyPlan(binding,applied);}catch{stop=true;throw;}
                if(v==null || v.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN" || v.ErrorCode=="VERIFY_OUTCOME_UNKNOWN") {stop=true;throw new InvalidOperationException("UNKNOWN tamper verification");}
                row["verified"]=v.Verified;row["checks"]=v.Checks;Require(!v.Verified,"tampered definition incorrectly verified");
            });
            report["passed"]=!stop && cases.Count==3;
        } catch(Exception ex) {report["fatal"]=ex.ToString();stop=true;}
        finally {report["writes_halted"]=stop;report["ended_utc"]=DateTime.UtcNow.ToString("o");Persist();}
    }
}
