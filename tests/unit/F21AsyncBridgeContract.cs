// Offline real M2Bridge queue/operation tests. Fake adapter; no NX is started.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using NxSketchMcp.M2;

public static class F21AsyncBridgeContract
{
    static readonly Type T=typeof(M2BridgeServer);static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;static int checks;
    const string Args="{\"operation_id\":\"op\",\"ownership_id\":\"owner\",\"binding_epoch\":1,\"revision_id\":\"rev\",\"sketch_id\":\"SKETCH(1)\",\"path_curve_ids\":[\"CURVE(1)\"],\"profile_output\":\"solid\",\"orientation_mode\":\"transport\",\"twist_angle_deg\":0}";
    static void Need(bool x,string label){checks++;if(!x)throw new Exception(label);}
    static void Set(object x,string field,object value){T.GetField(field,Private).SetValue(x,value);}
    static object Get(object x,string field){return T.GetField(field,Private).GetValue(x);}
    static string Call(M2BridgeServer server,string command,string args){return server.Invoke(new string('a',64),"{\"id\":\"1\",\"command\":\""+command+"\",\"params\":"+args+"}");}
    static M2BridgeServer New(Fake fake)
    {
        var server=new M2BridgeServer(fake);Set(server,"_started",true);Set(server,"_token",new string('a',64));
        Set(server,"_binding",new M2ModelBinding{RevisionId="rev",BindingEpoch=1});Set(server,"_ownershipState","AI_OWNED");
        Set(server,"_ownershipId","owner");Set(server,"_bindingEpoch",1);Set(server,"_acceptModelReads",true);
        // Pretend an existing main-thread callback is pending; do not pump it.
        Set(server,"_pumpPosted",true);return server;
    }
    static object Record(M2BridgeServer s){return ((IDictionary)Get(s,"_operations"))["op"];}
    static string ReadState(object record){return (string)record.GetType().GetField("State",Private).GetValue(record);}
    static void Pump(M2BridgeServer s){T.GetMethod("ProcessDispatchQueue",Private,null,Type.EmptyTypes,null).Invoke(s,null);}
    static void MainTests()
    {
        var fake=new Fake();var server=New(fake);var clock=Stopwatch.StartNew();string first=Call(server,"operation.prepare_sweep",Args);clock.Stop();
        Need(clock.ElapsedMilliseconds<1000,"un-pumped sweep returns fast");Need(first.Contains("\"state\":\"QUEUED\"")&&first.Contains("\"operation_id\":\"op\""),"real registered id and pending state");
        string duplicate=Call(server,"operation.prepare_sweep",Args);Need(duplicate.Contains("\"state\":\"QUEUED\""),"same id returns pending record");
        Need(((ICollection)Get(server,"_dispatchQueue")).Count==1&&fake.Calls==0,"same id enqueues once without dispatch");
        string conflict=Call(server,"operation.prepare_sweep",Args.Replace("SKETCH(1)","SKETCH(2)"));Need(conflict.Contains("OPERATION_ID_CONFLICT"),"different request with same id conflicts");
        string cancel=Call(server,"operation.cancel","{\"operation_id\":\"op\"}");Need(cancel.Contains("\"state\":\"CANCELLED\""),"queued operation cancels");
        Pump(server);Need(fake.Calls==0&&((ICollection)Get(server,"_dispatchQueue")).Count==0,"cancelled work never executes");

        fake=new Fake{Mode="failed"};server=New(fake);Call(server,"operation.prepare_sweep",Args);Pump(server);
        string lateFailure=Call(server,"operation.get","{\"operation_id\":\"op\"}");
        Need(fake.Calls==1&&lateFailure.Contains("\"state\":\"FAILED\"")&&lateFailure.Contains("UNSUPPORTED_PROFILE"),"later definitive adapter failure retained");
        Need(Call(server,"bridge.status","{}").Contains("\"writes_halted\":false"),"definitive failure does not freeze writes");

        fake=new Fake{Mode="unknown"};server=New(fake);Call(server,"operation.prepare_sweep",Args);Pump(server);
        Need(ReadState(Record(server))=="UNKNOWN","true adapter UNKNOWN retained");
        Need(Call(server,"bridge.status","{}").Contains("\"writes_halted\":true"),"true UNKNOWN freezes writes");

        fake=new Fake{Mode="wait_fail"};server=New(fake);Call(server,"operation.prepare_sweep",Args);M2BridgeServer runningServer=server;
        Exception threadError=null;Thread thread=new Thread(delegate(){try{Pump(runningServer);}catch(Exception ex){threadError=ex;}});thread.Start();
        Need(fake.Started.WaitOne(5000),"fake dispatch started");
        string runningCancel=Call(server,"operation.cancel","{\"operation_id\":\"op\"}");Need(runningCancel.Contains("\"state\":\"RUNNING\""),"running operation cannot be cancelled");
        object record=Record(server);object gate=Get(server,"_lifecycle");
        lock(gate){T.GetMethod("SetOperationUnknownLocked",Private).Invoke(server,new object[]{record,"OPERATION_EXECUTION_TIMEOUT","test-only timeout",null});}
        fake.Continue.Set();Need(thread.Join(5000),"fake late worker completed");Need(threadError==null,"fake worker did not throw out of pump");
        string late=Call(server,"operation.get","{\"operation_id\":\"op\"}");
        Need(late.Contains("\"state\":\"UNKNOWN\"")&&late.Contains("\"late_completion\"")&&late.Contains("UNSUPPORTED_PROFILE"),"late failure recorded without resolving UNKNOWN");
        Need(Call(server,"bridge.status","{}").Contains("\"writes_halted\":true"),"late failure does not thaw writes");
    }
    public static int Main(){try{MainTests();Console.WriteLine("F21 asynchronous bridge checks passed: "+checks);return 0;}catch(Exception ex){Console.Error.WriteLine(ex.ToString());return 1;}}
    sealed class Fake:IM2ModelAdapter,IM3OperationAdapter,IM3SweepOperationAdapter
    {
        public int Calls;public string Mode="ok";public ManualResetEvent Started=new ManualResetEvent(false),Continue=new ManualResetEvent(false);
        public M3PlanSnapshot PrepareSweep(M2ModelBinding b,M3SweepRequest r){Calls++;if(Mode=="wait_fail"){Started.Set();if(!Continue.WaitOne(5000))throw new Exception("test synchronization timeout");throw new M2ModelException("UNSUPPORTED_PROFILE","late fake failure");}
            if(Mode=="failed")throw new M2ModelException("UNSUPPORTED_PROFILE","fake rejected profile");
            if(Mode=="unknown")throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN","fake cleanup unknown");
            return new M3PlanSnapshot{Kind="create_sweep",RevisionId=r.RevisionId,SketchId=r.SketchId,AdapterState=new object(),PublicView=new Dictionary<string,object>()};}
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
