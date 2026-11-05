// Real-NX acceptance for F06 sketch curves and profile classification.
// Build offline; run only in the designated idle NX session with RunF06Acceptance.py.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

public static class F06Acceptance
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    private const double PositionToleranceMm = 1e-6;
    private const double RadiusToleranceMm = 1e-6;
    private const double FrameComponentTolerance = 1e-7;
    private const double AngleToleranceRad = 1e-7;
    private const double Pi = Math.PI;
    private const int UfCurveSlopeNone = -1;
    private const int UfCurveSlopeAuto = 0;
    private const int UfCurveCrvatrNone = -1;

    private sealed class Frame
    {
        internal double[] Origin;
        internal double[] Normal;
        internal double[] XAxis;
        internal double[] YAxis;
    }

    private sealed class SketchSpec
    {
        internal string Name;
        internal string SketchId;
        internal M3SketchRequest Request;
        internal Frame Frame;
        internal M3PlanSnapshot Plan;
    }

    private delegate void CaseBody(Dictionary<string, object> record);

    public static void Main(string[] args)
    {
        string runId = Guid.NewGuid().ToString("N");
        string modelDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "models");
        string evidenceDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "evidence");
        string sourcePath = Path.Combine(modelDirectory, "F06-acceptance-" + runId + ".prt");
        string savedPath = Path.Combine(modelDirectory, "F06-acceptance-" + runId + "-roundtrip.prt");
        string reportPath = Path.Combine(evidenceDirectory, "F06-acceptance-" + runId + ".json");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        RequireNewPath(sourcePath, "source model");
        RequireNewPath(savedPath, "roundtrip model");
        RequireNewPath(reportPath, "evidence report");

        List<object> cases = new List<object>();
        List<object> defects = new List<object>();
        List<SketchSpec> appliedSketches = new List<SketchSpec>();
        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test", "F06 native sketch geometry and profile-analysis NX acceptance" },
            { "test_version", "0.1.0" }, { "run_id", runId },
            { "started_utc", UtcNow() }, { "passed", false },
            { "source_model_path", sourcePath }, { "roundtrip_model_path", savedPath },
            { "report_path", reportPath }, { "cases", cases }, { "defects", defects },
            { "position_tolerance_mm", PositionToleranceMm },
            { "radius_tolerance_mm", RadiusToleranceMm },
            { "frame_component_tolerance", FrameComponentTolerance },
            { "angle_tolerance_rad", AngleToleranceRad },
            { "scope_limitations", new List<object> {
                "This is one deterministic NX2506 subsystem-validation run; it does not establish reliability, manufacturing accuracy, or behavior across other NX builds.",
                "No external physical metrology is used. NXOpen and UF model readbacks are numerical CAD evidence.",
                "F12 extrusion behavior has separate acceptance coverage in docs/F12.md; this F06 run does not claim F12 profile acceptance."
            } },
            { "configuration", new Dictionary<string, object>(StringComparer.Ordinal) {
                { "test_layer", "real NXOpen/UF subsystem validation through production M2OpenModelAdapter and M3 sketch factory" },
                { "traceability", "docs/F06.md; docs/V2.md" },
                { "production_bridge_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Bridge.dll") },
                { "production_adapter_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Entry.dll") },
                { "acceptance_assembly", Path.Combine(evidenceDirectory, "F06Acceptance.dll") },
                { "units", "mm; radians only for native curve parameter readback" },
                { "nx_started_by_harness", false }, { "physical_metrology", false },
                { "instrument_calibration", "Not applicable: no external instrument used; limits compare NX model values only." }
            } }
        };
        Persist(reportPath, evidence);

        Session session = null;
        Part originalPart = null;
        Part testPart = null;
        M2OpenModelAdapter adapter = null;
        M2ModelBinding binding = null;
        string staleAnchorSketchId = null;
        Frame testFrame = BuildFrame(new double[] { 10.0, 20.0, 30.0 },
            new double[] { 1.0, 2.0, 3.0 }, new double[] { 1.0, -1.0, 0.5 });

        try
        {
            session = Session.GetSession();
            originalPart = session.Parts.Work;
            evidence["original_work_part_path"] = originalPart == null ? null : originalPart.FullPath;
            evidence["original_work_part_was_modified"] = originalPart != null && originalPart.IsModified;
            evidence["session_was_in_task_environment"] = session.IsInTaskEnvironment;
            Require(!session.IsInTaskEnvironment, "Acceptance requires an idle NX session outside a task environment.");
            Require(originalPart == null || !originalPart.IsModified,
                "Acceptance requires the original Work Part to be saved and unmodified.");

            testPart = session.Parts.NewDisplay(sourcePath, Part.Units.Millimeters);
            SavePart(testPart, "isolated empty F06 fixture");
            adapter = new M2OpenModelAdapter();
            binding = adapter.Takeover(sourcePath, "saved_independent_copy", "f06-acceptance-session",
                "f06-acceptance-" + runId, 1);
            evidence["session"] = adapter.GetSession(binding);
            evidence["loaded_assemblies"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "bridge", AssemblyRecord(typeof(M2Json).Assembly) },
                { "production_adapter", AssemblyRecord(typeof(M2OpenModelAdapter).Assembly) },
                { "nxopen", AssemblyRecord(typeof(Session).Assembly) },
                { "nxopen_uf", AssemblyRecord(typeof(UFSession).Assembly) },
                { "acceptance", AssemblyRecord(Assembly.GetExecutingAssembly()) }
            };
            evidence["binding_revision_before_tests"] = binding.RevisionId;
            Persist(reportPath, evidence);

            RunCase(reportPath, evidence, cases, defects, "F06_empty_sketch_is_not_a_valid_area",
                "Keep the legacy empty sketch successful while reporting no curve or valid closed region.",
                new string[] { "Prepare an arbitrary-frame sketch with geometries=[].", "Execute and verify through the production adapter.", "Read Sketch.GetAllGeometry and the execution/verification profile_analysis records." },
                "Sketch is APPLIED and verified; native curve count and profile curve/loop counts are zero; profile status is EMPTY. No solid-area validity is inferred.",
                delegate(Dictionary<string, object> record)
                {
                    SketchSpec spec = ApplySketch(adapter, binding, testPart, "empty", Request(testFrame, new List<M3SketchGeometryRequest>()),
                        testFrame, record, "EMPTY");
                    appliedSketches.Add(spec);
                    RequireCurveCount(FindSketch(testPart, spec.SketchId), 0, record);
                    RequireProfileMetrics(record, 0, 0, 0, 0, 0);
                });

            RunCase(reportPath, evidence, cases, defects, "F06_legacy_open_line_remains_successful",
                "Preserve valid geometry creation for an open line without claiming a closed profile or usable solid region.",
                new string[] { "Prepare a free-frame sketch containing one finite line.", "Execute, verify, inspect, and read the native line endpoints directly." },
                "Line is APPLIED and verified with exact world endpoints; analysis status is OPEN_GEOMETRY, open_chain_count=1, and closed_loop_count=0.",
                delegate(Dictionary<string, object> record)
                {
                    List<M3SketchGeometryRequest> geometry = new List<M3SketchGeometryRequest> { Line("open-line", 1, 2, 8, 2) };
                    SketchSpec spec = ApplySketch(adapter, binding, testPart, "open-line", Request(testFrame, geometry),
                        testFrame, record, "OPEN_GEOMETRY");
                    appliedSketches.Add(spec);
                    RequireProfileMetrics(record, 1, 1, 0, 0, 0);
                });

            List<M3SketchGeometryRequest> mixedCurves = new List<M3SketchGeometryRequest>
            {
                Circle("legacy-circle", -12, 7, 8),
                Arc("arc-positive-crosses-zero", 0, 0, 6, 350, 45),
                Arc("arc-negative-long", 18, 0, 5, 35, -225),
                Ellipse("ellipse-rotated", 36, 4, 9, 3, 31),
                EllipticalArc("ellipse-arc-positive", 55, 3, 8, 2.5, 24, 325, 140),
                EllipticalArc("ellipse-arc-negative", 77, 3, 7, 2, 342, 210, -195),
                Spline("spline-five-points", new double[][] {
                    new double[] { 0, 25 }, new double[] { 3, 31 }, new double[] { 9, 27 },
                    new double[] { 13, 35 }, new double[] { 19, 30 }
                })
            };
            SketchSpec mixedSpec = null;
            RunCase(reportPath, evidence, cases, defects, "F06_native_curves_on_tilted_frame",
                "Validate circle, positive and negative circular arcs, rotated full ellipse, signed elliptical arcs, and a cubic interpolating spline on one tilted world frame.",
                new string[] { "Use the test frame origin [10,20,30] mm, normal [1,2,3], and non-orthogonal x-axis [1,-1,0.5].", "Prepare and execute the seven-curve sketch through the production adapter.", "Compare direct NXOpen/UF geometry to independently transformed requested centers, radii, axes, endpoints, signed sweeps, spline points, knots, and degree." },
                "Exactly seven native curves belong to the sketch: one circular Arc for the full circle, two circular arcs, one native ellipse, two native elliptical arcs, and one native cubic non-periodic spline. Centers/radii/axes/endpoints are within stated CAD tolerances; arc senses and sweep magnitudes preserve signed requests; all five spline interpolation points and UF definition readbacks match. Spline interpolation points have no user-defined slope or curvature vectors; NX may read back AUTO endpoint slopes after sketch update, NONE remains required for interior slopes, and curvature remains NONE at every point.",
                delegate(Dictionary<string, object> record)
                {
                    mixedSpec = ApplySketch(adapter, binding, testPart, "tilted-mixed-curves", GeometryOnlyRequest(testFrame, mixedCurves),
                        testFrame, record, "OPEN_GEOMETRY");
                    appliedSketches.Add(mixedSpec);
                    RequireDirectGeometryCount(record, 7);
                    RequireProfileAnalysisAvailableAndSafe(record, true);
                });

            SketchSpec mixedLoopSpec = null;
            List<M3SketchGeometryRequest> mixedLoop = new List<M3SketchGeometryRequest>
            {
                Arc("d-profile-arc", 40, 0, 10, 180, 180),
                Line("d-profile-chord", 50, 0, 30, 0)
            };
            RunCase(reportPath, evidence, cases, defects, "F06_mixed_nonrectangular_closed_loop",
                "Verify a native circular-arc-plus-line closed D profile, including endpoint topology and closed-region classification.",
                new string[] { "Create a 180 degree circular arc centered at local [40,0] mm from [30,0] to [50,0].", "Add the closing chord from [50,0] to [30,0].", "Execute and verify; inspect actual native endpoints and profile_analysis." },
                "Two native curves form one closed non-rectangular loop; analysis is VALID_CLOSED_PROFILES with one closed loop, one outer loop, no inner loop, no open chain, and native intersection/section checks verified.",
                delegate(Dictionary<string, object> record)
                {
                    mixedLoopSpec = ApplySketch(adapter, binding, testPart, "mixed-d-profile", GeometryOnlyRequest(testFrame, mixedLoop),
                        testFrame, record, "VALID_CLOSED_PROFILES");
                    appliedSketches.Add(mixedLoopSpec);
                    RequireProfileMetrics(record, 2, 0, 1, 1, 0);
                    RequireProfileAnalysisAvailableAndSafe(record, false);
                });

            RunCase(reportPath, evidence, cases, defects, "F06_complementary_arcs_with_tangent_shared_endpoints",
                "Accept complementary half-arcs that meet at endpoints with parallel tangents while their interiors remain disjoint.",
                new string[] { "Create two native circular arcs with the same center/radius: start 0 sweep +180 degrees and start 180 sweep +180 degrees.", "Create a second profile from two complementary elliptical arcs with nonzero center and 31 degree rotation.", "Execute and live-verify both profiles; directly check curve counts, arc endpoints/interior samples, and profile-analysis loop classification." },
                "Each profile is APPLIED and verified as VALID_CLOSED_PROFILES with two native curves, one closed outer loop, no open chains or holes, and verified native intersection/section checks; shared tangent endpoints are not treated as overlapping interiors.",
                delegate(Dictionary<string, object> record)
                {
                    Dictionary<string, object> circular = new Dictionary<string, object>(StringComparer.Ordinal);
                    record["circular_half_arc_profile"] = circular;
                    List<M3SketchGeometryRequest> circleHalves = new List<M3SketchGeometryRequest> {
                        Arc("complementary-circle-first-half", 0, 0, 5, 0, 180),
                        Arc("complementary-circle-second-half", 0, 0, 5, 180, 180)
                    };
                    SketchSpec circleSpec = ApplySketch(adapter, binding, testPart, "complementary-circle-halves",
                        GeometryOnlyRequest(testFrame, circleHalves), testFrame, circular, "VALID_CLOSED_PROFILES");
                    appliedSketches.Add(circleSpec);
                    RequireDirectGeometryCount(circular, 2);
                    RequireProfileMetrics(circular, 2, 0, 1, 1, 0);
                    RequireProfileAnalysisAvailableAndSafe(circular, false);

                    Dictionary<string, object> elliptical = new Dictionary<string, object>(StringComparer.Ordinal);
                    record["elliptical_half_arc_profile"] = elliptical;
                    List<M3SketchGeometryRequest> ellipseHalves = new List<M3SketchGeometryRequest> {
                        EllipticalArc("complementary-ellipse-first-half", 24, 13, 9, 3, 31, 0, 180),
                        EllipticalArc("complementary-ellipse-second-half", 24, 13, 9, 3, 31, 180, 180)
                    };
                    SketchSpec ellipseSpec = ApplySketch(adapter, binding, testPart, "complementary-ellipse-halves",
                        GeometryOnlyRequest(testFrame, ellipseHalves), testFrame, elliptical, "VALID_CLOSED_PROFILES");
                    appliedSketches.Add(ellipseSpec);
                    RequireDirectGeometryCount(elliptical, 2);
                    RequireProfileMetrics(elliptical, 2, 0, 1, 1, 0);
                    RequireProfileAnalysisAvailableAndSafe(elliptical, false);
                });

            RunCase(reportPath, evidence, cases, defects, "F06_duplicate_and_partial_arc_overlap_roll_back",
                "Reject coincident and partially overlapping circular arcs with a real production rollback even when native pair intersection returns no discrete point.",
                new string[] { "Prepare two separately identified, geometrically identical 180 degree arcs and execute the native sketch plan.", "On a fresh plan, prepare arcs on one circle covering [0,180] and [90,270] degrees, then execute.", "For each execution, capture production rollback evidence and compare native feature/sketch/body IDs, curve IDs, modified state, and active sketch before any test cleanup Undo." },
                "Both duplicate and partial-overlap arc profiles return ROLLED_BACK with verified production undo/readback flags and a real error code; native feature/sketch/body/curve inventories match their pre-execute baselines before any harness undo. Zero reported UF intersection points do not turn either profile into VALID_CLOSED_PROFILES.",
                delegate(Dictionary<string, object> record)
                {
                    Dictionary<string, object> duplicate = new Dictionary<string, object>(StringComparer.Ordinal);
                    record["duplicate_arc_result"] = duplicate;
                    RunArcOverlapRollback(adapter, binding, session, testPart, testFrame, "duplicate-arcs",
                        new List<M3SketchGeometryRequest> {
                            Arc("duplicate-half-a", 0, 0, 5, 0, 180), Arc("duplicate-half-b", 0, 0, 5, 0, 180)
                        }, duplicate);

                    Dictionary<string, object> overlap = new Dictionary<string, object>(StringComparer.Ordinal);
                    record["partial_overlap_arc_result"] = overlap;
                    RunArcOverlapRollback(adapter, binding, session, testPart, testFrame, "partial-overlap-arcs",
                        new List<M3SketchGeometryRequest> {
                            Arc("overlap-first", 20, 0, 5, 0, 180), Arc("overlap-second", 20, 0, 5, 90, 180)
                        }, overlap);
                });

            SketchSpec concaveSpec = null;
            List<M3SketchGeometryRequest> concave = Polygon("concave-l", new double[][] {
                new double[] { 0, 0 }, new double[] { 20, 0 }, new double[] { 20, 5 },
                new double[] { 8, 5 }, new double[] { 8, 20 }, new double[] { 0, 20 }
            });
            RunCase(reportPath, evidence, cases, defects, "F06_concave_closed_profile",
                "Prove a simple concave line contour is detected as one native closed outer profile.",
                new string[] { "Create the six-edge concave L contour from the recorded local points.", "Run the production prepare/execute/verify path.", "Check native line endpoints, loop counts, and NX profile analysis." },
                "Six lines close into one concave outer loop with no open chains or inner loops; native intersection check and section result are verified; status is VALID_CLOSED_PROFILES.",
                delegate(Dictionary<string, object> record)
                {
                    concaveSpec = ApplySketch(adapter, binding, testPart, "concave-l", GeometryOnlyRequest(testFrame, concave),
                        testFrame, record, "VALID_CLOSED_PROFILES");
                    appliedSketches.Add(concaveSpec);
                    RequireProfileMetrics(record, 6, 0, 1, 1, 0);
                    RequireProfileAnalysisAvailableAndSafe(record, false);
                });

            SketchSpec regionsSpec = null;
            List<M3SketchGeometryRequest> regions = new List<M3SketchGeometryRequest>();
            regions.AddRange(Polygon("region-l-outer", new double[][] {
                new double[] { 0, 0 }, new double[] { 20, 0 }, new double[] { 20, 5 },
                new double[] { 8, 5 }, new double[] { 8, 20 }, new double[] { 0, 20 }
            }));
            regions.AddRange(Polygon("region-inner-hole", new double[][] {
                new double[] { 2, 1 }, new double[] { 5, 1 }, new double[] { 5, 4 }, new double[] { 2, 4 }
            }));
            regions.AddRange(Polygon("region-disjoint-triangle", new double[][] {
                new double[] { 30, 0 }, new double[] { 36, 0 }, new double[] { 33, 6 }
            }));
            RunCase(reportPath, evidence, cases, defects, "F06_inner_loop_and_multiple_disjoint_regions",
                "Verify containment and disjoint-region classification for an inner hole and two separate outer contours.",
                new string[] { "Create the concave L outer loop, a contained four-edge hole loop, and a spatially separate triangular loop in one sketch.", "Execute and verify through the production adapter.", "Read every native line from Sketch.GetAllGeometry and compare profile region counts." },
                "Thirteen native lines form three closed loops, two outer regions, and one inner loop; no open chains; native checks and section loop count agree; analysis is VALID_CLOSED_PROFILES.",
                delegate(Dictionary<string, object> record)
                {
                    regionsSpec = ApplySketch(adapter, binding, testPart, "hole-and-disjoint-regions", GeometryOnlyRequest(testFrame, regions),
                        testFrame, record, "VALID_CLOSED_PROFILES");
                    appliedSketches.Add(regionsSpec);
                    RequireProfileMetrics(record, 13, 0, 3, 2, 1);
                    RequireProfileAnalysisAvailableAndSafe(record, false);
                });

            RunCase(reportPath, evidence, cases, defects, "F06_invalid_geometry_requests_are_read_only",
                "Reject duplicate IDs and degenerate points, radii, angle ranges, and spline requests during prepare without changing the native model.",
                new string[] { "Record the direct feature/sketch/body IDs, modified state, and binding revision.", "Prepare each malformed request separately: duplicate geometry ID; zero-length line; zero-diameter circle; zero-radius arc; zero or full-turn partial sweep; out-of-range start/rotation angles; zero minor radius or major radius smaller than minor; repeated spline interpolation point; fewer than four spline points.", "After each rejection, compare all recorded native IDs and revision." },
                "Every invalid request fails with M2ModelException(INVALID_REQUEST); prepare creates no native geometry/features/sketches and leaves the binding revision and modified state unchanged.",
                delegate(Dictionary<string, object> record)
                {
                    List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
                    List<M3SketchRequest> invalid = new List<M3SketchRequest>
                    {
                        Request(testFrame, new List<M3SketchGeometryRequest> { Line("duplicate", 0, 0, 2, 0), Line("duplicate", 3, 0, 5, 0) }),
                        Request(testFrame, new List<M3SketchGeometryRequest> { Line("zero-length-line", 3, 3, 3, 3) }),
                        Request(testFrame, new List<M3SketchGeometryRequest> { Circle("zero-diameter-circle", 0, 0, 0) }),
                        Request(testFrame, new List<M3SketchGeometryRequest> { Arc("zero-radius", 0, 0, 0, 0, 90) }),
                        Request(testFrame, new List<M3SketchGeometryRequest> { Arc("zero-sweep", 0, 0, 3, 0, 0) }),
                        Request(testFrame, new List<M3SketchGeometryRequest> { Arc("full-turn-arc", 0, 0, 3, 0, 360) }),
                        Request(testFrame, new List<M3SketchGeometryRequest> { Arc("negative-start-angle", 0, 0, 3, -1, 45) }),
                        Request(testFrame, new List<M3SketchGeometryRequest> { Ellipse("bad-ellipse-radii", 0, 0, 2, 3, 0) }),
                        Request(testFrame, new List<M3SketchGeometryRequest> { Ellipse("zero-minor-radius", 0, 0, 4, 0, 0) }),
                        Request(testFrame, new List<M3SketchGeometryRequest> { Ellipse("out-of-range-rotation", 0, 0, 3, 1, 360) }),
                        Request(testFrame, new List<M3SketchGeometryRequest> { Spline("repeated-spline-point", new double[][] {
                            new double[] { 0, 0 }, new double[] { 2, 1 }, new double[] { 2, 1 }, new double[] { 5, 0 }
                        }) }),
                        Request(testFrame, new List<M3SketchGeometryRequest> { Spline("short-spline", new double[][] {
                            new double[] { 0, 0 }, new double[] { 2, 1 }, new double[] { 5, 0 }
                        }) })
                    };
                    string[] labels = new string[] { "duplicate_id", "zero_length_line", "zero_diameter_circle", "zero_arc_radius", "zero_arc_sweep", "full_turn_arc_sweep", "start_angle_out_of_range", "major_less_than_minor", "zero_minor_radius", "rotation_out_of_range", "repeated_spline_point", "too_few_spline_points" };
                    foreach (M3SketchRequest request in invalid)
                    {
                        request.RevisionId = binding.RevisionId;
                        string before = ModelFingerprint(testPart);
                        string revisionBefore = binding.RevisionId;
                        Exception observed = null;
                        try { adapter.PrepareSketch(binding, request); }
                        catch (Exception ex) { observed = ex; }
                        string after = ModelFingerprint(testPart);
                        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "observed_exception", observed == null ? null : observed.ToString() },
                            { "error_code", observed is M2ModelException ? ((M2ModelException)observed).Code : null },
                            { "model_fingerprint_before", before }, { "model_fingerprint_after", after },
                            { "revision_before", revisionBefore }, { "revision_after", binding.RevisionId }
                        };
                        results.Add(result);
                        Require(observed is M2ModelException && ((M2ModelException)observed).Code == "INVALID_REQUEST",
                            "Malformed sketch request was not rejected with INVALID_REQUEST: " + (observed == null ? "no exception" : observed.ToString()));
                        Require(String.Equals(before, after, StringComparison.Ordinal), "Malformed prepare changed the NX model fingerprint.");
                        Require(String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal), "Malformed prepare changed the binding revision.");
                    }
                    for (int i = 0; i < results.Count; i++) results[i]["case"] = labels[i];
                    record["invalid_request_results"] = results;
                });

            RunCase(reportPath, evidence, cases, defects, "F06_self_intersection_is_rejected_and_rolled_back",
                "Prove an endpoint-closed bow-tie line contour cannot be reported as a valid profile and has no residual native feature after rejection.",
                new string[] { "Prepare four line segments forming a bow-tie contour with a proper interior crossing.", "Record model feature/sketch/body IDs and binding revision.", "Execute the prepared plan through the production adapter, capture the actual result/error and native profile evidence, then confirm rollback." },
                "Prepare or execute rejects the self-intersecting contour; any returned analysis is INVALID_SELF_INTERSECTION and never VALID_CLOSED_PROFILES. Failed execution is unchanged/rolled back: feature/sketch/body IDs and geometry inventories return to baseline.",
                delegate(Dictionary<string, object> record)
                {
                    List<M3SketchGeometryRequest> bowtie = Polygon("bowtie", new double[][] {
                        new double[] { 0, 0 }, new double[] { 10, 10 }, new double[] { 0, 10 }, new double[] { 10, 0 }
                    });
                    M3SketchRequest bowtieRequest = GeometryOnlyRequest(testFrame, bowtie);
                    bowtieRequest.RevisionId = binding.RevisionId;
                    List<string> geometryIdsBefore = GeometryIds(testPart);
                    Dictionary<string, object> modelStateBefore = ModelIdRecord(testPart);
                    string activeSketchBefore = ActiveSketchId(session);
                    string before = ModelFingerprint(testPart);
                    string revisionBefore = binding.RevisionId;
                    M3PlanSnapshot plan = null;
                    Exception prepareError = null;
                    try { plan = adapter.PrepareSketch(binding, bowtieRequest); }
                    catch (Exception ex) { prepareError = ex; }
                    record["prepare_exception"] = prepareError == null ? null : prepareError.ToString();
                    record["prepare_error_code"] = prepareError is M2ModelException ? ((M2ModelException)prepareError).Code : null;
                    if (prepareError == null)
                    {
                        record["prepared_plan"] = plan == null ? null : plan.PublicView;
                        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F06 bow-tie rollback acceptance");
                        bool markActive = true;
                        bool productionRollbackProven = false;
                        try
                        {
                            M3ExecutionResult execution = null;
                            Exception executeError = null;
                            try { execution = adapter.ExecutePlan(binding, plan); }
                            catch (Exception ex) { executeError = ex; }
                            record["execution"] = ExecutionRecord(execution);
                            record["execution_exception"] = executeError == null ? null : executeError.ToString();
                            record["production_error"] = executeError != null ? executeError.ToString() :
                                (execution == null ? null : execution.ErrorCode + ": " + execution.ErrorMessage);
                            record["production_error_code"] = executeError is M2ModelException ? ((M2ModelException)executeError).Code :
                                (execution == null ? null : execution.ErrorCode);
                            Dictionary<string, object> analysis = execution == null ? null : FindDictionary(execution.Result, "profile_analysis");
                            record["execute_profile_analysis"] = analysis;
                            Dictionary<string, object> modelStateAfter = ModelIdRecord(testPart);
                            List<string> geometryIdsAfter = GeometryIds(testPart);
                            string activeSketchAfter = ActiveSketchId(session);
                            productionRollbackProven = String.Equals(M2Json.CanonicalText(modelStateBefore),
                                    M2Json.CanonicalText(modelStateAfter), StringComparison.Ordinal) &&
                                String.Equals(M2Json.CanonicalText(geometryIdsBefore),
                                    M2Json.CanonicalText(geometryIdsAfter), StringComparison.Ordinal) &&
                                String.Equals(activeSketchBefore, activeSketchAfter, StringComparison.Ordinal);
                            bool rolledBack = execution != null && String.Equals(execution.State, "ROLLED_BACK", StringComparison.Ordinal);
                            bool unchanged = execution != null && String.Equals(execution.State, "UNCHANGED", StringComparison.Ordinal);
                            record["production_rollback_evidence"] = new Dictionary<string, object>(StringComparer.Ordinal) {
                                { "model_state_before", modelStateBefore }, { "model_state_after_execute_before_test_cleanup", modelStateAfter },
                                { "curve_ids_before", geometryIdsBefore }, { "curve_ids_after_execute_before_test_cleanup", geometryIdsAfter },
                                { "active_sketch_before", activeSketchBefore }, { "active_sketch_after_execute_before_test_cleanup", activeSketchAfter },
                                { "native_state_matches_before_test_cleanup", productionRollbackProven },
                                { "restored_by_production_undo", rolledBack && productionRollbackProven },
                                { "unchanged_without_mutation", unchanged && productionRollbackProven },
                                { "test_undo_was_used_to_create_this_evidence", false }
                            };
                            record["revision_after_execute_before_test_cleanup"] = binding.RevisionId;
                            Require(executeError is M2ModelException || rolledBack || unchanged,
                                "Self-intersection did not return a production model error, ROLLED_BACK, or unchanged non-mutating rejection.");
                            if (rolledBack)
                            {
                                Require(String.Equals(execution.ErrorCode, "M3_OPERATION_ROLLED_BACK", StringComparison.Ordinal),
                                    "Self-intersection transaction rollback returned the wrong production error code.");
                                RequireRollbackFlag(execution.Result, "undo_called");
                                RequireRollbackFlag(execution.Result, "semantic_restored");
                                RequireRollbackFlag(execution.Result, "geometry_restored");
                                RequireRollbackFlag(execution.Result, "history_restored");
                                RequireRollbackFlag(execution.Result, "bodies_restored");
                                Require(ReadInt(execution.Result, "undo_update_error_count") == 0,
                                    "NX reported errors while restoring the self-intersection transaction.");
                                Require(!String.IsNullOrWhiteSpace(ReadString(execution.Result, "operation_exception")) &&
                                    !String.IsNullOrWhiteSpace(ReadString(execution.Result, "operation_message")),
                                    "Production rollback result omitted the native failure class/message.");
                            }
                            Require(execution == null || !String.Equals(execution.State, "APPLIED", StringComparison.Ordinal),
                                "Self-intersection was applied; an INVALID status after mutation is not accepted as rollback evidence.");
                            Require(productionRollbackProven,
                                "Production rejection did not restore features, sketches, bodies, modified state, curve IDs, and active sketch before any test undo.");
                            Require(executeError is M2ModelException || (execution != null && !String.IsNullOrWhiteSpace(execution.ErrorCode)),
                                "Production rollback result omitted its real error code.");
                            Require(analysis == null || String.Equals(ReadString(analysis, "status"), "INVALID_SELF_INTERSECTION", StringComparison.Ordinal),
                                "Self-intersection failure analysis does not identify INVALID_SELF_INTERSECTION.");
                            session.DeleteUndoMark(mark, "F06 bow-tie rollback acceptance");
                            markActive = false;
                        }
                        finally
                        {
                            if (markActive)
                            {
                                record["test_cleanup_undo_used_after_rollback_failure"] = !productionRollbackProven;
                                session.UndoToMark(mark, "F06 bow-tie rollback acceptance cleanup after recorded failure");
                                session.DeleteUndoMark(mark, "F06 bow-tie rollback acceptance cleanup after recorded failure");
                            }
                        }
                    }
                    else
                    {
                        Require(prepareError is M2ModelException,
                            "Self-intersecting profile prepare failed outside the adapter's model-error contract.");
                        record["production_error"] = prepareError.ToString();
                        Dictionary<string, object> modelStateAfterPrepare = ModelIdRecord(testPart);
                        record["production_rollback_evidence"] = new Dictionary<string, object>(StringComparer.Ordinal) {
                            { "model_state_before", modelStateBefore }, { "model_state_after_prepare", modelStateAfterPrepare },
                            { "curve_ids_before", geometryIdsBefore }, { "curve_ids_after_prepare", GeometryIds(testPart) },
                            { "active_sketch_before", activeSketchBefore }, { "active_sketch_after_prepare", ActiveSketchId(session) },
                            { "restored_by_production_prepare", String.Equals(M2Json.CanonicalText(modelStateBefore), M2Json.CanonicalText(modelStateAfterPrepare), StringComparison.Ordinal) },
                            { "test_undo_was_used_to_create_this_evidence", false }
                        };
                        Require(String.Equals(M2Json.CanonicalText(modelStateBefore), M2Json.CanonicalText(modelStateAfterPrepare), StringComparison.Ordinal) &&
                            String.Equals(M2Json.CanonicalText(geometryIdsBefore), M2Json.CanonicalText(GeometryIds(testPart)), StringComparison.Ordinal) &&
                            String.Equals(activeSketchBefore, ActiveSketchId(session), StringComparison.Ordinal),
                            "Prepare rejection changed the native model before any test undo.");
                        Require(String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal),
                            "Prepare rejection changed the binding revision.");
                    }

                    string after = ModelFingerprint(testPart);
                    record["model_fingerprint_before"] = before;
                    record["model_fingerprint_after"] = after;
                    record["revision_before"] = revisionBefore;
                    record["revision_after"] = binding.RevisionId;
                    Require(String.Equals(before, after, StringComparison.Ordinal), "Self-intersection rejection left native feature/sketch/body changes behind.");
                    Require(String.Equals(M2Json.CanonicalText(geometryIdsBefore), M2Json.CanonicalText(GeometryIds(testPart)), StringComparison.Ordinal),
                        "Self-intersection rollback changed the native Sketch.GetAllGeometry inventory.");
                });

            RunCase(reportPath, evidence, cases, defects, "F06_external_geometry_edit_invalidates_and_undo_recovers",
                "Edit one interpolation point on a verified native spline in place, preserving its curve identity and inventory so revision change proves native shape data is captured.",
                new string[] { "Apply and verify a fresh sketch containing only one cubic open spline.", "Prepare and retain a separate unexecuted line-sketch plan; capture the anchor spline's native points, evaluated shape, poles, knots, Tag, JournalIdentifier, curve IDs/count, and production inspection.", "Within an NX undo mark, use UF Curve.EditSplineThruPts to move one middle interpolation point 2 mm along the sketch Y axis without adding or deleting curves.", "Refresh inspect/revision; attempt stale execution and verify the old applied spline plan.", "Undo the edit, reread the unchanged native identity/inventory and original NURBS/shape, and verify the old spline plan again." },
                "The native spline shape and poles/knots change while Tag, JournalIdentifier, curve IDs/count, features, and sketches stay fixed; InspectSketch and revision change; stale execute is UNCHANGED; old verification fails while edited; Undo restores native data and verification.",
                delegate(Dictionary<string, object> record)
                {
                    List<M3SketchGeometryRequest> splineGeometry = new List<M3SketchGeometryRequest> {
                        Spline("revision-anchor-spline", new double[][] {
                            new double[] { 2, 2 }, new double[] { 6, 7 }, new double[] { 11, 3 }, new double[] { 16, 8 }, new double[] { 20, 4 }
                        })
                    };
                    SketchSpec anchor = ApplySketch(adapter, binding, testPart, "revision-anchor-spline",
                        GeometryOnlyRequest(testFrame, splineGeometry), testFrame, record, "OPEN_GEOMETRY");
                    appliedSketches.Add(anchor);
                    RequireProfileMetrics(record, 1, 1, 0, 0, 0);
                    RequireProfileAnalysisAvailableAndSafe(record, true);
                    staleAnchorSketchId = anchor.SketchId;
                    M3VerificationResult baseline = adapter.VerifyPlan(binding, anchor.Plan);
                    record["baseline_verification"] = VerificationRecord(baseline);
                    Require(baseline != null && baseline.Verified && ChecksPass(baseline.Checks), "Single-spline anchor did not verify before external edit.");

                    Sketch anchorSketch = FindSketch(testPart, staleAnchorSketchId);
                    int geometryBefore = CountCurveObjects(anchorSketch);
                    Require(geometryBefore == 1, "External-edit anchor must contain exactly one native spline curve.");
                    Spline anchorSpline = FindSingleSpline(anchorSketch);
                    string splineIdBefore = anchorSpline.JournalIdentifier;
                    string splineTagBefore = anchorSpline.Tag.ToString();
                    Dictionary<string, object> modelStateBefore = ModelIdRecord(testPart);
                    List<string> curveIdsBefore = GeometryIds(testPart);
                    Dictionary<string, object> inspectBefore = adapter.InspectSketch(binding, staleAnchorSketchId);
                    record["inspect_sketch_before_edit"] = inspectBefore;
                    RequireInspectionSplineSnapshot(inspectBefore);
                    Dictionary<string, object> splineInspectBefore = FindSplineInspection(inspectBefore);
                    record["native_spline_inspection_before_edit"] = splineInspectBefore;
                    Dictionary<string, object> splineNurbsInspectBefore = ReadDictionary(splineInspectBefore, "nurbs_definition");
                    record["native_spline_nurbs_inspection_before_edit"] = splineNurbsInspectBefore;
                    List<object> nurbsBefore = NurbsRecords(testPart);
                    string nurbsSnapshotBefore = M2Json.CanonicalText(nurbsBefore);
                    M3SketchRequest staleRequest = Request(testFrame, new List<M3SketchGeometryRequest> { Line("stale-prepared-line", 0, 15, 5, 15) });
                    staleRequest.RevisionId = binding.RevisionId;
                    M3PlanSnapshot stalePlan = adapter.PrepareSketch(binding, staleRequest);
                    record["stale_prepared_plan"] = stalePlan == null ? null : stalePlan.PublicView;
                    string revisionBefore = binding.RevisionId;
                    Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F06 external sketch geometry edit");
                    try
                    {
                        int degreeBefore, periodicBefore, pointCountBefore;
                        UFCurve.PtSlopeCrvatr[] editPoints;
                        double[] parametersBefore;
                        UFSession.GetUFSession().Curve.AskSplineThruPts(anchorSpline.Tag,
                            out degreeBefore, out periodicBefore, out pointCountBefore, out editPoints, out parametersBefore);
                        Require(degreeBefore == 3 && periodicBefore == 0 && pointCountBefore == 5,
                            "Fresh anchor spline native interpolation definition is not the expected open cubic.");
                        int editedPointIndex = pointCountBefore / 2;
                        Point3d beforeFitPoint = new Point3d(editPoints[editedPointIndex].point[0],
                            editPoints[editedPointIndex].point[1], editPoints[editedPointIndex].point[2]);
                        Point3d beforeEvaluatedPoint = EvaluateCurveAtParameter(anchorSpline, parametersBefore[editedPointIndex]);
                        Matrix3x3 axes = anchorSketch.Orientation.Element;
                        double[] sketchY = Normalize(new double[] { axes.Yx, axes.Yy, axes.Yz });
                        Point3d expectedEditedPoint = new Point3d(beforeFitPoint.X + 2.0 * sketchY[0],
                            beforeFitPoint.Y + 2.0 * sketchY[1], beforeFitPoint.Z + 2.0 * sketchY[2]);
                        UFCurve.PtSlopeCrvatr editPoint = editPoints[editedPointIndex];
                        editPoint.point = new double[] { expectedEditedPoint.X, expectedEditedPoint.Y, expectedEditedPoint.Z };
                        editPoints[editedPointIndex] = editPoint;
                        record["external_edit_expected"] = new Dictionary<string, object>(StringComparer.Ordinal) {
                            { "spline_id", splineIdBefore }, { "point_index", editedPointIndex },
                            { "offset_mm", 2.0 }, { "sketch_y_axis_world", sketchY },
                            { "interpolation_point_before_world_mm", Point(beforeFitPoint) },
                            { "evaluated_point_before_world_mm", Point(beforeEvaluatedPoint) },
                            { "expected_interpolation_point_after_world_mm", Point(expectedEditedPoint) }
                        };
                        UFSession.GetUFSession().Curve.EditSplineThruPts(anchorSpline.Tag, 3, 0,
                            pointCountBefore, editPoints, parametersBefore, 1);
                        int editUpdateErrors = session.UpdateManager.DoUpdate(mark);
                        record["external_edit_update_error_count"] = editUpdateErrors;
                        Require(editUpdateErrors == 0,
                            "NX reported update errors after the in-place native spline edit.");
                        record["native_geometry_count_after_external_edit"] = CountCurveObjects(anchorSketch);
                        Require(CountCurveObjects(anchorSketch) == geometryBefore, "In-place spline edit changed the sketch curve count.");
                        Require(String.Equals(anchorSpline.JournalIdentifier, splineIdBefore, StringComparison.Ordinal) &&
                            String.Equals(anchorSpline.Tag.ToString(), splineTagBefore, StringComparison.Ordinal),
                            "In-place spline edit changed its NX Tag or JournalIdentifier.");
                        Require(String.Equals(M2Json.CanonicalText(curveIdsBefore), M2Json.CanonicalText(GeometryIds(testPart)), StringComparison.Ordinal),
                            "In-place spline edit changed the native curve ID inventory.");
                        Require(String.Equals(M2Json.CanonicalText(modelStateBefore), M2Json.CanonicalText(ModelIdRecord(testPart)), StringComparison.Ordinal),
                            "In-place spline edit changed feature/sketch/body identity or part modified state.");

                        int degreeAfter, periodicAfter, pointCountAfter;
                        UFCurve.PtSlopeCrvatr[] editedPoints;
                        double[] parametersAfter;
                        UFSession.GetUFSession().Curve.AskSplineThruPts(anchorSpline.Tag,
                            out degreeAfter, out periodicAfter, out pointCountAfter, out editedPoints, out parametersAfter);
                        Require(degreeAfter == degreeBefore && periodicAfter == periodicBefore && pointCountAfter == pointCountBefore,
                            "In-place spline edit changed degree, periodicity, or interpolation point count.");
                        Point3d actualFitPoint = new Point3d(editedPoints[editedPointIndex].point[0],
                            editedPoints[editedPointIndex].point[1], editedPoints[editedPointIndex].point[2]);
                        Point3d actualEvaluatedPoint = EvaluateCurveAtParameter(anchorSpline, parametersAfter[editedPointIndex]);
                        Require(PointDistance(actualFitPoint, expectedEditedPoint) <= PositionToleranceMm,
                            "UF AskSplineThruPts did not return the edited middle interpolation point.");
                        Require(PointDistance(actualEvaluatedPoint, expectedEditedPoint) <= PositionToleranceMm &&
                            PointDistance(actualEvaluatedPoint, beforeEvaluatedPoint) >= 1.9,
                            "The native evaluated spline shape did not move with the edited interpolation point.");
                        record["external_edit_actual"] = new Dictionary<string, object>(StringComparer.Ordinal) {
                            { "spline_id_after", anchorSpline.JournalIdentifier }, { "tag_after", anchorSpline.Tag.ToString() },
                            { "interpolation_point_after_world_mm", Point(actualFitPoint) },
                            { "evaluated_point_after_world_mm", Point(actualEvaluatedPoint) }
                        };
                        record["external_spline_direct_nurbs"] = RawCurveRecord(anchorSpline);
                        List<object> nurbsAfter = NurbsRecords(testPart);
                        string nurbsSnapshotAfter = M2Json.CanonicalText(nurbsAfter);
                        record["nurbs_records_before"] = nurbsBefore;
                        record["nurbs_records_after_external_edit"] = nurbsAfter;
                        record["nurbs_snapshot_changed"] = !String.Equals(nurbsSnapshotBefore, nurbsSnapshotAfter, StringComparison.Ordinal);
                        Require(!String.Equals(nurbsSnapshotBefore, nurbsSnapshotAfter, StringComparison.Ordinal),
                            "In-place spline edit did not change the direct native pole/knot NURBS snapshot.");
                        Dictionary<string, object> externalInspection = adapter.InspectSketch(binding, staleAnchorSketchId);
                        record["inspect_sketch_after_external_edit"] = externalInspection;
                        RequireInspectionSplineSnapshot(externalInspection);
                        Dictionary<string, object> splineInspectAfter = FindSplineInspection(externalInspection);
                        record["native_spline_inspection_after_edit"] = splineInspectAfter;
                        Dictionary<string, object> splineNurbsInspectAfter = ReadDictionary(splineInspectAfter, "nurbs_definition");
                        record["native_spline_nurbs_inspection_after_edit"] = splineNurbsInspectAfter;
                        Require(!String.Equals(M2Json.CanonicalText(splineInspectBefore), M2Json.CanonicalText(splineInspectAfter), StringComparison.Ordinal),
                            "InspectSketch's native spline geometry record did not change after the in-place edit.");
                        Require(!String.Equals(M2Json.CanonicalText(splineNurbsInspectBefore), M2Json.CanonicalText(splineNurbsInspectAfter), StringComparison.Ordinal),
                            "InspectSketch's nested native NURBS definition did not change after the in-place edit.");
                        Dictionary<string, object> inspectedPart = adapter.InspectPart(binding);
                        record["inspect_part_after_external_edit"] = inspectedPart;
                        record["revision_before_edit"] = revisionBefore;
                        record["revision_after_edit"] = binding.RevisionId;
                        Require(!String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal), "External NX geometry edit did not advance model revision.");

                        M3ExecutionResult staleExecution = adapter.ExecutePlan(binding, stalePlan);
                        record["stale_execution"] = ExecutionRecord(staleExecution);
                        Require(staleExecution != null && staleExecution.State == "UNCHANGED", "Prepared plan was not invalidated by an external native geometry edit.");

                        bool changedVerificationPassed = false;
                        try
                        {
                            M3VerificationResult changedVerification = adapter.VerifyPlan(binding, anchor.Plan);
                            record["old_verification_while_edited"] = VerificationRecord(changedVerification);
                            changedVerificationPassed = changedVerification != null && changedVerification.Verified;
                        }
                        catch (Exception verifyError) { record["old_verification_exception_while_edited"] = verifyError.ToString(); }
                        Require(!changedVerificationPassed, "Old spline verification continued to pass after a real in-place native spline edit.");
                    }
                    finally
                    {
                        session.UndoToMark(mark, "F06 external sketch geometry edit");
                        session.DeleteUndoMark(mark, "F06 external sketch geometry edit");
                    }

                    Sketch restored = FindSketch(testPart, staleAnchorSketchId);
                    record["native_geometry_count_after_undo"] = CountCurveObjects(restored);
                    RequireCurveCount(restored, 1, record);
                    Spline restoredSpline = FindSingleSpline(restored);
                    Require(String.Equals(restoredSpline.JournalIdentifier, splineIdBefore, StringComparison.Ordinal) &&
                        String.Equals(restoredSpline.Tag.ToString(), splineTagBefore, StringComparison.Ordinal),
                        "Undo did not restore the original spline Tag and JournalIdentifier.");
                    VerifyNativeGeometry(restored, testFrame, splineGeometry, record);
                    List<object> nurbsAfterUndo = NurbsRecords(testPart);
                    record["nurbs_records_after_undo"] = nurbsAfterUndo;
                    Require(String.Equals(nurbsSnapshotBefore, M2Json.CanonicalText(nurbsAfterUndo), StringComparison.Ordinal),
                        "Undo did not restore the pre-edit native NURBS snapshot.");
                    record["inspect_sketch_after_undo"] = adapter.InspectSketch(binding, staleAnchorSketchId);
                    M3VerificationResult restoredVerification = adapter.VerifyPlan(binding, anchor.Plan);
                    record["old_verification_after_undo"] = VerificationRecord(restoredVerification);
                    Require(restoredVerification != null && restoredVerification.Verified && ChecksPass(restoredVerification.Checks),
                        "Undo did not restore native circle geometry and original plan verification.");
                });

            RunCase(reportPath, evidence, cases, defects, "F06_save_reopen_native_geometry_roundtrip",
                "Save and reopen a production copy, then re-read every valid F06 geometry directly from NXOpen/UF.",
                new string[] { "Call production SaveCopy with a unique workspace/v2/models target and reopen=true.", "Require SAVED, reopened=true, and all SaveCopy checks VERIFIED.", "For every accepted sketch fixture, resolve its original JournalIdentifier after reopen and compare native curve count/type/coordinates/parameters again." },
                "The target part reopens; all accepted sketches and their full native curve sets remain resolvable with exact direct geometry readback and matching profile analysis. Failed or invalid sketches leave no roundtrip artifacts.",
                delegate(Dictionary<string, object> record)
                {
                    M2SaveCopyResult copy = adapter.SaveCopy(binding, savedPath, true);
                    record["save_copy"] = SaveCopyRecord(copy);
                    Require(copy != null && copy.State == "SAVED" && copy.Reopened, "Production SaveCopy did not save and reopen the F06 model.");
                    Require(ChecksPass(copy.Checks), "One or more production SaveCopy checks did not report VERIFIED.");
                    Require(File.Exists(savedPath), "SaveCopy target file does not exist.");
                    testPart = session.Parts.Work;
                    Require(testPart != null && String.Equals(Path.GetFullPath(testPart.FullPath), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase),
                        "NX Work Part is not the unique F06 SaveCopy target after reopen.");
                    List<object> roundtrip = new List<object>();
                    foreach (SketchSpec spec in appliedSketches)
                    {
                        Sketch reopened = FindSketch(testPart, spec.SketchId);
                        Dictionary<string, object> geometryRecord = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "fixture", spec.Name }, { "sketch_id", spec.SketchId }, { "expected_native_curve_count", ExpectedNativeCurveCount(spec.Request.Geometries) }
                        };
                        VerifyNativeGeometry(reopened, spec.Frame, spec.Request.Geometries, geometryRecord);
                        Dictionary<string, object> inspect = adapter.InspectSketch(binding, spec.SketchId);
                        geometryRecord["reopened_inspect_sketch"] = inspect;
                        roundtrip.Add(geometryRecord);
                    }
                    record["reopened_sketches"] = roundtrip;
                    Require(roundtrip.Count == appliedSketches.Count, "A valid F06 sketch was omitted from the roundtrip readback set.");
                });

            evidence["all_cases_passed"] = true;
        }
        catch (Exception ex)
        {
            evidence["error_case"] = evidence.ContainsKey("current_case") ? evidence["current_case"] : "setup_or_harness";
            evidence["error"] = ex.ToString();
            evidence["passed"] = false;
            if (!evidence.ContainsKey("current_case"))
            {
                defects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "test_case", "setup_or_harness" },
                    { "reproduction_steps", new string[] { "Build the current production M2 assemblies and F06 acceptance DLL offline.", "Run RunF06Acceptance.py in an idle NX session with an unmodified original Work Part.", "Inspect the exception and unique report/model paths recorded here." } },
                    { "observed_exception", ex.ToString() },
                    { "retest_criterion", "Correct the setup or harness issue and rerun all F06 cases against a new unique source model path." }
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

    private static SketchSpec ApplySketch(M2OpenModelAdapter adapter, M2ModelBinding binding, Part part,
        string name, M3SketchRequest request, Frame expectedFrame, Dictionary<string, object> record, string expectedProfileStatus)
    {
        record["requested_constraint_mode"] = String.IsNullOrWhiteSpace(request.ConstraintMode)
            ? "legacy_driving (default)" : request.ConstraintMode;
        string before = ModelFingerprint(part);
        string revisionBefore = binding.RevisionId;
        request.RevisionId = binding.RevisionId;
        M3PlanSnapshot plan = adapter.PrepareSketch(binding, request);
        record["prepared_plan"] = plan == null ? null : plan.PublicView;
        Require(plan != null && String.Equals(plan.Kind, "create_sketch", StringComparison.Ordinal), "PrepareSketch returned an invalid plan.");
        Require(String.Equals(before, ModelFingerprint(part), StringComparison.Ordinal), "PrepareSketch modified native features/sketches/bodies or part modified state.");
        Require(String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal), "PrepareSketch changed the binding revision.");
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        record["execution"] = ExecutionRecord(execution);
        Require(execution != null && String.Equals(execution.State, "APPLIED", StringComparison.Ordinal),
            "Sketch execution was not APPLIED: " + (execution == null ? "null" : execution.ErrorCode + ": " + execution.ErrorMessage));
        string sketchId = ReadRequiredString(execution.Result, "sketch_id");
        record["sketch_id"] = sketchId;
        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        record["verification"] = VerificationRecord(verification);
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks), "Production VerifyPlan did not pass every check.");
        Sketch sketch = FindSketch(part, sketchId);
        record["native_frame"] = VerifyFrame(sketch, expectedFrame);
        VerifyNativeGeometry(sketch, expectedFrame, request.Geometries, record);
        Dictionary<string, object> inspection = adapter.InspectSketch(binding, sketchId);
        record["inspect_sketch"] = inspection;
        RequireInspectionGeometryCount(inspection, ExpectedNativeCurveCount(request.Geometries));
        RequireEllipseInspectionGeometry(inspection, expectedFrame, request.Geometries);
        RequireProfileAnalysis(execution.Result, expectedProfileStatus, record, "execute");
        RequireProfileAnalysis(verification.Result, expectedProfileStatus, record, "verify");
        Dictionary<string, object> executeAnalysis = FindDictionary(execution.Result, "profile_analysis");
        Dictionary<string, object> verifyAnalysis = FindDictionary(verification.Result, "profile_analysis");
        Require(executeAnalysis != null && verifyAnalysis != null &&
            String.Equals(M2Json.CanonicalText(executeAnalysis), M2Json.CanonicalText(verifyAnalysis), StringComparison.Ordinal),
            "Execute and live Verify profile_analysis records differ.");
        SketchSpec result = new SketchSpec { Name = name, SketchId = sketchId, Request = request, Frame = expectedFrame, Plan = plan };
        return result;
    }

    private static void RequireProfileAnalysis(object result, string expectedStatus,
        Dictionary<string, object> record, string stage)
    {
        Dictionary<string, object> analysis = FindDictionary(result, "profile_analysis");
        Require(analysis != null, stage + " result omitted the authoritative profile_analysis object.");
        record[stage + "_profile_analysis"] = analysis;
        Require(String.Equals(ReadString(analysis, "status"), expectedStatus, StringComparison.Ordinal),
            stage + " profile_analysis status differs from the expected native classification.");
    }

    private static void RequireProfileMetrics(Dictionary<string, object> record, int curves,
        int openChains, int closedLoops, int outerLoops, int innerLoops)
    {
        Dictionary<string, object> analysis = ReadDictionary(record, "execute_profile_analysis");
        Require(ReadInt(analysis, "curve_count") == curves, "profile_analysis.curve_count mismatch.");
        Require(ReadInt(analysis, "open_chain_count") == openChains, "profile_analysis.open_chain_count mismatch.");
        Require(ReadInt(analysis, "closed_loop_count") == closedLoops, "profile_analysis.closed_loop_count mismatch.");
        Require(ReadInt(analysis, "outer_loop_count") == outerLoops, "profile_analysis.outer_loop_count mismatch.");
        Require(ReadInt(analysis, "inner_loop_count") == innerLoops, "profile_analysis.inner_loop_count mismatch.");
        Require(ReadInt(analysis, "section_loop_count") == closedLoops, "profile_analysis.section_loop_count mismatch.");
    }

    private static void RequireProfileAnalysisAvailableAndSafe(Dictionary<string, object> record, bool hasSpline)
    {
        string[] stages = new string[] { "execute", "verify" };
        foreach (string stage in stages)
        {
            Dictionary<string, object> analysis = ReadDictionary(record, stage + "_profile_analysis");
            string intersection = ReadString(analysis, "native_intersection_check");
            Require(intersection == "VERIFIED" || intersection == "NOT_REQUIRED",
                stage + " native_intersection_check is FAILED or unavailable for the tested geometry.");
            string spline = ReadString(analysis, "spline_self_intersection_check");
            if (hasSpline) Require(spline == "VERIFIED", stage + " spline self-intersection check was not VERIFIED.");
            else Require(spline == "NOT_APPLICABLE" || spline == "VERIFIED", stage + " spline self-intersection check is unexpectedly failed/unavailable.");
        }
    }

    private static Dictionary<string, object> VerifyFrame(Sketch sketch, Frame expected)
    {
        Point3d origin = sketch.Origin;
        Matrix3x3 matrix = sketch.Orientation.Element;
        double[] actualOrigin = new double[] { origin.X, origin.Y, origin.Z };
        double[] actualX = new double[] { matrix.Xx, matrix.Xy, matrix.Xz };
        double[] actualY = new double[] { matrix.Yx, matrix.Yy, matrix.Yz };
        double[] actualNormal = new double[] { matrix.Zx, matrix.Zy, matrix.Zz };
        Require(VectorDistance(actualOrigin, expected.Origin) <= PositionToleranceMm, "NXOpen sketch origin differs from the requested world frame.");
        Require(VectorDistance(actualX, expected.XAxis) <= FrameComponentTolerance, "NXOpen sketch X axis differs from the independent frame calculation.");
        Require(VectorDistance(actualY, expected.YAxis) <= FrameComponentTolerance, "NXOpen sketch Y axis differs from normal cross X.");
        Require(VectorDistance(actualNormal, expected.Normal) <= FrameComponentTolerance, "NXOpen sketch normal differs from the independent frame calculation.");
        Require(Math.Abs(Dot(Cross(actualX, actualY), actualNormal) - 1.0) <= FrameComponentTolerance,
            "NXOpen sketch frame is not right-handed and orthonormal.");
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "origin_mm", actualOrigin }, { "x_axis_world", actualX }, { "y_axis_world", actualY }, { "normal_world", actualNormal }
        };
    }

    private static void VerifyNativeGeometry(Sketch sketch, Frame frame,
        List<M3SketchGeometryRequest> requested, Dictionary<string, object> record)
    {
        NXObject[] objects = sketch.GetAllGeometry();
        Require(objects != null, "Sketch.GetAllGeometry returned null.");
        List<NXObject> curves = new List<NXObject>();
        List<object> direct = new List<object>();
        foreach (NXObject obj in objects)
        {
            Require(obj != null, "Sketch.GetAllGeometry contains a null object.");
            Curve curve = obj as Curve;
            if (curve == null) continue;
            curves.Add(obj);
            direct.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                { "journal_id", obj.JournalIdentifier }, { "native_type", obj.GetType().FullName },
                { "owning_part_path", obj.OwningPart == null ? null : obj.OwningPart.FullPath }
            });
        }
        int expectedCount = ExpectedNativeCurveCount(requested);
        Require(curves.Count == expectedCount, "Native NX curve count differs from the requested sketch geometry count.");
        record["direct_nx_curves"] = direct;
        record["direct_nx_curve_count"] = curves.Count;
        record["expected_nx_curve_count"] = expectedCount;

        List<object> expectedWorld = new List<object>();
        List<object> nativeParameterReadbacks = new List<object>();
        bool[] used = new bool[curves.Count];
        foreach (M3SketchGeometryRequest geometry in requested)
        {
            if (geometry.Type == "line")
            {
                Point3d a = WorldPoint(frame, geometry.StartMm[0], geometry.StartMm[1]);
                Point3d b = WorldPoint(frame, geometry.EndMm[0], geometry.EndMm[1]);
                expectedWorld.Add(new Dictionary<string, object> { { "id", geometry.Id }, { "type", "line" }, { "start_mm", Point(a) }, { "end_mm", Point(b) } });
                MatchLine(curves, used, a, b, geometry.Id);
            }
            else if (geometry.Type == "rectangle")
            {
                double x = geometry.CornerMm[0], y = geometry.CornerMm[1];
                Point3d[] points = new Point3d[] {
                    WorldPoint(frame, x, y), WorldPoint(frame, x + geometry.WidthMm, y),
                    WorldPoint(frame, x + geometry.WidthMm, y + geometry.HeightMm), WorldPoint(frame, x, y + geometry.HeightMm)
                };
                for (int i = 0; i < 4; i++) MatchLine(curves, used, points[i], points[(i + 1) % 4], geometry.Id + "-edge-" + i.ToString(CultureInfo.InvariantCulture));
                expectedWorld.Add(new Dictionary<string, object> { { "id", geometry.Id }, { "type", "rectangle" }, { "corners_mm", Points(points) } });
            }
            else if (geometry.Type == "circle")
            {
                Point3d center = WorldPoint(frame, geometry.CenterMm[0], geometry.CenterMm[1]);
                MatchCircle(curves, used, center, geometry.DiameterMm / 2.0, geometry.Id);
                expectedWorld.Add(new Dictionary<string, object> { { "id", geometry.Id }, { "type", "circle" }, { "center_mm", Point(center) }, { "radius_mm", geometry.DiameterMm / 2.0 } });
            }
            else if (geometry.Type == "arc")
            {
                Point3d center = WorldPoint(frame, geometry.CenterMm[0], geometry.CenterMm[1]);
                Point3d start = EllipticWorldPoint(frame, geometry.CenterMm, geometry.RadiusMm, geometry.RadiusMm, 0.0, geometry.StartAngleDeg);
                Point3d end = EllipticWorldPoint(frame, geometry.CenterMm, geometry.RadiusMm, geometry.RadiusMm, 0.0, geometry.StartAngleDeg + geometry.SweepDeg);
                nativeParameterReadbacks.Add(MatchArc(curves, used, frame, geometry.CenterMm, geometry.RadiusMm,
                    geometry.StartAngleDeg, geometry.SweepDeg, start, end, frame.Normal, geometry.Id));
                expectedWorld.Add(new Dictionary<string, object> { { "id", geometry.Id }, { "type", "arc" }, { "center_mm", Point(center) }, { "radius_mm", geometry.RadiusMm }, { "start_mm", Point(start) }, { "end_mm", Point(end) }, { "sweep_deg", geometry.SweepDeg } });
            }
            else if (geometry.Type == "ellipse")
            {
                Point3d center = WorldPoint(frame, geometry.CenterMm[0], geometry.CenterMm[1]);
                double[] majorAxis = Normalize(Add(Scale(frame.XAxis, Math.Cos(geometry.RotationDeg * Pi / 180.0)), Scale(frame.YAxis, Math.Sin(geometry.RotationDeg * Pi / 180.0))));
                nativeParameterReadbacks.Add(MatchEllipse(curves, used, frame, geometry.CenterMm, geometry.RotationDeg,
                    geometry.MajorRadiusMm, geometry.MinorRadiusMm, majorAxis, 0.0, 360.0,
                    null, null, frame.Normal, geometry.Id, false));
                expectedWorld.Add(new Dictionary<string, object> { { "id", geometry.Id }, { "type", "ellipse" }, { "center_mm", Point(center) }, { "major_radius_mm", geometry.MajorRadiusMm }, { "minor_radius_mm", geometry.MinorRadiusMm }, { "major_axis_world", majorAxis } });
            }
            else if (geometry.Type == "elliptical_arc")
            {
                Point3d center = WorldPoint(frame, geometry.CenterMm[0], geometry.CenterMm[1]);
                double rotation = geometry.RotationDeg * Pi / 180.0;
                double[] majorAxis = Normalize(Add(Scale(frame.XAxis, Math.Cos(rotation)), Scale(frame.YAxis, Math.Sin(rotation))));
                Point3d start = EllipticWorldPoint(frame, geometry.CenterMm, geometry.MajorRadiusMm, geometry.MinorRadiusMm, geometry.RotationDeg, geometry.StartAngleDeg);
                Point3d end = EllipticWorldPoint(frame, geometry.CenterMm, geometry.MajorRadiusMm, geometry.MinorRadiusMm, geometry.RotationDeg, geometry.StartAngleDeg + geometry.SweepDeg);
                nativeParameterReadbacks.Add(MatchEllipse(curves, used, frame, geometry.CenterMm, geometry.RotationDeg,
                    geometry.MajorRadiusMm, geometry.MinorRadiusMm, majorAxis, geometry.StartAngleDeg, geometry.SweepDeg,
                    start, end, frame.Normal, geometry.Id, true));
                expectedWorld.Add(new Dictionary<string, object> { { "id", geometry.Id }, { "type", "elliptical_arc" }, { "center_mm", Point(center) }, { "major_radius_mm", geometry.MajorRadiusMm }, { "minor_radius_mm", geometry.MinorRadiusMm }, { "major_axis_world", majorAxis }, { "start_mm", Point(start) }, { "end_mm", Point(end) }, { "sweep_deg", geometry.SweepDeg } });
            }
            else if (geometry.Type == "spline")
            {
                List<Point3d> points = new List<Point3d>();
                foreach (double[] p in geometry.InterpolationPointsMm) points.Add(WorldPoint(frame, p[0], p[1]));
                Dictionary<string, object> splineReadback;
                MatchSpline(curves, used, geometry, points, geometry.Id, record, out splineReadback);
                nativeParameterReadbacks.Add(splineReadback);
                expectedWorld.Add(new Dictionary<string, object> { { "id", geometry.Id }, { "type", "spline" }, { "interpolation_points_world_mm", Points(points) }, { "degree", geometry.Degree } });
            }
            else throw new InvalidOperationException("F06 verification encountered unrecognized geometry type " + geometry.Type + ".");
        }
        for (int i = 0; i < used.Length; i++) Require(used[i], "A native curve was not matched to any requested geometry; unexpected extra curve.");
        record["expected_world_geometry"] = expectedWorld;
        record["native_curve_parameter_readbacks"] = nativeParameterReadbacks;
    }

    private static void MatchLine(List<NXObject> curves, bool[] used, Point3d a, Point3d b, string id)
    {
        for (int i = 0; i < curves.Count; i++)
        {
            if (used[i]) continue;
            Line line = curves[i] as Line;
            if (line == null) continue;
            bool forward = PointDistance(line.StartPoint, a) <= PositionToleranceMm && PointDistance(line.EndPoint, b) <= PositionToleranceMm;
            bool reverse = PointDistance(line.StartPoint, b) <= PositionToleranceMm && PointDistance(line.EndPoint, a) <= PositionToleranceMm;
            if (forward || reverse) { used[i] = true; return; }
        }
        throw new InvalidOperationException("No native NXOpen.Line has requested endpoints for " + id + ".");
    }

    private static void MatchCircle(List<NXObject> curves, bool[] used, Point3d center, double radius, string id)
    {
        for (int i = 0; i < curves.Count; i++)
        {
            if (used[i]) continue;
            Arc arc = curves[i] as Arc;
            if (arc == null) continue;
            Point3d actualCenter = arc.CenterPoint;
            double actualRadius = arc.Radius;
            if (PointDistance(actualCenter, center) <= PositionToleranceMm && Math.Abs(actualRadius - radius) <= RadiusToleranceMm)
            {
                Require(Math.Abs(Math.Abs(arc.EndAngle - arc.StartAngle) - 2.0 * Pi) <= AngleToleranceRad,
                    "Native NX Arc requested as a circle does not cover a full turn.");
                used[i] = true;
                return;
            }
        }
        throw new InvalidOperationException("No native NXOpen.Arc has requested circle center/radius for " + id + ".");
    }

    private static Dictionary<string, object> MatchArc(List<NXObject> curves, bool[] used, Frame frame,
        double[] localCenter, double radius, double startAngleDeg, double sweepDeg, Point3d start,
        Point3d end, double[] normal, string id)
    {
        for (int i = 0; i < curves.Count; i++)
        {
            if (used[i]) continue;
            Arc obj = curves[i] as Arc;
            if (obj == null) continue;
            Point3d actualCenter, actualStart, actualEnd;
            Vector3d nativeXAxis, nativeYAxis;
            obj.GetOrientation(out actualCenter, out nativeXAxis, out nativeYAxis);
            double actualRadius = obj.Radius;
            Point3d expectedCenter = WorldPoint(frame, localCenter[0], localCenter[1]);
            if (PointDistance(actualCenter, expectedCenter) > PositionToleranceMm || Math.Abs(actualRadius - radius) > RadiusToleranceMm) continue;
            actualStart = ConicPoint(actualCenter, nativeXAxis, nativeYAxis, actualRadius, actualRadius, 0.0, obj.StartAngle);
            actualEnd = ConicPoint(actualCenter, nativeXAxis, nativeYAxis, actualRadius, actualRadius, 0.0, obj.EndAngle);
            Require(PointDistance(actualStart, start) <= PositionToleranceMm, "Native arc start point does not preserve the requested start angle for " + id + ".");
            Require(PointDistance(actualEnd, end) <= PositionToleranceMm, "Native arc end point does not preserve the signed sweep for " + id + ".");
            double[] nativeNormal = Cross(ToArray(nativeXAxis), ToArray(nativeYAxis));
            Require(Math.Abs(Dot(Normalize(nativeNormal), Normalize(normal))) >= 1.0 - FrameComponentTolerance,
                "Native arc plane normal differs from the tilted sketch frame for " + id + ".");
            List<object> samples = new List<object>();
            for (int sample = 1; sample <= 3; sample++)
            {
                double fraction = sample / 4.0;
                Point3d actual = EvaluateCurveAtParameter((Curve)obj, obj.StartAngle + (obj.EndAngle - obj.StartAngle) * fraction);
                Point3d expected = EllipticWorldPoint(frame, localCenter, radius, radius, 0.0,
                    startAngleDeg + sweepDeg * fraction);
                Require(PointDistance(actual, expected) <= PositionToleranceMm,
                    "Native circular arc interior sample disagrees with signed sweep for " + id + ".");
                samples.Add(new Dictionary<string, object> { { "fraction", fraction }, { "native_world_mm", Point(actual) }, { "expected_world_mm", Point(expected) } });
            }
            used[i] = true;
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "geometry_id", id }, { "native_type", obj.GetType().FullName },
                { "start_angle_rad", obj.StartAngle }, { "end_angle_rad", obj.EndAngle },
                { "signed_sweep_samples", samples }
            };
        }
        throw new InvalidOperationException("No native curve matched the requested circular arc " + id + ".");
    }

    private static Dictionary<string, object> MatchEllipse(List<NXObject> curves, bool[] used,
        Frame frame, double[] localCenter, double rotationDeg, double majorRadius, double minorRadius,
        double[] majorAxis, double startAngleDeg, double sweepDeg, Point3d? start, Point3d? end,
        double[] normal, string id, bool partial)
    {
        for (int i = 0; i < curves.Count; i++)
        {
            if (used[i] || curves[i].GetType().Name.IndexOf("Ellipse", StringComparison.OrdinalIgnoreCase) < 0) continue;
            NXObject obj = curves[i];
            Ellipse ellipse = obj as Ellipse;
            if (ellipse == null) continue;
            Point3d actualCenter, orientationCenter;
            double actualMajor, actualMinor;
            double actualStartAngle, actualEndAngle, actualRotation;
            Vector3d nativeXAxis, nativeYAxis;
            ellipse.GetParameters(out actualMajor, out actualMinor, out actualStartAngle,
                out actualEndAngle, out actualRotation, out actualCenter);
            ellipse.GetOrientation(out orientationCenter, out nativeXAxis, out nativeYAxis);
            double[] actualMajorAxis = RotatePlaneVector(ToArray(nativeXAxis), ToArray(nativeYAxis), actualRotation);
            double[] actualNormal = Cross(ToArray(nativeXAxis), ToArray(nativeYAxis));
            if (PointDistance(actualCenter, WorldPoint(frame, localCenter[0], localCenter[1])) > PositionToleranceMm ||
                Math.Abs(actualMajor - majorRadius) > RadiusToleranceMm || Math.Abs(actualMinor - minorRadius) > RadiusToleranceMm) continue;
            Require(Math.Abs(Dot(Normalize(actualMajorAxis), Normalize(majorAxis))) >= 1.0 - FrameComponentTolerance,
                "Native ellipse major-axis direction differs from requested rotation for " + id + ".");
            Require(Math.Abs(Dot(Normalize(actualNormal), Normalize(normal))) >= 1.0 - FrameComponentTolerance,
                "Native ellipse plane normal differs from the tilted sketch frame for " + id + ".");
            double[] parameterLimits = CurveParameterLimits((Curve)obj);
            double actualParameterStart = parameterLimits[0];
            double actualParameterEnd = parameterLimits[1];
            Require(actualParameterEnd > actualParameterStart,
                "UF evaluator returned a non-increasing native ellipse parameter interval for " + id + ".");
            Point3d actualStart = EvaluateCurveAtParameter((Curve)obj, actualParameterStart);
            Point3d actualEnd = EvaluateCurveAtParameter((Curve)obj, actualParameterEnd);
            if (partial)
            {
                Require(start.HasValue && end.HasValue && PointDistance(actualStart, start.Value) <= PositionToleranceMm &&
                    PointDistance(actualEnd, end.Value) <= PositionToleranceMm,
                    "Native elliptical arc endpoints do not match the signed requested parametric angles for " + id + ".");
                Require(Math.Abs((actualParameterEnd - actualParameterStart) - Math.Abs(sweepDeg) * Pi / 180.0) <= AngleToleranceRad,
                    "UF ellipse evaluator parameter span does not preserve the requested absolute sweep for " + id + ".");
                List<object> samples = new List<object>();
                for (int sample = 1; sample <= 3; sample++)
                {
                    double fraction = sample / 4.0;
                    double actualParameter = actualParameterStart + (actualParameterEnd - actualParameterStart) * fraction;
                    Point3d actual = EvaluateCurveAtParameter((Curve)obj, actualParameter);
                    Point3d expected = EllipticWorldPoint(frame, localCenter, majorRadius, minorRadius,
                        rotationDeg, startAngleDeg + sweepDeg * fraction);
                    Require(PointDistance(actual, expected) <= PositionToleranceMm,
                        "Native elliptical-arc interior sample disagrees with signed sweep for " + id + ".");
                    samples.Add(new Dictionary<string, object> { { "fraction", fraction }, { "native_parameter_rad", actualParameter },
                        { "native_world_mm", Point(actual) }, { "expected_world_mm", Point(expected) } });
                }
                used[i] = true;
                return new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "geometry_id", id }, { "native_type", obj.GetType().FullName },
                    { "start_angle_rad", actualParameterStart }, { "end_angle_rad", actualParameterEnd },
                    { "native_polar_start_angle_rad", actualStartAngle }, { "native_polar_end_angle_rad", actualEndAngle },
                    { "rotation_rad", actualRotation }, { "uf_parameter_limits_rad", parameterLimits }, { "signed_sweep_samples", samples }
                };
            }
            else
            {
                Require(Math.Abs(Math.Abs(actualEndAngle - actualStartAngle) - 2.0 * Pi) <= AngleToleranceRad,
                    "Full native ellipse parameter span is not 2*pi for " + id + ".");
                Require(Math.Abs((actualParameterEnd - actualParameterStart) - 2.0 * Pi) <= AngleToleranceRad,
                    "UF ellipse evaluator does not report a full 2*pi parameter interval for " + id + ".");
                List<object> samples = new List<object>();
                for (int sample = 0; sample <= 4; sample++)
                {
                    double fraction = sample / 4.0;
                    double actualParameter = actualParameterStart + (actualParameterEnd - actualParameterStart) * fraction;
                    Point3d actual = EvaluateCurveAtParameter((Curve)obj, actualParameter);
                    Point3d expected = EllipticWorldPoint(frame, localCenter, majorRadius, minorRadius,
                        rotationDeg, 360.0 * fraction);
                    Require(PointDistance(actual, expected) <= PositionToleranceMm,
                        "Native full-ellipse sample disagrees with the requested parametric ellipse for " + id + ".");
                    samples.Add(new Dictionary<string, object> { { "fraction", fraction }, { "native_parameter_rad", actualParameter },
                        { "native_world_mm", Point(actual) }, { "expected_world_mm", Point(expected) } });
                }
                used[i] = true;
                return new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "geometry_id", id }, { "native_type", obj.GetType().FullName },
                    { "start_angle_rad", actualParameterStart }, { "end_angle_rad", actualParameterEnd },
                    { "native_polar_start_angle_rad", actualStartAngle }, { "native_polar_end_angle_rad", actualEndAngle },
                    { "rotation_rad", actualRotation }, { "uf_parameter_limits_rad", parameterLimits }, { "full_ellipse_samples", samples }
                };
            }
        }
        throw new InvalidOperationException("No native NX ellipse matched requested geometry " + id + ".");
    }

    private static void MatchSpline(List<NXObject> curves, bool[] used, M3SketchGeometryRequest geometry,
        List<Point3d> expectedPoints, string id, Dictionary<string, object> caseRecord,
        out Dictionary<string, object> nativeRecord)
    {
        nativeRecord = null;
        for (int i = 0; i < curves.Count; i++)
        {
            if (used[i] || curves[i].GetType().Name.IndexOf("Spline", StringComparison.OrdinalIgnoreCase) < 0) continue;
            Spline spline = curves[i] as Spline;
            if (spline == null) continue;
            int degree, periodic, count;
            UFCurve.PtSlopeCrvatr[] nativePoints;
            double[] parameters;
            UFSession.GetUFSession().Curve.AskSplineThruPts(spline.Tag, out degree, out periodic, out count, out nativePoints, out parameters);
            List<object> conditionReadbacks = new List<object>();
            if (nativePoints != null)
            {
                for (int j = 0; j < nativePoints.Length; j++)
                {
                    conditionReadbacks.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "point_index", j }, { "slope_type", nativePoints[j].slope_type },
                        { "slope_vector", nativePoints[j].slope }, { "crvatr_type", nativePoints[j].crvatr_type },
                        { "curvature_vector", nativePoints[j].crvatr },
                        { "role", j == 0 || j == nativePoints.Length - 1 ? "endpoint" : "interior" }
                    });
                }
            }
            Dictionary<string, object> curveRecord = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "journal_id", spline.JournalIdentifier }, { "uf_interpolation_degree", degree },
                { "periodic", periodic }, { "interpolation_point_count", count },
                { "interpolation_parameters", parameters }, { "interpolation_points_world_mm", NativeSplinePoints(nativePoints) },
                { "condition_code_and_vector_readbacks", conditionReadbacks },
                { "condition_readback_semantics", "AUTO endpoint slope codes represent NX-generated endpoint slopes; numeric vectors are retained as native readback and are not treated as user-defined constraints." }
            };
            UFCurve.Spline nativeDefinition;
            UFSession.GetUFSession().Curve.AskSplineData(spline.Tag, out nativeDefinition);
            curveRecord["uf_native_definition"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "order", nativeDefinition.order }, { "degree", nativeDefinition.order - 1 },
                { "pole_count", nativeDefinition.num_poles }, { "knots", nativeDefinition.knots },
                { "start_parameter", nativeDefinition.start_param }, { "end_parameter", nativeDefinition.end_param },
                { "rational", nativeDefinition.is_rational }, { "poles", NativeSplinePoles(nativeDefinition) }
            };
            caseRecord["native_spline_curve_readback_before_condition_assertions"] = curveRecord;
            Require(degree == geometry.Degree && degree == 3, "UF AskSplineThruPts returned the wrong spline degree for " + id + ".");
            Require(periodic == 0, "UF spline readback is periodic for " + id + ".");
            Require(count == expectedPoints.Count && nativePoints != null && nativePoints.Length == expectedPoints.Count,
                "UF spline interpolation point count differs from request for " + id + ".");
            Require(parameters != null && parameters.Length == expectedPoints.Count, "UF spline interpolation parameter array is incomplete for " + id + ".");
            double[] expectedParams = NormalizedChordParameters(expectedPoints);
            for (int j = 0; j < expectedPoints.Count; j++)
            {
                Require(nativePoints[j].point != null && nativePoints[j].point.Length == 3, "UF spline point has no world XYZ coordinate.");
                Require(PointDistance(nativePoints[j].point, expectedPoints[j]) <= PositionToleranceMm,
                    "UF native spline interpolation point differs from the requested point " + j.ToString(CultureInfo.InvariantCulture) + " for " + id + ".");
                Point3d evaluated = EvaluateCurveAtParameter((Curve)spline, parameters[j]);
                Require(PointDistance(evaluated, expectedPoints[j]) <= PositionToleranceMm,
                    "Evaluated native spline shape does not pass through interpolation point " + j.ToString(CultureInfo.InvariantCulture) + " for " + id + ".");
                Require(Math.Abs(parameters[j] - expectedParams[j]) <= 1e-8,
                    "UF native spline parameter is not cumulative-chord-length normalized at point " + j.ToString(CultureInfo.InvariantCulture) + ".");
                bool endpoint = j == 0 || j == expectedPoints.Count - 1;
                bool slopeConditionUnconstrained = nativePoints[j].slope_type == UfCurveSlopeNone ||
                    (endpoint && nativePoints[j].slope_type == UfCurveSlopeAuto);
                Require(slopeConditionUnconstrained,
                    "UF spline has a user-defined or unexpected slope control at interpolation point " +
                    j.ToString(CultureInfo.InvariantCulture) + ".");
                Require(nativePoints[j].crvatr_type == UfCurveCrvatrNone,
                    "UF spline has a user-defined or unexpected curvature control at interpolation point " +
                    j.ToString(CultureInfo.InvariantCulture) + ".");
                Require(nativePoints[j].slope != null && nativePoints[j].slope.Length == 3 &&
                    nativePoints[j].crvatr != null && nativePoints[j].crvatr.Length == 3,
                    "UF spline did not return complete numerical slope/curvature vectors at interpolation point " +
                    j.ToString(CultureInfo.InvariantCulture) + ".");
                for (int coordinate = 0; coordinate < 3; coordinate++)
                    Require(!Double.IsNaN(nativePoints[j].slope[coordinate]) && !Double.IsInfinity(nativePoints[j].slope[coordinate]) &&
                        !Double.IsNaN(nativePoints[j].crvatr[coordinate]) && !Double.IsInfinity(nativePoints[j].crvatr[coordinate]),
                        "UF spline returned a non-finite slope/curvature readback at interpolation point " +
                        j.ToString(CultureInfo.InvariantCulture) + ".");
            }
            Require(nativeDefinition.order - 1 == 3 && nativeDefinition.num_poles >= 4,
                "UF native spline data does not report a cubic curve with at least four poles.");
            Require(nativeDefinition.knots != null && nativeDefinition.knots.Length > nativeDefinition.num_poles,
                "UF native spline knot vector is missing or too short.");
            for (int k = 0; k < nativeDefinition.knots.Length; k++)
            {
                Require(!Double.IsNaN(nativeDefinition.knots[k]) && !Double.IsInfinity(nativeDefinition.knots[k]), "UF spline knot is non-finite.");
                if (k > 0) Require(nativeDefinition.knots[k] >= nativeDefinition.knots[k - 1], "UF spline knot vector is not nondecreasing.");
            }
            curveRecord["endpoint_conditions_unconstrained"] = true;
            curveRecord["endpoint_slope_codes_allow_nx_auto_or_none"] = true;
            curveRecord["interior_slope_codes_are_none"] = true;
            curveRecord["all_curvature_codes_are_none"] = true;
            curveRecord["normalized_chord_parameters_match"] = true;
            curveRecord["native_shape_evaluated_at_all_interpolation_parameters"] = true;
            nativeRecord = curveRecord;
            used[i] = true;
            return;
        }
        throw new InvalidOperationException("No native NXOpen.Spline matched requested geometry " + id + ".");
    }

    private static object NativeSplinePoints(UFCurve.PtSlopeCrvatr[] points)
    {
        List<object> result = new List<object>();
        if (points != null) foreach (UFCurve.PtSlopeCrvatr point in points) result.Add(point.point);
        return result;
    }

    private static object NativeSplinePoles(UFCurve.Spline spline)
    {
        List<object> result = new List<object>();
        if (spline.poles != null)
        {
            for (int i = 0; i < spline.poles.GetLength(0); i++)
            {
                List<object> row = new List<object>();
                for (int j = 0; j < spline.poles.GetLength(1); j++) row.Add(spline.poles[i, j]);
                result.Add(row);
            }
        }
        return result;
    }

    private static void RequireInspectionGeometryCount(Dictionary<string, object> inspection, int expected)
    {
        List<object> geometries = FindListValue(inspection, "geometry");
        Require(geometries != null, "Production InspectSketch omitted its native geometry inventory.");
        Require(geometries.Count == expected, "InspectSketch geometry inventory count differs from direct NXOpen curve count.");
    }

    private static void RequireEllipseInspectionGeometry(Dictionary<string, object> inspection,
        Frame frame, List<M3SketchGeometryRequest> requested)
    {
        bool hasEllipse = false;
        foreach (M3SketchGeometryRequest geometry in requested)
            if (geometry.Type == "ellipse" || geometry.Type == "elliptical_arc") hasEllipse = true;
        if (!hasEllipse) return;

        List<object> records = FindListValue(inspection, "geometry");
        Require(records != null, "InspectSketch omitted ellipse geometry records.");
        bool[] used = new bool[records.Count];
        foreach (M3SketchGeometryRequest geometry in requested)
        {
            if (geometry.Type != "ellipse" && geometry.Type != "elliptical_arc") continue;
            string expectedKind = geometry.Type == "ellipse" ? "ELLIPSE" : "ELLIPTICAL_ARC";
            Point3d expectedCenter = WorldPoint(frame, geometry.CenterMm[0], geometry.CenterMm[1]);
            Point3d expectedStart = EllipticWorldPoint(frame, geometry.CenterMm, geometry.MajorRadiusMm,
                geometry.MinorRadiusMm, geometry.RotationDeg, geometry.Type == "ellipse" ? 0.0 : geometry.StartAngleDeg);
            Point3d expectedEnd = EllipticWorldPoint(frame, geometry.CenterMm, geometry.MajorRadiusMm,
                geometry.MinorRadiusMm, geometry.RotationDeg,
                geometry.Type == "ellipse" ? 360.0 : geometry.StartAngleDeg + geometry.SweepDeg);
            double rotation = geometry.RotationDeg * Pi / 180.0;
            double[] expectedMajorAxis = Normalize(Add(Scale(frame.XAxis, Math.Cos(rotation)), Scale(frame.YAxis, Math.Sin(rotation))));
            bool matched = false;
            for (int i = 0; i < records.Count; i++)
            {
                if (used[i]) continue;
                Dictionary<string, object> item = records[i] as Dictionary<string, object>;
                if (item == null || !item.ContainsKey("kind") ||
                    !String.Equals(Convert.ToString(item["kind"], CultureInfo.InvariantCulture), expectedKind, StringComparison.Ordinal)) continue;
                double[] center = NumericVector(item.ContainsKey("center") ? item["center"] : null, 3, "ellipse inspect center");
                double major = RequiredDouble(item, "major_radius_mm");
                double minor = RequiredDouble(item, "minor_radius_mm");
                if (VectorDistance(center, PointCoordinates(expectedCenter)) > PositionToleranceMm ||
                    Math.Abs(major - geometry.MajorRadiusMm) > RadiusToleranceMm ||
                    Math.Abs(minor - geometry.MinorRadiusMm) > RadiusToleranceMm) continue;

                double start = RequiredDouble(item, "start_angle_rad");
                double end = RequiredDouble(item, "end_angle_rad");
                double[] actualStart = NumericVector(item.ContainsKey("start") ? item["start"] : null, 3, "ellipse inspect start");
                double[] actualEnd = NumericVector(item.ContainsKey("end") ? item["end"] : null, 3, "ellipse inspect end");
                if (VectorDistance(actualStart, PointCoordinates(expectedStart)) > PositionToleranceMm ||
                    VectorDistance(actualEnd, PointCoordinates(expectedEnd)) > PositionToleranceMm) continue;
                double[] actualMajorAxis = Normalize(NumericVector(item.ContainsKey("major_axis_direction") ? item["major_axis_direction"] : null,
                    3, "ellipse inspect major axis"));
                double[] actualNormal = Normalize(NumericVector(item.ContainsKey("normal") ? item["normal"] : null, 3, "ellipse inspect normal"));
                double orientation = Dot(actualNormal, frame.Normal);
                Require(Math.Abs(Math.Abs(orientation) - 1.0) <= FrameComponentTolerance,
                    "InspectSketch ellipse normal is not aligned with the requested sketch frame.");
                Require(Dot(actualMajorAxis, expectedMajorAxis) >= 1.0 - FrameComponentTolerance,
                    "InspectSketch ellipse major-axis direction differs from the requested rotated axis.");
                if (geometry.Type == "ellipse")
                {
                    Require(Math.Abs(Math.Abs(end - start) - 2.0 * Pi) <= AngleToleranceRad &&
                        orientation > 0.0,
                        "InspectSketch full ellipse does not report a complete positive-frame parameter interval.");
                }
                else
                {
                    double expectedSweep = geometry.SweepDeg * Pi / 180.0;
                    double actualSignedSweep = (end - start) * orientation;
                    Require(Math.Abs(actualSignedSweep - expectedSweep) <= AngleToleranceRad,
                        "InspectSketch ellipse parameter interval does not preserve the signed schema sweep.");
                    double nativeStartExpected = orientation * geometry.StartAngleDeg * Pi / 180.0;
                    Require(PeriodicAngleError(start, nativeStartExpected) <= AngleToleranceRad,
                        "InspectSketch ellipse parameter start does not map to the requested schema parameter through its native frame.");
                }
                Require(item.ContainsKey("native_polar_start_angle_rad") && item.ContainsKey("native_polar_end_angle_rad"),
                    "InspectSketch omitted the raw native ellipse polar-angle readbacks.");
                double polarStart = RequiredDouble(item, "native_polar_start_angle_rad");
                double polarEnd = RequiredDouble(item, "native_polar_end_angle_rad");
                Require(!Double.IsNaN(polarStart) && !Double.IsInfinity(polarStart) &&
                    !Double.IsNaN(polarEnd) && !Double.IsInfinity(polarEnd),
                    "InspectSketch native ellipse polar-angle readback is non-finite.");
                used[i] = true;
                matched = true;
                break;
            }
            Require(matched, "InspectSketch did not expose a native ellipse record matching " + geometry.Id + ".");
        }
    }

    private static double[] NumericVector(object value, int expectedCount, string label)
    {
        double[] direct = value as double[];
        if (direct != null)
        {
            Require(direct.Length == expectedCount, label + " has the wrong component count.");
            return Copy(direct);
        }
        List<object> list = value as List<object>;
        Require(list != null && list.Count == expectedCount, label + " is missing or has the wrong component count.");
        double[] result = new double[expectedCount];
        for (int i = 0; i < expectedCount; i++) result[i] = Convert.ToDouble(list[i], CultureInfo.InvariantCulture);
        return result;
    }

    private static double RequiredDouble(Dictionary<string, object> values, string key)
    {
        object value = null;
        Require(values != null && values.TryGetValue(key, out value) && value != null,
            "Missing numeric field " + key + ".");
        double result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        Require(!Double.IsNaN(result) && !Double.IsInfinity(result), "Numeric field " + key + " is non-finite.");
        return result;
    }

    private static double PeriodicAngleError(double actual, double expected)
    {
        double difference = (actual - expected) % (2.0 * Pi);
        if (difference > Pi) difference -= 2.0 * Pi;
        else if (difference < -Pi) difference += 2.0 * Pi;
        return Math.Abs(difference);
    }

    private static int ExpectedNativeCurveCount(List<M3SketchGeometryRequest> geometries)
    {
        int result = 0;
        if (geometries == null) return 0;
        foreach (M3SketchGeometryRequest geometry in geometries)
            result += geometry.Type == "rectangle" ? 4 : 1;
        return result;
    }

    private static void RequireDirectGeometryCount(Dictionary<string, object> record, int expected)
    {
        Require(Convert.ToInt32(record["direct_nx_curve_count"], CultureInfo.InvariantCulture) == expected,
            "Direct NXOpen curve count differs from the expected geometry count.");
    }

    private static void RequireCurveCount(Sketch sketch, int expected, Dictionary<string, object> record)
    {
        int count = CountCurveObjects(sketch);
        record["native_curve_count"] = count;
        Require(count == expected, "NXOpen sketch curve count differs from expected value " + expected.ToString(CultureInfo.InvariantCulture) + ".");
    }

    private static int CountCurveObjects(Sketch sketch)
    {
        NXObject[] geometry = sketch.GetAllGeometry();
        Require(geometry != null, "Sketch.GetAllGeometry returned null.");
        int result = 0;
        foreach (NXObject item in geometry) if (item is Curve) result++;
        return result;
    }

    private static List<object> NurbsRecords(Part part)
    {
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Sketch sketch in part.Sketches.ToArray())
        {
            if (sketch == null) continue;
            NXObject[] geometry = sketch.GetAllGeometry();
            Require(geometry != null, "Sketch.GetAllGeometry returned null during NURBS snapshot capture.");
            foreach (NXObject item in geometry)
                if (item is Spline) records.Add(RawCurveRecord(item));
        }
        records.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            return StringComparer.Ordinal.Compare(Convert.ToString(a["journal_id"], CultureInfo.InvariantCulture),
                Convert.ToString(b["journal_id"], CultureInfo.InvariantCulture));
        });
        List<object> result = new List<object>();
        foreach (Dictionary<string, object> record in records) result.Add(record);
        return result;
    }

    private static void RequireInspectionSplineSnapshot(Dictionary<string, object> inspection)
    {
        Dictionary<string, object> splineRecord = FindSplineInspection(inspection);
        Require(splineRecord != null, "InspectSketch did not expose a native spline geometry record.");
        Dictionary<string, object> definition = ReadDictionary(splineRecord, "nurbs_definition");
        List<object> knots = FindListValue(definition, "knots");
        List<object> poles = FindListValue(definition, "poles");
        List<object> interpolationPoints = FindListValue(splineRecord, "interpolation_points");
        Require(RequiredDouble(splineRecord, "degree") == 3.0 &&
            RequiredDouble(definition, "degree") == 3.0 && RequiredDouble(definition, "order") == 4.0 &&
            knots != null && knots.Count > 0 && poles != null && poles.Count >= 4 &&
            interpolationPoints != null && interpolationPoints.Count >= 4,
            "InspectSketch nested NURBS definition omitted cubic degree/order, knots, poles, or interpolation points.");
    }

    private static Dictionary<string, object> FindSplineInspection(Dictionary<string, object> inspection)
    {
        List<object> geometries = FindListValue(inspection, "geometry");
        Require(geometries != null, "InspectSketch omitted its native geometry list.");
        foreach (object item in geometries)
        {
            Dictionary<string, object> candidate = item as Dictionary<string, object>;
            if (candidate != null && String.Equals(Convert.ToString(candidate.ContainsKey("kind") ? candidate["kind"] : null, CultureInfo.InvariantCulture),
                "SPLINE", StringComparison.Ordinal)) return candidate;
        }
        throw new InvalidOperationException("InspectSketch geometry list did not contain the expected native spline record.");
    }

    private static Spline FindSingleSpline(Sketch sketch)
    {
        Spline result = null;
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Spline spline = item as Spline;
            if (spline == null) continue;
            Require(result == null, "Sketch contains more than one native spline.");
            result = spline;
        }
        Require(result != null, "Sketch contains no native spline.");
        return result;
    }

    private static M3SketchRequest Request(Frame frame, List<M3SketchGeometryRequest> geometry)
    {
        return new M3SketchRequest { OriginMm = Copy(frame.Origin), Normal = Copy(frame.Normal), XAxis = Copy(frame.XAxis), Geometries = geometry };
    }

    private static M3SketchRequest GeometryOnlyRequest(Frame frame, List<M3SketchGeometryRequest> geometry)
    {
        M3SketchRequest request = Request(frame, geometry);
        request.ConstraintMode = "geometry_only";
        return request;
    }

    private static M3SketchGeometryRequest Line(string id, double x1, double y1, double x2, double y2)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "line", StartMm = new double[] { x1, y1 }, EndMm = new double[] { x2, y2 } };
    }

    private static M3SketchGeometryRequest Circle(string id, double x, double y, double diameter)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "circle", CenterMm = new double[] { x, y }, DiameterMm = diameter };
    }

    private static M3SketchGeometryRequest Arc(string id, double x, double y, double radius, double start, double sweep)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "arc", CenterMm = new double[] { x, y }, RadiusMm = radius, StartAngleDeg = start, SweepDeg = sweep };
    }

    private static M3SketchGeometryRequest Ellipse(string id, double x, double y, double major, double minor, double rotation)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "ellipse", CenterMm = new double[] { x, y }, MajorRadiusMm = major, MinorRadiusMm = minor, RotationDeg = rotation };
    }

    private static M3SketchGeometryRequest EllipticalArc(string id, double x, double y, double major,
        double minor, double rotation, double start, double sweep)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "elliptical_arc", CenterMm = new double[] { x, y },
            MajorRadiusMm = major, MinorRadiusMm = minor, RotationDeg = rotation, StartAngleDeg = start, SweepDeg = sweep };
    }

    private static M3SketchGeometryRequest Spline(string id, double[][] points)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "spline", InterpolationPointsMm = points, Degree = 3, EndpointCondition = "unconstrained" };
    }

    private static List<M3SketchGeometryRequest> Polygon(string prefix, double[][] points)
    {
        List<M3SketchGeometryRequest> result = new List<M3SketchGeometryRequest>();
        for (int i = 0; i < points.Length; i++)
        {
            int j = (i + 1) % points.Length;
            result.Add(Line(prefix + "-edge-" + i.ToString(CultureInfo.InvariantCulture),
                points[i][0], points[i][1], points[j][0], points[j][1]));
        }
        return result;
    }

    private static Frame BuildFrame(double[] origin, double[] normal, double[] xAxis)
    {
        double[] n = Normalize(normal);
        double[] x = Normalize(Subtract(xAxis, Scale(n, Dot(xAxis, n))));
        double[] y = Normalize(Cross(n, x));
        return new Frame { Origin = Copy(origin), Normal = n, XAxis = x, YAxis = y };
    }

    private static Point3d WorldPoint(Frame frame, double x, double y)
    {
        return new Point3d(frame.Origin[0] + frame.XAxis[0] * x + frame.YAxis[0] * y,
            frame.Origin[1] + frame.XAxis[1] * x + frame.YAxis[1] * y,
            frame.Origin[2] + frame.XAxis[2] * x + frame.YAxis[2] * y);
    }

    private static Point3d EllipticWorldPoint(Frame frame, double[] center, double major, double minor,
        double rotationDeg, double parameterDeg)
    {
        double rotation = rotationDeg * Pi / 180.0;
        double parameter = parameterDeg * Pi / 180.0;
        double x = major * Math.Cos(parameter) * Math.Cos(rotation) - minor * Math.Sin(parameter) * Math.Sin(rotation);
        double y = major * Math.Cos(parameter) * Math.Sin(rotation) + minor * Math.Sin(parameter) * Math.Cos(rotation);
        return WorldPoint(frame, center[0] + x, center[1] + y);
    }

    private static Point3d ConicPoint(Point3d center, Vector3d xAxis, Vector3d yAxis,
        double major, double minor, double rotationRad, double parameterRad)
    {
        double localX = major * Math.Cos(parameterRad) * Math.Cos(rotationRad) -
            minor * Math.Sin(parameterRad) * Math.Sin(rotationRad);
        double localY = major * Math.Cos(parameterRad) * Math.Sin(rotationRad) +
            minor * Math.Sin(parameterRad) * Math.Cos(rotationRad);
        return new Point3d(center.X + xAxis.X * localX + yAxis.X * localY,
            center.Y + xAxis.Y * localX + yAxis.Y * localY,
            center.Z + xAxis.Z * localX + yAxis.Z * localY);
    }

    private static double[] RotatePlaneVector(double[] xAxis, double[] yAxis, double rotationRad)
    {
        return Normalize(Add(Scale(xAxis, Math.Cos(rotationRad)), Scale(yAxis, Math.Sin(rotationRad))));
    }

    private static double[] ToArray(Vector3d value)
    {
        return new double[] { value.X, value.Y, value.Z };
    }

    private static double[] NormalizedChordParameters(List<Point3d> points)
    {
        double total = 0.0;
        double[] cumulative = new double[points.Count];
        for (int i = 1; i < points.Count; i++)
        {
            total += PointDistance(points[i - 1], points[i]);
            cumulative[i] = total;
        }
        Require(total > 0.0, "Spline fixture has zero total chord length.");
        for (int i = 0; i < cumulative.Length; i++) cumulative[i] /= total;
        return cumulative;
    }

    private static Point3d EvaluateCurveAtParameter(Curve curve, double parameter)
    {
        Require(curve != null && !Double.IsNaN(parameter) && !Double.IsInfinity(parameter),
            "Cannot evaluate a null curve or non-finite curve parameter.");
        double[] position = new double[3];
        UFSession.GetUFSession().Curve.EvaluateCurve(curve.Tag, parameter, 0, position);
        return new Point3d(position[0], position[1], position[2]);
    }

    private static double[] CurveParameterLimits(Curve curve)
    {
        Require(curve != null, "Cannot query parameter limits for a null native curve.");
        UFSession uf = UFSession.GetUFSession();
        IntPtr evaluator = IntPtr.Zero;
        try
        {
            uf.Eval.Initialize(curve.Tag, out evaluator);
            Require(evaluator != IntPtr.Zero, "UF did not initialize a curve evaluator.");
            double[] limits = new double[2];
            uf.Eval.AskLimits(evaluator, limits);
            Require(!Double.IsNaN(limits[0]) && !Double.IsInfinity(limits[0]) &&
                !Double.IsNaN(limits[1]) && !Double.IsInfinity(limits[1]),
                "UF returned non-finite native curve parameter limits.");
            return limits;
        }
        finally
        {
            if (evaluator != IntPtr.Zero) uf.Eval.Free(evaluator);
        }
    }

    private static Dictionary<string, object> RawCurveRecord(NXObject obj)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "journal_id", obj.JournalIdentifier }, { "native_type", obj.GetType().FullName },
            { "owning_part_path", obj.OwningPart == null ? null : obj.OwningPart.FullPath }
        };
        Line line = obj as Line;
        if (line != null)
        {
            record["start_world_mm"] = Point(line.StartPoint);
            record["end_world_mm"] = Point(line.EndPoint);
            return record;
        }
        Arc arc = obj as Arc;
        if (arc != null)
        {
            Point3d center; Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            record["center_world_mm"] = Point(arc.CenterPoint);
            record["radius_mm"] = arc.Radius;
            record["start_angle_rad"] = arc.StartAngle;
            record["end_angle_rad"] = arc.EndAngle;
            record["orientation_x_world"] = ToArray(xAxis);
            record["orientation_y_world"] = ToArray(yAxis);
            record["normal_world"] = Cross(ToArray(xAxis), ToArray(yAxis));
            record["start_world_mm"] = Point(ConicPoint(center, xAxis, yAxis, arc.Radius, arc.Radius, 0.0, arc.StartAngle));
            record["end_world_mm"] = Point(ConicPoint(center, xAxis, yAxis, arc.Radius, arc.Radius, 0.0, arc.EndAngle));
            return record;
        }
        Ellipse ellipse = obj as Ellipse;
        if (ellipse != null)
        {
            double major, minor, start, end, rotation;
            Point3d center, orientationCenter; Vector3d xAxis, yAxis;
            ellipse.GetParameters(out major, out minor, out start, out end, out rotation, out center);
            ellipse.GetOrientation(out orientationCenter, out xAxis, out yAxis);
            double[] parameterLimits = CurveParameterLimits(ellipse);
            record["center_world_mm"] = Point(center);
            record["major_radius_mm"] = major;
            record["minor_radius_mm"] = minor;
            record["rotation_rad"] = rotation;
            record["start_angle_rad"] = parameterLimits[0];
            record["end_angle_rad"] = parameterLimits[1];
            record["native_polar_start_angle_rad"] = start;
            record["native_polar_end_angle_rad"] = end;
            record["uf_parameter_limits_rad"] = parameterLimits;
            record["major_axis_direction_world"] = RotatePlaneVector(ToArray(xAxis), ToArray(yAxis), rotation);
            record["orientation_x_world"] = ToArray(xAxis);
            record["orientation_y_world"] = ToArray(yAxis);
            record["normal_world"] = Cross(ToArray(xAxis), ToArray(yAxis));
            record["start_world_mm"] = Point(EvaluateCurveAtParameter(ellipse, parameterLimits[0]));
            record["end_world_mm"] = Point(EvaluateCurveAtParameter(ellipse, parameterLimits[1]));
            return record;
        }
        Spline spline = obj as Spline;
        if (spline != null)
        {
            int degree, periodic, count;
            UFCurve.PtSlopeCrvatr[] points;
            double[] parameters;
            UFSession.GetUFSession().Curve.AskSplineThruPts(spline.Tag, out degree, out periodic, out count, out points, out parameters);
            UFCurve.Spline definition;
            UFSession.GetUFSession().Curve.AskSplineData(spline.Tag, out definition);
            record["uf_interpolation_degree"] = degree;
            record["uf_periodic"] = periodic;
            record["uf_interpolation_point_count"] = count;
            record["uf_interpolation_parameters"] = parameters;
            record["uf_interpolation_points_world_mm"] = NativeSplinePoints(points);
            record["uf_order"] = definition.order;
            record["uf_pole_count"] = definition.num_poles;
            record["uf_knots"] = definition.knots;
            record["uf_poles"] = NativeSplinePoles(definition);
        }
        return record;
    }

    private static int ReadInt(Dictionary<string, object> record, string key)
    {
        Require(record != null && record.ContainsKey(key), "Missing integer profile field " + key + ".");
        return Convert.ToInt32(record[key], CultureInfo.InvariantCulture);
    }

    private static string ReadString(Dictionary<string, object> record, string key)
    {
        Require(record != null && record.ContainsKey(key), "Missing string profile field " + key + ".");
        return Convert.ToString(record[key], CultureInfo.InvariantCulture);
    }

    private static void RequireRollbackFlag(Dictionary<string, object> record, string key)
    {
        Require(record != null && record.ContainsKey(key) && record[key] is bool && (bool)record[key],
            "Production rollback evidence did not verify " + key + ".");
    }

    private static Dictionary<string, object> ReadDictionary(Dictionary<string, object> record, string key)
    {
        object value = null;
        Require(record != null && record.TryGetValue(key, out value), "Missing dictionary field " + key + ".");
        Dictionary<string, object> result = value as Dictionary<string, object>;
        Require(result != null, "Field " + key + " is not a dictionary.");
        return result;
    }

    private static Dictionary<string, object> FindDictionary(object value, string key)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            object found;
            if (dictionary.TryGetValue(key, out found)) return found as Dictionary<string, object>;
            foreach (object nested in dictionary.Values)
            {
                Dictionary<string, object> result = FindDictionary(nested, key);
                if (result != null) return result;
            }
        }
        List<object> list = value as List<object>;
        if (list != null) foreach (object nested in list)
        {
            Dictionary<string, object> result = FindDictionary(nested, key);
            if (result != null) return result;
        }
        return null;
    }

    private static List<object> FindListValue(object value, string key)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            object found;
            if (dictionary.TryGetValue(key, out found)) return found as List<object>;
            foreach (object nested in dictionary.Values)
            {
                List<object> result = FindListValue(nested, key);
                if (result != null) return result;
            }
        }
        List<object> list = value as List<object>;
        if (list != null) foreach (object nested in list)
        {
            List<object> result = FindListValue(nested, key);
            if (result != null) return result;
        }
        return null;
    }

    private static string ReadRequiredString(Dictionary<string, object> record, string key)
    {
        object value = null;
        Require(record != null && record.TryGetValue(key, out value), "Missing required string field " + key + ".");
        string result = Convert.ToString(value, CultureInfo.InvariantCulture);
        Require(!String.IsNullOrWhiteSpace(result), "Required string field is empty: " + key + ".");
        return result;
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

    private static string ModelFingerprint(Part part) { return M2Json.CanonicalText(ModelIdRecord(part)); }

    private static List<string> GeometryIds(Part part)
    {
        List<string> result = new List<string>();
        foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null) foreach (NXObject item in sketch.GetAllGeometry()) if (item is Curve) result.Add(item.JournalIdentifier);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static string ActiveSketchId(Session session)
    {
        Sketch sketch = session == null ? null : session.ActiveSketch;
        return sketch == null ? null : sketch.JournalIdentifier;
    }

    private static Sketch FindSketch(Part part, string id)
    {
        foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null && String.Equals(sketch.JournalIdentifier, id, StringComparison.Ordinal)) return sketch;
        throw new InvalidOperationException("Native sketch JournalIdentifier was not found: " + id);
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

    private static void SavePart(Part part, string description)
    {
        PartSaveStatus status = part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
        try
        {
            Require(status != null && status.NumberUnsavedParts == 0 && status.NumberUnsavedObjects == 0,
                "Could not save the " + description + " cleanly before adapter takeover.");
        }
        finally { if (status != null) status.Dispose(); }
    }

    private static void RunCase(string reportPath, Dictionary<string, object> evidence,
        List<object> cases, List<object> defects, string name, string objective,
        string[] reproductionSteps, string retestCriterion, CaseBody body)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "objective", objective }, { "status", "RUNNING" },
            { "reproduction_steps", reproductionSteps }, { "retest_criterion", retestCriterion },
            { "started_utc", UtcNow() }
        };
        cases.Add(record);
        evidence["current_case"] = name;
        Persist(reportPath, evidence);
        try
        {
            body(record);
            record["status"] = "PASSED";
            record["finished_utc"] = UtcNow();
            Persist(reportPath, evidence);
        }
        catch (Exception ex)
        {
            record["status"] = "FAILED";
            record["observed_exception"] = ex.ToString();
            record["finished_utc"] = UtcNow();
            defects.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                { "test_case", name }, { "reproduction_steps", reproductionSteps },
                { "observed_exception", ex.ToString() }, { "retest_criterion", retestCriterion }
            });
            evidence["passed"] = false;
            Persist(reportPath, evidence);
            throw;
        }
    }

    private static void RunArcOverlapRollback(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Session session, Part part, Frame frame, string label,
        List<M3SketchGeometryRequest> geometries, Dictionary<string, object> record)
    {
        M3SketchRequest request = GeometryOnlyRequest(frame, geometries);
        request.RevisionId = binding.RevisionId;
        record["requested_constraint_mode"] = request.ConstraintMode;
        Dictionary<string, object> modelBefore = ModelIdRecord(part);
        List<string> curveIdsBefore = GeometryIds(part);
        string activeSketchBefore = ActiveSketchId(session);
        string revisionBefore = binding.RevisionId;
        M3PlanSnapshot plan = null;
        Exception prepareError = null;
        try { plan = adapter.PrepareSketch(binding, request); }
        catch (Exception ex) { prepareError = ex; }
        record["prepare_exception"] = prepareError == null ? null : prepareError.ToString();
        record["prepare_error_code"] = prepareError is M2ModelException ? ((M2ModelException)prepareError).Code : null;
        Dictionary<string, object> modelAfterPrepare = ModelIdRecord(part);
        List<string> curveIdsAfterPrepare = GeometryIds(part);
        string activeSketchAfterPrepare = ActiveSketchId(session);
        record["model_state_before_prepare"] = modelBefore;
        record["model_state_after_prepare"] = modelAfterPrepare;
        record["curve_ids_before_prepare"] = curveIdsBefore;
        record["curve_ids_after_prepare"] = curveIdsAfterPrepare;
        record["active_sketch_before_prepare"] = activeSketchBefore;
        record["active_sketch_after_prepare"] = activeSketchAfterPrepare;
        Require(prepareError == null && plan != null,
            "PrepareSketch did not accept the " + label + " fixture for transactional execute-time rejection: " +
            (prepareError == null ? "null plan" : prepareError.ToString()));
        record["prepared_plan"] = plan.PublicView;
        Require(String.Equals(M2Json.CanonicalText(modelBefore), M2Json.CanonicalText(modelAfterPrepare), StringComparison.Ordinal) &&
            String.Equals(M2Json.CanonicalText(curveIdsBefore), M2Json.CanonicalText(curveIdsAfterPrepare), StringComparison.Ordinal) &&
            String.Equals(activeSketchBefore, activeSketchAfterPrepare, StringComparison.Ordinal) &&
            String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal),
            "Preparing the " + label + " fixture changed native model state or the binding revision.");

        Session.UndoMarkId cleanupMark = session.SetUndoMark(Session.MarkVisibility.Invisible,
            "F06 " + label + " production rollback evidence");
        bool cleanupMarkActive = true;
        bool productionRollbackProven = false;
        try
        {
            M3ExecutionResult execution = null;
            Exception executeError = null;
            try { execution = adapter.ExecutePlan(binding, plan); }
            catch (Exception ex) { executeError = ex; }
            record["execution"] = ExecutionRecord(execution);
            record["execution_exception"] = executeError == null ? null : executeError.ToString();
            record["execution_revision_before"] = revisionBefore;
            record["execution_revision_after_before_test_cleanup"] = binding.RevisionId;
            Dictionary<string, object> result = execution == null ? null : execution.Result;
            Dictionary<string, object> analysis = result == null ? null : FindDictionary(result, "profile_analysis");
            record["execute_profile_analysis"] = analysis;
            Dictionary<string, object> modelAfterExecution = ModelIdRecord(part);
            List<string> curveIdsAfterExecution = GeometryIds(part);
            string activeSketchAfterExecution = ActiveSketchId(session);
            productionRollbackProven = String.Equals(M2Json.CanonicalText(modelBefore),
                    M2Json.CanonicalText(modelAfterExecution), StringComparison.Ordinal) &&
                String.Equals(M2Json.CanonicalText(curveIdsBefore),
                    M2Json.CanonicalText(curveIdsAfterExecution), StringComparison.Ordinal) &&
                String.Equals(activeSketchBefore, activeSketchAfterExecution, StringComparison.Ordinal);
            bool rolledBack = execution != null && String.Equals(execution.State, "ROLLED_BACK", StringComparison.Ordinal);
            record["production_rollback_evidence"] = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "model_state_before", modelBefore },
                { "model_state_after_execute_before_test_cleanup", modelAfterExecution },
                { "curve_ids_before", curveIdsBefore },
                { "curve_ids_after_execute_before_test_cleanup", curveIdsAfterExecution },
                { "active_sketch_before", activeSketchBefore },
                { "active_sketch_after_execute_before_test_cleanup", activeSketchAfterExecution },
                { "native_state_matches_before_test_cleanup", productionRollbackProven },
                { "restored_by_production_undo", rolledBack && productionRollbackProven },
                { "unchanged_without_mutation", execution != null && String.Equals(execution.State, "UNCHANGED", StringComparison.Ordinal) && productionRollbackProven },
                { "test_undo_was_used_to_create_this_evidence", false }
            };
            Require(executeError == null && execution != null,
                "ExecutePlan threw instead of returning production rollback evidence for " + label + ".");
            Require(rolledBack && String.Equals(execution.ErrorCode, "M3_OPERATION_ROLLED_BACK", StringComparison.Ordinal),
                "The " + label + " fixture did not return ROLLED_BACK/M3_OPERATION_ROLLED_BACK.");
            RequireRollbackFlag(result, "undo_called");
            RequireRollbackFlag(result, "semantic_restored");
            RequireRollbackFlag(result, "geometry_restored");
            RequireRollbackFlag(result, "history_restored");
            RequireRollbackFlag(result, "bodies_restored");
            Require(ReadInt(result, "undo_update_error_count") == 0,
                "NX reported update errors while undoing the " + label + " profile failure.");
            string operationException = ReadString(result, "operation_exception");
            string operationMessage = ReadString(result, "operation_message");
            Require(!String.IsNullOrWhiteSpace(operationException) && !String.IsNullOrWhiteSpace(operationMessage),
                "The " + label + " production rollback omitted its operation exception or message.");
            Require(productionRollbackProven,
                "The " + label + " rollback did not restore feature/sketch/body IDs, modified state, curve IDs, and active sketch before harness cleanup.");
            Require(analysis != null, "The " + label + " production rollback omitted profile_analysis evidence.");
            Require(String.Equals(ReadString(analysis, "status"), "INVALID_SELF_INTERSECTION", StringComparison.Ordinal),
                "The " + label + " overlapping arcs were not classified as INVALID_SELF_INTERSECTION.");
            Require(String.Equals(ReadString(analysis, "native_intersection_check"), "VERIFIED", StringComparison.Ordinal),
                "The " + label + " overlap case did not complete its native intersection check.");
            record["native_operation_exception"] = operationException;
            record["native_operation_message"] = operationMessage;
            record["production_error_code"] = execution.ErrorCode;
            record["model_fingerprint_before"] = M2Json.CanonicalText(modelBefore);
            record["model_fingerprint_after_execute_before_test_cleanup"] = M2Json.CanonicalText(modelAfterExecution);
            session.DeleteUndoMark(cleanupMark, "F06 " + label + " production rollback evidence");
            cleanupMarkActive = false;
        }
        finally
        {
            if (cleanupMarkActive)
            {
                record["test_cleanup_undo_used_after_recording_production_evidence"] = true;
                session.UndoToMark(cleanupMark, "F06 " + label + " cleanup after recorded production rollback failure");
                session.DeleteUndoMark(cleanupMark, "F06 " + label + " cleanup after recorded production rollback failure");
            }
        }
    }

    private static Dictionary<string, object> ExecutionRecord(M3ExecutionResult result)
    {
        if (result == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "state", result.State }, { "revision_id", result.RevisionId }, { "result", result.Result },
            { "error_code", result.ErrorCode }, { "error_message", result.ErrorMessage }
        };
    }

    private static Dictionary<string, object> VerificationRecord(M3VerificationResult result)
    {
        if (result == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "verified", result.Verified }, { "revision_id", result.RevisionId }, { "checks", result.Checks },
            { "result", result.Result }, { "error_code", result.ErrorCode }, { "error_message", result.ErrorMessage }
        };
    }

    private static Dictionary<string, object> SaveCopyRecord(M2SaveCopyResult result)
    {
        if (result == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "state", result.State }, { "part_path", result.PartPath }, { "part_tag", result.PartTag },
            { "revision_id", result.RevisionId }, { "reopened", result.Reopened }, { "checks", result.Checks },
            { "evidence", result.Evidence }, { "error_code", result.ErrorCode }, { "error_message", result.ErrorMessage }
        };
    }

    private static bool ChecksPass(Dictionary<string, object> checks)
    {
        if (checks == null || checks.Count == 0) return false;
        foreach (object value in checks.Values)
        {
            Dictionary<string, object> item = value as Dictionary<string, object>;
            if (item == null || !item.ContainsKey("status") ||
                !String.Equals(Convert.ToString(item["status"], CultureInfo.InvariantCulture), "VERIFIED", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static Dictionary<string, object> AssemblyRecord(Assembly assembly)
    {
        string path = assembly == null ? null : assembly.Location;
        string fileVersion = null;
        if (!String.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(path);
            fileVersion = version == null ? null : version.FileVersion;
        }
        return new Dictionary<string, object>(StringComparer.Ordinal) {
            { "path", path }, { "assembly_version", assembly == null ? null : assembly.GetName().Version.ToString() }, { "file_version", fileVersion }
        };
    }

    private static object Point(Point3d p) { return new double[] { p.X, p.Y, p.Z }; }
    private static double[] PointCoordinates(Point3d p) { return new double[] { p.X, p.Y, p.Z }; }
    private static List<object> Points(Point3d[] values) { List<object> result = new List<object>(); foreach (Point3d value in values) result.Add(Point(value)); return result; }
    private static List<object> Points(List<Point3d> values) { List<object> result = new List<object>(); foreach (Point3d value in values) result.Add(Point(value)); return result; }

    private static double PointDistance(Point3d a, Point3d b) { return VectorDistance(new double[] { a.X, a.Y, a.Z }, new double[] { b.X, b.Y, b.Z }); }
    private static double PointDistance(double[] a, Point3d b) { return VectorDistance(a, new double[] { b.X, b.Y, b.Z }); }
    private static double VectorDistance(double[] a, double[] b)
    {
        double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
        return Math.Sqrt(x * x + y * y + z * z);
    }
    private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    private static double[] Cross(double[] a, double[] b) { return new double[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
    private static double[] Add(double[] a, double[] b) { return new double[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
    private static double[] Subtract(double[] a, double[] b) { return new double[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    private static double[] Scale(double[] a, double s) { return new double[] { a[0] * s, a[1] * s, a[2] * s }; }
    private static double[] Normalize(double[] value)
    {
        double length = Math.Sqrt(Dot(value, value));
        Require(length > 1e-14, "Cannot normalize a zero-length vector.");
        return Scale(value, 1.0 / length);
    }
    private static double[] Copy(double[] values) { double[] result = new double[values.Length]; Array.Copy(values, result, values.Length); return result; }

    private static string UtcNow() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
    private static void RequireNewPath(string path, string description)
    {
        Require(!File.Exists(path) && !Directory.Exists(path), "The unique " + description + " path already exists; refusing to overwrite it.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Persist(string reportPath, Dictionary<string, object> evidence)
    {
        File.WriteAllText(reportPath, M2Json.CanonicalText(evidence), new System.Text.UTF8Encoding(false));
    }
    private static void AddRestoreError(Dictionary<string, object> evidence, Exception error)
    {
        List<object> errors;
        if (!evidence.ContainsKey("restore_errors")) { errors = new List<object>(); evidence["restore_errors"] = errors; }
        else errors = evidence["restore_errors"] as List<object>;
        if (errors != null) errors.Add(error.ToString());
        evidence["passed"] = false;
    }
}
