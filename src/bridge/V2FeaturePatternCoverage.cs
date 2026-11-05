using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.UF;
using NxSketchMcp.M2;

public sealed partial class M2OpenModelAdapter
{
    private static double[] F18Corner(double[] box, int index)
    { return new double[] { box[(index & 1) == 0 ? 0 : 3], box[(index & 2) == 0 ? 1 : 4], box[(index & 4) == 0 ? 2 : 5] }; }

    private static double[] F18Transform(double[] p, M3FeaturePatternRequest r, int i, int j)
    {
        double[] q = new double[3];
        if (r.Mode != "circular")
        {
            for (int n=0;n<3;n++) q[n] = p[n] + i*r.Pitch1Mm*r.Direction1[n] + (r.Mode=="rectangular" ? j*r.Pitch2Mm*r.Direction2[n] : 0);
            return q;
        }
        double angle=i*r.AngleDeg*Math.PI/180.0, c=Math.Cos(angle), s=Math.Sin(angle), dot=0;
        double[] v=new double[3], axis=r.AxisDirection;
        for(int n=0;n<3;n++) {v[n]=p[n]-r.AxisOriginMm[n];dot+=v[n]*axis[n];}
        double[] cross=new double[]{axis[1]*v[2]-axis[2]*v[1],axis[2]*v[0]-axis[0]*v[2],axis[0]*v[1]-axis[1]*v[0]};
        for(int n=0;n<3;n++) q[n]=r.AxisOriginMm[n]+c*v[n]+s*cross[n]+(1-c)*dot*axis[n];
        return q;
    }

    private static List<double[][]> F18Expected(double[] box, M3FeaturePatternRequest request)
    {
        List<double[][]> result=new List<double[][]>();
        int ny=request.Mode=="rectangular"?request.Count2:1;
        for(int i=0;i<request.Count1;i++) for(int j=0;j<ny;j++)
        {
            double[][] points=new double[8][];
            for(int k=0;k<8;k++) {points[k]=F18Transform(F18Corner(box,k),request,i,j);foreach(double value in points[k]) if(!IsFinite(value) || Math.Abs(value)>1e8) throw new M2ModelException("UNSUPPORTED_PATTERN_GEOMETRY","transformed coordinates exceed supported range");}
            result.Add(points);
        }
        return result;
    }

    private static double[] F18Bounds(double[][] points)
    {
        double[] b=new double[]{Double.PositiveInfinity,Double.PositiveInfinity,Double.PositiveInfinity,Double.NegativeInfinity,Double.NegativeInfinity,Double.NegativeInfinity};
        foreach(double[] p in points) for(int a=0;a<3;a++) {b[a]=Math.Min(b[a],p[a]);b[a+3]=Math.Max(b[a+3],p[a]);}
        return b;
    }

    private static bool F18Separate(double[] a,double[] b)
    { for(int i=0;i<3;i++) if(a[i+3]+1e-6<b[i] || b[i+3]+1e-6<a[i]) return true;return false; }

    private static void F18PreflightClearance(Part part,Body seed,List<double[][]> expected)
    {
        List<double[]> boxes=new List<double[]>(); foreach(double[][] p in expected) boxes.Add(F18Bounds(p));
        for(int i=0;i<boxes.Count;i++) for(int j=0;j<i;j++) if(!F18Separate(boxes[i],boxes[j]))
            throw new M2ModelException("UNSUPPORTED_PATTERN_OVERLAP","instance bounding boxes must be disjoint with positive clearance");
        foreach(Body body in part.Bodies)
        {
            if(body.Tag.Equals(seed.Tag)) continue;
            double[] bounds=new double[6];UFSession.GetUFSession().Modl.AskBoundingBox(body.Tag,bounds);
            for(int i=1;i<boxes.Count;i++) if(!F18Separate(boxes[i],bounds))
                throw new M2ModelException("UNSUPPORTED_PATTERN_OVERLAP","instance bounding box may contact an existing body");
        }
    }

    private static int F18Vertex(double[] p,double[][] expected)
    {
        int found=-1;
        for(int i=0;i<8;i++) if(F16SamePoint(p,expected[i])) {if(found>=0)return -1;found=i;}
        return found;
    }

    private static bool F18BodyMatches(Body body,double[][] expected,out Dictionary<string,object> geometry)
    {Dictionary<string,object> native;return F18BodyMatches(body,expected,null,null,out geometry,out native);}

