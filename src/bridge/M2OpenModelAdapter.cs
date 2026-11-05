using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

// NXOpen-backed adapter. M2Bridge calls these methods only after takeover and
// only on the WinForms dispatcher thread. Parameter edits are opt-in and limited
// to a revalidated Driving sketch dimension with a supported geometry readback.
public sealed partial class M2OpenModelAdapter : IM2ModelAdapter, IM2OperationAdapter, IM2M27ModelAdapter, IM3OperationAdapter,
    IM3DatumPlaneOperationAdapter, IM3RevolveOperationAdapter, IM3HoleOperationAdapter, IM3BooleanOperationAdapter, IM3EdgeBlendOperationAdapter, IM3ChamferOperationAdapter, IM3FeaturePatternOperationAdapter, IM3FeatureMirrorOperationAdapter, IM3SpatialCurveOperationAdapter, IM3SweepOperationAdapter, IM3SketchCornerOperationAdapter, IM3SketchOffsetOperationAdapter,
    IM4FaceQueryAdapter, IM7HelicalOperationAdapter
{
    private const double M4FaceNormalDotTolerance = 0.999;
    private const double M3FrameToleranceMm = 1e-5;
    private const double M3VectorTolerance = 1e-6;
    private const double M3AngleToleranceDeg = 1e-4;
    private const double M3DatumLineLengthToleranceMm = 1e-6;
    private const double M3DatumLineParallelTolerance = 1e-10;
    private const double M3DatumLineDirectionTolerance = 1e-7;
    private const double M3DatumLinePlaneToleranceMm = 1e-6;
    private const double M3ProfileEndpointToleranceMm = 1e-6;
    private const double M3LegacyMassPropertyAccuracy = 0.99;
    private const double F12MassPropertyAccuracy = 0.999999;
    private const double M4BoundaryChordToleranceMm = 0.001;
    private const double M4DomainClearanceMm = 0.002;
    private const string M4SelectorAttribute = "M4_SUPPORT_FACE_SELECTOR";
    private const string M4BodyAttribute = "M4_SUPPORT_BODY_ID";
    private const string M4DomainAttribute = "M4_SUPPORT_DOMAIN_VALIDATED";
    private const string M4BoundaryMethodAttribute = "M4_SUPPORT_BOUNDARY_METHOD";
    private const string M4CutFeatureAttribute = "M4_SUPPORT_OWN_CUT_FEATURE_IDS";

    private sealed class M4FaceSelector
    {
        internal int Axis;
        internal int Sign;
        internal string AxisName;
        internal string PositionRule;
        internal double? CoordinateMm;
        internal string Canonical;
    }

    private sealed class M4FaceLoop
    {
        internal List<M4Point2> Points;
        internal string EdgeType;
    }

    private sealed class M4Profile
    {
        internal string Kind;
        internal List<M4Point2> Points;
        internal List<string> GeometryIds = new List<string>();
        internal M4Point2 Center;
        internal double Radius;
        internal bool Closed;
    }

    private sealed class M4LineSegment
    {
        internal M4Point2 First;
        internal M4Point2 Last;
        internal string Id;
    }

    private struct M4Point2
    {
        internal double X;
        internal double Y;
        internal M4Point2(double x, double y) { X = x; Y = y; }
    }

    private sealed class M4FaceCandidate
    {
        internal Face Face;
        internal Body Body;
        internal string BodyId;
        internal string FaceId;
        internal double PlaneCoordinate;
        internal double PlaneOffset;
        internal double[] Normal;
        internal double[] Origin;
        internal double[] XAxis;
        internal double[] YAxis;
        internal double[] Bbox;
        internal double[] Centroid;
        internal double Area;
        internal List<M4FaceLoop> Loops;
        internal List<string> BoundaryTypes;
        internal List<M4Profile> SupportProfiles;
        internal bool BoundarySupported;
        internal string BoundaryReason;
        internal Dictionary<string, string> FeatureJournalIdsByTag;
        internal List<object> ExcludedCutFeatures;
        internal List<object> EdgeFeatureProvenance = new List<object>();
        internal List<object> LoopProvenance = new List<object>();
    }

    private sealed class M4SupportTrack
    {
        internal string SketchId;
        internal string BodyId;
        internal string Selector;
        internal string FaceId;
        internal string FeatureId;
        internal Plane Plane;
        internal bool DomainValidated;
        internal bool Native;
        internal string GeometryKind;
        internal double[] SupportOrigin;
        internal List<string> CutFeatureIds = new List<string>();
    }

    private sealed class BindingState
    {
        internal Session Session;
        internal Part WorkPart;
        internal string PartTag;
        internal string PartPath;
        internal string CanonicalPath;
        internal Dictionary<string, object> SupportSummary;
        internal string NxBuild;
        internal string NxBuildSource;
        internal string CanonicalSnapshot;
        internal List<object> CanonicalBodyMeasurements;
        internal int RevisionNumber;
        internal Dictionary<string, object> ReadEffects;
        internal readonly Dictionary<string, M4SupportTrack> M4SupportTracks =
            new Dictionary<string, M4SupportTrack>(StringComparer.Ordinal);
    }

    private sealed class SnapshotResult
    {
        internal Dictionary<string, object> Snapshot;
        internal Dictionary<string, object> ReadEffects;
        internal bool SemanticChanged;
    }

    private sealed class ParameterPlanState
    {
        internal BindingState Binding;
        internal Sketch Sketch;
        internal SketchDimensionalConstraint Dimension;
        internal Expression Expression;
        internal Unit Unit;
        internal string RevisionId;
        internal string SemanticSnapshot;
        internal string FullGeometrySnapshot;
        internal List<object> HistorySnapshot;
        internal List<object> AssociationSnapshot;
        internal string SketchId;
        internal string ConstraintId;
        internal string SketchFeatureId;
        internal string ExpressionName;
        internal string ExpressionRightHandSide;
        internal string UnitSymbol;
        internal string DimensionType;
        internal string GeometrySignature;
        internal string GeometryKind;
        internal List<string> GeometryRefs;
        internal double CurrentExpressionValue;
        internal double GeometryValue;
        internal double RequestedValue;
        internal string RequestedValueText;
        internal string RequestedUnit;
        internal Dictionary<string, object> GeometryBefore;
        internal List<object> M4SupportAssociationsBefore;
        internal bool HasM4SupportAssociations;
    }

    private sealed class ParameterTarget
    {
        internal Sketch Sketch;
        internal SketchDimensionalConstraint Dimension;
        internal Expression Expression;
        internal Unit Unit;
        internal string SketchId;
        internal string ConstraintId;
        internal string ExpressionRightHandSide;
        internal string UnitSymbol;
        internal string DimensionType;
        internal string DimensionState;
        internal string GeometrySignature;
        internal string GeometryKind;
        internal List<string> GeometryRefs;
        internal double ExpressionValue;
        internal double GeometryValue;
        internal Dictionary<string, object> GeometryReadback;
    }

    private sealed class M3DimensionState
    {
        internal SketchDimensionalConstraint Dimension;
        internal string GeometryId;
        internal string Role;
        internal double ExpectedValue;
    }

    private sealed class M3PrimitiveState
    {
        internal string RequestId;
        internal string Type;
        internal Curve[] Curves;
        internal List<M3DimensionState> Dimensions;
        internal double[] LocalPoints;
        internal double Width;
        internal double Height;
        internal double Diameter;
        internal double[] CenterMm;
        internal double Radius;
        internal double MajorRadius;
        internal double MinorRadius;
        internal double RotationDeg;
        internal double StartAngleDeg;
        internal double SweepDeg;
        internal double[][] InterpolationPointsMm;
        internal double[][] LocalEndpointPairs;
        internal bool[] ClosedCurves;
        internal Dictionary<string, object> SplineDefinition;
    }

    private sealed class M3BodyMeasure
    {
        internal Body Body;
        internal string Id;
        internal bool IsSolid;
        internal double? VolumeMm3;
        internal double[] BboxMm;
        internal Dictionary<string, object> PublicRecord;
    }

    private sealed class M3PlanState
    {
        internal BindingState BindingState;
        internal string Kind;
        internal string RevisionId;
        internal string SemanticBefore;
        internal Dictionary<string, object> F12SemanticBefore;
        internal Dictionary<string, object> F12PreservationEvidence;
        internal string F12CurveInventoryBefore;
        internal string F12CurveInventoryAfter;
        internal bool ForcePostCommitVerificationFailureForTesting = false;
        internal string GeometryBefore;
        internal List<object> HistoryBefore;
        internal Dictionary<string, object> SketchRecordBefore;
        internal List<M3BodyMeasure> BodiesBefore;
        internal M3SketchRequest SketchRequest;
        internal M3DatumPlaneRequest DatumPlaneRequest;
        internal M3ExtrudeRequest ExtrudeRequest;
        internal M3RevolveRequest RevolveRequest;
        internal M3BooleanRequest BooleanRequest;
        internal M3EdgeBlendRequest EdgeBlendRequest;
        internal M3ChamferRequest ChamferRequest;
        internal M3FeaturePatternRequest FeaturePatternRequest;
        internal M3FeatureMirrorRequest FeatureMirrorRequest;
        internal M3SpatialCurveRequest SpatialCurveRequest;
        internal F21SweepPlanData F21Sweep;
        internal F20SpatialGeometry F20Geometry;
        internal string F20CurveId, F20CurveTag, F20InventoryBefore, F20SemanticBefore, F20GeometryAfter;
        internal Dictionary<string, M3SketchProjectionCurveState> F20CurvesBefore;
        internal Dictionary<string, object> F19Definition, F19Geometry, F19MirrorsBefore;
        internal double[] F19Origin, F19Normal;
        internal string F19DatumTag;
        internal string F19PlanesBefore, F19DatumsBefore;
        internal Dictionary<string, object> F19ExpressionsBefore, F19ExpressionsAfter;
        internal HashSet<string> F19SeedExpressionTags;
        internal HashSet<string> F19OldMirrorBodyTags;
        internal Dictionary<string, Dictionary<string, object>> F19BodyDetailsBefore;
        internal List<string> F19DatumChildrenBefore;
        internal double[] F18SeedBox;
        internal Dictionary<string, object> F18Definition, F18Geometry, F18PatternsBefore;
        internal string F18SeedTag;
        internal string F18InventoryBefore;
        internal Dictionary<string, string> F18StableBefore;
        internal Dictionary<string, string> F18PatternReferenceTagsBefore;
        internal Dictionary<string, Dictionary<string, object>> F18BodyDetailsAfter;
        internal double F17OffsetJ, F17OffsetK;
        internal bool F17Reverse, F17ReverseBefore, F17Corrected;
        internal Dictionary<string, object> F17SwappedCoverage, F17PreCorrectionGeometry, F17PreCorrectionDefinition;
        internal Dictionary<string, object> ChamferDefinition, ChamferGeometry, ChamferDefinitionsBefore;
        internal Edge[] EdgeBlendEdges;
        internal string[] EdgeBlendTags;
        internal List<F16Edge> EdgeBlendInput;
        internal double[] EdgeBlendBox;
        internal Dictionary<string, object> EdgeBlendDefinition, EdgeBlendGeometry, EdgeBlendDefinitionsBefore;
        internal Body[] BooleanTools;
        internal string BooleanTargetTag;
        internal string[] BooleanToolTags;
        internal Dictionary<string, object> BooleanDefinition;
        internal Dictionary<string, object> BooleanDefinitionsBefore;
        internal Dictionary<string, object> BooleanResultGeometry;
        internal M3HoleRequest HoleRequest;
        internal F14ThreadStandardResult HoleThreadStandard;
        internal Dictionary<string, object> HoleDefinition;
        internal Dictionary<string, object> HoleResultGeometry;
        internal Dictionary<string, object> HoleCoverage;
        internal Dictionary<string, string> HoleHelpersBefore;
        internal Dictionary<string, object> HoleDefinitionsBefore;
        internal string HoleSourcePointTag;
        internal Dictionary<string, M3SketchProjectionCurveState> HoleCurvesBefore;
        internal Sketch Sketch;
        internal Feature Feature;
        internal DatumPlane DatumPlane;
        internal Body TargetBody;
        internal string SketchId;
        internal string FeatureId;
        internal string DatumPlaneId;
        internal string TargetBodyId;
        internal string AffectedBodyId;
        internal List<string> AffectedBodyIds;
        internal List<M3BodyMeasure> AffectedBodies;
        internal Dictionary<string, string> BodyFingerprintsBefore;
        internal Dictionary<string, string> BodyFingerprintsAfter;
        internal Dictionary<string, object> ExtrudeSectionEvidence;
        internal double? SheetSurfaceAreaMm2;
        internal double? ExpectedSheetSurfaceAreaMm2;
        internal Dictionary<string, object> SheetAreaEvidence;
        internal List<M3PrimitiveState> Primitives;
        internal List<M3BodyMeasure> BodiesAfter;
        internal string SemanticAfter;
        internal string GeometryAfter;
        internal List<object> HistoryAfter;
        internal List<string> ConstraintIds;
        internal List<object> DrivingDimensions;
        internal Dictionary<string, object> ExtrudeDefinitionEvidence;
        internal Dictionary<string, object> RevolveDefinitionEvidence;
        internal Dictionary<string, object> RevolveOutputGeometry;
        internal double RevolveAxisPlaneResidualMm;
        internal double RevolveAxisNormalDot;
        internal string RevolveAxisTag;
        internal string RevolveAxisPointTag;
        internal string RevolveAxisDirectionTag;
        internal double RevolveNativeStartAngleDeg;
        internal double RevolveNativeEndAngleDeg;
        internal Dictionary<string, object> ProfileAnalysis;
        internal M3ExecutionResult Execution;
        internal Sketch ActiveSketchBefore;
        internal bool WorkPartModifiedBefore;
        internal M4FaceCandidate SupportFace;
        internal Plane SupportPlane;
        internal DatumPlane SupportDatumPlane;
        internal M4FaceCandidate DatumReferenceFace;
        internal DatumPlane DatumReferencePlane;
        internal Feature DatumReferenceFeature;
        internal double[] DatumReferenceOrigin;
        internal double[] DatumReferenceNormal;
        internal Line DatumAxisLine;
        internal double[] DatumAxisStartMm;
        internal double[] DatumAxisEndMm;
        internal double[] DatumExpectedNormal;
        internal Line DatumLine1;
        internal Line DatumLine2;
        internal double[] DatumLine1StartMm;
        internal double[] DatumLine1EndMm;
        internal double[] DatumLine2StartMm;
        internal double[] DatumLine2EndMm;
        internal double[] DatumTwoLineExpectedOriginMm;
        internal double[] DatumTwoLineExpectedNormal;
        internal List<string> DatumLine1SourceFeatureIds;
        internal List<string> DatumLine2SourceFeatureIds;
        internal string SupportKind;
        internal string SupportObjectId;
        internal double[] SupportFrameOrigin;
        internal double[] SupportFrameX;
        internal double[] SupportFrameY;
        internal double[] SupportFrameNormal;
        internal bool SupportDomainValidated;
        internal Dictionary<string, object> SupportAssociationEvidence;
        internal Dictionary<string, object> ExecutionFailureEvidence;
    }

    public M2ModelBinding Takeover(string expectedPartPath, string artifactScope, string sessionEpoch,
        string loadInstanceId, int bindingEpoch)
    {
        bool currentWorkPartScope = String.Equals(artifactScope, "current_work_part", StringComparison.Ordinal);
        if (!currentWorkPartScope && !String.Equals(artifactScope, "saved_independent_copy", StringComparison.Ordinal))
            throw new M2ModelException("INVALID_REQUEST", "artifact_scope must be saved_independent_copy or current_work_part");
        if (String.IsNullOrWhiteSpace(expectedPartPath) && !currentWorkPartScope)
            throw new M2ModelException("INVALID_REQUEST", "expected_part_path is required for saved_independent_copy");

        try
        {
            Session session = Session.GetSession();
            if (session == null) throw new M2ModelException("MODEL_READ_FAILED", "NX Session is unavailable");
            if (!session.IsBatch)
            {
                UI ui = UI.GetUI();
                if (ui.AskLockStatus() != UI.Status.Unlock || !ui.CanOpenPart())
                    throw new M2ModelException("NX_BUSY", "NX UI is busy; no model read was started");
            }
            if (session.IsInTaskEnvironment)
                throw new M2ModelException("NX_BUSY", "NX task environment is active; no model read was started");

            Part workPart = session.Parts.Work;
            if (workPart == null) throw new M2ModelException("NO_WORK_PART", "NX has no active Work Part");

            string partPath = workPart.FullPath ?? String.Empty;
            string canonicalPath = CanonicalPartIdentityPath(partPath);
            if (!PartIdentityPathMatches(partPath, expectedPartPath))
                throw new M2ModelException("SESSION_MISMATCH", "active Work Part path does not match expected_part_path");

            bool modifiedBefore = workPart.IsModified;
            if (!currentWorkPartScope && modifiedBefore)
                throw new M2ModelException("UNSUPPORTED_ARTIFACT_SCOPE", "active Work Part is modified; takeover requires a saved, clean independent copy");
            if (workPart.ComponentAssembly != null && workPart.ComponentAssembly.RootComponent != null)
                throw new M2ModelException("ASSEMBLY_UNSUPPORTED", "M5 supports a standalone native part; assembly Work Parts are not supported");
            bool ugmanagerActive;
            UFSession.GetUFSession().UF.IsUgmanagerActive(out ugmanagerActive);
            if (ugmanagerActive || partPath.StartsWith("@DB", StringComparison.OrdinalIgnoreCase))
                throw new M2ModelException("TEAMCENTER_UNSUPPORTED", "Teamcenter managed parts are outside the M5 independent-part scope");
            Dictionary<string, object> supportSummary = BuildArtifactSupportSummary(workPart, artifactScope);
            string rejection = Convert.ToString(supportSummary["rejection_code"], CultureInfo.InvariantCulture);
            if (!String.IsNullOrEmpty(rejection))
                throw new M2ModelException(rejection,
                    Convert.ToString(supportSummary["rejection_reason"], CultureInfo.InvariantCulture));
            if (!currentWorkPartScope && !File.Exists(partPath))
                throw new M2ModelException("UNSUPPORTED_ARTIFACT_SCOPE", "saved_independent_copy requires an existing native .prt file");
            if (!currentWorkPartScope && !String.Equals(Path.GetExtension(partPath), ".prt", StringComparison.OrdinalIgnoreCase))
                throw new M2ModelException("UNSUPPORTED_ARTIFACT_SCOPE", "the active Work Part is not a native .prt part");

            string partTag = workPart.Tag.ToString();
            if (String.IsNullOrEmpty(partTag))
                throw new M2ModelException("UNSUPPORTED_MODEL", "active Work Part has no stable NX Tag");

            BindingState state = new BindingState();
            state.Session = session;
            state.WorkPart = workPart;
            state.PartTag = partTag;
            state.PartPath = partPath;
            state.CanonicalPath = canonicalPath;
            state.SupportSummary = supportSummary;
            state.NxBuild = GetNxBuild(session);
            state.NxBuildSource = String.IsNullOrEmpty(state.NxBuild)
                ? "NX_FULL_VERSION unavailable"
                : "Session.GetEnvironmentVariableValue(NX_FULL_VERSION)";
            state.RevisionNumber = 0;

            M2ModelBinding binding = new M2ModelBinding();
            binding.SessionEpoch = sessionEpoch;
            binding.LoadInstanceId = loadInstanceId;
            binding.BindingEpoch = bindingEpoch;
            binding.ArtifactScope = artifactScope;
            binding.ArtifactKind = "standalone_part";
            binding.ArtifactAssessment = supportSummary;
            binding.IsReadOnly = workPart.IsReadOnly;
            binding.PartPath = partPath;
            binding.PartTag = partTag;
            binding.NxBuild = state.NxBuild;
            binding.NxBuildSource = state.NxBuildSource;
            binding.AdapterState = state;

            SnapshotResult captured = Capture(binding, state);
            bool modifiedAfterTakeoverRead = workPart.IsModified;
            captured.ReadEffects["is_modified_before_read"] = modifiedBefore;
            captured.ReadEffects["is_modified_after_read"] = modifiedAfterTakeoverRead;
            captured.ReadEffects["dirty_changed_by_read"] = !modifiedBefore && modifiedAfterTakeoverRead;
            captured.Snapshot["support_summary"] = supportSummary;
            binding.RevisionId = state.RevisionNumber.ToString(CultureInfo.InvariantCulture);
            binding.CanonicalSnapshot = state.CanonicalSnapshot;
            binding.Identity = BuildIdentity(binding, workPart, captured.ReadEffects);
            return binding;
        }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "NX takeover inspection failed (" + ex.GetType().Name + ")");
        }
    }

    public bool IsBindingCurrent(M2ModelBinding binding, out string reason)
    {
        reason = null;
        BindingState state = binding == null ? null : binding.AdapterState as BindingState;
        if (state == null)
        {
            reason = "model binding has no NX adapter state";
            return false;
        }
        try
        {
            Session currentSession = Session.GetSession();
            if (!Object.ReferenceEquals(currentSession, state.Session))
            {
                reason = "NX Session identity changed after takeover";
                return false;
            }
            Part currentPart = currentSession.Parts.Work;
            if (currentPart == null)
            {
                reason = "NX Work Part was closed after takeover";
                return false;
            }
            if (!String.Equals(currentPart.Tag.ToString(), state.PartTag, StringComparison.Ordinal))
            {
                reason = "NX Work Part Tag changed after takeover";
                return false;
            }
            if (!String.Equals(CanonicalPartIdentityPath(currentPart.FullPath), state.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                reason = "NX Work Part path changed after takeover";
                return false;
            }
            if (binding.BindingEpoch <= 0 || String.IsNullOrEmpty(binding.SessionEpoch) || String.IsNullOrEmpty(binding.LoadInstanceId))
            {
                reason = "NX binding epoch or session identity is invalid";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = "NX identity could not be verified (" + ex.GetType().Name + ")";
            return false;
        }
    }

    public Dictionary<string, object> GetSession(M2ModelBinding binding)
    {
        BindingState state = RequireState(binding);
        SnapshotResult captured = Capture(binding, state);
        Part part = state.WorkPart;
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("nx_build", state.NxBuild ?? "UNKNOWN");
        result.Add("nx_build_source", state.NxBuildSource);
        result.Add("nxopen_assembly_version", typeof(Session).Assembly.GetName().Version.ToString());
        result.Add("nxopen_file_version", GetNxOpenFileVersion());
        result.Add("release_number", state.Session.ReleaseNumber.ToString(CultureInfo.InvariantCulture));
        result.Add("full_release_number", state.Session.FullReleaseNumber.ToString(CultureInfo.InvariantCulture));
        result.Add("application_name", state.Session.ApplicationName ?? String.Empty);
        result.Add("ugii_version", Environment.GetEnvironmentVariable("UGII_VERSION") ?? String.Empty);
        result.Add("process_id", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
        result.Add("session_epoch", binding.SessionEpoch);
        result.Add("load_instance_id", binding.LoadInstanceId);
        result.Add("binding_epoch", binding.BindingEpoch);
        result.Add("part_path", state.PartPath);
        result.Add("part_tag", state.PartTag);
        result.Add("part_units", part.PartUnits.ToString());
        result.Add("part_load_state", part.PartLoadState.ToString());
        result.Add("is_read_only", IsPartReadOnly(part));
        result.Add("artifact_scope", binding.ArtifactScope);
        result.Add("artifact_kind", binding.ArtifactKind);
        result.Add("support_summary", Clone(binding.ArtifactAssessment));
        result.Add("is_modified_before_read", captured.ReadEffects["is_modified_before_read"]);
        result.Add("is_modified_after_read", captured.ReadEffects["is_modified_after_read"]);
        result.Add("dirty_changed_by_read", captured.ReadEffects["dirty_changed_by_read"]);
        result.Add("read_may_mark_modified", true);
        result.Add("revision_id", binding.RevisionId);
        result.Add("semantic_changed_since_previous_read", captured.SemanticChanged);
        result.Add("support", SupportStatus("SUPPORTED", null));
        return result;
    }

    public Dictionary<string, object> InspectPart(M2ModelBinding binding)
    {
        BindingState state = RequireState(binding);
        SnapshotResult captured = Capture(binding, state);
        Dictionary<string, object> result = Clone(captured.Snapshot);
        result["read_effects"] = captured.ReadEffects;
        result["revision_id"] = binding.RevisionId;
        result["semantic_changed_since_previous_read"] = captured.SemanticChanged;
        return result;
    }

    public Dictionary<string, object> InspectSketch(M2ModelBinding binding, string sketchId)
    {
        if (String.IsNullOrWhiteSpace(sketchId))
            throw new M2ModelException("INVALID_REQUEST", "sketch_id is required");
        BindingState state = RequireState(binding);
        SnapshotResult captured = Capture(binding, state);
        List<object> sketches = captured.Snapshot["sketches"] as List<object>;
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch inventory is unavailable");

        Dictionary<string, object> found = null;
        foreach (object item in sketches)
        {
            Dictionary<string, object> sketch = item as Dictionary<string, object>;
            if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch inventory contains an invalid record");
            if (String.Equals(Convert.ToString(sketch["journal_id"], CultureInfo.InvariantCulture), sketchId, StringComparison.Ordinal))
            {
                found = sketch;
                break;
            }
        }
        if (found == null) throw new M2ModelException("SKETCH_NOT_FOUND", "sketch_id does not identify a Sketch in the bound Work Part");

        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("sketch", found);
        result.Add("part_path", state.PartPath);
        result.Add("part_tag", state.PartTag);
        result.Add("part_units", state.WorkPart.PartUnits.ToString());
        result.Add("read_effects", captured.ReadEffects);
        result.Add("revision_id", binding.RevisionId);
        result.Add("semantic_changed_since_previous_read", captured.SemanticChanged);
        object supportAssociation;
        if (found.TryGetValue("support_association", out supportAssociation))
            result.Add("support_association", supportAssociation);
        object supportFrame;
        if (found.TryGetValue("support_frame", out supportFrame))
            result.Add("support_frame", supportFrame);
        object profileGeometry;
        if (found.TryGetValue("geometry", out profileGeometry))
            result.Add("profile_geometry", profileGeometry);
        return result;
    }

    public Dictionary<string, object> QueryFaces(M2ModelBinding binding, string revisionId,
        string selectorText, string bodyId)
    {
        if (String.IsNullOrWhiteSpace(selectorText))
            throw new M2ModelException("INVALID_REQUEST", "selector is required");
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart, true);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        SnapshotResult captured = Capture(binding, state);
        if (!String.Equals(revisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id changed while querying planar faces");

        bool allPlanar = String.Equals(selectorText.Trim(), "all_planar", StringComparison.OrdinalIgnoreCase);
        M4FaceSelector selector = null;
        if (!allPlanar && !TryParseM4FaceSelector(selectorText, out selector))
            return M4UnsupportedSelectorResult(selectorText, binding.RevisionId);

        List<M4FaceCandidate> candidates = allPlanar
            ? ResolveM3AllPlanarFaces(state.WorkPart, bodyId)
            : ResolveM4Faces(state.WorkPart, selector, bodyId, null, null, null, null);
        if (!allPlanar && selector.PositionRule == "outermost" && candidates.Count > 0)
        {
            double extreme = candidates[0].PlaneCoordinate;
            for (int i = 1; i < candidates.Count; i++)
                extreme = selector.Sign > 0 ? Math.Max(extreme, candidates[i].PlaneCoordinate) : Math.Min(extreme, candidates[i].PlaneCoordinate);
            candidates.RemoveAll(delegate(M4FaceCandidate item)
            {
                return Math.Abs(item.PlaneCoordinate - extreme) > 1e-6;
            });
        }
        else if (!allPlanar && selector.PositionRule == "at" && candidates.Count > 0)
        {
            double expected = selector.CoordinateMm.Value;
            candidates.RemoveAll(delegate(M4FaceCandidate item)
            {
                return Math.Abs(item.PlaneCoordinate - expected) > 1e-5;
            });
        }
        SortM4FaceCandidates(candidates);

        string status = allPlanar ? (candidates.Count == 0 ? "none" : "all") :
            (candidates.Count == 1 ? "unique" : (candidates.Count > 1 ? "ambiguous" : "none"));
        List<object> publicCandidates = new List<object>();
        for (int i = 0; i < candidates.Count; i++)
        {
            Dictionary<string, object> item = M4FaceCandidateRecord(candidates[i]);
            item["candidate_id"] = "candidate-" + (i + 1).ToString("D3", CultureInfo.InvariantCulture);
            publicCandidates.Add(item);
        }
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("selection_status", status);
        result.Add("selector_normalized", allPlanar
            ? (object)new Dictionary<string, object>(StringComparer.Ordinal) { { "canonical", "all_planar" }, { "surface_type", "planar" } }
            : M4SelectorRecord(selector));
        result.Add("candidate_count", candidates.Count);
        result.Add("candidates", publicCandidates);
        result.Add("clarification_required", !allPlanar && candidates.Count != 1);
        result.Add("revision_id", binding.RevisionId);
        result.Add("supported_selector_examples", M4SupportedSelectorExamples());
        return result;
    }

    public M2ParameterPlanSnapshot PrepareParameterEdit(M2ModelBinding binding, M2ParameterEditRequest request)
    {
        if (request == null) throw new M2ModelException("INVALID_REQUEST", "parameter edit request is required");
        if (String.IsNullOrWhiteSpace(request.RevisionId) || String.IsNullOrWhiteSpace(request.SketchId) ||
            String.IsNullOrWhiteSpace(request.ConstraintId) || String.IsNullOrWhiteSpace(request.ValueText) ||
            String.IsNullOrWhiteSpace(request.Unit))
            throw new M2ModelException("INVALID_REQUEST", "revision_id, sketch_id, constraint_id, value_text and unit are required");

        double requestedValue = ParsePositiveMillimeters(request.ValueText, "value_text");
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            throw new M2ModelException("STALE_BINDING", bindingReason ?? "NX model binding is no longer current");
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current captured model revision");

        SnapshotResult preparedCapture;
        try { preparedCapture = Capture(binding, state); }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("PARAMETER_PREPARE_FAILED", "could not refresh the model revision (" + ex.GetType().Name + ")");
        }
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "model semantics changed while preparing the parameter edit");
        RefreshM4SupportTracksFromPart(state.WorkPart, state);
        RequireM4SubtractContourEditSupported(state.WorkPart, state, request.SketchId);

        ParameterTarget target;
        string fullGeometrySnapshot;
        try
        {
            RequireMillimeterPartAndUnit(state.WorkPart, request.Unit, null);
            target = ResolveParameterTarget(state.WorkPart, request.SketchId, request.ConstraintId);
            if (target.Sketch.Feature == null || target.Sketch.Feature.IsOccurrence ||
                target.Sketch.Feature.OwningPart == null || target.Sketch.Feature.OwningPart.Tag != state.WorkPart.Tag)
                throw new M2ModelException("SKETCH_HISTORY_UNAVAILABLE", "the selected Sketch has no native feature history; only supported operations that append new features are available");
            fullGeometrySnapshot = CaptureFullGeometry(state.WorkPart);
        }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("PARAMETER_PREPARE_FAILED", "selected dimension could not be inspected safely (" + ex.GetType().Name + ")");
        }
        if (!String.Equals(target.UnitSymbol, request.Unit, StringComparison.Ordinal))
            throw new M2ModelException("UNIT_MISMATCH", "requested unit must exactly match the associated expression unit symbol");
        if (!NearlyEqual(target.ExpressionValue, target.GeometryValue))
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "associated expression value does not match the measured target geometry");

        ParameterPlanState planState = new ParameterPlanState();
        planState.Binding = state;
        planState.SemanticSnapshot = CanonicalSemanticSnapshot(preparedCapture.Snapshot);
        planState.FullGeometrySnapshot = fullGeometrySnapshot;
        planState.HistorySnapshot = HistoryProjection(preparedCapture.Snapshot);
        planState.AssociationSnapshot = AssociationProjection(preparedCapture.Snapshot);
        planState.Sketch = target.Sketch;
        planState.Dimension = target.Dimension;
        planState.Expression = target.Expression;
        planState.Unit = target.Unit;
        planState.RevisionId = binding.RevisionId;
        planState.SketchId = target.SketchId;
        planState.ConstraintId = target.ConstraintId;
        planState.SketchFeatureId = target.Sketch.Feature == null
            ? null : RequiredJournalId(target.Sketch.Feature, "sketch feature");
        planState.ExpressionName = target.Expression.Name;
        planState.ExpressionRightHandSide = target.ExpressionRightHandSide;
        planState.UnitSymbol = target.UnitSymbol;
        planState.DimensionType = target.DimensionType;
        planState.GeometrySignature = target.GeometrySignature;
        planState.GeometryKind = target.GeometryKind;
        planState.GeometryRefs = CopyStrings(target.GeometryRefs);
        planState.CurrentExpressionValue = target.ExpressionValue;
        planState.GeometryValue = target.GeometryValue;
        planState.RequestedValue = requestedValue;
        planState.RequestedValueText = request.ValueText.Trim();
        planState.RequestedUnit = request.Unit;
        planState.GeometryBefore = Clone(target.GeometryReadback);
        planState.M4SupportAssociationsBefore = M4SupportSnapshot(state);
        planState.HasM4SupportAssociations = planState.M4SupportAssociationsBefore.Count > 0;

        M2ParameterPlanSnapshot snapshot = new M2ParameterPlanSnapshot();
        snapshot.RevisionId = binding.RevisionId;
        snapshot.SketchId = target.SketchId;
        snapshot.ConstraintId = target.ConstraintId;
        snapshot.DimensionType = target.DimensionType;
        snapshot.DimensionState = target.DimensionState;
        snapshot.SketchFeatureId = planState.SketchFeatureId;
        snapshot.ExpressionName = target.Expression.Name;
        snapshot.ExpressionRightHandSide = target.ExpressionRightHandSide;
        snapshot.CurrentValue = NumberText(target.ExpressionValue);
        snapshot.Unit = target.UnitSymbol;
        snapshot.GeometryRefs = CopyStrings(target.GeometryRefs);
        snapshot.AdapterState = planState;
        return snapshot;
    }

    public M2ParameterExecutionResult ExecuteParameterEdit(M2ModelBinding binding,
        M2ParameterPlanSnapshot prepared, string requestedValue, string requestedUnit)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("update_error_count", -1);
        evidence.Add("rollback_update_error_count", -1);
        evidence.Add("readback_verified", false);
        evidence.Add("rollback_readback_verified", false);
        evidence.Add("write_attempted", false);
        evidence.Add("after_rhs", null);
        evidence.Add("after_value", null);
        evidence.Add("after_unit", null);
        evidence.Add("geometry_after", null);

        ParameterPlanState plan = prepared == null ? null : prepared.AdapterState as ParameterPlanState;
        if (plan == null)
            return ParameterResult("UNCHANGED", binding == null ? null : binding.RevisionId, evidence,
                "INVALID_PLAN", "prepared parameter edit has no NX adapter state");

        AddBeforeEvidence(evidence, plan);
        BindingState state = null;
        try { state = RequireState(binding); }
        catch (M2ModelException ex)
        {
            return ParameterResult("UNCHANGED", binding == null ? null : binding.RevisionId, evidence, ex.Code, ex.Message);
        }
        if (!Object.ReferenceEquals(state, plan.Binding))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_BINDING", "prepared edit belongs to a different NX model binding");
        try { RequireWritableSimplePart(state.WorkPart); }
        catch (M2ModelException ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, ex.Code, ex.Message);
        }
        if (!PlanFieldsMatch(prepared, plan))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "INVALID_PLAN", "prepared plan fields do not match its retained NX state");

        double parsedRequested;
        try { parsedRequested = ParsePositiveMillimeters(requestedValue, "requested_value"); }
        catch (M2ModelException ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, ex.Code, ex.Message);
        }
        if (!NearlyEqual(parsedRequested, plan.RequestedValue) ||
            !String.Equals(requestedValue == null ? null : requestedValue.Trim(), plan.RequestedValueText, StringComparison.Ordinal) ||
            !String.Equals(requestedUnit, plan.RequestedUnit, StringComparison.Ordinal))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "INVALID_REQUEST", "execution value and unit must match the prepared request");

        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_BINDING", bindingReason ?? "NX model binding is no longer current");
        if (!String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_REVISION", "model revision changed after parameter preparation");

        SnapshotResult currentCapture;
        try { currentCapture = Capture(binding, state); }
        catch (Exception ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "MODEL_READ_FAILED", "could not refresh the model revision before editing (" + ex.GetType().Name + ")");
        }
        if (!String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_REVISION", "model semantics changed after parameter preparation");
        try
        {
            if (!String.Equals(CanonicalSemanticSnapshot(currentCapture.Snapshot), plan.SemanticSnapshot, StringComparison.Ordinal) ||
                !String.Equals(CaptureFullGeometry(state.WorkPart), plan.FullGeometrySnapshot, StringComparison.Ordinal))
                return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_GEOMETRY", "part geometry or semantic state changed after parameter preparation");
        }
        catch (Exception ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "MODEL_READ_FAILED", "could not verify complete geometry before editing (" + ex.GetType().Name + ")");
        }

        ParameterTarget current;
        try
        {
            RequireWritableSimplePart(state.WorkPart);
            RequireMillimeterPartAndUnit(state.WorkPart, requestedUnit, plan.UnitSymbol);
            current = ResolveParameterTarget(state.WorkPart, plan.SketchId, plan.ConstraintId);
            RequireSameTarget(plan, current);
        }
        catch (M2ModelException ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "PARAMETER_READ_FAILED", "selected dimension could not be revalidated (" + ex.GetType().Name + ")");
        }

        if (!String.Equals(current.ExpressionRightHandSide, plan.ExpressionRightHandSide, StringComparison.Ordinal) ||
            !NearlyEqual(current.ExpressionValue, plan.CurrentExpressionValue) ||
            !NearlyEqual(current.GeometryValue, plan.GeometryValue))
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "STALE_PARAMETER", "selected expression or measured geometry changed after preparation");

        evidence["association_verified_before"] = true;
        evidence["unit_verified_before"] = true;
        if (NearlyEqual(parsedRequested, current.GeometryValue) && NearlyEqual(parsedRequested, current.ExpressionValue))
        {
            SetFinalReadbackEvidence(evidence, current);
            evidence["readback_verified"] = true;
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, null, null);
        }

        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "M2 selected sketch dimension edit"); }
        catch (Exception ex)
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "UPDATE_FAILED", "could not create an NX update mark (" + ex.GetType().Name + ")");
        }

        int updateErrors = -1;
        bool updateCallReturned = false;
        string operationError = null;
        ParameterTarget after = null;
        bool m4SupportsValid = true;
        List<object> m4SupportEvidence = new List<object>();
        try
        {
            evidence["write_attempted"] = true;
            state.WorkPart.Expressions.EditExpressionWithUnits(current.Expression, current.Unit, plan.RequestedValueText);
            updateErrors = state.Session.UpdateManager.DoUpdate(mark);
            updateCallReturned = true;
            evidence["update_error_count"] = updateErrors;
            after = ResolveParameterTarget(state.WorkPart, plan.SketchId, plan.ConstraintId);
            RequireSameTarget(plan, after);
            m4SupportsValid = ValidateM4SupportTracks(state, out m4SupportEvidence, null);
            evidence["support_associations_before"] = plan.M4SupportAssociationsBefore ?? new List<object>();
            evidence["support_associations_after"] = m4SupportEvidence;
            evidence["support_associations_verified"] = m4SupportsValid;
            if (!m4SupportsValid) operationError = "M4_FACE_ASSOCIATION_INVALID: an attached Sketch lost its unique native support face or face-domain contract";
        }
        catch (Exception ex)
        {
            operationError = "parameter edit or verification failed (" + ex.GetType().Name + ")";
        }

        bool geometryChanged = after != null && !NearlyEqual(after.GeometryValue, plan.GeometryValue);
        bool appliedReadback = updateCallReturned && updateErrors == 0 && after != null &&
            geometryChanged && NearlyEqual(after.ExpressionValue, parsedRequested) &&
            NearlyEqual(after.GeometryValue, parsedRequested) &&
            String.Equals(after.UnitSymbol, plan.UnitSymbol, StringComparison.Ordinal) &&
            IsNumericRhsEqual(after.ExpressionRightHandSide, parsedRequested) && m4SupportsValid;
        evidence["geometry_changed"] = geometryChanged;
        evidence["readback_verified"] = appliedReadback;
        if (after != null)
        {
            evidence["attempt_after_rhs"] = after.ExpressionRightHandSide;
            evidence["attempt_after_value"] = NumberText(after.ExpressionValue);
            evidence["attempt_after_unit"] = after.UnitSymbol;
            evidence["geometry_after_attempt"] = after.GeometryReadback;
            evidence["association_verified_after"] = true;
            evidence["unit_verified_after"] = String.Equals(after.UnitSymbol, plan.UnitSymbol, StringComparison.Ordinal);
        }

        if (appliedReadback)
        {
            SetFinalReadbackEvidence(evidence, after);
            try
            {
                Capture(binding, state);
                TryDeleteUndoMark(state.Session, mark);
                return ParameterResult("APPLIED", binding.RevisionId, evidence, null, null);
            }
            catch (Exception ex)
            {
                TryDeleteUndoMark(state.Session, mark);
                evidence["revision_refresh_error"] = ex.GetType().Name;
                return ParameterResult("UNKNOWN", binding.RevisionId, evidence, "POST_EDIT_READ_FAILED", "edit readback succeeded but the semantic revision could not be refreshed");
            }
        }

        if (operationError == null)
            operationError = updateCallReturned && updateErrors != 0
                ? "NX update reported errors"
                : geometryChanged ? "expression and geometry readback did not match the requested value" : "selected geometry did not move to the requested value";
        evidence["operation_error"] = operationError;
        string restorationState = TryRestoreParameter(binding, state, plan, mark, evidence);
        TryDeleteUndoMark(state.Session, mark);
        if (String.Equals(restorationState, "UNCHANGED", StringComparison.Ordinal))
        {
            return ParameterResult("UNCHANGED", binding.RevisionId, evidence, "EDIT_FAILED_UNCHANGED", operationError);
        }
        if (String.Equals(restorationState, "ROLLED_BACK", StringComparison.Ordinal))
        {
            evidence["rollback_readback_verified"] = true;
            return ParameterResult("ROLLED_BACK", binding.RevisionId, evidence, "EDIT_NOT_VERIFIED", operationError);
        }
        return ParameterResult("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "NX edit failed and restoration could not be verified");
    }

    public M2PartVerificationResult VerifyParameterEdit(M2ModelBinding binding,
        M2ParameterPlanSnapshot prepared)
    {
        if (prepared == null) throw new M2ModelException("INVALID_PLAN", "prepared plan is required for verification");
        BindingState state = RequireState(binding);
        ParameterPlanState plan = prepared.AdapterState as ParameterPlanState;
        if (plan == null) throw new M2ModelException("INVALID_PLAN", "prepared plan has no retained NX parameter target");
        if (!PlanFieldsMatch(prepared, plan))
            throw new M2ModelException("INVALID_PLAN", "prepared plan fields do not match the retained verification state");
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            throw new M2ModelException("STALE_BINDING", bindingReason ?? "NX model binding is no longer current");

        SnapshotResult captured = Capture(binding, state);
        ParameterTarget actual;
        try { actual = ResolveParameterTarget(state.WorkPart, plan.SketchId, plan.ConstraintId); }
        catch (M2ModelException ex)
        {
            M2PartVerificationResult rejected = VerificationFailure(binding.RevisionId, ex.Code + ": " + ex.Message);
            rejected.ErrorCode = "PART_VERIFICATION_FAILED";
            rejected.ErrorMessage = "The selected Driving expression or geometry relationship could not be resolved from the live NX model.";
            return rejected;
        }

        bool expressionMatches = String.Equals(actual.Expression.Name, plan.ExpressionName, StringComparison.Ordinal) &&
            String.Equals(actual.UnitSymbol, plan.UnitSymbol, StringComparison.Ordinal) &&
            NearlyEqual(actual.ExpressionValue, plan.RequestedValue) &&
            IsNumericRhsEqual(actual.ExpressionRightHandSide, plan.RequestedValue);
        bool geometryMatches = StringListsEqual(actual.GeometryRefs, plan.GeometryRefs) &&
            NearlyEqual(actual.GeometryValue, plan.RequestedValue);
        Dictionary<string, object> historyEvidence;
        bool selectedFeatureMatches = SelectedHistoryMatches(state.WorkPart, actual.Sketch,
            prepared.SketchFeatureId, out historyEvidence);
        List<object> currentHistory = HistoryProjection(captured.Snapshot);
        historyEvidence.Add("before", plan.HistorySnapshot);
        historyEvidence.Add("after", currentHistory);
        bool historyMatches = selectedFeatureMatches &&
            SemanticValuesEqual(plan.HistorySnapshot, currentHistory, "history");

        List<object> currentAssociations = AssociationProjection(captured.Snapshot);
        bool targetSignatureMatches = true;
        string targetSignatureReason = null;
        try { RequireSameTarget(plan, actual); }
        catch (M2ModelException ex)
        {
            targetSignatureMatches = false;
            targetSignatureReason = ex.Code + ": " + ex.Message;
        }
        bool associationMatches = targetSignatureMatches &&
            SemanticValuesEqual(plan.AssociationSnapshot, currentAssociations, "associations");
        List<object> supportAssociationsNow;
        bool supportAssociationsMatch = ValidateM4SupportTracks(state, out supportAssociationsNow, null);

        Dictionary<string, object> expressionEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        expressionEvidence.Add("sketch_id", plan.SketchId);
        expressionEvidence.Add("constraint_id", plan.ConstraintId);
        expressionEvidence.Add("expression_name", actual.Expression.Name);
        expressionEvidence.Add("expected_value", NumberText(plan.RequestedValue));
        expressionEvidence.Add("actual_rhs", actual.ExpressionRightHandSide);
        expressionEvidence.Add("actual_value", NumberText(actual.ExpressionValue));
        expressionEvidence.Add("unit", actual.UnitSymbol);

        Dictionary<string, object> geometryEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        geometryEvidence.Add("sketch_id", plan.SketchId);
        geometryEvidence.Add("constraint_id", plan.ConstraintId);
        geometryEvidence.Add("geometry_refs", ObjectStrings(actual.GeometryRefs));
        geometryEvidence.Add("expected_value", NumberText(plan.RequestedValue));
        geometryEvidence.Add("actual_value", NumberText(actual.GeometryValue));
        geometryEvidence.Add("geometry", actual.GeometryReadback);

        Dictionary<string, object> associationEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        associationEvidence.Add("sketch_id", plan.SketchId);
        associationEvidence.Add("constraint_id", plan.ConstraintId);
        associationEvidence.Add("dimension_state", actual.DimensionState);
        associationEvidence.Add("dimension_type", actual.DimensionType);
        associationEvidence.Add("expression_name", actual.Expression.Name);
        associationEvidence.Add("geometry_refs", ObjectStrings(actual.GeometryRefs));
        associationEvidence.Add("geometry_kind", actual.GeometryKind);
        associationEvidence.Add("geometry_signature", actual.GeometrySignature);
        associationEvidence.Add("unit", actual.UnitSymbol);
        associationEvidence.Add("target_signature_verified", targetSignatureMatches);
        associationEvidence.Add("target_signature_reason", targetSignatureReason);
        associationEvidence.Add("before", plan.AssociationSnapshot);
        associationEvidence.Add("after", currentAssociations);

        Dictionary<string, object> supportEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        supportEvidence.Add("before", plan.M4SupportAssociationsBefore ?? new List<object>());
        supportEvidence.Add("after", supportAssociationsNow);
        supportEvidence.Add("native_unique_domain_verified", supportAssociationsMatch);

        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        checks.Add("expression", VerificationCheck(expressionMatches ? "VERIFIED" : "MISMATCH", expressionEvidence));
        checks.Add("geometry", VerificationCheck(geometryMatches ? "VERIFIED" : "MISMATCH", geometryEvidence));
        checks.Add("history", VerificationCheck(historyMatches ? "VERIFIED" : "MISMATCH", historyEvidence));
        checks.Add("associations", VerificationCheck(associationMatches ? "VERIFIED" : "MISMATCH", associationEvidence));
        if (plan.HasM4SupportAssociations)
            checks.Add("support_associations", VerificationCheck(supportAssociationsMatch ? "VERIFIED" : "MISMATCH", supportEvidence));
        bool verified = expressionMatches && geometryMatches && historyMatches && associationMatches &&
            (!plan.HasM4SupportAssociations || supportAssociationsMatch);
        return new M2PartVerificationResult
        {
            Verified = verified,
            RevisionId = binding.RevisionId,
            Checks = checks,
            ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified ? null : "The live NX expression, geometry, sketch history, or dimensional association differs from the successful execute plan."
        };
    }

    public M3PlanSnapshot PrepareSketch(M2ModelBinding binding, M3SketchRequest request)
    {
        if (request == null)
            throw new M2ModelException("INVALID_REQUEST", "M3 sketch request is required");
        if (request.Geometries == null) request.Geometries = new List<M3SketchGeometryRequest>();
        if (String.IsNullOrEmpty(request.ConstraintMode)) request.ConstraintMode = "legacy_driving";
        if (request.ConstraintMode != "legacy_driving" && request.ConstraintMode != "geometry_only")
            throw new M2ModelException("INVALID_REQUEST", "constraint_mode must be legacy_driving or geometry_only");
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "a Sketch is already active in NX");
        bool selectorFaceMode = !String.IsNullOrWhiteSpace(request.SupportFaceSelector);
        bool explicitFaceMode = !String.IsNullOrWhiteSpace(request.SupportFaceId);
        if (request.SupportFaceTag != null &&
            (String.IsNullOrWhiteSpace(request.SupportFaceTag) || !explicitFaceMode))
            throw new M2ModelException("INVALID_REQUEST", "support_face_tag requires a non-empty support_face_id and support_face_tag");
        bool datumPlaneMode = !String.IsNullOrWhiteSpace(request.SupportDatumPlaneId);
        bool customFrameMode = (request.Normal != null || request.XAxis != null) &&
            String.IsNullOrWhiteSpace(request.SupportFaceId) && String.IsNullOrWhiteSpace(request.SupportDatumPlaneId) &&
            request.SupportFaceTag == null;
        int frameModes = (selectorFaceMode ? 1 : 0) + (explicitFaceMode ? 1 : 0) +
            (datumPlaneMode ? 1 : 0) + (customFrameMode ? 1 : 0);
        if (frameModes > 1)
            throw new M2ModelException("INVALID_REQUEST", "sketch request must select exactly one frame/support mode");
        M4FaceCandidate resolvedSupportFace = null;
        DatumPlane resolvedSupportDatumPlane = null;
        Feature resolvedSupportDatumFeature = null;
        if (selectorFaceMode)
        {
            if (request.Plane != null || request.OriginMm != null || request.Normal != null || request.XAxis != null ||
                request.SupportFaceId != null || request.SupportFaceTag != null || request.SupportDatumPlaneId != null)
                throw new M2ModelException("INVALID_REQUEST", "face sketch mode cannot include plane or origin_mm");
            if (request.FaceOriginOffsetMm == null || request.FaceOriginOffsetMm.Length != 2)
                throw new M2ModelException("INVALID_REQUEST", "face_origin_offset_mm must contain two local coordinates");
            foreach (double value in request.FaceOriginOffsetMm) EnsureFinite(value, "face_origin_offset_mm");
        }
        else if (explicitFaceMode)
        {
            if (request.Plane != null || request.OriginMm == null || request.OriginMm.Length != 3 ||
                request.Normal != null || request.XAxis == null || request.XAxis.Length != 3 ||
                request.SupportFaceSelector != null || request.FaceOriginOffsetMm != null ||
                request.SupportDatumPlaneId != null)
                throw new M2ModelException("INVALID_REQUEST", "face-supported sketch requires support_face_id, origin_mm, and x_axis only");
            foreach (double value in request.OriginMm) EnsureFinite(value, "origin_mm");
            foreach (double value in request.XAxis) EnsureFinite(value, "x_axis");
            resolvedSupportFace = ResolveM3FaceById(state.WorkPart, request.SupportFaceId,
                request.SupportBodyId, request.SupportFaceTag);
            request.Normal = (double[])resolvedSupportFace.Normal.Clone();
            NormalizeM3SketchFrame(request);
            ValidateM3FrameOnFace(resolvedSupportFace, request.OriginMm, request.Normal);
        }
        else if (datumPlaneMode)
        {
            if (request.Plane != null || request.Normal != null || request.XAxis == null || request.XAxis.Length != 3 ||
                request.SupportFaceSelector != null || request.SupportFaceId != null || request.SupportFaceTag != null || request.FaceOriginOffsetMm != null)
                throw new M2ModelException("INVALID_REQUEST", "datum-supported sketch requires support_datum_plane_id and x_axis, with optional origin_mm");
            resolvedSupportDatumPlane = FindM3DatumPlane(state.WorkPart, request.SupportDatumPlaneId, out resolvedSupportDatumFeature);
            if (request.OriginMm == null)
            {
                Point3d datumOrigin = resolvedSupportDatumPlane.Origin;
                request.OriginMm = new double[] { datumOrigin.X, datumOrigin.Y, datumOrigin.Z };
            }
            request.Normal = new double[] { resolvedSupportDatumPlane.Normal.X,
                resolvedSupportDatumPlane.Normal.Y, resolvedSupportDatumPlane.Normal.Z };
            NormalizeM3SketchFrame(request);
            ValidateM3FrameOnDatumPlane(resolvedSupportDatumPlane, request.OriginMm, request.Normal);
        }
        else if (customFrameMode)
        {
            if (request.Plane != null || request.OriginMm == null || request.OriginMm.Length != 3 ||
                request.Normal == null || request.XAxis == null || request.Normal.Length != 3 || request.XAxis.Length != 3 ||
                request.SupportFaceSelector != null || request.SupportFaceId != null || request.SupportFaceTag != null || request.SupportDatumPlaneId != null ||
                request.FaceOriginOffsetMm != null)
                throw new M2ModelException("INVALID_REQUEST", "free sketch frame requires origin_mm, normal, and x_axis");
            NormalizeM3SketchFrame(request);
        }
        else
        {
            if (request.Plane != "XY" && request.Plane != "XZ" && request.Plane != "YZ")
                throw new M2ModelException("INVALID_REQUEST", "plane must be XY, XZ, or YZ");
            if (request.OriginMm == null || request.OriginMm.Length != 3)
                throw new M2ModelException("INVALID_REQUEST", "origin_mm must contain three world coordinates");
            foreach (double value in request.OriginMm) EnsureFinite(value, "origin_mm");
        }
        HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (M3SketchGeometryRequest geometry in request.Geometries)
        {
            if (geometry == null || String.IsNullOrEmpty(geometry.Id) || !ids.Add(geometry.Id))
                throw new M2ModelException("INVALID_REQUEST", "geometry records require unique non-empty ids");
            if (geometry.Type == "line")
            {
                if (geometry.StartMm == null || geometry.EndMm == null || geometry.StartMm.Length != 2 || geometry.EndMm.Length != 2)
                    throw new M2ModelException("INVALID_REQUEST", "line geometry requires 2D start_mm and end_mm");
                foreach (double value in geometry.StartMm) EnsureFinite(value, "line coordinate");
                foreach (double value in geometry.EndMm) EnsureFinite(value, "line coordinate");
                double dx = geometry.EndMm[0] - geometry.StartMm[0];
                double dy = geometry.EndMm[1] - geometry.StartMm[1];
                if (Math.Abs(dx) <= 1e-9 && Math.Abs(dy) <= 1e-9)
                    throw new M2ModelException("INVALID_REQUEST", "line start_mm and end_mm must differ");
            }
            else if (geometry.Type == "rectangle")
            {
                if (geometry.CornerMm == null || geometry.CornerMm.Length != 2 || geometry.WidthMm <= 0 || geometry.HeightMm <= 0)
                    throw new M2ModelException("INVALID_REQUEST", "rectangle requires a 2D corner and positive width_mm/height_mm");
                EnsureFinite(geometry.CornerMm[0], "rectangle corner x");
                EnsureFinite(geometry.CornerMm[1], "rectangle corner y");
                EnsureFinite(geometry.WidthMm, "rectangle width");
                EnsureFinite(geometry.HeightMm, "rectangle height");
            }
            else if (geometry.Type == "circle")
            {
                if (geometry.CenterMm == null || geometry.CenterMm.Length != 2 || geometry.DiameterMm <= 0)
                    throw new M2ModelException("INVALID_REQUEST", "circle requires a 2D center and positive diameter_mm");
                EnsureFinite(geometry.CenterMm[0], "circle center x");
                EnsureFinite(geometry.CenterMm[1], "circle center y");
                EnsureFinite(geometry.DiameterMm, "circle diameter");
            }
            else if (geometry.Type == "arc")
            {
                if (geometry.CenterMm == null || geometry.CenterMm.Length != 2 || geometry.RadiusMm <= 0.0 ||
                    geometry.StartAngleDeg < 0.0 || geometry.StartAngleDeg >= 360.0 ||
                    Math.Abs(geometry.SweepDeg) <= 1e-9 || Math.Abs(geometry.SweepDeg) >= 360.0)
                    throw new M2ModelException("INVALID_REQUEST", "arc requires center_mm, positive radius_mm, start_angle_deg in [0,360), and a nonzero sweep with magnitude below 360 degrees");
                foreach (double value in geometry.CenterMm) EnsureFinite(value, "arc center coordinate");
                EnsureFinite(geometry.RadiusMm, "arc radius");
                EnsureFinite(geometry.StartAngleDeg, "arc start angle");
                EnsureFinite(geometry.SweepDeg, "arc sweep");
            }
            else if (geometry.Type == "ellipse" || geometry.Type == "elliptical_arc")
            {
                if (geometry.CenterMm == null || geometry.CenterMm.Length != 2 || geometry.MajorRadiusMm <= 0.0 ||
                    geometry.MinorRadiusMm <= 0.0 || geometry.MajorRadiusMm < geometry.MinorRadiusMm ||
                    geometry.RotationDeg < 0.0 || geometry.RotationDeg >= 360.0 ||
                    (geometry.Type == "elliptical_arc" && (geometry.StartAngleDeg < 0.0 || geometry.StartAngleDeg >= 360.0 ||
                        Math.Abs(geometry.SweepDeg) <= 1e-9 || Math.Abs(geometry.SweepDeg) >= 360.0)))
                    throw new M2ModelException("INVALID_REQUEST", geometry.Type + " requires valid center/radii/orientation and nondegenerate arc angles");
                foreach (double value in geometry.CenterMm) EnsureFinite(value, geometry.Type + " center coordinate");
                EnsureFinite(geometry.MajorRadiusMm, geometry.Type + " major radius");
                EnsureFinite(geometry.MinorRadiusMm, geometry.Type + " minor radius");
                EnsureFinite(geometry.RotationDeg, geometry.Type + " rotation");
                if (geometry.Type == "elliptical_arc")
                {
                    EnsureFinite(geometry.StartAngleDeg, "elliptical arc start angle");
                    EnsureFinite(geometry.SweepDeg, "elliptical arc sweep");
                }
            }
            else if (geometry.Type == "spline")
            {
                if (geometry.Degree != 3 || geometry.EndpointCondition != "unconstrained" ||
                    geometry.InterpolationPointsMm == null || geometry.InterpolationPointsMm.Length < 4 ||
                    geometry.InterpolationPointsMm.Length > 32)
                    throw new M2ModelException("INVALID_REQUEST", "spline requires degree 3, unconstrained endpoints, and 4 through 32 interpolation points");
                for (int pointIndex = 0; pointIndex < geometry.InterpolationPointsMm.Length; pointIndex++)
                {
                    double[] point = geometry.InterpolationPointsMm[pointIndex];
                    if (point == null || point.Length != 2) throw new M2ModelException("INVALID_REQUEST", "spline interpolation points require two coordinates");
                    EnsureFinite(point[0], "spline x coordinate"); EnsureFinite(point[1], "spline y coordinate");
                    if (pointIndex > 0)
                    {
                        double dx = point[0] - geometry.InterpolationPointsMm[pointIndex - 1][0];
                        double dy = point[1] - geometry.InterpolationPointsMm[pointIndex - 1][1];
                        if (Math.Sqrt(dx * dx + dy * dy) <= 1e-9)
                            throw new M2ModelException("INVALID_REQUEST", "consecutive spline interpolation points must differ");
                    }
                }
            }
            else throw new M2ModelException("UNSUPPORTED_GEOMETRY", "unsupported M3 sketch geometry type");
        }

        SnapshotResult capture = Capture(binding, state);
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current part revision");
        M3PlanState planState = CreateM3PlanState(binding, state, "create_sketch", capture);
        planState.SketchRequest = CloneM3SketchRequest(request);
        planState.SupportKind = selectorFaceMode ? "FACE_SELECTOR" : explicitFaceMode ? "FACE" :
            datumPlaneMode ? "DATUM_PLANE" : customFrameMode ? "WORLD_FRAME" : "PRINCIPAL_PLANE";
        if (selectorFaceMode)
        {
            planState.SupportFace = ResolveM4UniqueFace(state.WorkPart, request.SupportFaceSelector, request.SupportBodyId);
            planState.SupportDomainValidated = ValidateM4SketchProfile(planState.SupportFace,
                request.Geometries, request.FaceOriginOffsetMm, out planState.SupportAssociationEvidence);
            planState.SupportFrameOrigin = M4OffsetFrameOrigin(planState.SupportFace, request.FaceOriginOffsetMm);
            planState.SupportFrameX = (double[])planState.SupportFace.XAxis.Clone();
            planState.SupportFrameY = (double[])planState.SupportFace.YAxis.Clone();
            planState.SupportFrameNormal = (double[])planState.SupportFace.Normal.Clone();
        }
        else if (explicitFaceMode)
        {
            planState.SupportFace = resolvedSupportFace;
            planState.SupportObjectId = planState.SupportFace.FaceId;
            planState.SupportFrameOrigin = (double[])request.OriginMm.Clone();
            planState.SupportFrameX = (double[])request.XAxis.Clone();
            planState.SupportFrameNormal = (double[])request.Normal.Clone();
            planState.SupportFrameY = Cross3(planState.SupportFrameNormal, planState.SupportFrameX);
            Normalize3(planState.SupportFrameY);
        }
        else if (datumPlaneMode)
        {
            planState.SupportDatumPlane = resolvedSupportDatumPlane;
            planState.Feature = resolvedSupportDatumFeature;
            planState.SupportObjectId = request.SupportDatumPlaneId;
            planState.SupportFrameOrigin = (double[])request.OriginMm.Clone();
            planState.SupportFrameX = (double[])request.XAxis.Clone();
            planState.SupportFrameNormal = (double[])request.Normal.Clone();
            planState.SupportFrameY = Cross3(planState.SupportFrameNormal, planState.SupportFrameX);
            Normalize3(planState.SupportFrameY);
        }
        else if (customFrameMode)
        {
            planState.SupportFrameOrigin = (double[])request.OriginMm.Clone();
            planState.SupportFrameX = (double[])request.XAxis.Clone();
            planState.SupportFrameNormal = (double[])request.Normal.Clone();
            planState.SupportFrameY = Cross3(planState.SupportFrameNormal, planState.SupportFrameX);
            Normalize3(planState.SupportFrameY);
        }
        Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal);
        publicView.Add("kind", "create_sketch");
        publicView.Add("revision_id", binding.RevisionId);
        if (selectorFaceMode)
        {
            publicView.Add("support_face_selector", request.SupportFaceSelector);
            publicView.Add("support_body_id", planState.SupportFace.BodyId);
            publicView.Add("face_origin_offset_mm", NumberList(request.FaceOriginOffsetMm));
            publicView.Add("support_face", M4FaceCandidateRecord(planState.SupportFace));
            publicView.Add("support_frame", M4FrameRecord(planState.SupportFrameOrigin,
                planState.SupportFrameX, planState.SupportFrameY, planState.SupportFrameNormal));
            publicView.Add("support_domain", planState.SupportAssociationEvidence);
        }
        else if (explicitFaceMode || datumPlaneMode || customFrameMode)
        {
            publicView.Add("frame_mode", planState.SupportKind);
            publicView.Add("origin_mm", NumberList(planState.SupportFrameOrigin));
            publicView.Add("normal", NumberList(planState.SupportFrameNormal));
            publicView.Add("x_axis", NumberList(planState.SupportFrameX));
            publicView.Add("support_id", planState.SupportObjectId);
            if (explicitFaceMode)
            {
                publicView.Add("support_face", M4FaceCandidateRecord(planState.SupportFace));
                if (request.SupportFaceTag != null) publicView.Add("support_face_tag", request.SupportFaceTag);
            }
        }
        else
        {
            publicView.Add("plane", request.Plane);
            publicView.Add("origin_mm", NumberList(request.OriginMm));
        }
        if (request.ConstraintMode == "geometry_only") publicView.Add("constraint_mode", request.ConstraintMode);
        publicView.Add("geometries", SketchGeometryPublicRecords(request.Geometries));
        publicView.Add("driving_dimensions", request.ConstraintMode == "geometry_only"
            ? new List<object>() : ExpectedDrivingDimensionRecords(request.Geometries));
        return new M3PlanSnapshot
        {
            Kind = "create_sketch",
            RevisionId = binding.RevisionId,
            PublicView = publicView,
            AdapterState = planState
        };
    }

    public M3PlanSnapshot PrepareDatumPlane(M2ModelBinding binding, M3DatumPlaneRequest request)
    {
        if (request == null) throw new M2ModelException("INVALID_REQUEST", "datum plane request is required");
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "a Sketch is active in NX; datum plane preparation is paused");
        NormalizeM3DatumPlaneRequest(request);
        SnapshotResult capture = Capture(binding, state);
        if (!String.Equals(request.RevisionId, binding.RevisionId, StringComparison.Ordinal))
            throw new M2ModelException("STALE_REVISION", "revision_id does not match the current part revision");
        M3PlanState planState = CreateM3PlanState(binding, state, "create_datum_plane", capture);
        planState.DatumPlaneRequest = CloneM3DatumPlaneRequest(request);
        Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal);
        publicView.Add("kind", "create_datum_plane");
        publicView.Add("revision_id", binding.RevisionId);
        publicView.Add("native_feature_type", "DATUM_PLANE");
        if (String.Equals(request.Mode, "offset", StringComparison.Ordinal) ||
            String.Equals(request.Mode, "angle", StringComparison.Ordinal))
        {
            if (!String.IsNullOrWhiteSpace(request.SupportFaceId))
            {
                planState.DatumReferenceFace = ResolveM3FaceById(state.WorkPart, request.SupportFaceId,
                    null, request.SupportFaceTag);
                planState.DatumReferenceOrigin = (double[])planState.DatumReferenceFace.Origin.Clone();
                planState.DatumReferenceNormal = (double[])planState.DatumReferenceFace.Normal.Clone();
                planState.SupportKind = "FACE";
                planState.SupportObjectId = planState.DatumReferenceFace.FaceId;
                publicView.Add("support_kind", "FACE");
                publicView.Add("support_face_id", planState.DatumReferenceFace.FaceId);
                if (request.SupportFaceTag != null) publicView.Add("support_face_tag", request.SupportFaceTag);
            }
            else
            {
                planState.DatumReferencePlane = FindM3DatumPlane(state.WorkPart,
                    request.SupportDatumPlaneId, out planState.DatumReferenceFeature);
                Point3d sourceOrigin = planState.DatumReferencePlane.Origin;
                Vector3d sourceNormal = planState.DatumReferencePlane.Normal;
                planState.DatumReferenceOrigin = new double[] { sourceOrigin.X, sourceOrigin.Y, sourceOrigin.Z };
                planState.DatumReferenceNormal = new double[] { sourceNormal.X, sourceNormal.Y, sourceNormal.Z };
                Normalize3(planState.DatumReferenceNormal);
                planState.SupportKind = "DATUM_PLANE";
                planState.SupportObjectId = RequiredJournalId(planState.DatumReferenceFeature, "offset reference DatumPlane feature");
                publicView.Add("support_kind", "DATUM_PLANE");
                publicView.Add("support_datum_plane_id", planState.SupportObjectId);
            }
            if (String.Equals(request.Mode, "offset", StringComparison.Ordinal))
            {
                planState.SupportFrameNormal = (double[])planState.DatumReferenceNormal.Clone();
                planState.SupportFrameOrigin = new double[] {
                    planState.DatumReferenceOrigin[0] + request.DistanceMm * planState.DatumReferenceNormal[0],
                    planState.DatumReferenceOrigin[1] + request.DistanceMm * planState.DatumReferenceNormal[1],
                    planState.DatumReferenceOrigin[2] + request.DistanceMm * planState.DatumReferenceNormal[2] };
                publicView.Add("mode", "offset");
                publicView.Add("distance_mm", request.DistanceMm);
                publicView.Add("reference_origin_mm", NumberList(planState.DatumReferenceOrigin));
                publicView.Add("reference_normal", NumberList(planState.DatumReferenceNormal));
                publicView.Add("expected_origin_mm", NumberList(planState.SupportFrameOrigin));
            }
            else
            {
                planState.DatumAxisLine = FindM3LineById(state.WorkPart, request.AxisLineId);
                ValidateM3AngleAxisLine(planState.DatumAxisLine, planState.DatumReferenceOrigin,
                    planState.DatumReferenceNormal, out planState.DatumAxisStartMm, out planState.DatumAxisEndMm);
                planState.DatumExpectedNormal = RotateM3VectorAroundAxis(planState.DatumReferenceNormal,
                    new double[] { planState.DatumAxisEndMm[0] - planState.DatumAxisStartMm[0],
                        planState.DatumAxisEndMm[1] - planState.DatumAxisStartMm[1],
                        planState.DatumAxisEndMm[2] - planState.DatumAxisStartMm[2] },
                    request.AngleDeg * Math.PI / 180.0);
                publicView.Add("mode", "angle");
                publicView.Add("angle_deg", request.AngleDeg);
                publicView.Add("axis_line_id", RequiredJournalId(planState.DatumAxisLine, "angle axis Line"));
                publicView.Add("axis_start_mm", NumberList(planState.DatumAxisStartMm));
                publicView.Add("axis_end_mm", NumberList(planState.DatumAxisEndMm));
                publicView.Add("reference_origin_mm", NumberList(planState.DatumReferenceOrigin));
                publicView.Add("reference_normal", NumberList(planState.DatumReferenceNormal));
                publicView.Add("expected_normal", NumberList(planState.DatumExpectedNormal));
            }
        }
        else if (String.Equals(request.Mode, "two_lines", StringComparison.Ordinal))
        {
            planState.DatumLine1 = FindM3LineById(state.WorkPart, request.Line1Id, "line1_id");
            planState.DatumLine2 = FindM3LineById(state.WorkPart, request.Line2Id, "line2_id");
            if (planState.DatumLine1.Tag.Equals(planState.DatumLine2.Tag))
                throw new M2ModelException("INVALID_REQUEST", "line1_id and line2_id resolve to the same native Line");
            ValidateM3TwoLineDatumPlane(planState.DatumLine1, planState.DatumLine2,
                out planState.DatumLine1StartMm, out planState.DatumLine1EndMm,
                out planState.DatumLine2StartMm, out planState.DatumLine2EndMm,
                out planState.DatumTwoLineExpectedOriginMm, out planState.DatumTwoLineExpectedNormal);
            planState.DatumLine1SourceFeatureIds = M3LineSourceFeatureIds(state.WorkPart, planState.DatumLine1);
            planState.DatumLine2SourceFeatureIds = M3LineSourceFeatureIds(state.WorkPart, planState.DatumLine2);
            publicView.Add("mode", "two_lines");
            publicView.Add("line1_id", RequiredJournalId(planState.DatumLine1, "datum plane line1"));
            publicView.Add("line2_id", RequiredJournalId(planState.DatumLine2, "datum plane line2"));
            publicView.Add("line1_start_mm", NumberList(planState.DatumLine1StartMm));
            publicView.Add("line1_end_mm", NumberList(planState.DatumLine1EndMm));
            publicView.Add("line2_start_mm", NumberList(planState.DatumLine2StartMm));
            publicView.Add("line2_end_mm", NumberList(planState.DatumLine2EndMm));
            publicView.Add("line1_source_feature_ids", ObjectStrings(planState.DatumLine1SourceFeatureIds));
            publicView.Add("line2_source_feature_ids", ObjectStrings(planState.DatumLine2SourceFeatureIds));
            publicView.Add("expected_plane_origin_mm", NumberList(planState.DatumTwoLineExpectedOriginMm));
            publicView.Add("expected_normal", NumberList(planState.DatumTwoLineExpectedNormal));
            publicView.Add("line_endpoint_plane_tolerance_mm", M3DatumLinePlaneToleranceMm);
        }
        else
        {
        planState.SupportFrameOrigin = (double[])request.OriginMm.Clone();
        planState.SupportFrameNormal = (double[])request.Normal.Clone();
        planState.SupportFrameX = (double[])request.XAxis.Clone();
        planState.SupportFrameY = Cross3(planState.SupportFrameNormal, planState.SupportFrameX);
        Normalize3(planState.SupportFrameY);
        publicView.Add("origin_mm", NumberList(planState.SupportFrameOrigin));
        publicView.Add("normal", NumberList(planState.SupportFrameNormal));
        publicView.Add("x_axis", NumberList(planState.SupportFrameX));
        }
        return new M3PlanSnapshot
        {
            Kind = "create_datum_plane",
            RevisionId = binding.RevisionId,
            PublicView = publicView,
            AdapterState = planState
        };
    }

    public M3PlanSnapshot PrepareExtrude(M2ModelBinding binding, M3ExtrudeRequest request)
    {
        if (request == null || String.IsNullOrWhiteSpace(request.SketchId))
            throw new M2ModelException("INVALID_REQUEST", "M3 Extrude requires sketch_id");
        request = NormalizeM3ExtrudeRequest(request);
        BindingState state = RequireState(binding);
        RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null)
            throw new M2ModelException("NX_BUSY", "an active Sketch must be ended before preparing an Extrude");
        ValidateExtrudeRequest(request);
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
            throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude whole-sketch profile is invalid or could not be verified: " + reason);
        }
        int openChains = ProfileCount(profileAnalysis, "open_component_count");
        int closedLoops = ProfileCount(profileAnalysis, "valid_closed_component_count");
        if (request.ProfileOutput == "solid" && (openChains != 0 || closedLoops == 0))
            throw new M2ModelException("UNSUPPORTED_PROFILE", "Solid Extrude requires only valid closed profile loops; open curves must request profile_output=sheet.");
        if (openChains == 0 && closedLoops == 0)
            throw new M2ModelException("UNSUPPORTED_PROFILE", "Extrude requires at least one valid active non-reference curve profile.");
        Body target = null;
        string targetId = null;
        if (request.Mode != "create")
        {
            target = FindBody(state.WorkPart, request.TargetBodyId);
            if (!target.IsSolidBody)
                throw new M2ModelException("UNSUPPORTED_BODY", "boolean Extrude target must identify one solid body");
            targetId = RequiredJournalId(target, "target body");
        }

        M3PlanState planState = CreateM3PlanState(binding, state, "create_extrude", capture,
            F12MassPropertyAccuracy);
        planState.ExtrudeRequest = CloneM3ExtrudeRequest(request);
        planState.Sketch = sketch;
        planState.SketchId = RequiredJournalId(sketch, "sketch");
        planState.SketchRecordBefore = Clone(FindSketchRecord(capture.Snapshot, planState.SketchId));
        planState.TargetBody = target;
        planState.TargetBodyId = targetId;
        planState.ProfileAnalysis = profileAnalysis;
        planState.F12SemanticBefore = capture.Snapshot;
        planState.F12CurveInventoryBefore = M3SketchProjectionCurveInventoryText(
            CaptureM3SketchProjectionCurveInventory(state.WorkPart));
        try { planState.BodyFingerprintsBefore = CaptureF12BodyFingerprints(state.WorkPart, planState.BodiesBefore); }
        catch (Exception ex)
        {
            ThrowIfF12AnalysisStateUnknown(ex,
                "F12 body signature capture could not prove preparation was read-only");
            throw;
        }
        Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal);
        publicView.Add("kind", "create_extrude");
        publicView.Add("revision_id", binding.RevisionId);
        publicView.Add("sketch_id", planState.SketchId);
        publicView.Add("mode", request.Mode);
        publicView.Add("profile_output", request.ProfileOutput);
        publicView.Add("direction", request.Direction);
        publicView.Add("extent", request.Extent);
        publicView.Add("start_offset_mm", request.StartOffsetMm);
        publicView.Add("end_offset_mm", request.Extent == "finite" ? (object)request.EndOffsetMm : null);
        publicView.Add("span_mm", request.Extent == "finite" ? (object)(request.EndOffsetMm - request.StartOffsetMm) : null);
        publicView.Add("profile_analysis", profileAnalysis);
        publicView.Add("target_body_id", targetId);
        publicView.Add("target_body_before", targetId == null ? null : FindBodyMeasure(planState.BodiesBefore, targetId).PublicRecord);
        publicView.Add("non_target_bodies_before", BodyMeasureRecords(planState.BodiesBefore, targetId));
        return new M3PlanSnapshot
        {
            Kind = "create_extrude",
            RevisionId = binding.RevisionId,
            SketchId = planState.SketchId,
            PublicView = publicView,
            AdapterState = planState
        };
    }

    public M3ExecutionResult ExecutePlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M3PlanState blendPlan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (blendPlan != null && blendPlan.F21Sweep != null) return ExecuteF21SweepPlan(binding, prepared, blendPlan);
        if (blendPlan != null && blendPlan.SpatialCurveRequest != null) return ExecuteF20SpatialCurvePlan(binding, prepared, blendPlan);
        if (blendPlan != null && blendPlan.ChamferRequest != null) return ExecuteF17ChamferPlan(binding, prepared, blendPlan);
        if (blendPlan != null && blendPlan.FeatureMirrorRequest != null) return ExecuteF19FeatureMirrorPlan(binding, prepared, blendPlan);
        if (blendPlan != null && blendPlan.FeaturePatternRequest != null) return ExecuteF18FeaturePatternPlan(binding, prepared, blendPlan);
        if (blendPlan != null && blendPlan.EdgeBlendRequest != null) return ExecuteF16EdgeBlendPlan(binding, prepared, blendPlan);
        M3PlanState booleanPlan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (booleanPlan != null && booleanPlan.BooleanRequest != null) return ExecuteF15BooleanPlan(binding, prepared, booleanPlan);
        M3PlanState holePlan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (holePlan != null && holePlan.HoleRequest != null) return ExecuteF14HolePlan(binding, prepared, holePlan);
        M3PlanState revolvePlan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (revolvePlan != null && revolvePlan.RevolveRequest != null)
            return ExecuteM3RevolvePlan(binding, prepared, revolvePlan);
        if (prepared != null && prepared.AdapterState is M7PlanState) return ExecuteM7Plan(binding, prepared);
        if (prepared != null && prepared.AdapterState is M3SketchCornerPlanState)
            return ExecuteSketchCornerPlan(binding, prepared);
        if (prepared != null && prepared.AdapterState is M3SketchOffsetPlanState)
            return ExecuteSketchOffsetPlan(binding, prepared);
        if (prepared != null && prepared.AdapterState is M3SketchPatternPlanState)
            return ExecuteSketchPatternPlan(binding, prepared);
        if (prepared != null && prepared.AdapterState is M3SketchProjectionPlanState)
            return ExecuteSketchProjectionPlan(binding, prepared);
        if (prepared != null && prepared.AdapterState is M3SketchReferenceConversionPlanState)
            return ExecuteSketchReferenceConversionPlan(binding, prepared);
        M3PlanState plan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (plan == null || (prepared.Kind != "create_sketch" && prepared.Kind != "create_extrude" &&
            prepared.Kind != "create_datum_plane"))
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null,
                "INVALID_PLAN", "M3 plan does not contain retained NX adapter state");
        BindingState state;
        try { state = RequireState(binding); }
        catch (M2ModelException ex) { return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, ex.Code, ex.Message); }
        if (!Object.ReferenceEquals(state, plan.BindingState) || !String.Equals(plan.RevisionId, prepared.RevisionId, StringComparison.Ordinal))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "M3 plan belongs to a different binding or revision");
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_BINDING", bindingReason);
        try { RequireWritableSimplePart(state.WorkPart); }
        catch (M2ModelException ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, ex.Code, ex.Message);
        }

        try
        {
            SnapshotResult current = Capture(binding, state);
            if (!String.Equals(CanonicalSemanticSnapshot(current.Snapshot), plan.SemanticBefore, StringComparison.Ordinal) ||
                !String.Equals(CaptureFullGeometry(state.WorkPart), plan.GeometryBefore, StringComparison.Ordinal))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "part geometry or semantic state changed after M3 plan preparation");
            Dictionary<string, object> bodyPreconditionEvidence;
            double bodyMeasurementAccuracy = plan.ExtrudeRequest == null
                ? M3LegacyMassPropertyAccuracy : F12MassPropertyAccuracy;
            List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart, bodyMeasurementAccuracy);
            if (!BodyMeasuresEqual(plan.BodiesBefore, bodiesNow, out bodyPreconditionEvidence))
                return M3Execution("UNCHANGED", binding.RevisionId, bodyPreconditionEvidence, "STALE_GEOMETRY", "body volume or bounding box changed after M3 plan preparation");
            if (plan.ExtrudeRequest != null)
            {
                Dictionary<string, object> exactBodyEvidence;
                Dictionary<string, string> exactBodiesNow = CaptureF12BodyFingerprints(state.WorkPart, bodiesNow);
                if (!F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore, exactBodiesNow, out exactBodyEvidence))
                    return M3Execution("UNCHANGED", binding.RevisionId, exactBodyEvidence, "STALE_GEOMETRY", "body topology or surface/edge geometry changed after Extrude preparation");
            }
        }
        catch (Exception ex)
        {
            M2ModelException unknown = F12AnalysisStateUnknownOrNull(ex,
                "F12 body signature read could not prove the model state before M3 execution");
            if (unknown != null)
                return M3Execution("UNKNOWN", binding.RevisionId, null, unknown.Code, unknown.Message);
            return M3Execution("UNCHANGED", binding.RevisionId, null, "MODEL_READ_FAILED", "could not refresh model state before M3 execution (" + ex.GetType().Name + ")");
        }

        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "M3 native sketch/extrude operation"); }
        catch (Exception ex)
        {
            return M3Execution("UNCHANGED", binding.RevisionId, null, "UPDATE_FAILED", "could not create an NX update mark (" + ex.GetType().Name + ")");
        }

        try
        {
            RequireWritableSimplePart(state.WorkPart);
            RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
            if (plan.Kind == "create_sketch")
                ExecuteM3Sketch(binding, state, plan, mark);
            else if (plan.Kind == "create_datum_plane")
                ExecuteM3DatumPlane(binding, state, plan, mark);
            else
                ExecuteM3Extrude(binding, state, plan, mark);
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

    public M3VerificationResult VerifyPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
    {
        M3PlanState blendPlan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (blendPlan != null && blendPlan.F21Sweep != null) return VerifyF21SweepPlan(binding, prepared, blendPlan);
        if (blendPlan != null && blendPlan.SpatialCurveRequest != null) return VerifyF20SpatialCurvePlan(binding, prepared, blendPlan);
        if (blendPlan != null && blendPlan.ChamferRequest != null) return VerifyF17ChamferPlan(binding, prepared, blendPlan);
        if (blendPlan != null && blendPlan.FeatureMirrorRequest != null) return VerifyF19FeatureMirrorPlan(binding, prepared, blendPlan);
        if (blendPlan != null && blendPlan.FeaturePatternRequest != null) return VerifyF18FeaturePatternPlan(binding, prepared, blendPlan);
        if (blendPlan != null && blendPlan.EdgeBlendRequest != null) return VerifyF16EdgeBlendPlan(binding, prepared, blendPlan);
        M3PlanState booleanPlan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (booleanPlan != null && booleanPlan.BooleanRequest != null) return VerifyF15BooleanPlan(binding, prepared, booleanPlan);
        M3PlanState holePlan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (holePlan != null && holePlan.HoleRequest != null) return VerifyF14HolePlan(binding, prepared, holePlan);
        M3PlanState revolvePlan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (revolvePlan != null && revolvePlan.RevolveRequest != null)
            return VerifyM3RevolvePlan(binding, prepared, revolvePlan);
        if (prepared != null && prepared.AdapterState is M7PlanState) return VerifyM7Plan(binding, prepared);
        if (prepared != null && prepared.AdapterState is M3SketchCornerPlanState)
            return VerifySketchCornerPlan(binding, prepared);
        if (prepared != null && prepared.AdapterState is M3SketchOffsetPlanState)
            return VerifySketchOffsetPlan(binding, prepared);
        if (prepared != null && prepared.AdapterState is M3SketchPatternPlanState)
            return VerifySketchPatternPlan(binding, prepared);
        if (prepared != null && prepared.AdapterState is M3SketchProjectionPlanState)
            return VerifySketchProjectionPlan(binding, prepared);
        if (prepared != null && prepared.AdapterState is M3SketchReferenceConversionPlanState)
            return VerifySketchReferenceConversionPlan(binding, prepared);
        M3PlanState plan = prepared == null ? null : prepared.AdapterState as M3PlanState;
        if (plan == null || plan.Execution == null || plan.Execution.State != "APPLIED")
            return M3VerificationFailure(binding == null ? null : binding.RevisionId, "M3 verification requires a successfully applied plan");
        BindingState state = RequireState(binding);
        string bindingReason;
        if (!Object.ReferenceEquals(state, plan.BindingState) || !IsBindingCurrent(binding, out bindingReason))
            return M3VerificationFailure(binding == null ? null : binding.RevisionId, "NX binding changed before M3 verification");
        if (plan.Kind == "create_datum_plane")
            return VerifyM3DatumPlanePlan(binding, plan, state);

        SnapshotResult captured = Capture(binding, state);
        string geometryNow = CaptureFullGeometry(state.WorkPart);
        List<object> historyNow = HistoryProjection(captured.Snapshot);
        List<M3BodyMeasure> bodiesNow = CaptureM3BodyMeasures(state.WorkPart,
            plan.ExtrudeRequest == null ? M3LegacyMassPropertyAccuracy : F12MassPropertyAccuracy);
        bool geometryMatches;
        bool constraintsMatch;
        bool profileMatches = true;
        Dictionary<string, object> profileAnalysisNow = null;
        Dictionary<string, object> profileStateEvidence = null;
        Dictionary<string, object> geometryEvidence;
        Dictionary<string, object> constraintEvidence;
        Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bool historyMatches = String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(captured.Snapshot), StringComparison.Ordinal) &&
            String.Equals(plan.GeometryAfter, geometryNow, StringComparison.Ordinal) &&
            SemanticValuesEqual(plan.HistoryAfter, historyNow, "history");
        historyEvidence.Add("expected_feature_id", plan.FeatureId);
        historyEvidence.Add("expected_sketch_id", plan.SketchId);
        historyEvidence.Add("before", plan.HistoryBefore);
        historyEvidence.Add("after_execute", plan.HistoryAfter);
        historyEvidence.Add("after_verify", historyNow);
        historyEvidence.Add("semantic_matches_execute", String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(captured.Snapshot), StringComparison.Ordinal));
        historyEvidence.Add("geometry_snapshot_matches_execute", String.Equals(plan.GeometryAfter, geometryNow, StringComparison.Ordinal));

        Dictionary<string, object> bodyEvidence;
        bool bodiesMatch = BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesAfter, null), BodyMeasureRecords(bodiesNow, null), out bodyEvidence);
        Dictionary<string, object> nonTargetEvidence;
        bool nonTargetsMatch;
        Dictionary<string, string> bodyFingerprintsNow = null;
        Dictionary<string, object> bodyFingerprintEvidence = null;
        bool bodyFingerprintsMatch = true;
        bool affectedBodiesMatch = true;
        List<M3BodyMeasure> affectedBodiesNow = null;
        Dictionary<string, object> affectedBodyEvidence = null;
        Dictionary<string, object> preservationEvidence = null;
        bool preservationMatches = true;
        bool sheetAreaMatches = true;
        Dictionary<string, object> sheetAreaEvidence = null;
        if (plan.Kind == "create_extrude")
        {
            bodyFingerprintsNow = CaptureF12BodyFingerprints(state.WorkPart, bodiesNow);
            bodyFingerprintsMatch = F12BodyFingerprintsEqual(plan.BodyFingerprintsAfter, bodyFingerprintsNow,
                out bodyFingerprintEvidence);
            nonTargetsMatch = F12NonTargetBodiesMatch(plan, bodiesNow, bodyFingerprintsNow, out nonTargetEvidence);
            preservationMatches = F12PreExistingModelStatePreserved(plan.F12SemanticBefore, captured.Snapshot,
                plan, out preservationEvidence);
            affectedBodiesNow = ResolveAffectedBodies(plan, bodiesNow, FindFeature(state.WorkPart, plan.FeatureId));
            affectedBodiesMatch = F12AffectedBodiesMatch(plan.AffectedBodies, affectedBodiesNow,
                out affectedBodyEvidence);
            if (String.Equals(plan.ExtrudeRequest.ProfileOutput, "sheet", StringComparison.Ordinal))
            {
                double? expectedArea, actualArea;
                sheetAreaMatches = F12SheetSurfaceAreaMatches(state.WorkPart, affectedBodiesNow,
                    plan.ProfileAnalysis, plan.ExtrudeRequest.EndOffsetMm - plan.ExtrudeRequest.StartOffsetMm,
                    out expectedArea, out actualArea, out sheetAreaEvidence) &&
                    expectedArea.HasValue && actualArea.HasValue && plan.ExpectedSheetSurfaceAreaMm2.HasValue &&
                    plan.SheetSurfaceAreaMm2.HasValue &&
                    NearlyEqual(expectedArea.Value, plan.ExpectedSheetSurfaceAreaMm2.Value) &&
                    NearlyEqual(actualArea.Value, plan.SheetSurfaceAreaMm2.Value);
            }
            string curveInventoryNow = M3SketchProjectionCurveInventoryText(
                CaptureM3SketchProjectionCurveInventory(state.WorkPart));
            bool curveInventoryMatches = String.Equals(plan.F12CurveInventoryAfter, curveInventoryNow, StringComparison.Ordinal) &&
                String.Equals(plan.F12CurveInventoryBefore, curveInventoryNow, StringComparison.Ordinal);
            preservationEvidence["complete_part_curve_inventory_matches_execute"] = curveInventoryMatches;
            preservationMatches = preservationMatches && curveInventoryMatches;
            bodyEvidence["geometry_fingerprint_after_execute_matches_verify"] = bodyFingerprintEvidence;
            bodyEvidence["geometry_fingerprint_matches"] = bodyFingerprintsMatch;
            bodyEvidence["affected_body_set_matches_execute"] = affectedBodyEvidence;
            bodyEvidence["affected_bodies_match"] = affectedBodiesMatch;
            if (sheetAreaEvidence != null) bodyEvidence["sheet_surface_area_matches_execute"] = sheetAreaEvidence;
            nonTargetEvidence["pre_existing_model_state"] = preservationEvidence;
        }
        else
            nonTargetsMatch = BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesBefore, null),
                BodyMeasureRecords(bodiesNow, null), out nonTargetEvidence);

        if (plan.Kind == "create_sketch")
        {
            Sketch sketch = FindSketch(state.WorkPart, plan.SketchId);
            geometryMatches = SketchGeometryMatches(plan, sketch, out geometryEvidence);
            constraintsMatch = SketchConstraintsMatch(plan, sketch, out constraintEvidence);
            profileMatches = VerifyM3ProfileAnalysis(state.WorkPart, sketch, plan.ProfileAnalysis,
                out profileAnalysisNow, out profileStateEvidence);
        }
        else
        {
            Sketch sketch = FindSketch(state.WorkPart, plan.SketchId);
            bool sketchGeometryMatches = SketchGeometryMatches(plan, sketch, out geometryEvidence);
            bool sketchConstraintsMatch = SketchConstraintsMatch(plan, sketch, out constraintEvidence);
            bool extrudeHistoryMatches = FeaturePresent(state.WorkPart, plan.FeatureId);
            Dictionary<string, object> effectEvidence;
            geometryEvidence.Add("sketch_geometry_matches", sketchGeometryMatches);
            geometryEvidence.Add("extrude_feature_id", plan.FeatureId);
            geometryEvidence.Add("extrude_feature_present", extrudeHistoryMatches);
            geometryEvidence.Add("extrude_section", plan.ExtrudeSectionEvidence);
            constraintsMatch = sketchConstraintsMatch;
            historyMatches = historyMatches && extrudeHistoryMatches;
            historyMatches = historyMatches && preservationMatches && bodyFingerprintsMatch;
            profileMatches = VerifyF12ProfileAnalysis(state.WorkPart, plan.Sketch, plan.ProfileAnalysis,
                out profileAnalysisNow, out profileStateEvidence);
            M3BodyMeasure affected = FindBodyMeasureByTag(bodiesNow, plan.AffectedBodyId, plan.TargetBody);
            bool bodyEffectMatches = false;
            if (affectedBodiesNow != null && affectedBodiesNow.Count > 0)
                bodyEffectMatches = M3BodyEffectMatches(plan, affectedBodiesNow, bodiesNow, out effectEvidence);
            else effectEvidence = new Dictionary<string, object>(StringComparer.Ordinal) { { "status", "MISMATCH" }, { "reason", "affected body is absent" } };
            Dictionary<string, object> extrudeDefinitionEvidence;
            bool extrudeDefinitionMatches = M3ExtrudeDefinitionMatches(plan, FindFeature(state.WorkPart, plan.FeatureId), out extrudeDefinitionEvidence);
            object sourceSketchParentValue;
            bool sourceSketchParent = extrudeDefinitionEvidence.TryGetValue("source_sketch_parent", out sourceSketchParentValue) && sourceSketchParentValue is bool && (bool)sourceSketchParentValue;
            geometryMatches = sketchGeometryMatches && bodyEffectMatches && extrudeDefinitionMatches && profileMatches &&
                affectedBodiesMatch &&
                sheetAreaMatches &&
                plan.ExtrudeSectionEvidence != null && F12SectionAnalysisVerified(plan.ExtrudeSectionEvidence);
            historyMatches = historyMatches && sourceSketchParent;
            geometryEvidence.Add("body_effect", effectEvidence);
            geometryEvidence.Add("extrude_definition", extrudeDefinitionEvidence);
            geometryEvidence.Add("profile_analysis", profileAnalysisNow);
            geometryEvidence.Add("profile_analysis_state", profileStateEvidence);
            if (sheetAreaEvidence != null) geometryEvidence.Add("sheet_surface_area", sheetAreaEvidence);
            bodyEvidence["affected_body"] = affected == null ? null : affected.PublicRecord;
            bodyEvidence["affected_bodies"] = BodyMeasureRecords(plan.AffectedBodies, null);
        }

        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        checks.Add("geometry", VerificationCheck(geometryMatches ? "VERIFIED" : "MISMATCH", geometryEvidence));
        checks.Add("constraints", VerificationCheck(constraintsMatch ? "VERIFIED" : "MISMATCH", constraintEvidence));
        if (plan.Kind == "create_sketch")
        {
            bool analysisMatches = M3ProfileAnalysisSuccessful(profileAnalysisNow) &&
                ProfileAnalysisCoreMatches(plan.ProfileAnalysis, profileAnalysisNow);
            checks.Add("profile_analysis", VerificationCheck(analysisMatches ? "VERIFIED" : "MISMATCH", profileAnalysisNow));
            checks.Add("profile_analysis_state_restored", VerificationCheck(
                profileStateEvidence != null && profileStateEvidence.ContainsKey("temporary_section_state_restored") &&
                profileStateEvidence["temporary_section_state_restored"] is bool &&
                (bool)profileStateEvidence["temporary_section_state_restored"] ? "VERIFIED" : "MISMATCH",
                profileStateEvidence));
        }
        checks.Add("history", VerificationCheck(historyMatches ? "VERIFIED" : "MISMATCH", historyEvidence));
        checks.Add("bodies", VerificationCheck(bodiesMatch ? "VERIFIED" : "MISMATCH", bodyEvidence));
        checks.Add("non_target_bodies", VerificationCheck(nonTargetsMatch ? "VERIFIED" : "MISMATCH", nonTargetEvidence));
        if (plan.Kind == "create_extrude")
        {
            checks.Add("model_preservation", VerificationCheck(preservationMatches && bodyFingerprintsMatch ? "VERIFIED" : "MISMATCH",
                new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "pre_existing_model_state", preservationEvidence },
                    { "body_fingerprints_match_execute", bodyFingerprintEvidence },
                    { "affected_body_set_matches_execute", affectedBodyEvidence }
                }));
            if (String.Equals(plan.ExtrudeRequest.ProfileOutput, "sheet", StringComparison.Ordinal))
                checks.Add("sheet_surface_area", VerificationCheck(sheetAreaMatches ? "VERIFIED" : "MISMATCH", sheetAreaEvidence));
        }
        bool supportMatches = true;
        List<object> supportRecords = new List<object>();
        if (state.M4SupportTracks.ContainsKey(plan.SketchId))
        {
            RefreshM4SupportTracksFromPart(state.WorkPart, state);
            supportMatches = ValidateM4SupportTracks(state, out supportRecords, null);
            Dictionary<string, object> supportEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            supportEvidence.Add("associations", supportRecords);
            supportEvidence.Add("native_unique_face_and_domain_verified", supportMatches);
            checks.Add("support_association", VerificationCheck(supportMatches ? "VERIFIED" : "MISMATCH", supportEvidence));
        }
        else if (plan.Kind == "create_sketch" && (plan.SupportKind == "FACE" || plan.SupportKind == "DATUM_PLANE"))
        {
            Sketch sketch = FindSketch(state.WorkPart, plan.SketchId);
            Dictionary<string, object> supportEvidence;
            Dictionary<string, object> supportFrame;
            bool readSupport = TryReadM3NativeSupport(state.WorkPart, sketch, null, out supportEvidence, out supportFrame);
            if (supportEvidence == null) supportEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            string expectedKind = plan.SupportKind;
            string actualKind = readSupport && supportEvidence.ContainsKey("support_kind")
                ? Convert.ToString(supportEvidence["support_kind"], CultureInfo.InvariantCulture) : null;
            string idKey = expectedKind == "FACE" ? "support_face_id" : "datum_plane_id";
            string expectedId = plan.SupportObjectId;
            string actualId = readSupport && supportEvidence.ContainsKey(idKey)
                ? Convert.ToString(supportEvidence[idKey], CultureInfo.InvariantCulture) : null;
            string expectedFaceTag = plan.SketchRequest == null ? null : plan.SketchRequest.SupportFaceTag;
            string actualFaceTag = readSupport && supportEvidence.ContainsKey("support_face_tag")
                ? Convert.ToString(supportEvidence["support_face_tag"], CultureInfo.InvariantCulture) : null;
            bool faceTagMatches = expectedKind != "FACE" || expectedFaceTag == null ||
                String.Equals(expectedFaceTag, actualFaceTag, StringComparison.Ordinal);
            bool native = readSupport && supportEvidence.ContainsKey("native") && supportEvidence["native"] is bool &&
                (bool)supportEvidence["native"];
            bool supportIdentityMatches = native && String.Equals(expectedKind, actualKind, StringComparison.Ordinal) &&
                String.Equals(expectedId, actualId, StringComparison.Ordinal) && faceTagMatches;
            if (expectedKind == "DATUM_PLANE")
            {
                object parentValue;
                supportIdentityMatches = supportIdentityMatches && supportEvidence.TryGetValue("native_feature_parent", out parentValue) &&
                    parentValue is bool && (bool)parentValue;
            }
            supportEvidence["expected_support_kind"] = expectedKind;
            supportEvidence["expected_support_id"] = expectedId;
            supportEvidence["expected_support_face_tag"] = expectedFaceTag;
            supportEvidence["support_face_tag_matches"] = faceTagMatches;
            supportEvidence["support_identity_matches"] = supportIdentityMatches;
            supportEvidence["frame"] = supportFrame;
            supportRecords.Add(supportEvidence);
            supportMatches = supportIdentityMatches;
            checks.Add("support_association", VerificationCheck(supportMatches ? "VERIFIED" : "MISMATCH", supportEvidence));
        }

        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("kind", plan.Kind);
        result.Add("sketch_id", plan.SketchId);
        result.Add("feature_id", plan.FeatureId);
        result.Add("body_id", plan.AffectedBodyId);
        if (plan.Kind == "create_extrude")
        {
            result.Add("body_ids", StringListAsObjects(plan.AffectedBodyIds ?? new List<string>()));
            result.Add("bodies", BodyMeasureRecords(affectedBodiesNow, null));
            result.Add("profile_output", plan.ExtrudeRequest.ProfileOutput);
            result.Add("start_offset_mm", plan.ExtrudeRequest.StartOffsetMm);
            result.Add("end_offset_mm", plan.ExtrudeRequest.Extent == "finite" ? (object)plan.ExtrudeRequest.EndOffsetMm : null);
            result.Add("span_mm", plan.ExtrudeRequest.Extent == "finite"
                ? (object)(plan.ExtrudeRequest.EndOffsetMm - plan.ExtrudeRequest.StartOffsetMm) : null);
            result.Add("volume_mm3", AggregateBodyVolume(affectedBodiesNow));
            result.Add("bbox_mm", AggregateBodyBounds(affectedBodiesNow));
            result.Add("surface_area_mm2", plan.SheetSurfaceAreaMm2);
        }
        result.Add("body_inventory", BodyMeasureRecords(bodiesNow, null));
        result.Add("revision_id", binding.RevisionId);
        if (plan.Kind == "create_sketch")
        {
            result.Add("profile_analysis", profileAnalysisNow);
            result.Add("profile_analysis_state", profileStateEvidence);
        }
        if (supportRecords.Count > 0) result.Add("support_associations", supportRecords);
        bool verified = geometryMatches && constraintsMatch && profileMatches && historyMatches && bodiesMatch && nonTargetsMatch && supportMatches;
        return new M3VerificationResult
        {
            Verified = verified,
            RevisionId = binding.RevisionId,
            Checks = checks,
            Result = result,
            ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified ? null : "Live M3 geometry, dimensions, feature history, body measurements, or non-target bodies differ from the executed result."
        };
    }

    private void ExecuteM3Sketch(M2ModelBinding binding, BindingState state, M3PlanState plan, Session.UndoMarkId mark)
    {
        M3SketchRequest request = plan.SketchRequest;
        bool selectorFaceMode = !String.IsNullOrWhiteSpace(request.SupportFaceSelector);
        bool explicitFaceMode = !String.IsNullOrWhiteSpace(request.SupportFaceId);
        bool datumPlaneMode = !String.IsNullOrWhiteSpace(request.SupportDatumPlaneId);
        bool customFrameMode = request.Normal != null && !explicitFaceMode && !datumPlaneMode;
        Sketch sketch;
        if (selectorFaceMode)
        {
            M4FaceCandidate currentFace = ResolveM4UniqueFace(state.WorkPart,
                request.SupportFaceSelector, request.SupportBodyId);
            if (!String.Equals(currentFace.FaceId, plan.SupportFace.FaceId, StringComparison.Ordinal) ||
                !String.Equals(currentFace.BodyId, plan.SupportFace.BodyId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "selected support face changed after the immutable M4 plan was prepared");
            Dictionary<string, object> domainEvidence;
            if (!ValidateM4SketchProfile(currentFace, request.Geometries,
                request.FaceOriginOffsetMm, out domainEvidence))
                throw new M2ModelException("M4_PROFILE_OUTSIDE_FACE", "the requested sketch profile is outside the selected face domain");
            plan.SupportFace = currentFace;
            plan.SupportAssociationEvidence = domainEvidence;
            plan.SupportFrameOrigin = M4OffsetFrameOrigin(currentFace, request.FaceOriginOffsetMm);
            Plane supportPlane;
            sketch = M3NxFeatureFactory.CreateSketchOnFace(state.WorkPart, currentFace.Face,
                ToPoint3d(plan.SupportFrameOrigin), ToVector3d(currentFace.XAxis),
                ToVector3d(currentFace.Normal), out supportPlane);
            plan.SupportPlane = supportPlane;
        }
        else if (explicitFaceMode)
        {
            M4FaceCandidate currentFace = ResolveM3FaceById(state.WorkPart, request.SupportFaceId,
                request.SupportBodyId, request.SupportFaceTag);
            if (!String.Equals(currentFace.FaceId, plan.SupportFace.FaceId, StringComparison.Ordinal) ||
                !String.Equals(currentFace.BodyId, plan.SupportFace.BodyId, StringComparison.Ordinal) ||
                (request.SupportFaceTag != null && !currentFace.Face.Tag.Equals(plan.SupportFace.Face.Tag)))
                throw new M2ModelException("STALE_REVISION", "selected support face changed after the sketch plan was prepared");
            ValidateM3FrameOnFace(currentFace, request.OriginMm, request.Normal);
            Plane supportPlane;
            sketch = M3NxFeatureFactory.CreateSketchOnFace(state.WorkPart, currentFace.Face,
                ToPoint3d(request.OriginMm), ToVector3d(request.XAxis), ToVector3d(request.Normal), out supportPlane);
            plan.SupportFace = currentFace;
            plan.SupportPlane = supportPlane;
        }
        else if (datumPlaneMode)
        {
            Feature datumFeature;
            DatumPlane currentDatum = FindM3DatumPlane(state.WorkPart, request.SupportDatumPlaneId, out datumFeature);
            if (plan.SupportDatumPlane == null || !currentDatum.Tag.Equals(plan.SupportDatumPlane.Tag) ||
                !datumFeature.Tag.Equals(plan.SupportDatumPlane.Feature.Tag))
                throw new M2ModelException("STALE_REVISION", "selected native DatumPlane changed after the sketch plan was prepared");
            ValidateM3FrameOnDatumPlane(currentDatum, request.OriginMm, request.Normal);
            Plane supportPlane = state.WorkPart.Planes.CreatePlane(PlaneTypes.MethodType.Coincident,
                PlaneTypes.AlternateType.One, ToPoint3d(request.OriginMm), ToVector3d(request.Normal),
                null, false, false, new NXObject[] { currentDatum });
            sketch = M3NxFeatureFactory.CreateSketchOnPlane(state.WorkPart, supportPlane,
                ToPoint3d(request.OriginMm), ToVector3d(request.Normal), ToVector3d(request.XAxis));
            plan.SupportDatumPlane = currentDatum;
            plan.SupportPlane = supportPlane;
        }
        else if (customFrameMode)
        {
            sketch = M3NxFeatureFactory.CreateSketch(state.WorkPart, ToPoint3d(request.OriginMm),
                ToVector3d(request.Normal), ToVector3d(request.XAxis));
        }
        else
        {
            M3SketchPlane plane = ParseSketchPlane(request.Plane);
            Point3d origin = new Point3d(request.OriginMm[0], request.OriginMm[1], request.OriginMm[2]);
            sketch = M3NxFeatureFactory.CreateSketch(state.WorkPart, plane, origin);
        }
        plan.Sketch = sketch;
        plan.SketchId = RequiredJournalId(sketch, "M3 sketch");
        plan.Feature = sketch.Feature;
        plan.FeatureId = RequiredJournalId(plan.Feature, "M3 sketch feature");
        bool geometryOnlyConstraints = request.ConstraintMode == "geometry_only";
        string prefixRoot = "m3_" + Guid.NewGuid().ToString("N");
        foreach (M3SketchGeometryRequest geometry in request.Geometries)
        {
            if (geometry.Type != "line" && geometry.Type != "rectangle" && geometry.Type != "circle") continue;
            string prefix = prefixRoot + "_" + ExpressionSuffix(geometry.Id);
            M3PrimitiveState primitive = new M3PrimitiveState();
            primitive.RequestId = geometry.Id;
            primitive.Type = geometry.Type;
            primitive.Dimensions = new List<M3DimensionState>();
            if (geometry.Type == "line")
            {
                M3LineResult created = geometryOnlyConstraints
                    ? M3NxFeatureFactory.CreateLineGeometry(state.WorkPart, sketch,
                        geometry.StartMm[0], geometry.StartMm[1], geometry.EndMm[0], geometry.EndMm[1])
                    : M3NxFeatureFactory.CreateLine(state.WorkPart, sketch,
                        geometry.StartMm[0], geometry.StartMm[1], geometry.EndMm[0], geometry.EndMm[1], prefix);
                primitive.Curves = new Curve[] { created.Geometry };
                primitive.LocalPoints = new double[] { geometry.StartMm[0], geometry.StartMm[1], geometry.EndMm[0], geometry.EndMm[1] };
                primitive.LocalEndpointPairs = new double[][] { new double[] { geometry.StartMm[0], geometry.StartMm[1], geometry.EndMm[0], geometry.EndMm[1] } };
                primitive.ClosedCurves = new bool[] { false };
                AddM3Dimensions(primitive, created.DrivingDimensions, prefix);
            }
            else if (geometry.Type == "rectangle")
            {
                M3RectangleResult created = geometryOnlyConstraints
                    ? M3NxFeatureFactory.CreateRectangleGeometry(state.WorkPart, sketch,
                        geometry.CornerMm[0], geometry.CornerMm[1], geometry.WidthMm, geometry.HeightMm)
                    : M3NxFeatureFactory.CreateRectangle(state.WorkPart, sketch,
                        geometry.CornerMm[0], geometry.CornerMm[1], geometry.WidthMm, geometry.HeightMm, prefix);
                primitive.Curves = new Curve[created.Geometry.Length];
                for (int i = 0; i < created.Geometry.Length; i++) primitive.Curves[i] = created.Geometry[i];
                primitive.LocalPoints = new double[] { geometry.CornerMm[0], geometry.CornerMm[1] };
                primitive.Width = geometry.WidthMm;
                primitive.Height = geometry.HeightMm;
                double x = geometry.CornerMm[0], y = geometry.CornerMm[1];
                double right = x + geometry.WidthMm, top = y + geometry.HeightMm;
                primitive.LocalEndpointPairs = new double[][] {
                    new double[] { x, y, right, y }, new double[] { right, y, right, top },
                    new double[] { right, top, x, top }, new double[] { x, top, x, y }
                };
                primitive.ClosedCurves = new bool[] { false, false, false, false };
                AddM3Dimensions(primitive, created.DrivingDimensions, prefix);
            }
            else if (geometry.Type == "circle")
            {
                M3CircleResult created = geometryOnlyConstraints
                    ? M3NxFeatureFactory.CreateCircleGeometry(state.WorkPart, sketch,
                        geometry.CenterMm[0], geometry.CenterMm[1], geometry.DiameterMm)
                    : M3NxFeatureFactory.CreateCircle(state.WorkPart, sketch,
                        geometry.CenterMm[0], geometry.CenterMm[1], geometry.DiameterMm, prefix);
                primitive.Curves = new Curve[] { created.Geometry };
                primitive.LocalPoints = new double[] { geometry.CenterMm[0], geometry.CenterMm[1] };
                primitive.Diameter = geometry.DiameterMm;
                primitive.ClosedCurves = new bool[] { true };
                AddM3Dimensions(primitive, created.DrivingDimensions, prefix);
            }
            else throw new M2ModelException("UNSUPPORTED_GEOMETRY", "unsupported M3 sketch geometry type");
            plan.Primitives.Add(primitive);
        }
        foreach (M3SketchGeometryRequest geometry in request.Geometries)
        {
            if (geometry.Type != "arc" && geometry.Type != "ellipse" && geometry.Type != "elliptical_arc" && geometry.Type != "spline") continue;
            Curve created = V2SketchFactory.Create(state.WorkPart, sketch, geometry);
            M3PrimitiveState primitive = new M3PrimitiveState();
            primitive.RequestId = geometry.Id;
            primitive.Type = geometry.Type;
            primitive.Curves = new Curve[] { created };
            primitive.Dimensions = new List<M3DimensionState>();
            primitive.CenterMm = geometry.CenterMm == null ? null : (double[])geometry.CenterMm.Clone();
            primitive.Radius = geometry.RadiusMm;
            primitive.MajorRadius = geometry.MajorRadiusMm;
            primitive.MinorRadius = geometry.MinorRadiusMm;
            primitive.RotationDeg = geometry.RotationDeg;
            primitive.StartAngleDeg = geometry.StartAngleDeg;
            primitive.SweepDeg = geometry.SweepDeg;
            primitive.InterpolationPointsMm = geometry.InterpolationPointsMm;
            bool isClosed = geometry.Type == "ellipse";
            primitive.ClosedCurves = new bool[] { isClosed };
            if (!isClosed) primitive.LocalEndpointPairs = new double[][] { M3LocalEndpoints(geometry) };
            if (geometry.Type == "spline") primitive.SplineDefinition = SplineDefinitionRecord(created as Spline);
            plan.Primitives.Add(primitive);
        }

        int updateErrors = state.Session.UpdateManager.DoUpdate(mark);
        if (updateErrors != 0) throw new M2ModelException("M3_UPDATE_FAILED", "NX update reported errors after native sketch creation");
        if (!FeaturePresent(state.WorkPart, plan.FeatureId))
            throw new M2ModelException("M3_SKETCH_NOT_CREATED", "native Sketch feature was not present after NX update");
        Dictionary<string, object> geometryEvidence;
        if (!SketchGeometryMatches(plan, sketch, out geometryEvidence))
            throw new M2ModelException("M3_SKETCH_READBACK_FAILED", "native Sketch geometry did not match the requested dimensions and position");
        List<object> constraints = ReadConstraintRecords(sketch);
        int dof;
        sketch.CalculateStatus();
        Sketch.Status createdStatus = sketch.GetStatus(out dof);
        bool emptySketch = request.Geometries.Count == 0;
        if (emptySketch)
        {
            NXObject[] emptyGeometry = sketch.GetAllGeometry();
            if (emptyGeometry == null || emptyGeometry.Length != 0 || constraints.Count != 0)
                throw new M2ModelException("M3_SKETCH_READBACK_FAILED", "empty native Sketch unexpectedly contains geometry or constraints");
        }
        else if (!M3SketchConstraintStatusAcceptable(plan, createdStatus, dof, constraints.Count))
            throw new M2ModelException("M3_SKETCH_UNDERCONSTRAINED", "native Sketch is not fully constrained");
        if (selectorFaceMode)
        {
            Dictionary<string, object> nativeEvidence;
            if (!ReadM4NativeSupport(state.WorkPart, sketch, plan.SupportPlane,
                plan.SupportFace.BodyId, out nativeEvidence))
                throw new M2ModelException("M4_FACE_ASSOCIATION_INVALID", "NX did not retain a native Coincident Plane reference to the uniquely selected Face");
            plan.SupportAssociationEvidence["native_readback"] = nativeEvidence;
            plan.SupportAssociationEvidence["native"] = true;
            plan.SupportAssociationEvidence["geometry_kind"] = "Face";
            plan.SupportAssociationEvidence["selector_unique"] = true;
            plan.SupportAssociationEvidence["in_face_domain"] = true;
            plan.SupportAssociationEvidence["current_face_matches"] = true;
            PersistM4SupportMetadata(sketch, request.SupportFaceSelector,
                plan.SupportFace.BodyId, true);
            M4SupportTrack supportTrack = new M4SupportTrack();
            supportTrack.SketchId = plan.SketchId;
            supportTrack.BodyId = plan.SupportFace.BodyId;
            supportTrack.Selector = request.SupportFaceSelector;
            supportTrack.FaceId = plan.SupportFace.Face.Tag.ToString();
            supportTrack.FeatureId = plan.FeatureId;
            supportTrack.Plane = plan.SupportPlane;
            supportTrack.DomainValidated = true;
            supportTrack.Native = true;
            supportTrack.GeometryKind = "Face";
            supportTrack.SupportOrigin = (double[])plan.SupportFrameOrigin.Clone();
            state.M4SupportTracks[plan.SketchId] = supportTrack;
        }
        else if (explicitFaceMode || datumPlaneMode)
        {
            Dictionary<string, object> supportAssociation;
            Dictionary<string, object> supportFrame;
            if (!TryReadM3NativeSupport(state.WorkPart, sketch, plan.SupportPlane,
                out supportAssociation, out supportFrame))
                throw new M2ModelException("M3_SUPPORT_ASSOCIATION_INVALID", "NX did not retain the requested native support Plane reference");
            string expectedKind = explicitFaceMode ? "FACE" : "DATUM_PLANE";
            string actualKind = Convert.ToString(supportAssociation["support_kind"], CultureInfo.InvariantCulture);
            bool native = supportAssociation.ContainsKey("native") && supportAssociation["native"] is bool && (bool)supportAssociation["native"];
            string expectedId = explicitFaceMode ? request.SupportFaceId : request.SupportDatumPlaneId;
            string idKey = explicitFaceMode ? "support_face_id" : "datum_plane_id";
            string actualId = supportAssociation.ContainsKey(idKey)
                ? Convert.ToString(supportAssociation[idKey], CultureInfo.InvariantCulture) : null;
            string actualFaceTag = supportAssociation.ContainsKey("support_face_tag")
                ? Convert.ToString(supportAssociation["support_face_tag"], CultureInfo.InvariantCulture) : null;
            bool faceTagMatches = !explicitFaceMode || request.SupportFaceTag == null ||
                String.Equals(request.SupportFaceTag, actualFaceTag, StringComparison.Ordinal);
            supportAssociation["expected_support_face_tag"] = explicitFaceMode ? request.SupportFaceTag : null;
            supportAssociation["support_face_tag_matches"] = faceTagMatches;
            if (!native || !String.Equals(actualKind, expectedKind, StringComparison.Ordinal) ||
                !String.Equals(actualId, expectedId, StringComparison.Ordinal) || !faceTagMatches)
                throw new M2ModelException("M3_SUPPORT_ASSOCIATION_INVALID", "NX support Plane readback does not match the requested native Face/DatumPlane: " + M2Json.CanonicalText(supportAssociation));
            plan.SupportAssociationEvidence = supportAssociation;
        }
        Dictionary<string, object> bodyChangeEvidence;
        if (!BodyMeasuresEqual(plan.BodiesBefore, CaptureM3BodyMeasures(state.WorkPart), out bodyChangeEvidence))
            throw new M2ModelException("M3_SKETCH_CREATED_BODY", "native Sketch unexpectedly changed body measurements");

        sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
        int deactivateUpdateErrors = state.Session.UpdateManager.DoUpdate(mark);
        if (deactivateUpdateErrors != 0)
            throw new M2ModelException("M3_UPDATE_FAILED", "NX update reported errors after leaving the new Sketch edit environment");
        updateErrors += deactivateUpdateErrors;
        plan.ProfileAnalysis = V2SketchProfileAnalyzer.Analyze(state.WorkPart, sketch, true);
        if (!M3ProfileAnalysisSuccessful(plan.ProfileAnalysis))
        {
            string profileStatus = plan.ProfileAnalysis == null || !plan.ProfileAnalysis.ContainsKey("status")
                ? "ANALYSIS_UNAVAILABLE" : Convert.ToString(plan.ProfileAnalysis["status"], CultureInfo.InvariantCulture);
            if (profileStatus == "INVALID_SELF_INTERSECTION")
                throw new M2ModelException("M3_INVALID_PROFILE_SELF_INTERSECTION", "NX native curve intersection checks found a self-intersection or non-endpoint crossing");
            if (profileStatus == "INVALID_DEGENERATE")
                throw new M2ModelException("M3_INVALID_PROFILE_DEGENERATE", "NX native profile analysis found overlapping or degenerate geometry");
            throw new M2ModelException("M3_PROFILE_ANALYSIS_UNAVAILABLE", "NX native profile validity could not be established; the sketch operation was rolled back");
        }
        SnapshotResult after = Capture(binding, state);
        if (!FeaturePresent(state.WorkPart, plan.FeatureId))
            throw new M2ModelException("M3_SKETCH_NOT_CREATED", "Sketch feature disappeared after deactivation");

        plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart);
        plan.SemanticAfter = CanonicalSemanticSnapshot(after.Snapshot);
        plan.GeometryAfter = CaptureFullGeometry(state.WorkPart);
        plan.HistoryAfter = HistoryProjection(after.Snapshot);
        List<object> constraintIds = ConstraintIds(constraints);
        plan.ConstraintIds = StringObjectList(constraintIds);
        plan.DrivingDimensions = ReadM3DrivingDimensions(plan.Primitives);
        Dictionary<string, object> sketchRecord = BuildSketchDetail(state.WorkPart, sketch, state.WorkPart.PartUnits.ToString());
        resultForSketch(plan, state, sketchRecord, constraints, constraintIds, geometryEvidence, updateErrors);
    }

    private void resultForSketch(M3PlanState plan, BindingState state, Dictionary<string, object> sketchRecord,
        List<object> constraints, List<object> constraintIds, Dictionary<string, object> geometryEvidence, int updateErrors)
    {
        int dof;
        Sketch.Status sketchStatus = plan.Sketch.GetStatus(out dof);
        bool emptySketch = plan.SketchRequest != null && plan.SketchRequest.Geometries.Count == 0;
        if (emptySketch)
        {
            NXObject[] geometry = plan.Sketch.GetAllGeometry();
            if (geometry == null || geometry.Length != 0 || constraints.Count != 0)
                throw new M2ModelException("M3_SKETCH_READBACK_FAILED", "empty Sketch readback contains geometry or constraints");
        }
        else if (!M3SketchConstraintStatusAcceptable(plan, sketchStatus, dof, constraints == null ? -1 : constraints.Count))
            throw new M2ModelException("M3_SKETCH_UNDERCONSTRAINED", "native Sketch did not remain fully constrained after update");
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("sketch_id", plan.SketchId);
        result.Add("feature_id", plan.FeatureId);
        result.Add("constraint_ids", constraintIds);
        result.Add("driving_dimensions", plan.DrivingDimensions);
        result.Add("constraints", constraints);
        result.Add("sketch", sketchRecord);
        result.Add("sketch_status", sketchStatus.ToString());
        result.Add("constraint_mode", plan.SketchRequest == null || String.IsNullOrEmpty(plan.SketchRequest.ConstraintMode)
            ? "legacy_driving" : plan.SketchRequest.ConstraintMode);
        result.Add("degrees_of_freedom", dof);
        result.Add("update_error_count", updateErrors);
        result.Add("geometry_readback", geometryEvidence);
        result.Add("profile_analysis", plan.ProfileAnalysis);
        result.Add("body_inventory", BodyMeasureRecords(plan.BodiesAfter, null));
        if (plan.SupportKind == "FACE_SELECTOR")
        {
            Dictionary<string, object> association = plan.SupportAssociationEvidence == null
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : Clone(plan.SupportAssociationEvidence);
            association["native"] = true;
            association["geometry_kind"] = "Face";
            association["selector_unique"] = true;
            association["in_face_domain"] = true;
            association["current_face_matches"] = true;
            result.Add("support_association", association);
            result.Add("support_frame", M4FrameRecord(plan.SupportFrameOrigin,
                plan.SupportFrameX, plan.SupportFrameY, plan.SupportFrameNormal));
        }
        else if (plan.SupportKind == "FACE" || plan.SupportKind == "DATUM_PLANE")
        {
            result.Add("support_association", Clone(plan.SupportAssociationEvidence));
            result.Add("support_frame", M4FrameRecord(plan.SupportFrameOrigin,
                plan.SupportFrameX, plan.SupportFrameY, plan.SupportFrameNormal));
        }
        else if (plan.SupportFrameOrigin != null)
            result.Add("support_frame", M4FrameRecord(plan.SupportFrameOrigin,
                plan.SupportFrameX, plan.SupportFrameY, plan.SupportFrameNormal));
        result.Add("revision_id", plan.BindingState.RevisionNumber.ToString(CultureInfo.InvariantCulture));
        plan.Execution = M3Execution("APPLIED", result["revision_id"] as string, result, null, null);
    }

    private void ExecuteM3DatumPlane(M2ModelBinding binding, BindingState state, M3PlanState plan, Session.UndoMarkId mark)
    {
        M3DatumPlaneRequest request = plan.DatumPlaneRequest;
        if (request == null) throw new M2ModelException("INVALID_PLAN", "datum plane plan has no retained request");
        Feature feature;
        if (String.Equals(request.Mode, "offset", StringComparison.Ordinal) ||
            String.Equals(request.Mode, "angle", StringComparison.Ordinal))
        {
            NXObject reference;
            if (!String.IsNullOrWhiteSpace(request.SupportFaceId))
            {
                M4FaceCandidate currentFace = ResolveM3FaceById(state.WorkPart, request.SupportFaceId,
                    null, request.SupportFaceTag);
                if (plan.DatumReferenceFace == null ||
                    !currentFace.Face.Tag.Equals(plan.DatumReferenceFace.Face.Tag) ||
                    !String.Equals(currentFace.BodyId, plan.DatumReferenceFace.BodyId, StringComparison.Ordinal) ||
                    !VectorNearArray(currentFace.Normal, plan.DatumReferenceNormal, M3VectorTolerance) ||
                    !PointNearArray(ToPoint3d(currentFace.Origin), plan.DatumReferenceOrigin, M3FrameToleranceMm))
                    throw new M2ModelException("STALE_REVISION", "selected planar Face changed after the offset DatumPlane plan was prepared");
                plan.DatumReferenceFace = currentFace;
                reference = currentFace.Face;
            }
            else
            {
                Feature currentFeature;
                DatumPlane currentDatum = FindM3DatumPlane(state.WorkPart, request.SupportDatumPlaneId, out currentFeature);
                Point3d currentOrigin = currentDatum.Origin;
                Vector3d currentNormal = currentDatum.Normal;
                double[] currentOriginArray = new double[] { currentOrigin.X, currentOrigin.Y, currentOrigin.Z };
                double[] currentNormalArray = new double[] { currentNormal.X, currentNormal.Y, currentNormal.Z };
                Normalize3(currentNormalArray);
                if (plan.DatumReferencePlane == null || plan.DatumReferenceFeature == null ||
                    !currentDatum.Tag.Equals(plan.DatumReferencePlane.Tag) ||
                    !currentFeature.Tag.Equals(plan.DatumReferenceFeature.Tag) ||
                    !VectorNearArray(currentNormalArray, plan.DatumReferenceNormal, M3VectorTolerance) ||
                    !PointNearArray(ToPoint3d(currentOriginArray), plan.DatumReferenceOrigin, M3FrameToleranceMm))
                    throw new M2ModelException("STALE_REVISION", "selected native DatumPlane changed after the offset plan was prepared");
                plan.DatumReferencePlane = currentDatum;
                plan.DatumReferenceFeature = currentFeature;
                reference = currentDatum;
            }
            if (String.Equals(request.Mode, "offset", StringComparison.Ordinal))
                feature = M3NxFeatureFactory.CreateOffsetDatumPlane(state.WorkPart, reference, request.DistanceMm);
            else
            {
                Line currentLine = FindM3LineById(state.WorkPart, request.AxisLineId);
                Point3d currentStart = currentLine.StartPoint;
                Point3d currentEnd = currentLine.EndPoint;
                if (plan.DatumAxisLine == null || !currentLine.Tag.Equals(plan.DatumAxisLine.Tag) ||
                    !PointNearArray(currentStart, plan.DatumAxisStartMm, M3FrameToleranceMm) ||
                    !PointNearArray(currentEnd, plan.DatumAxisEndMm, M3FrameToleranceMm))
                    throw new M2ModelException("STALE_REVISION", "selected native axis Line changed after the angle DatumPlane plan was prepared");
                ValidateM3AngleAxisLine(currentLine, plan.DatumReferenceOrigin, plan.DatumReferenceNormal,
                    out plan.DatumAxisStartMm, out plan.DatumAxisEndMm);
                plan.DatumAxisLine = currentLine;
                feature = M3NxFeatureFactory.CreateAngleDatumPlane(state.WorkPart, reference, currentLine, request.AngleDeg);
            }
        }
        else if (String.Equals(request.Mode, "two_lines", StringComparison.Ordinal))
        {
            Line currentLine1 = FindM3LineById(state.WorkPart, request.Line1Id, "line1_id");
            Line currentLine2 = FindM3LineById(state.WorkPart, request.Line2Id, "line2_id");
            double[] currentLine1Start, currentLine1End, currentLine2Start, currentLine2End;
            double[] currentPlaneOrigin, currentPlaneNormal;
            ValidateM3TwoLineDatumPlane(currentLine1, currentLine2,
                out currentLine1Start, out currentLine1End, out currentLine2Start, out currentLine2End,
                out currentPlaneOrigin, out currentPlaneNormal);
            List<string> currentLine1Sources = M3LineSourceFeatureIds(state.WorkPart, currentLine1);
            List<string> currentLine2Sources = M3LineSourceFeatureIds(state.WorkPart, currentLine2);
            if (plan.DatumLine1 == null || plan.DatumLine2 == null ||
                !currentLine1.Tag.Equals(plan.DatumLine1.Tag) || !currentLine2.Tag.Equals(plan.DatumLine2.Tag) ||
                !PointNearArray(ToPoint3d(currentLine1Start), plan.DatumLine1StartMm, M3DatumLinePlaneToleranceMm) ||
                !PointNearArray(ToPoint3d(currentLine1End), plan.DatumLine1EndMm, M3DatumLinePlaneToleranceMm) ||
                !PointNearArray(ToPoint3d(currentLine2Start), plan.DatumLine2StartMm, M3DatumLinePlaneToleranceMm) ||
                !PointNearArray(ToPoint3d(currentLine2End), plan.DatumLine2EndMm, M3DatumLinePlaneToleranceMm) ||
                !PointNearArray(ToPoint3d(currentPlaneOrigin), plan.DatumTwoLineExpectedOriginMm, M3DatumLinePlaneToleranceMm) ||
                !M3UnitVectorsNearUnoriented(currentPlaneNormal, plan.DatumTwoLineExpectedNormal,
                    M3DatumLineDirectionTolerance) ||
                !StringListsEqual(currentLine1Sources, plan.DatumLine1SourceFeatureIds) ||
                !StringListsEqual(currentLine2Sources, plan.DatumLine2SourceFeatureIds))
                throw new M2ModelException("STALE_REVISION", "selected native Lines changed after the two-line DatumPlane plan was prepared");
            plan.DatumLine1 = currentLine1;
            plan.DatumLine2 = currentLine2;
            feature = M3NxFeatureFactory.CreateTwoLineDatumPlane(state.WorkPart, currentLine1, currentLine2);
        }
        else
        {
            feature = M3NxFeatureFactory.CreateDatumPlane(state.WorkPart, ToPoint3d(request.OriginMm),
                ToVector3d(request.Normal), ToVector3d(request.XAxis));
        }
        plan.Feature = feature;
        plan.FeatureId = RequiredJournalId(feature, "M3 DatumPlane feature");
        int updateErrors = state.Session.UpdateManager.DoUpdate(mark);
        if (updateErrors != 0) throw new M2ModelException("M3_UPDATE_FAILED", "NX update reported errors after DatumPlane creation");
        if (!FeaturePresent(state.WorkPart, plan.FeatureId))
            throw new M2ModelException("M3_DATUM_PLANE_NOT_CREATED", "native DatumPlane feature was not present after NX update");
        DatumPlane datum = TryGetM3DatumPlane(feature);
        if (datum == null) throw new M2ModelException("M3_DATUM_PLANE_READBACK_FAILED", "native feature has no DatumPlane output entity");
        plan.DatumPlane = datum;
        plan.DatumPlaneId = plan.FeatureId;
        Dictionary<string, object> definitionEvidence;
        bool definitionMatches = String.Equals(request.Mode, "offset", StringComparison.Ordinal)
            ? M3DatumPlaneOffsetDefinitionMatches(state.WorkPart, datum, feature, request, plan, out definitionEvidence)
            : String.Equals(request.Mode, "angle", StringComparison.Ordinal)
                ? M3DatumPlaneAngleDefinitionMatches(state.WorkPart, datum, feature, request, plan, out definitionEvidence)
                : String.Equals(request.Mode, "two_lines", StringComparison.Ordinal)
                    ? M3DatumPlaneTwoLinesDefinitionMatches(state.WorkPart, datum, feature, request, plan, out definitionEvidence)
                    : M3DatumPlaneDefinitionMatches(state.WorkPart, datum, feature, request, out definitionEvidence);
        if (!definitionMatches)
            throw new M2ModelException("M3_DATUM_PLANE_READBACK_FAILED", "native DatumPlane definition, signed offset or angle, normal, or parent association differs from the request: " +
                M2Json.CanonicalText(definitionEvidence));
        Dictionary<string, object> bodyEvidence;
        if (!BodyMeasuresEqual(plan.BodiesBefore, CaptureM3BodyMeasures(state.WorkPart), out bodyEvidence))
            throw new M2ModelException("M3_DATUM_PLANE_CHANGED_BODY", "native DatumPlane unexpectedly changed body measurements");

        SnapshotResult after = Capture(binding, state);
        plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart);
        plan.SemanticAfter = CanonicalSemanticSnapshot(after.Snapshot);
        plan.GeometryAfter = CaptureFullGeometry(state.WorkPart);
        plan.HistoryAfter = HistoryProjection(after.Snapshot);
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("datum_plane_id", plan.DatumPlaneId);
        result.Add("feature_id", plan.FeatureId);
        result.Add("datum_plane", definitionEvidence["datum_plane"]);
        result.Add("native_feature_type", feature.FeatureType);
        result.Add("update_error_count", updateErrors);
        result.Add("body_inventory", BodyMeasureRecords(plan.BodiesAfter, null));
        result.Add("revision_id", binding.RevisionId);
        plan.Execution = M3Execution("APPLIED", binding.RevisionId, result, null, null);
    }

    private static bool M3DatumPlaneDefinitionMatches(Part work, DatumPlane datum, Feature feature,
        M3DatumPlaneRequest request, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> definition = M3DatumPlaneDefinitionRecord(work, datum, feature);
        Point3d actualOrigin = datum.Origin;
        Vector3d rawNormal = datum.Normal;
        double[] actualNormal = new double[] { rawNormal.X, rawNormal.Y, rawNormal.Z };
        double length = Length3(actualNormal);
        if (length > M3VectorTolerance)
        {
            actualNormal[0] /= length; actualNormal[1] /= length; actualNormal[2] /= length;
        }
        List<object> actualXAxisRecord = definition["x_axis"] as List<object>;
        double[] actualXAxis = actualXAxisRecord == null || actualXAxisRecord.Count != 3 ? null : new double[] {
            Convert.ToDouble(actualXAxisRecord[0], CultureInfo.InvariantCulture),
            Convert.ToDouble(actualXAxisRecord[1], CultureInfo.InvariantCulture),
            Convert.ToDouble(actualXAxisRecord[2], CultureInfo.InvariantCulture) };
        bool originMatches = PointNearArray(actualOrigin, request.OriginMm, M3FrameToleranceMm);
        bool normalMatches = VectorNearArray(actualNormal, request.Normal, M3VectorTolerance);
        bool xAxisMatches = VectorNearArray(actualXAxis, request.XAxis, M3VectorTolerance);
        evidence.Add("datum_plane", definition);
        evidence.Add("expected_frame", M4FrameRecord(request.OriginMm, request.XAxis,
            Cross3(request.Normal, request.XAxis), request.Normal));
        evidence.Add("origin_matches", originMatches);
        evidence.Add("normal_matches", normalMatches);
        evidence.Add("x_axis_matches", xAxisMatches);
        evidence.Add("status", originMatches && normalMatches && xAxisMatches ? "VERIFIED" : "MISMATCH");
        return originMatches && normalMatches && xAxisMatches;
    }

    private static bool M3DatumPlaneOffsetDefinitionMatches(Part work, DatumPlane datum, Feature feature,
        M3DatumPlaneRequest request, M3PlanState plan, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> definition = M3DatumPlaneDefinitionRecord(work, datum, feature);
        object distanceValue, expressionDistanceValue, supportKindValue, parentMatchValue,
            referenceGeometryValue, normalMatchValue, expressionMatchValue, nativeMethodValue,
            referenceNormalValue, referenceOriginValue, parentIdsValue;
        double actualDistance = definition.TryGetValue("distance_mm", out distanceValue)
            ? Convert.ToDouble(distanceValue, CultureInfo.InvariantCulture) : Double.NaN;
        double expressionDistance = definition.TryGetValue("native_expression_distance_mm", out expressionDistanceValue)
            ? Convert.ToDouble(expressionDistanceValue, CultureInfo.InvariantCulture) : Double.NaN;
        bool referenceIdMatches;
        string supportKind = definition.TryGetValue("support_kind", out supportKindValue)
            ? Convert.ToString(supportKindValue, CultureInfo.InvariantCulture) : null;
        if (String.IsNullOrWhiteSpace(request.SupportFaceId))
        {
            object supportId;
            referenceIdMatches = definition.TryGetValue("support_datum_plane_id", out supportId) &&
                String.Equals(Convert.ToString(supportId, CultureInfo.InvariantCulture), request.SupportDatumPlaneId, StringComparison.Ordinal) &&
                String.Equals(supportKind, "DATUM_PLANE", StringComparison.Ordinal);
        }
        else
        {
            object supportId;
            bool journalIdMatches = definition.TryGetValue("support_face_id", out supportId) &&
                String.Equals(Convert.ToString(supportId, CultureInfo.InvariantCulture), request.SupportFaceId, StringComparison.Ordinal) &&
                String.Equals(supportKind, "FACE", StringComparison.Ordinal);
            object supportFaceTagValue;
            bool supportFaceTagMatches = request.SupportFaceTag == null ||
                (definition.TryGetValue("support_face_tag", out supportFaceTagValue) &&
                 String.Equals(Convert.ToString(supportFaceTagValue, CultureInfo.InvariantCulture),
                    request.SupportFaceTag, StringComparison.Ordinal));
            referenceIdMatches = journalIdMatches && supportFaceTagMatches;
            evidence.Add("support_face_tag_matches", supportFaceTagMatches);
        }
        bool parentMatches = definition.TryGetValue("parent_reference_matches", out parentMatchValue) &&
            parentMatchValue is bool && (bool)parentMatchValue;
        bool nativeGeometry = definition.TryGetValue("native_reference_geometry", out referenceGeometryValue) &&
            referenceGeometryValue is bool && (bool)referenceGeometryValue;
        bool normalMatches = definition.TryGetValue("normal_matches_reference", out normalMatchValue) &&
            normalMatchValue is bool && (bool)normalMatchValue;
        bool expressionMatches = definition.TryGetValue("expression_matches_geometry", out expressionMatchValue) &&
            expressionMatchValue is bool && (bool)expressionMatchValue;
        bool nativeMethodMatches = definition.TryGetValue("native_method", out nativeMethodValue) &&
            String.Equals(Convert.ToString(nativeMethodValue, CultureInfo.InvariantCulture), "Distance", StringComparison.Ordinal);
        double[] actualReferenceNormal = M3ReadNumberArray(definition.TryGetValue("reference_normal", out referenceNormalValue) ? referenceNormalValue : null, 3);
        double[] actualReferenceOrigin = M3ReadNumberArray(definition.TryGetValue("reference_origin_mm", out referenceOriginValue) ? referenceOriginValue : null, 3);
        List<object> nativeParentIds = definition.TryGetValue("native_parent_ids", out parentIdsValue)
            ? parentIdsValue as List<object> : null;
        bool expectedSourceParent = false;
        if (nativeParentIds != null && !String.IsNullOrWhiteSpace(request.SupportDatumPlaneId))
        {
            foreach (object parentId in nativeParentIds)
                if (String.Equals(Convert.ToString(parentId, CultureInfo.InvariantCulture), request.SupportDatumPlaneId, StringComparison.Ordinal))
                    expectedSourceParent = true;
        }
        else if (String.Equals(request.Mode, "offset", StringComparison.Ordinal) &&
            String.IsNullOrWhiteSpace(request.SupportDatumPlaneId))
            expectedSourceParent = parentMatches;

        Point3d actualOrigin = datum.Origin;
        Vector3d rawNormal = datum.Normal;
        double[] actualNormal = new double[] { rawNormal.X, rawNormal.Y, rawNormal.Z };
        Normalize3(actualNormal);
        bool normalMatchesReference = VectorNearArray(actualNormal, plan.DatumReferenceNormal, M3VectorTolerance) &&
            VectorNearArray(actualReferenceNormal, plan.DatumReferenceNormal, M3VectorTolerance);
        bool referenceOriginMatches = actualReferenceOrigin != null && PointNearArray(ToPoint3d(actualReferenceOrigin),
            plan.DatumReferenceOrigin, M3FrameToleranceMm);
        bool distanceMatches = !Double.IsNaN(actualDistance) && !Double.IsInfinity(actualDistance) &&
            Math.Abs(actualDistance - request.DistanceMm) <= M3FrameToleranceMm;
        bool expressionDistanceMatches = !Double.IsNaN(expressionDistance) && !Double.IsInfinity(expressionDistance) &&
            Math.Abs(expressionDistance - request.DistanceMm) <= M3FrameToleranceMm;
        bool definitionMatches = nativeMethodMatches && referenceIdMatches && nativeGeometry && parentMatches &&
            expectedSourceParent && normalMatchesReference && referenceOriginMatches && distanceMatches &&
            expressionDistanceMatches && expressionMatches;
        evidence.Add("datum_plane", definition);
        evidence.Add("mode_matches", definition.ContainsKey("mode") &&
            String.Equals(Convert.ToString(definition["mode"], CultureInfo.InvariantCulture), "offset", StringComparison.Ordinal));
        evidence.Add("requested_distance_mm", request.DistanceMm);
        evidence.Add("actual_distance_mm", actualDistance);
        evidence.Add("native_expression_distance_mm", expressionDistance);
        evidence.Add("distance_matches", distanceMatches);
        evidence.Add("expression_distance_matches", expressionDistanceMatches);
        evidence.Add("native_method_matches", nativeMethodMatches);
        evidence.Add("reference_id_matches", referenceIdMatches);
        evidence.Add("native_reference_geometry", nativeGeometry);
        evidence.Add("native_parent_association_matches", expectedSourceParent);
        evidence.Add("normal_matches_reference", normalMatchesReference);
        evidence.Add("reference_origin_matches_prepare", referenceOriginMatches);
        evidence.Add("expression_matches_geometry", expressionMatches);
        evidence.Add("actual_origin_mm", Point(actualOrigin));
        evidence.Add("actual_normal", NumberList(actualNormal));
        evidence.Add("reference_normal", actualReferenceNormal == null ? null : NumberList(actualReferenceNormal));
        evidence.Add("native_parent_ids", nativeParentIds ?? new List<object>());
        evidence.Add("status", definitionMatches ? "VERIFIED" : "MISMATCH");
        return definitionMatches;
    }

    private static bool M3DatumPlaneAngleDefinitionMatches(Part work, DatumPlane datum, Feature feature,
        M3DatumPlaneRequest request, M3PlanState plan, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> definition = M3DatumPlaneDefinitionRecord(work, datum, feature);
        object value;
        double actualAngle = definition.TryGetValue("angle_deg", out value)
            ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : Double.NaN;
        double nativeExpressionAngle = definition.TryGetValue("native_expression_angle_deg", out value)
            ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : Double.NaN;
        string supportKind = definition.TryGetValue("support_kind", out value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        bool supportIdMatches;
        if (!String.IsNullOrWhiteSpace(request.SupportDatumPlaneId))
        {
            supportIdMatches = definition.TryGetValue("support_datum_plane_id", out value) &&
                String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), request.SupportDatumPlaneId, StringComparison.Ordinal) &&
                String.Equals(supportKind, "DATUM_PLANE", StringComparison.Ordinal);
        }
        else
        {
            bool journalIdMatches = definition.TryGetValue("support_face_id", out value) &&
                String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), request.SupportFaceId, StringComparison.Ordinal) &&
                String.Equals(supportKind, "FACE", StringComparison.Ordinal);
            bool supportFaceTagMatches = request.SupportFaceTag == null ||
                (definition.TryGetValue("support_face_tag", out value) &&
                 String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture),
                    request.SupportFaceTag, StringComparison.Ordinal));
            supportIdMatches = journalIdMatches && supportFaceTagMatches;
            evidence.Add("support_face_tag_matches", supportFaceTagMatches);
        }
        bool axisIdMatches = definition.TryGetValue("axis_line_id", out value) &&
            String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), request.AxisLineId, StringComparison.Ordinal);
        bool nativeGeometry = definition.TryGetValue("native_reference_geometry", out value) && value is bool && (bool)value;
        bool parentMatches = definition.TryGetValue("parent_reference_matches", out value) && value is bool && (bool)value;
        bool expressionMatchesGeometry = definition.TryGetValue("expression_matches_geometry", out value) && value is bool && (bool)value;
        bool normalMatchesAngle = definition.TryGetValue("normal_matches_angle", out value) && value is bool && (bool)value;
        bool axisInResultPlane = definition.TryGetValue("axis_in_result_plane", out value) && value is bool && (bool)value;
        bool nativeMethodMatches = definition.TryGetValue("native_method", out value) &&
            String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "Angle", StringComparison.Ordinal);
        double[] actualReferenceNormal = M3ReadNumberArray(
            definition.TryGetValue("reference_normal", out value) ? value : null, 3);
        double[] actualReferenceOrigin = M3ReadNumberArray(
            definition.TryGetValue("reference_origin_mm", out value) ? value : null, 3);
        double[] actualAxisStart = M3ReadNumberArray(
            definition.TryGetValue("axis_start_mm", out value) ? value : null, 3);
        double[] actualAxisEnd = M3ReadNumberArray(
            definition.TryGetValue("axis_end_mm", out value) ? value : null, 3);
        double[] actualNormal = new double[] { datum.Normal.X, datum.Normal.Y, datum.Normal.Z };
        Normalize3(actualNormal);
        Point3d actualOrigin = datum.Origin;
        bool referenceFrameMatches = actualReferenceNormal != null && actualReferenceOrigin != null &&
            VectorNearArray(actualReferenceNormal, plan.DatumReferenceNormal, M3VectorTolerance) &&
            PointNearArray(ToPoint3d(actualReferenceOrigin), plan.DatumReferenceOrigin, M3FrameToleranceMm);
        bool axisFrameMatches = actualAxisStart != null && actualAxisEnd != null &&
            PointNearArray(ToPoint3d(actualAxisStart), plan.DatumAxisStartMm, M3FrameToleranceMm) &&
            PointNearArray(ToPoint3d(actualAxisEnd), plan.DatumAxisEndMm, M3FrameToleranceMm);
        bool angleMatches = !Double.IsNaN(actualAngle) && !Double.IsInfinity(actualAngle) &&
            CircularAngleDifferenceDeg(actualAngle, request.AngleDeg) <= M3AngleToleranceDeg;
        bool expressionMatchesRequest = !Double.IsNaN(nativeExpressionAngle) && !Double.IsInfinity(nativeExpressionAngle) &&
            Math.Abs(nativeExpressionAngle + request.AngleDeg) <= M3AngleToleranceDeg;
        bool definitionMatches = nativeMethodMatches && supportIdMatches && axisIdMatches && nativeGeometry &&
            parentMatches && normalMatchesAngle && axisInResultPlane && expressionMatchesGeometry && referenceFrameMatches && axisFrameMatches &&
            angleMatches && expressionMatchesRequest;
        evidence.Add("datum_plane", definition);
        evidence.Add("mode_matches", definition.ContainsKey("mode") &&
            String.Equals(Convert.ToString(definition["mode"], CultureInfo.InvariantCulture), "angle", StringComparison.Ordinal));
        evidence.Add("requested_angle_deg", request.AngleDeg);
        evidence.Add("actual_angle_deg", actualAngle);
        evidence.Add("native_expression_angle_deg", nativeExpressionAngle);
        evidence.Add("angle_matches", angleMatches);
        evidence.Add("expression_matches_requested_right_hand_angle", expressionMatchesRequest);
        evidence.Add("native_method_matches", nativeMethodMatches);
        evidence.Add("support_id_matches", supportIdMatches);
        evidence.Add("axis_line_id_matches", axisIdMatches);
        evidence.Add("native_reference_geometry", nativeGeometry);
        evidence.Add("native_parent_association_matches", parentMatches);
        evidence.Add("normal_matches_angle", normalMatchesAngle);
        evidence.Add("axis_in_result_plane", axisInResultPlane);
        evidence.Add("expression_matches_geometry", expressionMatchesGeometry);
        evidence.Add("reference_frame_matches_prepare", referenceFrameMatches);
        evidence.Add("axis_frame_matches_prepare", axisFrameMatches);
        evidence.Add("actual_origin_mm", Point(actualOrigin));
        evidence.Add("actual_normal", NumberList(actualNormal));
        evidence.Add("reference_normal", actualReferenceNormal == null ? null : NumberList(actualReferenceNormal));
        evidence.Add("axis_start_mm", actualAxisStart == null ? null : NumberList(actualAxisStart));
        evidence.Add("axis_end_mm", actualAxisEnd == null ? null : NumberList(actualAxisEnd));
        evidence.Add("axis_start_result_plane_residual_mm", definition.ContainsKey("axis_start_result_plane_residual_mm") ? definition["axis_start_result_plane_residual_mm"] : null);
        evidence.Add("axis_end_result_plane_residual_mm", definition.ContainsKey("axis_end_result_plane_residual_mm") ? definition["axis_end_result_plane_residual_mm"] : null);
        evidence.Add("native_parent_ids", definition.ContainsKey("native_parent_ids") ? definition["native_parent_ids"] : new List<object>());
        evidence.Add("status", definitionMatches ? "VERIFIED" : "MISMATCH");
        return definitionMatches;
    }

    private static bool M3DatumPlaneTwoLinesDefinitionMatches(Part work, DatumPlane datum,
        Feature feature, M3DatumPlaneRequest request, M3PlanState plan, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> definition = M3DatumPlaneDefinitionRecord(work, datum, feature);
        object value;
        string actualLine1Id = definition.TryGetValue("line1_id", out value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        string actualLine2Id = definition.TryGetValue("line2_id", out value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        bool sameOrder = String.Equals(actualLine1Id, request.Line1Id, StringComparison.Ordinal) &&
            String.Equals(actualLine2Id, request.Line2Id, StringComparison.Ordinal);
        bool reverseOrder = String.Equals(actualLine1Id, request.Line2Id, StringComparison.Ordinal) &&
            String.Equals(actualLine2Id, request.Line1Id, StringComparison.Ordinal);
        bool lineIdsMatch = sameOrder || reverseOrder;

        double[] actualLine1Start = M3ReadNumberArray(
            definition.TryGetValue("line1_start_mm", out value) ? value : null, 3);
        double[] actualLine1End = M3ReadNumberArray(
            definition.TryGetValue("line1_end_mm", out value) ? value : null, 3);
        double[] actualLine2Start = M3ReadNumberArray(
            definition.TryGetValue("line2_start_mm", out value) ? value : null, 3);
        double[] actualLine2End = M3ReadNumberArray(
            definition.TryGetValue("line2_end_mm", out value) ? value : null, 3);
        List<string> actualLine1Sources = M3ReadStringList(
            definition.TryGetValue("line1_source_feature_ids", out value) ? value : null);
        List<string> actualLine2Sources = M3ReadStringList(
            definition.TryGetValue("line2_source_feature_ids", out value) ? value : null);
        if (reverseOrder)
        {
            double[] temporary = actualLine1Start; actualLine1Start = actualLine2Start; actualLine2Start = temporary;
            temporary = actualLine1End; actualLine1End = actualLine2End; actualLine2End = temporary;
            List<string> sourceTemporary = actualLine1Sources; actualLine1Sources = actualLine2Sources; actualLine2Sources = sourceTemporary;
        }
        bool endpointsMatch = actualLine1Start != null && actualLine1End != null &&
            actualLine2Start != null && actualLine2End != null &&
            VectorNearArray(actualLine1Start, plan.DatumLine1StartMm, M3DatumLinePlaneToleranceMm) &&
            VectorNearArray(actualLine1End, plan.DatumLine1EndMm, M3DatumLinePlaneToleranceMm) &&
            VectorNearArray(actualLine2Start, plan.DatumLine2StartMm, M3DatumLinePlaneToleranceMm) &&
            VectorNearArray(actualLine2End, plan.DatumLine2EndMm, M3DatumLinePlaneToleranceMm);

        double[] actualNormal = new double[] { datum.Normal.X, datum.Normal.Y, datum.Normal.Z };
        Normalize3(actualNormal);
        bool normalMatchesPlane = M3UnitVectorsNearUnoriented(actualNormal,
            plan.DatumTwoLineExpectedNormal, M3DatumLineDirectionTolerance);
        bool nativeGeometry = definition.TryGetValue("native_reference_geometry", out value) &&
            value is bool && (bool)value;
        bool allEndpointsOnPlane = definition.TryGetValue("all_line_endpoints_on_native_plane", out value) &&
            value is bool && (bool)value;
        bool nativeMethodMatches = definition.TryGetValue("native_method_matches", out value) &&
            value is bool && (bool)value;
        bool nativeAlternateMatches = definition.TryGetValue("native_alternate_matches", out value) &&
            value is bool && (bool)value;
        bool nativeMethod = definition.TryGetValue("native_method", out value) &&
            String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "TwoLines", StringComparison.Ordinal);
        bool nativeAlternate = definition.TryGetValue("native_alternate", out value) &&
            String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "Three", StringComparison.Ordinal);
        bool modeMatches = definition.TryGetValue("mode", out value) &&
            String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "two_lines", StringComparison.Ordinal);
        bool sourceFeatureIdsMatch = StringListsEqual(actualLine1Sources, plan.DatumLine1SourceFeatureIds) &&
            StringListsEqual(actualLine2Sources, plan.DatumLine2SourceFeatureIds);
        bool parentReferenceMatches = definition.TryGetValue("parent_reference_matches", out value) &&
            value is bool && (bool)value;

        bool definitionMatches = modeMatches && nativeGeometry && nativeMethod && nativeAlternate &&
            nativeMethodMatches && nativeAlternateMatches && lineIdsMatch && endpointsMatch &&
            sourceFeatureIdsMatch && parentReferenceMatches && allEndpointsOnPlane && normalMatchesPlane;
        evidence.Add("datum_plane", definition);
        evidence.Add("mode_matches", modeMatches);
        evidence.Add("native_method_matches", nativeMethod && nativeMethodMatches);
        evidence.Add("native_alternate_matches", nativeAlternate && nativeAlternateMatches);
        evidence.Add("native_reference_geometry", nativeGeometry);
        evidence.Add("line_ids_match", lineIdsMatch);
        evidence.Add("line_endpoints_match_prepare", endpointsMatch);
        evidence.Add("source_feature_ids_match_prepare", sourceFeatureIdsMatch);
        evidence.Add("parent_reference_matches", parentReferenceMatches);
        evidence.Add("all_line_endpoints_on_native_plane", allEndpointsOnPlane);
        evidence.Add("normal_matches_plane_unoriented", normalMatchesPlane);
        evidence.Add("expected_normal", NumberList(plan.DatumTwoLineExpectedNormal));
        evidence.Add("actual_normal", NumberList(actualNormal));
        evidence.Add("line1_id", actualLine1Id);
        evidence.Add("line2_id", actualLine2Id);
        evidence.Add("line1_start_mm", actualLine1Start == null ? null : NumberList(actualLine1Start));
        evidence.Add("line1_end_mm", actualLine1End == null ? null : NumberList(actualLine1End));
        evidence.Add("line2_start_mm", actualLine2Start == null ? null : NumberList(actualLine2Start));
        evidence.Add("line2_end_mm", actualLine2End == null ? null : NumberList(actualLine2End));
        evidence.Add("line1_endpoint_plane_residuals_mm", definition.ContainsKey("line1_endpoint_plane_residuals_mm") ? definition["line1_endpoint_plane_residuals_mm"] : null);
        evidence.Add("line2_endpoint_plane_residuals_mm", definition.ContainsKey("line2_endpoint_plane_residuals_mm") ? definition["line2_endpoint_plane_residuals_mm"] : null);
        evidence.Add("native_parent_ids", definition.ContainsKey("native_parent_ids") ? definition["native_parent_ids"] : new List<object>());
        evidence.Add("status", definitionMatches ? "VERIFIED" : "MISMATCH");
        return definitionMatches;
    }

    private static double[] M3ReadNumberArray(object value, int length)
    {
        List<object> list = value as List<object>;
        if (list == null || list.Count != length) return null;
        double[] result = new double[length];
        for (int i = 0; i < length; i++)
        {
            try { result[i] = Convert.ToDouble(list[i], CultureInfo.InvariantCulture); }
            catch { return null; }
            if (Double.IsNaN(result[i]) || Double.IsInfinity(result[i])) return null;
        }
        return result;
    }

    private static List<string> M3ReadStringList(object value)
    {
        List<object> items = value as List<object>;
        if (items == null) return null;
        List<string> result = new List<string>();
        foreach (object item in items)
        {
            string text = item as string;
            if (text == null) return null;
            result.Add(text);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private M3VerificationResult VerifyM3DatumPlanePlan(M2ModelBinding binding, M3PlanState plan, BindingState state)
    {
        SnapshotResult captured = Capture(binding, state);
        Feature feature = FeaturePresent(state.WorkPart, plan.FeatureId) ? FindFeature(state.WorkPart, plan.FeatureId) : null;
        DatumPlane datum = TryGetM3DatumPlane(feature);
        Dictionary<string, object> definitionEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bool definitionMatches = datum != null && (String.Equals(plan.DatumPlaneRequest.Mode, "offset", StringComparison.Ordinal)
            ? M3DatumPlaneOffsetDefinitionMatches(state.WorkPart, datum, feature, plan.DatumPlaneRequest, plan, out definitionEvidence)
            : String.Equals(plan.DatumPlaneRequest.Mode, "angle", StringComparison.Ordinal)
                ? M3DatumPlaneAngleDefinitionMatches(state.WorkPart, datum, feature, plan.DatumPlaneRequest, plan, out definitionEvidence)
                : String.Equals(plan.DatumPlaneRequest.Mode, "two_lines", StringComparison.Ordinal)
                    ? M3DatumPlaneTwoLinesDefinitionMatches(state.WorkPart, datum, feature, plan.DatumPlaneRequest, plan, out definitionEvidence)
                    : M3DatumPlaneDefinitionMatches(state.WorkPart, datum, feature, plan.DatumPlaneRequest, out definitionEvidence));
        if (!definitionMatches && datum == null)
            definitionEvidence.Add("status", "MISMATCH");
        string geometryNow = CaptureFullGeometry(state.WorkPart);
        List<object> historyNow = HistoryProjection(captured.Snapshot);
        bool historyMatches = String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(captured.Snapshot), StringComparison.Ordinal) &&
            String.Equals(plan.GeometryAfter, geometryNow, StringComparison.Ordinal) &&
            SemanticValuesEqual(plan.HistoryAfter, historyNow, "history");
        Dictionary<string, object> bodyEvidence;
        bool bodiesMatch = BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesAfter, null),
            BodyMeasureRecords(CaptureM3BodyMeasures(state.WorkPart), null), out bodyEvidence);
        Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        historyEvidence.Add("feature_id", plan.FeatureId);
        historyEvidence.Add("semantic_matches_execute", String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(captured.Snapshot), StringComparison.Ordinal));
        historyEvidence.Add("geometry_snapshot_matches_execute", String.Equals(plan.GeometryAfter, geometryNow, StringComparison.Ordinal));
        historyEvidence.Add("before", plan.HistoryBefore);
        historyEvidence.Add("after_execute", plan.HistoryAfter);
        historyEvidence.Add("after_verify", historyNow);
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        checks.Add("native_feature", VerificationCheck(datum != null && FeaturePresent(state.WorkPart, plan.FeatureId) ? "VERIFIED" : "MISMATCH",
            new Dictionary<string, object>(StringComparer.Ordinal) {
                { "feature_id", plan.FeatureId }, { "native_datum_plane_entity", datum != null },
                { "feature_type", feature == null ? null : feature.FeatureType }
            }));
        checks.Add("definition", VerificationCheck(definitionMatches ? "VERIFIED" : "MISMATCH", definitionEvidence));
        checks.Add("history", VerificationCheck(historyMatches ? "VERIFIED" : "MISMATCH", historyEvidence));
        checks.Add("bodies", VerificationCheck(bodiesMatch ? "VERIFIED" : "MISMATCH", bodyEvidence));
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("datum_plane_id", plan.DatumPlaneId);
        result.Add("feature_id", plan.FeatureId);
        result.Add("datum_plane", datum == null ? null : M3DatumPlaneDefinitionRecord(state.WorkPart, datum, feature));
        result.Add("revision_id", binding.RevisionId);
        bool verified = definitionMatches && historyMatches && bodiesMatch && datum != null;
        return new M3VerificationResult
        {
            Verified = verified,
            RevisionId = binding.RevisionId,
            Checks = checks,
            Result = result,
            ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified ? null : "Native DatumPlane definition, feature history, or body measurements differ from the executed result."
        };
    }

    private void ExecuteM3Extrude(M2ModelBinding binding, BindingState state, M3PlanState plan, Session.UndoMarkId mark)
    {
        RefreshM4SupportTracksFromPart(state.WorkPart, state);
        M3ExtrudeRequest request = plan.ExtrudeRequest;
        M3BooleanMode mode = request.Mode == "create" ? M3BooleanMode.Create :
            (request.Mode == "unite" ? M3BooleanMode.Unite : M3BooleanMode.Subtract);
        M3ExtrudeExtent extent = request.Extent == "through_all" ? M3ExtrudeExtent.ThroughAll : M3ExtrudeExtent.Finite;
        M3ExtrudeDirection direction = request.Direction == "positive" ? M3ExtrudeDirection.Positive : M3ExtrudeDirection.Negative;
        string inputDomainFingerprint = GetProfileFingerprint(plan.ProfileAnalysis, "input_geometry_domain_fingerprint");
        Dictionary<string, object> sectionEvidence;
        Feature feature = M3NxFeatureFactory.CreateExtrude(state.WorkPart, plan.Sketch,
            inputDomainFingerprint, request.ProfileOutput, extent, request.StartOffsetMm,
            request.EndOffsetMm, direction, mode, plan.TargetBody, out sectionEvidence);
        plan.ExtrudeSectionEvidence = sectionEvidence;
        plan.Feature = feature;
        plan.FeatureId = RequiredJournalId(feature, "M3 Extrude feature");
        int updateErrors = state.Session.UpdateManager.DoUpdate(mark);
        if (updateErrors != 0) throw new M2ModelException("M3_UPDATE_FAILED", "NX update reported errors after Extrude creation");
        if (plan.ForcePostCommitVerificationFailureForTesting)
            throw new M2ModelException("F12_FORCED_POST_COMMIT_FAILURE", "test-only failure injected after native Extrude Commit and update");
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
                plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
                plan.ExecutionFailureEvidence.Add("support_associations", supportRecords);
                plan.ExecutionFailureEvidence.Add("refreshed_source_sketch_ids",
                    refreshSourceSupport.Count == 0 ? new List<object>() : new List<object>(refreshSourceSupport));
                plan.ExecutionFailureEvidence.Add("extrude_feature_id", plan.FeatureId);
                throw new M2ModelException("M4_FACE_ASSOCIATION_INVALID", "appended Extrude no longer has a unique native Face support association");
            }
            foreach (object value in supportRecords)
            {
                Dictionary<string, object> item = value as Dictionary<string, object>;
                if (item != null && String.Equals(Convert.ToString(item["sketch_id"], CultureInfo.InvariantCulture), plan.SketchId, StringComparison.Ordinal))
                { plan.SupportAssociationEvidence = item; break; }
            }
        }
        SnapshotResult after = Capture(binding, state);
        plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart, F12MassPropertyAccuracy);
        plan.AffectedBodies = ResolveAffectedBodies(plan, plan.BodiesAfter, feature);
        plan.AffectedBodyIds = new List<string>();
        foreach (M3BodyMeasure affectedBody in plan.AffectedBodies) plan.AffectedBodyIds.Add(affectedBody.Id);
        plan.AffectedBodyIds.Sort(StringComparer.Ordinal);
        if (plan.AffectedBodies.Count == 0)
            throw new M2ModelException("M3_BODY_READ_FAILED", "Extrude feature returned no result bodies");
        plan.AffectedBodyId = plan.AffectedBodies[0].Id;
        plan.BodyFingerprintsAfter = CaptureF12BodyFingerprints(state.WorkPart, plan.BodiesAfter);
        if (String.Equals(request.ProfileOutput, "sheet", StringComparison.Ordinal))
        {
            bool sheetAreaVerified = F12SheetSurfaceAreaMatches(state.WorkPart, plan.AffectedBodies,
                plan.ProfileAnalysis, request.EndOffsetMm - request.StartOffsetMm,
                out plan.ExpectedSheetSurfaceAreaMm2, out plan.SheetSurfaceAreaMm2, out plan.SheetAreaEvidence);
            if (!sheetAreaVerified)
            {
                plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
                plan.ExecutionFailureEvidence.Add("sheet_surface_area", plan.SheetAreaEvidence);
                throw new M2ModelException("M3_SHEET_GEOMETRY_READBACK_FAILED", "NX sheet output did not have positive area matching the complete source profile length and extrusion span");
            }
        }
        plan.F12CurveInventoryAfter = M3SketchProjectionCurveInventoryText(
            CaptureM3SketchProjectionCurveInventory(state.WorkPart));
        Dictionary<string, object> preservationEvidence;
        bool modelStatePreserved = F12PreExistingModelStatePreserved(plan.F12SemanticBefore, after.Snapshot,
            plan, out preservationEvidence);
        bool curveInventoryMatches = String.Equals(plan.F12CurveInventoryBefore, plan.F12CurveInventoryAfter,
            StringComparison.Ordinal);
        preservationEvidence["complete_part_curve_inventory_preserved"] = curveInventoryMatches;
        if (!modelStatePreserved || !curveInventoryMatches)
        {
            preservationEvidence["status"] = "MISMATCH";
            plan.F12PreservationEvidence = preservationEvidence;
            plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            plan.ExecutionFailureEvidence.Add("before_after_model_preservation", preservationEvidence);
            throw new M2ModelException("F12_MODEL_PRESERVATION_FAILED", "Extrude changed pre-existing sketch, curve, expression, feature, dependency, part, or session state");
        }
        plan.F12PreservationEvidence = preservationEvidence;
        Dictionary<string, object> extrudeDefinitionEvidence;
        if (!M3ExtrudeDefinitionMatches(plan, feature, out extrudeDefinitionEvidence))
            throw new M2ModelException("M3_EXTRUDE_READBACK_FAILED", "live NX Extrude Section, output body type, direction, limits, boolean target, or sketch dependency differs from the request");
        plan.ExtrudeDefinitionEvidence = extrudeDefinitionEvidence;
        Dictionary<string, object> effectEvidence;
        if (!M3BodyEffectMatches(plan, plan.AffectedBodies, plan.BodiesAfter, out effectEvidence))
            throw new M2ModelException("M3_BOOLEAN_NO_EFFECT", "NX Extrude output bodies, Boolean result, or non-target body geometry did not match the requested operation");
        plan.SemanticAfter = CanonicalSemanticSnapshot(after.Snapshot);
        plan.GeometryAfter = CaptureFullGeometry(state.WorkPart);
        plan.HistoryAfter = HistoryProjection(after.Snapshot);
        if (!FeaturePresent(state.WorkPart, plan.FeatureId))
            throw new M2ModelException("M3_FEATURE_NOT_CREATED", "native Extrude feature was not present after NX update");

        Dictionary<string, object> sketchGeometryEvidence;
        if (!SketchGeometryMatches(plan, plan.Sketch, out sketchGeometryEvidence))
        {
            plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            plan.ExecutionFailureEvidence.Add("source_sketch_geometry", sketchGeometryEvidence);
            throw new M2ModelException("M3_SKETCH_CHANGED", "Extrude changed the source Sketch geometry");
        }
        Dictionary<string, object> sketchConstraintEvidence;
        if (!SketchConstraintsMatch(plan, plan.Sketch, out sketchConstraintEvidence))
        {
            plan.ExecutionFailureEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            plan.ExecutionFailureEvidence.Add("source_sketch_constraints", sketchConstraintEvidence);
            throw new M2ModelException("M3_SKETCH_CHANGED", "Extrude changed source Sketch constraints, dimensions, or reference state");
        }
        Dictionary<string, object> targetBefore = plan.TargetBodyId == null ? null : FindBodyMeasure(plan.BodiesBefore, plan.TargetBodyId).PublicRecord;
        List<object> affectedBodyRecords = BodyMeasureRecords(plan.AffectedBodies, null);
        List<object> nonTargetBefore = F12NonTargetFingerprintRecords(plan.BodyFingerprintsBefore,
            plan.TargetBody, new List<M3BodyMeasure>());
        List<object> nonTargetAfter = F12NonTargetFingerprintRecords(plan.BodyFingerprintsAfter,
            plan.TargetBody, plan.AffectedBodies);
        plan.ConstraintIds = new List<string>();
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("sketch_id", plan.SketchId);
        result.Add("feature_id", plan.FeatureId);
        result.Add("body_id", plan.AffectedBodyId);
        result.Add("body_ids", StringListAsObjects(plan.AffectedBodyIds));
        result.Add("bodies", affectedBodyRecords);
        result.Add("mode", request.Mode);
        result.Add("profile_output", request.ProfileOutput);
        result.Add("direction", request.Direction);
        result.Add("extent", request.Extent);
        result.Add("start_offset_mm", request.StartOffsetMm);
        result.Add("end_offset_mm", request.Extent == "finite" ? (object)request.EndOffsetMm : null);
        result.Add("span_mm", request.Extent == "finite" ? (object)(request.EndOffsetMm - request.StartOffsetMm) : null);
        result.Add("depth_mm", request.Extent == "finite" && request.StartOffsetMm == 0.0
            ? (object)request.EndOffsetMm : null);
        result.Add("target_body_id", plan.TargetBodyId);
        result.Add("target_body_before", targetBefore);
        result.Add("target_bodies_after", affectedBodyRecords);
        result.Add("non_target_bodies_before", nonTargetBefore);
        result.Add("non_target_bodies_after", nonTargetAfter);
        result.Add("body_inventory", BodyMeasureRecords(plan.BodiesAfter, null));
        result.Add("volume_mm3", AggregateBodyVolume(plan.AffectedBodies));
        result.Add("surface_area_mm2", plan.SheetSurfaceAreaMm2);
        result.Add("expected_surface_area_mm2", plan.ExpectedSheetSurfaceAreaMm2);
        result.Add("sheet_surface_area_evidence", plan.SheetAreaEvidence);
        result.Add("bbox_mm", AggregateBodyBounds(plan.AffectedBodies));
        result.Add("profile_analysis", plan.ProfileAnalysis);
        result.Add("section_evidence", sectionEvidence);
        result.Add("body_effect", effectEvidence);
        result.Add("extrude_definition", extrudeDefinitionEvidence);
        if (plan.SupportAssociationEvidence != null)
        {
            Dictionary<string, object> association = Clone(plan.SupportAssociationEvidence);
            result.Add("support_association", association);
            result.Add("support_frame", association["support_frame"]);
        }
        result.Add("update_error_count", updateErrors);
        result.Add("revision_id", binding.RevisionId);
        plan.Execution = M3Execution("APPLIED", binding.RevisionId, result, null, null);
    }

    private static M3SketchPlane ParseSketchPlane(string plane)
    {
        if (plane == "XY") return M3SketchPlane.XY;
        if (plane == "XZ") return M3SketchPlane.XZ;
        if (plane == "YZ") return M3SketchPlane.YZ;
        throw new M2ModelException("INVALID_REQUEST", "plane must be XY, XZ, or YZ");
    }

    private static string ExpressionSuffix(string value)
    {
        StringBuilder result = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            result.Append(((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_') ? c : '_');
        }
        if (result.Length == 0 || (result[0] >= '0' && result[0] <= '9')) result.Insert(0, "g_");
        return result.ToString();
    }

    private static void AddM3Dimensions(M3PrimitiveState primitive,
        SketchDimensionalConstraint[] dimensions, string prefix)
    {
        if (dimensions == null) throw new M2ModelException("M3_DIMENSION_FAILED", "NX returned no Driving dimension list");
        string marker = prefix + "_";
        foreach (SketchDimensionalConstraint dimension in dimensions)
        {
            if (dimension == null || dimension.AssociatedExpression == null ||
                dimension.DimensionState != SketchDimensionalConstraint.DimensionStateType.Driving)
                throw new M2ModelException("M3_DIMENSION_FAILED", "NX returned a sketch dimension that is not Driving");
            string name = dimension.AssociatedExpression.Name ?? String.Empty;
            if (!name.StartsWith(marker, StringComparison.Ordinal))
                throw new M2ModelException("M3_DIMENSION_FAILED", "NX Driving expression name did not match its M3 primitive");
            string role = name.Substring(marker.Length);
            M3DimensionState record = new M3DimensionState();
            record.Dimension = dimension;
            record.GeometryId = primitive.RequestId;
            record.Role = role;
            record.ExpectedValue = dimension.AssociatedExpression.Value;
            EnsureFinite(record.ExpectedValue, "M3 dimension value");
            primitive.Dimensions.Add(record);
        }
    }

    private static List<object> ReadConstraintRecords(Sketch sketch)
    {
        return BuildSketchConstraints(sketch);
    }

    private static List<object> ConstraintIds(List<object> constraints)
    {
        List<object> ids = new List<object>();
        foreach (object value in constraints)
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            if (record == null || !(record["journal_id"] is string))
                throw new M2ModelException("MODEL_READ_FAILED", "sketch constraint record has no journal_id");
            ids.Add(record["journal_id"]);
        }
        return ids;
    }

    private static List<string> StringObjectList(List<object> values)
    {
        List<string> result = new List<string>();
        foreach (object value in values)
        {
            string text = value as string;
            if (text == null) throw new M2ModelException("MODEL_READ_FAILED", "constraint id is not a string");
            result.Add(text);
        }
        return result;
    }

    private static List<object> ReadM3DrivingDimensions(List<M3PrimitiveState> primitives)
    {
        List<object> records = new List<object>();
        foreach (M3PrimitiveState primitive in primitives)
        {
            foreach (M3DimensionState state in primitive.Dimensions)
            {
                SketchDimensionalConstraint dimension = state.Dimension;
                Expression expression = dimension.AssociatedExpression;
                Sketch.DimensionGeometry[] operands = dimension.GetDimensionGeometry();
                if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "M3 dimension geometry returned null");
                List<string> geometryRefs = new List<string>();
                foreach (Sketch.DimensionGeometry operand in operands) geometryRefs.Add(DimensionReference(operand));
                geometryRefs.Sort(StringComparer.Ordinal);
                Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
                record.Add("constraint_id", RequiredJournalId(dimension, "M3 Driving constraint"));
                record.Add("geometry_id", state.GeometryId);
                record.Add("role", state.Role);
                record.Add("constraint_type", dimension.ConstraintType.ToString());
                record.Add("value", expression.Value);
                record.Add("unit", expression.Units == null ? null : expression.Units.Symbol);
                record.Add("expression_name", expression.Name);
                record.Add("dimension_state", dimension.DimensionState.ToString());
                record.Add("is_driving", dimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving);
                record.Add("geometry_refs", ObjectStrings(geometryRefs));
                records.Add(record);
            }
        }
        return records;
    }

    private static Dictionary<string, object> FindSketchRecord(Dictionary<string, object> snapshot, string sketchId)
    {
        List<object> sketches = SnapshotList(snapshot, "sketches");
        Dictionary<string, object> found = null;
        int matches = 0;
        foreach (object value in sketches)
        {
            Dictionary<string, object> sketch = value as Dictionary<string, object>;
            if (sketch != null && String.Equals(Convert.ToString(sketch["journal_id"], CultureInfo.InvariantCulture), sketchId, StringComparison.Ordinal))
            {
                found = sketch;
                matches++;
            }
        }
        if (matches != 1 || found == null)
            throw new M2ModelException(matches == 0 ? "SKETCH_NOT_FOUND" : "AMBIGUOUS_SKETCH", "Sketch record is not unique in the current Work Part snapshot");
        return found;
    }

    private static bool FeaturePresent(Part part, string featureId)
    {
        if (part == null || String.IsNullOrWhiteSpace(featureId)) return false;
        Feature[] features = part.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature history returned null");
        int matches = 0;
        foreach (Feature feature in features)
            if (feature != null && String.Equals(RequiredJournalId(feature, "feature"), featureId, StringComparison.Ordinal)) matches++;
        return matches == 1;
    }

    private static Feature FindFeature(Part part, string featureId)
    {
        if (part == null || String.IsNullOrWhiteSpace(featureId)) throw new M2ModelException("FEATURE_NOT_FOUND", "feature_id is required");
        Feature[] features = part.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature history returned null");
        Feature found = null;
        int matches = 0;
        foreach (Feature feature in features)
        {
            if (feature != null && String.Equals(RequiredJournalId(feature, "feature"), featureId, StringComparison.Ordinal))
            {
                found = feature;
                matches++;
            }
        }
        if (matches != 1 || found == null)
            throw new M2ModelException(matches == 0 ? "FEATURE_NOT_FOUND" : "AMBIGUOUS_FEATURE", "feature_id must identify exactly one feature");
        return found;
    }

    private static bool M3ExtrudeDefinitionMatches(M3PlanState plan, Feature feature,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (plan == null || plan.ExtrudeRequest == null || feature == null)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "retained Extrude request or feature is unavailable");
            return false;
        }
        ExtrudeBuilder builder = feature.OwningPart.Features.CreateExtrudeBuilder(feature);
        try
        {
            M3ExtrudeRequest request = plan.ExtrudeRequest;
            Part work = feature.OwningPart as Part;
            Dictionary<string, object> sectionEvidence = V2SketchProfileAnalyzer.AnalyzeF12SectionDomain(
                work, plan.Sketch, builder.Section);
            string expectedInputFingerprint = GetProfileFingerprint(plan.ProfileAnalysis, "input_geometry_domain_fingerprint");
            string expectedOutputFingerprint = GetProfileFingerprint(plan.ProfileAnalysis, "section_output_domain_fingerprint");
            string actualInputFingerprint = GetProfileFingerprint(sectionEvidence, "input_geometry_domain_fingerprint");
            string actualOutputFingerprint = GetProfileFingerprint(sectionEvidence, "section_output_domain_fingerprint");
            bool sectionMatches = F12SectionAnalysisVerified(sectionEvidence) &&
                String.Equals(expectedInputFingerprint, actualInputFingerprint, StringComparison.Ordinal) &&
                String.Equals(expectedOutputFingerprint, actualOutputFingerprint, StringComparison.Ordinal);
            Direction direction = builder.Direction;
            if (direction == null) throw new M2ModelException("M3_EXTRUDE_READBACK_FAILED", "NX returned no Extrude direction");
            Vector3d actualDirection = direction.Vector;
            Matrix3x3 axes = plan.Sketch.Orientation.Element;
            double sign = request.Direction == "positive" ? 1.0 : -1.0;
            Vector3d expectedDirection = new Vector3d(axes.Zx * sign, axes.Zy * sign, axes.Zz * sign);
            bool directionMatches = NearlyEqual(actualDirection.X, expectedDirection.X) &&
                NearlyEqual(actualDirection.Y, expectedDirection.Y) && NearlyEqual(actualDirection.Z, expectedDirection.Z);

            NXOpen.GeometricUtilities.Extend start = builder.Limits.StartExtend;
            NXOpen.GeometricUtilities.Extend end = builder.Limits.EndExtend;
            double startValue = start == null || start.Value == null ? Double.NaN : start.Value.Value;
            bool startMatches = start != null && start.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.Value &&
                NearlyEqual(startValue, request.StartOffsetMm);
            double endValue = end == null || end.Value == null ? Double.NaN : end.Value.Value;
            bool extentMatches = request.Extent == "finite"
                ? end != null && end.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.Value && NearlyEqual(endValue, request.EndOffsetMm)
                : end != null && end.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.ThroughAll;

            NXOpen.GeometricUtilities.MultiDraft draft = builder.Draft;
            NXOpen.GeometricUtilities.FeatureOffset offset = builder.Offset;
            string actualDraftOption = draft == null ? null : draft.DraftOption.ToString();
            string actualOffsetOption = offset == null ? null : offset.Option.ToString();
            bool draftOptionMatches = String.Equals(actualDraftOption, "NoDraft", StringComparison.Ordinal);
            bool offsetOptionMatches = String.Equals(actualOffsetOption, "NoOffset", StringComparison.Ordinal);
            bool dormantParametersMatch = draft != null && offset != null &&
                F12ParameterExpressionMatches(draft.DraftAngle, 0.0, "deg") &&
                F12ParameterExpressionMatches(draft.FrontDraftAngle, 0.0, "deg") &&
                F12ParameterExpressionMatches(draft.BackDraftAngle, 0.0, "deg") &&
                F12ParameterExpressionMatches(offset.StartOffset, 0.0, "mm") &&
                F12ParameterExpressionMatches(offset.EndOffset, 0.0, "mm");

            string expectedBodyType = request.ProfileOutput == "sheet" ? "Sheet" : "Solid";
            string actualBodyType = builder.FeatureOptions == null ? null : builder.FeatureOptions.BodyType.ToString();
            bool bodyTypeMatches = String.Equals(expectedBodyType, actualBodyType, StringComparison.Ordinal);

            string expectedBoolean = request.Mode == "create" ? "Create" : (request.Mode == "unite" ? "Unite" : "Subtract");
            string actualBoolean = builder.BooleanOperation == null ? null : builder.BooleanOperation.Type.ToString();
            bool booleanMatches = String.Equals(expectedBoolean, actualBoolean, StringComparison.Ordinal);
            Body[] targetBodies = builder.BooleanOperation.GetTargetBodies();
            bool targetsMatch;
            List<object> targetIds = new List<object>();
            if (targetBodies != null)
            {
                foreach (Body target in targetBodies)
                {
                    if (target == null) throw new M2ModelException("M3_EXTRUDE_READBACK_FAILED", "NX returned a null boolean target");
                    targetIds.Add(RequiredJournalId(target, "Extrude target body"));
                }
            }
            if (request.Mode == "create") targetsMatch = targetIds.Count == 0;
            else targetsMatch = targetBodies != null && targetBodies.Length == 1 &&
                targetBodies[0].Tag.Equals(plan.TargetBody.Tag) &&
                String.Equals(RequiredJournalId(targetBodies[0], "Extrude target body"), plan.TargetBodyId, StringComparison.Ordinal);

            Feature[] parents = feature.GetParents();
            if (parents == null) throw new M2ModelException("MODEL_READ_FAILED", "Extrude parent feature list returned null");
            List<object> parentIds = new List<object>();
            bool sketchParent = false;
            foreach (Feature parent in parents)
            {
                if (parent == null) throw new M2ModelException("MODEL_READ_FAILED", "Extrude parent feature list contains null");
                string parentId = RequiredJournalId(parent, "Extrude parent feature");
                parentIds.Add(parentId);
                if (plan.Sketch != null && plan.Sketch.Feature != null && parent.Tag.Equals(plan.Sketch.Feature.Tag)) sketchParent = true;
            }
            bool matched = sectionMatches && bodyTypeMatches && directionMatches && startMatches && extentMatches &&
                draftOptionMatches && offsetOptionMatches && booleanMatches && targetsMatch && sketchParent;
            evidence.Add("status", matched ? "VERIFIED" : "MISMATCH");
            evidence.Add("feature_id", RequiredJournalId(feature, "Extrude feature"));
            evidence.Add("section_domain", sectionEvidence);
            evidence.Add("section_domain_matches_prepare", sectionMatches);
            evidence.Add("expected_input_domain_fingerprint", expectedInputFingerprint);
            evidence.Add("actual_input_domain_fingerprint", actualInputFingerprint);
            evidence.Add("expected_output_domain_fingerprint", expectedOutputFingerprint);
            evidence.Add("actual_output_domain_fingerprint", actualOutputFingerprint);
            evidence.Add("profile_output", request.ProfileOutput);
            evidence.Add("expected_body_type", expectedBodyType);
            evidence.Add("actual_body_type", actualBodyType);
            evidence.Add("body_type_matches", bodyTypeMatches);
            evidence.Add("expected_direction", new List<object> { expectedDirection.X, expectedDirection.Y, expectedDirection.Z });
            evidence.Add("actual_direction", new List<object> { actualDirection.X, actualDirection.Y, actualDirection.Z });
            evidence.Add("direction_matches", directionMatches);
            evidence.Add("start_trim_type", start == null ? null : start.TrimType.ToString());
            evidence.Add("start_value_mm", startValue);
            evidence.Add("start_matches", startMatches);
            evidence.Add("extent", request.Extent);
            evidence.Add("end_trim_type", end == null ? null : end.TrimType.ToString());
            evidence.Add("expected_start_offset_mm", request.StartOffsetMm);
            evidence.Add("expected_end_offset_mm", request.Extent == "finite" ? (object)request.EndOffsetMm : null);
            evidence.Add("actual_end_offset_mm", request.Extent == "finite" ? (object)endValue : null);
            evidence.Add("span_mm", request.Extent == "finite" ? (object)(request.EndOffsetMm - request.StartOffsetMm) : null);
            evidence.Add("extent_matches", extentMatches);
            evidence.Add("draft_option", actualDraftOption);
            evidence.Add("draft_option_matches_no_draft", draftOptionMatches);
            evidence.Add("offset_option", actualOffsetOption);
            evidence.Add("offset_option_matches_no_offset", offsetOptionMatches);
            evidence.Add("dormant_draft_and_offset_parameters_are_zero_after_reconstruction", dormantParametersMatch);
            evidence.Add("dormant_parameter_readback_is_diagnostic_not_feature_membership", true);
            evidence.Add("draft_angle_rhs", draft == null || draft.DraftAngle == null ? null : draft.DraftAngle.RightHandSide);
            evidence.Add("front_draft_angle_rhs", draft == null || draft.FrontDraftAngle == null ? null : draft.FrontDraftAngle.RightHandSide);
            evidence.Add("back_draft_angle_rhs", draft == null || draft.BackDraftAngle == null ? null : draft.BackDraftAngle.RightHandSide);
            evidence.Add("offset_start_rhs", offset == null || offset.StartOffset == null ? null : offset.StartOffset.RightHandSide);
            evidence.Add("offset_end_rhs", offset == null || offset.EndOffset == null ? null : offset.EndOffset.RightHandSide);
            evidence.Add("expected_boolean_type", expectedBoolean);
            evidence.Add("actual_boolean_type", actualBoolean);
            evidence.Add("boolean_matches", booleanMatches);
            evidence.Add("target_body_ids", targetIds);
            evidence.Add("expected_target_body_id", plan.TargetBodyId);
            evidence.Add("targets_match", targetsMatch);
            evidence.Add("source_sketch_feature_id", plan.Sketch == null || plan.Sketch.Feature == null ? null : RequiredJournalId(plan.Sketch.Feature, "source sketch feature"));
            evidence.Add("parent_feature_ids", parentIds);
            evidence.Add("source_sketch_parent", sketchParent);
            return matched;
        }
        catch (V2SketchProfileAnalyzer.SectionCleanupException ex)
        {
            throw M3AnalysisStateUnknown("NX Extrude Section verification could not prove temporary Section cleanup restored the Work Part", ex);
        }
        finally
        {
            try { builder.Destroy(); }
            catch (Exception ex)
            {
                throw M3AnalysisStateUnknown("NX Extrude readback builder cleanup could not be verified", ex);
            }
        }
    }

    private static List<object> FeatureIds(Dictionary<string, object> snapshot)
    {
        List<object> features = SnapshotList(snapshot, "features");
        List<string> ids = new List<string>();
        foreach (object value in features)
        {
            Dictionary<string, object> feature = value as Dictionary<string, object>;
            if (feature == null || !(feature["journal_id"] is string))
                throw new M2ModelException("MODEL_READ_FAILED", "feature history contains a record without journal_id");
            ids.Add((string)feature["journal_id"]);
        }
        ids.Sort(StringComparer.Ordinal);
        return ObjectStrings(ids);
    }

    private static bool M3ProfileAnalysisSuccessful(Dictionary<string, object> analysis)
    {
        if (analysis == null) return false;
        object rawStatus;
        if (!analysis.TryGetValue("status", out rawStatus)) return false;
        string status = Convert.ToString(rawStatus, CultureInfo.InvariantCulture);
        if (status != "EMPTY" && status != "OPEN_GEOMETRY" && status != "VALID_CLOSED_PROFILES") return false;
        if (status == "EMPTY") return true;
        object intersections;
        if (!analysis.TryGetValue("native_intersection_check", out intersections)) return false;
        string intersectionStatus = Convert.ToString(intersections, CultureInfo.InvariantCulture);
        if (intersectionStatus != "VERIFIED" && intersectionStatus != "NOT_REQUIRED") return false;
        object splineChecks;
        if (!analysis.TryGetValue("spline_self_intersection_check", out splineChecks)) return false;
        string splineStatus = Convert.ToString(splineChecks, CultureInfo.InvariantCulture);
        if (splineStatus != "VERIFIED" && splineStatus != "NOT_APPLICABLE") return false;
        return true;
    }

    private bool VerifyM3ProfileAnalysis(Part work, Sketch sketch,
        Dictionary<string, object> expected, out Dictionary<string, object> actual,
        out Dictionary<string, object> cleanupEvidence)
    {
        actual = null;
        cleanupEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (work == null || sketch == null || expected == null)
        {
            cleanupEvidence.Add("reason", "execute profile analysis or current Sketch is unavailable");
            return false;
        }
        bool modifiedBefore = work.IsModified;
        string geometryBefore = CaptureFullGeometry(work);
        string analysisError = null;
        try { actual = V2SketchProfileAnalyzer.Analyze(work, sketch, true); }
        catch (V2SketchProfileAnalyzer.SectionCleanupException ex)
        {
            cleanupEvidence.Add("analysis_error", ex.GetType().Name);
            cleanupEvidence.Add("analysis_message", ex.Message);
            cleanupEvidence.Add("part_modified_before", modifiedBefore);
            cleanupEvidence.Add("temporary_section_state_restored", false);
            throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN",
                "NX profile verification could not prove temporary Section cleanup restored the Work Part; writes must remain frozen.");
        }
        catch (Exception ex) { analysisError = ex.GetType().Name; }
        bool modifiedAfter;
        string geometryAfter;
        try
        {
            modifiedAfter = work.IsModified;
            geometryAfter = CaptureFullGeometry(work);
        }
        catch (Exception ex)
        {
            cleanupEvidence.Add("post_analysis_state_audit_error", ex.GetType().Name);
            cleanupEvidence.Add("part_modified_before", modifiedBefore);
            cleanupEvidence.Add("temporary_section_state_restored", false);
            throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN",
                "NX profile verification could not read the Work Part after temporary Section analysis; writes must remain frozen.");
        }
        bool modelStateRestored = modifiedBefore == modifiedAfter &&
            String.Equals(geometryBefore, geometryAfter, StringComparison.Ordinal);
        if (!modelStateRestored)
        {
            cleanupEvidence.Add("part_modified_before", modifiedBefore);
            cleanupEvidence.Add("part_modified_after", modifiedAfter);
            cleanupEvidence.Add("live_geometry_unchanged", String.Equals(geometryBefore, geometryAfter, StringComparison.Ordinal));
            cleanupEvidence.Add("temporary_section_state_restored", false);
            throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN",
                "NX temporary Section analysis changed Work Part dirty state or geometry; writes must remain frozen.");
        }
        bool analysisMatches = M3ProfileAnalysisSuccessful(actual) &&
            ProfileAnalysisCoreMatches(expected, actual);
        bool verified = analysisError == null && analysisMatches;
        cleanupEvidence.Add("analysis_error", analysisError);
        cleanupEvidence.Add("analysis_matches_execute", analysisMatches);
        cleanupEvidence.Add("part_modified_before", modifiedBefore);
        cleanupEvidence.Add("part_modified_after", modifiedAfter);
        cleanupEvidence.Add("live_geometry_unchanged", String.Equals(geometryBefore, geometryAfter, StringComparison.Ordinal));
        cleanupEvidence.Add("temporary_section_state_restored", modelStateRestored);
        return verified;
    }

    private static bool ProfileAnalysisCoreMatches(Dictionary<string, object> expected,
        Dictionary<string, object> actual)
    {
        if (expected == null || actual == null) return false;
        string[] stringFields = new string[] {
            "status", "native_intersection_check", "spline_self_intersection_check"
        };
        foreach (string field in stringFields)
        {
            object left, right;
            if (!expected.TryGetValue(field, out left) || !actual.TryGetValue(field, out right) ||
                !String.Equals(Convert.ToString(left, CultureInfo.InvariantCulture),
                    Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal)) return false;
        }
        string[] countFields = new string[] {
            "curve_count", "open_chain_count", "closed_loop_count", "outer_loop_count", "inner_loop_count", "section_loop_count"
        };
        foreach (string field in countFields)
        {
            object left, right;
            int leftCount, rightCount;
            if (!expected.TryGetValue(field, out left) || !actual.TryGetValue(field, out right) ||
                !Int32.TryParse(Convert.ToString(left, CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out leftCount) ||
                !Int32.TryParse(Convert.ToString(right, CultureInfo.InvariantCulture), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out rightCount) || leftCount != rightCount) return false;
        }
        return true;
    }

    private static Dictionary<string, object> SplineDefinitionRecord(Spline spline)
    {
        if (spline == null) throw new M2ModelException("M3_SKETCH_READBACK_FAILED", "native spline object is unavailable");
        UFCurve.Spline native;
        UFSession.GetUFSession().Curve.AskSplineData(spline.Tag, out native);
        if (native.poles == null || native.knots == null || native.num_poles <= 0 || native.order <= 0)
            throw new M2ModelException("M3_SKETCH_READBACK_FAILED", "native spline definition is incomplete");
        List<object> poles = new List<object>();
        List<object> weights = new List<object>();
        for (int i = 0; i < native.poles.GetLength(0); i++)
        {
            List<object> pole = new List<object>();
            for (int j = 0; j < native.poles.GetLength(1); j++) pole.Add(NumberText(native.poles[i, j]));
            poles.Add(pole);
            if (native.poles.GetLength(1) > 3) weights.Add(NumberText(native.poles[i, 3]));
        }
        Dictionary<string, object> definition = new Dictionary<string, object>(StringComparer.Ordinal);
        definition.Add("degree", native.order - 1);
        definition.Add("order", native.order);
        definition.Add("pole_count", native.num_poles);
        definition.Add("rational", native.is_rational != 0);
        definition.Add("knots", NumberTextList(native.knots));
        definition.Add("poles", poles);
        definition.Add("weights", weights);
        definition.Add("start_parameter", native.start_param);
        definition.Add("end_parameter", native.end_param);
        return definition;
    }

    private static List<object> SplineInterpolationConditionRecords(UFCurve.PtSlopeCrvatr[] points)
    {
        if (points == null) throw new M2ModelException("MODEL_READ_FAILED", "spline interpolation condition array is null");
        List<object> result = new List<object>();
        for (int i = 0; i < points.Length; i++)
        {
            UFCurve.PtSlopeCrvatr point = points[i];
            if (point.slope == null || point.slope.Length != 3 || point.crvatr == null || point.crvatr.Length != 3)
                throw new M2ModelException("MODEL_READ_FAILED", "spline interpolation condition vector is incomplete");
            result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                { "point_index", i },
                { "slope_type", point.slope_type },
                { "slope", NumberTextList(point.slope) },
                { "crvatr_type", point.crvatr_type },
                { "crvatr", NumberTextList(point.crvatr) }
            });
        }
        return result;
    }

    private static bool NativeSplineConditionsAreUnconstrained(UFCurve.PtSlopeCrvatr[] points)
    {
        if (points == null || points.Length < 4) return false;
        for (int i = 0; i < points.Length; i++)
        {
            UFCurve.PtSlopeCrvatr point = points[i];
            bool endpoint = i == 0 || i == points.Length - 1;
            bool slopeIsUnconstrained = point.slope_type == -1 || (endpoint && point.slope_type == 0);
            if (!slopeIsUnconstrained || point.crvatr_type != -1 ||
                point.slope == null || point.slope.Length != 3 || point.crvatr == null || point.crvatr.Length != 3)
                return false;
            for (int coordinate = 0; coordinate < 3; coordinate++)
                if (Double.IsNaN(point.slope[coordinate]) || Double.IsInfinity(point.slope[coordinate]) ||
                    Double.IsNaN(point.crvatr[coordinate]) || Double.IsInfinity(point.crvatr[coordinate]))
                    return false;
        }
        return true;
    }

    private static bool M3SketchUsesLegacyGeometry(M3PlanState plan)
    {
        if (plan == null || plan.SketchRequest == null || plan.SketchRequest.Geometries == null ||
            plan.SketchRequest.ConstraintMode == "geometry_only") return false;
        foreach (M3SketchGeometryRequest geometry in plan.SketchRequest.Geometries)
            if (geometry.Type != "line" && geometry.Type != "rectangle" && geometry.Type != "circle") return false;
        return true;
    }

    private static bool M3SketchConstraintStatusAcceptable(M3PlanState plan, Sketch.Status status,
        int degreesOfFreedom, int constraintCount)
    {
        if (plan != null && plan.SketchRequest != null && plan.SketchRequest.ConstraintMode == "geometry_only")
            return status == Sketch.Status.UnderConstrained && degreesOfFreedom > 0 && constraintCount == 0;
        if (M3SketchUsesLegacyGeometry(plan))
            return status == Sketch.Status.WellConstrained && degreesOfFreedom == 0;
        return status == Sketch.Status.UnderConstrained ||
            (status == Sketch.Status.WellConstrained && degreesOfFreedom == 0);
    }

    private static double[] M3EllipseLocalPoint(double centerX, double centerY, double majorRadius,
        double minorRadius, double rotationDeg, double parameter)
    {
        double rotation = rotationDeg * Math.PI / 180.0;
        double x = majorRadius * Math.Cos(parameter);
        double y = minorRadius * Math.Sin(parameter);
        double cosine = Math.Cos(rotation), sine = Math.Sin(rotation);
        return new double[] { centerX + x * cosine - y * sine, centerY + x * sine + y * cosine };
    }

    private static double[] M3LocalEndpoints(M3SketchGeometryRequest geometry)
    {
        if (geometry.Type == "arc" || geometry.Type == "elliptical_arc")
        {
            double start = geometry.StartAngleDeg * Math.PI / 180.0;
            double end = (geometry.StartAngleDeg + geometry.SweepDeg) * Math.PI / 180.0;
            double radiusX = geometry.Type == "arc" ? geometry.RadiusMm : geometry.MajorRadiusMm;
            double radiusY = geometry.Type == "arc" ? geometry.RadiusMm : geometry.MinorRadiusMm;
            double rotation = geometry.Type == "arc" ? 0.0 : geometry.RotationDeg * Math.PI / 180.0;
            double cosine = Math.Cos(rotation), sine = Math.Sin(rotation);
            double startX = radiusX * Math.Cos(start), startY = radiusY * Math.Sin(start);
            double endX = radiusX * Math.Cos(end), endY = radiusY * Math.Sin(end);
            return new double[] {
                geometry.CenterMm[0] + startX * cosine - startY * sine,
                geometry.CenterMm[1] + startX * sine + startY * cosine,
                geometry.CenterMm[0] + endX * cosine - endY * sine,
                geometry.CenterMm[1] + endX * sine + endY * cosine
            };
        }
        if (geometry.Type == "spline")
        {
            double[] start = geometry.InterpolationPointsMm[0];
            double[] end = geometry.InterpolationPointsMm[geometry.InterpolationPointsMm.Length - 1];
            return new double[] { start[0], start[1], end[0], end[1] };
        }
        throw new M2ModelException("UNSUPPORTED_GEOMETRY", "geometry type has no open-curve endpoints");
    }

    private bool SketchGeometryMatches(M3PlanState plan, Sketch sketch, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (sketch == null)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "Sketch is unavailable");
            return false;
        }
        if (plan.Kind == "create_extrude" || plan.Kind == "create_revolve")
        {
            Dictionary<string, object> current = BuildSketchDetail(sketch.OwningPart as Part, sketch, sketch.OwningPart.PartUnits.ToString());
            Dictionary<string, object> expectedProjection = M3SourceSketchProjection(plan.SketchRecordBefore);
            Dictionary<string, object> actualProjection = M3SourceSketchProjection(current);
            bool same = plan.SketchRecordBefore != null &&
                ProjectionComplete(expectedProjection) && ProjectionComplete(actualProjection) &&
                SemanticValuesEqual(expectedProjection, actualProjection, "source_sketch");
            evidence.Add("status", same ? "VERIFIED" : "MISMATCH");
            evidence.Add("expected_source_sketch_projection", expectedProjection);
            evidence.Add("actual_source_sketch_projection", actualProjection);
            return same;
        }

        bool matches = true;
        List<object> primitiveEvidence = new List<object>();
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null during M3 verification");
        int expectedGeometryCount = 0;
        foreach (M3PrimitiveState primitive in plan.Primitives)
            expectedGeometryCount += primitive.Curves == null ? 0 : primitive.Curves.Length;
        bool geometryCountMatches = geometry.Length == expectedGeometryCount;
        matches = geometryCountMatches;
        Dictionary<string, NXObject> objects = new Dictionary<string, NXObject>(StringComparer.Ordinal);
        foreach (NXObject item in geometry)
            if (item != null) objects[RequiredJournalId(item, "M3 sketch geometry")] = item;
        foreach (M3PrimitiveState primitive in plan.Primitives)
        {
            Dictionary<string, object> itemEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            itemEvidence.Add("geometry_id", primitive.RequestId);
            itemEvidence.Add("type", primitive.Type);
            bool itemMatches = true;
            if (primitive.Type == "line")
            {
                Line expected = primitive.Curves[0] as Line;
                Line actual;
                string id = RequiredJournalId(expected, "M3 line");
                NXObject found;
                if (!objects.TryGetValue(id, out found) || (actual = found as Line) == null)
                    itemMatches = false;
                else
                {
                    double[] p = primitive.LocalPoints;
                    Point3d expectedA = SketchPoint(sketch, p[0], p[1]);
                    Point3d expectedB = SketchPoint(sketch, p[2], p[3]);
                    itemMatches = EndpointsMatch(actual.StartPoint, actual.EndPoint, expectedA, expectedB);
                    itemEvidence.Add("geometry_journal_id", id);
                    itemEvidence.Add("start", Point(actual.StartPoint));
                    itemEvidence.Add("end", Point(actual.EndPoint));
                    itemEvidence.Add("expected_start", Point(expectedA));
                    itemEvidence.Add("expected_end", Point(expectedB));
                }
            }
            else if (primitive.Type == "rectangle")
            {
                double[] p = primitive.LocalPoints;
                double x = p[0], y = p[1], width = primitive.Width, height = primitive.Height;
                Point3d[] corners = new Point3d[] {
                    SketchPoint(sketch, x, y), SketchPoint(sketch, x + width, y),
                    SketchPoint(sketch, x + width, y + height), SketchPoint(sketch, x, y + height)
                };
                HashSet<string> actualIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < primitive.Curves.Length; i++)
                {
                    Line expectedLine = primitive.Curves[i] as Line;
                    string id = RequiredJournalId(expectedLine, "M3 rectangle line");
                    NXObject found;
                    Line actualLine = objects.TryGetValue(id, out found) ? found as Line : null;
                    if (actualLine == null) { itemMatches = false; break; }
                    actualIds.Add(id);
                    int edge = -1;
                    for (int j = 0; j < 4; j++)
                        if (EndpointsMatch(actualLine.StartPoint, actualLine.EndPoint, corners[j], corners[(j + 1) % 4])) { edge = j; break; }
                    if (edge < 0) itemMatches = false;
                }
                itemEvidence.Add("expected_corners", new List<object> { Point(corners[0]), Point(corners[1]), Point(corners[2]), Point(corners[3]) });
                itemEvidence.Add("line_ids", ObjectStrings(new List<string>(actualIds)));
            }
            else if (primitive.Type == "circle")
            {
                Arc expected = primitive.Curves[0] as Arc;
                string id = RequiredJournalId(expected, "M3 circle");
                NXObject found;
                Arc actual = objects.TryGetValue(id, out found) ? found as Arc : null;
                double[] p = primitive.LocalPoints;
                Point3d expectedCenter = SketchPoint(sketch, p[0], p[1]);
                if (actual == null) itemMatches = false;
                else
                {
                    double sweep = Math.Abs(actual.EndAngle - actual.StartAngle);
                    itemMatches = PointNearlyEqual(actual.CenterPoint, expectedCenter) &&
                        NearlyEqual(actual.Radius * 2.0, primitive.Diameter) && NearlyEqual(sweep, 2.0 * Math.PI);
                    Point3d arcCenter; Vector3d arcX, arcY;
                    actual.GetOrientation(out arcCenter, out arcX, out arcY);
                    itemEvidence.Add("geometry_journal_id", id);
                    itemEvidence.Add("center", Point(actual.CenterPoint));
                    itemEvidence.Add("expected_center", Point(expectedCenter));
                    itemEvidence.Add("radius_mm", actual.Radius);
                    itemEvidence.Add("diameter_mm", actual.Radius * 2.0);
                    itemEvidence.Add("expected_diameter_mm", primitive.Diameter);
                    itemEvidence.Add("sweep_radians", sweep);
                    itemEvidence.Add("start", Point(ConicPoint(arcCenter, arcX, arcY, actual.Radius, actual.Radius, 0.0, actual.StartAngle)));
                    itemEvidence.Add("end", Point(ConicPoint(arcCenter, arcX, arcY, actual.Radius, actual.Radius, 0.0, actual.EndAngle)));
                }
            }
            else if (primitive.Type == "arc")
            {
                string id = RequiredJournalId(primitive.Curves[0], "M3 arc");
                NXObject found;
                Arc actual = objects.TryGetValue(id, out found) ? found as Arc : null;
                if (actual == null) itemMatches = false;
                else
                {
                    Point3d center; Vector3d xAxis, yAxis;
                    actual.GetOrientation(out center, out xAxis, out yAxis);
                    Point3d expectedCenter = SketchPoint(sketch, primitive.CenterMm[0], primitive.CenterMm[1]);
                    Point3d actualStart = ConicPoint(center, xAxis, yAxis, actual.Radius, actual.Radius, 0.0, actual.StartAngle);
                    Point3d actualEnd = ConicPoint(center, xAxis, yAxis, actual.Radius, actual.Radius, 0.0, actual.EndAngle);
                    double length = actual.GetLength();
                    double expectedLength = primitive.Radius * Math.Abs(primitive.SweepDeg) * Math.PI / 180.0;
                    Point3d actualMidpoint = ConicPoint(center, xAxis, yAxis, actual.Radius, actual.Radius, 0.0,
                        (actual.StartAngle + actual.EndAngle) / 2.0);
                    double[] expectedMidLocal = M3EllipseLocalPoint(primitive.CenterMm[0], primitive.CenterMm[1],
                        primitive.Radius, primitive.Radius, 0.0,
                        (primitive.StartAngleDeg + primitive.SweepDeg / 2.0) * Math.PI / 180.0);
                    Point3d expectedMidpoint = SketchPoint(sketch, expectedMidLocal[0], expectedMidLocal[1]);
                    bool midpointMatch = PointNearlyEqual(actualMidpoint, expectedMidpoint);
                    double[] normal = Cross3(new double[] { xAxis.X, xAxis.Y, xAxis.Z }, new double[] { yAxis.X, yAxis.Y, yAxis.Z });
                    Matrix3x3 axes = sketch.Orientation.Element;
                    double[] expectedNormal = new double[] { axes.Zx, axes.Zy, axes.Zz };
                    if (primitive.SweepDeg < 0.0)
                        expectedNormal = new double[] { -expectedNormal[0], -expectedNormal[1], -expectedNormal[2] };
                    double[] expectedEndpoints = primitive.LocalEndpointPairs[0];
                    Point3d expectedStart = SketchPoint(sketch, expectedEndpoints[0], expectedEndpoints[1]);
                    Point3d expectedEnd = SketchPoint(sketch, expectedEndpoints[2], expectedEndpoints[3]);
                    bool endpointMatch = EndpointsMatch(actualStart, actualEnd, expectedStart, expectedEnd);
                    itemMatches = PointNearlyEqual(center, expectedCenter) && NearlyEqual(actual.Radius, primitive.Radius) &&
                        endpointMatch && midpointMatch && NearlyEqual(length, expectedLength) &&
                        VectorNearArray(normal, expectedNormal, M3VectorTolerance);
                    itemEvidence.Add("geometry_journal_id", id);
                    itemEvidence.Add("center", Point(center));
                    itemEvidence.Add("expected_center", Point(expectedCenter));
                    itemEvidence.Add("radius_mm", actual.Radius);
                    itemEvidence.Add("expected_radius_mm", primitive.Radius);
                    itemEvidence.Add("start_angle_rad", actual.StartAngle);
                    itemEvidence.Add("end_angle_rad", actual.EndAngle);
                    itemEvidence.Add("start", Point(actualStart));
                    itemEvidence.Add("end", Point(actualEnd));
                    itemEvidence.Add("expected_start", Point(expectedStart));
                    itemEvidence.Add("expected_end", Point(expectedEnd));
                    itemEvidence.Add("midpoint", Point(actualMidpoint));
                    itemEvidence.Add("expected_midpoint", Point(expectedMidpoint));
                    itemEvidence.Add("length_mm", length);
                    itemEvidence.Add("expected_length_mm", expectedLength);
                    itemEvidence.Add("normal", NumberList(normal));
                    itemEvidence.Add("endpoint_match", endpointMatch);
                    itemEvidence.Add("midpoint_matches", midpointMatch);
                }
            }
            else if (primitive.Type == "ellipse" || primitive.Type == "elliptical_arc")
            {
                string id = RequiredJournalId(primitive.Curves[0], "M3 ellipse");
                NXObject found;
                Ellipse actual = objects.TryGetValue(id, out found) ? found as Ellipse : null;
                if (actual == null) itemMatches = false;
                else
                {
                    double major, minor, start, end, rotation;
                    Point3d center;
                    actual.GetParameters(out major, out minor, out start, out end, out rotation, out center);
                    Point3d frameCenter; Vector3d xAxis, yAxis;
                    actual.GetOrientation(out frameCenter, out xAxis, out yAxis);
                    double[] parameterLimits = NativeCurveParameterLimits(actual);
                    double actualParameterStart = parameterLimits[0];
                    double actualParameterEnd = parameterLimits[1];
                    Point3d actualStart = NativeCurvePoint(actual, actualParameterStart);
                    Point3d actualEnd = NativeCurvePoint(actual, actualParameterEnd);
                    double[] expectedEndpoints = primitive.Type == "ellipse"
                        ? null : primitive.LocalEndpointPairs[0];
                    Point3d expectedStart = expectedEndpoints == null ? actualStart :
                        SketchPoint(sketch, expectedEndpoints[0], expectedEndpoints[1]);
                    Point3d expectedEnd = expectedEndpoints == null ? actualEnd :
                        SketchPoint(sketch, expectedEndpoints[2], expectedEndpoints[3]);
                    double expectedStartAngle = primitive.Type == "ellipse" ? 0.0 : primitive.StartAngleDeg * Math.PI / 180.0;
                    double expectedSweep = primitive.Type == "ellipse" ? 2.0 * Math.PI : primitive.SweepDeg * Math.PI / 180.0;
                    double expectedLength = EllipseArcLength(primitive.MajorRadius, primitive.MinorRadius,
                        expectedStartAngle, expectedSweep);
                    double actualLength = actual.GetLength();
                    double actualParameterSpan = Math.Abs(actualParameterEnd - actualParameterStart);
                    double expectedParameterSpan = Math.Abs(expectedSweep);
                    double actualMidParameter = (actualParameterStart + actualParameterEnd) / 2.0;
                    Point3d actualMidpoint = NativeCurvePoint(actual, actualMidParameter);
                    double[] expectedMidLocal = M3EllipseLocalPoint(primitive.CenterMm[0], primitive.CenterMm[1],
                        primitive.MajorRadius, primitive.MinorRadius, primitive.RotationDeg,
                        expectedStartAngle + expectedSweep / 2.0);
                    Point3d expectedMidpoint = SketchPoint(sketch, expectedMidLocal[0], expectedMidLocal[1]);
                    bool midpointMatch = PointNearlyEqual(actualMidpoint, expectedMidpoint);
                    Vector3d expectedMajorAxis = SketchVector(sketch, Math.Cos(primitive.RotationDeg * Math.PI / 180.0),
                        Math.Sin(primitive.RotationDeg * Math.PI / 180.0));
                    Vector3d actualMajorAxis = RotateInPlane(xAxis, yAxis, rotation);
                    double[] actualNormal = Cross3(new double[] { xAxis.X, xAxis.Y, xAxis.Z }, new double[] { yAxis.X, yAxis.Y, yAxis.Z });
                    Matrix3x3 sketchAxes = sketch.Orientation.Element;
                    double[] expectedNormal = new double[] { sketchAxes.Zx, sketchAxes.Zy, sketchAxes.Zz };
                    if (primitive.Type == "elliptical_arc" && primitive.SweepDeg < 0.0)
                        expectedNormal = new double[] { -expectedNormal[0], -expectedNormal[1], -expectedNormal[2] };
                    bool endpointMatch = primitive.Type == "ellipse" || EndpointsMatch(actualStart, actualEnd, expectedStart, expectedEnd);
                    bool parameterSpanMatches = NearlyEqual(actualParameterSpan, expectedParameterSpan);
                    bool lengthDiagnosticMatches = NearlyEqual(actualLength, expectedLength);
                    itemMatches = PointNearlyEqual(center, SketchPoint(sketch, primitive.CenterMm[0], primitive.CenterMm[1])) &&
                        NearlyEqual(major, primitive.MajorRadius) && NearlyEqual(minor, primitive.MinorRadius) &&
                        M3UnitVectorsNearUnoriented(new double[] { actualMajorAxis.X, actualMajorAxis.Y, actualMajorAxis.Z },
                            new double[] { expectedMajorAxis.X, expectedMajorAxis.Y, expectedMajorAxis.Z }, M3VectorTolerance) &&
                        VectorNearArray(actualNormal, expectedNormal, M3VectorTolerance) &&
                        endpointMatch && parameterSpanMatches && midpointMatch;
                    itemEvidence.Add("geometry_journal_id", id);
                    itemEvidence.Add("center", Point(center));
                    itemEvidence.Add("major_radius_mm", major);
                    itemEvidence.Add("minor_radius_mm", minor);
                    itemEvidence.Add("major_axis_direction", Vector(actualMajorAxis.X, actualMajorAxis.Y, actualMajorAxis.Z));
                    itemEvidence.Add("start_angle_rad", actualParameterStart);
                    itemEvidence.Add("end_angle_rad", actualParameterEnd);
                    itemEvidence.Add("native_polar_start_angle_rad", start);
                    itemEvidence.Add("native_polar_end_angle_rad", end);
                    itemEvidence.Add("start", Point(actualStart));
                    itemEvidence.Add("end", Point(actualEnd));
                    itemEvidence.Add("expected_start", Point(expectedStart));
                    itemEvidence.Add("expected_end", Point(expectedEnd));
                    itemEvidence.Add("midpoint", Point(actualMidpoint));
                    itemEvidence.Add("expected_midpoint", Point(expectedMidpoint));
                    itemEvidence.Add("length_mm", actualLength);
                    itemEvidence.Add("expected_length_mm", expectedLength);
                    itemEvidence.Add("endpoint_match", endpointMatch);
                    itemEvidence.Add("parameter_span_rad", actualParameterSpan);
                    itemEvidence.Add("expected_parameter_span_rad", expectedParameterSpan);
                    itemEvidence.Add("parameter_span_matches", parameterSpanMatches);
                    itemEvidence.Add("midpoint_matches", midpointMatch);
                    itemEvidence.Add("length_matches_diagnostic", lengthDiagnosticMatches);
                }
            }
            else if (primitive.Type == "spline")
            {
                string id = RequiredJournalId(primitive.Curves[0], "M3 spline");
                NXObject found;
                Spline actual = objects.TryGetValue(id, out found) ? found as Spline : null;
                if (actual == null) itemMatches = false;
                else
                {
                    int degree, periodic, count;
                    UFCurve.PtSlopeCrvatr[] actualPoints;
                    double[] parameters;
                    UFSession.GetUFSession().Curve.AskSplineThruPts(actual.Tag,
                        out degree, out periodic, out count, out actualPoints, out parameters);
                    double[][] requested = primitive.InterpolationPointsMm;
                    bool pointCountMatch = actualPoints != null && actualPoints.Length == requested.Length && count == requested.Length;
                    bool pointsMatch = pointCountMatch;
                    bool conditionsMatch = NativeSplineConditionsAreUnconstrained(actualPoints);
                    bool evaluationMatches = pointCountMatch && parameters != null && parameters.Length == requested.Length;
                    List<object> actualWorldPoints = new List<object>();
                    if (actualPoints != null)
                    {
                        foreach (UFCurve.PtSlopeCrvatr point in actualPoints)
                        {
                            Point3d world = new Point3d(point.point[0], point.point[1], point.point[2]);
                            actualWorldPoints.Add(Point(world));
                        }
                    }
                    if (pointsMatch)
                        for (int i = 0; i < requested.Length; i++)
                            if (!PointNearlyEqual(new Point3d(actualPoints[i].point[0], actualPoints[i].point[1], actualPoints[i].point[2]),
                                SketchPoint(sketch, requested[i][0], requested[i][1]))) pointsMatch = false;
                    List<object> evaluatedWorldPoints = new List<object>();
                    if (evaluationMatches)
                    {
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            double[] evaluated = new double[3];
                            UFSession.GetUFSession().Curve.EvaluateCurve(actual.Tag, parameters[i], 0, evaluated);
                            Point3d evaluatedPoint = new Point3d(evaluated[0], evaluated[1], evaluated[2]);
                            evaluatedWorldPoints.Add(Point(evaluatedPoint));
                            if (!PointNearlyEqual(evaluatedPoint, SketchPoint(sketch, requested[i][0], requested[i][1])))
                                evaluationMatches = false;
                        }
                    }
                    Dictionary<string, object> actualDefinition = SplineDefinitionRecord(actual);
                    bool definitionMatches = primitive.SplineDefinition != null &&
                        SemanticValuesEqual(primitive.SplineDefinition, actualDefinition, "spline_definition");
                    double[] expectedEndpoints = primitive.LocalEndpointPairs[0];
                    Point3d expectedStart = SketchPoint(sketch, expectedEndpoints[0], expectedEndpoints[1]);
                    Point3d expectedEnd = SketchPoint(sketch, expectedEndpoints[2], expectedEndpoints[3]);
                    Point3d actualStart = actualPoints == null || actualPoints.Length == 0 ? expectedStart :
                        new Point3d(actualPoints[0].point[0], actualPoints[0].point[1], actualPoints[0].point[2]);
                    Point3d actualEnd = actualPoints == null || actualPoints.Length == 0 ? expectedEnd :
                        new Point3d(actualPoints[actualPoints.Length - 1].point[0], actualPoints[actualPoints.Length - 1].point[1], actualPoints[actualPoints.Length - 1].point[2]);
                    itemMatches = degree == 3 && periodic == 0 && pointCountMatch && pointsMatch && evaluationMatches &&
                        definitionMatches && conditionsMatch &&
                        EndpointsMatch(actualStart, actualEnd, expectedStart, expectedEnd);
                    itemEvidence.Add("geometry_journal_id", id);
                    itemEvidence.Add("degree", degree);
                    itemEvidence.Add("periodic", periodic);
                    itemEvidence.Add("interpolation_point_count", count);
                    itemEvidence.Add("interpolation_points", actualWorldPoints);
                    itemEvidence.Add("native_interpolation_conditions", SplineInterpolationConditionRecords(actualPoints));
                    itemEvidence.Add("interpolation_parameters", parameters == null ? new List<object>() : NumberList(parameters));
                    itemEvidence.Add("evaluated_interpolation_points", evaluatedWorldPoints);
                    itemEvidence.Add("start", Point(actualStart));
                    itemEvidence.Add("end", Point(actualEnd));
                    itemEvidence.Add("expected_start", Point(expectedStart));
                    itemEvidence.Add("expected_end", Point(expectedEnd));
                    itemEvidence.Add("interpolation_points_match_request", pointsMatch);
                    itemEvidence.Add("curve_evaluation_matches_request", evaluationMatches);
                    itemEvidence.Add("unconstrained_interpolation_conditions_match", conditionsMatch);
                    itemEvidence.Add("nurbs_definition_matches_execute", definitionMatches);
                    itemEvidence.Add("nurbs_definition", actualDefinition);
                }
            }
            else itemMatches = false;
            itemEvidence.Add("status", itemMatches ? "VERIFIED" : "MISMATCH");
            matches = matches && itemMatches;
            primitiveEvidence.Add(itemEvidence);
        }
        evidence.Add("status", matches ? "VERIFIED" : "MISMATCH");
        evidence.Add("sketch_id", RequiredJournalId(sketch, "M3 sketch"));
        evidence.Add("expected_geometry_count", expectedGeometryCount);
        evidence.Add("actual_geometry_count", geometry.Length);
        evidence.Add("geometry_count_matches", geometryCountMatches);
        evidence.Add("sketch_geometry", primitiveEvidence);
        if (plan.SupportFrameOrigin != null && plan.SupportFrameX != null && plan.SupportFrameNormal != null)
        {
            Point3d actualOrigin = sketch.Origin;
            Matrix3x3 orientation = sketch.Orientation.Element;
            double[] actualX = new double[] { orientation.Xx, orientation.Xy, orientation.Xz };
            double[] actualY = new double[] { orientation.Yx, orientation.Yy, orientation.Yz };
            double[] actualNormal = new double[] { orientation.Zx, orientation.Zy, orientation.Zz };
            double[] expectedY = plan.SupportFrameY ?? Cross3(plan.SupportFrameNormal, plan.SupportFrameX);
            bool frameMatches = PointNearArray(actualOrigin, plan.SupportFrameOrigin, M3FrameToleranceMm) &&
                VectorNearArray(actualX, plan.SupportFrameX, M3VectorTolerance) &&
                VectorNearArray(actualY, expectedY, M3VectorTolerance) &&
                VectorNearArray(actualNormal, plan.SupportFrameNormal, M3VectorTolerance);
            evidence.Add("frame_status", frameMatches ? "VERIFIED" : "MISMATCH");
            evidence.Add("expected_frame", M4FrameRecord(plan.SupportFrameOrigin, plan.SupportFrameX, expectedY, plan.SupportFrameNormal));
            evidence.Add("actual_frame", M4FrameRecord(new double[] { actualOrigin.X, actualOrigin.Y, actualOrigin.Z },
                actualX, actualY, actualNormal));
            matches = matches && frameMatches;
        }
        evidence["status"] = matches ? "VERIFIED" : "MISMATCH";
        return matches;
    }

    private static Dictionary<string, object> M3SourceSketchProjection(Dictionary<string, object> sketchRecord)
    {
        string[] fields = new string[] {
            "journal_id", "feature_journal_id", "status", "degrees_of_freedom", "units",
            "origin", "normal", "x_axis", "y_axis", "geometry", "constraints"
        };
        Dictionary<string, object> projection = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> missing = new List<object>();
        foreach (string field in fields)
        {
            object value;
            if (sketchRecord != null && sketchRecord.TryGetValue(field, out value)) projection.Add(field, value);
            else missing.Add(field);
        }
        projection.Add("missing_fields", missing);
        return projection;
    }

    private static bool ProjectionComplete(Dictionary<string, object> projection)
    {
        object value;
        List<object> missing = projection != null && projection.TryGetValue("missing_fields", out value)
            ? value as List<object> : null;
        return missing != null && missing.Count == 0;
    }

    private bool SketchConstraintsMatch(M3PlanState plan, Sketch sketch, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (sketch == null)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "Sketch is unavailable");
            return false;
        }
        if (plan.Kind == "create_extrude" || plan.Kind == "create_revolve")
        {
            Dictionary<string, object> current = BuildSketchDetail(sketch.OwningPart as Part, sketch, sketch.OwningPart.PartUnits.ToString());
            List<object> expected = plan.SketchRecordBefore == null ? null : plan.SketchRecordBefore["constraints"] as List<object>;
            List<object> actual = current["constraints"] as List<object>;
            bool same = expected != null && actual != null && SemanticValuesEqual(expected, actual, "constraints");
            evidence.Add("status", same ? "VERIFIED" : "MISMATCH");
            evidence.Add("expected", expected ?? new List<object>());
            evidence.Add("actual", actual ?? new List<object>());
            return same;
        }
        int dof;
        Sketch.Status status = sketch.GetStatus(out dof);
        List<object> actualDimensions = ReadM3DrivingDimensions(plan.Primitives);
        bool dimensionsEqual = SemanticValuesEqual(plan.DrivingDimensions, actualDimensions, "value");
        bool emptySketch = plan.SketchRequest != null && plan.SketchRequest.Geometries != null && plan.SketchRequest.Geometries.Count == 0;
        NXObject[] geometry = sketch.GetAllGeometry();
        List<object> constraints = ReadConstraintRecords(sketch);
        bool noUnrequestedGeometry = !emptySketch || (geometry != null && geometry.Length == 0 && constraints.Count == 0);
        bool fullyConstrained = !emptySketch && status == Sketch.Status.WellConstrained && dof == 0;
        bool geometryOnly = plan.SketchRequest != null && plan.SketchRequest.ConstraintMode == "geometry_only";
        bool constraintStateAcceptable = emptySketch ? noUnrequestedGeometry : geometryOnly
            ? status == Sketch.Status.UnderConstrained && dof > 0 && constraints.Count == 0
            : M3SketchUsesLegacyGeometry(plan) ? fullyConstrained
                : status == Sketch.Status.UnderConstrained || fullyConstrained;
        evidence.Add("status", dimensionsEqual && constraintStateAcceptable ? "VERIFIED" : "MISMATCH");
        evidence.Add("constraint_mode", geometryOnly ? "geometry_only" : "legacy_driving");
        evidence.Add("sketch_status", status.ToString());
        evidence.Add("degrees_of_freedom", dof);
        evidence.Add("expected_driving_dimensions", plan.DrivingDimensions ?? new List<object>());
        evidence.Add("actual_driving_dimensions", actualDimensions);
        evidence.Add("fully_constrained", fullyConstrained);
        evidence.Add("constraint_state_acceptable", constraintStateAcceptable);
        if (emptySketch) evidence.Add("empty_sketch_has_no_geometry_or_constraints", noUnrequestedGeometry);
        return dimensionsEqual && constraintStateAcceptable;
    }

    private static Point3d SketchPoint(Sketch sketch, double x, double y)
    {
        Matrix3x3 axes = sketch.Orientation.Element;
        Point3d origin = sketch.Origin;
        return new Point3d(origin.X + axes.Xx * x + axes.Yx * y,
            origin.Y + axes.Xy * x + axes.Yy * y,
            origin.Z + axes.Xz * x + axes.Yz * y);
    }

    private static Vector3d SketchVector(Sketch sketch, double x, double y)
    {
        Matrix3x3 axes = sketch.Orientation.Element;
        return new Vector3d(axes.Xx * x + axes.Yx * y,
            axes.Xy * x + axes.Yy * y, axes.Xz * x + axes.Yz * y);
    }

    private static Vector3d RotateInPlane(Vector3d xAxis, Vector3d yAxis, double rotation)
    {
        double cosine = Math.Cos(rotation), sine = Math.Sin(rotation);
        return new Vector3d(xAxis.X * cosine + yAxis.X * sine,
            xAxis.Y * cosine + yAxis.Y * sine, xAxis.Z * cosine + yAxis.Z * sine);
    }

    private static Point3d ConicPoint(Point3d center, Vector3d xAxis, Vector3d yAxis,
        double majorRadius, double minorRadius, double rotation, double parameter)
    {
        double cosine = Math.Cos(parameter), sine = Math.Sin(parameter);
        double rotationCosine = Math.Cos(rotation), rotationSine = Math.Sin(rotation);
        double x = majorRadius * cosine * rotationCosine - minorRadius * sine * rotationSine;
        double y = majorRadius * cosine * rotationSine + minorRadius * sine * rotationCosine;
        return new Point3d(center.X + xAxis.X * x + yAxis.X * y,
            center.Y + xAxis.Y * x + yAxis.Y * y,
            center.Z + xAxis.Z * x + yAxis.Z * y);
    }

    private static double[] NativeCurveParameterLimits(Curve curve)
    {
        IntPtr evaluator = IntPtr.Zero;
        try
        {
            UFSession.GetUFSession().Eval.Initialize(curve.Tag, out evaluator);
            if (evaluator == IntPtr.Zero) throw new InvalidOperationException("UF evaluator initialization returned a null handle.");
            double[] limits = new double[2];
            UFSession.GetUFSession().Eval.AskLimits(evaluator, limits);
            if (Double.IsNaN(limits[0]) || Double.IsInfinity(limits[0]) ||
                Double.IsNaN(limits[1]) || Double.IsInfinity(limits[1]) || limits[1] <= limits[0])
                throw new InvalidOperationException("UF evaluator returned invalid curve parameter limits.");
            return limits;
        }
        finally
        {
            if (evaluator != IntPtr.Zero) UFSession.GetUFSession().Eval.Free(evaluator);
        }
    }

    private static Point3d NativeCurvePoint(Curve curve, double parameter)
    {
        double[] point = new double[3];
        UFSession.GetUFSession().Curve.EvaluateCurve(curve.Tag, parameter, 0, point);
        return new Point3d(point[0], point[1], point[2]);
    }

    private static double EllipseArcLength(double majorRadius, double minorRadius,
        double startAngle, double sweep)
    {
        const int segments = 512;
        double endAngle = startAngle + sweep;
        double step = (endAngle - startAngle) / segments;
        double sum = 0.0;
        for (int i = 0; i <= segments; i++)
        {
            double angle = startAngle + step * i;
            double sine = Math.Sin(angle), cosine = Math.Cos(angle);
            double speed = Math.Sqrt(majorRadius * majorRadius * sine * sine +
                minorRadius * minorRadius * cosine * cosine);
            double weight = i == 0 || i == segments ? 1.0 : i % 2 == 0 ? 2.0 : 4.0;
            sum += weight * speed;
        }
        return Math.Abs(step) * sum / 3.0;
    }

    private static bool EndpointsMatch(Point3d a1, Point3d a2, Point3d b1, Point3d b2)
    {
        return (PointNearlyEqual(a1, b1) && PointNearlyEqual(a2, b2)) ||
            (PointNearlyEqual(a1, b2) && PointNearlyEqual(a2, b1));
    }

    private static bool PointNearlyEqual(Point3d left, Point3d right)
    {
        return NearlyEqual(left.X, right.X) && NearlyEqual(left.Y, right.Y) && NearlyEqual(left.Z, right.Z);
    }

    private static bool PointNearArray(Point3d point, double[] expected, double tolerance)
    {
        return expected != null && expected.Length == 3 && Math.Abs(point.X - expected[0]) <= tolerance &&
            Math.Abs(point.Y - expected[1]) <= tolerance && Math.Abs(point.Z - expected[2]) <= tolerance;
    }

    private static bool VectorNearArray(double[] actual, double[] expected, double tolerance)
    {
        return actual != null && expected != null && actual.Length == 3 && expected.Length == 3 &&
            Math.Abs(actual[0] - expected[0]) <= tolerance && Math.Abs(actual[1] - expected[1]) <= tolerance &&
            Math.Abs(actual[2] - expected[2]) <= tolerance;
    }

    private static M3ExtrudeRequest NormalizeM3ExtrudeRequest(M3ExtrudeRequest source)
    {
        M3ExtrudeRequest request = CloneM3ExtrudeRequest(source);
        bool legacy = String.IsNullOrEmpty(request.ProfileOutput) && request.Extent == "finite" &&
            request.StartOffsetMm == 0.0 && request.EndOffsetMm == 0.0 && request.DepthMm > 0.0;
        if (String.IsNullOrEmpty(request.ProfileOutput)) request.ProfileOutput = "solid";
        if (legacy) request.EndOffsetMm = request.DepthMm;
        if (request.Extent == "finite")
        {
            double span = request.EndOffsetMm - request.StartOffsetMm;
            if (!IsFinite(request.StartOffsetMm) || !IsFinite(request.EndOffsetMm) || !IsFinite(span) || span <= 0.0)
                throw new M2ModelException("INVALID_REQUEST", "finite extrusion requires finite start/end offsets with end > start and a finite span");
            request.DepthMm = span;
        }
        else if (request.Extent == "through_all")
        {
            if (!IsFinite(request.StartOffsetMm))
                throw new M2ModelException("INVALID_REQUEST", "through_all start_offset_mm must be finite");
            request.DepthMm = 0.0;
            request.EndOffsetMm = 0.0;
        }
        return request;
    }

    private static bool F12SectionAnalysisVerified(Dictionary<string, object> analysis)
    {
        if (analysis == null) return false;
        object statusValue, matchValue, fingerprintValue;
        Dictionary<string, object> match = analysis.TryGetValue("section_domain_match", out matchValue)
            ? matchValue as Dictionary<string, object> : null;
        object matchStatus = null;
        if (match != null) match.TryGetValue("status", out matchStatus);
        return analysis.TryGetValue("status", out statusValue) &&
            String.Equals(Convert.ToString(statusValue, CultureInfo.InvariantCulture), "VERIFIED", StringComparison.Ordinal) &&
            match != null && String.Equals(Convert.ToString(matchStatus, CultureInfo.InvariantCulture), "MATCHED", StringComparison.Ordinal) &&
            analysis.TryGetValue("input_geometry_domain_fingerprint", out fingerprintValue) &&
            !String.IsNullOrEmpty(Convert.ToString(fingerprintValue, CultureInfo.InvariantCulture)) &&
            analysis.ContainsKey("section_output_domain_fingerprint");
    }

    private static int ProfileCount(Dictionary<string, object> analysis, string field)
    {
        object value;
        int count;
        if (analysis == null || !analysis.TryGetValue(field, out value) ||
            !Int32.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out count) || count < 0)
            throw new M2ModelException("MODEL_READ_FAILED", "F12 profile analysis did not return a valid " + field);
        return count;
    }

    private static string GetProfileFingerprint(Dictionary<string, object> analysis, string key)
    {
        object value;
        string fingerprint = analysis != null && analysis.TryGetValue(key, out value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        if (String.IsNullOrEmpty(fingerprint))
            throw new M2ModelException("M3_PROFILE_READBACK_FAILED", "F12 profile analysis did not return " + key);
        return fingerprint;
    }

    private static List<M3BodyMeasure> ResolveAffectedBodies(M3PlanState plan,
        List<M3BodyMeasure> after, Feature feature)
    {
        if (after == null || feature == null)
            throw new M2ModelException("M3_BODY_READ_FAILED", "NX returned no M3 feature or body inventory");
        Body[] featureBodies = feature.GetBodies();
        if (featureBodies == null || featureBodies.Length == 0)
            throw new M2ModelException("M3_BODY_READ_FAILED", "NX Extrude feature returned no result bodies");
        HashSet<string> seenTags = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> seenIds = new HashSet<string>(StringComparer.Ordinal);
        List<M3BodyMeasure> result = new List<M3BodyMeasure>();
        foreach (Body candidate in featureBodies)
        {
            if (candidate == null) throw new M2ModelException("M3_BODY_READ_FAILED", "NX Extrude feature body list contains null");
            string tag = candidate.Tag.ToString();
            string id = RequiredJournalId(candidate, "Extrude result body");
            if (!seenTags.Add(tag) || !seenIds.Add(id))
                throw new M2ModelException("M3_BODY_READ_FAILED", "NX Extrude feature returned duplicate result bodies");
            M3BodyMeasure found = null;
            foreach (M3BodyMeasure body in after)
            {
                if (!body.Body.Tag.Equals(candidate.Tag)) continue;
                if (found != null) throw new M2ModelException("M3_BODY_READ_FAILED", "Extrude result body has duplicate Work Part inventory entries");
                found = body;
            }
            if (found == null)
                throw new M2ModelException("M3_BODY_READ_FAILED", "an Extrude feature result body is absent from the Work Part body inventory");
            result.Add(found);
        }
        result.Sort(delegate(M3BodyMeasure left, M3BodyMeasure right) { return String.CompareOrdinal(left.Id, right.Id); });
        return result;
    }

    private bool M3BodyEffectMatches(M3PlanState plan, List<M3BodyMeasure> affected,
        List<M3BodyMeasure> after, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        M3ExtrudeRequest request = plan == null ? null : plan.ExtrudeRequest;
        bool matches = request != null && affected != null && affected.Count > 0 && after != null && plan.BodiesBefore != null;
        M3BodyMeasure beforeTarget = null;
        if (matches && request.Mode != "create") beforeTarget = FindBodyMeasure(plan.BodiesBefore, plan.TargetBodyId);
        HashSet<string> beforeTags = new HashSet<string>(StringComparer.Ordinal);
        if (plan != null && plan.BodiesBefore != null)
            foreach (M3BodyMeasure body in plan.BodiesBefore) beforeTags.Add(body.Body.Tag.ToString());
        bool identitiesMatch = matches;
        foreach (M3BodyMeasure body in affected ?? new List<M3BodyMeasure>())
        {
            bool existedBefore = beforeTags.Contains(body.Body.Tag.ToString());
            if (request != null && request.Mode == "create" && existedBefore) identitiesMatch = false;
            if (request != null && request.Mode != "create" && !body.IsSolid) identitiesMatch = false;
            if (request != null && request.Mode == "create" && request.ProfileOutput == "solid" &&
                (!body.IsSolid || !body.VolumeMm3.HasValue || body.VolumeMm3.Value <= 0.0)) identitiesMatch = false;
            if (request != null && request.Mode == "create" && request.ProfileOutput == "sheet" && body.IsSolid) identitiesMatch = false;
            if (body.BboxMm == null || body.BboxMm.Length != 6) identitiesMatch = false;
        }
        int expectedAfterCount = request == null ? -1 : request.Mode == "create"
            ? plan.BodiesBefore.Count + affected.Count
            : plan.BodiesBefore.Count - 1 + affected.Count;
        bool countMatches = after != null && after.Count == expectedAfterCount;
        double? beforeVolume = request == null || request.Mode == "create" ? (double?)0.0 : beforeTarget == null ? null : beforeTarget.VolumeMm3;
        double? afterVolume = AggregateBodyVolume(affected);
        bool volumeChangedCorrectly = false;
        if (request != null && request.Mode == "create")
            volumeChangedCorrectly = request.ProfileOutput == "sheet"
                ? affected != null && affected.Count > 0 && affected.TrueForAll(delegate(M3BodyMeasure body) { return !body.IsSolid; })
                : afterVolume.HasValue && afterVolume.Value > 0.0;
        else if (request != null && beforeVolume.HasValue && afterVolume.HasValue)
        {
            if (request.Mode == "unite") volumeChangedCorrectly = afterVolume.Value > beforeVolume.Value + ValueTolerance(afterVolume.Value, beforeVolume.Value);
            else volumeChangedCorrectly = afterVolume.Value > 0.0 && afterVolume.Value < beforeVolume.Value - ValueTolerance(afterVolume.Value, beforeVolume.Value);
        }
        Dictionary<string, object> nonTargetEvidence;
        bool nonTargetsEqual = F12NonTargetBodiesMatch(plan, after, plan.BodyFingerprintsAfter, out nonTargetEvidence);
        matches = matches && identitiesMatch && countMatches && volumeChangedCorrectly && nonTargetsEqual;
        if (request != null && request.ProfileOutput == "sheet")
            matches = matches && plan.ExpectedSheetSurfaceAreaMm2.HasValue &&
                plan.ExpectedSheetSurfaceAreaMm2.Value > 0.0 && plan.SheetSurfaceAreaMm2.HasValue &&
                plan.SheetSurfaceAreaMm2.Value > 0.0 && plan.SheetAreaEvidence != null &&
                String.Equals(Convert.ToString(plan.SheetAreaEvidence["status"], CultureInfo.InvariantCulture),
                    "VERIFIED", StringComparison.Ordinal);
        evidence.Add("status", matches ? "VERIFIED" : "MISMATCH");
        evidence.Add("mode", request == null ? null : request.Mode);
        evidence.Add("profile_output", request == null ? null : request.ProfileOutput);
        evidence.Add("target_body_id_before", plan == null ? null : plan.TargetBodyId);
        evidence.Add("affected_body_ids_after", StringListAsObjects(plan == null || plan.AffectedBodyIds == null ? new List<string>() : plan.AffectedBodyIds));
        evidence.Add("target_volume_before_mm3", beforeVolume);
        evidence.Add("affected_volume_after_mm3", afterVolume);
        evidence.Add("volume_delta_mm3", beforeVolume.HasValue && afterVolume.HasValue
            ? (object)(afterVolume.Value - beforeVolume.Value) : null);
        evidence.Add("body_count_before", plan == null || plan.BodiesBefore == null ? 0 : plan.BodiesBefore.Count);
        evidence.Add("body_count_after", after == null ? 0 : after.Count);
        evidence.Add("body_count_expected_after", expectedAfterCount);
        evidence.Add("body_count_matches_mode", countMatches);
        evidence.Add("output_body_identity_matches_feature", identitiesMatch);
        evidence.Add("volume_changed_in_requested_direction", volumeChangedCorrectly);
        evidence.Add("volume_measurement_accuracy", request == null ? null :
            request.Mode == "create" && request.ProfileOutput == "sheet" ? (object)"native_area_measurement" : F12MassPropertyAccuracy);
        evidence.Add("non_target_body_geometry", nonTargetEvidence);
        evidence.Add("sheet_surface_area", plan == null ? null : plan.SheetAreaEvidence);
        evidence.Add("affected_bodies", BodyMeasureRecords(affected, null));
        return matches;
    }

    private static M2ModelException M3AnalysisStateUnknown(string message, Exception cause)
    {
        string detail = cause == null ? null : cause.GetType().Name;
        M2ModelException modelCause = cause as M2ModelException;
        if (modelCause != null) detail = modelCause.Code;
        F12BodyGeometrySnapshot.CleanupException cleanupCause = cause as F12BodyGeometrySnapshot.CleanupException;
        if (cleanupCause != null) detail = cleanupCause.GetType().Name + ":" + cleanupCause.CleanupOperation;
        if (!String.IsNullOrEmpty(detail)) message += " (" + detail + ")";
        return new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN", message);
    }

    private static M2ModelException F12AnalysisStateUnknownOrNull(Exception exception, string context)
    {
        M2ModelException modelException = exception as M2ModelException;
        if (modelException != null && String.Equals(modelException.Code,
            "M3_ANALYSIS_STATE_UNKNOWN", StringComparison.Ordinal)) return modelException;
        if ((modelException != null && String.Equals(modelException.Code,
            "F12_BODY_READ_EFFECT_UNKNOWN", StringComparison.Ordinal)) ||
            exception is V2SketchProfileAnalyzer.SectionCleanupException ||
            exception is F12BodyGeometrySnapshot.CleanupException)
            return M3AnalysisStateUnknown(context, exception);
        return null;
    }

    private static void ThrowIfF12AnalysisStateUnknown(Exception exception, string context)
    {
        M2ModelException unknown = F12AnalysisStateUnknownOrNull(exception, context);
        if (unknown != null) throw unknown;
    }

    private static string ExceptionCode(Exception exception)
    {
        M2ModelException modelException = exception as M2ModelException;
        return modelException == null ? "MODEL_READ_FAILED" : modelException.Code;
    }

    private static Dictionary<string, object> CaptureAuditedF12BodyGeometry(Part work, Body body)
    {
        if (work == null || body == null)
            throw new M2ModelException("M3_BODY_READ_FAILED", "F12 direct body geometry snapshot input is unavailable");
        bool modifiedBefore;
        HashSet<string> tagsBefore;
        try
        {
            modifiedBefore = work.IsModified;
            tagsBefore = F12CurrentBodyTags(work);
        }
        catch (Exception ex)
        {
            throw M3AnalysisStateUnknown("F12 could not establish Work Part state before direct body geometry snapshot", ex);
        }
        string requestedTag;
        try { requestedTag = body.Tag.ToString(); }
        catch (Exception ex)
        {
            throw new M2ModelException("M3_BODY_READ_FAILED", "F12 direct body geometry snapshot could not read the requested body identity (" + ex.GetType().Name + ")");
        }
        if (!tagsBefore.Contains(requestedTag))
            throw new M2ModelException("M3_BODY_READ_FAILED", "F12 direct body geometry snapshot input is absent from the Work Part body inventory");

        Dictionary<string, object> record = null;
        Exception captureFailure = null;
        try { record = F12BodyGeometrySnapshot.Capture(work, body); }
        catch (Exception ex) { captureFailure = ex; }

        bool modifiedAfter;
        HashSet<string> tagsAfter;
        try
        {
            modifiedAfter = work.IsModified;
            tagsAfter = F12CurrentBodyTags(work);
        }
        catch (Exception ex)
        {
            throw M3AnalysisStateUnknown("F12 could not verify Work Part state after direct body geometry snapshot", ex);
        }
        if (modifiedBefore != modifiedAfter)
            throw M3AnalysisStateUnknown("F12 direct body geometry snapshot changed Work Part dirty state", null);
        if (tagsBefore.Count != tagsAfter.Count || !tagsBefore.SetEquals(tagsAfter))
            throw M3AnalysisStateUnknown("F12 direct body geometry snapshot changed the Work Part body inventory", null);
        ThrowIfF12AnalysisStateUnknown(captureFailure,
            "F12 direct body geometry resource cleanup could not be verified");
        if (captureFailure != null)
            throw new M2ModelException("M3_BODY_READ_FAILED",
                "F12 could not capture body geometry after proving the Work Part remained unchanged (" + captureFailure.GetType().Name + ")");
        if (record == null)
            throw new M2ModelException("M3_BODY_READ_FAILED", "F12 direct body geometry snapshot returned no record");
        return record;
    }

    private static HashSet<string> F12CurrentBodyTags(Part work)
    {
        Body[] bodies = work.Bodies.ToArray();
        if (bodies == null) throw new InvalidOperationException("Work Part body inventory returned null");
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Body body in bodies)
            if (body == null || !tags.Add(body.Tag.ToString()))
                throw new InvalidOperationException("Work Part body inventory contains a null or duplicate Tag");
        return tags;
    }

    private static Dictionary<string, string> CaptureF12BodyFingerprints(Part work, List<M3BodyMeasure> bodies)
    { return CaptureF12BodyFingerprints(work, bodies, null); }

    private static Dictionary<string, string> CaptureF12BodyFingerprints(Part work, List<M3BodyMeasure> bodies,
        Dictionary<string, Dictionary<string, object>> details)
    {
        if (work == null || bodies == null) throw new M2ModelException("M3_BODY_READ_FAILED", "F12 body signature input is unavailable");
        bool modifiedBefore;
        try { modifiedBefore = work.IsModified; }
        catch (Exception ex)
        {
            throw M3AnalysisStateUnknown(
                "F12 could not capture Work Part dirty state before geometry measurement", ex);
        }
        HashSet<string> expectedTags = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> suppliedTags = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            Body[] bodiesBefore = work.Bodies.ToArray();
            if (bodiesBefore == null) throw new InvalidOperationException("Work Part body inventory returned null before F12 measurement");
            foreach (Body body in bodiesBefore)
                if (body == null || !expectedTags.Add(body.Tag.ToString()))
                    throw new InvalidOperationException("Work Part body inventory contains a null or duplicate Tag before F12 measurement");
            foreach (M3BodyMeasure measured in bodies)
                if (measured == null || measured.Body == null || !suppliedTags.Add(measured.Body.Tag.ToString()))
                    throw new InvalidOperationException("F12 body signature input contains a null or duplicate Tag");
        }
        catch (Exception ex)
        {
            throw M3AnalysisStateUnknown(
                "F12 could not establish the body inventory before geometry measurement", ex);
        }
        if (!expectedTags.SetEquals(suppliedTags) || expectedTags.Count != suppliedTags.Count)
            throw new M2ModelException("M3_BODY_READ_FAILED", "F12 body signature input differs from the current Work Part body inventory");
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        Exception captureFailure = null;
        foreach (M3BodyMeasure measured in bodies)
        {
            if (measured == null || measured.Body == null)
            {
                captureFailure = new InvalidOperationException("body signature inventory contains a null body");
                break;
            }
            string tag = measured.Body.Tag.ToString();
            if (String.IsNullOrEmpty(tag) || result.ContainsKey(tag))
            {
                captureFailure = new InvalidOperationException("body signature inventory contains duplicate or missing Tags");
                break;
            }
            try {
                Dictionary<string,object> record=F12BodyGeometrySnapshot.Capture(work,measured.Body);
                result.Add(tag,F12BodyGeometrySnapshot.Canonical(record));
                if(details!=null)details.Add(tag,record);
            }
            catch (Exception ex)
            {
                captureFailure = ex;
                break;
            }
        }
        bool modifiedAfter;
        bool bodyInventoryMatches;
        try
        {
            modifiedAfter = work.IsModified;
            Body[] bodiesAfter = work.Bodies.ToArray();
            if (bodiesAfter == null) throw new InvalidOperationException("Work Part body inventory returned null after F12 measurement");
            HashSet<string> afterTags = new HashSet<string>(StringComparer.Ordinal);
            foreach (Body body in bodiesAfter)
            {
                if (body == null || !afterTags.Add(body.Tag.ToString()))
                    throw new InvalidOperationException("Work Part body inventory contains a null or duplicate Tag after F12 measurement");
            }
            bodyInventoryMatches = afterTags.SetEquals(expectedTags) && afterTags.Count == expectedTags.Count;
        }
        catch (Exception ex)
        {
            throw M3AnalysisStateUnknown(
                "F12 could not verify Work Part state after geometry measurement", ex);
        }
        if (modifiedBefore != modifiedAfter)
            throw M3AnalysisStateUnknown("F12 body geometry measurement changed Work Part dirty state", null);
        if (!bodyInventoryMatches)
            throw M3AnalysisStateUnknown("F12 body geometry measurement changed the Work Part body inventory", null);
        if (captureFailure != null)
        {
            ThrowIfF12AnalysisStateUnknown(captureFailure,
                "F12 body geometry resource cleanup could not be verified");
            throw new M2ModelException("M3_BODY_READ_FAILED",
                "F12 could not capture a complete body geometry signature (" + captureFailure.GetType().Name + ")");
        }
        return result;
    }

    private static bool F12BodyFingerprintsEqual(Dictionary<string, string> before,
        Dictionary<string, string> after, out Dictionary<string, object> evidence)
    {
        bool same = before != null && after != null && before.Count == after.Count;
        List<string> missing = new List<string>();
        List<string> added = new List<string>();
        List<string> changed = new List<string>();
        if (before != null)
            foreach (KeyValuePair<string, string> pair in before)
            {
                string current;
                if (after == null || !after.TryGetValue(pair.Key, out current)) missing.Add(pair.Key);
                else if (!String.Equals(pair.Value, current, StringComparison.Ordinal)) changed.Add(pair.Key);
            }
        if (after != null)
            foreach (KeyValuePair<string, string> pair in after)
                if (before == null || !before.ContainsKey(pair.Key)) added.Add(pair.Key);
        same = same && missing.Count == 0 && added.Count == 0 && changed.Count == 0;
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("status", same ? "VERIFIED" : "MISMATCH");
        evidence.Add("body_count_before", before == null ? 0 : before.Count);
        evidence.Add("body_count_after", after == null ? 0 : after.Count);
        evidence.Add("missing_body_tags", StringListAsObjects(missing));
        evidence.Add("added_body_tags", StringListAsObjects(added));
        evidence.Add("changed_geometry_body_tags", StringListAsObjects(changed));
        evidence.Add("comparison", "NX analytic/NURBS edge and face parameters, topology, and UV surface samples by stable session Tag");
        return same;
    }

    private static bool F12AffectedBodiesMatch(List<M3BodyMeasure> expected,
        List<M3BodyMeasure> actual, out Dictionary<string, object> evidence)
    {
        List<string> expectedTags = new List<string>();
        List<string> actualTags = new List<string>();
        if (expected != null)
            foreach (M3BodyMeasure body in expected) expectedTags.Add(body.Body.Tag.ToString());
        if (actual != null)
            foreach (M3BodyMeasure body in actual) actualTags.Add(body.Body.Tag.ToString());
        expectedTags.Sort(StringComparer.Ordinal);
        actualTags.Sort(StringComparer.Ordinal);
        bool same = expectedTags.Count == actualTags.Count;
        if (same)
            for (int i = 0; i < expectedTags.Count; i++)
                if (!String.Equals(expectedTags[i], actualTags[i], StringComparison.Ordinal)) { same = false; break; }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", same ? "VERIFIED" : "MISMATCH" },
            { "expected_body_tags", StringListAsObjects(expectedTags) },
            { "actual_body_tags", StringListAsObjects(actualTags) }
        };
        return same;
    }

    private static bool F12NonTargetBodiesMatch(M3PlanState plan, List<M3BodyMeasure> after,
        Dictionary<string, string> currentFingerprints,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        if (plan == null || plan.BodyFingerprintsBefore == null || currentFingerprints == null ||
            plan.AffectedBodies == null || after == null)
        {
            evidence.Add("status", "MISMATCH");
            evidence.Add("reason", "F12 before/after body fingerprints or feature outputs are unavailable");
            return false;
        }
        HashSet<string> excludedBefore = new HashSet<string>(StringComparer.Ordinal);
        if (plan.TargetBody != null) excludedBefore.Add(plan.TargetBody.Tag.ToString());
        HashSet<string> excludedAfter = new HashSet<string>(StringComparer.Ordinal);
        foreach (M3BodyMeasure body in plan.AffectedBodies) excludedAfter.Add(body.Body.Tag.ToString());
        Dictionary<string, string> beforeNonTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> afterNonTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> item in plan.BodyFingerprintsBefore)
            if (!excludedBefore.Contains(item.Key)) beforeNonTargets.Add(item.Key, item.Value);
        foreach (KeyValuePair<string, string> item in currentFingerprints)
            if (!excludedAfter.Contains(item.Key)) afterNonTargets.Add(item.Key, item.Value);
        Dictionary<string, object> compareEvidence;
        bool same = F12BodyFingerprintsEqual(beforeNonTargets, afterNonTargets, out compareEvidence);
        evidence.Add("status", same ? "VERIFIED" : "MISMATCH");
        evidence.Add("comparison", compareEvidence);
        evidence.Add("excluded_target_tag", plan.TargetBody == null ? null : (object)plan.TargetBody.Tag.ToString());
        evidence.Add("excluded_feature_output_tags", StringListAsObjects(new List<string>(excludedAfter)));
        evidence.Add("compared_tags", StringListAsObjects(new List<string>(beforeNonTargets.Keys)));
        return same;
    }

    private static List<object> F12NonTargetFingerprintRecords(Dictionary<string, string> fingerprints,
        Body target, List<M3BodyMeasure> featureOutputs)
    {
        HashSet<string> excluded = new HashSet<string>(StringComparer.Ordinal);
        if (target != null) excluded.Add(target.Tag.ToString());
        if (featureOutputs != null)
            foreach (M3BodyMeasure body in featureOutputs) if (body != null && body.Body != null) excluded.Add(body.Body.Tag.ToString());
        List<string> tags = new List<string>();
        if (fingerprints != null)
            foreach (KeyValuePair<string, string> item in fingerprints)
                if (!excluded.Contains(item.Key)) tags.Add(item.Key);
        tags.Sort(StringComparer.Ordinal);
        List<object> records = new List<object>();
        foreach (string tag in tags)
            records.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                { "body_tag", tag }, { "geometry_signature_captured", true },
                { "signature_character_count", fingerprints[tag] == null ? 0 : fingerprints[tag].Length }
            });
        return records;
    }

    private static double? AggregateBodyVolume(List<M3BodyMeasure> bodies)
    {
        if (bodies == null || bodies.Count == 0) return null;
        double total = 0.0;
        foreach (M3BodyMeasure body in bodies)
        {
            if (body == null || !body.IsSolid || !body.VolumeMm3.HasValue) return null;
            total += body.VolumeMm3.Value;
            if (Double.IsNaN(total) || Double.IsInfinity(total)) return null;
        }
        return total;
    }

    private static double[] AggregateBodyBounds(List<M3BodyMeasure> bodies)
    {
        if (bodies == null || bodies.Count == 0) return null;
        double[] bounds = null;
        foreach (M3BodyMeasure body in bodies)
        {
            if (body == null || body.BboxMm == null || body.BboxMm.Length != 6) return null;
            if (bounds == null) bounds = (double[])body.BboxMm.Clone();
            else
            {
                for (int i = 0; i < 3; i++) bounds[i] = Math.Min(bounds[i], body.BboxMm[i]);
                for (int i = 3; i < 6; i++) bounds[i] = Math.Max(bounds[i], body.BboxMm[i]);
            }
        }
        return bounds;
    }

    private M3ExecutionResult RestoreM3State(M2ModelBinding binding, BindingState state,
        M3PlanState plan, Session.UndoMarkId mark, Exception operationException)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("operation_exception", operationException.GetType().Name);
        evidence.Add("operation_exception_code", ExceptionCode(operationException));
        evidence.Add("operation_message", operationException.Message);
        M2ModelException modelOperationException = operationException as M2ModelException;
        if (plan.ProfileAnalysis != null) evidence.Add("profile_analysis", plan.ProfileAnalysis);
        if (plan.ExecutionFailureEvidence != null && modelOperationException != null &&
            modelOperationException.Diagnostics != null)
        {
            Dictionary<string, object> combinedDiagnostics = new Dictionary<string, object>(StringComparer.Ordinal);
            combinedDiagnostics.Add("plan_execution_failure", plan.ExecutionFailureEvidence);
            combinedDiagnostics.Add("model_exception", modelOperationException.Diagnostics);
            evidence.Add("operation_diagnostics", combinedDiagnostics);
        }
        else if (plan.ExecutionFailureEvidence != null)
            evidence.Add("operation_diagnostics", plan.ExecutionFailureEvidence);
        else if (modelOperationException != null && modelOperationException.Diagnostics != null)
            evidence.Add("operation_diagnostics", modelOperationException.Diagnostics);
        bool undoCalled = false;
        int updateErrors = -1;
        try
        {
            Sketch active = state.Session.ActiveSketch;
            if (active != null && !Object.ReferenceEquals(active, plan.ActiveSketchBefore))
            {
                active.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
                int deactivateErrors = state.Session.UpdateManager.DoUpdate(mark);
                evidence.Add("active_sketch_deactivate_update_errors", deactivateErrors);
                if (deactivateErrors != 0) throw new InvalidOperationException("active Sketch deactivation update returned errors");
            }
        }
        catch (Exception ex)
        {
            evidence.Add("active_sketch_deactivate_error", ex.GetType().Name);
            return M3Execution("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "active Sketch could not be ended before undo");
        }
        try
        {
            state.Session.UndoToMarkWithStatus(mark, "M3 native sketch/extrude rollback");
            undoCalled = true;
            updateErrors = state.Session.UpdateManager.DoUpdate(mark);
            evidence.Add("undo_called", undoCalled);
            evidence.Add("undo_update_error_count", updateErrors);
            if (updateErrors != 0)
                return M3Execution("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "NX undo update returned errors");
        }
        catch (Exception ex)
        {
            evidence.Add("undo_called", undoCalled);
            evidence.Add("undo_exception", ex.GetType().Name);
            return M3Execution("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "NX UndoToMark could not establish the prior M3 model state");
        }
        try
        {
            bool modifiedAfterUndo = state.WorkPart.IsModified;
            Sketch activeSketchAfterUndo = state.Session.ActiveSketch;
            bool activeSketchRestored = SameSketchIdentity(plan.ActiveSketchBefore, activeSketchAfterUndo);
            evidence.Add("work_part_modified_before", plan.WorkPartModifiedBefore);
            evidence.Add("work_part_modified_after_undo", modifiedAfterUndo);
            evidence.Add("dirty_state_restored", plan.WorkPartModifiedBefore == modifiedAfterUndo);
            evidence.Add("active_sketch_tag_before", SketchTagText(plan.ActiveSketchBefore));
            evidence.Add("active_sketch_tag_after_undo", SketchTagText(activeSketchAfterUndo));
            evidence.Add("active_sketch_restored", activeSketchRestored);
            SnapshotResult restored = Capture(binding, state);
            string semantic = CanonicalSemanticSnapshot(restored.Snapshot);
            string geometry = CaptureFullGeometry(state.WorkPart);
            List<object> history = HistoryProjection(restored.Snapshot);
            List<M3BodyMeasure> bodies = CaptureM3BodyMeasures(state.WorkPart,
                plan.ExtrudeRequest == null && plan.RevolveRequest == null && plan.HoleRequest == null && plan.BooleanRequest == null && plan.EdgeBlendRequest == null && plan.ChamferRequest == null && plan.FeaturePatternRequest == null && plan.FeatureMirrorRequest == null && plan.F21Sweep == null
                    ? M3LegacyMassPropertyAccuracy : F12MassPropertyAccuracy);
            Dictionary<string, object> bodyEvidence;
            bool bodiesEqual = BodyMeasuresEqual(plan.BodiesBefore, bodies, out bodyEvidence);
            bool bodyGeometryRestored = true;
            Dictionary<string, object> bodyGeometryEvidence = null;
            bool curveInventoryRestored = true;
            bool holeHelpersRestored = true;
            bool holeDefinitionsRestored = true;
            string curveInventoryAfterUndo = null;
            if (plan.ExtrudeRequest != null || plan.RevolveRequest != null || plan.HoleRequest != null || (plan.BooleanRequest != null || plan.EdgeBlendRequest != null || plan.ChamferRequest != null || plan.FeaturePatternRequest != null || plan.FeatureMirrorRequest != null))
            {
                Dictionary<string,Dictionary<string,object>> f19RestoredDetails = plan.FeatureMirrorRequest == null ? null : new Dictionary<string,Dictionary<string,object>>(StringComparer.Ordinal);
                Dictionary<string, string> bodyFingerprints = CaptureF12BodyFingerprints(state.WorkPart, bodies, f19RestoredDetails);
                bodyGeometryRestored = plan.FeatureMirrorRequest == null
                    ? F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore, bodyFingerprints, out bodyGeometryEvidence)
                    : F19BodyPrintsCompare(plan.BodyFingerprintsBefore, bodyFingerprints, plan.F19BodyDetailsBefore, f19RestoredDetails, plan.F19OldMirrorBodyTags, out bodyGeometryEvidence);
                curveInventoryAfterUndo = M3SketchProjectionCurveInventoryText(
                    CaptureM3SketchProjectionCurveInventory(state.WorkPart));
                curveInventoryRestored = String.Equals(plan.F12CurveInventoryBefore,
                    curveInventoryAfterUndo, StringComparison.Ordinal);
                if ((plan.BooleanRequest != null || plan.EdgeBlendRequest != null || plan.ChamferRequest != null || plan.FeaturePatternRequest != null || plan.FeatureMirrorRequest != null))
                {
                    holeHelpersRestored = F14HelpersMatch(plan.HoleHelpersBefore, F14CaptureHelpers(state.WorkPart), null, null, null);
                    holeDefinitionsRestored = F14PersistentHoleDefinitionsEqual(plan.HoleDefinitionsBefore, F14CapturePersistentHoleDefinitions(state.WorkPart, null)) && F15DefinitionsEqual(plan.BooleanDefinitionsBefore, F15CaptureDefinitions(state.WorkPart, null)) &&
                        ((plan.EdgeBlendRequest == null && plan.ChamferRequest == null && plan.FeaturePatternRequest == null && plan.FeatureMirrorRequest == null) || F16DefinitionsEqual(plan.EdgeBlendDefinitionsBefore, F16CaptureDefinitions(state.WorkPart, null))) &&
                        ((plan.ChamferRequest == null && plan.FeaturePatternRequest == null && plan.FeatureMirrorRequest == null) || F16DefinitionsEqual(plan.ChamferDefinitionsBefore, F17CaptureDefinitions(state.WorkPart, null))) &&
                        (plan.FeaturePatternRequest == null || (F18RelatedDefinitionsMatch(state.WorkPart, plan, null) &&
                            String.Equals(plan.F18InventoryBefore, F18Inventory(state.WorkPart), StringComparison.Ordinal))) &&
                        (plan.FeatureMirrorRequest == null || (F19DefinitionsMatch(state.WorkPart, plan) &&
                            String.Equals(plan.F18InventoryBefore, F19Inventory(state.WorkPart), StringComparison.Ordinal) &&
                            String.Equals(M2Json.CanonicalText(plan.F19ExpressionsBefore), M2Json.CanonicalText(F19ExpressionStates(state.WorkPart)), StringComparison.Ordinal)));
                }
                if (plan.HoleRequest != null)
                {
                    holeHelpersRestored = F14HelpersMatch(plan.HoleHelpersBefore, F14CaptureHelpers(state.WorkPart), null, null, null);
                    holeDefinitionsRestored = F14PersistentHoleDefinitionsEqual(plan.HoleDefinitionsBefore, F14CapturePersistentHoleDefinitions(state.WorkPart, null));
                }
            }
            if (plan.F21Sweep != null)
            {
                F21SweepPlanData sweep = plan.F21Sweep;
                Dictionary<string, Dictionary<string, object>> details = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                Dictionary<string, string> fingerprints = CaptureF12BodyFingerprints(state.WorkPart, bodies, details);
                bodyGeometryRestored = F19BodyPrintsCompare(plan.BodyFingerprintsBefore, fingerprints,
                    sweep.BodyDetailsBefore, details, sweep.OldMirrorBodyTags, out bodyGeometryEvidence);
                curveInventoryAfterUndo = M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart));
                curveInventoryRestored = String.Equals(plan.F12CurveInventoryBefore, curveInventoryAfterUndo, StringComparison.Ordinal);
                holeHelpersRestored = F14HelpersMatch(plan.HoleHelpersBefore, F14CaptureHelpers(state.WorkPart), null, null, null);
                Dictionary<string, object> sweepDefinitionEvidence = null;
                holeDefinitionsRestored = F19DefinitionsMatch(state.WorkPart, plan) &&
                    F21SweepDefinitionComparison.Compare(sweep.PriorDefinitions, F21CaptureDefinitions(state.WorkPart, null), out sweepDefinitionEvidence) &&
                    F16DefinitionsEqual(sweep.SeedDefinitions, F21CaptureSeedDefinitions(state.WorkPart)) &&
                    F16DefinitionsEqual(sweep.SketchDatumDefinitions, F21CaptureSketchDatums(state.WorkPart)) &&
                    String.Equals(plan.F18InventoryBefore, F19Inventory(state.WorkPart), StringComparison.Ordinal) &&
                    String.Equals(M2Json.CanonicalText(sweep.ExpressionsBefore), M2Json.CanonicalText(F19ExpressionStates(state.WorkPart)), StringComparison.Ordinal);
                evidence["sweep_prior_definitions_restored"] = holeDefinitionsRestored;
                evidence["sweep_prior_definition_comparison"] = sweepDefinitionEvidence;
            }
            bool semanticEqual = String.Equals(plan.SemanticBefore, semantic, StringComparison.Ordinal);
            bool geometryEqual = String.Equals(plan.GeometryBefore, geometry, StringComparison.Ordinal);
            bool historyEqual = SemanticValuesEqual(plan.HistoryBefore, history, "history");
            evidence.Add("semantic_restored", semanticEqual);
            evidence.Add("geometry_restored", geometryEqual);
            evidence.Add("history_restored", historyEqual);
            evidence.Add("bodies_restored", bodiesEqual);
            evidence.Add("body_measurement_evidence", bodyEvidence);
            evidence.Add("full_body_geometry_restored", bodyGeometryRestored);
            evidence.Add("body_geometry_fingerprint_evidence", bodyGeometryEvidence);
            evidence.Add("full_part_curve_inventory_restored", curveInventoryRestored);
            if (plan.HoleRequest != null || (plan.BooleanRequest != null || plan.EdgeBlendRequest != null || plan.ChamferRequest != null || plan.FeaturePatternRequest != null || plan.FeatureMirrorRequest != null)) evidence.Add("point_and_direction_inventory_restored", holeHelpersRestored);
            if (plan.HoleRequest != null) evidence.Add("existing_hole_definitions_restored", holeDefinitionsRestored);
            if ((plan.BooleanRequest != null || plan.EdgeBlendRequest != null || plan.ChamferRequest != null || plan.FeaturePatternRequest != null || plan.FeatureMirrorRequest != null)) evidence.Add("existing_hole_and_boolean_definitions_restored", holeDefinitionsRestored);
            evidence.Add("curve_inventory_after_undo", curveInventoryAfterUndo);
            evidence.Add("feature_ids_after_undo", FeatureIds(restored.Snapshot));
            evidence.Add("body_inventory_after_undo", BodyMeasureRecords(bodies, null));
            bool restoredAll = semanticEqual && geometryEqual && historyEqual && bodiesEqual &&
                bodyGeometryRestored && curveInventoryRestored && holeHelpersRestored && holeDefinitionsRestored &&
                plan.WorkPartModifiedBefore == modifiedAfterUndo && plan.WorkPartModifiedBefore == state.WorkPart.IsModified && activeSketchRestored;
            if (plan.HoleRequest != null) evidence.Add("dirty_state_after_hole_readback_restored", plan.WorkPartModifiedBefore == state.WorkPart.IsModified);
            if ((plan.BooleanRequest != null || plan.EdgeBlendRequest != null || plan.ChamferRequest != null || plan.FeaturePatternRequest != null || plan.FeatureMirrorRequest != null)) evidence.Add("dirty_state_after_boolean_readback_restored", plan.WorkPartModifiedBefore == state.WorkPart.IsModified);
            if (!restoredAll)
                return M3Execution("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "NX Undo completed but the model did not match the captured M3 pre-operation state");
            if (modelOperationException != null && String.Equals(modelOperationException.Code,
                "M3_ANALYSIS_STATE_UNKNOWN", StringComparison.Ordinal))
            {
                evidence.Add("rollback_restored_after_analysis_state_unknown", true);
                return M3Execution("UNKNOWN", binding.RevisionId, evidence, "M3_ANALYSIS_STATE_UNKNOWN",
                    "NX model state was restored by Undo, but a native analysis resource cleanup or state audit failed; writes remain frozen");
            }
            if (plan.F21Sweep != null || plan.RevolveRequest != null || plan.HoleRequest != null || (plan.BooleanRequest != null || plan.EdgeBlendRequest != null || plan.ChamferRequest != null || plan.FeaturePatternRequest != null || plan.FeatureMirrorRequest != null))
            {
                try
                {
                    evidence.Add("rollback_revision_advance",
                        AdvanceVerifiedF13RevolveRollbackRevision(binding, state, plan));
                }
                catch (Exception ex)
                {
                    evidence.Add("rollback_revision_advance_error", ex.GetType().Name);
                    return M3Execution("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED",
                        "NX model restoration was verified, but the operation revision could not be advanced safely");
                }
            }
            return M3Execution("ROLLED_BACK", binding.RevisionId, evidence, "M3_OPERATION_ROLLED_BACK", operationException.Message);
        }
        catch (Exception ex)
        {
            evidence.Add("rollback_readback_error", ex.GetType().Name);
            return M3Execution("UNKNOWN", binding.RevisionId, evidence, "ROLLBACK_UNVERIFIED", "NX undo completed but the restored model state could not be measured");
        }
    }

    private static Dictionary<string, object> AdvanceVerifiedF13RevolveRollbackRevision(
        M2ModelBinding binding, BindingState state, M3PlanState plan)
    {
        if (binding == null || state == null || plan == null || (plan.RevolveRequest == null && plan.HoleRequest == null && plan.BooleanRequest == null && plan.EdgeBlendRequest == null && plan.ChamferRequest == null && plan.FeaturePatternRequest == null && plan.FeatureMirrorRequest == null && plan.SpatialCurveRequest == null && plan.F21Sweep == null))
            throw new InvalidOperationException("verified operation rollback revision inputs are unavailable");
        int preparedRevision;
        int bindingRevision;
        if (!Int32.TryParse(plan.RevisionId, NumberStyles.None, CultureInfo.InvariantCulture, out preparedRevision) ||
            preparedRevision < 0)
            throw new InvalidOperationException("prepared Revolve revision is not a non-negative integer");
        if (!Int32.TryParse(binding.RevisionId, NumberStyles.None, CultureInfo.InvariantCulture, out bindingRevision) ||
            bindingRevision < 0 || state.RevisionNumber < 0 || state.RevisionNumber != bindingRevision)
            throw new InvalidOperationException("binding and model revision counters do not agree after restored snapshot capture");
        if (state.CanonicalSnapshot == null ||
            !String.Equals(state.CanonicalSnapshot, binding.CanonicalSnapshot, StringComparison.Ordinal) ||
            state.CanonicalBodyMeasurements == null)
            throw new InvalidOperationException("restored semantic or body measurement snapshot is not synchronized with the binding");

        int revisionBeforeAdvance = state.RevisionNumber;
        if (revisionBeforeAdvance <= preparedRevision)
        {
            if (preparedRevision == Int32.MaxValue)
                throw new InvalidOperationException("prepared revision counter cannot be advanced without overflow");
            state.RevisionNumber = preparedRevision + 1;
        }
        binding.RevisionId = state.RevisionNumber.ToString(CultureInfo.InvariantCulture);
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("prepared_revision_id", plan.RevisionId);
        evidence.Add("revision_counter_before_advance", revisionBeforeAdvance);
        evidence.Add("binding_revision_before_advance", bindingRevision.ToString(CultureInfo.InvariantCulture));
        evidence.Add("revision_id_after_rollback", binding.RevisionId);
        evidence.Add("revision_advanced_past_prepared", state.RevisionNumber > preparedRevision);
        evidence.Add("snapshot_state_synchronized", true);
        evidence.Add("rule", "A verified Revolve, Hole or Boolean rollback invalidates all plans prepared at or before its transaction revision.");
        return evidence;
    }

    private static M3ExecutionResult M3Execution(string state, string revisionId,
        Dictionary<string, object> result, string errorCode, string errorMessage)
    {
        return new M3ExecutionResult
        {
            State = state,
            RevisionId = revisionId,
            Result = result ?? new Dictionary<string, object>(StringComparer.Ordinal),
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    private static M3PlanState CreateM3PlanState(M2ModelBinding binding, BindingState state,
        string kind, SnapshotResult capture, double bodyMeasurementAccuracy = M3LegacyMassPropertyAccuracy)
    {
        bool modifiedBefore = state.WorkPart.IsModified;
        return new M3PlanState
        {
            BindingState = state,
            Kind = kind,
            RevisionId = binding.RevisionId,
            SemanticBefore = CanonicalSemanticSnapshot(capture.Snapshot),
            GeometryBefore = CaptureFullGeometry(state.WorkPart),
            HistoryBefore = HistoryProjection(capture.Snapshot),
            BodiesBefore = CaptureM3BodyMeasures(state.WorkPart, bodyMeasurementAccuracy),
            Primitives = new List<M3PrimitiveState>(),
            ActiveSketchBefore = state.Session.ActiveSketch,
            WorkPartModifiedBefore = modifiedBefore
        };
    }

    private static string SketchTagText(Sketch sketch)
    {
        return sketch == null ? null : sketch.Tag.ToString();
    }

    private static bool SameSketchIdentity(Sketch expected, Sketch actual)
    {
        if (expected == null || actual == null) return expected == null && actual == null;
        return expected.Tag.Equals(actual.Tag);
    }

    private static M3SketchRequest CloneM3SketchRequest(M3SketchRequest request)
    {
        M3SketchRequest clone = new M3SketchRequest();
        clone.RevisionId = request.RevisionId;
        clone.Plane = request.Plane;
        clone.OriginMm = request.OriginMm == null ? null : (double[])request.OriginMm.Clone();
        clone.Normal = request.Normal == null ? null : (double[])request.Normal.Clone();
        clone.XAxis = request.XAxis == null ? null : (double[])request.XAxis.Clone();
        clone.SupportFaceSelector = request.SupportFaceSelector;
        clone.SupportFaceId = request.SupportFaceId;
        clone.SupportFaceTag = request.SupportFaceTag;
        clone.SupportDatumPlaneId = request.SupportDatumPlaneId;
        clone.SupportBodyId = request.SupportBodyId;
        clone.FaceOriginOffsetMm = request.FaceOriginOffsetMm == null ? null : (double[])request.FaceOriginOffsetMm.Clone();
        clone.ConstraintMode = request.ConstraintMode;
        clone.Geometries = new List<M3SketchGeometryRequest>();
        foreach (M3SketchGeometryRequest geometry in request.Geometries)
        {
            M3SketchGeometryRequest item = new M3SketchGeometryRequest();
            item.Id = geometry.Id; item.Type = geometry.Type;
            item.StartMm = geometry.StartMm == null ? null : (double[])geometry.StartMm.Clone();
            item.EndMm = geometry.EndMm == null ? null : (double[])geometry.EndMm.Clone();
            item.CornerMm = geometry.CornerMm == null ? null : (double[])geometry.CornerMm.Clone();
            item.CenterMm = geometry.CenterMm == null ? null : (double[])geometry.CenterMm.Clone();
            item.WidthMm = geometry.WidthMm; item.HeightMm = geometry.HeightMm; item.DiameterMm = geometry.DiameterMm;
            item.RadiusMm = geometry.RadiusMm; item.MajorRadiusMm = geometry.MajorRadiusMm;
            item.MinorRadiusMm = geometry.MinorRadiusMm; item.RotationDeg = geometry.RotationDeg;
            item.StartAngleDeg = geometry.StartAngleDeg; item.SweepDeg = geometry.SweepDeg;
            item.Degree = geometry.Degree; item.EndpointCondition = geometry.EndpointCondition;
            if (geometry.InterpolationPointsMm != null)
            {
                item.InterpolationPointsMm = new double[geometry.InterpolationPointsMm.Length][];
                for (int i = 0; i < geometry.InterpolationPointsMm.Length; i++)
                    item.InterpolationPointsMm[i] = geometry.InterpolationPointsMm[i] == null
                        ? null : (double[])geometry.InterpolationPointsMm[i].Clone();
            }
            clone.Geometries.Add(item);
        }
        return clone;
    }

    private static M3DatumPlaneRequest CloneM3DatumPlaneRequest(M3DatumPlaneRequest request)
    {
        return new M3DatumPlaneRequest
        {
            RevisionId = request.RevisionId,
            Mode = request.Mode,
            OriginMm = request.OriginMm == null ? null : (double[])request.OriginMm.Clone(),
            Normal = request.Normal == null ? null : (double[])request.Normal.Clone(),
            XAxis = request.XAxis == null ? null : (double[])request.XAxis.Clone(),
            SupportDatumPlaneId = request.SupportDatumPlaneId,
            SupportFaceId = request.SupportFaceId,
            SupportFaceTag = request.SupportFaceTag,
            DistanceMm = request.DistanceMm,
            AxisLineId = request.AxisLineId,
            AngleDeg = request.AngleDeg,
            Line1Id = request.Line1Id,
            Line2Id = request.Line2Id
        };
    }

    private static void NormalizeM3SketchFrame(M3SketchRequest request)
    {
        NormalizeM3Frame(request.OriginMm, ref request.Normal, ref request.XAxis, "sketch frame");
    }

    private static void NormalizeM3DatumPlaneRequest(M3DatumPlaneRequest request)
    {
        if (request.SupportFaceTag != null &&
            (String.IsNullOrWhiteSpace(request.SupportFaceTag) || String.IsNullOrWhiteSpace(request.SupportFaceId)))
            throw new M2ModelException("INVALID_REQUEST", "support_face_tag requires a non-empty support_face_id and support_face_tag");
        if (String.Equals(request.Mode, "two_lines", StringComparison.Ordinal))
        {
            if (String.IsNullOrWhiteSpace(request.Line1Id) || String.IsNullOrWhiteSpace(request.Line2Id) ||
                String.Equals(request.Line1Id, request.Line2Id, StringComparison.Ordinal) ||
                request.OriginMm != null || request.Normal != null || request.XAxis != null ||
                !String.IsNullOrWhiteSpace(request.SupportDatumPlaneId) ||
                !String.IsNullOrWhiteSpace(request.SupportFaceId) || request.DistanceMm != 0.0 ||
                !String.IsNullOrWhiteSpace(request.AxisLineId) || request.AngleDeg != 0.0)
                throw new M2ModelException("INVALID_REQUEST", "two-line datum plane requires two distinct line IDs without fixed, offset, or angle fields");
            return;
        }
        if (String.Equals(request.Mode, "angle", StringComparison.Ordinal))
        {
            bool datumReference = !String.IsNullOrWhiteSpace(request.SupportDatumPlaneId);
            bool faceReference = !String.IsNullOrWhiteSpace(request.SupportFaceId);
            if (datumReference == faceReference || String.IsNullOrWhiteSpace(request.AxisLineId) ||
                request.OriginMm != null || request.Normal != null || request.XAxis != null ||
                request.DistanceMm != 0.0 || request.Line1Id != null || request.Line2Id != null)
                throw new M2ModelException("INVALID_REQUEST", "angle datum plane requires exactly one support reference and axis_line_id without fixed, offset, or two-line fields");
            EnsureFinite(request.AngleDeg, "angle_deg");
            if (request.AngleDeg < -180.0 || request.AngleDeg > 180.0)
                throw new M2ModelException("INVALID_REQUEST", "angle_deg must be between -180 and 180 degrees, inclusive");
            return;
        }
        if (String.Equals(request.Mode, "offset", StringComparison.Ordinal))
        {
            bool datumReference = !String.IsNullOrWhiteSpace(request.SupportDatumPlaneId);
            bool faceReference = !String.IsNullOrWhiteSpace(request.SupportFaceId);
            if (datumReference == faceReference || request.OriginMm != null || request.Normal != null || request.XAxis != null ||
                !String.IsNullOrWhiteSpace(request.AxisLineId) || request.AngleDeg != 0.0 ||
                request.Line1Id != null || request.Line2Id != null)
                throw new M2ModelException("INVALID_REQUEST", "offset datum plane requires exactly one support reference and cannot be mixed with fixed, angle, or two-line fields");
            EnsureFinite(request.DistanceMm, "distance_mm");
            return;
        }
        if (!String.IsNullOrEmpty(request.Mode) && !String.Equals(request.Mode, "fixed", StringComparison.Ordinal))
            throw new M2ModelException("INVALID_REQUEST", "datum plane mode must be fixed, offset, angle, or two_lines");
        if (request.SupportDatumPlaneId != null || request.SupportFaceId != null ||
            !String.IsNullOrWhiteSpace(request.AxisLineId) || request.AngleDeg != 0.0 ||
            request.Line1Id != null || request.Line2Id != null)
            throw new M2ModelException("INVALID_REQUEST", "fixed datum plane fields cannot be mixed with offset, angle, or two-line references");
        NormalizeM3Frame(request.OriginMm, ref request.Normal, ref request.XAxis, "datum plane frame");
    }

    private static void NormalizeM3Frame(double[] origin, ref double[] normal, ref double[] xAxis, string label)
    {
        if (origin == null || origin.Length != 3 || normal == null || normal.Length != 3 || xAxis == null || xAxis.Length != 3)
            throw new M2ModelException("INVALID_REQUEST", label + " requires 3D origin, normal, and x_axis arrays");
        foreach (double value in origin) EnsureFinite(value, label + " origin");
        foreach (double value in normal) EnsureFinite(value, label + " normal");
        foreach (double value in xAxis) EnsureFinite(value, label + " x_axis");
        double normalLength = Length3(normal);
        if (normalLength <= M3VectorTolerance || Double.IsNaN(normalLength) || Double.IsInfinity(normalLength))
            throw new M2ModelException("INVALID_REQUEST", label + " normal must be finite and nonzero");
        normal = new double[] { normal[0] / normalLength, normal[1] / normalLength, normal[2] / normalLength };
        double projection = Dot3(xAxis, normal);
        double[] projected = new double[] { xAxis[0] - projection * normal[0],
            xAxis[1] - projection * normal[1], xAxis[2] - projection * normal[2] };
        double xLength = Length3(projected);
        if (xLength <= M3VectorTolerance || Double.IsNaN(xLength) || Double.IsInfinity(xLength))
            throw new M2ModelException("INVALID_REQUEST", label + " x_axis must not be zero or parallel to the normal");
        xAxis = new double[] { projected[0] / xLength, projected[1] / xLength, projected[2] / xLength };
    }

    private static M3ExtrudeRequest CloneM3ExtrudeRequest(M3ExtrudeRequest request)
    {
        return new M3ExtrudeRequest
        {
            RevisionId = request.RevisionId,
            SketchId = request.SketchId,
            Mode = request.Mode,
            Direction = request.Direction,
            Extent = request.Extent,
            ProfileOutput = request.ProfileOutput,
            DepthMm = request.DepthMm,
            StartOffsetMm = request.StartOffsetMm,
            EndOffsetMm = request.EndOffsetMm,
            TargetBodyId = request.TargetBodyId
        };
    }

    private static List<object> SketchGeometryPublicRecords(List<M3SketchGeometryRequest> geometries)
    {
        List<object> records = new List<object>();
        foreach (M3SketchGeometryRequest geometry in geometries)
        {
            Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
            record.Add("id", geometry.Id);
            record.Add("type", geometry.Type);
            if (geometry.Type == "line")
            {
                record.Add("start_mm", NumberList(geometry.StartMm));
                record.Add("end_mm", NumberList(geometry.EndMm));
            }
            else if (geometry.Type == "rectangle")
            {
                record.Add("corner_mm", NumberList(geometry.CornerMm));
                record.Add("width_mm", geometry.WidthMm);
                record.Add("height_mm", geometry.HeightMm);
            }
            else if (geometry.Type == "circle")
            {
                record.Add("center_mm", NumberList(geometry.CenterMm));
                record.Add("diameter_mm", geometry.DiameterMm);
            }
            else if (geometry.Type == "arc")
            {
                record.Add("center_mm", NumberList(geometry.CenterMm));
                record.Add("radius_mm", geometry.RadiusMm);
                record.Add("start_angle_deg", geometry.StartAngleDeg);
                record.Add("sweep_deg", geometry.SweepDeg);
            }
            else if (geometry.Type == "ellipse" || geometry.Type == "elliptical_arc")
            {
                record.Add("center_mm", NumberList(geometry.CenterMm));
                record.Add("major_radius_mm", geometry.MajorRadiusMm);
                record.Add("minor_radius_mm", geometry.MinorRadiusMm);
                record.Add("rotation_deg", geometry.RotationDeg);
                if (geometry.Type == "elliptical_arc")
                {
                    record.Add("start_angle_deg", geometry.StartAngleDeg);
                    record.Add("sweep_deg", geometry.SweepDeg);
                }
            }
            else if (geometry.Type == "spline")
            {
                List<object> points = new List<object>();
                foreach (double[] point in geometry.InterpolationPointsMm) points.Add(NumberList(point));
                record.Add("interpolation_points_mm", points);
                record.Add("degree", geometry.Degree);
                record.Add("endpoint_condition", geometry.EndpointCondition);
                record.Add("parameterization", "normalized_chord_length");
            }
            records.Add(record);
        }
        return records;
    }

    private static List<object> ExpectedDrivingDimensionRecords(List<M3SketchGeometryRequest> geometries)
    {
        List<object> records = new List<object>();
        foreach (M3SketchGeometryRequest geometry in geometries)
        {
            if (geometry.Type == "line")
            {
                double dx = geometry.EndMm[0] - geometry.StartMm[0];
                double dy = geometry.EndMm[1] - geometry.StartMm[1];
                if (geometry.StartMm[0] != 0.0 || geometry.StartMm[1] != 0.0)
                {
                    AddExpectedDimension(records, geometry.Id, "x", "HorizontalDim", Math.Abs(geometry.StartMm[0]), "sketch origin to line start x");
                    AddExpectedDimension(records, geometry.Id, "y", "VerticalDim", Math.Abs(geometry.StartMm[1]), "sketch origin to line start y");
                }
                if (Math.Abs(dy) <= 1e-9)
                    AddExpectedDimension(records, geometry.Id, "length", "HorizontalDim", Math.Abs(dx), "line endpoint projected x span");
                else if (Math.Abs(dx) <= 1e-9)
                    AddExpectedDimension(records, geometry.Id, "length", "VerticalDim", Math.Abs(dy), "line endpoint projected y span");
                else
                {
                    AddExpectedDimension(records, geometry.Id, "length_x", "HorizontalDim", Math.Abs(dx), "line endpoint projected x span");
                    AddExpectedDimension(records, geometry.Id, "length_y", "VerticalDim", Math.Abs(dy), "line endpoint projected y span");
                }
            }
            else if (geometry.Type == "rectangle")
            {
                if (geometry.CornerMm[0] != 0.0 || geometry.CornerMm[1] != 0.0)
                {
                    AddExpectedDimension(records, geometry.Id, "x", "HorizontalDim", Math.Abs(geometry.CornerMm[0]), "sketch origin to rectangle corner x");
                    AddExpectedDimension(records, geometry.Id, "y", "VerticalDim", Math.Abs(geometry.CornerMm[1]), "sketch origin to rectangle corner y");
                }
                AddExpectedDimension(records, geometry.Id, "width", "HorizontalDim", geometry.WidthMm, "rectangle width");
                AddExpectedDimension(records, geometry.Id, "height", "VerticalDim", geometry.HeightMm, "rectangle height");
            }
            else if (geometry.Type == "circle")
            {
                if (geometry.CenterMm[0] != 0.0 || geometry.CenterMm[1] != 0.0)
                {
                    AddExpectedDimension(records, geometry.Id, "x", "HorizontalDim", Math.Abs(geometry.CenterMm[0]), "sketch origin to circle center x");
                    AddExpectedDimension(records, geometry.Id, "y", "VerticalDim", Math.Abs(geometry.CenterMm[1]), "sketch origin to circle center y");
                }
                AddExpectedDimension(records, geometry.Id, "diameter", "DiameterDim", geometry.DiameterMm, "circle diameter");
            }
        }
        return records;
    }

    private static void AddExpectedDimension(List<object> records, string geometryId, string role,
        string type, double value, string reference)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
        record.Add("geometry_id", geometryId);
        record.Add("role", role);
        record.Add("constraint_type", type);
        record.Add("value", value);
        record.Add("unit", "mm");
        record.Add("geometry_refs_by_role", reference);
        records.Add(record);
    }

    private static void ValidateExtrudeRequest(M3ExtrudeRequest request)
    {
        if (request.Mode != "create" && request.Mode != "unite" && request.Mode != "subtract")
            throw new M2ModelException("INVALID_REQUEST", "mode must be create, unite, or subtract");
        if (request.Direction != "positive" && request.Direction != "negative")
            throw new M2ModelException("INVALID_REQUEST", "direction must be positive or negative");
        if (request.Extent != "finite" && request.Extent != "through_all")
            throw new M2ModelException("INVALID_REQUEST", "extent must be finite or through_all");
        if (request.ProfileOutput != "solid" && request.ProfileOutput != "sheet")
            throw new M2ModelException("INVALID_REQUEST", "profile_output must be solid or sheet");
        if (request.ProfileOutput == "sheet" && (request.Mode != "create" || request.Extent != "finite"))
            throw new M2ModelException("INVALID_REQUEST", "sheet output supports only finite create extrusion");
        if (request.Extent == "finite")
        {
            double span = request.EndOffsetMm - request.StartOffsetMm;
            if (!IsFinite(request.StartOffsetMm) || !IsFinite(request.EndOffsetMm) || !IsFinite(span) || span <= 0.0)
                throw new M2ModelException("INVALID_REQUEST", "finite Extrude requires finite start/end offsets with a positive finite span");
        }
        else
        {
            if (request.Mode == "create")
                throw new M2ModelException("INVALID_REQUEST", "create extrusion supports only finite extent");
            if (String.IsNullOrWhiteSpace(request.TargetBodyId))
                throw new M2ModelException("INVALID_REQUEST", "unite and subtract require one explicit target_body_id");
            if (request.ProfileOutput != "solid")
                throw new M2ModelException("INVALID_REQUEST", "through_all Boolean extrusion supports solid output only");
            if (request.DepthMm != 0.0 || request.EndOffsetMm != 0.0)
                throw new M2ModelException("INVALID_REQUEST", "through_all does not accept finite depth/end offsets");
            if (!IsFinite(request.StartOffsetMm))
                throw new M2ModelException("INVALID_REQUEST", "through_all start_offset_mm must be finite");
        }
        if (request.Mode == "create" && !String.IsNullOrEmpty(request.TargetBodyId))
            throw new M2ModelException("INVALID_REQUEST", "create extrusion does not accept target_body_id");
        if (request.Mode != "create" && String.IsNullOrWhiteSpace(request.TargetBodyId))
            throw new M2ModelException("INVALID_REQUEST", "unite and subtract require one explicit target_body_id");
    }

    private static Sketch FindSketch(Part part, string sketchId)
    {
        if (part == null || String.IsNullOrWhiteSpace(sketchId)) throw new M2ModelException("INVALID_REQUEST", "sketch_id is required");
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketches.ToArray returned null");
        Sketch found = null;
        int matches = 0;
        foreach (Sketch sketch in sketches)
        {
            if (sketch != null && String.Equals(RequiredJournalId(sketch, "sketch"), sketchId, StringComparison.Ordinal))
            {
                found = sketch;
                matches++;
            }
        }
        if (matches != 1 || found == null)
            throw new M2ModelException(matches == 0 ? "SKETCH_NOT_FOUND" : "AMBIGUOUS_SKETCH", "sketch_id must identify exactly one Sketch in the Work Part");
        return found;
    }

    private static Body FindBody(Part part, string bodyId)
    {
        if (part == null || String.IsNullOrWhiteSpace(bodyId)) throw new M2ModelException("INVALID_REQUEST", "target_body_id is required");
        Body[] bodies = part.Bodies.ToArray();
        if (bodies == null) throw new M2ModelException("MODEL_READ_FAILED", "Bodies.ToArray returned null");
        Body found = null;
        int matches = 0;
        foreach (Body body in bodies)
        {
            if (body != null && String.Equals(RequiredJournalId(body, "body"), bodyId, StringComparison.Ordinal))
            {
                found = body;
                matches++;
            }
        }
        if (matches != 1 || found == null)
            throw new M2ModelException(matches == 0 ? "BODY_NOT_FOUND" : "AMBIGUOUS_BODY", "target_body_id must identify exactly one body in the Work Part");
        return found;
    }

    private static List<M3BodyMeasure> CaptureM3BodyMeasures(Part part)
    {
        return CaptureM3BodyMeasures(part, M3LegacyMassPropertyAccuracy);
    }

    private static List<M3BodyMeasure> CaptureM3BodyMeasures(Part part, double massPropertyAccuracy)
    {
        if (!IsFinite(massPropertyAccuracy) || massPropertyAccuracy <= 0.0 || massPropertyAccuracy > 1.0)
            throw new M2ModelException("INVALID_MEASUREMENT_ACCURACY", "NX body mass-property accuracy must be finite and in (0, 1]");
        Body[] bodies = part.Bodies.ToArray();
        if (bodies == null) throw new M2ModelException("MODEL_READ_FAILED", "Bodies.ToArray returned null during M3 measurement");
        List<M3BodyMeasure> result = new List<M3BodyMeasure>();
        foreach (Body body in bodies)
        {
            if (body == null) throw new M2ModelException("UNSUPPORTED_MODEL", "body collection contains null during M3 measurement");
            result.Add(MeasureBody(part, body, massPropertyAccuracy));
        }
        result.Sort(delegate(M3BodyMeasure left, M3BodyMeasure right) { return String.CompareOrdinal(left.Id, right.Id); });
        return result;
    }

    private static List<object> BodyMeasureRecords(List<M3BodyMeasure> bodies, string omitBodyId)
    {
        List<object> records = new List<object>();
        if (bodies == null) return records;
        foreach (M3BodyMeasure body in bodies)
            if (omitBodyId == null || !String.Equals(body.Id, omitBodyId, StringComparison.Ordinal)) records.Add(Clone(body.PublicRecord));
        return records;
    }

    private static M3BodyMeasure FindBodyMeasure(List<M3BodyMeasure> bodies, string bodyId)
    {
        if (bodies == null) throw new M2ModelException("MODEL_READ_FAILED", "body measurements are unavailable");
        M3BodyMeasure found = null;
        int matches = 0;
        foreach (M3BodyMeasure body in bodies)
            if (String.Equals(body.Id, bodyId, StringComparison.Ordinal)) { found = body; matches++; }
        if (matches != 1 || found == null)
            throw new M2ModelException(matches == 0 ? "BODY_NOT_FOUND" : "AMBIGUOUS_BODY", "body measurement id is unavailable or ambiguous");
        return found;
    }

    private static M3BodyMeasure FindBodyMeasureByTag(List<M3BodyMeasure> bodies, string bodyId, Body bodyReference)
    {
        if (bodies == null) return null;
        foreach (M3BodyMeasure body in bodies)
        {
            if (bodyReference != null && body.Body.Tag.Equals(bodyReference.Tag)) return body;
            if (bodyReference == null && String.Equals(body.Id, bodyId, StringComparison.Ordinal)) return body;
        }
        return null;
    }

    private static bool BodyMeasuresEqual(List<M3BodyMeasure> before, List<M3BodyMeasure> after,
        out Dictionary<string, object> evidence)
    {
        return BodyMeasurementRecordsEqual(BodyMeasureRecords(before, null), BodyMeasureRecords(after, null), out evidence);
    }

    private static M3VerificationResult M3VerificationFailure(string revisionId, string reason)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("reason", reason);
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        string[] names = new string[] { "geometry", "constraints", "history", "bodies", "non_target_bodies" };
        foreach (string name in names) checks.Add(name, VerificationCheck("MISMATCH", Clone(evidence)));
        return new M3VerificationResult
        {
            Verified = false,
            RevisionId = revisionId,
            Checks = checks,
            Result = new Dictionary<string, object>(StringComparer.Ordinal),
            ErrorCode = "PART_VERIFICATION_FAILED",
            ErrorMessage = reason
        };
    }

    public M2SaveCopyResult SaveCopy(M2ModelBinding binding, string targetPath, bool reopen)
    {
        BindingState state = RequireState(binding);
        string sourcePath = state.WorkPart == null ? null : state.WorkPart.FullPath;
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("source_path", sourcePath);
        evidence.Add("saved_path", targetPath);
        evidence.Add("save_attempted", false);
        evidence.Add("reopen_requested", reopen);

        if (String.Equals(binding.ArtifactScope, "current_work_part", StringComparison.Ordinal))
            return SaveCopyRejected("CURRENT_WORK_PART_SAVE_UNSUPPORTED", "nx_save_copy is unavailable for current_work_part because SaveAs/reopen cannot preserve the in-memory Work Part identity.", evidence);
        if (IsPartReadOnly(state.WorkPart))
            return SaveCopyRejected("READ_ONLY_PART", "saving is not allowed for a read-only Work Part.", evidence);
        if (!reopen)
            return SaveCopyRejected("REOPEN_REQUIRED", "reopen=true is required so the saved native copy can be checked after loading it again.", evidence);
        string bindingReason;
        if (!IsBindingCurrent(binding, out bindingReason))
            return SaveCopyRejected("STALE_BINDING", bindingReason ?? "NX model binding is no longer current.", evidence);
        string fullTarget;
        try { fullTarget = Path.GetFullPath(targetPath); }
        catch (Exception ex)
        {
            return SaveCopyRejected("INVALID_TARGET_PATH", "target_path cannot be canonicalized (" + ex.GetType().Name + ").", evidence);
        }
        string canonicalTarget = CanonicalPath(fullTarget);
        if (!String.Equals(Path.GetExtension(fullTarget), ".prt", StringComparison.OrdinalIgnoreCase))
            return SaveCopyRejected("INVALID_TARGET_PATH", "target_path must end in .prt.", evidence);
        if (String.Equals(state.CanonicalPath, canonicalTarget, StringComparison.OrdinalIgnoreCase))
            return SaveCopyRejected("TARGET_IS_SOURCE_PART", "target_path cannot overwrite the takeover source part.", evidence);
        if (File.Exists(fullTarget) || Directory.Exists(fullTarget))
            return SaveCopyRejected("TARGET_ALREADY_EXISTS", "target_path already exists; save copy never overwrites existing files.", evidence);
        if (!Directory.Exists(Path.GetDirectoryName(fullTarget)))
            return SaveCopyRejected("TARGET_DIRECTORY_NOT_FOUND", "target_path directory does not exist.", evidence);

        SnapshotResult before;
        List<object> geometryBefore;
        Dictionary<string, object> holeDefinitionsBefore;
        Dictionary<string, object> booleanDefinitionsBefore;
        Dictionary<string, object> blendDefinitionsBefore;
        Dictionary<string, object> chamferDefinitionsBefore;
        Dictionary<string, object> patternDefinitionsBefore;
        Dictionary<string, object> mirrorDefinitionsBefore;
        List<object> sweptDefinitionsBefore;
        try
        {
            holeDefinitionsBefore = F14CapturePersistentHoleDefinitions(state.WorkPart, null);
            booleanDefinitionsBefore = F15CaptureDefinitions(state.WorkPart, null);
            blendDefinitionsBefore = F16CaptureDefinitions(state.WorkPart, null);
            chamferDefinitionsBefore = F17CaptureDefinitions(state.WorkPart, null);
            patternDefinitionsBefore = F18CaptureDefinitions(state.WorkPart, null);
            mirrorDefinitionsBefore = F19CaptureDefinitions(state.WorkPart, null);
            sweptDefinitionsBefore = F21PersistentDefinitions(state.WorkPart, F21CaptureDefinitions(state.WorkPart, null));
            before = Capture(binding, state);
            F20PersistentRecords(SnapshotList(before.Snapshot,"spatial_curves"));
            geometryBefore = CaptureGeometryRecords(state.WorkPart, false);
        }
        catch (M2ModelException ex)
        {
            if (String.Equals(ex.Code, "M3_ANALYSIS_STATE_UNKNOWN", StringComparison.Ordinal))
                return SaveCopyUnknown(ex.Code, "Existing hole readback cleanup could not be verified before SaveAs.", evidence);
            return SaveCopyRejected(ex.Code, "The native Work Part could not be read before SaveAs: " + ex.Message, evidence);
        }
        catch (Exception ex)
        {
            return SaveCopyRejected("MODEL_READ_FAILED", "The native Work Part could not be read before SaveAs (" + ex.GetType().Name + ").", evidence);
        }

        Dictionary<string, object> faceAssociationAuditBefore;
        try { faceAssociationAuditBefore = FaceAssociationPersistenceAudit(before.Snapshot); }
        catch (Exception ex) { faceAssociationAuditBefore = FailedFaceAssociationPersistenceAudit(ex); }
        evidence["face_association_persistence_before_save"] = faceAssociationAuditBefore;
        if (!String.Equals(Convert.ToString(faceAssociationAuditBefore["status"], CultureInfo.InvariantCulture),
            "VERIFIED", StringComparison.Ordinal))
            return SaveCopyRejected("UNSUPPORTED_ASSOCIATION_PERSISTENCE",
                "SaveAs was rejected because a Face-supported Sketch or DatumPlane cannot be uniquely reidentified after reopening.", evidence);

        evidence["source_revision_id"] = binding.RevisionId;
        evidence["source_part_tag"] = state.PartTag;
        evidence["save_attempted"] = true;
        PartSaveStatus saveStatus = null;
        try
        {
            saveStatus = state.WorkPart.SaveAs(fullTarget);
            if (saveStatus == null)
                return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX SaveAs returned no status object.", evidence);
            if (saveStatus.NumberUnsavedParts != 0 || saveStatus.NumberUnsavedObjects != 0)
            {
                evidence["unsaved_part_count"] = saveStatus.NumberUnsavedParts;
                evidence["unsaved_object_count"] = saveStatus.NumberUnsavedObjects;
                return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX SaveAs reported unsaved parts or objects.", evidence);
            }
        }
        catch (Exception ex)
        {
            AddExceptionEvidence(evidence, "save", ex);
            return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX SaveAs did not return a verifiable status.", evidence);
        }
        finally
        {
            if (saveStatus != null) saveStatus.Dispose();
        }

        if (!File.Exists(fullTarget))
            return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX SaveAs returned success but the requested .prt file is absent.", evidence);
        evidence["target_file_exists_after_save"] = true;

        Part reopenedPart;
        try
        {
            reopenedPart = ReopenSavedCopy(state.Session, state.WorkPart, fullTarget);
            if (reopenedPart == null || !String.Equals(CanonicalPath(reopenedPart.FullPath), canonicalTarget, StringComparison.OrdinalIgnoreCase))
                return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX did not activate the requested saved .prt after reopen.", evidence);
            state.WorkPart = reopenedPart;
            state.PartPath = reopenedPart.FullPath;
            state.PartTag = reopenedPart.Tag.ToString();
            state.CanonicalPath = CanonicalPath(reopenedPart.FullPath);
            binding.PartPath = state.PartPath;
            binding.PartTag = state.PartTag;
            Dictionary<string, object> holeDefinitionsAfter = F14CapturePersistentHoleDefinitions(reopenedPart, null);
            Dictionary<string, object> booleanDefinitionsAfter = F15CaptureDefinitions(reopenedPart, null);
            Dictionary<string, object> blendDefinitionsAfter = F16CaptureDefinitions(reopenedPart, null);
            Dictionary<string, object> chamferDefinitionsAfter = F17CaptureDefinitions(reopenedPart, null);
            Dictionary<string, object> patternDefinitionsAfter = F18CaptureDefinitions(reopenedPart, null);
            Dictionary<string, object> mirrorDefinitionsAfter = F19CaptureDefinitions(reopenedPart, null);
            List<object> sweptDefinitionsAfter = F21PersistentDefinitions(reopenedPart, F21CaptureDefinitions(reopenedPart, null));
            SnapshotResult after = Capture(binding, state);
            List<object> geometryAfter = CaptureGeometryRecords(reopenedPart, false);
            Dictionary<string, object> checks = BuildSaveCopyChecks(before.Snapshot, after.Snapshot,
                geometryBefore, geometryAfter);
            List<object> sweepMappings;
            bool sweptDefinitionsMatch = F21PersistentDefinitionsMatch(sweptDefinitionsBefore, sweptDefinitionsAfter, out sweepMappings);
            evidence["native_swept_definitions_before"] = sweptDefinitionsBefore;
            evidence["native_swept_definitions_after"] = sweptDefinitionsAfter;
            evidence["native_swept_identity_mappings"] = sweepMappings;
            if (!sweptDefinitionsMatch) checks["history"] = VerificationCheck("MISMATCH", new Dictionary<string, object> { { "reason", "NATIVE_SWEPT_DEFINITIONS_CHANGED" }, { "prior_history_check", checks["history"] } });
            bool mirrorDefinitionsMatch = F16DefinitionsEqual(mirrorDefinitionsBefore, mirrorDefinitionsAfter);
            evidence["native_feature_mirror_definitions_before"] = mirrorDefinitionsBefore;
            evidence["native_feature_mirror_definitions_after"] = mirrorDefinitionsAfter;
            if (!mirrorDefinitionsMatch) checks["history"] = VerificationCheck("MISMATCH", new Dictionary<string, object> { { "reason", "NATIVE_FEATURE_MIRROR_DEFINITIONS_CHANGED" }, { "prior_history_check", checks["history"] } });
            bool patternDefinitionsMatch = F16DefinitionsEqual(patternDefinitionsBefore, patternDefinitionsAfter);
            evidence["native_feature_pattern_definitions_before"] = patternDefinitionsBefore;
            evidence["native_feature_pattern_definitions_after"] = patternDefinitionsAfter;
            if (!patternDefinitionsMatch) checks["history"] = VerificationCheck("MISMATCH", new Dictionary<string, object> { { "reason", "NATIVE_FEATURE_PATTERN_DEFINITIONS_CHANGED" }, { "prior_history_check", checks["history"] } });
            bool chamferDefinitionsMatch = F16DefinitionsEqual(chamferDefinitionsBefore, chamferDefinitionsAfter);
            evidence["native_chamfer_definitions_before"] = chamferDefinitionsBefore;
            evidence["native_chamfer_definitions_after"] = chamferDefinitionsAfter;
            if (!chamferDefinitionsMatch) checks["history"] = VerificationCheck("MISMATCH", new Dictionary<string, object> { { "reason", "NATIVE_CHAMFER_DEFINITIONS_CHANGED" }, { "prior_history_check", checks["history"] } });
            bool blendDefinitionsMatch = F16DefinitionsEqual(blendDefinitionsBefore, blendDefinitionsAfter);
            evidence["native_edge_blend_definitions_before"] = blendDefinitionsBefore;
            evidence["native_edge_blend_definitions_after"] = blendDefinitionsAfter;
            if (!blendDefinitionsMatch) checks["history"] = VerificationCheck("MISMATCH", new Dictionary<string, object> { { "reason", "NATIVE_EDGE_BLEND_DEFINITIONS_CHANGED" }, { "prior_history_check", checks["history"] } });
            bool booleanDefinitionsMatch = F15DefinitionsEqual(booleanDefinitionsBefore, booleanDefinitionsAfter);
            evidence["native_boolean_definitions_before"] = booleanDefinitionsBefore;
            evidence["native_boolean_definitions_after"] = booleanDefinitionsAfter;
            if (!booleanDefinitionsMatch)
                checks["history"] = VerificationCheck("MISMATCH", new Dictionary<string, object> { { "reason", "NATIVE_BOOLEAN_DEFINITIONS_CHANGED" }, { "prior_history_check", checks["history"] } });
            bool holeDefinitionsMatch = F14PersistentHoleDefinitionsEqual(holeDefinitionsBefore, holeDefinitionsAfter);
            evidence["native_hole_definitions_before"] = holeDefinitionsBefore;
            evidence["native_hole_definitions_after"] = holeDefinitionsAfter;
            if (!holeDefinitionsMatch)
                checks["history"] = VerificationCheck("MISMATCH", new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "reason", "NATIVE_HOLE_DEFINITIONS_CHANGED" }, { "prior_history_check", checks["history"] },
                    { "native_hole_definitions_before", holeDefinitionsBefore }, { "native_hole_definitions_after", holeDefinitionsAfter }
                });
            Dictionary<string, object> faceAssociationAuditAfter;
            try { faceAssociationAuditAfter = FaceAssociationPersistenceAudit(after.Snapshot); }
            catch (Exception auditException) { faceAssociationAuditAfter = FailedFaceAssociationPersistenceAudit(auditException); }
            evidence["face_association_persistence_after_reopen"] = faceAssociationAuditAfter;
            if (!String.Equals(Convert.ToString(faceAssociationAuditAfter["status"], CultureInfo.InvariantCulture),
                "VERIFIED", StringComparison.Ordinal))
            {
                object previousAssociationCheckValue;
                Dictionary<string, object> mismatchEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
                if (checks.TryGetValue("associations", out previousAssociationCheckValue))
                    mismatchEvidence.Add("round_trip_association_check", previousAssociationCheckValue);
                mismatchEvidence.Add("face_association_persistence", faceAssociationAuditAfter);
                mismatchEvidence.Add("reason", "UNSUPPORTED_ASSOCIATION_PERSISTENCE");
                checks["associations"] = VerificationCheck("MISMATCH", mismatchEvidence);
            }
            evidence["source_path"] = sourcePath;
            evidence["saved_path"] = state.PartPath;
            evidence["reopened"] = true;
            evidence["source_part_tag"] = before.Snapshot["identity"] is Dictionary<string, object>
                ? ((Dictionary<string, object>)before.Snapshot["identity"])["part_tag"] : null;
            evidence["reopened_part_tag"] = state.PartTag;
            evidence["round_trip_checks"] = checks;
            evidence["read_effects_after_reopen"] = after.ReadEffects;
            if (!String.Equals(Convert.ToString(faceAssociationAuditAfter["status"], CultureInfo.InvariantCulture),
                "VERIFIED", StringComparison.Ordinal))
            {
                binding.Identity = BuildIdentity(binding, reopenedPart, after.ReadEffects);
                return new M2SaveCopyResult
                {
                    State = "SAVED",
                    PartPath = state.PartPath,
                    PartTag = state.PartTag,
                    RevisionId = binding.RevisionId,
                    Identity = Clone(binding.Identity),
                    Reopened = true,
                    Checks = checks,
                    Evidence = evidence,
                    ErrorCode = "UNSUPPORTED_ASSOCIATION_PERSISTENCE",
                    ErrorMessage = "The saved copy reopened, but a Face-supported Sketch or DatumPlane is ambiguous after loading."
                };
            }
            binding.Identity = BuildIdentity(binding, reopenedPart, after.ReadEffects);
            return new M2SaveCopyResult
            {
                State = "SAVED",
                PartPath = state.PartPath,
                PartTag = state.PartTag,
                RevisionId = binding.RevisionId,
                Identity = Clone(binding.Identity),
                Reopened = true,
                Checks = checks,
                Evidence = evidence
            };
        }
        catch (Exception ex)
        {
            AddExceptionEvidence(evidence, "reopen", ex);
            return SaveCopyUnknown("SAVE_RESULT_UNKNOWN", "NX saved the requested copy but its reopened model state could not be verified.", evidence);
        }
    }

    public void Release(M2ModelBinding binding)
    {
        if (binding != null) binding.AdapterState = null;
    }

    private static Dictionary<string, object> VerificationCheck(string status, Dictionary<string, object> evidence)
    {
        Dictionary<string, object> check = new Dictionary<string, object>(StringComparer.Ordinal);
        check.Add("status", status);
        check.Add("evidence", evidence);
        return check;
    }

    private static void AddExceptionEvidence(Dictionary<string, object> evidence, string prefix, Exception exception)
    {
        evidence[prefix + "_exception_type"] = exception.GetType().Name;
        evidence[prefix + "_exception_message"] = exception.Message;
        PropertyInfo codeProperty = exception.GetType().GetProperty("ErrorCode");
        if (codeProperty != null)
        {
            try { evidence[prefix + "_nx_error_code"] = Convert.ToString(codeProperty.GetValue(exception, null), CultureInfo.InvariantCulture); }
            catch (Exception) { }
        }
    }

    private static M2PartVerificationResult VerificationFailure(string revisionId, string reason)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("reason", reason);
        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        checks.Add("expression", VerificationCheck("MISMATCH", Clone(evidence)));
        checks.Add("geometry", VerificationCheck("MISMATCH", Clone(evidence)));
        checks.Add("history", VerificationCheck("MISMATCH", Clone(evidence)));
        checks.Add("associations", VerificationCheck("MISMATCH", Clone(evidence)));
        return new M2PartVerificationResult
        {
            Verified = false,
            RevisionId = revisionId,
            Checks = checks,
            ErrorCode = "PART_VERIFICATION_FAILED",
            ErrorMessage = reason
        };
    }

    private static bool SelectedHistoryMatches(Part part, Sketch sketch, string expectedFeatureId,
        out Dictionary<string, object> evidence)
    {
        Feature feature = sketch == null ? null : sketch.Feature;
        Feature[] allFeatures = part.Features.GetFeatures();
        if (allFeatures == null) throw new M2ModelException("MODEL_READ_FAILED", "feature history returned null during verification");
        string featureId = feature == null ? null : RequiredJournalId(feature, "sketch feature");
        int matches = 0;
        foreach (Feature candidate in allFeatures)
        {
            if (candidate != null && String.Equals(RequiredJournalId(candidate, "feature history"), featureId, StringComparison.Ordinal))
                matches++;
        }
        List<string> parentIds = feature == null ? new List<string>() : GetJournalIds(feature.GetParents(), "history parent");
        List<string> childIds = feature == null ? new List<string>() : GetJournalIds(feature.GetChildren(), "history child");
        Expression[] expressions = feature == null ? new Expression[0] : feature.GetExpressions();
        if (expressions == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch feature expressions returned null during verification");
        List<string> expressionNames = new List<string>();
        foreach (Expression expression in expressions)
        {
            if (expression == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch feature history contains a null expression");
            expressionNames.Add(expression.Name);
        }
        expressionNames.Sort(StringComparer.Ordinal);

        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("sketch_id", sketch == null ? null : RequiredJournalId(sketch, "sketch"));
        evidence.Add("feature_id", featureId);
        evidence.Add("expected_feature_id", expectedFeatureId);
        evidence.Add("feature_type", feature == null ? null : feature.FeatureType);
        evidence.Add("parents", ObjectStrings(parentIds));
        evidence.Add("children", ObjectStrings(childIds));
        evidence.Add("expressions", ObjectStrings(expressionNames));
        evidence.Add("feature_count", allFeatures.Length);
        evidence.Add("feature_in_part_count", matches);
        return feature != null && matches == 1 && String.Equals(featureId, expectedFeatureId, StringComparison.Ordinal);
    }

    private static M2SaveCopyResult SaveCopyRejected(string code, string message,
        Dictionary<string, object> evidence)
    {
        M2SaveCopyResult result = new M2SaveCopyResult();
        result.State = "UNCHANGED";
        result.Reopened = false;
        result.Checks = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Evidence = evidence;
        result.ErrorCode = code;
        result.ErrorMessage = message;
        return result;
    }

    private static M2SaveCopyResult SaveCopyUnknown(string code, string message,
        Dictionary<string, object> evidence)
    {
        M2SaveCopyResult result = new M2SaveCopyResult();
        result.State = "UNKNOWN";
        result.Reopened = false;
        result.Checks = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Evidence = evidence;
        result.ErrorCode = code;
        result.ErrorMessage = message;
        return result;
    }

    private static Part ReopenSavedCopy(Session session, Part current, string targetPath)
    {
        Part active;
        if (String.Equals(CanonicalPath(current.FullPath), CanonicalPath(targetPath), StringComparison.OrdinalIgnoreCase))
        {
            PartReopenReport report = null;
            try
            {
                BasePart reloaded = current.Reopen(BasePart.CloseWholeTree.False,
                    BasePart.CloseModified.CloseModified, null, out report);
                if (reloaded == null || (report != null && report.CouldNotResetWorkPart))
                    throw new M2ModelException("SAVE_COPY_REOPEN_FAILED", "NX did not reset the saved copy as the Work Part.");
                active = reloaded as Part;
            }
            finally
            {
                if (report != null) report.Dispose();
            }
        }
        else
        {
            PartLoadStatus loadStatus = null;
            BasePart opened;
            try
            {
                opened = session.Parts.OpenBaseDisplay(targetPath, out loadStatus);
                if (opened == null || (loadStatus != null && loadStatus.NumberUnloadedParts != 0))
                    throw new M2ModelException("SAVE_COPY_REOPEN_FAILED", "NX did not fully open the saved copy.");
            }
            finally
            {
                if (loadStatus != null) loadStatus.Dispose();
            }

            PartLoadStatus displayStatus = null;
            try
            {
                object displayResult = session.Parts.SetActiveDisplay(opened, DisplayPartOption.ReplaceExisting,
                    PartDisplayPartWorkPartOption.SameAsDisplay, out displayStatus);
                if (displayResult == null || !String.Equals(displayResult.ToString(), "OK", StringComparison.Ordinal) ||
                    (displayStatus != null && displayStatus.NumberUnloadedParts != 0))
                    throw new M2ModelException("SAVE_COPY_REOPEN_FAILED", "NX could not make the saved copy the active Work Part.");
            }
            finally
            {
                if (displayStatus != null) displayStatus.Dispose();
            }
            active = session.Parts.Work;
        }
        if (active == null) active = session.Parts.Work;
        if (active == null || !String.Equals(CanonicalPath(active.FullPath), CanonicalPath(targetPath), StringComparison.OrdinalIgnoreCase))
            throw new M2ModelException("SAVE_COPY_REOPEN_FAILED", "the active NX Work Part path does not match the saved copy.");
        if (!Object.ReferenceEquals(session.Parts.Work, active)) session.Parts.SetWork(active);
        return active;
    }

    private static Dictionary<string, object> BuildSaveCopyChecks(Dictionary<string, object> before,
        Dictionary<string, object> after, List<object> geometryBefore, List<object> geometryAfter)
    {
        List<object> beforeExpressions = SnapshotList(before, "expressions");
        List<object> afterExpressions = SnapshotList(after, "expressions");
        List<object> beforeFeatures = SaveCopyHistoryProjection(before);
        List<object> afterFeatures = SaveCopyHistoryProjection(after);
        List<object> beforeAssociations = AssociationProjection(before);
        List<object> afterAssociations = AssociationProjection(after);
        List<object> beforeBodies = BodyMeasurementProjection(before);
        List<object> afterBodies = BodyMeasurementProjection(after);
        Dictionary<string, object> bodyEvidence;
        bool bodiesEqual = BodyMeasurementRecordsEqual(beforeBodies, afterBodies, out bodyEvidence);
        bool expressionEqual = ExpressionRecordsEqual(beforeExpressions, afterExpressions);
        bool reopenedBodiesConsistent = M7SnapshotBodiesConsistent(after);
        List<object> spatialBefore=F20PersistentRecords(SnapshotList(before,"spatial_curves"));
        List<object> spatialAfter=F20PersistentRecords(SnapshotList(after,"spatial_curves"));
        List<object> spatialMappings;
        bool spatialEqual=F20PersistentRecordsMatch(spatialBefore,spatialAfter,out spatialMappings);
        bool geometryEqual = SemanticValuesEqual(geometryBefore, geometryAfter, "geometry") && bodiesEqual && reopenedBodiesConsistent && spatialEqual;
        bool historyEqual = SemanticValuesEqual(beforeFeatures, afterFeatures, "history");
        bool associationsEqual = SemanticValuesEqual(beforeAssociations, afterAssociations, "associations");

        Dictionary<string, object> expressionEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        expressionEvidence.Add("before_count", beforeExpressions.Count);
        expressionEvidence.Add("after_count", afterExpressions.Count);
        expressionEvidence.Add("before", ExpressionProjection(beforeExpressions));
        expressionEvidence.Add("after", ExpressionProjection(afterExpressions));
        Dictionary<string, object> geometryEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        geometryEvidence.Add("before_sketch_geometry", geometryBefore);
        geometryEvidence.Add("after_sketch_geometry", geometryAfter);
        geometryEvidence.Add("spatial_curves_before", spatialBefore);
        geometryEvidence.Add("spatial_curves_after", spatialAfter);
        geometryEvidence.Add("spatial_curves_match", spatialEqual);
        geometryEvidence.Add("spatial_curve_identity_mapping", spatialMappings);
        geometryEvidence.Add("before_sketch_count", geometryBefore.Count);
        geometryEvidence.Add("after_sketch_count", geometryAfter.Count);
        geometryEvidence.Add("body_measurements", bodyEvidence);
        geometryEvidence.Add("reopened_bodies_consistent", reopenedBodiesConsistent);
        Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        historyEvidence.Add("before", beforeFeatures);
        historyEvidence.Add("after", afterFeatures);
        historyEvidence.Add("before_feature_count", SnapshotList(before, "features").Count);
        historyEvidence.Add("after_feature_count", SnapshotList(after, "features").Count);
        Dictionary<string, object> associationEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        associationEvidence.Add("before", beforeAssociations);
        associationEvidence.Add("after", afterAssociations);

        Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal);
        checks.Add("expression", VerificationCheck(expressionEqual ? "VERIFIED" : "MISMATCH", expressionEvidence));
        checks.Add("geometry", VerificationCheck(geometryEqual ? "VERIFIED" : "MISMATCH", geometryEvidence));
        checks.Add("history", VerificationCheck(historyEqual ? "VERIFIED" : "MISMATCH", historyEvidence));
        checks.Add("associations", VerificationCheck(associationsEqual ? "VERIFIED" : "MISMATCH", associationEvidence));
        return checks;
    }

    private static Dictionary<string, object> FaceAssociationPersistenceAudit(Dictionary<string, object> snapshot)
    {
        Dictionary<string, Dictionary<string, int>> faceCountsByBody =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        HashSet<string> ambiguousBodyIds = new HashSet<string>(StringComparer.Ordinal);
        bool faceInventoryUnverified = false;
        bool repeatedFaceJournalIdObserved = false;
        foreach (object bodyValue in SnapshotList(snapshot, "bodies"))
        {
            Dictionary<string, object> body = bodyValue as Dictionary<string, object>;
            if (body == null) continue;
            string bodyId = body.ContainsKey("journal_id")
                ? Convert.ToString(body["journal_id"], CultureInfo.InvariantCulture) : null;
            if (String.IsNullOrEmpty(bodyId)) continue;
            List<object> refs = body.ContainsKey("face_refs") ? body["face_refs"] as List<object> : null;
            List<object> ids = body.ContainsKey("face_ids") ? body["face_ids"] as List<object> : null;
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            HashSet<string> faceTags = new HashSet<string>(StringComparer.Ordinal);
            string observedBodyTag = null;
            bool validInventory = refs != null && ids != null && refs.Count == ids.Count;
            if (refs != null)
            {
                foreach (object refValue in refs)
                {
                    Dictionary<string, object> faceRef = refValue as Dictionary<string, object>;
                    if (faceRef == null) { validInventory = false; continue; }
                    object faceIdValue, faceTagValue, refBodyId, refBodyTag;
                    if (!faceRef.TryGetValue("face_journal_id", out faceIdValue) ||
                        !faceRef.TryGetValue("face_tag", out faceTagValue) ||
                        !faceRef.TryGetValue("body_id", out refBodyId) ||
                        !faceRef.TryGetValue("body_tag", out refBodyTag) ||
                        String.IsNullOrEmpty(Convert.ToString(faceIdValue, CultureInfo.InvariantCulture)) ||
                        String.IsNullOrEmpty(Convert.ToString(faceTagValue, CultureInfo.InvariantCulture)) ||
                        String.IsNullOrEmpty(Convert.ToString(refBodyTag, CultureInfo.InvariantCulture)) ||
                        !String.Equals(Convert.ToString(refBodyId, CultureInfo.InvariantCulture), bodyId, StringComparison.Ordinal))
                    {
                        validInventory = false;
                        continue;
                    }
                    string bodyTag = Convert.ToString(refBodyTag, CultureInfo.InvariantCulture);
                    if (observedBodyTag == null) observedBodyTag = bodyTag;
                    else if (!String.Equals(observedBodyTag, bodyTag, StringComparison.Ordinal)) validInventory = false;
                    string faceTag = Convert.ToString(faceTagValue, CultureInfo.InvariantCulture);
                    if (!faceTags.Add(faceTag)) validInventory = false;
                    string faceId = Convert.ToString(faceIdValue, CultureInfo.InvariantCulture);
                    int count;
                    counts.TryGetValue(faceId, out count);
                    counts[faceId] = count + 1;
                }
            }
            if (ids != null)
            {
                Dictionary<string, int> listedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (object idValue in ids)
                {
                    string faceId = Convert.ToString(idValue, CultureInfo.InvariantCulture);
                    if (String.IsNullOrEmpty(faceId)) { validInventory = false; continue; }
                    int count;
                    listedCounts.TryGetValue(faceId, out count);
                    listedCounts[faceId] = count + 1;
                }
                if (listedCounts.Count != counts.Count) validInventory = false;
                foreach (KeyValuePair<string, int> item in listedCounts)
                {
                    int refCount;
                    if (!counts.TryGetValue(item.Key, out refCount) || refCount != item.Value) validInventory = false;
                }
            }
            if (!validInventory) faceInventoryUnverified = true;
            foreach (KeyValuePair<string, int> faceCount in counts)
                if (faceCount.Value > 1)
                {
                    repeatedFaceJournalIdObserved = true;
                    break;
                }
            if (faceCountsByBody.ContainsKey(bodyId)) ambiguousBodyIds.Add(bodyId);
            else faceCountsByBody.Add(bodyId, validInventory ? counts : null);
        }

        List<object> faceAssociations = new List<object>();
        List<object> sketches = SnapshotList(snapshot, "sketches");
        foreach (object sketchValue in sketches)
        {
            Dictionary<string, object> sketch = sketchValue as Dictionary<string, object>;
            if (sketch == null) continue;
            object supportValue;
            Dictionary<string, object> support = sketch.TryGetValue("support_association", out supportValue)
                ? supportValue as Dictionary<string, object> : null;
            if (support != null)
            {
                object supportKindValue, supportFaceIdValue;
                string supportKind = support.TryGetValue("support_kind", out supportKindValue)
                    ? Convert.ToString(supportKindValue, CultureInfo.InvariantCulture) : null;
                bool hasFaceId = support.TryGetValue("support_face_id", out supportFaceIdValue) &&
                    !String.IsNullOrEmpty(Convert.ToString(supportFaceIdValue, CultureInfo.InvariantCulture));
                if (String.Equals(supportKind, "WORLD_FRAME", StringComparison.Ordinal) ||
                    String.Equals(supportKind, "DATUM_PLANE", StringComparison.Ordinal)) continue;
                if (!hasFaceId && !String.Equals(supportKind, "FACE", StringComparison.Ordinal))
                {
                    if (repeatedFaceJournalIdObserved || faceInventoryUnverified)
                        AddUnverifiedFaceAssociation(faceAssociations, "SKETCH_SUPPORT",
                            Convert.ToString(sketch["journal_id"], CultureInfo.InvariantCulture),
                            "SKETCH_SUPPORT_ASSOCIATION_UNAVAILABLE_WHILE_FACE_IDENTITY_IS_AMBIGUOUS");
                    continue;
                }
                AppendFaceAssociationPersistenceRecord(faceAssociations, faceCountsByBody, ambiguousBodyIds,
                    "SKETCH_SUPPORT", Convert.ToString(sketch["journal_id"], CultureInfo.InvariantCulture),
                    support, "support_face_id", "support_body_id", "body_id");
            }
            else if (repeatedFaceJournalIdObserved || faceInventoryUnverified)
                AddUnverifiedFaceAssociation(faceAssociations, "SKETCH_SUPPORT",
                    Convert.ToString(sketch["journal_id"], CultureInfo.InvariantCulture),
                    "SKETCH_SUPPORT_ASSOCIATION_MISSING_WHILE_FACE_IDENTITY_IS_AMBIGUOUS");
        }

        List<object> features = SnapshotList(snapshot, "features");
        foreach (object featureValue in features)
        {
            Dictionary<string, object> feature = featureValue as Dictionary<string, object>;
            if (feature == null) continue;
            object datumValue;
            Dictionary<string, object> datum = feature.TryGetValue("datum_plane", out datumValue)
                ? datumValue as Dictionary<string, object> : null;
            if (datum != null)
            {
                object supportKindValue, supportFaceIdValue;
                string supportKind = datum.TryGetValue("support_kind", out supportKindValue)
                    ? Convert.ToString(supportKindValue, CultureInfo.InvariantCulture) : null;
                bool hasFaceId = datum.TryGetValue("support_face_id", out supportFaceIdValue) &&
                    !String.IsNullOrEmpty(Convert.ToString(supportFaceIdValue, CultureInfo.InvariantCulture));
                if (!hasFaceId && !String.Equals(supportKind, "FACE", StringComparison.Ordinal))
                {
                    if ((repeatedFaceJournalIdObserved || faceInventoryUnverified) &&
                        !String.Equals(supportKind, "DATUM_PLANE", StringComparison.Ordinal) &&
                        !String.Equals(supportKind, "WORLD_FRAME", StringComparison.Ordinal) &&
                        !IsVerifiedTwoLineDatumPlane(datum))
                        AddUnverifiedFaceAssociation(faceAssociations, "DATUM_PLANE_SUPPORT",
                            Convert.ToString(feature["journal_id"], CultureInfo.InvariantCulture),
                            "DATUM_PLANE_SUPPORT_ASSOCIATION_UNAVAILABLE_WHILE_FACE_IDENTITY_IS_AMBIGUOUS");
                    continue;
                }
                AppendFaceAssociationPersistenceRecord(faceAssociations, faceCountsByBody, ambiguousBodyIds,
                    "DATUM_PLANE_SUPPORT", Convert.ToString(feature["journal_id"], CultureInfo.InvariantCulture),
                    datum, "support_face_id", "support_body_id", "body_id");
            }
            else if (String.Equals(Convert.ToString(feature["feature_type"], CultureInfo.InvariantCulture),
                "DATUM_PLANE", StringComparison.Ordinal) && (repeatedFaceJournalIdObserved || faceInventoryUnverified))
                AddUnverifiedFaceAssociation(faceAssociations, "DATUM_PLANE_SUPPORT",
                    Convert.ToString(feature["journal_id"], CultureInfo.InvariantCulture),
                    "DATUM_PLANE_DEFINITION_UNAVAILABLE_WHILE_FACE_IDENTITY_IS_AMBIGUOUS");
        }

        int unverified = 0;
        foreach (object associationValue in faceAssociations)
        {
            Dictionary<string, object> association = associationValue as Dictionary<string, object>;
            if (association != null && !String.Equals(Convert.ToString(association["status"], CultureInfo.InvariantCulture),
                "VERIFIED", StringComparison.Ordinal)) unverified++;
        }
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("status", unverified == 0 ? "VERIFIED" : "MISMATCH");
        result.Add("face_association_count", faceAssociations.Count);
        result.Add("unverified_face_association_count", unverified);
        result.Add("identification_method", "face_journal_id multiplicity within body.face_refs; session face_tag is not compared across loads");
        result.Add("associations", faceAssociations);
        return result;
    }

    private static bool IsVerifiedTwoLineDatumPlane(Dictionary<string, object> datum)
    {
        if (datum == null) return false;
        object value;
        if (!datum.TryGetValue("mode", out value) ||
            !String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "two_lines", StringComparison.Ordinal)) return false;
        if (!datum.TryGetValue("native_method", out value) ||
            !String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "TwoLines", StringComparison.Ordinal)) return false;
        if (!datum.TryGetValue("native_reference_geometry", out value) || !(value is bool) || !(bool)value) return false;
        if (!datum.TryGetValue("native_method_matches", out value) || !(value is bool) || !(bool)value) return false;
        if (!datum.TryGetValue("parent_reference_matches", out value) || !(value is bool) || !(bool)value) return false;
        object linesValue, line1Value, line2Value;
        List<object> lineIds = datum.TryGetValue("native_geometry_line_ids", out linesValue)
            ? linesValue as List<object> : null;
        if (lineIds == null || lineIds.Count != 2) return false;
        string first = Convert.ToString(lineIds[0], CultureInfo.InvariantCulture);
        string second = Convert.ToString(lineIds[1], CultureInfo.InvariantCulture);
        if (String.IsNullOrEmpty(first) || String.IsNullOrEmpty(second) ||
            String.Equals(first, second, StringComparison.Ordinal)) return false;
        if (!datum.TryGetValue("line1_id", out line1Value) ||
            !datum.TryGetValue("line2_id", out line2Value)) return false;
        return String.Equals(first, Convert.ToString(line1Value, CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
            String.Equals(second, Convert.ToString(line2Value, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static void AddUnverifiedFaceAssociation(List<object> records,
        string associationKind, string associationId, string reason)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
        record.Add("association_kind", associationKind);
        record.Add("association_id", associationId);
        record.Add("support_body_id", null);
        record.Add("support_face_id", null);
        record.Add("matching_face_count", 0);
        record.Add("status", "MISMATCH");
        record.Add("reason", reason);
        records.Add(record);
    }

    private static List<object> SaveCopyHistoryProjection(Dictionary<string, object> snapshot)
    {
        List<object> projection = DeepCloneValue(HistoryProjection(snapshot)) as List<object>;
        if (projection == null || projection.Count == 0)
            throw new M2ModelException("MODEL_READ_FAILED", "SaveCopy history projection is unavailable");
        List<object> features = projection[0] as List<object>;
        if (features == null)
            throw new M2ModelException("MODEL_READ_FAILED", "SaveCopy feature history projection is unavailable");
        foreach (object featureValue in features)
        {
            Dictionary<string, object> feature = featureValue as Dictionary<string, object>;
            if (feature == null) continue;
            object datumValue;
            Dictionary<string, object> datum = feature.TryGetValue("datum_plane", out datumValue)
                ? datumValue as Dictionary<string, object> : null;
            if (datum != null) datum.Remove("support_face_tag");
        }
        return projection;
    }

    private static Dictionary<string, object> FailedFaceAssociationPersistenceAudit(Exception exception)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("status", "MISMATCH");
        result.Add("face_association_count", 0);
        result.Add("unverified_face_association_count", -1);
        result.Add("identification_method", "face_journal_id multiplicity within body.face_refs; session face_tag is not compared across loads");
        result.Add("reason", "FACE_ASSOCIATION_PERSISTENCE_AUDIT_FAILED");
        result.Add("audit_exception_type", exception == null ? null : exception.GetType().Name);
        result.Add("associations", new List<object>());
        return result;
    }

    private static void AppendFaceAssociationPersistenceRecord(List<object> records,
        Dictionary<string, Dictionary<string, int>> faceCountsByBody, HashSet<string> ambiguousBodyIds,
        string associationKind, string associationId, Dictionary<string, object> association,
        string faceIdKey, string bodyIdKey, string alternateBodyIdKey)
    {
        object faceIdValue, supportKindValue;
        bool hasFaceId = association.TryGetValue(faceIdKey, out faceIdValue) &&
            !String.IsNullOrEmpty(Convert.ToString(faceIdValue, CultureInfo.InvariantCulture));
        bool hasSupportKind = association.TryGetValue("support_kind", out supportKindValue);
        string supportKind = hasSupportKind
            ? Convert.ToString(supportKindValue, CultureInfo.InvariantCulture) : null;
        bool isFaceSupport = String.Equals(supportKind, "FACE", StringComparison.Ordinal) ||
            (hasFaceId && String.IsNullOrEmpty(supportKind));
        if (!hasFaceId && !isFaceSupport) return;

        object bodyIdValue;
        if (!association.TryGetValue(bodyIdKey, out bodyIdValue) &&
            (alternateBodyIdKey == null || !association.TryGetValue(alternateBodyIdKey, out bodyIdValue)))
            bodyIdValue = null;
        string bodyId = Convert.ToString(bodyIdValue, CultureInfo.InvariantCulture);
        string faceId = hasFaceId ? Convert.ToString(faceIdValue, CultureInfo.InvariantCulture) : null;
        string reason = null;
        int matchingFaceCount = 0;
        if (!isFaceSupport) reason = "FACE_ID_WITH_NON_FACE_SUPPORT_KIND";
        else if (String.IsNullOrEmpty(faceId)) reason = "SUPPORT_FACE_ID_MISSING";
        else if (String.IsNullOrEmpty(bodyId)) reason = "SUPPORT_BODY_ID_MISSING";
        else if (ambiguousBodyIds.Contains(bodyId)) reason = "SUPPORT_BODY_ID_AMBIGUOUS";
        else
        {
            Dictionary<string, int> counts;
            if (!faceCountsByBody.TryGetValue(bodyId, out counts)) reason = "SUPPORT_BODY_NOT_FOUND";
            else if (counts == null) reason = "BODY_FACE_REFS_UNVERIFIED";
            else if (!counts.TryGetValue(faceId, out matchingFaceCount) || matchingFaceCount == 0)
                reason = "SUPPORT_FACE_NOT_FOUND";
            else if (matchingFaceCount != 1) reason = "SUPPORT_FACE_JOURNAL_ID_AMBIGUOUS";
        }

        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
        record.Add("association_kind", associationKind);
        record.Add("association_id", associationId);
        record.Add("support_body_id", bodyId);
        record.Add("support_face_id", faceId);
        record.Add("matching_face_count", matchingFaceCount);
        record.Add("status", reason == null ? "VERIFIED" : "MISMATCH");
        if (reason != null) record.Add("reason", reason);
        records.Add(record);
    }

    private static List<object> BodyMeasurementProjection(Dictionary<string, object> snapshot)
    {
        List<object> bodies = SnapshotList(snapshot, "bodies");
        List<object> result = new List<object>();
        foreach (object value in bodies)
        {
            Dictionary<string, object> source = value as Dictionary<string, object>;
            if (source == null) throw new M2ModelException("MODEL_READ_FAILED", "body measurement evidence contains an invalid record");
            Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
            record.Add("journal_id", source["journal_id"]);
            record.Add("is_solid", source["is_solid"]);
            record.Add("volume_mm3", source["volume_mm3"]);
            record.Add("bbox_mm", source["bbox_mm"]);
            record.Add("measurement_status", source["measurement_status"]);
            result.Add(record);
        }
        result.Sort(delegate(object leftValue, object rightValue)
        {
            Dictionary<string, object> left = leftValue as Dictionary<string, object>;
            Dictionary<string, object> right = rightValue as Dictionary<string, object>;
            return String.CompareOrdinal(Convert.ToString(left["journal_id"], CultureInfo.InvariantCulture),
                Convert.ToString(right["journal_id"], CultureInfo.InvariantCulture));
        });
        return result;
    }

    private static bool BodyMeasurementRecordsEqual(List<object> before, List<object> after,
        out Dictionary<string, object> evidence)
    {
        bool same = before != null && after != null && before.Count == after.Count;
        if (same)
        {
            for (int i = 0; i < before.Count; i++)
            {
                Dictionary<string, object> left = before[i] as Dictionary<string, object>;
                Dictionary<string, object> right = after[i] as Dictionary<string, object>;
                if (left == null || right == null ||
                    !String.Equals(Convert.ToString(left["journal_id"], CultureInfo.InvariantCulture), Convert.ToString(right["journal_id"], CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    !Object.Equals(left["is_solid"], right["is_solid"]) ||
                    !String.Equals(Convert.ToString(left["measurement_status"], CultureInfo.InvariantCulture), Convert.ToString(right["measurement_status"], CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    !NullableNumberEqual(left["volume_mm3"], right["volume_mm3"]) ||
                    !NumericListEqual(left["bbox_mm"] as List<object>, right["bbox_mm"] as List<object>))
                {
                    same = false;
                    break;
                }
            }
        }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("status", same ? "VERIFIED" : "MISMATCH");
        evidence.Add("before", before ?? new List<object>());
        evidence.Add("after", after ?? new List<object>());
        evidence.Add("body_count_before", before == null ? 0 : before.Count);
        evidence.Add("body_count_after", after == null ? 0 : after.Count);
        evidence.Add("tolerance", "max(1e-7, max(|a|,|b|)*1e-9) in mm and mm^3");
        return same;
    }

    private static bool NullableNumberEqual(object left, object right)
    {
        if (left == null || right == null) return left == null && right == null;
        double a, b;
        return TryFiniteNumber(left, out a) && TryFiniteNumber(right, out b) && NearlyEqual(a, b);
    }

    private static bool NumericListEqual(List<object> left, List<object> right)
    {
        if (left == null || right == null) return left == null && right == null;
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!NullableNumberEqual(left[i], right[i])) return false;
        return true;
    }

    private static bool TryFiniteNumber(object value, out double number)
    {
        number = 0.0;
        if (value is double) { number = (double)value; return !Double.IsNaN(number) && !Double.IsInfinity(number); }
        if (value is float) { number = (float)value; return !Double.IsNaN(number) && !Double.IsInfinity(number); }
        return value is string && Double.TryParse((string)value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
            !Double.IsNaN(number) && !Double.IsInfinity(number);
    }

    private static List<object> ExpressionProjection(List<object> expressions)
    {
        List<object> projected = new List<object>();
        foreach (object item in expressions)
        {
            Dictionary<string, object> record = item as Dictionary<string, object>;
            if (record == null) throw new M2ModelException("MODEL_READ_FAILED", "expression evidence contains an invalid record");
            Dictionary<string, object> current = new Dictionary<string, object>(StringComparer.Ordinal);
            string[] keys = new string[] { "name", "right_hand_side", "status", "value", "unit" };
            foreach (string key in keys)
            {
                object value;
                if (!record.TryGetValue(key, out value)) throw new M2ModelException("MODEL_READ_FAILED", "expression evidence is missing " + key);
                current.Add(key, value);
            }
            object type;
            if (record.TryGetValue("expression_type", out type)) current.Add("expression_type", type);
            projected.Add(current);
        }
        return projected;
    }

    private static List<object> HistoryProjection(Dictionary<string, object> snapshot)
    {
        List<object> features = SnapshotList(snapshot, "features");
        List<object> dependencies = SnapshotList(snapshot, "dependencies");
        List<object> sketches = SnapshotList(snapshot, "sketches");
        List<object> sketchFeatures = new List<object>();
        foreach (object value in sketches)
        {
            Dictionary<string, object> sketch = value as Dictionary<string, object>;
            if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch history contains an invalid record");
            Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
            record.Add("journal_id", sketch["journal_id"]);
            record.Add("feature_journal_id", sketch["feature_journal_id"]);
            sketchFeatures.Add(record);
        }
        List<object> result = new List<object>();
        result.Add(features);
        result.Add(dependencies);
        result.Add(sketchFeatures);
        return result;
    }

    private static List<object> AssociationProjection(Dictionary<string, object> snapshot)
    {
        List<object> sketches = SnapshotList(snapshot, "sketches");
        List<object> projected = new List<object>();
        foreach (object value in sketches)
        {
            Dictionary<string, object> sketch = value as Dictionary<string, object>;
            if (sketch == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch association evidence contains an invalid record");
            List<object> constraints = sketch["constraints"] as List<object>;
            if (constraints == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch constraints are unavailable");
            List<object> relations = new List<object>();
            foreach (object constraintValue in constraints)
            {
                Dictionary<string, object> constraint = constraintValue as Dictionary<string, object>;
                if (constraint == null) throw new M2ModelException("MODEL_READ_FAILED", "constraint association evidence contains an invalid record");
                Dictionary<string, object> relation = new Dictionary<string, object>(StringComparer.Ordinal);
                string[] keys = new string[] { "journal_id", "constraint_type", "class", "dimension_state", "is_driving", "expression_name", "geometry_refs" };
                foreach (string key in keys)
                {
                    object field;
                    if (constraint.TryGetValue(key, out field)) relation.Add(key, field);
                }
                relations.Add(relation);
            }
            Dictionary<string, object> sketchRelations = new Dictionary<string, object>(StringComparer.Ordinal);
            sketchRelations.Add("sketch_id", sketch["journal_id"]);
            sketchRelations.Add("constraints", relations);
            sketchRelations.Add("parent_feature_ids", sketch.ContainsKey("parent_feature_ids") ? sketch["parent_feature_ids"] : new List<object>());
            sketchRelations.Add("frame", new Dictionary<string, object>(StringComparer.Ordinal) {
                { "origin", sketch["origin"] }, { "normal", sketch["normal"] },
                { "x_axis", sketch["x_axis"] }, { "y_axis", sketch["y_axis"] }
            });
            object supportValue;
            Dictionary<string, object> support = sketch.TryGetValue("support_association", out supportValue)
                ? supportValue as Dictionary<string, object> : null;
            sketchRelations.Add("support_association", M3SupportIdentityProjection(support));
            projected.Add(sketchRelations);
        }
        return projected;
    }

    private static Dictionary<string, object> M3SupportIdentityProjection(Dictionary<string, object> support)
    {
        if (support == null) return null;
        Dictionary<string, object> projected = new Dictionary<string, object>(StringComparer.Ordinal);
        string[] keys = new string[] { "support_kind", "native", "support_face_id", "support_body_id",
            "datum_plane_id", "native_feature_parent" };
        foreach (string key in keys)
        {
            object value;
            if (support.TryGetValue(key, out value)) projected.Add(key, value);
        }
        object fixedValue;
        if (support.TryGetValue("native_fixed_datum_definition", out fixedValue))
        {
            Dictionary<string, object> definition = fixedValue as Dictionary<string, object>;
            if (definition == null) throw new M2ModelException("MODEL_READ_FAILED", "fixed sketch support definition is invalid");
            projected.Add("native_fixed_datum_definition", F21FixedSupportProjection(definition));
        }
        return projected;
    }

    // Values and source kinds, rather than session-local Tag/JID, are compared
    // across SaveCopy. The core separately proves the exact native parent graph.
    private static Dictionary<string, object> F21FixedSupportProjection(Dictionary<string, object> definition)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (string key in new string[] { "associative", "origin", "x", "y", "z", "matrix", "scale" })
        {
            object value;
            if (!definition.TryGetValue(key, out value)) throw new M2ModelException("MODEL_READ_FAILED", "fixed sketch support field is missing: " + key);
            result.Add(key, F21ProjectionValue(value));
        }
        Dictionary<string, object> sourceMap = definition["sources"] as Dictionary<string, object>;
        if (sourceMap == null || sourceMap.Count != 4) throw new M2ModelException("MODEL_READ_FAILED", "fixed sketch support source set is incomplete");
        Dictionary<string, object> byKind = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (object sourceValue in sourceMap.Values)
        {
            Dictionary<string, object> source = sourceValue as Dictionary<string, object>;
            string kind = source == null ? null : source["kind"] as string;
            if (String.IsNullOrEmpty(kind) || byKind.ContainsKey(kind)) throw new M2ModelException("MODEL_READ_FAILED", "fixed sketch support source types are ambiguous");
            Dictionary<string, object> normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> field in source)
                if (!String.Equals(field.Key, "tag", StringComparison.Ordinal)) normalized.Add(field.Key, F21ProjectionValue(field.Value));
            byKind.Add(kind, normalized);
        }
        result.Add("sources", byKind);
        List<object> sourceComponents = definition["components"] as List<object>;
        if (sourceComponents == null || sourceComponents.Count != 3) throw new M2ModelException("MODEL_READ_FAILED", "fixed sketch support components are incomplete");
        List<object> components = new List<object>();
        foreach (object value in sourceComponents)
        {
            object[] row = value as object[];
            if (row == null || row.Length != 6) throw new M2ModelException("MODEL_READ_FAILED", "fixed sketch support component is invalid");
            components.Add(new List<object> { F21ProjectionValue(row[1]), F21ProjectionValue(row[2]), F21ProjectionValue(row[4]), F21ProjectionValue(row[5]) });
        }
        result.Add("components", components);
        return result;
    }

    private static object F21ProjectionValue(object value)
    {
        if (value == null || value is string) return value;
        System.Collections.IEnumerable sequence = value as System.Collections.IEnumerable;
        if (sequence != null)
        {
            List<object> result = new List<object>();
            foreach (object item in sequence) result.Add(F21ProjectionValue(item));
            return result;
        }
        return value;
    }

    private static List<object> SnapshotList(Dictionary<string, object> snapshot, string key)
    {
        object value;
        List<object> list = snapshot != null && snapshot.TryGetValue(key, out value) ? value as List<object> : null;
        if (list == null) throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot is missing " + key);
        return list;
    }

    private static bool ExpressionRecordsEqual(List<object> left, List<object> right)
    {
        List<object> a = ExpressionProjection(left);
        List<object> b = ExpressionProjection(right);
        if (a.Count != b.Count) return false;
        Dictionary<string, Dictionary<string, object>> byName = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (object item in b)
        {
            Dictionary<string, object> expression = item as Dictionary<string, object>;
            string name = expression["name"] as string;
            if (String.IsNullOrEmpty(name) || byName.ContainsKey(name)) return false;
            byName.Add(name, expression);
        }
        foreach (object item in a)
        {
            Dictionary<string, object> before = item as Dictionary<string, object>;
            string name = before["name"] as string;
            Dictionary<string, object> after;
            if (String.IsNullOrEmpty(name) || !byName.TryGetValue(name, out after)) return false;
            object rawBeforeType, rawAfterType;
            string beforeType = before.TryGetValue("expression_type", out rawBeforeType) ? rawBeforeType as string : "Number";
            string afterType = after.TryGetValue("expression_type", out rawAfterType) ? rawAfterType as string : "Number";
            if (String.IsNullOrEmpty(beforeType) || !String.Equals(beforeType, afterType, StringComparison.Ordinal) ||
                !String.Equals(before["status"] as string, after["status"] as string, StringComparison.Ordinal) ||
                !String.Equals(before["unit"] as string, after["unit"] as string, StringComparison.Ordinal)) return false;
            if (String.Equals(beforeType, "Number", StringComparison.Ordinal))
            {
                if (!NumericExpressionTextEqual(before["right_hand_side"] as string, after["right_hand_side"] as string) ||
                    !NumericTextEqual(before["value"] as string, after["value"] as string)) return false;
            }
            else if (!String.Equals(before["right_hand_side"] as string, after["right_hand_side"] as string, StringComparison.Ordinal) ||
                !String.Equals(M2Json.CanonicalText(before["value"]), M2Json.CanonicalText(after["value"]), StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool NumericExpressionTextEqual(string left, string right)
    {
        double a;
        double b;
        if (Double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
            Double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return NearlyEqual(a, b);
        return String.Equals(left, right, StringComparison.Ordinal);
    }

    private static bool NumericTextEqual(string left, string right)
    {
        double a;
        double b;
        return Double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
            Double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out b) && NearlyEqual(a, b);
    }

    private static bool SemanticValuesEqual(object left, object right, string key)
    {
        if (left == null || right == null) return left == null && right == null;
        Dictionary<string, object> leftMap = left as Dictionary<string, object>;
        Dictionary<string, object> rightMap = right as Dictionary<string, object>;
        if (leftMap != null || rightMap != null)
        {
            if (leftMap == null || rightMap == null || leftMap.Count != rightMap.Count) return false;
            foreach (KeyValuePair<string, object> field in leftMap)
            {
                object other;
                if (!rightMap.TryGetValue(field.Key, out other) || !SemanticValuesEqual(field.Value, other, field.Key)) return false;
            }
            return true;
        }
        List<object> leftList = left as List<object>;
        List<object> rightList = right as List<object>;
        if (leftList != null || rightList != null)
        {
            if (leftList == null || rightList == null || leftList.Count != rightList.Count) return false;
            for (int i = 0; i < leftList.Count; i++)
                if (!SemanticValuesEqual(leftList[i], rightList[i], key)) return false;
            return true;
        }
        if (left is string && right is string && IsNumericGeometryField(key))
        {
            double a;
            double b;
            if (Double.TryParse((string)left, NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                Double.TryParse((string)right, NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return NearlyEqual(a, b);
        }
        if (left is string || right is string) return left is string && right is string && String.Equals(left as string, right as string, StringComparison.Ordinal);
        return Object.Equals(left, right);
    }

    private static bool IsNumericGeometryField(string key)
    {
        return String.Equals(key, "start", StringComparison.Ordinal) ||
            String.Equals(key, "end", StringComparison.Ordinal) ||
            String.Equals(key, "center", StringComparison.Ordinal) ||
            String.Equals(key, "coordinates", StringComparison.Ordinal) ||
            String.Equals(key, "origin", StringComparison.Ordinal) ||
            String.Equals(key, "normal", StringComparison.Ordinal) ||
            String.Equals(key, "x_axis", StringComparison.Ordinal) ||
            String.Equals(key, "y_axis", StringComparison.Ordinal) ||
            String.Equals(key, "orientation_x", StringComparison.Ordinal) ||
            String.Equals(key, "orientation_y", StringComparison.Ordinal) ||
            String.Equals(key, "major_axis_direction", StringComparison.Ordinal) ||
            String.Equals(key, "radius", StringComparison.Ordinal) ||
            String.Equals(key, "major_radius", StringComparison.Ordinal) ||
            String.Equals(key, "minor_radius", StringComparison.Ordinal) ||
            String.Equals(key, "rotation", StringComparison.Ordinal) ||
            String.Equals(key, "start_angle", StringComparison.Ordinal) ||
            String.Equals(key, "end_angle", StringComparison.Ordinal) ||
            String.Equals(key, "knots", StringComparison.Ordinal) ||
            String.Equals(key, "poles", StringComparison.Ordinal) ||
            String.Equals(key, "weights", StringComparison.Ordinal) ||
            String.Equals(key, "start_parameter", StringComparison.Ordinal) ||
            String.Equals(key, "end_parameter", StringComparison.Ordinal) ||
            String.Equals(key, "value", StringComparison.Ordinal);
    }

    private static void RequireMillimeterPartAndUnit(Part part, string requestedUnit, string expectedSymbol)
    {
        if (part == null || part.PartUnits != BasePart.Units.Millimeters)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "parameter edits currently require a millimeter Work Part");
        if (!String.Equals(requestedUnit, "mm", StringComparison.Ordinal))
            throw new M2ModelException("UNIT_MISMATCH", "requested unit must be exactly mm");
        Unit baseLength = part.UnitCollection.GetBase("Length");
        if (baseLength == null || !String.Equals(baseLength.Symbol, "mm", StringComparison.Ordinal))
            throw new M2ModelException("UNIT_MISMATCH", "Work Part base length unit is not mm");
        if (expectedSymbol != null && !String.Equals(expectedSymbol, requestedUnit, StringComparison.Ordinal))
            throw new M2ModelException("UNIT_MISMATCH", "requested unit differs from the prepared expression unit");
    }

    private static void RequireWritableSimplePart(Part part)
    { RequireWritableSimplePart(part, false); }

    private static void RequireWritableSimplePart(Part part, bool allowSwept)
    {
        if (part == null) throw new M2ModelException("UNSUPPORTED_PARAMETER", "Work Part is unavailable");
        if (IsPartReadOnly(part)) throw new M2ModelException("READ_ONLY_PART", "model modification is not allowed on a read-only Work Part");
        if (part.ComponentAssembly != null && part.ComponentAssembly.RootComponent != null)
            throw new M2ModelException("ASSEMBLY_UNSUPPORTED", "model modification does not support assembly Work Parts");
        if (!allowSwept)
        {
            Feature[] features = part.Features.GetFeatures();
            if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature history is unavailable before write");
            foreach (Feature feature in features)
            {
                if (feature == null) throw new M2ModelException("MODEL_READ_FAILED", "feature history contains null before write");
                if (feature is Swept || String.Equals(feature.FeatureType, "SKIN", StringComparison.Ordinal))
                    throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION", "this write cannot verify existing Swept definitions; create its inputs before sweeping");
            }
        }
    }

    private static ParameterTarget ResolveParameterTarget(Part part, string sketchId, string constraintId)
    {
        if (part == null || String.IsNullOrWhiteSpace(sketchId) || String.IsNullOrWhiteSpace(constraintId))
            throw new M2ModelException("INVALID_REQUEST", "sketch_id and constraint_id are required");

        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketches.ToArray returned null");
        Sketch selectedSketch = null;
        int sketchMatches = 0;
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch collection contains null");
            if (String.Equals(RequiredJournalId(sketch, "sketch"), sketchId, StringComparison.Ordinal))
            {
                selectedSketch = sketch;
                sketchMatches++;
            }
        }
        if (sketchMatches != 1 || selectedSketch == null)
            throw new M2ModelException(sketchMatches == 0 ? "SKETCH_NOT_FOUND" : "AMBIGUOUS_SKETCH", "sketch_id must identify exactly one Sketch in the bound Work Part");

        SketchDimensionalConstraint selectedDimension = null;
        int constraintMatches = 0;
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] constraints = selectedSketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
            if (constraints == null)
                throw new M2ModelException("MODEL_READ_FAILED", "constraint enumeration returned null for " + type);
            foreach (SketchConstraint constraint in constraints)
            {
                if (constraint == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch constraint collection contains null");
                if (!String.Equals(RequiredJournalId(constraint, "sketch constraint"), constraintId, StringComparison.Ordinal)) continue;
                constraintMatches++;
                selectedDimension = constraint as SketchDimensionalConstraint;
            }
        }
        if (constraintMatches == 0 || selectedDimension == null)
            throw new M2ModelException("CONSTRAINT_NOT_FOUND", "constraint_id does not identify a dimensional constraint in the selected sketch");
        if (constraintMatches != 1)
            throw new M2ModelException("AMBIGUOUS_CONSTRAINT", "constraint_id identifies more than one constraint in the selected sketch");

        SketchDimensionalConstraint.DimensionStateType state = selectedDimension.DimensionState;
        if (state != SketchDimensionalConstraint.DimensionStateType.Driving)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected dimensional constraint is not Driving");
        Expression associated = selectedDimension.AssociatedExpression;
        if (associated == null)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected Driving dimension has no AssociatedExpression");

        Expression[] expressions = part.Expressions.ToArray();
        if (expressions == null) throw new M2ModelException("MODEL_READ_FAILED", "Expressions.ToArray returned null");
        int expressionMatches = 0;
        Expression partExpression = null;
        foreach (Expression expression in expressions)
        {
            if (expression == null) throw new M2ModelException("UNSUPPORTED_MODEL", "expression collection contains null");
            if (expression.Tag == associated.Tag)
            {
                expressionMatches++;
                partExpression = expression;
            }
        }
        if (expressionMatches != 1 || partExpression == null ||
            !String.Equals(partExpression.Name, associated.Name, StringComparison.Ordinal))
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "AssociatedExpression is not a unique expression in the bound Work Part");
        if (partExpression.IsInterpartExpression)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "interpart expressions are outside the supported edit scope");
        Expression[] referencingExpressions = partExpression.GetReferencingExpressions();
        if (referencingExpressions == null || referencingExpressions.Length != 0)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "expressions that feed other expressions are outside the supported edit scope");
        Feature[] usingFeatures = partExpression.GetUsingFeatures();
        if (usingFeatures == null) throw new M2ModelException("MODEL_READ_FAILED", "associated expression feature users returned null");
        foreach (Feature usingFeature in usingFeatures)
        {
            if (usingFeature == null || usingFeature.OwningPart == null || usingFeature.OwningPart.Tag != part.Tag)
                throw new M2ModelException("UNSUPPORTED_PARAMETER", "associated expression is used by a feature outside the bound Work Part");
        }
        if (partExpression.Units == null || String.IsNullOrEmpty(partExpression.Units.Symbol))
            throw new M2ModelException("UNIT_MISMATCH", "associated expression has no explicit unit");

        int relationMatches = 0;
        SketchDimensionalConstraint relation = null;
        foreach (Sketch sketch in sketches)
        {
            foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
            {
                if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
                SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
                if (constraints == null)
                    throw new M2ModelException("MODEL_READ_FAILED", "constraint enumeration returned null while validating expression uniqueness");
                foreach (SketchConstraint constraint in constraints)
                {
                    SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                    if (dimension == null) continue;
                    Expression expression = dimension.AssociatedExpression;
                    if (expression == null || expression.Tag != associated.Tag) continue;
                    relationMatches++;
                    relation = dimension;
                }
            }
        }
        if (relationMatches != 1 || relation == null || relation.Tag != selectedDimension.Tag)
            throw new M2ModelException("AMBIGUOUS_EXPRESSION_RELATION", "AssociatedExpression must belong to exactly the selected dimensional constraint");

        Sketch.DimensionGeometry[] operands = selectedDimension.GetDimensionGeometry();
        if (operands == null || operands.Length == 0)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected dimension has no measurable geometry operands");
        List<string> geometryRefs = new List<string>();
        List<string> signatureParts = new List<string>();
        foreach (Sketch.DimensionGeometry operand in operands)
        {
            if (operand.Geometry == null)
                throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected dimension contains a null geometry operand");
            if (operand.Geometry.OwningPart == null || operand.Geometry.OwningPart.Tag != part.Tag)
                throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected dimension references geometry outside the bound Work Part");
            string geometryId = RequiredJournalId(operand.Geometry, "dimension geometry");
            string objectType = operand.Geometry.GetType().Name;
            geometryRefs.Add(DimensionReference(operand));
            signatureParts.Add(geometryId + "|" + objectType + "|" + operand.Geometry.Tag.ToString() + "|" +
                operand.AssocType.ToString() + "|" + operand.AssocValue.ToString(CultureInfo.InvariantCulture));
        }
        geometryRefs.Sort(StringComparer.Ordinal);
        signatureParts.Sort(StringComparer.Ordinal);
        string geometryKind;
        double measured;
        Dictionary<string, object> geometryReadback;
        MeasureSupportedDimension(selectedSketch, selectedDimension, operands, out geometryKind, out measured, out geometryReadback);

        ParameterTarget target = new ParameterTarget();
        target.Sketch = selectedSketch;
        target.Dimension = selectedDimension;
        target.Expression = partExpression;
        target.Unit = partExpression.Units;
        target.SketchId = sketchId;
        target.ConstraintId = constraintId;
        target.ExpressionRightHandSide = partExpression.RightHandSide;
        target.UnitSymbol = partExpression.Units.Symbol;
        target.DimensionType = selectedDimension.ConstraintType.ToString();
        target.DimensionState = state.ToString();
        target.GeometrySignature = String.Join(";", signatureParts.ToArray());
        target.GeometryKind = geometryKind;
        target.GeometryRefs = geometryRefs;
        target.ExpressionValue = partExpression.Value;
        target.GeometryValue = measured;
        target.GeometryReadback = geometryReadback;
        EnsureFinite(target.ExpressionValue, "expression value");
        EnsureFinite(target.GeometryValue, "measured geometry value");
        return target;
    }

    private static void MeasureSupportedDimension(Sketch sketch, SketchDimensionalConstraint dimension,
        Sketch.DimensionGeometry[] operands, out string kind, out double value,
        out Dictionary<string, object> readback)
    {
        kind = null;
        value = 0.0;
        readback = new Dictionary<string, object>(StringComparer.Ordinal);
        string constraintType = dimension.ConstraintType.ToString();

        if (String.Equals(constraintType, "DiameterDim", StringComparison.Ordinal) && operands.Length == 1)
        {
            Arc arc = operands[0].Geometry as Arc;
            if (arc != null && operands[0].AssocValue == 0)
            {
                double sweep = Math.Abs(arc.EndAngle - arc.StartAngle);
                double radius = arc.Radius;
                if (NearlyEqual(sweep, 2.0 * Math.PI) && radius > 0.0)
                {
                    kind = "DIAMETER_ARC";
                    value = 2.0 * radius;
                    readback.Add("kind", kind);
                    readback.Add("geometry_id", RequiredJournalId(arc, "diameter arc"));
                    readback.Add("center", Point(arc.CenterPoint));
                    readback.Add("radius", NumberText(radius));
                    readback.Add("measured_value", NumberText(value));
                    return;
                }
            }
        }

        if (String.Equals(constraintType, "HorizontalDim", StringComparison.Ordinal) && operands.Length == 2)
        {
            Arc arc = null;
            NXOpen.Point point = null;
            foreach (Sketch.DimensionGeometry operand in operands)
            {
                Arc candidateArc = operand.Geometry as Arc;
                NXOpen.Point candidatePoint = operand.Geometry as NXOpen.Point;
                if (candidateArc != null && operand.AssocType == Sketch.AssocType.ArcCenter && arc == null)
                    arc = candidateArc;
                else if (candidatePoint != null && operand.AssocType == Sketch.AssocType.None && operand.AssocValue == 0 && point == null)
                    point = candidatePoint;
                else
                {
                    arc = null;
                    point = null;
                    break;
                }
            }
            if (arc != null && point != null)
            {
                Point3d center = arc.CenterPoint;
                Point3d reference = point.Coordinates;
                Matrix3x3 axes = sketch.Orientation.Element;
                double deltaX;
                double deltaY;
                double deltaNormal;
                ProjectToSketchAxes(center, reference, axes, out deltaX, out deltaY, out deltaNormal);
                if (NearlyEqual(deltaNormal, 0.0))
                {
                    kind = "HORIZONTAL_ARC_CENTER_TO_POINT";
                    value = Math.Abs(deltaX);
                    readback.Add("kind", kind);
                    readback.Add("arc_id", RequiredJournalId(arc, "dimension arc"));
                    readback.Add("point_id", RequiredJournalId(point, "dimension reference point"));
                    readback.Add("arc_center", Point(center));
                    readback.Add("reference_point", Point(reference));
                    readback.Add("sketch_x_axis", Vector(axes.Xx, axes.Xy, axes.Xz));
                    readback.Add("projected_y_delta", NumberText(deltaY));
                    readback.Add("measured_value", NumberText(value));
                    return;
                }
            }
        }

        if ((String.Equals(constraintType, "HorizontalDim", StringComparison.Ordinal) ||
            String.Equals(constraintType, "VerticalDim", StringComparison.Ordinal)) && operands.Length == 2)
        {
            Line line = operands[0].Geometry as Line;
            if (line != null && operands[1].Geometry is Line && line.Tag == operands[1].Geometry.Tag)
            {
                bool start = false;
                bool end = false;
                foreach (Sketch.DimensionGeometry operand in operands)
                {
                    if (operand.AssocValue != 0) { start = false; end = false; break; }
                    if (operand.AssocType == Sketch.AssocType.StartPoint && !start) start = true;
                    else if (operand.AssocType == Sketch.AssocType.EndPoint && !end) end = true;
                    else { start = false; end = false; break; }
                }
                if (start && end)
                {
                    Point3d a = line.StartPoint;
                    Point3d b = line.EndPoint;
                    Matrix3x3 axes = sketch.Orientation.Element;
                    double deltaX;
                    double deltaY;
                    double deltaNormal;
                    ProjectToSketchAxes(a, b, axes, out deltaX, out deltaY, out deltaNormal);
                    if (String.Equals(constraintType, "HorizontalDim", StringComparison.Ordinal) &&
                        NearlyEqual(deltaNormal, 0.0))
                    {
                        kind = "HORIZONTAL_LINE_ENDPOINTS";
                        value = Math.Abs(deltaX);
                    }
                    else if (String.Equals(constraintType, "VerticalDim", StringComparison.Ordinal) &&
                        NearlyEqual(deltaNormal, 0.0))
                    {
                        kind = "VERTICAL_LINE_ENDPOINTS";
                        value = Math.Abs(deltaY);
                    }
                    if (kind != null)
                    {
                        readback.Add("kind", kind);
                        readback.Add("line_id", RequiredJournalId(line, "dimension line"));
                        readback.Add("start", Point(a));
                        readback.Add("end", Point(b));
                        readback.Add("sketch_x_axis", Vector(axes.Xx, axes.Xy, axes.Xz));
                        readback.Add("sketch_y_axis", Vector(axes.Yx, axes.Yy, axes.Yz));
                        readback.Add("projected_delta_x", NumberText(deltaX));
                        readback.Add("projected_delta_y", NumberText(deltaY));
                        readback.Add("measurement_axis", String.Equals(constraintType, "HorizontalDim", StringComparison.Ordinal) ? "x" : "y");
                        readback.Add("measured_value", NumberText(value));
                        return;
                    }
                }
            }
        }

        throw new M2ModelException("UNSUPPORTED_PARAMETER", "selected dimension geometry does not match a supported measurable signature");
    }

    private static void RequireSameTarget(ParameterPlanState plan, ParameterTarget target)
    {
        if (target == null || target.Dimension.Tag != plan.Dimension.Tag || target.Expression.Tag != plan.Expression.Tag ||
            !String.Equals(target.SketchId, plan.SketchId, StringComparison.Ordinal) ||
            !String.Equals(target.ConstraintId, plan.ConstraintId, StringComparison.Ordinal) ||
            !String.Equals(target.Expression.Name, plan.ExpressionName, StringComparison.Ordinal) ||
            !String.Equals(target.DimensionType, plan.DimensionType, StringComparison.Ordinal) ||
            !String.Equals(target.DimensionState, "Driving", StringComparison.Ordinal) ||
            !String.Equals(target.UnitSymbol, plan.UnitSymbol, StringComparison.Ordinal) ||
            !String.Equals(target.GeometryKind, plan.GeometryKind, StringComparison.Ordinal) ||
            !String.Equals(target.GeometrySignature, plan.GeometrySignature, StringComparison.Ordinal) ||
            !StringListsEqual(target.GeometryRefs, plan.GeometryRefs))
            throw new M2ModelException("STALE_PARAMETER", "selected sketch constraint, expression relation, unit or geometry operands changed after preparation");
    }

    private static bool PlanFieldsMatch(M2ParameterPlanSnapshot prepared, ParameterPlanState plan)
    {
        return String.Equals(prepared.RevisionId, plan.RevisionId, StringComparison.Ordinal) &&
            String.Equals(prepared.SketchId, plan.SketchId, StringComparison.Ordinal) &&
            String.Equals(prepared.ConstraintId, plan.ConstraintId, StringComparison.Ordinal) &&
            String.Equals(prepared.SketchFeatureId, plan.SketchFeatureId, StringComparison.Ordinal) &&
            String.Equals(prepared.DimensionType, plan.DimensionType, StringComparison.Ordinal) &&
            String.Equals(prepared.DimensionState, "Driving", StringComparison.Ordinal) &&
            String.Equals(prepared.ExpressionName, plan.ExpressionName, StringComparison.Ordinal) &&
            String.Equals(prepared.ExpressionRightHandSide, plan.ExpressionRightHandSide, StringComparison.Ordinal) &&
            String.Equals(prepared.CurrentValue, NumberText(plan.CurrentExpressionValue), StringComparison.Ordinal) &&
            String.Equals(prepared.Unit, plan.UnitSymbol, StringComparison.Ordinal) &&
            StringListsEqual(prepared.GeometryRefs, plan.GeometryRefs);
    }

    private string TryRestoreParameter(M2ModelBinding binding, BindingState state, ParameterPlanState plan,
        Session.UndoMarkId mark, Dictionary<string, object> evidence)
    {
        try
        {
            SnapshotResult current = Capture(binding, state);
            string currentSemantic = CanonicalSemanticSnapshot(current.Snapshot);
            string currentGeometry = CaptureFullGeometry(state.WorkPart);
            ParameterTarget currentTarget = ResolveParameterTarget(state.WorkPart, plan.SketchId, plan.ConstraintId);
            RequireSameTarget(plan, currentTarget);
            bool same = String.Equals(currentSemantic, plan.SemanticSnapshot, StringComparison.Ordinal) &&
                String.Equals(currentGeometry, plan.FullGeometrySnapshot, StringComparison.Ordinal) &&
                String.Equals(currentTarget.ExpressionRightHandSide, plan.ExpressionRightHandSide, StringComparison.Ordinal) &&
                NearlyEqual(currentTarget.ExpressionValue, plan.CurrentExpressionValue) &&
                NearlyEqual(currentTarget.GeometryValue, plan.GeometryValue);
            if (same)
            {
                SetFinalReadbackEvidence(evidence, currentTarget);
                evidence["rollback_readback_verified"] = true;
                evidence["rollback_performed"] = false;
                return "UNCHANGED";
            }
        }
        catch (Exception ex) { evidence["pre_undo_state_check_error"] = ex.GetType().Name; }

        try
        {
            state.Session.UndoToMarkWithStatus(mark, "M2 selected sketch dimension edit");
            evidence["rollback_performed"] = true;
        }
        catch (Exception ex)
        {
            evidence["rollback_undo_error"] = ex.GetType().Name;
            return "UNKNOWN";
        }

        int rollbackErrors;
        try
        {
            rollbackErrors = state.Session.UpdateManager.DoUpdate(mark);
            evidence["rollback_update_error_count"] = rollbackErrors;
            if (rollbackErrors != 0) return "UNKNOWN";
        }
        catch (Exception ex)
        {
            evidence["rollback_update_error"] = ex.GetType().Name;
            return "UNKNOWN";
        }

        try
        {
            SnapshotResult restoredSnapshot = Capture(binding, state);
            string restoredSemantic = CanonicalSemanticSnapshot(restoredSnapshot.Snapshot);
            string restoredGeometry = CaptureFullGeometry(state.WorkPart);
            ParameterTarget restored = ResolveParameterTarget(state.WorkPart, plan.SketchId, plan.ConstraintId);
            RequireSameTarget(plan, restored);
            bool semanticRestored = String.Equals(restoredSemantic, plan.SemanticSnapshot, StringComparison.Ordinal);
            bool fullGeometryRestored = String.Equals(restoredGeometry, plan.FullGeometrySnapshot, StringComparison.Ordinal);
            bool rhsRestored = String.Equals(restored.ExpressionRightHandSide, plan.ExpressionRightHandSide, StringComparison.Ordinal);
            bool valueRestored = NearlyEqual(restored.ExpressionValue, plan.CurrentExpressionValue);
            bool geometryRestored = NearlyEqual(restored.GeometryValue, plan.GeometryValue);
            evidence["rollback_after_rhs"] = restored.ExpressionRightHandSide;
            evidence["rollback_after_value"] = NumberText(restored.ExpressionValue);
            evidence["rollback_after_unit"] = restored.UnitSymbol;
            evidence["geometry_after_rollback"] = restored.GeometryReadback;
            SetFinalReadbackEvidence(evidence, restored);
            evidence["rollback_rhs_verified"] = rhsRestored;
            evidence["rollback_expression_value_verified"] = valueRestored;
            evidence["rollback_geometry_verified"] = geometryRestored;
            evidence["rollback_semantic_snapshot_verified"] = semanticRestored;
            evidence["rollback_full_geometry_verified"] = fullGeometryRestored;
            bool verified = semanticRestored && fullGeometryRestored && rhsRestored &&
                valueRestored && geometryRestored;
            evidence["rollback_readback_verified"] = verified;
            return verified ? "ROLLED_BACK" : "UNKNOWN";
        }
        catch (Exception ex)
        {
            evidence["rollback_readback_error"] = ex.GetType().Name;
            evidence["rollback_readback_verified"] = false;
            return "UNKNOWN";
        }
    }

    private static void AddBeforeEvidence(Dictionary<string, object> evidence, ParameterPlanState plan)
    {
        evidence["before_rhs"] = plan.ExpressionRightHandSide;
        evidence["before_value"] = NumberText(plan.CurrentExpressionValue);
        evidence["before_unit"] = plan.UnitSymbol;
        evidence["geometry_before"] = Clone(plan.GeometryBefore);
    }

    private static void SetFinalReadbackEvidence(Dictionary<string, object> evidence, ParameterTarget target)
    {
        evidence["after_rhs"] = target.ExpressionRightHandSide;
        evidence["after_value"] = NumberText(target.ExpressionValue);
        evidence["after_unit"] = target.UnitSymbol;
        evidence["geometry_after"] = target.GeometryReadback;
    }

    private static bool IsNumericRhsEqual(string rightHandSide, double requestedValue)
    {
        double rightHandSideValue;
        return !String.IsNullOrWhiteSpace(rightHandSide) &&
            Double.TryParse(rightHandSide.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out rightHandSideValue) &&
            !Double.IsNaN(rightHandSideValue) && !Double.IsInfinity(rightHandSideValue) &&
            NearlyEqual(rightHandSideValue, requestedValue);
    }

    private static M2ParameterExecutionResult ParameterResult(string state, string revisionId,
        Dictionary<string, object> evidence, string errorCode, string errorMessage)
    {
        M2ParameterExecutionResult result = new M2ParameterExecutionResult();
        result.State = state;
        result.RevisionId = revisionId;
        result.Evidence = evidence;
        result.ErrorCode = errorCode;
        result.ErrorMessage = errorMessage;
        return result;
    }

    private static double ParsePositiveMillimeters(string text, string field)
    {
        double value;
        if (String.IsNullOrWhiteSpace(text) || !Double.TryParse(text.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value) || Double.IsNaN(value) || Double.IsInfinity(value) || value <= 0.0)
            throw new M2ModelException("INVALID_VALUE", field + " must be a finite positive numeric millimeter value without a unit suffix");
        return value;
    }

    private static void EnsureFinite(double value, string field)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value))
            throw new M2ModelException("UNSUPPORTED_MODEL", "NX returned a non-finite " + field);
    }

    private static double ValueTolerance(double left, double right)
    {
        double scale = Math.Max(Math.Abs(left), Math.Abs(right));
        return Math.Max(1e-7, scale * 1e-9);
    }

    private static void ProjectToSketchAxes(Point3d start, Point3d end, Matrix3x3 axes,
        out double x, out double y, out double normal)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double dz = end.Z - start.Z;
        x = dx * axes.Xx + dy * axes.Xy + dz * axes.Xz;
        y = dx * axes.Yx + dy * axes.Yy + dz * axes.Yz;
        normal = dx * axes.Zx + dy * axes.Zy + dz * axes.Zz;
        EnsureFinite(x, "sketch X projection");
        EnsureFinite(y, "sketch Y projection");
        EnsureFinite(normal, "sketch normal projection");
    }

    private static bool NearlyEqual(double left, double right)
    {
        if (Double.IsNaN(left) || Double.IsInfinity(left) || Double.IsNaN(right) || Double.IsInfinity(right)) return false;
        return Math.Abs(left - right) <= ValueTolerance(left, right);
    }

    private static List<string> CopyStrings(List<string> values)
    {
        List<string> copy = new List<string>();
        if (values != null) foreach (string value in values) copy.Add(value);
        return copy;
    }

    private static bool StringListsEqual(List<string> left, List<string> right)
    {
        if (left == null || right == null || left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!String.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static void TryDeleteUndoMark(Session session, Session.UndoMarkId mark)
    {
        try { session.DeleteUndoMark(mark, ""); }
        catch { }
    }

    // Builders for existing Swept features may create temporary expressions.
    // Discovery is isolated by its own undo mark and requires a full inventory
    // before and after; an unsupported definition is never silently accepted.
    private static Dictionary<string, object> F21CaptureDefinitions(Part part, string excludedFeatureId)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (Feature feature in part.Features.GetFeatures())
        {
            if (feature == null) throw new M2ModelException("MODEL_READ_FAILED", "null feature in Swept inventory");
            if (!String.Equals(feature.FeatureType, "SKIN", StringComparison.Ordinal) ||
                String.Equals(feature.JournalIdentifier, excludedFeatureId, StringComparison.Ordinal)) continue;
            Swept swept = feature as Swept;
            if (swept == null) throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION", "SKIN feature is not a readable native Swept");
            string id = RequiredJournalId(swept, "Swept feature");
            try
            {
                result.Add(id, V2SweepFactory.ReadDiscoveredIsolated(Session.GetSession(), part, swept,
                    delegate { return F19Inventory(part); }));
            }
            catch (V2SweepFactory.ReadStateUnknownException ex)
            { throw M3AnalysisStateUnknown("Swept reader could not prove state restoration", ex); }
            catch (Exception ex)
            { throw new M2ModelException("MODEL_READ_FAILED", "existing Swept definition could not be read (" + ex.GetType().Name + ")"); }
        }
        return result;
    }

    private static Dictionary<string, object> F21SnapshotDefinitions(Part part)
    {
        try
        {
            Dictionary<string, object> definitions = F21CaptureDefinitions(part, null);
            try { F21SweepDefinitionComparison.Normalize(definitions); }
            catch (F21SweepDefinitionComparison.UnsupportedDefinitionException ex)
            {
                return new Dictionary<string, object> { { "status", "UNSUPPORTED" },
                    { "reason", "UNSUPPORTED_SWEEP_COMPARISON_PROJECTION" }, { "detail", ex.Message }, { "definitions", definitions } };
            }
            return new Dictionary<string, object> { { "status", "SUPPORTED" }, { "definitions", definitions } };
        }
        catch (M2ModelException ex)
        {
            if (ex.Code != "UNSUPPORTED_SWEEP_PRESERVATION") throw;
            return new Dictionary<string, object> { { "status", "UNSUPPORTED" }, { "reason", ex.Code } };
        }
    }

    // A persistent identity uses native source geometry and control values, never
    // same-session tags or journal identifiers. Ambiguous sources fail before SaveAs.
    private static List<object> F21PersistentDefinitions(Part part, Dictionary<string, object> definitions)
    {
        List<object> result = new List<object>();
        if (definitions.Count == 0) return result;
        try { F21SweepDefinitionComparison.Normalize(definitions); }
        catch (F21SweepDefinitionComparison.UnsupportedDefinitionException ex)
        { throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE", "Swept Section identity cannot be established for SaveCopy: " + ex.Message); }
        List<object> spatial = F20PersistentRecords(F20SpatialRecords(part, false));
        Dictionary<string, object> curves = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (object item in spatial)
        {
            Dictionary<string, object> row = (Dictionary<string, object>)item;
            curves.Add((string)row["journal_id"], row["geometry"]);
        }
        Dictionary<string, int> profileCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Sketch sketch in part.Sketches.ToArray())
        {
            Dictionary<string, object> analysis;
            try { analysis = V2SketchProfileAnalyzer.AnalyzeF12WholeSketchSection(part, sketch); }
            catch (V2SketchProfileAnalyzer.SectionCleanupException ex)
            { throw M3AnalysisStateUnknown("Swept persistence profile analysis cleanup failed", ex); }
            string fingerprint;
            if (!F12SectionAnalysisVerified(analysis) || !F13TryProfileFingerprint(analysis, "input_geometry_domain_fingerprint", out fingerprint))
                throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE", "a Sketch cannot be uniquely compared with the Swept profile");
            int count; profileCounts.TryGetValue(fingerprint, out count); profileCounts[fingerprint] = count + 1;
        }
        foreach (KeyValuePair<string, object> entry in definitions)
        {
            Dictionary<string, object> row = entry.Value as Dictionary<string, object>;
            Dictionary<string, object> section = row == null ? null : row["section_evidence"] as Dictionary<string, object>;
            Dictionary<string, object> guide = row == null ? null : row["guide_evidence"] as Dictionary<string, object>;
            string fingerprint = section == null ? null : section["input_geometry_domain_fingerprint"] as string;
            int profileCount;
            if (String.IsNullOrEmpty(fingerprint) || !profileCounts.TryGetValue(fingerprint, out profileCount) || profileCount != 1 || guide == null)
                throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE", "Swept source Sketch is geometrically ambiguous");
            string[] ids = row["path_curve_ids"] as string[];
            if (ids == null || ids.Length == 0) throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE", "Swept path identity is unavailable");
            List<object> path = new List<object>();
            Curve[] nativePath = new Curve[ids.Length];
            int pathIndex = 0;
            foreach (string id in ids)
            {
                object geometry;
                if (!curves.TryGetValue(id, out geometry)) throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE", "Swept path is not a uniquely identified independent curve");
                path.Add(geometry);
                foreach (Curve curve in part.Curves.ToArray()) if (curve != null && String.Equals(curve.JournalIdentifier, id, StringComparison.Ordinal))
                { if (nativePath[pathIndex] != null) throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE", "Swept path id is ambiguous"); nativePath[pathIndex] = curve; }
                if (nativePath[pathIndex] == null) throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE", "Swept path curve is missing");
                pathIndex++;
            }
            List<object> starts = new List<object>(), ends = new List<object>();
            double[][] nativeStarts = guide["element_start_points_mm"] as double[][], nativeEnds = guide["element_end_points_mm"] as double[][];
            if (nativeStarts == null || nativeEnds == null || nativeStarts.Length != ids.Length || nativeEnds.Length != ids.Length)
                throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE", "Swept directed path evidence is incomplete");
            for (int i = 0; i < ids.Length; i++) { starts.Add(NumberList(nativeStarts[i])); ends.Add(NumberList(nativeEnds[i])); }
            double[][] directed = new double[ids.Length + 1][];
            for (int i = 0; i < ids.Length; i++) directed[i] = nativeStarts[i];
            directed[ids.Length] = nativeEnds[ids.Length - 1];
            Sketch sourceSketch = null;
            foreach (Sketch candidate in part.Sketches.ToArray())
                if (candidate != null && candidate.Feature != null && String.Equals(candidate.Feature.JournalIdentifier, row["sketch_feature_id"] as string, StringComparison.Ordinal))
                { if (sourceSketch != null) throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE", "Swept Sketch feature is ambiguous"); sourceSketch = candidate; }
            Body output = FindBody(part, row["body_id"] as string);
            if (sourceSketch == null || output == null) throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE", "Swept source Sketch or output body is unavailable");
            Dictionary<string, object> geometryEvidence;
            try
            {
                geometryEvidence = V2SweepGeometry.Verify(part, sourceSketch, nativePath, directed,
                    (string)row["orientation_mode"], Convert.ToDouble(row["requested_twist_deg"], CultureInfo.InvariantCulture), (string)row["profile_output"], output);
                if (geometryEvidence == null || !Object.Equals(geometryEvidence["status"], "SAMPLED_GEOMETRY_AND_TOPOLOGY_MATCH"))
                    throw new InvalidOperationException("Swept sampled geometry did not verify");
            }
            catch (V2SweepGeometry.CleanupException ex) { throw M3AnalysisStateUnknown("Swept persistence geometry cleanup failed", ex); }
            catch (Exception ex) { throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION", "Swept output geometry could not be verified (" + ex.GetType().Name + ")"); }
            Dictionary<string, object> signature = new Dictionary<string, object> {
                { "profile_domain", fingerprint }, { "path", path }, { "start", starts }, { "end", ends },
                { "profile_output", row["profile_output"] }, { "orientation_mode", row["orientation_mode"] },
                { "native_law_type", row["native_law_type"] }, { "native_end_deg", row["native_end_deg"] },
                { "alignment_type", row["alignment_type"] }, { "scaling_option", row["scaling_option"] },
                { "scale_factor", row["scale_factor"] }, { "section_location", row["section_location"] },
                { "interpolation_option", row["interpolation_option"] }, { "g0_tolerance", row["g0_tolerance"] },
                { "g1_tolerance", row["g1_tolerance"] }, { "preserve_shape", row["preserve_shape"] },
                { "preserve_guide_shape", row["preserve_guide_shape"] }
            };
            foreach (object existing in result)
                if (SemanticValuesEqual(((Dictionary<string, object>)existing)["signature"], signature, "sweep"))
                    throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE", "multiple Swept definitions are geometrically indistinguishable");
            result.Add(new Dictionary<string, object> { { "feature_id", entry.Key }, { "signature", signature }, { "sampled_output_geometry", geometryEvidence } });
        }
        return result;
    }

    private static bool F21PersistentDefinitionsMatch(List<object> before, List<object> after, out List<object> mappings)
    {
        mappings = new List<object>(); if (before.Count != after.Count) return false;
        bool[] used = new bool[after.Count]; bool ok = true;
        foreach (object value in before)
        {
            Dictionary<string, object> source = (Dictionary<string, object>)value; int match = -1, count = 0;
            for (int i = 0; i < after.Count; i++)
            {
                Dictionary<string, object> target = (Dictionary<string, object>)after[i];
                if (!used[i] && SemanticValuesEqual(source["signature"], target["signature"], "sweep")) { match = i; count++; }
            }
            if (count != 1) { ok = false; mappings.Add(new Dictionary<string, object> { { "before_id", source["feature_id"] }, { "matching_after_count", count } }); continue; }
            used[match] = true; mappings.Add(new Dictionary<string, object> { { "before_id", source["feature_id"] }, { "after_id", ((Dictionary<string, object>)after[match])["feature_id"] } });
        }
        foreach (bool found in used) if (!found) ok = false;
        return ok;
    }

    private static string CanonicalSemanticSnapshot(Dictionary<string, object> snapshot)
    {
        Dictionary<string, object> copy = DeepCloneValue(snapshot) as Dictionary<string, object>;
        if (copy == null) throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot could not be normalized");
        object sweepValue;
        Dictionary<string, object> swept = copy.TryGetValue("native_swept_definitions", out sweepValue)
            ? sweepValue as Dictionary<string, object> : null;
        if (swept != null && Object.Equals(swept.ContainsKey("status") ? swept["status"] : null, "SUPPORTED"))
        {
            Dictionary<string, object> definitions = swept.ContainsKey("definitions") ? swept["definitions"] as Dictionary<string, object> : null;
            if (definitions == null) throw new M2ModelException("MODEL_READ_FAILED", "native Swept definitions are unavailable");
            try { swept["definitions"] = F21SweepDefinitionComparison.Normalize(definitions); }
            catch (F21SweepDefinitionComparison.UnsupportedDefinitionException ex)
            { throw new M2ModelException("UNSUPPORTED_SWEEP_PRESERVATION", "native Swept Section identity cannot be normalized: " + ex.Message); }
            catch (Exception ex) { throw new M2ModelException("MODEL_READ_FAILED", "native Swept Section projection failed (" + ex.GetType().Name + ")"); }
        }
        NormalizeModifiedFlag(copy, "part");
        NormalizeModifiedFlag(copy, "identity");
        List<object> parts = copy.ContainsKey("session_parts") ? copy["session_parts"] as List<object> : null;
        if (parts != null)
        {
            foreach (object item in parts)
            {
                Dictionary<string, object> part = item as Dictionary<string, object>;
                if (part != null && part.ContainsKey("is_modified")) part["is_modified"] = false;
            }
        }
        List<object> bodies = copy.ContainsKey("bodies") ? copy["bodies"] as List<object> : null;
        if (bodies != null)
        {
            foreach (object item in bodies)
            {
                Dictionary<string, object> body = item as Dictionary<string, object>;
                if (body == null) continue;
                body.Remove("face_refs");
                body.Remove("volume_mm3");
                body.Remove("bbox_mm");
                body.Remove("measurement_status");
            }
        }
        return M2Json.CanonicalText(copy);
    }

    private static void NormalizeModifiedFlag(Dictionary<string, object> snapshot, string key)
    {
        Dictionary<string, object> record = snapshot.ContainsKey(key) ? snapshot[key] as Dictionary<string, object> : null;
        if (record != null && record.ContainsKey("is_modified")) record["is_modified"] = false;
    }

    private static object DeepCloneValue(object value)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            Dictionary<string, object> copy = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> entry in dictionary) copy.Add(entry.Key, DeepCloneValue(entry.Value));
            return copy;
        }
        List<object> list = value as List<object>;
        if (list != null)
        {
            List<object> copy = new List<object>();
            foreach (object item in list) copy.Add(DeepCloneValue(item));
            return copy;
        }
        return value;
    }

    private static string CaptureFullGeometry(Part part)
    {
        return M2Json.CanonicalText(CaptureGeometryRecords(part, true));
    }

    private static string CaptureFullGeometryWithoutSupport(Part part)
    {
        return M2Json.CanonicalText(CaptureGeometryRecords(part, true, false));
    }

    private static List<object> CaptureGeometryRecords(Part part, bool includeTag)
    { return CaptureGeometryRecords(part, includeTag, true); }

    private static List<object> CaptureGeometryRecords(Part part, bool includeTag, bool includeSupport)
    {
        Sketch[] sketches = part.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketches.ToArray returned null during geometry verification");
        List<Dictionary<string, object>> sketchRecords = new List<Dictionary<string, object>>();
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch collection contains null during geometry verification");
            string sketchId = RequiredJournalId(sketch, "sketch");
            NXObject[] geometry = sketch.GetAllGeometry();
            if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null during geometry verification");
            Dictionary<string, NXObject> allGeometry = new Dictionary<string, NXObject>(StringComparer.Ordinal);
            foreach (NXObject item in geometry) AddFullGeometryObject(allGeometry, item, part.Tag);
            foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
            {
                if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
                SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
                if (constraints == null)
                    throw new M2ModelException("MODEL_READ_FAILED", "constraint enumeration returned null during geometry verification");
                foreach (SketchConstraint constraint in constraints)
                {
                    if (constraint == null) throw new M2ModelException("UNSUPPORTED_MODEL", "constraint collection contains null during geometry verification");
                    SketchDimensionalConstraint dimensional = constraint as SketchDimensionalConstraint;
                    if (dimensional != null)
                    {
                        Sketch.DimensionGeometry[] operands = dimensional.GetDimensionGeometry();
                        if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "dimension geometry returned null during geometry verification");
                        foreach (Sketch.DimensionGeometry operand in operands) AddFullGeometryObject(allGeometry, operand.Geometry, part.Tag);
                    }
                    else
                    {
                        SketchGeometricConstraint geometric = constraint as SketchGeometricConstraint;
                        if (geometric == null) throw new M2ModelException("UNSUPPORTED_MODEL", "constraint class cannot be included in complete geometry verification");
                        Sketch.ConstraintGeometry[] operands = geometric.GetGeometry();
                        if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "geometric constraint operands returned null during geometry verification");
                        foreach (Sketch.ConstraintGeometry operand in operands) AddFullGeometryObject(allGeometry, operand.Geometry, part.Tag);
                    }
                }
            }
            List<Dictionary<string, object>> geometryRecords = new List<Dictionary<string, object>>();
            foreach (NXObject item in allGeometry.Values)
            {
                string id = RequiredJournalId(item, "sketch geometry");
                Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
                record.Add("journal_id", id);
                if (includeTag) record.Add("tag", item.Tag.ToString());
                record.Add("object_type", item.GetType().Name);
                Line line = item as Line;
                Arc arc = item as Arc;
                SketchVirtualPoint virtualPoint = item as SketchVirtualPoint;
                NXOpen.Point point = item as NXOpen.Point;
                if (line != null)
                {
                    record.Add("start", Point(line.StartPoint));
                    record.Add("end", Point(line.EndPoint));
                }
                else if (arc != null)
                {
                    Point3d center; Vector3d xAxis, yAxis;
                    arc.GetOrientation(out center, out xAxis, out yAxis);
                    record.Add("center", Point(arc.CenterPoint));
                    record.Add("radius", NumberText(arc.Radius));
                    record.Add("start_angle", NumberText(arc.StartAngle));
                    record.Add("end_angle", NumberText(arc.EndAngle));
                    record.Add("orientation_x", Vector(xAxis.X, xAxis.Y, xAxis.Z));
                    record.Add("orientation_y", Vector(yAxis.X, yAxis.Y, yAxis.Z));
                    record.Add("start", Point(ConicPoint(center, xAxis, yAxis, arc.Radius, arc.Radius, 0.0, arc.StartAngle)));
                    record.Add("end", Point(ConicPoint(center, xAxis, yAxis, arc.Radius, arc.Radius, 0.0, arc.EndAngle)));
                }
                else if (item is Ellipse)
                {
                    Ellipse ellipse = (Ellipse)item;
                    double major, minor, start, end, rotation;
                    Point3d center;
                    ellipse.GetParameters(out major, out minor, out start, out end, out rotation, out center);
                    double[] parameterLimits = NativeCurveParameterLimits(ellipse);
                    Point3d frameCenter; Vector3d xAxis, yAxis;
                    ellipse.GetOrientation(out frameCenter, out xAxis, out yAxis);
                    Vector3d majorAxis = RotateInPlane(xAxis, yAxis, rotation);
                    record.Add("center", Point(center));
                    record.Add("major_radius", NumberText(major));
                    record.Add("minor_radius", NumberText(minor));
                    record.Add("rotation", NumberText(rotation));
                    record.Add("start_angle", NumberText(parameterLimits[0]));
                    record.Add("end_angle", NumberText(parameterLimits[1]));
                    record.Add("native_polar_start_angle", NumberText(start));
                    record.Add("native_polar_end_angle", NumberText(end));
                    record.Add("major_axis_direction", Vector(majorAxis.X, majorAxis.Y, majorAxis.Z));
                    record.Add("orientation_x", Vector(xAxis.X, xAxis.Y, xAxis.Z));
                    record.Add("orientation_y", Vector(yAxis.X, yAxis.Y, yAxis.Z));
                    Vector3d ellipseNormal = Cross(xAxis, yAxis);
                    record.Add("normal", Vector(ellipseNormal.X, ellipseNormal.Y, ellipseNormal.Z));
                    record.Add("start", Point(NativeCurvePoint(ellipse, parameterLimits[0])));
                    record.Add("end", Point(NativeCurvePoint(ellipse, parameterLimits[1])));
                }
                else if (item is Spline)
                {
                    UFCurve.Spline native;
                    UFSession.GetUFSession().Curve.AskSplineData(item.Tag, out native);
                    List<object> poles = new List<object>();
                    for (int i = 0; i < native.poles.GetLength(0); i++)
                    {
                        List<object> pole = new List<object>();
                        for (int j = 0; j < native.poles.GetLength(1); j++) pole.Add(NumberText(native.poles[i, j]));
                        poles.Add(pole);
                    }
                    record.Add("degree", native.order - 1);
                    record.Add("rational", native.is_rational);
                    record.Add("pole_count", native.num_poles);
                    record.Add("knots", NumberTextList(native.knots));
                    record.Add("poles", poles);
                    List<object> weights = new List<object>();
                    if (native.poles.GetLength(1) > 3)
                        for (int i = 0; i < native.poles.GetLength(0); i++) weights.Add(NumberText(native.poles[i, 3]));
                    record.Add("weights", weights);
                    record.Add("start_parameter", NumberText(native.start_param));
                    record.Add("end_parameter", NumberText(native.end_param));
                    int interpolationDegree, periodic, interpolationCount;
                    UFCurve.PtSlopeCrvatr[] interpolationPoints;
                    double[] interpolationParameters;
                    UFSession.GetUFSession().Curve.AskSplineThruPts(item.Tag, out interpolationDegree,
                        out periodic, out interpolationCount, out interpolationPoints, out interpolationParameters);
                    if (interpolationPoints == null || interpolationPoints.Length != interpolationCount ||
                        interpolationParameters == null || interpolationParameters.Length != interpolationCount)
                        throw new M2ModelException("MODEL_READ_FAILED", "spline interpolation conditions are incomplete during geometry verification");
                    record.Add("interpolation_degree", interpolationDegree);
                    record.Add("interpolation_periodic", periodic);
                    record.Add("interpolation_parameters", NumberTextList(interpolationParameters));
                    record.Add("native_interpolation_conditions", SplineInterpolationConditionRecords(interpolationPoints));
                }
                else if (virtualPoint != null)
                {
                    record.Add("point_kind", "SKETCH_VIRTUAL_POINT");
                    record.Add("coordinates", Point(virtualPoint.Coordinates));
                }
                else if (point != null)
                {
                    record.Add("point_kind", "POINT");
                    record.Add("coordinates", Point(point.Coordinates));
                }
                else
                {
                    throw new M2ModelException("UNSUPPORTED_PARAMETER", "complete rollback geometry readback is unavailable for " + item.GetType().Name);
                }
                geometryRecords.Add(record);
            }
            SortRecords(geometryRecords, "journal_id", "sketch geometry");
            Dictionary<string, object> sketchRecord = new Dictionary<string, object>(StringComparer.Ordinal);
            sketchRecord.Add("journal_id", sketchId);
            Point3d sketchOrigin = sketch.Origin;
            Matrix3x3 sketchAxes = sketch.Orientation.Element;
            sketchRecord.Add("origin", Point(sketchOrigin));
            sketchRecord.Add("normal", Vector(sketchAxes.Zx, sketchAxes.Zy, sketchAxes.Zz));
            sketchRecord.Add("x_axis", Vector(sketchAxes.Xx, sketchAxes.Xy, sketchAxes.Xz));
            sketchRecord.Add("y_axis", Vector(sketchAxes.Yx, sketchAxes.Yy, sketchAxes.Yz));
            Feature[] sketchParents = sketch.Feature == null ? new Feature[0] : sketch.Feature.GetParents();
            if (sketchParents == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch feature parents returned null during geometry verification");
            sketchRecord.Add("parent_feature_ids", ObjectStrings(GetJournalIds(sketchParents, "sketch parent feature")));
            Dictionary<string, object> supportAssociation = null, supportFrame = null;
            bool hasSupport = includeSupport && TryReadM3NativeSupport(part, sketch, null, out supportAssociation, out supportFrame);
            sketchRecord.Add("support_association", M3SupportIdentityProjection(hasSupport ? supportAssociation : null));
            sketchRecord.Add("geometry", ToObjectList(geometryRecords));
            sketchRecords.Add(sketchRecord);
        }
        SortRecords(sketchRecords, "journal_id", "sketch");
        List<object> completeRecords = ToObjectList(sketchRecords);
        completeRecords.AddRange(CaptureM7NativeDefinitions(part));
        return completeRecords;
    }

    private static void AddFullGeometryObject(Dictionary<string, NXObject> allGeometry, NXObject item, Tag partTag)
    {
        if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "constraint geometry contains null during geometry verification");
        if (item.OwningPart == null || item.OwningPart.Tag != partTag)
            throw new M2ModelException("UNSUPPORTED_PARAMETER", "complete geometry verification does not support external geometry references");
        string key = item.Tag.ToString();
        if (String.IsNullOrEmpty(key)) throw new M2ModelException("UNSUPPORTED_MODEL", "geometry has no stable NX Tag during geometry verification");
        NXObject existing;
        if (allGeometry.TryGetValue(key, out existing))
        {
            if (!String.Equals(RequiredJournalId(existing, "sketch geometry"),
                RequiredJournalId(item, "sketch geometry"), StringComparison.Ordinal))
                throw new M2ModelException("UNSUPPORTED_MODEL", "geometry Tag resolves to inconsistent JournalIdentifiers");
            return;
        }
        allGeometry.Add(key, item);
    }

    private SnapshotResult Capture(M2ModelBinding binding, BindingState state)
    {
        bool modifiedBefore;
        try { modifiedBefore = state.WorkPart.IsModified; }
        catch (Exception ex) { throw new M2ModelException("MODEL_READ_FAILED", "could not read Work Part modified state (" + ex.GetType().Name + ")"); }

        Dictionary<string, object> snapshot;
        try { snapshot = BuildSemanticSnapshot(binding, state); }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot failed (" + ex.GetType().Name + ")");
        }

        bool modifiedAfter;
        try { modifiedAfter = state.WorkPart.IsModified; }
        catch (Exception ex) { throw new M2ModelException("MODEL_READ_FAILED", "could not verify Work Part modified state (" + ex.GetType().Name + ")"); }

        Dictionary<string, object> part = snapshot["part"] as Dictionary<string, object>;
        if (part == null) throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot has no part record");
        part["is_modified"] = modifiedAfter;
        Dictionary<string, object> identity = snapshot["identity"] as Dictionary<string, object>;
        if (identity == null) throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot has no identity record");
        identity["is_modified"] = modifiedAfter;
        List<object> inventory = snapshot["session_parts"] as List<object>;
        if (inventory == null) throw new M2ModelException("MODEL_READ_FAILED", "semantic snapshot has no session inventory");
        foreach (object inventoryItem in inventory)
        {
            Dictionary<string, object> partItem = inventoryItem as Dictionary<string, object>;
            if (partItem != null && String.Equals(Convert.ToString(partItem["tag"], CultureInfo.InvariantCulture),
                state.PartTag, StringComparison.Ordinal)) partItem["is_modified"] = modifiedAfter;
        }

        Dictionary<string, object> effects = new Dictionary<string, object>(StringComparer.Ordinal);
        effects.Add("is_modified_before_read", modifiedBefore);
        effects.Add("is_modified_after_read", modifiedAfter);
        effects.Add("dirty_changed_by_read", !modifiedBefore && modifiedAfter);
        effects.Add("read_may_mark_modified", true);
        effects.Add("note", "NXOpen getters can mark the Work Part modified; before/after state is reported without clearing dirty state.");

        string canonical = CanonicalSemanticSnapshot(snapshot);
        List<object> bodyMeasurements = BodyMeasurementProjection(snapshot);
        Dictionary<string, object> measurementComparison;
        bool measurementsChanged = state.CanonicalBodyMeasurements == null ||
            !BodyMeasurementRecordsEqual(state.CanonicalBodyMeasurements, bodyMeasurements, out measurementComparison);
        if (state.CanonicalBodyMeasurements == null) measurementComparison = new Dictionary<string, object>(StringComparer.Ordinal);
        bool semanticChanged = !String.Equals(state.CanonicalSnapshot, canonical, StringComparison.Ordinal);
        bool changed = semanticChanged || measurementsChanged;
        if (state.RevisionNumber == 0 || changed) state.RevisionNumber++;
        if (measurementsChanged || state.CanonicalBodyMeasurements == null)
            state.CanonicalBodyMeasurements = bodyMeasurements;
        state.CanonicalSnapshot = canonical;
        state.ReadEffects = effects;
        binding.CanonicalSnapshot = canonical;
        binding.RevisionId = state.RevisionNumber.ToString(CultureInfo.InvariantCulture);
        return new SnapshotResult { Snapshot = snapshot, ReadEffects = effects, SemanticChanged = changed };
    }

    private Dictionary<string, object> BuildSemanticSnapshot(M2ModelBinding binding, BindingState state)
    {
        Session session = state.Session;
        Part work = state.WorkPart;
        Dictionary<string, object> supportSummary = BuildArtifactSupportSummary(work, binding.ArtifactScope);
        string rejection = Convert.ToString(supportSummary["rejection_code"], CultureInfo.InvariantCulture);
        if (!String.IsNullOrEmpty(rejection))
            throw new M2ModelException(rejection, Convert.ToString(supportSummary["rejection_reason"], CultureInfo.InvariantCulture));
        state.SupportSummary = supportSummary;
        binding.ArtifactAssessment = supportSummary;
        binding.IsReadOnly = IsPartReadOnly(work);
        Dictionary<string, object> snapshot = new Dictionary<string, object>(StringComparer.Ordinal);
        snapshot.Add("schema", "m2-semantic-v1");
        snapshot.Add("identity", BuildIdentity(binding, work, null));

        Dictionary<string, object> part = new Dictionary<string, object>(StringComparer.Ordinal);
        part.Add("path", state.PartPath);
        part.Add("leaf", Path.GetFileName(state.PartPath));
        part.Add("tag", state.PartTag);
        part.Add("load_state", work.PartLoadState.ToString());
        part.Add("units", work.PartUnits.ToString());
        part.Add("is_read_only", IsPartReadOnly(work));
        part.Add("is_modified", work.IsModified);
        snapshot.Add("part", part);

        snapshot.Add("bodies", BuildBodies(work));
        snapshot.Add("features", BuildFeatures(work));
        snapshot.Add("native_swept_definitions", F21SnapshotDefinitions(work));
        snapshot.Add("native_helical_definitions", CaptureM7NativeDefinitions(work));
        snapshot.Add("curve_ids", ObjectStrings(GetJournalIds(work.Curves.ToArray(), "part curve")));
        snapshot.Add("spatial_curves", F20SpatialRecords(work, true));
        snapshot.Add("sketches", BuildSketches(work));
        snapshot.Add("expressions", BuildExpressions(work));
        snapshot.Add("dependencies", BuildDependencies(work));
        snapshot.Add("support", BuildSupportSummary(work));
        snapshot.Add("session_parts", BuildPartInventory(session, state.CanonicalPath));
        snapshot.Add("support_summary", supportSummary);
        return snapshot;
    }

    private static List<object> BuildPartInventory(Session session, string workPath)
    {
        BasePart[] parts = session.Parts.ToArray();
        if (parts == null) throw new M2ModelException("MODEL_READ_FAILED", "Session.Parts inventory returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        bool foundWork = false;
        foreach (BasePart item in parts)
        {
            if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Session.Parts contains a null part");
            string path = item.FullPath ?? String.Empty;
            if (item.Tag == session.Parts.Work.Tag) foundWork = true;
            Dictionary<string, object> entry = new Dictionary<string, object>(StringComparer.Ordinal);
            entry.Add("path", path);
            entry.Add("tag", item.Tag.ToString());
            entry.Add("load_state", item.PartLoadState.ToString());
            entry.Add("is_read_only", IsPartReadOnly(item));
            entry.Add("is_modified", item.IsModified);
            records.Add(entry);
        }
        if (!foundWork) throw new M2ModelException("UNSUPPORTED_MODEL", "active Work Part is absent from Session.Parts inventory");
        records.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            return String.CompareOrdinal((string)left["path"], (string)right["path"]);
        });
        return ToObjectList(records);
    }

    private static Dictionary<string, object> BuildArtifactSupportSummary(Part work, string artifactScope)
    {
        bool assembly = work.ComponentAssembly != null && work.ComponentAssembly.RootComponent != null;
        bool ugmanagerActive;
        UFSession.GetUFSession().UF.IsUgmanagerActive(out ugmanagerActive);
        bool teamcenter = ugmanagerActive || (work.FullPath ?? String.Empty).StartsWith("@DB", StringComparison.OrdinalIgnoreCase);
        Feature[] features = work.Features.GetFeatures();
        Sketch[] sketches = work.Sketches.ToArray();
        if (features == null || sketches == null)
            throw new M2ModelException("MODEL_READ_FAILED", "native history enumeration returned null");

        int waveOrLinkFeatures = 0;
        int externalFeatureParents = 0;
        int interpartExpressions = 0;
        int featureBackedSketches = 0;
        HashSet<string> externalSketchGeometry = new HashSet<string>(StringComparer.Ordinal);
        foreach (Feature feature in features)
        {
            if (feature == null) throw new M2ModelException("UNSUPPORTED_MODEL", "feature history contains null");
            string type = feature.FeatureType ?? String.Empty;
            string upperType = type.ToUpperInvariant();
            if (feature is WaveLink || upperType.Contains("WAVE") || upperType.Contains("LINKED") || upperType.Contains("INTERPART"))
                waveOrLinkFeatures++;
            if (feature.IsOccurrence) externalFeatureParents++;
            Feature[] parents = feature.GetParents();
            if (parents == null) throw new M2ModelException("MODEL_READ_FAILED", "feature parent enumeration returned null");
            foreach (Feature parent in parents)
            {
                if (parent == null || parent.IsOccurrence || parent.OwningPart == null || parent.OwningPart.Tag != work.Tag)
                    externalFeatureParents++;
            }
        }
        Expression[] expressions = work.Expressions.ToArray();
        if (expressions == null) throw new M2ModelException("MODEL_READ_FAILED", "expression history enumeration returned null");
        foreach (Expression expression in expressions)
        {
            if (expression == null) throw new M2ModelException("UNSUPPORTED_MODEL", "expression history contains null");
            if (expression.IsInterpartExpression) interpartExpressions++;
        }
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch collection contains null");
            if (sketch.Feature != null && !sketch.Feature.IsOccurrence && sketch.Feature.OwningPart != null &&
                sketch.Feature.OwningPart.Tag == work.Tag) featureBackedSketches++;
            NXObject[] geometry = sketch.GetAllGeometry();
            if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch geometry enumeration returned null");
            foreach (NXObject item in geometry)
            {
                if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch geometry contains null");
                if (item.IsOccurrence || item.OwningPart == null || item.OwningPart.Tag != work.Tag)
                    externalSketchGeometry.Add(item.Tag.ToString());
            }
            foreach (Sketch.ConstraintType constraintType in Enum.GetValues(typeof(Sketch.ConstraintType)))
            {
                if (constraintType == Sketch.ConstraintType.NoCon || constraintType == Sketch.ConstraintType.LastConType) continue;
                SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, constraintType);
                if (constraints == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch constraint enumeration returned null");
                foreach (SketchConstraint constraint in constraints)
                {
                    SketchGeometricConstraint geometric = constraint as SketchGeometricConstraint;
                    if (geometric != null)
                    {
                        Sketch.ConstraintGeometry[] operands = geometric.GetGeometry();
                        if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch constraint geometry enumeration returned null");
                        foreach (Sketch.ConstraintGeometry operand in operands)
                        {
                            NXObject item = operand.Geometry;
                            if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch constraint geometry contains null");
                            if (item.IsOccurrence || item.OwningPart == null || item.OwningPart.Tag != work.Tag)
                                externalSketchGeometry.Add(item.Tag.ToString());
                        }
                    }
                    SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                    if (dimension != null)
                    {
                        Sketch.DimensionGeometry[] operands = dimension.GetDimensionGeometry();
                        if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "sketch dimension geometry enumeration returned null");
                        foreach (Sketch.DimensionGeometry operand in operands)
                        {
                            NXObject item = operand.Geometry;
                            if (item == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch dimension geometry contains null");
                            if (item.IsOccurrence || item.OwningPart == null || item.OwningPart.Tag != work.Tag)
                                externalSketchGeometry.Add(item.Tag.ToString());
                        }
                    }
                }
            }
        }

        ExternalFileReference[] fileReferences = null;
        ExternalFileReferenceManager referenceManager = work.ExternalFileReferenceManager;
        if (referenceManager != null) referenceManager.GetExternalFileReferences(out fileReferences);
        int externalFileReferenceCount = fileReferences == null ? 0 : fileReferences.Length;

        string rejectionCode = null;
        string rejectionReason = null;
        if (assembly)
        {
            rejectionCode = "ASSEMBLY_UNSUPPORTED";
            rejectionReason = "M5 supports a standalone native part; assembly Work Parts are not supported";
        }
        else if (teamcenter)
        {
            rejectionCode = "TEAMCENTER_UNSUPPORTED";
            rejectionReason = "Teamcenter managed parts are outside the M5 independent-part scope";
        }
        else if (externalFileReferenceCount > 0 || interpartExpressions > 0 || externalFeatureParents > 0 ||
            externalSketchGeometry.Count > 0 || waveOrLinkFeatures > 0)
        {
            rejectionCode = "EXTERNAL_REFERENCE_UNSUPPORTED";
            rejectionReason = "M5 does not modify parts with external, interpart, occurrence, or WAVE/linked history references";
        }
        else if (!work.IsFullyLoaded)
        {
            rejectionCode = "PART_NOT_FULLY_LOADED";
            rejectionReason = "M5 requires the standalone Work Part to be fully loaded before inspection or modification";
        }

        Dictionary<string, object> dependencies = new Dictionary<string, object>(StringComparer.Ordinal);
        dependencies.Add("external_file_reference_count", externalFileReferenceCount);
        dependencies.Add("interpart_expression_count", interpartExpressions);
        dependencies.Add("external_feature_parent_count", externalFeatureParents);
        dependencies.Add("external_sketch_geometry_count", externalSketchGeometry.Count);
        dependencies.Add("wave_or_link_feature_count", waveOrLinkFeatures);
        Dictionary<string, object> featureSupport = BuildSupportSummary(work);
        List<object> unsupportedFeatures = featureSupport["unsupported"] as List<object>;
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("status", IsPartReadOnly(work) ? "READ_ONLY_INSPECTION" : "SUPPORTED_LIMITED");
        result.Add("artifact_scope", artifactScope);
        result.Add("history_mode", featureBackedSketches > 0 ? "NATIVE_SKETCH_HISTORY" : "APPEND_ONLY");
        result.Add("history_feature_count", features.Length);
        result.Add("sketch_count", sketches.Length);
        result.Add("feature_backed_sketch_count", featureBackedSketches);
        result.Add("driving_edit_scope", featureBackedSketches > 0
            ? "feature-backed Sketches with an actual Driving dimension and a supported M2 geometry signature"
            : "unsupported_without_feature_backed_sketch; appended M3 features remain available");
        result.Add("append_scope", "limited native sketch, extrusion, solid/sheet Revolve, planar normal HolePackage, external detailed single-start 60-degree thread and three-guide trapezoidal helical groove operations");
        result.Add("unsupported_feature_parameter_count", unsupportedFeatures == null ? 0 : unsupportedFeatures.Count);
        result.Add("dependencies", dependencies);
        result.Add("is_read_only", IsPartReadOnly(work));
        result.Add("rejection_code", rejectionCode);
        result.Add("rejection_reason", rejectionReason);
        return result;
    }

    private static List<object> BuildBodies(Part work)
    {
        Body[] bodies = work.Bodies.ToArray();
        if (bodies == null) throw new M2ModelException("MODEL_READ_FAILED", "Bodies.ToArray returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Body body in bodies)
        {
            if (body == null) throw new M2ModelException("UNSUPPORTED_MODEL", "body collection contains null");
            string id = RequiredJournalId(body, "body");
            Face[] faces = body.GetFaces();
            Edge[] edges = body.GetEdges();
            if (faces == null || edges == null) throw new M2ModelException("MODEL_READ_FAILED", "body face/edge enumeration returned null");
            string bodyTagError;
            string bodyTag = SafeNxTagText(body, out bodyTagError);
            if (String.IsNullOrEmpty(bodyTag))
                throw new M2ModelException("UNSUPPORTED_MODEL", "body has no stable session Tag" +
                    (bodyTagError == null ? String.Empty : " (" + bodyTagError + ")"));
            bool bodyIsSolid = body.IsSolidBody;
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("journal_id", id);
            item.Add("kind", bodyIsSolid ? "SOLID_BODY" : "SHEET_BODY");
            item.Add("is_solid", bodyIsSolid);
            List<string> faceIds;
            List<object> faceRefs = BuildBodyFaceReferences(work, body, id, bodyTag, bodyIsSolid, faces, out faceIds);
            item.Add("face_ids", ObjectStrings(faceIds));
            item.Add("face_refs", faceRefs);
            item.Add("edge_ids", ObjectStrings(GetJournalIds(edges, "edge")));
            M3BodyMeasure measured = MeasureBody(work, body);
            item.Add("volume_mm3", measured.VolumeMm3.HasValue ? (object)measured.VolumeMm3.Value : null);
            item.Add("bbox_mm", measured.BboxMm == null ? null : NumberList(measured.BboxMm));
            item.Add("measurement_status", measured.BboxMm == null ? "PART_UNITS_MIXED" : "MEASURED");
            int consistencyFaults; int[] consistencyTypes; Tag[] consistencyTags;
            UFSession.GetUFSession().Modl.AskBodyConsistency(body.Tag, out consistencyFaults, out consistencyTypes, out consistencyTags);
            item.Add("body_consistency_fault_count", consistencyFaults);
            records.Add(item);
        }
        SortRecords(records, "journal_id", "body");
        return ToObjectList(records);
    }

    private static M3BodyMeasure MeasureBody(Part work, Body body)
    {
        return MeasureBody(work, body, M3LegacyMassPropertyAccuracy);
    }

    private static M3BodyMeasure MeasureBody(Part work, Body body, double massPropertyAccuracy)
    {
        if (work == null || body == null) throw new M2ModelException("MODEL_READ_FAILED", "body measurement requires a Work Part and Body");
        M3BodyMeasure measured = new M3BodyMeasure();
        measured.Body = body;
        measured.Id = RequiredJournalId(body, "body");
        measured.IsSolid = body.IsSolidBody;
        if (body.IsSolidBody)
        {
            Unit[] units = new Unit[4];
            units[0] = work.UnitCollection.FindObject("Kilogram");
            units[1] = work.UnitCollection.FindObject("MilliMeter");
            units[2] = work.UnitCollection.FindObject("SquareMilliMeter");
            units[3] = work.UnitCollection.FindObject("CubicMilliMeter");
            if (units[0] == null || units[1] == null || units[2] == null || units[3] == null)
                throw new M2ModelException("MODEL_READ_FAILED", "NX millimeter mass-property units are unavailable");
            MeasureBodies mass = null;
            try
            {
                mass = work.MeasureManager.NewMassProperties(units, massPropertyAccuracy, new Body[] { body });
                if (mass == null || Double.IsNaN(mass.Volume) || Double.IsInfinity(mass.Volume))
                    throw new M2ModelException("MODEL_READ_FAILED", "NX returned no finite solid volume");
                measured.VolumeMm3 = mass.Volume;
            }
            finally { if (mass != null) mass.Dispose(); }
        }

        if (work.PartUnits == BasePart.Units.Mix)
        {
            measured.BboxMm = null;
        }
        else
        {
            double[] box = M7ExactBox(body.Tag);
            double scale = work.PartUnits == BasePart.Units.Inches ? 25.4 :
                (work.PartUnits == BasePart.Units.Meters ? 1000.0 :
                (work.PartUnits == BasePart.Units.Micrometers ? 0.001 : 1.0));
            for (int i = 0; i < box.Length; i++)
            {
                box[i] *= scale;
                if (Double.IsNaN(box[i]) || Double.IsInfinity(box[i]))
                    throw new M2ModelException("MODEL_READ_FAILED", "NX returned a non-finite body bounding box");
            }
            measured.BboxMm = box;
        }
        measured.PublicRecord = new Dictionary<string, object>(StringComparer.Ordinal);
        measured.PublicRecord.Add("journal_id", measured.Id);
        measured.PublicRecord.Add("is_solid", measured.IsSolid);
        measured.PublicRecord.Add("volume_mm3", measured.VolumeMm3.HasValue ? (object)measured.VolumeMm3.Value : null);
        measured.PublicRecord.Add("bbox_mm", measured.BboxMm == null ? null : NumberList(measured.BboxMm));
        measured.PublicRecord.Add("measurement_status", measured.BboxMm == null ? "PART_UNITS_MIXED" : "MEASURED");
        return measured;
    }

    private static List<object> NumberList(double[] values)
    {
        List<object> result = new List<object>();
        foreach (double value in values) result.Add(value);
        return result;
    }

    private static List<object> NumberTextList(double[] values)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", "numeric curve data is null");
        List<object> result = new List<object>();
        foreach (double value in values) result.Add(NumberText(value));
        return result;
    }

    private static List<object> BuildFeatures(Part work)
    {
        Feature[] features = work.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "Features.GetFeatures returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Feature feature in features)
        {
            if (feature == null) throw new M2ModelException("UNSUPPORTED_MODEL", "feature collection contains null");
            string id = RequiredJournalId(feature, "feature");
            string type = feature.FeatureType;
            if (String.IsNullOrEmpty(type)) throw new M2ModelException("UNSUPPORTED_MODEL", "feature " + id + " has no FeatureType");
            Feature[] parents = feature.GetParents();
            Feature[] children = feature.GetChildren();
            Body[] bodies = feature.GetBodies();
            Expression[] expressions = feature.GetExpressions();
            if (parents == null || children == null || bodies == null || expressions == null)
                throw new M2ModelException("MODEL_READ_FAILED", "feature relationship enumeration returned null for " + id);

            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("journal_id", id);
            item.Add("feature_type", type);
            item.Add("suppressed", feature.Suppressed);
            item.Add("parent_ids", ObjectStrings(GetJournalIds(parents, "feature parent")));
            item.Add("child_ids", ObjectStrings(GetJournalIds(children, "feature child")));
            item.Add("body_ids", ObjectStrings(GetJournalIds(bodies, "feature body")));
            item.Add("expression_ids", ObjectStrings(GetExpressionIds(expressions)));
            item.Add("parameter_support", ParameterSupport(type));
            DatumPlane datumPlane = TryGetM3DatumPlane(feature);
            if (datumPlane != null)
                item.Add("datum_plane", M3DatumPlaneDefinitionRecord(work, datumPlane, feature));
            if (M7HasNativeDefinition(feature))
            {
                item.Add("native_definition", CaptureM7FeatureDefinition(work, feature));
                item["parameter_support"] = new Dictionary<string, object> {
                    { "status", "READ_ONLY_NATIVE_PARAMETERS" },
                    { "reason", "Native parameter and association readback is available; edits remain limited to supported Driving sketch dimensions." } };
            }
            records.Add(item);
        }
        SortRecords(records, "journal_id", "feature");
        return ToObjectList(records);
    }

    private static DatumPlane TryGetM3DatumPlane(Feature feature)
    {
        DatumPlaneFeature datumFeature = feature as DatumPlaneFeature;
        return datumFeature == null ? null : datumFeature.DatumPlane;
    }

    private static DatumPlane FindM3DatumPlane(Part work, string datumPlaneId, out Feature feature)
    {
        if (String.IsNullOrWhiteSpace(datumPlaneId))
            throw new M2ModelException("INVALID_REQUEST", "support_datum_plane_id is required");
        feature = FindFeature(work, datumPlaneId);
        DatumPlane datum = TryGetM3DatumPlane(feature);
        if (datum == null)
            throw new M2ModelException("SUPPORT_NOT_FOUND", "support_datum_plane_id must identify a native DatumPlane feature");
        return datum;
    }

    private static Line FindM3LineById(Part work, string lineId)
    {
        return FindM3LineById(work, lineId, "axis_line_id");
    }

    private static Line FindM3LineById(Part work, string lineId, string fieldName)
    {
        if (work == null || String.IsNullOrWhiteSpace(lineId))
            throw new M2ModelException("INVALID_REQUEST", fieldName + " is required");
        Curve[] curves = work.Curves.ToArray();
        if (curves == null) throw new M2ModelException("MODEL_READ_FAILED", "Part.Curves returned null while resolving " + fieldName);
        Line found = null;
        int matches = 0;
        for (int i = 0; i < curves.Length; i++)
        {
            Curve curve = curves[i];
            if (curve == null) throw new M2ModelException("UNSUPPORTED_MODEL", "Part.Curves contains a null object");
            if (!String.Equals(RequiredJournalId(curve, fieldName + " curve"), lineId, StringComparison.Ordinal)) continue;
            matches++;
            found = curve as Line;
            if (found == null)
                throw new M2ModelException("UNSUPPORTED_AXIS", fieldName + " must identify a native NX Line; other curve types are not supported");
        }
        if (matches == 0) throw new M2ModelException("AXIS_LINE_NOT_FOUND", fieldName + " does not identify a curve in the active Work Part");
        if (matches != 1) throw new M2ModelException("AXIS_LINE_AMBIGUOUS", fieldName + " identifies more than one curve in the active Work Part");
        return found;
    }

    private static void ValidateM3TwoLineDatumPlane(Line line1, Line line2,
        out double[] line1Start, out double[] line1End,
        out double[] line2Start, out double[] line2End,
        out double[] planeOrigin, out double[] planeNormal)
    {
        if (line1 == null || line2 == null)
            throw new M2ModelException("INVALID_REQUEST", "two-line datum plane requires two native NX Lines");
        if (line1.Tag.Equals(line2.Tag))
            throw new M2ModelException("INVALID_REQUEST", "line1_id and line2_id resolve to the same native Line");

        Point3d firstStart = line1.StartPoint;
        Point3d firstEnd = line1.EndPoint;
        Point3d secondStart = line2.StartPoint;
        Point3d secondEnd = line2.EndPoint;
        line1Start = M3LinePoint(firstStart, "line1 start point");
        line1End = M3LinePoint(firstEnd, "line1 end point");
        line2Start = M3LinePoint(secondStart, "line2 start point");
        line2End = M3LinePoint(secondEnd, "line2 end point");

        double[] direction1 = new double[] {
            line1End[0] - line1Start[0], line1End[1] - line1Start[1], line1End[2] - line1Start[2] };
        double[] direction2 = new double[] {
            line2End[0] - line2Start[0], line2End[1] - line2Start[1], line2End[2] - line2Start[2] };
        double length1 = Length3(direction1);
        double length2 = Length3(direction2);
        if (!IsFinite(length1) || !IsFinite(length2) ||
            length1 <= M3DatumLineLengthToleranceMm || length2 <= M3DatumLineLengthToleranceMm)
            throw new M2ModelException("INVALID_REQUEST", "two-line datum plane does not accept a degenerate Line of length <= 1e-6 mm");
        direction1[0] /= length1; direction1[1] /= length1; direction1[2] /= length1;
        direction2[0] /= length2; direction2[1] /= length2; direction2[2] /= length2;

        planeOrigin = (double[])line1Start.Clone();
        double[] betweenStarts = new double[] {
            line2Start[0] - line1Start[0], line2Start[1] - line1Start[1], line2Start[2] - line1Start[2] };
        double[] directionCross = Cross3(direction1, direction2);
        double crossLength = Length3(directionCross);
        if (!IsFinite(crossLength))
            throw new M2ModelException("INVALID_REQUEST", "line directions do not define a finite plane");

        if (crossLength <= M3DatumLineParallelTolerance)
        {
            double along = Dot3(betweenStarts, direction1);
            double[] separation = new double[] {
                betweenStarts[0] - along * direction1[0],
                betweenStarts[1] - along * direction1[1],
                betweenStarts[2] - along * direction1[2] };
            double separationLength = Length3(separation);
            if (!IsFinite(separationLength) || separationLength <= M3DatumLinePlaneToleranceMm)
                throw new M2ModelException("INVALID_REQUEST", "two-line datum plane does not accept coincident or collinear Lines");
            planeNormal = Cross3(direction1, separation);
            double normalLength = Length3(planeNormal);
            if (!IsFinite(normalLength) || normalLength <= M3DatumLinePlaneToleranceMm)
                throw new M2ModelException("INVALID_REQUEST", "parallel Lines do not define a stable common plane");
            planeNormal[0] /= normalLength; planeNormal[1] /= normalLength; planeNormal[2] /= normalLength;
        }
        else
        {
            planeNormal = new double[] {
                directionCross[0] / crossLength,
                directionCross[1] / crossLength,
                directionCross[2] / crossLength };
        }

        // Keep the four checks explicit so each residual uses the same prepared plane frame.
        double[] residuals = new double[] {
            Dot3(new double[] { line1Start[0] - planeOrigin[0], line1Start[1] - planeOrigin[1], line1Start[2] - planeOrigin[2] }, planeNormal),
            Dot3(new double[] { line1End[0] - planeOrigin[0], line1End[1] - planeOrigin[1], line1End[2] - planeOrigin[2] }, planeNormal),
            Dot3(new double[] { line2Start[0] - planeOrigin[0], line2Start[1] - planeOrigin[1], line2Start[2] - planeOrigin[2] }, planeNormal),
            Dot3(new double[] { line2End[0] - planeOrigin[0], line2End[1] - planeOrigin[1], line2End[2] - planeOrigin[2] }, planeNormal)
        };
        for (int i = 0; i < residuals.Length; i++)
            if (!IsFinite(residuals[i]) || Math.Abs(residuals[i]) > M3DatumLinePlaneToleranceMm)
                throw new M2ModelException("INVALID_REQUEST", "selected Lines are not coplanar within 1e-6 mm, or do not intersect as infinite lines");
    }

    private static double[] M3LinePoint(Point3d point, string label)
    {
        if (!IsFinite(point.X) || !IsFinite(point.Y) || !IsFinite(point.Z))
            throw new M2ModelException("INVALID_REQUEST", "NX returned a non-finite " + label);
        return new double[] { point.X, point.Y, point.Z };
    }

    private static bool IsFinite(double value)
    {
        return !Double.IsNaN(value) && !Double.IsInfinity(value);
    }

    private static bool M3UnitVectorsNearUnoriented(double[] left, double[] right, double tolerance)
    {
        if (left == null || right == null || left.Length != 3 || right.Length != 3) return false;
        double leftLength = Length3(left);
        double rightLength = Length3(right);
        if (!IsFinite(leftLength) || !IsFinite(rightLength) || leftLength <= 1e-12 || rightLength <= 1e-12)
            return false;
        double[] unitLeft = new double[] { left[0] / leftLength, left[1] / leftLength, left[2] / leftLength };
        double[] unitRight = new double[] { right[0] / rightLength, right[1] / rightLength, right[2] / rightLength };
        if (Dot3(unitLeft, unitRight) < 0.0)
        {
            unitRight[0] = -unitRight[0]; unitRight[1] = -unitRight[1]; unitRight[2] = -unitRight[2];
        }
        return VectorNearArray(unitLeft, unitRight, tolerance);
    }

    private static List<string> M3LineSourceFeatureIds(Part work, Line line)
    {
        if (work == null || line == null)
            throw new M2ModelException("MODEL_READ_FAILED", "Work Part or native Line is unavailable while resolving sketch source features");
        Sketch[] sketches = work.Sketches.ToArray();
        if (sketches == null)
            throw new M2ModelException("MODEL_READ_FAILED", "Sketches.ToArray returned null while resolving Line source features");
        HashSet<string> featureIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null)
                throw new M2ModelException("UNSUPPORTED_MODEL", "Sketch collection contains null while resolving Line source features");
            NXObject[] geometry = sketch.GetAllGeometry();
            if (geometry == null)
                throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null while resolving Line source features");
            bool containsLine = false;
            foreach (NXObject item in geometry)
            {
                if (item == null)
                    throw new M2ModelException("UNSUPPORTED_MODEL", "Sketch geometry contains null while resolving Line source features");
                if (item.Tag.Equals(line.Tag)) containsLine = true;
            }
            if (!containsLine) continue;
            if (sketch.Feature == null)
                throw new M2ModelException("UNSUPPORTED_MODEL", "selected Line belongs to a Sketch without a native source Feature");
            Feature source = sketch.Feature;
            if (source.IsOccurrence || source.OwningPart == null || source.OwningPart.Tag != work.Tag)
                throw new M2ModelException("UNSUPPORTED_MODEL", "selected Line belongs to sketch geometry outside the current Work Part");
            featureIds.Add(RequiredJournalId(source, "Line source sketch feature"));
        }
        List<string> result = new List<string>(featureIds);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static void ValidateM3AngleAxisLine(Line line, double[] referenceOrigin, double[] referenceNormal,
        out double[] start, out double[] end)
    {
        if (line == null || referenceOrigin == null || referenceOrigin.Length != 3 ||
            referenceNormal == null || referenceNormal.Length != 3)
            throw new M2ModelException("INVALID_REQUEST", "angle axis or support plane frame is unavailable");
        Point3d first = line.StartPoint;
        Point3d last = line.EndPoint;
        start = new double[] { first.X, first.Y, first.Z };
        end = new double[] { last.X, last.Y, last.Z };
        double[] direction = new double[] { last.X - first.X, last.Y - first.Y, last.Z - first.Z };
        double length = Length3(direction);
        if (Double.IsNaN(length) || Double.IsInfinity(length) || length <= M3VectorTolerance)
            throw new M2ModelException("INVALID_REQUEST", "axis_line_id must identify a nondegenerate native NX Line");
        direction[0] /= length;
        direction[1] /= length;
        direction[2] /= length;
        double normalLength = Length3(referenceNormal);
        if (Double.IsNaN(normalLength) || Double.IsInfinity(normalLength) || normalLength <= M3VectorTolerance)
            throw new M2ModelException("INVALID_REQUEST", "angle support plane normal is invalid");
        double[] unitNormal = new double[] {
            referenceNormal[0] / normalLength, referenceNormal[1] / normalLength, referenceNormal[2] / normalLength };
        if (Math.Abs(Dot3(direction, unitNormal)) > M3VectorTolerance)
            throw new M2ModelException("INVALID_REQUEST", "axis Line direction must lie in the selected reference plane");
        double[] startDelta = new double[] { first.X - referenceOrigin[0], first.Y - referenceOrigin[1], first.Z - referenceOrigin[2] };
        double[] endDelta = new double[] { last.X - referenceOrigin[0], last.Y - referenceOrigin[1], last.Z - referenceOrigin[2] };
        if (Math.Abs(Dot3(startDelta, unitNormal)) > M3FrameToleranceMm ||
            Math.Abs(Dot3(endDelta, unitNormal)) > M3FrameToleranceMm)
            throw new M2ModelException("INVALID_REQUEST", "axis Line must lie in the selected reference plane");
    }

    private static double[] RotateM3VectorAroundAxis(double[] vector, double[] axis, double angleRadians)
    {
        double axisLength = Length3(axis);
        if (axisLength <= M3VectorTolerance || Double.IsNaN(axisLength) || Double.IsInfinity(axisLength))
            throw new M2ModelException("INVALID_REQUEST", "angle axis must be nondegenerate");
        double[] unitAxis = new double[] { axis[0] / axisLength, axis[1] / axisLength, axis[2] / axisLength };
        double cosine = Math.Cos(angleRadians);
        double sine = Math.Sin(angleRadians);
        double[] cross = Cross3(unitAxis, vector);
        double along = Dot3(unitAxis, vector) * (1.0 - cosine);
        double[] result = new double[] {
            vector[0] * cosine + cross[0] * sine + unitAxis[0] * along,
            vector[1] * cosine + cross[1] * sine + unitAxis[1] * along,
            vector[2] * cosine + cross[2] * sine + unitAxis[2] * along };
        Normalize3(result);
        return result;
    }

    private static double SignedM3AngleDeg(double[] fromNormal, double[] toNormal, double[] axis)
    {
        double sine = Dot3(axis, Cross3(fromNormal, toNormal));
        double cosine = Dot3(fromNormal, toNormal);
        if (cosine > 1.0) cosine = 1.0;
        else if (cosine < -1.0) cosine = -1.0;
        return Math.Atan2(sine, cosine) * 180.0 / Math.PI;
    }

    private static double CircularAngleDifferenceDeg(double left, double right)
    {
        double difference = (left - right) % 360.0;
        if (difference > 180.0) difference -= 360.0;
        else if (difference < -180.0) difference += 360.0;
        return Math.Abs(difference);
    }

    private static Dictionary<string, object> M3DatumPlaneDefinitionRecord(Part work, DatumPlane datum, Feature feature)
    {
        if (work == null || datum == null || feature == null)
            throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "DatumPlane or its native feature is unavailable");
        Point3d origin = datum.Origin;
        Vector3d rawNormal = datum.Normal;
        double[] normal = new double[] { rawNormal.X, rawNormal.Y, rawNormal.Z };
        double normalLength = Length3(normal);
        if (normalLength <= M3VectorTolerance || Double.IsNaN(normalLength) || Double.IsInfinity(normalLength))
            throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "DatumPlane returned an invalid normal");
        normal[0] /= normalLength; normal[1] /= normalLength; normal[2] /= normalLength;
        Dictionary<string, object> associatedDefinition = M3DatumPlaneNativeDefinitionRecord(work, datum, feature);
        bool twoLineDefinition = associatedDefinition != null && associatedDefinition.ContainsKey("mode") &&
            String.Equals(Convert.ToString(associatedDefinition["mode"], CultureInfo.InvariantCulture), "two_lines", StringComparison.Ordinal);
        Point3d p0, p1, p2, p3;
        datum.GetCornerPoints(out p0, out p1, out p2, out p3);
        double[] xAxis = new double[] { p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z };
        double projection = Dot3(xAxis, normal);
        xAxis[0] -= projection * normal[0]; xAxis[1] -= projection * normal[1]; xAxis[2] -= projection * normal[2];
        double xLength = Length3(xAxis);
        string twoLineXAxisSource = "datum_corner_edge";
        if (xLength <= M3VectorTolerance || Double.IsNaN(xLength) || Double.IsInfinity(xLength))
        {
            if (!twoLineDefinition)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "DatumPlane corner frame does not define an in-plane x axis");
            double[] line1Start = M3ReadNumberArray(associatedDefinition.ContainsKey("line1_start_mm")
                ? associatedDefinition["line1_start_mm"] : null, 3);
            double[] line1End = M3ReadNumberArray(associatedDefinition.ContainsKey("line1_end_mm")
                ? associatedDefinition["line1_end_mm"] : null, 3);
            if (line1Start == null || line1End == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "two-line DatumPlane cannot derive an in-plane x axis from its native line1 endpoints");
            xAxis = new double[] { line1End[0] - line1Start[0], line1End[1] - line1Start[1], line1End[2] - line1Start[2] };
            projection = Dot3(xAxis, normal);
            xAxis[0] -= projection * normal[0]; xAxis[1] -= projection * normal[1]; xAxis[2] -= projection * normal[2];
            xLength = Length3(xAxis);
            if (!IsFinite(xLength) || xLength <= M3DatumLineLengthToleranceMm)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "two-line DatumPlane line1 direction does not define a stable in-plane x axis");
            twoLineXAxisSource = "line1_direction_projected";
        }
        xAxis[0] /= xLength; xAxis[1] /= xLength; xAxis[2] /= xLength;
        double[] yAxis = Cross3(normal, xAxis);
        Normalize3(yAxis);
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("id", RequiredJournalId(feature, "datum plane feature"));
        result.Add("feature_type", feature.FeatureType);
        result.Add("origin", Point(origin));
        result.Add("normal", NumberList(normal));
        result.Add("x_axis", NumberList(xAxis));
        result.Add("y_axis", NumberList(yAxis));
        result.Add("corner_points", new List<object> { Point(p0), Point(p1), Point(p2), Point(p3) });
        result.Add("native", true);
        if (twoLineDefinition) result.Add("x_axis_source", twoLineXAxisSource);
        if (associatedDefinition != null)
            foreach (KeyValuePair<string, object> item in associatedDefinition) result.Add(item.Key, item.Value);
        return result;
    }

    private static Dictionary<string, object> M3DatumPlaneNativeDefinitionRecord(Part work,
        DatumPlane datum, Feature feature)
    {
        Feature[] featureParents = feature.GetParents();
        if (featureParents == null)
            throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "DatumPlane parent-feature list is unavailable");
        DatumPlaneBuilder builder = null;
        try
        {
            builder = work.Features.CreateDatumPlaneBuilder(feature);
            Plane definingPlane = builder.GetPlane();
            if (definingPlane == null) return null;
            if (definingPlane.Method == PlaneTypes.MethodType.TwoLines)
                return M3DatumPlaneTwoLinesDefinitionRecord(work, datum, feature, definingPlane, featureParents);
            // Angle references have a support Face/DatumPlane parent; an independent axis Line need not contribute one.
            if (featureParents.Length == 0) return null;
            if (definingPlane.Method == PlaneTypes.MethodType.Angle)
                return M3DatumPlaneAngleDefinitionRecord(work, datum, feature, definingPlane);
            if (definingPlane.Method != PlaneTypes.MethodType.Distance) return null;

            NXObject[] geometry = definingPlane.GetGeometry();
            if (geometry == null || geometry.Length != 1 || geometry[0] == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "offset DatumPlane must retain exactly one native reference object");
            Expression offsetExpression = definingPlane.Expression;
            if (offsetExpression == null || offsetExpression.Units == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "offset DatumPlane has no length expression for its native distance");
            Unit millimeter = work.UnitCollection.FindObject("MilliMeter");
            if (millimeter == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "NX millimeter unit is unavailable for offset readback");
            double expressionDistance = work.UnitCollection.Convert(offsetExpression.Units, millimeter,
                offsetExpression.Value);
            if (Double.IsNaN(expressionDistance) || Double.IsInfinity(expressionDistance))
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "offset expression returned a non-finite value");

            double[] referenceOrigin;
            double[] referenceNormal;
            string supportKind;
            string supportId;
            string supportBodyId = null;
            string supportFaceTag = null;
            List<string> referenceParentIds = new List<string>();
            DatumPlane referenceDatum = geometry[0] as DatumPlane;
            Feature referenceFeature = geometry[0] as Feature;
            if (referenceDatum == null && referenceFeature != null)
                referenceDatum = TryGetM3DatumPlane(referenceFeature);
            if (referenceDatum != null)
            {
                referenceFeature = referenceDatum.Feature;
                if (referenceFeature == null)
                    throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "offset reference DatumPlane has no native parent feature");
                Point3d referencePoint = referenceDatum.Origin;
                Vector3d referenceVector = referenceDatum.Normal;
                referenceOrigin = new double[] { referencePoint.X, referencePoint.Y, referencePoint.Z };
                referenceNormal = new double[] { referenceVector.X, referenceVector.Y, referenceVector.Z };
                Normalize3(referenceNormal);
                supportKind = "DATUM_PLANE";
                supportId = RequiredJournalId(referenceFeature, "offset support DatumPlane feature");
                referenceParentIds.Add(supportId);
            }
            else
            {
                Face referenceFace = geometry[0] as Face;
                if (referenceFace == null)
                    throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "offset DatumPlane reference is neither a planar Face nor a DatumPlane");
                M3ReadPlanarFaceReference(referenceFace, out referenceOrigin, out referenceNormal);
                supportKind = "FACE";
                supportId = RequiredJournalId(referenceFace, "offset support Face");
                Body referenceBody = referenceFace.GetBody();
                if (referenceBody == null)
                    throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "offset support Face has no owning Body");
                supportBodyId = RequiredJournalId(referenceBody, "offset support Body");
                supportFaceTag = referenceFace.Tag.ToString();
                referenceParentIds = M3FaceParentFeatureIds(work, referenceFace);
            }

            Feature[] parents = feature.GetParents();
            if (parents == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "offset DatumPlane parent-feature list is unavailable");
            List<string> parentIds = new List<string>();
            foreach (Feature parent in parents)
            {
                if (parent == null)
                    throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "offset DatumPlane parent-feature list contains null");
                parentIds.Add(RequiredJournalId(parent, "offset DatumPlane parent feature"));
            }
            bool parentMatches = false;
            foreach (string referenceParentId in referenceParentIds)
                if (parentIds.Contains(referenceParentId)) parentMatches = true;

            Point3d actualOrigin = datum.Origin;
            Vector3d actualRawNormal = datum.Normal;
            double[] actualNormal = new double[] { actualRawNormal.X, actualRawNormal.Y, actualRawNormal.Z };
            Normalize3(actualNormal);
            double[] originDelta = new double[] { actualOrigin.X - referenceOrigin[0],
                actualOrigin.Y - referenceOrigin[1], actualOrigin.Z - referenceOrigin[2] };
            double geometricDistance = Dot3(originDelta, referenceNormal);
            if (Double.IsNaN(geometricDistance) || Double.IsInfinity(geometricDistance))
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "offset DatumPlane geometry returned a non-finite reference distance");

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("mode", "offset");
            result.Add("distance_mm", geometricDistance);
            result.Add("native_expression_distance_mm", expressionDistance);
            result.Add("native_offset_expression", offsetExpression.RightHandSide);
            result.Add("native_offset_flip", definingPlane.OffsetFlip);
            result.Add("native_method", definingPlane.Method.ToString());
            result.Add("support_kind", supportKind);
            if (supportKind == "DATUM_PLANE") result.Add("support_datum_plane_id", supportId);
            else
            {
                result.Add("support_face_id", supportId);
                result.Add("support_body_id", supportBodyId);
                result.Add("support_face_tag", supportFaceTag);
            }
            result.Add("reference_origin_mm", NumberList(referenceOrigin));
            result.Add("reference_normal", NumberList(referenceNormal));
            result.Add("native_parent_ids", ObjectStrings(parentIds));
            result.Add("reference_parent_ids", ObjectStrings(referenceParentIds));
            result.Add("parent_reference_matches", parentMatches);
            result.Add("native_reference_geometry", true);
            result.Add("normal_matches_reference", VectorNearArray(actualNormal, referenceNormal, M3VectorTolerance));
            result.Add("expression_matches_geometry", Math.Abs(expressionDistance - geometricDistance) <= M3FrameToleranceMm);
            return result;
        }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("DATUM_PLANE_READBACK_FAILED",
                "NX could not read the native offset DatumPlane definition (" + ex.GetType().Name + ")");
        }
        finally
        {
            if (builder != null) try { builder.Destroy(); } catch { }
        }
    }

    private static Dictionary<string, object> M3DatumPlaneTwoLinesDefinitionRecord(Part work,
        DatumPlane datum, Feature feature, Plane definingPlane, Feature[] featureParents)
    {
        try
        {
            NXObject[] geometry = definingPlane.GetGeometry();
            if (geometry == null || geometry.Length != 2 || geometry[0] == null || geometry[1] == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "two-line DatumPlane must retain exactly two native objects in Plane.GetGeometry");
            Line line1 = geometry[0] as Line;
            Line line2 = geometry[1] as Line;
            if (line1 == null || line2 == null || line1.Tag.Equals(line2.Tag))
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "two-line DatumPlane geometry must contain two distinct native NX Lines");
            if (line1.IsOccurrence || line2.IsOccurrence || line1.OwningPart == null || line2.OwningPart == null ||
                line1.OwningPart.Tag != work.Tag || line2.OwningPart.Tag != work.Tag)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "two-line DatumPlane references must remain native Lines in the current Work Part");

            double[] line1Start, line1End, line2Start, line2End, pairOrigin, pairNormal;
            ValidateM3TwoLineDatumPlane(line1, line2, out line1Start, out line1End,
                out line2Start, out line2End, out pairOrigin, out pairNormal);
            Point3d actualOriginPoint = datum.Origin;
            double[] actualOrigin = M3LinePoint(actualOriginPoint, "two-line DatumPlane origin");
            Vector3d rawNormal = datum.Normal;
            double[] actualNormal = new double[] { rawNormal.X, rawNormal.Y, rawNormal.Z };
            double actualNormalLength = Length3(actualNormal);
            if (!IsFinite(actualNormalLength) || actualNormalLength <= M3VectorTolerance)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "two-line DatumPlane returned an invalid normal");
            actualNormal[0] /= actualNormalLength; actualNormal[1] /= actualNormalLength; actualNormal[2] /= actualNormalLength;

            double[] line1Residuals = M3PlaneResiduals(line1Start, line1End, actualOrigin, actualNormal);
            double[] line2Residuals = M3PlaneResiduals(line2Start, line2End, actualOrigin, actualNormal);
            bool methodMatches = definingPlane.Method == PlaneTypes.MethodType.TwoLines;
            PlaneTypes.AlternateType alternate = definingPlane.GetAlternate();
            bool alternateMatches = alternate == PlaneTypes.AlternateType.Three;
            bool normalMatchesPair = M3UnitVectorsNearUnoriented(actualNormal, pairNormal,
                M3DatumLineDirectionTolerance);
            bool endpointsOnPlane = M3PlaneResidualsWithinTolerance(line1Residuals, M3DatumLinePlaneToleranceMm) &&
                M3PlaneResidualsWithinTolerance(line2Residuals, M3DatumLinePlaneToleranceMm);

            List<string> parentIds = new List<string>();
            foreach (Feature parent in featureParents)
            {
                if (parent == null)
                    throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "two-line DatumPlane parent-feature list contains null");
                parentIds.Add(RequiredJournalId(parent, "two-line DatumPlane parent feature"));
            }
            List<string> line1SourceFeatureIds = M3LineSourceFeatureIds(work, line1);
            List<string> line2SourceFeatureIds = M3LineSourceFeatureIds(work, line2);
            bool parentReferenceMatches = true;
            foreach (string sourceId in line1SourceFeatureIds)
                if (!parentIds.Contains(sourceId)) parentReferenceMatches = false;
            foreach (string sourceId in line2SourceFeatureIds)
                if (!parentIds.Contains(sourceId)) parentReferenceMatches = false;

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("mode", "two_lines");
            result.Add("feature_id", RequiredJournalId(feature, "two-line DatumPlane feature"));
            result.Add("native_method", definingPlane.Method.ToString());
            result.Add("native_alternate", alternate.ToString());
            result.Add("line1_id", RequiredJournalId(line1, "two-line DatumPlane line1"));
            result.Add("line2_id", RequiredJournalId(line2, "two-line DatumPlane line2"));
            result.Add("native_geometry_line_ids", new List<object> {
                RequiredJournalId(line1, "two-line DatumPlane line1"),
                RequiredJournalId(line2, "two-line DatumPlane line2") });
            result.Add("native_parent_ids", ObjectStrings(parentIds));
            result.Add("line1_source_feature_ids", ObjectStrings(line1SourceFeatureIds));
            result.Add("line2_source_feature_ids", ObjectStrings(line2SourceFeatureIds));
            result.Add("parent_reference_matches", parentReferenceMatches);
            result.Add("native_reference_geometry", true);
            result.Add("line1_start_mm", NumberList(line1Start));
            result.Add("line1_end_mm", NumberList(line1End));
            result.Add("line2_start_mm", NumberList(line2Start));
            result.Add("line2_end_mm", NumberList(line2End));
            result.Add("expected_plane_origin_mm", NumberList(pairOrigin));
            result.Add("expected_pair_normal", NumberList(pairNormal));
            result.Add("line1_endpoint_plane_residuals_mm", NumberList(line1Residuals));
            result.Add("line2_endpoint_plane_residuals_mm", NumberList(line2Residuals));
            result.Add("normal_matches_plane_unoriented", normalMatchesPair);
            result.Add("native_method_matches", methodMatches);
            result.Add("native_alternate_matches", alternateMatches);
            result.Add("all_line_endpoints_on_native_plane", endpointsOnPlane);
            return result;
        }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("DATUM_PLANE_READBACK_FAILED",
                "NX could not read the native two-line DatumPlane definition (" + ex.GetType().Name + ")");
        }
    }

    private static double[] M3PlaneResiduals(double[] start, double[] end, double[] origin, double[] normal)
    {
        return new double[] {
            Dot3(new double[] { start[0] - origin[0], start[1] - origin[1], start[2] - origin[2] }, normal),
            Dot3(new double[] { end[0] - origin[0], end[1] - origin[1], end[2] - origin[2] }, normal)
        };
    }

    private static bool M3PlaneResidualsWithinTolerance(double[] residuals, double tolerance)
    {
        return residuals != null && residuals.Length == 2 && IsFinite(residuals[0]) && IsFinite(residuals[1]) &&
            Math.Abs(residuals[0]) <= tolerance && Math.Abs(residuals[1]) <= tolerance;
    }

    private static Dictionary<string, object> M3DatumPlaneAngleDefinitionRecord(Part work,
        DatumPlane datum, Feature feature, Plane definingPlane)
    {
        try
        {
            NXObject[] geometry = definingPlane.GetGeometry();
            if (geometry == null || geometry.Length != 2 || geometry[0] == null || geometry[1] == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle DatumPlane must retain exactly one native support object and one native Line in Plane.GetGeometry");

            Line axisLine = null;
            Face referenceFace = null;
            DatumPlane referenceDatum = null;
            for (int i = 0; i < geometry.Length; i++)
            {
                NXObject item = geometry[i];
                Line line = item as Line;
                if (line != null)
                {
                    if (axisLine != null)
                        throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle DatumPlane geometry contains more than one axis Line");
                    axisLine = line;
                    continue;
                }
                Face face = item as Face;
                if (face != null)
                {
                    if (referenceFace != null || referenceDatum != null)
                        throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle DatumPlane geometry contains more than one support plane");
                    referenceFace = face;
                    continue;
                }
                DatumPlane candidateDatum = item as DatumPlane;
                Feature candidateFeature = item as Feature;
                if (candidateDatum == null && candidateFeature != null)
                    candidateDatum = TryGetM3DatumPlane(candidateFeature);
                if (candidateDatum != null)
                {
                    if (referenceFace != null || referenceDatum != null)
                        throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle DatumPlane geometry contains more than one support plane");
                    referenceDatum = candidateDatum;
                    continue;
                }
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle DatumPlane geometry contains an unsupported native object type");
            }
            if (axisLine == null || (referenceFace == null) == (referenceDatum == null))
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle DatumPlane geometry must contain one support Face or DatumPlane and one Line");

            double[] referenceOrigin;
            double[] referenceNormal;
            string supportKind;
            string supportId;
            string supportBodyId = null;
            string supportFaceTag = null;
            List<string> referenceParentIds;
            if (referenceDatum != null)
            {
                Feature referenceFeature = referenceDatum.Feature;
                if (referenceFeature == null)
                    throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle reference DatumPlane has no native parent feature");
                Point3d origin = referenceDatum.Origin;
                Vector3d normal = referenceDatum.Normal;
                referenceOrigin = new double[] { origin.X, origin.Y, origin.Z };
                referenceNormal = new double[] { normal.X, normal.Y, normal.Z };
                Normalize3(referenceNormal);
                supportKind = "DATUM_PLANE";
                supportId = RequiredJournalId(referenceFeature, "angle support DatumPlane feature");
                referenceParentIds = new List<string> { supportId };
            }
            else
            {
                M3ReadPlanarFaceReference(referenceFace, out referenceOrigin, out referenceNormal);
                supportKind = "FACE";
                supportId = RequiredJournalId(referenceFace, "angle support Face");
                Body body = referenceFace.GetBody();
                if (body == null)
                    throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle support Face has no owning Body");
                supportBodyId = RequiredJournalId(body, "angle support Body");
                supportFaceTag = referenceFace.Tag.ToString();
                referenceParentIds = M3FaceParentFeatureIds(work, referenceFace);
            }

            Point3d axisStart = axisLine.StartPoint;
            Point3d axisEnd = axisLine.EndPoint;
            double[] axisStartArray, axisEndArray;
            try
            {
                ValidateM3AngleAxisLine(axisLine, referenceOrigin, referenceNormal,
                    out axisStartArray, out axisEndArray);
            }
            catch (M2ModelException)
            {
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle axis Line is degenerate or no longer lies in its native support plane");
            }

            Expression angleExpression = definingPlane.Expression;
            if (angleExpression == null || angleExpression.Units == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle DatumPlane has no native angle expression");
            Unit degree = work.UnitCollection.FindObject("Degrees") as Unit;
            if (degree == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "NX degree unit is unavailable for angle expression readback");
            double expressionAngleDeg = work.UnitCollection.Convert(angleExpression.Units, degree, angleExpression.Value);
            if (Double.IsNaN(expressionAngleDeg) || Double.IsInfinity(expressionAngleDeg))
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle expression returned a non-finite degree value");

            Vector3d rawNormal = datum.Normal;
            double[] actualNormal = new double[] { rawNormal.X, rawNormal.Y, rawNormal.Z };
            Normalize3(actualNormal);
            Point3d actualOrigin = datum.Origin;
            double[] axisDirection = new double[] {
                axisEndArray[0] - axisStartArray[0], axisEndArray[1] - axisStartArray[1], axisEndArray[2] - axisStartArray[2] };
            Normalize3(axisDirection);
            double geometricAngleDeg = SignedM3AngleDeg(referenceNormal, actualNormal, axisDirection);
            double requestedRightHandAngleDeg = -expressionAngleDeg;
            double[] calculatedNormal = RotateM3VectorAroundAxis(referenceNormal, axisDirection,
                requestedRightHandAngleDeg * Math.PI / 180.0);
            bool expressionMatchesGeometry = CircularAngleDifferenceDeg(expressionAngleDeg, -geometricAngleDeg) <= M3AngleToleranceDeg;
            double[] actualOriginArray = new double[] { actualOrigin.X, actualOrigin.Y, actualOrigin.Z };
            double[] axisStartDelta = new double[] { axisStartArray[0] - actualOriginArray[0],
                axisStartArray[1] - actualOriginArray[1], axisStartArray[2] - actualOriginArray[2] };
            double[] axisEndDelta = new double[] { axisEndArray[0] - actualOriginArray[0],
                axisEndArray[1] - actualOriginArray[1], axisEndArray[2] - actualOriginArray[2] };
            double startPlaneResidualMm = Dot3(axisStartDelta, actualNormal);
            double endPlaneResidualMm = Dot3(axisEndDelta, actualNormal);
            bool axisInResultPlane = Math.Abs(startPlaneResidualMm) <= M3FrameToleranceMm &&
                Math.Abs(endPlaneResidualMm) <= M3FrameToleranceMm;

            Feature[] parents = feature.GetParents();
            if (parents == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle DatumPlane parent-feature list is unavailable");
            List<string> parentIds = new List<string>();
            foreach (Feature parent in parents)
            {
                if (parent == null)
                    throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "angle DatumPlane parent-feature list contains null");
                parentIds.Add(RequiredJournalId(parent, "angle DatumPlane parent feature"));
            }
            bool parentMatches = referenceParentIds.Count == 0;
            foreach (string parentId in referenceParentIds)
                if (parentIds.Contains(parentId)) parentMatches = true;

            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("mode", "angle");
            result.Add("angle_deg", geometricAngleDeg);
            result.Add("requested_angle_deg", requestedRightHandAngleDeg);
            result.Add("native_expression_angle_deg", expressionAngleDeg);
            result.Add("native_angle_expression", angleExpression.RightHandSide);
            result.Add("native_method", definingPlane.Method.ToString());
            result.Add("support_kind", supportKind);
            if (supportKind == "DATUM_PLANE") result.Add("support_datum_plane_id", supportId);
            else
            {
                result.Add("support_face_id", supportId);
                result.Add("support_body_id", supportBodyId);
                result.Add("support_face_tag", supportFaceTag);
            }
            result.Add("axis_line_id", RequiredJournalId(axisLine, "angle axis Line"));
            result.Add("axis_start_mm", NumberList(axisStartArray));
            result.Add("axis_end_mm", NumberList(axisEndArray));
            result.Add("reference_origin_mm", NumberList(referenceOrigin));
            result.Add("reference_normal", NumberList(referenceNormal));
            result.Add("native_geometry_ids", new List<object> { supportId, RequiredJournalId(axisLine, "angle axis Line") });
            result.Add("native_parent_ids", ObjectStrings(parentIds));
            result.Add("reference_parent_ids", ObjectStrings(referenceParentIds));
            result.Add("parent_reference_matches", parentMatches);
            result.Add("native_reference_geometry", true);
            result.Add("normal_matches_angle", VectorNearArray(actualNormal, calculatedNormal, M3VectorTolerance));
            result.Add("axis_in_result_plane", axisInResultPlane);
            result.Add("axis_start_result_plane_residual_mm", startPlaneResidualMm);
            result.Add("axis_end_result_plane_residual_mm", endPlaneResidualMm);
            result.Add("expression_matches_geometry", expressionMatchesGeometry);
            return result;
        }
        catch (M2ModelException) { throw; }
        catch (Exception ex)
        {
            throw new M2ModelException("DATUM_PLANE_READBACK_FAILED",
                "NX could not read the native angle DatumPlane definition (" + ex.GetType().Name + ")");
        }
    }

    private static void M3ReadPlanarFaceReference(Face face, out double[] origin, out double[] normal)
    {
        int type;
        double[] point = new double[3];
        double[] direction = new double[3];
        double[] box = new double[6];
        double radius, radiusData;
        int normalDirection;
        UFSession.GetUFSession().Modl.AskFaceData(face.Tag, out type, point, direction,
            box, out radius, out radiusData, out normalDirection);
        if (type != UFConstants.UF_MODL_PLANAR_FACE)
            throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "offset DatumPlane reference Face is no longer planar");
        double length = Length3(direction);
        if (length <= M3VectorTolerance || Double.IsNaN(length) || Double.IsInfinity(length))
            throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "offset reference Face returned an invalid normal");
        normal = new double[] { direction[0] / length, direction[1] / length, direction[2] / length };
        double planeOffset = Dot3(normal, point);
        origin = new double[] { normal[0] * planeOffset, normal[1] * planeOffset,
            normal[2] * planeOffset };
    }

    private static List<string> M3FaceParentFeatureIds(Part work, Face face)
    {
        Feature[] features = work.Features.GetFeatures();
        if (features == null)
            throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "feature history is unavailable while resolving the offset Face parent");
        List<string> parentIds = new List<string>();
        foreach (Feature candidate in features)
        {
            if (candidate == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "feature history contains null while resolving the offset Face parent");
            Face[] faces = candidate.GetFaces();
            if (faces == null)
                throw new M2ModelException("DATUM_PLANE_READBACK_FAILED", "feature face history is unavailable while resolving the offset Face parent");
            foreach (Face candidateFace in faces)
                if (candidateFace != null && candidateFace.Tag.Equals(face.Tag))
                {
                    string id = RequiredJournalId(candidate, "offset Face source feature");
                    if (!parentIds.Contains(id)) parentIds.Add(id);
                }
        }
        parentIds.Sort(StringComparer.Ordinal);
        return parentIds;
    }

    private static bool TryParseM4FaceSelector(string text, out M4FaceSelector selector)
    {
        selector = null;
        if (String.IsNullOrWhiteSpace(text) || text.Length > 256) return false;
        string value = text.Trim().ToLowerInvariant().Replace('\u3000', ' ');
        bool outermost = value.Contains("outermost") || value.Contains("most outer") ||
            value.Contains("最外侧") || value.Contains("最外面");
        Match coordinateMatch = Regex.Match(value,
            @"(?:\bat\s+)?([xyz])\s*=\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?)\s*(?:mm|毫米)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        double coordinate = 0.0;
        bool hasCoordinate = coordinateMatch.Success && Double.TryParse(coordinateMatch.Groups[2].Value,
            NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate) &&
            !Double.IsNaN(coordinate) && !Double.IsInfinity(coordinate);
        if (coordinateMatch.Success && !hasCoordinate) return false;
        int axis = -1;
        int sign = 0;
        int directionTokens = 0;
        string[] axisNames = new string[] { "x", "y", "z" };
        for (int candidateAxis = 0; candidateAxis < 3; candidateAxis++)
        {
            string name = axisNames[candidateAxis];
            if (Regex.IsMatch(value, @"(?:\bpositive\s*[- ]?\s*" + name + @"\b|\b" + name + @"\s*\+|\+\s*" + name + @"\b)|正\s*" + name, RegexOptions.IgnoreCase))
            { axis = candidateAxis; sign = 1; directionTokens++; }
            if (Regex.IsMatch(value, @"(?:\bnegative\s*[- ]?\s*" + name + @"\b|\b" + name + @"\s*-|-\s*" + name + @"\b)|[负負]\s*" + name, RegexOptions.IgnoreCase))
            { axis = candidateAxis; sign = -1; directionTokens++; }
        }
        bool topAlias = Regex.IsMatch(value, @"\b(?:top|upper|topmost)\b|顶面|上表面|上面", RegexOptions.IgnoreCase);
        bool bottomAlias = Regex.IsMatch(value, @"\b(?:bottom|lower|bottommost)\b|底面|下表面|下面", RegexOptions.IgnoreCase);
        if (topAlias) { axis = 2; sign = 1; directionTokens++; }
        if (bottomAlias) { axis = 2; sign = -1; directionTokens++; }
        // One directional selector is required. A phrase containing two axes
        // cannot be reduced to whichever direction happened to be parsed last.
        if (directionTokens != 1) return false;
        if (coordinateMatch.Success && "xyz".IndexOf(Char.ToLowerInvariant(coordinateMatch.Groups[1].Value[0])) != axis)
            return false;
        if (coordinateMatch.Success && outermost) return false;
        if ((value.Contains("topmost") || value.Contains("bottommost")) && outermost) return false;

        string residue = value;
        if (coordinateMatch.Success) residue = residue.Remove(coordinateMatch.Index, coordinateMatch.Length);
        residue = Regex.Replace(residue, @"outermost|most\s+outer|最外侧|最外面", "", RegexOptions.IgnoreCase);
        residue = Regex.Replace(residue, @"\b(?:positive|negative)\s*[- ]?\s*[xyz]\b|\b[xyz]\s*[-+]|[-+]\s*[xyz]\b|正\s*[xyz]|[负負]\s*[xyz]|\b(?:top|upper|topmost|bottom|lower|bottommost)\b|顶面|上表面|上面|底面|下表面|下面", "", RegexOptions.IgnoreCase);
        residue = Regex.Replace(residue, @"\b(?:face|side|surface|planar|support|supporting|the|outer)\b|面|侧面|表面|支撑|外侧", "", RegexOptions.IgnoreCase);
        residue = Regex.Replace(residue, @"[\s,，:：()（）\[\]]", "");
        if (residue.Length != 0) return false;

        selector = new M4FaceSelector();
        selector.Axis = axis;
        selector.Sign = sign;
        selector.AxisName = axis == 0 ? "x" : (axis == 1 ? "y" : "z");
        selector.PositionRule = coordinateMatch.Success ? "at" : (outermost || value.Contains("topmost") || value.Contains("bottommost") ? "outermost" : "all");
        selector.CoordinateMm = coordinateMatch.Success ? (double?)coordinate : null;
        selector.Canonical = (sign > 0 ? "positive " : "negative ") + selector.AxisName + " side" +
            (selector.PositionRule == "outermost" ? " outermost" : (selector.PositionRule == "at" ? " at " + NumberText(coordinate) + " mm" : ""));
        return true;
    }

    private static List<object> M4SupportedSelectorExamples()
    {
        return new List<object> { "top", "bottom", "positive X side", "outermost positive X side",
            "positive X side at x=35 mm", "正X面", "最外侧正X面" };
    }

    private static Dictionary<string, object> M4UnsupportedSelectorResult(string selector, string revisionId)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("selection_status", "unsupported_selector");
        result.Add("selector", selector);
        result.Add("selector_normalized", null);
        result.Add("candidate_count", 0);
        result.Add("candidates", new List<object>());
        result.Add("clarification_required", true);
        result.Add("revision_id", revisionId);
        result.Add("supported_selector_examples", M4SupportedSelectorExamples());
        return result;
    }

    private static Dictionary<string, object> M4SelectorRecord(M4FaceSelector selector)
    {
        double[] normal = new double[3];
        normal[selector.Axis] = selector.Sign;
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
        record.Add("canonical", selector.Canonical);
        record.Add("axis", selector.AxisName);
        record.Add("side", selector.Sign > 0 ? "positive" : "negative");
        record.Add("normal_world", NumberList(normal));
        record.Add("position_rule", selector.PositionRule);
        if (selector.CoordinateMm.HasValue) record.Add("coordinate_mm", selector.CoordinateMm.Value);
        return record;
    }

    private List<M4FaceCandidate> ResolveM4Faces(Part work, M4FaceSelector selector, string bodyId,
        HashSet<string> excludedEdgeFeatureTags, Dictionary<string, string> featureJournalIdsByTag,
        List<string> excludedFeatureIds, Sketch supportSketch)
    {
        Body[] bodies = work.Bodies.ToArray();
        if (bodies == null) throw new M2ModelException("MODEL_READ_FAILED", "Bodies.ToArray returned null during M4 face query");
        List<Body> targets = new List<Body>();
        if (!String.IsNullOrWhiteSpace(bodyId))
        {
            foreach (Body body in bodies)
                if (body != null && String.Equals(RequiredJournalId(body, "body"), bodyId, StringComparison.Ordinal)) targets.Add(body);
            if (targets.Count == 0) throw new M2ModelException("M4_SUPPORT_BODY_NOT_FOUND", "body_id does not identify a Body in the current Work Part");
            if (targets.Count != 1) throw new M2ModelException("M4_SUPPORT_BODY_AMBIGUOUS", "body_id does not identify one Body");
            if (!targets[0].IsSolidBody) throw new M2ModelException("UNSUPPORTED_BODY", "face query body_id must identify a solid body");
        }
        else
        {
            foreach (Body body in bodies)
                if (body != null && body.IsSolidBody) targets.Add(body);
        }

        List<M4FaceCandidate> result = new List<M4FaceCandidate>();
        double[] requestedNormal = new double[3];
        requestedNormal[selector.Axis] = selector.Sign;
        foreach (Body body in targets)
        {
            Face[] faces = body.GetFaces();
            if (faces == null) throw new M2ModelException("MODEL_READ_FAILED", "Body.GetFaces returned null during M4 query");
            foreach (Face face in faces)
            {
                if (face == null) throw new M2ModelException("MODEL_READ_FAILED", "Body face collection contains null");
                int type;
                double[] point = new double[3];
                double[] direction = new double[3];
                double[] faceBox = new double[6];
                double radius, radiusData;
                int normalDirection;
                UFSession.GetUFSession().Modl.AskFaceData(face.Tag, out type, point, direction,
                    faceBox, out radius, out radiusData, out normalDirection);
                if (type != UFConstants.UF_MODL_PLANAR_FACE) continue;
                double directionLength = Length3(direction);
                if (directionLength <= 1e-12) continue;
                // For planar faces AskFaceData.dir is the face normal used by
                // the P04 NX probe. norm_dir relates that normal to the surface
                // parameterization and must not reverse candidate orientation.
                double[] normal = new double[] { direction[0] / directionLength,
                    direction[1] / directionLength, direction[2] / directionLength };
                if (Dot3(normal, requestedNormal) < M4FaceNormalDotTolerance) continue;
                M4FaceCandidate candidate = BuildM4FaceCandidate(work, body, face, point, normal,
                    excludedEdgeFeatureTags, featureJournalIdsByTag, excludedFeatureIds, supportSketch);
                candidate.PlaneCoordinate = point[selector.Axis];
                candidate.PlaneOffset = Dot3(candidate.Normal, candidate.Origin);
                result.Add(candidate);
            }
        }
        return result;
    }

    private List<M4FaceCandidate> ResolveM3AllPlanarFaces(Part work, string bodyId)
    {
        Body[] bodies = work.Bodies.ToArray();
        if (bodies == null) throw new M2ModelException("MODEL_READ_FAILED", "Bodies.ToArray returned null during F01 face discovery");
        List<Body> targets = new List<Body>();
        if (!String.IsNullOrWhiteSpace(bodyId))
        {
            foreach (Body body in bodies)
                if (body != null && String.Equals(RequiredJournalId(body, "body"), bodyId, StringComparison.Ordinal)) targets.Add(body);
            if (targets.Count == 0) throw new M2ModelException("M4_SUPPORT_BODY_NOT_FOUND", "body_id does not identify a Body in the current Work Part");
            if (targets.Count != 1) throw new M2ModelException("M4_SUPPORT_BODY_AMBIGUOUS", "body_id does not identify one Body");
            if (!targets[0].IsSolidBody) throw new M2ModelException("UNSUPPORTED_BODY", "body_id must identify a solid Body");
        }
        else
        {
            foreach (Body body in bodies)
                if (body != null && body.IsSolidBody) targets.Add(body);
        }

        Dictionary<string, string> featureJournalIdsByTag;
        HashSet<string> featureTags = M4FeatureTags(work, null, out featureJournalIdsByTag);
        List<M4FaceCandidate> result = new List<M4FaceCandidate>();
        foreach (Body body in targets)
        {
            Face[] faces = body.GetFaces();
            if (faces == null) throw new M2ModelException("MODEL_READ_FAILED", "Body.GetFaces returned null during F01 face discovery");
            foreach (Face face in faces)
            {
                if (face == null) throw new M2ModelException("MODEL_READ_FAILED", "Body face collection contains null");
                int type;
                double[] point = new double[3], direction = new double[3], faceBox = new double[6];
                double radius, radiusData;
                int normalDirection;
                UFSession.GetUFSession().Modl.AskFaceData(face.Tag, out type, point, direction,
                    faceBox, out radius, out radiusData, out normalDirection);
                if (type != UFConstants.UF_MODL_PLANAR_FACE) continue;
                double directionLength = Length3(direction);
                if (directionLength <= M3VectorTolerance) continue;
                double[] normal = new double[] { direction[0] / directionLength,
                    direction[1] / directionLength, direction[2] / directionLength };
                M4FaceCandidate candidate = BuildM4FaceCandidate(work, body, face, point, normal,
                    featureTags, featureJournalIdsByTag, null, null);
                candidate.PlaneCoordinate = Dot3(candidate.Normal, candidate.Origin);
                result.Add(candidate);
            }
        }
        SortM4FaceCandidates(result);
        return result;
    }

    private M4FaceCandidate ResolveM3FaceById(Part work, string faceId, string bodyId, string faceTag)
    {
        if (String.IsNullOrWhiteSpace(faceId))
            throw new M2ModelException("INVALID_REQUEST", "support_face_id is required");
        List<M4FaceCandidate> faces = ResolveM3AllPlanarFaces(work, bodyId);
        List<M4FaceCandidate> matches = new List<M4FaceCandidate>();
        foreach (M4FaceCandidate candidate in faces)
            if (String.Equals(candidate.FaceId, faceId, StringComparison.Ordinal) &&
                (String.IsNullOrEmpty(faceTag) || String.Equals(candidate.Face.Tag.ToString(), faceTag, StringComparison.Ordinal)))
                matches.Add(candidate);
        if (matches.Count == 0)
            throw new M2ModelException("SUPPORT_FACE_NOT_FOUND",
                "support_face_id and optional support_face_tag do not identify a planar Face in the requested scope");
        if (matches.Count != 1)
            throw new M2ModelException("SUPPORT_FACE_AMBIGUOUS", "support_face_id does not identify exactly one planar Face");
        return matches[0];
    }

    private static void ValidateM3FrameOnFace(M4FaceCandidate face, double[] origin, double[] normal)
    {
        if (face == null || face.Face == null || origin == null || origin.Length != 3 || normal == null || normal.Length != 3)
            throw new M2ModelException("INVALID_REQUEST", "face-supported sketch frame is incomplete");
        double[] relative = new double[] { origin[0] - face.Origin[0], origin[1] - face.Origin[1], origin[2] - face.Origin[2] };
        double distance = Dot3(relative, face.Normal);
        double normalDot = Dot3(normal, face.Normal);
        if (Math.Abs(distance) > M3FrameToleranceMm || normalDot < 1.0 - M3VectorTolerance)
            throw new M2ModelException("INVALID_REQUEST", "origin_mm must lie on the support Face and its normal must match the face normal");
        if (face.Loops == null || face.Loops.Count == 0)
            throw new M2ModelException("INVALID_REQUEST", "support Face boundary could not be read to validate its origin");
        M4Point2 local = new M4Point2(Dot3(relative, face.XAxis), Dot3(relative, face.YAxis));
        if (!M4PointInsideFaceDomain(local, face.Loops))
            throw new M2ModelException("INVALID_REQUEST", "origin_mm must lie inside the selected planar Face boundary");
    }

    private static void ValidateM3FrameOnDatumPlane(DatumPlane datum, double[] origin, double[] normal)
    {
        if (datum == null || origin == null || origin.Length != 3 || normal == null || normal.Length != 3)
            throw new M2ModelException("INVALID_REQUEST", "datum-supported sketch frame is incomplete");
        Point3d datumOrigin = datum.Origin;
        Vector3d rawNormal = datum.Normal;
        double[] planeNormal = new double[] { rawNormal.X, rawNormal.Y, rawNormal.Z };
        double length = Length3(planeNormal);
        if (length <= M3VectorTolerance || Double.IsNaN(length) || Double.IsInfinity(length))
            throw new M2ModelException("INVALID_REQUEST", "support DatumPlane has an invalid normal");
        planeNormal[0] /= length; planeNormal[1] /= length; planeNormal[2] /= length;
        double[] delta = new double[] { origin[0] - datumOrigin.X, origin[1] - datumOrigin.Y, origin[2] - datumOrigin.Z };
        if (Math.Abs(Dot3(delta, planeNormal)) > M3FrameToleranceMm || Dot3(normal, planeNormal) < 1.0 - M3VectorTolerance)
            throw new M2ModelException("INVALID_REQUEST", "origin_mm and normal must lie on and align with the support DatumPlane");
    }

    private M4FaceCandidate ResolveM4UniqueFace(Part work, string selectorText, string bodyId)
    {
        return ResolveM4UniqueFace(work, selectorText, bodyId, null);
    }

    private M4FaceCandidate ResolveM4UniqueFace(Part work, string selectorText, string bodyId,
        List<string> excludedFeatureIds)
    {
        return ResolveM4UniqueFace(work, selectorText, bodyId, excludedFeatureIds, null);
    }

    private M4FaceCandidate ResolveM4UniqueFace(Part work, string selectorText, string bodyId,
        List<string> excludedFeatureIds, Sketch supportSketch)
    {
        M4FaceSelector selector;
        if (!TryParseM4FaceSelector(selectorText, out selector))
            throw new M2ModelException("M4_UNSUPPORTED_SELECTOR", "selector is unsupported; use top/bottom, positive or negative X/Y/Z, optional outermost or an explicit mm coordinate");
        Dictionary<string, string> featureJournalIdsByTag;
        HashSet<string> excludedTags = M4FeatureTags(work, excludedFeatureIds, out featureJournalIdsByTag);
        List<M4FaceCandidate> candidates = ResolveM4Faces(work, selector, bodyId, excludedTags,
            featureJournalIdsByTag, excludedFeatureIds, supportSketch);
        if (selector.PositionRule == "outermost" && candidates.Count > 0)
        {
            double extreme = candidates[0].PlaneCoordinate;
            for (int i = 1; i < candidates.Count; i++)
                extreme = selector.Sign > 0 ? Math.Max(extreme, candidates[i].PlaneCoordinate) : Math.Min(extreme, candidates[i].PlaneCoordinate);
            candidates.RemoveAll(delegate(M4FaceCandidate item) { return Math.Abs(item.PlaneCoordinate - extreme) > 1e-6; });
        }
        else if (selector.PositionRule == "at" && candidates.Count > 0)
        {
            double coordinate = selector.CoordinateMm.Value;
            candidates.RemoveAll(delegate(M4FaceCandidate item) { return Math.Abs(item.PlaneCoordinate - coordinate) > 1e-5; });
        }
        if (candidates.Count == 0)
            throw new M2ModelException("M4_FACE_NOT_FOUND", "selector did not match a planar face on the requested body scope");
        if (candidates.Count != 1)
            throw new M2ModelException("M4_FACE_AMBIGUOUS", "selector matches " + candidates.Count.ToString(CultureInfo.InvariantCulture) + " planar faces; add an explicit outermost or at-axis-mm qualifier");
        return candidates[0];
    }

    private M4FaceCandidate BuildM4FaceCandidate(Part work, Body body, Face face, double[] facePoint,
        double[] normal, HashSet<string> excludedEdgeFeatureTags,
        Dictionary<string, string> featureJournalIdsByTag, List<string> excludedFeatureIds, Sketch supportSketch)
    {
        M4FaceCandidate item = new M4FaceCandidate();
        item.Face = face;
        item.Body = body;
        item.BodyId = RequiredJournalId(body, "body");
        item.FaceId = RequiredJournalId(face, "face");
        item.Normal = (double[])normal.Clone();
        item.FeatureJournalIdsByTag = featureJournalIdsByTag;
        item.ExcludedCutFeatures = StringListAsObjects(excludedFeatureIds ?? new List<string>());
        item.PlaneOffset = Dot3(normal, facePoint);
        item.Origin = new double[] { normal[0] * item.PlaneOffset, normal[1] * item.PlaneOffset, normal[2] * item.PlaneOffset };
        double[] reference = Math.Abs(normal[0]) < 0.999 ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 };
        double alongNormal = Dot3(reference, normal);
        double[] x = new double[] { reference[0] - alongNormal * normal[0], reference[1] - alongNormal * normal[1], reference[2] - alongNormal * normal[2] };
        Normalize3(x);
        double[] y = Cross3(normal, x);
        Normalize3(y);
        item.XAxis = x;
        item.YAxis = y;
        item.Bbox = new double[6];
        UFSession.GetUFSession().Modl.AskBoundingBox(face.Tag, item.Bbox);
        MeasureFaces measurement = work.MeasureManager.NewFaceProperties(
            work.UnitCollection.FindObject("SquareMilliMeter"), work.UnitCollection.FindObject("MilliMeter"),
            0.999, new IParameterizedSurface[] { face });
        item.Area = measurement.Area;
        if (supportSketch != null)
        {
            Dictionary<string, object> profileEvidence;
            item.SupportProfiles = M4BuildSketchProfiles(supportSketch, item, out profileEvidence);
        }
        item.Loops = ReadM4FaceLoops(face, item, excludedEdgeFeatureTags, item.SupportProfiles,
            out item.BoundarySupported, out item.BoundaryReason, out item.BoundaryTypes);
        item.Centroid = M4FaceCentroid(item, facePoint);
        return item;
    }

    private static Dictionary<string, object> M4FaceCandidateRecord(M4FaceCandidate candidate)
    {
        Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
        item.Add("surface_type", "planar");
        item.Add("body_id", candidate.BodyId);
        item.Add("face_journal_id", candidate.FaceId);
        item.Add("face_tag", candidate.Face.Tag.ToString());
        item.Add("normal_world", NumberList(candidate.Normal));
        item.Add("plane_origin_mm", NumberList(candidate.Origin));
        item.Add("plane_offset_mm", candidate.PlaneOffset);
        item.Add("centroid_mm", NumberList(candidate.Centroid));
        item.Add("area_mm2", candidate.Area);
        item.Add("bbox_mm", NumberList(candidate.Bbox));
        item.Add("loop_count", candidate.Loops == null ? 0 : candidate.Loops.Count);
        item.Add("boundary_types", candidate.BoundaryTypes == null ? new List<object>() : StringListAsObjects(candidate.BoundaryTypes));
        item.Add("boundary_supported", candidate.BoundarySupported);
        item.Add("boundary_chord_tolerance_mm", M4BoundaryChordToleranceMm);
        item.Add("frame", M4FrameRecord(candidate.Origin, candidate.XAxis, candidate.YAxis, candidate.Normal));
        return item;
    }

    private static Dictionary<string, object> M4FrameRecord(double[] origin, double[] xAxis, double[] yAxis, double[] normal)
    {
        Dictionary<string, object> frame = new Dictionary<string, object>(StringComparer.Ordinal);
        frame.Add("origin_mm", NumberList(origin));
        frame.Add("x_axis_world", NumberList(xAxis));
        frame.Add("y_axis_world", NumberList(yAxis));
        frame.Add("normal_world", NumberList(normal));
        return frame;
    }

    private static void SortM4FaceCandidates(List<M4FaceCandidate> candidates)
    {
        candidates.Sort(delegate(M4FaceCandidate left, M4FaceCandidate right)
        {
            int byBody = String.CompareOrdinal(left.BodyId, right.BodyId);
            if (byBody != 0) return byBody;
            int byOffset = left.PlaneCoordinate.CompareTo(right.PlaneCoordinate);
            if (byOffset != 0) return byOffset;
            int byFaceId = String.CompareOrdinal(left.FaceId, right.FaceId);
            return byFaceId != 0 ? byFaceId : String.CompareOrdinal(left.Face.Tag.ToString(), right.Face.Tag.ToString());
        });
    }

    private static double[] M4FaceCentroid(M4FaceCandidate candidate, double[] fallback)
    {
        if (candidate.Loops == null || candidate.Loops.Count == 0) return (double[])fallback.Clone();
        double weightedX = 0.0, weightedY = 0.0, total = 0.0;
        for (int i = 0; i < candidate.Loops.Count; i++)
        {
            double area, cx, cy;
            M4PolygonAreaCentroid(candidate.Loops[i].Points, out area, out cx, out cy);
            if (Math.Abs(area) <= 1e-10) continue;
            int depth = 0;
            M4Point2 probe = candidate.Loops[i].Points[0];
            for (int j = 0; j < candidate.Loops.Count; j++)
                if (i != j && M4PointInPolygon(probe, candidate.Loops[j].Points)) depth++;
            double signed = (depth % 2 == 0 ? 1.0 : -1.0) * Math.Abs(area);
            weightedX += signed * cx;
            weightedY += signed * cy;
            total += signed;
        }
        if (Math.Abs(total) <= 1e-10) return (double[])fallback.Clone();
        double localX = weightedX / total, localY = weightedY / total;
        return new double[] {
            candidate.Origin[0] + localX * candidate.XAxis[0] + localY * candidate.YAxis[0],
            candidate.Origin[1] + localX * candidate.XAxis[1] + localY * candidate.YAxis[1],
            candidate.Origin[2] + localX * candidate.XAxis[2] + localY * candidate.YAxis[2]
        };
    }

    private static List<object> StringListAsObjects(List<string> values)
    {
        List<object> result = new List<object>();
        foreach (string value in values) result.Add(value);
        return result;
    }

    private static List<M4FaceLoop> ReadM4FaceLoops(Face face, M4FaceCandidate frame,
        HashSet<string> excludedEdgeFeatureTags, List<M4Profile> supportProfiles,
        out bool supported, out string reason, out List<string> boundaryTypes)
    {
        supported = false;
        reason = null;
        boundaryTypes = new List<string>();
        List<M4FaceLoop> loops = new List<M4FaceLoop>();
        Edge[] edges;
        try { edges = face.GetEdges(); }
        catch (Exception ex) { reason = "Face.GetEdges failed (" + ex.GetType().Name + ")"; return loops; }
        if (edges == null || edges.Length == 0)
        {
            reason = "planar face has no enumerable boundary edges";
            return loops;
        }
        List<List<M4Point2>> edgePoints = new List<List<M4Point2>>();
        List<string> edgeTypes = new List<string>();
        List<bool> edgeOwnedByCut = new List<bool>();
        List<object> edgeFeatureEvidence = new List<object>();
        foreach (Edge edge in edges)
        {
            if (edge == null) { reason = "face boundary contains a null edge"; return loops; }
            bool ownedByExcludedFeature = false;
            Dictionary<string, object> edgeEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            if (excludedEdgeFeatureTags != null && excludedEdgeFeatureTags.Count > 0)
            {
                try { ownedByExcludedFeature = M4EdgeUsesExcludedFeature(edge, excludedEdgeFeatureTags,
                    frame.FeatureJournalIdsByTag, out edgeEvidence); }
                catch (Exception ex)
                {
                    reason = "could not identify downstream cut-edge provenance (" + ex.GetType().Name + ")";
                    return new List<M4FaceLoop>();
                }
            }
            else
            {
                edgeEvidence.Add("edge_tag", edge.Tag.ToString());
                try { edgeEvidence.Add("edge_journal_id", edge.JournalIdentifier); }
                catch { edgeEvidence.Add("edge_journal_id", null); }
                edgeEvidence.Add("ask_edge_feature_tags", new List<object>());
                edgeEvidence.Add("has_own_cut_feature", false);
                edgeEvidence.Add("all_feature_tags_are_own_cut", false);
            }
            int type;
            try { UFSession.GetUFSession().Modl.AskEdgeType(edge.Tag, out type); }
            catch (Exception ex) { reason = "AskEdgeType failed (" + ex.GetType().Name + ")"; return loops; }
            string edgeType;
            List<double[]> worldPoints = new List<double[]>();
            if (type == UFConstants.UF_MODL_LINEAR_EDGE)
            {
                edgeType = "line";
                double[] first = new double[3], last = new double[3];
                int vertexCount;
                try { UFSession.GetUFSession().Modl.AskEdgeVerts(edge.Tag, first, last, out vertexCount); }
                catch (Exception ex) { reason = "AskEdgeVerts failed (" + ex.GetType().Name + ")"; return loops; }
                if (vertexCount != 2)
                {
                    reason = "linear face boundary did not have exactly two vertices";
                    return loops;
                }
                worldPoints.Add(first);
                worldPoints.Add(last);
            }
            else if (type == UFConstants.UF_MODL_CIRCULAR_EDGE)
            {
                edgeType = "arc";
                int count;
                double[] points;
                try
                {
                    UFSession.GetUFSession().Modl.AskCurvePoints(edge.Tag,
                        M4BoundaryChordToleranceMm, 0.01, 0.5, out count, out points);
                }
                catch (Exception ex) { reason = "AskCurvePoints failed (" + ex.GetType().Name + ")"; return loops; }
                if (count < 3 || count > 100000 || points == null || points.Length < count * 3)
                {
                    reason = "circular face boundary could not be sampled within the supported point limit";
                    return loops;
                }
                for (int i = 0; i < count; i++)
                    worldPoints.Add(new double[] { points[i * 3], points[i * 3 + 1], points[i * 3 + 2] });
            }
            else
            {
                edgeType = "unsupported";
                if (!boundaryTypes.Contains(edgeType)) boundaryTypes.Add(edgeType);
                reason = "face boundaries support only exact line edges and circular edges; found UF edge type " + type.ToString(CultureInfo.InvariantCulture);
                return loops;
            }
            if (!boundaryTypes.Contains(edgeType)) boundaryTypes.Add(edgeType);
            List<M4Point2> localPoints = new List<M4Point2>();
            foreach (double[] world in worldPoints)
                localPoints.Add(M4ProjectToFrame(frame, world));
            RemoveAdjacentDuplicatePoints(localPoints, 1e-8);
            if (localPoints.Count < 2)
            {
                reason = "face boundary edge returned fewer than two distinct points";
                return loops;
            }
            edgePoints.Add(localPoints);
            edgeTypes.Add(edgeType);
            edgeOwnedByCut.Add(ownedByExcludedFeature);
            edgeFeatureEvidence.Add(edgeEvidence);
            frame.EdgeFeatureProvenance.Add(edgeEvidence);
        }

        bool[] used = new bool[edgePoints.Count];
        for (int seed = 0; seed < edgePoints.Count; seed++)
        {
            if (used[seed]) continue;
            List<M4Point2> path = new List<M4Point2>(edgePoints[seed]);
            string loopType = edgeTypes[seed];
            bool loopOwnedByCut = edgeOwnedByCut[seed];
            List<int> loopEdgeIndexes = new List<int> { seed };
            used[seed] = true;
            if (M4PointsNear(path[0], path[path.Count - 1], 1e-6))
            {
                path[path.Count - 1] = path[0];
                Dictionary<string, object> profileMatchEvidence;
                bool profileMatches = M4OwnedCutLoopMatchesProfile(path, supportProfiles, out profileMatchEvidence);
                AddM4LoopProvenance(frame, path, loopEdgeIndexes, edgeFeatureEvidence,
                    loopType, loopOwnedByCut, profileMatchEvidence);
                if (loopOwnedByCut)
                {
                    if (!profileMatches)
                    { reason = "own-cut circular/rectangular boundary could not be proven isolated from other face loops"; return new List<M4FaceLoop>(); }
                }
                else loops.Add(new M4FaceLoop { Points = path, EdgeType = loopType });
                continue;
            }
            bool closed = false;
            for (int hop = 0; hop < edgePoints.Count; hop++)
            {
                M4Point2 end = path[path.Count - 1];
                int next = -1;
                bool reverse = false;
                for (int candidate = 0; candidate < edgePoints.Count; candidate++)
                {
                    if (used[candidate]) continue;
                    if (M4PointsNear(end, edgePoints[candidate][0], 1e-6)) { next = candidate; break; }
                    if (M4PointsNear(end, edgePoints[candidate][edgePoints[candidate].Count - 1], 1e-6))
                    { next = candidate; reverse = true; break; }
                }
                if (next < 0)
                {
                    reason = "face boundary edges did not form closed line/circular loops";
                    return new List<M4FaceLoop>();
                }
                List<M4Point2> nextPoints = edgePoints[next];
                used[next] = true;
                loopEdgeIndexes.Add(next);
                loopOwnedByCut = loopOwnedByCut && edgeOwnedByCut[next];
                if (loopType != edgeTypes[next]) loopType = "line+arc";
                if (reverse)
                {
                    for (int k = nextPoints.Count - 2; k >= 0; k--) path.Add(nextPoints[k]);
                }
                else
                {
                    for (int k = 1; k < nextPoints.Count; k++) path.Add(nextPoints[k]);
                }
                if (M4PointsNear(path[0], path[path.Count - 1], 1e-6))
                {
                    path[path.Count - 1] = path[0];
                    closed = true;
                    break;
                }
            }
            if (!closed)
            {
                reason = "face boundary loop exceeded the supported edge count";
                return new List<M4FaceLoop>();
            }
            Dictionary<string, object> completeLoopProfileEvidence;
            bool completeLoopMatchesProfile = M4OwnedCutLoopMatchesProfile(path, supportProfiles, out completeLoopProfileEvidence);
            AddM4LoopProvenance(frame, path, loopEdgeIndexes, edgeFeatureEvidence,
                loopType, loopOwnedByCut, completeLoopProfileEvidence);
            if (loopOwnedByCut)
            {
                if (!completeLoopMatchesProfile)
                { reason = "own-cut boundary did not match the live source Sketch profile"; return new List<M4FaceLoop>(); }
            }
            else loops.Add(new M4FaceLoop { Points = path, EdgeType = loopType });
        }
        supported = loops.Count > 0;
        reason = supported ? null : "planar face has no closed boundary loops";
        return loops;
    }

    private static void AddM4LoopProvenance(M4FaceCandidate frame, List<M4Point2> points,
        List<int> edgeIndexes, List<object> edgeFeatureEvidence, string loopType, bool ownedByOwnCut,
        Dictionary<string, object> profileMatchEvidence)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("edge_type", loopType);
        evidence.Add("closed", points != null && points.Count >= 2 && M4PointsNear(points[0], points[points.Count - 1], 1e-6));
        evidence.Add("edge_indexes", edgeIndexes == null ? new List<object>() : new List<object>(edgeIndexes.ConvertAll<object>(delegate(int index) { return index; })));
        evidence.Add("all_edges_have_own_cut_influence", ownedByOwnCut);
        List<object> loopPoints = new List<object>();
        if (points != null)
            foreach (M4Point2 point in points) loopPoints.Add(new List<object> { point.X, point.Y });
        evidence.Add("local_loop_points_mm", loopPoints);
        List<object> edges = new List<object>();
        if (edgeIndexes != null && edgeFeatureEvidence != null)
            foreach (int index in edgeIndexes)
                if (index >= 0 && index < edgeFeatureEvidence.Count) edges.Add(edgeFeatureEvidence[index]);
        evidence.Add("edges", edges);
        evidence.Add("profile_match", profileMatchEvidence);
        frame.LoopProvenance.Add(evidence);
    }

    private static bool M4EdgeUsesExcludedFeature(Edge edge, HashSet<string> excludedFeatureTags,
        Dictionary<string, string> featureJournalIdsByTag, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("edge_tag", edge == null ? null : edge.Tag.ToString());
        try { evidence.Add("edge_journal_id", edge == null ? null : edge.JournalIdentifier); }
        catch { evidence.Add("edge_journal_id", null); }
        Tag[] featureTags;
        UFSession.GetUFSession().Modl.AskEdgeFeats(edge.Tag, out featureTags);
        List<object> featureRecords = new List<object>();
        bool allExcluded = featureTags != null && featureTags.Length > 0;
        bool hasOwnCut = false;
        if (featureTags != null)
        {
            foreach (Tag tag in featureTags)
            {
                string tagText = tag.ToString();
                string journalId = null;
                if (featureJournalIdsByTag != null) featureJournalIdsByTag.TryGetValue(tagText, out journalId);
                bool excluded = excludedFeatureTags != null && excludedFeatureTags.Contains(tagText);
                Dictionary<string, object> featureRecord = new Dictionary<string, object>(StringComparer.Ordinal);
                featureRecord.Add("tag", tagText);
                featureRecord.Add("journal_id", journalId);
                featureRecord.Add("own_cut_feature", excluded);
                featureRecords.Add(featureRecord);
                if (excluded) hasOwnCut = true;
                if (!excluded) allExcluded = false;
            }
        }
        evidence.Add("ask_edge_feature_tags", featureRecords);
        evidence.Add("has_own_cut_feature", hasOwnCut);
        evidence.Add("all_feature_tags_are_own_cut", allExcluded);
        return hasOwnCut;
    }

    private static bool M4OwnedCutLoopMatchesProfile(List<M4Point2> loop, List<M4Profile> profiles,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("closed_loop_point_count", loop == null ? 0 : loop.Count);
        evidence.Add("profile_count", profiles == null ? 0 : profiles.Count);
        List<object> profileChecks = new List<object>();
        bool matched = false;
        if (loop == null || loop.Count < 4 || profiles == null || profiles.Count == 0)
        {
            evidence.Add("matched", false);
            evidence.Add("reason", "closed loop or supported source profile is unavailable");
            evidence.Add("profiles", profileChecks);
            return false;
        }
        foreach (M4Profile profile in profiles)
        {
            if (!profile.Closed || (profile.Kind != "circle" && profile.Kind != "rectangle" && profile.Kind != "polygon")) continue;
            bool profileMatches = false;
            Dictionary<string, object> profileEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            profileEvidence.Add("geometry_ids", StringListAsObjects(profile.GeometryIds));
            profileEvidence.Add("type", profile.Kind);
            if (profile.Kind == "circle")
            {
                bool allOnCircle = true;
                double maxLoopPointRadialError = 0.0;
                for (int i = 0; i < loop.Count - 1; i++)
                {
                    double dx = loop[i].X - profile.Center.X, dy = loop[i].Y - profile.Center.Y;
                    double error = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - profile.Radius);
                    maxLoopPointRadialError = Math.Max(maxLoopPointRadialError, error);
                    if (error > M4BoundaryChordToleranceMm * 4.0) allOnCircle = false;
                }
                List<M4Point2> fullCircleSamples = M4CirclePolygon(profile.Center, profile.Radius, M4BoundaryChordToleranceMm);
                double maxProfileSampleDistance = 0.0;
                foreach (M4Point2 sample in fullCircleSamples)
                    maxProfileSampleDistance = Math.Max(maxProfileSampleDistance, M4DistanceToPolyline(sample, loop));
                bool fullCircumferenceCovered = maxProfileSampleDistance <= M4BoundaryChordToleranceMm * 4.0;
                profileMatches = allOnCircle && fullCircumferenceCovered;
                profileEvidence.Add("maximum_loop_point_radial_error_mm", maxLoopPointRadialError);
                profileEvidence.Add("loop_points_match_circle", allOnCircle);
                profileEvidence.Add("profile_sample_count", fullCircleSamples.Count);
                profileEvidence.Add("maximum_profile_sample_distance_to_loop_mm", maxProfileSampleDistance);
                profileEvidence.Add("full_circumference_covered", fullCircumferenceCovered);
            }
            else
            {
                bool loopOnProfile = true;
                for (int i = 0; i < loop.Count - 1 && loopOnProfile; i++)
                    if (M4DistanceToPolyline(loop[i], profile.Points) > M4BoundaryChordToleranceMm * 4.0) loopOnProfile = false;
                bool profileOnLoop = true;
                for (int i = 0; i < profile.Points.Count - 1 && profileOnLoop; i++)
                    if (M4DistanceToPolyline(profile.Points[i], loop) > M4BoundaryChordToleranceMm * 4.0) profileOnLoop = false;
                profileMatches = loopOnProfile && profileOnLoop;
                profileEvidence.Add("loop_points_match_profile", loopOnProfile);
                profileEvidence.Add("profile_points_match_loop", profileOnLoop);
            }
            profileEvidence.Add("matches_complete_loop", profileMatches);
            profileChecks.Add(profileEvidence);
            if (profileMatches) matched = true;
        }
        evidence.Add("matched", matched);
        evidence.Add("profiles", profileChecks);
        return matched;
    }

    private static double M4DistanceToPolyline(M4Point2 point, List<M4Point2> path)
    {
        double distance = Double.PositiveInfinity;
        for (int i = 0; i < path.Count - 1; i++)
            distance = Math.Min(distance, M4PointSegmentDistance(point, path[i], path[i + 1]));
        return distance;
    }

    private static HashSet<string> M4FeatureTags(Part work, List<string> featureIds,
        out Dictionary<string, string> featureJournalIdsByTag)
    {
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        featureJournalIdsByTag = new Dictionary<string, string>(StringComparer.Ordinal);
        if (work == null) return tags;
        Feature[] features = work.Features.GetFeatures();
        if (features == null) return tags;
        HashSet<string> requested = new HashSet<string>(featureIds ?? new List<string>(), StringComparer.Ordinal);
        foreach (Feature feature in features)
        {
            if (feature == null) continue;
            string journalId = feature.JournalIdentifier;
            string tag = feature.Tag.ToString();
            featureJournalIdsByTag[tag] = journalId;
            if (requested.Contains(journalId)) tags.Add(tag);
        }
        return tags;
    }

    private static M4Point2 M4ProjectToFrame(M4FaceCandidate frame, double[] point)
    {
        double dx = point[0] - frame.Origin[0];
        double dy = point[1] - frame.Origin[1];
        double dz = point[2] - frame.Origin[2];
        return new M4Point2(dx * frame.XAxis[0] + dy * frame.XAxis[1] + dz * frame.XAxis[2],
            dx * frame.YAxis[0] + dy * frame.YAxis[1] + dz * frame.YAxis[2]);
    }

    private static void RemoveAdjacentDuplicatePoints(List<M4Point2> points, double tolerance)
    {
        for (int i = points.Count - 1; i > 0; i--)
            if (M4PointsNear(points[i], points[i - 1], tolerance)) points.RemoveAt(i);
    }

    private bool ValidateM4SketchProfile(M4FaceCandidate face, List<M3SketchGeometryRequest> geometries,
        double[] offset, out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("body_id", face.BodyId);
        evidence.Add("face_journal_id", face.FaceId);
        evidence.Add("boundary_method", "exact linear edges plus circular edge samples from NX UF with chord tolerance 0.001 mm");
        evidence.Add("boundary_chord_tolerance_mm", M4BoundaryChordToleranceMm);
        evidence.Add("domain_clearance_mm", M4DomainClearanceMm);
        evidence.Add("loop_count", face.Loops == null ? 0 : face.Loops.Count);
        evidence.Add("boundary_types", face.BoundaryTypes == null ? new List<object>() : StringListAsObjects(face.BoundaryTypes));
        evidence.Add("own_cut_feature_journal_ids", face.ExcludedCutFeatures ?? new List<object>());
        evidence.Add("boundary_edge_feature_provenance", face.EdgeFeatureProvenance ?? new List<object>());
        evidence.Add("boundary_loop_provenance", face.LoopProvenance ?? new List<object>());
        if (!face.BoundarySupported || face.Loops == null || face.Loops.Count == 0)
        {
            evidence.Add("status", "UNSUPPORTED_BOUNDARY");
            evidence.Add("reason", face.BoundaryReason ?? "support face region is not enumerable");
            throw new M2ModelException("M4_PROFILE_OUTSIDE_FACE", "support face boundary could not be proven using line/circular-edge domain checks");
        }
        List<M4Profile> profiles = new List<M4Profile>();
        List<M4LineSegment> lineSegments = new List<M4LineSegment>();
        foreach (M3SketchGeometryRequest geometry in geometries)
        {
            M4Profile profile = new M4Profile();
            if (geometry.Type == "circle")
            {
                profile.Kind = "circle";
                profile.GeometryIds.Add(geometry.Id);
                profile.Center = new M4Point2(offset[0] + geometry.CenterMm[0], offset[1] + geometry.CenterMm[1]);
                profile.Radius = geometry.DiameterMm / 2.0;
                profile.Closed = true;
                profile.Points = M4CirclePolygon(profile.Center, profile.Radius, M4BoundaryChordToleranceMm);
            }
            else if (geometry.Type == "rectangle")
            {
                profile.Kind = "rectangle";
                profile.GeometryIds.Add(geometry.Id);
                double x = offset[0] + geometry.CornerMm[0];
                double y = offset[1] + geometry.CornerMm[1];
                profile.Closed = true;
                profile.Points = new List<M4Point2> {
                    new M4Point2(x,y), new M4Point2(x+geometry.WidthMm,y),
                    new M4Point2(x+geometry.WidthMm,y+geometry.HeightMm), new M4Point2(x,y+geometry.HeightMm), new M4Point2(x,y)
                };
                profile.Center = new M4Point2(x + geometry.WidthMm / 2.0, y + geometry.HeightMm / 2.0);
            }
            else
            {
                lineSegments.Add(new M4LineSegment {
                    First = new M4Point2(offset[0] + geometry.StartMm[0], offset[1] + geometry.StartMm[1]),
                    Last = new M4Point2(offset[0] + geometry.EndMm[0], offset[1] + geometry.EndMm[1]),
                    Id = geometry.Id
                });
                continue;
            }
            profiles.Add(profile);
        }

        profiles.AddRange(M4BuildLineProfiles(lineSegments));
        bool allInside = M4CheckProfilesInDomain(face, profiles, out evidence);
        if (!allInside)
            throw new M2ModelException("M4_PROFILE_OUTSIDE_FACE", "the sketch profile is outside, crosses, encloses a cutout in, or is too close to the selected face boundary");
        return true;
    }

    private static bool M4CheckProfilesInDomain(M4FaceCandidate face, List<M4Profile> profiles,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("body_id", face.BodyId);
        evidence.Add("face_journal_id", face.FaceId);
        evidence.Add("boundary_method", "exact linear edges plus circular edge samples from NX UF with chord tolerance 0.001 mm");
        evidence.Add("boundary_chord_tolerance_mm", M4BoundaryChordToleranceMm);
        evidence.Add("domain_clearance_mm", M4DomainClearanceMm);
        evidence.Add("loop_count", face.Loops == null ? 0 : face.Loops.Count);
        evidence.Add("boundary_types", face.BoundaryTypes == null ? new List<object>() : StringListAsObjects(face.BoundaryTypes));
        evidence.Add("own_cut_feature_journal_ids", face.ExcludedCutFeatures ?? new List<object>());
        evidence.Add("boundary_edge_feature_provenance", face.EdgeFeatureProvenance ?? new List<object>());
        evidence.Add("boundary_loop_provenance", face.LoopProvenance ?? new List<object>());
        if (!face.BoundarySupported || face.Loops == null || face.Loops.Count == 0)
        {
            evidence.Add("status", "UNSUPPORTED_BOUNDARY");
            evidence.Add("reason", face.BoundaryReason ?? "support face region is not enumerable");
            return false;
        }

        bool allInside = true;
        List<object> geometryEvidence = new List<object>();
        foreach (M4Profile profile in profiles)
        {
            string failure = profile.Kind == "unsupported" ? "closed line profile is not a supported axis-aligned rectangle" : null;
            foreach (M4Point2 point in profile.Points)
            {
                if (failure != null) break;
                if (!M4PointInsideFaceDomain(point, face.Loops)) { failure = "profile boundary leaves the support face region"; break; }
                if (M4DistanceToFaceBoundary(point, face.Loops) <= M4DomainClearanceMm)
                { failure = "profile is within the face-boundary uncertainty margin"; break; }
            }
            if (failure == null && profile.Closed && !M4PointInsideFaceDomain(profile.Center, face.Loops))
                failure = "profile interior lies outside the support face region";
            if (failure == null)
            {
                foreach (M4FaceLoop loop in face.Loops)
                {
                    for (int i = 0; i < loop.Points.Count - 1; i++)
                    {
                        M4Point2 boundaryPoint = loop.Points[i];
                        if (M4PointInsideProfile(boundaryPoint, profile, M4DomainClearanceMm))
                        { failure = "a face cutout or inner loop lies inside the requested profile"; break; }
                        for (int j = 0; j < profile.Points.Count - 1; j++)
                        {
                            if (M4SegmentsWithinTolerance(boundaryPoint, loop.Points[i + 1],
                                profile.Points[j], profile.Points[j + 1], M4DomainClearanceMm))
                            { failure = "profile crosses or approaches a face boundary"; break; }
                        }
                        if (failure != null) break;
                    }
                    if (failure != null) break;
                }
            }
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("geometry_ids", StringListAsObjects(profile.GeometryIds));
            item.Add("type", profile.Kind);
            item.Add("inside", failure == null);
            item.Add("reason", failure);
            geometryEvidence.Add(item);
            if (failure != null) allInside = false;
        }
        evidence.Add("geometries", geometryEvidence);
        evidence.Add("status", allInside ? "VERIFIED" : "OUTSIDE_FACE");
        evidence.Add("in_face_domain", allInside);
        return allInside;
    }

    private static List<M4Profile> M4BuildLineProfiles(List<M4LineSegment> segments)
    {
        List<M4Profile> profiles = new List<M4Profile>();
        if (segments == null) return profiles;
        List<M4Point2> vertices = new List<M4Point2>();
        List<int> degree = new List<int>();
        foreach (M4LineSegment segment in segments)
        {
            IncrementM4VertexDegree(segment.First, vertices, degree);
            IncrementM4VertexDegree(segment.Last, vertices, degree);
        }
        for (int i = 0; i < degree.Count; i++)
        {
            if (degree[i] <= 2) continue;
            M4Profile branched = new M4Profile();
            branched.Kind = "unsupported";
            branched.Closed = false;
            branched.GeometryIds = new List<string>();
            branched.Points = new List<M4Point2>();
            foreach (M4LineSegment segment in segments)
            {
                if (!branched.GeometryIds.Contains(segment.Id)) branched.GeometryIds.Add(segment.Id);
                branched.Points.Add(segment.First);
                branched.Points.Add(segment.Last);
            }
            profiles.Add(branched);
            return profiles;
        }
        bool[] used = new bool[segments.Count];
        for (int seed = 0; seed < segments.Count; seed++)
        {
            if (used[seed]) continue;
            List<M4Point2> path = new List<M4Point2> { segments[seed].First, segments[seed].Last };
            List<string> ids = new List<string> { segments[seed].Id };
            used[seed] = true;
            bool closed = false;
            for (int hop = 0; hop < segments.Count; hop++)
            {
                M4Point2 end = path[path.Count - 1];
                int next = -1;
                bool reverse = false;
                for (int candidate = 0; candidate < segments.Count; candidate++)
                {
                    if (used[candidate]) continue;
                    if (M4PointsNear(end, segments[candidate].First, 1e-7)) { next = candidate; break; }
                    if (M4PointsNear(end, segments[candidate].Last, 1e-7)) { next = candidate; reverse = true; break; }
                }
                if (next < 0) break;
                used[next] = true;
                ids.Add(segments[next].Id);
                M4Point2 nextPoint = reverse ? segments[next].First : segments[next].Last;
                path.Add(nextPoint);
                if (M4PointsNear(path[0], path[path.Count - 1], 1e-7))
                {
                    path[path.Count - 1] = path[0];
                    closed = true;
                    break;
                }
            }
            M4Profile profile = new M4Profile();
            profile.GeometryIds = ids;
            profile.Closed = closed;
            if (closed)
            {
                bool rectangle = M4IsAxisAlignedRectangle(path);
                profile.Kind = rectangle ? "polygon" : "unsupported";
                profile.Points = path;
                double area, cx, cy;
                M4PolygonAreaCentroid(path, out area, out cx, out cy);
                profile.Center = new M4Point2(cx, cy);
            }
            else
            {
                profile.Kind = "line";
                profile.Points = path;
            }
            profiles.Add(profile);
        }
        return profiles;
    }

    private static void IncrementM4VertexDegree(M4Point2 point, List<M4Point2> vertices, List<int> degree)
    {
        for (int i = 0; i < vertices.Count; i++)
        {
            if (!M4PointsNear(point, vertices[i], 1e-7)) continue;
            degree[i]++;
            return;
        }
        vertices.Add(point);
        degree.Add(1);
    }

    private static List<M4Profile> M4BuildSketchProfiles(Sketch sketch, M4FaceCandidate face,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("method", "live Sketch curves projected onto the selected face frame");
        NXObject[] geometry;
        try { geometry = sketch.GetAllGeometry(); }
        catch (Exception ex)
        {
            evidence.Add("status", "GEOMETRY_READ_FAILED");
            evidence.Add("reason", ex.GetType().Name);
            return null;
        }
        if (geometry == null || geometry.Length == 0)
        {
            evidence.Add("status", "EMPTY_SKETCH");
            return null;
        }
        List<M4Profile> profiles = new List<M4Profile>();
        List<M4LineSegment> lines = new List<M4LineSegment>();
        foreach (NXObject item in geometry)
        {
            if (item == null)
            {
                evidence.Add("status", "NULL_SKETCH_GEOMETRY");
                return null;
            }
            string id = RequiredJournalId(item, "M4 sketch geometry");
            Arc arc = item as Arc;
            if (arc != null)
            {
                double sweep = Math.Abs(arc.EndAngle - arc.StartAngle);
                if (sweep < (2.0 * Math.PI - 1e-4) || arc.Radius <= 0.0)
                {
                    evidence.Add("status", "UNSUPPORTED_ARC");
                    evidence.Add("geometry_id", id);
                    return null;
                }
                M4Point2 center = M4ProjectToFrame(face, new double[] { arc.CenterPoint.X, arc.CenterPoint.Y, arc.CenterPoint.Z });
                M4Profile circle = new M4Profile();
                circle.Kind = "circle";
                circle.GeometryIds.Add(id);
                circle.Center = center;
                circle.Radius = arc.Radius;
                circle.Closed = true;
                circle.Points = M4CirclePolygon(center, arc.Radius, M4BoundaryChordToleranceMm);
                profiles.Add(circle);
                continue;
            }
            Line line = item as Line;
            if (line != null)
            {
                M4Point2 first = M4ProjectToFrame(face, new double[] { line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z });
                M4Point2 last = M4ProjectToFrame(face, new double[] { line.EndPoint.X, line.EndPoint.Y, line.EndPoint.Z });
                lines.Add(new M4LineSegment { First = first, Last = last, Id = id });
                continue;
            }
            evidence.Add("status", "UNSUPPORTED_GEOMETRY");
            evidence.Add("geometry_id", id);
            evidence.Add("object_type", item.GetType().Name);
            return null;
        }
        profiles.AddRange(M4BuildLineProfiles(lines));
        if (profiles.Count == 0)
        {
            evidence.Add("status", "EMPTY_PROFILE");
            return null;
        }
        evidence["live_geometry_count"] = geometry.Length;
        return profiles;
    }

    private static bool M4SketchGeometryInsideFace(Sketch sketch, M4FaceCandidate face,
        out Dictionary<string, object> evidence)
    {
        List<M4Profile> profiles = M4BuildSketchProfiles(sketch, face, out evidence);
        if (profiles == null) return false;
        bool inside = M4CheckProfilesInDomain(face, profiles, out evidence);
        return inside;
    }

    private static bool M4IsAxisAlignedRectangle(List<M4Point2> path)
    {
        if (path == null || path.Count != 5 || !M4PointsNear(path[0], path[4], 1e-7)) return false;
        double minX = Double.PositiveInfinity, maxX = Double.NegativeInfinity;
        double minY = Double.PositiveInfinity, maxY = Double.NegativeInfinity;
        for (int i = 0; i < 4; i++)
        {
            minX = Math.Min(minX, path[i].X); maxX = Math.Max(maxX, path[i].X);
            minY = Math.Min(minY, path[i].Y); maxY = Math.Max(maxY, path[i].Y);
        }
        if (maxX - minX <= 1e-9 || maxY - minY <= 1e-9) return false;
        for (int i = 0; i < 4; i++)
        {
            bool horizontal = Math.Abs(path[i].Y - path[i + 1].Y) <= 1e-7 && Math.Abs(path[i].X - path[i + 1].X) > 1e-7;
            bool vertical = Math.Abs(path[i].X - path[i + 1].X) <= 1e-7 && Math.Abs(path[i].Y - path[i + 1].Y) > 1e-7;
            if (!horizontal && !vertical) return false;
        }
        return true;
    }

    private static List<M4Point2> M4CirclePolygon(M4Point2 center, double radius, double chordTolerance)
    {
        double maxAngle = radius <= chordTolerance ? Math.PI / 8.0 : 2.0 * Math.Acos(Math.Max(-1.0, Math.Min(1.0, 1.0 - chordTolerance / radius)));
        if (Double.IsNaN(maxAngle) || maxAngle <= 1e-6) maxAngle = Math.PI / 2048.0;
        int count = (int)Math.Ceiling(2.0 * Math.PI / maxAngle);
        if (count < 32) count = 32;
        if (count > 16384) throw new M2ModelException("M4_PROFILE_OUTSIDE_FACE", "circle is too large to prove inside the face within the configured chord tolerance");
        List<M4Point2> points = new List<M4Point2>();
        for (int i = 0; i <= count; i++)
        {
            double angle = 2.0 * Math.PI * i / count;
            points.Add(new M4Point2(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
        }
        return points;
    }

    private static bool M4PointInsideFaceDomain(M4Point2 point, List<M4FaceLoop> loops)
    {
        bool inside = false;
        foreach (M4FaceLoop loop in loops)
            if (M4PointInPolygon(point, loop.Points)) inside = !inside;
        return inside;
    }

    private static bool M4PointInPolygon(M4Point2 point, List<M4Point2> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            M4Point2 a = polygon[i], b = polygon[j];
            if (((a.Y > point.Y) != (b.Y > point.Y)) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static bool M4PointInsideProfile(M4Point2 point, M4Profile profile, double margin)
    {
        if (profile.Kind == "circle")
        {
            double dx = point.X - profile.Center.X, dy = point.Y - profile.Center.Y;
            return Math.Sqrt(dx * dx + dy * dy) < profile.Radius - margin;
        }
        if (profile.Kind == "rectangle" || profile.Kind == "polygon") return M4PointInPolygon(point, profile.Points);
        return false;
    }

    private static double M4DistanceToFaceBoundary(M4Point2 point, List<M4FaceLoop> loops)
    {
        double min = Double.PositiveInfinity;
        foreach (M4FaceLoop loop in loops)
            for (int i = 0; i < loop.Points.Count - 1; i++)
                min = Math.Min(min, M4PointSegmentDistance(point, loop.Points[i], loop.Points[i + 1]));
        return min;
    }

    private static bool M4SegmentsWithinTolerance(M4Point2 a, M4Point2 b, M4Point2 c, M4Point2 d, double tolerance)
    {
        if (M4SegmentsIntersect(a, b, c, d)) return true;
        return Math.Min(Math.Min(M4PointSegmentDistance(a, c, d), M4PointSegmentDistance(b, c, d)),
            Math.Min(M4PointSegmentDistance(c, a, b), M4PointSegmentDistance(d, a, b))) <= tolerance;
    }

    private static bool M4SegmentsIntersect(M4Point2 a, M4Point2 b, M4Point2 c, M4Point2 d)
    {
        double abC = M4Cross2(b.X - a.X, b.Y - a.Y, c.X - a.X, c.Y - a.Y);
        double abD = M4Cross2(b.X - a.X, b.Y - a.Y, d.X - a.X, d.Y - a.Y);
        double cdA = M4Cross2(d.X - c.X, d.Y - c.Y, a.X - c.X, a.Y - c.Y);
        double cdB = M4Cross2(d.X - c.X, d.Y - c.Y, b.X - c.X, b.Y - c.Y);
        const double eps = 1e-10;
        return ((abC > eps && abD < -eps) || (abC < -eps && abD > eps)) &&
            ((cdA > eps && cdB < -eps) || (cdA < -eps && cdB > eps));
    }

    private static double M4Cross2(double ax, double ay, double bx, double by) { return ax * by - ay * bx; }

    private static double M4PointSegmentDistance(M4Point2 point, M4Point2 first, M4Point2 last)
    {
        double dx = last.X - first.X, dy = last.Y - first.Y;
        double length2 = dx * dx + dy * dy;
        if (length2 <= 1e-24)
        {
            double px = point.X - first.X, py = point.Y - first.Y;
            return Math.Sqrt(px * px + py * py);
        }
        double t = ((point.X - first.X) * dx + (point.Y - first.Y) * dy) / length2;
        t = Math.Max(0.0, Math.Min(1.0, t));
        double ex = point.X - (first.X + t * dx), ey = point.Y - (first.Y + t * dy);
        return Math.Sqrt(ex * ex + ey * ey);
    }

    private static bool M4PointsNear(M4Point2 left, M4Point2 right, double tolerance)
    {
        double dx = left.X - right.X, dy = left.Y - right.Y;
        return dx * dx + dy * dy <= tolerance * tolerance;
    }

    private static double[] M4OffsetFrameOrigin(M4FaceCandidate face, double[] offset)
    {
        return new double[] {
            face.Origin[0] + offset[0] * face.XAxis[0] + offset[1] * face.YAxis[0],
            face.Origin[1] + offset[0] * face.XAxis[1] + offset[1] * face.YAxis[1],
            face.Origin[2] + offset[0] * face.XAxis[2] + offset[1] * face.YAxis[2]
        };
    }

    private static double Length3(double[] value) { return Math.Sqrt(value[0] * value[0] + value[1] * value[1] + value[2] * value[2]); }
    private static double Dot3(double[] left, double[] right) { return left[0] * right[0] + left[1] * right[1] + left[2] * right[2]; }
    private static void Normalize3(double[] value)
    {
        double length = Length3(value);
        if (length <= 1e-12) throw new M2ModelException("M4_FACE_UNSUPPORTED", "face frame contains a zero direction");
        value[0] /= length; value[1] /= length; value[2] /= length;
    }
    private static double[] Cross3(double[] left, double[] right)
    {
        return new double[] { left[1] * right[2] - left[2] * right[1],
            left[2] * right[0] - left[0] * right[2], left[0] * right[1] - left[1] * right[0] };
    }
    private static Vector3d Cross(Vector3d left, Vector3d right)
    {
        return new Vector3d(left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z, left.X * right.Y - left.Y * right.X);
    }
    private static Point3d ToPoint3d(double[] point) { return new Point3d(point[0], point[1], point[2]); }
    private static Vector3d ToVector3d(double[] vector) { return new Vector3d(vector[0], vector[1], vector[2]); }

    private static void M4PolygonAreaCentroid(List<M4Point2> points, out double area, out double cx, out double cy)
    {
        double twiceArea = 0.0, x = 0.0, y = 0.0;
        for (int i = 0; i < points.Count - 1; i++)
        {
            double cross = points[i].X * points[i + 1].Y - points[i + 1].X * points[i].Y;
            twiceArea += cross;
            x += (points[i].X + points[i + 1].X) * cross;
            y += (points[i].Y + points[i + 1].Y) * cross;
        }
        area = twiceArea / 2.0;
        if (Math.Abs(twiceArea) <= 1e-12) { cx = 0.0; cy = 0.0; }
        else { cx = x / (3.0 * twiceArea); cy = y / (3.0 * twiceArea); }
    }

    private static string ReadM4StringAttribute(NXObject target, string title)
    {
        if (target == null) return null;
        try { return target.GetStringUserAttribute(title, 0); }
        catch { return null; }
    }

    private static void PersistM4SupportMetadata(Sketch sketch, string selector, string bodyId, bool domainValidated)
    {
        if (sketch == null || sketch.Feature == null)
            throw new M2ModelException("M4_FACE_ASSOCIATION_INVALID", "on-face Sketch has no feature for persisted support metadata");
        NXObject target = sketch.Feature;
        target.SetUserAttribute(M4SelectorAttribute, 0, selector, Update.Option.Now);
        target.SetUserAttribute(M4BodyAttribute, 0, bodyId, Update.Option.Now);
        target.SetUserAttribute(M4DomainAttribute, 0, domainValidated ? "true" : "false", Update.Option.Now);
        target.SetUserAttribute(M4BoundaryMethodAttribute, 0,
            "linear-exact+circular-chord-0.001mm-clearance-0.002mm-v1", Update.Option.Now);
    }

    private static List<string> ReadM4CutFeatureIds(Sketch sketch)
    {
        List<string> ids = new List<string>();
        string text = ReadM4StringAttribute(sketch == null ? null : sketch.Feature, M4CutFeatureAttribute);
        if (String.IsNullOrWhiteSpace(text)) return ids;
        foreach (string value in text.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            if (!ids.Contains(value)) ids.Add(value);
        return ids;
    }

    private static void PersistM4CutFeatureIds(Sketch sketch, List<string> featureIds)
    {
        if (sketch == null || sketch.Feature == null) return;
        List<string> ids = featureIds == null ? new List<string>() : new List<string>(featureIds);
        ids.Sort(StringComparer.Ordinal);
        sketch.Feature.SetUserAttribute(M4CutFeatureAttribute, 0, String.Join("|", ids.ToArray()), Update.Option.Now);
    }

    private static string F21SupportInventory(Part work)
    {
        return M2Json.CanonicalText(new object[] { F18Inventory(work, null, false), F19PlaneInventory(work), F19DatumInventory(work) });
    }

    private static Dictionary<string, object> F21ReadFixedSupportAudited(Part work, Sketch sketch,
        DatumCsys datum, out Plane sourcePlane)
    {
        bool dirty; string before;
        try
        {
            dirty = work.IsModified; before = F21SupportInventory(work);
            if (work.IsModified != dirty) throw new InvalidOperationException("initial support inventory changed dirty state");
        }
        catch (Exception ex) { throw M3AnalysisStateUnknown("sketch support initial inventory could not be proven read-only", ex); }
        Dictionary<string, object> definition = null; Plane plane = null; Exception failure = null;
        try { definition = F21ReadSketchDatumCore(work, datum, sketch, out plane); }
        catch (Exception ex) { failure = ex; }
        try
        {
            if (work.IsModified != dirty) throw new InvalidOperationException("sketch support read changed dirty state");
            string after = F21SupportInventory(work);
            if (work.IsModified != dirty || !String.Equals(before, after, StringComparison.Ordinal))
                throw new InvalidOperationException("sketch support read changed captured inventory");
        }
        catch (Exception ex) { throw M3AnalysisStateUnknown("sketch support read state could not be proven unchanged", ex); }
        if (failure != null) throw failure;
        sourcePlane = plane; return definition;
    }

    private static Plane FindM4AttachedPlane(Part work, Sketch sketch)
    { Dictionary<string, object> ignored; return FindM4AttachedPlane(work, sketch, out ignored); }

    private static Plane FindM4AttachedPlane(Part work, Sketch sketch, out Dictionary<string, object> fixedSupport)
    {
        fixedSupport = null;
        // Prefer the documented native attachment object. Tag identity is
        // session-local but exact here; a coincident geometric plane is not
        // evidence of a dependency. Do not select an ambiguous object.
        NXObject direct = sketch.AttachPlane as NXObject;
        if (direct != null)
        {
            if (direct.IsOccurrence || direct.OwningPart == null || !direct.OwningPart.Tag.Equals(work.Tag))
                throw new M2ModelException("UNSUPPORTED_MODEL", "sketch attachment is not a native work-part object");
            Plane matched = null;
            Plane[] available = work.Planes.ToArray();
            if (available == null) throw new M2ModelException("MODEL_READ_FAILED", "work-part plane inventory is unavailable");
            foreach (Plane item in available)
            {
                if (item == null) throw new M2ModelException("MODEL_READ_FAILED", "work-part plane inventory contains null");
                if (!item.Tag.Equals(direct.Tag)) continue;
                if (matched != null || item.IsOccurrence || item.OwningPart == null || !item.OwningPart.Tag.Equals(work.Tag))
                    throw new M2ModelException("UNSUPPORTED_MODEL", "sketch attachment plane is ambiguous or external");
                matched = item;
            }
            if (matched != null) return matched;
            DatumPlane attachedDatum = direct as DatumPlane;
            DatumCsys datumCsys = attachedDatum == null ? null : attachedDatum.Feature as DatumCsys;
            if (datumCsys != null)
            {
                Plane source;
                fixedSupport = F21ReadFixedSupportAudited(work, sketch, datumCsys, out source);
                if (source == null) throw new M2ModelException("UNSUPPORTED_MODEL", "fixed sketch support source plane is unavailable");
                return source;
            }
        }
        // Rebuild the live sketch builder to read its actual PlaneReference.
        // This is direct dependency evidence; coincident origin/normal alone is
        // insufficient because unrelated planes can occupy the same location.
        SketchInPlaceBuilder attachedBuilder = null;
        try
        {
            attachedBuilder = work.Sketches.CreateSketchInPlaceBuilder2(sketch);
            Plane referenced = attachedBuilder.PlaneReference;
            if (referenced != null) return referenced;
        }
        catch { }
        finally
        {
            if (attachedBuilder != null) try { attachedBuilder.Destroy(); }
            catch (Exception ex) { throw M3AnalysisStateUnknown("sketch support builder cleanup failed", ex); }
        }

        NXObject attached = null;
        try { attached = (object)sketch.AttachPlane as NXObject; }
        catch { attached = null; }
        string attachedId = null;
        Tag attachedTag = Tag.Null;
        if (attached != null)
        {
            try { attachedId = attached.JournalIdentifier; } catch { }
            try { attachedTag = attached.Tag; } catch { }
        }
        Plane[] planes = work.Planes.ToArray();
        if (planes == null) return null;
        foreach (Plane plane in planes)
        {
            if (plane == null) continue;
            try
            {
                if (!String.IsNullOrEmpty(attachedId) && String.Equals(plane.JournalIdentifier, attachedId, StringComparison.Ordinal))
                    return plane;
                if (attachedTag != Tag.Null && plane.Tag.Equals(attachedTag)) return plane;
            }
            catch { }
        }
        return null;
    }

    private static bool ReadM4NativeSupport(Part work, Sketch sketch, Plane plane, string expectedBodyId,
        out Dictionary<string, object> evidence)
    {
        evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bool native = false;
        string geometryKind = null;
        string actualBodyId = null;
        string faceId = null;
        Face face = null;
        try
        {
            if (plane == null) plane = FindM4AttachedPlane(work, sketch);
            if (plane != null)
            {
                evidence.Add("plane_method", plane.Method.ToString());
                evidence.Add("plane_journal_id", plane.JournalIdentifier);
                evidence.Add("plane_origin_mm", Point(plane.Origin));
                evidence.Add("plane_normal_world", Vector(plane.Normal.X, plane.Normal.Y, plane.Normal.Z));
                NXObject[] geometry = plane.GetGeometry();
                evidence.Add("face_geometry_count", geometry == null ? 0 : geometry.Length);
                if (geometry != null && geometry.Length == 1)
                {
                    face = geometry[0] as Face;
                    if (face != null)
                    {
                        geometryKind = "Face";
                        faceId = RequiredJournalId(face, "M4 support face");
                        Body body = face.GetBody();
                        actualBodyId = RequiredJournalId(body, "M4 support body");
                        native = plane.Method == PlaneTypes.MethodType.Coincident;
                        if (!String.IsNullOrEmpty(expectedBodyId) && !String.Equals(expectedBodyId, actualBodyId, StringComparison.Ordinal))
                            native = false;
                        int type;
                        double[] point = new double[3], direction = new double[3], box = new double[6];
                        double radius, radiusData;
                        int normalDirection;
                        UFSession.GetUFSession().Modl.AskFaceData(face.Tag, out type, point, direction,
                            box, out radius, out radiusData, out normalDirection);
                        double length = Length3(direction);
                        double dot = length <= 1e-12 ? 0.0 :
                            Dot3(new double[] { direction[0] / length,
                                direction[1] / length, direction[2] / length },
                            new double[] { plane.Normal.X, plane.Normal.Y, plane.Normal.Z });
                        evidence.Add("face_type", type);
                        evidence.Add("face_normal_alignment", dot);
                        if (type != UFConstants.UF_MODL_PLANAR_FACE || dot < M4FaceNormalDotTolerance) native = false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            M2ModelException modelFailure = ex as M2ModelException;
            if (modelFailure != null && String.Equals(modelFailure.Code, "M3_ANALYSIS_STATE_UNKNOWN", StringComparison.Ordinal)) throw;
            evidence["readback_error"] = ex.GetType().Name;
            native = false;
        }
        evidence["native"] = native;
        evidence["geometry_kind"] = geometryKind;
        evidence["support_face_id"] = faceId;
        evidence["support_face_tag"] = face == null ? null : face.Tag.ToString();
        evidence["body_id"] = actualBodyId;
        evidence["expected_body_id"] = expectedBodyId;
        evidence["current_face_matches"] = false;
        return native;
    }

    private static List<string> M3AncestorFeatureIds(Feature feature)
    {
        List<string> ids = new List<string>();
        Queue<Feature> pending = new Queue<Feature>();
        foreach (Feature parent in feature.GetParents()) if (parent != null) pending.Enqueue(parent);
        while (pending.Count != 0)
        {
            Feature parent = pending.Dequeue();
            string id = RequiredJournalId(parent, "sketch ancestor feature");
            if (ids.Contains(id)) continue;
            ids.Add(id);
            foreach (Feature ancestor in parent.GetParents()) if (ancestor != null) pending.Enqueue(ancestor);
        }
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    private static bool TryReadM3NativeSupport(Part work, Sketch sketch, Plane plane,
        out Dictionary<string, object> association, out Dictionary<string, object> frame)
    {
        association = null;
        frame = null;
        if (work == null || sketch == null) return false;
        Dictionary<string, object> fixedSupport = null;
        if (plane == null) plane = FindM4AttachedPlane(work, sketch, out fixedSupport);
        if (plane == null) return false;
        NXObject[] geometry = plane.GetGeometry();
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Plane.GetGeometry returned null for sketch support readback");
        Feature[] parents = sketch.Feature == null ? new Feature[0] : sketch.Feature.GetParents();
        if (parents == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch feature parents returned null during support readback");
        List<string> parentIds = new List<string>();
        DatumPlane datum = null;
        Feature datumFeature = null;
        Face face = null;
        foreach (Feature parent in parents)
        {
            if (parent == null) continue;
            parentIds.Add(RequiredJournalId(parent, "sketch parent feature"));
            DatumPlane parentDatum = TryGetM3DatumPlane(parent);
            if (parentDatum != null)
            {
                datum = parentDatum;
                datumFeature = parent;
            }
        }
        foreach (NXObject item in geometry)
        {
            Face candidateFace = item as Face;
            if (candidateFace != null) face = candidateFace;
            DatumPlane candidateDatum = item as DatumPlane;
            if (candidateDatum != null)
            {
                datum = candidateDatum;
                datumFeature = candidateDatum.Feature;
            }
            Feature candidateFeature = item as Feature;
            if (candidateFeature != null)
            {
                DatumPlane featureDatum = TryGetM3DatumPlane(candidateFeature);
                if (featureDatum != null)
                {
                    datum = featureDatum;
                    datumFeature = candidateFeature;
                }
            }
        }
        if (face != null && datum != null)
            throw new M2ModelException("UNSUPPORTED_MODEL", "sketch support plane resolves to both a Face and DatumPlane");

        frame = M4SketchFrameRecord(sketch);
        association = new Dictionary<string, object>(StringComparer.Ordinal);
        association.Add("plane_method", plane.Method.ToString());
        association.Add("plane_journal_id", plane.JournalIdentifier);
        association.Add("plane_origin_mm", Point(plane.Origin));
        association.Add("plane_normal_world", Vector(plane.Normal.X, plane.Normal.Y, plane.Normal.Z));
        association.Add("plane_geometry_count", geometry.Length);
        association.Add("parent_feature_ids", ObjectStrings(parentIds));
        List<string> ancestorIds = sketch.Feature == null ? new List<string>() : M3AncestorFeatureIds(sketch.Feature);
        association.Add("ancestor_feature_ids", ObjectStrings(ancestorIds));
        association.Add("support_frame", frame);
        if (fixedSupport != null) association.Add("native_fixed_datum_definition", fixedSupport);
        if (face != null)
        {
            Body body = face.GetBody();
            bool nativeFace = plane.Method == PlaneTypes.MethodType.Coincident && geometry.Length == 1;
            association.Add("support_kind", "FACE");
            association.Add("support_face_id", RequiredJournalId(face, "sketch support face"));
            association.Add("support_face_tag", face.Tag.ToString());
            association.Add("support_body_id", RequiredJournalId(body, "sketch support body"));
            association.Add("native", nativeFace);
            return true;
        }
        if (datum != null && datumFeature != null)
        {
            string datumId = RequiredJournalId(datumFeature, "sketch support DatumPlane feature");
            bool parentMatches = ancestorIds.Contains(datumId);
            association.Add("support_kind", "DATUM_PLANE");
            association.Add("datum_plane_id", datumId);
            association.Add("native_feature_parent", parentMatches);
            association.Add("datum_plane", M3DatumPlaneDefinitionRecord(work, datum, datumFeature));
            association.Add("native", parentMatches);
            return true;
        }
        if (geometry.Length == 0)
        {
            association.Add("support_kind", "WORLD_FRAME");
            association.Add("native", true);
            return true;
        }
        association.Add("support_kind", "UNKNOWN");
        association.Add("native", false);
        return true;
    }

    private bool TryReadM4SketchSupport(Part work, Sketch sketch,
        out Dictionary<string, object> association, out Dictionary<string, object> frame)
    {
        association = null;
        frame = null;
        string selector = ReadM4StringAttribute(sketch.Feature, M4SelectorAttribute);
        string bodyId = ReadM4StringAttribute(sketch.Feature, M4BodyAttribute);
        string domain = ReadM4StringAttribute(sketch.Feature, M4DomainAttribute);
        if (String.IsNullOrWhiteSpace(selector) || String.IsNullOrWhiteSpace(bodyId)) return false;

        Plane plane = FindM4AttachedPlane(work, sketch);
        Dictionary<string, object> nativeEvidence;
        bool native = ReadM4NativeSupport(work, sketch, plane, bodyId, out nativeEvidence);
        bool selectorUnique = false;
        bool currentFaceMatches = false;
        M4FaceCandidate selected = null;
        string selectionError = null;
        try
        {
            List<string> ownCutFeatures = ReadM4CutFeatureIds(sketch);
            selected = ResolveM4UniqueFace(work, selector, bodyId, ownCutFeatures, sketch);
            selectorUnique = true;
            string supportFaceTag = Convert.ToString(nativeEvidence["support_face_tag"], CultureInfo.InvariantCulture);
            currentFaceMatches = String.Equals(selected.Face.Tag.ToString(), supportFaceTag, StringComparison.Ordinal);
        }
        catch (M2ModelException ex) { selectionError = ex.Code + ": " + ex.Message; }
        Dictionary<string, object> profileDomainEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
        bool profileInDomain = selected != null && M4SketchGeometryInsideFace(sketch, selected, out profileDomainEvidence);
        if (selected == null) profileDomainEvidence["status"] = "UNRESOLVED_SUPPORT_FACE";

        Point3d origin = sketch.Origin;
        Matrix3x3 axes = sketch.Orientation.Element;
        double[] frameOrigin = new double[] { origin.X, origin.Y, origin.Z };
        double[] xAxis = new double[] { axes.Xx, axes.Xy, axes.Xz };
        double[] yAxis = new double[] { axes.Yx, axes.Yy, axes.Yz };
        double[] normal = new double[] { axes.Zx, axes.Zy, axes.Zz };
        frame = M4FrameRecord(frameOrigin, xAxis, yAxis, normal);

        association = new Dictionary<string, object>(StringComparer.Ordinal);
        association.Add("native", native);
        association.Add("geometry_kind", nativeEvidence["geometry_kind"] ?? "unknown");
        association.Add("plane_method", nativeEvidence.ContainsKey("plane_method") ? nativeEvidence["plane_method"] : null);
        association.Add("support_face_id", nativeEvidence["support_face_id"]);
        association.Add("body_id", bodyId);
        association.Add("selector", selector);
        association.Add("selector_unique", selectorUnique);
        association.Add("current_face_matches", currentFaceMatches);
        association.Add("in_face_domain", String.Equals(domain, "true", StringComparison.OrdinalIgnoreCase) && profileInDomain);
        association.Add("profile_domain_readback", profileDomainEvidence);
        association.Add("domain_method", ReadM4StringAttribute(sketch.Feature, M4BoundaryMethodAttribute));
        association.Add("selection_error", selectionError);
        if (selected != null)
        {
            association.Add("face_area_mm2", selected.Area);
            association.Add("loop_count", selected.Loops == null ? 0 : selected.Loops.Count);
            association.Add("boundary_types", StringListAsObjects(selected.BoundaryTypes));
            association.Add("boundary_chord_tolerance_mm", M4BoundaryChordToleranceMm);
        }
        association.Add("native_readback", nativeEvidence);
        return true;
    }

    private static void RefreshM4SupportTracksFromPart(Part work, BindingState state)
    {
        Sketch[] sketches = work.Sketches.ToArray();
        if (sketches == null) return;
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) continue;
            string selector = ReadM4StringAttribute(sketch.Feature, M4SelectorAttribute);
            string bodyId = ReadM4StringAttribute(sketch.Feature, M4BodyAttribute);
            if (String.IsNullOrWhiteSpace(selector) || String.IsNullOrWhiteSpace(bodyId)) continue;
            Plane plane = FindM4AttachedPlane(work, sketch);
            Dictionary<string, object> evidence;
            bool native = ReadM4NativeSupport(work, sketch, plane, bodyId, out evidence);
            string sketchId = RequiredJournalId(sketch, "M4 on-face sketch");
            M4SupportTrack track;
            if (!state.M4SupportTracks.TryGetValue(sketchId, out track))
            {
                track = new M4SupportTrack();
                track.SketchId = sketchId;
                state.M4SupportTracks.Add(sketchId, track);
            }
            track.BodyId = bodyId;
            track.Selector = selector;
            if (String.IsNullOrEmpty(track.FaceId) && evidence["support_face_tag"] != null)
                track.FaceId = Convert.ToString(evidence["support_face_tag"], CultureInfo.InvariantCulture);
            track.FeatureId = sketch.Feature == null ? null : RequiredJournalId(sketch.Feature, "M4 sketch feature");
            track.Plane = plane;
            track.DomainValidated = String.Equals(ReadM4StringAttribute(sketch.Feature, M4DomainAttribute), "true", StringComparison.OrdinalIgnoreCase);
            track.Native = native;
            track.GeometryKind = Convert.ToString(evidence["geometry_kind"], CultureInfo.InvariantCulture);
            track.CutFeatureIds = ReadM4CutFeatureIds(sketch);
            Point3d origin = sketch.Origin;
            track.SupportOrigin = new double[] { origin.X, origin.Y, origin.Z };
        }
    }

    private bool ValidateM4SupportTracks(BindingState state, out List<object> records, HashSet<string> refreshFaceIds)
    {
        records = new List<object>();
        RefreshM4SupportTracksFromPart(state.WorkPart, state);
        bool allValid = true;
        foreach (M4SupportTrack track in state.M4SupportTracks.Values)
        {
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("sketch_id", track.SketchId);
            item.Add("body_id", track.BodyId);
            item.Add("selector", track.Selector);
            Sketch sketch = FindSketch(state.WorkPart, track.SketchId);
            Plane plane = FindM4AttachedPlane(state.WorkPart, sketch);
            Dictionary<string, object> nativeEvidence;
            bool native = ReadM4NativeSupport(state.WorkPart, sketch, plane, track.BodyId, out nativeEvidence);
            bool unique = false, matches = false, topologyMatches = false;
            string error = null;
            M4FaceCandidate selected = null;
            try
            {
                selected = ResolveM4UniqueFace(state.WorkPart, track.Selector, track.BodyId, track.CutFeatureIds, sketch);
                unique = true;
                string planeFaceTag = Convert.ToString(nativeEvidence["support_face_tag"], CultureInfo.InvariantCulture);
                string selectedFaceTag = selected.Face.Tag.ToString();
                matches = String.Equals(selectedFaceTag, planeFaceTag, StringComparison.Ordinal);
                topologyMatches = String.Equals(track.FaceId, selectedFaceTag, StringComparison.Ordinal);
            }
            catch (M2ModelException ex) { error = ex.Code + ": " + ex.Message; }
            Dictionary<string, object> profileDomainEvidence = new Dictionary<string, object>(StringComparer.Ordinal);
            bool profileInDomain = selected != null && M4SketchGeometryInsideFace(sketch, selected, out profileDomainEvidence);
            bool domainValidated = track.DomainValidated && String.Equals(
                ReadM4StringAttribute(sketch.Feature, M4DomainAttribute), "true", StringComparison.OrdinalIgnoreCase) && profileInDomain;
            bool refreshThis = refreshFaceIds != null && refreshFaceIds.Contains(track.SketchId);
            bool valid = native && unique && matches && domainValidated && (refreshThis || topologyMatches);
            item.Add("native", native);
            item.Add("geometry_kind", nativeEvidence["geometry_kind"] ?? "unknown");
            item.Add("native_support_face_journal_id", nativeEvidence["support_face_id"]);
            item.Add("native_support_face_tag", nativeEvidence["support_face_tag"]);
            item.Add("selector_face_journal_id", selected == null ? null : selected.FaceId);
            item.Add("selector_face_tag", selected == null || selected.Face == null ? null : selected.Face.Tag.ToString());
            item.Add("selector_unique", unique);
            item.Add("current_face_matches", matches);
            item.Add("topology_matches", topologyMatches);
            item.Add("in_face_domain", domainValidated);
            item.Add("support_frame", M4SketchFrameRecord(sketch));
            item.Add("profile_domain_readback", profileDomainEvidence);
            item.Add("native_readback", nativeEvidence);
            item.Add("selection_error", error);
            item.Add("status", valid ? "VERIFIED" : "MISMATCH");
            records.Add(item);
            if (!valid) allValid = false;
            if (valid && refreshThis)
            {
                track.FaceId = selected.Face.Tag.ToString();
                track.Plane = plane;
            }
        }
        return allValid;
    }

    private static List<object> M4SupportSnapshot(BindingState state)
    {
        List<string> ids = new List<string>(state.M4SupportTracks.Keys);
        ids.Sort(StringComparer.Ordinal);
        List<object> result = new List<object>();
        foreach (string id in ids)
        {
            M4SupportTrack track = state.M4SupportTracks[id];
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("sketch_id", track.SketchId);
            item.Add("body_id", track.BodyId);
            item.Add("selector", track.Selector);
            item.Add("support_face_tag", track.FaceId);
            Sketch sketch = FindSketch(state.WorkPart, track.SketchId);
            item.Add("support_frame", M4SketchFrameRecord(sketch));
            result.Add(item);
        }
        return result;
    }

    private static void RequireM4SubtractContourEditSupported(Part work, BindingState state, string editedSketchId)
    {
        if (state.M4SupportTracks.Count == 0) return;
        Sketch editedSketch = FindSketch(work, editedSketchId);
        if (editedSketch.Feature == null) return;
        HashSet<string> supportBodies = new HashSet<string>(StringComparer.Ordinal);
        foreach (M4SupportTrack track in state.M4SupportTracks.Values) supportBodies.Add(track.BodyId);
        Feature[] features = work.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature history returned null while checking M4 contour edits");
        foreach (Feature feature in features)
        {
            if (feature == null || !String.Equals(feature.FeatureType, "EXTRUDE", StringComparison.Ordinal)) continue;
            ExtrudeBuilder builder = null;
            try
            {
                builder = work.Features.CreateExtrudeBuilder(feature);
                if (builder.BooleanOperation == null || builder.BooleanOperation.Type != NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Subtract)
                    continue;
                Body[] targets = builder.BooleanOperation.GetTargetBodies();
                if (targets == null || targets.Length == 0) continue;
                bool sameSupportBody = false;
                foreach (Body target in targets)
                    if (target != null && supportBodies.Contains(RequiredJournalId(target, "M4 subtract target body"))) sameSupportBody = true;
                if (!sameSupportBody) continue;
                Feature[] parents = feature.GetParents();
                if (parents == null) throw new M2ModelException("M4_SUBTRACT_CONTOUR_EDIT_UNSUPPORTED", "an existing support-body Subtract has unreadable sketch parents");
                foreach (Feature parent in parents)
                    if (parent != null && parent.Tag.Equals(editedSketch.Feature.Tag))
                        throw new M2ModelException("M4_SUBTRACT_CONTOUR_EDIT_UNSUPPORTED",
                            "M4 permits upstream create/unite support changes, but refuses edits to an existing Subtract profile on the supported body because merged face-domain changes are not verified");
            }
            catch (M2ModelException) { throw; }
            catch (Exception ex)
            {
                throw new M2ModelException("M4_SUBTRACT_CONTOUR_EDIT_UNSUPPORTED",
                    "M4 cannot safely inspect an existing support-body Subtract contour (" + ex.GetType().Name + ")");
            }
            finally
            {
                if (builder != null) try { builder.Destroy(); } catch { }
            }
        }
    }

    private static Dictionary<string, object> M4SketchFrameRecord(Sketch sketch)
    {
        Point3d origin = sketch.Origin;
        Matrix3x3 axes = sketch.Orientation.Element;
        return M4FrameRecord(new double[] { origin.X, origin.Y, origin.Z },
            new double[] { axes.Xx, axes.Xy, axes.Xz }, new double[] { axes.Yx, axes.Yy, axes.Yz },
            new double[] { axes.Zx, axes.Zy, axes.Zz });
    }

    private List<object> BuildSketches(Part work)
    {
        Sketch[] sketches = work.Sketches.ToArray();
        if (sketches == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketches.ToArray returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Sketch sketch in sketches)
        {
            if (sketch == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch collection contains null");
            records.Add(BuildSketchDetail(work, sketch, work.PartUnits.ToString()));
        }
        SortRecords(records, "journal_id", "sketch");
        return ToObjectList(records);
    }

    private Dictionary<string, object> BuildSketchDetail(Part work, Sketch sketch, string units)
    {
        string id = RequiredJournalId(sketch, "sketch");
        int degreesOfFreedom;
        Sketch.Status status = sketch.GetStatus(out degreesOfFreedom);
        Point3d origin = sketch.Origin;
        Matrix3x3 axes = sketch.Orientation.Element;
        NXObject[] geometry = sketch.GetAllGeometry();
        if (geometry == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch.GetAllGeometry returned null for " + id);

        Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
        item.Add("journal_id", id);
        item.Add("feature_journal_id", sketch.Feature == null ? null : RequiredJournalId(sketch.Feature, "sketch feature"));
        item.Add("status", status.ToString());
        item.Add("degrees_of_freedom", degreesOfFreedom.ToString(CultureInfo.InvariantCulture));
        item.Add("units", units);
        item.Add("origin", Point(origin));
        item.Add("normal", Vector(axes.Zx, axes.Zy, axes.Zz));
        item.Add("x_axis", Vector(axes.Xx, axes.Xy, axes.Xz));
        item.Add("y_axis", Vector(axes.Yx, axes.Yy, axes.Yz));
        Feature[] sketchParents = sketch.Feature == null ? new Feature[0] : sketch.Feature.GetParents();
        if (sketchParents == null) throw new M2ModelException("MODEL_READ_FAILED", "Sketch feature parents returned null for " + id);
        item.Add("parent_feature_ids", ObjectStrings(GetJournalIds(sketchParents, "sketch parent feature")));
        item.Add("geometry", BuildSketchGeometry(sketch, geometry, units));
        item.Add("constraints", BuildSketchConstraints(sketch));
        bool hasNativeFeature = sketch.Feature != null && !sketch.Feature.IsOccurrence &&
            sketch.Feature.OwningPart != null && sketch.Feature.OwningPart.Tag == work.Tag;
        item.Add("history_support", SupportStatus(hasNativeFeature ? "NATIVE_FEATURE_HISTORY" : "APPEND_ONLY_NO_NATIVE_FEATURE",
            hasNativeFeature
                ? "This Sketch has an NX feature; only a verified Driving dimension with a supported M2 geometry signature is editable."
                : "This Sketch has no native feature history in the Work Part and is not editable; supported operations may append new features."));
        item.Add("history_feature_available", hasNativeFeature);
        Dictionary<string, object> supportAssociation;
        Dictionary<string, object> supportFrame;
        if (TryReadM4SketchSupport(work, sketch, out supportAssociation, out supportFrame))
        {
            item.Add("support_association", supportAssociation);
            item.Add("support_frame", supportFrame);
        }
        else if (TryReadM3NativeSupport(work, sketch, null, out supportAssociation, out supportFrame))
        {
            item.Add("support_association", supportAssociation);
            item.Add("support_frame", supportFrame);
        }
        item.Add("support", SupportStatus("SUPPORTED_METADATA", "Sketch frame, degrees of freedom, geometry identities and enumerated constraints were read."));
        return item;
    }

    private static List<object> BuildSketchGeometry(NXObject[] geometry, string units)
    {
        return BuildSketchGeometry(null, geometry, units);
    }

    private static List<object> BuildSketchGeometry(Sketch sketch, NXObject[] geometry, string units)
    {
        UFSession uf = sketch == null ? null : UFSession.GetUFSession();
        if (sketch != null && uf == null)
            throw new M2ModelException("MODEL_READ_FAILED", "UF session is unavailable during geometry inspection");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (NXObject obj in geometry)
        {
            if (obj == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch geometry contains null");
            string id = RequiredJournalId(obj, "sketch geometry");
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("journal_id", id);
            item.Add("object_type", obj.GetType().Name);
            item.Add("units", units);
            Curve sketchCurve = obj as Curve;
            if (sketchCurve != null && sketch != null)
            {
                UFSket.ReferenceStatus referenceStatus;
                try { uf.Sket.AskReferenceStatus(sketch.Tag, sketchCurve.Tag, out referenceStatus); }
                catch (Exception ex)
                {
                    throw new M2ModelException("MODEL_READ_FAILED",
                        "UF.Sket.AskReferenceStatus failed during sketch geometry inspection (" + ex.GetType().Name + ")");
                }
                item.Add("reference_status", referenceStatus.ToString());
                item.Add("reference_status_value", Convert.ToInt32(referenceStatus, CultureInfo.InvariantCulture));
                item.Add("reference_curve_solver_semantics",
                    referenceStatus == UFSket.ReferenceStatus.Reference
                        ? "reference_curve_can_still_participate_in_constraints"
                        : referenceStatus == UFSket.ReferenceStatus.Active
                            ? "active_sketch_curve"
                            : "native_status_not_classified");
            }
            Line line = obj as Line;
            if (line != null)
            {
                item.Add("detail_status", "SUPPORTED");
                item.Add("kind", "LINE");
                item.Add("start", Point(line.StartPoint));
                item.Add("end", Point(line.EndPoint));
            }
            else if (obj is Arc)
            {
                Arc arc = (Arc)obj;
                Vector3d arcX, arcY;
                Point3d arcCenter;
                arc.GetOrientation(out arcCenter, out arcX, out arcY);
                Vector3d arcNormal = Cross(arcX, arcY);
                item.Add("detail_status", "SUPPORTED");
                item.Add("kind", "ARC");
                item.Add("center", Point(arcCenter));
                item.Add("radius_mm", arc.Radius);
                item.Add("start_angle_rad", arc.StartAngle);
                item.Add("end_angle_rad", arc.EndAngle);
                item.Add("start", Point(ConicPoint(arcCenter, arcX, arcY, arc.Radius, arc.Radius, 0.0, arc.StartAngle)));
                item.Add("end", Point(ConicPoint(arcCenter, arcX, arcY, arc.Radius, arc.Radius, 0.0, arc.EndAngle)));
                item.Add("normal", Vector(arcNormal.X, arcNormal.Y, arcNormal.Z));
            }
            else if (obj is Ellipse)
            {
                Ellipse ellipse = (Ellipse)obj;
                double major, minor, startAngle, endAngle, rotation;
                Point3d center;
                ellipse.GetParameters(out major, out minor, out startAngle, out endAngle, out rotation, out center);
                double[] parameterLimits = NativeCurveParameterLimits(ellipse);
                Point3d frameCenter; Vector3d xAxis, yAxis;
                ellipse.GetOrientation(out frameCenter, out xAxis, out yAxis);
                Vector3d majorAxis = RotateInPlane(xAxis, yAxis, rotation);
                Vector3d ellipseNormal = Cross(xAxis, yAxis);
                double sweep = Math.Abs(parameterLimits[1] - parameterLimits[0]);
                item.Add("detail_status", "SUPPORTED");
                item.Add("kind", sweep >= 2.0 * Math.PI - 1e-8 ? "ELLIPSE" : "ELLIPTICAL_ARC");
                item.Add("center", Point(center));
                item.Add("major_radius_mm", major);
                item.Add("minor_radius_mm", minor);
                item.Add("major_axis_direction", Vector(majorAxis.X, majorAxis.Y, majorAxis.Z));
                item.Add("rotation_rad", rotation);
                item.Add("start_angle_rad", parameterLimits[0]);
                item.Add("end_angle_rad", parameterLimits[1]);
                item.Add("native_polar_start_angle_rad", startAngle);
                item.Add("native_polar_end_angle_rad", endAngle);
                item.Add("start", Point(NativeCurvePoint(ellipse, parameterLimits[0])));
                item.Add("end", Point(NativeCurvePoint(ellipse, parameterLimits[1])));
                item.Add("normal", Vector(ellipseNormal.X, ellipseNormal.Y, ellipseNormal.Z));
            }
            else if (obj is Spline)
            {
                Spline spline = (Spline)obj;
                item.Add("detail_status", "SUPPORTED");
                item.Add("kind", "SPLINE");
                item.Add("nurbs_definition", SplineDefinitionRecord(spline));
                try
                {
                    int degree, periodic, count;
                    UFCurve.PtSlopeCrvatr[] interpolationPoints;
                    double[] parameters;
                    UFSession.GetUFSession().Curve.AskSplineThruPts(spline.Tag,
                        out degree, out periodic, out count, out interpolationPoints, out parameters);
                    if (interpolationPoints == null || parameters == null || interpolationPoints.Length != count || parameters.Length != count)
                        throw new M2ModelException("MODEL_READ_FAILED", "spline interpolation definition is incomplete");
                    List<object> pointRecords = new List<object>();
                    List<object> evaluatedRecords = new List<object>();
                    bool fitPointsOnCurve = true;
                    for (int i = 0; i < interpolationPoints.Length; i++)
                    {
                        UFCurve.PtSlopeCrvatr fitPoint = interpolationPoints[i];
                        Point3d fitWorld = new Point3d(fitPoint.point[0], fitPoint.point[1], fitPoint.point[2]);
                        pointRecords.Add(Point(fitWorld));
                        double[] evaluated = new double[3];
                        UFSession.GetUFSession().Curve.EvaluateCurve(spline.Tag, parameters[i], 0, evaluated);
                        Point3d evaluatedPoint = new Point3d(evaluated[0], evaluated[1], evaluated[2]);
                        evaluatedRecords.Add(Point(evaluatedPoint));
                        if (!PointNearlyEqual(fitWorld, evaluatedPoint)) fitPointsOnCurve = false;
                    }
                    item.Add("interpolation_readback_status", "SUPPORTED");
                    item.Add("degree", degree);
                    item.Add("periodic", periodic);
                    item.Add("interpolation_point_count", count);
                    item.Add("interpolation_points", pointRecords);
                    item.Add("interpolation_parameters", NumberList(parameters));
                    item.Add("evaluated_interpolation_points", evaluatedRecords);
                    item.Add("interpolation_points_on_curve", fitPointsOnCurve);
                    item.Add("native_interpolation_conditions", SplineInterpolationConditionRecords(interpolationPoints));
                    item.Add("start", pointRecords[0]);
                    item.Add("end", pointRecords[pointRecords.Count - 1]);
                }
                catch
                {
                    item.Add("interpolation_readback_status", "UNAVAILABLE");
                }
            }
            else if (obj is SketchVirtualPoint)
            {
                SketchVirtualPoint virtualPoint = (SketchVirtualPoint)obj;
                item.Add("detail_status", "SUPPORTED");
                item.Add("kind", "SKETCH_VIRTUAL_POINT");
                item.Add("coordinates", Point(virtualPoint.Coordinates));
            }
            else if (obj is NXOpen.Point)
            {
                NXOpen.Point point = (NXOpen.Point)obj;
                item.Add("detail_status", "SUPPORTED");
                item.Add("kind", "POINT");
                item.Add("coordinates", Point(point.Coordinates));
            }
            else
            {
                item.Add("detail_status", "UNSUPPORTED");
                item.Add("reason", "Geometry identity is available, but detailed parameters for " + obj.GetType().Name + " are not implemented.");
            }
            records.Add(item);
        }
        SortRecords(records, "journal_id", "sketch geometry");
        return ToObjectList(records);
    }

    private static List<object> BuildSketchConstraints(Sketch sketch)
    {
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
            if (constraints == null)
                throw new M2ModelException("MODEL_READ_FAILED", "constraint enumeration returned null for " + type);
            foreach (SketchConstraint constraint in constraints)
            {
                if (constraint == null) throw new M2ModelException("UNSUPPORTED_MODEL", "sketch constraint collection contains null");
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item.Add("constraint_type", type.ToString());
                SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                SketchGeometricConstraint geometric = constraint as SketchGeometricConstraint;
                if (dimension != null)
                {
                    Sketch.DimensionGeometry[] operands = dimension.GetDimensionGeometry();
                    if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "dimension geometry returned null for " + type);
                    List<string> references = new List<string>();
                    foreach (Sketch.DimensionGeometry operand in operands) references.Add(DimensionReference(operand));
                    references.Sort(StringComparer.Ordinal);
                    Expression associated = dimension.AssociatedExpression;
                    SketchDimensionalConstraint.DimensionStateType dimensionState = dimension.DimensionState;
                    bool driving = dimensionState == SketchDimensionalConstraint.DimensionStateType.Driving;
                    string expressionName = associated == null ? null : associated.Name;
                    string dimensionValue = associated == null ? null : NumberText(associated.Value);
                    NXOpen.Annotations.Dimension associatedDimension = dimension.AssociatedDimension;
                    item.Add("class", "DIMENSIONAL");
                    item.Add("geometry_refs", ObjectStrings(references));
                    item.Add("is_driving", driving);
                    item.Add("dimension_state", dimensionState.ToString());
                    item.Add("solver_participation", driving ? "DRIVES_GEOMETRY" :
                        dimensionState == SketchDimensionalConstraint.DimensionStateType.Reference
                            ? "MEASUREMENT_ONLY" : "OTHER_NATIVE_STATE");
                    item.Add("expression_name", expressionName);
                    item.Add("value", dimensionValue);
                    item.Add("expression_value", dimensionValue);
                    item.Add("computed_size", associatedDimension == null ? null : NumberText(associatedDimension.ComputedSize));
                    if (associated != null)
                    {
                        item.Add("expression_flags", new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "is_measurement_expression", associated.IsMeasurementExpression },
                            { "is_geometric_expression", associated.IsGeometricExpression },
                            { "is_no_edit", associated.IsNoEdit },
                            { "is_no_update", associated.IsNoUpdate },
                            { "is_rhs_locked_from_edit", associated.IsRightHandSideLockedFromEdit },
                            { "is_user_locked", associated.IsUserLocked },
                            { "is_user_specified", associated.IsUserSpecified }
                        });
                    }
                    item.Add("journal_id", RequiredJournalId(constraint, "sketch constraint"));
                    item.Add("semantic_key", type.ToString() + "|" + JoinStrings(item["geometry_refs"] as List<object>) +
                        "|" + (expressionName ?? String.Empty) + "|" + (dimensionValue ?? String.Empty));
                }
                else if (geometric != null)
                {
                    Sketch.ConstraintGeometry[] operands = geometric.GetGeometry();
                    if (operands == null) throw new M2ModelException("MODEL_READ_FAILED", "geometric constraint operands returned null for " + type);
                    List<string> references = new List<string>();
                    foreach (Sketch.ConstraintGeometry operand in operands) references.Add(GeometryReference(operand));
                    references.Sort(StringComparer.Ordinal);
                    item.Add("class", "GEOMETRIC");
                    item.Add("geometry_refs", ObjectStrings(references));
                    item.Add("is_driving", false);
                    item.Add("expression_name", null);
                    item.Add("value", null);
                    item.Add("journal_id", RequiredJournalId(constraint, "sketch constraint"));
                    item.Add("semantic_key", type.ToString() + "|" + JoinStrings(item["geometry_refs"] as List<object>));
                }
                else
                {
                    throw new M2ModelException("UNSUPPORTED_MODEL", "unsupported sketch constraint class " + constraint.GetType().Name + " for " + type);
                }
                records.Add(item);
            }
        }
        SortRecords(records, "journal_id", "sketch constraint");
        return ToObjectList(records);
    }

    private static List<object> BuildExpressions(Part work)
    {
        Expression[] expressions = work.Expressions.ToArray();
        if (expressions == null) throw new M2ModelException("MODEL_READ_FAILED", "Expressions.ToArray returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Expression expression in expressions)
        {
            if (expression == null) throw new M2ModelException("UNSUPPORTED_MODEL", "expression collection contains null");
            string name = expression.Name;
            if (String.IsNullOrEmpty(name)) throw new M2ModelException("UNSUPPORTED_MODEL", "expression has no name");
            Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
            item.Add("name", name.Normalize(System.Text.NormalizationForm.FormC));
            item.Add("right_hand_side", expression.RightHandSide);
            item.Add("status", expression.Status.ToString());
            string expressionType = expression.Type;
            if (String.Equals(expressionType, "Number", StringComparison.OrdinalIgnoreCase))
                item.Add("value", NumberText(expression.Value));
            else
            {
                item.Add("expression_type", expressionType);
                if (String.Equals(expressionType, "String", StringComparison.OrdinalIgnoreCase)) item.Add("value", expression.StringValue);
                else if (String.Equals(expressionType, "Integer", StringComparison.OrdinalIgnoreCase)) item.Add("value", expression.IntegerValue);
                else if (String.Equals(expressionType, "Boolean", StringComparison.OrdinalIgnoreCase)) item.Add("value", expression.BooleanValue);
                else if (String.Equals(expressionType, "Point", StringComparison.OrdinalIgnoreCase))
                {
                    Point3d p = expression.PointValue;
                    item.Add("value", NumberTextList(new double[] { p.X, p.Y, p.Z }));
                }
                else if (String.Equals(expressionType, "Vector", StringComparison.OrdinalIgnoreCase))
                {
                    Vector3d v = expression.VectorValue;
                    item.Add("value", NumberTextList(new double[] { v.X, v.Y, v.Z }));
                }
                else throw new M2ModelException("UNSUPPORTED_MODEL", "expression has an unsupported native data type");
            }
            item.Add("unit", expression.Units == null ? null : expression.Units.Symbol);
            item.Add("journal_id", expression.JournalIdentifier);
            item.Add("flags", new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "is_measurement_expression", expression.IsMeasurementExpression },
                { "is_geometric_expression", expression.IsGeometricExpression },
                { "is_no_edit", expression.IsNoEdit },
                { "is_no_update", expression.IsNoUpdate },
                { "is_rhs_locked_from_edit", expression.IsRightHandSideLockedFromEdit },
                { "is_user_locked", expression.IsUserLocked },
                { "is_user_specified", expression.IsUserSpecified }
            });
            records.Add(item);
        }
        SortRecords(records, "name", "expression");
        return ToObjectList(records);
    }

    private static List<object> BuildDependencies(Part work)
    {
        Feature[] features = work.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "feature dependency enumeration returned null");
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Feature feature in features)
        {
            string child = RequiredJournalId(feature, "dependency feature");
            Feature[] parents = feature.GetParents();
            Expression[] expressions = feature.GetExpressions();
            if (parents == null || expressions == null) throw new M2ModelException("MODEL_READ_FAILED", "dependency enumeration returned null for " + child);
            foreach (Feature parent in parents)
            {
                Dictionary<string, object> edge = new Dictionary<string, object>(StringComparer.Ordinal);
                edge.Add("kind", "FEATURE_PARENT");
                edge.Add("source_id", RequiredJournalId(parent, "parent feature"));
                edge.Add("target_id", child);
                records.Add(edge);
            }
            foreach (Expression expression in expressions)
            {
                Dictionary<string, object> edge = new Dictionary<string, object>(StringComparer.Ordinal);
                edge.Add("kind", "FEATURE_EXPRESSION");
                edge.Add("source_id", child);
                edge.Add("target_id", expression == null ? null : expression.Name);
                if (expression == null) throw new M2ModelException("UNSUPPORTED_MODEL", "feature expression reference is null for " + child);
                records.Add(edge);
            }
        }
        records.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            int kind = String.CompareOrdinal((string)left["kind"], (string)right["kind"]);
            if (kind != 0) return kind;
            int source = String.CompareOrdinal((string)left["source_id"], (string)right["source_id"]);
            return source != 0 ? source : String.CompareOrdinal((string)left["target_id"], (string)right["target_id"]);
        });
        return ToObjectList(records);
    }

    private static Dictionary<string, object> BuildSupportSummary(Part work)
    {
        List<object> reasons = new List<object>();
        Feature[] features = work.Features.GetFeatures();
        if (features == null) throw new M2ModelException("MODEL_READ_FAILED", "support feature enumeration returned null");
        foreach (Feature feature in features)
        {
            if (feature == null) throw new M2ModelException("UNSUPPORTED_MODEL", "support feature collection contains null");
            string type = feature.FeatureType ?? String.Empty;
            if (!String.Equals(type, "SKETCH", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(type, "DATUM_CSYS", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(type, "EXTRUDE", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(type, "SWP104", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> reason = new Dictionary<string, object>(StringComparer.Ordinal);
                reason.Add("feature_id", RequiredJournalId(feature, "feature"));
                reason.Add("feature_type", type);
                reason.Add("code", "FEATURE_PARAMETERS_UNSUPPORTED");
                reason.Add("message", M7HasNativeDefinition(feature)
                    ? "Native helical definition readback is available; parameter editing remains limited to supported Driving sketch dimensions."
                    : "Generic feature metadata and dependency links are available; feature-specific parameter semantics are not implemented for this feature type.");
                reasons.Add(reason);
            }
        }
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("status", reasons.Count == 0 ? "SUPPORTED" : "PARTIALLY_SUPPORTED");
        result.Add("unsupported", reasons);
        return result;
    }

    private static Dictionary<string, object> ParameterSupport(string featureType)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        bool generic = String.Equals(featureType, "SKETCH", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(featureType, "DATUM_CSYS", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(featureType, "EXTRUDE", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(featureType, "SWP104", StringComparison.OrdinalIgnoreCase);
        result.Add("status", generic ? "SUPPORTED_METADATA" : "UNSUPPORTED_PARAMETERS");
        result.Add("reason", generic
            ? "Feature identity, status, parent/child links, created bodies and created expressions are available; generic parameter access is not declared."
            : "Feature identity and dependency metadata are available; feature-specific parameters are not implemented for this type.");
        return result;
    }

    private static Dictionary<string, object> BuildIdentity(M2ModelBinding binding, Part work,
        Dictionary<string, object> readEffects)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("nx_build", binding.NxBuild ?? "UNKNOWN");
        result.Add("nx_build_source", binding.NxBuildSource);
        result.Add("nxopen_assembly_version", typeof(Session).Assembly.GetName().Version.ToString());
        result.Add("nxopen_file_version", GetNxOpenFileVersion());
        result.Add("process_id", Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
        result.Add("session_epoch", binding.SessionEpoch);
        result.Add("load_instance_id", binding.LoadInstanceId);
        result.Add("binding_epoch", binding.BindingEpoch);
        result.Add("part_path", work.FullPath);
        result.Add("part_tag", work.Tag.ToString());
        result.Add("part_units", work.PartUnits.ToString());
        result.Add("part_load_state", work.PartLoadState.ToString());
        result.Add("is_read_only", IsPartReadOnly(work));
        result.Add("is_modified", work.IsModified);
        result.Add("read_may_mark_modified", true);
        result.Add("artifact_scope", binding.ArtifactScope);
        result.Add("artifact_kind", binding.ArtifactKind);
        result.Add("support_summary", Clone(binding.ArtifactAssessment));
        if (readEffects != null)
        {
            result.Add("is_modified_before_read", readEffects["is_modified_before_read"]);
            result.Add("is_modified_after_read", readEffects["is_modified_after_read"]);
            result.Add("dirty_changed_by_read", readEffects["dirty_changed_by_read"]);
        }
        return result;
    }

    private static Dictionary<string, object> SupportStatus(string status, string reason)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        result.Add("status", status);
        result.Add("reason", reason);
        return result;
    }

    private static BindingState RequireState(M2ModelBinding binding)
    {
        BindingState state = binding == null ? null : binding.AdapterState as BindingState;
        if (state == null) throw new M2ModelException("STALE_BINDING", "NX model binding has been released");
        return state;
    }

    private static string GetNxBuild(Session session)
    {
        try { return session.GetEnvironmentVariableValue("NX_FULL_VERSION"); }
        catch { return null; }
    }

    private static string GetNxOpenFileVersion()
    {
        try { return FileVersionInfo.GetVersionInfo(typeof(Session).Assembly.Location).ProductVersion ?? String.Empty; }
        catch { return String.Empty; }
    }

    private static string CanonicalPath(string path)
    {
        if (String.IsNullOrWhiteSpace(path)) throw new M2ModelException("UNSUPPORTED_ARTIFACT_SCOPE", "part path is empty");
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) { throw new M2ModelException("UNSUPPORTED_ARTIFACT_SCOPE", "part path cannot be canonicalized (" + ex.GetType().Name + ")"); }
    }

    private static string CanonicalPartIdentityPath(string path)
    {
        if (String.IsNullOrEmpty(path)) return String.Empty;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim(); }
    }

    private static bool PartIdentityPathMatches(string actualPath, string expectedPath)
    {
        return String.Equals(CanonicalPartIdentityPath(actualPath), CanonicalPartIdentityPath(expectedPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPartReadOnly(BasePart part)
    {
        return part == null || part.IsReadOnly;
    }

    private static string RequiredJournalId(NXObject value, string subject)
    {
        if (value == null) throw new M2ModelException("UNSUPPORTED_MODEL", subject + " is null");
        string id = value.JournalIdentifier;
        if (String.IsNullOrEmpty(id)) throw new M2ModelException("UNSUPPORTED_MODEL", subject + " has no JournalIdentifier");
        return id;
    }

    private static List<string> GetJournalIds(NXObject[] values, string subject)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", subject + " enumeration returned null");
        List<string> ids = new List<string>();
        foreach (NXObject value in values) ids.Add(RequiredJournalId(value, subject));
        ids.Sort(StringComparer.Ordinal);
        RejectDuplicates(ids, subject);
        return ids;
    }

    private static List<string> GetJournalIds(Feature[] values, string subject)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", subject + " enumeration returned null");
        List<string> ids = new List<string>();
        foreach (Feature value in values) ids.Add(RequiredJournalId(value, subject));
        ids.Sort(StringComparer.Ordinal);
        RejectDuplicates(ids, subject);
        return ids;
    }

    private static List<string> GetJournalIds(Body[] values, string subject)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", subject + " enumeration returned null");
        List<string> ids = new List<string>();
        foreach (Body value in values) ids.Add(RequiredJournalId(value, subject));
        ids.Sort(StringComparer.Ordinal);
        RejectDuplicates(ids, subject);
        return ids;
    }

    private static List<object> BuildBodyFaceReferences(Part work, Body body, string bodyId,
        string bodyTag, bool bodyIsSolid, Face[] faces, out List<string> faceJournalIds)
    {
        if (faces == null) throw new M2ModelException("MODEL_READ_FAILED", "body face enumeration returned null");
        faceJournalIds = new List<string>();
        Dictionary<string, List<int>> indicesByTag = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        for (int i = 0; i < faces.Length; i++)
        {
            Face face = faces[i];
            string faceId = RequiredJournalId(face, "face");
            string faceTagError;
            string faceTag = SafeNxTagText(face, out faceTagError);
            if (String.IsNullOrEmpty(faceTag))
                throw new M2ModelException("UNSUPPORTED_MODEL", "face has no stable session Tag" +
                    (faceTagError == null ? String.Empty : " (" + faceTagError + ")"));

            faceJournalIds.Add(faceId);
            List<int> indices;
            if (!indicesByTag.TryGetValue(faceTag, out indices))
            {
                indices = new List<int>();
                indicesByTag.Add(faceTag, indices);
            }
            indices.Add(i);

            Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
            record.Add("face_journal_id", faceId);
            record.Add("face_tag", faceTag);
            record.Add("body_id", bodyId);
            record.Add("body_tag", bodyTag);
            records.Add(record);
        }

        List<string> duplicateTags = new List<string>();
        foreach (KeyValuePair<string, List<int>> item in indicesByTag)
            if (item.Value.Count > 1) duplicateTags.Add(item.Key);
        if (duplicateTags.Count > 0)
        {
            duplicateTags.Sort(StringComparer.Ordinal);
            Dictionary<string, object> diagnostics = CaptureDuplicateFaceTagDiagnostics(
                work, bodyId, bodyTag, bodyIsSolid, faces, indicesByTag, duplicateTags);
            throw new M2ModelException("UNSUPPORTED_MODEL",
                "duplicate face session Tag " + duplicateTags[0], diagnostics);
        }

        faceJournalIds.Sort(StringComparer.Ordinal);
        records.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            int journalId = String.CompareOrdinal((string)left["face_journal_id"], (string)right["face_journal_id"]);
            return journalId != 0 ? journalId : String.CompareOrdinal((string)left["face_tag"], (string)right["face_tag"]);
        });
        return ToObjectList(records);
    }

    private static Dictionary<string, object> CaptureDuplicateFaceTagDiagnostics(Part work,
        string bodyId, string bodyTag, bool bodyIsSolid, Face[] faces,
        Dictionary<string, List<int>> indicesByTag, List<string> duplicateTags)
    {
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        evidence.Add("kind", "DUPLICATE_FACE_SESSION_TAGS");
        evidence.Add("body_tag", bodyTag);
        evidence.Add("body_journal_id", bodyId);
        evidence.Add("body_is_solid", bodyIsSolid);
        evidence.Add("face_array_count", faces == null ? 0 : faces.Length);
        evidence.Add("face_array_index_base", 0);
        List<object> groups = new List<object>();
        foreach (string duplicateTag in duplicateTags)
        {
            Dictionary<string, object> group = new Dictionary<string, object>(StringComparer.Ordinal);
            group.Add("face_tag", duplicateTag);
            List<object> members = new List<object>();
            foreach (int index in indicesByTag[duplicateTag])
            {
                Face face = faces[index];
                Dictionary<string, object> member = new Dictionary<string, object>(StringComparer.Ordinal);
                member.Add("array_index", index);
                member.Add("face_journal_id", RequiredJournalId(face, "face"));
                member.Add("face_tag", duplicateTag);
                CaptureFaceFeatureReferences(work, face, bodyIsSolid, member);
                members.Add(member);
            }
            group.Add("members", members);
            groups.Add(group);
        }
        evidence.Add("duplicate_groups", groups);
        return evidence;
    }

    private static void CaptureFaceFeatureReferences(Part work, Face face, bool containingBodyIsSolid,
        Dictionary<string, object> member)
    {
        List<object> references = new List<object>();
        member.Add("face_feature_ref_source", "UFModl.AskFaceFeats");
        member.Add("face_feature_ref_scope", "DIAGNOSTIC_ONLY");
        member.Add("face_feature_ref_semantics", "UF-returned feature Tags; not a unique-owner or persistence identity claim");
        member.Add("face_feature_refs", references);
        if (!containingBodyIsSolid)
        {
            member.Add("face_feature_read_status", "UNSUPPORTED_SHEET_BODY");
            return;
        }
        Tag[] featureTags;
        try
        {
            if (face == null) throw new InvalidOperationException("face object is null");
            UFSession.GetUFSession().Modl.AskFaceFeats(face.Tag, out featureTags);
        }
        catch (Exception ex)
        {
            member.Add("face_feature_read_status", "FAILED");
            member.Add("face_feature_read_error", ex.GetType().Name);
            return;
        }
        if (featureTags == null)
        {
            member.Add("face_feature_read_status", "NULL_RESULT");
            return;
        }
        member.Add("face_feature_read_status", "VERIFIED");
        member.Add("face_feature_ref_count", featureTags.Length);

        Feature[] features = null;
        string featureEnumerationError = null;
        try
        {
            if (work == null) throw new InvalidOperationException("Work Part is null");
            features = work.Features.GetFeatures();
            if (features == null) featureEnumerationError = "NULL_RESULT";
        }
        catch (Exception ex) { featureEnumerationError = ex.GetType().Name; }
        if (featureEnumerationError != null)
            member.Add("feature_journal_id_resolution_error", featureEnumerationError);

        foreach (Tag featureTag in featureTags)
        {
            Dictionary<string, object> reference = new Dictionary<string, object>(StringComparer.Ordinal);
            reference.Add("feature_tag", featureTag.ToString());
            reference.Add("feature_tag_source", "UFModl.AskFaceFeats");
            reference.Add("feature_journal_id_resolution", "CURRENT_WORK_PART_FEATURE_TAG_MATCH");
            reference.Add("feature_journal_id", null);
            if (features == null)
            {
                reference.Add("feature_journal_id_status", featureEnumerationError == "NULL_RESULT"
                    ? "NOT_RESOLVED" : "FAILED");
            }
            else
            {
                Feature matched = null;
                string tagResolutionError = null;
                foreach (Feature feature in features)
                {
                    if (feature == null) continue;
                    try
                    {
                        if (feature.Tag.Equals(featureTag)) { matched = feature; break; }
                    }
                    catch (Exception ex) { tagResolutionError = ex.GetType().Name; }
                }
                if (matched == null)
                {
                    reference.Add("feature_journal_id_status", tagResolutionError == null
                        ? "NOT_FOUND" : "FAILED");
                    if (tagResolutionError != null)
                        reference.Add("feature_tag_resolution_error", tagResolutionError);
                }
                else
                {
                    try
                    {
                        string featureId = matched.JournalIdentifier;
                        reference["feature_journal_id"] = String.IsNullOrEmpty(featureId) ? null : (object)featureId;
                        reference.Add("feature_journal_id_status", String.IsNullOrEmpty(featureId)
                            ? "EMPTY" : "VERIFIED");
                    }
                    catch (Exception ex)
                    {
                        reference.Add("feature_journal_id_status", "FAILED");
                        reference.Add("feature_journal_id_read_error", ex.GetType().Name);
                    }
                }
            }
            references.Add(reference);
        }
    }

    private static string SafeNxTagText(NXObject value, out string readError)
    {
        readError = null;
        if (value == null)
        {
            readError = "NULL_OBJECT";
            return null;
        }
        try { return value.Tag.ToString(); }
        catch (Exception ex)
        {
            readError = ex.GetType().Name;
            return null;
        }
    }

    private static List<string> GetJournalIds(Edge[] values, string subject)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", subject + " enumeration returned null");
        List<string> ids = new List<string>();
        foreach (Edge value in values) ids.Add(RequiredJournalId(value, subject));
        ids.Sort(StringComparer.Ordinal);
        RejectDuplicates(ids, subject);
        return ids;
    }

    private static List<string> GetExpressionIds(Expression[] values)
    {
        if (values == null) throw new M2ModelException("MODEL_READ_FAILED", "feature expression enumeration returned null");
        List<string> ids = new List<string>();
        foreach (Expression value in values)
        {
            if (value == null || String.IsNullOrEmpty(value.Name)) throw new M2ModelException("UNSUPPORTED_MODEL", "feature has an expression without a stable name");
            ids.Add(value.Name);
        }
        ids.Sort(StringComparer.Ordinal);
        RejectDuplicates(ids, "feature expression");
        return ids;
    }

    private static void RejectDuplicates(List<string> values, string subject)
    {
        for (int i = 1; i < values.Count; i++)
            if (String.Equals(values[i - 1], values[i], StringComparison.Ordinal))
                throw new M2ModelException("UNSUPPORTED_MODEL", "duplicate " + subject + " identity " + values[i]);
    }

    private static List<object> ObjectStrings(List<string> values)
    {
        List<object> result = new List<object>();
        foreach (string value in values) result.Add(value);
        return result;
    }

    private static List<object> ToObjectList(List<Dictionary<string, object>> values)
    {
        List<object> result = new List<object>();
        foreach (Dictionary<string, object> value in values) result.Add(value);
        return result;
    }

    private static void SortRecords(List<Dictionary<string, object>> records, string key, string subject)
    {
        records.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            return String.CompareOrdinal(Convert.ToString(left[key], CultureInfo.InvariantCulture),
                Convert.ToString(right[key], CultureInfo.InvariantCulture));
        });
        for (int i = 1; i < records.Count; i++)
            if (String.Equals(Convert.ToString(records[i - 1][key], CultureInfo.InvariantCulture),
                Convert.ToString(records[i][key], CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new M2ModelException("UNSUPPORTED_MODEL", "duplicate " + subject + " identity " + Convert.ToString(records[i][key], CultureInfo.InvariantCulture));
    }

    private static string JoinStrings(List<object> values)
    {
        if (values == null || values.Count == 0) return String.Empty;
        List<string> strings = new List<string>();
        foreach (object value in values) strings.Add(Convert.ToString(value, CultureInfo.InvariantCulture));
        return String.Join("|", strings.ToArray());
    }

    private static string GeometryReference(Sketch.ConstraintGeometry geometry)
    {
        if (geometry.Geometry == null) throw new M2ModelException("UNSUPPORTED_MODEL", "constraint geometry operand is null");
        return RequiredJournalId(geometry.Geometry, "constraint geometry") + ":" + geometry.PointType.ToString();
    }

    private static string DimensionReference(Sketch.DimensionGeometry geometry)
    {
        if (geometry.Geometry == null) throw new M2ModelException("UNSUPPORTED_MODEL", "dimension geometry operand is null");
        return RequiredJournalId(geometry.Geometry, "dimension geometry") + ":" + geometry.AssocType.ToString();
    }

    private static List<object> Point(Point3d point)
    {
        List<object> result = new List<object>();
        result.Add(NumberText(point.X));
        result.Add(NumberText(point.Y));
        result.Add(NumberText(point.Z));
        return result;
    }

    private static List<object> Vector(double x, double y, double z)
    {
        List<object> result = new List<object>();
        result.Add(NumberText(x));
        result.Add(NumberText(y));
        result.Add(NumberText(z));
        return result;
    }

    private static string NumberText(double value)
    {
        if (Double.IsNaN(value) || Double.IsInfinity(value))
            throw new M2ModelException("UNSUPPORTED_MODEL", "NX returned a non-finite numeric value");
        if (value == 0.0) return "0";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, object> Clone(Dictionary<string, object> source)
    {
        Dictionary<string, object> copy = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object> entry in source) copy.Add(entry.Key, entry.Value);
        return copy;
    }
}
