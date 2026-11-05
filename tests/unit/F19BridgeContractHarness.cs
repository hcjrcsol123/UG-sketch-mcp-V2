using System;
using System.Collections.Generic;
using System.Reflection;
using NxSketchMcp.M2;

public static class F19BridgeContractHarness
{
    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Type Server = typeof(M2BridgeServer);
    static readonly MethodInfo Parser = Server.GetMethod("ParseM3FeatureMirrorRequest", Private);
    static int checks;
    static void Assert(bool value, string label) { checks++; if (!value) throw new Exception(label); }
    static List<object> V(object a, object b, object c) { return new List<object> { a, b, c }; }
    static Dictionary<string, object> Base(bool datum)
    {
        Dictionary<string, object> a = new Dictionary<string, object> { {"operation_id", "op"}, {"ownership_id", "owner"},
            {"binding_epoch", 1}, {"revision_id", "rev"}, {"seed_feature_id", "EXTRUDE(0)"} };
        if (datum) a.Add("datum_plane_id", "DATUM_PLANE(0)");
        else { a.Add("plane_origin_mm", V(1.0, 2.0, 3.0)); a.Add("plane_normal", V(1.0 + 5e-10, 0.0, 0.0)); }
        return a;
    }
    static M3FeatureMirrorRequest Parse(Dictionary<string, object> args, bool valid, string label)
    {
        object[] call = { args, "rev", "owner", 1, null }; M3FeatureMirrorRequest request = null;
        try { request = (M3FeatureMirrorRequest)Parser.Invoke(null, call); }
        catch (TargetInvocationException ex) { if (ex.InnerException == null || ex.InnerException.GetType().Name != "BridgeException") throw; }
        Assert((request != null && call[4] != null) == valid, label); return request;
    }
    static void Set(object obj, string name, object value) { Server.GetField(name, Private).SetValue(obj, value); }
    static string Invoke(M2BridgeServer server, string command, string parameters)
    { return server.Invoke(new string('a', 64), "{\"id\":\"1\",\"command\":\"" + command + "\",\"params\":" + parameters + "}"); }
    static void BridgeChecks()
    {
        FakeAdapter adapter = new FakeAdapter(); M2BridgeServer server = new M2BridgeServer(adapter);
        Set(server, "_started", true); Set(server, "_token", new string('a', 64));
        string response = Invoke(server, "bridge.status", "{}");
        Assert(response.Contains("\"model_feature_mirror_adapter_available\":true") && response.Contains("\"model_feature_mirror_write_enabled\":false"), "capabilities before ownership: " + response);
        string args = "{\"operation_id\":\"op\",\"ownership_id\":\"owner\",\"binding_epoch\":1,\"revision_id\":\"rev\",\"seed_feature_id\":\"EXTRUDE(0)\",\"datum_plane_id\":\"DATUM_PLANE(0)\"}";
        response = Invoke(server, "operation.prepare_feature_mirror", args);
        Assert(response.Contains("AUTHORIZATION_REQUIRED"), "JSON command must enforce ownership");
        response = Invoke(server, "operation.prepare_feature_mirror", args.Replace("\"datum_plane_id\"", "\"extra\""));
        Assert(response.Contains("INVALID_REQUEST"), "JSON command must validate request");
        M2ModelBinding binding = new M2ModelBinding { RevisionId = "rev", BindingEpoch = 1 };
        Set(server, "_binding", binding); Set(server, "_ownershipState", "AI_OWNED");
        response = Invoke(server, "bridge.status", "{}");
        Assert(response.Contains("\"model_feature_mirror_write_enabled\":true"), "capability after ownership");
        Set(server, "_writesHalted", true);
        Assert(Invoke(server, "bridge.status", "{}").Contains("\"model_feature_mirror_write_enabled\":false"), "halt disables capability");
        Assert(Invoke(server, "operation.prepare_feature_mirror", args).Contains("OPERATION_OUTCOME_UNKNOWN"), "halt blocks preparation");
        Set(server, "_writesHalted", false);
        Type recordType = Server.GetNestedType("OperationRecord", BindingFlags.NonPublic);
        object record = Activator.CreateInstance(recordType, true);
        MethodInfo prepare = Server.GetMethod("PerformPrepareM3Operation", Private);
        object[] call = new object[prepare.GetParameters().Length]; call[0] = record; call[1] = binding; call[2] = "owner";
        for (int i = 3; i < call.Length; i++) if (prepare.GetParameters()[i].ParameterType == typeof(M3FeatureMirrorRequest)) call[i] = Parse(Base(true), true, "datum parse for dispatch");
        Dictionary<string, object> result = (Dictionary<string, object>)prepare.Invoke(server, call);
        Dictionary<string, object> plan = (Dictionary<string, object>)result["plan"];
        Assert(adapter.Calls == 1 && adapter.Last.DatumPlaneId == "DATUM_PLANE(0)" && (string)plan["kind"] == "create_feature_mirror", "adapter dispatch and plan kind");
        adapter.Unknown = true;
        recordType.GetField("State", Private).SetValue(record, "RUNNING");
        recordType.GetField("Kind", Private).SetValue(record, "prepare_m3_feature_mirror");
        result = (Dictionary<string, object>)prepare.Invoke(server, call);
        Assert((string)result["preparation_state"] == "UNKNOWN" && (string)recordType.GetField("State", Private).GetValue(record) == "UNKNOWN", "unknown preparation recorded");
        Assert(Invoke(server, "bridge.status", "{}").Contains("\"model_feature_mirror_write_enabled\":false"), "unknown preparation freezes writes");
        MethodInfo verify = Server.GetMethod("M3VerificationChecksPassed", Private);
        string[] names = { "mirror_definition", "mirror_geometry", "model_preservation", "body_effect", "geometry", "bodies", "history", "support_associations" };
        Dictionary<string, object> checksMap = new Dictionary<string, object>(); foreach (string name in names) checksMap[name] = new Dictionary<string, object> { { "status", "VERIFIED" } };
        Assert((bool)verify.Invoke(null, new object[] { checksMap, "create_feature_mirror" }), "complete mirror verification");
        foreach (string name in names) { checksMap[name] = false; Assert(!(bool)verify.Invoke(null, new object[] { checksMap, "create_feature_mirror" }), "missing verification " + name); checksMap[name] = new Dictionary<string, object> { { "status", "VERIFIED" } }; }
    }
    public static int Main()
    {
        try { return Run(); }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().FullName + ": " + ex.Message + "\n" + ex.StackTrace); return 1; }
    }
    static int Run()
    {
        Assert(Parser != null, "parser present");
        M3FeatureMirrorRequest plane = Parse(Base(false), true, "explicit plane");
        Assert(plane.PlaneNormal[0] == 1.0 + 5e-10 && plane.DatumPlaneId == null, "normal preserved");
        Assert(Parse(Base(true), true, "datum plane").PlaneNormal == null, "datum mode has no explicit vectors");
        Dictionary<string, object> x = Base(false); x.Remove("plane_normal"); Parse(x, false, "missing normal");
        x = Base(false); x.Remove("plane_origin_mm"); Parse(x, false, "missing origin");
        x = Base(false); x.Add("datum_plane_id", "DATUM_PLANE(0)"); Parse(x, false, "mixed forms");
        x = Base(true); x.Add("plane_normal", V(1, 0, 0)); Parse(x, false, "datum with partial vector");
        x = Base(true); x.Add("extra", 1); Parse(x, false, "extra key");
        x = Base(false); x["plane_normal"] = V(1.0 + 2e-9, 0, 0); Parse(x, false, "normal tolerance");
        x = Base(false); x["plane_normal"] = V(0, 0, 0); Parse(x, false, "zero normal");
        x = Base(false); x["plane_normal"] = V(Double.PositiveInfinity, 0, 0); Parse(x, false, "infinite normal");
        x = Base(false); x["plane_origin_mm"] = V(0, Double.NaN, 0); Parse(x, false, "nan origin");
        x = Base(false); x["plane_normal"] = V(true, 0, 0); Parse(x, false, "boolean normal");
        x = Base(false); x["plane_normal"] = new List<object> { 1, 0 }; Parse(x, false, "vector length");
        x = Base(true); x["datum_plane_id"] = " "; Parse(x, false, "empty datum");
        BridgeChecks(); Console.WriteLine("F19 bridge contract checks passed: " + checks); return 0;
    }
    sealed class FakeAdapter : IM2ModelAdapter, IM3OperationAdapter, IM3FeatureMirrorOperationAdapter
    {
        public int Calls; public bool Unknown; public M3FeatureMirrorRequest Last;
        public M3PlanSnapshot PrepareFeatureMirror(M2ModelBinding binding, M3FeatureMirrorRequest request)
        { Calls++; Last = request; if (Unknown) throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN", "test unknown"); return new M3PlanSnapshot { Kind = "create_feature_mirror", RevisionId = request.RevisionId, AdapterState = new object(), PublicView = new Dictionary<string, object>() }; }
        public bool IsBindingCurrent(M2ModelBinding binding, out string reason) { reason = null; return true; }
        public M2ModelBinding Takeover(string p, string a, string s, string l, int e) { throw new NotImplementedException(); }
        public Dictionary<string, object> GetSession(M2ModelBinding b) { throw new NotImplementedException(); }
        public Dictionary<string, object> InspectPart(M2ModelBinding b) { throw new NotImplementedException(); }
        public Dictionary<string, object> InspectSketch(M2ModelBinding b, string id) { throw new NotImplementedException(); }
        public void Release(M2ModelBinding b) { }
        public M3PlanSnapshot PrepareSketch(M2ModelBinding b, M3SketchRequest r) { throw new NotImplementedException(); }
        public M3PlanSnapshot PrepareExtrude(M2ModelBinding b, M3ExtrudeRequest r) { throw new NotImplementedException(); }
        public M3ExecutionResult ExecutePlan(M2ModelBinding b, M3PlanSnapshot p) { throw new NotImplementedException(); }
        public M3VerificationResult VerifyPlan(M2ModelBinding b, M3PlanSnapshot p) { throw new NotImplementedException(); }
    }
}
