// Offline reflection tests against the actual candidate assembly; never starts NX.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class F20PersistenceReflection
{
    static string build;static MethodInfo capture,match;static int passed;
    static Assembly Resolve(object sender,ResolveEventArgs e)
    {
        string name=new AssemblyName(e.Name).Name+".dll";
        foreach(string dir in new string[]{build,@"C:\0sf\ug\NXBIN\managed"}){string file=Path.Combine(dir,name);if(File.Exists(file))return Assembly.LoadFrom(file);}return null;
    }
    static Dictionary<string,object> Curve(string id,string native,string marker)
    {
        string x=marker=="line-a"?"1":marker=="line-changed"?"1.5":"30";
        var row=new Dictionary<string,object>{{"journal_id",id},{"tag",id},{"native_type",native},{"detail_status","SUPPORTED"},{"object_type",native},{"kind",native=="Line"?"LINE":"ARC"},{"units","mm"},{"start",Items(x,"2","3")},{"end",Items("4","5","6")},{"length_mm",5.0}};
        if(native=="Arc"){row["center"]=Items("30","0","0");row["normal"]=Items("0","0","1");row["radius_mm"]=5.0;row["start_angle_rad"]=0.0;row["end_angle_rad"]=1.0;}
        return row;
    }
    static List<object> Items(params object[] rows){return new List<object>(rows);}
    static List<object> Capture(params object[] rows){return (List<object>)capture.Invoke(null,new object[]{Items(rows)});}
    static bool Match(List<object> a,List<object> b){object[] args={a,b,null};return (bool)match.Invoke(null,args);}
    static void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);passed++;Console.WriteLine("PASS "+name);}
    static bool Rejected(params object[] rows)
    {try{Capture(rows);return false;}catch(TargetInvocationException e){return e.InnerException!=null;}}
    public static int Main(string[] args)
    {
        try{
            if(args.Length!=1)throw new ArgumentException("usage: F20PersistenceReflection.exe candidate_build");
            build=Path.GetFullPath(args[0]);AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
            Type t=Assembly.LoadFrom(Path.Combine(build,"M2Entry.dll")).GetType("M2OpenModelAdapter",true);
            capture=t.GetMethod("F20PersistentRecords",BindingFlags.Static|BindingFlags.NonPublic);
            match=t.GetMethod("F20PersistentRecordsMatch",BindingFlags.Static|BindingFlags.NonPublic);
            Check(capture!=null&&match!=null,"required candidate methods exist");
            var l=Curve("ENTITY 1","Line","line-a");var a=Curve("ENTITY 2","Arc","arc-b");
            var l2=Curve("HANDLE 100","Line","line-a");var a2=Curve("HANDLE 200","Arc","arc-b");
            Check(Match(Capture(l,a),Capture(a2,l2)),"reordered records and changed session ids");
            Check(!Match(Capture(l,a),Capture(a2,Curve("HANDLE 100","Line","line-changed"))),"changed geometry rejected");
            Check(!Match(Capture(l,a),Capture(l2)),"different count rejected");
            Check(Rejected(l,Curve("ENTITY 3","Line","line-a")),"duplicate geometry rejected before reopen");
            Check(Rejected(Curve("ENTITY 9","Spline","spline")),"unsupported native type rejected");
            var invalid=Curve("ENTITY 4","Line","line-a");invalid["detail_status"]="PARTIAL";
            Check(Rejected(invalid),"incomplete native geometry rejected");
            Check(!Match(Capture(l,a),Capture(l2,Curve("HANDLE 200","Line","arc-b"))),"changed type rejected");
            Console.WriteLine("8 offline reflection checks; no NX acceptance cases");return 0;
        }catch(Exception e){Console.Error.WriteLine("FAIL "+e);return 1;}
    }
}
