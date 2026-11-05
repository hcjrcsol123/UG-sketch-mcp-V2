// F15 production adapter controls: one geometric witness, forced post-commit
// rollback, and definition tamper. Other F15 cases are covered by MCP smoke.
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

public static class F15Acceptance
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
        try { b.SetOriginAndLengths(new Point3d(x,0,0),length.ToString(CultureInfo.InvariantCulture),"10","10");
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
    static M3PlanSnapshot Prepare(Body target, Body tool, string operation, bool retain)
    { return adapter.PrepareBoolean(binding,new M3BooleanRequest{RevisionId=binding.RevisionId,Operation=operation,
        TargetBodyId=target.JournalIdentifier, ToolBodyIds=new string[]{tool.JournalIdentifier},RetainTools=retain}); }
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
        string path=Path.Combine(Root,"workspace","v2","models","F15-native-"+run+".prt");
        reportPath=Path.Combine(Root,"workspace","v2","evidence","f15-native-"+run+".json");
        cases=new List<object>(); report=new Dictionary<string,object>{{"scope","F15 production adapter partial native controls; not complete acceptance"},
            {"model_path",path},{"cases",cases},{"run_id",run},{"started_utc",DateTime.UtcNow.ToString("o")},
            {"adapter_assembly",typeof(M2OpenModelAdapter).Assembly.Location},{"passed",false}};
        Persist();
        try {
            session=Session.GetSession(); Require(session.Parts.Work==null && !session.IsInTaskEnvironment && session.ActiveSketch==null,"Requires empty session");
            part=session.Parts.NewDisplay(path,Part.Units.Millimeters);
            Body target=Block(0,10), tool=Block(5,10), undoTarget=Block(40,10), undoTool=Block(45,10), sentinel=Block(80,10);
            using(PartSaveStatus save=part.Save(BasePart.SaveComponents.True,BasePart.CloseAfterSave.False)) { }
            adapter=new M2OpenModelAdapter(); binding=adapter.Takeover(path,"saved_independent_copy","f15-native-"+run,"f15-load-"+run,1);
            M3PlanSnapshot applied=null;
            Row("product_subtract_witness_and_fresh",delegate(Dictionary<string,object> row) {
                row["before"]=Snapshot(); applied=Prepare(target,tool,"subtract",true); row["plan"]=applied.PublicView; Persist();
                M3ExecutionResult r=Execute(applied); row["execution_state"]=r.State; row["execution"]=r.Result; row["error_code"]=r.ErrorCode; row["error_message"]=r.ErrorMessage; Persist();
                Require(r.State=="APPLIED","Expected APPLIED");
                M3VerificationResult v; try { v=adapter.VerifyPlan(binding,applied); } catch { stop=true; throw; }
                if(v==null || v.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN" || v.ErrorCode=="VERIFY_OUTCOME_UNKNOWN") { stop=true; throw new InvalidOperationException("UNKNOWN verification"); }
                row["verified"]=v.Verified; row["verify_checks"]=v.Checks; Require(v.Verified,"Fresh verification failed");
                string id=Convert.ToString(r.Result["feature_id"],CultureInfo.InvariantCulture);
                BooleanFeature feature=part.Features.GetFeatures().OfType<BooleanFeature>().Single(f=>f.JournalIdentifier==id);
                Body result=feature.GetBodies().Single(); row["result"]=BodyData(result); row["after"]=Snapshot();
                double volume=Convert.ToDouble(((Dictionary<string,object>)row["result"])["volume_mm3"],CultureInfo.InvariantCulture);
                Require(Math.Abs(volume-500)<1e-5,"subtract volume must be 500 mm3");
                double[] bounds=(double[])((Dictionary<string,object>)row["result"])["bbox_mm"];
                double[] expected={0,0,0,5,10,10}; for(int i=0;i<6;i++) Require(Math.Abs(bounds[i]-expected[i])<1e-6,"subtract bbox mismatch");
                int inside,outside; UFSession.GetUFSession().Modl.AskPointContainment(new double[]{2,5,5},result.Tag,out inside);
                UFSession.GetUFSession().Modl.AskPointContainment(new double[]{7,5,5},result.Tag,out outside);
                row["point_containment"]=new int[]{inside,outside}; Require(inside==1 && outside==2,"subtract point witness mismatch");
                row["sentinel"]=BodyData(sentinel);
            });
            Row("forced_post_commit_real_undo",delegate(Dictionary<string,object> row) {
                Dictionary<string,object> before=Snapshot(); string rev=binding.RevisionId; row["before"]=before; row["revision_before"]=rev;
                M3PlanSnapshot p=Prepare(undoTarget,undoTool,"unite",false);
                FieldInfo hook=p.AdapterState.GetType().GetField("ForcePostCommitVerificationFailureForTesting",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
                Require(hook!=null && hook.FieldType==typeof(bool),"missing failure injection hook"); hook.SetValue(p.AdapterState,true);
                row["forced_post_commit_failure"]=true; Persist(); M3ExecutionResult r=Execute(p);
                row["state"]=r.State; row["error_code"]=r.ErrorCode; row["result"]=r.Result; row["revision_after"]=binding.RevisionId; Persist();
                Require(r.State=="ROLLED_BACK" && binding.RevisionId!=rev,"expected verified rollback and advanced revision");
                Dictionary<string,object> after=Snapshot(); row["after"]=after;
                Require(M2Json.CanonicalText(before)==M2Json.CanonicalText(after),"Undo failed to restore complete body and feature snapshot");
            });
            Row("tamper_definition_fresh_negative",delegate(Dictionary<string,object> row) {
                M3VerificationResult control;
                try { control=adapter.VerifyPlan(binding,applied); } catch { stop=true; throw; }
                if(control==null || control.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN" || control.ErrorCode=="VERIFY_OUTCOME_UNKNOWN") { stop=true; throw new InvalidOperationException("UNKNOWN pre-tamper verification"); }
                row["pre_tamper_verified"]=control.Verified; row["pre_tamper_checks"]=control.Checks;
                Require(control.Verified,"fresh positive control failed before tampering");
                string id=Convert.ToString(((Dictionary<string,object>)cases[0])["execution"] is Dictionary<string,object> ?
                    ((Dictionary<string,object>)((Dictionary<string,object>)cases[0])["execution"])["feature_id"] : null,CultureInfo.InvariantCulture);
                BooleanFeature f=part.Features.GetFeatures().OfType<BooleanFeature>().Single(x=>x.JournalIdentifier==id);
                BooleanBuilder edit=part.Features.CreateBooleanBuilderUsingCollector(f);
                try { row["retain_before"]=edit.RetainTool; edit.RetainTool=false; edit.CommitFeature(); }
                finally { edit.Destroy(); }
                M3VerificationResult v; try { v=adapter.VerifyPlan(binding,applied); } catch { stop=true; throw; }
                if(v==null || v.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN" || v.ErrorCode=="VERIFY_OUTCOME_UNKNOWN") { stop=true; throw new InvalidOperationException("UNKNOWN tamper verification"); }
                row["verified"]=v.Verified; row["checks"]=v.Checks; Require(!v.Verified,"tampered definition incorrectly verified");
            });
            report["passed"]=!stop && cases.Count==3;
        } catch(Exception ex) { report["fatal"]=ex.ToString(); stop=true; }
        finally { report["writes_halted"]=stop; report["ended_utc"]=DateTime.UtcNow.ToString("o"); Persist(); }
    }
}
