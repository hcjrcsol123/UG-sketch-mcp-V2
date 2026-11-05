using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.ExceptionServices;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

/// <summary>Optional F07 adapter for modifying one explicit pair of native sketch curves.</summary>
public sealed partial class M2OpenModelAdapter
{
    private const double SketchCornerPositionToleranceMm = 1.0e-6;
    private const double SketchCornerAngleTolerance = 1.0e-7;
    private const double SketchCornerMinimumAngleRad = 1.0e-7;

    private sealed class CornerGeometrySnapshot
    {
        internal string Tag;
        internal string JournalId;
        internal string ObjectType;
        internal string SketchTag;
        internal string Signature;
        internal Dictionary<string, object> Record;
    }

    private sealed class CornerConstraintSnapshot
    {
        internal string Tag;
        internal string JournalId;
        internal string Signature;
        internal Dictionary<string, object> Record;
    }

    private sealed class M3SketchCornerPlanState
    {
        internal M2ModelBinding Binding;
        internal BindingState BindingState;
        internal string RevisionId;
        internal string ActiveSketchTagBefore;
        internal M3SketchCornerRequest Request;
        internal Sketch Sketch;
        internal string SketchId;
        internal string SketchTag;
        internal string FeatureId;
        internal string FeatureTag;
        internal Curve Curve1;
        internal Curve Curve2;
        internal string Curve1Tag;
        internal string Curve2Tag;
        internal string Curve1Type;
        internal string Curve2Type;
        internal Point3d Corner;
        internal Point3d Curve1EndpointBefore;
        internal Point3d Curve2EndpointBefore;
        internal Point3d Curve1RemoteBefore;
        internal Point3d Curve2RemoteBefore;
        internal Vector3d Ray1;
        internal Vector3d Ray2;
        internal double CornerAngleRad;
        internal double Curve1LengthBefore;
        internal double Curve2LengthBefore;
        internal Point3d FilletHelpPoint1;
        internal Point3d FilletHelpPoint2;
        internal Point3d ChamferHelpPoint;
        internal double ChamferHelpPointEpsilonMm;
        internal double ChamferHelpPointDistanceToCurve1SupportMm;
        internal double ChamferHelpPointDistanceToCurve2SupportMm;
        internal double Distance1Mm;
        internal double Distance2Mm;
        internal double AngleDeg;
        internal V2SketchChamferMode ChamferMode;
        internal bool IsDistanceAngle;
        internal bool DirtyBefore;
        internal string SemanticBefore;
        internal string HistoryBefore;
        internal Dictionary<string, CornerGeometrySnapshot> GeometryBefore;
        internal Dictionary<string, CornerConstraintSnapshot> ConstraintsBefore;
        internal List<M3BodyMeasure> BodiesBefore;
        internal List<string> FeatureTagsBefore;
        internal Dictionary<string, CornerGeometrySnapshot> GeometryAfter;
        internal Dictionary<string, CornerConstraintSnapshot> ConstraintsAfter;
        internal List<M3BodyMeasure> BodiesAfter;
        internal string SemanticAfter;
        internal string HistoryAfter;
        internal string ChamferNativeCurveOrder;
        internal string NativeFilletArcTag;
        internal Dictionary<string, object> NativeOperationEvidence;
        internal Dictionary<string, object> AppliedEvidence;
        internal M3ExecutionResult Execution;
    }

    public M3PlanSnapshot PrepareSketchCorner(M2ModelBinding binding, M3SketchCornerRequest request)
    {
        if (request == null) throw new M2ModelException("INVALID_REQUEST", "sketch corner request is required");
        if (String.IsNullOrEmpty(request.RevisionId) || String.IsNullOrEmpty(request.SketchId) ||
            String.IsNullOrEmpty(request.Kind) || String.IsNullOrEmpty(request.Curve1Id) ||
            String.IsNullOrEmpty(request.Curve2Id))
            throw new M2ModelException("INVALID_REQUEST", "revision_id, sketch_id, kind and two curve ids are required");
        if (request.Kind != "fillet" && request.Kind != "chamfer")
            throw new M2ModelException("INVALID_REQUEST", "kind must be fillet or chamfer");
        if ((request.Curve1End != "start" && request.Curve1End != "end") ||
            (request.Curve2End != "start" && request.Curve2End != "end"))
            throw new M2ModelException("INVALID_REQUEST", "curve1_end and curve2_end must be start or end");
        if (String.Equals(request.Curve1Id, request.Curve2Id, StringComparison.Ordinal))
            throw new M2ModelException("INVALID_REQUEST", "curve1_id and curve2_id must identify distinct curves");
        if (request.Kind == "fillet")
        {
            RequireFinitePositive(request.RadiusMm, "radius_mm");
            if (request.Distance2Mm.HasValue || request.AngleDeg.HasValue)
                throw new M2ModelException("INVALID_REQUEST", "fillet accepts radius_mm only");
        }
        else
        {
            RequireFinitePositive(request.Distance1Mm, "distance1_mm");
            if (request.Distance2Mm.HasValue == request.AngleDeg.HasValue)
                throw new M2ModelException("INVALID_REQUEST", "chamfer requires exactly one of distance2_mm or angle_deg");
            if (request.Distance2Mm.HasValue) RequireFinitePositive(request.Distance2Mm.Value, "distance2_mm");
            if (request.AngleDeg.HasValue && (!IsFinite(request.AngleDeg.Value) ||
                request.AngleDeg.Value <= 0.0 || request.AngleDeg.Value >= 180.0))
                throw new M2ModelException("INVALID_REQUEST", "angle_deg must be greater than 0 and less than 180 degrees");
        }

        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "a Sketch is active in NX; sketch corner preparation is paused");
        string activeSketchTagBefore = state.Session.ActiveSketch == null
            ? null : state.Session.ActiveSketch.Tag.ToString();
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            throw new M2ModelException("STALE_BINDING", bindingReason ?? "NX model binding is no longer current");
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current part revision");

        SnapshotResult captured = Capture(binding, state);
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id changed while resolving the sketch corner targets");
        Sketch sketch = FindSketch(state.WorkPart, request.SketchId);
        if (sketch == null || !String.Equals(RequiredJournalId(sketch, "sketch"), request.SketchId, StringComparison.Ordinal))
            throw new M2ModelException("SKETCH_NOT_FOUND", "sketch_id does not identify a native Sketch in the bound Work Part");

