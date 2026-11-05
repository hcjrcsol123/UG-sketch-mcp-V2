using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

/// <summary>Conservative analytic B-rep coverage check. Unsupported or ambiguous topology is rejected.</summary>
public static class V2HoleCoverage
{
    public sealed class CleanupException : Exception { public CleanupException(string message, Exception inner) : base(message, inner) {} }
    private const double Distance = 0.002;
    private const double Angular = 1e-7;
    private const double AreaRelative = 2e-6;
    private const double AreaAbsolute = 1e-5;
    private sealed class InvalidCoverage : Exception { public InvalidCoverage(string s) : base(s) {} }
    private sealed class Ring { public Edge Edge; public double Z; public double Radius; public double[] Center; }
    private sealed class Surface { public Face Face; public int Type; public double[] Origin; public double[] Axis; public double Radius; public double HalfAngle; public int Sense; public double Area; public double ExpectedArea; public double AreaTolerance; public List<Ring> Rings = new List<Ring>(); }
    private sealed class Context { public Part Part; public Body Body; public UFSession Uf; public double[] P; public double[] D; public Dictionary<string,object> Evidence; }

    public static bool Verify(Part part, Body body, HolePackage feature, M3HoleRequest request, double tapDrillDiameterMm, out Dictionary<string,object> evidence)
    {
        evidence = new Dictionary<string,object>(StringComparer.Ordinal);
        evidence["status"] = "REJECTED"; evidence["distance_tolerance_mm"] = Distance; evidence["angular_tolerance"] = Angular;
        evidence["area_relative_tolerance"] = AreaRelative; evidence["area_absolute_tolerance_mm2"] = AreaAbsolute;
        evidence["scope"] = "single analytic face per surface, full circular edges and area, exact adjacency; split faces rejected";
        try
        {
            if (part == null || body == null || feature == null || request == null || body.OwningPart == null || feature.OwningPart == null || !body.OwningPart.Tag.Equals(part.Tag) || !feature.OwningPart.Tag.Equals(part.Tag) || !body.IsSolidBody || part.PartUnits != BasePart.Units.Millimeters) Fail("same-part millimetre solid and HolePackage required");
            if (request.PositionMm == null || request.PositionMm.Length != 3 || request.Direction == null || request.Direction.Length != 3) Fail("missing world axis");
            double[] p = (double[])request.PositionMm.Clone(), d = (double[])request.Direction.Clone();
            for (int i=0;i<3;i++) if (!Finite(p[i]) || !Finite(d[i])) Fail("nonfinite axis");
            if (Math.Abs(Norm(d)-1.0)>Angular) Fail("axis is not a unit vector");
            if (request.Kind != "simple" && request.Kind != "counterbore" && request.Kind != "countersink" && request.Kind != "threaded") Fail("unsupported kind");
            if (request.DepthMode != "blind" && request.DepthMode != "through") Fail("unsupported depth mode");
            double radius = (request.Kind == "threaded" ? tapDrillDiameterMm : request.DiameterMm) / 2;
            if (!Finite(radius) || radius < 0.05) Fail("radius below supported geometric tolerance scale");
            if (request.DepthMode == "blind" && (!Finite(request.HoleDepthMm) || request.HoleDepthMm < 0.1)) Fail("blind depth below supported geometric tolerance scale");
            Context c = new Context { Part=part, Body=body, Uf=UFSession.GetUFSession(), P=p, D=d, Evidence=evidence };
            int faults; int[] faultTypes; Tag[] faultTags; c.Uf.Modl.AskBodyConsistency(body.Tag, out faults, out faultTypes, out faultTags);
            evidence["body_consistency_fault_count"] = faults; if (faults != 0) Fail("body consistency faults");
            Body[] fb = feature.GetBodies(); if (fb.Length != 1 || !fb[0].Tag.Equals(body.Tag)) Fail("HolePackage body mismatch");
            evidence["body_tag"] = body.Tag.ToString(); evidence["feature_tag"] = feature.Tag.ToString(); evidence["axis_origin_mm"] = p; evidence["axis_direction"] = d;
            double wallStart = 0, largeRadius = 0;
            if (request.Kind == "counterbore") { largeRadius=request.CounterboreDiameterMm/2; wallStart=request.CounterboreDepthMm; if (!Finite(largeRadius) || !Finite(wallStart) || largeRadius <= radius + Distance || wallStart < 0.1) Fail("invalid counterbore geometry"); }
            if (request.Kind == "countersink") { largeRadius=request.CountersinkDiameterMm/2; double a=request.CountersinkAngleDeg; if (!Finite(largeRadius) || !Finite(a) || largeRadius<=radius+Distance || a<=0 || a>=180) Fail("invalid countersink geometry"); wallStart=(largeRadius-radius)/Math.Tan(a*Math.PI/360.0); if (!Finite(wallStart) || wallStart<0.1) Fail("countersink height below supported scale"); }
            List<Surface> walls = Find(c, UFConstants.UF_MODL_CYLINDRICAL_FACE, radius);
            evidence["coaxial_same_radius_face_count"] = walls.Count;
            if (walls.Count != 1) Fail("expected exactly one coaxial same-radius cylinder in the whole body; split or additional walls are unsupported");
            List<Surface> qualified = new List<Surface>();
            foreach (Surface s in walls) if (s.Rings.Count == 2 && Near(s.Rings[0].Z, wallStart) && s.Rings[1].Z > wallStart+0.1 && (request.DepthMode == "through" || Near(s.Rings[1].Z, request.HoleDepthMm))) qualified.Add(s);
            if (qualified.Count != 1) Fail("expected exactly one complete analytic main cylinder with the requested axial interval");
            Surface wall = qualified[0];
            CheckArea(wall, 2*Math.PI*radius*(wall.Rings[1].Z-wall.Rings[0].Z));
            CheckVoidSide(c, radius, (wall.Rings[0].Z+wall.Rings[1].Z)/2);
            evidence["main_wall"] = Describe(wall); evidence["complete_wall_length_mm"] = wall.Rings[1].Z-wall.Rings[0].Z;
            if (request.Kind == "threaded" && (!Finite(request.ThreadDepthMm) || request.ThreadDepthMm <= 0 || request.ThreadDepthMm > wall.Rings[1].Z+Distance || wall.Rings[0].Z > Distance)) Fail("thread length exceeds the complete axial cylinder");
            if (request.Kind == "simple" || request.Kind == "threaded") CheckPlanarBoundary(c, wall.Rings[0], wall.Face, 0, "entry");
            else if (request.Kind == "counterbore")
            {
                Surface upper = UniqueMatchingCylinder(c, largeRadius, 0, wallStart);
                CheckArea(upper, 2*Math.PI*largeRadius*wallStart); CheckVoidSide(c,largeRadius,wallStart/2);
                Face shoulder = SharedOtherFace(wall.Rings[0], wall.Face);
                if (!HasEdge(shoulder, upper.Rings[1].Edge)) Fail("counterbore shoulder lacks shared full rings");
                CheckAnnulus(c, shoulder, radius, largeRadius, wallStart);
                CheckPlanarBoundary(c, upper.Rings[0], upper.Face, 0, "entry");
                evidence["counterbore_wall"] = Describe(upper); evidence["shoulder_face_tag"] = shoulder.Tag.ToString();
            }
            else
            {
                Face coneFace = SharedOtherFace(wall.Rings[0], wall.Face);
                Surface cone = Analyze(c, coneFace);
                if (cone.Type != UFConstants.UF_MODL_CONICAL_FACE || cone.Rings.Count != 2 || !Near(cone.Rings[0].Z,0) || !Near(cone.Rings[0].Radius,largeRadius) || !Near(cone.Rings[1].Z,wallStart) || !Near(cone.Rings[1].Radius,radius)) Fail("countersink conical ring geometry mismatch");
                if ((!Finite(cone.HalfAngle) || Math.Abs(Math.Abs(cone.HalfAngle)-request.CountersinkAngleDeg*Math.PI/360.0)>Angular)) Fail("countersink cone angle mismatch");
                CheckArea(cone,Math.PI*(radius+largeRadius)*Math.Sqrt(wallStart*wallStart+(largeRadius-radius)*(largeRadius-radius)));
                CheckPlanarBoundary(c,cone.Rings[0],cone.Face,0,"entry"); evidence["countersink_cone"] = Describe(cone);
            }
            if (request.DepthMode == "through")
            {
                CheckPlanarBoundary(c, wall.Rings[1], wall.Face, wall.Rings[1].Z, "exit");
                evidence["exit_axis_distance_mm"] = wall.Rings[1].Z;
                double[] rayOrigin = new double[] { p[0] + d[0]*(wall.Rings[1].Z+0.01), p[1] + d[1]*(wall.Rings[1].Z+0.01), p[2] + d[2]*(wall.Rings[1].Z+0.01) };
                evidence["exit_ray_origin_mm"] = rayOrigin;
                evidence["exit_ray_direction"] = d;
                evidence["exit_ray_offset_mm"] = 0.01;
                int outsideStatus; c.Uf.Modl.AskPointContainment(rayOrigin, body.Tag, out outsideStatus);
                evidence["exit_ray_origin_body_containment"] = outsideStatus;
                if (outsideStatus != 2) Fail("point beyond the exit ring is not outside the target body");
                double[] identity = new double[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
                int hitCount; UFModl.RayHitPointInfo[] hits;
                c.Uf.Modl.TraceARay(1, new Tag[] { body.Tag }, rayOrigin, d, identity, 0, out hitCount, out hits);
                evidence["exit_ray_hit_count"] = hitCount;
                if (hitCount != 0) Fail("target body has further ray intersections beyond the proposed through exit");
                CheckNoBodyBeyondExitPlane(c, wall.Rings[1].Z);
            }
            else
            {
                Face tipFace = SharedOtherFace(wall.Rings[1],wall.Face); Surface tip=Analyze(c,tipFace);
                double angle=59*Math.PI/180.0, height=radius/Math.Tan(angle);
                if (tip.Type != UFConstants.UF_MODL_CONICAL_FACE || tip.Rings.Count != 1 || !Near(tip.Rings[0].Z, request.HoleDepthMm) || !Near(tip.Rings[0].Radius,radius) || (!Finite(tip.HalfAngle) || Math.Abs(Math.Abs(tip.HalfAngle)-angle)>Angular)) Fail("blind drill tip is not an exact 118-degree full cone");
                CheckArea(tip,Math.PI*radius*Math.Sqrt(radius*radius+height*height));
                // The interior point selects the inward cone nappe; area and boundary, not this sample, establish full coverage.
                double[] e = Perpendicular(d), sample=At(c,request.HoleDepthMm+height/2,e,radius/2); int status; c.Uf.Modl.AskPointContainment(sample,tip.Face.Tag,out status);
                if (status != 1) Fail("blind tip points away from the requested inward axis or is trimmed");
                evidence["drill_tip"] = Describe(tip); evidence["drill_tip_extra_depth_mm"] = height; evidence["tip_nappe_point_containment"] = status;
            }
            evidence["status"]="VERIFIED"; return true;
        }
        catch (CleanupException) { throw; }
        catch (Exception ex) { evidence["reason"] = ex.Message; evidence["exception_type"] = ex.GetType().FullName; return false; }
    }
    private static List<Surface> Find(Context c,int type,double radius) { List<Surface> r=new List<Surface>(); foreach(Face f in c.Body.GetFaces()) { int t,n; double rr,aa; double[] p=new double[3],d=new double[3],box=new double[6]; c.Uf.Modl.AskFaceData(f.Tag,out t,p,d,box,out rr,out aa,out n); if(t!=type || !Near(rr,radius) || !Coaxial(c,p,d)) continue; r.Add(Analyze(c,f)); } return r; }
    private static Surface UniqueMatchingCylinder(Context c,double radius,double start,double end) { List<Surface> matches=Find(c,UFConstants.UF_MODL_CYLINDRICAL_FACE,radius); if(matches.Count!=1 || matches[0].Rings.Count!=2 || !Near(matches[0].Rings[0].Z,start) || !Near(matches[0].Rings[1].Z,end)) Fail("expected one complete counterbore cylinder"); return matches[0]; }
    private static Surface Analyze(Context c, Face f)
    {
        Surface s=new Surface { Face=f, Origin=new double[3], Axis=new double[3] }; double[] box=new double[6];
        c.Uf.Modl.AskFaceData(f.Tag,out s.Type,s.Origin,s.Axis,box,out s.Radius,out s.HalfAngle,out s.Sense);
        if ((s.Type!=UFConstants.UF_MODL_CYLINDRICAL_FACE && s.Type!=UFConstants.UF_MODL_CONICAL_FACE) || !Coaxial(c,s.Origin,s.Axis)) Fail("unsupported or noncoaxial analytic face");
        int lines=0; Edge[] edges=f.GetEdges(); if(edges==null || edges.Length==0) Fail("face lacks boundary");
        foreach(Edge edge in edges)
        {
            IntPtr ev=IntPtr.Zero;
            try {
                c.Uf.Eval.Initialize2(edge.Tag,out ev); if(ev==IntPtr.Zero) Fail("edge evaluator unavailable"); bool arc; c.Uf.Eval.IsArc(ev,out arc);
                if(arc) { UFEval.Arc a; c.Uf.Eval.AskArc(ev,out a); double length=edge.GetLength(); if (!Finite(a.radius) || !Finite(length) || a.radius<=Distance || Math.Abs(length-2*Math.PI*a.radius)>Math.Max(Distance,2*Math.PI*a.radius*AreaRelative)) Fail("boundary arc is not a full circle"); double[] normal=Cross(a.x_axis,a.y_axis); if(!Finite(Norm(normal)) || Norm(normal)<0.9 || !Finite(Dot(normal,c.D)) || Math.Abs(Dot(normal,c.D))/Norm(normal)<1-Angular) Fail("circle plane not normal to axis"); double z=Dot(Sub(a.center,c.P),c.D); if(!Finite(z) || !Finite(Norm(Sub(Sub(a.center,c.P),Scale(c.D,z)))) || Norm(Sub(Sub(a.center,c.P),Scale(c.D,z)))>Distance) Fail("circle centre is not on axis"); s.Rings.Add(new Ring{Edge=edge,Z=z,Radius=a.radius,Center=a.center}); }
                else { bool line; c.Uf.Eval.IsLine(ev,out line); if(!line || ++lines>1) Fail("trimmed or split analytic face boundary"); Point3d a,b; edge.GetVertices(out a,out b); double[] x={a.X,a.Y,a.Z}, y={b.X,b.Y,b.Z}; double zx=Dot(Sub(x,c.P),c.D),zy=Dot(Sub(y,c.P),c.D); double[] rx=Sub(Sub(x,c.P),Scale(c.D,zx)),ry=Sub(Sub(y,c.P),Scale(c.D,zy)); if(!Finite(Norm(rx)) || !Finite(Norm(ry)) || !Finite(Norm(Cross(rx,ry))) || Norm(Cross(rx,ry))>Distance*Math.Max(1,Math.Max(Norm(rx),Norm(ry)))) Fail("non-generator seam"); }
            } finally { if(ev!=IntPtr.Zero) try { c.Uf.Eval.Free(ev); } catch(Exception ex) { throw new CleanupException("UF.Eval.Free failed",ex); } }
        }
        s.Rings.Sort(delegate(Ring x,Ring y){return x.Z.CompareTo(y.Z);});
        Unit au=c.Part.UnitCollection.FindObject("SquareMilliMeter"), lu=c.Part.UnitCollection.FindObject("MilliMeter");
        MeasureFaces m=c.Part.MeasureManager.NewFaceProperties(au,lu,0.999999,new IParameterizedSurface[]{f});
        try { s.Area=m.Area; if(!Finite(s.Area) || s.Area<=0) Fail("invalid native face area"); }
        finally { try { m.Dispose(); } catch(Exception ex) { throw new CleanupException("MeasureFaces.Dispose failed",ex); } }
        return s;
    }
    private static void CheckArea(Surface s,double expected) { double tol=Math.Max(AreaAbsolute,expected*AreaRelative); s.ExpectedArea=expected; s.AreaTolerance=tol; if(!Finite(expected) || Math.Abs(s.Area-expected)>tol) Fail("analytic surface area mismatch"); }
    private static void CheckAnnulus(Context c,Face f,double r,double R,double z) { int t,n; double rr,aa; double[] p=new double[3],d=new double[3],box=new double[6]; c.Uf.Modl.AskFaceData(f.Tag,out t,p,d,box,out rr,out aa,out n); if(t!=UFConstants.UF_MODL_PLANAR_FACE || !Finite(Norm(d)) || Norm(d)<0.9 || !Finite(Dot(d,c.D)) || Math.Abs(Dot(d,c.D))/Norm(d)<1-Angular || !Near(Dot(Sub(p,c.P),c.D),z)) Fail("counterbore shoulder is not the required plane"); Edge[] es=f.GetEdges(); if(es.Length!=2) Fail("shoulder has extra or split boundary"); MeasureFaces m=c.Part.MeasureManager.NewFaceProperties(c.Part.UnitCollection.FindObject("SquareMilliMeter"),c.Part.UnitCollection.FindObject("MilliMeter"),0.999999,new IParameterizedSurface[]{f}); try { double expected=Math.PI*(R*R-r*r); if(!Finite(m.Area) || !Finite(expected) || Math.Abs(m.Area-expected)>Math.Max(AreaAbsolute,expected*AreaRelative)) Fail("counterbore shoulder area mismatch"); } finally { try {m.Dispose();} catch(Exception ex){throw new CleanupException("MeasureFaces.Dispose failed",ex);} } }
    private static void CheckPlanarBoundary(Context c,Ring ring,Face wall,double z,string label) { Face f=SharedOtherFace(ring,wall); int t,n; double rr,aa; double[] p=new double[3],d=new double[3],box=new double[6]; c.Uf.Modl.AskFaceData(f.Tag,out t,p,d,box,out rr,out aa,out n); if(t!=UFConstants.UF_MODL_PLANAR_FACE || !Finite(Norm(d)) || Norm(d)<0.9 || !Finite(Dot(d,c.D)) || Math.Abs(Dot(d,c.D))/Norm(d)<1-Angular || !Near(Dot(Sub(p,c.P),c.D),z)) Fail(label+" ring is not adjacent to the required normal plane"); c.Evidence[label+"_face_tag"]=f.Tag.ToString(); c.Evidence[label+"_edge_tag"]=ring.Edge.Tag.ToString(); }
    private static Face SharedOtherFace(Ring ring,Face wall) { Face[] faces=ring.Edge.GetFaces(); if(faces.Length!=2) { Fail("circular edge must have exactly two adjacent faces"); } Face other=null; int count=0; foreach(Face f in faces) if(f.Tag.Equals(wall.Tag)) count++; else other=f; if(count!=1 || other==null) Fail("circular edge lacks unique adjacent face"); return other; }
    private static bool HasEdge(Face f,Edge e) { foreach(Edge x in f.GetEdges()) if(x.Tag.Equals(e.Tag)) return true; return false; }
    private static void CheckNoBodyBeyondExitPlane(Context c, double exitZ)
    {
        double maximum = Double.NegativeInfinity;
        double[] secondary = Perpendicular(c.D), tertiary = Cross(c.D, secondary);
        Face[] faces = c.Body.GetFaces();
        if (faces == null || faces.Length == 0) Fail("cannot enumerate target faces for through exit bound");
        foreach (Face face in faces)
        {
            Tag subentity; double[] extreme = new double[3];
            c.Uf.Modl.AskExtreme(face.Tag, c.D, secondary, tertiary, out subentity, extreme);
            for (int i = 0; i < 3; i++) if (!Finite(extreme[i])) Fail("nonfinite face extreme for through exit bound");
            double z = Dot(Sub(extreme, c.P), c.D);
            if (!Finite(z)) Fail("nonfinite face projection for through exit bound");
            maximum = Math.Max(maximum, z);
        }
        c.Evidence["maximum_body_projection_mm"] = maximum;
        c.Evidence["exit_plane_bound_tolerance_mm"] = Distance;
        if (maximum > exitZ + Distance) Fail("target body extends beyond the proven through exit plane");
    }
    private static void CheckVoidSide(Context c,double r,double z) { double[] e=Perpendicular(c.D); double delta=Math.Min(0.01,r/10); int inside,outside; c.Uf.Modl.AskPointContainment(At(c,z,e,r-delta),c.Body.Tag,out inside); c.Uf.Modl.AskPointContainment(At(c,z,e,r+delta),c.Body.Tag,out outside); if(inside!=2 || outside!=1) Fail("cylinder is not a subtractive hole at radial witness"); }
    private static Dictionary<string,object> Describe(Surface s) { List<object> rings=new List<object>(); foreach(Ring r in s.Rings) rings.Add(new Dictionary<string,object>{{"edge_tag",r.Edge.Tag.ToString()},{"center_mm",r.Center},{"axis_distance_mm",r.Z},{"radius_mm",r.Radius}}); return new Dictionary<string,object>{{"face_tag",s.Face.Tag.ToString()},{"native_type",s.Type},{"axis_origin_mm",s.Origin},{"axis_direction",s.Axis},{"native_radius_mm",s.Radius},{"half_angle_rad",s.HalfAngle},{"normal_sense",s.Sense},{"area_mm2",s.Area},{"expected_area_mm2",s.ExpectedArea},{"area_tolerance_mm2",s.AreaTolerance},{"area_error_mm2",s.Area-s.ExpectedArea},{"rings",rings}}; }
    private static bool Coaxial(Context c,double[] p,double[] d) { double norm=Norm(d); if(!Finite(norm)||norm<0.9 || Math.Abs(Dot(d,c.D))/norm<1-Angular) return false; double[] v=Sub(p,c.P); double offset=Norm(Sub(v,Scale(c.D,Dot(v,c.D)))); return Finite(offset) && offset<=Distance; }
    private static double[] At(Context c,double z,double[] radial,double r) { return new double[]{c.P[0]+c.D[0]*z+radial[0]*r,c.P[1]+c.D[1]*z+radial[1]*r,c.P[2]+c.D[2]*z+radial[2]*r}; }
    private static double[] Perpendicular(double[] d) { double[] x=Math.Abs(d[0])<0.8?new double[]{1,0,0}:new double[]{0,1,0}; double[] v=Cross(d,x); return Scale(v,1/Norm(v)); }
    private static double[] Sub(double[] a,double[] b) {return new double[]{a[0]-b[0],a[1]-b[1],a[2]-b[2]};} private static double[] Scale(double[] a,double b){return new double[]{a[0]*b,a[1]*b,a[2]*b};} private static double Dot(double[] a,double[] b){return a[0]*b[0]+a[1]*b[1]+a[2]*b[2];} private static double Norm(double[] a){return Math.Sqrt(Dot(a,a));} private static double[] Cross(double[] a,double[] b){return new double[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};} private static bool Near(double a,double b){return Finite(a)&&Finite(b)&&Math.Abs(a-b)<=Distance;} private static bool Finite(double x){return !Double.IsNaN(x)&&!Double.IsInfinity(x);} private static void Fail(string s){throw new InvalidCoverage(s);}
}
