// Production-adapter F18 acceptance on an isolated millimetre part.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NXOpen;
using NXOpen.Features;
using NXOpen.GeometricUtilities;
using NxSketchMcp.M2;

public static class F18Acceptance
{
    const string Root=@"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    static Session session;static Part part;static M2OpenModelAdapter adapter;static M2ModelBinding binding;
    static Dictionary<string,object> report;static List<object> cases;static string reportPath;static bool stop;
    static void Persist(){File.WriteAllText(reportPath,M2Json.CanonicalText(report),new UTF8Encoding(false));}
    static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static Point3d P(double x,double y,double z){return new Point3d(x,y,z);}
    static Feature Seed(double x,double y)
    {
        Point3d[] p={P(x,y,1),P(x+2,y,1),P(x+2,y+3,1),P(x,y+3,1)};
        Curve[] curves=new Curve[4];for(int i=0;i<4;i++)curves[i]=part.Curves.CreateLine(p[i],p[(i+1)%4]);
        Section section=part.Sections.CreateSection(0.0095,0.01,0.5);section.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
        CurveDumbRule rule=part.ScRuleFactory.CreateRuleCurveDumb(curves);
        section.AddToSection(new SelectionIntentRule[]{rule},curves[0],null,null,p[0],Section.Mode.Create,false);
        ExtrudeBuilder b=part.Features.CreateExtrudeBuilder(null);
        try
        {
            b.Section=section;b.Direction=part.Directions.CreateDirection(P(0,0,0),new Vector3d(0,0,1),SmartObject.UpdateOption.WithinModeling);
            b.Limits.StartExtend.Value.RightHandSide="0";b.Limits.EndExtend.Value.RightHandSide="4";
            b.BooleanOperation.Type=BooleanOperation.BooleanType.Create;b.BooleanOperation.SetTargetBodies(new Body[]{null});
            return b.CommitFeature();
        }
        finally{b.Destroy();}
    }
    static string Inventory(){MethodInfo m=typeof(M2OpenModelAdapter).GetMethod("F18Inventory",BindingFlags.NonPublic|BindingFlags.Static);return (string)m.Invoke(null,new object[]{part});}
    static Dictionary<string,object> ReadPattern(PatternFeature feature)
    {MethodInfo m=typeof(M2OpenModelAdapter).GetMethod("F18Read",BindingFlags.NonPublic|BindingFlags.Static);return (Dictionary<string,object>)m.Invoke(null,new object[]{part,feature});}
    static M3PlanSnapshot Prepare(string id,string mode)
    {
        M3FeaturePatternRequest r=new M3FeaturePatternRequest{RevisionId=binding.RevisionId,SeedFeatureId=id,Mode=mode,Count1=3};
        if(mode=="linear"){r.Direction1=new double[]{1,0,0};r.Pitch1Mm=8;}
        else if(mode=="rectangular"){r.Direction1=new double[]{-1,0,0};r.Pitch1Mm=8;r.Direction2=new double[]{0,1,0};r.Pitch2Mm=11;r.Count2=2;}
        else{r.AxisOriginMm=new double[]{110,0,0};r.AxisDirection=new double[]{0,0,1};r.AngleDeg=-70;}
        return adapter.PrepareFeaturePattern(binding,r);
    }
    static M3ExecutionResult Execute(M3PlanSnapshot plan)
    {
        M3ExecutionResult r;try{r=adapter.ExecutePlan(binding,plan);}catch{stop=true;throw;}
        if(r==null || r.State=="UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN execution; stop");}return r;
    }
    static M3VerificationResult Verify(M3PlanSnapshot plan)
    {
        M3VerificationResult r;try{r=adapter.VerifyPlan(binding,plan);}catch{stop=true;throw;}
        if(r==null || r.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN" || r.ErrorCode=="VERIFY_OUTCOME_UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN verification; stop");}return r;
    }
    static void Row(string name,Action<Dictionary<string,object>> action)
    {
        if(stop)return;Dictionary<string,object> row=new Dictionary<string,object>{{"case",name},{"status","RUNNING"}};cases.Add(row);Persist();
        try{action(row);row["status"]="PASS";}catch(Exception ex){row["status"]=stop?"UNKNOWN_STOPPED":"FAIL";row["error"]=ex.ToString();stop=true;}Persist();
    }
    public static void Main(string[] args)
    {
        string run=Guid.NewGuid().ToString("N"),model=Path.Combine(Root,"workspace","v2","models","F18-native-"+run+".prt");
        reportPath=Path.Combine(Root,"workspace","v2","evidence","f18-native-"+run+".json");cases=new List<object>();
        report=new Dictionary<string,object>{{"scope","production adapter; not installed MCP"},{"run_id",run},{"model_path",model},{"cases",cases},
            {"adapter_assembly",typeof(M2OpenModelAdapter).Assembly.Location},{"started_utc",DateTime.UtcNow.ToString("o")},{"passed",false}};Persist();
        try
        {
            session=Session.GetSession();Require(session.Parts.Work==null && !session.IsInTaskEnvironment && session.ActiveSketch==null,"requires empty independent session");
            part=session.Parts.NewDisplay(model,Part.Units.Millimeters);
            string[] ids={Seed(0,0).JournalIdentifier,Seed(0,50).JournalIdentifier,Seed(120,0).JournalIdentifier,Seed(0,100).JournalIdentifier};
            using(PartSaveStatus saved=part.Save(BasePart.SaveComponents.True,BasePart.CloseAfterSave.False)) {Require(saved.NumberUnsavedParts==0 && saved.NumberUnsavedObjects==0,"fixture save failed");}
            adapter=new M2OpenModelAdapter();binding=adapter.Takeover(model,"saved_independent_copy","f18-native-"+run,"f18-load-"+run,1);
            M3PlanSnapshot latest=null;string latestId=null;string[] modes={"linear","rectangular","circular"};
            for(int n=0;n<modes.Length;n++)
            {
                int i=n;bool prerequisite=i==0 && Environment.GetEnvironmentVariable("F18_LINEAR_PREREQUISITE")=="1";
                Row(prerequisite?"linear_prerequisite":"native_"+modes[i],delegate(Dictionary<string,object> row){
                    if(prerequisite)row["evidence_role"]="prerequisite only; do not count as an independent accepted case";
                    string before=Inventory(),revision=binding.RevisionId;bool dirty=part.IsModified;row["inventory_before"]=before;row["dirty_before"]=dirty;
                    M3PlanSnapshot plan=Prepare(ids[i],modes[i]);row["plan"]=plan.PublicView;row["inventory_after_prepare"]=Inventory();row["dirty_after_prepare"]=part.IsModified;
                    Require(revision==binding.RevisionId && dirty==part.IsModified && before==(string)row["inventory_after_prepare"],"prepare changed state");
                    M3ExecutionResult e=Execute(plan);row["execution_state"]=e.State;row["execution"]=e.Result;row["error_code"]=e.ErrorCode;row["error_message"]=e.ErrorMessage;Persist();
                    Require(e.State=="APPLIED","expected APPLIED");if(prerequisite)return;
                    M3VerificationResult v=Verify(plan);row["verified"]=v.Verified;row["verification_checks"]=v.Checks;row["verification_result"]=v.Result;
                    Require(v.Verified,"fresh verification failed");latest=plan;latestId=(string)e.Result["feature_id"];
                });
            }
            Row("real_undo_after_postcommit_failure",delegate(Dictionary<string,object> row){
                string before=Inventory(),revision=binding.RevisionId;bool dirty=part.IsModified;M3PlanSnapshot p=Prepare(ids[3],"linear");
                FieldInfo hook=p.AdapterState.GetType().GetField("ForcePostCommitVerificationFailureForTesting",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
                Require(hook!=null && hook.FieldType==typeof(bool),"missing failure hook");hook.SetValue(p.AdapterState,true);
                M3ExecutionResult e=Execute(p);row["state"]=e.State;row["result"]=e.Result;row["revision_before"]=revision;row["revision_after"]=binding.RevisionId;
                row["inventory_before"]=before;row["inventory_after"]=Inventory();row["dirty_before"]=dirty;row["dirty_after"]=part.IsModified;
                Require(e.State=="ROLLED_BACK" && revision!=binding.RevisionId && before==(string)row["inventory_after"] && dirty==part.IsModified,"real undo or revision advance failed");
            });
            Row("fresh_tamper_negative",delegate(Dictionary<string,object> row){
                M3VerificationResult control=Verify(latest);row["control_verified"]=control.Verified;Require(control.Verified,"fresh positive control failed");
                PatternFeature feature=part.Features.GetFeatures().OfType<PatternFeature>().Single(f=>f.JournalIdentifier==latestId);
                Session.UndoMarkId mark=session.SetUndoMark(Session.MarkVisibility.Invisible,"F18 tamper control");
                PatternFeatureBuilder b=part.Features.CreatePatternFeatureBuilder(feature);
                try{b.PatternService.CircularDefinition.AngularSpacing.PitchAngle.RightHandSide="-65";b.CommitFeature();}finally{b.Destroy();}
                int updateErrors=session.UpdateManager.DoUpdate(mark);row["tamper_update_errors"]=updateErrors;
                session.DeleteUndoMark(mark,"F18 tamper control");Require(updateErrors==0,"tamper update failed");
                feature=part.Features.GetFeatures().OfType<PatternFeature>().Single(f=>f.JournalIdentifier==latestId);
                Dictionary<string,object> changed=ReadPattern(feature);row["tampered_definition"]=changed;
                Require(Math.Abs((double)changed["angle"]+65)<1e-6,"tamper angle was not persisted");
                string beforeVerify=Inventory();bool dirty=part.IsModified;row["inventory_after_tamper"]=beforeVerify;row["dirty_after_tamper"]=dirty;
                M3VerificationResult v=Verify(latest);row["verified"]=v.Verified;row["checks"]=v.Checks;
                row["inventory_after_verify"]=Inventory();row["dirty_after_verify"]=part.IsModified;
                Dictionary<string,object> check=(Dictionary<string,object>)v.Checks["pattern_definition"];
                Require(!v.Verified && Object.Equals(check["status"],"MISMATCH"),"tampered native definition was not rejected");
                Require(beforeVerify==(string)row["inventory_after_verify"] && dirty==part.IsModified,"fresh verification changed tampered state");
            });
            report["passed"]=!stop && cases.Count==5;
        }
        catch(Exception ex){report["fatal"]=ex.ToString();stop=true;}
        finally{report["writes_halted"]=stop;report["ended_utc"]=DateTime.UtcNow.ToString("o");Persist();}
    }
}
