using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

/// <summary>Native NX Revolve creation for validated whole-sketch F13 sections.</summary>
public sealed class M3RevolveNativeResult
{
    public Feature Feature;
    public Dictionary<string, object> SectionEvidence;
    public string AxisTag;
    public string AxisPointTag;
    public string AxisDirectionTag;
    public double NativeStartAngleDeg;
    public double NativeEndAngleDeg;
}

public static class V2SketchRevolveFactory
{
    private const double MinimumAngleSpanDeg = 1.0e-9;
    private const double MaximumAxisCoordinateMm = 1000000.0;

    /// <summary>Creates a native solid or sheet Revolve from every allowed curve in a Sketch.</summary>
    public static M3RevolveNativeResult Create(Part part, Sketch sketch,
        string expectedInputDomainFingerprint, M3RevolveRequest request, Body targetBody)
    {
        RequirePart(part);
        if (sketch == null) throw new ArgumentNullException("sketch");
        if (sketch.Feature == null) throw new ArgumentException("Sketch has no associated feature.", "sketch");
        if (request == null) throw new ArgumentNullException("request");
        if (String.IsNullOrEmpty(expectedInputDomainFingerprint))
            throw new ArgumentException("A validated input geometry domain fingerprint is required.", "expectedInputDomainFingerprint");
        if (!sketch.OwningPart.Tag.Equals(part.Tag))
            throw new ArgumentException("Sketch does not belong to the supplied Part.", "sketch");
        if (part.PartUnits != BasePart.Units.Millimeters)
            throw new ArgumentException("F13 axis point coordinates and tolerance require a millimeter Part.", "part");

        M3BooleanMode booleanMode = ParseBooleanMode(request.Mode);
        string profileOutput = request.ProfileOutput;
        if (profileOutput != "solid" && profileOutput != "sheet")
            throw new ArgumentException("profileOutput must be solid or sheet.", "request");
        if (profileOutput == "sheet" && booleanMode != M3BooleanMode.Create)
            throw new ArgumentException("Sheet output is supported only for create Revolves.", "request");
        if (booleanMode == M3BooleanMode.Create)
        {
            if (targetBody != null)
                throw new ArgumentException("Create mode does not accept a target body.", "targetBody");
        }
        else
        {
            if (profileOutput != "solid")
                throw new ArgumentException("Boolean Revolve operations require solid output.", "request");
            RequireTargetBody(part, targetBody);
        }

        Point3d axisPoint = ReadAxisPoint(request.AxisPointMm);
        Vector3d requestedDirection = ReadUnitAxisDirection(request.AxisDirection);
        double requestedSpan = request.EndAngleDeg - request.StartAngleDeg;
        RequireAngleEndpoint(request.StartAngleDeg, "start angle");
        RequireAngleEndpoint(request.EndAngleDeg, "end angle");
        RequireFinite(requestedSpan, "signed angle span");
        if (!IsSpanOutsideTolerance(requestedSpan) || requestedSpan > 360.0 || requestedSpan < -360.0)
            throw new ArgumentOutOfRangeException("request", "Signed angle span must be nonzero and have magnitude at most 360 degrees.");

        Vector3d nativeDirection;
        double nativeStartAngle;
        double nativeEndAngle;
        if (requestedSpan > 0.0)
        {
            nativeDirection = requestedDirection;
            nativeStartAngle = request.StartAngleDeg;
            nativeEndAngle = request.EndAngleDeg;
        }
        else
        {
            nativeDirection = new Vector3d(-requestedDirection.X, -requestedDirection.Y, -requestedDirection.Z);
            nativeStartAngle = -request.StartAngleDeg;
            nativeEndAngle = -request.EndAngleDeg;
        }

        Curve seed = GetFirstActiveCurve(sketch);
        Section section = part.Sections.CreateSection(0.0095, 0.01, 0.5);
        CurveFeatureRule curveRule = part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { sketch.Feature });
        section.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
        section.SetAllowRefCrvs(false);
        section.AddToSection(new SelectionIntentRule[] { curveRule }, seed, null, null,
            sketch.Origin, Section.Mode.Create, true);

        Dictionary<string, object> sectionEvidence = V2SketchProfileAnalyzer.AnalyzeF12SectionDomain(
            part, sketch, section);
        if (!SectionDomainMatches(sectionEvidence, expectedInputDomainFingerprint))
            throw new InvalidOperationException("NX Revolve Section does not exactly match the validated non-reference whole-sketch profile domain.");

        Point nativeAxisPoint = part.Points.CreatePoint(axisPoint);
        Direction nativeAxisDirection = part.Directions.CreateDirection(axisPoint, nativeDirection,
            SmartObject.UpdateOption.WithinModeling);
        Axis nativeAxis = part.Axes.CreateAxis(nativeAxisPoint, nativeAxisDirection,
            SmartObject.UpdateOption.WithinModeling);