    private static bool F18BodyMatches(Body body,double[][] expected,Dictionary<string,object> measured,double? volume,
        out Dictionary<string,object> geometry,out Dictionary<string,object> native)
    {
        object[] vertices=new object[8]; List<object> edges=new List<object>(),faces=new List<object>();
        List<object> nativeEdges=new List<object>(),nativeFaces=new List<object>();
        Dictionary<string,int> edgeIndices=new Dictionary<string,int>(StringComparer.Ordinal);
        bool ok=body!=null && body.IsSolidBody;
        if(!ok) {geometry=null;native=null;return false;}
        Edge[] actualEdges=body.GetEdges(); Face[] actualFaces=body.GetFaces();
        ok=actualEdges.Length==12 && actualFaces.Length==6;
        HashSet<string> segments=new HashSet<string>(StringComparer.Ordinal);
        foreach(Edge e in actualEdges)
        {
            Point3d a,b;e.GetVertices(out a,out b);double[] av=F16Point(a),bv=F16Point(b);int x=F18Vertex(av,expected),y=F18Vertex(bv,expected);
            if(x>=0) {if(vertices[x]!=null && !F16SamePoint((double[])vertices[x],av))ok=false;vertices[x]=av;}
            if(y>=0) {if(vertices[y]!=null && !F16SamePoint((double[])vertices[y],bv))ok=false;vertices[y]=bv;}
            int diff=x^y;string segment=x<y?x+":"+y:y+":"+x;
            if(x<0 || y<0 || (diff!=1 && diff!=2 && diff!=4) || e.SolidEdgeType!=Edge.EdgeType.Linear || e.GetFaces().Length!=2 || !segments.Add(segment)) ok=false;
            edgeIndices[e.Tag.ToString()]=edges.Count;edges.Add(new int[]{x,y});
            List<string> adjacent=new List<string>();foreach(Face face in e.GetFaces())adjacent.Add(face.Tag.ToString());adjacent.Sort(StringComparer.Ordinal);
            nativeEdges.Add(new Dictionary<string,object>{{"edge_id",e.JournalIdentifier},{"edge_tag",e.Tag.ToString()},{"endpoints_mm",new double[][]{av,bv}},{"adjacent_face_tags",adjacent}});
        }
        HashSet<string> faceKeys=new HashSet<string>(StringComparer.Ordinal);
        foreach(Face f in actualFaces)
        {
            int type,norm;double radius,rd;double[] point=new double[3],direction=new double[3],bounds=new double[6];
            UFSession.GetUFSession().Modl.AskFaceData(f.Tag,out type,point,direction,bounds,out radius,out rd,out norm);
            Edge[] boundary=f.GetEdges();List<int> indices=new List<int>();HashSet<int> cornerSet=new HashSet<int>();
            foreach(Edge edge in boundary)
            {
                int idx;if(!edgeIndices.TryGetValue(edge.Tag.ToString(),out idx)) {ok=false;continue;}
                indices.Add(idx);int[] pair=(int[])edges[idx];cornerSet.Add(pair[0]);cornerSet.Add(pair[1]);
            }
            List<int> sorted=new List<int>(cornerSet);sorted.Sort();
            string key=String.Join(",",sorted.ConvertAll(delegate(int x){return x.ToString();}).ToArray());
            if(type!=UFConstants.UF_MODL_PLANAR_FACE || boundary.Length!=4 || indices.Count!=4 || new HashSet<int>(indices).Count!=4 || cornerSet.Count!=4 || !faceKeys.Add(key)) ok=false;
            int faceAxis=-1,faceSide=-1;for(int axis=0;axis<3;axis++) for(int side=0;side<2;side++)
            {bool same=true;foreach(int corner in cornerSet) if(corner<0 || ((corner>>axis)&1)!=side) same=false;if(same){faceAxis=axis;faceSide=side;}}
            if(faceAxis<0)ok=false;
            double expectedArea=0;bool planeMatch=false,boundsMatch=false;
            if(faceAxis>=0)
            {
                int first=faceSide<<faceAxis,u=(faceAxis+1)%3,v=(faceAxis+2)%3;
                double[] p=expected[first],q=expected[first^(1<<u)],t=expected[first^(1<<v)];
                double[] a=new double[3],b=new double[3];for(int z=0;z<3;z++){a[z]=q[z]-p[z];b[z]=t[z]-p[z];}
                double[] normal=new double[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
                expectedArea=Math.Sqrt(normal[0]*normal[0]+normal[1]*normal[1]+normal[2]*normal[2]);
                double normalLength=Math.Sqrt(direction[0]*direction[0]+direction[1]*direction[1]+direction[2]*direction[2]);
                double alignment=0,offset=0;for(int z=0;z<3;z++){alignment+=normal[z]*direction[z];offset+=(point[z]-p[z])*normal[z];}
                planeMatch=expectedArea>0 && normalLength>0 && Math.Abs(Math.Abs(alignment)/(expectedArea*normalLength)-1)<1e-8 && Math.Abs(offset)/expectedArea<1e-6;
                double[] fb=new double[]{Double.PositiveInfinity,Double.PositiveInfinity,Double.PositiveInfinity,Double.NegativeInfinity,Double.NegativeInfinity,Double.NegativeInfinity};
                foreach(int k in cornerSet)for(int z=0;z<3;z++){fb[z]=Math.Min(fb[z],expected[k][z]);fb[z+3]=Math.Max(fb[z+3],expected[k][z]);}
                boundsMatch=true;for(int z=0;z<6;z++)if(!F16Near(bounds[z],fb[z]))boundsMatch=false;
            }
            double? actualArea=null;
            if(measured!=null)
            {
                List<object> measuredFaces=measured["faces"] as List<object>;
                if(measuredFaces!=null)foreach(object value in measuredFaces){Dictionary<string,object> row=value as Dictionary<string,object>;if(row!=null && Object.Equals(row["tag"],f.Tag.ToString()))actualArea=(double)row["area_mm2"];}
                if(!actualArea.HasValue || Math.Abs(actualArea.Value-expectedArea)>Math.Max(1e-5,expectedArea*1e-7))ok=false;
            }
            if(!planeMatch || !boundsMatch)ok=false;
            nativeFaces.Add(new Dictionary<string,object>{{"face_id",f.JournalIdentifier},{"face_tag",f.Tag.ToString()},{"native_type",type},
                {"point_mm",point},{"direction",direction},{"normal_sense",norm},{"bounds_mm",bounds},{"edge_tags",new List<string>(boundary.Length)},
                {"area_mm2",actualArea},{"expected_area_mm2",expectedArea},{"plane_matches",planeMatch},{"bounds_match",boundsMatch}});
            List<string> edgeTags=(List<string>)((Dictionary<string,object>)nativeFaces[nativeFaces.Count-1])["edge_tags"];
            foreach(Edge edge in boundary)edgeTags.Add(edge.Tag.ToString());edgeTags.Sort(StringComparer.Ordinal);
            faces.Add(indices.ToArray());
        }
        if(segments.Count!=12 || faceKeys.Count!=6)ok=false;
        foreach(object value in nativeEdges)
        {
            Dictionary<string,object> edge=(Dictionary<string,object>)value;string tag=(string)edge["edge_tag"];
            HashSet<string> fromEdge=new HashSet<string>((List<string>)edge["adjacent_face_tags"],StringComparer.Ordinal),fromFaces=new HashSet<string>(StringComparer.Ordinal);
            foreach(object faceValue in nativeFaces)
            {
                Dictionary<string,object> face=(Dictionary<string,object>)faceValue;
                if(((List<string>)face["edge_tags"]).Contains(tag))fromFaces.Add((string)face["face_tag"]);
            }
            if(!fromEdge.SetEquals(fromFaces) || fromEdge.Count!=2)ok=false;
        }
        foreach(object vertex in vertices) if(vertex==null)ok=false;
        geometry=new Dictionary<string,object>{{"vertices_mm",vertices},{"edges",edges},{"faces",faces}};
        double[] d1=new double[3],d2=new double[3],d3=new double[3];for(int z=0;z<3;z++){d1[z]=expected[1][z]-expected[0][z];d2[z]=expected[2][z]-expected[0][z];d3[z]=expected[4][z]-expected[0][z];}
        double expectedVolume=Math.Abs(d1[0]*(d2[1]*d3[2]-d2[2]*d3[1])-d1[1]*(d2[0]*d3[2]-d2[2]*d3[0])+d1[2]*(d2[0]*d3[1]-d2[1]*d3[0]));
        if(volume.HasValue && Math.Abs(volume.Value-expectedVolume)>Math.Max(1e-5,expectedVolume*1e-7))ok=false;
        native=new Dictionary<string,object>{{"body_id",body.JournalIdentifier},{"body_tag",body.Tag.ToString()},
            {"volume_mm3",volume},{"expected_volume_mm3",expectedVolume},{"faces",nativeFaces},{"edges",nativeEdges}};
        return ok;
    }
}
