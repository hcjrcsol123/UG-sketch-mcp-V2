// F21 independent geometry data capture. No production adapter or oracle imports.
// Call only from the single authorized NX operator; this class does not start NX.
// Face areas and volume are separately measured; no PASS is inferred here.
using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.UF;

public static class F21StraightGeometryReader
{
    static double[] P(Point3d p) { return new double[]{p.X,p.Y,p.Z}; }
    static int Vertex(List<double[]> points,Point3d p)
    {
        int hit=-1;
        for(int i=0;i<points.Count;i++)
        {
            double[] q=points[i]; double x=q[0]-p.X,y=q[1]-p.Y,z=q[2]-p.Z;
            if(Math.Sqrt(x*x+y*y+z*z)<=1e-9) { if(hit>=0)throw new InvalidOperationException("ambiguous vertex"); hit=i; }
        }
        if(hit<0){hit=points.Count;points.Add(P(p));} return hit;
    }
    public static Dictionary<string,object> Read(Part part,Body body,out Dictionary<string,object> measurement)
    {
        if(part==null||body==null)throw new ArgumentNullException();
        if(!body.IsSolidBody&&!body.IsSheetBody)throw new InvalidOperationException("unsupported body kind");
        List<double[]> vertices=new List<double[]>(); List<object> edges=new List<object>();
        List<object> faces=new List<object>(); Dictionary<Tag,int> edgeIndices=new Dictionary<Tag,int>();
        foreach(Edge edge in body.GetEdges())
        {
            Point3d a,b;edge.GetVertices(out a,out b);int i=Vertex(vertices,a),j=Vertex(vertices,b);
            if(i==j)throw new InvalidOperationException("edge has coincident endpoints");
            edgeIndices.Add(edge.Tag,edges.Count);
            edges.Add(new Dictionary<string,object>{{"vertices",new int[]{i,j}},
                {"curve_type",edge.SolidEdgeType==Edge.EdgeType.Linear?"LINE":edge.SolidEdgeType.ToString()}});
        }
        foreach(Face face in body.GetFaces())
        {
            int type,sense;double radius,radial;double[] point=new double[3],direction=new double[3],bounds=new double[6];
            UFSession.GetUFSession().Modl.AskFaceData(face.Tag,out type,point,direction,bounds,out radius,out radial,out sense);
            List<int> boundary=new List<int>();foreach(Edge edge in face.GetEdges())
            {int i;if(!edgeIndices.TryGetValue(edge.Tag,out i))throw new InvalidOperationException("face edge missing from body");boundary.Add(i);}
            MeasureFaces measured=part.MeasureManager.NewFaceProperties(part.UnitCollection.FindObject("SquareMilliMeter"),
                part.UnitCollection.FindObject("MilliMeter"),0.999999,new IParameterizedSurface[]{face});
            double area;try{area=measured.Area;}finally{try{measured.Dispose();}catch(Exception ex){throw new InvalidOperationException("UNKNOWN face measurement cleanup",ex);}}
            faces.Add(new Dictionary<string,object>{{"edges",boundary.ToArray()},
                {"surface_type",type==UFConstants.UF_MODL_PLANAR_FACE?"PLANE":type.ToString()},
                {"area_mm2",area},{"plane_origin_mm",point},{"plane_normal",direction}});
        }
        object volume=null;double[] statistics=null;double[] properties=null;double[] accuracy=null;
        if(body.IsSolidBody)
        {
            accuracy=new double[11];accuracy[0]=1e-8;accuracy[1]=1e-8;
            properties=new double[47];statistics=new double[13];
            // UF unit system 4 returns SI m^2/m^3, independently converted to mm^3.
            UFSession.GetUFSession().Modl.AskMassProps3d(new Tag[]{body.Tag},1,1,4,1.0,2,accuracy,properties,statistics);
            volume=properties[1]*1e9;
        }
        measurement=new Dictionary<string,object>{{"face_measure_accuracy_requested",0.999999},
            {"mass_uf_units",4},{"mass_uf_accuracy_requested",accuracy},
            {"mass_uf_raw_properties",properties},{"mass_uf_statistics",statistics},
            {"volume_unit_conversion","UF m^3 times 1e9 = mm^3"}};
        return new Dictionary<string,object>{{"body_type",body.IsSolidBody?"solid":"sheet"},
            {"vertices_mm",vertices},{"edges",edges},{"faces",faces},{"volume_mm3",volume}};
    }
}
