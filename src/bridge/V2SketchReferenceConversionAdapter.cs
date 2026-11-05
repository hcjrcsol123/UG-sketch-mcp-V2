using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Annotations;
using NXOpen.UF;
using NxSketchMcp.M2;

/// <summary>Optional F11 adapter for explicit Sketch curve/dimension reference conversion.</summary>
public sealed partial class M2OpenModelAdapter : IM3SketchReferenceConversionOperationAdapter
{
    private static readonly string[] M3SketchReferenceConversionCheckNames = new string[]
    {
        "reference_conversion_targets", "sketch_geometry", "sketch_constraints", "sketch_frames",
        "source_associations", "non_target_geometry", "history", "bodies", "sketch_solver_status",
        "profile_candidates", "dirty_state"
    };

    private sealed class M3SketchReferenceConversionObjectState
    {
        internal NXObject NativeObject;
        internal Curve Curve;
        internal SketchDimensionalConstraint Constraint;
        internal Dimension AssociatedDimension;
        internal Expression AssociatedExpression;
        internal string Id;
        internal string Kind;
        internal string Tag;
        internal string ConstraintType;
        internal string NativeInputTag;
        internal string AssociatedDimensionTag;
        internal string AssociatedDimensionId;
        internal string ExpressionTag;
        internal string ExpressionId;
        internal string ExpressionName;
        internal string ExpressionUnit;
        internal string ExpressionRightHandSideBefore;
        internal string ExpressionRightHandSideAfter;
        internal double? ExpressionValueBefore;
        internal double? ExpressionValueAfter;
        internal string StateBefore;
        internal string ExpectedAfter;
        internal bool WillChange;
        internal double? ComputedSizeBefore;
        internal double? ComputedSizeAfter;
    }

    private sealed class M3SketchReferenceConversionLiveState
    {
        internal SnapshotResult Capture;
        internal string Semantic;
        internal string SemanticNormalized;
        internal string FullGeometry;
        internal Dictionary<string, CornerGeometrySnapshot> Geometry;
        internal Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> Constraints;
        internal Dictionary<string, string> Frames;
        internal Dictionary<string, int> ReferenceStates;
        internal string DimensionAssociations;
        internal string SourceAssociations;
        internal List<M3BodyMeasure> Bodies;
        internal List<string> FeatureTags;
        internal Dictionary<string, M3SketchProjectionFeatureState> FeatureStates;
        internal string FeatureInventory;
        internal string FeatureInventoryNormalized;
        internal string History;
        internal string HistoryNormalized;
        internal string Expressions;
        internal string ExpressionsNormalized;
        internal Dictionary<string, M3SketchProjectionCurveState> CurveInventory;
        internal string CurveInventoryText;
        internal Dictionary<string, List<string>> HelperTags;
        internal bool Dirty;
        internal string ActiveSketchTag;
        internal Dictionary<string, object> Solver;
        internal List<object> ContourCandidates;
        internal string ContourCandidatesText;
    }

    private sealed class M3SketchReferenceConversionPlanState
    {
        internal M2ModelBinding Binding;
        internal BindingState BindingState;
        internal string RevisionId;
        internal M3SketchReferenceConversionRequest Request;
        internal Sketch Sketch;
        internal string SketchId;
        internal string SketchTag;
        internal string FeatureId;
        internal string FeatureTag;
        internal string TargetState;
        internal string ActiveSketchTagBefore;
        internal List<M3SketchReferenceConversionObjectState> Objects;
        internal NXObject[] ChangedNativeInputs;
        internal string[] ChangedNativeInputTags;
        internal string SemanticBefore;
        internal string SemanticBeforeNormalized;
        internal Dictionary<string, object> SemanticSnapshotBefore;
        internal string FullGeometryBefore;
        internal Dictionary<string, CornerGeometrySnapshot> GeometryBefore;
        internal Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> ConstraintsBefore;
        internal Dictionary<string, string> FramesBefore;
        internal Dictionary<string, int> ReferenceStatesBefore;
        internal string DimensionAssociationsBefore;
        internal string SourceAssociationsBefore;
        internal List<M3BodyMeasure> BodiesBefore;
        internal List<string> FeatureTagsBefore;
        internal Dictionary<string, M3SketchProjectionFeatureState> FeatureStatesBefore;
        internal string FeatureInventoryBefore;
        internal string FeatureInventoryBeforeNormalized;
        internal string HistoryBefore;
        internal string HistoryBeforeNormalized;
        internal string ExpressionsBefore;
        internal string ExpressionsBeforeNormalized;
        internal Dictionary<string, M3SketchProjectionCurveState> CurveInventoryBefore;
        internal string CurveInventoryBeforeText;
        internal Dictionary<string, List<string>> HelperTagsBefore;
        internal bool DirtyBefore;
        internal Dictionary<string, object> SolverBeforePrepare;
        internal List<object> ContourCandidatesBefore;
        internal string ContourCandidatesBeforeText;
        internal Dictionary<string, object> SolverBefore;
        internal Dictionary<string, object> SolverCalculatedAfter;
        internal Dictionary<string, object> SolverAfter;
        internal Dictionary<string, object> WorkRegionBefore;
        internal Dictionary<string, object> WorkRegionAfter;
        internal bool WorkRegionScopeCommitAttempted;
        internal bool WorkRegionScopeCommitSucceeded;
        internal bool DirtyAfter;
        internal bool DirtyAfterCaptured;
        internal string SemanticAfter;
        internal string SemanticAfterNormalized;
        internal string FullGeometryAfter;
        internal Dictionary<string, CornerGeometrySnapshot> GeometryAfter;
        internal Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> ConstraintsAfter;
        internal Dictionary<string, string> FramesAfter;
        internal Dictionary<string, int> ReferenceStatesAfter;
        internal string DimensionAssociationsAfter;
        internal string SourceAssociationsAfter;
        internal List<M3BodyMeasure> BodiesAfter;
        internal List<string> FeatureTagsAfter;
        internal Dictionary<string, M3SketchProjectionFeatureState> FeatureStatesAfter;
        internal string FeatureInventoryAfter;
        internal string FeatureInventoryAfterNormalized;
        internal string HistoryAfter;
        internal string HistoryAfterNormalized;
        internal string ExpressionsAfter;
        internal string ExpressionsAfterNormalized;
        internal Dictionary<string, M3SketchProjectionCurveState> CurveInventoryAfter;
        internal string CurveInventoryAfterText;
        internal Dictionary<string, List<string>> HelperTagsAfter;
        internal List<object> ContourCandidatesAfter;
        internal string ContourCandidatesAfterText;
        internal Dictionary<string, object> NativeOperationEvidence;
        internal Dictionary<string, object> AppliedEvidence;
        internal M3ExecutionResult Execution;

        // Private reflection-only fault seam for native acceptance. Default false.
        internal bool ForcePostCommitVerificationFailureForTesting = false;
    }

    public M3PlanSnapshot PrepareSketchReferenceConversion(M2ModelBinding binding,
        M3SketchReferenceConversionRequest request)
    {
        ValidateM3SketchReferenceConversionRequest(request);
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "a Sketch is active in NX; reference conversion preparation is paused");

        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            throw new M2ModelException("STALE_BINDING", bindingReason ?? "NX model binding is no longer current");
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current part revision");

        SnapshotResult captured = Capture(binding, state);
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id changed while resolving reference conversion objects");

        Sketch sketch = FindSketch(state.WorkPart, request.SketchId);
        if (!M3SketchProjectionNativeSketchFeature(state.WorkPart, sketch))
            throw new M2ModelException("UNSUPPORTED_SKETCH_HISTORY",
                "F11 target must be a native Sketch feature owned by the bound Work Part");
        string sketchId = RequiredJournalId(sketch, "reference conversion Sketch");
        if (!String.Equals(sketchId, request.SketchId, StringComparison.Ordinal))
            throw new M2ModelException("SKETCH_TARGET_MISMATCH", "resolved Sketch JournalIdentifier differs from sketch_id");

        Dictionary<string, int> referenceStates = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
        List<M3SketchReferenceConversionObjectState> objects = ResolveM3SketchReferenceConversionObjects(
            state.WorkPart, sketch, request.ObjectIds, request.TargetState, referenceStates);
        M3SketchReferenceConversionLiveState live = CaptureM3SketchReferenceConversionLiveState(
            binding, state, sketch, captured, objects);
        int changedCount = 0;
        List<NXObject> changedInputs = new List<NXObject>();
        List<string> changedInputTags = new List<string>();
        foreach (M3SketchReferenceConversionObjectState item in objects)
        {
            if (!item.WillChange) continue;
            changedCount++;
            changedInputs.Add(item.NativeObject);
            changedInputTags.Add(item.NativeInputTag);
        }
        if (changedCount == 0)
            throw new M2ModelException("INVALID_ARGUMENT", "all selected objects already have the requested reference state");

