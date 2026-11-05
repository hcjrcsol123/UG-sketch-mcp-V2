using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using NXOpen;
using NXOpen.UF;
using NXOpen.Utilities;

// Transaction-local preservation evidence. Tags are stable only in this loaded
// part. This is a canonical numeric record, NOT a file hash. Analytic/NURBS edge
// definitions and parametric B-surfaces are retained; other surfaces also have a
// fixed UV sample grid. Finite surface samples are not a universal shape proof.
public static class F12BodyGeometrySnapshot
{
    public sealed class CleanupException : Exception
    {
        public string CleanupOperation { get; private set; }

        public CleanupException(string cleanupOperation, Exception innerException)
            : base("F12 body geometry resource cleanup failed: " + cleanupOperation, innerException)
        {
            CleanupOperation = cleanupOperation;
        }
    }

    public static string CaptureFingerprint(Part work, Body body)
    {
        return Canonical(Capture(work, body));
    }

    public static Dictionary<string, object> Capture(Part work, Body body)
    {
        if (work == null || body == null || body.OwningPart == null || body.OwningPart.Tag != work.Tag)
            throw new InvalidOperationException("F12 body geometry requires an explicit same-part Body.");
        if (work.PartUnits != BasePart.Units.Millimeters)
            throw new InvalidOperationException("F12 body geometry evidence requires millimetres.");
        UFSession uf = UFSession.GetUFSession();
        Dictionary<string, object> result = Map();
        result["body_tag"] = body.Tag.ToString();
        result["is_solid"] = body.IsSolidBody;
        double[] corner = new double[3], lengths = new double[3];
        double[,] axes = new double[3, 3];
        uf.Modl.AskBoundingBoxExact(body.Tag, Tag.Null, corner, axes, lengths);
        result["exact_box_corner_mm"] = corner;
        result["exact_box_axes"] = axes;
        result["exact_box_lengths_mm"] = lengths;
        int count; int[] faultTypes; Tag[] faultTags;
        uf.Modl.AskBodyConsistency(body.Tag, out count, out faultTypes, out faultTags);
        result["consistency_fault_count"] = count;
        result["consistency_fault_types"] = faultTypes;
        result["consistency_fault_tags"] = Tags(faultTags);

        Unit areaUnit = work.UnitCollection.FindObject("SquareMilliMeter");
        Unit lengthUnit = work.UnitCollection.FindObject("MilliMeter");
        Face[] faces = body.GetFaces();
        Edge[] edges = body.GetEdges();
        if (faces == null || edges == null || faces.Length == 0)
            throw new InvalidOperationException("F12 body topology is unavailable.");
        List<object> faceRecords = new List<object>(), edgeRecords = new List<object>();
        Array.Sort(faces, delegate(Face a, Face b) { return StringComparer.Ordinal.Compare(a.Tag.ToString(), b.Tag.ToString()); });
        Array.Sort(edges, delegate(Edge a, Edge b) { return StringComparer.Ordinal.Compare(a.Tag.ToString(), b.Tag.ToString()); });
        foreach (Face face in faces)
        {
            Dictionary<string, object> item = Map();
            item["tag"] = face.Tag.ToString();
            item["face_type"] = face.SolidFaceType.ToString();
            item["edge_tags"] = ObjectTags(face.GetEdges());
            int nativeType, normalSense;
            double[] origin = new double[3], direction = new double[3], box = new double[6];
            double radius, radiusData;
            uf.Modl.AskFaceData(face.Tag, out nativeType, origin, direction, box, out radius, out radiusData, out normalSense);
            item["native_type"] = nativeType;
            item["origin_mm"] = origin;
            item["direction"] = direction;
            item["box_mm"] = box;
            item["radius_mm"] = radius;
            item["radius_data"] = radiusData;
            item["normal_sense"] = normalSense;
            double[] uv = new double[4];
            uf.Modl.AskFaceUvMinmax(face.Tag, uv);
            item["uv_limits"] = uv;
            List<object> samples = new List<object>();
            for (int iu = 0; iu <= 4; iu++)
                for (int iv = 0; iv <= 4; iv++)
                {
                    double[] at = new double[] { uv[0] + (uv[1] - uv[0]) * iu / 4.0,
                        uv[2] + (uv[3] - uv[2]) * iv / 4.0 };
                    double[] point = new double[3], du = new double[3], dv = new double[3];
                    double[] duu = new double[3], dvv = new double[3], normal = new double[3], radii = new double[2];
                    uf.Modl.AskFaceProps(face.Tag, at, point, du, dv, duu, dvv, normal, radii);
                    Dictionary<string, object> sample = Map();
                    sample["uv"] = at; sample["point_mm"] = point;
                    sample["du"] = du; sample["dv"] = dv;
                    sample["duu"] = duu; sample["dvv"] = dvv;
                    sample["normal"] = normal;
                    // Curvature radii can be undefined at plane/surface singularities.
                    // They are not needed for preservation: derivatives are retained.
                    samples.Add(sample);
                }
            item["surface_samples"] = samples;
            if (face.SolidFaceType == Face.FaceType.Parametric)
            {
                UFModl.Bsurface bsurface;
                uf.Modl.AskBsurf(face.Tag, out bsurface);
                item["native_bsurface"] = PublicFields(bsurface);
            }
            MeasureFaces measured = work.MeasureManager.NewFaceProperties(areaUnit, lengthUnit, 0.999999,
                new IParameterizedSurface[] { face });
            try { item["area_mm2"] = measured.Area; }
            finally
            {
                try { measured.Dispose(); }
                catch (Exception ex) { throw new CleanupException("MeasureFaces.Dispose", ex); }
            }
            faceRecords.Add(item);
        }
        foreach (Edge edge in edges)
        {
            Dictionary<string, object> item = Map();
            item["tag"] = edge.Tag.ToString();
            item["edge_type"] = edge.SolidEdgeType.ToString();
            item["face_tags"] = ObjectTags(edge.GetFaces());
            IntPtr evaluator = IntPtr.Zero;
            try
            {
                uf.Eval.Initialize2(edge.Tag, out evaluator);
                if (evaluator == IntPtr.Zero) throw new InvalidOperationException("F12 edge evaluator is unavailable.");
                double[] limits = new double[2]; uf.Eval.AskLimits(evaluator, limits);
                item["parameter_limits"] = limits;
                List<object> samples = new List<object>();
                for (int i = 0; i <= 16; i++)
                {
                    double[] point = new double[3], derivative = new double[3];
                    uf.Eval.Evaluate(evaluator, 1, limits[0] + (limits[1] - limits[0]) * i / 16.0, point, derivative);
                    samples.Add(new object[] { point, derivative });
                }
                item["domain_samples"] = samples;
                bool isLine, isArc, isEllipse, isSpline;
                uf.Eval.IsLine(evaluator, out isLine); uf.Eval.IsArc(evaluator, out isArc);
                uf.Eval.IsEllipse(evaluator, out isEllipse); uf.Eval.IsSpline(evaluator, out isSpline);
                if (isLine) { UFEval.Line value; uf.Eval.AskLine(evaluator, out value); item["native_definition"] = PublicFields(value); }
                else if (isArc) { UFEval.Arc value; uf.Eval.AskArc(evaluator, out value); item["native_definition"] = PublicFields(value); }
                else if (isEllipse) { UFEval.Ellipse value; uf.Eval.AskEllipse(evaluator, out value); item["native_definition"] = PublicFields(value); }
                else if (isSpline)
                {
                    UFEval.Spline value; uf.Eval.AskSpline(evaluator, out value);
                    Dictionary<string, object> definition = PublicFields(value);
                    int n; double[] poles, knots;
                    uf.Eval.AskSplineControlPts(evaluator, out n, out poles);
                    definition["control_point_count"] = n; definition["control_points"] = poles;
                    uf.Eval.AskSplineKnots(evaluator, out n, out knots);
                    definition["knot_count"] = n; definition["knots"] = knots;
                    item["native_definition"] = definition;
                }
                else throw new InvalidOperationException("F12 non-target edge type lacks a complete supported evaluator definition.");
            }
            finally
            {
                if (evaluator != IntPtr.Zero)
                {
                    try { uf.Eval.Free(evaluator); }
                    catch (Exception ex) { throw new CleanupException("UF.Eval.Free", ex); }
                }
            }
            edgeRecords.Add(item);
        }
        result["faces"] = faceRecords;
        result["edges"] = edgeRecords;
        // Force finite-value validation before callers can retain this record.
        Canonical(result);
        return result;
    }

