using System;
using System.Collections.Generic;
using System.Reflection;
using NxSketchMcp.M2;

// Offline bridge parser/routing checks against the candidate M2Bridge assembly.
public static class F20BridgeContractHarness
{
    static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static readonly Type Server=typeof(M2BridgeServer);
    static readonly MethodInfo Parser=Server.GetMethod("ParseM3SpatialCurveRequest",Private);
    static int checks;
    static void Assert(bool value,string label){checks++;if(!value)throw new Exception(label);}
    static List<object> V(object x,object y,object z){return new List<object>{x,y,z};}
    static Dictionary<string,object> Base(string type)
    {
        Dictionary<string,object> d=new Dictionary<string,object>{{"operation_id","op"},{"ownership_id","owner"},{"binding_epoch",1},{"revision_id","rev"},{"curve_type",type}};
        if(type=="line"){d.Add("start_mm",V(1,2,3));d.Add("end_mm",V(4,6,8));}
        else {d.Add("center_mm",V(2,3,4));d.Add("normal",V(0,0,1));d.Add("x_axis",V(1,0,0));d.Add("radius_mm",4.0);
            if(type=="arc"){d.Add("definition_mode","center");d.Add("start_angle_deg",20.0);d.Add("end_angle_deg",-230.0);}}
        return d;
    }
    static Dictionary<string,object> Three()
    {return new Dictionary<string,object>{{"operation_id","op"},{"ownership_id","owner"},{"binding_epoch",1},{"revision_id","rev"},{"curve_type","arc"},{"definition_mode","three_points"},{"start_mm",V(1,0,0)},{"through_mm",V(0,-1,0)},{"end_mm",V(0,1,0)}};}
    static M3SpatialCurveRequest Parse(Dictionary<string,object> d,bool valid,string label)
    {
        object[] args={d,"rev","owner",1,null};M3SpatialCurveRequest request=null;
        try{request=(M3SpatialCurveRequest)Parser.Invoke(null,args);}
        catch(TargetInvocationException ex){if(ex.InnerException==null || ex.InnerException.GetType().Name!="BridgeException")throw;}
        Assert((request!=null && args[4]!=null)==valid,label);return request;
    }
    static void Set(object o,string field,object value){Server.GetField(field,Private).SetValue(o,value);}
    static string Invoke(M2BridgeServer server,string command,string args)
    {return server.Invoke(new string('a',64),"{\"id\":\"1\",\"command\":\""+command+"\",\"params\":"+args+"}");}
    static void BridgeChecks()
    {
        Fake adapter=new Fake();M2BridgeServer server=new M2BridgeServer(adapter);
        Set(server,"_started",true);Set(server,"_token",new string('a',64));
        Assert(Invoke(server,"bridge.status","{}").Contains("\"model_spatial_curve_adapter_available\":true"),"adapter capability");
        string args="{\"operation_id\":\"op\",\"ownership_id\":\"owner\",\"binding_epoch\":1,\"revision_id\":\"rev\",\"curve_type\":\"line\",\"start_mm\":[1,2,3],\"end_mm\":[4,6,8]}";
        Assert(Invoke(server,"operation.prepare_spatial_curve",args).Contains("AUTHORIZATION_REQUIRED"),"ownership gate");
        M2ModelBinding binding=new M2ModelBinding{RevisionId="rev",BindingEpoch=1};Set(server,"_binding",binding);Set(server,"_ownershipState","AI_OWNED");
        Assert(Invoke(server,"bridge.status","{}").Contains("\"model_spatial_curve_write_enabled\":true"),"enabled status");
        Set(server,"_writesHalted",true);
        Assert(Invoke(server,"operation.prepare_spatial_curve",args).Contains("OPERATION_OUTCOME_UNKNOWN"),"halt gate");
        Assert(Invoke(server,"bridge.status","{}").Contains("\"model_spatial_curve_write_enabled\":false"),"halt capability");
        Set(server,"_writesHalted",false);
        Type recordType=Server.GetNestedType("OperationRecord",BindingFlags.NonPublic);object record=Activator.CreateInstance(recordType,true);
        MethodInfo prepare=Server.GetMethod("PerformPrepareM3Operation",Private);object[] call=new object[prepare.GetParameters().Length];
        call[0]=record;call[1]=binding;call[2]="owner";
        for(int i=3;i<call.Length;i++)if(prepare.GetParameters()[i].ParameterType==typeof(M3SpatialCurveRequest))call[i]=Parse(Base("line"),true,"dispatch parse");
        Dictionary<string,object> result=(Dictionary<string,object>)prepare.Invoke(server,call);
        Assert(adapter.Calls==1 && (string)((Dictionary<string,object>)result["plan"])["kind"]=="create_spatial_curve","prepare dispatch");
        adapter.Unknown=true;recordType.GetField("State",Private).SetValue(record,"RUNNING");recordType.GetField("Kind",Private).SetValue(record,"prepare_m3_spatial_curve");
        result=(Dictionary<string,object>)prepare.Invoke(server,call);
        Assert((string)result["preparation_state"]=="UNKNOWN" && (string)recordType.GetField("State",Private).GetValue(record)=="UNKNOWN","unknown recorded");
        Assert(Invoke(server,"bridge.status","{}").Contains("\"model_spatial_curve_write_enabled\":false"),"unknown freezes writes");
        MethodInfo verify=Server.GetMethod("M3VerificationChecksPassed",Private);
        Dictionary<string,object> v=new Dictionary<string,object>{{"curve_geometry",new Dictionary<string,object>{{"status","VERIFIED"}}},{"model_preservation",new Dictionary<string,object>{{"status","VERIFIED"}}}};
        Assert((bool)verify.Invoke(null,new object[]{v,"create_spatial_curve"}),"fresh checks required");v.Remove("curve_geometry");
        Assert(!(bool)verify.Invoke(null,new object[]{v,"create_spatial_curve"}),"missing geometry rejected");
    }
    public static int Main()
    {
        try
        {
            Assert(Parser!=null,"parser present");Parse(Base("line"),true,"line");Parse(Base("arc"),true,"negative major arc");Parse(Three(),true,"three point arc");Parse(Base("circle"),true,"circle");
            Dictionary<string,object> d=Base("line");d["end_mm"]=V(1,2,3);Parse(d,false,"coincident line");
            d=Three();d["through_mm"]=V(0.5,0.5,0);Parse(d,false,"collinear");
            d=Three();d.Add("radius_mm",4.0);Parse(d,false,"mixed three point parameters");
            d=Base("arc");d["normal"]=V(0,0,2);Parse(d,false,"non-unit normal");
            d=Base("arc");d["x_axis"]=V(0,0,1);Parse(d,false,"parallel frame");
            d=Base("arc");d["end_angle_deg"]=380.0;Parse(d,false,"full turn arc");
            d=Base("arc");d["start_angle_deg"]=Double.NaN;Parse(d,false,"nan angle");
            d=Base("circle");d["radius_mm"]=Double.PositiveInfinity;Parse(d,false,"infinite radius");
            d=Base("circle");d.Add("definition_mode","center");Parse(d,false,"circle mode not accepted");
            F20SpatialGeometry g=F20SpatialGeometry.FromRequest(Parse(Three(),true,"three point geometry"));
            Assert(Math.Abs(g.Radius-1)<1e-12 && Math.Abs(g.SweepRadians-1.5*Math.PI)<1e-12 && g.Normal[2]<0,"major arc orientation");
            BridgeChecks();Console.WriteLine("F20 bridge contract checks passed: "+checks);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex.GetType().FullName+": "+ex.Message+"\n"+ex.StackTrace);return 1;}
    }
    sealed class Fake:IM2ModelAdapter,IM3OperationAdapter,IM3SpatialCurveOperationAdapter
    {
        public int Calls;public bool Unknown;
        public M3PlanSnapshot PrepareSpatialCurve(M2ModelBinding b,M3SpatialCurveRequest r){Calls++;if(Unknown)throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN","test unknown");return new M3PlanSnapshot{Kind="create_spatial_curve",RevisionId=r.RevisionId,AdapterState=new object(),PublicView=new Dictionary<string,object>()};}
        public bool IsBindingCurrent(M2ModelBinding b,out string reason){reason=null;return true;}
        public M2ModelBinding Takeover(string p,string a,string s,string l,int e){throw new NotImplementedException();}
        public Dictionary<string,object> GetSession(M2ModelBinding b){throw new NotImplementedException();}
        public Dictionary<string,object> InspectPart(M2ModelBinding b){throw new NotImplementedException();}
        public Dictionary<string,object> InspectSketch(M2ModelBinding b,string id){throw new NotImplementedException();}
        public void Release(M2ModelBinding b){}
        public M3PlanSnapshot PrepareSketch(M2ModelBinding b,M3SketchRequest r){throw new NotImplementedException();}
        public M3PlanSnapshot PrepareExtrude(M2ModelBinding b,M3ExtrudeRequest r){throw new NotImplementedException();}
        public M3ExecutionResult ExecutePlan(M2ModelBinding b,M3PlanSnapshot p){throw new NotImplementedException();}
        public M3VerificationResult VerifyPlan(M2ModelBinding b,M3PlanSnapshot p){throw new NotImplementedException();}
    }
}
