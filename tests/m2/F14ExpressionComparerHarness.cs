// Offline controls against the compiled production SaveCopy comparator.
// No NX session is created; this is not a SaveCopy/reopen test.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class F14ExpressionComparerHarness
{
    static MethodInfo compare;
    static List<object> Row(string type, string rhs, object value)
    {
        Dictionary<string,object> row=new Dictionary<string,object> {
            {"name","f14_expr"},{"right_hand_side",rhs},{"status","OK"},{"value",value},{"unit",null} };
        if (type!=null) row["expression_type"]=type;
        return new List<object>{row};
    }
    static bool Equal(List<object> a,List<object> b) { return (bool)compare.Invoke(null,new object[]{a,b}); }
    static void Check(bool actual,bool expected,string name)
    { if (actual!=expected) throw new Exception(name+": expected "+expected+", actual "+actual); }
    public static int Main(string[] args)
    {
        if (args.Length!=1 || !File.Exists(args[0])) { Console.Error.WriteLine("Expected path to compiled candidate M2Entry.dll"); return 2; }
        string directory=Path.GetDirectoryName(Path.GetFullPath(args[0]));
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender,ResolveEventArgs e) {
            string name=new AssemblyName(e.Name).Name+".dll";
            foreach (string dir in new string[]{directory,@"C:\0sf\ug\NXBIN\managed"}) {
                string file=Path.Combine(dir,name); if (File.Exists(file)) return Assembly.LoadFrom(file);
            }
            return null;
        };
        try {
            Type adapter=Assembly.LoadFrom(args[0]).GetType("M2OpenModelAdapter",true);
            compare=adapter.GetMethod("ExpressionRecordsEqual",BindingFlags.Static|BindingFlags.NonPublic);
            if (compare==null) throw new MissingMethodException("ExpressionRecordsEqual");
            Check(Equal(Row("String",".75",".75"),Row("String",".75",".75")),true,"same string");
            Check(Equal(Row("String",".75",".75"),Row("String","0.75","0.75")),false,"distinct numeric-looking string");
            Check(Equal(Row("String","0.75","0.75"),Row(null,"0.75","0.75")),false,"String versus Number");
            Check(Equal(Row(null,".75",".75"),Row("Number","0.75","0.75")),true,"legacy numeric spelling");
            Check(Equal(Row(null,"1","1"),Row("Number","1.000000000000001","1.000000000000001")),true,"numeric tolerance");
            Check(Equal(Row("Number","1","1"),Row("Number","1.01","1.01")),false,"numeric mismatch");
            Console.WriteLine("F14 compiled expression comparison controls: 6/6 PASS (offline only)");
            return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }
}