    public static string Canonical(object value)
    {
        StringBuilder output = new StringBuilder(); Append(output, value); return output.ToString();
    }

    private static Dictionary<string, object> Map() { return new Dictionary<string, object>(StringComparer.Ordinal); }
    private static List<string> Tags(Tag[] tags)
    {
        List<string> result = new List<string>();
        if (tags != null) foreach (Tag tag in tags) result.Add(tag.ToString());
        result.Sort(StringComparer.Ordinal); return result;
    }
    private static List<string> ObjectTags<T>(T[] values) where T : TaggedObject
    {
        if (values == null) throw new InvalidOperationException("F12 topology adjacency is unavailable.");
        List<string> result = new List<string>();
        foreach (T value in values) result.Add(value.Tag.ToString());
        result.Sort(StringComparer.Ordinal); return result;
    }
    private static Dictionary<string, object> PublicFields(object value)
    {
        Dictionary<string, object> result = Map();
        foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            result[field.Name] = field.GetValue(value);
        return result;
    }
    private static void Append(StringBuilder output, object value)
    {
        if (value == null) { output.Append("null;"); return; }
        IDictionary<string, object> map = value as IDictionary<string, object>;
        if (map != null)
        {
            List<string> keys = new List<string>(map.Keys); keys.Sort(StringComparer.Ordinal);
            output.Append('{'); foreach (string key in keys) { Append(output, key); Append(output, map[key]); } output.Append('}'); return;
        }
        string text = value as string;
        if (text != null) { output.Append(text.Length).Append(':').Append(text); return; }
        if (value is double || value is float)
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(number) || Double.IsInfinity(number))
                throw new InvalidOperationException("F12 body geometry contains a non-finite native value.");
            output.Append(number.ToString("R", CultureInfo.InvariantCulture)).Append(';'); return;
        }
        Array array = value as Array;
        if (array != null)
        {
            output.Append("array(").Append(array.Rank).Append(':');
            for (int dimension = 0; dimension < array.Rank; dimension++)
                output.Append(array.GetLowerBound(dimension)).Append(',').Append(array.GetLength(dimension)).Append(';');
            output.Append(")[");
            foreach (object item in array) Append(output, item);
            output.Append(']'); return;
        }
        IEnumerable sequence = value as IEnumerable;
        if (sequence != null) { output.Append('['); foreach (object item in sequence) Append(output, item); output.Append(']'); return; }
        if (value is bool || value is int || value is uint || value is long || value is short)
        { output.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(';'); return; }
        throw new InvalidOperationException("F12 body geometry contains an unsupported evidence value type.");
    }
}