        RevolveBuilder builder = null;
        Feature feature;
        try
        {
            builder = part.Features.CreateRevolveBuilder(null);
            builder.Axis = nativeAxis;
            builder.Section = section;
            builder.FeatureOptions.BodyType = profileOutput == "sheet"
                ? NXOpen.GeometricUtilities.FeatureOptions.BodyStyle.Sheet
                : NXOpen.GeometricUtilities.FeatureOptions.BodyStyle.Solid;

            builder.Limits.StartExtend.TrimType = NXOpen.GeometricUtilities.Extend.ExtendType.Value;
            builder.Limits.EndExtend.TrimType = NXOpen.GeometricUtilities.Extend.ExtendType.Value;

            builder.Limits.SymmetricOption = false;
            builder.Offset.Option = NXOpen.GeometricUtilities.Type.NoOffset;
            builder.Offset.StartOffset.RightHandSide = "0";
            builder.Offset.EndOffset.RightHandSide = "0";
            builder.Tolerance = 0.001;

            builder.Limits.StartExtend.Value.RightHandSide = NumberText(nativeStartAngle);
            builder.Limits.EndExtend.Value.RightHandSide = NumberText(nativeEndAngle);

            if (booleanMode == M3BooleanMode.Create)
                builder.BooleanOperation.Type = NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Create;
            else
            {
                builder.BooleanOperation.Type = booleanMode == M3BooleanMode.Unite
                    ? NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Unite
                    : NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Subtract;
                builder.BooleanOperation.SetTargetBodies(new Body[] { targetBody });
            }

            if (!builder.Limits.Validate())
                throw new InvalidOperationException("NX Revolve limits rejected the requested signed angle interval.");
            feature = builder.CommitFeature();
            if (feature == null) throw new InvalidOperationException("NX RevolveBuilder committed no Feature.");
        }
        finally
        {
            if (builder != null) builder.Destroy();
        }

