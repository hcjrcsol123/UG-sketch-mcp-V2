// F09 native sketch pattern acceptance.
// Build offline; run only in a designated NX2506 session through RunF09Acceptance.py.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

public static class F09Acceptance
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    private const double PositionToleranceMm = 1.0e-6;
    private const double AngleToleranceRad = 1.0e-7;
    private const double VectorTolerance = 1.0e-7;
    private const double Pi = Math.PI;
    private const double TwoPi = 2.0 * Math.PI;

    private sealed class Frame
    {
        internal double[] Origin;
        internal double[] Normal;
        internal double[] XAxis;
        internal double[] YAxis;
    }

    private sealed class Fixture
    {
        internal string Name;
        internal string SketchId;
        internal Frame Frame;
        internal Part Part;
        internal readonly Dictionary<string, Curve> Curves = new Dictionary<string, Curve>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> CurveIds = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private sealed class CurveModel
    {
        internal string Tag;
        internal string JournalId;
        internal string Type;
        internal bool IsArc;
        internal bool IsFullCircle;
        internal double[] Start;
        internal double[] End;
        internal double[] Center;
        internal double[] AxisX;
        internal double[] AxisY;
        internal double Radius;
        internal double StartAngle;
        internal double Sweep;
        internal double LengthMm;
    }

    private sealed class ExpectedInstance
    {
        internal string SourceCurveId;
        internal int PositionIndex;
        internal int PositionXIndex;
        internal int PositionYIndex;
        internal CurveModel Geometry;
    }

    private sealed class PatternTransform
    {
        internal double M11 = 1.0;
        internal double M12;
        internal double M21;
        internal double M22 = 1.0;
        internal double Tx;
        internal double Ty;
        internal string Kind;
        internal int PositionIndex;
        internal int PositionXIndex;
        internal int PositionYIndex;
    }

    private delegate void CaseBody(Dictionary<string, object> evidence);
    private delegate Dictionary<string, object> MaterializationRead();

    public static void Main(string[] args)
    {
        string runId = Guid.NewGuid().ToString("N");
        string root = Path.Combine(ProjectRoot, "workspace", "v2");
        string modelDirectory = Path.Combine(root, "models");
        string evidenceDirectory = Path.Combine(root, "evidence");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        string sourcePath = Path.Combine(modelDirectory, "F09-acceptance-" + runId + ".prt");
        string roundTripPath = Path.Combine(modelDirectory, "F09-acceptance-" + runId + "-roundtrip.prt");
        string reportPath = Path.Combine(evidenceDirectory, "F09-acceptance-" + runId + ".json");
        RequireNewPath(sourcePath, "isolated model");
        RequireNewPath(roundTripPath, "round-trip model");
        RequireNewPath(reportPath, "report");

        List<object> cases = new List<object>();
        List<object> fixtures = new List<object>();
        List<object> defects = new List<object>();
        Dictionary<string, object> report = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test", "F09 native linear, rectangular, circular, and mirror sketch pattern subsystem validation" },
            { "run_id", runId }, { "started_utc", UtcNow() }, { "passed", false },
            { "cases", cases }, { "fixture_setup", fixtures }, { "defects", defects },
            { "source_model_path", sourcePath }, { "roundtrip_model_path", roundTripPath }, { "report_path", reportPath },
            { "sample_size", 1 }, { "units", "millimeter" },
            { "position_tolerance_mm", PositionToleranceMm }, { "angle_tolerance_rad", AngleToleranceRad },
            { "vector_tolerance", VectorTolerance },
            { "instrumentation", "Direct NXOpen/UF native geometry readback and production M2OpenModelAdapter results." },
            { "instrument_calibration", "Not applicable: no physical instrument is used. CAD numerical comparison tolerances are not metrology uncertainty." },
            { "scope_limitations", new string[] {
                "One NX2506 configuration run, sample_size=1; no reliability or population inference.",
                "No manufacturing accuracy, regulatory compliance, or other NX release claim is made.",
                "Protocol/CLR fake, analytic geometry, native Journal, and installed MCP smoke are separate evidence layers.",
                "F01-F08 acceptance suites are not executed by this entry." } },
            { "traceability", "docs/F09.md; tests/nx/v2/F09Acceptance.md F09-00 through F09-14" },
            { "test_layer", "Subsystem validation through production M2OpenModelAdapter with an independent direct NXOpen/UF numeric oracle." },
            { "prior_f09_software_failure", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "report_path", "workspace/v2/evidence/f09-software-0a746f68a6474dfda1e196c69c1c37d5/summary.json" },
                    { "companion_report_path", "workspace/v2/evidence/f09-software-0a746f68a6474dfda1e196c69c1c37d5/unittest.txt" },
                    { "first_run_result", "29 tests; 4 assertion failures; no NX was run." },
                    { "root_causes", new string[] {
                        "The null-verify follow-up correctly returned OPERATION_OUTCOME_UNKNOWN; the test allowed only CLIENT_OUTCOME_UNKNOWN.",
                        "Adapter preflight failures are terminal operation records with result.state=FAILED and a nested error, not top-level error.",
                        "A stale-plan test baseline included the intentional revision-advance Sketch mutation.",
                        "A route-only MCP mock returned no state for a compound edit, correctly marked the outcome unknown, and blocked later writes." } },
                    { "corrective_action", "Corrected the terminal unknown error expectation, adapter-level FAILED-record parsing, stale-plan baseline timing, and route-only compound-edit mock. The final selected software suite passed 29/29 with no skipped tests." },
                    { "retest_report_path", "workspace/v2/evidence/f09-software-b809a4f94ba147d8b1bc847b02f69281/summary.json" },
                    { "retest_result", "29 passed; 0 failed; 0 errors; 0 skipped; NX was not started." },
                    { "retest_criterion", "Preserve the first-run report and model evidence; corrected F09 software retest must report all selected cases passing before software-layer closure." }
                } },
            { "nx_started_by_harness", false }, { "nx_restarted_by_harness", false }
        };
        Persist(reportPath, report);

        Session session = null;
        Part originalPart = null;
        Part part = null;
        M2OpenModelAdapter adapter = null;
        M2ModelBinding binding = null;
        bool acceptanceFailed = false;
        Frame tilted = BuildFrame(new double[] { 18.0, -11.0, 9.0 },
            new double[] { 1.0, 2.0, 3.0 }, new double[] { 1.0, -1.0, 0.5 });
        Frame xy = BuildFrame(new double[] { 0.0, 0.0, 0.0 },
            new double[] { 0.0, 0.0, 1.0 }, new double[] { 1.0, 0.0, 0.0 });

        try
        {
            session = Session.GetSession();
            originalPart = session.Parts.Work;
            report["original_work_part_path"] = originalPart == null ? null : originalPart.FullPath;
            report["original_work_part_modified_before"] = originalPart != null && originalPart.IsModified;
            Require(!session.IsInTaskEnvironment && session.ActiveSketch == null,
                "Acceptance requires one idle NX session with no active Sketch or task environment.");
            Require(originalPart == null || !originalPart.IsModified,
                "The original Work Part must be saved and unmodified before acceptance starts.");

            part = session.Parts.NewDisplay(sourcePath, Part.Units.Millimeters);
            Require(part != null && part.PartUnits == BasePart.Units.Millimeters,
                "NX did not create the unique isolated model in millimetres.");
            SavePart(part, "empty isolated F09 part");
            adapter = new M2OpenModelAdapter();
            binding = adapter.Takeover(sourcePath, "saved_independent_copy",
                "f09-acceptance-session-" + runId, "f09-acceptance-load-" + runId, 1);
            Dictionary<string, object> sessionInfo = adapter.GetSession(binding);
            report["session"] = sessionInfo;
            report["assemblies"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "bridge", AssemblyRecord(typeof(M2Json).Assembly) },
                { "adapter", AssemblyRecord(typeof(M2OpenModelAdapter).Assembly) },
                { "nxopen", AssemblyRecord(typeof(Session).Assembly) },
                { "acceptance", AssemblyRecord(Assembly.GetExecutingAssembly()) }
            };
            string nxVersion = FirstNonEmpty(GetString(sessionInfo, "nx_build"),
                GetString(sessionInfo, "full_release_number"), GetString(sessionInfo, "release_number"));
            report["nx_configuration"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "required_release", "NX2506" }, { "recorded_release", nxVersion },
                { "release_number", GetString(sessionInfo, "release_number") },
                { "full_release_number", GetString(sessionInfo, "full_release_number") },
                { "nxopen_assembly_version", GetString(sessionInfo, "nxopen_assembly_version") },
                { "ugii_version", GetString(sessionInfo, "ugii_version") },
                { "part_units", part.PartUnits.ToString() },
                { "part_path", part.FullPath }, { "binding_revision_id", binding.RevisionId }
            };
            Require(IsNx2506(nxVersion) || IsNx2506(GetString(sessionInfo, "release_number")) ||
                IsNx2506(GetString(sessionInfo, "full_release_number")),
                "This native acceptance is scoped to NX2506; the active NX release was not identified as NX2506.");
            Require(part.PartUnits == BasePart.Units.Millimeters, "The isolated F09 part is not in millimetres.");

            // Setup only: a native solid body and a separately constrained Sketch are
            // captured as preservation controls. No F01-F08 acceptance suite is called.
            Fixture bodyFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "body-preservation-circle", xy,
                new List<M3SketchGeometryRequest> { Circle("body-circle", 120.0, 120.0, 12.0) });
            M3PlanSnapshot extrudePlan = adapter.PrepareExtrude(binding, new M3ExtrudeRequest
            {
                RevisionId = binding.RevisionId, SketchId = bodyFixture.SketchId,
                Mode = "create", Direction = "positive", Extent = "finite", DepthMm = 2.0
            });
            M3ExecutionResult extrudeExecute = adapter.ExecutePlan(binding, extrudePlan);
            RecordFixtureOperation(fixtures, "body-preservation-extrude", extrudePlan,
                extrudeExecute, null, part, "Setup-only solid for F09 body inventory preservation.");
            Require(extrudeExecute != null && extrudeExecute.State == "APPLIED",
                "Could not establish the F09 body-preservation fixture.");
            M3VerificationResult extrudeVerify = adapter.VerifyPlan(binding, extrudePlan);
            Require(extrudeVerify != null && extrudeVerify.Verified && ChecksPass(extrudeVerify.Checks),
                "The setup-only solid did not pass its production verification.");
            SavePart(part, "F09 body-preservation setup");

            Fixture otherConstraintFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "non-target-constrained-sketch", xy,
                new List<M3SketchGeometryRequest> { Line("other-line", 0.0, 0.0, 8.0, 0.0) });
            AddHorizontalDrivingDimension(session, part, otherConstraintFixture,
                otherConstraintFixture.Curves["other-line"] as Line, 8.0, fixtures);
            SavePart(part, "F09 non-target constraint setup");

            Fixture linearFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "linear-tilted-preservation", tilted,
                new List<M3SketchGeometryRequest> {
                    Line("source-line", 0.0, 0.0, 10.0, 0.0),
                    Line("connected-nontarget-line", 10.0, 0.0, 10.0, 8.0),
                    Line("reference-line", -5.0, 24.0, -1.0, 24.0)
                });
            AddHorizontalDrivingDimension(session, part, linearFixture,
                linearFixture.Curves["source-line"] as Line, 10.0, fixtures);
            AddTargetCoincidentAndReference(session, linearFixture, fixtures);
            SavePart(part, "F09 tilted constrained/reference fixture");

            RunCase(reportPath, report, cases, defects, "F09-00_strict_snapshot_read_side_effect_control",
                "Record the declared production InspectPart/InspectSketch read effects, then prove direct native snapshots are repeatable on the resulting dirty baseline.",
                new string[] { "Save the independent body, constrained Sketch, reference curve, and target Sketch fixture before explicit inspection.",
                    "Record every InspectPart/InspectSketch read_effects entry and the actual Part.IsModified transition; do not save after inspection.",
                    "Take two strict native snapshots after materialization and use the resulting state as the next Prepare baseline." },
                "The reported read_effects exactly match observed IsModified entry/exit state; two strict snapshots are identical and preserve the actual post-read dirty state; subsequent Prepare checks the complete native state against that baseline.",
                delegate(Dictionary<string, object> row)
                {
                    RunSnapshotReadEffectControl(adapter, binding, part, row);
                });

            RunCase(reportPath, report, cases, defects, "F09-00b_body_measure_read_side_effect_control",
                "Check the production body measurement helpers and semantic Capture against the already-materialized dirty baseline without a pattern Prepare.",
                new string[] { "Save the setup-only solid before explicit inspection; record InspectPart/InspectSketch read_effects and leave the resulting dirty state intact.",
                    "Capture direct native and dirty-state baselines, invoke private MeasureBody and CaptureM3BodyMeasures via reflection, then inspect semantic Capture and its declared read_effects." },
                "Each read helper returns evidence, preserves all direct native content and the actual dirty baseline, and semantic Capture reports before/after dirty state consistently; no save or dirty-state clearing follows materialization.",
                delegate(Dictionary<string, object> row)
                {
                    RunBodyMeasureReadEffectControl(adapter, binding, part, row);
                });

            RunCase(reportPath, report, cases, defects, "F09-01_linear_tilted_with_preservation",
                "Verify a 3-position linear pattern on a tilted Sketch while preserving source constraints, reference state, non-target geometry, history, solver state and body inventory.",
                new string[] { "Use the isolated NX2506 millimetre part and tilted frame recorded in the fixture setup.",
                    "Prepare direction_xy=[1,0], spacing_mm=18, instance_count=3 for the explicitly tagged source Line.",
                    "Require prepare to leave the direct all-part native snapshot and binding revision unchanged.",
                    "Execute once, call production verify once, then match each expected copy to a distinct direct NXOpen curve Tag." },
                "Two active output Lines at local x offsets 18 mm and 36 mm match independent endpoint geometry; source, constraints, reference status, frame, non-target state, history and bodies are preserved.",
                delegate(Dictionary<string, object> row)
                {
                    M3SketchPatternRequest request = LinearRequest(binding, linearFixture,
                        new string[] { "source-line" }, new double[] { 1.0, 0.0 }, 18.0, 3);
                    ApplyAndCheckPattern(adapter, binding, part, linearFixture, request, row);
                });

            Fixture rectangularFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "rectangular-mixed-line-arc", xy,
                new List<M3SketchGeometryRequest> {
                    Line("rect-line", 1.0, 2.0, 5.0, 2.0),
                    Arc("rect-arc", 10.0, 4.0, 2.0, 25.0, 105.0)
                });
            RunCase(reportPath, report, cases, defects, "F09-02_rectangular_mixed_line_arc",
                "Verify a 2 by 3 rectangular pattern of a Line and a partial Arc with orthogonal sketch-local directions.",
                new string[] { "Prepare direction_x_xy=[0,1], spacing_x_mm=3.5, count_x=2 and direction_y_xy=[-1,0], spacing_y_mm=5, count_y=3.",
                    "Execute once; read every new curve directly from target Sketch geometry and match six position groups one-to-one." },
                "All 10 total instances (2 sources times 6 positions, including originals) match independent coordinates; no output Tag is reused and each Arc retains its intended 105 degree sector.",
                delegate(Dictionary<string, object> row)
                {
                    M3SketchPatternRequest request = RectangularRequest(binding, rectangularFixture,
                        new string[] { "rect-line", "rect-arc" }, new double[] { 0.0, 1.0 },
                        new double[] { -1.0, 0.0 }, 3.5, 5.0, 2, 3);
                    ApplyAndCheckPattern(adapter, binding, part, rectangularFixture, request, row);
                });

            Frame circularFrame = BuildFrame(new double[] { -7.0, 13.0, 4.0 },
                new double[] { 2.0, -1.0, 2.0 }, new double[] { 0.5, 1.0, -0.5 });
            Fixture circularFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "circular-mixed-line-arc-tilted", circularFrame,
                new List<M3SketchGeometryRequest> {
                    Line("circular-line", 7.0, 1.0, 10.0, 1.0),
                    Arc("circular-arc", 13.0, 4.0, 1.75, 15.0, 125.0)
                });
            RunCase(reportPath, report, cases, defects, "F09-03_circular_mixed_line_arc_tilted",
                "Verify a four-position circular pattern on a tilted Sketch about a non-origin sketch-local center.",
                new string[] { "Prepare center_xy=[1,-2], angle_step_deg=70, instance_count=4 for one Line and one partial Arc.",
                    "Execute once and independently calculate each local rotation about the requested center.",
                    "Compare direct endpoints, arc center/radius, interior point, extent, tangent and orientation for all new Tags." },
                "All six clones match four position groups from the independent transform, including source position; no output Tag is reused and the requested Arc sector is not replaced by its complement.",
                delegate(Dictionary<string, object> row)
                {
                    M3SketchPatternRequest request = CircularRequest(binding, circularFixture,
                        new string[] { "circular-line", "circular-arc" }, new double[] { 1.0, -2.0 }, 70.0, 4);
                    ApplyAndCheckPattern(adapter, binding, part, circularFixture, request, row);
                });

            Fixture circularArcDomainFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "circular-negative-step-cross-zero-minor-and-major-arcs", circularFrame,
                new List<M3SketchGeometryRequest> {
                    Line("domain-line", -20.0, 10.0, -16.0, 10.0),
                    Arc("cross-zero-minor", 4.0, -2.0, 1.5, 330.0, 70.0),
                    Arc("major-arc", 10.0, 5.0, 2.0, 25.0, 235.0)
                });
            RunCase(reportPath, report, cases, defects,
                "F09-03b_circular_negative_step_cross_zero_minor_and_major_arcs",
                "Verify negative circular rotation of mixed native Line/Arc geometry, including a zero-crossing minor Arc and a major Arc.",
                new string[] { "Use the tilted circular frame and native source geometry recorded in the fixture.",
                    "Require the first Arc domain to be 330° through 400° (70° minor, crossing parameter zero) and the second to be 25° through 260° (235° major).",
                    "Prepare center_xy=[1,-2], angle_step_deg=-90, instance_count=3 for the Line and both Arcs.",
                    "Calculate expected curves from direct NXOpen source Arc centers, native plane bases, and measured angular domains; do not use production preview geometry.",
                    "Check the expected local Arc centers at positions 0, 1, 2 against [4,-2]/[1,-5]/[-2,-2] and [10,5]/[8,-11]/[-8,-9]." },
                "All six clone curves match one-to-one. Both Arc domains, centers, radii, endpoints, interior sectors, tangents, plane orientation and lengths match the independent source-basis oracle; the six explicit center coordinates meet 1e-6 mm.",
                delegate(Dictionary<string, object> row)
                {
                    M3SketchPatternRequest request = CircularRequest(binding, circularArcDomainFixture,
                        new string[] { "domain-line", "cross-zero-minor", "major-arc" },
                        new double[] { 1.0, -2.0 }, -90.0, 3);

                    CurveModel minorSource = CaptureCurve(circularArcDomainFixture.Curves["cross-zero-minor"],
                        circularArcDomainFixture.Frame);
                    CurveModel majorSource = CaptureCurve(circularArcDomainFixture.Curves["major-arc"],
                        circularArcDomainFixture.Frame);
                    double normalizedMinorStart = minorSource.StartAngle % TwoPi;
                    if (normalizedMinorStart < 0.0) normalizedMinorStart += TwoPi;
                    Require(Math.Abs(minorSource.Sweep - 70.0 * Pi / 180.0) <= AngleToleranceRad &&
                        normalizedMinorStart + minorSource.Sweep > TwoPi,
                        "Direct native minor Arc source does not cross parameter zero with the requested 70 degree domain.");
                    Require(Math.Abs(majorSource.Sweep - 235.0 * Pi / 180.0) <= AngleToleranceRad &&
                        Math.Abs(majorSource.Sweep) > Pi && Math.Abs(majorSource.Sweep) < TwoPi,
                        "Direct native major Arc source does not preserve the requested 235 degree domain.");

                    Dictionary<string, double[][]> expectedArcCenters = new Dictionary<string, double[][]>(StringComparer.Ordinal)
                    {
                        { "cross-zero-minor", new double[][] {
                            new double[] { 4.0, -2.0 }, new double[] { 1.0, -5.0 }, new double[] { -2.0, -2.0 } } },
                        { "major-arc", new double[][] {
                            new double[] { 10.0, 5.0 }, new double[] { 8.0, -11.0 }, new double[] { -8.0, -9.0 } } }
                    };
                    List<ExpectedInstance> independent = BuildExpectedInstances(circularArcDomainFixture, request);
                    List<object> centerChecks = new List<object>();
                    foreach (KeyValuePair<string, double[][]> item in expectedArcCenters)
                    {
                        string sourceId = ResolveGeometryId(circularArcDomainFixture, item.Key);
                        for (int position = 0; position < item.Value.Length; position++)
                        {
                            ExpectedInstance expected = null;
                            foreach (ExpectedInstance candidate in independent)
                                if (String.Equals(candidate.SourceCurveId, sourceId, StringComparison.Ordinal) &&
                                    candidate.PositionIndex == position)
                                {
                                    expected = candidate;
                                    break;
                                }
                            Require(expected != null && expected.Geometry.IsArc &&
                                Distance2(expected.Geometry.Center, item.Value[position]) <= PositionToleranceMm,
                                "Independent source-basis oracle disagrees with the explicit numeric Arc-center expectation.");
                            centerChecks.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                { "source_geometry_id", item.Key }, { "position_index", position },
                                { "source_native_center_xy_mm", item.Key == "cross-zero-minor" ? minorSource.Center : majorSource.Center },
                                { "source_native_axis_x_xy", item.Key == "cross-zero-minor" ? minorSource.AxisX : majorSource.AxisX },
                                { "source_native_axis_y_xy", item.Key == "cross-zero-minor" ? minorSource.AxisY : majorSource.AxisY },
                                { "source_native_start_angle_rad", item.Key == "cross-zero-minor" ? minorSource.StartAngle : majorSource.StartAngle },
                                { "source_native_sweep_rad", item.Key == "cross-zero-minor" ? minorSource.Sweep : majorSource.Sweep },
                                { "expected_center_xy_mm", expected.Geometry.Center },
                                { "explicit_center_xy_mm", item.Value[position] },
                                { "residual_mm", Distance2(expected.Geometry.Center, item.Value[position]) }
                            });
                        }
                    }
                    row["independent_explicit_arc_center_checks"] = centerChecks;
                    ApplyAndCheckPattern(adapter, binding, part, circularArcDomainFixture, request, row);
                });

            Fixture stepBoundsFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "circular-step-domain-bounds", xy,
                new List<M3SketchGeometryRequest> { Line("step-bounds-line", 4.0, 0.0, 8.0, 0.0) });
            RunCase(reportPath, report, cases, defects, "F09-04_circular_signed_step_domain_rejections",
                "Reject circular step angles outside the published open interval 0 < abs(angle_step_deg) < 360.",
                new string[] { "Use the identified step-bounds-line in a fresh valid XY Sketch.",
                    "Prepare circular patterns for +360, -360, +450 and -450 degrees, one request per fresh operation ID.",
                    "Capture direct all-part native snapshots and binding revisions before and after each rejected Prepare." },
                "Every request returns INVALID_REQUEST with no plan, no native operation call, and exact direct snapshot, dirty-state and revision preservation.",
                delegate(Dictionary<string, object> row)
                {
                    List<object> outcomes = new List<object>();
                    row["rejections"] = outcomes;
                    foreach (double angle in new double[] { 360.0, -360.0, 450.0, -450.0 })
                    {
                        M3SketchPatternRequest request = CircularRequest(binding, stepBoundsFixture,
                            new string[] { "step-bounds-line" }, new double[] { 0.0, 0.0 }, angle, 2);
                        ExpectPrepareReject(adapter, binding, part, outcomes,
                            "circular_angle_step_" + angle.ToString("0", CultureInfo.InvariantCulture), request, "INVALID_REQUEST");
                    }
                    row["passed"] = true;
                });

            Fixture largeAngleFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "circular-large-finite-step-boundary", xy,
                new List<M3SketchGeometryRequest> { Line("large-step-line", 14.0, 0.0, 18.0, 0.0) });
            RunCase(reportPath, report, cases, defects, "F09-05_large_finite_circular_step_rejection",
                "Reject a very large finite circular step under the published 0 < abs(step) < 360 domain.",
                new string[] { "Prepare a two-position circular pattern with angle_step_deg=1e308 and center_xy=[0,0].",
                    "Capture direct native snapshot, binding revision, dirty state and native call counters before/after Prepare." },
                "Prepare returns INVALID_REQUEST with no plan/native call; direct snapshot, dirty state and revision remain identical.",
                delegate(Dictionary<string, object> row)
                {
                    List<object> outcomes = new List<object>();
                    row["rejections"] = outcomes;
                    M3SketchPatternRequest request = CircularRequest(binding, largeAngleFixture,
                        new string[] { "large-step-line" }, new double[] { 0.0, 0.0 }, 1.0e308, 2);
                    ExpectPrepareReject(adapter, binding, part, outcomes,
                        "large_finite_circular_angle_step", request, "INVALID_REQUEST");
                    row["passed"] = true;
                });

            Frame mirrorFrame = BuildFrame(new double[] { 4.0, 5.0, -9.0 },
                new double[] { -2.0, 1.0, 3.0 }, new double[] { 2.0, 1.0, 0.0 });
            Fixture mirrorFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "mirror-native-axis-tilted", mirrorFrame,
                new List<M3SketchGeometryRequest> {
                    Line("mirror-line", 2.0, 1.0, 6.0, 1.0),
                    Arc("mirror-arc", 8.0, 3.0, 2.0, 32.0, 92.0),
                    Line("mirror-axis", 0.0, -12.0, 0.0, 12.0)
                });
                RunCase(reportPath, report, cases, defects, "F09-06_mirror_about_specified_native_line",
                "Verify reflection of a Line and partial Arc about the explicitly named native sketch Line on a tilted Sketch.",
                new string[] { "Use mirror-axis as axis_curve_id and exclude it from source_curve_ids.",
                    "Prepare and execute one mirror operation, then inspect the new target Sketch curves and all preexisting Tags directly through NXOpen/UF.",
                    "If the native builder reports a null CommitObject, accept only if direct Tag delta and independent geometry prove both reflected outputs." },
                "Two distinct active reflected curves match the exact point reflection. Arc start/end, midpoint, tangent direction, plane normal and sweep reject the complementary sector. The axis and sources remain unchanged.",
                delegate(Dictionary<string, object> row)
                {
                    M3SketchPatternRequest request = MirrorRequest(binding, mirrorFixture,
                        new string[] { "mirror-line", "mirror-arc" }, "mirror-axis");
                    ApplyAndCheckPattern(adapter, binding, part, mirrorFixture, request, row);
                });

            Fixture fullArcFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "full-circle-native-arc", xy,
                new List<M3SketchGeometryRequest> { Line("full-arc-carrier", 30.0, 30.0, 32.0, 30.0) });
            Arc fullArc = AddNativeFullCircleArc(session, part, fullArcFixture, 40.0, 0.0, 2.5);
            fullArcFixture.Curves["full-circle-arc"] = fullArc;
            fullArcFixture.CurveIds["full-circle-arc"] = fullArc.JournalIdentifier;
            SavePart(part, "F09 native full Circle-as-Arc fixture");
            RunCase(reportPath, report, cases, defects, "F09-07_full_circle_encoded_as_native_arc",
                "Verify the supported full Circle input when represented by a native NXOpen Arc.",
                new string[] { "Create a native full-turn Arc with NXOpen and add it to the isolated target Sketch; record its Tag and JournalIdentifier.",
                    "Prepare a two-position linear pattern with the full Arc as the only source.",
                    "Compare full-circle closure, center, radius, frame, tangent orientation and output Tag directly." },
                "The source full Arc remains unchanged and exactly one active, distinct native Arc is added at the requested translation; it is identified as a complete 2 pi interval.",
                delegate(Dictionary<string, object> row)
                {
                    M3SketchPatternRequest request = LinearRequest(binding, fullArcFixture,
                        new string[] { "full-circle-arc" }, new double[] { 1.0, 0.0 }, 7.0, 2);
                    ApplyAndCheckPattern(adapter, binding, part, fullArcFixture, request, row);
                });

            Fixture rejectFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "read-only-unsupported-curve-types", xy,
                new List<M3SketchGeometryRequest> {
                    Line("reject-line", 0.0, 0.0, 5.0, 0.0),
                    Ellipse("reject-ellipse", 12.0, 0.0, 5.0, 2.0),
                    Spline("reject-spline", new double[][] {
                        new double[] { 20.0, 0.0 }, new double[] { 22.0, 1.0 },
                        new double[] { 24.0, 0.0 }, new double[] { 26.0, 2.0 } }),
                    Line("reject-axis-line", 0.0, 10.0, 5.0, 10.0)
                });
            RunCase(reportPath, report, cases, defects, "F09-08_read_only_unsupported_type_and_ownership_rejections",
                "Prove that Ellipse/Spline sources, cross-Sketch ownership and invalid mirror axes reject during prepare without native mutation.",
                new string[] { "Use the explicit IDs captured for the Ellipse, Spline, target Line, and other-Sketch Line.",
                    "Submit one invalid prepare request at a time; capture direct all-part snapshots and revision before and after each call." },
                "Every invalid request throws its expected M2ModelException code, returns no plan, leaves the direct native snapshot and revision identical, and records no native pattern operation.",
                delegate(Dictionary<string, object> row)
                {
                    RunReadOnlyRejections(adapter, binding, part, rejectFixture, otherConstraintFixture, row);
                });

            RunCase(reportPath, report, cases, defects, "F09-09_read_only_numeric_bounds",
                "Verify the finite, nonzero, count and orthogonality bounds for every F09 pattern mode.",
                new string[] { "Use a valid target Line and prepare requests with zero/nonfinite directions, nonpositive/nonfinite spacing, nonorthogonal rectangular axes, invalid counts, duplicate circular positions, and a zero angle step.",
                    "Compare direct all-part native state and binding revision around each rejected prepare." },
                "Each out-of-bounds request is specifically rejected before native pattern evaluation with exact direct snapshot and revision preservation.",
                delegate(Dictionary<string, object> row)
                {
                    RunNumericRejections(adapter, binding, part, rejectFixture, row);
                });

            Fixture largeDirectionFixture = CreateFixture(adapter, binding, part, session, fixtures,
                "large-finite-linear-direction-boundary", xy,
                new List<M3SketchGeometryRequest> { Line("large-direction-line", 0.0, 0.0, 5.0, 0.0) });
            RunCase(reportPath, report, cases, defects, "F09-10_large_finite_direction_outcome",
                "Require a very large finite direction vector to be explicitly rejected read-only or normalized without overflow and verified numerically.",
                new string[] { "Use direction_xy=[1e308,1e308], spacing_mm=6 and instance_count=2 on the identified native Line.",
                    "If Prepare rejects, require INVALID_REQUEST and exact direct snapshot/revision preservation.",
                    "If Prepare returns a plan, require every expected coordinate finite, Execute once and match the direct native Line to the independent normalized-direction oracle." },
                "The boundary is either specifically rejected before native mutation or the one output Line matches the independently normalized direction at 1e-6 mm with exact output count.",
                delegate(Dictionary<string, object> row)
                {
                    RunLargeDirectionOutcome(adapter, binding, part, largeDirectionFixture, row);
                });

            RunCase(reportPath, report, cases, defects, "F09-11_static_arc_sweep_bounds",
                "Exercise the production raw Arc sweep helper at valid, wrap, full-turn and malformed numerical boundaries without changing NX state.",
                new string[] { "Invoke internal M2OpenModelAdapter.PatternArcSweep(double,double) by its exact reflection signature.",
                    "Require the raw signed quarter, zero-crossing (350°→10°), and one-turn intervals; equal endpoints, 450°, 720°, NaN and Infinity must raise INVALID_ARC_SWEEP." },
                "All valid sweep values match in radians within 1e-7; every out-of-domain/nonfinite interval is explicitly rejected.",
                delegate(Dictionary<string, object> row)
                {
                    RunArcSweepHelperBounds(row);
                });

            RunCase(reportPath, report, cases, defects, "F09-12_controlled_postcondition_injection_and_actual_Undo",
                "Exercise the real native Builder path and actual NX Undo with a controlled adapter postcondition mismatch.",
                new string[] { "Prepare a valid linear pattern on a fresh Sketch and capture the strict all-part native baseline.",
                    "After prepare only, use reflection to increment retained ExpectedOutputCurveCount by one.",
                    "Execute exactly once; record the native operation evidence, failure check, undo evidence, direct post-Undo snapshot and revision.",
                    "Do not retry or edit the part if the returned mutation state is UNKNOWN." },
                "The real native builder is called once; production returns ROLLED_BACK after actual Undo; the independent direct all-part snapshot is restored exactly. The record is labelled controlled_postcondition_injection, not a spontaneous NX kernel failure.",
                delegate(Dictionary<string, object> row)
                {
                    RunControlledUndoCase(adapter, binding, part, session, fixtures, row);
                });

            RunCase(reportPath, report, cases, defects, "F09-13_synthetic_verifier_baseline_corruption",
                "Challenge each retained Prepare baseline by corruption while leaving the live NX model and post-execution state untouched.",
                new string[] { "Use the successfully applied constrained tilted linear plan.",
                    "Replace a deep copy of one retained pre-edit baseline at a time with a contradictory empty or sentinel value.",
                    "Call public production VerifyPlan; require the corresponding check and overall result to fail; restore the exact baseline in finally.",
                    "After restoring every field, require production VerifyPlan to pass and compare a direct NXOpen snapshot before/after the challenge." },
                "Corrupting source geometry, constraints, frames, reference status, body measures, expressions or history baselines yields the corresponding MISMATCH; all fields are restored and the native snapshot is unchanged.",
                delegate(Dictionary<string, object> row)
                {
                    RunSyntheticBaselineCorruption(adapter, binding, part, linearFixture, row);
                }, true);

            RunCase(reportPath, report, cases, defects, "F09-14_save_copy_reopen_direct_projection",
                "Save a copy, reopen it, and compare independent native geometry/state projections including all pattern modes.",
                new string[] { "Capture the direct all-part native projection after all completed F09 cases.",
                    "Call production M2OpenModelAdapter.SaveCopy to a new unique -roundtrip.prt path with reopen=true.",
                    "Enumerate the reopened NXOpen Sketches, Curves, constraints, frames, history and bodies directly; compare numeric projections to the pre-save copy." },
                "Save reports SAVED and reopened=true; all production round-trip checks are VERIFIED; independent pre/post projections match at the stated CAD tolerances; original path is not overwritten.",
                delegate(Dictionary<string, object> row)
                {
                    RunSaveCopyRoundTrip(adapter, binding, session, part, roundTripPath, row);
                });

            report["passed"] = defects.Count == 0;
            report["completed_utc"] = UtcNow();
            report["final_work_part_path"] = session.Parts.Work == null ? null : session.Parts.Work.FullPath;
            report["original_work_part_modified_after"] = originalPart != null && originalPart.IsModified;
            report["acceptance_complete"] = true;
            Persist(reportPath, report);
        }
        catch (Exception ex)
        {
            acceptanceFailed = true;
            report["passed"] = false;
            report["acceptance_complete"] = false;
            report["fatal_error"] = ex.ToString();
            report["failed_utc"] = UtcNow();
            if (part != null)
            {
                try
                {
                    report["failure_state_native_snapshot"] = StrictSnapshot(part);
                    report["failure_state_model_retained"] = part.FullPath;
                    SavePart(part, "F09 failure-state evidence");
                }
                catch (Exception saveError) { report["failure_state_save_error"] = saveError.ToString(); }
            }
            AddDefect(report, defects, "acceptance-level failure", ex,
                new string[] { "Use the recorded unique F09 source model and exact failed case request.",
                    "Rebuild the current production adapter and F09 acceptance assembly offline.",
                    "Rerun only after a fresh NX2506 session is idle and a new unique source/report path is selected." },
                "Correct the recorded defect; rerun the failed F09 case and its required preservation and save/reopen checks on a fresh unique part. Keep this report and model.");
            Persist(reportPath, report);
        }
        finally
        {
            if (adapter != null && binding != null)
            {
                try { adapter.Release(binding); }
                catch (Exception ex) { report["adapter_release_error"] = ex.ToString(); }
            }
            if (report.ContainsKey("acceptance_complete"))
            {
                report["finished_utc"] = UtcNow();
                try { Persist(reportPath, report); } catch (Exception) { }
            }
        }
        if (acceptanceFailed || !Object.Equals(report["passed"], true))
            throw new InvalidOperationException("F09 native acceptance failed. Review " + reportPath);
    }

    private static void ApplyAndCheckPattern(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, Fixture fixture, M3SketchPatternRequest request, Dictionary<string, object> row)
    {
        ApplyAndCheckPattern(adapter, binding, part, fixture, request, row, null);
    }

    private static void ApplyAndCheckPattern(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, Fixture fixture, M3SketchPatternRequest request, Dictionary<string, object> row,
        M3PlanSnapshot suppliedPlan)
    {
        if (suppliedPlan == null)
        {
            row["materialization"] = Materialize(adapter, binding, part);
            request.RevisionId = binding.RevisionId;
        }
        else
        {
            Require(String.Equals(suppliedPlan.RevisionId, binding.RevisionId, StringComparison.Ordinal),
                "Supplied F09 plan revision does not match the current binding after Prepare.");
            request.RevisionId = suppliedPlan.RevisionId;
        }
        row["request"] = PatternRequestRecord(request);
        row["fixture_frame"] = FrameRecord(fixture.Frame);
        row["source_curve_ids"] = new List<string>(request.SourceCurveIds);
        Dictionary<string, object> before = StrictSnapshot(part);
        List<Curve> beforeCurves = CurvesForSketch(fixture);
        HashSet<string> beforeTags = CurveTagSet(beforeCurves);
        string revisionBefore = binding.RevisionId;
        row["native_before"] = before;
        List<string> beforeTagEvidence = new List<string>(beforeTags);
        beforeTagEvidence.Sort(StringComparer.Ordinal);
        row["target_sketch_curve_tags_before"] = beforeTagEvidence;
        row["revision_before"] = revisionBefore;
        bool dirtyBeforeSourceCapture = part.IsModified;
        row["source_geometry_before"] = SourceGeometryRecords(fixture, request.SourceCurveIds);
        bool dirtyAfterSourceCapture = part.IsModified;
        row["dirty_before_source_geometry_capture"] = dirtyBeforeSourceCapture;
        row["dirty_after_source_geometry_capture"] = dirtyAfterSourceCapture;
        Require(dirtyBeforeSourceCapture == dirtyAfterSourceCapture,
            "Independent source-geometry evidence capture changed Part.IsModified before Prepare.");

        M3PlanSnapshot plan = suppliedPlan ?? adapter.PrepareSketchPattern(binding, request);
        row["plan"] = plan == null ? null : plan.PublicView;
        bool dirtyImmediatelyAfterPrepare = part.IsModified;
        row["dirty_immediately_after_prepare"] = dirtyImmediatelyAfterPrepare;
        Dictionary<string, object> afterPrepare = StrictSnapshot(part);
        bool dirtyAfterPrepareSnapshot = part.IsModified;
        row["dirty_after_prepare_snapshot"] = dirtyAfterPrepareSnapshot;
        row["native_after_prepare"] = afterPrepare;
        row["revision_after_prepare"] = binding.RevisionId;
        row["prepare_snapshot_diff"] = SnapshotDiff(before, afterPrepare);
        Require(plan != null && plan.Kind == "edit_sketch_pattern", "F09 prepare did not return an edit_sketch_pattern plan.");
        Require(dirtyImmediatelyAfterPrepare == dirtyBeforeSourceCapture,
            "Production Prepare changed Part.IsModified before direct post-Prepare enumeration.");
        Require(dirtyAfterPrepareSnapshot == dirtyImmediatelyAfterPrepare,
            "Direct post-Prepare snapshot changed Part.IsModified.");
        Require(SameValue(before, afterPrepare), "F09 prepare changed direct NXOpen model state.");
        Require(String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal), "F09 prepare changed the binding revision.");

        int expectedPositionCount = ExpectedPositionCount(request);
        int expectedTotal = expectedPositionCount * request.SourceCurveIds.Count;
        Dictionary<string, object> publicPlan = plan.PublicView;
        Require(Convert.ToInt32(publicPlan["expected_pattern_instance_count"], CultureInfo.InvariantCulture) == expectedPositionCount,
            "Production plan position count differs from the independent request count.");
        Require(Convert.ToInt32(publicPlan["expected_output_curve_count"], CultureInfo.InvariantCulture) ==
            (expectedPositionCount - 1) * request.SourceCurveIds.Count,
            "Production plan clone count differs from the independent request count.");
        row["independent_expected_position_count"] = expectedPositionCount;
        row["independent_expected_total_curve_instances"] = expectedTotal;

        List<ExpectedInstance> expected = BuildExpectedInstances(fixture, request);
        List<object> sourceGeometryBefore = row["source_geometry_before"] as List<object>;
        Require(sourceGeometryBefore != null,
            "Direct pre-execution source geometry evidence is unavailable for source identity preservation.");
        foreach (string sourceId in request.SourceCurveIds)
        {
            Dictionary<string, object> sourceRecord = null;
            foreach (object value in sourceGeometryBefore)
            {
                Dictionary<string, object> candidate = value as Dictionary<string, object>;
                if (String.Equals(GetString(candidate, "source_curve_id"), sourceId, StringComparison.Ordinal))
                {
                    sourceRecord = candidate;
                    break;
                }
            }
            Require(sourceRecord != null,
                "Direct pre-execution source geometry record is missing for request ID " + sourceId + ".");
            Dictionary<string, object> nativeSource = ReadDictionary(sourceRecord, "native_geometry");
            Require(nativeSource != null && String.Equals(GetString(nativeSource, "journal_id"), sourceId,
                StringComparison.Ordinal) && String.Equals(GetString(nativeSource, "tag"),
                GetString(sourceRecord, "source_curve_tag"), StringComparison.Ordinal),
                "Direct source geometry Tag/JournalIdentifier baseline is inconsistent for request ID " + sourceId + ".");

            ExpectedInstance identityInstance = null;
            foreach (ExpectedInstance candidate in expected)
                if (String.Equals(candidate.SourceCurveId, sourceId, StringComparison.Ordinal) &&
                    candidate.PositionIndex == 0 && candidate.PositionXIndex == 0 && candidate.PositionYIndex == 0)
                {
                    identityInstance = candidate;
                    break;
                }
            Require(identityInstance != null && identityInstance.Geometry != null,
                "Independent oracle omitted the identity-position source instance for " + sourceId + ".");
            identityInstance.Geometry.Tag = GetString(nativeSource, "tag");
            identityInstance.Geometry.JournalId = GetString(nativeSource, "journal_id");
        }
        row["independent_expected_instances"] = ExpectedInstanceRecords(expected);
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        row["execution"] = ExecutionRecord(execution);
        Require(execution != null && execution.State == "APPLIED",
            "Native F09 operation did not return APPLIED: " + M2Json.CanonicalText(ExecutionRecord(execution)));
        Dictionary<string, object> afterExecute = StrictSnapshot(part);
        row["native_after_execute"] = afterExecute;
        row["execute_snapshot_diff"] = SnapshotDiff(before, afterExecute);
        row["execution_output_tags"] = ReadExecutionOutputTags(execution);
        row["native_commit_object_tag"] = ReadNested(execution.Result, "native_commit_object_tag");
        row["native_commit_object_id"] = ReadNested(execution.Result, "native_commit_object_id");
        row["native_commit_object_null"] = Object.Equals(ReadNested(execution.Result, "commit_returned_null"), true) ||
            ReadNested(execution.Result, "native_commit_object_tag") == null;
        row["native_operation_evidence"] = FindNestedDictionary(execution.Result, "native_operation");

        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        row["verification"] = VerificationRecord(verification);
        Dictionary<string, object> afterVerify = StrictSnapshot(part);
        row["native_after_verify"] = afterVerify;
        row["verify_snapshot_diff"] = SnapshotDiff(afterExecute, afterVerify);
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "Production VerifyPlan did not verify the applied F09 operation: " + M2Json.CanonicalText(VerificationRecord(verification)));
        Require(SameValue(afterExecute, afterVerify), "F09 VerifyPlan changed direct native state; verification must be read-only.");

        Sketch nativeSketch = FindSketch(part, fixture.SketchId);
        List<Curve> currentCurves = SketchCurves(nativeSketch);
        List<Curve> actualOutputs = new List<Curve>();
        foreach (Curve curve in currentCurves)
            if (!beforeTags.Contains(curve.Tag.ToString())) actualOutputs.Add(curve);
        row["direct_native_output_curve_records"] = CurveRecords(actualOutputs, fixture.Frame);
        row["direct_native_all_curve_records"] = CurveRecords(currentCurves, fixture.Frame);
        CheckExpectedOutputs(fixture, request, expected, actualOutputs, beforeCurves, row);
        CheckNativePreservation(before, afterExecute, fixture.SketchId, beforeTags, actualOutputs, row);
        CheckTemporaryHelpersAndTagDelta(before, afterExecute, execution, expected.Count - request.SourceCurveIds.Count, row);

        // Geometry-only arrays and mirror patterns may both return a null native
        // CommitObject. Accept that only after direct enumeration and the independent
        // oracle prove every expected clone with unique Tags.
        if (Object.Equals(row["native_commit_object_null"], true))
        {
            int expectedCloneCount = expected.Count - request.SourceCurveIds.Count;
            Require(actualOutputs.Count == expectedCloneCount,
                "Null CommitObject has no complete direct native Tag delta for all expected clones.");
            Require(UniqueTags(actualOutputs), "Null CommitObject path reuses an actual curve Tag.");
            row["null_commit_accepted_by_direct_tag_delta_and_oracle"] = true;
        }
    }

    private static void CheckExpectedOutputs(Fixture fixture, M3SketchPatternRequest request,
        List<ExpectedInstance> expected, List<Curve> actualOutputs, List<Curve> beforeCurves,
        Dictionary<string, object> row)
    {
        List<ExpectedInstance> clones = new List<ExpectedInstance>();
        foreach (ExpectedInstance item in expected)
            if (item.PositionIndex != 0 || item.PositionXIndex != 0 || item.PositionYIndex != 0) clones.Add(item);
        Require(actualOutputs.Count == clones.Count,
            "Direct native Tag delta count does not equal the independent source-by-position clone count.");
        Require(UniqueTags(actualOutputs), "Direct native output curve Tags are not unique.");

        bool[,] edges = new bool[clones.Count, actualOutputs.Count];
        for (int i = 0; i < clones.Count; i++)
            for (int j = 0; j < actualOutputs.Count; j++)
                edges[i, j] = CurvesMatch(clones[i].Geometry, CaptureCurve(actualOutputs[j], fixture.Frame));
        int[] expectedToActual = new int[clones.Count];
        int[] actualToExpected = new int[actualOutputs.Count];
        for (int i = 0; i < expectedToActual.Length; i++) expectedToActual[i] = -1;
        for (int i = 0; i < actualToExpected.Length; i++) actualToExpected[i] = -1;
        int matched = 0;
        for (int i = 0; i < expectedToActual.Length; i++)
            if (AugmentMatch(i, edges, expectedToActual, actualToExpected, new bool[actualOutputs.Count])) matched++;
        Require(matched == clones.Count,
            "Independent geometry oracle could not establish a complete one-to-one expected/actual mapping.");
        List<object> mapping = new List<object>();
        HashSet<string> assignedTags = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < clones.Count; i++)
        {
            Curve actual = actualOutputs[expectedToActual[i]];
            Require(assignedTags.Add(actual.Tag.ToString()), "One actual native Tag was reused by multiple expected instances.");
            mapping.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "source_curve_id", clones[i].SourceCurveId },
                { "position_index", clones[i].PositionIndex },
                { "position_x_index", clones[i].PositionXIndex },
                { "position_y_index", clones[i].PositionYIndex },
                { "expected_geometry", CurveModelRecord(clones[i].Geometry) },
                { "actual_tag", actual.Tag.ToString() }, { "actual_journal_id", actual.JournalIdentifier },
                { "actual_geometry", CurveModelRecord(CaptureCurve(actual, fixture.Frame)) },
                { "unique_actual_assignment", true }
            });
        }
        Require(assignedTags.Count == actualOutputs.Count, "At least one actual output curve was not consumed exactly once.");
        row["one_to_one_mapping"] = mapping;
        row["one_to_one_expected_count"] = clones.Count;
        row["one_to_one_actual_count"] = actualOutputs.Count;
        row["no_actual_tag_reused"] = true;

        List<object> sourceChecks = new List<object>();
        foreach (string sourceId in request.SourceCurveIds)
        {
            ExpectedInstance identityInstance = null;
            foreach (ExpectedInstance candidate in expected)
                if (String.Equals(candidate.SourceCurveId, sourceId, StringComparison.Ordinal) &&
                    candidate.PositionIndex == 0 && candidate.PositionXIndex == 0 && candidate.PositionYIndex == 0)
                {
                    identityInstance = candidate;
                    break;
                }
            Require(identityInstance != null && identityInstance.Geometry != null &&
                !String.IsNullOrWhiteSpace(identityInstance.Geometry.Tag),
                "Frozen identity-position source model is missing Tag/geometry for request ID " + sourceId + ".");
            Curve sourceNow = FindCurveByTag(FindSketchFromFixture(fixture), identityInstance.Geometry.Tag);
            CurveModel sourceAfter = sourceNow == null ? null : CaptureCurve(sourceNow, fixture.Frame);
            Require(sourceNow != null && CurvesMatch(identityInstance.Geometry, sourceAfter),
                "A source curve changed during F09 execution: " + sourceId);
            sourceChecks.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "source_curve_id", sourceId }, { "source_tag", identityInstance.Geometry.Tag },
                { "source_journal_id_before", identityInstance.Geometry.JournalId },
                { "source_journal_id_after", sourceNow.JournalIdentifier },
                { "geometry_before", CurveModelRecord(identityInstance.Geometry) },
                { "geometry_after", CurveModelRecord(sourceAfter) },
                { "preserved", true }
            });
        }
        row["source_curve_preservation"] = sourceChecks;
        row["source_instance_count_in_expected_records"] = request.SourceCurveIds.Count;
        row["expected_instance_count_including_sources"] = expected.Count;
    }

    private static void CheckTemporaryHelpersAndTagDelta(Dictionary<string, object> before,
        Dictionary<string, object> after, M3ExecutionResult execution, int expectedCloneCount,
        Dictionary<string, object> row)
    {
        Dictionary<string, object> nativeOperation = FindNestedDictionary(execution == null ? null : execution.Result, "native_operation");
        Dictionary<string, object> factory = nativeOperation == null ? null : ReadDictionary(nativeOperation, "factory");
        Require(factory != null, "Native F09 result omitted factory output and helper inventory evidence.");
        Dictionary<string, object> helperBefore = ReadDictionary(factory, "helper_tags_before");
        Dictionary<string, object> helperCreated = ReadDictionary(factory, "helper_tags_created");
        Dictionary<string, object> helperScheduled = ReadDictionary(factory, "helper_tags_delete_scheduled");
        Dictionary<string, object> helperAfter = ReadDictionary(nativeOperation, "helper_tags_after");
        Require(helperBefore != null && helperCreated != null && helperScheduled != null && helperAfter != null,
            "F09 result must record helper point/axis/direction Tag inventories before, created, scheduled for deletion, and after.");
        Dictionary<string, object> helperBeforeByKind = NormalizeHelperInventory(helperBefore);
        Dictionary<string, object> helperCreatedByKind = NormalizeHelperInventory(helperCreated);
        Dictionary<string, object> helperScheduledByKind = NormalizeHelperInventory(helperScheduled);
        Dictionary<string, object> helperAfterByKind = NormalizeHelperInventory(helperAfter);
        Require(SameValue(helperBeforeByKind, helperAfterByKind),
            "Temporary helper Tag inventories did not return to the pre-operation baseline.");
        Require(SameValue(helperCreatedByKind, helperScheduledByKind),
            "Factory did not schedule deletion of every helper Tag that it created.");
        List<string> helperTags = FlattenTagInventory(helperCreatedByKind);
        row["factory_helper_tags_before"] = helperBeforeByKind;
        row["factory_helper_tags_created"] = helperCreatedByKind;
        row["factory_helper_tags_delete_scheduled"] = helperScheduledByKind;
        row["factory_helper_tags_after"] = helperAfterByKind;
        row["factory_helper_tags_created_flat"] = helperTags;

        Dictionary<string, object> beforeSketches = ReadDictionary(before, "sketches_by_tag");
        Dictionary<string, object> afterSketches = ReadDictionary(after, "sketches_by_tag");
        HashSet<string> afterObjectTags = new HashSet<string>(StringComparer.Ordinal);
        int beforeSketchObjectCount = CountSketchObjects(beforeSketches);
        int afterSketchObjectCount = CountSketchObjects(afterSketches, afterObjectTags);
        List<string> beforeCurves = ToStringList(before["global_curve_tags"]);
        List<string> afterCurves = ToStringList(after["global_curve_tags"]);
        List<string> beforePoints = ToStringList(before["global_point_tags"]);
        List<string> afterPoints = ToStringList(after["global_point_tags"]);
        HashSet<string> afterCurveSet = new HashSet<string>(afterCurves, StringComparer.Ordinal);
        HashSet<string> afterPointSet = new HashSet<string>(afterPoints, StringComparer.Ordinal);
        List<object> tagChecks = new List<object>();
        foreach (string tag in helperTags)
        {
            bool curvePresent = afterCurveSet.Contains(tag);
            bool pointPresent = afterPointSet.Contains(tag);
            bool sketchObjectPresent = afterObjectTags.Contains(tag);
            tagChecks.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", tag }, { "present_as_global_curve", curvePresent },
                { "present_as_global_point", pointPresent }, { "present_in_sketch_object_inventory", sketchObjectPresent },
                { "absent_after_operation", !curvePresent && !pointPresent && !sketchObjectPresent }
            });
            Require(!curvePresent && !pointPresent && !sketchObjectPresent,
                "Temporary builder helper Tag remains in the native model after F09 operation: " + tag);
        }
        row["factory_helper_tag_cleanup_checks"] = tagChecks;
        row["sketch_object_count_before"] = beforeSketchObjectCount;
        row["sketch_object_count_after"] = afterSketchObjectCount;
        row["global_curve_count_before"] = beforeCurves.Count;
        row["global_curve_count_after"] = afterCurves.Count;
        row["global_point_count_before"] = beforePoints.Count;
        row["global_point_count_after"] = afterPoints.Count;
        Require(afterSketchObjectCount == beforeSketchObjectCount + expectedCloneCount,
            "Sketch object inventory contains an unaccounted temporary helper or missing pattern output.");
        Require(afterCurves.Count == beforeCurves.Count + expectedCloneCount,
            "Part global Curve Tag inventory does not contain exactly the expected pattern outputs.");
        Require(afterPoints.Count == beforePoints.Count,
            "Part global NXOpen.Point inventory changed; temporary Point helpers were not removed.");
        row["temporary_helper_objects_absent_and_tag_counts_restored"] = true;

        List<string> reportedOutputTags = ReadExecutionOutputTags(execution);
        List<string> factoryOutputTags = ToStringList(factory["output_curve_tags"]);
        List<object> factoryOutputCurves = factory["output_curves"] as List<object>;
        Require(Convert.ToInt32(factory["output_curve_count"], CultureInfo.InvariantCulture) == expectedCloneCount &&
            factoryOutputTags.Count == expectedCloneCount && factoryOutputCurves != null &&
            factoryOutputCurves.Count == expectedCloneCount &&
            Object.Equals(ReadNested(execution.Result, "factory_output_list_consistent_with_geometry_delta"), true),
            "Native factory output count/Tags/records were not recorded before postcondition checks.");
        HashSet<string> reported = new HashSet<string>(reportedOutputTags, StringComparer.Ordinal);
        HashSet<string> factoryReported = new HashSet<string>(factoryOutputTags, StringComparer.Ordinal);
        HashSet<string> direct = new HashSet<string>();
        foreach (KeyValuePair<string, object> pair in afterSketches)
        {
            Dictionary<string, object> sketch = pair.Value as Dictionary<string, object>;
            if (sketch == null) continue;
            Dictionary<string, object> geometry = ReadDictionary(sketch, "geometry_by_tag");
            if (geometry == null) continue;
            foreach (string tag in geometry.Keys)
            {
                if (!reported.Contains(tag)) continue;
                direct.Add(tag);
            }
        }
        Require(reported.Count == expectedCloneCount && direct.SetEquals(reported) &&
            factoryReported.SetEquals(reported),
            "Production output Tag list is empty/incomplete or differs from the independently enumerated native output curves.");
        row["factory_output_curve_count"] = factoryOutputCurves.Count;
        row["factory_output_curve_tags"] = factoryOutputTags;
        row["factory_output_curves"] = factoryOutputCurves;
        row["production_output_tags_match_direct_tag_delta"] = true;
    }

    private static Dictionary<string, object> NormalizeHelperInventory(Dictionary<string, object> inventory)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (string key in new string[] { "points", "axes", "directions" })
        {
            object value;
            if (!inventory.TryGetValue(key, out value)) value = new List<object>();
            List<string> tags = ToStringList(value);
            tags.Sort(StringComparer.Ordinal);
            result[key] = tags;
        }
        return result;
    }

    private static List<string> FlattenTagInventory(Dictionary<string, object> inventory)
    {
        List<string> result = new List<string>();
        foreach (KeyValuePair<string, object> pair in inventory)
        {
            foreach (string tag in ToStringList(pair.Value)) result.Add(tag);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static int CountSketchObjects(Dictionary<string, object> sketches)
    {
        return CountSketchObjects(sketches, null);
    }

    private static int CountSketchObjects(Dictionary<string, object> sketches, HashSet<string> allTags)
    {
        int count = 0;
        if (sketches == null) return count;
        foreach (KeyValuePair<string, object> pair in sketches)
        {
            Dictionary<string, object> sketch = pair.Value as Dictionary<string, object>;
            Dictionary<string, object> objects = sketch == null ? null : ReadDictionary(sketch, "all_objects_by_tag");
            if (objects == null) continue;
            count += objects.Count;
            if (allTags != null)
                foreach (string tag in objects.Keys) allTags.Add(tag);
        }
        return count;
    }

    private static void CheckNativePreservation(Dictionary<string, object> before,
        Dictionary<string, object> after, string targetSketchId, HashSet<string> sourceTags,
        List<Curve> outputs, Dictionary<string, object> row)
    {
        Dictionary<string, object> beforeSketches = ReadDictionary(before, "sketches_by_tag");
        Dictionary<string, object> afterSketches = ReadDictionary(after, "sketches_by_tag");
        Require(beforeSketches != null && afterSketches != null, "Direct snapshot is missing Sketch inventories.");
        string targetSketchTag = null;
        foreach (KeyValuePair<string, object> pair in beforeSketches)
        {
            Dictionary<string, object> record = pair.Value as Dictionary<string, object>;
            if (String.Equals(GetString(record, "journal_id"), targetSketchId, StringComparison.Ordinal))
            {
                targetSketchTag = pair.Key;
                break;
            }
        }
        Require(!String.IsNullOrWhiteSpace(targetSketchTag),
            "Target Sketch JournalIdentifier did not resolve to its stable native Tag in the pre-edit snapshot.");
        List<object> changedNonTarget = new List<object>();
        foreach (KeyValuePair<string, object> pair in beforeSketches)
        {
            Dictionary<string, object> preSketch = pair.Value as Dictionary<string, object>;
            Dictionary<string, object> postSketch = afterSketches.ContainsKey(pair.Key)
                ? afterSketches[pair.Key] as Dictionary<string, object> : null;
            Require(preSketch != null && postSketch != null, "An existing native Sketch disappeared during pattern execution.");
            bool target = String.Equals(pair.Key, targetSketchTag, StringComparison.Ordinal);
            if (!target)
            {
                Require(SameTagMatchedNativeProjection(preSketch, postSketch),
                    "Non-target Sketch geometry, constraints, frame or solver state changed beyond JournalIdentifier metadata.");
                continue;
            }
            CompareSketchPreservation(preSketch, postSketch, sourceTags, outputs, changedNonTarget);
        }
        Require(changedNonTarget.Count == 0, "One or more target non-source Sketch curves changed.");
        row["native_preservation"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "existing_sketches_preserved", true },
            { "target_sketch_tag", targetSketchTag },
            { "non_target_geometry_preserved", changedNonTarget.Count == 0 },
            { "constraints_and_expressions_preserved", ExistingConstraintInventoryPreserved(before, after) },
            { "reference_status_preserved", ExistingReferenceStatusPreserved(before, after, targetSketchTag, outputs) },
            { "frames_preserved", ExistingSketchFramesPreserved(before, after) },
            { "history_preexisting_features_preserved", ExistingHistoryPreserved(before, after) },
            { "bodies_preserved", SameValue(ReadDictionary(before, "bodies_by_tag"), ReadDictionary(after, "bodies_by_tag")) },
            { "tag_matched_journal_metadata_projection", "Exact comparisons after removing only journal_id/model_journal_id fields from Tag-matched Sketch, geometry, constraint, and operand records; raw snapshots retain original values." },
            { "target_non_source_differences", changedNonTarget },
            { "solver_before_and_after", SolverRecords(before, after, targetSketchId) }
        };
        Require(ExistingConstraintInventoryPreserved(before, after),
            "Existing native Sketch constraints or their expressions/operands changed.");
        Require(ExistingReferenceStatusPreserved(before, after, targetSketchTag, outputs),
            "Existing reference status changed or new pattern geometry is not active.");
        Require(ExistingSketchFramesPreserved(before, after), "An existing Sketch frame changed.");
        Require(ExistingHistoryPreserved(before, after), "Preexisting feature history changed or disappeared.");
        Require(SameValue(ReadDictionary(before, "bodies_by_tag"), ReadDictionary(after, "bodies_by_tag")),
            "Body inventory, face/edge Tags or bounding box changed during a sketch pattern.");
        Require(SolverStatusPreserved(before, after, targetSketchId),
            "The target Sketch solver status became invalid or changed classification.");
    }

    private static void CompareSketchPreservation(Dictionary<string, object> before,
        Dictionary<string, object> after, HashSet<string> sourceTags, List<Curve> outputs,
        List<object> changed)
    {
        Require(SameValue(before["frame"], after["frame"]), "Target Sketch frame changed during F09.");
        Require(SameValue(before["solver_status"], after["solver_status"]) ||
            (IsSolverAcceptable(GetString(before, "solver_status")) && IsSolverAcceptable(GetString(after, "solver_status"))),
            "Target Sketch solver status became invalid.");
        Dictionary<string, object> preCurves = ReadDictionary(before, "geometry_by_tag");
        Dictionary<string, object> postCurves = ReadDictionary(after, "geometry_by_tag");
        foreach (KeyValuePair<string, object> pair in preCurves)
        {
            if (sourceTags.Contains(pair.Key)) continue;
            Dictionary<string, object> pre = pair.Value as Dictionary<string, object>;
            Dictionary<string, object> post = postCurves.ContainsKey(pair.Key)
                ? postCurves[pair.Key] as Dictionary<string, object> : null;
            if (pre == null || post == null || !SameTagMatchedNativeProjection(pre, post))
                changed.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", pair.Key }, { "before", pre }, { "after", post }
                });
        }
        List<string> newTags = new List<string>();
        foreach (Curve curve in outputs) newTags.Add(curve.Tag.ToString());
        HashSet<string> outputTags = new HashSet<string>(newTags, StringComparer.Ordinal);
        foreach (string tag in postCurves.Keys)
            if (!preCurves.ContainsKey(tag) && !outputTags.Contains(tag))
                changed.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "unexpected_new_tag", tag }, { "record", postCurves[tag] }
                });
        Require(SameTagMatchedNativeProjection(ReadDictionary(before, "constraints_by_tag"),
            ReadDictionary(after, "constraints_by_tag")),
            "Target Sketch constraints or dimension expressions changed during pattern execution.");
    }

    private static void RunControlledUndoCase(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, Session session, List<object> fixtures, Dictionary<string, object> row)
    {
        Fixture fixture = CreateFixture(adapter, binding, part, session, fixtures,
            "controlled-undo-injection", BuildFrame(new double[] { 0.0, 0.0, 0.0 },
                new double[] { 0.0, 0.0, 1.0 }, new double[] { 1.0, 0.0, 0.0 }),
            new List<M3SketchGeometryRequest> { Line("undo-source", 0.0, 0.0, 4.0, 0.0) });
        row["materialization"] = Materialize(adapter, binding, part);
        M3SketchPatternRequest request = LinearRequest(binding, fixture,
            new string[] { "undo-source" }, new double[] { 0.0, 1.0 }, 4.0, 2);
        row["classification"] = "controlled_postcondition_injection";
        row["spontaneous_native_failure_claimed"] = false;
        row["request"] = PatternRequestRecord(request);
        Dictionary<string, object> before = StrictSnapshot(part);
        string revisionBefore = binding.RevisionId;
        row["native_before"] = before;
        row["revision_before"] = revisionBefore;
        M3PlanSnapshot plan = adapter.PrepareSketchPattern(binding, request);
        row["plan"] = plan == null ? null : plan.PublicView;
        Require(plan != null, "Controlled Undo fixture could not prepare a valid plan.");
        Require(SameValue(before, StrictSnapshot(part)) && String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal),
            "Controlled Undo prepare changed native state or revision.");

        object state = plan.AdapterState;
        FieldInfo expectedCountField = state.GetType().GetField("ExpectedOutputCurveCount",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Require(expectedCountField != null && expectedCountField.FieldType == typeof(int),
            "F09 plan no longer exposes ExpectedOutputCurveCount for controlled postcondition injection.");
        int expectedCount = (int)expectedCountField.GetValue(state);
        expectedCountField.SetValue(state, expectedCount + 1);
        row["injected_field"] = "M3SketchPatternPlanState.ExpectedOutputCurveCount";
        row["expected_output_count_before_injection"] = expectedCount;
        row["expected_output_count_after_injection"] = expectedCount + 1;
        row["injection_applied_after_prepare_before_execute"] = true;
        M3ExecutionResult execution;
        try { execution = adapter.ExecutePlan(binding, plan); }
        finally { expectedCountField.SetValue(state, expectedCount); }
        row["execution"] = ExecutionRecord(execution);
        row["native_operation_evidence"] = FindNestedDictionary(execution == null ? null : execution.Result, "native_operation");
        row["output_curve_tags_recorded_before_undo"] = ReadNested(execution == null ? null : execution.Result, "factory_output_curve_tags");
        row["factory_output_evidence"] = ReadNested(execution == null ? null : execution.Result, "output_curves");
        Dictionary<string, object> after = StrictSnapshot(part);
        row["native_after_undo"] = after;
        row["snapshot_diff"] = SnapshotDiff(before, after);
        row["revision_after_undo"] = binding.RevisionId;
        row["undo_restore"] = FindNestedDictionary(execution == null ? null : execution.Result, "rollback");
        Require(execution != null && execution.State == "ROLLED_BACK",
            "Controlled postcondition mismatch must end ROLLED_BACK with proven actual NX Undo.");
        List<string> nativeOutputTags = ToStringList(ReadNested(execution.Result, "factory_output_curve_tags"));
        object nativeOutputRecordsValue = ReadNested(execution.Result, "output_curves");
        List<object> nativeOutputRecords = nativeOutputRecordsValue as List<object>;
        Require(nativeOutputTags.Count == expectedCount && UniqueStringTags(nativeOutputTags) &&
            nativeOutputRecords != null && nativeOutputRecords.Count == expectedCount,
            "Controlled failure report omitted real native output Tags/records captured before the injected count assertion.");
        ValidateFactoryOutputRecords(nativeOutputRecords, row);
        Require(Object.Equals(ReadNested(execution.Result, "factory_output_list_consistent_with_geometry_delta"), true),
            "Controlled failure report did not confirm the native output list matched the real geometry Tag delta.");
        row["native_output_tags_reconciled_before_undo"] = nativeOutputTags;
        row["native_output_curve_records_before_undo"] = nativeOutputRecords;
        Dictionary<string, object> nativeOperation = FindNestedDictionary(execution.Result, "native_operation");
        Require(nativeOperation != null && Object.Equals(nativeOperation["native_operation_called"], true),
            "Controlled rollback did not record that the real native operation was called.");
        Dictionary<string, object> undo = FindNestedDictionary(execution.Result, "rollback");
        Require(undo != null && Object.Equals(undo["undo_to_mark_succeeded"], true),
            "Controlled rollback did not record successful NX UndoToMark.");
        Require(Object.Equals(undo["geometry_restored"], true) &&
            Object.Equals(undo["constraints_restored"], true) &&
            Object.Equals(undo["frames_restored"], true) &&
            Object.Equals(undo["reference_status_restored"], true) &&
            Object.Equals(undo["history_restored"], true) &&
            Object.Equals(undo["bodies_restored"], true) &&
            Object.Equals(undo["dirty_state_restored"], true),
            "The adapter did not prove each captured native restoration condition.");
        Require(SameValue(before, after), "Direct all-part NXOpen snapshot differs after actual NX Undo.");
        foreach (string tag in nativeOutputTags)
            Require(!FindTagInSnapshot(after, tag), "Rolled-back native output Tag remains after actual Undo: " + tag);
        row["direct_native_undo_restoration_passed"] = true;
    }

    private static void RunSyntheticBaselineCorruption(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part, Fixture fixture, Dictionary<string, object> row)
    {
        M3SketchPatternRequest request = LinearRequest(binding, fixture,
            new string[] { "source-line" }, new double[] { 0.0, 1.0 }, 12.0, 2);
        M3PlanSnapshot plan = adapter.PrepareSketchPattern(binding, request);
        Require(plan != null, "Synthetic baseline challenge could not prepare a valid plan.");
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        Require(execution != null && execution.State == "APPLIED", "Synthetic baseline challenge setup operation did not apply.");
        M3VerificationResult initial = adapter.VerifyPlan(binding, plan);
        Require(initial != null && initial.Verified && ChecksPass(initial.Checks),
            "Baseline challenge requires an initially verified production plan.");
        Dictionary<string, object> nativeBeforeChallenge = StrictSnapshot(part);
        object state = plan.AdapterState;
        List<object> challenges = new List<object>();
        row["classification"] = "synthetic_baseline_corruption";
        row["native_mutation_attempted"] = false;
        row["request"] = PatternRequestRecord(request);
        row["plan"] = plan.PublicView;
        row["initial_verify"] = VerificationRecord(initial);
        row["baseline_challenges"] = challenges;

        string[][] matrix = new string[][]
        {
            new string[] { "GeometryBefore", "source_curves" },
            new string[] { "ConstraintsBefore", "sketch_constraints" },
            new string[] { "FramesBefore", "sketch_frames" },
            new string[] { "ReferenceStatesBefore", "reference_status" },
            new string[] { "BodiesBefore", "bodies" },
            new string[] { "ExpressionsBefore", "sketch_constraints" },
            new string[] { "HistoryBefore", "history" },
            new string[] { "FeatureTagsBefore", "history" }
        };
        foreach (string[] entry in matrix)
        {
            FieldInfo field = state.GetType().GetField(entry[0],
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Require(field != null, "Required Prepare baseline field was not found: " + entry[0]);
            object original = field.GetValue(state);
            object corrupted = ContradictoryBaselineValue(field.FieldType, original);
            Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "baseline_field", entry[0] }, { "expected_mismatch_check", entry[1] },
                { "corrupted_value", BaselineValueRecord(corrupted) },
                { "native_mutation_attempted", false }
            };
            challenges.Add(evidence);
            field.SetValue(state, corrupted);
            try
            {
                M3VerificationResult challenged = adapter.VerifyPlan(binding, plan);
                evidence["verification"] = VerificationRecord(challenged);
                Dictionary<string, object> check = challenged == null || challenged.Checks == null
                    ? null : challenged.Checks[entry[1]] as Dictionary<string, object>;
                evidence["matching_check_status"] = check == null ? null : GetString(check, "status");
                Require(challenged != null && !challenged.Verified && check != null &&
                    String.Equals(GetString(check, "status"), "MISMATCH", StringComparison.Ordinal),
                    "Corrupting retained pre-edit " + entry[0] + " did not fail the expected production check.");
                evidence["passed"] = true;
            }
            finally { field.SetValue(state, original); }
            M3VerificationResult restored = adapter.VerifyPlan(binding, plan);
            Require(restored != null && restored.Verified && ChecksPass(restored.Checks),
                "Restoring " + entry[0] + " did not restore a passing production verify.");
            evidence["restored_verify"] = VerificationRecord(restored);
        }
        Dictionary<string, object> nativeAfterChallenge = StrictSnapshot(part);
        row["native_before_challenge"] = nativeBeforeChallenge;
        row["native_after_challenge"] = nativeAfterChallenge;
        row["native_snapshot_diff"] = SnapshotDiff(nativeBeforeChallenge, nativeAfterChallenge);
        Require(SameValue(nativeBeforeChallenge, nativeAfterChallenge),
            "Synthetic baseline corruption test changed the live NX model.");
        row["all_corruption_checks_rejected"] = true;
    }

    private static object ContradictoryBaselineValue(Type fieldType, object original)
    {
        if (fieldType == typeof(string)) return "F09-SYNTHETIC-CORRUPTED-BASELINE";
        if (fieldType == typeof(bool)) return !Object.Equals(original, true);
        if (fieldType == typeof(int)) return Convert.ToInt32(original, CultureInfo.InvariantCulture) + 1;
        if (fieldType == typeof(double)) return Convert.ToDouble(original, CultureInfo.InvariantCulture) + 1234.0;
        if (typeof(IDictionary).IsAssignableFrom(fieldType) || typeof(IList).IsAssignableFrom(fieldType))
            return Activator.CreateInstance(fieldType);
        throw new InvalidOperationException("No isolated contradiction strategy exists for baseline type " + fieldType.FullName);
    }

    private static object BaselineValueRecord(object value)
    {
        if (value == null) return new Dictionary<string, object> { { "kind", "null" } };
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "type", value.GetType().FullName }
        };
        IDictionary dictionary = value as IDictionary;
        if (dictionary != null)
        {
            List<string> keys = new List<string>();
            foreach (object key in dictionary.Keys) keys.Add(Convert.ToString(key, CultureInfo.InvariantCulture));
            record["count"] = dictionary.Count;
            record["keys"] = keys;
            return record;
        }
        IList list = value as IList;
        if (list != null)
        {
            record["count"] = list.Count;
            return record;
        }
        record["value"] = value is string ? value : Convert.ToString(value, CultureInfo.InvariantCulture);
        return record;
    }

    private static void RunSaveCopyRoundTrip(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Session session, Part part, string targetPath, Dictionary<string, object> row)
    {
        Dictionary<string, object> before = StrictSnapshot(part);
        Dictionary<string, object> projectionBefore = RoundTripProjection(before);
        row["native_before_save_copy"] = before;
        row["direct_projection_before"] = projectionBefore;
        row["source_path_before"] = part.FullPath;
        row["target_path"] = targetPath;
        M2SaveCopyResult result = adapter.SaveCopy(binding, targetPath, true);
        row["save_copy_result"] = SaveCopyRecord(result);
        Require(result != null && result.State == "SAVED" && result.Reopened && ChecksPass(result.Checks),
            "Production SaveCopy did not return SAVED/reopened with all round-trip checks verified.");
        Part reopened = session.Parts.Work;
        Require(reopened != null && String.Equals(Path.GetFullPath(reopened.FullPath),
            Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase),
            "NX Work Part is not the requested unique saved copy after reopen.");
        Require(!String.Equals(Path.GetFullPath(reopened.FullPath), Path.GetFullPath(row["source_path_before"].ToString()),
            StringComparison.OrdinalIgnoreCase), "SaveCopy reopened over the source acceptance part.");
        Dictionary<string, object> after = StrictSnapshot(reopened);
        Dictionary<string, object> projectionAfter = RoundTripProjection(after);
        row["native_after_reopen"] = after;
        row["direct_projection_after"] = projectionAfter;
        List<object> differences = new List<object>();
        CompareRoundTripValues("$", projectionBefore, projectionAfter, differences, PositionToleranceMm);
        row["direct_projection_differences"] = differences;
        Require(differences.Count == 0,
            "Independent direct NXOpen projection differs after save-copy/reopen: " + M2Json.CanonicalText(differences));
        row["direct_projection_roundtrip_passed"] = true;
        row["part_tag_changed_on_reopen"] = !String.Equals(GetString(before, "part_tag"), GetString(after, "part_tag"), StringComparison.Ordinal);
    }

    private static void RunArcSweepHelperBounds(Dictionary<string, object> row)
    {
        Type[] signature = new Type[] { typeof(double), typeof(double) };
        MethodInfo method = typeof(M2OpenModelAdapter).GetMethod("PatternArcSweep",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, signature, null);
        Require(method != null, "M2OpenModelAdapter.PatternArcSweep(double,double) was not found.");
        ParameterInfo[] parameters = method.GetParameters();
        Require(parameters.Length == 2 && parameters[0].ParameterType == typeof(double) &&
            parameters[1].ParameterType == typeof(double) && method.ReturnType == typeof(double),
            "PatternArcSweep must have the reviewed double start/end radians signature.");
        List<object> samples = new List<object>();
        row["method"] = method.DeclaringType.FullName + "." + method.Name;
        row["method_signature"] = "double PatternArcSweep(double startAngleRad, double endAngleRad)";
        AddSweepAccepted(method, samples, "quarter_arc", 0.0, Pi / 2.0, Pi / 2.0);
        AddSweepAccepted(method, samples, "cross_zero_raw_signed_interval",
            350.0 * Pi / 180.0, 10.0 * Pi / 180.0, -340.0 * Pi / 180.0);
        AddSweepAccepted(method, samples, "one_full_turn", 0.0, TwoPi, TwoPi);
        AddSweepRejected(method, samples, "equal_endpoints_zero_interval", 1.25, 1.25);
        AddSweepRejected(method, samples, "450_degree_sweep", 0.0, 450.0 * Pi / 180.0);
        AddSweepRejected(method, samples, "720_degree_sweep", 0.0, 4.0 * Pi);
        AddSweepRejected(method, samples, "nan_end", 0.0, Double.NaN);
        AddSweepRejected(method, samples, "infinite_end", 0.0, Double.PositiveInfinity);
        AddSweepRejected(method, samples, "infinite_start", Double.NegativeInfinity, 1.0);
        row["arc_sweep_samples"] = samples;
        row["native_model_mutation"] = false;
        RunWrongGeometryCandidates(row);
    }

    private static void AddSweepAccepted(MethodInfo method, List<object> samples,
        string name, double start, double end, double expected)
    {
        double actual = Convert.ToDouble(method.Invoke(null, new object[] { start, end }), CultureInfo.InvariantCulture);
        Require(IsFinite(actual) && Math.Abs(actual - expected) <= AngleToleranceRad,
            "Production sweep helper returned an incorrect value for " + name + ".");
        samples.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "start_angle_rad", start }, { "end_angle_rad", end },
            { "expected_sweep_rad", expected }, { "actual_sweep_rad", actual }, { "status", "ACCEPTED" }
        });
    }

    private static void AddSweepRejected(MethodInfo method, List<object> samples,
        string name, double start, double end)
    {
        Exception caught = null;
        try { method.Invoke(null, new object[] { start, end }); }
        catch (TargetInvocationException ex) { caught = ex.InnerException ?? ex; }
        string code = ErrorCode(caught);
        Require(caught != null && String.Equals(code, "INVALID_ARC_SWEEP", StringComparison.Ordinal),
            "Production sweep helper did not specifically reject " + name + " with INVALID_ARC_SWEEP.");
        samples.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "start_angle_rad", NumberOrText(start) }, { "end_angle_rad", NumberOrText(end) },
            { "error_code", code }, { "exception", caught.ToString() }, { "status", "REJECTED" }
        });
    }

    private static void RunWrongGeometryCandidates(Dictionary<string, object> row)
    {
        CurveModel expected = new CurveModel
        {
            Type = "Arc", IsArc = true, IsFullCircle = false,
            Center = new double[] { 2.0, -1.0 }, AxisX = new double[] { 1.0, 0.0 },
            AxisY = new double[] { 0.0, 1.0 }, Radius = 3.0,
            StartAngle = 0.35, Sweep = 2.0 * Pi / 3.0
        };
        expected.Start = ArcPoint(expected.Center, expected.AxisX, expected.AxisY,
            expected.Radius, expected.StartAngle);
        expected.End = ArcPoint(expected.Center, expected.AxisX, expected.AxisY,
            expected.Radius, expected.StartAngle + expected.Sweep);
        expected.LengthMm = expected.Radius * expected.Sweep;
        List<object> candidates = new List<object>();
        AddCandidate(candidates, "correct_partial_arc", expected, CloneCurveModel(expected), true);
        CurveModel complement = CloneCurveModel(expected);
        complement.Start = (double[])expected.End.Clone();
        complement.End = (double[])expected.Start.Clone();
        complement.StartAngle = expected.StartAngle + expected.Sweep;
        complement.Sweep = TwoPi - expected.Sweep;
        complement.LengthMm = complement.Radius * complement.Sweep;
        AddCandidate(candidates, "complementary_arc_sector", expected, complement, false);
        CurveModel wrongRadius = CloneCurveModel(expected);
        wrongRadius.Radius += 0.01;
        wrongRadius.LengthMm = wrongRadius.Radius * wrongRadius.Sweep;
        AddCandidate(candidates, "radius_drift_0p01mm", expected, wrongRadius, false);
        CurveModel wrongStart = CloneCurveModel(expected);
        wrongStart.Start = new double[] { expected.Start[0] + 1.0e-4, expected.Start[1] };
        AddCandidate(candidates, "endpoint_drift_0p0001mm", expected, wrongStart, false);
        CurveModel wrongTangent = CloneCurveModel(expected);
        wrongTangent.AxisY = new double[] { 0.0, -1.0 };
        AddCandidate(candidates, "reversed_plane_orientation", expected, wrongTangent, false);
        CurveModel wrongLength = CloneCurveModel(expected);
        wrongLength.LengthMm += 1.0e-4;
        AddCandidate(candidates, "arc_length_residual_0p0001mm", expected, wrongLength, false);
        row["wrong_geometry_candidate_checks"] = candidates;
    }

    private static void AddCandidate(List<object> records, string name, CurveModel expected,
        CurveModel actual, bool expectedMatch)
    {
        bool matched = CurvesMatch(expected, actual);
        Require(matched == expectedMatch, "Independent geometry oracle returned an unexpected candidate result: " + name);
        records.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "expected_match", expectedMatch }, { "actual_match", matched },
            { "expected", CurveModelRecord(expected) }, { "candidate", CurveModelRecord(actual) },
            { "passed", true }
        });
    }

    private static CurveModel CloneCurveModel(CurveModel source)
    {
        return new CurveModel
        {
            Type = source.Type, IsArc = source.IsArc, IsFullCircle = source.IsFullCircle,
            Start = source.Start == null ? null : (double[])source.Start.Clone(),
            End = source.End == null ? null : (double[])source.End.Clone(),
            Center = source.Center == null ? null : (double[])source.Center.Clone(),
            AxisX = source.AxisX == null ? null : (double[])source.AxisX.Clone(),
            AxisY = source.AxisY == null ? null : (double[])source.AxisY.Clone(),
            Radius = source.Radius, StartAngle = source.StartAngle,
            Sweep = source.Sweep, LengthMm = source.LengthMm
        };
    }

    private static void RunReadOnlyRejections(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, Fixture fixture, Fixture foreign, Dictionary<string, object> row)
    {
        Curve line = fixture.Curves["reject-line"];
        Curve ellipse = fixture.Curves["reject-ellipse"];
        Curve spline = fixture.Curves["reject-spline"];
        Curve wrongAxis = fixture.Curves["reject-axis-line"];
        List<object> outcomes = new List<object>();
        row["rejections"] = outcomes;
        ExpectPrepareReject(adapter, binding, part, outcomes, "unsupported_ellipse",
            PatternRequestFor(binding, fixture, "linear", new string[] { "reject-ellipse" }),
            "UNSUPPORTED_CURVE_TYPE");
        ExpectPrepareReject(adapter, binding, part, outcomes, "unsupported_spline",
            PatternRequestFor(binding, fixture, "linear", new string[] { "reject-spline" }),
            "UNSUPPORTED_CURVE_TYPE");
        M3SketchPatternRequest wrongAxisRequest = MirrorRequest(binding, fixture, new string[] { "reject-line" }, "reject-ellipse");
        ExpectPrepareReject(adapter, binding, part, outcomes, "mirror_axis_must_be_Line",
            wrongAxisRequest, "UNSUPPORTED_CURVE_TYPE");
        M3SketchPatternRequest sourceAxisRequest = MirrorRequest(binding, fixture,
            new string[] { "reject-line" }, "reject-line");
        ExpectPrepareReject(adapter, binding, part, outcomes, "mirror_axis_is_a_source",
            sourceAxisRequest, "INVALID_REQUEST");
        M3SketchPatternRequest missing = PatternRequestFor(binding, fixture, "linear", new string[] { "F09-MISSING-CURVE" });
        ExpectPrepareReject(adapter, binding, part, outcomes, "missing_native_source_id", missing, "CURVE_NOT_FOUND");
        M3SketchPatternRequest cross = PatternRequestFor(binding, fixture, "linear",
            new string[] { foreign.Curves["other-line"].JournalIdentifier });
        ExpectPrepareReject(adapter, binding, part, outcomes, "cross_sketch_source_id", cross, "CURVE_NOT_FOUND");
        M3SketchPatternRequest duplicate = PatternRequestFor(binding, fixture, "linear",
            new string[] { line.JournalIdentifier, line.JournalIdentifier });
        ExpectPrepareReject(adapter, binding, part, outcomes, "duplicate_source_id", duplicate, "INVALID_REQUEST");
        M3SketchPatternRequest axisMissing = MirrorRequest(binding, fixture, new string[] { "reject-line" }, "F09-MISSING-AXIS");
        ExpectPrepareReject(adapter, binding, part, outcomes, "missing_mirror_axis", axisMissing, "CURVE_NOT_FOUND");
        Require(ellipse is Ellipse && spline is Spline && wrongAxis is Line,
            "Read-only rejection fixture did not contain the intended native NXOpen object types.");
    }

    private static void RunNumericRejections(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, Fixture fixture, Dictionary<string, object> row)
    {
        string id = ResolveGeometryId(fixture, "reject-line");
        List<object> outcomes = new List<object>();
        row["rejections"] = outcomes;
        M3SketchPatternRequest invalid = LinearRequest(binding, fixture, new string[] { "reject-line" },
            new double[] { 0.0, 0.0 }, 3.0, 2);
        ExpectPrepareReject(adapter, binding, part, outcomes, "zero_linear_direction", invalid, "INVALID_REQUEST");
        invalid = LinearRequest(binding, fixture, new string[] { "reject-line" }, new double[] { 1.0, 0.0 }, 0.0, 2);
        ExpectPrepareReject(adapter, binding, part, outcomes, "zero_linear_spacing", invalid, "INVALID_REQUEST");
        invalid = LinearRequest(binding, fixture, new string[] { "reject-line" }, new double[] { 1.0, 0.0 }, Double.NaN, 2);
        ExpectPrepareReject(adapter, binding, part, outcomes, "nonfinite_linear_spacing", invalid, "INVALID_REQUEST");
        invalid = LinearRequest(binding, fixture, new string[] { "reject-line" }, new double[] { 1.0, 0.0 }, 3.0, 1);
        ExpectPrepareReject(adapter, binding, part, outcomes, "linear_count_below_two", invalid, "INVALID_REQUEST");
        invalid = RectangularRequest(binding, fixture, new string[] { id }, new double[] { 1.0, 0.0 },
            new double[] { 1.0, 1.0 }, 2.0, 2.0, 2, 2);
        ExpectPrepareReject(adapter, binding, part, outcomes, "nonorthogonal_rectangular_directions", invalid, "INVALID_REQUEST");
        invalid = RectangularRequest(binding, fixture, new string[] { id }, new double[] { 1.0, 0.0 },
            new double[] { 0.0, 1.0 }, 2.0, 2.0, 1, 2);
        ExpectPrepareReject(adapter, binding, part, outcomes, "rectangular_count_below_two", invalid, "INVALID_REQUEST");
        invalid = CircularRequest(binding, fixture, new string[] { id }, new double[] { 0.0, 0.0 }, 0.0, 3);
        ExpectPrepareReject(adapter, binding, part, outcomes, "zero_circular_angle", invalid, "INVALID_REQUEST");
        invalid = CircularRequest(binding, fixture, new string[] { id }, new double[] { 0.0, 0.0 }, 180.0, 3);
        ExpectPrepareReject(adapter, binding, part, outcomes, "duplicate_circular_positions", invalid, "INVALID_REQUEST");
        invalid = CircularRequest(binding, fixture, new string[] { id }, new double[] { Double.NaN, 0.0 }, 90.0, 3);
        ExpectPrepareReject(adapter, binding, part, outcomes, "nonfinite_circular_center", invalid, "INVALID_REQUEST");
        invalid = LinearRequest(binding, fixture, new string[] { id }, new double[] { 1.0, 0.0 }, 1.0e308, 3);
        ExpectPrepareReject(adapter, binding, part, outcomes, "derived_linear_coordinate_overflow", invalid, "INVALID_REQUEST");
        invalid = PatternRequestFor(binding, fixture, "unsupported_mode", new string[] { id });
        ExpectPrepareReject(adapter, binding, part, outcomes, "unsupported_mode", invalid, "INVALID_REQUEST");
    }

    private static void RunLargeDirectionOutcome(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, Fixture fixture, Dictionary<string, object> row)
    {
        M3SketchPatternRequest request = LinearRequest(binding, fixture,
            new string[] { "large-direction-line" }, new double[] { 1.0e308, 1.0e308 }, 6.0, 2);
        row["materialization"] = Materialize(adapter, binding, part);
        request.RevisionId = binding.RevisionId;
        row["request"] = PatternRequestRecord(request);
        Dictionary<string, object> before = StrictSnapshot(part);
        string revisionBefore = binding.RevisionId;
        M3PlanSnapshot plan = null;
        Exception error = null;
        try { plan = adapter.PrepareSketchPattern(binding, request); }
        catch (Exception ex) { error = ex; }
        row["prepare_error"] = error == null ? null : error.ToString();
        row["prepare_error_code"] = ErrorCode(error);
        row["plan"] = plan == null ? null : plan.PublicView;
        Dictionary<string, object> afterPrepare = StrictSnapshot(part);
        row["native_after_prepare"] = afterPrepare;
        row["revision_after_prepare"] = binding.RevisionId;
        Require(SameValue(before, afterPrepare) && String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal),
            "Large finite direction Prepare changed native model state or revision.");
        if (error != null)
        {
            Require(error is M2ModelException && String.Equals(ErrorCode(error), "INVALID_REQUEST", StringComparison.Ordinal),
                "Large finite direction rejection must be a specific INVALID_REQUEST.");
            row["outcome"] = "READ_ONLY_REJECT";
            row["passed"] = true;
            return;
        }
        Require(plan != null && plan.Kind == "edit_sketch_pattern",
            "Large finite direction returned neither a valid pattern plan nor a rejection.");
        RequireFiniteNumbers(plan.PublicView, "large finite direction preview");
        row["outcome"] = "INDEPENDENT_NATIVE_NUMERIC_CHECK";
        ApplyAndCheckPattern(adapter, binding, part, fixture, request, row, plan);
    }

    private static void RequireFiniteNumbers(object value, string label)
    {
        if (value == null || value is string || value is bool) return;
        if (value is double)
        {
            Require(IsFinite((double)value), label + " contains a nonfinite number.");
            return;
        }
        if (value is float)
        {
            Require(IsFinite((double)(float)value), label + " contains a nonfinite number.");
            return;
        }
        IDictionary dictionary = value as IDictionary;
        if (dictionary != null)
        {
            foreach (DictionaryEntry item in dictionary) RequireFiniteNumbers(item.Value, label);
            return;
        }
        IList list = value as IList;
        if (list != null)
        {
            foreach (object item in list) RequireFiniteNumbers(item, label);
        }
    }

    private static void RunLargeCircularStepOutcome(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part, Fixture fixture, Dictionary<string, object> row)
    {
        M3SketchPatternRequest request = CircularRequest(binding, fixture,
            new string[] { "large-step-line" }, new double[] { 0.0, 0.0 }, 1.0e308, 2);
        List<object> outcomes = new List<object>();
        row["rejections"] = outcomes;
        ExpectPrepareReject(adapter, binding, part, outcomes, "large_finite_circular_angle_step", request, "INVALID_REQUEST");
        row["outcome"] = "READ_ONLY_REJECT";
        row["passed"] = true;
    }

    private static void ExpectPrepareReject(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, List<object> outcomes, string name, M3SketchPatternRequest request, string expectedCode)
    {
        Dictionary<string, object> materialization = Materialize(adapter, binding, part);
        request.RevisionId = binding.RevisionId;
        Dictionary<string, object> before = StrictSnapshot(part);
        string revisionBefore = binding.RevisionId;
        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "request", PatternRequestRecord(request) },
            { "expected_error_code", expectedCode }, { "native_before", before }, { "revision_before", revisionBefore },
            { "materialization", materialization }
        };
        outcomes.Add(row);
        M3PlanSnapshot plan = null;
        Exception error = null;
        try { plan = adapter.PrepareSketchPattern(binding, request); }
        catch (Exception ex) { error = ex; }
        Dictionary<string, object> after = StrictSnapshot(part);
        row["plan_returned"] = plan == null ? null : plan.PublicView;
        row["exception"] = error == null ? null : error.ToString();
        row["actual_error_code"] = ErrorCode(error);
        row["native_after"] = after;
        row["snapshot_diff"] = SnapshotDiff(before, after);
        row["revision_after"] = binding.RevisionId;
        Require(plan == null && error is M2ModelException &&
            String.Equals(ErrorCode(error), expectedCode, StringComparison.Ordinal),
            "Invalid F09 request " + name + " did not return the expected specific rejection.");
        Require(SameValue(before, after) && String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal),
            "Rejected F09 prepare changed native state or revision for " + name + ".");
        row["passed"] = true;
    }

    private static void AddTargetCoincidentAndReference(Session session, Fixture fixture, List<object> fixtures)
    {
        Sketch sketch = FindSketchFromFixture(fixture);
        Line first = fixture.Curves["source-line"] as Line;
        Line second = fixture.Curves["connected-nontarget-line"] as Line;
        Line reference = fixture.Curves["reference-line"] as Line;
        Require(first != null && second != null && reference != null, "Preservation fixture Lines are unavailable.");
        NXOpen.UF.UFSket.ReferenceStatus referenceBefore;
        UFSession.GetUFSession().Sket.AskReferenceStatus(sketch.Tag, reference.Tag, out referenceBefore);
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F09 target relation fixture");
        bool active = false;
        try
        {
            sketch.Activate(Sketch.ViewReorient.False);
            active = true;
            SketchGeometricConstraint relation = sketch.CreateCoincidentConstraint(
                new Sketch.ConstraintGeometry(first, Sketch.ConstraintPointType.EndVertex, 0),
                new Sketch.ConstraintGeometry(second, Sketch.ConstraintPointType.StartVertex, 0));
            Require(relation != null, "Could not create the target Coincident preservation relation.");
            UFSession.GetUFSession().Sket.SetReferenceStatus(sketch.Tag, reference.Tag,
                (UFSket.ReferenceStatus)0);
            sketch.Update();
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            active = false;
            Require(session.UpdateManager.DoUpdate(mark) == 0, "F09 target relation fixture update reported errors.");
            UFSket.ReferenceStatus status;
            UFSession.GetUFSession().Sket.AskReferenceStatus(sketch.Tag, reference.Tag, out status);
            Require((int)status == 0, "F09 target reference Line did not become reference geometry.");
            fixtures.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", "target-constraint-reference-control" }, { "sketch_id", fixture.SketchId },
                { "coincident_constraint_id", relation.JournalIdentifier },
                { "reference_curve_tag", reference.Tag.ToString() },
                { "reference_status_before", (int)referenceBefore }, { "reference_status_after", (int)status }
            });
        }
        finally
        {
            if (active) sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            session.DeleteUndoMark(mark, "F09 target relation fixture");
        }
    }

    private static void AddHorizontalDrivingDimension(Session session, Part part, Fixture fixture,
        Line line, double lengthMm, List<object> fixtures)
    {
        Require(line != null, "Driving dimension fixture requires a native Line.");
        Sketch sketch = FindSketchFromFixture(fixture);
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F09 driving dimension fixture");
        bool active = false;
        try
        {
            sketch.Activate(Sketch.ViewReorient.False);
            active = true;
            Point3d start = line.StartPoint;
            Point3d end = line.EndPoint;
            Point3d placement = Midpoint(start, end);
            SketchDimensionalConstraint dimension = sketch.CreateDimension(Sketch.ConstraintType.HorizontalDim,
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.StartPoint, AssocValue = 0 },
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.EndPoint, AssocValue = 0 },
                placement, null, Sketch.DimensionOption.CreateAsDriving);
            Require(dimension != null && dimension.AssociatedExpression != null,
                "Native NX dimension fixture returned no Driving expression.");
            dimension.AssociatedExpression.RightHandSide = lengthMm.ToString("0.########", CultureInfo.InvariantCulture);
            sketch.Update();
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            active = false;
            Require(session.UpdateManager.DoUpdate(mark) == 0, "F09 Driving dimension fixture update reported errors.");
            Require(dimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving,
                "F09 fixture dimension is not Driving.");
            fixtures.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", "driving-dimension-preservation-control" }, { "sketch_id", fixture.SketchId },
                { "line_tag", line.Tag.ToString() }, { "dimension_tag", dimension.Tag.ToString() },
                { "dimension_journal_id", dimension.JournalIdentifier },
                { "rhs", dimension.AssociatedExpression.RightHandSide },
                { "value_mm", dimension.AssociatedExpression.Value },
                { "unit", dimension.AssociatedExpression.Units == null ? null : dimension.AssociatedExpression.Units.Symbol },
                { "requested_value_mm", lengthMm }
            });
        }
        finally
        {
            if (active) sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            session.DeleteUndoMark(mark, "F09 driving dimension fixture");
        }
    }

    private static Fixture CreateFixture(M2OpenModelAdapter adapter, M2ModelBinding binding, Part part,
        Session session, List<object> fixtures, string name, Frame frame,
        List<M3SketchGeometryRequest> geometries)
    {
        Materialize(adapter, binding, part);
        M3SketchRequest request = new M3SketchRequest
        {
            RevisionId = binding.RevisionId,
            OriginMm = (double[])frame.Origin.Clone(),
            Normal = (double[])frame.Normal.Clone(),
            XAxis = (double[])frame.XAxis.Clone(),
            ConstraintMode = "geometry_only",
            Geometries = geometries
        };
        M3PlanSnapshot plan = adapter.PrepareSketch(binding, request);
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        Require(execution != null && execution.State == "APPLIED",
            "F09 setup fixture Sketch did not apply: " + name);
        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "F09 setup fixture Sketch did not verify: " + name);
        string sketchId = GetString(execution.Result, "sketch_id");
        Require(!String.IsNullOrWhiteSpace(sketchId), "M3 fixture creation omitted the native Sketch id.");
        Sketch sketch = FindSketch(part, sketchId);
        CheckNativeFrame(sketch, frame, name);
        Fixture fixture = new Fixture { Name = name, SketchId = sketchId, Frame = frame, Part = part };
        foreach (M3SketchGeometryRequest geometry in geometries)
        {
            Curve found = FindSeedCurve(sketch, frame, geometry);
            Require(found != null, "Direct NXOpen enumeration did not find requested setup geometry " + geometry.Id + ".");
            fixture.Curves.Add(geometry.Id, found);
            fixture.CurveIds.Add(geometry.Id, found.JournalIdentifier);
        }
        Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "sketch_id", sketchId }, { "sketch_tag", sketch.Tag.ToString() },
            { "frame", FrameRecord(frame) }, { "request", SketchRequestRecord(request) },
            { "plan", plan.PublicView }, { "execution", ExecutionRecord(execution) },
            { "verification", VerificationRecord(verification) },
            { "geometry_ids", StringObjectMap(fixture.CurveIds) },
            { "direct_native_geometry", CurveRecords(SketchCurves(sketch), frame) },
            { "status", "READY" }
        };
        fixtures.Add(item);
        SavePart(part, "F09 fixture " + name);
        return fixture;
    }

    private static void RecordFixtureOperation(List<object> fixtures, string name, M3PlanSnapshot plan,
        M3ExecutionResult execution, M3VerificationResult verification, Part part, string purpose)
    {
        fixtures.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "purpose", purpose },
            { "plan", plan == null ? null : plan.PublicView },
            { "execution", ExecutionRecord(execution) },
            { "verification", VerificationRecord(verification) },
            { "native_snapshot", StrictSnapshot(part) }
        });
    }

    private static Dictionary<string, object> Materialize(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part)
    {
        // Persist the independent fixture before any semantic getter is allowed to
        // mark it modified. Never save after these reads: subsequent Prepare and Undo
        // baselines must include the real post-read dirty state.
        SavePart(part, "F09 materialized fixture state");
        bool dirtyAfterSave = part.IsModified;
        Require(!dirtyAfterSave, "F09 fixture did not save cleanly before explicit materialization reads.");

        List<object> readEffects = new List<object>();
        bool dirtyBefore = part.IsModified;
        Dictionary<string, object> inspection = adapter.InspectPart(binding);
        bool dirtyAfter = part.IsModified;
        readEffects.Add(MaterializationReadRecord("InspectPart_before_sketches", inspection, dirtyBefore, dirtyAfter));
        foreach (Sketch sketch in part.Sketches.ToArray())
        {
            dirtyBefore = part.IsModified;
            Dictionary<string, object> sketchInspection = adapter.InspectSketch(binding, sketch.JournalIdentifier);
            dirtyAfter = part.IsModified;
            readEffects.Add(MaterializationReadRecord("InspectSketch:" + sketch.JournalIdentifier,
                sketchInspection, dirtyBefore, dirtyAfter));
        }
        dirtyBefore = part.IsModified;
        inspection = adapter.InspectPart(binding);
        dirtyAfter = part.IsModified;
        readEffects.Add(MaterializationReadRecord("InspectPart_after_sketches", inspection, dirtyBefore, dirtyAfter));

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "saved_before_inspection_sequence", true }, { "dirty_after_save", dirtyAfterSave },
            { "dirty_after_inspection_sequence", part.IsModified }, { "read_effects", readEffects },
            { "binding_revision_after_inspection_sequence", binding.RevisionId }
        };
    }

    private static Dictionary<string, object> MaterializationReadRecord(string name,
        Dictionary<string, object> inspection, bool dirtyBefore, bool dirtyAfter)
    {
        Dictionary<string, object> effects = FindNestedDictionary(inspection, "read_effects");
        Require(ReadEffectsMatch(effects, dirtyBefore, dirtyAfter),
            "Production " + name + " read_effects do not match observed Part.IsModified state.");
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "dirty_before_read", dirtyBefore }, { "dirty_after_read", dirtyAfter },
            { "read_effects", effects }
        };
    }

    private static bool ReadEffectsMatch(Dictionary<string, object> effects,
        bool dirtyBefore, bool dirtyAfter)
    {
        object reportedBefore;
        object reportedAfter;
        object reportedChanged;
        object mayMark;
        return effects != null && effects.TryGetValue("is_modified_before_read", out reportedBefore) &&
            effects.TryGetValue("is_modified_after_read", out reportedAfter) &&
            effects.TryGetValue("dirty_changed_by_read", out reportedChanged) &&
            effects.TryGetValue("read_may_mark_modified", out mayMark) && Object.Equals(mayMark, true) &&
            Object.Equals(reportedBefore, dirtyBefore) && Object.Equals(reportedAfter, dirtyAfter) &&
            Object.Equals(reportedChanged, !dirtyBefore && dirtyAfter);
    }

    private static Dictionary<string, object> SnapshotWithoutDirtyState(Dictionary<string, object> snapshot)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(snapshot, StringComparer.Ordinal);
        result.Remove("is_modified");
        result.Remove("is_modified_before_snapshot");
        result.Remove("is_modified_after_snapshot");
        return result;
    }

    private static void RunSnapshotReadEffectControl(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part, Dictionary<string, object> row)
    {
        Dictionary<string, object> materialization = Materialize(adapter, binding, part);
        row["materialization"] = materialization;
        bool dirtyBeforeFirst = part.IsModified;
        Dictionary<string, object> first = StrictSnapshot(part);
        bool dirtyAfterFirst = part.IsModified;
        Dictionary<string, object> second = StrictSnapshot(part);
        bool dirtyAfterSecond = part.IsModified;
        row["dirty_before_first_snapshot"] = dirtyBeforeFirst;
        row["dirty_after_first_snapshot"] = dirtyAfterFirst;
        row["dirty_after_second_snapshot"] = dirtyAfterSecond;
        row["first_snapshot"] = first;
        row["second_snapshot"] = second;
        row["snapshot_diff"] = SnapshotDiff(first, second);
        Require(dirtyBeforeFirst == dirtyAfterFirst && dirtyAfterSecond == dirtyAfterFirst,
            "Direct StrictSnapshot read changed the Work Part IsModified baseline after materialization.");
        Require(SameValue(first, second),
            "Two consecutive direct StrictSnapshot reads changed native model observations without Prepare.");
        row["passed"] = true;
    }

    private static void RunBodyMeasureReadEffectControl(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part, Dictionary<string, object> row)
    {
        row["materialization"] = Materialize(adapter, binding, part);
        Body body = null;
        foreach (Body candidate in part.Bodies.ToArray())
            if (candidate != null && candidate.IsSolidBody) { body = candidate; break; }
        Require(body != null, "Body measurement read-effect control requires the setup-only solid body.");

        bool dirtyBefore = part.IsModified;
        Dictionary<string, object> before = StrictSnapshot(part);
        bool dirtyAfterBaselineSnapshot = part.IsModified;
        Require(dirtyAfterBaselineSnapshot == dirtyBefore,
            "Direct snapshot changed dirty state before body measurement control.");

        MethodInfo measure = typeof(M2OpenModelAdapter).GetMethod("MeasureBody",
            BindingFlags.NonPublic | BindingFlags.Static);
        Require(measure != null, "Could not locate production MeasureBody(Part, Body) for the read-only control.");
        Exception invocationError = null;
        object measurement = null;
        try { measurement = measure.Invoke(null, new object[] { part, body }); }
        catch (TargetInvocationException ex) { invocationError = ex.InnerException ?? ex; }
        catch (Exception ex) { invocationError = ex; }
        bool dirtyImmediatelyAfterMeasure = part.IsModified;
        Dictionary<string, object> afterMeasureSnapshot = StrictSnapshot(part);
        bool dirtyAfterMeasureSnapshot = part.IsModified;
        row["helper"] = "M2OpenModelAdapter.MeasureBody(Part, Body)";
        row["body_tag"] = body.Tag.ToString();
        row["dirty_before_measure"] = dirtyBefore;
        row["dirty_after_baseline_snapshot"] = dirtyAfterBaselineSnapshot;
        row["dirty_immediately_after_measure"] = dirtyImmediatelyAfterMeasure;
        row["dirty_after_measure_snapshot"] = dirtyAfterMeasureSnapshot;
        row["measurement_result_type"] = measurement == null ? null : measurement.GetType().FullName;
        row["measurement_error"] = invocationError == null ? null : invocationError.ToString();
        row["native_before"] = before;
        row["native_after_measure"] = afterMeasureSnapshot;
        row["measure_snapshot_diff"] = SnapshotDiff(before, afterMeasureSnapshot);
        Require(invocationError == null && measurement != null,
            "Production MeasureBody read helper failed: " + (invocationError == null ? "null result" : invocationError.ToString()));
        Require(dirtyImmediatelyAfterMeasure == dirtyAfterBaselineSnapshot &&
            dirtyAfterMeasureSnapshot == dirtyImmediatelyAfterMeasure,
            "Production MeasureBody or its direct snapshot changed the materialized dirty baseline.");
        Require(SameValue(before, afterMeasureSnapshot), "Production MeasureBody read helper changed the direct native snapshot.");

        MethodInfo capture = typeof(M2OpenModelAdapter).GetMethod("CaptureM3BodyMeasures",
            BindingFlags.NonPublic | BindingFlags.Static);
        Require(capture != null, "Could not locate production CaptureM3BodyMeasures(Part) for the read-only control.");
        Exception captureError = null;
        object bodyInventory = null;
        try { bodyInventory = capture.Invoke(null, new object[] { part }); }
        catch (TargetInvocationException ex) { captureError = ex.InnerException ?? ex; }
        catch (Exception ex) { captureError = ex; }
        bool dirtyImmediatelyAfterInventory = part.IsModified;
        Dictionary<string, object> afterInventorySnapshot = StrictSnapshot(part);
        bool dirtyAfterInventorySnapshot = part.IsModified;
        row["aggregate_helper"] = "M2OpenModelAdapter.CaptureM3BodyMeasures(Part)";
        row["aggregate_result_type"] = bodyInventory == null ? null : bodyInventory.GetType().FullName;
        row["aggregate_result_count"] = bodyInventory is IList ? ((IList)bodyInventory).Count : (int?)null;
        row["aggregate_error"] = captureError == null ? null : captureError.ToString();
        row["dirty_immediately_after_aggregate_helper"] = dirtyImmediatelyAfterInventory;
        row["dirty_after_aggregate_snapshot"] = dirtyAfterInventorySnapshot;
        row["native_after_aggregate_helper"] = afterInventorySnapshot;
        row["aggregate_snapshot_diff"] = SnapshotDiff(before, afterInventorySnapshot);
        Require(captureError == null && bodyInventory != null,
            "Production CaptureM3BodyMeasures read helper failed: " + (captureError == null ? "null result" : captureError.ToString()));
        Require(dirtyImmediatelyAfterInventory == dirtyAfterMeasureSnapshot &&
            dirtyAfterInventorySnapshot == dirtyImmediatelyAfterInventory,
            "Production CaptureM3BodyMeasures or its direct snapshot changed the materialized dirty baseline.");
        Require(SameValue(before, afterInventorySnapshot),
            "Production CaptureM3BodyMeasures read helper changed the direct native snapshot.");

        MethodInfo requireState = typeof(M2OpenModelAdapter).GetMethod("RequireState",
            BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo captureMethod = null;
        foreach (MethodInfo candidate in typeof(M2OpenModelAdapter).GetMethods(
            BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!String.Equals(candidate.Name, "Capture", StringComparison.Ordinal)) continue;
            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length == 2 && parameters[0].ParameterType == typeof(M2ModelBinding))
            {
                captureMethod = candidate;
                break;
            }
        }
        Require(requireState != null && captureMethod != null,
            "Could not locate production binding/semantic Capture helpers for the read-only control.");
        object bindingState = requireState.Invoke(null, new object[] { binding });
        Require(bindingState != null && captureMethod.GetParameters()[1].ParameterType.IsInstanceOfType(bindingState),
            "Production RequireState returned an incompatible BindingState for Capture reflection.");
        bool dirtyBeforeCapture = part.IsModified;
        object captured = null;
        Exception captureException = null;
        try { captured = captureMethod.Invoke(adapter, new object[] { binding, bindingState }); }
        catch (TargetInvocationException ex) { captureException = ex.InnerException ?? ex; }
        catch (Exception ex) { captureException = ex; }
        bool dirtyImmediatelyAfterCapture = part.IsModified;
        Dictionary<string, object> afterCaptureSnapshot = StrictSnapshot(part);
        bool dirtyAfterCaptureSnapshot = part.IsModified;
        FieldInfo semanticSnapshotField = captured == null ? null : captured.GetType().GetField("Snapshot",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        FieldInfo readEffectsField = captured == null ? null : captured.GetType().GetField("ReadEffects",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Dictionary<string, object> semanticSnapshot = semanticSnapshotField == null || captured == null
            ? null : semanticSnapshotField.GetValue(captured) as Dictionary<string, object>;
        Dictionary<string, object> semanticReadEffects = readEffectsField == null || captured == null
            ? null : readEffectsField.GetValue(captured) as Dictionary<string, object>;
        row["semantic_capture_helper"] = "M2OpenModelAdapter.Capture(M2ModelBinding, BindingState)";
        row["dirty_before_semantic_capture"] = dirtyBeforeCapture;
        row["dirty_immediately_after_semantic_capture"] = dirtyImmediatelyAfterCapture;
        row["dirty_after_semantic_capture_snapshot"] = dirtyAfterCaptureSnapshot;
        row["semantic_capture_result_type"] = captured == null ? null : captured.GetType().FullName;
        row["semantic_capture_error"] = captureException == null ? null : captureException.ToString();
        row["semantic_capture_read_effects"] = semanticReadEffects;
        row["semantic_capture_snapshot"] = semanticSnapshot;
        row["native_after_semantic_capture"] = afterCaptureSnapshot;
        row["semantic_capture_snapshot_diff"] = SnapshotDiff(before, afterCaptureSnapshot);
        row["semantic_capture_non_dirty_snapshot_diff"] = SnapshotDiff(
            SnapshotWithoutDirtyState(before), SnapshotWithoutDirtyState(afterCaptureSnapshot));
        Require(captureException == null && captured != null,
            "Production semantic Capture helper failed: " + (captureException == null ? "null result" : captureException.ToString()));
        Require(ReadEffectsMatch(semanticReadEffects, dirtyBeforeCapture, dirtyImmediatelyAfterCapture),
            "Production semantic Capture read_effects do not match observed Part.IsModified state.");
        Require(dirtyAfterCaptureSnapshot == dirtyImmediatelyAfterCapture,
            "Direct post-Capture snapshot changed the Work Part dirty state.");
        Require(SameValue(SnapshotWithoutDirtyState(before), SnapshotWithoutDirtyState(afterCaptureSnapshot)),
            "Production semantic Capture changed direct native state other than dirty-state fields.");
        row["passed"] = true;
    }

    private static Arc AddNativeFullCircleArc(Session session, Part part, Fixture fixture,
        double centerX, double centerY, double radius)
    {
        Sketch sketch = FindSketch(part, fixture.SketchId);
        Point3d center = WorldPoint(fixture.Frame, centerX, centerY);
        Vector3d x = ToVector(fixture.Frame.XAxis);
        Vector3d y = ToVector(fixture.Frame.YAxis);
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F09 native full Arc fixture");
        bool active = false;
        try
        {
            sketch.Activate(Sketch.ViewReorient.False);
            active = true;
            Arc arc = part.Curves.CreateArc(center, x, y, radius, 0.0, TwoPi);
            Require(arc != null && arc.GetType() == typeof(Arc),
                "NX did not create the intended full Circle-as-Arc native object.");
            sketch.AddGeometry(arc, Sketch.InferConstraintsOption.InferNoConstraints);
            sketch.Update();
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            active = false;
            Require(session.UpdateManager.DoUpdate(mark) == 0, "Native full Arc fixture update reported errors.");
            CurveModel model = CaptureCurve(arc, fixture.Frame);
            Require(model.IsFullCircle && Math.Abs(model.Sweep - TwoPi) <= AngleToleranceRad,
                "Native full Arc fixture does not expose a complete 2 pi interval.");
            return arc;
        }
        finally
        {
            if (active) sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            session.DeleteUndoMark(mark, "F09 native full Arc fixture");
        }
    }

    private static M3SketchPatternRequest LinearRequest(M2ModelBinding binding, Fixture fixture,
        string[] sourceIds, double[] direction, double spacing, int count)
    {
        return new M3SketchPatternRequest
        {
            RevisionId = binding.RevisionId, SketchId = fixture.SketchId, Mode = "linear",
            SourceCurveIds = ResolveSourceIds(fixture, sourceIds),
            DirectionXY = (double[])direction.Clone(), SpacingMm = spacing, InstanceCount = count
        };
    }

    private static M3SketchPatternRequest RectangularRequest(M2ModelBinding binding, Fixture fixture,
        string[] sourceIds, double[] directionX, double[] directionY,
        double spacingX, double spacingY, int countX, int countY)
    {
        return new M3SketchPatternRequest
        {
            RevisionId = binding.RevisionId, SketchId = fixture.SketchId, Mode = "rectangular",
            SourceCurveIds = ResolveSourceIds(fixture, sourceIds),
            DirectionXXY = (double[])directionX.Clone(), DirectionYXY = (double[])directionY.Clone(),
            SpacingXmm = spacingX, SpacingYmm = spacingY, CountX = countX, CountY = countY
        };
    }

    private static M3SketchPatternRequest CircularRequest(M2ModelBinding binding, Fixture fixture,
        string[] sourceIds, double[] center, double angleStepDeg, int count)
    {
        return new M3SketchPatternRequest
        {
            RevisionId = binding.RevisionId, SketchId = fixture.SketchId, Mode = "circular",
            SourceCurveIds = ResolveSourceIds(fixture, sourceIds),
            CenterXY = (double[])center.Clone(), AngleStepDeg = angleStepDeg, InstanceCount = count
        };
    }

    private static M3SketchPatternRequest MirrorRequest(M2ModelBinding binding, Fixture fixture,
        string[] sourceIds, string axisId)
    {
        return new M3SketchPatternRequest
        {
            RevisionId = binding.RevisionId, SketchId = fixture.SketchId, Mode = "mirror",
            SourceCurveIds = ResolveSourceIds(fixture, sourceIds),
            AxisCurveId = ResolveGeometryId(fixture, axisId)
        };
    }

    private static M3SketchPatternRequest PatternRequestFor(M2ModelBinding binding, Fixture fixture,
        string mode, string[] sourceIds)
    {
        M3SketchPatternRequest request = new M3SketchPatternRequest
        {
            RevisionId = binding.RevisionId, SketchId = fixture.SketchId, Mode = mode,
            SourceCurveIds = ResolveSourceIds(fixture, sourceIds)
        };
        if (mode == "linear") { request.DirectionXY = new double[] { 1.0, 0.0 }; request.SpacingMm = 3.0; request.InstanceCount = 2; }
        return request;
    }

    private static List<string> ResolveSourceIds(Fixture fixture, string[] requested)
    {
        List<string> result = new List<string>();
        foreach (string item in requested)
            result.Add(ResolveGeometryId(fixture, item));
        return result;
    }

    private static string ResolveGeometryId(Fixture fixture, string identifierOrSeedName)
    {
        Curve curve;
        if (fixture != null && fixture.Curves.TryGetValue(identifierOrSeedName, out curve))
        {
            Require(curve != null, "Fixture seed-name lookup returned a null native curve: " + identifierOrSeedName);
            return curve.JournalIdentifier;
        }
        // Explicit JournalIdentifiers and invalid identifiers pass through unchanged.
        return identifierOrSeedName;
    }

    private static List<ExpectedInstance> BuildExpectedInstances(Fixture fixture,
        M3SketchPatternRequest request)
    {
        List<PatternTransform> positions = BuildTransforms(fixture, request);
        List<ExpectedInstance> result = new List<ExpectedInstance>();
        foreach (string sourceId in request.SourceCurveIds)
        {
            Curve sourceCurve = FindCurveByJournalId(FindSketchFromFixture(fixture), sourceId);
            Require(sourceCurve != null, "Request source ID did not resolve directly in target Sketch.");
            CurveModel source = CaptureCurve(sourceCurve, fixture.Frame);
            foreach (PatternTransform transform in positions)
            {
                result.Add(new ExpectedInstance
                {
                    SourceCurveId = sourceId,
                    PositionIndex = transform.PositionIndex,
                    PositionXIndex = transform.PositionXIndex,
                    PositionYIndex = transform.PositionYIndex,
                    Geometry = TransformCurve(source, transform)
                });
            }
        }
        return result;
    }

    private static List<PatternTransform> BuildTransforms(Fixture fixture, M3SketchPatternRequest request)
    {
        List<PatternTransform> result = new List<PatternTransform>();
        if (request.Mode == "linear")
        {
            double[] direction = Normalize2(request.DirectionXY);
            for (int i = 0; i < request.InstanceCount; i++)
                result.Add(new PatternTransform
                {
                    Kind = "translation", PositionIndex = i,
                    Tx = direction[0] * request.SpacingMm * i,
                    Ty = direction[1] * request.SpacingMm * i
                });
        }
        else if (request.Mode == "rectangular")
        {
            double[] dx = Normalize2(request.DirectionXXY);
            double[] dy = Normalize2(request.DirectionYXY);
            for (int ix = 0; ix < request.CountX; ix++)
                for (int iy = 0; iy < request.CountY; iy++)
                    result.Add(new PatternTransform
                    {
                        Kind = "translation",
                        PositionIndex = iy * request.CountX + ix,
                        PositionXIndex = ix, PositionYIndex = iy,
                        Tx = dx[0] * request.SpacingXmm * ix + dy[0] * request.SpacingYmm * iy,
                        Ty = dx[1] * request.SpacingXmm * ix + dy[1] * request.SpacingYmm * iy
                    });
        }
        else if (request.Mode == "circular")
        {
            for (int i = 0; i < request.InstanceCount; i++)
            {
                double angle = (request.AngleStepDeg % 360.0) * Pi / 180.0 * i;
                double c = Math.Cos(angle);
                double s = Math.Sin(angle);
                result.Add(new PatternTransform
                {
                    Kind = "rotation", PositionIndex = i, M11 = c, M12 = -s, M21 = s, M22 = c,
                    Tx = request.CenterXY[0] * (1.0 - c) + request.CenterXY[1] * s,
                    Ty = request.CenterXY[1] * (1.0 - c) - request.CenterXY[0] * s
                });
            }
        }
        else if (request.Mode == "mirror")
        {
            result.Add(new PatternTransform { Kind = "identity", PositionIndex = 0 });
            Line axis = FindCurveByJournalId(FindSketchFromFixture(fixture), request.AxisCurveId) as Line;
            Require(axis != null, "Mirror axis request did not resolve to a native Line.");
            double[] a = WorldToLocal(fixture.Frame, axis.StartPoint);
            double[] b = WorldToLocal(fixture.Frame, axis.EndPoint);
            double[] d = Normalize2(new double[] { b[0] - a[0], b[1] - a[1] });
            result.Add(new PatternTransform
            {
                Kind = "reflection", PositionIndex = 1,
                M11 = 2.0 * d[0] * d[0] - 1.0, M12 = 2.0 * d[0] * d[1],
                M21 = 2.0 * d[0] * d[1], M22 = 2.0 * d[1] * d[1] - 1.0,
                Tx = a[0] - (2.0 * d[0] * (a[0] * d[0] + a[1] * d[1]) - a[0]),
                Ty = a[1] - (2.0 * d[1] * (a[0] * d[0] + a[1] * d[1]) - a[1])
            });
            // Keep the explicit axis point/direction for the affine reflection.
            result[1].Tx = a[0] - result[1].M11 * a[0] - result[1].M12 * a[1];
            result[1].Ty = a[1] - result[1].M21 * a[0] - result[1].M22 * a[1];
        }
        else throw new InvalidOperationException("Independent oracle does not support mode " + request.Mode);
        return result;
    }

    private static CurveModel TransformCurve(CurveModel source, PatternTransform transform)
    {
        CurveModel result = new CurveModel
        {
            Type = source.Type, IsArc = source.IsArc, IsFullCircle = source.IsFullCircle,
            Radius = source.Radius, StartAngle = source.StartAngle, Sweep = source.Sweep,
            LengthMm = source.LengthMm
        };
        result.Start = TransformPoint(source.Start, transform);
        result.End = TransformPoint(source.End, transform);
        if (source.IsArc)
        {
            result.Center = TransformPoint(source.Center, transform);
            result.AxisX = TransformVector(source.AxisX, transform);
            result.AxisY = TransformVector(source.AxisY, transform);
        }
        return result;
    }

    private static double[] TransformPoint(double[] point, PatternTransform transform)
    {
        return new double[] {
            transform.M11 * point[0] + transform.M12 * point[1] + transform.Tx,
            transform.M21 * point[0] + transform.M22 * point[1] + transform.Ty
        };
    }

    private static double[] TransformVector(double[] vector, PatternTransform transform)
    {
        return new double[] {
            transform.M11 * vector[0] + transform.M12 * vector[1],
            transform.M21 * vector[0] + transform.M22 * vector[1]
        };
    }

    private static bool AugmentMatch(int expected, bool[,] edges, int[] expectedToActual,
        int[] actualToExpected, bool[] seen)
    {
        for (int actual = 0; actual < actualToExpected.Length; actual++)
        {
            if (!edges[expected, actual] || seen[actual]) continue;
            seen[actual] = true;
            if (actualToExpected[actual] < 0 ||
                AugmentMatch(actualToExpected[actual], edges, expectedToActual, actualToExpected, seen))
            {
                actualToExpected[actual] = expected;
                expectedToActual[expected] = actual;
                return true;
            }
        }
        return false;
    }

    private static bool CurvesMatch(CurveModel expected, CurveModel actual)
    {
        if (expected == null || actual == null || expected.IsArc != actual.IsArc ||
            !String.Equals(expected.Type, actual.Type, StringComparison.Ordinal)) return false;
        if (!expected.IsArc)
        {
            double direct = Math.Max(Distance2(expected.Start, actual.Start), Distance2(expected.End, actual.End));
            double reverse = Math.Max(Distance2(expected.Start, actual.End), Distance2(expected.End, actual.Start));
            return Math.Min(direct, reverse) <= PositionToleranceMm &&
                Math.Abs(expected.LengthMm - actual.LengthMm) <= PositionToleranceMm;
        }
        if (Distance2(expected.Center, actual.Center) > PositionToleranceMm ||
            Math.Abs(expected.Radius - actual.Radius) > PositionToleranceMm ||
            expected.IsFullCircle != actual.IsFullCircle) return false;
        bool full = expected.IsFullCircle;
        if (!full && (Distance2(expected.Start, actual.Start) > PositionToleranceMm ||
            Distance2(expected.End, actual.End) > PositionToleranceMm ||
            Math.Abs(expected.Sweep - actual.Sweep) > AngleToleranceRad)) return false;
        double[] expectedMid = ArcPoint(expected.Center, expected.AxisX, expected.AxisY,
            expected.Radius, expected.StartAngle + expected.Sweep * 0.5);
        double[] actualMid = ArcPoint(actual.Center, actual.AxisX, actual.AxisY,
            actual.Radius, actual.StartAngle + actual.Sweep * 0.5);
        if (Distance2(expectedMid, actualMid) > PositionToleranceMm) return false;
        if (Distance2(expected.AxisX, actual.AxisX) > VectorTolerance ||
            Distance2(expected.AxisY, actual.AxisY) > VectorTolerance) return false;
        double[] expectedTangentStart = ArcTangent(expected.AxisX, expected.AxisY, expected.StartAngle, expected.Sweep);
        double[] actualTangentStart = ArcTangent(actual.AxisX, actual.AxisY, actual.StartAngle, actual.Sweep);
        double[] expectedTangentEnd = ArcTangent(expected.AxisX, expected.AxisY,
            expected.StartAngle + expected.Sweep, expected.Sweep);
        double[] actualTangentEnd = ArcTangent(actual.AxisX, actual.AxisY,
            actual.StartAngle + actual.Sweep, actual.Sweep);
        if (Distance2(expectedTangentStart, actualTangentStart) > VectorTolerance ||
            Distance2(expectedTangentEnd, actualTangentEnd) > VectorTolerance) return false;
        double expectedNormalSign = Cross2(expected.AxisX, expected.AxisY);
        double actualNormalSign = Cross2(actual.AxisX, actual.AxisY);
        if (Math.Abs(expectedNormalSign - actualNormalSign) > VectorTolerance) return false;
        double expectedLength = expected.Radius * Math.Abs(expected.Sweep);
        double actualLength = actual.Radius * Math.Abs(actual.Sweep);
        if (Math.Abs(expected.LengthMm - expectedLength) > PositionToleranceMm ||
            Math.Abs(actual.LengthMm - actualLength) > PositionToleranceMm ||
            Math.Abs(expectedLength - actualLength) > PositionToleranceMm) return false;
        return true;
    }

    private static bool IsSolverAcceptable(string status)
    {
        return String.Equals(status, "UnderConstrained", StringComparison.Ordinal) ||
            String.Equals(status, "WellConstrained", StringComparison.Ordinal);
    }

    private static bool SolverStatusPreserved(Dictionary<string, object> before,
        Dictionary<string, object> after, string targetSketchId)
    {
        Dictionary<string, object> preSketch = FindSketchRecord(before, targetSketchId);
        Dictionary<string, object> postSketch = FindSketchRecord(after, targetSketchId);
        if (preSketch == null || postSketch == null) return false;
        string pre = GetString(preSketch, "solver_status");
        string post = GetString(postSketch, "solver_status");
        int preDof = Convert.ToInt32(preSketch["degrees_of_freedom"], CultureInfo.InvariantCulture);
        int postDof = Convert.ToInt32(postSketch["degrees_of_freedom"], CultureInfo.InvariantCulture);
        return IsSolverAcceptable(pre) && IsSolverAcceptable(post) &&
            String.Equals(pre, post, StringComparison.Ordinal) && preDof >= 0 && postDof >= preDof;
    }

    private static List<object> SolverRecords(Dictionary<string, object> before,
        Dictionary<string, object> after, string targetSketchId)
    {
        return new List<object> {
            FindSketchRecord(before, targetSketchId),
            FindSketchRecord(after, targetSketchId)
        };
    }

    private static bool ExistingConstraintInventoryPreserved(Dictionary<string, object> before,
        Dictionary<string, object> after)
    {
        Dictionary<string, object> b = ReadDictionary(before, "constraints_by_tag_all");
        Dictionary<string, object> a = ReadDictionary(after, "constraints_by_tag_all");
        if (b == null || a == null) return false;
        foreach (KeyValuePair<string, object> pair in b)
            if (!a.ContainsKey(pair.Key) || !SameTagMatchedNativeProjection(pair.Value, a[pair.Key])) return false;
        return true;
    }

    private static bool ExistingReferenceStatusPreserved(Dictionary<string, object> before,
        Dictionary<string, object> after, string targetSketchTag, List<Curve> outputs)
    {
        Dictionary<string, object> b = ReadDictionary(before, "reference_status_by_tag_all");
        Dictionary<string, object> a = ReadDictionary(after, "reference_status_by_tag_all");
        if (b == null || a == null || String.IsNullOrWhiteSpace(targetSketchTag) || outputs == null) return false;
        foreach (KeyValuePair<string, object> pair in b)
            if (!a.ContainsKey(pair.Key) || !SameValue(pair.Value, a[pair.Key])) return false;

        HashSet<string> expectedNewKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Curve output in outputs)
        {
            if (output == null) return false;
            string key = targetSketchTag + "|" + output.Tag.ToString();
            if (b.ContainsKey(key) || !expectedNewKeys.Add(key)) return false;
            object status;
            if (!a.TryGetValue(key, out status) || !Object.Equals(status, 1)) return false;
        }
        foreach (string key in a.Keys)
            if (!b.ContainsKey(key) && !expectedNewKeys.Contains(key)) return false;
        return expectedNewKeys.Count == outputs.Count && a.Count == b.Count + outputs.Count;
    }

    private static bool ExistingSketchFramesPreserved(Dictionary<string, object> before,
        Dictionary<string, object> after)
    {
        Dictionary<string, object> b = ReadDictionary(before, "sketches_by_tag");
        Dictionary<string, object> a = ReadDictionary(after, "sketches_by_tag");
        if (b == null || a == null) return false;
        foreach (KeyValuePair<string, object> pair in b)
        {
            Dictionary<string, object> pre = pair.Value as Dictionary<string, object>;
            Dictionary<string, object> post = a.ContainsKey(pair.Key) ? a[pair.Key] as Dictionary<string, object> : null;
            if (pre == null || post == null || !SameValue(pre["frame"], post["frame"])) return false;
        }
        return true;
    }

    private static M3SketchRequest GeometryOnly(List<M3SketchGeometryRequest> geometries)
    {
        return new M3SketchRequest
        {
            ConstraintMode = "geometry_only",
            Geometries = geometries
        };
    }

    private static M3SketchGeometryRequest Line(string id, double x1, double y1, double x2, double y2)
    {
        return new M3SketchGeometryRequest
        {
            Id = id, Type = "line",
            StartMm = new double[] { x1, y1 }, EndMm = new double[] { x2, y2 }
        };
    }

    private static M3SketchGeometryRequest Arc(string id, double x, double y,
        double radius, double startDeg, double sweepDeg)
    {
        return new M3SketchGeometryRequest
        {
            Id = id, Type = "arc", CenterMm = new double[] { x, y },
            RadiusMm = radius, StartAngleDeg = startDeg, SweepDeg = sweepDeg
        };
    }

    private static M3SketchGeometryRequest Circle(string id, double x, double y, double diameter)
    {
        return new M3SketchGeometryRequest
        {
            Id = id, Type = "circle", CenterMm = new double[] { x, y }, DiameterMm = diameter
        };
    }

    private static M3SketchGeometryRequest Ellipse(string id, double x, double y,
        double majorRadius, double minorRadius)
    {
        return new M3SketchGeometryRequest
        {
            Id = id, Type = "ellipse", CenterMm = new double[] { x, y },
            MajorRadiusMm = majorRadius, MinorRadiusMm = minorRadius, RotationDeg = 0.0
        };
    }

    private static M3SketchGeometryRequest Spline(string id, double[][] points)
    {
        return new M3SketchGeometryRequest
        {
            Id = id, Type = "spline", InterpolationPointsMm = points,
            Degree = 3, EndpointCondition = "unconstrained"
        };
    }

    private static Dictionary<string, object> SketchRequestRecord(M3SketchRequest request)
    {
        List<object> geometries = new List<object>();
        if (request.Geometries != null)
            foreach (M3SketchGeometryRequest item in request.Geometries)
                geometries.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "id", item.Id }, { "type", item.Type },
                    { "start_mm", NumberArrayRecord(item.StartMm) }, { "end_mm", NumberArrayRecord(item.EndMm) },
                    { "center_mm", NumberArrayRecord(item.CenterMm) },
                    { "radius_mm", NumberOrText(item.RadiusMm) },
                    { "diameter_mm", NumberOrText(item.DiameterMm) },
                    { "major_radius_mm", NumberOrText(item.MajorRadiusMm) },
                    { "minor_radius_mm", NumberOrText(item.MinorRadiusMm) },
                    { "start_angle_deg", NumberOrText(item.StartAngleDeg) },
                    { "sweep_deg", NumberOrText(item.SweepDeg) },
                    { "rotation_deg", NumberOrText(item.RotationDeg) },
                    { "degree", item.Degree }, { "endpoint_condition", item.EndpointCondition },
                    { "interpolation_points_mm", NestedNumberArrayRecord(item.InterpolationPointsMm) }
                });
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", request.RevisionId }, { "plane", request.Plane },
            { "origin_mm", NumberArrayRecord(request.OriginMm) },
            { "normal", NumberArrayRecord(request.Normal) }, { "x_axis", NumberArrayRecord(request.XAxis) },
            { "constraint_mode", request.ConstraintMode }, { "geometries", geometries }
        };
    }

    private static Dictionary<string, object> PatternRequestRecord(M3SketchPatternRequest request)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", request.RevisionId }, { "sketch_id", request.SketchId },
            { "mode", request.Mode }, { "source_curve_ids", request.SourceCurveIds == null ? null : new List<string>(request.SourceCurveIds) },
            { "direction_xy", NumberArrayRecord(request.DirectionXY) },
            { "spacing_mm", NumberOrText(request.SpacingMm) }, { "instance_count", request.InstanceCount },
            { "direction_x_xy", NumberArrayRecord(request.DirectionXXY) },
            { "direction_y_xy", NumberArrayRecord(request.DirectionYXY) },
            { "spacing_x_mm", NumberOrText(request.SpacingXmm) },
            { "spacing_y_mm", NumberOrText(request.SpacingYmm) },
            { "count_x", request.CountX }, { "count_y", request.CountY },
            { "center_xy", NumberArrayRecord(request.CenterXY) },
            { "angle_step_deg", NumberOrText(request.AngleStepDeg) },
            { "axis_curve_id", request.AxisCurveId }
        };
    }

    private static List<object> ExpectedInstanceRecords(List<ExpectedInstance> instances)
    {
        List<object> records = new List<object>();
        foreach (ExpectedInstance item in instances)
            records.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "source_curve_id", item.SourceCurveId },
                { "position_index", item.PositionIndex },
                { "position_x_index", item.PositionXIndex },
                { "position_y_index", item.PositionYIndex },
                { "expected_curve", CurveModelRecord(item.Geometry) }
            });
        return records;
    }

    private static int ExpectedPositionCount(M3SketchPatternRequest request)
    {
        if (request.Mode == "linear" || request.Mode == "circular") return request.InstanceCount;
        if (request.Mode == "rectangular") return request.CountX * request.CountY;
        if (request.Mode == "mirror") return 2;
        return 0;
    }

    private static List<string> ReadExecutionOutputTags(M3ExecutionResult execution)
    {
        object value = ReadNested(execution == null ? null : execution.Result, "output_curve_tags");
        return ToStringList(value);
    }

    private static Dictionary<string, object> ExecutionRecord(M3ExecutionResult execution)
    {
        if (execution == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "state", execution.State }, { "revision_id", execution.RevisionId },
            { "result", execution.Result }, { "error_code", execution.ErrorCode },
            { "error_message", execution.ErrorMessage }
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
            { "state", result.State }, { "reopened", result.Reopened }, { "checks", result.Checks },
            { "part_path", result.PartPath }, { "error_code", result.ErrorCode },
            { "error_message", result.ErrorMessage }, { "evidence", result.Evidence }
        };
    }

    private static bool ChecksPass(Dictionary<string, object> checks)
    {
        if (checks == null || checks.Count == 0) return false;
        foreach (KeyValuePair<string, object> item in checks)
        {
            Dictionary<string, object> check = item.Value as Dictionary<string, object>;
            if (check == null || !String.Equals(GetString(check, "status"), "VERIFIED", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static Dictionary<string, object> AssemblyRecord(Assembly assembly)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "full_name", assembly.FullName }, { "location", assembly.Location },
            { "version", assembly.GetName().Version == null ? null : assembly.GetName().Version.ToString() }
        };
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

    private static void RequireNewPath(string path, string description)
    {
        Require(!File.Exists(path) && !Directory.Exists(path),
            "The unique " + description + " path already exists; refusing to overwrite it.");
    }

    private static void Persist(string path, Dictionary<string, object> report)
    {
        File.WriteAllText(path, M2Json.CanonicalText(report), new UTF8Encoding(false));
    }

    private static Dictionary<string, object> StrictSnapshot(Part part)
    {
        Require(part != null, "Direct native snapshot requires a non-null Part.");
        bool isModifiedBeforeSnapshot = part.IsModified;
        Dictionary<string, object> sketches = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> constraintsAll = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> referencesAll = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (Sketch sketch in part.Sketches.ToArray())
        {
            Require(sketch != null, "Part Sketch inventory contains null.");
            Dictionary<string, object> record = StrictSketchRecord(sketch);
            sketches[sketch.Tag.ToString()] = record;
            Dictionary<string, object> constraints = ReadDictionary(record, "constraints_by_tag");
            foreach (KeyValuePair<string, object> pair in constraints)
                constraintsAll[sketch.Tag.ToString() + "|" + pair.Key] = pair.Value;
            Dictionary<string, object> references = ReadDictionary(record, "reference_status_by_tag");
            foreach (KeyValuePair<string, object> pair in references)
                referencesAll[sketch.Tag.ToString() + "|" + pair.Key] = pair.Value;
        }
        List<object> history = new List<object>();
        Feature[] features = part.Features.GetFeatures();
        Require(features != null, "Part feature history returned null.");
        foreach (Feature feature in features)
        {
            Require(feature != null, "Part feature history contains null.");
            history.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", feature.Tag.ToString() }, { "journal_id", feature.JournalIdentifier },
                { "feature_type", feature.FeatureType }, { "suppressed", feature.Suppressed },
                { "name", feature.Name }
            });
        }
        Dictionary<string, object> bodies = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (Body body in part.Bodies.ToArray())
        {
            Require(body != null, "Part body inventory contains null.");
            double[] bbox = new double[6];
            UFSession.GetUFSession().Modl.AskBoundingBox(body.Tag, bbox);
            List<string> faces = new List<string>();
            foreach (Face face in body.GetFaces()) faces.Add(face.Tag.ToString());
            List<string> edges = new List<string>();
            foreach (Edge edge in body.GetEdges()) edges.Add(edge.Tag.ToString());
            faces.Sort(StringComparer.Ordinal);
            edges.Sort(StringComparer.Ordinal);
            bodies[body.Tag.ToString()] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", body.Tag.ToString() }, { "journal_id", body.JournalIdentifier },
                { "solid", body.IsSolidBody }, { "bounding_box_mm", bbox },
                { "face_tags", faces }, { "edge_tags", edges }
            };
        }
        List<string> globalCurves = new List<string>();
        foreach (Curve curve in part.Curves)
            if (curve != null) globalCurves.Add(curve.Tag.ToString());
        globalCurves.Sort(StringComparer.Ordinal);
        Dictionary<string, object> pointInventory = GlobalPointInventory(part);
        bool isModifiedAfterSnapshot = part.IsModified;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "part_tag", part.Tag.ToString() }, { "part_path", part.FullPath },
            { "part_units", part.PartUnits.ToString() }, { "is_modified_before_snapshot", isModifiedBeforeSnapshot },
            { "is_modified_after_snapshot", isModifiedAfterSnapshot }, { "is_modified", isModifiedAfterSnapshot },
            { "active_sketch_tag", Session.GetSession().ActiveSketch == null ? null : Session.GetSession().ActiveSketch.Tag.ToString() },
            { "sketches_by_tag", sketches }, { "constraints_by_tag_all", constraintsAll },
            { "reference_status_by_tag_all", referencesAll },
            { "features_in_history_order", history }, { "bodies_by_tag", bodies },
            { "global_curve_tags", globalCurves },
            { "global_point_tags", pointInventory["tags"] },
            { "global_point_records", pointInventory["records"] }
        };
    }

    private static Dictionary<string, object> StrictSketchRecord(Sketch sketch)
    {
        Frame frame = FrameFromSketch(sketch);
        Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> allObjects = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> references = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Require(item != null, "Sketch geometry list contains null.");
            Curve curve = item as Curve;
            if (curve != null)
            {
                Dictionary<string, object> curveRecord = NativeCurveRecord(curve, frame);
                geometry[curve.Tag.ToString()] = curveRecord;
                allObjects[curve.Tag.ToString()] = curveRecord;
                UFSket.ReferenceStatus status;
                UFSession.GetUFSession().Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status);
                references[curve.Tag.ToString()] = (int)status;
            }
            else
            {
                NXOpen.Point point = item as NXOpen.Point;
                Dictionary<string, object> objectRecord = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", item.Tag.ToString() }, { "journal_id", item.JournalIdentifier },
                    { "object_type", item.GetType().Name }, { "full_type", item.GetType().FullName },
                    { "coordinates_mm", point == null ? null : Point(point.Coordinates) }
                };
                allObjects[item.Tag.ToString()] = objectRecord;
            }
        }
        int dof;
        Sketch.Status solver = sketch.GetStatus(out dof);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", sketch.Tag.ToString() }, { "journal_id", sketch.JournalIdentifier },
            { "feature_tag", sketch.Feature == null ? null : sketch.Feature.Tag.ToString() },
            { "feature_journal_id", sketch.Feature == null ? null : sketch.Feature.JournalIdentifier },
            { "frame", FrameRecord(frame) }, { "origin_mm", (double[])frame.Origin.Clone() },
            { "geometry_by_tag", geometry }, { "constraints_by_tag", NativeConstraintRecords(sketch) },
            { "all_objects_by_tag", allObjects },
            { "reference_status_by_tag", references },
            { "solver_status", solver.ToString() }, { "degrees_of_freedom", dof }
        };
    }

    private static Dictionary<string, object> GlobalPointInventory(Part part)
    {
        List<string> tags = new List<string>();
        List<object> records = new List<object>();
        PropertyInfo property = part.GetType().GetProperty("Points",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null)
        {
            object collection = property.GetValue(part, null);
            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable != null)
                foreach (object item in enumerable)
                {
                    NXOpen.Point point = item as NXOpen.Point;
                    if (point == null) continue;
                    tags.Add(point.Tag.ToString());
                    records.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "tag", point.Tag.ToString() }, { "journal_id", point.JournalIdentifier },
                        { "coordinates_mm", Point(point.Coordinates) }
                    });
                }
        }
        tags.Sort(StringComparer.Ordinal);
        records.Sort(delegate(object left, object right)
        {
            return StringComparer.Ordinal.Compare(
                GetString(left as Dictionary<string, object>, "tag"),
                GetString(right as Dictionary<string, object>, "tag"));
        });
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tags", tags }, { "records", records }, { "inventory_property_available", property != null }
        };
    }

    private static Dictionary<string, object> NativeCurveRecord(Curve curve, Frame frame)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", curve.Tag.ToString() }, { "journal_id", curve.JournalIdentifier },
            { "object_type", curve.GetType().Name }, { "full_type", curve.GetType().FullName },
            { "native_length_mm", curve.GetLength() }
        };
        Line line = curve as Line;
        Arc arc = curve as Arc;
        if (line != null)
        {
            CurveModel model = CaptureCurve(line, frame);
            AddRecordPrefix(record, "model_", CurveModelRecord(model));
            record["start_world_mm"] = Point(line.StartPoint);
            record["end_world_mm"] = Point(line.EndPoint);
            return record;
        }
        if (arc != null)
        {
            CurveModel model = CaptureCurve(arc, frame);
            AddRecordPrefix(record, "model_", CurveModelRecord(model));
            record["center_world_mm"] = Point(arc.CenterPoint);
            Point3d orientationCenter;
            Vector3d x;
            Vector3d y;
            arc.GetOrientation(out orientationCenter, out x, out y);
            record["orientation_center_world_mm"] = Point(orientationCenter);
            record["plane_x_axis_world"] = Vector(x);
            record["plane_y_axis_world"] = Vector(y);
            record["start_angle_rad"] = arc.StartAngle;
            record["end_angle_rad"] = arc.EndAngle;
            return record;
        }
        Ellipse ellipse = curve as Ellipse;
        if (ellipse != null)
        {
            double major, minor, start, end, rotation;
            Point3d center;
            ellipse.GetParameters(out major, out minor, out start, out end, out rotation, out center);
            Point3d orientationCenter;
            Vector3d x;
            Vector3d y;
            ellipse.GetOrientation(out orientationCenter, out x, out y);
            record["center_world_mm"] = Point(center);
            record["major_radius_mm"] = major;
            record["minor_radius_mm"] = minor;
            record["start_angle_rad"] = start;
            record["end_angle_rad"] = end;
            record["rotation_rad"] = rotation;
            record["plane_x_axis_world"] = Vector(x);
            record["plane_y_axis_world"] = Vector(y);
            return record;
        }
        Spline spline = curve as Spline;
        if (spline != null)
        {
            UFCurve.Spline definition;
            UFSession.GetUFSession().Curve.AskSplineData(spline.Tag, out definition);
            record["spline_degree"] = definition.order - 1;
            record["spline_pole_count"] = definition.num_poles;
            record["spline_knot_count"] = definition.knots == null ? 0 : definition.knots.Length;
            record["spline_rational"] = definition.is_rational;
            record["spline_knots"] = definition.knots == null ? null : (object)definition.knots;
            List<object> poles = new List<object>();
            if (definition.poles != null)
                for (int i = 0; i < definition.poles.GetLength(0); i++)
                {
                    List<object> pole = new List<object>();
                    for (int j = 0; j < definition.poles.GetLength(1); j++) pole.Add(definition.poles[i, j]);
                    poles.Add(pole);
                }
            record["spline_poles"] = poles;
        }
        return record;
    }

    private static Dictionary<string, object> NativeConstraintRecords(Sketch sketch)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] values = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
            if (values == null) continue;
            foreach (SketchConstraint constraint in values)
            {
                Require(constraint != null, "Sketch constraint list contains null.");
                Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", constraint.Tag.ToString() }, { "journal_id", constraint.JournalIdentifier },
                    { "constraint_type", type.ToString() }, { "class", constraint.GetType().Name }
                };
                List<object> operands = new List<object>();
                row["operands"] = operands;
                SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                if (dimension != null)
                {
                    Expression expression = dimension.AssociatedExpression;
                    row["dimension_state"] = dimension.DimensionState.ToString();
                    row["rhs"] = expression == null ? null : expression.RightHandSide;
                    row["value"] = expression == null ? null : (object)expression.Value;
                    row["unit"] = expression == null || expression.Units == null ? null : expression.Units.Symbol;
                    row["expression_name"] = expression == null ? null : expression.Name;
                    Sketch.DimensionGeometry[] dimensionOperands = dimension.GetDimensionGeometry();
                    if (dimensionOperands != null)
                        foreach (Sketch.DimensionGeometry operand in dimensionOperands)
                            operands.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                { "geometry_tag", operand.Geometry == null ? null : operand.Geometry.Tag.ToString() },
                                { "journal_id", operand.Geometry == null ? null : operand.Geometry.JournalIdentifier },
                                { "association", operand.AssocType.ToString() },
                                { "association_value", operand.AssocValue }
                            });
                }
                else
                {
                    SketchGeometricConstraint geometric = constraint as SketchGeometricConstraint;
                    if (geometric != null)
                    {
                        Sketch.ConstraintGeometry[] geometry = geometric.GetGeometry();
                        if (geometry != null)
                            foreach (Sketch.ConstraintGeometry operand in geometry)
                                operands.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                                {
                                    { "geometry_tag", operand.Geometry == null ? null : operand.Geometry.Tag.ToString() },
                                    { "journal_id", operand.Geometry == null ? null : operand.Geometry.JournalIdentifier },
                                    { "point_type", operand.PointType.ToString() },
                                    { "spline_point_index", operand.SplineDefiningPointIndex }
                                });
                    }
                }
                result[constraint.Tag.ToString()] = row;
            }
        }
        return result;
    }

    private static void AddRecordPrefix(Dictionary<string, object> target, string prefix,
        Dictionary<string, object> source)
    {
        foreach (KeyValuePair<string, object> pair in source) target[prefix + pair.Key] = pair.Value;
    }

    private static List<object> CurveRecords(List<Curve> curves, Frame frame)
    {
        List<object> records = new List<object>();
        foreach (Curve curve in curves) records.Add(NativeCurveRecord(curve, frame));
        return records;
    }

    private static List<Curve> CurvesForSketch(Fixture fixture)
    {
        return SketchCurves(FindSketchFromFixture(fixture));
    }

    private static List<Curve> SketchCurves(Sketch sketch)
    {
        List<Curve> result = new List<Curve>();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve;
            if (curve != null) result.Add(curve);
        }
        return result;
    }

    private static HashSet<string> CurveTagSet(List<Curve> curves)
    {
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Curve curve in curves)
            Require(curve != null && tags.Add(curve.Tag.ToString()), "Native curve inventory has null or reused Tag.");
        return tags;
    }

    private static bool UniqueTags(List<Curve> curves)
    {
        if (curves == null) return false;
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Curve curve in curves)
            if (curve == null || String.IsNullOrWhiteSpace(curve.Tag.ToString()) || !tags.Add(curve.Tag.ToString())) return false;
        return true;
    }

    private static Curve FindCurveByTag(Sketch sketch, string tag)
    {
        foreach (Curve curve in SketchCurves(sketch))
            if (String.Equals(curve.Tag.ToString(), tag, StringComparison.Ordinal)) return curve;
        return null;
    }

    private static Curve FindCurveByJournalId(Sketch sketch, string id)
    {
        foreach (Curve curve in SketchCurves(sketch))
            if (String.Equals(curve.JournalIdentifier, id, StringComparison.Ordinal)) return curve;
        return null;
    }

    private static Sketch FindSketch(Part part, string sketchId)
    {
        foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null && String.Equals(sketch.JournalIdentifier, sketchId, StringComparison.Ordinal)) return sketch;
        throw new InvalidOperationException("Native Sketch JournalIdentifier was not found: " + sketchId);
    }

    private static Sketch FindSketchFromFixture(Fixture fixture)
    {
        return FindSketch(fixture.Part, fixture.SketchId);
    }

    private static Curve FindSeedCurve(Sketch sketch, Frame frame, M3SketchGeometryRequest request)
    {
        foreach (Curve candidate in SketchCurves(sketch))
        {
            if (request.Type == "line" && candidate is Line)
            {
                CurveModel model = CaptureCurve(candidate, frame);
                double direct = Math.Max(Distance2(model.Start, request.StartMm), Distance2(model.End, request.EndMm));
                double reverse = Math.Max(Distance2(model.Start, request.EndMm), Distance2(model.End, request.StartMm));
                if (Math.Min(direct, reverse) <= PositionToleranceMm) return candidate;
            }
            else if (request.Type == "arc" && candidate is Arc)
            {
                CurveModel model = CaptureCurve(candidate, frame);
                double start = request.StartAngleDeg * Pi / 180.0;
                double sweep = request.SweepDeg * Pi / 180.0;
                double[] expectedStart = new double[] {
                    request.CenterMm[0] + request.RadiusMm * Math.Cos(start),
                    request.CenterMm[1] + request.RadiusMm * Math.Sin(start) };
                double[] expectedEnd = new double[] {
                    request.CenterMm[0] + request.RadiusMm * Math.Cos(start + sweep),
                    request.CenterMm[1] + request.RadiusMm * Math.Sin(start + sweep) };
                if (Distance2(model.Center, request.CenterMm) <= PositionToleranceMm &&
                    Math.Abs(model.Radius - request.RadiusMm) <= PositionToleranceMm &&
                    Distance2(model.Start, expectedStart) <= PositionToleranceMm &&
                    Distance2(model.End, expectedEnd) <= PositionToleranceMm &&
                    Math.Abs(model.Sweep - sweep) <= AngleToleranceRad) return candidate;
            }
            else if ((request.Type == "ellipse" || request.Type == "elliptical_arc") && candidate is Ellipse)
            {
                Ellipse ellipse = (Ellipse)candidate;
                double major, minor, start, end, rotation;
                Point3d center;
                ellipse.GetParameters(out major, out minor, out start, out end, out rotation, out center);
                if (Distance2(WorldToLocal(frame, center), request.CenterMm) <= PositionToleranceMm &&
                    Math.Abs(major - request.MajorRadiusMm) <= PositionToleranceMm &&
                    Math.Abs(minor - request.MinorRadiusMm) <= PositionToleranceMm) return candidate;
            }
            else if (request.Type == "spline" && candidate is Spline)
            {
                return candidate;
            }
            else if (request.Type == "circle" && candidate is Arc)
            {
                CurveModel model = CaptureCurve(candidate, frame);
                if (model.IsFullCircle && Distance2(model.Center, request.CenterMm) <= PositionToleranceMm &&
                    Math.Abs(model.Radius - request.DiameterMm * 0.5) <= PositionToleranceMm) return candidate;
            }
        }
        return null;
    }

    private static CurveModel CaptureCurve(Curve curve, Frame frame)
    {
        Require(curve != null, "Cannot capture a null NXOpen Curve.");
        Line line = curve as Line;
        if (line != null)
        {
            double[] lineStart = WorldToLocal(frame, line.StartPoint);
            double[] lineEnd = WorldToLocal(frame, line.EndPoint);
            return new CurveModel
            {
                Tag = curve.Tag.ToString(), JournalId = curve.JournalIdentifier,
                Type = curve.GetType().Name, IsArc = false,
                Start = lineStart, End = lineEnd, LengthMm = Distance2(lineStart, lineEnd)
            };
        }
        Arc arc = curve as Arc;
        if (arc == null) throw new InvalidOperationException("Pattern oracle accepts only native Line and Arc curves.");
        Point3d orientationCenter;
        Vector3d xWorld;
        Vector3d yWorld;
        arc.GetOrientation(out orientationCenter, out xWorld, out yWorld);
        double[] center = WorldToLocal(frame, arc.CenterPoint);
        double[] axisX = WorldVectorToLocal(frame, xWorld);
        double[] axisY = WorldVectorToLocal(frame, yWorld);
        double raw = arc.EndAngle - arc.StartAngle;
        Require(IsFinite(raw) && Math.Abs(raw) <= TwoPi + AngleToleranceRad,
            "Native Arc has a nonfinite or more-than-one-turn parameter interval.");
        double[] rawStart = ArcPoint(center, axisX, axisY, arc.Radius, arc.StartAngle);
        double[] rawEnd = ArcPoint(center, axisX, axisY, arc.Radius, arc.EndAngle);
        double sweep = NormalizeSweep(raw, Distance2(rawStart, rawEnd));
        double[] start = ArcPoint(center, axisX, axisY, arc.Radius, arc.StartAngle);
        double[] end = ArcPoint(center, axisX, axisY, arc.Radius, arc.StartAngle + sweep);
        double nativeLength = arc.GetLength();
        double expectedLength = arc.Radius * Math.Abs(sweep);
        return new CurveModel
        {
            Tag = curve.Tag.ToString(), JournalId = curve.JournalIdentifier,
            Type = curve.GetType().Name, IsArc = true,
            IsFullCircle = Math.Abs(Math.Abs(sweep) - TwoPi) <= AngleToleranceRad,
            Start = start, End = end, Center = center, AxisX = axisX, AxisY = axisY,
            Radius = arc.Radius, StartAngle = arc.StartAngle, Sweep = sweep,
            LengthMm = nativeLength
        };
    }

    private static double NormalizeSweep(double raw, double endpointDistance)
    {
        Require(IsFinite(raw) && Math.Abs(raw) <= TwoPi + AngleToleranceRad,
            "Native Arc sweep is nonfinite or exceeds one turn.");
        if (Math.Abs(raw) <= AngleToleranceRad && endpointDistance <= PositionToleranceMm) return TwoPi;
        if (Math.Abs(raw) <= AngleToleranceRad)
            throw new InvalidOperationException("Native Arc sweep is degenerate with noncoincident endpoints.");
        return raw < 0.0 ? raw + TwoPi : raw;
    }

    private static List<object> SourceGeometryRecords(Fixture fixture, List<string> sourceIds)
    {
        List<object> records = new List<object>();
        foreach (string sourceId in sourceIds)
        {
            Curve source = null;
            foreach (Curve curve in fixture.Curves.Values)
                if (String.Equals(curve.JournalIdentifier, sourceId, StringComparison.Ordinal))
                {
                    source = curve;
                    break;
                }
            Require(source != null, "Could not resolve source geometry Tag for evidence: " + sourceId);
            CurveModel model = CaptureCurve(source, fixture.Frame);
            records.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "source_curve_id", sourceId }, { "source_curve_tag", source.Tag.ToString() },
                { "native_geometry", CurveModelRecord(model) }
            });
        }
        return records;
    }

    private static Dictionary<string, object> CurveModelRecord(CurveModel model)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", model.Tag }, { "journal_id", model.JournalId }, { "object_type", model.Type },
            { "is_arc", model.IsArc }, { "is_full_circle", model.IsFullCircle },
            { "start_xy_mm", model.Start }, { "end_xy_mm", model.End },
            { "center_xy_mm", model.Center }, { "axis_x_xy", model.AxisX }, { "axis_y_xy", model.AxisY },
            { "radius_mm", model.IsArc ? (object)model.Radius : null },
            { "start_angle_rad", model.IsArc ? (object)model.StartAngle : null },
            { "sweep_rad", model.IsArc ? (object)model.Sweep : null },
            { "native_curve_length_mm", model.LengthMm },
            { "expected_intrinsic_length_mm", model.IsArc ? (object)(model.Radius * Math.Abs(model.Sweep)) :
                (model.Start == null || model.End == null ? (object)null : Distance2(model.Start, model.End)) },
            { "intrinsic_length_residual_mm", model.IsArc ?
                Math.Abs(model.LengthMm - model.Radius * Math.Abs(model.Sweep)) :
                (model.Start == null || model.End == null ? (object)null : Math.Abs(model.LengthMm - Distance2(model.Start, model.End))) }
        };
    }

    private static double[] ArcPoint(double[] center, double[] axisX, double[] axisY,
        double radius, double angle)
    {
        return new double[] {
            center[0] + radius * (Math.Cos(angle) * axisX[0] + Math.Sin(angle) * axisY[0]),
            center[1] + radius * (Math.Cos(angle) * axisX[1] + Math.Sin(angle) * axisY[1])
        };
    }

    private static double[] ArcTangent(double[] axisX, double[] axisY, double angle, double sweep)
    {
        double sign = sweep < 0.0 ? -1.0 : 1.0;
        return Normalize2(new double[] {
            sign * (-Math.Sin(angle) * axisX[0] + Math.Cos(angle) * axisY[0]),
            sign * (-Math.Sin(angle) * axisX[1] + Math.Cos(angle) * axisY[1])
        });
    }

    private static Frame FrameFromSketch(Sketch sketch)
    {
        Matrix3x3 axes = sketch.Orientation.Element;
        return new Frame
        {
            Origin = new double[] { sketch.Origin.X, sketch.Origin.Y, sketch.Origin.Z },
            XAxis = new double[] { axes.Xx, axes.Xy, axes.Xz },
            YAxis = new double[] { axes.Yx, axes.Yy, axes.Yz },
            Normal = new double[] { axes.Zx, axes.Zy, axes.Zz }
        };
    }

    private static void CheckNativeFrame(Sketch sketch, Frame expected, string label)
    {
        Frame actual = FrameFromSketch(sketch);
        Require(VectorDistance(actual.Origin, expected.Origin) <= PositionToleranceMm &&
            VectorDistance(actual.XAxis, expected.XAxis) <= VectorTolerance &&
            VectorDistance(actual.YAxis, expected.YAxis) <= VectorTolerance &&
            VectorDistance(actual.Normal, expected.Normal) <= VectorTolerance,
            "Native Sketch frame differs from requested origin/axes: " + label);
    }

    private static Dictionary<string, object> FrameRecord(Frame frame)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "origin_mm", (double[])frame.Origin.Clone() },
            { "x_axis_world", (double[])frame.XAxis.Clone() },
            { "y_axis_world", (double[])frame.YAxis.Clone() },
            { "normal_world", (double[])frame.Normal.Clone() }
        };
    }

    private static Vector3d ToVector(double[] value)
    {
        Require(value != null && value.Length == 3, "Expected a 3D vector for the sketch frame.");
        return new Vector3d(value[0], value[1], value[2]);
    }

    private static Frame BuildFrame(double[] origin, double[] normal, double[] requestedX)
    {
        double[] n = Normalize3(normal);
        double projection = Dot3(requestedX, n);
        double[] projected = new double[] {
            requestedX[0] - projection * n[0],
            requestedX[1] - projection * n[1],
            requestedX[2] - projection * n[2]
        };
        double[] x = Normalize3(projected);
        double[] y = Normalize3(Cross3(n, x));
        return new Frame { Origin = (double[])origin.Clone(), Normal = n, XAxis = x, YAxis = y };
    }

    private static Point3d WorldPoint(Frame frame, double x, double y)
    {
        return new Point3d(frame.Origin[0] + x * frame.XAxis[0] + y * frame.YAxis[0],
            frame.Origin[1] + x * frame.XAxis[1] + y * frame.YAxis[1],
            frame.Origin[2] + x * frame.XAxis[2] + y * frame.YAxis[2]);
    }

    private static double[] WorldToLocal(Frame frame, Point3d point)
    {
        double[] delta = new double[] { point.X - frame.Origin[0], point.Y - frame.Origin[1], point.Z - frame.Origin[2] };
        Require(Math.Abs(Dot3(delta, frame.Normal)) <= PositionToleranceMm,
            "Native curve point is outside the target Sketch plane.");
        return new double[] { Dot3(delta, frame.XAxis), Dot3(delta, frame.YAxis) };
    }

    private static double[] WorldVectorToLocal(Frame frame, Vector3d value)
    {
        double[] vector = new double[] { value.X, value.Y, value.Z };
        Require(Math.Abs(Dot3(vector, frame.Normal)) <= VectorTolerance,
            "Native curve direction is outside the target Sketch plane.");
        return Normalize2(new double[] { Dot3(vector, frame.XAxis), Dot3(vector, frame.YAxis) });
    }

    private static double[] Point(Point3d value)
    {
        return new double[] { value.X, value.Y, value.Z };
    }

    private static double[] Vector(Vector3d value)
    {
        return new double[] { value.X, value.Y, value.Z };
    }

    private static Point3d Midpoint(Point3d a, Point3d b)
    {
        return new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
    }

    private static bool ExistingHistoryPreserved(Dictionary<string, object> before,
        Dictionary<string, object> after)
    {
        List<object> b = before["features_in_history_order"] as List<object>;
        List<object> a = after["features_in_history_order"] as List<object>;
        if (b == null || a == null) return false;
        HashSet<string> actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (object item in a)
        {
            Dictionary<string, object> feature = item as Dictionary<string, object>;
            if (feature != null) actual.Add(GetString(feature, "tag"));
        }
        foreach (object item in b)
        {
            Dictionary<string, object> feature = item as Dictionary<string, object>;
            if (feature == null || !actual.Contains(GetString(feature, "tag"))) return false;
        }
        return true;
    }

    private static Dictionary<string, object> RoundTripProjection(Dictionary<string, object> snapshot)
    {
        List<object> sketchProjections = new List<object>();
        Dictionary<string, object> sketches = ReadDictionary(snapshot, "sketches_by_tag");
        if (sketches == null) throw new InvalidOperationException("Round-trip snapshot has no Sketch inventory.");
        foreach (KeyValuePair<string, object> pair in sketches)
        {
            Dictionary<string, object> sketch = pair.Value as Dictionary<string, object>;
            if (sketch == null) throw new InvalidOperationException("Round-trip Sketch record is invalid.");
            Dictionary<string, object> geometries = ReadDictionary(sketch, "geometry_by_tag");
            List<object> geometryProjection = new List<object>();
            Dictionary<string, string> shapeByTag = new Dictionary<string, string>(StringComparer.Ordinal);
            if (geometries != null)
                foreach (KeyValuePair<string, object> geometry in geometries)
                {
                    Dictionary<string, object> record = geometry.Value as Dictionary<string, object>;
                    Dictionary<string, object> projected = GeometryRoundTripProjection(record);
                    geometryProjection.Add(projected);
                    shapeByTag[geometry.Key] = M2Json.CanonicalText(projected);
                }
            SortCanonical(geometryProjection);

            List<object> constraints = new List<object>();
            Dictionary<string, object> constraintMap = ReadDictionary(sketch, "constraints_by_tag");
            if (constraintMap != null)
                foreach (KeyValuePair<string, object> constraintPair in constraintMap)
                {
                    Dictionary<string, object> constraint = constraintPair.Value as Dictionary<string, object>;
                    constraints.Add(ConstraintRoundTripProjection(constraint, shapeByTag));
                }
            SortCanonical(constraints);

            List<object> references = new List<object>();
            Dictionary<string, object> statuses = ReadDictionary(sketch, "reference_status_by_tag");
            if (statuses != null)
                foreach (KeyValuePair<string, object> status in statuses)
                    references.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "geometry_signature", shapeByTag.ContainsKey(status.Key) ? shapeByTag[status.Key] : "missing" },
                        { "status", status.Value }
                    });
            SortCanonical(references);
            sketchProjections.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "frame", sketch["frame"] }, { "geometry", geometryProjection },
                { "constraints", constraints }, { "reference_statuses", references },
                { "solver_status", sketch["solver_status"] },
                { "degrees_of_freedom", sketch["degrees_of_freedom"] }
            });
        }
        SortCanonical(sketchProjections);

        List<object> historyProjection = new List<object>();
        List<object> history = snapshot["features_in_history_order"] as List<object>;
        if (history != null)
            foreach (object item in history)
            {
                Dictionary<string, object> feature = item as Dictionary<string, object>;
                historyProjection.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "feature_type", GetString(feature, "feature_type") },
                    { "suppressed", feature == null ? null : feature["suppressed"] },
                    { "name", GetString(feature, "name") }
                });
            }

        List<object> bodyProjection = new List<object>();
        Dictionary<string, object> bodies = ReadDictionary(snapshot, "bodies_by_tag");
        if (bodies != null)
            foreach (KeyValuePair<string, object> pair in bodies)
            {
                Dictionary<string, object> body = pair.Value as Dictionary<string, object>;
                if (body == null) continue;
                List<string> faceTags = ToStringList(body["face_tags"]);
                List<string> edgeTags = ToStringList(body["edge_tags"]);
                bodyProjection.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "solid", body["solid"] }, { "bounding_box_mm", body["bounding_box_mm"] },
                    { "face_count", faceTags.Count }, { "edge_count", edgeTags.Count }
                });
            }
        SortCanonical(bodyProjection);

        List<object> pointProjection = new List<object>();
        List<object> points = snapshot["global_point_records"] as List<object>;
        if (points != null)
            foreach (object value in points)
            {
                Dictionary<string, object> point = value as Dictionary<string, object>;
                if (point != null) pointProjection.Add(point["coordinates_mm"]);
            }
        SortCanonical(pointProjection);

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "part_units", snapshot["part_units"] },
            { "sketches", sketchProjections },
            { "history", historyProjection },
            { "bodies", bodyProjection },
            { "global_points", pointProjection }
        };
    }

    private static Dictionary<string, object> GeometryRoundTripProjection(Dictionary<string, object> record)
    {
        if (record == null) return new Dictionary<string, object>();
        string type = GetString(record, "object_type");
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "object_type", type }, { "native_length_mm", record["native_length_mm"] }
        };
        if (type == "Line")
        {
            result["start_xy_mm"] = record["model_start_xy_mm"];
            result["end_xy_mm"] = record["model_end_xy_mm"];
        }
        else if (type == "Arc")
        {
            result["is_full_circle"] = record["model_is_full_circle"];
            result["start_xy_mm"] = record["model_start_xy_mm"];
            result["end_xy_mm"] = record["model_end_xy_mm"];
            result["center_xy_mm"] = record["model_center_xy_mm"];
            result["axis_x_xy"] = record["model_axis_x_xy"];
            result["axis_y_xy"] = record["model_axis_y_xy"];
            result["radius_mm"] = record["model_radius_mm"];
            result["start_angle_rad"] = record["model_start_angle_rad"];
            result["sweep_rad"] = record["model_sweep_rad"];
        }
        else if (type == "Ellipse")
        {
            result["center_world_mm"] = record["center_world_mm"];
            result["major_radius_mm"] = record["major_radius_mm"];
            result["minor_radius_mm"] = record["minor_radius_mm"];
            result["start_angle_rad"] = record["start_angle_rad"];
            result["end_angle_rad"] = record["end_angle_rad"];
            result["rotation_rad"] = record["rotation_rad"];
            result["plane_x_axis_world"] = record["plane_x_axis_world"];
            result["plane_y_axis_world"] = record["plane_y_axis_world"];
        }
        else
        {
            foreach (string key in new string[] { "spline_degree", "spline_pole_count", "spline_knot_count",
                "spline_rational", "spline_knots", "spline_poles" })
                if (record.ContainsKey(key)) result[key] = record[key];
        }
        return result;
    }

    private static Dictionary<string, object> ConstraintRoundTripProjection(
        Dictionary<string, object> record, Dictionary<string, string> shapeByTag)
    {
        if (record == null) return new Dictionary<string, object>();
        List<object> operands = new List<object>();
        List<object> nativeOperands = record["operands"] as List<object>;
        if (nativeOperands != null)
            foreach (object native in nativeOperands)
            {
                Dictionary<string, object> operand = native as Dictionary<string, object>;
                if (operand == null) continue;
                string tag = GetString(operand, "geometry_tag");
                operands.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "geometry_signature", tag != null && shapeByTag.ContainsKey(tag) ? shapeByTag[tag] : "noncurve_or_origin" },
                    { "association", GetString(operand, "association") },
                    { "association_value", operand.ContainsKey("association_value") ? operand["association_value"] : null },
                    { "point_type", GetString(operand, "point_type") },
                    { "spline_point_index", operand.ContainsKey("spline_point_index") ? operand["spline_point_index"] : null }
                });
            }
        SortCanonical(operands);
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "constraint_type", GetString(record, "constraint_type") }, { "class", GetString(record, "class") },
            { "dimension_state", GetString(record, "dimension_state") },
            { "rhs", record.ContainsKey("rhs") ? record["rhs"] : null },
            { "value", record.ContainsKey("value") ? record["value"] : null },
            { "unit", GetString(record, "unit") },
            { "expression_name", GetString(record, "expression_name") },
            { "operands", operands }
        };
        return result;
    }

    private static void CompareRoundTripValues(string path, object before, object after,
        List<object> differences, double defaultTolerance)
    {
        if (before == null || after == null)
        {
            if (!Object.Equals(before, after)) differences.Add(DifferenceRecord(path, before, after));
            return;
        }
        Dictionary<string, object> beforeMap = before as Dictionary<string, object>;
        Dictionary<string, object> afterMap = after as Dictionary<string, object>;
        if (beforeMap != null || afterMap != null)
        {
            if (beforeMap == null || afterMap == null || beforeMap.Count != afterMap.Count)
            {
                differences.Add(DifferenceRecord(path, before, after));
                return;
            }
            foreach (KeyValuePair<string, object> pair in beforeMap)
            {
                if (!afterMap.ContainsKey(pair.Key)) differences.Add(DifferenceRecord(path + "." + pair.Key, pair.Value, null));
                else CompareRoundTripValues(path + "." + pair.Key, pair.Value, afterMap[pair.Key],
                    differences, ToleranceForPath(path + "." + pair.Key, defaultTolerance));
            }
            return;
        }
        if (IsNumber(before) && IsNumber(after))
        {
            double left = Convert.ToDouble(before, CultureInfo.InvariantCulture);
            double right = Convert.ToDouble(after, CultureInfo.InvariantCulture);
            if (!IsFinite(left) || !IsFinite(right) || Math.Abs(left - right) > ToleranceForPath(path, defaultTolerance))
                differences.Add(DifferenceRecord(path, before, after));
            return;
        }
        IList beforeList = before as IList;
        IList afterList = after as IList;
        if (beforeList != null || afterList != null)
        {
            if (beforeList == null || afterList == null || beforeList.Count != afterList.Count)
            {
                differences.Add(DifferenceRecord(path, before, after));
                return;
            }
            for (int i = 0; i < beforeList.Count; i++)
                CompareRoundTripValues(path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]",
                    beforeList[i], afterList[i], differences, defaultTolerance);
            return;
        }
        if (!String.Equals(Convert.ToString(before, CultureInfo.InvariantCulture),
            Convert.ToString(after, CultureInfo.InvariantCulture), StringComparison.Ordinal))
            differences.Add(DifferenceRecord(path, before, after));
    }

    private static double ToleranceForPath(string path, double fallback)
    {
        string lower = (path ?? "").ToLowerInvariant();
        if (lower.Contains("angle") || lower.Contains("_rad") || lower.Contains("sweep")) return AngleToleranceRad;
        if (lower.Contains("axis") || lower.Contains("normal")) return VectorTolerance;
        return fallback;
    }

    private static bool IsNumber(object value)
    {
        return value is byte || value is sbyte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong ||
            value is float || value is double || value is decimal;
    }

    private static Dictionary<string, object> DifferenceRecord(string path, object before, object after)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "path", path }, { "before", before }, { "after", after }
        };
    }

    private static void SortCanonical(List<object> values)
    {
        values.Sort(delegate(object left, object right)
        {
            return StringComparer.Ordinal.Compare(M2Json.CanonicalText(left), M2Json.CanonicalText(right));
        });
    }

    private static void RunCase(string reportPath, Dictionary<string, object> report,
        List<object> cases, List<object> defects, string name, string objective,
        string[] reproductionSteps, string passCriterion, CaseBody body,
        bool continueAfterFailure = false)
    {
        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "objective", objective }, { "status", "RUNNING" },
            { "reproduction_steps", reproductionSteps }, { "pass_criterion", passCriterion },
            { "started_utc", UtcNow() }
        };
        cases.Add(row);
        report["current_case"] = name;
        Persist(reportPath, report);
        try
        {
            body(row);
            row["status"] = "PASSED";
            row["finished_utc"] = UtcNow();
            Persist(reportPath, report);
        }
        catch (Exception ex)
        {
            row["status"] = "FAILED";
            row["observed_exception"] = ex.ToString();
            row["observed_error_code"] = ErrorCode(ex);
            row["finished_utc"] = UtcNow();
            row["retest_criterion"] = "Fix the recorded defect, preserve this report and failed model, then rerun this exact F09 case plus its preservation and save/reopen checks on a new unique part/report path.";
            defects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "test_case", name }, { "reproduction_steps", reproductionSteps },
                { "request", row.ContainsKey("request") ? row["request"] : null },
                { "native_before", row.ContainsKey("native_before") ? row["native_before"] : null },
                { "native_after", row.ContainsKey("native_after_execute") ? row["native_after_execute"] :
                    row.ContainsKey("native_after_undo") ? row["native_after_undo"] : null },
                { "observed_exception", ex.ToString() }, { "observed_error_code", ErrorCode(ex) },
                { "undo_evidence", row.ContainsKey("undo_restore") ? row["undo_restore"] : null },
                { "retest_criterion", row["retest_criterion"] }
            });
            report["passed"] = false;
            Persist(reportPath, report);
            if (!continueAfterFailure)
                throw new InvalidOperationException("F09 acceptance case failed: " + name, ex);
        }
    }

    private static void AddDefect(Dictionary<string, object> report, List<object> defects,
        string name, Exception exception, string[] reproductionSteps, string retestCriterion)
    {
        defects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test_case", name }, { "reproduction_steps", reproductionSteps },
            { "observed_exception", exception == null ? null : exception.ToString() },
            { "observed_error_code", ErrorCode(exception) },
            { "retest_criterion", retestCriterion }
        });
    }

    private static Dictionary<string, object> FindNestedDictionary(object value, string key)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary == null) return null;
        object direct;
        if (dictionary.TryGetValue(key, out direct))
        {
            Dictionary<string, object> found = direct as Dictionary<string, object>;
            if (found != null) return found;
        }
        foreach (object child in dictionary.Values)
        {
            Dictionary<string, object> found = FindNestedDictionary(child, key);
            if (found != null) return found;
        }
        List<object> list = value as List<object>;
        if (list != null)
            foreach (object child in list)
            {
                Dictionary<string, object> found = FindNestedDictionary(child, key);
                if (found != null) return found;
            }
        return null;
    }

    private static object ReadNested(object value, string key)
    {
        object result;
        if (TryReadNested(value, key, out result)) return result;
        return null;
    }

    private static bool TryReadNested(object value, string key, out object result)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            if (dictionary.TryGetValue(key, out result)) return true;
            foreach (object child in dictionary.Values)
                if (TryReadNested(child, key, out result)) return true;
        }
        List<object> list = value as List<object>;
        if (list != null)
            foreach (object child in list)
                if (TryReadNested(child, key, out result)) return true;
        result = null;
        return false;
    }

    private static Dictionary<string, object> ReadDictionary(Dictionary<string, object> source, string key)
    {
        object value;
        return source != null && source.TryGetValue(key, out value)
            ? value as Dictionary<string, object> : null;
    }

    private static string GetString(Dictionary<string, object> source, string key)
    {
        object value;
        if (source == null || !source.TryGetValue(key, out value) || value == null) return null;
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
            if (!String.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    private static string ErrorCode(Exception error)
    {
        if (error == null) return null;
        TargetInvocationException target = error as TargetInvocationException;
        if (target != null && target.InnerException != null) error = target.InnerException;
        M2ModelException model = error as M2ModelException;
        if (model != null) return model.Code;
        PropertyInfo code = error.GetType().GetProperty("Code", BindingFlags.Instance | BindingFlags.Public);
        if (code != null)
        {
            try { return Convert.ToString(code.GetValue(error, null), CultureInfo.InvariantCulture); }
            catch (Exception) { }
        }
        return null;
    }

    private static object NumberOrText(double value)
    {
        if (Double.IsNaN(value)) return "NaN";
        if (Double.IsPositiveInfinity(value)) return "Infinity";
        if (Double.IsNegativeInfinity(value)) return "-Infinity";
        return value;
    }

    private static object NumberArrayRecord(double[] values)
    {
        if (values == null) return null;
        List<object> result = new List<object>();
        foreach (double value in values) result.Add(NumberOrText(value));
        return result;
    }

    private static object NestedNumberArrayRecord(double[][] values)
    {
        if (values == null) return null;
        List<object> result = new List<object>();
        foreach (double[] value in values) result.Add(NumberArrayRecord(value));
        return result;
    }

    private static Dictionary<string, object> StringObjectMap(Dictionary<string, string> values)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (values != null)
            foreach (KeyValuePair<string, string> pair in values) result[pair.Key] = pair.Value;
        return result;
    }

    private static List<string> ToStringList(object value)
    {
        List<string> result = new List<string>();
        if (value == null) return result;
        string text = value as string;
        if (text != null) { result.Add(text); return result; }
        IEnumerable enumerable = value as IEnumerable;
        if (enumerable == null) return result;
        foreach (object item in enumerable)
        {
            if (item == null) continue;
            string itemText = item as string;
            if (itemText != null) result.Add(itemText);
            else result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
        }
        return result;
    }

    private static bool UniqueStringTags(List<string> tags)
    {
        if (tags == null) return false;
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string tag in tags)
            if (String.IsNullOrWhiteSpace(tag) || !seen.Add(tag)) return false;
        return true;
    }

    private static void ValidateFactoryOutputRecords(List<object> records, Dictionary<string, object> row)
    {
        List<object> measured = new List<object>();
        foreach (object value in records)
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            Require(record != null, "Native factory output record is not an object.");
            string type = FirstNonEmpty(GetString(record, "object_type"), GetString(record, "curve_type"),
                GetString(record, "type"));
            string tag = FirstNonEmpty(GetString(record, "tag"), GetString(record, "output_curve_tag"));
            object length = record.ContainsKey("intrinsic_length_mm") ? record["intrinsic_length_mm"] :
                record.ContainsKey("native_length_mm") ? record["native_length_mm"] :
                record.ContainsKey("length_mm") ? record["length_mm"] : null;
            Require(!String.IsNullOrWhiteSpace(type) && !String.IsNullOrWhiteSpace(tag) &&
                length != null && IsNumber(length) && IsFinite(Convert.ToDouble(length, CultureInfo.InvariantCulture)),
                "Native factory record lacks Tag/type/finite intrinsic curve length.");
            if (type.IndexOf("Line", StringComparison.OrdinalIgnoreCase) >= 0)
                Require(HasAny(record, "start_mm", "start", "start_point_mm") &&
                    HasAny(record, "end_mm", "end", "end_point_mm"),
                    "Native factory Line record lacks direct endpoint coordinates.");
            else if (type.IndexOf("Arc", StringComparison.OrdinalIgnoreCase) >= 0)
                Require(HasAny(record, "center_mm", "center", "center_point_mm") &&
                    HasAny(record, "radius_mm", "radius") &&
                    HasAny(record, "start_angle_rad", "start_angle") &&
                    HasAny(record, "end_angle_rad", "end_angle") &&
                    HasAny(record, "plane_x_axis_world", "axis_x_world", "orientation_x") &&
                    HasAny(record, "plane_y_axis_world", "axis_y_world", "orientation_y") &&
                    HasAny(record, "full_circle", "is_full_circle"),
                    "Native factory Arc record lacks center/radius/extent/orientation/full-circle evidence.");
            measured.Add(record);
        }
        row["factory_output_records_numeric_and_complete"] = measured;
    }

    private static bool HasAny(Dictionary<string, object> record, params string[] keys)
    {
        foreach (string key in keys)
            if (record.ContainsKey(key) && record[key] != null) return true;
        return false;
    }

    private static bool FindTagInSnapshot(Dictionary<string, object> snapshot, string tag)
    {
        Dictionary<string, object> sketches = ReadDictionary(snapshot, "sketches_by_tag");
        if (sketches != null)
            foreach (object value in sketches.Values)
            {
                Dictionary<string, object> sketch = value as Dictionary<string, object>;
                Dictionary<string, object> objects = ReadDictionary(sketch, "all_objects_by_tag");
                if (objects != null && objects.ContainsKey(tag)) return true;
            }
        if (ToStringList(snapshot["global_curve_tags"]).Contains(tag)) return true;
        if (ToStringList(snapshot["global_point_tags"]).Contains(tag)) return true;
        return false;
    }

    private static List<object> SnapshotDiff(object before, object after)
    {
        List<object> result = new List<object>();
        AppendSnapshotDiff("$", before, after, result);
        return result;
    }

    private static void AppendSnapshotDiff(string path, object before, object after, List<object> result)
    {
        if (SameValue(before, after)) return;
        Dictionary<string, object> beforeMap = before as Dictionary<string, object>;
        Dictionary<string, object> afterMap = after as Dictionary<string, object>;
        if (beforeMap != null && afterMap != null)
        {
            HashSet<string> keys = new HashSet<string>(beforeMap.Keys, StringComparer.Ordinal);
            foreach (string key in afterMap.Keys) keys.Add(key);
            foreach (string key in keys)
            {
                object left;
                object right;
                bool hasLeft = beforeMap.TryGetValue(key, out left);
                bool hasRight = afterMap.TryGetValue(key, out right);
                if (!hasLeft || !hasRight) result.Add(DifferenceRecord(path + "." + key,
                    hasLeft ? left : null, hasRight ? right : null));
                else AppendSnapshotDiff(path + "." + key, left, right, result);
            }
            return;
        }
        IList beforeList = before as IList;
        IList afterList = after as IList;
        if (beforeList != null && afterList != null && beforeList.Count == afterList.Count)
        {
            for (int i = 0; i < beforeList.Count; i++)
                AppendSnapshotDiff(path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]",
                    beforeList[i], afterList[i], result);
            return;
        }
        result.Add(DifferenceRecord(path, before, after));
    }

    private static bool SameValue(object left, object right)
    {
        return String.Equals(M2Json.CanonicalText(left), M2Json.CanonicalText(right), StringComparison.Ordinal);
    }

    // Call only after the containing Sketch, geometry, or constraint was matched
    // by its stable native Tag. JournalIdentifiers are mutable display metadata;
    // every other field, including all geometry and constraint operands, stays exact.
    private static bool SameTagMatchedNativeProjection(object left, object right)
    {
        return SameValue(WithoutJournalIdentifierMetadata(left), WithoutJournalIdentifierMetadata(right));
    }

    private static object WithoutJournalIdentifierMetadata(object value)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                if (String.Equals(pair.Key, "journal_id", StringComparison.Ordinal) ||
                    String.Equals(pair.Key, "model_journal_id", StringComparison.Ordinal)) continue;
                result[pair.Key] = WithoutJournalIdentifierMetadata(pair.Value);
            }
            return result;
        }
        IList list = value as IList;
        if (list != null)
        {
            List<object> result = new List<object>();
            foreach (object item in list) result.Add(WithoutJournalIdentifierMetadata(item));
            return result;
        }
        return value;
    }

    private static Dictionary<string, object> FindSketchRecord(Dictionary<string, object> snapshot,
        string sketchId)
    {
        Dictionary<string, object> sketches = ReadDictionary(snapshot, "sketches_by_tag");
        if (sketches == null) return null;
        foreach (object value in sketches.Values)
        {
            Dictionary<string, object> sketch = value as Dictionary<string, object>;
            if (String.Equals(GetString(sketch, "journal_id"), sketchId, StringComparison.Ordinal)) return sketch;
        }
        return null;
    }

    private static bool IsNx2506(string version)
    {
        if (String.IsNullOrWhiteSpace(version)) return false;
        string normalized = version.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        return normalized.Contains("2506") || normalized.Contains("25.06") || normalized.Contains("25.6");
    }

    private static string UtcNow()
    {
        return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
    }

    private static double[] Normalize2(double[] value)
    {
        Require(value != null && value.Length == 2, "Expected a two-coordinate vector.");
        double scale = Math.Max(Math.Abs(value[0]), Math.Abs(value[1]));
        Require(IsFinite(scale) && scale > 0.0, "Cannot normalize a zero or nonfinite 2D vector.");
        double x = value[0] / scale;
        double y = value[1] / scale;
        double length = Math.Sqrt(x * x + y * y);
        Require(IsFinite(length) && length > 0.0, "Cannot normalize a degenerate 2D vector.");
        return new double[] { x / length, y / length };
    }

    private static double[] Normalize3(double[] value)
    {
        double length = Math.Sqrt(Dot3(value, value));
        Require(IsFinite(length) && length > 1.0e-15, "Cannot normalize a zero or nonfinite 3D vector.");
        return new double[] { value[0] / length, value[1] / length, value[2] / length };
    }

    private static double Distance2(double[] left, double[] right)
    {
        if (left == null || right == null || left.Length != 2 || right.Length != 2) return Double.PositiveInfinity;
        double dx = left[0] - right[0];
        double dy = left[1] - right[1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double PointDistance(Point3d left, Point3d right)
    {
        double dx = left.X - right.X, dy = left.Y - right.Y, dz = left.Z - right.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double VectorDistance(double[] left, double[] right)
    {
        if (left == null || right == null || left.Length != right.Length) return Double.PositiveInfinity;
        double total = 0.0;
        for (int i = 0; i < left.Length; i++)
        {
            double delta = left[i] - right[i];
            total += delta * delta;
        }
        return Math.Sqrt(total);
    }

    private static double Dot3(double[] left, double[] right)
    {
        return left[0] * right[0] + left[1] * right[1] + left[2] * right[2];
    }

    private static double[] Cross3(double[] left, double[] right)
    {
        return new double[] {
            left[1] * right[2] - left[2] * right[1],
            left[2] * right[0] - left[0] * right[2],
            left[0] * right[1] - left[1] * right[0]
        };
    }

    private static double Cross2(double[] left, double[] right)
    {
        return left[0] * right[1] - left[1] * right[0];
    }

    private static bool IsFinite(double value)
    {
        return !Double.IsNaN(value) && !Double.IsInfinity(value);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
