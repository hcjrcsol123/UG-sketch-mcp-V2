// Real-NX acceptance for F05 two-line datum planes. Build offline; execute only
// in the designated idle NX session through RunF05Acceptance.py.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NXOpen;
using NXOpen.Features;
using NxSketchMcp.M2;

public static class F05Acceptance
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    private const double PositionToleranceMm = 1e-6;
    private const double DirectionTolerance = 1e-7;
    private const double MinimumLineLengthMm = 1e-6;
    private const double ParallelDirectionCrossTolerance = 1e-10;
    private const double ParallelSeparationToleranceMm = 1e-6;
    private const double CoplanarityToleranceMm = 1e-6;
    private const double EditLengthMm = 10.0;
    private const int RequiredCaseCount = 32;

    private sealed class Fixture
    {
        internal Part Part;
        internal string BaseDatumId;
        internal string SketchOneId;
        internal string SketchTwoId;
        internal string SketchOneFeatureId;
        internal string SketchTwoFeatureId;
        internal string ArcSketchId;
        internal string ArcSketchFeatureId;
        internal string WrongTypeCurveId;
        internal string SketchLineOneId;
        internal string SketchLineTwoId;
        internal Line SketchLineOne;
        internal Line SketchLineTwo;
        internal Line CrossA;
        internal Line CrossB;
        internal Line OutsideA;
        internal Line OutsideB;
        internal Line ReverseA;
        internal Line ReverseB;
        internal Line CoincidentA;
        internal Line CoincidentB;
        internal Line SkewA;
        internal Line SkewB;
        internal Line Short;
        internal Line ShortNativeFailure;
        internal Line ShortBelow;
        internal Line NearParallelAboveA;
        internal Line NearParallelAboveB;
        internal Line NearParallelNativeFailureA;
        internal Line NearParallelNativeFailureB;
        internal Line NearParallelBelowA;
        internal Line NearParallelBelowB;
        internal Line NearParallelBelowDistinctA;
        internal Line NearParallelBelowDistinctB;
        internal Line ParallelGapBelowA;
        internal Line ParallelGapBelowB;
        internal Line ParallelGapAboveA;
        internal Line ParallelGapAboveB;
        internal Line NearCoplanarA;
        internal Line NearCoplanarB;
        internal Line SkewBeyondToleranceA;
        internal Line SkewBeyondToleranceB;
        internal Line EditLineOne;
        internal Line EditLineTwo;
        internal double[] Origin;
        internal double[] U;
        internal double[] V;
        internal double[] PlaneNormal;
    }

    private sealed class PlaneOracle
    {
        internal double[] Origin;
        internal double[] Normal;
        internal double Offset;
        internal double DirectionCrossNorm;
        internal double ParallelSeparationMm;
        internal double CoplanarityMm;
        internal double Line1LengthMm;
        internal double Line2LengthMm;
        internal bool ParallelByTolerance;
        internal double[] EndpointResidualsMm;
        internal string Classification;
    }

    private sealed class PlaneResult
    {
        internal string FeatureId;
        internal M3PlanSnapshot Plan;
        internal Dictionary<string, object> Native;
        internal PlaneOracle Oracle;
        internal string Line1Id;
        internal string Line2Id;
        internal Line Line1Object;
        internal Line Line2Object;
    }

    private sealed class SketchResult
    {
        internal string SketchId;
        internal string FeatureId;
        internal string SupportDatumId;
        internal Dictionary<string, object> Inspection;
    }

    private delegate void CaseBody(Dictionary<string, object> record);

    public static void Main(string[] args)
    {
        string runId = Guid.NewGuid().ToString("N");
        string modelDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "models");
        string evidenceDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "evidence");
        string sourcePath = Path.Combine(modelDirectory, "F05-acceptance-" + runId + ".prt");
        string savedPath = Path.Combine(modelDirectory, "F05-acceptance-" + runId + "-roundtrip.prt");
        string reportPath = Path.Combine(evidenceDirectory, "F05-acceptance-" + runId + ".json");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        RequireNewPath(sourcePath, "source model");
        RequireNewPath(savedPath, "roundtrip model");
        RequireNewPath(reportPath, "evidence report");

        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test", "F05 two-line datum-plane NX acceptance" },
            { "test_version", "0.2.0" }, { "run_id", runId }, { "started_utc", UtcNow() },
            { "passed", false }, { "source_model_path", sourcePath }, { "roundtrip_model_path", savedPath },
            { "report_path", reportPath }, { "position_tolerance_mm", PositionToleranceMm },
            { "unit_vector_tolerance", DirectionTolerance }, { "minimum_line_length_mm", MinimumLineLengthMm },
            { "parallel_direction_cross_tolerance", ParallelDirectionCrossTolerance },
            { "parallel_separation_tolerance_mm", ParallelSeparationToleranceMm },
            { "coplanarity_tolerance_mm", CoplanarityToleranceMm },
            { "required_case_count", RequiredCaseCount },
            { "required_native_method", "TwoLines" }, { "required_native_alternate", "Three" },
            { "normal_sign_constrained", false }, { "cases", new List<object>() }, { "defects", new List<object>() },
            { "configuration", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "test_layer", "real NXOpen subsystem validation through production M2OpenModelAdapter" },
                    { "traceability", "docs/F05.md; docs/V2.md" },
                    { "production_bridge_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Bridge.dll") },
                    { "production_adapter_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Entry.dll") },
                    { "acceptance_assembly", Path.Combine(evidenceDirectory, "F05Acceptance.dll") },
                    { "units", "mm" }, { "nx_started_by_harness", false }, { "physical_metrology", false }
                }
            }
        };
        Persist(reportPath, evidence);

        Session session = null;
        M2OpenModelAdapter adapter = null;
        M2ModelBinding binding = null;
        Part part = null;
        Fixture fixture = null;
        PlaneResult outsideResult = null;
        PlaneResult sketchParallelResult = null;
        PlaneResult crossingResult = null;
        SketchResult supportSketchResult = null;
        bool setupPassed = false;

        try
        {
            session = Session.GetSession();
            Part originalPart = session.Parts.Work;
            evidence["original_work_part_path"] = originalPart == null ? null : originalPart.FullPath;
            evidence["original_work_part_was_modified"] = originalPart != null && originalPart.IsModified;
            evidence["session_was_in_task_environment"] = session.IsInTaskEnvironment;
            Require(!session.IsInTaskEnvironment, "Acceptance requires an idle NX session outside a task environment.");
            Require(originalPart == null || !originalPart.IsModified,
                "Acceptance requires the original Work Part to be saved and unmodified.");

            part = session.Parts.NewDisplay(sourcePath, Part.Units.Millimeters);
            SaveInitialFixture(part);
            adapter = new M2OpenModelAdapter();
            binding = adapter.Takeover(sourcePath, "saved_independent_copy", "f05-acceptance-session",
                "f05-acceptance-" + runId, 1);
            evidence["session"] = adapter.GetSession(binding);
            evidence["loaded_assemblies"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "bridge", AssemblyRecord(typeof(M2Json).Assembly) },
                { "production_adapter", AssemblyRecord(typeof(M2OpenModelAdapter).Assembly) },
                { "nxopen", AssemblyRecord(typeof(Session).Assembly) },
                { "acceptance", AssemblyRecord(Assembly.GetExecutingAssembly()) }
            };

            setupPassed = RunCase(reportPath, evidence, "F05_isolated_fixture_setup",
                "Create a saved millimeter part, one fixed datum support, two separate F01 sketches, and all native line fixtures.",
                new string[] {
                    "Start in an idle NX session with the original Work Part saved and unmodified.",
                    "Create and save a new millimeter part, then take it over through M2OpenModelAdapter.",
                    "Create a tilted fixed datum and two independent supported sketches, each containing one line.",
                    "Create all raw NXOpen.Line fixtures before refreshing and recording any final line JournalIdentifiers.",
                    "Inspect both sketches after the full fixture exists and resolve their current native line IDs."
                },
                "Fixture is saved and isolated; base datum and both sketches are APPLIED and verified; their parent ancestry is direct; all line IDs resolve to NXOpen.Line; fixture endpoint values and current binding revision are recorded.",
                delegate(Dictionary<string, object> record)
                {
                    fixture = CreateFixture(record, adapter, binding, part);
                });

            if (setupPassed && fixture != null)
            {
                crossingResult = RunPositivePlane(reportPath, evidence, adapter, binding, fixture,
                    "F05_oblique_intersection_inside_segments",
                    "Build the expected plane for u=(1,1,1), v=(0,1,2) from independent infinite-line geometry.",
                    fixture.CrossA, fixture.CrossB, null, null);

                outsideResult = RunPositivePlane(reportPath, evidence, adapter, binding, fixture,
                    "F05_infinite_intersection_outside_segments",
                    "Prove two finite segments may miss while their nonparallel coplanar infinite lines uniquely determine the plane.",
                    fixture.OutsideA, fixture.OutsideB, null, null);

                sketchParallelResult = RunPositivePlane(reportPath, evidence, adapter, binding, fixture,
                    "F05_parallel_lines_from_two_sketch_features",
                    "Create a unique plane from distinct parallel lines belonging to two separate native F01 sketch features.",
                    fixture.SketchLineOne, fixture.SketchLineTwo,
                    new string[] { fixture.SketchOneFeatureId, fixture.SketchTwoFeatureId }, null);

                RunPositivePlane(reportPath, evidence, adapter, binding, fixture,
                    "F05_reverse_line1_endpoints_invariant",
                    "Reverse line 1 endpoints and prove the unoriented plane is unchanged.",
                    fixture.ReverseA, fixture.CrossB, null, crossingResult);

                RunPositivePlane(reportPath, evidence, adapter, binding, fixture,
                    "F05_reverse_line2_endpoints_invariant",
                    "Reverse line 2 endpoints and prove the unoriented plane is unchanged.",
                    fixture.CrossA, fixture.ReverseB, null, crossingResult);

                RunPositivePlane(reportPath, evidence, adapter, binding, fixture,
                    "F05_swap_line_order_invariant",
                    "Swap line1 and line2 request order and prove the unoriented plane is unchanged.",
                    fixture.CrossB, fixture.CrossA, null, crossingResult);

                RunExpectedNativeRollbackCase(reportPath, evidence, adapter, binding, fixture,
                    "F05_near_parallel_delta_1e_minus_7_native_wrong_normal_rolls_back",
                    "Retain the measured near-parallel NX alternate failure as an expected rolled-back execution; the generated plane must not be reported as applied when native normal/residual readback is wrong.",
                    fixture.NearParallelNativeFailureA, fixture.NearParallelNativeFailureB,
                    "native_two_line_definition_readback");

                RunPositivePlane(reportPath, evidence, adapter, binding, fixture,
                    "F05_near_parallel_delta_1e_minus_3_native_plane_verified",
                    "Verify the measured 1e-3 direction perturbation fixture using the native TwoLines/Three plane and independent endpoint oracle.",
                    fixture.NearParallelAboveA, fixture.NearParallelAboveB, null, null);

                RunPositivePlane(reportPath, evidence, adapter, binding, fixture,
                    "F05_near_parallel_below_threshold_distinct_accepted",
                    "Classify cross norm <=1e-10 as parallel and accept the pair when its parallel separation is distinct and every endpoint remains within plane tolerance.",
                    fixture.NearParallelBelowDistinctA, fixture.NearParallelBelowDistinctB, null, null);

                RunPositivePlane(reportPath, evidence, adapter, binding, fixture,
                    "F05_parallel_separation_above_threshold_accepted",
                    "Accept distinct parallel lines separated by more than 1e-6 mm.",
                    fixture.ParallelGapAboveA, fixture.ParallelGapAboveB, null, null);

                RunPositivePlane(reportPath, evidence, adapter, binding, fixture,
                    "F05_coplanarity_residual_within_threshold_accepted",
                    "Accept a near-coplanar pair only when all measured endpoint residuals remain within 1e-6 mm.",
                    fixture.NearCoplanarA, fixture.NearCoplanarB, null, null);

                RunExpectedNativeRollbackCase(reportPath, evidence, adapter, binding, fixture,
                    "F05_short_line_1e_minus_5_native_evaluate_rejection_rolls_back",
                    "Retain the measured 1e-5 mm NX Evaluate collinear-edge rejection as a verified rollback even though geometric prepare accepts its length.",
                    fixture.ShortNativeFailure, fixture.CrossB, "native_evaluate_collinear_edge_axis");

                RunPositivePlane(reportPath, evidence, adapter, binding, fixture,
                    "F05_short_line_0_01_native_plane_verified",
                    "Verify the measured 0.01 mm short-line fixture through native readback and all four endpoint residuals.",
                    fixture.Short, fixture.CrossB, null, null);

                if (crossingResult != null)
                    supportSketchResult = RunF01SupportCase(reportPath, evidence, adapter, binding, fixture, crossingResult,
                        new PlaneResult[] { crossingResult, outsideResult, sketchParallelResult });
                else
                    RecordSkippedAsFailure(reportPath, evidence, "F05_two_line_plane_supports_F01_sketch",
                        "No independently verified crossing datum exists to provide the F01 sketch support.");

                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_same_native_line_rejected",
                    TwoLineRequest(binding.RevisionId, fixture.CrossA.JournalIdentifier, fixture.CrossA.JournalIdentifier),
                    "Reject one native NXOpen.Line supplied in both line slots.");
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_coincident_infinite_lines_rejected",
                    TwoLineRequest(binding.RevisionId, fixture.CoincidentA.JournalIdentifier, fixture.CoincidentB.JournalIdentifier),
                    "Reject distinct native line objects that occupy the same infinite line.");
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_line_at_minimum_length_rejected",
                    TwoLineRequest(binding.RevisionId, fixture.ShortBelow.JournalIdentifier,
                        fixture.CrossB.JournalIdentifier),
                    "Reject a fixture line with measured length at or below the 1e-6 mm degeneracy threshold.",
                    delegate { return VectorDistance(Point(fixture.ShortBelow.EndPoint), Point(fixture.ShortBelow.StartPoint)) <= MinimumLineLengthMm; });
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_missing_line1_rejected",
                    TwoLineRequest(binding.RevisionId, "missing-f05-line1", fixture.CrossB.JournalIdentifier),
                    "Reject a missing first native line ID.");
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_missing_line2_rejected",
                    TwoLineRequest(binding.RevisionId, fixture.CrossA.JournalIdentifier, "missing-f05-line2"),
                    "Reject a missing second native line ID.");
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_wrong_type_curve_line1_rejected",
                    TwoLineRequest(binding.RevisionId, fixture.WrongTypeCurveId, fixture.CrossB.JournalIdentifier),
                    "Reject a valid native NXOpen.Arc Curve ID in line1_id, after it resolves as an object but fails the NXOpen.Line type check.");
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_wrong_type_line2_rejected",
                    TwoLineRequest(binding.RevisionId, fixture.CrossA.JournalIdentifier, fixture.BaseDatumId),
                    "Reject a valid non-Line datum-feature ID in line2_id.");
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_skew_lines_rejected",
                    TwoLineRequest(binding.RevisionId, fixture.SkewA.JournalIdentifier, fixture.SkewB.JournalIdentifier),
                    "Reject nonparallel lines with a shortest infinite-line separation well above 1e-6 mm.");
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_near_parallel_below_threshold_rejected",
                    TwoLineRequest(binding.RevisionId, fixture.NearParallelBelowA.JournalIdentifier,
                        fixture.NearParallelBelowB.JournalIdentifier),
                    "Apply the <=1e-10 parallel classification; a near-parallel pair with separation <=1e-6 mm does not define a unique numerically distinct plane.");
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_parallel_separation_below_threshold_rejected",
                    TwoLineRequest(binding.RevisionId, fixture.ParallelGapBelowA.JournalIdentifier,
                        fixture.ParallelGapBelowB.JournalIdentifier),
                    "Reject parallel lines whose separation is <=1e-6 mm.");
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_coplanarity_residual_above_threshold_rejected",
                    TwoLineRequest(binding.RevisionId, fixture.SkewBeyondToleranceA.JournalIdentifier,
                        fixture.SkewBeyondToleranceB.JournalIdentifier),
                    "Reject nonparallel lines with an independently measured coplanarity residual above 1e-6 mm.");

                M3DatumPlaneRequest mixedFixed = TwoLineRequest(binding.RevisionId,
                    fixture.CrossA.JournalIdentifier, fixture.CrossB.JournalIdentifier);
                mixedFixed.OriginMm = Copy(fixture.Origin);
                mixedFixed.Normal = Normalize(new double[] { 1.0, 2.0, -1.0 });
                mixedFixed.XAxis = Normalize(new double[] { 1.0, -1.0, -1.0 });
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_two_lines_mixed_with_fixed_fields_rejected",
                    mixedFixed, "Reject two-line mode mixed with a complete fixed frame.");
                M3DatumPlaneRequest mixedAngle = TwoLineRequest(binding.RevisionId,
                    fixture.CrossA.JournalIdentifier, fixture.CrossB.JournalIdentifier);
                mixedAngle.SupportDatumPlaneId = fixture.BaseDatumId;
                mixedAngle.AxisLineId = fixture.CrossA.JournalIdentifier;
                mixedAngle.AngleDeg = 12.0;
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_two_lines_mixed_with_angle_fields_rejected",
                    mixedAngle, "Reject two-line mode mixed with angle-reference fields.");
                M3DatumPlaneRequest missingOneLine = TwoLineRequest(binding.RevisionId,
                    null, fixture.CrossB.JournalIdentifier);
                RunRejectedPrepare(reportPath, evidence, adapter, binding, part, "F05_incomplete_line_pair_rejected",
                    missingOneLine, "Reject a request containing only line2_id.");

                RunLineEditRevisionUndoCase(reportPath, evidence, session, adapter, binding, fixture,
                    "F05_line1_edit_revision_stale_plan_undo",
                    fixture.EditLineOne, fixture.EditLineTwo, true);
                RunLineEditRevisionUndoCase(reportPath, evidence, session, adapter, binding, fixture,
                    "F05_line2_edit_revision_stale_plan_undo",
                    fixture.EditLineOne, fixture.EditLineTwo, false);

                if (sketchParallelResult != null)
                    RunSaveReopenCase(reportPath, evidence, session, adapter, binding, fixture,
                        new PlaneResult[] { crossingResult, sketchParallelResult }, supportSketchResult, savedPath);
                else
                    RecordSkippedAsFailure(reportPath, evidence, "F05_save_copy_reopen_native_roundtrip",
                        "The required two-sketch parallel positive case did not pass.");
            }
            else
            {
                RecordSkippedAsFailure(reportPath, evidence, "F05_positive_negative_edit_and_roundtrip_matrix",
                    "F05 fixture setup failed; dependent acceptance cases were not executed.");
            }
        }
        catch (Exception ex)
        {
            List<object> defects = (List<object>)evidence["defects"];
            AddDefect(defects, "F05_harness_top_level", new string[] {
                "Open a saved, unmodified Work Part in an idle NX session outside a task environment.",
                "Run the compiled F05 acceptance wrapper in NX.",
                "Review the first incomplete case and the unique source model path."
            }, ex.ToString(), "Correct the harness/setup failure and rerun the full F05 acceptance with new unique model and evidence paths.");
            evidence["harness_failure"] = ExceptionRecord(ex);
        }
        finally
        {
            List<object> cases = (List<object>)evidence["cases"];
            List<object> defects = (List<object>)evidence["defects"];
            evidence["case_count"] = cases.Count;
            evidence["pass_count"] = CountCases(cases, "PASS");
            evidence["fail_count"] = CountCases(cases, "FAIL");
            evidence["skipped_case_count"] = CountCases(cases, "SKIPPED");
            evidence["passed"] = setupPassed && cases.Count == RequiredCaseCount &&
                CountCases(cases, "PASS") == RequiredCaseCount && defects.Count == 0;
            evidence["completed_utc"] = UtcNow();
            Persist(reportPath, evidence);
        }
    }

    private static Fixture CreateFixture(Dictionary<string, object> record,
        M2OpenModelAdapter adapter, M2ModelBinding binding, Part part)
    {
        Fixture fixture = new Fixture();
        fixture.Part = part;
        fixture.Origin = new double[] { 100.0, 200.0, 300.0 };
        fixture.U = Normalize(new double[] { 1.0, 1.0, 1.0 });
        fixture.V = Normalize(new double[] { 0.0, 1.0, 2.0 });
        fixture.PlaneNormal = Normalize(Cross(fixture.U, fixture.V));
        double[] fixedXAxis = Normalize(new double[] { 1.0, -1.0, -1.0 });
        M3DatumPlaneRequest fixedRequest = new M3DatumPlaneRequest
        {
            RevisionId = binding.RevisionId, Mode = "fixed", OriginMm = Copy(fixture.Origin),
            Normal = Normalize(new double[] { 1.0, 2.0, -1.0 }), XAxis = fixedXAxis
        };
        Dictionary<string, object> fixedBefore = ModelDefinitionRecord(adapter, binding, part);
        M3PlanSnapshot fixedPlan = adapter.PrepareDatumPlane(binding, fixedRequest);
        Dictionary<string, object> fixedAfter = ModelDefinitionRecord(adapter, binding, part);
        Require(String.Equals(M2Json.CanonicalText(fixedBefore), M2Json.CanonicalText(fixedAfter), StringComparison.Ordinal),
            "Fixed F01 support prepare changed the fixture model.");
        M3ExecutionResult fixedExecution = adapter.ExecutePlan(binding, fixedPlan);
        Require(fixedExecution != null && fixedExecution.State == "APPLIED", "Fixed F01 support did not apply.");
        M3VerificationResult fixedVerification = adapter.VerifyPlan(binding, fixedPlan);
        Require(fixedVerification != null && fixedVerification.Verified && ChecksPass(fixedVerification.Checks),
            "Fixed F01 support did not production-verify.");
        fixture.BaseDatumId = ReadFeatureId(fixedExecution.Result);
        Dictionary<string, object> fixedNative = ReadNativeDatum(part, fixture.BaseDatumId);
        RequireVectorClose(ReadVector(fixedNative, "origin_mm", 3), fixture.Origin, PositionToleranceMm,
            "Fixed F01 support origin differs from the requested fixture point.");
        RequireVectorClose(Normalize(ReadVector(fixedNative, "normal_world", 3)),
            Normalize(fixedRequest.Normal), DirectionTolerance, "Fixed F01 support normal differs from its request.");
        double[] sketchXAxis = Normalize(ReadVector(fixedNative, "x_axis_world", 3));

        Dictionary<string, object> sketchOne = CreateFixtureSketch(adapter, binding, part,
            fixture.BaseDatumId, sketchXAxis, "f05-fixture-line-one",
            new double[] { -20.0, -20.0 }, new double[] { 20.0, -20.0 });
        fixture.SketchOneId = ReadRequiredString(sketchOne, "sketch_id");
        fixture.SketchOneFeatureId = ReadRequiredString(sketchOne, "feature_id");
        Dictionary<string, object> sketchTwo = CreateFixtureSketch(adapter, binding, part,
            fixture.BaseDatumId, sketchXAxis, "f05-fixture-line-two",
            new double[] { -20.0, 20.0 }, new double[] { 20.0, 20.0 });
        fixture.SketchTwoId = ReadRequiredString(sketchTwo, "sketch_id");
        fixture.SketchTwoFeatureId = ReadRequiredString(sketchTwo, "feature_id");
        Dictionary<string, object> arcSketch = CreateFixtureCircleSketch(adapter, binding, part,
            fixture.BaseDatumId, sketchXAxis);
        fixture.ArcSketchId = ReadRequiredString(arcSketch, "sketch_id");
        fixture.ArcSketchFeatureId = ReadRequiredString(arcSketch, "feature_id");

        double[] w = Normalize(Cross(fixture.PlaneNormal, fixture.U));
        double[] parallelOffsetBelow = Scale(w, 0.5e-6);
        double[] parallelOffsetAbove = Scale(w, 2.0e-6);
        double[] nearParallelAbove = Normalize(Add(fixture.U, Scale(w, 1e-3)));
        double[] nearParallelNativeFailure = Normalize(Add(fixture.U, Scale(w, 1e-7)));
        double[] nearParallelBelow = Normalize(Add(fixture.U, Scale(w, 0.5e-10)));
        double[] nearParallelBelowOffset = Scale(w, 5e-6);
        double[] shiftedNearPlane = Add(fixture.Origin, Scale(fixture.PlaneNormal, 0.5e-6));
        double[] shiftedBeyondPlane = Add(fixture.Origin, Scale(fixture.PlaneNormal, 2.0e-6));

        fixture.CrossA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, -10.0)),
            Add(fixture.Origin, Scale(fixture.U, 10.0)));
        fixture.CrossB = CreateLine(part, Add(fixture.Origin, Scale(fixture.V, -10.0)),
            Add(fixture.Origin, Scale(fixture.V, 10.0)));
        fixture.OutsideA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, 12.0)),
            Add(fixture.Origin, Scale(fixture.U, 20.0)));
        fixture.OutsideB = CreateLine(part, Add(fixture.Origin, Scale(fixture.V, 12.0)),
            Add(fixture.Origin, Scale(fixture.V, 20.0)));
        fixture.ReverseA = CreateLine(part, Point(fixture.CrossA.EndPoint), Point(fixture.CrossA.StartPoint));
        fixture.ReverseB = CreateLine(part, Point(fixture.CrossB.EndPoint), Point(fixture.CrossB.StartPoint));
        fixture.CoincidentA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, -10.0)),
            Add(fixture.Origin, Scale(fixture.U, -2.0)));
        fixture.CoincidentB = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, 3.0)),
            Add(fixture.Origin, Scale(fixture.U, 9.0)));
        fixture.SkewA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, -10.0)),
            Add(fixture.Origin, Scale(fixture.U, 10.0)));
        fixture.SkewB = CreateLine(part, Add(Add(fixture.Origin, Scale(fixture.PlaneNormal, 3.0)), Scale(fixture.V, -10.0)),
            Add(Add(fixture.Origin, Scale(fixture.PlaneNormal, 3.0)), Scale(fixture.V, 10.0)));
        fixture.Short = CreateLine(part, fixture.Origin, Add(fixture.Origin, Scale(fixture.U, 0.01)));
        fixture.ShortNativeFailure = CreateLine(part, fixture.Origin, Add(fixture.Origin, Scale(fixture.U, 1e-5)));
        fixture.ShortBelow = CreateLine(part, fixture.Origin, Add(fixture.Origin, Scale(fixture.U, 0.5e-6)));
        fixture.NearParallelAboveA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, -10.0)),
            Add(fixture.Origin, Scale(fixture.U, 10.0)));
        fixture.NearParallelAboveB = CreateLine(part, Add(fixture.Origin, Scale(nearParallelAbove, -10.0)),
            Add(fixture.Origin, Scale(nearParallelAbove, 10.0)));
        fixture.NearParallelNativeFailureA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, -10.0)),
            Add(fixture.Origin, Scale(fixture.U, 10.0)));
        fixture.NearParallelNativeFailureB = CreateLine(part, Add(fixture.Origin, Scale(nearParallelNativeFailure, -10.0)),
            Add(fixture.Origin, Scale(nearParallelNativeFailure, 10.0)));
        fixture.NearParallelBelowA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, -10.0)),
            Add(fixture.Origin, Scale(fixture.U, 10.0)));
        fixture.NearParallelBelowB = CreateLine(part, Add(fixture.Origin, Scale(nearParallelBelow, -10.0)),
            Add(fixture.Origin, Scale(nearParallelBelow, 10.0)));
        fixture.NearParallelBelowDistinctA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, -10.0)),
            Add(fixture.Origin, Scale(fixture.U, 10.0)));
        fixture.NearParallelBelowDistinctB = CreateLine(part,
            Add(Add(fixture.Origin, nearParallelBelowOffset), Scale(nearParallelBelow, -10.0)),
            Add(Add(fixture.Origin, nearParallelBelowOffset), Scale(nearParallelBelow, 10.0)));
        fixture.ParallelGapBelowA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, -10.0)),
            Add(fixture.Origin, Scale(fixture.U, 10.0)));
        fixture.ParallelGapBelowB = CreateLine(part, Add(Add(fixture.Origin, parallelOffsetBelow), Scale(fixture.U, -10.0)),
            Add(Add(fixture.Origin, parallelOffsetBelow), Scale(fixture.U, 10.0)));
        fixture.ParallelGapAboveA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, -10.0)),
            Add(fixture.Origin, Scale(fixture.U, 10.0)));
        fixture.ParallelGapAboveB = CreateLine(part, Add(Add(fixture.Origin, parallelOffsetAbove), Scale(fixture.U, -10.0)),
            Add(Add(fixture.Origin, parallelOffsetAbove), Scale(fixture.U, 10.0)));
        fixture.NearCoplanarA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, -10.0)),
            Add(fixture.Origin, Scale(fixture.U, 10.0)));
        fixture.NearCoplanarB = CreateLine(part, Add(Add(shiftedNearPlane, Scale(fixture.V, -10.0)), new double[] { 0.0, 0.0, 0.0 }),
            Add(shiftedNearPlane, Scale(fixture.V, 10.0)));
        fixture.SkewBeyondToleranceA = CreateLine(part, Add(fixture.Origin, Scale(fixture.U, -10.0)),
            Add(fixture.Origin, Scale(fixture.U, 10.0)));
        fixture.SkewBeyondToleranceB = CreateLine(part, Add(shiftedBeyondPlane, Scale(fixture.V, -10.0)),
            Add(shiftedBeyondPlane, Scale(fixture.V, 10.0)));
        fixture.EditLineOne = CreateLine(part, fixture.Origin, Add(fixture.Origin, Scale(fixture.U, EditLengthMm)));
        fixture.EditLineTwo = CreateLine(part, fixture.Origin, Add(fixture.Origin, Scale(fixture.V, EditLengthMm)));

        // Refresh identifiers only after both sketches and every independent line exist.
        Dictionary<string, object> sketchOneRead = adapter.InspectSketch(binding, fixture.SketchOneId);
        Dictionary<string, object> sketchTwoRead = adapter.InspectSketch(binding, fixture.SketchTwoId);
        Dictionary<string, object> arcSketchRead = adapter.InspectSketch(binding, fixture.ArcSketchId);
        fixture.SketchLineOneId = FindSketchLineId(sketchOneRead, "f05-fixture-line-one");
        fixture.SketchLineTwoId = FindSketchLineId(sketchTwoRead, "f05-fixture-line-two");
        fixture.WrongTypeCurveId = FindSketchGeometryIdByKind(arcSketchRead, "ARC");
        fixture.SketchLineOne = FindLineById(part, fixture.SketchLineOneId);
        fixture.SketchLineTwo = FindLineById(part, fixture.SketchLineTwoId);
        Curve wrongTypeCurve = FindCurveById(part, fixture.WrongTypeCurveId);
        Require(!String.Equals(fixture.SketchOneFeatureId, fixture.SketchTwoFeatureId, StringComparison.Ordinal),
            "The two fixture lines must come from different Sketch features.");
        Require(!String.Equals(fixture.SketchLineOneId, fixture.SketchLineTwoId, StringComparison.Ordinal),
            "The two fixture sketches resolved to the same line identifier.");
        Require(wrongTypeCurve != null && !(wrongTypeCurve is Line),
            "Wrong-type fixture did not resolve to a native non-Line Curve.");
        Require(FindSketch(part, fixture.SketchOneId).Feature != null && FindSketch(part, fixture.SketchTwoId).Feature != null,
            "Fixture Sketch feature references did not resolve.");
        Require(FeatureAncestorIds(FindSketch(part, fixture.SketchOneId).Feature).Contains(fixture.BaseDatumId) &&
            FeatureAncestorIds(FindSketch(part, fixture.SketchTwoId).Feature).Contains(fixture.BaseDatumId),
            "Fixture line sketches are not associatively supported by the F01 base datum.");

        List<object> fixtureLines = new List<object>();
        foreach (KeyValuePair<string, Line> item in FixtureLineMap(fixture)) fixtureLines.Add(LineRecord(item.Value));
        record["fixed_base_datum"] = fixedNative;
        record["sketch_one"] = sketchOne;
        record["sketch_two"] = sketchTwo;
        record["sketch_one_inspection_after_all_lines"] = sketchOneRead;
        record["sketch_two_inspection_after_all_lines"] = sketchTwoRead;
        record["arc_sketch"] = arcSketch;
        record["arc_sketch_inspection_after_all_lines"] = arcSketchRead;
        record["wrong_type_curve_id"] = fixture.WrongTypeCurveId;
        record["wrong_type_curve_runtime_type"] = wrongTypeCurve.GetType().FullName;
        record["line_one_sketch_feature_id"] = fixture.SketchOneFeatureId;
        record["line_two_sketch_feature_id"] = fixture.SketchTwoFeatureId;
        record["line_one_sketch_line_id"] = fixture.SketchLineOneId;
        record["line_two_sketch_line_id"] = fixture.SketchLineTwoId;
        record["line_ids_captured_after_all_line_creation"] = true;
        record["fixture_lines"] = fixtureLines;
        record["binding_revision_after_fixture"] = binding.RevisionId;
        return fixture;
    }

    private static Dictionary<string, object> CreateFixtureSketch(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part, string supportDatumId, double[] xAxis,
        string geometryId, double[] start, double[] end)
    {
        M3SketchRequest request = new M3SketchRequest
        {
            RevisionId = binding.RevisionId, SupportDatumPlaneId = supportDatumId,
            XAxis = Copy(xAxis), Geometries = new List<M3SketchGeometryRequest>
            {
                new M3SketchGeometryRequest { Id = geometryId, Type = "line", StartMm = Copy(start), EndMm = Copy(end) }
            }
        };
        Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, part);
        M3PlanSnapshot plan = adapter.PrepareSketch(binding, request);
        Dictionary<string, object> afterPrepare = ModelDefinitionRecord(adapter, binding, part);
        Require(String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(afterPrepare), StringComparison.Ordinal),
            "F01 fixture Sketch prepare changed the model.");
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        Require(execution != null && execution.State == "APPLIED", "F01 fixture Sketch did not apply.");
        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "F01 fixture Sketch did not production-verify.");
        string sketchId = ReadOptionalString(execution.Result, "sketch_id");
        if (String.IsNullOrWhiteSpace(sketchId)) sketchId = plan.SketchId;
        Require(!String.IsNullOrWhiteSpace(sketchId), "F01 fixture Sketch result omitted sketch_id.");
        Sketch sketch = FindSketch(part, sketchId);
        Dictionary<string, object> inspected = adapter.InspectSketch(binding, sketchId);
        RequireAnyDictionaryPropertyValue(inspected, new string[] { "datum_plane_id", "support_datum_plane_id" }, supportDatumId);
        string lineId = FindSketchLineId(inspected, geometryId);
        Line line = FindLineById(part, lineId);
        Require(line != null && line.GetType() == typeof(Line), "F01 fixture geometry did not resolve to NXOpen.Line.");
        Require(FeatureAncestorIds(sketch.Feature).Contains(supportDatumId),
            "F01 fixture Sketch native ancestry does not reach its datum support.");
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "sketch_id", sketch.JournalIdentifier }, { "feature_id", sketch.Feature.JournalIdentifier },
            { "line_id_at_creation", lineId }, { "line_type", line.GetType().FullName },
            { "support_datum_id", supportDatumId }, { "plan", PlanRecord(plan) },
            { "execution", ExecutionRecord(execution) }, { "verification", VerificationRecord(verification) },
            { "inspection", inspected }, { "geometry_id", geometryId },
            { "start_local_mm", start }, { "end_local_mm", end }
        };
    }

    private static Dictionary<string, object> CreateFixtureCircleSketch(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part, string supportDatumId, double[] xAxis)
    {
        M3SketchRequest request = new M3SketchRequest
        {
            RevisionId = binding.RevisionId, SupportDatumPlaneId = supportDatumId,
            XAxis = Copy(xAxis), Geometries = new List<M3SketchGeometryRequest>
            {
                new M3SketchGeometryRequest
                {
                    Id = "f05-fixture-nonline-arc", Type = "circle",
                    CenterMm = new double[] { 0.0, 0.0 }, DiameterMm = 6.0
                }
            }
        };
        Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, part);
        M3PlanSnapshot plan = adapter.PrepareSketch(binding, request);
        Dictionary<string, object> afterPrepare = ModelDefinitionRecord(adapter, binding, part);
        Require(String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(afterPrepare), StringComparison.Ordinal),
            "F01 non-Line fixture Sketch prepare changed the model.");
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        Require(execution != null && execution.State == "APPLIED", "F01 non-Line fixture Sketch did not apply.");
        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "F01 non-Line fixture Sketch did not production-verify.");
        string sketchId = ReadOptionalString(execution.Result, "sketch_id");
        if (String.IsNullOrWhiteSpace(sketchId)) sketchId = plan.SketchId;
        Require(!String.IsNullOrWhiteSpace(sketchId), "F01 non-Line fixture Sketch result omitted sketch_id.");
        Sketch sketch = FindSketch(part, sketchId);
        Dictionary<string, object> inspected = adapter.InspectSketch(binding, sketchId);
        RequireAnyDictionaryPropertyValue(inspected, new string[] { "datum_plane_id", "support_datum_plane_id" }, supportDatumId);
        Require(FeatureAncestorIds(sketch.Feature).Contains(supportDatumId),
            "F01 non-Line fixture Sketch ancestry does not reach its datum support.");
        string geometryId = FindSketchGeometryIdByKind(inspected, "ARC");
        Curve curve = FindCurveById(part, geometryId);
        Require(curve != null && !(curve is Line), "F01 circle did not resolve to a native non-Line Curve.");
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "sketch_id", sketch.JournalIdentifier }, { "feature_id", sketch.Feature.JournalIdentifier },
            { "curve_id_at_creation", geometryId }, { "curve_type", curve.GetType().FullName },
            { "support_datum_id", supportDatumId }, { "plan", PlanRecord(plan) },
            { "execution", ExecutionRecord(execution) }, { "verification", VerificationRecord(verification) },
            { "inspection", inspected }
        };
    }

    private static Line CreateLine(Part part, double[] start, double[] end)
    {
        return part.Curves.CreateLine(ToPoint(start), ToPoint(end));
    }

    private static Dictionary<string, Line> FixtureLineMap(Fixture fixture)
    {
        return new Dictionary<string, Line>(StringComparer.Ordinal)
        {
            { "sketch_one", fixture.SketchLineOne }, { "sketch_two", fixture.SketchLineTwo },
            { "cross_a", fixture.CrossA }, { "cross_b", fixture.CrossB },
            { "outside_a", fixture.OutsideA }, { "outside_b", fixture.OutsideB },
            { "reverse_a", fixture.ReverseA }, { "reverse_b", fixture.ReverseB },
            { "coincident_a", fixture.CoincidentA }, { "coincident_b", fixture.CoincidentB },
            { "skew_a", fixture.SkewA }, { "skew_b", fixture.SkewB },
            { "short_0_01mm", fixture.Short }, { "short_1e_minus_5mm_native_failure", fixture.ShortNativeFailure },
            { "short_below", fixture.ShortBelow },
            { "near_parallel_above_a", fixture.NearParallelAboveA }, { "near_parallel_above_b", fixture.NearParallelAboveB },
            { "near_parallel_1e_minus_7_failure_a", fixture.NearParallelNativeFailureA },
            { "near_parallel_1e_minus_7_failure_b", fixture.NearParallelNativeFailureB },
            { "near_parallel_below_a", fixture.NearParallelBelowA }, { "near_parallel_below_b", fixture.NearParallelBelowB },
            { "near_parallel_below_distinct_a", fixture.NearParallelBelowDistinctA }, { "near_parallel_below_distinct_b", fixture.NearParallelBelowDistinctB },
            { "parallel_gap_below_a", fixture.ParallelGapBelowA }, { "parallel_gap_below_b", fixture.ParallelGapBelowB },
            { "parallel_gap_above_a", fixture.ParallelGapAboveA }, { "parallel_gap_above_b", fixture.ParallelGapAboveB },
            { "near_coplanar_a", fixture.NearCoplanarA }, { "near_coplanar_b", fixture.NearCoplanarB },
            { "skew_beyond_tolerance_a", fixture.SkewBeyondToleranceA }, { "skew_beyond_tolerance_b", fixture.SkewBeyondToleranceB },
            { "edit_line_one", fixture.EditLineOne }, { "edit_line_two", fixture.EditLineTwo }
        };
    }

    private static PlaneResult RunPositivePlane(string reportPath, Dictionary<string, object> evidence,
        M2OpenModelAdapter adapter, M2ModelBinding binding, Fixture fixture,
        string name, string objective, Line line1, Line line2,
        string[] expectedSketchFeatureIds, PlaneResult samePlaneAs)
    {
        PlaneResult result = null;
        string line1Id = line1.JournalIdentifier;
        string line2Id = line2.JournalIdentifier;
        string[] reproduction = new string[]
        {
            "Use the unique saved F05 fixture and the recorded native line IDs.",
            "Capture line runtime types and all four directed endpoints.",
            "Prepare the two-line datum through M2OpenModelAdapter and compare full before/after model snapshots.",
            "Execute and production-verify the plan, then inspect Plane.Method, Plane.GetAlternate, Plane.GetGeometry, origin, normal, and parent ancestry.",
            "Independently calculate the infinite-line plane and all four endpoint residuals."
        };
        bool passed = RunCase(reportPath, evidence, name, objective, reproduction,
            "Prepare is read-only; execution is APPLIED and production verify passes; native method is TwoLines; native alternate is Three; Plane.GetGeometry contains exactly the requested two NXOpen.Line objects; source Sketch ancestry is present when requested; the independent plane equation, origin membership, unoriented normal, and all four residuals meet tolerance.",
            delegate(Dictionary<string, object> record)
            {
                line1Id = line1.JournalIdentifier;
                line2Id = line2.JournalIdentifier;
                record["input"] = PairInputRecord(binding.RevisionId, line1Id, line2Id, line1, line2);
                record["expected"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "mode", "two_lines" }, { "native_method", "TwoLines" }, { "native_alternate", "Three" },
                    { "line1_id", line1Id }, { "line2_id", line2Id }, { "normal_sign_constrained", false },
                    { "prepare_read_only", true }, { "line1_source_feature_ids", expectedSketchFeatureIds }
                };
                record["tolerances"] = Tolerances();
                Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, fixture.Part);
                M3PlanSnapshot plan = adapter.PrepareDatumPlane(binding, TwoLineRequest(binding.RevisionId, line1Id, line2Id));
                Dictionary<string, object> afterPrepare = ModelDefinitionRecord(adapter, binding, fixture.Part);
                RequireUnchangedPrepare(record, before, afterPrepare);
                RequireTwoLinePlan(plan, line1Id, line2Id);
                if (expectedSketchFeatureIds != null)
                    RequirePlanLineSourceFeatures(plan, expectedSketchFeatureIds[0], expectedSketchFeatureIds[1]);
                record["plan"] = PlanRecord(plan);

                M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
                record["execution"] = ExecutionRecord(execution);
                Require(execution != null && String.Equals(execution.State, "APPLIED", StringComparison.Ordinal),
                    "Two-line datum-plane execution did not return APPLIED.");
                string featureId = ReadFeatureId(execution.Result);
                M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
                record["verification"] = VerificationRecord(verification);
                Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
                    "Two-line datum-plane production verification did not pass.");
                RequireParentAssociationChecks(verification.Checks, expectedSketchFeatureIds);
                record["production_parent_association_evidence"] = AssociationCheckRecord(
                    verification.Checks, expectedSketchFeatureIds);

                Line liveLine1 = FindLineById(fixture.Part, line1Id);
                Line liveLine2 = FindLineById(fixture.Part, line2Id);
                Dictionary<string, object> native = ReadNativeTwoLinePlane(fixture.Part, featureId, liveLine1, liveLine2);
                PlaneOracle oracle = AnalyzePair(liveLine1, liveLine2, true);
                record["native_raw"] = native;
                record["independent_oracle"] = OracleRecord(oracle);
                RequireTwoLineReadback(native, fixture.Part, featureId, liveLine1, liveLine2,
                    expectedSketchFeatureIds, oracle);
                Dictionary<string, object> afterExecution = ModelDefinitionRecord(adapter, binding, fixture.Part);
                record["model_after_execution"] = afterExecution;
                RequireExistingObjectsPreserved(before, afterExecution, featureId);
                if (samePlaneAs != null)
                    RequireSameUnorientedPlane(native, samePlaneAs.Native,
                        "Reversing endpoints or swapping request order changed the defined plane.");
                if (name.IndexOf("invariant", StringComparison.Ordinal) >= 0)
                    Require(samePlaneAs != null,
                        "The baseline plane result is unavailable, so this required orientation-invariance comparison cannot be completed.");

                result = new PlaneResult
                {
                    FeatureId = featureId, Plan = plan, Native = native, Oracle = oracle,
                    Line1Id = line1Id, Line2Id = line2Id,
                    Line1Object = liveLine1, Line2Object = liveLine2
                };
            });
        return passed ? result : null;
    }

    private static void RunRejectedPrepare(string reportPath, Dictionary<string, object> evidence,
        M2OpenModelAdapter adapter, M2ModelBinding binding, Part part,
        string name, M3DatumPlaneRequest request, string objective)
    {
        RunRejectedPrepare(reportPath, evidence, adapter, binding, part, name, request, objective, null);
    }

    private static void RunRejectedPrepare(string reportPath, Dictionary<string, object> evidence,
        M2OpenModelAdapter adapter, M2ModelBinding binding, Part part,
        string name, M3DatumPlaneRequest request, string objective, Func<bool> precondition)
    {
        string[] reproduction = new string[]
        {
            "Use the unique saved F05 fixture and the recorded invalid line pair/request fields.",
            "Capture adapter inspection, all native IDs and line endpoints, modified state, and binding revision.",
            "Call PrepareDatumPlane and record its exception or unexpected plan.",
            "Capture the same full model snapshot and compare it with the pre-prepare snapshot."
        };
        RunCase(reportPath, evidence, name, objective, reproduction,
            "Prepare rejects the invalid two-line request and the complete before/after model snapshot is identical.",
            delegate(Dictionary<string, object> record)
            {
                record["request"] = DatumRequestRecord(request);
                record["expected"] = "Prepare rejection without part definition, IDs, modified state, or revision change";
                record["tolerances"] = Tolerances();
                if (precondition != null)
                    Require(precondition(), "Invalid-line fixture does not satisfy its stated measured rejection boundary.");
                Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, part);
                Exception rejected = null;
                try
                {
                    M3PlanSnapshot unexpected = adapter.PrepareDatumPlane(binding, request);
                    record["unexpected_plan"] = PlanRecord(unexpected);
                }
                catch (Exception ex) { rejected = ex; }
                record["rejection_exception"] = rejected == null ? null : ExceptionRecord(rejected);
                Dictionary<string, object> after = ModelDefinitionRecord(adapter, binding, part);
                record["before_snapshot"] = before;
                record["after_snapshot"] = after;
                bool unchanged = String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal);
                record["prepare_read_only"] = unchanged;
                Require(rejected != null, "Prepare accepted an invalid two-line request.");
                Require(unchanged, "Rejected prepare changed the native model, IDs, modified state, or binding revision.");
            });
    }

    private static void RunExpectedNativeRollbackCase(string reportPath, Dictionary<string, object> evidence,
        M2OpenModelAdapter adapter, M2ModelBinding binding, Fixture fixture,
        string name, string objective, Line line1, Line line2, string observedFailureClass)
    {
        string[] reproduction = new string[]
        {
            "Use the unique saved F05 fixture and the exact measured line IDs/endpoints recorded in this case.",
            "Capture the complete stable semantic/native model snapshot and revision before prepare.",
            "Prepare the geometrically valid two-line pair and prove prepare left the snapshot unchanged.",
            "Execute once and retain the raw native operation exception/message and rollback evidence.",
            "Inspect the part again and compare the restored full model snapshot while allowing the binding revision to advance."
        };
        RunCase(reportPath, evidence, name, objective, reproduction,
            "Execution returns ROLLED_BACK; undo_called, semantic_restored, geometry_restored, history_restored, and bodies_restored are all true; the native failure message is retained; the post-Undo stable model snapshot matches pre-execution; execution is not UNKNOWN or APPLIED.",
            delegate(Dictionary<string, object> record)
            {
                string line1Id = line1.JournalIdentifier;
                string line2Id = line2.JournalIdentifier;
                PlaneOracle independentExpected = AnalyzePair(line1, line2, true);
                record["outcome_class"] = "EXPECTED_NATIVE_ROLLBACK";
                record["input"] = PairInputRecord(binding.RevisionId, line1Id, line2Id, line1, line2);
                record["independent_expected_plane_without_native_pass_claim"] = OracleRecord(independentExpected);
                record["expected_native_failure_class"] = observedFailureClass;
                record["expected"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "prepare_accepts_geometric_pair", true }, { "execution_state", "ROLLED_BACK" },
                    { "undo_called", true }, { "semantic_restored", true }, { "geometry_restored", true },
                    { "history_restored", true }, { "bodies_restored", true },
                    { "applied_or_unknown_state_is_failure", true }
                };
                record["tolerances"] = Tolerances();

                Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, fixture.Part);
                M3PlanSnapshot plan = adapter.PrepareDatumPlane(binding,
                    TwoLineRequest(binding.RevisionId, line1Id, line2Id));
                Dictionary<string, object> afterPrepare = ModelDefinitionRecord(adapter, binding, fixture.Part);
                RequireUnchangedPrepare(record, before, afterPrepare);
                RequireTwoLinePlan(plan, line1Id, line2Id);
                record["plan"] = PlanRecord(plan);
                record["execution_revision_before"] = binding.RevisionId;

                M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
                record["execution"] = ExecutionRecord(execution);
                Require(execution != null && String.Equals(execution.State, "ROLLED_BACK", StringComparison.Ordinal),
                    "Known F05 numerical-domain failure did not report a completed ROLLED_BACK state; UNKNOWN or APPLIED is not acceptable.");
                Require(String.Equals(execution.ErrorCode, "M3_OPERATION_ROLLED_BACK", StringComparison.Ordinal),
                    "Expected rollback execution did not preserve the production rollback error code.");
                Dictionary<string, object> rollback = execution.Result;
                Require(rollback != null, "ROLLED_BACK result omitted rollback evidence.");
                RequireRollbackFlag(rollback, "undo_called");
                RequireRollbackFlag(rollback, "semantic_restored");
                RequireRollbackFlag(rollback, "geometry_restored");
                RequireRollbackFlag(rollback, "history_restored");
                RequireRollbackFlag(rollback, "bodies_restored");
                int updateErrors = Convert.ToInt32(rollback["undo_update_error_count"], CultureInfo.InvariantCulture);
                Require(updateErrors == 0, "NX reported update errors while undoing the expected native failure.");
                string nativeException = ReadOptionalString(rollback, "operation_exception");
                string nativeMessage = ReadOptionalString(rollback, "operation_message");
                Require(!String.IsNullOrWhiteSpace(nativeException) && !String.IsNullOrWhiteSpace(nativeMessage),
                    "Expected native failure exception/message was not preserved in rollback evidence.");
                bool expectedFailureMatched = MatchesExpectedNativeFailure(observedFailureClass, nativeException, nativeMessage);
                record["expected_failure_class"] = observedFailureClass;
                record["expected_failure_class_match"] = expectedFailureMatched;
                Require(expectedFailureMatched,
                    "Execution rolled back for a different failure class than the measured native limitation; this must not pass as an expected rollback.");
                record["native_failure_exception_preserved"] = nativeException;
                record["native_failure_message_preserved"] = nativeMessage;
                record["rollback_flags"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "undo_called", rollback["undo_called"] }, { "semantic_restored", rollback["semantic_restored"] },
                    { "geometry_restored", rollback["geometry_restored"] }, { "history_restored", rollback["history_restored"] },
                    { "bodies_restored", rollback["bodies_restored"] }, { "undo_update_error_count", updateErrors }
                };

                Dictionary<string, object> afterRollback = ModelDefinitionRecord(adapter, binding, fixture.Part);
                record["before_execution_snapshot"] = before;
                record["after_rollback_snapshot"] = afterRollback;
                record["revision_after_rollback"] = binding.RevisionId;
                bool sameStableState = StableModelSnapshotsEqualIgnoringRevision(before, afterRollback);
                record["stable_model_snapshot_restored"] = sameStableState;
                Require(sameStableState,
                    "Post-Undo native IDs, geometry, semantic feature/sketch/body/history inventory, or modified state differ from pre-execution.");
                object writeHalted = FindNamedValue(adapter.GetSession(binding), "writes_halted");
                if (writeHalted is bool)
                {
                    record["writes_halted_after_known_rollback"] = writeHalted;
                    Require(!(bool)writeHalted, "Known ROLLED_BACK execution left writes halted; do not report a passing recovery state.");
                }
                record["unknown_write_state_claimed"] = false;
            });
    }

    private static bool MatchesExpectedNativeFailure(string expectedClass, string nativeException, string nativeMessage)
    {
        if (String.Equals(expectedClass, "native_two_line_definition_readback", StringComparison.Ordinal))
            return String.Equals(nativeException, "M2ModelException", StringComparison.Ordinal) &&
                nativeMessage.IndexOf("\"all_line_endpoints_on_native_plane\":false", StringComparison.Ordinal) >= 0 &&
                nativeMessage.IndexOf("\"normal_matches_plane_unoriented\":false", StringComparison.Ordinal) >= 0;
        if (String.Equals(expectedClass, "native_evaluate_collinear_edge_axis", StringComparison.Ordinal))
            return String.Equals(nativeException, "InvalidOperationException", StringComparison.Ordinal) &&
                nativeMessage.IndexOf("NX two-line DatumPlane failed at Evaluate", StringComparison.Ordinal) >= 0 &&
                nativeMessage.IndexOf("Collinear edge and axis", StringComparison.Ordinal) >= 0;
        return false;
    }

    private static void RequireRollbackFlag(Dictionary<string, object> rollback, string key)
    {
        object value = null;
        Require(rollback.TryGetValue(key, out value) && value is bool && (bool)value,
            "Rollback evidence field must be true: " + key);
    }

    private static bool StableModelSnapshotsEqualIgnoringRevision(Dictionary<string, object> before,
        Dictionary<string, object> after)
    {
        Dictionary<string, object> beforeStable = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, object> afterStable = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object> item in before)
            if (!String.Equals(item.Key, "binding_revision", StringComparison.Ordinal)) beforeStable[item.Key] = item.Value;
        foreach (KeyValuePair<string, object> item in after)
            if (!String.Equals(item.Key, "binding_revision", StringComparison.Ordinal)) afterStable[item.Key] = item.Value;
        return String.Equals(M2Json.CanonicalText(beforeStable), M2Json.CanonicalText(afterStable), StringComparison.Ordinal);
    }

    private static PlaneOracle AnalyzePair(Line line1, Line line2, bool requireValid)
    {
        double[] p0 = Point(line1.StartPoint), p1 = Point(line1.EndPoint);
        double[] q0 = Point(line2.StartPoint), q1 = Point(line2.EndPoint);
        double[] d1 = Subtract(p1, p0), d2 = Subtract(q1, q0);
        double l1 = Length(d1), l2 = Length(d2);
        if (requireValid)
        {
            Require(l1 > MinimumLineLengthMm && l2 > MinimumLineLengthMm,
                "Accepted oracle input contains a line at or below the documented degeneracy limit.");
        }
        double[] u = Normalize(d1), v = Normalize(d2);
        double[] cross = Cross(u, v);
        double crossNorm = Length(cross);
        double[] delta = Subtract(q0, p0);
        bool parallel = crossNorm <= ParallelDirectionCrossTolerance;
        double[] normal;
        double separation = 0.0;
        double coplanarity = 0.0;
        string classification;
        if (parallel)
        {
            double[] perpendicular = Subtract(delta, Scale(u, Dot(delta, u)));
            separation = Length(perpendicular);
            if (requireValid)
                Require(separation > ParallelSeparationToleranceMm,
                    "Accepted oracle input is coincident or has parallel separation at or below tolerance.");
            normal = Normalize(Cross(u, perpendicular));
            classification = separation <= ParallelSeparationToleranceMm ? "parallel_coincident_or_undefined" : "parallel_distinct";
        }
        else
        {
            normal = Normalize(cross);
            coplanarity = Math.Abs(Dot(delta, normal));
            if (requireValid)
                Require(coplanarity <= CoplanarityToleranceMm,
                    "Accepted oracle input exceeds the documented infinite-line coplanarity tolerance.");
            classification = coplanarity <= CoplanarityToleranceMm ? "nonparallel_coplanar" : "skew";
        }
        double offset = Dot(normal, p0);
        double[] residuals = new double[]
        {
            Dot(Subtract(p0, Scale(normal, offset)), normal),
            Dot(Subtract(p1, Scale(normal, offset)), normal),
            Dot(Subtract(q0, Scale(normal, offset)), normal),
            Dot(Subtract(q1, Scale(normal, offset)), normal)
        };
        if (requireValid)
            foreach (double residual in residuals)
                Require(Math.Abs(residual) <= PositionToleranceMm,
                    "An input endpoint exceeds the independent expected-plane residual tolerance.");
        return new PlaneOracle
        {
            Origin = p0, Normal = normal, Offset = offset, DirectionCrossNorm = crossNorm,
            ParallelSeparationMm = separation, CoplanarityMm = coplanarity,
            Line1LengthMm = l1, Line2LengthMm = l2, ParallelByTolerance = parallel,
            EndpointResidualsMm = residuals, Classification = classification
        };
    }

    private static Dictionary<string, object> ReadNativeTwoLinePlane(Part part, string featureId,
        Line expectedLine1, Line expectedLine2)
    {
        Feature feature = FindFeature(part, featureId);
        DatumPlane datum = FindDatumEntity(feature);
        DatumPlaneBuilder builder = part.Features.CreateDatumPlaneBuilder(feature);
        try
        {
            Plane plane = builder.GetPlane();
            Require(plane != null, "DatumPlaneBuilder.GetPlane returned null for the two-line datum.");
            NXObject[] geometry = plane.GetGeometry();
            List<string> geometryIds = new List<string>();
            List<string> geometryTypes = new List<string>();
            if (geometry != null)
                foreach (NXObject item in geometry)
                    if (item != null)
                    {
                        geometryIds.Add(item.JournalIdentifier);
                        geometryTypes.Add(item.GetType().FullName);
                    }
            geometryIds.Sort(StringComparer.Ordinal);
            geometryTypes.Sort(StringComparer.Ordinal);
            Point3d p0, p1, p2, p3;
            datum.GetCornerPoints(out p0, out p1, out p2, out p3);
            Vector3d rawNormal = datum.Normal;
            PlaneTypes.AlternateType alternate = plane.GetAlternate();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "feature_id", feature.JournalIdentifier }, { "datum_entity_id", datum.JournalIdentifier },
                { "datum_entity_feature_id", datum.Feature == null ? null : datum.Feature.JournalIdentifier },
                { "origin_mm", Point(datum.Origin) },
                { "normal_world", Normalize(new double[] { rawNormal.X, rawNormal.Y, rawNormal.Z }) },
                { "x_axis_world", Normalize(Subtract(Point(p1), Point(p0))) },
                { "y_axis_world", Normalize(Subtract(Point(p3), Point(p0))) },
                { "feature_parent_ids", FeatureIds(feature.GetParents()) },
                { "feature_ancestor_ids", FeatureAncestorIds(feature) },
                { "plane_method", plane.Method.ToString() },
                { "plane_method_numeric", Convert.ToInt32(plane.Method, CultureInfo.InvariantCulture) },
                { "plane_alternate", alternate.ToString() },
                { "plane_alternate_numeric", Convert.ToInt32(alternate, CultureInfo.InvariantCulture) },
                { "geometry_ids", geometryIds }, { "geometry_types", geometryTypes },
                { "line1_id", expectedLine1.JournalIdentifier }, { "line2_id", expectedLine2.JournalIdentifier },
                { "line1_type", expectedLine1.GetType().FullName }, { "line2_type", expectedLine2.GetType().FullName },
                { "line1_start_mm", Point(expectedLine1.StartPoint) }, { "line1_end_mm", Point(expectedLine1.EndPoint) },
                { "line2_start_mm", Point(expectedLine2.StartPoint) }, { "line2_end_mm", Point(expectedLine2.EndPoint) },
                { "line1_feature_parent_not_required", true }, { "line2_feature_parent_not_required", true }
            };
        }
        finally { builder.Destroy(); }
    }

    private static void RequireTwoLineReadback(Dictionary<string, object> native, Part part,
        string featureId, Line line1, Line line2, string[] expectedSketchFeatureIds, PlaneOracle oracle)
    {
        Require(String.Equals(ReadRequiredString(native, "plane_method"), PlaneTypes.MethodType.TwoLines.ToString(), StringComparison.Ordinal),
            "Native Plane.Method is not TwoLines.");
        Require(String.Equals(ReadRequiredString(native, "plane_alternate"), PlaneTypes.AlternateType.Three.ToString(), StringComparison.Ordinal),
            "Native Plane.GetAlternate() is not AlternateType.Three.");
        Require(String.Equals(ReadRequiredString(native, "line1_type"), typeof(Line).FullName, StringComparison.Ordinal) &&
            String.Equals(ReadRequiredString(native, "line2_type"), typeof(Line).FullName, StringComparison.Ordinal),
            "One or both selected native inputs are not NXOpen.Line.");
        Require(!String.Equals(line1.JournalIdentifier, line2.JournalIdentifier, StringComparison.Ordinal),
            "Two-line plane references the same native line twice.");
        List<string> geometryIds = ReadStringList(native, "geometry_ids");
        Require(geometryIds.Count == 2 && geometryIds.Contains(line1.JournalIdentifier) && geometryIds.Contains(line2.JournalIdentifier),
            "Plane.GetGeometry() does not contain exactly the two requested native Line IDs.");
        List<string> geometryTypes = ReadStringList(native, "geometry_types");
        Require(geometryTypes.Count == 2 && geometryTypes[0] == typeof(Line).FullName && geometryTypes[1] == typeof(Line).FullName,
            "Plane.GetGeometry() contains a non-Line or unexpected extra object.");

        double[] origin = ReadVector(native, "origin_mm", 3);
        double[] actualNormal = Normalize(ReadVector(native, "normal_world", 3));
        double originResidual = Dot(Subtract(origin, oracle.Origin), oracle.Normal);
        double alignedOffset = Dot(actualNormal, oracle.Normal) < 0.0 ? -oracle.Offset : oracle.Offset;
        double nativeOffset = Dot(actualNormal, origin);
        double offsetError = Math.Abs(nativeOffset - alignedOffset);
        double normalDistance = Math.Min(VectorDistance(actualNormal, oracle.Normal),
            VectorDistance(actualNormal, Scale(oracle.Normal, -1.0)));
        double[] nativeResiduals = new double[]
        {
            Dot(Subtract(Point(line1.StartPoint), origin), actualNormal),
            Dot(Subtract(Point(line1.EndPoint), origin), actualNormal),
            Dot(Subtract(Point(line2.StartPoint), origin), actualNormal),
            Dot(Subtract(Point(line2.EndPoint), origin), actualNormal)
        };
        native["independent_origin_signed_distance_mm"] = originResidual;
        native["independent_plane_offset_error_mm"] = offsetError;
        native["unoriented_normal_error"] = normalDistance;
        native["independent_endpoint_residuals_mm"] = oracle.EndpointResidualsMm;
        native["native_endpoint_residuals_mm"] = nativeResiduals;
        Require(Math.Abs(originResidual) <= PositionToleranceMm,
            "DatumPlane.Origin does not lie on the independently calculated infinite-line plane.");
        Require(offsetError <= PositionToleranceMm,
            "Native and independent plane offsets differ beyond the model-value tolerance.");
        Require(normalDistance <= DirectionTolerance,
            "Native plane unit normal differs from the independent normal up to sign.");
        foreach (double residual in oracle.EndpointResidualsMm)
            Require(Math.Abs(residual) <= PositionToleranceMm,
                "One of the four input endpoints misses the independent plane by more than 1e-6 mm.");
        foreach (double residual in nativeResiduals)
            Require(Math.Abs(residual) <= PositionToleranceMm,
                "One of the four input endpoints misses the actual native plane by more than 1e-6 mm.");

        if (expectedSketchFeatureIds != null)
        {
            List<string> ancestors = ReadStringList(native, "feature_ancestor_ids");
            foreach (string sketchFeatureId in expectedSketchFeatureIds)
                Require(ancestors.Contains(sketchFeatureId),
                    "Two-line datum feature ancestry does not include both source Sketch features.");
            native["expected_sketch_feature_ids"] = expectedSketchFeatureIds;
            native["both_sketch_sources_in_ancestry"] = true;
        }
        native["plane_method_matches"] = true;
        native["plane_alternate_matches"] = true;
        native["both_exact_native_lines_in_geometry"] = true;
        native["independent_oracle_passes"] = true;
    }

    private static void RequireSameUnorientedPlane(Dictionary<string, object> actual,
        Dictionary<string, object> expected, string message)
    {
        double[] actualOrigin = ReadVector(actual, "origin_mm", 3);
        double[] expectedOrigin = ReadVector(expected, "origin_mm", 3);
        double[] actualNormal = Normalize(ReadVector(actual, "normal_world", 3));
        double[] expectedNormal = Normalize(ReadVector(expected, "normal_world", 3));
        double orientation = Math.Min(VectorDistance(actualNormal, expectedNormal),
            VectorDistance(actualNormal, Scale(expectedNormal, -1.0)));
        double planeOffset = Math.Abs(Dot(Subtract(actualOrigin, expectedOrigin), expectedNormal));
        Require(orientation <= DirectionTolerance && planeOffset <= PositionToleranceMm,
            message + " Normal error=" + orientation.ToString("R", CultureInfo.InvariantCulture) +
            "; plane offset error mm=" + planeOffset.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void RequirePlaneChanged(Dictionary<string, object> actual,
        Dictionary<string, object> expected, string message)
    {
        double[] actualOrigin = ReadVector(actual, "origin_mm", 3);
        double[] expectedOrigin = ReadVector(expected, "origin_mm", 3);
        double[] actualNormal = Normalize(ReadVector(actual, "normal_world", 3));
        double[] expectedNormal = Normalize(ReadVector(expected, "normal_world", 3));
        double orientationError = Math.Min(VectorDistance(actualNormal, expectedNormal),
            VectorDistance(actualNormal, Scale(expectedNormal, -1.0)));
        double planeOffset = Math.Abs(Dot(Subtract(actualOrigin, expectedOrigin), expectedNormal));
        Require(orientationError > 1e-4 || planeOffset > 10.0 * PositionToleranceMm,
            message + " Orientation error=" + orientationError.ToString("R", CultureInfo.InvariantCulture) +
            "; plane offset change mm=" + planeOffset.ToString("R", CultureInfo.InvariantCulture));
    }

    private static Dictionary<string, object> OracleRecord(PlaneOracle oracle)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "classification", oracle.Classification }, { "origin_mm", oracle.Origin },
            { "normal_unoriented", oracle.Normal }, { "offset_mm", oracle.Offset },
            { "line1_length_mm", oracle.Line1LengthMm }, { "line2_length_mm", oracle.Line2LengthMm },
            { "unit_direction_cross_norm", oracle.DirectionCrossNorm },
            { "parallel_by_threshold", oracle.ParallelByTolerance },
            { "parallel_separation_mm", oracle.ParallelSeparationMm },
            { "coplanarity_mm", oracle.CoplanarityMm },
            { "four_endpoint_residuals_mm", oracle.EndpointResidualsMm }
        };
    }

    private static void RequireTwoLinePlan(M3PlanSnapshot plan, string line1Id, string line2Id)
    {
        Require(plan != null, "PrepareDatumPlane returned no two-line plan.");
        Require(String.Equals(plan.Kind, "create_datum_plane", StringComparison.Ordinal),
            "Two-line request returned the wrong plan kind.");
        Require(plan.PublicView != null, "Two-line plan omitted its public view.");
        RequireDictionaryContains(plan.PublicView, "mode", "two_lines");
        RequireDictionaryContains(plan.PublicView, "line1_id", line1Id);
        RequireDictionaryContains(plan.PublicView, "line2_id", line2Id);
    }

    private static SketchResult RunF01SupportCase(string reportPath, Dictionary<string, object> evidence,
        M2OpenModelAdapter adapter, M2ModelBinding binding, Fixture fixture,
        PlaneResult supportPlane, PlaneResult[] existingPlaneResults)
    {
        SketchResult sketchResult = null;
        bool passed = RunCase(reportPath, evidence, "F05_two_line_plane_supports_F01_sketch",
            "Use the applied two-line datum as the support for a production F01 sketch and inspect native support ancestry.",
            new string[]
            {
                "Use the independently verified F05 crossing datum feature.",
                "Prepare a simple line Sketch with support_datum_plane_id set to the F05 feature.",
                "Compare full pre/post prepare snapshots, then execute and production-verify the sketch.",
                "Inspect the sketch support association and resolve native sketch feature ancestry.",
                "Refresh all sketch line identifiers after inserting the new sketch."
            },
            "The Sketch is APPLIED and production-verified; inspection reports the exact F05 support feature; native sketch feature ancestry reaches it; existing fixture features and lines remain; any journal IDs are refreshed after sketch insertion.",
            delegate(Dictionary<string, object> record)
            {
                string supportId = supportPlane.FeatureId;
                double[] xAxis = ReadVector(supportPlane.Native, "x_axis_world", 3);
                M3SketchRequest request = new M3SketchRequest
                {
                    RevisionId = binding.RevisionId, SupportDatumPlaneId = supportId,
                    XAxis = xAxis, Geometries = new List<M3SketchGeometryRequest>
                    {
                        new M3SketchGeometryRequest
                        {
                            Id = "f05-supported-sketch-line", Type = "line",
                            StartMm = new double[] { 1.0, 2.0 }, EndMm = new double[] { 9.0, 2.0 }
                        }
                    }
                };
                record["input"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "support_datum_plane_id", supportId }, { "x_axis_world", xAxis },
                    { "geometry_start_local_mm", new double[] { 1.0, 2.0 } },
                    { "geometry_end_local_mm", new double[] { 9.0, 2.0 } }
                };
                record["expected"] = "F01 sketch support is associated to the exact F05 two-line datum and remains in native feature ancestry";
                record["tolerances"] = Tolerances();
                Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, fixture.Part);
                M3PlanSnapshot plan = adapter.PrepareSketch(binding, request);
                Dictionary<string, object> afterPrepare = ModelDefinitionRecord(adapter, binding, fixture.Part);
                RequireUnchangedPrepare(record, before, afterPrepare);
                record["plan"] = PlanRecord(plan);
                M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
                record["execution"] = ExecutionRecord(execution);
                Require(execution != null && execution.State == "APPLIED", "F01 sketch on the two-line datum did not return APPLIED.");
                M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
                record["verification"] = VerificationRecord(verification);
                Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
                    "F01 sketch on the two-line datum did not production-verify.");
                string sketchId = ReadOptionalString(execution.Result, "sketch_id");
                if (String.IsNullOrWhiteSpace(sketchId)) sketchId = plan.SketchId;
                Require(!String.IsNullOrWhiteSpace(sketchId), "F01 support case result omitted sketch_id.");
                Sketch sketch = FindSketch(fixture.Part, sketchId);
                Dictionary<string, object> inspected = adapter.InspectSketch(binding, sketchId);
                record["sketch_id"] = sketch.JournalIdentifier;
                record["sketch_feature_id"] = sketch.Feature.JournalIdentifier;
                record["inspection"] = inspected;
                record["native_sketch_ancestor_ids"] = FeatureAncestorIds(sketch.Feature);
                RequireAnyDictionaryPropertyValue(inspected,
                    new string[] { "datum_plane_id", "support_datum_plane_id" }, supportId);
                Require(FeatureAncestorIds(sketch.Feature).Contains(supportId),
                    "F01 sketch feature ancestry does not reach the requested two-line datum.");
                RefreshSketchLineReferences(adapter, binding, fixture);
                foreach (PlaneResult existing in existingPlaneResults)
                    if (existing != null) RefreshPlaneResultLineIdentifiers(existing);
                record["sketch_line_ids_after_feature_insertion"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "fixture_sketch_one", fixture.SketchLineOneId }, { "fixture_sketch_two", fixture.SketchLineTwoId }
                };
                Dictionary<string, object> afterExecution = ModelDefinitionRecord(adapter, binding, fixture.Part);
                record["model_after_execution"] = afterExecution;
                RequireF01SketchObjectsPreserved(before, afterExecution,
                    sketch.JournalIdentifier, sketch.Feature.JournalIdentifier, supportId);
                sketchResult = new SketchResult
                {
                    SketchId = sketch.JournalIdentifier, FeatureId = sketch.Feature.JournalIdentifier,
                    SupportDatumId = supportId, Inspection = inspected
                };
            });
        return passed ? sketchResult : null;
    }

    private static void RunSaveReopenCase(string reportPath, Dictionary<string, object> evidence,
        Session session, M2OpenModelAdapter adapter, M2ModelBinding binding, Fixture fixture,
        PlaneResult[] planes, SketchResult supportSketch, string savedPath)
    {
        RunCase(reportPath, evidence, "F05_save_copy_reopen_native_roundtrip",
            "Save and reopen the isolated part containing both intersecting and parallel two-line datum modes and the supported F01 sketch.",
            new string[]
            {
                "Refresh current line IDs for both sketch-source and raw-line plane associations.",
                "Call production SaveCopy to a unique path with reopen=true.",
                "Resolve both F05 plane features, their exact input Lines, and the F01 support Sketch in the reopened Work Part.",
                "Read native method, alternate, GetGeometry IDs, parents/ancestry, datum origin/normal, and endpoints directly from NXOpen.",
                "Recalculate both independent infinite-line plane equations and all four endpoint residuals."
            },
            "SaveCopy is SAVED/reopened and all production roundtrip checks are VERIFIED; both modes remain TwoLines/Three with exact line association and source Sketch ancestry; each plane passes the independent geometry oracle; F01 sketch support still reaches the crossing datum.",
            delegate(Dictionary<string, object> record)
            {
                Require(planes != null && planes.Length >= 2 && planes[0] != null && planes[1] != null,
                    "Both intersecting and parallel baseline plane results are required for roundtrip coverage.");
                Require(supportSketch != null, "The F01 sketch-on-F05 prerequisite did not pass.");
                foreach (PlaneResult plane in planes) RefreshPlaneResultLineIdentifiers(plane);
                M2SaveCopyResult saved = adapter.SaveCopy(binding, savedPath, true);
                record["save_copy"] = SaveCopyRecord(saved);
                Require(saved != null && saved.State == "SAVED" && saved.Reopened,
                    "Production SaveCopy did not save and reopen the unique F05 copy.");
                Require(ChecksPass(saved.Checks), "Production SaveCopy roundtrip checks were not all VERIFIED.");
                Require(String.Equals(Path.GetFullPath(savedPath), Path.GetFullPath(saved.PartPath), StringComparison.OrdinalIgnoreCase),
                    "SaveCopy returned a different target path.");
                Part reopened = FindOwnedPart(session, savedPath);
                Require(reopened != null, "Reopened F05 copy is not loaded in the NX session.");
                fixture.Part = reopened;

                Dictionary<string, object> reopenedInspection = adapter.InspectPart(binding);
                List<object> reopenedPlanes = new List<object>();
                for (int i = 0; i < planes.Length; i++)
                {
                    PlaneResult expected = planes[i];
                    Line line1 = FindLineById(reopened, expected.Line1Id);
                    Line line2 = FindLineById(reopened, expected.Line2Id);
                    Dictionary<string, object> native = ReadNativeTwoLinePlane(reopened, expected.FeatureId, line1, line2);
                    PlaneOracle oracle = AnalyzePair(line1, line2, true);
                    string[] expectedSources = expected == planes[1]
                        ? new string[] { fixture.SketchOneFeatureId, fixture.SketchTwoFeatureId } : null;
                    RequireTwoLineReadback(native, reopened, expected.FeatureId, line1, line2, expectedSources, oracle);
                    Dictionary<string, object> inspectionDatum = FindFeatureDatumPlaneRecord(reopenedInspection, expected.FeatureId);
                    RequireInspectedTwoLineAssociations(inspectionDatum, line1.JournalIdentifier,
                        line2.JournalIdentifier, expectedSources);
                    reopenedPlanes.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "feature_id", expected.FeatureId }, { "line1_id", line1.JournalIdentifier },
                        { "line2_id", line2.JournalIdentifier }, { "native", native },
                        { "production_inspection_datum_plane", inspectionDatum },
                        { "independent_oracle", OracleRecord(oracle) }
                    });
                }
                Sketch reopenedSketch = FindSketch(reopened, supportSketch.SketchId);
                Require(reopenedSketch != null && reopenedSketch.Feature != null,
                    "F01 sketch supported by a two-line plane did not survive reopen.");
                List<string> sketchAncestors = FeatureAncestorIds(reopenedSketch.Feature);
                Require(sketchAncestors.Contains(supportSketch.SupportDatumId),
                    "Reopened F01 sketch ancestry no longer reaches the two-line datum.");
                Dictionary<string, object> inspectedSketch = adapter.InspectSketch(binding, reopenedSketch.JournalIdentifier);
                RequireAnyDictionaryPropertyValue(inspectedSketch,
                    new string[] { "datum_plane_id", "support_datum_plane_id" }, supportSketch.SupportDatumId);
                record["roundtrip_path"] = saved.PartPath;
                record["reopened_revision_id"] = binding.RevisionId;
                record["reopened_part_inspection"] = reopenedInspection;
                record["reopened_two_line_planes"] = reopenedPlanes;
                record["reopened_f01_sketch_id"] = reopenedSketch.JournalIdentifier;
                record["reopened_f01_sketch_feature_id"] = reopenedSketch.Feature.JournalIdentifier;
                record["reopened_f01_sketch_ancestor_ids"] = sketchAncestors;
                record["reopened_f01_sketch_inspection"] = inspectedSketch;
            });
    }

    private static void RunLineEditRevisionUndoCase(string reportPath, Dictionary<string, object> evidence,
        Session session, M2OpenModelAdapter adapter, M2ModelBinding binding, Fixture fixture,
        string name, Line line1, Line line2, bool editLine1)
    {
        RunCase(reportPath, evidence, name,
            "Edit one selected native line under Undo, prove the associative two-line datum follows, reject stale state, and restore the verified baseline.",
            new string[]
            {
                "Create a fresh two-line feature from the current line IDs and production-verify its baseline.",
                "Prepare a separate plan against the same pre-edit revision and verify prepare is read-only.",
                "Under one invisible Undo mark, change only the selected line's end point so the pair remains nonparallel and coplanar but defines a changed plane.",
                "Update NX, refresh through adapter inspection, and independently check the updated native datum plane.",
                "Execute the stale plan, re-verify the old plan, then Undo and verify line, plane, revision behavior, and old plan recovery."
            },
            "Changed-line plane remains APPLIED/associative and passes the independent oracle; native associations remain exact; binding revision changes; stale execution is UNCHANGED; old verification fails after edit; Undo restores endpoints and plane and the applied plan verifies again.",
            delegate(Dictionary<string, object> record)
            {
                line1 = FindLineById(fixture.Part, line1.JournalIdentifier);
                line2 = FindLineById(fixture.Part, line2.JournalIdentifier);
                string line1Id = line1.JournalIdentifier;
                string line2Id = line2.JournalIdentifier;
                M3DatumPlaneRequest baselineRequest = TwoLineRequest(binding.RevisionId, line1Id, line2Id);
                Dictionary<string, object> beforeBaselinePrepare = ModelDefinitionRecord(adapter, binding, fixture.Part);
                M3PlanSnapshot baselinePlan = adapter.PrepareDatumPlane(binding, baselineRequest);
                Dictionary<string, object> afterBaselinePrepare = ModelDefinitionRecord(adapter, binding, fixture.Part);
                RequireUnchangedPrepare(record, beforeBaselinePrepare, afterBaselinePrepare);
                RequireTwoLinePlan(baselinePlan, line1Id, line2Id);
                M3ExecutionResult baselineExecution = adapter.ExecutePlan(binding, baselinePlan);
                record["fresh_baseline_execution"] = ExecutionRecord(baselineExecution);
                Require(baselineExecution != null && baselineExecution.State == "APPLIED",
                    "Fresh two-line edit baseline did not return APPLIED.");
                string featureId = ReadFeatureId(baselineExecution.Result);
                M3VerificationResult baselineVerification = adapter.VerifyPlan(binding, baselinePlan);
                record["fresh_baseline_verification"] = VerificationRecord(baselineVerification);
                Require(baselineVerification != null && baselineVerification.Verified && ChecksPass(baselineVerification.Checks),
                    "Fresh two-line edit baseline did not production-verify.");
                Dictionary<string, object> nativeBefore = ReadNativeTwoLinePlane(fixture.Part, featureId, line1, line2);
                PlaneOracle oracleBefore = AnalyzePair(line1, line2, true);
                RequireTwoLineReadback(nativeBefore, fixture.Part, featureId, line1, line2, null, oracleBefore);

                Dictionary<string, object> beforeStalePrepare = ModelDefinitionRecord(adapter, binding, fixture.Part);
                M3PlanSnapshot stalePlan = adapter.PrepareDatumPlane(binding,
                    TwoLineRequest(binding.RevisionId, line1Id, line2Id));
                Dictionary<string, object> afterStalePrepare = ModelDefinitionRecord(adapter, binding, fixture.Part);
                RequireUnchangedPrepare(record, beforeStalePrepare, afterStalePrepare);
                record["stale_plan"] = PlanRecord(stalePlan);
                RequireTwoLinePlan(stalePlan, line1Id, line2Id);

                Point3d startBefore = editLine1 ? line1.StartPoint : line2.StartPoint;
                Point3d endBefore = editLine1 ? line1.EndPoint : line2.EndPoint;
                double[] baselineNewDirection = editLine1
                    ? Normalize(new double[] { 0.0, 1.0, 0.0 })
                    : Normalize(new double[] { 1.0, 0.0, 0.0 });
                double[] newEnd = Add(Point(startBefore), Scale(baselineNewDirection, EditLengthMm));
                string editedLineId = editLine1 ? line1Id : line2Id;
                Line editedLine = editLine1 ? line1 : line2;
                record["input"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "edited_line", editLine1 ? "line1" : "line2" },
                    { "line1_id", line1Id }, { "line2_id", line2Id }, { "edited_line_id", editedLineId },
                    { "start_before_mm", Point(startBefore) }, { "end_before_mm", Point(endBefore) },
                    { "end_requested_mm", newEnd }, { "fresh_feature_id", featureId },
                    { "fresh_revision_id", binding.RevisionId }
                };
                record["baseline_native"] = nativeBefore;
                record["baseline_oracle"] = OracleRecord(oracleBefore);
                Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible,
                    "F05 acceptance two-line input edit " + (editLine1 ? "line1" : "line2"));
                bool markActive = true;
                try
                {
                    editedLine.SetEndpoints(startBefore, ToPoint(newEnd));
                    int updateErrors = session.UpdateManager.DoUpdate(mark);
                    record["nx_update_error_count"] = updateErrors;
                    Require(updateErrors == 0, "NX update reported errors after the selected line endpoint edit.");
                    adapter.InspectPart(binding);
                    string revisionAfter = binding.RevisionId;
                    Require(!String.Equals(revisionAfter, baselinePlan.RevisionId, StringComparison.Ordinal),
                        "Input line edit did not change the adapter model revision.");
                    line1 = FindLineById(fixture.Part, line1Id);
                    line2 = FindLineById(fixture.Part, line2Id);
                    Dictionary<string, object> nativeAfter = ReadNativeTwoLinePlane(fixture.Part, featureId, line1, line2);
                    PlaneOracle oracleAfter = AnalyzePair(line1, line2, true);
                    RequireTwoLineReadback(nativeAfter, fixture.Part, featureId, line1, line2, null, oracleAfter);
                    record["revision_after_edit"] = revisionAfter;
                    record["edited_line_after"] = LineRecord(editedLine);
                    record["native_after_edit"] = nativeAfter;
                    record["oracle_after_edit"] = OracleRecord(oracleAfter);
                    RequireVectorClose(Point(editedLine.StartPoint), Point(startBefore), PositionToleranceMm,
                        "Selected line start point changed even though only its end point was edited.");
                    RequireVectorClose(Point(editedLine.EndPoint), newEnd, PositionToleranceMm,
                        "Selected line endpoint does not match the requested edit.");
                    RequirePlaneChanged(nativeAfter, nativeBefore,
                        "Editing one input line did not change the associated two-line plane.");
                    M3ExecutionResult stale = adapter.ExecutePlan(binding, stalePlan);
                    record["stale_execution_after_edit"] = ExecutionRecord(stale);
                    Require(stale != null && String.Equals(stale.State, "UNCHANGED", StringComparison.Ordinal),
                        "A plan prepared before the line edit did not return UNCHANGED.");
                    RequireOldPlanVerificationFails(record, adapter, binding, baselinePlan,
                        "The old applied two-line plan still verified after the input line changed.");
                }
                finally
                {
                    if (markActive)
                    {
                        session.UndoToMark(mark, "F05 acceptance two-line input edit");
                        session.DeleteUndoMark(mark, "F05 acceptance two-line input edit");
                        markActive = false;
                    }
                }
                adapter.InspectPart(binding);
                line1 = FindLineById(fixture.Part, line1Id);
                line2 = FindLineById(fixture.Part, line2Id);
                editedLine = editLine1 ? line1 : line2;
                Dictionary<string, object> nativeRestored = ReadNativeTwoLinePlane(fixture.Part, featureId, line1, line2);
                PlaneOracle oracleRestored = AnalyzePair(line1, line2, true);
                RequireTwoLineReadback(nativeRestored, fixture.Part, featureId, line1, line2, null, oracleRestored);
                record["revision_after_undo"] = binding.RevisionId;
                record["edited_line_after_undo"] = LineRecord(editedLine);
                record["native_after_undo"] = nativeRestored;
                record["oracle_after_undo"] = OracleRecord(oracleRestored);
                RequireVectorClose(Point(editedLine.StartPoint), Point(startBefore), PositionToleranceMm,
                    "Undo did not restore the selected line start point.");
                RequireVectorClose(Point(editedLine.EndPoint), Point(endBefore), PositionToleranceMm,
                    "Undo did not restore the selected line end point.");
                RequireSameUnorientedPlane(nativeRestored, nativeBefore,
                    "Undo did not restore the original two-line datum plane.");
                M3VerificationResult restored = adapter.VerifyPlan(binding, baselinePlan);
                record["baseline_verification_after_undo"] = VerificationRecord(restored);
                Require(restored != null && restored.Verified && ChecksPass(restored.Checks),
                    "Fresh applied two-line plan did not verify after Undo restored its inputs.");
            });
    }

    private static void RequireOldPlanVerificationFails(Dictionary<string, object> record,
        M2OpenModelAdapter adapter, M2ModelBinding binding, M3PlanSnapshot appliedPlan, string failureMessage)
    {
        bool passed = false;
        try
        {
            M3VerificationResult changed = adapter.VerifyPlan(binding, appliedPlan);
            record["old_applied_plan_verification_after_edit"] = VerificationRecord(changed);
            passed = changed != null && changed.Verified;
        }
        catch (Exception ex)
        {
            record["old_applied_plan_verification_exception_after_edit"] = ExceptionRecord(ex);
        }
        Require(!passed, failureMessage);
    }

    private static void RefreshSketchLineReferences(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Fixture fixture)
    {
        Dictionary<string, object> one = adapter.InspectSketch(binding, fixture.SketchOneId);
        Dictionary<string, object> two = adapter.InspectSketch(binding, fixture.SketchTwoId);
        fixture.SketchLineOneId = FindSketchLineId(one, "f05-fixture-line-one");
        fixture.SketchLineTwoId = FindSketchLineId(two, "f05-fixture-line-two");
        fixture.SketchLineOne = FindLineById(fixture.Part, fixture.SketchLineOneId);
        fixture.SketchLineTwo = FindLineById(fixture.Part, fixture.SketchLineTwoId);
        Dictionary<string, object> arc = adapter.InspectSketch(binding, fixture.ArcSketchId);
        fixture.WrongTypeCurveId = FindSketchGeometryIdByKind(arc, "ARC");
    }

    private static void RefreshPlaneResultLineIdentifiers(PlaneResult result)
    {
        if (result == null) return;
        result.Line1Id = result.Line1Object.JournalIdentifier;
        result.Line2Id = result.Line2Object.JournalIdentifier;
    }

    private static void RequirePlanLineSourceFeatures(M3PlanSnapshot plan, string line1FeatureId, string line2FeatureId)
    {
        List<string> line1Sources = ReadStringList(plan.PublicView, "line1_source_feature_ids");
        List<string> line2Sources = ReadStringList(plan.PublicView, "line2_source_feature_ids");
        Require(line1Sources.Contains(line1FeatureId),
            "Two-line plan did not attribute line1_id to its source Sketch feature.");
        Require(line2Sources.Contains(line2FeatureId),
            "Two-line plan did not attribute line2_id to its source Sketch feature.");
    }

    private static void RequireParentAssociationChecks(Dictionary<string, object> checks,
        string[] expectedSketchFeatureIds)
    {
        object parentMatch = FindNamedValue(checks, "parent_reference_matches");
        Require(parentMatch is bool && (bool)parentMatch,
            "Production verification did not report parent_reference_matches=true.");
        if (expectedSketchFeatureIds == null) return;
        List<string> line1Sources = ReadNestedStringList(checks, "line1_source_feature_ids");
        List<string> line2Sources = ReadNestedStringList(checks, "line2_source_feature_ids");
        Require(line1Sources.Contains(expectedSketchFeatureIds[0]),
            "Production verify evidence omitted the line1 source Sketch feature.");
        Require(line2Sources.Contains(expectedSketchFeatureIds[1]),
            "Production verify evidence omitted the line2 source Sketch feature.");
    }

    private static Dictionary<string, object> AssociationCheckRecord(Dictionary<string, object> checks,
        string[] expectedSketchFeatureIds)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "parent_reference_matches", FindNamedValue(checks, "parent_reference_matches") },
            { "line1_source_feature_ids", FindNamedValue(checks, "line1_source_feature_ids") },
            { "line2_source_feature_ids", FindNamedValue(checks, "line2_source_feature_ids") },
            { "expected_line1_source_feature_id", expectedSketchFeatureIds == null ? null : expectedSketchFeatureIds[0] },
            { "expected_line2_source_feature_id", expectedSketchFeatureIds == null ? null : expectedSketchFeatureIds[1] }
        };
    }

    private static List<string> ReadNestedStringList(object root, string key)
    {
        object value = FindNamedValue(root, key);
        Require(value != null, "Nested source feature evidence is missing: " + key);
        return ObjectStringList(value);
    }

    private static object FindNamedValue(object value, string key)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            object found;
            if (dictionary.TryGetValue(key, out found)) return found;
            foreach (object child in dictionary.Values)
            {
                object nested = FindNamedValue(child, key);
                if (nested != null) return nested;
            }
        }
        List<object> list = value as List<object>;
        if (list != null)
            foreach (object child in list)
            {
                object nested = FindNamedValue(child, key);
                if (nested != null) return nested;
            }
        object[] array = value as object[];
        if (array != null)
            foreach (object child in array)
            {
                object nested = FindNamedValue(child, key);
                if (nested != null) return nested;
            }
        return null;
    }

    private static List<string> ObjectStringList(object value)
    {
        List<string> result = new List<string>();
        List<object> objects = value as List<object>;
        if (objects != null)
        {
            foreach (object item in objects) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            return result;
        }
        IList<string> strings = value as IList<string>;
        if (strings != null)
        {
            foreach (string item in strings) result.Add(item);
            return result;
        }
        string[] stringArray = value as string[];
        if (stringArray != null)
        {
            result.AddRange(stringArray);
            return result;
        }
        object[] objectArray = value as object[];
        if (objectArray != null)
        {
            foreach (object item in objectArray) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            return result;
        }
        throw new InvalidOperationException("Expected an array of source feature identifiers.");
    }

    private static Dictionary<string, object> FindFeatureDatumPlaneRecord(Dictionary<string, object> inspection,
        string featureId)
    {
        object rawFeatures = null;
        Require(inspection != null && inspection.TryGetValue("features", out rawFeatures),
            "Part inspection omitted feature records.");
        List<object> features = rawFeatures as List<object>;
        Require(features != null, "Part inspection feature inventory has an unexpected type.");
        foreach (object raw in features)
        {
            Dictionary<string, object> feature = raw as Dictionary<string, object>;
            if (feature == null) continue;
            string id = ReadOptionalString(feature, "journal_id");
            if (!String.Equals(id, featureId, StringComparison.Ordinal)) continue;
            object rawDatum;
            Require(feature.TryGetValue("datum_plane", out rawDatum),
                "F05 feature inspection omitted its native datum_plane definition.");
            Dictionary<string, object> datum = rawDatum as Dictionary<string, object>;
            Require(datum != null, "F05 native datum_plane definition has an unexpected type.");
            return datum;
        }
        throw new InvalidOperationException("Part inspection did not contain the F05 feature: " + featureId);
    }

    private static void RequireInspectedTwoLineAssociations(Dictionary<string, object> datum,
        string line1Id, string line2Id, string[] expectedSketchFeatureIds)
    {
        RequireDictionaryContains(datum, "mode", "two_lines");
        RequireDictionaryContains(datum, "native_method", "TwoLines");
        RequireDictionaryContains(datum, "native_alternate", "Three");
        RequireDictionaryContains(datum, "line1_id", line1Id);
        RequireDictionaryContains(datum, "line2_id", line2Id);
        object parentValue = null;
        Require(datum.TryGetValue("parent_reference_matches", out parentValue) && parentValue is bool && (bool)parentValue,
            "Reopened production record parent_reference_matches is not true.");
        object geometryValue = null;
        Require(datum.TryGetValue("native_geometry_line_ids", out geometryValue),
            "Reopened production record omitted native_geometry_line_ids.");
        List<string> geometryIds = ObjectStringList(geometryValue);
        Require(geometryIds.Count == 2 && geometryIds.Contains(line1Id) && geometryIds.Contains(line2Id),
            "Reopened production record does not retain both exact line IDs in native geometry.");
        RequireDictionaryContains(datum, "native_method_matches", true);
        RequireDictionaryContains(datum, "native_alternate_matches", true);
        RequireDictionaryContains(datum, "all_line_endpoints_on_native_plane", true);
        if (expectedSketchFeatureIds != null)
        {
            List<string> line1Sources = ReadStringList(datum, "line1_source_feature_ids");
            List<string> line2Sources = ReadStringList(datum, "line2_source_feature_ids");
            Require(line1Sources.Contains(expectedSketchFeatureIds[0]) && line2Sources.Contains(expectedSketchFeatureIds[1]),
                "Reopened production record did not preserve the two source Sketch feature associations.");
        }
    }

    private static string FindSketchLineId(Dictionary<string, object> inspected, string geometryId)
    {
        string id = FindSketchGeometryIdByKind(inspected, "LINE");
        Require(!String.IsNullOrWhiteSpace(id), "InspectSketch did not expose an NXOpen.Line geometry record: " + geometryId);
        return id;
    }

    private static string FindSketchGeometryIdByKind(Dictionary<string, object> inspected, string expectedKind)
    {
        Dictionary<string, object> sketch = null;
        object rawSketch;
        if (inspected != null && inspected.TryGetValue("sketch", out rawSketch)) sketch = rawSketch as Dictionary<string, object>;
        object rawGeometry = null;
        if (sketch == null || !sketch.TryGetValue("geometry", out rawGeometry)) return null;
        List<object> geometries = rawGeometry as List<object>;
        if (geometries == null) return null;
        string match = null;
        foreach (object raw in geometries)
        {
            Dictionary<string, object> geometry = raw as Dictionary<string, object>;
            if (geometry == null) continue;
            string kind = ReadOptionalString(geometry, "kind");
            if (!String.Equals(kind, expectedKind, StringComparison.Ordinal)) continue;
            string id = ReadOptionalString(geometry, "journal_id");
            Require(!String.IsNullOrWhiteSpace(id), "Sketch geometry omitted its JournalIdentifier.");
            Require(match == null, "Expected exactly one " + expectedKind + " fixture geometry in its Sketch.");
            match = id;
        }
        return match;
    }

    private static Dictionary<string, object> PairInputRecord(string revisionId,
        string line1Id, string line2Id, Line line1, Line line2)
    {
        double[] d1 = Subtract(Point(line1.EndPoint), Point(line1.StartPoint));
        double[] d2 = Subtract(Point(line2.EndPoint), Point(line2.StartPoint));
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", revisionId }, { "mode", "two_lines" },
            { "line1_id", line1Id }, { "line2_id", line2Id },
            { "line1_type", line1.GetType().FullName }, { "line2_type", line2.GetType().FullName },
            { "line1_start_mm", Point(line1.StartPoint) }, { "line1_end_mm", Point(line1.EndPoint) },
            { "line2_start_mm", Point(line2.StartPoint) }, { "line2_end_mm", Point(line2.EndPoint) },
            { "line1_length_mm", Length(d1) }, { "line2_length_mm", Length(d2) },
            { "unit_direction_cross_norm", Length(Cross(Normalize(d1), Normalize(d2))) }
        };
    }

    private static M3DatumPlaneRequest TwoLineRequest(string revisionId, string line1Id, string line2Id)
    {
        return new M3DatumPlaneRequest { RevisionId = revisionId, Mode = "two_lines", Line1Id = line1Id, Line2Id = line2Id };
    }

    private static Dictionary<string, object> DatumRequestRecord(M3DatumPlaneRequest request)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", request == null ? null : request.RevisionId },
            { "mode", request == null ? null : request.Mode },
            { "line1_id", request == null ? null : request.Line1Id },
            { "line2_id", request == null ? null : request.Line2Id },
            { "origin_mm", request == null ? null : request.OriginMm },
            { "normal", request == null ? null : request.Normal },
            { "x_axis", request == null ? null : request.XAxis },
            { "support_datum_plane_id", request == null ? null : request.SupportDatumPlaneId },
            { "support_face_id", request == null ? null : request.SupportFaceId },
            { "distance_mm", request == null ? 0.0 : request.DistanceMm },
            { "axis_line_id", request == null ? null : request.AxisLineId },
            { "angle_deg", request == null ? 0.0 : request.AngleDeg }
        };
    }

    private static Dictionary<string, object> Tolerances()
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "position_mm", PositionToleranceMm }, { "unit_vector", DirectionTolerance },
            { "minimum_line_length_mm", MinimumLineLengthMm },
            { "parallel_unit_direction_cross_norm", ParallelDirectionCrossTolerance },
            { "parallel_separation_mm", ParallelSeparationToleranceMm },
            { "coplanarity_mm", CoplanarityToleranceMm },
            { "source", "NX2506 digital model-value comparisons; not physical metrology limits" }
        };
    }

    private static bool RunCase(string reportPath, Dictionary<string, object> evidence,
        string name, string objective, string[] procedure, string passCriteria, CaseBody body)
    {
        List<object> cases = (List<object>)evidence["cases"];
        List<object> defects = (List<object>)evidence["defects"];
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "objective", objective }, { "procedure", procedure },
            { "pass_criteria", passCriteria }, { "status", "RUNNING" }, { "started_utc", UtcNow() }
        };
        cases.Add(record);
        evidence["current_case"] = name;
        Persist(reportPath, evidence);
        try
        {
            body(record);
            record["status"] = "PASS";
            record["completed_utc"] = UtcNow();
            return true;
        }
        catch (Exception ex)
        {
            record["status"] = "FAIL";
            record["failure"] = ExceptionRecord(ex);
            record["completed_utc"] = UtcNow();
            evidence["passed"] = false;
            AddDefect(defects, name, procedure, ex.ToString(),
                "Correct the observed defect and rerun the complete 32-case F05 acceptance on new unique model and evidence paths; the original failed report remains unchanged.");
            return false;
        }
        finally
        {
            evidence.Remove("current_case");
            Persist(reportPath, evidence);
        }
    }

    private static void RecordSkippedAsFailure(string reportPath, Dictionary<string, object> evidence,
        string name, string reason)
    {
        List<object> cases = (List<object>)evidence["cases"];
        List<object> defects = (List<object>)evidence["defects"];
        string[] reproduction = new string[]
        {
            "Complete the named F05 prerequisite on a new isolated fixture.",
            "Run the complete F05 acceptance journal.",
            "Review the prerequisite report and execute the skipped case until it passes."
        };
        cases.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "objective", "Required downstream F05 coverage." },
            { "procedure", reproduction }, { "pass_criteria", "Prerequisite passes and the full named F05 case completes." },
            { "status", "SKIPPED" }, { "skip_reason", reason }, { "completed_utc", UtcNow() }
        });
        evidence["passed"] = false;
        AddDefect(defects, name, reproduction, reason,
            "Correct the prerequisite and rerun the complete 32-case F05 matrix on a new unique model path; this case must execute and pass.");
        Persist(reportPath, evidence);
    }

    private static int CountCases(List<object> cases, string status)
    {
        int count = 0;
        foreach (object raw in cases)
        {
            Dictionary<string, object> record = raw as Dictionary<string, object>;
            if (record != null && String.Equals(ReadOptionalString(record, "status"), status, StringComparison.Ordinal)) count++;
        }
        return count;
    }

    private static bool ChecksPass(Dictionary<string, object> checks)
    {
        if (checks == null || checks.Count == 0) return false;
        foreach (object raw in checks.Values)
        {
            Dictionary<string, object> check = raw as Dictionary<string, object>;
            if (check == null || !String.Equals(ReadOptionalString(check, "status"), "VERIFIED", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static Dictionary<string, object> PlanRecord(M3PlanSnapshot plan)
    {
        if (plan == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "kind", plan.Kind }, { "revision_id", plan.RevisionId }, { "feature_id", plan.FeatureId },
            { "sketch_id", plan.SketchId }, { "public_view", plan.PublicView }
        };
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

    private static Dictionary<string, object> SaveCopyRecord(M2SaveCopyResult result)
    {
        if (result == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "state", result.State }, { "part_path", result.PartPath }, { "part_tag", result.PartTag },
            { "revision_id", result.RevisionId }, { "reopened", result.Reopened }, { "checks", result.Checks },
            { "evidence", result.Evidence }, { "error_code", result.ErrorCode }, { "error_message", result.ErrorMessage }
        };
    }

    private static Dictionary<string, object> ExceptionRecord(Exception ex)
    {
        object code = null;
        if (ex != null)
        {
            PropertyInfo property = ex.GetType().GetProperty("Code", BindingFlags.Public | BindingFlags.Instance);
            if (property != null) try { code = property.GetValue(ex, null); } catch { }
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "type", ex == null ? null : ex.GetType().FullName }, { "code", code },
            { "message", ex == null ? null : ex.Message }, { "detail", ex == null ? null : ex.ToString() }
        };
    }

    private static string ReadFeatureId(Dictionary<string, object> result)
    {
        string id = ReadOptionalString(result, "datum_plane_id");
        if (String.IsNullOrWhiteSpace(id)) id = ReadOptionalString(result, "feature_id");
        Require(!String.IsNullOrWhiteSpace(id), "Datum-plane execution result omitted datum_plane_id/feature_id.");
        return id;
    }

    private static Part FindOwnedPart(Session session, string path)
    {
        if (String.IsNullOrWhiteSpace(path)) return null;
        string full = Path.GetFullPath(path);
        foreach (BasePart candidate in session.Parts)
        {
            Part part = candidate as Part;
            if (part != null && !String.IsNullOrWhiteSpace(part.FullPath) &&
                String.Equals(Path.GetFullPath(part.FullPath), full, StringComparison.OrdinalIgnoreCase)) return part;
        }
        return null;
    }

    private static void SaveInitialFixture(Part part)
    {
        PartSaveStatus status = part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
        try
        {
            Require(status != null && status.NumberUnsavedParts == 0 && status.NumberUnsavedObjects == 0,
                "Isolated F05 fixture did not save cleanly before adapter operations.");
        }
        finally { if (status != null) status.Dispose(); }
    }

    private static Dictionary<string, object> ReadNativeDatum(Part part, string featureId)
    {
        Feature feature = FindFeature(part, featureId);
        DatumPlane datum = FindDatumEntity(feature);
        Point3d p0, p1, p2, p3;
        datum.GetCornerPoints(out p0, out p1, out p2, out p3);
        Vector3d normal = datum.Normal;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "feature_id", feature.JournalIdentifier }, { "datum_entity_id", datum.JournalIdentifier },
            { "origin_mm", Point(datum.Origin) }, { "normal_world", new double[] { normal.X, normal.Y, normal.Z } },
            { "x_axis_world", Normalize(Subtract(Point(p1), Point(p0))) },
            { "y_axis_world", Normalize(Subtract(Point(p3), Point(p0))) },
            { "feature_parent_ids", FeatureIds(feature.GetParents()) },
            { "feature_ancestor_ids", FeatureAncestorIds(feature) }
        };
    }

    private static DatumPlane FindDatumEntity(Feature feature)
    {
        NXObject[] entities = feature.GetEntities();
        if (entities != null)
            foreach (NXObject item in entities)
            {
                DatumPlane datum = item as DatumPlane;
                if (datum != null) return datum;
            }
        DatumPlaneFeature datumFeature = feature as DatumPlaneFeature;
        if (datumFeature != null && datumFeature.DatumPlane != null) return datumFeature.DatumPlane;
        throw new InvalidOperationException("Feature does not resolve to a native DatumPlane: " + feature.JournalIdentifier);
    }

    private static Feature FindFeature(Part part, string featureId)
    {
        foreach (Feature feature in part.Features.GetFeatures())
            if (feature != null && String.Equals(feature.JournalIdentifier, featureId, StringComparison.Ordinal)) return feature;
        throw new InvalidOperationException("NX feature JournalIdentifier could not be resolved: " + featureId);
    }

    private static Sketch FindSketch(Part part, string sketchId)
    {
        foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null && String.Equals(sketch.JournalIdentifier, sketchId, StringComparison.Ordinal)) return sketch;
        throw new InvalidOperationException("NX Sketch JournalIdentifier could not be resolved: " + sketchId);
    }

    private static Line FindLineById(Part part, string lineId)
    {
        foreach (Curve curve in part.Curves.ToArray())
        {
            Line line = curve as Line;
            if (line != null && String.Equals(line.JournalIdentifier, lineId, StringComparison.Ordinal)) return line;
        }
        throw new InvalidOperationException("NXOpen.Line JournalIdentifier could not be resolved: " + lineId);
    }

    private static Curve FindCurveById(Part part, string curveId)
    {
        foreach (Curve curve in part.Curves.ToArray())
            if (curve != null && String.Equals(curve.JournalIdentifier, curveId, StringComparison.Ordinal)) return curve;
        throw new InvalidOperationException("Native Curve JournalIdentifier could not be resolved: " + curveId);
    }

    private static List<string> FeatureAncestorIds(Feature feature)
    {
        List<string> result = new List<string>();
        Queue<Feature> pending = new Queue<Feature>();
        Feature[] initial = feature.GetParents();
        if (initial != null) foreach (Feature parent in initial) if (parent != null) pending.Enqueue(parent);
        while (pending.Count != 0)
        {
            Feature current = pending.Dequeue();
            if (result.Contains(current.JournalIdentifier)) continue;
            result.Add(current.JournalIdentifier);
            Feature[] parents = current.GetParents();
            if (parents != null) foreach (Feature parent in parents) if (parent != null) pending.Enqueue(parent);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static List<string> FeatureIds(Feature[] features)
    {
        List<string> result = new List<string>();
        if (features != null)
            foreach (Feature feature in features) if (feature != null) result.Add(feature.JournalIdentifier);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static Dictionary<string, object> ModelDefinitionRecord(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part)
    {
        Dictionary<string, object> inspected = adapter.InspectPart(binding);
        Dictionary<string, object> stableInspection = new Dictionary<string, object>(StringComparer.Ordinal);
        string[] keys = new string[]
        {
            "schema", "identity", "part", "bodies", "features", "native_helical_definitions",
            "curve_ids", "sketches", "expressions", "dependencies", "support", "session_parts", "support_summary", "datum_planes"
        };
        foreach (string key in keys)
        {
            object value;
            if (inspected.TryGetValue(key, out value)) stableInspection[key] = value;
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "binding_revision", binding.RevisionId }, { "adapter_model_definition", stableInspection },
            { "direct_nx_ids", ModelIdRecord(part) }, { "native_line_endpoints", AllLineRecords(part) },
            { "part_is_modified", part.IsModified }
        };
    }

    private static Dictionary<string, object> ModelIdRecord(Part part)
    {
        List<string> features = new List<string>();
        foreach (Feature feature in part.Features.GetFeatures()) if (feature != null) features.Add(feature.JournalIdentifier);
        List<string> sketches = new List<string>();
        foreach (Sketch sketch in part.Sketches.ToArray()) if (sketch != null) sketches.Add(sketch.JournalIdentifier);
        List<string> bodies = new List<string>();
        foreach (Body body in part.Bodies.ToArray()) if (body != null) bodies.Add(body.JournalIdentifier);
        List<string> curves = new List<string>();
        foreach (Curve curve in part.Curves.ToArray()) if (curve != null) curves.Add(curve.JournalIdentifier);
        features.Sort(StringComparer.Ordinal); sketches.Sort(StringComparer.Ordinal);
        bodies.Sort(StringComparer.Ordinal); curves.Sort(StringComparer.Ordinal);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "features", features }, { "sketches", sketches }, { "bodies", bodies },
            { "curves", curves }, { "is_modified", part.IsModified }
        };
    }

    private static List<object> AllLineRecords(Part part)
    {
        List<Dictionary<string, object>> lines = new List<Dictionary<string, object>>();
        foreach (Curve curve in part.Curves.ToArray())
        {
            Line line = curve as Line;
            if (line != null) lines.Add(LineRecord(line));
        }
        lines.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            return StringComparer.Ordinal.Compare(ReadOptionalString(a, "journal_id"), ReadOptionalString(b, "journal_id"));
        });
        return new List<object>(lines.ToArray());
    }

    private static void RequireUnchangedPrepare(Dictionary<string, object> record,
        Dictionary<string, object> before, Dictionary<string, object> after)
    {
        bool unchanged = String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal);
        record["prepare_read_only_check"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "before", before }, { "after", after }, { "unchanged", unchanged }
        };
        Require(unchanged, "Prepare changed the full adapter model, native IDs/line endpoints, modified state, or revision.");
    }

    private static void RequireExistingObjectsPreserved(Dictionary<string, object> before,
        Dictionary<string, object> after, string newlyCreatedFeatureId)
    {
        RequirePriorNativeObjectsPreserved(before, after);
        List<string> addedFeatures = AddedFeatureIds(before, after);
        Require(addedFeatures.Count == 1 && addedFeatures.Contains(newlyCreatedFeatureId),
            "Operation did not add exactly its expected native feature while preserving the prior feature set.");
    }

    private static void RequireF01SketchObjectsPreserved(Dictionary<string, object> before,
        Dictionary<string, object> after, string sketchId, string sketchFeatureId, string supportDatumId)
    {
        RequirePriorNativeObjectsPreserved(before, after);
        List<string> addedFeatures = AddedFeatureIds(before, after);
        Require(addedFeatures.Count == 2 && addedFeatures.Contains(sketchFeatureId),
            "F01 Sketch operation did not add exactly its public Sketch feature and one native support feature.");
        string internalFeatureId = null;
        foreach (string candidateId in addedFeatures)
        {
            if (String.Equals(candidateId, sketchFeatureId, StringComparison.Ordinal)) continue;
            Dictionary<string, object> candidate = FindAdapterFeatureRecord(after, candidateId);
            string featureType = ReadOptionalString(candidate, "feature_type");
            List<string> parents = ReadStringList(candidate, "parent_ids");
            List<string> children = ReadStringList(candidate, "child_ids");
            if (String.Equals(featureType, "DATUM_CSYS", StringComparison.Ordinal) &&
                parents.Contains(supportDatumId) && children.Contains(sketchFeatureId))
                internalFeatureId = candidateId;
        }
        Require(!String.IsNullOrWhiteSpace(internalFeatureId),
            "F01 Sketch operation added an extra feature that is not its single native DATUM_CSYS support child.");
        Dictionary<string, object> publicSketchFeature = FindAdapterFeatureRecord(after, sketchFeatureId);
        RequireDictionaryContains(publicSketchFeature, "feature_type", "SKETCH");
        List<string> publicParents = ReadStringList(publicSketchFeature, "parent_ids");
        Require(publicParents.Contains(internalFeatureId),
            "Public Sketch feature does not identify its expected native internal DATUM_CSYS parent.");

        Dictionary<string, object> beforeIds = (Dictionary<string, object>)before["direct_nx_ids"];
        Dictionary<string, object> afterIds = (Dictionary<string, object>)after["direct_nx_ids"];
        List<string> oldSketchIds = ObjectStringList(beforeIds["sketches"]);
        List<string> newSketchIds = ObjectStringList(afterIds["sketches"]);
        List<string> addedSketches = new List<string>();
        foreach (string id in newSketchIds) if (!oldSketchIds.Contains(id)) addedSketches.Add(id);
        Require(addedSketches.Count == 1 && addedSketches.Contains(sketchId),
            "F01 Sketch operation did not add exactly the requested native Sketch object.");
    }

    private static void RequirePriorNativeObjectsPreserved(Dictionary<string, object> before,
        Dictionary<string, object> after)
    {
        Dictionary<string, object> beforeIds = (Dictionary<string, object>)before["direct_nx_ids"];
        Dictionary<string, object> afterIds = (Dictionary<string, object>)after["direct_nx_ids"];
        string[] keys = new string[] { "features", "sketches", "bodies", "curves" };
        foreach (string key in keys)
        {
            List<string> previous = ObjectStringList(beforeIds[key]);
            List<string> current = ObjectStringList(afterIds[key]);
            foreach (string id in previous)
                Require(current.Contains(id), "New F05 feature removed an existing " + key + " object: " + id);
        }
    }

    private static List<string> AddedFeatureIds(Dictionary<string, object> before, Dictionary<string, object> after)
    {
        Dictionary<string, object> beforeIds = (Dictionary<string, object>)before["direct_nx_ids"];
        Dictionary<string, object> afterIds = (Dictionary<string, object>)after["direct_nx_ids"];
        List<string> previousFeatures = ObjectStringList(beforeIds["features"]);
        List<string> currentFeatures = ObjectStringList(afterIds["features"]);
        List<string> addedFeatures = new List<string>();
        foreach (string id in currentFeatures) if (!previousFeatures.Contains(id)) addedFeatures.Add(id);
        return addedFeatures;
    }

    private static Dictionary<string, object> FindAdapterFeatureRecord(Dictionary<string, object> snapshot,
        string featureId)
    {
        Dictionary<string, object> inspection = (Dictionary<string, object>)snapshot["adapter_model_definition"];
        List<object> features = ReadList(inspection, "features");
        foreach (object raw in features)
        {
            Dictionary<string, object> feature = raw as Dictionary<string, object>;
            if (feature != null && String.Equals(ReadOptionalString(feature, "journal_id"), featureId, StringComparison.Ordinal))
                return feature;
        }
        throw new InvalidOperationException("Adapter feature inventory omitted expected new feature: " + featureId);
    }

    private static Dictionary<string, object> LineRecord(Line line)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "journal_id", line.JournalIdentifier }, { "object_type", line.GetType().FullName },
            { "start_point_mm", Point(line.StartPoint) }, { "end_point_mm", Point(line.EndPoint) },
            { "length_mm", Length(Subtract(Point(line.EndPoint), Point(line.StartPoint))) },
            { "direction_unit_start_to_end", Normalize(Subtract(Point(line.EndPoint), Point(line.StartPoint))) }
        };
    }

    private static void AddDefect(List<object> defects, string name, string[] reproduction,
        string observed, string retest)
    {
        defects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test_case", name }, { "reproduction_steps", reproduction },
            { "observed_exception_or_result", observed }, { "retest_criterion", retest }
        });
    }

    private static void Persist(string reportPath, Dictionary<string, object> evidence)
    {
        File.WriteAllText(reportPath, M2Json.CanonicalText(evidence), new UTF8Encoding(false));
    }

    private static Dictionary<string, object> AssemblyRecord(Assembly assembly)
    {
        string path = assembly == null ? null : assembly.Location;
        string version = null;
        if (!String.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            version = info == null ? null : info.FileVersion;
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "path", path }, { "assembly_version", assembly == null ? null : assembly.GetName().Version.ToString() },
            { "file_version", version }
        };
    }

    private static void RequireNewPath(string path, string description)
    {
        Require(!File.Exists(path) && !Directory.Exists(path), "Unique " + description + " path already exists; refusing to overwrite it: " + path);
    }

    private static void RequireDictionaryContains(Dictionary<string, object> record, string key, object expected)
    {
        object actual = null;
        Require(record != null && record.TryGetValue(key, out actual), "Required record field is missing: " + key);
        Require(String.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture),
            Convert.ToString(expected, CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "Record field differs from expected value for " + key + ".");
    }

    private static void RequireDictionaryContains(Dictionary<string, object> record, string key,
        double expected, double tolerance)
    {
        object actual = null;
        Require(record != null && record.TryGetValue(key, out actual), "Required numeric record field is missing: " + key);
        double actualNumber = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
        Require(Math.Abs(actualNumber - expected) <= tolerance, "Record field differs from expected numeric value for " + key + ".");
    }

    private static List<object> ReadList(Dictionary<string, object> record, string key)
    {
        object value = null;
        Require(record != null && record.TryGetValue(key, out value), "Expected list field is missing: " + key);
        List<object> objects = value as List<object>;
        if (objects != null) return objects;
        IList<string> strings = value as IList<string>;
        if (strings != null)
        {
            objects = new List<object>();
            foreach (string item in strings) objects.Add(item);
            return objects;
        }
        string[] stringArray = value as string[];
        if (stringArray != null)
        {
            objects = new List<object>();
            foreach (string item in stringArray) objects.Add(item);
            return objects;
        }
        object[] objectArray = value as object[];
        if (objectArray != null) return new List<object>(objectArray);
        throw new InvalidOperationException("Expected list field has wrong type: " + key);
    }

    private static List<string> ReadStringList(Dictionary<string, object> record, string key)
    {
        return ObjectStringList(ReadList(record, key));
    }

    private static string RequiredString(Dictionary<string, object> record, string key)
    {
        string value = ReadOptionalString(record, key);
        Require(!String.IsNullOrWhiteSpace(value), "Required string field is missing or empty: " + key);
        return value;
    }

    private static string ReadRequiredString(Dictionary<string, object> record, string key) { return RequiredString(record, key); }

    private static string ReadOptionalString(Dictionary<string, object> record, string key)
    {
        object value = null;
        return record != null && record.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    private static double ReadRequiredDouble(Dictionary<string, object> record, string key)
    {
        object value = null;
        Require(record != null && record.TryGetValue(key, out value), "Required numeric field is missing: " + key);
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static double[] ReadVector(Dictionary<string, object> record, string key, int count)
    {
        object value = null;
        Require(record != null && record.TryGetValue(key, out value), "Expected vector field is missing: " + key);
        double[] result = value as double[];
        if (result == null)
        {
            List<object> list = value as List<object>;
            if (list != null)
            {
                result = new double[list.Count];
                for (int i = 0; i < list.Count; i++) result[i] = Convert.ToDouble(list[i], CultureInfo.InvariantCulture);
            }
        }
        Require(result != null && result.Length == count, "Expected vector has the wrong length: " + key);
        return result;
    }

    private static void RequireAnyDictionaryPropertyValue(Dictionary<string, object> record,
        string[] keys, string expected)
    {
        Require(ContainsAnyDictionaryPropertyValue(record, keys, expected),
            "Inspection omitted the expected association value " + expected + " under keys " + String.Join(",", keys));
    }

    private static bool ContainsAnyDictionaryPropertyValue(object value, string[] keys, string expected)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            foreach (string key in keys)
            {
                object property;
                if (dictionary.TryGetValue(key, out property))
                {
                    if (String.Equals(Convert.ToString(property, CultureInfo.InvariantCulture), expected, StringComparison.Ordinal)) return true;
                    if (ContainsAnyDictionaryPropertyValue(property, keys, expected)) return true;
                }
            }
            foreach (object child in dictionary.Values)
                if (ContainsAnyDictionaryPropertyValue(child, keys, expected)) return true;
        }
        List<object> list = value as List<object>;
        if (list != null)
            foreach (object child in list) if (ContainsAnyDictionaryPropertyValue(child, keys, expected)) return true;
        object[] array = value as object[];
        if (array != null)
            foreach (object child in array) if (ContainsAnyDictionaryPropertyValue(child, keys, expected)) return true;
        return false;
    }

    private static string UtcNow() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
    private static double[] Point(Point3d point) { return new double[] { point.X, point.Y, point.Z }; }
    private static Point3d ToPoint(double[] point) { return new Point3d(point[0], point[1], point[2]); }
    private static double[] Copy(double[] value) { return value == null ? null : (double[])value.Clone(); }
    private static double[] Add(double[] a, double[] b) { return new double[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
    private static double[] Subtract(double[] a, double[] b) { return new double[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    private static double[] Scale(double[] a, double s) { return new double[] { a[0] * s, a[1] * s, a[2] * s }; }
    private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    private static double[] Cross(double[] a, double[] b)
    {
        return new double[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    }
    private static double Length(double[] a) { return Math.Sqrt(Dot(a, a)); }
    private static double VectorDistance(double[] a, double[] b) { return Length(Subtract(a, b)); }
    private static double[] Normalize(double[] a)
    {
        double length = Length(a);
        Require(length > 0.0 && !Double.IsNaN(length) && !Double.IsInfinity(length), "Cannot normalize a zero or invalid vector.");
        return Scale(a, 1.0 / length);
    }
    private static void RequireVectorClose(double[] actual, double[] expected, double tolerance, string message)
    {
        double error = VectorDistance(actual, expected);
        Require(error <= tolerance, message + " Error=" + error.ToString("R", CultureInfo.InvariantCulture));
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static int GetUnloadOption(string dummy) { return (int)Session.LibraryUnloadOption.Explicitly; }
}
