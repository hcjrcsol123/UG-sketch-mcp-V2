using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using NxSketchMcp.M2;

public static class F19ExpressionSerializationHarness
{
    static int checks;
    static void Require(bool value,string message) { checks++; if(!value)throw new Exception(message); }
    static Dictionary<string,object> Expression(string rhs,string owner,params string[] users)
    {
        return new Dictionary<string,object>{{"tag","11"},{"id","p0"},{"name","p0"},{"type","Number"},
            {"rhs",rhs},{"value",0.0},{"units","MilliMeter"},{"owner_tag",owner},{"user_tags",new List<string>(users)}};
    }
    public static int Main(string[] args)
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender,ResolveEventArgs eventArgs) {
                string root=Environment.GetEnvironmentVariable("UGII_BASE_DIR");
                string file=Path.Combine(root,"NXBIN","managed",new AssemblyName(eventArgs.Name).Name+".dll");
                return File.Exists(file)?Assembly.LoadFrom(file):null;
            };
            Assembly entry=Assembly.LoadFrom(args[0]);
            Type adapter=entry.GetType("M2OpenModelAdapter",true);
            MethodInfo capture=adapter.GetMethod("F19ExpressionStates",BindingFlags.NonPublic|BindingFlags.Static);
            Require(capture!=null && capture.ReturnType==typeof(Dictionary<string,object>),"expression capture must return serializable outer map");
            Type state=adapter.GetNestedType("M3PlanState",BindingFlags.NonPublic);
            foreach(string name in new string[]{"F19ExpressionsBefore","F19ExpressionsAfter"})
            {
                FieldInfo f=state.GetField(name,BindingFlags.NonPublic|BindingFlags.Instance);
                Require(f!=null && f.FieldType==typeof(Dictionary<string,object>),"expression plan map must be serializable: "+name);
            }
            Dictionary<string,object> before=new Dictionary<string,object>{{"11",Expression("0","21","21")}};
            Dictionary<string,object> after=new Dictionary<string,object>{{"11",Expression("0 // Used By ...",null,"21","22")}};
            Dictionary<string,object> change=new Dictionary<string,object>{{"tag","11"},{"before",before["11"]},{"after",after["11"]},
                {"allowed_shared_seed_expression",true},{"output_references_same_expression",true}};
            Dictionary<string,object> sharing=new Dictionary<string,object>{{"status","VERIFIED"},{"before",before},{"after",after},
                {"seed_expression_tags",new List<string>{"11"}},{"output_expression_tags",new List<string>{"11"}},{"changes",new List<object>{change}}};
            Dictionary<string,object> evidence=new Dictionary<string,object>{{"model_preservation",new Dictionary<string,object>{{"expression_sharing",sharing}}}};
            string a=M2Json.CanonicalText(before),b=M2Json.CanonicalText(after),full=M2Json.CanonicalText(evidence);
            Require(a.Contains("\"owner_tag\":\"21\"") && a.Contains("\"user_tags\":[\"21\"]"),"before raw expression missing");
            Require(b.Contains("0 // Used By ...") && b.Contains("\"owner_tag\":null"),"after raw expression missing");
            Require(full.Contains("\"seed_expression_tags\":[\"11\"]") && full.Contains("\"output_references_same_expression\":true"),"nested diagnostics missing");
            Require(full==M2Json.CanonicalText(evidence),"canonical serialization is unstable");
            bool rejected=false;
            try {M2Json.CanonicalText(new Dictionary<string,Dictionary<string,object>>{{"11",Expression("0","21","21")}});}
            catch(InvalidOperationException){rejected=true;}
            Require(rejected,"control: unsupported typed nested dictionary must reject");
            Console.WriteLine("F19 expression serialization checks passed: "+checks);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
