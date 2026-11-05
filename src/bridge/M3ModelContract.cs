using System.Collections.Generic;

namespace NxSketchMcp.M2
{
    // Optional native helical operations; keep the existing M3 implementers compatible.
    public interface IM7HelicalOperationAdapter
    {
        M3PlanSnapshot PrepareThread(M2ModelBinding binding, M7ThreadRequest request);
        M3PlanSnapshot PrepareHelicalGroove(M2ModelBinding binding, M7HelicalGrooveRequest request);
    }

    public sealed class M7ThreadRequest
    {
        public string RevisionId;
        public string TargetBodyId;
        public string Axis;
        public double[] AxisOriginMm;
        public double StartMm;
        public double LengthMm;
        public string Handedness;
        public string Direction;
        public double MajorDiameterMm;
        public double MinorDiameterMm;
        public double PitchMm;
    }

    public sealed class M7HelicalGrooveRequest
    {
        public string RevisionId;
        public string TargetBodyId;
        public string Axis;
        public double[] AxisOriginMm;
        public double StartMm;
        public double LengthMm;
        public string Handedness;
        public double LeadMm;
        public double RootDiameterMm;
        public double ReferenceDiameterMm;
        public double OuterDiameterMm;
        public double SpaceWidthMm;
        public double FlankAngleDeg;
        public double PhaseDeg;
    }

    // M3 is an optional extension of the frozen M2 model adapter. The M2 bridge
    // keeps ownership, binding, revision, operation and cancellation behavior;
    // this contract describes only the additional native sketch/Extrude work.
    public interface IM3OperationAdapter
    {
        M3PlanSnapshot PrepareSketch(M2ModelBinding binding, M3SketchRequest request);
        M3PlanSnapshot PrepareExtrude(M2ModelBinding binding, M3ExtrudeRequest request);
        M3ExecutionResult ExecutePlan(M2ModelBinding binding, M3PlanSnapshot prepared);
        M3VerificationResult VerifyPlan(M2ModelBinding binding, M3PlanSnapshot prepared);
    }

    // Optional extension for modifying existing sketch corners. Keeping it
    // separate preserves compatibility with existing IM3OperationAdapter implementations.
    public interface IM3SketchCornerOperationAdapter
    {
        M3PlanSnapshot PrepareSketchCorner(M2ModelBinding binding, M3SketchCornerRequest request);
    }

    // Optional extension for native sketch offsets. This stays separate from the
    // frozen M3 adapter contract so existing adapters remain source compatible.
    public interface IM3SketchOffsetOperationAdapter
    {
        M3PlanSnapshot PrepareSketchOffset(M2ModelBinding binding, M3SketchOffsetRequest request);
    }

    // Optional extension for linear, rectangular, circular, and mirror sketch
    // patterns. PrepareSketchPattern must be read-only and publish the expected
    // position count plus the per-position transforms in M3PlanSnapshot.PublicView.
    // Keeping it separate preserves compatibility with existing M3 adapters.
    public interface IM3SketchPatternOperationAdapter
    {
        M3PlanSnapshot PrepareSketchPattern(M2ModelBinding binding, M3SketchPatternRequest request);
    }

    // Optional extension for source projection into an explicit native Sketch.
    // Keeping it separate preserves compatibility with existing M3 adapters.
    public interface IM3SketchProjectionOperationAdapter
    {
        M3PlanSnapshot PrepareSketchProjection(M2ModelBinding binding, M3SketchProjectionRequest request);
    }

    // Optional extension for changing explicitly selected sketch curve/dimension reference states.
    public interface IM3SketchReferenceConversionOperationAdapter
    {
        M3PlanSnapshot PrepareSketchReferenceConversion(M2ModelBinding binding,
            M3SketchReferenceConversionRequest request);
    }

    // Optional sweep extension. transport requires zero twist; linear_twist runs
    // from zero to the requested right-hand angle over normalized path arc length.
    public interface IM3SweepOperationAdapter
    {
        M3PlanSnapshot PrepareSweep(M2ModelBinding binding, M3SweepRequest request);
    }

    public sealed class M3SweepRequest
    {
        public string RevisionId;
        public string SketchId;
        public string[] PathCurveIds;
        public string ProfileOutput;
        public string OrientationMode;
        public double TwistAngleDeg;
    }

    // Optional extension for revolved features. Keeping this separate preserves
    // compatibility with existing M3 adapters while the common M3 adapter owns
    // execution, fresh verification, and the transaction boundary.
    public interface IM3RevolveOperationAdapter
    {
        M3PlanSnapshot PrepareRevolve(M2ModelBinding binding, M3RevolveRequest request);
    }

