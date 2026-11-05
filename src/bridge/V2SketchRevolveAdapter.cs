using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NXOpen;
using NXOpen.Features;
using NxSketchMcp.M2;

/// <summary>F13 world-axis Revolve plans, execution, verification and exact feature readback.</summary>
public sealed partial class M2OpenModelAdapter
{
    private const double F13RevolveAxisPlaneToleranceMm = 1.0e-5;
    private const double F13RevolveAxisDirectionTolerance = 1.0e-6;
    private const double F13RevolveAngleToleranceDeg = 1.0e-9;
    private const double F13RevolveBooleanVolumeTolerance = 1.0e-9;
    private const double F13MachineEpsilon = 2.2204460492503131e-16;
    private const double F13CanonicalUvRoundoffMultiplier = 64.0;
    private const double F13CanonicalPointRoundoffMultiplier = 64.0;
    private const double F13CanonicalPointMaxDifferenceMm = 1.0e-7;
    private const double F13CanonicalNormalComponentRoundoffMultiplier = 64.0;
    private const double F13CanonicalNormalLengthRoundoffMultiplier = 64.0;
    private const double F13CanonicalNormalMaxAngleRad = 1.0e-12;
    private const int F13CanonicalMaxDetailedRoundoffDifferences = 512;
    private const int F13FingerprintCanonicalDiagnosticMaxSerializedUtf8Bytes = 512 * 1024;
    private const int F13FingerprintCanonicalDiagnosticEvidenceCopies = 2;
    private const int F13FingerprintCanonicalDiagnosticMaxChangedTags = 4;

    public M3PlanSnapshot PrepareRevolve(M2ModelBinding binding, M3RevolveRequest request)
    {
        request = NormalizeM3RevolveRequest(request);
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "an active Sketch must be ended before preparing a Revolve");

        SnapshotResult capture = Capture(binding, state);
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current part revision");