        RevolveBuilder readback = null;
        try
        {
            readback = part.Features.CreateRevolveBuilder(feature);
            Axis readAxis = readback.Axis;
            if (readAxis == null || readAxis.Point == null || readAxis.Direction == null)
                throw new InvalidOperationException("Committed NX Revolve has no readable point-direction Axis.");

            NXOpen.GeometricUtilities.Extend start = readback.Limits.StartExtend;
            NXOpen.GeometricUtilities.Extend end = readback.Limits.EndExtend;
            if (start == null || start.Value == null || end == null || end.Value == null ||
                start.TrimType != NXOpen.GeometricUtilities.Extend.ExtendType.Value ||
                end.TrimType != NXOpen.GeometricUtilities.Extend.ExtendType.Value)
                throw new InvalidOperationException("Committed NX Revolve angle limits are not readable Value limits.");

            double actualStartAngle = start.Value.Value;
            double actualEndAngle = end.Value.Value;
            RequireFinite(actualStartAngle, "committed native start angle");
            RequireFinite(actualEndAngle, "committed native end angle");
            if (!IsSpanOutsideTolerance(actualEndAngle - actualStartAngle))
                throw new InvalidOperationException("Committed NX Revolve has a degenerate native angle interval.");

            return new M3RevolveNativeResult
            {
                Feature = feature,
                SectionEvidence = sectionEvidence,
                AxisTag = readAxis.Tag.ToString(),
                AxisPointTag = readAxis.Point.Tag.ToString(),
                AxisDirectionTag = readAxis.Direction.Tag.ToString(),
                NativeStartAngleDeg = actualStartAngle,
                NativeEndAngleDeg = actualEndAngle
            };
        }
        finally
        {
            if (readback != null) readback.Destroy();
        }
    }

    private static M3BooleanMode ParseBooleanMode(string mode)
    {
        if (mode == "create") return M3BooleanMode.Create;
        if (mode == "unite") return M3BooleanMode.Unite;
        if (mode == "subtract") return M3BooleanMode.Subtract;
        throw new ArgumentException("mode must be create, unite, or subtract.", "request");
    }

    private static Point3d ReadAxisPoint(double[] values)
    {
        if (values == null || values.Length != 3)
            throw new ArgumentException("AxisPointMm must contain exactly three coordinates.", "request");
        for (int i = 0; i < values.Length; i++)
        {
            RequireFinite(values[i], "axis point coordinate");
            if (values[i] > MaximumAxisCoordinateMm || values[i] < -MaximumAxisCoordinateMm)
                throw new ArgumentOutOfRangeException("request", "Axis point coordinates must be within +/-1000000 mm.");
        }
        return new Point3d(values[0], values[1], values[2]);
    }

    private static Vector3d ReadUnitAxisDirection(double[] values)
    {
        if (values == null || values.Length != 3)
            throw new ArgumentException("AxisDirection must contain exactly three components.", "request");
        for (int i = 0; i < values.Length; i++) RequireFinite(values[i], "axis direction component");

        double scale = Math.Max(Math.Abs(values[0]), Math.Max(Math.Abs(values[1]), Math.Abs(values[2])));
        if (scale == 0.0) throw new ArgumentException("AxisDirection must be nonzero.", "request");
        double x = values[0] / scale;
        double y = values[1] / scale;
        double z = values[2] / scale;
        double length = Math.Sqrt(x * x + y * y + z * z);
        if (length == 0.0 || Double.IsNaN(length) || Double.IsInfinity(length))
            throw new ArgumentException("AxisDirection must be a finite nonzero vector.", "request");
        return new Vector3d(x / length, y / length, z / length);
    }

    private static Curve GetFirstActiveCurve(Sketch sketch)
    {
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new InvalidOperationException("Sketch.GetAllGeometry returned null.");
        UFSession uf = UFSession.GetUFSession();
        if (uf == null) throw new InvalidOperationException("UF session is unavailable for sketch reference-state readback.");
        Curve first = null;
        foreach (NXObject item in geometry)
        {
            if (item == null) throw new InvalidOperationException("Sketch geometry contains null.");
            Curve curve = item as Curve;
            if (curve == null) throw new InvalidOperationException("Revolve source Sketch contains non-curve geometry.");
            UFSket.ReferenceStatus status;
            uf.Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status);
            if (status == UFSket.ReferenceStatus.Reference) continue;
            if (status != UFSket.ReferenceStatus.Active)
                throw new InvalidOperationException("Revolve source curve has an unclassified NX reference state.");
            if (!(curve is Line) && !(curve is Arc) && !(curve is Ellipse) && !(curve is Spline))
                throw new InvalidOperationException("Revolve source Sketch contains an unsupported curve type.");
            if (first == null) first = curve;
        }
        if (first == null) throw new InvalidOperationException("Revolve source Sketch contains no active non-reference curves.");
        return first;
    }

    private static bool SectionDomainMatches(Dictionary<string, object> evidence,
        string expectedInputDomainFingerprint)
    {
        if (evidence == null || String.IsNullOrEmpty(expectedInputDomainFingerprint)) return false;
        object statusValue, inputFingerprintValue, outputFingerprintValue, matchValue;
        if (!evidence.TryGetValue("status", out statusValue) ||
            !String.Equals(Convert.ToString(statusValue, CultureInfo.InvariantCulture), "VERIFIED", StringComparison.Ordinal) ||
            !evidence.TryGetValue("input_geometry_domain_fingerprint", out inputFingerprintValue) ||
            !String.Equals(Convert.ToString(inputFingerprintValue, CultureInfo.InvariantCulture),
                expectedInputDomainFingerprint, StringComparison.Ordinal) ||
            !evidence.TryGetValue("section_output_domain_fingerprint", out outputFingerprintValue) ||
            !evidence.TryGetValue("section_domain_match", out matchValue)) return false;

        Dictionary<string, object> match = matchValue as Dictionary<string, object>;
        if (match == null) return false;
        object matchStatus, matchInputFingerprint, matchOutputFingerprint;
        return match.TryGetValue("status", out matchStatus) &&
            String.Equals(Convert.ToString(matchStatus, CultureInfo.InvariantCulture), "MATCHED", StringComparison.Ordinal) &&
            match.TryGetValue("input_domain_fingerprint", out matchInputFingerprint) &&
            String.Equals(Convert.ToString(matchInputFingerprint, CultureInfo.InvariantCulture),
                expectedInputDomainFingerprint, StringComparison.Ordinal) &&
            match.TryGetValue("output_domain_fingerprint", out matchOutputFingerprint) &&
            String.Equals(Convert.ToString(matchOutputFingerprint, CultureInfo.InvariantCulture),
                Convert.ToString(outputFingerprintValue, CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
            !String.IsNullOrEmpty(Convert.ToString(outputFingerprintValue, CultureInfo.InvariantCulture));
    }

    private static void RequireTargetBody(Part part, Body targetBody)
    {
        if (targetBody == null)
            throw new ArgumentNullException("targetBody", "Unite and Subtract require an explicit target body.");
        Body[] bodies = part.Bodies.ToArray();
        foreach (Body body in bodies)
            if (body != null && body.Tag.Equals(targetBody.Tag)) return;
        throw new ArgumentException("The target body does not belong to the input Part.", "targetBody");
    }

    private static void RequirePart(Part part)
    {
        if (part == null) throw new ArgumentNullException("part");
    }

    private static void RequireAngleEndpoint(double value, string name)
    {
        RequireFinite(value, name);
        if (value < -360.0 || value > 360.0)
            throw new ArgumentOutOfRangeException("request", name + " must be between -360 and 360 degrees.");
    }

    private static bool IsSpanOutsideTolerance(double span)
    {
        return span > MinimumAngleSpanDeg || span < -MinimumAngleSpanDeg;
    }

    private static void RequireFinite(double value, string name)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(name, "Value must be finite.");
    }

    private static string NumberText(double value)
    {
        RequireFinite(value, "value");
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}
