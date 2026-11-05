// Independent complete native edge / face structure capture for F21 evidence.
// No product reader imports; no operation on NX except read-only UF queries.
using System;
using System.Collections.Generic;
using System.Reflection;
using NXOpen;
using NXOpen.UF;

public static class F21SurfaceStructureReader
{
    static Dictionary<string,object> Fields(object value)
    {
        Dictionary<string,object> output=new Dictionary<string,object>();
        foreach(FieldInfo f in value.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public))
        {
            object field=f.GetValue(value);Array array=field as Array;
            if(array!=null&&array.Rank>1)
            {
                int[] lengths=new int[array.Rank],lowerBounds=new int[array.Rank];
                for(int i=0;i<array.Rank;i++){lengths[i]=array.GetLength(i);lowerBounds[i]=array.GetLowerBound(i);}
                output.Add(f.Name,NestedArray(array,0,new int[array.Rank]));
                output.Add(f.Name+"_array_metadata",new Dictionary<string,object>{{"rank",array.Rank},{"lengths",lengths},{"lower_bounds",lowerBounds},{"axis_order","native CLR dimension order; no flattening"}});
            }
            else output.Add(f.Name,field);
        }
        return output;
    }
    static object NestedArray(Array array,int dimension,int[] indices)
    {
        List<object> result=new List<object>();int lower=array.GetLowerBound(dimension),upper=array.GetUpperBound(dimension);
        for(int i=lower;i<=upper;i++){indices[dimension]=i;result.Add(dimension+1==array.Rank?array.GetValue(indices):NestedArray(array,dimension+1,indices));}
        return result;
    }
    public static Dictionary<string,object> Read(Body body)
    {
        if(body==null)throw new ArgumentNullException("body");
        UFSession uf=UFSession.GetUFSession();List<object> faces=new List<object>(),edges=new List<object>();
        foreach(Face face in body.GetFaces())
        {
            int type,sense;double radius,radial;double[] origin=new double[3],direction=new double[3],box=new double[6],uv=new double[4];
            uf.Modl.AskFaceData(face.Tag,out type,origin,direction,box,out radius,out radial,out sense);uf.Modl.AskFaceUvMinmax(face.Tag,uv);
            List<string> boundary=new List<string>();foreach(Edge e in face.GetEdges())boundary.Add(e.Tag.ToString());
            Dictionary<string,object> row=new Dictionary<string,object>{{"face_id",face.JournalIdentifier},{"face_tag",face.Tag.ToString()},
                {"native_type",type},{"is_planar",type==UFConstants.UF_MODL_PLANAR_FACE},{"face_type",face.SolidFaceType.ToString()},{"origin_mm",origin},{"direction",direction},
                {"bounds_mm",box},{"radius_mm",radius},{"radius_data",radial},{"normal_sense",sense},{"uv_limits",uv},{"edge_tags",boundary}};
            if(face.SolidFaceType==Face.FaceType.Parametric)
            {UFModl.Bsurface value;uf.Modl.AskBsurf(face.Tag,out value);row.Add("native_bsurface",Fields(value));}
            faces.Add(row);
        }
        foreach(Edge edge in body.GetEdges())
        {
            List<string> adjacent=new List<string>();foreach(Face face in edge.GetFaces())adjacent.Add(face.Tag.ToString());
            Point3d a,b;edge.GetVertices(out a,out b);
            Dictionary<string,object> row=new Dictionary<string,object>{{"edge_id",edge.JournalIdentifier},{"edge_tag",edge.Tag.ToString()},
                {"edge_type",edge.SolidEdgeType.ToString()},{"face_tags",adjacent},
                {"vertices_mm",new double[][]{new double[]{a.X,a.Y,a.Z},new double[]{b.X,b.Y,b.Z}}}};
            IntPtr evaluator=IntPtr.Zero;
            try{
                uf.Eval.Initialize2(edge.Tag,out evaluator);if(evaluator==IntPtr.Zero)throw new InvalidOperationException("missing evaluator");
                double[] limits=new double[2];uf.Eval.AskLimits(evaluator,limits);row.Add("parameter_limits",limits);
                double[] start=new double[3],end=new double[3],firstDerivative=new double[3],lastDerivative=new double[3];
                uf.Eval.Evaluate(evaluator,1,limits[0],start,firstDerivative);uf.Eval.Evaluate(evaluator,1,limits[1],end,lastDerivative);
                row.Add("limit_points_mm",new double[][]{start,end});
                bool line,arc,ellipse,spline;uf.Eval.IsLine(evaluator,out line);uf.Eval.IsArc(evaluator,out arc);
                uf.Eval.IsEllipse(evaluator,out ellipse);uf.Eval.IsSpline(evaluator,out spline);
                if(line){UFEval.Line value;uf.Eval.AskLine(evaluator,out value);row.Add("evaluator_type","LINE");row.Add("native_definition",Fields(value));}
                else if(arc){UFEval.Arc value;uf.Eval.AskArc(evaluator,out value);row.Add("evaluator_type","ARC");row.Add("native_definition",Fields(value));}
                else if(ellipse){UFEval.Ellipse value;uf.Eval.AskEllipse(evaluator,out value);row.Add("evaluator_type","ELLIPSE");row.Add("native_definition",Fields(value));}
                else if(spline){UFEval.Spline value;uf.Eval.AskSpline(evaluator,out value);Dictionary<string,object> definition=Fields(value);
                    int n;double[] poles,knots;uf.Eval.AskSplineControlPts(evaluator,out n,out poles);definition.Add("control_point_count",n);definition.Add("control_points",poles);
                    uf.Eval.AskSplineKnots(evaluator,out n,out knots);definition.Add("knot_count",n);definition.Add("knots",knots);row.Add("evaluator_type","SPLINE");row.Add("native_definition",definition);}
                else throw new InvalidOperationException("unsupported native edge definition");
            }finally{if(evaluator!=IntPtr.Zero)try{uf.Eval.Free(evaluator);}catch(Exception ex){throw new InvalidOperationException("UNKNOWN structure evaluator cleanup",ex);}}
            edges.Add(row);
        }
        return new Dictionary<string,object>{{"body_id",body.JournalIdentifier},{"body_tag",body.Tag.ToString()},
            {"solid",body.IsSolidBody},{"sheet",body.IsSheetBody},{"faces",faces},{"edges",edges}};
    }
}
