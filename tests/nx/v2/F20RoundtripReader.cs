// Read-only raw NX collector invoked by f20-audit-roundtrip.py in the same session.
// The evaluator is always released, including on error. Does not use product code.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using NXOpen;
using NXOpen.UF;

public static class F20RoundtripReader
{
    static double[] V(Point3d p){return new double[]{p.X,p.Y,p.Z};}
    static double[] V(Vector3d p){return new double[]{p.X,p.Y,p.Z};}
    static object Read(Curve c)
    {
        var row=new Dictionary<string,object>{{"id",c.JournalIdentifier},{"tag",c.Tag.ToString()},{"length_mm",c.GetLength()}};
        Line line=c as Line;Arc arc=c as Arc;
        if(line!=null){row["kind"]="LINE";row["start_mm"]=V(line.StartPoint);row["end_mm"]=V(line.EndPoint);}
        else if(arc!=null){Point3d center;Vector3d x,y;arc.GetOrientation(out center,out x,out y);row["kind"]="ARC";row["center_mm"]=V(center);row["axis_x"]=V(x);row["axis_y"]=V(y);row["radius_mm"]=arc.Radius;row["start_angle_rad"]=arc.StartAngle;row["sweep_rad"]=arc.EndAngle-arc.StartAngle;}
        else throw new InvalidOperationException("unsupported native curve type: "+c.GetType().Name);
        UFSession uf=UFSession.GetUFSession();IntPtr eval=IntPtr.Zero;double[] limits=new double[2];var samples=new List<double[]>();
        try{
            uf.Eval.Initialize(c.Tag,out eval);if(eval==IntPtr.Zero)throw new InvalidOperationException("null native evaluator");
            uf.Eval.AskLimits(eval,limits);if(limits[1]<=limits[0])throw new InvalidOperationException("invalid parameter limits");
            for(int i=0;i<=4;i++){double[] p=new double[3];uf.Curve.EvaluateCurve(c.Tag,limits[0]+(limits[1]-limits[0])*i/4.0,0,p);samples.Add(p);}
        }finally{if(eval!=IntPtr.Zero)uf.Eval.Free(eval);}
        row["parameter_limits"]=limits;row["points_mm"]=samples;return row;
    }
    public static int Run()
    {
        string path=Environment.GetEnvironmentVariable("F20_NX_RAW_OUTPUT");
        if(String.IsNullOrEmpty(path))throw new InvalidOperationException("F20_NX_RAW_OUTPUT required");
        Part part=Session.GetSession().Parts.Work;if(part==null)throw new InvalidOperationException("no open work part");
        var rows=new List<object>();foreach(Curve c in part.Curves.ToArray())rows.Add(Read(c));
        var serializer=new JavaScriptSerializer();serializer.MaxJsonLength=Int32.MaxValue;
        using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
        using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(serializer.Serialize(rows));
        return 0;
    }
    public static int GetUnloadOption(string dummy){return (int)Session.LibraryUnloadOption.Explicitly;}
}