    public interface IM3BooleanOperationAdapter
    {
        M3PlanSnapshot PrepareBoolean(M2ModelBinding binding, M3BooleanRequest request);
    }

    public interface IM3ChamferOperationAdapter
    {
        M3PlanSnapshot PrepareChamfer(M2ModelBinding binding, M3ChamferRequest request);
    }

    public interface IM3FeaturePatternOperationAdapter
    {
        M3PlanSnapshot PrepareFeaturePattern(M2ModelBinding binding, M3FeaturePatternRequest request);
    }

    public interface IM3SpatialCurveOperationAdapter
    {
        M3PlanSnapshot PrepareSpatialCurve(M2ModelBinding binding, M3SpatialCurveRequest request);
    }

    public sealed class M3SpatialCurveRequest
    {
        public string RevisionId, CurveType, DefinitionMode;
        public double[] StartMm, ThroughMm, EndMm, CenterMm, Normal, XAxis;
        public double RadiusMm, StartAngleDeg, EndAngleDeg;
    }

    // The common M3 adapter owns execution, verification and transaction handling.
    public interface IM3FeatureMirrorOperationAdapter
    {
        M3PlanSnapshot PrepareFeatureMirror(M2ModelBinding binding, M3FeatureMirrorRequest request);
    }

    public sealed class M3FeatureMirrorRequest
    {
        public string RevisionId, SeedFeatureId, DatumPlaneId;
        public double[] PlaneOriginMm, PlaneNormal;
    }

    public sealed class M3FeaturePatternRequest
    {
        public string RevisionId, SeedFeatureId, Mode;
        public int Count1, Count2;
        public double Pitch1Mm, Pitch2Mm, AngleDeg;
        public double[] Direction1, Direction2, AxisOriginMm, AxisDirection;
    }

    public sealed class M3ChamferRequest
    {
        public string RevisionId, TargetBodyId, Mode, ReferenceAxis;
        public string[] EdgeIds;
        public double DistanceMm, SecondDistanceMm, AngleDeg;
    }

    // Edge blends share the common M3 execution and fresh verification lifecycle.
    public interface IM3EdgeBlendOperationAdapter
    {
        M3PlanSnapshot PrepareEdgeBlend(M2ModelBinding binding, M3EdgeBlendRequest request);
    }

    // Hole creation shares the common M3 execution and fresh verification lifecycle.
    public interface IM3HoleOperationAdapter
    {
        M3PlanSnapshot PrepareHole(M2ModelBinding binding, M3HoleRequest request);
    }

    // Keep datum creation optional so existing M3 adapter implementations remain compatible.
    public interface IM3DatumPlaneOperationAdapter
    {
        M3PlanSnapshot PrepareDatumPlane(M2ModelBinding binding, M3DatumPlaneRequest request);
    }

    // M4 adds read-only planar face discovery. Face choices remain inside the
    // NX adapter; callers supply a constrained selector, never a face tag/index.
    public interface IM4FaceQueryAdapter
    {
        Dictionary<string, object> QueryFaces(M2ModelBinding binding, string revisionId,
            string selector, string bodyId);
    }

    public sealed class M3SketchRequest
    {
        public string RevisionId;
        public string Plane;
        public double[] OriginMm;
        // A custom world frame uses OriginMm, Normal and XAxis.  For an
        // associative face/datum-plane frame, the same vectors describe the
        // requested sketch frame and are checked against the native support.
        public double[] Normal;
        public double[] XAxis;
        public string SupportFaceSelector;
        public string SupportFaceId;
        public string SupportFaceTag;
        public string SupportDatumPlaneId;
        public string SupportBodyId;
        public double[] FaceOriginOffsetMm;
        public string ConstraintMode;
        public List<M3SketchGeometryRequest> Geometries;
    }

    public sealed class M3DatumPlaneRequest
    {
        public string RevisionId;
        public string Mode;
        public double[] OriginMm;
        public double[] Normal;
        public double[] XAxis;
        public string SupportDatumPlaneId;
        public string SupportFaceId;
        public string SupportFaceTag;
        public double DistanceMm;
        public string AxisLineId;
        public double AngleDeg;
        public string Line1Id;
        public string Line2Id;
    }

    public sealed class M3SketchGeometryRequest
    {
        public string Id;
        public string Type;
        public double[] StartMm;
        public double[] EndMm;
        public double[] CornerMm;
        public double[] CenterMm;
        public double WidthMm;
        public double HeightMm;
        public double DiameterMm;
        public double RadiusMm;
        public double MajorRadiusMm;
        public double MinorRadiusMm;
        public double RotationDeg;
        public double StartAngleDeg;
        public double SweepDeg;
        public double[][] InterpolationPointsMm;
        public int Degree;
        public string EndpointCondition;
    }

