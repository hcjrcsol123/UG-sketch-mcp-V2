using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

// These NX2506 properties are the exact interfaces exercised by the F14 native probe.
#pragma warning disable 0618

public sealed class V2HoleBuilderCleanupException : Exception
{
    public V2HoleBuilderCleanupException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Creates and reads NX HolePackage features. Transaction ownership remains with the caller.</summary>
public static class V2HoleFactory
{
    public static HolePackage Create(Part part, M3HoleRequest request, Body target)
    {
        string sourcePointTag;
        return Create(part, request, target, out sourcePointTag);
    }

    public static HolePackage Create(Part part, M3HoleRequest request, Body target, out string sourcePointTag)
    {
        sourcePointTag = null;
        if (part == null) throw new ArgumentNullException("part");
        if (request == null) throw new ArgumentNullException("request");
        if (target == null || target.OwningPart == null || !target.OwningPart.Tag.Equals(part.Tag) || !target.IsSolidBody)
            throw new ArgumentException("A solid target body in the supplied part is required.", "target");
        if (part.PartUnits != BasePart.Units.Millimeters) throw new ArgumentException("A millimetre part is required.", "part");
        Point3d position = Point(request.PositionMm);
        Vector3d direction = Vector(request.Direction);
        bool threaded = request.Kind == "threaded";
        if (!threaded && request.Kind != "simple" && request.Kind != "counterbore" && request.Kind != "countersink") throw new ArgumentException("Unsupported hole kind.");
        if (request.DepthMode != "blind" && request.DepthMode != "through") throw new ArgumentException("Unsupported hole depth mode.");
        if (request.DepthMode == "blind") Positive(request.HoleDepthMm, "hole depth");
        if (threaded)
        {
            if (String.IsNullOrWhiteSpace(request.ThreadStandard) || String.IsNullOrWhiteSpace(request.ThreadSize) || String.IsNullOrWhiteSpace(request.RadialEngage)) throw new ArgumentException("Thread standard, size, and radial engagement are required.");
            if (request.Hand != "right" && request.Hand != "left") throw new ArgumentException("Thread hand must be right or left.");
            Positive(request.PitchMm, "pitch"); Positive(request.ThreadDepthMm, "thread depth");
        }
        else Positive(request.DiameterMm, "diameter");
        if (request.Kind == "counterbore") { Positive(request.CounterboreDiameterMm, "counterbore diameter"); Positive(request.CounterboreDepthMm, "counterbore depth"); }
        if (request.Kind == "countersink") { Positive(request.CountersinkDiameterMm, "countersink diameter"); Positive(request.CountersinkAngleDeg, "countersink angle"); }
        HolePackageBuilder builder = null;
        try
        {
            builder = part.Features.CreateHolePackageBuilder(null);
            builder.Tolerance = 0.001;
            builder.HolePosition.DistanceTolerance = 0.01;
            builder.HolePosition.ChainingTolerance = 0.0095;
            builder.HolePosition.AngleTolerance = 0.5;
            builder.BooleanOperation.Type = NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Subtract;
            builder.BooleanOperation.SetTargetBodies(new Body[] { target });
            builder.ProjectPointOntoTargetEnabled = false;
            builder.HolePosition.SetAllowedEntityTypes(Section.AllowTypes.OnlyPoints);
            NXOpen.Point point = part.Points.CreatePoint(position);
            sourcePointTag = point.Tag.ToString();
            var rule = part.ScRuleFactory.CreateRuleCurveDumbFromPoints(new NXOpen.Point[] { point });
            builder.HolePosition.AddToSection(new SelectionIntentRule[] { rule }, point, null, null, position, Section.Mode.Create, false);
            NXOpen.Direction nativeDirection = part.Directions.CreateDirection(position, direction, SmartObject.UpdateOption.WithinModeling);
            builder.ProjectionDirection.ProjectDirectionMethod = NXOpen.GeometricUtilities.ProjectionOptions.DirectionType.Vector;
            builder.ProjectionDirection.ProjectVector = nativeDirection;
            builder.DepthOption = HolePackageBuilder.HoleDepthOptions.ToCylinderBottom;
            builder.HoleDepthLimitOption = request.DepthMode == "blind" ? HolePackageBuilder.HoleDepthLimitOptions.Value : HolePackageBuilder.HoleDepthLimitOptions.ThroughBody;
            if (threaded)
            {
                builder.Type = HolePackageBuilder.Types.ThreadedHole;
                builder.ThreadStandard = request.ThreadStandard;
                builder.ThreadSize = request.ThreadSize;
                builder.RadialEngageOption = request.RadialEngage;
                builder.ThreadRotation = request.Hand == "right" ? HolePackageBuilder.ThreadRotationOptions.Right : HolePackageBuilder.ThreadRotationOptions.Left;
                builder.ThreadLengthOption = HolePackageBuilder.ThreadLengthOptions.Custom;
                builder.RelateHoleDepthToThreadDepth = false;
                Unit millimeter = part.UnitCollection.FindObject("MilliMeter");
                if (millimeter == null) throw new InvalidOperationException("NX millimetre unit is unavailable.");
                builder.ThreadDepth.SetNumberValueWithUnits(request.ThreadDepthMm, millimeter);
                if (request.DepthMode == "blind") builder.ThreadedHoleDepth.SetNumberValueWithUnits(request.HoleDepthMm, millimeter);
                builder.ThreadedTipAngle.RightHandSide = "118";
                builder.ThreadedStartChamferEnabled = false;
                builder.ThreadedEndChamferEnabled = false;
                builder.ThreadedReliefEnabled = false;
                builder.ReliefChamferEnabled = false;
                builder.ThreadAtBothEnds = false;
            }
            else
            {
                builder.Type = HolePackageBuilder.Types.GeneralHole;
                builder.GeneralHoleForm = request.Kind == "simple" ? HolePackageBuilder.HoleForms.Simple : request.Kind == "counterbore" ? HolePackageBuilder.HoleForms.Counterbored : HolePackageBuilder.HoleForms.Countersink;
                Expression dia = request.Kind == "simple" ? builder.GeneralSimpleHoleDiameter : request.Kind == "counterbore" ? builder.GeneralCounterboreHoleDiameter : builder.GeneralCountersinkHoleDiameter;
                Expression depth = request.Kind == "simple" ? builder.GeneralSimpleHoleDepth : request.Kind == "counterbore" ? builder.GeneralCounterboreHoleDepth : builder.GeneralCountersinkHoleDepth;
                dia.RightHandSide = Number(request.DiameterMm);
                if (request.DepthMode == "blind") depth.RightHandSide = Number(request.HoleDepthMm);
                builder.GeneralTipAngle.RightHandSide = "118";
                if (request.Kind == "counterbore") { builder.GeneralCounterboreDiameter.RightHandSide = Number(request.CounterboreDiameterMm); builder.GeneralCounterboreDepth.RightHandSide = Number(request.CounterboreDepthMm); }
                if (request.Kind == "countersink") { builder.GeneralCountersinkDiameter.RightHandSide = Number(request.CountersinkDiameterMm); builder.GeneralCountersinkAngle.RightHandSide = Number(request.CountersinkAngleDeg); }
            }
            if (!builder.Validate()) throw new InvalidOperationException("NX rejected the hole definition.");
            HolePackage result = builder.CommitFeature() as HolePackage;
            if (result == null) throw new InvalidOperationException("NX did not return a HolePackage.");
            return result;
        }
        finally { if (builder != null) Destroy(builder); }
    }

