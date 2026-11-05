// F12 production-adapter acceptance. Compile offline; invoke only in the
// root-owned, already-open NX session. It never starts or closes NX.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

public static class F12Acceptance
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    private const double PositionToleranceMm = 1.0e-6;
    private const double AreaToleranceMm2 = 1.0e-5;
    private const double VolumeToleranceMm3 = 1.0e-5;
    private const double DirectBodyMassPropertiesAccuracy = 0.999999;
    private const double CoarseComparisonMassPropertiesAccuracy = 0.99;
    private static string _reportPath;
    private static Dictionary<string, object> _report;
    private static List<object> _cases;
    private static List<object> _defects;
    private static Session _session;
    private static Part _originalPart;
    private static Part _part;
    private static M2OpenModelAdapter _adapter;
    private static M2ModelBinding _binding;
    private static bool _writesHalted;
    private static bool _unknownOutcome;

    private sealed class UnknownOutcomeException : Exception
    {
        internal UnknownOutcomeException(string message) : base(message) { }
    }

    public static void Main(string[] args)
    {
        string runId = Guid.NewGuid().ToString("N");
        string modelDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "models");
        string evidenceDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "evidence");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        string sourcePath = Path.Combine(modelDirectory, "F12-acceptance-" + runId + ".prt");
        string copyPath = Path.Combine(modelDirectory, "F12-acceptance-" + runId + "-roundtrip.prt");
        _reportPath = Path.Combine(evidenceDirectory, "F12-acceptance-" + runId + ".json");
        RequireNewPath(sourcePath, "isolated source part");
        RequireNewPath(copyPath, "SaveCopy target");
        RequireNewPath(_reportPath, "acceptance report");

        _cases = new List<object>();
        _defects = new List<object>();
        _report = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test", "F12 extrusion native subsystem acceptance" },
            { "run_id", runId }, { "started_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
            { "passed", false }, { "acceptance_status", "NOT_RUN" },
            { "layer", "native_subsystem_acceptance" }, { "sample_size", 1 },
            { "source_model_path", sourcePath }, { "save_copy_path", copyPath },
            { "report_path", _reportPath }, { "cases", _cases }, { "defects", _defects },
            { "units", new Dictionary<string, object> { { "length", "mm" }, { "area", "mm^2" }, { "volume", "mm^3" } } },
            { "position_tolerance_mm", PositionToleranceMm }, { "volume_tolerance_mm3", VolumeToleranceMm3 },
            { "instrument_calibration", "Not applicable: direct NXOpen/UF model readback only; these CAD comparison limits are not physical metrology." },
            { "configuration", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "required_nx_release", "NX2506" },
                    { "production_adapter", "M2OpenModelAdapter / IM3OperationAdapter" },
                    { "units", "Millimeters" }, { "nx_started_by_harness", false },
                    { "nx_closed_by_harness", false }, { "physical_measurement", false }
                }
            },
            { "scope_limits", new List<object>
                {
                    "One deterministic NX2506 subsystem run is one observation per case, not a reliability sample.",
                    "Only the recorded line, arc, ellipse and closed-spline fixtures and tested Boolean/extent combinations are covered.",
                    "F12 native results do not establish behavior on other NX builds or manufacturing accuracy."
                }
            },
            { "writes_halted", false }
        };
        Persist();

        try
        {
            Setup(sourcePath, runId);
            RunMultiRegionHoleReferenceCase();
            RunEllipseSolidCase();
            RunClosedSplineSolidCase();
            RunOpenArcLineSheetCase();
            RunOpenSolidReadOnlyRejection();
            RunBooleanSplitCase();
            RunUniteCase();
            RunNegativeFiniteOffsetCase();
            RunThroughAllStartInsideTargetCase();
            RunNoEffectBooleanRollbackCase();
            RunProductPostCommitRollbackCase();
            RunInvalidTopologyReadOnlyCases();
            RunSaveCopyCase(copyPath);
        }
        catch (UnknownOutcomeException ex)
        {
            _writesHalted = true;
            _unknownOutcome = true;
            _report["fatal_unknown_outcome"] = ex.ToString();
            SkipRemaining("An NX mutation outcome is UNKNOWN; preserve the model and stop all further writes.");
        }
        catch (Exception ex)
        {
            RecordDefect("F12-SETUP", ex, "Create a unique millimeter part and execute each new case through the production adapter.",
                "Preserve the original report and model; fix the reported problem and rerun in a fresh root-owned NX session with new paths.");
            _report["setup_or_runner_failure"] = ex.ToString();
        }
        finally
        {
            try
            {
                if (_adapter != null && _binding != null && !_writesHalted) _adapter.Release(_binding);
            }
            catch (Exception ex) { _report["release_error"] = ex.ToString(); }
            _report["completed_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _report["writes_halted"] = _unknownOutcome;
            _report["writes_stopped_after_failure"] = _writesHalted;
            _report["case_counts"] = CaseCounts();
            _report["passed"] = !_writesHalted && _defects.Count == 0 && AllCasesPassed();
            _report["acceptance_status"] = Object.Equals(_report["passed"], true) ? "PASS" :
                (_unknownOutcome ? "UNKNOWN_GATE_STOPPED" : (_writesHalted ? "FAILED_CASES_STOPPED" : "FAIL_OR_SKIPPED"));
            Persist();
        }
    }

    private static void Setup(string sourcePath, string runId)
    {
        _session = Session.GetSession();
        Require(_session != null, "Session.GetSession returned null.");
        _originalPart = _session.Parts.Work;
        _report["prior_work_part"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "path", _originalPart == null ? null : _originalPart.FullPath },
            { "modified", _originalPart != null && _originalPart.IsModified }
        };
        Require(!_session.IsInTaskEnvironment && _session.ActiveSketch == null,
            "Run requires an idle NX session outside a task environment and no active Sketch.");
        Require(_originalPart == null || !_originalPart.IsModified,
            "The original Work Part must be saved and unmodified; it is not edited by this harness.");
        _part = _session.Parts.NewDisplay(sourcePath, Part.Units.Millimeters);
        Require(_part != null && _part.PartUnits == BasePart.Units.Millimeters, "NX did not create a unique millimeter Part.");
        SavePart(_part, "empty isolated F12 Part");
        _adapter = new M2OpenModelAdapter();
        _binding = _adapter.Takeover(sourcePath, "saved_independent_copy",
            "f12-acceptance-session-" + runId, "f12-acceptance-load-" + runId, 1);
        Require(_adapter is IM3OperationAdapter, "Production adapter does not implement IM3OperationAdapter.");
        _report["session"] = _adapter.GetSession(_binding);
        _report["assemblies"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "bridge", AssemblyRecord(typeof(M2Json).Assembly) },
            { "adapter", AssemblyRecord(typeof(M2OpenModelAdapter).Assembly) },
            { "nxopen", AssemblyRecord(typeof(Session).Assembly) },
            { "uf", AssemblyRecord(typeof(UFSession).Assembly) }
        };
        Persist();
    }

    private static void RunMultiRegionHoleReferenceCase()
    {
        RunCase("F12-N01-whole-sketch-multi-region-hole-reference",
            "A whole-sketch solid includes two disconnected regions and subtracts an inner full-circle loop while excluding a Reference curve.",
            "The active Section source contains exactly four outer lines, one full Arc, and four island lines; the Reference line is excluded by exact native curve geometry matching. It creates two solid bodies with volumes (400-4pi)*10 and 1000 mm^3 and exact bounds for z=[-2,8].",
            delegate(Dictionary<string, object> row)
            {
                List<M3SketchGeometryRequest> geometry = MultiRegionGeometry();
                Sketch sketch = CreateSketch("multi-region", geometry, new double[] { 0, 0, 0 }, row);
                Line reference = FindLine(sketch, 50, 0, 60, 0);
                UFSession.GetUFSession().Sket.SetReferenceStatus(sketch.Tag, reference.Tag, (UFSket.ReferenceStatus)0);
                UFSket.ReferenceStatus status;
                UFSession.GetUFSession().Sket.AskReferenceStatus(sketch.Tag, reference.Tag, out status);
                Require(Convert.ToInt32(status, CultureInfo.InvariantCulture) == 0, "Fixture curve did not become UF Reference status 0.");
                _adapter.InspectPart(_binding); // refresh the production revision after the direct test-fixture state change
                row["reference_curve"] = CurveRecord(reference);
                row["profile_area_oracle_mm2"] = 500.0 - 4.0 * Math.PI;
                row["expected_volumes_mm3"] = new double[] { (400.0 - 4.0 * Math.PI) * 10.0, 1000.0 };
                M3PlanSnapshot plan = ExecuteExtrude(sketch, "create", "positive", "finite", "solid",
                    -2.0, 8.0, null, row);
                Dictionary<string, object> section = ReadSectionAndMatch(sketch, plan, 9, 1, row);
                row["section_match"] = section;
                List<Dictionary<string, object>> bodies = BodyRecords(_part);
                row["direct_bodies"] = bodies;
                Require(bodies.Count == 2, "Independent multi-region profile did not create exactly two solid bodies.");
                Require(bodies.All(body => Object.Equals(body["is_solid"], true)), "A multi-region solid output is not solid.");
                double[] actualVolumes = bodies.Select(body => Convert.ToDouble(body["volume_mm3"], CultureInfo.InvariantCulture)).OrderBy(value => value).ToArray();
                double[] expected = new double[] { 1000.0, (400.0 - 4.0 * Math.PI) * 10.0 }.OrderBy(value => value).ToArray();
                for (int i = 0; i < expected.Length; i++) RequireNear(actualVolumes[i], expected[i], VolumeToleranceMm3, "multi-region body volume " + i);
                RequireBboxExists(bodies, new double[] { 0, 0, -2, 20, 20, 8 });
                RequireBboxExists(bodies, new double[] { 30, 0, -2, 40, 10, 8 });
                row["independent_oracle_method"] = "shoelace polygon areas plus analytic full circle pi*r^2; net section area is checked against two direct NX MeasureManager volumes and exact UF body boxes";
                row["case_result"] = "PASS";
            });
    }

    private static void RunEllipseSolidCase()
    {
        RunCase("F12-N02-full-ellipse-solid", "F06 full Ellipse geometry is accepted as a solid profile and independently verified.",
            "One solid is created; direct NX volume matches pi*a*b*depth, full ellipse parameter span is one turn, and direct exact body bounds match the requested radii and depth.",
            delegate(Dictionary<string, object> row)
            {
                M3SketchGeometryRequest ellipse = new M3SketchGeometryRequest
                {
                    Id = "f12-full-ellipse", Type = "ellipse", CenterMm = new double[] { 0, 0 },
                    MajorRadiusMm = 6.0, MinorRadiusMm = 2.5, RotationDeg = 27.0
                };
                Sketch sketch = CreateSketch("ellipse", new List<M3SketchGeometryRequest> { ellipse }, new double[] { 70, 0, 0 }, row);
                double expectedVolume = Math.PI * 6.0 * 2.5 * 7.0;
                row["analytic_area_mm2"] = Math.PI * 6.0 * 2.5;
                row["expected_volume_mm3"] = expectedVolume;
                M3PlanSnapshot plan = ExecuteExtrude(sketch, "create", "positive", "finite", "solid", 0, 7, null, row);
                List<Dictionary<string, object>> bodies = FeatureBodyRecords(plan.FeatureId);
                row["direct_bodies"] = bodies;
                Require(bodies.Count == 1 && Object.Equals(bodies[0]["is_solid"], true), "Ellipse create did not produce one solid body.");
                Curve nativeEllipse = sketch.GetAllGeometry().OfType<Curve>().Single();
                row["ellipse_direct_geometry"] = CurveRecord(nativeEllipse);
                Require(nativeEllipse is Ellipse, "Direct NX sketch geometry is not an Ellipse.");
                Body ellipseBody = FeatureBodies(plan.FeatureId).Single();
                Dictionary<string, object> coarseAccuracyMeasures = MeasureBodyProperties(ellipseBody, CoarseComparisonMassPropertiesAccuracy);
                double coarseVolume = Convert.ToDouble(coarseAccuracyMeasures["volume_mm3"], CultureInfo.InvariantCulture);
                double refinedVolume = Convert.ToDouble(bodies[0]["volume_mm3"], CultureInfo.InvariantCulture);
                row["volume_measurement_accuracy_comparison"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "coarse_accuracy", coarseAccuracyMeasures["accuracy"] },
                    { "coarse_volume_mm3", coarseVolume },
                    { "coarse_area_mm2", coarseAccuracyMeasures["area_mm2"] },
                    { "refined_accuracy", bodies[0]["mass_properties_accuracy"] },
                    { "refined_volume_mm3", refinedVolume },
                    { "refined_area_mm2", bodies[0]["area_mm2"] },
                    { "volume_refinement_delta_mm3", refinedVolume - coarseVolume },
                    { "analytic_volume_mm3", expectedVolume }
                };
                RequireNear(refinedVolume, expectedVolume, VolumeToleranceMm3,
                    "rotated ellipse extrusion volume at refined direct measurement accuracy");
                row["case_result"] = "PASS";
            });
    }

    private static void RunClosedSplineSolidCase()
    {
        RunCase("F12-N02-closed-spline-solid", "A native closed F06 cubic Spline is accepted as a solid profile and area is calculated from direct UF samples.",
            "One solid is created; direct native samples are adaptively doubled until successive Green-area estimates differ by at most 0.001 mm^2 or the 65536-segment bound is reached; NX volume matches the converged independent integral times finite span.",
            delegate(Dictionary<string, object> row)
            {
                M3SketchGeometryRequest spline = new M3SketchGeometryRequest
                {
                    Id = "f12-closed-spline", Type = "spline", Degree = 3, EndpointCondition = "unconstrained",
                    InterpolationPointsMm = new double[][] {
                        new double[] { 0, 0 }, new double[] { 6, -1 }, new double[] { 10, 3 },
                        new double[] { 8, 9 }, new double[] { 2, 8 }, new double[] { 0, 0 }
                    }
                };
                Sketch sketch = CreateSketch("closed-spline", new List<M3SketchGeometryRequest> { spline }, new double[] { 90, 0, 0 }, row);
                Curve[] native = sketch.GetAllGeometry().OfType<Curve>().ToArray();
                Require(native.Length == 1 && native[0] is Spline, "Closed spline fixture did not create exactly one native Spline curve.");
                int segments = 128;
                double previousArea = SampledCurveArea(native[0], segments);
                double coarseArea = previousArea;
                double fineArea = Double.NaN;
                int coarseSegments = segments;
                int fineSegments = 0;
                bool converged = false;
                List<object> convergence = new List<object>();
                while (segments < 65536)
                {
                    int nextSegments = segments * 2;
                    double nextArea = SampledCurveArea(native[0], nextSegments);
                    double delta = Math.Abs(nextArea - previousArea);
                    convergence.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "coarse_segments", segments }, { "coarse_area_mm2", previousArea },
                        { "fine_segments", nextSegments }, { "fine_area_mm2", nextArea }, { "delta_mm2", delta }
                    });
                    coarseArea = previousArea;
                    coarseSegments = segments;
                    fineArea = nextArea;
                    fineSegments = nextSegments;
                    if (delta <= 0.001) { converged = true; break; }
                    segments = nextSegments;
                    previousArea = nextArea;
                }
                row["spline_direct_geometry"] = CurveRecord(native[0]);
                row["spline_area_oracle"] = new Dictionary<string, object>
                {
                    { "coarse_segments", coarseSegments }, { "coarse_area_mm2", coarseArea },
                    { "fine_segments", fineSegments }, { "fine_area_mm2", fineArea },
                    { "convergence_delta_mm2", Math.Abs(fineArea - coarseArea) },
                    { "convergence_history", convergence }, { "maximum_segments", 65536 },
                    { "fine_sample_count", fineSegments + 1 },
                    { "method", "Green integral over native UF evaluator samples; not production profile_analysis" }
                };
                Require(converged, "Native spline area sampling did not converge within 0.001 mm^2 before the 65536-segment maximum.");
                double expected = fineArea * 4.0;
                M3PlanSnapshot plan = ExecuteExtrude(sketch, "create", "positive", "finite", "solid", 0, 4, null, row);
                List<Dictionary<string, object>> bodies = FeatureBodyRecords(plan.FeatureId);
                row["direct_bodies"] = bodies;
                Require(bodies.Count == 1 && Object.Equals(bodies[0]["is_solid"], true), "Closed spline did not create one solid body.");
                RequireNear(Convert.ToDouble(bodies[0]["volume_mm3"], CultureInfo.InvariantCulture), expected,
                    Math.Max(VolumeToleranceMm3, 4.0 * 0.001), "closed spline extrusion volume");
                row["feature_id"] = plan.FeatureId;
                row["case_result"] = "PASS";
            });
    }

    private static void RunOpenArcLineSheetCase()
    {
        RunCase("F12-N06-open-arc-line-sheet", "Explicit finite sheet output accepts an open connected Arc/Line chain and keeps its surface open.",
            "One or two non-solid sheet outputs represent the two open source curves; aggregate faces=2 with no end caps, area=(pi*r+line_length)*extrusion_span, and direct exact bounds; no solid-volume claim applies.",
            delegate(Dictionary<string, object> row)
            {
                double radius = 4.0;
                List<M3SketchGeometryRequest> geometry = new List<M3SketchGeometryRequest>
                {
                    new M3SketchGeometryRequest { Id = "sheet-arc", Type = "arc", CenterMm = new double[] { 0, 0 }, RadiusMm = radius, StartAngleDeg = 0, SweepDeg = 180 },
                    Line("sheet-line", radius, 0, radius, 4)
                };
                Sketch sketch = CreateSketch("open-sheet", geometry, new double[] { 0, 0, 30 }, row);
                Line unrelatedFreeCurve = _part.Curves.CreateLine(
                    new Point3d(100, 120, 140), new Point3d(111, 126, 143));
                Dictionary<string, object> freeCurveBefore = CurveRecord(unrelatedFreeCurve);
                row["unrelated_non_sketch_curve_before"] = freeCurveBefore;
                Require(!sketch.GetAllGeometry().OfType<Curve>().Any(curve =>
                    String.Equals(curve.Tag.ToString(), unrelatedFreeCurve.Tag.ToString(), StringComparison.Ordinal)),
                    "The unrelated free Curve fixture was unexpectedly added to the source Sketch.");
                // Direct fixture setup is an external model change. Refresh the
                // binding before constructing the next product request.
                _adapter.InspectPart(_binding);
                row["revision_after_free_curve_fixture_refresh"] = _binding.RevisionId;
                double sourceLength = Math.PI * radius + 4.0;
                double span = 7.0;
                row["open_profile_length_oracle_mm"] = sourceLength;
                row["sheet_area_oracle_mm2"] = sourceLength * span;
                M3PlanSnapshot plan = ExecuteExtrude(sketch, "create", "positive", "finite", "sheet", -2, 5, null, row);
                List<Dictionary<string, object>> bodies = FeatureBodyRecords(plan.FeatureId);
                row["direct_bodies"] = bodies;
                Require(bodies.Count >= 1 && bodies.Count <= 2 && bodies.All(body => Object.Equals(body["is_solid"], false)),
                    "Explicit sheet request did not produce one or two sheet output bodies.");
                row["section_domain"] = ReadSectionAndMatch(sketch, plan, 2, 0, row);
                row["unrelated_non_sketch_curve_preservation"] = VerifyUnrelatedFreeCurveUnchanged(
                    _part, sketch, unrelatedFreeCurve, freeCurveBefore);
                Dictionary<string, object> faceAreaReadback = MeasureDirectFaceAreas(FeatureBodies(plan.FeatureId));
                row["direct_face_area_measurements"] = faceAreaReadback;
                row["mass_properties_area_diagnostic_mm2"] = bodies.Sum(body =>
                    Convert.ToDouble(body["mass_area_diagnostic_mm2"], CultureInfo.InvariantCulture));
                row["sheet_area_oracle_method"] = "sum per-face MeasureManager.NewFaceProperties at accuracy 0.999999 over direct Feature.GetBodies().GetFaces(); NewMassProperties.Area is retained only as a diagnostic for sheet bodies";
                RequireNear(Convert.ToDouble(faceAreaReadback["total_area_mm2"], CultureInfo.InvariantCulture),
                    sourceLength * span, AreaToleranceMm2, "open sheet sum of direct face areas");
                Require(bodies.Sum(body => Convert.ToInt32(body["face_count"], CultureInfo.InvariantCulture)) == 2,
                    "Open Arc/Line sheet was capped or lost a source side face; expected two ruled side faces total.");
                RequireBboxNear(AggregateBounds(bodies), new double[] { -4, 0, 28, 4, 4, 35 });
                row["source_arc"] = CurveRecord(sketch.GetAllGeometry().OfType<Arc>().Single());
                row["builder_readback"] = ExtrudeReadback(_part, plan.FeatureId);
                row["case_result"] = "PASS";
            });
    }

    private static void RunOpenSolidReadOnlyRejection()
    {
        RunCase("F12-N07-open-solid-rejected-read-only", "An open profile is rejected for solid output before a plan or model mutation.",
            "Prepare returns M2ModelException(UNSUPPORTED_PROFILE); no Extrude plan/feature exists and direct inventory, dirty state and binding revision are unchanged.",
            delegate(Dictionary<string, object> row)
            {
                Sketch sketch = CreateSketch("open-solid-reject", new List<M3SketchGeometryRequest> { Line("open-line", 0, 0, 12, 0) }, new double[] { 0, 0, 60 }, row);
                Dictionary<string, object> before = DirectInventory(_part);
                bool dirtyBefore = _part.IsModified;
                string revisionBefore = _binding.RevisionId;
                M2ModelException observed = null;
                try
                {
                    M3ExtrudeRequest request = new M3ExtrudeRequest
                    {
                        RevisionId = _binding.RevisionId, SketchId = sketch.JournalIdentifier,
                        Mode = "create", Direction = "positive", Extent = "finite", DepthMm = 5
                    };
                    SetRequestField(request, "ProfileOutput", "solid");
                    SetRequestField(request, "StartOffsetMm", 0.0);
                    SetRequestField(request, "EndOffsetMm", 5.0);
                    _adapter.PrepareExtrude(_binding, request);
                }
                catch (M2ModelException ex) { observed = ex; }
                row["observed_exception"] = observed == null ? null : observed.ToString();
                row["observed_error_code"] = observed == null ? null : observed.Code;
                Dictionary<string, object> after = DirectInventory(_part);
                row["direct_inventory_before"] = before; row["direct_inventory_after"] = after;
                Require(observed != null && observed.Code == "UNSUPPORTED_PROFILE", "Open solid profile did not reject with UNSUPPORTED_PROFILE.");
                Require(String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal),
                    "Rejected open solid request changed direct model inventory.");
                Require(dirtyBefore == _part.IsModified && String.Equals(revisionBefore, _binding.RevisionId, StringComparison.Ordinal),
                    "Rejected open solid request changed dirty state or binding revision.");
                row["case_result"] = "PASS";
            });
    }

    private static void RunBooleanSplitCase()
    {
        RunCase("F12-N05-subtract-splits-target-into-two-solids", "Subtracting a through-slot reports and preserves both resulting target solids.",
            "The explicit 10x10x15 mm target block becomes exactly two solids of 600 mm^3 each. The untouched non-target body's Tag, face/edge topology and sampled geometry signatures are identical before/after.",
            delegate(Dictionary<string, object> row)
            {
                Sketch targetSketch = CreateSketch("split-target", Rectangle("split-target-box", 0, 0, 10, 10), new double[] { 0, 0, -5 }, row);
                M3PlanSnapshot targetPlan = ExecuteExtrude(targetSketch, "create", "positive", "finite", "solid", 0, 15, null, row);
                Body targetBody = FeatureBodies(targetPlan.FeatureId).Single();
                Require(targetBody != null && targetBody.IsSolidBody, "Initial target body is unavailable or not solid.");
                string targetId = targetBody.JournalIdentifier;

                Sketch protectedSketch = CreateSketch("split-protected", Rectangle("protected-box", 30, 30, 34, 34), new double[] { 0, 0, -5 }, row);
                M3PlanSnapshot protectedPlan = ExecuteExtrude(protectedSketch, "create", "positive", "finite", "solid", 0, 3, null, row);
                Body protectedBody = FeatureBodies(protectedPlan.FeatureId).Single();
                Dictionary<string, object> protectedBefore = BodyRecord(protectedBody);

                Sketch cutter = CreateSketch("split-slot", Rectangle("slot", 4, -1, 6, 11), new double[] { 0, 0, -5 }, row);
                row["target_body_id"] = targetId;
                List<Dictionary<string, object>> bodyInventoryBeforeCut = BodyRecords(_part);
                M3PlanSnapshot cutPlan = ExecuteExtrude(cutter, "subtract", "positive", "finite", "solid", 0, 15, targetId, row);
                List<Dictionary<string, object>> targetOutputs = FeatureBodyRecords(cutPlan.FeatureId);
                List<Dictionary<string, object>> bodiesAfterCut = BodyRecords(_part);
                row["all_direct_bodies_before_cut"] = bodyInventoryBeforeCut;
                row["all_direct_bodies_after_cut"] = bodiesAfterCut;
                Require(targetOutputs.Count == 2, "Subtract split did not leave two independently inventoried solid outputs.");
                foreach (Dictionary<string, object> body in targetOutputs)
                {
                    Require(Object.Equals(body["is_solid"], true), "A split target output is not a solid body.");
                    RequireNear(Convert.ToDouble(body["volume_mm3"], CultureInfo.InvariantCulture), 600.0,
                        VolumeToleranceMm3, "split output body volume");
                }
                Dictionary<string, object> protectedAfter = BodyRecord(FindBodyByJournalId(protectedBody.JournalIdentifier));
                row["non_target_before"] = protectedBefore;
                row["non_target_after"] = protectedAfter;
                Require(String.Equals(M2Json.CanonicalText(protectedBefore), M2Json.CanonicalText(protectedAfter), StringComparison.Ordinal),
                    "Non-target body's direct Tag/JID, face and edge signatures, surface data, volume or exact bounds changed.");
                List<string> nonTargetIdsBefore = bodyInventoryBeforeCut
                    .Select(body => Convert.ToString(body["journal_id"], CultureInfo.InvariantCulture))
                    .Where(id => !String.Equals(id, targetId, StringComparison.Ordinal)).OrderBy(id => id, StringComparer.Ordinal).ToList();
                List<string> nonTargetIdsAfter = bodiesAfterCut
                    .Select(body => Convert.ToString(body["journal_id"], CultureInfo.InvariantCulture))
                    .Where(id => !targetOutputs.Any(output => String.Equals(Convert.ToString(output["journal_id"], CultureInfo.InvariantCulture), id, StringComparison.Ordinal)))
                    .OrderBy(id => id, StringComparer.Ordinal).ToList();
                Require(nonTargetIdsBefore.SequenceEqual(nonTargetIdsAfter), "Subtract split changed the unrelated body identity inventory.");
                foreach (string nonTargetId in nonTargetIdsBefore)
                {
                    Dictionary<string, object> beforeNonTarget = bodyInventoryBeforeCut.Single(body =>
                        String.Equals(Convert.ToString(body["journal_id"], CultureInfo.InvariantCulture), nonTargetId, StringComparison.Ordinal));
                    Dictionary<string, object> afterNonTarget = bodiesAfterCut.Single(body =>
                        String.Equals(Convert.ToString(body["journal_id"], CultureInfo.InvariantCulture), nonTargetId, StringComparison.Ordinal));
                    Require(String.Equals(M2Json.CanonicalText(beforeNonTarget), M2Json.CanonicalText(afterNonTarget), StringComparison.Ordinal),
                        "Subtract split changed an unrelated body direct face/edge signature: " + nonTargetId);
                }
                Require(bodiesAfterCut.Count == bodyInventoryBeforeCut.Count + 1,
                    "Subtract split body inventory did not increase by exactly one output solid.");
                row["split_target_body_count"] = targetOutputs.Count;
                row["split_volume_sum_mm3"] = targetOutputs.Sum(body => Convert.ToDouble(body["volume_mm3"], CultureInfo.InvariantCulture));
                RequireNear(Convert.ToDouble(row["split_volume_sum_mm3"], CultureInfo.InvariantCulture), 1200.0,
                    VolumeToleranceMm3, "sum of split target solid volumes");
                row["independent_set_oracle"] = "20*10*15 target minus a 2*10*15 slot = 1200 mm^3; each of two symmetric solids is 4*10*15 = 600 mm^3";
                row["builder_readback"] = ExtrudeReadback(_part, cutPlan.FeatureId);
                row["case_result"] = "PASS";
            });
    }

    private static void RunUniteCase()
    {
        RunCase("F12-N05-unite-explicit-target", "Unite updates exactly one explicit target and preserves an unrelated body by direct topology signature.",
            "Target box 10x10x15 plus tool 7x6x6 with a 2x6x6 overlap has independent union volume 1680 mm^3; direct NX volume, explicit target JID, and non-target body signature all match.",
            delegate(Dictionary<string, object> row)
            {
                Sketch targetSketch = CreateSketch("unite-target", Rectangle("unite-target-box", 50, 0, 60, 10), new double[] { 0, 0, -5 }, row);
                M3PlanSnapshot targetPlan = ExecuteExtrude(targetSketch, "create", "positive", "finite", "solid", 0, 15, null, row);
                Body targetBody = FeatureBodies(targetPlan.FeatureId).Single();
                string targetId = targetBody.JournalIdentifier;
                Dictionary<string, object> targetBefore = BodyRecord(targetBody);

                Sketch protectedSketch = CreateSketch("unite-protected", Rectangle("protected-unite-box", 80, 0, 84, 4), new double[] { 0, 0, -5 }, row);
                M3PlanSnapshot protectedPlan = ExecuteExtrude(protectedSketch, "create", "positive", "finite", "solid", 0, 3, null, row);
                Body protectedBody = FeatureBodies(protectedPlan.FeatureId).Single();
                Dictionary<string, object> protectedBefore = BodyRecord(protectedBody);

                Sketch toolSketch = CreateSketch("unite-tool", Rectangle("unite-tool-box", 58, 2, 65, 8), new double[] { 0, 0, 0 }, row);
                M3PlanSnapshot unitePlan = ExecuteExtrude(toolSketch, "unite", "positive", "finite", "solid", 0, 6, targetId, row);
                Body updatedTarget = FindBodyByJournalId(targetId);
                Require(updatedTarget != null, "NX did not retain the explicit target body identity after Unite.");
                Dictionary<string, object> targetAfter = BodyRecord(updatedTarget);
                Dictionary<string, object> protectedAfter = BodyRecord(FindBodyByJournalId(protectedBody.JournalIdentifier));
                row["target_before"] = targetBefore; row["target_after"] = targetAfter;
                row["non_target_before"] = protectedBefore; row["non_target_after"] = protectedAfter;
                RequireNear(Convert.ToDouble(targetAfter["volume_mm3"], CultureInfo.InvariantCulture), 1680.0,
                    VolumeToleranceMm3, "explicit target union volume");
                Require(String.Equals(M2Json.CanonicalText(protectedBefore), M2Json.CanonicalText(protectedAfter), StringComparison.Ordinal),
                    "Unite changed the non-target body topology or geometry signature.");
                Dictionary<string, object> readback = ExtrudeReadback(_part, unitePlan.FeatureId);
                row["builder_readback"] = readback;
                string[] targetIds = (string[])readback["target_body_ids"];
                Require(targetIds.Length == 1 && targetIds[0] == targetId, "Unite did not use exactly the explicit target JID.");
                row["independent_set_oracle"] = "1500 + (7*6*6) - (2*6*6) = 1680 mm^3";
                row["case_result"] = "PASS";
            });
    }

    private static void RunNegativeFiniteOffsetCase()
    {
        RunCase("F12-N03-negative-finite-offset", "Negative direction applies the signed finite start/end coordinates relative to the Sketch normal.",
            "A 4x3 mm profile extruded in negative direction from signed offsets -2 to 8 has independent volume 120 mm^3 and world Z bounds [-8,2].",
            delegate(Dictionary<string, object> row)
            {
                double[] tiltedNormal = new double[] { 0, 1, 1 };
                double[] tiltedXAxis = new double[] { 1, 0, 0 };
                Sketch sketch = CreateSketch("negative-offset-tilted", Rectangle("negative-profile", 0, 0, 4, 3),
                    new double[] { 100, 0, 0 }, row, tiltedNormal, tiltedXAxis);
                M3PlanSnapshot plan = ExecuteExtrude(sketch, "create", "negative", "finite", "solid", -2, 8, null, row);
                List<Dictionary<string, object>> bodies = FeatureBodyRecords(plan.FeatureId);
                row["direct_bodies"] = bodies;
                Require(bodies.Count == 1, "Negative finite extrusion did not create one body.");
                RequireNear(Convert.ToDouble(bodies[0]["volume_mm3"], CultureInfo.InvariantCulture), 120.0,
                    VolumeToleranceMm3, "negative direction finite volume");
                double[] expectedBox = TiltedNegativeDirectionBounds();
                double[] box = (double[])bodies[0]["exact_bbox_mm"];
                for (int i = 0; i < 6; i++) RequireNear(box[i], expectedBox[i], PositionToleranceMm, "tilted negative finite exact bound " + i);
                row["independent_world_bounds_oracle_mm"] = expectedBox;
                row["independent_volume_oracle_mm3"] = 12.0 * 10.0;
                row["feature_id"] = plan.FeatureId;
                row["case_result"] = "PASS";
            });
    }

    private static void RunThroughAllStartInsideTargetCase()
    {
        RunCase("F12-N04-through-all-start-inside-target", "True native ThroughAll starts inside the target and cuts only the remaining seven millimeters.",
            "NX readback is ExtendType.ThroughAll, start RHS=3 mm and explicit subtract target JID; direct volume is 1000-3*3*7=937 mm^3. The start plane lies inside the target, so the observed delta distinguishes it from default start=0.",
            delegate(Dictionary<string, object> row)
            {
                // Keep the ThroughAll ray clear of unrelated sheet geometry.
                // Native limit dependencies outside source/target are rejected
                // by the product and covered by the retained failed fixture.
                Sketch targetSketch = CreateSketch("through-target", Rectangle("through-target-box", 0, 0, 10, 10), new double[] { 400, 0, 200 }, row);
                M3PlanSnapshot targetPlan = ExecuteExtrude(targetSketch, "create", "positive", "finite", "solid", 0, 10, null, row);
                Body targetBody = FeatureBodies(targetPlan.FeatureId).Single();
                string targetId = targetBody.JournalIdentifier;
                Dictionary<string, object> targetBefore = BodyRecord(targetBody);

                Sketch cutSketch = CreateSketch("through-cut", Rectangle("through-cutter", 3, 3, 6, 6), new double[] { 400, 0, 200 }, row);
                M3PlanSnapshot cutPlan = ExecuteExtrude(cutSketch, "subtract", "positive", "through_all", "solid", 3, null, targetId, row);
                Dictionary<string, object> targetAfter = BodyRecord(FindBodyByJournalId(targetId));
                row["target_before"] = targetBefore;
                row["target_after"] = targetAfter;
                RequireNear(Convert.ToDouble(targetAfter["volume_mm3"], CultureInfo.InvariantCulture), 937.0,
                    VolumeToleranceMm3, "through-all cut with start inside target");
                Dictionary<string, object> readback = ExtrudeReadback(_part, cutPlan.FeatureId);
                row["builder_readback"] = readback;
                Require(String.Equals((string)readback["end_trim_type"], "ThroughAll", StringComparison.Ordinal),
                    "NX Extrude end TrimType was not ThroughAll.");
                RequireNear(Convert.ToDouble(readback["start_value_mm"], CultureInfo.InvariantCulture), 3.0,
                    PositionToleranceMm, "through-all start limit");
                row["independent_set_oracle"] = "1000 mm^3 target minus 3*3*7 mm^3 remaining intersection = 937 mm^3";
                RequireNear(Convert.ToDouble(targetBefore["exact_bbox_mm"] is double[] ? ((double[])targetBefore["exact_bbox_mm"])[5] : Double.NaN,
                    CultureInfo.InvariantCulture), 210.0, PositionToleranceMm, "positive through target upper Z");

                Sketch negativeTargetSketch = CreateSketch("through-negative-target", Rectangle("negative-through-target-box", 0, 0, 10, 10), new double[] { 450, 0, -210 }, row);
                M3PlanSnapshot negativeTargetPlan = ExecuteExtrude(negativeTargetSketch, "create", "positive", "finite", "solid", 0, 10, null, row);
                Body negativeTargetBody = FeatureBodies(negativeTargetPlan.FeatureId).Single();
                string negativeTargetId = negativeTargetBody.JournalIdentifier;
                Sketch negativeCutSketch = CreateSketch("through-negative-cut", Rectangle("negative-through-cutter", 3, 3, 6, 6), new double[] { 450, 0, -200 }, row);
                M3PlanSnapshot negativeCutPlan = ExecuteExtrude(negativeCutSketch, "subtract", "negative", "through_all", "solid", 3, null, negativeTargetId, row);
                Dictionary<string, object> negativeAfter = BodyRecord(FindBodyByJournalId(negativeTargetId));
                RequireNear(Convert.ToDouble(negativeAfter["volume_mm3"], CultureInfo.InvariantCulture), 937.0,
                    VolumeToleranceMm3, "negative ThroughAll start-inside-target cut");
                Dictionary<string, object> negativeReadback = ExtrudeReadback(_part, negativeCutPlan.FeatureId);
                row["negative_direction_readback"] = negativeReadback;
                Require(String.Equals((string)negativeReadback["end_trim_type"], "ThroughAll", StringComparison.Ordinal),
                    "Negative direction did not retain native ThroughAll end type.");
                double[] negativeVector = (double[])negativeReadback["direction_vector"];
                Require(negativeVector[2] < -0.999999, "Negative ThroughAll direction vector is not aligned to -Sketch normal.");
                row["case_result"] = "PASS";
            });
    }

    private static void RunNoEffectBooleanRollbackCase()
    {
        RunCase("F12-N07-no-effect-subtract-safe-rejection", "A valid but disjoint subtract tool is never applied and leaves no model residue.",
            "Execution is not APPLIED; it either returns a verified rollback or a native pre-commit rejection. Direct feature/body/curve inventory and full target topology match the pre-execute state. Record whether the operation diagnostic proves the M3_BOOLEAN_NO_EFFECT effect check after feature commit; only the separate controlled post-commit case counts as actual Commit plus Undo evidence.",
            delegate(Dictionary<string, object> row)
            {
                Sketch targetSketch = CreateSketch("no-effect-target", Rectangle("target", 0, 0, 10, 10), new double[] { 0, 0, 0 }, row);
                M3PlanSnapshot targetPlan = ExecuteExtrude(targetSketch, "create", "positive", "finite", "solid", 0, 10, null, row);
                Body targetBody = FeatureBodies(targetPlan.FeatureId).Single();
                string targetId = targetBody.JournalIdentifier;
                Sketch toolSketch = CreateSketch("no-effect-tool", Rectangle("remote-tool", 20, 20, 22, 22), new double[] { 0, 0, 0 }, row);
                M3ExtrudeRequest request = BuildExtrudeRequest(toolSketch, "subtract", "positive", "finite", "solid", 0, 10, targetId);
                M3PlanSnapshot plan = _adapter.PrepareExtrude(_binding, request);
                Dictionary<string, object> before = DirectInventory(_part);
                List<Dictionary<string, object>> bodiesBefore = BodyRecords(_part);
                bool dirtyBefore = _part.IsModified;
                M3ExecutionResult result = _adapter.ExecutePlan(_binding, plan);
                row["request"] = ExtrudeRequestRecord(request);
                row["execution"] = ExecutionRecord(result);
                if (result != null && result.State == "UNKNOWN")
                    throw new UnknownOutcomeException("Disjoint subtract returned UNKNOWN; preserve the model and stop writes.");
                Require(result != null && result.State == "ROLLED_BACK" && result.ErrorCode == "M3_OPERATION_ROLLED_BACK",
                    "Disjoint subtract was applied or did not return the product's verified rollback result.");
                AssertRollbackEvidence(result.Result, row);
                object rawException = null, rawExceptionCode = null, rawMessage = null;
                Require(result.Result != null && result.Result.TryGetValue("operation_exception", out rawException) &&
                        rawException is string && !String.IsNullOrWhiteSpace((string)rawException),
                    "Rollback evidence omitted the original operation exception type.");
                Require(result.Result.TryGetValue("operation_exception_code", out rawExceptionCode) &&
                        rawExceptionCode is string && !String.IsNullOrWhiteSpace((string)rawExceptionCode),
                    "Rollback evidence omitted the original operation exception code.");
                Require(result.Result.TryGetValue("operation_message", out rawMessage) &&
                        rawMessage is string && !String.IsNullOrWhiteSpace((string)rawMessage),
                    "Rollback evidence omitted the original operation diagnostic message.");
                string operationExceptionCode = (string)rawExceptionCode;
                Require(!String.Equals(operationExceptionCode, result.ErrorCode, StringComparison.Ordinal),
                    "Rollback evidence reported the wrapper result code instead of the underlying operation exception code.");
                string operationMessage = (string)rawMessage;
                bool effectMessagePresent = operationMessage.IndexOf(
                    "NX Extrude output bodies, Boolean result, or non-target body geometry did not match the requested operation",
                    StringComparison.Ordinal) >= 0;
                Require(!effectMessagePresent || String.Equals(operationExceptionCode, "M3_BOOLEAN_NO_EFFECT", StringComparison.Ordinal),
                    "Post-commit no-effect diagnostic did not retain underlying M3_BOOLEAN_NO_EFFECT code.");
                bool effectCheckRejectedAfterCommit = String.Equals(operationExceptionCode, "M3_BOOLEAN_NO_EFFECT", StringComparison.Ordinal);
                row["no_effect_outcome_stage"] = effectCheckRejectedAfterCommit
                    ? "M3_BOOLEAN_NO_EFFECT_AFTER_FEATURE_EFFECT_CHECK"
                    : "NATIVE_OR_PRECOMMIT_REJECTION_ROLLED_BACK; POST_COMMIT_EFFECT_CHECK_NOT_PROVEN";
                row["original_operation_exception"] = rawException;
                row["original_operation_exception_code"] = operationExceptionCode;
                row["original_operation_message"] = operationMessage;
                row["rollback_wrapper_error_code"] = result.ErrorCode;
                Dictionary<string, object> after = DirectInventory(_part);
                List<Dictionary<string, object>> bodiesAfter = BodyRecords(_part);
                row["direct_inventory_before"] = before; row["direct_inventory_after"] = after;
                row["bodies_before"] = bodiesBefore; row["bodies_after"] = bodiesAfter;
                Require(String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal),
                    "No-effect Boolean rollback did not restore direct model inventory.");
                Dictionary<string, object> bodyRestoreComparison;
                bool bodiesRestored = TransactionBodyRecordsMatch(bodiesBefore, bodiesAfter, out bodyRestoreComparison);
                row["direct_body_restore_comparison"] = bodyRestoreComparison;
                Require(bodiesRestored,
                    "No-effect Boolean rollback did not restore complete body topology, geometry and measurements.");
                Require(dirtyBefore == _part.IsModified, "No-effect Boolean rollback did not restore part dirty state.");
                row["case_result"] = "PASS";
            });
    }

    private static void RunProductPostCommitRollbackCase()
    {
        RunCase("F12-N07-controlled-post-commit-product-undo", "A controlled failure after actual NX Commit triggers the production Undo and restores complete direct state.",
            "Fault injection is consumed once after `CommitFeature` plus NX update. Product execution returns ROLLED_BACK with undo_called=true, zero Undo update errors, and semantic/geometry/history/body restoration all true. Direct feature/curve/body/face/edge inventory and dirty state are unchanged.",
            delegate(Dictionary<string, object> row)
            {
                Sketch sketch = CreateSketch("controlled-rollback", Rectangle("rollback-profile", 0, 0, 8, 5), new double[] { 140, 0, 0 }, row);
                M3ExtrudeRequest request = BuildExtrudeRequest(sketch, "create", "positive", "finite", "solid", 0, 6, null);
                M3PlanSnapshot plan = _adapter.PrepareExtrude(_binding, request);
                Dictionary<string, object> before = DirectInventory(_part);
                List<Dictionary<string, object>> bodiesBefore = BodyRecords(_part);
                bool dirtyBefore = _part.IsModified;
                string revisionBefore = _binding.RevisionId;
                System.Reflection.FieldInfo hook = plan.AdapterState.GetType().GetField(
                    "ForcePostCommitVerificationFailureForTesting",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                Require(hook != null && hook.FieldType == typeof(bool), "Production M3 plan has no controlled post-commit test fault hook.");
                hook.SetValue(plan.AdapterState, true);
                row["fault_injection"] = new Dictionary<string, object>
                {
                    { "hook", hook.Name }, { "stage", "after native CommitFeature and UpdateManager.DoUpdate, before postcondition verification" },
                    { "test_only_private_plan_state", true }
                };
                M3ExecutionResult result = _adapter.ExecutePlan(_binding, plan);
                row["request"] = ExtrudeRequestRecord(request);
                row["execution"] = ExecutionRecord(result);
                Require(result != null && result.State == "ROLLED_BACK" && result.ErrorCode == "M3_OPERATION_ROLLED_BACK",
                    "Controlled post-commit fault did not return verified product rollback.");
                AssertRollbackEvidence(result.Result, row);
                Dictionary<string, object> after = DirectInventory(_part);
                List<Dictionary<string, object>> bodiesAfter = BodyRecords(_part);
                row["direct_inventory_before"] = before; row["direct_inventory_after"] = after;
                row["bodies_before"] = bodiesBefore; row["bodies_after"] = bodiesAfter;
                row["dirty_before"] = dirtyBefore; row["dirty_after"] = _part.IsModified;
                row["revision_before"] = revisionBefore; row["revision_after_undo"] = _binding.RevisionId;
                Require(String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal),
                    "Product post-commit Undo failed to restore the complete direct model inventory.");
                Dictionary<string, object> bodyRestoreComparison;
                bool bodiesRestored = TransactionBodyRecordsMatch(bodiesBefore, bodiesAfter, out bodyRestoreComparison);
                row["direct_body_restore_comparison"] = bodyRestoreComparison;
                Require(bodiesRestored,
                    "Product post-commit Undo failed to restore direct body/face/edge topology and measurements.");
                Require(dirtyBefore == _part.IsModified, "Product post-commit Undo failed to restore the part dirty state.");
                Require(_session.ActiveSketch == null, "Product post-commit Undo left an active Sketch.");
                row["case_result"] = "PASS";
            });
    }

    private static void RunInvalidTopologyReadOnlyCases()
    {
        RunCase("F12-N07-self-intersection-and-branch-read-only-reject", "F12 Prepare rejects malformed topology injected directly into an otherwise valid empty Sketch, without mutation.",
            "Each fixture starts from a product-created valid empty Sketch; malformed native Lines are added directly with NXOpen only for test setup, bypassing F06/F12 sketch creation. F12 Prepare returns UNSUPPORTED_PROFILE and before/after native feature, curve, body, dirty and revision snapshots match exactly.",
            delegate(Dictionary<string, object> row)
            {
                List<List<M3SketchGeometryRequest>> invalidProfiles = new List<List<M3SketchGeometryRequest>>
                {
                    new List<M3SketchGeometryRequest> {
                        Line("bowtie-1", 0, 0, 4, 4), Line("bowtie-2", 4, 4, 0, 4),
                        Line("bowtie-3", 0, 4, 4, 0), Line("bowtie-4", 4, 0, 0, 0)
                    },
                    new List<M3SketchGeometryRequest> {
                        Line("branch-1", 10, 0, 14, 0), Line("branch-2", 14, 0, 18, 0),
                        Line("branch-3", 14, 0, 14, 4)
                    }
                };
                string[] labels = new string[] { "self_intersection", "branch" };
                List<object> outcomes = new List<object>();
                List<object> fixtures = new List<object>();
                row["profile_rejections"] = outcomes;
                row["invalid_profile_fixture_setup"] = fixtures;
                for (int i = 0; i < invalidProfiles.Count; i++)
                {
                    Dictionary<string, object> fixtureRecord;
                    Sketch sketch = CreateDirectInvalidProfileSketch("invalid-" + labels[i], invalidProfiles[i],
                        170 + i * 20, row, out fixtureRecord);
                    fixtures.Add(fixtureRecord);
                    Persist();
                    Dictionary<string, object> before = CaptureExtrudeDirectSnapshot(_part);
                    M2ModelException observed = null;
                    try
                    {
                        _adapter.PrepareExtrude(_binding, BuildExtrudeRequest(sketch, "create", "positive", "finite", "solid", 0, 5, null));
                    }
                    catch (M2ModelException ex)
                    {
                        observed = ex;
                        if (String.Equals(ex.Code, "M3_ANALYSIS_STATE_UNKNOWN", StringComparison.Ordinal))
                            throw new UnknownOutcomeException("F12 malformed-profile analysis could not prove temporary Section cleanup; stop writes.");
                    }
                    Require(observed != null && observed.Code == "UNSUPPORTED_PROFILE",
                        labels[i] + " profile did not reject with UNSUPPORTED_PROFILE.");
                    Dictionary<string, object> after = CaptureExtrudeDirectSnapshot(_part);
                    bool inventoryUnchanged = String.Equals(M2Json.CanonicalText(before["direct_inventory"]),
                        M2Json.CanonicalText(after["direct_inventory"]), StringComparison.Ordinal);
                    bool curvesUnchanged = String.Equals(M2Json.CanonicalText(before["curve_records"]),
                        M2Json.CanonicalText(after["curve_records"]), StringComparison.Ordinal);
                    Dictionary<string, object> bodyComparison;
                    bool bodiesUnchanged = TransactionBodyRecordsMatch(
                        (List<Dictionary<string, object>>)before["body_records"],
                        (List<Dictionary<string, object>>)after["body_records"], out bodyComparison);
                    bool dirtyRevisionUnchanged = Object.Equals(before["part_modified"], after["part_modified"]) &&
                        Object.Equals(before["revision_id"], after["revision_id"]);
                    outcomes.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "profile", labels[i] }, { "error_code", observed.Code },
                        { "direct_fixture_setup", fixtureRecord },
                        { "direct_before_prepare", before }, { "direct_after_prepare", after },
                        { "direct_inventory_unchanged", inventoryUnchanged },
                        { "direct_curves_unchanged", curvesUnchanged }, { "direct_bodies_unchanged", bodiesUnchanged },
                        { "direct_body_comparison", bodyComparison },
                        { "dirty_revision_unchanged", dirtyRevisionUnchanged }
                    });
                    Persist();
                    Require(inventoryUnchanged,
                        labels[i] + " rejection changed direct model inventory.");
                    Require(curvesUnchanged && bodiesUnchanged,
                        labels[i] + " rejection changed direct Curve or Body geometry readback.");
                    Require(dirtyRevisionUnchanged,
                        labels[i] + " rejection changed dirty state or binding revision.");
                    Persist();
                }
                row["profile_rejections"] = outcomes;
                row["case_result"] = "PASS";
            });
    }

    private static M3ExtrudeRequest BuildExtrudeRequest(Sketch sketch, string mode, string direction, string extent,
        string profileOutput, double startOffset, double? endOffset, string targetBodyId)
    {
        M3ExtrudeRequest request = new M3ExtrudeRequest
        {
            RevisionId = _binding.RevisionId, SketchId = sketch.JournalIdentifier,
            Mode = mode, Direction = direction, Extent = extent,
            DepthMm = endOffset.HasValue ? endOffset.Value - startOffset : 0.0,
            TargetBodyId = targetBodyId
        };
        SetRequestField(request, "ProfileOutput", profileOutput);
        SetRequestField(request, "StartOffsetMm", startOffset);
        SetRequestField(request, "EndOffsetMm", endOffset.HasValue ? endOffset.Value : 0.0);
        return request;
    }

    private static void AssertRollbackEvidence(Dictionary<string, object> evidence, Dictionary<string, object> row)
    {
        Require(evidence != null, "Rollback result omitted product evidence.");
        foreach (string key in new string[] { "undo_called", "undo_update_error_count", "semantic_restored", "geometry_restored", "history_restored", "bodies_restored" })
            Require(evidence.ContainsKey(key), "Rollback result omitted required evidence " + key + ".");
        row["product_rollback_evidence"] = evidence;
        Require(Object.Equals(evidence["undo_called"], true), "Product rollback evidence does not confirm native UndoToMark.");
        Require(Convert.ToInt32(evidence["undo_update_error_count"], CultureInfo.InvariantCulture) == 0,
            "Product rollback Undo update reported errors.");
        foreach (string key in new string[] { "semantic_restored", "geometry_restored", "history_restored", "bodies_restored" })
            Require(Object.Equals(evidence[key], true), "Product rollback did not prove " + key + ".");
    }

    private static void RunSaveCopyCase(string copyPath)
    {
        RunCase("F12-N08-save-copy-reopen-roundtrip", "SaveCopy and reopen preserve both split solids, sheet type and feature inventory.",
            "SaveCopy returns SAVED/reopened=true with verified checks; direct reopened readback retains all body solid flags, exact boxes, face/edge signatures and measured volume/area.",
            delegate(Dictionary<string, object> row)
            {
                List<Dictionary<string, object>> before = BodyRecords(_part);
                M2SaveCopyResult saved = _adapter.SaveCopy(_binding, copyPath, true);
                row["save_copy"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "state", saved == null ? null : saved.State }, { "path", saved == null ? null : saved.PartPath },
                    { "reopened", saved != null && saved.Reopened }, { "checks", saved == null ? null : saved.Checks },
                    { "error_code", saved == null ? null : saved.ErrorCode }, { "error_message", saved == null ? null : saved.ErrorMessage }
                };
                Require(saved != null && saved.State == "SAVED" && saved.Reopened, "Production SaveCopy did not save and reopen the copy.");
                _part = _session.Parts.Work;
                List<Dictionary<string, object>> after = BodyRecords(_part);
                row["bodies_before"] = before;
                row["bodies_after_reopen"] = after;
                Require(before.Count == after.Count, "SaveCopy/reopen body count changed.");
                Dictionary<string, object> comparison = CompareBodyInventoriesWithinTolerance(before, after);
                row["body_roundtrip_comparison"] = comparison;
                Require(Object.Equals(comparison["matched"], true),
                    "SaveCopy/reopen direct body or one-to-one face/edge geometry differed beyond declared tolerances.");
                row["case_result"] = "PASS";
            });
    }

    private static Sketch CreateSketch(string suffix, List<M3SketchGeometryRequest> geometry,
        double[] originMm, Dictionary<string, object> row, double[] normal = null, double[] xAxis = null)
    {
        M3SketchRequest request = new M3SketchRequest
        {
            RevisionId = _binding.RevisionId, Plane = normal == null ? "XY" : null, OriginMm = originMm,
            Normal = normal, XAxis = xAxis, ConstraintMode = "geometry_only", Geometries = geometry
        };
        M3PlanSnapshot plan = _adapter.PrepareSketch(_binding, request);
        Require(plan != null && plan.Kind == "create_sketch", "PrepareSketch returned an invalid plan.");
        M3ExecutionResult execution = _adapter.ExecutePlan(_binding, plan);
        row["last_sketch_execution"] = ExecutionRecord(execution);
        if (execution == null || execution.State == "UNKNOWN") throw new UnknownOutcomeException("Sketch execution returned UNKNOWN: " + (execution == null ? "null" : execution.ErrorMessage));
        Require(execution.State == "APPLIED", "Sketch execution failed: " + (execution == null ? "null" : execution.ErrorCode + ": " + execution.ErrorMessage));
        M3VerificationResult verification = _adapter.VerifyPlan(_binding, plan);
        row["last_sketch_verification"] = VerificationRecord(verification);
        Require(verification != null && verification.Verified, "Production Sketch verification failed.");
        string sketchId = ReadString(execution.Result, "sketch_id");
        row["last_sketch_id"] = sketchId;
        row["last_sketch_request"] = SketchRequestRecord(request);
        return FindSketch(sketchId);
    }

    private static Sketch CreateDirectInvalidProfileSketch(string suffix,
        List<M3SketchGeometryRequest> invalidGeometry, double originX,
        Dictionary<string, object> row, out Dictionary<string, object> setupEvidence)
    {
        Sketch sketch = CreateSketch("empty-base-" + suffix, new List<M3SketchGeometryRequest>(),
            new double[] { originX, 0.0, 0.0 }, row);
        Session.UndoMarkId fixtureMark = _session.SetUndoMark(Session.MarkVisibility.Invisible,
            "F12 direct malformed Sketch fixture " + suffix);
        bool active = false;
        try
        {
            sketch.Activate(Sketch.ViewReorient.False);
            active = true;
            foreach (M3SketchGeometryRequest geometry in invalidGeometry)
            {
                Require(geometry != null && geometry.Type == "line" && geometry.StartMm != null && geometry.EndMm != null,
                    "F12 malformed-topology fixture supports only directly-created native Lines.");
                Line line = _part.Curves.CreateLine(
                    new Point3d(originX + geometry.StartMm[0], geometry.StartMm[1], 0.0),
                    new Point3d(originX + geometry.EndMm[0], geometry.EndMm[1], 0.0));
                sketch.AddGeometry(line, Sketch.InferConstraintsOption.InferNoConstraints);
            }
            sketch.Update();
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            active = false;
            int updateErrors = _session.UpdateManager.DoUpdate(fixtureMark);
            Require(updateErrors == 0, "NX update reported errors while directly preparing the malformed F12 fixture.");
            Dictionary<string, object> refreshed = _adapter.InspectPart(_binding);
            Curve[] directSketchCurves = sketch.GetAllGeometry().OfType<Curve>().ToArray();
            Require(directSketchCurves.Length == invalidGeometry.Count,
                "Directly-created malformed curves did not all enter the test Sketch.");
            setupEvidence = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "method", "product-created valid empty Sketch followed by direct NXOpen Part.Curves.CreateLine + Sketch.AddGeometry setup" },
                { "product_sketch_creation_tested", false },
                { "invalid_topology_under_test", true }, { "sketch_id", sketch.JournalIdentifier },
                { "sketch_curve_count", directSketchCurves.Length },
                { "direct_curve_records", directSketchCurves.Select(CurveRecord).Cast<object>().ToList() },
                { "part_modified_after_fixture_setup", _part.IsModified },
                { "revision_after_fixture_inspect", _binding.RevisionId }, { "inspect_part_readback", refreshed }
            };
            _session.DeleteUndoMark(fixtureMark, null);
            Persist();
            return sketch;
        }
        catch (Exception setupException)
        {
            if (active && _session.ActiveSketch != null && _session.ActiveSketch.Tag.Equals(sketch.Tag))
            {
                try { sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model); }
                catch { }
            }
            try
            {
                _session.UndoToMark(fixtureMark, null);
                int undoUpdateErrors = _session.UpdateManager.DoUpdate(fixtureMark);
                if (undoUpdateErrors != 0) throw new InvalidOperationException("fixture setup Undo update returned errors");
                _adapter.InspectPart(_binding);
            }
            catch (Exception undoException)
            {
                throw new UnknownOutcomeException("Direct malformed Sketch fixture setup failed and its own setup Undo could not be verified: " + undoException.Message);
            }
            throw new InvalidOperationException("Direct malformed Sketch fixture setup failed and was undone.", setupException);
        }
    }

    private static M3PlanSnapshot ExecuteExtrude(Sketch sketch, string mode, string direction, string extent,
        string profileOutput, double startOffset, double? endOffset, string targetBodyId,
        Dictionary<string, object> row)
    {
        object existingOperations;
        List<Dictionary<string, object>> operations = row.TryGetValue("extrude_operations", out existingOperations)
            ? existingOperations as List<Dictionary<string, object>> : null;
        if (operations == null)
        {
            operations = new List<Dictionary<string, object>>();
            row["extrude_operations"] = operations;
        }
        Dictionary<string, object> operation = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "sequence", operations.Count + 1 }, { "status", "RUNNING" },
            { "sketch_id", sketch == null ? null : sketch.JournalIdentifier },
            { "mode", mode }, { "direction", direction }, { "extent", extent },
            { "profile_output", profileOutput }, { "target_body_id", targetBodyId }
        };
        operations.Add(operation);
        Persist();

        M3ExtrudeRequest request = new M3ExtrudeRequest
        {
            RevisionId = _binding.RevisionId, SketchId = sketch.JournalIdentifier,
            Mode = mode, Direction = direction, Extent = extent,
            DepthMm = endOffset.HasValue ? endOffset.Value - startOffset : 0.0,
            TargetBodyId = targetBodyId
        };
        SetRequestField(request, "ProfileOutput", profileOutput);
        SetRequestField(request, "StartOffsetMm", startOffset);
        SetRequestField(request, "EndOffsetMm", endOffset.HasValue ? endOffset.Value : 0.0);
        Dictionary<string, object> requestRecord = ExtrudeRequestRecord(request);
        operation["request"] = requestRecord;
        row["prepare_request"] = requestRecord;
        Dictionary<string, object> beforePrepare = CaptureExtrudeDirectSnapshot(_part);
        operation["direct_before_prepare"] = beforePrepare;
        row["direct_inventory_before_prepare"] = beforePrepare["direct_inventory"];
        Persist();

        M3PlanSnapshot plan;
        try { plan = _adapter.PrepareExtrude(_binding, request); }
        catch (Exception exception)
        {
            operation["prepare_exception"] = exception.ToString();
            M2ModelException modelException = exception as M2ModelException;
            if (modelException != null && String.Equals(modelException.Code, "M3_ANALYSIS_STATE_UNKNOWN", StringComparison.Ordinal))
            {
                operation["status"] = "UNKNOWN";
                Persist();
                throw new UnknownOutcomeException("Extrude prepare could not prove temporary NX Section cleanup; do not continue or retry.");
            }
            try
            {
                Dictionary<string, object> afterPrepareFailure = CaptureExtrudeDirectSnapshot(_part);
                operation["direct_after_prepare_failure"] = afterPrepareFailure;
                bool readOnlyFailure = String.Equals(M2Json.CanonicalText(beforePrepare["direct_inventory"]),
                        M2Json.CanonicalText(afterPrepareFailure["direct_inventory"]), StringComparison.Ordinal) &&
                    Object.Equals(beforePrepare["part_modified"], afterPrepareFailure["part_modified"]) &&
                    Object.Equals(beforePrepare["revision_id"], afterPrepareFailure["revision_id"]);
                operation["prepare_failure_state"] = readOnlyFailure ? "DIRECT_STATE_UNCHANGED" : "STATE_CHANGED_OR_UNKNOWN";
                operation["status"] = readOnlyFailure ? "PREPARE_FAILED" : "UNKNOWN";
                Persist();
                if (!readOnlyFailure)
                    throw new UnknownOutcomeException("Extrude prepare failed and direct state did not prove read-only behavior; stop writes.");
            }
            catch (UnknownOutcomeException) { throw; }
            catch (Exception readException)
            {
                operation["post_prepare_failure_read_exception"] = readException.ToString();
                operation["status"] = "UNKNOWN";
                Persist();
                throw new UnknownOutcomeException("Extrude prepare failed and post-failure direct state could not be read; stop writes.");
            }
            throw;
        }

        Dictionary<string, object> afterPrepare = CaptureExtrudeDirectSnapshot(_part);
        operation["plan_public_view"] = plan == null ? null : plan.PublicView;
        operation["plan_feature_id"] = plan == null ? null : plan.FeatureId;
        operation["direct_after_prepare"] = afterPrepare;
        row["prepare_public_view"] = plan == null ? null : plan.PublicView;
        row["direct_inventory_after_prepare"] = afterPrepare["direct_inventory"];
        if (plan == null || plan.PublicView == null || plan.AdapterState == null)
        {
            operation["prepare_state"] = "FAILED_INCOMPLETE_PLAN";
            operation["status"] = "PREPARE_FAILED";
            Persist();
            throw new InvalidOperationException("PrepareExtrude returned an incomplete M3PlanSnapshot.");
        }
        operation["prepare_state"] = "PREPARED";
        Persist();
        bool prepareInventoryUnchanged = String.Equals(M2Json.CanonicalText(beforePrepare["direct_inventory"]),
            M2Json.CanonicalText(afterPrepare["direct_inventory"]), StringComparison.Ordinal);
        bool prepareStateUnchanged = Object.Equals(beforePrepare["part_modified"], afterPrepare["part_modified"]) &&
            Object.Equals(beforePrepare["revision_id"], afterPrepare["revision_id"]);
        operation["prepare_direct_inventory_unchanged"] = prepareInventoryUnchanged;
        operation["prepare_dirty_revision_unchanged"] = prepareStateUnchanged;
        if (!prepareInventoryUnchanged || !prepareStateUnchanged)
        {
            operation["status"] = "PREPARE_MUTATION_DETECTED";
            Persist();
        }
        Require(prepareInventoryUnchanged,
            "PrepareExtrude changed direct part inventory.");
        Require(prepareStateUnchanged,
            "PrepareExtrude changed dirty state or binding revision.");

        Dictionary<string, object> beforeExecute = CaptureExtrudeDirectSnapshot(_part);
        operation["direct_before_execute"] = beforeExecute;
        Persist();
        M3ExecutionResult execution;
        try { execution = _adapter.ExecutePlan(_binding, plan); }
        catch (Exception exception)
        {
            operation["execution_exception"] = exception.ToString();
            operation["status"] = "UNKNOWN";
            try { operation["direct_after_execute_exception"] = CaptureExtrudeDirectSnapshot(_part); }
            catch (Exception readException) { operation["post_execute_exception_read_error"] = readException.ToString(); }
            Persist();
            throw new UnknownOutcomeException("Extrude execution threw before a verifiable final state; do not retry.");
        }
        operation["execution"] = ExecutionRecord(execution);
        row["extrude_execution"] = ExecutionRecord(execution);
        Dictionary<string, object> afterExecute = CaptureExtrudeDirectSnapshot(_part);
        operation["direct_after_execute"] = afterExecute;
        Persist();
        if (execution == null || execution.State == "UNKNOWN")
        {
            operation["status"] = "UNKNOWN";
            Persist();
            throw new UnknownOutcomeException("Extrude execution returned UNKNOWN; do not retry: " + (execution == null ? "null" : execution.ErrorMessage));
        }
        if (execution.State != "APPLIED")
        {
            operation["status"] = "EXECUTE_NOT_APPLIED";
            Persist();
        }
        Require(execution.State == "APPLIED", "Extrude execution did not return APPLIED (" + execution.ErrorCode + ": " + execution.ErrorMessage + ").");
        plan.FeatureId = ReadString(execution.Result, "feature_id");
        object bodyId;
        if (execution.Result != null && execution.Result.TryGetValue("body_id", out bodyId))
            plan.BodyId = Convert.ToString(bodyId, CultureInfo.InvariantCulture);
        operation["feature_id"] = plan.FeatureId;
        operation["body_id"] = plan.BodyId;
        Dictionary<string, object> beforeVerify = CaptureExtrudeDirectSnapshot(_part);
        operation["direct_before_verify"] = beforeVerify;
        Persist();
        M3VerificationResult verification;
        try { verification = _adapter.VerifyPlan(_binding, plan); }
        catch (Exception exception)
        {
            operation["verification_exception"] = exception.ToString();
            operation["status"] = "VERIFY_EXCEPTION";
            try { operation["direct_after_verify_exception"] = CaptureExtrudeDirectSnapshot(_part); }
            catch (Exception readException) { operation["post_verify_exception_read_error"] = readException.ToString(); }
            Persist();
            throw;
        }
        operation["verification"] = VerificationRecord(verification);
        row["extrude_verification"] = VerificationRecord(verification);
        Dictionary<string, object> afterVerify = CaptureExtrudeDirectSnapshot(_part);
        operation["direct_after_verify"] = afterVerify;
        operation["status"] = verification != null && verification.Verified ? "COMPLETE" : "VERIFY_FAILED";
        Persist();
        Require(verification != null && verification.Verified, "Production VerifyPlan did not verify the Extrude.");
        row["feature_id"] = plan.FeatureId;
        return plan;
    }

    private static Dictionary<string, object> CaptureExtrudeDirectSnapshot(Part part)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "direct_inventory", DirectInventory(part) }, { "curve_records", PartCurveRecords(part) },
            { "body_records", BodyRecords(part) },
            { "part_modified", part.IsModified }, { "revision_id", _binding.RevisionId }
        };
    }

    private static List<Dictionary<string, object>> PartCurveRecords(Part part)
    {
        List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();
        foreach (Curve curve in part.Curves.ToArray())
            if (curve != null) records.Add(CurveRecord(curve));
        records.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        {
            return String.CompareOrdinal(Convert.ToString(left["tag"], CultureInfo.InvariantCulture),
                Convert.ToString(right["tag"], CultureInfo.InvariantCulture));
        });
        return records;
    }

    private static Dictionary<string, object> ReadSectionAndMatch(Sketch sketch, M3PlanSnapshot plan,
        int expectedOutputCount, int expectedReferenceCount, Dictionary<string, object> row)
    {
        Feature feature = FindFeature(plan.FeatureId);
        ExtrudeBuilder builder = _part.Features.CreateExtrudeBuilder(feature);
        try
        {
            Require(builder.Section != null, "Committed Extrude has no Section.");
            NXObject[] sectionOutputs;
            builder.Section.GetOutputCurves(out sectionOutputs);
            Require(sectionOutputs != null, "Section.GetOutputCurves returned null.");
            Curve[] sketchCurves = sketch.GetAllGeometry().OfType<Curve>().ToArray();
            Curve[] active = sketchCurves.Where(curve => ReferenceStatus(sketch, curve) == 1).ToArray();
            Curve[] references = sketchCurves.Where(curve => ReferenceStatus(sketch, curve) == 0).ToArray();
            Require(references.Length == expectedReferenceCount, "Fixture reference-curve count differs from expectation.");
            Require(active.Length == expectedOutputCount, "Active whole-sketch source-curve count differs from expected Section domain.");
            Curve[] outputs = sectionOutputs.OfType<Curve>().ToArray();
            Require(outputs.Length == expectedOutputCount, "Committed Section output count is incorrect.");
            List<string> unmatched = outputs.Select(GeometrySignature).ToList();
            List<object> sourceRecords = new List<object>();
            foreach (Curve source in active)
            {
                string signature = GeometrySignature(source);
                int index = unmatched.FindIndex(value => String.Equals(value, signature, StringComparison.Ordinal));
                Require(index >= 0, "Section output has no one-to-one exact-domain geometric match for active source " + source.JournalIdentifier + ".");
                unmatched.RemoveAt(index);
                sourceRecords.Add(CurveRecord(source));
            }
            Require(unmatched.Count == 0, "Section contains one or more curves outside the nonreference whole-sketch domain.");
            foreach (Curve reference in references)
                Require(!outputs.Any(output => String.Equals(GeometrySignature(output), GeometrySignature(reference), StringComparison.Ordinal)),
                    "Reference curve geometry entered the Extrude Section domain.");
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "allow_reference_curves", false }, { "source_curve_count", sketchCurves.Length },
                { "active_source_curve_count", active.Length }, { "reference_curve_count", references.Length },
                { "section_output_curve_count", outputs.Length }, { "source_active_geometries", sourceRecords },
                { "section_output_geometries", outputs.Select(CurveRecord).Cast<object>().ToList() },
                { "one_to_one_geometry_match", true }
            };
        }
        finally { builder.Destroy(); }
    }

    private static Dictionary<string, object> ExtrudeReadback(Part part, string featureId)
    {
        Feature feature = FindFeature(featureId);
        ExtrudeBuilder builder = part.Features.CreateExtrudeBuilder(feature);
        try
        {
            Body[] targets = builder.BooleanOperation.GetTargetBodies();
            Direction direction = builder.Direction;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "feature_id", feature.JournalIdentifier }, { "feature_type", feature.FeatureType },
                { "output_body_solid_flags", feature.GetBodies().Select(body => body.IsSolidBody).ToArray() },
                { "direction_vector", direction == null ? null : Vector(direction.Vector) },
                { "start_trim_type", builder.Limits.StartExtend.TrimType.ToString() },
                { "start_value_mm", builder.Limits.StartExtend.Value.Value },
                { "end_trim_type", builder.Limits.EndExtend.TrimType.ToString() },
                { "end_value_mm", builder.Limits.EndExtend.TrimType == NXOpen.GeometricUtilities.Extend.ExtendType.ThroughAll
                    ? (object)null : builder.Limits.EndExtend.Value.Value },
                { "boolean_type", builder.BooleanOperation.Type.ToString() },
                { "target_body_ids", targets == null ? new string[0] : targets.Select(body => body.JournalIdentifier).OrderBy(id => id, StringComparer.Ordinal).ToArray() }
            };
        }
        finally { builder.Destroy(); }
    }

    private static List<Dictionary<string, object>> BodyRecords(Part part)
    {
        List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
        foreach (Body body in part.Bodies.ToArray()) result.Add(BodyRecord(body));
        result.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        { return String.CompareOrdinal(Convert.ToString(left["journal_id"], CultureInfo.InvariantCulture), Convert.ToString(right["journal_id"], CultureInfo.InvariantCulture)); });
        return result;
    }

    private static List<Dictionary<string, object>> FeatureBodyRecords(string featureId)
    {
        List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
        foreach (Body body in FeatureBodies(featureId)) result.Add(BodyRecord(body));
        result.Sort(delegate(Dictionary<string, object> left, Dictionary<string, object> right)
        { return String.CompareOrdinal(Convert.ToString(left["journal_id"], CultureInfo.InvariantCulture), Convert.ToString(right["journal_id"], CultureInfo.InvariantCulture)); });
        return result;
    }

    private static Dictionary<string, object> BodyRecord(Body body)
    {
        Require(body != null, "Direct Part.Bodies inventory contains null.");
        List<object> faces = new List<object>();
        foreach (Face face in body.GetFaces())
        {
            int type, normalDirection;
            double[] point = new double[3], normal = new double[3], box = new double[6];
            double radius, radiusData;
            UFSession.GetUFSession().Modl.AskFaceData(face.Tag, out type, point, normal, box, out radius, out radiusData, out normalDirection);
            faces.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", face.Tag.ToString() }, { "journal_id", face.JournalIdentifier }, { "face_type", type },
                { "point_mm", point }, { "normal", normal }, { "face_box_mm", box },
                { "radius_mm", radius }, { "radius_data", radiusData }, { "normal_direction", normalDirection }
            });
        }
        faces.Sort(delegate(object left, object right)
        {
            return String.CompareOrdinal(
                Convert.ToString(((Dictionary<string, object>)left)["journal_id"], CultureInfo.InvariantCulture),
                Convert.ToString(((Dictionary<string, object>)right)["journal_id"], CultureInfo.InvariantCulture));
        });
        List<object> edges = new List<object>();
        foreach (Edge edge in body.GetEdges())
        {
            Point3d first, second;
            edge.GetVertices(out first, out second);
            if (String.CompareOrdinal(VectorKey(Point(first)), VectorKey(Point(second))) > 0)
            { Point3d temporary = first; first = second; second = temporary; }
            edges.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", edge.Tag.ToString() }, { "journal_id", edge.JournalIdentifier },
                { "edge_type", edge.SolidEdgeType.ToString() }, { "length_mm", edge.GetLength() },
                { "vertices_mm", new double[][] { Point(first), Point(second) } }
            });
        }
        edges.Sort(delegate(object left, object right)
        {
            return String.CompareOrdinal(
                Convert.ToString(((Dictionary<string, object>)left)["journal_id"], CultureInfo.InvariantCulture),
                Convert.ToString(((Dictionary<string, object>)right)["journal_id"], CultureInfo.InvariantCulture));
        });
        double[] boxExact = ExactBox(body.Tag);
        double? volume = null;
        double? area = null;
        double? massAreaDiagnostic = null;
        Unit[] units = new Unit[] {
            _part.UnitCollection.FindObject("Kilogram"), _part.UnitCollection.FindObject("MilliMeter"),
            _part.UnitCollection.FindObject("SquareMilliMeter"), _part.UnitCollection.FindObject("CubicMilliMeter")
        };
        MeasureBodies measure = null;
        try
        {
            measure = _part.MeasureManager.NewMassProperties(units, DirectBodyMassPropertiesAccuracy, new Body[] { body });
            if (body.IsSolidBody) volume = measure.Volume;
            massAreaDiagnostic = measure.Area;
            if (body.IsSolidBody) area = massAreaDiagnostic;
        }
        finally { if (measure != null) measure.Dispose(); }
        if (!body.IsSolidBody)
        {
            Dictionary<string, object> directFaces = MeasureDirectFaceAreas(new Body[] { body });
            area = Convert.ToDouble(directFaces["total_area_mm2"], CultureInfo.InvariantCulture);
        }
        int faultCount; int[] faultTypes; Tag[] faultTags;
        UFSession.GetUFSession().Modl.AskBodyConsistency(body.Tag, out faultCount, out faultTypes, out faultTags);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", body.Tag.ToString() }, { "journal_id", body.JournalIdentifier },
            { "is_solid", body.IsSolidBody }, { "face_count", faces.Count }, { "edge_count", edges.Count },
            { "faces", faces }, { "edges", edges }, { "exact_bbox_mm", boxExact },
            { "mass_properties_accuracy", DirectBodyMassPropertiesAccuracy },
            { "area_measurement_accuracy", DirectBodyMassPropertiesAccuracy },
            { "area_measurement_method", body.IsSolidBody ? "MeasureManager.NewMassProperties.Area" : "sum_direct_face_NewFaceProperties" },
            { "volume_mm3", volume.HasValue ? (object)volume.Value : null },
            { "area_mm2", area.HasValue ? (object)area.Value : null },
            { "mass_area_diagnostic_mm2", massAreaDiagnostic.HasValue ? (object)massAreaDiagnostic.Value : null },
            { "consistency_fault_count", faultCount }
        };
    }

    private static Dictionary<string, object> MeasureBodyProperties(Body body, double accuracy)
    {
        Require(body != null, "Cannot measure properties for a null body.");
        Unit[] units = new Unit[] {
            _part.UnitCollection.FindObject("Kilogram"), _part.UnitCollection.FindObject("MilliMeter"),
            _part.UnitCollection.FindObject("SquareMilliMeter"), _part.UnitCollection.FindObject("CubicMilliMeter")
        };
        MeasureBodies measure = null;
        try
        {
            measure = _part.MeasureManager.NewMassProperties(units, accuracy, new Body[] { body });
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "accuracy", accuracy }, { "volume_mm3", body.IsSolidBody ? (object)measure.Volume : null },
                { "area_mm2", measure.Area }
            };
        }
        finally { if (measure != null) measure.Dispose(); }
    }

    private static Dictionary<string, object> MeasureDirectFaceAreas(Body[] bodies)
    {
        Require(bodies != null && bodies.Length > 0, "Cannot measure sheet area without direct feature output bodies.");
        Unit areaUnit = _part.UnitCollection.FindObject("SquareMilliMeter");
        Unit lengthUnit = _part.UnitCollection.FindObject("MilliMeter");
        List<object> faceMeasurements = new List<object>();
        double total = 0.0;
        foreach (Body body in bodies)
        {
            Require(body != null, "Direct sheet output inventory contains a null Body.");
            Face[] faces = body.GetFaces();
            Require(faces != null && faces.Length > 0, "Direct sheet output Body contains no Faces.");
            foreach (Face face in faces)
            {
                MeasureFaces measured = null;
                try
                {
                    measured = _part.MeasureManager.NewFaceProperties(areaUnit, lengthUnit,
                        DirectBodyMassPropertiesAccuracy, new IParameterizedSurface[] { face });
                    double area = measured.Area;
                    Require(!Double.IsNaN(area) && !Double.IsInfinity(area) && area > 0.0,
                        "Direct NewFaceProperties returned a non-finite or non-positive sheet-face area.");
                    total += area;
                    faceMeasurements.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "body_tag", body.Tag.ToString() }, { "body_journal_id", body.JournalIdentifier },
                        { "face_tag", face.Tag.ToString() }, { "face_journal_id", face.JournalIdentifier },
                        { "accuracy", DirectBodyMassPropertiesAccuracy }, { "area_mm2", area }
                    });
                }
                finally { if (measured != null) measured.Dispose(); }
            }
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "measurement_method", "MeasureManager.NewFaceProperties over direct feature-output Body.GetFaces()" },
            { "accuracy", DirectBodyMassPropertiesAccuracy }, { "face_count", faceMeasurements.Count },
            { "faces", faceMeasurements }, { "total_area_mm2", total }
        };
    }

    private static Dictionary<string, object> VerifyUnrelatedFreeCurveUnchanged(Part part, Sketch sourceSketch,
        Curve beforeCurve, Dictionary<string, object> beforeRecord)
    {
        Require(part != null && sourceSketch != null && beforeCurve != null && beforeRecord != null,
            "Unrelated free-Curve verification inputs are incomplete.");
        string tag = beforeCurve.Tag.ToString();
        Curve afterCurve = part.Curves.ToArray().FirstOrDefault(curve => curve != null &&
            String.Equals(curve.Tag.ToString(), tag, StringComparison.Ordinal));
        Require(afterCurve != null, "The unrelated free Curve disappeared from direct Part.Curves inventory.");
        Dictionary<string, object> afterRecord = CurveRecord(afterCurve);
        bool sameJournalIdentifier = String.Equals(
            Convert.ToString(beforeRecord["journal_id"], CultureInfo.InvariantCulture), afterCurve.JournalIdentifier,
            StringComparison.Ordinal);
        Dictionary<string, object> beforeGeometry = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object> item in beforeRecord)
            if (!String.Equals(item.Key, "tag", StringComparison.Ordinal) &&
                !String.Equals(item.Key, "journal_id", StringComparison.Ordinal) &&
                !String.Equals(item.Key, "native_type", StringComparison.Ordinal))
                beforeGeometry[item.Key] = item.Value;
        bool sameGeometry = String.Equals(M2Json.CanonicalText(beforeGeometry),
            M2Json.CanonicalText(CurveGeometryRecord(afterCurve)), StringComparison.Ordinal);
        bool wasAddedToSketch = sourceSketch.GetAllGeometry().OfType<Curve>().Any(curve =>
            String.Equals(curve.Tag.ToString(), tag, StringComparison.Ordinal));
        Require(String.Equals(Convert.ToString(beforeRecord["tag"], CultureInfo.InvariantCulture), tag,
                StringComparison.Ordinal), "Recorded free-Curve Tag changed before post-operation comparison.");
        Require(sameJournalIdentifier, "The unrelated free Curve JournalIdentifier changed across Extrude.");
        Require(sameGeometry, "The unrelated free Curve geometry changed across Extrude.");
        Require(!wasAddedToSketch, "The unrelated free Curve became associated with the source Sketch.");
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "status", "UNCHANGED" }, { "same_tag", true }, { "same_journal_identifier", sameJournalIdentifier },
            { "same_geometry", sameGeometry }, { "is_source_sketch_member", wasAddedToSketch },
            { "before", beforeRecord }, { "after", afterRecord }
        };
    }

    private static double[] ExactBox(Tag tag)
    {
        double[] corner = new double[3], distances = new double[3];
        double[,] directions = new double[3, 3];
        UFSession.GetUFSession().Modl.AskBoundingBoxExact(tag, Tag.Null, corner, directions, distances);
        double[] box = new double[] { corner[0], corner[1], corner[2], corner[0], corner[1], corner[2] };
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
        {
            double delta = directions[i, j] * distances[i];
            if (delta < 0) box[j] += delta; else box[j + 3] += delta;
        }
        return box;
    }

    private static Dictionary<string, object> DirectInventory(Part part)
    {
        List<string> features = part.Features.GetFeatures().Select(feature => feature.Tag.ToString()).OrderBy(value => value, StringComparer.Ordinal).ToList();
        List<string> sketches = part.Sketches.ToArray().Select(sketch => sketch.Tag.ToString()).OrderBy(value => value, StringComparer.Ordinal).ToList();
        List<string> curves = part.Curves.ToArray().Select(curve => curve.Tag.ToString()).OrderBy(value => value, StringComparer.Ordinal).ToList();
        List<string> bodies = part.Bodies.ToArray().Select(body => body.Tag.ToString()).OrderBy(value => value, StringComparer.Ordinal).ToList();
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "features", features }, { "sketches", sketches }, { "curves", curves }, { "bodies", bodies },
            { "part_modified", part.IsModified }, { "active_sketch", _session.ActiveSketch == null ? null : _session.ActiveSketch.Tag.ToString() }
        };
    }

    private static List<M3SketchGeometryRequest> MultiRegionGeometry()
    {
        List<M3SketchGeometryRequest> geometry = Rectangle("outer", 0, 0, 20, 20);
        geometry.Add(new M3SketchGeometryRequest { Id = "hole", Type = "circle", CenterMm = new double[] { 8, 6 }, DiameterMm = 4 });
        geometry.AddRange(Rectangle("island", 30, 0, 40, 10));
        geometry.Add(Line("reference", 50, 0, 60, 0));
        return geometry;
    }

    private static double[] TiltedNegativeDirectionBounds()
    {
        double s = 1.0 / Math.Sqrt(2.0);
        double[] origin = new double[] { 100.0, 0.0, 0.0 };
        double[] normal = new double[] { 0.0, s, s };
        double[] xAxis = new double[] { 1.0, 0.0, 0.0 };
        double[] yAxis = new double[] { 0.0, s, -s };
        double[] min = new double[] { Double.PositiveInfinity, Double.PositiveInfinity, Double.PositiveInfinity };
        double[] max = new double[] { Double.NegativeInfinity, Double.NegativeInfinity, Double.NegativeInfinity };
        foreach (double x in new double[] { 0.0, 4.0 })
            foreach (double y in new double[] { 0.0, 3.0 })
                foreach (double offset in new double[] { -2.0, 8.0 })
                {
                    double[] point = new double[3];
                    for (int axis = 0; axis < 3; axis++)
                    {
                        point[axis] = origin[axis] + x * xAxis[axis] + y * yAxis[axis] - offset * normal[axis];
                        min[axis] = Math.Min(min[axis], point[axis]);
                        max[axis] = Math.Max(max[axis], point[axis]);
                    }
                }
        return new double[] { min[0], min[1], min[2], max[0], max[1], max[2] };
    }

    private static List<M3SketchGeometryRequest> Rectangle(string prefix, double x0, double y0, double x1, double y1)
    {
        return new List<M3SketchGeometryRequest>
        {
            Line(prefix + "-bottom", x0, y0, x1, y0), Line(prefix + "-right", x1, y0, x1, y1),
            Line(prefix + "-top", x1, y1, x0, y1), Line(prefix + "-left", x0, y1, x0, y0)
        };
    }

    private static M3SketchGeometryRequest Line(string id, double x0, double y0, double x1, double y1)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "line", StartMm = new double[] { x0, y0 }, EndMm = new double[] { x1, y1 } };
    }

    private static string GeometrySignature(Curve curve)
    {
        return M2Json.CanonicalText(RoundGeometryValue(CurveGeometryRecord(curve)));
    }

    private static object RoundGeometryValue(object value)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> item in dictionary) result[item.Key] = RoundGeometryValue(item.Value);
            return result;
        }
        double[] vector = value as double[];
        if (vector != null) return vector.Select(item => Math.Round(item, 7)).ToArray();
        double[][] matrix = value as double[][];
        if (matrix != null) return matrix.Select(row => row.Select(item => Math.Round(item, 7)).ToArray()).Cast<object>().ToList();
        List<double[]> vectorList = value as List<double[]>;
        if (vectorList != null) return vectorList.Select(item => item.Select(component => Math.Round(component, 7)).ToArray()).Cast<object>().ToList();
        List<object> list = value as List<object>;
        if (list != null) return list.Select(RoundGeometryValue).ToList();
        if (value is double) return Math.Round((double)value, 7);
        return value;
    }

    private static Dictionary<string, object> CurveRecord(Curve curve)
    {
        Dictionary<string, object> record = CurveGeometryRecord(curve);
        record["tag"] = curve.Tag.ToString();
        record["journal_id"] = curve.JournalIdentifier;
        record["native_type"] = curve.GetType().Name;
        return record;
    }

    private static Dictionary<string, object> CurveGeometryRecord(Curve curve)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal);
        Line line = curve as Line;
        if (line != null)
        {
            double[][] endpoints = new double[][] { Point(line.StartPoint), Point(line.EndPoint) };
            Array.Sort(endpoints, delegate(double[] left, double[] right) { return String.CompareOrdinal(VectorKey(left), VectorKey(right)); });
            record["curve_type"] = "Line"; record["endpoints_mm"] = endpoints; record["length_mm"] = Distance(line.StartPoint, line.EndPoint);
            return record;
        }
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center; Vector3d xAxis, yAxis;
            arc.GetOrientation(out center, out xAxis, out yAxis);
            record["curve_type"] = "Arc"; record["center_mm"] = Point(arc.CenterPoint); record["radius_mm"] = arc.Radius;
            record["orientation_x"] = Vector(xAxis); record["orientation_y"] = Vector(yAxis);
            record["start_angle_rad"] = arc.StartAngle; record["end_angle_rad"] = arc.EndAngle;
            record["sweep_rad"] = arc.EndAngle - arc.StartAngle; record["sample_points_mm"] = CurveSamples(arc);
            return record;
        }
        Ellipse ellipse = curve as Ellipse;
        if (ellipse != null)
        {
            double major, minor, start, end, rotation; Point3d center; Vector3d xAxis, yAxis;
            ellipse.GetParameters(out major, out minor, out start, out end, out rotation, out center);
            ellipse.GetOrientation(out center, out xAxis, out yAxis);
            record["curve_type"] = "Ellipse"; record["center_mm"] = Point(center);
            record["major_radius_mm"] = major; record["minor_radius_mm"] = minor; record["rotation_rad"] = rotation;
            record["native_start_angle_rad"] = start; record["native_end_angle_rad"] = end;
            record["orientation_x"] = Vector(xAxis); record["orientation_y"] = Vector(yAxis);
            record["sample_points_mm"] = CurveSamples(ellipse);
            return record;
        }
        Spline spline = curve as Spline;
        if (spline != null)
        {
            record["curve_type"] = "Spline"; record["sample_points_mm"] = CurveSamples(spline);
            UFCurve.Spline definition; UFSession.GetUFSession().Curve.AskSplineData(spline.Tag, out definition);
            record["degree"] = definition.order - 1; record["order"] = definition.order;
            bool rational = definition.is_rational != 0;
            record["pole_count"] = definition.num_poles; record["knots"] = definition.knots;
            record["rational"] = rational;
            record["pole_coordinate_encoding"] = rational ? "HOMOGENEOUS_XYZ_W" : "CARTESIAN_XYZ";
            record["native_poles"] = NativeSplinePoleCoordinates(definition);
            record["start_parameter"] = definition.start_param;
            record["end_parameter"] = definition.end_param;
            record["native_parameter_domain"] = new double[] { definition.start_param, definition.end_param };
            record["poles_mm"] = NativeSplinePoles(definition);
            return record;
        }
        throw new InvalidOperationException("Unsupported direct curve class in F12 evidence: " + curve.GetType().FullName);
    }

    private static double[][] CurveSamples(Curve curve)
    {
        UFSession uf = UFSession.GetUFSession();
        IntPtr evaluator = IntPtr.Zero;
        try
        {
            uf.Eval.Initialize(curve.Tag, out evaluator);
            double[] limits = new double[2]; uf.Eval.AskLimits(evaluator, limits);
            double[][] result = new double[5][];
            for (int i = 0; i < 5; i++)
            {
                double parameter = limits[0] + (limits[1] - limits[0]) * i / 4.0;
                double[] position = new double[3]; uf.Curve.EvaluateCurve(curve.Tag, parameter, 0, position);
                result[i] = position;
            }
            return result;
        }
        finally { if (evaluator != IntPtr.Zero) uf.Eval.Free(evaluator); }
    }

    private static List<double[]> NativeSplinePoles(UFCurve.Spline definition)
    {
        List<double[]> result = new List<double[]>();
        if (definition.poles != null)
            for (int i = 0; i < definition.poles.GetLength(0); i++)
                result.Add(new double[] { definition.poles[i, 0], definition.poles[i, 1], definition.poles[i, 2] });
        return result;
    }

    private static List<double[]> NativeSplinePoleCoordinates(UFCurve.Spline definition)
    {
        List<double[]> result = new List<double[]>();
        if (definition.poles == null) return result;
        int dimension = definition.poles.GetLength(1);
        for (int i = 0; i < definition.poles.GetLength(0); i++)
        {
            double[] pole = new double[dimension];
            for (int component = 0; component < dimension; component++) pole[component] = definition.poles[i, component];
            result.Add(pole);
        }
        return result;
    }

    private static double SampledCurveArea(Curve curve, int segments)
    {
        if (segments < 32) throw new ArgumentOutOfRangeException("segments");
        UFSession uf = UFSession.GetUFSession(); IntPtr evaluator = IntPtr.Zero;
        try
        {
            uf.Eval.Initialize(curve.Tag, out evaluator);
            double[] limits = new double[2]; uf.Eval.AskLimits(evaluator, limits);
            double[][] points = new double[segments + 1][];
            for (int i = 0; i <= segments; i++)
            {
                double parameter = limits[0] + (limits[1] - limits[0]) * i / segments;
                double[] position = new double[3]; uf.Curve.EvaluateCurve(curve.Tag, parameter, 0, position);
                points[i] = position;
            }
            Require(Distance(new Point3d(points[0][0], points[0][1], points[0][2]),
                new Point3d(points[segments][0], points[segments][1], points[segments][2])) <= 1.0e-6,
                "Closed spline endpoints differ by more than 1e-6 mm.");
            double area2 = 0;
            for (int i = 0; i < segments; i++) area2 += points[i][0] * points[i + 1][1] - points[i + 1][0] * points[i][1];
            return Math.Abs(area2) * 0.5;
        }
        finally { if (evaluator != IntPtr.Zero) uf.Eval.Free(evaluator); }
    }

    private static int ReferenceStatus(Sketch sketch, Curve curve)
    {
        UFSket.ReferenceStatus status;
        UFSession.GetUFSession().Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status);
        return Convert.ToInt32(status, CultureInfo.InvariantCulture);
    }

    private static Line FindLine(Sketch sketch, double x0, double y0, double x1, double y1)
    {
        foreach (Line line in sketch.GetAllGeometry().OfType<Line>())
        {
            Point3d a = line.StartPoint, b = line.EndPoint;
            if ((Near(a.X, x0) && Near(a.Y, y0) && Near(b.X, x1) && Near(b.Y, y1)) ||
                (Near(a.X, x1) && Near(a.Y, y1) && Near(b.X, x0) && Near(b.Y, y0))) return line;
        }
        throw new InvalidOperationException("Could not directly resolve fixture line endpoints.");
    }

    private static Sketch FindSketch(string id)
    {
        foreach (Sketch sketch in _part.Sketches.ToArray()) if (sketch.JournalIdentifier == id) return sketch;
        throw new InvalidOperationException("Sketch JID not found: " + id);
    }

    private static Feature FindFeature(string id)
    {
        foreach (Feature feature in _part.Features.GetFeatures()) if (feature.JournalIdentifier == id) return feature;
        throw new InvalidOperationException("Feature JID not found: " + id);
    }

    private static Body FindBodyByJournalId(string id)
    {
        foreach (Body body in _part.Bodies.ToArray()) if (body.JournalIdentifier == id) return body;
        return null;
    }

    private static Body[] FeatureBodies(string featureId)
    {
        Body[] bodies = FindFeature(featureId).GetBodies();
        Require(bodies != null && bodies.Length > 0, "Feature.GetBodies returned no direct body outputs for " + featureId + ".");
        return bodies;
    }

    private static void RequireBboxExists(List<Dictionary<string, object>> bodies, double[] expected)
    {
        foreach (Dictionary<string, object> body in bodies)
        {
            double[] actual = (double[])body["exact_bbox_mm"];
            bool match = true;
            for (int i = 0; i < 6; i++) if (Math.Abs(actual[i] - expected[i]) > PositionToleranceMm) match = false;
            if (match) return;
        }
        throw new InvalidOperationException("No direct NX body exact bounding box matched " + VectorKey(expected) + ".");
    }

    private static double[] AggregateBounds(List<Dictionary<string, object>> bodies)
    {
        Require(bodies != null && bodies.Count > 0, "Cannot aggregate exact bounds for an empty body set.");
        double[] aggregate = new double[] {
            Double.PositiveInfinity, Double.PositiveInfinity, Double.PositiveInfinity,
            Double.NegativeInfinity, Double.NegativeInfinity, Double.NegativeInfinity
        };
        foreach (Dictionary<string, object> body in bodies)
        {
            object raw = null;
            Require(body != null && body.TryGetValue("exact_bbox_mm", out raw) && raw is double[],
                "A direct body record is missing its exact world-axis bounding box.");
            double[] box = (double[])raw;
            Require(box.Length == 6, "A direct body exact bounding box must contain six coordinates.");
            for (int axis = 0; axis < 3; axis++)
            {
                Require(!Double.IsNaN(box[axis]) && !Double.IsInfinity(box[axis]) &&
                        !Double.IsNaN(box[axis + 3]) && !Double.IsInfinity(box[axis + 3]),
                    "A direct body exact bounding box contains a non-finite coordinate.");
                aggregate[axis] = Math.Min(aggregate[axis], box[axis]);
                aggregate[axis + 3] = Math.Max(aggregate[axis + 3], box[axis + 3]);
            }
        }
        return aggregate;
    }

    private static void RequireBboxNear(double[] actual, double[] expected)
    {
        Require(actual != null && actual.Length == 6 && expected != null && expected.Length == 6,
            "Expected and measured exact bounding boxes must each contain six coordinates.");
        for (int axis = 0; axis < 6; axis++)
            RequireNear(actual[axis], expected[axis], PositionToleranceMm, "aggregate exact bounding box coordinate " + axis);
    }

    private static bool TransactionBodyRecordsMatch(List<Dictionary<string, object>> before,
        List<Dictionary<string, object>> after, out Dictionary<string, object> evidence)
    {
        List<object> measurements = new List<object>();
        bool matches = before.Count == after.Count;
        for (int index = 0; matches && index < before.Count; index++)
        {
            Dictionary<string, object> left = new Dictionary<string, object>(before[index], StringComparer.Ordinal);
            Dictionary<string, object> right = new Dictionary<string, object>(after[index], StringComparer.Ordinal);
            foreach (string field in new string[] { "volume_mm3", "area_mm2" })
            {
                object a = null, b = null;
                bool found = left.TryGetValue(field, out a) && right.TryGetValue(field, out b);
                double tolerance = field == "volume_mm3" ? VolumeToleranceMm3 : AreaToleranceMm2;
                double? residual = null;
                bool equal = found;
                if (found && (a != null || b != null))
                {
                    equal = a != null && b != null;
                    if (equal)
                    {
                        double first = Convert.ToDouble(a, CultureInfo.InvariantCulture);
                        double second = Convert.ToDouble(b, CultureInfo.InvariantCulture);
                        residual = second - first;
                        equal = !Double.IsNaN(first) && !Double.IsInfinity(first) &&
                            !Double.IsNaN(second) && !Double.IsInfinity(second) && Math.Abs(residual.Value) <= tolerance;
                    }
                }
                measurements.Add(new Dictionary<string, object> {
                    { "body_id", before[index]["journal_id"] }, { "field", field },
                    { "residual", residual }, { "tolerance", tolerance }, { "matches", equal }
                });
                matches = matches && equal;
                left.Remove(field); right.Remove(field);
            }
            // Sheet mass-properties area is diagnostic only, never its geometry oracle.
            left.Remove("mass_area_diagnostic_mm2"); right.Remove("mass_area_diagnostic_mm2");
            matches = matches && String.Equals(M2Json.CanonicalText(left), M2Json.CanonicalText(right), StringComparison.Ordinal);
        }
        evidence = new Dictionary<string, object> {
            { "status", matches ? "VERIFIED" : "MISMATCH" }, { "measured_properties", measurements },
            { "comparison", "Transaction-local identity, topology, geometry and inventory exact; measured volume/area use the declared CAD tolerances." }
        };
        return matches;
    }

    private static List<string> SortedBodySemantics(List<Dictionary<string, object>> bodies)
    {
        List<string> result = new List<string>();
        foreach (Dictionary<string, object> body in bodies)
        {
            List<object> faceGeometry = new List<object>();
            foreach (Dictionary<string, object> face in (List<object>)body["faces"])
                faceGeometry.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "face_type", face["face_type"] }, { "point_mm", face["point_mm"] }, { "normal", face["normal"] },
                    { "face_box_mm", face["face_box_mm"] }, { "radius_mm", face["radius_mm"] },
                    { "radius_data", face["radius_data"] }, { "normal_direction", face["normal_direction"] }
                });
            List<object> edgeGeometry = new List<object>();
            foreach (Dictionary<string, object> edge in (List<object>)body["edges"])
                edgeGeometry.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "edge_type", edge["edge_type"] }, { "length_mm", edge["length_mm"] }, { "vertices_mm", edge["vertices_mm"] }
                });
            faceGeometry.Sort(delegate(object left, object right)
            { return String.CompareOrdinal(M2Json.CanonicalText(left), M2Json.CanonicalText(right)); });
            edgeGeometry.Sort(delegate(object left, object right)
            { return String.CompareOrdinal(M2Json.CanonicalText(left), M2Json.CanonicalText(right)); });
            result.Add(M2Json.CanonicalText(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "is_solid", body["is_solid"] }, { "face_count", body["face_count"] }, { "edge_count", body["edge_count"] },
                { "exact_bbox_mm", body["exact_bbox_mm"] }, { "volume_mm3", body["volume_mm3"] }, { "area_mm2", body["area_mm2"] },
                { "faces", faceGeometry }, { "edges", edgeGeometry }
            }));
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static Dictionary<string, object> CompareBodyInventoriesWithinTolerance(
        List<Dictionary<string, object>> before, List<Dictionary<string, object>> after)
    {
        List<object> pairs = new List<object>();
        List<string> unmatchedBefore = new List<string>();
        List<string> unmatchedAfter = new List<string>();
        List<object> unmatchedBeforeDetails = new List<object>();
        bool[] afterUsed = new bool[after == null ? 0 : after.Count];
        if (before != null)
        {
            foreach (Dictionary<string, object> expected in before)
            {
                int matchedIndex = -1;
                Dictionary<string, object> matchedDetails = null;
                List<object> candidateDiagnostics = new List<object>();
                for (int candidate = 0; after != null && candidate < after.Count; candidate++)
                {
                    if (afterUsed[candidate]) continue;
                    Dictionary<string, object> details;
                    if (BodyRecordsMatchWithinTolerance(expected, after[candidate], out details))
                    {
                        matchedIndex = candidate;
                        matchedDetails = details;
                        break;
                    }
                    candidateDiagnostics.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "after_body_tag", after[candidate]["tag"] },
                        { "after_body_journal_id", after[candidate]["journal_id"] },
                        { "comparison", details }
                    });
                }
                if (matchedIndex < 0)
                {
                    unmatchedBefore.Add(Convert.ToString(expected["journal_id"], CultureInfo.InvariantCulture));
                    unmatchedBeforeDetails.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "before_body_tag", expected["tag"] }, { "before_body_journal_id", expected["journal_id"] },
                        { "candidate_comparisons", candidateDiagnostics }
                    });
                    continue;
                }
                afterUsed[matchedIndex] = true;
                Dictionary<string, object> actual = after[matchedIndex];
                matchedDetails["before_body_tag"] = expected["tag"];
                matchedDetails["before_body_journal_id"] = expected["journal_id"];
                matchedDetails["after_body_tag"] = actual["tag"];
                matchedDetails["after_body_journal_id"] = actual["journal_id"];
                matchedDetails["body_tag_persisted_diagnostic"] = Object.Equals(expected["tag"], actual["tag"]);
                matchedDetails["body_journal_id_persisted_diagnostic"] = Object.Equals(expected["journal_id"], actual["journal_id"]);
                pairs.Add(matchedDetails);
            }
        }
        for (int i = 0; after != null && i < after.Count; i++)
            if (!afterUsed[i]) unmatchedAfter.Add(Convert.ToString(after[i]["journal_id"], CultureInfo.InvariantCulture));
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "matched", unmatchedBefore.Count == 0 && unmatchedAfter.Count == 0 && before != null && after != null && before.Count == after.Count },
            { "before_body_count", before == null ? 0 : before.Count }, { "after_body_count", after == null ? 0 : after.Count },
            { "absolute_tolerances", new Dictionary<string, object>(StringComparer.Ordinal)
                { { "position_mm", PositionToleranceMm }, { "volume_mm3", VolumeToleranceMm3 }, { "area_mm2", AreaToleranceMm2 } } },
            { "geometry_matched_body_pairs", pairs }, { "unmatched_before_body_journal_ids", unmatchedBefore },
            { "unmatched_before_body_comparison_details", unmatchedBeforeDetails },
            { "unmatched_after_body_journal_ids", unmatchedAfter },
            { "identity_note", "Tag and JournalIdentifier are retained as diagnostics and are not required to persist across SaveCopy/reopen." }
        };
    }

    private static bool BodyRecordsMatchWithinTolerance(Dictionary<string, object> expected,
        Dictionary<string, object> actual, out Dictionary<string, object> evidence)
    {
        List<string> mismatches = new List<string>();
        double bboxResidual = Double.PositiveInfinity;
        double volumeResidual = Double.PositiveInfinity;
        double areaResidual = Double.PositiveInfinity;
        bool solid = Object.Equals(expected["is_solid"], actual["is_solid"]);
        if (!solid) mismatches.Add("is_solid");
        if (!Object.Equals(expected["face_count"], actual["face_count"])) mismatches.Add("face_count");
        if (!Object.Equals(expected["edge_count"], actual["edge_count"])) mismatches.Add("edge_count");
        if (!Object.Equals(expected["consistency_fault_count"], actual["consistency_fault_count"])) mismatches.Add("consistency_fault_count");

        bool boundsMatch = NumberArrayMatches(expected["exact_bbox_mm"], actual["exact_bbox_mm"],
            PositionToleranceMm, out bboxResidual);
        if (!boundsMatch) mismatches.Add("exact_bbox_mm");
        bool volumeMatch = NumberMatches(expected["volume_mm3"], actual["volume_mm3"],
            VolumeToleranceMm3, 1.0e-8, out volumeResidual);
        if (!volumeMatch) mismatches.Add("volume_mm3");
        bool areaMatch = NumberMatches(expected["area_mm2"], actual["area_mm2"],
            AreaToleranceMm2, 0.0, out areaResidual);
        if (!areaMatch) mismatches.Add("area_mm2");

        List<object> facePairs;
        List<string> unmatchedFaces;
        bool facesMatch = MatchDirectFaces((List<object>)expected["faces"], (List<object>)actual["faces"],
            out facePairs, out unmatchedFaces);
        if (!facesMatch) mismatches.Add("faces");
        List<object> edgePairs;
        List<string> unmatchedEdges;
        bool edgesMatch = MatchDirectEdges((List<object>)expected["edges"], (List<object>)actual["edges"],
            out edgePairs, out unmatchedEdges);
        if (!edgesMatch) mismatches.Add("edges");

        evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "matched", mismatches.Count == 0 }, { "mismatched_fields", mismatches },
            { "bbox_max_abs_residual_mm", FiniteResidualOrNull(bboxResidual) }, { "volume_residual_mm3", FiniteResidualOrNull(volumeResidual) },
            { "area_residual_mm2", FiniteResidualOrNull(areaResidual) }, { "face_pairs", facePairs },
            { "unmatched_before_face_journal_ids", unmatchedFaces }, { "edge_pairs", edgePairs },
            { "unmatched_before_edge_journal_ids", unmatchedEdges }
        };
        return mismatches.Count == 0;
    }

    private static bool MatchDirectFaces(List<object> before, List<object> after,
        out List<object> pairs, out List<string> unmatchedBefore)
    {
        pairs = new List<object>();
        unmatchedBefore = new List<string>();
        bool[] afterUsed = new bool[after == null ? 0 : after.Count];
        bool allMatched = before != null && after != null && before.Count == after.Count;
        foreach (object raw in before ?? new List<object>())
        {
            Dictionary<string, object> expected = raw as Dictionary<string, object>;
            int match = -1;
            for (int i = 0; expected != null && after != null && i < after.Count; i++)
            {
                if (afterUsed[i]) continue;
                Dictionary<string, object> actual = after[i] as Dictionary<string, object>;
                if (actual != null && FaceGeometryMatches(expected, actual)) { match = i; break; }
            }
            if (match < 0)
            {
                allMatched = false;
                unmatchedBefore.Add(expected == null ? "<invalid>" : Convert.ToString(expected["journal_id"], CultureInfo.InvariantCulture));
                continue;
            }
            afterUsed[match] = true;
            Dictionary<string, object> actualFace = (Dictionary<string, object>)after[match];
            pairs.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "before_tag", expected["tag"] }, { "after_tag", actualFace["tag"] },
                { "before_journal_id", expected["journal_id"] }, { "after_journal_id", actualFace["journal_id"] },
                { "identity_persisted_diagnostic", Object.Equals(expected["tag"], actualFace["tag"]) && Object.Equals(expected["journal_id"], actualFace["journal_id"]) }
            });
        }
        if (after != null) for (int i = 0; i < after.Count; i++) if (!afterUsed[i]) allMatched = false;
        return allMatched;
    }

    private static bool FaceGeometryMatches(Dictionary<string, object> expected, Dictionary<string, object> actual)
    {
        double residual;
        return Object.Equals(expected["face_type"], actual["face_type"]) &&
            Object.Equals(expected["normal_direction"], actual["normal_direction"]) &&
            NumberArrayMatches(expected["point_mm"], actual["point_mm"], PositionToleranceMm, out residual) &&
            NumberArrayMatches(expected["normal"], actual["normal"], 1.0e-6, out residual) &&
            NumberArrayMatches(expected["face_box_mm"], actual["face_box_mm"], PositionToleranceMm, out residual) &&
            NumberMatches(expected["radius_mm"], actual["radius_mm"], PositionToleranceMm, 0.0, out residual) &&
            NumberMatches(expected["radius_data"], actual["radius_data"], PositionToleranceMm, 0.0, out residual);
    }

    private static bool MatchDirectEdges(List<object> before, List<object> after,
        out List<object> pairs, out List<string> unmatchedBefore)
    {
        pairs = new List<object>();
        unmatchedBefore = new List<string>();
        bool[] afterUsed = new bool[after == null ? 0 : after.Count];
        bool allMatched = before != null && after != null && before.Count == after.Count;
        foreach (object raw in before ?? new List<object>())
        {
            Dictionary<string, object> expected = raw as Dictionary<string, object>;
            int match = -1;
            for (int i = 0; expected != null && after != null && i < after.Count; i++)
            {
                if (afterUsed[i]) continue;
                Dictionary<string, object> actual = after[i] as Dictionary<string, object>;
                if (actual != null && EdgeGeometryMatches(expected, actual)) { match = i; break; }
            }
            if (match < 0)
            {
                allMatched = false;
                unmatchedBefore.Add(expected == null ? "<invalid>" : Convert.ToString(expected["journal_id"], CultureInfo.InvariantCulture));
                continue;
            }
            afterUsed[match] = true;
            Dictionary<string, object> actualEdge = (Dictionary<string, object>)after[match];
            pairs.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "before_tag", expected["tag"] }, { "after_tag", actualEdge["tag"] },
                { "before_journal_id", expected["journal_id"] }, { "after_journal_id", actualEdge["journal_id"] },
                { "identity_persisted_diagnostic", Object.Equals(expected["tag"], actualEdge["tag"]) && Object.Equals(expected["journal_id"], actualEdge["journal_id"]) }
            });
        }
        if (after != null) for (int i = 0; i < after.Count; i++) if (!afterUsed[i]) allMatched = false;
        return allMatched;
    }

    private static bool EdgeGeometryMatches(Dictionary<string, object> expected, Dictionary<string, object> actual)
    {
        double residual;
        if (!Object.Equals(expected["edge_type"], actual["edge_type"]) ||
            !NumberMatches(expected["length_mm"], actual["length_mm"], PositionToleranceMm, 0.0, out residual)) return false;
        double[][] expectedVertices = expected["vertices_mm"] as double[][];
        double[][] actualVertices = actual["vertices_mm"] as double[][];
        if (expectedVertices == null || actualVertices == null || expectedVertices.Length != 2 || actualVertices.Length != 2) return false;
        return (NumberArrayMatches(expectedVertices[0], actualVertices[0], PositionToleranceMm, out residual) &&
                NumberArrayMatches(expectedVertices[1], actualVertices[1], PositionToleranceMm, out residual)) ||
            (NumberArrayMatches(expectedVertices[0], actualVertices[1], PositionToleranceMm, out residual) &&
                NumberArrayMatches(expectedVertices[1], actualVertices[0], PositionToleranceMm, out residual));
    }

    private static bool NumberArrayMatches(object expectedValue, object actualValue, double tolerance, out double maxResidual)
    {
        double[] expected = expectedValue as double[];
        double[] actual = actualValue as double[];
        maxResidual = Double.PositiveInfinity;
        if (expected == null || actual == null || expected.Length != actual.Length) return false;
        maxResidual = 0.0;
        for (int i = 0; i < expected.Length; i++)
        {
            if (Double.IsNaN(expected[i]) || Double.IsInfinity(expected[i]) || Double.IsNaN(actual[i]) || Double.IsInfinity(actual[i]))
                return false;
            maxResidual = Math.Max(maxResidual, Math.Abs(expected[i] - actual[i]));
        }
        return maxResidual <= tolerance;
    }

    private static bool NumberMatches(object expectedValue, object actualValue,
        double absoluteTolerance, double relativeTolerance, out double residual)
    {
        residual = Double.PositiveInfinity;
        if (expectedValue == null || actualValue == null)
        {
            if (expectedValue == null && actualValue == null) { residual = 0.0; return true; }
            return false;
        }
        double expected = Convert.ToDouble(expectedValue, CultureInfo.InvariantCulture);
        double actual = Convert.ToDouble(actualValue, CultureInfo.InvariantCulture);
        if (Double.IsNaN(expected) || Double.IsInfinity(expected) || Double.IsNaN(actual) || Double.IsInfinity(actual)) return false;
        residual = Math.Abs(expected - actual);
        return residual <= Math.Max(absoluteTolerance, relativeTolerance * Math.Abs(expected));
    }

    private static object FiniteResidualOrNull(double residual)
    {
        return Double.IsNaN(residual) || Double.IsInfinity(residual) ? null : (object)residual;
    }

    private static Dictionary<string, object> ExecutionRecord(M3ExecutionResult result)
    {
        if (result == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "state", result.State }, { "revision_id", result.RevisionId }, { "result", result.Result },
            { "error_code", result.ErrorCode }, { "error_message", result.ErrorMessage }
        };
    }

    private static Dictionary<string, object> VerificationRecord(M3VerificationResult result)
    {
        if (result == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "verified", result.Verified }, { "revision_id", result.RevisionId }, { "checks", result.Checks },
            { "result", result.Result }, { "error_code", result.ErrorCode }, { "error_message", result.ErrorMessage }
        };
    }

    private static Dictionary<string, object> SketchRequestRecord(M3SketchRequest request)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", request.RevisionId }, { "plane", request.Plane }, { "origin_mm", request.OriginMm },
            { "constraint_mode", request.ConstraintMode }, { "geometry_count", request.Geometries.Count },
            { "geometry", request.Geometries.Select(item => new Dictionary<string, object>(StringComparer.Ordinal)
                { { "id", item.Id }, { "type", item.Type }, { "start_mm", item.StartMm }, { "end_mm", item.EndMm },
                  { "center_mm", item.CenterMm }, { "diameter_mm", item.DiameterMm }, { "radius_mm", item.RadiusMm },
                  { "major_radius_mm", item.MajorRadiusMm }, { "minor_radius_mm", item.MinorRadiusMm },
                  { "rotation_deg", item.RotationDeg }, { "start_angle_deg", item.StartAngleDeg }, { "sweep_deg", item.SweepDeg } }).Cast<object>().ToList() }
        };
    }

    private static Dictionary<string, object> ExtrudeRequestRecord(M3ExtrudeRequest request)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", request.RevisionId }, { "sketch_id", request.SketchId }, { "mode", request.Mode },
            { "direction", request.Direction }, { "extent", request.Extent }, { "profile_output", GetRequestField(request, "ProfileOutput") },
            { "start_offset_mm", GetRequestField(request, "StartOffsetMm") },
            { "end_offset_mm", request.Extent == "finite" ? GetRequestField(request, "EndOffsetMm") : null },
            { "depth_mm", request.Extent == "finite" ? (object)request.DepthMm : null }, { "target_body_id", request.TargetBodyId }
        };
    }

    private static void SetRequestField(M3ExtrudeRequest request, string name, object value)
    {
        System.Reflection.FieldInfo field = typeof(M3ExtrudeRequest).GetField(name);
        if (field == null) throw new MissingFieldException(typeof(M3ExtrudeRequest).FullName, name);
        field.SetValue(request, value);
    }

    private static object GetRequestField(M3ExtrudeRequest request, string name)
    {
        System.Reflection.FieldInfo field = typeof(M3ExtrudeRequest).GetField(name);
        return field == null ? null : field.GetValue(request);
    }

    private static Dictionary<string, object> CaseCounts()
    {
        int passed = 0, failed = 0, skipped = 0;
        foreach (object raw in _cases)
        {
            Dictionary<string, object> row = raw as Dictionary<string, object>;
            if (row == null) continue;
            string status = Convert.ToString(row["status"], CultureInfo.InvariantCulture);
            if (status == "PASSED") passed++; else if (status == "FAILED") failed++; else if (status == "SKIPPED") skipped++;
        }
        return new Dictionary<string, object> { { "selected", _cases.Count }, { "passed", passed }, { "failed", failed }, { "skipped", skipped } };
    }

    private static bool AllCasesPassed()
    {
        if (_cases.Count == 0) return false;
        foreach (object raw in _cases)
        {
            Dictionary<string, object> row = raw as Dictionary<string, object>;
            if (row == null || !String.Equals(Convert.ToString(row["status"], CultureInfo.InvariantCulture), "PASSED", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static void RunCase(string id, string objective, string retest, Action<Dictionary<string, object>> body)
    {
        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "id", id }, { "objective", objective }, { "status", "RUNNING" },
            { "reproduction_steps", new string[] { "Use a fresh unique F12 model path.", "Run the root-owned F12 batch journal against the recorded NX2506 build.", "Inspect the raw request, NX feature readback and direct NXOpen/UF evidence in this case record." } },
            { "retest_criterion", retest }, { "started_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
        };
        _cases.Add(row); _report["current_case"] = id; Persist();
        if (_writesHalted) { row["status"] = "SKIPPED"; row["skip_reason"] = "An earlier case halted writes."; Persist(); return; }
        try
        {
            body(row);
            row["status"] = "PASSED";
        }
        catch (UnknownOutcomeException ex)
        {
            _writesHalted = true; _unknownOutcome = true; row["status"] = "UNKNOWN"; row["observed_exception"] = ex.ToString();
            _defects.Add(DefectRecord(id, ex, retest)); SkipRemaining("Prior native outcome is UNKNOWN; preserve this model and do not replay.");
        }
        catch (Exception ex)
        {
            _writesHalted = true; row["status"] = "FAILED"; row["observed_exception"] = ex.ToString();
            _defects.Add(DefectRecord(id, ex, retest)); SkipRemaining("An acceptance assertion failed after native work; preserve the current model for diagnosis.");
        }
        row["finished_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        Persist();
    }

    private static Dictionary<string, object> DefectRecord(string id, Exception ex, string retest)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test_case", id }, { "observed_exception", ex.ToString() },
            { "reproduction_steps", new string[] { "Create a new unique millimeter Part.", "Recreate the serialized request from the failed case record.", "Read direct NX body/feature/Section state before any cleanup or replay." } },
            { "retest_criterion", retest }
        };
    }

    private static void RecordDefect(string id, Exception ex, string reproduction, string retest)
    {
        _defects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test_case", id }, { "observed_exception", ex.ToString() },
            { "reproduction_steps", new string[] { reproduction } }, { "retest_criterion", retest }
        });
    }

    private static void SkipRemaining(string reason)
    {
        foreach (object raw in _cases)
        {
            Dictionary<string, object> row = raw as Dictionary<string, object>;
            if (row != null && String.Equals(Convert.ToString(row["status"], CultureInfo.InvariantCulture), "RUNNING", StringComparison.Ordinal))
            { row["status"] = "SKIPPED"; row["skip_reason"] = reason; }
        }
    }

    private static void RequireNewPath(string path, string label)
    {
        if (File.Exists(path)) throw new IOException("Unique " + label + " path already exists: " + path);
    }

    private static void SavePart(Part part, string label)
    {
        PartSaveStatus status = part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
        if (status == null) throw new InvalidOperationException("NX Save returned null for " + label + ".");
        try { Require(status.NumberUnsavedParts == 0 && status.NumberUnsavedObjects == 0, "NX left unsaved objects while saving " + label + "."); }
        finally { status.Dispose(); }
    }

    private static void Persist()
    {
        if (_report != null && !String.IsNullOrEmpty(_reportPath)) File.WriteAllText(_reportPath, M2Json.CanonicalText(_report), new UTF8Encoding(false));
    }

    private static Dictionary<string, object> AssemblyRecord(System.Reflection.Assembly assembly)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "full_name", assembly.FullName }, { "location", assembly.Location },
            { "file_version", System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion }
        };
    }

    private static string ReadString(Dictionary<string, object> record, string key)
    {
        object value = null;
        Require(record != null && record.TryGetValue(key, out value), "Missing result field " + key + ".");
        string result = Convert.ToString(value, CultureInfo.InvariantCulture);
        Require(!String.IsNullOrWhiteSpace(result), "Empty result field " + key + ".");
        return result;
    }

    private static double[] Point(Point3d point) { return new double[] { point.X, point.Y, point.Z }; }
    private static double[] Vector(Vector3d value) { return new double[] { value.X, value.Y, value.Z }; }
    private static double Distance(Point3d a, Point3d b) { return Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2)); }
    private static string VectorKey(double[] values) { return String.Join(",", values.Select(value => Math.Round(value, 9).ToString("R", CultureInfo.InvariantCulture)).ToArray()); }
    private static bool Near(double a, double b) { return Math.Abs(a - b) <= PositionToleranceMm; }
    private static void RequireNear(double actual, double expected, double tolerance, string label)
    { Require(!Double.IsNaN(actual) && !Double.IsInfinity(actual) && Math.Abs(actual - expected) <= tolerance, label + " expected " + expected.ToString("R", CultureInfo.InvariantCulture) + " but measured " + actual.ToString("R", CultureInfo.InvariantCulture) + "."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
