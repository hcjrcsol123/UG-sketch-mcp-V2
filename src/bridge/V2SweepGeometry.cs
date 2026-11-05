using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.UF;

// Transaction-local, read-only sweep checks. Successful finite sampling is
// evidence of agreement at the stated locations, not a proof of an entire face.
public static class V2SweepGeometry
{
    // Thrown only after a complete, finite native comparison establishes a mismatch.
    public sealed class GeometryMismatchException : Exception
    {
        public IDictionary<string,object> Evidence { get; private set; }
        private GeometryMismatchException(string message, IDictionary<string,object> evidence) : base(message) { Evidence=evidence; }
        internal static GeometryMismatchException Create(string message, IDictionary<string,object> evidence)
        { return new GeometryMismatchException(message,evidence); }
    }
    public sealed class CleanupException : Exception
    {
        public CleanupException(Exception inner) : base("Sweep geometry evaluator cleanup failed.", inner) { }
    }
    private const double Tol = 1.0e-6;
    private const double DirectionTol = 1.0e-7;
    private const int PathSamples = 32;
    private const int EndEdgeSamples = 32;
    // Independent finite-sample acceptance limit in millimetres;
    // this does not claim a bound over the entire surface.
    private const double SampleTol = 0.01;
    private sealed class Station
    {
        internal int Segment;
        internal int Step;
        internal double ArcLengthMm;
        internal double[] PathPoint;
        internal double[] Tangent;
        internal double AngleDeg;
        internal double[][] Points;
    }

