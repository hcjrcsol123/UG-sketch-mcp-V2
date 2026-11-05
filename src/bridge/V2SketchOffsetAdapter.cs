using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.UF;
using NxSketchMcp.M2;

/// <summary>Optional F08 adapter for adding native NX sketch offsets.</summary>
public sealed partial class M2OpenModelAdapter
{
    private const double SketchOffsetPositionToleranceMm = 1.0e-6;
    private const double SketchOffsetAngleTolerance = 1.0e-7;

    private sealed class M3SketchOffsetPlanState
    {
        internal M2ModelBinding Binding;
        internal BindingState BindingState;
        internal string RevisionId;
        internal string ActiveSketchTagBefore;
        internal M3SketchOffsetRequest Request;
        internal Sketch Sketch;
        internal string SketchId;
        internal string SketchTag;
        internal string FeatureId;
        internal string FeatureTag;
        internal Curve[] InputCurves;
        internal string[] InputCurveTags;
        internal string[] InputCurveTypes;
        internal Point3d[] InputStarts;
        internal Point3d[] InputEnds;
        internal Point3d ChainStart;
        internal Point3d SketchOrigin;
        internal Vector3d SketchNormal;
        internal Vector3d SketchXAxis;
        internal Vector3d SketchYAxis;
        internal bool InputClosed;
        internal bool DirtyBefore;
        internal string SemanticBefore;
        internal string HistoryBefore;
        internal string ExpressionsBefore;
        internal string FullGeometryBefore;
        internal Dictionary<string, CornerGeometrySnapshot> GeometryBefore;
        internal Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> ConstraintsBefore;
        internal Dictionary<string, string> FramesBefore;
        internal Dictionary<string, int> ReferenceStatesBefore;
        internal List<M3BodyMeasure> BodiesBefore;
        internal List<string> FeatureTagsBefore;
        internal Curve[] OutputCurves;
        internal string[] OutputCurveTags;
        internal string[] OutputCurveIds;
        internal Dictionary<string, CornerGeometrySnapshot> GeometryAfter;
        internal Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> ConstraintsAfter;
        internal Dictionary<string, string> FramesAfter;
        internal Dictionary<string, int> ReferenceStatesAfter;
        internal List<M3BodyMeasure> BodiesAfter;
        internal List<string> FeatureTagsAfter;
        internal string SemanticAfter;
        internal string HistoryAfter;
        internal Dictionary<string, object> NativeOperationEvidence;
        internal Dictionary<string, object> AppliedEvidence;
        internal M3ExecutionResult Execution;
    }

    public M3PlanSnapshot PrepareSketchOffset(M2ModelBinding binding, M3SketchOffsetRequest request)
    {
        ValidateSketchOffsetRequest(request);
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "a Sketch is active in NX; sketch offset preparation is paused");
        string activeSketchTagBefore = state.Session.ActiveSketch == null
            ? null : state.Session.ActiveSketch.Tag.ToString();
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            throw new M2ModelException("STALE_BINDING", bindingReason ?? "NX model binding is no longer current");
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current part revision");

        SnapshotResult captured = Capture(binding, state);
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id changed while resolving the sketch offset chain");
        Sketch sketch = FindSketch(state.WorkPart, request.SketchId);
        if (sketch == null || !String.Equals(RequiredJournalId(sketch, "sketch"), request.SketchId, StringComparison.Ordinal))
            throw new M2ModelException("SKETCH_NOT_FOUND", "sketch_id does not identify a native Sketch in the bound Work Part");

