using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

public static class F19UvComparisonHarness
{
    static MethodInfo detailed,prints;
    static int checks,failed;
    static long decodedFloatCount,decodedIntegerCount;
    const double Eps=2.2204460492503131e-16;
    static void Test(string label,bool expected,Func<bool> run)
    {
        checks++;
        try { bool got=run();if(got!=expected){failed++;Console.WriteLine("FAIL "+label+": expected "+expected+", got "+got);}else Console.WriteLine("PASS "+label); }
        catch(Exception ex){failed++;var tie=ex as TargetInvocationException;var cause=tie!=null&&tie.InnerException!=null?tie.InnerException:ex;Console.WriteLine("FAIL "+label+": threw "+cause.GetType().FullName+": "+cause.Message);}
    }
    static double Next(double v,long n){return BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(v)+n);}
    static double[] Vector(int axis,double value){double[] r=new double[3];r[axis]=value;return r;}
    static Dictionary<string,object> Body(double xOrigin)
    {
        var faces=new List<object>();
        for(int axis=0;axis<3;axis++)for(int side=0;side<2;side++)
        {
            int u=(axis+1)%3,v=(axis+2)%3;
            var origin=new double[]{xOrigin,0,0};origin[axis]+=side;
            var normal=Vector(axis,side==0?-1:1);var du=Vector(u,1);var dv=Vector(v,1);
            var box=new double[]{xOrigin,0,0,xOrigin+1,1,1};box[axis]=origin[axis];box[axis+3]=origin[axis];
            var samples=new List<object>();
            for(int iu=0;iu<5;iu++)for(int iv=0;iv<5;iv++)
            {
                double a=iu/4.0,b=iv/4.0;var point=(double[])origin.Clone();point[u]+=a;point[v]+=b;
                samples.Add(new Dictionary<string,object>{{"uv",new double[]{a,b}},{"point_mm",point},{"du",(double[])du.Clone()},
                    {"dv",(double[])dv.Clone()},{"duu",new double[3]},{"dvv",new double[3]},{"normal",(double[])normal.Clone()}});
            }
            faces.Add(new Dictionary<string,object>{{"tag","face-"+axis+"-"+side},{"face_type","Planar"},{"native_type",22},
                {"edge_tags",new List<string>{"e0","e1","e2","e3"}},{"origin_mm",origin},{"direction",normal},{"box_mm",box},
                {"radius_mm",0.0},{"radius_data",0.0},{"normal_sense",side==0?-1:1},{"uv_limits",new double[]{0,1,0,1}},
                {"surface_samples",samples},{"area_mm2",1.0}});
        }
        return new Dictionary<string,object>{{"body_tag","mirror"},{"is_solid",true},
            {"exact_box_corner_mm",new double[]{xOrigin,0,0}},{"exact_box_axes",new double[,]{{1,0,0},{0,1,0},{0,0,1}}},
            {"exact_box_lengths_mm",new double[]{1,1,1}},{"consistency_fault_count",0},
            {"consistency_fault_types",new int[0]},{"consistency_fault_tags",new List<string>()},
            {"faces",faces},{"edges",new List<object>()}};
    }
    static Dictionary<string,object> Face(Dictionary<string,object> body,int index){return (Dictionary<string,object>)((List<object>)body["faces"])[index];}
    static Dictionary<string,object> Sample(Dictionary<string,object> face,int index){return (Dictionary<string,object>)((List<object>)face["surface_samples"])[index];}
    static void ShiftUv(Dictionary<string,object> body,double delta)
    {
        var f=Face(body,0);var limits=(double[])f["uv_limits"];limits[0]=delta;limits[1]=1+delta;
        for(int iu=0;iu<5;iu++)for(int iv=0;iv<5;iv++)((double[])Sample(f,iu*5+iv)["uv"])[0]=limits[0]+(limits[1]-limits[0])*iu/4.0;
    }
    static void ShiftPoints(Dictionary<string,object> body,double delta)
    {
        var f=Face(body,0);for(int i=0;i<25;i++)((double[])Sample(f,i)["point_mm"])[1]+=delta;
    }
    static void OffsetArea(Dictionary<string,object> body){Face(body,0)["area_mm2"]=Next(1.0,1);}
    static bool Detailed(Dictionary<string,object> before,Dictionary<string,object> after)
    {object[] args={before,after,null};return (bool)detailed.Invoke(null,args);}
    static bool BodyPrints(Dictionary<string,object> a,Dictionary<string,object> b,bool allowed)
    {
        var p=new Dictionary<string,string>{{"mirror","old"}};var q=new Dictionary<string,string>{{"mirror","new"}};
        var x=new Dictionary<string,Dictionary<string,object>>{{"mirror",a}};var y=new Dictionary<string,Dictionary<string,object>>{{"mirror",b}};
        var whitelist=new HashSet<string>();if(allowed)whitelist.Add("mirror");
        object[] args={p,q,x,y,whitelist,null};return (bool)prints.Invoke(null,args);
    }
    static bool IdenticalInvalid(Action<Dictionary<string,object>> mutate)
    {var a=Body(0);var b=Body(0);mutate(a);mutate(b);OffsetArea(b);return Detailed(a,b);}
    static bool Numeric(object value)
    {
        var map=value as Dictionary<string,object>;
        return value is int || value is long || value is double || value is decimal || value is float || (map!=null && map.ContainsKey("__f19_ieee754_bits__"));
    }
    static double Number(object value)
    {
        var map=value as Dictionary<string,object>;
        if(map!=null && map.ContainsKey("__f19_ieee754_bits__"))
        {
            long bits=unchecked((long)Convert.ToUInt64((string)map["__f19_ieee754_bits__"],16));
            double decoded=BitConverter.Int64BitsToDouble(bits);
            if(BitConverter.DoubleToInt64Bits(decoded)!=bits)throw new Exception("native IEEE-754 bit roundtrip mismatch");
            decodedFloatCount++;return decoded;
        }
        double number=Convert.ToDouble(value);
        if(value is int || value is long)
        {
            if(Convert.ToDecimal(number)!=Convert.ToDecimal(value))throw new Exception("native integer-to-double loses value");
            decodedIntegerCount++;
        }
        return number;
    }
    static object Restore(object value,string key)
    {
        var map=value as Dictionary<string,object>;
        if(map!=null)
        {
            if(map.ContainsKey("__f19_ieee754_bits__"))return Number(map);
            if(map.ContainsKey("array_rank") && map.ContainsKey("dimensions") && map.ContainsKey("lower_bounds") && map.ContainsKey("values"))
            {
                int rank=Convert.ToInt32(map["array_rank"]);var dims=(object[])map["dimensions"];var bounds=(object[])map["lower_bounds"];var values=(object[])map["values"];
                if(rank!=dims.Length || rank!=bounds.Length)throw new Exception("invalid native array rank");
                for(int i=0;i<rank;i++)if(Convert.ToInt32(bounds[i])!=0)throw new Exception("unexpected nonzero array bound");
                if(rank==1)
                {
                    int length=Convert.ToInt32(dims[0]);if(length!=values.Length)throw new Exception("native array length mismatch");
                    if(length==0 && key=="consistency_fault_types")return new int[0];
                    bool numbers=true;foreach(object item in values)if(!Numeric(item))numbers=false;
                    if(numbers){var result=new double[length];for(int i=0;i<length;i++)result[i]=Number(values[i]);return result;}
                    var objects=new object[length];for(int i=0;i<length;i++)objects[i]=Restore(values[i],"");return objects;
                }
                if(rank==2)
                {
                    int rows=Convert.ToInt32(dims[0]),cols=Convert.ToInt32(dims[1]);if(rows*cols!=values.Length)throw new Exception("native matrix length mismatch");
                    var result=new double[rows,cols];for(int i=0;i<values.Length;i++)result[i/cols,i%cols]=Number(values[i]);return result;
                }
                throw new Exception("unsupported native array rank "+rank);
            }
            var resultMap=new Dictionary<string,object>(StringComparer.Ordinal);
            foreach(var item in map)resultMap[item.Key]=Restore(item.Value,item.Key);
            return resultMap;
        }
        var list=value as object[];
        if(list!=null){var result=new List<object>();foreach(object item in list)result.Add(Restore(item,""));return result;}
        if(Numeric(value))
        {
            if(key=="consistency_fault_count" || key=="native_type" || key=="normal_sense")return Convert.ToInt32(value);
            return Number(value);
        }
        return value;
    }
    static Dictionary<string,object> Map(object value){return (Dictionary<string,object>)value;}
    static object[] Items(object value){return (object[])value;}
    static object NativeRaw(string path,bool after)
    {
        var parser=new JavaScriptSerializer();parser.MaxJsonLength=Int32.MaxValue;
        var root=Map(parser.DeserializeObject(File.ReadAllText(path)));
        var cases=Items(root["cases"]);Dictionary<string,object> row=null;
        foreach(object c in cases){var candidate=Map(c);if(candidate.ContainsKey("checks")){row=candidate;break;}}
        if(row==null)throw new Exception("native case with checks missing");
        var checks=Map(row["checks"]);var bodies=Map(checks["bodies"]);var evidence=Map(bodies["evidence"]);var fingerprints=Map(evidence["fingerprints"]);
        var differences=Items(fingerprints["differences"]);if(differences.Length!=1)throw new Exception("unexpected native fingerprint differences");
        var diff=Map(differences[0]);return diff[after?"raw_after":"raw_before"];
    }
    static Dictionary<string,object> NativeBody(object raw)
    {
        var body=(Dictionary<string,object>)Restore(raw,"");var faces=(List<object>)body["faces"];
        if(faces.Count!=6)throw new Exception("native face count is not six");
        foreach(object value in faces)if(((List<object>)Map(value)["surface_samples"]).Count!=25)throw new Exception("native sample count is not 25");
        return body;
    }
    static void NativeShiftUv(Dictionary<string,object> body,double delta)
    {
        var face=Face(body,0);var limits=(double[])face["uv_limits"];limits[0]+=delta;limits[1]+=delta;
        for(int iu=0;iu<5;iu++)for(int iv=0;iv<5;iv++)
        {
            var uv=(double[])Sample(face,iu*5+iv)["uv"];
            uv[0]=limits[0]+(limits[1]-limits[0])*iu/4.0;
        }
    }
    static void NativeShiftPoints(Dictionary<string,object> body,double distance)
    {
        var f=Face(body,0);var du=(double[])Sample(f,0)["du"];
        double length=Math.Sqrt(du[0]*du[0]+du[1]*du[1]+du[2]*du[2]);
        for(int i=0;i<25;i++){var point=(double[])Sample(f,i)["point_mm"];for(int j=0;j<3;j++)point[j]+=distance*du[j]/length;}
    }
    static bool NativeControl(Dictionary<string,object> a,Dictionary<string,object> b)
    {
        object[] args={a,b,null};bool result=(bool)detailed.Invoke(null,args);
        if(!result)
        {
            var evidence=Map(args[2]);Console.WriteLine("Native replay detail area type: "+Face(a,0)["area_mm2"].GetType().FullName+
                "; native_type: "+Face(a,0)["native_type"].GetType().FullName+"; sense: "+Face(a,0)["normal_sense"].GetType().FullName+
                "; consistency count: "+a["consistency_fault_count"].GetType().FullName+"; fault types: "+a["consistency_fault_types"].GetType().FullName+
                "; UV limits: "+Face(a,0)["uv_limits"].GetType().FullName+"; box axes: "+a["exact_box_axes"].GetType().FullName);
            foreach(object item in (List<object>)evidence["uv_validation"])
            {var row=Map(item);Console.WriteLine("Native face "+row["face_tag"]+" uv_validated="+row["validated"]);}
            foreach(object item in (List<object>)evidence["areas"])
            {var row=Map(item);Console.WriteLine("Native face "+row["face_tag_before"]+" area_accepted="+row["accepted"]+" ulp="+row["ulp_distance"]);}
            for(int fi=0;fi<6;fi++)
            {
                var face=Face(a,fi);var limits=(double[])face["uv_limits"];
                for(int i=0;i<25;i++)
                {
                    var uv=(double[])Sample(face,i)["uv"];double u=limits[0]+(limits[1]-limits[0])*(i/5)/4.0,v=limits[2]+(limits[3]-limits[2])*(i%5)/4.0;
                    if(uv[0]!=u||uv[1]!=v){Console.WriteLine("Native face "+face["tag"]+" grid mismatch index="+i+
                        " actual="+uv[0].ToString("R")+","+uv[1].ToString("R")+" expected="+u.ToString("R")+","+v.ToString("R"));break;}
                }
            }
        }
        return result;
    }
    public static int Main(string[] args)
    {
        if(args.Length!=2){Console.Error.WriteLine("usage: F19UvComparisonHarness M2Entry.dll native-evidence.json");return 2;}
        string dir=Path.GetDirectoryName(Path.GetFullPath(args[0]));
        AppDomain.CurrentDomain.AssemblyResolve+=delegate(object sender,ResolveEventArgs e){
            string name=new AssemblyName(e.Name).Name+".dll",local=Path.Combine(dir,name);if(File.Exists(local))return Assembly.LoadFrom(local);
            string root=Environment.GetEnvironmentVariable("UGII_BASE_DIR");if(String.IsNullOrEmpty(root))return null;
            string nx=Path.Combine(Path.Combine(root,"NXBIN\\managed"),name);return File.Exists(nx)?Assembly.LoadFrom(nx):null;};
        try
        {
            Type type=Assembly.LoadFrom(args[0]).GetType("M2OpenModelAdapter",true);var flags=BindingFlags.NonPublic|BindingFlags.Static;
            detailed=type.GetMethod("F19DetailedBodyCompare",flags);prints=type.GetMethod("F19BodyPrintsCompare",flags);
            if(detailed==null||prints==null)throw new Exception("comparison method missing");
            Test("valid identical 6-face grid",true,delegate{return Detailed(Body(0),Body(0));});
            Test("valid 1 ULP area",true,delegate{var b=Body(0);OffsetArea(b);return Detailed(Body(0),b);});
            Test("valid 32 epsilon UV shift",true,delegate{var b=Body(0);ShiftUv(b,32*Eps);return Detailed(Body(0),b);});
            Test("valid 64 epsilon UV boundary",true,delegate{var b=Body(0);ShiftUv(b,64*Eps);return Detailed(Body(0),b);});
            Test("65 epsilon UV over boundary",false,delegate{var b=Body(0);ShiftUv(b,65*Eps);return Detailed(Body(0),b);});
            Test("large coherent UV shift",false,delegate{var b=Body(0);ShiftUv(b,0.125);return Detailed(Body(0),b);});
            Test("valid 32 epsilon point shift",true,delegate{var b=Body(0);ShiftPoints(b,32*Eps);return Detailed(Body(0),b);});
            Test("valid 64 epsilon point boundary",true,delegate{var b=Body(0);ShiftPoints(b,64*Eps);return Detailed(Body(0),b);});
            Test("65 epsilon point over boundary",false,delegate{var b=Body(0);ShiftPoints(b,65*Eps);return Detailed(Body(0),b);});
            Test("large coordinate 0.9e-7 point shift",true,delegate{var b=Body(1e8);ShiftPoints(b,0.9e-7);return Detailed(Body(1e8),b);});
            Test("large coordinate 1.1e-7 point shift",false,delegate{var b=Body(1e8);ShiftPoints(b,1.1e-7);return Detailed(Body(1e8),b);});
            Test("invalid sampled UV grid even when both snapshots match",false,delegate{return IdenticalInvalid(delegate(Dictionary<string,object> b){((double[])Sample(Face(b,0),7)["uv"])[0]+=Eps;});});
            Test("invalid UV limits order",false,delegate{return IdenticalInvalid(delegate(Dictionary<string,object> b){((double[])Face(b,0)["uv_limits"])[1]=-1;});});
            Test("non-finite UV rejected",false,delegate{return IdenticalInvalid(delegate(Dictionary<string,object> b){((double[])Sample(Face(b,0),0)["uv"])[0]=Double.NaN;});});
            Test("non-finite point rejected",false,delegate{return IdenticalInvalid(delegate(Dictionary<string,object> b){((double[])Sample(Face(b,0),0)["point_mm"])[0]=Double.PositiveInfinity;});});
            Test("point off plane",false,delegate{return IdenticalInvalid(delegate(Dictionary<string,object> b){((double[])Sample(Face(b,0),12)["point_mm"])[0]=1e-6;});});
            Test("sample violates affine map",false,delegate{return IdenticalInvalid(delegate(Dictionary<string,object> b){((double[])Sample(Face(b,0),12)["point_mm"])[1]+=1e-6;});});
            Test("degenerate du",false,delegate{return IdenticalInvalid(delegate(Dictionary<string,object> b){Sample(Face(b,0),12)["du"]=new double[3];});});
            Test("parallel du and dv",false,delegate{return IdenticalInvalid(delegate(Dictionary<string,object> b){Sample(Face(b,0),12)["dv"]=new double[]{0,1,0};});});
            Test("wrong normal sign",false,delegate{return IdenticalInvalid(delegate(Dictionary<string,object> b){((double[])Sample(Face(b,0),12)["normal"])[0]=1;});});
            Test("non-unit normal",false,delegate{return IdenticalInvalid(delegate(Dictionary<string,object> b){((double[])Sample(Face(b,0),12)["normal"])[0]=-2;});});
            Test("changed normal even within unit tolerance",false,delegate{var b=Body(0);((double[])Sample(Face(b,0),12)["normal"])[0]=Next(-1.0,1);return Detailed(Body(0),b);});
            Test("changed derivative even within geometric tolerance",false,delegate{var b=Body(0);((double[])Sample(Face(b,0),12)["du"])[1]=Next(1.0,1);return Detailed(Body(0),b);});
            Test("changed face tag",false,delegate{var b=Body(0);Face(b,0)["tag"]="different";return Detailed(Body(0),b);});
            Test("changed face adjacency",false,delegate{var b=Body(0);((List<string>)Face(b,0)["edge_tags"])[0]="different";return Detailed(Body(0),b);});
            Test("missing sample",false,delegate{var b=Body(0);((List<object>)Face(b,0)["surface_samples"]).RemoveAt(0);return Detailed(Body(0),b);});
            Test("wrong UV value type",false,delegate{var b=Body(0);Sample(Face(b,0),0)["uv"]="bad";return Detailed(Body(0),b);});
            Test("whitelisted mirror accepts UV perturbation",true,delegate{var b=Body(0);ShiftUv(b,32*Eps);return BodyPrints(Body(0),b,true);});
            Test("non-mirror rejects UV perturbation",false,delegate{var b=Body(0);ShiftUv(b,32*Eps);return BodyPrints(Body(0),b,false);});
            Test("whitelisted mirror rejects excessive UV shift",false,delegate{var b=Body(0);ShiftUv(b,0.125);return BodyPrints(Body(0),b,true);});
            Console.WriteLine("Native replay source: "+Path.GetFullPath(args[1]));
            object nativeBefore=NativeRaw(args[1],false),nativeAfter=NativeRaw(args[1],true);
            var nativeSchema=NativeBody(nativeBefore);var nativeFace=Face(nativeSchema,0);var nativeSample=Sample(nativeFace,0);
            Console.WriteLine("Native reconstructed types: area="+nativeFace["area_mm2"].GetType().FullName+
                ", radius="+nativeFace["radius_mm"].GetType().FullName+", radius_data="+nativeFace["radius_data"].GetType().FullName+
                ", native_type="+nativeFace["native_type"].GetType().FullName+", normal_sense="+nativeFace["normal_sense"].GetType().FullName+
                ", fault_count="+nativeSchema["consistency_fault_count"].GetType().FullName+", fault_types="+nativeSchema["consistency_fault_types"].GetType().FullName+
                ", uv_limits="+nativeFace["uv_limits"].GetType().FullName+", point="+nativeSample["point_mm"].GetType().FullName+
                ", box_axes="+nativeSchema["exact_box_axes"].GetType().FullName);
            var rawFace=Map(Items(Map(nativeBefore)["faces"])[0]);var rawSample=Map(Items(rawFace["surface_samples"])[15]);
            var rawUv=Map(rawSample["uv"]);var witness=Map(Items(rawUv["values"])[0]);
            Console.WriteLine("Native UV witness JSON literal="+witness["__f19_literal__"]+"; Python float.hex="+witness["__f19_python_float_hex__"]+
                "; Python bits="+witness["__f19_ieee754_bits__"]+"; CLR bits="+
                unchecked((ulong)BitConverter.DoubleToInt64Bits(((double[])Sample(nativeFace,15)["uv"])[0])).ToString("x16"));
            Test("native JSON float bit-exact witness",true,delegate{return BitConverter.DoubleToInt64Bits(((double[])Sample(nativeFace,15)["uv"])[0])==0x3f589374bc6a7ef5L;});
            Test("native raw before/after six faces by 25 samples",true,delegate{return NativeControl(NativeBody(nativeBefore),NativeBody(nativeAfter));});
            Test("native raw before self control",true,delegate{return Detailed(NativeBody(nativeBefore),NativeBody(nativeBefore));});
            Test("native whitelist accepts original raw snapshots",true,delegate{return BodyPrints(NativeBody(nativeBefore),NativeBody(nativeAfter),true);});
            Test("native non-mirror rejects original raw differences",false,delegate{return BodyPrints(NativeBody(nativeBefore),NativeBody(nativeAfter),false);});
            Test("native coherent excessive UV shift",false,delegate{var b=NativeBody(nativeAfter);NativeShiftUv(b,0.125);return Detailed(NativeBody(nativeBefore),b);});
            Test("native coherent excessive tangent point shift",false,delegate{var b=NativeBody(nativeAfter);NativeShiftPoints(b,1e-4);return Detailed(NativeBody(nativeBefore),b);});
            Test("native corrupted UV grid",false,delegate{var b=NativeBody(nativeAfter);((double[])Sample(Face(b,0),7)["uv"])[0]+=1e-7;return Detailed(NativeBody(nativeBefore),b);});
            Test("native area nine ULP rejected",false,delegate{var a=NativeBody(nativeBefore);var b=NativeBody(nativeAfter);Face(b,0)["area_mm2"]=Next((double)Face(a,0)["area_mm2"],9);return Detailed(a,b);});
            Test("native changed face tag rejected",false,delegate{var b=NativeBody(nativeAfter);Face(b,0)["tag"]="other";return Detailed(NativeBody(nativeBefore),b);});
            Test("native changed face adjacency rejected",false,delegate{var b=NativeBody(nativeAfter);((List<object>)Face(b,0)["edge_tags"])[0]="other";return Detailed(NativeBody(nativeBefore),b);});
            Console.WriteLine("Native number decode checks: "+decodedFloatCount+" IEEE-754 float bit roundtrips, "+decodedIntegerCount+" exact integer-to-double conversions (including repeated snapshot reconstruction)");
            Console.WriteLine("F19 UV comparison: "+checks+" checks, "+failed+" failed");return failed==0?0:1;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 2;}
    }
}
