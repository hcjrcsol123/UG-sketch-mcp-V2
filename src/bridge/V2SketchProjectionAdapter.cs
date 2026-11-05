using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

/// <summary>Optional adapter for projecting native source geometry into an explicit Sketch.</summary>
public sealed partial class M2OpenModelAdapter : IM3SketchProjectionOperationAdapter
{
    private const double M3SketchProjectionPositionToleranceMm = 1.0e-6;
    private const double M3SketchProjectionAngleTolerance = 1.0e-7;

    private static readonly string[] M3SketchProjectionCheckNames = new string[]
    {
        "projection_geometry", "source_curves", "sketch_constraints", "sketch_frames",
        "reference_status", "non_target_geometry", "history", "bodies", "sketch_solver_status"
    };

    private sealed class M3SketchProjectionSourceState
    {
        internal NXObject Source;
        internal string Id;
        internal string Tag;
        internal string ObjectType;
        internal string GeometryType;
        internal string BodyId;
        internal string SourceSketchId;
        internal Point3d Start;
        internal Point3d End;
        internal Point3d Center;
        internal Vector3d AxisX;
        internal Vector3d AxisY;
        internal Vector3d PlaneNormal;
        internal double Radius;
        internal double StartAngle;
        internal double EndAngle;
        internal double DirectedSweep;
        internal double Length;
        internal bool IsPeriodic;
        internal Point3d ExpectedStart;
        internal Point3d ExpectedEnd;
        internal Point3d ExpectedCenter;
        internal double ExpectedSweep;
        internal Point3d TargetOrigin;
        internal Vector3d TargetNormal;
        internal Vector3d TargetXAxis;
        internal Vector3d TargetYAxis;
        internal List<string> SourceFeatureParentIds;
    }

    private sealed class M3SketchProjectionFeatureState
    {
        internal Feature Feature;
        internal string Id;
        internal string Tag;
        internal string Type;
        internal List<string> ParentIds;
        internal List<string> ChildIds;
        internal List<string> ParentTags;
        internal List<string> ChildTags;
        internal List<string> BodyTags;
        internal List<string> ExpressionNames;
        internal bool Suppressed;
        internal string Signature;
    }

    private sealed class M3SketchProjectionCurveState
    {
        internal Curve Curve;
        internal string Id;
        internal string Tag;
        internal string Type;
        internal string Signature;
    }

    private sealed class M3SketchProjectionPlanState
    {
        internal M2ModelBinding Binding;
        internal BindingState BindingState;
        internal string RevisionId;
        internal M3SketchProjectionRequest Request;
        internal Sketch Sketch;
        internal string SketchId;
        internal string SketchTag;
        internal string SketchFeatureId;
        internal string SketchFeatureTag;
        internal string ActiveSketchTagBefore;
        internal Point3d SketchOrigin;
        internal Vector3d SketchNormal;
        internal Vector3d SketchXAxis;
        internal Vector3d SketchYAxis;
        internal Vector3d Direction;
        internal string SketchFrameBefore;
        internal string SemanticBefore;
        internal string HistoryBefore;
        internal List<object> HistoryProjectionBefore;
        internal string FullGeometryBefore;
        internal string FeatureInventoryBefore;
        internal string SourceSnapshotBefore;
        internal string ExpressionsBefore;
        internal string ExpressionsAfter;
        internal bool DirtyBefore;
        internal string SolverStatusBefore;
        internal int DegreesOfFreedomBefore;
        internal string[] SketchGeometryTagsBefore;
        internal string TargetOriginPointTag;
        internal Point3d TargetOriginPointCoordinates;
        internal bool TargetOriginPointAvailable;
        internal M3SketchProjectionSourceState[] Sources;
        internal Dictionary<string, CornerGeometrySnapshot> GeometryBefore;
        internal Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> ConstraintsBefore;
        internal Dictionary<string, string> FramesBefore;
        internal Dictionary<string, int> ReferenceStatesBefore;
        internal List<M3BodyMeasure> BodiesBefore;
        internal List<string> FeatureTagsBefore;
        internal Dictionary<string, M3SketchProjectionFeatureState> FeatureStatesBefore;
        internal Dictionary<string, M3SketchProjectionCurveState> CurveInventoryBefore;
        internal Dictionary<string, List<string>> PartHelperTagsBefore;
        internal Dictionary<string, object> SourceRecordsBefore;
        internal Dictionary<string, object> Preview;
        internal bool DirtyAfter;
        internal string SemanticAfter;
        internal string HistoryAfter;
        internal List<object> HistoryProjectionAfter;
        internal string FullGeometryAfter;
        internal string FeatureInventoryAfter;
        internal string SourceSnapshotAfter;
        internal Dictionary<string, CornerGeometrySnapshot> GeometryAfter;
        internal Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> ConstraintsAfter;
        internal Dictionary<string, string> FramesAfter;
        internal Dictionary<string, int> ReferenceStatesAfter;
        internal List<M3BodyMeasure> BodiesAfter;
        internal List<string> FeatureTagsAfter;
        internal Dictionary<string, M3SketchProjectionFeatureState> FeatureStatesAfter;
        internal Dictionary<string, M3SketchProjectionCurveState> CurveInventoryAfter;
        internal Dictionary<string, List<string>> PartHelperTagsAfter;
        internal string[] OutputCurveTags;
        internal string[] OutputCurveIds;
        internal string[] OutputCurveTypes;
        internal Dictionary<string, object> NativeOperationEvidence;
        internal Dictionary<string, object> AppliedEvidence;
        internal M3ExecutionResult Execution;
    }

    public M3PlanSnapshot PrepareSketchProjection(M2ModelBinding binding, M3SketchProjectionRequest request)
    {
        ValidateM3SketchProjectionRequest(request);
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "a Sketch is active in NX; projection preparation is paused");

        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            throw new M2ModelException("STALE_BINDING", bindingReason ?? "NX model binding is no longer current");
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current part revision");

        SnapshotResult captured = Capture(binding, state);
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id changed while resolving projection sources");

        Sketch sketch = FindSketch(state.WorkPart, request.SketchId);
        if (sketch == null || !String.Equals(RequiredJournalId(sketch, "Sketch"), request.SketchId, StringComparison.Ordinal))
            throw new M2ModelException("SKETCH_NOT_FOUND", "sketch_id does not identify one native Sketch in the bound Work Part");
        if (!M3SketchProjectionNativeSketchFeature(state.WorkPart, sketch))
            throw new M2ModelException("UNSUPPORTED_SKETCH_HISTORY", "projection target must be a native Sketch feature in the bound Work Part");

        Point3d sketchOrigin;
        Vector3d sketchNormal, sketchXAxis, sketchYAxis;
        Dictionary<string, object> frameRecord = M3SketchProjectionFrame(sketch,
            out sketchOrigin, out sketchNormal, out sketchXAxis, out sketchYAxis);
        Vector3d direction = M3SketchProjectionDirection(request.DirectionXYZ, sketchNormal);
        M3SketchProjectionSourceState[] sources = CaptureM3SketchProjectionSources(state.WorkPart,
            sketch, request.SourceCurveIds, sketchOrigin, sketchNormal, sketchXAxis, sketchYAxis, direction);

