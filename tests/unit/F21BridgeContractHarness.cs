using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using NxSketchMcp.M2;

// Offline candidate bridge contract only; does not instantiate NX.
public static class F21BridgeContractHarness
{
    static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static readonly Type Server=typeof(M2BridgeServer);
    static readonly MethodInfo Parser=Server.GetMethod("ParseM3SweepRequest",Private,null,new Type[]{typeof(Dictionary<string,object>),typeof(string),typeof(string),typeof(int),typeof(Dictionary<string,object>).MakeByRefType()},null);
    static int checks;
    static void Assert(bool value,string label){checks++;if(!value)throw new Exception(label);}
    static Dictionary<string,object> Base(){return new Dictionary<string,object>{{"operation_id","op"},{"ownership_id","owner"},
        {"binding_epoch",1},{"revision_id","rev"},{"sketch_id","SKETCH(1)"},{"path_curve_ids",new List<object>{"CURVE(2)","CURVE(1)"}},
        {"profile_output","solid"},{"orientation_mode","transport"},{"twist_angle_deg",0.0}};}
    static M3SweepRequest Parse(Dictionary<string,object> d,bool valid,string label)
    {
        object[] args={d,"rev","owner",1,null};M3SweepRequest request=null;
        try{request=(M3SweepRequest)Parser.Invoke(null,args);}
        catch(TargetInvocationException ex){if(ex.InnerException==null||ex.InnerException.GetType().Name!="BridgeException")throw;}
        Assert((request!=null&&args[4]!=null)==valid,label);return request;
    }
    static void Set(object o,string field,object value){Server.GetField(field,Private).SetValue(o,value);}
    static string Invoke(M2BridgeServer server,string command,string args)
    {return server.Invoke(new string('a',64),"{\"id\":\"1\",\"command\":\""+command+"\",\"params\":"+args+"}");}
    static void BridgeChecks()
    {
        Fake adapter=new Fake();M2BridgeServer server=new M2BridgeServer(adapter);
        Set(server,"_started",true);Set(server,"_token",new string('a',64));
        Assert(Invoke(server,"bridge.status","{}").Contains("\"model_sweep_adapter_available\":true"),"capability available");
        string args="{\"operation_id\":\"op\",\"ownership_id\":\"owner\",\"binding_epoch\":1,\"revision_id\":\"rev\",\"sketch_id\":\"SKETCH(1)\",\"path_curve_ids\":[\"CURVE(2)\",\"CURVE(1)\"],\"profile_output\":\"solid\",\"orientation_mode\":\"transport\",\"twist_angle_deg\":0}";
        Assert(Invoke(server,"operation.prepare_sweep",args).Contains("AUTHORIZATION_REQUIRED"),"ownership gate");
        M2ModelBinding binding=new M2ModelBinding{RevisionId="rev",BindingEpoch=1};Set(server,"_binding",binding);Set(server,"_ownershipState","AI_OWNED");
        Assert(Invoke(server,"bridge.status","{}").Contains("\"model_sweep_write_enabled\":true"),"write enabled");
        Set(server,"_writesHalted",true);
        Assert(Invoke(server,"operation.prepare_sweep",args).Contains("OPERATION_OUTCOME_UNKNOWN"),"halt gate");
        Assert(Invoke(server,"bridge.status","{}").Contains("\"model_sweep_write_enabled\":false"),"halt status");
        Set(server,"_writesHalted",false);
        Type recordType=Server.GetNestedType("OperationRecord",BindingFlags.NonPublic);object record=Activator.CreateInstance(recordType,true);
        MethodInfo prepare=Server.GetMethods(Private).Single(m=>m.Name=="PerformPrepareM3Operation" && m.GetParameters().Count(p=>p.ParameterType==typeof(M3SweepRequest))==1);
        object[] call=new object[prepare.GetParameters().Length];
        call[0]=record;call[1]=binding;call[2]="owner";
        for(int i=3;i<call.Length;i++)if(prepare.GetParameters()[i].ParameterType==typeof(M3SweepRequest))call[i]=Parse(Base(),true,"dispatch request");
        Dictionary<string,object> result=(Dictionary<string,object>)prepare.Invoke(server,call);
        Assert(adapter.Calls==1&&(string)((Dictionary<string,object>)result["plan"])["kind"]=="create_sweep","dispatch");
        Assert(adapter.Last.PathCurveIds[0]=="CURVE(2)"&&adapter.Last.PathCurveIds[1]=="CURVE(1)","path order retained");
        adapter.Unknown=true;recordType.GetField("State",Private).SetValue(record,"RUNNING");recordType.GetField("Kind",Private).SetValue(record,"prepare_m3_sweep");
        result=(Dictionary<string,object>)prepare.Invoke(server,call);
        Assert((string)result["preparation_state"]=="UNKNOWN"&&(string)recordType.GetField("State",Private).GetValue(record)=="UNKNOWN","unknown recorded");
        Assert(Invoke(server,"bridge.status","{}").Contains("\"model_sweep_write_enabled\":false"),"unknown freezes writes");
        MethodInfo verify=Server.GetMethod("M3VerificationChecksPassed",Private,null,new Type[]{typeof(Dictionary<string,object>),typeof(string)},null);
        Assert(verify!=null,"sweep verification gate exists");
        string[] required={"sweep_definition","sweep_geometry","model_preservation","body_effect","geometry","bodies","history","support_associations"};
        Dictionary<string,object> checks=new Dictionary<string,object>();foreach(string name in required)checks.Add(name,new Dictionary<string,object>{{"status","VERIFIED"}});
        Assert((bool)verify.Invoke(null,new object[]{checks,"create_sweep"}),"all required sweep verification checks accepted");
        foreach(string name in required){
            checks.Remove(name);Assert(!(bool)verify.Invoke(null,new object[]{checks,"create_sweep"}),"missing "+name+" rejected");
            checks[name]=new Dictionary<string,object>{{"status","MISMATCH"}};
            Assert(!(bool)verify.Invoke(null,new object[]{checks,"create_sweep"}),"mismatched "+name+" rejected");
            checks[name]=new Dictionary<string,object>{{"status","UNKNOWN"}};
            Assert(!(bool)verify.Invoke(null,new object[]{checks,"create_sweep"}),"unknown "+name+" rejected");
            checks[name]=new Dictionary<string,object>{{"status","VERIFIED"}};
        }
        VerifyUnknownGate();
    }
    static void VerifyUnknownGate()
    {
        Fake adapter=new Fake{VerifyUnknown=true};M2BridgeServer server=new M2BridgeServer(adapter);
        Set(server,"_started",true);Set(server,"_token",new string('a',64));
        M2ModelBinding binding=new M2ModelBinding{RevisionId="rev",BindingEpoch=1};Set(server,"_binding",binding);Set(server,"_ownershipState","AI_OWNED");
        Type recordType=Server.GetNestedType("OperationRecord",BindingFlags.NonPublic);object operation=Activator.CreateInstance(recordType,true);
        recordType.GetField("State",Private).SetValue(operation,"RUNNING");recordType.GetField("Kind",Private).SetValue(operation,"verify_part");
        Type planType=Server.GetNestedType("PreparedM3Plan",BindingFlags.NonPublic);
        M3PlanSnapshot snapshot=new M3PlanSnapshot{Kind="create_sweep",RevisionId="rev",AdapterState=new object(),PublicView=new Dictionary<string,object>()};
        object plan=Activator.CreateInstance(planType,BindingFlags.NonPublic|BindingFlags.Instance,null,
            new object[]{"plan","owner",1,"session","load",binding,snapshot},null);
        MethodInfo method=Server.GetMethods(Private).Single(m=>m.Name=="PerformM3PartVerification" && m.GetParameters().Length==5 && m.GetParameters()[3].ParameterType==planType);
        bool threw=false;
        try{method.Invoke(server,new object[]{operation,adapter,binding,plan,"execute"});}
        catch(TargetInvocationException ex){M2ModelException inner=ex.InnerException as M2ModelException;threw=inner!=null&&inner.Code=="M3_ANALYSIS_STATE_UNKNOWN";}
        Assert(threw,"sweep fresh UNKNOWN exception preserved");
        Assert((string)recordType.GetField("State",Private).GetValue(operation)=="UNKNOWN","sweep fresh operation frozen UNKNOWN");
        Assert(Invoke(server,"bridge.status","{}").Contains("\"writes_halted\":true"),"sweep fresh UNKNOWN halts writes");
        Assert(Invoke(server,"bridge.status","{}").Contains("\"ownership_state\":\"UNKNOWN_FROZEN\""),"sweep fresh UNKNOWN freezes ownership");
    }
    public static int Main()
    {
        try{
            Assert(Parser!=null,"parser present");M3SweepRequest p=Parse(Base(),true,"transport");
            Assert(p.PathCurveIds.Length==2&&p.PathCurveIds[0]=="CURVE(2)"&&p.PathCurveIds[1]=="CURVE(1)","ordered ids");
            var d=Base();d["profile_output"]="sheet";d["orientation_mode"]="linear_twist";d["twist_angle_deg"]=-90.0;p=Parse(d,true,"sheet angular limit");
            Assert(p.ProfileOutput=="sheet"&&p.OrientationMode=="linear_twist"&&p.TwistAngleDeg==-90.0,"fields retained");
            foreach(string key in new List<string>(Base().Keys)){d=Base();d.Remove(key);Parse(d,false,"required "+key);}
            d=Base();d["path_curve_ids"]=new List<object>();Parse(d,false,"empty path");
            d=Base();d["path_curve_ids"]=new List<object>{"x","x"};Parse(d,false,"duplicate path");
            d=Base();d["path_curve_ids"]=new List<object>{"x",1};Parse(d,false,"non-string path");
            d=Base();d["orientation_mode"]="linear_twist";d["twist_angle_deg"]=91.0;Parse(d,false,"twist bounds");
            d=Base();d["twist_angle_deg"]=1.0;Parse(d,false,"transport with nonzero twist");
            d=Base();d["twist_angle_deg"]=Double.NaN;Parse(d,false,"nan twist");
            d=Base();d["profile_output"]="wire";Parse(d,false,"wrong body type");
            d=Base();d["extra"]=1;Parse(d,false,"extra field");
            BridgeChecks();Console.WriteLine("F21 candidate bridge contract checks passed: "+checks);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex.GetType().FullName+": "+ex.Message+"\n"+ex.StackTrace);return 1;}
    }
    sealed class Fake:IM2ModelAdapter,IM3OperationAdapter,IM3SweepOperationAdapter
    {
        public int Calls;public bool Unknown,VerifyUnknown;public M3SweepRequest Last;
        public M3PlanSnapshot PrepareSweep(M2ModelBinding b,M3SweepRequest r){Calls++;Last=r;if(Unknown)throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN","test unknown");return new M3PlanSnapshot{Kind="create_sweep",RevisionId=r.RevisionId,SketchId=r.SketchId,AdapterState=new object(),PublicView=new Dictionary<string,object>()};}
        public bool IsBindingCurrent(M2ModelBinding b,out string reason){reason=null;return true;}
        public M2ModelBinding Takeover(string p,string a,string s,string l,int e){throw new NotImplementedException();}
        public Dictionary<string,object> GetSession(M2ModelBinding b){throw new NotImplementedException();}
        public Dictionary<string,object> InspectPart(M2ModelBinding b){throw new NotImplementedException();}
        public Dictionary<string,object> InspectSketch(M2ModelBinding b,string id){throw new NotImplementedException();}
        public void Release(M2ModelBinding b){}
        public M3PlanSnapshot PrepareSketch(M2ModelBinding b,M3SketchRequest r){throw new NotImplementedException();}
        public M3PlanSnapshot PrepareExtrude(M2ModelBinding b,M3ExtrudeRequest r){throw new NotImplementedException();}
        public M3ExecutionResult ExecutePlan(M2ModelBinding b,M3PlanSnapshot p){throw new NotImplementedException();}
        public M3VerificationResult VerifyPlan(M2ModelBinding b,M3PlanSnapshot p){if(VerifyUnknown)throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN","fixture cleanup unknown");throw new NotImplementedException();}
    }
}
