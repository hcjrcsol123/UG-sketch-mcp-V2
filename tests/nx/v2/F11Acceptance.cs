// F11 native acceptance harness. Compile offline and invoke only in the
// root-operated, already-open NX session. This code never launches or closes NX.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NXOpen;
using NXOpen.Annotations;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

public static class F11Acceptance
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    private const double PositionToleranceMm = 1.0e-6;
    private const double AngularToleranceRad = 1.0e-7;
    private static readonly string[] ExpressionFlagNames = new string[]
    {
        "IsMeasurementExpression", "IsGeometricExpression", "IsNoEdit", "IsNoUpdate",
        "IsRightHandSideLockedFromEdit", "IsUserLocked", "IsUserSpecified"
    };

    private sealed class Fixture
    {
        internal Sketch Sketch;
        internal Line DrivingLine;
        internal Line VerticalLine;
        internal Line WitnessLine;
        internal Arc PartialArc;
        internal Arc FullCircle;
        internal Ellipse UnsupportedEllipse;
        internal SketchDimensionalConstraint HorizontalDimension;
        internal SketchDimensionalConstraint VerticalDimension;
        internal SketchDimensionalConstraint RadialDimension;
    }

    private sealed class Context
    {
        internal Session Session;
        internal Part Part;
        internal M2OpenModelAdapter Adapter;
        internal IM3SketchReferenceConversionOperationAdapter ReferenceAdapter;
        internal M2ModelBinding Binding;
        internal Fixture Fixture;
        internal string RunId;
        internal string SourcePath;
        internal string CopyPath;
        internal bool WritesHalted;
        internal Dictionary<string, object> Report;
        internal List<object> Cases;
        internal List<object> Defects;
    }

    private delegate void CaseAction(Context context, Dictionary<string, object> row);

    private sealed class UnknownOutcomeException : Exception
    {
        internal UnknownOutcomeException(string message) : base(message) { }
    }

    private sealed class SkipCaseException : Exception
    {
        internal SkipCaseException(string message) : base(message) { }
    }

    public static void Main(string[] args)
    {
        string runId = Guid.NewGuid().ToString("N");
        string root = Path.Combine(ProjectRoot, "workspace", "v2");
        string modelDirectory = Path.Combine(root, "models");
        string evidenceDirectory = Path.Combine(root, "evidence");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        string sourcePath = Path.Combine(modelDirectory, "F11-acceptance-" + runId + ".prt");
        string copyPath = Path.Combine(modelDirectory, "F11-acceptance-" + runId + "-roundtrip.prt");
        string reportPath = Path.Combine(evidenceDirectory, "F11-acceptance-" + runId + ".json");
        RequireNewPath(sourcePath, "isolated model");
        RequireNewPath(copyPath, "Save Copy model");
        RequireNewPath(reportPath, "report");

        List<object> cases = new List<object>();
        List<object> defects = new List<object>();
        Dictionary<string, object> report = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test", "F11 native sketch reference-state acceptance" },
            { "run_id", runId }, { "started_utc", UtcNow() }, { "passed", false },
            { "acceptance_status", "NOT_RUN" }, { "layer", "native_subsystem_acceptance" },
            { "cases", cases }, { "defects", defects },
            { "source_model_path", sourcePath }, { "save_copy_path", copyPath },
            { "report_path", reportPath }, { "sample_size", 1 }, { "units", "millimeter" },
            { "position_tolerance_mm", PositionToleranceMm },
            { "angular_tolerance_rad", AngularToleranceRad },
            { "measurement_uncertainty", "No physical measuring instrument is used. CAD tolerances are comparison limits, not metrology uncertainty." },
            { "calibration", "Not applicable: direct NXOpen/UF model readback only." },
            { "solver_interpretation", "Capture Sketch.GetStatus enum and numeric DOF as direct readback. GetStatus may be cached; controlled behavior uses Sketch.Activate + CalculateStatus + direct geometry/dimension reads. A NotEvaluated enum or DOF value alone is not a solver pass." },
            { "computed_size_policy", "Read AssociatedDimension.ComputedSize only through a direct NX property query. Record query errors/unavailability and make no computed-size claim when the direct query fails." },
            { "unknown_gate_policy", "If any production operation returns UNKNOWN or rollback cannot be proven, stop all subsequent model writes, do not replay the operation, retain the model and evidence, and mark dependent cases skipped." },
            { "unknown_gate_owner", "This native harness calls the production M2OpenModelAdapter directly; the M2Bridge write gate is not directly exercised here. Record its behavior as NOT_EXERCISED and report the adapter/harness write-stop behavior separately." },
            { "prior_api_probe_context", "Root-owned NX2506 API probe e739df97a84c41d69f39067132d88b95 observed UF curve Reference=0/Active=1; dimension Reference=1/Driving=0; SetAllowRefCrvs(false) with a whole-Sketch CurveFeatureRule excluded reference curves; expression flags remained false; AssociatedDimension.ComputedSize reported 10, then 14 for a reference dimension after endpoint movement. Probe data is context only, not harness acceptance evidence." },
            { "direct_inventory_scope", "Harness direct snapshots include Part.Curves, Sketches and GetAllConstraintsOfType(Any, NoCon), feature history order/relations, Bodies, and Expressions. Point, Axis and Direction helper inventories are outside this harness snapshot; no preservation claim is made for those categories. This isolated F11 fixture has no solid Bodies; an empty body inventory is captured directly, so no body volume or bounding-box claim applies." },
            { "traceability", "docs/V2.md F11; tests/nx/v2/F11Acceptance.md F11-N01 through F11-N12; finalized M3SketchReferenceConversionRequest contract." },
            { "configuration", new Dictionary<string, object>(StringComparer.Ordinal)
                { { "required_nx_release", "NX2506" }, { "adapter_api", "IM3SketchReferenceConversionOperationAdapter" }, { "part_units", "Millimeters" } } },
            { "test_layer_counts", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "offline_protocol", EmptyCounts() }, { "sta_fake_lifecycle", EmptyCounts() },
                    { "native_subsystem_acceptance", EmptyCounts() }, { "installed_mcp_smoke", EmptyCounts() }
                } },
            { "nx_started_by_harness", false }, { "nx_restarted_by_harness", false },
            { "nx_closed_by_harness", false }, { "existing_user_model_modified_by_harness", false },
            { "calibration_required", false }
        };
        Context context = new Context
        {
            RunId = runId, SourcePath = sourcePath, CopyPath = copyPath,
            Report = report, Cases = cases, Defects = defects
        };
        Persist(reportPath, report);

        try
        {
            Setup(context);
            RecordConfiguration(context);
            RunCurveDirectionCase(context, "F11-N01-line_bidirectional", "Line", context.Fixture.DrivingLine);
            RunCurveDirectionCase(context, "F11-N01-partial_arc_bidirectional", "Arc", context.Fixture.PartialArc);
            RunCurveDirectionCase(context, "F11-N01-full_circle_bidirectional", "full_circle_Arc", context.Fixture.FullCircle);
            RunUnsupportedGeometryCase(context);
            RunMixedAndRejectCases(context);
            RunSectionEligibilityCase(context);
            RunUnsupportedDimensionCase(context);
            RunDrivingDimensionBehaviorCase(context);
            RunVerticalDimensionCase(context);
            RunCprojSiblingCase(context);
            RunProductUndoFaultCase(context);
            RunOverconstraintCase(context);
            RunSaveCopyCase(context);
        }
        catch (UnknownOutcomeException ex)
        {
            context.WritesHalted = true;
            report["fatal_unknown_outcome"] = ExceptionRecord(ex);
            AddSkippedCasesAfterUnknown(context, "A prior native outcome was UNKNOWN; writes are halted and no case may replay or continue.");
        }
        catch (Exception ex)
        {
            Dictionary<string, object> defect = DefectRecord("F11-SETUP", ex,
                "Create and save a unique millimetre part, call production Takeover, construct the fixture, and record direct NX/UF state.",
                "Preserve this report/model; resolve the fixture or adapter error, then rerun the native matrix in a fresh idle NX2506 session with new unique paths.");
            defects.Add(defect);
            report["setup_or_runner_failure"] = defect;
            if (context.WritesHalted) AddSkippedCasesAfterUnknown(context, "A native state could not be proven; no further model writes are permitted.");
        }
        finally
        {
            try
            {
                if (context.Adapter != null && context.Binding != null && !context.WritesHalted)
                    context.Adapter.Release(context.Binding);
            }
            catch (Exception ex) { report["release_error"] = ExceptionRecord(ex); }
            report["completed_utc"] = UtcNow();
            report["writes_halted"] = context.WritesHalted;
            report["native_subsystem_case_counts"] = CaseCounts(cases);
            report["case_counts_by_id"] = CaseCountsById(cases);
            report["passed"] = IsCompletePass(cases, defects, context.WritesHalted);
            report["acceptance_status"] = context.WritesHalted ? "UNKNOWN_GATE_STOPPED" :
                (Object.Equals(report["passed"], true) ? "PASS" : "FAIL_OR_SKIPPED");
            Persist(reportPath, report);
        }
    }

    private static void Setup(Context context)
    {
        Session session = Session.GetSession();
        Require(session != null, "NX Session.GetSession returned null.");
        Part previousPart = session.Parts.Work;
        context.Report["prior_work_part"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "path", previousPart == null ? null : previousPart.FullPath },
            { "tag", previousPart == null ? null : previousPart.Tag.ToString() },
            { "modified_before", previousPart != null && previousPart.IsModified }
        };
        Require(!session.IsInTaskEnvironment && session.ActiveSketch == null,
            "Acceptance requires an idle NX session with no active Sketch or task environment.");
        Require(previousPart == null || !previousPart.IsModified,
            "The previous Work Part must be saved and unmodified; the harness leaves it untouched.");

        Part part = session.Parts.NewDisplay(context.SourcePath, Part.Units.Millimeters);
        Require(part != null && part.PartUnits == BasePart.Units.Millimeters,
            "NX did not create the unique millimetre part.");
        SavePart(part, "empty F11 isolated part before production takeover");

        M2OpenModelAdapter adapter = new M2OpenModelAdapter();
        M2ModelBinding binding = adapter.Takeover(context.SourcePath, "saved_independent_copy",
            "f11-acceptance-session-" + context.RunId, "f11-acceptance-load-" + context.RunId, 1);
        IM3SketchReferenceConversionOperationAdapter referenceAdapter = (object)adapter as IM3SketchReferenceConversionOperationAdapter;
        Require(referenceAdapter != null,
            "The production M2OpenModelAdapter does not expose IM3SketchReferenceConversionOperationAdapter.");
        Require(adapter is IM3OperationAdapter,
            "The production adapter does not expose the base IM3 ExecutePlan lifecycle needed by F11.");

        context.Session = session;
        context.Part = part;
        context.Adapter = adapter;
        context.Binding = binding;
        context.ReferenceAdapter = referenceAdapter;
        Fixture fixture = CreateMainFixture(context);
        context.Fixture = fixture;
        context.Report["fixture"] = FixtureRecord(fixture);
        context.Report["part_tolerances"] = PartToleranceRecord(part);
        SavePart(part, "initial F11 geometry/dimension fixture");
        Dictionary<string, object> sessionInfo = adapter.GetSession(binding);
        context.Report["session"] = sessionInfo;
        context.Report["initial_direct_snapshot"] = NativeSnapshot(context, fixture.Sketch);
        context.Report["initial_binding_revision"] = binding.RevisionId;
        context.Report["assemblies"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "bridge", AssemblyRecord(typeof(M2Json).Assembly) },
            { "adapter", AssemblyRecord(typeof(M2OpenModelAdapter).Assembly) },
            { "nxopen", AssemblyRecord(typeof(Session).Assembly) },
            { "acceptance", AssemblyRecord(Assembly.GetExecutingAssembly()) }
        };
        string release = FirstNonEmpty(GetString(sessionInfo, "nx_build"), GetString(sessionInfo, "full_release_number"), GetString(sessionInfo, "release_number"));
        context.Report["nx_configuration"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "required_release", "NX2506" }, { "recorded_release", release },
            { "release_number", GetString(sessionInfo, "release_number") },
            { "full_release_number", GetString(sessionInfo, "full_release_number") },
            { "nxopen_assembly_version", GetString(sessionInfo, "nxopen_assembly_version") },
            { "ugii_version", GetString(sessionInfo, "ugii_version") },
            { "part_units", part.PartUnits.ToString() }, { "part_path", part.FullPath },
            { "binding_revision_id", binding.RevisionId }, { "binding_artifact_scope", binding.ArtifactScope },
            { "part_tolerances", PartToleranceRecord(part) }
        };
        Require(IsNx2506(release) || IsNx2506(GetString(sessionInfo, "release_number")) || IsNx2506(GetString(sessionInfo, "full_release_number")),
            "The active release was not identified as NX2506; native acceptance is scoped to NX2506.");
        Persist(GetString(context.Report, "report_path"), context.Report);
    }

    private static Fixture CreateMainFixture(Context context)
    {
        M3SketchRequest request = new M3SketchRequest
        {
            RevisionId = context.Binding.RevisionId,
            OriginMm = new double[] { 0.0, 0.0, 0.0 },
            Normal = new double[] { 0.0, 0.0, 1.0 },
            XAxis = new double[] { 1.0, 0.0, 0.0 },
            ConstraintMode = "geometry_only",
            Geometries = new List<M3SketchGeometryRequest>
            {
                new M3SketchGeometryRequest { Id = "drive-line", Type = "line", StartMm = new double[] { 0.0, 0.0 }, EndMm = new double[] { 10.0, 0.0 } },
                new M3SketchGeometryRequest { Id = "vertical-line", Type = "line", StartMm = new double[] { 15.0, 20.0 }, EndMm = new double[] { 15.0, 28.0 } },
                new M3SketchGeometryRequest { Id = "witness-line", Type = "line", StartMm = new double[] { 40.0, 0.0 }, EndMm = new double[] { 43.0, 0.0 } },
                new M3SketchGeometryRequest { Id = "partial-arc", Type = "arc", CenterMm = new double[] { 22.0, 4.0 }, RadiusMm = 4.0, StartAngleDeg = 15.0, SweepDeg = 120.0 },
                new M3SketchGeometryRequest { Id = "full-circle", Type = "circle", CenterMm = new double[] { 32.0, 0.0 }, DiameterMm = 6.0 },
                new M3SketchGeometryRequest { Id = "unsupported-ellipse", Type = "ellipse", CenterMm = new double[] { 52.0, 0.0 }, MajorRadiusMm = 4.0, MinorRadiusMm = 2.0, RotationDeg = 0.0 }
            }
        };
        Dictionary<string, object> requestRecord = SketchRequestRecord(request);
        context.Report["fixture_sketch_request"] = requestRecord;
        IM3OperationAdapter baseAdapter = context.Adapter as IM3OperationAdapter;
        Require(baseAdapter != null, "Base IM3 adapter could not be resolved after capability validation.");
        M3PlanSnapshot plan = baseAdapter.PrepareSketch(context.Binding, request);
        Require(plan != null && plan.PublicView != null, "Production PrepareSketch returned no fixture plan.");
        M3ExecutionResult execution = baseAdapter.ExecutePlan(context.Binding, plan);
        Require(execution != null && String.Equals(execution.State, "APPLIED", StringComparison.Ordinal),
            "Production sketch fixture creation did not return APPLIED.");
        M3VerificationResult verification = baseAdapter.VerifyPlan(context.Binding, plan);
        Require(verification != null && verification.Verified,
            "Production fixture Sketch failed VerifyPlan.");
        string sketchId = GetString(execution.Result, "sketch_id");
        Sketch sketch = FindSketchByJournalId(context.Part, sketchId);
        sketch.Feature.SetName("F11_MAIN_SKETCH");

        Fixture fixture = new Fixture { Sketch = sketch };
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve;
            if (curve == null) continue;
            if (curve is Line)
            {
                Line line = (Line)curve;
                Point3d a = line.StartPoint, b = line.EndPoint;
                if (Near(a.X, 0.0) && Near(a.Y, 0.0) && Near(b.X, 10.0) && Near(b.Y, 0.0)) fixture.DrivingLine = line;
                else if (Near(a.X, 15.0) && Near(a.Y, 20.0) && Near(b.X, 15.0) && Near(b.Y, 28.0)) fixture.VerticalLine = line;
                else if (Near(a.X, 40.0) && Near(a.Y, 0.0) && Near(b.X, 43.0) && Near(b.Y, 0.0)) fixture.WitnessLine = line;
            }
            Arc arc = curve as Arc;
            if (arc != null && Math.Abs(arc.EndAngle - arc.StartAngle) >= 2.0 * Math.PI - AngularToleranceRad)
                fixture.FullCircle = arc;
            else if (arc != null && fixture.PartialArc == null) fixture.PartialArc = arc;
            Ellipse ellipse = curve as Ellipse;
            if (ellipse != null) fixture.UnsupportedEllipse = ellipse;
        }
        Require(fixture.DrivingLine != null && fixture.VerticalLine != null && fixture.WitnessLine != null &&
            fixture.PartialArc != null && fixture.FullCircle != null && fixture.UnsupportedEllipse != null,
            "The native fixture did not contain the requested Lines, partial Arc, full-circle Arc and unsupported Ellipse.");
        fixture.DrivingLine.SetName("F11_DRIVING_LINE");
        fixture.VerticalLine.SetName("F11_VERTICAL_LINE");
        fixture.WitnessLine.SetName("F11_WITNESS_LINE");
        fixture.PartialArc.SetName("F11_PARTIAL_ARC");
        fixture.FullCircle.SetName("F11_FULL_CIRCLE");
        fixture.UnsupportedEllipse.SetName("F11_UNSUPPORTED_ELLIPSE");

        fixture.HorizontalDimension = CreateLinearDimension(context, sketch, fixture.DrivingLine,
            Sketch.ConstraintType.HorizontalDim, 10.0, new Point3d(5.0, 2.0, 0.0), "F11_HORIZONTAL_DIMENSION");
        fixture.VerticalDimension = CreateLinearDimension(context, sketch, fixture.VerticalLine,
            Sketch.ConstraintType.VerticalDim, 8.0, new Point3d(18.0, 24.0, 0.0), "F11_VERTICAL_DIMENSION");
        Sketch.DimensionGeometry arcGeometry = new Sketch.DimensionGeometry
        {
            Geometry = fixture.PartialArc, AssocType = Sketch.AssocType.None, AssocValue = 0
        };
        sketch.Activate(Sketch.ViewReorient.False);
        try
        {
            fixture.RadialDimension = sketch.CreateRadialDimension(arcGeometry,
                new Point3d(25.0, 8.0, 0.0), null, Sketch.DimensionOption.CreateAsReference);
            if (fixture.RadialDimension != null) fixture.RadialDimension.SetName("F11_UNSUPPORTED_RADIAL_DIMENSION");
            sketch.Update();
        }
        finally { DeactivateIfActive(context.Session, sketch); }
        context.Adapter.GetSession(context.Binding);
        return fixture;
    }

    private static SketchDimensionalConstraint CreateLinearDimension(Context context, Sketch sketch,
        Line line, Sketch.ConstraintType type, double value, Point3d placement, string name)
    {
        Session.UndoMarkId mark = context.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F11 fixture dimension " + name);
        bool active = false;
        try
        {
            sketch.Activate(Sketch.ViewReorient.False);
            active = true;
            SketchDimensionalConstraint dimension = sketch.CreateDimension(type,
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.StartPoint, AssocValue = 0 },
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.EndPoint, AssocValue = 0 },
                placement, null, Sketch.DimensionOption.CreateAsDriving);
            Require(dimension != null && dimension.AssociatedExpression != null,
                "NX did not create a linear Driving dimension for " + name + ".");
            dimension.SetName(name);
            dimension.AssociatedExpression.RightHandSide = NumberText(value);
            sketch.Update();
            Dictionary<string, object> fixtureSolver = ApplyWholeSketchRegionAndCalculate(context, sketch);
            context.Report["dimension_fixture_solver"] = fixtureSolver;
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            active = false;
            Require(context.Session.UpdateManager.DoUpdate(mark) == 0,
                "NX update reported errors while creating dimension " + name + ".");
            Require(dimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving,
                "Fixture dimension was not Driving: " + name);
            return dimension;
        }
        finally
        {
            if (active && context.Session.ActiveSketch != null && context.Session.ActiveSketch.Tag.Equals(sketch.Tag))
                sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            try { context.Session.DeleteUndoMark(mark, null); } catch { }
        }
    }

    private static void RecordConfiguration(Context context)
    {
        Dictionary<string, object> cfg = context.Report["nx_configuration"] as Dictionary<string, object>;
        if (cfg == null) return;
        cfg["source_part_path"] = context.Part.FullPath;
        cfg["part_modified_before_native_cases"] = context.Part.IsModified;
        cfg["selected_sketch_tag"] = context.Fixture.Sketch.Tag.ToString();
        cfg["selected_sketch_journal_id"] = context.Fixture.Sketch.JournalIdentifier;
        cfg["sketch_feature_tag"] = context.Fixture.Sketch.Feature == null ? null : context.Fixture.Sketch.Feature.Tag.ToString();
        cfg["sketch_feature_journal_id"] = context.Fixture.Sketch.Feature == null ? null : context.Fixture.Sketch.Feature.JournalIdentifier;
        cfg["adapter_capability"] = context.ReferenceAdapter.GetType().FullName;
        cfg["binding_revision_id"] = context.Binding.RevisionId;
        cfg["calibration_needed"] = false;
    }

    private static void RunCurveDirectionCase(Context context, string testId, string type, Curve curve)
    {
        RunCase(context, testId, "Verify explicit " + type + " conversion in both Reference and Active directions.",
            "Record complete direct state; use a harness-owned Undo mark; call the production F11 adapter once for Reference and once for Active by the exact current JID; direct-read UF reference status and geometry after each; undo the harness mark and prove restoration.",
            "The selected Tag alone changes to Reference=0 then Active=1; geometry, dimensions, expressions, unselected curve states, feature history, bodies, active Sketch, and all unrelated inventories remain unchanged. Record native builder enum/selection/commit evidence and the solver enum/numeric DOF.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                Fixture fixture = ctx.Fixture;
                Dictionary<string, object> before = NativeSnapshot(ctx, fixture.Sketch);
                bool dirtyBefore = ctx.Part.IsModified;
                string tag = curve.Tag.ToString();
                row["selected_object"] = CurveRecord(curve);
                row["native_before"] = before;
                row["part_modified_before"] = dirtyBefore;
                Session.UndoMarkId mark = ctx.Session.SetUndoMark(Session.MarkVisibility.Invisible, testId + " harness restore");
                bool restored = false;
                try
                {
                    Dictionary<string, object> toReference = ExecuteConversion(ctx, fixture.Sketch,
                        new string[] { curve.JournalIdentifier }, "reference", false, row, "active_to_reference");
                    Require(String.Equals(ReadCurveReferenceStatus(fixture.Sketch, tag), "Reference", StringComparison.Ordinal),
                        type + " did not read back as Reference through UF AskReferenceStatus.");
                    Require(SameExceptTargetState(before, NativeSnapshot(ctx, fixture.Sketch),
                        new HashSet<string>(new string[] { "curve:" + tag }, StringComparer.Ordinal)),
                        "Reference conversion changed geometry, dimensions, expressions, constraints, history, bodies, or unrelated model state.");
                    row["after_reference"] = NativeSnapshot(ctx, fixture.Sketch);
                    row["reference_operation"] = toReference;

                    Dictionary<string, object> toActive = ExecuteConversion(ctx, fixture.Sketch,
                        new string[] { curve.JournalIdentifier }, "active", false, row, "reference_to_active");
                    Require(String.Equals(ReadCurveReferenceStatus(fixture.Sketch, tag), "Active", StringComparison.Ordinal),
                        type + " did not read back as Active through UF AskReferenceStatus.");
                    Require(SameExceptTargetState(before, NativeSnapshot(ctx, fixture.Sketch),
                        new HashSet<string>(new string[] { "curve:" + tag }, StringComparer.Ordinal)),
                        "Active conversion changed geometry, dimensions, expressions, constraints, history, bodies, or unrelated model state.");
                    row["after_active"] = NativeSnapshot(ctx, fixture.Sketch);
                    row["active_operation"] = toActive;
                }
                finally
                {
                    if (!ctx.WritesHalted)
                        restored = RestoreHarnessUndo(ctx, mark, before, dirtyBefore, row, "bidirectional " + type);
                    else row["undo_skipped_due_to_unknown"] = true;
                }
                Require(restored || ctx.WritesHalted,
                    "Harness Undo did not restore the complete pre-case direct snapshot after " + type + " conversions.");
                if (!ctx.WritesHalted) RefreshBinding(ctx, row, "after harness Undo");
                row["passed"] = !ctx.WritesHalted;
            });
    }

    private static void RunMixedAndRejectCases(Context context)
    {
        RunCase(context, "F11-N11-mixed_target_states_both_directions", "Mixed already-target and to-be-converted selections are accepted in both target directions.",
            "Within a harness Undo mark, make Line reference; request Reference for that already-target Line plus the active partial Arc; make Line active; request Active for that already-target Line plus the reference Arc; directly read every curve status and compare all other inventories; undo the fixture.",
            "Each mixed request is accepted, the changed object reaches the target state, an already-target object remains a valid no-op, no unselected or sibling object changes, and product native input evidence includes only objects needing conversion where supplied.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                Fixture f = ctx.Fixture;
                Dictionary<string, object> before = NativeSnapshot(ctx, f.Sketch);
                bool dirty = ctx.Part.IsModified;
                Session.UndoMarkId mark = ctx.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F11 mixed-state restore");
                bool restored = false;
                try
                {
                    ExecuteConversion(ctx, f.Sketch, new string[] { f.DrivingLine.JournalIdentifier }, "reference", false, row, "setup_line_reference");
                    Dictionary<string, object> afterSetup = NativeSnapshot(ctx, f.Sketch);
                    Dictionary<string, object> first = ExecuteConversion(ctx, f.Sketch,
                        new string[] { f.DrivingLine.JournalIdentifier, f.PartialArc.JournalIdentifier }, "reference", false, row, "mixed_to_reference");
                    Require(ReadCurveReferenceStatus(f.Sketch, f.DrivingLine.Tag.ToString()) == "Reference" &&
                        ReadCurveReferenceStatus(f.Sketch, f.PartialArc.Tag.ToString()) == "Reference",
                        "Mixed Reference request did not leave both selected objects Reference.");
                    row["request_1_input_native_evidence"] = FindNestedValue(first, "native_input_tags");
                    row["native_after_mixed_reference"] = NativeSnapshot(ctx, f.Sketch);

                    ExecuteConversion(ctx, f.Sketch, new string[] { f.DrivingLine.JournalIdentifier }, "active", false, row, "setup_line_active");
                    Dictionary<string, object> second = ExecuteConversion(ctx, f.Sketch,
                        new string[] { f.DrivingLine.JournalIdentifier, f.PartialArc.JournalIdentifier }, "active", false, row, "mixed_to_active");
                    Require(ReadCurveReferenceStatus(f.Sketch, f.DrivingLine.Tag.ToString()) == "Active" &&
                        ReadCurveReferenceStatus(f.Sketch, f.PartialArc.Tag.ToString()) == "Active",
                        "Mixed Active request did not leave both selected objects Active.");
                    row["request_2_input_native_evidence"] = FindNestedValue(second, "native_input_tags");
                    row["native_after_mixed_active"] = NativeSnapshot(ctx, f.Sketch);
                    row["setup_snapshot_line_reference"] = afterSetup;
                }
                finally
                {
                    if (!ctx.WritesHalted) restored = RestoreHarnessUndo(ctx, mark, before, dirty, row, "mixed-state cases");
                    else row["undo_skipped_due_to_unknown"] = true;
                }
                Require(restored || ctx.WritesHalted, "Mixed-state harness Undo did not restore its pre-case snapshot.");
                if (!ctx.WritesHalted) RefreshBinding(ctx, row, "after mixed-state Undo");
                row["passed"] = !ctx.WritesHalted;
            });

        RunRejectedPrepareCase(context, "F11-N05-all_target_state_rejected", "All selected objects already in the requested target state are rejected during prepare.",
            new string[] { context.Fixture.DrivingLine.JournalIdentifier }, "active", "INVALID_ARGUMENT");
        RunRejectedPrepareCase(context, "F11-N05-empty_selection_rejected", "An empty explicit selection is rejected before any NX mutation.",
            new string[0], "reference", "INVALID_REQUEST");
        RunRejectedPrepareCase(context, "F11-N05-duplicate_selection_rejected", "A duplicate selected JID is rejected before any NX mutation.",
            new string[] { context.Fixture.WitnessLine.JournalIdentifier, context.Fixture.WitnessLine.JournalIdentifier }, "reference", "INVALID_REQUEST");
        RunRejectedPrepareCase(context, "F11-N05-unsupported_input_class_rejected", "A Sketch feature JID is rejected as an unsupported input class.",
            new string[] { context.Fixture.Sketch.Feature.JournalIdentifier }, "reference", "OBJECT_NOT_FOUND");
    }

    private static void RunRejectedPrepareCase(Context context, string testId, string objective,
        string[] objectIds, string targetState, string expectedCode)
    {
        RunCase(context, testId, objective,
            "Capture a complete direct snapshot, call only PrepareSketchReferenceConversion with the recorded explicit request, capture the exception/code, and read the same direct snapshot and dirty/revision/write-gate state afterward.",
            "Prepare rejects with the expected deterministic error code; no native builder is created or committed; direct model state, dirty state, active Sketch, binding revision, and harness write gate are unchanged.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                Dictionary<string, object> before = NativeSnapshot(ctx, ctx.Fixture.Sketch);
                bool dirty = ctx.Part.IsModified;
                string revision = ctx.Binding.RevisionId;
                M3SketchReferenceConversionRequest request = new M3SketchReferenceConversionRequest
                {
                    RevisionId = revision, SketchId = ctx.Fixture.Sketch.JournalIdentifier,
                    ObjectIds = new List<string>(objectIds), TargetState = targetState
                };
                row["request"] = RequestRecord(request);
                try
                {
                    M3PlanSnapshot plan = ctx.ReferenceAdapter.PrepareSketchReferenceConversion(ctx.Binding, request);
                    row["unexpected_plan"] = plan == null ? null : plan.PublicView;
                    throw new InvalidOperationException("Prepare unexpectedly accepted an invalid F11 request.");
                }
                catch (M2ModelException ex)
                {
                    row["prepare_exception"] = ExceptionRecord(ex);
                    row["observed_error_code"] = ex.Code;
                    Require(String.Equals(ex.Code, expectedCode, StringComparison.Ordinal),
                        "Prepare rejected with " + ex.Code + "; expected " + expectedCode + ".");
                }
                Dictionary<string, object> after = NativeSnapshot(ctx, ctx.Fixture.Sketch);
                row["native_before"] = before;
                row["native_after"] = after;
                row["part_modified_before"] = dirty;
                row["part_modified_after"] = ctx.Part.IsModified;
                row["binding_revision_before"] = revision;
                row["binding_revision_after"] = ctx.Binding.RevisionId;
                Require(SameExceptRuntime(before, after) && dirty == ctx.Part.IsModified &&
                    String.Equals(revision, ctx.Binding.RevisionId, StringComparison.Ordinal),
                    "Rejected Prepare changed native state, dirty state, or binding revision.");
                row["passed"] = true;
            });
    }

    private static void RunSectionEligibilityCase(Context context)
    {
        RunCase(context, "F11-N03-whole_sketch_section_reference_filter", "Whole-Sketch CurveFeatureRule excludes Reference geometry when Section.SetAllowRefCrvs(false).",
            "Capture the part inventory; under a harness Undo mark set the partial Arc Reference; create a temporary NX Section with only curves allowed, SetAllowRefCrvs(false), and a whole-Sketch CurveFeatureRule; evaluate output copies; geometrically match every output one-to-one to the active source curves and prove no Reference curve appears; destroy Section and undo the fixture.",
            "Every active curve has exactly one geometrically matching Section output; every Reference curve has no match; output copy Tags differ from source Tags; temporary Section leaves no residual object/feature/curve/dirty-state change after cleanup.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                Fixture f = ctx.Fixture;
                Dictionary<string, object> before = NativeSnapshot(ctx, f.Sketch);
                bool dirty = ctx.Part.IsModified;
                Session.UndoMarkId mark = ctx.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F11 Section eligibility cleanup");
                Section section = null;
                bool restored = false;
                try
                {
                    ExecuteConversion(ctx, f.Sketch, new string[] { f.PartialArc.JournalIdentifier }, "reference", false, row, "set_partial_arc_reference");
                    Dictionary<string, object> selection = CurveEligibilityRecord(f.Sketch);
                    List<Curve> activeCurves = CurvesInSketch(f.Sketch, "Active");
                    Require(activeCurves.Count > 0, "Section fixture has no Active curve seed.");
                    section = ctx.Part.Sections.CreateSection(0.001, 0.001, 0.5);
                    section.SetAllowedEntityTypes(Section.AllowTypes.OnlyCurves);
                    section.SetAllowRefCrvs(false);
                    CurveFeatureRule rule = ctx.Part.ScRuleFactory.CreateRuleCurveFeature(new Feature[] { f.Sketch.Feature });
                    Require(rule != null, "NX did not create a whole-Sketch CurveFeatureRule.");
                    Curve seed = activeCurves[0];
                    section.AddToSection(new SelectionIntentRule[] { rule }, seed, null, null,
                        CurveHelpPoint(seed), Section.Mode.Create);
                    NXObject[] outputs;
                    section.GetOutputCurves(out outputs);
                    if (outputs == null) outputs = new NXObject[0];
                    List<Curve> outputCurves = CurvesFromObjects(outputs);
                    Dictionary<string, object> matching = MatchSectionCurves(activeCurves, outputCurves);
                    row["curve_eligibility_before_section"] = selection;
                    row["section_output_records"] = CurveRecords(outputCurves.ToArray());
                    row["section_geometric_one_to_one_matching"] = matching;
                    row["section_set_allow_ref_curves"] = false;
                    row["section_rule_type"] = rule.GetType().FullName;
                    row["section_output_tags_are_source_tags"] = SectionTagsAreSourceTags(outputCurves, activeCurves);
                    Require(Object.Equals(GetValue(matching, "one_to_one_match"), true),
                        "Section output copies did not map one-to-one to all active source curves.");
                    Require(!Object.Equals(row["section_output_tags_are_source_tags"], true),
                        "Section output identity was conflated with source Tags; the NX probe expects temporary output copies.");
                }
                finally
                {
                    if (section != null && !ctx.WritesHalted)
                    {
                        try { section.Destroy(); row["temporary_section_destroyed"] = true; }
                        catch (Exception ex) { row["section_destroy_error"] = ExceptionRecord(ex); }
                    }
                    else if (section != null) row["temporary_section_destroy_skipped_due_to_unknown"] = true;
                    if (!ctx.WritesHalted) restored = RestoreHarnessUndo(ctx, mark, before, dirty, row, "Section eligibility case");
                    else row["undo_skipped_due_to_unknown"] = true;
                }
                Require(restored || ctx.WritesHalted, "Temporary Section cleanup/Undo did not restore the full direct snapshot.");
                if (!ctx.WritesHalted) RefreshBinding(ctx, row, "after Section cleanup");
                row["passed"] = !ctx.WritesHalted;
            });
    }

    private static void RunUnsupportedDimensionCase(Context context)
    {
        RunCase(context, "F11-N06-radial_dimension_explicit_rejection", "Radial dimensions remain outside the linear mm conversion scope and reject before mutation.",
            "Submit the explicit RadialDim SketchDimensionalConstraint JID through the same production request path with target_state=active; capture the exact rejection and complete native state before/after.",
            "Prepare returns UNSUPPORTED_DIMENSION with no native conversion call and the radial dimension, its association, expression, geometry, solver readback, dirty flag, and revision remain unchanged.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                if (ctx.Fixture.RadialDimension == null) throw new SkipCaseException("NX did not create the reference RadialDim fixture; no radial support claim is made.");
                Dictionary<string, object> before = NativeSnapshot(ctx, ctx.Fixture.Sketch);
                bool dirty = ctx.Part.IsModified;
                string revision = ctx.Binding.RevisionId;
                M3SketchReferenceConversionRequest request = new M3SketchReferenceConversionRequest
                {
                    RevisionId = revision, SketchId = ctx.Fixture.Sketch.JournalIdentifier,
                    ObjectIds = new List<string> { ctx.Fixture.RadialDimension.JournalIdentifier }, TargetState = "active"
                };
                row["request"] = RequestRecord(request);
                try
                {
                    M3PlanSnapshot plan = ctx.ReferenceAdapter.PrepareSketchReferenceConversion(ctx.Binding, request);
                    row["unexpected_plan"] = plan == null ? null : plan.PublicView;
                    throw new InvalidOperationException("Radial dimension was accepted although only HorizontalDim/VerticalDim are in scope.");
                }
                catch (M2ModelException ex)
                {
                    row["prepare_exception"] = ExceptionRecord(ex);
                    row["observed_error_code"] = ex.Code;
                    Require(String.Equals(ex.Code, "UNSUPPORTED_DIMENSION", StringComparison.Ordinal),
                        "Radial dimension rejection code was " + ex.Code + "; expected UNSUPPORTED_DIMENSION.");
                }
                Dictionary<string, object> after = NativeSnapshot(ctx, ctx.Fixture.Sketch);
                row["native_before"] = before; row["native_after"] = after;
                Require(SameExceptRuntime(before, after) && dirty == ctx.Part.IsModified &&
                    String.Equals(revision, ctx.Binding.RevisionId, StringComparison.Ordinal),
                    "Unsupported radial dimension rejection changed native or binding state.");
                row["passed"] = true;
            });
    }

    private static void RunUnsupportedGeometryCase(Context context)
    {
        RunCase(context, "F11-N04-ellipse_explicit_rejection", "An Ellipse is explicitly rejected because supported curve conversion is limited to Line, circular Arc and full-circle Arc.",
            "Capture complete direct state; submit only the Ellipse current JID with target_state=reference; require Prepare to reject with UNSUPPORTED_GEOMETRY before native builder invocation; compare direct geometry, identities, statuses, expressions, constraints, history, helper inventories, dirty state and revision.",
            "Prepare returns UNSUPPORTED_GEOMETRY and leaves the Ellipse plus all other model state unchanged.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                if (ctx.Fixture.UnsupportedEllipse == null)
                    throw new SkipCaseException("NX did not create the Ellipse fixture; no unsupported-geometry result is claimed.");
                Dictionary<string, object> before = NativeSnapshot(ctx, ctx.Fixture.Sketch);
                bool dirty = ctx.Part.IsModified;
                string revision = ctx.Binding.RevisionId;
                M3SketchReferenceConversionRequest request = MakeReferenceRequest(ctx, ctx.Fixture.Sketch,
                    new string[] { ctx.Fixture.UnsupportedEllipse.JournalIdentifier }, "reference");
                row["request"] = RequestRecord(request);
                try
                {
                    M3PlanSnapshot plan = ctx.ReferenceAdapter.PrepareSketchReferenceConversion(ctx.Binding, request);
                    row["unexpected_plan"] = plan == null ? null : plan.PublicView;
                    throw new InvalidOperationException("F11 accepted an Ellipse outside the selected curve scope.");
                }
                catch (M2ModelException ex)
                {
                    row["prepare_exception"] = ExceptionRecord(ex);
                    row["observed_error_code"] = ex.Code;
                    Require(String.Equals(ex.Code, "UNSUPPORTED_GEOMETRY", StringComparison.Ordinal),
                        "Ellipse rejection code was " + ex.Code + "; expected UNSUPPORTED_GEOMETRY.");
                }
                Dictionary<string, object> after = NativeSnapshot(ctx, ctx.Fixture.Sketch);
                row["native_before"] = before; row["native_after"] = after;
                Require(SameExceptRuntime(before, after) && dirty == ctx.Part.IsModified &&
                    String.Equals(revision, ctx.Binding.RevisionId, StringComparison.Ordinal),
                    "Unsupported Ellipse rejection changed native or binding state.");
                row["passed"] = true;
            });
    }

    private static void RunDrivingDimensionBehaviorCase(Context context)
    {
        RunCase(context, "F11-N02-horizontal_driving_dimension_behavior_and_undo", "A horizontal driving dimension remains effective on a Reference curve; a Reference dimension measures edits without driving, and Active conversion restores driving behavior.",
            "Under an outer NX Undo mark: convert only the line to Reference; confirm 10 mm and Driving; convert the dimension JID to Reference; change line endpoint to 14 mm and CalculateStatus; read ComputedSize if the direct property works while RHS/Value remain 10; convert the selected dimension to Active; verify line stays 14 and only its own RHS/Value may synchronize to the 14 mm measure; set RHS=12 and CalculateStatus; prove line and ComputedSize are 12; Undo to the mark and compare the complete baseline.",
            "The Driving dimension holds the Reference curve at 10 mm. Reference dimension allows the measured line to reach 14 mm while RHS/Value stay 10 and ComputedSize reads 14 when available. Active conversion leaves geometry at 14 and may synchronize only this selected dimension's own RHS/Value to ComputedSize=14; its Tag/name/unit/seven flags/associations and all other expressions remain unchanged. Explicit RHS=12 drives the line and ComputedSize to 12. Actual NX Undo restores the initial geometry, constraint, expression, reference states, inventories, active Sketch, dirty state, and current binding validity.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                Fixture f = ctx.Fixture;
                Dictionary<string, object> before = NativeSnapshot(ctx, f.Sketch);
                bool dirty = ctx.Part.IsModified;
                string lineTag = f.DrivingLine.Tag.ToString();
                string dimensionTag = f.HorizontalDimension.Tag.ToString();
                row["native_before"] = before;
                row["part_modified_before"] = dirty;
                Session.UndoMarkId mark = ctx.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F11 horizontal driving/reference dimension behavior");
                bool undoRestored = false;
                try
                {
                    ExecuteConversion(ctx, f.Sketch, new string[] { f.DrivingLine.JournalIdentifier }, "reference", false, row, "curve_to_reference");
                    Dictionary<string, object> afterCurveReference = DimensionBehaviorRecord(ctx, f);
                    row["after_curve_reference"] = afterCurveReference;
                    Require(Math.Abs(f.DrivingLine.GetLength() - 10.0) <= PositionToleranceMm,
                        "Reference curve was not held at 10 mm by its selected Driving dimension.");
                    Require(f.HorizontalDimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving &&
                        Object.Equals(GetValue(ReadDimensionRecord(f.HorizontalDimension), "associated_dimension_is_driving"), true),
                        "The Driving dimension stopped driving after its curve became Reference.");

                    ExecuteConversion(ctx, f.Sketch, new string[] { f.HorizontalDimension.JournalIdentifier }, "reference", false, row, "dimension_to_reference");
                    Dictionary<string, object> afterDimensionReference = DimensionBehaviorRecord(ctx, f);
                    row["after_dimension_reference"] = afterDimensionReference;
                    Require(f.HorizontalDimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Reference,
                        "The selected horizontal dimension did not become Reference.");
                    Require(Object.Equals(GetValue(ReadDimensionRecord(f.HorizontalDimension), "associated_dimension_is_driving"), false),
                        "AssociatedDimension.IsDriving did not report false for Reference dimension state.");
                    Require(Math.Abs(f.DrivingLine.GetLength() - 10.0) <= PositionToleranceMm,
                        "Dimension conversion alone changed the constrained line geometry.");

                    ActivateSketch(ctx, f.Sketch);
                    Point3d start = f.DrivingLine.StartPoint;
                    f.DrivingLine.SetEndpoints(start, new Point3d(start.X + 14.0, start.Y, start.Z));
                    f.Sketch.Update();
                    Dictionary<string, object> solvedReference = CalculateAndDeactivate(ctx, f.Sketch);
                    Require(Math.Abs(f.DrivingLine.GetLength() - 14.0) <= PositionToleranceMm,
                        "Reference dimension unexpectedly pulled the line back from the controlled 14 mm endpoint edit.");
                    Dictionary<string, object> measuredAt14 = ReadDimensionRecord(f.HorizontalDimension);
                    row["solver_after_reference_dimension_endpoint_edit"] = solvedReference;
                    row["dimension_after_14mm_endpoint_edit"] = measuredAt14;
                    Require(Math.Abs(Convert.ToDouble(measuredAt14["expression_value"], CultureInfo.InvariantCulture) - 10.0) <= PositionToleranceMm &&
                        IsTenRhs(GetString(measuredAt14, "expression_rhs")),
                        "Reference dimension RHS/Value changed during the endpoint edit instead of retaining 10 mm.");
                    if (GetValue(measuredAt14, "associated_dimension_computed_size") is double)
                    {
                        row["computed_size_14mm_direct_query"] = "available";
                        Require(Math.Abs(Convert.ToDouble(measuredAt14["associated_dimension_computed_size"], CultureInfo.InvariantCulture) - 14.0) <= PositionToleranceMm,
                            "Direct NX AssociatedDimension.ComputedSize did not read 14 mm after the Reference dimension endpoint edit.");
                    }
                    else row["computed_size_14mm_direct_query"] = GetValue(measuredAt14, "associated_dimension_computed_size_query");

                    Dictionary<string, object> flagsBeforeActive = GetDictionary(afterDimensionReference, "dimension");
                    ExecuteConversion(ctx, f.Sketch, new string[] { f.HorizontalDimension.JournalIdentifier }, "active", false, row, "dimension_to_active");
                    Require(f.HorizontalDimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving,
                        "The selected horizontal dimension did not return to Driving state.");
                    Require(Math.Abs(f.DrivingLine.GetLength() - 14.0) <= PositionToleranceMm,
                        "Converting the Reference dimension to Active changed the 14 mm geometry before the explicit RHS edit.");
                    Dictionary<string, object> afterDimensionActive = DimensionBehaviorRecord(ctx, f);
                    row["after_dimension_active"] = afterDimensionActive;
                    Dictionary<string, object> selectedDimension = ReadDimensionRecord(f.HorizontalDimension);
                    Require(Math.Abs(Convert.ToDouble(selectedDimension["associated_dimension_computed_size"], CultureInfo.InvariantCulture) - 14.0) <= PositionToleranceMm,
                        "Active dimension ComputedSize did not remain synchronized with the 14 mm measured geometry.");
                    Require(String.Equals(GetString(selectedDimension, "expression_rhs"), "14", StringComparison.Ordinal) ||
                        Math.Abs(Convert.ToDouble(selectedDimension["expression_value"], CultureInfo.InvariantCulture) - 14.0) <= PositionToleranceMm,
                        "The selected Active dimension did not synchronize its own RHS/Value to the measured 14 mm size.");
                    Require(UnchangedDimensionIdentityAndFlags(flagsBeforeActive, selectedDimension),
                        "Dimension activation changed identity, units, expression flags, or geometry associations outside the allowed selected RHS/Value synchronization.");
                    Require(OnlySelectedDimensionExpressionMaySynchronize(before, NativeSnapshot(ctx, f.Sketch), dimensionTag),
                        "Dimension activation changed a non-selected expression, dimension Tag, or expression flag.");

                    ActivateSketch(ctx, f.Sketch);
                    f.HorizontalDimension.AssociatedExpression.RightHandSide = "12";
                    f.Sketch.Update();
                    Dictionary<string, object> solvedAt12 = CalculateAndDeactivate(ctx, f.Sketch);
                    Dictionary<string, object> at12 = ReadDimensionRecord(f.HorizontalDimension);
                    row["solver_after_expression_12"] = solvedAt12;
                    row["dimension_at_12mm"] = at12;
                    row["line_at_12mm"] = CurveRecord(f.DrivingLine);
                    Require(Math.Abs(f.DrivingLine.GetLength() - 12.0) <= PositionToleranceMm,
                        "The Active dimension RHS=12 did not drive the line to 12 mm.");
                    if (GetValue(at12, "associated_dimension_computed_size") is double)
                        Require(Math.Abs(Convert.ToDouble(at12["associated_dimension_computed_size"], CultureInfo.InvariantCulture) - 12.0) <= PositionToleranceMm,
                            "Direct NX ComputedSize did not report 12 mm after the explicit RHS edit.");
                }
                finally
                {
                    if (!ctx.WritesHalted) undoRestored = RestoreHarnessUndo(ctx, mark, before, dirty, row, "horizontal dimension behavior");
                    else row["undo_skipped_due_to_unknown"] = true;
                }
                if (!undoRestored)
                {
                    ctx.WritesHalted = true;
                    throw new UnknownOutcomeException("Actual NX Undo did not restore the dimension behavior baseline; stopping all model writes.");
                }
                RefreshBinding(ctx, row, "after horizontal behavior Undo");
                Require(String.Equals(ReadCurveReferenceStatus(f.Sketch, lineTag), "Active", StringComparison.Ordinal),
                    "Undo did not restore the original active state of the Driving Line.");
                Require(FindConstraintByTag(f.Sketch, dimensionTag) != null,
                    "Undo removed the original horizontal dimensional constraint.");
                row["passed"] = true;
            });
    }

    private static void RunVerticalDimensionCase(Context context)
    {
        RunCase(context, "F11-N06-vertical_linear_dimension_bidirectional", "VerticalDim constraints in millimetres use the same explicit conversion path in both directions.",
            "Record the vertical line and dimension; under a harness Undo mark convert only the dimension JID to Reference then Active; directly read state, associated measurement, expression, identity, seven flags and solver enum/numeric DOF; undo and prove restoration.",
            "The selected VerticalDim toggles Reference/Driving, direct IsDriving matches the state, its own expression/measurement is captured, all unrelated geometry/constraints/expressions remain unchanged, and actual Undo restores the baseline.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                Fixture f = ctx.Fixture;
                Dictionary<string, object> before = NativeSnapshot(ctx, f.Sketch);
                bool dirty = ctx.Part.IsModified;
                Session.UndoMarkId mark = ctx.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F11 vertical dimension restoration");
                bool restored = false;
                try
                {
                    ExecuteConversion(ctx, f.Sketch, new string[] { f.VerticalDimension.JournalIdentifier }, "reference", false, row, "vertical_to_reference");
                    Dictionary<string, object> referenceRecord = ReadDimensionRecord(f.VerticalDimension);
                    row["after_reference"] = referenceRecord;
                    Require(f.VerticalDimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Reference &&
                        Object.Equals(referenceRecord["associated_dimension_is_driving"], false),
                        "Vertical dimension did not become non-driving Reference state.");
                    ExecuteConversion(ctx, f.Sketch, new string[] { f.VerticalDimension.JournalIdentifier }, "active", false, row, "vertical_to_active");
                    Dictionary<string, object> activeRecord = ReadDimensionRecord(f.VerticalDimension);
                    row["after_active"] = activeRecord;
                    Require(f.VerticalDimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving &&
                        Object.Equals(activeRecord["associated_dimension_is_driving"], true),
                        "Vertical dimension did not return to Driving state.");
                    Require(Math.Abs(f.VerticalLine.GetLength() - 8.0) <= PositionToleranceMm,
                        "Vertical dimension conversion changed the fixture geometry.");
                }
                finally
                {
                    if (!ctx.WritesHalted) restored = RestoreHarnessUndo(ctx, mark, before, dirty, row, "vertical dimension conversion");
                    else row["undo_skipped_due_to_unknown"] = true;
                }
                Require(restored || ctx.WritesHalted, "Vertical-dimension Undo did not restore the baseline.");
                if (!ctx.WritesHalted) RefreshBinding(ctx, row, "after vertical dimension Undo");
                row["passed"] = !ctx.WritesHalted;
            });
    }

    private static void RunCprojSiblingCase(Context context)
    {
        RunCase(context, "F11-N10-cproj_sibling_output_isolation", "Converting one CPROJ output curve leaves its sibling output, source association and feature intact.",
            "Under an outer Undo mark create two independent source Lines, project both into the fixture Sketch with the native production SketchProjectBuilder factory, capture CPROJ feature/Section source relation and both output Tags/geometries/statuses, convert exactly one output through F11, re-read all three outputs/feature/relation, then Undo all setup and conversion work.",
            "Only the explicitly selected output changes state; sibling output, CPROJ feature, Section source relation and all source/output geometry remain intact. Harness Undo returns the exact prior model state.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                IM3SketchProjectionOperationAdapter projectionAdapter = (object)ctx.Adapter as IM3SketchProjectionOperationAdapter;
                if (projectionAdapter == null)
                    throw new SkipCaseException("Production M2OpenModelAdapter does not currently expose the projection factory needed to build a CPROJ sibling fixture.");
                Dictionary<string, object> before = NativeSnapshot(ctx, ctx.Fixture.Sketch);
                bool dirty = ctx.Part.IsModified;
                Session.UndoMarkId mark = ctx.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F11 CPROJ sibling fixture and conversion restore");
                bool restored = false;
                try
                {
                    Line sourceA = ctx.Part.Curves.CreateLine(new Point3d(60.0, 0.0, 4.0), new Point3d(68.0, 0.0, 4.0));
                    Line sourceB = ctx.Part.Curves.CreateLine(new Point3d(60.0, 6.0, 4.0), new Point3d(66.0, 6.0, 4.0));
                    sourceA.SetName("F11_CPROJ_SOURCE_A"); sourceB.SetName("F11_CPROJ_SOURCE_B");
                    ctx.Fixture.Sketch.Activate(Sketch.ViewReorient.False);
                    V2SketchProjectionNativeResult projected = V2SketchProjectionFactory.Execute(ctx.Part,
                        ctx.Fixture.Sketch, new NXObject[] { sourceA, sourceB }, true);
                    DeactivateIfActive(ctx.Session, ctx.Fixture.Sketch);
                    Require(projected != null && projected.OutputCurves != null && projected.OutputCurves.Length == 2,
                        "CPROJ fixture did not produce exactly two sibling outputs.");
                    Feature feature = projected.CommitObject as Feature;
                    if (feature == null && projected.CommittedObjects != null)
                        foreach (NXObject item in projected.CommittedObjects) if (item is Feature) { feature = (Feature)item; break; }
                    Require(feature != null, "Native SketchProjectBuilder did not return a CPROJ Feature object.");
                    Curve outputA = projected.OutputCurves[0], outputB = projected.OutputCurves[1];
                    string featureTag = feature.Tag.ToString(), featureJid = feature.JournalIdentifier;
                    string selectedTag = outputA.Tag.ToString(), siblingTag = outputB.Tag.ToString();
                    Dictionary<string, object> relationBefore = ReadCprojSectionRelation(ctx.Part, feature);
                    Dictionary<string, object> geometryBefore = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "source_a", CurveRecord(sourceA) }, { "source_b", CurveRecord(sourceB) },
                        { "output_a", CurveRecord(outputA) }, { "output_b", CurveRecord(outputB) }
                    };
                    RefreshBinding(ctx, row, "after native CPROJ fixture creation");
                    row["native_cproj_factory_evidence"] = projected.Evidence;
                    row["cproj_feature_before"] = FeatureRecord(feature, 0);
                    row["cproj_section_relation_before"] = relationBefore;
                    row["source_and_output_geometry_before"] = geometryBefore;
                    Dictionary<string, object> operation = ExecuteConversion(ctx, ctx.Fixture.Sketch,
                        new string[] { outputA.JournalIdentifier }, "reference", false, row, "convert_one_output");
                    Curve outputAAfter = FindCurveByTag(ctx.Part, selectedTag);
                    Curve outputBAfter = FindCurveByTag(ctx.Part, siblingTag);
                    Feature featureAfter = FindFeatureByTag(ctx.Part, featureTag);
                    Dictionary<string, object> relationAfter = ReadCprojSectionRelation(ctx.Part, featureAfter);
                    Require(ReadCurveReferenceStatus(ctx.Fixture.Sketch, selectedTag) == "Reference" &&
                        ReadCurveReferenceStatus(ctx.Fixture.Sketch, siblingTag) == "Active",
                        "CPROJ conversion changed the wrong output or sibling state.");
                    Require(SameCurveGeometry(outputA, outputAAfter) && SameCurveGeometry(outputB, outputBAfter) &&
                        SameValue(relationBefore, relationAfter) &&
                        String.Equals(featureTag, featureAfter.Tag.ToString(), StringComparison.Ordinal) &&
                        String.Equals(featureJid, featureAfter.JournalIdentifier, StringComparison.Ordinal),
                        "CPROJ output geometry, feature identity, source Section relation, or sibling output changed.");
                    row["selected_output_after"] = CurveRecord(outputAAfter);
                    row["sibling_output_after"] = CurveRecord(outputBAfter);
                    row["cproj_feature_after"] = FeatureRecord(featureAfter, 0);
                    row["cproj_section_relation_after"] = relationAfter;
                    row["operation"] = operation;
                }
                finally
                {
                    if (!ctx.WritesHalted) restored = RestoreHarnessUndo(ctx, mark, before, dirty, row, "CPROJ sibling isolation");
                    else row["undo_skipped_due_to_unknown"] = true;
                }
                Require(restored || ctx.WritesHalted, "CPROJ sibling test Undo did not restore the original model.");
                if (!ctx.WritesHalted) RefreshBinding(ctx, row, "after CPROJ Undo");
                row["passed"] = !ctx.WritesHalted;
            });
    }

    private static void RunProductUndoFaultCase(Context context)
    {
        RunCase(context, "F11-N08-product_undo_after_injected_postcondition_failure", "A post-commit injected F11 verification fault exercises the product adapter's real NX Undo exactly once.",
            "Capture full direct baseline; prepare one valid Active-to-Reference conversion; set the adapter-owned ForcePostCommitVerificationFailureForTesting flag on that exact retained plan; call ExecutePlan once; capture direct model and complete product rollback evidence; do not replay. If rollback is unproven, mark UNKNOWN and stop writes.",
            "Native conversion is actually called; product result is ROLLED_BACK; real Undo, update, mark closure, all product restoration booleans, direct full snapshot, dirty state and active Sketch are restored. This is a controlled injection, not spontaneous NX reliability evidence.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                Fixture f = ctx.Fixture;
                Dictionary<string, object> before = NativeSnapshot(ctx, f.Sketch);
                bool dirty = ctx.Part.IsModified;
                string revisionBefore = ctx.Binding.RevisionId;
                M3SketchReferenceConversionRequest request = MakeReferenceRequest(ctx, f.Sketch,
                    new string[] { f.WitnessLine.JournalIdentifier }, "reference");
                row["request"] = RequestRecord(request);
                M3PlanSnapshot plan = ctx.ReferenceAdapter.PrepareSketchReferenceConversion(ctx.Binding, request);
                Require(plan != null && plan.AdapterState != null, "F11 Undo fault case prepare returned no retained adapter plan state.");
                Dictionary<string, object> afterPrepare = NativeSnapshot(ctx, f.Sketch);
                Require(SameExceptRuntime(before, afterPrepare) && dirty == ctx.Part.IsModified,
                    "Fault-case Prepare modified the native model or dirty state.");
                SetBooleanMember(plan.AdapterState, "ForcePostCommitVerificationFailureForTesting", true);
                row["fault_injection"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "field", "M3SketchReferenceConversionPlanState.ForcePostCommitVerificationFailureForTesting" },
                    { "value", true }, { "injection_count", 1 },
                    { "classification", "controlled post-commit verification mismatch; not spontaneous NX failure" }
                };
                M3ExecutionResult execution = null;
                try { execution = ((IM3OperationAdapter)ctx.Adapter).ExecutePlan(ctx.Binding, plan); }
                catch (Exception ex)
                {
                    ctx.WritesHalted = true;
                    row["execute_exception"] = ExceptionRecord(ex);
                    row["operation_outcome"] = "UNKNOWN";
                    row["writes_must_remain_stopped"] = true;
                    try { row["native_after_unclassified_execute_exception"] = NativeSnapshot(ctx, f.Sketch); }
                    catch (Exception readEx) { row["readback_after_execute_exception"] = ExceptionRecord(readEx); }
                    throw new UnknownOutcomeException("F11 fault-injection ExecutePlan threw; no cleanup or replay is allowed.");
                }
                row["execution"] = ExecutionRecord(execution);
                Dictionary<string, object> after;
                try { after = NativeSnapshot(ctx, f.Sketch); }
                catch (Exception ex)
                {
                    ctx.WritesHalted = true;
                    row["native_after_read_exception"] = ExceptionRecord(ex);
                    row["operation_outcome"] = "UNKNOWN";
                    row["writes_must_remain_stopped"] = true;
                    throw new UnknownOutcomeException("F11 product Undo result could not be directly read back; no further write or replay is allowed.");
                }
                row["native_before"] = before; row["native_after_execute_or_undo"] = after;
                row["part_modified_before"] = dirty; row["part_modified_after"] = ctx.Part.IsModified;
                row["binding_revision_before"] = revisionBefore; row["binding_revision_after"] = ctx.Binding.RevisionId;
                Dictionary<string, object> rollback = FindNestedDictionary(execution == null ? null : execution.Result, "rollback");
                if (rollback == null) rollback = FindNestedDictionary(execution == null ? null : execution.Result, "evidence");
                row["production_rollback_evidence"] = rollback;
                row["native_operation_called"] = FindNestedValue(execution == null ? null : execution.Result, "native_operation_called");
                bool directRestored = SameExceptRuntime(before, after) && dirty == ctx.Part.IsModified && ctx.Session.ActiveSketch == null;
                row["direct_full_snapshot_restored"] = directRestored;
                bool productRestored = RollbackEvidenceComplete(rollback);
                row["product_full_restoration_evidence_complete"] = productRestored;
                bool success = execution != null && execution.State == "ROLLED_BACK" &&
                    Object.Equals(FindNestedValue(execution.Result, "native_operation_called"), true) &&
                    directRestored && productRestored;
                row["passed"] = success;
                if (execution == null || execution.State == "UNKNOWN" || !directRestored || !productRestored)
                {
                    row["operation_outcome"] = "UNKNOWN";
                    row["writes_must_remain_stopped"] = true;
                    ctx.WritesHalted = true;
                    throw new UnknownOutcomeException("F11 product Undo injection did not prove full restoration; operation is UNKNOWN and no write or retry is allowed.");
                }
                Require(success, "Controlled F11 post-commit fault did not produce a fully proven product ROLLED_BACK result.");
                row["operation_outcome"] = "ROLLED_BACK";
                row["writes_must_remain_stopped"] = false;
            });
    }

    private static void RunOverconstraintCase(Context context)
    {
        RunCase(context, "F11-N12-conflicting_linear_dimensions_reject_without_constraint_loss", "Activating a Reference dimension that conflicts with another Driving dimension rejects without deleting pre-existing constraints.",
            "Within a harness Undo mark create an isolated geometry-only line; add Driving horizontal dimension A=10; convert A to Reference; add second Driving horizontal dimension B=12 on the same operand and solve the consistent model; capture full constraints/expressions/solver; request A Active exactly once; directly CalculateStatus and inspect result, constraints, dimensions, associations and geometry; Undo fixture.",
            "The conflicting activation is rejected or ROLLED_BACK only when the complete before snapshot and both constraints are restored. If any state is unproven, mark UNKNOWN and stop writes; never count a post-commit or spontaneous fault as pass without full direct evidence.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                Dictionary<string, object> beforeAll = NativeSnapshot(ctx, ctx.Fixture.Sketch);
                bool dirtyAll = ctx.Part.IsModified;
                Session.UndoMarkId mark = ctx.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F11 conflicting dimension fixture and restore");
                bool restored = false;
                try
                {
                    Fixture conflict = CreateConflictFixture(ctx, row);
                    Dictionary<string, object> preActivationSolver = CalculateStatusWhileActive(ctx, conflict.Sketch);
                    row["solver_before_conflicting_activation"] = preActivationSolver;
                    Require(!IsOverconstrained(GetString(preActivationSolver, "status")),
                        "Conflict fixture is already overconstrained before the F11 operation; skip this matrix row as invalid fixture.");
                    Dictionary<string, object> before = NativeSnapshot(ctx, conflict.Sketch);
                    bool conflictDirtyBefore = ctx.Part.IsModified;
                    row["conflict_fixture_before_activation"] = before;
                    M3SketchReferenceConversionRequest request = MakeReferenceRequest(ctx, conflict.Sketch,
                        new string[] { conflict.HorizontalDimension.JournalIdentifier }, "active");
                    row["request"] = RequestRecord(request);
                    M3PlanSnapshot plan = ctx.ReferenceAdapter.PrepareSketchReferenceConversion(ctx.Binding, request);
                    M3ExecutionResult execution;
                    try { execution = ((IM3OperationAdapter)ctx.Adapter).ExecutePlan(ctx.Binding, plan); }
                    catch (Exception ex)
                    {
                        ctx.WritesHalted = true;
                        row["execute_exception"] = ExceptionRecord(ex);
                        row["operation_outcome"] = "UNKNOWN";
                        row["writes_must_remain_stopped"] = true;
                        try { row["native_after_unclassified_execute_exception"] = NativeSnapshot(ctx, conflict.Sketch); }
                        catch (Exception readEx) { row["readback_after_execute_exception"] = ExceptionRecord(readEx); }
                        throw new UnknownOutcomeException("Conflicting-dimension ExecutePlan threw; no cleanup or replay is allowed.");
                    }
                    row["execution"] = ExecutionRecord(execution);
                    if (execution != null && execution.State == "UNKNOWN")
                    {
                        ctx.WritesHalted = true;
                        row["operation_outcome"] = "UNKNOWN";
                        row["writes_must_remain_stopped"] = true;
                        throw new UnknownOutcomeException("Conflicting dimension activation returned UNKNOWN.");
                    }
                    if (execution == null || (execution.State != "APPLIED" && execution.State != "ROLLED_BACK" && execution.State != "UNCHANGED"))
                    {
                        ctx.WritesHalted = true;
                        row["operation_outcome"] = "UNKNOWN";
                        row["writes_must_remain_stopped"] = true;
                        throw new UnknownOutcomeException("Conflicting activation returned no execution or an unclassified terminal state.");
                    }
                    if (execution != null && execution.State == "APPLIED")
                    {
                        Dictionary<string, object> postSolve = CalculateStatusWhileActive(ctx, conflict.Sketch);
                        row["solver_after_conflicting_activation"] = postSolve;
                        row["operation_outcome"] = "APPLIED";
                        row["overconstraint_detected_after_applied_result"] = IsOverconstrained(GetString(postSolve, "status"));
                        throw new InvalidOperationException("The production adapter applied a dimension conflict instead of returning a known rollback.");
                    }
                    Dictionary<string, object> after;
                    try { after = NativeSnapshot(ctx, conflict.Sketch); }
                    catch (Exception ex)
                    {
                        ctx.WritesHalted = true;
                        row["native_after_read_exception"] = ExceptionRecord(ex);
                        row["operation_outcome"] = "UNKNOWN";
                        row["writes_must_remain_stopped"] = true;
                        throw new UnknownOutcomeException("Overconstraint terminal result could not be directly read back; harness Undo is prohibited.");
                    }
                    row["native_after_rejection_or_rollback"] = after;
                    bool stateRestored = SameExceptRuntime(before, after);
                    bool dirtyRestored = ctx.Part.IsModified == conflictDirtyBefore;
                    bool bothDimensionsPresent = FindConstraintByTag(conflict.Sketch, conflict.HorizontalDimension.Tag.ToString()) != null &&
                        FindConstraintByTag(conflict.Sketch, conflict.VerticalDimension.Tag.ToString()) != null;
                    row["all_preexisting_constraints_preserved"] = bothDimensionsPresent;
                    row["conflict_fixture_restored"] = stateRestored;
                    row["conflict_dirty_state_restored"] = dirtyRestored;
                    Dictionary<string, object> productRollback = FindNestedDictionary(execution.Result, "rollback");
                    if (productRollback == null) productRollback = FindNestedDictionary(execution.Result, "evidence");
                    row["product_rollback_evidence"] = productRollback;
                    bool productRollbackProven = execution.State != "ROLLED_BACK" || RollbackEvidenceComplete(productRollback);
                    row["product_rollback_evidence_complete"] = productRollbackProven;
                    if (!stateRestored || !dirtyRestored || !bothDimensionsPresent || !productRollbackProven)
                    {
                        ctx.WritesHalted = true;
                        row["operation_outcome"] = "UNKNOWN";
                        row["writes_must_remain_stopped"] = true;
                        throw new UnknownOutcomeException("Conflicting activation returned ROLLED_BACK without full direct/product restoration evidence; harness Undo is prohibited.");
                    }
                    bool knownRejected = execution.State == "ROLLED_BACK" ||
                        (execution.State == "UNCHANGED" && !String.IsNullOrEmpty(execution.ErrorCode) &&
                         execution.ErrorCode != "STALE_PLAN" && execution.ErrorCode != "STALE_BINDING" &&
                         execution.ErrorCode != "INVALID_PLAN" && execution.ErrorCode != "NX_BUSY");
                    row["known_rejected_terminal_state"] = knownRejected;
                    Require(knownRejected && stateRestored && dirtyRestored && bothDimensionsPresent && productRollbackProven,
                        "Conflicting dimension activation was not a fully restored known ROLLED_BACK or deterministic UNCHANGED result.");
                    row["passed"] = true;
                }
                finally
                {
                    if (!ctx.WritesHalted) restored = RestoreHarnessUndo(ctx, mark, beforeAll, dirtyAll, row, "overconstraint fixture cleanup");
                    else row["undo_skipped_due_to_unknown"] = true;
                }
                if (!restored && !ctx.WritesHalted)
                {
                    ctx.WritesHalted = true;
                    throw new UnknownOutcomeException("Conflict fixture cleanup Undo failed; no further writes are permitted.");
                }
                if (!ctx.WritesHalted) RefreshBinding(ctx, row, "after overconstraint cleanup");
            });
    }

    private static Fixture CreateConflictFixture(Context context, Dictionary<string, object> row)
    {
        M3SketchRequest request = new M3SketchRequest
        {
            RevisionId = context.Binding.RevisionId,
            OriginMm = new double[] { 0.0, 0.0, 0.0 }, Normal = new double[] { 0.0, 0.0, 1.0 }, XAxis = new double[] { 1.0, 0.0, 0.0 },
            ConstraintMode = "geometry_only",
            Geometries = new List<M3SketchGeometryRequest>
            {
                new M3SketchGeometryRequest { Id = "conflict-line", Type = "line", StartMm = new double[] { 80.0, 0.0 }, EndMm = new double[] { 90.0, 0.0 } }
            }
        };
        IM3OperationAdapter baseAdapter = (IM3OperationAdapter)context.Adapter;
        M3PlanSnapshot sketchPlan = baseAdapter.PrepareSketch(context.Binding, request);
        M3ExecutionResult sketchExecution = baseAdapter.ExecutePlan(context.Binding, sketchPlan);
        Require(sketchExecution != null && sketchExecution.State == "APPLIED", "Could not create the conflicting-dimension Sketch fixture.");
        Sketch sketch = FindSketchByJournalId(context.Part, GetString(sketchExecution.Result, "sketch_id"));
        sketch.Feature.SetName("F11_CONFLICT_SKETCH");
        Line line = null;
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Line candidate = item as Line;
            if (candidate != null && Math.Abs(candidate.GetLength() - 10.0) <= PositionToleranceMm) { line = candidate; break; }
        }
        Require(line != null, "Conflict fixture did not resolve its 10 mm Line.");
        Session.UndoMarkId dimensionMark = context.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F11 conflict dimension setup");
        bool active = false;
        SketchDimensionalConstraint first = null, second = null;
        try
        {
            sketch.Activate(Sketch.ViewReorient.False); active = true;
            first = sketch.CreateDimension(Sketch.ConstraintType.HorizontalDim,
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.StartPoint, AssocValue = 0 },
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.EndPoint, AssocValue = 0 },
                new Point3d(85.0, 2.0, 0.0), null, Sketch.DimensionOption.CreateAsDriving);
            first.SetName("F11_CONFLICT_REFERENCE_10");
            first.AssociatedExpression.RightHandSide = "10";
            sketch.Update();
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model); active = false;
            Require(context.Session.UpdateManager.DoUpdate(dimensionMark) == 0, "NX update failed creating first conflict dimension.");
        }
        finally
        {
            if (active && context.Session.ActiveSketch != null && context.Session.ActiveSketch.Tag.Equals(sketch.Tag))
                sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
        }
        context.Adapter.GetSession(context.Binding);
        ExecuteConversion(context, sketch, new string[] { first.JournalIdentifier }, "reference", false, row, "conflict_first_dimension_to_reference");
        try
        {
            sketch.Activate(Sketch.ViewReorient.False); active = true;
            second = sketch.CreateDimension(Sketch.ConstraintType.HorizontalDim,
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.StartPoint, AssocValue = 0 },
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.EndPoint, AssocValue = 0 },
                new Point3d(85.0, -2.0, 0.0), null, Sketch.DimensionOption.CreateAsDriving);
            second.SetName("F11_CONFLICT_DRIVING_12");
            second.AssociatedExpression.RightHandSide = "12";
            sketch.Update();
                    row["conflict_fixture_solver_after_second_dimension"] = ApplyWholeSketchRegionAndCalculate(context, sketch);
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model); active = false;
            Require(context.Session.UpdateManager.DoUpdate(dimensionMark) == 0, "NX update failed creating second conflict dimension.");
        }
        finally
        {
            if (active && context.Session.ActiveSketch != null && context.Session.ActiveSketch.Tag.Equals(sketch.Tag))
                sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            try { context.Session.DeleteUndoMark(dimensionMark, null); } catch { }
        }
        Require(first != null && second != null && second.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving &&
            first.DimensionState == SketchDimensionalConstraint.DimensionStateType.Reference,
            "Conflict fixture must contain one Reference 10 mm dimension and a second Driving 12 mm dimension.");
        Require(Math.Abs(line.GetLength() - 12.0) <= PositionToleranceMm,
            "Second Driving dimension did not establish the measured 12 mm conflict fixture.");
        context.Adapter.GetSession(context.Binding);
        row["conflict_fixture"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "feature_name", sketch.Feature.Name }, { "sketch_tag", sketch.Tag.ToString() },
            { "line", CurveRecord(line) }, { "reference_dimension", ReadDimensionRecord(first) },
            { "driving_dimension", ReadDimensionRecord(second) },
            { "precondition", "one horizontal dimension is Reference with retained RHS 10 mm; a second horizontal dimension is Driving at 12 mm on the same line; CalculateStatus is captured before reactivation." }
        };
        return new Fixture { Sketch = sketch, DrivingLine = line, HorizontalDimension = first, VerticalDimension = second };
    }

    private static void RunSaveCopyCase(Context context)
    {
        RunCase(context, "F11-N09-save_copy_reopen_state_persistence", "Save Copy/reopen preserves a mixed Reference/Active fixture, dimensions, geometry, and a live CPROJ source relation.",
            "Before saving, use production F11 conversion to leave one partial Arc and one VerticalDim in Reference while leaving the driving Line/HorizontalDim active; create a two-output associative CPROJ on the same Sketch and convert one CPROJ output to Reference. Capture the full direct state and Section source relation, call production M2 SaveCopy(binding, unique target, reopen=true), then resolve the named Sketch, curves, dimensions, CPROJ feature and Section sources in the reopened copy by one-to-one geometry/identity mapping.",
            "Production SaveCopy returns SAVED and reopened=true. The copy retains the mixed curve and dimension states, unselected Active/Driving controls, all fixture geometry and expressions, and the CPROJ feature with its two source relation records and sibling output. After reopen, every output maps one-to-one by name and direct geometry; Section inputs resolve to the named reopened sources. Do not assume Tag/JID persistence without resolving the saved relation against reopened objects.",
            delegate(Context ctx, Dictionary<string, object> row)
            {
                if (ctx.WritesHalted) throw new SkipCaseException("Writes are halted after an UNKNOWN native outcome.");
                ExecuteConversion(ctx, ctx.Fixture.Sketch,
                    new string[] { ctx.Fixture.PartialArc.JournalIdentifier }, "reference", false, row, "savecopy_setup_partial_arc_reference");
                ExecuteConversion(ctx, ctx.Fixture.Sketch,
                    new string[] { ctx.Fixture.VerticalDimension.JournalIdentifier }, "reference", false, row, "savecopy_setup_vertical_dimension_reference");
                Dictionary<string, object> cprojFixture = CreatePersistentCprojFixture(ctx, row);
                string selectedOutputTag = GetString(cprojFixture, "selected_output_tag");
                Curve selectedOutput = FindCurveByTag(ctx.Part, selectedOutputTag);
                ExecuteConversion(ctx, ctx.Fixture.Sketch,
                    new string[] { selectedOutput.JournalIdentifier }, "reference", false, row, "savecopy_setup_cproj_output_reference");
                RefreshBinding(ctx, row, "after persistent mixed state and CPROJ fixture");
                Require(ReadCurveReferenceStatus(ctx.Fixture.Sketch, ctx.Fixture.PartialArc.Tag.ToString()) == "Reference" &&
                    ctx.Fixture.VerticalDimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Reference &&
                    ReadCurveReferenceStatus(ctx.Fixture.Sketch, ctx.Fixture.DrivingLine.Tag.ToString()) == "Active" &&
                    ctx.Fixture.HorizontalDimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving,
                    "Save Copy source fixture does not contain the required mixed curve/dimension states.");
                Dictionary<string, object> before = NativeSnapshot(ctx, ctx.Fixture.Sketch);
                string originalPath = ctx.Part.FullPath;
                Dictionary<string, object> cprojRelationBefore = ReadCprojSectionRelation(ctx.Part,
                    FindFeatureByName(ctx.Part, "F11_CPROJ_PERSISTENT"));
                row["persistent_cproj_fixture"] = cprojFixture;
                row["persistent_cproj_relation_before_save"] = cprojRelationBefore;
                row["persistent_cproj_relation_resolves_to_named_sources_before_save"] =
                    CprojRelationMatchesNamedSources(ctx.Part, cprojRelationBefore,
                        "F11_CPROJ_SOURCE_A", "F11_CPROJ_SOURCE_B");
                Require(Object.Equals(row["persistent_cproj_relation_resolves_to_named_sources_before_save"], true),
                    "CPROJ Section relation did not resolve to the exact two named source Lines before Save Copy.");
                M2SaveCopyResult saved = ctx.Adapter.SaveCopy(ctx.Binding, ctx.CopyPath, true);
                row["save_copy_result"] = SaveCopyRecord(saved);
                row["source_path"] = originalPath; row["copy_path"] = saved == null ? null : saved.PartPath;
                Require(saved != null && String.Equals(saved.State, "SAVED", StringComparison.Ordinal) && saved.Reopened,
                    "Production SaveCopy did not return SAVED/reopened=true.");
                Part reopened = ctx.Session.Parts.Work;
                Require(reopened != null && String.Equals(Path.GetFullPath(reopened.FullPath), Path.GetFullPath(ctx.CopyPath), StringComparison.OrdinalIgnoreCase),
                    "The Work Part after SaveCopy reopen is not the unique requested copy.");
                ctx.Part = reopened;
                ctx.Fixture.Sketch = FindSketchByFeatureName(reopened, "F11_MAIN_SKETCH");
                ctx.Fixture.DrivingLine = FindCurveByName(ctx.Fixture.Sketch, "F11_DRIVING_LINE") as Line;
                ctx.Fixture.VerticalLine = FindCurveByName(ctx.Fixture.Sketch, "F11_VERTICAL_LINE") as Line;
                ctx.Fixture.WitnessLine = FindCurveByName(ctx.Fixture.Sketch, "F11_WITNESS_LINE") as Line;
                ctx.Fixture.PartialArc = FindCurveByName(ctx.Fixture.Sketch, "F11_PARTIAL_ARC") as Arc;
                ctx.Fixture.FullCircle = FindCurveByName(ctx.Fixture.Sketch, "F11_FULL_CIRCLE") as Arc;
                ctx.Fixture.UnsupportedEllipse = FindCurveByName(ctx.Fixture.Sketch, "F11_UNSUPPORTED_ELLIPSE") as Ellipse;
                ctx.Fixture.HorizontalDimension = FindDimensionByName(ctx.Fixture.Sketch, "F11_HORIZONTAL_DIMENSION");
                ctx.Fixture.VerticalDimension = FindDimensionByName(ctx.Fixture.Sketch, "F11_VERTICAL_DIMENSION");
                ctx.Fixture.RadialDimension = FindDimensionByName(ctx.Fixture.Sketch, "F11_UNSUPPORTED_RADIAL_DIMENSION");
                Require(ctx.Fixture.DrivingLine != null && ctx.Fixture.VerticalLine != null && ctx.Fixture.PartialArc != null && ctx.Fixture.FullCircle != null &&
                    ctx.Fixture.UnsupportedEllipse != null &&
                    ctx.Fixture.HorizontalDimension != null && ctx.Fixture.VerticalDimension != null,
                    "Save Copy reopen did not resolve the main fixture by stable names and geometry.");
                Dictionary<string, object> after = NativeSnapshot(ctx, ctx.Fixture.Sketch);
                Feature reopenedCproj = FindFeatureByName(ctx.Part, "F11_CPROJ_PERSISTENT");
                Dictionary<string, object> cprojRelationAfter = ReadCprojSectionRelation(ctx.Part, reopenedCproj);
                row["native_before_save_copy"] = before;
                row["native_after_reopen"] = after;
                row["production_roundtrip_checks"] = saved.Checks;
                row["identity_mapping"] = ReopenedIdentityMap(before, after);
                row["cproj_relation_after_reopen"] = cprojRelationAfter;
                row["cproj_relation_resolves_to_named_sources_after_reopen"] = CprojRelationMatchesNamedSources(ctx.Part,
                    cprojRelationAfter, "F11_CPROJ_SOURCE_A", "F11_CPROJ_SOURCE_B");
                row["cproj_feature_after_reopen"] = reopenedCproj == null ? null : FeatureRecord(reopenedCproj, -1);
                Curve reopenedCprojSelected = FindCurveByName(ctx.Fixture.Sketch, "F11_CPROJ_SELECTED_OUTPUT");
                Curve reopenedCprojSibling = FindCurveByName(ctx.Fixture.Sketch, "F11_CPROJ_SIBLING_OUTPUT");
                row["cproj_output_identity_mapping"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "selected_output_found", reopenedCprojSelected != null },
                    { "sibling_output_found", reopenedCprojSibling != null },
                    { "selected_output_status", reopenedCprojSelected == null ? null : ReadCurveReferenceStatus(ctx.Fixture.Sketch, reopenedCprojSelected.Tag.ToString()) },
                    { "sibling_output_status", reopenedCprojSibling == null ? null : ReadCurveReferenceStatus(ctx.Fixture.Sketch, reopenedCprojSibling.Tag.ToString()) }
                };
                Require(ReopenedFixtureMatches(before, after),
                    "Direct state/geometry/dimension checks did not match after Save Copy/reopen.");
                Require(reopenedCproj != null && reopenedCprojSelected != null && reopenedCprojSibling != null &&
                    ReadCurveReferenceStatus(ctx.Fixture.Sketch, reopenedCprojSelected.Tag.ToString()) == "Reference" &&
                    ReadCurveReferenceStatus(ctx.Fixture.Sketch, reopenedCprojSibling.Tag.ToString()) == "Active" &&
                    Object.Equals(row["cproj_relation_resolves_to_named_sources_after_reopen"], true),
                    "Save Copy/reopen did not preserve the CPROJ feature, mixed sibling output states and source relation.");
                row["passed"] = true;
            });
    }

    private static Dictionary<string, object> CreatePersistentCprojFixture(Context context,
        Dictionary<string, object> row)
    {
        Line sourceA = context.Part.Curves.CreateLine(new Point3d(60.0, 10.0, 4.0), new Point3d(68.0, 10.0, 4.0));
        Line sourceB = context.Part.Curves.CreateLine(new Point3d(60.0, 15.0, 4.0), new Point3d(66.0, 15.0, 4.0));
        sourceA.SetName("F11_CPROJ_SOURCE_A"); sourceB.SetName("F11_CPROJ_SOURCE_B");
        V2SketchProjectionNativeResult projected;
        try
        {
            context.Fixture.Sketch.Activate(Sketch.ViewReorient.False);
            projected = V2SketchProjectionFactory.Execute(context.Part, context.Fixture.Sketch,
                new NXObject[] { sourceA, sourceB }, true);
            DeactivateIfActive(context.Session, context.Fixture.Sketch);
        }
        catch (Exception ex)
        {
            context.WritesHalted = true;
            row["persistent_cproj_fixture_exception"] = ExceptionRecord(ex);
            row["writes_must_remain_stopped"] = true;
            try { row["native_after_cproj_fixture_exception"] = NativeSnapshot(context, context.Fixture.Sketch); }
            catch (Exception readEx) { row["readback_after_cproj_fixture_exception"] = ExceptionRecord(readEx); }
            throw new UnknownOutcomeException("Native CPROJ fixture creation threw after entry; its mutation state is unclassified.");
        }
        Require(projected != null && projected.OutputCurves != null && projected.OutputCurves.Length == 2,
            "Persistent CPROJ fixture did not produce two output curves.");
        Line outputA = projected.OutputCurves[0] as Line;
        Line outputB = projected.OutputCurves[1] as Line;
        Require(outputA != null && outputB != null, "Persistent CPROJ fixture outputs are not both Lines.");
        if (outputA.StartPoint.Y > outputB.StartPoint.Y) { Line swap = outputA; outputA = outputB; outputB = swap; }
        outputA.SetName("F11_CPROJ_SELECTED_OUTPUT"); outputB.SetName("F11_CPROJ_SIBLING_OUTPUT");
        Feature feature = projected.CommitObject as Feature;
        if (feature == null && projected.CommittedObjects != null)
            foreach (NXObject item in projected.CommittedObjects) if (item is Feature) { feature = (Feature)item; break; }
        Require(feature != null, "Persistent CPROJ fixture did not return its native Feature.");
        feature.SetName("F11_CPROJ_PERSISTENT");
        RefreshBinding(context, row, "after persistent native CPROJ fixture");
        Dictionary<string, object> relation = ReadCprojSectionRelation(context.Part, feature);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "feature", FeatureRecord(feature, -1) }, { "native_factory_evidence", projected.Evidence },
            { "source_a", CurveRecord(sourceA) }, { "source_b", CurveRecord(sourceB) },
            { "selected_output", CurveRecord(outputA) }, { "sibling_output", CurveRecord(outputB) },
            { "selected_output_tag", outputA.Tag.ToString() }, { "sibling_output_tag", outputB.Tag.ToString() },
            { "section_relation", relation },
            { "relation_resolves_to_named_sources", CprojRelationMatchesNamedSources(context.Part, relation,
                "F11_CPROJ_SOURCE_A", "F11_CPROJ_SOURCE_B") }
        };
    }

    private static Curve FindPartCurveByName(Part part, string name)
    {
        foreach (Curve curve in part.Curves.ToArray())
            if (curve != null && String.Equals(curve.Name, name, StringComparison.Ordinal)) return curve;
        return null;
    }

    private static Feature FindFeatureByName(Part part, string name)
    {
        foreach (Feature feature in part.Features.GetFeatures())
            if (feature != null && String.Equals(feature.Name, name, StringComparison.Ordinal)) return feature;
        return null;
    }

    private static bool CprojRelationMatchesNamedSources(Part part, Dictionary<string, object> relation,
        string sourceAName, string sourceBName)
    {
        Curve sourceA = FindPartCurveByName(part, sourceAName), sourceB = FindPartCurveByName(part, sourceBName);
        List<object> inputs = relation == null ? null : relation["source_inputs"] as List<object>;
        if (sourceA == null || sourceB == null || inputs == null || inputs.Count != 2) return false;
        HashSet<string> matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (object item in inputs)
        {
            Dictionary<string, object> input = item as Dictionary<string, object>;
            if (input == null) return false;
            string tag = GetString(input, "selected_tag");
            string jid = GetString(input, "selected_journal_id");
            Curve current = FindCurveByTag(part, tag);
            if (String.Equals(tag, sourceA.Tag.ToString(), StringComparison.Ordinal) &&
                String.Equals(jid, sourceA.JournalIdentifier, StringComparison.Ordinal) && SameCurveGeometry(current, sourceA)) matched.Add(sourceAName);
            else if (String.Equals(tag, sourceB.Tag.ToString(), StringComparison.Ordinal) &&
                String.Equals(jid, sourceB.JournalIdentifier, StringComparison.Ordinal) && SameCurveGeometry(current, sourceB)) matched.Add(sourceBName);
        }
        return matched.Count == 2;
    }

    private static Dictionary<string, object> ExecuteConversion(Context context, Sketch sketch,
        string[] objectIds, string targetState, bool injectFault,
        Dictionary<string, object> parentRow, string label)
    {
        if (context.WritesHalted) throw new UnknownOutcomeException("The harness write gate is halted after an UNKNOWN result.");
        M3SketchReferenceConversionRequest request = MakeReferenceRequest(context, sketch, objectIds, targetState);
        M3PlanSnapshot plan = context.ReferenceAdapter.PrepareSketchReferenceConversion(context.Binding, request);
        Require(plan != null && plan.AdapterState != null, "F11 Prepare returned no retained plan for " + label + ".");
        if (injectFault) SetBooleanMember(plan.AdapterState, "ForcePostCommitVerificationFailureForTesting", true);
        Dictionary<string, object> beforeExecuteSnapshot;
        try { beforeExecuteSnapshot = NativeSnapshot(context, sketch); }
        catch (Exception ex)
        {
            context.WritesHalted = true;
            throw new UnknownOutcomeException("Direct baseline snapshot failed immediately before F11 ExecutePlan for " + label + "; no model write is issued.");
        }
        bool dirtyBeforeExecute = context.Part.IsModified;
        M3ExecutionResult execution = null;
        try { execution = ((IM3OperationAdapter)context.Adapter).ExecutePlan(context.Binding, plan); }
        catch (Exception ex)
        {
            context.WritesHalted = true;
            Dictionary<string, object> operationFailure = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "label", label }, { "request", RequestRecord(request) }, { "plan", plan.PublicView },
                { "execute_exception", ExceptionRecord(ex) }, { "operation_outcome", "UNKNOWN" },
                { "writes_must_remain_stopped", true }
            };
            try { operationFailure["native_after_unclassified_execute_exception"] = NativeSnapshot(context, sketch); }
            catch (Exception readEx) { operationFailure["readback_after_unclassified_execute_exception"] = ExceptionRecord(readEx); }
            parentRow[label] = operationFailure;
            throw new UnknownOutcomeException("ExecutePlan threw after entry for " + label + "; direct outcome is unclassified, no cleanup write or replay is allowed.");
        }
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "label", label }, { "request", RequestRecord(request) }, { "plan", plan.PublicView },
            { "execution", ExecutionRecord(execution) },
            { "native_operation_called", FindNestedValue(execution == null ? null : execution.Result, "native_operation_called") },
            { "native_input_tags", FindNestedValue(execution == null ? null : execution.Result, "input_tags") },
            { "builder_output_state", FindNestedValue(execution == null ? null : execution.Result, "output_state_readback") },
            { "builder_commit_method", FindNestedValue(execution == null ? null : execution.Result, "commit_method") },
            { "revision_after", context.Binding.RevisionId }
        };
        parentRow[label] = record;
        Dictionary<string, object> retained = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (string fieldName in new string[] { "FeatureInventoryBefore", "FeatureInventoryAfter",
            "HistoryBefore", "HistoryAfter", "SemanticBeforeNormalized", "SemanticAfterNormalized" })
        {
            FieldInfo field = plan.AdapterState.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null) retained[fieldName] = field.GetValue(plan.AdapterState);
        }
        record["retained_product_snapshots"] = retained;
        if (execution == null || execution.State == "UNKNOWN")
        {
            context.WritesHalted = true;
            record["operation_outcome"] = "UNKNOWN";
            record["writes_must_remain_stopped"] = true;
            throw new UnknownOutcomeException("F11 conversion returned UNKNOWN for " + label + "; no replay is allowed.");
        }
        if (execution.State == "ROLLED_BACK" || execution.State == "UNCHANGED")
        {
            Dictionary<string, object> productRestore = FindNestedDictionary(execution.Result, "rollback");
            if (productRestore == null) productRestore = FindNestedDictionary(execution.Result, "evidence");
            Dictionary<string, object> afterExecute;
            try { afterExecute = NativeSnapshot(context, sketch); }
            catch (Exception ex)
            {
                context.WritesHalted = true;
                record["native_after_read_exception"] = ExceptionRecord(ex);
                record["operation_outcome"] = "UNKNOWN";
                record["writes_must_remain_stopped"] = true;
                throw new UnknownOutcomeException("Direct native readback failed after F11 returned " + execution.State + " for " + label + ".");
            }
            bool directRestored = SameExceptRuntime(beforeExecuteSnapshot, afterExecute) && dirtyBeforeExecute == context.Part.IsModified;
            bool productRestored = execution.State == "UNCHANGED" || RollbackEvidenceComplete(productRestore);
            record["known_no_mutation_or_product_rollback"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "direct_snapshot_restored", directRestored }, { "product_rollback_evidence_complete", productRestored },
                { "native_before", beforeExecuteSnapshot }, { "native_after", afterExecute },
                { "part_modified_before", dirtyBeforeExecute }, { "part_modified_restored", dirtyBeforeExecute == context.Part.IsModified }
            };
            if (!directRestored || !productRestored)
            {
                context.WritesHalted = true;
                record["operation_outcome"] = "UNKNOWN";
                record["writes_must_remain_stopped"] = true;
                throw new UnknownOutcomeException("F11 execution returned " + execution.State + " but full no-mutation/rollback restoration was not proven for " + label + ".");
            }
            throw new InvalidOperationException("Expected APPLIED for " + label + "; observed known " + execution.State + " with restoration proven.");
        }
        if (execution.State != "APPLIED" && !injectFault)
        {
            context.WritesHalted = true;
            record["operation_outcome"] = "UNKNOWN";
            record["writes_must_remain_stopped"] = true;
            throw new UnknownOutcomeException("F11 returned an unclassified terminal state " + execution.State + " for " + label + ".");
        }
        if (!injectFault && execution.State == "APPLIED")
        {
            M3VerificationResult verification;
            try { verification = ((IM3OperationAdapter)context.Adapter).VerifyPlan(context.Binding, plan); }
            catch (Exception ex)
            {
                context.WritesHalted = true;
                record["verify_exception"] = ExceptionRecord(ex);
                record["operation_outcome"] = "UNKNOWN";
                record["writes_must_remain_stopped"] = true;
                try { record["native_after_verify_exception"] = NativeSnapshot(context, sketch); }
                catch (Exception readEx) { record["readback_after_verify_exception"] = ExceptionRecord(readEx); }
                throw new UnknownOutcomeException("F11 VerifyPlan threw after APPLIED for " + label + "; state is UNKNOWN and must not be replayed.");
            }
            record["verification"] = VerificationRecord(verification);
            if (verification == null || !verification.Verified)
            {
                context.WritesHalted = true;
                record["operation_outcome"] = "UNKNOWN";
                record["writes_must_remain_stopped"] = true;
                try { record["native_after_unverified_result"] = NativeSnapshot(context, sketch); }
                catch (Exception readEx) { record["readback_after_unverified_result"] = ExceptionRecord(readEx); }
                throw new UnknownOutcomeException("F11 VerifyPlan did not verify APPLIED for " + label + "; no harness Undo or retry is allowed.");
            }
        }
        return record;
    }

    private static M3SketchReferenceConversionRequest MakeReferenceRequest(Context context,
        Sketch sketch, string[] objectIds, string targetState)
    {
        // Fixture edits/readbacks and Undo can advance the binding. Query the
        // current revision before constructing a new request; never retry an edit.
        context.Adapter.GetSession(context.Binding);
        return new M3SketchReferenceConversionRequest
        {
            RevisionId = context.Binding.RevisionId,
            SketchId = sketch.JournalIdentifier,
            ObjectIds = new List<string>(objectIds ?? new string[0]),
            TargetState = targetState
        };
    }

    private static bool RestoreHarnessUndo(Context context, Session.UndoMarkId mark,
        Dictionary<string, object> before, bool dirtyBefore, Dictionary<string, object> row, string label)
    {
        bool attempted = true, succeeded = false, updateSucceeded = false, full = false;
        bool safeToCloseMark = false;
        try
        {
            if (context.Session.ActiveSketch != null)
                context.Session.ActiveSketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            context.Session.UndoToMark(mark, null);
            succeeded = true;
            int errors = context.Session.UpdateManager.DoUpdate(mark);
            row[label + "_harness_undo_update_error_count"] = errors;
            updateSucceeded = errors == 0;
            Dictionary<string, object> after = NativeSnapshot(context, context.Fixture == null ? null : context.Fixture.Sketch);
            row[label + "_native_after_harness_undo"] = after;
            bool direct = SameExceptRuntime(before, after);
            bool dirty = context.Part.IsModified == dirtyBefore;
            bool activeSketchRestored = context.Session.ActiveSketch == null;
            full = succeeded && updateSucceeded && direct && dirty && activeSketchRestored;
            row[label + "_harness_undo"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "attempted", attempted }, { "succeeded", succeeded }, { "update_succeeded", updateSucceeded },
                { "direct_full_snapshot_restored", direct }, { "dirty_state_restored", dirty },
                { "active_sketch_restored", activeSketchRestored }, { "full_restoration_proven", full }
            };
            safeToCloseMark = full;
            if (!full)
            {
                row["operation_outcome"] = "UNKNOWN";
                row["writes_must_remain_stopped"] = true;
                context.WritesHalted = true;
            }
        }
        catch (Exception ex)
        {
            row[label + "_harness_undo_error"] = ExceptionRecord(ex);
            row["operation_outcome"] = "UNKNOWN";
            row["writes_must_remain_stopped"] = true;
            context.WritesHalted = true;
        }
        finally
        {
            if (safeToCloseMark)
            {
                try { context.Session.DeleteUndoMark(mark, null); row[label + "_harness_undo_mark_closed"] = true; }
                catch (Exception ex)
                {
                    row[label + "_harness_delete_undo_mark_error"] = ExceptionRecord(ex); full = false;
                    row["operation_outcome"] = "UNKNOWN"; row["writes_must_remain_stopped"] = true;
                    context.WritesHalted = true;
                }
            }
            else row[label + "_harness_undo_mark_retained"] = true;
        }
        return full;
    }

    private static void RefreshBinding(Context context, Dictionary<string, object> row, string label)
    {
        if (context.WritesHalted) return;
        string before = context.Binding.RevisionId;
        Dictionary<string, object> info = context.Adapter.GetSession(context.Binding);
        row[label + "_session_readback"] = info;
        row[label + "_revision_before_read"] = before;
        row[label + "_revision_after_read"] = context.Binding.RevisionId;
        context.Report["current_part_path"] = context.Part.FullPath;
        context.Report["current_revision"] = context.Binding.RevisionId;
    }

    private static void RunCase(Context context, string testId, string objective,
        string procedure, string passCriteria, CaseAction action)
    {
        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test_id", testId }, { "layer", "native_subsystem_acceptance" },
            { "objective", objective }, { "setup_and_procedure", procedure },
            { "pass_fail_criteria", passCriteria }, { "status", "RUNNING" }, { "passed", false },
            { "configuration", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "nx_release", GetString(GetDictionary(context.Report, "nx_configuration"), "recorded_release") },
                    { "units", "millimeter" }, { "position_tolerance_mm", PositionToleranceMm },
                    { "angular_tolerance_rad", AngularToleranceRad },
                    { "sample_size", 1 }, { "instrument_calibration", "Not applicable; no physical instrument." }
                } }
        };
        context.Cases.Add(row);
        if (context.WritesHalted)
        {
            row["status"] = "SKIPPED";
            row["skip_reason"] = "A prior native result is UNKNOWN; model writes are halted and replay is prohibited.";
            Persist(GetString(context.Report, "report_path"), context.Report);
            return;
        }
        try
        {
            action(context, row);
            if (context.WritesHalted)
            {
                row["passed"] = false; row["status"] = "UNKNOWN";
                row["operation_outcome"] = "UNKNOWN"; row["writes_must_remain_stopped"] = true;
            }
            else if (row["status"] == null || String.Equals(Convert.ToString(row["status"], CultureInfo.InvariantCulture), "RUNNING", StringComparison.Ordinal))
                row["status"] = Object.Equals(GetValue(row, "passed"), true) ? "PASSED" : "FAILED";
        }
        catch (SkipCaseException ex)
        {
            row["passed"] = false; row["status"] = "SKIPPED"; row["skip_reason"] = ex.Message;
        }
        catch (UnknownOutcomeException ex)
        {
            row["passed"] = false; row["status"] = "UNKNOWN"; row["operation_outcome"] = "UNKNOWN";
            row["writes_must_remain_stopped"] = true; row["error"] = ExceptionRecord(ex);
            context.WritesHalted = true;
        }
        catch (Exception ex)
        {
            if (context.WritesHalted)
            {
                row["passed"] = false; row["status"] = "UNKNOWN"; row["operation_outcome"] = "UNKNOWN";
                row["writes_must_remain_stopped"] = true; row["error"] = ExceptionRecord(ex);
                Persist(GetString(context.Report, "report_path"), context.Report);
                return;
            }
            row["passed"] = false; row["status"] = "FAILED";
            Dictionary<string, object> defect = DefectRecord(testId, ex,
                procedure + " Capture the exact request, native return/exception, direct before/after snapshot, solver/DOF, dirty state, and Undo evidence.",
                "Preserve the original model/report. Reproduce this exact case on a fresh unique millimetre part; correct the identified cause; retest this ID plus state-preservation, solver behavior, actual Undo, and Save Copy/reopen. Keep the original failure evidence.");
            row["error"] = defect["observed_error"];
            row["reproduction_steps"] = defect["reproduction_steps"];
            row["retest_criterion"] = defect["retest_criterion"];
            context.Defects.Add(defect);
        }
        Persist(GetString(context.Report, "report_path"), context.Report);
    }

    private static void AddSkippedCasesAfterUnknown(Context context, string reason)
    {
        string[] ids = new string[]
        {
            "F11-N03-whole_sketch_section_reference_filter", "F11-N06-radial_dimension_explicit_rejection",
            "F11-N02-horizontal_driving_dimension_behavior_and_undo", "F11-N06-vertical_linear_dimension_bidirectional",
            "F11-N10-cproj_sibling_output_isolation", "F11-N08-product_undo_after_injected_postcondition_failure",
            "F11-N12-conflicting_linear_dimensions_reject_without_constraint_loss", "F11-N09-save_copy_reopen_state_persistence"
        };
        HashSet<string> existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (object item in context.Cases)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            if (row != null) existing.Add(GetString(row, "test_id"));
        }
        foreach (string id in ids)
        {
            if (existing.Contains(id)) continue;
            context.Cases.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "test_id", id }, { "layer", "native_subsystem_acceptance" },
                { "status", "SKIPPED" }, { "passed", false }, { "skip_reason", reason },
                { "writes_halted", true }, { "reproduction_steps", new string[] { "Retain the UNKNOWN model and evidence.", "Obtain explicit human recovery before another native run." } },
                { "retest_criterion", "Run this test on a fresh unique part only after human recovery confirms the prior model state and the same F11 configuration." }
            });
        }
    }

    private static Dictionary<string, object> NativeSnapshot(Context context, Sketch primarySketch)
    {
        Part part = context.Part;
        Session session = context.Session;
        List<object> curves = new List<object>();
        Curve[] nativeCurves = part.Curves.ToArray();
        if (nativeCurves == null) throw new InvalidOperationException("Part.Curves.ToArray returned null.");
        foreach (Curve curve in nativeCurves)
        {
            if (curve == null) throw new InvalidOperationException("Part curve inventory contains null.");
            curves.Add(CurveRecord(curve));
        }
        List<object> sketches = new List<object>();
        foreach (Sketch sketch in part.Sketches.ToArray()) sketches.Add(SketchRecord(sketch));
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "part_tag", part.Tag.ToString() }, { "part_path", part.FullPath }, { "part_units", part.PartUnits.ToString() },
            { "part_modified", part.IsModified }, { "part_tolerances", PartToleranceRecord(part) },
            { "active_sketch_tag", session.ActiveSketch == null ? null : session.ActiveSketch.Tag.ToString() },
            { "primary_sketch_tag", primarySketch == null ? null : primarySketch.Tag.ToString() },
            { "primary_sketch_journal_id", primarySketch == null ? null : primarySketch.JournalIdentifier },
            { "all_part_curves", curves }, { "sketches", sketches },
            { "features_in_history_order", FeatureRecords(part) }, { "bodies", BodyRecords(part) },
            { "body_measurement_scope", part.Bodies.ToArray().Length == 0 ? "empty_body_inventory; volume_and_bbox_not_applicable" : "body identifiers/solid state captured; no volume/bbox measurement claimed" },
            { "expressions", ExpressionRecords(part) },
            { "adapter_revision_id", context.Binding == null ? null : context.Binding.RevisionId },
            { "ownership", context.Binding == null ? null : new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "artifact_scope", context.Binding.ArtifactScope }, { "part_path", context.Binding.PartPath },
                    { "part_tag", context.Binding.PartTag }, { "is_read_only", context.Binding.IsReadOnly }
                } },
            { "write_gate", new Dictionary<string, object>(StringComparer.Ordinal)
                { { "harness_writes_halted", context.WritesHalted }, { "m2bridge_write_gate", "NOT_EXERCISED_BY_DIRECT_ADAPTER_HARNESS" } } }
        };
    }

    private static Dictionary<string, object> SketchRecord(Sketch sketch)
    {
        if (sketch == null) throw new InvalidOperationException("Sketch inventory contains null.");
        int dof;
        Sketch.Status status = sketch.GetStatus(out dof);
        List<object> geometry = new List<object>();
        List<object> curveStatuses = new List<object>();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve;
            if (curve == null) continue;
            geometry.Add(CurveRecord(curve));
            curveStatuses.Add(CurveStatusRecord(sketch, curve));
        }
        List<object> constraints = ConstraintRecords(sketch);
        Matrix3x3 axes = sketch.Orientation.Element;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", sketch.Tag.ToString() }, { "journal_id", sketch.JournalIdentifier }, { "name", sketch.Name },
            { "feature_tag", sketch.Feature == null ? null : sketch.Feature.Tag.ToString() },
            { "feature_journal_id", sketch.Feature == null ? null : sketch.Feature.JournalIdentifier },
            { "feature_name", sketch.Feature == null ? null : sketch.Feature.Name },
            { "origin_mm", Point(sketch.Origin) },
            { "axes", new double[] { axes.Xx, axes.Xy, axes.Xz, axes.Yx, axes.Yy, axes.Yz, axes.Zx, axes.Zy, axes.Zz } },
            { "geometry", geometry }, { "curve_reference_status", curveStatuses },
            { "constraints", constraints },
            { "solver", new Dictionary<string, object>(StringComparer.Ordinal) { { "status", status.ToString() }, { "status_numeric", Convert.ToInt32(status, CultureInfo.InvariantCulture) }, { "degrees_of_freedom", dof }, { "source", "Sketch.GetStatus cached direct API read" } } }
        };
    }

    private static Dictionary<string, object> CurveStatusRecord(Sketch sketch, Curve curve)
    {
        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", curve.Tag.ToString() }, { "journal_id", curve.JournalIdentifier }, { "type", curve.GetType().Name }
        };
        try
        {
            UFSket.ReferenceStatus status;
            UFSession.GetUFSession().Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status);
            row["status"] = status.ToString();
            row["status_value"] = Convert.ToInt32(status, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) { row["read_error"] = ex.GetType().FullName + ": " + ex.Message; }
        return row;
    }

    private static List<object> ConstraintRecords(Sketch sketch)
    {
        List<object> rows = new List<object>();
        SketchConstraint[] found = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, Sketch.ConstraintType.NoCon);
        if (found == null) throw new InvalidOperationException("Sketch.GetAllConstraintsOfType(Any, NoCon) returned null.");
        foreach (SketchConstraint constraint in found)
        {
            if (constraint == null) throw new InvalidOperationException("Sketch constraint inventory contains null.");
            SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
            Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", constraint.Tag.ToString() }, { "journal_id", constraint.JournalIdentifier },
                { "name", constraint.Name }, { "type", constraint.ConstraintType.ToString() }, { "class", constraint.GetType().Name }
            };
            if (dimension != null) record["dimension"] = ReadDimensionRecord(dimension);
            List<object> operands = new List<object>();
            if (dimension != null)
            {
                Sketch.DimensionGeometry[] geometry = dimension.GetDimensionGeometry();
                if (geometry != null)
                    foreach (Sketch.DimensionGeometry item in geometry)
                        operands.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "geometry_tag", item.Geometry == null ? null : item.Geometry.Tag.ToString() },
                            { "geometry_journal_id", item.Geometry == null ? null : item.Geometry.JournalIdentifier },
                            { "association", item.AssocType.ToString() }, { "association_value", item.AssocValue }
                        });
            }
            else
            {
                SketchGeometricConstraint geometric = constraint as SketchGeometricConstraint;
                if (geometric != null)
                {
                    Sketch.ConstraintGeometry[] geometry = geometric.GetGeometry();
                    if (geometry != null)
                        foreach (Sketch.ConstraintGeometry item in geometry)
                            operands.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                { "geometry_tag", item.Geometry == null ? null : item.Geometry.Tag.ToString() },
                                { "geometry_journal_id", item.Geometry == null ? null : item.Geometry.JournalIdentifier },
                                { "point_type", item.PointType.ToString() }, { "spline_defining_point_index", item.SplineDefiningPointIndex }
                            });
                }
            }
            record["operands"] = operands;
            rows.Add(record);
        }
        return SortedRows(rows, "tag");
    }

    private static Dictionary<string, object> ReadDimensionRecord(SketchDimensionalConstraint constraint)
    {
        Expression expression = constraint == null ? null : constraint.AssociatedExpression;
        Dimension associated = constraint == null ? null : constraint.AssociatedDimension;
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", constraint == null ? null : constraint.Tag.ToString() },
            { "journal_id", constraint == null ? null : constraint.JournalIdentifier },
            { "constraint_name", constraint == null ? null : constraint.Name },
            { "constraint_type", constraint == null ? null : constraint.ConstraintType.ToString() },
            { "dimension_state", constraint == null ? null : constraint.DimensionState.ToString() },
            { "associated_dimension_tag", associated == null ? null : associated.Tag.ToString() },
            { "associated_dimension_journal_id", associated == null ? null : associated.JournalIdentifier },
            { "associated_dimension_type", associated == null ? null : associated.GetType().Name },
            { "expression_tag", expression == null ? null : expression.Tag.ToString() },
            { "expression_journal_id", expression == null ? null : expression.JournalIdentifier },
            { "expression_name", expression == null ? null : expression.Name },
            { "expression_rhs", expression == null ? null : expression.RightHandSide },
            { "expression_value", expression == null ? null : (object)expression.Value },
            { "expression_unit", expression == null || expression.Units == null ? null : expression.Units.Symbol },
            { "expression_flags", expression == null ? null : ReadExpressionFlags(expression) },
            { "associated_dimension_computed_size", TryReadNumericMember(associated, "ComputedSize") },
            { "associated_dimension_computed_size_query", ReadMemberStatus(associated, "ComputedSize") },
            { "associated_dimension_is_driving", TryInvokeBooleanMember(associated, "IsDriving") },
            { "associated_dimension_is_driving_query", ReadMethodStatus(associated, "IsDriving") }
        };
        return result;
    }

    private static Dictionary<string, object> ReadExpressionFlags(Expression expression)
    {
        Dictionary<string, object> flags = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (string name in ExpressionFlagNames)
        {
            try
            {
                PropertyInfo property = expression.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null) throw new MissingMemberException(expression.GetType().FullName, name);
                flags[name] = property.GetValue(expression, null);
            }
            catch (Exception ex) { flags[name] = "QUERY_ERROR: " + ex.GetType().Name + ": " + ex.Message; }
        }
        return flags;
    }

    private static object TryReadNumericMember(object target, string name)
    {
        try
        {
            PropertyInfo property = target == null ? null : target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null) return null;
            object value = property.GetValue(target, null);
            return value == null ? (object)null : (object)Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    private static object ReadMemberStatus(object target, string name)
    {
        try
        {
            PropertyInfo property = target == null ? null : target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null) return "UNAVAILABLE";
            object value = property.GetValue(target, null);
            return value == null ? "NULL" : "READ_OK";
        }
        catch (Exception ex) { return "QUERY_ERROR: " + ex.GetType().Name + ": " + ex.Message; }
    }

    private static object TryInvokeBooleanMember(object target, string name)
    {
        try
        {
            MethodInfo method = target == null ? null : target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (method == null) return null;
            return Convert.ToBoolean(method.Invoke(target, null), CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    private static object ReadMethodStatus(object target, string name)
    {
        try
        {
            MethodInfo method = target == null ? null : target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (method == null) return "UNAVAILABLE";
            method.Invoke(target, null);
            return "READ_OK";
        }
        catch (Exception ex) { return "QUERY_ERROR: " + ex.GetType().Name + ": " + ex.Message; }
    }

    private static Dictionary<string, object> CurveRecord(Curve curve)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", curve.Tag.ToString() }, { "journal_id", curve.JournalIdentifier },
            { "name", curve.Name }, { "type", curve.GetType().Name }, { "length_mm", curve.GetLength() }
        };
        Line line = curve as Line;
        if (line != null)
        {
            record["start_mm"] = Point(line.StartPoint); record["end_mm"] = Point(line.EndPoint);
        }
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center; Vector3d axisX, axisY; arc.GetOrientation(out center, out axisX, out axisY);
            record["center_mm"] = Point(center); record["radius_mm"] = arc.Radius;
            record["start_angle_rad"] = arc.StartAngle; record["end_angle_rad"] = arc.EndAngle;
            record["axis_x"] = Vector(axisX); record["axis_y"] = Vector(axisY);
        }
        Ellipse ellipse = curve as Ellipse;
        if (ellipse != null)
        {
            double major, minor, start, end, rotation; Point3d center;
            ellipse.GetParameters(out major, out minor, out start, out end, out rotation, out center);
            Point3d orientationCenter; Vector3d axisX, axisY;
            ellipse.GetOrientation(out orientationCenter, out axisX, out axisY);
            record["center_mm"] = Point(center); record["major_radius_mm"] = major; record["minor_radius_mm"] = minor;
            record["start_angle_rad"] = start; record["end_angle_rad"] = end; record["rotation_rad"] = rotation;
            record["plane_axis_x"] = Vector(axisX); record["plane_axis_y"] = Vector(axisY);
        }
        return record;
    }

    private static List<object> CurveRecords(Curve[] curves)
    {
        List<object> rows = new List<object>();
        if (curves == null) return rows;
        foreach (Curve curve in curves)
        {
            if (curve == null) throw new InvalidOperationException("Curve array contains null.");
            rows.Add(CurveRecord(curve));
        }
        return SortedRows(rows, "tag");
    }

    private static List<object> FeatureRecords(Part part)
    {
        Feature[] features = part.Features.GetFeatures();
        if (features == null) throw new InvalidOperationException("Feature history returned null.");
        List<object> rows = new List<object>();
        for (int i = 0; i < features.Length; i++) rows.Add(FeatureRecord(features[i], i));
        return rows;
    }

    private static Dictionary<string, object> FeatureRecord(Feature feature, int index)
    {
        List<string> parents = FeatureIds(feature.GetParents());
        List<string> children = FeatureIds(feature.GetChildren());
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "history_index", index }, { "tag", feature.Tag.ToString() }, { "journal_id", feature.JournalIdentifier },
            { "name", feature.Name }, { "type", feature.GetType().Name }, { "feature_type", feature.FeatureType },
            { "parents", parents }, { "children", children }
        };
    }

    private static List<string> FeatureIds(Feature[] features)
    {
        List<string> ids = new List<string>();
        if (features != null) foreach (Feature feature in features) ids.Add(feature == null ? null : feature.Tag.ToString() + "/" + feature.JournalIdentifier);
        ids.Sort(StringComparer.Ordinal); return ids;
    }

    private static List<object> BodyRecords(Part part)
    {
        List<object> rows = new List<object>();
        foreach (Body body in part.Bodies.ToArray())
        {
            if (body == null) throw new InvalidOperationException("Body inventory contains null.");
            rows.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", body.Tag.ToString() }, { "journal_id", body.JournalIdentifier },
                { "name", body.Name }, { "type", body.GetType().Name }, { "is_solid", body.IsSolidBody }
            });
        }
        return SortedRows(rows, "tag");
    }

    private static List<object> ExpressionRecords(Part part)
    {
        List<object> rows = new List<object>();
        foreach (Expression expression in part.Expressions.ToArray())
        {
            if (expression == null) throw new InvalidOperationException("Expression inventory contains null.");
            rows.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", expression.Tag.ToString() }, { "journal_id", expression.JournalIdentifier },
                { "name", expression.Name }, { "right_hand_side", expression.RightHandSide },
                { "value", expression.Value }, { "unit", expression.Units == null ? null : expression.Units.Symbol },
                { "flags", ReadExpressionFlags(expression) }
            });
        }
        return SortedRows(rows, "tag");
    }

    private static Dictionary<string, object> PartToleranceRecord(Part part)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "distance_tolerance", TryReadNumericMember(part, "DistanceTolerance") },
            { "distance_tolerance_query", ReadMemberStatus(part, "DistanceTolerance") },
            { "angle_tolerance", TryReadNumericMember(part, "AngleTolerance") },
            { "angle_tolerance_query", ReadMemberStatus(part, "AngleTolerance") },
            { "units", part.PartUnits.ToString() }
        };
    }

    private static Dictionary<string, object> ExecuteStatusRecord(Sketch sketch)
    {
        int dof; Sketch.Status status = sketch.GetStatus(out dof);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", status.ToString() }, { "status_numeric", Convert.ToInt32(status, CultureInfo.InvariantCulture) },
            { "degrees_of_freedom", dof }, { "source", "Sketch.GetStatus direct API" }
        };
    }

    private static void ActivateSketch(Context context, Sketch sketch)
    {
        if (context.Session.ActiveSketch == null || !context.Session.ActiveSketch.Tag.Equals(sketch.Tag))
            sketch.Activate(Sketch.ViewReorient.False);
    }

    private static Dictionary<string, object> CalculateAndDeactivate(Context context, Sketch sketch)
    {
        Dictionary<string, object> result = ApplyWholeSketchRegionAndCalculate(context, sketch);
        DeactivateIfActive(context.Session, sketch);
        return result;
    }

    private static Dictionary<string, object> ApplyWholeSketchRegionAndCalculate(Context context, Sketch sketch)
    {
        SketchWorkRegionBuilder builder = null;
        Exception failure = null;
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        try
        {
            builder = context.Part.Sketches.CreateWorkRegionBuilder();
            Require(builder != null, "NX returned no SketchWorkRegionBuilder.");
            builder.Scope = SketchWorkRegionBuilder.ScopeType.EntireSketch;
            string configuredScope = builder.Scope.ToString();
            bool valid = builder.Validate();
            Require(valid && String.Equals(configuredScope, "EntireSketch", StringComparison.Ordinal),
                "Could not validate an explicit EntireSketch solver region.");
            object commit = builder.Commit();
            string committedScope = builder.Scope.ToString();
            Require(String.Equals(committedScope, "EntireSketch", StringComparison.Ordinal),
                "WorkRegionBuilder did not retain EntireSketch scope after Commit.");
            sketch.CalculateStatus();
            int dof;
            Sketch.Status status = sketch.GetStatus(out dof);
            result["solver_status"] = status.ToString();
            result["solver_status_numeric"] = Convert.ToInt32(status, CultureInfo.InvariantCulture);
            result["degrees_of_freedom"] = dof;
            result["source"] = "Sketch.CalculateStatus after validated and committed Scope=EntireSketch";
            result["work_region"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "builder_type", builder.GetType().FullName }, { "scope_before_commit", configuredScope },
                { "validate", valid }, { "commit_returned_null", commit == null },
                { "scope_after_commit", committedScope }, { "is_persistent_model_feature", false }
            };
        }
        catch (Exception ex)
        {
            failure = ex;
            result["calculation_error"] = ExceptionRecord(ex);
        }
        finally
        {
            if (builder != null)
            {
                try { builder.Destroy(); result["work_region_builder_destroyed"] = true; }
                catch (Exception ex) { result["work_region_builder_destroy_error"] = ExceptionRecord(ex); }
            }
        }
        if (failure != null) throw new InvalidOperationException("Whole-Sketch solver calculation failed.", failure);
        return result;
    }

    private static Dictionary<string, object> CalculateStatusWhileActive(Context context, Sketch sketch)
    {
        ActivateSketch(context, sketch);
        return CalculateAndDeactivate(context, sketch);
    }

    private static void DeactivateIfActive(Session session, Sketch sketch)
    {
        if (session.ActiveSketch != null && sketch != null && session.ActiveSketch.Tag.Equals(sketch.Tag))
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
    }

    private static Dictionary<string, object> DimensionBehaviorRecord(Context context, Fixture fixture)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "curve", CurveRecord(fixture.DrivingLine) },
            { "curve_reference_status", ReadCurveReferenceStatus(fixture.Sketch, fixture.DrivingLine.Tag.ToString()) },
            { "dimension", ReadDimensionRecord(fixture.HorizontalDimension) },
            { "solver_cached", ExecuteStatusRecord(fixture.Sketch) },
            { "part_modified", context.Part.IsModified }, { "binding_revision", context.Binding.RevisionId }
        };
    }

    private static string ReadCurveReferenceStatus(Sketch sketch, string tag)
    {
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve;
            if (curve == null || !String.Equals(curve.Tag.ToString(), tag, StringComparison.Ordinal)) continue;
            UFSket.ReferenceStatus status;
            UFSession.GetUFSession().Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status);
            return status.ToString();
        }
        throw new InvalidOperationException("Sketch curve Tag not found for UF reference-status read: " + tag);
    }

    private static List<Curve> CurvesInSketch(Sketch sketch, string targetState)
    {
        List<Curve> result = new List<Curve>();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve;
            if (curve != null && ReadCurveReferenceStatus(sketch, curve.Tag.ToString()) == targetState) result.Add(curve);
        }
        return result;
    }

    private static Dictionary<string, object> CurveEligibilityRecord(Sketch sketch)
    {
        List<object> active = new List<object>(), reference = new List<object>();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve; if (curve == null) continue;
            Dictionary<string, object> entry = new Dictionary<string, object>(CurveRecord(curve), StringComparer.Ordinal);
            string status = ReadCurveReferenceStatus(sketch, curve.Tag.ToString());
            entry["reference_status"] = status;
            if (status == "Active") active.Add(entry); else if (status == "Reference") reference.Add(entry);
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "active_curves", active }, { "reference_curves", reference },
            { "active_count", active.Count }, { "reference_count", reference.Count }
        };
    }

    private static Dictionary<string, object> MatchSectionCurves(List<Curve> active, List<Curve> outputs)
    {
        bool[] used = new bool[outputs.Count];
        List<object> matched = new List<object>(), missing = new List<object>();
        foreach (Curve source in active)
        {
            int match = -1;
            for (int i = 0; i < outputs.Count; i++) if (!used[i] && SameCurveGeometry(source, outputs[i])) { match = i; break; }
            if (match < 0) missing.Add(CurveRecord(source));
            else
            {
                used[match] = true;
                matched.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "source", CurveRecord(source) }, { "section_output_copy", CurveRecord(outputs[match]) },
                    { "geometric_match", true }, { "same_tag", source.Tag.Equals(outputs[match].Tag) }
                });
            }
        }
        List<object> extra = new List<object>();
        for (int i = 0; i < outputs.Count; i++) if (!used[i]) extra.Add(CurveRecord(outputs[i]));
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "one_to_one_match", active.Count == outputs.Count && missing.Count == 0 && extra.Count == 0 },
            { "expected_active_count", active.Count }, { "output_copy_count", outputs.Count },
            { "matched_pairs", matched }, { "missing_active_sources", missing }, { "unmatched_output_copies", extra },
            { "identity_basis", "type and direct geometric locus; output copy Tags are not source identity" }
        };
    }

    private static bool SectionTagsAreSourceTags(List<Curve> outputs, List<Curve> sources)
    {
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Curve curve in sources) tags.Add(curve.Tag.ToString());
        foreach (Curve curve in outputs) if (!tags.Contains(curve.Tag.ToString())) return false;
        return outputs.Count > 0;
    }

    private static Point3d CurveHelpPoint(Curve curve)
    {
        Line line = curve as Line; if (line != null) return line.StartPoint;
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center; Vector3d x, y; arc.GetOrientation(out center, out x, out y);
            double angle = arc.StartAngle;
            return new Point3d(center.X + arc.Radius * (x.X * Math.Cos(angle) + y.X * Math.Sin(angle)),
                center.Y + arc.Radius * (x.Y * Math.Cos(angle) + y.Y * Math.Sin(angle)),
                center.Z + arc.Radius * (x.Z * Math.Cos(angle) + y.Z * Math.Sin(angle)));
        }
        return new Point3d();
    }

    private static List<Curve> CurvesFromObjects(NXObject[] objects)
    {
        List<Curve> result = new List<Curve>();
        if (objects != null)
            foreach (NXObject item in objects)
            {
                Curve curve = item as Curve;
                if (curve == null) throw new InvalidOperationException("Section returned a non-Curve output object.");
                result.Add(curve);
            }
        return result;
    }

    private static bool SameCurveGeometry(Curve first, Curve second)
    {
        if (first == null || second == null || first.GetType() != second.GetType()) return false;
        Line a = first as Line, b = second as Line;
        if (a != null && b != null)
            return (Distance(Point(a.StartPoint), Point(b.StartPoint)) <= PositionToleranceMm && Distance(Point(a.EndPoint), Point(b.EndPoint)) <= PositionToleranceMm) ||
                (Distance(Point(a.StartPoint), Point(b.EndPoint)) <= PositionToleranceMm && Distance(Point(a.EndPoint), Point(b.StartPoint)) <= PositionToleranceMm);
        Arc arcA = first as Arc, arcB = second as Arc;
        if (arcA != null && arcB != null)
        {
            Point3d ca, cb; Vector3d xa, ya, xb, yb;
            arcA.GetOrientation(out ca, out xa, out ya); arcB.GetOrientation(out cb, out xb, out yb);
            bool fullA = Math.Abs(Math.Abs(arcA.EndAngle - arcA.StartAngle) - 2.0 * Math.PI) <= AngularToleranceRad;
            bool fullB = Math.Abs(Math.Abs(arcB.EndAngle - arcB.StartAngle) - 2.0 * Math.PI) <= AngularToleranceRad;
            if (Distance(Point(ca), Point(cb)) > PositionToleranceMm || Math.Abs(arcA.Radius - arcB.Radius) > PositionToleranceMm ||
                Math.Abs(arcA.GetLength() - arcB.GetLength()) > PositionToleranceMm || fullA != fullB) return false;
            if (fullA) return true;
            double sweepA = arcA.EndAngle - arcA.StartAngle, sweepB = arcB.EndAngle - arcB.StartAngle;
            Point3d midA = ArcPoint(arcA, arcA.StartAngle + sweepA / 2.0);
            Point3d midB = ArcPoint(arcB, arcB.StartAngle + sweepB / 2.0);
            return Distance(Point(midA), Point(midB)) <= PositionToleranceMm;
        }
        Ellipse ellipseA = first as Ellipse, ellipseB = second as Ellipse;
        if (ellipseA != null && ellipseB != null)
        {
            double majorA, minorA, startA, endA, rotationA; Point3d centerA;
            double majorB, minorB, startB, endB, rotationB; Point3d centerB;
            ellipseA.GetParameters(out majorA, out minorA, out startA, out endA, out rotationA, out centerA);
            ellipseB.GetParameters(out majorB, out minorB, out startB, out endB, out rotationB, out centerB);
            return Distance(Point(centerA), Point(centerB)) <= PositionToleranceMm &&
                Math.Abs(majorA - majorB) <= PositionToleranceMm && Math.Abs(minorA - minorB) <= PositionToleranceMm &&
                Math.Abs(rotationA - rotationB) <= AngularToleranceRad &&
                Math.Abs((endA - startA) - (endB - startB)) <= AngularToleranceRad &&
                Math.Abs(ellipseA.GetLength() - ellipseB.GetLength()) <= PositionToleranceMm;
        }
        return Math.Abs(first.GetLength() - second.GetLength()) <= PositionToleranceMm;
    }

    private static Point3d ArcPoint(Arc arc, double angle)
    {
        Point3d center; Vector3d x, y; arc.GetOrientation(out center, out x, out y);
        return new Point3d(center.X + arc.Radius * (x.X * Math.Cos(angle) + y.X * Math.Sin(angle)),
            center.Y + arc.Radius * (x.Y * Math.Cos(angle) + y.Y * Math.Sin(angle)),
            center.Z + arc.Radius * (x.Z * Math.Cos(angle) + y.Z * Math.Sin(angle)));
    }

    private static Dictionary<string, object> ReadCprojSectionRelation(Part part, Feature feature)
    {
        SketchProjectBuilder builder = null;
        try
        {
            builder = part.Sketches.CreateProjectBuilder(feature);
            Require(builder != null && builder.Section != null, "CPROJ feature has no readable SketchProjectBuilder Section.");
            SectionData[] sections; builder.Section.GetSectionData(out sections);
            List<object> inputs = new List<object>();
            if (sections != null)
                for (int i = 0; i < sections.Length; i++)
                {
                    SectionElementData[] elements; sections[i].GetSectionElementsData(out elements);
                    if (elements == null) continue;
                    for (int j = 0; j < elements.Length; j++)
                    {
                        DisplayableObject selected, startConnector, endConnector; Point3d startPoint, endPoint;
                        elements[j].GetSectionElementData1(out selected, out startConnector, out startPoint, out endConnector, out endPoint);
                        inputs.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "selected_tag", selected == null ? null : selected.Tag.ToString() },
                            { "selected_journal_id", selected == null ? null : selected.JournalIdentifier },
                            { "selected_type", selected == null ? null : selected.GetType().Name },
                            { "start_mm", Point(startPoint) }, { "end_mm", Point(endPoint) }
                        });
                    }
                }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "feature_tag", feature.Tag.ToString() }, { "feature_journal_id", feature.JournalIdentifier },
                { "section_count", sections == null ? 0 : sections.Length }, { "source_inputs", inputs }
            };
        }
        finally { if (builder != null) builder.Destroy(); }
    }

    private static Dictionary<string, object> SnapshotWithoutStatuses(Dictionary<string, object> snapshot)
    {
        return (Dictionary<string, object>)TransformSnapshot(snapshot, false);
    }

    private static object TransformSnapshot(object value, bool keepMutableState)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                string key = pair.Key.ToLowerInvariant();
                if (!keepMutableState && (key == "adapter_revision_id" || key == "part_modified" || key == "is_modified" ||
                    key == "status_value" || key == "curve_reference_status" || key == "dimension_state" || key == "associated_dimension_is_driving"))
                {
                    if (key == "curve_reference_status") result[pair.Key] = TransformStatusList(pair.Value);
                    continue;
                }
                result[pair.Key] = TransformSnapshot(pair.Value, keepMutableState);
            }
            return result;
        }
        List<object> list = value as List<object>;
        if (list != null)
        {
            List<object> result = new List<object>();
            foreach (object item in list) result.Add(TransformSnapshot(item, keepMutableState));
            return result;
        }
        return value;
    }

    private static object TransformStatusList(object value)
    {
        List<object> list = value as List<object>;
        if (list == null) return value;
        List<object> result = new List<object>();
        foreach (object item in list)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            if (row == null) { result.Add(item); continue; }
            Dictionary<string, object> copy = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in row) if (pair.Key != "status" && pair.Key != "status_value") copy[pair.Key] = pair.Value;
            result.Add(copy);
        }
        return result;
    }

    private static bool SameExceptRuntime(Dictionary<string, object> before, Dictionary<string, object> after)
    {
        return SameValue(TransformSnapshot(before, false), TransformSnapshot(after, false)) &&
            SameValue(StateMap(before), StateMap(after));
    }

    private static bool SameExceptTargetState(Dictionary<string, object> before, Dictionary<string, object> after,
        HashSet<string> allowedStatusKeys)
    {
        if (before == null || after == null) return false;
        Dictionary<string, object> normalizedBefore = (Dictionary<string, object>)TransformSnapshot(before, false);
        Dictionary<string, object> normalizedAfter = (Dictionary<string, object>)TransformSnapshot(after, false);
        if (!SameValue(normalizedBefore, normalizedAfter)) return false;
        Dictionary<string, string> beforeStates = StateMap(before), afterStates = StateMap(after);
        if (beforeStates.Count != afterStates.Count) return false;
        foreach (KeyValuePair<string, string> pair in beforeStates)
        {
            string post;
            if (!afterStates.TryGetValue(pair.Key, out post)) return false;
            if (!allowedStatusKeys.Contains(pair.Key) && !String.Equals(pair.Value, post, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static Dictionary<string, string> StateMap(Dictionary<string, object> snapshot)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        List<object> sketches = snapshot == null ? null : snapshot["sketches"] as List<object>;
        if (sketches == null) return result;
        foreach (object item in sketches)
        {
            Dictionary<string, object> sketch = item as Dictionary<string, object>;
            List<object> curves = sketch == null ? null : sketch["curve_reference_status"] as List<object>;
            if (curves != null)
                foreach (object c in curves)
                {
                    Dictionary<string, object> curve = c as Dictionary<string, object>;
                    if (curve != null) result["curve:" + GetString(curve, "tag")] = GetString(curve, "status");
                }
            List<object> constraints = sketch == null ? null : sketch["constraints"] as List<object>;
            if (constraints != null)
                foreach (object d in constraints)
                {
                    Dictionary<string, object> constraint = d as Dictionary<string, object>;
                    Dictionary<string, object> dimension = constraint == null ? null : GetDictionary(constraint, "dimension");
                    if (dimension != null)
                    {
                        string state = GetString(dimension, "dimension_state");
                        bool? driving = GetNullableBool(dimension, "associated_dimension_is_driving");
                        result["dimension:" + GetString(dimension, "tag")] = state + "/" + (driving.HasValue ? driving.Value.ToString() : "unknown");
                    }
                }
        }
        return result;
    }

    private static bool OnlySelectedDimensionExpressionMaySynchronize(Dictionary<string, object> before,
        Dictionary<string, object> after, string selectedDimensionTag)
    {
        List<object> beforeRows = before == null ? null : before["expressions"] as List<object>;
        List<object> afterRows = after == null ? null : after["expressions"] as List<object>;
        if (beforeRows == null || afterRows == null || beforeRows.Count != afterRows.Count) return false;
        Dictionary<string, Dictionary<string, object>> afterByTag = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (object item in afterRows)
        {
            Dictionary<string, object> record = item as Dictionary<string, object>;
            if (record != null) afterByTag[GetString(record, "tag")] = record;
        }
        Dictionary<string, object> selectedDimension = FindConstraintDimensionByTag(before, selectedDimensionTag);
        string selectedExpressionTag = GetString(selectedDimension, "expression_tag");
        foreach (object item in beforeRows)
        {
            Dictionary<string, object> pre = item as Dictionary<string, object>;
            if (pre == null) return false;
            Dictionary<string, object> post;
            if (!afterByTag.TryGetValue(GetString(pre, "tag"), out post)) return false;
            Dictionary<string, object> preIdentity = new Dictionary<string, object>(pre, StringComparer.Ordinal);
            Dictionary<string, object> postIdentity = new Dictionary<string, object>(post, StringComparer.Ordinal);
            preIdentity.Remove("right_hand_side"); preIdentity.Remove("value");
            postIdentity.Remove("right_hand_side"); postIdentity.Remove("value");
            if (!SameValue(preIdentity, postIdentity)) return false;
            if (!String.Equals(GetString(pre, "tag"), selectedExpressionTag, StringComparison.Ordinal) &&
                (!SameValue(GetValue(pre, "right_hand_side"), GetValue(post, "right_hand_side")) ||
                 !SameValue(GetValue(pre, "value"), GetValue(post, "value")))) return false;
        }
        Dictionary<string, object> dimensionBefore = selectedDimension;
        Dictionary<string, object> dimensionAfter = FindConstraintDimensionByTag(after, selectedDimensionTag);
        return dimensionBefore != null && dimensionAfter != null &&
            SameValue(GetValue(dimensionBefore, "expression_flags"), GetValue(dimensionAfter, "expression_flags")) &&
            String.Equals(GetString(dimensionBefore, "expression_name"), GetString(dimensionAfter, "expression_name"), StringComparison.Ordinal) &&
            String.Equals(GetString(dimensionBefore, "expression_unit"), GetString(dimensionAfter, "expression_unit"), StringComparison.Ordinal);
    }

    private static bool UnchangedDimensionIdentityAndFlags(Dictionary<string, object> before, Dictionary<string, object> after)
    {
        return before != null && after != null &&
            String.Equals(GetString(before, "tag"), GetString(after, "tag"), StringComparison.Ordinal) &&
            String.Equals(GetString(before, "journal_id"), GetString(after, "journal_id"), StringComparison.Ordinal) &&
            String.Equals(GetString(before, "constraint_name"), GetString(after, "constraint_name"), StringComparison.Ordinal) &&
            String.Equals(GetString(before, "associated_dimension_tag"), GetString(after, "associated_dimension_tag"), StringComparison.Ordinal) &&
            String.Equals(GetString(before, "associated_dimension_journal_id"), GetString(after, "associated_dimension_journal_id"), StringComparison.Ordinal) &&
            String.Equals(GetString(before, "expression_name"), GetString(after, "expression_name"), StringComparison.Ordinal) &&
            String.Equals(GetString(before, "expression_unit"), GetString(after, "expression_unit"), StringComparison.Ordinal) &&
            SameValue(GetValue(before, "expression_flags"), GetValue(after, "expression_flags"));
    }

    private static Dictionary<string, object> FindConstraintDimensionByTag(Dictionary<string, object> snapshot, string tag)
    {
        List<object> sketches = snapshot == null ? null : snapshot["sketches"] as List<object>;
        if (sketches == null) return null;
        foreach (object item in sketches)
        {
            Dictionary<string, object> sketch = item as Dictionary<string, object>;
            List<object> constraints = sketch == null ? null : sketch["constraints"] as List<object>;
            if (constraints == null) continue;
            foreach (object c in constraints)
            {
                Dictionary<string, object> constraint = c as Dictionary<string, object>;
                Dictionary<string, object> dimension = constraint == null ? null : GetDictionary(constraint, "dimension");
                if (dimension != null && String.Equals(GetString(dimension, "tag"), tag, StringComparison.Ordinal)) return dimension;
            }
        }
        return null;
    }

    private static bool ReopenedFixtureMatches(Dictionary<string, object> before, Dictionary<string, object> after)
    {
        Dictionary<string, object> beforeSketch = FindSketchSnapshotByFeatureName(before, "F11_MAIN_SKETCH");
        Dictionary<string, object> afterSketch = FindSketchSnapshotByFeatureName(after, "F11_MAIN_SKETCH");
        if (beforeSketch == null || afterSketch == null) return false;
        List<object> beforeGeometry = beforeSketch["geometry"] as List<object>, afterGeometry = afterSketch["geometry"] as List<object>;
        if (beforeGeometry == null || afterGeometry == null || beforeGeometry.Count != afterGeometry.Count) return false;
        Dictionary<string, Dictionary<string, object>> afterByName = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (object item in afterGeometry)
        {
            Dictionary<string, object> curve = item as Dictionary<string, object>;
            if (curve != null) afterByName[GetString(curve, "name")] = curve;
        }
        foreach (object item in beforeGeometry)
        {
            Dictionary<string, object> curve = item as Dictionary<string, object>;
            Dictionary<string, object> matched;
            if (curve == null || !afterByName.TryGetValue(GetString(curve, "name"), out matched)) return false;
            if (!CurveRecordsMatch(curve, matched)) return false;
        }
        Dictionary<string, string> statesBefore = NamedCurveStates(beforeSketch), statesAfter = NamedCurveStates(afterSketch);
        if (statesBefore.Count != statesAfter.Count) return false;
        foreach (KeyValuePair<string, string> item in statesBefore)
        {
            string afterState;
            if (!statesAfter.TryGetValue(item.Key, out afterState) || item.Value != afterState) return false;
        }
        return SameDimensionStatesAndExpressions(beforeSketch, afterSketch);
    }

    private static bool SameDimensionStatesAndExpressions(Dictionary<string, object> before, Dictionary<string, object> after)
    {
        Dictionary<string, Dictionary<string, object>> a = DimensionRecordsByName(before), b = DimensionRecordsByName(after);
        if (a.Count != b.Count) return false;
        foreach (KeyValuePair<string, Dictionary<string, object>> pair in a)
        {
            Dictionary<string, object> other;
            if (!b.TryGetValue(pair.Key, out other)) return false;
            if (!String.Equals(GetString(pair.Value, "dimension_state"), GetString(other, "dimension_state"), StringComparison.Ordinal) ||
                !String.Equals(GetString(pair.Value, "expression_rhs"), GetString(other, "expression_rhs"), StringComparison.Ordinal) ||
                !SameValue(GetValue(pair.Value, "expression_flags"), GetValue(other, "expression_flags"))) return false;
        }
        return true;
    }

    private static Dictionary<string, Dictionary<string, object>> DimensionRecordsByName(Dictionary<string, object> sketchRecord)
    {
        Dictionary<string, Dictionary<string, object>> result = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        List<object> constraints = sketchRecord == null ? null : sketchRecord["constraints"] as List<object>;
        if (constraints == null) return result;
        foreach (object item in constraints)
        {
            Dictionary<string, object> constraint = item as Dictionary<string, object>;
            Dictionary<string, object> dimension = constraint == null ? null : GetDictionary(constraint, "dimension");
            if (dimension != null) result[GetString(dimension, "constraint_name")] = dimension;
        }
        return result;
    }

    private static Dictionary<string, string> NamedCurveStates(Dictionary<string, object> sketchRecord)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        List<object> curves = sketchRecord == null ? null : sketchRecord["geometry"] as List<object>;
        List<object> statuses = sketchRecord == null ? null : sketchRecord["curve_reference_status"] as List<object>;
        Dictionary<string, string> statusByTag = new Dictionary<string, string>(StringComparer.Ordinal);
        if (statuses != null)
            foreach (object item in statuses)
            {
                Dictionary<string, object> status = item as Dictionary<string, object>;
                if (status != null) statusByTag[GetString(status, "tag")] = GetString(status, "status");
            }
        if (curves != null)
            foreach (object item in curves)
            {
                Dictionary<string, object> curve = item as Dictionary<string, object>;
                string state;
                if (curve != null && statusByTag.TryGetValue(GetString(curve, "tag"), out state)) result[GetString(curve, "name")] = state;
            }
        return result;
    }

    private static Dictionary<string, object> ReopenedIdentityMap(Dictionary<string, object> before, Dictionary<string, object> after)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> a = FindSketchSnapshotByFeatureName(before, "F11_MAIN_SKETCH");
        Dictionary<string, object> b = FindSketchSnapshotByFeatureName(after, "F11_MAIN_SKETCH");
        List<object> beforeGeometry = a == null ? null : a["geometry"] as List<object>;
        List<object> afterGeometry = b == null ? null : b["geometry"] as List<object>;
        Dictionary<string, Dictionary<string, object>> byName = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        if (afterGeometry != null) foreach (object item in afterGeometry) { Dictionary<string, object> curve = item as Dictionary<string, object>; if (curve != null) byName[GetString(curve, "name")] = curve; }
        if (beforeGeometry != null)
            foreach (object item in beforeGeometry)
            {
                Dictionary<string, object> oldCurve = item as Dictionary<string, object>, newCurve;
                if (oldCurve == null || !byName.TryGetValue(GetString(oldCurve, "name"), out newCurve)) continue;
                result[GetString(oldCurve, "name")] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "source_tag", GetString(oldCurve, "tag") }, { "reopened_tag", GetString(newCurve, "tag") },
                    { "source_journal_id", GetString(oldCurve, "journal_id") }, { "reopened_journal_id", GetString(newCurve, "journal_id") },
                    { "match_by_name_and_direct_geometry", CurveRecordsMatch(oldCurve, newCurve) }
                };
            }
        return result;
    }

    private static Dictionary<string, object> FindSketchSnapshotByFeatureName(Dictionary<string, object> snapshot, string name)
    {
        List<object> sketches = snapshot == null ? null : snapshot["sketches"] as List<object>;
        if (sketches != null)
            foreach (object item in sketches)
            {
                Dictionary<string, object> sketch = item as Dictionary<string, object>;
                if (sketch != null && String.Equals(GetString(sketch, "feature_name"), name, StringComparison.Ordinal)) return sketch;
            }
        return null;
    }

    private static bool CurveRecordsMatch(Dictionary<string, object> a, Dictionary<string, object> b)
    {
        if (a == null || b == null || !String.Equals(GetString(a, "type"), GetString(b, "type"), StringComparison.Ordinal) ||
            !String.Equals(GetString(a, "name"), GetString(b, "name"), StringComparison.Ordinal)) return false;
        if (GetValue(a, "start_mm") is double[] && GetValue(b, "start_mm") is double[])
        {
            double[] a0 = (double[])a["start_mm"], a1 = (double[])a["end_mm"], b0 = (double[])b["start_mm"], b1 = (double[])b["end_mm"];
            return Math.Min(Math.Max(Distance(a0, b0), Distance(a1, b1)), Math.Max(Distance(a0, b1), Distance(a1, b0))) <= PositionToleranceMm;
        }
        double[] ca = a["center_mm"] as double[], cb = b["center_mm"] as double[];
        if (GetValue(a, "major_radius_mm") is double && GetValue(b, "major_radius_mm") is double)
            return ca != null && cb != null && Distance(ca, cb) <= PositionToleranceMm &&
                Math.Abs(Convert.ToDouble(a["major_radius_mm"], CultureInfo.InvariantCulture) - Convert.ToDouble(b["major_radius_mm"], CultureInfo.InvariantCulture)) <= PositionToleranceMm &&
                Math.Abs(Convert.ToDouble(a["minor_radius_mm"], CultureInfo.InvariantCulture) - Convert.ToDouble(b["minor_radius_mm"], CultureInfo.InvariantCulture)) <= PositionToleranceMm &&
                Math.Abs(Convert.ToDouble(a["rotation_rad"], CultureInfo.InvariantCulture) - Convert.ToDouble(b["rotation_rad"], CultureInfo.InvariantCulture)) <= AngularToleranceRad &&
                Math.Abs(Convert.ToDouble(a["length_mm"], CultureInfo.InvariantCulture) - Convert.ToDouble(b["length_mm"], CultureInfo.InvariantCulture)) <= PositionToleranceMm;
        return ca != null && cb != null && Distance(ca, cb) <= PositionToleranceMm &&
            Math.Abs(Convert.ToDouble(a["radius_mm"], CultureInfo.InvariantCulture) - Convert.ToDouble(b["radius_mm"], CultureInfo.InvariantCulture)) <= PositionToleranceMm &&
            Math.Abs(Convert.ToDouble(a["length_mm"], CultureInfo.InvariantCulture) - Convert.ToDouble(b["length_mm"], CultureInfo.InvariantCulture)) <= PositionToleranceMm;
    }

    private static bool RollbackEvidenceComplete(Dictionary<string, object> evidence)
    {
        string[] keys = new string[]
        {
            "undo_to_mark_attempted", "undo_to_mark_succeeded", "undo_update_succeeded", "semantic_state_restored",
            "full_geometry_restored", "sketch_geometry_restored", "constraints_restored", "dimension_associations_restored",
            "frames_restored", "reference_status_restored", "source_associations_restored", "history_restored",
            "feature_inventory_restored", "expressions_restored", "bodies_restored", "global_curve_inventory_restored",
            "helper_inventory_restored", "dirty_state_restored", "solver_status_restored", "selected_object_states_restored",
            "active_sketch_restored", "undo_mark_closed", "full_restoration_proven"
        };
        if (evidence == null) return false;
        foreach (string key in keys) if (!Object.Equals(GetValue(evidence, key), true)) return false;
        return true;
    }

    private static bool IsOverconstrained(string status)
    {
        return !String.IsNullOrEmpty(status) && (status.IndexOf("Over", StringComparison.OrdinalIgnoreCase) >= 0 ||
            status.IndexOf("Conflict", StringComparison.OrdinalIgnoreCase) >= 0 || status.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static Dictionary<string, object> FixtureRecord(Fixture fixture)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "sketch", SketchRecord(fixture.Sketch) },
            { "driving_line", CurveRecord(fixture.DrivingLine) }, { "vertical_line", CurveRecord(fixture.VerticalLine) },
            { "witness_line", CurveRecord(fixture.WitnessLine) }, { "partial_arc", CurveRecord(fixture.PartialArc) },
            { "full_circle_arc", CurveRecord(fixture.FullCircle) },
            { "unsupported_ellipse", fixture.UnsupportedEllipse == null ? null : CurveRecord(fixture.UnsupportedEllipse) },
            { "horizontal_dimension", ReadDimensionRecord(fixture.HorizontalDimension) },
            { "vertical_dimension", ReadDimensionRecord(fixture.VerticalDimension) },
            { "radial_dimension", fixture.RadialDimension == null ? null : ReadDimensionRecord(fixture.RadialDimension) }
        };
    }

    private static Dictionary<string, object> SketchRequestRecord(M3SketchRequest request)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", request.RevisionId }, { "origin_mm", request.OriginMm }, { "normal", request.Normal },
            { "x_axis", request.XAxis }, { "constraint_mode", request.ConstraintMode },
            { "geometry_count", request.Geometries == null ? 0 : request.Geometries.Count }
        };
    }

    private static Dictionary<string, object> RequestRecord(M3SketchReferenceConversionRequest request)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", request.RevisionId }, { "sketch_id", request.SketchId },
            { "object_ids", request.ObjectIds == null ? null : request.ObjectIds.ToArray() },
            { "target_state", request.TargetState }
        };
    }

    private static Dictionary<string, object> ExecutionRecord(M3ExecutionResult execution)
    {
        if (execution == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "state", execution.State }, { "revision_id", execution.RevisionId }, { "result", execution.Result },
            { "error_code", execution.ErrorCode }, { "error_message", execution.ErrorMessage }
        };
    }

    private static Dictionary<string, object> VerificationRecord(M3VerificationResult verification)
    {
        if (verification == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "verified", verification.Verified }, { "revision_id", verification.RevisionId },
            { "checks", verification.Checks }, { "result", verification.Result },
            { "error_code", verification.ErrorCode }, { "error_message", verification.ErrorMessage }
        };
    }

    private static Dictionary<string, object> SaveCopyRecord(M2SaveCopyResult result)
    {
        if (result == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "state", result.State }, { "part_path", result.PartPath }, { "part_tag", result.PartTag },
            { "revision_id", result.RevisionId }, { "identity", result.Identity }, { "reopened", result.Reopened },
            { "checks", result.Checks }, { "evidence", result.Evidence },
            { "error_code", result.ErrorCode }, { "error_message", result.ErrorMessage }
        };
    }

    private static Dictionary<string, object> AssemblyRecord(Assembly assembly)
    {
        string location = null;
        try { location = assembly.Location; } catch { }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "full_name", assembly.FullName }, { "location", location },
            { "module_version_id", assembly.ManifestModule.ModuleVersionId.ToString("D") }
        };
    }

    private static object EmptyCounts()
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        { { "selected", 0 }, { "passed", 0 }, { "failed", 0 }, { "skipped", 0 }, { "unknown", 0 } };
    }

    private static Dictionary<string, object> CaseCounts(List<object> cases)
    {
        int passed = 0, failed = 0, skipped = 0, unknown = 0;
        foreach (object item in cases)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            string status = row == null ? "FAILED" : GetString(row, "status");
            if (status == "PASSED") passed++; else if (status == "SKIPPED") skipped++; else if (status == "UNKNOWN") unknown++; else failed++;
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        { { "selected", cases.Count }, { "passed", passed }, { "failed", failed }, { "skipped", skipped }, { "unknown", unknown } };
    }

    private static Dictionary<string, object> CaseCountsById(List<object> cases)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (object item in cases)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            if (row == null) continue;
            string id = GetString(row, "test_id");
            if (String.IsNullOrEmpty(id)) continue;
            result[id] = new Dictionary<string, object>(StringComparer.Ordinal)
            { { "selected", 1 }, { "passed", GetString(row, "status") == "PASSED" ? 1 : 0 },
                { "failed", GetString(row, "status") == "FAILED" ? 1 : 0 }, { "skipped", GetString(row, "status") == "SKIPPED" ? 1 : 0 },
                { "unknown", GetString(row, "status") == "UNKNOWN" ? 1 : 0 } };
        }
        return result;
    }

    private static bool IsCompletePass(List<object> cases, List<object> defects, bool writesHalted)
    {
        if (writesHalted || cases.Count == 0 || defects.Count != 0) return false;
        foreach (object item in cases)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            if (row == null || GetString(row, "status") != "PASSED") return false;
        }
        return true;
    }

    private static void SavePart(Part part, string label)
    {
        PartSaveStatus status = part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
        if (status == null) throw new InvalidOperationException("NX Save returned no status while saving " + label + ".");
        try
        {
            Require(status.NumberUnsavedParts == 0 && status.NumberUnsavedObjects == 0,
                "NX left unsaved parts/objects while saving " + label + ".");
        }
        finally { status.Dispose(); }
    }

    private static Sketch FindSketchByJournalId(Part part, string journalId)
    {
        foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null && String.Equals(sketch.JournalIdentifier, journalId, StringComparison.Ordinal)) return sketch;
        throw new InvalidOperationException("Sketch JournalIdentifier not found: " + journalId);
    }

    private static Sketch FindSketchByFeatureName(Part part, string name)
    {
        foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null && sketch.Feature != null && String.Equals(sketch.Feature.Name, name, StringComparison.Ordinal)) return sketch;
        throw new InvalidOperationException("Sketch with feature name not found: " + name);
    }

    private static Curve FindCurveByName(Sketch sketch, string name)
    {
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve;
            if (curve != null && String.Equals(curve.Name, name, StringComparison.Ordinal)) return curve;
        }
        return null;
    }

    private static Feature FindFeatureByTag(Part part, string tag)
    {
        foreach (Feature feature in part.Features.GetFeatures())
            if (feature != null && String.Equals(feature.Tag.ToString(), tag, StringComparison.Ordinal)) return feature;
        return null;
    }

    private static Curve FindCurveByTag(Part part, string tag)
    {
        foreach (Curve curve in part.Curves.ToArray()) if (curve != null && String.Equals(curve.Tag.ToString(), tag, StringComparison.Ordinal)) return curve;
        throw new InvalidOperationException("Curve Tag not found: " + tag);
    }

    private static SketchDimensionalConstraint FindDimensionByName(Sketch sketch, string name)
    {
        List<object> constraints = ConstraintRecords(sketch);
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] found;
            try { found = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type); } catch { continue; }
            if (found == null) continue;
            foreach (SketchConstraint item in found)
            {
                SketchDimensionalConstraint dimension = item as SketchDimensionalConstraint;
                if (dimension != null && String.Equals(dimension.Name, name, StringComparison.Ordinal)) return dimension;
            }
        }
        return null;
    }

    private static SketchConstraint FindConstraintByTag(Sketch sketch, string tag)
    {
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] found;
            try { found = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type); } catch { continue; }
            if (found == null) continue;
            foreach (SketchConstraint item in found) if (item != null && String.Equals(item.Tag.ToString(), tag, StringComparison.Ordinal)) return item;
        }
        return null;
    }

    private static List<object> SortedRows(List<object> rows, string key)
    {
        rows.Sort(delegate(object left, object right)
        {
            Dictionary<string, object> a = left as Dictionary<string, object>, b = right as Dictionary<string, object>;
            return StringComparer.Ordinal.Compare(GetString(a, key), GetString(b, key));
        });
        return rows;
    }

    private static Dictionary<string, object> DefectRecord(string testId, Exception error, string reproduction, string retest)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test_id", testId }, { "classification", "native_acceptance_failure" },
            { "observed_error", ExceptionRecord(error) }, { "reproduction_steps", new string[] { reproduction } },
            { "retest_criterion", retest }
        };
    }

    private static Dictionary<string, object> ExceptionRecord(Exception error)
    {
        if (error == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "type", error.GetType().FullName }, { "message", error.Message }, { "stack_trace", error.StackTrace }
        };
    }

    private static void SetBooleanMember(object target, string name, bool value)
    {
        Require(target != null, "Adapter state is null while setting test seam " + name + ".");
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Require(field != null && field.FieldType == typeof(bool), "Adapter test seam bool field was not found: " + name);
        field.SetValue(target, value);
    }

    private static Dictionary<string, object> GetDictionary(Dictionary<string, object> dictionary, string key)
    {
        if (dictionary == null) return null;
        object value; return dictionary.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
    }

    private static object FindNestedValue(object value, string key)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary == null) return null;
        object found; if (dictionary.TryGetValue(key, out found)) return found;
        foreach (object child in dictionary.Values) { object nested = FindNestedValue(child, key); if (nested != null) return nested; }
        return null;
    }

    private static Dictionary<string, object> FindNestedDictionary(object value, string key)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary == null) return null;
        object found; if (dictionary.TryGetValue(key, out found)) return found as Dictionary<string, object>;
        foreach (object child in dictionary.Values) { Dictionary<string, object> nested = FindNestedDictionary(child, key); if (nested != null) return nested; }
        return null;
    }

    private static object GetValue(Dictionary<string, object> dictionary, string key)
    {
        object value; return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null;
    }

    private static string GetString(Dictionary<string, object> dictionary, string key)
    {
        object value = GetValue(dictionary, key);
        return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static bool? GetNullableBool(Dictionary<string, object> dictionary, string key)
    {
        object value = GetValue(dictionary, key);
        if (value == null) return null;
        try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); } catch { return null; }
    }

    private static bool IsTenRhs(string value)
    {
        double numeric; return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out numeric) && Math.Abs(numeric - 10.0) <= PositionToleranceMm;
    }

    private static bool Near(double value, double expected) { return Math.Abs(value - expected) <= PositionToleranceMm; }
    private static string NumberText(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
    private static double[] Point(Point3d point) { return new double[] { point.X, point.Y, point.Z }; }
    private static double[] Vector(Vector3d vector) { return new double[] { vector.X, vector.Y, vector.Z }; }
    private static double Distance(double[] a, double[] b)
    { return Math.Sqrt(Math.Pow(a[0] - b[0], 2.0) + Math.Pow(a[1] - b[1], 2.0) + Math.Pow(a[2] - b[2], 2.0)); }
    private static bool SameValue(object a, object b)
    { return String.Equals(M2Json.CanonicalText(JsonCollections(a)), M2Json.CanonicalText(JsonCollections(b)), StringComparison.Ordinal); }

    // State maps are Dictionary<string,string>; M2Json accepts only object-valued
    // maps. Preserve every key/value while adapting collection types. Unknown
    // scalar types still fail in M2Json instead of being silently stringified.
    private static object JsonCollections(object value)
    {
        IDictionary map = value as IDictionary;
        if (map != null)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (DictionaryEntry pair in map)
            {
                string key = pair.Key as string;
                if (key == null) throw new InvalidOperationException("Snapshot JSON map key must be a string.");
                result.Add(key, JsonCollections(pair.Value));
            }
            return result;
        }
        IList sequence = value as IList;
        if (sequence != null)
        {
            List<object> result = new List<object>();
            foreach (object item in sequence) result.Add(JsonCollections(item));
            return result;
        }
        return value;
    }
    private static bool IsNx2506(string value)
    {
        if (String.IsNullOrWhiteSpace(value)) return false;
        string normalized = value.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        return normalized.Contains("2506") || normalized.Contains("25.06") || normalized.Contains("25.6");
    }
    private static object GetValue(Dictionary<string, object> dictionary, string key, object fallback)
    { object value = GetValue(dictionary, key); return value == null ? fallback : value; }
    private static string FirstNonEmpty(params string[] values)
    { foreach (string value in values) if (!String.IsNullOrWhiteSpace(value)) return value; return null; }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void RequireNewPath(string path, string label)
    { if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Unique " + label + " path already exists; refusing to overwrite it: " + path); }
    private static string UtcNow() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
    private static void Persist(string path, Dictionary<string, object> report)
    {
        if (String.IsNullOrEmpty(path)) return;
        File.WriteAllText(path, M2Json.CanonicalText(JsonCollections(report)), new UTF8Encoding(false));
    }
}
