using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.UF;
using NxSketchMcp.M2;

public sealed partial class M2OpenModelAdapter
{
    private sealed class F16Edge { internal Edge Edge; internal string Id, Tag; internal int Axis; internal double[] A, B; }
    private static bool F16Near(double a, double b) { return Math.Abs(a-b) <= 1e-6; }
    private static double[] F16Point(Point3d p) { return new double[] { p.X, p.Y, p.Z }; }
    private static bool F16SamePoint(double[] a, double[] b) { for(int i=0;i<3;i++) if(!F16Near(a[i],b[i]))return false;return true; }
    private static void F16Reject(string message) { throw new M2ModelException("UNSUPPORTED_EDGE_BLEND_GEOMETRY", message); }

    private static List<F16Edge> F16Preflight(Body body, string[] ids, double radius, out double[] box)
    {
        box = new double[6]; UFSession.GetUFSession().Modl.AskBoundingBox(body.Tag, box);
        for (int i=0;i<3;i++) if (!IsFinite(box[i]) || !IsFinite(box[i+3]) || box[i+3]-box[i] <= 1e-6) F16Reject("box dimensions must be finite and positive");
        Face[] faces=body.GetFaces(); Edge[] edges=body.GetEdges();
        if (faces.Length!=6 || edges.Length!=12) F16Reject("target must be a complete unmodified rectangular box");
        HashSet<string> faceSides=new HashSet<string>(StringComparer.Ordinal);
        foreach (Face face in faces) {
            int t,n; double r,rd; double[] p=new double[3], d=new double[3], fb=new double[6];
            UFSession.GetUFSession().Modl.AskFaceData(face.Tag,out t,p,d,fb,out r,out rd,out n);
            if(t!=UFConstants.UF_MODL_PLANAR_FACE || face.GetEdges().Length!=4) F16Reject("box must have six four-edge planar faces");
            int axis=-1; for(int i=0;i<3;i++) if(Math.Abs(Math.Abs(d[i])-1)<1e-8) axis=i;
            if(axis<0) F16Reject("box faces must be world-axis aligned");
            string side=axis+":"+(F16Near(p[axis],box[axis])?"lo":F16Near(p[axis],box[axis+3])?"hi":"invalid");
            if(side.EndsWith("invalid") || !faceSides.Add(side)) F16Reject("box face positions are ambiguous");
            for(int i=0;i<3;i++) if(i!=axis && (!F16Near(fb[i],box[i]) || !F16Near(fb[i+3],box[i+3]))) F16Reject("box face bounds differ from the target bounds");
        }
        Dictionary<string,List<F16Edge>> byId=new Dictionary<string,List<F16Edge>>(StringComparer.Ordinal);
        HashSet<string> segments=new HashSet<string>(StringComparer.Ordinal); Dictionary<string,int> corners=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(Edge edge in edges) {
            if(edge.SolidEdgeType!=Edge.EdgeType.Linear || edge.GetFaces().Length!=2) F16Reject("box edges must be straight and have two adjacent faces");
            Point3d pa,pb; edge.GetVertices(out pa,out pb); double[] a=F16Point(pa), b=F16Point(pb); int axis=-1; string ka="",kb="";
            for(int i=0;i<3;i++) {
                bool al=F16Near(a[i],box[i]), ah=F16Near(a[i],box[i+3]), bl=F16Near(b[i],box[i]), bh=F16Near(b[i],box[i+3]);
                if((!al&&!ah)||(!bl&&!bh)) F16Reject("box edges must join box vertices");
                ka+=ah?"1":"0"; kb+=bh?"1":"0";
                if(!F16Near(a[i],b[i])) { if(axis>=0) F16Reject("box edges must follow one world axis"); axis=i; }
            }
            if(axis<0) F16Reject("degenerate box edge");
            string segment=String.CompareOrdinal(ka,kb)<0?ka+kb:kb+ka;
            if(!segments.Add(segment)) F16Reject("duplicate box edge");
            foreach(string k in new string[]{ka,kb}) {int c;corners.TryGetValue(k,out c);corners[k]=c+1;}
            F16Edge item=new F16Edge { Edge=edge, Id=RequiredJournalId(edge,"edge"), Tag=edge.Tag.ToString(), Axis=axis, A=a, B=b };
            List<F16Edge> list; if(!byId.TryGetValue(item.Id,out list)) {list=new List<F16Edge>();byId.Add(item.Id,list);} list.Add(item);
        }
        if(corners.Count!=8) F16Reject("box must have exactly eight vertices"); foreach(int c in corners.Values) if(c!=3) F16Reject("box vertex adjacency differs");
        List<F16Edge> selected=new List<F16Edge>(); int selectedAxis=-1;
        foreach(string id in ids) { List<F16Edge> list; if(!byId.TryGetValue(id,out list) || list.Count!=1) F16Reject("each edge ID must uniquely identify an edge of the target"); F16Edge item=list[0];
            if(selectedAxis<0) selectedAxis=item.Axis; else if(selectedAxis!=item.Axis) F16Reject("selected edges must be parallel"); selected.Add(item); }
        for (int i=0;i<3;i++) if (i!=selectedAxis && box[i+3]-box[i] <= 2*radius+1e-6) F16Reject("radius must be less than half each transverse box dimension");
        return selected;
    }

