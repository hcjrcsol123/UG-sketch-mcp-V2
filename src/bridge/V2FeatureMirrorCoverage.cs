using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.UF;
using NxSketchMcp.M2;

public sealed partial class M2OpenModelAdapter
{
    // Preserve the seed's bit-indexed corner correspondence through the reflection.
    private static double[][] F19Expected(double[] box, double[] origin, double[] unitNormal)
    {
        if (box == null || box.Length != 6 || origin == null || origin.Length != 3 || unitNormal == null || unitNormal.Length != 3)
            throw new M2ModelException("UNSUPPORTED_MIRROR_GEOMETRY", "mirror box and plane must have valid dimensions");
        double lengthSquared = 0;
        for (int i = 0; i < 3; i++)
        {
            if (!IsFinite(box[i]) || !IsFinite(box[i + 3]) || box[i] >= box[i + 3] ||
                !IsFinite(origin[i]) || !IsFinite(unitNormal[i]))
                throw new M2ModelException("UNSUPPORTED_MIRROR_GEOMETRY", "mirror box or plane is invalid");
            lengthSquared += unitNormal[i] * unitNormal[i];
        }
        if (!IsFinite(lengthSquared) || Math.Abs(Math.Sqrt(lengthSquared) - 1.0) > 1e-9)
            throw new M2ModelException("UNSUPPORTED_MIRROR_GEOMETRY", "mirror plane normal must be a unit vector");
        double[][] points = new double[8][];
        for (int k = 0; k < 8; k++)
        {
            double[] p = F18Corner(box, k);
            double distance = 0;
            for (int i = 0; i < 3; i++) distance += (p[i] - origin[i]) * unitNormal[i];
            points[k] = new double[3];
            for (int i = 0; i < 3; i++)
            {
                points[k][i] = p[i] - 2.0 * distance * unitNormal[i];
                if (!IsFinite(points[k][i]) || Math.Abs(points[k][i]) > 1e8)
                    throw new M2ModelException("UNSUPPORTED_MIRROR_GEOMETRY", "reflected coordinates exceed supported range");
            }
        }
        return points;
    }

