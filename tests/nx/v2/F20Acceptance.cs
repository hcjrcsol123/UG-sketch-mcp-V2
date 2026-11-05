// F20 native production-adapter controls, not installed MCP acceptance.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NXOpen;
using NXOpen.UF;
using NxSketchMcp.M2;

public static class F20Acceptance
{
    const string Root=@"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    static Session session; static Part part; static M2OpenModelAdapter adapter; static M2ModelBinding binding;
    static Dictionary<string,object> report; static List<object> cases; static string path; static bool stop;
    static HashSet<string> selected;
    static void Save(){File.WriteAllText(path,M2Json.CanonicalText(report),new UTF8Encoding(false));}
    static void Need(bool ok,string error){if(!ok)throw new InvalidOperationException(error);}
    static Point3d P(double x,double y,double z){return new Point3d(x,y,z);}
    static double[] V(Point3d p){return new double[]{p.X,p.Y,p.Z};}
    static double[] V(Vector3d p){return new double[]{p.X,p.Y,p.Z};}
    static Curve Find(string id){return part.Curves.ToArray().Single(c=>c.JournalIdentifier==id);}
    static object Read(Curve c)
    {
        Dictionary<string,object> row=new Dictionary<string,object>{{"id",c.JournalIdentifier},{"tag",c.Tag.ToString()},{"length_mm",c.GetLength()}};
        Line line=c as Line;Arc arc=c as Arc;
        if(line!=null){row["kind"]="LINE";row["start_mm"]=V(line.StartPoint);row["end_mm"]=V(line.EndPoint);}
        else if(arc!=null){Point3d center;Vector3d x,y;arc.GetOrientation(out center,out x,out y);row["kind"]="ARC";row["center_mm"]=V(center);row["axis_x"]=V(x);row["axis_y"]=V(y);row["radius_mm"]=arc.Radius;row["start_angle_rad"]=arc.StartAngle;row["sweep_rad"]=arc.EndAngle-arc.StartAngle;}
        else throw new InvalidOperationException("unsupported curve type");
        UFSession uf=UFSession.GetUFSession();IntPtr eval=IntPtr.Zero;double[] limits=new double[2];List<object> samples=new List<object>();
        try{uf.Eval.Initialize(c.Tag,out eval);Need(eval!=IntPtr.Zero,"null native evaluator");uf.Eval.AskLimits(eval,limits);Need(limits[1]>limits[0],"invalid native parameter limits");
            for(int i=0;i<=4;i++){double[] pt=new double[3];uf.Curve.EvaluateCurve(c.Tag,limits[0]+(limits[1]-limits[0])*i/4.0,0,pt);samples.Add(pt);}}
        finally{if(eval!=IntPtr.Zero)uf.Eval.Free(eval);}
        row["parameter_limits"]=limits;row["points_mm"]=samples;return row;
    }
    static string Inventory()
    {
        MethodInfo method=typeof(M2OpenModelAdapter).GetMethod("F19Inventory",BindingFlags.Static|BindingFlags.NonPublic,null,new Type[]{typeof(Part)},null);
        Need(method!=null,"missing inventory reader");
        List<object> curves=new List<object>();foreach(Curve c in part.Curves.ToArray())curves.Add(Read(c));
        return M2Json.CanonicalText(new object[]{method.Invoke(null,new object[]{part}),curves,part.IsModified,session.ActiveSketch==null});
    }
    static M3PlanSnapshot Prepare(M3SpatialCurveRequest q){q.RevisionId=binding.RevisionId;try{return ((IM3SpatialCurveOperationAdapter)adapter).PrepareSpatialCurve(binding,q);}catch{stop=true;throw;}}
    static M3ExecutionResult Execute(M3PlanSnapshot p)
    {
        M3ExecutionResult e;try{e=adapter.ExecutePlan(binding,p);}catch{stop=true;throw;}
        if(e==null||e.State=="UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN execution; stop");}return e;
    }
    static M3VerificationResult Verify(M3PlanSnapshot p)
    {
        M3VerificationResult v;try{v=adapter.VerifyPlan(binding,p);}catch{stop=true;throw;}
        if(v==null||v.ErrorCode=="VERIFY_OUTCOME_UNKNOWN"||v.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN verification; stop");}return v;
    }
    static void Row(string name,Action<Dictionary<string,object>> action)
    {
        if(stop||!selected.Contains(name))return;var row=new Dictionary<string,object>{{"case",name},{"status","RUNNING"}};cases.Add(row);Save();
        try{action(row);row["status"]="PASS";}catch(Exception e){row["status"]=stop?"UNKNOWN_STOPPED":"FAIL";row["error"]=e.ToString();stop=true;}Save();
    }
    public static void Main(string[] args)
    {
        string run=Guid.NewGuid().ToString("N"),model=Path.Combine(Root,"workspace","v2","models","F20-native-"+run+".prt");
        path=Path.Combine(Root,"workspace","v2","evidence","f20-native-"+run+".json");cases=new List<object>();
        report=new Dictionary<string,object>{{"run_id",run},{"scope","native production adapter; not installed MCP"},{"model_path",model},{"assembly",typeof(M2OpenModelAdapter).Assembly.Location},{"cases",cases},{"passed",false},{"started_utc",DateTime.UtcNow.ToString("o")}};Save();
        try{
            string[] names={"line","center_signed_major","three_point_major","full_circle","postcommit_failure_real_undo","fresh_native_tamper","stale_old_curve_edit","active_sketch_rejected"};
            string filter=Environment.GetEnvironmentVariable("F20_CASES");
            selected=new HashSet<string>(String.IsNullOrWhiteSpace(filter)?names:filter.Split(','),StringComparer.Ordinal);
            Need(selected.Count>0&&selected.All(x=>names.Contains(x)),"F20_CASES must contain valid comma-separated case names");
            Need(!selected.Contains("fresh_native_tamper")||selected.Contains("full_circle"),"fresh tamper requires full_circle in this session");
            report["selected_cases"]=selected.ToArray();
            session=Session.GetSession();Need(session.Parts.Work==null&&!session.IsInTaskEnvironment&&session.ActiveSketch==null,"empty session required");
            part=session.Parts.NewDisplay(model,Part.Units.Millimeters);
            part.Curves.CreateLine(P(-100,-100,5),P(-96,-94,13));
            part.Curves.CreateArc(P(-150,-140,17),new Vector3d(1,0,0),new Vector3d(0,1,0),7,Math.PI/12,25*Math.PI/36);
            using(PartSaveStatus s=part.Save(BasePart.SaveComponents.True,BasePart.CloseAfterSave.False))Need(s.NumberUnsavedParts==0&&s.NumberUnsavedObjects==0&&!part.IsModified,"fixture save failed");
            adapter=new M2OpenModelAdapter();binding=adapter.Takeover(model,"saved_independent_copy","f20-native-"+run,"f20-load-"+run,1);
            var requests=new M3SpatialCurveRequest[]{
                new M3SpatialCurveRequest{CurveType="line",StartMm=new double[]{1,2,3},EndMm=new double[]{8,13,19}},
                new M3SpatialCurveRequest{CurveType="arc",DefinitionMode="center",CenterMm=new double[]{20,30,40},Normal=new double[]{0,0.6,0.8},XAxis=new double[]{1,0,0},RadiusMm=7,StartAngleDeg=30,EndAngleDeg=-210},
                new M3SpatialCurveRequest{CurveType="arc",DefinitionMode="three_points",StartMm=new double[]{35,50,70},ThroughMm=new double[]{25,50,70},EndMm=new double[]{30,54,67}},
                new M3SpatialCurveRequest{CurveType="circle",CenterMm=new double[]{80,90,100},Normal=new double[]{0,0.6,0.8},XAxis=new double[]{1,0,0},RadiusMm=9}};
            var inputs=new Dictionary<string,object>[]{
                new Dictionary<string,object>{{"curve_type","line"},{"start_mm",requests[0].StartMm},{"end_mm",requests[0].EndMm}},
                new Dictionary<string,object>{{"curve_type","arc"},{"definition_mode","center"},{"center_mm",requests[1].CenterMm},{"normal",requests[1].Normal},{"x_axis",requests[1].XAxis},{"radius_mm",7},{"start_angle_deg",30},{"end_angle_deg",-210}},
                new Dictionary<string,object>{{"curve_type","arc"},{"definition_mode","three_points"},{"start_mm",requests[2].StartMm},{"through_mm",requests[2].ThroughMm},{"end_mm",requests[2].EndMm}},
                new Dictionary<string,object>{{"curve_type","circle"},{"center_mm",requests[3].CenterMm},{"normal",requests[3].Normal},{"x_axis",requests[3].XAxis},{"radius_mm",9}}};
            M3PlanSnapshot latest=null;string latestId=null;
            for(int i=0;i<requests.Length;i++){int index=i;Row(new string[]{"line","center_signed_major","three_point_major","full_circle"}[i],delegate(Dictionary<string,object> row){
                row["request"]=inputs[index];string before=Inventory(),revision=binding.RevisionId;M3PlanSnapshot p=Prepare(requests[index]);
                row["plan"]=p.PublicView;row["inventory_before_prepare"]=before;row["inventory_after_prepare"]=Inventory();
                Need(before==(string)row["inventory_after_prepare"]&&revision==binding.RevisionId,"prepare modified model");
                M3ExecutionResult e=Execute(p);row["state"]=e.State;row["error_code"]=e.ErrorCode;row["error_message"]=e.ErrorMessage;row["result"]=e.Result;Save();Need(e.State=="APPLIED","execute failed");
                M3VerificationResult v=Verify(p);row["verified"]=v.Verified;row["checks"]=v.Checks;row["verification_error_code"]=v.ErrorCode;Need(v.Verified,"fresh verify failed");
                latestId=(string)e.Result["curve_id"];row["native"]=Read(Find(latestId));latest=p;
            });}
            Row("postcommit_failure_real_undo",delegate(Dictionary<string,object> row){
                string before=Inventory(),revision=binding.RevisionId;var q=new M3SpatialCurveRequest{CurveType="line",StartMm=new double[]{200,201,202},EndMm=new double[]{210,215,220}};
                M3PlanSnapshot p=Prepare(q);FieldInfo hook=p.AdapterState.GetType().GetField("ForcePostCommitVerificationFailureForTesting",BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance);Need(hook!=null&&hook.FieldType==typeof(bool),"fault hook missing");hook.SetValue(p.AdapterState,true);
                M3ExecutionResult e=Execute(p);row["state"]=e.State;row["error_code"]=e.ErrorCode;row["error_message"]=e.ErrorMessage;row["result"]=e.Result;row["before"]=before;row["after"]=Inventory();row["revision_before"]=revision;row["revision_after"]=binding.RevisionId;
                Need(e.State=="ROLLED_BACK"&&e.ErrorCode=="M3_OPERATION_ROLLED_BACK"&&e.Result!=null&&
                    Object.Equals(e.Result["operation_exception_code"],"F20_FORCED_POST_COMMIT_FAILURE")&&
                    Object.Equals(e.Result["undo_called"],true)&&before==(string)row["after"]&&revision!=binding.RevisionId,"forced postcommit rollback mismatch");
            });
            Row("fresh_native_tamper",delegate(Dictionary<string,object> row){
                Need(Verify(latest).Verified,"fresh control failed");Arc arc=Find(latestId) as Arc;Need(arc!=null,"expected circle Arc");row["original"]=Read(arc);string revision=binding.RevisionId;row["revision_before_edit"]=revision;
                Session.UndoMarkId mark=session.SetUndoMark(Session.MarkVisibility.Invisible,"F20 native tamper");arc.SetRadius(10.0);int errors=session.UpdateManager.DoUpdate(mark);session.DeleteUndoMark(mark,"F20 native tamper");Need(errors==0,"native tamper update failed");
                row["changed"]=Read(Find(latestId));Need(Math.Abs(((Arc)Find(latestId)).Radius-10.0)<1e-6,"native tamper did not persist");
                string before=Inventory();M3VerificationResult v=Verify(latest);row["verified"]=v.Verified;row["checks"]=v.Checks;row["error_code"]=v.ErrorCode;row["before"]=before;row["after"]=Inventory();row["revision_after_verify"]=binding.RevisionId;row["verification_revision"]=v.RevisionId;
                var check=v.Checks["curve_geometry"] as Dictionary<string,object>;Need(!v.Verified&&check!=null&&Object.Equals(check["status"],"MISMATCH"),"fresh did not detect tamper");Need(before==(string)row["after"],"fresh changed tampered state");
                Need(revision!=binding.RevisionId&&v.RevisionId==binding.RevisionId,"fresh did not advance revision after external edit");
            });
            Row("stale_old_curve_edit",delegate(Dictionary<string,object> row){
                var q=new M3SpatialCurveRequest{CurveType="line",StartMm=new double[]{240,241,242},EndMm=new double[]{250,255,260}};
                M3PlanSnapshot p=Prepare(q);Line old=part.Curves.ToArray().OfType<Line>().Single(c=>Math.Abs(c.StartPoint.X+100)<1e-8);row["old_before"]=Read(old);
                Session.UndoMarkId mark=session.SetUndoMark(Session.MarkVisibility.Invisible,"F20 external old curve edit");old.SetEndPoint(P(-95,-94,13));
                int errors=session.UpdateManager.DoUpdate(mark);session.DeleteUndoMark(mark,"F20 external old curve edit");Need(errors==0,"external update failed");
                row["old_after_edit"]=Read(old);Need(Math.Abs(old.EndPoint.X+95)<1e-6,"external edit not applied");string before=Inventory();string revision=binding.RevisionId;
                M3ExecutionResult e=Execute(p);row["state"]=e.State;row["error_code"]=e.ErrorCode;row["result"]=e.Result;row["before_execute"]=before;row["after_execute"]=Inventory();row["revision_before"]=revision;row["revision_after"]=binding.RevisionId;
                Need(e.State=="UNCHANGED"&&e.ErrorCode=="STALE_PLAN"&&before==(string)row["after_execute"],"stale plan committed or changed external edit");
            });
            Row("active_sketch_rejected",delegate(Dictionary<string,object> row){
                Sketch sketch=M3NxFeatureFactory.CreateSketch(part,P(300,300,300),new Vector3d(0,0,1),new Vector3d(1,0,0));
                Need(session.ActiveSketch!=null&&session.ActiveSketch.Tag==sketch.Tag,"fixture sketch not active");string before=Inventory();string revision=binding.RevisionId;
                var q=new M3SpatialCurveRequest{RevisionId=revision,CurveType="line",StartMm=new double[]{310,311,312},EndMm=new double[]{320,325,330}};
                string code=null;try{((IM3SpatialCurveOperationAdapter)adapter).PrepareSpatialCurve(binding,q);}catch(M2ModelException ex){code=ex.Code;}
                row["error_code"]=code;row["before"]=before;row["after"]=Inventory();row["active_sketch_tag"]=session.ActiveSketch==null?null:session.ActiveSketch.Tag.ToString();
                Need(code=="NX_BUSY"&&before==(string)row["after"]&&session.ActiveSketch!=null&&session.ActiveSketch.Tag==sketch.Tag&&binding.RevisionId==revision,"active sketch was not rejected without changes");
                sketch.Deactivate(Sketch.ViewReorient.False,Sketch.UpdateLevel.Model);Need(session.ActiveSketch==null,"could not deactivate fixture sketch");
            });
            report["passed"]=!stop&&cases.Count==selected.Count;
        }catch(Exception e){report["fatal"]=e.ToString();stop=true;}
        finally{report["writes_halted"]=stop;report["ended_utc"]=DateTime.UtcNow.ToString("o");Save();}
    }
}
