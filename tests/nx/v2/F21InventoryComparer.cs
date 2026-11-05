using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

// Pure comparison of captured data; no NX calls. Synthetic tests are not NX evidence.
public static class F21InventoryComparer
{
    static readonly JavaScriptSerializer J = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
    const double Mm = 1e-9;
    static void Need(bool ok,string s) { if(!ok) throw new InvalidOperationException(s); }
    static double D(object x) { Need(x is double || x is int || x is long || x is decimal,"numeric value required"); double v=x is decimal ? Double.Parse(((decimal)x).ToString("G29",CultureInfo.InvariantCulture),CultureInfo.InvariantCulture) : Convert.ToDouble(x,CultureInfo.InvariantCulture); Need(!Double.IsNaN(v)&&!Double.IsInfinity(v),"nonfinite value"); return v; }
    public static double Number(object x){return D(x);}
    static Dictionary<string,object> Dict(object x){var d=x as Dictionary<string,object>;Need(d!=null,"object required");return d;}
    static object[] Arr(object x){var a=x as object[];Need(a!=null,"array required");return a;}
    static double[] Vec(object x,int n){var a=Arr(x);Need(a.Length==n,"vector length");double[] v=new double[n];for(int i=0;i<n;i++)v[i]=D(a[i]);return v;}
    static double Norm(double[] a){double v=Math.Sqrt(a[0]*a[0]+a[1]*a[1]+a[2]*a[2]);Need(!Double.IsNaN(v)&&!Double.IsInfinity(v),"nonfinite vector norm");return v;}
    static double Dot(double[] a,double[] b){return a[0]*b[0]+a[1]*b[1]+a[2]*b[2];}
    static double[] Sub(double[] a,double[] b){return new double[]{a[0]-b[0],a[1]-b[1],a[2]-b[2]};}
    static double[] Cross(double[] a,double[] b){return new double[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};}
    static ulong Order(double x){ulong b=unchecked((ulong)BitConverter.DoubleToInt64Bits(x));ulong mag=b&0x7fffffffffffffffUL;return (b>>63)!=0 ? 0x8000000000000000UL-mag : 0x8000000000000000UL+mag;}
    public static ulong Steps(double a,double b){Need(!Double.IsNaN(a)&&!Double.IsInfinity(a)&&!Double.IsNaN(b)&&!Double.IsInfinity(b),"nonfinite ULP");ulong x=Order(a),y=Order(b);return x>=y?x-y:y-x;}
    // Each sample binds the exact UV to its own capture; tolerance tests are in mm.
    static double[][] CheckMap(object value,string tag,double[] lim,double[] normal,double[] origin)
    {
        var m=Dict(value);Need((string)m["face_tag"]==tag,"mapping face tag");double[] ml=Vec(m["uv_limits"],4);
        for(int i=0;i<4;i++)Need(ml[i]==lim[i],"mapping limits differ");
        Need(lim[0]<lim[1]&&lim[2]<lim[3],"degenerate UV limits");double n=Norm(normal);Need(n>0&&!Double.IsInfinity(n),"invalid normal");
        var samples=Arr(m["samples"]);Need(samples.Length==5,"five mapping samples required");double u0=lim[0],u1=lim[1],v0=lim[2],v1=lim[3];
        double um=u0/2+u1/2,vm=v0/2+v1/2;double[][] uv={new[]{u0,v0},new[]{u0,v1},new[]{u1,v0},new[]{u1,v1},new[]{um,vm}};
        double[][] points=new double[5][],dus=new double[5][],dvs=new double[5][];
        for(int i=0;i<5;i++){var s=Dict(samples[i]);double[] actual=Vec(s["uv"],2);Need(actual[0]==uv[i][0]&&actual[1]==uv[i][1],"mapping UV binding");points[i]=Vec(s["point_mm"],3);dus[i]=Vec(s["du"],3);dvs[i]=Vec(s["dv"],3);double nu=Norm(dus[i]),nv=Norm(dvs[i]);Need(nu>0&&nv>0&&!Double.IsInfinity(nu*nv)&&Norm(Cross(dus[i],dvs[i]))>1e-12*nu*nv,"degenerate derivatives");
            Need(Math.Abs(Dot(dus[i],normal))/n*(u1-u0)<=Mm&&Math.Abs(Dot(dvs[i],normal))/n*(v1-v0)<=Mm,"derivatives not planar");}
        for(int i=0;i<5;i++){double[] rel=Sub(points[i],origin);Need(Math.Abs(Dot(rel,normal))/n<=Mm,"points not on native plane");
            double[] expect=new double[3];for(int k=0;k<3;k++)expect[k]=points[4][k]+dus[4][k]*(uv[i][0]-um)+dvs[4][k]*(uv[i][1]-vm);
            Need(Norm(Sub(points[i],expect))<=Mm,"non-affine mapping points");Need(Norm(Sub(dus[i],dus[4]))*(u1-u0)<=Mm&&Norm(Sub(dvs[i],dvs[4]))*(v1-v0)<=Mm,"non-affine derivatives");}
        return new double[][]{points[0],points[1],points[2],points[3],points[4],dus[0],dus[1],dus[2],dus[3],dus[4],dvs[0],dvs[1],dvs[2],dvs[3],dvs[4]};
    }
    public static void ValidateMap(object value,string tag,object limits,object normal,object origin){CheckMap(value,tag,Vec(limits,4),Vec(normal,3),Vec(origin,3));}
    static void Face(Dictionary<string,object> a,Dictionary<string,object> b,Dictionary<string,object> ma,Dictionary<string,object> mb,List<object> exceptions,string path)
    {
        Need(a.Count==b.Count,"face fields changed "+path);foreach(string key in a.Keys)Need(b.ContainsKey(key),"face field missing "+path);
        foreach(string key in a.Keys)if(key!="uv_limits")Need(J.Serialize(a[key])==J.Serialize(b[key]),"face changed "+path+"."+key);
        if(J.Serialize(a["uv_limits"])==J.Serialize(b["uv_limits"]))return;
        Need((string)a["face_tag"]==(string)b["face_tag"] && a["is_planar"] is bool && (bool)a["is_planar"] && b["is_planar"] is bool && (bool)b["is_planar"] && Convert.ToInt32(a["native_type"])==22,"changed UV face not same plane");
        string tag=(string)a["face_tag"];Need(ma.ContainsKey(tag)&&mb.ContainsKey(tag),"missing physical mapping "+tag);double[] ua=Vec(a["uv_limits"],4),ub=Vec(b["uv_limits"],4),normal=Vec(a["direction"],3),origin=Vec(a["origin_mm"],3);ulong[] steps=new ulong[4];
        for(int i=0;i<4;i++){steps[i]=Steps(ua[i],ub[i]);Need(steps[i]<=8,"UV exceeds 8 ULP "+tag);}
        double[][] pa=CheckMap(ma[tag],tag,ua,normal,origin),pb=CheckMap(mb[tag],tag,ub,normal,origin);double maxdu=0,maxdv=0,maxworld=0;
        for(int i=0;i<5;i++){maxdu=Math.Max(maxdu,Math.Max(Norm(pa[i+5]),Norm(pb[i+5])));maxdv=Math.Max(maxdv,Math.Max(Norm(pa[i+10]),Norm(pb[i+10])));maxworld=Math.Max(maxworld,Norm(Sub(pa[i],pb[i])));}
        double bound=maxdu*Math.Max(Math.Abs(ua[0]-ub[0]),Math.Abs(ua[1]-ub[1]))+maxdv*Math.Max(Math.Abs(ua[2]-ub[2]),Math.Abs(ua[3]-ub[3]));
        Need(!Double.IsNaN(bound)&&!Double.IsInfinity(bound)&&bound<=Mm&&maxworld<=Mm,"physical UV/world bound exceeded "+tag);
        exceptions.Add(new Dictionary<string,object>{{"face_tag",tag},{"steps",steps},{"physical_bound_mm",bound},{"max_world_delta_mm",maxworld}});
    }
    static void ValidateSnapshot(object[] inventory,Dictionary<string,object> maps)
    {
        Need(inventory.Length==3 && inventory[1] is bool && !(bool)inventory[1] && inventory[2] is bool && (bool)inventory[2],"dirty or active snapshot");
        var seen=new HashSet<string>();foreach(object entry in Arr(inventory[0])){string row=entry as string;Need(row!=null,"inventory row type");if(!row.StartsWith("B|",StringComparison.Ordinal))continue;
            string[] parts=row.Split(new[]{'|'},5);Need(parts.Length==5,"body row shape");var body=Dict(J.DeserializeObject(parts[4]));foreach(object item in Arr(body["faces"])){
                var face=Dict(item);Need(face["is_planar"] is bool,"planarity flag missing");if(!(bool)face["is_planar"])continue;
                Need(Convert.ToInt32(face["native_type"])==22,"planar native type mismatch");string tag=(string)face["face_tag"];Need(!String.IsNullOrEmpty(tag)&&seen.Add(tag)&&maps.ContainsKey(tag),"missing or duplicate plane mapping");
                ValidateMap(maps[tag],tag,face["uv_limits"],face["direction"],face["origin_mm"]);
            }}
        Need(maps.Count==seen.Count,"extra plane mapping");
    }
    public static Dictionary<string,object> Compare(string before,string after,Dictionary<string,object> mapsBefore,Dictionary<string,object> mapsAfter)
    {
        List<object> exceptions=new List<object>();var result=new Dictionary<string,object>{{"passed",false},{"exceptions",exceptions}};
        try{
            var a=Arr(J.DeserializeObject(before));var b=Arr(J.DeserializeObject(after));Need(a.Length==3&&b.Length==3,"inventory shape");ValidateSnapshot(a,mapsBefore);ValidateSnapshot(b,mapsAfter);
            List<object> worldChecks=new List<object>();result["world_checks"]=worldChecks;
            foreach(string tag in mapsBefore.Keys){Need(mapsAfter.ContainsKey(tag),"mapping identity changed");var sa=Arr(Dict(mapsBefore[tag])["samples"]);var sb=Arr(Dict(mapsAfter[tag])["samples"]);double max=0;
                for(int i=0;i<5;i++){double[] pa=Vec(Dict(sa[i])["point_mm"],3),pb=Vec(Dict(sb[i])["point_mm"],3);max=Math.Max(max,Norm(Sub(pa,pb)));}
                Need(max<=Mm,"physical world drift "+tag);worldChecks.Add(new Dictionary<string,object>{{"face_tag",tag},{"max_world_delta_mm",max}});}
            Need(mapsBefore.Count==mapsAfter.Count,"mapping count changed");Need(J.Serialize(a[1])==J.Serialize(b[1])&&J.Serialize(a[2])==J.Serialize(b[2]),"dirty or active sketch changed");var ar=Arr(a[0]);var br=Arr(b[0]);Need(ar.Length==br.Length,"inventory row count");
            for(int i=0;i<ar.Length;i++){string x=ar[i] as string,y=br[i] as string;Need(x!=null&&y!=null,"inventory row type");if(x==y)continue;string[] xs=x.Split(new[]{'|'},5),ys=y.Split(new[]{'|'},5);Need(xs.Length==5&&ys.Length==5&&xs[0]=="B"&&xs[0]==ys[0]&&xs[1]==ys[1]&&xs[2]==ys[2]&&xs[3]==ys[3],"inventory row changed "+i);
                var da=Dict(J.DeserializeObject(xs[4]));var db=Dict(J.DeserializeObject(ys[4]));Need(da.Count==db.Count,"body fields changed");foreach(string k in da.Keys){Need(db.ContainsKey(k),"body field missing");if(k!="faces")Need(J.Serialize(da[k])==J.Serialize(db[k]),"body changed "+i+"."+k);}
                var fa=Arr(da["faces"]);var fb=Arr(db["faces"]);Need(fa.Length==fb.Length,"face count changed");for(int j=0;j<fa.Length;j++)Face(Dict(fa[j]),Dict(fb[j]),mapsBefore,mapsAfter,exceptions,"body["+i+"].face["+j+"]");
            }
            result["passed"]=true;
        }catch(Exception e){result["first_difference"]=e.Message;}
        return result;
    }
}
