using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;
using NXOpen.GeometricUtilities;
using NXOpen.UF;

// The caller owns the outer undo mark, complete inventory, geometric validation,
// and classification of any failure following mutation.
public static class V2SweepFactory
{
    private const double G0Tolerance = 0.001;
    public sealed class ReadStateUnknownException : Exception
    {
        public ReadStateUnknownException(string message, Exception inner) : base(message, inner) { }
    }

    public sealed class NativeResult
    {
        public Swept Feature;
        public Dictionary<string, object> SectionEvidence;
    }

    private static bool InPart(Part part, NXObject item)
    {
        return part != null && item != null && !item.IsOccurrence && item.OwningPart != null &&
            part.Tag.Equals(item.OwningPart.Tag);
    }

    private static string Id(NXObject item, string label)
    {
        if (item == null || String.IsNullOrEmpty(item.JournalIdentifier))
            throw new InvalidOperationException(label + " has no journal identifier.");
        return item.JournalIdentifier;
    }

    private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }

    private const double EndpointToleranceMm = 1.0e-6;

    private static void CheckInputs(Part part, Sketch sketch, Curve[] path, double[][] directedPointsMm, string fingerprint)
    {
        if (part == null || part.PartUnits != BasePart.Units.Millimeters)
            throw new ArgumentException("A millimeter work part is required.", "part");
        if (!InPart(part, sketch) || !InPart(part, sketch.Feature))
            throw new ArgumentException("A native work-part sketch with a feature is required.", "sketch");
        if (String.IsNullOrEmpty(fingerprint)) throw new ArgumentException("A section domain fingerprint is required.", "fingerprint");
        if (path == null || path.Length == 0) throw new ArgumentException("At least one ordered path curve is required.", "path");
        if (directedPointsMm == null || directedPointsMm.Length != path.Length + 1)
            throw new ArgumentException("An oriented path requires exactly one more point than curves.", "directedPointsMm");
        for (int i = 0; i < directedPointsMm.Length; i++)
        {
            if (directedPointsMm[i] == null || directedPointsMm[i].Length != 3 ||
                !Finite(directedPointsMm[i][0]) || !Finite(directedPointsMm[i][1]) || !Finite(directedPointsMm[i][2]))
                throw new ArgumentException("Oriented path points must have three finite coordinates.", "directedPointsMm");
            if (i > 0 && Close(directedPointsMm[i - 1], directedPointsMm[i]))
                throw new ArgumentException("Oriented path segments must have distinct endpoints.", "directedPointsMm");
        }
        HashSet<Tag> tags = new HashSet<Tag>();
        foreach (Curve curve in path)
        {
            if (!InPart(part, curve) || (!(curve is Line) && !(curve is Arc)))
                throw new ArgumentException("Path must contain native work-part Line or Arc curves only.", "path");
            if (!tags.Add(curve.Tag)) throw new ArgumentException("Path curves must be unique.", "path");
        }
        NXObject[] sketchGeometry = sketch.GetAllGeometry();
        if (sketchGeometry == null) throw new InvalidOperationException("Sketch geometry is unavailable.");
        foreach (NXObject item in sketchGeometry)
            if (item == null || tags.Contains(item.Tag))
                throw new ArgumentException("Guide curves must be distinct from all source sketch geometry.", "path");
    }

    private static bool Close(double[] a, double[] b)
    {
        double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return Finite(dx) && Finite(dy) && Finite(dz) &&
            Math.Abs(dx) <= EndpointToleranceMm && Math.Abs(dy) <= EndpointToleranceMm && Math.Abs(dz) <= EndpointToleranceMm &&
            dx * dx + dy * dy + dz * dz <= EndpointToleranceMm * EndpointToleranceMm;
    }

    private static double[] Coordinates(Point3d p)
    {
        if (!Finite(p.X) || !Finite(p.Y) || !Finite(p.Z)) throw new InvalidOperationException("Invalid Section element endpoint.");
        return new double[] { p.X, p.Y, p.Z };
    }

    private static Curve FirstActiveCurve(Sketch sketch)
    {
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new InvalidOperationException("Sketch geometry is unavailable.");
        UFSession uf = UFSession.GetUFSession();
        if (uf == null) throw new InvalidOperationException("UF session is unavailable.");
        Curve first = null;
        foreach (NXObject item in geometry)
        {
            Curve curve = item as Curve;
            if (curve == null) throw new InvalidOperationException("Sketch contains non-curve geometry.");
            UFSket.ReferenceStatus status;
            uf.Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status);
            if (status == UFSket.ReferenceStatus.Reference) continue;
            if (status != UFSket.ReferenceStatus.Active ||
                (!(curve is Line) && !(curve is Arc) && !(curve is Ellipse) && !(curve is Spline)))
                throw new InvalidOperationException("Sketch contains an unsupported active curve or reference state.");
            if (first == null) first = curve;
        }
        if (first == null) throw new InvalidOperationException("Sketch has no active profile curves.");
        return first;
    }

    private static Point3d HelpPoint(Curve curve)
    {
        UFSession uf = UFSession.GetUFSession();
        if (uf == null) throw new InvalidOperationException("UF session is unavailable.");
        IntPtr evaluator = IntPtr.Zero;
        try
        {
            uf.Eval.Initialize(curve.Tag, out evaluator);
            if (evaluator == IntPtr.Zero) throw new InvalidOperationException("Curve evaluator is unavailable.");
            double[] limits = new double[2];
            uf.Eval.AskLimits(evaluator, limits);
            if (!Finite(limits[0]) || !Finite(limits[1]) || limits[0] == limits[1])
                throw new InvalidOperationException("Invalid path curve parameter limits.");
            double[] point = new double[3];
            uf.Curve.EvaluateCurve(curve.Tag, limits[0] * 0.5 + limits[1] * 0.5, 0, point);
            if (!Finite(point[0]) || !Finite(point[1]) || !Finite(point[2]))
                throw new InvalidOperationException("Invalid path curve point.");
            return new Point3d(point[0], point[1], point[2]);
        }
        finally { if (evaluator != IntPtr.Zero) uf.Eval.Free(evaluator); }
    }

    private static double[][] CurveEndpoints(Curve curve)
    {
        UFSession uf = UFSession.GetUFSession();
        if (uf == null) throw new InvalidOperationException("UF session is unavailable.");
        IntPtr evaluator = IntPtr.Zero;
        try
        {
            uf.Eval.Initialize(curve.Tag, out evaluator);
            if (evaluator == IntPtr.Zero) throw new InvalidOperationException("Curve evaluator is unavailable.");
            double[] limits = new double[2];
            uf.Eval.AskLimits(evaluator, limits);
            if (!Finite(limits[0]) || !Finite(limits[1]) || limits[0] == limits[1])
                throw new InvalidOperationException("Invalid curve limits.");
            double[][] result = new double[][] { new double[3], new double[3] };
            uf.Curve.EvaluateCurve(curve.Tag, limits[0], 0, result[0]);
            uf.Curve.EvaluateCurve(curve.Tag, limits[1], 0, result[1]);
            for (int i = 0; i < 2; i++) for (int j = 0; j < 3; j++)
                if (!Finite(result[i][j])) throw new InvalidOperationException("Invalid curve endpoint.");
            if (Close(result[0], result[1])) throw new InvalidOperationException("Closed or degenerate guide curve is unsupported.");
            return result;
        }
        finally { if (evaluator != IntPtr.Zero) uf.Eval.Free(evaluator); }
    }

    private static Dictionary<string, object> CheckProfile(Part part, Sketch sketch, Section section, string fingerprint)
    {
        SectionData[] data;
        section.GetSectionData(out data);
        if (data == null || data.Length == 0) throw new InvalidOperationException("Profile Section has no data.");
        int count = 0;
        try
        {
            foreach (SectionData entry in data)
            {
                if (entry == null) throw new InvalidOperationException("Null profile Section data.");
                SelectionIntentRule[] rules;
                entry.GetRules(out rules);
                if (rules == null || rules.Length == 0) throw new InvalidOperationException("Profile Section has no rules.");
                foreach (SelectionIntentRule rule in rules)
                {
                    CurveFeatureRule featureRule = rule as CurveFeatureRule;
                    if (featureRule == null) throw new InvalidOperationException("Unsupported profile Section rule.");
                    Feature[] features;
                    featureRule.GetData(out features);
                    if (features == null || features.Length != 1 || features[0] == null ||
                        !features[0].Tag.Equals(sketch.Feature.Tag))
                        throw new InvalidOperationException("Profile rule does not select the whole source sketch feature.");
                    count++;
                }
            }
        }
        finally { foreach (SectionData entry in data) if (entry != null) entry.Dispose(); }
        if (count == 0) throw new InvalidOperationException("Profile feature rule is missing.");
        Dictionary<string, object> evidence = V2SketchProfileAnalyzer.AnalyzeF12SectionDomain(part, sketch, section);
        object status, input, output, matchValue;
        if (evidence == null || !evidence.TryGetValue("status", out status) ||
            !String.Equals(Convert.ToString(status, CultureInfo.InvariantCulture), "VERIFIED", StringComparison.Ordinal) ||
            !evidence.TryGetValue("input_geometry_domain_fingerprint", out input) ||
            !String.Equals(Convert.ToString(input, CultureInfo.InvariantCulture), fingerprint, StringComparison.Ordinal) ||
            !evidence.TryGetValue("section_output_domain_fingerprint", out output) ||
            String.IsNullOrEmpty(Convert.ToString(output, CultureInfo.InvariantCulture)) ||
            !evidence.TryGetValue("section_domain_match", out matchValue))
            throw new InvalidOperationException("Profile Section domain is not verified against the supplied fingerprint.");
        Dictionary<string, object> match = matchValue as Dictionary<string, object>;
        object matchStatus, matchInput, matchOutput;
        if (match == null || !match.TryGetValue("status", out matchStatus) ||
            !String.Equals(Convert.ToString(matchStatus, CultureInfo.InvariantCulture), "MATCHED", StringComparison.Ordinal) ||
            !match.TryGetValue("input_domain_fingerprint", out matchInput) ||
            !String.Equals(Convert.ToString(matchInput, CultureInfo.InvariantCulture), fingerprint, StringComparison.Ordinal) ||
            !match.TryGetValue("output_domain_fingerprint", out matchOutput) ||
            !String.Equals(Convert.ToString(matchOutput, CultureInfo.InvariantCulture), Convert.ToString(output, CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new InvalidOperationException("Profile Section domain does not match.");
        return evidence;
    }

    // Section enumeration may differ from the directed geometric chain order.
    private static Dictionary<string, object> CheckGuide(Section section, Curve[] path, double[][] directedPointsMm)
    {
        SectionData[] data;
        section.GetSectionData(out data);
        if (data == null || data.Length == 0) throw new InvalidOperationException("Guide Section has no data.");
        List<Tag> rulesSeen = new List<Tag>();
        List<Tag> elementsSeen = new List<Tag>();
        List<double[]> starts = new List<double[]>();
        List<double[]> ends = new List<double[]>();
        try
        {
            foreach (SectionData entry in data)
            {
                if (entry == null) throw new InvalidOperationException("Null guide Section data.");
                SelectionIntentRule[] rules;
                entry.GetRules(out rules);
                if (rules == null || rules.Length == 0) throw new InvalidOperationException("Guide Section has no rules.");
                foreach (SelectionIntentRule rule in rules)
                {
                    CurveDumbRule dumb = rule as CurveDumbRule;
                    if (dumb == null) throw new InvalidOperationException("Unsupported guide Section rule.");
                    Curve[] curves;
                    dumb.GetData(out curves);
                    if (curves == null || curves.Length == 0) throw new InvalidOperationException("Empty guide curve rule.");
                    foreach (Curve curve in curves)
                    {
                        if (curve == null) throw new InvalidOperationException("Null guide rule curve.");
                        rulesSeen.Add(curve.Tag);
                    }
                }
                SectionElementData[] elements;
                entry.GetSectionElementsData(out elements);
                if (elements == null || elements.Length == 0) throw new InvalidOperationException("Guide Section has no elements.");
                try
                {
                    foreach (SectionElementData element in elements)
                    {
                        if (element == null) throw new InvalidOperationException("Null guide element.");
                        DisplayableObject selected, start, end;
                        Point3d startPoint, endPoint;
                        element.GetSectionElementData1(out selected, out start, out startPoint, out end, out endPoint);
                        Curve curve = selected as Curve;
                        if (curve == null) throw new InvalidOperationException("Guide selected element is not a curve.");
                        elementsSeen.Add(curve.Tag);
                        starts.Add(Coordinates(startPoint));
                        ends.Add(Coordinates(endPoint));
                    }
                }
                finally { foreach (SectionElementData element in elements) if (element != null) element.Dispose(); }
            }
        }
        finally { foreach (SectionData entry in data) if (entry != null) entry.Dispose(); }
        if (rulesSeen.Count != path.Length || elementsSeen.Count != path.Length)
            throw new InvalidOperationException("Guide rule or selected element count differs from the requested path.");
        string[] ids = new string[path.Length];
        double[][] orderedStarts = new double[path.Length][];
        double[][] orderedEnds = new double[path.Length][];
        int[] enumerationIndices = new int[path.Length];
        HashSet<Tag> ruleSet = new HashSet<Tag>(rulesSeen);
        HashSet<Tag> elementSet = new HashSet<Tag>(elementsSeen);
        if (ruleSet.Count != path.Length || elementSet.Count != path.Length)
            throw new InvalidOperationException("Guide rules or elements contain duplicate curves.");
        for (int i = 0; i < path.Length; i++)
        {
            int index = elementsSeen.FindIndex(delegate(Tag tag) { return tag.Equals(path[i].Tag); });
            if (!ruleSet.Contains(path[i].Tag) || index < 0)
                throw new InvalidOperationException("Guide rules or selected elements differ from the requested path.");
            orderedStarts[i] = starts[index];
            orderedEnds[i] = ends[index];
            enumerationIndices[i] = index;
            if (!Close(orderedStarts[i], directedPointsMm[i]) || !Close(orderedEnds[i], directedPointsMm[i + 1]) ||
                (i > 0 && !Close(orderedEnds[i - 1], orderedStarts[i])))
                throw new InvalidOperationException("Guide element direction or continuity differs from the requested oriented path.");
            ids[i] = Id(path[i], "path curve");
        }
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "path_curve_ids", ids }, { "element_start_points_mm", orderedStarts },
            { "element_end_points_mm", orderedEnds }, { "element_enumeration_indices", enumerationIndices },
            { "endpoint_tolerance_mm", EndpointToleranceMm }
        };
    }

    public static NativeResult Create(Part part, Sketch sketch, Curve[] path, double[][] directedPointsMm, string profileOutput,
        string orientationMode, double twistAngleDeg, string expectedInputDomainFingerprint)
    {
        CheckInputs(part, sketch, path, directedPointsMm, expectedInputDomainFingerprint);
        if (profileOutput != "solid" && profileOutput != "sheet") throw new ArgumentException("Output must be solid or sheet.", "profileOutput");
        if (orientationMode != "transport" && orientationMode != "linear_twist") throw new ArgumentException("Unsupported orientation mode.", "orientationMode");
        if (!Finite(twistAngleDeg) || twistAngleDeg < -90.0 || twistAngleDeg > 90.0 ||
            (orientationMode == "transport" && twistAngleDeg != 0.0))
            throw new ArgumentException("Twist must be within [-90, 90] degrees and zero for transport orientation.", "twistAngleDeg");

        Section profile = part.Sections.CreateSection(0.0095, 0.01, 0.5);
        profile.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
        profile.SetAllowRefCrvs(false);
        CurveFeatureRule profileRule = part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { sketch.Feature });
        profile.AddToSection(new SelectionIntentRule[] { profileRule }, FirstActiveCurve(sketch), null, null,
            sketch.Origin, Section.Mode.Create, true);
        Dictionary<string, object> evidence = CheckProfile(part, sketch, profile, expectedInputDomainFingerprint);

        Section guide = part.Sections.CreateSection(0.0095, 0.01, 0.5);
        guide.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
        guide.SetAllowRefCrvs(false);
        CurveDumbRule pathRule = part.ScRuleFactory.CreateRuleCurveDumb(path);
        guide.AddToSection(new SelectionIntentRule[] { pathRule }, path[0], null, null,
            HelpPoint(path[0]), Section.Mode.Create, false);
        CheckGuide(guide, path, directedPointsMm);
        SweptBuilder builder = null;
        try
        {
            builder = part.Features.CreateSweptBuilder(null);
            if (builder == null) throw new InvalidOperationException("NX did not create the Swept builder.");
            builder.SectionList.Append(profile);
            builder.GuideList.Append(guide);
            builder.G0Tolerance = G0Tolerance;
            builder.G1Tolerance = 0.5;
            builder.BodyPreference.BodyType = profileOutput == "solid" ? FeatureOptions.BodyStyle.Solid : FeatureOptions.BodyStyle.Sheet;
            // These restrictions define the small supported subset and are not
            // evidence that NX has produced the intended geometry.
            builder.AlignmentMethod.AlignType = AlignmentMethodBuilder.Type.Parameter;
            builder.ScalingMethod.ScalingOption = ScalingMethodBuilder.ScalingOptions.Constant;
            builder.ScalingMethod.ScaleFactor = 1.0;
            builder.SectionRebuildData.RebuildType = Rebuild.RebuildTypes.None;
            builder.GuideRebuildData.RebuildType = Rebuild.RebuildTypes.None;
            builder.SectionLocation = SweptBuilder.SectionLocationTypes.AnywhereAlongGuides;
            builder.InterpolationOption = SweptBuilder.InterpolationOptions.Linear;
            builder.PreserveShapeOption = true;
            builder.PreserveGuideShapeOption = false;
            ConfigureOrientation(builder, orientationMode, twistAngleDeg);
            if (!builder.Validate()) throw new InvalidOperationException("NX rejected the Swept definition.");
            Swept feature = builder.CommitFeature() as Swept;
            if (feature == null) throw new InvalidOperationException("NX did not return a Swept feature.");
            return new NativeResult { Feature = feature, SectionEvidence = evidence };
        }
        finally
        {
            if (builder != null) try { builder.Destroy(); }
            catch (Exception ex) { throw new ReadStateUnknownException("Swept builder destruction failed.", ex); }
        }
    }

    // Native direction and angular law behavior must be validated against NX geometry.
    private static void ConfigureOrientation(SweptBuilder builder, string mode, double angleDeg)
    {
        if (mode == "transport")
            builder.OrientationMethod.OrientationOption = OrientationMethodBuilder.OrientationOptions.Fixed;
        else
        {
            builder.OrientationMethod.OrientationOption = OrientationMethodBuilder.OrientationOptions.ByAngularLaw;
            builder.OrientationMethod.AngularLaw.LawType = LawBuilder.Type.Linear;
            builder.OrientationMethod.AngularLaw.StartValue.RightHandSide = "0";
            builder.OrientationMethod.AngularLaw.EndValue.RightHandSide = (-angleDeg).ToString("R", CultureInfo.InvariantCulture);
        }
    }

    private static Dictionary<string, object> ReadMaterialized(Part part, Swept feature, Sketch sketch, Curve[] path, double[][] directedPointsMm, string fingerprint)
    {
        CheckInputs(part, sketch, path, directedPointsMm, fingerprint);
        if (!InPart(part, feature) || !String.Equals(feature.FeatureType, "SKIN", StringComparison.Ordinal))
            throw new InvalidOperationException("A native work-part Swept SKIN feature is required.");
        SweptBuilder builder = null;
        try
        {
            builder = part.Features.CreateSweptBuilder(feature);
            if (builder == null) throw new InvalidOperationException("NX did not open the Swept builder.");
            if (builder.AlignmentMethod.AlignType != AlignmentMethodBuilder.Type.Parameter ||
                builder.ScalingMethod.ScalingOption != ScalingMethodBuilder.ScalingOptions.Constant ||
                !Finite(builder.ScalingMethod.ScaleFactor) || Math.Abs(builder.ScalingMethod.ScaleFactor - 1.0) > 1e-12 ||
                builder.SectionRebuildData.RebuildType != Rebuild.RebuildTypes.None ||
                builder.GuideRebuildData.RebuildType != Rebuild.RebuildTypes.None ||
                builder.SectionLocation != SweptBuilder.SectionLocationTypes.AnywhereAlongGuides ||
                builder.InterpolationOption != SweptBuilder.InterpolationOptions.Linear ||
                !builder.PreserveShapeOption || builder.PreserveGuideShapeOption ||
                !Finite(builder.G0Tolerance) || Math.Abs(builder.G0Tolerance - G0Tolerance) > 1e-12 ||
                !Finite(builder.G1Tolerance) || Math.Abs(builder.G1Tolerance - 0.5) > 1e-12)
                throw new InvalidOperationException("Unsupported Swept alignment, scaling, rebuild, interpolation, preservation, or tolerance state.");
            Section[] profiles = builder.SectionList.GetContents();
            Section[] guides = builder.GuideList.GetContents();
            if (profiles == null || profiles.Length != 1 || profiles[0] == null || guides == null || guides.Length != 1 || guides[0] == null)
                throw new InvalidOperationException("Only one profile and one guide Section are supported.");
            Dictionary<string, object> sectionEvidence = CheckProfile(part, sketch, profiles[0], fingerprint);
            Dictionary<string, object> guideEvidence = CheckGuide(guides[0], path, directedPointsMm);
            SectionData[] spineData;
            builder.Spine.GetSectionData(out spineData);
            if (spineData == null) throw new InvalidOperationException("Spine Section state is unavailable.");
            try
            {
                if (spineData.Length != 0) throw new InvalidOperationException("Additional Spine geometry is unsupported.");
            }
            finally { foreach (SectionData entry in spineData) if (entry != null) entry.Dispose(); }
            Feature[] parents = feature.GetParents();
            if (parents == null || parents.Length != 1)
                throw new InvalidOperationException("Swept must have exactly one source sketch parent and no additional feature parents.");
            List<string> parentIds = new List<string>();
            HashSet<Tag> parentTags = new HashSet<Tag>();
            int sketchParents = 0;
            foreach (Feature parent in parents)
            {
                if (!InPart(part, parent) || !parentTags.Add(parent.Tag))
                    throw new InvalidOperationException("Swept has a null, external, or duplicate parent feature.");
                parentIds.Add(Id(parent, "Swept parent"));
                if (parent.Tag.Equals(sketch.Feature.Tag)) sketchParents++;
            }
            if (sketchParents != 1) throw new InvalidOperationException("Swept does not have exactly one source sketch feature parent.");
            Body[] bodies = feature.GetBodies();
            if (bodies == null || bodies.Length != 1 || !InPart(part, bodies[0]))
                throw new InvalidOperationException("Swept must have exactly one native work-part body.");
            FeatureOptions.BodyStyle style = builder.BodyPreference.BodyType;
            if ((style == FeatureOptions.BodyStyle.Solid && (!bodies[0].IsSolidBody || bodies[0].IsSheetBody)) ||
                (style == FeatureOptions.BodyStyle.Sheet && (!bodies[0].IsSheetBody || bodies[0].IsSolidBody)) ||
                (style != FeatureOptions.BodyStyle.Solid && style != FeatureOptions.BodyStyle.Sheet))
                throw new InvalidOperationException("Swept output body does not match its body preference.");
            string orientation;
            object start = null, end = null, startFormula = null, endFormula = null, startUnit = null, endUnit = null;
            if (builder.OrientationMethod.OrientationOption == OrientationMethodBuilder.OrientationOptions.Fixed) orientation = "transport";
            else if (builder.OrientationMethod.OrientationOption == OrientationMethodBuilder.OrientationOptions.ByAngularLaw &&
                builder.OrientationMethod.AngularLaw.LawType == LawBuilder.Type.Linear)
            {
                orientation = "linear_twist";
                Expression startExpression = builder.OrientationMethod.AngularLaw.StartValue;
                Expression endExpression = builder.OrientationMethod.AngularLaw.EndValue;
                if (startExpression == null || endExpression == null || !Finite(startExpression.Value) || !Finite(endExpression.Value))
                    throw new InvalidOperationException("Angular law values are unavailable.");
                if (startExpression.Units == null || endExpression.Units == null ||
                    !String.Equals(startExpression.Units.Name, "Degrees", StringComparison.Ordinal) ||
                    !String.Equals(endExpression.Units.Name, "Degrees", StringComparison.Ordinal))
                    throw new InvalidOperationException("Angular law expressions must both use the observed Degrees unit.");
                double startLiteral, endLiteral;
                if (builder.OrientationMethod.AngularLaw.ReverseDirection ||
                    !Double.TryParse(startExpression.RightHandSide, NumberStyles.Float, CultureInfo.InvariantCulture, out startLiteral) ||
                    !Double.TryParse(endExpression.RightHandSide, NumberStyles.Float, CultureInfo.InvariantCulture, out endLiteral) ||
                    !Finite(startLiteral) || !Finite(endLiteral) || Math.Abs(startLiteral) > 1e-12 ||
                    Math.Abs(startExpression.Value - startLiteral) > 1e-9 || Math.Abs(endExpression.Value - endLiteral) > 1e-9 ||
                    endLiteral < -90.0 || endLiteral > 90.0)
                    throw new InvalidOperationException("Unsupported angular law direction or nonliteral angle expressions.");
                start = startExpression.Value; end = endExpression.Value;
                startFormula = startExpression.RightHandSide; endFormula = endExpression.RightHandSide;
                startUnit = startExpression.Units == null ? null : startExpression.Units.Name;
                endUnit = endExpression.Units == null ? null : endExpression.Units.Name;
            }
            else throw new InvalidOperationException("Unsupported Swept orientation or angular law.");
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "feature_id", Id(feature, "Swept feature") }, { "feature_tag", feature.Tag.ToString() },
                { "feature_type", feature.FeatureType }, { "sketch_feature_id", Id(sketch.Feature, "sketch feature") },
                { "path_curve_ids", guideEvidence["path_curve_ids"] }, { "guide_evidence", guideEvidence },
                { "parent_feature_ids", parentIds.ToArray() }, { "section_evidence", sectionEvidence },
                { "body_id", Id(bodies[0], "Swept body") }, { "body_tag", bodies[0].Tag.ToString() },
                { "profile_output", style == FeatureOptions.BodyStyle.Solid ? "solid" : "sheet" },
                { "orientation_mode", orientation }, { "law_start_value", start }, { "law_end_value", end },
                { "native_orientation_option", builder.OrientationMethod.OrientationOption.ToString() },
                { "native_law_type", builder.OrientationMethod.AngularLaw.LawType.ToString() },
                { "law_start_formula", startFormula }, { "law_end_formula", endFormula },
                { "law_start_unit", startUnit }, { "law_end_unit", endUnit },
                { "native_end_deg", end }, { "requested_twist_deg", end == null ? (object)0.0 : -(double)end },
                { "alignment_type", builder.AlignmentMethod.AlignType.ToString() },
                { "scaling_option", builder.ScalingMethod.ScalingOption.ToString() },
                { "scale_factor", builder.ScalingMethod.ScaleFactor },
                { "section_location", builder.SectionLocation.ToString() },
                { "interpolation_option", builder.InterpolationOption.ToString() },
                { "g0_tolerance", builder.G0Tolerance }, { "g1_tolerance", builder.G1Tolerance },
                { "preserve_shape", builder.PreserveShapeOption }, { "preserve_guide_shape", builder.PreserveGuideShapeOption }
            };
        }
        finally
        {
            if (builder != null) try { builder.Destroy(); }
            catch (Exception ex) { throw new ReadStateUnknownException("Swept reader builder destruction failed.", ex); }
        }
    }

    // Discover references only inside the same isolated read. No NX object from
    // this process may escape the undo mark or be reused across sessions.
    private static Dictionary<string, object> DiscoverMaterialized(Part part, Swept feature)
    {
        if (!InPart(part, feature) || !String.Equals(feature.FeatureType, "SKIN", StringComparison.Ordinal))
            throw new InvalidOperationException("A native work-part Swept SKIN feature is required.");
        SweptBuilder builder = null;
        Sketch sketch = null;
        Curve[] orderedPath = null;
        double[][] points = null;
        string fingerprint = null;
        try
        {
            builder = part.Features.CreateSweptBuilder(feature);
            if (builder == null) throw new InvalidOperationException("NX did not open the Swept builder.");
            Section[] profiles = builder.SectionList.GetContents(), guides = builder.GuideList.GetContents();
            if (profiles == null || profiles.Length != 1 || profiles[0] == null ||
                guides == null || guides.Length != 1 || guides[0] == null)
                throw new InvalidOperationException("Only one profile and one guide Section are supported.");
            Feature sketchFeature = null;
            SectionData[] profileData;
            profiles[0].GetSectionData(out profileData);
            if (profileData == null || profileData.Length == 0) throw new InvalidOperationException("Profile Section has no data.");
            try
            {
                foreach (SectionData entry in profileData)
                {
                    if (entry == null) throw new InvalidOperationException("Null profile Section data.");
                    SelectionIntentRule[] rules;
                    entry.GetRules(out rules);
                    if (rules == null || rules.Length == 0) throw new InvalidOperationException("Profile rules are unavailable.");
                    foreach (SelectionIntentRule rule in rules)
                    {
                        CurveFeatureRule source = rule as CurveFeatureRule;
                        if (source == null) throw new InvalidOperationException("Unsupported profile rule.");
                        Feature[] features;
                        source.GetData(out features);
                        if (features == null || features.Length != 1 || !InPart(part, features[0]))
                            throw new InvalidOperationException("Profile must reference exactly one native feature.");
                        if (sketchFeature != null && !sketchFeature.Tag.Equals(features[0].Tag))
                            throw new InvalidOperationException("Multiple profile feature sources are unsupported.");
                        sketchFeature = features[0];
                    }
                }
            }
            finally { foreach (SectionData entry in profileData) if (entry != null) entry.Dispose(); }
            if (sketchFeature == null) throw new InvalidOperationException("No source sketch feature.");
            Sketch[] sketches = part.Sketches.ToArray();
            if (sketches == null) throw new InvalidOperationException("Part sketches are unavailable.");
            foreach (Sketch candidate in sketches)
            {
                if (candidate != null && candidate.Feature != null && candidate.Feature.Tag.Equals(sketchFeature.Tag))
                {
                    if (sketch != null || !InPart(part, candidate)) throw new InvalidOperationException("Ambiguous sketch feature source.");
                    sketch = candidate;
                }
            }
            if (sketch == null) throw new InvalidOperationException("Profile feature has no unique native sketch.");
            Dictionary<string, object> analysis = V2SketchProfileAnalyzer.AnalyzeF12SectionDomain(part, sketch, profiles[0]);
            object value;
            if (analysis == null || !analysis.TryGetValue("input_geometry_domain_fingerprint", out value))
                throw new InvalidOperationException("Profile domain fingerprint is unavailable.");
            fingerprint = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (String.IsNullOrEmpty(fingerprint)) throw new InvalidOperationException("Profile domain fingerprint is empty.");
            CheckProfile(part, sketch, profiles[0], fingerprint);

            List<Curve> candidates = new List<Curve>();
            HashSet<Tag> seen = new HashSet<Tag>();
            SectionData[] guideData;
            guides[0].GetSectionData(out guideData);
            if (guideData == null || guideData.Length == 0) throw new InvalidOperationException("Guide Section has no data.");
            try
            {
                foreach (SectionData entry in guideData)
                {
                    if (entry == null) throw new InvalidOperationException("Null guide Section data.");
                    SelectionIntentRule[] rules;
                    entry.GetRules(out rules);
                    if (rules == null || rules.Length == 0) throw new InvalidOperationException("Guide rules are unavailable.");
                    foreach (SelectionIntentRule rule in rules)
                    {
                        CurveDumbRule source = rule as CurveDumbRule;
                        if (source == null) throw new InvalidOperationException("Unsupported guide rule.");
                        Curve[] curves;
                        source.GetData(out curves);
                        if (curves == null || curves.Length == 0) throw new InvalidOperationException("Empty guide rule.");
                        foreach (Curve curve in curves)
                        {
                            if (!InPart(part, curve) || (!(curve is Line) && !(curve is Arc)) || !seen.Add(curve.Tag))
                                throw new InvalidOperationException("Unsupported or repeated guide curve.");
                            candidates.Add(curve);
                        }
                    }
                }
            }
            finally { foreach (SectionData entry in guideData) if (entry != null) entry.Dispose(); }
            if (candidates.Count == 0) throw new InvalidOperationException("Guide has no curves.");
            List<double[][]> endpoints = new List<double[][]>();
            foreach (Curve curve in candidates) endpoints.Add(CurveEndpoints(curve));
            orderedPath = new Curve[candidates.Count];
            points = new double[candidates.Count + 1][];
            points[0] = Coordinates(sketch.Origin);
            bool[] used = new bool[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                int found = -1, connectedEnd = -1;
                for (int j = 0; j < candidates.Count; j++)
                {
                    if (used[j]) continue;
                    for (int k = 0; k < 2; k++)
                    {
                        if (!Close(points[i], endpoints[j][k])) continue;
                        if (found >= 0) throw new InvalidOperationException("Guide connectivity is ambiguous or branched.");
                        found = j; connectedEnd = k;
                    }
                }
                if (found < 0) throw new InvalidOperationException("Guide does not form a connected chain from the sketch origin.");
                used[found] = true;
                orderedPath[i] = candidates[found];
                points[i + 1] = endpoints[found][1 - connectedEnd];
                if (Close(points[i + 1], points[0]))
                    throw new InvalidOperationException("Closed guide chains are unsupported.");
            }
        }
        finally
        {
            if (builder != null) try { builder.Destroy(); }
            catch (Exception ex) { throw new ReadStateUnknownException("Swept discovery builder destruction failed.", ex); }
        }
        return ReadMaterialized(part, feature, sketch, orderedPath, points, fingerprint);
    }

    // The inventory must deterministically cover all relevant part objects and values.
    public static Dictionary<string, object> ReadIsolated(Session session, Part part, Swept feature,
        Sketch sketch, Curve[] path, double[][] directedPointsMm, string expectedInputDomainFingerprint, Func<string> inventory)
    {
        return ReadIsolatedCore(session, part, feature, inventory,
            delegate { return ReadMaterialized(part, feature, sketch, path, directedPointsMm, expectedInputDomainFingerprint); });
    }

    public static Dictionary<string, object> ReadDiscoveredIsolated(Session session, Part part, Swept feature, Func<string> inventory)
    {
        return ReadIsolatedCore(session, part, feature, inventory,
            delegate { return DiscoverMaterialized(part, feature); });
    }

    private static Dictionary<string, object> ReadIsolatedCore(Session session, Part part, Swept feature,
        Func<string> inventory, Func<Dictionary<string, object>> reader)
    {
        if (session == null || part == null || feature == null || inventory == null)
            throw new ArgumentException("Swept reader requires session, part, feature and inventory.");
        bool dirty = part.IsModified;
        string before;
        try
        {
            before = inventory();
            if (before == null || part.IsModified != dirty) throw new InvalidOperationException("Initial inventory is missing or changed dirty state.");
        }
        catch (Exception ex) { throw new ReadStateUnknownException("Swept reader could not establish an initial inventory.", ex); }
        Session.UndoMarkId mark;
        try { mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F21 isolated Swept reader"); }
        catch (Exception ex) { throw new ReadStateUnknownException("Swept reader could not establish an undo mark.", ex); }
        Dictionary<string, object> result = null;
        Exception readError = null;
        try { result = reader(); }
        catch (Exception ex) { readError = ex; }
        try
        {
            session.UndoToMarkWithStatus(mark, "F21 isolated Swept reader");
            int update = session.UpdateManager.DoUpdate(mark);
            if (update != 0) throw new InvalidOperationException("NX update returned " + update.ToString(CultureInfo.InvariantCulture));
            session.DeleteUndoMark(mark, "F21 isolated Swept reader");
            if (part.IsModified != dirty) throw new InvalidOperationException("Swept read changed dirty state.");
            string after = inventory();
            if (part.IsModified != dirty || !String.Equals(before, after, StringComparison.Ordinal))
                throw new InvalidOperationException("Swept read did not restore the captured part state.");
        }
        catch (Exception ex) { throw new ReadStateUnknownException("Swept reader could not prove state restoration.", ex); }
        if (readError is ReadStateUnknownException) throw readError;
        if (readError != null)
            throw new InvalidOperationException("Native Swept is not supported by the reader (" + readError.GetType().Name + ": " + readError.Message + ").", readError);
        return result;
    }
}