    private static bool F16Coverage(Body body, M3PlanState plan, out Dictionary<string,object> evidence)
    {
        List<object> observed=new List<object>(); bool all=true; Face[] faces=body.GetFaces();
        int cylinders=0, planes=0; foreach(Face f in faces) {int t,n;double r,rd;double[] p=new double[3],d=new double[3],b=new double[6]; UFSession.GetUFSession().Modl.AskFaceData(f.Tag,out t,p,d,b,out r,out rd,out n); if(t==UFConstants.UF_MODL_CYLINDRICAL_FACE)cylinders++; else if(t==UFConstants.UF_MODL_PLANAR_FACE)planes++; else all=false;}
        if(cylinders!=plan.EdgeBlendInput.Count || planes!=6) all=false;
        foreach(F16Edge input in plan.EdgeBlendInput) {
            int count=0; Dictionary<string,object> hit=null; int ax=input.Axis; double radius=plan.EdgeBlendRequest.RadiusMm;
            foreach(Face face in faces) {int t,n;double r,rd;double[] p=new double[3],d=new double[3],b=new double[6]; UFSession.GetUFSession().Modl.AskFaceData(face.Tag,out t,p,d,b,out r,out rd,out n);
                if(t!=UFConstants.UF_MODL_CYLINDRICAL_FACE || !F16Near(r,radius) || Math.Abs(Math.Abs(d[ax])-1)>1e-8) continue;
                bool match=F16Near(b[ax],plan.EdgeBlendBox[ax]) && F16Near(b[ax+3],plan.EdgeBlendBox[ax+3]);
                for(int i=0;i<3;i++) if(i!=ax) {double expected=input.A[i]+(F16Near(input.A[i],plan.EdgeBlendBox[i])?radius:-radius); if(!F16Near(p[i],expected)) match=false;}
                Edge[] boundary=face.GetEdges(); int lines=0,arcs=0,adjacent=0; HashSet<string> boundaryLabels=new HashSet<string>(StringComparer.Ordinal);
                int j=(ax+1)%3,k=(ax+2)%3; double[] tangentJ=(double[])input.A.Clone(),tangentK=(double[])input.A.Clone();
                tangentJ[j]+=F16Near(input.A[j],plan.EdgeBlendBox[j])?radius:-radius; tangentK[k]+=F16Near(input.A[k],plan.EdgeBlendBox[k])?radius:-radius;
                foreach(Edge edge in boundary) {
                    Point3d ea,eb;edge.GetVertices(out ea,out eb);double[] a=F16Point(ea),z=F16Point(eb); string label=null; int planeAxis=-1;double planeValue=0;
                    if(edge.SolidEdgeType==Edge.EdgeType.Linear) {lines++;double[] lo=(double[])tangentJ.Clone(),hi=(double[])tangentJ.Clone();lo[ax]=plan.EdgeBlendBox[ax];hi[ax]=plan.EdgeBlendBox[ax+3];
                        if((F16SamePoint(a,lo)&&F16SamePoint(z,hi))||(F16SamePoint(z,lo)&&F16SamePoint(a,hi))) {label="linear_j";planeAxis=k;planeValue=input.A[k];}
                        lo=(double[])tangentK.Clone();hi=(double[])tangentK.Clone();lo[ax]=plan.EdgeBlendBox[ax];hi[ax]=plan.EdgeBlendBox[ax+3];
                        if((F16SamePoint(a,lo)&&F16SamePoint(z,hi))||(F16SamePoint(z,lo)&&F16SamePoint(a,hi))) {label="linear_k";planeAxis=j;planeValue=input.A[j];}
                    } else if(edge.SolidEdgeType==Edge.EdgeType.Circular) {arcs++;for(int side=0;side<2;side++) {double[] x=(double[])tangentJ.Clone(),y=(double[])tangentK.Clone();x[ax]=plan.EdgeBlendBox[ax+3*side];y[ax]=x[ax];if((F16SamePoint(a,x)&&F16SamePoint(z,y))||(F16SamePoint(z,x)&&F16SamePoint(a,y))) {label="arc_"+side;planeAxis=ax;planeValue=x[ax];}}}
                    Face[] neighbors=edge.GetFaces(); if(neighbors.Length==2) { foreach(Face other in neighbors) if(!other.Tag.Equals(face.Tag)) {int ot,on;double orr,ord;double[] op=new double[3],od=new double[3],ob=new double[6];UFSession.GetUFSession().Modl.AskFaceData(other.Tag,out ot,op,od,ob,out orr,out ord,out on); if(ot==UFConstants.UF_MODL_PLANAR_FACE && planeAxis>=0 && Math.Abs(Math.Abs(od[planeAxis])-1)<1e-8 && F16Near(op[planeAxis],planeValue)) adjacent++;} }
                    if(label==null || !boundaryLabels.Add(label))match=false;
                }
                match=match && boundary.Length==4 && lines==2 && arcs==2 && adjacent==4 && boundaryLabels.Count==4;
                if(match) {count++;hit=new Dictionary<string,object>{{"face_id",face.JournalIdentifier},{"face_tag",face.Tag.ToString()},{"radius_mm",r},{"axis_point_mm",p},{"axis_direction",d},{"bounds_mm",b},{"linear_edges",lines},{"circular_edges",arcs},{"planar_adjacencies",adjacent}};}
            }
            if(count!=1)all=false; observed.Add(new Dictionary<string,object>{{"input_edge_id",input.Id},{"input_edge_tag",input.Tag},{"matched_faces",count},{"face",hit}});
        }
        Dictionary<string,object> graph; bool graphOk=F16GraphAndAreas(body,plan,out graph); all=all && graphOk;
        evidence=new Dictionary<string,object>{{"status",all?"VERIFIED":"MISMATCH"},{"cylindrical_face_count",cylinders},{"planar_face_count",planes},{"edges",observed},{"complete_boundary_graph",graph}}; return all;
    }

