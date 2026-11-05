// Real-NX F08 subsystem acceptance through the production adapter.
// Build offline; run only by the designated NX operator through RunF08Acceptance.py.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using NXOpen;
using NXOpen.Features;
using NxSketchMcp.M2;

public static class F08Acceptance
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

    private sealed class OffsetOracle
    {
        internal SketchFixture Fixture;
        internal List<M3SketchGeometryRequest> Expected;
        internal List<string> OutputTags;
        internal bool Closed;
        internal Dictionary<string, object> BeforeRoundtrip;
    }

    public static void Main(string[] args)
    {
        string runId = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(ProjectRoot, "workspace", "v2");
        Directory.CreateDirectory(Path.Combine(directory, "models"));
        Directory.CreateDirectory(Path.Combine(directory, "evidence"));
        string sourcePath = Path.Combine(directory, "models", "F08-acceptance-" + runId + ".prt");
        string savedPath = Path.Combine(directory, "models", "F08-acceptance-" + runId + "-roundtrip.prt");
        string reportPath = Path.Combine(directory, "evidence", "F08-acceptance-" + runId + ".json");
        RequireNewPath(sourcePath, "source model"); RequireNewPath(savedPath, "roundtrip model"); RequireNewPath(reportPath, "report");
        List<object> cases = new List<object>();
        List<object> defects = new List<object>();
        List<OffsetOracle> oracles = new List<OffsetOracle>();
        Dictionary<string, object> report = new Dictionary<string, object> {
            { "test", "F08 native sketch offset subsystem validation" }, { "run_id", runId },
            { "started_utc", UtcNow() }, { "passed", false }, { "cases", cases }, { "defects", defects },
            { "source_model_path", sourcePath }, { "roundtrip_model_path", savedPath }, { "report_path", reportPath },
            { "sample_size", 1 }, { "units", "millimeter" }, { "position_tolerance_mm", PositionToleranceMm },
            { "length_tolerance_mm", LengthToleranceMm }, { "angle_tolerance_rad", AngleToleranceRad },
            { "instrument_calibration", "Not applicable: native CAD numeric values, no physical instrument." },
            { "scope_limitations", new string[] { "NX2506 subsystem validation only; no reliability, manufacturing-accuracy or compliance inference.",
                "Protocol/CLR-fake, analytic-unit, installed-MCP tests and native Journal results are separate evidence layers.",
                "F01-F07 cases are not executed by this journal." } },
            { "traceability", "docs/F08.md; tests/nx/v2/F08Acceptance.md F08-01 through F08-16" },
            { "test_layer", "production M2OpenModelAdapter plus independent direct NXOpen/UF numeric oracle" },
            { "nx_started_by_harness", false }
        };
        Persist(reportPath, report);
        Session session = null; Part original = null; Part part = null;
        M2OpenModelAdapter adapter = null; M2ModelBinding binding = null;
        Frame xy = BuildFrame(new double[] { 0, 0, 0 }, new double[] { 0, 0, 1 }, new double[] { 1, 0, 0 });
        try
        {
            session = Session.GetSession(); original = session.Parts.Work;
            report["original_work_part_path"] = original == null ? null : original.FullPath;
            report["original_work_part_was_modified"] = original != null && original.IsModified;
            Require(!session.IsInTaskEnvironment && session.ActiveSketch == null, "Idle NX session required.");
            Require(original == null || !original.IsModified, "Original Work Part must be saved and unmodified.");
            part = session.Parts.NewDisplay(sourcePath, Part.Units.Millimeters);
            SavePart(part, "empty unique F08 fixture");
            adapter = new M2OpenModelAdapter();
            binding = adapter.Takeover(sourcePath, "saved_independent_copy", "f08-acceptance-session", "f08-acceptance-" + runId, 1);
            report["session"] = adapter.GetSession(binding);
            report["assemblies"] = new Dictionary<string, object> {
                { "bridge", AssemblyRecord(typeof(M2Json).Assembly) }, { "adapter", AssemblyRecord(typeof(M2OpenModelAdapter).Assembly) },
                { "nxopen", AssemblyRecord(typeof(Session).Assembly) }, { "acceptance", AssemblyRecord(Assembly.GetExecutingAssembly()) }
            };

            // A real existing solid and non-target constrained sketch are established once.
            // Their full native records are then protected by every offset operation below.
            RunCase(reportPath, report, cases, defects, "F08-11_preservation_fixture",
                "Establish nonempty constraints, a reference curve, another sketch and an existing solid.",
                new string[] { "Create a circle sketch and finite 5 mm create Extrude through production prepare/execute/verify.",
                    "Create another Line sketch, add one native Driving horizontal 10 mm dimension, and save the isolated part." },
                "Solid exists and a non-target sketch has a verified native Driving dimension before any F08 edit.", delegate(Dictionary<string, object> row) {
                    SketchFixture bodySketch = CreateSketch(adapter, binding, part, "body-preservation-circle", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> { Circle("body-circle", 100, 100, 10) }), row);
                    M3PlanSnapshot plan = adapter.PrepareExtrude(binding, new M3ExtrudeRequest {
                        RevisionId = binding.RevisionId, SketchId = bodySketch.SketchId, Mode = "create", Direction = "positive", Extent = "finite", DepthMm = 5
                    });
                    M3ExecutionResult execute = adapter.ExecutePlan(binding, plan);
                    row["extrude_execute"] = ExecutionRecord(execute);
                    Require(execute != null && execute.State == "APPLIED", "Preservation fixture Extrude failed.");
                    M3VerificationResult verify = adapter.VerifyPlan(binding, plan); row["extrude_verify"] = VerificationRecord(verify);
                    Require(verify != null && verify.Verified && ChecksPass(verify.Checks), "Preservation fixture Extrude did not verify.");
                    Require(part.Bodies.ToArray().Length > 0, "No body created for preservation coverage.");
                    SketchFixture other = CreateSketch(adapter, binding, part, "other-constrained-sketch", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> { Line("drive-1", 0, 0, 10, 0) }), row);
                    AddNativeDrivingHorizontalDimension(adapter, binding, session, part, other, FindSketch(part, other.SketchId), row);
                    SavePart(part, "non-target constrained and body fixture");
                    row["native_fixture"] = StrictSnapshot(part);
                });

            foreach (string sideValue in new string[] { "left", "right" })
            {
                string side = sideValue; double sign = side == "left" ? 1 : -1;
                RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                    "F08-01_line_" + side, xy,
                    new List<M3SketchGeometryRequest> { Line("a", 0, 0, 10, 0) }, new string[] { "a" }, new string[] { "forward" },
                    side, "extension", new List<M3SketchGeometryRequest> { Line("out", 0, sign * 2, 10, sign * 2) }, false);
                double x = side == "left" ? 8 : 12, y = sign * 2;
                RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                    "F08-02_open_L_" + side, xy, LSource(false), new string[] { "a", "b" }, new string[] { "forward", "forward" },
                    side, "extension", LOutput(x, y), false);
                double low = side == "left" ? 2 : -2, high = side == "left" ? 8 : 12;
                RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                    "F08-04_square_" + side, xy, Square(0, 10), new string[] { "a", "b", "c", "d" },
                    new string[] { "forward", "forward", "forward", "forward" }, side, "extension", Square(low, high), true);
                double radius = side == "left" ? 3 : 7;
                RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                    "F08-07_line_arc_line_" + side, xy, new List<M3SketchGeometryRequest> {
                        Line("a", -10, 0, 0, 0), Arc("b", 0, 5, 5, 270, 90), Line("c", 5, 5, 5, 15)
                    }, new string[] { "a", "b", "c" }, new string[] { "forward", "forward", "forward" }, side, "extension",
                    new List<M3SketchGeometryRequest> { Line("a", -10, sign * 2, 0, sign * 2), Arc("b", 0, 5, radius, 270, 90), Line("c", radius, 5, radius, 15) }, false);
                RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                    "F08-08_capsule_" + side, xy, Capsule(5), new string[] { "a", "b", "c", "d" },
                    new string[] { "forward", "forward", "forward", "forward" }, side, "extension", Capsule(radius), true);
                RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                    "F08-09_circle_" + side, xy, new List<M3SketchGeometryRequest> { Circle("a", 0, 0, 10) },
                    new string[] { "a" }, new string[] { "forward" }, side, "extension", new List<M3SketchGeometryRequest> { Circle("out", 0, 0, radius * 2) }, true);
            }
            RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                "F08-03_open_L_reverse_left", xy, LSource(false), new string[] { "b", "a" }, new string[] { "reverse", "reverse" },
                "left", "extension", LOutput(12, -2), false);
            RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                "F08-06_mixed_entry_direction", xy, LSource(true), new string[] { "a", "b" }, new string[] { "forward", "reverse" },
                "left", "extension", LOutput(8, 2), false);
            RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                "F08-04_square_reverse_left", xy, Square(0, 10), new string[] { "d", "c", "b", "a" },
                new string[] { "reverse", "reverse", "reverse", "reverse" }, "left", "extension", Square(-2, 12), true);
            RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                "F08-09_circle_reverse_left", xy, new List<M3SketchGeometryRequest> { Circle("a", 0, 0, 10) },
                new string[] { "a" }, new string[] { "reverse" }, "left", "extension", new List<M3SketchGeometryRequest> { Circle("out", 0, 0, 14) }, true);
            foreach (bool reverseValue in new bool[] { false, true })
            {
                bool reverse = reverseValue;
                RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                    "F08-05_outer_arc_" + (reverse ? "reverse_left" : "forward_right"), xy, LSource(false),
                    reverse ? new string[] { "b", "a" } : new string[] { "a", "b" },
                    reverse ? new string[] { "reverse", "reverse" } : new string[] { "forward", "forward" },
                    reverse ? "left" : "right", "arc", new List<M3SketchGeometryRequest> {
                        Line("a", 0, -2, 10, -2), Arc("join", 10, 0, 2, 270, 90), Line("b", 12, 0, 12, 10)
                    }, false);
            }
            Frame tilted = BuildFrame(new double[] { 10, 20, 30 }, new double[] { 1, 2, 3 }, new double[] { 1, -1, 0.5 });
            RunGoodCase(reportPath, report, cases, defects, adapter, binding, part, oracles,
                "F08-10_tilted_plane", tilted, LSource(false), new string[] { "a", "b" }, new string[] { "forward", "forward" },
                "left", "extension", LOutput(8, 2), false);

            RunCase(reportPath, report, cases, defects, "F08-11_nonempty_target_constraints_and_reference",
                "Protect target Driving/Coincident constraints and selected/reference status during native offset.",
                new string[] { "Create a 10 mm L with one unselected reference Line.", "Add one Driving horizontal dimension and one Coincident relation.",
                    "Offset both selected active Lines right 2 mm, independently verify all preserved Tags, values, operands, frames, bodies and references." },
                "APPLIED and independent verify; all preexisting constraints, source curves and reference status preserved; two active output Lines at y=-2 and x=12.",
                delegate(Dictionary<string, object> row) {
                    List<M3SketchGeometryRequest> geometry = LSource(false); geometry.Add(Line("reference", -5, 6, -1, 6));
                    SketchFixture fixture = CreateSketch(adapter, binding, part, "constrained-reference-target", xy, GeometryOnly(geometry), row);
                    Sketch native = FindSketch(part, fixture.SketchId);
                    AddNativeDrivingHorizontalDimension(adapter, binding, session, part, fixture, native, row);
                    AddCoincidentAndReference(session, native, fixture, row);
                    SavePart(part, "constrained and reference F08 fixture");
                    RefreshFixtureGeometryIds(adapter, binding, part, fixture, row, "after_constraints_and_reference");
                    OffsetOracle oracle = ApplyOffset(adapter, binding, part, fixture,
                        new string[] { "a", "b" }, new string[] { "forward", "forward" }, "right", "extension", LOutput(12, -2), false, row);
                    oracles.Add(oracle);
                });

            RunCase(reportPath, report, cases, defects, "F08-12_read_only_prepare_rejections",
                "Reject invalid parameters, chain topology, ownership and unsupported type before native mutation.",
                new string[] { "Create disconnected Lines and one explicit native Ellipse as an unsupported-type fixture.", "Submit each recorded malformed request once through production PrepareSketchOffset.",
                    "Compare strict materialized all-part snapshots, binding revision and dirty state after each rejection." },
                "Each input raises an explicit M2ModelException and leaves the native model, dirty state and revision exactly unchanged.",
                delegate(Dictionary<string, object> row) { RunInvalidRequests(adapter, binding, part, xy, row); });

            RunCase(reportPath, report, cases, defects, "F08-13_14_native_inner_arc_failure_and_Undo",
                "Retire the observed native radius-zero inner-arc failure and prove real Undo recovery.",
                new string[] { "Create a fresh right-angle L; materialize getters and record a strict baseline.",
                    "Prepare left 2 mm join=arc and execute exactly once.", "Capture failed_postcondition and actual Undo evidence; directly compare the restored all-part snapshot." },
                "A native call occurs; invalid native result is rejected with ROLLED_BACK, readable failed_postcondition and verified Undo. No zero-radius Arc remains.",
                delegate(Dictionary<string, object> row) {
                    SketchFixture fixture = CreateSketch(adapter, binding, part, "inner-arc-native-recovery", xy, GeometryOnly(LSource(false)), row);
                    Materialize(adapter, binding, part, row);
                    Dictionary<string, object> before = StrictSnapshot(part); row["native_before"] = before;
                    M3SketchOffsetRequest request = OffsetRequest(binding, fixture, new string[] { "a", "b" }, new string[] { "forward", "forward" }, "left", "arc");
                    row["request"] = OffsetRequestRecord(request); string revision = binding.RevisionId;
                    M3PlanSnapshot plan = adapter.PrepareSketchOffset(binding, request); row["plan"] = plan.PublicView;
                    Require(SameValue(before, StrictSnapshot(part)) && revision == binding.RevisionId, "Inner Arc prepare mutated the model.");
                    M3ExecutionResult execute = adapter.ExecutePlan(binding, plan); row["execute"] = ExecutionRecord(execute);
                    Dictionary<string, object> after = StrictSnapshot(part); row["native_after"] = after; row["snapshot_diff"] = SnapshotDiff(before, after);
                    Require(execute != null && execute.State == "ROLLED_BACK", "Observed degenerate inner Arc must be genuinely rolled back, not APPLIED or UNKNOWN.");
                    Require(NativeCallWasRecorded(execute.Result), "Native recovery fixture did not enter the offset operation.");
                    Require(FindNestedDictionary(execute.Result, "failed_postcondition") != null, "Missing readable failed_postcondition before Undo.");
                    Dictionary<string, object> undo = FindNestedDictionary(execute.Result, "undo_restore"); row["undo_restore"] = undo;
                    Require(undo != null && Object.Equals(undo["undo_to_mark_succeeded"], true), "No successful actual NX Undo was recorded.");
                    Require(SameValue(before, after), "NX Undo did not restore the independent all-part baseline.");
                    row["revision_before"] = revision; row["revision_after_recovery"] = binding.RevisionId;
                    Require(RevisionNotLower(revision, binding.RevisionId), "Undo invalidated monotonic revision tracking.");
                });

            RunCase(reportPath, report, cases, defects, "F08_geometry_checker_controlled_candidates",
                "Challenge geometric validation directly with connected but wrong arc candidates.",
                new string[] { "Prepare a valid outer L arc offset and a standalone quarter-Arc offset.",
                    "Under a temporary Undo mark, construct correct and wrong native candidate outputs; call only the private production geometric validator via test reflection.",
                    "Require correct candidates accepted, same-radius wrong-center join and complementary 270-degree source Arc rejected; Undo candidate objects." },
                "Direct geometry validator rejects wrong center/tangency and complementary sweep, accepts both correct candidates, and temporary candidates are removed by actual Undo.",
                delegate(Dictionary<string, object> row) { RunCheckerCandidates(adapter, binding, session, part, xy, row); });

            RunCase(reportPath, report, cases, defects, "F08-15_stale_plan_and_independent_verify",
                "Reject a stale F08 plan after one controlled other-sketch edit, then execute a fresh plan and verify read-only.",
                new string[] { "Prepare a fresh L offset plan.", "Create one other-sketch Line through production, thereby advancing revision.",
                    "Execute the stale plan once and compare the unchanged native state; then prepare/execute a fresh plan and independently verify native coordinates." },
                "Stale plan returns explicit STALE_PLAN/STALE_REVISION without native call or mutation; fresh offset APPLIED and verify remains read-only.",
                delegate(Dictionary<string, object> row) {
                    SketchFixture fixture = CreateSketch(adapter, binding, part, "stale-target", xy, GeometryOnly(LSource(false)), row);
                    Materialize(adapter, binding, part, row);
                    M3SketchOffsetRequest request = OffsetRequest(binding, fixture, new string[] { "a", "b" }, new string[] { "forward", "forward" }, "left", "extension");
                    M3PlanSnapshot stale = adapter.PrepareSketchOffset(binding, request); row["stale_plan"] = stale.PublicView;
                    CreateSketch(adapter, binding, part, "controlled-other-sketch-change", xy,
                        GeometryOnly(new List<M3SketchGeometryRequest> { Line("control", 30, 30, 40, 30) }), row);
                    Materialize(adapter, binding, part, row); Dictionary<string, object> before = StrictSnapshot(part); string revision = binding.RevisionId;
                    Exception error; M3ExecutionResult execution = ExecuteExpectStale(adapter, binding, stale, out error);
                    row["stale_execute"] = ExecutionRecord(execution); row["stale_exception"] = error == null ? null : error.ToString();
                    row["native_before_stale"] = before; row["native_after_stale"] = StrictSnapshot(part);
                    Require(IsStaleRejection(execution, error), "Stale F08 plan was not explicitly refused.");
                    Require(execution == null || !NativeCallWasRecorded(execution.Result), "Stale F08 plan entered native mutation.");
                    Require(SameValue(before, row["native_after_stale"]) && revision == binding.RevisionId, "Stale rejection changed native state or revision.");
                    Dictionary<string, object> fresh = new Dictionary<string, object>(); row["fresh_operation"] = fresh;
                    RefreshFixtureGeometryIds(adapter, binding, part, fixture, fresh, "after_other_sketch_edit");
                    oracles.Add(ApplyOffset(adapter, binding, part, fixture, new string[] { "a", "b" }, new string[] { "forward", "forward" },
                        "left", "extension", LOutput(8, 2), false, fresh));
                });

            RunCase(reportPath, report, cases, defects, "F08-16_save_copy_reopen_native_numeric_readback",
                "Persist every F08 result and re-read current native curves after production SaveCopy/reopen.",
                new string[] { "Save the unique source model; capture all fixture native records and numerical output expectations.",
                    "Use production SaveCopy(binding, unique-roundtrip-path, true).", "Re-resolve sketch IDs and read each native curve directly; compare source, output, constraints, reference status and connectivity." },
                "SaveCopy is SAVED, reopened, all production checks VERIFIED and all direct per-curve numerical oracles still pass.",
                delegate(Dictionary<string, object> row) {
                    SavePart(part, "completed F08 source evidence");
                    Materialize(adapter, binding, part, row);
                    List<object> rechecks = new List<object>(); row["native_roundtrip_rechecks"] = rechecks;
                    foreach (OffsetOracle oracle in oracles) oracle.BeforeRoundtrip = RoundtripProjection(StrictSketchRecord(FindSketch(part, oracle.Fixture.SketchId)));
                    M2SaveCopyResult saved = adapter.SaveCopy(binding, savedPath, true); row["save_copy"] = SaveCopyRecord(saved);
                    Require(saved != null && saved.State == "SAVED" && saved.Reopened && ChecksPass(saved.Checks), "Production SaveCopy/reopen did not verify.");
                    Part reopened = session.Parts.Work;
                    Require(reopened != null && String.Equals(Path.GetFullPath(reopened.FullPath), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase), "Unique roundtrip model is not Work Part.");
                    Require(File.Exists(sourcePath) && File.Exists(savedPath), "Unique source and roundtrip evidence models are required.");
                    foreach (OffsetOracle oracle in oracles)
                    {
                        Sketch sketch = FindSketch(reopened, oracle.Fixture.SketchId);
                        Dictionary<string, object> check = new Dictionary<string, object> { { "fixture", oracle.Fixture.Name }, { "current_sketch_id", sketch.JournalIdentifier } };
                        rechecks.Add(check); check["native_after_reopen"] = StrictSketchRecord(sketch);
                        Dictionary<string, object> projection = RoundtripProjection((Dictionary<string, object>)check["native_after_reopen"]);
                        check["before_projection"] = oracle.BeforeRoundtrip; check["after_projection"] = projection;
                        Require(SameValue(oracle.BeforeRoundtrip, projection), "Roundtrip changed native source/output geometry, constraints, references or frame for " + oracle.Fixture.Name);
                        List<Curve> output = ResolveExpectedOutputsAfterReopen(sketch, oracle.Fixture, oracle.Expected);
                        CheckOutputOracle(output, oracle.Expected, oracle.Fixture.Frame, oracle.Closed, check);
                        check["production_inspection"] = adapter.InspectSketch(binding, sketch.JournalIdentifier); check["passed"] = true;
                    }
                    row["all_part_after_reopen"] = StrictSnapshot(reopened);
                });
            report["all_cases_passed"] = true;
        }
        catch (Exception error)
        {
            report["error"] = error.ToString();
            report["error_case"] = report.ContainsKey("current_case") ? report["current_case"] : "setup_or_harness";
            if (!report.ContainsKey("current_case")) defects.Add(new Dictionary<string, object> { { "test_case", "setup_or_harness" },
                { "observed_exception", error.ToString() }, { "retest_criterion", "Correct setup and rerun F08 on new unique model/report paths; preserve this report." } });
            if (part != null) try { SavePart(part, "F08 failure-state evidence"); report["failed_state_model_retained"] = part.FullPath; }
                catch (Exception saveError) { report["failure_model_save_error"] = saveError.ToString(); }
        }
        finally
        {
            try { if (binding != null && adapter != null) adapter.Release(binding); }
            catch (Exception error) { report["release_error"] = error.ToString(); }
            try
            {
                if (session != null)
                {
                    List<Part> owned = FindOwnedParts(session, sourcePath, savedPath);
                    if (original != null)
                    {
                        PartLoadStatus status; session.Parts.SetDisplay(original, false, false, out status); if (status != null) status.Dispose(); session.Parts.SetWork(original);
                    }
                    foreach (Part item in owned)
                        if (original == null || item.Tag != original.Tag) item.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null);
                    report["original_session_restored"] = original == null ? session.Parts.Work == null : session.Parts.Work != null && session.Parts.Work.Tag == original.Tag && !original.IsModified;
                }
            }
            catch (Exception error) { AddRestoreError(report, error); }
            bool passed = Object.Equals(report.ContainsKey("all_cases_passed") ? report["all_cases_passed"] : false, true) && cases.Count > 0 && defects.Count == 0;
            foreach (object value in cases) passed &= StringValue((Dictionary<string, object>)value, "status") == "PASSED";
            report["passed"] = passed && !report.ContainsKey("restore_errors") && !report.ContainsKey("release_error") && Object.Equals(report.ContainsKey("original_session_restored") ? report["original_session_restored"] : false, true);
            report["case_count"] = cases.Count; report["defect_count"] = defects.Count; report["finished_utc"] = UtcNow(); Persist(reportPath, report);
        }
    }

    private static List<M3SketchGeometryRequest> LSource(bool mixed)
    {
        return new List<M3SketchGeometryRequest> { Line("a", 0, 0, 10, 0), mixed ? Line("b", 10, 10, 10, 0) : Line("b", 10, 0, 10, 10) };
    }
    private static List<M3SketchGeometryRequest> LOutput(double x, double y)
    {
        return new List<M3SketchGeometryRequest> { Line("a", 0, y, x, y), Line("b", x, y, x, 10) };
    }
    private static List<M3SketchGeometryRequest> Square(double low, double high)
    {
        return new List<M3SketchGeometryRequest> { Line("a", low, low, high, low), Line("b", high, low, high, high), Line("c", high, high, low, high), Line("d", low, high, low, low) };
    }
    private static List<M3SketchGeometryRequest> Capsule(double radius)
    {
        return new List<M3SketchGeometryRequest> { Line("a", 0, -radius, 20, -radius), Arc("b", 20, 0, radius, 270, 180),
            Line("c", 20, radius, 0, radius), Arc("d", 0, 0, radius, 90, 180) };
    }

    private static void RunGoodCase(string path, Dictionary<string, object> report, List<object> cases, List<object> defects,
        M2OpenModelAdapter adapter, M2ModelBinding binding, Part part, List<OffsetOracle> oracles, string id, Frame frame,
        List<M3SketchGeometryRequest> source, string[] chain, string[] directions, string side, string join,
        List<M3SketchGeometryRequest> expected, bool closed)
    {
        RunCase(path, report, cases, defects, id, "Offset an explicitly ordered native chain and prove geometry with an independent numerical oracle.",
            new string[] { "Create the recorded geometry-only source sketch in its explicit frame.", "Resolve current source IDs; prepare distance=2 mm, retain_original=true with recorded traversal, side and join.",
                "Execute once and call independent production VerifyPlan; compare every added curve to the numerical expected set and all preexisting native records by Tag." },
            "Prepare is read-only; execute APPLIED; native call recorded; verify succeeds read-only; added geometry uniquely matches numerical expectations at 1e-6 mm/1e-7 rad and intended connectivity; existing sources and all non-target state preserved.",
            delegate(Dictionary<string, object> row) {
                SketchFixture fixture = CreateSketch(adapter, binding, part, id, frame, GeometryOnly(source), row);
                oracles.Add(ApplyOffset(adapter, binding, part, fixture, chain, directions, side, join, expected, closed, row));
            });
    }

    private static M3SketchOffsetRequest OffsetRequest(M2ModelBinding binding, SketchFixture fixture,
        string[] chain, string[] directions, string side, string join)
    {
        List<M3SketchOffsetCurveRequest> entries = new List<M3SketchOffsetCurveRequest>();
        for (int i = 0; i < chain.Length; i++) entries.Add(new M3SketchOffsetCurveRequest { CurveId = fixture.GeometryIdsByRequestId[chain[i]], Direction = directions[i] });
        return new M3SketchOffsetRequest { RevisionId = binding.RevisionId, SketchId = fixture.SketchId,
            CurveChain = entries, DistanceMm = 2, Side = side, RetainOriginal = true, JoinMode = join };
    }
    private static Dictionary<string, object> OffsetRequestRecord(M3SketchOffsetRequest request)
    {
        List<object> chain = new List<object>();
        if (request.CurveChain != null) foreach (M3SketchOffsetCurveRequest entry in request.CurveChain)
            chain.Add(entry == null ? null : new Dictionary<string, object> { { "curve_id", entry.CurveId }, { "direction", entry.Direction } });
        return new Dictionary<string, object> { { "revision_id", request.RevisionId }, { "sketch_id", request.SketchId }, { "curve_chain", chain },
            { "distance_mm", JsonNumberValue(request.DistanceMm) }, { "side", request.Side }, { "retain_original", request.RetainOriginal }, { "join_mode", request.JoinMode } };
    }

    private static OffsetOracle ApplyOffset(M2OpenModelAdapter adapter, M2ModelBinding binding, Part part, SketchFixture fixture,
        string[] chain, string[] directions, string side, string join, List<M3SketchGeometryRequest> expected, bool closed, Dictionary<string, object> row)
    {
        RefreshFixtureGeometryIds(adapter, binding, part, fixture, row, "current_before_offset");
        Materialize(adapter, binding, part, row);
        M3SketchOffsetRequest request = OffsetRequest(binding, fixture, chain, directions, side, join);
        row["request"] = OffsetRequestRecord(request); row["expected_geometry"] = SketchRequestRecord(GeometryOnly(expected)); row["expected_closed"] = closed;
        row["expected_frame"] = FrameRecord(fixture.Frame);
        Dictionary<string, object> before = StrictSnapshot(part); row["native_before"] = before;
        HashSet<string> baseline = CurveTags(FindSketch(part, fixture.SketchId)); string revision = binding.RevisionId;
        M3PlanSnapshot plan = adapter.PrepareSketchOffset(binding, request); row["plan"] = plan == null ? null : plan.PublicView;
        row["native_after_prepare"] = StrictSnapshot(part); row["revision_after_prepare"] = binding.RevisionId;
        Require(plan != null && plan.Kind == "edit_sketch_offset", "Unexpected F08 plan kind.");
        Require(SameValue(before, row["native_after_prepare"]) && revision == binding.RevisionId, "F08 prepare changed strict native baseline or revision.");
        M3ExecutionResult execute = adapter.ExecutePlan(binding, plan); row["execute"] = ExecutionRecord(execute);
        row["native_after_execute"] = StrictSnapshot(part);
        Require(execute != null && execute.State == "APPLIED", "F08 ExecutePlan did not return APPLIED: " + M2Json.CanonicalText(ExecutionRecord(execute)));
        Require(NativeCallWasRecorded(execute.Result), "APPLIED F08 operation lacks native call evidence.");
        Require(revision != binding.RevisionId, "F08 edit did not advance revision.");
        Dictionary<string, object> after = (Dictionary<string, object>)row["native_after_execute"];
        CheckPreservation(before, after, fixture.SketchId, row);
        M3VerificationResult verify = adapter.VerifyPlan(binding, plan); row["verification"] = VerificationRecord(verify);
        row["native_after_verify"] = StrictSnapshot(part);
        Require(verify != null && verify.Verified && ChecksPass(verify.Checks), "Independent F08 VerifyPlan failed: " + M2Json.CanonicalText(VerificationRecord(verify)));
        Require(SameValue(after, row["native_after_verify"]), "Independent F08 verification mutated the native state.");
        Sketch sketch = FindSketch(part, fixture.SketchId); List<Curve> outputs = new List<Curve>(); List<string> tags = new List<string>();
        foreach (NXObject item in sketch.GetAllGeometry()) if (item is Curve && !baseline.Contains(item.Tag.ToString())) { outputs.Add((Curve)item); tags.Add(item.Tag.ToString()); }
        CheckOutputOracle(outputs, expected, fixture.Frame, closed, row);
        CheckPublicOffsetEvidence(execute.Result, verify.Result, outputs, closed, row);
        if (join == "arc")
        {
            Arc connector = null; List<Line> lines = new List<Line>();
            foreach (Curve curve in outputs) { if (curve is Arc) connector = (Arc)curve; if (curve is Line) lines.Add((Line)curve); }
            Require(connector != null && lines.Count == 2, "Outer L arc oracle expected exactly one connector and two Lines.");
            PointNear(connector.CenterPoint, WorldPoint(fixture.Frame, 10, 0), "Outer Arc center");
            Require(Math.Abs(ArcLength(connector) - Pi) <= LengthToleranceMm && Math.Abs(Math.Abs(connector.EndAngle - connector.StartAngle) - Pi / 2) <= AngleToleranceRad, "Outer Arc sweep or arc length incorrect.");
            foreach (Line line in lines)
            {
                Point3d contact = PointDistance(line.StartPoint, ArcEndpoint(connector, true)) <= PositionToleranceMm || PointDistance(line.StartPoint, ArcEndpoint(connector, false)) <= PositionToleranceMm ? line.StartPoint : line.EndPoint;
                VerifyLineArcTangent(connector, line, contact, fixture.Frame.Normal, "F08 outer connector");
            }
            // Object parameter direction is independent of the requested traversal.
            // Resolve the connector endpoints in that traversal before checking its sign.
            bool reversedTraversal = directions[0] == "reverse";
            Point3d traversalStart = WorldPoint(fixture.Frame, reversedTraversal ? 12 : 10, reversedTraversal ? 0 : -2);
            Point3d traversalEnd = WorldPoint(fixture.Frame, reversedTraversal ? 10 : 12, reversedTraversal ? -2 : 0);
            Require((PointDistance(ArcEndpoint(connector, true), traversalStart) <= PositionToleranceMm && PointDistance(ArcEndpoint(connector, false), traversalEnd) <= PositionToleranceMm) ||
                (PointDistance(ArcEndpoint(connector, false), traversalStart) <= PositionToleranceMm && PointDistance(ArcEndpoint(connector, true), traversalEnd) <= PositionToleranceMm), "Native connector endpoints do not match traversal contacts.");
            double[] firstRadius = LocalVector(fixture.Frame, Sub(traversalStart, connector.CenterPoint));
            double[] lastRadius = LocalVector(fixture.Frame, Sub(traversalEnd, connector.CenterPoint));
            double orientedSweep = Math.Atan2(Cross2(firstRadius, lastRadius), Dot2(firstRadius, lastRadius));
            row["connector_traversal_start_mm"] = Point(traversalStart); row["connector_traversal_end_mm"] = Point(traversalEnd);
            row["connector_traversal_signed_sweep_rad"] = orientedSweep;
            row["connector_native_start_mm"] = Point(ArcEndpoint(connector, true)); row["connector_native_end_mm"] = Point(ArcEndpoint(connector, false));
            Require(Math.Abs(orientedSweep - (reversedTraversal ? -Pi / 2 : Pi / 2)) <= AngleToleranceRad, "Native outer connector traversal sweep has the wrong sign.");
        }
        return new OffsetOracle { Fixture = fixture, Expected = expected, OutputTags = tags, Closed = closed };
    }

    private static void CheckPublicOffsetEvidence(Dictionary<string, object> execution, Dictionary<string, object> verification,
        List<Curve> outputs, bool closed, Dictionary<string, object> row)
    {
        Dictionary<string, object> executeEvidence = DirectDictionary(execution, "evidence");
        Dictionary<string, object> verifyEvidence = DirectDictionary(DirectDictionary(verification, "evidence"), "live_geometry");
        foreach (Dictionary<string, object> result in new Dictionary<string, object>[] { execution, verification })
        {
            Require(result.ContainsKey("source_chain_closed"), "Public result must name input closure source_chain_closed.");
            Require(result.ContainsKey("output_closed") && Object.Equals(result["output_closed"], closed), "Public result output_closed does not describe the live output.");
            CheckPublicCurveRecords(result, outputs);
        }
        foreach (Dictionary<string, object> evidence in new Dictionary<string, object>[] { executeEvidence, verifyEvidence })
        {
            Dictionary<string, object> geometry = DirectDictionary(evidence, "offset_geometry");
            Require(Object.Equals(evidence["output_closed"], closed) && Object.Equals(geometry["output_closed"], closed), "Public checker output closure differs from independent native topology.");
            Require(Object.Equals(evidence["verified"], true) && Object.Equals(geometry["valid"], true), "Public live checker did not report valid geometry.");
            CheckPublicCurveRecords(evidence, outputs); CheckPublicCurveRecords(geometry, outputs);
        }
        row["public_live_evidence_structure_verified"] = true;
    }

    private static Dictionary<string, object> DirectDictionary(Dictionary<string, object> parent, string key)
    {
        object value;
        Dictionary<string, object> result = parent != null && parent.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        Require(result != null, "Missing directly addressable public dictionary: " + key); return result;
    }

    private static void CheckPublicCurveRecords(Dictionary<string, object> container, List<Curve> outputs)
    {
        object value;
        List<object> records = container.TryGetValue("output_curve_records", out value) ? value as List<object> : null;
        Require(records != null && records.Count == outputs.Count, "Public output_curve_records does not contain the complete live output set.");
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (object item in records)
        {
            Dictionary<string, object> record = item as Dictionary<string, object>;
            Require(record != null, "Public output curve record is not structured.");
            string tag = StringValue(record, "tag"); Curve found = null;
            foreach (Curve curve in outputs) if (curve.Tag.ToString() == tag) found = curve;
            Require(found != null && seen.Add(tag), "Public output curve record is missing, duplicated or stale.");
            Line line = found as Line; Arc arc = found as Arc;
            PointNear(Point3dFromArray(DoubleArray(record["start_mm"])), line == null ? ArcEndpoint(arc, true) : line.StartPoint, "Public live output start");
            PointNear(Point3dFromArray(DoubleArray(record["end_mm"])), line == null ? ArcEndpoint(arc, false) : line.EndPoint, "Public live output end");
            if (arc != null)
            {
                PointNear(Point3dFromArray(DoubleArray(record["center_mm"])), arc.CenterPoint, "Public live output center");
                Require(Math.Abs(Convert.ToDouble(record["radius_mm"], CultureInfo.InvariantCulture) - arc.Radius) <= LengthToleranceMm, "Public live output radius is stale.");
            }
        }
    }

    private static double[] LocalVector(Frame frame, Point3d point)
    {
        double[] value = new double[] { point.X, point.Y, point.Z };
        return new double[] { Dot3(value, frame.XAxis), Dot3(value, frame.YAxis) };
    }

    private static HashSet<string> CurveTags(Sketch sketch)
    {
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (NXObject item in sketch.GetAllGeometry()) if (item is Curve) tags.Add(item.Tag.ToString());
        return tags;
    }

    private static void Materialize(M2OpenModelAdapter adapter, M2ModelBinding binding, Part part, Dictionary<string, object> row)
    {
        Dictionary<string, object> warmup = new Dictionary<string, object>();
        GetOrCreateEvidenceList(row, "explicit_materialization_reads").Add(warmup);
        warmup["before_native_read"] = StrictSnapshot(part);
        warmup["production_inspect"] = adapter.InspectPart(binding);
        warmup["after_native_read"] = StrictSnapshot(part);
        warmup["revision_after"] = binding.RevisionId;
        // Materialization read effects are retained; no dirty bit is ever cleared.
        Dictionary<string, object> stable = StrictSnapshot(part);
        warmup["strict_baseline_stable"] = SameValue(warmup["after_native_read"], stable);
        Require(Object.Equals(warmup["strict_baseline_stable"], true), "Native getters did not settle before strict F08 baseline.");
    }

    private static void CheckOutputOracle(List<Curve> outputs, List<M3SketchGeometryRequest> expected, Frame frame, bool closed, Dictionary<string, object> row)
    {
        row["actual_output_count"] = outputs.Count; row["expected_output_count"] = expected.Count;
        List<object> matches = new List<object>(); row["independent_numeric_matches"] = matches;
        Require(outputs.Count == expected.Count, "Native output count does not match the complete independent expected set.");
        HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        double perimeter = 0;
        foreach (M3SketchGeometryRequest request in expected)
        {
            Curve match = null; int count = 0;
            foreach (Curve curve in outputs) if (MatchesNumericCurve(curve, request, frame)) { match = curve; count++; }
            matches.Add(new Dictionary<string, object> { { "expected_request_id", request.Id }, { "matching_native_curves", count },
                { "native_tag", match == null ? null : match.Tag.ToString() }, { "native_journal_id", match == null ? null : match.JournalIdentifier } });
            Require(count == 1 && used.Add(match.Tag.ToString()), "Expected native curve did not have one unique independent geometry match: " + request.Id);
            // Match is separately checked to belong to target Sketch by preservation and caller.
            perimeter += match.GetLength();
        }
        List<Point3d> nodes = new List<Point3d>(); List<int> degrees = new List<int>(); List<int[]> edges = new List<int[]>();
        foreach (Curve curve in outputs)
        {
            Line line = curve as Line; Arc arc = curve as Arc;
            Point3d start = line != null ? line.StartPoint : ArcEndpoint(arc, true);
            Point3d end = line != null ? line.EndPoint : ArcEndpoint(arc, false);
            int a = EndpointNode(nodes, degrees, start), b = EndpointNode(nodes, degrees, end);
            degrees[a]++; degrees[b]++; edges.Add(new int[] { a, b });
        }
        int loose = 0; foreach (int degree in degrees) { if (degree == 1) loose++; else Require(degree == 2, "Output chain has an invalid branch/interior endpoint degree."); }
        HashSet<int> visited = new HashSet<int>(); if (nodes.Count > 0) visited.Add(0);
        bool progress = true; while (progress) { progress = false; foreach (int[] edge in edges) if (visited.Contains(edge[0]) || visited.Contains(edge[1])) { progress |= visited.Add(edge[0]); progress |= visited.Add(edge[1]); } }
        Require(visited.Count == nodes.Count && loose == (closed ? 0 : 2), "Independent native endpoint graph does not form the requested one connected open/closed chain.");
        row["independent_topology"] = new Dictionary<string, object> { { "node_degrees", degrees }, { "connected", visited.Count == nodes.Count }, { "loose_endpoints", loose }, { "closed", loose == 0 } };
        double expectedPerimeter = 0; foreach (M3SketchGeometryRequest request in expected) expectedPerimeter += ExpectedLength(request);
        row["measured_perimeter_mm"] = perimeter; row["expected_perimeter_mm"] = expectedPerimeter;
        Require(Math.Abs(perimeter - expectedPerimeter) <= LengthToleranceMm, "Independent native total curve length differs from oracle.");
        if (closed)
        {
            double expectedArea = 0; foreach (M3SketchGeometryRequest request in expected) expectedArea += ExpectedAreaIntegral(request);
            row["expected_enclosed_area_mm2"] = Math.Abs(expectedArea);
            // Every native curve is already matched with its exact endpoints, radius, span and plane.
            // Compute an independent native Green integral after ordering the endpoint graph.
            double area = OrderedNativeArea(outputs, frame); row["measured_enclosed_area_mm2"] = Math.Abs(area);
            Require(Math.Abs(Math.Abs(area) - Math.Abs(expectedArea)) <= PositionToleranceMm * Math.Max(1, expectedPerimeter), "Native closed-loop area differs from analytic oracle.");
        }
        row["independent_numeric_oracle_passed"] = true;
    }

    private static int EndpointNode(List<Point3d> nodes, List<int> degrees, Point3d point)
    {
        for (int i = 0; i < nodes.Count; i++) if (PointDistance(nodes[i], point) <= PositionToleranceMm) return i;
        nodes.Add(point); degrees.Add(0); return nodes.Count - 1;
    }
    private static bool MatchesNumericCurve(Curve curve, M3SketchGeometryRequest request, Frame frame)
    {
        Line line = curve as Line; Arc arc = curve as Arc;
        if (request.Type == "line") return line != null && LineHasEndpoints(line, WorldPoint(frame, request.StartMm[0], request.StartMm[1]), WorldPoint(frame, request.EndMm[0], request.EndMm[1]));
        if (arc == null) return false;
        double radius = request.Type == "circle" ? request.DiameterMm / 2 : request.RadiusMm;
        double sweep = request.Type == "circle" ? 2 * Pi : request.SweepDeg * Pi / 180;
        if (PointDistance(arc.CenterPoint, WorldPoint(frame, request.CenterMm[0], request.CenterMm[1])) > PositionToleranceMm || Math.Abs(arc.Radius - radius) > LengthToleranceMm ||
            Math.Abs(Math.Abs(arc.EndAngle - arc.StartAngle) - Math.Abs(sweep)) > AngleToleranceRad || Math.Abs(ArcLength(arc) - radius * Math.Abs(sweep)) > LengthToleranceMm) return false;
        Point3d center; Vector3d x, y; arc.GetOrientation(out center, out x, out y);
        double[] actualNormal = Normalize3(Cross3(new double[] { x.X, x.Y, x.Z }, new double[] { y.X, y.Y, y.Z }));
        if (Math.Abs(Math.Abs(Dot3(actualNormal, frame.Normal)) - 1) > FrameTolerance) return false;
        if (request.Type == "circle") return PointDistance(ArcEndpoint(arc, true), ArcEndpoint(arc, false)) <= PositionToleranceMm;
        double start = request.StartAngleDeg * Pi / 180, end = start + sweep;
        Point3d a = WorldPoint(frame, request.CenterMm[0] + radius * Math.Cos(start), request.CenterMm[1] + radius * Math.Sin(start));
        Point3d b = WorldPoint(frame, request.CenterMm[0] + radius * Math.Cos(end), request.CenterMm[1] + radius * Math.Sin(end));
        return (PointDistance(ArcEndpoint(arc, true), a) <= PositionToleranceMm && PointDistance(ArcEndpoint(arc, false), b) <= PositionToleranceMm) ||
            (PointDistance(ArcEndpoint(arc, false), a) <= PositionToleranceMm && PointDistance(ArcEndpoint(arc, true), b) <= PositionToleranceMm);
    }
    private static double ExpectedLength(M3SketchGeometryRequest request)
    {
        if (request.Type == "line") return VectorDistance(request.StartMm, request.EndMm, 2);
        return request.Type == "circle" ? Pi * request.DiameterMm : request.RadiusMm * Math.Abs(request.SweepDeg) * Pi / 180;
    }
    private static double ExpectedAreaIntegral(M3SketchGeometryRequest request)
    {
        if (request.Type == "line") return Cross2(request.StartMm, request.EndMm) / 2;
        double radius = request.Type == "circle" ? request.DiameterMm / 2 : request.RadiusMm;
        double a = request.Type == "circle" ? 0 : request.StartAngleDeg * Pi / 180;
        double sweep = request.Type == "circle" ? 2 * Pi : request.SweepDeg * Pi / 180;
        double b = a + sweep;
        return (radius * request.CenterMm[0] * (Math.Sin(b) - Math.Sin(a)) + radius * request.CenterMm[1] * (Math.Cos(a) - Math.Cos(b)) + radius * radius * sweep) / 2;
    }
    private static double OrderedNativeArea(List<Curve> curves, Frame frame)
    {
        HashSet<string> used = new HashSet<string>(StringComparer.Ordinal); Point3d? cursor = null; double area = 0;
        for (int count = 0; count < curves.Count; count++)
        {
            Curve found = null; bool reverse = false;
            foreach (Curve curve in curves)
            {
                if (used.Contains(curve.Tag.ToString())) continue;
                Line line = curve as Line; Arc arc = curve as Arc;
                Point3d a = line == null ? ArcEndpoint(arc, true) : line.StartPoint, b = line == null ? ArcEndpoint(arc, false) : line.EndPoint;
                if (cursor == null || PointDistance(cursor.Value, a) <= PositionToleranceMm || PointDistance(cursor.Value, b) <= PositionToleranceMm)
                { found = curve; reverse = cursor != null && PointDistance(cursor.Value, b) <= PositionToleranceMm && PointDistance(cursor.Value, a) > PositionToleranceMm; break; }
            }
            Require(found != null, "Could not order native closed loop for independent area integral."); used.Add(found.Tag.ToString());
            Line ln = found as Line; Arc ar = found as Arc;
            if (ln != null)
            {
                double[] a = LocalVector(frame, Sub(ln.StartPoint, WorldPoint(frame, 0, 0))), b = LocalVector(frame, Sub(ln.EndPoint, WorldPoint(frame, 0, 0)));
                area += (reverse ? -1 : 1) * Cross2(a, b) / 2; cursor = reverse ? ln.StartPoint : ln.EndPoint;
            }
            else
            {
                Point3d center; Vector3d x, y; ar.GetOrientation(out center, out x, out y);
                double[] c = LocalVector(frame, Sub(center, WorldPoint(frame, 0, 0)));
                double[] a = LocalVector(frame, Sub(ArcEndpoint(ar, true), center)), b = LocalVector(frame, Sub(ArcEndpoint(ar, false), center));
                double normalSign = Dot3(Cross3(new double[] { x.X, x.Y, x.Z }, new double[] { y.X, y.Y, y.Z }), frame.Normal) >= 0 ? 1 : -1;
                double sweep = (ar.EndAngle - ar.StartAngle) * normalSign;
                double integral = (c[0] * (b[1] - a[1]) - c[1] * (b[0] - a[0]) + ar.Radius * ar.Radius * sweep) / 2;
                area += (reverse ? -1 : 1) * integral; cursor = reverse ? ArcEndpoint(ar, true) : ArcEndpoint(ar, false);
            }
        }
        return area;
    }
    private static Dictionary<string, object> StrictSketchRecord(Sketch sketch)
    {
        Dictionary<string, object> geometry = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (object value in NativeGeometryRecords(sketch))
        {
            Dictionary<string, object> record = (Dictionary<string, object>)value;
            string tag = StringValue(record, "tag");
            NXObject item = null; foreach (NXObject candidate in sketch.GetAllGeometry()) if (candidate.Tag.ToString() == tag) { item = candidate; break; }
            Curve curve = item as Curve;
            if (curve != null)
            {
                NXOpen.UF.UFSket.ReferenceStatus status;
                NXOpen.UF.UFSession.GetUFSession().Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status);
                record["reference_status"] = (int)status;
                record["native_length_mm"] = curve.GetLength();
                Arc arc = curve as Arc;
                if (arc != null) { Point3d center; Vector3d x, y; arc.GetOrientation(out center, out x, out y); record["plane_x_axis"] = new double[] { x.X, x.Y, x.Z }; record["plane_y_axis"] = new double[] { y.X, y.Y, y.Z }; }
            }
            geometry[tag] = record;
        }
        Dictionary<string, object> constraints = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] values = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
            if (values == null) continue;
            foreach (SketchConstraint constraint in values)
            {
                Require(constraint != null, "Null native constraint.");
                string tag = constraint.Tag.ToString(); if (constraints.ContainsKey(tag)) continue;
                Dictionary<string, object> record = new Dictionary<string, object> { { "tag", tag }, { "journal_id", constraint.JournalIdentifier }, { "constraint_type", type.ToString() }, { "class", constraint.GetType().Name } };
                SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                List<object> operands = new List<object>(); record["operands"] = operands;
                if (dimension != null)
                {
                    Expression expression = dimension.AssociatedExpression;
                    record["dimension_state"] = dimension.DimensionState.ToString(); record["rhs"] = expression == null ? null : expression.RightHandSide;
                    record["value"] = expression == null ? (object)null : expression.Value; record["unit"] = expression == null || expression.Units == null ? null : expression.Units.Symbol;
                    record["expression_name"] = expression == null ? null : expression.Name;
                    foreach (Sketch.DimensionGeometry operand in dimension.GetDimensionGeometry()) operands.Add(new Dictionary<string, object> {
                        { "geometry_tag", operand.Geometry == null ? null : operand.Geometry.Tag.ToString() }, { "association", operand.AssocType.ToString() }, { "value", operand.AssocValue }
                    });
                }
                else
                {
                    SketchGeometricConstraint geometric = constraint as SketchGeometricConstraint;
                    if (geometric != null) foreach (Sketch.ConstraintGeometry operand in geometric.GetGeometry()) operands.Add(new Dictionary<string, object> {
                        { "geometry_tag", operand.Geometry == null ? null : operand.Geometry.Tag.ToString() }, { "point_type", operand.PointType.ToString() }, { "spline_point_index", operand.SplineDefiningPointIndex }
                    });
                }
                constraints[tag] = record;
            }
        }
        Matrix3x3 axes = sketch.Orientation.Element; int dof; Sketch.Status solver = sketch.GetStatus(out dof);
        return new Dictionary<string, object> {
            { "tag", sketch.Tag.ToString() }, { "journal_id", sketch.JournalIdentifier }, { "feature_tag", sketch.Feature.Tag.ToString() },
            { "geometry", geometry }, { "constraints", constraints }, { "origin_mm", Point(sketch.Origin) },
            { "frame_axes", new double[] { axes.Xx, axes.Xy, axes.Xz, axes.Yx, axes.Yy, axes.Yz, axes.Zx, axes.Zy, axes.Zz } },
            { "solver_status", solver.ToString() }, { "degrees_of_freedom", dof }
        };
    }

    private static Dictionary<string, object> StrictSnapshot(Part part)
    {
        Dictionary<string, object> sketches = new Dictionary<string, object>();
        foreach (Sketch sketch in part.Sketches.ToArray()) sketches[sketch.Tag.ToString()] = StrictSketchRecord(sketch);
        List<object> features = new List<object>();
        foreach (Feature feature in part.Features.GetFeatures()) features.Add(new Dictionary<string, object> {
            { "tag", feature.Tag.ToString() }, { "journal_id", feature.JournalIdentifier }, { "feature_type", feature.FeatureType }, { "suppressed", feature.Suppressed }
        });
        Dictionary<string, object> bodies = new Dictionary<string, object>();
        foreach (Body body in part.Bodies.ToArray())
        {
            double[] box = new double[6]; NXOpen.UF.UFSession.GetUFSession().Modl.AskBoundingBox(body.Tag, box);
            List<string> faces = new List<string>(); foreach (Face face in body.GetFaces()) faces.Add(face.Tag.ToString()); faces.Sort(StringComparer.Ordinal);
            List<string> edges = new List<string>(); foreach (Edge edge in body.GetEdges()) edges.Add(edge.Tag.ToString()); edges.Sort(StringComparer.Ordinal);
            bodies[body.Tag.ToString()] = new Dictionary<string, object> { { "tag", body.Tag.ToString() }, { "journal_id", body.JournalIdentifier },
                { "solid", body.IsSolidBody }, { "bounding_box_mm", box }, { "face_tags", faces }, { "edge_tags", edges } };
        }
        List<string> globalCurves = new List<string>(); foreach (Curve curve in part.Curves) globalCurves.Add(curve.Tag.ToString()); globalCurves.Sort(StringComparer.Ordinal);
        return new Dictionary<string, object> { { "part_tag", part.Tag.ToString() }, { "part_path", part.FullPath }, { "units", part.PartUnits.ToString() },
            { "sketches_by_tag", sketches }, { "features_in_history_order", features }, { "bodies_by_tag", bodies }, { "global_curve_tags", globalCurves },
            { "active_sketch_tag", Session.GetSession().ActiveSketch == null ? null : Session.GetSession().ActiveSketch.Tag.ToString() }, { "is_modified", part.IsModified } };
    }

    private static object StableProjection(object value)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> pair in dictionary) if (pair.Key != "journal_id") result[pair.Key] = StableProjection(pair.Value);
            return result;
        }
        List<object> list = value as List<object>;
        if (list != null) { List<object> result = new List<object>(); foreach (object item in list) result.Add(StableProjection(item)); return result; }
        return value;
    }

    private static void CheckPreservation(Dictionary<string, object> before, Dictionary<string, object> after, string targetId, Dictionary<string, object> row)
    {
        Dictionary<string, object> beforeSketches = (Dictionary<string, object>)before["sketches_by_tag"], afterSketches = (Dictionary<string, object>)after["sketches_by_tag"];
        Require(beforeSketches.Count == afterSketches.Count, "Offset changed Sketch inventory.");
        int sourceCount = 0; int constraints = 0; int nonTargets = 0;
        foreach (KeyValuePair<string, object> pair in beforeSketches)
        {
            Require(afterSketches.ContainsKey(pair.Key), "Offset removed preexisting native Sketch.");
            Dictionary<string, object> original = (Dictionary<string, object>)pair.Value, current = (Dictionary<string, object>)afterSketches[pair.Key];
            Dictionary<string, object> originalGeometry = (Dictionary<string, object>)original["geometry"], currentGeometry = (Dictionary<string, object>)current["geometry"];
            bool target = StringValue(original, "journal_id") == targetId;
            if (!target) { Require(SameValue(StableProjection(original), StableProjection(current)), "Offset changed non-target Sketch, constraints, frame or solver state."); nonTargets++; }
            else
            {
                foreach (KeyValuePair<string, object> geometry in originalGeometry)
                {
                    Require(currentGeometry.ContainsKey(geometry.Key) && SameValue(StableProjection(geometry.Value), StableProjection(currentGeometry[geometry.Key])), "Offset altered or removed original native geometry/reference Tag " + geometry.Key);
                    sourceCount++;
                }
                Require(SameValue(StableProjection(original["constraints"]), StableProjection(current["constraints"])), "Offset changed existing target constraints or their operands/RHS/state.");
                Require(SameValue(original["origin_mm"], current["origin_mm"]) && SameValue(original["frame_axes"], current["frame_axes"]), "Offset changed target sketch frame.");
                string status = StringValue(current, "solver_status"); Require(status == "UnderConstrained" || status == "WellConstrained", "Offset left target solver in an abnormal state.");
                foreach (KeyValuePair<string, object> added in currentGeometry) if (!originalGeometry.ContainsKey(added.Key))
                {
                    Dictionary<string, object> record = (Dictionary<string, object>)added.Value;
                    Require(Object.Equals(record["is_curve"], true) && Object.Equals(record["is_sketch_geometry"], true) && Convert.ToInt32(record["reference_status"], CultureInfo.InvariantCulture) == 1,
                        "Unexpected added object or reference output geometry.");
                }
            }
            constraints += ((Dictionary<string, object>)original["constraints"]).Count;
        }
        foreach (string key in new string[] { "part_tag", "part_path", "units", "features_in_history_order", "bodies_by_tag", "active_sketch_tag" })
            Require(SameValue(StableProjection(before[key]), StableProjection(after[key])), "Offset changed protected native " + key);
        HashSet<string> globalBefore = new HashSet<string>((List<string>)before["global_curve_tags"], StringComparer.Ordinal);
        HashSet<string> globalAfter = new HashSet<string>((List<string>)after["global_curve_tags"], StringComparer.Ordinal);
        foreach (string tag in globalBefore) Require(globalAfter.Contains(tag), "Offset removed a global native curve.");
        Dictionary<string, object> targetAfter = null; Dictionary<string, object> targetBefore = null;
        foreach (object value in afterSketches.Values) if (StringValue((Dictionary<string, object>)value, "journal_id") == targetId) targetAfter = (Dictionary<string, object>)value;
        foreach (object value in beforeSketches.Values) if (StringValue((Dictionary<string, object>)value, "journal_id") == targetId) targetBefore = (Dictionary<string, object>)value;
        Require(targetAfter != null && targetBefore != null, "Cannot independently identify the unchanged target Sketch.");
        Require(globalAfter.Count - globalBefore.Count == ((Dictionary<string, object>)targetAfter["geometry"]).Count - ((Dictionary<string, object>)targetBefore["geometry"]).Count,
            "Offset added unexpected geometry outside target Sketch.");
        row["independent_preservation"] = new Dictionary<string, object> { { "source_objects_preserved", sourceCount }, { "preexisting_constraints_preserved", constraints },
            { "non_target_sketches_preserved", nonTargets }, { "body_count_preserved", ((Dictionary<string, object>)before["bodies_by_tag"]).Count }, { "passed", true } };
    }

    private static Dictionary<string, object> RoundtripProjection(Dictionary<string, object> sketch)
    {
        // Reopening may change Tags and JournalIdentifiers. Compare a sorted multiset
        // of full numerical records and bind constraint operands to those records.
        // These are canonical JSON strings, not file or content hashes.
        Dictionary<string, object> geometry = (Dictionary<string, object>)sketch["geometry"];
        Dictionary<string, string> tagToGeometry = new Dictionary<string, string>();
        List<string> geometryRecords = new List<string>();
        foreach (KeyValuePair<string, object> pair in geometry)
        {
            Dictionary<string, object> record = new Dictionary<string, object>((Dictionary<string, object>)pair.Value);
            record.Remove("tag"); record.Remove("journal_id");
            string identity = M2Json.CanonicalText(record);
            tagToGeometry[pair.Key] = identity; geometryRecords.Add(identity);
        }
        geometryRecords.Sort(StringComparer.Ordinal);
        List<string> constraintRecords = new List<string>();
        foreach (object value in ((Dictionary<string, object>)sketch["constraints"]).Values)
        {
            Dictionary<string, object> record = new Dictionary<string, object>((Dictionary<string, object>)value);
            record.Remove("tag"); record.Remove("journal_id");
            List<string> operands = new List<string>();
            foreach (object operandValue in (List<object>)record["operands"])
            {
                Dictionary<string, object> operand = new Dictionary<string, object>((Dictionary<string, object>)operandValue);
                string tag = StringValue(operand, "geometry_tag"); operand.Remove("geometry_tag");
                Require(tag == null || tagToGeometry.ContainsKey(tag), "Roundtrip constraint operand is outside captured native geometry.");
                operand["geometry_numeric_identity"] = tag == null ? null : tagToGeometry[tag];
                operands.Add(M2Json.CanonicalText(operand));
            }
            operands.Sort(StringComparer.Ordinal); record["operands"] = operands;
            constraintRecords.Add(M2Json.CanonicalText(record));
        }
        constraintRecords.Sort(StringComparer.Ordinal);
        return new Dictionary<string, object> { { "geometry_numeric_records", geometryRecords }, { "constraint_numeric_records", constraintRecords },
            { "origin_mm", sketch["origin_mm"] }, { "frame_axes", sketch["frame_axes"] }, { "solver_status", sketch["solver_status"] }, { "degrees_of_freedom", sketch["degrees_of_freedom"] } };
    }

    private static List<Curve> ResolveExpectedOutputsAfterReopen(Sketch sketch, SketchFixture fixture, List<M3SketchGeometryRequest> expected)
    {
        List<Curve> output = new List<Curve>();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve; if (curve == null) continue;
            bool isOutput = false; foreach (M3SketchGeometryRequest request in expected) if (MatchesNumericCurve(curve, request, fixture.Frame)) isOutput = true;
            if (isOutput)
            {
                NXOpen.UF.UFSket.ReferenceStatus status; NXOpen.UF.UFSession.GetUFSession().Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status);
                Require((int)status == 1 && sketch.IsSketchGeometry(curve), "Reopened output is reference or not target Sketch geometry."); output.Add(curve);
            }
        }
        foreach (M3SketchGeometryRequest source in fixture.GeometryRequests)
        {
            int count = 0; foreach (NXObject item in sketch.GetAllGeometry()) if (item is Curve && MatchesNumericCurve((Curve)item, source, fixture.Frame)) count++;
            Require(count == 1, "Retained source did not uniquely survive save/reopen: " + source.Id);
        }
        return output;
    }

    private static void AddCoincidentAndReference(Session session, Sketch native, SketchFixture fixture, Dictionary<string, object> row)
    {
        Line a = FindLine(native, WorldPoint(fixture.Frame, 0, 0), WorldPoint(fixture.Frame, 10, 0));
        Line b = FindLine(native, WorldPoint(fixture.Frame, 10, 0), WorldPoint(fixture.Frame, 10, 10));
        Line reference = FindLine(native, WorldPoint(fixture.Frame, -5, 6), WorldPoint(fixture.Frame, -1, 6));
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F08 constrained target fixture"); bool active = false;
        try
        {
            native.Activate(Sketch.ViewReorient.False); active = true;
            SketchGeometricConstraint relation = native.CreateCoincidentConstraint(new Sketch.ConstraintGeometry(a, Sketch.ConstraintPointType.EndVertex, 0), new Sketch.ConstraintGeometry(b, Sketch.ConstraintPointType.StartVertex, 0));
            Require(relation != null, "Failed to create nonempty Coincident relation fixture.");
            NXOpen.UF.UFSession.GetUFSession().Sket.SetReferenceStatus(native.Tag, reference.Tag, (NXOpen.UF.UFSket.ReferenceStatus)0);
            native.Update(); native.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model); active = false;
            Require(session.UpdateManager.DoUpdate(mark) == 0, "Constrained/reference fixture update failed.");
            NXOpen.UF.UFSket.ReferenceStatus status; NXOpen.UF.UFSession.GetUFSession().Sket.AskReferenceStatus(native.Tag, reference.Tag, out status);
            Require((int)status == 0 && CountDrivingConstraints(NativeConstraintRecords(native)) == 1, "Reference/Driving fixture readback failed.");
            row["coincident_constraint_id"] = relation.JournalIdentifier; row["reference_curve_tag"] = reference.Tag.ToString(); row["reference_status"] = (int)status;
        }
        finally { if (active) native.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model); session.DeleteUndoMark(mark, "F08 constrained target fixture"); }
    }

    private static void RunInvalidRequests(M2OpenModelAdapter adapter, M2ModelBinding binding, Part part, Frame frame, Dictionary<string, object> row)
    {
        List<M3SketchGeometryRequest> sources = LSource(false); sources.Add(Line("disconnected", 30, 30, 40, 30));
        sources.Add(new M3SketchGeometryRequest { Id = "ellipse", Type = "ellipse", CenterMm = new double[] { 60, 60 }, MajorRadiusMm = 6, MinorRadiusMm = 3, RotationDeg = 0 });
        SketchFixture fixture = CreateSketch(adapter, binding, part, "invalid-request-target", frame, GeometryOnly(sources), row);
        List<M3SketchGeometryRequest> foreignGeometry = new List<M3SketchGeometryRequest>();
        for (int i = 0; i < 12; i++) foreignGeometry.Add(Line("foreign" + i, i * 20, 100, i * 20 + 10, 100));
        SketchFixture other = CreateSketch(adapter, binding, part, "foreign-id-owner", frame, GeometryOnly(foreignGeometry), row);
        RefreshFixtureGeometryIds(adapter, binding, part, fixture, row, "before_invalid_requests");
        string foreign = null; foreach (string id in other.GeometryIds) if (!fixture.GeometryIds.Contains(id)) { foreign = id; break; }
        Require(foreign != null, "Could not establish a cross-Sketch ID with no local alias collision.");
        List<object> requests = new List<object>(); row["rejection_requests"] = requests;
        string[] kinds = new string[] { "missing_curve", "cross_sketch_curve", "unsupported_ellipse", "zero_distance", "negative_distance", "nan_distance", "infinite_distance", "empty_chain", "null_chain", "invalid_direction", "disconnected_chain", "wrong_directed_connection", "duplicate_curve", "retain_false", "invalid_side", "invalid_join", "missing_sketch" };
        foreach (string kind in kinds)
        {
            Materialize(adapter, binding, part, row);
            M3SketchOffsetRequest request = OffsetRequest(binding, fixture, new string[] { "a", "b" }, new string[] { "forward", "forward" }, "left", "extension");
            string expectedCode = "INVALID_REQUEST";
            if (kind == "missing_curve") { request.CurveChain[0].CurveId = "F08-NONEXISTENT-CURVE"; expectedCode = "CURVE_NOT_FOUND"; }
            else if (kind == "cross_sketch_curve") { request.CurveChain[0].CurveId = foreign; expectedCode = "CURVE_NOT_FOUND"; }
            else if (kind == "unsupported_ellipse") { request.CurveChain = new List<M3SketchOffsetCurveRequest> { new M3SketchOffsetCurveRequest { CurveId = fixture.GeometryIdsByRequestId["ellipse"], Direction = "forward" } }; expectedCode = "UNSUPPORTED_CURVE_TYPE"; }
            else if (kind == "zero_distance") request.DistanceMm = 0;
            else if (kind == "negative_distance") request.DistanceMm = -2;
            else if (kind == "nan_distance") request.DistanceMm = Double.NaN;
            else if (kind == "infinite_distance") request.DistanceMm = Double.PositiveInfinity;
            else if (kind == "empty_chain") request.CurveChain.Clear();
            else if (kind == "null_chain") request.CurveChain = null;
            else if (kind == "invalid_direction") request.CurveChain[0].Direction = "backwards";
            else if (kind == "disconnected_chain") { request.CurveChain[1].CurveId = fixture.GeometryIdsByRequestId["disconnected"]; expectedCode = "CURVE_CHAIN_DISCONNECTED"; }
            else if (kind == "wrong_directed_connection") { request.CurveChain[1].Direction = "reverse"; expectedCode = "CURVE_CHAIN_DISCONNECTED"; }
            else if (kind == "duplicate_curve") request.CurveChain[1].CurveId = request.CurveChain[0].CurveId;
            else if (kind == "retain_false") { request.RetainOriginal = false; expectedCode = "UNSUPPORTED_REQUEST"; }
            else if (kind == "invalid_side") request.Side = "outside";
            else if (kind == "invalid_join") request.JoinMode = "fillet";
            else if (kind == "missing_sketch") { request.SketchId = "F08-NONEXISTENT-SKETCH"; expectedCode = "SKETCH_NOT_FOUND"; }
            Dictionary<string, object> record = new Dictionary<string, object> { { "name", kind }, { "request", OffsetRequestRecord(request) }, { "expected_error_code", expectedCode }, { "native_before", StrictSnapshot(part) }, { "revision_before", binding.RevisionId } };
            requests.Add(record); M3PlanSnapshot plan = null; Exception error = null;
            try { plan = adapter.PrepareSketchOffset(binding, request); } catch (Exception exception) { error = exception; }
            record["plan_returned"] = plan == null ? null : plan.PublicView; record["exception"] = error == null ? null : error.ToString();
            record["actual_error_code"] = error is M2ModelException ? ((M2ModelException)error).Code : null;
            record["native_after"] = StrictSnapshot(part); record["revision_after"] = binding.RevisionId;
            Require(plan == null && error is M2ModelException && ((M2ModelException)error).Code == expectedCode, "Invalid F08 request did not return its explicit expected rejection: " + kind);
            Require(SameValue(record["native_before"], record["native_after"]) && Object.Equals(record["revision_before"], record["revision_after"]), "Rejected F08 prepare mutated native state/revision: " + kind);
            record["passed"] = true;
        }
    }

    private static void RunCheckerCandidates(M2OpenModelAdapter adapter, M2ModelBinding binding, Session session, Part part, Frame frame, Dictionary<string, object> row)
    {
        row["test_layer"] = "Controlled native geometry candidates via test-only private-validator reflection; not evidence NX generated the bad curves.";
        SketchFixture l = CreateSketch(adapter, binding, part, "checker-L-source", frame, GeometryOnly(LSource(false)), row);
        SketchFixture arcSource = CreateSketch(adapter, binding, part, "checker-quarter-arc-source", frame,
            GeometryOnly(new List<M3SketchGeometryRequest> { Arc("a", 0, 0, 5, 0, 90) }), row);
        SketchFixture squareSource = CreateSketch(adapter, binding, part, "checker-collapse-square-source", frame, GeometryOnly(Square(0, 10)), row);
        SketchFixture nearReturnSource = CreateSketch(adapter, binding, part, "checker-near-180-source", frame,
            GeometryOnly(new List<M3SketchGeometryRequest> { Line("a", 0, 0, 10, 0), Line("b", 10, 0, 0, 0.00001) }), row);
        RefreshFixtureGeometryIds(adapter, binding, part, l, row, "checker_plans");
        RefreshFixtureGeometryIds(adapter, binding, part, arcSource, row, "checker_arc_plan");
        RefreshFixtureGeometryIds(adapter, binding, part, squareSource, row, "checker_square_plan");
        RefreshFixtureGeometryIds(adapter, binding, part, nearReturnSource, row, "checker_near_180_plan");
        Materialize(adapter, binding, part, row);
        M3PlanSnapshot outerPlan = adapter.PrepareSketchOffset(binding, OffsetRequest(binding, l, new string[] { "a", "b" }, new string[] { "forward", "forward" }, "right", "arc"));
        M3PlanSnapshot linePlan = adapter.PrepareSketchOffset(binding, OffsetRequest(binding, l, new string[] { "a", "b" }, new string[] { "forward", "forward" }, "left", "extension"));
        M3PlanSnapshot arcPlan = adapter.PrepareSketchOffset(binding, OffsetRequest(binding, arcSource, new string[] { "a" }, new string[] { "forward" }, "left", "extension"));
        M3SketchOffsetRequest squareRequest = OffsetRequest(binding, squareSource, new string[] { "a", "b", "c", "d" }, new string[] { "forward", "forward", "forward", "forward" }, "left", "extension");
        squareRequest.DistanceMm = 6;
        M3PlanSnapshot collapsePlan = adapter.PrepareSketchOffset(binding, squareRequest);
        M3SketchOffsetRequest nearReturnRequest = OffsetRequest(binding, nearReturnSource, new string[] { "a", "b" }, new string[] { "forward", "forward" }, "right", "arc");
        nearReturnRequest.DistanceMm = 0.01;
        M3PlanSnapshot nearReturnPlan = adapter.PrepareSketchOffset(binding, nearReturnRequest);
        row["outer_plan"] = outerPlan.PublicView; row["line_plan"] = linePlan.PublicView; row["arc_plan"] = arcPlan.PublicView;
        row["collapse_plan"] = collapsePlan.PublicView;
        row["near_180_plan"] = nearReturnPlan.PublicView;
        Dictionary<string, object> before = StrictSnapshot(part); row["native_before_candidates"] = before;
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F08 temporary checker candidates");
        try
        {
            Vector3d x = new Vector3d(frame.XAxis[0], frame.XAxis[1], frame.XAxis[2]), y = new Vector3d(frame.YAxis[0], frame.YAxis[1], frame.YAxis[2]);
            Curve a = part.Curves.CreateLine(WorldPoint(frame, 0, -2), WorldPoint(frame, 10, -2));
            Curve b = part.Curves.CreateLine(WorldPoint(frame, 12, 0), WorldPoint(frame, 12, 10));
            Curve correctJoin = part.Curves.CreateArc(WorldPoint(frame, 10, 0), x, y, 2, 3 * Pi / 2, 2 * Pi);
            // Wrong circle has identical endpoint set and radius; it is a clockwise quarter at the wrong corner.
            Curve badJoin = part.Curves.CreateArc(WorldPoint(frame, 12, -2), x, y, 2, Pi / 2, Pi);
            ValidateCandidate(adapter, outerPlan, FindSketch(part, l.SketchId), new Curve[] { a, correctJoin, b }, true, "correct_outer_join", row);
            ValidateCandidate(adapter, outerPlan, FindSketch(part, l.SketchId), new Curve[] { a, badJoin, b }, false, "wrong_center_same_radius_join", row);
            Curve extensionA = part.Curves.CreateLine(WorldPoint(frame, 0, -2), WorldPoint(frame, 12, -2));
            Curve extensionB = part.Curves.CreateLine(WorldPoint(frame, 12, -2), WorldPoint(frame, 12, 10));
            ValidateCandidate(adapter, outerPlan, FindSketch(part, l.SketchId), new Curve[] { extensionB, extensionA }, false, "arc_join_missing_connector_extension_substitution", row);
            Curve correctArc = part.Curves.CreateArc(WorldPoint(frame, 0, 0), x, y, 3, 0, Pi / 2);
            Curve complement = part.Curves.CreateArc(WorldPoint(frame, 0, 0), x, y, 3, Pi / 2, 2 * Pi);
            ValidateCandidate(adapter, arcPlan, FindSketch(part, arcSource.SketchId), new Curve[] { correctArc }, true, "correct_partial_arc", row);
            ValidateCandidate(adapter, arcPlan, FindSketch(part, arcSource.SketchId), new Curve[] { complement }, false, "complementary_270_degree_arc", row);
            Curve line1 = part.Curves.CreateLine(WorldPoint(frame, 0, 2), WorldPoint(frame, 8, 2));
            Curve line2 = part.Curves.CreateLine(WorldPoint(frame, 8, 10), WorldPoint(frame, 8, 2));
            Curve drift1 = part.Curves.CreateLine(WorldPoint(frame, 0, 2), WorldPoint(frame, 8, 2.0008));
            Curve drift2 = part.Curves.CreateLine(WorldPoint(frame, 8, 10), WorldPoint(frame, 8, 2.0008));
            ValidateCandidate(adapter, linePlan, FindSketch(part, l.SketchId), new Curve[] { line1, line2 }, true, "correct_line_supports", row);
            ValidateCandidate(adapter, linePlan, FindSketch(part, l.SketchId), new Curve[] { drift1, drift2 }, false, "line_support_drift_0p0008mm", row);
            Curve collapseA = part.Curves.CreateLine(WorldPoint(frame, 6, 6), WorldPoint(frame, 4, 6));
            Curve collapseB = part.Curves.CreateLine(WorldPoint(frame, 4, 6), WorldPoint(frame, 4, 4));
            Curve collapseC = part.Curves.CreateLine(WorldPoint(frame, 4, 4), WorldPoint(frame, 6, 4));
            Curve collapseD = part.Curves.CreateLine(WorldPoint(frame, 6, 4), WorldPoint(frame, 6, 6));
            ValidateCandidate(adapter, collapsePlan, FindSketch(part, squareSource.SketchId), new Curve[] { collapseC, collapseA, collapseD, collapseB }, false, "closed_square_inner_offset_collapsed_traversal", row);
            double slope = 0.000001, norm = Math.Sqrt(1 + slope * slope), smallDistance = 0.01;
            double shiftedEndX = smallDistance * slope / norm, shiftedEndY = 0.00001 + smallDistance / norm;
            double farJoinX = 10 + smallDistance * (1 + norm) / slope;
            Curve nearExtensionA = part.Curves.CreateLine(WorldPoint(frame, 0, -smallDistance), WorldPoint(frame, farJoinX, -smallDistance));
            Curve nearExtensionB = part.Curves.CreateLine(WorldPoint(frame, farJoinX, -smallDistance), WorldPoint(frame, shiftedEndX, shiftedEndY));
            ValidateCandidate(adapter, nearReturnPlan, FindSketch(part, nearReturnSource.SketchId), new Curve[] { nearExtensionB, nearExtensionA }, false, "near_180_arc_join_missing_connector_small_distance", row);
        }
        finally
        {
            session.UndoToMark(mark, null); row["candidate_undo_to_mark_succeeded"] = true; session.DeleteUndoMark(mark, "F08 temporary checker candidates");
            row["native_after_candidate_undo"] = StrictSnapshot(part);
            Require(SameValue(before, row["native_after_candidate_undo"]), "Temporary checker candidate Undo did not restore native baseline.");
        }
    }

    private static void ValidateCandidate(M2OpenModelAdapter adapter, M3PlanSnapshot plan, Sketch sketch, Curve[] outputs,
        bool expected, string name, Dictionary<string, object> row)
    {
        MethodInfo validator = null;
        foreach (MethodInfo method in typeof(M2OpenModelAdapter).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
            if (method.Name == "ValidateNativeOffsetOutput" && method.GetParameters().Length == 4) { validator = method; break; }
        Require(validator != null, "Production direct geometry validator signature not found.");
        FieldInfo inputField = plan.AdapterState.GetType().GetField("InputCurves", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Require(inputField != null, "Prepared plan does not expose its test-reflection input curves.");
        Curve[] sources = (Curve[])inputField.GetValue(plan.AdapterState);
        object[] args = new object[] { plan.AdapterState, sources, outputs, null };
        bool actual = (bool)validator.Invoke(adapter, args);
        List<object> curves = new List<object>();
        foreach (Curve curve in outputs)
        {
            Line line = curve as Line; Arc arc = curve as Arc;
            Dictionary<string, object> record = new Dictionary<string, object> { { "type", curve.GetType().Name }, { "tag", curve.Tag.ToString() }, { "length_mm", curve.GetLength() } };
            if (line != null) { record["start_mm"] = Point(line.StartPoint); record["end_mm"] = Point(line.EndPoint); }
            if (arc != null) { record["center_mm"] = Point(arc.CenterPoint); record["radius_mm"] = arc.Radius; record["start_mm"] = Point(ArcEndpoint(arc, true)); record["end_mm"] = Point(ArcEndpoint(arc, false)); record["sweep_rad"] = arc.EndAngle - arc.StartAngle; }
            curves.Add(record);
        }
        GetOrCreateEvidenceList(row, "controlled_candidates").Add(new Dictionary<string, object> {
            { "name", name }, { "expected_valid", expected }, { "actual_valid", actual }, { "native_candidate_geometry", curves }, { "production_geometry_evidence", args[3] }, { "passed", actual == expected }
        });
        Require(actual == expected, "Production direct geometry validator candidate mismatch: " + name);
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
            "Preparing the F08 seed sketch changed native model state: " + M2Json.CanonicalText(seedEvidence["prepare_snapshot_diff"]));
        Require(prepareRevisionSame,
            "Preparing the F08 seed sketch changed the binding revision: " + M2Json.CanonicalText(new Dictionary<string, object> {
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
            "F08 seed sketch ExecutePlan threw: " + M2Json.CanonicalText(executionException == null ? null : executionException.ToString()));
        Require(execution != null && String.Equals(execution.State, "APPLIED", StringComparison.Ordinal),
            "F08 seed sketch execution did not return APPLIED: " + M2Json.CanonicalText(ExecutionRecord(execution)));

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
            "F08 seed sketch VerifyPlan threw: " + M2Json.CanonicalText(verificationException == null ? null : verificationException.ToString()));
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "F08 seed sketch did not pass production verification: " + M2Json.CanonicalText(VerificationRecord(verification)));
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
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F08 native Driving fixture");
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
            finally { session.DeleteUndoMark(mark, "F08 native Driving fixture"); }
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
            "F08 materialization read could not capture its before/after snapshots: " + M2Json.CanonicalText(warmup));
        Require(allowed,
            "F08 materialization read produced a change outside the declared InspectPart dirty-read effect: " +
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
        Require(evidence != null, "F08 case evidence dictionary is missing.");
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
            else if (item is Ellipse)
            {
                Ellipse ellipse = (Ellipse)item;
                double major, minor, start, end, rotation; Point3d center;
                ellipse.GetParameters(out major, out minor, out start, out end, out rotation, out center);
                Point3d frameCenter; Vector3d x, y; ellipse.GetOrientation(out frameCenter, out x, out y);
                record["center_mm"] = Point(center); record["major_radius_mm"] = major; record["minor_radius_mm"] = minor;
                record["native_polar_start_angle_rad"] = start; record["native_polar_end_angle_rad"] = end; record["rotation_rad"] = rotation;
                record["plane_center_mm"] = Point(frameCenter);
                record["plane_x_axis"] = new double[] { x.X, x.Y, x.Z }; record["plane_y_axis"] = new double[] { y.X, y.Y, y.Z };
                record["native_length_mm"] = ellipse.GetLength();
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
        if (requested.Type == "ellipse")
        {
            if (!String.Equals(actualKind, "ELLIPSE", StringComparison.Ordinal)) return false;
            Point3d expectedCenter = WorldPoint(frame, requested.CenterMm[0], requested.CenterMm[1]);
            double rotation = requested.RotationDeg * Pi / 180;
            double[] expectedMajorAxis = new double[] {
                Math.Cos(rotation) * frame.XAxis[0] + Math.Sin(rotation) * frame.YAxis[0],
                Math.Cos(rotation) * frame.XAxis[1] + Math.Sin(rotation) * frame.YAxis[1],
                Math.Cos(rotation) * frame.XAxis[2] + Math.Sin(rotation) * frame.YAxis[2]
            };
            Point3d expectedEndpoint = WorldPoint(frame, requested.CenterMm[0] + requested.MajorRadiusMm * Math.Cos(rotation),
                requested.CenterMm[1] + requested.MajorRadiusMm * Math.Sin(rotation));
            return PointDistance(Point3dFromArray(DoubleArray(record["center"])), expectedCenter) <= PositionToleranceMm &&
                Math.Abs(Convert.ToDouble(record["major_radius_mm"], CultureInfo.InvariantCulture) - requested.MajorRadiusMm) <= LengthToleranceMm &&
                Math.Abs(Convert.ToDouble(record["minor_radius_mm"], CultureInfo.InvariantCulture) - requested.MinorRadiusMm) <= LengthToleranceMm &&
                VectorDistance(DoubleArray(record["major_axis_direction"]), expectedMajorAxis) <= FrameTolerance &&
                VectorDistance(DoubleArray(record["normal"]), frame.Normal) <= FrameTolerance &&
                Math.Abs(Convert.ToDouble(record["start_angle_rad"], CultureInfo.InvariantCulture)) <= AngleToleranceRad &&
                Math.Abs(Convert.ToDouble(record["end_angle_rad"], CultureInfo.InvariantCulture) - 2 * Pi) <= AngleToleranceRad &&
                PointDistance(Point3dFromArray(DoubleArray(record["start"])), expectedEndpoint) <= PositionToleranceMm &&
                PointDistance(Point3dFromArray(DoubleArray(record["end"])), expectedEndpoint) <= PositionToleranceMm;
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
            row["retest_criterion"] = "After fixing the recorded defect, rebuild and rerun all F08 cases on a new unique model path; retain this JSON and model.";
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