        string targetOriginPointTag = null;
        Point3d targetOriginPointCoordinates = sketchOrigin;
        bool targetOriginPointAvailable = false;
        try
        {
            Point targetOriginPoint = sketch.OriginPoint;
            if (targetOriginPoint != null)
            {
                targetOriginPointTag = targetOriginPoint.Tag.ToString();
                targetOriginPointCoordinates = targetOriginPoint.Coordinates;
                targetOriginPointAvailable = !String.IsNullOrEmpty(targetOriginPointTag) &&
                    !targetOriginPoint.IsOccurrence && targetOriginPoint.OwningPart != null &&
                    targetOriginPoint.OwningPart.Tag == state.WorkPart.Tag && !targetOriginPoint.IsReference &&
                    M3SketchProjectionFinite(targetOriginPointCoordinates) &&
                    M3SketchProjectionDistance(targetOriginPointCoordinates, sketchOrigin) <= M3SketchProjectionPositionToleranceMm;
                if (!targetOriginPointAvailable)
                    throw new M2ModelException("MODEL_READ_FAILED", "Sketch.OriginPoint is not a same-part, non-reference point at the target Sketch origin");
            }
        }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "could not read the native target Sketch origin Point identity (" + ex.GetType().Name + ")");
        }

        bool dirtyBefore;
        try { dirtyBefore = state.WorkPart.IsModified; }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "could not capture Work Part dirty state for sketch projection (" + ex.GetType().Name + ")");
        }
        Dictionary<string, CornerGeometrySnapshot> geometryBefore = CaptureSketchCornerGeometry(state.WorkPart);
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraintsBefore =
            CaptureAllSketchOffsetConstraints(state.WorkPart);
        Dictionary<string, string> framesBefore = CaptureAllSketchOffsetFrames(state.WorkPart);
        Dictionary<string, int> referenceStatesBefore = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
        List<M3BodyMeasure> bodiesBefore = CaptureM3BodyMeasures(state.WorkPart);
        List<string> featureTagsBefore = CaptureFeatureTags(state.WorkPart);
        Dictionary<string, M3SketchProjectionFeatureState> featureStatesBefore =
            CaptureM3SketchProjectionFeatureStates(state.WorkPart);
        string semanticBefore = CanonicalSemanticSnapshot(captured.Snapshot);
        List<object> historyProjectionBefore = DeepCloneValue(
            M3SketchProjectionStableHistoryProjection(state.WorkPart, featureStatesBefore)) as List<object>;
        if (historyProjectionBefore == null)
            throw new M2ModelException("MODEL_READ_FAILED", "projection history snapshot could not be retained");
        string historyBefore = M2Json.CanonicalText(historyProjectionBefore);
        string fullGeometryBefore = CaptureFullGeometry(state.WorkPart);
        string featureInventoryBefore = M3SketchProjectionFeatureInventoryText(featureStatesBefore);
        string sourceSnapshotBefore = M3SketchProjectionSourceSnapshot(sources);
        string expressionsBefore = M2Json.CanonicalText(SnapshotList(captured.Snapshot, "expressions"));
        string[] geometryTagsBefore = M3SketchProjectionGeometryTags(sketch);
        Dictionary<string, M3SketchProjectionCurveState> curveInventoryBefore =
            CaptureM3SketchProjectionCurveInventory(state.WorkPart);
        Dictionary<string, List<string>> partHelperTagsBefore = CapturePatternPartHelperTags(state.WorkPart);
        Sketch.Status solverStatus;
        int degreesOfFreedom;
        try { solverStatus = sketch.GetStatus(out degreesOfFreedom); }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "could not read projection target Sketch solver state (" + ex.GetType().Name + ")");
        }

        M3SketchProjectionPlanState planState = new M3SketchProjectionPlanState
        {
            Binding = binding,
            BindingState = state,
            RevisionId = binding.RevisionId,
            Request = CloneM3SketchProjectionRequest(request),
            Sketch = sketch,
            SketchId = request.SketchId,
            SketchTag = sketch.Tag.ToString(),
            SketchFeatureId = RequiredJournalId(sketch.Feature, "projection target Sketch feature"),
            SketchFeatureTag = sketch.Feature.Tag.ToString(),
            ActiveSketchTagBefore = null,
            SketchOrigin = sketchOrigin,
            SketchNormal = sketchNormal,
            SketchXAxis = sketchXAxis,
            SketchYAxis = sketchYAxis,
            Direction = direction,
            SketchFrameBefore = M2Json.CanonicalText(frameRecord),
            SemanticBefore = semanticBefore,
            HistoryBefore = historyBefore,
            HistoryProjectionBefore = historyProjectionBefore,
            FullGeometryBefore = fullGeometryBefore,
            FeatureInventoryBefore = featureInventoryBefore,
            SourceSnapshotBefore = sourceSnapshotBefore,
            ExpressionsBefore = expressionsBefore,
            DirtyBefore = dirtyBefore,
            SolverStatusBefore = solverStatus.ToString(),
            DegreesOfFreedomBefore = degreesOfFreedom,
            SketchGeometryTagsBefore = geometryTagsBefore,
            TargetOriginPointTag = targetOriginPointTag,
            TargetOriginPointCoordinates = targetOriginPointCoordinates,
            TargetOriginPointAvailable = targetOriginPointAvailable,
            Sources = sources,
            GeometryBefore = geometryBefore,
            ConstraintsBefore = constraintsBefore,
            FramesBefore = framesBefore,
            ReferenceStatesBefore = referenceStatesBefore,
            BodiesBefore = bodiesBefore,
            FeatureTagsBefore = featureTagsBefore,
            FeatureStatesBefore = featureStatesBefore,
            CurveInventoryBefore = curveInventoryBefore,
            PartHelperTagsBefore = partHelperTagsBefore,
            SourceRecordsBefore = M3SketchProjectionSourceRecords(sources),
            NativeOperationEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
        };

        Dictionary<string, object> preview = new Dictionary<string, object>(StringComparer.Ordinal);
        preview.Add("preview_type", "orthogonal_projection_to_sketch_plane");
        preview.Add("direction_xyz_normalized", M3SketchProjectionVector(direction));
        preview.Add("direction_alignment", "positive_or_negative_target_sketch_normal");
        preview.Add("target_frame", frameRecord);
        preview.Add("target_origin_point", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "available_before_activation", targetOriginPointAvailable },
            { "tag", targetOriginPointTag },
            { "coordinates_mm", M3SketchProjectionPoint(targetOriginPointCoordinates) },
            { "belongs_to_target_sketch", targetOriginPointAvailable }
        });
        preview.Add("source_snapshot", planState.SourceRecordsBefore);
        preview.Add("expected_output_geometry", M3SketchProjectionExpectedOutputRecords(sources));
        preview.Add("native_output_count", sources.Length);
        preview.Add("numeric_preview_tolerance_mm", M3SketchProjectionPositionToleranceMm);
        preview.Add("numeric_preview_angle_tolerance_rad", M3SketchProjectionAngleTolerance);
        planState.Preview = preview;

        Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal);
        publicView.Add("kind", "edit_sketch_projection");
        publicView.Add("revision_id", binding.RevisionId);
        publicView.Add("sketch_id", request.SketchId);
        publicView.Add("sketch_tag", planState.SketchTag);
        publicView.Add("target_feature_id", planState.SketchFeatureId);
        publicView.Add("source_curve_ids", M3SketchProjectionStringObjects(request.SourceCurveIds));
        publicView.Add("source_count", sources.Length);
        publicView.Add("direction_xyz", M3SketchProjectionVector(direction));
        publicView.Add("associative", request.Associative);
        publicView.Add("preview", preview);
        publicView.Add("baseline_geometry_count", geometryBefore.Count);
        publicView.Add("baseline_constraint_count", M3SketchProjectionConstraintCount(constraintsBefore));
        publicView.Add("baseline_reference_curve_count", referenceStatesBefore.Count);
        publicView.Add("baseline_feature_count", featureStatesBefore.Count);

        return new M3PlanSnapshot
        {
            Kind = "edit_sketch_projection",
            RevisionId = binding.RevisionId,
            SketchId = request.SketchId,
            FeatureId = planState.SketchFeatureId,
            PublicView = publicView,
            AdapterState = planState
        };
    }

    private M3ExecutionResult ExecuteSketchProjectionPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M3SketchProjectionPlanState plan = prepared == null ? null : prepared.AdapterState as M3SketchProjectionPlanState;
        if (plan == null || prepared.Kind != "edit_sketch_projection")
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null,
                "INVALID_PLAN", "sketch projection plan does not contain retained NX adapter state");

        BindingState state;
        try { state = RequireState(binding); }
        catch (M2ModelException ex)
        {
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, ex.Code, ex.Message);
        }
        if (!Object.ReferenceEquals(state, plan.BindingState) || !Object.ReferenceEquals(binding, plan.Binding) ||
            !String.Equals(plan.RevisionId, prepared.RevisionId, StringComparison.Ordinal) ||
            !String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "sketch projection plan belongs to a different binding or revision");
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_BINDING", bindingReason);

        M3SketchProjectionSourceState[] sourcesNow;
        SnapshotResult current;
        try
        {
            RequireWritableSimplePart(state.WorkPart);
            RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
            if (state.Session.ActiveSketch != null)
                return M3Execution("UNCHANGED", binding.RevisionId, null, "NX_BUSY", "a Sketch is active in NX before sketch projection execution");
            current = Capture(binding, state);
            if (!String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(CanonicalSemanticSnapshot(current.Snapshot), plan.SemanticBefore, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "semantic model state changed after projection preparation");
            if (!String.Equals(CaptureFullGeometry(state.WorkPart), plan.FullGeometryBefore, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_GEOMETRY", "Sketch geometry changed after projection preparation");
            if (!CornerGeometryMapsEqual(plan.GeometryBefore, CaptureSketchCornerGeometry(state.WorkPart)))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_GEOMETRY", "native Sketch geometry changed after projection preparation");
            if (!OffsetConstraintMapsEqual(plan.ConstraintsBefore, CaptureAllSketchOffsetConstraints(state.WorkPart)))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "Sketch constraints changed after projection preparation");
            if (!OffsetStringMapsEqual(plan.FramesBefore, CaptureAllSketchOffsetFrames(state.WorkPart)))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "Sketch frames changed after projection preparation");
            if (!OffsetIntMapsEqual(plan.ReferenceStatesBefore, CaptureAllSketchOffsetReferenceStates(state.WorkPart)))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "Sketch reference status changed after projection preparation");
            Dictionary<string, object> bodyEvidence;
            if (!BodyMeasuresEqual(plan.BodiesBefore, CaptureM3BodyMeasures(state.WorkPart), out bodyEvidence))
                return M3Execution("UNCHANGED", binding.RevisionId, bodyEvidence, "STALE_GEOMETRY", "body measurements changed after projection preparation");
            Dictionary<string, M3SketchProjectionFeatureState> featuresNow = CaptureM3SketchProjectionFeatureStates(state.WorkPart);
            if (!String.Equals(plan.FeatureInventoryBefore, M3SketchProjectionFeatureInventoryText(featuresNow), StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "feature identities or dependency links changed after projection preparation");
            Sketch currentSketch = FindSketch(state.WorkPart, plan.SketchId);
            if (currentSketch == null || !String.Equals(currentSketch.Tag.ToString(), plan.SketchTag, StringComparison.Ordinal) ||
                currentSketch.Feature == null || !String.Equals(RequiredJournalId(currentSketch.Feature, "Sketch feature"), plan.SketchFeatureId, StringComparison.Ordinal) ||
                !String.Equals(currentSketch.Feature.Tag.ToString(), plan.SketchFeatureTag, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "projection target Sketch identity changed after preparation");
            Point3d currentSketchOrigin;
            Vector3d currentSketchNormal, currentSketchXAxis, currentSketchYAxis;
            Dictionary<string, object> currentSketchFrame = M3SketchProjectionFrame(currentSketch,
                out currentSketchOrigin, out currentSketchNormal, out currentSketchXAxis, out currentSketchYAxis);
            if (!String.Equals(M2Json.CanonicalText(currentSketchFrame), plan.SketchFrameBefore, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "projection target Sketch frame changed after preparation");
            sourcesNow = CaptureM3SketchProjectionSources(state.WorkPart, currentSketch,
                plan.Request.SourceCurveIds, plan.SketchOrigin, plan.SketchNormal, plan.SketchXAxis,
                plan.SketchYAxis, plan.Direction);
            if (!String.Equals(M3SketchProjectionSourceSnapshot(sourcesNow), plan.SourceSnapshotBefore, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_SOURCE_GEOMETRY", "projection source geometry or ownership changed after preparation");
            string stableHistoryNow = M2Json.CanonicalText(
                M3SketchProjectionStableHistoryProjection(state.WorkPart, featuresNow));
            if (!String.Equals(M3SketchProjectionFeatureInventoryText(featuresNow), plan.FeatureInventoryBefore, StringComparison.Ordinal) ||
                !String.Equals(stableHistoryNow, plan.HistoryBefore, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "feature history changed after projection preparation");
            if (!String.Equals(plan.CurveInventoryBefore == null ? null : M3SketchProjectionCurveInventoryText(plan.CurveInventoryBefore),
                M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart)), StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_SOURCE_GEOMETRY", "Work Part curve inventory changed after projection preparation");
            if (!PatternHelperTagInventoriesEqual(plan.PartHelperTagsBefore, CapturePatternPartHelperTags(state.WorkPart)))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_SOURCE_GEOMETRY", "Part Point, Axis, or Direction inventory changed after projection preparation");
            if (state.WorkPart.IsModified != plan.DirtyBefore)
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "Work Part dirty state changed after projection preparation");
        }
        catch (M2ModelException ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "MODEL_READ_FAILED",
                "could not revalidate sketch projection preconditions (" + ex.GetType().Name + ")");
        }

        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Visible, "M3 native sketch projection"); }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "UPDATE_FAILED",
                "could not create a real NX undo mark for sketch projection (" + ex.GetType().Name + ")");
        }

        plan.NativeOperationEvidence["undo_mark_created"] = true;
        plan.NativeOperationEvidence["undo_mark_visibility"] = "Visible";
        plan.NativeOperationEvidence["source_spatial_snapshot_before"] = plan.SourceRecordsBefore;
        plan.NativeOperationEvidence["associativity_requested"] = plan.Request.Associative;
        bool sketchActivated = false;
        bool updateCallAttempted = false;
        try
        {
            plan.Sketch.Activate(Sketch.ViewReorient.False);
            sketchActivated = true;
            V2SketchProjectionNativeResult native = V2SketchProjectionFactory.Execute(state.WorkPart,
                plan.Sketch, ProjectionSourceObjects(sourcesNow), plan.Request.Associative);
            if (native == null || native.OutputCurves == null || native.OutputCurves.Length == 0)
                throw new M2ModelException("PROJECTION_OUTPUT_INVALID", "NX sketch projection returned no native output curves");
            plan.NativeOperationEvidence["native_operation_called"] = true;
            plan.NativeOperationEvidence["factory_evidence"] = native.Evidence ?? new Dictionary<string, object>(StringComparer.Ordinal);
            plan.NativeOperationEvidence["factory_output_tags"] = M3SketchProjectionObjectTags(native.OutputCurves);
            plan.NativeOperationEvidence["factory_commit_object_type"] = native.CommitObject == null ? null : native.CommitObject.GetType().Name;
            plan.NativeOperationEvidence["factory_committed_object_types"] = M3SketchProjectionObjectTypes(native.CommittedObjects);

            // NX2506 requires sketch-local update work while the target is active.
            plan.Sketch.Update();
            plan.Sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            sketchActivated = false;
            if (state.Session.ActiveSketch != null)
                throw new M2ModelException("UPDATE_FAILED", "NX did not deactivate the projection target Sketch");
            updateCallAttempted = true;
            int updateErrors = state.Session.UpdateManager.DoUpdate(mark);
            plan.NativeOperationEvidence["update_error_count"] = updateErrors;
            if (updateErrors != 0)
                throw new M2ModelException("UPDATE_FAILED", "NX update reported errors after sketch projection");

            SnapshotResult afterCapture = Capture(binding, state);
            plan.GeometryAfter = CaptureSketchCornerGeometry(state.WorkPart);
            plan.ConstraintsAfter = CaptureAllSketchOffsetConstraints(state.WorkPart);
            plan.FramesAfter = CaptureAllSketchOffsetFrames(state.WorkPart);
            plan.ReferenceStatesAfter = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
            plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart);
            plan.FeatureTagsAfter = CaptureFeatureTags(state.WorkPart);
            plan.FeatureStatesAfter = CaptureM3SketchProjectionFeatureStates(state.WorkPart);
            plan.FeatureInventoryAfter = M3SketchProjectionFeatureInventoryText(plan.FeatureStatesAfter);
            plan.SemanticAfter = CanonicalSemanticSnapshot(afterCapture.Snapshot);
            plan.HistoryProjectionAfter = DeepCloneValue(
                M3SketchProjectionStableHistoryProjection(state.WorkPart, plan.FeatureStatesAfter)) as List<object>;
            plan.HistoryAfter = M2Json.CanonicalText(plan.HistoryProjectionAfter);
            plan.ExpressionsAfter = M2Json.CanonicalText(SnapshotList(afterCapture.Snapshot, "expressions"));
            plan.FullGeometryAfter = CaptureFullGeometry(state.WorkPart);
            plan.DirtyAfter = state.WorkPart.IsModified;
            plan.CurveInventoryAfter = CaptureM3SketchProjectionCurveInventory(state.WorkPart);
            plan.PartHelperTagsAfter = CapturePatternPartHelperTags(state.WorkPart);

            Dictionary<string, NXObject> targetGeometryAfter = M3SketchProjectionGeometryByTag(plan.Sketch);
            Dictionary<string, object> deltaEvidence;
            List<Curve> deltaCurves = M3SketchProjectionGeometryDeltaCurves(plan,
                targetGeometryAfter, native.OutputCurves, out plan.OutputCurveTags, out deltaEvidence);
            plan.OutputCurveIds = M3SketchProjectionCurveIds(deltaCurves);
            plan.OutputCurveTypes = M3SketchProjectionCurveTypes(deltaCurves);
            plan.NativeOperationEvidence["target_sketch_geometry_delta"] = deltaEvidence;
            plan.NativeOperationEvidence["output_curve_tags"] = M3SketchProjectionStringObjects(plan.OutputCurveTags);
            plan.NativeOperationEvidence["output_curve_ids"] = M3SketchProjectionStringObjects(plan.OutputCurveIds);
            plan.NativeOperationEvidence["output_curve_types"] = M3SketchProjectionStringObjects(plan.OutputCurveTypes);

            Dictionary<string, object> actualRelationEvidence = M3SketchProjectionRelationEvidence(
                state.WorkPart, plan, plan.FeatureStatesAfter, deltaCurves);
            plan.NativeOperationEvidence["native_relation"] = actualRelationEvidence;
            M3SketchProjectionSourceState[] sourceAfter = CaptureM3SketchProjectionSourcesByTags(state.WorkPart, plan.Sketch,
                M3SketchProjectionSourceTags(plan.Sources), plan.SketchOrigin, plan.SketchNormal, plan.SketchXAxis,
                plan.SketchYAxis, plan.Direction);
            plan.SourceSnapshotAfter = M3SketchProjectionSourceSnapshot(sourceAfter);
            plan.NativeOperationEvidence["source_spatial_snapshot_after"] = M3SketchProjectionSourceRecords(sourceAfter);

            plan.AppliedEvidence = EvaluateM3SketchProjectionState(binding, state, plan,
                afterCapture.Snapshot, true);
            if (!M3SketchProjectionEvidenceChecksPassed(plan.AppliedEvidence))
                throw new M2ModelException("PROJECTION_VERIFY_FAILED", "native projection geometry, relation, or preserved Sketch state did not match the prepared plan");

            Dictionary<string, object> result = M3SketchProjectionExecutionResult(plan,
                binding.RevisionId, "APPLIED", plan.AppliedEvidence);
            try
            {
                state.Session.DeleteUndoMark(mark, "");
                plan.NativeOperationEvidence["undo_mark_closed"] = true;
            }
            catch (Exception closeException)
            {
                plan.NativeOperationEvidence["undo_mark_closed"] = false;
                plan.NativeOperationEvidence["undo_mark_close_exception_type"] = closeException.GetType().FullName;
                plan.NativeOperationEvidence["undo_mark_close_exception_message"] = M3SketchProjectionSafeMessage(closeException);
                throw new M2ModelException("UNDO_MARK_CLOSE_FAILED", "projection applied but NX could not close its undo mark");
            }
            plan.Execution = M3Execution("APPLIED", binding.RevisionId, result, null, null);
            return plan.Execution;
        }
        catch (Exception ex)
        {
            if (sketchActivated)
            {
                try
                {
                    if (M3SketchProjectionIsSketchActive(state.Session, plan.Sketch))
                        plan.Sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
                    sketchActivated = false;
                }
                catch (Exception deactivateException)
                {
                    plan.NativeOperationEvidence["deactivation_exception_type"] = deactivateException.GetType().FullName;
                    plan.NativeOperationEvidence["deactivation_exception_message"] = M3SketchProjectionSafeMessage(deactivateException);
                }
            }
            plan.NativeOperationEvidence["native_exception_type"] = ex.GetType().FullName;
            plan.NativeOperationEvidence["native_exception_code"] = M3SketchProjectionExceptionCode(ex);
            plan.NativeOperationEvidence["native_exception_message"] = M3SketchProjectionSafeMessage(ex);
            M3ExecutionResult restored = RestoreM3SketchProjectionPlan(binding, state, plan,
                mark, ex, updateCallAttempted);
            plan.Execution = restored;
            return restored;
        }
    }

    private M3VerificationResult VerifySketchProjectionPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M3SketchProjectionPlanState plan = prepared == null ? null : prepared.AdapterState as M3SketchProjectionPlanState;
        if (plan == null || prepared.Kind != "edit_sketch_projection" || plan.Execution == null || plan.Execution.State != "APPLIED")
            return M3SketchProjectionFailure(binding == null ? null : binding.RevisionId,
                "only a successfully applied projection plan can be verified");
        BindingState state;
        try { state = RequireState(binding); }
        catch (M2ModelException ex)
        {
            return M3SketchProjectionFailure(binding == null ? null : binding.RevisionId, ex.Message);
        }
        if (!Object.ReferenceEquals(state, plan.BindingState) || !Object.ReferenceEquals(binding, plan.Binding) ||
            !String.Equals(binding.RevisionId, plan.Execution.RevisionId, StringComparison.Ordinal))
            return M3SketchProjectionFailure(binding == null ? null : binding.RevisionId,
                "projection plan belongs to a different binding or post-execution revision");
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return M3SketchProjectionFailure(binding.RevisionId, bindingReason ?? "NX model binding is no longer current");

        try
        {
            if (state.Session.ActiveSketch != null)
                return M3SketchProjectionFailure(binding.RevisionId, "a Sketch is active during projection verification");
            SnapshotResult current = Capture(binding, state);
            Dictionary<string, object> evidence = EvaluateM3SketchProjectionState(binding, state, plan,
                current.Snapshot, false);
            bool verified = M3SketchProjectionEvidenceChecksPassed(evidence);
            Dictionary<string, object> checks = evidence["checks"] as Dictionary<string, object>;
            return new M3VerificationResult
            {
                Verified = verified,
                RevisionId = binding.RevisionId,
                Checks = checks ?? new Dictionary<string, object>(StringComparer.Ordinal),
                Result = verified ? M3SketchProjectionExecutionResult(plan, binding.RevisionId, "VERIFIED", evidence)
                    : new Dictionary<string, object>(StringComparer.Ordinal),
                ErrorCode = verified ? null : "PROJECTION_VERIFICATION_FAILED",
                ErrorMessage = verified ? null : "one or more projection readback checks did not match the committed plan"
            };
        }
        catch (Exception ex)
        {
            return M3SketchProjectionFailure(binding.RevisionId,
                "projection verification readback failed (" + ex.GetType().Name + ")");
        }
    }

    private M3ExecutionResult RestoreM3SketchProjectionPlan(M2ModelBinding binding, BindingState state,
        M3SketchProjectionPlanState plan, Session.UndoMarkId mark, Exception operationException, bool updateCallAttempted)
    {
        Dictionary<string, object> restore = new Dictionary<string, object>(StringComparer.Ordinal);
        restore["undo_to_mark_attempted"] = true;
        restore["update_call_attempted"] = updateCallAttempted;
        restore["exception_type"] = operationException == null ? null : operationException.GetType().FullName;
        restore["exception_code"] = M3SketchProjectionExceptionCode(operationException);
        restore["exception_message"] = M3SketchProjectionSafeMessage(operationException);
        restore["active_sketch_tag_before"] = plan.ActiveSketchTagBefore;
        bool undoSucceeded = false;
        try
        {
            if (state.Session.ActiveSketch != null)
                throw new InvalidOperationException("NX still has an active Sketch before projection undo");
            state.Session.UndoToMark(mark, null);
            undoSucceeded = true;
            restore["undo_to_mark_succeeded"] = true;
        }
        catch (Exception undoException)
        {
            restore["undo_to_mark_succeeded"] = false;
            restore["undo_exception_type"] = undoException.GetType().FullName;
            restore["undo_exception_message"] = M3SketchProjectionSafeMessage(undoException);
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
                restore["undo_update_exception_message"] = M3SketchProjectionSafeMessage(updateException);
            }
        }
        restore["undo_update_succeeded"] = updateSucceeded;

        bool restored = false;
        if (undoSucceeded && updateSucceeded)
        {
            try
            {
                SnapshotResult restoredCapture = Capture(binding, state);
                Sketch active = state.Session.ActiveSketch;
                string activeTag = active == null ? null : active.Tag.ToString();
                bool activeRestored = String.Equals(plan.ActiveSketchTagBefore, activeTag, StringComparison.Ordinal);
                Dictionary<string, object> bodyEvidence;
                bool bodiesRestored = BodyMeasuresEqual(plan.BodiesBefore, CaptureM3BodyMeasures(state.WorkPart), out bodyEvidence);
                Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
                bool semantic = String.Equals(plan.SemanticBefore, CanonicalSemanticSnapshot(restoredCapture.Snapshot), StringComparison.Ordinal);
                Dictionary<string, M3SketchProjectionFeatureState> restoredFeatureStates =
                    CaptureM3SketchProjectionFeatureStates(state.WorkPart);
                bool history = String.Equals(plan.HistoryBefore,
                    M2Json.CanonicalText(M3SketchProjectionStableHistoryProjection(state.WorkPart, restoredFeatureStates)),
                    StringComparison.Ordinal);
                bool fullGeometry = String.Equals(plan.FullGeometryBefore, CaptureFullGeometry(state.WorkPart), StringComparison.Ordinal);
                bool geometry = CornerGeometryMapsEqual(plan.GeometryBefore, CaptureSketchCornerGeometry(state.WorkPart));
                bool constraints = OffsetConstraintMapsEqual(plan.ConstraintsBefore, CaptureAllSketchOffsetConstraints(state.WorkPart));
                bool frames = OffsetStringMapsEqual(plan.FramesBefore, CaptureAllSketchOffsetFrames(state.WorkPart));
                bool references = OffsetIntMapsEqual(plan.ReferenceStatesBefore, CaptureAllSketchOffsetReferenceStates(state.WorkPart));
                bool features = FeatureTagsMatch(plan.FeatureTagsBefore, CaptureFeatureTags(state.WorkPart)) &&
                    String.Equals(plan.FeatureInventoryBefore,
                        M3SketchProjectionFeatureInventoryText(restoredFeatureStates), StringComparison.Ordinal);
                bool dirty = state.WorkPart.IsModified == plan.DirtyBefore;
                bool expressionsRestored = String.Equals(plan.ExpressionsBefore,
                    M2Json.CanonicalText(SnapshotList(restoredCapture.Snapshot, "expressions")), StringComparison.Ordinal);
                bool curvesRestored = String.Equals(M3SketchProjectionCurveInventoryText(plan.CurveInventoryBefore),
                    M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart)), StringComparison.Ordinal);
                bool helpersRestored = PatternHelperTagInventoriesEqual(plan.PartHelperTagsBefore,
                    CapturePatternPartHelperTags(state.WorkPart));
                bool sources = String.Equals(plan.SourceSnapshotBefore,
                    M3SketchProjectionSourceSnapshot(CaptureM3SketchProjectionSourcesByTags(state.WorkPart, plan.Sketch,
                        M3SketchProjectionSourceTags(plan.Sources), plan.SketchOrigin, plan.SketchNormal, plan.SketchXAxis,
                        plan.SketchYAxis, plan.Direction)), StringComparison.Ordinal);
                restore["active_sketch_restored"] = activeRestored;
                restore["semantic_state_restored"] = semantic;
                restore["history_restored"] = history;
                restore["full_geometry_restored"] = fullGeometry;
                restore["sketch_geometry_restored"] = geometry;
                restore["constraints_restored"] = constraints;
                restore["frames_restored"] = frames;
                restore["reference_status_restored"] = references;
                restore["features_restored"] = features;
                restore["expressions_restored"] = expressionsRestored;
                restore["global_curve_inventory_restored"] = curvesRestored;
                restore["point_axis_direction_inventory_restored"] = helpersRestored;
                restore["bodies_restored"] = bodiesRestored;
                restore["body_measurements"] = bodyEvidence;
                restore["dirty_state_restored"] = dirty;
                restore["source_spatial_snapshot_restored"] = sources;
                restored = activeRestored && semantic && history && fullGeometry && geometry && constraints &&
                    frames && references && features && expressionsRestored && curvesRestored && helpersRestored &&
                    bodiesRestored && dirty && sources;
            }
            catch (Exception readbackException)
            {
                restore["restoration_readback_exception_type"] = readbackException.GetType().FullName;
                restore["restoration_readback_exception_message"] = M3SketchProjectionSafeMessage(readbackException);
            }
        }
        restore["full_restoration_proven"] = restored;

        bool markClosed = false;
        try
        {
            state.Session.DeleteUndoMark(mark, "");
            markClosed = true;
        }
        catch (Exception closeException)
        {
            restore["undo_mark_close_exception_type"] = closeException.GetType().FullName;
            restore["undo_mark_close_exception_message"] = M3SketchProjectionSafeMessage(closeException);
        }
        restore["undo_mark_closed"] = markClosed;
        plan.NativeOperationEvidence["restoration"] = restore;
        Dictionary<string, object> result = M3SketchProjectionExecutionResult(plan,
            binding == null ? null : binding.RevisionId, restored && markClosed ? "ROLLED_BACK" : "UNKNOWN", restore);
        if (restored && markClosed)
            return M3Execution("ROLLED_BACK", binding == null ? null : binding.RevisionId, result,
                "PROJECTION_ROLLED_BACK", "NX projection failed and Undo proved restoration of model, geometry, history, relations, and dirty state");
        return M3Execution("UNKNOWN", binding == null ? null : binding.RevisionId, result,
            "ROLLBACK_UNVERIFIED", "NX could not prove full restoration of the pre-projection state");
    }

    private Dictionary<string, object> EvaluateM3SketchProjectionState(M2ModelBinding binding, BindingState state,
        M3SketchProjectionPlanState plan, Dictionary<string, object> semanticSnapshot, bool captureAfterBaseline)
    {
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> measured = new Dictionary<string, object>(StringComparer.Ordinal);
        Sketch sketch = FindSketch(state.WorkPart, plan.SketchId);
        bool targetIdentity = sketch != null && String.Equals(sketch.Tag.ToString(), plan.SketchTag, StringComparison.Ordinal) &&
            sketch.Feature != null && String.Equals(sketch.Feature.Tag.ToString(), plan.SketchFeatureTag, StringComparison.Ordinal);
        Dictionary<string, object> frame = null;
        Point3d origin = new Point3d();
        Vector3d normal = new Vector3d(), axisX = new Vector3d(), axisY = new Vector3d();
        if (sketch != null) frame = M3SketchProjectionFrame(sketch, out origin, out normal, out axisX, out axisY);
        bool frameMatchesPlan = frame != null &&
            String.Equals(M2Json.CanonicalText(frame), plan.SketchFrameBefore, StringComparison.Ordinal);
        measured["target_sketch_identity_matches"] = targetIdentity;
        measured["target_frame_matches_prepare"] = frameMatchesPlan;
        measured["target_frame"] = frame;

        Dictionary<string, NXObject> geometryByTag = sketch == null
            ? new Dictionary<string, NXObject>(StringComparer.Ordinal)
            : M3SketchProjectionGeometryByTag(sketch);
        List<Curve> addedCurves = new List<Curve>();
        List<string> addedTags = new List<string>();
        List<object> unexpectedNew = new List<object>();
        foreach (KeyValuePair<string, NXObject> item in geometryByTag)
        {
            if (Array.IndexOf(plan.SketchGeometryTagsBefore, item.Key) >= 0) continue;
            Curve curve = item.Value as Curve;
            if (curve == null || (!(curve is Line) && !(curve is Arc)))
            {
                unexpectedNew.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", item.Key }, { "object_type", item.Value.GetType().Name }
                });
                continue;
            }
            addedTags.Add(item.Key);
            addedCurves.Add(curve);
        }
        addedTags.Sort(StringComparer.Ordinal);
        addedCurves.Sort(delegate(Curve left, Curve right)
        {
            return String.CompareOrdinal(left.Tag.ToString(), right.Tag.ToString());
        });
        bool factoryDeltaMatches = plan.OutputCurveTags != null &&
            M3SketchProjectionStringArraysEqual(plan.OutputCurveTags, addedTags.ToArray());
        Dictionary<string, object> deltaEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        deltaEvidence.Add("expected_count", plan.Request.SourceCurveIds.Count);
        deltaEvidence.Add("actual_curve_count", addedCurves.Count);
        deltaEvidence.Add("curve_tags", M3SketchProjectionStringObjects(addedTags.ToArray()));
        deltaEvidence.Add("unsupported_new_sketch_objects", unexpectedNew);
        deltaEvidence.Add("factory_output_tags_match_target_delta", factoryDeltaMatches);
        deltaEvidence.Add("output_curve_ids", M3SketchProjectionCurveIds(addedCurves));
        deltaEvidence.Add("output_curve_types", M3SketchProjectionCurveTypes(addedCurves));

        Dictionary<string, object> outputMatchEvidence;
        Dictionary<string, string> outputBySource;
        bool outputGeometryMatches = M3SketchProjectionOutputsMatch(plan, addedCurves,
            out outputBySource, out outputMatchEvidence);
        measured["target_geometry_delta"] = deltaEvidence;
        measured["expected_output_geometry"] = M3SketchProjectionExpectedOutputRecords(plan.Sources);
        measured["actual_output_geometry"] = outputMatchEvidence;
        measured["output_curve_ids"] = M3SketchProjectionCurveIds(addedCurves);
        measured["output_curve_types"] = M3SketchProjectionCurveTypes(addedCurves);
        measured["source_to_output_curve_ids"] = M3SketchProjectionMapRecords(outputBySource);
        measured["output_count_matches_source_count"] = addedCurves.Count == plan.Sources.Length;
        measured["output_tags_unique_and_factory_reported"] = factoryDeltaMatches;
        measured["target_frame_matches_prepare"] = frameMatchesPlan;
        measured["target_sketch_identity_matches"] = targetIdentity;

        Dictionary<string, object> relationEvidence = M3SketchProjectionRelationEvidence(state.WorkPart,
            plan, CaptureM3SketchProjectionFeatureStates(state.WorkPart), addedCurves);
        measured["native_nx_relation"] = relationEvidence;
        bool relationMatches = Boolean.Equals(relationEvidence["valid"], true);
        Dictionary<string, M3SketchProjectionCurveState> curveInventoryNow =
            CaptureM3SketchProjectionCurveInventory(state.WorkPart);
        Dictionary<string, List<string>> partHelperTagsNow = CapturePatternPartHelperTags(state.WorkPart);
        Dictionary<string, object> partHelperEvidence;
        bool partHelperTagsMatch = M3SketchProjectionPartHelperInventoryMatches(plan,
            state.WorkPart, sketch, partHelperTagsNow, out partHelperEvidence);
        measured["part_point_axis_direction_inventory"] = partHelperEvidence;
        measured["part_point_axis_direction_inventory_preserved"] = partHelperTagsMatch;
        Dictionary<string, object> globalCurveEvidence;
        bool globalCurveInventoryMatches = M3SketchProjectionGlobalCurveDeltaMatches(plan,
            curveInventoryNow, addedTags, relationEvidence, out globalCurveEvidence);
        measured["global_curve_inventory"] = globalCurveEvidence;

        Dictionary<string, CornerGeometrySnapshot> geometryNow = CaptureSketchCornerGeometry(state.WorkPart);
        Dictionary<string, CornerGeometrySnapshot> nonTargetGeometry = M3SketchProjectionWithoutTags(
            geometryNow, new HashSet<string>(addedTags, StringComparer.Ordinal));
        bool nonTargetGeometryMatches = CornerGeometryMapsEqual(plan.GeometryBefore, nonTargetGeometry);
        measured["non_target_geometry_preserved"] = nonTargetGeometryMatches;
        measured["non_target_geometry_before_count"] = plan.GeometryBefore.Count;
        measured["non_target_geometry_after_count"] = nonTargetGeometry.Count;
        measured["whole_sketch_geometry_changed_only_by_output_delta"] =
            geometryNow.Count == plan.GeometryBefore.Count + addedTags.Count;

        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraintsNow =
            CaptureAllSketchOffsetConstraints(state.WorkPart);
        bool constraintsMatch = OffsetConstraintMapsEqual(plan.ConstraintsBefore, constraintsNow);
        measured["constraints_preserved"] = constraintsMatch;
        measured["constraint_count_before"] = M3SketchProjectionConstraintCount(plan.ConstraintsBefore);
        measured["constraint_count_after"] = M3SketchProjectionConstraintCount(constraintsNow);

        Dictionary<string, string> framesNow = CaptureAllSketchOffsetFrames(state.WorkPart);
        bool framesMatch = OffsetStringMapsEqual(plan.FramesBefore, framesNow) && frameMatchesPlan;
        measured["all_sketch_frames_preserved"] = framesMatch;
        measured["frame_count_before"] = plan.FramesBefore.Count;
        measured["frame_count_after"] = framesNow.Count;

        Dictionary<string, int> refsNow = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
        Dictionary<string, object> referenceEvidence;
        bool refsMatch = M3SketchProjectionReferenceStatesMatch(plan.ReferenceStatesBefore,
            refsNow, addedCurves, plan.Request.Associative, out referenceEvidence);
        measured["reference_status"] = referenceEvidence;

        M3SketchProjectionSourceState[] sourceNow = CaptureM3SketchProjectionSourcesByTags(state.WorkPart,
            sketch, M3SketchProjectionSourceTags(plan.Sources), plan.SketchOrigin, plan.SketchNormal,
            plan.SketchXAxis, plan.SketchYAxis, plan.Direction);
        string sourceSnapshotNow = M3SketchProjectionSourceSnapshot(sourceNow);
        bool sourceGeometryMatches = String.Equals(plan.SourceSnapshotBefore, sourceSnapshotNow, StringComparison.Ordinal);
        measured["source_snapshot_before"] = plan.SourceRecordsBefore;
        measured["source_snapshot_after"] = M3SketchProjectionSourceRecords(sourceNow);
        measured["source_spatial_values_unchanged"] = sourceGeometryMatches;

        Dictionary<string, M3SketchProjectionFeatureState> featureStatesNow =
            CaptureM3SketchProjectionFeatureStates(state.WorkPart);
        bool featureInventoryMatches = M3SketchProjectionFeatureDeltaMatches(plan,
            featureStatesNow, relationEvidence);
        string semanticBeforeHistoryCheck = CanonicalSemanticSnapshot(semanticSnapshot);
        Dictionary<string, object> historyEvidence;
        bool historyMatches = M3SketchProjectionHistoryMatches(plan, state.WorkPart,
            featureStatesNow, relationEvidence, out historyEvidence);
        measured["feature_inventory_before"] = plan.FeatureInventoryBefore;
        measured["feature_inventory_after"] = M3SketchProjectionFeatureInventoryText(featureStatesNow);
        measured["expected_project_curve_feature_delta"] = plan.Request.Associative ? 1 : 0;
        measured["feature_inventory_delta_matches"] = featureInventoryMatches;
        measured["history_projection_matches_before_outside_allowed_projection"] = historyMatches;
        measured["stable_history_relation_evidence"] = historyEvidence;
        measured["history_comparison_did_not_change_semantic_snapshot"] = String.Equals(
            semanticBeforeHistoryCheck, CanonicalSemanticSnapshot(semanticSnapshot), StringComparison.Ordinal);
        bool expressionsMatch = String.Equals(plan.ExpressionsBefore,
            M2Json.CanonicalText(SnapshotList(semanticSnapshot, "expressions")), StringComparison.Ordinal);
        measured["expressions_preserved"] = expressionsMatch;
        measured["native_projection_feature"] = relationEvidence;

        Dictionary<string, object> bodiesEvidence;
        bool bodiesMatch = BodyMeasuresEqual(plan.BodiesBefore, CaptureM3BodyMeasures(state.WorkPart), out bodiesEvidence);
        measured["body_measurements"] = bodiesEvidence;
        measured["body_inventory_and_shape_preserved"] = bodiesMatch;

        bool dirtyNow = state.WorkPart.IsModified;
        bool dirtyMatches = dirtyNow;
        measured["dirty_before"] = plan.DirtyBefore;
        measured["dirty_after"] = dirtyNow;
        measured["mutation_dirty_state_expected"] = true;
        measured["dirty_state_matches"] = dirtyMatches;

        Sketch.Status solverStatus;
        int degreesOfFreedom;
        bool solverMatches = false;
        try
        {
            solverStatus = sketch.GetStatus(out degreesOfFreedom);
            solverMatches = String.Equals(solverStatus.ToString(), plan.SolverStatusBefore, StringComparison.Ordinal) &&
                degreesOfFreedom == plan.DegreesOfFreedomBefore;
            measured["solver_status_before"] = plan.SolverStatusBefore;
            measured["solver_status_after"] = solverStatus.ToString();
            measured["degrees_of_freedom_before"] = plan.DegreesOfFreedomBefore;
            measured["degrees_of_freedom_after"] = degreesOfFreedom;
            measured["solver_status_matches_prepare"] = solverMatches;
        }
        catch (Exception ex)
        {
            measured["solver_readback_exception_type"] = ex.GetType().FullName;
            measured["solver_status_matches_prepare"] = false;
        }

        if (!captureAfterBaseline)
        {
            bool afterGeometry = CornerGeometryMapsEqual(plan.GeometryAfter, geometryNow) &&
                String.Equals(plan.FullGeometryAfter, CaptureFullGeometry(state.WorkPart), StringComparison.Ordinal);
            bool afterConstraints = OffsetConstraintMapsEqual(plan.ConstraintsAfter, constraintsNow);
            bool afterFrames = OffsetStringMapsEqual(plan.FramesAfter, framesNow);
            bool afterReferences = OffsetIntMapsEqual(plan.ReferenceStatesAfter, refsNow);
            bool afterBodies = BodyMeasuresEqual(plan.BodiesAfter, CaptureM3BodyMeasures(state.WorkPart), out bodiesEvidence);
            bool afterHistory = String.Equals(plan.HistoryAfter,
                M2Json.CanonicalText(M3SketchProjectionStableHistoryProjection(state.WorkPart, featureStatesNow)),
                StringComparison.Ordinal);
            bool afterExpressions = String.Equals(plan.ExpressionsAfter,
                M2Json.CanonicalText(SnapshotList(semanticSnapshot, "expressions")), StringComparison.Ordinal);
            bool afterFeatures = String.Equals(plan.FeatureInventoryAfter,
                M3SketchProjectionFeatureInventoryText(featureStatesNow), StringComparison.Ordinal);
            bool afterCurveInventory = String.Equals(
                M3SketchProjectionCurveInventoryText(plan.CurveInventoryAfter),
                M3SketchProjectionCurveInventoryText(curveInventoryNow), StringComparison.Ordinal);
            bool afterHelperTags = PatternHelperTagInventoriesEqual(plan.PartHelperTagsAfter, partHelperTagsNow);
            bool afterSemantic = String.Equals(plan.SemanticAfter,
                CanonicalSemanticSnapshot(semanticSnapshot), StringComparison.Ordinal);
            bool afterDirty = dirtyNow == plan.DirtyAfter;
            measured["matches_execute_geometry"] = afterGeometry;
            measured["matches_execute_constraints"] = afterConstraints;
            measured["matches_execute_frames"] = afterFrames;
            measured["matches_execute_reference_states"] = afterReferences;
            measured["matches_execute_bodies"] = afterBodies;
            measured["matches_execute_history"] = afterHistory;
            measured["matches_execute_expressions"] = afterExpressions;
            measured["matches_execute_feature_inventory"] = afterFeatures;
            measured["matches_execute_global_curve_inventory"] = afterCurveInventory;
            measured["matches_execute_part_helper_inventory"] = afterHelperTags;
            measured["matches_execute_semantic_snapshot"] = afterSemantic;
            measured["matches_execute_dirty_state"] = afterDirty;
            outputGeometryMatches = outputGeometryMatches && afterGeometry;
            constraintsMatch = constraintsMatch && afterConstraints;
            framesMatch = framesMatch && afterFrames;
            refsMatch = refsMatch && afterReferences;
            bodiesMatch = bodiesMatch && afterBodies;
            historyMatches = historyMatches && afterHistory && afterFeatures && afterSemantic;
            globalCurveInventoryMatches = globalCurveInventoryMatches && afterCurveInventory;
            partHelperTagsMatch = partHelperTagsMatch && afterHelperTags;
            expressionsMatch = expressionsMatch && afterExpressions;
            dirtyMatches = dirtyMatches && afterDirty;
        }

        checks.Add("projection_geometry", VerificationCheck(
            targetIdentity && frameMatchesPlan && factoryDeltaMatches &&
                addedCurves.Count == plan.Sources.Length && unexpectedNew.Count == 0 && outputGeometryMatches
                ? "VERIFIED" : "MISMATCH", measured));
        checks.Add("source_curves", VerificationCheck(sourceGeometryMatches ? "VERIFIED" : "MISMATCH", measured));
        checks.Add("sketch_constraints", VerificationCheck(constraintsMatch ? "VERIFIED" : "MISMATCH", measured));
        checks.Add("sketch_frames", VerificationCheck(framesMatch ? "VERIFIED" : "MISMATCH", measured));
        checks.Add("reference_status", VerificationCheck(refsMatch && relationMatches ? "VERIFIED" : "MISMATCH", measured));
        checks.Add("non_target_geometry", VerificationCheck(nonTargetGeometryMatches &&
            Boolean.Equals(measured["whole_sketch_geometry_changed_only_by_output_delta"], true) &&
            globalCurveInventoryMatches && partHelperTagsMatch
            ? "VERIFIED" : "MISMATCH", measured));
        checks.Add("history", VerificationCheck(historyMatches && featureInventoryMatches && expressionsMatch && dirtyMatches
            ? "VERIFIED" : "MISMATCH", measured));
        checks.Add("bodies", VerificationCheck(bodiesMatch ? "VERIFIED" : "MISMATCH", measured));
        checks.Add("sketch_solver_status", VerificationCheck(solverMatches ? "VERIFIED" : "MISMATCH", measured));

        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("checks", checks);
        result.Add("measured", measured);
        result.Add("all_checks_verified", M3SketchProjectionChecksPassed(checks));
        result.Add("revision_id", binding == null ? null : binding.RevisionId);
        result.Add("output_curve_ids", plan.OutputCurveIds == null ? new List<object>() : M3SketchProjectionStringObjects(plan.OutputCurveIds));
        result.Add("output_curve_tags", plan.OutputCurveTags == null ? new List<object>() : M3SketchProjectionStringObjects(plan.OutputCurveTags));
        result.Add("associative", plan.Request.Associative);
        return result;
    }

    private static void ValidateM3SketchProjectionRequest(M3SketchProjectionRequest request)
    {
        if (request == null || String.IsNullOrWhiteSpace(request.RevisionId) || String.IsNullOrWhiteSpace(request.SketchId))
            throw new M2ModelException("INVALID_REQUEST", "revision_id and sketch_id are required for sketch projection");
        if (request.SourceCurveIds == null || request.SourceCurveIds.Count < 1 || request.SourceCurveIds.Count > 128)
            throw new M2ModelException("INVALID_REQUEST", "source_curve_ids must contain 1 through 128 native source identities");
        if (request.DirectionXYZ == null || request.DirectionXYZ.Length != 3)
            throw new M2ModelException("INVALID_REQUEST", "direction_xyz is required and must contain three finite values");
        double directionScale = Math.Max(Math.Abs(request.DirectionXYZ[0]),
            Math.Max(Math.Abs(request.DirectionXYZ[1]), Math.Abs(request.DirectionXYZ[2])));
        if (!IsFinite(request.DirectionXYZ[0]) || !IsFinite(request.DirectionXYZ[1]) ||
            !IsFinite(request.DirectionXYZ[2]) || !IsFinite(directionScale) || directionScale <= 0.0)
            throw new M2ModelException("INVALID_REQUEST", "direction_xyz must be a finite nonzero vector");
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string sourceId in request.SourceCurveIds)
            if (String.IsNullOrWhiteSpace(sourceId) || !seen.Add(sourceId))
                throw new M2ModelException("INVALID_REQUEST", "source_curve_ids must contain nonempty unique native JournalIdentifiers");
    }

    private static M3SketchProjectionRequest CloneM3SketchProjectionRequest(M3SketchProjectionRequest source)
    {
        return new M3SketchProjectionRequest
        {
            RevisionId = source.RevisionId,
            SketchId = source.SketchId,
            SourceCurveIds = new List<string>(source.SourceCurveIds),
            DirectionXYZ = (double[])source.DirectionXYZ.Clone(),
            Associative = source.Associative
        };
    }

    private static bool M3SketchProjectionNativeSketchFeature(Part part, Sketch sketch)
    {
        try
        {
            return part != null && sketch != null && sketch.Feature != null && !sketch.Feature.IsOccurrence &&
                sketch.OwningPart != null && sketch.OwningPart.Tag.Equals(part.Tag) &&
                sketch.Feature.OwningPart != null && sketch.Feature.OwningPart.Tag.Equals(part.Tag);
        }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "could not verify native target Sketch feature ownership (" + ex.GetType().Name + ")");
        }
    }

    private static Dictionary<string, object> M3SketchProjectionFrame(Sketch sketch,
        out Point3d origin, out Vector3d normal, out Vector3d axisX, out Vector3d axisY)
    {
        if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "projection Sketch is unavailable");
        origin = sketch.Origin;
        Matrix3x3 axes = sketch.Orientation.Element;
        normal = new Vector3d(axes.Zx, axes.Zy, axes.Zz);
        axisX = new Vector3d(axes.Xx, axes.Xy, axes.Xz);
        axisY = new Vector3d(axes.Yx, axes.Yy, axes.Yz);
        M3SketchProjectionNormalize(ref normal, "target Sketch normal");
        M3SketchProjectionNormalize(ref axisX, "target Sketch x-axis");
        M3SketchProjectionNormalize(ref axisY, "target Sketch y-axis");
        if (!M3SketchProjectionNear(M3SketchProjectionDot(axisX, axisY), 0.0, M3SketchProjectionAngleTolerance) ||
            Math.Abs(M3SketchProjectionDot(axisX, normal)) > M3SketchProjectionAngleTolerance ||
            Math.Abs(M3SketchProjectionDot(axisY, normal)) > M3SketchProjectionAngleTolerance)
            throw new M2ModelException("UNSUPPORTED_SKETCH_FRAME", "target Sketch frame axes are not a finite orthonormal frame");
        if (!M3SketchProjectionFinite(origin))
            throw new M2ModelException("UNSUPPORTED_SKETCH_FRAME", "target Sketch origin is non-finite");
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
        record.Add("origin_mm", M3SketchProjectionPoint(origin));
        record.Add("x_axis_world", M3SketchProjectionVector(axisX));
        record.Add("y_axis_world", M3SketchProjectionVector(axisY));
        record.Add("normal_world", M3SketchProjectionVector(normal));
        return record;
    }

    private static Vector3d M3SketchProjectionDirection(double[] input, Vector3d sketchNormal)
    {
        double scale = Math.Max(Math.Abs(input[0]), Math.Max(Math.Abs(input[1]), Math.Abs(input[2])));
        if (!IsFinite(input[0]) || !IsFinite(input[1]) || !IsFinite(input[2]) ||
            !IsFinite(scale) || scale <= 0.0)
            throw new M2ModelException("INVALID_REQUEST", "direction_xyz must be a finite nonzero vector");
        double x = input[0] / scale;
        double y = input[1] / scale;
        double z = input[2] / scale;
        double length = Math.Sqrt(x * x + y * y + z * z);
        if (!IsFinite(length) || length <= 0.0)
            throw new M2ModelException("INVALID_REQUEST", "direction_xyz must be a finite nonzero vector");
        Vector3d direction = new Vector3d(x / length, y / length, z / length);
        double alignment = M3SketchProjectionDot(direction, sketchNormal);
        Vector3d cross = M3SketchProjectionCross(direction, sketchNormal);
        double crossLength = M3SketchProjectionLength(cross);
        if (!IsFinite(alignment) || !IsFinite(crossLength) ||
            Math.Abs(Math.Abs(alignment) - 1.0) > M3SketchProjectionAngleTolerance ||
            crossLength > M3SketchProjectionAngleTolerance)
            throw new M2ModelException("UNSUPPORTED_DIRECTION", "direction_xyz must be parallel to the target Sketch normal or its negative");
        return direction;
    }

    private static M3SketchProjectionSourceState[] CaptureM3SketchProjectionSources(Part part, Sketch targetSketch,
        List<string> sourceIds, Point3d sketchOrigin, Vector3d sketchNormal, Vector3d sketchXAxis,
        Vector3d sketchYAxis, Vector3d projectionDirection)
    {
        NXObject[] resolved = ResolveM3SketchProjectionSources(part, sourceIds);
        List<M3SketchProjectionFeatureState> features = new List<M3SketchProjectionFeatureState>(
            CaptureM3SketchProjectionFeatureStates(part).Values);
        Dictionary<string, string> featureIdsByTag = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (M3SketchProjectionFeatureState feature in features)
            featureIdsByTag[feature.Tag] = feature.Id;

        M3SketchProjectionSourceState[] result = new M3SketchProjectionSourceState[resolved.Length];
        for (int i = 0; i < resolved.Length; i++)
        {
            NXObject source = resolved[i];
            M3SketchProjectionSourceState item = new M3SketchProjectionSourceState
            {
                Source = source,
                Id = RequiredJournalId(source, "projection source"),
                Tag = source.Tag.ToString(),
                ObjectType = source.GetType().Name,
                TargetOrigin = sketchOrigin,
                TargetNormal = sketchNormal,
                TargetXAxis = sketchXAxis,
                TargetYAxis = sketchYAxis,
                SourceFeatureParentIds = new List<string>()
            };
            if (!source.OwningPart.Tag.Equals(part.Tag) || source.IsOccurrence)
                throw new M2ModelException("UNSUPPORTED_SOURCE", "projection sources must be native non-occurrence objects in the bound Work Part");

            Line line = source as Line;
            Arc arc = source as Arc;
            Edge edge = source as Edge;
            if (line != null)
            {
                item.GeometryType = "Line";
                item.Start = line.StartPoint;
                item.End = line.EndPoint;
                item.Length = line.GetLength();
                M3SketchProjectionValidateLine(item, projectionDirection, sketchOrigin,
                    sketchNormal, M3SketchProjectionPositionToleranceMm);
                item.SourceSketchId = M3SketchProjectionOwningSketchId(part, targetSketch, line,
                    item.SourceFeatureParentIds);
                M3SketchProjectionAddCurveOwnerFeature(line, featureIdsByTag, item.SourceFeatureParentIds);
            }
            else if (arc != null)
            {
                item.GeometryType = "Arc";
                item.Length = arc.GetLength();
                Point3d frameCenter;
                arc.GetOrientation(out frameCenter, out item.AxisX, out item.AxisY);
                item.Center = arc.CenterPoint;
                item.Radius = arc.Radius;
                item.StartAngle = arc.StartAngle;
                item.EndAngle = arc.EndAngle;
                item.DirectedSweep = arc.EndAngle - arc.StartAngle;
                item.IsPeriodic = M3SketchProjectionArcIsPeriodic(item.Radius,
                    item.StartAngle, item.EndAngle, item.Length);
                M3SketchProjectionPrepareArc(item, sketchOrigin, sketchNormal,
                    sketchXAxis, sketchYAxis, projectionDirection);
                item.SourceSketchId = M3SketchProjectionOwningSketchId(part, targetSketch, arc,
                    item.SourceFeatureParentIds);
                M3SketchProjectionAddCurveOwnerFeature(arc, featureIdsByTag, item.SourceFeatureParentIds);
            }
            else if (edge != null)
            {
                Body body = edge.GetBody();
                if (body == null || !body.IsSolidBody || !body.OwningPart.Tag.Equals(part.Tag))
                    throw new M2ModelException("UNSUPPORTED_SOURCE", "Edge sources must belong to a native solid Body in the bound Work Part");
                item.BodyId = RequiredJournalId(body, "projection source solid Body");
                if (edge.SolidEdgeType == Edge.EdgeType.Linear)
                {
                    item.GeometryType = "Line";
                    edge.GetVertices(out item.Start, out item.End);
                    item.Length = edge.GetLength();
                    M3SketchProjectionValidateLine(item, projectionDirection, sketchOrigin,
                        sketchNormal, M3SketchProjectionPositionToleranceMm);
                }
                else if (edge.SolidEdgeType == Edge.EdgeType.Circular)
                {
                    item.GeometryType = "Arc";
                    M3SketchProjectionReadCircularEdge(edge, item);
                    M3SketchProjectionPrepareArc(item, sketchOrigin, sketchNormal,
                        sketchXAxis, sketchYAxis, projectionDirection);
                }
                else
                    throw new M2ModelException("UNSUPPORTED_SOURCE", "only Edge.EdgeType.Linear and Edge.EdgeType.Circular solid Edges are supported");
                Tag[] sourceFeatureTags;
                try { UFSession.GetUFSession().Modl.AskEdgeFeats(edge.Tag, out sourceFeatureTags); }
                catch (Exception ex)
                {
                    throw new M2ModelException("MODEL_READ_FAILED", "could not read source Edge feature provenance (" + ex.GetType().Name + ")");
                }
                if (sourceFeatureTags != null)
                    foreach (Tag featureTag in sourceFeatureTags)
                    {
                        string id;
                        if (featureIdsByTag.TryGetValue(featureTag.ToString(), out id) && !item.SourceFeatureParentIds.Contains(id))
                            item.SourceFeatureParentIds.Add(id);
                    }
                item.SourceFeatureParentIds.Sort(StringComparer.Ordinal);
            }
            else
                throw new M2ModelException("UNSUPPORTED_SOURCE", "only native Line/Arc curves and solid Linear/Circular Edges are supported");

            if (!String.IsNullOrEmpty(item.SourceSketchId) &&
                String.Equals(item.SourceSketchId, RequiredJournalId(targetSketch, "projection target Sketch"), StringComparison.Ordinal))
                throw new M2ModelException("SKETCH_SOURCE_TARGET_MISMATCH", "a Sketch curve cannot be projected back into its own source Sketch");

            if (!IsFinite(item.Length) || item.Length <= M3SketchProjectionPositionToleranceMm)
                throw new M2ModelException("DEGENERATE_SOURCE", "every projection source must have finite length greater than 1e-6 mm");
            result[i] = item;
        }
        return result;
    }

    private static void M3SketchProjectionAddCurveOwnerFeature(Curve curve,
        Dictionary<string, string> featureIdsByTag, List<string> sourceFeatureParentIds)
    {
        if (curve == null || featureIdsByTag == null || sourceFeatureParentIds == null)
            throw new M2ModelException("MODEL_READ_FAILED", "curve feature provenance inputs are unavailable");
        Tag featureTag;
        try { UFSession.GetUFSession().Modl.AskObjectFeat(curve.Tag, out featureTag); }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "could not read source Curve feature provenance (" + ex.GetType().Name + ")");
        }
        if (featureTag == Tag.Null) return;
        string featureId;
        if (!featureIdsByTag.TryGetValue(featureTag.ToString(), out featureId))
            throw new M2ModelException("MODEL_READ_FAILED", "source Curve owner Feature is missing from the Work Part history inventory");
        if (!sourceFeatureParentIds.Contains(featureId)) sourceFeatureParentIds.Add(featureId);
        sourceFeatureParentIds.Sort(StringComparer.Ordinal);
    }

    private static M3SketchProjectionSourceState[] CaptureM3SketchProjectionSourcesByTags(Part part,
        Sketch targetSketch, List<string> sourceTags, Point3d sketchOrigin, Vector3d sketchNormal,
        Vector3d sketchXAxis, Vector3d sketchYAxis, Vector3d projectionDirection)
    {
        List<string> currentSourceIds = M3SketchProjectionSourceIdsByTags(part, sourceTags);
        return CaptureM3SketchProjectionSources(part, targetSketch, currentSourceIds, sketchOrigin,
            sketchNormal, sketchXAxis, sketchYAxis, projectionDirection);
    }

    private static List<string> M3SketchProjectionSourceIdsByTags(Part part, List<string> sourceTags)
    {
        if (part == null || sourceTags == null || sourceTags.Count == 0)
            throw new M2ModelException("STALE_SOURCE_GEOMETRY", "projection source Tags are unavailable after model update");
        List<string> result = new List<string>();
        HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
        Curve[] curves = part.Curves.ToArray();
        Body[] bodies = part.Bodies.ToArray();
        if (curves == null || bodies == null)
            throw new M2ModelException("MODEL_READ_FAILED", "Part curve or body inventory returned null while resolving stable projection source Tags");
        foreach (string sourceTag in sourceTags)
        {
            if (String.IsNullOrEmpty(sourceTag) || !unique.Add(sourceTag))
                throw new M2ModelException("STALE_SOURCE_GEOMETRY", "prepared projection source Tags are empty or duplicated");
            NXObject found = null;
            foreach (Curve curve in curves)
            {
                if (curve == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Part.Curves contains null while resolving a projection source Tag");
                if (!String.Equals(curve.Tag.ToString(), sourceTag, StringComparison.Ordinal)) continue;
                if (found != null && found.Tag != curve.Tag)
                    throw new M2ModelException("STALE_SOURCE_GEOMETRY", "a prepared projection source Tag resolves to multiple curves");
                found = curve;
            }
            foreach (Body body in bodies)
            {
                if (body == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Part.Bodies contains null while resolving a projection source Tag");
                Edge[] edges = body.GetEdges();
                if (edges == null) throw new M2ModelException("MODEL_READ_FAILED", "Body.GetEdges returned null while resolving a projection source Tag");
                foreach (Edge edge in edges)
                {
                    if (edge == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Body edge inventory contains null while resolving a projection source Tag");
                    if (!String.Equals(edge.Tag.ToString(), sourceTag, StringComparison.Ordinal)) continue;
                    if (found != null && found.Tag != edge.Tag)
                        throw new M2ModelException("STALE_SOURCE_GEOMETRY", "a prepared projection source Tag resolves to multiple objects");
                    found = edge;
                }
            }
            if (found == null)
                throw new M2ModelException("STALE_SOURCE_GEOMETRY", "a prepared projection source Tag no longer resolves after model update");
            if (found.OwningPart == null || found.OwningPart.Tag != part.Tag || found.IsOccurrence)
                throw new M2ModelException("STALE_SOURCE_GEOMETRY", "a prepared projection source Tag no longer belongs to the bound Work Part");
            result.Add(RequiredJournalId(found, "projection source resolved by stable Tag"));
        }
        return result;
    }

    private static List<string> M3SketchProjectionSourceTags(M3SketchProjectionSourceState[] sources)
    {
        List<string> result = new List<string>();
        if (sources == null) return result;
        foreach (M3SketchProjectionSourceState source in sources)
        {
            if (source == null || String.IsNullOrEmpty(source.Tag))
                throw new M2ModelException("STALE_SOURCE_GEOMETRY", "prepared projection source has no stable Tag");
            result.Add(source.Tag);
        }
        return result;
    }

    private static NXObject[] ResolveM3SketchProjectionSources(Part part, List<string> sourceIds)
    {
        if (part == null || sourceIds == null) throw new M2ModelException("MODEL_READ_FAILED", "projection source resolution has no Work Part or source ids");
        Curve[] curves = part.Curves.ToArray();
        Body[] bodies = part.Bodies.ToArray();
        if (curves == null || bodies == null)
            throw new M2ModelException("MODEL_READ_FAILED", "Work Part curve or body inventory is unavailable during projection source resolution");
        NXObject[] result = new NXObject[sourceIds.Count];
        for (int i = 0; i < sourceIds.Count; i++)
        {
            NXObject found = null;
            int matches = 0;
            foreach (Curve curve in curves)
            {
                if (curve != null && String.Equals(RequiredJournalId(curve, "part curve"), sourceIds[i], StringComparison.Ordinal))
                {
                    found = curve;
                    matches++;
                }
            }
            foreach (Body body in bodies)
            {
                if (body == null) throw new M2ModelException("UNSUPPORTED_MODEL", "body inventory contains null during projection source resolution");
                Edge[] edges = body.GetEdges();
                if (edges == null) throw new M2ModelException("MODEL_READ_FAILED", "Body.GetEdges returned null during projection source resolution");
                foreach (Edge edge in edges)
                {
                    if (edge != null && String.Equals(RequiredJournalId(edge, "solid Edge"), sourceIds[i], StringComparison.Ordinal))
                    {
                        found = edge;
                        matches++;
                    }
                }
            }
            if (matches != 1 || found == null)
                throw new M2ModelException(matches == 0 ? "SOURCE_NOT_FOUND" : "AMBIGUOUS_SOURCE",
                    "each source_curve_id must identify exactly one native Curve or solid Edge in the Work Part");
            result[i] = found;
        }
        return result;
    }

    private static void M3SketchProjectionReadCircularEdge(Edge edge, M3SketchProjectionSourceState item)
    {
        if (edge == null || item == null) throw new M2ModelException("UNSUPPORTED_SOURCE", "circular Edge data is unavailable");
        UFSession uf = UFSession.GetUFSession();
        IntPtr evaluator = IntPtr.Zero;
        try
        {
            uf.Eval.Initialize2(edge.Tag, out evaluator);
            if (evaluator == IntPtr.Zero)
                throw new M2ModelException("MODEL_READ_FAILED", "NX returned no evaluator for a circular source Edge");
            UFEval.Arc arc;
            uf.Eval.AskArc(evaluator, out arc);
            if (arc.center == null || arc.center.Length < 3 || arc.x_axis == null || arc.x_axis.Length < 3 ||
                arc.y_axis == null || arc.y_axis.Length < 3 || arc.limits == null || arc.limits.Length < 2)
                throw new M2ModelException("MODEL_READ_FAILED", "NX returned incomplete circular source Edge evaluator data");
            item.Center = new Point3d(arc.center[0], arc.center[1], arc.center[2]);
            item.AxisX = new Vector3d(arc.x_axis[0], arc.x_axis[1], arc.x_axis[2]);
            item.AxisY = new Vector3d(arc.y_axis[0], arc.y_axis[1], arc.y_axis[2]);
            item.Radius = arc.radius;
            item.StartAngle = arc.limits[0];
            item.EndAngle = arc.limits[1];
            item.DirectedSweep = item.EndAngle - item.StartAngle;
            item.IsPeriodic = arc.is_periodic;
            item.Length = edge.GetLength();
            if (item.IsPeriodic)
            {
                item.StartAngle = 0.0;
                item.EndAngle = 2.0 * Math.PI;
                item.DirectedSweep = 2.0 * Math.PI;
            }
            item.Start = M3SketchProjectionArcPoint(item.Center, item.AxisX, item.AxisY,
                item.Radius, item.StartAngle);
            item.End = M3SketchProjectionArcPoint(item.Center, item.AxisX, item.AxisY,
                item.Radius, item.EndAngle);
        }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "could not evaluate circular source Edge (" + ex.GetType().Name + ")");
        }
        finally
        {
            if (evaluator != IntPtr.Zero)
            {
                try { uf.Eval.Free(evaluator); }
                catch { }
            }
        }
    }

    private static void M3SketchProjectionValidateLine(M3SketchProjectionSourceState source,
        Vector3d direction, Point3d origin, Vector3d normal, double tolerance)
    {
        if (!M3SketchProjectionFinite(source.Start) || !M3SketchProjectionFinite(source.End))
            throw new M2ModelException("UNSUPPORTED_SOURCE", "source Line endpoints must be finite");
        source.ExpectedStart = M3SketchProjectionProjectPoint(source.Start, origin, normal, direction);
        source.ExpectedEnd = M3SketchProjectionProjectPoint(source.End, origin, normal, direction);
        double projectedLength = M3SketchProjectionDistance(source.ExpectedStart, source.ExpectedEnd);
        if (!IsFinite(projectedLength) || projectedLength <= tolerance)
            throw new M2ModelException("DEGENERATE_PROJECTION", "projected Line source would collapse to a point in the target Sketch plane");
    }

    private static void M3SketchProjectionPrepareArc(M3SketchProjectionSourceState source,
        Point3d sketchOrigin, Vector3d sketchNormal, Vector3d sketchXAxis, Vector3d sketchYAxis,
        Vector3d direction)
    {
        if (!M3SketchProjectionFinite(source.Center) || !IsFinite(source.Radius) ||
            source.Radius <= M3SketchProjectionPositionToleranceMm ||
            !IsFinite(source.StartAngle) || !IsFinite(source.EndAngle) || !IsFinite(source.Length))
            throw new M2ModelException("UNSUPPORTED_SOURCE", "circular source values must be finite and radius must exceed 1e-6 mm");
        M3SketchProjectionNormalize(ref source.AxisX, "circular source x-axis");
        M3SketchProjectionNormalize(ref source.AxisY, "circular source y-axis");
        source.PlaneNormal = M3SketchProjectionCross(source.AxisX, source.AxisY);
        M3SketchProjectionNormalize(ref source.PlaneNormal, "circular source plane normal");
        if (Math.Abs(M3SketchProjectionDot(source.AxisX, source.AxisY)) > M3SketchProjectionAngleTolerance)
            throw new M2ModelException("UNSUPPORTED_SOURCE", "circular source axes are not orthogonal");
        double planeAlignment = Math.Abs(M3SketchProjectionDot(source.PlaneNormal, sketchNormal));
        double planeCross = M3SketchProjectionLength(M3SketchProjectionCross(source.PlaneNormal, sketchNormal));
        if (!IsFinite(planeAlignment) || !IsFinite(planeCross) ||
            Math.Abs(1.0 - planeAlignment) > M3SketchProjectionAngleTolerance ||
            planeCross > M3SketchProjectionAngleTolerance)
            throw new M2ModelException("UNSUPPORTED_SOURCE", "Arc/circular Edge plane must be parallel to the target Sketch plane");
        double signedSweep = source.EndAngle - source.StartAngle;
        source.DirectedSweep = source.IsPeriodic && Math.Abs(signedSweep) <= M3SketchProjectionAngleTolerance
            ? 2.0 * Math.PI : signedSweep;
        source.ExpectedSweep = source.IsPeriodic ? 2.0 * Math.PI : Math.Abs(signedSweep);
        if (!IsFinite(source.ExpectedSweep) || source.ExpectedSweep <= M3SketchProjectionAngleTolerance ||
            source.ExpectedSweep > 2.0 * Math.PI + M3SketchProjectionAngleTolerance)
            throw new M2ModelException("UNSUPPORTED_SOURCE", "circular source sweep is not finite or is outside one revolution");
        source.IsPeriodic = source.IsPeriodic ||
            Math.Abs(source.ExpectedSweep - 2.0 * Math.PI) <= M3SketchProjectionAngleTolerance;
        if (source.IsPeriodic)
        {
            source.ExpectedSweep = 2.0 * Math.PI;
            if (Math.Abs(source.DirectedSweep) <= M3SketchProjectionAngleTolerance)
                source.DirectedSweep = 2.0 * Math.PI;
        }
        if (!IsFinite(source.Length) || source.Length <= M3SketchProjectionPositionToleranceMm)
            throw new M2ModelException("DEGENERATE_SOURCE", "circular source length must be finite and greater than 1e-6 mm");
        double measuredSweep = source.Length / source.Radius;
        if (!IsFinite(measuredSweep) || Math.Abs(measuredSweep - source.ExpectedSweep) > M3SketchProjectionAngleTolerance)
            throw new M2ModelException("UNSUPPORTED_SOURCE", "circular source parameter interval does not match its measured length; the arc sweep cannot be verified conservatively");
        source.Start = M3SketchProjectionArcPoint(source.Center, source.AxisX, source.AxisY,
            source.Radius, source.StartAngle);
        source.End = M3SketchProjectionArcPoint(source.Center, source.AxisX, source.AxisY,
            source.Radius, source.EndAngle);
        source.ExpectedCenter = M3SketchProjectionProjectPoint(source.Center, sketchOrigin,
            sketchNormal, direction);
        source.ExpectedStart = M3SketchProjectionProjectPoint(source.Start, sketchOrigin,
            sketchNormal, direction);
        source.ExpectedEnd = M3SketchProjectionProjectPoint(source.End, sketchOrigin,
            sketchNormal, direction);

        // A projection direction parallel to the Sketch normal leaves the Arc plane intact.
        Vector3d projectedX = M3SketchProjectionProjectVector(source.AxisX, sketchNormal);
        Vector3d projectedY = M3SketchProjectionProjectVector(source.AxisY, sketchNormal);
        double xLen = M3SketchProjectionLength(projectedX);
        double yLen = M3SketchProjectionLength(projectedY);
        if (!IsFinite(xLen) || !IsFinite(yLen) || xLen <= M3SketchProjectionAngleTolerance ||
            yLen <= M3SketchProjectionAngleTolerance)
            throw new M2ModelException("UNSUPPORTED_SOURCE", "circular source axes cannot be expressed in the target Sketch frame");
        projectedX = M3SketchProjectionScale(projectedX, 1.0 / xLen);
        projectedY = M3SketchProjectionScale(projectedY, 1.0 / yLen);
        source.AxisX = projectedX;
        source.AxisY = projectedY;
    }

    private static string M3SketchProjectionOwningSketchId(Part part, Sketch targetSketch,
        Curve curve, List<string> sourceFeatureParentIds)
    {
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory is unavailable while resolving a projection source owner");
        string ownerId = null;
        foreach (Sketch candidate in sketches)
        {
            if (candidate == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Sketch inventory contains null while resolving a projection source owner");
            if (!candidate.IsSketchGeometry(curve)) continue;
            if (ownerId != null)
                throw new M2ModelException("UNSUPPORTED_MODEL", "projection source Curve appears in more than one native Sketch");
            ownerId = RequiredJournalId(candidate, "source Sketch");
            if (candidate.Feature != null)
            {
                string featureId = RequiredJournalId(candidate.Feature, "source Sketch feature");
                if (!sourceFeatureParentIds.Contains(featureId)) sourceFeatureParentIds.Add(featureId);
            }
        }
        sourceFeatureParentIds.Sort(StringComparer.Ordinal);
        return ownerId;
    }

    private static Dictionary<string, object> M3SketchProjectionSourceRecords(M3SketchProjectionSourceState[] sources)
    {
        List<object> records = new List<object>();
        if (sources != null)
            foreach (M3SketchProjectionSourceState source in sources)
            {
                Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
                record.Add("journal_id", source.Id);
                record.Add("tag", source.Tag);
                record.Add("object_type", source.ObjectType);
                record.Add("geometry_type", source.GeometryType);
                record.Add("body_id", source.BodyId);
                record.Add("source_sketch_id", source.SourceSketchId);
                record.Add("source_feature_parent_ids", M3SketchProjectionStringObjects(source.SourceFeatureParentIds));
                record.Add("start_world_mm", M3SketchProjectionPoint(source.Start));
                record.Add("end_world_mm", M3SketchProjectionPoint(source.End));
                record.Add("length_mm", source.Length);
                if (source.GeometryType == "Arc")
                {
                    record.Add("center_world_mm", M3SketchProjectionPoint(source.Center));
                    record.Add("radius_mm", source.Radius);
                    record.Add("axis_x_world", M3SketchProjectionVector(source.AxisX));
                    record.Add("axis_y_world", M3SketchProjectionVector(source.AxisY));
                    record.Add("plane_normal_world", M3SketchProjectionVector(source.PlaneNormal));
                    record.Add("start_angle_rad", source.StartAngle);
                    record.Add("end_angle_rad", source.EndAngle);
                    record.Add("directed_sweep_rad", source.DirectedSweep);
                    record.Add("sweep_rad", source.ExpectedSweep);
                    record.Add("periodic", source.IsPeriodic);
                }
                record.Add("expected_projected_start_world_mm", M3SketchProjectionPoint(source.ExpectedStart));
                record.Add("expected_projected_end_world_mm", M3SketchProjectionPoint(source.ExpectedEnd));
                if (source.GeometryType == "Arc")
                {
                    record.Add("expected_projected_center_world_mm", M3SketchProjectionPoint(source.ExpectedCenter));
                    record.Add("expected_projected_center_target_local_mm", M3SketchProjectionLocalPoint(source.ExpectedCenter,
                        source.TargetOrigin, source.TargetXAxis, source.TargetYAxis, source.TargetNormal));
                }
                records.Add(record);
            }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "count", records.Count },
            { "sources", records }
        };
    }

    private static string M3SketchProjectionSourceSnapshot(M3SketchProjectionSourceState[] sources)
    {
        Dictionary<string, object> records = M3SketchProjectionSourceRecords(sources);
        List<object> sourceRecords = records["sources"] as List<object>;
        if (sourceRecords == null)
            throw new M2ModelException("MODEL_READ_FAILED", "projection source snapshot is malformed");
        foreach (object value in sourceRecords)
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            if (record == null)
                throw new M2ModelException("MODEL_READ_FAILED", "projection source snapshot contains an invalid record");
            // NX can renumber JournalIdentifiers when an embedded projection is inserted.
            // Tag plus numeric geometry and Work Part ownership are the transaction identity.
            record.Remove("journal_id");
            record.Remove("body_id");
            record.Remove("source_sketch_id");
            record.Remove("source_feature_parent_ids");
        }
        return M2Json.CanonicalText(records);
    }

    private static List<object> M3SketchProjectionExpectedOutputRecords(M3SketchProjectionSourceState[] sources)
    {
        List<object> result = new List<object>();
        if (sources == null) return result;
        foreach (M3SketchProjectionSourceState source in sources)
        {
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("source_id", source.Id);
            item.Add("source_tag", source.Tag);
            item.Add("output_type", source.GeometryType);
            item.Add("start_world_mm", M3SketchProjectionPoint(source.ExpectedStart));
            item.Add("end_world_mm", M3SketchProjectionPoint(source.ExpectedEnd));
            if (source.GeometryType == "Arc")
            {
                item.Add("center_world_mm", M3SketchProjectionPoint(source.ExpectedCenter));
                item.Add("radius_mm", source.Radius);
                item.Add("sweep_rad", source.ExpectedSweep);
                item.Add("directed_sweep_rad", source.DirectedSweep);
                item.Add("periodic", source.IsPeriodic);
                item.Add("axis_x_target_local", M3SketchProjectionVector(new Vector3d(
                    M3SketchProjectionDot(source.AxisX, new Vector3d(1.0, 0.0, 0.0)),
                    M3SketchProjectionDot(source.AxisX, new Vector3d(0.0, 1.0, 0.0)),
                    M3SketchProjectionDot(source.AxisX, new Vector3d(0.0, 0.0, 1.0)))));
            }
            result.Add(item);
        }
        return result;
    }

    private static NXObject[] ProjectionSourceObjects(M3SketchProjectionSourceState[] sources)
    {
        NXObject[] result = new NXObject[sources == null ? 0 : sources.Length];
        for (int i = 0; i < result.Length; i++) result[i] = sources[i].Source;
        return result;
    }

    private static bool M3SketchProjectionArcIsPeriodic(double radius, double startAngle, double endAngle, double length)
    {
        if (!IsFinite(radius) || radius <= 0.0 || !IsFinite(startAngle) || !IsFinite(endAngle)) return false;
        double sweep = Math.Abs(endAngle - startAngle);
        return Math.Abs(sweep - 2.0 * Math.PI) <= M3SketchProjectionAngleTolerance ||
            (IsFinite(length) && Math.Abs(length - 2.0 * Math.PI * radius) <= M3SketchProjectionPositionToleranceMm);
    }

    private static Point3d M3SketchProjectionArcPoint(Point3d center, Vector3d axisX,
        Vector3d axisY, double radius, double angle)
    {
        return new Point3d(center.X + radius * (axisX.X * Math.Cos(angle) + axisY.X * Math.Sin(angle)),
            center.Y + radius * (axisX.Y * Math.Cos(angle) + axisY.Y * Math.Sin(angle)),
            center.Z + radius * (axisX.Z * Math.Cos(angle) + axisY.Z * Math.Sin(angle)));
    }

    private static Point3d M3SketchProjectionProjectPoint(Point3d point, Point3d origin,
        Vector3d normal, Vector3d direction)
    {
        Vector3d fromPointToOrigin = new Vector3d(origin.X - point.X, origin.Y - point.Y, origin.Z - point.Z);
        double denominator = M3SketchProjectionDot(direction, normal);
        if (!IsFinite(denominator) || Math.Abs(denominator) <= M3SketchProjectionAngleTolerance)
            throw new M2ModelException("UNSUPPORTED_DIRECTION", "projection direction is parallel to the target Sketch plane");
        double distance = M3SketchProjectionDot(fromPointToOrigin, normal) / denominator;
        Point3d projected = new Point3d(point.X + direction.X * distance,
            point.Y + direction.Y * distance, point.Z + direction.Z * distance);
        if (!M3SketchProjectionFinite(projected))
            throw new M2ModelException("UNSUPPORTED_SOURCE", "projection produced a non-finite target point");
        return projected;
    }

    private static Vector3d M3SketchProjectionProjectVector(Vector3d vector, Vector3d normal)
    {
        double component = M3SketchProjectionDot(vector, normal);
        return new Vector3d(vector.X - normal.X * component,
            vector.Y - normal.Y * component, vector.Z - normal.Z * component);
    }

    private static Dictionary<string, NXObject> M3SketchProjectionGeometryByTag(Sketch sketch)
    {
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null during projection readback");
        Dictionary<string, NXObject> result = new Dictionary<string, NXObject>(StringComparer.Ordinal);
        foreach (NXObject item in geometry)
        {
            if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Sketch geometry contains null during projection readback");
            string tag = item.Tag.ToString();
            if (String.IsNullOrEmpty(tag) || result.ContainsKey(tag))
                throw new M2ModelException("MODEL_READ_FAILED", "Sketch geometry contains an empty or duplicate Tag during projection readback");
            result.Add(tag, item);
        }
        return result;
    }

    private static string[] M3SketchProjectionGeometryTags(Sketch sketch)
    {
        Dictionary<string, NXObject> geometry = M3SketchProjectionGeometryByTag(sketch);
        List<string> tags = new List<string>(geometry.Keys);
        tags.Sort(StringComparer.Ordinal);
        return tags.ToArray();
    }

    private static List<Curve> M3SketchProjectionGeometryDeltaCurves(M3SketchProjectionPlanState plan,
        Dictionary<string, NXObject> current, Curve[] factoryOutputs, out string[] outputTags,
        out Dictionary<string, object> evidence)
    {
        List<string> added = new List<string>();
        List<object> unsupported = new List<object>();
        List<Curve> outputs = new List<Curve>();
        foreach (KeyValuePair<string, NXObject> item in current)
        {
            if (Array.IndexOf(plan.SketchGeometryTagsBefore, item.Key) >= 0) continue;
            Curve curve = item.Value as Curve;
            if (curve == null || (!(curve is Line) && !(curve is Arc)))
            {
                unsupported.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", item.Key }, { "object_type", item.Value.GetType().Name }
                });
                continue;
            }
            added.Add(item.Key);
            outputs.Add(curve);
        }
        added.Sort(StringComparer.Ordinal);
        outputs.Sort(delegate(Curve left, Curve right) { return String.CompareOrdinal(left.Tag.ToString(), right.Tag.ToString()); });
        string[] factoryTags = M3SketchProjectionObjectTagStrings(factoryOutputs);
        bool factoryMatches = M3SketchProjectionStringArraysEqual(added.ToArray(), factoryTags);
        outputTags = added.ToArray();
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "source_count", plan.Sources.Length },
            { "new_target_curve_count", outputs.Count },
            { "new_target_curve_tags", M3SketchProjectionStringObjects(outputTags) },
            { "factory_output_tags", M3SketchProjectionStringObjects(factoryTags) },
            { "factory_output_tags_match", factoryMatches },
            { "unsupported_new_objects", unsupported },
            { "output_count_matches_source_count", outputs.Count == plan.Sources.Length && unsupported.Count == 0 }
        };
        if (!factoryMatches || outputs.Count != plan.Sources.Length || unsupported.Count != 0)
            throw new M2ModelException("PROJECTION_OUTPUT_INVALID", "new target Sketch geometry must contain exactly one supported output per selected source");
        return outputs;
    }

    private static bool M3SketchProjectionOutputsMatch(M3SketchProjectionPlanState plan,
        List<Curve> outputs, out Dictionary<string, string> outputBySource, out Dictionary<string, object> evidence)
    {
        outputBySource = new Dictionary<string, string>(StringComparer.Ordinal);
        List<object> matches = new List<object>();
        HashSet<string> usedOutputTags = new HashSet<string>(StringComparer.Ordinal);
        bool allMatch = outputs != null && outputs.Count == plan.Sources.Length;
        if (outputs == null) outputs = new List<Curve>();
        foreach (M3SketchProjectionSourceState source in plan.Sources)
        {
            List<Curve> candidates = new List<Curve>();
            foreach (Curve output in outputs)
            {
                Dictionary<string, object> ignored;
                if (output != null && !usedOutputTags.Contains(output.Tag.ToString()) &&
                    String.Equals(output.GetType().Name, source.GeometryType, StringComparison.Ordinal) &&
                    M3SketchProjectionOutputGeometryMatches(source, output, out ignored)) candidates.Add(output);
            }
            bool unique = candidates.Count == 1;
            if (unique)
            {
                Curve output = candidates[0];
                usedOutputTags.Add(output.Tag.ToString());
                outputBySource[source.Id] = RequiredJournalId(output, "projection output curve");
            }
            else allMatch = false;
            matches.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "source_id", source.Id },
                { "candidate_count", candidates.Count },
                { "uniquely_matched", unique },
                { "output_id", unique ? (object)RequiredJournalId(candidates[0], "projection output curve") : null },
                { "output_tag", unique ? (object)candidates[0].Tag.ToString() : null }
            });
        }
        bool everyOutputUsed = usedOutputTags.Count == outputs.Count;
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "source_output_matches", matches },
            { "unique_source_to_output_mapping", allMatch },
            { "every_output_used_once", everyOutputUsed }
        };
        return allMatch && everyOutputUsed;
    }

    private static bool M3SketchProjectionOutputGeometryMatches(M3SketchProjectionSourceState source,
        Curve output, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (source.GeometryType == "Line")
        {
            Line line = output as Line;
            if (line == null) return false;
            double forward = Math.Max(M3SketchProjectionDistance(line.StartPoint, source.ExpectedStart),
                M3SketchProjectionDistance(line.EndPoint, source.ExpectedEnd));
            double reverse = Math.Max(M3SketchProjectionDistance(line.StartPoint, source.ExpectedEnd),
                M3SketchProjectionDistance(line.EndPoint, source.ExpectedStart));
            double residual = Math.Min(forward, reverse);
            evidence["output_type"] = "Line";
            evidence["forward_endpoint_residual_mm"] = forward;
            evidence["reverse_endpoint_residual_mm"] = reverse;
            evidence["endpoint_residual_mm"] = residual;
            evidence["output_length_mm"] = line.GetLength();
            evidence["expected_length_mm"] = source.Length;
            return IsFinite(residual) && residual <= M3SketchProjectionPositionToleranceMm &&
                IsFinite(line.GetLength()) && Math.Abs(line.GetLength() - M3SketchProjectionDistance(source.ExpectedStart, source.ExpectedEnd)) <= M3SketchProjectionPositionToleranceMm;
        }

        Arc arc = output as Arc;
        if (arc == null) return false;
        Point3d center;
        Vector3d axisX, axisY;
        arc.GetOrientation(out center, out axisX, out axisY);
        Vector3d normal = M3SketchProjectionCross(axisX, axisY);
        M3SketchProjectionNormalize(ref normal, "output Arc plane normal");
        double centerResidual = M3SketchProjectionDistance(center, source.ExpectedCenter);
        double radiusResidual = Math.Abs(arc.Radius - source.Radius);
        double planeAlignment = Math.Abs(M3SketchProjectionDot(normal, source.PlaneNormal));
        double outputSweep = arc.Radius <= M3SketchProjectionPositionToleranceMm
            ? Double.NaN : arc.GetLength() / arc.Radius;
        double sweepResidual = Math.Abs(outputSweep - source.ExpectedSweep);
        double forwardTraceResidual;
        double reverseTraceResidual;
        double arcTraceResidual = M3SketchProjectionArcTraceResidual(source.ExpectedCenter,
            source.AxisX, source.AxisY, source.Radius, source.StartAngle, source.DirectedSweep,
            source.IsPeriodic, arc, out forwardTraceResidual, out reverseTraceResidual);
        bool outputPeriodic = M3SketchProjectionArcIsPeriodic(arc.Radius, arc.StartAngle, arc.EndAngle, arc.GetLength());
        Point3d outputStart = M3SketchProjectionArcPoint(center, axisX, axisY, arc.Radius, arc.StartAngle);
        Point3d outputEnd = M3SketchProjectionArcPoint(center, axisX, axisY, arc.Radius, arc.EndAngle);
        double endpointForward = Math.Max(M3SketchProjectionDistance(outputStart, source.ExpectedStart),
            M3SketchProjectionDistance(outputEnd, source.ExpectedEnd));
        double endpointReverse = Math.Max(M3SketchProjectionDistance(outputStart, source.ExpectedEnd),
            M3SketchProjectionDistance(outputEnd, source.ExpectedStart));
        double endpointResidual = source.IsPeriodic ? 0.0 : Math.Min(endpointForward, endpointReverse);
        evidence["output_type"] = "Arc";
        evidence["center_residual_mm"] = centerResidual;
        evidence["radius_residual_mm"] = radiusResidual;
        evidence["plane_normal_alignment"] = planeAlignment;
        evidence["source_sweep_rad"] = source.ExpectedSweep;
        evidence["output_sweep_rad"] = outputSweep;
        evidence["sweep_residual_rad"] = sweepResidual;
        evidence["arc_trace_residual_mm"] = arcTraceResidual;
        evidence["arc_trace_forward_residual_mm"] = forwardTraceResidual;
        evidence["arc_trace_reverse_residual_mm"] = reverseTraceResidual;
        evidence["source_periodic"] = source.IsPeriodic;
        evidence["output_periodic"] = outputPeriodic;
        evidence["endpoint_residual_mm"] = endpointResidual;
        evidence["forward_endpoint_residual_mm"] = endpointForward;
        evidence["reverse_endpoint_residual_mm"] = endpointReverse;
        return IsFinite(centerResidual) && centerResidual <= M3SketchProjectionPositionToleranceMm &&
            IsFinite(radiusResidual) && radiusResidual <= M3SketchProjectionPositionToleranceMm &&
            IsFinite(planeAlignment) && Math.Abs(1.0 - planeAlignment) <= M3SketchProjectionAngleTolerance &&
            IsFinite(sweepResidual) && sweepResidual <= M3SketchProjectionAngleTolerance &&
            IsFinite(arcTraceResidual) && arcTraceResidual <= M3SketchProjectionPositionToleranceMm &&
            source.IsPeriodic == outputPeriodic &&
            (source.IsPeriodic || (IsFinite(endpointResidual) && endpointResidual <= M3SketchProjectionPositionToleranceMm));
    }

    private static bool M3SketchProjectionOriginalSourceGeometryMatches(M3SketchProjectionSourceState source,
        Curve candidate, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (source.GeometryType == "Line")
        {
            Line line = candidate as Line;
            if (line == null) return false;
            double forward = Math.Max(M3SketchProjectionDistance(line.StartPoint, source.Start),
                M3SketchProjectionDistance(line.EndPoint, source.End));
            double reverse = Math.Max(M3SketchProjectionDistance(line.StartPoint, source.End),
                M3SketchProjectionDistance(line.EndPoint, source.Start));
            double lineEndpointResidual = Math.Min(forward, reverse);
            double lineLengthResidual = Math.Abs(line.GetLength() - source.Length);
            evidence["source_geometry_type"] = "Line";
            evidence["endpoint_residual_mm"] = lineEndpointResidual;
            evidence["length_residual_mm"] = lineLengthResidual;
            return IsFinite(lineEndpointResidual) && lineEndpointResidual <= M3SketchProjectionPositionToleranceMm &&
                IsFinite(lineLengthResidual) && lineLengthResidual <= M3SketchProjectionPositionToleranceMm;
        }

        Arc arc = candidate as Arc;
        if (arc == null) return false;
        Point3d center;
        Vector3d axisX, axisY;
        arc.GetOrientation(out center, out axisX, out axisY);
        Vector3d normal = M3SketchProjectionCross(axisX, axisY);
        M3SketchProjectionNormalize(ref normal, "Section source output Arc normal");
        double centerResidual = M3SketchProjectionDistance(center, source.Center);
        double radiusResidual = Math.Abs(arc.Radius - source.Radius);
        double planeAlignment = Math.Abs(M3SketchProjectionDot(normal, source.PlaneNormal));
        double sweep = arc.Radius <= M3SketchProjectionPositionToleranceMm
            ? Double.NaN : arc.GetLength() / arc.Radius;
        double sweepResidual = Math.Abs(sweep - source.ExpectedSweep);
        Point3d start = M3SketchProjectionArcPoint(center, axisX, axisY, arc.Radius, arc.StartAngle);
        Point3d end = M3SketchProjectionArcPoint(center, axisX, axisY, arc.Radius, arc.EndAngle);
        double endpointForward = Math.Max(M3SketchProjectionDistance(start, source.Start), M3SketchProjectionDistance(end, source.End));
        double endpointReverse = Math.Max(M3SketchProjectionDistance(start, source.End), M3SketchProjectionDistance(end, source.Start));
        double endpointResidual = source.IsPeriodic ? 0.0 : Math.Min(endpointForward, endpointReverse);
        bool periodic = M3SketchProjectionArcIsPeriodic(arc.Radius, arc.StartAngle, arc.EndAngle, arc.GetLength());
        double traceForward;
        double traceReverse;
        double traceResidual = M3SketchProjectionArcTraceResidual(source.Center, source.AxisX,
            source.AxisY, source.Radius, source.StartAngle, source.DirectedSweep, source.IsPeriodic,
            arc, out traceForward, out traceReverse);
        evidence["source_geometry_type"] = "Arc";
        evidence["center_residual_mm"] = centerResidual;
        evidence["radius_residual_mm"] = radiusResidual;
        evidence["plane_normal_alignment"] = planeAlignment;
        evidence["sweep_residual_rad"] = sweepResidual;
        evidence["endpoint_residual_mm"] = endpointResidual;
        evidence["arc_trace_residual_mm"] = traceResidual;
        evidence["arc_trace_forward_residual_mm"] = traceForward;
        evidence["arc_trace_reverse_residual_mm"] = traceReverse;
        evidence["source_periodic"] = source.IsPeriodic;
        evidence["output_periodic"] = periodic;
        return IsFinite(centerResidual) && centerResidual <= M3SketchProjectionPositionToleranceMm &&
            IsFinite(radiusResidual) && radiusResidual <= M3SketchProjectionPositionToleranceMm &&
            IsFinite(planeAlignment) && Math.Abs(1.0 - planeAlignment) <= M3SketchProjectionAngleTolerance &&
            IsFinite(sweepResidual) && sweepResidual <= M3SketchProjectionAngleTolerance &&
            IsFinite(traceResidual) && traceResidual <= M3SketchProjectionPositionToleranceMm &&
            source.IsPeriodic == periodic &&
            (source.IsPeriodic || (IsFinite(endpointResidual) && endpointResidual <= M3SketchProjectionPositionToleranceMm));
    }

    private static double M3SketchProjectionArcTraceResidual(Point3d expectedCenter,
        Vector3d expectedAxisX, Vector3d expectedAxisY, double expectedRadius,
        double expectedStartAngle, double expectedDirectedSweep, bool periodic, Arc output,
        out double forwardResidual, out double reverseResidual)
    {
        forwardResidual = periodic ? 0.0 : Double.PositiveInfinity;
        reverseResidual = periodic ? 0.0 : Double.PositiveInfinity;
        if (periodic) return 0.0;
        if (output == null || !M3SketchProjectionFinite(expectedCenter) ||
            !M3SketchProjectionFinite(expectedAxisX) || !M3SketchProjectionFinite(expectedAxisY) ||
            !IsFinite(expectedRadius) || expectedRadius <= 0.0 ||
            !IsFinite(expectedStartAngle) || !IsFinite(expectedDirectedSweep)) return Double.PositiveInfinity;

        Point3d outputCenter;
        Vector3d outputAxisX, outputAxisY;
        output.GetOrientation(out outputCenter, out outputAxisX, out outputAxisY);
        double outputSweep = output.EndAngle - output.StartAngle;
        if (!M3SketchProjectionFinite(outputCenter) || !M3SketchProjectionFinite(outputAxisX) ||
            !M3SketchProjectionFinite(outputAxisY) || !IsFinite(outputSweep)) return Double.PositiveInfinity;

        double[] fractions = new double[] { 0.25, 0.5, 0.75 };
        forwardResidual = 0.0;
        reverseResidual = 0.0;
        foreach (double fraction in fractions)
        {
            double expectedAngle = expectedStartAngle + expectedDirectedSweep * fraction;
            Point3d expected = M3SketchProjectionArcPoint(expectedCenter, expectedAxisX,
                expectedAxisY, expectedRadius, expectedAngle);
            Point3d forward = M3SketchProjectionArcPoint(outputCenter, outputAxisX,
                outputAxisY, output.Radius, output.StartAngle + outputSweep * fraction);
            Point3d reverse = M3SketchProjectionArcPoint(outputCenter, outputAxisX,
                outputAxisY, output.Radius, output.StartAngle + outputSweep * (1.0 - fraction));
            double forwardDistance = M3SketchProjectionDistance(expected, forward);
            double reverseDistance = M3SketchProjectionDistance(expected, reverse);
            if (!IsFinite(forwardDistance) || !IsFinite(reverseDistance)) return Double.PositiveInfinity;
            if (forwardDistance > forwardResidual) forwardResidual = forwardDistance;
            if (reverseDistance > reverseResidual) reverseResidual = reverseDistance;
        }
        return Math.Min(forwardResidual, reverseResidual);
    }

    private static Dictionary<string, object> M3SketchProjectionRelationEvidence(Part part,
        M3SketchProjectionPlanState plan, Dictionary<string, M3SketchProjectionFeatureState> features,
        List<Curve> outputs)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> statuses = new List<object>();
        HashSet<string> outputTags = new HashSet<string>(StringComparer.Ordinal);
        UFSession uf = UFSession.GetUFSession();
        bool referenceStatusesMatch = outputs != null && outputs.Count == plan.Sources.Length;
        // The native ProjectCurve embed carries associativity through CPROJ while
        // sketch curves remain UF_SKET reference-status Active in both modes.
        int expectedStatus = 1;
        if (outputs != null)
            foreach (Curve curve in outputs)
            {
                if (curve == null) { referenceStatusesMatch = false; continue; }
                string tag = curve.Tag.ToString();
                if (!outputTags.Add(tag)) referenceStatusesMatch = false;
                UFSket.ReferenceStatus status;
                bool read = true;
                try { uf.Sket.AskReferenceStatus(plan.Sketch.Tag, curve.Tag, out status); }
                catch (Exception ex)
                {
                    status = UFSket.ReferenceStatus.Active;
                    read = false;
                    result["reference_status_read_exception_type"] = ex.GetType().FullName;
                }
                int numeric = Convert.ToInt32(status, CultureInfo.InvariantCulture);
                bool match = read && numeric == expectedStatus;
                if (!match) referenceStatusesMatch = false;
                statuses.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "curve_id", SafeM3SketchProjectionJournalId(curve) },
                    { "tag", tag },
                    { "status", numeric },
                    { "status_name", status.ToString() },
                    { "read_succeeded", read },
                    { "expected_status", expectedStatus },
                    { "matches_associativity", match }
                });
            }
        List<M3SketchProjectionFeatureState> delta = M3SketchProjectionNewFeatures(plan.FeatureStatesBefore, features);
        List<object> featureRecords = new List<object>();
        M3SketchProjectionFeatureState projectionFeature = null;
        foreach (M3SketchProjectionFeatureState feature in delta)
        {
            featureRecords.Add(M3SketchProjectionFeatureRecord(feature));
            if (String.Equals(feature.Type, "CPROJ", StringComparison.OrdinalIgnoreCase)) projectionFeature = feature;
        }
        bool featureDeltaValid = plan.Request.Associative
            ? delta.Count == 1 && projectionFeature != null
            : delta.Count == 0;
        List<object> parentIds = new List<object>();
        bool sourceParentObserved = false;
        if (projectionFeature != null)
        {
            foreach (string id in projectionFeature.ParentIds) parentIds.Add(id);
            HashSet<string> expectedParents = new HashSet<string>(StringComparer.Ordinal);
            foreach (M3SketchProjectionSourceState source in plan.Sources)
                foreach (string id in source.SourceFeatureParentIds) expectedParents.Add(id);
            if (expectedParents.Count == 0) sourceParentObserved = projectionFeature.ParentIds.Count > 0;
            else
                foreach (string parentId in projectionFeature.ParentIds)
                    if (expectedParents.Contains(parentId)) sourceParentObserved = true;
        }
        // NX can list only a support datum as a CPROJ feature parent. The Section readback
        // below proves the exact source entities, so do not infer source identity from parents.
        bool parentEvidenceValid = !plan.Request.Associative ||
            (projectionFeature != null && projectionFeature.ParentIds.Count > 0);

        Dictionary<string, object> sectionEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bool sectionMatches = !plan.Request.Associative;
        if (plan.Request.Associative && projectionFeature != null)
        {
            try
            {
                sectionEvidence = M3SketchProjectionReadCommittedSectionSources(part,
                    projectionFeature.Feature, plan.Sources);
                object associativeValue;
                object dumbFixedValue;
                bool associativeRead = sectionEvidence.TryGetValue("associativity", out associativeValue) && associativeValue is bool;
                bool dumbFixedRead = sectionEvidence.TryGetValue("project_as_dumb_fixed_curves", out dumbFixedValue) && dumbFixedValue is bool;
                bool builderOptionsMatch = associativeRead && dumbFixedRead &&
                    (bool)associativeValue == plan.Request.Associative && !(bool)dumbFixedValue;
                sectionEvidence["builder_options_match_request"] = builderOptionsMatch;
                List<string> actualSourceTags = sectionEvidence["source_tags"] as List<string>;
                List<string> expectedSourceTags = new List<string>();
                foreach (M3SketchProjectionSourceState source in plan.Sources) expectedSourceTags.Add(source.Tag);
                expectedSourceTags.Sort(StringComparer.Ordinal);
                sectionMatches = actualSourceTags != null && M3SketchProjectionStringListsEqual(expectedSourceTags, actualSourceTags);
                sectionEvidence["expected_source_tags"] = M3SketchProjectionStringObjects(expectedSourceTags);
                sectionEvidence["source_set_matches_request"] = sectionMatches;
                object sectionOutputCount;
                sectionEvidence.TryGetValue("section_output_curve_count", out sectionOutputCount);
                bool outputCountMatches = Convert.ToInt32(sectionOutputCount ?? 0, CultureInfo.InvariantCulture) == plan.Sources.Length;
                sectionEvidence["section_output_count_matches_source_count"] = outputCountMatches;
                bool outputGeometryMatches = Boolean.Equals(
                    sectionEvidence.ContainsKey("section_output_geometry_matches_source_geometries")
                        ? sectionEvidence["section_output_geometry_matches_source_geometries"] : null, true);
                sectionEvidence["section_output_geometry_matches_source_geometries_required"] = true;
                sectionMatches = sectionMatches && outputCountMatches && builderOptionsMatch && outputGeometryMatches;
            }
            catch (Exception ex)
            {
                sectionEvidence["readback_exception_type"] = ex.GetType().FullName;
                sectionEvidence["readback_exception_message"] = M3SketchProjectionSafeMessage(ex);
                sectionEvidence["source_set_matches_request"] = false;
                sectionMatches = false;
            }
        }
        else
        {
            sectionEvidence["applicable"] = false;
            sectionEvidence["reason"] = "non-associative projection has no committed CPROJ source Section";
            sectionEvidence["section_output_curve_tags"] = new List<string>();
            sectionEvidence["section_output_curve_count"] = 0;
        }

        bool valid = referenceStatusesMatch && featureDeltaValid && parentEvidenceValid && sectionMatches;
        result["valid"] = valid;
        result["associative_requested"] = plan.Request.Associative;
        result["output_reference_status"] = statuses;
        result["expected_reference_status"] = expectedStatus;
        result["reference_status_values"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "reference", 0 }, { "active", 1 }
        };
        result["native_relation_semantics"] = "output curves remain Active in both modes; associative source dependence is carried by the CPROJ Section";
        result["reference_statuses_match"] = referenceStatusesMatch;
        result["new_feature_count"] = delta.Count;
        result["new_feature_records"] = featureRecords;
        result["feature_delta_matches_associativity"] = featureDeltaValid;
        result["projection_feature_id"] = projectionFeature == null ? null : projectionFeature.Id;
        result["projection_feature_tag"] = projectionFeature == null ? null : projectionFeature.Tag;
        result["projection_feature_type"] = projectionFeature == null ? null : projectionFeature.Type;
        result["projection_feature_parent_journal_ids"] = parentIds;
        result["source_feature_parent_observed"] = sourceParentObserved;
        result["parent_relation_matches"] = parentEvidenceValid;
        result["committed_section_source_readback"] = sectionEvidence;
        result["committed_section_source_set_matches"] = sectionMatches;
        return result;
    }

    private static Dictionary<string, object> M3SketchProjectionReadCommittedSectionSources(Part part,
        Feature projectFeature, M3SketchProjectionSourceState[] sources)
    {
        if (part == null || projectFeature == null || sources == null)
            throw new M2ModelException("PROJECTION_RELATION_UNVERIFIED", "associative projection feature is unavailable for Section readback");
        SketchProjectBuilder builder = null;
        List<SectionData> sectionData = new List<SectionData>();
        List<SectionElementData> elementData = new List<SectionElementData>();
        Exception operationException = null;
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            builder = part.Sketches.CreateProjectBuilder(projectFeature);
            if (builder == null || builder.Section == null)
                throw new M2ModelException("PROJECTION_RELATION_UNVERIFIED", "NX returned no editing builder or source Section for committed ProjectCurve feature");
            result["associativity"] = builder.Associativity;
            result["project_as_dumb_fixed_curves"] = builder.ProjectAsDumbFixedCurves;
            SectionData[] sections;
            builder.Section.GetSectionData(out sections);
            if (sections == null) throw new M2ModelException("PROJECTION_RELATION_UNVERIFIED", "committed ProjectCurve Section data returned null");
            NXObject[] sectionOutputs;
            builder.Section.GetOutputCurves(out sectionOutputs);
            if (sectionOutputs == null) throw new M2ModelException("PROJECTION_RELATION_UNVERIFIED", "committed ProjectCurve Section outputs returned null");
            List<string> sectionOutputTags = new List<string>();
            List<object> sectionOutputGeometry = new List<object>();
            List<int> unmatchedSourceIndices = new List<int>();
            for (int i = 0; i < sources.Length; i++) unmatchedSourceIndices.Add(i);
            bool sectionOutputGeometryMatchesSources = sectionOutputs.Length == sources.Length;
            foreach (NXObject sectionOutput in sectionOutputs)
            {
                if (sectionOutput == null) throw new M2ModelException("PROJECTION_RELATION_UNVERIFIED", "committed ProjectCurve Section output contains null");
                string outputTag = sectionOutput.Tag.ToString();
                sectionOutputTags.Add(outputTag);
                Curve outputCurve = sectionOutput as Curve;
                int matchedSourceIndex = -1;
                Dictionary<string, object> matchedGeometry = null;
                if (outputCurve != null)
                    for (int i = 0; i < unmatchedSourceIndices.Count; i++)
                    {
                        int sourceIndex = unmatchedSourceIndices[i];
                        Dictionary<string, object> candidateGeometry;
                        if (!M3SketchProjectionOriginalSourceGeometryMatches(sources[sourceIndex],
                            outputCurve, out candidateGeometry)) continue;
                        matchedSourceIndex = sourceIndex;
                        matchedGeometry = candidateGeometry;
                        unmatchedSourceIndices.RemoveAt(i);
                        break;
                    }
                bool matchedSource = matchedSourceIndex >= 0;
                if (!matchedSource) sectionOutputGeometryMatchesSources = false;
                sectionOutputGeometry.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", outputTag },
                    { "curve_id", outputCurve == null ? null : SafeM3SketchProjectionJournalId(outputCurve) },
                    { "curve_type", outputCurve == null ? sectionOutput.GetType().Name : outputCurve.GetType().Name },
                    { "matches_source_geometry", matchedSource },
                    { "source_tag", matchedSource ? (object)sources[matchedSourceIndex].Tag : null },
                    { "geometry_match", matchedGeometry }
                });
            }
            sectionOutputTags.Sort(StringComparer.Ordinal);
            List<string> sourceTags = new List<string>();
            List<object> sourceRecords = new List<object>();
            foreach (SectionData section in sections)
            {
                if (section == null) throw new M2ModelException("PROJECTION_RELATION_UNVERIFIED", "committed ProjectCurve Section contains null SectionData");
                sectionData.Add(section);
                SectionElementData[] elements;
                section.GetSectionElementsData(out elements);
                if (elements == null) throw new M2ModelException("PROJECTION_RELATION_UNVERIFIED", "committed ProjectCurve SectionData returned null elements");
                foreach (SectionElementData elementDataItem in elements)
                {
                    if (elementDataItem == null) throw new M2ModelException("PROJECTION_RELATION_UNVERIFIED", "committed ProjectCurve Section contains null SectionElementData");
                    elementData.Add(elementDataItem);
                    DisplayableObject element, startConnector, endConnector;
                    Point3d startPoint, endPoint;
                    elementDataItem.GetSectionElementData1(out element, out startConnector,
                        out startPoint, out endConnector, out endPoint);
                    NXObject source = element as NXObject;
                    if (source == null) throw new M2ModelException("PROJECTION_RELATION_UNVERIFIED", "committed ProjectCurve Section element is not a native NXObject");
                    string tag = source.Tag.ToString();
                    string id = RequiredJournalId(source, "committed projection Section source");
                    sourceTags.Add(tag);
                    sourceRecords.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "source_tag", tag }, { "source_id", id }, { "source_type", source.GetType().Name },
                        { "start_connector_tag", startConnector == null ? null : startConnector.Tag.ToString() },
                        { "end_connector_tag", endConnector == null ? null : endConnector.Tag.ToString() },
                        { "start_point_mm", M3SketchProjectionPoint(startPoint) },
                        { "end_point_mm", M3SketchProjectionPoint(endPoint) }
                    });
                }
            }
            sourceTags.Sort(StringComparer.Ordinal);
            result["applicable"] = true;
            result["section_count"] = sections.Length;
            result["source_element_count"] = sourceTags.Count;
            result["source_tags"] = sourceTags;
            result["source_elements"] = sourceRecords;
            result["section_output_curve_count"] = sectionOutputTags.Count;
            result["section_output_curve_tags"] = sectionOutputTags;
            result["section_output_geometry"] = sectionOutputGeometry;
            result["section_output_geometry_matches_source_geometries"] = sectionOutputGeometryMatchesSources && unmatchedSourceIndices.Count == 0;
        }
        catch (Exception ex)
        {
            operationException = ex;
        }

        Exception cleanupException = null;
        for (int i = elementData.Count - 1; i >= 0; i--)
        {
            try { elementData[i].Dispose(); }
            catch (Exception ex) { if (cleanupException == null) cleanupException = ex; }
        }
        for (int i = sectionData.Count - 1; i >= 0; i--)
        {
            try { sectionData[i].Dispose(); }
            catch (Exception ex) { if (cleanupException == null) cleanupException = ex; }
        }
        if (builder != null)
        {
            try { builder.Destroy(); }
            catch (Exception ex) { if (cleanupException == null) cleanupException = ex; }
        }
        if (operationException != null) throw operationException;
        if (cleanupException != null)
            throw new M2ModelException("PROJECTION_RELATION_UNVERIFIED",
                "NX Section readback transients or projection edit builder could not be destroyed (" + cleanupException.GetType().Name + ")");
        return result;
    }

    private static bool M3SketchProjectionPartHelperInventoryMatches(M3SketchProjectionPlanState plan,
        Part part, Sketch sketch, Dictionary<string, List<string>> current,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (plan == null || part == null || sketch == null || plan.PartHelperTagsBefore == null || current == null)
        {
            evidence["inventory_available"] = false;
            return false;
        }

        List<string> beforePoints;
        List<string> afterPoints;
        List<string> beforeAxes;
        List<string> afterAxes;
        List<string> beforeDirections;
        List<string> afterDirections;
        if (!plan.PartHelperTagsBefore.TryGetValue("points", out beforePoints) ||
            !current.TryGetValue("points", out afterPoints) ||
            !plan.PartHelperTagsBefore.TryGetValue("axes", out beforeAxes) ||
            !current.TryGetValue("axes", out afterAxes) ||
            !plan.PartHelperTagsBefore.TryGetValue("directions", out beforeDirections) ||
            !current.TryGetValue("directions", out afterDirections) ||
            beforePoints == null || afterPoints == null || beforeAxes == null || afterAxes == null ||
            beforeDirections == null || afterDirections == null)
        {
            evidence["inventory_available"] = false;
            return false;
        }

        HashSet<string> beforePointTags = new HashSet<string>(beforePoints, StringComparer.Ordinal);
        HashSet<string> afterPointTags = new HashSet<string>(afterPoints, StringComparer.Ordinal);
        List<string> newPointTags = new List<string>();
        List<string> removedPointTags = new List<string>();
        foreach (string tag in afterPointTags) if (!beforePointTags.Contains(tag)) newPointTags.Add(tag);
        foreach (string tag in beforePointTags) if (!afterPointTags.Contains(tag)) removedPointTags.Add(tag);
        newPointTags.Sort(StringComparer.Ordinal);
        removedPointTags.Sort(StringComparer.Ordinal);

        string originPointTagAfter = null;
        bool originPointMatchesTarget = false;
        try
        {
            Point originPoint = sketch.OriginPoint;
            if (originPoint != null)
            {
                originPointTagAfter = originPoint.Tag.ToString();
                Point3d coordinates = originPoint.Coordinates;
                bool identityMatches = !String.IsNullOrEmpty(originPointTagAfter) &&
                    plan.TargetOriginPointAvailable &&
                    String.Equals(originPointTagAfter, plan.TargetOriginPointTag, StringComparison.Ordinal);
                bool ownershipMatches = !originPoint.IsOccurrence && originPoint.OwningPart != null &&
                    originPoint.OwningPart.Tag == part.Tag && !originPoint.IsReference;
                bool coordinatesMatch = M3SketchProjectionFinite(coordinates) &&
                    M3SketchProjectionDistance(coordinates, plan.SketchOrigin) <= M3SketchProjectionPositionToleranceMm &&
                    M3SketchProjectionDistance(coordinates, plan.TargetOriginPointCoordinates) <= M3SketchProjectionPositionToleranceMm;
                originPointMatchesTarget = identityMatches && ownershipMatches && coordinatesMatch;
            }
            else originPointMatchesTarget = !plan.TargetOriginPointAvailable;
        }
        catch (Exception ex)
        {
            evidence["origin_point_read_exception_type"] = ex.GetType().FullName;
            originPointMatchesTarget = false;
        }

        bool newPointSetMatches = false;
        if (newPointTags.Count == 0) newPointSetMatches = true;
        else if (plan.TargetOriginPointAvailable && newPointTags.Count == 1 &&
            String.Equals(newPointTags[0], plan.TargetOriginPointTag, StringComparison.Ordinal))
            newPointSetMatches = true;
        bool pointsMatch = removedPointTags.Count == 0 && newPointSetMatches && originPointMatchesTarget;
        bool axesMatch = M3SketchProjectionStringListsEqual(beforeAxes, afterAxes);
        bool directionsMatch = M3SketchProjectionStringListsEqual(beforeDirections, afterDirections);

        evidence["inventory_available"] = true;
        evidence["points_before"] = M3SketchProjectionStringObjects(beforePoints);
        evidence["points_after"] = M3SketchProjectionStringObjects(afterPoints);
        evidence["axes_before"] = M3SketchProjectionStringObjects(beforeAxes);
        evidence["axes_after"] = M3SketchProjectionStringObjects(afterAxes);
        evidence["directions_before"] = M3SketchProjectionStringObjects(beforeDirections);
        evidence["directions_after"] = M3SketchProjectionStringObjects(afterDirections);
        evidence["point_tags_added"] = M3SketchProjectionStringObjects(newPointTags);
        evidence["point_tags_removed"] = M3SketchProjectionStringObjects(removedPointTags);
        evidence["expected_target_origin_point_tag"] = plan.TargetOriginPointTag;
        evidence["target_origin_point_tag_after"] = originPointTagAfter;
        evidence["target_origin_point_available_before_activation"] = plan.TargetOriginPointAvailable;
        evidence["target_origin_point_matches_same_part_origin"] = originPointMatchesTarget;
        evidence["points_match"] = pointsMatch;
        evidence["axes_match"] = axesMatch;
        evidence["directions_match"] = directionsMatch;
        return pointsMatch && axesMatch && directionsMatch;
    }

    private static bool M3SketchProjectionReferenceStatesMatch(Dictionary<string, int> before,
        Dictionary<string, int> after, List<Curve> outputs, bool associative, out Dictionary<string, object> evidence)
    {
        List<object> missing = new List<object>();
        List<object> changed = new List<object>();
        int preserved = 0;
        if (before != null)
            foreach (KeyValuePair<string, int> pair in before)
            {
                int current;
                if (after == null || !after.TryGetValue(pair.Key, out current)) missing.Add(pair.Key);
                else if (current != pair.Value) changed.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", pair.Key }, { "before", pair.Value }, { "after", current }
                });
                else preserved++;
            }
        List<object> outputsState = new List<object>();
        int expected = 1;
        bool outputsMatch = outputs != null;
        HashSet<string> outputTags = new HashSet<string>(StringComparer.Ordinal);
        if (outputs != null)
            foreach (Curve curve in outputs)
            {
                if (curve == null) { outputsMatch = false; continue; }
                string tag = curve.Tag.ToString();
                int status = 0;
                bool found = after != null && after.TryGetValue(tag, out status);
                bool match = found && status == expected && outputTags.Add(tag);
                if (!match) outputsMatch = false;
                outputsState.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", tag }, { "curve_id", SafeM3SketchProjectionJournalId(curve) },
                    { "found", found }, { "status", found ? (object)status : null },
                    { "expected_status", expected }, { "matches", match }
                });
            }
        List<object> unexpected = new List<object>();
        if (after != null)
            foreach (string tag in after.Keys)
                if (!before.ContainsKey(tag) && !outputTags.Contains(tag)) unexpected.Add(tag);
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "baseline_curve_count", before == null ? 0 : before.Count },
            { "preserved_count", preserved },
            { "missing_baseline_tags", missing },
            { "changed_baseline_statuses", changed },
            { "expected_new_curve_status", expected },
            { "output_statuses", outputsState },
            { "unexpected_new_reference_tags", unexpected },
            { "actual_curve_count", after == null ? 0 : after.Count },
            { "expected_curve_count", (before == null ? 0 : before.Count) + outputTags.Count }
        };
        return missing.Count == 0 && changed.Count == 0 && outputsMatch && unexpected.Count == 0 &&
            after != null && before != null && after.Count == before.Count + outputTags.Count;
    }

    private static Dictionary<string, M3SketchProjectionFeatureState> CaptureM3SketchProjectionFeatureStates(Part part)
    {
        Feature[] features = part.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature inventory returned null during sketch projection");
        Dictionary<string, M3SketchProjectionFeatureState> result =
            new Dictionary<string, M3SketchProjectionFeatureState>(StringComparer.Ordinal);
        foreach (Feature feature in features)
        {
            if (feature == null) throw new M2ModelException("UNSUPPORTED_MODEL", "feature inventory contains null during sketch projection");
            string tag = feature.Tag.ToString();
            if (String.IsNullOrEmpty(tag) || result.ContainsKey(tag))
                throw new M2ModelException("MODEL_READ_FAILED", "feature inventory contains an empty or duplicate Tag during sketch projection");
            Feature[] parents = feature.GetParents();
            Feature[] children = feature.GetChildren();
            Body[] bodies = feature.GetBodies();
            Expression[] expressions = feature.GetExpressions();
            if (parents == null || children == null || bodies == null || expressions == null)
                throw new M2ModelException("MODEL_READ_FAILED", "feature parent/child readback returned null during sketch projection");
            List<string> parentIds = GetJournalIds(parents, "projection feature parent");
            List<string> childIds = GetJournalIds(children, "projection feature child");
            List<string> parentTags = new List<string>();
            foreach (Feature parent in parents) parentTags.Add(parent.Tag.ToString());
            List<string> childTags = new List<string>();
            foreach (Feature child in children) childTags.Add(child.Tag.ToString());
            List<string> bodyTags = new List<string>();
            foreach (Body body in bodies) bodyTags.Add(body.Tag.ToString());
            List<string> expressionNames = new List<string>();
            foreach (Expression expression in expressions)
            {
                if (expression == null) throw new M2ModelException("UNSUPPORTED_MODEL", "feature expression inventory contains null during stable history capture");
                expressionNames.Add(expression.Name);
            }
            parentTags.Sort(StringComparer.Ordinal);
            childTags.Sort(StringComparer.Ordinal);
            bodyTags.Sort(StringComparer.Ordinal);
            expressionNames.Sort(StringComparer.Ordinal);
            M3SketchProjectionFeatureState item = new M3SketchProjectionFeatureState
            {
                Feature = feature,
                Id = RequiredJournalId(feature, "feature"),
                Tag = tag,
                Type = feature.FeatureType ?? String.Empty,
                ParentIds = parentIds,
                ChildIds = childIds,
                ParentTags = parentTags,
                ChildTags = childTags,
                BodyTags = bodyTags,
                ExpressionNames = expressionNames,
                Suppressed = feature.Suppressed
            };
            item.Signature = M2Json.CanonicalText(M3SketchProjectionStableFeatureRecord(item));
            result.Add(tag, item);
        }
        return result;
    }

    private static Dictionary<string, M3SketchProjectionCurveState> CaptureM3SketchProjectionCurveInventory(Part part)
    {
        Curve[] curves = part.Curves.ToArray();
        if (curves == null) throw new M2ModelException("MODEL_READ_FAILED", "Part.Curves returned null during projection inventory capture");
        Dictionary<string, M3SketchProjectionCurveState> result =
            new Dictionary<string, M3SketchProjectionCurveState>(StringComparer.Ordinal);
        foreach (Curve curve in curves)
        {
            if (curve == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Part.Curves contains null during projection inventory capture");
            string tag = curve.Tag.ToString();
            if (String.IsNullOrEmpty(tag) || result.ContainsKey(tag))
                throw new M2ModelException("MODEL_READ_FAILED", "Part.Curves contains an empty or duplicate Tag during projection inventory capture");
            List<object> geometryRecords = BuildSketchGeometry(new NXObject[] { curve }, "mm");
            if (geometryRecords == null || geometryRecords.Count != 1)
                throw new M2ModelException("MODEL_READ_FAILED", "complete Part.Curves geometry readback returned no unique record");
            Dictionary<string, object> record = geometryRecords[0] as Dictionary<string, object>;
            if (record == null)
                throw new M2ModelException("MODEL_READ_FAILED", "complete Part.Curves geometry readback returned an invalid record");
            object detailStatus;
            if (!record.TryGetValue("detail_status", out detailStatus) ||
                !String.Equals(Convert.ToString(detailStatus, CultureInfo.InvariantCulture), "SUPPORTED", StringComparison.Ordinal))
                throw new M2ModelException("UNSUPPORTED_PART_GEOMETRY",
                    "F10 requires complete native geometry readback for every Part.Curves object; unsupported global curve type " + curve.GetType().Name);
            record.Remove("journal_id");
            record["tag"] = tag;
            record["length_mm"] = curve.GetLength();
            M3SketchProjectionCurveState item = new M3SketchProjectionCurveState
            {
                Curve = curve,
                Id = RequiredJournalId(curve, "Part curve"),
                Tag = tag,
                Type = curve.GetType().Name,
                Signature = M2Json.CanonicalText(record)
            };
            result.Add(tag, item);
        }
        return result;
    }

    private static string M3SketchProjectionCurveInventoryText(Dictionary<string, M3SketchProjectionCurveState> inventory)
    {
        List<M3SketchProjectionCurveState> ordered = new List<M3SketchProjectionCurveState>();
        if (inventory != null) ordered.AddRange(inventory.Values);
        ordered.Sort(delegate(M3SketchProjectionCurveState left, M3SketchProjectionCurveState right)
        {
            return String.CompareOrdinal(left.Tag, right.Tag);
        });
        List<object> signatures = new List<object>();
        foreach (M3SketchProjectionCurveState item in ordered) signatures.Add(item.Signature);
        return M2Json.CanonicalText(signatures);
    }

    private static bool M3SketchProjectionGlobalCurveDeltaMatches(M3SketchProjectionPlanState plan,
        Dictionary<string, M3SketchProjectionCurveState> after, List<string> targetOutputTags,
        Dictionary<string, object> relationEvidence, out Dictionary<string, object> evidence)
    {
        List<string> delta = new List<string>();
        List<object> changedBaseline = new List<object>();
        foreach (KeyValuePair<string, M3SketchProjectionCurveState> pair in plan.CurveInventoryBefore)
        {
            M3SketchProjectionCurveState current;
            if (!after.TryGetValue(pair.Key, out current))
                changedBaseline.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                { { "tag", pair.Key }, { "reason", "baseline curve missing" } });
            else if (!String.Equals(pair.Value.Signature, current.Signature, StringComparison.Ordinal))
                changedBaseline.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                { { "tag", pair.Key }, { "reason", "baseline curve geometry or identity changed" } });
        }
        foreach (string tag in after.Keys)
            if (!plan.CurveInventoryBefore.ContainsKey(tag)) delta.Add(tag);
        delta.Sort(StringComparer.Ordinal);

        HashSet<string> expected = new HashSet<string>(targetOutputTags ?? new List<string>(), StringComparer.Ordinal);
        List<string> sectionOutputTags = relationEvidence != null && relationEvidence.ContainsKey("committed_section_source_readback")
            ? ((relationEvidence["committed_section_source_readback"] as Dictionary<string, object>) ??
                new Dictionary<string, object>(StringComparer.Ordinal)).ContainsKey("section_output_curve_tags")
                ? (((Dictionary<string, object>)relationEvidence["committed_section_source_readback"])["section_output_curve_tags"] as List<string>) ?? new List<string>()
                : new List<string>()
            : new List<string>();
        List<string> sectionTagsInPartInventory = new List<string>();
        List<string> sectionTagsNotInPartInventory = new List<string>();
        foreach (string tag in sectionOutputTags)
        {
            // Section.GetOutputCurves can expose an internal projection-helper Curve that is
            // not present in Part.Curves. If it is present, allow only that exact CPROJ-owned tag.
            if (after.ContainsKey(tag))
            {
                sectionTagsInPartInventory.Add(tag);
                expected.Add(tag);
            }
            else sectionTagsNotInPartInventory.Add(tag);
        }
        List<string> expectedTags = new List<string>(expected);
        expectedTags.Sort(StringComparer.Ordinal);
        bool tagsMatch = M3SketchProjectionStringArraysEqual(delta.ToArray(), expectedTags.ToArray());

        List<object> sectionGeometry = new List<object>();
        bool sectionShapesMatch = true;
        foreach (string tag in sectionTagsInPartInventory)
        {
            M3SketchProjectionCurveState curveState;
            if (!after.TryGetValue(tag, out curveState) || curveState.Curve == null)
            {
                sectionShapesMatch = false;
                sectionGeometry.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                { { "tag", tag }, { "found", false }, { "matches_source", false } });
                continue;
            }
            int candidates = 0;
            foreach (M3SketchProjectionSourceState source in plan.Sources)
            {
                Dictionary<string, object> ignored;
                if (M3SketchProjectionOriginalSourceGeometryMatches(source, curveState.Curve, out ignored)) candidates++;
            }
            bool match = candidates > 0;
            if (!match) sectionShapesMatch = false;
            sectionGeometry.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", tag }, { "curve_id", curveState.Id }, { "curve_type", curveState.Type },
                { "matching_source_count", candidates }, { "matches_original_source_geometry", match }
            });
        }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "baseline_curve_count", plan.CurveInventoryBefore.Count },
            { "after_curve_count", after.Count },
            { "baseline_curve_changes", changedBaseline },
            { "actual_new_curve_tags", M3SketchProjectionStringObjects(delta) },
            { "target_sketch_output_tags", M3SketchProjectionStringObjects(targetOutputTags) },
            { "section_output_curve_tags", M3SketchProjectionStringObjects(sectionOutputTags) },
            { "section_output_tags_in_part_curve_inventory", M3SketchProjectionStringObjects(sectionTagsInPartInventory) },
            { "section_output_tags_not_in_part_curve_inventory", M3SketchProjectionStringObjects(sectionTagsNotInPartInventory) },
            { "expected_new_curve_tags", M3SketchProjectionStringObjects(expectedTags) },
            { "new_curve_delta_matches_exact_allowed_set", tagsMatch },
            { "section_output_geometry_in_part_inventory", sectionGeometry },
            { "section_output_geometry_matches_original_sources", sectionShapesMatch }
        };
        return changedBaseline.Count == 0 && tagsMatch && sectionShapesMatch &&
            (plan.Request.Associative || sectionOutputTags.Count == 0);
    }

    private static string M3SketchProjectionFeatureInventoryText(Dictionary<string, M3SketchProjectionFeatureState> features)
    {
        List<M3SketchProjectionFeatureState> ordered = new List<M3SketchProjectionFeatureState>(features.Values);
        ordered.Sort(delegate(M3SketchProjectionFeatureState left, M3SketchProjectionFeatureState right)
        {
            return String.CompareOrdinal(left.Tag, right.Tag);
        });
        List<object> records = new List<object>();
        foreach (M3SketchProjectionFeatureState feature in ordered) records.Add(M3SketchProjectionStableFeatureRecord(feature));
        return M2Json.CanonicalText(records);
    }

    private static List<object> M3SketchProjectionStableHistoryProjection(Part part,
        Dictionary<string, M3SketchProjectionFeatureState> features)
    {
        if (part == null || features == null)
            throw new M2ModelException("MODEL_READ_FAILED", "stable projection history requires a Part and feature inventory");
        List<M3SketchProjectionFeatureState> orderedFeatures = new List<M3SketchProjectionFeatureState>(features.Values);
        orderedFeatures.Sort(delegate(M3SketchProjectionFeatureState left, M3SketchProjectionFeatureState right)
        {
            return String.CompareOrdinal(left.Tag, right.Tag);
        });
        List<object> featureRecords = new List<object>();
        foreach (M3SketchProjectionFeatureState feature in orderedFeatures)
            featureRecords.Add(M3SketchProjectionStableFeatureRecord(feature));

        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory returned null during stable projection history capture");
        List<Dictionary<string, object>> sketchRecords = new List<Dictionary<string, object>>();
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Sketch inventory contains null during stable projection history capture");
            sketchRecords.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", sketch.Tag.ToString() },
                { "feature_tag", sketch.Feature == null ? null : sketch.Feature.Tag.ToString() },
                { "is_internal", sketch.IsInternal },
                { "is_drafting_sketch", sketch.IsDraftingSketch }
            });
        }
        sketchRecords.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            return String.CompareOrdinal(Convert.ToString(left["tag"], CultureInfo.InvariantCulture),
                Convert.ToString(right["tag"], CultureInfo.InvariantCulture));
        });
        List<object> result = new List<object>();
        result.Add(featureRecords);
        result.Add(ToObjectList(sketchRecords));
        return result;
    }

    private static Dictionary<string, object> M3SketchProjectionStableFeatureRecord(
        M3SketchProjectionFeatureState feature)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", feature.Tag },
            { "feature_type", feature.Type },
            { "suppressed", feature.Suppressed },
            { "parent_tags", M3SketchProjectionStringObjects(feature.ParentTags) },
            { "child_tags", M3SketchProjectionStringObjects(feature.ChildTags) },
            { "body_tags", M3SketchProjectionStringObjects(feature.BodyTags) },
            { "expression_names", M3SketchProjectionStringObjects(feature.ExpressionNames) }
        };
    }

    private static Dictionary<string, object> M3SketchProjectionFeatureRecord(M3SketchProjectionFeatureState feature)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", feature.Tag }, { "journal_id", feature.Id }, { "feature_type", feature.Type },
            { "parent_ids", M3SketchProjectionStringObjects(feature.ParentIds) },
            { "child_ids", M3SketchProjectionStringObjects(feature.ChildIds) },
            { "parent_tags", M3SketchProjectionStringObjects(feature.ParentTags) },
            { "child_tags", M3SketchProjectionStringObjects(feature.ChildTags) }
        };
    }

    private static List<M3SketchProjectionFeatureState> M3SketchProjectionNewFeatures(
        Dictionary<string, M3SketchProjectionFeatureState> before,
        Dictionary<string, M3SketchProjectionFeatureState> after)
    {
        List<M3SketchProjectionFeatureState> result = new List<M3SketchProjectionFeatureState>();
        foreach (KeyValuePair<string, M3SketchProjectionFeatureState> pair in after)
            if (!before.ContainsKey(pair.Key)) result.Add(pair.Value);
        result.Sort(delegate(M3SketchProjectionFeatureState left, M3SketchProjectionFeatureState right)
        {
            return String.CompareOrdinal(left.Tag, right.Tag);
        });
        return result;
    }

    private static bool M3SketchProjectionFeatureDeltaMatches(M3SketchProjectionPlanState plan,
        Dictionary<string, M3SketchProjectionFeatureState> after, Dictionary<string, object> relationEvidence)
    {
        List<M3SketchProjectionFeatureState> delta = M3SketchProjectionNewFeatures(plan.FeatureStatesBefore, after);
        if (!plan.Request.Associative) return delta.Count == 0;
        if (delta.Count != 1 || !String.Equals(delta[0].Type, "CPROJ", StringComparison.OrdinalIgnoreCase)) return false;
        object idValue;
        return relationEvidence.TryGetValue("projection_feature_id", out idValue) &&
            String.Equals(Convert.ToString(idValue, CultureInfo.InvariantCulture), delta[0].Id, StringComparison.Ordinal);
    }

    private static bool M3SketchProjectionHistoryMatches(M3SketchProjectionPlanState plan, Part part,
        Dictionary<string, M3SketchProjectionFeatureState> featuresAfter,
        Dictionary<string, object> relationEvidence, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> baseline = DeepCloneValue(plan.HistoryProjectionBefore) as List<object>;
        List<object> current = DeepCloneValue(
            M3SketchProjectionStableHistoryProjection(part, featuresAfter)) as List<object>;
        if (baseline == null || current == null || baseline.Count != 2 || current.Count != 2)
        {
            evidence["stable_history_available"] = false;
            return false;
        }

        List<object> beforeFeatures = baseline[0] as List<object>;
        List<object> afterFeatures = current[0] as List<object>;
        if (beforeFeatures == null || afterFeatures == null)
        {
            evidence["stable_feature_history_available"] = false;
            return false;
        }
        Dictionary<string, Dictionary<string, object>> beforeByTag = M3SketchProjectionHistoryFeaturesByTag(beforeFeatures);
        Dictionary<string, Dictionary<string, object>> afterByTag = M3SketchProjectionHistoryFeaturesByTag(afterFeatures);
        if (beforeByTag == null || afterByTag == null)
        {
            evidence["unique_feature_tags"] = false;
            return false;
        }

        bool projectionFeatureRemoved = !plan.Request.Associative;
        bool targetParentDeltaMatches = !plan.Request.Associative;
        bool sourceChildDeltaMatches = !plan.Request.Associative;
        bool sourceParentsResolved;
        HashSet<string> sourceParentTags = M3SketchProjectionExpectedSourceParentTags(plan, out sourceParentsResolved);
        if (!sourceParentsResolved)
        {
            evidence["source_parent_tags_resolved"] = false;
            return false;
        }
        if (plan.Request.Associative)
        {
            string projectionTag = relationEvidence == null || !relationEvidence.ContainsKey("projection_feature_tag")
                ? null : Convert.ToString(relationEvidence["projection_feature_tag"], CultureInfo.InvariantCulture);
            Dictionary<string, object> projectionRecord;
            if (String.IsNullOrEmpty(projectionTag) || !afterByTag.TryGetValue(projectionTag, out projectionRecord) ||
                !String.Equals(Convert.ToString(projectionRecord["feature_type"], CultureInfo.InvariantCulture), "CPROJ", StringComparison.OrdinalIgnoreCase))
            {
                evidence["projection_feature_tag_resolved"] = false;
                return false;
            }

            object rawProjectionChildTags;
            bool projectionChildTagsPresent = projectionRecord.TryGetValue("child_tags", out rawProjectionChildTags);
            List<object> projectionChildTagList = rawProjectionChildTags as List<object>;
            bool projectionChildTagsAllowed = projectionChildTagsPresent && projectionChildTagList != null &&
                projectionChildTagList.Count == 0;
            evidence["projection_feature_child_tags_raw"] = projectionChildTagsPresent
                ? DeepCloneValue(rawProjectionChildTags) : null;
            evidence["projection_feature_child_tags_allowed"] = projectionChildTagsAllowed;
            if (!projectionChildTagsAllowed) return false;

            Dictionary<string, object> beforeTarget;
            Dictionary<string, object> afterTarget;
            if (!beforeByTag.TryGetValue(plan.SketchFeatureTag, out beforeTarget) ||
                !afterByTag.TryGetValue(plan.SketchFeatureTag, out afterTarget))
            {
                evidence["target_sketch_feature_tag_stable"] = false;
                return false;
            }
            HashSet<string> baselineTargetParents = M3SketchProjectionHistoryTagSet(beforeTarget, "parent_tags");
            HashSet<string> actualTargetParents = M3SketchProjectionHistoryTagSet(afterTarget, "parent_tags");
            HashSet<string> expectedAddedParents = new HashSet<string>(sourceParentTags, StringComparer.Ordinal);
            expectedAddedParents.ExceptWith(baselineTargetParents);
            HashSet<string> actualAddedParents = new HashSet<string>(actualTargetParents, StringComparer.Ordinal);
            actualAddedParents.ExceptWith(baselineTargetParents);
            targetParentDeltaMatches = M3SketchProjectionTagSetsEqual(expectedAddedParents, actualAddedParents);

            HashSet<string> baselineTargetParentsForCproj = new HashSet<string>(baselineTargetParents, StringComparer.Ordinal);
            baselineTargetParentsForCproj.UnionWith(sourceParentTags);
            HashSet<string> actualCprojParents = M3SketchProjectionHistoryTagSet(projectionRecord, "parent_tags");
            bool projectionParentsMatch = M3SketchProjectionTagSetsEqual(baselineTargetParentsForCproj, actualCprojParents);

            List<string> projectionParentReferenceTags = new List<string>();
            List<string> projectionChildReferenceTags = new List<string>();
            List<string> projectionChildReferenceTagsOutsideParentSet = new List<string>();
            foreach (KeyValuePair<string, Dictionary<string, object>> pair in afterByTag)
            {
                if (String.Equals(pair.Key, projectionTag, StringComparison.Ordinal)) continue;
                HashSet<string> featureParents = M3SketchProjectionHistoryTagSet(pair.Value, "parent_tags");
                if (featureParents.Contains(projectionTag)) projectionParentReferenceTags.Add(pair.Key);
                HashSet<string> featureChildren = M3SketchProjectionHistoryTagSet(pair.Value, "child_tags");
                if (featureChildren.Contains(projectionTag))
                {
                    projectionChildReferenceTags.Add(pair.Key);
                    if (!actualCprojParents.Contains(pair.Key))
                        projectionChildReferenceTagsOutsideParentSet.Add(pair.Key);
                }
            }
            projectionParentReferenceTags.Sort(StringComparer.Ordinal);
            projectionChildReferenceTags.Sort(StringComparer.Ordinal);
            projectionChildReferenceTagsOutsideParentSet.Sort(StringComparer.Ordinal);
            bool projectionTagAbsentFromExistingParentTags = projectionParentReferenceTags.Count == 0;
            bool projectionChildReferencesMatchParentSet = projectionChildReferenceTagsOutsideParentSet.Count == 0;
            evidence["projection_feature_parent_tags_match_source_and_support"] = projectionParentsMatch;
            evidence["projection_tag_absent_from_existing_feature_parent_tags"] = projectionTagAbsentFromExistingParentTags;
            evidence["projection_tag_existing_child_references_within_verified_parent_set"] = projectionChildReferencesMatchParentSet;
            evidence["projection_tag_existing_parent_reference_feature_tags"] = M3SketchProjectionStringObjects(projectionParentReferenceTags);
            evidence["projection_tag_existing_child_reference_feature_tags"] = M3SketchProjectionStringObjects(projectionChildReferenceTags);
            evidence["projection_tag_existing_child_references_outside_verified_parent_set"] =
                M3SketchProjectionStringObjects(projectionChildReferenceTagsOutsideParentSet);
            if (!projectionParentsMatch || !projectionTagAbsentFromExistingParentTags ||
                !projectionChildReferencesMatchParentSet) return false;

            actualTargetParents.ExceptWith(expectedAddedParents);
            M3SketchProjectionSetHistoryTags(afterTarget, "parent_tags", actualTargetParents);

            sourceChildDeltaMatches = true;
            foreach (string sourceParentTag in sourceParentTags)
            {
                Dictionary<string, object> beforeSource;
                Dictionary<string, object> afterSource;
                if (!beforeByTag.TryGetValue(sourceParentTag, out beforeSource) ||
                    !afterByTag.TryGetValue(sourceParentTag, out afterSource))
                {
                    sourceChildDeltaMatches = false;
                    continue;
                }
                HashSet<string> baselineChildren = M3SketchProjectionHistoryTagSet(beforeSource, "child_tags");
                HashSet<string> actualChildren = M3SketchProjectionHistoryTagSet(afterSource, "child_tags");
                actualChildren.Remove(projectionTag);
                HashSet<string> addedChildren = new HashSet<string>(actualChildren, StringComparer.Ordinal);
                addedChildren.ExceptWith(baselineChildren);
                foreach (string addedChild in addedChildren)
                    if (!String.Equals(addedChild, plan.SketchFeatureTag, StringComparison.Ordinal))
                        sourceChildDeltaMatches = false;
                if (!baselineChildren.Contains(plan.SketchFeatureTag))
                    actualChildren.Remove(plan.SketchFeatureTag);
                M3SketchProjectionSetHistoryTags(afterSource, "child_tags", actualChildren);
            }

            // The single CPROJ and only its recorded parent links are operation-owned.
            afterByTag.Remove(projectionTag);
            afterFeatures.Remove(projectionRecord);
            foreach (string parentTag in actualCprojParents)
            {
                Dictionary<string, object> featureRecord;
                if (!afterByTag.TryGetValue(parentTag, out featureRecord)) continue;
                HashSet<string> children = M3SketchProjectionHistoryTagSet(featureRecord, "child_tags");
                children.Remove(projectionTag);
                M3SketchProjectionSetHistoryTags(featureRecord, "child_tags", children);
            }
            projectionFeatureRemoved = projectionParentsMatch &&
                afterByTag.Count == beforeByTag.Count && afterFeatures.Count == beforeFeatures.Count;
            evidence["projection_feature_tag"] = projectionTag;
        }

        bool featureTagsMatch = beforeByTag.Count == afterByTag.Count;
        foreach (KeyValuePair<string, Dictionary<string, object>> pair in beforeByTag)
        {
            Dictionary<string, object> afterRecord;
            if (!afterByTag.TryGetValue(pair.Key, out afterRecord) ||
                !String.Equals(M2Json.CanonicalText(pair.Value), M2Json.CanonicalText(afterRecord), StringComparison.Ordinal))
            {
                featureTagsMatch = false;
                break;
            }
        }
        bool sketchesMatch = String.Equals(M2Json.CanonicalText(baseline[1]),
            M2Json.CanonicalText(current[1]), StringComparison.Ordinal);
        bool matches = featureTagsMatch && sketchesMatch && projectionFeatureRemoved &&
            targetParentDeltaMatches && sourceChildDeltaMatches;
        evidence["feature_tag_set_matches_after_allowed_projection"] = featureTagsMatch;
        evidence["sketch_tag_set_matches"] = sketchesMatch;
        evidence["single_projection_feature_and_exact_source_edges"] = projectionFeatureRemoved;
        evidence["target_sketch_added_source_parent_tags_match"] = targetParentDeltaMatches;
        evidence["source_parent_child_delta_is_limited_to_target_sketch"] = sourceChildDeltaMatches;
        evidence["stable_history_matches"] = matches;
        evidence["expected_source_parent_feature_tags"] = M3SketchProjectionStringObjects(new List<string>(sourceParentTags));
        return matches;
    }

    private static Dictionary<string, Dictionary<string, object>> M3SketchProjectionHistoryFeaturesByTag(List<object> features)
    {
        Dictionary<string, Dictionary<string, object>> result =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (object value in features)
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            if (record == null) return null;
            object tagValue;
            if (!record.TryGetValue("tag", out tagValue)) return null;
            string tag = tagValue as string;
            if (String.IsNullOrEmpty(tag) || result.ContainsKey(tag)) return null;
            result.Add(tag, record);
        }
        return result;
    }

    private static HashSet<string> M3SketchProjectionHistoryTagSet(Dictionary<string, object> record, string name)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        object value;
        if (!record.TryGetValue(name, out value)) return result;
        List<object> list = value as List<object>;
        if (list == null) return result;
        foreach (object item in list)
        {
            string tag = item as string;
            if (!String.IsNullOrEmpty(tag)) result.Add(tag);
        }
        return result;
    }

    private static void M3SketchProjectionSetHistoryTags(Dictionary<string, object> record,
        string name, HashSet<string> tags)
    {
        List<string> sorted = new List<string>(tags);
        sorted.Sort(StringComparer.Ordinal);
        record[name] = M3SketchProjectionStringObjects(sorted);
    }

    private static bool M3SketchProjectionTagSetsEqual(HashSet<string> left, HashSet<string> right)
    {
        if (left == null || right == null || left.Count != right.Count) return false;
        foreach (string item in left) if (!right.Contains(item)) return false;
        return true;
    }

    private static HashSet<string> M3SketchProjectionExpectedSourceParentTags(M3SketchProjectionPlanState plan,
        out bool resolved)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        resolved = plan != null && plan.Sources != null && plan.FeatureStatesBefore != null;
        if (!resolved) return result;
        foreach (M3SketchProjectionSourceState source in plan.Sources)
            foreach (string sourceParentId in source.SourceFeatureParentIds)
            {
                M3SketchProjectionFeatureState match = null;
                foreach (M3SketchProjectionFeatureState candidate in plan.FeatureStatesBefore.Values)
                    if (String.Equals(candidate.Id, sourceParentId, StringComparison.Ordinal))
                    {
                        if (match != null) { resolved = false; break; }
                        match = candidate;
                    }
                if (match == null) resolved = false;
                else result.Add(match.Tag);
            }
        return result;
    }

    private static Dictionary<string, CornerGeometrySnapshot> M3SketchProjectionWithoutTags(
        Dictionary<string, CornerGeometrySnapshot> source, HashSet<string> removedTags)
    {
        Dictionary<string, CornerGeometrySnapshot> result = new Dictionary<string, CornerGeometrySnapshot>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, CornerGeometrySnapshot> pair in source)
            if (removedTags == null || !removedTags.Contains(pair.Key)) result.Add(pair.Key, pair.Value);
        return result;
    }

    private static int M3SketchProjectionConstraintCount(
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraints)
    {
        int count = 0;
        if (constraints != null)
            foreach (Dictionary<string, CornerConstraintSnapshot> perSketch in constraints.Values)
                if (perSketch != null) count += perSketch.Count;
        return count;
    }

    private static bool M3SketchProjectionEvidenceChecksPassed(Dictionary<string, object> evidence)
    {
        Dictionary<string, object> checks = evidence == null ? null : evidence["checks"] as Dictionary<string, object>;
        return M3SketchProjectionChecksPassed(checks);
    }

    private static bool M3SketchProjectionChecksPassed(Dictionary<string, object> checks)
    {
        if (checks == null) return false;
        foreach (string name in M3SketchProjectionCheckNames)
        {
            object value;
            Dictionary<string, object> check;
            if (!checks.TryGetValue(name, out value) || (check = value as Dictionary<string, object>) == null ||
                !String.Equals(Convert.ToString(check["status"], CultureInfo.InvariantCulture), "VERIFIED", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static M3VerificationResult M3SketchProjectionFailure(string revisionId, string reason)
    {
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> detail = new Dictionary<string, object>(StringComparer.Ordinal);
        detail["reason"] = reason;
        foreach (string name in M3SketchProjectionCheckNames) checks[name] = VerificationCheck("MISMATCH", detail);
        return new M3VerificationResult
        {
            Verified = false,
            RevisionId = revisionId,
            Checks = checks,
            Result = new Dictionary<string, object>(StringComparer.Ordinal),
            ErrorCode = "PROJECTION_VERIFICATION_FAILED",
            ErrorMessage = reason
        };
    }

    private static Dictionary<string, object> M3SketchProjectionExecutionResult(M3SketchProjectionPlanState plan,
        string revisionId, string mutationState, Dictionary<string, object> evidence)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result["kind"] = "edit_sketch_projection";
        result["revision_id"] = revisionId;
        result["mutation_state"] = mutationState;
        result["sketch_id"] = plan.SketchId;
        result["sketch_tag"] = plan.SketchTag;
        result["target_feature_id"] = SafeM3SketchProjectionJournalId(plan.Sketch == null ? null : plan.Sketch.Feature);
        result["target_feature_tag"] = plan.SketchFeatureTag;
        result["source_curve_ids"] = M3SketchProjectionStringObjects(plan.Request.SourceCurveIds);
        result["output_curve_ids"] = plan.OutputCurveIds == null ? new List<object>() : M3SketchProjectionStringObjects(plan.OutputCurveIds);
        result["output_curve_tags"] = plan.OutputCurveTags == null ? new List<object>() : M3SketchProjectionStringObjects(plan.OutputCurveTags);
        result["output_curve_types"] = plan.OutputCurveTypes == null ? new List<object>() : M3SketchProjectionStringObjects(plan.OutputCurveTypes);
        result["associative"] = plan.Request.Associative;
        result["direction_xyz"] = M3SketchProjectionVector(plan.Direction);
        result["native_operation"] = plan.NativeOperationEvidence;
        result["evidence"] = evidence ?? new Dictionary<string, object>(StringComparer.Ordinal);
        return result;
    }

    private static bool M3SketchProjectionIsSketchActive(Session session, Sketch sketch)
    {
        Sketch active = session == null ? null : session.ActiveSketch;
        return active != null && sketch != null && active.Tag.Equals(sketch.Tag);
    }

    private static List<object> M3SketchProjectionMapRecords(Dictionary<string, string> map)
    {
        List<object> result = new List<object>();
        if (map == null) return result;
        List<string> keys = new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (string key in keys)
            result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "source_id", key }, { "output_id", map[key] }
            });
        return result;
    }

    private static string[] M3SketchProjectionCurveIds(List<Curve> curves)
    {
        string[] result = new string[curves == null ? 0 : curves.Count];
        for (int i = 0; i < result.Length; i++) result[i] = RequiredJournalId(curves[i], "projection output curve");
        return result;
    }

    private static string[] M3SketchProjectionCurveTypes(List<Curve> curves)
    {
        string[] result = new string[curves == null ? 0 : curves.Count];
        for (int i = 0; i < result.Length; i++) result[i] = curves[i].GetType().Name;
        return result;
    }

    private static string[] M3SketchProjectionObjectTagStrings(NXObject[] objects)
    {
        List<string> result = new List<string>();
        if (objects != null)
            foreach (NXObject item in objects)
                if (item != null) result.Add(item.Tag.ToString());
        result.Sort(StringComparer.Ordinal);
        return result.ToArray();
    }

    private static List<object> M3SketchProjectionObjectTags(NXObject[] objects)
    {
        return M3SketchProjectionStringObjects(M3SketchProjectionObjectTagStrings(objects));
    }

    private static List<object> M3SketchProjectionObjectTypes(NXObject[] objects)
    {
        List<object> result = new List<object>();
        if (objects != null)
            foreach (NXObject item in objects) result.Add(item == null ? null : item.GetType().Name);
        return result;
    }

    private static List<object> M3SketchProjectionStringObjects(IEnumerable<string> values)
    {
        List<object> result = new List<object>();
        if (values != null) foreach (string value in values) result.Add(value);
        return result;
    }

    private static bool M3SketchProjectionStringArraysEqual(string[] left, string[] right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        string[] a = (string[])left.Clone();
        string[] b = (string[])right.Clone();
        Array.Sort(a, StringComparer.Ordinal);
        Array.Sort(b, StringComparer.Ordinal);
        for (int i = 0; i < a.Length; i++)
            if (!String.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool M3SketchProjectionStringListsEqual(List<string> left, List<string> right)
    {
        return left != null && right != null && M3SketchProjectionStringArraysEqual(left.ToArray(), right.ToArray());
    }

    private static List<object> M3SketchProjectionPoint(Point3d point)
    {
        if (!M3SketchProjectionFinite(point)) throw new M2ModelException("MODEL_READ_FAILED", "projection numeric point is non-finite");
        return new List<object> { point.X, point.Y, point.Z };
    }

    private static List<object> M3SketchProjectionVector(Vector3d vector)
    {
        if (!M3SketchProjectionFinite(vector)) throw new M2ModelException("MODEL_READ_FAILED", "projection numeric vector is non-finite");
        return new List<object> { vector.X, vector.Y, vector.Z };
    }

    private static List<object> M3SketchProjectionLocalPoint(Point3d point, Point3d origin,
        Vector3d xAxis, Vector3d yAxis, Vector3d normal)
    {
        Vector3d displacement = new Vector3d(point.X - origin.X, point.Y - origin.Y, point.Z - origin.Z);
        return new List<object>
        {
            M3SketchProjectionDot(displacement, xAxis),
            M3SketchProjectionDot(displacement, yAxis),
            M3SketchProjectionDot(displacement, normal)
        };
    }

    private static double M3SketchProjectionLength(double[] vector)
    {
        if (vector == null || vector.Length != 3) return Double.NaN;
        return Math.Sqrt(vector[0] * vector[0] + vector[1] * vector[1] + vector[2] * vector[2]);
    }

    private static double M3SketchProjectionLength(Vector3d vector)
    {
        return Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
    }

    private static double M3SketchProjectionDot(Vector3d left, Vector3d right)
    {
        return left.X * right.X + left.Y * right.Y + left.Z * right.Z;
    }

    private static Vector3d M3SketchProjectionCross(Vector3d left, Vector3d right)
    {
        return new Vector3d(left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);
    }

    private static Vector3d M3SketchProjectionScale(Vector3d vector, double scale)
    {
        return new Vector3d(vector.X * scale, vector.Y * scale, vector.Z * scale);
    }

    private static double M3SketchProjectionDistance(Point3d left, Point3d right)
    {
        double x = left.X - right.X, y = left.Y - right.Y, z = left.Z - right.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static void M3SketchProjectionNormalize(ref Vector3d vector, string label)
    {
        double length = M3SketchProjectionLength(vector);
        if (!IsFinite(length) || length <= M3SketchProjectionAngleTolerance)
            throw new M2ModelException("UNSUPPORTED_SOURCE", label + " must be finite and nonzero");
        vector = M3SketchProjectionScale(vector, 1.0 / length);
    }

    private static bool M3SketchProjectionFinite(Point3d point)
    {
        return IsFinite(point.X) && IsFinite(point.Y) && IsFinite(point.Z);
    }

    private static bool M3SketchProjectionFinite(Vector3d vector)
    {
        return IsFinite(vector.X) && IsFinite(vector.Y) && IsFinite(vector.Z);
    }

    private static bool M3SketchProjectionNear(double value, double expected, double tolerance)
    {
        return IsFinite(value) && Math.Abs(value - expected) <= tolerance;
    }

    private static string SafeM3SketchProjectionJournalId(NXObject value)
    {
        try { return value == null ? null : value.JournalIdentifier; }
        catch { return null; }
    }

    private static string M3SketchProjectionSafeMessage(Exception ex)
    {
        if (ex == null) return null;
        try { return String.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message; }
        catch { return ex.GetType().Name; }
    }

    private static string M3SketchProjectionExceptionCode(Exception ex)
    {
        M2ModelException model = ex as M2ModelException;
        return model == null ? (ex == null ? null : ex.GetType().Name) : model.Code;
    }
}