        Curve curve1 = FindSketchCornerCurve(sketch, request.Curve1Id);
        Curve curve2 = FindSketchCornerCurve(sketch, request.Curve2Id);
        if (curve1 == null || curve2 == null)
            throw new M2ModelException("CURVE_NOT_FOUND", "both curve ids must identify native geometry in the requested Sketch");
        if (!sketch.IsSketchGeometry(curve1) || !sketch.IsSketchGeometry(curve2))
            throw new M2ModelException("SKETCH_TARGET_MISMATCH", "both selected curves must be owned by the explicitly requested Sketch");
        if (curve1.Tag.Equals(curve2.Tag))
            throw new M2ModelException("INVALID_REQUEST", "curve1_id and curve2_id resolve to the same native curve");
        if (request.Kind == "fillet")
        {
            if (!IsSketchCornerFilletCurve(curve1) || !IsSketchCornerFilletCurve(curve2))
                throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "fillet supports native Line and partial Arc curve pairs only");
            if (IsClosedSketchCornerCurve(curve1) || IsClosedSketchCornerCurve(curve2))
                throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "closed curves have no unique start/end corner for F07");
            if (HasMirrorConstraint(sketch, curve1) || HasMirrorConstraint(sketch, curve2))
                throw new M2ModelException("MIRROR_CONSTRAINT_UNSUPPORTED", "NX sketch fillet propagates to mirrored curves; F07 rejects mirror-constrained targets");
        }
        else if (!(curve1 is Line) || !(curve2 is Line))
            throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "chamfer is limited to native Line-Line pairs by NXOpen SketchChamferBuilder");

        Point3d endpoint1 = CurveEndpoint(curve1, request.Curve1End);
        Point3d endpoint2 = CurveEndpoint(curve2, request.Curve2End);
        if (PointDistance(endpoint1, endpoint2) > SketchCornerPositionToleranceMm)
            throw new M2ModelException("CORNER_ENDPOINTS_NOT_CONNECTED", "the explicitly selected start/end endpoints do not share one corner within 1e-6 mm");
        Point3d corner = Midpoint(endpoint1, endpoint2);
        Vector3d ray1 = CurveTangentAwayFromEndpoint(curve1, request.Curve1End);
        Vector3d ray2 = CurveTangentAwayFromEndpoint(curve2, request.Curve2End);
        NormalizeVector(ref ray1, "curve1 endpoint tangent");
        NormalizeVector(ref ray2, "curve2 endpoint tangent");
        double dot = Clamp(Dot(ray1, ray2), -1.0, 1.0);
        double theta = Math.Acos(dot);
        if (!IsFinite(theta) || theta <= SketchCornerMinimumAngleRad || Math.PI - theta <= SketchCornerMinimumAngleRad)
            throw new M2ModelException("DEGENERATE_CORNER", "the selected curve rays are parallel or collinear at the chosen endpoints");

        double distance1 = request.Distance1Mm;
        double distance2 = request.Distance2Mm.HasValue ? request.Distance2Mm.Value : 0.0;
        double angleDeg = request.AngleDeg.HasValue ? request.AngleDeg.Value : 0.0;
        bool distanceAngle = request.Kind == "chamfer" && request.AngleDeg.HasValue;
        if (distanceAngle)
        {
            double delta = angleDeg * Math.PI / 180.0;
            if (theta + delta >= Math.PI - SketchCornerMinimumAngleRad)
                throw new M2ModelException("INVALID_CHAMFER_ANGLE", "angle_deg leaves no interior chamfer solution for the selected corner");
            double denominator = Math.Sin(theta + delta);
            if (Math.Abs(denominator) <= SketchCornerMinimumAngleRad)
                throw new M2ModelException("INVALID_CHAMFER_ANGLE", "distance-angle parameters have a singular geometric solution");
            distance2 = distance1 * Math.Sin(delta) / denominator;
            if (!IsFinite(distance2) || distance2 <= 0.0)
                throw new M2ModelException("INVALID_CHAMFER_ANGLE", "distance-angle parameters produce a nonpositive second distance");
        }

        double length1 = curve1.GetLength();
        double length2 = curve2.GetLength();
        if (!IsFinite(length1) || !IsFinite(length2) || length1 <= SketchCornerPositionToleranceMm || length2 <= SketchCornerPositionToleranceMm)
            throw new M2ModelException("DEGENERATE_CURVE", "selected curve length is not finite and greater than 1e-6 mm");
        if (request.Kind == "chamfer" && (distance1 >= length1 - SketchCornerPositionToleranceMm ||
            distance2 >= length2 - SketchCornerPositionToleranceMm))
            throw new M2ModelException("DIMENSION_EXCEEDS_CURVE", "chamfer distances must be shorter than both selected retained curve segments");

        Vector3d expected1 = Scale(ray1, distance1);
        Vector3d expected2 = Scale(ray2, distance2);
        Point3d cut1 = Add(corner, expected1);
        Point3d cut2 = Add(corner, expected2);
        double chamferHelpEpsilonMm = 0.0;
        double chamferHelpDistanceToCurve1SupportMm = 0.0;
        double chamferHelpDistanceToCurve2SupportMm = 0.0;
        Point3d chamferHelp = corner;
        if (request.Kind == "chamfer")
        {
            // NX2506 maps asymmetric Distance1 to the curve branch selected by HelpPoint.
            // Bias the help point into the interior wedge near curve2, independent of
            // whether distance1 or distance2 is larger.
            chamferHelpEpsilonMm = Math.Min(distance1, distance2) * 0.1;
            Vector3d chamferHelpOffset = Add(Scale(ray1, chamferHelpEpsilonMm),
                Scale(ray2, 2.0 * chamferHelpEpsilonMm));
            chamferHelp = Add(corner, chamferHelpOffset);
            chamferHelpDistanceToCurve1SupportMm = PointToSupportLineDistance(chamferHelp, corner, ray1);
            chamferHelpDistanceToCurve2SupportMm = PointToSupportLineDistance(chamferHelp, corner, ray2);
        }
        Point3d filletHelp1 = PointAlongCurveFromEndpoint(curve1, request.Curve1End, 0.25);
        Point3d filletHelp2 = PointAlongCurveFromEndpoint(curve2, request.Curve2End, 0.25);

        string sketchId = RequiredJournalId(sketch, "sketch");
        string featureId = sketch.Feature == null ? null : RequiredJournalId(sketch.Feature, "sketch feature");
        Dictionary<string, CornerGeometrySnapshot> geometryBefore = CaptureSketchCornerGeometry(state.WorkPart);
        Dictionary<string, CornerConstraintSnapshot> constraintsBefore = CaptureSketchCornerConstraints(sketch);
        bool dirtyBefore;
        try { dirtyBefore = state.WorkPart.IsModified; }
        catch (Exception ex) { throw new M2ModelException("MODEL_READ_FAILED", "could not capture Work Part modified state for F07 (" + ex.GetType().Name + ")"); }
        List<M3BodyMeasure> bodiesBefore = CaptureM3BodyMeasures(state.WorkPart);
        Feature[] features = state.WorkPart.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature inventory is unavailable during F07 preparation");
        List<string> featureTagsBefore = new List<string>();
        foreach (Feature feature in features)
        {
            if (feature == null) throw new M2ModelException("MODEL_READ_FAILED", "feature inventory contains null during F07 preparation");
            featureTagsBefore.Add(feature.Tag.ToString());
        }
        featureTagsBefore.Sort(StringComparer.Ordinal);

        bool sameSketchFeature;
        try { sameSketchFeature = sketch.Feature != null && !sketch.Feature.IsOccurrence &&
            sketch.Feature.OwningPart != null && sketch.Feature.OwningPart.Tag == state.WorkPart.Tag; }
        catch (Exception ex) { throw new M2ModelException("MODEL_READ_FAILED", "could not verify native Sketch feature ownership (" + ex.GetType().Name + ")"); }
        if (!sameSketchFeature)
            throw new M2ModelException("UNSUPPORTED_SKETCH_HISTORY", "F07 edits require a native Sketch feature owned by the bound Work Part");

        string semanticBefore = CanonicalSemanticSnapshot(captured.Snapshot);
        string historyBefore = M2Json.CanonicalText(HistoryProjection(captured.Snapshot));
        V2SketchChamferMode chamferMode = V2SketchChamferMode.Asymmetric;
        string nativeChamferOrder = null;
        if (request.Kind == "chamfer")
        {
            if (!distanceAngle && Math.Abs(distance1 - distance2) <= 1.0e-12)
                chamferMode = V2SketchChamferMode.Symmetric;
            else
                chamferMode = V2SketchChamferMode.Asymmetric;
            // NX2506's OffsetAndAngle anchor follows help-point branch selection,
            // so F07 maps the requested A-angle to explicit Asymmetric offsets.
            nativeChamferOrder = "curve1,curve2";
        }

        M3SketchCornerPlanState planState = new M3SketchCornerPlanState
        {
            Binding = binding,
            BindingState = state,
            RevisionId = binding.RevisionId,
            ActiveSketchTagBefore = activeSketchTagBefore,
            Request = CloneM3SketchCornerRequest(request),
            Sketch = sketch,
            SketchId = sketchId,
            SketchTag = sketch.Tag.ToString(),
            FeatureId = featureId,
            FeatureTag = sketch.Feature.Tag.ToString(),
            Curve1 = curve1,
            Curve2 = curve2,
            Curve1Tag = curve1.Tag.ToString(),
            Curve2Tag = curve2.Tag.ToString(),
            Curve1Type = curve1.GetType().Name,
            Curve2Type = curve2.GetType().Name,
            Corner = corner,
            Curve1EndpointBefore = endpoint1,
            Curve2EndpointBefore = endpoint2,
            Curve1RemoteBefore = CurveEndpoint(curve1, request.Curve1End == "start" ? "end" : "start"),
            Curve2RemoteBefore = CurveEndpoint(curve2, request.Curve2End == "start" ? "end" : "start"),
            Ray1 = ray1,
            Ray2 = ray2,
            CornerAngleRad = theta,
            Curve1LengthBefore = length1,
            Curve2LengthBefore = length2,
            FilletHelpPoint1 = filletHelp1,
            FilletHelpPoint2 = filletHelp2,
            ChamferHelpPoint = chamferHelp,
            ChamferHelpPointEpsilonMm = chamferHelpEpsilonMm,
            ChamferHelpPointDistanceToCurve1SupportMm = chamferHelpDistanceToCurve1SupportMm,
            ChamferHelpPointDistanceToCurve2SupportMm = chamferHelpDistanceToCurve2SupportMm,
            Distance1Mm = distance1,
            Distance2Mm = distance2,
            AngleDeg = angleDeg,
            ChamferMode = chamferMode,
            IsDistanceAngle = distanceAngle,
            DirtyBefore = dirtyBefore,
            SemanticBefore = semanticBefore,
            HistoryBefore = historyBefore,
            GeometryBefore = geometryBefore,
            ConstraintsBefore = constraintsBefore,
            BodiesBefore = bodiesBefore,
            ChamferNativeCurveOrder = nativeChamferOrder,
            NativeOperationEvidence = new Dictionary<string, object>(StringComparer.Ordinal),
            FeatureTagsBefore = featureTagsBefore
        };

        Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal);
        publicView.Add("kind", "edit_sketch_corner");
        publicView.Add("revision_id", binding.RevisionId);
        publicView.Add("sketch_id", sketchId);
        publicView.Add("sketch_tag", sketch.Tag.ToString());
        publicView.Add("operation", request.Kind);
        publicView.Add("curve1_id", RequiredJournalId(curve1, "curve1"));
        publicView.Add("curve1_tag", planState.Curve1Tag);
        publicView.Add("curve1_type", planState.Curve1Type);
        publicView.Add("curve1_end", request.Curve1End);
        publicView.Add("curve1_selected_endpoint_mm", Point(endpoint1));
        publicView.Add("curve1_remote_endpoint_mm", Point(planState.Curve1RemoteBefore));
        publicView.Add("curve2_id", RequiredJournalId(curve2, "curve2"));
        publicView.Add("curve2_tag", planState.Curve2Tag);
        publicView.Add("curve2_type", planState.Curve2Type);
        publicView.Add("curve2_end", request.Curve2End);
        publicView.Add("curve2_selected_endpoint_mm", Point(endpoint2));
        publicView.Add("curve2_remote_endpoint_mm", Point(planState.Curve2RemoteBefore));
        publicView.Add("corner_mm", Point(corner));
        publicView.Add("retained_ray1", Vector(ray1.X, ray1.Y, ray1.Z));
        publicView.Add("retained_ray2", Vector(ray2.X, ray2.Y, ray2.Z));
        publicView.Add("interior_angle_deg", theta * 180.0 / Math.PI);
        if (request.Kind == "fillet")
        {
            publicView.Add("requested_definition", "fillet");
            publicView.Add("radius_mm", request.RadiusMm);
            publicView.Add("help_point1_mm", Point(filletHelp1));
            publicView.Add("help_point2_mm", Point(filletHelp2));
            publicView.Add("native_mode", "Sketch.Fillet");
        }
        else
        {
            string definition = distanceAngle ? "distance_angle" :
                (Math.Abs(distance1 - distance2) <= 1.0e-12 ? "equal_distance" : "two_distances");
            publicView.Add("requested_definition", definition);
            publicView.Add("distance1_mm", distance1);
            publicView.Add("distance2_mm", distance2);
            publicView.Add("angle_deg", distanceAngle ? (object)angleDeg : null);
            publicView.Add("native_mode", chamferMode.ToString());
            publicView.Add("native_curve_order", nativeChamferOrder);
            publicView.Add("expected_cut_point1_mm", Point(cut1));
            publicView.Add("expected_cut_point2_mm", Point(cut2));
            publicView.Add("help_point_strategy", "curve2_biased_ray_anchor");
            publicView.Add("help_point_epsilon_mm", chamferHelpEpsilonMm);
            publicView.Add("help_point_distance_to_curve1_support_mm", chamferHelpDistanceToCurve1SupportMm);
            publicView.Add("help_point_distance_to_curve2_support_mm", chamferHelpDistanceToCurve2SupportMm);
            if (distanceAngle) publicView.Add("derived_distance2_mm", distance2);
            publicView.Add("help_point_mm", Point(chamferHelp));
        }
        publicView.Add("existing_constraints_before", CornerConstraintPublicRecords(constraintsBefore));
        publicView.Add("selected_curve_constraints_before",
            CornerConstraintPublicRecordsForGeometry(constraintsBefore, planState.Curve1Tag, planState.Curve2Tag));
        publicView.Add("rollback_geometry_tag_count", geometryBefore.Count);
        publicView.Add("rollback_constraint_tag_count", constraintsBefore.Count);
        publicView.Add("profile_analysis", "NOT_PERFORMED_BY_F07");
        return new M3PlanSnapshot
        {
            Kind = "edit_sketch_corner",
            RevisionId = binding.RevisionId,
            SketchId = sketchId,
            FeatureId = featureId,
            PublicView = publicView,
            AdapterState = planState
        };
    }

    private M3ExecutionResult ExecuteSketchCornerPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M3SketchCornerPlanState plan = prepared == null ? null : prepared.AdapterState as M3SketchCornerPlanState;
        if (plan == null || prepared.Kind != "edit_sketch_corner")
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null,
                "INVALID_PLAN", "sketch corner plan does not contain retained NX adapter state");
        BindingState state;
        try { state = RequireState(binding); }
        catch (M2ModelException ex)
        {
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, ex.Code, ex.Message);
        }
        if (!Object.ReferenceEquals(state, plan.BindingState) || !Object.ReferenceEquals(binding, plan.Binding) ||
            !String.Equals(plan.RevisionId, prepared.RevisionId, StringComparison.Ordinal) ||
            !String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "sketch corner plan belongs to a different binding or revision");
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_BINDING", bindingReason);
        try
        {
            RequireWritableSimplePart(state.WorkPart);
            RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
            if (state.Session.ActiveSketch != null)
                return M3Execution("UNCHANGED", binding.RevisionId, null, "NX_BUSY", "a Sketch is active in NX before sketch corner execution");
            SnapshotResult current = Capture(binding, state);
            if (!String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(CanonicalSemanticSnapshot(current.Snapshot), plan.SemanticBefore, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "semantic model state changed after sketch corner preparation");
            Dictionary<string, CornerGeometrySnapshot> geometryNow = CaptureSketchCornerGeometry(state.WorkPart);
            if (!CornerGeometryMapsEqual(plan.GeometryBefore, geometryNow))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "native geometry changed after sketch corner preparation");
            Dictionary<string, CornerConstraintSnapshot> constraintsNow = CaptureSketchCornerConstraints(plan.Sketch);
            if (!CornerConstraintMapsEqual(plan.ConstraintsBefore, constraintsNow))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "sketch constraints changed after sketch corner preparation");
            Dictionary<string, object> bodyEvidence;
            if (!BodyMeasuresEqual(plan.BodiesBefore, CaptureM3BodyMeasures(state.WorkPart), out bodyEvidence))
                return M3Execution("UNCHANGED", binding.RevisionId, bodyEvidence, "STALE_GEOMETRY", "body measurements changed after sketch corner preparation");
            Sketch currentSketch = FindSketch(state.WorkPart, plan.SketchId);
            if (currentSketch == null ||
                !String.Equals(RequiredJournalId(currentSketch, "sketch"), plan.SketchId, StringComparison.Ordinal) ||
                !String.Equals(currentSketch.Tag.ToString(), plan.SketchTag, StringComparison.Ordinal) ||
                currentSketch.Feature == null || !String.Equals(currentSketch.Feature.Tag.ToString(), plan.FeatureTag, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "target Sketch or feature identity changed after preparation");
        }
        catch (M2ModelException ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "MODEL_READ_FAILED",
                "could not revalidate sketch corner preconditions (" + ex.GetType().Name + ")");
        }

        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F07 sketch corner modification"); }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "UPDATE_FAILED", "could not create an NX update mark (" + ex.GetType().Name + ")");
        }

        Exception operationException = null;
        bool updateCallAttempted = false;
        try
        {
            ExecuteSketchCornerNative(state, plan);
            updateCallAttempted = true;
            int updateErrors = state.Session.UpdateManager.DoUpdate(mark);
            plan.NativeOperationEvidence["update_error_count"] = updateErrors;
            if (updateErrors != 0)
                throw new M2ModelException("UPDATE_FAILED", "NX update reported errors after the native sketch corner call");

            SnapshotResult afterCapture = Capture(binding, state);
            plan.GeometryAfter = CaptureSketchCornerGeometry(state.WorkPart);
            plan.ConstraintsAfter = CaptureSketchCornerConstraints(plan.Sketch);
            plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart);
            plan.SemanticAfter = CanonicalSemanticSnapshot(afterCapture.Snapshot);
            plan.HistoryAfter = M2Json.CanonicalText(HistoryProjection(afterCapture.Snapshot));
            Dictionary<string, object> checks;
            Dictionary<string, object> measured;
            Dictionary<string, object> nonTargetEvidence;
            Dictionary<string, object> constraintEvidence;
            Dictionary<string, object> bodyEvidence;
            Dictionary<string, object> historyEvidence;
            bool verified = VerifySketchCornerGeometryNow(state, plan, plan.GeometryAfter,
                out checks, out measured, out nonTargetEvidence, out constraintEvidence, out bodyEvidence, out historyEvidence);
            Dictionary<string, object> sketchStatusEvidence = CaptureCornerSketchStatus(plan.Sketch);
            plan.NativeOperationEvidence["sketch_status_after_execute"] = sketchStatusEvidence;
            bool sketchStatusVerified = Boolean.Equals(sketchStatusEvidence["acceptable"], true);
            checks["sketch_solver_status"] = VerificationCheck(sketchStatusVerified ? "VERIFIED" : "MISMATCH", sketchStatusEvidence);
            if (!verified)
            {
                plan.AppliedEvidence = BuildSketchCornerEvidence(plan, measured, checks,
                    nonTargetEvidence, constraintEvidence, bodyEvidence, historyEvidence);
                throw new M2ModelException("CORNER_VERIFY_FAILED", "native corner operation did not match the requested trimmed geometry or preserved model state");
            }
            if (!sketchStatusVerified)
            {
                plan.AppliedEvidence = BuildSketchCornerEvidence(plan, measured, checks,
                    nonTargetEvidence, constraintEvidence, bodyEvidence, historyEvidence);
                throw new M2ModelException("CORNER_SKETCH_STATUS_INVALID", "NX Sketch solver status is not an accepted under-constrained or well-constrained state");
            }

            plan.AppliedEvidence = BuildSketchCornerEvidence(plan, measured, checks,
                nonTargetEvidence, constraintEvidence, bodyEvidence, historyEvidence);
            Dictionary<string, object> result = BuildSketchCornerExecutionResult(plan, binding.RevisionId,
                "APPLIED", true, plan.AppliedEvidence);
            plan.Execution = M3Execution("APPLIED", binding.RevisionId, result, null, null);
            return plan.Execution;
        }
        catch (Exception ex)
        {
            operationException = ex;
            plan.NativeOperationEvidence["native_exception_type"] = ex.GetType().FullName;
            plan.NativeOperationEvidence["native_exception_message"] = SafeCornerExceptionMessage(ex);
            M3ExecutionResult restored = RestoreSketchCornerPlan(binding, state, plan, mark, operationException, updateCallAttempted);
            plan.Execution = restored;
            return restored;
        }
        finally
        {
            TryDeleteUndoMark(state.Session, mark);
        }
    }

    private M3VerificationResult VerifySketchCornerPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M3SketchCornerPlanState plan = prepared == null ? null : prepared.AdapterState as M3SketchCornerPlanState;
        if (plan == null || plan.Execution == null || plan.Execution.State != "APPLIED")
            return M3VerificationFailure(binding == null ? null : binding.RevisionId,
                "sketch corner verification requires a successfully applied plan");
        BindingState state = RequireState(binding);
        string bindingReason;
        if (!Object.ReferenceEquals(state, plan.BindingState) || !Object.ReferenceEquals(binding, plan.Binding) ||
            !IsBindingCurrent(binding, out bindingReason))
            return M3VerificationFailure(binding == null ? null : binding.RevisionId,
                "NX binding changed before sketch corner verification");

        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> measured = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> nonTargetEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> constraintEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> bodyEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bool geometryVerified = false;
        bool constraintsVerified = false;
        bool nonTargetsVerified = false;
        bool sketchOwned = false;
        bool sketchStatusVerified = false;
        bool historyVerified = false;
        bool bodiesVerified = false;
        try
        {
            SnapshotResult captured = Capture(binding, state);
            Dictionary<string, CornerGeometrySnapshot> geometryNow = CaptureSketchCornerGeometry(state.WorkPart);
            Dictionary<string, CornerConstraintSnapshot> constraintsNow = CaptureSketchCornerConstraints(plan.Sketch);
            List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart);
            Dictionary<string, object> ignoredChecks;
            bool geometricReadback = VerifySketchCornerGeometryNow(state, plan, geometryNow,
                out ignoredChecks, out measured, out nonTargetEvidence, out constraintEvidence,
                out bodyEvidence, out historyEvidence);
            geometryVerified = geometricReadback && CornerGeometryMapsEqual(plan.GeometryAfter, geometryNow);
            Dictionary<string, object> sketchStatusEvidence = CaptureCornerSketchStatus(plan.Sketch);
            measured["sketch_status_after_verify"] = sketchStatusEvidence;
            sketchStatusVerified = Boolean.Equals(sketchStatusEvidence["acceptable"], true);
            constraintsVerified = CornerConstraintMapsEqual(plan.ConstraintsAfter, constraintsNow);
            Sketch currentSketch = FindSketch(state.WorkPart, plan.SketchId);
            Curve currentCurve1 = currentSketch == null ? null : FindCornerCurveByTag(currentSketch, plan.Curve1Tag);
            Curve currentCurve2 = currentSketch == null ? null : FindCornerCurveByTag(currentSketch, plan.Curve2Tag);
            Feature currentSketchFeature = currentSketch == null ? null : currentSketch.Feature;
            sketchOwned = currentSketch != null && currentCurve1 != null && currentCurve2 != null &&
                String.Equals(currentSketch.Tag.ToString(), plan.SketchTag, StringComparison.Ordinal) &&
                String.Equals(RequiredJournalId(currentSketch, "sketch"), plan.SketchId, StringComparison.Ordinal) &&
                currentSketch.IsSketchGeometry(currentCurve1) && currentSketch.IsSketchGeometry(currentCurve2);
            nonTargetsVerified = VerifyCornerNonTargets(plan, geometryNow, out nonTargetEvidence);
            historyVerified = String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(captured.Snapshot), StringComparison.Ordinal) &&
                String.Equals(plan.HistoryAfter, M2Json.CanonicalText(HistoryProjection(captured.Snapshot)), StringComparison.Ordinal) &&
                String.Equals(plan.FeatureTag, currentSketchFeature == null ? null : currentSketchFeature.Tag.ToString(), StringComparison.Ordinal) &&
                FeatureTagsMatch(plan.FeatureTagsBefore, CaptureFeatureTags(state.WorkPart));
            Dictionary<string, object> bodyCompare;
            bodiesVerified = BodyMeasuresEqual(plan.BodiesAfter, bodiesNow, out bodyCompare);
            bodyEvidence["after_verify"] = bodyCompare;
            object previousConstraints;
            object currentConstraints;
            if (plan.AppliedEvidence != null && plan.AppliedEvidence.TryGetValue("constraints", out previousConstraints))
                constraintEvidence["after_execute"] = previousConstraints;
            currentConstraints = CornerConstraintPublicRecords(constraintsNow);
            constraintEvidence["after_verify"] = currentConstraints;
            constraintEvidence["preserved_original_definitions"] = constraintsVerified;
        }
        catch (Exception ex)
        {
            measured["verification_exception"] = ex.GetType().FullName;
            measured["verification_exception_message"] = SafeCornerExceptionMessage(ex);
        }

        checks.Add("geometry", VerificationCheck(geometryVerified ? "VERIFIED" : "MISMATCH", measured));
        checks.Add("sketch_solver_status", VerificationCheck(sketchStatusVerified ? "VERIFIED" : "MISMATCH", measured));
        checks.Add("constraints", VerificationCheck(constraintsVerified ? "VERIFIED" : "MISMATCH", constraintEvidence));
        checks.Add("sketch_ownership", VerificationCheck(sketchOwned ? "VERIFIED" : "MISMATCH",
            new Dictionary<string, object>(StringComparer.Ordinal) { { "sketch_id", plan.SketchId }, { "sketch_tag", plan.SketchTag } }));
        checks.Add("non_target_geometry", VerificationCheck(nonTargetsVerified ? "VERIFIED" : "MISMATCH", nonTargetEvidence));
        checks.Add("history", VerificationCheck(historyVerified ? "VERIFIED" : "MISMATCH", historyEvidence));
        checks.Add("bodies", VerificationCheck(bodiesVerified ? "VERIFIED" : "MISMATCH", bodyEvidence));

        bool verified = geometryVerified && sketchStatusVerified && constraintsVerified && sketchOwned && nonTargetsVerified && historyVerified && bodiesVerified;
        Dictionary<string, object> evidence = BuildSketchCornerEvidence(plan, measured, checks,
            nonTargetEvidence, constraintEvidence, bodyEvidence, historyEvidence);
        Dictionary<string, object> result = BuildSketchCornerExecutionResult(plan, binding.RevisionId,
            plan.Execution.State, verified, evidence);
        return new M3VerificationResult
        {
            Verified = verified,
            RevisionId = binding.RevisionId,
            Checks = checks,
            Result = result,
            ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified ? null : "Live sketch corner geometry, solver status, constraints, history, sketch ownership, or non-target geometry did not match the applied plan."
        };
    }

    private static Dictionary<string, object> CaptureCornerSketchStatus(Sketch sketch)
    {
        if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "target Sketch is unavailable during solver status readback");
        int degreesOfFreedom;
        Sketch.Status status = sketch.GetStatus(out degreesOfFreedom);
        bool acceptable = (status == Sketch.Status.UnderConstrained && degreesOfFreedom >= 0) ||
            (status == Sketch.Status.WellConstrained && degreesOfFreedom == 0);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", status.ToString() },
            { "degrees_of_freedom", degreesOfFreedom },
            { "acceptable", acceptable }
        };
    }

    private void ExecuteSketchCornerNative(BindingState state, M3SketchCornerPlanState plan)
    {
        bool activatedHere = false;
        Exception operationException = null;
        try
        {
            if (!IsSketchActive(state.Session, plan.Sketch))
            {
                if (state.Session.ActiveSketch != null)
                    throw new M2ModelException("NX_BUSY", "a different Sketch became active before F07 execution");
                plan.Sketch.Activate(Sketch.ViewReorient.False);
                activatedHere = true;
            }

            if (plan.Request.Kind == "fillet")
            {
                plan.NativeOperationEvidence["api"] = "NXOpen.Sketch.Fillet";
                plan.NativeOperationEvidence["native_mode"] = "Sketch.Fillet";
                plan.NativeOperationEvidence["native_operation_called"] = true;
                plan.NativeOperationEvidence["curve_order_tags"] = new List<object> {
                    plan.Curve1Tag, plan.Curve2Tag };
                V2SketchFilletResult native = V2SketchCornerFactory.CreateFillet(plan.Sketch,
                    plan.Curve1, plan.Curve2, plan.FilletHelpPoint1, plan.FilletHelpPoint2,
                    plan.Request.RadiusMm, false);
                if (native == null || native.Geometry == null)
                    throw new M2ModelException("NATIVE_FILLET_FAILED", "NX did not return a native Sketch fillet Arc");
                plan.NativeFilletArcTag = native.Geometry.Tag.ToString();
                plan.NativeOperationEvidence["native_constraints_returned_count"] = native.NativeConstraintsReturnedCount;
                plan.NativeOperationEvidence["native_arc_tag"] = plan.NativeFilletArcTag;
            }
            else
            {
                Line line1 = plan.Curve1 as Line;
                Line line2 = plan.Curve2 as Line;
                if (line1 == null || line2 == null)
                    throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "native sketch chamfer requires two Lines");
                plan.NativeOperationEvidence["api"] = "NXOpen.SketchChamferBuilder";
                plan.NativeOperationEvidence["native_mode"] = plan.ChamferMode.ToString();
                plan.NativeOperationEvidence["requested_definition"] = SketchCornerDefinition(plan);
                plan.NativeOperationEvidence["help_point_strategy"] = "curve2_biased_ray_anchor";
                plan.NativeOperationEvidence["help_point_mm"] = Point(plan.ChamferHelpPoint);
                plan.NativeOperationEvidence["help_point_epsilon_mm"] = plan.ChamferHelpPointEpsilonMm;
                plan.NativeOperationEvidence["help_point_distance_to_curve1_support_mm"] =
                    plan.ChamferHelpPointDistanceToCurve1SupportMm;
                plan.NativeOperationEvidence["help_point_distance_to_curve2_support_mm"] =
                    plan.ChamferHelpPointDistanceToCurve2SupportMm;
                plan.NativeOperationEvidence["native_operation_called"] = true;
                V2SketchChamferResult native = V2SketchCornerFactory.CreateChamfer(state.WorkPart,
                    plan.Sketch, line1, line2, plan.ChamferMode, plan.Distance1Mm,
                    plan.Distance2Mm, 0.0, plan.ChamferHelpPoint);
                if (native == null)
                    throw new M2ModelException("NATIVE_CHAMFER_FAILED", "NX sketch chamfer returned no builder result");
                plan.NativeOperationEvidence["builder_option_before_commit"] = native.ChamferOptionBeforeCommit;
                plan.NativeOperationEvidence["builder_option_after_commit"] = native.ChamferOptionAfterCommit;
                plan.NativeOperationEvidence["curve_order_tags_before_commit"] = StringListObjects(native.CurvesToChamferTagStringsBeforeCommit);
                plan.NativeOperationEvidence["curve_order_tags_after_commit"] = StringListObjects(native.CurvesToChamferTagStringsAfterCommit);
                plan.NativeOperationEvidence["distance1_rhs_before_commit"] = native.Distance1RightHandSideBeforeCommit;
                plan.NativeOperationEvidence["distance1_rhs_after_commit"] = native.Distance1RightHandSideAfterCommit;
                plan.NativeOperationEvidence["distance2_rhs_before_commit"] = native.Distance2RightHandSideBeforeCommit;
                plan.NativeOperationEvidence["distance2_rhs_after_commit"] = native.Distance2RightHandSideAfterCommit;
                plan.NativeOperationEvidence["angle_rhs_before_commit"] = native.AngleRightHandSideBeforeCommit;
                plan.NativeOperationEvidence["angle_rhs_after_commit"] = native.AngleRightHandSideAfterCommit;
                plan.NativeOperationEvidence["post_commit_readback_error"] = native.PostCommitReadbackError;
                plan.NativeOperationEvidence["dimension_creation_flags"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "distance1", false }, { "distance2", false }, { "angle", false }
                };
                if (native.CommittedObjects != null)
                {
                    List<object> committed = new List<object>();
                    foreach (NXObject item in native.CommittedObjects)
                        if (item != null) committed.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                            { { "tag", item.Tag.ToString() }, { "type", item.GetType().Name } });
                    plan.NativeOperationEvidence["builder_committed_objects"] = committed;
                }
            }
            if (activatedHere)
            {
                plan.Sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
                activatedHere = false;
            }
        }
        catch (Exception ex)
        {
            operationException = ex;
        }

        Exception deactivationException = null;
        if (activatedHere)
        {
            try
            {
                if (IsSketchActive(state.Session, plan.Sketch))
                    plan.Sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
                activatedHere = false;
            }
            catch (Exception ex)
            {
                deactivationException = ex;
            }
        }

        if (operationException != null && deactivationException != null)
            throw new AggregateException("F07 native sketch operation and sketch deactivation both failed.",
                operationException, deactivationException);
        if (operationException != null)
            ExceptionDispatchInfo.Capture(operationException).Throw();
        if (deactivationException != null)
            ExceptionDispatchInfo.Capture(deactivationException).Throw();
    }

    private bool VerifySketchCornerGeometryNow(BindingState state, M3SketchCornerPlanState plan,
        Dictionary<string, CornerGeometrySnapshot> geometryNow, out Dictionary<string, object> checks,
        out Dictionary<string, object> measured, out Dictionary<string, object> nonTargetEvidence,
        out Dictionary<string, object> constraintEvidence, out Dictionary<string, object> bodyEvidence,
        out Dictionary<string, object> historyEvidence)
    {
        checks = new Dictionary<string, object>(StringComparer.Ordinal);
        measured = new Dictionary<string, object>(StringComparer.Ordinal);
        nonTargetEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        constraintEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bodyEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal);

        NXObject curve1Object = FindSketchGeometryByTag(plan.Sketch, plan.Curve1Tag);
        NXObject curve2Object = FindSketchGeometryByTag(plan.Sketch, plan.Curve2Tag);
        Curve curve1 = curve1Object as Curve;
        Curve curve2 = curve2Object as Curve;
        bool targetTypesMatch = curve1 != null && curve2 != null &&
            String.Equals(curve1.GetType().Name, plan.Curve1Type, StringComparison.Ordinal) &&
            String.Equals(curve2.GetType().Name, plan.Curve2Type, StringComparison.Ordinal);
        bool sketchOwned = targetTypesMatch && plan.Sketch.IsSketchGeometry(curve1) && plan.Sketch.IsSketchGeometry(curve2) &&
            String.Equals(plan.Sketch.Tag.ToString(), plan.SketchTag, StringComparison.Ordinal) &&
            String.Equals(RequiredJournalId(plan.Sketch, "sketch"), plan.SketchId, StringComparison.Ordinal);
        measured["sketch_id"] = plan.SketchId;
        measured["sketch_tag"] = plan.SketchTag;
        measured["target_curve1_tag"] = plan.Curve1Tag;
        measured["target_curve2_tag"] = plan.Curve2Tag;
        measured["target_types_match"] = targetTypesMatch;
        measured["targets_owned_by_sketch"] = sketchOwned;

        bool endpointsValid = false;
        bool noExtension = false;
        bool dimensionMatch = false;
        bool tangentMatch = true;
        bool connectivityMatch = false;
        bool targetShapesPreserved = false;
        Point3d actualCut1 = new Point3d();
        Point3d actualCut2 = new Point3d();
        NXObject newCornerCurveObject = null;
        Dictionary<string, object> curve1ShapeEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> curve2ShapeEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        List<string> newVirtualPointTags = new List<string>();
        List<string> newCurveTags = new List<string>();

        if (targetTypesMatch)
        {
            actualCut1 = CurveEndpoint(curve1, plan.Request.Curve1End);
            actualCut2 = CurveEndpoint(curve2, plan.Request.Curve2End);
            double actualLength1 = curve1.GetLength();
            double actualLength2 = curve2.GetLength();
            bool lengthsAndCutsValid = IsFinite(actualLength1) && IsFinite(actualLength2) &&
                actualLength1 < plan.Curve1LengthBefore - SketchCornerPositionToleranceMm &&
                actualLength2 < plan.Curve2LengthBefore - SketchCornerPositionToleranceMm &&
                PointDistance(plan.Corner, actualCut1) > SketchCornerPositionToleranceMm &&
                PointDistance(plan.Corner, actualCut2) > SketchCornerPositionToleranceMm;
            Point3d remote1 = CurveEndpoint(curve1, plan.Request.Curve1End == "start" ? "end" : "start");
            Point3d remote2 = CurveEndpoint(curve2, plan.Request.Curve2End == "start" ? "end" : "start");
            endpointsValid = PointDistance(remote1, plan.Curve1RemoteBefore) <= SketchCornerPositionToleranceMm &&
                PointDistance(remote2, plan.Curve2RemoteBefore) <= SketchCornerPositionToleranceMm;
            CornerGeometrySnapshot curve1Before;
            CornerGeometrySnapshot curve2Before;
            bool hasCurve1Before = plan.GeometryBefore.TryGetValue(plan.Curve1Tag, out curve1Before);
            bool hasCurve2Before = plan.GeometryBefore.TryGetValue(plan.Curve2Tag, out curve2Before);
            bool curve1ShapePreserved = hasCurve1Before && VerifyTargetCurveShape(curve1, curve1Before,
                plan.Request.Curve1End, actualCut1, remote1, plan.Corner, out curve1ShapeEvidence);
            bool curve2ShapePreserved = hasCurve2Before && VerifyTargetCurveShape(curve2, curve2Before,
                plan.Request.Curve2End, actualCut2, remote2, plan.Corner, out curve2ShapeEvidence);
            targetShapesPreserved = curve1ShapePreserved && curve2ShapePreserved;
            noExtension = lengthsAndCutsValid && targetShapesPreserved;
            measured["curve1_endpoint_after_mm"] = Point(actualCut1);
            measured["curve2_endpoint_after_mm"] = Point(actualCut2);
            measured["curve1_remote_after_mm"] = Point(remote1);
            measured["curve2_remote_after_mm"] = Point(remote2);
            measured["curve1_length_before_mm"] = plan.Curve1LengthBefore;
            measured["curve2_length_before_mm"] = plan.Curve2LengthBefore;
            measured["curve1_length_after_mm"] = actualLength1;
            measured["curve2_length_after_mm"] = actualLength2;
            measured["curve1_target_shape"] = curve1ShapeEvidence;
            measured["curve2_target_shape"] = curve2ShapeEvidence;
            measured["target_shapes_preserved"] = targetShapesPreserved;
            measured["no_extension"] = noExtension;
            measured["remote_endpoints_preserved"] = endpointsValid;
        }
        else
        {
            measured["curve1_target_shape"] = curve1ShapeEvidence;
            measured["curve2_target_shape"] = curve2ShapeEvidence;
            measured["target_shapes_preserved"] = false;
        }

        foreach (KeyValuePair<string, CornerGeometrySnapshot> pair in geometryNow)
        {
            if (plan.GeometryBefore.ContainsKey(pair.Key)) continue;
            if (pair.Value.ObjectType == "SketchVirtualPoint") newVirtualPointTags.Add(pair.Key);
            else if (pair.Value.ObjectType == "Line" || pair.Value.ObjectType == "Arc") newCurveTags.Add(pair.Key);
        }
        newVirtualPointTags.Sort(StringComparer.Ordinal);
        newCurveTags.Sort(StringComparer.Ordinal);
        for (int i = 0; i < newCurveTags.Count; i++)
        {
            NXObject candidate = FindSketchGeometryByTag(plan.Sketch, newCurveTags[i]);
            if (candidate != null && candidate is Curve)
            {
                if (newCornerCurveObject != null) newCornerCurveObject = null;
                else newCornerCurveObject = candidate;
            }
        }
        string expectedNewCurveType = plan.Request.Kind == "fillet" ? "Arc" : "Line";
        bool addedCurveCountMatch = newCurveTags.Count == 1 && newCornerCurveObject != null &&
            String.Equals(newCornerCurveObject.GetType().Name, expectedNewCurveType, StringComparison.Ordinal) &&
            plan.Sketch.IsSketchGeometry(newCornerCurveObject as Curve);
        if (plan.Request.Kind == "fillet")
        {
            addedCurveCountMatch = addedCurveCountMatch &&
                String.Equals(newCurveTags.Count == 1 ? newCurveTags[0] : null,
                    plan.NativeFilletArcTag, StringComparison.Ordinal);
        }

        bool virtualPointsAtCorner = true;
        foreach (string tag in newVirtualPointTags)
        {
            SketchVirtualPoint virtualPoint = FindSketchGeometryByTag(plan.Sketch, tag) as SketchVirtualPoint;
            if (virtualPoint == null || PointDistance(virtualPoint.Coordinates, plan.Corner) > SketchCornerPositionToleranceMm ||
                !plan.Sketch.IsSketchGeometry(virtualPoint)) virtualPointsAtCorner = false;
        }
        bool virtualPointEvidence = newVirtualPointTags.Count <= 1 && virtualPointsAtCorner;
        measured["new_curve_tags"] = StringListObjects(newCurveTags.ToArray());
        measured["new_virtual_point_tags"] = StringListObjects(newVirtualPointTags.ToArray());
        measured["new_curve_count_match"] = addedCurveCountMatch;
        measured["virtual_points_at_corner"] = virtualPointEvidence;
        measured["new_corner_curve_type"] = newCornerCurveObject == null ? null : newCornerCurveObject.GetType().Name;

        if (plan.Request.Kind == "chamfer" && targetTypesMatch && newCornerCurveObject is Line)
        {
            Line chamfer = (Line)newCornerCurveObject;
            Point3d start = chamfer.StartPoint;
            Point3d end = chamfer.EndPoint;
            bool normalOrder = PointDistance(start, actualCut1) <= SketchCornerPositionToleranceMm &&
                PointDistance(end, actualCut2) <= SketchCornerPositionToleranceMm;
            bool reverseOrder = PointDistance(end, actualCut1) <= SketchCornerPositionToleranceMm &&
                PointDistance(start, actualCut2) <= SketchCornerPositionToleranceMm;
            connectivityMatch = normalOrder || reverseOrder;
            double measuredDistance1 = PointDistance(plan.Corner, actualCut1);
            double measuredDistance2 = PointDistance(plan.Corner, actualCut2);
            Vector3d toCorner = Subtract(plan.Corner, actualCut1);
            Vector3d alongChamfer = Subtract(actualCut2, actualCut1);
            double measuredAngleDeg = VectorAngleDegrees(toCorner, alongChamfer);
            double distanceTolerance = SketchCornerPositionToleranceMm;
            double angleToleranceDeg = 1.0e-6;
            dimensionMatch = Math.Abs(measuredDistance1 - plan.Distance1Mm) <= distanceTolerance &&
                Math.Abs(measuredDistance2 - plan.Distance2Mm) <= distanceTolerance &&
                (!plan.IsDistanceAngle || Math.Abs(measuredAngleDeg - plan.AngleDeg) <= angleToleranceDeg);
            measured["corner_mm"] = Point(plan.Corner);
            measured["curve1_cut_point_mm"] = Point(actualCut1);
            measured["curve2_cut_point_mm"] = Point(actualCut2);
            measured["chamfer_line_start_mm"] = Point(start);
            measured["chamfer_line_end_mm"] = Point(end);
            measured["distance1_mm"] = measuredDistance1;
            measured["distance2_mm"] = measuredDistance2;
            measured["angle_deg"] = measuredAngleDeg;
            measured["requested_angle_deg"] = plan.IsDistanceAngle ? (object)plan.AngleDeg : null;
            measured["calculated_distance2_mm"] = plan.IsDistanceAngle ? (object)plan.Distance2Mm : null;
            measured["chamfer_endpoints_connected"] = connectivityMatch;
            measured["dimensions_match"] = dimensionMatch;
        }
        else if (plan.Request.Kind == "fillet" && targetTypesMatch && newCornerCurveObject is Arc)
        {
            Arc fillet = (Arc)newCornerCurveObject;
            Point3d filletStart = CurveEndpoint(fillet, "start");
            Point3d filletEnd = CurveEndpoint(fillet, "end");
            bool startTo1EndTo2 = PointDistance(filletStart, actualCut1) <= SketchCornerPositionToleranceMm &&
                PointDistance(filletEnd, actualCut2) <= SketchCornerPositionToleranceMm;
            bool startTo2EndTo1 = PointDistance(filletStart, actualCut2) <= SketchCornerPositionToleranceMm &&
                PointDistance(filletEnd, actualCut1) <= SketchCornerPositionToleranceMm;
            connectivityMatch = startTo1EndTo2 || startTo2EndTo1;
            bool tangent1 = false, tangent2 = false;
            if (startTo1EndTo2)
            {
                tangent1 = TangentVectorsMatch(CurveTangentAwayFromEndpoint(curve1, plan.Request.Curve1End),
                    CurveTangentAwayFromEndpoint(fillet, "start"));
                tangent2 = TangentVectorsMatch(CurveTangentAwayFromEndpoint(curve2, plan.Request.Curve2End),
                    CurveTangentAwayFromEndpoint(fillet, "end"));
            }
            else if (startTo2EndTo1)
            {
                tangent1 = TangentVectorsMatch(CurveTangentAwayFromEndpoint(curve1, plan.Request.Curve1End),
                    CurveTangentAwayFromEndpoint(fillet, "end"));
                tangent2 = TangentVectorsMatch(CurveTangentAwayFromEndpoint(curve2, plan.Request.Curve2End),
                    CurveTangentAwayFromEndpoint(fillet, "start"));
            }
            tangentMatch = tangent1 && tangent2;
            double radius = fillet.Radius;
            dimensionMatch = Math.Abs(radius - plan.Request.RadiusMm) <= SketchCornerPositionToleranceMm;
            measured["fillet_center_mm"] = Point(fillet.CenterPoint);
            measured["fillet_start_mm"] = Point(filletStart);
            measured["fillet_end_mm"] = Point(filletEnd);
            measured["radius_mm"] = radius;
            measured["requested_radius_mm"] = plan.Request.RadiusMm;
            measured["fillet_endpoints_connected"] = connectivityMatch;
            measured["tangent_at_curve1"] = tangent1;
            measured["tangent_at_curve2"] = tangent2;
            measured["tangent_match"] = tangentMatch;
        }

        Dictionary<string, CornerConstraintSnapshot> constraintsNow = CaptureSketchCornerConstraints(plan.Sketch);
        bool constraintsPreserved = CornerConstraintMapsPreserve(plan.ConstraintsBefore, constraintsNow);
        constraintEvidence["before"] = CornerConstraintPublicRecords(plan.ConstraintsBefore);
        constraintEvidence["after"] = CornerConstraintPublicRecords(constraintsNow);
        constraintEvidence["preserved_original_definitions"] = constraintsPreserved;
        constraintEvidence["native_constraints_returned_count"] = plan.NativeOperationEvidence.ContainsKey("native_constraints_returned_count")
            ? plan.NativeOperationEvidence["native_constraints_returned_count"] : (object)0;

        bool sketchFeatureSame = plan.Sketch.Feature != null &&
            String.Equals(plan.Sketch.Feature.Tag.ToString(), plan.FeatureTag, StringComparison.Ordinal);
        bool ownershipVerified = sketchOwned && addedCurveCountMatch && virtualPointEvidence && sketchFeatureSame;
        bool geometryVerified = targetTypesMatch && endpointsValid && noExtension && addedCurveCountMatch &&
            targetShapesPreserved && connectivityMatch && dimensionMatch && tangentMatch && virtualPointEvidence;
        bool nonTargetsVerified = VerifyCornerNonTargets(plan, geometryNow, out nonTargetEvidence);
        List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart);
        Dictionary<string, object> bodyCompare;
        bool bodiesVerified = BodyMeasuresEqual(plan.BodiesBefore, bodiesNow, out bodyCompare);
        bodyEvidence["before"] = BodyMeasureRecords(plan.BodiesBefore, null);
        bodyEvidence["after"] = BodyMeasureRecords(bodiesNow, null);
        bodyEvidence["measurements_equal"] = bodiesVerified;
        bodyEvidence["comparison"] = bodyCompare;
        bool historyVerified = String.Equals(plan.HistoryBefore, plan.HistoryAfter, StringComparison.Ordinal) &&
            FeatureTagsMatch(plan.FeatureTagsBefore, CaptureFeatureTags(state.WorkPart)) && sketchFeatureSame;
        historyEvidence["before"] = plan.HistoryBefore;
        historyEvidence["after"] = plan.HistoryAfter;
        historyEvidence["feature_tags_before"] = StringListObjects(plan.FeatureTagsBefore.ToArray());
        historyEvidence["feature_tags_after"] = StringListObjects(CaptureFeatureTags(state.WorkPart).ToArray());
        historyEvidence["history_unchanged"] = historyVerified;
        checks.Add("geometry", VerificationCheck(geometryVerified ? "VERIFIED" : "MISMATCH", measured));
        checks.Add("constraints", VerificationCheck(constraintsPreserved ? "VERIFIED" : "MISMATCH", constraintEvidence));
        checks.Add("sketch_ownership", VerificationCheck(ownershipVerified ? "VERIFIED" : "MISMATCH",
            new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", plan.SketchId }, { "sketch_tag", plan.SketchTag },
                { "sketch_feature_tag", plan.FeatureTag }, { "targets_and_new_curve_owned", ownershipVerified }
            }));
        checks.Add("non_target_geometry", VerificationCheck(nonTargetsVerified ? "VERIFIED" : "MISMATCH", nonTargetEvidence));
        checks.Add("history", VerificationCheck(historyVerified ? "VERIFIED" : "MISMATCH", historyEvidence));
        checks.Add("bodies", VerificationCheck(bodiesVerified ? "VERIFIED" : "MISMATCH", bodyEvidence));
        return geometryVerified && constraintsPreserved && ownershipVerified && nonTargetsVerified && historyVerified && bodiesVerified;
    }

    private static Dictionary<string, object> BuildSketchCornerEvidence(M3SketchCornerPlanState plan,
        Dictionary<string, object> measured, Dictionary<string, object> checks,
        Dictionary<string, object> nonTargetEvidence, Dictionary<string, object> constraintEvidence,
        Dictionary<string, object> bodyEvidence, Dictionary<string, object> historyEvidence)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence["requested_definition"] = SketchCornerDefinition(plan);
        evidence["native_mode"] = SketchCornerNativeMode(plan);
        evidence["native_operation"] = CloneCornerDictionary(plan.NativeOperationEvidence);
        evidence["native_operation_called"] = plan.NativeOperationEvidence.ContainsKey("native_operation_called") &&
            plan.NativeOperationEvidence["native_operation_called"] is bool &&
            (bool)plan.NativeOperationEvidence["native_operation_called"];
        evidence["measured_geometry"] = measured;
        evidence["checks"] = checks;
        evidence["non_target_geometry"] = nonTargetEvidence;
        evidence["constraints"] = constraintEvidence;
        evidence["bodies"] = bodyEvidence;
        evidence["history"] = historyEvidence;
        evidence["profile_analysis"] = "NOT_PERFORMED_BY_F07";
        evidence["scope"] = "local corner geometry, trimmed endpoints, tangent/connectivity, target sketch ownership, preserved constraints, non-target geometry, history and body readback; no whole-profile validity claim";
        return evidence;
    }

    private static Dictionary<string, object> BuildSketchCornerExecutionResult(M3SketchCornerPlanState plan,
        string revisionId, string mutationState, bool verified, Dictionary<string, object> evidence)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result["kind"] = "edit_sketch_corner";
        result["sketch_id"] = plan.SketchId;
        result["revision_id"] = revisionId;
        result["operation"] = plan.Request.Kind;
        result["mutation_state"] = mutationState;
        result["verified"] = verified;
        result["requested_definition"] = SketchCornerDefinition(plan);
        result["native_mode"] = SketchCornerNativeMode(plan);
        result["curve1_id"] = plan.Request.Curve1Id;
        result["curve1_tag"] = plan.Curve1Tag;
        result["curve1_end"] = plan.Request.Curve1End;
        result["curve2_id"] = plan.Request.Curve2Id;
        result["curve2_tag"] = plan.Curve2Tag;
        result["curve2_end"] = plan.Request.Curve2End;
        result["evidence"] = evidence;
        if (evidence != null)
        {
            object measured;
            if (evidence.TryGetValue("measured_geometry", out measured)) result["measured_geometry"] = measured;
        }
        return result;
    }

    private static string SketchCornerDefinition(M3SketchCornerPlanState plan)
    {
        if (plan.Request.Kind == "fillet") return "fillet";
        if (plan.IsDistanceAngle) return "distance_angle";
        return Math.Abs(plan.Distance1Mm - plan.Distance2Mm) <= 1.0e-12 ? "equal_distance" : "two_distances";
    }

    private static string SketchCornerNativeMode(M3SketchCornerPlanState plan)
    {
        if (plan.Request.Kind == "fillet") return "Sketch.Fillet";
        return plan.ChamferMode.ToString();
    }

    private M3ExecutionResult RestoreSketchCornerPlan(M2ModelBinding binding, BindingState state,
        M3SketchCornerPlanState plan, Session.UndoMarkId mark, Exception operationException, bool updateCall)
    {
        Dictionary<string, object> restore = new Dictionary<string, object>(StringComparer.Ordinal);
        restore["native_operation_called"] = plan.NativeOperationEvidence.ContainsKey("native_operation_called") &&
            plan.NativeOperationEvidence["native_operation_called"] is bool &&
            (bool)plan.NativeOperationEvidence["native_operation_called"];
        restore["exception_type"] = operationException == null ? null : operationException.GetType().FullName;
        restore["exception_code"] = CornerExceptionCode(operationException);
        restore["exception_message"] = SafeCornerExceptionMessage(operationException);
        restore["update_call"] = updateCall;
        restore["active_sketch_tag_before"] = plan.ActiveSketchTagBefore;
        try
        {
            if (IsSketchActive(state.Session, plan.Sketch))
                plan.Sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
        }
        catch (Exception ex)
        {
            restore["deactivation_exception_type"] = ex.GetType().FullName;
            restore["deactivation_exception_message"] = SafeCornerExceptionMessage(ex);
        }

        bool undoSucceeded = false;
        try
        {
            state.Session.UndoToMark(mark, null);
            undoSucceeded = true;
            restore["undo_to_mark_succeeded"] = true;
        }
        catch (Exception ex)
        {
            restore["undo_to_mark_succeeded"] = false;
            restore["undo_exception_type"] = ex.GetType().FullName;
            restore["undo_exception_message"] = SafeCornerExceptionMessage(ex);
        }

        bool geometryRestored = false, constraintsRestored = false, semanticRestored = false;
        bool bodiesRestored = false, historyRestored = false, dirtyRestored = false, featuresRestored = false;
        bool activeSketchRestored = false;
        string restoredRevision = binding == null ? null : binding.RevisionId;
        if (undoSucceeded)
        {
            try
            {
                Sketch activeSketchAfterUndo = state.Session.ActiveSketch;
                string activeSketchTagAfterUndo = activeSketchAfterUndo == null
                    ? null : activeSketchAfterUndo.Tag.ToString();
                activeSketchRestored = String.Equals(plan.ActiveSketchTagBefore,
                    activeSketchTagAfterUndo, StringComparison.Ordinal);
                restore["active_sketch_tag_after"] = activeSketchTagAfterUndo;
                restore["active_sketch_restored"] = activeSketchRestored;

                SnapshotResult restoredCapture = Capture(binding, state);
                restoredRevision = binding.RevisionId;
                Dictionary<string, CornerGeometrySnapshot> geometryNow = CaptureSketchCornerGeometry(state.WorkPart);
                Dictionary<string, CornerConstraintSnapshot> constraintsNow = CaptureSketchCornerConstraints(plan.Sketch);
                List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart);
                geometryRestored = CornerGeometryMapsEqual(plan.GeometryBefore, geometryNow);
                constraintsRestored = CornerConstraintMapsEqual(plan.ConstraintsBefore, constraintsNow);
                semanticRestored = String.Equals(plan.SemanticBefore,
                    CanonicalSemanticSnapshot(restoredCapture.Snapshot), StringComparison.Ordinal);
                Dictionary<string, object> bodyReadback;
                bodiesRestored = BodyMeasuresEqual(plan.BodiesBefore, bodiesNow, out bodyReadback);
                historyRestored = String.Equals(plan.HistoryBefore,
                    M2Json.CanonicalText(HistoryProjection(restoredCapture.Snapshot)), StringComparison.Ordinal);
                dirtyRestored = state.WorkPart.IsModified == plan.DirtyBefore;
                featuresRestored = FeatureTagsMatch(plan.FeatureTagsBefore, CaptureFeatureTags(state.WorkPart));
                restore["geometry_restored"] = geometryRestored;
                restore["constraints_restored"] = constraintsRestored;
                restore["semantic_state_restored"] = semanticRestored;
                restore["bodies_restored"] = bodiesRestored;
                restore["history_restored"] = historyRestored;
                restore["dirty_state_restored"] = dirtyRestored;
                restore["features_restored"] = featuresRestored;
                restore["body_measurements"] = bodyReadback;
            }
            catch (Exception ex)
            {
                restore["active_sketch_restored"] = false;
                restore["readback_exception_type"] = ex.GetType().FullName;
                restore["readback_exception_message"] = SafeCornerExceptionMessage(ex);
            }
        }
        else restore["active_sketch_restored"] = false;
        restore["revision_id"] = restoredRevision;
        plan.NativeOperationEvidence["undo_restore"] = restore;
        bool restored = undoSucceeded && geometryRestored && constraintsRestored && semanticRestored &&
            bodiesRestored && historyRestored && dirtyRestored && featuresRestored && activeSketchRestored;
        Dictionary<string, object> evidence = BuildSketchCornerEvidence(plan, null, null, null, null, null, null);
        if (plan.AppliedEvidence != null)
            evidence["failed_postcondition"] = CloneCornerDictionary(plan.AppliedEvidence);
        evidence["rollback"] = restore;
        bool nativeCalled = Boolean.Equals(restore["native_operation_called"], true);
        string stateName = restored ? (nativeCalled ? "ROLLED_BACK" : "UNCHANGED") : "UNKNOWN";
        string code = restored ? CornerExceptionCode(operationException) : "SKETCH_CORNER_ROLLBACK_UNVERIFIED";
        string message = restored
            ? "NX sketch corner operation failed and the prior geometry, constraints, history, bodies, and dirty state were restored."
            : "NX sketch corner operation failed and Undo could not prove restoration of the prior model state.";
        Dictionary<string, object> result = BuildSketchCornerExecutionResult(plan, restoredRevision, stateName, false, evidence);
        return M3Execution(stateName, restoredRevision, result, code, message);
    }

    private static Dictionary<string, CornerGeometrySnapshot> CaptureSketchCornerGeometry(Part part)
    {
        if (part == null) throw new M2ModelException("MODEL_READ_FAILED", "Work Part is unavailable during F07 geometry capture");
        Dictionary<string, string> sketchOwnerTags = new Dictionary<string, string>(StringComparer.Ordinal);
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory returned null during F07 geometry capture");
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory contains null during F07 geometry capture");
            NXObject[] geometry = sketch.GetAllGeometry();
            if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null during F07 geometry capture");
            string ownerTag = sketch.Tag.ToString();
            foreach (NXObject item in geometry)
            {
                if (item == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch geometry contains null during F07 geometry capture");
                string tag = item.Tag.ToString();
                if (String.IsNullOrEmpty(tag) || sketchOwnerTags.ContainsKey(tag))
                    throw new M2ModelException("MODEL_READ_FAILED", "NX Tag is missing or duplicated in F07 geometry capture");
                sketchOwnerTags.Add(tag, ownerTag);
            }
        }

        List<object> sketchRecords = CaptureGeometryRecords(part, true);
        Dictionary<string, CornerGeometrySnapshot> result = new Dictionary<string, CornerGeometrySnapshot>(StringComparer.Ordinal);
        foreach (object sketchValue in sketchRecords)
        {
            Dictionary<string, object> sketchRecord = sketchValue as Dictionary<string, object>;
            if (sketchRecord == null) throw new M2ModelException("MODEL_READ_FAILED", "geometry capture contains an invalid Sketch record");
            object geometryValue;
            if (!sketchRecord.TryGetValue("geometry", out geometryValue)) continue;
            List<object> records = geometryValue as List<object>;
            if (records == null) throw new M2ModelException("MODEL_READ_FAILED", "geometry capture has invalid geometry records");
            foreach (object itemValue in records)
            {
                Dictionary<string, object> item = itemValue as Dictionary<string, object>;
                if (item == null) throw new M2ModelException("MODEL_READ_FAILED", "geometry capture contains an invalid object record");
                object tagValue, idValue, typeValue;
                string tag = item.TryGetValue("tag", out tagValue) ? tagValue as string : null;
                string id = item.TryGetValue("journal_id", out idValue) ? idValue as string : null;
                string type = item.TryGetValue("object_type", out typeValue) ? typeValue as string : null;
                if (String.IsNullOrEmpty(tag) || String.IsNullOrEmpty(id) || String.IsNullOrEmpty(type))
                    throw new M2ModelException("MODEL_READ_FAILED", "F07 geometry identity is incomplete");
                // CaptureGeometryRecords also includes dimension/constraint operands
                // that are not members of this Sketch. F07 tracks only owned geometry.
                if (!sketchOwnerTags.ContainsKey(tag)) continue;
                Dictionary<string, object> definition = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> pair in item)
                    if (!String.Equals(pair.Key, "journal_id", StringComparison.Ordinal)) definition.Add(pair.Key, pair.Value);
                CornerGeometrySnapshot snapshot = new CornerGeometrySnapshot
                {
                    Tag = tag,
                    JournalId = id,
                    ObjectType = type,
                    SketchTag = sketchOwnerTags[tag],
                    Record = definition,
                    Signature = M2Json.CanonicalText(definition)
                };
                if (result.ContainsKey(tag)) throw new M2ModelException("MODEL_READ_FAILED", "duplicate geometry tag in F07 capture");
                result.Add(tag, snapshot);
            }
        }
        return result;
    }

    private static Dictionary<string, CornerConstraintSnapshot> CaptureSketchCornerConstraints(Sketch sketch)
    {
        if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch is unavailable during F07 constraint capture");
        Dictionary<string, CornerConstraintSnapshot> result = new Dictionary<string, CornerConstraintSnapshot>(StringComparer.Ordinal);
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
            if (constraints == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch constraint enumeration returned null for " + type);
            foreach (SketchConstraint constraint in constraints)
            {
                if (constraint == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch constraint enumeration contains null");
                string tag = constraint.Tag.ToString();
                Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
                record["tag"] = tag;
                record["journal_id"] = RequiredJournalId(constraint, "sketch constraint");
                record["constraint_type"] = constraint.ConstraintType.ToString();
                record["class"] = constraint.GetType().Name;
                record["all_geometry_fixed"] = constraint.IsAllGeometryFixed();
                SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                SketchGeometricConstraint geometric = constraint as SketchGeometricConstraint;
                if (dimension != null)
                {
                    Sketch.DimensionGeometry[] operands = dimension.GetDimensionGeometry();
                    if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "dimensional constraint operands are unavailable");
                    List<string> refs = new List<string>();
                    foreach (Sketch.DimensionGeometry operand in operands)
                    {
                        if (operand.Geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "dimensional operand geometry is null");
                        refs.Add(operand.Geometry.Tag.ToString() + ":" + operand.AssocType.ToString() + ":" +
                            Convert.ToString(operand.AssocValue, CultureInfo.InvariantCulture));
                    }
                    refs.Sort(StringComparer.Ordinal);
                    Expression expression = dimension.AssociatedExpression;
                    record["dimension_state"] = dimension.DimensionState.ToString();
                    record["is_driving"] = dimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving;
                    record["expression_name"] = expression == null ? null : expression.Name;
                    record["expression_rhs"] = expression == null ? null : expression.RightHandSide;
                    record["expression_value"] = expression == null ? null : NumberText(expression.Value);
                    record["expression_unit"] = expression == null || expression.Units == null ? null : expression.Units.Symbol;
                    record["geometry_refs"] = StringListObjects(refs.ToArray());
                }
                else if (geometric != null)
                {
                    Sketch.ConstraintGeometry[] operands = geometric.GetGeometry();
                    if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "geometric constraint operands are unavailable");
                    List<string> refs = new List<string>();
                    foreach (Sketch.ConstraintGeometry operand in operands)
                    {
                        if (operand.Geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "geometric operand is null");
                        refs.Add(operand.Geometry.Tag.ToString() + ":" + operand.PointType.ToString() + ":" +
                            operand.SplineDefiningPointIndex.ToString(CultureInfo.InvariantCulture));
                    }
                    refs.Sort(StringComparer.Ordinal);
                    record["is_inferred"] = geometric.IsInferred;
                    record["geometry_refs"] = StringListObjects(refs.ToArray());
                }
                else throw new M2ModelException("UNSUPPORTED_SKETCH_CONSTRAINT", "F07 cannot snapshot constraint class " + constraint.GetType().Name);

                Dictionary<string, object> definition = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> pair in record)
                    if (!String.Equals(pair.Key, "journal_id", StringComparison.Ordinal)) definition.Add(pair.Key, pair.Value);
                CornerConstraintSnapshot snapshot = new CornerConstraintSnapshot
                {
                    Tag = tag,
                    JournalId = Convert.ToString(record["journal_id"], CultureInfo.InvariantCulture),
                    Record = record,
                    Signature = M2Json.CanonicalText(definition)
                };
                if (result.ContainsKey(tag)) throw new M2ModelException("MODEL_READ_FAILED", "duplicate constraint tag during F07 capture");
                result.Add(tag, snapshot);
            }
        }
        return result;
    }

    private static bool CornerGeometryMapsEqual(Dictionary<string, CornerGeometrySnapshot> expected,
        Dictionary<string, CornerGeometrySnapshot> actual)
    {
        if (expected == null || actual == null || expected.Count != actual.Count) return false;
        foreach (KeyValuePair<string, CornerGeometrySnapshot> pair in expected)
        {
            CornerGeometrySnapshot current;
            if (!actual.TryGetValue(pair.Key, out current) ||
                !String.Equals(pair.Value.Signature, current.Signature, StringComparison.Ordinal) ||
                !String.Equals(pair.Value.SketchTag, current.SketchTag, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool CornerConstraintMapsEqual(Dictionary<string, CornerConstraintSnapshot> expected,
        Dictionary<string, CornerConstraintSnapshot> actual)
    {
        if (expected == null || actual == null || expected.Count != actual.Count) return false;
        foreach (KeyValuePair<string, CornerConstraintSnapshot> pair in expected)
        {
            CornerConstraintSnapshot current;
            if (!actual.TryGetValue(pair.Key, out current) ||
                !String.Equals(pair.Value.Signature, current.Signature, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool CornerConstraintMapsPreserve(Dictionary<string, CornerConstraintSnapshot> before,
        Dictionary<string, CornerConstraintSnapshot> after)
    {
        if (before == null || after == null) return false;
        foreach (KeyValuePair<string, CornerConstraintSnapshot> pair in before)
        {
            CornerConstraintSnapshot current;
            if (!after.TryGetValue(pair.Key, out current) ||
                !String.Equals(pair.Value.Signature, current.Signature, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool VerifyCornerNonTargets(M3SketchCornerPlanState plan,
        Dictionary<string, CornerGeometrySnapshot> geometryNow, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> preserved = new List<object>();
        List<object> mismatches = new List<object>();
        List<string> newCurveTags = new List<string>();
        List<string> newVirtualPointTags = new List<string>();
        bool targetsPresent = true;
        foreach (KeyValuePair<string, CornerGeometrySnapshot> pair in plan.GeometryBefore)
        {
            CornerGeometrySnapshot current;
            if (!geometryNow.TryGetValue(pair.Key, out current))
            {
                mismatches.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    { { "tag", pair.Key }, { "reason", "existing geometry was removed" } });
                if (pair.Key == plan.Curve1Tag || pair.Key == plan.Curve2Tag) targetsPresent = false;
                continue;
            }
            bool target = pair.Key == plan.Curve1Tag || pair.Key == plan.Curve2Tag;
            if (target)
            {
                if (!String.Equals(pair.Value.ObjectType, current.ObjectType, StringComparison.Ordinal) ||
                    !String.Equals(current.SketchTag, plan.SketchTag, StringComparison.Ordinal))
                {
                    targetsPresent = false;
                    mismatches.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        { { "tag", pair.Key }, { "reason", "target identity or owner changed" } });
                }
                else preserved.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    { { "tag", pair.Key }, { "role", pair.Key == plan.Curve1Tag ? "curve1" : "curve2" }, { "target_modified", true } });
            }
            else if (String.Equals(pair.Value.Signature, current.Signature, StringComparison.Ordinal))
                preserved.Add(new Dictionary<string, object>(StringComparer.Ordinal) { { "tag", pair.Key }, { "unchanged", true } });
            else mismatches.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                { { "tag", pair.Key }, { "reason", "non-target geometry definition changed" } });
        }
        foreach (KeyValuePair<string, CornerGeometrySnapshot> pair in geometryNow)
        {
            if (plan.GeometryBefore.ContainsKey(pair.Key)) continue;
            if (pair.Value.SketchTag != plan.SketchTag)
            {
                mismatches.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    { { "tag", pair.Key }, { "reason", "new geometry is owned by another Sketch" } });
                continue;
            }
            if (pair.Value.ObjectType == "SketchVirtualPoint") newVirtualPointTags.Add(pair.Key);
            else if (pair.Value.ObjectType == "Line" || pair.Value.ObjectType == "Arc") newCurveTags.Add(pair.Key);
            else mismatches.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                { { "tag", pair.Key }, { "reason", "unexpected native geometry type was added" }, { "type", pair.Value.ObjectType } });
        }
        newCurveTags.Sort(StringComparer.Ordinal);
        newVirtualPointTags.Sort(StringComparer.Ordinal);
        bool curveCountMatches = newCurveTags.Count == 1 &&
            String.Equals(geometryNow[newCurveTags[0]].ObjectType,
                plan.Request.Kind == "fillet" ? "Arc" : "Line", StringComparison.Ordinal);
        if (!curveCountMatches)
            mismatches.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                { { "reason", "native corner operation did not add exactly one expected sketch curve" }, { "new_curve_tags", StringListObjects(newCurveTags.ToArray()) } });
        bool virtualPointsValid = newVirtualPointTags.Count <= 1;
        foreach (string tag in newVirtualPointTags)
        {
            NXObject item = FindSketchGeometryByTag(plan.Sketch, tag);
            SketchVirtualPoint virtualPoint = item as SketchVirtualPoint;
            if (virtualPoint == null || PointDistance(virtualPoint.Coordinates, plan.Corner) > SketchCornerPositionToleranceMm ||
                !plan.Sketch.IsSketchGeometry(virtualPoint)) virtualPointsValid = false;
        }
        if (!virtualPointsValid)
            mismatches.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                { { "reason", "native SketchVirtualPoint readback is absent, ambiguous, or not at the selected corner" } });
        evidence["preserved_non_target_records"] = preserved;
        evidence["mismatches"] = mismatches;
        evidence["target_tags_present"] = targetsPresent;
        evidence["new_curve_tags"] = StringListObjects(newCurveTags.ToArray());
        evidence["new_virtual_point_tags"] = StringListObjects(newVirtualPointTags.ToArray());
        evidence["new_curve_count_matches"] = curveCountMatches;
        evidence["virtual_points_valid"] = virtualPointsValid;
        evidence["preserved_non_target_geometry"] = mismatches.Count == 0 && targetsPresent && curveCountMatches && virtualPointsValid;
        return (bool)evidence["preserved_non_target_geometry"];
    }

    private static List<object> CornerConstraintPublicRecords(Dictionary<string, CornerConstraintSnapshot> constraints)
    {
        List<object> result = new List<object>();
        if (constraints == null) return result;
        List<string> tags = new List<string>(constraints.Keys);
        tags.Sort(StringComparer.Ordinal);
        foreach (string tag in tags)
        {
            CornerConstraintSnapshot item = constraints[tag];
            result.Add(item.Record);
        }
        return result;
    }

    private static List<object> CornerConstraintPublicRecordsForGeometry(
        Dictionary<string, CornerConstraintSnapshot> constraints, string geometryTag1, string geometryTag2)
    {
        List<object> result = new List<object>();
        if (constraints == null) return result;
        List<string> tags = new List<string>(constraints.Keys);
        tags.Sort(StringComparer.Ordinal);
        foreach (string tag in tags)
        {
            CornerConstraintSnapshot item = constraints[tag];
            object referencesValue = null;
            List<object> references = null;
            if (item.Record != null && item.Record.TryGetValue("geometry_refs", out referencesValue))
                references = referencesValue as List<object>;
            if (references == null) continue;
            bool targetsCurve = false;
            foreach (object referenceValue in references)
            {
                string reference = referenceValue as string;
                if (reference != null &&
                    (reference.StartsWith(geometryTag1 + ":", StringComparison.Ordinal) ||
                    reference.StartsWith(geometryTag2 + ":", StringComparison.Ordinal)))
                {
                    targetsCurve = true;
                    break;
                }
            }
            if (targetsCurve) result.Add(item.Record);
        }
        return result;
    }

    private static List<string> CaptureFeatureTags(Part part)
    {
        Feature[] features = part.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature inventory is unavailable during F07 verification");
        List<string> tags = new List<string>();
        foreach (Feature feature in features)
        {
            if (feature == null) throw new M2ModelException("MODEL_READ_FAILED", "feature inventory contains null during F07 verification");
            tags.Add(feature.Tag.ToString());
        }
        tags.Sort(StringComparer.Ordinal);
        return tags;
    }

    private static bool FeatureTagsMatch(List<string> expected, List<string> actual)
    {
        if (expected == null || actual == null || expected.Count != actual.Count) return false;
        List<string> before = new List<string>(expected);
        List<string> after = new List<string>(actual);
        before.Sort(StringComparer.Ordinal);
        after.Sort(StringComparer.Ordinal);
        for (int i = 0; i < before.Count; i++)
            if (!String.Equals(before[i], after[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static Dictionary<string, object> CloneCornerDictionary(Dictionary<string, object> source)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (source != null) foreach (KeyValuePair<string, object> pair in source) result[pair.Key] = pair.Value;
        return result;
    }

    private static List<object> StringListObjects(string[] values)
    {
        List<object> result = new List<object>();
        if (values != null) foreach (string value in values) result.Add(value);
        return result;
    }

    private static List<object> StringListObjects(List<string> values)
    {
        List<object> result = new List<object>();
        if (values != null) foreach (string value in values) result.Add(value);
        return result;
    }

    private static Curve FindSketchCornerCurve(Sketch sketch, string journalId)
    {
        if (sketch == null || String.IsNullOrEmpty(journalId)) return null;
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null while resolving F07 curve ids");
        Curve found = null;
        foreach (NXObject item in geometry)
        {
            Curve curve = item as Curve;
            if (curve == null) continue;
            if (!String.Equals(curve.JournalIdentifier, journalId, StringComparison.Ordinal)) continue;
            if (found != null) throw new M2ModelException("AMBIGUOUS_CURVE_ID", "curve id identifies more than one native curve in the Sketch");
            found = curve;
        }
        return found;
    }

    private static NXObject FindSketchGeometryByTag(Sketch sketch, string tag)
    {
        if (sketch == null || String.IsNullOrEmpty(tag)) return null;
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null while resolving an F07 native Tag");
        NXObject found = null;
        foreach (NXObject item in geometry)
        {
            if (item == null || !String.Equals(item.Tag.ToString(), tag, StringComparison.Ordinal)) continue;
            if (found != null) throw new M2ModelException("MODEL_READ_FAILED", "native Tag resolved to multiple sketch objects");
            found = item;
        }
        return found;
    }

    private static Curve FindCornerCurveByTag(Sketch sketch, string tag)
    {
        return FindSketchGeometryByTag(sketch, tag) as Curve;
    }

    private static bool VerifyTargetCurveShape(Curve curve, CornerGeometrySnapshot before,
        string selectedEndpoint, Point3d selectedAfter, Point3d remoteAfter, Point3d originalCorner,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence["baseline_tag"] = before == null ? null : before.Tag;
        evidence["baseline_type"] = before == null ? null : before.ObjectType;
        if (curve == null || before == null || before.Record == null)
        {
            evidence["baseline_record_available"] = false;
            evidence["shape_preserved"] = false;
            return false;
        }
        evidence["baseline_record_available"] = true;

        Line line = curve as Line;
        if (line != null)
        {
            Point3d baselineStart, baselineEnd;
            if (!String.Equals(before.ObjectType, "Line", StringComparison.Ordinal) ||
                !TryReadCornerRecordPoint(before.Record, "start", out baselineStart) ||
                !TryReadCornerRecordPoint(before.Record, "end", out baselineEnd))
            {
                evidence["reason"] = "baseline Line endpoints are unavailable";
                evidence["shape_preserved"] = false;
                return false;
            }

            Point3d originalRemote = selectedEndpoint == "start" ? baselineEnd : baselineStart;
            Point3d originalSelected = selectedEndpoint == "start" ? baselineStart : baselineEnd;
            Vector3d segment = Subtract(originalRemote, originalCorner);
            Vector3d moved = Subtract(selectedAfter, originalCorner);
            double segmentLengthSquared = Dot(segment, segment);
            if (!IsFinite(segmentLengthSquared) || segmentLengthSquared <= SketchCornerPositionToleranceMm * SketchCornerPositionToleranceMm)
            {
                evidence["reason"] = "baseline Line segment has no usable length";
                evidence["shape_preserved"] = false;
                return false;
            }

            double parameter = Dot(moved, segment) / segmentLengthSquared;
            Point3d projected = Add(originalCorner, Scale(segment, parameter));
            double crossTrackResidual = PointDistance(selectedAfter, projected);
            double selectedCornerResidual = PointDistance(originalSelected, originalCorner);
            double remoteResidual = PointDistance(remoteAfter, originalRemote);
            bool endpointInsideOriginalSegment = IsFinite(parameter) && parameter > 0.0 && parameter < 1.0;
            bool linePreserved = selectedCornerResidual <= SketchCornerPositionToleranceMm &&
                endpointInsideOriginalSegment && IsFinite(crossTrackResidual) &&
                crossTrackResidual <= SketchCornerPositionToleranceMm &&
                remoteResidual <= SketchCornerPositionToleranceMm;
            evidence["original_corner_endpoint_residual_mm"] = selectedCornerResidual;
            evidence["trim_parameter_on_original_corner_to_remote_segment"] = parameter;
            evidence["trim_endpoint_cross_track_residual_mm"] = crossTrackResidual;
            evidence["trim_endpoint_inside_original_segment"] = endpointInsideOriginalSegment;
            evidence["remote_endpoint_residual_mm"] = remoteResidual;
            evidence["shape_preserved"] = linePreserved;
            return linePreserved;
        }

        Arc arc = curve as Arc;
        if (arc != null)
            return VerifyTargetArcShape(arc, before, selectedEndpoint, selectedAfter, remoteAfter, evidence);

        evidence["reason"] = "target shape verification supports only Line and Arc geometry";
        evidence["shape_preserved"] = false;
        return false;
    }

    private static bool VerifyTargetArcShape(Arc arc, CornerGeometrySnapshot before,
        string selectedEndpoint, Point3d selectedAfter, Point3d remoteAfter,
        Dictionary<string, object> evidence)
    {
        Point3d centerBefore, startBefore, endBefore;
        Vector3d xBefore, yBefore;
        double radiusBefore, startAngleBefore, endAngleBefore;
        if (!String.Equals(before.ObjectType, "Arc", StringComparison.Ordinal) ||
            !TryReadCornerRecordPoint(before.Record, "center", out centerBefore) ||
            !TryReadCornerRecordPoint(before.Record, "start", out startBefore) ||
            !TryReadCornerRecordPoint(before.Record, "end", out endBefore) ||
            !TryReadCornerRecordVector(before.Record, "orientation_x", out xBefore) ||
            !TryReadCornerRecordVector(before.Record, "orientation_y", out yBefore) ||
            !TryReadCornerRecordNumber(before.Record, "radius", out radiusBefore) ||
            !TryReadCornerRecordNumber(before.Record, "start_angle", out startAngleBefore) ||
            !TryReadCornerRecordNumber(before.Record, "end_angle", out endAngleBefore))
        {
            evidence["reason"] = "baseline Arc center, radius, angle interval, or orientation is unavailable";
            evidence["shape_preserved"] = false;
            return false;
        }

        Vector3d normalBefore;
        if (!TryBuildCornerFrame(ref xBefore, ref yBefore, out normalBefore))
        {
            evidence["reason"] = "baseline Arc orientation frame is degenerate";
            evidence["shape_preserved"] = false;
            return false;
        }
        Point3d orientationCenterAfter;
        Vector3d xAfter, yAfter;
        arc.GetOrientation(out orientationCenterAfter, out xAfter, out yAfter);
        Vector3d normalAfter;
        if (!TryBuildCornerFrame(ref xAfter, ref yAfter, out normalAfter))
        {
            evidence["reason"] = "current Arc orientation frame is degenerate";
            evidence["shape_preserved"] = false;
            return false;
        }

        Point3d centerAfter = arc.CenterPoint;
        double centerResidual = PointDistance(centerAfter, centerBefore);
        double orientationCenterResidual = PointDistance(orientationCenterAfter, centerBefore);
        double radiusResidual = Math.Abs(arc.Radius - radiusBefore);
        double normalAlignment = Dot(normalBefore, normalAfter);
        Point3d originalRemote = selectedEndpoint == "start" ? endBefore : startBefore;
        double remoteResidual = PointDistance(remoteAfter, originalRemote);

        Vector3d selectedFromCenter = Subtract(selectedAfter, centerBefore);
        double planeResidual = Math.Abs(Dot(selectedFromCenter, normalBefore));
        double xComponent = Dot(selectedFromCenter, xBefore);
        double yComponent = Dot(selectedFromCenter, yBefore);
        double selectedRadius = Math.Sqrt(xComponent * xComponent + yComponent * yComponent);
        double selectedRadiusResidual = Math.Abs(selectedRadius - radiusBefore);
        double selectedAngle = Math.Atan2(yComponent, xComponent);
        double originalSweep = endAngleBefore - startAngleBefore;
        double sweepDirection = originalSweep < 0.0 ? -1.0 : 1.0;
        double sweepMagnitude = Math.Abs(originalSweep);
        double angularTolerance = Math.Max(SketchCornerAngleTolerance,
            SketchCornerPositionToleranceMm / Math.Max(radiusBefore, SketchCornerPositionToleranceMm));
        double selectedParameterOffset = DirectedAngleOffset(startAngleBefore, selectedAngle, sweepDirection);
        bool selectedEndpointStrictlyInside = IsFinite(originalSweep) && sweepMagnitude > 2.0 * angularTolerance &&
            selectedParameterOffset > angularTolerance &&
            selectedParameterOffset < sweepMagnitude - angularTolerance;
        bool arcPreserved = IsFinite(centerResidual) && centerResidual <= SketchCornerPositionToleranceMm &&
            IsFinite(orientationCenterResidual) && orientationCenterResidual <= SketchCornerPositionToleranceMm &&
            IsFinite(radiusResidual) && radiusResidual <= SketchCornerPositionToleranceMm &&
            IsFinite(normalAlignment) && normalAlignment >= 1.0 - SketchCornerAngleTolerance &&
            IsFinite(planeResidual) && planeResidual <= SketchCornerPositionToleranceMm &&
            IsFinite(selectedRadiusResidual) && selectedRadiusResidual <= SketchCornerPositionToleranceMm &&
            remoteResidual <= SketchCornerPositionToleranceMm && selectedEndpointStrictlyInside;

        evidence["center_residual_mm"] = centerResidual;
        evidence["orientation_center_residual_mm"] = orientationCenterResidual;
        evidence["radius_before_mm"] = radiusBefore;
        evidence["radius_after_mm"] = arc.Radius;
        evidence["radius_residual_mm"] = radiusResidual;
        evidence["plane_normal_alignment"] = normalAlignment;
        evidence["selected_endpoint_plane_residual_mm"] = planeResidual;
        evidence["selected_endpoint_radius_residual_mm"] = selectedRadiusResidual;
        evidence["remote_endpoint_residual_mm"] = remoteResidual;
        evidence["original_start_angle_rad"] = startAngleBefore;
        evidence["original_end_angle_rad"] = endAngleBefore;
        evidence["original_sweep_rad"] = originalSweep;
        evidence["selected_endpoint_angle_rad_in_original_frame"] = selectedAngle;
        evidence["selected_endpoint_directed_offset_rad"] = selectedParameterOffset;
        evidence["selected_endpoint_strictly_inside_original_interval"] = selectedEndpointStrictlyInside;
        evidence["shape_preserved"] = arcPreserved;
        return arcPreserved;
    }

    private static bool TryReadCornerRecordPoint(Dictionary<string, object> record, string key, out Point3d point)
    {
        point = new Point3d();
        object value = null;
        List<object> coordinates = record != null && record.TryGetValue(key, out value) ? value as List<object> : null;
        if (coordinates == null || coordinates.Count != 3) return false;
        double x, y, z;
        if (!TryReadCornerRecordNumber(coordinates[0], out x) ||
            !TryReadCornerRecordNumber(coordinates[1], out y) ||
            !TryReadCornerRecordNumber(coordinates[2], out z)) return false;
        point = new Point3d(x, y, z);
        return true;
    }

    private static bool TryReadCornerRecordVector(Dictionary<string, object> record, string key, out Vector3d vector)
    {
        Point3d point;
        bool read = TryReadCornerRecordPoint(record, key, out point);
        vector = new Vector3d(point.X, point.Y, point.Z);
        return read;
    }

    private static bool TryReadCornerRecordNumber(Dictionary<string, object> record, string key, out double number)
    {
        object value = null;
        number = Double.NaN;
        return record != null && record.TryGetValue(key, out value) && TryReadCornerRecordNumber(value, out number);
    }

    private static bool TryReadCornerRecordNumber(object value, out double number)
    {
        number = Double.NaN;
        if (value == null) return false;
        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return IsFinite(number);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryBuildCornerFrame(ref Vector3d xAxis, ref Vector3d yAxis, out Vector3d normal)
    {
        normal = new Vector3d();
        if (!TryNormalizeCornerVector(ref xAxis)) return false;
        double yAlongX = Dot(yAxis, xAxis);
        yAxis = new Vector3d(yAxis.X - yAlongX * xAxis.X,
            yAxis.Y - yAlongX * xAxis.Y, yAxis.Z - yAlongX * xAxis.Z);
        if (!TryNormalizeCornerVector(ref yAxis)) return false;
        normal = CrossCornerVectors(xAxis, yAxis);
        return TryNormalizeCornerVector(ref normal);
    }

    private static bool TryNormalizeCornerVector(ref Vector3d vector)
    {
        double length = Math.Sqrt(Dot(vector, vector));
        if (!IsFinite(length) || length <= 1.0e-12) return false;
        vector = new Vector3d(vector.X / length, vector.Y / length, vector.Z / length);
        return true;
    }

    private static Vector3d CrossCornerVectors(Vector3d first, Vector3d second)
    {
        return new Vector3d(first.Y * second.Z - first.Z * second.Y,
            first.Z * second.X - first.X * second.Z,
            first.X * second.Y - first.Y * second.X);
    }

    private static double DirectedAngleOffset(double startAngle, double angle, double direction)
    {
        double offset = direction < 0.0 ? startAngle - angle : angle - startAngle;
        double fullTurn = 2.0 * Math.PI;
        offset %= fullTurn;
        if (offset < 0.0) offset += fullTurn;
        return offset;
    }

    private static bool HasMirrorConstraint(Sketch sketch, Curve curve)
    {
        SketchConstraint[] constraints = sketch.GetConstraintsForGeometry(curve, Sketch.ConstraintClass.Any);
        if (constraints == null) throw new M2ModelException("MODEL_READ_FAILED", "could not enumerate constraints for a selected curve");
        foreach (SketchConstraint constraint in constraints)
            if (constraint != null && constraint.ConstraintType == Sketch.ConstraintType.Mirror) return true;
        return false;
    }

    private static bool IsSketchCornerFilletCurve(Curve curve)
    {
        return curve is Line || curve is Arc;
    }

    private static bool IsClosedSketchCornerCurve(Curve curve)
    {
        Arc arc = curve as Arc;
        if (arc == null) return false;
        return Math.Abs(arc.EndAngle - arc.StartAngle) >= 2.0 * Math.PI - SketchCornerAngleTolerance ||
            PointDistance(CurveEndpoint(arc, "start"), CurveEndpoint(arc, "end")) <= SketchCornerPositionToleranceMm;
    }

    private static M3SketchCornerRequest CloneM3SketchCornerRequest(M3SketchCornerRequest source)
    {
        if (source == null) return null;
        return new M3SketchCornerRequest
        {
            RevisionId = source.RevisionId,
            SketchId = source.SketchId,
            Kind = source.Kind,
            Curve1Id = source.Curve1Id,
            Curve1End = source.Curve1End,
            Curve2Id = source.Curve2Id,
            Curve2End = source.Curve2End,
            RadiusMm = source.RadiusMm,
            Distance1Mm = source.Distance1Mm,
            Distance2Mm = source.Distance2Mm,
            AngleDeg = source.AngleDeg
        };
    }

    private static Point3d CurveEndpoint(Curve curve, string endpoint)
    {
        if (curve == null) throw new ArgumentNullException("curve");
        Line line = curve as Line;
        if (line != null) return endpoint == "start" ? line.StartPoint : line.EndPoint;
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center; Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            double angle = endpoint == "start" ? arc.StartAngle : arc.EndAngle;
            return ConicPoint(center, xAxis, yAxis, arc.Radius, arc.Radius, 0.0, angle);
        }
        throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "F07 endpoint readback supports Line and Arc curves only");
    }

    private static Vector3d CurveTangentAwayFromEndpoint(Curve curve, string endpoint)
    {
        Line line = curve as Line;
        if (line != null)
        {
            Point3d start = line.StartPoint, end = line.EndPoint;
            return endpoint == "start"
                ? new Vector3d(end.X - start.X, end.Y - start.Y, end.Z - start.Z)
                : new Vector3d(start.X - end.X, start.Y - end.Y, start.Z - end.Z);
        }
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center; Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            double angle = endpoint == "start" ? arc.StartAngle : arc.EndAngle;
            double sign = endpoint == "start" ? 1.0 : -1.0;
            return new Vector3d(
                sign * (-xAxis.X * Math.Sin(angle) + yAxis.X * Math.Cos(angle)),
                sign * (-xAxis.Y * Math.Sin(angle) + yAxis.Y * Math.Cos(angle)),
                sign * (-xAxis.Z * Math.Sin(angle) + yAxis.Z * Math.Cos(angle)));
        }
        throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "F07 tangent readback supports Line and Arc curves only");
    }

    private static Point3d PointAlongCurveFromEndpoint(Curve curve, string endpoint, double fraction)
    {
        if (!(fraction > 0.0 && fraction < 1.0)) throw new ArgumentOutOfRangeException("fraction");
        Line line = curve as Line;
        if (line != null)
        {
            Point3d start = endpoint == "start" ? line.StartPoint : line.EndPoint;
            Point3d remote = endpoint == "start" ? line.EndPoint : line.StartPoint;
            return new Point3d(start.X + (remote.X - start.X) * fraction,
                start.Y + (remote.Y - start.Y) * fraction,
                start.Z + (remote.Z - start.Z) * fraction);
        }
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center; Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            double start = arc.StartAngle, end = arc.EndAngle;
            if (end < start) end += 2.0 * Math.PI;
            double angle = endpoint == "start" ? start + (end - start) * fraction : end - (end - start) * fraction;
            return ConicPoint(center, xAxis, yAxis, arc.Radius, arc.Radius, 0.0, angle);
        }
        throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "F07 help point generation supports Line and Arc curves only");
    }

    private static void RequireFinitePositive(double value, string label)
    {
        if (!IsFinite(value) || value <= 0.0)
            throw new M2ModelException("INVALID_REQUEST", label + " must be a positive finite number");
    }

    private static void NormalizeVector(ref Vector3d vector, string label)
    {
        double length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
        if (!IsFinite(length) || length <= 1.0e-12)
            throw new M2ModelException("DEGENERATE_CORNER", label + " is zero or non-finite");
        vector = new Vector3d(vector.X / length, vector.Y / length, vector.Z / length);
    }

    private static double VectorAngleDegrees(Vector3d first, Vector3d second)
    {
        double denominator = Math.Sqrt(Dot(first, first) * Dot(second, second));
        if (!IsFinite(denominator) || denominator <= 1.0e-12) return Double.NaN;
        return Math.Acos(Clamp(Dot(first, second) / denominator, -1.0, 1.0)) * 180.0 / Math.PI;
    }

    private static bool TangentVectorsMatch(Vector3d first, Vector3d second)
    {
        NormalizeVector(ref first, "first tangent");
        NormalizeVector(ref second, "second tangent");
        return Dot(first, second) <= -1.0 + SketchCornerAngleTolerance;
    }

    private static Point3d Add(Point3d point, Vector3d vector)
    {
        return new Point3d(point.X + vector.X, point.Y + vector.Y, point.Z + vector.Z);
    }

    private static Vector3d Add(Vector3d first, Vector3d second)
    {
        return new Vector3d(first.X + second.X, first.Y + second.Y, first.Z + second.Z);
    }

    private static Vector3d Subtract(Point3d first, Point3d second)
    {
        return new Vector3d(first.X - second.X, first.Y - second.Y, first.Z - second.Z);
    }

    private static Vector3d Scale(Vector3d vector, double factor)
    {
        return new Vector3d(vector.X * factor, vector.Y * factor, vector.Z * factor);
    }

    private static Point3d Midpoint(Point3d first, Point3d second)
    {
        return new Point3d((first.X + second.X) * 0.5, (first.Y + second.Y) * 0.5, (first.Z + second.Z) * 0.5);
    }

    private static double PointDistance(Point3d first, Point3d second)
    {
        double x = first.X - second.X, y = first.Y - second.Y, z = first.Z - second.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static double PointToSupportLineDistance(Point3d point, Point3d lineOrigin, Vector3d unitDirection)
    {
        double projection = Dot(Subtract(point, lineOrigin), unitDirection);
        return PointDistance(point, Add(lineOrigin, Scale(unitDirection, projection)));
    }

    private static double PointDistance(Point3d first, NXOpen.Point second)
    {
        return PointDistance(first, second.Coordinates);
    }

    private static double Dot(Vector3d first, Vector3d second)
    {
        return first.X * second.X + first.Y * second.Y + first.Z * second.Z;
    }

    private static double Clamp(double value, double low, double high)
    {
        return value < low ? low : value > high ? high : value;
    }

    private static string SafeCornerExceptionMessage(Exception exception)
    {
        if (exception == null) return null;
        M2ModelException model = exception as M2ModelException;
        if (model != null) return model.Message;
        AggregateException aggregate = exception as AggregateException;
        if (aggregate != null) return aggregate.Message;
        return exception.Message ?? exception.GetType().Name;
    }

    private static string CornerExceptionCode(Exception exception)
    {
        if (exception == null) return "NATIVE_CORNER_FAILED";
        M2ModelException model = exception as M2ModelException;
        if (model != null) return model.Code;
        return "NATIVE_CORNER_FAILED";
    }

    private static bool IsFiniteCornerValue(double value)
    {
        return !Double.IsNaN(value) && !Double.IsInfinity(value);
    }

    private static bool IsSketchActive(Session session, Sketch sketch)
    {
        if (session == null || sketch == null) return false;
        Sketch active = session.ActiveSketch;
        return active != null && active.Tag.Equals(sketch.Tag);
    }

}