    public static Dictionary<string, object> Read(Part part, HolePackage feature)
    {
        if (part == null) throw new ArgumentNullException("part");
        if (feature == null || feature.OwningPart == null || !feature.OwningPart.Tag.Equals(part.Tag)) throw new ArgumentException("HolePackage must belong to the supplied part.", "feature");
        HolePackageBuilder b = null;
        try
        {
            b = part.Features.CreateHolePackageBuilder(feature);
            Point3d[] origins; Vector3d[] directions;
            feature.GetOrigins(out origins); feature.GetDirections(out directions);
            if (origins == null || origins.Length != 1 || directions == null || directions.Length != 1) throw new InvalidOperationException("Expected exactly one native hole position and direction.");
            bool threaded = b.Type == HolePackageBuilder.Types.ThreadedHole;
            string kind;
            if (threaded) kind = "threaded";
            else if (b.Type != HolePackageBuilder.Types.GeneralHole) throw new InvalidOperationException("Unexpected native hole type.");
            else if (b.GeneralHoleForm == HolePackageBuilder.HoleForms.Simple) kind = "simple";
            else if (b.GeneralHoleForm == HolePackageBuilder.HoleForms.Counterbored) kind = "counterbore";
            else if (b.GeneralHoleForm == HolePackageBuilder.HoleForms.Countersink) kind = "countersink";
            else throw new InvalidOperationException("Unexpected native hole form.");
            string mode = b.HoleDepthLimitOption == HolePackageBuilder.HoleDepthLimitOptions.Value ? "blind" : b.HoleDepthLimitOption == HolePackageBuilder.HoleDepthLimitOptions.ThroughBody ? "through" : null;
            if (mode == null) throw new InvalidOperationException("Unexpected native hole depth limit.");
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["feature_id"] = feature.JournalIdentifier;
            d["feature_tag"] = feature.Tag.ToString();
            d["kind"] = kind; d["depth_mode"] = mode;
            d["position_mm"] = P(origins[0]); d["direction"] = V(directions[0]);
            d["native_type"] = b.Type.ToString(); d["native_form"] = b.GeneralHoleForm.ToString();
            d["native_depth_option"] = b.DepthOption.ToString(); d["native_depth_limit"] = b.HoleDepthLimitOption.ToString();
            d["tolerance"] = b.Tolerance; d["section_distance_tolerance"] = b.HolePosition.DistanceTolerance; d["section_chaining_tolerance"] = b.HolePosition.ChainingTolerance; d["section_angle_tolerance"] = b.HolePosition.AngleTolerance;
            d["section_id"] = b.HolePosition.JournalIdentifier; d["section_tag"] = b.HolePosition.Tag.ToString();
            d["project_point_onto_target"] = b.ProjectPointOntoTargetEnabled;
            d["direction_method"] = b.ProjectionDirection.ProjectDirectionMethod.ToString();
            NXOpen.Direction pd = b.ProjectionDirection.ProjectVector;
            if (pd == null) throw new InvalidOperationException("Missing native projection direction.");
            d["projection_direction"] = V(pd.Vector); d["projection_direction_origin_mm"] = P(pd.Origin); d["direction_id"] = pd.JournalIdentifier; d["direction_tag"] = pd.Tag.ToString();
            d["boolean_type"] = b.BooleanOperation.Type.ToString();
            Body[] bodies = b.BooleanOperation.GetTargetBodies();
            if (bodies == null || bodies.Length != 1 || bodies[0] == null) throw new InvalidOperationException("Expected exactly one native boolean target.");
            d["target_body_ids"] = new string[] { bodies[0].JournalIdentifier }; d["target_body_tags"] = new string[] { bodies[0].Tag.ToString() };
            NXObject[] outputs; b.HolePosition.EvaluateAndAskOutputEntities(out outputs);
            if (outputs == null || outputs.Length != 1 || !(outputs[0] is NXOpen.Point)) throw new InvalidOperationException("Expected exactly one native section point.");
            d["point_id"] = outputs[0].JournalIdentifier; d["point_tag"] = outputs[0].Tag.ToString();
            d["point_coordinates_mm"] = P(((NXOpen.Point)outputs[0]).Coordinates);
            SelectionIntentRule[] rules; int[] ruleIndices;
            TaggedObject[] collected = b.HolePosition.AskRulesAndCollectedEntities(out rules, out ruleIndices);
            List<string> ruleTypes = new List<string>(); foreach (SelectionIntentRule rule in rules) ruleTypes.Add(rule.Type.ToString()); d["rule_types"] = ruleTypes.ToArray();
            List<string> collectedTags = new List<string>();
            foreach (TaggedObject item in collected) collectedTags.Add(item.Tag.ToString());
            d["section_collected_tags"] = collectedTags.ToArray(); d["section_rule_indices"] = ruleIndices;
            NXOpen.Point sourcePoint = collected != null && collected.Length == 1 ? collected[0] as NXOpen.Point : null;
            d["section_source_point_id"] = sourcePoint == null ? null : sourcePoint.JournalIdentifier;
            d["section_source_point_tag"] = sourcePoint == null ? null : sourcePoint.Tag.ToString();
            d["section_source_point_coordinates_mm"] = sourcePoint == null ? null : P(sourcePoint.Coordinates);
            d["section_association_verified"] = ruleTypes.Count == 1 && ruleTypes[0] == "CurveDumb" && sourcePoint != null &&
                ruleIndices != null && ruleIndices.Length == 1 && ruleIndices[0] == 1;
            Expression dia = kind == "simple" ? b.GeneralSimpleHoleDiameter : kind == "counterbore" ? b.GeneralCounterboreHoleDiameter : kind == "countersink" ? b.GeneralCountersinkHoleDiameter : b.TapDrillDiameter;
            Expression depth = kind == "simple" ? b.GeneralSimpleHoleDepth : kind == "counterbore" ? b.GeneralCounterboreHoleDepth : kind == "countersink" ? b.GeneralCountersinkHoleDepth : b.ThreadedHoleDepth;
            d["diameter_mm"] = dia.Value; d["diameter_expression_id"] = dia.JournalIdentifier;
            d["hole_depth_mm"] = mode == "blind" ? (object)depth.Value : null;
            d["hole_depth_expression_id"] = mode == "blind" ? depth.JournalIdentifier : null;
            d["tip_angle_deg"] = (threaded ? b.ThreadedTipAngle : b.GeneralTipAngle).Value;
            if (kind == "counterbore") { d["counterbore_diameter_mm"] = b.GeneralCounterboreDiameter.Value; d["counterbore_depth_mm"] = b.GeneralCounterboreDepth.Value; d["counterbore_diameter_expression_id"] = b.GeneralCounterboreDiameter.JournalIdentifier; d["counterbore_depth_expression_id"] = b.GeneralCounterboreDepth.JournalIdentifier; }
            if (kind == "countersink") { d["countersink_diameter_mm"] = b.GeneralCountersinkDiameter.Value; d["countersink_angle_deg"] = b.GeneralCountersinkAngle.Value; d["countersink_diameter_expression_id"] = b.GeneralCountersinkDiameter.JournalIdentifier; d["countersink_angle_expression_id"] = b.GeneralCountersinkAngle.JournalIdentifier; }
            List<Dictionary<string, object>> expressions = new List<Dictionary<string, object>>();
            Feature hole = FindOwnedHole(part, feature, b, kind, mode, d);
            AddExpressions(feature, expressions);
            AddExpressions(hole, expressions);
            if (threaded) ReadThread(part, feature, hole, b, d, expressions);
            AddExclusivePackageExpressions(part, feature, hole, d, expressions);
            d["feature_expressions"] = expressions;
            return d;
        }
        finally { if (b != null) Destroy(b); }
    }

