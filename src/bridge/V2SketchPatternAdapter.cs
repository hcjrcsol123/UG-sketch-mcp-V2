using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.Features;
using NxSketchMcp.M2;

/// <summary>Optional F09 adapter for native Line/Arc sketch patterns and mirror operations.</summary>
public sealed partial class M2OpenModelAdapter : IM3SketchPatternOperationAdapter
{
    private const double SketchPatternPositionToleranceMm = 1.0e-6;
    private const double SketchPatternAngleToleranceRad = 1.0e-7;
    private const double SketchPatternDirectionDotTolerance = 1.0e-9;

    private sealed class PatternPoint2
    {
        internal double X;
        internal double Y;
        internal PatternPoint2() { }
        internal PatternPoint2(double x, double y) { X = x; Y = y; }
    }

    private sealed class PatternCurveModel
    {
        internal string SourceCurveId;
        internal string SourceCurveTag;
        internal string ObjectType;
        internal bool IsArc;
        internal bool IsFullCircle;
        internal PatternPoint2 Start;
        internal PatternPoint2 End;
        internal PatternPoint2 Mid;
        internal PatternPoint2 Center;
        internal PatternPoint2 AxisX;
        internal PatternPoint2 AxisY;
        internal double Radius;
        internal double LengthMm;
        internal double StartAngleRad;
        internal double SweepRad;
    }

    private sealed class PatternTransform
    {
        internal string Kind;
        internal double M11 = 1.0;
        internal double M12;
        internal double M21;
        internal double M22 = 1.0;
        internal double TranslateX;
        internal double TranslateY;
        internal double PivotX;
        internal double PivotY;
        internal double AngleRad;
        internal double AxisPointX;
        internal double AxisPointY;
        internal double AxisDirectionX;
        internal double AxisDirectionY;
        internal double AxisStartX;
        internal double AxisStartY;
        internal double AxisEndX;
        internal double AxisEndY;
    }

    private sealed class PatternPosition
    {
        internal int PositionIndex;
        internal int PositionXIndex;
        internal int PositionYIndex;
        internal bool HasRectangularIndices;
        internal PatternTransform Transform;
    }

    private sealed class PatternExpectedInstance
    {
        internal string SourceCurveId;
        internal string SourceCurveTag;
        internal int PositionIndex;
        internal int PositionXIndex;
        internal int PositionYIndex;
        internal bool HasRectangularIndices;
        internal bool IsSourceInstance;
        internal PatternTransform Transform;
        internal PatternCurveModel Geometry;
        internal Dictionary<string, object> ExpectedCurveRecord;
        internal string OutputCurveTag;
    }

    private sealed class M3SketchPatternPlanState
    {
        internal M2ModelBinding Binding;
        internal BindingState BindingState;
        internal string RevisionId;
        internal string ActiveSketchTagBefore;
        internal M3SketchPatternRequest Request;
        internal Sketch Sketch;
        internal string SketchId;
        internal string SketchTag;
        internal string FeatureId;
        internal string FeatureTag;
        internal string[] SourceCurveIds;
        internal string[] SourceCurveTags;
        internal string[] SourceCurveTypes;
        internal PatternCurveModel[] SourceModels;
        internal string AxisCurveTag;
        internal Point3d SketchOrigin;
        internal Vector3d SketchXAxis;
        internal Vector3d SketchYAxis;
        internal Vector3d SketchNormal;
        internal PatternPoint2 MirrorAxisStart;
        internal PatternPoint2 MirrorAxisEnd;
        internal PatternPoint2 MirrorAxisDirection;
        internal List<PatternPosition> Positions;
        internal List<PatternExpectedInstance> ExpectedInstances;
        internal int ExpectedPatternInstanceCount;
        internal int ExpectedOutputCurveCount;
        internal bool DirtyBefore;
        internal string SemanticBefore;
        internal string HistoryBefore;
        internal string ExpressionsBefore;
        internal string FullGeometryBefore;
        internal Dictionary<string, CornerGeometrySnapshot> GeometryBefore;
        internal Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> ConstraintsBefore;
        internal Dictionary<string, string> FramesBefore;
        internal Dictionary<string, int> ReferenceStatesBefore;
        internal Dictionary<string, List<string>> PartHelperTagsBefore;
        internal Dictionary<string, List<string>> PartHelperTagsAfter;
        internal List<M3BodyMeasure> BodiesBefore;
        internal List<string> FeatureTagsBefore;
        internal Curve[] OutputCurves;
        internal string[] OutputCurveTags;
        internal bool CommitReturnedNull;
        internal bool FactoryOutputListConsistent;
        internal bool FactoryConstraintFlagsClear;
        internal Dictionary<string, CornerGeometrySnapshot> GeometryAfter;
        internal Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> ConstraintsAfter;
        internal Dictionary<string, string> FramesAfter;
        internal Dictionary<string, int> ReferenceStatesAfter;
        internal List<M3BodyMeasure> BodiesAfter;
        internal List<string> FeatureTagsAfter;
        internal string SemanticAfter;
        internal string HistoryAfter;
        internal string ExpressionsAfter;
        internal Dictionary<string, object> NativeOperationEvidence;
        internal Dictionary<string, object> AppliedEvidence;
        internal M3ExecutionResult Execution;
    }

    public M3PlanSnapshot PrepareSketchPattern(M2ModelBinding binding, M3SketchPatternRequest request)
    {
        ValidateSketchPatternRequest(request);
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "a Sketch is active in NX; sketch pattern preparation is paused");

        string activeSketchTagBefore = state.Session.ActiveSketch == null
            ? null : state.Session.ActiveSketch.Tag.ToString();
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            throw new M2ModelException("STALE_BINDING", bindingReason ?? "NX model binding is no longer current");
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current part revision");

        SnapshotResult captured = Capture(binding, state);
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id changed while resolving the sketch pattern sources");
        Sketch sketch = FindSketch(state.WorkPart, request.SketchId);
        if (sketch == null || !String.Equals(RequiredJournalId(sketch, "sketch"), request.SketchId, StringComparison.Ordinal))
            throw new M2ModelException("SKETCH_NOT_FOUND", "sketch_id does not identify a native Sketch in the bound Work Part");

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
            throw new M2ModelException("UNSUPPORTED_SKETCH_HISTORY", "F09 requires a native Sketch feature owned by the bound Work Part");

        Dictionary<string, object> frameRecord = M4SketchFrameRecord(sketch);
        Point3d sketchOrigin = ReadFramePoint(frameRecord, "origin_mm");
        Vector3d sketchNormal = ReadFrameVector(frameRecord, "normal_world");
        Vector3d sketchXAxis = ReadFrameVector(frameRecord, "x_axis_world");
        Vector3d sketchYAxis = ReadFrameVector(frameRecord, "y_axis_world");
        PatternNormalizeVector(ref sketchNormal, "Sketch normal");
        PatternNormalizeVector(ref sketchXAxis, "Sketch x-axis");
        PatternNormalizeVector(ref sketchYAxis, "Sketch y-axis");
        if (Math.Abs(PatternDot(sketchXAxis, sketchYAxis)) > SketchPatternDirectionDotTolerance ||
            Math.Abs(PatternDot(sketchNormal, sketchXAxis)) > SketchPatternDirectionDotTolerance ||
            Math.Abs(PatternDot(sketchNormal, sketchYAxis)) > SketchPatternDirectionDotTolerance ||
            PatternDot(PatternCross(sketchXAxis, sketchYAxis), sketchNormal) < 1.0 - SketchPatternDirectionDotTolerance)
            throw new M2ModelException("UNSUPPORTED_SKETCH_FRAME", "target Sketch frame is not a right-handed orthonormal frame");

