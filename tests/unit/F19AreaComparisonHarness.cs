using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

public static class F19AreaComparisonHarness
{
    static MethodInfo area, geometry, detailed, prints;
    static int checks, failed;
    static void Test(string name, bool expected, Func<bool> check)
    {
        checks++;
        try
        {
            bool actual = check();
            if (actual != expected) { failed++; Console.WriteLine("FAIL " + name + ": expected " + expected + ", got " + actual); }
            else Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            failed++;
            TargetInvocationException tie = ex as TargetInvocationException;
            Exception cause = tie != null && tie.InnerException != null ? tie.InnerException : ex;
            Console.WriteLine("FAIL " + name + ": threw " + cause.GetType().FullName + ": " + cause.Message);
        }
    }
    static double Next(double v, long steps) { return BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(v) + steps); }
    static bool Area(object a, object b, out long distance)
    {
        object[] args = { a, b, -1L }; bool result = (bool)area.Invoke(null, args); distance = (long)args[2]; return result;
    }
    static bool Area(object a, object b) { long distance; return Area(a, b, out distance); }
    static Dictionary<string,object> Face(object value)
    {
        return new Dictionary<string,object> { { "tag", "face-1" }, { "area_mm2", value },
            { "edge_tags", new List<object> { "edge-1", "edge-2" } },
            { "metadata", new Dictionary<string,object> { { "area_mm2", 6.0 } } } };
    }
    static Dictionary<string,object> Body(object value)
    {
        return new Dictionary<string,object> { { "tag", "body-1" }, { "volume_mm3", 10.0 },
            { "faces", new List<object> { Face(value) } } };
    }
    static Dictionary<string,object> FaceOf(Dictionary<string,object> body)
    { return (Dictionary<string,object>)((List<object>)body["faces"])[0]; }
    static bool Detailed(Dictionary<string,object> a, Dictionary<string,object> b)
    { object[] args = { a, b, null }; return (bool)detailed.Invoke(null, args); }
    static bool DetailedWithMatrixPath()
    {
        var a=Body(1.0);var b=Body(1.0);
        a["matrix"]=new double[,]{{1.0,2.0},{3.0,4.0}};
        b["matrix"]=new double[,]{{1.0,2.0},{3.0,Next(4.0,1)}};
        object[] args={a,b,null};bool result=(bool)detailed.Invoke(null,args);
        var evidence=(Dictionary<string,object>)args[2];
        foreach(object item in (List<object>)evidence["differences"])
        {
            var row=item as Dictionary<string,object>;
            if(row!=null && Object.Equals(row["path"],"/body/matrix/flat/3"))return !result;
        }
        return false;
    }
    static Dictionary<string,object> Geometry(object value)
    {
        return new Dictionary<string,object> { { "instances", new List<object> { new Dictionary<string,object> {
            { "native_geometry", new Dictionary<string,object> { { "faces", new List<object> { Face(value) } },
                { "edges", new List<object> { "edge-1" } } } } } } } };
    }
    static Dictionary<string,object> GeometryFace(Dictionary<string,object> root)
    {
        var instance=(Dictionary<string,object>)((List<object>)root["instances"])[0];
        var native=(Dictionary<string,object>)instance["native_geometry"];
        return (Dictionary<string,object>)((List<object>)native["faces"])[0];
    }
    static bool GeometryCompare(Dictionary<string,object> a, Dictionary<string,object> b)
    { object[] args = { a, b, "", new List<object>() }; return (bool)geometry.Invoke(null,args); }
    static bool Prints(double mirrorArea, double ordinaryArea, bool allowMirror, bool allowOrdinary, bool missingDetails)
    {
        var oldPrints=new Dictionary<string,string>{{"mirror","mirror-before"},{"ordinary","ordinary-before"}};
        var newPrints=new Dictionary<string,string>{{"mirror",mirrorArea == 1.0 ? "mirror-before" : "mirror-after"},
            {"ordinary",ordinaryArea == 1.0 ? "ordinary-before" : "ordinary-after"}};
        var oldDetails=new Dictionary<string,Dictionary<string,object>>{{"mirror",Body(1.0)},{"ordinary",Body(1.0)}};
        var newDetails=new Dictionary<string,Dictionary<string,object>>{{"mirror",Body(mirrorArea)},{"ordinary",Body(ordinaryArea)}};
        if(missingDetails)newDetails.Remove("mirror");
        var allow=new HashSet<string>();if(allowMirror)allow.Add("mirror");if(allowOrdinary)allow.Add("ordinary");
        object[] args={oldPrints,newPrints,oldDetails,newDetails,allow,null};return (bool)prints.Invoke(null,args);
    }
    static void Need(bool value,string message) { if(!value)throw new Exception(message); }
    static Dictionary<string,object> Map(object value) { return (Dictionary<string,object>)value; }
    static object[] Items(object value) { return (object[])value; }
    static void CheckMatrix(Dictionary<string,object> body,double last)
    {
        var matrix=Map(Map(body["nested"])["matrix"]);
        Need(Convert.ToInt32(matrix["array_rank"])==2,"matrix rank");
        var dimensions=Items(matrix["dimensions"]);var bounds=Items(matrix["lower_bounds"]);var values=Items(matrix["values"]);
        Need(dimensions.Length==2 && Convert.ToInt32(dimensions[0])==2 && Convert.ToInt32(dimensions[1])==2,"matrix dimensions");
        Need(bounds.Length==2 && Convert.ToInt32(bounds[0])==0 && Convert.ToInt32(bounds[1])==0,"matrix lower bounds");
        Need(values.Length==4 && Convert.ToDouble(values[0])==1.0 && Convert.ToDouble(values[1])==2.0 &&
            Convert.ToDouble(values[2])==3.0 && Convert.ToDouble(values[3])==last,"matrix values");
        var faces=Items(body["faces"]);var edges=Items(body["edges"]);
        Need(Object.Equals(body["tag"],"body-1") && faces.Length==6 && edges.Length==12,"complete mock body topology");
        for(int f=0;f<faces.Length;f++)
        {
            var samples=Items(Map(faces[f])["surface_samples"]);
            Need(samples.Length==25,"face sample count");
            for(int s=0;s<samples.Length;s++)
            {
                var sample=Map(samples[s]);
                foreach(string key in new string[]{"uv","point_mm","du","dv","duu","dvv","normal"})
                {
                    var array=Map(sample[key]);int count=key=="uv"?2:3;
                    Need(Convert.ToInt32(array["array_rank"])==1 && Items(array["values"]).Length==count,"face sample "+key);
                }
            }
        }
        foreach(object edge in edges)Need(Items(Map(edge)["domain_samples"]).Length==17,"edge sample count");
    }
    static void AddMockBodyGeometry(Dictionary<string,object> body)
    {
        body["body_tag"]="mock-body";body["is_solid"]=true;body["exact_box_corner_mm"]=new double[]{0,0,0};
        body["exact_box_axes"]=new double[,]{{1,0,0},{0,1,0},{0,0,1}};body["exact_box_lengths_mm"]=new double[]{10,20,30};
        body["consistency_fault_count"]=0;body["consistency_fault_types"]=new int[0];body["consistency_fault_tags"]=new List<object>();
        var faces=(List<object>)body["faces"];faces.Clear();
        for(int f=0;f<6;f++)
        {
            var face=Face(1.0);face["tag"]="face-"+f;face["face_type"]="Planar";face["native_type"]=22;
            face["origin_mm"]=new double[]{f,0,0};face["direction"]=new double[]{0,0,1};face["box_mm"]=new double[]{0,0,0,10,20,30};
            face["radius_mm"]=0.0;face["radius_data"]=0.0;face["normal_sense"]=1;face["uv_limits"]=new double[]{0,1,0,1};
            var samples=new List<object>();
            for(int u=0;u<5;u++)for(int v=0;v<5;v++)samples.Add(new Dictionary<string,object>{
                {"uv",new double[]{u/4.0,v/4.0}},{"point_mm",new double[]{u/4.0,v/4.0,f}},
                {"du",new double[]{1,0,0}},{"dv",new double[]{0,1,0}},{"duu",new double[]{0,0,0}},
                {"dvv",new double[]{0,0,0}},{"normal",new double[]{0,0,1}}});
            face["surface_samples"]=samples;faces.Add(face);
        }
        var edges=new List<object>();
        for(int e=0;e<12;e++)
        {
            var samples=new List<object>();for(int i=0;i<17;i++)samples.Add(new object[]{new double[]{i/16.0,e,0},new double[]{1,0,0}});
            edges.Add(new Dictionary<string,object>{{"tag","edge-"+e},{"edge_type","Linear"},{"face_tags",new List<object>{"face-0","face-1"}},
                {"parameter_limits",new double[]{0,1}},{"domain_samples",samples},{"native_definition",new Dictionary<string,object>{{"mock_line",new double[]{0,0,0,1,0,0}}}}});
        }
        body["edges"]=edges;
    }
    static bool DiagnosticJson(string candidateDir)
    {
        double changed=Next(4.0,1);
        var before=Body(1.0);var after=Body(Next(1.0,1));
        AddMockBodyGeometry(before);AddMockBodyGeometry(after);
        ((Dictionary<string,object>)((List<object>)after["faces"])[0])["area_mm2"]=Next(1.0,1);
        before["nested"]=new Dictionary<string,object>{{"matrix",new double[,]{{1.0,2.0},{3.0,4.0}}}};
        after["nested"]=new Dictionary<string,object>{{"matrix",new double[,]{{1.0,2.0},{3.0,changed}}}};
        var oldPrints=new Dictionary<string,string>{{"mirror","before"}};
        var newPrints=new Dictionary<string,string>{{"mirror","after"}};
        var oldDetails=new Dictionary<string,Dictionary<string,object>>{{"mirror",before}};
        var newDetails=new Dictionary<string,Dictionary<string,object>>{{"mirror",after}};
        object[] args={oldPrints,newPrints,oldDetails,newDetails,new HashSet<string>{"mirror"},null};
        bool result=(bool)prints.Invoke(null,args);
        Need(!result,"non-area matrix change accepted");
        Assembly bridge=Assembly.LoadFrom(Path.Combine(candidateDir,"M2Bridge.dll"));
        MethodInfo canonical=bridge.GetType("NxSketchMcp.M2.M2Json",true).GetMethod("CanonicalText",BindingFlags.Public|BindingFlags.Static);
        string json=(string)canonical.Invoke(null,new object[]{args[5]});
        int byteCount=System.Text.Encoding.UTF8.GetByteCount(json);
        Need(byteCount>0 && System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(json))==json,"UTF-8 evidence length/roundtrip");
        Console.WriteLine("Synthetic 6-face, 12-edge, 25 UV samples/face, 17 samples/edge evidence UTF-8 bytes: "+byteCount+
            "; bridge line limit: 8388608; difference: "+(8388608-byteCount)+" bytes; excludes the surrounding response envelope");
        var root=Map(new JavaScriptSerializer().DeserializeObject(json));
        var differences=Items(root["differences"]);Need(differences.Length==1,"body differences count");
        var bodyDiff=Map(differences[0]);Need(Object.Equals(bodyDiff["body_tag"],"mirror"),"body tag");
        CheckMatrix(Map(bodyDiff["raw_before"]),4.0);
        CheckMatrix(Map(bodyDiff["raw_after"]),changed);
        var comparison=Map(bodyDiff["comparison"]);var changes=Items(comparison["differences"]);bool found=false;
        foreach(object item in changes)
        {
            var row=Map(item);
            if(Object.Equals(row["path"],"/body/nested/matrix/flat/3"))
            {
                Need(Convert.ToDouble(row["before"])==4.0 && Convert.ToDouble(row["after"])==changed,"difference numeric values");
                Need(Object.Equals(row["before_type"],"System.Double") && Object.Equals(row["after_type"],"System.Double"),"difference types");
                found=true;
            }
        }
        Need(found,"matrix difference path missing");
        return true;
    }
    public static int Main(string[] args)
    {
        if(args.Length<1 || args.Length>2 || (args.Length==2 && args[1]!="--diagnostics-only")) { Console.Error.WriteLine("usage: F19AreaComparisonHarness M2Entry.dll [--diagnostics-only]"); return 2; }
        string candidateDir=Path.GetDirectoryName(Path.GetFullPath(args[0]));
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender,ResolveEventArgs e) {
            string name=new AssemblyName(e.Name).Name+".dll", local=Path.Combine(candidateDir,name);
            if(File.Exists(local))return Assembly.LoadFrom(local);
            string root=Environment.GetEnvironmentVariable("UGII_BASE_DIR");
            if(String.IsNullOrEmpty(root))return null;
            string nx=Path.Combine(Path.Combine(root,"NXBIN\\managed"),name);
            return File.Exists(nx)?Assembly.LoadFrom(nx):null;
        };
        try
        {
            Type t=Assembly.LoadFrom(args[0]).GetType("M2OpenModelAdapter",true);
            BindingFlags flags=BindingFlags.Static|BindingFlags.NonPublic;
            area=t.GetMethod("F19AreaEqual",flags);geometry=t.GetMethod("F19GeometryCompare",flags);
            detailed=t.GetMethod("F19DetailedBodyCompare",flags);prints=t.GetMethod("F19BodyPrintsCompare",flags);
            if(area==null || geometry==null || detailed==null || prints==null) throw new Exception("comparison method missing");
            if(args.Length==2)
            {
                Test("JSON retains before/after matrix and non-area difference",true,delegate{return DiagnosticJson(candidateDir);});
                Console.WriteLine("F19 diagnostics serialization: " + checks + " checks, " + failed + " failed");
                return failed==0 ? 0 : 1;
            }
            double one=1.0, ulp1=Next(one,1), ulp8=Next(one,8), ulp9=Next(one,9);
            Test("area identical",true,delegate{return Area(one,one);});
            Test("area one ULP",true,delegate{long d;return Area(one,ulp1,out d)&&d==1;});
            Test("area eight ULP",true,delegate{long d;return Area(ulp8,one,out d)&&d==8;});
            Test("area nine ULP rejected",false,delegate{return Area(one,ulp9);});
            Test("area positive zero",true,delegate{return Area(0.0,0.0);});
            Test("area negative zero rejected",false,delegate{return Area(BitConverter.Int64BitsToDouble(long.MinValue),0.0);});
            Test("area negative rejected",false,delegate{return Area(-1.0,-1.0);});
            Test("area NaN rejected",false,delegate{return Area(Double.NaN,Double.NaN);});
            Test("area infinity rejected",false,delegate{return Area(Double.PositiveInfinity,Double.PositiveInfinity);});
            Test("area integer rejected",false,delegate{return Area(1,1.0);});
            Test("area null rejected",false,delegate{return Area(null,1.0);});
            Test("detailed area one ULP",true,delegate{return Detailed(Body(one),Body(ulp1));});
            Test("detailed area eight ULP",true,delegate{return Detailed(Body(one),Body(ulp8));});
            Test("detailed area nine ULP",false,delegate{return Detailed(Body(one),Body(ulp9));});
            Test("detailed face tag changed",false,delegate{var b=Body(ulp1);FaceOf(b)["tag"]="face-2";return Detailed(Body(one),b);});
            Test("detailed adjacent edge changed",false,delegate{var b=Body(one);((List<object>)FaceOf(b)["edge_tags"])[0]="edge-x";return Detailed(Body(one),b);});
            Test("detailed nested area changed",false,delegate{var b=Body(one);((Dictionary<string,object>)FaceOf(b)["metadata"])["area_mm2"]=Next(6.0,1);return Detailed(Body(one),b);});
            Test("detailed volume changed",false,delegate{var b=Body(one);b["volume_mm3"]=Next(10.0,1);return Detailed(Body(one),b);});
            Test("detailed matrix one ULP rejected at /body/matrix/flat/3",true,delegate{return DetailedWithMatrixPath();});
            Test("detailed area missing",false,delegate{var b=Body(one);FaceOf(b).Remove("area_mm2");return Detailed(Body(one),b);});
            Test("detailed faces missing",false,delegate{var b=Body(one);b.Remove("faces");return Detailed(Body(one),b);});
            Test("detailed faces wrong type",false,delegate{var b=Body(one);b["faces"]="invalid";return Detailed(Body(one),b);});
            Test("detailed area wrong type",false,delegate{var b=Body(one);FaceOf(b)["area_mm2"]=1;return Detailed(Body(one),b);});
            Test("geometry area one ULP",true,delegate{return GeometryCompare(Geometry(one),Geometry(ulp1));});
            Test("geometry area eight ULP",true,delegate{return GeometryCompare(Geometry(one),Geometry(ulp8));});
            Test("geometry area nine ULP",false,delegate{return GeometryCompare(Geometry(one),Geometry(ulp9));});
            Test("geometry nested area changed",false,delegate{var b=Geometry(one);((Dictionary<string,object>)GeometryFace(b)["metadata"])["area_mm2"]=Next(6.0,1);return GeometryCompare(Geometry(one),b);});
            Test("geometry face tag changed",false,delegate{var b=Geometry(one);GeometryFace(b)["tag"]="face-2";return GeometryCompare(Geometry(one),b);});
            Test("geometry adjacency changed",false,delegate{var b=Geometry(one);((List<object>)GeometryFace(b)["edge_tags"])[0]="edge-x";return GeometryCompare(Geometry(one),b);});
            Test("geometry non-area signed zero changed",false,delegate{var a=Geometry(one);var b=Geometry(one);GeometryFace(a)["zero"]=0.0;GeometryFace(b)["zero"]=BitConverter.Int64BitsToDouble(long.MinValue);return GeometryCompare(a,b);});
            Test("geometry area missing",false,delegate{var b=Geometry(one);GeometryFace(b).Remove("area_mm2");return GeometryCompare(Geometry(one),b);});
            Test("geometry area wrong type",false,delegate{var b=Geometry(one);GeometryFace(b)["area_mm2"]=1;return GeometryCompare(Geometry(one),b);});
            Test("geometry first non-target instance",false,delegate{var a=Geometry(one);var b=Geometry(one);var ia=(List<object>)a["instances"];var ib=(List<object>)b["instances"];ia.Add(ia[0]);var other=Geometry(ulp1);ib.Add(((List<object>)other["instances"])[0]);return GeometryCompare(a,b);});
            Test("prints mirror whitelisted",true,delegate{return Prints(ulp1,one,true,false,false);});
            Test("prints mirror not whitelisted",false,delegate{return Prints(ulp1,one,false,false,false);});
            Test("prints ordinary one ULP",false,delegate{return Prints(one,ulp1,true,false,false);});
            Test("prints ordinary also changed",false,delegate{return Prints(ulp1,ulp1,true,false,false);});
            Test("prints mirror nine ULP",false,delegate{return Prints(ulp9,one,true,false,false);});
            Test("prints missing eligible detail",false,delegate{return Prints(ulp1,one,true,false,true);});
            Console.WriteLine("F19 area comparison: " + checks + " checks, " + failed + " failed");
            return failed==0 ? 0 : 1;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 2; }
    }
}