    private static void ReadThread(Part part, HolePackage package, Feature hole, HolePackageBuilder b, Dictionary<string, object> d, List<Dictionary<string, object>> expressions)
    {
        List<NXOpen.Features.Thread> candidates = new List<NXOpen.Features.Thread>();
        foreach (Feature child in hole.GetChildren())
        {
            NXOpen.Features.Thread childThread = child as NXOpen.Features.Thread; if (childThread != null) candidates.Add(childThread);
        }
        if (candidates.Count != 1) throw new InvalidOperationException("Expected one native internal Thread child of the owned hole.");
        NXOpen.Features.Thread thread = candidates[0];
        Feature[] parents = thread.GetParents();
        if (!thread.IsInternal || thread.Timestamp != package.Timestamp || thread.FeatureType != "SYMBOLIC_THREAD" || parents.Length != 1 || !parents[0].Tag.Equals(hole.Tag) || thread.GetBodies().Length != 0)
            throw new InvalidOperationException("Native Thread identity, timestamp, body or parent does not establish ownership.");
        Expression[] threadExpressions = thread.GetExpressions();
        if (threadExpressions == null || threadExpressions.Length == 0) throw new InvalidOperationException("Native Thread has no expressions.");
        foreach (Expression e in threadExpressions) RequireExclusiveUser(e, package);
        List<string> shared = SharedExpressionTags(threadExpressions, package.GetExpressions());
        if (shared.Count == 0) throw new InvalidOperationException("Thread has no shared native expression with the package.");
        AddExpressions(thread, expressions);
        NXOpen.Features.Thread t = thread;
        NXObject[] symbols = t.GetEntities();
        if (symbols == null || symbols.Length != 2) throw new InvalidOperationException("Expected exactly two symbolic Thread Arc entities.");
        List<string> symbolTags = new List<string>(); List<object> symbolRecords = new List<object>();
        List<Dictionary<string, object>> symbolGeometry = new List<Dictionary<string, object>>();
        foreach (NXObject symbol in symbols)
        {
            if (!(symbol is NXOpen.Arc) || symbol.OwningPart == null || !symbol.OwningPart.Tag.Equals(part.Tag) || symbolTags.Contains(symbol.Tag.ToString()))
                throw new InvalidOperationException("Symbolic Thread entities are not two unique same-part Arcs.");
            symbolTags.Add(symbol.Tag.ToString());
            symbolRecords.Add(new Dictionary<string, object> { { "tag", symbol.Tag.ToString() }, { "id", symbol.JournalIdentifier }, { "type", symbol.GetType().FullName }, { "owner_thread_id", t.JournalIdentifier } });
            NXOpen.Arc arc = (NXOpen.Arc)symbol; Point3d center; Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            symbolGeometry.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                { "center_mm", P(arc.CenterPoint) }, { "orientation_center_mm", P(center) }, { "radius_mm", arc.Radius },
                { "orientation_x", V(xAxis) }, { "orientation_y", V(yAxis) },
                { "start_angle_rad", arc.StartAngle }, { "end_angle_rad", arc.EndAngle }
            });
        }
        symbolTags.Sort(StringComparer.Ordinal);
        symbolGeometry.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right) {
            return String.CompareOrdinal(M2Json.CanonicalText(left), M2Json.CanonicalText(right));
        });
        d["thread_symbol_curve_tags"] = symbolTags.ToArray(); d["thread_symbol_entities"] = symbolRecords;
        d["thread_symbol_geometry"] = symbolGeometry;
        d["thread_ownership"] = new Dictionary<string, object> {
            { "tag", t.Tag.ToString() }, { "id", t.JournalIdentifier }, { "is_internal", t.IsInternal },
            { "feature_type", t.FeatureType }, { "timestamp", t.Timestamp },
            { "parent_ids", FeatureIds(parents) }, { "parent_tags", FeatureTags(parents) },
            { "body_ids", BodyIds(t.GetBodies()) }, { "body_tags", BodyTags(t.GetBodies()) },
            { "shared_expression_tags", shared.ToArray() }
        };
        ThreadBuilder tb = null;
        try
        {
            tb = part.Features.CreateThreadBuilder(t);
            if (tb.ThreadType != ThreadBuilder.Type.Symbolic || !tb.IsInternalThread) throw new InvalidOperationException("Expected an internal symbolic thread.");
            Expression threadLength = tb.ThreadLength;
            Feature lengthOwner = threadLength.GetOwningFeature();
            if (lengthOwner == null || !lengthOwner.Tag.Equals(t.Tag)) throw new InvalidOperationException("Thread length expression has the wrong native owner.");
            RequireExclusiveUser(threadLength, package);
            bool lengthListed = false;
            foreach (Expression e in threadExpressions) if (e.Tag.Equals(threadLength.Tag)) lengthListed = true;
            if (!lengthListed) throw new InvalidOperationException("Thread length is not listed by the native Thread feature.");
            d["thread_id"] = t.JournalIdentifier; d["thread_tag"] = t.Tag.ToString();
            List<string> parentIds = new List<string>(); foreach (Feature p in t.GetParents()) parentIds.Add(p.JournalIdentifier); d["thread_parent_ids"] = parentIds.ToArray();
            d["thread_type"] = tb.ThreadType.ToString(); d["thread_internal"] = tb.IsInternalThread;
            d["thread_limit"] = tb.ThreadLimit.ToString();
            d["thread_reverse_direction"] = tb.ReverseThreadDirection;
            d["thread_cylindrical_face"] = SelectedObject(tb.CylindricalFace);
            d["thread_start_object"] = SelectedObject(tb.StartObject);
            d["thread_face_definition"] = ValidateThreadCylinder(package, hole, tb);
            d["thread_standard"] = tb.ThreadStandard; d["thread_size"] = tb.ThreadSize; d["radial_engage"] = tb.RadialEngage;
            d["hand"] = tb.ThreadHandedness == ThreadBuilder.Handedness.RightHand ? "right" : tb.ThreadHandedness == ThreadBuilder.Handedness.LeftHand ? "left" : null; d["pitch_mm"] = tb.Pitch; d["pitch_expression_value_mm"] = tb.PitchExp.Value; d["thread_starts"] = tb.NumStarts; d["thread_major_diameter_mm"] = tb.MajorDiameter; d["thread_minor_diameter_mm"] = tb.MinorDiameter; d["thread_angle_deg"] = tb.Angle; d["thread_tapered"] = tb.Tapered; d["thread_depth_mm"] = tb.ThreadLength.Value; d["tap_drill_diameter_mm"] = tb.TapDrillDiameter;
            d["thread_length_expression_id"] = tb.ThreadLength.JournalIdentifier; d["pitch_expression_id"] = tb.PitchExp.JournalIdentifier;
            d["package_thread_standard"] = b.ThreadStandard; d["package_thread_size"] = b.ThreadSize; d["package_radial_engage"] = b.RadialEngageOption; d["package_thread_rotation"] = b.ThreadRotation.ToString();
            d["package_thread_depth_mm"] = b.ThreadDepth.Value; d["thread_length_option"] = b.ThreadLengthOption.ToString();
            d["relate_hole_depth_to_thread_depth"] = b.RelateHoleDepthToThreadDepth; d["threaded_start_chamfer_enabled"] = b.ThreadedStartChamferEnabled; d["threaded_end_chamfer_enabled"] = b.ThreadedEndChamferEnabled; d["threaded_relief_enabled"] = b.ThreadedReliefEnabled; d["relief_chamfer_enabled"] = b.ReliefChamferEnabled; d["thread_at_both_ends"] = b.ThreadAtBothEnds;
        }
        finally { if (tb != null) Destroy(tb); }
    }
    private static Feature FindOwnedHole(Part part, HolePackage package, HolePackageBuilder builder, string kind, string mode, Dictionary<string, object> evidence)
    {
        Body[] packageBodies = package.GetBodies();
        Body[] targets = builder.BooleanOperation.GetTargetBodies();
        Feature[] packageParents = package.GetParents();
        Expression[] packageExpressions = package.GetExpressions();
        if (package.IsInternal || packageBodies.Length != 1 || targets.Length != 1 || !packageBodies[0].Tag.Equals(targets[0].Tag) || packageParents.Length == 0 || packageExpressions.Length == 0)
            throw new InvalidOperationException("Invalid package body, parent, or expression evidence.");
        string expectedType = kind == "counterbore" ? "CBORE_HOLE" : kind == "countersink" ? "CSUNK_HOLE" : "SIMPLE HOLE";
        Expression diameter = kind == "threaded" ? builder.TapDrillDiameter : kind == "simple" ? builder.GeneralSimpleHoleDiameter : kind == "counterbore" ? builder.GeneralCounterboreHoleDiameter : builder.GeneralCountersinkHoleDiameter;
        Expression depth = kind == "threaded" ? builder.ThreadedHoleDepth : kind == "simple" ? builder.GeneralSimpleHoleDepth : kind == "counterbore" ? builder.GeneralCounterboreHoleDepth : builder.GeneralCountersinkHoleDepth;
        List<Feature> matches = new List<Feature>();
        foreach (Feature candidate in part.Features)
        {
            if (!candidate.IsInternal || candidate.FeatureType != expectedType || candidate.Timestamp != package.Timestamp) continue;
            Body[] bodies = candidate.GetBodies();
            if (bodies.Length != 1 || !bodies[0].Tag.Equals(packageBodies[0].Tag) || !SameFeatures(candidate.GetParents(), packageParents)) continue;
            Expression[] candidateExpressions = candidate.GetExpressions();
            if (candidateExpressions.Length == 0 || !Contains(candidateExpressions, diameter) || !Contains(packageExpressions, diameter)) continue;
            if (mode == "blind" && (!Contains(candidateExpressions, depth) || !Contains(packageExpressions, depth))) continue;
            bool uniqueUsers = true;
            foreach (Expression expression in candidateExpressions) if (!IsExclusiveUser(expression, package)) { uniqueUsers = false; break; }
            if (uniqueUsers) matches.Add(candidate);
        }
        if (matches.Count != 1) throw new InvalidOperationException("Native internal hole ownership is absent or ambiguous.");
        Feature hole = matches[0];
        foreach (Feature other in part.Features)
        {
            HolePackage otherPackage = other as HolePackage;
            if (otherPackage == null || other.Tag.Equals(package.Tag)) continue;
            if (other.Timestamp != hole.Timestamp || !SameBodies(other.GetBodies(), hole.GetBodies()) || !SameFeatures(other.GetParents(), hole.GetParents())) continue;
            List<string> overlap = SharedExpressionTags(other.GetExpressions(), hole.GetExpressions());
            if (overlap.Count != 0) throw new InvalidOperationException("Internal hole also matches another HolePackage.");
        }
        evidence["hole_ownership"] = new Dictionary<string, object> {
            { "id", hole.JournalIdentifier }, { "tag", hole.Tag.ToString() }, { "feature_type", hole.FeatureType },
            { "is_internal", hole.IsInternal }, { "timestamp", hole.Timestamp }, { "package_timestamp", package.Timestamp },
            { "parent_ids", FeatureIds(hole.GetParents()) }, { "parent_tags", FeatureTags(hole.GetParents()) },
            { "package_parent_ids", FeatureIds(packageParents) }, { "package_parent_tags", FeatureTags(packageParents) },
            { "body_ids", BodyIds(hole.GetBodies()) }, { "body_tags", BodyTags(hole.GetBodies()) },
            { "package_body_ids", BodyIds(packageBodies) }, { "package_body_tags", BodyTags(packageBodies) },
            { "shared_expression_tags", SharedExpressionTags(hole.GetExpressions(), packageExpressions).ToArray() },
            { "diameter_expression_tag", diameter.Tag.ToString() }, { "depth_expression_tag", mode == "blind" ? depth.Tag.ToString() : null },
            { "matching_internal_hole_count", matches.Count }, { "other_matching_package_count", 0 }
        };
        return hole;
    }
    private static void AddExclusivePackageExpressions(Part part, HolePackage package, Feature hole, Dictionary<string, object> evidence, List<Dictionary<string, object>> items)
    {
        string threadTag = null;
        object raw;
        if (evidence.TryGetValue("thread_tag", out raw)) threadTag = (string)raw;
        HashSet<string> already = new HashSet<string>();
        foreach (Dictionary<string, object> item in items) already.Add((string)item["tag"]);
        foreach (Expression e in part.Expressions)
        {
            if (!IsExclusiveUser(e, package)) continue;
            Feature owner = e.GetOwningFeature();
            if (owner != null && !owner.Tag.Equals(package.Tag) && !owner.Tag.Equals(hole.Tag) && (threadTag == null || owner.Tag.ToString() != threadTag))
                throw new InvalidOperationException("Package expression is owned by an uncontrolled feature.");
            if (already.Add(e.Tag.ToString())) AddExpression(e, null, items);
        }
        foreach (Dictionary<string, object> item in items)
        {
            string[] users = (string[])item["user_feature_tags"];
            if (users.Length != 1 || users[0] != package.Tag.ToString()) throw new InvalidOperationException("Owned-feature expression has non-exclusive users.");
            string ownerTag = item["owner_feature_tag"] as string;
            if (ownerTag != null && ownerTag != package.Tag.ToString() && ownerTag != hole.Tag.ToString() && ownerTag != threadTag) throw new InvalidOperationException("Owned-feature expression has an uncontrolled owner.");
        }
    }
    private static bool IsExclusiveUser(Expression e, Feature f) { Feature[] users = e.GetUsingFeatures(); return users != null && users.Length == 1 && users[0].Tag.Equals(f.Tag); }
    private static void RequireExclusiveUser(Expression e, Feature f) { if (!IsExclusiveUser(e, f)) throw new InvalidOperationException("Expression does not have a unique package user."); }
    private static bool Contains(Expression[] expressions, Expression expression) { foreach (Expression e in expressions) if (e.Tag.Equals(expression.Tag)) return true; return false; }
    private static List<string> SharedExpressionTags(Expression[] a, Expression[] b) { List<string> result = new List<string>(); foreach (Expression e in a) if (Contains(b, e)) result.Add(e.Tag.ToString()); return result; }
    private static bool SameFeatures(Feature[] a, Feature[] b) { if (a.Length != b.Length) return false; HashSet<string> tags = new HashSet<string>(); foreach (Feature f in a) tags.Add(f.Tag.ToString()); foreach (Feature f in b) if (!tags.Remove(f.Tag.ToString())) return false; return tags.Count == 0; }
    private static bool SameBodies(Body[] a, Body[] b) { if (a.Length != b.Length) return false; HashSet<string> tags = new HashSet<string>(); foreach (Body f in a) tags.Add(f.Tag.ToString()); foreach (Body f in b) if (!tags.Remove(f.Tag.ToString())) return false; return tags.Count == 0; }
    private static string[] FeatureIds(Feature[] a) { List<string> r = new List<string>(); foreach (Feature f in a) r.Add(f.JournalIdentifier); return r.ToArray(); }
    private static string[] FeatureTags(Feature[] a) { List<string> r = new List<string>(); foreach (Feature f in a) r.Add(f.Tag.ToString()); return r.ToArray(); }
    private static string[] BodyIds(Body[] a) { List<string> r = new List<string>(); foreach (Body f in a) r.Add(f.JournalIdentifier); return r.ToArray(); }
    private static string[] BodyTags(Body[] a) { List<string> r = new List<string>(); foreach (Body f in a) r.Add(f.Tag.ToString()); return r.ToArray(); }
    private static object SelectedObject(SelectDisplayableObject selector)
    {
        if (selector == null) return null;
        DisplayableObject selected = selector.Value;
        if (selected == null) return null;
        Dictionary<string, object> record = new Dictionary<string, object> {
            { "type", selected.GetType().FullName },
            { "tag", selected.Tag.ToString() },
            { "id", selected.JournalIdentifier }
        };
        NXOpen.Plane plane = selected as NXOpen.Plane;
        if (plane != null) { record["origin_mm"] = P(plane.Origin); record["normal"] = V(plane.Normal); }
        NXOpen.Face face = selected as NXOpen.Face;
        if (face != null) { Body body = face.GetBody(); record["body_tag"] = body == null ? null : body.Tag.ToString(); record["body_id"] = body == null ? null : body.JournalIdentifier; }
        return record;
    }
    private static Dictionary<string, object> ValidateThreadCylinder(HolePackage package, Feature hole, ThreadBuilder builder)
    {
        if (Double.IsNaN(builder.TapDrillDiameter) || Double.IsInfinity(builder.TapDrillDiameter) || builder.TapDrillDiameter <= 0)
            throw new InvalidOperationException("Thread tap diameter is invalid.");
        Face selected = builder.CylindricalFace == null ? null : builder.CylindricalFace.Value as Face;
        if (selected == null) throw new InvalidOperationException("Thread has no selected native cylindrical face.");
        Body[] bodies = package.GetBodies();
        if (bodies == null || bodies.Length != 1 || selected.GetBody() == null || !selected.GetBody().Tag.Equals(bodies[0].Tag))
            throw new InvalidOperationException("Thread face does not belong to the package target body.");
        Face[] holeFaces = hole.GetFaces(); bool listed = false;
        if (holeFaces != null) foreach (Face candidate in holeFaces) if (candidate != null && candidate.Tag.Equals(selected.Tag)) listed = true;
        if (!listed) throw new InvalidOperationException("Thread face is not a face of the proven internal hole.");
        Point3d[] origins; Vector3d[] directions; package.GetOrigins(out origins); package.GetDirections(out directions);
        if (origins == null || origins.Length != 1 || directions == null || directions.Length != 1) throw new InvalidOperationException("Hole frame is unavailable.");
        double[] p = P(origins[0]), axis = V(directions[0]);
        double n = Math.Sqrt(axis[0]*axis[0] + axis[1]*axis[1] + axis[2]*axis[2]);
        if (Double.IsNaN(n) || Double.IsInfinity(n) || n < 1e-12) throw new InvalidOperationException("Hole direction is invalid.");
        for (int i=0;i<3;i++) axis[i] /= n;
        int count = 0; double[] chosenPoint = null, chosenAxis = null, chosenBox = null; double chosenRadius = 0;
        foreach (Face candidate in bodies[0].GetFaces())
        {
            int type, sense; double radius, radiusData; double[] point = new double[3], dir = new double[3], box = new double[6];
            UFSession.GetUFSession().Modl.AskFaceData(candidate.Tag, out type, point, dir, box, out radius, out radiusData, out sense);
            if (type != UFConstants.UF_MODL_CYLINDRICAL_FACE || Double.IsNaN(radius) || Double.IsInfinity(radius) || Math.Abs(radius-builder.TapDrillDiameter/2) > 1e-6) continue;
            double length = Math.Sqrt(dir[0]*dir[0]+dir[1]*dir[1]+dir[2]*dir[2]);
            if (Double.IsNaN(length) || Double.IsInfinity(length) || length < 1e-12) continue;
            double dot = (dir[0]*axis[0]+dir[1]*axis[1]+dir[2]*axis[2])/length;
            double x=point[0]-p[0], y=point[1]-p[1], z=point[2]-p[2], axial=x*axis[0]+y*axis[1]+z*axis[2];
            double offset=Math.Sqrt((x-axial*axis[0])*(x-axial*axis[0])+(y-axial*axis[1])*(y-axial*axis[1])+(z-axial*axis[2])*(z-axial*axis[2]));
            if (Double.IsNaN(offset) || Double.IsInfinity(offset) || Math.Abs(dot)<1-1e-9 || offset>1e-6) continue;
            count++;
            if (candidate.Tag.Equals(selected.Tag)) { chosenPoint=point; chosenAxis=dir; chosenBox=box; chosenRadius=radius; }
        }
        if (count != 1 || chosenPoint == null) throw new InvalidOperationException("Thread cylinder is not the unique coaxial tap-diameter face.");
        double chosenLength = Math.Sqrt(chosenAxis[0]*chosenAxis[0]+chosenAxis[1]*chosenAxis[1]+chosenAxis[2]*chosenAxis[2]);
        double sign = chosenAxis[0]*axis[0] + chosenAxis[1]*axis[1] + chosenAxis[2]*axis[2] < 0 ? -1.0 : 1.0;
        for (int i=0;i<3;i++) chosenAxis[i] = sign*chosenAxis[i]/chosenLength;
        double projection = chosenPoint[0]*chosenAxis[0] + chosenPoint[1]*chosenAxis[1] + chosenPoint[2]*chosenAxis[2];
        for (int i=0;i<3;i++) chosenPoint[i] -= projection*chosenAxis[i];
        return new Dictionary<string, object> { { "face_tag", selected.Tag.ToString() }, { "face_id", selected.JournalIdentifier },
            { "body_tag", bodies[0].Tag.ToString() }, { "axis_point_mm", chosenPoint }, { "axis_direction", chosenAxis },
            { "bbox_mm", chosenBox }, { "radius_mm", chosenRadius }, { "coaxial_tap_cylinder_count", count },
            { "listed_by_internal_hole", listed } };
    }
    private static void Destroy(Builder builder)
    {
        try { builder.Destroy(); }
        catch (Exception error) { throw new V2HoleBuilderCleanupException("NX Hole builder destruction failed; model state is unknown.", error); }
    }
    private static void AddExpressions(Feature feature, List<Dictionary<string, object>> list)
    {
        foreach (Expression expression in feature.GetExpressions()) AddExpression(expression, feature, list);
    }
    private static void AddExpression(Expression expression, Feature feature, List<Dictionary<string, object>> list)
    {
            string type = expression.Type;
            object value;
            switch (type.ToLowerInvariant())
            {
                case "number": value = expression.Value; break;
                case "integer": value = expression.IntegerValue; break;
                case "boolean": value = expression.BooleanValue; break;
                case "string": value = expression.StringValue; break;
                case "point": value = P(expression.PointValue); break;
                case "vector": value = V(expression.VectorValue); break;
                default: throw new InvalidOperationException("Unsupported native expression type: " + type);
            }
            Feature owner = expression.GetOwningFeature();
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["id"] = expression.JournalIdentifier; item["tag"] = expression.Tag.ToString(); item["name"] = expression.Name;
            item["type"] = type; item["value"] = value; item["rhs"] = expression.RightHandSide;
            item["listed_by_feature_id"] = feature == null ? null : feature.JournalIdentifier;
            item["owner_feature_id"] = owner == null ? null : owner.JournalIdentifier;
            item["owner_feature_tag"] = owner == null ? null : owner.Tag.ToString();
            Feature[] users = expression.GetUsingFeatures();
            item["user_feature_ids"] = FeatureIds(users); item["user_feature_tags"] = FeatureTags(users);
            list.Add(item);
    }
    private static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
    private static void Positive(double x, string name) { if (Double.IsNaN(x) || Double.IsInfinity(x) || x <= 0) throw new ArgumentException(name + " must be finite and positive."); }
    private static Point3d Point(double[] a) { Check(a); return new Point3d(a[0], a[1], a[2]); }
    private static Vector3d Vector(double[] a) { Check(a); double n = Math.Sqrt(a[0]*a[0] + a[1]*a[1] + a[2]*a[2]); if (Double.IsInfinity(n) || Math.Abs(n - 1.0) > 1e-8) throw new ArgumentException("Direction must be a unit vector."); return new Vector3d(a[0], a[1], a[2]); }
    private static void Check(double[] a) { if (a == null || a.Length != 3) throw new ArgumentException("Expected three coordinates."); foreach (double x in a) if (Double.IsNaN(x) || Double.IsInfinity(x)) throw new ArgumentException("Coordinates must be finite."); }
    private static double[] P(Point3d p) { return new double[] { p.X, p.Y, p.Z }; }
    private static double[] V(Vector3d p) { return new double[] { p.X, p.Y, p.Z }; }
}
