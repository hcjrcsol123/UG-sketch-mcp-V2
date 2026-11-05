// Real-NX F07 subsystem acceptance through the production adapter.
// Build offline; run only by the designated NX operator through RunF07Acceptance.py.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using NXOpen;
using NXOpen.Features;
using NxSketchMcp.M2;

public static class F07Acceptance
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    private const double PositionToleranceMm = 1e-6;
    private const double LengthToleranceMm = 1e-6;
    private const double FrameTolerance = 1e-7;
    private const double AngleToleranceRad = 1e-7;
    private const double Pi = Math.PI;

    private sealed class Frame
    {
        internal double[] Origin;
        internal double[] Normal;
        internal double[] XAxis;
        internal double[] YAxis;
    }

    private sealed class SketchFixture
    {
        internal string Name;
        internal string SketchId;
        internal Frame Frame;
        internal M3PlanSnapshot CreatePlan;
        internal Dictionary<string, object> NativeAfter;
        internal Dictionary<string, object> SeedEvidence;
        internal int ExpectedCurveCountAfterOneFillet;
        internal List<string> GeometryIds = new List<string>();
        internal List<M3SketchGeometryRequest> GeometryRequests = new List<M3SketchGeometryRequest>();
        internal Dictionary<string, string> GeometryIdsByRequestId = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed class CornerRun
    {
        internal M3PlanSnapshot Plan;
        internal M3ExecutionResult Execution;
        internal M3VerificationResult Verification;
    }

    private sealed class ForeignCurvePair
    {
        internal string Curve1RequestId;
        internal string Curve1End;
        internal string Curve2RequestId;
        internal string Curve2End;
        internal string Curve1JournalId;
        internal string Curve2JournalId;
    }

    private delegate void CaseBody(Dictionary<string, object> evidence);

    public static void Main(string[] args)
    {
        string runId = Guid.NewGuid().ToString("N");
        string modelDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "models");
        string evidenceDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "evidence");
        string sourcePath = Path.Combine(modelDirectory, "F07-acceptance-" + runId + ".prt");
        string savedPath = Path.Combine(modelDirectory, "F07-acceptance-" + runId + "-roundtrip.prt");
        string reportPath = Path.Combine(evidenceDirectory, "F07-acceptance-" + runId + ".json");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        RequireNewPath(sourcePath, "source model");
        RequireNewPath(savedPath, "round-trip model");
        RequireNewPath(reportPath, "acceptance report");

        List<object> cases = new List<object>();
        List<object> defects = new List<object>();
        List<SketchFixture> fixturesToRecheck = new List<SketchFixture>();
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test", "F07 native sketch fillet and chamfer acceptance" },
            { "test_version", "0.1.0" }, { "run_id", runId },
            { "started_utc", UtcNow() }, { "passed", false },
            { "source_model_path", sourcePath }, { "roundtrip_model_path", savedPath },
            { "report_path", reportPath }, { "cases", cases }, { "defects", defects },
            { "position_tolerance_mm", PositionToleranceMm },
            { "length_tolerance_mm", LengthToleranceMm },
            { "frame_component_tolerance", FrameTolerance },
            { "angle_tolerance_rad", AngleToleranceRad },
            { "units", "millimeter" }, { "physical_metrology", false },
            { "instrument_calibration", "Not applicable: direct NX model values only; no external instrument." },
            { "sample_size", 1 }, { "reliability_claim", false },
            { "scope_limitations", new List<object> {
                "One deterministic NX2506 subsystem-validation run does not establish reliability, manufacturing accuracy, compliance, or cross-version behavior.",
                "Chamfer distance-angle uses delta measured at first trim point A between A-to-corner C and A-to-second trim point B.",
                "NX SketchVirtualPoint helper objects are recorded separately from NXOpen.Curve objects."
            } },
            { "configuration", new Dictionary<string, object>(StringComparer.Ordinal) {
                { "test_layer", "real NXOpen/UF subsystem validation through production M2OpenModelAdapter" },
                { "traceability", "docs/V2.md F07/T08; tests/nx/v2/F07Acceptance.md" },
                { "production_bridge_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Bridge.dll") },
                { "production_adapter_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Entry.dll") },
                { "acceptance_assembly", Path.Combine(evidenceDirectory, "F07Acceptance.dll") },
                { "nx_started_by_harness", false }
            } }
        };
        Persist(reportPath, evidence);

        Session session = null;
        Part originalPart = null;
        Part testPart = null;
        M2OpenModelAdapter adapter = null;
        M2ModelBinding binding = null;
        Frame xy = BuildFrame(new double[] { 0.0, 0.0, 0.0 },
            new double[] { 0.0, 0.0, 1.0 }, new double[] { 1.0, 0.0, 0.0 });
        Frame tilted = BuildFrame(new double[] { 10.0, 20.0, 30.0 },
            new double[] { 1.0, 2.0, 3.0 }, new double[] { 1.0, -1.0, 0.5 });

        try
        {
            session = Session.GetSession();
            originalPart = session.Parts.Work;
            evidence["original_work_part_path"] = originalPart == null ? null : originalPart.FullPath;
            evidence["original_work_part_was_modified"] = originalPart != null && originalPart.IsModified;
            evidence["session_was_in_task_environment"] = session.IsInTaskEnvironment;
            Require(!session.IsInTaskEnvironment, "F07 acceptance requires an idle NX session outside a task environment.");
            Require(originalPart == null || !originalPart.IsModified,
                "F07 acceptance requires the original Work Part to be saved and unmodified.");

            testPart = session.Parts.NewDisplay(sourcePath, Part.Units.Millimeters);
            SavePart(testPart, "isolated empty F07 fixture");
            adapter = new M2OpenModelAdapter();
            binding = adapter.Takeover(sourcePath, "saved_independent_copy", "f07-acceptance-session",
                "f07-acceptance-" + runId, 1);
            evidence["session"] = adapter.GetSession(binding);
            evidence["loaded_assemblies"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "bridge", AssemblyRecord(typeof(M2Json).Assembly) },
                { "production_adapter", AssemblyRecord(typeof(M2OpenModelAdapter).Assembly) },
                { "nxopen", AssemblyRecord(typeof(Session).Assembly) },
                { "acceptance", AssemblyRecord(Assembly.GetExecutingAssembly()) }
            };
            evidence["binding_revision_before_tests"] = binding.RevisionId;
            Persist(reportPath, evidence);

            SketchFixture rightAngleFillet = null;
            RunCase(reportPath, evidence, cases, defects, "F07_fillet_line_line_right_angle",
                "Create an explicit right-angle Line-Line fillet with different endpoint roles and preserve an unselected curve.",
                new string[] {
                    "Create a geometry-only XY sketch with line1=(0,0)->(10,0), line2=(10,0)->(10,10), and unselected=(0,5)->(4,5).",
                    "Resolve line IDs from production nx_inspect_sketch output; prepare radius=2 mm with curve1_end=end and curve2_end=start.",
                    "Execute and verify the plan through M2OpenModelAdapter, then read every Sketch.GetAllGeometry object directly through NXOpen."
                },
                "Production execute is APPLIED and VerifyPlan succeeds. Native line tangent points are (8,0) and (10,2) mm; native fillet radius is 2 mm, center is (8,2) mm, arc length is π mm, lines are tangent, and the unrelated line is unchanged.",
                delegate(Dictionary<string, object> row)
                {
                    rightAngleFillet = CreateSketch(adapter, binding, testPart, "line-line-fillet", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> {
                            Line("line1", 0, 0, 10, 0), Line("line2", 10, 0, 10, 10),
                            Line("untouched", 0, 5, 4, 5)
                        }), row);
                    Dictionary<string, object> before = NativeSnapshot(testPart, FindSketch(testPart, rightAngleFillet.SketchId));
                    row["native_before"] = before;
                    M3SketchCornerRequest request = CornerRequest(binding, rightAngleFillet, "fillet",
                        "line1", "end", "line2", "start", 2.0, null, null, null);
                    CornerRun run = ExecuteCorner(adapter, binding, request, row, "line-line-fillet");
                    Sketch native = FindSketch(testPart, rightAngleFillet.SketchId);
                    row["native_after"] = NativeSnapshot(testPart, native);
                    VerifyLineLineFillet(native, xy, new double[] { 10, 0 }, new double[] { 0, 0 },
                        new double[] { 10, 10 }, 2.0, new double[] { 8, 0 }, new double[] { 10, 2 },
                        new double[] { 8, 2 }, Pi, rightAngleFillet.ExpectedCurveCountAfterOneFillet, row);
                    VerifyUnselectedLineUnchanged(before, row["native_after"], new double[] { 0, 5, 0 },
                        new double[] { 4, 5, 0 }, row);
                    row["prepared_plan"] = run.Plan.PublicView;
                    rightAngleFillet.NativeAfter = row["native_after"] as Dictionary<string, object>;
                    row["execute"] = ExecutionRecord(run.Execution);
                    row["verification"] = VerificationRecord(run.Verification);
                    fixturesToRecheck.Add(rightAngleFillet);
                });

            RunCase(reportPath, evidence, cases, defects, "F07_fillet_line_arc",
                "Validate a native Line-Arc fillet using explicit Line end and Arc start selection.",
                new string[] {
                    "Create line=(10,2)->(10,9) and arc center=(10,0), radius=2, start=90°, sweep=90° in a geometry-only XY sketch.",
                    "Prepare a 0.5 mm fillet with explicit line/end and arc/start IDs from current inspection.",
                    "Execute/verify through the production adapter and independently inspect retained original and added arcs."
                },
                "Direct NX readback finds the 0.5 mm fillet centered at (9.5,sqrt(6)) mm, tangent points (10,sqrt(6)) and (9.6,1.9595917942), retained original arc endpoint (8,0), and geometric tangency at both contacts.",
                delegate(Dictionary<string, object> row)
                {
                    SketchFixture fixture = CreateSketch(adapter, binding, testPart, "line-arc-fillet", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> {
                            Line("line", 10, 2, 10, 9), Arc("arc", 10, 0, 2, 90, 90)
                        }), row);
                    Dictionary<string, object> before = NativeSnapshot(testPart, FindSketch(testPart, fixture.SketchId));
                    row["native_before"] = before;
                    M3SketchCornerRequest request = CornerRequest(binding, fixture, "fillet",
                        "line", "start", "arc", "start", 0.5, null, null, null);
                    CornerRun run = ExecuteCorner(adapter, binding, request, row, "line-arc-fillet");
                    Sketch native = FindSketch(testPart, fixture.SketchId);
                    row["native_after"] = NativeSnapshot(testPart, native);
                    VerifyLineArcFillet(native, xy, row);
                    row["prepared_plan"] = run.Plan.PublicView;
                    fixture.NativeAfter = row["native_after"] as Dictionary<string, object>;
                    row["execute"] = ExecutionRecord(run.Execution);
                    row["verification"] = VerificationRecord(run.Verification);
                    fixturesToRecheck.Add(fixture);
                });

            RunCase(reportPath, evidence, cases, defects, "F07_fillet_arc_arc",
                "Validate a native Arc-Arc fillet on explicitly selected shared start endpoints.",
                new string[] {
                    "Create arcs centered at (0,0) and (2,0), both with radius sqrt(2); arc1 spans 45° to 135°, arc2 spans 135° to 225°.",
                    "Prepare a 0.25 mm fillet using both inspected arc IDs and start endpoints.",
                    "Execute/verify through the production adapter; classify all curve objects and the NX helper point separately."
                },
                "Direct readback finds both retained original arcs with correct far endpoints, the 0.25 mm fillet at the independent center/tangent values in F07Acceptance.md, and tangent contact to both original arcs.",
                delegate(Dictionary<string, object> row)
                {
                    double root2 = Math.Sqrt(2.0);
                    SketchFixture fixture = CreateSketch(adapter, binding, testPart, "arc-arc-fillet", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> {
                            Arc("arc1", 0, 0, root2, 45, 90), Arc("arc2", 2, 0, root2, 135, 90)
                        }), row);
                    Dictionary<string, object> before = NativeSnapshot(testPart, FindSketch(testPart, fixture.SketchId));
                    row["native_before"] = before;
                    M3SketchCornerRequest request = CornerRequest(binding, fixture, "fillet",
                        "arc1", "start", "arc2", "start", 0.25, null, null, null);
                    CornerRun run = ExecuteCorner(adapter, binding, request, row, "arc-arc-fillet");
                    Sketch native = FindSketch(testPart, fixture.SketchId);
                    row["native_after"] = NativeSnapshot(testPart, native);
                    VerifyArcArcFillet(native, root2, row);
                    row["prepared_plan"] = run.Plan.PublicView;
                    fixture.NativeAfter = row["native_after"] as Dictionary<string, object>;
                    row["execute"] = ExecutionRecord(run.Execution);
                    row["verification"] = VerificationRecord(run.Verification);
                    fixturesToRecheck.Add(fixture);
                });

            RunCase(reportPath, evidence, cases, defects, "F07_oblique_tilted_line_line_fillet",
                "Check non-axial line directions and curve1=end/curve2=start on a tilted arbitrary-frame sketch.",
                new string[] {
                    "Create a sketch at [10,20,30] mm with normal [1,2,3] and requested X direction [1,-1,0.5].",
                    "Create line1=(10,10)->(30,20) and line2=(30,20)->(35,35) in local millimeters; their selected-corner angle is 135 degrees.",
                    "Prepare and execute an R=1.5 mm fillet at line1=end and line2=start, then compare native world coordinates to independent frame and corner calculations."
                },
                "Native tangent points match the independently calculated local values (29.4442741904,19.7221370952) and (30.1964787442,20.5894362327) mm after world-frame transformation, the fillet radius is 1.5 mm, center and arc length match the oracle, and both contacts are tangent.",
                delegate(Dictionary<string, object> row)
                {
                    SketchFixture fixture = CreateSketch(adapter, binding, testPart, "tilted-oblique-fillet", tilted,
                        GeometryOnly(new List<M3SketchGeometryRequest> {
                            Line("line1", 10, 10, 30, 20), Line("line2", 30, 20, 35, 35)
                        }), row);
                    CheckNativeFrame(FindSketch(testPart, fixture.SketchId), tilted, row);
                    Dictionary<string, object> before = NativeSnapshot(testPart, FindSketch(testPart, fixture.SketchId));
                    row["native_before"] = before;
                    M3SketchCornerRequest request = CornerRequest(binding, fixture, "fillet",
                        "line1", "end", "line2", "start", 1.5, null, null, null);
                    CornerRun run = ExecuteCorner(adapter, binding, request, row, "tilted-oblique-fillet");
                    Sketch native = FindSketch(testPart, fixture.SketchId);
                    row["native_after"] = NativeSnapshot(testPart, native);
                    Dictionary<string, object> expected = ExpectedFillet(new double[] { 30, 20 },
                        new double[] { 10, 10 }, new double[] { 35, 35 }, 1.5);
                    VerifyLineLineFillet(native, tilted, new double[] { 30, 20 }, new double[] { 10, 10 },
                        new double[] { 35, 35 }, 1.5, (double[])expected["point1"], (double[])expected["point2"], (double[])expected["center"],
                        (double)expected["arc_length"], fixture.ExpectedCurveCountAfterOneFillet, row);
                    row["independent_local_oracle"] = expected;
                    row["independent_world_expected"] = new Dictionary<string, object> {
                        { "point1_mm", Point(WorldPoint(tilted, ((double[])expected["point1"])[0], ((double[])expected["point1"])[1])) },
                        { "point2_mm", Point(WorldPoint(tilted, ((double[])expected["point2"])[0], ((double[])expected["point2"])[1])) },
                        { "center_mm", Point(WorldPoint(tilted, ((double[])expected["center"])[0], ((double[])expected["center"])[1])) }
                    };
                    row["prepared_plan"] = run.Plan.PublicView;
                    fixture.NativeAfter = row["native_after"] as Dictionary<string, object>;
                    row["execute"] = ExecutionRecord(run.Execution);
                    row["verification"] = VerificationRecord(run.Verification);
                    fixturesToRecheck.Add(fixture);
                });

            RunCase(reportPath, evidence, cases, defects, "F07_chamfer_equal_two_distance_and_angle",
                "Validate equal-distance, unequal two-distance, and distance-angle chamfers with independent endpoint/length/angle checks.",
                new string[] {
                    "Create fresh XY right-angle line pairs for each chamfer case; use line1=(0,0)->(20,0) and line2=(20,0)->(20,10).",
                    "Resolve each curve ID by current sketch inspection and select line1=end, line2=start.",
                    "Use distance1=3,distance2=3; distance1=4,distance2=2; and distance1=4,angle_deg=30 on separate sketches."
                },
                "Equal inputs produce (17,0)/(20,3) and sqrt(18) mm segment; unequal inputs produce (16,0)/(20,2) and sqrt(20) mm; distance-angle produces (16,0)/(20,2.3094010768), 8/sqrt(3) mm segment, and measured A-angle 30 degrees. No chamfer Driving dimension is created.",
                delegate(Dictionary<string, object> row)
                {
                    RunChamferCase(adapter, binding, testPart, xy, "equal-distance", 3.0, 3.0, null,
                        new double[] { 17, 0 }, new double[] { 20, 3 }, Math.Sqrt(18.0), null, row, fixturesToRecheck);
                    RunChamferCase(adapter, binding, testPart, xy, "two-distance", 4.0, 2.0, null,
                        new double[] { 16, 0 }, new double[] { 20, 2 }, Math.Sqrt(20.0), null, row, fixturesToRecheck);
                    RunChamferCase(adapter, binding, testPart, xy, "distance-angle", 4.0, null, 30.0,
                        new double[] { 16, 0 }, new double[] { 20, 4.0 / Math.Sqrt(3.0) }, 8.0 / Math.Sqrt(3.0),
                        30.0 * Pi / 180.0, row, fixturesToRecheck);
                });

            RunCase(reportPath, evidence, cases, defects, "F07_wrong_object_and_cross_sketch_rejections",
                "Refuse missing, wrong-type, same-curve, disconnected-endpoint, and cross-sketch IDs without changing model state.",
                new string[] {
                    "Create one sketch with a valid line pair and one full circle, plus a second sketch with a distinct line pair.",
                    "Snapshot the complete native geometry and constraints, part IDs, modified state, active sketch, and revision.",
                    "Prepare invalid requests separately: missing ID, chamfer on a full circle, fillet on a full circle, duplicate curve ID, mismatched selected endpoints, and a curve ID belonging to the other sketch."
                },
                "Each request is refused before native mutation with an explicit error. Every before/after native snapshot and binding revision match exactly.",
                delegate(Dictionary<string, object> row)
                {
                    SketchFixture local = CreateSketch(adapter, binding, testPart, "invalid-target-local", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> {
                            Line("local-1", 0, 0, 20, 0), Line("local-2", 20, 0, 20, 10),
                            Line("local-other", 30, 0, 35, 0), Circle("local-circle", 40, 0, 4)
                        }), row);
                    SketchFixture foreign = CreateSketch(adapter, binding, testPart, "invalid-target-foreign", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> {
                            Line("foreign-1", 0, 20, 20, 20), Line("foreign-2", 20, 20, 20, 30),
                            Line("foreign-3", 20, 30, 30, 30), Line("foreign-4", 30, 30, 30, 40),
                            Line("foreign-5", 30, 40, 40, 40)
                        }), row);
                    RefreshFixtureGeometryIds(adapter, binding, testPart, local, row, "after_foreign_seed_creation_local_refresh");
                    RefreshFixtureGeometryIds(adapter, binding, testPart, foreign, row, "before_cross_sketch_requests_foreign_refresh");
                    ForeignCurvePair foreignPair = FindForeignCurvePairWithoutLocalIdCollisions(local, foreign, testPart, row);
                    List<object> attempts = new List<object>();
                    row["rejection_attempts"] = attempts;
                    M3SketchCornerRequest missingCurveRequest = CornerRequest(binding, local, "fillet",
                        "local-1", "end", "local-2", "start", 2.0, null, null, null);
                    missingCurveRequest.Curve1Id = "F07_missing_curve_id";
                    attempts.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        missingCurveRequest,
                        "missing_curve_id", null, row));
                    attempts.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, local, "chamfer", "local-circle", "start", "local-2", "start", null, 2.0, 2.0, null),
                        "chamfer_non_line_curve", "UNSUPPORTED_CURVE_TYPE", row));
                    attempts.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, local, "fillet", "local-circle", "start", "local-2", "start", 1.0, null, null, null),
                        "fillet_full_circle_has_no_endpoint", "UNSUPPORTED_CURVE_TYPE", row));
                    attempts.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, local, "fillet", "local-1", "end", "local-1", "start", 1.0, null, null, null),
                        "same_curve_selected_twice", null, row));
                    attempts.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, local, "fillet", "local-1", "end", "local-other", "start", 1.0, null, null, null),
                        "different_endpoints", null, row));
                    attempts.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, foreign, "fillet", foreignPair.Curve1RequestId, foreignPair.Curve1End,
                            foreignPair.Curve2RequestId, foreignPair.Curve2End, 1.0, null, null, null, local.SketchId),
                        "foreign_curve_ids_in_local_sketch", "CURVE_NOT_FOUND", row));
                    row["local_sketch_id"] = local.SketchId;
                    row["foreign_sketch_id"] = foreign.SketchId;
                });

            RunCase(reportPath, evidence, cases, defects, "F07_oversize_degenerate_and_native_rollback",
                "Accept a read-only preflight refusal or one verified native rollback for an oversize fillet; retain constrained native-failure recovery evidence.",
                new string[] {
                    "Create short geometry-only lines whose selected right-angle legs are 1 mm; prepare radius=2 mm and chamfer distance1=4 mm to exceed available lengths.",
                    "Create a collinear endpoint pair and prepare a positive fillet radius.",
                    "Separately create geometry-only orthogonal lines, add one native 10 mm horizontal Driving dimension, save the fixture, record all native constraints, then submit a valid 2 mm fillet through the production execute path."
                },
                "Oversize fillet either refuses during prepare with no state change or enters one native call and returns ROLLED_BACK with exact model/dirty restoration; oversize chamfer and collinear cases refuse without state change. The constrained case either applies with all old Driving constraints semantically preserved, or proves complete native rollback. No oversize edit may return APPLIED or UNKNOWN.",
                delegate(Dictionary<string, object> row)
                {
                    SketchFixture shortFixture = CreateSketch(adapter, binding, testPart, "oversize-fixture", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> { Line("short-1", 0, 0, 1, 0), Line("short-2", 1, 0, 1, 1) }), row);
                    List<object> rejects = new List<object>();
                    row["preflight_rejections"] = rejects;
                    M3SketchCornerRequest oversizeFillet = CornerRequest(binding, shortFixture, "fillet",
                        "short-1", "end", "short-2", "start", 2.0, null, null, null);
                    rejects.Add(CheckOversizeFilletOutcome(adapter, binding, testPart, session,
                        oversizeFillet, "oversize_fillet", row));
                    rejects.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, shortFixture, "chamfer", "short-1", "end", "short-2", "start", null, 4.0, 4.0, null),
                        "oversize_chamfer", null, row));

                    SketchFixture collinear = CreateSketch(adapter, binding, testPart, "degenerate-fixture", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> { Line("collinear-1", 0, 0, 10, 0), Line("collinear-2", 10, 0, 20, 0) }), row);
                    rejects.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, collinear, "fillet", "collinear-1", "end", "collinear-2", "start", 1.0, null, null, null),
                        "collinear_corner", null, row));

                    SketchFixture constrained = CreateSketch(adapter, binding, testPart, "driving-constraints-native-failure-candidate", xy,
                        new M3SketchRequest {
                            OriginMm = Copy(xy.Origin), Normal = Copy(xy.Normal), XAxis = Copy(xy.XAxis),
                            ConstraintMode = "geometry_only",
                            Geometries = new List<M3SketchGeometryRequest> { Line("drive-1", 0, 0, 10, 0), Line("drive-2", 10, 0, 10, 10) }
                        }, row);
                    Sketch nativeSketch = FindSketch(testPart, constrained.SketchId);
                    AddNativeDrivingHorizontalDimension(adapter, binding, session, testPart, constrained, nativeSketch, row);
                    SavePart(testPart, "clean Driving-constraint failure candidate");
                    row["native_operation_called"] = false;
                    CaptureInspectPartWarmup(adapter, binding, testPart, row, "driving_failure_warmup");
                    Dictionary<string, object> drivingWarmup = row["driving_failure_warmup"] as Dictionary<string, object>;
                    Dictionary<string, object> cleanBeforePrepare = drivingWarmup == null
                        ? null : drivingWarmup["before_snapshot"] as Dictionary<string, object>;
                    Dictionary<string, object> afterWarmup = drivingWarmup == null
                        ? null : drivingWarmup["after_snapshot"] as Dictionary<string, object>;
                    row["driving_failure_candidate_clean_before_prepare"] = cleanBeforePrepare;
                    row["driving_failure_candidate_after_warmup"] = afterWarmup;
                    string constrainedRevisionBefore = binding.RevisionId;
                    row["driving_failure_revision_before"] = constrainedRevisionBefore;
                    M3SketchCornerRequest constrainedRequest = CornerRequest(binding, constrained, "fillet",
                        "drive-1", "end", "drive-2", "start", 2.0, null, null, null);
                    M3SketchCornerRequest staleProbeRequest = CornerRequest(binding, constrained, "fillet",
                        "drive-1", "end", "drive-2", "start", 1.0, null, null, null);
                    row["driving_candidate_request"] = CornerRequestRecord(constrainedRequest);
                    row["driving_candidate_stale_probe_request"] = CornerRequestRecord(staleProbeRequest);
                    M3PlanSnapshot constrainedPlan = null;
                    M3PlanSnapshot staleProbePlan = null;
                    Exception constrainedPrepareException = null;
                    Exception staleProbePrepareException = null;
                    M3ExecutionResult constrainedExecution = null;
                    Exception constrainedExecutionException = null;
                    M3VerificationResult constrainedVerification = null;
                    try { constrainedPlan = adapter.PrepareSketchCorner(binding, constrainedRequest); }
                    catch (Exception ex) { constrainedPrepareException = ex; }
                    row["driving_candidate_plan"] = constrainedPlan == null ? null : constrainedPlan.PublicView;
                    row["driving_candidate_prepare_exception"] = constrainedPrepareException == null
                        ? null : constrainedPrepareException.ToString();
                    if (constrainedPrepareException == null && constrainedPlan != null)
                    {
                        try { staleProbePlan = adapter.PrepareSketchCorner(binding, staleProbeRequest); }
                        catch (Exception ex) { staleProbePrepareException = ex; }
                    }
                    row["driving_candidate_stale_probe_plan"] = staleProbePlan == null ? null : staleProbePlan.PublicView;
                    row["driving_candidate_stale_probe_prepare_exception"] = staleProbePrepareException == null
                        ? null : staleProbePrepareException.ToString();
                    row["driving_candidate_revision_after_prepare"] = binding.RevisionId;
                    string activeBeforeExecute = ActiveSketchId(session);
                    row["driving_candidate_active_sketch_before_execute"] = activeBeforeExecute;
                    Dictionary<string, object> prepareAfterFull = NativeSnapshot(testPart, null);
                    row["driving_candidate_after_prepare_full_snapshot"] = prepareAfterFull;
                    row["driving_candidate_prepare_snapshot_diff"] = SnapshotDiff(afterWarmup, prepareAfterFull);
                    bool prepareSnapshotUnchanged = afterWarmup != null && SameSnapshot(afterWarmup, prepareAfterFull);
                    bool prepareRevisionUnchanged = String.Equals(constrainedRevisionBefore, binding.RevisionId, StringComparison.Ordinal);
                    row["driving_candidate_prepare_snapshot_unchanged"] = prepareSnapshotUnchanged;
                    row["driving_candidate_prepare_revision_unchanged"] = prepareRevisionUnchanged;
                    Require(constrainedPrepareException == null && constrainedPlan != null && staleProbePrepareException == null && staleProbePlan != null,
                        "Driving-candidate plans were not prepared: " + M2Json.CanonicalText(row));
                    Require(prepareSnapshotUnchanged && prepareRevisionUnchanged,
                        "Preparing the Driving-candidate plans changed native state after materialization: " +
                        M2Json.CanonicalText(row["driving_candidate_prepare_snapshot_diff"]));

                    Dictionary<string, object> baseline = NativeSnapshot(testPart, nativeSketch);
                    row["driving_failure_candidate_before_execute"] = baseline;
                    List<object> baselineConstraints = SnapshotList(baseline, "constraints");
                    row["driving_failure_constraints_before_execute"] = baselineConstraints;
                    Require(CountDrivingConstraints(baselineConstraints) > 0,
                        "The native-dimension failure candidate has no native Driving constraint to protect.");
                    string revisionBeforeExecute = binding.RevisionId;
                    row["driving_failure_revision_before_execute"] = revisionBeforeExecute;

                    try { constrainedExecution = adapter.ExecutePlan(binding, constrainedPlan); }
                    catch (Exception ex) { constrainedExecutionException = ex; }
                    row["driving_candidate_execution"] = ExecutionRecord(constrainedExecution);
                    row["execute_result"] = ExecutionRecord(constrainedExecution);
                    row["driving_candidate_execute_exception"] = constrainedExecutionException == null
                        ? null : constrainedExecutionException.ToString();
                    row["native_operation_called"] = NativeCallWasRecorded(row);
                    if (constrainedExecution != null && constrainedExecution.State == "APPLIED")
                    {
                        try { constrainedVerification = adapter.VerifyPlan(binding, constrainedPlan); }
                        catch (Exception ex) { row["driving_candidate_verification_exception"] = ex.ToString(); }
                        row["driving_candidate_verification"] = VerificationRecord(constrainedVerification);
                    }
                    string constrainedRevisionAfter = binding.RevisionId;
                    row["driving_failure_revision_after"] = constrainedRevisionAfter;
                    row["driving_failure_revision_did_not_decrease"] = RevisionNotLower(revisionBeforeExecute, constrainedRevisionAfter);
                    string activeAfterExecute = ActiveSketchId(session);
                    row["driving_candidate_active_sketch_after_execute"] = activeAfterExecute;
                    Dictionary<string, object> after = NativeSnapshot(testPart, FindSketch(testPart, constrained.SketchId));
                    row["driving_failure_candidate_after"] = after;
                    Dictionary<string, object> afterFull = NativeSnapshot(testPart, null);
                    row["driving_failure_candidate_after_full_snapshot"] = afterFull;
                    row["driving_failure_candidate_full_snapshot_diff_from_execute_baseline"] = SnapshotDiff(prepareAfterFull, afterFull);
                    if (constrainedExecution != null && constrainedExecution.State == "APPLIED")
                    {
                        Require(constrainedVerification != null && constrainedVerification.Verified,
                            "The constrained operation reported APPLIED without independent plan verification.");
                        Require(OriginalConstraintsPreserved(baselineConstraints, SnapshotList(after, "constraints")),
                            "A successful native fillet removed or changed a preexisting Driving constraint.");
                        Require(!String.Equals(revisionBeforeExecute, constrainedRevisionAfter, StringComparison.Ordinal),
                            "A successful constrained native fillet did not advance the model revision.");
                        row["driving_candidate_outcome"] = "APPLIED_WITH_OLD_CONSTRAINTS_PRESERVED";
                    }
                    else
                    {
                        Require(SameSnapshot(baseline, after),
                            "A failed constrained native fillet did not restore the exact post-prepare geometry, constraints, dirty state, and inventory.");
                        Require(SameSnapshot(prepareAfterFull, afterFull),
                            "A failed constrained native fillet did not restore the full pre-execute model snapshot.");
                        Require(RevisionNotLower(revisionBeforeExecute, constrainedRevisionAfter),
                            "A failed constrained native fillet moved the model revision backwards after rollback.");
                        Require(String.Equals(activeBeforeExecute, activeAfterExecute, StringComparison.Ordinal),
                            "A failed constrained native fillet changed the active Sketch after rollback.");
                        string mutation = constrainedExecution == null ? null : constrainedExecution.State;
                        Require(mutation == "ROLLED_BACK",
                            "Constrained native failure did not report a verified ROLLED_BACK state.");
                        Require(mutation != "UNKNOWN", "Constrained native failure returned UNKNOWN and requires human recovery.");
                        Require(NativeCallWasRecorded(row),
                            "Driving-conflict failure evidence does not prove the NX Sketch.Fillet call was entered.");
                        row["driving_candidate_outcome"] = "NATIVE_FAILURE_ROLLED_BACK";
                    }
                    if ((constrainedExecution != null &&
                        (String.Equals(constrainedExecution.State, "APPLIED", StringComparison.Ordinal) ||
                         String.Equals(constrainedExecution.State, "ROLLED_BACK", StringComparison.Ordinal))) &&
                        staleProbePlan != null && !String.Equals(staleProbePlan.RevisionId, constrainedRevisionAfter, StringComparison.Ordinal))
                    {
                        Exception staleProbeException;
                        M3ExecutionResult staleProbeExecution = ExecuteExpectStale(adapter, binding, staleProbePlan, out staleProbeException);
                        row["driving_candidate_stale_probe_execution"] = staleProbeExecution == null ? null : ExecutionRecord(staleProbeExecution);
                        row["driving_candidate_stale_probe_exception"] = staleProbeException == null ? null : staleProbeException.ToString();
                        Require(IsStalePlanRejection(staleProbeExecution, staleProbeException),
                            "An unexecuted plan prepared before the constrained native result was not invalidated by the updated revision.");
                        Dictionary<string, object> afterStaleProbe = NativeSnapshot(testPart, null);
                        row["driving_candidate_after_stale_probe_snapshot"] = afterStaleProbe;
                        row["driving_candidate_stale_probe_snapshot_diff"] = SnapshotDiff(afterFull, afterStaleProbe);
                        Require(SameSnapshot(afterFull, afterStaleProbe),
                            "The stale constrained-candidate plan changed model state when rejected.");
                        Require(String.Equals(constrainedRevisionAfter, binding.RevisionId, StringComparison.Ordinal),
                            "Rejecting the stale constrained-candidate plan changed the model revision.");
                    }
                    else
                    {
                        row["driving_candidate_stale_probe_status"] = staleProbePlan == null
                            ? "NOT_PREPARED"
                            : (String.Equals(staleProbePlan.RevisionId, constrainedRevisionAfter, StringComparison.Ordinal)
                                ? "REVISION_UNCHANGED_NO_STALE_SUBMISSION"
                                : "NOT_APPLICABLE_FOR_UNKNOWN_OR_UNCHANGED_EXECUTION");
                    }
                });

            RunCase(reportPath, evidence, cases, defects, "F07_stale_plan_and_external_line_edit_revision",
                "Prove corner edits advance semantic revision, stale plans are rejected, and native in-place line edits invalidate plans.",
                new string[] {
                    "Create two disconnected right-angle line pairs in one geometry-only sketch and prepare distinct valid corner plans against one revision.",
                    "Execute and independently verify the first plan; attempt the second old plan and capture stale refusal/state.",
                    "Create a separate geometry-only Line sketch; prepare a valid corner plan, then change the selected Line's far endpoint with NXOpen.Line.SetStartPoint under an NX Undo mark.",
                    "Run NX UpdateManager.DoUpdate, refresh inspect/revision, submit the stale plan, and then Undo the external edit."
                },
                "A successful corner edit advances revision and the old unexecuted plan returns STALE_PLAN/STALE_REVISION with UNCHANGED state and no native call. Direct Line edit preserves Tag, JournalIdentifier, feature/sketch/body inventory, and curve count while changing geometry/revision; stale plan rejects; Undo restores the exact Line and recorded pre-edit dirty state.",
                delegate(Dictionary<string, object> row)
                {
                    SketchFixture twoCorners = CreateSketch(adapter, binding, testPart, "two-corner-stale-plan", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> {
                            Line("a1", 0, 0, 10, 0), Line("a2", 10, 0, 10, 10),
                            Line("b1", 20, 0, 30, 0), Line("b2", 30, 0, 30, 10)
                        }), row);
                    M3SketchCornerRequest requestA = CornerRequest(binding, twoCorners, "fillet", "a1", "end", "a2", "start", 1.0, null, null, null);
                    M3PlanSnapshot planA = adapter.PrepareSketchCorner(binding, requestA);
                    string preparedRevision = binding.RevisionId;
                    M3SketchCornerRequest requestB = CornerRequest(binding, twoCorners, "fillet", "b1", "end", "b2", "start", 1.0, null, null, null);
                    M3PlanSnapshot staleByCornerEdit = adapter.PrepareSketchCorner(binding, requestB);
                    row["prepared_revision"] = preparedRevision;
                    CornerRun first = ExecutePreparedCorner(adapter, binding, planA, requestA, row, "first-of-two-corners");
                    Require(first.Verification != null && first.Verification.Verified, "First corner plan did not verify.");
                    string revisionAfterCorner = binding.RevisionId;
                    Require(!String.Equals(preparedRevision, revisionAfterCorner, StringComparison.Ordinal),
                        "Successful F07 geometry edit did not advance the semantic revision.");
                    Dictionary<string, object> afterFirst = NativeSnapshot(testPart, FindSketch(testPart, twoCorners.SketchId));
                    Exception staleError;
                    M3ExecutionResult staleResult = ExecuteExpectStale(adapter, binding, staleByCornerEdit, out staleError);
                    row["revision_after_corner_edit"] = revisionAfterCorner;
                    row["stale_corner_execution"] = staleResult == null ? null : ExecutionRecord(staleResult);
                    row["stale_corner_exception"] = staleError == null ? null : staleError.ToString();
                    Dictionary<string, object> afterStaleCornerPlan = NativeSnapshot(testPart, FindSketch(testPart, twoCorners.SketchId));
                    row["native_after_stale_corner_plan"] = afterStaleCornerPlan;
                    row["stale_corner_snapshot_diff"] = SnapshotDiff(afterFirst, afterStaleCornerPlan);
                    row["stale_corner_revision_after_rejection"] = binding.RevisionId;
                    Require(IsStaleRejection(staleResult, staleError),
                        "Stale F07 plan did not return an explicit STALE_PLAN/STALE_REVISION unchanged result.");
                    Require(staleResult == null || !NativeCallWasRecorded(staleResult.Result),
                        "Stale F07 plan unexpectedly entered a native NX operation.");
                    Require(SameSnapshot(afterFirst, afterStaleCornerPlan),
                        "Stale F07 plan changed native geometry or constraints.");
                    Require(String.Equals(revisionAfterCorner, binding.RevisionId, StringComparison.Ordinal),
                        "Rejecting a stale F07 plan changed the semantic revision.");

                    SketchFixture editFixture = CreateSketch(adapter, binding, testPart, "external-line-edit", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> {
                            Line("edit-line1", 0, 20, 20, 20), Line("edit-line2", 20, 20, 20, 30)
                        }), row);
                    M3SketchCornerRequest staleByNativeEditRequest = CornerRequest(binding, editFixture, "chamfer",
                        "edit-line1", "end", "edit-line2", "start", null, 2.0, 2.0, null);
                    M3PlanSnapshot staleByNativeEdit = adapter.PrepareSketchCorner(binding, staleByNativeEditRequest);
                    Sketch sketch = FindSketch(testPart, editFixture.SketchId);
                    Line target = FindLine(sketch, WorldPoint(xy, 0, 20), WorldPoint(xy, 20, 20));
                    Point3d startBefore = target.StartPoint;
                    Point3d endBefore = target.EndPoint;
                    string tagBefore = target.Tag.ToString();
                    string journalBefore = target.JournalIdentifier;
                    Dictionary<string, object> modelBeforeEdit = ModelIdRecord(testPart);
                    int curveCountBeforeEdit = CountCurveObjects(sketch);
                    string editRevisionBefore = binding.RevisionId;
                    Dictionary<string, object> originalEditSnapshot = NativeSnapshot(testPart, sketch);
                    row["external_edit_baseline"] = originalEditSnapshot;
                    Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F07 external Line edit");
                    bool markActive = true;
                    try
                    {
                        target.SetStartPoint(new Point3d(startBefore.X + 1.0, startBefore.Y, startBefore.Z));
                        int updateErrors = session.UpdateManager.DoUpdate(mark);
                        row["line_edit_update_error_count"] = updateErrors;
                        Require(updateErrors == 0, "NX update manager reported errors after the native Line edit.");
                        Dictionary<string, object> nativeAfterEdit = NativeSnapshot(testPart, sketch);
                        row["native_line_after_external_edit"] = nativeAfterEdit;
                        Require(String.Equals(tagBefore, target.Tag.ToString(), StringComparison.Ordinal) &&
                            String.Equals(journalBefore, target.JournalIdentifier, StringComparison.Ordinal),
                            "In-place native Line edit changed Tag or JournalIdentifier.");
                        Require(CountCurveObjects(sketch) == curveCountBeforeEdit,
                            "In-place native Line edit changed sketch Curve count.");
                        Require(SameModelIdRecord(modelBeforeEdit, ModelIdRecord(testPart), includeModified: false),
                            "In-place native Line edit changed feature/sketch/body identity inventory.");
                        Dictionary<string, object> refreshed = adapter.InspectSketch(binding, editFixture.SketchId);
                        row["inspect_after_external_line_edit"] = refreshed;
                        string editRevisionAfter = binding.RevisionId;
                        Require(!String.Equals(editRevisionBefore, editRevisionAfter, StringComparison.Ordinal),
                            "Direct line shape edit did not change the binding semantic revision after inspect refresh.");
                        Dictionary<string, object> beforeStaleNativeEditPlan = NativeSnapshot(testPart, sketch);
                        row["native_before_stale_external_edit_plan"] = beforeStaleNativeEditPlan;
                        Exception nativeStaleError;
                        M3ExecutionResult nativeStaleResult = ExecuteExpectStale(adapter, binding, staleByNativeEdit, out nativeStaleError);
                        row["revision_before_external_edit"] = editRevisionBefore;
                        row["revision_after_external_edit"] = editRevisionAfter;
                        row["stale_after_external_edit_result"] = nativeStaleResult == null ? null : ExecutionRecord(nativeStaleResult);
                        row["stale_after_external_edit_exception"] = nativeStaleError == null ? null : nativeStaleError.ToString();
                        Dictionary<string, object> afterStaleNativeEditPlan = NativeSnapshot(testPart, sketch);
                        row["native_after_stale_external_edit_plan"] = afterStaleNativeEditPlan;
                        row["stale_after_external_edit_snapshot_diff"] = SnapshotDiff(beforeStaleNativeEditPlan, afterStaleNativeEditPlan);
                        Require(IsStaleRejection(nativeStaleResult, nativeStaleError),
                            "Plan prepared before direct Line edit did not return an explicit STALE_PLAN/STALE_REVISION unchanged result.");
                        Require(nativeStaleResult == null || !NativeCallWasRecorded(nativeStaleResult.Result),
                            "Plan prepared before direct Line edit unexpectedly entered a native NX operation.");
                        Require(SameSnapshot(beforeStaleNativeEditPlan, afterStaleNativeEditPlan),
                            "Rejecting a plan prepared before direct Line edit changed model state.");
                        Require(String.Equals(editRevisionAfter, binding.RevisionId, StringComparison.Ordinal),
                            "Rejecting a plan prepared before direct Line edit changed the semantic revision.");
                    }
                    finally
                    {
                        if (markActive)
                        {
                            session.UndoToMark(mark, "F07 external Line edit recovery");
                            session.DeleteUndoMark(mark, "F07 external Line edit recovery");
                        }
                    }
                    markActive = false;
                    Dictionary<string, object> restored = NativeSnapshot(testPart, sketch);
                    row["native_after_undo"] = restored;
                    Require(SameGeometryAndConstraints(NativeSnapshotFromOriginalRecord(row["external_edit_baseline"]), restored),
                        "Undo did not restore exact native line geometry and constraints.");
                    object dirtyBeforeEdit = null;
                    object dirtyAfterUndo = null;
                    originalEditSnapshot.TryGetValue("is_modified", out dirtyBeforeEdit);
                    restored.TryGetValue("is_modified", out dirtyAfterUndo);
                    row["external_edit_dirty_before_edit"] = dirtyBeforeEdit;
                    row["external_edit_dirty_after_undo"] = dirtyAfterUndo;
                    Require(Object.Equals(dirtyBeforeEdit, dirtyAfterUndo),
                        "Undo did not restore the recorded pre-edit IsModified state.");
                    PointNear(target.StartPoint, startBefore, "Undo did not restore the exact Line start point.");
                    PointNear(target.EndPoint, endBefore, "Undo changed the Line end point.");
                    Require(String.Equals(tagBefore, target.Tag.ToString(), StringComparison.Ordinal) &&
                        String.Equals(journalBefore, target.JournalIdentifier, StringComparison.Ordinal),
                        "Undo did not preserve the edited Line identity.");
                    Dictionary<string, object> inspectAfterUndo = adapter.InspectSketch(binding, editFixture.SketchId);
                    row["inspect_after_external_edit_undo"] = inspectAfterUndo;
                    M3VerificationResult restoredSketchVerification = adapter.VerifyPlan(binding, editFixture.CreatePlan);
                    row["verification_after_external_edit_undo"] = VerificationRecord(restoredSketchVerification);
                    Require(restoredSketchVerification != null && restoredSketchVerification.Verified && ChecksPass(restoredSketchVerification.Checks),
                        "Undo did not restore the original sketch creation plan's native geometry verification.");
                });

            RunCase(reportPath, evidence, cases, defects, "F07_invalid_numbers_are_read_only",
                "Verify direct adapter boundary checks reject non-finite and non-positive dimensions without touching the NX model.",
                new string[] {
                    "Create and save a clean valid right-angle sketch.",
                    "Call PrepareSketchCorner with zero/negative/infinite radius and chamfer distance, zero/out-of-range angle, and conflicting distance2 plus angle.",
                    "Compare native geometry, constraints, feature/sketch/body inventory, modified state, and binding revision before and after each call."
                },
                "Every malformed numeric request is refused with a nonempty model-validation error before NX mutation; snapshots and revision remain exactly unchanged.",
                delegate(Dictionary<string, object> row)
                {
                    SketchFixture fixture = CreateSketch(adapter, binding, testPart, "invalid-numeric-fixture", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> { Line("n1", 0, 0, 20, 0), Line("n2", 20, 0, 20, 10) }), row);
                    List<object> rejects = new List<object>();
                    row["numeric_rejection_attempts"] = rejects;
                    rejects.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, fixture, "fillet", "n1", "end", "n2", "start", 0.0, null, null, null), "zero-radius", null, row));
                    rejects.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, fixture, "fillet", "n1", "end", "n2", "start", -1.0, null, null, null), "negative-radius", null, row));
                    rejects.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, fixture, "fillet", "n1", "end", "n2", "start", Double.PositiveInfinity, null, null, null), "infinite-radius", null, row));
                    rejects.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, fixture, "chamfer", "n1", "end", "n2", "start", null, 0.0, 2.0, null), "zero-distance", null, row));
                    rejects.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, fixture, "chamfer", "n1", "end", "n2", "start", null, 4.0, null, 90.0), "right-angle-domain", null, row));
                    rejects.Add(CheckPrepareRejected(adapter, binding, testPart, session,
                        CornerRequest(binding, fixture, "chamfer", "n1", "end", "n2", "start", null, 4.0, 2.0, 30.0), "conflicting-chamfer-mode", null, row));
                });

            RunCase(reportPath, evidence, cases, defects, "F07_save_reopen_native_corner_geometry",
                "Save and reopen a production copy, then directly reread all valid fillet and chamfer results.",
                new string[] {
                    "Call production M2OpenModelAdapter.SaveCopy with a unique round-trip path and reopen=true.",
                    "Require SAVED, reopened=true, and every production SaveCopy check VERIFIED.",
                    "Resolve each successful sketch after reopen and repeat direct native curve/endpoint/radius/length/ownership checks."
                },
                "The target part reopens as the NX Work Part; all accepted F07 sketch IDs remain resolvable and their native curves, tangent points, dimensions, constraints, and ownership match recorded expected values.",
                delegate(Dictionary<string, object> row)
                {
                    List<object> roundtrip = new List<object>();
                    row["native_roundtrip_readbacks"] = roundtrip;
                    // Later seed sketches may renumber JournalIdentifiers. Capture the actual
                    // save boundary rather than comparing reopen with an earlier edit's IDs.
                    foreach (SketchFixture fixture in fixturesToRecheck)
                    {
                        Dictionary<string, object> beforeSave = NativeSnapshot(testPart, FindSketch(testPart, fixture.SketchId));
                        Dictionary<string, object> beforeProjection = NativeGeometryConstraintProjection(beforeSave);
                        Dictionary<string, object> originalConsistency = PreSaveConsistencyProjection(fixture.NativeAfter);
                        Dictionary<string, object> freshConsistency = PreSaveConsistencyProjection(beforeSave);
                        bool preSaveConsistent = originalConsistency != null && freshConsistency != null &&
                            SameValue(originalConsistency, freshConsistency);
                        Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal) {
                            { "name", fixture.Name }, { "sketch_id", fixture.SketchId },
                            { "original_post_edit_snapshot", fixture.NativeAfter },
                            { "before_save_native_snapshot", beforeSave },
                            { "before_projection", beforeProjection },
                            { "original_consistency_projection", originalConsistency },
                            { "fresh_consistency_projection", freshConsistency },
                            { "pre_save_consistency_diff", SnapshotDiff(originalConsistency, freshConsistency) },
                            { "pre_save_consistent", preSaveConsistent },
                            { "original_post_edit_to_before_save_projection_diff", SnapshotDiff(
                                NativeGeometryConstraintProjection(fixture.NativeAfter), beforeProjection) }
                        };
                        roundtrip.Add(item);
                        Require(preSaveConsistent,
                            "Later fixtures changed native geometry or constraint dependencies before save for " + fixture.Name + ".");
                    }
                    M2SaveCopyResult copy = adapter.SaveCopy(binding, savedPath, true);
                    row["save_copy"] = SaveCopyRecord(copy);
                    bool savedAndReopened = copy != null && copy.State == "SAVED" && copy.Reopened;
                    bool saveChecksVerified = copy != null && ChecksPass(copy.Checks);
                    row["save_copy_saved_and_reopened"] = savedAndReopened;
                    row["save_copy_checks_verified"] = saveChecksVerified;
                    Require(savedAndReopened,
                        "Production SaveCopy did not save and reopen the F07 test part.");
                    Require(saveChecksVerified, "One or more F07 SaveCopy checks did not report VERIFIED.");
                    Require(File.Exists(savedPath), "F07 round-trip model file was not created.");
                    Part reopened = session.Parts.Work as Part;
                    Require(reopened != null && String.Equals(Path.GetFullPath(reopened.FullPath), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase),
                        "NX Work Part is not the unique F07 SaveCopy target after reopen.");
                    for (int index = 0; index < fixturesToRecheck.Count; index++)
                    {
                        SketchFixture fixture = fixturesToRecheck[index];
                        Dictionary<string, object> item = roundtrip[index] as Dictionary<string, object>;
                        Sketch sketch = FindSketch(reopened, fixture.SketchId);
                        Dictionary<string, object> direct = NativeSnapshot(reopened, sketch);
                        Dictionary<string, object> beforeSave = item["before_save_native_snapshot"] as Dictionary<string, object>;
                        Dictionary<string, object> afterProjection = NativeGeometryConstraintProjection(direct);
                        item["native_readback"] = direct;
                        item["after_projection"] = afterProjection;
                        item["native_snapshot_diff"] = SnapshotDiff(beforeSave, direct);
                        item["comparison_projection_diff"] = SnapshotDiff(item["before_projection"], afterProjection);
                        item["save_copy_checks_verified"] = saveChecksVerified;
                        bool comparisonPassed = SameNativeGeometryAndConstraints(beforeSave, direct);
                        item["geometry_and_constraints_match"] = comparisonPassed;
                        Require(comparisonPassed,
                            "Save/reopen changed native F07 geometry or constraints for " + fixture.Name + ".");
                        item["production_inspection"] = adapter.InspectSketch(binding, fixture.SketchId);
                    }
                });

            evidence["all_cases_passed"] = true;
        }
        catch (Exception ex)
        {
            evidence["error_case"] = evidence.ContainsKey("current_case") ? evidence["current_case"] : "setup_or_harness";
            evidence["error"] = ex.ToString();
            evidence["passed"] = false;
            if (testPart != null)
            {
                try
                {
                    SavePart(testPart, "unique F07 failure-state diagnostic model");
                    evidence["failed_state_model_retained"] = testPart.FullPath;
                }
                catch (Exception saveError) { evidence["failure_model_save_error"] = saveError.ToString(); }
            }
            if (!evidence.ContainsKey("current_case"))
            {
                defects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "test_case", "setup_or_harness" },
                    { "reproduction_steps", new string[] { "Build the current production M2 assemblies and F07 acceptance offline.", "Run RunF07Acceptance.py in the designated idle NX session.", "Inspect the unique report and model paths retained in this record." } },
                    { "observed_exception", ex.ToString() },
                    { "retest_criterion", "Correct the setup or harness issue and rerun all F07 cases on a new unique model path." }
                });
            }
        }
        finally
        {
            try { if (binding != null && adapter != null) adapter.Release(binding); }
            catch (Exception ex) { evidence["release_error"] = ex.ToString(); }
            try
            {
                if (session != null)
                {
                    List<Part> owned = FindOwnedParts(session, sourcePath, savedPath);
                    if (originalPart != null)
                    {
                        PartLoadStatus displayStatus;
                        session.Parts.SetDisplay(originalPart, false, false, out displayStatus);
                        if (displayStatus != null) displayStatus.Dispose();
                        session.Parts.SetWork(originalPart);
                        foreach (Part part in owned)
                        {
                            if (part.Tag == originalPart.Tag) continue;
                            try { part.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null); }
                            catch (Exception closeError) { AddRestoreError(evidence, closeError); }
                        }
                        evidence["original_part_restored"] = session.Parts.Work != null &&
                            session.Parts.Work.Tag == originalPart.Tag && !originalPart.IsModified;
                    }
                    else
                    {
                        foreach (Part part in owned)
                        {
                            try { part.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null); }
                            catch (Exception closeError) { AddRestoreError(evidence, closeError); }
                        }
                        evidence["empty_original_session_restored"] = session.Parts.Work == null;
                    }
                }
            }
            catch (Exception ex) { AddRestoreError(evidence, ex); }

            evidence["finished_utc"] = UtcNow();
            evidence["final_work_part_path"] = session == null || session.Parts.Work == null ? null : session.Parts.Work.FullPath;
            bool allCasesPassed = cases.Count > 0;
            foreach (object item in cases)
            {
                Dictionary<string, object> testCase = item as Dictionary<string, object>;
                if (testCase == null || !String.Equals(Convert.ToString(testCase["status"], CultureInfo.InvariantCulture), "PASSED", StringComparison.Ordinal))
                    allCasesPassed = false;
            }
            evidence["passed"] = allCasesPassed && !evidence.ContainsKey("restore_errors") &&
                ((!evidence.ContainsKey("original_part_restored") || Object.Equals(evidence["original_part_restored"], true)) &&
                 (!evidence.ContainsKey("empty_original_session_restored") || Object.Equals(evidence["empty_original_session_restored"], true)));
            evidence["case_count"] = cases.Count;
            evidence["defect_count"] = defects.Count;
            Persist(reportPath, evidence);
        }
    }

    private static SketchFixture CreateSketch(M2OpenModelAdapter adapter, M2ModelBinding binding, Part part,
        string name, Frame frame, M3SketchRequest request, Dictionary<string, object> evidence)
    {
        string revisionBeforeWarmup = binding.RevisionId;
        request.RevisionId = revisionBeforeWarmup;
        request.OriginMm = Copy(frame.Origin);
        request.Normal = Copy(frame.Normal);
        request.XAxis = Copy(frame.XAxis);
        Dictionary<string, object> seedEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "name", name }, { "status", "WARMING_UP" }, { "units", "mm" },
            { "frame", FrameRecord(frame) }, { "request_before_warmup", SketchRequestRecord(request) },
            { "revision_before_warmup", revisionBeforeWarmup }
        };
        List<object> seedEvidenceList = GetOrCreateSeedEvidenceList(evidence);
        seedEvidenceList.Add(seedEvidence);
        evidence["last_seed_sketch"] = seedEvidence;
        CaptureInspectPartWarmup(adapter, binding, part, seedEvidence, "warmup");

        string revisionBefore = binding.RevisionId;
        request.RevisionId = revisionBefore;
        seedEvidence["request"] = SketchRequestRecord(request);
        seedEvidence["revision_before_prepare"] = revisionBefore;
        seedEvidence["status"] = "PREPARING";
        Dictionary<string, object> before = null;
        try { before = NativeSnapshot(part, null); }
        catch (Exception ex) { seedEvidence["before_prepare_snapshot_exception"] = ex.ToString(); throw; }
        seedEvidence["before_prepare_snapshot"] = before;

        M3PlanSnapshot plan = null;
        Exception prepareException = null;
        try { plan = adapter.PrepareSketch(binding, request); }
        catch (Exception ex) { prepareException = ex; seedEvidence["prepare_exception"] = ex.ToString(); }
        seedEvidence["plan"] = plan == null ? null : plan.PublicView;
        seedEvidence["revision_after_prepare"] = binding.RevisionId;

        Dictionary<string, object> afterPrepare = null;
        try { afterPrepare = NativeSnapshot(part, null); }
        catch (Exception ex) { seedEvidence["after_prepare_snapshot_exception"] = ex.ToString(); }
        seedEvidence["after_prepare_snapshot"] = afterPrepare;
        bool prepareSnapshotSame = before != null && afterPrepare != null && SameSnapshot(before, afterPrepare);
        seedEvidence["prepare_snapshot_unchanged"] = prepareSnapshotSame;
        seedEvidence["prepare_snapshot_diff"] = SnapshotDiff(before, afterPrepare);
        bool prepareRevisionSame = String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal);
        seedEvidence["prepare_revision_unchanged"] = prepareRevisionSame;
        seedEvidence["status"] = "PREPARE_CAPTURED";

        Require(prepareException == null,
            "Production PrepareSketch threw: " + M2Json.CanonicalText(prepareException == null ? null : prepareException.ToString()));
        Require(plan != null,
            "Production PrepareSketch returned a null fixture plan: " + M2Json.CanonicalText(seedEvidence["plan"]));
        Require(prepareSnapshotSame,
            "Preparing the F07 seed sketch changed native model state: " + M2Json.CanonicalText(seedEvidence["prepare_snapshot_diff"]));
        Require(prepareRevisionSame,
            "Preparing the F07 seed sketch changed the binding revision: " + M2Json.CanonicalText(new Dictionary<string, object> {
                { "revision_before_prepare", revisionBefore }, { "revision_after_prepare", binding.RevisionId }
            }));

        M3ExecutionResult execution = null;
        Exception executionException = null;
        try { execution = adapter.ExecutePlan(binding, plan); }
        catch (Exception ex)
        {
            executionException = ex;
            seedEvidence["execute_exception"] = ex.ToString();
        }
        seedEvidence["execution"] = ExecutionRecord(execution);
        seedEvidence["execution_result"] = execution == null ? null : execution.Result;
        seedEvidence["revision_after_execute"] = binding.RevisionId;
        Dictionary<string, object> afterExecute = null;
        try { afterExecute = NativeSnapshot(part, null); }
        catch (Exception ex) { seedEvidence["after_execute_snapshot_exception"] = ex.ToString(); }
        seedEvidence["after_execute_snapshot"] = afterExecute;
        seedEvidence["execute_snapshot_diff_from_baseline"] = SnapshotDiff(before, afterExecute);
        seedEvidence["status"] = "EXECUTE_CAPTURED";
        Require(executionException == null,
            "F07 seed sketch ExecutePlan threw: " + M2Json.CanonicalText(executionException == null ? null : executionException.ToString()));
        Require(execution != null && String.Equals(execution.State, "APPLIED", StringComparison.Ordinal),
            "F07 seed sketch execution did not return APPLIED: " + M2Json.CanonicalText(ExecutionRecord(execution)));

        M3VerificationResult verification = null;
        Exception verificationException = null;
        try { verification = adapter.VerifyPlan(binding, plan); }
        catch (Exception ex) { verificationException = ex; seedEvidence["verification_exception"] = ex.ToString(); }
        seedEvidence["verification"] = VerificationRecord(verification);
        seedEvidence["verification_result"] = verification == null ? null : verification.Result;
        seedEvidence["revision_after_verify"] = binding.RevisionId;
        Dictionary<string, object> afterVerify = null;
        try { afterVerify = NativeSnapshot(part, null); }
        catch (Exception ex) { seedEvidence["after_verify_snapshot_exception"] = ex.ToString(); }
        seedEvidence["after_verify_snapshot"] = afterVerify;
        seedEvidence["verify_snapshot_diff_from_after_execute"] = SnapshotDiff(afterExecute, afterVerify);
        seedEvidence["status"] = "VERIFY_CAPTURED";
        Require(verificationException == null,
            "F07 seed sketch VerifyPlan threw: " + M2Json.CanonicalText(verificationException == null ? null : verificationException.ToString()));
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "F07 seed sketch did not pass production verification: " + M2Json.CanonicalText(VerificationRecord(verification)));
        string sketchId = ReadRequiredString(execution.Result, "sketch_id");
        seedEvidence["sketch_id"] = sketchId;
        Sketch native = FindSketch(part, sketchId);
        CheckNativeFrame(native, frame, evidence);
        Dictionary<string, object> inspection = adapter.InspectSketch(binding, sketchId);
        seedEvidence["inspection"] = inspection;
        List<string> ids = new List<string>();
        Dictionary<string, string> resultGeometryIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (M3SketchGeometryRequest geometry in request.Geometries)
        {
            string found = FindInspectedGeometryId(inspection, geometry, frame);
            Require(!String.IsNullOrWhiteSpace(found), "InspectSketch did not expose a native JournalIdentifier for requested curve " + geometry.Id + ".");
            ids.Add(found);
            resultGeometryIds.Add(geometry.Id, found);
        }
        Dictionary<string, object> direct = NativeSnapshot(part, native);
        seedEvidence["expected_curve_count_after_one_fillet"] = request.Geometries.Count + 1;
        seedEvidence["native_geometry_and_constraints"] = direct;
        SketchFixture result = new SketchFixture {
            Name = name, SketchId = sketchId, Frame = frame, CreatePlan = plan, SeedEvidence = seedEvidence,
            GeometryIds = ids, GeometryRequests = new List<M3SketchGeometryRequest>(request.Geometries),
            GeometryIdsByRequestId = resultGeometryIds,
            ExpectedCurveCountAfterOneFillet = request.Geometries.Count + 1
        };
        seedEvidence["geometry_ids"] = StringObjectMapRecord(resultGeometryIds);
        seedEvidence["status"] = "READY_TO_SAVE";
        try { SavePart(part, name + " fixture"); }
        catch (Exception ex) { seedEvidence["save_fixture_exception"] = ex.ToString(); throw; }
        seedEvidence["status"] = "READY";
        seedEvidence["saved_part_path"] = part.FullPath;
        return result;
    }

    private static void AddNativeDrivingHorizontalDimension(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Session session, Part part, SketchFixture fixture, Sketch sketch, Dictionary<string, object> parentEvidence)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", "RUNNING" }, { "fixture_name", fixture.Name }, { "sketch_id", sketch.JournalIdentifier },
            { "request_geometry_id", "drive-1" }, { "requested_horizontal_span_mm", 10.0 },
            { "revision_before", binding.RevisionId }, { "active_sketch_before", ActiveSketchId(session) },
            { "before_snapshot", NativeSnapshot(part, sketch) }
        };
        parentEvidence["native_driving_dimension_fixture"] = record;
        Require(session.ActiveSketch == null, "Native Driving fixture requires no active sketch.");
        Line line = FindLine(sketch, WorldPoint(fixture.Frame, 0, 0), WorldPoint(fixture.Frame, 10, 0));
        record["line_tag"] = line.Tag.ToString();
        record["line_journal_id_before"] = line.JournalIdentifier;
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F07 native Driving fixture");
        bool activated = false;
        try
        {
            sketch.Activate(Sketch.ViewReorient.False);
            activated = true;
            SketchDimensionalConstraint dimension = sketch.CreateDimension(Sketch.ConstraintType.HorizontalDim,
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.StartPoint, AssocValue = 0 },
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.EndPoint, AssocValue = 0 },
                WorldPoint(fixture.Frame, 5, -3), null, Sketch.DimensionOption.CreateAsDriving);
            Require(dimension != null && dimension.AssociatedExpression != null,
                "Native CreateDimension returned no dimensional constraint or expression.");
            dimension.AssociatedExpression.RightHandSide = "10";
            record["dimension_journal_id"] = dimension.JournalIdentifier;
            record["dimension_rhs_after_create"] = dimension.AssociatedExpression.RightHandSide;
            sketch.Update();
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            activated = false;
            int updateErrors = session.UpdateManager.DoUpdate(mark);
            record["update_error_count"] = updateErrors;
            record["after_native_snapshot"] = NativeSnapshot(part, sketch);
            record["active_sketch_after"] = ActiveSketchId(session);
            int degreesOfFreedom;
            Sketch.Status status = sketch.GetStatus(out degreesOfFreedom);
            record["solver_status"] = status.ToString();
            record["degrees_of_freedom"] = degreesOfFreedom;
            record["dimension_journal_id"] = dimension.JournalIdentifier;
            record["dimension_state"] = dimension.DimensionState.ToString();
            Expression expression = dimension.AssociatedExpression;
            record["rhs"] = expression.RightHandSide;
            record["value_mm"] = expression.Value;
            record["unit"] = expression.Units == null ? null : expression.Units.Symbol;
            Sketch.DimensionGeometry[] operands = dimension.GetDimensionGeometry();
            List<object> operandRecords = new List<object>();
            record["operands"] = operandRecords;
            bool hasStart = false;
            bool hasEnd = false;
            if (operands != null) foreach (Sketch.DimensionGeometry operand in operands)
            {
                operandRecords.Add(new Dictionary<string, object> {
                    { "journal_id", operand.Geometry == null ? null : operand.Geometry.JournalIdentifier },
                    { "tag", operand.Geometry == null ? null : operand.Geometry.Tag.ToString() },
                    { "association", operand.AssocType.ToString() }, { "association_value", operand.AssocValue }
                });
                bool sameLine = operand.Geometry != null && operand.Geometry.Tag == line.Tag;
                hasStart |= sameLine && operand.AssocType == Sketch.AssocType.StartPoint;
                hasEnd |= sameLine && operand.AssocType == Sketch.AssocType.EndPoint;
            }
            Require(updateErrors == 0, "Native Driving fixture update reported errors.");
            Require((status == Sketch.Status.UnderConstrained || status == Sketch.Status.WellConstrained) && degreesOfFreedom >= 0,
                "Native Driving fixture is not in a normal solver state.");
            Require(dimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving,
                "Native fixture dimension is not Driving.");
            Require(String.Equals(expression.RightHandSide, "10", StringComparison.Ordinal) &&
                Math.Abs(expression.Value - 10.0) <= LengthToleranceMm && expression.Units != null &&
                String.Equals(expression.Units.Symbol, "mm", StringComparison.OrdinalIgnoreCase),
                "Native fixture dimension does not read back as 10 mm.");
            Require(operands != null && operands.Length == 2 && hasStart && hasEnd,
                "Native fixture dimension is not associated with both endpoints of drive-1.");
            Require(CountDrivingConstraints(NativeConstraintRecords(sketch)) == 1,
                "Native fixture must contain exactly one Driving dimension.");
            RefreshFixtureGeometryIds(adapter, binding, part, fixture, record, "after_native_driving_dimension");
            record["revision_after_refresh"] = binding.RevisionId;
            record["after_refresh_snapshot"] = NativeSnapshot(part, sketch);
            record["status"] = "READY";
        }
        catch (Exception ex)
        {
            record["status"] = "FAILED";
            record["exception"] = ex.ToString();
            throw;
        }
        finally
        {
            try { if (activated) sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model); }
            finally { session.DeleteUndoMark(mark, "F07 native Driving fixture"); }
        }
    }

    private static Dictionary<string, object> CaptureInspectPartWarmup(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part, Dictionary<string, object> parentEvidence, string evidenceKey)
    {
        Dictionary<string, object> warmup = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", "RUNNING" }, { "revision_before", binding.RevisionId }
        };
        parentEvidence[evidenceKey] = warmup;

        Dictionary<string, object> before = null;
        Exception beforeException = null;
        try { before = NativeSnapshot(part, null); }
        catch (Exception ex) { beforeException = ex; }
        warmup["before_snapshot"] = before;
        warmup["before_snapshot_exception"] = beforeException == null ? null : beforeException.ToString();

        Dictionary<string, object> inspection = null;
        Exception inspectException = null;
        try { inspection = adapter.InspectPart(binding); }
        catch (Exception ex) { inspectException = ex; }
        warmup["inspect_part"] = inspection;
        warmup["inspect_part_exception"] = inspectException == null ? null : inspectException.ToString();

        Dictionary<string, object> after = null;
        Exception afterException = null;
        try { after = NativeSnapshot(part, null); }
        catch (Exception ex) { afterException = ex; }
        warmup["after_snapshot"] = after;
        warmup["after_snapshot_exception"] = afterException == null ? null : afterException.ToString();
        warmup["revision_after"] = binding.RevisionId;
        Dictionary<string, object> readEffects = FindNestedDictionary(inspection, "read_effects");
        warmup["read_effects"] = readEffects;
        List<object> diff = SnapshotDiff(before, after);
        warmup["snapshot_diff"] = diff;
        bool allowed = beforeException == null && afterException == null && inspectException == null &&
            WarmupReadEffectAllowed(before, after, readEffects, diff);
        warmup["read_effect_validated"] = allowed;
        warmup["status"] = "CAPTURED";

        Require(beforeException == null && afterException == null && inspectException == null,
            "F07 materialization read could not capture its before/after snapshots: " + M2Json.CanonicalText(warmup));
        Require(allowed,
            "F07 materialization read produced a change outside the declared InspectPart dirty-read effect: " +
            M2Json.CanonicalText(warmup));
        warmup["status"] = "VALIDATED";
        return warmup;
    }

    private static void RefreshFixtureGeometryIds(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, SketchFixture fixture, Dictionary<string, object> parentEvidence, string phase)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "phase", phase }, { "fixture_name", fixture.Name }, { "sketch_id", fixture.SketchId },
            { "revision_before_inspect", binding.RevisionId },
            { "journal_ids_before", StringObjectMapRecord(fixture.GeometryIdsByRequestId) },
            { "status", "RUNNING" }
        };
        GetOrCreateEvidenceList(parentEvidence, "fixture_identity_refreshes").Add(record);
        try
        {
            Dictionary<string, object> inspection = adapter.InspectSketch(binding, fixture.SketchId);
            record["inspection"] = inspection;
            record["revision_after_inspect"] = binding.RevisionId;
            Dictionary<string, object> nativeOwnerReadback = NativeSnapshot(part, FindSketch(part, fixture.SketchId));
            record["native_owner_readback"] = nativeOwnerReadback;
            Dictionary<string, string> refreshedMap = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> refreshedIds = new List<string>();
            List<object> resolutions = new List<object>();
            record["geometry_id_resolutions"] = resolutions;
            foreach (M3SketchGeometryRequest geometry in fixture.GeometryRequests)
            {
                string currentId = FindInspectedGeometryId(inspection, geometry, fixture.Frame);
                Dictionary<string, object> resolution = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "request_geometry_id", geometry.Id }, { "current_journal_id", currentId },
                    { "geometry_type", geometry.Type }
                };
                resolutions.Add(resolution);
                Require(!String.IsNullOrWhiteSpace(currentId),
                    "InspectSketch did not resolve a current JournalIdentifier for fixture geometry " + geometry.Id + ": " +
                    M2Json.CanonicalText(record));
                refreshedMap.Add(geometry.Id, currentId);
                refreshedIds.Add(currentId);
            }

            fixture.GeometryIds = refreshedIds;
            fixture.GeometryIdsByRequestId = refreshedMap;
            Dictionary<string, object> refreshedRecord = StringObjectMapRecord(refreshedMap);
            record["journal_ids_after"] = refreshedRecord;
            record["status"] = "REFRESHED";
            if (fixture.SeedEvidence != null)
            {
                fixture.SeedEvidence["geometry_ids"] = refreshedRecord;
                GetOrCreateEvidenceList(fixture.SeedEvidence, "post_additional_seed_inspections").Add(record);
            }
        }
        catch (Exception ex)
        {
            record["status"] = "FAILED";
            record["exception"] = ex.ToString();
            throw;
        }
    }

    private static ForeignCurvePair FindForeignCurvePairWithoutLocalIdCollisions(
        SketchFixture local, SketchFixture foreign, Part part, Dictionary<string, object> evidence)
    {
        HashSet<string> localIds = new HashSet<string>(local.GeometryIdsByRequestId.Values, StringComparer.Ordinal);
        List<object> candidatePairs = new List<object>();
        ForeignCurvePair selected = null;
        for (int i = 0; i < foreign.GeometryRequests.Count; i++)
        {
            M3SketchGeometryRequest first = foreign.GeometryRequests[i];
            if (!String.Equals(first.Type, "line", StringComparison.Ordinal) || first.StartMm == null || first.EndMm == null) continue;
            for (int j = i + 1; j < foreign.GeometryRequests.Count; j++)
            {
                M3SketchGeometryRequest second = foreign.GeometryRequests[j];
                if (!String.Equals(second.Type, "line", StringComparison.Ordinal) || second.StartMm == null || second.EndMm == null) continue;
                string end1;
                string end2;
                if (!SharedEndpointRoles(first, second, out end1, out end2)) continue;
                string id1;
                string id2;
                if (!foreign.GeometryIdsByRequestId.TryGetValue(first.Id, out id1) ||
                    !foreign.GeometryIdsByRequestId.TryGetValue(second.Id, out id2)) continue;
                bool absentFromLocal = !localIds.Contains(id1) && !localIds.Contains(id2) &&
                    !String.Equals(id1, id2, StringComparison.Ordinal);
                Dictionary<string, object> candidate = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "curve1_request_id", first.Id }, { "curve1_end", end1 }, { "curve1_journal_id", id1 },
                    { "curve2_request_id", second.Id }, { "curve2_end", end2 }, { "curve2_journal_id", id2 },
                    { "both_ids_present_in_foreign_sketch", true }, { "both_ids_absent_from_local_id_set", absentFromLocal }
                };
                candidatePairs.Add(candidate);
                if (selected == null && absentFromLocal)
                {
                    selected = new ForeignCurvePair {
                        Curve1RequestId = first.Id, Curve1End = end1,
                        Curve2RequestId = second.Id, Curve2End = end2,
                        Curve1JournalId = id1, Curve2JournalId = id2
                    };
                }
            }
        }

        Dictionary<string, object> localNative = NativeSnapshot(part, FindSketch(part, local.SketchId));
        Dictionary<string, object> foreignNative = NativeSnapshot(part, FindSketch(part, foreign.SketchId));
        Dictionary<string, object> scopeEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "local_sketch_id", local.SketchId }, { "local_current_journal_ids", StringObjectMapRecord(local.GeometryIdsByRequestId) },
            { "local_native_owner_and_tag_readback", localNative },
            { "foreign_sketch_id", foreign.SketchId }, { "foreign_current_journal_ids", StringObjectMapRecord(foreign.GeometryIdsByRequestId) },
            { "foreign_native_owner_and_tag_readback", foreignNative }, { "candidate_connected_foreign_pairs", candidatePairs }
        };
        if (selected != null)
        {
            scopeEvidence["selected_pair"] = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "curve1_request_id", selected.Curve1RequestId }, { "curve1_end", selected.Curve1End },
                { "curve1_journal_id", selected.Curve1JournalId },
                { "curve2_request_id", selected.Curve2RequestId }, { "curve2_end", selected.Curve2End },
                { "curve2_journal_id", selected.Curve2JournalId },
                { "selected_ids_absent_from_local_set", true }, { "selected_ids_resolved_in_foreign", true }
            };
        }
        evidence["cross_sketch_identity_scope"] = scopeEvidence;
        Require(selected != null,
            "No connected foreign Line pair has current JournalIdentifiers absent from the local sketch ID set: " +
            M2Json.CanonicalText(scopeEvidence));
        return selected;
    }

    private static bool SharedEndpointRoles(M3SketchGeometryRequest first,
        M3SketchGeometryRequest second, out string firstEnd, out string secondEnd)
    {
        firstEnd = null;
        secondEnd = null;
        if (LocalPointNear(first.EndMm, second.StartMm)) { firstEnd = "end"; secondEnd = "start"; return true; }
        if (LocalPointNear(first.EndMm, second.EndMm)) { firstEnd = "end"; secondEnd = "end"; return true; }
        if (LocalPointNear(first.StartMm, second.StartMm)) { firstEnd = "start"; secondEnd = "start"; return true; }
        if (LocalPointNear(first.StartMm, second.EndMm)) { firstEnd = "start"; secondEnd = "end"; return true; }
        return false;
    }

    private static bool LocalPointNear(double[] first, double[] second)
    {
        return first != null && second != null && first.Length >= 2 && second.Length >= 2 &&
            VectorDistance(first, second, 2) <= PositionToleranceMm;
    }

    private static List<object> GetOrCreateSeedEvidenceList(Dictionary<string, object> evidence)
    {
        return GetOrCreateEvidenceList(evidence, "seed_sketches");
    }

    private static List<object> GetOrCreateEvidenceList(Dictionary<string, object> evidence, string key)
    {
        Require(evidence != null, "F07 case evidence dictionary is missing.");
        object existing;
        List<object> records = evidence.TryGetValue(key, out existing)
            ? existing as List<object>
            : null;
        if (records == null)
        {
            records = new List<object>();
            evidence[key] = records;
        }
        return records;
    }

    private static Dictionary<string, object> FrameRecord(Frame frame)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "origin_mm", NumberList(frame.Origin) }, { "normal", NumberList(frame.Normal) },
            { "x_axis", NumberList(frame.XAxis) }, { "y_axis", NumberList(frame.YAxis) }
        };
    }

    private static Dictionary<string, object> SketchRequestRecord(M3SketchRequest request)
    {
        List<object> geometries = new List<object>();
        if (request.Geometries != null)
        {
            foreach (M3SketchGeometryRequest geometry in request.Geometries)
            {
                geometries.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "id", geometry.Id }, { "type", geometry.Type },
                    { "start_mm", NumberList(geometry.StartMm) },
                    { "end_mm", NumberList(geometry.EndMm) },
                    { "corner_mm", NumberList(geometry.CornerMm) },
                    { "center_mm", NumberList(geometry.CenterMm) },
                    { "width_mm", JsonNumberValue(geometry.WidthMm) }, { "height_mm", JsonNumberValue(geometry.HeightMm) },
                    { "diameter_mm", JsonNumberValue(geometry.DiameterMm) }, { "radius_mm", JsonNumberValue(geometry.RadiusMm) },
                    { "major_radius_mm", JsonNumberValue(geometry.MajorRadiusMm) }, { "minor_radius_mm", JsonNumberValue(geometry.MinorRadiusMm) },
                    { "rotation_deg", JsonNumberValue(geometry.RotationDeg) }, { "start_angle_deg", JsonNumberValue(geometry.StartAngleDeg) },
                    { "sweep_deg", JsonNumberValue(geometry.SweepDeg) }, { "interpolation_points_mm", NestedNumberList(geometry.InterpolationPointsMm) },
                    { "degree", geometry.Degree }, { "endpoint_condition", geometry.EndpointCondition }
                });
            }
        }
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "revision_id", request.RevisionId }, { "plane", request.Plane },
            { "origin_mm", NumberList(request.OriginMm) },
            { "normal", NumberList(request.Normal) },
            { "x_axis", NumberList(request.XAxis) },
            { "support_face_selector", request.SupportFaceSelector }, { "support_face_id", request.SupportFaceId },
            { "support_datum_plane_id", request.SupportDatumPlaneId }, { "support_body_id", request.SupportBodyId },
            { "face_origin_offset_mm", NumberList(request.FaceOriginOffsetMm) },
            { "constraint_mode", request.ConstraintMode }, { "geometries", geometries }
        };
    }

    private static List<object> NumberList(double[] values)
    {
        if (values == null) return null;
        List<object> result = new List<object>();
        foreach (double value in values) result.Add(JsonNumberValue(value));
        return result;
    }

    private static List<object> NestedNumberList(double[][] values)
    {
        if (values == null) return null;
        List<object> result = new List<object>();
        foreach (double[] value in values) result.Add(NumberList(value));
        return result;
    }

    private static Dictionary<string, object> StringObjectMapRecord(Dictionary<string, string> values)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (values != null)
            foreach (KeyValuePair<string, string> entry in values) result.Add(entry.Key, entry.Value);
        return result;
    }

    private static object JsonNumberValue(double value)
    {
        if (!Double.IsNaN(value) && !Double.IsInfinity(value)) return value;
        string marker = Double.IsNaN(value) ? "NaN" : (value > 0.0 ? "PositiveInfinity" : "NegativeInfinity");
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "type", "double" }, { "non_finite", marker }
        };
    }

    private static CornerRun ExecuteCorner(M2OpenModelAdapter adapter, M2ModelBinding binding,
        M3SketchCornerRequest request, Dictionary<string, object> evidence, string label)
    {
        evidence["request"] = CornerRequestRecord(request);
        M3PlanSnapshot plan = null;
        try { plan = adapter.PrepareSketchCorner(binding, request); }
        catch (Exception ex) { evidence["prepare_exception"] = ex.ToString(); throw; }
        evidence["plan"] = plan == null ? null : plan.PublicView;
        Require(plan != null && String.Equals(plan.Kind, "edit_sketch_corner", StringComparison.Ordinal),
            "PrepareSketchCorner returned an unexpected plan: " + M2Json.CanonicalText(plan == null ? null : plan.PublicView));
        string revisionBefore = binding.RevisionId;
        evidence["prepare_revision"] = revisionBefore;
        M3ExecutionResult execution = null;
        try { execution = adapter.ExecutePlan(binding, plan); }
        catch (Exception ex)
        {
            evidence["execute_exception"] = ex.ToString();
            evidence["execute"] = null;
            evidence["execute_result"] = null;
            evidence["execute_revision"] = binding.RevisionId;
            throw;
        }
        evidence["execute"] = ExecutionRecord(execution);
        evidence["execute_result"] = execution == null ? null : execution.Result;
        evidence["execute_revision"] = binding.RevisionId;
        Require(execution != null && String.Equals(execution.State, "APPLIED", StringComparison.Ordinal),
            label + " did not return APPLIED: " + M2Json.CanonicalText(ExecutionRecord(execution)));
        Require(NativeCallWasRecorded(execution.Result), label + " did not record a successful native NX operation call: " +
            M2Json.CanonicalText(ExecutionRecord(execution)));
        Require(!String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal),
            label + " did not advance the binding revision after geometry edit.");
        M3VerificationResult verification = null;
        try { verification = adapter.VerifyPlan(binding, plan); }
        catch (Exception ex) { evidence["verification_exception"] = ex.ToString(); throw; }
        evidence["verification"] = VerificationRecord(verification);
        evidence["verify_result"] = verification == null ? null : verification.Result;
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            label + " did not pass independent production plan verification: " + M2Json.CanonicalText(VerificationRecord(verification)));
        if (String.Equals(request.Kind, "fillet", StringComparison.Ordinal))
        {
            Require(String.Equals(StringValue(execution.Result, "requested_definition"), "fillet", StringComparison.Ordinal),
                label + " result does not preserve the fillet definition.");
            Require(String.Equals(StringValue(execution.Result, "native_mode"), "Sketch.Fillet", StringComparison.Ordinal),
                label + " result does not identify the native Sketch.Fillet API.");
        }
        return new CornerRun { Plan = plan, Execution = execution, Verification = verification };
    }

    private static CornerRun ExecutePreparedCorner(M2OpenModelAdapter adapter, M2ModelBinding binding,
        M3PlanSnapshot plan, M3SketchCornerRequest request, Dictionary<string, object> evidence, string label)
    {
        evidence["request"] = CornerRequestRecord(request);
        evidence["plan"] = plan == null ? null : plan.PublicView;
        string before = binding.RevisionId;
        evidence["prepare_revision"] = before;
        M3ExecutionResult execution = null;
        try { execution = adapter.ExecutePlan(binding, plan); }
        catch (Exception ex)
        {
            evidence["execute_exception"] = ex.ToString();
            evidence["execute"] = null;
            evidence["execute_result"] = null;
            evidence["execute_revision"] = binding.RevisionId;
            throw;
        }
        evidence["execute"] = ExecutionRecord(execution);
        evidence["execute_result"] = execution == null ? null : execution.Result;
        evidence["execute_revision"] = binding.RevisionId;
        Require(execution != null && String.Equals(execution.State, "APPLIED", StringComparison.Ordinal),
            label + " did not return APPLIED: " + M2Json.CanonicalText(ExecutionRecord(execution)));
        Require(!String.Equals(before, binding.RevisionId, StringComparison.Ordinal), label + " did not advance revision.");
        M3VerificationResult verification = null;
        try { verification = adapter.VerifyPlan(binding, plan); }
        catch (Exception ex) { evidence["verification_exception"] = ex.ToString(); throw; }
        evidence["verification"] = VerificationRecord(verification);
        evidence["verify_result"] = verification == null ? null : verification.Result;
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            label + " did not verify: " + M2Json.CanonicalText(VerificationRecord(verification)));
        return new CornerRun { Plan = plan, Execution = execution, Verification = verification };
    }

    private static Dictionary<string, object> CornerRequestRecord(M3SketchCornerRequest request)
    {
        if (request == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "revision_id", request.RevisionId }, { "sketch_id", request.SketchId },
            { "kind", request.Kind }, { "curve1_id", request.Curve1Id }, { "curve1_end", request.Curve1End },
            { "curve2_id", request.Curve2Id }, { "curve2_end", request.Curve2End },
            { "radius_mm", String.Equals(request.Kind, "fillet", StringComparison.Ordinal) ? JsonNumberValue(request.RadiusMm) : null },
            { "distance1_mm", String.Equals(request.Kind, "chamfer", StringComparison.Ordinal) ? JsonNumberValue(request.Distance1Mm) : null },
            { "distance2_mm", request.Distance2Mm.HasValue ? JsonNumberValue(request.Distance2Mm.Value) : null },
            { "angle_deg", request.AngleDeg.HasValue ? JsonNumberValue(request.AngleDeg.Value) : null }
        };
    }

    private static M3ExecutionResult ExecuteExpectStale(M2OpenModelAdapter adapter, M2ModelBinding binding,
        M3PlanSnapshot plan, out Exception error)
    {
        error = null;
        try { return adapter.ExecutePlan(binding, plan); }
        catch (Exception ex) { error = ex; return null; }
    }

    private static bool IsStaleRejection(M3ExecutionResult result, Exception error)
    {
        M2ModelException modelError = error as M2ModelException;
        if (modelError != null && (String.Equals(modelError.Code, "STALE_REVISION", StringComparison.Ordinal) ||
            String.Equals(modelError.Code, "STALE_PLAN", StringComparison.Ordinal))) return true;
        return result != null && String.Equals(result.State, "UNCHANGED", StringComparison.Ordinal) &&
            (String.Equals(result.ErrorCode, "STALE_REVISION", StringComparison.Ordinal) ||
             String.Equals(result.ErrorCode, "STALE_PLAN", StringComparison.Ordinal));
    }

    private static bool IsStalePlanRejection(M3ExecutionResult result, Exception error)
    {
        M2ModelException modelError = error as M2ModelException;
        if (modelError != null && String.Equals(modelError.Code, "STALE_PLAN", StringComparison.Ordinal)) return true;
        return result != null && String.Equals(result.State, "UNCHANGED", StringComparison.Ordinal) &&
            String.Equals(result.ErrorCode, "STALE_PLAN", StringComparison.Ordinal);
    }

    private static void RunChamferCase(M2OpenModelAdapter adapter, M2ModelBinding binding, Part part,
        Frame frame, string name, double distance1, double? distance2, double? angleDeg,
        double[] expected1, double[] expected2, double expectedLength, double? expectedAngleRad,
        Dictionary<string, object> parentEvidence, List<SketchFixture> fixtures)
    {
        string prefix = "chamfer-" + name;
        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal);
        object existingSubcases;
        List<object> subcases = parentEvidence.TryGetValue("subcases", out existingSubcases)
            ? existingSubcases as List<object>
            : null;
        if (subcases == null) { subcases = new List<object>(); parentEvidence["subcases"] = subcases; }
        subcases.Add(row);
        row["name"] = name;
        SketchFixture fixture = CreateSketch(adapter, binding, part, prefix, frame,
            GeometryOnly(new List<M3SketchGeometryRequest> {
                Line("line1", 0, 0, 20, 0), Line("line2", 20, 0, 20, 10), Line("unselected", -10, 5, -6, 5)
            }), row);
        Dictionary<string, object> before = NativeSnapshot(part, FindSketch(part, fixture.SketchId));
        row["native_before"] = before;
        M3SketchCornerRequest request = CornerRequest(binding, fixture, "chamfer", "line1", "end", "line2", "start",
            null, distance1, distance2, angleDeg);
        CornerRun run = ExecuteCorner(adapter, binding, request, row, prefix);
        VerifyChamferOperationEvidence(run.Execution, distance1, distance2, angleDeg,
            distance2 ?? distance1 * Math.Sin(expectedAngleRad.Value) / Math.Sin(Pi / 2.0 + expectedAngleRad.Value));
        Sketch native = FindSketch(part, fixture.SketchId);
        Dictionary<string, object> after = NativeSnapshot(part, native);
        row["native_after"] = after;
        VerifyChamfer(native, frame, new double[] { 20, 0 }, new double[] { 0, 0 },
            new double[] { 20, 10 }, expected1, expected2, distance1,
            distance2 ?? distance1 * Math.Sin(expectedAngleRad.Value) / Math.Sin(Pi / 2.0 + expectedAngleRad.Value),
            expectedLength, expectedAngleRad, row);
        VerifyUnselectedLineUnchanged(before, after, new double[] { -10, 5, 0 },
            new double[] { -6, 5, 0 }, row);
        Require(CountNewDrivingConstraints(SnapshotList(before, "constraints"), SnapshotList(after, "constraints")) == 0,
            "Chamfer unexpectedly added a Driving dimension constraint.");
        row["plan"] = run.Plan.PublicView;
        row["execute"] = ExecutionRecord(run.Execution);
        row["verification"] = VerificationRecord(run.Verification);
        fixture.NativeAfter = after;
        fixtures.Add(fixture);
    }

    private static M3SketchCornerRequest CornerRequest(M2ModelBinding binding, SketchFixture fixture,
        string kind, string curve1, string end1, string curve2, string end2,
        double? radius, double? distance1, double? distance2, double? angleDeg, string sketchOverride = null)
    {
        string selected1;
        string selected2;
        Require(fixture.GeometryIdsByRequestId.TryGetValue(curve1, out selected1), "Selected curve1 ID did not come from nx_inspect_sketch: " + curve1);
        Require(fixture.GeometryIdsByRequestId.TryGetValue(curve2, out selected2), "Selected curve2 ID did not come from nx_inspect_sketch: " + curve2);
        M3SketchCornerRequest request = new M3SketchCornerRequest {
            RevisionId = binding.RevisionId, SketchId = sketchOverride ?? fixture.SketchId,
            Kind = kind, Curve1Id = selected1, Curve1End = end1, Curve2Id = selected2, Curve2End = end2
        };
        if (radius.HasValue) request.RadiusMm = radius.Value;
        if (distance1.HasValue) request.Distance1Mm = distance1.Value;
        if (distance2.HasValue) request.Distance2Mm = distance2.Value;
        if (angleDeg.HasValue) request.AngleDeg = angleDeg.Value;
        return request;
    }

    private static object CheckPrepareRejected(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, Session session, M3SketchCornerRequest request, string label, string expectedCode,
        Dictionary<string, object> parentEvidence)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "label", label }, { "expected_error_code", expectedCode },
            { "request", CornerRequestRecord(request) }, { "status", "RUNNING" }
        };
        try
        {
            record["request_before_warmup"] = record["request"];
            CaptureInspectPartWarmup(adapter, binding, part, record, "warmup");
            request.RevisionId = binding.RevisionId;
            record["request"] = CornerRequestRecord(request);
            string revisionBefore = binding.RevisionId;
            string activeBefore = ActiveSketchId(session);
            record["revision_before_prepare"] = revisionBefore;
            record["active_sketch_before_prepare"] = activeBefore;
            Dictionary<string, object> before = NativeSnapshot(part, null);
            record["before_prepare_snapshot"] = before;

            Exception observed = null;
            M3PlanSnapshot plan = null;
            try { plan = adapter.PrepareSketchCorner(binding, request); }
            catch (Exception ex) { observed = ex; }
            record["prepare_exception"] = observed == null ? null : observed.ToString();
            record["plan"] = plan == null ? null : plan.PublicView;

            Dictionary<string, object> after = null;
            Exception afterSnapshotException = null;
            try { after = NativeSnapshot(part, null); }
            catch (Exception ex) { afterSnapshotException = ex; }
            record["after_prepare_snapshot"] = after;
            record["after_prepare_snapshot_exception"] = afterSnapshotException == null ? null : afterSnapshotException.ToString();
            record["revision_after_prepare"] = binding.RevisionId;
            string activeAfter = null;
            Exception activeSnapshotException = null;
            try { activeAfter = ActiveSketchId(session); }
            catch (Exception ex) { activeSnapshotException = ex; }
            record["active_sketch_after_prepare"] = activeAfter;
            record["active_sketch_snapshot_exception"] = activeSnapshotException == null ? null : activeSnapshotException.ToString();

            bool nativeStateUnchanged = before != null && after != null && SameSnapshot(before, after);
            bool revisionUnchanged = String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal);
            bool activeSketchUnchanged = String.Equals(activeBefore, activeAfter, StringComparison.Ordinal);
            string observedCode = (observed as M2ModelException) == null ? null : ((M2ModelException)observed).Code;
            record["observed_error_code"] = observedCode;
            record["native_state_unchanged"] = nativeStateUnchanged;
            record["prepare_snapshot_diff"] = SnapshotDiff(before, after);
            record["revision_unchanged"] = revisionUnchanged;
            record["active_sketch_unchanged"] = activeSketchUnchanged;
            record["status"] = "CAPTURED";

            Require(observed != null && plan == null,
                label + " was not rejected during prepare: " + M2Json.CanonicalText(record));
            Require(observed is M2ModelException,
                label + " did not return a classified adapter model error: " + M2Json.CanonicalText(record));
            Require(!String.IsNullOrWhiteSpace(observedCode),
                label + " returned an empty adapter error code: " + M2Json.CanonicalText(record));
            if (expectedCode != null)
                Require(String.Equals(observedCode, expectedCode, StringComparison.Ordinal),
                    label + " returned an unexpected error code: " + M2Json.CanonicalText(record));
            Require(nativeStateUnchanged,
                label + " changed native model state during prepare; diff=" + M2Json.CanonicalText(record["prepare_snapshot_diff"]));
            Require(revisionUnchanged,
                label + " changed the binding revision during prepare: " + M2Json.CanonicalText(record));
            Require(activeSketchUnchanged,
                label + " changed the active sketch during prepare: " + M2Json.CanonicalText(record));
            record["status"] = "PASSED";
            return record;
        }
        catch (Exception ex)
        {
            record["status"] = "FAILED";
            record["harness_exception"] = ex.ToString();
            if (parentEvidence != null) GetOrCreateEvidenceList(parentEvidence, "prepare_rejection_diagnostics").Add(record);
            throw;
        }
    }

    private static Dictionary<string, object> CheckOversizeFilletOutcome(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part, Session session, M3SketchCornerRequest request,
        string label, Dictionary<string, object> parentEvidence)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "label", label }, { "request", CornerRequestRecord(request) }, { "status", "PREPARING" },
            { "units", "mm" }, { "acceptance", "preflight refusal or one native ROLLED_BACK; APPLIED/UNKNOWN fail" }
        };
        parentEvidence["oversize_fillet_attempt"] = record;
        record["request_before_warmup"] = record["request"];
        CaptureInspectPartWarmup(adapter, binding, part, record, "warmup");
        request.RevisionId = binding.RevisionId;
        record["request"] = CornerRequestRecord(request);
        string revisionBeforePrepare = binding.RevisionId;
        string activeBeforePrepare = ActiveSketchId(session);
        record["revision_before_prepare"] = revisionBeforePrepare;
        record["active_sketch_before_prepare"] = activeBeforePrepare;
        Dictionary<string, object> before = NativeSnapshot(part, null);
        record["before_prepare_snapshot"] = before;

        M3PlanSnapshot plan = null;
        Exception prepareException = null;
        try { plan = adapter.PrepareSketchCorner(binding, request); }
        catch (Exception ex) { prepareException = ex; }
        record["prepare_exception"] = prepareException == null ? null : prepareException.ToString();
        M2ModelException prepareModelException = prepareException as M2ModelException;
        record["prepare_error_code"] = prepareModelException == null ? null : prepareModelException.Code;
        record["plan"] = plan == null ? null : plan.PublicView;
        record["revision_after_prepare"] = binding.RevisionId;

        Dictionary<string, object> afterPrepare = NativeSnapshot(part, null);
        record["after_prepare_snapshot"] = afterPrepare;
        record["prepare_snapshot_diff"] = SnapshotDiff(before, afterPrepare);
        bool prepareSnapshotUnchanged = SameSnapshot(before, afterPrepare);
        bool prepareRevisionUnchanged = String.Equals(revisionBeforePrepare, binding.RevisionId, StringComparison.Ordinal);
        string activeAfterPrepare = ActiveSketchId(session);
        record["active_sketch_after_prepare"] = activeAfterPrepare;
        bool prepareActiveSketchUnchanged = String.Equals(activeBeforePrepare, activeAfterPrepare, StringComparison.Ordinal);
        record["prepare_snapshot_unchanged"] = prepareSnapshotUnchanged;
        record["prepare_revision_unchanged"] = prepareRevisionUnchanged;
        record["prepare_active_sketch_unchanged"] = prepareActiveSketchUnchanged;

        if (prepareException != null || plan == null)
        {
            record["status"] = "PREFLIGHT_CAPTURED";
            Require(prepareException is M2ModelException && plan == null,
                label + " did not return a classified read-only preflight refusal: " + M2Json.CanonicalText(record));
            Require(prepareSnapshotUnchanged && prepareRevisionUnchanged && prepareActiveSketchUnchanged,
                label + " changed state during its preflight refusal: " + M2Json.CanonicalText(record));
            record["status"] = "PREFLIGHT_REJECTED";
            return record;
        }

        record["status"] = "PREPARED_FOR_NATIVE_ROLLBACK_CHECK";
        Require(String.Equals(plan.Kind, "edit_sketch_corner", StringComparison.Ordinal),
            label + " returned an unexpected plan kind: " + M2Json.CanonicalText(record));
        Require(prepareSnapshotUnchanged && prepareRevisionUnchanged && prepareActiveSketchUnchanged,
            label + " PrepareSketchCorner changed native state or revision: " + M2Json.CanonicalText(record));

        string revisionBeforeExecute = binding.RevisionId;
        record["revision_before_execute"] = revisionBeforeExecute;
        M3ExecutionResult execution = null;
        Exception executeException = null;
        try { execution = adapter.ExecutePlan(binding, plan); }
        catch (Exception ex) { executeException = ex; }
        record["execute_exception"] = executeException == null ? null : executeException.ToString();
        record["execution"] = ExecutionRecord(execution);
        record["execution_result"] = execution == null ? null : execution.Result;
        record["revision_after_execute"] = binding.RevisionId;
        Dictionary<string, object> afterExecute = NativeSnapshot(part, null);
        record["after_execute_snapshot"] = afterExecute;
        record["execute_snapshot_diff_from_baseline"] = SnapshotDiff(before, afterExecute);
        string activeAfterExecute = ActiveSketchId(session);
        record["active_sketch_after_execute"] = activeAfterExecute;
        record["execute_snapshot_restored"] = SameSnapshot(before, afterExecute);
        record["execute_active_sketch_restored"] = String.Equals(activeBeforePrepare, activeAfterExecute, StringComparison.Ordinal);
        record["revision_did_not_decrease"] = RevisionNotLower(revisionBeforePrepare, binding.RevisionId);

        Require(executeException == null && execution != null,
            label + " execution returned no structured rollback result: " + M2Json.CanonicalText(record));
        Require(String.Equals(execution.State, "ROLLED_BACK", StringComparison.Ordinal),
            label + " must refuse in prepare or return ROLLED_BACK; APPLIED/UNCHANGED/UNKNOWN are not accepted: " +
            M2Json.CanonicalText(record));
        Require(NativeCallWasRecorded(execution.Result),
            label + " rollback evidence does not prove one native NX call: " + M2Json.CanonicalText(record));
        Dictionary<string, object> rollback = FindNestedDictionary(execution.Result, "rollback");
        record["native_rollback_evidence"] = rollback;
        Require(rollback != null && Object.Equals(rollback.ContainsKey("undo_to_mark_succeeded")
                ? rollback["undo_to_mark_succeeded"] : null, true) &&
            Object.Equals(rollback.ContainsKey("geometry_restored") ? rollback["geometry_restored"] : null, true) &&
            Object.Equals(rollback.ContainsKey("constraints_restored") ? rollback["constraints_restored"] : null, true) &&
            Object.Equals(rollback.ContainsKey("semantic_state_restored") ? rollback["semantic_state_restored"] : null, true) &&
            Object.Equals(rollback.ContainsKey("bodies_restored") ? rollback["bodies_restored"] : null, true) &&
            Object.Equals(rollback.ContainsKey("history_restored") ? rollback["history_restored"] : null, true) &&
            Object.Equals(rollback.ContainsKey("dirty_state_restored") ? rollback["dirty_state_restored"] : null, true) &&
            Object.Equals(rollback.ContainsKey("features_restored") ? rollback["features_restored"] : null, true) &&
            Object.Equals(rollback.ContainsKey("active_sketch_restored") ? rollback["active_sketch_restored"] : null, true),
            label + " native Undo evidence does not prove complete rollback: " + M2Json.CanonicalText(record));
        Dictionary<string, object> nativeOperation = FindNestedDictionary(execution.Result, "native_operation");
        object updateErrorCount;
        bool updateErrorsAbsentOrZero = nativeOperation == null ||
            !nativeOperation.TryGetValue("update_error_count", out updateErrorCount) || updateErrorCount == null ||
            Convert.ToInt32(updateErrorCount, CultureInfo.InvariantCulture) == 0;
        record["update_errors_absent_or_zero"] = updateErrorsAbsentOrZero;
        Require(record["execute_snapshot_restored"] is bool && (bool)record["execute_snapshot_restored"] &&
            record["execute_active_sketch_restored"] is bool && (bool)record["execute_active_sketch_restored"] &&
            record["revision_did_not_decrease"] is bool && (bool)record["revision_did_not_decrease"] && updateErrorsAbsentOrZero,
            label + " rollback did not restore exact native state or reported an update/revision inconsistency: " +
            M2Json.CanonicalText(record));
        record["status"] = "NATIVE_ROLLBACK_VERIFIED";
        return record;
    }

    private static void VerifyLineLineFillet(Sketch sketch, Frame frame, double[] corner,
        double[] far1, double[] far2, double radius, double[] tangent1Local, double[] tangent2Local,
        double[] centerLocal, double expectedArcLength, int expectedCurveCount, Dictionary<string, object> evidence)
    {
        Point3d tangent1 = WorldPoint(frame, tangent1Local[0], tangent1Local[1]);
        Point3d tangent2 = WorldPoint(frame, tangent2Local[0], tangent2Local[1]);
        Point3d cornerWorld = WorldPoint(frame, corner[0], corner[1]);
        Point3d far1World = WorldPoint(frame, far1[0], far1[1]);
        Point3d far2World = WorldPoint(frame, far2[0], far2[1]);
        Point3d center = WorldPoint(frame, centerLocal[0], centerLocal[1]);
        Line first = FindLineWithEndpoint(sketch, tangent1, far1World);
        Line second = FindLineWithEndpoint(sketch, tangent2, far2World);
        Require(LineHasEndpoints(first, tangent1, far1World), "Fillet changed the tangent or retained far endpoint of curve1.");
        Require(LineHasEndpoints(second, tangent2, far2World), "Fillet changed the tangent or retained far endpoint of curve2.");
        PointNear(first.StartPoint, far1World, "Fillet did not preserve the selected curve1 parameter orientation.");
        PointNear(first.EndPoint, tangent1, "Fillet did not trim curve1's explicitly selected end.");
        PointNear(second.StartPoint, tangent2, "Fillet did not trim curve2's explicitly selected start.");
        PointNear(second.EndPoint, far2World, "Fillet did not preserve curve2's retained remote end.");
        Point3d actualTangent1 = EndpointClosest(first, tangent1);
        Point3d actualTangent2 = EndpointClosest(second, tangent2);
        double line1Residual = VerifyLineSupport(first, cornerWorld, far1World, actualTangent1, "fillet curve1");
        double line2Residual = VerifyLineSupport(second, cornerWorld, far2World, actualTangent2, "fillet curve2");
        Arc fillet = FindArcByRadiusAndCenter(sketch, radius, center);
        Point3d arcStart = ArcEndpoint(fillet, true);
        Point3d arcEnd = ArcEndpoint(fillet, false);
        Require((PointDistance(arcStart, tangent1) <= PositionToleranceMm && PointDistance(arcEnd, tangent2) <= PositionToleranceMm) ||
            (PointDistance(arcStart, tangent2) <= PositionToleranceMm && PointDistance(arcEnd, tangent1) <= PositionToleranceMm),
            "Native fillet arc endpoints do not meet the two independently calculated tangent points.");
        Require(Math.Abs(fillet.Radius - radius) <= LengthToleranceMm, "Native fillet radius differs from the requested radius.");
        Require(Math.Abs(ArcLength(fillet) - expectedArcLength) <= LengthToleranceMm,
            "Native fillet arc length differs from the independent expected sweep.");
        VerifyLineArcTangent(fillet, first, actualTangent1, frame.Normal, "curve1");
        VerifyLineArcTangent(fillet, second, actualTangent2, frame.Normal, "curve2");
        int actualCurveCount = CountCurveObjects(sketch);
        Require(actualCurveCount == expectedCurveCount,
            "Line-Line fillet native curve count differs from the fixture expectation (" + expectedCurveCount + " Curve objects).");
        Require(PointDistance(cornerWorld, tangent1) > PositionToleranceMm && PointDistance(cornerWorld, tangent2) > PositionToleranceMm,
            "Fillet left one selected endpoint untrimmed at the original shared corner.");
        evidence["fillet_direct_geometry"] = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "selected_corner_world_mm", Point(cornerWorld) }, { "tangent1_world_mm", Point(tangent1) },
            { "tangent2_world_mm", Point(tangent2) }, { "center_world_mm", Point(center) },
            { "radius_mm", fillet.Radius }, { "arc_length_mm", ArcLength(fillet) },
            { "curve1_original_support_line_residual_mm", line1Residual },
            { "curve2_original_support_line_residual_mm", line2Residual },
            { "arc_start_world_mm", Point(arcStart) }, { "arc_end_world_mm", Point(arcEnd) },
            { "curve_count", actualCurveCount }, { "expected_curve_count", expectedCurveCount },
            { "all_sketch_objects", NativeGeometryRecords(sketch) }
        };
    }

    private static void VerifyLineArcFillet(Sketch sketch, Frame frame, Dictionary<string, object> evidence)
    {
        Point3d lineFar = WorldPoint(frame, 10, 9);
        Point3d lineTangent = WorldPoint(frame, 10, Math.Sqrt(6.0));
        Point3d arcTangent = WorldPoint(frame, 9.6, 1.959591794226542);
        Point3d originalArcFar = WorldPoint(frame, 8, 0);
        Point3d center = WorldPoint(frame, 9.5, Math.Sqrt(6.0));
        Line retainedLine = FindLineWithEndpoint(sketch, lineTangent, lineFar);
        PointNear(retainedLine.StartPoint, lineTangent, "Line-Arc fillet tangent point on the Line is incorrect.");
        PointNear(retainedLine.EndPoint, lineFar, "Line-Arc fillet moved the Line far endpoint.");
        Arc fillet = FindArcByRadiusAndCenter(sketch, 0.5, center);
        Arc original = FindArcByRadiusAndEndpoint(sketch, 2.0, originalArcFar, fillet.JournalIdentifier);
        double expectedArcStart = Math.Atan2(arcTangent.Y, arcTangent.X - 10.0);
        VerifyArcSupport(original, WorldPoint(frame, 10, 0), 2.0, frame.Normal,
            expectedArcStart, Pi, "Line-Arc retained source circle");
        Require((PointDistance(ArcEndpoint(fillet, true), lineTangent) <= PositionToleranceMm &&
            PointDistance(ArcEndpoint(fillet, false), arcTangent) <= PositionToleranceMm) ||
            (PointDistance(ArcEndpoint(fillet, false), lineTangent) <= PositionToleranceMm &&
            PointDistance(ArcEndpoint(fillet, true), arcTangent) <= PositionToleranceMm),
            "Line-Arc fillet endpoints do not match independent tangency coordinates.");
        PointNear(ArcEndpoint(original, true), arcTangent, "Fillet did not trim the selected Arc start parameter.");
        PointNear(ArcEndpoint(original, false), originalArcFar, "Filleting the Line-Arc pair changed the original arc remote end parameter.");
        PointNear(retainedLine.StartPoint, lineTangent, "Line-Arc fillet did not trim the explicitly selected Line start.");
        PointNear(retainedLine.EndPoint, lineFar, "Line-Arc fillet changed the Line remote end parameter.");
        Require(Math.Abs(fillet.Radius - 0.5) <= LengthToleranceMm, "Line-Arc fillet radius differs from 0.5 mm.");
        Require(CountCurveObjects(sketch) == 3, "Line-Arc fillet must leave the retained Line, original trimmed Arc, and added fillet Arc as three curves.");
        double lineResidual = VerifyLineSupport(retainedLine, WorldPoint(frame, 10, 2), lineFar, lineTangent, "Line-Arc retained source Line");
        VerifyLineArcTangent(fillet, retainedLine, lineTangent, frame.Normal, "line-arc line contact");
        VerifyArcArcTangent(fillet, original, arcTangent, "line-arc circle contact");
        Require(ArcLength(original) < Pi + LengthToleranceMm, "Native original arc was not trimmed at the selected endpoint.");
        evidence["line_arc_native_readback"] = NativeGeometryRecords(sketch);
        evidence["fillet_radius_mm"] = fillet.Radius;
        evidence["fillet_arc_length_mm"] = ArcLength(fillet);
        evidence["original_arc_remaining_length_mm"] = ArcLength(original);
        evidence["original_arc_center_mm"] = Point(original.CenterPoint);
        evidence["original_arc_radius_mm"] = original.Radius;
        evidence["original_arc_start_parameter_rad"] = original.StartAngle;
        evidence["original_arc_end_parameter_rad"] = original.EndAngle;
        evidence["retained_line_support_residual_mm"] = lineResidual;
        evidence["line_tangent_mm"] = Point(lineTangent);
        evidence["arc_tangent_mm"] = Point(arcTangent);
    }

    private static void VerifyArcArcFillet(Sketch sketch, double originalRadius, Dictionary<string, object> evidence)
    {
        Point3d center1 = new Point3d(0, 0, 0);
        Point3d center2 = new Point3d(2, 0, 0);
        double filletX = 1.0 - originalRadius * 0.25;
        double filletY = Math.Sqrt(15.0) / 4.0;
        Point3d filletCenter = new Point3d(filletX, filletY, 0);
        double distance1 = PointDistance(filletCenter, center1);
        double distance2 = PointDistance(filletCenter, center2);
        Point3d tangent1 = new Point3d(originalRadius * filletX / distance1, originalRadius * filletY / distance1, 0);
        Point3d tangent2 = new Point3d(2.0 + originalRadius * (filletX - 2.0) / distance2,
            originalRadius * filletY / distance2, 0);
        Point3d far1 = new Point3d(-1, 1, 0);
        Point3d far2 = new Point3d(1, -1, 0);
        Arc fillet = FindArcByRadiusAndCenter(sketch, 0.25, filletCenter);
        Arc original1 = FindArcByRadiusAndEndpoint(sketch, originalRadius, far1, fillet.JournalIdentifier);
        Arc original2 = FindArcByRadiusAndEndpoint(sketch, originalRadius, far2, fillet.JournalIdentifier, original1.JournalIdentifier);
        double original1Start = Math.Atan2(tangent1.Y - center1.Y, tangent1.X - center1.X);
        double original2Start = Math.Atan2(tangent2.Y - center2.Y, tangent2.X - center2.X);
        VerifyArcSupport(original1, center1, originalRadius, new double[] { 0, 0, 1 }, original1Start, 3.0 * Pi / 4.0,
            "Arc-Arc retained source circle 1");
        VerifyArcSupport(original2, center2, originalRadius, new double[] { 0, 0, 1 }, original2Start, 5.0 * Pi / 4.0,
            "Arc-Arc retained source circle 2");
        Require((PointDistance(ArcEndpoint(fillet, true), tangent1) <= PositionToleranceMm &&
            PointDistance(ArcEndpoint(fillet, false), tangent2) <= PositionToleranceMm) ||
            (PointDistance(ArcEndpoint(fillet, false), tangent1) <= PositionToleranceMm &&
            PointDistance(ArcEndpoint(fillet, true), tangent2) <= PositionToleranceMm),
            "Arc-Arc fillet endpoints differ from independently constructed tangent points.");
        PointNear(ArcEndpoint(original1, true), tangent1, "Arc-Arc fillet did not trim curve1's explicitly selected start.");
        PointNear(ArcEndpoint(original1, false), far1, "Arc-Arc fillet changed curve1's remote end parameter.");
        PointNear(ArcEndpoint(original2, true), tangent2, "Arc-Arc fillet did not trim curve2's explicitly selected start.");
        PointNear(ArcEndpoint(original2, false), far2, "Arc-Arc fillet changed curve2's remote end parameter.");
        VerifyArcArcTangent(fillet, original1, tangent1, "first arc contact");
        VerifyArcArcTangent(fillet, original2, tangent2, "second arc contact");
        Require(ArcLength(original1) < originalRadius * Pi / 2.0 + LengthToleranceMm &&
            ArcLength(original2) < originalRadius * Pi / 2.0 + LengthToleranceMm,
            "Arc-Arc fillet did not trim both selected original arcs.");
        Require(CountCurveObjects(sketch) == 3, "Arc-Arc fillet must leave two trimmed source arcs plus one fillet arc.");
        VerifyArcPlane(fillet, new double[] { 0, 0, 1 }, "Arc-Arc fillet arc");
        evidence["arc_arc_native_readback"] = NativeGeometryRecords(sketch);
        evidence["fillet_center_mm"] = Point(filletCenter);
        evidence["tangent1_mm"] = Point(tangent1);
        evidence["tangent2_mm"] = Point(tangent2);
        evidence["fillet_radius_mm"] = fillet.Radius;
        evidence["original_arc1_remaining_length_mm"] = ArcLength(original1);
        evidence["original_arc2_remaining_length_mm"] = ArcLength(original2);
        evidence["original_arc1_domain_rad"] = new double[] { original1.StartAngle, original1.EndAngle };
        evidence["original_arc2_domain_rad"] = new double[] { original2.StartAngle, original2.EndAngle };
    }

    private static void VerifyChamfer(Sketch sketch, Frame frame, double[] corner,
        double[] far1, double[] far2, double[] trim1, double[] trim2,
        double expectedDistance1, double expectedDistance2, double expectedLength,
        double? expectedAngleRad, Dictionary<string, object> evidence)
    {
        Point3d cornerWorld = WorldPoint(frame, corner[0], corner[1]);
        Point3d far1World = WorldPoint(frame, far1[0], far1[1]);
        Point3d far2World = WorldPoint(frame, far2[0], far2[1]);
        Point3d trim1World = WorldPoint(frame, trim1[0], trim1[1]);
        Point3d trim2World = WorldPoint(frame, trim2[0], trim2[1]);
        Line retained1 = FindLineWithEndpoint(sketch, trim1World, far1World);
        Line retained2 = FindLineWithEndpoint(sketch, trim2World, far2World);
        Require(LineHasEndpoints(retained1, trim1World, far1World), "Chamfer changed curve1's selected trim or retained far endpoint.");
        Require(LineHasEndpoints(retained2, trim2World, far2World), "Chamfer changed curve2's selected trim or retained far endpoint.");
        PointNear(retained1.StartPoint, far1World, "Chamfer changed curve1's retained start parameter.");
        PointNear(retained1.EndPoint, trim1World, "Chamfer did not trim curve1's explicitly selected end.");
        PointNear(retained2.StartPoint, trim2World, "Chamfer did not trim curve2's explicitly selected start.");
        PointNear(retained2.EndPoint, far2World, "Chamfer changed curve2's retained end parameter.");
        Point3d actualTrim1 = EndpointClosest(retained1, trim1World);
        Point3d actualTrim2 = EndpointClosest(retained2, trim2World);
        double line1Residual = VerifyLineSupport(retained1, cornerWorld, far1World, actualTrim1, "chamfer curve1");
        double line2Residual = VerifyLineSupport(retained2, cornerWorld, far2World, actualTrim2, "chamfer curve2");
        Line chamfer = FindLineWithEndpoints(sketch, trim1World, trim2World, retained1.JournalIdentifier, retained2.JournalIdentifier);
        double actualLength = PointDistance(chamfer.StartPoint, chamfer.EndPoint);
        Require(Math.Abs(actualLength - expectedLength) <= LengthToleranceMm,
            "Native chamfer segment length differs from the independent expected value.");
        double trimDistance1 = PointDistance(cornerWorld, actualTrim1);
        double trimDistance2 = PointDistance(cornerWorld, actualTrim2);
        Require(Math.Abs(trimDistance1 - expectedDistance1) <= PositionToleranceMm,
            "Curve1 chamfer trim distance differs from the requested value.");
        Require(Math.Abs(trimDistance2 - expectedDistance2) <= PositionToleranceMm,
            "Curve2 chamfer trim distance differs from the requested value.");
        double actualAngle = AngleBetween(Sub(cornerWorld, actualTrim1), Sub(actualTrim2, actualTrim1));
        if (expectedAngleRad.HasValue)
            Require(Math.Abs(actualAngle - expectedAngleRad.Value) <= AngleToleranceRad,
                "Native chamfer endpoint geometry does not produce the requested A-angle.");
        evidence["chamfer_native_geometry"] = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "corner_world_mm", Point(cornerWorld) }, { "curve1_trim_world_mm", Point(actualTrim1) },
            { "curve2_trim_world_mm", Point(actualTrim2) }, { "curve1_trim_distance_mm", trimDistance1 },
            { "curve2_trim_distance_mm", trimDistance2 }, { "segment_length_mm", actualLength },
            { "curve1_original_support_line_residual_mm", line1Residual },
            { "curve2_original_support_line_residual_mm", line2Residual },
            { "measured_A_angle_rad", actualAngle }, { "expected_A_angle_rad", expectedAngleRad },
            { "chamfer_line", FindNativeGeometryRecord(sketch, chamfer.JournalIdentifier) }
        };
    }

    private static Dictionary<string, object> ExpectedFillet(double[] corner, double[] far1, double[] far2, double radius)
    {
        double[] e1 = Normalize(Sub(far1, corner));
        double[] e2 = Normalize(Sub(far2, corner));
        double theta = Math.Atan2(Math.Abs(Cross2(e1, e2)), Dot2(e1, e2));
        double distance = radius / Math.Tan(theta / 2.0);
        double[] point1 = Add(corner, Scale(e1, distance));
        double[] point2 = Add(corner, Scale(e2, distance));
        double[] bisector = Normalize(Add(e1, e2));
        double[] center = Add(corner, Scale(bisector, radius / Math.Sin(theta / 2.0)));
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "corner_angle_rad", theta }, { "tangent_distance_mm", distance },
            { "point1", point1 }, { "point2", point2 }, { "center", center },
            { "arc_sweep_rad", Pi - theta }, { "arc_length", radius * (Pi - theta) }
        };
    }

    private static M3SketchRequest GeometryOnly(List<M3SketchGeometryRequest> geometries)
    {
        return new M3SketchRequest {
            OriginMm = new double[] { 0, 0, 0 }, Normal = new double[] { 0, 0, 1 }, XAxis = new double[] { 1, 0, 0 },
            ConstraintMode = "geometry_only", Geometries = geometries
        };
    }

    private static M3SketchGeometryRequest Line(string id, double x1, double y1, double x2, double y2)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "line", StartMm = new double[] { x1, y1 }, EndMm = new double[] { x2, y2 } };
    }

    private static M3SketchGeometryRequest Circle(string id, double x, double y, double diameter)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "circle", CenterMm = new double[] { x, y }, DiameterMm = diameter };
    }

    private static M3SketchGeometryRequest Arc(string id, double x, double y, double radius, double startDeg, double sweepDeg)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "arc", CenterMm = new double[] { x, y }, RadiusMm = radius,
            StartAngleDeg = startDeg, SweepDeg = sweepDeg };
    }

    private static Dictionary<string, object> NativeSnapshot(Part part, Sketch onlySketch)
    {
        List<string> features = new List<string>();
        foreach (Feature feature in part.Features.GetFeatures()) if (feature != null) features.Add(feature.JournalIdentifier);
        List<string> sketches = new List<string>();
        foreach (Sketch sketch in part.Sketches.ToArray()) if (sketch != null) sketches.Add(sketch.JournalIdentifier);
        List<string> bodies = new List<string>();
        foreach (Body body in part.Bodies.ToArray()) if (body != null) bodies.Add(body.JournalIdentifier);
        features.Sort(StringComparer.Ordinal); sketches.Sort(StringComparer.Ordinal); bodies.Sort(StringComparer.Ordinal);
        List<object> sketchRecords = new List<object>();
        if (onlySketch != null) sketchRecords.Add(new Dictionary<string, object> {
            { "sketch_id", onlySketch.JournalIdentifier }, { "geometry", NativeGeometryRecords(onlySketch) },
            { "curve_count", CountCurveObjects(onlySketch) }, { "object_count", onlySketch.GetAllGeometry().Length },
            { "constraints", NativeConstraintRecords(onlySketch) }
        });
        else foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null) sketchRecords.Add(new Dictionary<string, object> {
                { "sketch_id", sketch.JournalIdentifier }, { "geometry", NativeGeometryRecords(sketch) },
                { "curve_count", CountCurveObjects(sketch) }, { "object_count", sketch.GetAllGeometry().Length },
                { "constraints", NativeConstraintRecords(sketch) }
            });
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "features", features }, { "sketches", sketches }, { "bodies", bodies },
            { "is_modified", part.IsModified }, { "native_sketches", sketchRecords }
        };
    }

    private static List<object> NativeGeometryRecords(Sketch sketch)
    {
        NXObject[] objects = sketch.GetAllGeometry();
        Require(objects != null, "Sketch.GetAllGeometry returned null.");
        List<object> records = new List<object>();
        foreach (NXObject item in objects)
        {
            Require(item != null, "Sketch.GetAllGeometry contains null.");
            SmartObject smartObject = item as SmartObject;
            Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "journal_id", item.JournalIdentifier }, { "tag", item.Tag.ToString() },
                { "object_type", item.GetType().Name }, { "full_type", item.GetType().FullName },
                { "is_sketch_geometry", smartObject != null && sketch.IsSketchGeometry(smartObject) },
                { "is_curve", item is Curve }
            };
            Line line = item as Line;
            Arc arc = item as Arc;
            if (line != null)
            {
                record["start_mm"] = Point(line.StartPoint);
                record["end_mm"] = Point(line.EndPoint);
                record["length_mm"] = PointDistance(line.StartPoint, line.EndPoint);
            }
            else if (arc != null)
            {
                record["center_mm"] = Point(arc.CenterPoint);
                record["radius_mm"] = arc.Radius;
                record["start_angle_rad"] = arc.StartAngle;
                record["end_angle_rad"] = arc.EndAngle;
                record["start_mm"] = Point(ArcEndpoint(arc, true));
                record["end_mm"] = Point(ArcEndpoint(arc, false));
                record["arc_length_mm"] = ArcLength(arc);
            }
            else
            {
                NXOpen.Point point = item as NXOpen.Point;
                if (point != null) record["coordinates_mm"] = Point(point.Coordinates);
            }
            records.Add(record);
        }
        return records;
    }

    private static Dictionary<string, object> FindNativeGeometryRecord(Sketch sketch, string journalId)
    {
        foreach (object value in NativeGeometryRecords(sketch))
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            if (record != null && String.Equals(StringValue(record, "journal_id"), journalId, StringComparison.Ordinal)) return record;
        }
        return null;
    }

    private static List<object> NativeConstraintRecords(Sketch sketch)
    {
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
            Require(constraints != null, "Native sketch constraint enumeration returned null for " + type + ".");
            foreach (SketchConstraint constraint in constraints)
            {
                Require(constraint != null, "Native sketch constraints contain a null entry.");
                Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "journal_id", constraint.JournalIdentifier }, { "constraint_type", type.ToString() },
                    { "class", constraint.GetType().Name }
                };
                SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                if (dimension != null)
                {
                    Expression expression = dimension.AssociatedExpression;
                    List<string> refs = new List<string>();
                    Sketch.DimensionGeometry[] operands = dimension.GetDimensionGeometry();
                    Require(operands != null, "Native dimensional constraint geometry readback returned null.");
                    foreach (Sketch.DimensionGeometry operand in operands)
                        refs.Add((operand.Geometry == null ? "null" : operand.Geometry.JournalIdentifier) + "|" + operand.AssocType + "|" + operand.AssocValue.ToString(CultureInfo.InvariantCulture));
                    refs.Sort(StringComparer.Ordinal);
                    row["dimension_state"] = dimension.DimensionState.ToString();
                    row["is_driving"] = dimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving;
                    row["expression_name"] = expression == null ? null : expression.Name;
                    row["rhs"] = expression == null ? null : expression.RightHandSide;
                    row["value"] = expression == null ? (object)null : expression.Value;
                    row["unit"] = expression == null || expression.Units == null ? null : expression.Units.Symbol;
                    row["geometry_refs"] = refs;
                }
                else
                {
                    SketchGeometricConstraint geometric = constraint as SketchGeometricConstraint;
                    if (geometric != null)
                    {
                        List<string> refs = new List<string>();
                        Sketch.ConstraintGeometry[] operands = geometric.GetGeometry();
                        Require(operands != null, "Native geometric constraint geometry readback returned null.");
                        foreach (Sketch.ConstraintGeometry operand in operands)
                            refs.Add((operand.Geometry == null ? "null" : operand.Geometry.JournalIdentifier) + "|" +
                                operand.PointType + "|" + operand.SplineDefiningPointIndex.ToString(CultureInfo.InvariantCulture));
                        refs.Sort(StringComparer.Ordinal);
                        row["geometry_refs"] = refs;
                    }
                }
                records.Add(row);
            }
        }
        records.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b) {
            return StringComparer.Ordinal.Compare(Convert.ToString(a["journal_id"], CultureInfo.InvariantCulture),
                Convert.ToString(b["journal_id"], CultureInfo.InvariantCulture));
        });
        List<object> result = new List<object>();
        foreach (Dictionary<string, object> row in records) result.Add(row);
        return result;
    }

    private static void CheckNativeFrame(Sketch sketch, Frame expected, Dictionary<string, object> evidence)
    {
        Point3d origin = sketch.Origin;
        Matrix3x3 axes = sketch.Orientation.Element;
        double[] actualOrigin = new double[] { origin.X, origin.Y, origin.Z };
        double[] actualX = new double[] { axes.Xx, axes.Xy, axes.Xz };
        double[] actualY = new double[] { axes.Yx, axes.Yy, axes.Yz };
        double[] actualNormal = new double[] { axes.Zx, axes.Zy, axes.Zz };
        Require(VectorDistance(actualOrigin, expected.Origin) <= PositionToleranceMm, "Native sketch origin differs from the independent frame.");
        Require(VectorDistance(actualX, expected.XAxis) <= FrameTolerance, "Native sketch X direction differs from the independent frame.");
        Require(VectorDistance(actualY, expected.YAxis) <= FrameTolerance, "Native sketch Y direction differs from the independent frame.");
        Require(VectorDistance(actualNormal, expected.Normal) <= FrameTolerance, "Native sketch normal differs from the independent frame.");
        Require(Math.Abs(Dot3(Cross3(actualX, actualY), actualNormal) - 1.0) <= FrameTolerance,
            "Native sketch axes are not right-handed and orthonormal.");
        evidence["native_frame"] = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "origin_mm", actualOrigin }, { "x_axis_world", actualX }, { "y_axis_world", actualY }, { "normal_world", actualNormal }
        };
    }

    private static string FindInspectedGeometryId(Dictionary<string, object> inspection,
        M3SketchGeometryRequest requested, Frame frame)
    {
        List<object> records = FindAllGeometryRecords(inspection);
        foreach (object item in records)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            if (row == null) continue;
            if (InspectionMatchesRequest(row, requested, frame))
            {
                string journalId = StringValue(row, "journal_id");
                if (!String.IsNullOrWhiteSpace(journalId)) return journalId;
            }
        }
        return null;
    }

    private static bool InspectionMatchesRequest(Dictionary<string, object> record,
        M3SketchGeometryRequest requested, Frame frame)
    {
        string actualKind = StringValue(record, "kind");
        if (requested.Type == "line")
        {
            if (!String.Equals(actualKind, "LINE", StringComparison.Ordinal)) return false;
            Point3d expectedStart = WorldPoint(frame, requested.StartMm[0], requested.StartMm[1]);
            Point3d expectedEnd = WorldPoint(frame, requested.EndMm[0], requested.EndMm[1]);
            Point3d actualStart = Point3dFromArray(DoubleArray(record["start"]));
            Point3d actualEnd = Point3dFromArray(DoubleArray(record["end"]));
            return (PointDistance(actualStart, expectedStart) <= PositionToleranceMm && PointDistance(actualEnd, expectedEnd) <= PositionToleranceMm) ||
                (PointDistance(actualStart, expectedEnd) <= PositionToleranceMm && PointDistance(actualEnd, expectedStart) <= PositionToleranceMm);
        }
        if (requested.Type == "circle" || requested.Type == "arc")
        {
            if (!String.Equals(actualKind, "ARC", StringComparison.Ordinal)) return false;
            Point3d expectedCenter = WorldPoint(frame, requested.CenterMm[0], requested.CenterMm[1]);
            Point3d actualCenter = Point3dFromArray(DoubleArray(record["center"]));
            double expectedRadius = requested.Type == "circle" ? requested.DiameterMm / 2.0 : requested.RadiusMm;
            object rawRadius;
            return PointDistance(actualCenter, expectedCenter) <= PositionToleranceMm &&
                record.TryGetValue("radius_mm", out rawRadius) && rawRadius != null &&
                Math.Abs(Convert.ToDouble(rawRadius, CultureInfo.InvariantCulture) - expectedRadius) <= LengthToleranceMm;
        }
        return false;
    }

    private static List<object> FindAllGeometryRecords(object value)
    {
        List<object> result = new List<object>();
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            foreach (KeyValuePair<string, object> item in dictionary)
            {
                List<object> list = item.Value as List<object>;
                if (list != null && (String.Equals(item.Key, "profile_geometry", StringComparison.Ordinal) ||
                    String.Equals(item.Key, "geometry", StringComparison.Ordinal)))
                {
                    foreach (object child in list) result.Add(child);
                }
                else result.AddRange(FindAllGeometryRecords(item.Value));
            }
        }
        else
        {
            List<object> list = value as List<object>;
            if (list != null) foreach (object child in list) result.AddRange(FindAllGeometryRecords(child));
        }
        return result;
    }

    private static Line FindLine(Sketch sketch, Point3d expectedStart, Point3d expectedEnd)
    {
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Line line = item as Line;
            if (line != null && ((PointDistance(line.StartPoint, expectedStart) <= PositionToleranceMm && PointDistance(line.EndPoint, expectedEnd) <= PositionToleranceMm) ||
                (PointDistance(line.StartPoint, expectedEnd) <= PositionToleranceMm && PointDistance(line.EndPoint, expectedStart) <= PositionToleranceMm))) return line;
        }
        throw new InvalidOperationException("Native sketch does not contain the requested Line endpoints.");
    }

    private static Line FindLineWithEndpoint(Sketch sketch, Point3d tangent, Point3d far)
    {
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Line line = item as Line;
            if (line == null) continue;
            if ((PointDistance(line.StartPoint, tangent) <= PositionToleranceMm && PointDistance(line.EndPoint, far) <= PositionToleranceMm) ||
                (PointDistance(line.EndPoint, tangent) <= PositionToleranceMm && PointDistance(line.StartPoint, far) <= PositionToleranceMm)) return line;
        }
        throw new InvalidOperationException("Native sketch does not contain a retained Line between the expected tangent and far endpoint.");
    }

    private static bool LineHasEndpoints(Line line, Point3d point1, Point3d point2)
    {
        return (PointDistance(line.StartPoint, point1) <= PositionToleranceMm && PointDistance(line.EndPoint, point2) <= PositionToleranceMm) ||
            (PointDistance(line.StartPoint, point2) <= PositionToleranceMm && PointDistance(line.EndPoint, point1) <= PositionToleranceMm);
    }

    private static Point3d EndpointClosest(Line line, Point3d expected)
    {
        return PointDistance(line.StartPoint, expected) <= PointDistance(line.EndPoint, expected) ? line.StartPoint : line.EndPoint;
    }

    private static Line FindLineWithEndpoints(Sketch sketch, Point3d point1, Point3d point2, string exclude1, string exclude2)
    {
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Line line = item as Line;
            if (line == null || String.Equals(line.JournalIdentifier, exclude1, StringComparison.Ordinal) ||
                String.Equals(line.JournalIdentifier, exclude2, StringComparison.Ordinal)) continue;
            if ((PointDistance(line.StartPoint, point1) <= PositionToleranceMm && PointDistance(line.EndPoint, point2) <= PositionToleranceMm) ||
                (PointDistance(line.StartPoint, point2) <= PositionToleranceMm && PointDistance(line.EndPoint, point1) <= PositionToleranceMm)) return line;
        }
        throw new InvalidOperationException("Native sketch does not contain the expected independent chamfer Line.");
    }

    private static Arc FindArcByRadiusAndCenter(Sketch sketch, double radius, Point3d center)
    {
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Arc arc = item as Arc;
            if (arc != null && Math.Abs(arc.Radius - radius) <= LengthToleranceMm &&
                PointDistance(arc.CenterPoint, center) <= PositionToleranceMm) return arc;
        }
        throw new InvalidOperationException("Native sketch has no Arc with the expected independent center and radius.");
    }

    private static Arc FindArcByRadiusAndEndpoint(Sketch sketch, double radius, Point3d endpoint,
        string excludeJournalId, string optionalExcludeJournalId = null)
    {
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Arc arc = item as Arc;
            if (arc == null || Math.Abs(arc.Radius - radius) > LengthToleranceMm ||
                String.Equals(arc.JournalIdentifier, excludeJournalId, StringComparison.Ordinal) ||
                String.Equals(arc.JournalIdentifier, optionalExcludeJournalId, StringComparison.Ordinal)) continue;
            if (PointDistance(ArcEndpoint(arc, true), endpoint) <= PositionToleranceMm ||
                PointDistance(ArcEndpoint(arc, false), endpoint) <= PositionToleranceMm) return arc;
        }
        throw new InvalidOperationException("Native sketch has no remaining original Arc with the expected far endpoint.");
    }

    private static Point3d ArcEndpoint(Arc arc, bool start)
    {
        Point3d center;
        Vector3d xAxis, yAxis;
        arc.GetOrientation(out center, out xAxis, out yAxis);
        double angle = start ? arc.StartAngle : arc.EndAngle;
        return new Point3d(center.X + arc.Radius * (Math.Cos(angle) * xAxis.X + Math.Sin(angle) * yAxis.X),
            center.Y + arc.Radius * (Math.Cos(angle) * xAxis.Y + Math.Sin(angle) * yAxis.Y),
            center.Z + arc.Radius * (Math.Cos(angle) * xAxis.Z + Math.Sin(angle) * yAxis.Z));
    }

    private static double ArcLength(Arc arc) { return Math.Abs(arc.EndAngle - arc.StartAngle) * arc.Radius; }

    private static void VerifyLineArcTangent(Arc arc, Line line, Point3d contact, double[] normal, string label)
    {
        VerifyArcPlane(arc, normal, label + " fillet Arc");
        Point3d center = arc.CenterPoint;
        Point3d sourceCut = EndpointClosest(line, contact);
        Point3d sourceFar = PointDistance(line.StartPoint, sourceCut) <= PositionToleranceMm ? line.EndPoint : line.StartPoint;
        double[] sourceAway = Normalize3(new double[] { sourceFar.X - sourceCut.X, sourceFar.Y - sourceCut.Y, sourceFar.Z - sourceCut.Z });
        double[] filletAway = ArcTangentAwayFromEndpoint(arc, contact);
        double tangentDot = Dot3(sourceAway, filletAway);
        Require(tangentDot <= -1.0 + FrameTolerance,
            label + " source/fillet away-from-contact tangents are not oppositely directed.");
        Require(Math.Abs(PointDistance(contact, center) - arc.Radius) <= LengthToleranceMm,
            label + " contact point is not on the native fillet circle.");
    }

    private static double[] ArcTangentAwayFromEndpoint(Arc arc, Point3d endpoint)
    {
        Point3d center;
        Vector3d xAxis, yAxis;
        arc.GetOrientation(out center, out xAxis, out yAxis);
        double startDistance = PointDistance(ArcEndpoint(arc, true), endpoint);
        double endDistance = PointDistance(ArcEndpoint(arc, false), endpoint);
        Require(Math.Min(startDistance, endDistance) <= PositionToleranceMm,
            "Tangency point is not an endpoint of the native Arc.");
        bool atStart = startDistance <= endDistance;
        double parameter = atStart ? arc.StartAngle : arc.EndAngle;
        double sense = arc.EndAngle >= arc.StartAngle ? 1.0 : -1.0;
        double endpointDirection = atStart ? sense : -sense;
        double[] parameterTangent = new double[] {
            -Math.Sin(parameter) * xAxis.X + Math.Cos(parameter) * yAxis.X,
            -Math.Sin(parameter) * xAxis.Y + Math.Cos(parameter) * yAxis.Y,
            -Math.Sin(parameter) * xAxis.Z + Math.Cos(parameter) * yAxis.Z
        };
        return Normalize3(new double[] {
            endpointDirection * parameterTangent[0], endpointDirection * parameterTangent[1], endpointDirection * parameterTangent[2]
        });
    }

    private static double VerifyLineSupport(Line line, Point3d originalCorner, Point3d originalFar,
        Point3d actualCut, string label)
    {
        Point3d actualFar = EndpointClosest(line, originalFar);
        Point3d actualTangent = EndpointClosest(line, actualCut);
        Require(PointDistance(actualFar, originalFar) <= PositionToleranceMm,
            label + " did not retain the original remote endpoint.");
        Require(PointDistance(actualTangent, actualCut) <= PositionToleranceMm,
            label + " trim point differs from the independent geometric result.");
        double[] originalDirection = new double[] { originalFar.X - originalCorner.X,
            originalFar.Y - originalCorner.Y, originalFar.Z - originalCorner.Z };
        double[] actualDirection = new double[] { actualFar.X - actualTangent.X,
            actualFar.Y - actualTangent.Y, actualFar.Z - actualTangent.Z };
        double originalLength = Math.Sqrt(Dot3(originalDirection, originalDirection));
        Require(originalLength > 0.0, label + " input support line has zero length.");
        double residual = Math.Sqrt(Dot3(Cross3(originalDirection, actualDirection),
            Cross3(originalDirection, actualDirection))) / originalLength;
        Require(residual <= PositionToleranceMm, label + " trimmed native Line left its original support line.");
        return residual;
    }

    private static void VerifyArcSupport(Arc arc, Point3d expectedCenter, double expectedRadius,
        double[] expectedNormal, double expectedStartAngle, double expectedEndAngle, string label)
    {
        Require(PointDistance(arc.CenterPoint, expectedCenter) <= PositionToleranceMm,
            label + " changed the original circle center.");
        Require(Math.Abs(arc.Radius - expectedRadius) <= LengthToleranceMm,
            label + " changed the original circle radius.");
        VerifyArcPlane(arc, expectedNormal, label);
        Require(AngleDistance(arc.StartAngle, expectedStartAngle) <= AngleToleranceRad,
            label + " start parameter domain differs from the independent trim point.");
        Require(AngleDistance(arc.EndAngle, expectedEndAngle) <= AngleToleranceRad,
            label + " remote end parameter domain changed.");
    }

    private static void VerifyArcPlane(Arc arc, double[] expectedNormal, string label)
    {
        Point3d center;
        Vector3d xAxis, yAxis;
        arc.GetOrientation(out center, out xAxis, out yAxis);
        double[] actual = Normalize3(Cross3(new double[] { xAxis.X, xAxis.Y, xAxis.Z },
            new double[] { yAxis.X, yAxis.Y, yAxis.Z }));
        double[] expected = Normalize3(expectedNormal);
        Require(Dot3(actual, expected) >= 1.0 - FrameTolerance,
            label + " native arc plane normal changed.");
    }

    private static double AngleDistance(double actual, double expected)
    {
        return Math.Abs(Math.Atan2(Math.Sin(actual - expected), Math.Cos(actual - expected)));
    }

    private static void VerifyArcArcTangent(Arc fillet, Arc original, Point3d contact, string label)
    {
        double[] filletRadial = Normalize3(new double[] { contact.X - fillet.CenterPoint.X,
            contact.Y - fillet.CenterPoint.Y, contact.Z - fillet.CenterPoint.Z });
        double[] originalRadial = Normalize3(new double[] { contact.X - original.CenterPoint.X,
            contact.Y - original.CenterPoint.Y, contact.Z - original.CenterPoint.Z });
        Require(Math.Abs(Math.Abs(Dot3(filletRadial, originalRadial)) - 1.0) <= FrameTolerance,
            label + " fillet and source circle are not geometrically tangent at the contact.");
        Require(Math.Abs(PointDistance(contact, fillet.CenterPoint) - fillet.Radius) <= LengthToleranceMm &&
            Math.Abs(PointDistance(contact, original.CenterPoint) - original.Radius) <= LengthToleranceMm,
            label + " tangency point is not on both native circles.");
        double awayDot = Dot3(ArcTangentAwayFromEndpoint(fillet, contact), ArcTangentAwayFromEndpoint(original, contact));
        Require(awayDot <= -1.0 + FrameTolerance,
            label + " source and fillet away-from-contact tangents point in the same direction or select a complementary arc.");
    }

    private static int CountCurveObjects(Sketch sketch)
    {
        int count = 0;
        foreach (NXObject item in sketch.GetAllGeometry()) if (item is Curve) count++;
        return count;
    }

    private static void VerifyUnselectedLineUnchanged(object beforeValue, object afterValue,
        double[] start, double[] end, Dictionary<string, object> evidence)
    {
        Dictionary<string, object> before = FindNativeLineRecord(beforeValue, start, end);
        Dictionary<string, object> after = FindNativeLineRecord(afterValue, start, end);
        Require(before != null && after != null, "Unselected native Line was not present before and after the corner edit.");
        string beforeOwner = OnlyNativeSketchId(beforeValue);
        string afterOwner = OnlyNativeSketchId(afterValue);
        string beforeTag = ReadRequiredString(before, "tag");
        string afterTag = ReadRequiredString(after, "tag");
        Require(String.Equals(beforeOwner, afterOwner, StringComparison.Ordinal),
            "Unselected native Line moved to a different owning sketch.");
        Require(String.Equals(StringValue(before, "object_type"), "Line", StringComparison.Ordinal) &&
            String.Equals(StringValue(after, "object_type"), "Line", StringComparison.Ordinal) &&
            String.Equals(StringValue(before, "full_type"), StringValue(after, "full_type"), StringComparison.Ordinal),
            "Unselected native Line type changed.");
        Require(Object.Equals(before["is_sketch_geometry"], true) && Object.Equals(after["is_sketch_geometry"], true) &&
            Object.Equals(before["is_curve"], true) && Object.Equals(after["is_curve"], true),
            "Unselected native Line no longer belongs to its sketch geometry inventory.");
        Require(String.Equals(beforeTag, afterTag, StringComparison.Ordinal),
            "Unselected native Line Tag identity changed.");
        Require(SameValue(before["start_mm"], after["start_mm"]) && SameValue(before["end_mm"], after["end_mm"]),
            "Unselected native Line endpoints changed.");
        evidence["unselected_line_identity"] = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "object_type", StringValue(after, "object_type") }, { "full_type", StringValue(after, "full_type") },
            { "tag_before", beforeTag }, { "tag_after", afterTag },
            { "owner_sketch_id_before", beforeOwner }, { "owner_sketch_id_after", afterOwner },
            { "journal_id_before_diagnostic", StringValue(before, "journal_id") },
            { "journal_id_after_diagnostic", StringValue(after, "journal_id") },
            { "current_journal_id_from_native_requery", ReadRequiredString(after, "journal_id") },
            { "start_mm", before["start_mm"] }, { "end_mm", before["end_mm"] }
        };
    }

    private static string OnlyNativeSketchId(object snapshotValue)
    {
        Dictionary<string, object> snapshot = snapshotValue as Dictionary<string, object>;
        object rawSketches;
        Require(snapshot != null, "Unselected Line snapshot is invalid.");
        Require(snapshot.TryGetValue("native_sketches", out rawSketches),
            "Unselected Line snapshot omitted its native Sketch inventory.");
        List<object> sketches = rawSketches as List<object>;
        Require(sketches != null && sketches.Count == 1,
            "Unselected Line snapshot must contain exactly one owning native Sketch.");
        Dictionary<string, object> sketch = sketches[0] as Dictionary<string, object>;
        Require(sketch != null, "Unselected Line snapshot owning Sketch record is invalid.");
        return ReadRequiredString(sketch, "sketch_id");
    }

    private static Dictionary<string, object> FindNativeLineRecord(object snapshot, double[] start, double[] end)
    {
        List<object> records = FindAllGeometryRecords(snapshot);
        foreach (object item in records)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            if (row == null || !String.Equals(StringValue(row, "object_type"), "Line", StringComparison.Ordinal)) continue;
            double[] a = DoubleArray(row["start_mm"]);
            double[] b = DoubleArray(row["end_mm"]);
            if ((VectorDistance(a, start) <= PositionToleranceMm && VectorDistance(b, end) <= PositionToleranceMm) ||
                (VectorDistance(a, end) <= PositionToleranceMm && VectorDistance(b, start) <= PositionToleranceMm)) return row;
        }
        return null;
    }

    private static int CountDrivingConstraints(List<object> constraints)
    {
        int count = 0;
        if (constraints == null) return count;
        foreach (object value in constraints)
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            if (row != null && Object.Equals(row.ContainsKey("is_driving") ? row["is_driving"] : null, true)) count++;
        }
        return count;
    }

    private static int CountNewDrivingConstraints(List<object> before, List<object> after)
    {
        HashSet<string> beforeIds = new HashSet<string>(StringComparer.Ordinal);
        if (before != null) foreach (object item in before)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            if (row != null) beforeIds.Add(StringValue(row, "journal_id"));
        }
        int added = 0;
        if (after != null) foreach (object item in after)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            if (row != null && Object.Equals(row.ContainsKey("is_driving") ? row["is_driving"] : null, true) &&
                !beforeIds.Contains(StringValue(row, "journal_id"))) added++;
        }
        return added;
    }

    private static bool OriginalConstraintsPreserved(List<object> before, List<object> after)
    {
        if (before == null || after == null) return before == after;
        Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (object item in after)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            if (row != null) current[StringValue(row, "journal_id")] = M2Json.CanonicalText(row);
        }
        foreach (object item in before)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            if (row == null) return false;
            string id = StringValue(row, "journal_id");
            string actual;
            if (!current.TryGetValue(id, out actual) || !String.Equals(actual, M2Json.CanonicalText(row), StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool NativeCallWasRecorded(Dictionary<string, object> caseEvidence)
    {
        Dictionary<string, object> operation = FindNestedDictionary(caseEvidence, "native_operation");
        return operation != null && Object.Equals(operation.ContainsKey("native_operation_called") ? operation["native_operation_called"] : null, true);
    }

    private static bool NativeCallWasRecorded(object executionResult)
    {
        Dictionary<string, object> operation = FindNestedDictionary(executionResult, "native_operation");
        return operation != null && Object.Equals(operation.ContainsKey("native_operation_called") ? operation["native_operation_called"] : null, true);
    }

    private static void VerifyChamferOperationEvidence(M3ExecutionResult execution, double distance1,
        double? distance2, double? angleDeg, double expectedDistance2)
    {
        Require(execution != null && execution.Result != null, "Chamfer execute result is missing.");
        Dictionary<string, object> result = execution.Result;
        Dictionary<string, object> native = FindNestedDictionary(result, "native_operation");
        Dictionary<string, object> measured = FindNestedDictionary(result, "measured_geometry");
        Require(native != null && Object.Equals(native.ContainsKey("native_operation_called") ? native["native_operation_called"] : null, true),
            "Chamfer result omitted proof of the native builder call.");
        Require(measured != null, "Chamfer result omitted measured native geometry evidence.");
        Require(Math.Abs(ReadDouble(native, "distance1_rhs_before_commit") - distance1) <= LengthToleranceMm,
            "Native chamfer Distance1 RHS before commit differs from the requested public distance1_mm.");
        Require(Math.Abs(ReadDouble(native, "distance1_rhs_after_commit") - distance1) <= LengthToleranceMm,
            "Native chamfer Distance1 expression RHS differs from the requested public distance1_mm.");
        Require(Math.Abs(ReadDouble(measured, "distance1_mm") - distance1) <= LengthToleranceMm,
            "Native measured curve1 chamfer distance differs from distance1_mm.");
        Require(Math.Abs(ReadDouble(measured, "distance2_mm") - expectedDistance2) <= LengthToleranceMm,
            "Native measured curve2 chamfer distance differs from the independent expected value.");
        if (angleDeg.HasValue)
        {
            Require(String.Equals(StringValue(result, "requested_definition"), "distance_angle", StringComparison.Ordinal),
                "Native evidence did not retain the public distance-angle request mode.");
            Require(String.Equals(StringValue(result, "native_mode"), "Asymmetric", StringComparison.Ordinal),
                "Distance-angle request was not mapped to the verified Asymmetric native builder mode.");
            Require(Math.Abs(ReadDouble(native, "distance2_rhs_before_commit") - expectedDistance2) <= LengthToleranceMm,
                "Native builder second-distance RHS before commit differs from the independent angle formula.");
            Require(Math.Abs(ReadDouble(native, "distance2_rhs_after_commit") - expectedDistance2) <= LengthToleranceMm,
                "Native builder second-distance RHS differs from the distance-angle derived distance.");
            Require(Math.Abs(ReadDouble(measured, "calculated_distance2_mm") - expectedDistance2) <= LengthToleranceMm,
                "Production angle mapping did not report the independent sine-rule second distance.");
            Require(Math.Abs(ReadDouble(measured, "angle_deg") - angleDeg.Value) <= AngleToleranceRad * 180.0 / Pi,
                "Native chamfer measured A-angle differs from requested angle_deg.");
            Require(Math.Abs(ReadDouble(measured, "requested_angle_deg") - angleDeg.Value) <= 1e-12,
                "Production measured-geometry evidence lost the requested angle_deg.");
            Require(!distance2.HasValue, "Distance-angle request unexpectedly supplied distance2_mm.");
        }
        else
        {
            Require(distance2.HasValue, "Two-distance chamfer request omitted distance2_mm.");
            if (Math.Abs(distance1 - distance2.Value) <= LengthToleranceMm)
            {
                Require(String.Equals(StringValue(result, "requested_definition"), "equal_distance", StringComparison.Ordinal),
                    "Equal chamfer inputs were not retained as the equal_distance definition.");
                Require(String.Equals(StringValue(result, "native_mode"), "Symmetric", StringComparison.Ordinal),
                    "Equal chamfer distances were not mapped to the native Symmetric builder mode.");
                // NX uses Distance1 in Symmetric mode; the inactive Distance2 expression is not an acceptance value.
                // Both public equal distances are checked against independently read native trim endpoints above.
            }
            else
            {
                Require(String.Equals(StringValue(result, "requested_definition"), "two_distances", StringComparison.Ordinal),
                    "Unequal chamfer inputs were not retained as a two_distances definition.");
                Require(String.Equals(StringValue(result, "native_mode"), "Asymmetric", StringComparison.Ordinal),
                    "Unequal chamfer distances were not mapped to the native Asymmetric builder mode.");
                Require(Math.Abs(ReadDouble(native, "distance2_rhs_before_commit") - distance2.Value) <= LengthToleranceMm,
                    "Native chamfer Distance2 RHS before commit differs from requested distance2_mm.");
                Require(Math.Abs(ReadDouble(native, "distance2_rhs_after_commit") - distance2.Value) <= LengthToleranceMm,
                    "Native chamfer Distance2 expression RHS differs from requested distance2_mm.");
            }
        }
    }

    private static double ReadDouble(Dictionary<string, object> dictionary, string key)
    {
        object value = null;
        Require(dictionary != null && dictionary.TryGetValue(key, out value) && value != null,
            "Native result omitted numeric field " + key + ".");
        double result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        Require(IsFinite(result), "Native result field " + key + " is not finite.");
        return result;
    }

    private static Dictionary<string, object> FindNestedDictionary(object value, string key)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            object found;
            if (dictionary.TryGetValue(key, out found))
            {
                Dictionary<string, object> direct = found as Dictionary<string, object>;
                if (direct != null) return direct;
            }
            foreach (object child in dictionary.Values)
            {
                Dictionary<string, object> nested = FindNestedDictionary(child, key);
                if (nested != null) return nested;
            }
        }
        else
        {
            List<object> list = value as List<object>;
            if (list != null) foreach (object child in list)
            {
                Dictionary<string, object> nested = FindNestedDictionary(child, key);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static bool SameSnapshot(Dictionary<string, object> left, Dictionary<string, object> right)
    {
        return String.Equals(M2Json.CanonicalText(left), M2Json.CanonicalText(right), StringComparison.Ordinal);
    }

    private static List<object> SnapshotDiff(object before, object after)
    {
        List<object> changes = new List<object>();
        AppendSnapshotDiff("$", before, true, after, true, changes);
        return changes;
    }

    private static void AppendSnapshotDiff(string path, object before, bool beforePresent,
        object after, bool afterPresent, List<object> changes)
    {
        if (beforePresent != afterPresent)
        {
            changes.Add(SnapshotDiffRecord(path, before, beforePresent, after, afterPresent));
            return;
        }
        if (!beforePresent) return;

        Dictionary<string, object> beforeDictionary = before as Dictionary<string, object>;
        Dictionary<string, object> afterDictionary = after as Dictionary<string, object>;
        if (beforeDictionary != null && afterDictionary != null)
        {
            List<string> keys = new List<string>();
            foreach (string key in beforeDictionary.Keys) keys.Add(key);
            foreach (string key in afterDictionary.Keys) if (!keys.Contains(key)) keys.Add(key);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                object beforeValue;
                object afterValue;
                bool hasBefore = beforeDictionary.TryGetValue(key, out beforeValue);
                bool hasAfter = afterDictionary.TryGetValue(key, out afterValue);
                AppendSnapshotDiff(path + "." + key, beforeValue, hasBefore, afterValue, hasAfter, changes);
            }
            return;
        }

        List<object> beforeList = before as List<object>;
        List<object> afterList = after as List<object>;
        if (beforeList != null && afterList != null)
        {
            int count = Math.Max(beforeList.Count, afterList.Count);
            for (int index = 0; index < count; index++)
            {
                bool hasBefore = index < beforeList.Count;
                bool hasAfter = index < afterList.Count;
                AppendSnapshotDiff(path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                    hasBefore ? beforeList[index] : null, hasBefore,
                    hasAfter ? afterList[index] : null, hasAfter, changes);
            }
            return;
        }

        if (!SameValue(before, after))
            changes.Add(SnapshotDiffRecord(path, before, true, after, true));
    }

    private static Dictionary<string, object> SnapshotDiffRecord(string path, object before,
        bool beforePresent, object after, bool afterPresent)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "path", path }, { "before_present", beforePresent }, { "before", beforePresent ? before : null },
            { "after_present", afterPresent }, { "after", afterPresent ? after : null }
        };
    }

    private static bool WarmupReadEffectAllowed(Dictionary<string, object> before,
        Dictionary<string, object> after, Dictionary<string, object> readEffects, List<object> diff)
    {
        bool mayMarkModified;
        bool readModifiedBefore;
        bool readModifiedAfter;
        bool dirtyChangedByRead;
        bool snapshotModifiedBefore;
        bool snapshotModifiedAfter;
        if (before == null || after == null || readEffects == null || diff == null ||
            !TryBooleanValue(readEffects, "read_may_mark_modified", out mayMarkModified) || !mayMarkModified ||
            !TryBooleanValue(readEffects, "is_modified_before_read", out readModifiedBefore) ||
            !TryBooleanValue(readEffects, "is_modified_after_read", out readModifiedAfter) ||
            !TryBooleanValue(readEffects, "dirty_changed_by_read", out dirtyChangedByRead) ||
            !TryBooleanValue(before, "is_modified", out snapshotModifiedBefore) ||
            !TryBooleanValue(after, "is_modified", out snapshotModifiedAfter)) return false;
        if (readModifiedBefore != snapshotModifiedBefore || readModifiedAfter != snapshotModifiedAfter ||
            dirtyChangedByRead != (!readModifiedBefore && readModifiedAfter)) return false;
        if (diff.Count == 0) return snapshotModifiedBefore == snapshotModifiedAfter && !dirtyChangedByRead;
        if (diff.Count != 1) return false;

        Dictionary<string, object> change = diff[0] as Dictionary<string, object>;
        bool beforePresent;
        bool afterPresent;
        bool beforeValue;
        bool afterValue;
        return change != null && String.Equals(StringValue(change, "path"), "$.is_modified", StringComparison.Ordinal) &&
            TryBooleanValue(change, "before_present", out beforePresent) && beforePresent &&
            TryBooleanValue(change, "after_present", out afterPresent) && afterPresent &&
            TryBooleanValue(change, "before", out beforeValue) && !beforeValue &&
            TryBooleanValue(change, "after", out afterValue) && afterValue &&
            !snapshotModifiedBefore && snapshotModifiedAfter && dirtyChangedByRead;
    }

    private static bool TryBooleanValue(Dictionary<string, object> dictionary, string key, out bool value)
    {
        value = false;
        object raw;
        if (dictionary == null || !dictionary.TryGetValue(key, out raw) || !(raw is bool)) return false;
        value = (bool)raw;
        return true;
    }

    private static bool SameGeometryAndConstraints(Dictionary<string, object> left, Dictionary<string, object> right)
    {
        if (left == null || right == null) return false;
        return SameValue(left.ContainsKey("native_sketches") ? left["native_sketches"] : null,
            right.ContainsKey("native_sketches") ? right["native_sketches"] : null);
    }

    private static Dictionary<string, object> PreSaveConsistencyProjection(Dictionary<string, object> snapshot)
    {
        if (snapshot == null) return null;
        List<object> sketches = snapshot.ContainsKey("native_sketches") ? snapshot["native_sketches"] as List<object> : null;
        if (sketches == null || sketches.Count != 1) return null;
        Dictionary<string, object> sketch = sketches[0] as Dictionary<string, object>;
        if (sketch == null) return null;
        Dictionary<string, object> geometryByTag = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, string> tagsByJournalId = new Dictionary<string, string>(StringComparer.Ordinal);
        List<object> geometryRows = sketch.ContainsKey("geometry") ? sketch["geometry"] as List<object> : null;
        List<object> constraintRows = sketch.ContainsKey("constraints") ? sketch["constraints"] as List<object> : null;
        if (geometryRows == null || constraintRows == null) return null;
        foreach (object item in geometryRows)
        {
            Dictionary<string, object> geometry = item as Dictionary<string, object>;
            if (geometry == null) return null;
            string tag = StringValue(geometry, "tag");
            string journalId = StringValue(geometry, "journal_id");
            if (String.IsNullOrWhiteSpace(tag) || String.IsNullOrWhiteSpace(journalId) ||
                geometryByTag.ContainsKey(tag) || tagsByJournalId.ContainsKey(journalId)) return null;
            Dictionary<string, object> projected = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> field in geometry)
                if (field.Key != "journal_id") projected[field.Key] = field.Value;
            geometryByTag.Add(tag, projected);
            tagsByJournalId.Add(journalId, tag);
        }
        List<object> constraints = new List<object>();
        foreach (object item in constraintRows)
        {
            Dictionary<string, object> constraint = item as Dictionary<string, object>;
            if (constraint == null) return null;
            Dictionary<string, object> projected = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> field in constraint)
            {
                if (field.Key == "journal_id") continue;
                if (field.Key != "geometry_refs") { projected[field.Key] = field.Value; continue; }
                List<string> refs = new List<string>();
                System.Collections.IList operands = field.Value as System.Collections.IList;
                if (operands == null) return null;
                foreach (object operand in operands)
                {
                    string value = operand as string;
                    if (value == null) return null;
                    int separator = value.IndexOf('|');
                    string id = separator < 0 ? value : value.Substring(0, separator);
                    string tag;
                    refs.Add(tagsByJournalId.TryGetValue(id, out tag)
                        ? "tag:" + tag + (separator < 0 ? "" : value.Substring(separator)) : value);
                }
                refs.Sort(StringComparer.Ordinal);
                projected[field.Key] = refs;
            }
            constraints.Add(projected);
        }
        constraints.Sort(delegate(object left, object right) {
            return StringComparer.Ordinal.Compare(M2Json.CanonicalText(left), M2Json.CanonicalText(right));
        });
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "sketch_id", sketch["sketch_id"] }, { "curve_count", sketch["curve_count"] },
            { "object_count", sketch["object_count"] }, { "geometry_by_tag", geometryByTag },
            { "constraints", constraints }
        };
    }

    private static bool SameNativeGeometryAndConstraints(Dictionary<string, object> before, Dictionary<string, object> after)
    {
        Dictionary<string, object> left = NativeGeometryConstraintProjection(before);
        Dictionary<string, object> right = NativeGeometryConstraintProjection(after);
        return left != null && right != null &&
            String.Equals(M2Json.CanonicalText(left), M2Json.CanonicalText(right), StringComparison.Ordinal);
    }

    private static Dictionary<string, object> NativeGeometryConstraintProjection(Dictionary<string, object> snapshot)
    {
        if (snapshot == null) return null;
        List<object> sketches = snapshot.ContainsKey("native_sketches") ? snapshot["native_sketches"] as List<object> : null;
        if (sketches == null || sketches.Count != 1) return null;
        Dictionary<string, object> sketch = sketches[0] as Dictionary<string, object>;
        if (sketch == null) return null;
        List<object> geometry = sketch.ContainsKey("geometry") ? sketch["geometry"] as List<object> : null;
        List<object> constraints = sketch.ContainsKey("constraints") ? sketch["constraints"] as List<object> : null;
        List<object> geometryProjection = new List<object>();
        if (geometry != null) foreach (object item in geometry)
        {
            Dictionary<string, object> row = item as Dictionary<string, object>;
            if (row == null) return null;
            Dictionary<string, object> projected = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> field in row)
                if (!String.Equals(field.Key, "tag", StringComparison.Ordinal)) projected[field.Key] = field.Value;
            geometryProjection.Add(projected);
        }
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "sketch_id", sketch.ContainsKey("sketch_id") ? sketch["sketch_id"] : null },
            { "geometry", geometryProjection }, { "constraints", constraints ?? new List<object>() }
        };
    }

    private static bool SameModelIdRecord(Dictionary<string, object> left, Dictionary<string, object> right, bool includeModified)
    {
        if (left == null || right == null) return false;
        foreach (string key in new string[] { "features", "sketches", "bodies" })
            if (!SameValue(left[key], right[key])) return false;
        return !includeModified || SameValue(left["is_modified"], right["is_modified"]);
    }

    private static Dictionary<string, object> ModelIdRecord(Part part)
    {
        List<string> features = new List<string>();
        foreach (Feature feature in part.Features.GetFeatures()) if (feature != null) features.Add(feature.JournalIdentifier);
        List<string> sketches = new List<string>();
        foreach (Sketch sketch in part.Sketches.ToArray()) if (sketch != null) sketches.Add(sketch.JournalIdentifier);
        List<string> bodies = new List<string>();
        foreach (Body body in part.Bodies.ToArray()) if (body != null) bodies.Add(body.JournalIdentifier);
        features.Sort(StringComparer.Ordinal); sketches.Sort(StringComparer.Ordinal); bodies.Sort(StringComparer.Ordinal);
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "features", features }, { "sketches", sketches }, { "bodies", bodies }, { "is_modified", part.IsModified }
        };
    }

    private static List<object> SnapshotList(Dictionary<string, object> snapshot, string key)
    {
        if (snapshot == null) return null;
        List<object> sketches = snapshot.ContainsKey("native_sketches") ? snapshot["native_sketches"] as List<object> : null;
        if (sketches == null || sketches.Count == 0) return null;
        Dictionary<string, object> sketch = sketches[0] as Dictionary<string, object>;
        return sketch != null && sketch.ContainsKey(key) ? sketch[key] as List<object> : null;
    }

    private static Dictionary<string, object> NativeSnapshotFromOriginalRecord(object value)
    {
        return value as Dictionary<string, object>;
    }

    private static void PointNear(Point3d actual, Point3d expected, string message)
    {
        Require(PointDistance(actual, expected) <= PositionToleranceMm, message + " Expected " + Point(expected) + ", got " + Point(actual) + ".");
    }

    private static double AngleBetween(Point3d left, Point3d right)
    {
        double[] a = Normalize3(new double[] { left.X, left.Y, left.Z });
        double[] b = Normalize3(new double[] { right.X, right.Y, right.Z });
        return Math.Atan2(VectorDistance(Cross3(a, b), new double[] { 0, 0, 0 }), Math.Max(-1.0, Math.Min(1.0, Dot3(a, b))));
    }

    private static Frame BuildFrame(double[] origin, double[] normal, double[] requestedX)
    {
        double[] n = Normalize3(normal);
        double projection = Dot3(requestedX, n);
        double[] projected = new double[] { requestedX[0] - projection * n[0], requestedX[1] - projection * n[1], requestedX[2] - projection * n[2] };
        double[] x = Normalize3(projected);
        double[] y = Normalize3(Cross3(n, x));
        return new Frame { Origin = Copy(origin), Normal = n, XAxis = x, YAxis = y };
    }

    private static Point3d WorldPoint(Frame frame, double x, double y)
    {
        return new Point3d(frame.Origin[0] + x * frame.XAxis[0] + y * frame.YAxis[0],
            frame.Origin[1] + x * frame.XAxis[1] + y * frame.YAxis[1],
            frame.Origin[2] + x * frame.XAxis[2] + y * frame.YAxis[2]);
    }

    private static double[] Normalize(double[] value)
    {
        double length = Math.Sqrt(Dot2(value, value));
        Require(length > 1e-15 && IsFinite(length), "Cannot normalize a zero or invalid local vector.");
        return new double[] { value[0] / length, value[1] / length };
    }

    private static double[] Normalize3(double[] value)
    {
        double length = Math.Sqrt(Dot3(value, value));
        Require(length > 1e-15 && IsFinite(length), "Cannot normalize a zero or invalid 3D vector.");
        return new double[] { value[0] / length, value[1] / length, value[2] / length };
    }

    private static double Dot2(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1]; }
    private static double Cross2(double[] a, double[] b) { return a[0] * b[1] - a[1] * b[0]; }
    private static double Dot3(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    private static double[] Cross3(double[] a, double[] b)
    {
        return new double[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    }
    private static double[] Add(double[] a, double[] b) { return new double[] { a[0] + b[0], a[1] + b[1] }; }
    private static double[] Sub(double[] a, double[] b) { return new double[] { a[0] - b[0], a[1] - b[1] }; }
    private static double[] Scale(double[] a, double s) { return new double[] { a[0] * s, a[1] * s }; }
    private static double[] DoubleArray(object value)
    {
        List<object> list = value as List<object>;
        if (list == null) return value as double[];
        double[] result = new double[list.Count];
        for (int i = 0; i < result.Length; i++) result[i] = Convert.ToDouble(list[i], CultureInfo.InvariantCulture);
        return result;
    }

    private static bool SameValue(object left, object right)
    {
        return String.Equals(M2Json.CanonicalText(left), M2Json.CanonicalText(right), StringComparison.Ordinal);
    }

    private static List<Part> FindOwnedParts(Session session, string sourcePath, string savedPath)
    {
        List<Part> result = new List<Part>();
        foreach (BasePart loaded in session.Parts)
        {
            Part part = loaded as Part;
            if (part == null || String.IsNullOrWhiteSpace(part.FullPath)) continue;
            string path = Path.GetFullPath(part.FullPath);
            if (String.Equals(path, Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase) ||
                String.Equals(path, Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase)) result.Add(part);
        }
        return result;
    }

    private static Sketch FindSketch(Part part, string id)
    {
        foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null && String.Equals(sketch.JournalIdentifier, id, StringComparison.Ordinal)) return sketch;
        throw new InvalidOperationException("Native Sketch JournalIdentifier was not found: " + id);
    }

    private static string ActiveSketchId(Session session)
    {
        Sketch sketch = session == null ? null : session.ActiveSketch;
        return sketch == null ? null : sketch.JournalIdentifier;
    }

    private static void SavePart(Part part, string description)
    {
        PartSaveStatus status = part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
        try
        {
            Require(status != null && status.NumberUnsavedParts == 0 && status.NumberUnsavedObjects == 0,
                "Could not save " + description + " cleanly.");
        }
        finally { if (status != null) status.Dispose(); }
    }

    private static void RunCase(string reportPath, Dictionary<string, object> evidence,
        List<object> cases, List<object> defects, string name, string objective,
        string[] reproductionSteps, string passCriterion, CaseBody body)
    {
        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "name", name }, { "objective", objective }, { "status", "RUNNING" },
            { "reproduction_steps", reproductionSteps }, { "pass_criterion", passCriterion },
            { "started_utc", UtcNow() }
        };
        cases.Add(row);
        evidence["current_case"] = name;
        Persist(reportPath, evidence);
        try
        {
            body(row);
            row["status"] = "PASSED";
            row["finished_utc"] = UtcNow();
            Persist(reportPath, evidence);
        }
        catch (Exception ex)
        {
            row["status"] = "FAILED";
            row["observed_exception"] = ex.ToString();
            row["finished_utc"] = UtcNow();
            row["retest_criterion"] = "After fixing the recorded defect, rebuild and rerun all F07 cases on a new unique model path; retain this JSON and model.";
            defects.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                { "test_case", name }, { "reproduction_steps", reproductionSteps },
                { "observed_exception", ex.ToString() },
                { "retest_criterion", row["retest_criterion"] }
            });
            evidence["passed"] = false;
            Persist(reportPath, evidence);
            throw;
        }
    }

    private static object ExecutionRecord(M3ExecutionResult result)
    {
        if (result == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "state", result.State }, { "revision_id", result.RevisionId },
            { "result", result.Result }, { "error_code", result.ErrorCode }, { "error_message", result.ErrorMessage }
        };
    }

    private static object VerificationRecord(M3VerificationResult result)
    {
        if (result == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "verified", result.Verified }, { "revision_id", result.RevisionId },
            { "checks", result.Checks }, { "result", result.Result },
            { "error_code", result.ErrorCode }, { "error_message", result.ErrorMessage }
        };
    }

    private static object SaveCopyRecord(M2SaveCopyResult result)
    {
        if (result == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "state", result.State }, { "reopened", result.Reopened }, { "checks", result.Checks },
            { "part_path", result.PartPath }, { "error_code", result.ErrorCode }, { "error_message", result.ErrorMessage }
        };
    }

    private static bool ChecksPass(Dictionary<string, object> checks)
    {
        if (checks == null) return false;
        foreach (KeyValuePair<string, object> entry in checks)
        {
            Dictionary<string, object> check = entry.Value as Dictionary<string, object>;
            if (check == null || !String.Equals(StringValue(check, "status"), "VERIFIED", StringComparison.Ordinal)) return false;
        }
        return checks.Count > 0;
    }

    private static Dictionary<string, object> AssemblyRecord(Assembly assembly)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "full_name", assembly.FullName }, { "location", assembly.Location },
            { "version", assembly.GetName().Version == null ? null : assembly.GetName().Version.ToString() }
        };
    }

    private static string ReadRequiredString(Dictionary<string, object> dictionary, string key)
    {
        string value = StringValue(dictionary, key);
        Require(!String.IsNullOrWhiteSpace(value), "Missing required string field " + key + ".");
        return value;
    }

    private static string StringValue(Dictionary<string, object> dictionary, string key)
    {
        object value;
        if (dictionary == null || !dictionary.TryGetValue(key, out value) || value == null) return null;
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static double PointDistance(Point3d a, Point3d b)
    {
        double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static double VectorDistance(double[] a, double[] b)
    {
        Require(a != null && b != null && a.Length == b.Length, "Vector dimensions differ.");
        double sum = 0.0;
        for (int i = 0; i < a.Length; i++) { double delta = a[i] - b[i]; sum += delta * delta; }
        return Math.Sqrt(sum);
    }

    private static double VectorDistance(double[] a, double[] b, int count)
    {
        double sum = 0.0;
        for (int i = 0; i < count; i++) { double delta = a[i] - b[i]; sum += delta * delta; }
        return Math.Sqrt(sum);
    }

    private static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }

    private static bool RevisionNotLower(string before, string after)
    {
        if (String.Equals(before, after, StringComparison.Ordinal)) return true;
        long beforeNumber;
        long afterNumber;
        return Int64.TryParse(before, NumberStyles.Integer, CultureInfo.InvariantCulture, out beforeNumber) &&
            Int64.TryParse(after, NumberStyles.Integer, CultureInfo.InvariantCulture, out afterNumber) &&
            afterNumber > beforeNumber;
    }

    private static double[] Copy(double[] values) { double[] result = new double[values.Length]; Array.Copy(values, result, values.Length); return result; }
    private static Point3d Sub(Point3d a, Point3d b) { return new Point3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
    private static Point3d Point3dFromArray(double[] point) { return new Point3d(point[0], point[1], point.Length > 2 ? point[2] : 0.0); }
    private static object Point(Point3d point) { return new double[] { point.X, point.Y, point.Z }; }
    private static object Point(double[] point) { return new double[] { point[0], point[1], point.Length > 2 ? point[2] : 0.0 }; }

    private static string UtcNow() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }

    private static void RequireNewPath(string path, string description)
    {
        Require(!File.Exists(path) && !Directory.Exists(path), "The unique " + description + " path already exists; refusing to overwrite it.");
    }

    private static void Persist(string path, Dictionary<string, object> evidence)
    {
        File.WriteAllText(path, M2Json.CanonicalText(evidence), new System.Text.UTF8Encoding(false));
    }

    private static void AddRestoreError(Dictionary<string, object> evidence, Exception error)
    {
        List<object> errors;
        if (!evidence.ContainsKey("restore_errors")) { errors = new List<object>(); evidence["restore_errors"] = errors; }
        else errors = evidence["restore_errors"] as List<object>;
        if (errors != null) errors.Add(error.ToString());
        evidence["passed"] = false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