    private static bool F19BodyMatches(Body body, double[][] expected, Dictionary<string, object> detail, double? volume,
        out Dictionary<string, object> shape, out Dictionary<string, object> native)
    {
        shape = null;
        native = null;
        if (detail != null && !F19HealthyBody(detail)) return false;
        if (expected == null || expected.Length != 8) return false;
        foreach (double[] p in expected)
        {
            if (p == null || p.Length != 3) return false;
            foreach (double value in p) if (!IsFinite(value) || Math.Abs(value) > 1e8) return false;
        }
        bool ok = F18BodyMatches(body, expected, detail, volume, out shape, out native);
        if (native == null) return false;
        if (volume.HasValue && !IsFinite(volume.Value)) ok = false;
        List<object> nativeFaces = native["faces"] as List<object>;
        if (nativeFaces == null) return false;
        Dictionary<string, Dictionary<string, object>> rows = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (object value in nativeFaces)
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            if (row == null || !(row["face_tag"] is string)) return false;
            rows[(string)row["face_tag"]] = row;
            if (row["area_mm2"] is double && !IsFinite((double)row["area_mm2"])) ok = false;
        }
        foreach (Face face in body.GetFaces())
        {
            Dictionary<string, object> row;
            if (!rows.TryGetValue(face.Tag.ToString(), out row)) { ok = false; continue; }
            HashSet<int> corners = new HashSet<int>();
            foreach (Edge edge in face.GetEdges())
            {
                Point3d a, b;
                edge.GetVertices(out a, out b);
                corners.Add(F18Vertex(F16Point(a), expected));
                corners.Add(F18Vertex(F16Point(b), expected));
            }
            int axis = -1, side = -1;
            if (corners.Count == 4)
                for (int i = 0; i < 3; i++) for (int s = 0; s < 2; s++)
                {
                    bool same = true;
                    foreach (int corner in corners) if (corner < 0 || ((corner >> i) & 1) != s) same = false;
                    if (same) { axis = i; side = s; }
                }
            bool normalMatch = false;
            double[] outward = null;
            if (axis >= 0)
            {
                int first = side << axis, u = (axis + 1) % 3, v = (axis + 2) % 3;
                double[] p = expected[first], q = expected[first ^ (1 << u)], t = expected[first ^ (1 << v)];
                double[] inside = expected[first ^ (1 << axis)];
                double[] a = new double[3], b = new double[3];
                for (int i = 0; i < 3; i++) { a[i] = q[i] - p[i]; b[i] = t[i] - p[i]; }
                outward = new double[] { a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0] };
                double length = Math.Sqrt(outward[0]*outward[0] + outward[1]*outward[1] + outward[2]*outward[2]);
                double inwardDot = 0;
                for (int i = 0; i < 3; i++) inwardDot += outward[i] * (inside[i] - p[i]);
                // The transformed frame has determinant -1. Select the sign by
                // the opposite corner, rather than assuming the seed's handedness.
                if (IsFinite(length) && length > 0 && IsFinite(inwardDot) && inwardDot != 0)
                {
                    double sign = inwardDot > 0 ? -1 : 1;
                    for (int i = 0; i < 3; i++) outward[i] *= sign / length;
                    double[] center = new double[3];
                    foreach (int corner in corners)
                        for (int i = 0; i < 3; i++) center[i] += expected[corner][i] * 0.25;
                    double[] uv = new double[2], projected = new double[3], evaluated = new double[3];
                    double[] du = new double[3], dv = new double[3], duu = new double[3], dvv = new double[3];
                    double[] faceNormal = new double[3], radii = new double[2];
                    UFSession.GetUFSession().Modl.AskFaceParm(face.Tag, center, uv, projected);
                    UFSession.GetUFSession().Modl.AskFaceProps(face.Tag, uv, evaluated, du, dv, duu, dvv, faceNormal, radii);
                    bool centerMatch = true;
                    for (int i = 0; i < 3; i++)
                        if (!IsFinite(center[i]) || !IsFinite(projected[i]) || !IsFinite(evaluated[i]) ||
                            !F16Near(center[i], projected[i]) || !F16Near(center[i], evaluated[i])) centerMatch = false;
                    if (!IsFinite(uv[0]) || !IsFinite(uv[1])) centerMatch = false;
                    double dLength = Math.Sqrt(faceNormal[0]*faceNormal[0] + faceNormal[1]*faceNormal[1] + faceNormal[2]*faceNormal[2]);
                    double dot = outward[0]*faceNormal[0] + outward[1]*faceNormal[1] + outward[2]*faceNormal[2];
                    normalMatch = centerMatch && IsFinite(dLength) && dLength > 0 && IsFinite(dot) && dot / dLength > 1.0 - 1e-8;
                    row["face_center_mm"] = center;
                    row["face_center_uv"] = uv;
                    row["face_center_projected_mm"] = projected;
                    row["face_center_evaluated_mm"] = evaluated;
                    row["face_center_matches"] = centerMatch;
                    row["face_props_unit_normal"] = faceNormal;
                    row["face_props_du"] = du;
                    row["face_props_dv"] = dv;
                    double[] cross = new double[] { du[1]*dv[2]-du[2]*dv[1], du[2]*dv[0]-du[0]*dv[2], du[0]*dv[1]-du[1]*dv[0] };
                    double crossLength = Math.Sqrt(cross[0]*cross[0] + cross[1]*cross[1] + cross[2]*cross[2]);
                    double crossDot = cross[0]*faceNormal[0] + cross[1]*faceNormal[1] + cross[2]*faceNormal[2];
                    if (IsFinite(crossLength) && crossLength > 0 && IsFinite(dLength) && dLength > 0 && IsFinite(crossDot))
                    {
                        row["face_props_normal_dot_unit_cross"] = crossDot / (crossLength * dLength);
                    }
                }
            }
            row["expected_outward_normal"] = outward;
            row["normal_matches"] = normalMatch;
            if (!normalMatch) ok = false;
        }
        return ok;
    }
}