        Curve[] sources = new Curve[request.SourceCurveIds.Count];
        string[] sourceTags = new string[sources.Length];
        string[] sourceTypes = new string[sources.Length];
        PatternCurveModel[] sourceModels = new PatternCurveModel[sources.Length];
        HashSet<string> sourceTagsSeen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < sources.Length; i++)
        {
            string sourceId = request.SourceCurveIds[i];
            Curve curve = FindSketchCornerCurve(sketch, sourceId);
            if (curve == null)
                throw new M2ModelException("CURVE_NOT_FOUND", "source_curve_ids contains an ID that is not native geometry in the requested Sketch");
            if (!sketch.IsSketchGeometry(curve))
                throw new M2ModelException("SKETCH_TARGET_MISMATCH", "every source_curve_id must belong to the explicitly requested Sketch");
            if (!(curve is Line) && !(curve is Arc))
                throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "F09 supports native Line and circular Arc curves only");
            string tag = curve.Tag.ToString();
            if (String.IsNullOrEmpty(tag) || !sourceTagsSeen.Add(tag))
                throw new M2ModelException("INVALID_REQUEST", "source_curve_ids resolve to duplicate or untagged native geometry");
            PatternCurveModel model;
            string geometryError;
            if (!TryCreatePatternCurveModel(curve, sketchOrigin, sketchXAxis, sketchYAxis, sketchNormal,
                sourceId, tag, out model, out geometryError))
                throw new M2ModelException("UNSUPPORTED_CURVE_GEOMETRY", "source curve " + sourceId + " is invalid: " + geometryError);
            sources[i] = curve;
            sourceTags[i] = tag;
            sourceTypes[i] = curve.GetType().Name;
            sourceModels[i] = model;
        }

        string axisTag = null;
        PatternPoint2 mirrorAxisStart = null;
        PatternPoint2 mirrorAxisEnd = null;
        PatternPoint2 mirrorAxisDirection = null;
        if (request.Mode == "mirror")
        {
            Curve axisCurve = FindSketchCornerCurve(sketch, request.AxisCurveId);
            if (axisCurve == null)
                throw new M2ModelException("CURVE_NOT_FOUND", "axis_curve_id does not identify native geometry in the requested Sketch");
            if (sourceTagsSeen.Contains(axisCurve.Tag.ToString()))
                throw new M2ModelException("INVALID_REQUEST", "axis_curve_id must identify a distinct native Line from every source curve");
            Line axisLine = axisCurve as Line;
            if (axisLine == null)
                throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "axis_curve_id must identify a native Line in the target Sketch");
            if (!sketch.IsSketchGeometry(axisLine))
                throw new M2ModelException("SKETCH_TARGET_MISMATCH", "axis_curve_id must belong to the explicitly requested Sketch");
            axisTag = axisLine.Tag.ToString();
            if (String.IsNullOrEmpty(axisTag) || sourceTagsSeen.Contains(axisTag))
                throw new M2ModelException("INVALID_REQUEST", "axis_curve_id must identify a distinct tagged native Line");
            Point3d axisStart3 = axisLine.StartPoint;
            Point3d axisEnd3 = axisLine.EndPoint;
            mirrorAxisStart = PatternPointToLocal(axisStart3, sketchOrigin, sketchXAxis, sketchYAxis, sketchNormal,
                "mirror axis start point");
            mirrorAxisEnd = PatternPointToLocal(axisEnd3, sketchOrigin, sketchXAxis, sketchYAxis, sketchNormal,
                "mirror axis end point");
            mirrorAxisDirection = new PatternPoint2(mirrorAxisEnd.X - mirrorAxisStart.X, mirrorAxisEnd.Y - mirrorAxisStart.Y);
            mirrorAxisDirection = PatternNormalize2(mirrorAxisDirection, "mirror axis direction");
        }

        Dictionary<string, CornerGeometrySnapshot> geometryBefore = CaptureSketchCornerGeometry(state.WorkPart);
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraintsBefore =
            CaptureAllSketchOffsetConstraints(state.WorkPart);
        Dictionary<string, string> framesBefore = CaptureAllSketchOffsetFrames(state.WorkPart);
        Dictionary<string, int> referenceStatesBefore = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
        Dictionary<string, List<string>> helperTagsBefore = CapturePatternPartHelperTags(state.WorkPart);
        bool dirtyBefore;
        try { dirtyBefore = state.WorkPart.IsModified; }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "could not capture Work Part modified state for F09 (" + ex.GetType().Name + ")");
        }
        List<M3BodyMeasure> bodiesBefore = CaptureM3BodyMeasures(state.WorkPart);
        List<string> featureTagsBefore = CaptureFeatureTags(state.WorkPart);
        string semanticBefore = CanonicalSemanticSnapshot(captured.Snapshot);
        string historyBefore = M2Json.CanonicalText(HistoryProjection(captured.Snapshot));
        string expressionsBefore = M2Json.CanonicalText(SnapshotList(captured.Snapshot, "expressions"));
        string fullGeometryBefore = CaptureFullGeometry(state.WorkPart);

        M3SketchPatternPlanState planState = new M3SketchPatternPlanState
        {
            Binding = binding,
            BindingState = state,
            RevisionId = binding.RevisionId,
            ActiveSketchTagBefore = activeSketchTagBefore,
            Request = CloneM3SketchPatternRequest(request),
            Sketch = sketch,
            SketchId = request.SketchId,
            SketchTag = sketch.Tag.ToString(),
            FeatureId = RequiredJournalId(sketch.Feature, "sketch feature"),
            FeatureTag = sketch.Feature.Tag.ToString(),
            SourceCurveIds = request.SourceCurveIds.ToArray(),
            SourceCurveTags = sourceTags,
            SourceCurveTypes = sourceTypes,
            SourceModels = sourceModels,
            AxisCurveTag = axisTag,
            SketchOrigin = sketchOrigin,
            SketchXAxis = sketchXAxis,
            SketchYAxis = sketchYAxis,
            SketchNormal = sketchNormal,
            MirrorAxisStart = mirrorAxisStart,
            MirrorAxisEnd = mirrorAxisEnd,
            MirrorAxisDirection = mirrorAxisDirection,
            DirtyBefore = dirtyBefore,
            SemanticBefore = semanticBefore,
            HistoryBefore = historyBefore,
            ExpressionsBefore = expressionsBefore,
            FullGeometryBefore = fullGeometryBefore,
            GeometryBefore = geometryBefore,
            ConstraintsBefore = constraintsBefore,
            FramesBefore = framesBefore,
            ReferenceStatesBefore = referenceStatesBefore,
            PartHelperTagsBefore = helperTagsBefore,
            BodiesBefore = bodiesBefore,
            FeatureTagsBefore = featureTagsBefore,
            NativeOperationEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
        };
        BuildPatternPositions(planState);
        BuildExpectedPatternInstances(planState);

        Dictionary<string, object> preview = new Dictionary<string, object>(StringComparer.Ordinal);
        preview["preview_type"] = "explicit_source_geometry_and_pattern_transforms";
        preview["native_geometry_evaluation"] = "deferred_until_atomic_execute";
        preview["source_curves"] = PatternSourceCurveRecords(planState);
        preview["mirror_axis_curve_id"] = request.Mode == "mirror" ? request.AxisCurveId : null;
        preview["expected_pattern_instance_count"] = planState.ExpectedPatternInstanceCount;
        preview["expected_output_curve_count"] = planState.ExpectedOutputCurveCount;
        preview["expected_instances"] = PatternExpectedInstanceRecords(planState);

        Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal);
        publicView["kind"] = "edit_sketch_pattern";
        publicView["revision_id"] = binding.RevisionId;
        publicView["sketch_id"] = request.SketchId;
        publicView["sketch_tag"] = planState.SketchTag;
        publicView["feature_id"] = planState.FeatureId;
        publicView["mode"] = request.Mode;
        publicView["source_curve_ids"] = StringListObjects(planState.SourceCurveIds);
        publicView["axis_curve_id"] = request.Mode == "mirror" ? request.AxisCurveId : null;
        publicView["expected_pattern_instance_count"] = planState.ExpectedPatternInstanceCount;
        publicView["expected_output_curve_count"] = planState.ExpectedOutputCurveCount;
        publicView["expected_instances"] = PatternExpectedInstanceRecords(planState);
        publicView["preview"] = preview;
        publicView["baseline_geometry_count"] = geometryBefore.Count;
        publicView["baseline_constraint_count"] = CountOffsetConstraints(constraintsBefore);
        publicView["baseline_reference_curve_count"] = referenceStatesBefore.Count;
        return new M3PlanSnapshot
        {
            Kind = "edit_sketch_pattern",
            RevisionId = binding.RevisionId,
            SketchId = request.SketchId,
            FeatureId = planState.FeatureId,
            PublicView = publicView,
            AdapterState = planState
        };
    }

    private static void ValidateSketchPatternRequest(M3SketchPatternRequest request)
    {
        if (request == null || String.IsNullOrEmpty(request.RevisionId) || String.IsNullOrEmpty(request.SketchId))
            throw new M2ModelException("INVALID_REQUEST", "revision_id and sketch_id are required for F09");
        if (request.SourceCurveIds == null || request.SourceCurveIds.Count < 1 || request.SourceCurveIds.Count > 256)
            throw new M2ModelException("INVALID_REQUEST", "source_curve_ids must contain 1 through 256 explicit curves");
        HashSet<string> sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in request.SourceCurveIds)
            if (String.IsNullOrWhiteSpace(id) || !sourceIds.Add(id))
                throw new M2ModelException("INVALID_REQUEST", "source_curve_ids must contain unique nonempty native IDs");
        if (request.Mode != "linear" && request.Mode != "rectangular" && request.Mode != "circular" && request.Mode != "mirror")
            throw new M2ModelException("INVALID_REQUEST", "mode must be linear, rectangular, circular, or mirror");

        long positions;
        if (request.Mode == "linear")
        {
            PatternValidateDirection(request.DirectionXY, "direction_xy");
            if (!IsFinite(request.SpacingMm) || request.SpacingMm <= 0.0)
                throw new M2ModelException("INVALID_REQUEST", "spacing_mm must be a positive finite millimeter value");
            if (request.InstanceCount < 2 || request.InstanceCount > 256)
                throw new M2ModelException("INVALID_REQUEST", "instance_count must be between 2 and 256 including the source position");
            positions = request.InstanceCount;
        }
        else if (request.Mode == "rectangular")
        {
            PatternValidateDirection(request.DirectionXXY, "direction_x_xy");
            PatternValidateDirection(request.DirectionYXY, "direction_y_xy");
            PatternPoint2 dx = PatternNormalize2(new PatternPoint2(request.DirectionXXY[0], request.DirectionXXY[1]), "direction_x_xy");
            PatternPoint2 dy = PatternNormalize2(new PatternPoint2(request.DirectionYXY[0], request.DirectionYXY[1]), "direction_y_xy");
            if (Math.Abs(dx.X * dy.X + dx.Y * dy.Y) > SketchPatternDirectionDotTolerance)
                throw new M2ModelException("INVALID_REQUEST", "direction_x_xy and direction_y_xy must be orthogonal");
            if (!IsFinite(request.SpacingXmm) || request.SpacingXmm <= 0.0 ||
                !IsFinite(request.SpacingYmm) || request.SpacingYmm <= 0.0)
                throw new M2ModelException("INVALID_REQUEST", "spacing_x_mm and spacing_y_mm must be positive finite millimeter values");
            if (request.CountX < 2 || request.CountY < 2)
                throw new M2ModelException("INVALID_REQUEST", "count_x and count_y must each be at least 2 including the source position");
            positions = (long)request.CountX * (long)request.CountY;
            if (positions > 256)
                throw new M2ModelException("INVALID_REQUEST", "count_x * count_y must not exceed 256 total positions");
        }
        else if (request.Mode == "circular")
        {
            PatternValidatePoint(request.CenterXY, "center_xy");
            if (!IsFinite(request.AngleStepDeg) || request.AngleStepDeg == 0.0 || Math.Abs(request.AngleStepDeg) >= 360.0)
                throw new M2ModelException("INVALID_REQUEST", "angle_step_deg must satisfy 0 < abs(angle_step_deg) < 360 degrees");
            if (request.InstanceCount < 2 || request.InstanceCount > 256)
                throw new M2ModelException("INVALID_REQUEST", "instance_count must be between 2 and 256 including the source position");
            List<double> seen = new List<double>();
            double normalizedStep = request.AngleStepDeg;
            for (int i = 0; i < request.InstanceCount; i++)
            {
                double angle = (normalizedStep * i) % 360.0;
                if (angle < 0.0) angle += 360.0;
                foreach (double prior in seen)
                {
                    double delta = Math.Abs(angle - prior);
                    if (Math.Min(delta, 360.0 - delta) <= 1.0e-9)
                        throw new M2ModelException("INVALID_REQUEST", "angle_step_deg and instance_count produce duplicate circular positions");
                }
                seen.Add(angle);
            }
            positions = request.InstanceCount;
        }
        else
        {
            if (String.IsNullOrWhiteSpace(request.AxisCurveId))
                throw new M2ModelException("INVALID_REQUEST", "axis_curve_id is required for mirror mode");
            if (sourceIds.Contains(request.AxisCurveId))
                throw new M2ModelException("INVALID_REQUEST", "axis_curve_id must not occur among source_curve_ids");
            positions = 2;
        }
        if (positions * request.SourceCurveIds.Count > 4096)
            throw new M2ModelException("INVALID_REQUEST", "total pattern positions times source_curve_ids count must not exceed 4096");
    }

    private static void PatternValidateDirection(double[] value, string name)
    {
        PatternValidatePoint(value, name);
        if (value[0] == 0.0 && value[1] == 0.0)
            throw new M2ModelException("INVALID_REQUEST", name + " must be nonzero");
    }

    private static void PatternValidatePoint(double[] value, string name)
    {
        if (value == null || value.Length != 2 || !IsFinite(value[0]) || !IsFinite(value[1]))
            throw new M2ModelException("INVALID_REQUEST", name + " must contain exactly two finite numbers");
    }

    private M3ExecutionResult ExecuteSketchPatternPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M3SketchPatternPlanState plan = prepared == null ? null : prepared.AdapterState as M3SketchPatternPlanState;
        if (plan == null || prepared.Kind != "edit_sketch_pattern")
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null,
                "INVALID_PLAN", "sketch pattern plan does not contain retained NX adapter state");
        BindingState state;
        try { state = RequireState(binding); }
        catch (M2ModelException ex)
        {
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, ex.Code, ex.Message);
        }
        if (!Object.ReferenceEquals(state, plan.BindingState) || !Object.ReferenceEquals(binding, plan.Binding) ||
            !String.Equals(plan.RevisionId, prepared.RevisionId, StringComparison.Ordinal) ||
            !String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "sketch pattern plan belongs to a different binding or revision");
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_BINDING", bindingReason);
        try
        {
            RevalidateSketchPatternPlan(binding, state, plan);
        }
        catch (M2ModelException ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "MODEL_READ_FAILED",
                "could not revalidate sketch pattern preconditions (" + ex.GetType().Name + ")");
        }

        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F09 sketch pattern modification"); }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "UPDATE_FAILED",
                "could not create an NX update mark (" + ex.GetType().Name + ")");
        }

        Exception operationException = null;
        bool updateCallAttempted = false;
        bool sketchActivated = false;
        bool allPostconditionsVerified = false;
        Dictionary<string, object> postconditionEvidence = null;
        plan.NativeOperationEvidence["undo_mark_created"] = true;
        plan.NativeOperationEvidence["precondition_revalidation"] = true;
        plan.NativeOperationEvidence["restore_on_failure_enabled"] = true;
        try
        {
            Sketch currentSketch = FindSketchByTag(state.WorkPart, plan.SketchTag);
            if (currentSketch == null) throw new M2ModelException("STALE_PLAN", "target Sketch Tag no longer resolves before execution");
            plan.Sketch = currentSketch;
            Curve[] sourceCurves = ResolvePatternSourceCurves(currentSketch, plan);
            Line mirrorAxis = ResolvePatternMirrorAxis(currentSketch, plan);
            if (state.Session.ActiveSketch != null)
                throw new M2ModelException("NX_BUSY", "a Sketch became active before F09 execution");
            currentSketch.Activate(Sketch.ViewReorient.False);
            sketchActivated = true;
            plan.NativeOperationEvidence["native_operation_called"] = true;
            V2SketchPatternNativeResult native = V2SketchPatternFactory.Execute(state.WorkPart, currentSketch,
                sourceCurves, mirrorAxis, CloneM3SketchPatternRequest(plan.Request));
            if (native == null)
                throw new M2ModelException("PATTERN_OUTPUT_INVALID", "NX sketch pattern factory returned no native operation result");
            plan.CommitReturnedNull = native.CommitObject == null;
            plan.NativeOperationEvidence["commit_returned_null"] = plan.CommitReturnedNull;
            plan.NativeOperationEvidence["native_commit_object_type"] = native.CommitObject == null ? null : native.CommitObject.GetType().Name;
            Dictionary<string, object> factoryEvidence = PatternCloneDictionary(native.Evidence);
            Dictionary<string, object> factoryConstraintEvidence;
            plan.FactoryConstraintFlagsClear = PatternFactoryConstraintFlagsClear(factoryEvidence, out factoryConstraintEvidence);
            plan.NativeOperationEvidence["factory_constraint_flags"] = factoryConstraintEvidence;
            string[] factoryOutputTags = native.OutputCurves == null ? new string[0] : PatternCurveTags(native.OutputCurves);
            plan.NativeOperationEvidence["factory_output_curve_tags"] = StringListObjects(factoryOutputTags);
            plan.NativeOperationEvidence["expected_pattern_instance_count"] = plan.ExpectedPatternInstanceCount;
            plan.NativeOperationEvidence["expected_output_curve_count"] = plan.ExpectedOutputCurveCount;
            plan.NativeOperationEvidence["factory"] = factoryEvidence;
            currentSketch.Update();
            currentSketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            sketchActivated = false;
            if (state.Session.ActiveSketch != null)
                throw new M2ModelException("NX_BUSY", "NX did not close the target Sketch before update verification");
            updateCallAttempted = true;
            int updateErrors = state.Session.UpdateManager.DoUpdate(mark);
            plan.NativeOperationEvidence["update_error_count"] = updateErrors;
            if (updateErrors != 0)
                throw new M2ModelException("UPDATE_FAILED", "NX update reported errors after the native sketch pattern call");

            SnapshotResult afterCapture = Capture(binding, state);
            plan.GeometryAfter = CaptureSketchCornerGeometry(state.WorkPart);
            plan.ConstraintsAfter = CaptureAllSketchOffsetConstraints(state.WorkPart);
            plan.FramesAfter = CaptureAllSketchOffsetFrames(state.WorkPart);
            plan.ReferenceStatesAfter = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
            plan.PartHelperTagsAfter = CapturePatternPartHelperTags(state.WorkPart);
            plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart);
            plan.FeatureTagsAfter = CaptureFeatureTags(state.WorkPart);
            plan.SemanticAfter = CanonicalSemanticSnapshot(afterCapture.Snapshot);
            plan.HistoryAfter = M2Json.CanonicalText(HistoryProjection(afterCapture.Snapshot));
            plan.ExpressionsAfter = M2Json.CanonicalText(SnapshotList(afterCapture.Snapshot, "expressions"));
            Dictionary<string, object> geometryDeltaEvidence;
            plan.OutputCurves = PatternOutputsFromGeometryDelta(plan.Sketch, plan, plan.GeometryAfter,
                factoryOutputTags, out plan.FactoryOutputListConsistent, out geometryDeltaEvidence);
            plan.OutputCurveTags = PatternCurveTags(plan.OutputCurves);
            plan.NativeOperationEvidence["geometry_delta"] = geometryDeltaEvidence;
            plan.NativeOperationEvidence["output_curve_tags_from_geometry_delta"] = StringListObjects(plan.OutputCurveTags);
            plan.NativeOperationEvidence["factory_output_list_consistent_with_geometry_delta"] = plan.FactoryOutputListConsistent;
            plan.NativeOperationEvidence["helper_tags_after"] = PatternCloneValue(plan.PartHelperTagsAfter);
            if (!plan.FactoryOutputListConsistent)
                throw new M2ModelException("PATTERN_OUTPUT_INVALID", "factory output list disagrees with the target Sketch geometry Tag delta");
            if (!plan.FactoryConstraintFlagsClear)
                throw new M2ModelException("PATTERN_CONSTRAINT_POLICY_FAILED", "NX pattern builder did not confirm CreateConstraint and spacing-expression creation are disabled");
            if (plan.OutputCurves.Length != plan.ExpectedOutputCurveCount)
                throw new M2ModelException("PATTERN_OUTPUT_INVALID", "new target Sketch geometry count does not match the expected source-by-position copy count");
            if (!Boolean.Equals(geometryDeltaEvidence["valid"], true))
                throw new M2ModelException("PATTERN_OUTPUT_INVALID", "target Sketch geometry delta contains unsupported or unresolved new geometry");
            postconditionEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            allPostconditionsVerified = VerifySketchPatternLive(binding, state, plan,
                plan.GeometryAfter, plan.ConstraintsAfter, plan.FramesAfter, plan.ReferenceStatesAfter,
                plan.BodiesAfter, plan.FeatureTagsAfter, afterCapture.Snapshot, postconditionEvidence);
            plan.AppliedEvidence = postconditionEvidence;
            plan.NativeOperationEvidence["postconditions_verified_before_undo_mark_close"] = allPostconditionsVerified;
            if (!allPostconditionsVerified)
                throw new M2ModelException("PATTERN_VERIFY_FAILED", "native pattern geometry or preserved Sketch state did not match the requested pattern");
        }
        catch (Exception ex)
        {
            operationException = ex;
            try
            {
                if (ex.Data != null && ex.Data.Contains("nx_builder_destroy_exception_type"))
                    plan.NativeOperationEvidence["nx_builder_destroy_exception_type"] =
                        ex.Data["nx_builder_destroy_exception_type"];
                if (ex.Data != null && ex.Data.Contains("nx_builder_destroy_exception_message"))
                    plan.NativeOperationEvidence["nx_builder_destroy_exception_message"] =
                        ex.Data["nx_builder_destroy_exception_message"];
            }
            catch (Exception evidenceException)
            {
                plan.NativeOperationEvidence["builder_destroy_evidence_read_exception_type"] = evidenceException.GetType().FullName;
            }
            if (sketchActivated)
            {
                try
                {
                    Sketch active = FindSketchByTag(state.WorkPart, plan.SketchTag);
                    if (active != null && IsSketchActive(state.Session, active))
                        active.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
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
        }

        if (operationException == null && allPostconditionsVerified)
        {
            try
            {
                state.Session.DeleteUndoMark(mark, "");
                plan.NativeOperationEvidence["undo_mark_closed"] = true;
            }
            catch (Exception closeException)
            {
                plan.NativeOperationEvidence["undo_mark_closed"] = false;
                plan.NativeOperationEvidence["undo_mark_close_exception_type"] = closeException.GetType().FullName;
                plan.NativeOperationEvidence["undo_mark_close_exception_message"] = SafeOffsetExceptionMessage(closeException);
                operationException = new M2ModelException("UNDO_MARK_CLOSE_FAILED", "NX did not confirm closure of the F09 protection mark");
            }
            if (operationException == null)
            {
                Dictionary<string, object> evidence = BuildSketchPatternEvidence(plan, postconditionEvidence);
                evidence["protection"] = PatternProtectionEvidence(plan, true);
                Dictionary<string, object> result = BuildSketchPatternExecutionResult(plan, binding.RevisionId,
                    "APPLIED", true, evidence);
                plan.Execution = M3Execution("APPLIED", binding.RevisionId, result, null, null);
                return plan.Execution;
            }
        }

        M3ExecutionResult restored = RestoreSketchPatternPlan(binding, state, plan, mark,
            operationException, updateCallAttempted);
        plan.Execution = restored;
        return restored;
    }

    private M3VerificationResult VerifySketchPatternPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M3SketchPatternPlanState plan = prepared == null ? null : prepared.AdapterState as M3SketchPatternPlanState;
        if (plan == null || prepared.Kind != "edit_sketch_pattern" || plan.Execution == null || plan.Execution.State != "APPLIED")
            return M3VerificationFailure(binding == null ? null : binding.RevisionId,
                "sketch pattern verification requires a successfully applied plan with closed UndoMark evidence");
        BindingState state = RequireState(binding);
        string bindingReason;
        if (!Object.ReferenceEquals(state, plan.BindingState) || !Object.ReferenceEquals(binding, plan.Binding) ||
            !IsBindingCurrent(binding, out bindingReason))
            return M3VerificationFailure(binding == null ? null : binding.RevisionId,
                "NX binding changed before sketch pattern verification");

        Dictionary<string, object> measured = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        bool verified = false;
        try
        {
            SnapshotResult captured = Capture(binding, state);
            Dictionary<string, CornerGeometrySnapshot> geometryNow = CaptureSketchCornerGeometry(state.WorkPart);
            Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraintsNow = CaptureAllSketchOffsetConstraints(state.WorkPart);
            Dictionary<string, string> framesNow = CaptureAllSketchOffsetFrames(state.WorkPart);
            Dictionary<string, int> referencesNow = CaptureAllSketchOffsetReferenceStates(state.WorkPart);
            List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart);
            List<string> featureTagsNow = CaptureFeatureTags(state.WorkPart);
            verified = VerifySketchPatternLive(binding, state, plan, geometryNow, constraintsNow,
                framesNow, referencesNow, bodiesNow, featureTagsNow, captured.Snapshot, measured);
            object liveChecks;
            if (measured.TryGetValue("checks", out liveChecks))
                checks = liveChecks as Dictionary<string, object> ?? checks;
        }
        catch (Exception ex)
        {
            measured["verification_exception_type"] = ex.GetType().FullName;
            measured["verification_exception_message"] = SafeOffsetExceptionMessage(ex);
            checks = PatternFailedChecks(measured);
        }
        measured["native_operation"] = PatternCloneDictionary(plan.NativeOperationEvidence);
        Dictionary<string, object> evidence = BuildSketchPatternEvidence(plan, measured);
        evidence["protection"] = PatternProtectionEvidence(plan, true);
        Dictionary<string, object> result = BuildSketchPatternExecutionResult(plan, binding.RevisionId,
            plan.Execution.State, verified, evidence);
        return new M3VerificationResult
        {
            Verified = verified,
            RevisionId = binding.RevisionId,
            Checks = checks,
            Result = result,
            ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified ? null : "Live pattern instances, source geometry, Sketch state, history, or body measures no longer match the applied plan."
        };
    }

    private void RevalidateSketchPatternPlan(M2ModelBinding binding, BindingState state, M3SketchPatternPlanState plan)
    {
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "a Sketch is active in NX before sketch pattern execution");
        SnapshotResult current = Capture(binding, state);
        if (!String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal) ||
            !String.Equals(CanonicalSemanticSnapshot(current.Snapshot), plan.SemanticBefore, StringComparison.Ordinal) ||
            !String.Equals(CaptureFullGeometry(state.WorkPart), plan.FullGeometryBefore, StringComparison.Ordinal))
            throw new M2ModelException("STALE_PLAN", "semantic model or geometry changed after sketch pattern preparation");
        if (!OffsetConstraintMapsEqual(plan.ConstraintsBefore, CaptureAllSketchOffsetConstraints(state.WorkPart)))
            throw new M2ModelException("STALE_PLAN", "Sketch constraints changed after sketch pattern preparation");
        if (!OffsetStringMapsEqual(plan.FramesBefore, CaptureAllSketchOffsetFrames(state.WorkPart)))
            throw new M2ModelException("STALE_PLAN", "Sketch frames changed after sketch pattern preparation");
        if (!OffsetIntMapsEqual(plan.ReferenceStatesBefore, CaptureAllSketchOffsetReferenceStates(state.WorkPart)))
            throw new M2ModelException("STALE_PLAN", "Sketch reference status changed after sketch pattern preparation");
        if (!PatternHelperTagInventoriesEqual(plan.PartHelperTagsBefore, CapturePatternPartHelperTags(state.WorkPart)))
            throw new M2ModelException("STALE_PLAN", "Part Point, Axis, or Direction Tag inventories changed after pattern preparation");
        Dictionary<string, object> bodyEvidence;
        if (!BodyMeasuresEqual(plan.BodiesBefore, CaptureM3BodyMeasures(state.WorkPart), out bodyEvidence))
            throw new M2ModelException("STALE_GEOMETRY", "body measurements changed after sketch pattern preparation");
        Sketch currentSketch = FindSketchByTag(state.WorkPart, plan.SketchTag);
        if (currentSketch == null || currentSketch.Feature == null ||
            !String.Equals(currentSketch.Feature.Tag.ToString(), plan.FeatureTag, StringComparison.Ordinal))
            throw new M2ModelException("STALE_PLAN", "target Sketch or feature Tag changed after pattern preparation");
        ResolvePatternSourceCurves(currentSketch, plan);
        ResolvePatternMirrorAxis(currentSketch, plan);
        string activeSketchTag = state.Session.ActiveSketch == null ? null : state.Session.ActiveSketch.Tag.ToString();
        if (!String.Equals(plan.ActiveSketchTagBefore, activeSketchTag, StringComparison.Ordinal))
            throw new M2ModelException("STALE_PLAN", "NX active Sketch state changed after pattern preparation");
    }

    private static Curve[] ResolvePatternSourceCurves(Sketch sketch, M3SketchPatternPlanState plan)
    {
        Curve[] result = new Curve[plan.SourceCurveTags.Length];
        for (int i = 0; i < result.Length; i++)
        {
            Curve curve = FindCornerCurveByTag(sketch, plan.SourceCurveTags[i]);
            if (curve == null || !sketch.IsSketchGeometry(curve) ||
                !String.Equals(curve.GetType().Name, plan.SourceCurveTypes[i], StringComparison.Ordinal) ||
                (!(curve is Line) && !(curve is Arc)))
                throw new M2ModelException("STALE_PLAN", "a source curve Tag no longer resolves to the same supported geometry in the target Sketch");
            result[i] = curve;
        }
        return result;
    }

    private static Line ResolvePatternMirrorAxis(Sketch sketch, M3SketchPatternPlanState plan)
    {
        if (plan.Request.Mode != "mirror") return null;
        Curve curve = FindCornerCurveByTag(sketch, plan.AxisCurveTag);
        Line line = curve as Line;
        if (line == null || !sketch.IsSketchGeometry(line))
            throw new M2ModelException("STALE_PLAN", "mirror axis Tag no longer resolves to a native Line in the target Sketch");
        for (int i = 0; i < plan.SourceCurveTags.Length; i++)
            if (String.Equals(plan.SourceCurveTags[i], plan.AxisCurveTag, StringComparison.Ordinal))
                throw new M2ModelException("STALE_PLAN", "mirror axis Tag is also present among source curve Tags");
        return line;
    }

    private bool VerifySketchPatternLive(M2ModelBinding binding, BindingState state, M3SketchPatternPlanState plan,
        Dictionary<string, CornerGeometrySnapshot> geometryNow,
        Dictionary<string, Dictionary<string, CornerConstraintSnapshot>> constraintsNow,
        Dictionary<string, string> framesNow, Dictionary<string, int> referencesNow,
        List<M3BodyMeasure> bodiesNow, List<string> featureTagsNow,
        Dictionary<string, object> semanticSnapshot, Dictionary<string, object> measured)
    {
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        measured["checks"] = checks;
        bool patternInstances = false;
        bool sourcesPreserved = false;
        bool constraintsPreserved = false;
        bool framesPreserved = false;
        bool referencesPreserved = false;
        bool nonTargetsPreserved = false;
        bool historyVerified = false;
        bool bodiesPreserved = false;
        bool solverVerified = false;
        Dictionary<string, object> instanceEvidence = null;
        Dictionary<string, object> sourceEvidence = null;
        Dictionary<string, object> constraintEvidence = null;
        Dictionary<string, object> frameEvidence = null;
        Dictionary<string, object> referenceEvidence = null;
        Dictionary<string, object> nonTargetEvidence = null;
        Dictionary<string, object> historyEvidence = null;
        Dictionary<string, object> bodyEvidence = null;
        Dictionary<string, object> solverEvidence = null;
        try
        {
            Sketch currentSketch = FindSketchByTag(state.WorkPart, plan.SketchTag);
            bool sketchIdentity = currentSketch != null && currentSketch.Feature != null &&
                String.Equals(currentSketch.Feature.Tag.ToString(), plan.FeatureTag, StringComparison.Ordinal);
            Curve[] liveOutputs = new Curve[plan.OutputCurveTags == null ? 0 : plan.OutputCurveTags.Length];
            bool outputsResolved = sketchIdentity && liveOutputs.Length == plan.ExpectedOutputCurveCount;
            for (int i = 0; i < liveOutputs.Length && sketchIdentity; i++)
            {
                liveOutputs[i] = FindCornerCurveByTag(currentSketch, plan.OutputCurveTags[i]);
                if (liveOutputs[i] == null || !currentSketch.IsSketchGeometry(liveOutputs[i]) ||
                    plan.GeometryBefore.ContainsKey(plan.OutputCurveTags[i]) ||
                    Array.IndexOf(plan.SourceCurveTags, plan.OutputCurveTags[i]) >= 0 ||
                    String.Equals(plan.OutputCurveTags[i], plan.AxisCurveTag, StringComparison.Ordinal)) outputsResolved = false;
            }
            Dictionary<string, object> matchingEvidence = null;
            patternInstances = false;
            if (sketchIdentity && outputsResolved)
                patternInstances = MatchPatternOutputs(plan, liveOutputs, out matchingEvidence) && plan.FactoryOutputListConsistent;
            if (matchingEvidence == null)
                matchingEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "valid", false },
                    { "reason", "target Sketch or one of the retained output curve Tags did not resolve" }
                };
            instanceEvidence = matchingEvidence;
            instanceEvidence["target_sketch_tag_resolved"] = sketchIdentity;
            instanceEvidence["output_curve_tags_resolved"] = outputsResolved;
            instanceEvidence["expected_pattern_instance_count"] = plan.ExpectedPatternInstanceCount;
            instanceEvidence["expected_output_curve_count"] = plan.ExpectedOutputCurveCount;
            instanceEvidence["factory_output_list_consistent_with_geometry_delta"] = plan.FactoryOutputListConsistent;
            Dictionary<string, object> helperInventoryEvidence;
            bool helperInventoryClean = PatternHelperCleanupMatches(plan,
                CapturePatternPartHelperTags(state.WorkPart), out helperInventoryEvidence);
            patternInstances = patternInstances && helperInventoryClean;
            instanceEvidence["helper_cleanup"] = helperInventoryEvidence;
            measured["pattern_instances"] = instanceEvidence;

            List<object> sourceRecords = new List<object>();
            List<object> sourceMismatches = new List<object>();
            for (int i = 0; i < plan.SourceCurveTags.Length; i++)
            {
                CornerGeometrySnapshot before;
                CornerGeometrySnapshot current;
                bool foundBefore = plan.GeometryBefore.TryGetValue(plan.SourceCurveTags[i], out before);
                bool foundNow = geometryNow.TryGetValue(plan.SourceCurveTags[i], out current);
                bool same = foundBefore && foundNow &&
                    String.Equals(before.Signature, current.Signature, StringComparison.Ordinal) &&
                    String.Equals(current.SketchTag, plan.SketchTag, StringComparison.Ordinal) &&
                    String.Equals(current.ObjectType, plan.SourceCurveTypes[i], StringComparison.Ordinal);
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "source_curve_id", plan.SourceCurveIds[i] },
                    { "source_curve_tag", plan.SourceCurveTags[i] },
                    { "preserved", same }
                };
                sourceRecords.Add(item);
                if (!same) sourceMismatches.Add(item);
            }
            sourcesPreserved = sourceMismatches.Count == 0;
            sourceEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "count", plan.SourceCurveTags.Length },
                { "preserved", sourceRecords },
                { "mismatches", sourceMismatches }
            };
            measured["source_curves"] = sourceEvidence;

            string expressionState = M2Json.CanonicalText(SnapshotList(semanticSnapshot, "expressions"));
            bool constraintsMatchBefore = OffsetConstraintMapsEqual(plan.ConstraintsBefore, plan.ConstraintsAfter);
            bool constraintsMatchExecute = OffsetConstraintMapsEqual(plan.ConstraintsAfter, constraintsNow);
            bool expressionsMatchBefore = String.Equals(plan.ExpressionsBefore, plan.ExpressionsAfter, StringComparison.Ordinal);
            bool expressionsPreserved = String.Equals(plan.ExpressionsAfter, expressionState, StringComparison.Ordinal);
            constraintsPreserved = constraintsMatchBefore && constraintsMatchExecute && expressionsMatchBefore &&
                expressionsPreserved && plan.FactoryConstraintFlagsClear;
            object createConstraintFlag = null;
            object createSpacingFlag = null;
            Dictionary<string, object> factoryConstraintEvidence = plan.NativeOperationEvidence.ContainsKey("factory_constraint_flags")
                ? plan.NativeOperationEvidence["factory_constraint_flags"] as Dictionary<string, object> : null;
            if (factoryConstraintEvidence != null)
            {
                factoryConstraintEvidence.TryGetValue("create_constraint", out createConstraintFlag);
                factoryConstraintEvidence.TryGetValue("create_spacing_expression", out createSpacingFlag);
            }
            constraintEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "constraints_unchanged_from_before", constraintsMatchBefore },
                { "constraints_match_execute", constraintsMatchExecute },
                { "expressions_unchanged_from_before", expressionsMatchBefore },
                { "expressions_match_execute", expressionsPreserved },
                { "factory_flags_confirmed_clear", plan.FactoryConstraintFlagsClear },
                { "create_constraint", createConstraintFlag },
                { "create_spacing_expression", createSpacingFlag }
            };
            measured["sketch_constraints"] = constraintEvidence;

            bool framesMatchBefore = OffsetStringMapsEqual(plan.FramesBefore, plan.FramesAfter);
            bool framesMatchExecute = OffsetStringMapsEqual(plan.FramesAfter, framesNow);
            framesPreserved = framesMatchBefore && framesMatchExecute;
            frameEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "unchanged_from_before", framesMatchBefore },
                { "matches_execute", framesMatchExecute }
            };
            measured["sketch_frames"] = frameEvidence;

            Dictionary<string, object> referenceAfterEvidence;
            bool referencesMatchBefore = PatternReferenceStatesMatch(plan, plan.ReferenceStatesAfter, out referenceAfterEvidence);
            bool referencesMatchExecute = PatternReferenceStatesMatch(plan, referencesNow, out referenceEvidence);
            referencesPreserved = referencesMatchBefore && referencesMatchExecute;
            referenceEvidence["after_snapshot_matches_baseline_and_outputs"] = referencesMatchBefore;
            referenceEvidence["after_snapshot"] = referenceAfterEvidence;
            measured["reference_status"] = referenceEvidence;

            Dictionary<string, object> nonTargetAfterEvidence;
            bool nonTargetsMatchAtExecute = PatternNonTargetGeometryMatches(plan, plan.GeometryAfter, out nonTargetAfterEvidence);
            bool nonTargetsMatchLive = PatternNonTargetGeometryMatches(plan, geometryNow, out nonTargetEvidence);
            nonTargetsPreserved = nonTargetsMatchAtExecute && nonTargetsMatchLive;
            nonTargetEvidence["after_snapshot_matches_baseline_delta"] = nonTargetsMatchAtExecute;
            nonTargetEvidence["after_snapshot"] = nonTargetAfterEvidence;
            measured["non_target_geometry"] = nonTargetEvidence;

            bool semanticMatches = String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(semanticSnapshot), StringComparison.Ordinal);
            string currentHistory = M2Json.CanonicalText(HistoryProjection(semanticSnapshot));
            bool historyMatches = String.Equals(plan.HistoryAfter, currentHistory, StringComparison.Ordinal);
            bool historyUnchanged = String.Equals(plan.HistoryBefore, plan.HistoryAfter, StringComparison.Ordinal);
            bool featuresMatchExecute = FeatureTagsMatch(plan.FeatureTagsAfter, featureTagsNow);
            bool featuresUnchanged = FeatureTagsMatch(plan.FeatureTagsBefore, plan.FeatureTagsAfter);
            historyVerified = semanticMatches && historyMatches && historyUnchanged &&
                featuresMatchExecute && featuresUnchanged &&
                !String.Equals(plan.SemanticBefore, plan.SemanticAfter, StringComparison.Ordinal);
            historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "semantic_matches_execute", semanticMatches },
                { "history_matches_execute", historyMatches },
                { "history_unchanged_from_before", historyUnchanged },
                { "feature_tags_unchanged_from_before", featuresUnchanged },
                { "feature_tags_match_execute", featuresMatchExecute },
                { "commit_returned_null", plan.CommitReturnedNull }
            };
            measured["history"] = historyEvidence;

            Dictionary<string, object> bodyAfterEvidence;
            Dictionary<string, object> bodyLiveEvidence;
            bool bodiesMatchBefore = BodyMeasuresEqual(plan.BodiesBefore, plan.BodiesAfter, out bodyAfterEvidence);
            bool bodiesMatchExecute = BodyMeasuresEqual(plan.BodiesAfter, bodiesNow, out bodyLiveEvidence);
            bodiesPreserved = bodiesMatchBefore && bodiesMatchExecute;
            bodyEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "unchanged_from_before", bodiesMatchBefore },
                { "matches_execute", bodiesMatchExecute },
                { "after_snapshot", bodyAfterEvidence },
                { "live_snapshot", bodyLiveEvidence }
            };
            measured["bodies"] = bodyEvidence;

            solverEvidence = CaptureCornerSketchStatus(currentSketch);
            solverVerified = Boolean.Equals(solverEvidence["acceptable"], true);
            measured["sketch_solver_status"] = solverEvidence;
        }
        catch (Exception ex)
        {
            patternInstances = false;
            sourcesPreserved = false;
            constraintsPreserved = false;
            framesPreserved = false;
            referencesPreserved = false;
            nonTargetsPreserved = false;
            historyVerified = false;
            bodiesPreserved = false;
            solverVerified = false;
            measured["verification_exception_type"] = ex.GetType().FullName;
            measured["verification_exception_message"] = SafeOffsetExceptionMessage(ex);
        }

        Dictionary<string, object> patternCheck = VerificationCheck(patternInstances ? "VERIFIED" : "MISMATCH", instanceEvidence);
        object helperCleanupEvidence = null;
        if (instanceEvidence != null && instanceEvidence.TryGetValue("helper_cleanup", out helperCleanupEvidence))
            patternCheck["helper_cleanup"] = helperCleanupEvidence;
        checks["pattern_instances"] = patternCheck;
        checks["source_curves"] = VerificationCheck(sourcesPreserved ? "VERIFIED" : "MISMATCH", sourceEvidence);
        checks["sketch_constraints"] = VerificationCheck(constraintsPreserved ? "VERIFIED" : "MISMATCH", constraintEvidence);
        checks["sketch_frames"] = VerificationCheck(framesPreserved ? "VERIFIED" : "MISMATCH", frameEvidence);
        checks["reference_status"] = VerificationCheck(referencesPreserved ? "VERIFIED" : "MISMATCH", referenceEvidence);
        checks["non_target_geometry"] = VerificationCheck(nonTargetsPreserved ? "VERIFIED" : "MISMATCH", nonTargetEvidence);
        checks["history"] = VerificationCheck(historyVerified ? "VERIFIED" : "MISMATCH", historyEvidence);
        checks["bodies"] = VerificationCheck(bodiesPreserved ? "VERIFIED" : "MISMATCH", bodyEvidence);
        checks["sketch_solver_status"] = VerificationCheck(solverVerified ? "VERIFIED" : "MISMATCH", solverEvidence);
        return patternInstances && sourcesPreserved && constraintsPreserved && framesPreserved &&
            referencesPreserved && nonTargetsPreserved && historyVerified && bodiesPreserved && solverVerified;
    }

    private static bool MatchPatternOutputs(M3SketchPatternPlanState plan, Curve[] outputs,
        out Dictionary<string, object> evidence)
    {
        List<PatternExpectedInstance> expectedList = new List<PatternExpectedInstance>();
        foreach (PatternExpectedInstance instance in plan.ExpectedInstances)
            if (!instance.IsSourceInstance)
            {
                instance.OutputCurveTag = null;
                expectedList.Add(instance);
            }
        List<object> mappingRecords = new List<object>();
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (outputs == null || outputs.Length != expectedList.Count || outputs.Length != plan.ExpectedOutputCurveCount)
        {
            evidence["valid"] = false;
            evidence["reason"] = "native output and expected cloned instance counts differ";
            evidence["expected_output_count"] = expectedList.Count;
            evidence["actual_output_count"] = outputs == null ? 0 : outputs.Length;
            evidence["mapping"] = mappingRecords;
            return false;
        }

        PatternCurveModel[] actualModels = new PatternCurveModel[outputs.Length];
        bool actualModelsValid = true;
        for (int i = 0; i < outputs.Length; i++)
        {
            string reason;
            if (outputs[i] == null || !(outputs[i] is Line) && !(outputs[i] is Arc) ||
                !plan.Sketch.IsSketchGeometry(outputs[i]) ||
                !TryCreatePatternCurveModel(outputs[i], plan.SketchOrigin, plan.SketchXAxis, plan.SketchYAxis,
                    plan.SketchNormal, null, outputs[i] == null ? null : outputs[i].Tag.ToString(),
                    out actualModels[i], out reason))
            {
                actualModelsValid = false;
                actualModels[i] = null;
            }
        }
        if (!actualModelsValid)
        {
            evidence["valid"] = false;
            evidence["reason"] = "one or more native outputs are unsupported, outside the target Sketch, or not planar Line/Arc geometry";
            evidence["mapping"] = mappingRecords;
            return false;
        }

        bool[,] edges = new bool[expectedList.Count, outputs.Length];
        for (int left = 0; left < expectedList.Count; left++)
            for (int right = 0; right < outputs.Length; right++)
                edges[left, right] = PatternCurveModelsMatch(expectedList[left].Geometry, actualModels[right], null);
        int[] pairExpected = new int[expectedList.Count];
        int[] pairOutput = new int[outputs.Length];
        int[] distance = new int[expectedList.Count];
        for (int i = 0; i < pairExpected.Length; i++) pairExpected[i] = -1;
        for (int i = 0; i < pairOutput.Length; i++) pairOutput[i] = -1;
        int matched = 0;
        while (PatternMatchingBreadthFirst(edges, pairExpected, pairOutput, distance))
            for (int left = 0; left < pairExpected.Length; left++)
                if (pairExpected[left] < 0 && PatternMatchingDepthFirst(left, edges, pairExpected, pairOutput, distance)) matched++;

        if (matched == expectedList.Count)
        {
            for (int i = 0; i < expectedList.Count; i++)
            {
                int outputIndex = pairExpected[i];
                PatternExpectedInstance expected = expectedList[i];
                expected.OutputCurveTag = outputs[outputIndex].Tag.ToString();
                Dictionary<string, object> geometricResiduals = new Dictionary<string, object>(StringComparer.Ordinal);
                bool geometryMatches = PatternCurveModelsMatch(expected.Geometry, actualModels[outputIndex], geometricResiduals);
                mappingRecords.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "source_curve_id", expected.SourceCurveId },
                    { "position_index", expected.PositionIndex },
                    { "position_x_index", expected.HasRectangularIndices ? (object)expected.PositionXIndex : null },
                    { "position_y_index", expected.HasRectangularIndices ? (object)expected.PositionYIndex : null },
                    { "output_curve_tag", expected.OutputCurveTag },
                    { "expected_curve_record", expected.ExpectedCurveRecord },
                    { "actual_curve_record", PatternCurveRecord(actualModels[outputIndex], plan.SketchOrigin, plan.SketchXAxis, plan.SketchYAxis, plan.SketchNormal) },
                    { "geometric_match", geometryMatches },
                    { "geometry_residuals", geometricResiduals }
                });
            }
        }
        evidence["valid"] = matched == expectedList.Count;
        evidence["expected_output_count"] = expectedList.Count;
        evidence["actual_output_count"] = outputs.Length;
        evidence["matched_output_count"] = matched;
        evidence["one_to_one_mapping"] = matched == expectedList.Count;
        evidence["mapping"] = mappingRecords;
        return matched == expectedList.Count;
    }

    private static bool PatternMatchingBreadthFirst(bool[,] edges, int[] pairExpected,
        int[] pairOutput, int[] distance)
    {
        Queue<int> queue = new Queue<int>();
        for (int i = 0; i < pairExpected.Length; i++)
        {
            if (pairExpected[i] < 0) { distance[i] = 0; queue.Enqueue(i); }
            else distance[i] = -1;
        }
        bool augmentingPath = false;
        while (queue.Count > 0)
        {
            int expected = queue.Dequeue();
            for (int output = 0; output < pairOutput.Length; output++)
            {
                if (!edges[expected, output]) continue;
                int paired = pairOutput[output];
                if (paired < 0) augmentingPath = true;
                else if (distance[paired] < 0)
                {
                    distance[paired] = distance[expected] + 1;
                    queue.Enqueue(paired);
                }
            }
        }
        return augmentingPath;
    }

    private static bool PatternMatchingDepthFirst(int expected, bool[,] edges,
        int[] pairExpected, int[] pairOutput, int[] distance)
    {
        for (int output = 0; output < pairOutput.Length; output++)
        {
            if (!edges[expected, output]) continue;
            int paired = pairOutput[output];
            if (paired < 0 || (distance[paired] == distance[expected] + 1 &&
                PatternMatchingDepthFirst(paired, edges, pairExpected, pairOutput, distance)))
            {
                pairExpected[expected] = output;
                pairOutput[output] = expected;
                return true;
            }
        }
        distance[expected] = -1;
        return false;
    }

    private static bool PatternCurveModelsMatch(PatternCurveModel expected,
        PatternCurveModel actual, Dictionary<string, object> residuals)
    {
        if (expected == null || actual == null || expected.IsArc != actual.IsArc ||
            !String.Equals(expected.ObjectType, actual.ObjectType, StringComparison.Ordinal)) return false;
        if (!expected.IsArc)
        {
            double direct = Math.Max(PatternDistance(expected.Start, actual.Start), PatternDistance(expected.End, actual.End));
            double reverse = Math.Max(PatternDistance(expected.Start, actual.End), PatternDistance(expected.End, actual.Start));
            double endpointResidual = Math.Min(direct, reverse);
            double expectedLength = PatternDistance(expected.Start, expected.End);
            double lengthResidual = Math.Abs(expectedLength - actual.LengthMm);
            if (residuals != null)
            {
                residuals["endpoint_residual_mm"] = endpointResidual;
                residuals["intrinsic_length_residual_mm"] = lengthResidual;
            }
            return IsFinite(endpointResidual) && endpointResidual <= SketchPatternPositionToleranceMm &&
                IsFinite(lengthResidual) && lengthResidual <= SketchPatternPositionToleranceMm;
        }
        double centerResidual = PatternDistance(expected.Center, actual.Center);
        double radiusResidual = Math.Abs(expected.Radius - actual.Radius);
        double expectedArcLength = expected.IsFullCircle
            ? 2.0 * Math.PI * expected.Radius : expected.Radius * Math.Abs(expected.SweepRad);
        double lengthResidualArc = Math.Abs(expectedArcLength - actual.LengthMm);
        bool full = expected.IsFullCircle && actual.IsFullCircle;
        double endpointResidualArc = full ? 0.0 : Math.Min(
            Math.Max(PatternDistance(expected.Start, actual.Start), PatternDistance(expected.End, actual.End)),
            Math.Max(PatternDistance(expected.Start, actual.End), PatternDistance(expected.End, actual.Start)));
        double midpointResidual = full ? 0.0 : PatternDistance(expected.Mid, actual.Mid);
        double sweepResidual = full ? 0.0 : Math.Abs(Math.Abs(expected.SweepRad) - Math.Abs(actual.SweepRad));
        if (residuals != null)
        {
            residuals["center_residual_mm"] = centerResidual;
            residuals["radius_residual_mm"] = radiusResidual;
            residuals["intrinsic_length_residual_mm"] = lengthResidualArc;
            residuals["endpoint_residual_mm"] = endpointResidualArc;
            residuals["midpoint_residual_mm"] = midpointResidual;
            residuals["sweep_residual_rad"] = sweepResidual;
            residuals["full_circle"] = full;
        }
        return IsFinite(centerResidual) && centerResidual <= SketchPatternPositionToleranceMm &&
            IsFinite(radiusResidual) && radiusResidual <= SketchPatternPositionToleranceMm &&
            IsFinite(lengthResidualArc) && lengthResidualArc <= SketchPatternPositionToleranceMm &&
            IsFinite(endpointResidualArc) && endpointResidualArc <= SketchPatternPositionToleranceMm &&
            IsFinite(midpointResidual) && midpointResidual <= SketchPatternPositionToleranceMm &&
            (full || IsFinite(sweepResidual) && sweepResidual <= SketchPatternAngleToleranceRad);
    }

    private static bool PatternReferenceStatesMatch(M3SketchPatternPlanState plan,
        Dictionary<string, int> current, out Dictionary<string, object> evidence)
    {
        List<object> changed = new List<object>();
        List<object> missing = new List<object>();
        List<object> outputStatuses = new List<object>();
        HashSet<string> outputTags = new HashSet<string>(plan.OutputCurveTags ?? new string[0], StringComparer.Ordinal);
        int preserved = 0;
        foreach (KeyValuePair<string, int> pair in plan.ReferenceStatesBefore)
        {
            int status;
            if (!current.TryGetValue(pair.Key, out status)) missing.Add(pair.Key);
            else if (status != pair.Value) changed.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", pair.Key }, { "before", pair.Value }, { "after", status }
            });
            else preserved++;
        }
        bool outputStatusesActive = true;
        foreach (string tag in outputTags)
        {
            int status;
            bool found = current.TryGetValue(tag, out status);
            outputStatuses.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", tag }, { "found", found }, { "status", found ? (object)status : null }
            });
            if (!found || status != 1) outputStatusesActive = false;
        }
        List<object> unexpected = new List<object>();
        foreach (KeyValuePair<string, int> pair in current)
            if (!plan.ReferenceStatesBefore.ContainsKey(pair.Key) && !outputTags.Contains(pair.Key)) unexpected.Add(pair.Key);
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "baseline_curve_count", plan.ReferenceStatesBefore.Count },
            { "preserved_count", preserved },
            { "missing_tags", missing },
            { "changed", changed },
            { "output_reference_status", outputStatuses },
            { "output_curves_active", outputStatusesActive },
            { "unexpected_new_curve_tags", unexpected },
            { "status_values", new Dictionary<string, object>(StringComparer.Ordinal) { { "reference", 0 }, { "active", 1 } } }
        };
        return missing.Count == 0 && changed.Count == 0 && outputStatusesActive && unexpected.Count == 0 &&
            current.Count == plan.ReferenceStatesBefore.Count + outputTags.Count;
    }

    private static bool PatternNonTargetGeometryMatches(M3SketchPatternPlanState plan,
        Dictionary<string, CornerGeometrySnapshot> current, out Dictionary<string, object> evidence)
    {
        List<object> missing = new List<object>();
        List<object> changed = new List<object>();
        List<string> unexpectedNew = new List<string>();
        HashSet<string> sourceTags = new HashSet<string>(plan.SourceCurveTags, StringComparer.Ordinal);
        HashSet<string> outputTags = new HashSet<string>(plan.OutputCurveTags ?? new string[0], StringComparer.Ordinal);
        int preserved = 0;
        foreach (KeyValuePair<string, CornerGeometrySnapshot> pair in plan.GeometryBefore)
        {
            if (sourceTags.Contains(pair.Key)) continue;
            CornerGeometrySnapshot now;
            if (!current.TryGetValue(pair.Key, out now) || now == null) missing.Add(pair.Key);
            else if (!String.Equals(pair.Value.Signature, now.Signature, StringComparison.Ordinal) ||
                !String.Equals(pair.Value.SketchTag, now.SketchTag, StringComparison.Ordinal)) changed.Add(pair.Key);
            else preserved++;
        }
        foreach (KeyValuePair<string, CornerGeometrySnapshot> pair in current)
            if (!plan.GeometryBefore.ContainsKey(pair.Key) && !outputTags.Contains(pair.Key)) unexpectedNew.Add(pair.Key);
        foreach (string tag in outputTags)
            if (!current.ContainsKey(tag)) missing.Add(tag);
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "baseline_non_source_geometry_count", plan.GeometryBefore.Count - sourceTags.Count },
            { "preserved_count", preserved },
            { "missing_tags", missing },
            { "changed_tags", changed },
            { "unexpected_new_geometry_tags", StringListObjects(unexpectedNew) },
            { "expected_new_output_curve_count", plan.ExpectedOutputCurveCount },
            { "actual_new_output_curve_count", outputTags.Count }
        };
        return missing.Count == 0 && changed.Count == 0 && unexpectedNew.Count == 0 &&
            outputTags.Count == plan.ExpectedOutputCurveCount;
    }

    private M3ExecutionResult RestoreSketchPatternPlan(M2ModelBinding binding, BindingState state,
        M3SketchPatternPlanState plan, Session.UndoMarkId mark, Exception operationException, bool updateCall)
    {
        Dictionary<string, object> restore = new Dictionary<string, object>(StringComparer.Ordinal);
        restore["native_operation_called"] = plan.NativeOperationEvidence.ContainsKey("native_operation_called") &&
            Boolean.Equals(plan.NativeOperationEvidence["native_operation_called"], true);
        restore["exception_type"] = operationException == null ? null : operationException.GetType().FullName;
        M2ModelException modelException = operationException as M2ModelException;
        restore["exception_code"] = modelException == null ? "NATIVE_PATTERN_FAILED" : modelException.Code;
        restore["exception_message"] = SafeOffsetExceptionMessage(operationException);
        restore["update_call"] = updateCall;
        restore["active_sketch_tag_before"] = plan.ActiveSketchTagBefore;
        try
        {
            Sketch activeTarget = FindSketchByTag(state.WorkPart, plan.SketchTag);
            if (activeTarget != null && IsSketchActive(state.Session, activeTarget))
                activeTarget.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
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
        bool helperTagsRestored = false;
        bool semanticRestored = false;
        bool historyRestored = false;
        bool expressionsRestored = false;
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
                Dictionary<string, List<string>> helperTagsNow = CapturePatternPartHelperTags(state.WorkPart);
                List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart);
                geometryRestored = CornerGeometryMapsEqual(plan.GeometryBefore, geometryNow) &&
                    String.Equals(plan.FullGeometryBefore, CaptureFullGeometry(state.WorkPart), StringComparison.Ordinal);
                constraintsRestored = OffsetConstraintMapsEqual(plan.ConstraintsBefore, constraintsNow);
                framesRestored = OffsetStringMapsEqual(plan.FramesBefore, framesNow);
                referencesRestored = OffsetIntMapsEqual(plan.ReferenceStatesBefore, referencesNow);
                helperTagsRestored = PatternHelperTagInventoriesEqual(plan.PartHelperTagsBefore, helperTagsNow);
                semanticRestored = String.Equals(plan.SemanticBefore, CanonicalSemanticSnapshot(restoredCapture.Snapshot), StringComparison.Ordinal);
                historyRestored = String.Equals(plan.HistoryBefore, M2Json.CanonicalText(HistoryProjection(restoredCapture.Snapshot)), StringComparison.Ordinal);
                expressionsRestored = String.Equals(plan.ExpressionsBefore, M2Json.CanonicalText(SnapshotList(restoredCapture.Snapshot, "expressions")), StringComparison.Ordinal);
                Dictionary<string, object> bodyEvidence;
                bodiesRestored = BodyMeasuresEqual(plan.BodiesBefore, bodiesNow, out bodyEvidence);
                dirtyRestored = state.WorkPart.IsModified == plan.DirtyBefore;
                featuresRestored = FeatureTagsMatch(plan.FeatureTagsBefore, CaptureFeatureTags(state.WorkPart));
                restore["geometry_restored"] = geometryRestored;
                restore["constraints_restored"] = constraintsRestored;
                restore["frames_restored"] = framesRestored;
                restore["reference_status_restored"] = referencesRestored;
                restore["part_helper_tags_restored"] = helperTagsRestored;
                restore["semantic_state_restored"] = semanticRestored;
                restore["history_restored"] = historyRestored;
                restore["expressions_restored"] = expressionsRestored;
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

        bool undoMarkClosed = false;
        try
        {
            state.Session.DeleteUndoMark(mark, "");
            undoMarkClosed = true;
        }
        catch (Exception ex)
        {
            restore["undo_mark_close_exception_type"] = ex.GetType().FullName;
            restore["undo_mark_close_exception_message"] = SafeOffsetExceptionMessage(ex);
        }
        restore["undo_mark_closed"] = undoMarkClosed;
        restore["active_sketch_restored"] = activeSketchRestored;
        restore["revision_id"] = restoredRevision;
        plan.NativeOperationEvidence["undo_restore"] = restore;
        plan.NativeOperationEvidence["undo_mark_closed"] = undoMarkClosed;
        bool restored = undoSucceeded && undoMarkClosed && geometryRestored && constraintsRestored && framesRestored &&
            referencesRestored && helperTagsRestored && semanticRestored && historyRestored && expressionsRestored && bodiesRestored &&
            dirtyRestored && featuresRestored && activeSketchRestored;
        Dictionary<string, object> evidence = BuildSketchPatternEvidence(plan, null);
        if (plan.AppliedEvidence != null)
            evidence["failed_postcondition"] = PatternCloneValue(plan.AppliedEvidence);
        evidence["rollback"] = restore;
        evidence["protection"] = PatternProtectionEvidence(plan, undoMarkClosed);
        string stateName = restored
            ? (Boolean.Equals(restore["native_operation_called"], true) ? "ROLLED_BACK" : "UNCHANGED")
            : "UNKNOWN";
        string code = restored ? (modelException == null ? "NATIVE_PATTERN_FAILED" : modelException.Code) : "SKETCH_PATTERN_ROLLBACK_UNVERIFIED";
        string message = restored
            ? "NX sketch pattern failed and Undo proved restoration of prior geometry, Sketch state, history, bodies, and dirty state."
            : "NX sketch pattern failed and Undo could not prove restoration and UndoMark closure of the prior model state.";
        Dictionary<string, object> result = BuildSketchPatternExecutionResult(plan, restoredRevision, stateName, false, evidence);
        return M3Execution(stateName, restoredRevision, result, code, message);
    }

    private static bool TryCreatePatternCurveModel(Curve curve, Point3d sketchOrigin,
        Vector3d sketchXAxis, Vector3d sketchYAxis, Vector3d sketchNormal,
        string sourceCurveId, string sourceCurveTag, out PatternCurveModel model, out string reason)
    {
        model = null;
        reason = null;
        if (curve == null) { reason = "native curve is null"; return false; }
        PatternCurveModel result = new PatternCurveModel
        {
            SourceCurveId = sourceCurveId,
            SourceCurveTag = sourceCurveTag,
            ObjectType = curve.GetType().Name,
            IsArc = curve is Arc
        };
        Line line = curve as Line;
        if (line != null)
        {
            try
            {
                result.Start = PatternPointToLocal(line.StartPoint, sketchOrigin, sketchXAxis, sketchYAxis, sketchNormal, "Line start point");
                result.End = PatternPointToLocal(line.EndPoint, sketchOrigin, sketchXAxis, sketchYAxis, sketchNormal, "Line end point");
            }
            catch (M2ModelException ex) { reason = ex.Message; return false; }
            double length = PatternDistance(result.Start, result.End);
            double nativeLength = line.GetLength();
            double lengthResidual = Math.Abs(length - nativeLength);
            if (!IsFinite(length) || length <= SketchPatternPositionToleranceMm)
            { reason = "Line length is not finite and greater than 1e-6 mm"; return false; }
            if (!IsFinite(nativeLength) || !IsFinite(lengthResidual) || lengthResidual > SketchPatternPositionToleranceMm)
            { reason = "Line endpoint distance and native curve length disagree"; return false; }
            result.LengthMm = nativeLength;
            model = result;
            return true;
        }
        Arc arc = curve as Arc;
        if (arc == null) { reason = "only native Line and circular Arc geometry are supported"; return false; }
        try
        {
            Point3d orientationCenter;
            Vector3d orientationX, orientationY;
            arc.GetOrientation(out orientationCenter, out orientationX, out orientationY);
            if (PointDistance(orientationCenter, arc.CenterPoint) > SketchPatternPositionToleranceMm)
            { reason = "Arc orientation center differs from its native center"; return false; }
            double xLength = PatternLength(orientationX);
            double yLength = PatternLength(orientationY);
            double normalAlignment = Math.Abs(PatternDot(PatternCross(orientationX, orientationY), sketchNormal));
            if (!IsFinite(xLength) || !IsFinite(yLength) || Math.Abs(xLength - 1.0) > SketchPatternAngleToleranceRad ||
                Math.Abs(yLength - 1.0) > SketchPatternAngleToleranceRad ||
                Math.Abs(PatternDot(orientationX, orientationY)) > SketchPatternAngleToleranceRad ||
                normalAlignment < 1.0 - SketchPatternAngleToleranceRad)
            { reason = "Arc orientation is not a circular frame parallel to the target Sketch plane"; return false; }
            result.Center = PatternPointToLocal(arc.CenterPoint, sketchOrigin, sketchXAxis, sketchYAxis, sketchNormal, "Arc center");
            result.AxisX = PatternVectorToLocal(orientationX, sketchXAxis, sketchYAxis, sketchNormal, "Arc x-axis");
            result.AxisY = PatternVectorToLocal(orientationY, sketchXAxis, sketchYAxis, sketchNormal, "Arc y-axis");
            result.Radius = arc.Radius;
            result.StartAngleRad = arc.StartAngle;
            result.SweepRad = PatternArcSweep(arc);
            result.IsFullCircle = Math.Abs(result.SweepRad) >= 2.0 * Math.PI - SketchPatternAngleToleranceRad;
            if (!IsFinite(result.Radius) || result.Radius <= SketchPatternPositionToleranceMm ||
                !IsFinite(result.SweepRad) || Math.Abs(result.SweepRad) <= SketchPatternAngleToleranceRad ||
                Math.Abs(result.SweepRad) > 2.0 * Math.PI + SketchPatternAngleToleranceRad)
            { reason = "Arc radius or angular sweep is degenerate"; return false; }
            result.LengthMm = arc.GetLength();
            double expectedArcLength = result.Radius * Math.Abs(result.SweepRad);
            double arcLengthTolerance = SketchPatternPositionToleranceMm;
            if (!IsFinite(result.LengthMm) || !IsFinite(expectedArcLength) ||
                Math.Abs(result.LengthMm - expectedArcLength) > arcLengthTolerance)
            { reason = "Arc native length and radius-times-directed-sweep disagree"; return false; }
            result.Start = PatternArcPoint(result.Center, result.AxisX, result.AxisY, result.Radius, result.StartAngleRad);
            result.End = PatternArcPoint(result.Center, result.AxisX, result.AxisY, result.Radius,
                result.StartAngleRad + result.SweepRad);
            result.Mid = PatternArcPoint(result.Center, result.AxisX, result.AxisY, result.Radius,
                result.StartAngleRad + result.SweepRad * 0.5);
            PatternPoint2 nativeStart = PatternPointToLocal(CurveEndpoint(arc, "start"), sketchOrigin,
                sketchXAxis, sketchYAxis, sketchNormal, "Arc start point");
            PatternPoint2 nativeEnd = PatternPointToLocal(CurveEndpoint(arc, "end"), sketchOrigin,
                sketchXAxis, sketchYAxis, sketchNormal, "Arc end point");
            if (PatternDistance(result.Start, nativeStart) > SketchPatternPositionToleranceMm ||
                PatternDistance(result.End, nativeEnd) > SketchPatternPositionToleranceMm)
            { reason = "Arc parameter limits do not agree with its native endpoints"; return false; }
        }
        catch (M2ModelException ex) { reason = ex.Message; return false; }
        catch (Exception ex) { reason = "Arc geometry readback failed (" + ex.GetType().Name + ")"; return false; }
        model = result;
        return true;
    }

    private static PatternPoint2 PatternVectorToLocal(Vector3d vector, Vector3d sketchXAxis,
        Vector3d sketchYAxis, Vector3d sketchNormal, string label)
    {
        double normalResidual = Math.Abs(PatternDot(vector, sketchNormal));
        double x = PatternDot(vector, sketchXAxis);
        double y = PatternDot(vector, sketchYAxis);
        double length = Math.Sqrt(x * x + y * y);
        if (!IsFinite(normalResidual) || normalResidual > SketchPatternAngleToleranceRad ||
            !IsFinite(length) || Math.Abs(length - 1.0) > SketchPatternAngleToleranceRad)
            throw new M2ModelException("UNSUPPORTED_CURVE_GEOMETRY", label + " is not a unit direction in the target Sketch plane");
        PatternPoint2 result = new PatternPoint2(x / length, y / length);
        PatternRequireFinitePoint(result, label);
        return result;
    }

    private static PatternPoint2 PatternPointToLocal(Point3d point, Point3d sketchOrigin,
        Vector3d sketchXAxis, Vector3d sketchYAxis, Vector3d sketchNormal, string label)
    {
        Vector3d delta = new Vector3d(point.X - sketchOrigin.X, point.Y - sketchOrigin.Y, point.Z - sketchOrigin.Z);
        double normalResidual = Math.Abs(PatternDot(delta, sketchNormal));
        if (!IsFinite(normalResidual) || normalResidual > SketchPatternPositionToleranceMm)
            throw new M2ModelException("UNSUPPORTED_CURVE_GEOMETRY", label + " is outside the target Sketch plane by more than 1e-6 mm");
        PatternPoint2 result = new PatternPoint2(PatternDot(delta, sketchXAxis), PatternDot(delta, sketchYAxis));
        PatternRequireFinitePoint(result, label);
        return result;
    }

    private static void BuildPatternPositions(M3SketchPatternPlanState plan)
    {
        List<PatternPosition> positions = new List<PatternPosition>();
        M3SketchPatternRequest request = plan.Request;
        if (request.Mode == "linear")
        {
            PatternPoint2 direction = PatternNormalize2(new PatternPoint2(request.DirectionXY[0], request.DirectionXY[1]), "direction_xy");
            for (int i = 0; i < request.InstanceCount; i++)
            {
                PatternTransform transform = new PatternTransform { Kind = "translation" };
                transform.TranslateX = direction.X * request.SpacingMm * i;
                transform.TranslateY = direction.Y * request.SpacingMm * i;
                PatternRequireFiniteTransform(transform, "linear pattern transform");
                positions.Add(new PatternPosition { PositionIndex = i, Transform = transform });
            }
        }
        else if (request.Mode == "rectangular")
        {
            PatternPoint2 dx = PatternNormalize2(new PatternPoint2(request.DirectionXXY[0], request.DirectionXXY[1]), "direction_x_xy");
            PatternPoint2 dy = PatternNormalize2(new PatternPoint2(request.DirectionYXY[0], request.DirectionYXY[1]), "direction_y_xy");
            for (int y = 0; y < request.CountY; y++)
                for (int x = 0; x < request.CountX; x++)
                {
                    PatternTransform transform = new PatternTransform { Kind = "translation" };
                    transform.TranslateX = dx.X * request.SpacingXmm * x + dy.X * request.SpacingYmm * y;
                    transform.TranslateY = dx.Y * request.SpacingXmm * x + dy.Y * request.SpacingYmm * y;
                    PatternRequireFiniteTransform(transform, "rectangular pattern transform");
                    positions.Add(new PatternPosition
                    {
                        PositionIndex = y * request.CountX + x,
                        PositionXIndex = x,
                        PositionYIndex = y,
                        HasRectangularIndices = true,
                        Transform = transform
                    });
                }
        }
        else if (request.Mode == "circular")
        {
            double cx = request.CenterXY[0];
            double cy = request.CenterXY[1];
            for (int i = 0; i < request.InstanceCount; i++)
            {
                double angle = request.AngleStepDeg * i * Math.PI / 180.0;
                double cosine = Math.Cos(angle);
                double sine = Math.Sin(angle);
                PatternTransform transform = new PatternTransform { Kind = "rotation", PivotX = cx, PivotY = cy, AngleRad = angle };
                transform.M11 = cosine;
                transform.M12 = -sine;
                transform.M21 = sine;
                transform.M22 = cosine;
                PatternRequireFiniteTransform(transform, "circular pattern transform");
                positions.Add(new PatternPosition { PositionIndex = i, Transform = transform });
            }
        }
        else
        {
            PatternTransform identity = new PatternTransform { Kind = "identity" };
            positions.Add(new PatternPosition { PositionIndex = 0, Transform = identity });
            PatternPoint2 p = plan.MirrorAxisStart;
            PatternPoint2 d = plan.MirrorAxisDirection;
            PatternTransform mirror = new PatternTransform { Kind = "reflection" };
            mirror.AxisPointX = p.X;
            mirror.AxisPointY = p.Y;
            mirror.AxisDirectionX = d.X;
            mirror.AxisDirectionY = d.Y;
            mirror.AxisStartX = plan.MirrorAxisStart.X;
            mirror.AxisStartY = plan.MirrorAxisStart.Y;
            mirror.AxisEndX = plan.MirrorAxisEnd.X;
            mirror.AxisEndY = plan.MirrorAxisEnd.Y;
            mirror.M11 = 2.0 * d.X * d.X - 1.0;
            mirror.M12 = 2.0 * d.X * d.Y;
            mirror.M21 = 2.0 * d.X * d.Y;
            mirror.M22 = 2.0 * d.Y * d.Y - 1.0;
            PatternRequireFiniteTransform(mirror, "mirror pattern transform");
            positions.Add(new PatternPosition { PositionIndex = 1, Transform = mirror });
        }
        plan.Positions = positions;
        plan.ExpectedPatternInstanceCount = positions.Count;
        plan.ExpectedOutputCurveCount = (positions.Count - 1) * plan.SourceModels.Length;
    }

    private static void BuildExpectedPatternInstances(M3SketchPatternPlanState plan)
    {
        List<PatternExpectedInstance> instances = new List<PatternExpectedInstance>();
        foreach (PatternCurveModel source in plan.SourceModels)
        {
            foreach (PatternPosition position in plan.Positions)
            {
                PatternCurveModel transformed = TransformPatternCurve(source, position.Transform);
                PatternRequireFiniteCurveModel(transformed, "expected pattern geometry");
                PatternExpectedInstance instance = new PatternExpectedInstance
                {
                    SourceCurveId = source.SourceCurveId,
                    SourceCurveTag = source.SourceCurveTag,
                    PositionIndex = position.PositionIndex,
                    PositionXIndex = position.PositionXIndex,
                    PositionYIndex = position.PositionYIndex,
                    HasRectangularIndices = position.HasRectangularIndices,
                    IsSourceInstance = position.PositionIndex == 0,
                    Transform = position.Transform,
                    Geometry = transformed
                };
                instance.ExpectedCurveRecord = PatternCurveRecord(transformed, plan.SketchOrigin,
                    plan.SketchXAxis, plan.SketchYAxis, plan.SketchNormal);
                instances.Add(instance);
            }
        }
        plan.ExpectedInstances = instances;
    }

    private static PatternCurveModel TransformPatternCurve(PatternCurveModel source, PatternTransform transform)
    {
        PatternCurveModel result = new PatternCurveModel
        {
            SourceCurveId = source.SourceCurveId,
            SourceCurveTag = source.SourceCurveTag,
            ObjectType = source.ObjectType,
            IsArc = source.IsArc,
            IsFullCircle = source.IsFullCircle,
            Radius = source.Radius,
            LengthMm = source.LengthMm,
            StartAngleRad = source.StartAngleRad,
            SweepRad = source.SweepRad
        };
        result.Start = PatternTransformPoint(source.Start, transform);
        result.End = PatternTransformPoint(source.End, transform);
        if (source.IsArc)
        {
            result.Center = PatternTransformPoint(source.Center, transform);
            result.Mid = PatternTransformPoint(source.Mid, transform);
            result.AxisX = PatternTransformVector(source.AxisX, transform);
            result.AxisY = PatternTransformVector(source.AxisY, transform);
        }
        return result;
    }

    private static PatternPoint2 PatternTransformPoint(PatternPoint2 point, PatternTransform transform)
    {
        PatternRequireFinitePoint(point, "pattern input point");
        PatternRequireFiniteTransform(transform, "pattern transform");
        PatternPoint2 result;
        if (transform.Kind == "reflection")
        {
            double vx = point.X - transform.AxisPointX;
            double vy = point.Y - transform.AxisPointY;
            double projection = vx * transform.AxisDirectionX + vy * transform.AxisDirectionY;
            result = new PatternPoint2(
                transform.AxisPointX + 2.0 * projection * transform.AxisDirectionX - vx,
                transform.AxisPointY + 2.0 * projection * transform.AxisDirectionY - vy);
        }
        else if (transform.Kind == "rotation")
        {
            double x = point.X - transform.PivotX;
            double y = point.Y - transform.PivotY;
            result = new PatternPoint2(transform.PivotX + transform.M11 * x + transform.M12 * y,
                transform.PivotY + transform.M21 * x + transform.M22 * y);
        }
        else
            result = new PatternPoint2(transform.M11 * point.X + transform.M12 * point.Y + transform.TranslateX,
                transform.M21 * point.X + transform.M22 * point.Y + transform.TranslateY);
        PatternRequireFinitePoint(result, "transformed pattern point");
        return result;
    }

    private static PatternPoint2 PatternTransformVector(PatternPoint2 vector, PatternTransform transform)
    {
        PatternRequireFinitePoint(vector, "pattern input vector");
        PatternRequireFiniteTransform(transform, "pattern transform");
        PatternPoint2 result;
        if (transform.Kind == "reflection")
        {
            double projection = vector.X * transform.AxisDirectionX + vector.Y * transform.AxisDirectionY;
            result = new PatternPoint2(2.0 * projection * transform.AxisDirectionX - vector.X,
                2.0 * projection * transform.AxisDirectionY - vector.Y);
        }
        else
            result = new PatternPoint2(transform.M11 * vector.X + transform.M12 * vector.Y,
                transform.M21 * vector.X + transform.M22 * vector.Y);
        PatternRequireFinitePoint(result, "transformed pattern vector");
        return result;
    }

    private static List<object> PatternExpectedInstanceRecords(M3SketchPatternPlanState plan)
    {
        List<object> records = new List<object>();
        foreach (PatternExpectedInstance instance in plan.ExpectedInstances)
        {
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item["source_curve_id"] = instance.SourceCurveId;
            item["position_index"] = instance.PositionIndex;
            item["position_x_index"] = instance.HasRectangularIndices ? (object)instance.PositionXIndex : null;
            item["position_y_index"] = instance.HasRectangularIndices ? (object)instance.PositionYIndex : null;
            item["is_source_instance"] = instance.IsSourceInstance;
            item["transform"] = PatternTransformRecord(instance.Transform);
            item["expected_curve_record"] = instance.ExpectedCurveRecord;
            records.Add(item);
        }
        return records;
    }

    private static List<object> PatternSourceCurveRecords(M3SketchPatternPlanState plan)
    {
        List<object> records = new List<object>();
        for (int i = 0; i < plan.SourceModels.Length; i++)
        {
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "source_curve_id", plan.SourceCurveIds[i] },
                { "source_curve_tag", plan.SourceCurveTags[i] },
                { "object_type", plan.SourceCurveTypes[i] },
                { "geometry", PatternCurveRecord(plan.SourceModels[i], plan.SketchOrigin, plan.SketchXAxis, plan.SketchYAxis, plan.SketchNormal) }
            };
            records.Add(item);
        }
        return records;
    }

    private static Dictionary<string, object> PatternTransformRecord(PatternTransform transform)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result["kind"] = transform.Kind;
        result["matrix_2x2"] = new List<object>
        {
            new List<object> { NumberText(transform.M11), NumberText(transform.M12) },
            new List<object> { NumberText(transform.M21), NumberText(transform.M22) }
        };
        result["translation_xy_mm"] = PatternPointRecord(new PatternPoint2(transform.TranslateX, transform.TranslateY));
        if (transform.Kind == "rotation")
        {
            result["center_xy_mm"] = PatternPointRecord(new PatternPoint2(transform.PivotX, transform.PivotY));
            result["angle_rad"] = NumberText(transform.AngleRad);
            result["angle_deg"] = NumberText(transform.AngleRad * 180.0 / Math.PI);
        }
        if (transform.Kind == "reflection")
        {
            result["axis_start_xy_mm"] = PatternPointRecord(new PatternPoint2(transform.AxisStartX, transform.AxisStartY));
            result["axis_end_xy_mm"] = PatternPointRecord(new PatternPoint2(transform.AxisEndX, transform.AxisEndY));
            result["axis_direction_xy"] = PatternPointRecord(new PatternPoint2(transform.AxisDirectionX, transform.AxisDirectionY));
        }
        return result;
    }

    private static Dictionary<string, object> PatternCurveRecord(PatternCurveModel model,
        Point3d sketchOrigin, Vector3d sketchXAxis, Vector3d sketchYAxis, Vector3d sketchNormal)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result["object_type"] = model.ObjectType;
        result["start_xy_mm"] = PatternPointRecord(model.Start);
        result["end_xy_mm"] = PatternPointRecord(model.End);
        result["start_mm"] = Point(PatternPointToWorld(model.Start, sketchOrigin, sketchXAxis, sketchYAxis));
        result["end_mm"] = Point(PatternPointToWorld(model.End, sketchOrigin, sketchXAxis, sketchYAxis));
        result["length_mm"] = NumberText(model.LengthMm);
        if (!model.IsArc) return result;
        PatternPoint2 xAxis = model.AxisX;
        PatternPoint2 yAxis = model.AxisY;
        Vector3d xWorld = new Vector3d(
            xAxis.X * sketchXAxis.X + xAxis.Y * sketchYAxis.X,
            xAxis.X * sketchXAxis.Y + xAxis.Y * sketchYAxis.Y,
            xAxis.X * sketchXAxis.Z + xAxis.Y * sketchYAxis.Z);
        Vector3d yWorld = new Vector3d(
            yAxis.X * sketchXAxis.X + yAxis.Y * sketchYAxis.X,
            yAxis.X * sketchXAxis.Y + yAxis.Y * sketchYAxis.Y,
            yAxis.X * sketchXAxis.Z + yAxis.Y * sketchYAxis.Z);
        result["center_xy_mm"] = PatternPointRecord(model.Center);
        result["center_mm"] = Point(PatternPointToWorld(model.Center, sketchOrigin, sketchXAxis, sketchYAxis));
        result["radius_mm"] = NumberText(model.Radius);
        result["intrinsic_length_mm"] = NumberText(model.LengthMm);
        result["start_angle_rad"] = NumberText(model.StartAngleRad);
        result["end_angle_rad"] = NumberText(model.StartAngleRad + model.SweepRad);
        result["sweep_rad"] = NumberText(model.SweepRad);
        result["full_circle"] = model.IsFullCircle;
        result["midpoint_xy_mm"] = PatternPointRecord(model.Mid);
        result["midpoint_mm"] = Point(PatternPointToWorld(model.Mid, sketchOrigin, sketchXAxis, sketchYAxis));
        result["plane_x_axis_world"] = Vector(xWorld.X, xWorld.Y, xWorld.Z);
        result["plane_y_axis_world"] = Vector(yWorld.X, yWorld.Y, yWorld.Z);
        Vector3d arcNormal = PatternCross(xWorld, yWorld);
        PatternNormalizeVector(ref arcNormal, "expected Arc normal");
        result["plane_normal_world"] = Vector(arcNormal.X, arcNormal.Y, arcNormal.Z);
        return result;
    }

    private static List<object> PatternPointRecord(PatternPoint2 point)
    {
        return new List<object> { NumberText(point.X), NumberText(point.Y) };
    }

    private static Point3d PatternPointToWorld(PatternPoint2 point, Point3d origin,
        Vector3d xAxis, Vector3d yAxis)
    {
        PatternRequireFinitePoint(point, "expected Sketch point");
        Point3d result = new Point3d(origin.X + point.X * xAxis.X + point.Y * yAxis.X,
            origin.Y + point.X * xAxis.Y + point.Y * yAxis.Y,
            origin.Z + point.X * xAxis.Z + point.Y * yAxis.Z);
        if (!IsFinite(result.X) || !IsFinite(result.Y) || !IsFinite(result.Z))
            throw new M2ModelException("INVALID_REQUEST", "pattern transform produces a nonfinite world coordinate");
        return result;
    }

    private static PatternPoint2 PatternArcPoint(PatternPoint2 center, PatternPoint2 xAxis,
        PatternPoint2 yAxis, double radius, double angle)
    {
        PatternRequireFinitePoint(center, "Arc center");
        PatternRequireFinitePoint(xAxis, "Arc x-axis");
        PatternRequireFinitePoint(yAxis, "Arc y-axis");
        if (!IsFinite(radius) || !IsFinite(angle))
            throw new M2ModelException("INVALID_REQUEST", "pattern Arc radius or angle is nonfinite");
        double c = Math.Cos(angle);
        double s = Math.Sin(angle);
        PatternPoint2 result = new PatternPoint2(center.X + radius * (xAxis.X * c + yAxis.X * s),
            center.Y + radius * (xAxis.Y * c + yAxis.Y * s));
        PatternRequireFinitePoint(result, "Arc sample point");
        return result;
    }

    private static double PatternArcSweep(Arc arc)
    {
        if (arc == null) return Double.NaN;
        double rawSweep = PatternArcSweep(arc.StartAngle, arc.EndAngle);
        double full = 2.0 * Math.PI;
        double radius = arc.Radius;
        if (!IsFinite(radius) || radius <= SketchPatternPositionToleranceMm)
            throw new M2ModelException("INVALID_ARC_SWEEP", "Arc radius must be finite and greater than 1e-6 mm");
        double nativeLength = arc.GetLength();
        Point3d nativeStart = CurveEndpoint(arc, "start");
        Point3d nativeEnd = CurveEndpoint(arc, "end");
        bool closed = PointDistance(nativeStart, nativeEnd) <= SketchPatternPositionToleranceMm;
        double circumference = full * radius;
        double fullLengthTolerance = SketchPatternPositionToleranceMm;
        bool fullTurnInterval = Math.Abs(Math.Abs(rawSweep) - full) <= SketchPatternAngleToleranceRad;
        if (closed && fullTurnInterval && IsFinite(nativeLength) &&
            Math.Abs(nativeLength - circumference) <= fullLengthTolerance)
            return full;
        if (Math.Abs(rawSweep) <= SketchPatternAngleToleranceRad)
            throw new M2ModelException("INVALID_ARC_SWEEP", "closed Arc parameters do not have a full-circle native length");

        Point3d orientationCenter;
        Vector3d orientationX, orientationY;
        arc.GetOrientation(out orientationCenter, out orientationX, out orientationY);
        double[] limits = NativeCurveParameterLimits(arc);
        Point3d actualMidpoint = NativeCurvePoint(arc, (limits[0] + limits[1]) * 0.5);
        double alternateSweep = rawSweep > 0.0 ? rawSweep - full : rawSweep + full;
        Point3d directMidpoint = ConicPoint(orientationCenter, orientationX, orientationY,
            radius, radius, 0.0, arc.StartAngle + rawSweep * 0.5);
        Point3d alternateMidpoint = ConicPoint(orientationCenter, orientationX, orientationY,
            radius, radius, 0.0, arc.StartAngle + alternateSweep * 0.5);
        double directResidual = PointDistance(actualMidpoint, directMidpoint);
        double alternateResidual = PointDistance(actualMidpoint, alternateMidpoint);
        double selectedSweep = directResidual <= alternateResidual ? rawSweep : alternateSweep;
        double selectedResidual = Math.Min(directResidual, alternateResidual);
        double expectedLength = Math.Abs(selectedSweep) * radius;
        double lengthTolerance = SketchPatternPositionToleranceMm;
        if (!IsFinite(nativeLength) || !IsFinite(directResidual) || !IsFinite(alternateResidual) ||
            selectedResidual > SketchPatternPositionToleranceMm ||
            Math.Abs(nativeLength - expectedLength) > lengthTolerance)
            throw new M2ModelException("INVALID_ARC_SWEEP", "Arc native midpoint or length does not agree with a single directed sweep");
        return selectedSweep;
    }

    private static void PatternRequireFinitePoint(PatternPoint2 point, string label)
    {
        if (point == null || !IsFinite(point.X) || !IsFinite(point.Y))
            throw new M2ModelException("INVALID_REQUEST", label + " contains a nonfinite pattern coordinate");
    }

    private static void PatternRequireFiniteTransform(PatternTransform transform, string label)
    {
        if (transform == null || String.IsNullOrEmpty(transform.Kind) ||
            !IsFinite(transform.M11) || !IsFinite(transform.M12) ||
            !IsFinite(transform.M21) || !IsFinite(transform.M22) ||
            !IsFinite(transform.TranslateX) || !IsFinite(transform.TranslateY) ||
            !IsFinite(transform.PivotX) || !IsFinite(transform.PivotY) ||
            !IsFinite(transform.AngleRad) || !IsFinite(transform.AxisPointX) ||
            !IsFinite(transform.AxisPointY) || !IsFinite(transform.AxisDirectionX) ||
            !IsFinite(transform.AxisDirectionY) || !IsFinite(transform.AxisStartX) ||
            !IsFinite(transform.AxisStartY) || !IsFinite(transform.AxisEndX) ||
            !IsFinite(transform.AxisEndY))
            throw new M2ModelException("INVALID_REQUEST", label + " contains a nonfinite transform component");
    }

    private static void PatternRequireFiniteCurveModel(PatternCurveModel model, string label)
    {
        if (model == null) throw new M2ModelException("INVALID_REQUEST", label + " is unavailable");
        PatternRequireFinitePoint(model.Start, label + " start");
        PatternRequireFinitePoint(model.End, label + " end");
        if (model.IsArc)
        {
            PatternRequireFinitePoint(model.Center, label + " center");
            PatternRequireFinitePoint(model.Mid, label + " midpoint");
            PatternRequireFinitePoint(model.AxisX, label + " x-axis");
            PatternRequireFinitePoint(model.AxisY, label + " y-axis");
            if (!IsFinite(model.Radius) || !IsFinite(model.LengthMm) || !IsFinite(model.StartAngleRad) || !IsFinite(model.SweepRad))
                throw new M2ModelException("INVALID_REQUEST", label + " contains a nonfinite Arc parameter");
        }
        else if (!IsFinite(model.LengthMm))
            throw new M2ModelException("INVALID_REQUEST", label + " contains a nonfinite curve length");
    }

    internal static double PatternArcSweep(double startAngleRad, double endAngleRad)
    {
        if (!IsFinite(startAngleRad) || !IsFinite(endAngleRad))
            throw new M2ModelException("INVALID_ARC_SWEEP", "Arc start and end angles must be finite");
        double rawSweep = endAngleRad - startAngleRad;
        double full = 2.0 * Math.PI;
        if (!IsFinite(rawSweep) || Math.Abs(rawSweep) > full + SketchPatternAngleToleranceRad)
            throw new M2ModelException("INVALID_ARC_SWEEP", "Arc sweep magnitude must not exceed one full turn");
        if (Math.Abs(rawSweep) <= SketchPatternAngleToleranceRad)
            throw new M2ModelException("INVALID_ARC_SWEEP", "Arc interval is degenerate; a full circle requires an explicit one-turn interval");
        return rawSweep;
    }

    private static PatternPoint2 PatternNormalize2(PatternPoint2 value, string label)
    {
        double scale = Math.Max(Math.Abs(value.X), Math.Abs(value.Y));
        if (!IsFinite(scale) || scale <= 0.0)
            throw new M2ModelException("INVALID_REQUEST", label + " must be nonzero and finite");
        double x = value.X / scale;
        double y = value.Y / scale;
        double length = Math.Sqrt(x * x + y * y);
        if (!IsFinite(length) || length <= 0.0)
            throw new M2ModelException("INVALID_REQUEST", label + " must be nonzero and finite");
        return new PatternPoint2(x / length, y / length);
    }

    private static void PatternNormalizeVector(ref Vector3d vector, string label)
    {
        double length = PatternLength(vector);
        if (!IsFinite(length) || length <= 1.0e-12)
            throw new M2ModelException("UNSUPPORTED_SKETCH_FRAME", label + " is degenerate");
        vector = new Vector3d(vector.X / length, vector.Y / length, vector.Z / length);
    }

    private static double PatternLength(Vector3d vector)
    {
        return Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
    }

    private static double PatternDot(Vector3d left, Vector3d right)
    {
        return left.X * right.X + left.Y * right.Y + left.Z * right.Z;
    }

    private static Vector3d PatternCross(Vector3d left, Vector3d right)
    {
        return new Vector3d(left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);
    }

    private static double PatternDistance(PatternPoint2 left, PatternPoint2 right)
    {
        double dx = left.X - right.X;
        double dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string[] PatternCurveTags(Curve[] curves)
    {
        if (curves == null) return new string[0];
        string[] result = new string[curves.Length];
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < curves.Length; i++)
        {
            if (curves[i] == null) throw new M2ModelException("PATTERN_OUTPUT_INVALID", "native output contains a null Curve");
            result[i] = curves[i].Tag.ToString();
            if (String.IsNullOrEmpty(result[i]) || !seen.Add(result[i]))
                throw new M2ModelException("PATTERN_OUTPUT_INVALID", "native output contains a missing or duplicate Curve Tag");
        }
        return result;
    }

    private static Curve[] PatternOutputsFromGeometryDelta(Sketch sketch, M3SketchPatternPlanState plan,
        Dictionary<string, CornerGeometrySnapshot> geometryAfter, string[] factoryOutputTags,
        out bool factoryOutputListConsistent, out Dictionary<string, object> evidence)
    {
        List<string> newCurveTags = new List<string>();
        List<string> unexpectedTags = new List<string>();
        List<string> unresolvedTags = new List<string>();
        List<object> nativeRecords = new List<object>();
        List<Curve> outputs = new List<Curve>();
        bool sketchAvailable = sketch != null && String.Equals(sketch.Tag.ToString(), plan.SketchTag, StringComparison.Ordinal);
        if (geometryAfter != null)
        {
            List<string> tags = new List<string>(geometryAfter.Keys);
            tags.Sort(StringComparer.Ordinal);
            foreach (string tag in tags)
            {
                if (plan.GeometryBefore.ContainsKey(tag)) continue;
                CornerGeometrySnapshot item = geometryAfter[tag];
                if (!String.Equals(item.SketchTag, plan.SketchTag, StringComparison.Ordinal)) continue;
                Curve curve = null;
                if (String.Equals(item.ObjectType, "Line", StringComparison.Ordinal) ||
                    String.Equals(item.ObjectType, "Arc", StringComparison.Ordinal))
                {
                    newCurveTags.Add(tag);
                    if (sketchAvailable) curve = FindCornerCurveByTag(sketch, tag);
                    if (curve == null || !sketch.IsSketchGeometry(curve)) unresolvedTags.Add(tag);
                    else
                    {
                        outputs.Add(curve);
                        nativeRecords.Add(PatternCloneValue(item.Record));
                    }
                }
                else unexpectedTags.Add(tag);
            }
        }
        newCurveTags.Sort(StringComparer.Ordinal);
        unexpectedTags.Sort(StringComparer.Ordinal);
        unresolvedTags.Sort(StringComparer.Ordinal);
        string[] deltaTags = newCurveTags.ToArray();
        string[] factoryTags = factoryOutputTags ?? new string[0];
        bool factoryListReported = factoryTags.Length > 0;
        factoryOutputListConsistent = !factoryListReported || PatternStringSetsEqual(factoryTags, deltaTags);
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "valid", sketchAvailable && unexpectedTags.Count == 0 && unresolvedTags.Count == 0 },
            { "source", "post_update_target_sketch_tag_delta" },
            { "new_curve_tags", StringListObjects(deltaTags) },
            { "new_curve_count", deltaTags.Length },
            { "expected_output_curve_count", plan.ExpectedOutputCurveCount },
            { "unexpected_new_geometry_tags", StringListObjects(unexpectedTags) },
            { "unresolved_new_curve_tags", StringListObjects(unresolvedTags) },
            { "native_curve_records", nativeRecords },
            { "factory_output_tags_reported", factoryListReported },
            { "factory_output_tags", StringListObjects(factoryTags) },
            { "factory_output_list_matches_delta", factoryOutputListConsistent }
        };
        return outputs.ToArray();
    }

    private static bool PatternStringSetsEqual(string[] left, string[] right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        HashSet<string> values = new HashSet<string>(StringComparer.Ordinal);
        foreach (string item in left) if (!values.Add(item)) return false;
        foreach (string item in right) if (!values.Remove(item)) return false;
        return values.Count == 0;
    }

    private static Sketch FindSketchByTag(Part part, string sketchTag)
    {
        if (part == null || String.IsNullOrEmpty(sketchTag)) return null;
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory returned null while resolving a retained F09 Tag");
        Sketch found = null;
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch inventory contains null while resolving a retained F09 Tag");
            if (!String.Equals(sketch.Tag.ToString(), sketchTag, StringComparison.Ordinal)) continue;
            if (found != null) throw new M2ModelException("MODEL_READ_FAILED", "retained F09 Sketch Tag resolves more than once");
            found = sketch;
        }
        return found;
    }

    private static Dictionary<string, List<string>> CapturePatternPartHelperTags(Part part)
    {
        if (part == null) throw new M2ModelException("MODEL_READ_FAILED", "Work Part is unavailable during F09 helper-object inventory capture");
        NXOpen.Point[] points = part.Points.ToArray();
        NXOpen.Axis[] axes = part.Axes.ToArray();
        NXOpen.Direction[] directions = part.Directions.ToArray();
        if (points == null || axes == null || directions == null)
            throw new M2ModelException("MODEL_READ_FAILED", "Part Point, Axis, or Direction inventory returned null during F09 capture");
        return new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            { "points", PatternObjectTags(points, "Part Point") },
            { "axes", PatternObjectTags(axes, "Part Axis") },
            { "directions", PatternObjectTags(directions, "Part Direction") }
        };
    }

    private static List<string> PatternObjectTags(NXObject[] objects, string label)
    {
        List<string> result = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (NXObject item in objects)
        {
            if (item == null) throw new M2ModelException("MODEL_READ_FAILED", label + " inventory contains null");
            string tag = item.Tag.ToString();
            if (String.IsNullOrEmpty(tag) || !seen.Add(tag))
                throw new M2ModelException("MODEL_READ_FAILED", label + " inventory contains a missing or duplicate Tag");
            result.Add(tag);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static bool PatternHelperTagInventoriesEqual(Dictionary<string, List<string>> expected,
        Dictionary<string, List<string>> actual)
    {
        if (expected == null || actual == null || expected.Count != 3 || actual.Count != 3) return false;
        string[] kinds = new string[] { "points", "axes", "directions" };
        foreach (string kind in kinds)
        {
            List<string> left, right;
            if (!expected.TryGetValue(kind, out left) || !actual.TryGetValue(kind, out right) ||
                !PatternStringListsEqual(left, right)) return false;
        }
        return true;
    }

    private static bool PatternStringListsEqual(List<string> left, List<string> right)
    {
        if (left == null || right == null || left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!String.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool PatternHelperCleanupMatches(M3SketchPatternPlanState plan,
        Dictionary<string, List<string>> current, out Dictionary<string, object> evidence)
    {
        Dictionary<string, object> factory = plan.NativeOperationEvidence.ContainsKey("factory")
            ? plan.NativeOperationEvidence["factory"] as Dictionary<string, object> : null;
        bool beforeReported, createdReported, scheduledReported;
        Dictionary<string, List<string>> factoryBefore = PatternReadHelperTagInventory(factory, "helper_tags_before", out beforeReported);
        Dictionary<string, List<string>> factoryCreated = PatternReadHelperTagInventory(factory, "helper_tags_created", out createdReported);
        Dictionary<string, List<string>> factoryScheduled = PatternReadHelperTagInventory(factory, "helper_tags_delete_scheduled", out scheduledReported);
        bool beforeMatchesAdapter = beforeReported && PatternHelperTagInventoriesEqual(plan.PartHelperTagsBefore, factoryBefore);
        bool createdAreNew = createdReported && PatternHelperTagsAreNew(plan.PartHelperTagsBefore, factoryCreated);
        bool createdScheduled = createdReported && scheduledReported && PatternHelperTagsScheduled(factoryCreated, factoryScheduled);
        bool afterMatchesBaseline = PatternHelperTagInventoriesEqual(plan.PartHelperTagsBefore, plan.PartHelperTagsAfter);
        bool liveMatchesAfter = PatternHelperTagInventoriesEqual(plan.PartHelperTagsAfter, current);
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "source", "Part Points/Axes/Directions Tag inventories" },
            { "factory_inventory_fields_reported", beforeReported && createdReported && scheduledReported },
            { "factory_before_matches_prepare", beforeMatchesAdapter },
            { "created_tags_are_new", createdAreNew },
            { "every_created_tag_scheduled_for_delete", createdScheduled },
            { "post_update_inventory_matches_prepare", afterMatchesBaseline },
            { "live_inventory_matches_post_update", liveMatchesAfter },
            { "helper_tags_before", PatternCloneValue(factoryBefore) },
            { "helper_tags_created", PatternCloneValue(factoryCreated) },
            { "helper_tags_delete_scheduled", PatternCloneValue(factoryScheduled) },
            { "helper_tags_after", PatternCloneValue(plan.PartHelperTagsAfter) },
            { "before", PatternCloneValue(plan.PartHelperTagsBefore) },
            { "factory_before", PatternCloneValue(factoryBefore) },
            { "factory_created", PatternCloneValue(factoryCreated) },
            { "factory_delete_scheduled", PatternCloneValue(factoryScheduled) },
            { "after", PatternCloneValue(plan.PartHelperTagsAfter) },
            { "live", PatternCloneValue(current) }
        };
        return beforeMatchesAdapter && createdAreNew && createdScheduled && afterMatchesBaseline && liveMatchesAfter;
    }

    private static Dictionary<string, List<string>> PatternReadHelperTagInventory(
        Dictionary<string, object> factory, string key, out bool reported)
    {
        reported = false;
        Dictionary<string, List<string>> result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        object value = null;
        Dictionary<string, object> inventory = factory != null && factory.TryGetValue(key, out value)
            ? value as Dictionary<string, object> : null;
        if (inventory == null) return result;
        string[] kinds = new string[] { "points", "axes", "directions" };
        foreach (string kind in kinds)
        {
            object tagsValue;
            if (!inventory.TryGetValue(kind, out tagsValue)) return new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> tags = tagsValue as List<string>;
            if (tags == null)
            {
                List<object> objects = tagsValue as List<object>;
                if (objects != null)
                {
                    tags = new List<string>();
                    foreach (object item in objects)
                    {
                        string tag = item as string;
                        if (String.IsNullOrEmpty(tag)) return new Dictionary<string, List<string>>(StringComparer.Ordinal);
                        tags.Add(tag);
                    }
                }
            }
            if (tags == null) return new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> copy = new List<string>(tags);
            copy.Sort(StringComparer.Ordinal);
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string tag in copy)
                if (String.IsNullOrEmpty(tag) || !unique.Add(tag)) return new Dictionary<string, List<string>>(StringComparer.Ordinal);
            result.Add(kind, copy);
        }
        reported = true;
        return result;
    }

    private static bool PatternHelperTagsAreNew(Dictionary<string, List<string>> before,
        Dictionary<string, List<string>> created)
    {
        if (before == null || created == null || before.Count != 3 || created.Count != 3) return false;
        string[] kinds = new string[] { "points", "axes", "directions" };
        foreach (string kind in kinds)
        {
            List<string> oldTags, newTags;
            if (!before.TryGetValue(kind, out oldTags) || !created.TryGetValue(kind, out newTags)) return false;
            HashSet<string> oldSet = new HashSet<string>(oldTags, StringComparer.Ordinal);
            foreach (string tag in newTags) if (oldSet.Contains(tag)) return false;
        }
        return true;
    }

    private static bool PatternHelperTagsScheduled(Dictionary<string, List<string>> created,
        Dictionary<string, List<string>> scheduled)
    {
        if (created == null || scheduled == null || created.Count != 3 || scheduled.Count != 3) return false;
        string[] kinds = new string[] { "points", "axes", "directions" };
        foreach (string kind in kinds)
        {
            List<string> createdTags, scheduledTags;
            if (!created.TryGetValue(kind, out createdTags) || !scheduled.TryGetValue(kind, out scheduledTags)) return false;
            HashSet<string> scheduledSet = new HashSet<string>(scheduledTags, StringComparer.Ordinal);
            foreach (string tag in createdTags) if (!scheduledSet.Contains(tag)) return false;
        }
        return true;
    }

    private static bool PatternFactoryConstraintFlagsClear(Dictionary<string, object> factoryEvidence,
        out Dictionary<string, object> evidence)
    {
        object createConstraintValue = null;
        object createSpacingValue = null;
        bool hasConstraint = factoryEvidence != null && factoryEvidence.TryGetValue("create_constraint", out createConstraintValue);
        bool hasSpacing = factoryEvidence != null && factoryEvidence.TryGetValue("create_spacing_expression", out createSpacingValue);
        bool createConstraint = hasConstraint && createConstraintValue is bool && (bool)createConstraintValue;
        bool createSpacing = hasSpacing && createSpacingValue is bool && (bool)createSpacingValue;
        bool valid = hasConstraint && createConstraintValue is bool &&
            hasSpacing && createSpacingValue is bool && !createConstraint && !createSpacing;
        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "create_constraint", hasConstraint ? createConstraintValue : null },
            { "create_spacing_expression", hasSpacing ? createSpacingValue : null },
            { "flags_present", hasConstraint && hasSpacing },
            { "flags_clear", valid }
        };
        return valid;
    }

    private static Dictionary<string, object> PatternProtectionEvidence(M3SketchPatternPlanState plan, bool markClosed)
    {
        object recordedMarkClosure = null;
        plan.NativeOperationEvidence.TryGetValue("undo_mark_closed", out recordedMarkClosure);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "precondition_revalidation", Boolean.Equals(plan.NativeOperationEvidence.ContainsKey("precondition_revalidation") ? plan.NativeOperationEvidence["precondition_revalidation"] : null, true) },
            { "undo_mark_created", Boolean.Equals(plan.NativeOperationEvidence.ContainsKey("undo_mark_created") ? plan.NativeOperationEvidence["undo_mark_created"] : null, true) },
            { "restore_on_failure_enabled", Boolean.Equals(plan.NativeOperationEvidence.ContainsKey("restore_on_failure_enabled") ? plan.NativeOperationEvidence["restore_on_failure_enabled"] : null, true) },
            { "all_postconditions_verified", Boolean.Equals(plan.NativeOperationEvidence.ContainsKey("postconditions_verified_before_undo_mark_close") ? plan.NativeOperationEvidence["postconditions_verified_before_undo_mark_close"] : null, true) },
            { "undo_mark_closed", markClosed && Boolean.Equals(recordedMarkClosure, true) }
        };
    }

    private static Dictionary<string, object> BuildSketchPatternEvidence(M3SketchPatternPlanState plan,
        Dictionary<string, object> checks)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence["native_operation"] = PatternCloneDictionary(plan.NativeOperationEvidence);
        evidence["expected_pattern_instance_count"] = plan.ExpectedPatternInstanceCount;
        evidence["expected_output_curve_count"] = plan.ExpectedOutputCurveCount;
        evidence["expected_instances"] = PatternExpectedInstanceRecords(plan);
        if (checks != null) evidence["checks"] = PatternCloneValue(checks);
        return evidence;
    }

    private static Dictionary<string, object> BuildSketchPatternExecutionResult(M3SketchPatternPlanState plan,
        string revisionId, string state, bool verified, Dictionary<string, object> evidence)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result["kind"] = "edit_sketch_pattern";
        result["mutation_state"] = state;
        result["revision_id"] = revisionId;
        result["sketch_id"] = plan.SketchId;
        result["sketch_tag"] = plan.SketchTag;
        result["feature_id"] = plan.FeatureId;
        result["mode"] = plan.Request.Mode;
        result["source_curve_ids"] = StringListObjects(plan.SourceCurveIds);
        result["output_curve_tags"] = StringListObjects(plan.OutputCurveTags);
        result["commit_returned_null"] = plan.CommitReturnedNull;
        result["expected_pattern_instance_count"] = plan.ExpectedPatternInstanceCount;
        result["expected_output_curve_count"] = plan.ExpectedOutputCurveCount;
        result["verified"] = verified;
        result["evidence"] = evidence;
        return result;
    }

    private static Dictionary<string, object> PatternFailedChecks(Dictionary<string, object> measured)
    {
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        string[] names = new string[] { "pattern_instances", "source_curves", "sketch_constraints", "sketch_frames",
            "reference_status", "non_target_geometry", "history", "bodies", "sketch_solver_status" };
        foreach (string name in names)
        {
            object detail = null;
            if (measured != null) measured.TryGetValue(name, out detail);
            checks[name] = VerificationCheck("MISMATCH", detail as Dictionary<string, object>);
        }
        return checks;
    }

    private static M3SketchPatternRequest CloneM3SketchPatternRequest(M3SketchPatternRequest source)
    {
        if (source == null) return null;
        return new M3SketchPatternRequest
        {
            RevisionId = source.RevisionId,
            SketchId = source.SketchId,
            Mode = source.Mode,
            SourceCurveIds = source.SourceCurveIds == null ? null : new List<string>(source.SourceCurveIds),
            DirectionXY = source.DirectionXY == null ? null : (double[])source.DirectionXY.Clone(),
            SpacingMm = source.SpacingMm,
            InstanceCount = source.InstanceCount,
            DirectionXXY = source.DirectionXXY == null ? null : (double[])source.DirectionXXY.Clone(),
            DirectionYXY = source.DirectionYXY == null ? null : (double[])source.DirectionYXY.Clone(),
            SpacingXmm = source.SpacingXmm,
            SpacingYmm = source.SpacingYmm,
            CountX = source.CountX,
            CountY = source.CountY,
            CenterXY = source.CenterXY == null ? null : (double[])source.CenterXY.Clone(),
            AngleStepDeg = source.AngleStepDeg,
            AxisCurveId = source.AxisCurveId
        };
    }

    private static Dictionary<string, object> PatternCloneDictionary(Dictionary<string, object> source)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (source != null)
            foreach (KeyValuePair<string, object> pair in source) result[pair.Key] = PatternCloneValue(pair.Value);
        return result;
    }

    private static object PatternCloneValue(object value)
    {
        Dictionary<string, List<string>> stringListDictionary = value as Dictionary<string, List<string>>;
        if (stringListDictionary != null)
        {
            Dictionary<string, object> copy = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<string>> pair in stringListDictionary)
            {
                List<object> tags = new List<object>();
                if (pair.Value != null)
                    foreach (string tag in pair.Value) tags.Add(tag);
                copy[pair.Key] = tags;
            }
            return copy;
        }
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null) return PatternCloneDictionary(dictionary);
        List<object> list = value as List<object>;
        if (list != null)
        {
            List<object> copy = new List<object>();
            foreach (object item in list) copy.Add(PatternCloneValue(item));
            return copy;
        }
        List<string> stringList = value as List<string>;
        if (stringList != null) return new List<string>(stringList);
        string[] strings = value as string[];
        if (strings != null) return (string[])strings.Clone();
        double[] numbers = value as double[];
        if (numbers != null) return (double[])numbers.Clone();
        return value;
    }
}
