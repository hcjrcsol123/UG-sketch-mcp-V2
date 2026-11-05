using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.UF;
using NxSketchMcp.M2;

// F14 preflight and transaction guards. This partial is deliberately not
// exposed as an adapter until native construction/readback is integrated.
public sealed partial class M2OpenModelAdapter
{
    public M3PlanSnapshot PrepareHole(M2ModelBinding binding, M3HoleRequest request)
    {
        return PrepareF14HoleGuarded(binding, request);
    }

    private M3ExecutionResult ExecuteF14HolePlan(M2ModelBinding binding, M3PlanSnapshot prepared, M3PlanState plan)
    {
        BindingState state;
        M3ExecutionResult rejection = F14CheckExecutionPreconditions(binding, prepared, plan, out state);
        if (rejection != null) return rejection;
        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F14 native hole transaction"); }
        catch (Exception ex) { return M3Execution("UNCHANGED", binding.RevisionId, null, "UPDATE_FAILED", "could not create hole undo mark (" + ex.GetType().Name + ")"); }
        try
        {
            NXOpen.Features.HolePackage feature = V2HoleFactory.Create(state.WorkPart, plan.HoleRequest, plan.TargetBody, out plan.HoleSourcePointTag);
            plan.Feature = feature; plan.FeatureId = RequiredJournalId(feature, "F14 hole feature");
            if (state.Session.UpdateManager.DoUpdate(mark) != 0) throw new M2ModelException("M3_UPDATE_FAILED", "NX hole update reported errors");
            if (plan.ForcePostCommitVerificationFailureForTesting) throw new M2ModelException("F14_FORCED_POST_COMMIT_FAILURE", "test-only failure after hole commit");
            Dictionary<string, object> definition;
            try { definition = V2HoleFactory.Read(state.WorkPart, feature); }
            catch (Exception ex)
            {
                plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "failure_stage", "native_hole_definition_readback" },
                    { "exception_type", ex.GetType().FullName }, { "exception_message", ex.Message }
                };
                throw;
            }
            Dictionary<string, object> definitionEvidence;
            if (!F14DefinitionMatches(plan, definition, out definitionEvidence))
            {
                plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "hole_definition", definitionEvidence }
                };
                throw new M2ModelException("F14_DEFINITION_MISMATCH", "native hole definition differs from its request");
            }
            plan.HoleDefinition = definition;
            Dictionary<string, object> coverage;
            double tapDiameter = plan.HoleThreadStandard == null ? 0.0 : plan.HoleThreadStandard.TapDrillDiameterMm;
            if (!V2HoleCoverage.Verify(state.WorkPart, plan.TargetBody, feature, plan.HoleRequest, tapDiameter, out coverage))
            {
                plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal) { { "hole_coverage", coverage } };
                throw new M2ModelException("F14_COVERAGE_UNPROVEN", "complete native hole coverage could not be verified");
            }
            if (!F14ThreadWallAssociationMatches(plan, definition, coverage))
            {
                plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal) { { "hole_definition", definitionEvidence }, { "hole_coverage", coverage }, { "thread_wall_association", "MISMATCH" } };
                throw new M2ModelException("F14_DEFINITION_MISMATCH", "native Thread does not reference the proven target hole wall");
            }
            plan.HoleCoverage = coverage;
            bool existingHolesMatch = F14PersistentHoleDefinitionsEqual(plan.HoleDefinitionsBefore,
                F14CapturePersistentHoleDefinitions(state.WorkPart, plan.FeatureId));
            SnapshotResult after = Capture(binding, state);
            plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart, F12MassPropertyAccuracy);
            plan.AffectedBodies = ResolveAffectedBodies(plan, plan.BodiesAfter, feature);
            plan.AffectedBodyIds = new List<string>();
            foreach (M3BodyMeasure body in plan.AffectedBodies) plan.AffectedBodyIds.Add(body.Id);
            plan.AffectedBodyIds.Sort(StringComparer.Ordinal);
            plan.AffectedBodyId = plan.AffectedBodyIds.Count == 1 ? plan.AffectedBodyIds[0] : null;
            plan.BodyFingerprintsAfter = CaptureF12BodyFingerprints(state.WorkPart, plan.BodiesAfter);
            plan.HoleResultGeometry = CaptureF13RevolveOutputGeometry(state.WorkPart, plan.AffectedBodies);
            Dictionary<string, object> preservation, effect;
            bool preserved = F14ModelPreserved(plan, after.Snapshot, definition, out preservation);
            Dictionary<string, M3SketchProjectionCurveState> curvesAfter = CaptureM3SketchProjectionCurveInventory(state.WorkPart);
            Dictionary<string, object> curveEvidence;
            bool curvePreserved = F14CurveInventoryMatches(state.WorkPart, plan, definition, curvesAfter, out curveEvidence);
            plan.F12CurveInventoryAfter = M3SketchProjectionCurveInventoryText(curvesAfter);
            string pointTag = definition.ContainsKey("point_tag") ? definition["point_tag"] as string : null;
            string directionTag = definition.ContainsKey("direction_tag") ? definition["direction_tag"] as string : null;
            bool helpersPreserved = F14HelpersMatch(plan.HoleHelpersBefore, F14CaptureHelpers(state.WorkPart), pointTag, plan.HoleSourcePointTag, directionTag);
            bool effectMatches = F14BodyEffectMatches(plan, plan.AffectedBodies, plan.BodiesAfter, plan.BodyFingerprintsAfter, out effect);
            List<object> supportRecords;
            bool supportMatches = ValidateM4SupportTracks(state, out supportRecords, null);
            if (!preserved || !curvePreserved || !helpersPreserved || !effectMatches || !supportMatches || !existingHolesMatch)
            {
                plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "model_preservation", preservation }, { "body_effect", effect }, { "curves_preserved", curvePreserved }, { "curve_inventory", curveEvidence },
                    { "helpers_preserved", helpersPreserved }, { "support_associations", supportRecords }, { "existing_hole_definitions_preserved", existingHolesMatch }
                };
                throw new M2ModelException("F14_PRESERVATION_FAILED", "hole effect or existing model preservation could not be verified");
            }
            plan.SemanticAfter = CanonicalSemanticSnapshot(after.Snapshot);
            plan.GeometryAfter = CaptureFullGeometry(state.WorkPart);
            plan.HistoryAfter = HistoryProjection(after.Snapshot);
            Dictionary<string, object> result = F14Result(binding, plan, definition, plan.AffectedBodies, plan.HoleResultGeometry);
            result.Add("hole_definition", definitionEvidence); result.Add("hole_coverage", coverage); result.Add("model_preservation", preservation); result.Add("body_effect", effect); result.Add("curve_inventory", curveEvidence);
            plan.Execution = M3Execution("APPLIED", binding.RevisionId, result, null, null);
            return plan.Execution;
        }
        catch (Exception ex)
        {
            Exception failure = ex is V2HoleBuilderCleanupException || ex is V2HoleCoverage.CleanupException
                ? (Exception)new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN", "hole builder cleanup could not be verified") : ex;
            plan.Execution = RestoreM3State(binding, state, plan, mark, failure);
            return plan.Execution;
        }
        finally { TryDeleteUndoMark(state.Session, mark); }
    }

    private M3VerificationResult VerifyF14HolePlan(M2ModelBinding binding, M3PlanSnapshot prepared, M3PlanState plan)
    {
        if (prepared == null || plan == null || plan.HoleRequest == null || plan.Execution == null || plan.Execution.State != "APPLIED")
            return M3VerificationFailure(binding == null ? null : binding.RevisionId, "fresh hole verification requires an applied plan");
        BindingState state = RequireState(binding); string reason;
        if (!Object.ReferenceEquals(state, plan.BindingState) || !IsBindingCurrent(binding, out reason))
            return M3VerificationFailure(binding.RevisionId, "hole binding changed before verification");
        Dictionary<string, object> definition;
        try { definition = V2HoleFactory.Read(state.WorkPart, FindFeature(state.WorkPart, plan.FeatureId) as NXOpen.Features.HolePackage); }
        catch (V2HoleBuilderCleanupException ex) { throw M3AnalysisStateUnknown("hole readback builder cleanup could not be verified", ex); }
        Dictionary<string, object> coverage;
        bool coverageMatches;
        try { coverageMatches = V2HoleCoverage.Verify(state.WorkPart, plan.TargetBody,
            FindFeature(state.WorkPart, plan.FeatureId) as NXOpen.Features.HolePackage, plan.HoleRequest,
            plan.HoleThreadStandard == null ? 0.0 : plan.HoleThreadStandard.TapDrillDiameterMm, out coverage); }
        catch (V2HoleCoverage.CleanupException ex) { throw M3AnalysisStateUnknown("hole geometry audit cleanup could not be verified", ex); }
        coverageMatches = coverageMatches && F14ThreadWallAssociationMatches(plan, definition, coverage);
        bool existingHolesMatch = F14PersistentHoleDefinitionsEqual(plan.HoleDefinitionsBefore,
            F14CapturePersistentHoleDefinitions(state.WorkPart, plan.FeatureId));
        SnapshotResult capture = Capture(binding, state);
        List<M3BodyMeasure> bodies = CaptureM3BodyMeasures(state.WorkPart, F12MassPropertyAccuracy);
        List<M3BodyMeasure> affected = ResolveAffectedBodies(plan, bodies, FindFeature(state.WorkPart, plan.FeatureId));
        Dictionary<string, string> fingerprints = CaptureF12BodyFingerprints(state.WorkPart, bodies);
        Dictionary<string, object> geometry = CaptureF13RevolveOutputGeometry(state.WorkPart, affected);
        Dictionary<string, object> definitionEvidence, preservation, effect, geometryEvidence, bodyEvidence, fingerprintEvidence;
        bool definitionMatches = F14DefinitionMatches(plan, definition, out definitionEvidence) &&
            F14NativeIdentitiesMatch(plan.HoleDefinition, definition, plan.HoleRequest.Kind == "threaded");
        bool preservationMatches = F14ModelPreserved(plan, capture.Snapshot, definition, out preservation);
        bool effectMatches = F14BodyEffectMatches(plan, affected, bodies, fingerprints, out effect);
        bool bodiesMatch = BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesAfter, null), BodyMeasureRecords(bodies, null), out bodyEvidence);
        bool fingerprintsMatch = F12BodyFingerprintsEqual(plan.BodyFingerprintsAfter, fingerprints, out fingerprintEvidence);
        bool geometryMatches = String.Equals(M2Json.CanonicalText(plan.HoleResultGeometry), M2Json.CanonicalText(geometry), StringComparison.Ordinal);
        geometryEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", geometryMatches ? "VERIFIED" : "MISMATCH" },
            { "comparison", "exact canonical native hole output geometry; no F13 roundoff waiver" }
        };
        bool semanticMatches = String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(capture.Snapshot), StringComparison.Ordinal) &&
            String.Equals(plan.GeometryAfter, CaptureFullGeometry(state.WorkPart), StringComparison.Ordinal) &&
            SemanticValuesEqual(plan.HistoryAfter, HistoryProjection(capture.Snapshot), "history");
        Dictionary<string, M3SketchProjectionCurveState> currentCurves = CaptureM3SketchProjectionCurveInventory(state.WorkPart);
        Dictionary<string, object> curveEvidence;
        bool curveMatches = F14CurveInventoryMatches(state.WorkPart, plan, definition, currentCurves, out curveEvidence) &&
            String.Equals(plan.F12CurveInventoryAfter, M3SketchProjectionCurveInventoryText(currentCurves), StringComparison.Ordinal);
        preservation["curve_inventory"] = curveEvidence;
        string pointTag = definition.ContainsKey("point_tag") ? definition["point_tag"] as string : null;
        string directionTag = definition.ContainsKey("direction_tag") ? definition["direction_tag"] as string : null;
        bool helpersMatch = F14HelpersMatch(plan.HoleHelpersBefore, F14CaptureHelpers(state.WorkPart), pointTag, plan.HoleSourcePointTag, directionTag);
        List<object> supportRecords; bool supportMatches = ValidateM4SupportTracks(state, out supportRecords, null);
        bool verified = definitionMatches && coverageMatches && preservationMatches && effectMatches && bodiesMatch && fingerprintsMatch && geometryMatches && semanticMatches && curveMatches && helpersMatch && supportMatches && existingHolesMatch;
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "hole_definition", VerificationCheck(definitionMatches ? "VERIFIED" : "MISMATCH", definitionEvidence) },
            { "hole_coverage", VerificationCheck(coverageMatches ? "VERIFIED" : "MISMATCH", coverage) },
            { "model_preservation", VerificationCheck(preservationMatches && curveMatches && helpersMatch && existingHolesMatch ? "VERIFIED" : "MISMATCH", preservation) },
            { "body_effect", VerificationCheck(effectMatches ? "VERIFIED" : "MISMATCH", effect) },
            { "geometry", VerificationCheck(geometryMatches ? "VERIFIED" : "MISMATCH", geometryEvidence) },
            { "bodies", VerificationCheck(bodiesMatch && fingerprintsMatch ? "VERIFIED" : "MISMATCH", new Dictionary<string, object> { { "measurements", bodyEvidence }, { "complete_geometry_fingerprints", fingerprintEvidence } }) },
            { "history", VerificationCheck(semanticMatches ? "VERIFIED" : "MISMATCH", new Dictionary<string, object> { { "matches_execute", semanticMatches } }) },
            { "support_associations", VerificationCheck(supportMatches ? "VERIFIED" : "MISMATCH", new Dictionary<string, object> { { "associations", supportRecords } }) }
        };
        return new M3VerificationResult { Verified = verified, RevisionId = binding.RevisionId, Checks = checks,
            Result = F14Result(binding, plan, definition, affected, geometry), ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified ? null : "Fresh native hole definition, geometry or model preservation differs from the executed plan." };
    }

    private static Dictionary<string, object> F14Result(M2ModelBinding binding, M3PlanState plan,
        Dictionary<string, object> definition, List<M3BodyMeasure> affected, Dictionary<string, object> geometry)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "kind", "create_hole" }, { "feature_id", plan.FeatureId }, { "body_id", plan.AffectedBodyId },
            { "body_ids", StringListAsObjects(plan.AffectedBodyIds) }, { "bodies", BodyMeasureRecords(affected, null) },
            { "target_body_id", plan.TargetBodyId }, { "hole_kind", plan.HoleRequest.Kind },
            { "depth_mode", plan.HoleRequest.DepthMode }, { "native_definition", definition },
            { "result_geometry", geometry }, { "physical_thread_geometry", false }, { "revision_id", binding.RevisionId }
        };
    }

    private static bool F14BodyEffectMatches(M3PlanState plan, List<M3BodyMeasure> affected, List<M3BodyMeasure> after,
        Dictionary<string, string> fingerprints, out Dictionary<string, object> evidence)
    {
        M3BodyMeasure prior = FindBodyMeasure(plan.BodiesBefore, plan.TargetBodyId);
        double? volume = AggregateBodyVolume(affected);
        bool oneSolid = affected != null && affected.Count == 1 && affected[0].IsSolid &&
            affected[0].Body.Tag.Equals(plan.TargetBody.Tag);
        bool reduced = prior != null && prior.VolumeMm3.HasValue && volume.HasValue && volume.Value > 0 &&
            volume.Value < prior.VolumeMm3.Value - ValueTolerance(volume.Value, prior.VolumeMm3.Value);
        Dictionary<string, object> otherBodies;
        bool nonTarget = F12NonTargetBodiesMatch(plan, after, fingerprints, out otherBodies);
        bool count = after != null && after.Count == plan.BodiesBefore.Count;
        bool match = oneSolid && reduced && nonTarget && count;
        evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", match ? "VERIFIED" : "MISMATCH" }, { "one_target_solid", oneSolid },
            { "target_volume_before_mm3", prior == null ? null : (object)prior.VolumeMm3 }, { "target_volume_after_mm3", volume },
            { "target_volume_reduced", reduced }, { "body_count_preserved", count }, { "non_target_bodies", otherBodies }
        };
        return match;
    }

    private M3PlanSnapshot PrepareF14HoleGuarded(M2ModelBinding binding, M3HoleRequest source)
    {
        M3HoleRequest request = F14ValidatedHoleRequest(source);
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "an active Sketch must be ended before preparing a hole");
        Dictionary<string, object> priorHoles = F14CapturePersistentHoleDefinitions(state.WorkPart, null);
        SnapshotResult capture = Capture(binding, state);
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current part revision");
        Body target = FindBody(state.WorkPart, request.TargetBodyId);
        if (target == null || !target.IsSolidBody)
            throw new M2ModelException("UNSUPPORTED_BODY", "hole target must identify one solid body");
        Dictionary<string, object> entryEvidence = F14EntryEvidence(target, request);
        M3PlanState plan = CreateM3PlanState(binding, state, "create_hole", capture, F12MassPropertyAccuracy);
        plan.HoleRequest = request;
        plan.HoleDefinitionsBefore = priorHoles;
        if (request.Kind == "threaded") plan.HoleThreadStandard = F14ReadThreadStandard(state.Session, request);
        plan.TargetBody = target;
        plan.TargetBodyId = RequiredJournalId(target, "hole target body");
        plan.F12SemanticBefore = capture.Snapshot;
        plan.HoleCurvesBefore = CaptureM3SketchProjectionCurveInventory(state.WorkPart);
        plan.F12CurveInventoryBefore = M3SketchProjectionCurveInventoryText(plan.HoleCurvesBefore);
        plan.HoleHelpersBefore = F14CaptureHelpers(state.WorkPart);
        try { plan.BodyFingerprintsBefore = CaptureF12BodyFingerprints(state.WorkPart, plan.BodiesBefore); }
        catch (Exception ex)
        {
            ThrowIfF12AnalysisStateUnknown(ex, "body geometry capture could not prove hole preparation was read-only");
            throw;
        }
        Dictionary<string, object> view = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "kind", "create_hole" }, { "revision_id", binding.RevisionId },
            { "target_body_id", plan.TargetBodyId }, { "hole_kind", request.Kind },
            { "depth_mode", request.DepthMode }, { "position_mm", NumberList(request.PositionMm) },
            { "direction", NumberList(request.Direction) },
            { "target_body_before", FindBodyMeasure(plan.BodiesBefore, plan.TargetBodyId).PublicRecord },
            { "non_target_bodies_before", BodyMeasureRecords(plan.BodiesBefore, plan.TargetBodyId) },
            { "physical_thread_geometry", false }, { "entry_check", entryEvidence }
        };
        if (request.DepthMode == "blind") view.Add("hole_depth_mm", request.HoleDepthMm);
        if (request.Kind != "threaded") view.Add("diameter_mm", request.DiameterMm);
        if (request.Kind == "counterbore") { view.Add("counterbore_diameter_mm", request.CounterboreDiameterMm); view.Add("counterbore_depth_mm", request.CounterboreDepthMm); }
        if (request.Kind == "countersink") { view.Add("countersink_diameter_mm", request.CountersinkDiameterMm); view.Add("countersink_angle_deg", request.CountersinkAngleDeg); }
        if (request.Kind == "threaded")
        {
            view.Add("thread_standard", request.ThreadStandard); view.Add("thread_size", request.ThreadSize);
            view.Add("radial_engage", request.RadialEngage); view.Add("pitch_mm", request.PitchMm);
            view.Add("hand", request.Hand); view.Add("thread_depth_mm", request.ThreadDepthMm);
        }
        if (plan.HoleThreadStandard != null) view.Add("thread_standard_table", F14StandardEvidence(plan.HoleThreadStandard));
        return new M3PlanSnapshot { Kind = "create_hole", RevisionId = binding.RevisionId,
            PublicView = view, AdapterState = plan };
    }

    // Called immediately before opening the Undo transaction, not as a cached
    // assertion from prepare. Native model state and topology must still match.
    private M3ExecutionResult F14CheckExecutionPreconditions(M2ModelBinding binding,
        M3PlanSnapshot prepared, M3PlanState plan, out BindingState state)
    {
        state = null;
        if (binding == null || prepared == null || plan == null || plan.HoleRequest == null || prepared.Kind != "create_hole")
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, "INVALID_PLAN", "hole plan state is unavailable");
        try { state = RequireState(binding); }
        catch (M2ModelException ex) { return M3Execution("UNCHANGED", binding.RevisionId, null, ex.Code, ex.Message); }
        string reason;
        if (!Object.ReferenceEquals(state, plan.BindingState) || !String.Equals(plan.RevisionId, prepared.RevisionId, StringComparison.Ordinal) ||
            !IsBindingCurrent(binding, out reason) || !String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "hole plan binding or revision changed");
        try
        {
            RequireWritableSimplePart(state.WorkPart);
            RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
            if (state.Session.ActiveSketch != null) throw new M2ModelException("NX_BUSY", "an active Sketch must be ended before creating a hole");
            F14EntryEvidence(plan.TargetBody, plan.HoleRequest);
            if (!F14PersistentHoleDefinitionsEqual(plan.HoleDefinitionsBefore, F14CapturePersistentHoleDefinitions(state.WorkPart, null)))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "an existing native hole definition changed after preparation");
            if (plan.HoleRequest.Kind == "threaded")
            {
                F14ThreadStandardResult currentStandard = F14ReadThreadStandard(state.Session, plan.HoleRequest);
                if (!F14StandardEqual(plan.HoleThreadStandard, currentStandard))
                    return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "thread standard row or effective directory changed after preparation");
            }
            SnapshotResult current = Capture(binding, state);
            if (!String.Equals(CanonicalSemanticSnapshot(current.Snapshot), plan.SemanticBefore, StringComparison.Ordinal) ||
                !String.Equals(CaptureFullGeometry(state.WorkPart), plan.GeometryBefore, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "model state changed after hole preparation");
            List<M3BodyMeasure> bodies = CaptureM3BodyMeasures(state.WorkPart, F12MassPropertyAccuracy);
            Dictionary<string, object> evidence;
            if (!BodyMeasuresEqual(plan.BodiesBefore, bodies, out evidence))
                return M3Execution("UNCHANGED", binding.RevisionId, evidence, "STALE_GEOMETRY", "body measurements changed after hole preparation");
            Dictionary<string, string> fingerprints = CaptureF12BodyFingerprints(state.WorkPart, bodies);
            if (!F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore, fingerprints, out evidence))
                return M3Execution("UNCHANGED", binding.RevisionId, evidence, "STALE_GEOMETRY", "body geometry changed after hole preparation");
            if (!F14HelpersMatch(plan.HoleHelpersBefore, F14CaptureHelpers(state.WorkPart), null, null, null))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_GEOMETRY", "point or direction inventory changed after hole preparation");
        }
        catch (M2ModelException ex)
        {
            M2ModelException unknown = F12AnalysisStateUnknownOrNull(ex, "hole precondition audit could not prove model state");
            return unknown != null ? M3Execution("UNKNOWN", binding.RevisionId, null, unknown.Code, unknown.Message)
                : M3Execution("UNCHANGED", binding.RevisionId, null, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            M2ModelException unknown = F12AnalysisStateUnknownOrNull(ex, "hole precondition audit could not prove model state");
            return unknown != null ? M3Execution("UNKNOWN", binding.RevisionId, null, unknown.Code, unknown.Message)
                : M3Execution("UNCHANGED", binding.RevisionId, null, "MODEL_READ_FAILED", "could not refresh hole preconditions (" + ex.GetType().Name + ")");
        }
        return null;
    }

    private static M3HoleRequest F14ValidatedHoleRequest(M3HoleRequest source)
    {
        if (source == null || String.IsNullOrWhiteSpace(source.RevisionId) || source.RevisionId.Length > 128 ||
            String.IsNullOrWhiteSpace(source.TargetBodyId) || source.TargetBodyId.Length > 4096 ||
            (source.Kind != "simple" && source.Kind != "counterbore" && source.Kind != "countersink" && source.Kind != "threaded") ||
            (source.DepthMode != "blind" && source.DepthMode != "through") ||
            source.PositionMm == null || source.PositionMm.Length != 3 || source.Direction == null || source.Direction.Length != 3)
            throw new M2ModelException("INVALID_REQUEST", "invalid hole request identity, kind, depth or world vectors");
        double[] point = (double[])source.PositionMm.Clone(), direction = (double[])source.Direction.Clone();
        for (int i = 0; i < 3; i++)
            if (!IsFinite(point[i]) || Math.Abs(point[i]) > 1000000 || !IsFinite(direction[i]))
                throw new M2ModelException("INVALID_REQUEST", "hole world point or direction is invalid");
        double scale = Math.Max(Math.Abs(direction[0]), Math.Max(Math.Abs(direction[1]), Math.Abs(direction[2])));
        if (scale == 0) throw new M2ModelException("INVALID_REQUEST", "hole direction must be nonzero");
        double x = direction[0]/scale, y = direction[1]/scale, z = direction[2]/scale;
        double length = Math.Sqrt(x*x + y*y + z*z);
        direction = new double[] { x/length, y/length, z/length };
        if ((source.DepthMode == "blind" && !F14PositiveMm(source.HoleDepthMm)) ||
            (source.Kind != "threaded" && !F14PositiveMm(source.DiameterMm)))
            throw new M2ModelException("INVALID_REQUEST", "hole depth or diameter is invalid");
        if (source.Kind == "counterbore" && (!F14PositiveMm(source.CounterboreDiameterMm) || !F14PositiveMm(source.CounterboreDepthMm) ||
            source.CounterboreDiameterMm <= source.DiameterMm || (source.DepthMode == "blind" && source.CounterboreDepthMm >= source.HoleDepthMm)))
            throw new M2ModelException("INVALID_REQUEST", "counterbore dimensions are invalid");
        if (source.Kind == "countersink" && (!F14PositiveMm(source.CountersinkDiameterMm) || source.CountersinkDiameterMm <= source.DiameterMm ||
            !IsFinite(source.CountersinkAngleDeg) || source.CountersinkAngleDeg <= 0 || source.CountersinkAngleDeg >= 180 ||
            (source.DepthMode == "blind" && Math.Atan2(source.CountersinkDiameterMm/2-source.DiameterMm/2, source.HoleDepthMm) >= source.CountersinkAngleDeg*Math.PI/360)))
            throw new M2ModelException("INVALID_REQUEST", "countersink dimensions or included angle are invalid");
        if (source.Kind == "threaded" && (String.IsNullOrWhiteSpace(source.ThreadStandard) || String.IsNullOrWhiteSpace(source.ThreadSize) ||
            String.IsNullOrWhiteSpace(source.RadialEngage) || source.ThreadStandard.Length > 128 || source.ThreadSize.Length > 128 ||
            source.RadialEngage.Length > 128 || (source.Hand != "left" && source.Hand != "right") ||
            !F14PositiveMm(source.PitchMm) || !F14PositiveMm(source.ThreadDepthMm) ||
            (source.DepthMode == "blind" && source.ThreadDepthMm > source.HoleDepthMm)))
            throw new M2ModelException("INVALID_REQUEST", "thread standard, size, engagement, pitch, hand or depth is invalid");
        return new M3HoleRequest { RevisionId = source.RevisionId, TargetBodyId = source.TargetBodyId, Kind = source.Kind,
            DepthMode = source.DepthMode, PositionMm = point, Direction = direction, DiameterMm = source.DiameterMm,
            HoleDepthMm = source.HoleDepthMm, CounterboreDiameterMm = source.CounterboreDiameterMm, CounterboreDepthMm = source.CounterboreDepthMm,
            CountersinkDiameterMm = source.CountersinkDiameterMm, CountersinkAngleDeg = source.CountersinkAngleDeg,
            ThreadStandard = source.ThreadStandard, ThreadSize = source.ThreadSize, RadialEngage = source.RadialEngage,
            PitchMm = source.PitchMm, Hand = source.Hand, ThreadDepthMm = source.ThreadDepthMm };
    }

    private static bool F14PositiveMm(double value) { return IsFinite(value) && value > 0 && value <= 1000000; }

    private static Dictionary<string, object> F14CapturePersistentHoleDefinitions(Part part, string excludedFeatureId)
    {
        Dictionary<string, object> records = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (NXOpen.Features.Feature feature in part.Features)
        {
            NXOpen.Features.HolePackage hole = feature as NXOpen.Features.HolePackage;
            if (hole == null || String.Equals(hole.JournalIdentifier, excludedFeatureId, StringComparison.Ordinal)) continue;
            Dictionary<string, object> actual;
            try { actual = V2HoleFactory.Read(part, hole); }
            catch (V2HoleBuilderCleanupException ex) { throw M3AnalysisStateUnknown("existing hole readback cleanup could not be verified", ex); }
            catch (Exception ex) { throw new M2ModelException("UNSUPPORTED_HOLE_PRESERVATION", "existing HolePackage cannot be uniquely read (" + ex.GetType().Name + ")"); }
            if (!actual.ContainsKey("section_association_verified") || !Object.Equals(actual["section_association_verified"], true))
                throw new M2ModelException("UNSUPPORTED_HOLE_PRESERVATION", "existing hole Section point association is not uniquely verified");
            List<object> relationFailures = new List<object>();
            double[] origin = actual.ContainsKey("position_mm") ? actual["position_mm"] as double[] : null;
            double[] axis = actual.ContainsKey("direction") ? actual["direction"] as double[] : null;
            if (origin == null || axis == null) relationFailures.Add("native_frame");
            else { F14Vector(actual, "point_coordinates_mm", origin, 1e-6, relationFailures); F14Vector(actual, "section_source_point_coordinates_mm", origin, 1e-6, relationFailures); F14Vector(actual, "projection_direction_origin_mm", origin, 1e-6, relationFailures); F14Vector(actual, "projection_direction", axis, 1e-8, relationFailures); }
            F14Require(actual, "boolean_type", "Subtract", relationFailures);
            F14Require(actual, "project_point_onto_target", false, relationFailures);
            F14Require(actual, "direction_method", "Vector", relationFailures);
            if (relationFailures.Count != 0) throw new M2ModelException("UNSUPPORTED_HOLE_PRESERVATION", "existing hole native frame or point association is inconsistent");
            string id = actual.ContainsKey("feature_id") ? actual["feature_id"] as string : null;
            if (String.IsNullOrEmpty(id) || records.ContainsKey(id)) throw new M2ModelException("UNSUPPORTED_HOLE_PRESERVATION", "existing hole identities are not unique");
            Dictionary<string, object> stable = new Dictionary<string, object>(StringComparer.Ordinal);
            string[] keys = new string[] { "feature_id", "kind", "depth_mode", "position_mm", "direction", "native_depth_option",
                "native_depth_limit", "native_type", "native_form", "boolean_type", "project_point_onto_target", "direction_method",
                "point_coordinates_mm", "section_source_point_coordinates_mm", "projection_direction", "projection_direction_origin_mm", "rule_types", "section_rule_indices",
                "target_body_ids", "diameter_mm", "hole_depth_mm", "tip_angle_deg", "counterbore_diameter_mm",
                "counterbore_depth_mm", "countersink_diameter_mm", "countersink_angle_deg", "thread_type", "thread_internal",
                "thread_standard", "thread_size", "radial_engage", "hand", "pitch_mm", "pitch_expression_value_mm", "thread_starts",
                "thread_major_diameter_mm", "thread_minor_diameter_mm", "thread_angle_deg", "thread_tapered", "thread_depth_mm",
                "tap_drill_diameter_mm", "thread_length_option", "relate_hole_depth_to_thread_depth", "threaded_start_chamfer_enabled",
                "threaded_end_chamfer_enabled", "threaded_relief_enabled", "relief_chamfer_enabled", "thread_at_both_ends",
                "package_thread_standard", "package_thread_size", "package_radial_engage", "package_thread_rotation", "package_thread_depth_mm",
                "thread_limit", "thread_reverse_direction", "thread_symbol_geometry" };
            foreach (string key in keys) if (actual.ContainsKey(key)) stable.Add(key, actual[key]);
            foreach (string key in new string[] { "thread_cylindrical_face", "thread_start_object" })
            {
                if (!actual.ContainsKey(key)) continue;
                Dictionary<string, object> selected = actual[key] as Dictionary<string, object>;
                if (selected == null) { stable.Add(key, null); continue; }
                Dictionary<string, object> stableSelection = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string member in new string[] { "type", "origin_mm", "normal", "body_id" }) if (selected.ContainsKey(member)) stableSelection.Add(member, selected[member]);
                stable.Add(key, stableSelection);
            }
            Dictionary<string, object> faceDefinition = actual.ContainsKey("thread_face_definition") ? actual["thread_face_definition"] as Dictionary<string, object> : null;
            if (faceDefinition != null)
            {
                Dictionary<string, object> stableFace = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string member in new string[] { "axis_point_mm", "axis_direction", "bbox_mm", "radius_mm", "coaxial_tap_cylinder_count", "listed_by_internal_hole" })
                    if (faceDefinition.ContainsKey(member)) stableFace.Add(member, faceDefinition[member]);
                stable.Add("thread_face_definition", stableFace);
            }
            foreach (string key in new string[] { "hole_ownership", "thread_ownership" })
            {
                Dictionary<string, object> ownership = actual.ContainsKey(key) ? actual[key] as Dictionary<string, object> : null;
                if (ownership == null) continue;
                Dictionary<string, object> stableOwnership = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string member in new string[] { "id", "is_internal", "feature_type", "timestamp", "parent_ids", "body_ids" })
                    if (ownership.ContainsKey(member)) stableOwnership.Add(member, ownership[member]);
                stable.Add(key, stableOwnership);
            }
            records.Add(id, stable);
        }
        return records;
    }

    private static bool F14PersistentHoleDefinitionsEqual(Dictionary<string, object> left, Dictionary<string, object> right)
    {
        return left != null && right != null && String.Equals(M2Json.CanonicalText(left), M2Json.CanonicalText(right), StringComparison.Ordinal);
    }

    private static Dictionary<string, object> F14EntryEvidence(Body body, M3HoleRequest request)
    {
        const double witnessMm = 0.01;
        UFSession uf = UFSession.GetUFSession();
        int onBody, inward, outward;
        double[] p = request.PositionMm;
        double[] plus = new double[3], minus = new double[3];
        for (int i = 0; i < 3; i++) { plus[i] = p[i] + witnessMm * request.Direction[i]; minus[i] = p[i] - witnessMm * request.Direction[i]; }
        uf.Modl.AskPointContainment(p, body.Tag, out onBody);
        uf.Modl.AskPointContainment(plus, body.Tag, out inward);
        uf.Modl.AskPointContainment(minus, body.Tag, out outward);
        List<string> interiorFaceTags = new List<string>();
        Face entryFace = null;
        foreach (Face face in body.GetFaces())
        {
            int status; uf.Modl.AskPointContainment(p, face.Tag, out status);
            if (status == 1) { interiorFaceTags.Add(face.Tag.ToString()); entryFace = face; }
        }
        if (onBody != 3 || inward != 1 || outward != 2 || interiorFaceTags.Count != 1)
            throw new M2ModelException("INVALID_HOLE_ENTRY", "hole position must lie inside one target face, with the world direction entering the solid at a 0.01 mm witness offset");
        int faceType, normalSense; double radius, radiusData;
        double[] facePoint = new double[3], faceDirection = new double[3], box = new double[6];
        uf.Modl.AskFaceData(entryFace.Tag, out faceType, facePoint, faceDirection, box, out radius, out radiusData, out normalSense);
        double normalLength = Math.Sqrt(faceDirection[0]*faceDirection[0] + faceDirection[1]*faceDirection[1] + faceDirection[2]*faceDirection[2]);
        double normalDot = normalLength == 0 ? 0 : Math.Abs((faceDirection[0]*request.Direction[0] + faceDirection[1]*request.Direction[1] + faceDirection[2]*request.Direction[2]) / normalLength);
        if (faceType != UFConstants.UF_MODL_PLANAR_FACE || !IsFinite(normalDot) || normalDot < 1.0 - 1e-9)
            throw new M2ModelException("INVALID_HOLE_ENTRY", "hole entry must be planar with its direction normal to the face");
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", "VERIFIED" }, { "body_containment_at_entry", onBody },
            { "body_containment_inward", inward }, { "body_containment_outward", outward },
            { "witness_offset_mm", witnessMm }, { "interior_face_tags", StringListAsObjects(interiorFaceTags) },
            { "entry_face_type", "planar" }, { "absolute_axis_normal_dot", normalDot }, { "normal_dot_tolerance", 1e-9 }
        };
    }

    private static Dictionary<string, string> F14CaptureHelpers(Part part)
    {
        Dictionary<string, string> records = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (NXOpen.Point point in part.Points.ToArray())
        {
            Point3d p = point.Coordinates;
            records.Add("P:" + point.Tag.ToString(), M2Json.CanonicalText(new object[] { point.JournalIdentifier, p.X, p.Y, p.Z }));
        }
        foreach (NXOpen.Direction direction in part.Directions.ToArray())
        {
            Point3d p = direction.Origin; Vector3d v = direction.Vector;
            records.Add("D:" + direction.Tag.ToString(), M2Json.CanonicalText(new object[] { direction.JournalIdentifier, p.X, p.Y, p.Z, v.X, v.Y, v.Z }));
        }
        return records;
    }

    private static bool F14HelpersMatch(Dictionary<string, string> before, Dictionary<string, string> after,
        string allowedPointTag, string allowedSourcePointTag, string allowedDirectionTag)
    {
        if (before == null || after == null) return false;
        foreach (KeyValuePair<string, string> row in before)
        {
            string actual;
            if (!after.TryGetValue(row.Key, out actual) || !String.Equals(row.Value, actual, StringComparison.Ordinal)) return false;
        }
        foreach (string key in after.Keys)
            if (!before.ContainsKey(key) && key != "P:" + allowedPointTag && key != "P:" + allowedSourcePointTag && key != "D:" + allowedDirectionTag) return false;
        return true;
    }

    private static bool F14CurveInventoryMatches(Part part, M3PlanState plan, Dictionary<string, object> definition,
        Dictionary<string, M3SketchProjectionCurveState> after, out Dictionary<string, object> evidence)
    {
        Dictionary<string, M3SketchProjectionCurveState> before = plan.HoleCurvesBefore;
        List<object> priorRows = new List<object>(), afterRows = new List<object>(), violations = new List<object>();
        HashSet<string> added = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, M3SketchProjectionCurveState> pair in before)
        {
            priorRows.Add(F14CurveRecord(pair.Value));
            M3SketchProjectionCurveState current;
            if (!after.TryGetValue(pair.Key, out current) || !String.Equals(pair.Value.Signature, current.Signature, StringComparison.Ordinal))
                violations.Add("existing_curve_changed:" + pair.Key);
        }
        foreach (KeyValuePair<string, M3SketchProjectionCurveState> pair in after)
        {
            afterRows.Add(F14CurveRecord(pair.Value));
            if (!before.ContainsKey(pair.Key)) added.Add(pair.Key);
        }
        string[] symbols = plan.HoleRequest.Kind == "threaded" && definition.ContainsKey("thread_symbol_curve_tags")
            ? definition["thread_symbol_curve_tags"] as string[] : new string[0];
        HashSet<string> expected = symbols == null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(symbols, StringComparer.Ordinal);
        if (symbols == null || expected.Count != symbols.Length || (plan.HoleRequest.Kind == "threaded" && expected.Count != 2) || !SetEquals(added, expected))
            violations.Add("new_curve_set_differs_from_native_thread_symbols");
        foreach (string tag in expected)
        {
            M3SketchProjectionCurveState item;
            if (!after.TryGetValue(tag, out item) || item.Type != "Arc") violations.Add("symbol_not_an_inventory_arc:" + tag);
        }
        foreach (Sketch sketch in part.Sketches.ToArray())
            foreach (NXObject item in sketch.GetAllGeometry())
                if (item != null && expected.Contains(item.Tag.ToString())) violations.Add("symbol_entered_sketch:" + item.Tag.ToString());
        evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", violations.Count == 0 ? "VERIFIED" : "MISMATCH" }, { "violations", violations },
            { "before", priorRows }, { "after", afterRows }, { "added_tags", StringListAsObjects(SortedStrings(added)) },
            { "expected_native_thread_symbol_tags", StringListAsObjects(SortedStrings(expected)) },
            { "native_symbol_entities", definition.ContainsKey("thread_symbol_entities") ? definition["thread_symbol_entities"] : null }
        };
        return violations.Count == 0;
    }

    private static Dictionary<string, object> F14CurveRecord(M3SketchProjectionCurveState state)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "tag", state.Tag }, { "id", state.Id }, { "type", state.Type }, { "native_geometry_record", state.Signature }
        };
    }

    private static F14ThreadStandardResult F14ReadThreadStandard(Session session, M3HoleRequest request)
    {
        try
        {
            string directory = session.GetEnvironmentVariableValue("UGII_THREADED_HOLE_STANDARD_DIR");
            return F14ThreadStandard.Read(directory, request.ThreadStandard, request.ThreadSize,
                request.RadialEngage, request.PitchMm);
        }
        catch (Exception ex)
        {
            throw new M2ModelException("THREAD_STANDARD_UNAVAILABLE", "the effective NX thread standard row could not be uniquely validated (" + ex.GetType().Name + ")");
        }
    }

    private static Dictionary<string, object> F14StandardEvidence(F14ThreadStandardResult row)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "source", "NX effective UGII_THREADED_HOLE_STANDARD_DIR/NX_Thread_Standard.xml" },
            { "file_path", row.FilePath }, { "standard", row.Standard }, { "size", row.Size },
            { "radial_engage", row.RadialEngage }, { "table_unit", row.Unit },
            { "raw_pitch", row.RawPitch }, { "pitch_mm", row.PitchMm },
            { "tap_drill_diameter_mm", row.TapDrillDiameterMm }, { "major_diameter_mm", row.MajorDiameterMm },
            { "minor_diameter_mm", row.MinorDiameterMm }, { "angle_deg", row.AngleDeg },
            { "num_starts", row.NumStarts }, { "tapered", row.Tapered },
            { "evidence_kind", "configuration_table_not_native_feature_readback" }
        };
    }

    private static bool F14StandardEqual(F14ThreadStandardResult a, F14ThreadStandardResult b)
    {
        return a != null && b != null && String.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase) &&
            String.Equals(a.Standard, b.Standard, StringComparison.Ordinal) && String.Equals(a.Size, b.Size, StringComparison.Ordinal) &&
            String.Equals(a.RadialEngage, b.RadialEngage, StringComparison.Ordinal) && String.Equals(a.Unit, b.Unit, StringComparison.Ordinal) &&
            a.RawPitch == b.RawPitch && a.PitchMm == b.PitchMm && a.TapDrillDiameterMm == b.TapDrillDiameterMm &&
            a.MajorDiameterMm == b.MajorDiameterMm && a.MinorDiameterMm == b.MinorDiameterMm && a.AngleDeg == b.AngleDeg &&
            a.NumStarts == b.NumStarts && a.Tapered == b.Tapered;
    }

    private static bool F14DefinitionMatches(M3PlanState plan, Dictionary<string, object> actual,
        out Dictionary<string, object> evidence)
    {
        List<object> failures = new List<object>();
        M3HoleRequest r = plan.HoleRequest;
        F14Require(actual, "kind", r.Kind, failures);
        F14Require(actual, "depth_mode", r.DepthMode, failures);
        F14Require(actual, "native_depth_option", "ToCylinderBottom", failures);
        F14Require(actual, "boolean_type", "Subtract", failures);
        F14Require(actual, "project_point_onto_target", false, failures);
        F14Require(actual, "direction_method", "Vector", failures);
        F14Vector(actual, "position_mm", r.PositionMm, 1e-6, failures);
        F14Vector(actual, "point_coordinates_mm", r.PositionMm, 1e-6, failures);
        F14Vector(actual, "section_source_point_coordinates_mm", r.PositionMm, 1e-6, failures);
        F14Vector(actual, "direction", r.Direction, 1e-8, failures);
        F14Vector(actual, "projection_direction", r.Direction, 1e-8, failures);
        F14Vector(actual, "projection_direction_origin_mm", r.PositionMm, 1e-6, failures);
        F14Require(actual, "section_association_verified", true, failures);
        F14Require(actual, "section_source_point_tag", plan.HoleSourcePointTag, failures);
        string[] targets = actual.ContainsKey("target_body_tags") ? actual["target_body_tags"] as string[] : null;
        if (targets == null || targets.Length != 1 || !String.Equals(targets[0], plan.TargetBody.Tag.ToString(), StringComparison.Ordinal))
            failures.Add("explicit_target_tag");
        F14Number(actual, "tip_angle_deg", 118.0, 1e-8, failures);
        if (r.DepthMode == "blind") F14Number(actual, "hole_depth_mm", r.HoleDepthMm, 1e-6, failures);
        if (r.Kind != "threaded") F14Number(actual, "diameter_mm", r.DiameterMm, 1e-6, failures);
        if (r.Kind == "counterbore")
        {
            F14Number(actual, "counterbore_diameter_mm", r.CounterboreDiameterMm, 1e-6, failures);
            F14Number(actual, "counterbore_depth_mm", r.CounterboreDepthMm, 1e-6, failures);
        }
        if (r.Kind == "countersink")
        {
            F14Number(actual, "countersink_diameter_mm", r.CountersinkDiameterMm, 1e-6, failures);
            F14Number(actual, "countersink_angle_deg", r.CountersinkAngleDeg, 1e-8, failures);
        }
        if (r.Kind == "threaded")
        {
            F14ThreadStandardResult row = plan.HoleThreadStandard;
            if (row == null) failures.Add("thread_standard_table");
            F14Require(actual, "thread_standard", r.ThreadStandard, failures);
            F14Require(actual, "thread_size", r.ThreadSize, failures);
            F14Require(actual, "radial_engage", r.RadialEngage, failures);
            F14Require(actual, "hand", r.Hand, failures);
            F14Require(actual, "thread_type", "Symbolic", failures);
            F14Require(actual, "thread_internal", true, failures);
            F14Require(actual, "thread_length_option", "Custom", failures);
            F14Require(actual, "thread_limit", "Value", failures);
            F14Require(actual, "thread_reverse_direction", false, failures);
            F14Require(actual, "relate_hole_depth_to_thread_depth", false, failures);
            F14Require(actual, "threaded_start_chamfer_enabled", false, failures);
            F14Require(actual, "threaded_end_chamfer_enabled", false, failures);
            F14Require(actual, "threaded_relief_enabled", false, failures);
            F14Require(actual, "relief_chamfer_enabled", false, failures);
            F14Require(actual, "thread_at_both_ends", false, failures);
            F14Require(actual, "package_thread_standard", r.ThreadStandard, failures);
            F14Require(actual, "package_thread_size", r.ThreadSize, failures);
            F14Require(actual, "package_radial_engage", r.RadialEngage, failures);
            F14Require(actual, "package_thread_rotation", r.Hand == "right" ? "Right" : "Left", failures);
            F14Number(actual, "pitch_mm", r.PitchMm, 1e-9, failures);
            F14Number(actual, "pitch_expression_value_mm", r.PitchMm, 1e-9, failures);
            F14Number(actual, "thread_depth_mm", r.ThreadDepthMm, 1e-6, failures);
            F14Number(actual, "package_thread_depth_mm", r.ThreadDepthMm, 1e-6, failures);
            if (row != null)
            {
                F14Number(actual, "tap_drill_diameter_mm", row.TapDrillDiameterMm, 1e-6, failures);
                F14Number(actual, "diameter_mm", row.TapDrillDiameterMm, 1e-6, failures);
                F14Number(actual, "thread_major_diameter_mm", row.MajorDiameterMm, 1e-6, failures);
                F14Number(actual, "thread_minor_diameter_mm", row.MinorDiameterMm, 1e-6, failures);
                F14Number(actual, "thread_angle_deg", row.AngleDeg, 1e-8, failures);
                F14Require(actual, "thread_starts", row.NumStarts, failures);
                F14Require(actual, "thread_tapered", row.Tapered, failures);
            }
            Dictionary<string, object> startPlane = actual.ContainsKey("thread_start_object") ? actual["thread_start_object"] as Dictionary<string, object> : null;
            double[] planeOrigin = startPlane != null && startPlane.ContainsKey("origin_mm") ? startPlane["origin_mm"] as double[] : null;
            double[] planeNormal = startPlane != null && startPlane.ContainsKey("normal") ? startPlane["normal"] as double[] : null;
            if (startPlane == null || !Object.Equals(startPlane["type"], "NXOpen.Plane") || planeOrigin == null || planeOrigin.Length != 3 ||
                planeNormal == null || planeNormal.Length != 3 || !F14PlaneMatches(planeOrigin, planeNormal, r.PositionMm, r.Direction)) failures.Add("thread_start_plane");
        }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", failures.Count == 0 ? "VERIFIED" : "MISMATCH" }, { "failed_fields", failures },
            { "native_definition", actual }, { "thread_geometry", r.Kind == "threaded" ? "symbolic; no physical thread geometry" : null }
        };
        return failures.Count == 0;
    }

    private static void F14Require(Dictionary<string, object> data, string key, object expected, List<object> failures)
    {
        object actual;
        if (data == null || !data.TryGetValue(key, out actual) || !Object.Equals(actual, expected)) failures.Add(key);
    }
    private static bool F14PlaneMatches(double[] origin, double[] normal, double[] expectedPoint, double[] axis)
    {
        double n = Math.Sqrt(normal[0]*normal[0] + normal[1]*normal[1] + normal[2]*normal[2]);
        if (!IsFinite(n) || n <= 1e-12) return false;
        double dot = (normal[0]*axis[0] + normal[1]*axis[1] + normal[2]*axis[2]) / n;
        double offset = ((origin[0]-expectedPoint[0])*normal[0] + (origin[1]-expectedPoint[1])*normal[1] + (origin[2]-expectedPoint[2])*normal[2]) / n;
        return IsFinite(dot) && IsFinite(offset) && Math.Abs(dot) >= 1.0-1e-9 && Math.Abs(offset) <= 1e-6;
    }
    private static bool F14ThreadWallAssociationMatches(M3PlanState plan, Dictionary<string, object> definition,
        Dictionary<string, object> coverage)
    {
        if (plan.HoleRequest.Kind != "threaded") return true;
        Dictionary<string, object> wall = coverage != null && coverage.ContainsKey("main_wall") ? coverage["main_wall"] as Dictionary<string, object> : null;
        Dictionary<string, object> selected = definition != null && definition.ContainsKey("thread_cylindrical_face") ? definition["thread_cylindrical_face"] as Dictionary<string, object> : null;
        return wall != null && selected != null && wall.ContainsKey("face_tag") && selected.ContainsKey("tag") && selected.ContainsKey("body_tag") &&
            Object.Equals(wall["face_tag"], selected["tag"]) && Object.Equals(selected["body_tag"], plan.TargetBody.Tag.ToString());
    }
    private static bool F14NativeIdentitiesMatch(Dictionary<string, object> before, Dictionary<string, object> after, bool threaded)
    {
        if (before == null || after == null) return false;
        foreach (string key in new string[] { "feature_tag", "point_tag", "section_source_point_tag", "direction_tag" })
            if (!before.ContainsKey(key) || !after.ContainsKey(key) || !Object.Equals(before[key], after[key])) return false;
        Dictionary<string, object> priorHole = before.ContainsKey("hole_ownership") ? before["hole_ownership"] as Dictionary<string, object> : null;
        Dictionary<string, object> currentHole = after.ContainsKey("hole_ownership") ? after["hole_ownership"] as Dictionary<string, object> : null;
        if (priorHole == null || currentHole == null || !priorHole.ContainsKey("tag") || !currentHole.ContainsKey("tag") ||
            !Object.Equals(priorHole["tag"], currentHole["tag"])) return false;
        return !threaded || (before.ContainsKey("thread_tag") && after.ContainsKey("thread_tag") && Object.Equals(before["thread_tag"], after["thread_tag"]));
    }
    private static void F14Number(Dictionary<string, object> data, string key, double expected, double tolerance, List<object> failures)
    {
        object raw; double actual;
        if (data == null || !data.TryGetValue(key, out raw) || !TryFiniteNumber(raw, out actual) || Math.Abs(actual - expected) > tolerance)
            failures.Add(key);
    }
    private static void F14Vector(Dictionary<string, object> data, string key, double[] expected, double tolerance, List<object> failures)
    {
        object raw; double[] actual;
        if (data == null || !data.TryGetValue(key, out raw) || (actual = raw as double[]) == null || actual.Length != 3 ||
            !IsFinite(actual[0]) || !IsFinite(actual[1]) || !IsFinite(actual[2]) ||
            Math.Abs(actual[0] - expected[0]) > tolerance || Math.Abs(actual[1] - expected[1]) > tolerance || Math.Abs(actual[2] - expected[2]) > tolerance)
            failures.Add(key);
    }

    private static bool F14ModelPreserved(M3PlanState plan, Dictionary<string, object> after,
        Dictionary<string, object> definition, out Dictionary<string, object> evidence)
    {
        Dictionary<string, object> before = plan.F12SemanticBefore;
        List<object> errors = new List<object>();
        string[] unchanged = new string[] { "sketches", "native_helical_definitions" };
        foreach (string key in unchanged) if (!ExactProjection(before, after, key, null)) errors.Add(key);
        if (!ExactProjection(before, after, "part", new string[] { "is_modified" })) errors.Add("part");
        if (!ExactProjection(before, after, "identity", new string[] { "is_modified", "support_summary" })) errors.Add("identity");
        if (!ExactSessionParts(before, after) || !PreserveExistingCurveIds(before, after)) errors.Add("session_or_curves");

        HashSet<string> owned = new HashSet<string>(StringComparer.Ordinal);
        owned.Add(plan.FeatureId);
        Dictionary<string, object> hole = definition.ContainsKey("hole_ownership") ? definition["hole_ownership"] as Dictionary<string, object> : null;
        Dictionary<string, object> thread = definition.ContainsKey("thread_ownership") ? definition["thread_ownership"] as Dictionary<string, object> : null;
        string holeId = hole != null && hole.ContainsKey("id") ? hole["id"] as string : null;
        string threadId = thread != null && thread.ContainsKey("id") ? thread["id"] as string : null;
        if (String.IsNullOrEmpty(holeId) || !owned.Add(holeId)) errors.Add("internal_hole_identity");
        if (plan.HoleRequest.Kind == "threaded" && (String.IsNullOrEmpty(threadId) || !owned.Add(threadId))) errors.Add("thread_identity");
        if (plan.HoleRequest.Kind != "threaded" && thread != null) errors.Add("unexpected_thread");
        if (!F14SupportPreserved(before, after, owned)) errors.Add("support");

        List<object> oldFeatures = SnapshotList(before, "features"), newFeatures = SnapshotList(after, "features");
        HashSet<string> targetParents = F12AllowedParentFeatureIds(before, plan);
        HashSet<string> observedNew = new HashSet<string>(StringComparer.Ordinal);
        foreach (object value in newFeatures)
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            string id = record == null || !record.ContainsKey("journal_id") ? null : record["journal_id"] as string;
            if (String.IsNullOrEmpty(id)) { errors.Add("feature_identity"); continue; }
            if (!ContainsFeatureId(oldFeatures, id))
            {
                if (!observedNew.Add(id) || !owned.Contains(id)) errors.Add("unexpected_new_feature:" + id);
                foreach (string parent in ReadStringList(record, "parent_ids"))
                    if (!owned.Contains(parent) && !targetParents.Contains(parent)) errors.Add("unexpected_parent:" + id);
            }
        }
        if (!SetEquals(observedNew, owned)) errors.Add("new_feature_set");
        foreach (object value in oldFeatures)
        {
            Dictionary<string, object> prior = value as Dictionary<string, object>;
            string id = prior == null || !prior.ContainsKey("journal_id") ? null : prior["journal_id"] as string;
            Dictionary<string, object> current = FindSnapshotFeature(after, id);
            if (current == null) { errors.Add("missing_existing_feature:" + id); continue; }
            bool owner = targetParents.Contains(id);
            string[] omitted = owner ? new string[] { "child_ids", "body_ids" } : new string[] { "child_ids" };
            if (!SemanticValuesEqual(OmitFields(prior, omitted), OmitFields(current, omitted), "feature")) errors.Add("existing_feature_changed:" + id);
            HashSet<string> oldChildren = new HashSet<string>(ReadStringList(prior, "child_ids"), StringComparer.Ordinal);
            HashSet<string> currentChildren = new HashSet<string>(ReadStringList(current, "child_ids"), StringComparer.Ordinal);
            foreach (string child in oldChildren) if (!currentChildren.Contains(child)) errors.Add("existing_child_removed:" + id);
            foreach (string child in currentChildren)
            {
                if (oldChildren.Contains(child)) continue;
                Dictionary<string, object> added = FindSnapshotFeature(after, child);
                if (!owner || !owned.Contains(child) || added == null || !ReadStringList(added, "parent_ids").Contains(id))
                    errors.Add("unexpected_child:" + id);
            }
            if (owner)
            {
                HashSet<string> oldBodies = new HashSet<string>(ReadStringList(prior, "body_ids"), StringComparer.Ordinal);
                HashSet<string> currentBodies = new HashSet<string>(ReadStringList(current, "body_ids"), StringComparer.Ordinal);
                foreach (string body in oldBodies) if (body != plan.TargetBodyId && !currentBodies.Contains(body)) errors.Add("other_body_removed:" + id);
                foreach (string body in currentBodies) if (!oldBodies.Contains(body) && !plan.AffectedBodyIds.Contains(body)) errors.Add("unexpected_body:" + id);
            }
        }

        Dictionary<string, object> newExpressionRows = new Dictionary<string, object>(StringComparer.Ordinal);
        HashSet<string> oldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (object value in SnapshotList(before, "expressions"))
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            if (row == null || !(row["name"] is string) || !oldNames.Add((string)row["name"])) errors.Add("old_expression_identity");
        }
        foreach (object value in SnapshotList(after, "expressions"))
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            string name = row == null || !row.ContainsKey("name") ? null : row["name"] as string;
            if (String.IsNullOrEmpty(name)) { errors.Add("expression_identity"); continue; }
            if (!oldNames.Contains(name)) newExpressionRows[name] = row;
        }
        List<Dictionary<string, object>> nativeExpressions = definition.ContainsKey("feature_expressions")
            ? definition["feature_expressions"] as List<Dictionary<string, object>> : null;
        HashSet<string> provedNames = new HashSet<string>(StringComparer.Ordinal);
        if (nativeExpressions == null) errors.Add("native_expression_evidence");
        else foreach (Dictionary<string, object> row in nativeExpressions)
        {
            string name = row.ContainsKey("name") ? row["name"] as string : null;
            if (String.IsNullOrEmpty(name) || !newExpressionRows.ContainsKey(name)) continue;
            string[] users = row.ContainsKey("user_feature_ids") ? row["user_feature_ids"] as string[] : null;
            string owner = row.ContainsKey("owner_feature_id") ? row["owner_feature_id"] as string : null;
            if (users == null || users.Length != 1 || users[0] != plan.FeatureId || (owner != null && !owned.Contains(owner)))
                errors.Add("expression_ownership:" + name);
            else provedNames.Add(name);
        }
        if (!SetEquals(provedNames, new HashSet<string>(newExpressionRows.Keys, StringComparer.Ordinal))) errors.Add("new_expression_set");
        List<object> expectedExpressions = new List<object>();
        foreach (string name in provedNames) expectedExpressions.Add(newExpressionRows[name]);
        bool expressionPreserved; List<object> additions;
        Dictionary<string, object> expressionsEvidence = PreserveExpressionRecords(before, after, expectedExpressions, out expressionPreserved, out additions);
        if (!expressionPreserved) errors.Add("existing_expressions");

        List<object> oldDependencies = SnapshotList(before, "dependencies"), currentDependencies = SnapshotList(after, "dependencies");
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (object edge in currentDependencies) { string k = M2Json.CanonicalText(edge); int n; counts.TryGetValue(k, out n); counts[k] = n + 1; }
        foreach (object edge in oldDependencies) { string k = M2Json.CanonicalText(edge); int n; if (!counts.TryGetValue(k, out n) || n == 0) errors.Add("existing_dependency_removed"); else counts[k] = n - 1; }
        foreach (object value in currentDependencies)
        {
            string key = M2Json.CanonicalText(value); int n = counts[key]; if (n <= 0) continue; counts[key] = n - 1;
            Dictionary<string, object> edge = value as Dictionary<string, object>;
            string kind = edge == null ? null : edge["kind"] as string, source = edge == null ? null : edge["source_id"] as string, target = edge == null ? null : edge["target_id"] as string;
            if (kind == "FEATURE_PARENT" && owned.Contains(target) && (owned.Contains(source) || targetParents.Contains(source))) continue;
            if (kind == "FEATURE_EXPRESSION" && owned.Contains(source) && provedNames.Contains(target)) continue;
            errors.Add("unexpected_dependency");
        }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", errors.Count == 0 ? "VERIFIED" : "MISMATCH" }, { "errors", errors },
            { "owned_feature_ids", StringListAsObjects(SortedStrings(owned)) },
            { "owned_expression_names", StringListAsObjects(SortedStrings(provedNames)) },
            { "expression_preservation", expressionsEvidence }
        };
        return errors.Count == 0;
    }

    private static bool F14SupportPreserved(Dictionary<string, object> before, Dictionary<string, object> after,
        HashSet<string> owned)
    {
        Dictionary<string, object> a = before["support"] as Dictionary<string, object>;
        Dictionary<string, object> b = after["support"] as Dictionary<string, object>;
        Dictionary<string, object> sa = before["support_summary"] as Dictionary<string, object>;
        Dictionary<string, object> sb = after["support_summary"] as Dictionary<string, object>;
        if (a == null || b == null || sa == null || sb == null || owned == null) return false;
        List<object> oldRows = a["unsupported"] as List<object>, newRows = b["unsupported"] as List<object>;
        if (oldRows == null || newRows == null) return false;
        List<object> retained = new List<object>(); HashSet<string> removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (object value in newRows)
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            string id = row != null && row.ContainsKey("feature_id") ? row["feature_id"] as string : null;
            if (id != null && owned.Contains(id))
            {
                Dictionary<string, object> nativeFeature = FindSnapshotFeature(after, id);
                if (!removed.Add(id) || nativeFeature == null || !row.ContainsKey("feature_type") || !nativeFeature.ContainsKey("feature_type") ||
                    !Object.Equals(row["feature_type"], nativeFeature["feature_type"]) || !row.ContainsKey("code") ||
                    !String.Equals(row["code"] as string, "FEATURE_PARAMETERS_UNSUPPORTED", StringComparison.Ordinal)) return false;
            }
            else retained.Add(value);
        }
        bool rowsEqual = String.Equals(M2Json.CanonicalText(oldRows), M2Json.CanonicalText(retained), StringComparison.Ordinal);
        string expectedBefore = oldRows.Count == 0 ? "SUPPORTED" : "PARTIALLY_SUPPORTED";
        string expectedAfter = newRows.Count == 0 ? "SUPPORTED" : "PARTIALLY_SUPPORTED";
        if (!String.Equals(a["status"] as string, expectedBefore, StringComparison.Ordinal) ||
            !String.Equals(b["status"] as string, expectedAfter, StringComparison.Ordinal)) return false;
        if (Convert.ToInt32(sa["unsupported_feature_parameter_count"]) != oldRows.Count ||
            Convert.ToInt32(sb["unsupported_feature_parameter_count"]) != newRows.Count ||
            Convert.ToInt32(sb["history_feature_count"]) - Convert.ToInt32(sa["history_feature_count"]) != owned.Count) return false;
        return rowsEqual && SemanticValuesEqual(OmitFields(a, new string[] { "status", "unsupported" }), OmitFields(b, new string[] { "status", "unsupported" }), "support") &&
            SemanticValuesEqual(OmitFields(sa, new string[] { "history_feature_count", "unsupported_feature_parameter_count" }),
                OmitFields(sb, new string[] { "history_feature_count", "unsupported_feature_parameter_count" }), "support_summary");
    }
}
