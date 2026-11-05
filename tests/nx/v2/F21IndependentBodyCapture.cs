// F21 sample capture aggregator; no NX startup, no product-code dependencies.
// Target points select nearest independently evaluated native geometry; only the
// actual evaluated points are returned in the oracle's measured samples.
using System;
using System.Collections.Generic;
using NXOpen;

public static class F21IndependentBodyCapture
{
    static double[][] Profile=new double[][]{new double[]{0,0},new double[]{3,0},new double[]{0,2},
        new double[]{.75,0},new double[]{1.5,0},new double[]{2.25,0},new double[]{2.25,.5},
        new double[]{1.5,1},new double[]{.75,1.5},new double[]{0,1.5},new double[]{0,1},new double[]{0,.5}};
    public static Dictionary<string,object> Capture(Body body,string path,string mode,double angleDeg,double[] offset)
    {
        if(body==null || offset==null || offset.Length!=3)throw new ArgumentException("body and offset required");
        if(path!="straight" && path!="line_arc")throw new ArgumentException("unsupported path");
        if(mode!="transport" && mode!="linear_twist")throw new ArgumentException("unsupported mode");
        if(Double.IsNaN(angleDeg)||Double.IsInfinity(angleDeg)||Math.Abs(angleDeg)>90 || (mode=="transport"&&angleDeg!=0))throw new ArgumentException("unsupported angle");
        double length=path=="straight"?5.0:5.0+5.0*Math.PI;
        double[] positions=path=="straight"?new double[]{0,1.25,2.5,3.75,5}:
            new double[]{0,2.5,5,5+10*Math.PI/8,5+10*Math.PI/4,5+30*Math.PI/8,5+5*Math.PI};
        List<object> samples=new List<object>(),raw=new List<object>();
        for(int i=0;i<positions.Length;i++)
        {
            double s=positions[i],theta=(path=="line_arc" && s>5)?(s-5)/10:0,angle=angleDeg*Math.PI/180*s/length;
            double[] origin=theta==0?new double[]{0,0,s}:new double[]{10*(1-Math.Cos(theta)),0,5+10*Math.Sin(theta)};
            double[] x=new double[]{Math.Cos(theta),0,-Math.Sin(theta)},y=new double[]{0,1,0};
            List<object> points=new List<object>(),detail=new List<object>();
            for(int j=0;j<Profile.Length;j++)
            {
                double u=Profile[j][0],v=Profile[j][1],a=u*Math.Cos(angle)-v*Math.Sin(angle),b=u*Math.Sin(angle)+v*Math.Cos(angle);
                double[] target=new double[3];for(int k=0;k<3;k++)target[k]=origin[k]+offset[k]+a*x[k]+b*y[k];
                // Vertices lie on longitudinal edges; other points lie on side
                // faces except at the start/end where they lie on boundary edges.
                bool edge=(j<3 || i==0 || i==positions.Length-1);
                Dictionary<string,object> record;
                if(!edge && path=="line_arc" && Math.Abs(s-5)<1e-12)
                {
                    // A smooth join may be represented either inside one face
                    // or by a real seam edge. Keep the failed first attempt.
                    try {record=F21FaceSampleReader.Read(body,target);}
                    catch(InvalidOperationException ex) {if(ex.Message.IndexOf("UNKNOWN",StringComparison.OrdinalIgnoreCase)>=0)throw;
                        record=F21EdgeSampleReader.Read(body,target);record.Add("face_attempt_error",ex.Message);}
                }
                else record=edge?F21EdgeSampleReader.Read(body,target):F21FaceSampleReader.Read(body,target);
                Dictionary<string,object> selected=(Dictionary<string,object>)record["selected"];
                double[] measured=(double[])selected["point_mm"],local=new double[3];for(int k=0;k<3;k++)local[k]=measured[k]-offset[k];
                points.Add(local);detail.Add(record);
            }
            samples.Add(new Dictionary<string,object>{{"s_mm",s},{"points_mm",points}});
            raw.Add(new Dictionary<string,object>{{"s_mm",s},{"native_reads",detail}});
        }
        Dictionary<string,object> oracleInput=new Dictionary<string,object>{{"path",path},{"mode",mode},{"angle_deg",angleDeg},{"samples",samples}};
        return new Dictionary<string,object>{{"oracle_input",oracleInput},{"offset_mm",offset},{"native_reads",raw},
            {"body_structure",F21SurfaceStructureReader.Read(body)},
            {"scope","native sample/structure capture only; oracle and association/preservation evaluated separately"}};
    }
}
