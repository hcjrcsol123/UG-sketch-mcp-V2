using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.UF;
using NxSketchMcp.M2;

public sealed partial class M2OpenModelAdapter
{
    private sealed class F17ExpectedEdge
    {
        internal double[] A, B;
        internal string Face1, Face2, Tag;
    }

    private sealed class F17ExpectedFace
    {
        internal string Key, Tag;
        internal double[] Point, Normal;
        internal double Area;
        internal List<F17ExpectedEdge> Edges = new List<F17ExpectedEdge>();
    }

    private static string F17PlaneKey(int axis, int side) { return "plane:" + axis + ":" + side; }
    private static string F17ChamferKey(int jSide, int kSide) { return "chamfer:" + jSide + ":" + kSide; }

    private static void F17AddEdge(List<F17ExpectedEdge> edges, Dictionary<string,F17ExpectedFace> faces,
        double[] a, double[] b, string f1, string f2)
    {
        F17ExpectedEdge edge = new F17ExpectedEdge { A = a, B = b, Face1 = f1, Face2 = f2 };
        edges.Add(edge);
        faces[f1].Edges.Add(edge);
        faces[f2].Edges.Add(edge);
    }

    private static bool F17PlaneMatches(F17ExpectedFace expected, int type, double[] point, double[] direction)
    {
        if (type != UFConstants.UF_MODL_PLANAR_FACE) return false;
        double norm = 0, dot = 0, distance = 0;
        for (int i = 0; i < 3; i++) {
            if (!IsFinite(point[i]) || !IsFinite(direction[i])) return false;
            norm += direction[i] * direction[i];
            dot += direction[i] * expected.Normal[i];
            distance += (point[i] - expected.Point[i]) * expected.Normal[i];
        }
        return norm > 0 && Math.Abs(Math.Abs(dot / Math.Sqrt(norm)) - 1) < 1e-8 && Math.Abs(distance) <= 1e-6;
    }