        Sketch sketch = FindSketch(state.WorkPart, request.SketchId);
        Dictionary<string, object> profileAnalysis;
        try { profileAnalysis = V2SketchProfileAnalyzer.AnalyzeF12WholeSketchSection(state.WorkPart, sketch); }
        catch (V2SketchProfileAnalyzer.SectionCleanupException)
        {
            throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN",
                "F12 temporary Section analysis could not prove that the Work Part was restored");
        }
        if (!F12SectionAnalysisVerified(profileAnalysis))
        {
            object failureValue;
            string reason = profileAnalysis != null && profileAnalysis.TryGetValue("failure_reason", out failureValue)
                ? Convert.ToString(failureValue, CultureInfo.InvariantCulture) : "whole-sketch profile analysis did not verify";
            throw new M2ModelException("UNSUPPORTED_PROFILE", "Revolve whole-sketch profile is invalid or could not be verified: " + reason);
        }
        int openChains = ProfileCount(profileAnalysis, "open_component_count");
        int closedLoops = ProfileCount(profileAnalysis, "valid_closed_component_count");
        if (request.ProfileOutput == "solid" && (openChains != 0 || closedLoops == 0))
            throw new M2ModelException("UNSUPPORTED_PROFILE",
                "Solid Revolve requires only valid closed whole-sketch profile loops; open curves require explicit sheet output.");
        if (openChains == 0 && closedLoops == 0)
            throw new M2ModelException("UNSUPPORTED_PROFILE", "Revolve requires at least one valid active non-reference curve profile.");

        Dictionary<string, object> axisPlaneEvidence;
        if (!M3RevolveAxisInSketchPlane(sketch, request, out axisPlaneEvidence))
            throw new M2ModelException("REVOLVE_AXIS_NOT_IN_SKETCH_PLANE",
                "The requested world-space Revolve axis line must lie in the selected Sketch plane.");

        Body target = null;
        string targetId = null;
        if (request.Mode != "create")
        {
            target = FindBody(state.WorkPart, request.TargetBodyId);
            if (!target.IsSolidBody)
                throw new M2ModelException("UNSUPPORTED_BODY", "boolean Revolve target must identify one solid body");
            targetId = RequiredJournalId(target, "Revolve target body");
        }

        M3PlanState plan = CreateM3PlanState(binding, state, "create_revolve", capture,
            F12MassPropertyAccuracy);
        plan.RevolveRequest = CloneM3RevolveRequest(request);
        plan.Sketch = sketch;
        plan.SketchId = RequiredJournalId(sketch, "Revolve source Sketch");
        plan.SketchRecordBefore = Clone(FindSketchRecord(capture.Snapshot, plan.SketchId));
        plan.TargetBody = target;
        plan.TargetBodyId = targetId;
        plan.ProfileAnalysis = profileAnalysis;
        plan.F12SemanticBefore = capture.Snapshot;
        plan.F12CurveInventoryBefore = M3SketchProjectionCurveInventoryText(
            CaptureM3SketchProjectionCurveInventory(state.WorkPart));
        plan.RevolveAxisPlaneResidualMm = ReadFiniteDouble(axisPlaneEvidence, "axis_plane_residual_mm");
        plan.RevolveAxisNormalDot = ReadFiniteDouble(axisPlaneEvidence, "axis_direction_normal_dot");
        try { plan.BodyFingerprintsBefore = CaptureF12BodyFingerprints(state.WorkPart, plan.BodiesBefore); }
        catch (Exception ex)
        {
            ThrowIfF12AnalysisStateUnknown(ex,
                "F12 body signature capture could not prove Revolve preparation was read-only");
            throw;
        }

        double nativeStart, nativeEnd;
        M3RevolveNativeAngles(request, out nativeStart, out nativeEnd);
        Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "kind", "create_revolve" },
            { "revision_id", binding.RevisionId },
            { "sketch_id", plan.SketchId },
            { "mode", request.Mode },
            { "profile_output", request.ProfileOutput },
            { "axis_point_mm", NumberList(request.AxisPointMm) },
            { "axis_direction", NumberList(request.AxisDirection) },
            { "native_axis_direction", NumberList(M3RevolveNativeDirection(request)) },
            { "start_angle_deg", request.StartAngleDeg },
            { "end_angle_deg", request.EndAngleDeg },
            { "signed_span_deg", request.EndAngleDeg - request.StartAngleDeg },
            { "native_start_angle_deg", nativeStart },
            { "native_end_angle_deg", nativeEnd },
            { "axis_plane", axisPlaneEvidence },
            { "profile_analysis", profileAnalysis },
            { "target_body_id", targetId },
            { "target_body_before", targetId == null ? null : FindBodyMeasure(plan.BodiesBefore, targetId).PublicRecord },
            { "non_target_bodies_before", BodyMeasureRecords(plan.BodiesBefore, targetId) }
        };
        return new M3PlanSnapshot
        {
            Kind = "create_revolve",
            RevisionId = binding.RevisionId,
            SketchId = plan.SketchId,
            PublicView = publicView,
            AdapterState = plan
        };
    }

    private M3ExecutionResult ExecuteM3RevolvePlan(M2ModelBinding binding, M3PlanSnapshot prepared,
        M3PlanState plan)
    {
        if (prepared == null || plan == null || plan.RevolveRequest == null || prepared.Kind != "create_revolve")
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null,
                "INVALID_PLAN", "Revolve plan does not contain retained F13 adapter state");
        BindingState state;
        try { state = RequireState(binding); }
        catch (M2ModelException ex) { return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, ex.Code, ex.Message); }
        if (!Object.ReferenceEquals(state, plan.BindingState) || !String.Equals(plan.RevisionId, prepared.RevisionId, StringComparison.Ordinal))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "Revolve plan belongs to a different binding or revision");
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_BINDING", bindingReason);
        if (!String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_REVISION",
                "model revision changed after F13 Revolve plan preparation");
        try { RequireWritableSimplePart(state.WorkPart); }
        catch (M2ModelException ex) { return M3Execution("UNCHANGED", binding.RevisionId, null, ex.Code, ex.Message); }

        try
        {
            SnapshotResult current = Capture(binding, state);
            if (!String.Equals(CanonicalSemanticSnapshot(current.Snapshot), plan.SemanticBefore, StringComparison.Ordinal) ||
                !String.Equals(CaptureFullGeometry(state.WorkPart), plan.GeometryBefore, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN",
                    "part geometry or semantic state changed after Revolve plan preparation");
            List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart, F12MassPropertyAccuracy);
            Dictionary<string, object> bodyPreconditionEvidence;
            if (!BodyMeasuresEqual(plan.BodiesBefore, bodiesNow, out bodyPreconditionEvidence))
                return M3Execution("UNCHANGED", binding.RevisionId, bodyPreconditionEvidence, "STALE_GEOMETRY",
                    "body measurements changed after Revolve plan preparation");
            Dictionary<string, string> exactBodiesNow = CaptureF12BodyFingerprints(state.WorkPart, bodiesNow);
            Dictionary<string, object> exactBodyEvidence;
            if (!F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore, exactBodiesNow, out exactBodyEvidence))
                return M3Execution("UNCHANGED", binding.RevisionId, exactBodyEvidence, "STALE_GEOMETRY",
                    "body topology or surface/edge geometry changed after Revolve preparation");
        }
        catch (Exception ex)
        {
            M2ModelException unknown = F12AnalysisStateUnknownOrNull(ex,
                "F12 body signature read could not prove the model state before Revolve execution");
            if (unknown != null) return M3Execution("UNKNOWN", binding.RevisionId, null, unknown.Code, unknown.Message);
            return M3Execution("UNCHANGED", binding.RevisionId, null, "MODEL_READ_FAILED",
                "could not refresh model state before Revolve execution (" + ex.GetType().Name + ")");
        }

        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F13 native Revolve transaction"); }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "UPDATE_FAILED",
                "could not create an NX update mark for Revolve (" + ex.GetType().Name + ")");
        }

        try
        {
            ExecuteM3Revolve(binding, state, plan, mark);
            return plan.Execution;
        }
        catch (Exception ex)
        {
            M3ExecutionResult rolled = RestoreM3State(binding, state, plan, mark, ex);
            plan.Execution = rolled;
            return rolled;
        }
        finally { TryDeleteUndoMark(state.Session, mark); }
    }

    private void ExecuteM3Revolve(M2ModelBinding binding, BindingState state, M3PlanState plan,
        Session.UndoMarkId mark)
    {
        M3RevolveRequest request = plan.RevolveRequest;
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        Dictionary<string, object> liveAxisPlaneEvidence;
        if (!M3RevolveAxisInSketchPlane(plan.Sketch, request, out liveAxisPlaneEvidence))
            throw new M2ModelException("REVOLVE_AXIS_NOT_IN_SKETCH_PLANE",
                "the selected Sketch frame changed or the requested Revolve axis is no longer coplanar");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "an active Sketch must be ended before creating a Revolve");

        M3RevolveNativeResult native = V2SketchRevolveFactory.Create(state.WorkPart, plan.Sketch,
            GetProfileFingerprint(plan.ProfileAnalysis, "input_geometry_domain_fingerprint"), request, plan.TargetBody);
        plan.Feature = native.Feature;
        plan.FeatureId = RequiredJournalId(native.Feature, "F13 Revolve feature");
        plan.RevolveAxisTag = native.AxisTag;
        plan.RevolveAxisPointTag = native.AxisPointTag;
        plan.RevolveAxisDirectionTag = native.AxisDirectionTag;
        plan.RevolveNativeStartAngleDeg = native.NativeStartAngleDeg;
        plan.RevolveNativeEndAngleDeg = native.NativeEndAngleDeg;

        int updateErrors = state.Session.UpdateManager.DoUpdate(mark);
        if (updateErrors != 0)
            throw new M2ModelException("M3_UPDATE_FAILED", "NX update reported errors after Revolve creation");
        if (plan.ForcePostCommitVerificationFailureForTesting)
            throw new M2ModelException("F13_FORCED_POST_COMMIT_FAILURE",
                "test-only fresh native Revolve verification failure injected after Commit and update");

        M4SupportTrack m4Support;
        HashSet<string> refreshSourceSupport = new HashSet<string>(StringComparer.Ordinal);
        if (state.M4SupportTracks.TryGetValue(plan.SketchId, out m4Support))
        {
            if (String.Equals(request.Mode, "subtract", StringComparison.Ordinal) &&
                !m4Support.CutFeatureIds.Contains(plan.FeatureId)) m4Support.CutFeatureIds.Add(plan.FeatureId);
            PersistM4CutFeatureIds(plan.Sketch, m4Support.CutFeatureIds);
            refreshSourceSupport.Add(plan.SketchId);
        }
        if (state.M4SupportTracks.Count > 0)
        {
            List<object> supportRecords;
            if (!ValidateM4SupportTracks(state, out supportRecords,
                refreshSourceSupport.Count == 0 ? null : refreshSourceSupport))
            {
                plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "support_associations", supportRecords },
                    { "refreshed_source_sketch_ids", StringListAsObjects(new List<string>(refreshSourceSupport)) }
                };
                throw new M2ModelException("M4_SUPPORT_ASSOCIATION_FAILED",
                    "Revolve changed or could not preserve an M4 support-face association");
            }
        }

        SnapshotResult after = Capture(binding, state);
        plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart, F12MassPropertyAccuracy);
        plan.AffectedBodies = ResolveAffectedBodies(plan, plan.BodiesAfter, native.Feature);
        if (plan.AffectedBodies.Count == 0)
            throw new M2ModelException("M3_BODY_READ_FAILED", "NX Revolve feature returned no result body");
        plan.AffectedBodyIds = new List<string>();
        foreach (M3BodyMeasure body in plan.AffectedBodies) plan.AffectedBodyIds.Add(body.Id);
        plan.AffectedBodyIds.Sort(StringComparer.Ordinal);
        plan.AffectedBodyId = plan.AffectedBodyIds.Count == 1 ? plan.AffectedBodyIds[0] : null;
        plan.BodyFingerprintsAfter = CaptureF12BodyFingerprints(state.WorkPart, plan.BodiesAfter);
        plan.RevolveOutputGeometry = CaptureF13RevolveOutputGeometry(state.WorkPart, plan.AffectedBodies);

        plan.F12CurveInventoryAfter = M3SketchProjectionCurveInventoryText(
            CaptureM3SketchProjectionCurveInventory(state.WorkPart));
        bool curveInventoryMatches = String.Equals(plan.F12CurveInventoryBefore, plan.F12CurveInventoryAfter,
            StringComparison.Ordinal);
        Dictionary<string, object> preservationEvidence;
        bool modelStatePreserved = F12PreExistingModelStatePreserved(plan.F12SemanticBefore, after.Snapshot,
            plan, out preservationEvidence);
        preservationEvidence["complete_part_curve_inventory_preserved"] = curveInventoryMatches;
        if (!modelStatePreserved || !curveInventoryMatches)
        {
            preservationEvidence["status"] = "MISMATCH";
            plan.F12PreservationEvidence = preservationEvidence;
            plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "before_after_model_preservation", preservationEvidence }
            };
            throw new M2ModelException("F13_MODEL_PRESERVATION_FAILED",
                "Revolve changed pre-existing sketch, curve, expression, feature, dependency, part, or session state");
        }
        plan.F12PreservationEvidence = preservationEvidence;

        Dictionary<string, object> definitionEvidence;
        if (!M3RevolveDefinitionMatches(plan, native.Feature, out definitionEvidence))
        {
            plan.RevolveDefinitionEvidence = definitionEvidence;
            plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "failure_stage", "COMMITTED_REVOLVE_DEFINITION_READBACK" },
                { "revolve_definition", definitionEvidence }
            };
            throw new M2ModelException("F13_REVOLVE_READBACK_FAILED",
                "live NX Revolve Section, point-direction Axis, signed angle limits, output type, Boolean target, or Sketch parent differs from the request");
        }
        plan.RevolveDefinitionEvidence = definitionEvidence;

        Dictionary<string, object> bodyEffectEvidence;
        if (!M3RevolveBodyEffectMatches(plan, plan.AffectedBodies, plan.BodiesAfter,
            plan.RevolveOutputGeometry, out bodyEffectEvidence))
        {
            plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "body_effect", bodyEffectEvidence }
            };
            throw new M2ModelException("F13_REVOLVE_GEOMETRY_READBACK_FAILED",
                "NX Revolve output body geometry, Boolean effect, or non-target body geometry did not match the requested operation");
        }

        plan.SemanticAfter = CanonicalSemanticSnapshot(after.Snapshot);
        plan.GeometryAfter = CaptureFullGeometry(state.WorkPart);
        plan.HistoryAfter = HistoryProjection(after.Snapshot);
        if (!FeaturePresent(state.WorkPart, plan.FeatureId))
            throw new M2ModelException("M3_FEATURE_NOT_CREATED", "native Revolve feature was not present after NX update");

        Dictionary<string, object> sketchGeometryEvidence;
        if (!SketchGeometryMatches(plan, plan.Sketch, out sketchGeometryEvidence))
        {
            plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "source_sketch_geometry", sketchGeometryEvidence }
            };
            throw new M2ModelException("M3_SKETCH_CHANGED", "Revolve changed the source Sketch geometry");
        }
        Dictionary<string, object> sketchConstraintEvidence;
        if (!SketchConstraintsMatch(plan, plan.Sketch, out sketchConstraintEvidence))
        {
            plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "source_sketch_constraints", sketchConstraintEvidence }
            };
            throw new M2ModelException("M3_SKETCH_CHANGED", "Revolve changed source Sketch constraints, dimensions, or reference state");
        }

        List<object> affectedRecords = BodyMeasureRecords(plan.AffectedBodies, null);
        List<object> nonTargetBefore = F12NonTargetFingerprintRecords(plan.BodyFingerprintsBefore,
            plan.TargetBody, new List<M3BodyMeasure>());
        List<object> nonTargetAfter = F12NonTargetFingerprintRecords(plan.BodyFingerprintsAfter,
            plan.TargetBody, plan.AffectedBodies);
        double nativeStart, nativeEnd;
        M3RevolveNativeAngles(request, out nativeStart, out nativeEnd);
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "kind", "create_revolve" },
            { "sketch_id", plan.SketchId },
            { "feature_id", plan.FeatureId },
            { "body_id", plan.AffectedBodyId },
            { "body_ids", StringListAsObjects(plan.AffectedBodyIds) },
            { "bodies", affectedRecords },
            { "mode", request.Mode },
            { "profile_output", request.ProfileOutput },
            { "axis_point_mm", NumberList(request.AxisPointMm) },
            { "axis_direction", NumberList(request.AxisDirection) },
            { "native_axis_direction", NumberList(M3RevolveNativeDirection(request)) },
            { "start_angle_deg", request.StartAngleDeg },
            { "end_angle_deg", request.EndAngleDeg },
            { "signed_span_deg", request.EndAngleDeg - request.StartAngleDeg },
            { "native_start_angle_deg", nativeStart },
            { "native_end_angle_deg", nativeEnd },
            { "axis_tag", plan.RevolveAxisTag },
            { "axis_point_tag", plan.RevolveAxisPointTag },
            { "axis_direction_tag", plan.RevolveAxisDirectionTag },
            { "target_body_id", plan.TargetBodyId },
            { "target_body_before", plan.TargetBodyId == null ? null : FindBodyMeasure(plan.BodiesBefore, plan.TargetBodyId).PublicRecord },
            { "target_bodies_after", affectedRecords },
            { "non_target_bodies_before", nonTargetBefore },
            { "non_target_bodies_after", nonTargetAfter },
            { "body_inventory", BodyMeasureRecords(plan.BodiesAfter, null) },
            { "volume_mm3", AggregateBodyVolume(plan.AffectedBodies) },
            { "surface_area_mm2", F13RevolveAggregateSurfaceArea(plan.RevolveOutputGeometry) },
            { "bbox_mm", AggregateBodyBounds(plan.AffectedBodies) },
            { "result_geometry", plan.RevolveOutputGeometry },
            { "profile_analysis", plan.ProfileAnalysis },
            { "section_evidence", definitionEvidence["section_domain"] },
            { "revolve_definition", definitionEvidence },
            { "body_effect", bodyEffectEvidence },
            { "model_preservation", preservationEvidence },
            { "axis_plane", new Dictionary<string, object>(StringComparer.Ordinal)
                { { "axis_plane_residual_mm", plan.RevolveAxisPlaneResidualMm }, { "axis_direction_normal_dot", plan.RevolveAxisNormalDot } } },
            { "update_error_count", 0 },
            { "revision_id", binding.RevisionId }
        };
        if (state.M4SupportTracks.ContainsKey(plan.SketchId))
        {
            List<object> supportRecords;
            bool supportMatches = ValidateM4SupportTracks(state, out supportRecords, null);
            result.Add("support_associations", supportRecords);
            result.Add("support_associations_verified", supportMatches);
            if (!supportMatches)
                throw new M2ModelException("M4_SUPPORT_ASSOCIATION_FAILED", "Revolve left an M4 support-face association unverifiable");
        }
        plan.Execution = M3Execution("APPLIED", binding.RevisionId, result, null, null);
    }

    private M3VerificationResult VerifyM3RevolvePlan(M2ModelBinding binding, M3PlanSnapshot prepared,
        M3PlanState plan)
    {
        if (prepared == null || plan == null || plan.RevolveRequest == null || prepared.Kind != "create_revolve" ||
            plan.Execution == null || plan.Execution.State != "APPLIED")
            return M3VerificationFailure(binding == null ? null : binding.RevisionId,
                "fresh Revolve verification requires a successfully applied F13 plan");
        BindingState state = RequireState(binding);
        string bindingReason;
        if (!Object.ReferenceEquals(state, plan.BindingState) || !IsBindingCurrent(binding, out bindingReason))
            return M3VerificationFailure(binding == null ? null : binding.RevisionId,
                "NX binding changed before fresh Revolve verification");

        SnapshotResult captured = Capture(binding, state);
        string geometryNow = CaptureFullGeometry(state.WorkPart);
        List<object> historyNow = HistoryProjection(captured.Snapshot);
        List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart, F12MassPropertyAccuracy);
        Dictionary<string, object> bodyMeasureEvidence;
        bool bodiesMatch = BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesAfter, null),
            BodyMeasureRecords(bodiesNow, null), out bodyMeasureEvidence);
        Dictionary<string, string> fingerprintsNow = CaptureF12BodyFingerprints(state.WorkPart, bodiesNow);
        List<M3BodyMeasure> affectedNow = ResolveAffectedBodies(plan, bodiesNow, FindFeature(state.WorkPart, plan.FeatureId));
        Dictionary<string, object> fingerprintEvidence;
        bool fingerprintsExactlyMatch;
        List<string> roundoffAcceptedBodyTags;
        List<object> acceptedRoundoffDiffs;
        int acceptedRoundoffDiffCount, omittedAcceptedRoundoffDiffCount;
        bool fingerprintsMatch = F13AffectedBodyFingerprintsMatch(plan.BodyFingerprintsAfter, fingerprintsNow,
            plan.AffectedBodies, affectedNow, out fingerprintEvidence, out fingerprintsExactlyMatch,
            out roundoffAcceptedBodyTags, out acceptedRoundoffDiffs, out acceptedRoundoffDiffCount,
            out omittedAcceptedRoundoffDiffCount);
        if (!fingerprintsExactlyMatch)
            AddF13FingerprintCanonicalDiffEvidence(plan.BodyFingerprintsAfter, fingerprintsNow,
                roundoffAcceptedBodyTags, acceptedRoundoffDiffs, acceptedRoundoffDiffCount,
                omittedAcceptedRoundoffDiffCount, fingerprintEvidence);
        Dictionary<string, object> affectedEvidence;
        bool affectedMatch = F12AffectedBodiesMatch(plan.AffectedBodies, affectedNow, out affectedEvidence);
        Dictionary<string, object> actualOutputGeometry = CaptureF13RevolveOutputGeometry(state.WorkPart, affectedNow);
        Dictionary<string, object> outputGeometryComparisonEvidence;
        bool outputGeometryMatches = F13RevolveOutputGeometryEqual(plan.RevolveOutputGeometry,
            actualOutputGeometry, out outputGeometryComparisonEvidence);
        Dictionary<string, object> targetEvidence;
        bool nonTargetsMatch = F12NonTargetBodiesMatch(plan, bodiesNow, fingerprintsNow, out targetEvidence);
        Dictionary<string, object> preservationEvidence;
        bool preservationMatches = F12PreExistingModelStatePreserved(plan.F12SemanticBefore, captured.Snapshot,
            plan, out preservationEvidence);
        string curveInventoryNow = M3SketchProjectionCurveInventoryText(
            CaptureM3SketchProjectionCurveInventory(state.WorkPart));
        bool curvesPreserved = String.Equals(plan.F12CurveInventoryBefore, curveInventoryNow, StringComparison.Ordinal) &&
            String.Equals(plan.F12CurveInventoryAfter, curveInventoryNow, StringComparison.Ordinal);
        preservationEvidence["complete_part_curve_inventory_matches_execute_and_prepare"] = curvesPreserved;
        preservationMatches = preservationMatches && curvesPreserved;

        Dictionary<string, object> profileNow, profileStateEvidence;
        bool profileMatches = VerifyF13ProfileAnalysis(state.WorkPart, plan.Sketch, plan.ProfileAnalysis,
            out profileNow, out profileStateEvidence);
        Dictionary<string, object> definitionEvidence;
        bool definitionMatches = M3RevolveDefinitionMatches(plan, FindFeature(state.WorkPart, plan.FeatureId),
            out definitionEvidence);
        Dictionary<string, object> bodyEffectEvidence;
        bool bodyEffectMatches = M3RevolveBodyEffectMatches(plan, affectedNow, bodiesNow,
            actualOutputGeometry, out bodyEffectEvidence);
        Dictionary<string, object> sketchGeometryEvidence;
        bool sketchGeometryMatches = SketchGeometryMatches(plan, FindSketch(state.WorkPart, plan.SketchId),
            out sketchGeometryEvidence);
        Dictionary<string, object> sketchConstraintEvidence;
        bool sketchConstraintsMatch = SketchConstraintsMatch(plan, FindSketch(state.WorkPart, plan.SketchId),
            out sketchConstraintEvidence);
        bool historyMatches = String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(captured.Snapshot), StringComparison.Ordinal) &&
            String.Equals(plan.GeometryAfter, geometryNow, StringComparison.Ordinal) &&
            SemanticValuesEqual(plan.HistoryAfter, historyNow, "history") && FeaturePresent(state.WorkPart, plan.FeatureId) &&
            preservationMatches && definitionMatches;
        bool geometryMatches = sketchGeometryMatches && profileMatches && definitionMatches && bodyEffectMatches &&
            fingerprintsMatch && affectedMatch && outputGeometryMatches;

        Dictionary<string, object> geometryEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "source_sketch_geometry", sketchGeometryEvidence },
            { "profile_analysis", profileNow },
            { "profile_analysis_state", profileStateEvidence },
            { "revolve_definition", definitionEvidence },
            { "body_effect", bodyEffectEvidence },
            { "body_geometry_fingerprints_match_execute", fingerprintEvidence },
            { "affected_body_set_matches_execute", affectedEvidence },
            { "result_geometry_matches_execute", outputGeometryMatches },
            { "result_geometry_comparison", outputGeometryComparisonEvidence },
            { "result_geometry", actualOutputGeometry }
        };
        Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "expected_feature_id", plan.FeatureId },
            { "expected_sketch_id", plan.SketchId },
            { "feature_present", FeaturePresent(state.WorkPart, plan.FeatureId) },
            { "semantic_matches_execute", String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(captured.Snapshot), StringComparison.Ordinal) },
            { "full_geometry_matches_execute", String.Equals(plan.GeometryAfter, geometryNow, StringComparison.Ordinal) },
            { "feature_history_matches_execute", SemanticValuesEqual(plan.HistoryAfter, historyNow, "history") },
            { "model_preservation", preservationEvidence }
        };
        Dictionary<string, object> constraintsEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "source_sketch_constraints", sketchConstraintEvidence }
        };
        Dictionary<string, object> bodyEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", bodiesMatch && affectedMatch && fingerprintsMatch && outputGeometryMatches ? "VERIFIED" : "MISMATCH" },
            { "body_measurements_match_execute", bodyMeasureEvidence },
            { "body_geometry_fingerprints_match_execute", fingerprintEvidence },
            { "affected_body_set_matches_execute", affectedEvidence },
            { "result_geometry_matches_execute", outputGeometryMatches },
            { "result_geometry_comparison", outputGeometryComparisonEvidence },
            { "body_inventory", BodyMeasureRecords(bodiesNow, null) },
            { "affected_bodies", BodyMeasureRecords(affectedNow, null) },
            { "result_geometry", actualOutputGeometry }
        };

        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "geometry", VerificationCheck(geometryMatches ? "VERIFIED" : "MISMATCH", geometryEvidence) },
            { "constraints", VerificationCheck(sketchConstraintsMatch ? "VERIFIED" : "MISMATCH", constraintsEvidence) },
            { "history", VerificationCheck(historyMatches ? "VERIFIED" : "MISMATCH", historyEvidence) },
            { "bodies", VerificationCheck(bodiesMatch && affectedMatch && fingerprintsMatch && bodyEffectMatches ? "VERIFIED" : "MISMATCH", bodyEvidence) },
            { "non_target_bodies", VerificationCheck(nonTargetsMatch ? "VERIFIED" : "MISMATCH", targetEvidence) },
            { "model_preservation", VerificationCheck(preservationMatches ? "VERIFIED" : "MISMATCH", preservationEvidence) },
            { "profile_analysis", VerificationCheck(profileMatches ? "VERIFIED" : "MISMATCH", profileNow) },
            { "revolve_definition", VerificationCheck(definitionMatches ? "VERIFIED" : "MISMATCH", definitionEvidence) }
        };
        if (state.M4SupportTracks.ContainsKey(plan.SketchId))
        {
            RefreshM4SupportTracksFromPart(state.WorkPart, state);
            List<object> supportRecords;
            bool supportMatches = ValidateM4SupportTracks(state, out supportRecords, null);
            checks.Add("support_association", VerificationCheck(supportMatches ? "VERIFIED" : "MISMATCH",
                new Dictionary<string, object>(StringComparer.Ordinal) { { "associations", supportRecords } }));
            nonTargetsMatch = nonTargetsMatch && supportMatches;
        }

        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "kind", "create_revolve" },
            { "sketch_id", plan.SketchId },
            { "feature_id", plan.FeatureId },
            { "body_id", plan.AffectedBodyId },
            { "body_ids", StringListAsObjects(plan.AffectedBodyIds) },
            { "bodies", BodyMeasureRecords(affectedNow, null) },
            { "mode", plan.RevolveRequest.Mode },
            { "profile_output", plan.RevolveRequest.ProfileOutput },
            { "axis_point_mm", NumberList(plan.RevolveRequest.AxisPointMm) },
            { "axis_direction", NumberList(plan.RevolveRequest.AxisDirection) },
            { "start_angle_deg", plan.RevolveRequest.StartAngleDeg },
            { "end_angle_deg", plan.RevolveRequest.EndAngleDeg },
            { "signed_span_deg", plan.RevolveRequest.EndAngleDeg - plan.RevolveRequest.StartAngleDeg },
            { "target_body_id", plan.TargetBodyId },
            { "volume_mm3", AggregateBodyVolume(affectedNow) },
            { "surface_area_mm2", F13RevolveAggregateSurfaceArea(actualOutputGeometry) },
            { "bbox_mm", AggregateBodyBounds(affectedNow) },
            { "result_geometry", actualOutputGeometry },
            { "profile_analysis", profileNow },
            { "revision_id", binding.RevisionId }
        };
        bool verified = geometryMatches && sketchConstraintsMatch && historyMatches && bodiesMatch &&
            affectedMatch && fingerprintsMatch && outputGeometryMatches && bodyEffectMatches && nonTargetsMatch && preservationMatches;
        return new M3VerificationResult
        {
            Verified = verified,
            RevisionId = binding.RevisionId,
            Checks = checks,
            Result = result,
            ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified ? null : "Fresh native Revolve definition, result geometry, feature history, or non-target preservation differs from its executed plan."
        };
    }

    private static bool F13AffectedBodyFingerprintsMatch(Dictionary<string, string> expected,
        Dictionary<string, string> actual, List<M3BodyMeasure> expectedAffected,
        List<M3BodyMeasure> actualAffected, out Dictionary<string, object> evidence,
        out bool exactMapMatch, out List<string> roundoffAcceptedBodyTags,
        out List<object> acceptedRoundoffDiffs, out int acceptedRoundoffDiffCount,
        out int omittedAcceptedRoundoffDiffCount)
    {
        HashSet<string> eligibleTags = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> expectedAffectedTags = F13BodyMeasureTags(expectedAffected);
        HashSet<string> actualAffectedTags = F13BodyMeasureTags(actualAffected);
        foreach (string tag in expectedAffectedTags)
            if (actualAffectedTags.Contains(tag)) eligibleTags.Add(tag);

        bool matches = expected != null && actual != null && expected.Count == actual.Count;
        List<string> missing = new List<string>();
        List<string> added = new List<string>();
        List<string> rawChanged = new List<string>();
        List<string> unacceptedChanged = new List<string>();
        List<string> notEligible = new List<string>();
        List<string> toleratedTags = new List<string>();
        List<object> failedCanonicalComparisons = new List<object>();
        roundoffAcceptedBodyTags = new List<string>();
        acceptedRoundoffDiffs = new List<object>();
        acceptedRoundoffDiffCount = 0;
        omittedAcceptedRoundoffDiffCount = 0;

        if (expected != null)
        {
            List<string> expectedTags = new List<string>(expected.Keys);
            expectedTags.Sort(StringComparer.Ordinal);
            foreach (string tag in expectedTags)
            {
                string current;
                if (actual == null || !actual.TryGetValue(tag, out current))
                {
                    missing.Add(tag);
                    continue;
                }
                string expectedCanonical = expected[tag];
                if (String.Equals(expectedCanonical, current, StringComparison.Ordinal)) continue;
                rawChanged.Add(tag);
                if (!eligibleTags.Contains(tag))
                {
                    unacceptedChanged.Add(tag);
                    notEligible.Add(tag);
                    continue;
                }

                Dictionary<string, object> canonicalEvidence;
                if (F13BodyGeometryCanonicalMatches(expectedCanonical, current, out canonicalEvidence))
                {
                    toleratedTags.Add(tag);
                    object countValue;
                    int bodyDiffCount = canonicalEvidence.TryGetValue("accepted_roundoff_difference_count", out countValue) &&
                        countValue is int ? (int)countValue : 0;
                    acceptedRoundoffDiffCount += bodyDiffCount;
                    object listValue;
                    List<object> bodyDiffs = canonicalEvidence.TryGetValue("accepted_roundoff_differences", out listValue)
                        ? listValue as List<object> : null;
                    if (bodyDiffs != null)
                        foreach (object bodyDiffValue in bodyDiffs)
                        {
                            Dictionary<string, object> bodyDiff = bodyDiffValue as Dictionary<string, object>;
                            if (bodyDiff == null) continue;
                            if (acceptedRoundoffDiffs.Count < F13CanonicalMaxDetailedRoundoffDifferences)
                                acceptedRoundoffDiffs.Add(F13RoundoffDifferenceWithBodyTag(tag, bodyDiff));
                            else omittedAcceptedRoundoffDiffCount++;
                        }
                    object omittedValue;
                    if (canonicalEvidence.TryGetValue("omitted_accepted_roundoff_difference_count", out omittedValue) &&
                        omittedValue is int) omittedAcceptedRoundoffDiffCount += (int)omittedValue;
                }
                else
                {
                    unacceptedChanged.Add(tag);
                    object failure;
                    canonicalEvidence.TryGetValue("comparison_failure", out failure);
                    failedCanonicalComparisons.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "body_tag", tag },
                        { "failure", failure }
                    });
                }
            }
        }
        if (actual != null)
        {
            List<string> actualTags = new List<string>(actual.Keys);
            actualTags.Sort(StringComparer.Ordinal);
            foreach (string tag in actualTags)
                if (expected == null || !expected.ContainsKey(tag)) added.Add(tag);
        }
        missing.Sort(StringComparer.Ordinal);
        added.Sort(StringComparer.Ordinal);
        rawChanged.Sort(StringComparer.Ordinal);
        unacceptedChanged.Sort(StringComparer.Ordinal);
        notEligible.Sort(StringComparer.Ordinal);
        toleratedTags.Sort(StringComparer.Ordinal);
        roundoffAcceptedBodyTags.AddRange(toleratedTags);

        exactMapMatch = expected != null && actual != null && expected.Count == actual.Count &&
            missing.Count == 0 && added.Count == 0 && rawChanged.Count == 0;
        matches = matches && missing.Count == 0 && added.Count == 0 && unacceptedChanged.Count == 0;
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", matches ? "VERIFIED" : "MISMATCH" },
            { "body_count_before", expected == null ? 0 : expected.Count },
            { "body_count_after", actual == null ? 0 : actual.Count },
            { "missing_body_tags", StringListAsObjects(missing) },
            { "added_body_tags", StringListAsObjects(added) },
            { "changed_geometry_body_tags", StringListAsObjects(rawChanged) },
            { "roundoff_tolerated_body_tags", StringListAsObjects(toleratedTags) },
            { "effective_mismatch_body_tags", StringListAsObjects(unacceptedChanged) },
            { "roundoff_not_eligible_body_tags", StringListAsObjects(notEligible) },
            { "canonical_comparison_failures", failedCanonicalComparisons },
            { "raw_canonical_map_exact_match", exactMapMatch },
            { "comparison", "F13 fresh Verify affected-result body Tags use a path-whitelisted machine-roundoff canonical comparison; all other bodies require exact F12 canonical equality" }
        };
        return matches;
    }

    private static HashSet<string> F13BodyMeasureTags(List<M3BodyMeasure> bodies)
    {
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        if (bodies != null)
            foreach (M3BodyMeasure body in bodies)
                if (body != null && body.Body != null) tags.Add(body.Body.Tag.ToString());
        return tags;
    }

    private static Dictionary<string, object> F13RoundoffDifferenceWithBodyTag(string tag,
        Dictionary<string, object> difference)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("body_tag", tag);
        if (difference != null)
            foreach (KeyValuePair<string, object> pair in difference)
                if (!String.Equals(pair.Key, "body_tag", StringComparison.Ordinal)) result.Add(pair.Key, pair.Value);
        return result;
    }

    internal static bool F13BodyGeometryCanonicalMatches(string expected, string actual,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> acceptedDifferences = new List<object>();
        int acceptedDifferenceCount = 0;
        int omittedAcceptedDifferenceCount = 0;
        Dictionary<string, object> failure = null;
        bool matches = false;
        if (expected != null && actual != null)
        {
            try
            {
                F13CanonicalBodyFingerprintComparer comparer = new F13CanonicalBodyFingerprintComparer(expected, actual);
                matches = comparer.Compare();
                acceptedDifferences = comparer.AcceptedDifferences;
                acceptedDifferenceCount = comparer.AcceptedDifferenceCount;
                omittedAcceptedDifferenceCount = comparer.OmittedAcceptedDifferenceCount;
                failure = comparer.Failure;
            }
            catch (Exception ex)
            {
                failure = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "path", "" },
                    { "reason", "canonical_parser_exception" },
                    { "exception_type", ex.GetType().Name }
                };
            }
        }
        else
            failure = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "path", "" }, { "reason", "canonical_fingerprint_missing" }
            };

        bool exact = expected != null && actual != null &&
            String.Equals(expected, actual, StringComparison.Ordinal);
        evidence.Add("status", matches ? "VERIFIED" : "MISMATCH");
        evidence.Add("canonical_exact_match", exact);
        evidence.Add("roundoff_only_match", matches && !exact);
        evidence.Add("accepted_roundoff_difference_count", acceptedDifferenceCount);
        evidence.Add("accepted_roundoff_differences", acceptedDifferences);
        evidence.Add("omitted_accepted_roundoff_difference_count", omittedAcceptedDifferenceCount);
        evidence.Add("comparison_failure", failure);
        evidence.Add("tolerance_policy", F13CanonicalRoundoffPolicy());
        return matches;
    }

    private static Dictionary<string, object> F13CanonicalRoundoffPolicy()
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "scope", "fresh F13 Verify affected-result body fingerprints only" },
            { "machine_epsilon", F13MachineEpsilon },
            { "uv_parameter_rule", "abs(delta) <= 64 * epsilon * max(1, abs(expected), abs(actual))" },
            { "point_mm_rule", "scale = max(1, abs(each expected component), abs(each actual component)); 3D Euclidean delta <= min(64 * epsilon * scale, 1e-7 mm)" },
            { "normal_rule", "when numerically changed: each finite component delta <= 64 * epsilon * max(1, abs(expected component), abs(actual component)); both vector norms are unit within 64 * epsilon; atan2(|cross|, dot) <= 1e-12 rad" },
            { "allowlisted_paths", new List<object>
                {
                    "faces[i].uv_limits[j] (rank 1, lower bound 0, length 4)",
                    "faces[i].surface_samples[k].uv[j] (rank 1, lower bound 0, length 2)",
                    "faces[i].surface_samples[k].point_mm[j] (rank 1, lower bound 0, length 3)",
                    "faces[i].surface_samples[k].normal[j] (rank 1, lower bound 0, length 3)"
                } },
            { "non_allowlisted_values", "exact canonical token equality" },
            { "array_rank_shape_and_lower_bounds", "exact" }
        };
    }

    private static void AddF13FingerprintCanonicalDiffEvidence(
        Dictionary<string, string> before, Dictionary<string, string> after,
        List<string> roundoffAcceptedBodyTags, List<object> acceptedRoundoffDiffs,
        int acceptedRoundoffDiffCount, int omittedAcceptedRoundoffDiffCount,
        Dictionary<string, object> evidence)
    {
        List<string> changedTags = new List<string>();
        if (before != null && after != null)
            foreach (KeyValuePair<string, string> pair in before)
            {
                string current;
                if (after.TryGetValue(pair.Key, out current) &&
                    !String.Equals(pair.Value, current, StringComparison.Ordinal))
                    changedTags.Add(pair.Key);
            }
        changedTags.Sort(StringComparer.Ordinal);

        HashSet<string> includedCanonicalTags = new HashSet<string>(StringComparer.Ordinal);
        int maxIncludedTags = Math.Min(changedTags.Count, F13FingerprintCanonicalDiagnosticMaxChangedTags);
        for (int i = 0; i < maxIncludedTags; i++) includedCanonicalTags.Add(changedTags[i]);
        List<string> acceptedBodyTags = new List<string>();
        int roundoffAcceptedBodyTagCount = roundoffAcceptedBodyTags == null ? 0 : roundoffAcceptedBodyTags.Count;
        if (roundoffAcceptedBodyTags != null)
            foreach (string tag in roundoffAcceptedBodyTags)
                if (includedCanonicalTags.Contains(tag)) acceptedBodyTags.Add(tag);
        acceptedBodyTags.Sort(StringComparer.Ordinal);
        List<object> selectedAcceptedDiffs = new List<object>();
        if (acceptedRoundoffDiffs != null)
            foreach (object value in acceptedRoundoffDiffs)
            {
                Dictionary<string, object> diff = value as Dictionary<string, object>;
                object tagValue;
                if (diff != null && diff.TryGetValue("body_tag", out tagValue) &&
                    includedCanonicalTags.Contains(Convert.ToString(tagValue, CultureInfo.InvariantCulture)) &&
                    selectedAcceptedDiffs.Count < F13CanonicalMaxDetailedRoundoffDifferences)
                    selectedAcceptedDiffs.Add(value);
            }

        List<object> canonicalDiffs = new List<object>();
        bool anyIncompleteCanonical = false;
        for (int i = 0; i < maxIncludedTags; i++)
        {
            string tag = changedTags[i];
            string beforeCanonical = before[tag];
            string afterCanonical = after[tag];
            int beforeLength = beforeCanonical == null ? 0 : beforeCanonical.Length;
            int afterLength = afterCanonical == null ? 0 : afterCanonical.Length;

            bool addedCanonical = false;
            while (!addedCanonical)
            {
                Dictionary<string, object> fullDiff = F13FingerprintCanonicalDiffRecord(tag,
                    beforeCanonical, afterCanonical, beforeLength, afterLength);
                bool hasMissingCanonical = beforeCanonical == null || afterCanonical == null;
                int fullTrialBytes = Int32.MaxValue;
                if ((long)beforeLength + afterLength <= F13FingerprintCanonicalDiagnosticBytesPerOccurrence())
                {
                    List<object> fullTrial = new List<object>(canonicalDiffs);
                    fullTrial.Add(fullDiff);
                    F13BuildFingerprintCanonicalDiagnostic(fullTrial, changedTags.Count,
                        anyIncompleteCanonical || hasMissingCanonical, acceptedBodyTags,
                        roundoffAcceptedBodyTagCount,
                        selectedAcceptedDiffs, acceptedRoundoffDiffCount,
                        omittedAcceptedRoundoffDiffCount, out fullTrialBytes);
                    if (fullTrialBytes <= F13FingerprintCanonicalDiagnosticBytesPerOccurrence())
                    {
                        canonicalDiffs.Add(fullDiff);
                        anyIncompleteCanonical = anyIncompleteCanonical || hasMissingCanonical;
                        addedCanonical = true;
                        break;
                    }
                }

                // Keep complete canonical bodies ahead of optional per-path summaries.
                // Drop detail rows in deterministic halves before considering a prefix.
                if (selectedAcceptedDiffs.Count > 0)
                {
                    selectedAcceptedDiffs = new List<object>(selectedAcceptedDiffs.GetRange(0,
                        selectedAcceptedDiffs.Count / 2));
                    omittedAcceptedRoundoffDiffCount = Math.Max(omittedAcceptedRoundoffDiffCount,
                        acceptedRoundoffDiffCount - selectedAcceptedDiffs.Count);
                    continue;
                }

                int maxPrefixLength = Math.Min(Math.Max(beforeLength, afterLength),
                    F13FingerprintCanonicalDiagnosticBytesPerOccurrence());
                int low = 0;
                int high = maxPrefixLength;
                int bestPrefixLength = -1;
                Dictionary<string, object> bestDiff = null;
                while (low <= high)
                {
                    int prefixLength = low + (high - low) / 2;
                    Dictionary<string, object> candidate = F13FingerprintCanonicalDiffRecord(tag,
                        beforeCanonical, afterCanonical, prefixLength, prefixLength);
                    List<object> trial = new List<object>(canonicalDiffs);
                    trial.Add(candidate);
                    int trialBytes;
                    F13BuildFingerprintCanonicalDiagnostic(trial, changedTags.Count,
                        true, acceptedBodyTags, roundoffAcceptedBodyTagCount, selectedAcceptedDiffs,
                        acceptedRoundoffDiffCount, omittedAcceptedRoundoffDiffCount, out trialBytes);
                    if (trialBytes <= F13FingerprintCanonicalDiagnosticBytesPerOccurrence())
                    {
                        bestPrefixLength = prefixLength;
                        bestDiff = candidate;
                        low = prefixLength + 1;
                    }
                    else high = prefixLength - 1;
                }

                if (bestPrefixLength >= 0)
                {
                    canonicalDiffs.Add(bestDiff);
                    anyIncompleteCanonical = true;
                    addedCanonical = true;
                    // Preserve the largest useful prefix for this changed body. Remaining
                    // tags stay explicitly omitted instead of diluting the available bytes.
                    break;
                }

                break;
            }
            if (!addedCanonical) break;
            if (anyIncompleteCanonical) break;
        }

        int serializedDiagnosticBytes;
        Dictionary<string, object> diagnostic = F13BuildFingerprintCanonicalDiagnostic(
            canonicalDiffs, changedTags.Count, anyIncompleteCanonical, acceptedBodyTags,
            roundoffAcceptedBodyTagCount, selectedAcceptedDiffs, acceptedRoundoffDiffCount,
            omittedAcceptedRoundoffDiffCount,
            out serializedDiagnosticBytes);
        if ((long)serializedDiagnosticBytes * F13FingerprintCanonicalDiagnosticEvidenceCopies >
            F13FingerprintCanonicalDiagnosticMaxSerializedUtf8Bytes)
        {
            canonicalDiffs.Clear();
            diagnostic = F13BuildFingerprintCanonicalDiagnostic(canonicalDiffs,
                changedTags.Count, changedTags.Count != 0, acceptedBodyTags,
                roundoffAcceptedBodyTagCount, new List<object>(), acceptedRoundoffDiffCount,
                acceptedRoundoffDiffCount,
                out serializedDiagnosticBytes);
        }
        evidence["changed_geometry_body_fingerprint_canonical_diagnostic"] = diagnostic;
    }

    private static Dictionary<string, object> F13FingerprintCanonicalDiffRecord(string tag,
        string beforeCanonical, string afterCanonical, int beforePrefixChars, int afterPrefixChars)
    {
        bool beforePresent = beforeCanonical != null;
        bool afterPresent = afterCanonical != null;
        int beforeLength = beforePresent ? beforeCanonical.Length : -1;
        int afterLength = afterPresent ? afterCanonical.Length : -1;
        int beforeIncludedChars = beforePresent ? Math.Min(beforePrefixChars, beforeLength) : 0;
        int afterIncludedChars = afterPresent ? Math.Min(afterPrefixChars, afterLength) : 0;
        bool beforeTruncated = beforePresent && beforeIncludedChars < beforeLength;
        bool afterTruncated = afterPresent && afterIncludedChars < afterLength;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "body_tag", tag },
            { "before_canonical_original_length_chars", beforeLength },
            { "before_canonical_original_utf8_bytes", beforePresent ? Encoding.UTF8.GetByteCount(beforeCanonical) : -1 },
            { "before_canonical_present", beforePresent },
            { "before_canonical_truncated", beforeTruncated },
            { "before_canonical", beforePresent ? beforeCanonical.Substring(0, beforeIncludedChars) : null },
            { "after_canonical_original_length_chars", afterLength },
            { "after_canonical_original_utf8_bytes", afterPresent ? Encoding.UTF8.GetByteCount(afterCanonical) : -1 },
            { "after_canonical_present", afterPresent },
            { "after_canonical_truncated", afterTruncated },
            { "after_canonical", afterPresent ? afterCanonical.Substring(0, afterIncludedChars) : null }
        };
    }

    private static int F13FingerprintCanonicalDiagnosticBytesPerOccurrence()
    {
        return F13FingerprintCanonicalDiagnosticMaxSerializedUtf8Bytes /
            F13FingerprintCanonicalDiagnosticEvidenceCopies;
    }

    private static Dictionary<string, object> F13BuildFingerprintCanonicalDiagnostic(
        List<object> canonicalDiffs, int changedTagCount, bool anyTruncatedOrMissing,
        List<string> roundoffAcceptedBodyTags, int roundoffAcceptedBodyTagCount,
        List<object> acceptedRoundoffDiffs,
        int acceptedRoundoffDiffCount, int omittedAcceptedRoundoffDiffCount,
        out int serializedBytes)
    {
        int includedTagCount = canonicalDiffs == null ? 0 : canonicalDiffs.Count;
        int omittedTagCount = Math.Max(0, changedTagCount - includedTagCount);
        int includedRoundoffTagCount = roundoffAcceptedBodyTags == null ? 0 : roundoffAcceptedBodyTags.Count;
        int omittedRoundoffTagCount = Math.Max(0, roundoffAcceptedBodyTagCount - includedRoundoffTagCount);
        int includedRoundoffDiffCount = acceptedRoundoffDiffs == null ? 0 : acceptedRoundoffDiffs.Count;
        int effectiveOmittedRoundoffDiffCount = Math.Max(omittedAcceptedRoundoffDiffCount,
            acceptedRoundoffDiffCount - includedRoundoffDiffCount);
        Dictionary<string, object> diagnostic = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "changed_body_fingerprint_canonical_diffs", canonicalDiffs ?? new List<object>() },
            { "changed_body_tag_count", changedTagCount },
            { "included_changed_body_tag_count", includedTagCount },
            { "omitted_changed_body_tag_count", omittedTagCount },
            { "max_changed_body_tags", F13FingerprintCanonicalDiagnosticMaxChangedTags },
            { "changed_tag_canonicals_complete", omittedTagCount == 0 && !anyTruncatedOrMissing },
            { "roundoff_tolerated_body_tags", StringListAsObjects(roundoffAcceptedBodyTags ?? new List<string>()) },
            { "omitted_roundoff_tolerated_body_tag_count", omittedRoundoffTagCount },
            { "accepted_roundoff_difference_count", acceptedRoundoffDiffCount },
            { "included_accepted_roundoff_difference_detail_count", includedRoundoffDiffCount },
            { "omitted_accepted_roundoff_difference_detail_count", effectiveOmittedRoundoffDiffCount },
            { "accepted_roundoff_difference_details_complete", effectiveOmittedRoundoffDiffCount == 0 },
            { "accepted_roundoff_differences", acceptedRoundoffDiffs ?? new List<object>() },
            { "tolerance_policy", F13CanonicalRoundoffPolicy() },
            { "serialized_utf8_byte_budget_per_container", F13FingerprintCanonicalDiagnosticBytesPerOccurrence() },
            { "serialized_utf8_byte_budget_across_geometry_and_bodies", F13FingerprintCanonicalDiagnosticMaxSerializedUtf8Bytes },
            { "serialized_utf8_container_occurrences_in_checks", F13FingerprintCanonicalDiagnosticEvidenceCopies },
            { "serialized_utf8_bytes_per_container", 0 },
            { "serialized_utf8_bytes_across_checks", 0 }
        };
        Dictionary<string, object> wrapper = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "changed_geometry_body_fingerprint_canonical_diagnostic", diagnostic }
        };

        int estimate = 0;
        for (int iteration = 0; iteration < 4; iteration++)
        {
            diagnostic["serialized_utf8_bytes_per_container"] = estimate;
            diagnostic["serialized_utf8_bytes_across_checks"] = estimate * F13FingerprintCanonicalDiagnosticEvidenceCopies;
            int measured = Encoding.UTF8.GetByteCount(M2Json.CanonicalText(wrapper));
            if (measured == estimate) break;
            estimate = measured;
        }
        diagnostic["serialized_utf8_bytes_per_container"] = estimate;
        diagnostic["serialized_utf8_bytes_across_checks"] = estimate * F13FingerprintCanonicalDiagnosticEvidenceCopies;
        serializedBytes = Encoding.UTF8.GetByteCount(M2Json.CanonicalText(wrapper));
        return diagnostic;
    }

    private sealed class F13CanonicalPathSegment
    {
        internal readonly string Key;
        internal readonly int Index;
        internal readonly bool IsIndex;

        private F13CanonicalPathSegment(string key, int index, bool isIndex)
        {
            Key = key;
            Index = index;
            IsIndex = isIndex;
        }

        internal static F13CanonicalPathSegment ForKey(string key)
        {
            return new F13CanonicalPathSegment(key, -1, false);
        }

        internal static F13CanonicalPathSegment ForIndex(int index)
        {
            return new F13CanonicalPathSegment(null, index, true);
        }
    }

    private sealed class F13CanonicalCursor
    {
        internal readonly string Text;
        internal int Position;

        internal F13CanonicalCursor(string text)
        {
            Text = text;
            Position = 0;
        }

        internal bool AtEnd { get { return Position == Text.Length; } }

        internal bool Peek(char value)
        {
            return Position < Text.Length && Text[Position] == value;
        }

        internal bool StartsWith(string value)
        {
            return Position <= Text.Length - value.Length &&
                String.CompareOrdinal(Text, Position, value, 0, value.Length) == 0;
        }

        internal bool TryConsume(char value)
        {
            if (!Peek(value)) return false;
            Position++;
            return true;
        }

        internal bool TryConsume(string value)
        {
            if (!StartsWith(value)) return false;
            Position += value.Length;
            return true;
        }

        internal bool LooksLikeEncodedString()
        {
            if (Position >= Text.Length || Text[Position] < '0' || Text[Position] > '9') return false;
            int cursor = Position;
            while (cursor < Text.Length && Text[cursor] >= '0' && Text[cursor] <= '9') cursor++;
            return cursor < Text.Length && Text[cursor] == ':';
        }

        internal bool TryReadEncodedString(out string value)
        {
            value = null;
            if (!LooksLikeEncodedString()) return false;
            int colon = Text.IndexOf(':', Position);
            if (colon < 0) return false;
            int length;
            string lengthToken = Text.Substring(Position, colon - Position);
            if (!Int32.TryParse(lengthToken, NumberStyles.None,
                CultureInfo.InvariantCulture, out length) || length < 0 ||
                !String.Equals(lengthToken, length.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) return false;
            int valueStart = colon + 1;
            if (length > Text.Length - valueStart) return false;
            value = Text.Substring(valueStart, length);
            Position = valueStart + length;
            return true;
        }

        internal bool TryReadIntegerUntil(char delimiter, out int value)
        {
            value = 0;
            int end = Text.IndexOf(delimiter, Position);
            if (end < 0 || end == Position) return false;
            string token = Text.Substring(Position, end - Position);
            if (!Int32.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                return false;
            if (!String.Equals(token, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                return false;
            Position = end + 1;
            return true;
        }

        internal bool TryReadScalarToken(out string raw)
        {
            raw = null;
            int end = Text.IndexOf(';', Position);
            if (end < 0 || end == Position) return false;
            raw = Text.Substring(Position, end + 1 - Position);
            Position = end + 1;
            return true;
        }
    }

    private sealed class F13CanonicalBodyFingerprintComparer
    {
        private readonly F13CanonicalCursor _expected;
        private readonly F13CanonicalCursor _actual;
        private readonly List<F13CanonicalPathSegment> _path = new List<F13CanonicalPathSegment>();
        private int _depth;

        internal readonly List<object> AcceptedDifferences = new List<object>();
        internal int AcceptedDifferenceCount;
        internal int OmittedAcceptedDifferenceCount;
        internal Dictionary<string, object> Failure;

        internal F13CanonicalBodyFingerprintComparer(string expected, string actual)
        {
            _expected = new F13CanonicalCursor(expected);
            _actual = new F13CanonicalCursor(actual);
        }

        internal bool Compare()
        {
            if (!CompareValue()) return false;
            if (!_expected.AtEnd || !_actual.AtEnd)
                return Fail("trailing_canonical_data");
            return true;
        }

        private bool CompareValue()
        {
            if (_depth >= 64) return Fail("canonical_nesting_depth_exceeded");
            _depth++;
            try { return CompareValueCore(); }
            finally { _depth--; }
        }

        private bool CompareValueCore()
        {
            string targetArrayKind = F13CanonicalTargetArrayKind(_path);
            if (targetArrayKind != null) return CompareTargetArray(targetArrayKind);

            bool expectedMap = _expected.Peek('{');
            bool actualMap = _actual.Peek('{');
            if (expectedMap || actualMap)
                return expectedMap && actualMap ? CompareMap() : Fail("canonical_value_kind_mismatch");

            bool expectedList = _expected.Peek('[');
            bool actualList = _actual.Peek('[');
            if (expectedList || actualList)
                return expectedList && actualList ? CompareList() : Fail("canonical_value_kind_mismatch");

            bool expectedArray = _expected.StartsWith("array(");
            bool actualArray = _actual.StartsWith("array(");
            if (expectedArray || actualArray)
                return expectedArray && actualArray ? CompareArray() : Fail("canonical_value_kind_mismatch");

            bool expectedNull = _expected.StartsWith("null;");
            bool actualNull = _actual.StartsWith("null;");
            if (expectedNull || actualNull)
            {
                if (!expectedNull || !actualNull) return Fail("canonical_value_kind_mismatch");
                return _expected.TryConsume("null;") && _actual.TryConsume("null;") ||
                    Fail("invalid_null_token");
            }

            bool expectedString = _expected.LooksLikeEncodedString();
            bool actualString = _actual.LooksLikeEncodedString();
            if (expectedString || actualString)
            {
                string expectedValue, actualValue;
                if (!expectedString || !actualString ||
                    !_expected.TryReadEncodedString(out expectedValue) ||
                    !_actual.TryReadEncodedString(out actualValue)) return Fail("invalid_or_mismatched_string_token");
                return String.Equals(expectedValue, actualValue, StringComparison.Ordinal) ||
                    Fail("canonical_string_value_mismatch");
            }

            return CompareScalarExact();
        }

        private bool CompareMap()
        {
            if (!_expected.TryConsume('{') || !_actual.TryConsume('{')) return Fail("invalid_map_start");
            string previousKey = null;
            while (true)
            {
                bool expectedEnd = _expected.Peek('}');
                bool actualEnd = _actual.Peek('}');
                if (expectedEnd || actualEnd)
                {
                    if (!expectedEnd || !actualEnd) return Fail("map_key_set_mismatch");
                    _expected.TryConsume('}');
                    _actual.TryConsume('}');
                    return true;
                }

                string expectedKey, actualKey;
                if (!_expected.TryReadEncodedString(out expectedKey) ||
                    !_actual.TryReadEncodedString(out actualKey)) return Fail("invalid_map_key");
                if (!String.Equals(expectedKey, actualKey, StringComparison.Ordinal))
                    return Fail("map_key_mismatch");
                if (previousKey != null && StringComparer.Ordinal.Compare(previousKey, expectedKey) >= 0)
                    return Fail("noncanonical_map_key_order");
                previousKey = expectedKey;
                _path.Add(F13CanonicalPathSegment.ForKey(expectedKey));
                bool childMatches = CompareValue();
                _path.RemoveAt(_path.Count - 1);
                if (!childMatches) return false;
            }
        }

        private bool CompareList()
        {
            if (!_expected.TryConsume('[') || !_actual.TryConsume('[')) return Fail("invalid_list_start");
            int index = 0;
            while (true)
            {
                bool expectedEnd = _expected.Peek(']');
                bool actualEnd = _actual.Peek(']');
                if (expectedEnd || actualEnd)
                {
                    if (!expectedEnd || !actualEnd) return Fail("list_length_mismatch");
                    _expected.TryConsume(']');
                    _actual.TryConsume(']');
                    return true;
                }

                _path.Add(F13CanonicalPathSegment.ForIndex(index));
                bool childMatches = CompareValue();
                _path.RemoveAt(_path.Count - 1);
                if (!childMatches) return false;
                index++;
            }
        }

        private bool CompareArray()
        {
            int[] expectedLowerBounds, expectedLengths, actualLowerBounds, actualLengths;
            int expectedElementCount, actualElementCount;
            if (!TryReadArrayHeader(_expected, out expectedLowerBounds, out expectedLengths, out expectedElementCount) ||
                !TryReadArrayHeader(_actual, out actualLowerBounds, out actualLengths, out actualElementCount))
                return Fail("invalid_array_shape_header");
            if (!IntArrayEqual(expectedLowerBounds, actualLowerBounds) ||
                !IntArrayEqual(expectedLengths, actualLengths) || expectedElementCount != actualElementCount)
                return Fail("array_rank_shape_or_lower_bound_mismatch");
            for (int i = 0; i < expectedElementCount; i++)
            {
                _path.Add(F13CanonicalPathSegment.ForIndex(i));
                bool childMatches = CompareValue();
                _path.RemoveAt(_path.Count - 1);
                if (!childMatches) return false;
            }
            if (!_expected.TryConsume(']') || !_actual.TryConsume(']')) return Fail("array_element_count_mismatch");
            return true;
        }

        private bool CompareTargetArray(string kind)
        {
            int expectedLength = kind == "uv_limits" ? 4 : kind == "surface_uv" ? 2 : 3;
            int[] expectedLowerBounds, expectedLengths, actualLowerBounds, actualLengths;
            int expectedElementCount, actualElementCount;
            if (!TryReadArrayHeader(_expected, out expectedLowerBounds, out expectedLengths, out expectedElementCount) ||
                !TryReadArrayHeader(_actual, out actualLowerBounds, out actualLengths, out actualElementCount))
                return Fail("invalid_allowlisted_array_header");
            if (!IsExpectedTargetArrayShape(expectedLowerBounds, expectedLengths, expectedElementCount, expectedLength) ||
                !IsExpectedTargetArrayShape(actualLowerBounds, actualLengths, actualElementCount, expectedLength) ||
                !IntArrayEqual(expectedLowerBounds, actualLowerBounds) ||
                !IntArrayEqual(expectedLengths, actualLengths))
                return Fail("allowlisted_array_rank_shape_or_lower_bound_mismatch");

            if (kind == "point_mm") return ComparePointVector(expectedLength);
            if (kind == "normal") return CompareNormalVector(expectedLength);

            for (int i = 0; i < expectedLength; i++)
            {
                _path.Add(F13CanonicalPathSegment.ForIndex(i));
                string expectedRaw, actualRaw;
                double expectedValue, actualValue;
                if (!TryReadFiniteNumber(_expected, out expectedRaw, out expectedValue) ||
                    !TryReadFiniteNumber(_actual, out actualRaw, out actualValue))
                {
                    bool failed = Fail("allowlisted_value_is_not_finite_numeric");
                    _path.RemoveAt(_path.Count - 1);
                    return failed;
                }
                double difference = Math.Abs(expectedValue - actualValue);
                double tolerance = F13CanonicalUvRoundoffMultiplier * F13MachineEpsilon *
                    Math.Max(1.0, Math.Max(Math.Abs(expectedValue), Math.Abs(actualValue)));
                if (Double.IsNaN(difference) || Double.IsInfinity(difference) || difference > tolerance)
                {
                    bool failed = FailNumeric("uv_parameter_roundoff_exceeded", expectedRaw,
                        actualRaw, expectedValue, actualValue, difference, tolerance);
                    _path.RemoveAt(_path.Count - 1);
                    return failed;
                }
                if (!String.Equals(expectedRaw, actualRaw, StringComparison.Ordinal))
                    AddAcceptedDifference(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "path", F13CanonicalPathText(_path) },
                        { "category", "uv_parameter" },
                        { "expected_canonical_token", expectedRaw },
                        { "actual_canonical_token", actualRaw },
                        { "expected_value", expectedValue },
                        { "actual_value", actualValue },
                        { "absolute_difference", difference },
                        { "maximum_allowed_difference", tolerance }
                    });
                _path.RemoveAt(_path.Count - 1);
            }
            if (!_expected.TryConsume(']') || !_actual.TryConsume(']')) return Fail("allowlisted_array_element_count_mismatch");
            return true;
        }

        private bool ComparePointVector(int length)
        {
            double[] expectedValues, actualValues;
            string[] expectedTokens, actualTokens;
            if (!ReadFiniteVector(length, out expectedValues, out expectedTokens, out actualValues, out actualTokens))
                return false;
            if (!_expected.TryConsume(']') || !_actual.TryConsume(']')) return Fail("point_vector_element_count_mismatch");
            if (VectorNumericValuesEqual(expectedValues, actualValues)) return true;

            double difference = EuclideanDifference(expectedValues, actualValues);
            double scale = MaxAbs(expectedValues, actualValues);
            double tolerance = Math.Min(F13CanonicalPointRoundoffMultiplier * F13MachineEpsilon *
                Math.Max(1.0, scale), F13CanonicalPointMaxDifferenceMm);
            if (Double.IsNaN(difference) || Double.IsInfinity(difference) || difference > tolerance)
                return FailVector("point_mm_roundoff_exceeded", "sample_point_euclidean_mm",
                    expectedTokens, actualTokens, expectedValues, actualValues, difference, tolerance);
            AddAcceptedDifference(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "path", F13CanonicalPathText(_path) },
                { "category", "sample_point_mm" },
                { "expected_canonical_tokens", StringArrayAsObjects(expectedTokens) },
                { "actual_canonical_tokens", StringArrayAsObjects(actualTokens) },
                { "expected_values_mm", NumberArrayAsObjects(expectedValues) },
                { "actual_values_mm", NumberArrayAsObjects(actualValues) },
                { "euclidean_difference_mm", difference },
                { "maximum_allowed_difference_mm", tolerance }
            });
            return true;
        }

        private bool CompareNormalVector(int length)
        {
            double[] expectedValues, actualValues;
            string[] expectedTokens, actualTokens;
            if (!ReadFiniteVector(length, out expectedValues, out expectedTokens, out actualValues, out actualTokens))
                return false;
            if (!_expected.TryConsume(']') || !_actual.TryConsume(']')) return Fail("normal_vector_element_count_mismatch");
            if (VectorNumericValuesEqual(expectedValues, actualValues)) return true;

            double maximumComponentDifference = 0.0;
            double maximumComponentTolerance = 0.0;
            for (int i = 0; i < length; i++)
            {
                _path.Add(F13CanonicalPathSegment.ForIndex(i));
                double difference = Math.Abs(expectedValues[i] - actualValues[i]);
                double tolerance = F13CanonicalNormalComponentRoundoffMultiplier * F13MachineEpsilon *
                    Math.Max(1.0, Math.Max(Math.Abs(expectedValues[i]), Math.Abs(actualValues[i])));
                maximumComponentDifference = Math.Max(maximumComponentDifference, difference);
                maximumComponentTolerance = Math.Max(maximumComponentTolerance, tolerance);
                if (Double.IsNaN(difference) || Double.IsInfinity(difference) || difference > tolerance)
                {
                    bool failed = FailNumeric("normal_component_roundoff_exceeded", expectedTokens[i],
                        actualTokens[i], expectedValues[i], actualValues[i], difference, tolerance);
                    _path.RemoveAt(_path.Count - 1);
                    return failed;
                }
                _path.RemoveAt(_path.Count - 1);
            }

            double expectedLength = VectorLength(expectedValues);
            double actualLength = VectorLength(actualValues);
            double normTolerance = F13CanonicalNormalLengthRoundoffMultiplier * F13MachineEpsilon *
                Math.Max(1.0, Math.Max(expectedLength, actualLength));
            if (Double.IsNaN(expectedLength) || Double.IsInfinity(expectedLength) ||
                Double.IsNaN(actualLength) || Double.IsInfinity(actualLength) ||
                Math.Abs(expectedLength - 1.0) > normTolerance ||
                Math.Abs(actualLength - 1.0) > normTolerance)
                return FailVector("normal_vector_not_unit_within_machine_roundoff", "normal_unit_norm",
                    expectedTokens, actualTokens, expectedValues, actualValues,
                    Math.Abs(expectedLength - actualLength), normTolerance);

            double angle = VectorAngle(expectedValues, actualValues, expectedLength, actualLength);
            if (Double.IsNaN(angle) || Double.IsInfinity(angle) || angle > F13CanonicalNormalMaxAngleRad)
                return FailVector("normal_vector_angle_exceeded", "normal_angle_rad",
                    expectedTokens, actualTokens, expectedValues, actualValues, angle,
                    F13CanonicalNormalMaxAngleRad);
            AddAcceptedDifference(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "path", F13CanonicalPathText(_path) },
                { "category", "sample_normal" },
                { "expected_canonical_tokens", StringArrayAsObjects(expectedTokens) },
                { "actual_canonical_tokens", StringArrayAsObjects(actualTokens) },
                { "expected_values", NumberArrayAsObjects(expectedValues) },
                { "actual_values", NumberArrayAsObjects(actualValues) },
                { "maximum_component_difference", maximumComponentDifference },
                { "maximum_allowed_component_difference", maximumComponentTolerance },
                { "expected_unit_norm", expectedLength },
                { "actual_unit_norm", actualLength },
                { "unit_norm_tolerance", normTolerance },
                { "angle_difference_rad", angle },
                { "maximum_allowed_angle_rad", F13CanonicalNormalMaxAngleRad }
            });
            return true;
        }

        private bool ReadFiniteVector(int length, out double[] expectedValues, out string[] expectedTokens,
            out double[] actualValues, out string[] actualTokens)
        {
            expectedValues = new double[length];
            actualValues = new double[length];
            expectedTokens = new string[length];
            actualTokens = new string[length];
            for (int i = 0; i < length; i++)
            {
                _path.Add(F13CanonicalPathSegment.ForIndex(i));
                if (!TryReadFiniteNumber(_expected, out expectedTokens[i], out expectedValues[i]) ||
                    !TryReadFiniteNumber(_actual, out actualTokens[i], out actualValues[i]))
                {
                    bool failed = Fail("allowlisted_vector_value_is_not_finite_numeric");
                    _path.RemoveAt(_path.Count - 1);
                    return failed;
                }
                _path.RemoveAt(_path.Count - 1);
            }
            return true;
        }

        private bool CompareScalarExact()
        {
            string expectedRaw, actualRaw;
            if (!_expected.TryReadScalarToken(out expectedRaw) || !_actual.TryReadScalarToken(out actualRaw))
                return Fail("invalid_scalar_token");
            bool expectedBoolean = expectedRaw == "True;" || expectedRaw == "False;";
            bool actualBoolean = actualRaw == "True;" || actualRaw == "False;";
            if (expectedBoolean || actualBoolean)
                return expectedBoolean && actualBoolean && String.Equals(expectedRaw, actualRaw, StringComparison.Ordinal) ||
                    Fail("canonical_boolean_mismatch");

            double expectedValue, actualValue;
            if (!TryParseFiniteNumberToken(expectedRaw, out expectedValue) ||
                !TryParseFiniteNumberToken(actualRaw, out actualValue))
                return Fail("nonfinite_or_invalid_canonical_number");
            return String.Equals(expectedRaw, actualRaw, StringComparison.Ordinal) ||
                Fail("non_allowlisted_numeric_token_mismatch");
        }

        private bool TryReadArrayHeader(F13CanonicalCursor cursor, out int[] lowerBounds,
            out int[] lengths, out int elementCount)
        {
            lowerBounds = null;
            lengths = null;
            elementCount = 0;
            if (!cursor.TryConsume("array(")) return false;
            int rank;
            if (!cursor.TryReadIntegerUntil(':', out rank) || rank < 1 || rank > 32) return false;
            lowerBounds = new int[rank];
            lengths = new int[rank];
            long total = 1;
            for (int i = 0; i < rank; i++)
            {
                if (!cursor.TryReadIntegerUntil(',', out lowerBounds[i]) ||
                    !cursor.TryReadIntegerUntil(';', out lengths[i]) || lengths[i] < 0) return false;
                total *= lengths[i];
                if (total > Int32.MaxValue) return false;
            }
            if (!cursor.TryConsume(")[")) return false;
            elementCount = (int)total;
            return true;
        }

        private bool TryReadFiniteNumber(F13CanonicalCursor cursor, out string raw, out double value)
        {
            raw = null;
            value = 0.0;
            if (!cursor.TryReadScalarToken(out raw) || !TryParseFiniteNumberToken(raw, out value))
                return Fail("nonfinite_or_invalid_allowlisted_number");
            return true;
        }

        private static bool TryParseFiniteNumberToken(string raw, out double value)
        {
            value = 0.0;
            if (String.IsNullOrEmpty(raw) || raw[raw.Length - 1] != ';') return false;
            string token = raw.Substring(0, raw.Length - 1);
            return Double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private bool Fail(string reason)
        {
            if (Failure == null)
                Failure = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "path", F13CanonicalPathText(_path) },
                    { "reason", reason },
                    { "expected_position", _expected.Position },
                    { "actual_position", _actual.Position }
                };
            return false;
        }

        private bool FailNumeric(string reason, string expectedRaw, string actualRaw,
            double expectedValue, double actualValue, double difference, double tolerance)
        {
            if (Failure == null)
                Failure = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "path", F13CanonicalPathText(_path) },
                    { "reason", reason },
                    { "expected_canonical_token", expectedRaw },
                    { "actual_canonical_token", actualRaw },
                    { "expected_value", expectedValue },
                    { "actual_value", actualValue },
                    { "difference", FiniteOrNull(difference) },
                    { "difference_is_finite", IsFinite(difference) },
                    { "maximum_allowed_difference", FiniteOrNull(tolerance) },
                    { "maximum_allowed_difference_is_finite", IsFinite(tolerance) }
                };
            return false;
        }

        private bool FailVector(string reason, string metric, string[] expectedTokens,
            string[] actualTokens, double[] expectedValues, double[] actualValues,
            double difference, double tolerance)
        {
            if (Failure == null)
                Failure = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "path", F13CanonicalPathText(_path) },
                    { "reason", reason },
                    { "metric", metric },
                    { "expected_canonical_tokens", StringArrayAsObjects(expectedTokens) },
                    { "actual_canonical_tokens", StringArrayAsObjects(actualTokens) },
                    { "expected_values", NumberArrayAsObjects(expectedValues) },
                    { "actual_values", NumberArrayAsObjects(actualValues) },
                    { "difference", FiniteOrNull(difference) },
                    { "difference_is_finite", IsFinite(difference) },
                    { "maximum_allowed_difference", FiniteOrNull(tolerance) },
                    { "maximum_allowed_difference_is_finite", IsFinite(tolerance) }
                };
            return false;
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private static object FiniteOrNull(double value)
        {
            return IsFinite(value) ? (object)value : null;
        }

        private void AddAcceptedDifference(Dictionary<string, object> difference)
        {
            AcceptedDifferenceCount++;
            if (AcceptedDifferences.Count < F13CanonicalMaxDetailedRoundoffDifferences)
                AcceptedDifferences.Add(difference);
            else OmittedAcceptedDifferenceCount++;
        }

        private static string F13CanonicalTargetArrayKind(List<F13CanonicalPathSegment> path)
        {
            if (path.Count == 3 && PathKey(path, 0, "faces") && PathIndex(path, 1) &&
                PathKey(path, 2, "uv_limits")) return "uv_limits";
            if (path.Count == 5 && PathKey(path, 0, "faces") && PathIndex(path, 1) &&
                PathKey(path, 2, "surface_samples") && PathIndex(path, 3))
            {
                if (PathKey(path, 4, "uv")) return "surface_uv";
                if (PathKey(path, 4, "point_mm")) return "point_mm";
                if (PathKey(path, 4, "normal")) return "normal";
            }
            return null;
        }

        private static bool PathKey(List<F13CanonicalPathSegment> path, int index, string key)
        {
            return index < path.Count && !path[index].IsIndex &&
                String.Equals(path[index].Key, key, StringComparison.Ordinal);
        }

        private static bool PathIndex(List<F13CanonicalPathSegment> path, int index)
        {
            return index < path.Count && path[index].IsIndex;
        }

        private static bool IsExpectedTargetArrayShape(int[] lowerBounds, int[] lengths,
            int elementCount, int expectedLength)
        {
            return lowerBounds != null && lengths != null && lowerBounds.Length == 1 && lengths.Length == 1 &&
                lowerBounds[0] == 0 && lengths[0] == expectedLength && elementCount == expectedLength;
        }

        private static bool IntArrayEqual(int[] left, int[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static bool VectorNumericValuesEqual(double[] left, double[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static double VectorLength(double[] vector)
        {
            double scale = 0.0;
            for (int i = 0; i < vector.Length; i++) scale = Math.Max(scale, Math.Abs(vector[i]));
            if (scale == 0.0) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < vector.Length; i++)
            {
                double component = vector[i] / scale;
                sum += component * component;
            }
            return scale * Math.Sqrt(sum);
        }

        private static double VectorAngle(double[] left, double[] right, double leftLength, double rightLength)
        {
            double ax = left[0] / leftLength, ay = left[1] / leftLength, az = left[2] / leftLength;
            double bx = right[0] / rightLength, by = right[1] / rightLength, bz = right[2] / rightLength;
            double cx = ay * bz - az * by;
            double cy = az * bx - ax * bz;
            double cz = ax * by - ay * bx;
            double crossLength = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            double dot = ax * bx + ay * by + az * bz;
            return Math.Atan2(crossLength, dot);
        }

        private static double EuclideanDifference(double[] left, double[] right)
        {
            double[] delta = new double[left.Length];
            for (int i = 0; i < left.Length; i++) delta[i] = left[i] - right[i];
            return VectorLength(delta);
        }

        private static double MaxAbs(double[] left, double[] right)
        {
            double result = 0.0;
            for (int i = 0; i < left.Length; i++) result = Math.Max(result, Math.Abs(left[i]));
            for (int i = 0; i < right.Length; i++) result = Math.Max(result, Math.Abs(right[i]));
            return result;
        }

        private static List<object> NumberArrayAsObjects(double[] values)
        {
            List<object> result = new List<object>();
            if (values != null) foreach (double value in values) result.Add(value);
            return result;
        }

        private static List<object> StringArrayAsObjects(string[] values)
        {
            List<object> result = new List<object>();
            if (values != null) foreach (string value in values) result.Add(value);
            return result;
        }

        private static string F13CanonicalPathText(List<F13CanonicalPathSegment> path)
        {
            StringBuilder result = new StringBuilder();
            foreach (F13CanonicalPathSegment segment in path)
            {
                if (segment.IsIndex) result.Append('[').Append(segment.Index).Append(']');
                else
                {
                    if (result.Length > 0) result.Append('.');
                    result.Append(segment.Key);
                }
            }
            return result.ToString();
        }
    }

    private static M3RevolveRequest NormalizeM3RevolveRequest(M3RevolveRequest request)
    {
        if (request == null) throw new M2ModelException("INVALID_REQUEST", "M3 Revolve request is required");
        if (String.IsNullOrWhiteSpace(request.SketchId))
            throw new M2ModelException("INVALID_REQUEST", "M3 Revolve requires sketch_id");
        if (request.Mode != "create" && request.Mode != "unite" && request.Mode != "subtract")
            throw new M2ModelException("INVALID_REQUEST", "mode must be create, unite, or subtract");
        if (request.ProfileOutput != "solid" && request.ProfileOutput != "sheet")
            throw new M2ModelException("INVALID_REQUEST", "profile_output must be solid or sheet");
        if (request.ProfileOutput == "sheet" && request.Mode != "create")
            throw new M2ModelException("INVALID_REQUEST", "sheet output is supported only for create Revolve operations");
        if (request.Mode == "create" && !String.IsNullOrEmpty(request.TargetBodyId))
            throw new M2ModelException("INVALID_REQUEST", "create Revolve does not accept target_body_id");
        if (request.Mode != "create" && String.IsNullOrWhiteSpace(request.TargetBodyId))
            throw new M2ModelException("INVALID_REQUEST", "unite and subtract Revolve require target_body_id");
        if (request.AxisPointMm == null || request.AxisPointMm.Length != 3 ||
            request.AxisDirection == null || request.AxisDirection.Length != 3)
            throw new M2ModelException("INVALID_REQUEST", "axis_point_mm and axis_direction must each contain three values");
        M3RevolveRequest normalized = CloneM3RevolveRequest(request);
        for (int i = 0; i < 3; i++)
        {
            EnsureFinite(normalized.AxisPointMm[i], "axis_point_mm");
            if (Math.Abs(normalized.AxisPointMm[i]) > 1000000.0)
                throw new M2ModelException("INVALID_REQUEST", "axis_point_mm coordinates must be within +/-1000000 mm");
            EnsureFinite(normalized.AxisDirection[i], "axis_direction");
        }
        double scale = Math.Max(Math.Abs(normalized.AxisDirection[0]),
            Math.Max(Math.Abs(normalized.AxisDirection[1]), Math.Abs(normalized.AxisDirection[2])));
        if (scale == 0.0) throw new M2ModelException("INVALID_REQUEST", "axis_direction must be nonzero");
        double x = normalized.AxisDirection[0] / scale;
        double y = normalized.AxisDirection[1] / scale;
        double z = normalized.AxisDirection[2] / scale;
        double length = Math.Sqrt(x * x + y * y + z * z);
        if (Double.IsNaN(length) || Double.IsInfinity(length) || length <= 0.0)
            throw new M2ModelException("INVALID_REQUEST", "axis_direction must be a finite nonzero vector");
        normalized.AxisDirection = new double[] { x / length, y / length, z / length };
        EnsureFinite(normalized.StartAngleDeg, "start_angle_deg");
        EnsureFinite(normalized.EndAngleDeg, "end_angle_deg");
        if (normalized.StartAngleDeg < -360.0 || normalized.StartAngleDeg > 360.0 ||
            normalized.EndAngleDeg < -360.0 || normalized.EndAngleDeg > 360.0)
            throw new M2ModelException("INVALID_REQUEST", "start_angle_deg and end_angle_deg must be in [-360, 360]");
        double span = normalized.EndAngleDeg - normalized.StartAngleDeg;
        if (Double.IsNaN(span) || Double.IsInfinity(span) || Math.Abs(span) <= F13RevolveAngleToleranceDeg || Math.Abs(span) > 360.0)
            throw new M2ModelException("INVALID_REQUEST", "signed angle span must be nonzero and have magnitude at most 360 degrees");
        return normalized;
    }

    private static M3RevolveRequest CloneM3RevolveRequest(M3RevolveRequest request)
    {
        return new M3RevolveRequest
        {
            RevisionId = request.RevisionId,
            SketchId = request.SketchId,
            Mode = request.Mode,
            ProfileOutput = request.ProfileOutput,
            AxisPointMm = request.AxisPointMm == null ? null : (double[])request.AxisPointMm.Clone(),
            AxisDirection = request.AxisDirection == null ? null : (double[])request.AxisDirection.Clone(),
            StartAngleDeg = request.StartAngleDeg,
            EndAngleDeg = request.EndAngleDeg,
            TargetBodyId = request.TargetBodyId
        };
    }

    private static bool M3RevolveAxisInSketchPlane(Sketch sketch, M3RevolveRequest request,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (sketch == null || request == null || request.AxisPointMm == null || request.AxisDirection == null)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "Sketch or world axis input is unavailable");
            return false;
        }
        Point3d origin = sketch.Origin;
        Matrix3x3 orientation = sketch.Orientation.Element;
        double[] normal = new double[] { orientation.Zx, orientation.Zy, orientation.Zz };
        Normalize3(normal);
        double[] point = request.AxisPointMm;
        double[] direction = request.AxisDirection;
        double residual = (point[0] - origin.X) * normal[0] +
            (point[1] - origin.Y) * normal[1] + (point[2] - origin.Z) * normal[2];
        double normalDot = direction[0] * normal[0] + direction[1] * normal[1] + direction[2] * normal[2];
        bool matches = !Double.IsNaN(residual) && !Double.IsInfinity(residual) &&
            Math.Abs(residual) <= F13RevolveAxisPlaneToleranceMm &&
            !Double.IsNaN(normalDot) && !Double.IsInfinity(normalDot) &&
            Math.Abs(normalDot) <= F13RevolveAxisDirectionTolerance;
        evidence.Add("status", matches ? "VERIFIED" : "MISMATCH");
        evidence.Add("sketch_origin_mm", Point(origin));
        evidence.Add("sketch_normal", NumberList(normal));
        evidence.Add("axis_point_mm", NumberList(point));
        evidence.Add("axis_direction", NumberList(direction));
        evidence.Add("axis_plane_residual_mm", residual);
        evidence.Add("axis_direction_normal_dot", normalDot);
        evidence.Add("point_tolerance_mm", F13RevolveAxisPlaneToleranceMm);
        evidence.Add("direction_tolerance", F13RevolveAxisDirectionTolerance);
        evidence.Add("geometric_rule", "The complete world axis line lies in the selected Sketch plane.");
        return matches;
    }

    private static void M3RevolveNativeAngles(M3RevolveRequest request, out double start, out double end)
    {
        if (request.EndAngleDeg > request.StartAngleDeg)
        {
            start = request.StartAngleDeg;
            end = request.EndAngleDeg;
        }
        else
        {
            start = -request.StartAngleDeg;
            end = -request.EndAngleDeg;
        }
    }

    private static double[] M3RevolveNativeDirection(M3RevolveRequest request)
    {
        double sign = request.EndAngleDeg > request.StartAngleDeg ? 1.0 : -1.0;
        return new double[] {
            request.AxisDirection[0] * sign,
            request.AxisDirection[1] * sign,
            request.AxisDirection[2] * sign
        };
    }

    private static bool M3RevolveDefinitionMatches(M3PlanState plan, Feature feature,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (plan == null || plan.RevolveRequest == null || feature == null)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "retained F13 request or committed Revolve feature is unavailable");
            return false;
        }
        Part work = feature.OwningPart as Part;
        RevolveBuilder builder = work == null ? null : work.Features.CreateRevolveBuilder(feature);
        if (builder == null) throw new M2ModelException("F13_REVOLVE_READBACK_FAILED", "NX could not rebuild the committed Revolve feature");
        try
        {
            M3RevolveRequest request = plan.RevolveRequest;
            Dictionary<string, object> sectionEvidence = V2SketchProfileAnalyzer.AnalyzeF12SectionDomain(
                work, plan.Sketch, builder.Section);
            Dictionary<string, object> sectionComparisonEvidence;
            bool sectionMatches = F13PreparedProfileSectionMatches(plan.ProfileAnalysis,
                sectionEvidence, out sectionComparisonEvidence);

            Axis axis = builder.Axis;
            if (axis == null || axis.Point == null || axis.Direction == null)
                throw new M2ModelException("F13_REVOLVE_READBACK_FAILED", "NX Revolve returned no point-direction Axis");
            Point3d actualPoint = axis.Point.Coordinates;
            Vector3d actualVector = axis.Direction.Vector;
            double[] actualDirection = new double[] { actualVector.X, actualVector.Y, actualVector.Z };
            Normalize3(actualDirection);
            double[] expectedDirection = M3RevolveNativeDirection(request);
            bool axisPointMatches = PointNearArray(actualPoint, request.AxisPointMm, M3FrameToleranceMm);
            bool axisDirectionMatches = VectorNearArray(actualDirection, expectedDirection, F13RevolveAxisDirectionTolerance);
            string axisTag = axis.Tag.ToString();
            string pointTag = axis.Point.Tag.ToString();
            string directionTag = axis.Direction.Tag.ToString();
            bool helperIdentityMatches = !String.IsNullOrEmpty(plan.RevolveAxisTag) &&
                !String.IsNullOrEmpty(plan.RevolveAxisPointTag) && !String.IsNullOrEmpty(plan.RevolveAxisDirectionTag) &&
                String.Equals(axisTag, plan.RevolveAxisTag, StringComparison.Ordinal) &&
                String.Equals(pointTag, plan.RevolveAxisPointTag, StringComparison.Ordinal) &&
                String.Equals(directionTag, plan.RevolveAxisDirectionTag, StringComparison.Ordinal);

            double expectedNativeStart, expectedNativeEnd;
            M3RevolveNativeAngles(request, out expectedNativeStart, out expectedNativeEnd);
            NXOpen.GeometricUtilities.Limits limits = builder.Limits;
            NXOpen.GeometricUtilities.Extend start = limits == null ? null : limits.StartExtend;
            NXOpen.GeometricUtilities.Extend end = limits == null ? null : limits.EndExtend;
            Expression startExpression = start == null ? null : start.Value;
            Expression endExpression = end == null ? null : end.Value;
            bool startMatches = start != null && start.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.Value &&
                F12ParameterExpressionMatches(startExpression, expectedNativeStart, "degrees");
            bool endMatches = end != null && end.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.Value &&
                F12ParameterExpressionMatches(endExpression, expectedNativeEnd, "degrees");
            bool symmetricMatches = limits != null && !limits.SymmetricOption;
            bool toleranceMatches = builder.Tolerance == 0.001;
            NXOpen.GeometricUtilities.FeatureOffset offset = builder.Offset;
            bool offsetMatches = offset != null && String.Equals(offset.Option.ToString(), "NoOffset", StringComparison.Ordinal);
            string expectedBodyType = request.ProfileOutput == "sheet" ? "Sheet" : "Solid";
            string actualBodyType = builder.FeatureOptions == null ? null : builder.FeatureOptions.BodyType.ToString();
            bool bodyTypeMatches = String.Equals(expectedBodyType, actualBodyType, StringComparison.Ordinal);
            string expectedBoolean = request.Mode == "create" ? "Create" : request.Mode == "unite" ? "Unite" : "Subtract";
            string actualBoolean = builder.BooleanOperation == null ? null : builder.BooleanOperation.Type.ToString();
            bool booleanMatches = String.Equals(expectedBoolean, actualBoolean, StringComparison.Ordinal);
            Body[] targetBodies = builder.BooleanOperation == null ? null : builder.BooleanOperation.GetTargetBodies();
            List<string> targetIds = new List<string>();
            if (targetBodies != null)
                foreach (Body target in targetBodies)
                {
                    if (target == null) throw new M2ModelException("F13_REVOLVE_READBACK_FAILED", "NX Revolve returned a null Boolean target");
                    targetIds.Add(RequiredJournalId(target, "Revolve Boolean target"));
                }
            bool targetsMatch = request.Mode == "create" ? targetIds.Count == 0 :
                targetBodies != null && targetBodies.Length == 1 && targetBodies[0].Tag.Equals(plan.TargetBody.Tag) &&
                String.Equals(targetIds[0], plan.TargetBodyId, StringComparison.Ordinal);

            Feature[] parents = feature.GetParents();
            if (parents == null) throw new M2ModelException("MODEL_READ_FAILED", "Revolve parent feature list returned null");
            List<object> parentIds = new List<object>();
            bool sketchParent = false;
            foreach (Feature parent in parents)
            {
                if (parent == null) throw new M2ModelException("MODEL_READ_FAILED", "Revolve parent feature list contains null");
                string parentId = RequiredJournalId(parent, "Revolve parent feature");
                parentIds.Add(parentId);
                if (plan.Sketch != null && plan.Sketch.Feature != null && parent.Tag.Equals(plan.Sketch.Feature.Tag)) sketchParent = true;
            }
            Dictionary<string, object> axisPlane;
            bool axisInPlane = M3RevolveAxisInSketchPlane(plan.Sketch, request, out axisPlane);
            bool matched = sectionMatches && axisPointMatches && axisDirectionMatches && helperIdentityMatches &&
                startMatches && endMatches && symmetricMatches && toleranceMatches && offsetMatches &&
                bodyTypeMatches && booleanMatches && targetsMatch && sketchParent && axisInPlane;
            evidence.Add("status", matched ? "VERIFIED" : "MISMATCH");
            evidence.Add("feature_id", RequiredJournalId(feature, "Revolve feature"));
            evidence.Add("native_feature_type", feature.FeatureType);
            evidence.Add("section_domain", sectionEvidence);
            evidence.Add("section_domain_comparison", sectionComparisonEvidence);
            evidence.Add("section_domain_matches_prepare", sectionMatches);
            evidence.Add("expected_input_domain_fingerprint", sectionComparisonEvidence["expected_input_domain_fingerprint"]);
            evidence.Add("actual_input_domain_fingerprint", sectionComparisonEvidence["actual_input_domain_fingerprint"]);
            evidence.Add("expected_section_output_domain_fingerprint", sectionComparisonEvidence["expected_section_output_domain_fingerprint"]);
            evidence.Add("actual_section_output_domain_fingerprint", sectionComparisonEvidence["actual_section_output_domain_fingerprint"]);
            evidence.Add("section_output_domain_fingerprint_matches_prepare_diagnostic_only",
                sectionComparisonEvidence["output_domain_fingerprint_matches_prepare"]);
            evidence.Add("axis_point_mm", Point(actualPoint));
            evidence.Add("expected_axis_point_mm", NumberList(request.AxisPointMm));
            evidence.Add("axis_direction", NumberList(actualDirection));
            evidence.Add("expected_native_axis_direction", NumberList(expectedDirection));
            evidence.Add("axis_point_matches", axisPointMatches);
            evidence.Add("axis_direction_matches", axisDirectionMatches);
            evidence.Add("axis_tag", axisTag);
            evidence.Add("axis_point_tag", pointTag);
            evidence.Add("axis_direction_tag", directionTag);
            evidence.Add("axis_helpers_match_committed_feature", helperIdentityMatches);
            evidence.Add("requested_start_angle_deg", request.StartAngleDeg);
            evidence.Add("requested_end_angle_deg", request.EndAngleDeg);
            evidence.Add("requested_signed_span_deg", request.EndAngleDeg - request.StartAngleDeg);
            evidence.Add("expected_native_start_angle_deg", expectedNativeStart);
            evidence.Add("expected_native_end_angle_deg", expectedNativeEnd);
            evidence.Add("actual_native_start_angle_deg", startExpression == null ? (object)null : startExpression.Value);
            evidence.Add("actual_native_end_angle_deg", endExpression == null ? (object)null : endExpression.Value);
            evidence.Add("start_angle_expression_unit", startExpression == null || startExpression.Units == null ? null : startExpression.Units.Symbol);
            evidence.Add("end_angle_expression_unit", endExpression == null || endExpression.Units == null ? null : endExpression.Units.Symbol);
            evidence.Add("start_limit_matches_request", startMatches);
            evidence.Add("end_limit_matches_request", endMatches);
            evidence.Add("symmetric_option_is_disabled", symmetricMatches);
            evidence.Add("revolve_tolerance_matches", toleranceMatches);
            evidence.Add("offset_option_is_disabled", offsetMatches);
            evidence["inactive_offset_start_expression_diagnostic"] = F13InactiveExpressionDiagnostic(offset == null ? null : offset.StartOffset);
            evidence["inactive_offset_end_expression_diagnostic"] = F13InactiveExpressionDiagnostic(offset == null ? null : offset.EndOffset);
            evidence.Add("requested_profile_output", request.ProfileOutput);
            evidence.Add("actual_body_type", actualBodyType);
            evidence.Add("body_type_matches", bodyTypeMatches);
            evidence.Add("requested_boolean_mode", request.Mode);
            evidence.Add("actual_boolean_mode", actualBoolean);
            evidence.Add("boolean_mode_matches", booleanMatches);
            evidence.Add("target_body_ids", StringListAsObjects(targetIds));
            evidence.Add("target_body_matches", targetsMatch);
            evidence.Add("parent_feature_ids", parentIds);
            evidence.Add("source_sketch_is_feature_parent", sketchParent);
            evidence.Add("axis_in_sketch_plane", axisInPlane);
            evidence.Add("axis_plane_evidence", axisPlane);
            return matched;
        }
        finally
        {
            try { builder.Destroy(); }
            catch (Exception ex)
            {
                throw M3AnalysisStateUnknown("NX Revolve readback builder cleanup could not be verified", ex);
            }
        }
    }

    private static bool F13PreparedProfileSectionMatches(Dictionary<string, object> expected,
        Dictionary<string, object> actual, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        string expectedInput, expectedOutput, actualInput, actualOutput;
        bool expectedInputPresent = F13TryProfileFingerprint(expected, "input_geometry_domain_fingerprint", out expectedInput);
        bool expectedOutputPresent = F13TryProfileFingerprint(expected, "section_output_domain_fingerprint", out expectedOutput);
        bool actualInputPresent = F13TryProfileFingerprint(actual, "input_geometry_domain_fingerprint", out actualInput);
        bool actualOutputPresent = F13TryProfileFingerprint(actual, "section_output_domain_fingerprint", out actualOutput);

        Dictionary<string, object> expectedInternalEvidence;
        Dictionary<string, object> actualInternalEvidence;
        bool expectedSectionVerified = F13SectionAnalysisInternallyVerified(expected, expectedInput,
            out expectedInternalEvidence);
        bool actualSectionVerified = F13SectionAnalysisInternallyVerified(actual, expectedInput,
            out actualInternalEvidence);
        bool inputFingerprintMatches = expectedInputPresent && actualInputPresent &&
            String.Equals(expectedInput, actualInput, StringComparison.Ordinal);

        string[] topologyFields = new string[] {
            "open_component_count", "valid_closed_component_count", "section_loop_count"
        };
        Dictionary<string, object> topologyEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bool topologyMatches = expected != null && actual != null;
        foreach (string field in topologyFields)
        {
            object expectedValue = null, actualValue = null;
            bool hasExpected = expected != null && expected.TryGetValue(field, out expectedValue);
            bool hasActual = actual != null && actual.TryGetValue(field, out actualValue);
            bool present = hasExpected && hasActual && expectedValue != null && actualValue != null;
            bool matches = present && String.Equals(M2Json.CanonicalText(expectedValue),
                M2Json.CanonicalText(actualValue), StringComparison.Ordinal);
            topologyEvidence.Add(field, new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "expected", hasExpected ? expectedValue : null },
                { "actual", hasActual ? actualValue : null },
                { "matches", matches }
            });
            topologyMatches = topologyMatches && matches;
        }

        bool outputFingerprintMatches = expectedOutputPresent && actualOutputPresent &&
            String.Equals(expectedOutput, actualOutput, StringComparison.Ordinal);
        bool matchesPreparedSource = expectedSectionVerified && actualSectionVerified &&
            inputFingerprintMatches && topologyMatches;
        evidence.Add("status", matchesPreparedSource ? "VERIFIED" : "MISMATCH");
        evidence.Add("expected_profile_f12_section_verified", expectedSectionVerified);
        evidence.Add("actual_profile_f12_section_verified", actualSectionVerified);
        evidence.Add("expected_input_domain_fingerprint", expectedInput);
        evidence.Add("actual_input_domain_fingerprint", actualInput);
        evidence.Add("input_domain_fingerprint_matches_prepare", inputFingerprintMatches);
        evidence.Add("expected_section_output_domain_fingerprint", expectedOutput);
        evidence.Add("actual_section_output_domain_fingerprint", actualOutput);
        evidence.Add("output_domain_fingerprint_matches_prepare", outputFingerprintMatches);
        evidence.Add("output_fingerprint_equality_is_diagnostic_only", true);
        evidence.Add("topology_fields", topologyEvidence);
        evidence.Add("expected_section_internal_evidence", expectedInternalEvidence);
        evidence.Add("actual_section_internal_evidence", actualInternalEvidence);
        return matchesPreparedSource;
    }

    private static bool F13SectionAnalysisInternallyVerified(Dictionary<string, object> analysis,
        string expectedInputFingerprint, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        string inputFingerprint, outputFingerprint;
        bool inputPresent = F13TryProfileFingerprint(analysis, "input_geometry_domain_fingerprint", out inputFingerprint);
        bool outputPresent = F13TryProfileFingerprint(analysis, "section_output_domain_fingerprint", out outputFingerprint);
        object matchValue = null;
        Dictionary<string, object> domainMatch = analysis != null &&
            analysis.TryGetValue("section_domain_match", out matchValue)
            ? matchValue as Dictionary<string, object> : null;
        object matchStatusValue = null, matchInputValue = null, matchOutputValue = null;
        string matchInput = domainMatch != null && domainMatch.TryGetValue("input_domain_fingerprint", out matchInputValue)
            ? Convert.ToString(matchInputValue, CultureInfo.InvariantCulture) : null;
        string matchOutput = domainMatch != null && domainMatch.TryGetValue("output_domain_fingerprint", out matchOutputValue)
            ? Convert.ToString(matchOutputValue, CultureInfo.InvariantCulture) : null;
        bool matchStatusVerified = domainMatch != null && domainMatch.TryGetValue("status", out matchStatusValue) &&
            String.Equals(Convert.ToString(matchStatusValue, CultureInfo.InvariantCulture), "MATCHED", StringComparison.Ordinal);
        bool f12StatusVerified = F12SectionAnalysisVerified(analysis);
        bool matchInputIsTopLevel = inputPresent && !String.IsNullOrEmpty(matchInput) &&
            String.Equals(inputFingerprint, matchInput, StringComparison.Ordinal);
        bool matchOutputIsTopLevel = outputPresent && !String.IsNullOrEmpty(matchOutput) &&
            String.Equals(outputFingerprint, matchOutput, StringComparison.Ordinal);
        bool inputIsExpected = inputPresent && !String.IsNullOrEmpty(expectedInputFingerprint) &&
            String.Equals(inputFingerprint, expectedInputFingerprint, StringComparison.Ordinal);
        bool verified = f12StatusVerified && matchStatusVerified && matchInputIsTopLevel &&
            matchOutputIsTopLevel && inputIsExpected;
        evidence.Add("status", verified ? "VERIFIED" : "MISMATCH");
        evidence.Add("f12_section_analysis_verified", f12StatusVerified);
        evidence.Add("section_domain_match_status_verified", matchStatusVerified);
        evidence.Add("input_domain_fingerprint", inputFingerprint);
        evidence.Add("match_input_domain_fingerprint", matchInput);
        evidence.Add("match_input_fingerprint_matches_top_level", matchInputIsTopLevel);
        evidence.Add("expected_input_domain_fingerprint", expectedInputFingerprint);
        evidence.Add("input_fingerprint_matches_prepared_source", inputIsExpected);
        evidence.Add("section_output_domain_fingerprint", outputFingerprint);
        evidence.Add("match_output_domain_fingerprint", matchOutput);
        evidence.Add("match_output_fingerprint_matches_top_level", matchOutputIsTopLevel);
        return verified;
    }

    private static bool F13TryProfileFingerprint(Dictionary<string, object> analysis,
        string key, out string fingerprint)
    {
        object value = null;
        fingerprint = analysis != null && analysis.TryGetValue(key, out value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        return !String.IsNullOrWhiteSpace(fingerprint);
    }

    private bool VerifyF13ProfileAnalysis(Part work, Sketch sketch,
        Dictionary<string, object> expected, out Dictionary<string, object> actual,
        out Dictionary<string, object> cleanupEvidence)
    {
        actual = null;
        cleanupEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (work == null || sketch == null || expected == null)
        {
            cleanupEvidence.Add("status", "MISMATCH");
            cleanupEvidence.Add("reason", "F13 profile verification input is unavailable");
            return false;
        }
        bool modifiedBefore = work.IsModified;
        string geometryBefore = CaptureFullGeometry(work);
        try { actual = V2SketchProfileAnalyzer.AnalyzeF12WholeSketchSection(work, sketch); }
        catch (V2SketchProfileAnalyzer.SectionCleanupException ex)
        {
            cleanupEvidence.Add("status", "UNKNOWN");
            cleanupEvidence.Add("exception", ex.GetType().Name);
            throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN",
                "F13 temporary Section cleanup could not prove the source Work Part was restored");
        }
        catch (Exception ex) { cleanupEvidence.Add("analysis_exception", ex.GetType().Name); }
        bool modifiedAfter;
        string geometryAfter;
        try
        {
            modifiedAfter = work.IsModified;
            geometryAfter = CaptureFullGeometry(work);
        }
        catch (Exception ex)
        {
            cleanupEvidence.Add("status", "UNKNOWN");
            cleanupEvidence.Add("post_analysis_read_exception", ex.GetType().Name);
            throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN",
                "F13 could not verify model state after temporary Section analysis");
        }
        bool stateRestored = modifiedBefore == modifiedAfter &&
            String.Equals(geometryBefore, geometryAfter, StringComparison.Ordinal);
        cleanupEvidence.Add("part_modified_before", modifiedBefore);
        cleanupEvidence.Add("part_modified_after", modifiedAfter);
        cleanupEvidence.Add("full_sketch_geometry_restored", String.Equals(geometryBefore, geometryAfter, StringComparison.Ordinal));
        cleanupEvidence.Add("temporary_section_state_restored", stateRestored);
        Dictionary<string, object> domainEvidence;
        bool domainMatches = F13PreparedProfileSectionMatches(expected, actual, out domainEvidence);
        bool verified = domainMatches && stateRestored;
        cleanupEvidence.Add("section_domain_comparison", domainEvidence);
        cleanupEvidence.Add("status", verified ? "VERIFIED" : "MISMATCH");
        cleanupEvidence.Add("analysis", actual);
        return verified;
    }

    private static Dictionary<string, object> F13ReadExpectedRevolveExpressions(
        Dictionary<string, object> after, M3PlanState plan, out List<object> expectedRows,
        out HashSet<string> expectedNames)
    {
        expectedRows = new List<object>();
        expectedNames = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (after == null || plan == null || plan.RevolveRequest == null || plan.Feature == null)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "request, committed Revolve feature, or after snapshot is unavailable");
            return evidence;
        }
        Part work = plan.Feature.OwningPart as Part;
        RevolveBuilder builder = work == null ? null : work.Features.CreateRevolveBuilder(plan.Feature);
        if (builder == null) throw new M2ModelException("F13_REVOLVE_READBACK_FAILED", "NX could not rebuild the committed Revolve feature to read expressions");
        try
        {
            NXOpen.GeometricUtilities.Limits limits = builder.Limits;
            NXOpen.GeometricUtilities.Extend start = limits == null ? null : limits.StartExtend;
            NXOpen.GeometricUtilities.Extend end = limits == null ? null : limits.EndExtend;
            Expression startExpression = start == null ? null : start.Value;
            Expression endExpression = end == null ? null : end.Value;
            double expectedStart, expectedEnd;
            M3RevolveNativeAngles(plan.RevolveRequest, out expectedStart, out expectedEnd);
            bool startMatches = start != null && start.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.Value &&
                F12ParameterExpressionMatches(startExpression, expectedStart, "degrees");
            bool endMatches = end != null && end.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.Value &&
                F12ParameterExpressionMatches(endExpression, expectedEnd, "degrees");
            if (startExpression != null && !String.IsNullOrEmpty(startExpression.Name))
                expectedNames.Add(startExpression.Name.Normalize(System.Text.NormalizationForm.FormC));
            if (endExpression != null && !String.IsNullOrEmpty(endExpression.Name))
                expectedNames.Add(endExpression.Name.Normalize(System.Text.NormalizationForm.FormC));

            Expression[] featureExpressions = plan.Feature.GetExpressions();
            if (featureExpressions == null) throw new InvalidOperationException("Revolve.GetExpressions returned null");
            Dictionary<string, Expression> nativeByName = new Dictionary<string, Expression>(StringComparer.Ordinal);
            bool featureMembershipMatches = true;
            foreach (Expression expression in featureExpressions)
            {
                if (expression == null || String.IsNullOrEmpty(expression.Name)) { featureMembershipMatches = false; continue; }
                string name = expression.Name.Normalize(System.Text.NormalizationForm.FormC);
                if (nativeByName.ContainsKey(name)) featureMembershipMatches = false;
                else nativeByName.Add(name, expression);
            }
            featureMembershipMatches = featureMembershipMatches && expectedNames.Count == 2 &&
                nativeByName.Count == 2 && SetEquals(new HashSet<string>(nativeByName.Keys, StringComparer.Ordinal), expectedNames);

            Dictionary<string, Dictionary<string, object>> snapshotByName = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            bool snapshotMembershipMatches = true;
            foreach (object value in SnapshotList(after, "expressions"))
            {
                Dictionary<string, object> record = value as Dictionary<string, object>;
                object rawName;
                if (record == null || !record.TryGetValue("name", out rawName) || rawName == null)
                { snapshotMembershipMatches = false; continue; }
                string name = Convert.ToString(rawName, CultureInfo.InvariantCulture).Normalize(System.Text.NormalizationForm.FormC);
                if (snapshotByName.ContainsKey(name)) snapshotMembershipMatches = false;
                else snapshotByName.Add(name, record);
            }
            bool snapshotRowsMatch = true;
            foreach (string name in expectedNames)
            {
                Expression native;
                Dictionary<string, object> snapshot;
                if (!nativeByName.TryGetValue(name, out native) || !snapshotByName.TryGetValue(name, out snapshot))
                { snapshotRowsMatch = false; continue; }
                Dictionary<string, object> record = F12ExpressionSnapshot(native);
                if (!String.Equals(M2Json.CanonicalText(record), M2Json.CanonicalText(snapshot), StringComparison.Ordinal))
                    snapshotRowsMatch = false;
                expectedRows.Add(record);
            }
            expectedRows.Sort(delegate(object left, object right)
            {
                Dictionary<string, object> a = left as Dictionary<string, object>;
                Dictionary<string, object> b = right as Dictionary<string, object>;
                return StringComparer.Ordinal.Compare(Convert.ToString(a["name"], CultureInfo.InvariantCulture),
                    Convert.ToString(b["name"], CultureInfo.InvariantCulture));
            });

            bool symmetricMatches = limits != null && !limits.SymmetricOption;
            NXOpen.GeometricUtilities.FeatureOffset offset = builder.Offset;
            bool offsetMatches = offset != null && String.Equals(offset.Option.ToString(), "NoOffset", StringComparison.Ordinal);
            bool verified = startMatches && endMatches && symmetricMatches && offsetMatches &&
                featureMembershipMatches && snapshotMembershipMatches && snapshotRowsMatch && expectedRows.Count == 2;
            evidence.Add("status", verified ? "VERIFIED" : "MISMATCH");
            evidence.Add("expected_expression_names", StringListAsObjects(SortedStrings(expectedNames)));
            evidence.Add("actual_feature_expression_names", StringListAsObjects(SortedStrings(nativeByName.Keys)));
            evidence.Add("exactly_two_angle_expressions_owned_by_revolve", featureMembershipMatches);
            evidence.Add("snapshot_expression_rows_match_native", snapshotRowsMatch && snapshotMembershipMatches);
            evidence.Add("native_start_angle_matches_request", startMatches);
            evidence.Add("native_end_angle_matches_request", endMatches);
            evidence.Add("symmetric_option_disabled", symmetricMatches);
            evidence.Add("offset_option_disabled", offsetMatches);
            evidence.Add("inactive_offset_start_expression_diagnostic", F13InactiveExpressionDiagnostic(offset == null ? null : offset.StartOffset));
            evidence.Add("inactive_offset_end_expression_diagnostic", F13InactiveExpressionDiagnostic(offset == null ? null : offset.EndOffset));
            evidence.Add("expected_expression_records", expectedRows);
            return evidence;
        }
        finally
        {
            try { builder.Destroy(); }
            catch (Exception ex) { throw M3AnalysisStateUnknown("NX Revolve expression readback builder cleanup could not be verified", ex); }
        }
    }

    private bool M3RevolveBodyEffectMatches(M3PlanState plan, List<M3BodyMeasure> affected,
        List<M3BodyMeasure> after, Dictionary<string, object> actualGeometry,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        M3RevolveRequest request = plan == null ? null : plan.RevolveRequest;
        bool matches = request != null && plan.BodiesBefore != null && affected != null && affected.Count > 0 &&
            after != null && actualGeometry != null;
        M3BodyMeasure beforeTarget = matches && request.Mode != "create"
            ? FindBodyMeasure(plan.BodiesBefore, plan.TargetBodyId) : null;
        HashSet<string> beforeTags = new HashSet<string>(StringComparer.Ordinal);
        if (plan != null && plan.BodiesBefore != null)
            foreach (M3BodyMeasure body in plan.BodiesBefore) beforeTags.Add(body.Body.Tag.ToString());
        bool outputIdentityMatches = matches;
        foreach (M3BodyMeasure body in affected ?? new List<M3BodyMeasure>())
        {
            bool existedBefore = beforeTags.Contains(body.Body.Tag.ToString());
            if (request != null && request.Mode == "create" && existedBefore) outputIdentityMatches = false;
            if (request != null && request.ProfileOutput == "solid" &&
                (!body.IsSolid || !body.VolumeMm3.HasValue || body.VolumeMm3.Value <= 0.0)) outputIdentityMatches = false;
            if (request != null && request.ProfileOutput == "sheet" && body.IsSolid) outputIdentityMatches = false;
            if (request != null && request.Mode != "create" && !body.IsSolid) outputIdentityMatches = false;
            if (body.BboxMm == null || body.BboxMm.Length != 6) outputIdentityMatches = false;
        }
        int expectedBodyCount = request == null ? -1 : request.Mode == "create"
            ? plan.BodiesBefore.Count + affected.Count : plan.BodiesBefore.Count - 1 + affected.Count;
        bool bodyCountMatches = after != null && after.Count == expectedBodyCount;
        double? beforeVolume = request == null || request.Mode == "create" ? (double?)0.0 :
            beforeTarget == null ? null : beforeTarget.VolumeMm3;
        double? afterVolume = AggregateBodyVolume(affected);
        bool volumeEffectMatches;
        if (request == null) volumeEffectMatches = false;
        else if (request.Mode == "create")
            volumeEffectMatches = request.ProfileOutput == "sheet"
                ? affected != null && affected.Count > 0 && affected.TrueForAll(delegate(M3BodyMeasure item) { return !item.IsSolid; })
                : afterVolume.HasValue && afterVolume.Value > 0.0;
        else if (!beforeVolume.HasValue || !afterVolume.HasValue) volumeEffectMatches = false;
        else if (request.Mode == "unite")
            volumeEffectMatches = afterVolume.Value > beforeVolume.Value + ValueTolerance(afterVolume.Value, beforeVolume.Value);
        else
            volumeEffectMatches = afterVolume.Value > 0.0 && afterVolume.Value < beforeVolume.Value - ValueTolerance(afterVolume.Value, beforeVolume.Value);

        Dictionary<string, object> nonTargetEvidence;
        bool nonTargetsMatch = F12NonTargetBodiesMatch(plan, after, plan.BodyFingerprintsAfter, out nonTargetEvidence);
        Dictionary<string, object> geometryEvidence;
        bool geometryUsable = F13RevolveOutputGeometryUsable(actualGeometry, out geometryEvidence);
        matches = matches && outputIdentityMatches && bodyCountMatches && volumeEffectMatches && nonTargetsMatch && geometryUsable;
        evidence.Add("status", matches ? "VERIFIED" : "MISMATCH");
        evidence.Add("mode", request == null ? null : request.Mode);
        evidence.Add("profile_output", request == null ? null : request.ProfileOutput);
        evidence.Add("target_body_id_before", plan == null ? null : plan.TargetBodyId);
        evidence.Add("affected_body_ids_after", StringListAsObjects(plan == null || plan.AffectedBodyIds == null ? new List<string>() : plan.AffectedBodyIds));
        evidence.Add("output_body_identity_matches_feature", outputIdentityMatches);
        evidence.Add("body_count_before", plan == null || plan.BodiesBefore == null ? 0 : plan.BodiesBefore.Count);
        evidence.Add("body_count_after", after == null ? 0 : after.Count);
        evidence.Add("body_count_expected_after", expectedBodyCount);
        evidence.Add("body_count_matches_mode", bodyCountMatches);
        evidence.Add("target_volume_before_mm3", beforeVolume);
        evidence.Add("revolve_output_volume_mm3", afterVolume);
        evidence.Add("volume_effect_matches_mode", volumeEffectMatches);
        evidence.Add("non_target_body_geometry", nonTargetEvidence);
        evidence.Add("result_geometry_usable", geometryEvidence);
        evidence.Add("actual_result_geometry", actualGeometry);
        return matches;
    }

    private static Dictionary<string, object> CaptureF13RevolveOutputGeometry(Part work,
        List<M3BodyMeasure> bodies)
    {
        if (work == null || bodies == null || bodies.Count == 0)
            throw new M2ModelException("M3_BODY_READ_FAILED", "F13 Revolve output geometry input is unavailable");
        List<object> resultBodies = new List<object>();
        foreach (M3BodyMeasure measure in bodies)
        {
            if (measure == null || measure.Body == null)
                throw new M2ModelException("M3_BODY_READ_FAILED", "F13 Revolve output body is missing");
            Dictionary<string, object> nativeGeometry = CaptureAuditedF12BodyGeometry(work, measure.Body);
            object faultCountValue;
            if (!nativeGeometry.TryGetValue("consistency_fault_count", out faultCountValue) ||
                !(faultCountValue is int) || (int)faultCountValue != 0)
                throw new M2ModelException("M3_BODY_READ_FAILED",
                    "NX UF reported a body-consistency fault for a Revolve result body");
            int faultCount = (int)faultCountValue;
            List<object> faces = nativeGeometry["faces"] as List<object>;
            List<object> edges = nativeGeometry["edges"] as List<object>;
            if (faces == null || faces.Count == 0 || edges == null)
                throw new M2ModelException("M3_BODY_READ_FAILED", "F13 Revolve output lacks complete face/edge geometry");
            List<object> faceRecords = new List<object>();
            double area = 0.0;
            foreach (object value in faces)
            {
                Dictionary<string, object> face = value as Dictionary<string, object>;
                if (face == null) throw new M2ModelException("M3_BODY_READ_FAILED", "F12 face record is unavailable");
                object areaValue;
                double faceArea;
                if (!face.TryGetValue("area_mm2", out areaValue) || !TryFiniteNumber(areaValue, out faceArea) || faceArea <= 0.0)
                    throw new M2ModelException("M3_BODY_READ_FAILED", "F13 Revolve result face has no positive native area");
                area += faceArea;
                faceRecords.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", face.ContainsKey("tag") ? face["tag"] : null },
                    { "face_type", face.ContainsKey("face_type") ? face["face_type"] : null },
                    { "native_type", face.ContainsKey("native_type") ? face["native_type"] : null },
                    { "edge_tags", face.ContainsKey("edge_tags") ? face["edge_tags"] : null },
                    { "box_mm", face.ContainsKey("box_mm") ? face["box_mm"] : null },
                    { "direction", face.ContainsKey("direction") ? face["direction"] : null },
                    { "radius_mm", face.ContainsKey("radius_mm") ? face["radius_mm"] : null },
                    { "normal_sense", face.ContainsKey("normal_sense") ? face["normal_sense"] : null },
                    { "uv_limits", face.ContainsKey("uv_limits") ? face["uv_limits"] : null },
                    { "area_mm2", faceArea }
                });
            }
            List<object> edgeRecords = new List<object>();
            foreach (object value in edges)
            {
                Dictionary<string, object> edge = value as Dictionary<string, object>;
                if (edge == null) throw new M2ModelException("M3_BODY_READ_FAILED", "F12 edge record is unavailable");
                edgeRecords.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", edge.ContainsKey("tag") ? edge["tag"] : null },
                    { "edge_type", edge.ContainsKey("edge_type") ? edge["edge_type"] : null },
                    { "face_tags", edge.ContainsKey("face_tags") ? edge["face_tags"] : null },
                    { "parameter_limits", edge.ContainsKey("parameter_limits") ? edge["parameter_limits"] : null },
                    { "native_definition", edge.ContainsKey("native_definition") ? edge["native_definition"] : null }
                });
            }
            resultBodies.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "body_id", measure.Id },
                { "body_tag", measure.Body.Tag.ToString() },
                { "solid", measure.IsSolid },
                { "volume_mm3", measure.VolumeMm3 },
                { "bbox_mm", measure.BboxMm == null ? null : NumberList(measure.BboxMm) },
                { "consistency_fault_count", faultCount },
                { "face_count", faceRecords.Count },
                { "edge_count", edgeRecords.Count },
                { "surface_area_mm2", area },
                { "faces", faceRecords },
                { "edges", edgeRecords }
            });
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", "VERIFIED" },
            { "geometry_source", "NXOpen/UF direct face, edge, topology, analytic parameters, areas, exact boxes, and UV signatures" },
            { "bodies", resultBodies }
        };
    }

    private static bool F13RevolveOutputGeometryUsable(Dictionary<string, object> geometry,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        object statusValue, bodiesValue;
        List<object> bodies = geometry != null && geometry.TryGetValue("bodies", out bodiesValue)
            ? bodiesValue as List<object> : null;
        bool valid = geometry != null && geometry.TryGetValue("status", out statusValue) &&
            String.Equals(Convert.ToString(statusValue, CultureInfo.InvariantCulture), "VERIFIED", StringComparison.Ordinal) &&
            bodies != null && bodies.Count > 0;
        List<object> counts = new List<object>();
        if (bodies != null)
            foreach (object item in bodies)
            {
                Dictionary<string, object> body = item as Dictionary<string, object>;
                object facesValue, edgesValue, faceCountValue, faultCountValue;
                List<object> faces = body != null && body.TryGetValue("faces", out facesValue) ? facesValue as List<object> : null;
                List<object> edges = body != null && body.TryGetValue("edges", out edgesValue) ? edgesValue as List<object> : null;
                int faceCount = body != null && body.TryGetValue("face_count", out faceCountValue) && faceCountValue is int
                    ? (int)faceCountValue : -1;
                bool noConsistencyFaults = body != null && body.TryGetValue("consistency_fault_count", out faultCountValue) &&
                    faultCountValue is int && (int)faultCountValue == 0;
                bool itemValid = body != null && faces != null && edges != null && faces.Count > 0 &&
                    faceCount == faces.Count && noConsistencyFaults;
                valid = valid && itemValid;
                counts.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "body_id", body == null || !body.ContainsKey("body_id") ? null : body["body_id"] },
                    { "face_count", faceCount },
                    { "edge_count", edges == null ? -1 : edges.Count },
                    { "consistency_fault_count", body != null && body.ContainsKey("consistency_fault_count")
                        ? body["consistency_fault_count"] : null },
                    { "face_edge_geometry_available", itemValid }
                });
            }
        evidence.Add("status", valid ? "VERIFIED" : "MISMATCH");
        evidence.Add("body_count", bodies == null ? 0 : bodies.Count);
        evidence.Add("direct_face_edge_readback", counts);
        return valid;
    }

    private static double? F13RevolveAggregateSurfaceArea(Dictionary<string, object> geometry)
    {
        object bodyValue;
        List<object> bodies = geometry != null && geometry.TryGetValue("bodies", out bodyValue)
            ? bodyValue as List<object> : null;
        if (bodies == null || bodies.Count == 0) return null;
        double total = 0.0;
        foreach (object value in bodies)
        {
            Dictionary<string, object> body = value as Dictionary<string, object>;
            object areaValue;
            double area;
            if (body == null || !body.TryGetValue("surface_area_mm2", out areaValue) || !TryFiniteNumber(areaValue, out area))
                return null;
            total += area;
            if (Double.IsNaN(total) || Double.IsInfinity(total)) return null;
        }
        return total;
    }

    private static Dictionary<string, object> F13InactiveExpressionDiagnostic(Expression expression)
    {
        if (expression == null) return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "available", false }, { "role", "inactive NoOffset default; not a persistent feature parameter" }
        };
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "available", true },
            { "name", expression.Name },
            { "right_hand_side", expression.RightHandSide },
            { "value", expression.Value },
            { "unit", expression.Units == null ? null : expression.Units.Symbol },
            { "role", "inactive NoOffset default; diagnostic only, not a persistent feature parameter" }
        };
    }

    private static bool F13RevolveOutputGeometryEqual(Dictionary<string, object> expected,
        Dictionary<string, object> actual, out Dictionary<string, object> evidence)
    {
        List<object> acceptedUvDifferences = new List<object>();
        int acceptedUvDifferenceCount = 0;
        string failure = null;
        bool matches = expected != null && actual != null && expected.Count == actual.Count;
        object expectedBodiesValue, actualBodiesValue;
        if (matches && (!expected.TryGetValue("bodies", out expectedBodiesValue) ||
            !actual.TryGetValue("bodies", out actualBodiesValue) ||
            !F13RevolveBodyGeometryRecordsEqual(expectedBodiesValue, actualBodiesValue,
                acceptedUvDifferences, ref acceptedUvDifferenceCount, out failure))) matches = false;
        if (!matches && failure == null) failure = "top_level_geometry_record_shape_mismatch";
        if (matches)
            foreach (KeyValuePair<string, object> item in expected)
            {
                if (String.Equals(item.Key, "bodies", StringComparison.Ordinal)) continue;
                object value;
                if (!actual.TryGetValue(item.Key, out value) ||
                    !F13RevolveGeometryValueEqual(item.Value, value))
                {
                    failure = "top_level_value_mismatch:" + item.Key;
                    matches = false;
                    break;
                }
            }

        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", matches ? "VERIFIED" : "MISMATCH" },
            { "raw_geometry_equal_without_uv_roundoff", matches && acceptedUvDifferenceCount == 0 },
            { "accepted_uv_roundoff_difference_count", acceptedUvDifferenceCount },
            { "included_accepted_uv_roundoff_difference_count", acceptedUvDifferences.Count },
            { "omitted_accepted_uv_roundoff_difference_count", acceptedUvDifferenceCount - acceptedUvDifferences.Count },
            { "accepted_uv_roundoff_differences", acceptedUvDifferences },
            { "machine_epsilon", F13MachineEpsilon },
            { "uv_parameter_multiplier", F13CanonicalUvRoundoffMultiplier },
            { "uv_parameter_rule", "abs(delta) <= 64 * 2^-52 * max(1, abs(expected), abs(actual)); only result faces[].uv_limits[]" },
            { "non_allowlisted_geometry", "exact equality" },
            { "failure", failure }
        };
        return matches;
    }

    private static bool F13RevolveBodyGeometryRecordsEqual(object expected, object actual,
        List<object> acceptedUvDifferences, ref int acceptedUvDifferenceCount, out string failure)
    {
        failure = null;
        IList expectedBodies = expected as IList;
        IList actualBodies = actual as IList;
        if (expectedBodies == null || actualBodies == null || expectedBodies.Count != actualBodies.Count)
        { failure = "body_list_shape_mismatch"; return false; }
        for (int i = 0; i < expectedBodies.Count; i++)
        {
            Dictionary<string, object> expectedBody = expectedBodies[i] as Dictionary<string, object>;
            Dictionary<string, object> actualBody = actualBodies[i] as Dictionary<string, object>;
            if (expectedBody == null || actualBody == null || expectedBody.Count != actualBody.Count)
            { failure = "body_record_shape_mismatch:" + i.ToString(CultureInfo.InvariantCulture); return false; }
            object expectedVolume, actualVolume, expectedArea, actualArea, expectedFaces, actualFaces;
            if (!expectedBody.TryGetValue("volume_mm3", out expectedVolume) ||
                !actualBody.TryGetValue("volume_mm3", out actualVolume) ||
                !expectedBody.TryGetValue("surface_area_mm2", out expectedArea) ||
                !actualBody.TryGetValue("surface_area_mm2", out actualArea) ||
                !expectedBody.TryGetValue("faces", out expectedFaces) ||
                !actualBody.TryGetValue("faces", out actualFaces) ||
                !NullableNumberEqual(expectedVolume, actualVolume) ||
                !F13MeasuredAreaEqual(expectedArea, actualArea) ||
                !F13RevolveFaceGeometryRecordsEqual(expectedFaces, actualFaces, i,
                    acceptedUvDifferences, ref acceptedUvDifferenceCount, out failure))
            {
                if (failure == null) failure = "body_measurement_or_face_geometry_mismatch:" + i.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            foreach (KeyValuePair<string, object> item in expectedBody)
            {
                if (String.Equals(item.Key, "volume_mm3", StringComparison.Ordinal) ||
                    String.Equals(item.Key, "surface_area_mm2", StringComparison.Ordinal) ||
                    String.Equals(item.Key, "faces", StringComparison.Ordinal)) continue;
                object value;
                if (!actualBody.TryGetValue(item.Key, out value) ||
                    !F13RevolveGeometryValueEqual(item.Value, value))
                { failure = "body_value_mismatch:" + i.ToString(CultureInfo.InvariantCulture) + "." + item.Key; return false; }
            }
        }
        return true;
    }

    private static bool F13RevolveFaceGeometryRecordsEqual(object expected, object actual, int bodyIndex,
        List<object> acceptedUvDifferences, ref int acceptedUvDifferenceCount, out string failure)
    {
        failure = null;
        IList expectedFaces = expected as IList;
        IList actualFaces = actual as IList;
        if (expectedFaces == null || actualFaces == null || expectedFaces.Count != actualFaces.Count)
        { failure = "face_list_shape_mismatch:" + bodyIndex.ToString(CultureInfo.InvariantCulture); return false; }
        for (int i = 0; i < expectedFaces.Count; i++)
        {
            Dictionary<string, object> expectedFace = expectedFaces[i] as Dictionary<string, object>;
            Dictionary<string, object> actualFace = actualFaces[i] as Dictionary<string, object>;
            object expectedArea, actualArea, expectedUv, actualUv;
            if (expectedFace == null || actualFace == null || expectedFace.Count != actualFace.Count ||
                !expectedFace.TryGetValue("area_mm2", out expectedArea) ||
                !actualFace.TryGetValue("area_mm2", out actualArea) ||
                !F13MeasuredAreaEqual(expectedArea, actualArea))
            { failure = "face_record_or_area_mismatch:" + bodyIndex.ToString(CultureInfo.InvariantCulture) + "." + i.ToString(CultureInfo.InvariantCulture); return false; }
            if (!expectedFace.TryGetValue("uv_limits", out expectedUv) ||
                !actualFace.TryGetValue("uv_limits", out actualUv) ||
                !F13RevolveUvLimitsEqual(expectedUv, actualUv, bodyIndex, i,
                    acceptedUvDifferences, ref acceptedUvDifferenceCount))
            { failure = "face_uv_limits_mismatch:" + bodyIndex.ToString(CultureInfo.InvariantCulture) + "." + i.ToString(CultureInfo.InvariantCulture); return false; }
            foreach (KeyValuePair<string, object> item in expectedFace)
            {
                if (String.Equals(item.Key, "area_mm2", StringComparison.Ordinal) ||
                    String.Equals(item.Key, "uv_limits", StringComparison.Ordinal)) continue;
                object value;
                if (!actualFace.TryGetValue(item.Key, out value) ||
                    !F13RevolveGeometryValueEqual(item.Value, value))
                { failure = "face_value_mismatch:" + bodyIndex.ToString(CultureInfo.InvariantCulture) + "." + i.ToString(CultureInfo.InvariantCulture) + "." + item.Key; return false; }
            }
        }
        return true;
    }

    private static bool F13RevolveUvLimitsEqual(object expected, object actual, int bodyIndex,
        int faceIndex, List<object> acceptedDifferences, ref int acceptedDifferenceCount)
    {
        Array expectedArray = expected as Array;
        Array actualArray = actual as Array;
        if (expectedArray == null || actualArray == null || expectedArray.Rank != 1 || actualArray.Rank != 1 ||
            expectedArray.GetLowerBound(0) != 0 || actualArray.GetLowerBound(0) != 0 ||
            expectedArray.GetLength(0) != 4 || actualArray.GetLength(0) != 4) return false;
        for (int i = 0; i < 4; i++)
        {
            object expectedValueRaw = expectedArray.GetValue(i);
            object actualValueRaw = actualArray.GetValue(i);
            if (!(expectedValueRaw is double) || !(actualValueRaw is double)) return false;
            double expectedValue = (double)expectedValueRaw;
            double actualValue = (double)actualValueRaw;
            if (Double.IsNaN(expectedValue) || Double.IsInfinity(expectedValue) ||
                Double.IsNaN(actualValue) || Double.IsInfinity(actualValue)) return false;
            double difference = Math.Abs(expectedValue - actualValue);
            double tolerance = F13CanonicalUvRoundoffMultiplier * F13MachineEpsilon *
                Math.Max(1.0, Math.Max(Math.Abs(expectedValue), Math.Abs(actualValue)));
            if (Double.IsNaN(difference) || Double.IsInfinity(difference) || difference > tolerance) return false;
            bool rawTokenMatches = String.Equals(expectedValue.ToString("R", CultureInfo.InvariantCulture),
                actualValue.ToString("R", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (!rawTokenMatches && acceptedDifferenceCount < Int32.MaxValue)
            {
                acceptedDifferenceCount++;
                if (acceptedDifferences.Count < F13CanonicalMaxDetailedRoundoffDifferences)
                    acceptedDifferences.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "path", "bodies[" + bodyIndex.ToString(CultureInfo.InvariantCulture) + "].faces[" + faceIndex.ToString(CultureInfo.InvariantCulture) + "].uv_limits[" + i.ToString(CultureInfo.InvariantCulture) + "]" },
                        { "expected_value", expectedValue },
                        { "actual_value", actualValue },
                        { "absolute_difference", difference },
                        { "maximum_allowed_difference", tolerance }
                    });
            }
        }
        return true;
    }

    private static bool F13MeasuredAreaEqual(object expected, object actual)
    {
        double expectedValue, actualValue;
        return TryFiniteNumber(expected, out expectedValue) && TryFiniteNumber(actual, out actualValue) &&
            NearlyEqual(expectedValue, actualValue);
    }

    private static bool F13RevolveGeometryValueEqual(object expected, object actual)
    {
        if (expected == null || actual == null) return expected == null && actual == null;
        Dictionary<string, object> expectedMap = expected as Dictionary<string, object>;
        Dictionary<string, object> actualMap = actual as Dictionary<string, object>;
        if (expectedMap != null || actualMap != null)
        {
            if (expectedMap == null || actualMap == null || expectedMap.Count != actualMap.Count) return false;
            foreach (KeyValuePair<string, object> item in expectedMap)
            {
                object value;
                if (!actualMap.TryGetValue(item.Key, out value) ||
                    !F13RevolveGeometryValueEqual(item.Value, value)) return false;
            }
            return true;
        }
        Array expectedArray = expected as Array;
        Array actualArray = actual as Array;
        if (expectedArray != null || actualArray != null)
        {
            if (expectedArray == null || actualArray == null || expectedArray.Rank != actualArray.Rank) return false;
            for (int dimension = 0; dimension < expectedArray.Rank; dimension++)
                if (expectedArray.GetLength(dimension) != actualArray.GetLength(dimension) ||
                    expectedArray.GetLowerBound(dimension) != actualArray.GetLowerBound(dimension)) return false;
            IEnumerator expectedElements = expectedArray.GetEnumerator();
            IEnumerator actualElements = actualArray.GetEnumerator();
            while (true)
            {
                bool expectedHasValue = expectedElements.MoveNext();
                bool actualHasValue = actualElements.MoveNext();
                if (expectedHasValue != actualHasValue) return false;
                if (!expectedHasValue) return true;
                if (!F13RevolveGeometryValueEqual(expectedElements.Current, actualElements.Current)) return false;
            }
        }
        IList expectedList = expected as IList;
        IList actualList = actual as IList;
        if (expectedList != null || actualList != null)
        {
            if (expectedList == null || actualList == null || expectedList.Count != actualList.Count) return false;
            for (int i = 0; i < expectedList.Count; i++)
                if (!F13RevolveGeometryValueEqual(expectedList[i], actualList[i])) return false;
            return true;
        }
        return Object.Equals(expected, actual);
    }

    private static double ReadFiniteDouble(Dictionary<string, object> record, string name)
    {
        object value;
        double result;
        if (record == null || !record.TryGetValue(name, out value) || !TryFiniteNumber(value, out result))
            throw new M2ModelException("MODEL_READ_FAILED", "F13 axis evidence has no finite " + name);
        return result;
    }
}