    private static GeometryMismatchException Mismatch(string message, params object[] values)
    {
        Dictionary<string,object> evidence=new Dictionary<string,object>(StringComparer.Ordinal);
        for(int i=0;i<values.Length;i+=2) evidence.Add((string)values[i],values[i+1]);
        return GeometryMismatchException.Create(message,evidence);
    }
    private static List<string> EdgeTags(Edge[] edges)
    {
        if(edges==null) throw new InvalidOperationException("Edge list is unavailable.");
        List<string> tags=new List<string>();
        foreach(Edge edge in edges) { if(edge==null || edge.Tag==Tag.Null) throw new InvalidOperationException("Edge list is incomplete."); tags.Add(edge.Tag.ToString()); }
        return tags;
    }
    private static List<string> FaceTags(Face[] faces)
    {
        if(faces==null) throw new InvalidOperationException("Face list is unavailable.");
        List<string> tags=new List<string>();
        foreach(Face face in faces) { if(face==null || face.Tag==Tag.Null) throw new InvalidOperationException("Face list is incomplete."); tags.Add(face.Tag.ToString()); }
        return tags;
    }
    private static List<object> FaceBoundaries(Face[] faces)
    {
        List<object> result=new List<object>();
        foreach(Face face in faces)
        {
            if(face==null) throw new InvalidOperationException("Face list is incomplete.");
            result.Add(new Dictionary<string,object> { {"face_tag",face.Tag.ToString()}, {"edge_tags",EdgeTags(face.GetEdges())} });
        }
        return result;
    }
    private static bool Finite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }
    private static double[] P(Point3d p) { return new double[] { p.X, p.Y, p.Z }; }
    private static double[] Add(double[] a, double[] b) { return new double[] { a[0]+b[0], a[1]+b[1], a[2]+b[2] }; }
    private static double[] Sub(double[] a, double[] b) { return new double[] { a[0]-b[0], a[1]-b[1], a[2]-b[2] }; }
    private static double[] Scale(double[] a, double s) { return new double[] { a[0]*s, a[1]*s, a[2]*s }; }
    private static double Dot(double[] a, double[] b) { return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]; }
    private static double[] Cross(double[] a, double[] b) { return new double[] { a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0] }; }
    private static double Norm(double[] a) { return Math.Sqrt(Dot(a,a)); }
    private static bool Close(double[] a, double[] b)
    { double d=Norm(Sub(a,b)); if(!Finite(d)) throw new InvalidOperationException("Non-finite comparison distance."); return d<=Tol; }
    private static void CheckPoint(double[] p)
    {
        if (p==null || p.Length!=3 || !Finite(p[0]) || !Finite(p[1]) || !Finite(p[2]))
            throw new InvalidOperationException("Sweep geometry contains an invalid point.");
    }
    private static double[] Unit(double[] a)
    {
        double n=Norm(a); if (!Finite(n) || n<=1.0e-12) throw new InvalidOperationException("Sweep tangent is invalid.");
        return Scale(a,1.0/n);
    }
    private static double[] Rotate(double[] v, double[] axis, double angle)
    {
        double c=Math.Cos(angle), s=Math.Sin(angle);
        return Add(Add(Scale(v,c),Scale(Cross(axis,v),s)),Scale(axis,Dot(axis,v)*(1-c)));
    }
    private static double[] Transport(double[] v, double[] a, double[] b)
    {
        double[] cross=Cross(a,b); double s=Norm(cross), c=Dot(a,b);
        if (!Finite(s) || !Finite(c) || c < -0.999999)
            throw new InvalidOperationException("Sweep tangent transport is ambiguous.");
        return s<1.0e-14 ? v : Rotate(v,Scale(cross,1.0/s),Math.Atan2(s,c));
    }
    private static int Vertex(List<double[]> vertices, double[] p)
    {
        CheckPoint(p); int found=-1;
        for (int i=0;i<vertices.Count;i++) if (Close(vertices[i],p))
        { if(found>=0) throw new InvalidOperationException("Ambiguous profile vertex."); found=i; }
        if(found>=0) return found; vertices.Add(p); return vertices.Count-1;
    }
    private static bool InPart(Part p, NXObject o)
    { return o!=null && !o.IsOccurrence && o.OwningPart!=null && o.OwningPart.Tag.Equals(p.Tag); }
    private static void Free(UFSession uf, IntPtr evaluator)
    { if(evaluator!=IntPtr.Zero) try { uf.Eval.Free(evaluator); } catch(Exception ex) { throw new CleanupException(ex); } }
    private static void Evaluate(UFSession uf, IntPtr evaluator, double t, out double[] point, out double[] tangent)
    {
        point=new double[3]; tangent=new double[3]; uf.Eval.Evaluate(evaluator,1,t,point,tangent);
        CheckPoint(point); CheckPoint(tangent);
    }
    private static double SegmentDistance(double[] p, double[] a, double[] b)
    {
        double[] d=Sub(b,a); double len2=Dot(d,d);
        if(!Finite(len2) || len2<=Tol*Tol) throw new InvalidOperationException("Degenerate profile edge.");
        double u=Dot(Sub(p,a),d)/len2; u=Math.Max(0,Math.Min(1,u));
        return Norm(Sub(p,Add(a,Scale(d,u))));
    }
    private static Dictionary<string,object> CheckSectionEdge(UFSession uf, Edge edge, double[] a, double[] b)
    {
        IntPtr ev=IntPtr.Zero; List<object> samples=new List<object>();
        try
        {
            uf.Eval.Initialize2(edge.Tag,out ev); if(ev==IntPtr.Zero) throw new InvalidOperationException("Missing edge evaluator.");
            double[] limits=new double[2]; uf.Eval.AskLimits(ev,limits);
            if(!Finite(limits[0]) || !Finite(limits[1]) || limits[0]>=limits[1]) throw new InvalidOperationException("Invalid edge domain.");
            for(int i=0;i<=EndEdgeSamples;i++)
            {
                double[] p,t; Evaluate(uf,ev,limits[0]+(limits[1]-limits[0])*i/EndEdgeSamples,out p,out t);
                double edgeResidual=SegmentDistance(p,a,b);
                if(!Finite(edgeResidual)) throw new InvalidOperationException("Non-finite section edge residual.");
                if(edgeResidual>SampleTol) throw Mismatch("End section edge deviates from requested segment at a sample.", "edge_tag",edge.Tag.ToString(), "sample_index",i, "actual_mm",p, "expected_segment_mm",new double[][] {a,b}, "residual_mm",edgeResidual, "limit_mm",SampleTol);
                // Check the converse direction as well: the segment is represented at each sample.
                double[] expected=Add(a,Scale(Sub(b,a),(double)i/EndEdgeSamples));
                double parameter; double[] nearest=new double[3]; uf.Eval.EvaluateClosestPoint(ev,expected,out parameter,nearest);
                CheckPoint(nearest); double segmentResidual=Norm(Sub(expected,nearest));
                if(!Finite(parameter) || !Finite(segmentResidual)) throw new InvalidOperationException("Invalid section edge closest-point result.");
                if(parameter<limits[0] || parameter>limits[1]) throw new InvalidOperationException("Section closest point is outside the edge domain.");
                if(segmentResidual>SampleTol)
                    throw Mismatch("End section segment is not covered by the actual edge at a sample.", "edge_tag",edge.Tag.ToString(), "sample_index",i, "target_mm",expected, "actual_mm",nearest, "parameter",parameter, "residual_mm",segmentResidual, "limit_mm",SampleTol);
                samples.Add(new Dictionary<string,object> { {"index",i}, {"edge_point_mm",p},
                    {"edge_to_segment_residual_mm",edgeResidual}, {"segment_point_mm",expected},
                    {"nearest_on_edge_mm",nearest}, {"segment_to_edge_residual_mm",segmentResidual} });
            }
            return new Dictionary<string,object> { {"edge_tag",edge.Tag.ToString()},
                {"endpoint_a_mm",a}, {"endpoint_b_mm",b}, {"samples",samples} };
        }
        finally { Free(uf,ev); }
    }
    private static Dictionary<string,object> CheckEdgePoint(UFSession uf, Edge edge, double[] target)
    {
        IntPtr ev=IntPtr.Zero;
        try
        {
            uf.Eval.Initialize2(edge.Tag,out ev);
            if(ev==IntPtr.Zero) throw new InvalidOperationException("Missing longitudinal edge evaluator.");
            double[] limits=new double[2]; uf.Eval.AskLimits(ev,limits);
            if(!Finite(limits[0]) || !Finite(limits[1]) || limits[0]>=limits[1]) throw new InvalidOperationException("Invalid longitudinal edge domain.");
            double parameter; double[] nearest=new double[3];
            uf.Eval.EvaluateClosestPoint(ev,target,out parameter,nearest);
            CheckPoint(nearest); double residual=Norm(Sub(target,nearest));
            if(!Finite(parameter) || !Finite(residual)) throw new InvalidOperationException("Invalid longitudinal edge closest-point result.");
            if(parameter<limits[0] || parameter>limits[1]) throw new InvalidOperationException("Longitudinal closest point is outside the edge domain.");
            if(residual>SampleTol) throw Mismatch("Longitudinal edge deviates from an internal station.", "edge_tag",edge.Tag.ToString(), "target_mm",target, "actual_mm",nearest, "parameter",parameter, "residual_mm",residual, "limit_mm",SampleTol);
            return new Dictionary<string,object> { {"edge_tag",edge.Tag.ToString()}, {"target_mm",target},
                {"actual_mm",nearest}, {"parameter",parameter}, {"residual_mm",residual} };
        }
        finally { Free(uf,ev); }
    }
    private static Dictionary<string,object> CheckFacePoint(UFSession uf, Face face, double[] target)
    {
        double[] uv=new double[2], projection=new double[3], point=new double[3];
        double[] du=new double[3],dv=new double[3],duu=new double[3],dvv=new double[3],normal=new double[3],radii=new double[2];
        uf.Modl.AskFaceParm(face.Tag,target,uv,projection);
        uf.Modl.AskFaceProps(face.Tag,uv,point,du,dv,duu,dvv,normal,radii);
        CheckPoint(projection); CheckPoint(point);
        if(!Finite(uv[0]) || !Finite(uv[1])) throw new InvalidOperationException("Invalid face parameters.");
        double residual=Norm(Sub(target,point)), projectionResidual=Norm(Sub(projection,point));
        int containment; uf.Modl.AskPointContainment(point,face.Tag,out containment);
        if(!Finite(residual) || !Finite(projectionResidual)) throw new InvalidOperationException("Invalid face projection residual.");
        if(containment<1 || containment>3) throw new InvalidOperationException("Invalid face containment result.");
        // Inconsistent projection/evaluation or an untrimmed projection outside
        // the face does not establish the distance to the trimmed face.
        if(projectionResidual>Tol || containment!=1)
            throw new InvalidOperationException("Face projection did not establish a reliable trimmed-face comparison.");
        if(residual>SampleTol)
            throw Mismatch("Expected interior station point does not match the trimmed side face.", "face_tag",face.Tag.ToString(), "target_mm",target, "projected_mm",projection, "actual_mm",point, "uv",uv, "containment",containment, "residual_mm",residual, "limit_mm",SampleTol, "projection_residual_mm",projectionResidual);
        return new Dictionary<string,object> { {"face_tag",face.Tag.ToString()}, {"target_mm",target},
            {"uv",uv}, {"projected_mm",projection}, {"evaluated_mm",point}, {"residual_mm",residual},
            {"projection_residual_mm",projectionResidual}, {"face_containment",containment} };
    }
    private static bool HasExactEdges(Face face, Edge[] wanted)
    {
        if(face==null || wanted==null) throw new InvalidOperationException("Face boundary comparison is incomplete.");
        Edge[] actual=face.GetEdges(); if(actual==null) throw new InvalidOperationException("Face boundary is unavailable.");
        foreach(Edge e in actual) if(e==null) throw new InvalidOperationException("Face boundary contains a missing edge.");
        foreach(Edge e in wanted) if(e==null) throw new InvalidOperationException("Expected face boundary is incomplete.");
        if(actual.Length!=wanted.Length) return false;
        HashSet<Tag> set=new HashSet<Tag>(); foreach(Edge e in actual) if(!set.Add(e.Tag)) return false;
        foreach(Edge e in wanted) if(!set.Remove(e.Tag)) return false;
        return set.Count==0;
    }
    private static Face UniqueFace(Face[] faces, Edge[] wanted)
    {
        Face found=null;
        foreach(Face face in faces) if(HasExactEdges(face,wanted))
        { if(found!=null) throw Mismatch("Ambiguous face boundary.", "expected_edge_tags",EdgeTags(wanted), "actual_face_boundaries",FaceBoundaries(faces)); found=face; }
        if(found==null) throw Mismatch("Missing face with the complete expected boundary.", "expected_edge_tags",EdgeTags(wanted), "actual_face_boundaries",FaceBoundaries(faces));
        return found;
    }
    private static void CheckCap(UFSession uf, Face face, double[] point, double[] tangent)
    {
        int type,sense; double[] origin=new double[3],direction=new double[3],box=new double[6]; double radius,data;
        uf.Modl.AskFaceData(face.Tag,out type,origin,direction,box,out radius,out data,out sense);
        CheckPoint(origin); CheckPoint(direction); double[] normal=Unit(direction);
        if(type<=0) throw new InvalidOperationException("Invalid native cap face type.");
        double normalError=Math.Abs(Math.Abs(Dot(normal,tangent))-1);
        double planeError=Math.Abs(Dot(Sub(point,origin),normal));
        if(!Finite(normalError) || !Finite(planeError)) throw new InvalidOperationException("Non-finite cap plane comparison.");
        // A non-analytic native face may still be geometrically planar.
        if(type!=UFConstants.UF_MODL_PLANAR_FACE) throw new InvalidOperationException("Cap has no proven analytic plane.");
        if(normalError>DirectionTol || planeError>Tol)
            throw Mismatch("Cap is not on the expected section plane.", "face_tag",face.Tag.ToString(),
                "actual_origin_mm",origin, "actual_normal",normal, "expected_point_mm",point, "expected_normal",tangent,
                "normal_residual",normalError, "normal_limit",DirectionTol, "plane_residual_mm",planeError, "plane_limit_mm",Tol);
    }

    private static bool InsidePolygon(double[] point, double[][] vertices, List<int[]> segments, double[] u, double[] v)
    {
        double x=Dot(point,u),y=Dot(point,v); bool inside=false;
        foreach(int[] segment in segments)
        {
            double[] a=vertices[segment[0]],b=vertices[segment[1]];
            if(SegmentDistance(point,a,b)<=10*Tol) return false;
            double ax=Dot(a,u), ay=Dot(a,v), bx=Dot(b,u), by=Dot(b,v);
            if((ay>y)!=(by>y) && x < ax+(y-ay)*(bx-ax)/(by-ay)) inside=!inside;
        }
        return inside;
    }
    private static Dictionary<string,object> CapWitness(UFSession uf, Face face, double[][] vertices,
        List<int[]> segments, double[] tangent)
    {
        int[] first=segments[0]; double[] u=Unit(Sub(vertices[first[1]],vertices[first[0]]));
        double[] v=Unit(Cross(tangent,u)); double[] fractions=new double[] {0.1,0.01,0.001};
        foreach(int[] segment in segments)
        {
            double[] a=vertices[segment[0]],b=vertices[segment[1]],d=Sub(b,a);
            double length=Norm(d); double[] sideways=Unit(Cross(tangent,d));
            double[] middle=Scale(Add(a,b),0.5);
            foreach(double fraction in fractions) for(int sign=-1;sign<=1;sign+=2)
            {
                double[] candidate=Add(middle,Scale(sideways,sign*length*fraction));
                if(!InsidePolygon(candidate,vertices,segments,u,v)) continue;
                int containment; uf.Modl.AskPointContainment(candidate,face.Tag,out containment);
                if(containment<1 || containment>3) throw new InvalidOperationException("Invalid cap containment result.");
                if(containment!=1) throw Mismatch("Cap does not contain an interior polygon witness.", "face_tag",face.Tag.ToString(), "target_mm",candidate, "actual_containment",containment, "expected_containment",1);
                return new Dictionary<string,object> { {"face_tag",face.Tag.ToString()},
                    {"target_mm",candidate}, {"face_containment",containment} };
            }
        }
        throw new InvalidOperationException("A robust interior cap witness could not be established.");
    }

    // Source sketch must already have passed independent Section/domain checks.
    // The path must start at one unique profile vertex and be tangent-continuous.
    // The returned status covers finite samples and topology only.
    public static Dictionary<string,object> Verify(Part part, Sketch sketch, Curve[] path, double[][] directedPointsMm,
        string orientationMode, double twistAngleDeg, string profileOutput, Body body)
    {
        if(part==null || part.PartUnits!=BasePart.Units.Millimeters || !InPart(part,sketch) || !InPart(part,body))
            throw new InvalidOperationException("Sweep geometry requires a same-part sketch and body in millimetres.");
        if(profileOutput!="solid" && profileOutput!="sheet") throw new InvalidOperationException("Unsupported sweep body type request.");
        bool actualSolid=body.IsSolidBody, actualSheet=body.IsSheetBody;
        if(actualSolid==actualSheet) throw new InvalidOperationException("Inconsistent or unsupported native body type.");
        if(actualSolid!=(profileOutput=="solid") || (profileOutput=="sheet" && !actualSheet))
            throw Mismatch("Unexpected sweep body type.", "body_tag",body.Tag.ToString(), "actual_solid",actualSolid,
                "actual_sheet",actualSheet, "expected_type",profileOutput);
        if((orientationMode!="transport" && orientationMode!="linear_twist") || !Finite(twistAngleDeg) ||
            Math.Abs(twistAngleDeg)>90 || (orientationMode=="transport" && twistAngleDeg!=0))
            throw new InvalidOperationException("Unsupported sweep orientation.");
        if(path==null || path.Length==0 || directedPointsMm==null || directedPointsMm.Length!=path.Length+1)
            throw new InvalidOperationException("Invalid directed path.");
        foreach(double[] point in directedPointsMm) CheckPoint(point);
        UFSession uf=UFSession.GetUFSession();
        List<double[]> vertices=new List<double[]>(); List<int[]> segments=new List<int[]>();
        NXObject[] geometry=sketch.GetAllGeometry(); if(geometry==null) throw new InvalidOperationException("Missing profile geometry.");
        foreach(NXObject item in geometry)
        {
            Curve curve=item as Curve; if(curve==null) throw new InvalidOperationException("Unsupported sketch geometry.");
            UFSket.ReferenceStatus status; uf.Sket.AskReferenceStatus(sketch.Tag,curve.Tag,out status);
            if(status==UFSket.ReferenceStatus.Reference) continue;
            Line line=curve as Line;
            if(status!=UFSket.ReferenceStatus.Active || line==null || !InPart(part,line))
                throw new InvalidOperationException("Only active Line profiles are supported.");
            int a=Vertex(vertices,P(line.StartPoint)),b=Vertex(vertices,P(line.EndPoint));
            if(a==b) throw new InvalidOperationException("Degenerate profile line.");
            foreach(int[] s in segments) if((s[0]==a && s[1]==b)||(s[0]==b && s[1]==a))
                throw new InvalidOperationException("Duplicate profile segment.");
            segments.Add(new int[] {a,b});
        }
        int n=vertices.Count, m=segments.Count;
        if(m==0 || n>256 || (profileOutput=="solid" && (n<3 || m!=n)) || (profileOutput=="sheet" && m!=n-1 && !(n>=3 && m==n)))
            throw new InvalidOperationException("Profile must be one closed polygon for solid or one open/closed chain for sheet.");
        int[] degree=new int[n]; foreach(int[] s in segments) { degree[s[0]]++; degree[s[1]]++; }
        int endpoints=0; foreach(int d in degree) { if(d==1) endpoints++; else if(d!=2) throw new InvalidOperationException("Profile branches or is disconnected."); }
        if(endpoints!=(m==n?0:2)) throw new InvalidOperationException("Invalid profile connectivity.");
        bool[] reached=new bool[n]; reached[0]=true;
        for(int pass=0;pass<n;pass++) foreach(int[] s in segments) if(reached[s[0]] || reached[s[1]]) { reached[s[0]]=true; reached[s[1]]=true; }
        foreach(bool ok in reached) if(!ok) throw new InvalidOperationException("Disconnected profile.");
        int anchor=-1; for(int i=0;i<n;i++) if(Close(vertices[i],directedPointsMm[0])) { if(anchor>=0) throw new InvalidOperationException("Ambiguous guide anchor."); anchor=i; }
        if(anchor<0) throw new InvalidOperationException("Path must start at a profile vertex.");
        double[][] offsets=new double[n][]; for(int i=0;i<n;i++) offsets[i]=Sub(vertices[i],directedPointsMm[0]);
        double[] firstTangent=null,previousTangent=null; List<Station> stations=new List<Station>();
        double arcLength=0;
        HashSet<Tag> pathTags=new HashSet<Tag>();
        for(int k=0;k<path.Length;k++)
        {
            Curve curve=path[k]; if(!InPart(part,curve) || (!(curve is Line) && !(curve is Arc)) || !pathTags.Add(curve.Tag))
                throw new InvalidOperationException("Path contains an invalid or repeated curve.");
            IntPtr ev=IntPtr.Zero;
            try
            {
                uf.Eval.Initialize2(curve.Tag,out ev); if(ev==IntPtr.Zero) throw new InvalidOperationException("Missing path evaluator.");
                double[] lim=new double[2]; uf.Eval.AskLimits(ev,lim);
                if(!Finite(lim[0]) || !Finite(lim[1]) || lim[0]>=lim[1]) throw new InvalidOperationException("Invalid path domain.");
                double[] p0,d0,p1,d1; Evaluate(uf,ev,lim[0],out p0,out d0); Evaluate(uf,ev,lim[1],out p1,out d1);
                bool forward=Close(p0,directedPointsMm[k]) && Close(p1,directedPointsMm[k+1]);
                bool reverse=Close(p1,directedPointsMm[k]) && Close(p0,directedPointsMm[k+1]);
                if(forward==reverse) throw new InvalidOperationException("Path endpoints do not establish a unique direction.");
                double[] previousPoint=null; double segmentLength=0;
                for(int j=0;j<=PathSamples;j++)
                {
                    double f=(double)j/PathSamples, t=forward ? lim[0]+f*(lim[1]-lim[0]) : lim[1]-f*(lim[1]-lim[0]);
                    double[] p,d; Evaluate(uf,ev,t,out p,out d); double[] tangent=Unit(Scale(d,forward ? 1 : -1));
                    if(firstTangent==null) firstTangent=tangent;
                    if(previousTangent!=null)
                    {
                        if(j==0 && Norm(Sub(previousTangent,tangent))>DirectionTol)
                            throw new InvalidOperationException("Path segments are not tangent-continuous.");
                        if(j>0) for(int i=0;i<n;i++) offsets[i]=Transport(offsets[i],previousTangent,tangent);
                    }
                    if(j>0)
                    {
                        double chord=Norm(Sub(p,previousPoint));
                        double turn=Math.Atan2(Norm(Cross(previousTangent,tangent)),Dot(previousTangent,tangent));
                        if(!Finite(chord) || !Finite(turn) || chord<=0 || turn>=Math.PI/2)
                            throw new InvalidOperationException("Path station spacing is degenerate or ambiguous.");
                        double increment=curve is Arc && turn>1.0e-10 ? chord*turn/(2*Math.Sin(turn/2)) : chord;
                        if(!Finite(increment) || increment<=0) throw new InvalidOperationException("Invalid path arc length.");
                        segmentLength+=increment; arcLength+=increment;
                    }
                    previousTangent=tangent; previousPoint=p;
                    // Sample both sides of every segment junction and three more
                    // internal locations. Arc length is measured geometrically.
                    if(j==1 || j==8 || j==16 || j==24 || j==31)
                    {
                        double[][] section=new double[n][];
                        for(int i=0;i<n;i++) section[i]=Add(p,offsets[i]);
                        stations.Add(new Station { Segment=k, Step=j, ArcLengthMm=arcLength, PathPoint=p, Tangent=tangent, Points=section });
                    }
                }
                double nativeLength=curve.GetLength();
                if(!Finite(nativeLength) || Math.Abs(segmentLength-nativeLength)>Math.Max(Tol,nativeLength*1.0e-8))
                    throw new InvalidOperationException("Path arc length could not be established from native curve samples.");
            }
            finally { Free(uf,ev); }
        }
        for(int i=0;i<n;i++) if(Math.Abs(Dot(Sub(vertices[i],directedPointsMm[0]),firstTangent))>Tol)
            throw new InvalidOperationException("Profile is not normal to the starting path tangent.");
        if(!Finite(arcLength) || arcLength<=0) throw new InvalidOperationException("Invalid total path arc length.");
        // This is an explicitly challenged model of NX angular-law behavior:
        // public right-hand angle proportional to accumulated path arc length.
        // Actual native geometry must independently satisfy every station test.
        foreach(Station station in stations)
        {
            station.AngleDeg=twistAngleDeg*station.ArcLengthMm/arcLength;
            double stationAngle=station.AngleDeg*Math.PI/180.0;
            for(int i=0;i<n;i++) station.Points[i]=Add(station.PathPoint,
                Rotate(Sub(station.Points[i],station.PathPoint),station.Tangent,stationAngle));
        }
        double angle=twistAngleDeg*Math.PI/180.0; double[][] end=new double[n][];
        for(int i=0;i<n;i++) end[i]=Add(directedPointsMm[path.Length],Rotate(offsets[i],previousTangent,angle));
        // Each vertex must identify one endpoint unambiguously, including both sections.
        List<double[]> all=new List<double[]>();
        foreach(double[] p in vertices) { int before=all.Count; int index=Vertex(all,p); if(index!=before) throw new InvalidOperationException("Duplicate section vertex."); }
        foreach(double[] p in end) { int before=all.Count; int index=Vertex(all,p); if(index!=before) throw new InvalidOperationException("End sections overlap or are ambiguous."); }
        Edge[] edges=body.GetEdges(); Face[] faces=body.GetFaces();
        if(edges==null || faces==null) throw new InvalidOperationException("Sweep body topology is unavailable.");
        EdgeTags(edges); FaceTags(faces); // Require complete native membership before classifying counts.
        if(edges.Length!=2*m+n || faces.Length!=(profileOutput=="solid"?m+2:m))
            throw Mismatch("Unexpected sweep topology counts.", "actual_edge_tags",EdgeTags(edges), "actual_face_tags",FaceTags(faces), "actual_edge_count",edges.Length, "expected_edge_count",2*m+n, "actual_face_count",faces.Length, "expected_face_count",profileOutput=="solid"?m+2:m);
        bool[] startSeen=new bool[m],endSeen=new bool[m],longSeen=new bool[n];
        Edge[] startEdges=new Edge[m],endEdges=new Edge[m],longEdges=new Edge[n];
        List<object> endpointEvidence=new List<object>(),sectionEvidence=new List<object>();
        foreach(Edge edge in edges)
        {
            Point3d ea,eb; edge.GetVertices(out ea,out eb); double[] a=P(ea),b=P(eb); CheckPoint(a); CheckPoint(b);
            int ia=-1,ib=-1; for(int i=0;i<all.Count;i++) { if(Close(a,all[i])) { if(ia>=0) throw new InvalidOperationException("Ambiguous body vertex."); ia=i; } if(Close(b,all[i])) { if(ib>=0) throw new InvalidOperationException("Ambiguous body vertex."); ib=i; } }
            if(ia<0 || ib<0 || ia==ib) throw Mismatch("Unexpected body edge endpoint.", "edge_tag",edge.Tag.ToString(), "actual_a_mm",a, "actual_b_mm",b, "expected_vertices_mm",all, "matched_index_a",ia, "matched_index_b",ib);
            endpointEvidence.Add(new Dictionary<string,object> { {"edge_tag",edge.Tag.ToString()},
                {"actual_a_mm",a}, {"expected_a_mm",all[ia]}, {"a_residual_mm",Norm(Sub(a,all[ia]))},
                {"actual_b_mm",b}, {"expected_b_mm",all[ib]}, {"b_residual_mm",Norm(Sub(b,all[ib]))} });
            Face[] adjacent=edge.GetFaces(); if(adjacent==null) throw new InvalidOperationException("Missing edge adjacency.");
            List<string> adjacentTags=FaceTags(adjacent);
            if((ia<n)==(ib<n))
            {
                bool atEnd=ia>=n; int x=ia%n,y=ib%n,match=-1;
                for(int i=0;i<m;i++) if((segments[i][0]==x && segments[i][1]==y)||(segments[i][0]==y && segments[i][1]==x)) { match=i; break; }
                bool[] seen=atEnd?endSeen:startSeen;
                if(match<0 || seen[match] || adjacent.Length!=(profileOutput=="solid"?2:1)) throw Mismatch("Unexpected end section topology.", "edge_tag",edge.Tag.ToString(), "actual_endpoint_indices",new int[]{ia,ib}, "expected_profile_segments",segments, "matching_segment_index",match, "previously_seen",match>=0 && seen[match], "actual_adjacent_face_tags",adjacentTags, "expected_adjacent_count",profileOutput=="solid"?2:1);
                seen[match]=true; if(atEnd) endEdges[match]=edge; else startEdges[match]=edge;
                Dictionary<string,object> sectionRecord=CheckSectionEdge(uf,edge,all[ia],all[ib]);
                sectionRecord["profile_edge_index"]=match; sectionRecord["section"]=atEnd?"end":"start";
                sectionEvidence.Add(sectionRecord);
            }
            else
            {
                int x=ia%n,y=ib%n;
                if(x!=y || longSeen[x] || adjacent.Length!=degree[x]) throw Mismatch("Unexpected longitudinal edge topology.", "edge_tag",edge.Tag.ToString(), "actual_endpoint_indices",new int[]{ia,ib}, "actual_adjacent_face_tags",adjacentTags, "expected_adjacent_count",degree[x], "previously_seen",longSeen[x]);
                longSeen[x]=true; longEdges[x]=edge;
            }
        }
        foreach(bool ok in startSeen) if(!ok) throw Mismatch("Missing start section edge.", "expected_segments",segments, "seen",startSeen, "actual_edge_tags",EdgeTags(edges));
        foreach(bool ok in endSeen) if(!ok) throw Mismatch("Missing end section edge.", "expected_segments",segments, "seen",endSeen, "actual_edge_tags",EdgeTags(edges));
        foreach(bool ok in longSeen) if(!ok) throw Mismatch("Missing longitudinal edge.", "expected_vertex_count",n, "seen",longSeen, "actual_edge_tags",EdgeTags(edges));
        Face[] sideFaces=new Face[m]; HashSet<Tag> usedFaces=new HashSet<Tag>();
        for(int i=0;i<m;i++)
        {
            int[] s=segments[i];
            sideFaces[i]=UniqueFace(faces,new Edge[] {startEdges[i],endEdges[i],longEdges[s[0]],longEdges[s[1]]});
            if(!usedFaces.Add(sideFaces[i].Tag)) throw Mismatch("A side face was assigned twice.", "actual_face_tag",sideFaces[i].Tag.ToString(), "profile_segment_index",i, "expected_unique_face_count",m);
        }
        List<object> capEvidence=new List<object>();
        if(profileOutput=="solid")
        {
            Face startCap=UniqueFace(faces,startEdges),endCap=UniqueFace(faces,endEdges);
            if(!usedFaces.Add(startCap.Tag) || !usedFaces.Add(endCap.Tag)) throw Mismatch("Cap face assignment is ambiguous.", "start_cap_face_tag",startCap.Tag.ToString(), "end_cap_face_tag",endCap.Tag.ToString(), "previously_assigned_face_tags",new List<string>(Array.ConvertAll(new List<Tag>(usedFaces).ToArray(),delegate(Tag tag){ return tag.ToString(); })));
            CheckCap(uf,startCap,directedPointsMm[0],firstTangent);
            CheckCap(uf,endCap,directedPointsMm[path.Length],previousTangent);
            capEvidence.Add(CapWitness(uf,startCap,vertices.ToArray(),segments,firstTangent));
            capEvidence.Add(CapWitness(uf,endCap,end,segments,previousTangent));
        }
        if(usedFaces.Count!=faces.Length) throw Mismatch("Unexpected unassigned sweep face.", "actual_face_tags",FaceTags(faces), "assigned_face_count",usedFaces.Count, "expected_face_count",faces.Length);
        List<object> keypointEvidence=new List<object>();
        for(int i=0;i<directedPointsMm.Length;i++)
        {
            Dictionary<string,object> record=CheckEdgePoint(uf,longEdges[anchor],directedPointsMm[i]);
            record["path_point_index"]=i; keypointEvidence.Add(record);
        }
        List<object> stationEvidence=new List<object>();
        foreach(Station station in stations)
        {
            List<object> vertexEvidence=new List<object>(), faceEvidence=new List<object>();
            for(int i=0;i<n;i++)
            {
                Dictionary<string,object> record=CheckEdgePoint(uf,longEdges[i],station.Points[i]);
                record["vertex_index"]=i; vertexEvidence.Add(record);
            }
            for(int i=0;i<m;i++)
                for(int sample=1;sample<=3;sample++)
                {
                    int[] s=segments[i]; double fraction=sample/4.0;
                    double[] target=Add(station.Points[s[0]],Scale(Sub(station.Points[s[1]],station.Points[s[0]]),fraction));
                    Dictionary<string,object> record=CheckFacePoint(uf,sideFaces[i],target);
                    record["profile_edge_index"]=i; record["profile_edge_fraction"]=fraction; faceEvidence.Add(record);
                }
            stationEvidence.Add(new Dictionary<string,object> { {"segment_index",station.Segment},
                {"step",station.Step}, {"arc_length_mm",station.ArcLengthMm},
                {"angle_deg_right_hand",station.AngleDeg}, {"path_point_mm",station.PathPoint},
                {"path_tangent",station.Tangent}, {"vertices",vertexEvidence}, {"side_face_samples",faceEvidence} });
        }
        int faults; int[] faultTypes; Tag[] faultTags; uf.Modl.AskBodyConsistency(body.Tag,out faults,out faultTypes,out faultTags);
        if(faults<0) throw new InvalidOperationException("Invalid body consistency fault count.");
        if(faults>0)
        {
            if(faultTypes==null || faultTags==null || faultTypes.Length<faults || faultTags.Length<faults)
                throw new InvalidOperationException("Body consistency fault details are incomplete.");
            string[] faultTagStrings=new string[faultTags.Length];
            for(int i=0;i<faultTags.Length;i++) faultTagStrings[i]=faultTags[i].ToString();
            throw Mismatch("Sweep body consistency check failed.", "body_tag",body.Tag.ToString(), "actual_fault_count",faults,
                "expected_fault_count",0, "fault_types",faultTypes, "fault_tags",faultTagStrings);
        }
        return new Dictionary<string,object>(StringComparer.Ordinal) {
            {"status","SAMPLED_GEOMETRY_AND_TOPOLOGY_MATCH"}, {"body_tag",body.Tag.ToString()},
            {"profile_vertex_count",n}, {"profile_edge_count",m}, {"guide_anchor_vertex",anchor},
            {"path_segment_count",path.Length}, {"path_samples_per_segment",PathSamples+1},
            {"section_edge_samples",EndEdgeSamples+1}, {"endpoint_tolerance_mm",Tol},
            {"interior_and_section_sample_tolerance_mm",SampleTol}, {"endpoint_residuals",endpointEvidence},
            {"section_edge_residuals",sectionEvidence}, {"cap_witnesses",capEvidence},
            {"path_keypoints",keypointEvidence}, {"stations",stationEvidence},
            {"station_steps_per_segment",new int[] {1,8,16,24,31}}, {"total_path_arc_length_mm",arcLength},
            {"tangent_tolerance",DirectionTol}, {"twist_degrees_right_hand_about_forward_tangent",twistAngleDeg},
            {"orientation_model","parallel transport plus right-hand angle proportional to accumulated path arc length; native angular-law progression is an unproven model"},
            {"scope","Endpoint connectivity, planar cap boundaries, and finite internal samples only; complete face/edge geometry is not proven."}
        };
    }
}
