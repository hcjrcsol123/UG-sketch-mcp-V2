// F10 native sketch projection acceptance skeleton.
// Build offline; invoke only as a Journal inside a designated idle NX2506 session.
// This entry never starts, restarts, closes, or terminates NX.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

public static class F10Acceptance
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    private const double PositionToleranceMm = 1.0e-6;
    private const double DirectionTolerance = 1.0e-7;
    private const double TwoPi = 2.0 * Math.PI;

    private sealed class Frame
    {
        internal double[] Origin;
        internal double[] Normal;
        internal double[] XAxis;
        internal double[] YAxis;
    }

    private sealed class ProjectionFixture
    {
        internal string Name;
        internal string SketchId;
        internal string ProjectionFeatureId;
        internal string ProjectionFeatureTag;
        internal string SourceTag;
        internal string RequestedSourceId;
        internal Dictionary<string, object> SourceBefore;
        internal Dictionary<string, object> TargetDrivingLine;
        internal Sketch Sketch;
        internal Line Output;
        internal bool Associative;
        internal double[] ExpectedStart;
        internal double[] ExpectedEnd;
        internal Dictionary<string, object> ExecuteResult;
        internal Dictionary<string, object> NativeRelation;
    }

    private sealed class ArcFixture
    {
        internal string Name;
        internal Arc Source;
    }

    private sealed class ArcProjectionFixture
    {
        internal string Name;
        internal string SketchId;
        internal string SourceTag;
        internal string RequestedSourceId;
        internal Dictionary<string, object> SourceBefore;
        internal Sketch Sketch;
        internal Arc Source;
        internal Arc Output;
        internal Dictionary<string, object> Oracle;
    }

    private delegate void CaseBody(Dictionary<string, object> row);

    public static void Main(string[] args)
    {
        string runId = Guid.NewGuid().ToString("N");
        string root = Path.Combine(ProjectRoot, "workspace", "v2");
        string modelDirectory = Path.Combine(root, "models");
        string evidenceDirectory = Path.Combine(root, "evidence");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        string sourcePath = Path.Combine(modelDirectory, "F10-acceptance-" + runId + ".prt");
        string roundTripPath = Path.Combine(modelDirectory, "F10-acceptance-" + runId + "-roundtrip.prt");
        string reportPath = Path.Combine(evidenceDirectory, "F10-acceptance-" + runId + ".json");
        RequireNewPath(sourcePath, "isolated model");
        RequireNewPath(roundTripPath, "round-trip model");
        RequireNewPath(reportPath, "report");

        List<object> cases = new List<object>();
        List<object> defects = new List<object>();
        List<object> fixtures = new List<object>();
        Dictionary<string, object> report = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test", "F10 native same-part sketch projection minimum acceptance matrix" },
            { "run_id", runId }, { "started_utc", UtcNow() }, { "passed", false },
            { "acceptance_status", "NOT_RUN" }, { "implementation_maturity", "SKELETON_MINIMUM_MATRIX" },
            { "cases", cases }, { "defects", defects }, { "fixtures", fixtures },
            { "source_model_path", sourcePath }, { "roundtrip_model_path", roundTripPath },
            { "report_path", reportPath }, { "sample_size", 1 }, { "units", "millimeter" },
            { "position_tolerance_mm", PositionToleranceMm }, { "direction_tolerance", DirectionTolerance },
            { "instrumentation", "Production M2OpenModelAdapter results plus direct NXOpen source, target Sketch, feature, body, and curve readback; the geometric projection oracle is implemented independently in this file." },
            { "instrument_calibration", "Not applicable: no physical instrument is used. CAD comparison limits are not metrology uncertainty." },
            { "prior_api_probe_context", "The separate F10 API probe reported that Commit with builder/Section tolerances 0.001 and Section.AngleTolerance API value 0.5 returned through commit, deactivation, and DoUpdate; inactive Sketch.Update threw. Those observations are context only and are not an acceptance result. This harness does not call inactive Sketch.Update." },
            { "prior_relation_probe_context", "A separate native relation probe reported that a reopened CPROJ feature's Section element-data input resolved to the exact original source Line Tag/JID. Its evidence is workspace/v2/evidence/F10-relation-probe-95d691ae27214962ac29263bbe15270e.json and is not acceptance evidence." },
            { "scope_limitations", new string[] {
                "One NX2506 configuration and one millimetre standalone part; this run cannot establish reliability, manufacturing accuracy, regulatory compliance, or behavior on another release.",
                "Minimum matrix covers one same-part spatial Line, positive and negative Sketch-normal directions, associative true and false, invalid prepare requests, stale-plan rejection, and source-edit propagation. Solid Edge and Arc coverage is not implemented in this skeleton.",
                "Save Copy and reopen are attempted when the production adapter exposes the expected operation; round-trip checks are reported separately from the native operation.",
                "A native run is evidence for the recorded configuration only. API probe, fake lifecycle, analytic geometry, and installed MCP evidence remain separate." } },
            { "traceability", "docs/F10.md; tests/nx/v2/F10Acceptance.md F10-00 through F10-10; docs/V2.md F10" },
            { "test_layer", "Subsystem validation through production M2OpenModelAdapter and an independent direct NXOpen numeric oracle." },
            { "nx_started_by_harness", false }, { "nx_restarted_by_harness", false },
            { "nx_closed_by_harness", false }
        };
        Persist(reportPath, report);

        Session session = null;
        Part originalPart = null;
        Part part = null;
        M2OpenModelAdapter adapter = null;
        M2ModelBinding binding = null;
        bool safeToContinue = true;
        bool fatalSetupFailure = false;
        Frame frame = MakeFrame(new double[] { 18.0, -11.0, 9.0 },
            new double[] { 1.0, 2.0, 3.0 }, new double[] { 1.0, -1.0, 0.5 });
        Line source = null;
        Line nonSourceCurve = null;
        Ellipse nonSourceEllipse = null;
        Arc matcherComplement = null;
        Point preservationPoint = null;
        Direction preservationDirection = null;
        Axis preservationAxis = null;
        List<ArcFixture> arcSources = new List<ArcFixture>();
        List<ProjectionFixture> successfulFixtures = new List<ProjectionFixture>();
        List<ArcProjectionFixture> successfulArcFixtures = new List<ArcProjectionFixture>();

        try
        {
            session = Session.GetSession();
            originalPart = session.Parts.Work;
            report["original_work_part_path"] = originalPart == null ? null : originalPart.FullPath;
            report["original_work_part_modified_before"] = originalPart != null && originalPart.IsModified;
            Require(!session.IsInTaskEnvironment && session.ActiveSketch == null,
                "Acceptance requires an idle NX session with no active Sketch or task environment.");
            Require(originalPart == null || !originalPart.IsModified,
                "The prior Work Part must be saved and unmodified; the harness leaves it untouched.");

            part = session.Parts.NewDisplay(sourcePath, Part.Units.Millimeters);
            Require(part != null && part.PartUnits == BasePart.Units.Millimeters,
                "NX did not create the unique isolated millimetre model.");
            source = CreateSpatialSource(part, frame);
            nonSourceCurve = CreateNonSourceSpatialCurve(part, frame);
            nonSourceEllipse = CreateNonSourceSpatialEllipse(part, frame);
            preservationPoint = part.Points.CreatePoint(ToPoint(Add(Add(frame.Origin, Scale(frame.XAxis, -9.0)), Scale(frame.Normal, 4.0))));
            preservationDirection = part.Directions.CreateDirection(ToPoint(Add(frame.Origin, Scale(frame.YAxis, 15.0))),
                ToVector(frame.XAxis), SmartObject.UpdateOption.WithinModeling);
            preservationAxis = part.Axes.CreateAxis(ToPoint(Add(frame.Origin, Scale(frame.XAxis, -14.0))),
                ToVector(frame.YAxis), SmartObject.UpdateOption.WithinModeling);
            matcherComplement = CreateMatcherComplementArc(part, frame);
            arcSources.AddRange(CreateArcSourceMatrix(part, frame, report));
            report["source_curve"] = CurveRecord(source);
            report["non_source_spatial_curve"] = CurveRecord(nonSourceCurve);
            report["non_source_spatial_ellipse"] = CurveRecord(nonSourceEllipse);
            report["preservation_objects"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "point", PointObjectRecord(preservationPoint) },
                { "direction", DirectionObjectRecord(preservationDirection) },
                { "axis", AxisObjectRecord(preservationAxis) }
            };
            report["arc_source_fixtures"] = ArcFixtureRecords(arcSources, frame);
            report["production_matcher_complement_candidate"] = CurveRecord(matcherComplement);
            SavePart(part, "initial isolated F10 source fixture");

            adapter = new M2OpenModelAdapter();
            binding = adapter.Takeover(sourcePath, "saved_independent_copy",
                "f10-acceptance-session-" + runId, "f10-acceptance-load-" + runId, 1);
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
                { "part_units", part.PartUnits.ToString() }, { "part_path", part.FullPath },
                { "binding_revision_id", binding.RevisionId }
            };
            Require(IsNx2506(nxVersion) || IsNx2506(GetString(sessionInfo, "release_number")) ||
                IsNx2506(GetString(sessionInfo, "full_release_number")),
                "This native acceptance is scoped to NX2506; the active release was not identified as NX2506.");

            // Cast through object so the skeleton still compiles against an older
            // installed M2Entry. Runtime capability is recorded as a defect until
            // the production adapter implements this optional interface.
            IM3SketchProjectionOperationAdapter projectionAdapter = (object)adapter as IM3SketchProjectionOperationAdapter;
            if (projectionAdapter == null)
            {
                fatalSetupFailure = true;
                AddDefect(report, "F10-capability", "The production M2OpenModelAdapter does not expose IM3SketchProjectionOperationAdapter.",
                    "Run the F10 native minimum matrix against a production adapter implementing PrepareSketchProjection.",
                    new string[] {
                        "Start from the unique saved millimetre fixture created by this run.",
                        "Call adapter.Takeover(saved_independent_copy) and inspect the production adapter capabilities.",
                        "Observe that IM3SketchProjectionOperationAdapter is unavailable before any projection prepare or execute call." },
                    "Implement and build the production F10 adapter, preserve this report, then rerun this entry in a fresh idle NX2506 session using a new part and report path.");
                return;
            }

            RunCase(report, "F10-00_production_arc_matcher_complement_counterexample",
                "The production Arc geometry matcher rejects the other 180-degree semicircle even though center, radius, endpoints, plane, and length are identical.",
                new string[] {
                    "Use the isolated 180-degree source and complementary candidate Arcs recorded in this report.",
                    "Construct the adapter's private M3SketchProjectionSourceState through reflection with the source center, radius, plane normal, endpoints, and pi-radian expected sweep.",
                    "Invoke M3SketchProjectionOutputGeometryMatches through reflection on the complementary NX Arc.",
                    "Independently check whether the source's expected interior midpoint lies on the candidate Arc." },
                "The analytic midpoint is outside the complementary candidate Arc and the production matcher returns false with evidence explaining the interior/winding-domain mismatch.",
                delegate(Dictionary<string, object> row)
                {
                    RunProductionMatcherCounterexample(matcherComplement, frame, row);
                });

            RunCase(report, "F10-01_associative_true_positive_normal", "A selected spatial Line projects to the explicit tilted Sketch along its normal with associative=true.",
                new string[] { "Create a new target Sketch on the isolated part.", "Prepare an explicit one-source request with direction equal to three times the target normal and associative=true.", "Execute and verify through M2OpenModelAdapter; compare direct output endpoints with the independent Euclidean projection oracle." },
                "Exactly one new Sketch Line matches the independent projected endpoints within 1e-6 mm and production VerifyPlan passes. Source edit propagation is assessed in the separately reported controlled challenge.",
                delegate(Dictionary<string, object> row)
                {
                    ProjectionFixture fixture = RunProjection(adapter, projectionAdapter, binding, part, session,
                        source, frame, "associative-true-positive", true, Scale(frame.Normal, 3.0), true, row);
                    successfulFixtures.Add(fixture);
                });
            safeToContinue = !HasUnknownOperation(cases);

            if (safeToContinue) RunCase(report, "F10-02_associative_false_negative_normal", "A selected spatial Line projects to the explicit tilted Sketch along the negative normal with associative=false.",
                new string[] { "Create a new target Sketch on the isolated part.", "Prepare an explicit one-source request with direction equal to the negative target normal and associative=false.", "Execute and verify through M2OpenModelAdapter; compare direct output endpoints with the independent Euclidean projection oracle." },
                "Exactly one new Sketch Line matches the independent projected endpoints within 1e-6 mm and production VerifyPlan passes. Source edit freeze behavior is assessed in the separately reported controlled challenge.",
                delegate(Dictionary<string, object> row)
                {
                    ProjectionFixture fixture = RunProjection(adapter, projectionAdapter, binding, part, session,
                        source, frame, "associative-false-negative", false, Scale(frame.Normal, -1.0), false, row);
                    successfulFixtures.Add(fixture);
                });
            safeToContinue = safeToContinue && !HasUnknownOperation(cases);

            if (safeToContinue) RunCase(report, "F10-05_dirty_verification_mismatch_rejected",
                "The production VerifyPlan result must include the dirty-state match in the overall verified result.",
                new string[] {
                    "Create an explicit target Sketch and prepare one same-part source Line with associative=false.",
                    "Execute the projection and verify it once; record the actual execute dirty state and retained DirtyAfter baseline.",
                    "Through reflection, change only the plan's retained DirtyAfter expectation to the opposite boolean and call VerifyPlan again.",
                    "Restore DirtyAfter exactly and call VerifyPlan again; compare native state and retained history/reference baselines after every verification." },
                "The controlled plan-baseline mismatch makes VerifyPlan.Verified=false and history=MISMATCH with matches_execute_dirty_state=false and all checks unverified; restoring the retained baseline makes the same native model verify again.",
                delegate(Dictionary<string, object> row)
                {
                    RunDirtyMismatchChallenge(adapter, projectionAdapter, binding, part, session, source, frame, row);
                });
            safeToContinue = safeToContinue && !HasUnknownOperation(cases);

            if (safeToContinue) RunCase(report, "F10-03_invalid_prepare_read_only_matrix", "Malformed or unsupported requests reject before creating a plan or changing native state.",
                new string[] { "Create a new explicit target Sketch.", "Capture all direct Work Part curve geometry, feature identities, body identities, Sketch geometry, active Sketch, dirty state, and binding revision.", "Try zero, in-plane, non-finite, empty-source, and missing-source requests one at a time.", "Capture the same direct state after each Prepare attempt and compare it byte-canonicalized through M2Json." },
                "Each request is rejected with a structured M2ModelException; native direct state, Part.IsModified, active Sketch, and binding revision remain unchanged after each rejection.",
                delegate(Dictionary<string, object> row)
                {
                    RunInvalidPrepareMatrix(adapter, projectionAdapter, binding, part, session, frame, source, row);
                });

            if (safeToContinue) RunCase(report, "F10-04_stale_plan_rejected_without_native_call", "Only a plan prepared against the current revision may execute.",
                new string[] { "Create a fresh target Sketch.", "Prepare two valid projection plans against the same binding revision.", "Execute and verify the first plan.", "Capture direct native state, execute the second plan, and prove it is unchanged and not applied." },
                "The first plan applies and verifies; the second returns UNCHANGED with a stale-plan or stale-revision error, makes no native geometry delta, and does not change Part.IsModified.",
                delegate(Dictionary<string, object> row)
                {
                    RunStalePlanCase(adapter, projectionAdapter, binding, part, session, source, frame, row);
                });
            safeToContinue = safeToContinue && !HasUnknownOperation(cases);

            foreach (ArcFixture arcFixture in arcSources)
            {
                ArcFixture currentArcFixture = arcFixture;
                RunCase(report, "F10-06_arc_" + currentArcFixture.Name,
                    "A selected native partial Arc projects to the tilted target Sketch with its center, radius, sweep domain, endpoints, and interior path preserved.",
                    new string[] {
                        "Create a fresh target Sketch in the isolated millimetre part.",
                        "Prepare one same-part Arc source with the Sketch-normal direction and associative=false.",
                        "Execute and verify through M2OpenModelAdapter.",
                        "Compare center in target-local coordinates, radius, endpoints, sweep, signed orientation, and an interior midpoint through direct NXOpen readback." },
                    "One Arc output matches center/radius/endpoints/sweep and contains the analytic source midpoint within configured CAD comparison limits; production VerifyPlan passes and native feature inventory remains correct for associative=false.",
                    delegate(Dictionary<string, object> row)
                    {
                        ArcProjectionFixture result = RunArcProjection(adapter, projectionAdapter, binding,
                            part, session, currentArcFixture, frame, row);
                        successfulArcFixtures.Add(result);
                    });
                safeToContinue = safeToContinue && !HasUnknownOperation(cases);
                if (!safeToContinue) break;
            }

            if (safeToContinue)
                RunCase(report, "F10-08_same_length_spatial_ellipse_inventory_detects_shape_change_and_restores",
                    "The production global Part.Curves inventory must reject same-length translated and rotated Ellipses, then a real NX Undo mark must restore the full direct native snapshot.",
                    new string[] {
                        "Use the non-source spatial Ellipse created in the unique fixture and capture the direct full native Part snapshot.",
                        "Capture the production M3SketchProjectionAdapter Part.Curves inventory by Tag.",
                        "Translate the Ellipse while preserving radii and length; invoke the production global curve delta matcher against the retained inventory and require a baseline-geometry mismatch.",
                        "Rotate the same Ellipse in its plane without changing its length; require the production inventory matcher to reject again.",
                        "Use the same NX Undo mark to restore the model and directly compare curves, features, bodies, expressions, Points, Axes, Directions, Sketches, constraints, references, dirty state, and active Sketch." },
                    "Both transformed Ellipses have unchanged intrinsic length but the production global inventory matcher rejects their changed geometry by the same stable Tag; actual NX Undo restores the complete direct native snapshot and Part.IsModified value.",
                    delegate(Dictionary<string, object> row)
                    {
                        RunEllipseInventoryMutationChallenge(adapter, binding, part, session, successfulFixtures,
                            source, nonSourceEllipse, frame, row);
                    });
            safeToContinue = safeToContinue && !HasUnknownOperation(cases);

            if (safeToContinue)
                RunCase(report, "F10-09_product_undo_after_controlled_postcondition_failure",
                    "A controlled expected-geometry mismatch after a real SketchProjectBuilder commit must use the production Undo mark and restore the full direct native baseline.",
                    new string[] {
                        "Create an independent target Sketch and preserve its existing seed Line as a native Driving Horizontal dimension.",
                        "Prepare a valid associative=false projection of the explicit spatial Line and capture the complete direct native snapshot plus dirty state.",
                        "After Prepare but before ExecutePlan, shift the retained expected output start point by 0.1 mm through reflection; do not modify the live model.",
                        "Call ExecutePlan exactly once. Require the real native projection call to occur, the deliberate postcondition mismatch to trigger production Undo, and the returned state to be ROLLED_BACK.",
                        "Take a new direct full Part snapshot immediately after rollback and compare geometry, constraints, references, history, expressions, bodies, curve inventory, helper objects, active Sketch, dirty state, and binding revision to the pre-execute baseline." },
                    "The native operation was attempted once; the controlled mismatch causes ROLLED_BACK; independent before/after direct snapshots and dirty state match exactly; the target Driving line remains intact. An UNKNOWN or unproven restoration stops all further writes.",
                    delegate(Dictionary<string, object> row)
                    {
                        RunProductUndoAfterInjectedPostcondition(adapter, projectionAdapter, binding,
                            part, session, source, frame, row);
                    });
            safeToContinue = safeToContinue && !HasUnknownOperation(cases);

            if (safeToContinue)
            {
                // Re-run the association challenge after all adapter lifecycle cases. This
                // keeps any external NX source edits after the last M2 adapter call.
                foreach (ProjectionFixture fixture in successfulFixtures)
                {
                    bool restored = RunAssociationChallenge(report, fixture, source, frame, session);
                    if (!restored) { safeToContinue = false; break; }
                }
            }

            if (safeToContinue && binding != null)
                RunSaveCopyRoundTrip(report, adapter, binding, session, part, roundTripPath,
                    successfulFixtures, successfulArcFixtures, frame);
            else
                report["save_copy_skipped_reason"] = "Prior operation could not prove exact Undo restoration; no more model writes or Save Copy are attempted.";
        }
        catch (Exception ex)
        {
            fatalSetupFailure = true;
            safeToContinue = false;
            AddDefect(report, "F10-fatal-setup", ex.GetType().FullName + ": " + ex.Message,
                "The native F10 setup and minimum matrix must complete without changing the pre-existing Work Part.",
                new string[] {
                    "Open a designated idle NX2506 session with no active Sketch or task environment.",
                    "Ensure the prior Work Part is saved and unmodified.",
                    "Run this Journal and inspect the unique part path, acceptance report, and captured NX version." },
                "Retain this report and all created models; correct the setup or adapter failure, then rerun against a new unique part and report path.");
        }
        finally
        {
            report["original_work_part_path_after"] = originalPart == null ? null : originalPart.FullPath;
            report["original_work_part_modified_after"] = originalPart != null && originalPart.IsModified;
            report["work_part_after"] = session == null || session.Parts.Work == null ? null : session.Parts.Work.FullPath;
            report["active_sketch_after"] = session == null || session.ActiveSketch == null ? null : session.ActiveSketch.Tag.ToString();
            report["completed_utc"] = UtcNow();
            report["safe_to_continue_after_run"] = safeToContinue;
            report["passed"] = !fatalSetupFailure && defects.Count == 0 && AllCasesPassed(cases) &&
                report.ContainsKey("save_copy") && SaveCopyPassed(report["save_copy"]);
            report["acceptance_status"] = Object.Equals(report["passed"], true) ? "PASS_SINGLE_RUN_EVIDENCE_ONLY" : "INCOMPLETE_OR_FAILED";
            try { Persist(reportPath, report); } catch { }
            if (adapter != null && binding != null)
            {
                try { adapter.Release(binding); } catch { }
            }
        }
    }

    private static ProjectionFixture RunProjection(M2OpenModelAdapter adapter,
        IM3SketchProjectionOperationAdapter projectionAdapter, M2ModelBinding binding, Part part,
        Session session, Line source, Frame frame, string name, bool associative,
        double[] direction, bool preserveDrivingLine, Dictionary<string, object> row)
    {
        Sketch historyCheckerNonTargetSketch = null;
        if (associative && String.Equals(name, "associative-true-positive", StringComparison.Ordinal))
        {
            historyCheckerNonTargetSketch = CreateTargetSketch(adapter, binding, part, frame,
                name + "-history-checker-non-target");
            Require(historyCheckerNonTargetSketch.Feature != null,
                "History-checker controlled input setup did not create an existing non-target Sketch feature.");
            row["history_checker_non_target_fixture"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "sketch_tag", historyCheckerNonTargetSketch.Tag.ToString() },
                { "feature_tag", historyCheckerNonTargetSketch.Feature.Tag.ToString() },
                { "feature_type", historyCheckerNonTargetSketch.Feature.FeatureType },
                { "created_before_projection_prepare", true },
                { "native_projection_dependency_injected", false }
            };
        }
        Sketch sketch = CreateTargetSketch(adapter, binding, part, frame, name);
        Dictionary<string, object> targetDrivingLine = null;
        if (preserveDrivingLine)
        {
            targetDrivingLine = AddHorizontalDrivingTargetLine(adapter, binding, session, part, sketch, name);
            row["target_driving_line_fixture"] = targetDrivingLine;
        }
        string sketchId = sketch.JournalIdentifier;
        string sourceTag = source.Tag.ToString();
        source = FindCurveByTag(part, sourceTag) as Line;
        Require(source != null, "Explicit source Line Tag could not be re-resolved before Prepare.");
        string requestSourceId = source.JournalIdentifier;
        Dictionary<string, object> sourceBefore = CurveRecord(source);
        double[] sourceStart = Point(source.StartPoint);
        double[] sourceEnd = Point(source.EndPoint);
        double[] expectedStart = ProjectPoint(sourceStart, frame.Origin, frame.Normal, direction);
        double[] expectedEnd = ProjectPoint(sourceEnd, frame.Origin, frame.Normal, direction);
        double[] normalizedDirection = Normalize3(direction);
        double directionAlignmentResidual = Norm(Cross(normalizedDirection, frame.Normal));
        HashSet<string> beforeTags = TagsInSketch(sketch);
        M3SketchProjectionRequest request = Request(binding, sketchId,
            new List<string> { requestSourceId }, direction, associative);
        Dictionary<string, object> beforePrepare = NativeSnapshot(part, session, sketch, source);
        bool dirtyBeforePrepare = part.IsModified;
        string revisionBeforePrepare = binding.RevisionId;
        row["request"] = ProjectionRequestRecord(request);
        row["sketch_id"] = sketchId;
        row["source_before"] = CurveRecord(source);
        row["oracle_expected_projected_start_mm"] = expectedStart;
        row["oracle_expected_projected_end_mm"] = expectedEnd;
        row["normalized_direction_xyz"] = normalizedDirection;
        row["direction_normal_alignment_residual"] = directionAlignmentResidual;
        row["direction_tolerance"] = DirectionTolerance;
        Require(directionAlignmentResidual <= DirectionTolerance,
            "Acceptance success case direction is not parallel to positive or negative target Sketch normal.");
        row["native_before_prepare"] = beforePrepare;
        row["dirty_before_prepare"] = dirtyBeforePrepare;

        M3PlanSnapshot plan = projectionAdapter.PrepareSketchProjection(binding, request);
        row["plan_public_view"] = plan == null ? null : plan.PublicView;
        Dictionary<string, object> afterPrepare = NativeSnapshot(part, session, sketch, source);
        row["native_after_prepare"] = afterPrepare;
        row["dirty_after_prepare"] = part.IsModified;
        row["revision_before_prepare"] = revisionBeforePrepare;
        row["revision_after_prepare"] = binding.RevisionId;
        Require(plan != null && plan.PublicView != null, "PrepareSketchProjection returned no immutable public plan.");
        row["plan_revision_id"] = plan.RevisionId;
        Require(String.Equals(plan.RevisionId, revisionBeforePrepare, StringComparison.Ordinal),
            "PrepareSketchProjection returned a plan with a different binding revision.");
        Require(SameValue(beforePrepare, afterPrepare), "PrepareSketchProjection changed direct native geometry, history, body, or Sketch state.");
        Require(dirtyBeforePrepare == part.IsModified, "PrepareSketchProjection changed the Part dirty state.");
        Require(String.Equals(revisionBeforePrepare, binding.RevisionId, StringComparison.Ordinal),
            "PrepareSketchProjection advanced the binding revision.");

        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        row["execution"] = ExecutionRecord(execution);
        Require(execution != null && execution.State == "APPLIED",
            "ExecutePlan did not return APPLIED for the F10 projection: " + M2Json.CanonicalText(ExecutionRecord(execution)));
        Dictionary<string, object> afterExecute = NativeSnapshot(part, session, sketch, FindCurveByTag(part, sourceTag));
        row["native_after_execute"] = afterExecute;
        row["active_sketch_after_execute"] = session.ActiveSketch == null ? null : session.ActiveSketch.Tag.ToString();

        bool dirtyBeforeVerify = part.IsModified;
        Dictionary<string, string> retainedBaselinesBeforeVerify = PlanBaselines(plan);
        row["retained_history_and_reference_baselines_before_verify"] = PublicPlanBaselines(retainedBaselinesBeforeVerify);
        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        row["verification"] = VerificationRecord(verification);
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "Production VerifyPlan did not verify the applied projection: " + M2Json.CanonicalText(VerificationRecord(verification)));
        Dictionary<string, object> afterVerify = NativeSnapshot(part, session, sketch, FindCurveByTag(part, sourceTag));
        row["native_after_verify"] = afterVerify;
        row["verify_read_only"] = SameValue(afterExecute, afterVerify);
        row["dirty_before_verify"] = dirtyBeforeVerify;
        row["dirty_after_verify"] = part.IsModified;
        row["retained_history_and_reference_baselines_after_verify"] = PublicPlanBaselines(PlanBaselines(plan));
        Dictionary<string, object> verificationHistoryEvidence = CheckEvidence(verification, "history");
        row["verification_history_evidence"] = verificationHistoryEvidence;
        Require(Object.Equals(row["verify_read_only"], true), "Production VerifyPlan changed direct native geometry, history, body, or Sketch state.");
        Require(dirtyBeforeVerify == part.IsModified, "Production VerifyPlan changed the Work Part dirty state.");
        Require(PlanBaselinesEqual(retainedBaselinesBeforeVerify, PlanBaselines(plan)),
            "VerifyPlan mutated retained pre-operation history or reference-status associations.");
        Require(Object.Equals(GetValue(verificationHistoryEvidence, "dirty_state_matches"), true) &&
            Object.Equals(GetValue(verificationHistoryEvidence, "matches_execute_dirty_state"), true),
            "Overall VerifyPlan evidence did not include passing dirty-state comparisons.");
        if (targetDrivingLine != null)
        {
            Dictionary<string, object> drivingPreservation = CheckHorizontalDrivingTargetLine(sketch, targetDrivingLine);
            row["existing_target_driving_line_preservation"] = drivingPreservation;
            Require(Object.Equals(GetValue(drivingPreservation, "matches"), true),
                "Projection did not preserve the target Sketch's existing Driving line geometry/constraint.");
        }

        List<Curve> outputs = NewSketchCurves(sketch, beforeTags);
        if (outputs.Count == 0)
            outputs = CurvesMatchingExpected(sketch, expectedStart, expectedEnd);
        row["target_geometry_after"] = SketchGeometryRecords(sketch);
        row["direct_output_curve_count"] = outputs.Count;
        row["direct_output_curve_records"] = CurveRecords(outputs.ToArray());
        Require(outputs.Count == 1, "Direct Tag/geometry oracle did not find exactly one new output curve for one selected source.");
        Line output = outputs[0] as Line;
        Require(output != null, "Direct native projection output is not a Line for the selected spatial Line source.");
        Dictionary<string, object> match = LineMatch(Point(output.StartPoint), Point(output.EndPoint), expectedStart, expectedEnd);
        row["direct_oracle_match"] = match;
        Require(Object.Equals(match["matches"], true), "Direct output Line differs from the independent analytic projection oracle.");
        Curve sourceAfterCurve = FindCurveByTag(part, sourceTag);
        Dictionary<string, object> sourceAfter = CurveRecord(sourceAfterCurve);
        row["source_after_verify"] = sourceAfter;
        row["source_journal_id_before"] = GetString(sourceBefore, "journal_id");
        row["source_journal_id_after"] = GetString(sourceAfter, "journal_id");
        Require(SameTagGeometry(sourceBefore, sourceAfter), "Projection changed the selected source Line Tag or geometry.");
        HashSet<string> beforeFeatureTags = FeatureTagsFromSnapshot(beforePrepare);
        List<Feature> newFeatures = NewFeatures(part, beforeFeatureTags);
        row["new_projection_features"] = FeatureRecords(newFeatures.ToArray());
        Feature projectionFeature = null;
        Dictionary<string, object> relation = null;
        if (associative)
        {
            Require(newFeatures.Count == 1,
                "One associative native projection operation did not create exactly one identifiable projection feature.");
            projectionFeature = newFeatures[0];
            bool dirtyBeforeRelationRead = part.IsModified;
            relation = ReadProjectionRelation(part, projectionFeature);
            bool dirtyAfterRelationRead = part.IsModified;
            row["native_section_relation"] = relation;
            row["dirty_before_relation_read"] = dirtyBeforeRelationRead;
            row["dirty_after_relation_read"] = dirtyAfterRelationRead;
            Require(dirtyBeforeRelationRead == dirtyAfterRelationRead,
                "Native Section relation readback changed the Part dirty state.");
            Require(RelationMatchesSource(relation, FindCurveByTag(part, sourceTag)),
                "Section element-data input Tag/JournalIdentifier did not resolve to the explicitly selected source Line.");
            Require(Object.Equals(GetValue(relation, "builder_associativity_readback"), true) &&
                Object.Equals(GetValue(relation, "builder_project_as_dumb_fixed_curves_readback"), false) &&
                String.Equals(GetString(relation, "builder_curve_type_readback"), "Original", StringComparison.Ordinal),
                "Reopened native CPROJ builder options do not match the explicit associative=true fixed-curve contract.");
        }
        else
        {
            Require(newFeatures.Count == 0,
                "Associative=false created a native projection feature; verify that the result is a fixed Sketch curve.");
            row["native_section_relation"] = null;
            row["native_section_relation_not_applicable"] = "Associative=false is expected to produce a dumb fixed Sketch curve without a CPROJ feature.";
        }
        row["native_operation_evidence"] = FindNestedDictionary(execution.Result, "native_operation");
        Dictionary<string, object> factoryEvidence = FindNestedDictionary(execution.Result, "factory_evidence");
        row["native_factory_evidence"] = factoryEvidence;
        row["complete_native_execution_result"] = execution.Result;
        row["native_commit_method_if_reported"] = FindNestedValue(execution.Result, "commit_method");
        row["source_tag"] = sourceTag;
        row["requested_source_journal_id"] = requestSourceId;
        row["current_source_journal_id"] = GetString(sourceAfter, "journal_id");
        row["output_tag"] = output.Tag.ToString();
        row["output_type"] = output.GetType().Name;
        row["associative"] = associative;
        row["native_section_relation_matches_source"] = true;
        Require(factoryEvidence != null, "Native projection execution omitted the production factory builder configuration evidence.");
        Require(Object.Equals(GetValue(factoryEvidence, "associativity_requested"), associative),
            "Production factory evidence does not record the explicit associativity request.");
        Require(Object.Equals(GetValue(factoryEvidence, "project_as_dumb_fixed_curves"), false),
            "Production factory evidence does not confirm ProjectAsDumbFixedCurves=false.");
        Require(String.Equals(GetString(factoryEvidence, "curve_type"), "Original", StringComparison.Ordinal),
            "Production factory evidence does not record CurveType=Original.");
        Require(PositiveFinite(factoryEvidence, "builder_tolerance") &&
            PositiveFinite(factoryEvidence, "section_distance_tolerance") &&
            PositiveFinite(factoryEvidence, "section_chaining_tolerance") &&
            PositiveFinite(factoryEvidence, "section_angle_tolerance_api"),
            "Production factory evidence omitted positive finite native builder or Section tolerance readbacks.");
        if (associative && String.Equals(name, "associative-true-positive", StringComparison.Ordinal))
            RunProductionHistoryGuardChallenge(adapter, binding, part, session, sketch,
                historyCheckerNonTargetSketch.Feature, sourceTag, plan, relation, row);
        return new ProjectionFixture
        {
            Name = name, SketchId = sketchId,
            ProjectionFeatureId = projectionFeature == null ? null : projectionFeature.JournalIdentifier,
            ProjectionFeatureTag = projectionFeature == null ? null : projectionFeature.Tag.ToString(),
            SourceTag = sourceTag, RequestedSourceId = requestSourceId, SourceBefore = sourceBefore,
            Sketch = sketch, Output = output,
            TargetDrivingLine = targetDrivingLine,
            Associative = associative, ExpectedStart = expectedStart, ExpectedEnd = expectedEnd,
            ExecuteResult = execution.Result, NativeRelation = relation
        };
    }

    private static void RunProductionHistoryGuardChallenge(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part, Session session, Sketch targetSketch,
        Feature nonTargetFeature, string sourceTag, M3PlanSnapshot plan,
        Dictionary<string, object> nativeRelation, Dictionary<string, object> row)
    {
        Require(adapter != null && binding != null && part != null && session != null &&
            targetSketch != null && nonTargetFeature != null && plan != null && plan.AdapterState != null,
            "History-checker challenge requires the live successful projection plan and fixture objects.");
        Require(nativeRelation != null && String.Equals(GetString(nativeRelation, "relation_evidence"),
            "Section.GetSectionData/GetSectionElementsData/GetSectionElementData1 input object identity",
            StringComparison.Ordinal),
            "History-checker challenge requires the independently read native CPROJ Section relation.");

        string projectionTag = GetString(nativeRelation, "projection_feature_tag");
        string targetFeatureTag = Convert.ToString(ReadPlanField(plan, "SketchFeatureTag"), CultureInfo.InvariantCulture);
        string nonTargetTag = nonTargetFeature.Tag.ToString();
        IDictionary featuresAfter = ReadPlanField(plan, "FeatureStatesAfter") as IDictionary;
        Require(featuresAfter != null && !String.IsNullOrEmpty(projectionTag) &&
            featuresAfter.Contains(projectionTag) && featuresAfter.Contains(targetFeatureTag) &&
            featuresAfter.Contains(nonTargetTag),
            "Production execution state did not retain the target, CPROJ, and existing non-target feature records.");

        object projectionState = featuresAfter[projectionTag];
        object nonTargetState = featuresAfter[nonTargetTag];
        Require(projectionState != null && nonTargetState != null &&
            String.Equals(Convert.ToString(ReadPrivateField(projectionState, "Type"), CultureInfo.InvariantCulture),
                "CPROJ", StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(Convert.ToString(ReadPrivateField(nonTargetState, "Type"), CultureInfo.InvariantCulture),
                "CPROJ", StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(nonTargetTag, targetFeatureTag, StringComparison.Ordinal),
            "History-checker fixture did not resolve a distinct non-target feature and the real CPROJ state.");

        List<string> originalProjectionChildren = ReadPrivateField(projectionState, "ChildTags") as List<string>;
        List<string> originalNonTargetParents = ReadPrivateField(nonTargetState, "ParentTags") as List<string>;
        List<string> originalProjectionParents = ReadPrivateField(projectionState, "ParentTags") as List<string>;
        Require(originalProjectionChildren != null && originalProjectionChildren.Count == 0 &&
            originalNonTargetParents != null && originalProjectionParents != null &&
            !originalNonTargetParents.Contains(projectionTag) && !originalProjectionParents.Contains(nonTargetTag),
            "Live NX history is not a clean positive control for the in-memory reverse-edge challenge.");

        row["history_checker_challenge_scope"] =
            "Controlled checker inputs only: temporary replacement List<string> values in retained in-memory FeatureStatesAfter records. No NX feature, parent/child relation, or Section relation is edited; the candidate is a pre-existing sibling Sketch feature outside the actual CPROJ parent set.";
        row["history_checker_candidate_non_target_feature"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", nonTargetTag },
            { "type", Convert.ToString(ReadPrivateField(nonTargetState, "Type"), CultureInfo.InvariantCulture) },
            { "preexisting_parent_tags", new List<string>(originalNonTargetParents) },
            { "real_cproj_parent_tags", new List<string>(originalProjectionParents) },
            { "is_target_sketch_feature", false },
            { "is_real_cproj_parent", originalProjectionParents.Contains(nonTargetTag) }
        };

        Dictionary<string, object> nativeBefore = NativeSnapshot(part, session, targetSketch,
            FindCurveByTag(part, sourceTag));
        row["native_snapshot_before_history_checker_challenge"] = nativeBefore;
        Dictionary<string, object> positiveEvidence;
        bool positiveMatches = InvokeProductionProjectionHistoryMatches(plan, part,
            featuresAfter, nativeRelation, out positiveEvidence);
        row["production_history_checker_positive_control"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "matches", positiveMatches }, { "evidence", positiveEvidence }
        };
        Require(positiveMatches, "The production history checker rejected its unmodified live FeatureStatesAfter baseline.");

        List<object> injectedCases = new List<object>();
        try
        {
            List<string> projectionChildrenOnly = new List<string>(originalProjectionChildren);
            projectionChildrenOnly.Add(nonTargetTag);
            SetField(projectionState, "ChildTags", projectionChildrenOnly);
            Dictionary<string, object> childOnlyEvidence;
            bool childOnlyMatches = InvokeProductionProjectionHistoryMatches(plan, part,
                featuresAfter, nativeRelation, out childOnlyEvidence);
            injectedCases.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "case", "cproj_child_tags_only" },
                { "injected_cproj_child_tags", new List<string>(projectionChildrenOnly) },
                { "injected_non_target_parent_tags", new List<string>(originalNonTargetParents) },
                { "matches", childOnlyMatches }, { "evidence", childOnlyEvidence }
            });
            Require(!childOnlyMatches && Object.Equals(GetValue(childOnlyEvidence,
                "projection_feature_child_tags_allowed"), false),
                "Production history checker accepted a non-empty CPROJ.ChildTags list by itself.");

            List<string> nonTargetParentOnly = new List<string>(originalNonTargetParents);
            nonTargetParentOnly.Add(projectionTag);
            SetField(projectionState, "ChildTags", new List<string>(originalProjectionChildren));
            SetField(nonTargetState, "ParentTags", nonTargetParentOnly);
            Dictionary<string, object> parentOnlyEvidence;
            bool parentOnlyMatches = InvokeProductionProjectionHistoryMatches(plan, part,
                featuresAfter, nativeRelation, out parentOnlyEvidence);
            injectedCases.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "case", "non_target_feature_parent_tags_only" },
                { "injected_cproj_child_tags", new List<string>(originalProjectionChildren) },
                { "injected_non_target_parent_tags", new List<string>(nonTargetParentOnly) },
                { "matches", parentOnlyMatches }, { "evidence", parentOnlyEvidence }
            });
            Require(!parentOnlyMatches && Object.Equals(GetValue(parentOnlyEvidence,
                "projection_tag_absent_from_existing_feature_parent_tags"), false),
                "Production history checker accepted a reverse CPROJ reference in a non-target feature ParentTags list by itself.");

            SetField(projectionState, "ChildTags", new List<string>(projectionChildrenOnly));
            Dictionary<string, object> bothEdgesEvidence;
            bool bothEdgesMatches = InvokeProductionProjectionHistoryMatches(plan, part,
                featuresAfter, nativeRelation, out bothEdgesEvidence);
            injectedCases.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "case", "both_cproj_child_and_non_target_parent_tags" },
                { "injected_cproj_child_tags", new List<string>(projectionChildrenOnly) },
                { "injected_non_target_parent_tags", new List<string>(nonTargetParentOnly) },
                { "matches", bothEdgesMatches }, { "evidence", bothEdgesEvidence }
            });
            Require(!bothEdgesMatches && Object.Equals(GetValue(bothEdgesEvidence,
                "projection_feature_child_tags_allowed"), false),
                "Production history checker accepted simultaneous CPROJ child and non-target reverse-parent references.");
        }
        finally
        {
            try { SetField(projectionState, "ChildTags", originalProjectionChildren); }
            finally { SetField(nonTargetState, "ParentTags", originalNonTargetParents); }
        }

        row["production_history_checker_controlled_input_cases"] = injectedCases;
        bool exactPrivateFieldRestoration = Object.ReferenceEquals(
                ReadPrivateField(projectionState, "ChildTags"), originalProjectionChildren) &&
            Object.ReferenceEquals(ReadPrivateField(nonTargetState, "ParentTags"), originalNonTargetParents);
        row["history_checker_private_field_references_restored_exactly"] = exactPrivateFieldRestoration;
        Require(exactPrivateFieldRestoration,
            "Controlled history-checker challenge did not restore the exact retained private List<string> references.");

        Dictionary<string, object> nativeAfter = NativeSnapshot(part, session, targetSketch,
            FindCurveByTag(part, sourceTag));
        row["native_snapshot_after_history_checker_challenge"] = nativeAfter;
        bool nativeUnchanged = SameValue(nativeBefore, nativeAfter);
        row["history_checker_native_snapshot_unchanged"] = nativeUnchanged;
        Require(nativeUnchanged,
            "Controlled history-checker in-memory inputs changed the direct NX native snapshot.");

        M3VerificationResult ordinaryVerification = adapter.VerifyPlan(binding, plan);
        Dictionary<string, object> ordinaryVerificationRecord = VerificationRecord(ordinaryVerification);
        row["ordinary_verify_after_history_checker_input_restoration"] = ordinaryVerificationRecord;
        bool ordinaryVerifyStillPasses = ordinaryVerification != null && ordinaryVerification.Verified &&
            ChecksPass(ordinaryVerification.Checks);
        row["ordinary_verify_after_history_checker_input_restoration_passes"] = ordinaryVerifyStillPasses;
        Require(ordinaryVerifyStillPasses,
            "Production VerifyPlan did not remain verified after the in-memory history-checker records were restored.");
        Dictionary<string, object> nativeAfterOrdinaryVerify = NativeSnapshot(part, session,
            targetSketch, FindCurveByTag(part, sourceTag));
        row["native_snapshot_after_ordinary_verify"] = nativeAfterOrdinaryVerify;
        Require(SameValue(nativeBefore, nativeAfterOrdinaryVerify),
            "Production VerifyPlan changed the direct native snapshot during the controlled history-checker test.");
    }

    private static bool InvokeProductionProjectionHistoryMatches(M3PlanSnapshot plan, Part part,
        IDictionary featuresAfter, Dictionary<string, object> relation,
        out Dictionary<string, object> evidence)
    {
        MethodInfo matcher = typeof(M2OpenModelAdapter).GetMethod(
            "M3SketchProjectionHistoryMatches", BindingFlags.Static | BindingFlags.NonPublic);
        Require(matcher != null,
            "Production M3SketchProjectionHistoryMatches method was not found for controlled history inputs.");
        object[] arguments = new object[] { plan.AdapterState, part, featuresAfter, relation, null };
        bool matches = (bool)matcher.Invoke(null, arguments);
        evidence = arguments[4] as Dictionary<string, object>;
        Require(evidence != null,
            "Production M3SketchProjectionHistoryMatches omitted its evidence dictionary.");
        return matches;
    }

    private static ArcProjectionFixture RunArcProjection(M2OpenModelAdapter adapter,
        IM3SketchProjectionOperationAdapter projectionAdapter, M2ModelBinding binding,
        Part part, Session session, ArcFixture fixture, Frame frame,
        Dictionary<string, object> row)
    {
        Sketch sketch = CreateTargetSketch(adapter, binding, part, frame, "arc-" + fixture.Name);
        string sourceTag = fixture.Source.Tag.ToString();
        Arc source = FindCurveByTag(part, sourceTag) as Arc;
        Require(source != null, "Explicit source Arc Tag could not be re-resolved before Prepare.");
        string requestSourceId = source.JournalIdentifier;
        Dictionary<string, object> sourceBefore = CurveRecord(source);
        Dictionary<string, object> oracle = ArcOracle(source, frame, frame.Normal);
        HashSet<string> beforeTags = TagsInSketch(sketch);
        M3SketchProjectionRequest request = Request(binding, sketch.JournalIdentifier,
            new List<string> { requestSourceId }, frame.Normal, false);
        Dictionary<string, object> beforePrepare = NativeSnapshot(part, session, sketch, source);
        bool dirtyBeforePrepare = part.IsModified;
        string revisionBeforePrepare = binding.RevisionId;
        row["fixture"] = fixture.Name;
        row["request"] = ProjectionRequestRecord(request);
        row["source_tag"] = sourceTag;
        row["requested_source_journal_id"] = requestSourceId;
        row["source_before"] = sourceBefore;
        row["independent_arc_oracle"] = oracle;
        row["native_before_prepare"] = beforePrepare;
        M3PlanSnapshot plan = projectionAdapter.PrepareSketchProjection(binding, request);
        row["plan_public_view"] = plan == null ? null : plan.PublicView;
        Dictionary<string, object> afterPrepare = NativeSnapshot(part, session, sketch, FindCurveByTag(part, sourceTag));
        row["native_after_prepare"] = afterPrepare;
        row["dirty_before_prepare"] = dirtyBeforePrepare;
        row["dirty_after_prepare"] = part.IsModified;
        Require(plan != null && plan.PublicView != null, "Arc PrepareSketchProjection returned no immutable public plan.");
        Require(String.Equals(plan.RevisionId, revisionBeforePrepare, StringComparison.Ordinal),
            "Arc projection Prepare returned a plan with a different binding revision.");
        Require(SameValue(beforePrepare, afterPrepare) && dirtyBeforePrepare == part.IsModified,
            "Arc projection Prepare changed direct native state or dirty state.");

        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        row["execution"] = ExecutionRecord(execution);
        Require(execution != null && execution.State == "APPLIED", "Arc projection ExecutePlan did not return APPLIED.");
        Dictionary<string, object> afterExecute = NativeSnapshot(part, session, sketch, FindCurveByTag(part, sourceTag));
        bool dirtyBeforeVerify = part.IsModified;
        Dictionary<string, string> retainedBeforeVerify = PlanBaselines(plan);
        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        row["verification"] = VerificationRecord(verification);
        Dictionary<string, object> afterVerify = NativeSnapshot(part, session, sketch, FindCurveByTag(part, sourceTag));
        Dictionary<string, object> historyEvidence = CheckEvidence(verification, "history");
        row["native_after_execute"] = afterExecute;
        row["native_after_verify"] = afterVerify;
        row["verify_read_only"] = SameValue(afterExecute, afterVerify);
        row["dirty_before_verify"] = dirtyBeforeVerify;
        row["dirty_after_verify"] = part.IsModified;
        row["verification_history_evidence"] = historyEvidence;
        row["retained_history_reference_baselines_unchanged"] = PlanBaselinesEqual(retainedBeforeVerify, PlanBaselines(plan));
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "Production VerifyPlan did not verify native Arc projection: " + M2Json.CanonicalText(VerificationRecord(verification)));
        Require(Object.Equals(row["verify_read_only"], true) && dirtyBeforeVerify == part.IsModified,
            "Arc VerifyPlan changed direct native state or dirty state.");
        Require(Object.Equals(row["retained_history_reference_baselines_unchanged"], true),
            "Arc VerifyPlan mutated retained pre-operation history or reference-status associations.");
        Require(Object.Equals(GetValue(historyEvidence, "dirty_state_matches"), true) &&
            Object.Equals(GetValue(historyEvidence, "matches_execute_dirty_state"), true),
            "Arc verification did not include passing dirty-state comparison evidence.");

        List<Curve> outputs = NewSketchCurves(sketch, beforeTags);
        row["direct_output_curve_count"] = outputs.Count;
        row["direct_output_curve_records"] = CurveRecords(outputs.ToArray());
        Require(outputs.Count == 1, "Direct Tag delta did not yield exactly one output for one Arc source.");
        Arc output = outputs[0] as Arc;
        Require(output != null, "NX native projection output is not an Arc for an Arc source.");
        Dictionary<string, object> arcMatch = ArcOutputMatches(output, oracle, frame);
        row["direct_arc_oracle_match"] = arcMatch;
        Require(Object.Equals(GetValue(arcMatch, "matches"), true),
            "Direct native Arc differs from the independent local-frame, sweep, and interior-point oracle.");
        Dictionary<string, object> sourceAfter = CurveRecord(FindCurveByTag(part, sourceTag));
        row["source_after_verify"] = sourceAfter;
        row["source_journal_id_after"] = GetString(sourceAfter, "journal_id");
        Require(SameTagGeometry(sourceBefore, sourceAfter),
            "Arc projection changed the selected source Tag or geometry.");
        HashSet<string> beforeFeatureTags = FeatureTagsFromSnapshot(beforePrepare);
        List<Feature> newFeatures = NewFeatures(part, beforeFeatureTags);
        row["new_projection_features"] = FeatureRecords(newFeatures.ToArray());
        Require(newFeatures.Count == 0,
            "Arc projection with associative=false created a native projection feature instead of fixed target geometry.");
        row["complete_native_execution_result"] = execution.Result;
        row["normalized_direction_xyz"] = Normalize3(frame.Normal);
        row["associative"] = false;
        row["passed"] = true;
        return new ArcProjectionFixture
        {
            Name = fixture.Name, SketchId = sketch.JournalIdentifier, SourceTag = sourceTag,
            RequestedSourceId = requestSourceId, SourceBefore = sourceBefore,
            Sketch = sketch, Source = source, Output = output, Oracle = oracle
        };
    }

    private static void RunProductionMatcherCounterexample(Arc candidate,
        Frame frame, Dictionary<string, object> row)
    {
        Require(candidate != null, "Complementary 180-degree native Arc candidate is missing.");
        Point3d center;
        Vector3d axisX, axisY;
        candidate.GetOrientation(out center, out axisX, out axisY);
        double radius = candidate.Radius;
        Point3d expectedStart = AddPoint(center, Scale(Vector(axisX), radius));
        Point3d expectedEnd = AddPoint(center, Scale(Vector(axisX), -radius));
        Point3d expectedMidpoint = AddPoint(center, Scale(Vector(axisY), radius));
        double[] targetNormal = frame.Normal;
        Type sourceStateType = typeof(M2OpenModelAdapter).GetNestedType("M3SketchProjectionSourceState", BindingFlags.NonPublic);
        Require(sourceStateType != null, "Production M3SketchProjectionSourceState type was not found for reflected matcher evidence.");
        object sourceState = Activator.CreateInstance(sourceStateType, true);
        SetField(sourceState, "GeometryType", "Arc");
        SetField(sourceState, "ExpectedCenter", center);
        SetField(sourceState, "ExpectedStart", expectedStart);
        SetField(sourceState, "ExpectedEnd", expectedEnd);
        SetField(sourceState, "PlaneNormal", ToVector(targetNormal));
        SetField(sourceState, "Radius", radius);
        SetField(sourceState, "ExpectedSweep", Math.PI);
        SetField(sourceState, "IsPeriodic", false);
        SetField(sourceState, "Center", center);
        SetField(sourceState, "Start", expectedStart);
        SetField(sourceState, "End", expectedEnd);
        SetField(sourceState, "AxisX", axisX);
        SetField(sourceState, "AxisY", axisY);
        SetField(sourceState, "StartAngle", 0.0);
        SetField(sourceState, "EndAngle", Math.PI);
        SetField(sourceState, "DirectedSweep", Math.PI);
        SetField(sourceState, "Length", radius * Math.PI);
        MethodInfo matcher = typeof(M2OpenModelAdapter).GetMethod("M3SketchProjectionOutputGeometryMatches",
            BindingFlags.Static | BindingFlags.NonPublic);
        Require(matcher != null, "Private production Arc output matcher was not found through reflection.");
        Arc correct = null, reverseEquivalent = null;
        foreach (Curve curve in ((Part)candidate.OwningPart).Curves.ToArray())
        {
            if (curve.Name == "F10_MATCHER_CORRECT")
            {
                Require(correct == null, "Correct matcher control must be unique.");
                correct = curve as Arc;
            }
            if (curve.Name == "F10_MATCHER_REVERSE_EQUIVALENT")
            {
                Require(reverseEquivalent == null, "Reverse matcher control must be unique.");
                reverseEquivalent = curve as Arc;
            }
        }
        Require(correct != null && reverseEquivalent != null, "Native positive matcher controls were not created before takeover.");
        object[] correctArguments = new object[] { sourceState, correct, null };
        object[] reverseArguments = new object[] { sourceState, reverseEquivalent, null };
        bool correctAccepted = (bool)matcher.Invoke(null, correctArguments);
        bool reverseAccepted = (bool)matcher.Invoke(null, reverseArguments);
        row["complete_expected_arc_definition"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "center_mm", Point(center) }, { "axis_x", Vector(axisX) }, { "axis_y", Vector(axisY) },
            { "radius_mm", radius }, { "start_angle_rad", 0.0 }, { "directed_sweep_rad", Math.PI },
            { "length_mm", radius * Math.PI }
        };
        row["correct_control_arc"] = CurveRecord(correct);
        row["reverse_equivalent_control_arc"] = CurveRecord(reverseEquivalent);
        row["production_matcher_accepted_correct_arc"] = correctAccepted;
        row["production_matcher_accepted_reverse_equivalent_arc"] = reverseAccepted;
        row["correct_control_matcher_evidence"] = correctArguments[2];
        row["reverse_equivalent_control_matcher_evidence"] = reverseArguments[2];
        Require(correctAccepted && reverseAccepted,
            "Production Arc matcher must accept both native positive controls before its complementary-arc rejection is meaningful.");
        object[] arguments = new object[] { sourceState, candidate, null };
        bool productionAccepted = (bool)matcher.Invoke(null, arguments);
        Dictionary<string, object> productionEvidence = arguments[2] as Dictionary<string, object>;
        Dictionary<string, object> midpointMembership = ArcContainsPoint(candidate, Point(expectedMidpoint));
        row["production_matcher_method"] = matcher.Name;
        row["production_matcher_accepted_complement"] = productionAccepted;
        row["production_matcher_evidence"] = productionEvidence;
        row["analytic_expected_interior_midpoint_mm"] = Point(expectedMidpoint);
        row["candidate_midpoint_membership"] = midpointMembership;
        row["candidate_arc"] = CurveRecord(candidate);
        Require(Object.Equals(GetValue(midpointMembership, "contains"), false),
            "Complementary candidate unexpectedly contains the expected semicircle midpoint; fixture is not discriminating.");
        Require(!productionAccepted,
            "Production Arc matcher accepted a complementary 180-degree Arc with the same endpoints, center, radius, plane, and length.");
    }

    private static void RunInvalidPrepareMatrix(M2OpenModelAdapter adapter,
        IM3SketchProjectionOperationAdapter projectionAdapter, M2ModelBinding binding,
        Part part, Session session, Frame frame, Line source, Dictionary<string, object> row)
    {
        Sketch sketch = CreateTargetSketch(adapter, binding, part, frame, "invalid-prepare-target");
        List<Dictionary<string, object>> subcases = new List<Dictionary<string, object>>();
        string sourceTag = source.Tag.ToString();
        row["sketch_id"] = sketch.JournalIdentifier;
        row["subcases"] = subcases;
        double[] inPlane = (double[])frame.XAxis.Clone();
        double[][] directions = new double[][]
        {
            new double[] { 0.0, 0.0, 0.0 },
            inPlane,
            new double[] { Double.NaN, 0.0, 1.0 }
        };
        string[] directionNames = new string[] { "zero_direction", "in_plane_direction", "non_finite_direction" };
        for (int i = 0; i < directions.Length; i++)
            RunRejectedPrepare(subcases, adapter, projectionAdapter, binding, part, session, sketch, source,
                directionNames[i], Request(binding, sketch.JournalIdentifier,
                    new List<string> { FindCurveByTag(part, sourceTag).JournalIdentifier }, directions[i], false));
        RunRejectedPrepare(subcases, adapter, projectionAdapter, binding, part, session, sketch, source,
            "empty_source_list", Request(binding, sketch.JournalIdentifier, new List<string>(), frame.Normal, false));
        RunRejectedPrepare(subcases, adapter, projectionAdapter, binding, part, session, sketch, source,
            "missing_source_id", Request(binding, sketch.JournalIdentifier,
                new List<string> { "F10-missing-source-id" }, frame.Normal, false));
        bool passed = true;
        foreach (Dictionary<string, object> subcase in subcases)
            if (!Object.Equals(subcase["passed"], true)) passed = false;
        row["passed"] = passed;
        Require(passed, "One or more invalid Prepare cases were accepted, returned an unstructured error, or changed native state.");
    }

    private static void RunRejectedPrepare(List<Dictionary<string, object>> subcases,
        M2OpenModelAdapter adapter, IM3SketchProjectionOperationAdapter projectionAdapter,
        M2ModelBinding binding, Part part, Session session, Sketch sketch, Line source,
        string name, M3SketchProjectionRequest request)
    {
        Line liveSource = FindCurveByTag(part, source.Tag.ToString()) as Line;
        Require(liveSource != null, "Explicit source Line Tag could not be re-resolved before invalid Prepare.");
        Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "request", ProjectionRequestRecord(request) }
        };
        subcases.Add(item);
        Dictionary<string, object> before = NativeSnapshot(part, session, sketch, liveSource);
        bool dirtyBefore = part.IsModified;
        string revisionBefore = binding.RevisionId;
        item["native_before"] = before;
        item["dirty_before"] = dirtyBefore;
        item["revision_before"] = revisionBefore;
        try
        {
            M3PlanSnapshot plan = projectionAdapter.PrepareSketchProjection(binding, request);
            item["unexpected_plan"] = plan == null ? null : plan.PublicView;
            item["rejection_observed"] = false;
            item["passed"] = false;
        }
        catch (M2ModelException ex)
        {
            item["rejection_observed"] = true;
            item["error_code"] = ex.Code;
            item["error_message"] = ex.Message;
            item["passed"] = true;
        }
        catch (Exception ex)
        {
            item["rejection_observed"] = false;
            item["unexpected_exception"] = ex.GetType().FullName + ": " + ex.Message;
            item["passed"] = false;
        }
        Dictionary<string, object> after = NativeSnapshot(part, session, sketch, FindCurveByTag(part, liveSource.Tag.ToString()));
        item["native_after"] = after;
        item["dirty_after"] = part.IsModified;
        item["revision_after"] = binding.RevisionId;
        item["native_unchanged"] = SameValue(before, after);
        item["dirty_unchanged"] = dirtyBefore == part.IsModified;
        item["revision_unchanged"] = String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal);
        item["passed"] = Object.Equals(item["passed"], true) && Object.Equals(item["native_unchanged"], true) &&
            Object.Equals(item["dirty_unchanged"], true) && Object.Equals(item["revision_unchanged"], true);
    }

    private static void RunStalePlanCase(M2OpenModelAdapter adapter,
        IM3SketchProjectionOperationAdapter projectionAdapter, M2ModelBinding binding,
        Part part, Session session, Line source, Frame frame, Dictionary<string, object> row)
    {
        Sketch sketch = CreateTargetSketch(adapter, binding, part, frame, "stale-plan-target");
        Line liveSource = FindCurveByTag(part, source.Tag.ToString()) as Line;
        Require(liveSource != null, "Explicit source Line Tag could not be re-resolved before stale-plan Prepare.");
        M3SketchProjectionRequest request = Request(binding, sketch.JournalIdentifier,
            new List<string> { liveSource.JournalIdentifier }, frame.Normal, false);
        string revision = binding.RevisionId;
        M3PlanSnapshot firstPlan = projectionAdapter.PrepareSketchProjection(binding, request);
        M3PlanSnapshot stalePlan = projectionAdapter.PrepareSketchProjection(binding, request);
        Require(firstPlan != null && stalePlan != null &&
            String.Equals(firstPlan.RevisionId, stalePlan.RevisionId, StringComparison.Ordinal),
            "Two plans prepared against one unchanged revision did not retain the same revision ID.");
        row["prepared_revision"] = revision;
        row["first_plan"] = firstPlan.PublicView;
        row["stale_plan"] = stalePlan.PublicView;
        M3ExecutionResult first = adapter.ExecutePlan(binding, firstPlan);
        row["first_execution"] = ExecutionRecord(first);
        Require(first != null && first.State == "APPLIED", "First valid F10 plan did not apply.");
        M3VerificationResult verification = adapter.VerifyPlan(binding, firstPlan);
        row["first_verification"] = VerificationRecord(verification);
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "First valid F10 plan did not verify before the stale-plan challenge.");
        Dictionary<string, object> beforeStaleExecute = NativeSnapshot(part, session, sketch, liveSource);
        bool dirtyBeforeStaleExecute = part.IsModified;
        M3ExecutionResult stale = adapter.ExecutePlan(binding, stalePlan);
        row["stale_execution"] = ExecutionRecord(stale);
        Dictionary<string, object> afterStaleExecute = NativeSnapshot(part, session, sketch, FindCurveByTag(part, liveSource.Tag.ToString()));
        row["native_before_stale_execute"] = beforeStaleExecute;
        row["native_after_stale_execute"] = afterStaleExecute;
        row["dirty_before_stale_execute"] = dirtyBeforeStaleExecute;
        row["dirty_after_stale_execute"] = part.IsModified;
        row["stale_execute_native_unchanged"] = SameValue(beforeStaleExecute, afterStaleExecute);
        row["stale_execute_dirty_unchanged"] = dirtyBeforeStaleExecute == part.IsModified;
        Require(stale != null && stale.State == "UNCHANGED" &&
            (stale.ErrorCode == "STALE_PLAN" || stale.ErrorCode == "STALE_REVISION"),
            "Second plan did not fail with the expected stale-plan or stale-revision result.");
        Require(Object.Equals(row["stale_execute_native_unchanged"], true) &&
            Object.Equals(row["stale_execute_dirty_unchanged"], true),
            "Stale-plan execution changed direct native state or dirty state.");
    }

    private static void RunDirtyMismatchChallenge(M2OpenModelAdapter adapter,
        IM3SketchProjectionOperationAdapter projectionAdapter, M2ModelBinding binding,
        Part part, Session session, Line source, Frame frame, Dictionary<string, object> row)
    {
        Sketch sketch = CreateTargetSketch(adapter, binding, part, frame, "dirty-mismatch-target");
        Line liveSource = FindCurveByTag(part, source.Tag.ToString()) as Line;
        Require(liveSource != null, "Source Line Tag could not be re-resolved before dirty-state challenge.");
        M3SketchProjectionRequest request = Request(binding, sketch.JournalIdentifier,
            new List<string> { liveSource.JournalIdentifier }, frame.Normal, false);
        M3PlanSnapshot plan = projectionAdapter.PrepareSketchProjection(binding, request);
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        row["plan"] = plan == null ? null : plan.PublicView;
        row["execution"] = ExecutionRecord(execution);
        Require(execution != null && execution.State == "APPLIED", "Dirty-state challenge projection did not apply.");
        bool actualDirtyAfterExecute = part.IsModified;
        object dirtyAfterExecuteField = ReadPlanField(plan, "DirtyAfter");
        Require(dirtyAfterExecuteField is bool && Object.Equals(dirtyAfterExecuteField, actualDirtyAfterExecute),
            "Retained DirtyAfter baseline does not match the measured Part dirty state after execute.");
        Dictionary<string, object> nativeAfterExecute = NativeSnapshot(part, session, sketch, FindCurveByTag(part, liveSource.Tag.ToString()));
        Dictionary<string, string> retainedBeforeVerify = PlanBaselines(plan);
        M3VerificationResult initial = adapter.VerifyPlan(binding, plan);
        row["initial_verification"] = VerificationRecord(initial);
        Dictionary<string, object> initialHistoryEvidence = CheckEvidence(initial, "history");
        row["initial_history_evidence"] = initialHistoryEvidence;
        Require(initial != null && initial.Verified && ChecksPass(initial.Checks),
            "Initial projection verification did not pass before the controlled dirty expectation mismatch.");
        Require(Object.Equals(GetValue(initialHistoryEvidence, "dirty_state_matches"), true) &&
            Object.Equals(GetValue(initialHistoryEvidence, "matches_execute_dirty_state"), true),
            "The passing history check omitted dirty-state evidence or did not compare with the execute dirty baseline.");
        Require(PlanBaselinesEqual(retainedBeforeVerify, PlanBaselines(plan)),
            "VerifyPlan changed retained pre-operation history or reference-status associations.");

        bool originalDirtyExpected = (bool)dirtyAfterExecuteField;
        bool dirtyFieldWasRestored = false;
        try
        {
            SetPlanField(plan, "DirtyAfter", !originalDirtyExpected);
            M3VerificationResult mismatch = adapter.VerifyPlan(binding, plan);
            row["controlled_mismatch_verification"] = VerificationRecord(mismatch);
            Dictionary<string, object> mismatchHistory = CheckEvidence(mismatch, "history");
            row["controlled_mismatch_history_evidence"] = mismatchHistory;
            row["controlled_dirty_after_expected"] = !originalDirtyExpected;
            row["actual_dirty_state_unchanged"] = part.IsModified == actualDirtyAfterExecute;
            row["controlled_mismatch_result_all_checks_verified"] = GetValue(mismatch == null ? null : mismatch.Result, "all_checks_verified");
            Require(mismatch != null && !mismatch.Verified,
                "VerifyPlan accepted an intentionally incorrect retained DirtyAfter baseline.");
            Require(String.Equals(GetString(ReadDictionary(mismatch.Checks, "history"), "status"), "MISMATCH", StringComparison.Ordinal),
                "Controlled dirty-state mismatch did not fail the overall history verification check.");
        row["controlled_mismatch_all_checks_verified"] = ChecksPass(mismatch == null ? null : mismatch.Checks);
        Require(Object.Equals(GetValue(mismatchHistory, "matches_execute_dirty_state"), false),
            "The failed history evidence did not expose matches_execute_dirty_state=false.");
        Require(!Object.Equals(row["controlled_mismatch_all_checks_verified"], true),
            "The controlled dirty-state mismatch still reports all checks verified.");
            Require(part.IsModified == actualDirtyAfterExecute,
                "VerifyPlan changed native Part dirty state during the controlled mismatch check.");
            Require(SameStableNativeSnapshot(nativeAfterExecute,
                NativeSnapshot(part, session, sketch, FindCurveByTag(part, liveSource.Tag.ToString()))),
                "VerifyPlan changed direct native state during the controlled mismatch check.");
            Require(PlanBaselinesEqual(retainedBeforeVerify, PlanBaselines(plan)),
                "Controlled mismatch VerifyPlan changed retained pre-operation history or associations.");
        }
        finally
        {
            SetPlanField(plan, "DirtyAfter", originalDirtyExpected);
            dirtyFieldWasRestored = Object.Equals(ReadPlanField(plan, "DirtyAfter"), originalDirtyExpected);
        }
        row["dirty_after_field_restored"] = dirtyFieldWasRestored;
        Require(dirtyFieldWasRestored, "Harness could not restore the retained DirtyAfter plan expectation.");
        M3VerificationResult restored = adapter.VerifyPlan(binding, plan);
        row["restored_verification"] = VerificationRecord(restored);
        Dictionary<string, object> restoredHistory = CheckEvidence(restored, "history");
        row["restored_history_evidence"] = restoredHistory;
        Require(restored != null && restored.Verified && ChecksPass(restored.Checks),
            "Restoring the retained DirtyAfter baseline did not restore verification success.");
        Require(Object.Equals(GetValue(restoredHistory, "dirty_state_matches"), true) &&
            Object.Equals(GetValue(restoredHistory, "matches_execute_dirty_state"), true),
            "Restored history evidence omitted a passing dirty-state comparison.");
        Require(PlanBaselinesEqual(retainedBeforeVerify, PlanBaselines(plan)),
            "Repeated VerifyPlan calls changed retained pre-operation history or reference-status associations.");
        Require(part.IsModified == actualDirtyAfterExecute &&
            SameStableNativeSnapshot(nativeAfterExecute,
                NativeSnapshot(part, session, sketch, FindCurveByTag(part, liveSource.Tag.ToString()))),
            "Dirty-state challenge or its verifications changed native geometry or dirty state.");
        row["passed"] = true;
    }

    private static void RunEllipseInventoryMutationChallenge(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part, Session session, List<ProjectionFixture> fixtures, Line source,
        Ellipse ellipse, Frame frame, Dictionary<string, object> row)
    {
        Require(ellipse != null && fixtures != null && fixtures.Count > 0,
            "The same-length Ellipse inventory challenge requires the real native Ellipse and a verified F10 target Sketch.");
        ProjectionFixture fixture = fixtures[0];
        Sketch target = fixture.Sketch;
        Require(target != null && FindCurveByTag(part, ellipse.Tag.ToString()) != null,
            "R10 fixture Ellipse Tag or target Sketch could not be re-resolved before the inventory mutation challenge.");

        bool dirtyBeforeInventoryRead = part.IsModified;
        Dictionary<string, object> ellipseBefore = CurveRecord(ellipse);
        double lengthBefore = ellipse.GetLength();
        double major, minor, start, end, rotation;
        Point3d center;
        ellipse.GetParameters(out major, out minor, out start, out end, out rotation, out center);
        row["ellipse_before"] = ellipseBefore;
        row["ellipse_length_before_mm"] = lengthBefore;

        object baselineInventory = CaptureProductionProjectionCurveInventory(part);
        Dictionary<string, object> positiveEvidence;
        bool positiveControl = ProductionProjectionCurveInventoryMatches(
            baselineInventory, baselineInventory, false, out positiveEvidence);
        row["production_inventory_positive_control"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "matches_unchanged_inventory", positiveControl }, { "evidence", positiveEvidence }
        };
        Require(positiveControl, "Production global Part.Curves matcher rejected its unchanged baseline control.");

        bool dirtyBeforeSnapshot = part.IsModified;
        Dictionary<string, object> nativeBefore = NativeSnapshot(part, session, target, source);
        bool dirtyBefore = part.IsModified;
        row["read_effects_before_r10_baseline"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "is_modified_before_curve_record_and_inventory", dirtyBeforeInventoryRead },
            { "is_modified_before_direct_snapshot", dirtyBeforeSnapshot },
            { "is_modified_after_direct_snapshot", dirtyBefore },
            { "undo_baseline_dirty_state", dirtyBefore }
        };
        row["native_before_test_owned_mutation"] = nativeBefore;
        row["part_modified_before"] = dirtyBefore;

        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible,
            "F10 same-length Ellipse global inventory mutation challenge");
        bool undoAttempted = false;
        bool undoSucceeded = false;
        bool undoUpdateSucceeded = false;
        bool fullRestoration = false;
        Exception challengeError = null;
        try
        {
            Point3d translatedCenter = new Point3d(
                center.X + frame.XAxis[0] * 9.0,
                center.Y + frame.XAxis[1] * 9.0,
                center.Z + frame.XAxis[2] * 9.0);
            ellipse.SetParameters(major, minor, start, end, rotation, translatedCenter);
            int translateUpdateErrors = session.UpdateManager.DoUpdate(mark);
            double translatedLength = ellipse.GetLength();
            Dictionary<string, object> translatedEllipse = CurveRecord(ellipse);
            object translatedInventory = CaptureProductionProjectionCurveInventory(part);
            Dictionary<string, object> translatedMatcherEvidence;
            bool translatedInventoryMatches = ProductionProjectionCurveInventoryMatches(
                baselineInventory, translatedInventory, false, out translatedMatcherEvidence);
            row["ellipse_after_translation"] = translatedEllipse;
            row["ellipse_length_after_translation_mm"] = translatedLength;
            row["translation_update_error_count"] = translateUpdateErrors;
            row["translation_length_residual_mm"] = Math.Abs(translatedLength - lengthBefore);
            row["production_inventory_after_translation"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "matches_before", translatedInventoryMatches }, { "evidence", translatedMatcherEvidence }
            };
            row["direct_snapshot_after_translation"] = NativeSnapshot(part, session, target, source);
            Require(translateUpdateErrors == 0, "NX UpdateManager reported errors during the controlled Ellipse translation.");
            Require(Math.Abs(translatedLength - lengthBefore) <= PositionToleranceMm,
                "The translated Ellipse changed intrinsic length, so it is not a discriminating same-length counterexample.");
            Require(!translatedInventoryMatches && ProductionInventoryReportsTagChange(
                translatedMatcherEvidence, ellipse.Tag.ToString()),
                "Production global Part.Curves inventory failed to detect a same-length Ellipse translation.");

            Point3d originalCenter = center;
            ellipse.SetParameters(major, minor, start, end, rotation + 0.4, originalCenter);
            int rotationUpdateErrors = session.UpdateManager.DoUpdate(mark);
            double rotatedLength = ellipse.GetLength();
            Dictionary<string, object> rotatedEllipse = CurveRecord(ellipse);
            object rotatedInventory = CaptureProductionProjectionCurveInventory(part);
            Dictionary<string, object> rotatedMatcherEvidence;
            bool rotatedInventoryMatches = ProductionProjectionCurveInventoryMatches(
                baselineInventory, rotatedInventory, false, out rotatedMatcherEvidence);
            row["ellipse_after_rotation"] = rotatedEllipse;
            row["ellipse_length_after_rotation_mm"] = rotatedLength;
            row["rotation_update_error_count"] = rotationUpdateErrors;
            row["rotation_length_residual_mm"] = Math.Abs(rotatedLength - lengthBefore);
            row["production_inventory_after_rotation"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "matches_before", rotatedInventoryMatches }, { "evidence", rotatedMatcherEvidence }
            };
            row["direct_snapshot_after_rotation"] = NativeSnapshot(part, session, target, source);
            Require(rotationUpdateErrors == 0, "NX UpdateManager reported errors during the controlled Ellipse rotation.");
            Require(Math.Abs(rotatedLength - lengthBefore) <= PositionToleranceMm,
                "The rotated Ellipse changed intrinsic length, so it is not a discriminating same-length counterexample.");
            Require(!rotatedInventoryMatches && ProductionInventoryReportsTagChange(
                rotatedMatcherEvidence, ellipse.Tag.ToString()),
                "Production global Part.Curves inventory failed to detect a same-length Ellipse in-plane rotation.");
        }
        catch (Exception ex)
        {
            challengeError = ex;
            row["challenge_error"] = ExceptionRecord(ex);
        }
        finally
        {
            undoAttempted = true;
            try
            {
                if (session.ActiveSketch != null)
                    throw new InvalidOperationException("A Sketch remains active before the R10 Ellipse Undo.");
                session.UndoToMark(mark, null);
                undoSucceeded = true;
                int errors = session.UpdateManager.DoUpdate(mark);
                row["undo_update_error_count"] = errors;
                undoUpdateSucceeded = errors == 0;
                Dictionary<string, object> nativeAfterUndo = NativeSnapshot(part, session, target, source);
                row["native_after_undo"] = nativeAfterUndo;
                Ellipse ellipseAfterUndo = FindCurveByTag(part, ellipse.Tag.ToString()) as Ellipse;
                row["ellipse_after_undo"] = ellipseAfterUndo == null ? null : CurveRecord(ellipseAfterUndo);
                row["part_modified_after_undo"] = part.IsModified;
                fullRestoration = undoSucceeded && undoUpdateSucceeded && ellipseAfterUndo != null &&
                    session.ActiveSketch == null && part.IsModified == dirtyBefore &&
                    SameStableNativeSnapshot(nativeBefore, nativeAfterUndo);
                row["direct_native_snapshot_restored"] = SameStableNativeSnapshot(nativeBefore, nativeAfterUndo);
                row["full_direct_restoration_proven"] = fullRestoration;
            }
            catch (Exception ex)
            {
                row["undo_error"] = ExceptionRecord(ex);
                row["operation_outcome"] = "UNKNOWN";
                row["writes_must_remain_stopped"] = true;
            }
            try { session.DeleteUndoMark(mark, null); }
            catch (Exception ex) { row["delete_undo_mark_error"] = ExceptionRecord(ex); }
        }

        row["undo_attempted"] = undoAttempted;
        row["undo_succeeded"] = undoSucceeded;
        row["undo_update_succeeded"] = undoUpdateSucceeded;
        row["part_modified_restored"] = part.IsModified == dirtyBefore;
        Require(fullRestoration,
            "Test-owned NX Undo did not independently restore the complete direct native snapshot and original dirty state.");
        row["session_after_ellipse_undo"] = adapter.GetSession(binding);
        row["binding_revision_after_ellipse_undo"] = binding.RevisionId;
        if (challengeError != null)
            throw new InvalidOperationException("R10 production global Ellipse inventory challenge failed after Undo restoration.", challengeError);
        row["passed"] = true;
    }

    private static object CaptureProductionProjectionCurveInventory(Part part)
    {
        MethodInfo capture = typeof(M2OpenModelAdapter).GetMethod(
            "CaptureM3SketchProjectionCurveInventory", BindingFlags.Static | BindingFlags.NonPublic);
        Require(capture != null, "Production Part.Curves projection inventory capture method was not found.");
        return capture.Invoke(null, new object[] { part });
    }

    private static bool ProductionProjectionCurveInventoryMatches(object beforeInventory,
        object afterInventory, bool associative, out Dictionary<string, object> evidence)
    {
        Type planStateType = typeof(M2OpenModelAdapter).GetNestedType(
            "M3SketchProjectionPlanState", BindingFlags.NonPublic);
        Require(planStateType != null, "Production projection plan state was not found for inventory matcher challenge.");
        object state = Activator.CreateInstance(planStateType, true);
        SetField(state, "CurveInventoryBefore", beforeInventory);
        SetField(state, "Request", new M3SketchProjectionRequest { Associative = associative });
        MethodInfo match = typeof(M2OpenModelAdapter).GetMethod(
            "M3SketchProjectionGlobalCurveDeltaMatches", BindingFlags.Static | BindingFlags.NonPublic);
        Require(match != null, "Production global Part.Curves inventory matcher was not found.");
        object[] arguments = new object[] { state, afterInventory, new List<string>(),
            new Dictionary<string, object>(StringComparer.Ordinal), null };
        bool matches = (bool)match.Invoke(null, arguments);
        evidence = arguments[4] as Dictionary<string, object>;
        Require(evidence != null, "Production global curve matcher omitted its direct evidence dictionary.");
        return matches;
    }

    private static bool ProductionInventoryReportsTagChange(Dictionary<string, object> evidence, string tag)
    {
        object rawChanges;
        List<object> changes = evidence != null && evidence.TryGetValue("baseline_curve_changes", out rawChanges)
            ? rawChanges as List<object> : null;
        if (changes == null) return false;
        foreach (object item in changes)
        {
            Dictionary<string, object> record = item as Dictionary<string, object>;
            if (record != null && String.Equals(GetString(record, "tag"), tag, StringComparison.Ordinal) &&
                String.Equals(GetString(record, "reason"), "baseline curve geometry or identity changed", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void RunProductUndoAfterInjectedPostcondition(M2OpenModelAdapter adapter,
        IM3SketchProjectionOperationAdapter projectionAdapter, M2ModelBinding binding,
        Part part, Session session, Line originalSource, Frame frame, Dictionary<string, object> row)
    {
        Sketch sketch = CreateTargetSketch(adapter, binding, part, frame, "controlled-product-undo-target");
        Dictionary<string, object> drivingLine = AddHorizontalDrivingTargetLine(
            adapter, binding, session, part, sketch, "controlled-product-undo-target");
        string sourceTag = originalSource.Tag.ToString();
        Line source = FindCurveByTag(part, sourceTag) as Line;
        Require(source != null, "Controlled product Undo could not re-resolve the explicit source Line by Tag.");
        M3SketchProjectionRequest request = Request(binding, sketch.JournalIdentifier,
            new List<string> { source.JournalIdentifier }, frame.Normal, false);
        row["request"] = ProjectionRequestRecord(request);
        row["target_sketch_before"] = DirectSketchRecord(sketch);
        row["target_driving_line_fixture"] = drivingLine;

        Dictionary<string, object> nativeBeforePrepare = NativeSnapshot(part, session, sketch, source);
        bool dirtyBeforePrepare = part.IsModified;
        string revisionBeforePrepare = binding.RevisionId;
        M3PlanSnapshot plan = projectionAdapter.PrepareSketchProjection(binding, request);
        Require(plan != null && plan.PublicView != null,
            "Controlled product Undo Prepare returned no projection plan.");
        Dictionary<string, object> nativeAfterPrepare = NativeSnapshot(part, session, sketch,
            FindCurveByTag(part, sourceTag));
        row["native_before_prepare"] = nativeBeforePrepare;
        row["native_after_prepare"] = nativeAfterPrepare;
        row["dirty_before_prepare"] = dirtyBeforePrepare;
        row["dirty_after_prepare"] = part.IsModified;
        row["revision_before_prepare"] = revisionBeforePrepare;
        row["revision_after_prepare"] = binding.RevisionId;
        Require(SameValue(nativeBeforePrepare, nativeAfterPrepare) &&
            dirtyBeforePrepare == part.IsModified && String.Equals(revisionBeforePrepare, binding.RevisionId, StringComparison.Ordinal),
            "Controlled product Undo Prepare changed direct native state, dirty state, or revision.");

        object planState = plan.AdapterState;
        Array sources = ReadPlanField(plan, "Sources") as Array;
        Require(sources != null && sources.Length == 1 && sources.GetValue(0) != null,
            "Controlled product Undo plan did not retain one source state for deterministic postcondition injection.");
        object sourceState = sources.GetValue(0);
        Point3d expectedStart = (Point3d)ReadPrivateField(sourceState, "ExpectedStart");
        double injectionMm = 0.1;
        Point3d corruptedExpectedStart = new Point3d(
            expectedStart.X + frame.XAxis[0] * injectionMm,
            expectedStart.Y + frame.XAxis[1] * injectionMm,
            expectedStart.Z + frame.XAxis[2] * injectionMm);
        Dictionary<string, object> directBeforeExecute = NativeSnapshot(part, session, sketch,
            FindCurveByTag(part, sourceTag));
        bool dirtyBeforeExecute = part.IsModified;
        row["native_before_execute"] = directBeforeExecute;
        row["dirty_before_execute"] = dirtyBeforeExecute;
        row["expected_output_start_before_injection_world_mm"] = Point(expectedStart);
        row["expected_output_start_injected_world_mm"] = Point(corruptedExpectedStart);
        row["controlled_expected_geometry_error_mm"] = injectionMm;

        M3ExecutionResult execution = null;
        Exception executionException = null;
        try
        {
            SetField(sourceState, "ExpectedStart", corruptedExpectedStart);
            row["fault_injection"] = "in_memory expected output start point moved 0.1 mm along target Sketch X; live NX model untouched before ExecutePlan";
            execution = adapter.ExecutePlan(binding, plan);
        }
        catch (Exception ex)
        {
            executionException = ex;
            row["execute_exception"] = ExceptionRecord(ex);
        }
        finally
        {
            SetField(sourceState, "ExpectedStart", expectedStart);
        }

        row["execution"] = ExecutionRecord(execution);
        Dictionary<string, object> directAfterExecute = NativeSnapshot(part, session, sketch,
            FindCurveByTag(part, sourceTag));
        row["native_after_execute_or_product_undo"] = directAfterExecute;
        row["dirty_after_execute_or_product_undo"] = part.IsModified;
        row["binding_revision_after_execute_or_product_undo"] = binding.RevisionId;
        bool directRestored = SameStableNativeSnapshot(directBeforeExecute, directAfterExecute);
        bool dirtyRestored = dirtyBeforeExecute == part.IsModified;
        bool activeSketchRestored = session.ActiveSketch == null;
        row["direct_full_native_snapshot_restored"] = directRestored;
        row["dirty_state_restored"] = dirtyRestored;
        row["active_sketch_restored"] = activeSketchRestored;
        Dictionary<string, object> restoration = FindNestedDictionary(execution == null ? null : execution.Result,
            "restoration");
        row["production_restore_evidence"] = restoration;
        bool fullProductRestore = Object.Equals(GetValue(restoration, "full_restoration_proven"), true) &&
            Object.Equals(GetValue(restoration, "undo_to_mark_succeeded"), true) &&
            Object.Equals(GetValue(restoration, "global_curve_inventory_restored"), true) &&
            Object.Equals(GetValue(restoration, "constraints_restored"), true) &&
            Object.Equals(GetValue(restoration, "history_restored"), true) &&
            Object.Equals(GetValue(restoration, "dirty_state_restored"), true);
        row["production_full_restore_evidence_matches"] = fullProductRestore;
        Dictionary<string, object> drivingAfter = CheckHorizontalDrivingTargetLine(sketch, drivingLine);
        row["target_driving_line_after_product_undo"] = drivingAfter;
        row["native_operation_called"] = FindNestedValue(execution == null ? null : execution.Result,
            "native_operation_called");
        row["native_undo_attempted"] = GetValue(restoration, "undo_to_mark_attempted");
        row["operation_outcome"] = execution == null ? "UNKNOWN" : execution.State;
        row["writes_must_remain_stopped"] = execution == null || execution.State == "UNKNOWN" ||
            !(directRestored && dirtyRestored && activeSketchRestored && fullProductRestore);

        Require(executionException == null,
            "The deterministic product postcondition failure escaped ExecutePlan instead of returning its rollback result.");
        Require(Object.Equals(row["native_operation_called"], true),
            "The product Undo challenge failed before the real NX projection operation was called.");
        Require(execution != null && String.Equals(execution.State, "ROLLED_BACK", StringComparison.Ordinal),
            "The controlled postcondition mismatch did not return a known ROLLED_BACK result.");
        Require(fullProductRestore && directRestored && dirtyRestored && activeSketchRestored,
            "The product Undo path did not prove complete restoration by both product evidence and direct native snapshots.");
        Require(Object.Equals(GetValue(drivingAfter, "matches"), true),
            "The existing target Driving line or its constraint was not restored by product Undo.");
        row["revision_policy"] = "Restored native geometry may advance the binding revision; old plans remain invalid.";
        row["execution_revision_matches_current_binding"] = execution != null &&
            !String.IsNullOrEmpty(binding.RevisionId) &&
            String.Equals(binding.RevisionId, execution.RevisionId, StringComparison.Ordinal);
        Require(Object.Equals(row["execution_revision_matches_current_binding"], true),
            "Product Undo returned a revision inconsistent with the current binding.");
        row["passed"] = true;
        row["operation_outcome"] = "ROLLED_BACK";
        row["writes_must_remain_stopped"] = false;
    }

    private static Dictionary<string, object> PublicPlanBaselines(Dictionary<string, string> values)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> item in values) result[item.Key] = item.Value;
        return result;
    }

    private static Dictionary<string, string> PlanBaselines(M3PlanSnapshot plan)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "history_projection_before", M2Json.CanonicalText(ReadPlanField(plan, "HistoryProjectionBefore")) },
            { "reference_states_before", M2Json.CanonicalText(ReferenceStateBaselineRecord(
                ReadPlanField(plan, "ReferenceStatesBefore"))) }
        };
    }

    // M2Json accepts the harness' public List<Dictionary<string,object>> records,
    // while the retained adapter baseline is Dictionary<string,int>. Convert it
    // deterministically without discarding either the stable curve Tag or status.
    private static List<object> ReferenceStateBaselineRecord(object value)
    {
        Dictionary<string, int> raw = value as Dictionary<string, int>;
        Require(raw != null, "Retained ReferenceStatesBefore is not Dictionary<string,int>.");
        List<string> tags = new List<string>(raw.Keys);
        tags.Sort(StringComparer.Ordinal);
        List<object> result = new List<object>();
        foreach (string tag in tags)
            result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "curve_tag", tag }, { "reference_status_value", raw[tag] }
            });
        return result;
    }

    private static bool PlanBaselinesEqual(Dictionary<string, string> left, Dictionary<string, string> right)
    { return SameValue(PublicPlanBaselines(left), PublicPlanBaselines(right)); }

    private static object ReadPlanField(M3PlanSnapshot plan, string name)
    {
        Require(plan != null && plan.AdapterState != null, "M3 plan state is unavailable for retained-field evidence.");
        FieldInfo field = plan.AdapterState.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Require(field != null, "M3 projection plan state did not expose retained field " + name + ".");
        return field.GetValue(plan.AdapterState);
    }

    private static void SetPlanField(M3PlanSnapshot plan, string name, object value)
    {
        Require(plan != null && plan.AdapterState != null, "M3 plan state is unavailable for controlled fault injection.");
        FieldInfo field = plan.AdapterState.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Require(field != null, "M3 projection plan state did not expose retained field " + name + ".");
        field.SetValue(plan.AdapterState, value);
    }

    private static Dictionary<string, object> CheckEvidence(M3VerificationResult result, string checkName)
    {
        Dictionary<string, object> check = ReadDictionary(result == null ? null : result.Checks, checkName);
        return ReadDictionary(check, "evidence");
    }

    private static object GetValue(Dictionary<string, object> dictionary, string key)
    { object value; return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null; }

    private static bool RunAssociationChallenge(Dictionary<string, object> report,
        ProjectionFixture fixture, Line source, Frame frame, Session session)
    {
        string name = fixture.Name + "_source_edit_propagation";
        Dictionary<string, object> row = NewCase(name,
            "Verify source edit propagation and exact NX Undo restoration for the recorded associativity setting.",
            new string[] {
                "Read the source Line and projected Sketch Line directly through NXOpen.",
                "Set an invisible Undo mark, translate both source endpoints by 2 mm along the target Sketch X axis, and call UpdateManager.DoUpdate(mark).",
                "Compare the projected Sketch Line with the associative or frozen expectation using 1e-6 mm point limits.",
                "Undo to the mark and prove source and output geometry returned to their pre-edit coordinates." },
            "Associative=true follows the source translation; associative=false retains its initial output; Undo restores the exact source and output coordinates.");
        List<object> cases = report["cases"] as List<object>;
        List<object> defects = report["defects"] as List<object>;
        cases.Add(row);
        Part fixturePart = fixture.Sketch.OwningPart as Part;
        Require(fixturePart != null, "Projection fixture does not belong to a native Part.");
        string sourceTag = fixture.SourceTag;
        Line currentSource = FindCurveByTag(fixturePart, sourceTag) as Line;
        Require(currentSource != null, "Selected source Line could not be re-resolved by its stable Tag before edit propagation.");
        bool undoProven = false;
        bool markCreated = false;
        Session.UndoMarkId mark = default(Session.UndoMarkId);
        double[] sourceStartBefore = Point(currentSource.StartPoint);
        double[] sourceEndBefore = Point(currentSource.EndPoint);
        double[] outputStartBefore = Point(fixture.Output.StartPoint);
        double[] outputEndBefore = Point(fixture.Output.EndPoint);
        bool dirtyBefore = fixturePart.IsModified;
        Dictionary<string, object> nativeBefore = NativeSnapshot(fixturePart, session, fixture.Sketch, currentSource);
        try
        {
            row["associative"] = fixture.Associative;
            row["source_before"] = CurveRecord(currentSource);
            row["output_before"] = CurveRecord(fixture.Output);
            row["dirty_before"] = dirtyBefore;
            row["native_before"] = nativeBefore;
            mark = session.SetUndoMark(Session.MarkVisibility.Invisible, "F10 association challenge " + fixture.Name);
            markCreated = true;
            double[] shift = Scale(frame.XAxis, 2.0);
            currentSource.SetEndpoints(ToPoint(Add(sourceStartBefore, shift)), ToPoint(Add(sourceEndBefore, shift)));
            int updateErrors = session.UpdateManager.DoUpdate(mark);
            row["source_edit_update_errors"] = updateErrors;
            Line sourceAfterEdit = FindCurveByTag(fixturePart, sourceTag) as Line;
            Line outputAfterEdit = FindProjectedLine(fixture.Sketch, fixture.Output.Tag.ToString(),
                fixture.Associative ? Add(fixture.ExpectedStart, shift) : fixture.ExpectedStart,
                fixture.Associative ? Add(fixture.ExpectedEnd, shift) : fixture.ExpectedEnd);
            row["source_after_edit"] = CurveRecord(sourceAfterEdit);
            row["source_journal_id_after_edit"] = sourceAfterEdit.JournalIdentifier;
            row["output_after_edit"] = CurveRecord(outputAfterEdit);
            double[] expectedStart = fixture.Associative ? Add(fixture.ExpectedStart, shift) : fixture.ExpectedStart;
            double[] expectedEnd = fixture.Associative ? Add(fixture.ExpectedEnd, shift) : fixture.ExpectedEnd;
            Dictionary<string, object> propagationMatch = LineMatch(Point(outputAfterEdit.StartPoint),
                Point(outputAfterEdit.EndPoint), expectedStart, expectedEnd);
            row["expected_output_after_edit_start_mm"] = expectedStart;
            row["expected_output_after_edit_end_mm"] = expectedEnd;
            row["output_after_edit_match"] = propagationMatch;
            Require(updateErrors == 0, "NX UpdateManager reported errors during the controlled source edit.");
            Require(Object.Equals(propagationMatch["matches"], true),
                "Native projection did not meet the requested associative or frozen source-edit behavior.");
            session.UndoToMark(mark, null);
            row["undo_attempted"] = true;
            Line sourceAfterUndo = FindCurveByTag(fixturePart, sourceTag) as Line;
            Line outputAfterUndo = FindProjectedLine(fixture.Sketch, fixture.Output.Tag.ToString(), outputStartBefore, outputEndBefore);
            Dictionary<string, object> nativeAfterUndo = NativeSnapshot(fixturePart, session, fixture.Sketch, sourceAfterUndo);
            undoProven = SameLineCoordinates(sourceStartBefore, sourceEndBefore, sourceAfterUndo) &&
                Object.Equals(LineMatch(Point(outputAfterUndo.StartPoint), Point(outputAfterUndo.EndPoint), outputStartBefore, outputEndBefore)["matches"], true) &&
                dirtyBefore == fixturePart.IsModified && SameStableNativeSnapshot(nativeBefore, nativeAfterUndo);
            row["native_after_undo"] = nativeAfterUndo;
            row["source_after_undo"] = CurveRecord(sourceAfterUndo);
            row["source_journal_id_after_undo"] = sourceAfterUndo.JournalIdentifier;
            row["output_after_undo"] = CurveRecord(outputAfterUndo);
            row["dirty_after_undo"] = fixturePart.IsModified;
            row["undo_restored_source_and_output"] = undoProven;
            Require(undoProven, "NX Undo did not restore the pre-edit source and projection coordinates.");
            row["passed"] = true;
        }
        catch (Exception ex)
        {
            row["passed"] = false;
            row["error"] = ex.GetType().FullName + ": " + ex.Message;
            row["error_stack_trace"] = ex.StackTrace;
            if (markCreated)
            {
                try
                {
                    session.UndoToMark(mark, null);
                    Line sourceAfterUndo = FindCurveByTag(fixturePart, sourceTag) as Line;
                    Line outputAfterUndo = FindProjectedLine(fixture.Sketch, fixture.Output.Tag.ToString(), outputStartBefore, outputEndBefore);
                    undoProven = SameLineCoordinates(sourceStartBefore, sourceEndBefore, sourceAfterUndo) &&
                        Object.Equals(LineMatch(Point(outputAfterUndo.StartPoint), Point(outputAfterUndo.EndPoint),
                            outputStartBefore, outputEndBefore)["matches"], true) &&
                        dirtyBefore == fixturePart.IsModified &&
                        SameStableNativeSnapshot(nativeBefore, NativeSnapshot(fixturePart, session, fixture.Sketch, sourceAfterUndo));
                }
                catch (Exception undoError) { row["undo_error"] = undoError.GetType().FullName + ": " + undoError.Message; }
            }
            row["undo_restored_source_and_output"] = undoProven;
        }
        finally
        {
            if (markCreated)
            {
                try { session.DeleteUndoMark(mark, null); } catch (Exception ex) { row["delete_undo_mark_error"] = ex.Message; }
            }
        }
        if (!Object.Equals(row["passed"], true))
        {
            AddCaseDefect(defects, row);
            if (!undoProven)
            {
                row["operation_outcome"] = "UNKNOWN";
                row["writes_must_remain_stopped"] = true;
                return false;
            }
        }
        Persist(GetString(report, "report_path"), report);
        return true;
    }

    private static void RunSaveCopyRoundTrip(Dictionary<string, object> report,
        M2OpenModelAdapter adapter, M2ModelBinding binding, Session session, Part part,
        string roundTripPath, List<ProjectionFixture> fixtures,
        List<ArcProjectionFixture> arcFixtures, Frame frame)
    {
        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "id", "F10-05_save_copy_roundtrip" },
            { "objective", "Projection geometry and native source relation survive Save Copy and reopen." },
            { "reproduction_steps", new string[] {
                "Run both positive and negative normal projection cases.",
                "Read each associative projection feature Section element-data record and resolve its selected source Tag/JournalIdentifier.",
                "Save Copy to the unique -roundtrip.prt path with reopen=true.",
                "Re-resolve each projection feature, source curve, and target Sketch after reopen; repeat direct geometry and Section element-data readback." } },
            { "retest_criterion", "On a new unique part and report, Save Copy reports SAVED/reopened; direct output geometry matches within 1e-6 mm; associative Section input Tag/JID resolves to the reopened source, while associative=false outputs remain fixed curves without CPROJ source relations." },
            { "target_path", roundTripPath }, { "reopen_requested", true }, { "passed", false }
        };
        report["save_copy"] = row;
        (report["cases"] as List<object>).Add(row);
        try
        {
            Require((fixtures != null && fixtures.Count > 0) || (arcFixtures != null && arcFixtures.Count > 0),
                "No successfully verified projection fixture is available for Save Copy/reopen evidence.");
            List<object> beforeOracle = new List<object>();
            foreach (ProjectionFixture fixture in fixtures)
                beforeOracle.Add(DirectFixtureOracle(fixture));
            foreach (ArcProjectionFixture fixture in arcFixtures)
                beforeOracle.Add(DirectArcFixtureOracle(fixture));
            row["direct_oracle_before_save_copy"] = beforeOracle;
            M2SaveCopyResult saved = adapter.SaveCopy(binding, roundTripPath, true);
            row["result"] = SaveCopyRecord(saved);
            Require(saved != null && saved.State == "SAVED" && saved.Reopened && ChecksPass(saved.Checks),
                "Production SaveCopy did not report SAVED/reopened with passing checks.");
            Part reopened = session.Parts.Work;
            Require(reopened != null && String.Equals(Path.GetFullPath(reopened.FullPath),
                Path.GetFullPath(roundTripPath), StringComparison.OrdinalIgnoreCase),
                "NX did not reopen the unique round-trip copy as Work Part.");
            List<object> afterOracle = new List<object>();
            foreach (ProjectionFixture fixture in fixtures)
            {
                Sketch reopenedSketch = FindSketch(reopened, fixture.SketchId);
                Feature reopenedFeature = null;
                Curve reopenedSource = null;
                Dictionary<string, object> relation = null;
                if (fixture.Associative)
                {
                    reopenedFeature = FindFeatureByTagOrJournalId(reopened,
                        fixture.ProjectionFeatureTag, fixture.ProjectionFeatureId);
                    relation = ReadProjectionRelation(reopened, reopenedFeature);
                    reopenedSource = ResolveRelationSource(reopened, relation, fixture.SourceBefore);
                    Require(RelationMatchesSource(relation, reopenedSource),
                        "After reopen, Section element-data no longer resolves to the re-resolved source curve for " + fixture.Name + ".");
                }
                else
                    reopenedSource = FindCurveByTagOrGeometry(reopened, fixture.SourceTag, fixture.SourceBefore);
                double[] expectedStart = fixture.ExpectedStart;
                double[] expectedEnd = fixture.ExpectedEnd;
                List<Curve> matches = CurvesMatchingExpected(reopenedSketch, expectedStart, expectedEnd);
                Dictionary<string, object> direct = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "fixture", fixture.Name }, { "sketch_id", reopenedSketch.JournalIdentifier },
                    { "associative", fixture.Associative },
                    { "projection_feature_id", reopenedFeature == null ? null : reopenedFeature.JournalIdentifier },
                    { "projection_feature_tag", reopenedFeature == null ? null : reopenedFeature.Tag.ToString() },
                    { "reopened_source", CurveRecord(reopenedSource) }, { "native_section_relation", relation },
                    { "expected_start_mm", expectedStart }, { "expected_end_mm", expectedEnd },
                    { "candidate_count", matches.Count }, { "candidates", CurveRecords(matches.ToArray()) },
                    { "matches", matches.Count == 1 }
                };
                if (fixture.TargetDrivingLine != null)
                {
                    Dictionary<string, object> drivingLineMatch = CheckHorizontalDrivingTargetLine(
                        reopenedSketch, fixture.TargetDrivingLine);
                    direct["target_driving_line_preservation"] = drivingLineMatch;
                    Require(Object.Equals(GetValue(drivingLineMatch, "matches"), true),
                        "The target Driving line/constraint did not survive Save Copy/reopen for " + fixture.Name + ".");
                }
                afterOracle.Add(direct);
                Require(matches.Count == 1, "Direct post-reopen oracle did not find exactly one projection in " + fixture.Name + ".");
            }
            foreach (ArcProjectionFixture fixture in arcFixtures)
            {
                Sketch reopenedSketch = FindSketch(reopened, fixture.SketchId);
                Curve reopenedSource = FindCurveByTagOrGeometry(reopened, fixture.SourceTag, fixture.SourceBefore);
                List<Curve> matches = NewSketchCurves(reopenedSketch, new HashSet<string>(StringComparer.Ordinal));
                List<Curve> arcs = new List<Curve>();
                foreach (Curve candidate in matches) if (candidate is Arc) arcs.Add(candidate);
                Dictionary<string, object> arcMatch = arcs.Count == 1
                    ? ArcOutputMatches((Arc)arcs[0], fixture.Oracle, frame)
                    : new Dictionary<string, object>(StringComparer.Ordinal) { { "matches", false } };
                Dictionary<string, object> direct = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "fixture", fixture.Name }, { "sketch_id", reopenedSketch.JournalIdentifier },
                    { "reopened_source", CurveRecord(reopenedSource) }, { "candidate_arc_count", arcs.Count },
                    { "candidate_arcs", CurveRecords(arcs.ToArray()) }, { "direct_arc_oracle_match", arcMatch }
                };
                afterOracle.Add(direct);
                Require(arcs.Count == 1 && Object.Equals(GetValue(arcMatch, "matches"), true),
                    "Direct post-reopen Arc oracle did not find exactly one preserved source-domain projection in " + fixture.Name + ".");
            }
            row["direct_oracle_after_reopen"] = afterOracle;
            row["passed"] = true;
        }
        catch (Exception ex)
        {
            row["passed"] = false;
            row["error"] = ex.GetType().FullName + ": " + ex.Message;
            AddDefect(report["defects"] as List<object>, "F10-05_save_copy_roundtrip",
                ex.GetType().FullName + ": " + ex.Message,
                "The adapter reports SAVED/reopened, its checks pass, direct output geometry matches, and associative Section input resolves to the reopened source.",
                new string[] { "Run the native line and partial Arc projection matrix.", "Save Copy to the reported unique -roundtrip.prt path with reopen=true.", "Re-resolve each target Sketch, source, and associative CPROJ feature by stable Tag or direct source geometry; inspect Section element data and compare direct projected geometry after reopen." },
                "Preserve the original model and report. Correct the save/reopen, source identity mapping, relation, or geometry persistence fault; rerun the same line, Arc-domain, Section relation, and direct-oracle checks on a new unique part and report.", row);
        }
    }

    private static Dictionary<string, object> DirectFixtureOracle(ProjectionFixture fixture)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "fixture", fixture.Name }, { "sketch_id", fixture.SketchId },
            { "projection_feature_id", fixture.ProjectionFeatureId }, { "projection_feature_tag", fixture.ProjectionFeatureTag },
            { "source_tag", fixture.SourceTag }, { "requested_source_journal_id", fixture.RequestedSourceId },
            { "source_before", fixture.SourceBefore },
            { "native_section_relation", fixture.NativeRelation },
            { "direct_output", CurveRecord(fixture.Output) },
            { "expected_start_mm", fixture.ExpectedStart }, { "expected_end_mm", fixture.ExpectedEnd }
        };
    }

    private static Dictionary<string, object> DirectArcFixtureOracle(ArcProjectionFixture fixture)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "fixture", fixture.Name }, { "sketch_id", fixture.SketchId },
            { "source_tag", fixture.SourceTag }, { "requested_source_journal_id", fixture.RequestedSourceId },
            { "source_before", fixture.SourceBefore }, { "direct_output", CurveRecord(fixture.Output) },
            { "independent_arc_oracle", fixture.Oracle }
        };
    }

    private static Sketch CreateTargetSketch(M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, Frame frame, string name)
    {
        M3SketchRequest request = new M3SketchRequest
        {
            RevisionId = binding.RevisionId,
            OriginMm = (double[])frame.Origin.Clone(),
            Normal = (double[])frame.Normal.Clone(),
            XAxis = (double[])frame.XAxis.Clone(),
            ConstraintMode = "geometry_only",
            Geometries = new List<M3SketchGeometryRequest>
            {
                new M3SketchGeometryRequest
                {
                    Id = name + "-seed", Type = "line",
                    StartMm = new double[] { -4.0, -4.0 }, EndMm = new double[] { -1.0, -4.0 }
                }
            }
        };
        M3PlanSnapshot plan = adapter.PrepareSketch(binding, request);
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        Require(execution != null && execution.State == "APPLIED",
            "Could not create the isolated F10 target Sketch " + name + ".");
        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "F10 target Sketch fixture did not pass production VerifyPlan: " + name);
        string sketchId = GetString(execution.Result, "sketch_id");
        Require(!String.IsNullOrWhiteSpace(sketchId), "F10 Sketch fixture omitted its JournalIdentifier.");
        Sketch sketch = FindSketch(part, sketchId);
        return sketch;
    }

    private static Dictionary<string, object> AddHorizontalDrivingTargetLine(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Session session, Part part, Sketch sketch, string label)
    {
        NXObject[] geometry = sketch.GetAllGeometry();
        Require(geometry != null && geometry.Length == 1 && geometry[0] is Line,
            "Driving target-line setup requires exactly one native seed Line in the target Sketch.");
        Line line = (Line)geometry[0];
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible,
            "F10 target Driving line constraint " + label);
        bool active = false;
        try
        {
            sketch.Activate(Sketch.ViewReorient.False);
            active = true;
            Point3d placement = new Point3d(
                (line.StartPoint.X + line.EndPoint.X) / 2.0,
                (line.StartPoint.Y + line.EndPoint.Y) / 2.0,
                (line.StartPoint.Z + line.EndPoint.Z) / 2.0);
            SketchDimensionalConstraint dimension = sketch.CreateDimension(Sketch.ConstraintType.HorizontalDim,
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.StartPoint, AssocValue = 0 },
                new Sketch.DimensionGeometry { Geometry = line, AssocType = Sketch.AssocType.EndPoint, AssocValue = 0 },
                placement, null, Sketch.DimensionOption.CreateAsDriving);
            Require(dimension != null && dimension.AssociatedExpression != null,
                "NX did not create the target Driving line expression.");
            double lengthMm = line.GetLength();
            dimension.AssociatedExpression.RightHandSide = lengthMm.ToString("0.########", CultureInfo.InvariantCulture);
            sketch.Update();
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            active = false;
            Require(session.UpdateManager.DoUpdate(mark) == 0,
                "NX update reported errors while creating the target Driving line constraint.");
            Require(dimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving,
                "The target preservation control is not a Driving dimension.");
            Dictionary<string, object> sessionInfo = adapter.GetSession(binding);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "line_tag", line.Tag.ToString() }, { "line_journal_id_at_setup", line.JournalIdentifier },
                { "line_start_world_mm", Point(line.StartPoint) }, { "line_end_world_mm", Point(line.EndPoint) },
                { "line_length_mm", lengthMm }, { "dimension_tag", dimension.Tag.ToString() },
                { "dimension_journal_id_at_setup", dimension.JournalIdentifier },
                { "dimension_state", dimension.DimensionState.ToString() },
                { "expression_name", dimension.AssociatedExpression.Name },
                { "expression_rhs", dimension.AssociatedExpression.RightHandSide },
                { "expression_value_mm", dimension.AssociatedExpression.Value },
                { "expression_unit", dimension.AssociatedExpression.Units == null
                    ? null : dimension.AssociatedExpression.Units.Symbol },
                { "revision_after_native_dimension", binding.RevisionId },
                { "session_readback", sessionInfo }
            };
        }
        finally
        {
            if (active && session.ActiveSketch != null && session.ActiveSketch.Tag.Equals(sketch.Tag))
                sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            session.DeleteUndoMark(mark, null);
        }
    }

    private static Dictionary<string, object> CheckHorizontalDrivingTargetLine(Sketch sketch,
        Dictionary<string, object> before)
    {
        string lineTag = GetString(before, "line_tag");
        string dimensionTag = GetString(before, "dimension_tag");
        Line line = null;
        foreach (NXObject item in sketch.GetAllGeometry())
            if (item is Line && String.Equals(item.Tag.ToString(), lineTag, StringComparison.Ordinal))
            {
                line = (Line)item;
                break;
            }
        SketchDimensionalConstraint dimension = null;
        foreach (SketchConstraint constraint in sketch.GetAllConstraintsOfType(
            Sketch.ConstraintClass.Any, Sketch.ConstraintType.HorizontalDim))
        {
            SketchDimensionalConstraint candidate = constraint as SketchDimensionalConstraint;
            if (candidate != null && String.Equals(candidate.Tag.ToString(), dimensionTag, StringComparison.Ordinal))
            {
                dimension = candidate;
                break;
            }
        }
        double[] expectedStart = ReadDoubleArray(before, "line_start_world_mm");
        double[] expectedEnd = ReadDoubleArray(before, "line_end_world_mm");
        double expectedLength = Convert.ToDouble(before["line_length_mm"], CultureInfo.InvariantCulture);
        double expectedValue = Convert.ToDouble(before["expression_value_mm"], CultureInfo.InvariantCulture);
        bool lineMatches = line != null &&
            Object.Equals(LineMatch(Point(line.StartPoint), Point(line.EndPoint), expectedStart, expectedEnd)["matches"], true) &&
            Math.Abs(line.GetLength() - expectedLength) <= PositionToleranceMm;
        bool driving = dimension != null && dimension.DimensionState == SketchDimensionalConstraint.DimensionStateType.Driving &&
            dimension.AssociatedExpression != null;
        double valueResidual = driving ? Math.Abs(dimension.AssociatedExpression.Value - expectedValue) : Double.PositiveInfinity;
        string unit = driving && dimension.AssociatedExpression.Units != null ? dimension.AssociatedExpression.Units.Symbol : null;
        bool expressionMatches = driving && valueResidual <= PositionToleranceMm &&
            String.Equals(unit, GetString(before, "expression_unit"), StringComparison.Ordinal);
        bool referencesLine = false;
        if (dimension != null)
        {
            Sketch.DimensionGeometry[] operands = dimension.GetDimensionGeometry();
            if (operands != null)
                foreach (Sketch.DimensionGeometry operand in operands)
                    if (operand.Geometry != null && String.Equals(operand.Geometry.Tag.ToString(), lineTag, StringComparison.Ordinal))
                        referencesLine = true;
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "matches", lineMatches && driving && expressionMatches && referencesLine },
            { "line_found_by_stable_tag", line != null }, { "line_geometry_matches", lineMatches },
            { "dimension_found_by_stable_tag", dimension != null },
            { "dimension_state", driving ? dimension.DimensionState.ToString() : null },
            { "expression_value_mm", driving ? (object)dimension.AssociatedExpression.Value : null },
            { "expression_residual_mm", valueResidual }, { "expression_unit", unit },
            { "dimension_references_line_tag", referencesLine },
            { "line_record_after", line == null ? null : CurveRecord(line) }
        };
    }

    private static Line CreateSpatialSource(Part part, Frame frame)
    {
        double[] start = Add(Add(Add(frame.Origin, Scale(frame.XAxis, 2.0)), Scale(frame.YAxis, 3.0)), Scale(frame.Normal, 8.0));
        double[] end = Add(Add(Add(frame.Origin, Scale(frame.XAxis, 10.0)), Scale(frame.YAxis, 6.0)), Scale(frame.Normal, 11.0));
        return part.Curves.CreateLine(ToPoint(start), ToPoint(end));
    }

    private static Line CreateNonSourceSpatialCurve(Part part, Frame frame)
    {
        double[] start = Add(Add(Add(frame.Origin, Scale(frame.XAxis, -18.0)), Scale(frame.YAxis, 9.0)), Scale(frame.Normal, 13.0));
        double[] end = Add(Add(Add(frame.Origin, Scale(frame.XAxis, -11.0)), Scale(frame.YAxis, 12.0)), Scale(frame.Normal, 6.0));
        return part.Curves.CreateLine(ToPoint(start), ToPoint(end));
    }

    private static Ellipse CreateNonSourceSpatialEllipse(Part part, Frame frame)
    {
        double[] center = Add(Add(Add(frame.Origin, Scale(frame.XAxis, 23.0)),
            Scale(frame.YAxis, 14.0)), Scale(frame.Normal, 7.0));
        return part.Curves.CreateEllipse(ToPoint(center), ToVector(frame.XAxis),
            ToVector(frame.YAxis), 8.0, 3.0, 0.0, TwoPi);
    }

    private static Arc CreateMatcherComplementArc(Part part, Frame frame)
    {
        double[] center = Add(Add(Add(frame.Origin, Scale(frame.XAxis, 16.0)),
            Scale(frame.YAxis, 15.0)), Scale(frame.Normal, 4.0));
        Arc complement = part.Curves.CreateArc(ToPoint(center), ToVector(frame.XAxis), ToVector(frame.YAxis),
            1.75, Math.PI, 2.0 * Math.PI);
        Arc correct = part.Curves.CreateArc(ToPoint(center), ToVector(frame.XAxis), ToVector(frame.YAxis),
            1.75, 0.0, Math.PI);
        correct.SetName("F10_MATCHER_CORRECT");
        Arc reverseEquivalent = part.Curves.CreateArc(ToPoint(center), ToVector(Scale(frame.XAxis, -1.0)),
            ToVector(frame.YAxis), 1.75, 0.0, Math.PI);
        reverseEquivalent.SetName("F10_MATCHER_REVERSE_EQUIVALENT");
        return complement;
    }

    private static List<ArcFixture> CreateArcSourceMatrix(Part part, Frame frame,
        Dictionary<string, object> report)
    {
        List<ArcFixture> result = new List<ArcFixture>();
        TryCreateArcFixture(result, report, part, frame, "semicircle_180deg",
            new double[] { 4.0, 8.0, 6.0 }, 2.5, 0.0, Math.PI, frame.YAxis);
        TryCreateArcFixture(result, report, part, frame, "cross_zero_minor",
            new double[] { 9.0, 7.0, 8.0 }, 1.7, 5.5, 2.0 * Math.PI + 0.5, frame.YAxis);
        TryCreateArcFixture(result, report, part, frame, "major_sweep",
            new double[] { 12.0, 4.0, 5.0 }, 3.1, 0.35, 5.55, frame.YAxis);
        TryCreateArcFixture(result, report, part, frame, "negative_signed_sweep",
            new double[] { -8.0, 9.0, 7.0 }, 2.2, 1.35, 0.25, frame.YAxis);
        TryCreateArcFixture(result, report, part, frame, "reverse_plane_signed_sweep",
            new double[] { -12.0, -4.0, 5.0 }, 2.4, 0.3, 1.9, Scale(frame.YAxis, -1.0));
        return result;
    }

    private static void TryCreateArcFixture(List<ArcFixture> output,
        Dictionary<string, object> report, Part part, Frame frame, string name,
        double[] centerLocal, double radius, double startAngle, double endAngle,
        double[] axisY)
    {
        try
        {
            double[] center = Add(Add(Add(frame.Origin, Scale(frame.XAxis, centerLocal[0])),
                Scale(frame.YAxis, centerLocal[1])), Scale(frame.Normal, centerLocal[2]));
            Arc source = part.Curves.CreateArc(ToPoint(center), ToVector(frame.XAxis),
                ToVector(axisY), radius, startAngle, endAngle);
            output.Add(new ArcFixture { Name = name, Source = source });
        }
        catch (Exception ex)
        {
            AddDefect(report, "F10-arc-fixture-" + name,
                ex.GetType().FullName + ": " + ex.Message,
                "NX should create a native circular Arc fixture for the recorded signed or crossing domain before projection.",
                new string[] {
                    "Create the unique isolated millimetre part.",
                    "Call Part.Curves.CreateArc with the recorded center frame, axes, radius, start angle, and end angle.",
                    "Capture the exact NX exception before any projection Prepare or Execute." },
                "Preserve this run's evidence; on a new part, confirm the NXCreateArc domain accepted for this case and update the fixture only from recorded native API behavior.");
        }
    }

    private static List<object> ArcFixtureRecords(List<ArcFixture> fixtures, Frame frame)
    {
        List<object> result = new List<object>();
        foreach (ArcFixture fixture in fixtures)
        {
            Point3d center; Vector3d axisX, axisY;
            fixture.Source.GetOrientation(out center, out axisX, out axisY);
            result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", fixture.Name }, { "source", CurveRecord(fixture.Source) },
                { "start_angle_rad", fixture.Source.StartAngle }, { "end_angle_rad", fixture.Source.EndAngle },
                { "raw_parameter_delta_rad", fixture.Source.EndAngle - fixture.Source.StartAngle },
                { "interpreted_signed_sweep_rad", ArcDirectedSweep(fixture.Source) },
                { "plane_normal_world", Normalize3(Cross(Vector(axisX), Vector(axisY))) },
                { "center_target_local_mm", WorldToLocal(frame, Point(center)) }
            });
        }
        return result;
    }

    private static M3SketchProjectionRequest Request(M2ModelBinding binding, string sketchId,
        List<string> sourceIds, double[] direction, bool associative)
    {
        return new M3SketchProjectionRequest
        {
            RevisionId = binding.RevisionId, SketchId = sketchId,
            SourceCurveIds = sourceIds, DirectionXYZ = direction == null ? null : (double[])direction.Clone(),
            Associative = associative
        };
    }

    private static Dictionary<string, object> NativeSnapshot(Part part, Session session,
        Sketch target, Curve source)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "part_tag", part.Tag.ToString() }, { "part_path", part.FullPath },
            { "part_units", part.PartUnits.ToString() },
            { "active_sketch_tag", session.ActiveSketch == null ? null : session.ActiveSketch.Tag.ToString() },
            { "all_part_curves", CurveRecords(part.Curves.ToArray()) },
            { "features", FeatureRecords(part) }, { "bodies", BodyRecords(part) },
            { "points", PointRecords(part) }, { "axes", AxisRecords(part) },
            { "directions", DirectionRecords(part) }, { "expressions", ExpressionRecords(part) },
            { "sketches", SketchRecords(part) }, { "source", CurveRecord(source) },
            { "target_sketch_tag", target == null ? null : target.Tag.ToString() }
        };
        return result;
    }

    private static List<object> SketchRecords(Part part)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (Sketch sketch in part.Sketches.ToArray())
        {
            if (sketch == null) throw new InvalidOperationException("Part sketch inventory contains null.");
            rows.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", sketch.Tag.ToString() }, { "journal_id", sketch.JournalIdentifier },
                { "origin_mm", Point(sketch.Origin) }, { "geometry", SketchGeometryRecords(sketch) },
                { "feature_id", sketch.Feature == null ? null : sketch.Feature.JournalIdentifier },
                { "constraints", ConstraintRecords(sketch) }, { "reference_status", ReferenceStatusRecords(sketch) }
            });
        }
        rows.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
        { return StringComparer.Ordinal.Compare(GetString(a, "tag"), GetString(b, "tag")); });
        return new List<object>(rows.ToArray());
    }

    private static Dictionary<string, object> DirectSketchRecord(Sketch sketch)
    {
        if (sketch == null) return null;
        Matrix3x3 axes = sketch.Orientation.Element;
        Frame frame = MakeFrame(Point(sketch.Origin),
            new double[] { axes.Zx, axes.Zy, axes.Zz },
            new double[] { axes.Xx, axes.Xy, axes.Xz });
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", sketch.Tag.ToString() }, { "journal_id", sketch.JournalIdentifier },
            { "origin_mm", frame.Origin }, { "normal_world", frame.Normal },
            { "x_axis_world", frame.XAxis }, { "y_axis_world", frame.YAxis },
            { "feature_tag", sketch.Feature == null ? null : sketch.Feature.Tag.ToString() },
            { "feature_journal_id", sketch.Feature == null ? null : sketch.Feature.JournalIdentifier },
            { "geometry", SketchGeometryRecords(sketch) },
            { "constraints", ConstraintRecords(sketch) },
            { "reference_status", ReferenceStatusRecords(sketch) }
        };
    }

    private static List<object> FeatureRecords(Part part)
    {
        Feature[] features = part.Features.GetFeatures();
        if (features == null) throw new InvalidOperationException("Part feature inventory returned null.");
        return FeatureRecords(features);
    }

    private static List<object> FeatureRecords(Feature[] features)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        if (features == null) throw new InvalidOperationException("Part feature inventory returned null.");
        foreach (Feature feature in features)
        {
            if (feature == null) throw new InvalidOperationException("Part feature inventory contains null.");
            rows.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", feature.Tag.ToString() }, { "journal_id", feature.JournalIdentifier },
                { "type", feature.GetType().Name },
                { "parent_ids", FeatureRelationIds(feature.GetParents(), "parents") },
                { "child_ids", FeatureRelationIds(feature.GetChildren(), "children") }
            });
        }
        rows.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
        { return StringComparer.Ordinal.Compare(GetString(a, "tag"), GetString(b, "tag")); });
        return new List<object>(rows.ToArray());
    }

    private static HashSet<string> FeatureTagsFromSnapshot(Dictionary<string, object> snapshot)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        List<object> rows = snapshot == null ? null : snapshot["features"] as List<object>;
        if (rows == null) throw new InvalidOperationException("Direct pre-operation snapshot omitted its feature inventory.");
        foreach (object value in rows)
        {
            Dictionary<string, object> feature = value as Dictionary<string, object>;
            string tag = GetString(feature, "tag");
            if (String.IsNullOrWhiteSpace(tag) || !result.Add(tag))
                throw new InvalidOperationException("Direct feature inventory contains an empty or duplicate Tag.");
        }
        return result;
    }

    private static List<Feature> NewFeatures(Part part, HashSet<string> beforeTags)
    {
        List<Feature> result = new List<Feature>();
        Feature[] features = part.Features.GetFeatures();
        if (features == null) throw new InvalidOperationException("Part feature inventory returned null after projection.");
        foreach (Feature feature in features)
            if (feature != null && !beforeTags.Contains(feature.Tag.ToString())) result.Add(feature);
        return result;
    }

    private static Feature FindFeature(Part part, string journalId)
    {
        foreach (Feature feature in part.Features.GetFeatures())
            if (feature != null && String.Equals(feature.JournalIdentifier, journalId, StringComparison.Ordinal)) return feature;
        throw new InvalidOperationException("Native projection feature JournalIdentifier was not found after reopen: " + journalId);
    }

    private static Curve FindCurve(Part part, string journalId)
    {
        foreach (Curve curve in part.Curves.ToArray())
            if (curve != null && String.Equals(curve.JournalIdentifier, journalId, StringComparison.Ordinal)) return curve;
        throw new InvalidOperationException("Native source curve JournalIdentifier was not found after reopen: " + journalId);
    }

    private static Curve FindCurveByTag(Part part, string tag)
    {
        Curve match = null;
        foreach (Curve curve in part.Curves.ToArray())
        {
            if (curve == null || !String.Equals(curve.Tag.ToString(), tag, StringComparison.Ordinal)) continue;
            if (match != null) throw new InvalidOperationException("Native Part.Curves inventory reused Tag " + tag + ".");
            match = curve;
        }
        if (match == null) throw new InvalidOperationException("Native source curve Tag was not found: " + tag);
        return match;
    }

    private static Curve FindCurveByTagOrGeometry(Part part, string tag,
        Dictionary<string, object> expectedGeometry)
    {
        foreach (Curve curve in part.Curves.ToArray())
            if (curve != null && String.Equals(curve.Tag.ToString(), tag, StringComparison.Ordinal))
            {
                Require(CurveMatchesRecordGeometry(curve, expectedGeometry),
                    "A re-resolved source Tag exists but its direct geometry differs from the retained source geometry.");
                return curve;
            }
        List<Curve> candidates = new List<Curve>();
        foreach (Curve curve in part.Curves.ToArray())
            if (curve != null && CurveMatchesRecordGeometry(curve, expectedGeometry)) candidates.Add(curve);
        Require(candidates.Count == 1,
            "Source Tag changed and direct geometry did not resolve one unique source curve after reopen.");
        return candidates[0];
    }

    private static bool CurveMatchesRecordGeometry(Curve curve, Dictionary<string, object> record)
    {
        if (curve == null || record == null || !String.Equals(curve.GetType().Name,
            GetString(record, "type"), StringComparison.Ordinal)) return false;
        Line line = curve as Line;
        if (line != null)
        {
            double[] start = ReadDoubleArray(record, "start_mm");
            double[] end = ReadDoubleArray(record, "end_mm");
            return start != null && end != null && Object.Equals(LineMatch(Point(line.StartPoint),
                Point(line.EndPoint), start, end)["matches"], true);
        }
        Arc arc = curve as Arc;
        if (arc != null)
        {
            double[] center = ReadDoubleArray(record, "center_mm");
            object radiusValue = GetValue(record, "radius_mm");
            object lengthValue = GetValue(record, "length_mm");
            if (center == null || !(radiusValue is double) || !(lengthValue is double)) return false;
            Point3d actualCenter; Vector3d axisX, axisY;
            arc.GetOrientation(out actualCenter, out axisX, out axisY);
            return Distance(Point(actualCenter), center) <= PositionToleranceMm &&
                Math.Abs(arc.Radius - (double)radiusValue) <= PositionToleranceMm &&
                Math.Abs(arc.GetLength() - (double)lengthValue) <= PositionToleranceMm;
        }
        return false;
    }

    private static Feature FindFeatureByTagOrJournalId(Part part, string tag, string journalId)
    {
        foreach (Feature feature in part.Features.GetFeatures())
            if (feature != null && String.Equals(feature.Tag.ToString(), tag, StringComparison.Ordinal)) return feature;
        foreach (Feature feature in part.Features.GetFeatures())
            if (feature != null && String.Equals(feature.JournalIdentifier, journalId, StringComparison.Ordinal)) return feature;
        throw new InvalidOperationException("Associative projection feature could not be re-resolved by Tag or JournalIdentifier after reopen.");
    }

    private static Curve ResolveRelationSource(Part part, Dictionary<string, object> relation,
        Dictionary<string, object> expectedGeometry)
    {
        List<object> inputs = relation == null ? null : relation["input_elements"] as List<object>;
        Require(inputs != null && inputs.Count == 1, "Section relation readback did not return exactly one explicit source element.");
        Dictionary<string, object> input = inputs[0] as Dictionary<string, object>;
        string tag = GetString(input, "input_tag");
        string jid = GetString(input, "input_journal_id");
        Curve source = null;
        foreach (Curve curve in part.Curves.ToArray())
            if (curve != null && String.Equals(curve.Tag.ToString(), tag, StringComparison.Ordinal)) { source = curve; break; }
        if (source == null)
            foreach (Curve curve in part.Curves.ToArray())
                if (curve != null && String.Equals(curve.JournalIdentifier, jid, StringComparison.Ordinal)) { source = curve; break; }
        if (source == null) source = FindCurveByTagOrGeometry(part, tag, expectedGeometry);
        Require(CurveMatchesRecordGeometry(source, expectedGeometry),
            "Section relation Tag/JID resolves to a curve whose direct source geometry is not preserved.");
        Require(String.Equals(source.JournalIdentifier, jid, StringComparison.Ordinal),
            "Section element-data JournalIdentifier does not resolve to the current source selected by its Tag.");
        return source;
    }

    // Independent relation readback: the output curve Tag and Feature.GetParents
    // are not treated as source identity. Resolve the selected source from the
    // projection feature's native Section element-data record.
    private static Dictionary<string, object> ReadProjectionRelation(Part part, Feature feature)
    {
        if (part == null || feature == null) throw new ArgumentNullException("part/feature");
        SketchProjectBuilder builder = null;
        try
        {
            builder = part.Sketches.CreateProjectBuilder(feature);
            if (builder == null) throw new InvalidOperationException("NX returned no SketchProjectBuilder for the committed projection feature.");
            Section section = builder.Section;
            if (section == null) throw new InvalidOperationException("Projection feature builder has no input Section.");
            SectionData[] sectionData;
            section.GetSectionData(out sectionData);
            if (sectionData == null) throw new InvalidOperationException("Section.GetSectionData returned null.");
            List<object> inputElements = new List<object>();
            for (int sectionIndex = 0; sectionIndex < sectionData.Length; sectionIndex++)
            {
                SectionElementData[] elements;
                sectionData[sectionIndex].GetSectionElementsData(out elements);
                if (elements == null) throw new InvalidOperationException("SectionData.GetSectionElementsData returned null.");
                for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
                {
                    DisplayableObject selected = null;
                    DisplayableObject startConnector = null;
                    DisplayableObject endConnector = null;
                    Point3d startPoint = new Point3d();
                    Point3d endPoint = new Point3d();
                    elements[elementIndex].GetSectionElementData1(out selected, out startConnector,
                        out startPoint, out endConnector, out endPoint);
                    inputElements.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "section_index", sectionIndex }, { "element_index", elementIndex },
                        { "input_tag", selected == null ? null : selected.Tag.ToString() },
                        { "input_journal_id", selected == null ? null : selected.JournalIdentifier },
                        { "input_type", selected == null ? null : selected.GetType().Name },
                        { "start_point_mm", Point(startPoint) }, { "end_point_mm", Point(endPoint) },
                        { "start_connector_tag", startConnector == null ? null : startConnector.Tag.ToString() },
                        { "end_connector_tag", endConnector == null ? null : endConnector.Tag.ToString() }
                    });
                }
            }
            NXObject[] outputCurves;
            section.GetOutputCurves(out outputCurves);
            List<object> outputs = new List<object>();
            if (outputCurves != null)
                foreach (NXObject output in outputCurves)
                    outputs.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "tag", output == null ? null : output.Tag.ToString() },
                        { "journal_id", output == null ? null : output.JournalIdentifier },
                        { "type", output == null ? null : output.GetType().Name }
                    });
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "relation_evidence", "Section.GetSectionData/GetSectionElementsData/GetSectionElementData1 input object identity" },
                { "projection_feature_tag", feature.Tag.ToString() },
                { "projection_feature_journal_id", feature.JournalIdentifier },
                { "builder_associativity_readback", builder.Associativity },
                { "builder_project_as_dumb_fixed_curves_readback", builder.ProjectAsDumbFixedCurves },
                { "builder_curve_type_readback", builder.CurveType.ToString() },
                { "builder_tolerance_readback", builder.Tolerance },
                { "section_count", sectionData.Length }, { "input_elements", inputElements },
                { "section_internal_generated_curves", outputs },
                { "section_internal_curves_used_as_target_output_oracle", false },
                { "feature_parents_are_source_oracle", false }
            };
        }
        finally { if (builder != null) builder.Destroy(); }
    }

    private static bool RelationMatchesSource(Dictionary<string, object> relation, NXObject source)
    {
        if (relation == null || source == null) return false;
        List<object> inputs = relation["input_elements"] as List<object>;
        if (inputs == null || inputs.Count != 1) return false;
        Dictionary<string, object> input = inputs[0] as Dictionary<string, object>;
        return input != null && String.Equals(GetString(input, "input_tag"), source.Tag.ToString(), StringComparison.Ordinal) &&
            String.Equals(GetString(input, "input_journal_id"), source.JournalIdentifier, StringComparison.Ordinal);
    }

    private static List<object> BodyRecords(Part part)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (Body body in part.Bodies.ToArray())
        {
            if (body == null) throw new InvalidOperationException("Part body inventory contains null.");
            rows.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", body.Tag.ToString() }, { "journal_id", body.JournalIdentifier },
                { "type", body.GetType().Name }
            });
        }
        rows.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
        { return StringComparer.Ordinal.Compare(GetString(a, "tag"), GetString(b, "tag")); });
        return new List<object>(rows.ToArray());
    }

    private static List<object> PointRecords(Part part)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (Point point in part.Points.ToArray())
        {
            if (point == null) throw new InvalidOperationException("Part Point inventory contains null.");
            rows.Add(PointObjectRecord(point));
        }
        return SortedObjectRows(rows);
    }

    private static List<object> AxisRecords(Part part)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (Axis axis in part.Axes.ToArray())
        {
            if (axis == null) throw new InvalidOperationException("Part Axis inventory contains null.");
            rows.Add(AxisObjectRecord(axis));
        }
        return SortedObjectRows(rows);
    }

    private static List<object> DirectionRecords(Part part)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (Direction direction in part.Directions.ToArray())
        {
            if (direction == null) throw new InvalidOperationException("Part Direction inventory contains null.");
            rows.Add(DirectionObjectRecord(direction));
        }
        return SortedObjectRows(rows);
    }

    private static List<object> ExpressionRecords(Part part)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        Expression[] expressions = part.Expressions.ToArray();
        if (expressions == null) throw new InvalidOperationException("Part expression inventory returned null.");
        foreach (Expression expression in expressions)
        {
            if (expression == null) throw new InvalidOperationException("Part expression inventory contains null.");
            rows.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", expression.Tag.ToString() }, { "journal_id", expression.JournalIdentifier },
                { "name", expression.Name }, { "right_hand_side", expression.RightHandSide },
                { "value", expression.Value }, { "status", expression.Status.ToString() },
                { "unit", expression.Units == null ? null : expression.Units.Symbol }
            });
        }
        rows.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
        { return StringComparer.Ordinal.Compare(GetString(a, "name"), GetString(b, "name")); });
        return new List<object>(rows.ToArray());
    }

    private static Dictionary<string, object> PointObjectRecord(Point point)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", point.Tag.ToString() }, { "journal_id", point.JournalIdentifier },
            { "coordinates_mm", Point(point.Coordinates) }, { "is_reference", point.IsReference }
        };
    }

    private static Dictionary<string, object> AxisObjectRecord(Axis axis)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", axis.Tag.ToString() }, { "journal_id", axis.JournalIdentifier },
            { "origin_mm", Point(axis.Origin) }, { "direction", Vector(axis.DirectionVector) },
            { "axis_type", axis.Type.ToString() }
        };
    }

    private static Dictionary<string, object> DirectionObjectRecord(Direction direction)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", direction.Tag.ToString() }, { "journal_id", direction.JournalIdentifier },
            { "origin_mm", Point(direction.Origin) }, { "vector", Vector(direction.Vector) },
            { "sense", direction.Sense.ToString() }
        };
    }

    private static List<object> ConstraintRecords(Sketch sketch)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (Sketch.ConstraintType type in Enum.GetValues(typeof(Sketch.ConstraintType)))
        {
            if (type == Sketch.ConstraintType.NoCon || type == Sketch.ConstraintType.LastConType) continue;
            SketchConstraint[] constraints = sketch.GetAllConstraintsOfType(Sketch.ConstraintClass.Any, type);
            if (constraints == null) continue;
            foreach (SketchConstraint constraint in constraints)
            {
                if (constraint == null) throw new InvalidOperationException("Sketch constraint inventory contains null.");
                Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "tag", constraint.Tag.ToString() }, { "journal_id", constraint.JournalIdentifier },
                    { "constraint_type", type.ToString() }, { "class", constraint.GetType().Name }
                };
                List<object> operands = new List<object>();
                SketchDimensionalConstraint dimension = constraint as SketchDimensionalConstraint;
                if (dimension != null)
                {
                    Expression expression = dimension.AssociatedExpression;
                    record["dimension_state"] = dimension.DimensionState.ToString();
                    record["expression_name"] = expression == null ? null : expression.Name;
                    record["right_hand_side"] = expression == null ? null : expression.RightHandSide;
                    record["value"] = expression == null ? null : (object)expression.Value;
                    record["unit"] = expression == null || expression.Units == null ? null : expression.Units.Symbol;
                    Sketch.DimensionGeometry[] dimensionGeometry = dimension.GetDimensionGeometry();
                    if (dimensionGeometry != null)
                        foreach (Sketch.DimensionGeometry operand in dimensionGeometry)
                            operands.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                { "geometry_tag", operand.Geometry == null ? null : operand.Geometry.Tag.ToString() },
                                { "geometry_journal_id", operand.Geometry == null ? null : operand.Geometry.JournalIdentifier },
                                { "association", operand.AssocType.ToString() }, { "association_value", operand.AssocValue }
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
                                    { "geometry_journal_id", operand.Geometry == null ? null : operand.Geometry.JournalIdentifier },
                                    { "point_type", operand.PointType.ToString() },
                                    { "spline_defining_point_index", operand.SplineDefiningPointIndex }
                                });
                    }
                }
                record["operands"] = operands;
                rows.Add(record);
            }
        }
        return SortedObjectRows(rows);
    }

    private static List<object> ReferenceStatusRecords(Sketch sketch)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        UFSession uf = UFSession.GetUFSession();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve;
            if (curve == null) continue;
            Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", curve.Tag.ToString() }, { "journal_id", curve.JournalIdentifier }
            };
            try
            {
                UFSket.ReferenceStatus status;
                uf.Sket.AskReferenceStatus(sketch.Tag, curve.Tag, out status);
                row["status"] = status.ToString();
                row["status_value"] = Convert.ToInt32(status, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { row["read_error"] = ex.GetType().FullName + ": " + ex.Message; }
            rows.Add(row);
        }
        return SortedObjectRows(rows);
    }

    private static List<object> FeatureRelationIds(Feature[] features, string label)
    {
        if (features == null) throw new InvalidOperationException("Feature " + label + " inventory returned null.");
        List<string> ids = new List<string>();
        foreach (Feature feature in features)
        {
            if (feature == null) throw new InvalidOperationException("Feature " + label + " inventory contains null.");
            ids.Add(feature.JournalIdentifier);
        }
        ids.Sort(StringComparer.Ordinal);
        List<object> result = new List<object>();
        foreach (string id in ids) result.Add(id);
        return result;
    }

    private static List<object> SortedObjectRows(List<Dictionary<string, object>> rows)
    {
        rows.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
        { return StringComparer.Ordinal.Compare(GetString(a, "tag"), GetString(b, "tag")); });
        return new List<object>(rows.ToArray());
    }

    private static List<object> SketchGeometryRecords(Sketch sketch)
    {
        List<Curve> curves = new List<Curve>();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve;
            if (curve != null) curves.Add(curve);
        }
        return CurveRecords(curves.ToArray());
    }

    private static List<object> CurveRecords(Curve[] curves)
    {
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        foreach (Curve curve in curves)
        {
            if (curve == null) throw new InvalidOperationException("Curve inventory contains null.");
            rows.Add(CurveRecord(curve));
        }
        rows.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
        { return StringComparer.Ordinal.Compare(GetString(a, "tag"), GetString(b, "tag")); });
        return new List<object>(rows.ToArray());
    }

    private static Dictionary<string, object> CurveRecord(Curve curve)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", curve.Tag.ToString() }, { "journal_id", curve.JournalIdentifier },
            { "type", curve.GetType().Name }, { "length_mm", curve.GetLength() }
        };
        Line line = curve as Line;
        if (line != null)
        {
            result["start_mm"] = Point(line.StartPoint);
            result["end_mm"] = Point(line.EndPoint);
        }
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center; Vector3d axisX, axisY;
            arc.GetOrientation(out center, out axisX, out axisY);
            result["center_mm"] = Point(center);
            result["radius_mm"] = arc.Radius;
            result["start_angle_rad"] = arc.StartAngle;
            result["end_angle_rad"] = arc.EndAngle;
            result["axis_x"] = Vector(axisX);
            result["axis_y"] = Vector(axisY);
        }
        Ellipse ellipse = curve as Ellipse;
        if (ellipse != null)
        {
            double majorRadius, minorRadius, startAngle, endAngle, rotationAngle;
            Point3d center;
            ellipse.GetParameters(out majorRadius, out minorRadius, out startAngle,
                out endAngle, out rotationAngle, out center);
            Point3d orientationCenter;
            Vector3d axisX, axisY;
            ellipse.GetOrientation(out orientationCenter, out axisX, out axisY);
            result["center_mm"] = Point(center);
            result["major_radius_mm"] = majorRadius;
            result["minor_radius_mm"] = minorRadius;
            result["start_angle_rad"] = startAngle;
            result["end_angle_rad"] = endAngle;
            result["rotation_rad"] = rotationAngle;
            result["plane_axis_x"] = Vector(axisX);
            result["plane_axis_y"] = Vector(axisY);
        }
        return result;
    }

    private static Dictionary<string, object> ArcOracle(Arc source, Frame frame, double[] direction)
    {
        Point3d center; Vector3d axisX, axisY;
        source.GetOrientation(out center, out axisX, out axisY);
        double sweep = ArcDirectedSweep(source);
        double planeSense = Math.Sign(Dot(Cross(Vector(axisX), Vector(axisY)), frame.Normal));
        double signedSweep = sweep * planeSense;
        double startAngle = source.StartAngle;
        double endAngle = startAngle + sweep;
        double[] centerProjected = ProjectPoint(Point(center), frame.Origin, frame.Normal, direction);
        double[] startProjected = ProjectPoint(Point(ArcPoint(source, startAngle)), frame.Origin, frame.Normal, direction);
        double[] endProjected = ProjectPoint(Point(ArcPoint(source, endAngle)), frame.Origin, frame.Normal, direction);
        double[] midpointProjected = ProjectPoint(Point(ArcPoint(source, startAngle + sweep / 2.0)),
            frame.Origin, frame.Normal, direction);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "source_raw_start_angle_rad", source.StartAngle },
            { "source_raw_end_angle_rad", source.EndAngle },
            { "source_raw_parameter_delta_rad", source.EndAngle - source.StartAngle },
            { "source_interpreted_signed_sweep_rad", sweep },
            { "expected_signed_sweep_relative_to_target_normal_rad", signedSweep },
            { "expected_sweep_rad", Math.Abs(sweep) },
            { "source_plane_normal_world", Normalize3(Cross(Vector(axisX), Vector(axisY))) },
            { "source_center_world_mm", Point(center) },
            { "expected_center_world_mm", centerProjected },
            { "expected_center_target_local_mm", WorldToLocal(frame, centerProjected) },
            { "radius_mm", source.Radius },
            { "expected_start_world_mm", startProjected },
            { "expected_end_world_mm", endProjected },
            { "expected_interior_midpoint_world_mm", midpointProjected }
        };
    }

    private static Dictionary<string, object> ArcOutputMatches(Arc output,
        Dictionary<string, object> oracle, Frame frame)
    {
        Point3d center; Vector3d axisX, axisY;
        output.GetOrientation(out center, out axisX, out axisY);
        double[] expectedCenter = ReadDoubleArray(oracle, "expected_center_world_mm");
        double[] expectedLocalCenter = ReadDoubleArray(oracle, "expected_center_target_local_mm");
        double[] expectedStart = ReadDoubleArray(oracle, "expected_start_world_mm");
        double[] expectedEnd = ReadDoubleArray(oracle, "expected_end_world_mm");
        double[] expectedMidpoint = ReadDoubleArray(oracle, "expected_interior_midpoint_world_mm");
        double expectedRadius = Convert.ToDouble(oracle["radius_mm"], CultureInfo.InvariantCulture);
        double expectedSweep = Convert.ToDouble(oracle["expected_sweep_rad"], CultureInfo.InvariantCulture);
        double expectedSignedSweep = Convert.ToDouble(oracle["expected_signed_sweep_relative_to_target_normal_rad"], CultureInfo.InvariantCulture);
        double outputSweep = Math.Abs(ArcDirectedSweep(output));
        double outputPlaneSign = Math.Sign(Dot(Cross(Vector(axisX), Vector(axisY)), frame.Normal));
        double outputSignedSweep = ArcDirectedSweep(output) * outputPlaneSign;
        double[] outputStart = Point(ArcPoint(output, output.StartAngle));
        double[] outputEnd = Point(ArcPoint(output, output.StartAngle + ArcDirectedSweep(output)));
        Dictionary<string, object> endpointMatch = LineMatch(outputStart, outputEnd, expectedStart, expectedEnd);
        Dictionary<string, object> midpointMembership = ArcContainsPoint(output, expectedMidpoint);
        double centerResidualMm = Distance(Point(center), expectedCenter);
        double localCenterResidualMm = Distance(WorldToLocal(frame, Point(center)), expectedLocalCenter);
        double radiusResidualMm = Math.Abs(output.Radius - expectedRadius);
        double sweepResidualRad = Math.Abs(outputSweep - expectedSweep);
        double signedSweepResidualRad = Math.Abs(outputSignedSweep - expectedSignedSweep);
        double planeNormalAlignment = Math.Abs(Dot(Normalize3(Cross(Vector(axisX), Vector(axisY))), frame.Normal));
        bool matches = centerResidualMm <= PositionToleranceMm && localCenterResidualMm <= PositionToleranceMm &&
            radiusResidualMm <= PositionToleranceMm && sweepResidualRad <= DirectionTolerance &&
            signedSweepResidualRad <= DirectionTolerance && planeNormalAlignment >= 1.0 - DirectionTolerance &&
            Object.Equals(endpointMatch["matches"], true) && Object.Equals(midpointMembership["contains"], true);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "matches", matches }, { "output_curve", CurveRecord(output) },
            { "expected_center_target_local_mm", expectedLocalCenter },
            { "actual_center_target_local_mm", WorldToLocal(frame, Point(center)) },
            { "center_residual_mm", centerResidualMm }, { "local_center_residual_mm", localCenterResidualMm },
            { "radius_residual_mm", radiusResidualMm }, { "expected_radius_mm", expectedRadius },
            { "actual_sweep_rad", outputSweep }, { "expected_sweep_rad", expectedSweep },
            { "sweep_residual_rad", sweepResidualRad },
            { "actual_signed_sweep_relative_to_target_normal_rad", outputSignedSweep },
            { "expected_signed_sweep_relative_to_target_normal_rad", expectedSignedSweep },
            { "signed_sweep_residual_rad", signedSweepResidualRad },
            { "plane_normal_alignment", planeNormalAlignment },
            { "endpoint_match", endpointMatch }, { "interior_midpoint_membership", midpointMembership },
            { "position_tolerance_mm", PositionToleranceMm }, { "angle_tolerance_rad", DirectionTolerance }
        };
    }

    private static Dictionary<string, object> ArcContainsPoint(Arc arc, double[] point)
    {
        Point3d center; Vector3d axisX, axisY;
        arc.GetOrientation(out center, out axisX, out axisY);
        double[] delta = Subtract(point, Point(center));
        double[] x = Normalize3(Vector(axisX));
        double[] y = Normalize3(Vector(axisY));
        double[] normal = Normalize3(Cross(x, y));
        double localX = Dot(delta, x);
        double localY = Dot(delta, y);
        double radialResidualMm = Math.Abs(Math.Sqrt(localX * localX + localY * localY) - arc.Radius);
        double planeResidualMm = Math.Abs(Dot(delta, normal));
        double theta = PositiveAngle(Math.Atan2(localY, localX));
        double signedSweep = ArcDirectedSweep(arc);
        double start = PositiveAngle(arc.StartAngle);
        double deltaAngle = signedSweep >= 0.0 ? PositiveAngle(theta - start) : PositiveAngle(start - theta);
        bool withinAngularDomain = deltaAngle <= Math.Abs(signedSweep) + DirectionTolerance;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "contains", radialResidualMm <= PositionToleranceMm && planeResidualMm <= PositionToleranceMm && withinAngularDomain },
            { "candidate_angle_rad", theta }, { "start_angle_rad", arc.StartAngle },
            { "signed_sweep_rad", signedSweep }, { "directed_delta_from_start_rad", deltaAngle },
            { "radial_residual_mm", radialResidualMm }, { "plane_residual_mm", planeResidualMm },
            { "position_tolerance_mm", PositionToleranceMm }, { "angle_tolerance_rad", DirectionTolerance }
        };
    }

    private static double ArcDirectedSweep(Arc arc)
    {
        Require(arc != null && IsFinite(arc.Radius) && arc.Radius > 0.0,
            "Arc sweep oracle requires a finite positive radius.");
        double raw = arc.EndAngle - arc.StartAngle;
        double sweep = arc.GetLength() / arc.Radius;
        Require(IsFinite(raw) && IsFinite(sweep) && sweep > 0.0 && sweep <= 2.0 * Math.PI + DirectionTolerance,
            "Arc sweep oracle observed a non-finite or out-of-domain NX Arc extent.");
        double forward = PositiveAngle(raw);
        if (Math.Abs(forward - sweep) <= DirectionTolerance) return sweep;
        double backward = 2.0 * Math.PI - forward;
        if (Math.Abs(backward - sweep) <= DirectionTolerance) return -sweep;
        if (Math.Abs(raw - sweep) <= DirectionTolerance) return sweep;
        if (Math.Abs(-raw - sweep) <= DirectionTolerance) return -sweep;
        throw new InvalidOperationException("Arc native limits and intrinsic length do not identify one signed domain.");
    }

    private static Point3d ArcPoint(Arc arc, double angle)
    {
        Point3d center; Vector3d axisX, axisY;
        arc.GetOrientation(out center, out axisX, out axisY);
        double cosine = Math.Cos(angle), sine = Math.Sin(angle);
        return new Point3d(center.X + arc.Radius * (axisX.X * cosine + axisY.X * sine),
            center.Y + arc.Radius * (axisX.Y * cosine + axisY.Y * sine),
            center.Z + arc.Radius * (axisX.Z * cosine + axisY.Z * sine));
    }

    private static double PositiveAngle(double angle)
    {
        double value = angle % (2.0 * Math.PI);
        if (value < 0.0) value += 2.0 * Math.PI;
        return value;
    }

    private static double[] WorldToLocal(Frame frame, double[] point)
    {
        double[] delta = Subtract(point, frame.Origin);
        return new double[] { Dot(delta, frame.XAxis), Dot(delta, frame.YAxis), Dot(delta, frame.Normal) };
    }

    private static HashSet<string> TagsInSketch(Sketch sketch)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        foreach (NXObject item in sketch.GetAllGeometry())
            if (item != null) result.Add(item.Tag.ToString());
        return result;
    }

    private static List<Curve> NewSketchCurves(Sketch sketch, HashSet<string> beforeTags)
    {
        List<Curve> result = new List<Curve>();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve;
            if (curve != null && !beforeTags.Contains(curve.Tag.ToString())) result.Add(curve);
        }
        return result;
    }

    private static List<Curve> CurvesMatchingExpected(Sketch sketch, double[] start, double[] end)
    {
        List<Curve> result = new List<Curve>();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Line line = item as Line;
            if (line != null && Object.Equals(LineMatch(Point(line.StartPoint), Point(line.EndPoint), start, end)["matches"], true))
                result.Add(line);
        }
        return result;
    }

    private static Line FindProjectedLine(Sketch sketch, string preferredTag,
        double[] expectedStart, double[] expectedEnd)
    {
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Line line = item as Line;
            if (line != null && String.Equals(line.Tag.ToString(), preferredTag, StringComparison.Ordinal)) return line;
        }
        List<Curve> matches = CurvesMatchingExpected(sketch, expectedStart, expectedEnd);
        if (matches.Count == 1) return matches[0] as Line;
        List<Line> allLines = new List<Line>();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Line line = item as Line;
            if (line != null) allLines.Add(line);
        }
        return allLines.Count == 1 ? allLines[0] : null;
    }

    private static bool SameLineCoordinates(double[] expectedStart, double[] expectedEnd, Line actual)
    {
        return Object.Equals(LineMatch(Point(actual.StartPoint), Point(actual.EndPoint), expectedStart, expectedEnd)["matches"], true);
    }

    private static Dictionary<string, object> LineMatch(double[] actualStart, double[] actualEnd,
        double[] expectedStart, double[] expectedEnd)
    {
        double directA = Distance(actualStart, expectedStart);
        double directB = Distance(actualEnd, expectedEnd);
        double reverseA = Distance(actualStart, expectedEnd);
        double reverseB = Distance(actualEnd, expectedStart);
        bool reverse = Math.Max(reverseA, reverseB) < Math.Max(directA, directB);
        double first = reverse ? reverseA : directA;
        double second = reverse ? reverseB : directB;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "matches", Math.Max(first, second) <= PositionToleranceMm },
            { "endpoint_residuals_mm", new double[] { first, second } },
            { "max_residual_mm", Math.Max(first, second) },
            { "endpoint_order_reversed", reverse }, { "position_tolerance_mm", PositionToleranceMm }
        };
    }

    // Independent analytic oracle: intersect p + t*d with the target plane.
    private static double[] ProjectPoint(double[] point, double[] planeOrigin,
        double[] planeNormal, double[] direction)
    {
        double[] p = FiniteVector(point, "point");
        double[] origin = FiniteVector(planeOrigin, "plane origin");
        double[] n = Normalize3(FiniteVector(planeNormal, "plane normal"));
        double[] d = Normalize3(FiniteVector(direction, "direction"));
        double denominator = Dot(n, d);
        Require(Math.Abs(denominator) > 1.0e-12, "Projection oracle direction is parallel to the target plane.");
        double t = -Dot(n, Subtract(p, origin)) / denominator;
        return Add(p, Scale(d, t));
    }

    private static void SavePart(Part part, string description)
    {
        PartSaveStatus status = null;
        try
        {
            status = part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
            Require(status != null && status.NumberUnsavedParts == 0 && status.NumberUnsavedObjects == 0,
                "NX did not save " + description + " cleanly.");
        }
        finally { if (status != null) status.Dispose(); }
    }

    private static Dictionary<string, object> ExecutionRecord(M3ExecutionResult value)
    {
        if (value == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "state", value.State }, { "revision_id", value.RevisionId },
            { "result", value.Result }, { "error_code", value.ErrorCode }, { "error_message", value.ErrorMessage }
        };
    }

    private static Dictionary<string, object> VerificationRecord(M3VerificationResult value)
    {
        if (value == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "verified", value.Verified }, { "revision_id", value.RevisionId },
            { "checks", value.Checks }, { "result", value.Result },
            { "error_code", value.ErrorCode }, { "error_message", value.ErrorMessage }
        };
    }

    private static Dictionary<string, object> SaveCopyRecord(M2SaveCopyResult value)
    {
        if (value == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "state", value.State }, { "part_path", value.PartPath }, { "part_tag", value.PartTag },
            { "revision_id", value.RevisionId }, { "reopened", value.Reopened },
            { "identity", value.Identity }, { "checks", value.Checks }, { "evidence", value.Evidence },
            { "error_code", value.ErrorCode }, { "error_message", value.ErrorMessage }
        };
    }

    private static Dictionary<string, object> ProjectionRequestRecord(M3SketchProjectionRequest value)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", value.RevisionId }, { "sketch_id", value.SketchId },
            { "source_curve_ids", value.SourceCurveIds }, { "direction_xyz", SafeNumbers(value.DirectionXYZ) },
            { "associative", value.Associative }
        };
    }

    private static Dictionary<string, object> AssemblyRecord(Assembly assembly)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "full_name", assembly.FullName }, { "location", assembly.Location },
            { "version", assembly.GetName().Version == null ? null : assembly.GetName().Version.ToString() }
        };
    }

    private static Dictionary<string, object> NewCase(string name, string objective,
        string[] reproduction, string criterion)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "id", name }, { "objective", objective }, { "reproduction_steps", reproduction },
            { "retest_criterion", criterion }
        };
    }

    private static void RunCase(Dictionary<string, object> report, string name, string objective,
        string[] reproduction, string criterion, CaseBody body)
    {
        List<object> cases = report["cases"] as List<object>;
        List<object> defects = report["defects"] as List<object>;
        Dictionary<string, object> row = NewCase(name, objective, reproduction, criterion);
        cases.Add(row);
        try
        {
            body(row);
            if (!row.ContainsKey("passed")) row["passed"] = true;
            if (!Object.Equals(row["passed"], true)) AddCaseDefect(defects, row);
        }
        catch (Exception ex)
        {
            row["passed"] = false;
            row["error"] = ex.GetType().FullName + ": " + ex.Message;
            AddCaseDefect(defects, row);
        }
        Persist(GetString(report, "report_path"), report);
    }

    private static void AddCaseDefect(List<object> defects, Dictionary<string, object> row)
    {
        string observed = GetString(row, "error");
        if (String.IsNullOrEmpty(observed)) observed = "The case pass criterion was not met; see recorded native and numeric evidence.";
        AddDefect(defects, GetString(row, "id"), observed,
            GetString(row, "retest_criterion"),
            ToStringArray(row.ContainsKey("reproduction_steps") ? row["reproduction_steps"] : null),
            GetString(row, "retest_criterion"), row);
    }

    private static bool HasUnknownOperation(List<object> cases)
    {
        if (cases == null) return false;
        foreach (object value in cases)
            if (ContainsUnknownState(value)) return true;
        return false;
    }

    private static bool IsUnknown(object value)
    { return value != null && String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "UNKNOWN", StringComparison.Ordinal); }

    private static bool ContainsUnknownState(object value)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            object state;
            if (dictionary.TryGetValue("state", out state) && IsUnknown(state)) return true;
            foreach (object child in dictionary.Values)
                if (ContainsUnknownState(child)) return true;
        }
        List<object> list = value as List<object>;
        if (list != null)
            foreach (object child in list)
                if (ContainsUnknownState(child)) return true;
        return false;
    }

    private static void AddDefect(Dictionary<string, object> report, string id, string observed,
        string expected, string[] reproduction, string retest)
    {
        AddDefect(report["defects"] as List<object>, id, observed, expected, reproduction, retest, null);
    }

    private static void AddDefect(List<object> defects, string id, string observed,
        string expected, string[] reproduction, string retest, object evidence)
    {
        defects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "defect_id", id }, { "expected", expected }, { "observed", observed },
            { "units", "millimeter for geometry; error and state fields are categorical" },
            { "evidence", evidence }, { "reproduction_steps", reproduction },
            { "retest_criterion", retest }
        });
    }

    private static bool AllCasesPassed(List<object> cases)
    {
        if (cases == null || cases.Count == 0) return false;
        foreach (object value in cases)
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            if (row == null || !Object.Equals(row["passed"], true)) return false;
        }
        return true;
    }

    private static bool SaveCopyPassed(object value)
    {
        Dictionary<string, object> row = value as Dictionary<string, object>;
        return row != null && Object.Equals(row["passed"], true);
    }

    private static bool ChecksPass(Dictionary<string, object> checks)
    {
        if (checks == null || checks.Count == 0) return false;
        foreach (object value in checks.Values)
        {
            Dictionary<string, object> check = value as Dictionary<string, object>;
            if (check == null || !String.Equals(GetString(check, "status"), "VERIFIED", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static object FindNestedValue(object value, string key)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary == null) return null;
        object found;
        if (dictionary.TryGetValue(key, out found)) return found;
        foreach (object child in dictionary.Values)
        {
            object nested = FindNestedValue(child, key);
            if (nested != null) return nested;
        }
        return null;
    }

    private static Dictionary<string, object> FindNestedDictionary(object value, string key)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary == null) return null;
        object found;
        if (dictionary.TryGetValue(key, out found)) return found as Dictionary<string, object>;
        foreach (object child in dictionary.Values)
        {
            Dictionary<string, object> nested = FindNestedDictionary(child, key);
            if (nested != null) return nested;
        }
        return null;
    }

    private static Sketch FindSketch(Part part, string sketchId)
    {
        foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null && String.Equals(sketch.JournalIdentifier, sketchId, StringComparison.Ordinal)) return sketch;
        throw new InvalidOperationException("Native Sketch JournalIdentifier was not found: " + sketchId);
    }

    private static Frame MakeFrame(double[] origin, double[] normal, double[] xAxis)
    {
        double[] n = Normalize3(normal);
        double[] projectedX = Subtract(xAxis, Scale(n, Dot(xAxis, n)));
        double[] x = Normalize3(projectedX);
        double[] y = Normalize3(Cross(n, x));
        return new Frame { Origin = origin, Normal = n, XAxis = x, YAxis = y };
    }

    private static double[] FiniteVector(double[] value, string label)
    {
        Require(value != null && value.Length == 3, label + " must contain exactly 3 values.");
        for (int i = 0; i < value.Length; i++)
            Require(IsFinite(value[i]), label + " contains a non-finite coordinate.");
        return (double[])value.Clone();
    }

    private static double[] Normalize3(double[] value)
    {
        double[] vector = FiniteVector(value, "vector");
        double scale = Math.Max(Math.Abs(vector[0]), Math.Max(Math.Abs(vector[1]), Math.Abs(vector[2])));
        Require(scale > 0.0, "Cannot normalize a zero vector.");
        double x = vector[0] / scale, y = vector[1] / scale, z = vector[2] / scale;
        double length = Math.Sqrt(x * x + y * y + z * z);
        return new double[] { x / length, y / length, z / length };
    }

    private static double[] Add(double[] a, double[] b)
    { return new double[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
    private static double[] Subtract(double[] a, double[] b)
    { return new double[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    private static double[] Scale(double[] a, double value)
    { return new double[] { a[0] * value, a[1] * value, a[2] * value }; }
    private static double Dot(double[] a, double[] b)
    { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    private static double[] Cross(double[] a, double[] b)
    { return new double[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
    private static double Distance(double[] a, double[] b)
    { return Math.Sqrt(Math.Pow(a[0] - b[0], 2.0) + Math.Pow(a[1] - b[1], 2.0) + Math.Pow(a[2] - b[2], 2.0)); }
    private static double Norm(double[] a)
    { return Math.Sqrt(Dot(a, a)); }
    private static double[] Point(Point3d point)
    { return new double[] { point.X, point.Y, point.Z }; }
    private static double[] Vector(Vector3d vector)
    { return new double[] { vector.X, vector.Y, vector.Z }; }
    private static Point3d ToPoint(double[] value)
    { return new Point3d(value[0], value[1], value[2]); }
    private static Vector3d ToVector(double[] value)
    { return new Vector3d(value[0], value[1], value[2]); }
    private static Point3d AddPoint(Point3d point, double[] delta)
    { return new Point3d(point.X + delta[0], point.Y + delta[1], point.Z + delta[2]); }

    private static void SetField(object target, string name, object value)
    {
        Require(target != null, "Reflected object state is null while setting " + name + ".");
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Require(field != null, "Reflected object does not expose field " + name + ".");
        field.SetValue(target, value);
    }

    private static object ReadPrivateField(object target, string name)
    {
        Require(target != null, "Reflected object state is null while reading " + name + ".");
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Require(field != null, "Reflected object does not expose field " + name + ".");
        return field.GetValue(target);
    }

    private static Dictionary<string, object> ReadDictionary(Dictionary<string, object> value, string key)
    {
        if (value == null) return null;
        object raw;
        return value.TryGetValue(key, out raw) ? raw as Dictionary<string, object> : null;
    }

    private static List<object> SafeNumbers(double[] values)
    {
        List<object> result = new List<object>();
        if (values == null) return null;
        foreach (double value in values) result.Add(IsFinite(value) ? (object)value : (Double.IsNaN(value) ? "NaN" : (value < 0 ? "-Infinity" : "Infinity")));
        return result;
    }

    private static bool SameValue(object left, object right)
    { return String.Equals(M2Json.CanonicalText(left), M2Json.CanonicalText(right), StringComparison.Ordinal); }

    private static bool SameTagGeometry(object left, object right)
    { return SameValue(WithoutMutableJournalIds(left, false), WithoutMutableJournalIds(right, false)); }

    private static bool SameStableNativeSnapshot(object left, object right)
    { return SameValue(WithoutMutableJournalIds(left, false), WithoutMutableJournalIds(right, false)); }

    private static object WithoutMutableJournalIds(object value, bool stripTag)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                string key = pair.Key.ToLowerInvariant();
                if (key == "journal_id" || key.EndsWith("_journal_id", StringComparison.Ordinal) ||
                    key == "feature_id" || key == "parent_ids" || key == "child_ids" || key == "revision_id" ||
                    (stripTag && key == "tag")) continue;
                result[pair.Key] = WithoutMutableJournalIds(pair.Value, stripTag);
            }
            return result;
        }
        List<object> list = value as List<object>;
        if (list != null)
        {
            List<object> result = new List<object>();
            foreach (object item in list) result.Add(WithoutMutableJournalIds(item, stripTag));
            return result;
        }
        return value;
    }

    private static double[] ReadDoubleArray(Dictionary<string, object> dictionary, string key)
    {
        object value = GetValue(dictionary, key);
        double[] direct = value as double[];
        if (direct != null) return (double[])direct.Clone();
        List<object> list = value as List<object>;
        if (list == null || list.Count != 3) return null;
        double[] result = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (list[i] == null) return null;
            result[i] = Convert.ToDouble(list[i], CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static bool IsNx2506(string version)
    {
        if (String.IsNullOrWhiteSpace(version)) return false;
        string normalized = version.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        return normalized.Contains("2506") || normalized.Contains("25.06") || normalized.Contains("25.6");
    }

    private static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    private static bool PositiveFinite(Dictionary<string, object> dictionary, string key)
    {
        object value = GetValue(dictionary, key);
        if (value == null) return false;
        try
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return IsFinite(number) && number > 0.0;
        }
        catch { return false; }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static Dictionary<string, object> ExceptionRecord(Exception error)
    {
        if (error == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "type", error.GetType().FullName }, { "message", error.Message },
            { "stack_trace", error.StackTrace }
        };
    }
    private static void RequireNewPath(string path, string label)
    { Require(!File.Exists(path) && !Directory.Exists(path), "Unique " + label + " path already exists; refusing to overwrite it."); }
    private static void Persist(string path, Dictionary<string, object> report)
    {
        try { File.WriteAllText(path, M2Json.CanonicalText(report), new UTF8Encoding(false)); }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".serialization-failure.txt", ex.ToString(), new UTF8Encoding(false));
            throw;
        }
    }
    private static string UtcNow() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
    private static string FirstNonEmpty(params string[] values)
    { foreach (string value in values) if (!String.IsNullOrWhiteSpace(value)) return value; return null; }
    private static string GetString(Dictionary<string, object> dictionary, string key)
    { if (dictionary == null) return null; object value; return dictionary.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null; }

    private static string[] ToStringArray(object value)
    {
        string[] strings = value as string[];
        if (strings != null) return strings;
        List<object> list = value as List<object>;
        if (list == null) return new string[0];
        List<string> result = new List<string>();
        foreach (object item in list) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
        return result.ToArray();
    }
}