        Point3d sketchOrigin;
        Vector3d sketchNormal, sketchXAxis, sketchYAxis;
        Dictionary<string, object> sketchFrame = M3SketchProjectionFrame(sketch,
            out sketchOrigin, out sketchNormal, out sketchXAxis, out sketchYAxis);
        M3SketchReferenceConversionPlanState planState = new M3SketchReferenceConversionPlanState
        {
            Binding = binding,
            BindingState = state,
            RevisionId = binding.RevisionId,
            Request = CloneM3SketchReferenceConversionRequest(request),
            Sketch = sketch,
            SketchId = request.SketchId,
            SketchTag = sketch.Tag.ToString(),
            FeatureId = RequiredJournalId(sketch.Feature, "reference conversion Sketch feature"),
            FeatureTag = sketch.Feature.Tag.ToString(),
            TargetState = request.TargetState,
            ActiveSketchTagBefore = live.ActiveSketchTag,
            Objects = objects,
            ChangedNativeInputs = changedInputs.ToArray(),
            ChangedNativeInputTags = changedInputTags.ToArray(),
            SemanticBefore = live.Semantic,
            SemanticBeforeNormalized = M3SketchReferenceConversionSemanticProjection(
                captured.Snapshot, request.SketchId,
                RequiredJournalId(sketch.Feature, "reference conversion Sketch feature"), objects),
            SemanticSnapshotBefore = DeepCloneValue(captured.Snapshot) as Dictionary<string, object>,
            FullGeometryBefore = live.FullGeometry,
            GeometryBefore = live.Geometry,
            ConstraintsBefore = live.Constraints,
            FramesBefore = live.Frames,
            ReferenceStatesBefore = live.ReferenceStates,
            DimensionAssociationsBefore = live.DimensionAssociations,
            SourceAssociationsBefore = live.SourceAssociations,
            BodiesBefore = live.Bodies,
            FeatureTagsBefore = live.FeatureTags,
            FeatureStatesBefore = live.FeatureStates,
            FeatureInventoryBefore = live.FeatureInventory,
            FeatureInventoryBeforeNormalized = live.FeatureInventoryNormalized,
            HistoryBefore = live.History,
            HistoryBeforeNormalized = live.HistoryNormalized,
            ExpressionsBefore = live.Expressions,
            ExpressionsBeforeNormalized = M3SketchReferenceConversionExpressionProjection(
                captured.Snapshot, objects),
            CurveInventoryBefore = live.CurveInventory,
            CurveInventoryBeforeText = live.CurveInventoryText,
            HelperTagsBefore = live.HelperTags,
            DirtyBefore = live.Dirty,
            SolverBeforePrepare = live.Solver,
            ContourCandidatesBefore = live.ContourCandidates,
            ContourCandidatesBeforeText = live.ContourCandidatesText,
            NativeOperationEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
        };

        Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal);
        publicView["kind"] = "edit_sketch_reference_conversion";
        publicView["revision_id"] = binding.RevisionId;
        publicView["sketch_id"] = request.SketchId;
        publicView["sketch_tag"] = planState.SketchTag;
        publicView["target_feature_id"] = planState.FeatureId;
        publicView["target_state"] = request.TargetState;
        publicView["objects"] = M3SketchReferenceConversionObjectRecords(planState, null, false);
        publicView["changed_count"] = changedCount;
        publicView["already_target_count"] = objects.Count - changedCount;
        publicView["contour_candidates_before"] = live.ContourCandidates;
        publicView["contour_candidate_basis"] = "UF reference status Active only; this is not NX Section/profile validation";
        publicView["contour_validation"] = "NOT_EVALUATED";
        publicView["solver_prepare"] = live.Solver;
        publicView["target_sketch_frame"] = sketchFrame;
        publicView["baseline_geometry_count"] = live.Geometry.Count;
        publicView["baseline_constraint_count"] = M3SketchReferenceConversionConstraintCount(live.Constraints);
        publicView["baseline_reference_curve_count"] = live.ReferenceStates.Count;
        publicView["baseline_feature_count"] = live.FeatureStates.Count;
        publicView["baseline_body_count"] = live.Bodies.Count;
        publicView["dirty_before"] = live.Dirty;

        return new M3PlanSnapshot
        {
            Kind = "edit_sketch_reference_conversion",
            RevisionId = binding.RevisionId,
            SketchId = request.SketchId,
            FeatureId = planState.FeatureId,
            PublicView = publicView,
            AdapterState = planState
        };
    }

    private M3ExecutionResult ExecuteSketchReferenceConversionPlan(M2ModelBinding binding,
        M3PlanSnapshot prepared)
    {
        M3SketchReferenceConversionPlanState plan = prepared == null
            ? null : prepared.AdapterState as M3SketchReferenceConversionPlanState;
        if (plan == null || prepared.Kind != "edit_sketch_reference_conversion")
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null,
                "INVALID_PLAN", "F11 plan does not contain retained NX adapter state");

        BindingState state;
        try { state = RequireState(binding); }
        catch (M2ModelException ex)
        {
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, ex.Code, ex.Message);
        }
        if (!Object.ReferenceEquals(state, plan.BindingState) || !Object.ReferenceEquals(binding, plan.Binding) ||
            !String.Equals(plan.RevisionId, prepared.RevisionId, StringComparison.Ordinal) ||
            !String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null,
                "STALE_PLAN", "F11 plan belongs to a different binding or revision");
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_BINDING", bindingReason);

        try
        {
            RequireWritableSimplePart(state.WorkPart);
            RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
            if (state.Session.ActiveSketch != null)
                return M3Execution("UNCHANGED", binding.RevisionId, null, "NX_BUSY",
                    "a Sketch is active in NX before F11 execution");
            Sketch currentSketch = FindSketch(state.WorkPart, plan.SketchId);
            if (currentSketch == null || !String.Equals(currentSketch.Tag.ToString(), plan.SketchTag, StringComparison.Ordinal) ||
                currentSketch.Feature == null ||
                !String.Equals(currentSketch.Feature.Tag.ToString(), plan.FeatureTag, StringComparison.Ordinal) ||
                !String.Equals(RequiredJournalId(currentSketch.Feature, "F11 Sketch feature"), plan.FeatureId, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN",
                    "target Sketch or feature identity changed after F11 preparation");

            SnapshotResult currentCapture = Capture(binding, state);
            M3SketchReferenceConversionLiveState current = CaptureM3SketchReferenceConversionLiveState(
                binding, state, currentSketch, currentCapture, plan.Objects);
            Dictionary<string, object> preconditionEvidence;
            if (!M3SketchReferenceConversionMatchesPrepare(plan, current, out preconditionEvidence))
                return M3Execution("UNCHANGED", binding.RevisionId, preconditionEvidence,
                    "STALE_PLAN", "F11 model state changed after reference conversion preparation");
            List<M3SketchReferenceConversionObjectState> currentObjects = ResolveM3SketchReferenceConversionObjects(
                state.WorkPart, currentSketch, plan.Request.ObjectIds, plan.TargetState, current.ReferenceStates);
            if (!M3SketchReferenceConversionObjectIdentitiesMatch(plan.Objects, currentObjects, true))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN",
                    "selected F11 object identity or state changed after preparation");
        }
        catch (M2ModelException ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "MODEL_READ_FAILED",
                "could not revalidate F11 preconditions (" + ex.GetType().Name + ")");
        }

        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F11 sketch reference conversion"); }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "UPDATE_FAILED",
                "could not create an NX F11 undo mark (" + ex.GetType().Name + ")");
        }

        Exception operationException = null;
        bool sketchActivated = false;
        plan.NativeOperationEvidence["native_operation_called"] = false;
        plan.NativeOperationEvidence["select_all_project_feature_curves"] = false;
        plan.NativeOperationEvidence["find_movable_objects_restore_succeeded"] = true;
        try
        {
            plan.Sketch.Activate(Sketch.ViewReorient.False);
            sketchActivated = true;
            if (!M3SketchReferenceConversionIsSketchActive(state.Session, plan.Sketch))
                throw new M2ModelException("SKETCH_ACTIVATION_FAILED", "NX did not activate the explicit F11 target Sketch");

            plan.WorkRegionBefore = ConfigureM3SketchReferenceConversionWorkRegion(state.WorkPart,
                out plan.WorkRegionScopeCommitAttempted, out plan.WorkRegionScopeCommitSucceeded);
            if (!plan.WorkRegionScopeCommitSucceeded || !Boolean.Equals(plan.WorkRegionBefore["unrestricted"], true))
                throw new M2ModelException("WORK_REGION_UNAVAILABLE",
                    "NX did not commit and read back the F11 EntireSketch solver scope");
            plan.NativeOperationEvidence["work_region_before"] = plan.WorkRegionBefore;

            plan.SolverBefore = CaptureM3SketchReferenceConversionCalculatedSolver(
                state.Session, plan.Sketch, plan.NativeOperationEvidence, "solver_before");
            plan.NativeOperationEvidence["solver_before"] = plan.SolverBefore;

            plan.NativeOperationEvidence["native_operation_called"] = true;
            V2SketchReferenceConversionNativeResult native = V2SketchReferenceConversionFactory.Execute(
                state.WorkPart, plan.Sketch, plan.ChangedNativeInputs, plan.TargetState);
            if (native == null || native.Evidence == null)
                throw new M2ModelException("REFERENCE_CONVERSION_OUTPUT_INVALID",
                    "NX reference conversion factory returned no native evidence");
            plan.NativeOperationEvidence["factory"] = native.Evidence;
            plan.NativeOperationEvidence["factory_input_tags_match"] =
                M3SketchReferenceConversionFactoryInputsMatch(plan, native.Evidence);

            plan.Sketch.Update();
            plan.WorkRegionAfter = new Dictionary<string, object>(plan.WorkRegionBefore, StringComparer.Ordinal);
            plan.WorkRegionAfter["solver_evaluation_scope"] = "EntireSketch";
            plan.WorkRegionAfter["calculate_status_called_while_sketch_active"] = true;
            plan.SolverCalculatedAfter = CaptureM3SketchReferenceConversionCalculatedSolver(
                state.Session, plan.Sketch, plan.NativeOperationEvidence, "solver_calculated_after");
            plan.NativeOperationEvidence["solver_calculated_after"] = plan.SolverCalculatedAfter;

            plan.Sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            sketchActivated = false;
            if (state.Session.ActiveSketch != null)
                throw new M2ModelException("SKETCH_DEACTIVATION_FAILED", "NX retained an active Sketch after F11 conversion");
            int updateErrors = state.Session.UpdateManager.DoUpdate(mark);
            plan.NativeOperationEvidence["update_error_count"] = updateErrors;
            if (updateErrors != 0)
                throw new M2ModelException("UPDATE_FAILED", "NX update reported errors after F11 conversion");

            SnapshotResult afterCapture = Capture(binding, state);
            Sketch afterSketch = FindSketch(state.WorkPart, plan.SketchId);
            if (afterSketch == null || !String.Equals(afterSketch.Tag.ToString(), plan.SketchTag, StringComparison.Ordinal))
                throw new M2ModelException("REFERENCE_CONVERSION_TARGET_LOST", "target Sketch could not be resolved after F11 conversion");
            M3SketchReferenceConversionLiveState after = CaptureM3SketchReferenceConversionLiveState(
                binding, state, afterSketch, afterCapture, plan.Objects);
            M3SketchReferenceConversionCaptureAfter(plan, after);
            List<M3SketchReferenceConversionObjectState> objectsAfter = ResolveM3SketchReferenceConversionObjects(
                state.WorkPart, afterSketch, plan.Request.ObjectIds, plan.TargetState, after.ReferenceStates);
            M3SketchReferenceConversionUpdateObjectComputedSizes(plan.Objects, objectsAfter);
            plan.SolverAfter = after.Solver;
            plan.NativeOperationEvidence["solver_after"] = plan.SolverAfter;
            plan.NativeOperationEvidence["work_region_after_update"] = plan.WorkRegionAfter;

            if (plan.ForcePostCommitVerificationFailureForTesting)
                throw new M2ModelException("TEST_FORCED_POSTCOMMIT_VERIFY_FAILURE",
                    "native acceptance requested a post-commit F11 verification fault");

            plan.AppliedEvidence = EvaluateM3SketchReferenceConversion(plan, after,
                objectsAfter, native.Evidence, true);
            plan.NativeOperationEvidence["postcondition_evidence"] = plan.AppliedEvidence;
            if (!M3SketchReferenceConversionChecksPassed(plan.AppliedEvidence))
                throw new M2ModelException("REFERENCE_CONVERSION_VERIFY_FAILED",
                    "NX changed an unrequested F11 state or a required postcondition did not hold");

            Dictionary<string, object> result = BuildM3SketchReferenceConversionResult(plan,
                binding.RevisionId, "APPLIED", plan.AppliedEvidence, objectsAfter);
            try
            {
                state.Session.DeleteUndoMark(mark, "");
                plan.NativeOperationEvidence["undo_mark_closed"] = true;
            }
            catch (Exception closeException)
            {
                plan.NativeOperationEvidence["undo_mark_closed"] = false;
                plan.NativeOperationEvidence["undo_mark_close_exception_type"] = closeException.GetType().FullName;
                plan.NativeOperationEvidence["undo_mark_close_exception_message"] = M3SketchReferenceConversionSafeMessage(closeException);
                throw new M2ModelException("UNDO_MARK_CLOSE_FAILED", "F11 conversion applied but NX could not close its undo mark");
            }
            plan.Execution = M3Execution("APPLIED", binding.RevisionId, result, null, null);
            return plan.Execution;
        }
        catch (Exception ex)
        {
            operationException = ex;
            if (sketchActivated)
            {
                try
                {
                    if (M3SketchReferenceConversionIsSketchActive(state.Session, plan.Sketch))
                        plan.Sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
                    sketchActivated = false;
                }
                catch (Exception deactivateException)
                {
                    plan.NativeOperationEvidence["failure_deactivation_exception_type"] = deactivateException.GetType().FullName;
                    plan.NativeOperationEvidence["failure_deactivation_exception_message"] =
                        M3SketchReferenceConversionSafeMessage(deactivateException);
                }
            }
            plan.NativeOperationEvidence["native_exception_type"] = ex.GetType().FullName;
            plan.NativeOperationEvidence["native_exception_code"] = M3SketchReferenceConversionExceptionCode(ex);
            plan.NativeOperationEvidence["native_exception_message"] = M3SketchReferenceConversionSafeMessage(ex);
            M3ExecutionResult restored = RestoreM3SketchReferenceConversionPlan(binding, state,
                plan, mark, operationException);
            plan.Execution = restored;
            return restored;
        }
    }

    private M3VerificationResult VerifySketchReferenceConversionPlan(M2ModelBinding binding,
        M3PlanSnapshot prepared)
    {
        M3SketchReferenceConversionPlanState plan = prepared == null
            ? null : prepared.AdapterState as M3SketchReferenceConversionPlanState;
        if (plan == null || prepared.Kind != "edit_sketch_reference_conversion" ||
            plan.Execution == null || plan.Execution.State != "APPLIED")
            return M3VerificationFailure(binding == null ? null : binding.RevisionId,
                "F11 verification requires a successfully applied reference conversion plan");
        BindingState state;
        try { state = RequireState(binding); }
        catch (M2ModelException ex)
        {
            return M3VerificationFailure(binding == null ? null : binding.RevisionId, ex.Message);
        }
        string bindingReason;
        if (!Object.ReferenceEquals(state, plan.BindingState) || !Object.ReferenceEquals(binding, plan.Binding) ||
            !IsBindingCurrent(binding, out bindingReason) ||
            !String.Equals(binding.RevisionId, plan.Execution.RevisionId, StringComparison.Ordinal))
            return M3VerificationFailure(binding == null ? null : binding.RevisionId,
                "NX binding changed before F11 verification");

        Dictionary<string, object> measured = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        bool verified = false;
        List<M3SketchReferenceConversionObjectState> verifiedObjectsAfter = null;
        try
        {
            Sketch sketch = FindSketch(state.WorkPart, plan.SketchId);
            if (sketch == null || !String.Equals(sketch.Tag.ToString(), plan.SketchTag, StringComparison.Ordinal))
                throw new M2ModelException("SKETCH_NOT_FOUND", "F11 target Sketch is unavailable during verification");
            SnapshotResult captured = Capture(binding, state);
            M3SketchReferenceConversionLiveState current = CaptureM3SketchReferenceConversionLiveState(
                binding, state, sketch, captured, plan.Objects);
            verifiedObjectsAfter = ResolveM3SketchReferenceConversionObjects(
                state.WorkPart, sketch, plan.Request.ObjectIds, plan.TargetState, current.ReferenceStates);
            M3SketchReferenceConversionUpdateObjectComputedSizes(plan.Objects, verifiedObjectsAfter);
            Dictionary<string, object> evidence = EvaluateM3SketchReferenceConversion(plan,
                current, verifiedObjectsAfter, plan.NativeOperationEvidence.ContainsKey("factory")
                    ? plan.NativeOperationEvidence["factory"] as Dictionary<string, object> : null, false);
            measured["execute_evidence"] = plan.AppliedEvidence;
            measured["live_evidence"] = evidence;
            Dictionary<string, object> liveChecks = evidence["checks"] as Dictionary<string, object>;
            if (liveChecks != null)
                foreach (KeyValuePair<string, object> item in liveChecks) checks[item.Key] = item.Value;
            verified = M3SketchReferenceConversionChecksPassed(evidence);
        }
        catch (Exception ex)
        {
            measured["verification_exception_type"] = ex.GetType().FullName;
            measured["verification_exception_message"] = M3SketchReferenceConversionSafeMessage(ex);
            foreach (string name in M3SketchReferenceConversionCheckNames)
                if (!checks.ContainsKey(name)) checks[name] = VerificationCheck("MISMATCH", null);
        }

        Dictionary<string, object> result = BuildM3SketchReferenceConversionResult(plan,
            binding.RevisionId, plan.Execution.State, measured,
            verifiedObjectsAfter ?? new List<M3SketchReferenceConversionObjectState>());
        return new M3VerificationResult
        {
            Verified = verified,
            RevisionId = binding.RevisionId,
            Checks = checks,
            Result = result,
            ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified ? null : "Live F11 target states or preserved Sketch state did not match the applied conversion."
        };
    }

    private M3ExecutionResult RestoreM3SketchReferenceConversionPlan(M2ModelBinding binding,
        BindingState state, M3SketchReferenceConversionPlanState plan, Session.UndoMarkId mark,
        Exception operationException)
    {
        Dictionary<string, object> restore = new Dictionary<string, object>(StringComparer.Ordinal);
        restore["native_operation_called"] = Boolean.Equals(plan.NativeOperationEvidence["native_operation_called"], true);
        restore["exception_type"] = operationException == null ? null : operationException.GetType().FullName;
        restore["exception_code"] = M3SketchReferenceConversionExceptionCode(operationException);
        restore["exception_message"] = M3SketchReferenceConversionSafeMessage(operationException);
        restore["active_sketch_tag_before"] = plan.ActiveSketchTagBefore;
        restore["undo_to_mark_attempted"] = true;
        restore["work_region_scope_commit_attempted"] = plan.WorkRegionScopeCommitAttempted;
        restore["work_region_scope_commit_succeeded"] = plan.WorkRegionScopeCommitSucceeded;
        restore["work_region_scope_restore"] = "NOT_INDEPENDENTLY_OBSERVABLE";
        restore["restoration_scope"] = "captured_model_state_excluding_inactive_work_region_ui_scope";
        object preferenceRestoreValue;
        bool sessionPreferenceRestored = plan.NativeOperationEvidence.TryGetValue(
            "find_movable_objects_restore_succeeded", out preferenceRestoreValue) &&
            preferenceRestoreValue is bool && (bool)preferenceRestoreValue;
        restore["find_movable_objects_restore_succeeded"] = sessionPreferenceRestored;
        bool undoSucceeded = false;
        try
        {
            Sketch active = state.Session.ActiveSketch;
            if (active != null && String.Equals(active.Tag.ToString(), plan.SketchTag, StringComparison.Ordinal))
                active.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            if (state.Session.ActiveSketch != null)
                throw new InvalidOperationException("NX retained an active Sketch before F11 Undo");
            state.Session.UndoToMark(mark, null);
            undoSucceeded = true;
            restore["undo_to_mark_succeeded"] = true;
        }
        catch (Exception undoException)
        {
            restore["undo_to_mark_succeeded"] = false;
            restore["undo_exception_type"] = undoException.GetType().FullName;
            restore["undo_exception_message"] = M3SketchReferenceConversionSafeMessage(undoException);
        }

        bool updateSucceeded = false;
        if (undoSucceeded)
        {
            try
            {
                int errors = state.Session.UpdateManager.DoUpdate(mark);
                restore["undo_update_error_count"] = errors;
                updateSucceeded = errors == 0;
            }
            catch (Exception updateException)
            {
                restore["undo_update_exception_type"] = updateException.GetType().FullName;
                restore["undo_update_exception_message"] = M3SketchReferenceConversionSafeMessage(updateException);
            }
        }
        restore["undo_update_succeeded"] = updateSucceeded;

        bool restored = false;
        if (undoSucceeded && updateSucceeded)
        {
            try
            {
                Sketch sketch = FindSketch(state.WorkPart, plan.SketchId);
                SnapshotResult capture = Capture(binding, state);
                M3SketchReferenceConversionLiveState live = CaptureM3SketchReferenceConversionLiveState(
                    binding, state, sketch, capture, plan.Objects);
                List<M3SketchReferenceConversionObjectState> objects = ResolveM3SketchReferenceConversionObjects(
                    state.WorkPart, sketch, plan.Request.ObjectIds, plan.TargetState, live.ReferenceStates);
                Dictionary<string, object> bodyEvidence;
                bool bodies = BodyMeasuresEqual(plan.BodiesBefore, live.Bodies, out bodyEvidence);
                string rawSemantic = CanonicalSemanticSnapshot(capture.Snapshot);
                bool active = String.Equals(plan.ActiveSketchTagBefore, live.ActiveSketchTag, StringComparison.Ordinal);
                bool semantic = String.Equals(plan.SemanticBefore, rawSemantic, StringComparison.Ordinal);
                bool fullGeometry = String.Equals(plan.FullGeometryBefore, live.FullGeometry, StringComparison.Ordinal);
                bool geometry = CornerGeometryMapsEqual(plan.GeometryBefore, live.Geometry);
                bool constraints = M3SketchReferenceConversionConstraintMapsEqual(plan.ConstraintsBefore,
                    live.Constraints, null, null);
                bool frames = OffsetStringMapsEqual(plan.FramesBefore, live.Frames);
                bool references = OffsetIntMapsEqual(plan.ReferenceStatesBefore, live.ReferenceStates);
                bool dimensionAssociations = String.Equals(plan.DimensionAssociationsBefore,
                    live.DimensionAssociations, StringComparison.Ordinal);
                bool sourceAssociations = String.Equals(plan.SourceAssociationsBefore,
                    live.SourceAssociations, StringComparison.Ordinal);
                bool history = String.Equals(plan.HistoryBefore, live.History, StringComparison.Ordinal);
                bool features = String.Equals(plan.FeatureInventoryBefore, live.FeatureInventory, StringComparison.Ordinal) &&
                    FeatureTagsMatch(plan.FeatureTagsBefore, live.FeatureTags);
                bool expressions = String.Equals(plan.ExpressionsBefore, live.Expressions, StringComparison.Ordinal);
                bool curves = String.Equals(plan.CurveInventoryBeforeText, live.CurveInventoryText, StringComparison.Ordinal);
                bool helpers = PatternHelperTagInventoriesEqual(plan.HelperTagsBefore, live.HelperTags);
                bool dirty = plan.DirtyBefore == live.Dirty;
                bool solver = M3SketchReferenceConversionSolverSnapshotsEqual(plan.SolverBeforePrepare, live.Solver);
                bool objectsRestored = M3SketchReferenceConversionObjectsAtPrepareState(plan.Objects, objects);
                restore["active_sketch_tag_after"] = live.ActiveSketchTag;
                restore["active_sketch_restored"] = active;
                restore["semantic_state_restored"] = semantic;
                restore["full_geometry_restored"] = fullGeometry;
                restore["sketch_geometry_restored"] = geometry;
                restore["constraints_restored"] = constraints;
                restore["dimension_associations_restored"] = dimensionAssociations;
                restore["frames_restored"] = frames;
                restore["reference_status_restored"] = references;
                restore["source_associations_restored"] = sourceAssociations;
                restore["history_restored"] = history;
                restore["feature_inventory_restored"] = features;
                restore["expressions_restored"] = expressions;
                restore["bodies_restored"] = bodies;
                restore["body_measurements"] = bodyEvidence;
                restore["global_curve_inventory_restored"] = curves;
                restore["helper_inventory_restored"] = helpers;
                restore["dirty_state_restored"] = dirty;
                restore["solver_status_restored"] = solver;
                restore["selected_object_states_restored"] = objectsRestored;
                restored = active && semantic && fullGeometry && geometry && constraints &&
                    dimensionAssociations && frames && references && sourceAssociations && history &&
                    features && expressions && bodies && curves && helpers && dirty && solver && objectsRestored &&
                    sessionPreferenceRestored;
            }
            catch (Exception readbackException)
            {
                restore["restoration_readback_exception_type"] = readbackException.GetType().FullName;
                restore["restoration_readback_exception_message"] = M3SketchReferenceConversionSafeMessage(readbackException);
            }
        }

        bool markClosed = false;
        try
        {
            state.Session.DeleteUndoMark(mark, "");
            markClosed = true;
        }
        catch (Exception closeException)
        {
            restore["undo_mark_close_exception_type"] = closeException.GetType().FullName;
            restore["undo_mark_close_exception_message"] = M3SketchReferenceConversionSafeMessage(closeException);
        }
        restore["undo_mark_closed"] = markClosed;
        restore["full_restoration_proven"] = restored && markClosed;
        plan.NativeOperationEvidence["rollback"] = restore;

        bool transactionMutationAttempted = Boolean.Equals(restore["native_operation_called"], true) ||
            plan.WorkRegionScopeCommitAttempted;
        string resultState = restored && markClosed
            ? transactionMutationAttempted ? "ROLLED_BACK" : "UNCHANGED"
            : "UNKNOWN";
        Dictionary<string, object> result = BuildM3SketchReferenceConversionResult(plan,
            binding == null ? null : binding.RevisionId, resultState, restore,
            plan.Objects ?? new List<M3SketchReferenceConversionObjectState>());
        if (restored && markClosed)
            return M3Execution(resultState, binding == null ? null : binding.RevisionId, result,
                M3SketchReferenceConversionExceptionCode(operationException),
                "F11 failed and Undo proved restoration of the captured Sketch and Part state.");
        return M3Execution("UNKNOWN", binding == null ? null : binding.RevisionId, result,
            "ROLLBACK_UNVERIFIED", "NX Undo could not prove full restoration of the F11 baseline.");
    }

    private M3SketchReferenceConversionLiveState CaptureM3SketchReferenceConversionLiveState(
        M2ModelBinding binding, BindingState state, Sketch sketch, SnapshotResult capture,
        List<M3SketchReferenceConversionObjectState> objects)
    {
        if (state == null || state.WorkPart == null || sketch == null || capture == null || capture.Snapshot == null)
            throw new M2ModelException("MODEL_READ_FAILED", "F11 state capture requires a Work Part, Sketch and semantic snapshot");
        Dictionary<string, M3SketchProjectionFeatureState> featureStates =
            CaptureM3SketchProjectionFeatureStates(state.WorkPart);
        string featureInventory = M3SketchProjectionFeatureInventoryText(featureStates);
        string history = M2Json.CanonicalText(M3SketchProjectionStableHistoryProjection(state.WorkPart, featureStates));
        string targetFeatureId = RequiredJournalId(sketch.Feature, "F11 target Sketch feature");
        string targetFeatureTag = sketch.Feature.Tag.ToString();
        string featureInventoryNormalized = M3SketchReferenceConversionFeatureInventoryProjectionText(
            featureStates, targetFeatureTag, objects);
        string historyNormalized = M3SketchReferenceConversionHistoryProjectionText(
            state.WorkPart, featureStates, targetFeatureTag, objects);
        Dictionary<string, int> references = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
        List<object> contourCandidates;
        string contourCandidatesText;
        CaptureM3SketchReferenceConversionContourCandidates(sketch, references,
            out contourCandidates, out contourCandidatesText);
        Sketch active = state.Session.ActiveSketch;
        Dictionary<string, object> solver = CaptureM3SketchReferenceConversionSolver(sketch);
        Dictionary<string, M3SketchProjectionCurveState> curveInventory =
            CaptureM3SketchProjectionCurveInventory(state.WorkPart);
        string semantic = CanonicalSemanticSnapshot(capture.Snapshot);
        string semanticNormalized = M3SketchReferenceConversionSemanticProjection(capture.Snapshot,
            RequiredJournalId(sketch, "F11 Sketch"), targetFeatureId, objects);
        string fullGeometry = CaptureFullGeometry(state.WorkPart);
        Dictionary<string, CornerGeometrySnapshot> geometry = CaptureSketchCornerGeometry(state.WorkPart);
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraints =
            CaptureAllSketchOffsetConstraints(state.WorkPart);
        Dictionary<string, string> frames = CaptureAllSketchOffsetFrames(state.WorkPart);
        string dimensionAssociations = CaptureM3SketchReferenceConversionDimensionAssociations(state.WorkPart, objects);
        string sourceAssociations = CaptureM3SketchReferenceConversionSourceAssociations(state.WorkPart, featureStates);
        List<M3BodyMeasure> bodies = CaptureM3BodyMeasures(state.WorkPart);
        List<string> featureTags = CaptureFeatureTags(state.WorkPart);
        string expressions = M2Json.CanonicalText(SnapshotList(capture.Snapshot, "expressions"));
        string expressionsNormalized = M3SketchReferenceConversionExpressionProjection(capture.Snapshot, objects);
        string curveInventoryText = M3SketchProjectionCurveInventoryText(curveInventory);
        Dictionary<string, List<string>> helperTags = CapturePatternPartHelperTags(state.WorkPart);
        bool dirty;
        try { dirty = state.WorkPart.IsModified; }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "could not read F11 Work Part dirty state after snapshot getters (" + ex.GetType().Name + ")");
        }
        return new M3SketchReferenceConversionLiveState
        {
            Capture = capture,
            Semantic = semantic,
            SemanticNormalized = semanticNormalized,
            FullGeometry = fullGeometry,
            Geometry = geometry,
            Constraints = constraints,
            Frames = frames,
            ReferenceStates = references,
            DimensionAssociations = dimensionAssociations,
            SourceAssociations = sourceAssociations,
            Bodies = bodies,
            FeatureTags = featureTags,
            FeatureStates = featureStates,
            FeatureInventory = featureInventory,
            FeatureInventoryNormalized = featureInventoryNormalized,
            History = history,
            HistoryNormalized = historyNormalized,
            Expressions = expressions,
            ExpressionsNormalized = expressionsNormalized,
            CurveInventory = curveInventory,
            CurveInventoryText = curveInventoryText,
            HelperTags = helperTags,
            Dirty = dirty,
            ActiveSketchTag = active == null ? null : active.Tag.ToString(),
            Solver = solver,
            ContourCandidates = contourCandidates,
            ContourCandidatesText = contourCandidatesText
        };
    }

    private static void ValidateM3SketchReferenceConversionRequest(M3SketchReferenceConversionRequest request)
    {
        if (request == null || String.IsNullOrWhiteSpace(request.RevisionId) ||
            String.IsNullOrWhiteSpace(request.SketchId))
            throw new M2ModelException("INVALID_REQUEST", "revision_id and sketch_id are required for F11");
        if (request.TargetState != "reference" && request.TargetState != "active")
            throw new M2ModelException("INVALID_REQUEST", "target_state must be reference or active");
        if (request.ObjectIds == null || request.ObjectIds.Count < 1 || request.ObjectIds.Count > 256)
            throw new M2ModelException("INVALID_REQUEST", "object_ids must contain 1 through 256 Sketch object ids");
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in request.ObjectIds)
            if (String.IsNullOrWhiteSpace(id) || !seen.Add(id))
                throw new M2ModelException("INVALID_REQUEST", "object_ids must contain nonempty unique current JournalIdentifiers");
    }

    private static M3SketchReferenceConversionRequest CloneM3SketchReferenceConversionRequest(
        M3SketchReferenceConversionRequest source)
    {
        return new M3SketchReferenceConversionRequest
        {
            RevisionId = source.RevisionId,
            SketchId = source.SketchId,
            ObjectIds = source.ObjectIds == null ? new List<string>() : new List<string>(source.ObjectIds),
            TargetState = source.TargetState
        };
    }

    private static List<M3SketchReferenceConversionObjectState> ResolveM3SketchReferenceConversionObjects(
        Part part, Sketch sketch, List<string> objectIds, string targetState,
        Dictionary<string, int> referenceStates)
    {
        if (part == null || sketch == null || objectIds == null || referenceStates == null)
            throw new M2ModelException("MODEL_READ_FAILED", "F11 object resolution requires a Work Part, Sketch and state inventory");
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null during F11 resolution");
        Dictionary<string, List<Curve>> curvesById = new Dictionary<string, List<Curve>>(StringComparer.Ordinal);
        foreach (NXObject item in geometry)
        {
            if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Sketch geometry contains null during F11 resolution");
            Curve curve = item as Curve;
            if (curve == null) continue;
            if (!sketch.IsSketchGeometry(curve) || curve.OwningPart == null || curve.OwningPart.Tag != part.Tag || curve.IsOccurrence)
                throw new M2ModelException("SKETCH_TARGET_MISMATCH", "F11 curve is not owned by the requested Sketch in the Work Part");
            string id = RequiredJournalId(curve, "F11 Sketch curve");
            List<Curve> matches;
            if (!curvesById.TryGetValue(id, out matches))
            {
                matches = new List<Curve>();
                curvesById.Add(id, matches);
            }
            matches.Add(curve);
        }

        Dictionary<string, List<SketchDimensionalConstraint>> dimensionsById =
            new Dictionary<string, List<SketchDimensionalConstraint>>(StringComparer.Ordinal);
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
            if (constraints == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch constraint inventory returned null during F11 resolution");
            foreach (SketchConstraint constraint in constraints)
            {
                if (constraint == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Sketch constraint inventory contains null during F11 resolution");
                SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                if (dimension == null) continue;
                string id = RequiredJournalId(dimension, "F11 dimensional constraint");
                List<SketchDimensionalConstraint> matches;
                if (!dimensionsById.TryGetValue(id, out matches))
                {
                    matches = new List<SketchDimensionalConstraint>();
                    dimensionsById.Add(id, matches);
                }
                matches.Add(dimension);
            }
        }

        List<M3SketchReferenceConversionObjectState> result = new List<M3SketchReferenceConversionObjectState>();
        HashSet<string> selectedTags = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> selectedNativeTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in objectIds)
        {
            List<Curve> curveMatches;
            List<SketchDimensionalConstraint> dimensionMatches;
            curvesById.TryGetValue(id, out curveMatches);
            dimensionsById.TryGetValue(id, out dimensionMatches);
            int curveCount = curveMatches == null ? 0 : curveMatches.Count;
            int dimensionCount = dimensionMatches == null ? 0 : dimensionMatches.Count;
            if (curveCount + dimensionCount == 0)
                throw new M2ModelException("OBJECT_NOT_FOUND", "object_ids must identify a current curve or dimensional constraint in the requested Sketch");
            if (curveCount + dimensionCount != 1)
                throw new M2ModelException("AMBIGUOUS_OBJECT", "an F11 JournalIdentifier resolves to multiple Sketch objects");

            M3SketchReferenceConversionObjectState selected = new M3SketchReferenceConversionObjectState
            {
                Id = id,
                ExpectedAfter = targetState
            };
            if (curveCount == 1)
            {
                Curve curve = curveMatches[0];
                if (!(curve is Line) && !(curve is Arc))
                    throw new M2ModelException("UNSUPPORTED_GEOMETRY",
                        "F11 explicit curve conversion currently supports native Line and Arc objects only");
                string tag = curve.Tag.ToString();
                int referenceStatus;
                if (!referenceStates.TryGetValue(tag, out referenceStatus))
                    throw new M2ModelException("MODEL_READ_FAILED", "selected curve has no captured UF reference state");
                selected.Curve = curve;
                selected.NativeObject = curve;
                selected.Kind = "curve";
                selected.Tag = tag;
                selected.NativeInputTag = tag;
                selected.StateBefore = M3SketchReferenceConversionCurveState(referenceStatus);
            }
            else
            {
                SketchDimensionalConstraint constraint = dimensionMatches[0];
                string constraintType = constraint.ConstraintType.ToString();
                if (!String.Equals(constraintType, "HorizontalDim", StringComparison.Ordinal) &&
                    !String.Equals(constraintType, "VerticalDim", StringComparison.Ordinal))
                    throw new M2ModelException("UNSUPPORTED_DIMENSION",
                        "F11 currently supports HorizontalDim and VerticalDim constraints only");
                Dimension associated = constraint.AssociatedDimension;
                if (associated == null || associated.OwningPart == null || associated.OwningPart.Tag != part.Tag || associated.IsOccurrence)
                    throw new M2ModelException("UNSUPPORTED_DIMENSION", "selected dimensional constraint has no same-part AssociatedDimension");
                SketchDimensionalConstraint.DimensionStateType dimensionState = constraint.DimensionState;
                selected.StateBefore = M3SketchReferenceConversionDimensionState(dimensionState);
                Expression expression = constraint.AssociatedExpression;
                if (expression == null)
                    throw new M2ModelException("UNSUPPORTED_DIMENSION",
                        "F11 cannot convert the selected HorizontalDim/VerticalDim because its AssociatedExpression is null");
                if (expression.Units == null || !String.Equals(expression.Units.Symbol, "mm", StringComparison.Ordinal))
                    throw new M2ModelException("UNSUPPORTED_DIMENSION",
                        "F11 supports only HorizontalDim/VerticalDim dimensions with a millimeter AssociatedExpression");
                double computedSize = associated.ComputedSize;
                if (!IsFinite(computedSize))
                    throw new M2ModelException("MODEL_READ_FAILED", "selected AssociatedDimension.ComputedSize is non-finite");
                double expressionValue = expression.Value;
                if (!IsFinite(expressionValue))
                    throw new M2ModelException("MODEL_READ_FAILED", "selected dimension expression Value is non-finite");
                selected.Constraint = constraint;
                selected.AssociatedDimension = associated;
                selected.AssociatedExpression = expression;
                selected.NativeObject = associated;
                selected.Kind = "dimension";
                selected.Tag = constraint.Tag.ToString();
                selected.ConstraintType = constraintType;
                selected.NativeInputTag = associated.Tag.ToString();
                selected.AssociatedDimensionTag = associated.Tag.ToString();
                selected.AssociatedDimensionId = RequiredJournalId(associated, "F11 AssociatedDimension");
                selected.ExpressionTag = expression.Tag.ToString();
                selected.ExpressionId = RequiredJournalId(expression, "F11 dimension expression");
                selected.ExpressionName = expression.Name;
                selected.ExpressionUnit = expression.Units.Symbol;
                selected.ExpressionRightHandSideBefore = expression.RightHandSide;
                selected.ExpressionRightHandSideAfter = expression.RightHandSide;
                selected.ExpressionValueBefore = expressionValue;
                selected.ExpressionValueAfter = expressionValue;
                selected.ComputedSizeBefore = computedSize;
                selected.WillChange = !String.Equals(selected.StateBefore, targetState, StringComparison.Ordinal);
                if (selected.WillChange && M3SketchReferenceConversionExpressionUseCount(part, selected.ExpressionTag) != 1)
                    throw new M2ModelException("UNSUPPORTED_DIMENSION_EXPRESSION",
                        "F11 cannot change a dimension whose expression is shared by another dimensional constraint");
                if (String.Equals(selected.StateBefore, "reference", StringComparison.Ordinal) &&
                    String.Equals(targetState, "active", StringComparison.Ordinal))
                {
                    double numericRhs;
                    if (!M3SketchReferenceConversionTryParseNumericRhs(selected.ExpressionRightHandSideBefore, out numericRhs))
                        throw new M2ModelException("UNSUPPORTED_DIMENSION_EXPRESSION",
                            "Reference-to-Active conversion requires a numeric literal dimension expression");
                }
            }
            if (String.IsNullOrEmpty(selected.Tag) || !selectedTags.Add(selected.Tag) ||
                String.IsNullOrEmpty(selected.NativeInputTag) || !selectedNativeTags.Add(selected.NativeInputTag))
                throw new M2ModelException("INVALID_REQUEST", "object_ids resolve to duplicate native Sketch or builder input Tags");
            selected.WillChange = !String.Equals(selected.StateBefore, targetState, StringComparison.Ordinal);
            result.Add(selected);
        }
        return result;
    }

    private static int M3SketchReferenceConversionExpressionUseCount(Part part, string expressionTag)
    {
        if (part == null || String.IsNullOrEmpty(expressionTag))
            throw new M2ModelException("MODEL_READ_FAILED", "F11 expression use count requires a Part and expression Tag");
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory returned null while checking shared F11 expressions");
        int count = 0;
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Sketch inventory contains null while checking shared F11 expressions");
            foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
            {
                if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
                SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
                if (constraints == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch constraints returned null while checking shared F11 expressions");
                foreach (SketchConstraint constraint in constraints)
                {
                    SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                    if (dimension == null) continue;
                    Expression expression = dimension.AssociatedExpression;
                    if (expression != null && String.Equals(expression.Tag.ToString(), expressionTag, StringComparison.Ordinal)) count++;
                }
            }
        }
        return count;
    }

    private static bool M3SketchReferenceConversionTryParseNumericRhs(string rightHandSide, out double value)
    {
        value = Double.NaN;
        if (String.IsNullOrWhiteSpace(rightHandSide) ||
            !Double.TryParse(rightHandSide.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            !IsFinite(value)) return false;
        return true;
    }

    private static string M3SketchReferenceConversionCurveState(int status)
    {
        if (status == Convert.ToInt32(UFSket.ReferenceStatus.Reference, CultureInfo.InvariantCulture)) return "reference";
        if (status == Convert.ToInt32(UFSket.ReferenceStatus.Active, CultureInfo.InvariantCulture)) return "active";
        throw new M2ModelException("UNSUPPORTED_OBJECT_STATE", "selected Sketch curve has an unsupported UF reference state");
    }

    private static string M3SketchReferenceConversionCurveState(UFSket.ReferenceStatus status)
    {
        if (status == UFSket.ReferenceStatus.Reference) return "reference";
        if (status == UFSket.ReferenceStatus.Active) return "active";
        throw new M2ModelException("UNSUPPORTED_OBJECT_STATE", "Sketch curve has an unsupported UF reference state");
    }

    private static string M3SketchReferenceConversionDimensionState(
        SketchDimensionalConstraint.DimensionStateType state)
    {
        if (state == SketchDimensionalConstraint.DimensionStateType.Reference) return "reference";
        if (state == SketchDimensionalConstraint.DimensionStateType.Driving) return "active";
        throw new M2ModelException("UNSUPPORTED_OBJECT_STATE", "selected Sketch dimension has an unsupported DimensionState");
    }

    private static bool M3SketchReferenceConversionObjectIdentitiesMatch(
        List<M3SketchReferenceConversionObjectState> expected,
        List<M3SketchReferenceConversionObjectState> actual, bool compareState)
    {
        if (expected == null || actual == null || expected.Count != actual.Count) return false;
        for (int i = 0; i < expected.Count; i++)
        {
            M3SketchReferenceConversionObjectState left = expected[i];
            M3SketchReferenceConversionObjectState right = actual[i];
            if (!String.Equals(left.Id, right.Id, StringComparison.Ordinal) ||
                !String.Equals(left.Kind, right.Kind, StringComparison.Ordinal) ||
                !String.Equals(left.Tag, right.Tag, StringComparison.Ordinal) ||
                !String.Equals(left.ConstraintType, right.ConstraintType, StringComparison.Ordinal) ||
                !String.Equals(left.NativeInputTag, right.NativeInputTag, StringComparison.Ordinal) ||
                !String.Equals(left.AssociatedDimensionTag, right.AssociatedDimensionTag, StringComparison.Ordinal) ||
                !String.Equals(left.AssociatedDimensionId, right.AssociatedDimensionId, StringComparison.Ordinal) ||
                !String.Equals(left.ExpressionTag, right.ExpressionTag, StringComparison.Ordinal) ||
                !String.Equals(left.ExpressionId, right.ExpressionId, StringComparison.Ordinal) ||
                !String.Equals(left.ExpressionName, right.ExpressionName, StringComparison.Ordinal) ||
                !String.Equals(left.ExpressionUnit, right.ExpressionUnit, StringComparison.Ordinal) ||
                !String.Equals(left.ExpectedAfter, right.ExpectedAfter, StringComparison.Ordinal)) return false;
            if (compareState && (!String.Equals(left.StateBefore, right.StateBefore, StringComparison.Ordinal) ||
                left.WillChange != right.WillChange)) return false;
            if (left.Kind == "dimension" && left.ComputedSizeBefore.HasValue && right.ComputedSizeBefore.HasValue &&
                !NearlyEqual(left.ComputedSizeBefore.Value, right.ComputedSizeBefore.Value)) return false;
        }
        return true;
    }

    private static void M3SketchReferenceConversionUpdateObjectComputedSizes(
        List<M3SketchReferenceConversionObjectState> before,
        List<M3SketchReferenceConversionObjectState> after)
    {
        if (before == null || after == null || before.Count != after.Count)
            throw new M2ModelException("REFERENCE_CONVERSION_TARGET_LOST", "selected F11 objects are unavailable after update");
        for (int i = 0; i < before.Count; i++)
        {
            if (before[i].Kind != "dimension") continue;
            if (after[i].Kind != "dimension" || after[i].AssociatedDimension == null)
                throw new M2ModelException("REFERENCE_CONVERSION_TARGET_LOST", "selected F11 dimension lost its AssociatedDimension");
            double computedSize = after[i].AssociatedDimension.ComputedSize;
            if (!IsFinite(computedSize))
                throw new M2ModelException("MODEL_READ_FAILED", "selected AssociatedDimension.ComputedSize is non-finite after update");
            before[i].ComputedSizeAfter = computedSize;
            before[i].ExpressionRightHandSideAfter = after[i].ExpressionRightHandSideBefore;
            before[i].ExpressionValueAfter = after[i].ExpressionValueBefore;
        }
    }

    private static List<object> M3SketchReferenceConversionObjectRecords(
        M3SketchReferenceConversionPlanState plan,
        List<M3SketchReferenceConversionObjectState> objectsAfter, bool useAfter)
    {
        List<object> result = new List<object>();
        if (plan == null || plan.Objects == null) return result;
        for (int i = 0; i < plan.Objects.Count; i++)
        {
            M3SketchReferenceConversionObjectState before = plan.Objects[i];
            M3SketchReferenceConversionObjectState after = useAfter && objectsAfter != null && i < objectsAfter.Count
                ? objectsAfter[i] : null;
            Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "object_id", before.Id },
                { "kind", before.Kind },
                { "tag", before.Tag },
                { "journal_id", before.Id },
                { "current_state", after == null ? before.StateBefore : after.StateBefore },
                { "requested_state", plan.TargetState },
                { "expected_after", before.ExpectedAfter },
                { "will_change", before.WillChange }
            };
            if (before.Kind == "dimension")
            {
                record["associated_dimension_tag"] = before.AssociatedDimensionTag;
                record["associated_dimension_journal_id"] = before.AssociatedDimensionId;
                record["computed_size_before"] = before.ComputedSizeBefore;
                record["computed_size_after"] = after != null ? after.ComputedSizeBefore : before.ComputedSizeAfter;
                record["computed_size_preserved"] = after != null && before.ComputedSizeBefore.HasValue && after.ComputedSizeBefore.HasValue &&
                    NearlyEqual(before.ComputedSizeBefore.Value, after.ComputedSizeBefore.Value);
                record["expression_tag"] = before.ExpressionTag;
                record["expression_journal_id"] = before.ExpressionId;
                record["expression_name"] = before.ExpressionName;
                record["expression_unit"] = before.ExpressionUnit;
                record["expression_right_hand_side_before"] = before.ExpressionRightHandSideBefore;
                record["expression_right_hand_side_after"] = after != null
                    ? after.ExpressionRightHandSideBefore : before.ExpressionRightHandSideAfter;
                record["expression_value_before"] = before.ExpressionValueBefore;
                record["expression_value_after"] = after != null
                    ? after.ExpressionValueBefore : before.ExpressionValueAfter;
            }
            result.Add(record);
        }
        return result;
    }

    private static string M3SketchReferenceConversionSemanticProjection(
        Dictionary<string, object> snapshot, string sketchId, string targetFeatureId,
        List<M3SketchReferenceConversionObjectState> objects)
    {
        Dictionary<string, object> copy = DeepCloneValue(snapshot) as Dictionary<string, object>;
        if (copy == null) throw new M2ModelException("MODEL_READ_FAILED", "F11 semantic snapshot could not be normalized");
        if (String.IsNullOrEmpty(targetFeatureId))
            throw new M2ModelException("MODEL_READ_FAILED", "F11 target Sketch feature identity is missing from the semantic projection");
        HashSet<string> curves = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, string> dimensionTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        HashSet<string> promotedDimensions = M3SketchReferenceConversionPromotionDimensionIds(objects);
        if (objects != null)
            foreach (M3SketchReferenceConversionObjectState item in objects)
            {
                if (item.Kind == "curve") curves.Add(item.Id);
                else if (item.Kind == "dimension") dimensionTargets.Add(item.Id, item.ExpectedAfter);
            }

        List<object> sketches = SnapshotList(copy, "sketches");
        Dictionary<string, object> target = null;
        foreach (object value in sketches)
        {
            Dictionary<string, object> item = value as Dictionary<string, object>;
            if (item == null) continue;
            if (!String.Equals(Convert.ToString(item["journal_id"], CultureInfo.InvariantCulture), sketchId, StringComparison.Ordinal)) continue;
            if (target != null) throw new M2ModelException("AMBIGUOUS_SKETCH", "F11 semantic snapshot contains a duplicate target Sketch");
            target = item;
        }
        if (target == null) throw new M2ModelException("SKETCH_NOT_FOUND", "F11 semantic snapshot has no target Sketch record");
        target.Remove("status");
        target.Remove("degrees_of_freedom");

        List<object> geometry = target.ContainsKey("geometry") ? target["geometry"] as List<object> : null;
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "F11 semantic snapshot has no Sketch geometry list");
        foreach (object value in geometry)
        {
            Dictionary<string, object> item = value as Dictionary<string, object>;
            if (item == null) continue;
            string id = Convert.ToString(item["journal_id"], CultureInfo.InvariantCulture);
            if (!curves.Contains(id)) continue;
            item.Remove("reference_status");
            item.Remove("reference_status_value");
            item.Remove("reference_curve_solver_semantics");
        }

        List<object> constraints = target.ContainsKey("constraints") ? target["constraints"] as List<object> : null;
        if (constraints == null) throw new M2ModelException("MODEL_READ_FAILED", "F11 semantic snapshot has no Sketch constraints list");
        foreach (object value in constraints)
        {
            Dictionary<string, object> item = value as Dictionary<string, object>;
            if (item == null) continue;
            string id = Convert.ToString(item["journal_id"], CultureInfo.InvariantCulture);
            string targetState;
            if (!dimensionTargets.TryGetValue(id, out targetState)) continue;
            bool targetActive = String.Equals(targetState, "active", StringComparison.Ordinal);
            item["dimension_state"] = targetActive ? "Driving" : "Reference";
            item["is_driving"] = targetActive;
            item["solver_participation"] = targetActive ? "DRIVES_GEOMETRY" : "MEASUREMENT_ONLY";
            if (promotedDimensions.Contains(id))
            {
                item.Remove("value");
                item.Remove("expression_value");
                item.Remove("semantic_key");
            }
        }
        M3SketchReferenceConversionNormalizeSemanticFeatureExpressionMembership(copy, targetFeatureId,
            M3SketchReferenceConversionChangedExpressionNames(objects));
        M3SketchReferenceConversionNormalizePromotedExpressions(copy, objects);
        return CanonicalSemanticSnapshot(copy);
    }

    private static string M3SketchReferenceConversionFeatureInventoryProjectionText(
        Dictionary<string, M3SketchProjectionFeatureState> features, string targetFeatureTag,
        List<M3SketchReferenceConversionObjectState> objects)
    {
        if (features == null || String.IsNullOrEmpty(targetFeatureTag))
            throw new M2ModelException("MODEL_READ_FAILED", "F11 feature inventory projection requires the target Sketch feature");
        List<M3SketchProjectionFeatureState> ordered = new List<M3SketchProjectionFeatureState>(features.Values);
        ordered.Sort(delegate(M3SketchProjectionFeatureState left, M3SketchProjectionFeatureState right)
        {
            return String.CompareOrdinal(left.Tag, right.Tag);
        });
        List<object> records = new List<object>();
        int targetMatches = 0;
        HashSet<string> changedNames = M3SketchReferenceConversionChangedExpressionNames(objects);
        foreach (M3SketchProjectionFeatureState feature in ordered)
        {
            Dictionary<string, object> record = M3SketchProjectionStableFeatureRecord(feature);
            if (String.Equals(feature.Tag, targetFeatureTag, StringComparison.Ordinal))
            {
                targetMatches++;
                M3SketchReferenceConversionRemoveSelectedExpressionNames(record, "expression_names", changedNames);
            }
            records.Add(record);
        }
        if (targetMatches != 1)
            throw new M2ModelException("MODEL_READ_FAILED", "F11 feature inventory must contain one target Sketch feature");
        return M2Json.CanonicalText(records);
    }

    private static string M3SketchReferenceConversionHistoryProjectionText(Part part,
        Dictionary<string, M3SketchProjectionFeatureState> features, string targetFeatureTag,
        List<M3SketchReferenceConversionObjectState> objects)
    {
        if (part == null || features == null || String.IsNullOrEmpty(targetFeatureTag))
            throw new M2ModelException("MODEL_READ_FAILED", "F11 stable history projection requires a target Sketch feature");
        List<object> projection = M3SketchProjectionStableHistoryProjection(part, features);
        if (projection == null || projection.Count != 2)
            throw new M2ModelException("MODEL_READ_FAILED", "F11 stable history projection has an unexpected shape");
        List<object> featureRecords = projection[0] as List<object>;
        if (featureRecords == null)
            throw new M2ModelException("MODEL_READ_FAILED", "F11 stable history projection has no feature records");
        int targetMatches = 0;
        HashSet<string> changedNames = M3SketchReferenceConversionChangedExpressionNames(objects);
        foreach (object value in featureRecords)
        {
            Dictionary<string, object> feature = value as Dictionary<string, object>;
            if (feature == null) throw new M2ModelException("MODEL_READ_FAILED", "F11 stable feature record is invalid");
            object tagValue;
            if (!feature.TryGetValue("tag", out tagValue) ||
                !String.Equals(Convert.ToString(tagValue, CultureInfo.InvariantCulture), targetFeatureTag, StringComparison.Ordinal)) continue;
            targetMatches++;
            M3SketchReferenceConversionRemoveSelectedExpressionNames(feature, "expression_names", changedNames);
        }
        if (targetMatches != 1)
            throw new M2ModelException("MODEL_READ_FAILED", "F11 stable history must contain one target Sketch feature record");
        return M2Json.CanonicalText(projection);
    }

    private static void M3SketchReferenceConversionRemoveSelectedExpressionNames(
        Dictionary<string, object> featureRecord, string expressionListName, HashSet<string> selectedNames)
    {
        if (featureRecord == null || selectedNames == null || selectedNames.Count == 0) return;
        object value;
        List<object> expressionNames = featureRecord.TryGetValue(expressionListName, out value) ? value as List<object> : null;
        if (expressionNames == null)
            throw new M2ModelException("MODEL_READ_FAILED", "F11 target feature has no expression membership list");
        List<object> retained = new List<object>();
        foreach (object expressionValue in expressionNames)
        {
            string expressionName = Convert.ToString(expressionValue, CultureInfo.InvariantCulture);
            if (!selectedNames.Contains(expressionName)) retained.Add(expressionValue);
        }
        featureRecord[expressionListName] = retained;
    }

    private static HashSet<string> M3SketchReferenceConversionChangedExpressionNames(
        List<M3SketchReferenceConversionObjectState> objects)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        if (objects == null) return result;
        foreach (M3SketchReferenceConversionObjectState item in objects)
        {
            if (item == null || !item.WillChange || !String.Equals(item.Kind, "dimension", StringComparison.Ordinal)) continue;
            if (String.IsNullOrEmpty(item.ExpressionName))
                throw new M2ModelException("UNSUPPORTED_DIMENSION", "a changing F11 dimension has no expression name for feature membership verification");
            result.Add(item.ExpressionName);
        }
        return result;
    }

    private static void M3SketchReferenceConversionNormalizeSemanticFeatureExpressionMembership(
        Dictionary<string, object> snapshot, string targetFeatureId, HashSet<string> selectedExpressionNames)
    {
        if (snapshot == null || String.IsNullOrEmpty(targetFeatureId))
            throw new M2ModelException("MODEL_READ_FAILED", "F11 semantic feature membership projection requires a target feature");
        if (selectedExpressionNames == null || selectedExpressionNames.Count == 0) return;

        List<object> features = SnapshotList(snapshot, "features");
        int featureMatches = 0;
        foreach (object value in features)
        {
            Dictionary<string, object> feature = value as Dictionary<string, object>;
            if (feature == null) throw new M2ModelException("MODEL_READ_FAILED", "F11 semantic feature inventory contains an invalid record");
            object idValue;
            if (!feature.TryGetValue("journal_id", out idValue) ||
                !String.Equals(Convert.ToString(idValue, CultureInfo.InvariantCulture), targetFeatureId, StringComparison.Ordinal)) continue;
            featureMatches++;
            object expressionValue;
            List<object> expressionIds = feature.TryGetValue("expression_ids", out expressionValue) ? expressionValue as List<object> : null;
            if (expressionIds == null) throw new M2ModelException("MODEL_READ_FAILED", "target Sketch feature has no expression membership list");
            List<object> retained = new List<object>();
            foreach (object expressionIdValue in expressionIds)
            {
                string expressionId = Convert.ToString(expressionIdValue, CultureInfo.InvariantCulture);
                if (!selectedExpressionNames.Contains(expressionId)) retained.Add(expressionIdValue);
            }
            feature["expression_ids"] = retained;
        }
        if (featureMatches != 1)
            throw new M2ModelException("MODEL_READ_FAILED", "F11 semantic feature inventory does not contain one target Sketch feature");

        List<object> dependencies = SnapshotList(snapshot, "dependencies");
        List<object> retainedDependencies = new List<object>();
        foreach (object value in dependencies)
        {
            Dictionary<string, object> edge = value as Dictionary<string, object>;
            if (edge == null) throw new M2ModelException("MODEL_READ_FAILED", "F11 semantic dependency inventory contains an invalid record");
            object kindValue, sourceValue, targetValue;
            bool isSelectedFeatureExpression = edge.TryGetValue("kind", out kindValue) &&
                String.Equals(Convert.ToString(kindValue, CultureInfo.InvariantCulture), "FEATURE_EXPRESSION", StringComparison.Ordinal) &&
                edge.TryGetValue("source_id", out sourceValue) &&
                String.Equals(Convert.ToString(sourceValue, CultureInfo.InvariantCulture), targetFeatureId, StringComparison.Ordinal) &&
                edge.TryGetValue("target_id", out targetValue) &&
                selectedExpressionNames.Contains(Convert.ToString(targetValue, CultureInfo.InvariantCulture));
            if (!isSelectedFeatureExpression) retainedDependencies.Add(value);
        }
        snapshot["dependencies"] = retainedDependencies;
    }

    // Pure snapshot checker used by F11 postconditions and reflection-only counterexample challenges.
    private static bool M3SketchReferenceConversionFeatureExpressionMembershipMatches(
        string targetFeatureTag, string targetFeatureId,
        Dictionary<string, M3SketchProjectionFeatureState> featuresBefore,
        Dictionary<string, M3SketchProjectionFeatureState> featuresAfter,
        Dictionary<string, object> semanticBefore, Dictionary<string, object> semanticAfter,
        List<M3SketchReferenceConversionObjectState> objects,
        out Dictionary<string, object> evidence)
    {
        bool valid = !String.IsNullOrEmpty(targetFeatureTag) && !String.IsNullOrEmpty(targetFeatureId) &&
            featuresBefore != null && featuresAfter != null && semanticBefore != null && semanticAfter != null && objects != null;
        bool featureBeforeValid, featureAfterValid, dependencyBeforeValid, dependencyAfterValid;
        Dictionary<string, int> featureBefore = M3SketchReferenceConversionFeatureExpressionCounts(featuresBefore, out featureBeforeValid);
        Dictionary<string, int> featureAfter = M3SketchReferenceConversionFeatureExpressionCounts(featuresAfter, out featureAfterValid);
        Dictionary<string, int> dependencyBefore = M3SketchReferenceConversionDependencyCounts(semanticBefore, out dependencyBeforeValid);
        Dictionary<string, int> dependencyAfter = M3SketchReferenceConversionDependencyCounts(semanticAfter, out dependencyAfterValid);
        valid = valid && featureBeforeValid && featureAfterValid && dependencyBeforeValid && dependencyAfterValid;

        List<object> selectedRecords = new List<object>();
        HashSet<string> selectedChangedFeatureKeys = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> selectedChangedDependencyKeys = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> seenChangedExpressionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (M3SketchReferenceConversionObjectState item in objects ?? new List<M3SketchReferenceConversionObjectState>())
        {
            if (item == null || !String.Equals(item.Kind, "dimension", StringComparison.Ordinal)) continue;
            if (String.IsNullOrEmpty(item.ExpressionName))
            {
                valid = false;
                selectedRecords.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "object_id", item.Id }, { "status", "MISSING_EXPRESSION_IDENTITY" }
                });
                continue;
            }

            string featureKey = M3SketchReferenceConversionMembershipKey(targetFeatureTag, item.ExpressionName);
            string dependencyKey = M3SketchReferenceConversionDependencyKey("FEATURE_EXPRESSION", targetFeatureId, item.ExpressionName);
            int beforeFeatureCount = M3SketchReferenceConversionCount(featureBefore, featureKey);
            int afterFeatureCount = M3SketchReferenceConversionCount(featureAfter, featureKey);
            int beforeDependencyCount = M3SketchReferenceConversionCount(dependencyBefore, dependencyKey);
            int afterDependencyCount = M3SketchReferenceConversionCount(dependencyAfter, dependencyKey);

            int expectedBefore = String.Equals(item.StateBefore, "active", StringComparison.Ordinal) ? 1 :
                String.Equals(item.StateBefore, "reference", StringComparison.Ordinal) ? 0 : -1;
            int expectedAfter = item.WillChange
                ? (String.Equals(item.ExpectedAfter, "active", StringComparison.Ordinal) ? 1 :
                    String.Equals(item.ExpectedAfter, "reference", StringComparison.Ordinal) ? 0 : -1)
                : beforeFeatureCount;
            bool baselineMatchesState = expectedBefore >= 0 && beforeFeatureCount == expectedBefore &&
                beforeDependencyCount == expectedBefore;
            bool afterMatchesPolicy = expectedAfter >= 0 && afterFeatureCount == expectedAfter &&
                afterDependencyCount == expectedAfter;
            bool featureDependencyConsistent = beforeFeatureCount == beforeDependencyCount &&
                afterFeatureCount == afterDependencyCount;
            if (!baselineMatchesState || !afterMatchesPolicy || !featureDependencyConsistent) valid = false;

            if (item.WillChange)
            {
                if (!seenChangedExpressionNames.Add(item.ExpressionName)) valid = false;
                selectedChangedFeatureKeys.Add(featureKey);
                selectedChangedDependencyKeys.Add(dependencyKey);
            }
            selectedRecords.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "object_id", item.Id }, { "constraint_tag", item.Tag },
                { "expression_name", item.ExpressionName }, { "expression_journal_id", item.ExpressionId },
                { "state_before", item.StateBefore }, { "expected_after", item.ExpectedAfter },
                { "will_change", item.WillChange },
                { "feature_membership_before_count", beforeFeatureCount },
                { "feature_membership_after_count", afterFeatureCount },
                { "dependency_membership_before_count", beforeDependencyCount },
                { "dependency_membership_after_count", afterDependencyCount },
                { "expected_feature_membership_before_count", expectedBefore },
                { "expected_feature_membership_after_count", expectedAfter },
                { "baseline_matches_native_state", baselineMatchesState },
                { "after_matches_native_policy", afterMatchesPolicy },
                { "feature_dependency_membership_consistent", featureDependencyConsistent }
            });
        }

        List<object> featureDiffs = M3SketchReferenceConversionCountDiffs(
            featureBefore, featureAfter, selectedChangedFeatureKeys);
        List<object> dependencyDiffs = M3SketchReferenceConversionCountDiffs(
            dependencyBefore, dependencyAfter, selectedChangedDependencyKeys);
        bool nonSelectedFeatureMembershipPreserved = featureDiffs.Count == 0;
        bool nonSelectedDependenciesPreserved = dependencyDiffs.Count == 0;
        valid = valid && nonSelectedFeatureMembershipPreserved && nonSelectedDependenciesPreserved;
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "target_feature_tag", targetFeatureTag },
            { "target_feature_journal_id", targetFeatureId },
            { "selected_dimension_expression_membership", selectedRecords },
            { "changed_selected_expression_pair_count", selectedChangedFeatureKeys.Count },
            { "non_selected_feature_expression_membership_preserved", nonSelectedFeatureMembershipPreserved },
            { "non_selected_feature_expression_diffs", featureDiffs },
            { "non_selected_dependency_edges_preserved", nonSelectedDependenciesPreserved },
            { "non_selected_dependency_edge_diffs", dependencyDiffs },
            { "feature_inventory_valid", featureBeforeValid && featureAfterValid },
            { "dependency_inventory_valid", dependencyBeforeValid && dependencyAfterValid },
            { "membership_matches_expected", valid }
        };
        return valid;
    }

    private static Dictionary<string, int> M3SketchReferenceConversionFeatureExpressionCounts(
        Dictionary<string, M3SketchProjectionFeatureState> features, out bool valid)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        valid = features != null;
        if (features == null) return counts;
        foreach (M3SketchProjectionFeatureState feature in features.Values)
        {
            if (feature == null || String.IsNullOrEmpty(feature.Tag) || feature.ExpressionNames == null)
            {
                valid = false;
                continue;
            }
            foreach (string expressionName in feature.ExpressionNames)
            {
                if (String.IsNullOrEmpty(expressionName))
                {
                    valid = false;
                    continue;
                }
                string key = M3SketchReferenceConversionMembershipKey(feature.Tag, expressionName);
                int count;
                counts.TryGetValue(key, out count);
                counts[key] = count + 1;
            }
        }
        return counts;
    }

    private static Dictionary<string, int> M3SketchReferenceConversionDependencyCounts(
        Dictionary<string, object> snapshot, out bool valid)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        valid = snapshot != null;
        if (snapshot == null) return counts;
        List<object> dependencies;
        try { dependencies = SnapshotList(snapshot, "dependencies"); }
        catch { valid = false; return counts; }
        foreach (object value in dependencies)
        {
            Dictionary<string, object> edge = value as Dictionary<string, object>;
            object kindValue, sourceValue, targetValue;
            if (edge == null || !edge.TryGetValue("kind", out kindValue) ||
                !edge.TryGetValue("source_id", out sourceValue) || !edge.TryGetValue("target_id", out targetValue))
            {
                valid = false;
                continue;
            }
            string kind = Convert.ToString(kindValue, CultureInfo.InvariantCulture);
            string source = Convert.ToString(sourceValue, CultureInfo.InvariantCulture);
            string target = Convert.ToString(targetValue, CultureInfo.InvariantCulture);
            if (String.IsNullOrEmpty(kind) || String.IsNullOrEmpty(source) || String.IsNullOrEmpty(target))
            {
                valid = false;
                continue;
            }
            string key = M3SketchReferenceConversionDependencyKey(kind, source, target);
            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
        }
        return counts;
    }

    private static List<object> M3SketchReferenceConversionCountDiffs(Dictionary<string, int> before,
        Dictionary<string, int> after, HashSet<string> allowedKeys)
    {
        List<object> result = new List<object>();
        HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in before.Keys) keys.Add(key);
        foreach (string key in after.Keys) keys.Add(key);
        foreach (string key in keys)
        {
            if (allowedKeys != null && allowedKeys.Contains(key)) continue;
            int beforeCount = M3SketchReferenceConversionCount(before, key);
            int afterCount = M3SketchReferenceConversionCount(after, key);
            if (beforeCount == afterCount) continue;
            result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "membership_key", key }, { "before_count", beforeCount }, { "after_count", afterCount }
            });
        }
        return result;
    }

    private static string M3SketchReferenceConversionMembershipKey(string featureTag, string expressionName)
    {
        return (featureTag == null ? "" : featureTag.Length.ToString(CultureInfo.InvariantCulture)) + ":" +
            (featureTag ?? "") + (expressionName == null ? "" : expressionName.Length.ToString(CultureInfo.InvariantCulture)) + ":" +
            (expressionName ?? "");
    }

    private static string M3SketchReferenceConversionDependencyKey(string kind, string source, string target)
    {
        return (kind == null ? "" : kind.Length.ToString(CultureInfo.InvariantCulture)) + ":" + (kind ?? "") +
            (source == null ? "" : source.Length.ToString(CultureInfo.InvariantCulture)) + ":" + (source ?? "") +
            (target == null ? "" : target.Length.ToString(CultureInfo.InvariantCulture)) + ":" + (target ?? "");
    }

    private static int M3SketchReferenceConversionCount(Dictionary<string, int> counts, string key)
    {
        int value;
        return counts != null && key != null && counts.TryGetValue(key, out value) ? value : 0;
    }

    private static string M3SketchReferenceConversionExpressionProjection(
        Dictionary<string, object> snapshot, List<M3SketchReferenceConversionObjectState> objects)
    {
        Dictionary<string, object> copy = DeepCloneValue(snapshot) as Dictionary<string, object>;
        if (copy == null) throw new M2ModelException("MODEL_READ_FAILED", "F11 expression snapshot could not be normalized");
        M3SketchReferenceConversionNormalizePromotedExpressions(copy, objects);
        return M2Json.CanonicalText(SnapshotList(copy, "expressions"));
    }

    private static void M3SketchReferenceConversionNormalizePromotedExpressions(
        Dictionary<string, object> snapshot, List<M3SketchReferenceConversionObjectState> objects)
    {
        HashSet<string> expressionIds = M3SketchReferenceConversionPromotionExpressionIds(objects);
        if (expressionIds.Count == 0) return;
        List<object> expressions = SnapshotList(snapshot, "expressions");
        HashSet<string> found = new HashSet<string>(StringComparer.Ordinal);
        foreach (object value in expressions)
        {
            Dictionary<string, object> expression = value as Dictionary<string, object>;
            if (expression == null) throw new M2ModelException("MODEL_READ_FAILED", "F11 expression inventory contains an invalid record");
            object idValue;
            string id = expression.TryGetValue("journal_id", out idValue)
                ? Convert.ToString(idValue, CultureInfo.InvariantCulture) : null;
            if (id == null || !expressionIds.Contains(id)) continue;
            expression.Remove("right_hand_side");
            expression.Remove("value");
            found.Add(id);
        }
        if (!found.SetEquals(expressionIds))
            throw new M2ModelException("MODEL_READ_FAILED", "F11 selected expression was missing from the Part expression inventory");
    }

    private static HashSet<string> M3SketchReferenceConversionPromotionDimensionIds(
        List<M3SketchReferenceConversionObjectState> objects)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        if (objects != null)
            foreach (M3SketchReferenceConversionObjectState item in objects)
                if (M3SketchReferenceConversionIsReferenceToActivePromotion(item)) result.Add(item.Id);
        return result;
    }

    private static HashSet<string> M3SketchReferenceConversionPromotionDimensionTags(
        List<M3SketchReferenceConversionObjectState> objects)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        if (objects != null)
            foreach (M3SketchReferenceConversionObjectState item in objects)
                if (M3SketchReferenceConversionIsReferenceToActivePromotion(item)) result.Add(item.Tag);
        return result;
    }

    private static HashSet<string> M3SketchReferenceConversionPromotionExpressionIds(
        List<M3SketchReferenceConversionObjectState> objects)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        if (objects != null)
            foreach (M3SketchReferenceConversionObjectState item in objects)
                if (M3SketchReferenceConversionIsReferenceToActivePromotion(item))
                    result.Add(item.ExpressionId);
        return result;
    }

    private static bool M3SketchReferenceConversionIsReferenceToActivePromotion(
        M3SketchReferenceConversionObjectState item)
    {
        return item != null && item.Kind == "dimension" &&
            String.Equals(item.StateBefore, "reference", StringComparison.Ordinal) &&
            String.Equals(item.ExpectedAfter, "active", StringComparison.Ordinal);
    }

    private static Dictionary<string, object> CaptureM3SketchReferenceConversionSolver(Sketch sketch)
    {
        if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "F11 target Sketch is unavailable for solver status readback");
        int degreesOfFreedom;
        Sketch.Status status;
        try { status = sketch.GetStatus(out degreesOfFreedom); }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "could not read cached F11 Sketch status (" + ex.GetType().Name + ")");
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", status.ToString() },
            { "degrees_of_freedom", degreesOfFreedom },
            { "assessment", String.Equals(status.ToString(), "NotEvaluated", StringComparison.Ordinal)
                ? "NOT_EVALUATED" : "CACHED_STATUS_ONLY" },
            { "calculate_status_called", false }
        };
    }

    private static Dictionary<string, object> CaptureM3SketchReferenceConversionCalculatedSolver(Session session,
        Sketch sketch, Dictionary<string, object> operationEvidence, string evidenceStage)
    {
        if (session == null || sketch == null)
            throw new M2ModelException("MODEL_READ_FAILED", "F11 CalculateStatus requires an NX Session and active target Sketch");

        NXOpen.Preferences.SessionSketch sketchPreferences;
        bool originalFindMovableObjects;
        try
        {
            sketchPreferences = session.Preferences.Sketch;
            if (sketchPreferences == null) throw new InvalidOperationException("Session Preferences returned no Sketch preferences");
            originalFindMovableObjects = sketchPreferences.FindMovableObjects;
        }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED",
                "could not capture the session FindMovableObjects Sketch preference (" + ex.GetType().Name + ")");
        }

        bool temporarySetAttempted = false;
        bool temporaryReadSucceeded = false;
        bool temporaryFindMovableObjects = false;
        bool restorationAttempted = false;
        bool restorationReadSucceeded = false;
        bool restoredFindMovableObjects = false;
        bool restorationSucceeded = false;
        int degreesOfFreedom = -1;
        Sketch.Status status = Sketch.Status.Unknown;
        Exception calculationException = null;
        try
        {
            temporarySetAttempted = true;
            sketchPreferences.FindMovableObjects = true;
            temporaryFindMovableObjects = sketchPreferences.FindMovableObjects;
            temporaryReadSucceeded = true;
            if (!temporaryFindMovableObjects)
                throw new InvalidOperationException("FindMovableObjects did not read back true for F11 solver evaluation");
            sketch.CalculateStatus();
            status = sketch.GetStatus(out degreesOfFreedom);
        }
        catch (Exception ex) { calculationException = ex; }
        finally
        {
            restorationAttempted = true;
            try
            {
                sketchPreferences.FindMovableObjects = originalFindMovableObjects;
                restoredFindMovableObjects = sketchPreferences.FindMovableObjects;
                restorationReadSucceeded = true;
                restorationSucceeded = restoredFindMovableObjects == originalFindMovableObjects;
            }
            catch { restorationSucceeded = false; }

            Dictionary<string, object> preferenceEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "original", originalFindMovableObjects },
                { "temporary_set_attempted", temporarySetAttempted },
                { "temporary_requested", true },
                { "temporary_read_succeeded", temporaryReadSucceeded },
                { "temporary_readback", temporaryReadSucceeded ? (object)temporaryFindMovableObjects : null },
                { "restore_attempted", restorationAttempted },
                { "restored_read_succeeded", restorationReadSucceeded },
                { "restored_readback", restorationReadSucceeded ? (object)restoredFindMovableObjects : null },
                { "restoration_succeeded", restorationSucceeded },
                { "preferences_saved_to_user_configuration", false }
            };
            if (operationEvidence != null)
            {
                operationEvidence[evidenceStage + "_find_movable_objects"] = preferenceEvidence;
                object priorRestoreValue;
                bool priorRestoreSucceeded = !operationEvidence.TryGetValue(
                    "find_movable_objects_restore_succeeded", out priorRestoreValue) ||
                    !(priorRestoreValue is bool) || (bool)priorRestoreValue;
                operationEvidence["find_movable_objects_restore_succeeded"] = priorRestoreSucceeded && restorationSucceeded;
            }
        }

        if (!restorationSucceeded)
            throw new M2ModelException("SKETCH_PREFERENCE_RESTORE_FAILED",
                "NX session FindMovableObjects preference could not be restored and read back; model outcome must remain UNKNOWN");
        if (calculationException != null)
            throw new M2ModelException("SKETCH_SOLVER_FAILED",
                "Sketch.CalculateStatus failed after temporary FindMovableObjects enable (" + calculationException.GetType().Name + ")");

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", status.ToString() },
            { "degrees_of_freedom", degreesOfFreedom },
            { "assessment", String.Equals(status.ToString(), "NotEvaluated", StringComparison.Ordinal)
                ? "NOT_EVALUATED" : M3SketchReferenceConversionSolverSeverity(status.ToString()) > 0
                    ? "SOLVER_ERROR_STATUS" : M3SketchReferenceConversionSolverIsEvaluatedValid(status.ToString(), degreesOfFreedom)
                        ? "CALCULATED_STATUS_ONLY" : "UNSUPPORTED_SOLVER_STATUS" },
            { "calculate_status_called", true },
            { "calculate_status_succeeded", true },
            { "find_movable_objects_before", originalFindMovableObjects },
            { "find_movable_objects_temporary", temporaryFindMovableObjects },
            { "find_movable_objects_restored", restoredFindMovableObjects },
            { "find_movable_objects_restore_succeeded", restorationSucceeded },
            { "preferences_saved_to_user_configuration", false }
        };
    }

    private static int M3SketchReferenceConversionSolverSeverity(string status)
    {
        if (String.IsNullOrEmpty(status)) return 0;
        if (status.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
            status.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (status.IndexOf("Inconsistent", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
        if (status.IndexOf("OverConstrained", StringComparison.OrdinalIgnoreCase) >= 0 ||
            status.IndexOf("Over Constrained", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
        return 0;
    }

    private static bool M3SketchReferenceConversionSolverDidNotWorsen(
        Dictionary<string, object> before, Dictionary<string, object> calculatedAfter,
        Dictionary<string, object> finalAfter, out Dictionary<string, object> evidence)
    {
        string beforeStatus = M3SketchReferenceConversionSolverStatus(before);
        string calculatedStatus = M3SketchReferenceConversionSolverStatus(calculatedAfter);
        string finalStatus = M3SketchReferenceConversionSolverStatus(finalAfter);
        int beforeSeverity = M3SketchReferenceConversionSolverSeverity(beforeStatus);
        int calculatedSeverity = M3SketchReferenceConversionSolverSeverity(calculatedStatus);
        int finalSeverity = M3SketchReferenceConversionSolverSeverity(finalStatus);
        int calculatedDof = M3SketchReferenceConversionSolverDofAsInt(calculatedAfter);
        int finalDof = M3SketchReferenceConversionSolverDofAsInt(finalAfter);
        bool calculatedValid = M3SketchReferenceConversionSolverIsEvaluatedValid(calculatedStatus, calculatedDof);
        bool finalValid = M3SketchReferenceConversionSolverIsEvaluatedValid(finalStatus, finalDof);
        bool notWorse = calculatedValid && finalValid && calculatedSeverity <= beforeSeverity &&
            finalSeverity <= beforeSeverity && finalSeverity <= calculatedSeverity;
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "solver_before", before },
            { "solver_calculated_after", calculatedAfter },
            { "solver_after", finalAfter },
            { "solver_before_status", beforeStatus },
            { "solver_calculated_status_after", calculatedStatus },
            { "solver_status_after", finalStatus },
            { "solver_degrees_of_freedom_before", M3SketchReferenceConversionSolverDof(before) },
            { "solver_degrees_of_freedom_after", M3SketchReferenceConversionSolverDof(finalAfter) },
            { "solver_assessment", String.Equals(calculatedStatus, "NotEvaluated", StringComparison.Ordinal) ||
                String.Equals(finalStatus, "NotEvaluated", StringComparison.Ordinal)
                    ? "NOT_EVALUATED" : notWorse ? "NO_NEW_SOLVER_ERROR" : "NEW_OR_WORSENED_SOLVER_ERROR" },
            { "solver_error_severity_before", beforeSeverity },
            { "solver_error_severity_calculated_after", calculatedSeverity },
            { "solver_error_severity_after", finalSeverity },
            { "calculated_solver_status_valid", calculatedValid },
            { "final_solver_status_valid", finalValid },
            { "solver_status_worsened", !notWorse },
            { "degrees_of_freedom_is_informational", true }
        };
        return notWorse;
    }

    private static string M3SketchReferenceConversionSolverStatus(Dictionary<string, object> solver)
    {
        object value;
        return solver != null && solver.TryGetValue("status", out value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) : String.Empty;
    }

    private static object M3SketchReferenceConversionSolverDof(Dictionary<string, object> solver)
    {
        object value;
        return solver != null && solver.TryGetValue("degrees_of_freedom", out value) ? value : null;
    }

    private static int M3SketchReferenceConversionSolverDofAsInt(Dictionary<string, object> solver)
    {
        object value = M3SketchReferenceConversionSolverDof(solver);
        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return Int32.MinValue; }
    }

    private static bool M3SketchReferenceConversionSolverIsEvaluatedValid(string status, int degreesOfFreedom)
    {
        if (String.Equals(status, "UnderConstrained", StringComparison.Ordinal)) return degreesOfFreedom >= 0;
        if (String.Equals(status, "WellConstrained", StringComparison.Ordinal)) return degreesOfFreedom == 0;
        return false;
    }

    private static bool M3SketchReferenceConversionSolverSnapshotsEqual(
        Dictionary<string, object> expected, Dictionary<string, object> actual)
    {
        return expected != null && actual != null &&
            String.Equals(M3SketchReferenceConversionSolverStatus(expected), M3SketchReferenceConversionSolverStatus(actual), StringComparison.Ordinal) &&
            Convert.ToString(M3SketchReferenceConversionSolverDof(expected), CultureInfo.InvariantCulture) ==
                Convert.ToString(M3SketchReferenceConversionSolverDof(actual), CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, object> ConfigureM3SketchReferenceConversionWorkRegion(
        Part part, out bool commitAttempted, out bool commitSucceeded)
    {
        if (part == null) throw new M2ModelException("MODEL_READ_FAILED", "F11 Work Part is unavailable for Work Region configuration");
        SketchWorkRegionBuilder builder = null;
        Exception operationException = null;
        Exception destroyException = null;
        Dictionary<string, object> result = null;
        commitAttempted = false;
        commitSucceeded = false;
        string builderDefaultScope = null;
        string scopeAfterCommit = null;
        try
        {
            builder = part.Sketches.CreateWorkRegionBuilder();
            if (builder == null) throw new M2ModelException("WORK_REGION_UNAVAILABLE", "NX returned no Sketch Work Region builder");
            builderDefaultScope = builder.Scope.ToString();
            builder.Scope = SketchWorkRegionBuilder.ScopeType.EntireSketch;
            commitAttempted = true;
            builder.Commit();
            commitSucceeded = true;
            scopeAfterCommit = builder.Scope.ToString();
            if (builder.Scope != SketchWorkRegionBuilder.ScopeType.EntireSketch)
                throw new M2ModelException("WORK_REGION_UNAVAILABLE", "NX did not retain the requested EntireSketch solver scope");
            result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "builder_default_scope", builderDefaultScope },
                { "scope_requested", "EntireSketch" },
                { "scope_after_commit", scopeAfterCommit },
                { "unrestricted", true },
                { "commit_attempted", commitAttempted },
                { "commit_succeeded", commitSucceeded },
                { "persistent_prior_scope_observable", false },
                { "scope_evidence", "same_builder_scope_readback_after_commit" }
            };
        }
        catch (Exception ex) { operationException = ex; }

        if (builder != null)
        {
            try { builder.Destroy(); }
            catch (Exception ex) { destroyException = ex; }
        }
        if (operationException != null)
        {
            M2ModelException model = operationException as M2ModelException;
            if (model != null) throw model;
            throw new M2ModelException("WORK_REGION_UNAVAILABLE",
                "NX EntireSketch solver scope could not be committed and read back (" + operationException.GetType().Name + ")");
        }
        if (destroyException != null)
            throw new M2ModelException("WORK_REGION_UNAVAILABLE",
                "NX Sketch Work Region builder could not be destroyed (" + destroyException.GetType().Name + ")");
        return result;
    }

    private static void CaptureM3SketchReferenceConversionContourCandidates(Sketch sketch,
        Dictionary<string, int> referenceStates, out List<object> candidates, out string candidatesText)
    {
        if (sketch == null || referenceStates == null)
            throw new M2ModelException("MODEL_READ_FAILED", "F11 profile candidate capture requires a Sketch and UF reference state map");
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null during F11 candidate capture");
        UFSession uf = UFSession.GetUFSession();
        if (uf == null) throw new M2ModelException("MODEL_READ_FAILED", "UF session is unavailable during F11 candidate capture");
        List<Dictionary<string, object>> all = new List<Dictionary<string, object>>();
        HashSet<string> seenTags = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (NXObject item in geometry)
        {
            if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Sketch geometry contains null during F11 candidate capture");
            Curve curve = item as Curve;
            if (curve == null) continue;
            string tag = curve.Tag.ToString();
            string id = RequiredJournalId(curve, "F11 profile candidate curve");
            if (String.IsNullOrEmpty(tag) || !seenTags.Add(tag) || !seenIds.Add(id))
                throw new M2ModelException("MODEL_READ_FAILED", "Sketch curve inventory has duplicate Tags or JournalIdentifiers during F11 candidate capture");
            UFSket.ReferenceStatus status;
            try { uf.Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status); }
            catch (Exception ex)
            {
                throw new M2ModelException("MODEL_READ_FAILED", "UF.Sket.AskReferenceStatus failed during F11 candidate capture (" + ex.GetType().Name + ")");
            }
            int value = Convert.ToInt32(status, CultureInfo.InvariantCulture);
            int captured;
            if (!referenceStates.TryGetValue(tag, out captured) || captured != value)
                throw new M2ModelException("MODEL_READ_FAILED", "F11 candidate status differs from the complete UF reference-state snapshot");
            string currentState = M3SketchReferenceConversionCurveState(status);
            all.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "curve_id", id }, { "tag", tag }, { "reference_status", currentState },
                { "reference_status_value", value }, { "candidate", status == UFSket.ReferenceStatus.Active },
                { "assessment", "UF_STATUS_ONLY_NOT_SECTION_VALIDATION" }
            });
        }
        all.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            return String.CompareOrdinal(Convert.ToString(left["tag"], CultureInfo.InvariantCulture),
                Convert.ToString(right["tag"], CultureInfo.InvariantCulture));
        });
        candidates = new List<object>();
        foreach (Dictionary<string, object> item in all)
            if (Boolean.Equals(item["candidate"], true)) candidates.Add(item);
        candidatesText = M2Json.CanonicalText(candidates);
    }

    private static string CaptureM3SketchReferenceConversionDimensionAssociations(Part part,
        List<M3SketchReferenceConversionObjectState> selectedObjects)
    {
        if (part == null) throw new M2ModelException("MODEL_READ_FAILED", "Work Part is unavailable during F11 dimension association capture");
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory returned null during F11 dimension association capture");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        HashSet<string> seenConstraints = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> selectedDimensionTags = new HashSet<string>(StringComparer.Ordinal);
        if (selectedObjects != null)
        {
            foreach (M3SketchReferenceConversionObjectState selected in selectedObjects)
            {
                if (selected == null || !String.Equals(selected.Kind, "dimension", StringComparison.Ordinal)) continue;
                if (String.IsNullOrEmpty(selected.Tag) || !selectedDimensionTags.Add(selected.Tag))
                    throw new M2ModelException("INVALID_REQUEST", "F11 selected dimension Tag is missing or duplicated");
            }
        }
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Sketch inventory contains null during F11 dimension association capture");
            foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
            {
                if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
                SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
                if (constraints == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch constraints returned null during F11 dimension association capture");
                foreach (SketchConstraint item in constraints)
                {
                    SketchDimensionalConstraint dimension = item as SketchDimensionalConstraint;
                    if (dimension == null) continue;
                    string constraintTag = dimension.Tag.ToString();
                    if (String.IsNullOrEmpty(constraintTag) || !seenConstraints.Add(constraintTag))
                        throw new M2ModelException("MODEL_READ_FAILED", "dimensional constraint Tag is missing or duplicated during F11 association capture");
                    Dimension associated = dimension.AssociatedDimension;
                    Expression expression = dimension.AssociatedExpression;
                    bool selected = selectedDimensionTags.Contains(constraintTag);
                    if (associated == null || associated.OwningPart == null || associated.OwningPart.Tag != part.Tag ||
                        associated.IsOccurrence || !sketch.IsSketchDimension(associated) || !sketch.IsSketchConstraint(dimension))
                        throw new M2ModelException("UNSUPPORTED_DIMENSION",
                            "F11 cannot prove a dimensional constraint's same-part Sketch Dimension association");
                    if (selected && expression == null)
                        throw new M2ModelException("UNSUPPORTED_DIMENSION",
                            "selected F11 HorizontalDim/VerticalDim requires its native AssociatedExpression");

                    Sketch.DimensionGeometry[] dimensionGeometry = dimension.GetDimensionGeometry();
                    if (dimensionGeometry == null)
                        throw new M2ModelException("MODEL_READ_FAILED", "F11 dimension operands returned null");
                    List<object> operands = new List<object>();
                    foreach (Sketch.DimensionGeometry operand in dimensionGeometry)
                    {
                        if (operand.Geometry == null || operand.Geometry.OwningPart == null ||
                            operand.Geometry.OwningPart.Tag != part.Tag || operand.Geometry.IsOccurrence)
                            throw new M2ModelException("UNSUPPORTED_DIMENSION",
                                "F11 dimensional operand is not a same-part native Sketch object");
                        operands.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "geometry_tag", operand.Geometry.Tag.ToString() },
                            { "geometry_journal_id", RequiredJournalId(operand.Geometry, "F11 dimension operand") },
                            { "geometry_type", operand.Geometry.GetType().Name },
                            { "association", operand.AssocType.ToString() },
                            { "association_value", Convert.ToString(operand.AssocValue, CultureInfo.InvariantCulture) }
                        });
                    }
                    Dictionary<string, object> expressionFlags = null;
                    if (expression != null)
                    {
                        if (expression.OwningPart == null || expression.OwningPart.Tag != part.Tag)
                            throw new M2ModelException("UNSUPPORTED_DIMENSION",
                                "dimensional AssociatedExpression is not owned by the bound Work Part");
                        expressionFlags = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "is_measurement_expression", expression.IsMeasurementExpression },
                            { "is_geometric_expression", expression.IsGeometricExpression },
                            { "is_no_edit", expression.IsNoEdit },
                            { "is_no_update", expression.IsNoUpdate },
                            { "is_rhs_locked_from_edit", expression.IsRightHandSideLockedFromEdit },
                            { "is_user_locked", expression.IsUserLocked },
                            { "is_user_specified", expression.IsUserSpecified }
                        };
                    }
                    records.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "sketch_tag", sketch.Tag.ToString() },
                        { "constraint_tag", constraintTag },
                        { "constraint_journal_id", RequiredJournalId(dimension, "F11 dimensional constraint") },
                        { "constraint_type", dimension.ConstraintType.ToString() },
                        { "associated_dimension_tag", associated.Tag.ToString() },
                        { "associated_dimension_journal_id", RequiredJournalId(associated, "F11 AssociatedDimension") },
                        { "associated_dimension_type", associated.GetType().Name },
                        { "associated_dimension_owner_part_tag", associated.OwningPart.Tag.ToString() },
                        { "associated_dimension_is_sketch_dimension", sketch.IsSketchDimension(associated) },
                        { "expression_present", expression != null },
                        { "expression_tag", expression == null ? null : expression.Tag.ToString() },
                        { "expression_journal_id", expression == null ? null : RequiredJournalId(expression, "F11 dimension expression") },
                        { "expression_name", expression == null ? null : expression.Name },
                        { "expression_units", expression == null || expression.Units == null ? null : expression.Units.Symbol },
                        { "expression_flags", expressionFlags },
                        { "dimension_operands", operands }
                    });
                }
            }
        }
        foreach (string selectedTag in selectedDimensionTags)
            if (!seenConstraints.Contains(selectedTag))
                throw new M2ModelException("MODEL_READ_FAILED", "an explicitly selected F11 dimension is missing from the native constraint inventory");
        records.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            return String.CompareOrdinal(Convert.ToString(left["constraint_tag"], CultureInfo.InvariantCulture),
                Convert.ToString(right["constraint_tag"], CultureInfo.InvariantCulture));
        });
        return M2Json.CanonicalText(records);
    }

    private static string CaptureM3SketchReferenceConversionSourceAssociations(Part part,
        Dictionary<string, M3SketchProjectionFeatureState> featureStates)
    {
        if (part == null || featureStates == null)
            throw new M2ModelException("MODEL_READ_FAILED", "F11 source association capture requires a Part and feature inventory");
        List<M3SketchProjectionFeatureState> ordered = new List<M3SketchProjectionFeatureState>(featureStates.Values);
        ordered.Sort(delegate(M3SketchProjectionFeatureState left, M3SketchProjectionFeatureState right)
        {
            return String.CompareOrdinal(left.Tag, right.Tag);
        });
        List<object> associations = new List<object>();
        foreach (M3SketchProjectionFeatureState feature in ordered)
        {
            if (!String.Equals(feature.Type, "CPROJ", StringComparison.OrdinalIgnoreCase)) continue;
            Dictionary<string, object> section = M3SketchProjectionReadCommittedSectionSources(part,
                feature.Feature, new M3SketchProjectionSourceState[0]);
            List<object> sourceElements = section["source_elements"] as List<object>;
            List<string> sourceTags = section["source_tags"] as List<string>;
            if (sourceElements == null || sourceTags == null)
                throw new M2ModelException("SOURCE_ASSOCIATION_UNVERIFIED", "committed CPROJ Section returned incomplete source association data");
            associations.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "feature_tag", feature.Tag },
                { "feature_journal_id", feature.Id },
                { "associativity", section.ContainsKey("associativity") ? section["associativity"] : null },
                { "project_as_dumb_fixed_curves", section.ContainsKey("project_as_dumb_fixed_curves")
                    ? section["project_as_dumb_fixed_curves"] : null },
                { "source_tags", sourceTags },
                { "source_elements", sourceElements },
                { "section_output_curve_tags", section.ContainsKey("section_output_curve_tags")
                    ? section["section_output_curve_tags"] : new List<string>() }
            });
        }
        return M2Json.CanonicalText(associations);
    }

    private static int M3SketchReferenceConversionConstraintCount(
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraints)
    {
        int count = 0;
        if (constraints != null)
            foreach (Dictionary<string, CornerConstraintSnapshot> sketch in constraints.Values)
                if (sketch != null) count += sketch.Count;
        return count;
    }

    private static Dictionary<string, object> M3SketchReferenceConversionStateEvidence(
        M3SketchReferenceConversionLiveState live)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "semantic", live == null ? null : live.Semantic },
            { "full_geometry", live == null ? null : live.FullGeometry },
            { "geometry_count", live == null || live.Geometry == null ? 0 : live.Geometry.Count },
            { "constraint_count", live == null ? 0 : M3SketchReferenceConversionConstraintCount(live.Constraints) },
            { "reference_curve_count", live == null || live.ReferenceStates == null ? 0 : live.ReferenceStates.Count },
            { "feature_inventory", live == null ? null : live.FeatureInventory },
            { "history", live == null ? null : live.History },
            { "expressions", live == null ? null : live.Expressions },
            { "dimension_associations", live == null ? null : live.DimensionAssociations },
            { "source_associations", live == null ? null : live.SourceAssociations },
            { "dirty", live != null && live.Dirty },
            { "active_sketch_tag", live == null ? null : live.ActiveSketchTag },
            { "solver", live == null ? null : live.Solver },
            { "contour_candidates", live == null ? new List<object>() : live.ContourCandidates }
        };
    }

    private static bool M3SketchReferenceConversionMatchesPrepare(
        M3SketchReferenceConversionPlanState plan, M3SketchReferenceConversionLiveState current,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> bodyEvidence;
        bool bodies = BodyMeasuresEqual(plan.BodiesBefore, current.Bodies, out bodyEvidence);
        bool semantic = String.Equals(plan.SemanticBefore, current.Semantic, StringComparison.Ordinal);
        bool geometry = String.Equals(plan.FullGeometryBefore, current.FullGeometry, StringComparison.Ordinal) &&
            CornerGeometryMapsEqual(plan.GeometryBefore, current.Geometry);
        bool constraints = M3SketchReferenceConversionConstraintMapsEqual(plan.ConstraintsBefore, current.Constraints, null, null);
        bool frames = OffsetStringMapsEqual(plan.FramesBefore, current.Frames);
        bool references = OffsetIntMapsEqual(plan.ReferenceStatesBefore, current.ReferenceStates);
        bool dimensionAssociations = String.Equals(plan.DimensionAssociationsBefore, current.DimensionAssociations, StringComparison.Ordinal);
        bool sourceAssociations = String.Equals(plan.SourceAssociationsBefore, current.SourceAssociations, StringComparison.Ordinal);
        bool history = String.Equals(plan.HistoryBefore, current.History, StringComparison.Ordinal);
        bool features = String.Equals(plan.FeatureInventoryBefore, current.FeatureInventory, StringComparison.Ordinal) &&
            FeatureTagsMatch(plan.FeatureTagsBefore, current.FeatureTags);
        bool expressions = String.Equals(plan.ExpressionsBefore, current.Expressions, StringComparison.Ordinal);
        bool curves = String.Equals(plan.CurveInventoryBeforeText, current.CurveInventoryText, StringComparison.Ordinal);
        bool helpers = PatternHelperTagInventoriesEqual(plan.HelperTagsBefore, current.HelperTags);
        bool dirty = plan.DirtyBefore == current.Dirty;
        bool active = String.Equals(plan.ActiveSketchTagBefore, current.ActiveSketchTag, StringComparison.Ordinal);
        evidence["semantic_matches_prepare"] = semantic;
        evidence["geometry_matches_prepare"] = geometry;
        evidence["constraints_match_prepare"] = constraints;
        evidence["frames_match_prepare"] = frames;
        evidence["reference_status_matches_prepare"] = references;
        evidence["dimension_associations_match_prepare"] = dimensionAssociations;
        evidence["source_associations_match_prepare"] = sourceAssociations;
        evidence["history_matches_prepare"] = history;
        evidence["features_match_prepare"] = features;
        evidence["expressions_match_prepare"] = expressions;
        evidence["global_curve_inventory_matches_prepare"] = curves;
        evidence["helper_inventory_matches_prepare"] = helpers;
        evidence["bodies_match_prepare"] = bodies;
        evidence["body_measurements"] = bodyEvidence;
        evidence["dirty_matches_prepare"] = dirty;
        evidence["active_sketch_matches_prepare"] = active;
        return semantic && geometry && constraints && frames && references && dimensionAssociations &&
            sourceAssociations && history && features && expressions && curves && helpers && bodies && dirty && active;
    }

    private static void M3SketchReferenceConversionCaptureAfter(
        M3SketchReferenceConversionPlanState plan, M3SketchReferenceConversionLiveState live)
    {
        plan.DirtyAfter = live.Dirty;
        plan.DirtyAfterCaptured = true;
        plan.SemanticAfter = live.Semantic;
        plan.SemanticAfterNormalized = live.SemanticNormalized;
        plan.FullGeometryAfter = live.FullGeometry;
        plan.GeometryAfter = live.Geometry;
        plan.ConstraintsAfter = live.Constraints;
        plan.FramesAfter = live.Frames;
        plan.ReferenceStatesAfter = live.ReferenceStates;
        plan.DimensionAssociationsAfter = live.DimensionAssociations;
        plan.SourceAssociationsAfter = live.SourceAssociations;
        plan.BodiesAfter = live.Bodies;
        plan.FeatureTagsAfter = live.FeatureTags;
        plan.FeatureStatesAfter = live.FeatureStates;
        plan.FeatureInventoryAfter = live.FeatureInventory;
        plan.FeatureInventoryAfterNormalized = live.FeatureInventoryNormalized;
        plan.HistoryAfter = live.History;
        plan.HistoryAfterNormalized = live.HistoryNormalized;
        plan.ExpressionsAfter = live.Expressions;
        plan.ExpressionsAfterNormalized = live.ExpressionsNormalized;
        plan.CurveInventoryAfter = live.CurveInventory;
        plan.CurveInventoryAfterText = live.CurveInventoryText;
        plan.HelperTagsAfter = live.HelperTags;
        plan.ContourCandidatesAfter = live.ContourCandidates;
        plan.ContourCandidatesAfterText = live.ContourCandidatesText;
    }

    private static Dictionary<string, object> EvaluateM3SketchReferenceConversion(
        M3SketchReferenceConversionPlanState plan, M3SketchReferenceConversionLiveState live,
        List<M3SketchReferenceConversionObjectState> objectsAfter,
        Dictionary<string, object> factoryEvidence, bool captureAfterBaseline)
    {
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> measured = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> bodyEvidence;
        Dictionary<string, object> dimensionEvidence;
        Dictionary<string, object> referenceEvidence;
        Dictionary<string, object> targetEvidence;
        HashSet<string> selectedDimensionTags = M3SketchReferenceConversionSelectedDimensionTags(plan.Objects);
        HashSet<string> promotedDimensionTags = M3SketchReferenceConversionPromotionDimensionTags(plan.Objects);
        bool bodies = BodyMeasuresEqual(plan.BodiesBefore, live.Bodies, out bodyEvidence);
        bool geometry = String.Equals(plan.FullGeometryBefore, live.FullGeometry, StringComparison.Ordinal) &&
            CornerGeometryMapsEqual(plan.GeometryBefore, live.Geometry);
        bool constraints = M3SketchReferenceConversionConstraintMapsEqual(
            plan.ConstraintsBefore, live.Constraints, selectedDimensionTags, promotedDimensionTags);
        bool selectedDimensions = M3SketchReferenceConversionSelectedDimensionsMatch(plan.Objects, objectsAfter,
            plan.TargetState, out dimensionEvidence);
        constraints = constraints && selectedDimensions;
        bool frames = OffsetStringMapsEqual(plan.FramesBefore, live.Frames);
        bool references = M3SketchReferenceConversionReferenceStatesMatch(plan, live.ReferenceStates,
            out referenceEvidence);
        bool objectStates = M3SketchReferenceConversionObjectsHaveExpectedState(plan.Objects, objectsAfter,
            plan.TargetState, out targetEvidence);
        bool nativeInputs = factoryEvidence != null && M3SketchReferenceConversionFactoryInputsMatch(plan, factoryEvidence);
        bool targets = objectStates && selectedDimensions && nativeInputs;
        bool dimensionAssociations = String.Equals(plan.DimensionAssociationsBefore, live.DimensionAssociations, StringComparison.Ordinal);
        bool sourceAssociations = String.Equals(plan.SourceAssociationsBefore, live.SourceAssociations, StringComparison.Ordinal) && dimensionAssociations;
        Dictionary<string, object> expressionMembershipEvidence;
        bool expressionMembership = M3SketchReferenceConversionFeatureExpressionMembershipMatches(
            plan.FeatureTag, plan.FeatureId, plan.FeatureStatesBefore, live.FeatureStates,
            plan.SemanticSnapshotBefore, live.Capture == null ? null : live.Capture.Snapshot,
            plan.Objects, out expressionMembershipEvidence);
        bool history = String.Equals(plan.SemanticBeforeNormalized, live.SemanticNormalized, StringComparison.Ordinal) &&
            String.Equals(plan.HistoryBeforeNormalized, live.HistoryNormalized, StringComparison.Ordinal) &&
            String.Equals(plan.FeatureInventoryBeforeNormalized, live.FeatureInventoryNormalized, StringComparison.Ordinal) &&
            FeatureTagsMatch(plan.FeatureTagsBefore, live.FeatureTags) &&
            String.Equals(plan.ExpressionsBeforeNormalized, live.ExpressionsNormalized, StringComparison.Ordinal) &&
            (captureAfterBaseline || String.Equals(plan.ExpressionsAfterNormalized,
                live.ExpressionsNormalized, StringComparison.Ordinal)) && expressionMembership;
        bool nonTargets = references &&
            String.Equals(plan.CurveInventoryBeforeText, live.CurveInventoryText, StringComparison.Ordinal) &&
            PatternHelperTagInventoriesEqual(plan.HelperTagsBefore, live.HelperTags);
        bool solver = false;
        Dictionary<string, object> solverEvidence = null;
        if (plan.SolverBefore != null && plan.SolverCalculatedAfter != null)
            solver = M3SketchReferenceConversionSolverDidNotWorsen(plan.SolverBefore,
                plan.SolverCalculatedAfter, live.Solver, out solverEvidence);
        else
        {
            solverEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "solver_assessment", "UNAVAILABLE" }, { "solver_status_worsened", true }
            };
        }
        if (captureAfterBaseline)
        {
            plan.SemanticAfter = live.Semantic;
            plan.SemanticAfterNormalized = live.SemanticNormalized;
            plan.FullGeometryAfter = live.FullGeometry;
            plan.GeometryAfter = live.Geometry;
            plan.ConstraintsAfter = live.Constraints;
            plan.FramesAfter = live.Frames;
            plan.ReferenceStatesAfter = live.ReferenceStates;
            plan.DimensionAssociationsAfter = live.DimensionAssociations;
            plan.SourceAssociationsAfter = live.SourceAssociations;
            plan.BodiesAfter = live.Bodies;
            plan.FeatureTagsAfter = live.FeatureTags;
            plan.FeatureStatesAfter = live.FeatureStates;
            plan.FeatureInventoryAfter = live.FeatureInventory;
            plan.FeatureInventoryAfterNormalized = live.FeatureInventoryNormalized;
            plan.HistoryAfter = live.History;
            plan.HistoryAfterNormalized = live.HistoryNormalized;
            plan.ExpressionsAfter = live.Expressions;
            plan.ExpressionsAfterNormalized = live.ExpressionsNormalized;
            plan.CurveInventoryAfter = live.CurveInventory;
            plan.CurveInventoryAfterText = live.CurveInventoryText;
            plan.HelperTagsAfter = live.HelperTags;
            plan.ContourCandidatesAfter = live.ContourCandidates;
            plan.ContourCandidatesAfterText = live.ContourCandidatesText;
        }
        bool profileCandidates = live.ContourCandidates != null && live.ContourCandidatesText != null &&
            String.Equals(live.ContourCandidatesText, M2Json.CanonicalText(live.ContourCandidates), StringComparison.Ordinal);
        bool dirty;
        if (captureAfterBaseline)
        {
            plan.DirtyAfter = live.Dirty;
            plan.DirtyAfterCaptured = true;
            dirty = true;
        }
        else dirty = plan.DirtyAfter == live.Dirty;

        measured["reference_conversion_targets"] = targetEvidence;
        targetEvidence["native_inputs_match_factory"] = nativeInputs;
        targetEvidence["factory_evidence"] = factoryEvidence;
        measured["sketch_geometry"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "full_geometry_preserved", String.Equals(plan.FullGeometryBefore, live.FullGeometry, StringComparison.Ordinal) },
            { "native_sketch_geometry_preserved", CornerGeometryMapsEqual(plan.GeometryBefore, live.Geometry) },
            { "geometry_count_before", plan.GeometryBefore.Count }, { "geometry_count_after", live.Geometry.Count }
        };
        measured["sketch_constraints"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "all_non_target_constraints_preserved", M3SketchReferenceConversionConstraintMapsEqual(
                plan.ConstraintsBefore, live.Constraints, selectedDimensionTags, promotedDimensionTags) },
            { "selected_dimension_state_matches", selectedDimensions },
            { "dimension_details", dimensionEvidence },
            { "constraint_count_before", M3SketchReferenceConversionConstraintCount(plan.ConstraintsBefore) },
            { "constraint_count_after", M3SketchReferenceConversionConstraintCount(live.Constraints) }
        };
        measured["sketch_frames"] = new Dictionary<string, object>(StringComparer.Ordinal)
        { { "preserved", frames } };
        measured["source_associations"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "dimension_associations_preserved", dimensionAssociations },
            { "committed_projection_source_associations_preserved", String.Equals(
                plan.SourceAssociationsBefore, live.SourceAssociations, StringComparison.Ordinal) },
            { "before", plan.SourceAssociationsBefore }, { "after", live.SourceAssociations }
        };
        measured["non_target_geometry"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "unselected_reference_states_preserved", references },
            { "complete_part_curve_inventory_preserved", String.Equals(plan.CurveInventoryBeforeText, live.CurveInventoryText, StringComparison.Ordinal) },
            { "part_helper_inventory_preserved", PatternHelperTagInventoriesEqual(plan.HelperTagsBefore, live.HelperTags) },
            { "reference_status_details", referenceEvidence }
        };
        measured["history"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "semantic_delta_limited_to_requested_states", String.Equals(plan.SemanticBeforeNormalized, live.SemanticNormalized, StringComparison.Ordinal) },
            { "feature_history_preserved_except_selected_expression_membership", String.Equals(plan.HistoryBeforeNormalized, live.HistoryNormalized, StringComparison.Ordinal) },
            { "feature_inventory_preserved_except_selected_expression_membership", String.Equals(plan.FeatureInventoryBeforeNormalized, live.FeatureInventoryNormalized, StringComparison.Ordinal) },
            { "feature_order_preserved", FeatureTagsMatch(plan.FeatureTagsBefore, live.FeatureTags) },
            { "selected_expression_membership", expressionMembershipEvidence },
            { "raw_feature_history_before", plan.HistoryBefore },
            { "raw_feature_history_after", live.History },
            { "raw_feature_inventory_before", plan.FeatureInventoryBefore },
            { "raw_feature_inventory_after", live.FeatureInventory },
            { "expression_state_preserved", String.Equals(plan.ExpressionsBeforeNormalized,
                live.ExpressionsNormalized, StringComparison.Ordinal) },
            { "expression_inventory_raw_before", plan.ExpressionsBefore },
            { "expression_inventory_raw_after", live.Expressions }
        };
        measured["bodies"] = bodyEvidence;
        measured["sketch_solver_status"] = solverEvidence;
        measured["profile_candidates"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "basis", "UF_reference_status_Active_only" },
            { "actual_section_validation_performed", false },
            { "section_constructed", false },
            { "candidates_before", plan.ContourCandidatesBefore },
            { "candidates_after", live.ContourCandidates },
            { "candidate_count_before", plan.ContourCandidatesBefore == null ? 0 : plan.ContourCandidatesBefore.Count },
            { "candidate_count_after", live.ContourCandidates == null ? 0 : live.ContourCandidates.Count }
        };
        measured["dirty_state"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "dirty_before", plan.DirtyBefore },
            { "dirty_after", live.Dirty },
            { "matches_execute", dirty }
        };

        checks["reference_conversion_targets"] = VerificationCheck(targets ? "VERIFIED" : "MISMATCH",
            measured["reference_conversion_targets"] as Dictionary<string, object>);
        checks["sketch_geometry"] = VerificationCheck(geometry ? "VERIFIED" : "MISMATCH",
            measured["sketch_geometry"] as Dictionary<string, object>);
        checks["sketch_constraints"] = VerificationCheck(constraints ? "VERIFIED" : "MISMATCH",
            measured["sketch_constraints"] as Dictionary<string, object>);
        checks["sketch_frames"] = VerificationCheck(frames ? "VERIFIED" : "MISMATCH",
            measured["sketch_frames"] as Dictionary<string, object>);
        checks["source_associations"] = VerificationCheck(sourceAssociations ? "VERIFIED" : "MISMATCH",
            measured["source_associations"] as Dictionary<string, object>);
        checks["non_target_geometry"] = VerificationCheck(nonTargets ? "VERIFIED" : "MISMATCH",
            measured["non_target_geometry"] as Dictionary<string, object>);
        checks["history"] = VerificationCheck(history ? "VERIFIED" : "MISMATCH",
            measured["history"] as Dictionary<string, object>);
        checks["bodies"] = VerificationCheck(bodies ? "VERIFIED" : "MISMATCH",
            measured["bodies"] as Dictionary<string, object>);
        checks["sketch_solver_status"] = VerificationCheck(solver ? "VERIFIED" : "MISMATCH",
            measured["sketch_solver_status"] as Dictionary<string, object>);
        checks["profile_candidates"] = VerificationCheck(profileCandidates ? "VERIFIED" : "MISMATCH",
            measured["profile_candidates"] as Dictionary<string, object>);
        checks["dirty_state"] = VerificationCheck(dirty ? "VERIFIED" : "MISMATCH",
            measured["dirty_state"] as Dictionary<string, object>);
        result["checks"] = checks;
        result["measured"] = measured;
        return result;
    }

    private static bool M3SketchReferenceConversionChecksPassed(Dictionary<string, object> evidence)
    {
        Dictionary<string, object> checks = evidence == null ? null : evidence["checks"] as Dictionary<string, object>;
        if (checks == null) return false;
        foreach (string name in M3SketchReferenceConversionCheckNames)
        {
            object value;
            Dictionary<string, object> check = checks.TryGetValue(name, out value) ? value as Dictionary<string, object> : null;
            if (check == null || !String.Equals(Convert.ToString(check["status"], CultureInfo.InvariantCulture),
                "VERIFIED", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool M3SketchReferenceConversionFactoryInputsMatch(
        M3SketchReferenceConversionPlanState plan, Dictionary<string, object> factoryEvidence)
    {
        if (plan == null || factoryEvidence == null || plan.ChangedNativeInputTags == null) return false;
        object selectAllValue, countValue, stateValue, inputTagsValue, builderTagsValue;
        bool selectAllRead = factoryEvidence.TryGetValue("select_all_project_feature_curves", out selectAllValue) &&
            selectAllValue is bool;
        bool selectAll = selectAllRead && (bool)selectAllValue;
        int count = -1;
        if (factoryEvidence.TryGetValue("input_count", out countValue))
            try { count = Convert.ToInt32(countValue, CultureInfo.InvariantCulture); } catch { }
        string target = factoryEvidence.TryGetValue("target_state", out stateValue)
            ? Convert.ToString(stateValue, CultureInfo.InvariantCulture) : null;
        object outputStateValue;
        string outputState = factoryEvidence.TryGetValue("output_state_readback", out outputStateValue)
            ? Convert.ToString(outputStateValue, CultureInfo.InvariantCulture) : null;
        string expectedOutputState = String.Equals(plan.TargetState, "reference", StringComparison.Ordinal)
            ? "Reference" : "Active";
        List<string> actual = M3SketchReferenceConversionReadTagList(
            factoryEvidence.TryGetValue("input_tags", out inputTagsValue) ? inputTagsValue : null);
        List<string> builder = M3SketchReferenceConversionReadTagList(
            factoryEvidence.TryGetValue("builder_input_tags", out builderTagsValue) ? builderTagsValue : null);
        HashSet<string> expectedSet = new HashSet<string>(plan.ChangedNativeInputTags, StringComparer.Ordinal);
        HashSet<string> actualSet = new HashSet<string>(actual, StringComparer.Ordinal);
        HashSet<string> builderSet = new HashSet<string>(builder, StringComparer.Ordinal);
        return selectAllRead && !selectAll && count == plan.ChangedNativeInputTags.Length &&
            String.Equals(target, plan.TargetState, StringComparison.Ordinal) &&
            String.Equals(outputState, expectedOutputState, StringComparison.Ordinal) &&
            actual.Count == plan.ChangedNativeInputTags.Length && actualSet.Count == actual.Count &&
            builder.Count == plan.ChangedNativeInputTags.Length && builderSet.Count == builder.Count &&
            expectedSet.SetEquals(actualSet) && expectedSet.SetEquals(builderSet);
    }

    private static List<string> M3SketchReferenceConversionReadTagList(object value)
    {
        List<string> result = new List<string>();
        List<object> objects = value as List<object>;
        if (objects == null) return result;
        foreach (object item in objects)
        {
            string tag = Convert.ToString(item, CultureInfo.InvariantCulture);
            if (!String.IsNullOrEmpty(tag)) result.Add(tag);
        }
        return result;
    }

    private static bool M3SketchReferenceConversionConstraintMapsEqual(
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> expected,
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> actual,
        HashSet<string> allowedDimensionStateChanges,
        HashSet<string> allowedDimensionExpressionChanges)
    {
        if (expected == null || actual == null || expected.Count != actual.Count) return false;
        foreach (KeyValuePair<string, Dictionary<string, CornerConstraintSnapshot>> sketch in expected)
        {
            Dictionary<string, CornerConstraintSnapshot> current;
            if (!actual.TryGetValue(sketch.Key, out current) || sketch.Value == null || current == null ||
                sketch.Value.Count != current.Count) return false;
            foreach (KeyValuePair<string, CornerConstraintSnapshot> constraint in sketch.Value)
            {
                CornerConstraintSnapshot readback;
                if (!current.TryGetValue(constraint.Key, out readback) || readback == null || constraint.Value == null) return false;
                if (allowedDimensionStateChanges != null && allowedDimensionStateChanges.Contains(constraint.Key))
                {
                    Dictionary<string, object> before = DeepCloneValue(constraint.Value.Record) as Dictionary<string, object>;
                    Dictionary<string, object> after = DeepCloneValue(readback.Record) as Dictionary<string, object>;
                    if (before == null || after == null) return false;
                    before.Remove("dimension_state");
                    before.Remove("is_driving");
                    after.Remove("dimension_state");
                    after.Remove("is_driving");
                    if (allowedDimensionExpressionChanges != null && allowedDimensionExpressionChanges.Contains(constraint.Key))
                    {
                        before.Remove("expression_rhs");
                        before.Remove("expression_value");
                        after.Remove("expression_rhs");
                        after.Remove("expression_value");
                    }
                    if (!String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal)) return false;
                }
                else if (!String.Equals(constraint.Value.Signature, readback.Signature, StringComparison.Ordinal)) return false;
            }
        }
        return true;
    }

    private static HashSet<string> M3SketchReferenceConversionSelectedDimensionTags(
        List<M3SketchReferenceConversionObjectState> objects)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        if (objects != null)
            foreach (M3SketchReferenceConversionObjectState item in objects)
                if (item.Kind == "dimension") result.Add(item.Tag);
        return result;
    }

    private static bool M3SketchReferenceConversionReferenceStatesMatch(
        M3SketchReferenceConversionPlanState plan, Dictionary<string, int> after,
        out Dictionary<string, object> evidence)
    {
        List<object> changed = new List<object>();
        List<object> missing = new List<object>();
        int preserved = 0;
        if (plan.ReferenceStatesBefore != null)
            foreach (KeyValuePair<string, int> pair in plan.ReferenceStatesBefore)
            {
                int actual;
                if (after == null || !after.TryGetValue(pair.Key, out actual))
                {
                    missing.Add(pair.Key);
                    continue;
                }
                M3SketchReferenceConversionObjectState targetCurve = null;
                foreach (M3SketchReferenceConversionObjectState item in plan.Objects)
                    if (item.Kind == "curve" && String.Equals(item.Tag, pair.Key, StringComparison.Ordinal)) targetCurve = item;
                int expected = pair.Value;
                if (targetCurve != null)
                    expected = targetCurve.ExpectedAfter == "reference"
                        ? Convert.ToInt32(UFSket.ReferenceStatus.Reference, CultureInfo.InvariantCulture)
                        : Convert.ToInt32(UFSket.ReferenceStatus.Active, CultureInfo.InvariantCulture);
                if (actual != expected)
                    changed.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "tag", pair.Key }, { "before", pair.Value }, { "expected", expected }, { "after", actual },
                        { "selected", targetCurve != null }
                    });
                else preserved++;
            }
        List<object> added = new List<object>();
        if (after != null)
            foreach (string tag in after.Keys)
                if (plan.ReferenceStatesBefore == null || !plan.ReferenceStatesBefore.ContainsKey(tag)) added.Add(tag);
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "baseline_curve_count", plan.ReferenceStatesBefore == null ? 0 : plan.ReferenceStatesBefore.Count },
            { "current_curve_count", after == null ? 0 : after.Count },
            { "preserved_or_expected_count", preserved },
            { "unexpected_status_changes", changed },
            { "missing_tags", missing },
            { "unexpected_new_tags", added }
        };
        return after != null && plan.ReferenceStatesBefore != null && after.Count == plan.ReferenceStatesBefore.Count &&
            changed.Count == 0 && missing.Count == 0 && added.Count == 0;
    }

    private static bool M3SketchReferenceConversionSelectedDimensionsMatch(
        List<M3SketchReferenceConversionObjectState> before,
        List<M3SketchReferenceConversionObjectState> after, string targetState,
        out Dictionary<string, object> evidence)
    {
        List<object> details = new List<object>();
        bool allMatch = before != null && after != null && before.Count == after.Count;
        if (before != null && after != null && before.Count == after.Count)
        {
            for (int i = 0; i < before.Count; i++)
            {
                M3SketchReferenceConversionObjectState first = before[i];
                M3SketchReferenceConversionObjectState current = after[i];
                if (first.Kind != "dimension") continue;
                bool stateMatch = String.Equals(current.StateBefore, targetState, StringComparison.Ordinal);
                bool associationMatch = String.Equals(first.Tag, current.Tag, StringComparison.Ordinal) &&
                    String.Equals(first.AssociatedDimensionTag, current.AssociatedDimensionTag, StringComparison.Ordinal) &&
                    String.Equals(first.AssociatedDimensionId, current.AssociatedDimensionId, StringComparison.Ordinal) &&
                    String.Equals(first.ExpressionTag, current.ExpressionTag, StringComparison.Ordinal) &&
                    String.Equals(first.ExpressionId, current.ExpressionId, StringComparison.Ordinal) &&
                    String.Equals(first.ExpressionName, current.ExpressionName, StringComparison.Ordinal) &&
                    String.Equals(first.ExpressionUnit, current.ExpressionUnit, StringComparison.Ordinal) &&
                    String.Equals(first.ConstraintType, current.ConstraintType, StringComparison.Ordinal);
                bool computedSizeMatch = first.ComputedSizeBefore.HasValue && current.ComputedSizeBefore.HasValue &&
                    NearlyEqual(first.ComputedSizeBefore.Value, current.ComputedSizeBefore.Value);
                bool promotion = M3SketchReferenceConversionIsReferenceToActivePromotion(first);
                bool expressionValueMatch = false;
                bool expressionRhsMatch = false;
                double afterRhsValue;
                if (promotion && first.ComputedSizeBefore.HasValue)
                {
                    expressionValueMatch = current.ExpressionValueBefore.HasValue &&
                        NearlyEqual(current.ExpressionValueBefore.Value, first.ComputedSizeBefore.Value);
                    expressionRhsMatch = M3SketchReferenceConversionTryParseNumericRhs(
                        current.ExpressionRightHandSideBefore, out afterRhsValue) &&
                        NearlyEqual(afterRhsValue, first.ComputedSizeBefore.Value);
                }
                else
                {
                    expressionValueMatch = first.ExpressionValueBefore.HasValue && current.ExpressionValueBefore.HasValue &&
                        NearlyEqual(first.ExpressionValueBefore.Value, current.ExpressionValueBefore.Value);
                    expressionRhsMatch = String.Equals(first.ExpressionRightHandSideBefore,
                        current.ExpressionRightHandSideBefore, StringComparison.Ordinal);
                }
                if (!stateMatch || !associationMatch || !computedSizeMatch ||
                    !expressionValueMatch || !expressionRhsMatch) allMatch = false;
                details.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "object_id", first.Id }, { "constraint_tag", first.Tag },
                    { "associated_dimension_tag_before", first.AssociatedDimensionTag },
                    { "associated_dimension_tag_after", current.AssociatedDimensionTag },
                    { "state_after", current.StateBefore }, { "expected_state", targetState },
                    { "state_matches", stateMatch },
                    { "computed_size_before", first.ComputedSizeBefore },
                    { "computed_size_after", current.ComputedSizeBefore },
                    { "computed_size_matches", computedSizeMatch },
                    { "expression_name_matches", String.Equals(first.ExpressionName, current.ExpressionName, StringComparison.Ordinal) },
                    { "expression_units_match", String.Equals(first.ExpressionUnit, current.ExpressionUnit, StringComparison.Ordinal) },
                    { "expression_value_before", first.ExpressionValueBefore },
                    { "expression_value_after", current.ExpressionValueBefore },
                    { "expression_value_matches_policy", expressionValueMatch },
                    { "expression_right_hand_side_before", first.ExpressionRightHandSideBefore },
                    { "expression_right_hand_side_after", current.ExpressionRightHandSideBefore },
                    { "expression_right_hand_side_matches_policy", expressionRhsMatch },
                    { "reference_to_active_uses_preconversion_computed_size", promotion }
                });
            }
        }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "selected_dimensions", details },
            { "selected_dimension_states_match", allMatch }
        };
        return allMatch;
    }

    private static bool M3SketchReferenceConversionObjectsHaveExpectedState(
        List<M3SketchReferenceConversionObjectState> before,
        List<M3SketchReferenceConversionObjectState> after, string targetState,
        out Dictionary<string, object> evidence)
    {
        List<object> details = new List<object>();
        bool match = before != null && after != null && before.Count == after.Count;
        if (before != null && after != null && before.Count == after.Count)
        {
            for (int i = 0; i < before.Count; i++)
            {
                M3SketchReferenceConversionObjectState expected = before[i];
                M3SketchReferenceConversionObjectState actual = after[i];
                bool objectStateMatch = String.Equals(actual.StateBefore, targetState, StringComparison.Ordinal);
                bool identityMatch = String.Equals(expected.Id, actual.Id, StringComparison.Ordinal) &&
                    String.Equals(expected.Kind, actual.Kind, StringComparison.Ordinal) &&
                    String.Equals(expected.Tag, actual.Tag, StringComparison.Ordinal) &&
                    String.Equals(expected.NativeInputTag, actual.NativeInputTag, StringComparison.Ordinal);
                if (!objectStateMatch || !identityMatch) match = false;
                details.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "object_id", expected.Id }, { "kind", expected.Kind }, { "tag", expected.Tag },
                    { "before", expected.StateBefore }, { "expected_after", targetState },
                    { "actual_after", actual.StateBefore }, { "identity_matches", identityMatch },
                    { "state_matches", objectStateMatch }
                });
            }
        }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "objects", details }, { "all_selected_objects_match", match }
        };
        return match;
    }

    private static bool M3SketchReferenceConversionObjectsAtPrepareState(
        List<M3SketchReferenceConversionObjectState> expected,
        List<M3SketchReferenceConversionObjectState> actual)
    {
        if (expected == null || actual == null || expected.Count != actual.Count) return false;
        for (int i = 0; i < expected.Count; i++)
            if (!String.Equals(expected[i].Id, actual[i].Id, StringComparison.Ordinal) ||
                !String.Equals(expected[i].Kind, actual[i].Kind, StringComparison.Ordinal) ||
                !String.Equals(expected[i].Tag, actual[i].Tag, StringComparison.Ordinal) ||
                !String.Equals(expected[i].NativeInputTag, actual[i].NativeInputTag, StringComparison.Ordinal) ||
                !String.Equals(expected[i].StateBefore, actual[i].StateBefore, StringComparison.Ordinal)) return false;
        return true;
    }

    private static Dictionary<string, object> BuildM3SketchReferenceConversionResult(
        M3SketchReferenceConversionPlanState plan, string revisionId, string state,
        Dictionary<string, object> evidence, List<M3SketchReferenceConversionObjectState> objectsAfter)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> objectsBefore = M3SketchReferenceConversionObjectRecords(plan, null, false);
        List<object> afterRecords = M3SketchReferenceConversionObjectRecords(plan, objectsAfter, true);
        result["kind"] = "edit_sketch_reference_conversion";
        result["revision_id"] = revisionId;
        result["sketch_id"] = plan == null ? null : plan.SketchId;
        result["sketch_tag"] = plan == null ? null : plan.SketchTag;
        result["target_state"] = plan == null ? null : plan.TargetState;
        result["state"] = state;
        result["objects"] = objectsBefore;
        result["objects_after"] = afterRecords;
        result["changed_count"] = plan == null || plan.Objects == null ? 0 : CountM3SketchReferenceConversionChangedObjects(plan.Objects);
        result["dirty_before"] = plan == null ? null : (object)plan.DirtyBefore;
        result["dirty_after"] = plan == null || !plan.DirtyAfterCaptured ? null : (object)plan.DirtyAfter;
        result["solver_before"] = plan == null ? null : plan.SolverBefore;
        result["solver_after"] = plan == null ? null : plan.SolverAfter;
        result["solver_prepare"] = plan == null ? null : plan.SolverBeforePrepare;
        result["contour_candidates_before"] = plan == null ? new List<object>() : plan.ContourCandidatesBefore;
        result["contour_candidates_after"] = plan == null ? new List<object>() : plan.ContourCandidatesAfter;
        result["contour_candidate_basis"] = "UF reference status Active only; not actual Section/profile validation";
        result["work_region_scope_before_conversion"] = plan == null ? null : plan.WorkRegionBefore;
        result["work_region_scope_after_conversion"] = plan == null ? null : plan.WorkRegionAfter;
        result["native_operation"] = plan == null ? null : plan.NativeOperationEvidence;
        result["evidence"] = evidence;
        return result;
    }

    private static int CountM3SketchReferenceConversionChangedObjects(
        List<M3SketchReferenceConversionObjectState> objects)
    {
        int count = 0;
        if (objects != null)
            foreach (M3SketchReferenceConversionObjectState item in objects)
                if (item.WillChange) count++;
        return count;
    }

    private static bool M3SketchReferenceConversionIsSketchActive(Session session, Sketch sketch)
    {
        if (session == null || sketch == null) return false;
        Sketch active = session.ActiveSketch;
        return active != null && active.Tag.Equals(sketch.Tag);
    }

    private static string M3SketchReferenceConversionSafeMessage(Exception exception)
    {
        if (exception == null) return null;
        M2ModelException model = exception as M2ModelException;
        if (model != null) return model.Message;
        return exception.Message ?? exception.GetType().Name;
    }

    private static string M3SketchReferenceConversionExceptionCode(Exception exception)
    {
        if (exception == null) return "NATIVE_REFERENCE_CONVERSION_FAILED";
        M2ModelException model = exception as M2ModelException;
        return model == null ? "NATIVE_REFERENCE_CONVERSION_FAILED" : model.Code;
    }
}