    private sealed class F16ExpectedEdge { internal double[] A,B,Corner; internal string Tag; internal bool Arc; internal bool Matched; }
    private static double[] F16Corner(double[] box,int ax,int j,int k,int a,int b,int side)
    {double[] p=new double[3];p[ax]=box[ax+3*side];p[j]=box[j+3*a];p[k]=box[k+3*b];return p;}
    private static bool F16Selected(M3PlanState plan,int j,int k,int a,int b)
    {foreach(F16Edge e in plan.EdgeBlendInput)if(F16Near(e.A[j],plan.EdgeBlendBox[j+3*a]) && F16Near(e.A[k],plan.EdgeBlendBox[k+3*b]))return true;return false;}
    private static void F16AddEdge(List<F16ExpectedEdge> list,double[] a,double[] b,bool arc,double[] corner)
    {list.Add(new F16ExpectedEdge {A=a,B=b,Arc=arc,Corner=corner});}
    private static bool F16ArcMidpoint(Edge edge,double[] corner,int axis,double radius)
    {
        IntPtr evaluator=IntPtr.Zero; UFSession uf=UFSession.GetUFSession();
        try {uf.Eval.Initialize2(edge.Tag,out evaluator);if(evaluator==IntPtr.Zero)return false;bool arc;uf.Eval.IsArc(evaluator,out arc);if(!arc)return false;
            double[] limits=new double[2],point=new double[3],derivative=new double[3];uf.Eval.AskLimits(evaluator,limits);uf.Eval.Evaluate(evaluator,1,(limits[0]+limits[1])*0.5,point,derivative);
            UFEval.Arc data;uf.Eval.AskArc(evaluator,out data);
            Point3d pa,pb;edge.GetVertices(out pa,out pb);double[] a=F16Point(pa),b=F16Point(pb);
            double[] expected=new double[3],centerExpected=new double[3];for(int i=0;i<3;i++) {if(i==axis) {expected[i]=corner[i];centerExpected[i]=corner[i];}else {centerExpected[i]=a[i]+b[i]-corner[i];expected[i]=centerExpected[i]+(corner[i]-centerExpected[i])/Math.Sqrt(2.0);}}
            return F16SamePoint(point,expected) && F16SamePoint(data.center,centerExpected) && F16Near(data.radius,radius) && Math.Abs((limits[1]-limits[0])-Math.PI/2)<1e-8 && Math.Abs(data.x_axis[axis])<1e-8 && Math.Abs(data.y_axis[axis])<1e-8;
        } finally {if(evaluator!=IntPtr.Zero)try{uf.Eval.Free(evaluator);}catch(Exception ex){throw new F12BodyGeometrySnapshot.CleanupException("F16 UF.Eval.Free",ex);}}
    }
    private static bool F16GraphAndAreas(Body body,M3PlanState plan,out Dictionary<string,object> evidence)
    {
        int ax=plan.EdgeBlendInput[0].Axis,j=(ax+1)%3,k=(ax+2)%3;double[] box=plan.EdgeBlendBox;double radius=plan.EdgeBlendRequest.RadiusMm;
        List<F16ExpectedEdge> expected=new List<F16ExpectedEdge>();
        for(int a=0;a<2;a++)for(int b=0;b<2;b++) {
            bool selected=F16Selected(plan,j,k,a,b); double[] lo=F16Corner(box,ax,j,k,a,b,0),hi=F16Corner(box,ax,j,k,a,b,1);
            if(!selected) F16AddEdge(expected,lo,hi,false,null);
            else {double[] loJ=(double[])lo.Clone(),hiJ=(double[])hi.Clone(),loK=(double[])lo.Clone(),hiK=(double[])hi.Clone();loJ[j]+=(a==0?radius:-radius);hiJ[j]=loJ[j];loK[k]+=(b==0?radius:-radius);hiK[k]=loK[k];F16AddEdge(expected,loJ,hiJ,false,null);F16AddEdge(expected,loK,hiK,false,null);F16AddEdge(expected,loJ,loK,true,lo);F16AddEdge(expected,hiJ,hiK,true,hi);}
        }
        for(int side=0;side<2;side++)for(int fixedSide=0;fixedSide<2;fixedSide++) {
            double[] a=F16Corner(box,ax,j,k,fixedSide,0,side),b=F16Corner(box,ax,j,k,fixedSide,1,side);
            if(F16Selected(plan,j,k,fixedSide,0))a[k]+=radius;if(F16Selected(plan,j,k,fixedSide,1))b[k]-=radius;F16AddEdge(expected,a,b,false,null);
            a=F16Corner(box,ax,j,k,0,fixedSide,side);b=F16Corner(box,ax,j,k,1,fixedSide,side);
            if(F16Selected(plan,j,k,0,fixedSide))a[j]+=radius;if(F16Selected(plan,j,k,1,fixedSide))b[j]-=radius;F16AddEdge(expected,a,b,false,null);
        }
        bool ok=true;Edge[] edges=body.GetEdges();Face[] faces=body.GetFaces();if(edges.Length!=expected.Count)ok=false;List<object> errors=new List<object>();
        HashSet<string> bodyEdges=new HashSet<string>(StringComparer.Ordinal);foreach(Edge edge in edges) {
            string tag=edge.Tag.ToString();if(!bodyEdges.Add(tag))ok=false;Point3d pa,pb;edge.GetVertices(out pa,out pb);double[] a=F16Point(pa),b=F16Point(pb);bool isArc=edge.SolidEdgeType==Edge.EdgeType.Circular;bool isLine=edge.SolidEdgeType==Edge.EdgeType.Linear;
            int matches=0;F16ExpectedEdge hit=null;foreach(F16ExpectedEdge e in expected)if(!e.Matched && ((e.Arc&&isArc)||(!e.Arc&&isLine)) && ((F16SamePoint(a,e.A)&&F16SamePoint(b,e.B))||(F16SamePoint(a,e.B)&&F16SamePoint(b,e.A)))) {matches++;hit=e;}
            if(matches!=1) {ok=false;errors.Add("edge_geometry:"+tag);continue;}hit.Matched=true;hit.Tag=tag;if(hit.Arc && !F16ArcMidpoint(edge,hit.Corner,ax,radius)){ok=false;errors.Add("arc_quadrant:"+tag);}
            Face[] adjacent=edge.GetFaces();if(adjacent.Length!=2 || adjacent[0].Tag.Equals(adjacent[1].Tag)){ok=false;errors.Add("edge_adjacency:"+tag);} else foreach(Face f in adjacent) {int back=0;foreach(Edge fe in f.GetEdges())if(fe.Tag.Equals(edge.Tag))back++;if(back!=1){ok=false;errors.Add("edge_face_reciprocity:"+tag);}}
        }
        foreach(F16ExpectedEdge e in expected)if(!e.Matched){ok=false;errors.Add("missing_expected_edge");}
        Part part=body.OwningPart as Part; if(part==null)throw new InvalidOperationException("edge blend body has no owning part");
        double length=box[ax+3]-box[ax];HashSet<string> planarSides=new HashSet<string>(StringComparer.Ordinal);
        foreach(Face face in faces) {
            foreach(Edge edge in face.GetEdges())if(!bodyEdges.Contains(edge.Tag.ToString())){ok=false;errors.Add("face_edge_not_in_body");}
            int t,n;double r,rd;double[] p=new double[3],d=new double[3],bounds=new double[6];UFSession.GetUFSession().Modl.AskFaceData(face.Tag,out t,p,d,bounds,out r,out rd,out n);
            double expectedArea=Double.NaN;
            if(t==UFConstants.UF_MODL_PLANAR_FACE) {int normal=-1;for(int i=0;i<3;i++)if(Math.Abs(Math.Abs(d[i])-1)<1e-8)normal=i;int side=normal<0?-1:F16Near(p[normal],box[normal])?0:F16Near(p[normal],box[normal+3])?1:-1;
                if(side<0 || !planarSides.Add(normal+":"+side)){ok=false;errors.Add("plane_identity");continue;}
                HashSet<string> expectedFaceEdges=new HashSet<string>(StringComparer.Ordinal), actualFaceEdges=new HashSet<string>(StringComparer.Ordinal);
                foreach(F16ExpectedEdge e in expected)if(e.Tag!=null && F16Near(e.A[normal],box[normal+3*side]) && F16Near(e.B[normal],box[normal+3*side]))expectedFaceEdges.Add(e.Tag);
                foreach(Edge edge in face.GetEdges())if(!actualFaceEdges.Add(edge.Tag.ToString())){ok=false;errors.Add("duplicate_face_edge");}
                if(!expectedFaceEdges.SetEquals(actualFaceEdges)){ok=false;errors.Add("plane_boundary:"+normal+":"+side);}
                if(normal==ax) expectedArea=(box[j+3]-box[j])*(box[k+3]-box[k])-plan.EdgeBlendInput.Count*(1-Math.PI/4)*radius*radius;
                else {int count=0;foreach(F16Edge e in plan.EdgeBlendInput)if(F16Near(e.A[normal],box[normal+3*side]))count++;int other=normal==j?k:j;expectedArea=length*((box[other+3]-box[other])-count*radius);}
            } else if(t==UFConstants.UF_MODL_CYLINDRICAL_FACE)expectedArea=length*Math.PI*radius/2;else {ok=false;errors.Add("unsupported_face");continue;}
            MeasureFaces measurement=part.MeasureManager.NewFaceProperties(part.UnitCollection.FindObject("SquareMilliMeter"),part.UnitCollection.FindObject("MilliMeter"),0.999999,new IParameterizedSurface[]{face});
            try {if(!IsFinite(measurement.Area)||Math.Abs(measurement.Area-expectedArea)>Math.Max(1e-5,Math.Abs(expectedArea)*1e-6)){ok=false;errors.Add("face_area:"+face.Tag);}}
            finally {try{measurement.Dispose();}catch(Exception ex){throw new F12BodyGeometrySnapshot.CleanupException("F16 MeasureFaces.Dispose",ex);}}
        }
        if(planarSides.Count!=6)ok=false;evidence=new Dictionary<string,object>{{"status",ok?"VERIFIED":"MISMATCH"},{"expected_edge_count",expected.Count},{"actual_edge_count",edges.Length},{"planar_sides",planarSides.Count},{"errors",errors}};return ok;
    }
}