    public sealed class M3SketchCornerRequest
    {
        public string RevisionId;
        public string SketchId;
        public string Kind;
        public string Curve1Id;
        public string Curve1End;
        public string Curve2Id;
        public string Curve2End;
        public double RadiusMm;
        public double Distance1Mm;
        public double? Distance2Mm;
        public double? AngleDeg;
    }

    public sealed class M3SketchOffsetCurveRequest
    {
        public string CurveId;
        public string Direction;
    }

    public sealed class M3SketchOffsetRequest
    {
        public string RevisionId;
        public string SketchId;
        public List<M3SketchOffsetCurveRequest> CurveChain;
        public double DistanceMm;
        public string Side;
        public bool RetainOriginal;
        public string JoinMode;
    }

    public sealed class M3SketchPatternRequest
    {
        public string RevisionId;
        public string SketchId;
        public string Mode;
        public List<string> SourceCurveIds;
        public double[] DirectionXY;
        public double SpacingMm;
        public int InstanceCount;
        public double[] DirectionXXY;
        public double[] DirectionYXY;
        public double SpacingXmm;
        public double SpacingYmm;
        public int CountX;
        public int CountY;
        public double[] CenterXY;
        public double AngleStepDeg;
        public string AxisCurveId;
    }

    public sealed class M3SketchProjectionRequest
    {
        public string RevisionId;
        public string SketchId;
        public List<string> SourceCurveIds;
        public double[] DirectionXYZ;
        public bool Associative;
    }

    public sealed class M3SketchReferenceConversionRequest
    {
        public string RevisionId;
        public string SketchId;
        // Current JournalIdentifiers of Sketch curves or SketchDimensionalConstraints.
        public List<string> ObjectIds;
        // "reference" maps to curve Reference/dimension Reference; "active" maps to
        // curve Active/dimension Driving in the native ConvertToFromReferenceBuilder.
        public string TargetState;
    }

    public sealed class M3ExtrudeRequest
    {
        public string RevisionId;
        public string SketchId;
        public string Mode;
        public string Direction;
        public string Extent;
        // "solid" by default; "sheet" is explicit and limited to finite create.
        public string ProfileOutput;
        // Compatibility span length; F12 execution reads the explicit start/end offsets below.
        public double DepthMm;
        public double StartOffsetMm;
        public double EndOffsetMm;
        public string TargetBodyId;
    }

    public sealed class M3RevolveRequest
    {
        public string RevisionId;
        public string SketchId;
        public string Mode;
        public string ProfileOutput;
        public double[] AxisPointMm;
        // Normalized world-space direction. The signed angular interval is
        // measured by the right-hand rule around this direction.
        public double[] AxisDirection;
        public double StartAngleDeg;
        public double EndAngleDeg;
        public string TargetBodyId;
    }

    public sealed class M3BooleanRequest
    {
        public string RevisionId, Operation, TargetBodyId;
        public string[] ToolBodyIds;
        public bool RetainTools;
    }

    public sealed class M3EdgeBlendRequest
    {
        public string RevisionId, TargetBodyId;
        public string[] EdgeIds;
        public double RadiusMm;
    }

    public sealed class M3HoleRequest
    {
        public string RevisionId, TargetBodyId, Kind, DepthMode;
        public double[] PositionMm, Direction;
        public double DiameterMm, HoleDepthMm, CounterboreDiameterMm, CounterboreDepthMm;
        public double CountersinkDiameterMm, CountersinkAngleDeg;
        public string ThreadStandard, ThreadSize, RadialEngage, Hand;
        public double PitchMm, ThreadDepthMm;
    }

    public sealed class M3PlanSnapshot
    {
        public string Kind;
        public string RevisionId;
        public string SketchId;
        public string FeatureId;
        public string BodyId;
        public Dictionary<string, object> PublicView;
        public object AdapterState;
    }

    public sealed class M3ExecutionResult
    {
        // APPLIED, ROLLED_BACK, UNCHANGED, or UNKNOWN. The bridge freezes future
        // model writes for UNKNOWN and never automatically retries the plan.
        public string State;
        public string RevisionId;
        public Dictionary<string, object> Result;
        public string ErrorCode;
        public string ErrorMessage;
    }

    public sealed class M3VerificationResult
    {
        public bool Verified;
        public string RevisionId;
        public Dictionary<string, object> Checks;
        public Dictionary<string, object> Result;
        public string ErrorCode;
        public string ErrorMessage;
    }
}