        Curve[] inputCurves = new Curve[request.CurveChain.Count];
        string[] inputTags = new string[inputCurves.Length];
        string[] inputTypes = new string[inputCurves.Length];
        Point3d[] inputStarts = new Point3d[inputCurves.Length];
        Point3d[] inputEnds = new Point3d[inputCurves.Length];
        HashSet<string> inputTagSet = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < request.CurveChain.Count; i++)
        {
            M3SketchOffsetCurveRequest item = request.CurveChain[i];
            Curve curve = FindSketchCornerCurve(sketch, item.CurveId);
            if (curve == null)
                throw new M2ModelException("CURVE_NOT_FOUND", "curve_chain contains an ID that is not native geometry in the requested Sketch");
            if (!sketch.IsSketchGeometry(curve))
                throw new M2ModelException("SKETCH_TARGET_MISMATCH", "every curve_chain entry must belong to the explicitly requested Sketch");
            if (!(curve is Line) && !(curve is Arc))
                throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "F08 supports native Line and circular Arc curves only");
            string tag = curve.Tag.ToString();
            if (!inputTagSet.Add(tag))
                throw new M2ModelException("INVALID_REQUEST", "curve_chain contains duplicate native curves");
            inputCurves[i] = curve;
            inputTags[i] = tag;
            inputTypes[i] = curve.GetType().Name;
            string startName = item.Direction == "forward" ? "start" : "end";
            string endName = item.Direction == "forward" ? "end" : "start";
            inputStarts[i] = CurveEndpoint(curve, startName);
            inputEnds[i] = CurveEndpoint(curve, endName);
            double length = curve.GetLength();
            if (!IsFinite(length) || length <= SketchOffsetPositionToleranceMm)
                throw new M2ModelException("DEGENERATE_CURVE", "every selected curve must have finite length greater than 1e-6 mm");
            ValidateOffsetCurvePlane(curve, sketch);
        }

        for (int i = 1; i < inputCurves.Length; i++)
            if (PointDistance(inputEnds[i - 1], inputStarts[i]) > SketchOffsetPositionToleranceMm)
                throw new M2ModelException("CURVE_CHAIN_DISCONNECTED", "curve_chain order and direction must form one connected chain within 1e-6 mm");
        bool inputClosed = PointDistance(inputStarts[0], inputEnds[inputEnds.Length - 1]) <= SketchOffsetPositionToleranceMm;
        for (int i = 0; i < inputCurves.Length; i++)
        {
            Arc arc = inputCurves[i] as Arc;
            if (arc != null && IsClosedSketchCornerCurve(arc) && inputCurves.Length != 1)
                throw new M2ModelException("INVALID_CURVE_CHAIN", "a full circular Arc is supported only as a single-curve closed chain");
        }

        Dictionary<string, object> frameRecord = M4SketchFrameRecord(sketch);
        Point3d sketchOrigin = ReadFramePoint(frameRecord, "origin_mm");
        Vector3d sketchNormal = ReadFrameVector(frameRecord, "normal_world");
        Vector3d sketchXAxis = ReadFrameVector(frameRecord, "x_axis_world");
        Vector3d sketchYAxis = ReadFrameVector(frameRecord, "y_axis_world");
        NormalizeVector(ref sketchNormal, "sketch normal");
        NormalizeVector(ref sketchXAxis, "sketch x-axis");
        NormalizeVector(ref sketchYAxis, "sketch y-axis");
        Dictionary<string, object> sourceTopology = AnalyzeOffsetCurveTopology(inputCurves,
            sketchOrigin, sketchXAxis, sketchYAxis, sketchNormal);
        if (!Boolean.Equals(sourceTopology["valid_chain"], true) ||
            !Boolean.Equals(sourceTopology["closed"], inputClosed))
            throw new M2ModelException("INVALID_CURVE_CHAIN", "curve_chain is branched, disconnected, self-intersecting, or not a single open/closed chain");

        bool sameSketchFeature;
        try
        {
            sameSketchFeature = sketch.Feature != null && !sketch.Feature.IsOccurrence &&
                sketch.Feature.OwningPart != null && sketch.Feature.OwningPart.Tag == state.WorkPart.Tag;
        }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "could not verify native Sketch feature ownership (" + ex.GetType().Name + ")");
        }
        if (!sameSketchFeature)
            throw new M2ModelException("UNSUPPORTED_SKETCH_HISTORY", "F08 requires a native Sketch feature owned by the bound Work Part");

        Dictionary<string, CornerGeometrySnapshot> geometryBefore = CaptureSketchCornerGeometry(state.WorkPart);
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraintsBefore =
            CaptureAllSketchOffsetConstraints(state.WorkPart);
        Dictionary<string, string> framesBefore = CaptureAllSketchOffsetFrames(state.WorkPart);
        Dictionary<string, int> referenceStatesBefore = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
        bool dirtyBefore;
        try { dirtyBefore = state.WorkPart.IsModified; }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "could not capture Work Part modified state for F08 (" + ex.GetType().Name + ")");
        }
        List<M3BodyMeasure> bodiesBefore = CaptureM3BodyMeasures(state.WorkPart);
        List<string> featureTagsBefore = CaptureFeatureTags(state.WorkPart);
        string semanticBefore = CanonicalSemanticSnapshot(captured.Snapshot);
        string historyBefore = M2Json.CanonicalText(HistoryProjection(captured.Snapshot));
        string fullGeometryBefore = CaptureFullGeometry(state.WorkPart);
        string featureId = sketch.Feature == null ? null : RequiredJournalId(sketch.Feature, "sketch feature");

        M3SketchOffsetPlanState planState = new M3SketchOffsetPlanState
        {
            Binding = binding,
            BindingState = state,
            RevisionId = binding.RevisionId,
            ActiveSketchTagBefore = activeSketchTagBefore,
            Request = CloneM3SketchOffsetRequest(request),
            Sketch = sketch,
            SketchId = request.SketchId,
            SketchTag = sketch.Tag.ToString(),
            FeatureId = featureId,
            FeatureTag = sketch.Feature.Tag.ToString(),
            InputCurves = inputCurves,
            InputCurveTags = inputTags,
            InputCurveTypes = inputTypes,
            InputStarts = inputStarts,
            InputEnds = inputEnds,
            ChainStart = inputStarts[0],
            SketchOrigin = sketchOrigin,
            SketchNormal = sketchNormal,
            SketchXAxis = sketchXAxis,
            SketchYAxis = sketchYAxis,
            InputClosed = inputClosed,
            DirtyBefore = dirtyBefore,
            SemanticBefore = semanticBefore,
            HistoryBefore = historyBefore,
            ExpressionsBefore = M2Json.CanonicalText(SnapshotList(captured.Snapshot, "expressions")),
            FullGeometryBefore = fullGeometryBefore,
            GeometryBefore = geometryBefore,
            ConstraintsBefore = constraintsBefore,
            FramesBefore = framesBefore,
            ReferenceStatesBefore = referenceStatesBefore,
            BodiesBefore = bodiesBefore,
            FeatureTagsBefore = featureTagsBefore,
            NativeOperationEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
        };

        Dictionary<string, object> preview = new Dictionary<string, object>(StringComparer.Ordinal);
        preview.Add("preview_type", "source_chain_and_requested_offset");
        preview.Add("source_chain", OffsetInputCurveRecords(planState));
        preview.Add("source_chain_closed", inputClosed);
        preview.Add("distance_mm", request.DistanceMm);
        preview.Add("side", request.Side);
        preview.Add("retain_original", true);
        preview.Add("join_mode", request.JoinMode);
        preview.Add("native_geometry_evaluation", "deferred_until_atomic_execute");

        Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal);
        publicView.Add("kind", "edit_sketch_offset");
        publicView.Add("revision_id", binding.RevisionId);
        publicView.Add("sketch_id", request.SketchId);
        publicView.Add("sketch_tag", planState.SketchTag);
        publicView.Add("feature_id", planState.FeatureId);
        publicView.Add("distance_mm", request.DistanceMm);
        publicView.Add("side", request.Side);
        publicView.Add("retain_original", true);
        publicView.Add("join_mode", request.JoinMode);
        publicView.Add("source_chain_closed", inputClosed);
        publicView.Add("curve_chain", OffsetRequestedCurveChain(planState));
        publicView.Add("preview", preview);
        publicView.Add("baseline_geometry_count", geometryBefore.Count);
        publicView.Add("baseline_constraint_count", CountOffsetConstraints(constraintsBefore));
        publicView.Add("baseline_reference_curve_count", referenceStatesBefore.Count);
        return new M3PlanSnapshot
        {
            Kind = "edit_sketch_offset",
            RevisionId = binding.RevisionId,
            SketchId = request.SketchId,
            FeatureId = planState.FeatureId,
            PublicView = publicView,
            AdapterState = planState
        };
    }

    private static void ValidateSketchOffsetRequest(M3SketchOffsetRequest request)
    {
        if (request == null || String.IsNullOrEmpty(request.RevisionId) || String.IsNullOrEmpty(request.SketchId))
            throw new M2ModelException("INVALID_REQUEST", "revision_id and sketch_id are required for F08");
        if (request.CurveChain == null || request.CurveChain.Count < 1 || request.CurveChain.Count > 128)
            throw new M2ModelException("INVALID_REQUEST", "curve_chain must contain 1 through 128 ordered curves");
        if (!IsFinite(request.DistanceMm) || request.DistanceMm <= 0.0)
            throw new M2ModelException("INVALID_REQUEST", "distance_mm must be a positive finite millimeter value");
        if (request.Side != "left" && request.Side != "right")
            throw new M2ModelException("INVALID_REQUEST", "side must be left or right");
        if (request.JoinMode != "extension" && request.JoinMode != "arc")
            throw new M2ModelException("INVALID_REQUEST", "join_mode must be extension or arc");
        if (!request.RetainOriginal)
            throw new M2ModelException("UNSUPPORTED_REQUEST", "F08 requires retain_original=true");
        foreach (M3SketchOffsetCurveRequest curve in request.CurveChain)
            if (curve == null || String.IsNullOrEmpty(curve.CurveId) ||
                (curve.Direction != "forward" && curve.Direction != "reverse"))
                throw new M2ModelException("INVALID_REQUEST", "each curve_chain entry requires curve_id and forward/reverse direction");
    }

    private M3ExecutionResult ExecuteSketchOffsetPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M3SketchOffsetPlanState plan = prepared == null ? null : prepared.AdapterState as M3SketchOffsetPlanState;
        if (plan == null || prepared.Kind != "edit_sketch_offset")
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null,
                "INVALID_PLAN", "sketch offset plan does not contain retained NX adapter state");
        BindingState state;
        try { state = RequireState(binding); }
        catch (M2ModelException ex)
        {
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, ex.Code, ex.Message);
        }
        if (!Object.ReferenceEquals(state, plan.BindingState) || !Object.ReferenceEquals(binding, plan.Binding) ||
            !String.Equals(plan.RevisionId, prepared.RevisionId, StringComparison.Ordinal) ||
            !String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "sketch offset plan belongs to a different binding or revision");
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_BINDING", bindingReason);
        try
        {
            RequireWritableSimplePart(state.WorkPart);
            RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
            if (state.Session.ActiveSketch != null)
                return M3Execution("UNCHANGED", binding.RevisionId, null, "NX_BUSY", "a Sketch is active in NX before sketch offset execution");
            SnapshotResult current = Capture(binding, state);
            if (!String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(CanonicalSemanticSnapshot(current.Snapshot), plan.SemanticBefore, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "semantic model state changed after sketch offset preparation");
            Dictionary<string, CornerGeometrySnapshot> geometryNow = CaptureSketchCornerGeometry(state.WorkPart);
            if (!CornerGeometryMapsEqual(plan.GeometryBefore, geometryNow))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "native geometry changed after sketch offset preparation");
            if (!OffsetConstraintMapsEqual(plan.ConstraintsBefore, CaptureAllSketchOffsetConstraints(state.WorkPart)))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "Sketch constraints changed after sketch offset preparation");
            if (!OffsetStringMapsEqual(plan.FramesBefore, CaptureAllSketchOffsetFrames(state.WorkPart)))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "Sketch frames changed after sketch offset preparation");
            if (!OffsetIntMapsEqual(plan.ReferenceStatesBefore, CaptureAllSketchOffsetReferenceStates(state.WorkPart)))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "Sketch reference status changed after sketch offset preparation");
            Dictionary<string, object> bodyEvidence;
            if (!BodyMeasuresEqual(plan.BodiesBefore, CaptureM3BodyMeasures(state.WorkPart), out bodyEvidence))
                return M3Execution("UNCHANGED", binding.RevisionId, bodyEvidence, "STALE_GEOMETRY", "body measurements changed after sketch offset preparation");
            Sketch currentSketch = FindSketch(state.WorkPart, plan.SketchId);
            if (currentSketch == null || !String.Equals(RequiredJournalId(currentSketch, "sketch"), plan.SketchId, StringComparison.Ordinal) ||
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
                "could not revalidate sketch offset preconditions (" + ex.GetType().Name + ")");
        }

        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F08 sketch offset modification"); }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "UPDATE_FAILED", "could not create an NX update mark (" + ex.GetType().Name + ")");
        }

        Exception operationException = null;
        bool updateCallAttempted = false;
        bool sketchActivated = false;
        List<object> sideEvaluations = new List<object>();
        plan.NativeOperationEvidence["native_operation_called"] = true;
        try
        {
            plan.Sketch.Activate(Sketch.ViewReorient.False);
            sketchActivated = true;
            Func<Curve[], bool> sideMatcher = delegate(Curve[] curves)
            {
                Dictionary<string, object> evaluation;
                bool matched = ValidateNativeOffsetOutput(plan, curves, out evaluation);
                sideEvaluations.Add(evaluation);
                plan.NativeOperationEvidence["side_evaluations"] = sideEvaluations;
                return matched;
            };
            V2SketchOffsetNativeResult native = V2SketchOffsetFactory.Execute(state.WorkPart,
                plan.InputCurves, plan.ChainStart, plan.Request.DistanceMm, plan.Request.JoinMode, sideMatcher);
            if (native == null || native.OutputCurves == null || native.OutputCurves.Length == 0)
                throw new M2ModelException("OFFSET_OUTPUT_INVALID", "NX offset builder returned no native output curves after commit");
            plan.OutputCurves = (Curve[])native.OutputCurves.Clone();
            plan.OutputCurveTags = OffsetCurveTags(plan.OutputCurves);
            plan.OutputCurveIds = OffsetCurveIds(plan.OutputCurves);
            plan.NativeOperationEvidence["native_cap_type"] = native.NativeCapType;
            plan.NativeOperationEvidence["reverse_direction_used"] = native.ReverseDirectionUsed;
            plan.NativeOperationEvidence["commit_returned_null"] = native.CommitReturnedNull;
            plan.NativeOperationEvidence["side_evaluation_count"] = native.SideEvaluations;
            plan.NativeOperationEvidence["output_curve_tags"] = StringListObjects(plan.OutputCurveTags);
            plan.NativeOperationEvidence["output_curve_ids"] = StringListObjects(plan.OutputCurveIds);
            plan.Sketch.Update();
            plan.Sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            sketchActivated = false;
            updateCallAttempted = true;
            int updateErrors = state.Session.UpdateManager.DoUpdate(mark);
            plan.NativeOperationEvidence["update_error_count"] = updateErrors;
            if (updateErrors != 0)
                throw new M2ModelException("UPDATE_FAILED", "NX update reported errors after the native sketch offset call");

            SnapshotResult afterCapture = Capture(binding, state);
            plan.GeometryAfter = CaptureSketchCornerGeometry(state.WorkPart);
            plan.ConstraintsAfter = CaptureAllSketchOffsetConstraints(state.WorkPart);
            plan.FramesAfter = CaptureAllSketchOffsetFrames(state.WorkPart);
            plan.ReferenceStatesAfter = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
            plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart);
            plan.FeatureTagsAfter = CaptureFeatureTags(state.WorkPart);
            plan.SemanticAfter = CanonicalSemanticSnapshot(afterCapture.Snapshot);
            plan.HistoryAfter = M2Json.CanonicalText(HistoryProjection(afterCapture.Snapshot));
            Dictionary<string, object> evidence;
            bool verified = VerifySketchOffsetGeometryNow(state, plan, plan.GeometryAfter,
                plan.ConstraintsAfter, plan.FramesAfter, plan.ReferenceStatesAfter,
                plan.BodiesAfter, plan.FeatureTagsAfter, afterCapture.Snapshot, out evidence);
            plan.AppliedEvidence = evidence;
            if (!verified)
                throw new M2ModelException("OFFSET_VERIFY_FAILED", "native offset geometry or preserved Sketch state did not match the requested side and chain");
            Dictionary<string, object> result = BuildSketchOffsetExecutionResult(plan, binding.RevisionId,
                "APPLIED", true, plan.AppliedEvidence);
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
                    if (IsSketchActive(state.Session, plan.Sketch))
                        plan.Sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
                    sketchActivated = false;
                }
                catch (Exception deactivateException)
                {
                    plan.NativeOperationEvidence["deactivation_exception_type"] = deactivateException.GetType().FullName;
                    plan.NativeOperationEvidence["deactivation_exception_message"] = SafeOffsetExceptionMessage(deactivateException);
                }
            }
            plan.NativeOperationEvidence["native_exception_type"] = ex.GetType().FullName;
            plan.NativeOperationEvidence["native_exception_message"] = SafeOffsetExceptionMessage(ex);
            M3ExecutionResult restored = RestoreSketchOffsetPlan(binding, state, plan, mark, operationException, updateCallAttempted);
            plan.Execution = restored;
            return restored;
        }
        finally
        {
            TryDeleteUndoMark(state.Session, mark);
        }
    }

    private M3VerificationResult VerifySketchOffsetPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M3SketchOffsetPlanState plan = prepared == null ? null : prepared.AdapterState as M3SketchOffsetPlanState;
        if (plan == null || plan.Execution == null || plan.Execution.State != "APPLIED")
            return M3VerificationFailure(binding == null ? null : binding.RevisionId,
                "sketch offset verification requires a successfully applied plan");
        BindingState state = RequireState(binding);
        string bindingReason;
        if (!Object.ReferenceEquals(state, plan.BindingState) || !Object.ReferenceEquals(binding, plan.Binding) ||
            !IsBindingCurrent(binding, out bindingReason))
            return M3VerificationFailure(binding == null ? null : binding.RevisionId,
                "NX binding changed before sketch offset verification");

        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> measured = new Dictionary<string, object>(StringComparer.Ordinal);
        measured["native_operation"] = CloneCornerDictionary(plan.NativeOperationEvidence);
        bool geometryVerified = false;
        bool sourcesPreserved = false;
        bool constraintsPreserved = false;
        bool framesPreserved = false;
        bool referencesPreserved = false;
        bool nonTargetsPreserved = false;
        bool historyVerified = false;
        bool bodiesPreserved = false;
        bool solverVerified = false;
        object sourceEvidence = null;
        object referenceEvidence = null;
        object nonTargetEvidence = null;
        object constraintEvidence = null;
        object frameEvidence = null;
        object bodyEvidenceResult = null;
        object historyEvidence = null;
        object solverEvidenceResult = null;
        try
        {
            SnapshotResult captured = Capture(binding, state);
            Dictionary<string, CornerGeometrySnapshot> geometryNow = CaptureSketchCornerGeometry(state.WorkPart);
            Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraintsNow = CaptureAllSketchOffsetConstraints(state.WorkPart);
            Dictionary<string, string> framesNow = CaptureAllSketchOffsetFrames(state.WorkPart);
            Dictionary<string, int> referencesNow = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
            List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart);
            List<string> featureTagsNow = CaptureFeatureTags(state.WorkPart);
            Dictionary<string, object> geometryEvidence;
            bool liveValid = VerifySketchOffsetGeometryNow(state, plan, geometryNow, constraintsNow,
                framesNow, referencesNow, bodiesNow, featureTagsNow, captured.Snapshot, out geometryEvidence);
            measured["live_geometry"] = geometryEvidence;
            geometryVerified = liveValid && OffsetGeometryMapsEqual(plan.GeometryAfter, geometryNow);
            sourcesPreserved = VerifyOffsetOriginalGeometry(plan, geometryNow, out sourceEvidence);
            measured["source_curves"] = sourceEvidence;
            constraintsPreserved = OffsetConstraintMapsEqual(plan.ConstraintsAfter, constraintsNow);
            constraintEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "preserved", constraintsPreserved }
            };
            measured["sketch_constraints"] = constraintEvidence;
            framesPreserved = OffsetStringMapsEqual(plan.FramesAfter, framesNow);
            frameEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "preserved", framesPreserved }
            };
            measured["sketch_frames"] = frameEvidence;
            referencesPreserved = VerifyOffsetReferenceStates(plan, referencesNow, out referenceEvidence);
            measured["reference_status"] = referenceEvidence;
            nonTargetsPreserved = VerifyOffsetNonTargetGeometry(plan, geometryNow, out nonTargetEvidence);
            measured["non_target_geometry"] = nonTargetEvidence;
            Dictionary<string, object> bodyEvidence;
            bodiesPreserved = BodyMeasuresEqual(plan.BodiesAfter, bodiesNow, out bodyEvidence);
            bodyEvidenceResult = bodyEvidence;
            measured["bodies"] = bodyEvidence;
            historyVerified = String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(captured.Snapshot), StringComparison.Ordinal) &&
                String.Equals(plan.HistoryAfter, M2Json.CanonicalText(HistoryProjection(captured.Snapshot)), StringComparison.Ordinal) &&
                FeatureTagsMatch(plan.FeatureTagsAfter, featureTagsNow);
            historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "semantic_matches_execute", String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(captured.Snapshot), StringComparison.Ordinal) },
                { "history_matches_execute", String.Equals(plan.HistoryAfter, M2Json.CanonicalText(HistoryProjection(captured.Snapshot)), StringComparison.Ordinal) },
                { "feature_tags_match_execute", FeatureTagsMatch(plan.FeatureTagsAfter, featureTagsNow) }
            };
            measured["history"] = historyEvidence;
            Sketch currentSketch = FindSketch(state.WorkPart, plan.SketchId);
            Dictionary<string, object> solverEvidence = CaptureCornerSketchStatus(currentSketch);
            measured["sketch_solver_status"] = solverEvidence;
            solverEvidenceResult = solverEvidence;
            solverVerified = Boolean.Equals(solverEvidence["acceptable"], true);
        }
        catch (Exception ex)
        {
            measured["verification_exception_type"] = ex.GetType().FullName;
            measured["verification_exception_message"] = SafeOffsetExceptionMessage(ex);
        }

        object liveGeometryEvidence;
        measured.TryGetValue("live_geometry", out liveGeometryEvidence);
        checks["offset_geometry"] = VerificationCheck(geometryVerified ? "VERIFIED" : "MISMATCH", liveGeometryEvidence as Dictionary<string, object>);
        checks["source_curves"] = VerificationCheck(sourcesPreserved ? "VERIFIED" : "MISMATCH", sourceEvidence as Dictionary<string, object>);
        checks["sketch_constraints"] = VerificationCheck(constraintsPreserved ? "VERIFIED" : "MISMATCH", constraintEvidence as Dictionary<string, object>);
        checks["sketch_frames"] = VerificationCheck(framesPreserved ? "VERIFIED" : "MISMATCH", frameEvidence as Dictionary<string, object>);
        checks["reference_status"] = VerificationCheck(referencesPreserved ? "VERIFIED" : "MISMATCH", referenceEvidence as Dictionary<string, object>);
        checks["non_target_geometry"] = VerificationCheck(nonTargetsPreserved ? "VERIFIED" : "MISMATCH", nonTargetEvidence as Dictionary<string, object>);
        checks["history"] = VerificationCheck(historyVerified ? "VERIFIED" : "MISMATCH", historyEvidence as Dictionary<string, object>);
        checks["bodies"] = VerificationCheck(bodiesPreserved ? "VERIFIED" : "MISMATCH", bodyEvidenceResult as Dictionary<string, object>);
        checks["sketch_solver_status"] = VerificationCheck(solverVerified ? "VERIFIED" : "MISMATCH", solverEvidenceResult as Dictionary<string, object>);

        bool verified = geometryVerified && sourcesPreserved && constraintsPreserved && framesPreserved &&
            referencesPreserved && nonTargetsPreserved && historyVerified && bodiesPreserved && solverVerified;
        Dictionary<string, object> result = BuildSketchOffsetExecutionResult(plan, binding.RevisionId,
            plan.Execution.State, verified, measured);
        return new M3VerificationResult
        {
            Verified = verified,
            RevisionId = binding.RevisionId,
            Checks = checks,
            Result = result,
            ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified
                ? null
                : "Live offset geometry, source curves, constraints, Sketch frames, reference state, history, or non-target geometry did not match the applied plan."
        };
    }

    private M3ExecutionResult RestoreSketchOffsetPlan(M2ModelBinding binding, BindingState state,
        M3SketchOffsetPlanState plan, Session.UndoMarkId mark, Exception operationException, bool updateCall)
    {
        Dictionary<string, object> restore = new Dictionary<string, object>(StringComparer.Ordinal);
        restore["native_operation_called"] = Boolean.Equals(plan.NativeOperationEvidence["native_operation_called"], true);
        restore["exception_type"] = operationException == null ? null : operationException.GetType().FullName;
        M2ModelException modelException = operationException as M2ModelException;
        restore["exception_code"] = modelException == null ? "NATIVE_OFFSET_FAILED" : modelException.Code;
        restore["exception_message"] = SafeOffsetExceptionMessage(operationException);
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
            restore["deactivation_exception_message"] = SafeOffsetExceptionMessage(ex);
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
            restore["undo_exception_message"] = SafeOffsetExceptionMessage(ex);
        }

        bool geometryRestored = false;
        bool constraintsRestored = false;
        bool framesRestored = false;
        bool referencesRestored = false;
        bool semanticRestored = false;
        bool historyRestored = false;
        bool bodiesRestored = false;
        bool dirtyRestored = false;
        bool featuresRestored = false;
        bool activeSketchRestored = false;
        string restoredRevision = binding == null ? null : binding.RevisionId;
        if (undoSucceeded)
        {
            try
            {
                Sketch activeSketchAfterUndo = state.Session.ActiveSketch;
                string activeSketchTagAfterUndo = activeSketchAfterUndo == null ? null : activeSketchAfterUndo.Tag.ToString();
                activeSketchRestored = String.Equals(plan.ActiveSketchTagBefore, activeSketchTagAfterUndo, StringComparison.Ordinal);
                restore["active_sketch_tag_after"] = activeSketchTagAfterUndo;
                SnapshotResult restoredCapture = Capture(binding, state);
                restoredRevision = binding.RevisionId;
                Dictionary<string, CornerGeometrySnapshot> geometryNow = CaptureSketchCornerGeometry(state.WorkPart);
                Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraintsNow = CaptureAllSketchOffsetConstraints(state.WorkPart);
                Dictionary<string, string> framesNow = CaptureAllSketchOffsetFrames(state.WorkPart);
                Dictionary<string, int> referencesNow = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
                List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart);
                geometryRestored = CornerGeometryMapsEqual(plan.GeometryBefore, geometryNow) &&
                    String.Equals(plan.FullGeometryBefore, CaptureFullGeometry(state.WorkPart), StringComparison.Ordinal);
                constraintsRestored = OffsetConstraintMapsEqual(plan.ConstraintsBefore, constraintsNow);
                framesRestored = OffsetStringMapsEqual(plan.FramesBefore, framesNow);
                referencesRestored = OffsetIntMapsEqual(plan.ReferenceStatesBefore, referencesNow);
                semanticRestored = String.Equals(plan.SemanticBefore, CanonicalSemanticSnapshot(restoredCapture.Snapshot), StringComparison.Ordinal);
                historyRestored = String.Equals(plan.HistoryBefore, M2Json.CanonicalText(HistoryProjection(restoredCapture.Snapshot)), StringComparison.Ordinal);
                Dictionary<string, object> bodyEvidence;
                bodiesRestored = BodyMeasuresEqual(plan.BodiesBefore, bodiesNow, out bodyEvidence);
                dirtyRestored = state.WorkPart.IsModified == plan.DirtyBefore;
                featuresRestored = FeatureTagsMatch(plan.FeatureTagsBefore, CaptureFeatureTags(state.WorkPart));
                restore["geometry_restored"] = geometryRestored;
                restore["constraints_restored"] = constraintsRestored;
                restore["frames_restored"] = framesRestored;
                restore["reference_status_restored"] = referencesRestored;
                restore["semantic_state_restored"] = semanticRestored;
                restore["history_restored"] = historyRestored;
                restore["bodies_restored"] = bodiesRestored;
                restore["dirty_state_restored"] = dirtyRestored;
                restore["features_restored"] = featuresRestored;
                restore["body_measurements"] = bodyEvidence;
            }
            catch (Exception ex)
            {
                restore["readback_exception_type"] = ex.GetType().FullName;
                restore["readback_exception_message"] = SafeOffsetExceptionMessage(ex);
            }
        }
        else restore["active_sketch_tag_after"] = null;
        restore["active_sketch_restored"] = activeSketchRestored;
        restore["revision_id"] = restoredRevision;
        plan.NativeOperationEvidence["undo_restore"] = restore;
        bool restored = undoSucceeded && geometryRestored && constraintsRestored && framesRestored && referencesRestored &&
            semanticRestored && historyRestored && bodiesRestored && dirtyRestored && featuresRestored && activeSketchRestored;
        Dictionary<string, object> evidence = BuildSketchOffsetEvidence(plan, null, restore);
        if (plan.AppliedEvidence != null)
            evidence["failed_postcondition"] = CloneCornerDictionary(plan.AppliedEvidence);
        else
        {
            object evaluationValue;
            List<object> evaluations = plan.NativeOperationEvidence.TryGetValue("side_evaluations", out evaluationValue)
                ? evaluationValue as List<object> : null;
            Dictionary<string, object> lastEvaluation = evaluations != null && evaluations.Count > 0
                ? evaluations[evaluations.Count - 1] as Dictionary<string, object> : null;
            if (lastEvaluation != null) evidence["failed_postcondition"] = CloneCornerDictionary(lastEvaluation);
        }
        string stateName = restored
            ? (Boolean.Equals(restore["native_operation_called"], true) ? "ROLLED_BACK" : "UNCHANGED")
            : "UNKNOWN";
        string code = restored ? (modelException == null ? "NATIVE_OFFSET_FAILED" : modelException.Code) : "SKETCH_OFFSET_ROLLBACK_UNVERIFIED";
        string message = restored
            ? "NX sketch offset failed and Undo proved restoration of prior geometry, constraints, reference states, history, bodies, and dirty state."
            : "NX sketch offset failed and Undo could not prove restoration of the prior model state.";
        Dictionary<string, object> result = BuildSketchOffsetExecutionResult(plan, restoredRevision, stateName, false, evidence);
        return M3Execution(stateName, restoredRevision, result, code, message);
    }

    private bool VerifySketchOffsetGeometryNow(BindingState state, M3SketchOffsetPlanState plan,
        Dictionary<string, CornerGeometrySnapshot> geometryNow,
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraintsNow,
        Dictionary<string, string> framesNow, Dictionary<string, int> referencesNow,
        List<M3BodyMeasure> bodiesNow, List<string> featureTagsNow,
        Dictionary<string, object> semanticSnapshot, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence["checks"] = checks;
        Sketch currentSketch = FindSketch(state.WorkPart, plan.SketchId);
        bool sketchIdentity = currentSketch != null &&
            String.Equals(RequiredJournalId(currentSketch, "sketch"), plan.SketchId, StringComparison.Ordinal) &&
            String.Equals(currentSketch.Tag.ToString(), plan.SketchTag, StringComparison.Ordinal) &&
            currentSketch.Feature != null && String.Equals(currentSketch.Feature.Tag.ToString(), plan.FeatureTag, StringComparison.Ordinal);
        checks["sketch_identity"] = sketchIdentity;

        Curve[] liveSources = new Curve[plan.InputCurveTags.Length];
        bool sourcesResolved = currentSketch != null;
        for (int i = 0; i < plan.InputCurveTags.Length && currentSketch != null; i++)
        {
            liveSources[i] = FindCornerCurveByTag(currentSketch, plan.InputCurveTags[i]);
            if (liveSources[i] == null || !currentSketch.IsSketchGeometry(liveSources[i])) sourcesResolved = false;
        }
        object sourceEvidence;
        bool sourcesPreserved = VerifyOffsetOriginalGeometry(plan, geometryNow, out sourceEvidence);
        checks["source_geometry"] = sourceEvidence;
        checks["source_curves_resolved"] = sourcesResolved;

        Curve[] liveOutputs = new Curve[plan.OutputCurveTags == null ? 0 : plan.OutputCurveTags.Length];
        bool outputsResolved = currentSketch != null && liveOutputs.Length > 0;
        for (int i = 0; i < liveOutputs.Length && currentSketch != null; i++)
        {
            liveOutputs[i] = FindCornerCurveByTag(currentSketch, plan.OutputCurveTags[i]);
            if (liveOutputs[i] == null || !currentSketch.IsSketchGeometry(liveOutputs[i])) outputsResolved = false;
        }
        checks["output_curves_resolved"] = outputsResolved;

        Dictionary<string, object> geometryEvidence;
        bool offsetGeometry = false;
        if (sourcesResolved && outputsResolved)
            offsetGeometry = ValidateNativeOffsetOutput(plan, liveSources, liveOutputs, out geometryEvidence);
        else
            geometryEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "valid", false },
                { "reason", "one or more source or output curves could not be independently resolved from the target Sketch" }
            };
        checks["offset_geometry"] = geometryEvidence;
        evidence["offset_geometry"] = geometryEvidence;
        object liveOutputClosed;
        evidence["output_closed"] = geometryEvidence.TryGetValue("output_closed", out liveOutputClosed) &&
            Boolean.Equals(liveOutputClosed, true);
        object liveOutputRecords;
        if (geometryEvidence.TryGetValue("output_curve_records", out liveOutputRecords))
            evidence["output_curve_records"] = liveOutputRecords;
        object addedEvidence;
        bool addedGeometryMatches = VerifyOffsetAddedGeometry(plan, geometryNow, out addedEvidence);
        checks["added_geometry"] = addedEvidence;
        bool constraintsPreserved = OffsetConstraintMapsEqual(plan.ConstraintsBefore, constraintsNow);
        checks["constraints_preserved"] = constraintsPreserved;
        bool framesPreserved = OffsetStringMapsEqual(plan.FramesBefore, framesNow);
        checks["sketch_frames_preserved"] = framesPreserved;
        object referenceEvidence;
        bool referencesPreserved = VerifyOffsetReferenceStates(plan, referencesNow, out referenceEvidence);
        checks["reference_status"] = referenceEvidence;
        object nonTargetEvidence;
        bool nonTargetsPreserved = VerifyOffsetNonTargetGeometry(plan, geometryNow, out nonTargetEvidence);
        checks["non_target_geometry"] = nonTargetEvidence;
        Dictionary<string, object> bodyEvidence;
        bool bodiesPreserved = BodyMeasuresEqual(plan.BodiesBefore, bodiesNow, out bodyEvidence);
        checks["bodies_preserved"] = bodyEvidence;
        bool featuresPreserved = FeatureTagsMatch(plan.FeatureTagsBefore, featureTagsNow);
        checks["feature_tags_preserved"] = featuresPreserved;
        bool historyPreserved = semanticSnapshot != null &&
            String.Equals(plan.ExpressionsBefore, M2Json.CanonicalText(SnapshotList(semanticSnapshot, "expressions")), StringComparison.Ordinal) &&
            String.Equals(plan.HistoryBefore, M2Json.CanonicalText(HistoryProjection(semanticSnapshot)), StringComparison.Ordinal) &&
            String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(semanticSnapshot), StringComparison.Ordinal) &&
            String.Equals(plan.HistoryAfter, M2Json.CanonicalText(HistoryProjection(semanticSnapshot)), StringComparison.Ordinal) &&
            FeatureTagsMatch(plan.FeatureTagsAfter, featureTagsNow);
        checks["history"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "expressions_match_before", semanticSnapshot != null && String.Equals(plan.ExpressionsBefore, M2Json.CanonicalText(SnapshotList(semanticSnapshot, "expressions")), StringComparison.Ordinal) },
            { "history_matches_before", semanticSnapshot != null && String.Equals(plan.HistoryBefore, M2Json.CanonicalText(HistoryProjection(semanticSnapshot)), StringComparison.Ordinal) },
            { "semantic_matches_execute", semanticSnapshot != null && String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(semanticSnapshot), StringComparison.Ordinal) },
            { "history_matches_execute", semanticSnapshot != null && String.Equals(plan.HistoryAfter, M2Json.CanonicalText(HistoryProjection(semanticSnapshot)), StringComparison.Ordinal) },
            { "feature_tags_match_execute", FeatureTagsMatch(plan.FeatureTagsAfter, featureTagsNow) },
            { "preserved", historyPreserved }
        };
        Dictionary<string, object> solverEvidence = CaptureCornerSketchStatus(currentSketch);
        checks["sketch_solver_status"] = solverEvidence;
        bool solverAcceptable = Boolean.Equals(solverEvidence["acceptable"], true);
        bool noActiveSketch = state.Session.ActiveSketch == null;
        checks["no_active_sketch"] = noActiveSketch;

        bool valid = sketchIdentity && sourcesResolved && sourcesPreserved && outputsResolved && offsetGeometry &&
            addedGeometryMatches && constraintsPreserved && framesPreserved && referencesPreserved &&
            nonTargetsPreserved && bodiesPreserved && featuresPreserved && historyPreserved && solverAcceptable && noActiveSketch;
        evidence["verified"] = valid;
        evidence["native_operation"] = CloneCornerDictionary(plan.NativeOperationEvidence);
        evidence["source_chain_closed"] = plan.InputClosed;
        object outputClosedValue;
        evidence["output_closed"] = geometryEvidence.TryGetValue("output_closed", out outputClosedValue) &&
            Boolean.Equals(outputClosedValue, true);
        evidence["output_curve_records"] = geometryEvidence.ContainsKey("output_curve_records") ? geometryEvidence["output_curve_records"] : null;
        return valid;
    }

    private bool VerifyOffsetOriginalGeometry(M3SketchOffsetPlanState plan,
        Dictionary<string, CornerGeometrySnapshot> current, out object evidence)
    {
        int preserved = 0;
        List<object> missing = new List<object>();
        List<object> changed = new List<object>();
        foreach (string sourceTag in plan.InputCurveTags)
        {
            CornerGeometrySnapshot before;
            if (!plan.GeometryBefore.TryGetValue(sourceTag, out before) || before == null)
            {
                missing.Add(sourceTag);
                continue;
            }
            CornerGeometrySnapshot now;
            if (!current.TryGetValue(sourceTag, out now) || now == null)
            {
                missing.Add(sourceTag);
                continue;
            }
            if (!String.Equals(before.Signature, now.Signature, StringComparison.Ordinal) ||
                !String.Equals(before.SketchTag, now.SketchTag, StringComparison.Ordinal))
                changed.Add(sourceTag);
            else preserved++;
        }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "selected_source_curve_count", plan.InputCurveTags.Length },
            { "preserved_count", preserved },
            { "missing_tags", missing },
            { "changed_tags", changed }
        };
        return missing.Count == 0 && changed.Count == 0;
    }

    private static bool VerifyOffsetAddedGeometry(M3SketchOffsetPlanState plan,
        Dictionary<string, CornerGeometrySnapshot> current, out object evidence)
    {
        HashSet<string> baseline = new HashSet<string>(plan.GeometryBefore.Keys, StringComparer.Ordinal);
        HashSet<string> expected = new HashSet<string>(plan.OutputCurveTags ?? new string[0], StringComparer.Ordinal);
        List<object> unexpected = new List<object>();
        List<object> missing = new List<object>();
        foreach (string tag in current.Keys)
            if (!baseline.Contains(tag) && !expected.Contains(tag)) unexpected.Add(tag);
        foreach (string tag in expected)
            if (!current.ContainsKey(tag)) missing.Add(tag);
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "expected_output_tags", StringListObjects(plan.OutputCurveTags ?? new string[0]) },
            { "actual_added_count", current.Count - baseline.Count },
            { "unexpected_added_tags", unexpected },
            { "missing_output_tags", missing }
        };
        return unexpected.Count == 0 && missing.Count == 0 && current.Count - baseline.Count == expected.Count;
    }

    private bool VerifyOffsetReferenceStates(M3SketchOffsetPlanState plan,
        Dictionary<string, int> current, out object evidence)
    {
        List<object> changed = new List<object>();
        List<object> missing = new List<object>();
        int preserved = 0;
        foreach (KeyValuePair<string, int> pair in plan.ReferenceStatesBefore)
        {
            int value;
            if (!current.TryGetValue(pair.Key, out value)) missing.Add(pair.Key);
            else if (value != pair.Value) changed.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", pair.Key }, { "before", pair.Value }, { "after", value }
            });
            else preserved++;
        }
        List<object> outputStatuses = new List<object>();
        bool outputsActive = true;
        foreach (string tag in plan.OutputCurveTags ?? new string[0])
        {
            int value;
            bool found = current.TryGetValue(tag, out value);
            outputStatuses.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", tag }, { "found", found }, { "status", found ? (object)value : null }
            });
            if (!found || value != 1) outputsActive = false;
        }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "baseline_curve_count", plan.ReferenceStatesBefore.Count },
            { "preserved_count", preserved },
            { "missing_tags", missing },
            { "changed", changed },
            { "output_curves_active", outputsActive },
            { "output_reference_status", outputStatuses },
            { "status_values", new Dictionary<string, object>(StringComparer.Ordinal) { { "reference", 0 }, { "active", 1 } } }
        };
        return missing.Count == 0 && changed.Count == 0 && outputsActive;
    }

    private static bool VerifyOffsetNonTargetGeometry(M3SketchOffsetPlanState plan,
        Dictionary<string, CornerGeometrySnapshot> current, out object evidence)
    {
        int preserved = 0;
        List<object> missing = new List<object>();
        List<object> changed = new List<object>();
        HashSet<string> sourceTags = new HashSet<string>(plan.InputCurveTags, StringComparer.Ordinal);
        foreach (KeyValuePair<string, CornerGeometrySnapshot> pair in plan.GeometryBefore)
        {
            if (sourceTags.Contains(pair.Key)) continue;
            CornerGeometrySnapshot now;
            if (!current.TryGetValue(pair.Key, out now) || now == null) missing.Add(pair.Key);
            else if (!String.Equals(pair.Value.Signature, now.Signature, StringComparison.Ordinal) ||
                !String.Equals(pair.Value.SketchTag, now.SketchTag, StringComparison.Ordinal)) changed.Add(pair.Key);
            else preserved++;
        }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "baseline_non_target_geometry_count", plan.GeometryBefore.Count - sourceTags.Count },
            { "preserved_count", preserved }, { "missing_tags", missing }, { "changed_tags", changed }
        };
        return missing.Count == 0 && changed.Count == 0;
    }

    private bool ValidateNativeOffsetOutput(M3SketchOffsetPlanState plan, Curve[] outputs,
        out Dictionary<string, object> evidence)
    {
        return ValidateNativeOffsetOutput(plan, plan.InputCurves, outputs, out evidence);
    }

    private bool ValidateNativeOffsetOutput(M3SketchOffsetPlanState plan, Curve[] sources,
        Curve[] outputs, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> topology = AnalyzeOffsetCurveTopology(outputs,
            plan.SketchOrigin, plan.SketchXAxis, plan.SketchYAxis, plan.SketchNormal);
        evidence["native_output_count"] = outputs == null ? 0 : outputs.Length;
        evidence["topology"] = topology;
        if (sources == null || outputs == null || sources.Length != plan.InputCurves.Length || outputs.Length < sources.Length)
        {
            evidence["valid"] = false;
            evidence["reason"] = "native output count cannot contain one offset result for every source curve";
            return false;
        }
        if (!Boolean.Equals(topology["valid_chain"], true))
        {
            evidence["valid"] = false;
            evidence["reason"] = topology["reason"];
            return false;
        }
        bool outputClosed = Boolean.Equals(topology["closed"], true);
        if (outputClosed != plan.InputClosed)
        {
            evidence["valid"] = false;
            evidence["reason"] = "native output closure does not match the source chain closure";
            evidence["output_closed"] = outputClosed;
            return false;
        }

        bool[] consumed = new bool[outputs.Length];
        int[] outputIndexForSource = new int[sources.Length];
        List<object> perInput = new List<object>();
        double expectedShift = plan.Request.Side == "left" ? plan.Request.DistanceMm : -plan.Request.DistanceMm;
        bool allMatched = true;
        for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
        {
            int matchIndex = -1;
            Dictionary<string, object> match = null;
            int candidateCount = 0;
            for (int outputIndex = 0; outputIndex < outputs.Length; outputIndex++)
            {
                if (consumed[outputIndex]) continue;
                Dictionary<string, object> candidate;
                if (!TryMeasureOffsetPair(plan, sourceIndex, sources[sourceIndex], outputs[outputIndex], expectedShift, out candidate))
                    continue;
                candidateCount++;
                matchIndex = outputIndex;
                match = candidate;
            }
            bool unique = candidateCount == 1;
            outputIndexForSource[sourceIndex] = unique ? matchIndex : -1;
            if (unique) consumed[matchIndex] = true;
            else allMatched = false;
            if (match == null) match = new Dictionary<string, object>(StringComparer.Ordinal);
            match["source_curve_id"] = plan.Request.CurveChain[sourceIndex].CurveId;
            match["source_curve_tag"] = plan.InputCurveTags[sourceIndex];
            match["unique_output_match"] = unique;
            if (unique)
            {
                match["output_curve_index"] = matchIndex;
                match["output_curve_tag"] = outputs[matchIndex].Tag.ToString();
                match["output_curve_id"] = RequiredJournalId(outputs[matchIndex], "offset output curve");
            }
            perInput.Add(match);
        }

        List<object> connectorCurves;
        bool connectorsValid = ValidateOffsetJoinConnectors(plan, sources, outputs, consumed, outputIndexForSource,
            out connectorCurves);

        Dictionary<string, object> endEvidence;
        bool openEndsValid = VerifyOffsetOpenEnds(plan, sources, outputs, outputIndexForSource, out endEvidence);
        evidence["per_source_curve"] = perInput;
        evidence["join_curves"] = connectorCurves;
        evidence["connectors_valid"] = connectorsValid;
        evidence["open_endpoints"] = endEvidence;
        evidence["output_closed"] = outputClosed;
        evidence["requested_closed"] = plan.InputClosed;
        evidence["requested_side"] = plan.Request.Side;
        evidence["distance_mm"] = plan.Request.DistanceMm;
        evidence["all_source_curves_matched"] = allMatched;
        evidence["output_curve_records"] = OffsetCurveRecords(outputs, null);
        bool valid = allMatched && connectorsValid && openEndsValid;
        evidence["valid"] = valid;
        if (!valid)
            evidence["reason"] = !allMatched
                ? "one or more source curves did not have a unique output at the requested side and distance"
                : !connectorsValid
                    ? "join output contains an unsupported or non-distance connector curve"
                    : "open chain end did not match the requested offset";
        return valid;
    }

    private bool ValidateOffsetJoinConnectors(M3SketchOffsetPlanState plan, Curve[] sources,
        Curve[] outputs, bool[] consumed, int[] outputIndexForSource, out List<object> records)
    {
        records = new List<object>();
        List<int> extraIndices = new List<int>();
        for (int i = 0; i < outputs.Length; i++)
            if (!consumed[i]) extraIndices.Add(i);
        bool valid = plan.Request.JoinMode != "extension" || extraIndices.Count == 0;
        HashSet<int> usedVertices = new HashSet<int>();
        foreach (int outputIndex in extraIndices)
        {
            Arc connector = outputs[outputIndex] as Arc;
            Dictionary<string, object> connectorEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            connectorEvidence["is_join_curve"] = true;
            int matchedVertex = -1;
            int candidateMatches = 0;
            Dictionary<string, object> matchEvidence = null;
            if (connector != null && plan.Request.JoinMode == "arc")
            {
                int vertexCount = plan.InputClosed ? sources.Length : Math.Max(0, sources.Length - 1);
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    int next = (vertex + 1) % sources.Length;
                    Dictionary<string, object> candidateEvidence;
                    if (!IsNativeArcJoinForVertex(plan, connector, vertex, next, out candidateEvidence)) continue;
                    candidateMatches++;
                    matchedVertex = vertex;
                    matchEvidence = candidateEvidence;
                }
            }
            bool unique = candidateMatches == 1 && matchedVertex >= 0 && !usedVertices.Contains(matchedVertex);
            if (unique)
            {
                usedVertices.Add(matchedVertex);
                foreach (KeyValuePair<string, object> pair in matchEvidence) connectorEvidence[pair.Key] = pair.Value;
                connectorEvidence["source_vertex_index"] = matchedVertex;
                connectorEvidence["unique_source_vertex"] = true;
            }
            else
            {
                connectorEvidence["unique_source_vertex"] = false;
                connectorEvidence["candidate_source_vertex_count"] = candidateMatches;
                valid = false;
            }
            Dictionary<string, object> curveRecord = OffsetCurveRecord(connector, null);
            foreach (KeyValuePair<string, object> pair in connectorEvidence) curveRecord[pair.Key] = pair.Value;
            records.Add(curveRecord);
        }
        int sourceVertexCount = plan.InputClosed ? sources.Length : Math.Max(0, sources.Length - 1);
        for (int vertex = 0; vertex < sourceVertexCount; vertex++)
        {
            int next = (vertex + 1) % sources.Length;
            int previousOutputIndex = outputIndexForSource[vertex];
            int nextOutputIndex = outputIndexForSource[next];
            if (previousOutputIndex < 0 || nextOutputIndex < 0) { valid = false; continue; }
            Point3d previousEnd = OffsetOutputTraversalEndpoint(plan, vertex, sources[vertex], outputs[previousOutputIndex], false);
            Point3d nextStart = OffsetOutputTraversalEndpoint(plan, next, sources[next], outputs[nextOutputIndex], true);
            bool connectorPresent = usedVertices.Contains(vertex);
            bool orderedConnection;
            if (connectorPresent)
                orderedConnection = PointDistance(previousEnd, OffsetChainEndpoint(plan, sources[vertex], false)) <= SketchOffsetPositionToleranceMm &&
                    PointDistance(nextStart, OffsetChainEndpoint(plan, sources[next], true)) <= SketchOffsetPositionToleranceMm;
            else
                orderedConnection = PointDistance(previousEnd, nextStart) <= SketchOffsetPositionToleranceMm;
            Vector3d previousTangent = OffsetTraversalTangent(plan, vertex, false);
            Vector3d nextTangent = OffsetTraversalTangent(plan, next, true);
            double turn = Dot(CrossCornerVectors(previousTangent, nextTangent), plan.SketchNormal);
            double theoreticalEndpointSeparation = PointDistance(OffsetChainEndpoint(plan, sources[vertex], false),
                OffsetChainEndpoint(plan, sources[next], true));
            bool outerTurn = plan.Request.Side == "left" ? turn < 0.0 : turn > 0.0;
            bool reversedTangents = turn == 0.0 && Dot(previousTangent, nextTangent) < 0.0;
            bool requiresArc = plan.Request.JoinMode == "arc" && (outerTurn || reversedTangents) &&
                theoreticalEndpointSeparation > SketchOffsetPositionToleranceMm;
            if (!orderedConnection || (requiresArc && !connectorPresent))
            {
                valid = false;
                records.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "source_vertex_index", vertex }, { "ordered_connection", orderedConnection },
                    { "requires_arc_connector", requiresArc }, { "connector_present", connectorPresent },
                    { "previous_output_endpoint_mm", Point(previousEnd) }, { "next_output_endpoint_mm", Point(nextStart) }
                });
            }
        }
        return valid;
    }

    private static Point3d OffsetOutputTraversalEndpoint(M3SketchOffsetPlanState plan, int sourceIndex,
        Curve source, Curve output, bool orientedStart)
    {
        bool sameNaturalDirection = true;
        Line sourceLine = source as Line;
        Line outputLine = output as Line;
        if (sourceLine != null && outputLine != null)
            sameNaturalDirection = Dot(Subtract(sourceLine.EndPoint, sourceLine.StartPoint),
                Subtract(outputLine.EndPoint, outputLine.StartPoint)) > 0.0;
        bool nativeStart = orientedStart == (plan.Request.CurveChain[sourceIndex].Direction == "forward");
        if (!sameNaturalDirection) nativeStart = !nativeStart;
        return CurveEndpoint(output, nativeStart ? "start" : "end");
    }

    private bool IsNativeArcJoinForVertex(M3SketchOffsetPlanState plan, Arc connector,
        int previousIndex, int nextIndex, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (connector == null || !IsFinite(connector.Radius) ||
            Math.Abs(connector.Radius - plan.Request.DistanceMm) > SketchOffsetPositionToleranceMm)
            return false;
        double sweep = ArcSweep(connector);
        if (!IsFinite(sweep) || sweep <= SketchOffsetAngleTolerance ||
            sweep > Math.PI + SketchOffsetAngleTolerance) return false;
        Point3d joint = Midpoint(plan.InputEnds[previousIndex], plan.InputStarts[nextIndex]);
        double centerResidual = PointDistance(connector.CenterPoint, joint);
        if (!IsFinite(centerResidual) || centerResidual > SketchOffsetPositionToleranceMm) return false;
        Point3d expectedPrevious = OffsetChainEndpoint(plan, plan.InputCurves[previousIndex], false);
        Point3d expectedNext = OffsetChainEndpoint(plan, plan.InputCurves[nextIndex], true);
        Point3d start = CurveEndpoint(connector, "start");
        Point3d end = CurveEndpoint(connector, "end");
        double normalOrderResidual = Math.Max(PointDistance(start, expectedPrevious), PointDistance(end, expectedNext));
        double reverseOrderResidual = Math.Max(PointDistance(start, expectedNext), PointDistance(end, expectedPrevious));
        bool normalOrder = normalOrderResidual <= SketchOffsetPositionToleranceMm;
        bool reverseOrder = reverseOrderResidual <= SketchOffsetPositionToleranceMm;
        if (normalOrder == reverseOrder) return false;

        Vector3d previousTangent = OffsetTraversalTangent(plan, previousIndex, false);
        Vector3d nextTangent = OffsetTraversalTangent(plan, nextIndex, true);
        Vector3d tangentAwayStart = CurveTangentAwayFromEndpoint(connector, "start");
        Vector3d tangentAwayEnd = CurveTangentAwayFromEndpoint(connector, "end");
        NormalizeVector(ref tangentAwayStart, "join arc start tangent");
        NormalizeVector(ref tangentAwayEnd, "join arc end tangent");
        double previousDot;
        double nextDot;
        if (normalOrder)
        {
            previousDot = Dot(previousTangent, tangentAwayStart);
            nextDot = Dot(nextTangent, tangentAwayEnd);
            if (previousDot < 1.0 - SketchOffsetAngleTolerance ||
                nextDot > -1.0 + SketchOffsetAngleTolerance ||
                VectorLength(CrossCornerVectors(previousTangent, tangentAwayStart)) > SketchOffsetAngleTolerance ||
                VectorLength(CrossCornerVectors(nextTangent, tangentAwayEnd)) > SketchOffsetAngleTolerance) return false;
        }
        else
        {
            nextDot = Dot(nextTangent, tangentAwayStart);
            previousDot = Dot(previousTangent, tangentAwayEnd);
            if (nextDot > -1.0 + SketchOffsetAngleTolerance ||
                previousDot < 1.0 - SketchOffsetAngleTolerance ||
                VectorLength(CrossCornerVectors(nextTangent, tangentAwayStart)) > SketchOffsetAngleTolerance ||
                VectorLength(CrossCornerVectors(previousTangent, tangentAwayEnd)) > SketchOffsetAngleTolerance) return false;
        }
        evidence["center_residual_mm"] = centerResidual;
        evidence["radius_mm"] = connector.Radius;
        evidence["radius_residual_mm"] = Math.Abs(connector.Radius - plan.Request.DistanceMm);
        evidence["sweep_rad"] = sweep;
        evidence["expected_previous_endpoint_mm"] = Point(expectedPrevious);
        evidence["expected_next_endpoint_mm"] = Point(expectedNext);
        evidence["endpoint_order"] = normalOrder ? "previous_to_next" : "next_to_previous";
        evidence["previous_tangent_dot"] = previousDot;
        evidence["next_tangent_dot"] = nextDot;
        evidence["endpoint_residual_mm"] = normalOrder ? normalOrderResidual : reverseOrderResidual;
        return true;
    }

    private Vector3d OffsetTraversalTangent(M3SketchOffsetPlanState plan, int curveIndex, bool orientedStart)
    {
        string direction = plan.Request.CurveChain[curveIndex].Direction;
        bool nativeStart = orientedStart ? direction == "forward" : direction == "reverse";
        Vector3d tangent = OffsetCurveTangentAtEndpoint(plan.InputCurves[curveIndex], nativeStart);
        if (direction == "reverse") tangent = Scale(tangent, -1.0);
        NormalizeVector(ref tangent, "offset chain tangent");
        return tangent;
    }

    private bool TryMeasureOffsetPair(M3SketchOffsetPlanState plan, int sourceIndex,
        Curve source, Curve output, double expectedShift, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (source == null || output == null) return false;
        Line sourceLine = source as Line;
        Line outputLine = output as Line;
        if (sourceLine != null)
        {
            if (outputLine == null) return false;
            Vector3d naturalSourceTangent = new Vector3d(
                sourceLine.EndPoint.X - sourceLine.StartPoint.X,
                sourceLine.EndPoint.Y - sourceLine.StartPoint.Y,
                sourceLine.EndPoint.Z - sourceLine.StartPoint.Z);
            Vector3d outputTangent = new Vector3d(
                outputLine.EndPoint.X - outputLine.StartPoint.X,
                outputLine.EndPoint.Y - outputLine.StartPoint.Y,
                outputLine.EndPoint.Z - outputLine.StartPoint.Z);
            double sourceLength = VectorLength(naturalSourceTangent);
            double outputLength = VectorLength(outputTangent);
            if (!IsFinite(sourceLength) || !IsFinite(outputLength) ||
                sourceLength <= SketchOffsetPositionToleranceMm || outputLength <= SketchOffsetPositionToleranceMm)
                return false;
            naturalSourceTangent = Scale(naturalSourceTangent, 1.0 / sourceLength);
            outputTangent = Scale(outputTangent, 1.0 / outputLength);
            double orientationDot = Dot(naturalSourceTangent, outputTangent);
            Vector3d directionCross = CrossCornerVectors(naturalSourceTangent, outputTangent);
            double directionError = VectorLength(directionCross);
            if (Math.Abs(orientationDot) < 1.0 - SketchOffsetAngleTolerance ||
                !IsFinite(directionError) || directionError > SketchOffsetAngleTolerance)
                return false;
            Vector3d traversalTangent = naturalSourceTangent;
            if (plan.Request.CurveChain[sourceIndex].Direction == "reverse")
                traversalTangent = Scale(traversalTangent, -1.0);
            Vector3d left = CrossCornerVectors(plan.SketchNormal, traversalTangent);
            double leftLength = VectorLength(left);
            if (!IsFinite(leftLength) || leftLength <= SketchOffsetAngleTolerance) return false;
            left = Scale(left, 1.0 / leftLength);
            Vector3d startDisplacement = Subtract(outputLine.StartPoint, sourceLine.StartPoint);
            Vector3d endDisplacement = Subtract(outputLine.EndPoint, sourceLine.EndPoint);
            double signedStart = Dot(startDisplacement, left);
            double signedEnd = Dot(endDisplacement, left);
            double startResidual = Math.Abs(signedStart - expectedShift);
            double endResidual = Math.Abs(signedEnd - expectedShift);
            double startPlaneResidual = Math.Abs(Dot(startDisplacement, plan.SketchNormal));
            double endPlaneResidual = Math.Abs(Dot(endDisplacement, plan.SketchNormal));
            double residual = Math.Max(Math.Max(startResidual, endResidual),
                Math.Max(startPlaneResidual, endPlaneResidual));
            evidence["curve_type"] = "Line";
            evidence["measured_signed_offset_mm"] = signedStart;
            evidence["start_signed_offset_mm"] = signedStart;
            evidence["end_signed_offset_mm"] = signedEnd;
            evidence["start_offset_residual_mm"] = startResidual;
            evidence["end_offset_residual_mm"] = endResidual;
            evidence["start_plane_residual_mm"] = startPlaneResidual;
            evidence["end_plane_residual_mm"] = endPlaneResidual;
            evidence["offset_residual_mm"] = residual;
            evidence["orientation_dot"] = orientationDot;
            evidence["direction_cross_norm"] = directionError;
            evidence["source_length_mm"] = source.GetLength();
            evidence["output_length_mm"] = output.GetLength();
            return IsFinite(residual) && residual <= SketchOffsetPositionToleranceMm;
        }

        Arc sourceArc = source as Arc;
        Arc outputArc = output as Arc;
        if (sourceArc == null || outputArc == null) return false;
        Point3d sourceCenter;
        Point3d sourceFrameCenter;
        Vector3d sourceX, sourceY;
        sourceArc.GetOrientation(out sourceFrameCenter, out sourceX, out sourceY);
        sourceCenter = sourceArc.CenterPoint;
        Point3d outputFrameCenter;
        Vector3d outputX, outputY;
        outputArc.GetOrientation(out outputFrameCenter, out outputX, out outputY);
        Vector3d sourceNormal = CrossCornerVectors(sourceX, sourceY);
        Vector3d outputNormal = CrossCornerVectors(outputX, outputY);
        double sourceNormalLength = VectorLength(sourceNormal);
        double outputNormalLength = VectorLength(outputNormal);
        if (!IsFinite(sourceNormalLength) || !IsFinite(outputNormalLength) ||
            sourceNormalLength <= SketchOffsetAngleTolerance || outputNormalLength <= SketchOffsetAngleTolerance)
            return false;
        sourceNormal = Scale(sourceNormal, 1.0 / sourceNormalLength);
        outputNormal = Scale(outputNormal, 1.0 / outputNormalLength);
        double normalAlignment = Math.Abs(Dot(sourceNormal, outputNormal));
        double normalCrossError = VectorLength(CrossCornerVectors(sourceNormal, outputNormal));
        double centerResidual = PointDistance(sourceCenter, outputArc.CenterPoint);
        double radiusResidualMagnitude = Math.Abs(Math.Abs(outputArc.Radius - sourceArc.Radius) - plan.Request.DistanceMm);
        if (!IsFinite(centerResidual) || centerResidual > SketchOffsetPositionToleranceMm ||
            !IsFinite(normalAlignment) || normalAlignment < 1.0 - SketchOffsetAngleTolerance ||
            !IsFinite(normalCrossError) || normalCrossError > SketchOffsetAngleTolerance ||
            !IsFinite(radiusResidualMagnitude) || radiusResidualMagnitude > SketchOffsetPositionToleranceMm ||
            outputArc.Radius <= SketchOffsetPositionToleranceMm)
            return false;

        double sourceSweep = ArcSweep(sourceArc);
        double outputSweep = ArcSweep(outputArc);
        if (!IsFinite(sourceSweep) || !IsFinite(outputSweep) ||
            sourceSweep <= SketchOffsetAngleTolerance || outputSweep <= SketchOffsetAngleTolerance ||
            sourceSweep > 2.0 * Math.PI + SketchOffsetAngleTolerance ||
            outputSweep > 2.0 * Math.PI + SketchOffsetAngleTolerance)
            return false;
        bool sourceClosed = Math.Abs(sourceSweep - 2.0 * Math.PI) <= SketchOffsetAngleTolerance;
        bool outputClosed = Math.Abs(outputSweep - 2.0 * Math.PI) <= SketchOffsetAngleTolerance;
        if (sourceClosed != outputClosed) return false;

        double sampleAngle = ArcSampleAngle(sourceArc);
        Vector3d radial = new Vector3d(
            sourceX.X * Math.Cos(sampleAngle) + sourceY.X * Math.Sin(sampleAngle),
            sourceX.Y * Math.Cos(sampleAngle) + sourceY.Y * Math.Sin(sampleAngle),
            sourceX.Z * Math.Cos(sampleAngle) + sourceY.Z * Math.Sin(sampleAngle));
        double radialLength = VectorLength(radial);
        if (!IsFinite(radialLength) || radialLength <= SketchOffsetAngleTolerance) return false;
        radial = Scale(radial, 1.0 / radialLength);
        Vector3d tangent = new Vector3d(
            -sourceX.X * Math.Sin(sampleAngle) + sourceY.X * Math.Cos(sampleAngle),
            -sourceX.Y * Math.Sin(sampleAngle) + sourceY.Y * Math.Cos(sampleAngle),
            -sourceX.Z * Math.Sin(sampleAngle) + sourceY.Z * Math.Cos(sampleAngle));
        if (plan.Request.CurveChain[sourceIndex].Direction == "reverse")
            tangent = Scale(tangent, -1.0);
        Vector3d leftArc = CrossCornerVectors(plan.SketchNormal, tangent);
        double leftArcLength = VectorLength(leftArc);
        if (!IsFinite(leftArcLength) || leftArcLength <= SketchOffsetAngleTolerance) return false;
        leftArc = Scale(leftArc, 1.0 / leftArcLength);
        Vector3d radialDelta = Scale(radial, outputArc.Radius - sourceArc.Radius);
        double signedArc = Dot(radialDelta, leftArc);
        double sideResidual = Math.Abs(signedArc - expectedShift);
        double naturalTangentAlignment = OffsetArcNaturalTangentAlignment(sourceArc, outputArc, sampleAngle, radial);
        if (!IsFinite(naturalTangentAlignment) || naturalTangentAlignment < 1.0 - SketchOffsetAngleTolerance)
            return false;
        string direction = plan.Request.CurveChain[sourceIndex].Direction;
        Point3d expectedNativeStart = OffsetChainEndpoint(plan, sourceArc, direction == "forward");
        Point3d expectedNativeEnd = OffsetChainEndpoint(plan, sourceArc, direction == "reverse");
        double startEndpointResidual = PointDistance(CurveEndpoint(outputArc, "start"), expectedNativeStart);
        double endEndpointResidual = PointDistance(CurveEndpoint(outputArc, "end"), expectedNativeEnd);
        double startDomainDelta = 0.0;
        double endDomainDelta = 0.0;
        if (!sourceClosed)
        {
            Vector3d outputStartRadial = Subtract(CurveEndpoint(outputArc, "start"), sourceCenter);
            double outputStartInSourceFrame = Math.Atan2(Dot(outputStartRadial, sourceY), Dot(outputStartRadial, sourceX));
            startDomainDelta = SignedOffsetAngle(outputStartInSourceFrame - sourceArc.StartAngle);
            endDomainDelta = startDomainDelta + outputSweep - sourceSweep;
            if (plan.InputCurves.Length == 1)
            {
                if (Math.Abs(outputSweep - sourceSweep) > SketchOffsetAngleTolerance ||
                    startEndpointResidual > SketchOffsetPositionToleranceMm ||
                    endEndpointResidual > SketchOffsetPositionToleranceMm) return false;
            }
            else if (Math.Abs(startDomainDelta) >= Math.PI - SketchOffsetAngleTolerance ||
                Math.Abs(endDomainDelta) >= Math.PI - SketchOffsetAngleTolerance ||
                startDomainDelta >= sourceSweep - SketchOffsetAngleTolerance ||
                startDomainDelta + outputSweep <= SketchOffsetAngleTolerance)
                return false;
        }
        if (!IsFinite(startEndpointResidual) || !IsFinite(endEndpointResidual)) return false;
        evidence["curve_type"] = "Arc";
        evidence["measured_signed_offset_mm"] = signedArc;
        evidence["offset_residual_mm"] = sideResidual;
        evidence["center_residual_mm"] = centerResidual;
        evidence["radius_before_mm"] = sourceArc.Radius;
        evidence["radius_after_mm"] = outputArc.Radius;
        evidence["radius_delta_mm"] = outputArc.Radius - sourceArc.Radius;
        evidence["plane_normal_alignment"] = normalAlignment;
        evidence["plane_normal_cross_norm"] = normalCrossError;
        evidence["natural_tangent_alignment"] = naturalTangentAlignment;
        evidence["source_sweep_rad"] = sourceSweep;
        evidence["output_sweep_rad"] = outputSweep;
        evidence["start_endpoint_residual_mm"] = startEndpointResidual;
        evidence["end_endpoint_residual_mm"] = endEndpointResidual;
        evidence["start_domain_delta_rad"] = startDomainDelta;
        evidence["end_domain_delta_rad"] = endDomainDelta;
        evidence["source_closed"] = sourceClosed;
        evidence["output_closed"] = outputClosed;
        return IsFinite(sideResidual) && sideResidual <= SketchOffsetPositionToleranceMm;
    }

    private bool VerifyOffsetOpenEnds(M3SketchOffsetPlanState plan, Curve[] sources,
        Curve[] outputs, int[] outputIndexForSource, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (plan.InputClosed)
        {
            evidence["applicable"] = false;
            evidence["reason"] = "closed source chain has no remote open endpoints";
            return true;
        }
        int last = sources.Length - 1;
        int firstOutput = outputIndexForSource.Length == 0 ? -1 : outputIndexForSource[0];
        int lastOutput = outputIndexForSource.Length == 0 ? -1 : outputIndexForSource[last];
        if (firstOutput < 0 || lastOutput < 0 || firstOutput >= outputs.Length || lastOutput >= outputs.Length)
        {
            evidence["applicable"] = true;
            evidence["valid"] = false;
            evidence["reason"] = "source endpoint outputs were not uniquely matched";
            return false;
        }
        Point3d expectedStart = OffsetChainEndpoint(plan, sources[0], true);
        Point3d expectedEnd = OffsetChainEndpoint(plan, sources[last], false);
        double startResidual = PointToCurveEndpointDistance(outputs[firstOutput], expectedStart);
        double endResidual = PointToCurveEndpointDistance(outputs[lastOutput], expectedEnd);
        evidence["applicable"] = true;
        evidence["expected_start_mm"] = Point(expectedStart);
        evidence["expected_end_mm"] = Point(expectedEnd);
        evidence["start_residual_mm"] = startResidual;
        evidence["end_residual_mm"] = endResidual;
        evidence["valid"] = IsFinite(startResidual) && IsFinite(endResidual) &&
            startResidual <= SketchOffsetPositionToleranceMm && endResidual <= SketchOffsetPositionToleranceMm;
        return Boolean.Equals(evidence["valid"], true);
    }

    private Point3d OffsetChainEndpoint(M3SketchOffsetPlanState plan, Curve curve, bool orientedStart)
    {
        string direction = null;
        for (int i = 0; i < plan.InputCurves.Length; i++)
            if (plan.InputCurves[i].Tag.Equals(curve.Tag))
            {
                direction = plan.Request.CurveChain[i].Direction;
                break;
            }
        if (direction == null) throw new M2ModelException("PLAN_TARGET_MISMATCH", "offset endpoint source is not in the retained chain");
        bool nativeStart = orientedStart ? direction == "forward" : direction == "reverse";
        string endpoint = nativeStart ? "start" : "end";
        Point3d point = CurveEndpoint(curve, endpoint);
        Vector3d tangent = OffsetCurveTangentAtEndpoint(curve, nativeStart);
        if (direction == "reverse") tangent = Scale(tangent, -1.0);
        NormalizeVector(ref tangent, "offset chain endpoint tangent");
        Vector3d left = CrossCornerVectors(plan.SketchNormal, tangent);
        NormalizeVector(ref left, "offset chain endpoint side");
        double shift = plan.Request.Side == "left" ? plan.Request.DistanceMm : -plan.Request.DistanceMm;
        return Add(point, Scale(left, shift));
    }

    private static double PointToCurveEndpointDistance(Curve curve, Point3d point)
    {
        return Math.Min(PointDistance(CurveEndpoint(curve, "start"), point),
            PointDistance(CurveEndpoint(curve, "end"), point));
    }

    private static M3SketchOffsetRequest CloneM3SketchOffsetRequest(M3SketchOffsetRequest source)
    {
        if (source == null) return null;
        M3SketchOffsetRequest clone = new M3SketchOffsetRequest
        {
            RevisionId = source.RevisionId,
            SketchId = source.SketchId,
            DistanceMm = source.DistanceMm,
            Side = source.Side,
            RetainOriginal = source.RetainOriginal,
            JoinMode = source.JoinMode,
            CurveChain = new List<M3SketchOffsetCurveRequest>()
        };
        foreach (M3SketchOffsetCurveRequest curve in source.CurveChain)
            clone.CurveChain.Add(new M3SketchOffsetCurveRequest { CurveId = curve.CurveId, Direction = curve.Direction });
        return clone;
    }

    private static Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> CaptureAllSketchOffsetConstraints(Part part)
    {
        if (part == null) throw new M2ModelException("MODEL_READ_FAILED", "Work Part is unavailable during F08 constraint capture");
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory returned null during F08 constraint capture");
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> result =
            new Dictionary<string, Dictionary<string, CornerConstraintSnapshot>>(StringComparer.Ordinal);
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory contains null during F08 constraint capture");
            string tag = sketch.Tag.ToString();
            if (result.ContainsKey(tag)) throw new M2ModelException("MODEL_READ_FAILED", "Sketch Tag is duplicated during F08 constraint capture");
            result.Add(tag, CaptureSketchCornerConstraints(sketch));
        }
        return result;
    }

    private static Dictionary<string, string> CaptureAllSketchOffsetFrames(Part part)
    {
        if (part == null) throw new M2ModelException("MODEL_READ_FAILED", "Work Part is unavailable during F08 frame capture");
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory returned null during F08 frame capture");
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory contains null during F08 frame capture");
            Dictionary<string, object> frame = new Dictionary<string, object>(StringComparer.Ordinal);
            frame["journal_id"] = RequiredJournalId(sketch, "sketch");
            frame["feature_tag"] = sketch.Feature == null ? null : sketch.Feature.Tag.ToString();
            frame["frame"] = M4SketchFrameRecord(sketch);
            string tag = sketch.Tag.ToString();
            if (result.ContainsKey(tag)) throw new M2ModelException("MODEL_READ_FAILED", "Sketch Tag is duplicated during F08 frame capture");
            result.Add(tag, M2Json.CanonicalText(frame));
        }
        return result;
    }

    private static Dictionary<string, int> CaptureAllSketchOffsetReferenceStates(Part part)
    {
        if (part == null) throw new M2ModelException("MODEL_READ_FAILED", "Work Part is unavailable during F08 reference-state capture");
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory returned null during F08 reference-state capture");
        Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.Ordinal);
        UFSession uf = UFSession.GetUFSession();
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory contains null during F08 reference-state capture");
            NXObject[] geometry = sketch.GetAllGeometry();
            if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null during F08 reference-state capture");
            foreach (NXObject item in geometry)
            {
                Curve curve = item as Curve;
                if (curve == null) continue;
                string tag = curve.Tag.ToString();
                if (String.IsNullOrEmpty(tag) || result.ContainsKey(tag))
                    throw new M2ModelException("MODEL_READ_FAILED", "curve Tag is missing or duplicated during F08 reference-state capture");
                UFSket.ReferenceStatus status;
                try { uf.Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status); }
                catch (Exception ex)
                {
                    throw new M2ModelException("MODEL_READ_FAILED",
                        "UF.Sket.AskReferenceStatus failed while capturing F08 curve state (" + ex.GetType().Name + ")");
                }
                result.Add(tag, Convert.ToInt32(status, CultureInfo.InvariantCulture));
            }
        }
        return result;
    }

    private static Dictionary<string, object> OffsetInputCurveRecords(M3SketchOffsetPlanState plan)
    {
        List<object> records = new List<object>();
        for (int i = 0; i < plan.InputCurves.Length; i++)
            records.Add(OffsetCurveRecord(plan.InputCurves[i], plan.Request.CurveChain[i].Direction));
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "count", records.Count },
            { "curves", records }
        };
    }

    private static List<object> OffsetRequestedCurveChain(M3SketchOffsetPlanState plan)
    {
        List<object> records = new List<object>();
        foreach (M3SketchOffsetCurveRequest curve in plan.Request.CurveChain)
            records.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "curve_id", curve.CurveId }, { "direction", curve.Direction }
            });
        return records;
    }

    private static Dictionary<string, object> OffsetCurveRecord(Curve curve, string direction)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
        if (curve == null) return record;
        record["journal_id"] = RequiredJournalId(curve, "offset curve");
        record["tag"] = curve.Tag.ToString();
        record["object_type"] = curve.GetType().Name;
        if (direction != null) record["direction"] = direction;
        Line line = curve as Line;
        if (line != null)
        {
            record["start_mm"] = Point(line.StartPoint);
            record["end_mm"] = Point(line.EndPoint);
            return record;
        }
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center;
            Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            record["center_mm"] = Point(arc.CenterPoint);
            record["radius_mm"] = arc.Radius;
            record["start_angle_rad"] = arc.StartAngle;
            record["end_angle_rad"] = arc.EndAngle;
            record["sweep_rad"] = ArcSweep(arc);
            record["start_mm"] = Point(CurveEndpoint(arc, "start"));
            record["end_mm"] = Point(CurveEndpoint(arc, "end"));
            record["plane_x_axis"] = Vector(xAxis.X, xAxis.Y, xAxis.Z);
            record["plane_y_axis"] = Vector(yAxis.X, yAxis.Y, yAxis.Z);
            return record;
        }
        throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "F08 result readback supports Line and circular Arc curves only");
    }

    private static List<object> OffsetCurveRecords(Curve[] curves, string[] directions)
    {
        List<object> records = new List<object>();
        if (curves == null) return records;
        for (int i = 0; i < curves.Length; i++)
            records.Add(OffsetCurveRecord(curves[i], directions != null && i < directions.Length ? directions[i] : null));
        return records;
    }

    private static string[] OffsetCurveTags(Curve[] curves)
    {
        if (curves == null) return new string[0];
        string[] result = new string[curves.Length];
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < curves.Length; i++)
        {
            if (curves[i] == null) throw new M2ModelException("OFFSET_OUTPUT_INVALID", "native output contains a null Curve");
            result[i] = curves[i].Tag.ToString();
            if (String.IsNullOrEmpty(result[i]) || !seen.Add(result[i]))
                throw new M2ModelException("OFFSET_OUTPUT_INVALID", "native output contains a missing or duplicate Curve Tag");
        }
        return result;
    }

    private static string[] OffsetCurveIds(Curve[] curves)
    {
        if (curves == null) return new string[0];
        string[] result = new string[curves.Length];
        for (int i = 0; i < curves.Length; i++)
            result[i] = RequiredJournalId(curves[i], "offset output curve");
        return result;
    }

    private static int CountOffsetConstraints(
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraints)
    {
        int count = 0;
        if (constraints != null)
            foreach (Dictionary<string, CornerConstraintSnapshot> sketchConstraints in constraints.Values)
                if (sketchConstraints != null) count += sketchConstraints.Count;
        return count;
    }

    private static bool OffsetConstraintMapsEqual(
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> expected,
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> actual)
    {
        if (expected == null || actual == null || expected.Count != actual.Count) return false;
        foreach (KeyValuePair<string, Dictionary<string, CornerConstraintSnapshot>> pair in expected)
        {
            Dictionary<string, CornerConstraintSnapshot> current;
            if (!actual.TryGetValue(pair.Key, out current) || !CornerConstraintMapsEqual(pair.Value, current)) return false;
        }
        return true;
    }

    private static bool OffsetStringMapsEqual(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        if (expected == null || actual == null || expected.Count != actual.Count) return false;
        foreach (KeyValuePair<string, string> pair in expected)
        {
            string value;
            if (!actual.TryGetValue(pair.Key, out value) || !String.Equals(pair.Value, value, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool OffsetIntMapsEqual(Dictionary<string, int> expected, Dictionary<string, int> actual)
    {
        if (expected == null || actual == null || expected.Count != actual.Count) return false;
        foreach (KeyValuePair<string, int> pair in expected)
        {
            int value;
            if (!actual.TryGetValue(pair.Key, out value) || pair.Value != value) return false;
        }
        return true;
    }

    private static bool OffsetGeometryMapsEqual(Dictionary<string, CornerGeometrySnapshot> expected,
        Dictionary<string, CornerGeometrySnapshot> actual)
    {
        return CornerGeometryMapsEqual(expected, actual);
    }

    private static Point3d ReadFramePoint(Dictionary<string, object> frame, string key)
    {
        Point3d value;
        if (!TryReadCornerRecordPoint(frame, key, out value))
            throw new M2ModelException("MODEL_READ_FAILED", "Sketch frame is missing " + key);
        return value;
    }

    private static Vector3d ReadFrameVector(Dictionary<string, object> frame, string key)
    {
        Vector3d value;
        if (!TryReadCornerRecordVector(frame, key, out value))
            throw new M2ModelException("MODEL_READ_FAILED", "Sketch frame is missing " + key);
        return value;
    }

    private static string SafeOffsetExceptionMessage(Exception exception)
    {
        if (exception == null) return null;
        M2ModelException model = exception as M2ModelException;
        if (model != null) return model.Message;
        AggregateException aggregate = exception as AggregateException;
        if (aggregate != null) return aggregate.Message;
        return exception.Message ?? exception.GetType().Name;
    }

    private static Dictionary<string, object> BuildSketchOffsetEvidence(M3SketchOffsetPlanState plan,
        Dictionary<string, object> liveEvidence, Dictionary<string, object> rollback)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence["requested"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "sketch_id", plan.SketchId },
            { "curve_chain", OffsetRequestedCurveChain(plan) },
            { "distance_mm", plan.Request.DistanceMm },
            { "side", plan.Request.Side },
            { "retain_original", plan.Request.RetainOriginal },
            { "join_mode", plan.Request.JoinMode },
            { "source_chain_closed", plan.InputClosed }
        };
        evidence["native_operation"] = CloneCornerDictionary(plan.NativeOperationEvidence);
        if (liveEvidence != null) evidence["live_geometry"] = CloneCornerDictionary(liveEvidence);
        if (rollback != null) evidence["rollback"] = CloneCornerDictionary(rollback);
        if (plan.OutputCurveTags != null) evidence["output_curve_tags"] = StringListObjects(plan.OutputCurveTags);
        if (plan.OutputCurveIds != null) evidence["output_curve_ids"] = StringListObjects(plan.OutputCurveIds);
        return evidence;
    }

    private static Dictionary<string, object> BuildSketchOffsetExecutionResult(M3SketchOffsetPlanState plan,
        string revisionId, string mutationState, bool verified, Dictionary<string, object> evidence)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result["kind"] = "edit_sketch_offset";
        result["revision_id"] = revisionId;
        result["sketch_id"] = plan.SketchId;
        result["distance_mm"] = plan.Request.DistanceMm;
        result["side"] = plan.Request.Side;
        result["retain_original"] = plan.Request.RetainOriginal;
        result["join_mode"] = plan.Request.JoinMode;
        result["curve_chain"] = OffsetRequestedCurveChain(plan);
        result["source_chain_closed"] = plan.InputClosed;
        result["mutation_state"] = mutationState;
        result["verified"] = verified;
        result["output_curve_tags"] = StringListObjects(plan.OutputCurveTags ?? new string[0]);
        result["output_curve_ids"] = StringListObjects(plan.OutputCurveIds ?? new string[0]);
        object liveValue;
        Dictionary<string, object> live = evidence != null && evidence.TryGetValue("live_geometry", out liveValue)
            ? liveValue as Dictionary<string, object> : null;
        if (live == null && evidence != null) live = evidence;
        object closed;
        if (live != null && live.TryGetValue("output_closed", out closed))
            result["output_closed"] = closed;
        object outputRecords;
        if (live != null && live.TryGetValue("output_curve_records", out outputRecords))
            result["output_curve_records"] = outputRecords;
        if (evidence != null) result["evidence"] = CloneCornerDictionary(evidence);
        return result;
    }

    private sealed class OffsetPoint2d
    {
        internal double X;
        internal double Y;
        internal OffsetPoint2d(double x, double y) { X = x; Y = y; }
    }

    private sealed class OffsetCurve2d
    {
        internal Curve Native;
        internal bool IsLine;
        internal OffsetPoint2d Start;
        internal OffsetPoint2d End;
        internal OffsetPoint2d Center;
        internal double Radius;
        internal double StartAngle;
        internal double Sweep;
        internal double AxisXX;
        internal double AxisXY;
        internal double AxisYX;
        internal double AxisYY;
    }

    private static Dictionary<string, object> AnalyzeOffsetCurveTopology(Curve[] curves)
    {
        return AnalyzeOffsetCurveTopology(curves, new Point3d(), new Vector3d(1.0, 0.0, 0.0),
            new Vector3d(0.0, 1.0, 0.0), new Vector3d(0.0, 0.0, 1.0));
    }

    private static Dictionary<string, object> AnalyzeOffsetCurveTopology(Curve[] curves,
        Point3d sketchOrigin, Vector3d sketchXAxis, Vector3d sketchYAxis, Vector3d sketchNormal)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (curves == null || curves.Length == 0)
        {
            result["valid_chain"] = false;
            result["closed"] = false;
            result["open_end_count"] = 0;
            result["component_count"] = 0;
            result["self_intersection_count"] = 0;
            result["reason"] = "curve set is empty";
            return result;
        }

        List<Point3d> nodes = new List<Point3d>();
        List<int> degrees = new List<int>();
        List<int> parents = new List<int>();
        List<int[]> curveNodes = new List<int[]>();
        bool curveTypesValid = true;
        string typeFailure = null;
        for (int i = 0; i < curves.Length; i++)
        {
            Curve curve = curves[i];
            if (curve == null)
            {
                curveTypesValid = false;
                typeFailure = "curve set contains null";
                break;
            }
            if (!(curve is Line) && !(curve is Arc))
            {
                curveTypesValid = false;
                typeFailure = "curve set contains unsupported native geometry";
                break;
            }
            double length = curve.GetLength();
            if (!IsFinite(length) || length <= SketchOffsetPositionToleranceMm)
            {
                curveTypesValid = false;
                typeFailure = "curve set contains a degenerate line or arc";
                break;
            }
            Arc arc = curve as Arc;
            if (arc != null && (!IsFinite(arc.Radius) || arc.Radius <= SketchOffsetPositionToleranceMm ||
                ArcSweep(arc) <= SketchOffsetAngleTolerance || ArcSweep(arc) > 2.0 * Math.PI + SketchOffsetAngleTolerance))
            {
                curveTypesValid = false;
                typeFailure = "curve set contains a zero-radius or invalid-sweep Arc";
                break;
            }
            int startNode = OffsetEndpointNode(CurveEndpoint(curve, "start"), nodes, degrees, parents);
            int endNode = OffsetEndpointNode(CurveEndpoint(curve, "end"), nodes, degrees, parents);
            degrees[startNode]++;
            degrees[endNode]++;
            OffsetUnion(parents, startNode, endNode);
            curveNodes.Add(new int[] { startNode, endNode });
        }
        if (!curveTypesValid)
        {
            result["valid_chain"] = false;
            result["closed"] = false;
            result["open_end_count"] = 0;
            result["component_count"] = 0;
            result["self_intersection_count"] = 0;
            result["reason"] = typeFailure;
            return result;
        }

        int openEnds = 0;
        bool degreeValid = true;
        for (int i = 0; i < degrees.Count; i++)
        {
            if (degrees[i] == 1) openEnds++;
            else if (degrees[i] != 2) degreeValid = false;
        }
        HashSet<int> components = new HashSet<int>();
        for (int i = 0; i < parents.Count; i++) components.Add(OffsetFind(parents, i));
        int componentCount = components.Count;

        List<OffsetCurve2d> projected = new List<OffsetCurve2d>();
        bool projectionValid = true;
        for (int i = 0; i < curves.Length; i++)
        {
            OffsetCurve2d curve2d;
            if (!TryProjectOffsetCurve(curves[i], sketchOrigin, sketchXAxis, sketchYAxis,
                sketchNormal, out curve2d))
            {
                projectionValid = false;
                break;
            }
            projected.Add(curve2d);
        }
        int selfIntersections = 0;
        if (projectionValid)
        {
            for (int i = 0; i < projected.Count; i++)
            {
                for (int j = i + 1; j < projected.Count; j++)
                {
                    List<OffsetPoint2d> intersections;
                    bool overlap;
                    OffsetCurveIntersections(projected[i], projected[j], out intersections, out overlap);
                    if (overlap)
                    {
                        selfIntersections++;
                        continue;
                    }
                    foreach (OffsetPoint2d point in intersections)
                        if (!IsSharedOffsetEndpoint(projected[i], projected[j], point))
                            selfIntersections++;
                }
            }
        }
        bool closed = degreeValid && openEnds == 0 && componentCount == 1;
        bool singleOpenChain = degreeValid && openEnds == 2 && componentCount == 1;
        bool validChain = projectionValid && degreeValid && componentCount == 1 &&
            (closed || singleOpenChain) && selfIntersections == 0;
        string reason = !projectionValid ? "curve is not coplanar with the Sketch frame" :
            !degreeValid ? "curve endpoints form a branch or gap" :
            componentCount != 1 ? "curves form more than one connected component" :
            !(closed || singleOpenChain) ? "endpoint graph is neither one open chain nor one closed loop" :
            selfIntersections != 0 ? "curve set contains a non-endpoint self-intersection or overlap" : null;
        result["valid_chain"] = validChain;
        result["closed"] = closed;
        result["open_end_count"] = openEnds;
        result["component_count"] = componentCount;
        result["self_intersection_count"] = selfIntersections;
        result["endpoint_count"] = nodes.Count;
        if (reason != null) result["reason"] = reason;
        return result;
    }

    private static int OffsetEndpointNode(Point3d point, List<Point3d> nodes, List<int> degrees, List<int> parents)
    {
        for (int i = 0; i < nodes.Count; i++)
            if (PointDistance(nodes[i], point) <= SketchOffsetPositionToleranceMm) return i;
        int index = nodes.Count;
        nodes.Add(point);
        degrees.Add(0);
        parents.Add(index);
        return index;
    }

    private static int OffsetFind(List<int> parents, int value)
    {
        if (parents[value] != value) parents[value] = OffsetFind(parents, parents[value]);
        return parents[value];
    }

    private static void OffsetUnion(List<int> parents, int first, int second)
    {
        int a = OffsetFind(parents, first);
        int b = OffsetFind(parents, second);
        if (a != b) parents[b] = a;
    }

    private static bool TryProjectOffsetCurve(Curve curve, Point3d origin, Vector3d xAxis,
        Vector3d yAxis, Vector3d normal, out OffsetCurve2d projected)
    {
        projected = null;
        if (curve == null) return false;
        Point3d startPoint = CurveEndpoint(curve, "start");
        Point3d endPoint = CurveEndpoint(curve, "end");
        if (Math.Abs(Dot(Subtract(startPoint, origin), normal)) > SketchOffsetPositionToleranceMm ||
            Math.Abs(Dot(Subtract(endPoint, origin), normal)) > SketchOffsetPositionToleranceMm)
            return false;
        OffsetCurve2d value = new OffsetCurve2d();
        value.Native = curve;
        value.Start = ProjectOffsetPoint(startPoint, origin, xAxis, yAxis);
        value.End = ProjectOffsetPoint(endPoint, origin, xAxis, yAxis);
        Line line = curve as Line;
        if (line != null)
        {
            value.IsLine = true;
            projected = value;
            return true;
        }
        Arc arc = curve as Arc;
        if (arc == null) return false;
        Point3d frameCenter;
        Vector3d frameX, frameY;
        arc.GetOrientation(out frameCenter, out frameX, out frameY);
        if (Math.Abs(Dot(Subtract(arc.CenterPoint, origin), normal)) > SketchOffsetPositionToleranceMm)
            return false;
        Vector3d arcNormal = CrossCornerVectors(frameX, frameY);
        double normalLength = VectorLength(arcNormal);
        if (!IsFinite(normalLength) || normalLength <= SketchOffsetAngleTolerance) return false;
        arcNormal = Scale(arcNormal, 1.0 / normalLength);
        if (Math.Abs(Dot(normal, arcNormal)) < 1.0 - SketchOffsetAngleTolerance ||
            VectorLength(CrossCornerVectors(normal, arcNormal)) > SketchOffsetAngleTolerance) return false;
        value.IsLine = false;
        value.Center = ProjectOffsetPoint(arc.CenterPoint, origin, xAxis, yAxis);
        value.Radius = arc.Radius;
        value.StartAngle = arc.StartAngle;
        value.Sweep = ArcSweep(arc);
        value.AxisXX = Dot(frameX, xAxis);
        value.AxisXY = Dot(frameX, yAxis);
        value.AxisYX = Dot(frameY, xAxis);
        value.AxisYY = Dot(frameY, yAxis);
        projected = value;
        return IsFinite(value.Radius) && value.Radius > SketchOffsetPositionToleranceMm;
    }

    private static OffsetPoint2d ProjectOffsetPoint(Point3d point, Point3d origin, Vector3d xAxis, Vector3d yAxis)
    {
        Vector3d delta = Subtract(point, origin);
        return new OffsetPoint2d(Dot(delta, xAxis), Dot(delta, yAxis));
    }

    private static void OffsetCurveIntersections(OffsetCurve2d first, OffsetCurve2d second,
        out List<OffsetPoint2d> intersections, out bool overlap)
    {
        intersections = new List<OffsetPoint2d>();
        overlap = false;
        if (first.IsLine && second.IsLine)
        {
            OffsetLineLineIntersections(first, second, intersections, out overlap);
            return;
        }
        if (first.IsLine && !second.IsLine)
        {
            OffsetLineArcIntersections(first, second, intersections);
            return;
        }
        if (!first.IsLine && second.IsLine)
        {
            OffsetLineArcIntersections(second, first, intersections);
            return;
        }
        OffsetArcArcIntersections(first, second, intersections, out overlap);
    }

    private static void OffsetLineLineIntersections(OffsetCurve2d a, OffsetCurve2d b,
        List<OffsetPoint2d> output, out bool overlap)
    {
        overlap = false;
        double rx = a.End.X - a.Start.X, ry = a.End.Y - a.Start.Y;
        double sx = b.End.X - b.Start.X, sy = b.End.Y - b.Start.Y;
        double denominator = Cross2(rx, ry, sx, sy);
        double qpx = b.Start.X - a.Start.X, qpy = b.Start.Y - a.Start.Y;
        double scale = Math.Max(1.0, Math.Sqrt(rx * rx + ry * ry) + Math.Sqrt(sx * sx + sy * sy));
        if (Math.Abs(denominator) <= SketchOffsetPositionToleranceMm * scale)
        {
            if (Math.Abs(Cross2(qpx, qpy, rx, ry)) > SketchOffsetPositionToleranceMm * scale) return;
            double rLength2 = rx * rx + ry * ry;
            if (rLength2 <= SketchOffsetPositionToleranceMm * SketchOffsetPositionToleranceMm) return;
            double t0 = (qpx * rx + qpy * ry) / rLength2;
            double t1 = ((b.End.X - a.Start.X) * rx + (b.End.Y - a.Start.Y) * ry) / rLength2;
            double low = Math.Max(0.0, Math.Min(t0, t1));
            double high = Math.Min(1.0, Math.Max(t0, t1));
            if (high - low > SketchOffsetPositionToleranceMm / Math.Sqrt(rLength2))
                overlap = true;
            else if (high >= low - SketchOffsetPositionToleranceMm / Math.Sqrt(rLength2))
                AddOffsetIntersection(output, new OffsetPoint2d(a.Start.X + (low + high) * 0.5 * rx,
                    a.Start.Y + (low + high) * 0.5 * ry));
            return;
        }
        double t = Cross2(qpx, qpy, sx, sy) / denominator;
        double u = Cross2(qpx, qpy, rx, ry) / denominator;
        double tol = SketchOffsetPositionToleranceMm / scale;
        if (t < -tol || t > 1.0 + tol || u < -tol || u > 1.0 + tol) return;
        AddOffsetIntersection(output, new OffsetPoint2d(a.Start.X + t * rx, a.Start.Y + t * ry));
    }

    private static void OffsetLineArcIntersections(OffsetCurve2d line, OffsetCurve2d arc,
        List<OffsetPoint2d> output)
    {
        double dx = line.End.X - line.Start.X, dy = line.End.Y - line.Start.Y;
        double fx = line.Start.X - arc.Center.X, fy = line.Start.Y - arc.Center.Y;
        double aa = dx * dx + dy * dy;
        if (aa <= SketchOffsetPositionToleranceMm * SketchOffsetPositionToleranceMm) return;
        double bb = 2.0 * (fx * dx + fy * dy);
        double cc = fx * fx + fy * fy - arc.Radius * arc.Radius;
        double discriminant = bb * bb - 4.0 * aa * cc;
        if (discriminant < -SketchOffsetPositionToleranceMm) return;
        if (discriminant < 0.0) discriminant = 0.0;
        double root = Math.Sqrt(discriminant);
        double t1 = (-bb - root) / (2.0 * aa);
        double t2 = (-bb + root) / (2.0 * aa);
        AddLineArcCandidate(line, arc, t1, output);
        if (Math.Abs(t2 - t1) > SketchOffsetAngleTolerance) AddLineArcCandidate(line, arc, t2, output);
    }

    private static void AddLineArcCandidate(OffsetCurve2d line, OffsetCurve2d arc, double t,
        List<OffsetPoint2d> output)
    {
        double tol = SketchOffsetPositionToleranceMm / Math.Max(1.0, Math.Sqrt(
            (line.End.X - line.Start.X) * (line.End.X - line.Start.X) +
            (line.End.Y - line.Start.Y) * (line.End.Y - line.Start.Y)));
        if (t < -tol || t > 1.0 + tol) return;
        OffsetPoint2d point = new OffsetPoint2d(
            line.Start.X + t * (line.End.X - line.Start.X),
            line.Start.Y + t * (line.End.Y - line.Start.Y));
        if (OffsetArcContainsPoint(arc, point)) AddOffsetIntersection(output, point);
    }

    private static void OffsetArcArcIntersections(OffsetCurve2d a, OffsetCurve2d b,
        List<OffsetPoint2d> output, out bool overlap)
    {
        overlap = false;
        double dx = b.Center.X - a.Center.X, dy = b.Center.Y - a.Center.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= SketchOffsetPositionToleranceMm && Math.Abs(a.Radius - b.Radius) <= SketchOffsetPositionToleranceMm)
        {
            OffsetPoint2d middleA = OffsetArcPoint(a, a.StartAngle + a.Sweep * 0.5);
            OffsetPoint2d middleB = OffsetArcPoint(b, b.StartAngle + b.Sweep * 0.5);
            if (OffsetArcContainsPoint(b, middleA) && !OffsetPointIsEndpoint(a, middleA) && !OffsetPointIsEndpoint(b, middleA))
                overlap = true;
            if (OffsetArcContainsPoint(a, middleB) && !OffsetPointIsEndpoint(a, middleB) && !OffsetPointIsEndpoint(b, middleB))
                overlap = true;
            if (a.Sweep >= 2.0 * Math.PI - SketchOffsetAngleTolerance &&
                b.Sweep >= 2.0 * Math.PI - SketchOffsetAngleTolerance)
                overlap = true;
            OffsetPoint2d[] candidates = new OffsetPoint2d[] { a.Start, a.End, b.Start, b.End };
            foreach (OffsetPoint2d candidate in candidates)
                if (OffsetArcContainsPoint(a, candidate) && OffsetArcContainsPoint(b, candidate))
                    AddOffsetIntersection(output, candidate);
            return;
        }
        if (distance <= SketchOffsetPositionToleranceMm ||
            distance > a.Radius + b.Radius + SketchOffsetPositionToleranceMm ||
            distance < Math.Abs(a.Radius - b.Radius) - SketchOffsetPositionToleranceMm) return;
        double along = (a.Radius * a.Radius - b.Radius * b.Radius + distance * distance) / (2.0 * distance);
        double heightSquared = a.Radius * a.Radius - along * along;
        if (heightSquared < -SketchOffsetPositionToleranceMm) return;
        if (heightSquared < 0.0) heightSquared = 0.0;
        double height = Math.Sqrt(heightSquared);
        double ux = dx / distance, uy = dy / distance;
        double baseX = a.Center.X + along * ux, baseY = a.Center.Y + along * uy;
        OffsetPoint2d p1 = new OffsetPoint2d(baseX - height * uy, baseY + height * ux);
        OffsetPoint2d p2 = new OffsetPoint2d(baseX + height * uy, baseY - height * ux);
        if (OffsetArcContainsPoint(a, p1) && OffsetArcContainsPoint(b, p1)) AddOffsetIntersection(output, p1);
        if (height > SketchOffsetPositionToleranceMm &&
            OffsetArcContainsPoint(a, p2) && OffsetArcContainsPoint(b, p2)) AddOffsetIntersection(output, p2);
    }

    private static bool OffsetArcContainsPoint(OffsetCurve2d arc, OffsetPoint2d point)
    {
        double dx = point.X - arc.Center.X, dy = point.Y - arc.Center.Y;
        double radius = Math.Sqrt(dx * dx + dy * dy);
        if (Math.Abs(radius - arc.Radius) > SketchOffsetPositionToleranceMm) return false;
        double cos = (dx * arc.AxisXX + dy * arc.AxisXY) / arc.Radius;
        double sin = (dx * arc.AxisYX + dy * arc.AxisYY) / arc.Radius;
        double angle = Math.Atan2(sin, cos);
        double offset = PositiveAngleOffset(arc.StartAngle, angle);
        return arc.Sweep >= 2.0 * Math.PI - SketchOffsetAngleTolerance ||
            offset <= arc.Sweep + SketchOffsetAngleTolerance;
    }

    private static OffsetPoint2d OffsetArcPoint(OffsetCurve2d arc, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new OffsetPoint2d(arc.Center.X + arc.Radius * (arc.AxisXX * c + arc.AxisYX * s),
            arc.Center.Y + arc.Radius * (arc.AxisXY * c + arc.AxisYY * s));
    }

    private static bool IsSharedOffsetEndpoint(OffsetCurve2d a, OffsetCurve2d b, OffsetPoint2d point)
    {
        return OffsetPointIsEndpoint(a, point) && OffsetPointIsEndpoint(b, point);
    }

    private static bool OffsetPointIsEndpoint(OffsetCurve2d curve, OffsetPoint2d point)
    {
        return OffsetPointDistance(curve.Start, point) <= SketchOffsetPositionToleranceMm ||
            OffsetPointDistance(curve.End, point) <= SketchOffsetPositionToleranceMm;
    }

    private static void AddOffsetIntersection(List<OffsetPoint2d> points, OffsetPoint2d point)
    {
        foreach (OffsetPoint2d current in points)
            if (OffsetPointDistance(current, point) <= SketchOffsetPositionToleranceMm) return;
        points.Add(point);
    }

    private static double OffsetPointDistance(OffsetPoint2d a, OffsetPoint2d b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Cross2(double ax, double ay, double bx, double by)
    {
        return ax * by - ay * bx;
    }

    private static double PositiveAngleOffset(double start, double angle)
    {
        double full = 2.0 * Math.PI;
        double value = (angle - start) % full;
        if (value < 0.0) value += full;
        return value;
    }

    private static double SignedOffsetAngle(double angle)
    {
        double value = (angle + Math.PI) % (2.0 * Math.PI);
        if (value < 0.0) value += 2.0 * Math.PI;
        return value - Math.PI;
    }

    private static double VectorLength(Vector3d vector)
    {
        return Math.Sqrt(Dot(vector, vector));
    }

    private static double ArcSweep(Arc arc)
    {
        if (arc == null) return Double.NaN;
        double sweep = arc.EndAngle - arc.StartAngle;
        double full = 2.0 * Math.PI;
        if (Math.Abs(sweep) <= SketchOffsetAngleTolerance &&
            PointDistance(CurveEndpoint(arc, "start"), CurveEndpoint(arc, "end")) <= SketchOffsetPositionToleranceMm)
            return full;
        while (sweep < 0.0) sweep += full;
        return sweep;
    }

    private static double ArcSampleAngle(Arc arc)
    {
        double sweep = ArcSweep(arc);
        return arc.StartAngle + sweep * 0.5;
    }

    private static Vector3d OffsetCurveTangentAtEndpoint(Curve curve, bool nativeStart)
    {
        Line line = curve as Line;
        if (line != null)
            return new Vector3d(line.EndPoint.X - line.StartPoint.X,
                line.EndPoint.Y - line.StartPoint.Y, line.EndPoint.Z - line.StartPoint.Z);
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center;
            Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            double angle = nativeStart ? arc.StartAngle : arc.EndAngle;
            return new Vector3d(
                -xAxis.X * Math.Sin(angle) + yAxis.X * Math.Cos(angle),
                -xAxis.Y * Math.Sin(angle) + yAxis.Y * Math.Cos(angle),
                -xAxis.Z * Math.Sin(angle) + yAxis.Z * Math.Cos(angle));
        }
        throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "F08 endpoint tangent supports Line and circular Arc only");
    }

    private static double OffsetArcNaturalTangentAlignment(Arc source, Arc output,
        double sourceAngle, Vector3d sourceRadial)
    {
        Point3d sourceCenter, outputCenter;
        Vector3d sourceX, sourceY, outputX, outputY;
        source.GetOrientation(out sourceCenter, out sourceX, out sourceY);
        output.GetOrientation(out outputCenter, out outputX, out outputY);
        Vector3d sourceTangent = new Vector3d(
            -sourceX.X * Math.Sin(sourceAngle) + sourceY.X * Math.Cos(sourceAngle),
            -sourceX.Y * Math.Sin(sourceAngle) + sourceY.Y * Math.Cos(sourceAngle),
            -sourceX.Z * Math.Sin(sourceAngle) + sourceY.Z * Math.Cos(sourceAngle));
        double angleOnOutput = Math.Atan2(Dot(sourceRadial, outputY), Dot(sourceRadial, outputX));
        Vector3d outputTangent = new Vector3d(
            -outputX.X * Math.Sin(angleOnOutput) + outputY.X * Math.Cos(angleOnOutput),
            -outputX.Y * Math.Sin(angleOnOutput) + outputY.Y * Math.Cos(angleOnOutput),
            -outputX.Z * Math.Sin(angleOnOutput) + outputY.Z * Math.Cos(angleOnOutput));
        NormalizeVector(ref sourceTangent, "source Arc tangent");
        NormalizeVector(ref outputTangent, "output Arc tangent");
        return Dot(sourceTangent, outputTangent);
    }

    private static void ValidateOffsetCurvePlane(Curve curve, Sketch sketch)
    {
        if (curve == null || sketch == null) throw new ArgumentNullException(curve == null ? "curve" : "sketch");
        Point3d origin = sketch.Origin;
        Matrix3x3 axes = sketch.Orientation.Element;
        Vector3d normal = new Vector3d(axes.Zx, axes.Zy, axes.Zz);
        NormalizeVector(ref normal, "sketch normal");
        Line line = curve as Line;
        if (line != null)
        {
            if (Math.Abs(Dot(Subtract(line.StartPoint, origin), normal)) > SketchOffsetPositionToleranceMm ||
                Math.Abs(Dot(Subtract(line.EndPoint, origin), normal)) > SketchOffsetPositionToleranceMm)
                throw new M2ModelException("SKETCH_TARGET_MISMATCH", "Line endpoints do not lie in the target Sketch plane");
            return;
        }
        Arc arc = curve as Arc;
        if (arc == null) throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "F08 supports Line and circular Arc only");
        Point3d center;
        Vector3d xAxis, yAxis;
        arc.GetOrientation(out center, out xAxis, out yAxis);
        Vector3d arcNormal = CrossCornerVectors(xAxis, yAxis);
        NormalizeVector(ref arcNormal, "Arc plane normal");
        if (Math.Abs(Dot(Subtract(arc.CenterPoint, origin), normal)) > SketchOffsetPositionToleranceMm ||
            Math.Abs(Dot(normal, arcNormal)) < 1.0 - SketchOffsetAngleTolerance ||
            VectorLength(CrossCornerVectors(normal, arcNormal)) > SketchOffsetAngleTolerance)
            throw new M2ModelException("SKETCH_TARGET_MISMATCH", "Arc plane does not coincide with the target Sketch plane");
    }
}
