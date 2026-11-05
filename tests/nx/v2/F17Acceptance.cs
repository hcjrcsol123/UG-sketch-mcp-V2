// F17 production adapter test: three native modes, true Undo and definition tamper.
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

public static class F17Acceptance
{
    const string Root=@"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    static Session session; static Part part; static M2OpenModelAdapter adapter; static M2ModelBinding binding;
    static string reportPath; static Dictionary<string,object> report; static List<object> cases; static bool stop;
    static void Persist(){File.WriteAllText(reportPath,M2Json.CanonicalText(report),new UTF8Encoding(false));}
    static void Require(bool v,string message){if(!v)throw new InvalidOperationException(message);}
    static Body Block(double x,double l){BlockFeatureBuilder b=part.Features.CreateBlockFeatureBuilder(null);try{b.SetOriginAndLengths(new Point3d(x,0,0),l.ToString(CultureInfo.InvariantCulture),"12","10");b.SetBooleanOperationAndTarget(Feature.BooleanType.Create,null);return b.CommitFeature().GetBodies().Single();}finally{b.Destroy();}}
    static Edge SelectEdge(Body body,double x,double y){List<Edge> matches=new List<Edge>();foreach(Edge e in body.GetEdges()){Point3d a,b;e.GetVertices(out a,out b);if(Math.Abs(a.X-x)<1e-6 && Math.Abs(b.X-x)<1e-6 && Math.Abs(a.Y-y)<1e-6 && Math.Abs(b.Y-y)<1e-6 && Math.Abs(Math.Abs(a.Z-b.Z)-10)<1e-6)matches.Add(e);}Require(matches.Count==1,"expected unique z edge");return matches[0];}
    static double[] Point(Point3d p){return new double[]{p.X,p.Y,p.Z};}
    static Dictionary<string,object> BodyData(Body body){
        double[] bounds=new double[6];UFSession.GetUFSession().Modl.AskBoundingBox(body.Tag,bounds);
        Unit[] units={part.UnitCollection.FindObject("Kilogram"),part.UnitCollection.FindObject("MilliMeter"),part.UnitCollection.FindObject("SquareMilliMeter"),part.UnitCollection.FindObject("CubicMilliMeter")};
        double volume;using(MeasureBodies m=part.MeasureManager.NewMassProperties(units,0.999999,new Body[]{body}))volume=m.Volume;
        List<object> faces=new List<object>();foreach(Face f in body.GetFaces()){
            int type,n;double r,rd;double[] p=new double[3],d=new double[3],box=new double[6];UFSession.GetUFSession().Modl.AskFaceData(f.Tag,out type,p,d,box,out r,out rd,out n);
            List<object> edges=new List<object>();foreach(Edge e in f.GetEdges()){Point3d a,b;e.GetVertices(out a,out b);edges.Add(new Dictionary<string,object>{{"id",e.JournalIdentifier},{"tag",e.Tag.ToString()},{"vertices_mm",new double[][]{Point(a),Point(b)}},{"adjacent_face_ids",e.GetFaces().Select(x=>x.JournalIdentifier).ToArray()}});}
            faces.Add(new Dictionary<string,object>{{"id",f.JournalIdentifier},{"tag",f.Tag.ToString()},{"type",type},{"point_mm",p},{"direction",d},{"normal_sign",n},{"bounds_mm",box},{"edges",edges}});
        }
        return new Dictionary<string,object>{{"id",body.JournalIdentifier},{"tag",body.Tag.ToString()},{"volume_mm3",volume},{"bbox_mm",bounds},{"faces",faces}};
    }
    static Dictionary<string,object> Snapshot(){return new Dictionary<string,object>{{"bodies",part.Bodies.ToArray().OrderBy(b=>b.JournalIdentifier,StringComparer.Ordinal).Select(b=>(object)BodyData(b)).ToArray()},{"features",part.Features.GetFeatures().Select(f=>(object)new Dictionary<string,object>{{"id",f.JournalIdentifier},{"type",f.FeatureType}}).ToArray()}};}
    static bool SnapshotEqual(Dictionary<string,object> a,Dictionary<string,object> b){
        if(M2Json.CanonicalText(a["features"])!=M2Json.CanonicalText(b["features"]))return false;
        object[] aa=(object[])a["bodies"],bb=(object[])b["bodies"];if(aa.Length!=bb.Length)return false;
        for(int i=0;i<aa.Length;i++){Dictionary<string,object> x=(Dictionary<string,object>)aa[i],y=(Dictionary<string,object>)bb[i];if(!Object.Equals(x["id"],y["id"])||!Object.Equals(x["tag"],y["tag"])||Math.Abs((double)x["volume_mm3"]-(double)y["volume_mm3"])>1e-5||((List<object>)x["faces"]).Count!=((List<object>)y["faces"]).Count)return false;double[] xb=(double[])x["bbox_mm"],yb=(double[])y["bbox_mm"];for(int j=0;j<6;j++)if(Math.Abs(xb[j]-yb[j])>1e-6)return false;}
        return true;
    }
    static M3PlanSnapshot Prepare(Body target,Edge edge,string mode,string axis){return adapter.PrepareChamfer(binding,new M3ChamferRequest{RevisionId=binding.RevisionId,TargetBodyId=target.JournalIdentifier,EdgeIds=new string[]{edge.JournalIdentifier},Mode=mode,ReferenceAxis=axis,DistanceMm=2.0,SecondDistanceMm=3.0,AngleDeg=30.0});}
    static M3ExecutionResult Execute(M3PlanSnapshot p){M3ExecutionResult r;try{r=adapter.ExecutePlan(binding,p);}catch{stop=true;throw;}if(r==null||r.State=="UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN execution; stop");}return r;}
    static M3VerificationResult Verify(M3PlanSnapshot p){M3VerificationResult v;try{v=adapter.VerifyPlan(binding,p);}catch{stop=true;throw;}if(v==null||v.ErrorCode=="M3_ANALYSIS_STATE_UNKNOWN"||v.ErrorCode=="VERIFY_OUTCOME_UNKNOWN"){stop=true;throw new InvalidOperationException("UNKNOWN verification; stop");}return v;}
    static void Row(string name,Action<Dictionary<string,object>> action){if(stop)return;string selection=Environment.GetEnvironmentVariable("F17_NATIVE_CASES");if(!String.IsNullOrWhiteSpace(selection)&&!selection.Split(',').Contains(name))return;Dictionary<string,object> row=new Dictionary<string,object>{{"case",name},{"status","RUNNING"},{"reproduction","Run candidate on a new isolated fixture with the recorded parameters."},{"retest","Fix defect and repeat this case on a new fixture with original criteria."}};cases.Add(row);Persist();try{action(row);row["status"]="PASS";}catch(Exception ex){row["status"]=stop?"UNKNOWN_STOPPED":"FAIL";row["error"]=ex.ToString();stop=true;}Persist();}
    public static void Main(string[] args){
        string run=Guid.NewGuid().ToString("N"),model=Path.Combine(Root,"workspace","v2","models","F17-native-"+run+".prt");reportPath=Path.Combine(Root,"workspace","v2","evidence","f17-native-"+run+".json");cases=new List<object>();report=new Dictionary<string,object>{{"scope","production adapter; not installed MCP"},{"run_id",run},{"model_path",model},{"cases",cases},{"adapter_assembly",typeof(M2OpenModelAdapter).Assembly.Location},{"started_utc",DateTime.UtcNow.ToString("o")},{"passed",false}};Persist();
        try{session=Session.GetSession();Require(session.Parts.Work==null&&!session.IsInTaskEnvironment&&session.ActiveSketch==null,"requires empty session");part=session.Parts.NewDisplay(model,Part.Units.Millimeters);
            Body[] targets={Block(0,20),Block(40,20),Block(80,20),Block(120,20)};Body undo=Block(160,20),sentinel=Block(200,4);Edge[] edges={SelectEdge(targets[0],0,0),SelectEdge(targets[1],40,0),SelectEdge(targets[2],80,0),SelectEdge(targets[3],120,12)};Edge undoEdge=SelectEdge(undo,160,0);
            using(PartSaveStatus save=part.Save(BasePart.SaveComponents.True,BasePart.CloseAfterSave.False)){} adapter=new M2OpenModelAdapter();binding=adapter.Takeover(model,"saved_independent_copy","f17-native-"+run,"f17-load-"+run,1);
            M3PlanSnapshot applied=null;string appliedFeature=null;
            string[] modes={"equal_distance","two_distances","distance_angle","two_distances"}, axes={"x","x","y","x"}, options={"SymmetricOffsets","TwoOffsets","OffsetAndAngle","TwoOffsets"};
            for(int i=0;i<4;i++){int ix=i;Row(ix==3?"native_high_corner_direction_correction":"native_"+modes[ix],delegate(Dictionary<string,object> row){
                row["before"]=Snapshot();string tag=edges[ix].Tag.ToString(),id=edges[ix].JournalIdentifier;row["input_edge_id"]=id;row["input_edge_tag"]=tag;row["mode"]=modes[ix];row["reference_axis"]=axes[ix];row["distance_mm"]=2.0;row["second_distance_mm"]=3.0;row["angle_deg"]=30.0;
                string rev=binding.RevisionId;M3PlanSnapshot p=Prepare(targets[ix],edges[ix],modes[ix],axes[ix]);row["plan"]=p.PublicView;row["after_prepare"]=Snapshot();Require(rev==binding.RevisionId && SnapshotEqual((Dictionary<string,object>)row["before"],(Dictionary<string,object>)row["after_prepare"]),"prepare changed measured model inventory");
                M3ExecutionResult r=Execute(p);row["execution_state"]=r.State;row["execution"]=r.Result;row["error_code"]=r.ErrorCode;row["error_message"]=r.ErrorMessage;Persist();Require(r.State=="APPLIED","expected APPLIED");
                if(ix==1){Require(Convert.ToInt32(r.Result["correction_count"],CultureInfo.InvariantCulture)==1 && !Object.Equals(r.Result["reverse_before"],r.Result["reverse_after"]),"expected one direction correction");Dictionary<string,object> swapped=r.Result["swapped_pre_correction_coverage"] as Dictionary<string,object>;Require(swapped!=null&&Object.Equals(swapped["status"],"VERIFIED"),"missing verified initial swapped coverage");}
                M3VerificationResult v=Verify(p);row["verified"]=v.Verified;row["verify_checks"]=v.Checks;row["verify_result"]=v.Result;Require(v.Verified,"fresh verification failed");
                string featureId=Convert.ToString(r.Result["feature_id"],CultureInfo.InvariantCulture);Feature feature=part.Features.GetFeatures().Single(f=>f.JournalIdentifier==featureId);Dictionary<string,object> d=V2ChamferFactory.Read(part,feature);row["native_readback"]=d;
                Require(Object.Equals(d["option"],options[ix])&&Object.Equals(d["method"],"EdgesAlongFaces")&&Object.Equals(d["rule_edges_match_collector"],true),"native mode/rule mismatch");string[] tags=d["edge_tags"] as string[];Require(tags!=null&&tags.Length==1&&tags[0]==tag,"wrong native input edge");
                Dictionary<string,object> first=(Dictionary<string,object>)d["first_offset"],second=(Dictionary<string,object>)d["second_offset"],angle=(Dictionary<string,object>)d["angle"];
                double other=ix==0?2:ix==2?2*Math.Tan(Math.PI/6):3;Require(Math.Abs((double)first["value"]-2)<1e-6&&Math.Abs((double)second["value"]-other)<1e-6&&Object.Equals(first["units"],"MilliMeter")&&Object.Equals(second["units"],"MilliMeter")&&Object.Equals(angle["units"],"Degrees")&&(ix!=2||Math.Abs((double)angle["value"]-30)<1e-6),"native parameters mismatch");
                Body result=feature.GetBodies().Single();Dictionary<string,object> data=BodyData(result);row["result"]=data;row["after"]=Snapshot();double expected=2400-10*other;row["expected_volume_mm3"]=expected;Require(Math.Abs((double)data["volume_mm3"]-expected)<1e-5,"volume mismatch");
                double x=40*ix;int outside,inside;UFSession.GetUFSession().Modl.AskPointContainment(new double[]{x+0.1,ix==3?11.9:0.1,5},result.Tag,out outside);UFSession.GetUFSession().Modl.AskPointContainment(new double[]{x+5,5,5},result.Tag,out inside);row["containment"]=new int[]{outside,inside};Require(outside==2&&inside==1,"witness mismatch");
                row["sentinel"]=BodyData(sentinel);applied=p;appliedFeature=featureId;
            });}
            Row("forced_post_commit_real_undo",delegate(Dictionary<string,object> row){Dictionary<string,object> before=Snapshot();row["before"]=before;string rev=binding.RevisionId;row["revision_before"]=rev;M3PlanSnapshot p=Prepare(undo,undoEdge,"two_distances","x");FieldInfo hook=p.AdapterState.GetType().GetField("ForcePostCommitVerificationFailureForTesting",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);Require(hook!=null&&hook.FieldType==typeof(bool),"missing hook");hook.SetValue(p.AdapterState,true);M3ExecutionResult r=Execute(p);row["state"]=r.State;row["error_code"]=r.ErrorCode;row["result"]=r.Result;row["revision_after"]=binding.RevisionId;row["after"]=Snapshot();Require(r.State=="ROLLED_BACK"&&binding.RevisionId!=rev&&r.Result!=null&&r.Result.ContainsKey("undo_called")&&Object.Equals(r.Result["undo_called"],true),"missing true Undo");Dictionary<string,object> diag=r.Result["operation_diagnostics"] as Dictionary<string,object>;Require(diag!=null,"missing failure diagnostics");if(diag.ContainsKey("plan_execution_failure"))diag=diag["plan_execution_failure"] as Dictionary<string,object>;Require(diag!=null&&Convert.ToInt32(diag["correction_count"],CultureInfo.InvariantCulture)==1&&!Object.Equals(diag["reverse_before"],diag["reverse_after"]),"failure was not injected after correction");Require(SnapshotEqual(before,(Dictionary<string,object>)row["after"]),"Undo did not restore measured model inventory");});
            Row("tamper_native_fresh_negative",delegate(Dictionary<string,object> row){M3VerificationResult control=Verify(applied);row["control_verified"]=control.Verified;Require(control.Verified,"fresh positive control failed");Chamfer f=part.Features.GetFeatures().OfType<Chamfer>().Single(z=>z.JournalIdentifier==appliedFeature);ChamferBuilder edit=part.Features.CreateChamferBuilder(f);try{row["before_mm"]=edit.FirstOffsetExp.Value;edit.FirstOffsetExp.RightHandSide="2.5";edit.CommitFeature();}finally{edit.Destroy();}Dictionary<string,object> changed=V2ChamferFactory.Read(part,f);row["changed_definition"]=changed;Require(Math.Abs((double)((Dictionary<string,object>)changed["first_offset"])["value"]-2.5)<1e-6,"tamper not persisted");M3VerificationResult v=Verify(applied);row["verified"]=v.Verified;row["checks"]=v.Checks;Require(!v.Verified,"fresh incorrectly accepted native tamper");});
            string selection=Environment.GetEnvironmentVariable("F17_NATIVE_CASES");report["selected_cases"]=selection;report["passed"]=!stop&&cases.Count==(String.IsNullOrWhiteSpace(selection)?6:selection.Split(',').Length);
        }catch(Exception ex){report["fatal"]=ex.ToString();stop=true;}finally{report["writes_halted"]=stop;report["ended_utc"]=DateTime.UtcNow.ToString("o");Persist();}
    }
}