    private static bool F17Coverage(Body body, M3PlanState plan, out Dictionary<string,object> evidence)
    {
        List<object> errors = new List<object>();
        List<object> observed = new List<object>();
        bool ok = true;
        if (body == null || plan == null || plan.EdgeBlendInput == null || plan.EdgeBlendInput.Count == 0 || plan.EdgeBlendInput[0] == null ||
            plan.EdgeBlendBox == null || plan.EdgeBlendBox.Length != 6 ||
            !IsFinite(plan.F17OffsetJ) || !IsFinite(plan.F17OffsetK) || plan.F17OffsetJ <= 0 || plan.F17OffsetK <= 0) {
            evidence = new Dictionary<string,object> { {"status", "MISMATCH"}, {"errors", new object[] {"invalid_plan"}} };
            return false;
        }
        double[] box = plan.EdgeBlendBox;
        int ax = plan.EdgeBlendInput[0].Axis;
        if (ax < 0 || ax > 2) {
            evidence = new Dictionary<string,object> { {"status", "MISMATCH"}, {"errors", new object[] {"invalid_axis"}} };
            return false;
        }
        int j = (ax + 1) % 3, k = (ax + 2) % 3;
        double dj = plan.F17OffsetJ, dk = plan.F17OffsetK;
        for (int i = 0; i < 3; i++) if (!IsFinite(box[i]) || !IsFinite(box[i+3]) || box[i+3] - box[i] <= 1e-6) ok = false;
        if (!ok || box[j+3]-box[j] <= 2*Math.Max(dj,dk)+1e-6 || box[k+3]-box[k] <= 2*Math.Max(dj,dk)+1e-6) {
            evidence = new Dictionary<string,object> { {"status", "MISMATCH"}, {"errors", new object[] {"invalid_box_or_offsets"}} };
            return false;
        }
        bool[,] selected = new bool[2,2];
        List<object> inputEvidence = new List<object>();
        foreach (F16Edge input in plan.EdgeBlendInput) {
            if (input == null || input.Axis != ax || input.A == null || input.B == null || input.A.Length != 3 || input.B.Length != 3) {
                errors.Add("invalid_input_edge"); ok = false; continue;
            }
            int a = F16Near(input.A[j], box[j]) ? 0 : F16Near(input.A[j], box[j+3]) ? 1 : -1;
            int b = F16Near(input.A[k], box[k]) ? 0 : F16Near(input.A[k], box[k+3]) ? 1 : -1;
            if (a < 0 || b < 0 || !F16Near(input.B[j], box[j+3*a]) || !F16Near(input.B[k], box[k+3*b]) ||
                !((F16Near(input.A[ax],box[ax]) && F16Near(input.B[ax],box[ax+3])) ||
                  (F16Near(input.B[ax],box[ax]) && F16Near(input.A[ax],box[ax+3]))) || selected[a,b]) {
                errors.Add("invalid_or_duplicate_input_edge:" + input.Id); ok = false; continue;
            }
            selected[a,b] = true;
            double[] cutJ=(double[])input.A.Clone(), cutK=(double[])input.A.Clone();
            cutJ[j] += a==0 ? dj : -dj; cutK[k] += b==0 ? dk : -dk;
            inputEvidence.Add(new Dictionary<string,object>{{"input_edge_id",input.Id},{"input_edge_tag",input.Tag},{"j_cut_point_mm",cutJ},{"k_cut_point_mm",cutK}});
        }
        if (!ok) {
            evidence = new Dictionary<string,object> { {"status", "MISMATCH"}, {"errors", errors} };
            return false;
        }

        double length = box[ax+3]-box[ax], widthJ = box[j+3]-box[j], widthK = box[k+3]-box[k];
        Dictionary<string,F17ExpectedFace> expectedFaces = new Dictionary<string,F17ExpectedFace>(StringComparer.Ordinal);
        for (int axis = 0; axis < 3; axis++) for (int side = 0; side < 2; side++) {
            string key = F17PlaneKey(axis,side);
            double[] p = new double[3], n = new double[3]; p[axis] = box[axis+3*side]; n[axis] = 1;
            double area;
            if (axis == ax) area = widthJ * widthK - plan.EdgeBlendInput.Count * dj * dk / 2;
            else {
                int count = 0;
                for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++)
                    if (selected[a,b] && (axis == j ? a == side : b == side)) count++;
                area = length * (axis == j ? widthK - count * dk : widthJ - count * dj);
            }
            if (!IsFinite(area) || area <= 0) {ok=false; errors.Add("invalid_expected_area:"+key);}
            expectedFaces.Add(key, new F17ExpectedFace { Key = key, Point = p, Normal = n, Area = area });
        }
        for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++) if (selected[a,b]) {
            string key = F17ChamferKey(a,b);
            double[] p = F16Corner(box,ax,j,k,a,b,0), n = new double[3];
            p[j] += a == 0 ? dj : -dj;
            n[j] = (a == 0 ? 1 : -1) / dj; n[k] = (b == 0 ? 1 : -1) / dk;
            double magnitude = Math.Sqrt(n[j]*n[j] + n[k]*n[k]); n[j] /= magnitude; n[k] /= magnitude;
            double area=length * Math.Sqrt(dj*dj + dk*dk);
            if (!IsFinite(area) || area <= 0 || !IsFinite(n[j]) || !IsFinite(n[k])) {ok=false; errors.Add("invalid_expected_geometry:"+key);}
            expectedFaces.Add(key, new F17ExpectedFace { Key = key, Point = p, Normal = n, Area = area });
        }

        List<F17ExpectedEdge> expectedEdges = new List<F17ExpectedEdge>();
        for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++) {
            double[] lo = F16Corner(box,ax,j,k,a,b,0), hi = F16Corner(box,ax,j,k,a,b,1);
            if (!selected[a,b]) F17AddEdge(expectedEdges,expectedFaces,lo,hi,F17PlaneKey(j,a),F17PlaneKey(k,b));
            else {
                double[] loJ=(double[])lo.Clone(), hiJ=(double[])hi.Clone(), loK=(double[])lo.Clone(), hiK=(double[])hi.Clone();
                loJ[j] += a == 0 ? dj : -dj; hiJ[j] = loJ[j];
                loK[k] += b == 0 ? dk : -dk; hiK[k] = loK[k];
                string chamfer = F17ChamferKey(a,b);
                F17AddEdge(expectedEdges,expectedFaces,loJ,hiJ,chamfer,F17PlaneKey(k,b));
                F17AddEdge(expectedEdges,expectedFaces,loK,hiK,chamfer,F17PlaneKey(j,a));
                F17AddEdge(expectedEdges,expectedFaces,loJ,loK,chamfer,F17PlaneKey(ax,0));
                F17AddEdge(expectedEdges,expectedFaces,hiJ,hiK,chamfer,F17PlaneKey(ax,1));
            }
        }
        for (int side=0; side<2; side++) for (int fixedSide=0; fixedSide<2; fixedSide++) {
            double[] a=F16Corner(box,ax,j,k,fixedSide,0,side), b=F16Corner(box,ax,j,k,fixedSide,1,side);
            if(selected[fixedSide,0]) a[k]+=dk; if(selected[fixedSide,1]) b[k]-=dk;
            F17AddEdge(expectedEdges,expectedFaces,a,b,F17PlaneKey(ax,side),F17PlaneKey(j,fixedSide));
            a=F16Corner(box,ax,j,k,0,fixedSide,side); b=F16Corner(box,ax,j,k,1,fixedSide,side);
            if(selected[0,fixedSide]) a[j]+=dj; if(selected[1,fixedSide]) b[j]-=dj;
            F17AddEdge(expectedEdges,expectedFaces,a,b,F17PlaneKey(ax,side),F17PlaneKey(k,fixedSide));
        }

        Edge[] edges=body.GetEdges(); Face[] faces=body.GetFaces();
        if (edges.Length != expectedEdges.Count) { ok=false; errors.Add("edge_count"); }
        if (faces.Length != expectedFaces.Count) { ok=false; errors.Add("face_count"); }
        HashSet<string> bodyEdges=new HashSet<string>(StringComparer.Ordinal), bodyFaces=new HashSet<string>(StringComparer.Ordinal);
        foreach (Face face in faces) if (!bodyFaces.Add(face.Tag.ToString())) { ok=false; errors.Add("duplicate_body_face"); }
        foreach (Edge edge in edges) {
            string tag=edge.Tag.ToString();
            if (!bodyEdges.Add(tag)) {ok=false;errors.Add("duplicate_body_edge:"+tag);}
            Point3d pa,pb; edge.GetVertices(out pa,out pb); double[] a=F16Point(pa), b=F16Point(pb);
            int matches=0; F17ExpectedEdge hit=null;
            if (edge.SolidEdgeType == Edge.EdgeType.Linear) foreach (F17ExpectedEdge e in expectedEdges)
                if ((F16SamePoint(a,e.A) && F16SamePoint(b,e.B)) || (F16SamePoint(a,e.B) && F16SamePoint(b,e.A))) { matches++; hit=e; }
            if (matches != 1 || hit.Tag != null) {ok=false;errors.Add("edge_geometry:"+tag);continue;}
            hit.Tag=tag;
        }
        foreach (F17ExpectedEdge e in expectedEdges) if (e.Tag == null) {ok=false;errors.Add("missing_expected_edge");}

        Part part=body.OwningPart as Part;
        if(part==null) throw new InvalidOperationException("chamfer body has no owning part");
        foreach (Face face in faces) {
            string tag=face.Tag.ToString(); Edge[] boundary=face.GetEdges();
            HashSet<string> actual=new HashSet<string>(StringComparer.Ordinal);
            foreach (Edge edge in boundary) {
                string edgeTag=edge.Tag.ToString();
                if (!bodyEdges.Contains(edgeTag) || !actual.Add(edgeTag)) {ok=false;errors.Add("invalid_face_edge:"+tag);}
            }
            int type,normalSense; double radius,radialData; double[] point=new double[3],direction=new double[3],bounds=new double[6];
            UFSession.GetUFSession().Modl.AskFaceData(face.Tag,out type,point,direction,bounds,out radius,out radialData,out normalSense);
            int matches=0; F17ExpectedFace hit=null;
            foreach (F17ExpectedFace candidate in expectedFaces.Values) {
                HashSet<string> wanted=new HashSet<string>(StringComparer.Ordinal); bool complete=true;
                foreach (F17ExpectedEdge edge in candidate.Edges) { if (edge.Tag==null || !wanted.Add(edge.Tag)) complete=false; }
                if (complete && wanted.SetEquals(actual) && F17PlaneMatches(candidate,type,point,direction)) {matches++;hit=candidate;}
            }
            if (matches != 1 || hit.Tag != null) {ok=false;errors.Add("face_geometry_or_boundary:"+tag);continue;}
            hit.Tag=tag;
            MeasureFaces measurement=part.MeasureManager.NewFaceProperties(part.UnitCollection.FindObject("SquareMilliMeter"),part.UnitCollection.FindObject("MilliMeter"),0.999999,new IParameterizedSurface[]{face});
            double actualArea;
            try {actualArea=measurement.Area; if(!IsFinite(actualArea) || Math.Abs(actualArea-hit.Area)>Math.Max(1e-5,Math.Abs(hit.Area)*1e-6)) {ok=false;errors.Add("face_area:"+tag);}}
            finally {try {measurement.Dispose();} catch(Exception ex) {throw new F12BodyGeometrySnapshot.CleanupException("F17 MeasureFaces.Dispose",ex);}}
            observed.Add(new Dictionary<string,object>{{"expected_face",hit.Key},{"face_id",face.JournalIdentifier},{"face_tag",tag},{"expected_area_mm2",hit.Area},{"actual_area_mm2",actualArea},{"boundary_edge_count",boundary.Length}});
        }
        foreach (F17ExpectedFace face in expectedFaces.Values) if (face.Tag==null) {ok=false;errors.Add("missing_expected_face:"+face.Key);}
        foreach (Edge edge in edges) {
            string tag=edge.Tag.ToString(); F17ExpectedEdge hit=null;
            foreach (F17ExpectedEdge e in expectedEdges) if (e.Tag==tag) {hit=e;break;}
            Face[] adjacent=edge.GetFaces();
            if (hit==null || adjacent.Length!=2 || adjacent[0].Tag.Equals(adjacent[1].Tag)) {ok=false;errors.Add("edge_adjacency:"+tag);continue;}
            HashSet<string> actual=new HashSet<string>(StringComparer.Ordinal);
            foreach (Face face in adjacent) {
                string faceTag=face.Tag.ToString(); actual.Add(faceTag);
                int back=0; foreach (Edge fe in face.GetEdges()) if (fe.Tag.Equals(edge.Tag)) back++;
                if (!bodyFaces.Contains(faceTag) || back!=1) {ok=false;errors.Add("edge_face_reciprocity:"+tag);}
            }
            string f1=expectedFaces[hit.Face1].Tag, f2=expectedFaces[hit.Face2].Tag;
            if (f1==null || f2==null || f1==f2 || !actual.SetEquals(new string[]{f1,f2})) {ok=false;errors.Add("edge_expected_adjacency:"+tag);}
        }
        evidence=new Dictionary<string,object>{{"status",ok?"VERIFIED":"MISMATCH"},{"axis",ax},{"j_axis",j},{"k_axis",k},{"expected_edge_count",expectedEdges.Count},{"actual_edge_count",edges.Length},{"expected_face_count",expectedFaces.Count},{"actual_face_count",faces.Length},{"offset_j_mm",dj},{"offset_k_mm",dk},{"input_edges",inputEvidence},{"faces",observed},{"errors",errors}};
        return ok;
    }
}
