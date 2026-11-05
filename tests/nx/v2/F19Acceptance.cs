// F19 production adapter acceptance on a new isolated part. No installed claim.
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

public static class F19Acceptance
{
    const string Root=@"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    static Session session; static Part part; static M2OpenModelAdapter adapter; static M2ModelBinding binding;
    static Dictionary<string,object> report; static List<object> cases; static string path; static bool stop;
    static void Save(){File.WriteAllText(path,M2Json.CanonicalText(report),new UTF8Encoding(false));}
    static void Require(bool ok,string error){if(!ok)throw new InvalidOperationException(error);}
    static Point3d P(double x,double y,double z){return new Point3d(x,y,z);}
    static Feature Seed(double y)
    {
        Point3d[] p={P(0,y,1),P(2,y,1),P(2,y+3,1),P(0,y+3,1)};
        Curve[] c=new Curve[4];for(int i=0;i<4;i++)c[i]=part.Curves.CreateLine(p[i],p[(i+1)%4]);
        Section s=part.Sections.CreateSection(0.0095,0.01,0.5);s.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
        CurveDumbRule rule=part.ScRuleFactory.CreateRuleCurveDumb(c);
        s.AddToSection(new SelectionIntentRule[]{rule},c[0],null,null,p[0],Section.Mode.Create,false);
        ExtrudeBuilder b=part.Features.CreateExtrudeBuilder(null);
        try{b.Section=s;b.Direction=part.Directions.CreateDirection(P(0,0,0),new Vector3d(0,0,1),SmartObject.UpdateOption.WithinModeling);
            b.Limits.StartExtend.Value.RightHandSide="0";b.Limits.EndExtend.Value.RightHandSide="4";
            b.BooleanOperation.Type=BooleanOperation.BooleanType.Create;b.BooleanOperation.SetTargetBodies(new Body[]{null});return b.CommitFeature();}
        finally{b.Destroy();}
    }
    static object Private(string name,params object[] args)
    {
        MethodInfo m=typeof(M2OpenModelAdapter).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic);
        Require(m!=null,"missing native reader "+name);return m.Invoke(null,args);
    }
    static string Inventory(){return (string)Private("F19Inventory",part);}
    static string PatternDefinition(string id)
    {
        PatternFeature f=part.Features.GetFeatures().OfType<PatternFeature>().Single(x=>x.JournalIdentifier==id);
        return M2Json.CanonicalText(Private("F18Read",part,f));
    }
    static string MirrorDefinition(string id)
    {
        MirrorFeature f=part.Features.GetFeatures().OfType<MirrorFeature>().Single(x=>x.JournalIdentifier==id);
        return M2Json.CanonicalText(Private("F19Read",part,f));
    }
    static M3PlanSnapshot Prepare(string id,double y,string datum)
    {
        M3FeatureMirrorRequest req=new M3FeatureMirrorRequest{RevisionId=binding.RevisionId,SeedFeatureId=id};
        if(datum==null){req.PlaneOriginMm=new double[]{40,y,0};req.PlaneNormal=new double[]{2.0/7.0,-3.0/7.0,6.0/7.0};}
        else req.DatumPlaneId=datum;
        return ((IM3FeatureMirrorOperationAdapter)adapter).PrepareFeatureMirror(binding,req);
    }
    static M3ExecutionResult Execute(M3PlanSnapshot p)
    {
        M3ExecutionResult r;try{r=adapter.ExecutePlan(binding,p);}catch{stop=true;throw;}
        if(r==null||r.State=="UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN execute: stop");}return r;
    }
    static M3VerificationResult Verify(M3PlanSnapshot p)
    {
        M3VerificationResult r;try{r=adapter.VerifyPlan(binding,p);}catch{stop=true;throw;}
        if(r==null||r.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN"||r.ErrorCode=="VERIFY_OUTCOME_UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN verify: stop");}return r;
    }
    static void Row(string name,Action<Dictionary<string,object>> action)
    {
        if(stop)return;Dictionary<string,object> r=new Dictionary<string,object>{{"case",name},{"status","RUNNING"}};cases.Add(r);Save();
        try{action(r);r["status"]="PASS";}catch(Exception e){r["status"]=stop?"UNKNOWN_STOPPED":"FAIL";r["error"]=e.ToString();stop=true;}Save();
    }
    public static void Main(string[] args)
    {
        string run=Guid.NewGuid().ToString("N"),model=Path.Combine(Root,"workspace","v2","models","F19-native-"+run+".prt");
        path=Path.Combine(Root,"workspace","v2","evidence","f19-native-"+run+".json");cases=new List<object>();
        report=new Dictionary<string,object>{{"run_id",run},{"scope","production adapter; not installed MCP"},{"model_path",model},
            {"adapter_assembly",typeof(M2OpenModelAdapter).Assembly.Location},{"cases",cases},{"passed",false},{"started_utc",DateTime.UtcNow.ToString("o")}};Save();
        try
        {
            session=Session.GetSession();Require(session.Parts.Work==null&&!session.IsInTaskEnvironment&&session.ActiveSketch==null,"requires empty session");
            part=session.Parts.NewDisplay(model,Part.Units.Millimeters);
            string[] ids={Seed(0).JournalIdentifier,Seed(40).JournalIdentifier,Seed(80).JournalIdentifier,Seed(120).JournalIdentifier,Seed(160).JournalIdentifier};
            double k=1.0/Math.Sqrt(2.0);
            Feature datum=M3NxFeatureFactory.CreateDatumPlane(part,P(10,50,0),new Vector3d(k,k,0),new Vector3d(-k,k,0));
            string datumId=datum.JournalIdentifier;
            Feature alternateDatum=M3NxFeatureFactory.CreateDatumPlane(part,P(10,51,0),new Vector3d(k,k,0),new Vector3d(-k,k,0));
            string alternateDatumId=alternateDatum.JournalIdentifier;
            Feature staleDatum=M3NxFeatureFactory.CreateDatumPlane(part,P(10,170,0),new Vector3d(k,k,0),new Vector3d(-k,k,0));
            string staleDatumId=staleDatum.JournalIdentifier;
            using(PartSaveStatus save=part.Save(BasePart.SaveComponents.True,BasePart.CloseAfterSave.False))
                Require(save.NumberUnsavedParts==0&&save.NumberUnsavedObjects==0,"fixture save failed");
            adapter=new M2OpenModelAdapter();binding=adapter.Takeover(model,"saved_independent_copy","f19-native-"+run,"f19-load-"+run,1);
            string patternId=null,patternBefore=null,firstMirror=null,firstDefinition=null,latestId=null; M3PlanSnapshot latest=null;
            bool skipStale=Environment.GetEnvironmentVariable("F19_SKIP_STALE_CONTROL")=="1";
            bool numericOnly=Environment.GetEnvironmentVariable("F19_NUMERIC_ONLY")=="1";
            report["numeric_only_diagnostic"]=numericOnly;
            report["stale_control_skipped_prior_pass"]=skipStale;
            if(!skipStale)Row("datum_changed_after_prepare_rejected",delegate(Dictionary<string,object> row){
                M3PlanSnapshot plan=Prepare(ids[4],160,staleDatumId);row["plan"]=plan.PublicView;
                DatumPlaneFeature f=part.Features.GetFeatures().OfType<DatumPlaneFeature>().Single(x=>x.JournalIdentifier==staleDatumId);
                Point3d before=f.DatumPlane.Origin;row["datum_before_mm"]=new double[]{before.X,before.Y,before.Z};
                Matrix3x3 frame=new Matrix3x3();frame.Xx=-k;frame.Xy=k;frame.Xz=0;frame.Yx=0;frame.Yy=0;frame.Yz=1;frame.Zx=k;frame.Zy=k;frame.Zz=0;
                Session.UndoMarkId mark=session.SetUndoMark(Session.MarkVisibility.Invisible,"F19 stale datum edit");
                DatumPlaneBuilder edit=part.Features.CreateDatumPlaneBuilder(f);
                try{Plane plane=edit.GetPlane();plane.EditFixedPlane(P(10,171,0),frame);plane.Evaluate();edit.CommitFeature();}finally{edit.Destroy();}
                int errors=session.UpdateManager.DoUpdate(mark);row["edit_update_errors"]=errors;session.DeleteUndoMark(mark,"F19 stale datum edit");
                Require(errors==0,"native datum edit update failed");
                f=part.Features.GetFeatures().OfType<DatumPlaneFeature>().Single(x=>x.JournalIdentifier==staleDatumId);
                Point3d after=f.DatumPlane.Origin;row["datum_after_mm"]=new double[]{after.X,after.Y,after.Z};
                Require(Math.Abs(after.Y-171)<1e-6 && Math.Abs(before.Y-after.Y)>0.5,"native datum edit did not persist");
                string inv=Inventory(),rev=binding.RevisionId;bool dirty=part.IsModified;
                M3ExecutionResult e=Execute(plan);row["state"]=e.State;row["error_code"]=e.ErrorCode;row["result"]=e.Result;
                row["inventory_before_execute"]=inv;row["inventory_after_execute"]=Inventory();row["dirty_before_execute"]=dirty;
                row["dirty_after_execute"]=part.IsModified;row["revision_before_execute"]=rev;row["revision_after_execute"]=binding.RevisionId;row["result_revision"]=e.RevisionId;
                Require(e.State=="UNCHANGED"&&e.ErrorCode=="STALE_PLAN"&&inv==(string)row["inventory_after_execute"]&&dirty==part.IsModified&&Int64.Parse(binding.RevisionId,CultureInfo.InvariantCulture)==Int64.Parse(rev,CultureInfo.InvariantCulture)+1&&e.RevisionId==binding.RevisionId,
                    "changed datum did not safely reject stale plan with one revision advance");
            });
            Row("existing_pattern_setup",delegate(Dictionary<string,object> row){
                M3FeaturePatternRequest q=new M3FeaturePatternRequest{RevisionId=binding.RevisionId,SeedFeatureId=ids[3],Mode="linear",Count1=2,Direction1=new double[]{1,0,0},Pitch1Mm=8};
                M3PlanSnapshot plan=adapter.PrepareFeaturePattern(binding,q);M3ExecutionResult e=Execute(plan);row["state"]=e.State;row["error_code"]=e.ErrorCode;row["error_message"]=e.ErrorMessage;row["result_revision"]=e.RevisionId;row["result"]=e.Result;
                Require(e.State=="APPLIED","pattern setup not applied");M3VerificationResult v=Verify(plan);row["verified"]=v.Verified;Require(v.Verified,"pattern setup verify failed");
                patternId=(string)e.Result["feature_id"];patternBefore=PatternDefinition(patternId);row["native_definition"]=patternBefore;
            });
            Row("explicit_oblique_plane",delegate(Dictionary<string,object> row){
                string inv=Inventory(),rev=binding.RevisionId;bool dirty=part.IsModified;M3PlanSnapshot p=Prepare(ids[0],0,null);
                row["plan"]=p.PublicView;row["inventory_before_prepare"]=inv;row["inventory_after_prepare"]=Inventory();
                Require(inv==(string)row["inventory_after_prepare"]&&dirty==part.IsModified&&rev==binding.RevisionId,"prepare changed state");
                M3ExecutionResult e=Execute(p);row["execution_state"]=e.State;row["execution_error_code"]=e.ErrorCode;row["execution_error_message"]=e.ErrorMessage;row["execution_revision"]=e.RevisionId;row["result"]=e.Result;Save();Require(e.State=="APPLIED","mirror not applied");
                M3VerificationResult v=Verify(p);row["verified"]=v.Verified;row["verification_error_code"]=v.ErrorCode;row["verification_error_message"]=v.ErrorMessage;row["verification_result"]=v.Result;row["checks"]=v.Checks;Require(v.Verified,"fresh failed");firstMirror=(string)e.Result["feature_id"];
                firstDefinition=MirrorDefinition(firstMirror);row["native_definition"]=firstDefinition;
                Require(patternBefore==PatternDefinition(patternId),"existing Pattern changed");
            });
            if(!numericOnly)Row("datum_plane_with_old_mirror_and_pattern",delegate(Dictionary<string,object> row){
                string inv=Inventory(),rev=binding.RevisionId;bool dirty=part.IsModified;M3PlanSnapshot p=Prepare(ids[1],40,datumId);
                row["plan"]=p.PublicView;row["inventory_before_prepare"]=inv;row["inventory_after_prepare"]=Inventory();
                Require(inv==(string)row["inventory_after_prepare"]&&dirty==part.IsModified&&rev==binding.RevisionId,"prepare changed state");
                M3ExecutionResult e=Execute(p);row["execution_state"]=e.State;row["execution_error_code"]=e.ErrorCode;row["execution_error_message"]=e.ErrorMessage;row["execution_revision"]=e.RevisionId;row["result"]=e.Result;Save();Require(e.State=="APPLIED","datum mirror not applied");
                M3VerificationResult v=Verify(p);row["verified"]=v.Verified;row["verification_error_code"]=v.ErrorCode;row["verification_error_message"]=v.ErrorMessage;row["verification_result"]=v.Result;row["checks"]=v.Checks;Require(v.Verified,"fresh failed");
                row["old_mirror_before"]=firstDefinition;row["old_mirror_after"]=MirrorDefinition(firstMirror);
                row["old_pattern_before"]=patternBefore;row["old_pattern_after"]=PatternDefinition(patternId);
                Require(firstDefinition==(string)row["old_mirror_after"]&&patternBefore==(string)row["old_pattern_after"],"old feature definition changed");latest=p;latestId=(string)e.Result["feature_id"];
            });
            if(!numericOnly)Row("postcommit_failure_real_undo",delegate(Dictionary<string,object> row){
                string inv=Inventory(),rev=binding.RevisionId;bool dirty=part.IsModified;M3PlanSnapshot p=Prepare(ids[2],80,null);
                FieldInfo hook=p.AdapterState.GetType().GetField("ForcePostCommitVerificationFailureForTesting",BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance);
                Require(hook!=null&&hook.FieldType==typeof(bool),"missing fault injection hook");hook.SetValue(p.AdapterState,true);
                M3ExecutionResult e=Execute(p);row["state"]=e.State;row["error_code"]=e.ErrorCode;row["error_message"]=e.ErrorMessage;row["result_revision"]=e.RevisionId;row["result"]=e.Result;row["before"]=inv;row["after"]=Inventory();
                row["revision_before"]=rev;row["revision_after"]=binding.RevisionId;row["dirty_before"]=dirty;row["dirty_after"]=part.IsModified;
                Require(e.State=="ROLLED_BACK"&&inv==(string)row["after"]&&dirty==part.IsModified&&rev!=binding.RevisionId,"rollback mismatch");
            });
            if(!numericOnly)Row("fresh_native_tamper",delegate(Dictionary<string,object> row){
                M3VerificationResult control=Verify(latest);row["control_verified"]=control.Verified;Require(control.Verified,"fresh control failed");
                string original=MirrorDefinition(latestId);
                MirrorFeature feature=part.Features.GetFeatures().OfType<MirrorFeature>().Single(f=>f.JournalIdentifier==latestId);
                DatumPlaneFeature other=part.Features.GetFeatures().OfType<DatumPlaneFeature>().Single(f=>f.JournalIdentifier==alternateDatumId);
                Session.UndoMarkId mark=session.SetUndoMark(Session.MarkVisibility.Invisible,"F19 native tamper");
                MirrorFeatureBuilder b=part.Features.CreateMirrorFeatureBuilder(feature);
                try{b.Plane.Value=other.DatumPlane;b.CommitFeature();}finally{b.Destroy();}
                int errors=session.UpdateManager.DoUpdate(mark);row["tamper_update_errors"]=errors;
                session.DeleteUndoMark(mark,"F19 native tamper");Require(errors==0,"native tamper update failed");
                string changed=MirrorDefinition(latestId);row["definition_before"]=original;row["definition_after"]=changed;
                Require(original!=changed,"native tamper was not persisted");
                string before=Inventory();bool dirty=part.IsModified;M3VerificationResult v=Verify(latest);
                row["verified"]=v.Verified;row["verification_error_code"]=v.ErrorCode;row["verification_error_message"]=v.ErrorMessage;row["verification_result"]=v.Result;row["checks"]=v.Checks;row["inventory_before_verify"]=before;row["inventory_after_verify"]=Inventory();
                row["dirty_before_verify"]=dirty;row["dirty_after_verify"]=part.IsModified;
                Dictionary<string,object> check=(Dictionary<string,object>)v.Checks["mirror_definition"];
                Require(!v.Verified&&Object.Equals(check["status"],"MISMATCH"),"fresh did not reject native tamper");
                Require(before==(string)row["inventory_after_verify"]&&dirty==part.IsModified,"fresh changed native state");
            });
            report["numeric_only_case_passed"]=numericOnly&&!stop&&cases.Count==(skipStale?2:3);
            report["passed"]=!numericOnly&&!stop&&cases.Count==(skipStale?5:6);
        }
        catch(Exception e){report["fatal"]=e.ToString();stop=true;}
        finally{report["writes_halted"]=stop;report["ended_utc"]=DateTime.UtcNow.ToString("o");Save();}
    }
}
