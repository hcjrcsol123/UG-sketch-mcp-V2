// F10 native solid-Edge projection acceptance extension.
// This is a callable test module; it never launches, closes, or controls NX.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

public static class F10EdgeAcceptance
{
    private const double PositionToleranceMm = 1.0e-6;
    private const double DirectionTolerance = 1.0e-7;
    private const double FixtureMatchToleranceMm = 1.0e-5;

    private sealed class Frame
    {
        internal double[] Origin;
        internal double[] Normal;
        internal double[] XAxis;
        internal double[] YAxis;
    }

    private sealed class CircularData
    {
        internal double[] Center;
        internal double[] AxisX;
        internal double[] AxisY;
        internal double[] Normal;
        internal double Radius;
        internal double StartAngle;
        internal double EndAngle;
        internal bool Periodic;
        internal double Length;
    }

    private sealed class ProjectionFixture
    {
        internal string CaseId;
        internal string SourceKind;
        internal bool Associative;
        internal int DirectionSign;
        internal string SourceTag;
        internal string SourceJournalId;
        internal string SketchId;
        internal string SketchTag;
        internal string FeatureId;
        internal string FeatureTag;
        internal string OutputTag;
        internal string OutputType;
        internal Dictionary<string, object> SourceRecord;
        internal Dictionary<string, object> Oracle;
        internal Dictionary<string, object> Relation;
    }

    /// <summary>
    /// Runs native solid-Edge projection cases in a unique saved millimetre part.
    /// artifactDirectory is the workspace/v2 directory; models are retained in
    /// its models subdirectory and JSON evidence is retained in evidence.
    /// Call with a clean saved prior Work Part so it can be restored unchanged.
    /// </summary>
    public static Dictionary<string, object> Run(Session session,
        M2OpenModelAdapter adapter, string artifactDirectory)
    {
        string runId = Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(artifactDirectory);
        string modelDirectory = Path.Combine(root, "models");
        string evidenceDirectory = Path.Combine(root, "evidence");
        string sourcePath = Path.Combine(modelDirectory, "F10-edge-" + runId + ".prt");
        string copyPath = Path.Combine(modelDirectory, "F10-edge-" + runId + "-copy.prt");
        string reportPath = Path.Combine(evidenceDirectory, "F10-edge-" + runId + ".json");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        RequireNewPath(sourcePath, "isolated source model");
        RequireNewPath(copyPath, "Save Copy model");
        RequireNewPath(reportPath, "evidence report");

        List<object> cases = new List<object>();
        List<object> defects = new List<object>();
        Dictionary<string, string> targetSketchTags = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, Frame> targetFrames = new Dictionary<string, Frame>(StringComparer.Ordinal);
        List<object> targetSketchSetup = new List<object>();
        Dictionary<string, object> report = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test", "F10 native solid linear and circular Edge projection acceptance" },
            { "run_id", runId }, { "started_utc", UtcNow() }, { "passed", false },
            { "acceptance_status", "NOT_RUN" }, { "sample_size", 1 }, { "units", "millimeter" },
            { "test_layer", "NX subsystem validation through production M2OpenModelAdapter with independent NXOpen and UF native Edge readback" },
            { "source_model_path", sourcePath }, { "save_copy_path", copyPath },
            { "report_path", reportPath }, { "cases", cases }, { "defects", defects },
            { "position_tolerance_mm", PositionToleranceMm }, { "direction_tolerance", DirectionTolerance },
            { "nx_projection_tolerances", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "production_adapter_position_tolerance_mm", PositionToleranceMm },
                    { "production_adapter_angle_tolerance", DirectionTolerance },
                    { "native_builder_tolerance_mm", 0.001 },
                    { "section_distance_tolerance_mm", 0.001 },
                    { "section_chaining_tolerance_mm", 0.001 },
                    { "section_angle_tolerance_api_value", 0.5 }
                } },
            { "instrumentation_and_calibration", "No physical measurement instrument is used. Values are NX model-space geometry readback; CAD comparison tolerances are not metrology uncertainty. No calibration applies." },
            { "coverage", "Linear block Edge and circular cylinder Edge; associative=true and false; each with positive and negative target Sketch normal; direct Edge-record projection oracle; target Sketch geometry Tag delta; associative CPROJ Section element-data source Tag/JID; one representative Save Copy/reopen relation and geometry check." },
            { "limitations", new string[] {
                "One NX session, one fixture part, and one execution of each of the eight cases. This is single-run subsystem evidence, not reliability, production accuracy, or standards-compliance evidence.",
                "No environmental, endurance, thermal, manufacturing, or physical metrology claim is made.",
                "The native builder tolerance values are NX API settings; they are not calibrated measurement uncertainty." } },
            { "nx_started_by_module", false }, { "nx_closed_by_module", false },
            { "traceability", "F10 production M2OpenModelAdapter PrepareSketchProjection/ExecutePlan/VerifyPlan; native Edge support and V2SketchProjectionFactory; F10 acceptance requirements and docs/F10.md." }
        };

        Session actualSession = session;
        Part originalPart = null;
        Part part = null;
        M2ModelBinding binding = null;
        bool matrixComplete = false;
        ProjectionFixture saveCopyFixture = null;
        try
        {
            Require(actualSession != null, "NX Session was not supplied by the caller.");
            Require(adapter != null, "Production M2OpenModelAdapter was not supplied by the caller.");
            Require(!String.IsNullOrWhiteSpace(artifactDirectory), "Evidence artifact directory is empty.");
            originalPart = actualSession.Parts.Work;
            report["original_work_part_path"] = originalPart == null ? null : originalPart.FullPath;
            report["original_work_part_modified_before"] = originalPart != null && originalPart.IsModified;
            Require(!actualSession.IsInTaskEnvironment && actualSession.ActiveSketch == null,
                "Edge acceptance requires an idle NX session with no active Sketch or task environment.");
            Require(originalPart == null || !originalPart.IsModified,
                "The prior Work Part must be saved and unmodified so the isolated Edge fixture can be run and the prior state restored.");

            Frame fixtureFrame = MakeFrame(new double[] { 10.0, -5.0, 4.0 },
                new double[] { 1.0, 2.0, 3.0 }, new double[] { 2.0, -1.0, 0.0 });
            report["fixture_frame_requested"] = FrameRecord(fixtureFrame);
            part = actualSession.Parts.NewDisplay(sourcePath, Part.Units.Millimeters);
            Require(part != null && part.PartUnits == BasePart.Units.Millimeters,
                "NX did not create the unique isolated millimetre part.");
            report["fixture_part_tag"] = part.Tag.ToString();
            report["fixture_part_units"] = part.PartUnits.ToString();
            Dictionary<string, object> sources = CreateSolidSources(part, fixtureFrame);
            report["solid_source_fixtures"] = sources;
            SavePart(part, "isolated solid-Edge source Bodies before target Sketch setup");
            report["source_bodies_saved_before_target_sketch_setup"] = true;

            // Set up the empty target Sketches through direct NXOpen before takeover.
            // F10 Edge validation does not depend on re-running F01 Prepare/Verify.
            string[] kinds = new string[] { "linear_edge", "circular_edge" };
            bool[] associativity = new bool[] { true, false };
            int[] signs = new int[] { 1, -1 };
            int targetIndex = 0;
            foreach (string kind in kinds)
            {
                foreach (bool associative in associativity)
                {
                    foreach (int sign in signs)
                    {
                        Dictionary<string, object> caseDefinition = NewCase(kind, associative, sign);
                        string caseId = GetString(caseDefinition, "id");
                        Frame targetFrame = TranslateFrame(fixtureFrame,
                            Scale(fixtureFrame.YAxis, targetIndex * 32.0));
                        Dictionary<string, object> targetSetup = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "case_id", caseId }, { "requested_frame", FrameRecord(targetFrame) },
                            { "creation_method", "NXOpen Plane/Direction/Point + SketchInPlaceBuilder2 before adapter Takeover" },
                            { "direct_native_creation", true }, { "passed", false }
                        };
                        targetSketchSetup.Add(targetSetup);
                        try
                        {
                            Sketch target = CreateTargetSketch(actualSession, part, targetFrame, caseId);
                            targetSketchTags.Add(caseId, target.Tag.ToString());
                            targetFrames.Add(caseId, ReadSketchFrame(target));
                            targetSetup["sketch_tag"] = target.Tag.ToString();
                            targetSetup["sketch_journal_id_at_setup"] = target.JournalIdentifier;
                            targetSetup["frame"] = FrameRecord(ReadSketchFrame(target));
                            targetSetup["empty_geometry_count"] = target.GetAllGeometry().Length;
                            Require(target.GetAllGeometry() != null && target.GetAllGeometry().Length == 0,
                                "Direct NXOpen target Sketch setup must be empty before F10 projection.");
                            targetSetup["passed"] = true;
                        }
                        catch (Exception setupException)
                        {
                            targetSetup["failure"] = ExceptionRecord(setupException);
                            targetSetup["part_path"] = part.FullPath;
                            try
                            {
                                SavePart(part, "partial solid-Edge fixture after direct target Sketch setup failure");
                                targetSetup["partial_fixture_saved"] = true;
                            }
                            catch (Exception saveException)
                            {
                                targetSetup["partial_fixture_saved"] = false;
                                targetSetup["partial_fixture_save_failure"] = ExceptionRecord(saveException);
                            }
                            Persist(reportPath, report);
                            throw;
                        }
                        targetIndex++;
                    }
                }
            }
            report["target_sketches_setup"] = targetSketchSetup;
            Require(actualSession.ActiveSketch == null,
                "Direct NXOpen target Sketch setup left an active Sketch before takeover.");
            SavePart(part, "initial isolated solid-Edge source fixture");
            report["source_model_saved"] = true;
            report["source_model_bytes"] = new FileInfo(sourcePath).Length;

            M2OpenModelAdapter activeAdapter = adapter;
            binding = activeAdapter.Takeover(sourcePath, "saved_independent_copy",
                "f10-edge-session-" + runId, "f10-edge-load-" + runId, 1);
            Dictionary<string, object> sessionInfo = activeAdapter.GetSession(binding);
            report["session"] = sessionInfo;
            report["binding_revision_initial"] = binding.RevisionId;
            report["assemblies"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "bridge", AssemblyRecord(typeof(M2Json).Assembly) },
                { "adapter", AssemblyRecord(typeof(M2OpenModelAdapter).Assembly) },
                { "nxopen", AssemblyRecord(typeof(Session).Assembly) },
                { "acceptance", AssemblyRecord(typeof(F10EdgeAcceptance).Assembly) }
            };
            string nxVersion = FirstNonEmpty(GetString(sessionInfo, "nx_build"),
                GetString(sessionInfo, "full_release_number"), GetString(sessionInfo, "release_number"));
            report["nx_configuration"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "required_release", "NX2506 (the containing F10 acceptance configuration)" },
                { "recorded_release", nxVersion },
                { "release_number", GetString(sessionInfo, "release_number") },
                { "full_release_number", GetString(sessionInfo, "full_release_number") },
                { "part_path", part.FullPath }, { "part_units", part.PartUnits.ToString() },
                { "nxopen_assembly_version", GetString(sessionInfo, "nxopen_assembly_version") }
            };
            Require(IsNx2506(nxVersion) || IsNx2506(GetString(sessionInfo, "release_number")) ||
                IsNx2506(GetString(sessionInfo, "full_release_number")),
                "The active NX release could not be identified as NX2506.");

            IM3SketchProjectionOperationAdapter projectionAdapter = (object)activeAdapter as IM3SketchProjectionOperationAdapter;
            Require(projectionAdapter != null,
                "The supplied production adapter does not expose IM3SketchProjectionOperationAdapter.");
            Edge straight = FindEdgeByTag(part, GetString((Dictionary<string, object>)sources["linear_edge"], "tag"));
            Edge circular = FindEdgeByTag(part, GetString((Dictionary<string, object>)sources["circular_edge"], "tag"));
            Require(straight != null && circular != null,
                "A native source Edge Tag could not be re-resolved from the solid Body inventory.");

            foreach (string kind in kinds)
            {
                foreach (bool associative in associativity)
                {
                    foreach (int sign in signs)
                    {
                        Dictionary<string, object> row = NewCase(kind, associative, sign);
                        cases.Add(row);
                        try
                        {
                            string caseId = GetString(row, "id");
                            string targetTag = targetSketchTags[caseId];
                            Sketch target = FindSketchByTag(part, targetTag);
                            Require(target != null,
                                "Pre-created empty target Sketch Tag could not be re-resolved after prior operations.");
                            Edge source = kind == "linear_edge" ? straight : circular;
                            ProjectionFixture fixture = RunProjectionCase(activeAdapter, projectionAdapter,
                                binding, part, target, source, kind, associative, sign,
                                targetFrames[caseId], row);
                            if (kind == "linear_edge" && associative && sign == 1)
                                saveCopyFixture = fixture;
                            row["passed"] = true;
                            Persist(reportPath, report);
                        }
                        catch (Exception ex)
                        {
                            row["passed"] = false;
                            row["failure"] = ExceptionRecord(ex);
                            AddDefect(report, row, "Projection case failed. Preserve the generated model and report; reproduce the exact case before changing any NX state.");
                            Persist(reportPath, report);
                            throw;
                        }
                    }
                }
            }
            matrixComplete = true;
            report["matrix_complete"] = true;

            Require(saveCopyFixture != null,
                "The representative associative linear Edge case was not produced; Save Copy round trip cannot be checked.");
            report["save_copy_round_trip"] = RunSaveCopyRoundTrip(activeAdapter, binding, actualSession,
                copyPath, saveCopyFixture, PositionToleranceMm);
            Dictionary<string, object> roundTrip = report["save_copy_round_trip"] as Dictionary<string, object>;
            if (roundTrip != null && !Object.Equals(roundTrip["passed"], true))
                AddDefect(report, roundTrip,
                    "Using the recorded representative linear Edge case, save and reopen to the recorded unique copy path; the native CPROJ Section source must resolve to one Edge by exact Tag/JID and the reopened Sketch Line endpoint residual must be at most 1e-6 mm.");
            Require(roundTrip != null && Object.Equals(roundTrip["passed"], true),
                "Save Copy/reopen relation and direct geometry checks did not pass.");
            report["passed"] = true;
            report["acceptance_status"] = "PASS_SINGLE_RUN_EVIDENCE_ONLY";
        }
        catch (Exception ex)
        {
            report["passed"] = false;
            report["acceptance_status"] = "FAILED_OR_INCOMPLETE";
            report["run_failure"] = ExceptionRecord(ex);
            if (defects.Count == 0)
            {
                Dictionary<string, object> setupFailure = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "id", "F10-edge-setup-or-roundtrip" },
                    { "failure", ExceptionRecord(ex) },
                    { "reproduction_steps", new string[] {
                        "Open a designated idle NX2506 session with no active Sketch or task environment.",
                        "Ensure the prior Work Part is saved and unmodified.",
                        "Invoke F10EdgeAcceptance.Run(session, productionAdapter, workspaceV2Root) and preserve the unique part/report paths recorded in this report." } },
                    { "retest_criterion", "Correct the setup or native operation issue, retain the failing report, and rerun the same edge case matrix in a fresh idle NX2506 session using new unique model and report paths. All eight case checks, Section identity checks, and Save Copy checks must pass." }
                };
                defects.Add(setupFailure);
            }
        }
        finally
        {
            report["matrix_complete"] = matrixComplete;
            report["original_work_part_modified_before_restore"] = originalPart == null ? null : (object)SafeModified(originalPart);
            report["completed_utc"] = UtcNow();
            report["sample_size"] = 1;
            try
            {
                if (binding != null) adapter.Release(binding);
            }
            catch (Exception ex)
            {
                report["binding_release_error"] = ExceptionRecord(ex);
                report["passed"] = false;
                report["acceptance_status"] = "FAILED_OR_INCOMPLETE";
            }
            try
            {
                if (actualSession != null && originalPart != null && actualSession.Parts.Work != null &&
                    actualSession.Parts.Work.Tag != originalPart.Tag)
                    actualSession.Parts.SetWork(originalPart);
                report["work_part_after"] = actualSession == null || actualSession.Parts.Work == null ? null : actualSession.Parts.Work.FullPath;
                report["active_sketch_after"] = actualSession == null || actualSession.ActiveSketch == null ? null : actualSession.ActiveSketch.Tag.ToString();
                report["original_work_part_restored"] = originalPart == null ||
                    (actualSession != null && actualSession.Parts.Work != null && actualSession.Parts.Work.Tag == originalPart.Tag);
                report["original_work_part_modified_after_restore"] = originalPart == null ? null : (object)SafeModified(originalPart);
                report["original_work_part_preserved"] = Object.Equals(report["original_work_part_restored"], true) &&
                    (originalPart == null || !SafeModified(originalPart));
                if (!Object.Equals(report["original_work_part_preserved"], true))
                {
                    report["passed"] = false;
                    report["acceptance_status"] = "FAILED_OR_INCOMPLETE";
                    defects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "id", "F10-edge-original-work-part-restore" },
                        { "failure", "The prior Work Part was not restored unchanged after the isolated Edge run." },
                        { "original_work_part_path", originalPart == null ? null : originalPart.FullPath },
                        { "work_part_after", actualSession == null || actualSession.Parts.Work == null ? null : actualSession.Parts.Work.FullPath },
                        { "original_work_part_modified_after_restore", originalPart == null ? null : (object)SafeModified(originalPart) },
                        { "reproduction_steps", new string[] {
                            "Start from the reported saved, unmodified prior Work Part.",
                            "Invoke the Edge Journal and let it create its unique model, execute projection cases, and perform Save Copy/reopen.",
                            "Inspect current Work Part path and prior Work Part dirty state after the Journal returns." } },
                        { "retest_criterion", "On a fresh idle NX session, rerun the Edge Journal; its final Work Part must be the original path and that original part must remain unmodified." }
                    });
                }
            }
            catch (Exception ex)
            {
                report["restore_work_part_error"] = ExceptionRecord(ex);
                report["passed"] = false;
                report["acceptance_status"] = "FAILED_OR_INCOMPLETE";
            }
            if (!Object.Equals(report["passed"], true) && defects.Count == 0)
                report["acceptance_status"] = "FAILED_OR_INCOMPLETE";
            try { Persist(reportPath, report); } catch { }
        }
        return report;
    }

    private static ProjectionFixture RunProjectionCase(M2OpenModelAdapter adapter,
        IM3SketchProjectionOperationAdapter projectionAdapter, M2ModelBinding binding,
        Part part, Sketch sketch, Edge initialSource, string sourceKind, bool associative, int directionSign,
        Frame requestedFrame, Dictionary<string, object> row)
    {
        string caseId = GetString(row, "id");
        Require(sketch != null && sketch.OwningPart != null &&
            String.Equals(sketch.OwningPart.Tag.ToString(), part.Tag.ToString(), StringComparison.Ordinal),
            "Pre-created target Sketch must belong to the isolated Edge fixture Part.");
        Require(sketch.GetAllGeometry() != null && sketch.GetAllGeometry().Length == 0,
            "Each direct NXOpen target Sketch must be empty when its Edge projection begins.");
        Frame frame = ReadSketchFrame(sketch);
        double[] direction = Scale(frame.Normal, directionSign);
        double alignmentResidual = Norm(Cross(Normalize(direction), frame.Normal));
        Require(alignmentResidual <= DirectionTolerance,
            "Native target Sketch normal and requested positive/negative direction are not parallel within tolerance.");
        string sourceTag = initialSource.Tag.ToString();
        Edge source = FindEdgeByTag(part, sourceTag);
        Require(source != null, "The source Edge Tag could not be re-resolved immediately before PrepareSketchProjection.");
        string sourceJournalId = source.JournalIdentifier;
        Dictionary<string, object> sourceBefore = NativeEdgeRecord(source, sourceKind);
        Dictionary<string, object> oracle = BuildIndependentOracle(source, sourceKind, frame, direction);
        HashSet<string> geometryTagsBefore = SketchGeometryTags(sketch);
        HashSet<string> featureTagsBefore = FeatureTags(part);
        M3SketchProjectionRequest request = new M3SketchProjectionRequest
        {
            RevisionId = binding.RevisionId,
            SketchId = sketch.JournalIdentifier,
            SourceCurveIds = new List<string> { sourceJournalId },
            DirectionXYZ = (double[])direction.Clone(),
            Associative = associative
        };
        row["request"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", request.RevisionId }, { "sketch_id", request.SketchId },
            { "source_curve_ids", request.SourceCurveIds }, { "direction_xyz", direction },
            { "associative", associative }
        };
        row["target_sketch_before"] = SketchRecord(sketch);
        row["source_native_before"] = sourceBefore;
        row["independent_oracle"] = oracle;
        row["direction_alignment_residual"] = alignmentResidual;
        row["direction_tolerance"] = DirectionTolerance;
        row["target_sketch_geometry_before"] = SketchGeometryRecords(sketch);

        M3PlanSnapshot plan = projectionAdapter.PrepareSketchProjection(binding, request);
        Require(plan != null && plan.PublicView != null,
            "PrepareSketchProjection returned no immutable public plan.");
        row["prepared_plan"] = plan.PublicView;
        row["prepared_plan_revision_id"] = plan.RevisionId;
        Require(String.Equals(plan.RevisionId, request.RevisionId, StringComparison.Ordinal),
            "Prepared projection plan revision does not match the request revision.");
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        row["execute_result"] = ExecutionRecord(execution);
        if (plan.AdapterState != null)
        {
            FieldInfo appliedEvidence = plan.AdapterState.GetType().GetField("AppliedEvidence",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (appliedEvidence != null)
                row["production_postcondition_evidence"] = appliedEvidence.GetValue(plan.AdapterState);
            foreach (string fieldName in new string[] { "PartHelperTagsBefore", "PartHelperTagsAfter" })
            {
                FieldInfo field = plan.AdapterState.GetType().GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Dictionary<string, List<string>> inventory = field == null ? null
                    : field.GetValue(plan.AdapterState) as Dictionary<string, List<string>>;
                Dictionary<string, object> publicInventory = new Dictionary<string, object>(StringComparer.Ordinal);
                if (inventory != null)
                    foreach (KeyValuePair<string, List<string>> entry in inventory)
                        publicInventory[entry.Key] = entry.Value;
                row[fieldName] = publicInventory;
            }
        }
        Require(execution != null && String.Equals(execution.State, "APPLIED", StringComparison.Ordinal),
            "ExecutePlan did not return APPLIED for the solid Edge projection.");
        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        row["verify_result"] = VerificationRecord(verification);
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "Production VerifyPlan did not verify the solid Edge projection.");
        row["active_sketch_after_verify"] = Session.GetSession().ActiveSketch == null
            ? null : Session.GetSession().ActiveSketch.Tag.ToString();
        Require(Session.GetSession().ActiveSketch == null,
            "Production VerifyPlan left a Sketch active after solid Edge projection.");

        List<NXObject> newGeometry = NewSketchGeometry(sketch, geometryTagsBefore);
        row["target_sketch_geometry_after"] = SketchGeometryRecords(sketch);
        row["target_sketch_geometry_tag_delta"] = ObjectRecords(newGeometry);
        Require(newGeometry.Count == 1,
            "Direct Sketch.GetAllGeometry Tag delta did not contain exactly one new projection output.");
        Curve output = newGeometry[0] as Curve;
        Require(output != null && sketch.IsSketchGeometry(output),
            "The new direct Sketch Tag is not a native curve owned by the target Sketch.");
        Dictionary<string, object> outputRecord = CurveRecord(output);
        row["direct_output_curve"] = outputRecord;
        Dictionary<string, object> numericCheck = CompareOutputToOracle(output, sourceKind, oracle, frame);
        row["direct_numeric_oracle_check"] = numericCheck;
        Require(Object.Equals(numericCheck["matches"], true),
            "Direct target Sketch geometry differs from the independent native Edge projection oracle.");

        Edge sourceAfterEdge = FindEdgeByTag(part, sourceTag);
        Require(sourceAfterEdge != null,
            "The source Edge Tag no longer resolves after projection execution.");
        Dictionary<string, object> sourceAfter = NativeEdgeRecord(sourceAfterEdge, sourceKind);
        Dictionary<string, object> sourcePreservation = CompareSourceGeometry(sourceBefore, sourceAfter, sourceKind);
        row["source_native_after"] = sourceAfter;
        row["source_geometry_preservation"] = sourcePreservation;
        Require(Object.Equals(sourcePreservation["matches"], true),
            "Direct source Edge geometry changed during projection.");

        List<Feature> newFeatures = NewFeatures(part, featureTagsBefore);
        row["new_features"] = FeatureRecords(newFeatures);
        Dictionary<string, object> relation = null;
        Feature projectionFeature = null;
        if (associative)
        {
            Require(newFeatures.Count == 1,
                "Associative native solid Edge projection did not create exactly one new feature.");
            projectionFeature = newFeatures[0];
            relation = ReadProjectionRelation(part, projectionFeature);
            row["native_section_relation"] = relation;
            Require(RelationMatchesSource(relation, sourceAfterEdge),
                "CPROJ Section element-data source Tag/JID do not resolve to the selected native Edge.");
            row["section_relation_source_match"] = true;
        }
        else
        {
            Require(newFeatures.Count == 0,
                "Associative=false unexpectedly created a projection feature instead of a fixed target Sketch curve.");
            row["native_section_relation"] = null;
            row["section_relation_source_match"] = "not_applicable_associative_false";
        }
        row["source_tag"] = sourceTag;
        row["source_journal_id"] = sourceJournalId;
        row["source_object_type"] = sourceAfterEdge.GetType().Name;
        row["target_sketch_tag"] = sketch.Tag.ToString();
        row["target_sketch_journal_id"] = sketch.JournalIdentifier;
        row["output_tag"] = output.Tag.ToString();
        row["output_type"] = output.GetType().Name;
        row["native_operation_evidence"] = execution.Result;

        return new ProjectionFixture
        {
            CaseId = caseId, SourceKind = sourceKind, Associative = associative,
            DirectionSign = directionSign, SourceTag = sourceTag,
            SourceJournalId = sourceJournalId, SketchId = sketch.JournalIdentifier,
            SketchTag = sketch.Tag.ToString(),
            FeatureId = projectionFeature == null ? null : projectionFeature.JournalIdentifier,
            FeatureTag = projectionFeature == null ? null : projectionFeature.Tag.ToString(),
            OutputTag = output.Tag.ToString(), OutputType = output.GetType().Name,
            SourceRecord = sourceBefore, Oracle = oracle, Relation = relation
        };
    }

    private static Dictionary<string, object> RunSaveCopyRoundTrip(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Session session, string targetPath,
        ProjectionFixture fixture, double toleranceMm)
    {
        Dictionary<string, object> row = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "case_id", fixture.CaseId }, { "source_part_path_before", session.Parts.Work.FullPath },
            { "target_path", targetPath }, { "feature_tag_before", fixture.FeatureTag },
            { "feature_journal_id_before", fixture.FeatureId },
            { "source_tag_before", fixture.SourceTag }, { "source_journal_id_before", fixture.SourceJournalId },
            { "target_sketch_tag_before", fixture.SketchTag }, { "target_sketch_journal_id_before", fixture.SketchId },
            { "output_tag_before", fixture.OutputTag }, { "output_type_before", fixture.OutputType },
            { "reproduction_steps", new string[] {
                "Use the recorded unique source model and the associative positive-normal block Edge case.",
                "Call production M2OpenModelAdapter.SaveCopy with the recorded unique copy path and reopen=true.",
                "Resolve the saved CPROJ feature, re-read Section.GetSectionData/GetSectionElementsData/GetSectionElementData1, resolve the reported source Tag to a native solid Edge, and compare the reopened target Sketch Line to the independent native Edge projection oracle." } },
            { "retest_criterion", "On a new unique millimetre fixture, production SaveCopy must return SAVED/reopened with passing checks, the CPROJ Section input must resolve by exact Tag/JID to a native Edge, and the direct reopened output Line endpoint residual must not exceed 1e-6 mm." }
        };
        try
        {
            M2SaveCopyResult saved = adapter.SaveCopy(binding, targetPath, true);
            row["adapter_save_copy_result"] = SaveCopyRecord(saved);
            Require(saved != null && saved.State == "SAVED" && saved.Reopened && ChecksPass(saved.Checks),
                "Production SaveCopy did not save, reopen, and pass its independent checks.");
            Part reopened = session.Parts.Work;
            Require(reopened != null && String.Equals(Path.GetFullPath(reopened.FullPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase),
                "The saved copy is not the current NX Work Part after reopen.");
            row["reopened_part_path"] = reopened.FullPath;
            row["reopened_part_tag"] = reopened.Tag.ToString();
            row["reopened_part_units"] = reopened.PartUnits.ToString();

            Feature feature = FindFeatureByJournalId(reopened, fixture.FeatureId);
            Require(feature != null,
                "The associative projection feature Journal Identifier did not resolve after Save Copy/reopen.");
            Dictionary<string, object> relation = ReadProjectionRelation(reopened, feature);
            row["native_section_relation_after_reopen"] = relation;
            List<object> inputs = relation["input_elements"] as List<object>;
            Require(inputs != null && inputs.Count == 1,
                "Reopened CPROJ Section element-data did not contain exactly one source input.");
            Dictionary<string, object> input = inputs[0] as Dictionary<string, object>;
            string reopenedSourceTag = GetString(input, "input_tag");
            string reopenedSourceId = GetString(input, "input_journal_id");
            Edge reopenedSource = FindEdgeByTag(reopened, reopenedSourceTag);
            Require(reopenedSource != null,
                "Reopened Section element-data source Tag did not resolve to a native Edge in the reopened Body inventory.");
            row["reopened_source_edge"] = NativeEdgeRecord(reopenedSource, fixture.SourceKind);
            row["reopened_source_tag_matches_relation"] = String.Equals(reopenedSource.Tag.ToString(), reopenedSourceTag, StringComparison.Ordinal);
            row["reopened_source_journal_id_matches_relation"] = String.Equals(reopenedSource.JournalIdentifier, reopenedSourceId, StringComparison.Ordinal);
            row["relation_source_object_type"] = GetString(input, "input_type");
            Require(String.Equals(reopenedSource.JournalIdentifier, reopenedSourceId, StringComparison.Ordinal),
                "Reopened Section element-data source Journal Identifier did not match the resolved native Edge.");
            Require(String.Equals(GetString(input, "input_type"), "Edge", StringComparison.Ordinal),
                "Reopened Section element-data source is not a solid Edge.");

            Sketch sketch = FindSketchByJournalId(reopened, fixture.SketchId);
            Require(sketch != null,
                "Target Sketch Journal Identifier did not resolve after Save Copy/reopen.");
            row["reopened_target_sketch"] = SketchRecord(sketch);
            Dictionary<string, object> directOutput = FindLineMatchingOracle(sketch, fixture.Oracle, toleranceMm);
            Require(directOutput != null && Object.Equals(directOutput["matches"], true),
                "No reopened target Sketch Line matches the independent expected projection geometry.");
            row["reopened_target_geometry_match"] = directOutput;
            row["geometry_residual_mm"] = GetDouble(directOutput, "max_endpoint_residual_mm");
            Require(GetDouble(directOutput, "max_endpoint_residual_mm") <= toleranceMm,
                "Reopened target Sketch geometry exceeds the direct projection position tolerance.");
            row["passed"] = true;
        }
        catch (Exception ex)
        {
            row["passed"] = false;
            row["failure"] = ExceptionRecord(ex);
        }
        return row;
    }

    private static Sketch CreateTargetSketch(Session session, Part part, Frame frame, string name)
    {
        Require(session != null && part != null, "Direct NXOpen target Sketch setup requires Session and Part.");
        Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible,
            "F10 direct Edge target Sketch " + name);
        Sketch sketch = null;
        SketchInPlaceBuilder builder = null;
        try
        {
            Plane plane = part.Planes.CreatePlane(ToPoint(frame.Origin), ToVector(frame.Normal),
                SmartObject.UpdateOption.WithinModeling);
            Direction axis = part.Directions.CreateDirection(ToPoint(frame.Origin), ToVector(frame.XAxis),
                SmartObject.UpdateOption.WithinModeling);
            Point sketchOrigin = part.Points.CreatePoint(ToPoint(frame.Origin));
            builder = part.Sketches.CreateSketchInPlaceBuilder2(null);
            builder.PlaneReference = plane;
            builder.AxisReference = axis;
            builder.SketchOrigin = sketchOrigin;
            builder.PlaneOption = Sketch.PlaneOption.ExistingPlane;
            builder.OriginOption = OriginMethod.SpecifyPoint;
            sketch = builder.Commit() as Sketch;
            Require(sketch != null, "Direct NXOpen SketchInPlaceBuilder did not return a Sketch.");
            if (session.ActiveSketch != null)
            {
                Require(session.ActiveSketch.Tag.Equals(sketch.Tag),
                    "NX activated a different Sketch while committing the direct target Sketch fixture.");
                sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
            }
            int updateErrors = session.UpdateManager.DoUpdate(mark);
            Require(updateErrors == 0, "Direct NXOpen target Sketch update reported errors.");
            Require(session.ActiveSketch == null,
                "Direct NXOpen target Sketch fixture setup left a Sketch active.");
            Require(sketch.GetAllGeometry() != null && sketch.GetAllGeometry().Length == 0,
                "Direct NXOpen target Sketch fixture unexpectedly contains geometry.");
            Frame actual = ReadSketchFrame(sketch);
            Require(Distance(actual.Origin, frame.Origin) <= FixtureMatchToleranceMm &&
                Norm(Subtract(actual.Normal, frame.Normal)) <= DirectionTolerance &&
                Norm(Subtract(actual.XAxis, frame.XAxis)) <= DirectionTolerance,
                "Direct NXOpen target Sketch frame differs from the requested tilted frame.");
            return sketch;
        }
        finally
        {
            if (builder != null) builder.Destroy();
            if (session.ActiveSketch != null && sketch != null && session.ActiveSketch.Tag.Equals(sketch.Tag))
            {
                try { sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model); }
                catch { }
            }
            session.DeleteUndoMark(mark, null);
        }
    }

    private static Dictionary<string, object> CreateSolidSources(Part part, Frame frame)
    {
        double[] blockOrigin = new double[] { 30.0, 25.0, 18.0 };
        string[] blockSizes = new string[] { "14", "9", "6" };
        Tag blockFeatureTag;
        UFSession.GetUFSession().Modl.CreateBlock1(FeatureSigns.Nullsign,
            blockOrigin, blockSizes, out blockFeatureTag);
        Feature blockFeature = NXOpen.Utilities.NXObjectManager.Get(blockFeatureTag) as Feature;
        Require(blockFeature != null, "UF CreateBlock1 did not return a native Block feature.");
        Tag blockBodyTag;
        UFSession.GetUFSession().Modl.AskFeatBody(blockFeatureTag, out blockBodyTag);
        Body blockBody = NXOpen.Utilities.NXObjectManager.Get(blockBodyTag) as Body;
        Require(blockBody != null && blockBody.IsSolidBody,
            "UF block fixture did not produce a native solid Body.");
        Edge linearEdge = null;
        Point3d expectedStart = new Point3d(blockOrigin[0], blockOrigin[1], blockOrigin[2]);
        Point3d expectedEnd = new Point3d(blockOrigin[0] + 14.0, blockOrigin[1], blockOrigin[2]);
        foreach (Edge edge in blockBody.GetEdges())
        {
            if (edge == null || edge.SolidEdgeType != Edge.EdgeType.Linear) continue;
            Point3d first, second;
            edge.GetVertices(out first, out second);
            if (SamePoint(Point(first), Point(expectedStart), FixtureMatchToleranceMm) &&
                SamePoint(Point(second), Point(expectedEnd), FixtureMatchToleranceMm) ||
                SamePoint(Point(first), Point(expectedEnd), FixtureMatchToleranceMm) &&
                SamePoint(Point(second), Point(expectedStart), FixtureMatchToleranceMm))
            {
                Require(linearEdge == null, "Block fixture has multiple linear Edges matching the specified straight source.");
                linearEdge = edge;
            }
        }
        Require(linearEdge != null,
            "Could not locate the specified block straight Edge by direct native endpoint inspection.");

        double[] circleCenter = Add(Add(Add(frame.Origin, Scale(frame.XAxis, 22.0)),
            Scale(frame.YAxis, 15.0)), Scale(frame.Normal, 10.0));
        double[] cylinderAxis = (double[])frame.Normal.Clone();
        Tag cylinderFeatureTag;
        UFSession.GetUFSession().Modl.CreateCylinder(FeatureSigns.Nullsign, Tag.Null,
            circleCenter, "8", "6", cylinderAxis, out cylinderFeatureTag);
        Feature cylinderFeature = NXOpen.Utilities.NXObjectManager.Get(cylinderFeatureTag) as Feature;
        Require(cylinderFeature != null, "UF CreateCylinder did not return a native Cylinder feature.");
        Tag cylinderBodyTag;
        UFSession.GetUFSession().Modl.AskFeatBody(cylinderFeatureTag, out cylinderBodyTag);
        Body cylinderBody = NXOpen.Utilities.NXObjectManager.Get(cylinderBodyTag) as Body;
        Require(cylinderBody != null && cylinderBody.IsSolidBody,
            "UF cylinder fixture did not produce a native solid Body.");
        Edge circularEdge = null;
        foreach (Edge edge in cylinderBody.GetEdges())
        {
            if (edge == null || edge.SolidEdgeType != Edge.EdgeType.Circular) continue;
            CircularData data = ReadCircularEdge(edge);
            if (Math.Abs(data.Radius - 3.0) > FixtureMatchToleranceMm ||
                Distance(data.Center, circleCenter) > FixtureMatchToleranceMm) continue;
            Require(circularEdge == null, "Cylinder fixture has multiple circular Edges matching the specified source center.");
            circularEdge = edge;
        }
        Require(circularEdge != null,
            "Could not locate the cylinder circular Edge by direct UF native evaluator inspection.");

        Dictionary<string, object> linearRecord = NativeEdgeRecord(linearEdge, "linear_edge");
        Dictionary<string, object> circularRecord = NativeEdgeRecord(circularEdge, "circular_edge");
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "block_feature", FeatureRecord(blockFeature) },
            { "block_body", BodyRecord(blockBody) },
            { "block_origin_mm", blockOrigin }, { "block_size_mm", new double[] { 14.0, 9.0, 6.0 } },
            { "linear_edge", linearRecord },
            { "cylinder_feature", FeatureRecord(cylinderFeature) },
            { "cylinder_body", BodyRecord(cylinderBody) },
            { "cylinder_base_center_mm", circleCenter }, { "cylinder_axis_world", cylinderAxis },
            { "cylinder_diameter_mm", 6.0 }, { "cylinder_length_mm", 8.0 },
            { "circular_edge", circularRecord }
        };
    }

    private static Dictionary<string, object> NativeEdgeRecord(Edge edge, string sourceKind)
    {
        Require(edge != null, "Native Edge record requested for a null Edge.");
        Body body = edge.GetBody();
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", edge.Tag.ToString() }, { "journal_id", edge.JournalIdentifier },
            { "object_type", edge.GetType().Name }, { "solid_edge_type", edge.SolidEdgeType.ToString() },
            { "source_kind", sourceKind }, { "owning_part_path", edge.OwningPart == null ? null : edge.OwningPart.FullPath },
            { "body_tag", body == null ? null : body.Tag.ToString() },
            { "body_journal_id", body == null ? null : body.JournalIdentifier },
            { "length_mm", edge.GetLength() }
        };
        Require(!String.IsNullOrWhiteSpace(edge.JournalIdentifier),
            "Native source Edge has no Journal Identifier for the production request.");
        if (edge.SolidEdgeType == Edge.EdgeType.Linear)
        {
            Point3d first, second;
            edge.GetVertices(out first, out second);
            result["start_vertex_mm"] = Point(first);
            result["end_vertex_mm"] = Point(second);
            result["geometric_definition"] = "Straight finite segment from direct Edge.GetVertices readback; no adapter preview values used.";
        }
        else if (edge.SolidEdgeType == Edge.EdgeType.Circular)
        {
            CircularData circle = ReadCircularEdge(edge);
            result["center_mm"] = circle.Center;
            result["axis_x_world"] = circle.AxisX;
            result["axis_y_world"] = circle.AxisY;
            result["plane_normal_world"] = circle.Normal;
            result["radius_mm"] = circle.Radius;
            result["start_angle_rad"] = circle.StartAngle;
            result["end_angle_rad"] = circle.EndAngle;
            result["raw_parameter_delta_rad"] = circle.EndAngle - circle.StartAngle;
            result["effective_sweep_rad"] = circle.Periodic ? 2.0 * Math.PI : Math.Abs(circle.EndAngle - circle.StartAngle);
            result["periodic"] = circle.Periodic;
            result["geometric_definition"] = "Circular Edge from independent UF evaluator UFEval.AskArc center, axes, limits, periodic flag, radius, and Edge.GetLength.";
        }
        else
            throw new InvalidOperationException("Fixture Edge type is not the supported linear or circular type.");
        return result;
    }

    private static Dictionary<string, object> BuildIndependentOracle(Edge source,
        string sourceKind, Frame frame, double[] direction)
    {
        Dictionary<string, object> oracle = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "method", "Independent projection from native NX Edge records; adapter preview is not used." },
            { "source_tag", source.Tag.ToString() }, { "source_journal_id", source.JournalIdentifier },
            { "target_origin_mm", frame.Origin }, { "target_normal_world", frame.Normal },
            { "projection_direction_world", direction }
        };
        if (sourceKind == "linear_edge")
        {
            Point3d first, second;
            source.GetVertices(out first, out second);
            double[] expectedStart = ProjectPoint(Point(first), frame.Origin, frame.Normal, direction);
            double[] expectedEnd = ProjectPoint(Point(second), frame.Origin, frame.Normal, direction);
            oracle["source_start_vertex_mm"] = Point(first);
            oracle["source_end_vertex_mm"] = Point(second);
            oracle["expected_start_mm"] = expectedStart;
            oracle["expected_end_mm"] = expectedEnd;
            oracle["expected_length_mm"] = Distance(expectedStart, expectedEnd);
        }
        else
        {
            CircularData circle = ReadCircularEdge(source);
            double normalAlignment = Math.Abs(Dot(circle.Normal, frame.Normal));
            double normalCrossResidual = Norm(Cross(circle.Normal, frame.Normal));
            double[] expectedCenter = ProjectPoint(circle.Center, frame.Origin, frame.Normal, direction);
            oracle["source_center_mm"] = circle.Center;
            oracle["source_axis_x_world"] = circle.AxisX;
            oracle["source_axis_y_world"] = circle.AxisY;
            oracle["source_plane_normal_world"] = circle.Normal;
            oracle["source_radius_mm"] = circle.Radius;
            oracle["source_periodic"] = circle.Periodic;
            oracle["source_effective_sweep_rad"] = circle.Periodic ? 2.0 * Math.PI : Math.Abs(circle.EndAngle - circle.StartAngle);
            oracle["source_length_mm"] = circle.Length;
            oracle["expected_center_mm"] = expectedCenter;
            oracle["expected_radius_mm"] = circle.Radius;
            oracle["expected_length_mm"] = circle.Length;
            oracle["source_plane_alignment_abs_dot_residual"] = Math.Abs(1.0 - normalAlignment);
            oracle["source_plane_cross_residual"] = normalCrossResidual;
            Require(Math.Abs(1.0 - normalAlignment) <= DirectionTolerance &&
                normalCrossResidual <= DirectionTolerance,
                "Circular solid Edge plane is not parallel to the tilted target Sketch plane.");
            Require(circle.Periodic || Math.Abs(circle.Length / circle.Radius - Math.Abs(circle.EndAngle - circle.StartAngle)) <= DirectionTolerance,
                "Circular Edge length and evaluator sweep do not independently agree.");
        }
        return oracle;
    }

    private static Dictionary<string, object> CompareOutputToOracle(Curve output,
        string sourceKind, Dictionary<string, object> oracle, Frame frame)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "output_tag", output.Tag.ToString() }, { "output_type", output.GetType().Name },
            { "acceptance_position_tolerance_mm", PositionToleranceMm }
        };
        if (sourceKind == "linear_edge")
        {
            Line line = output as Line;
            Require(line != null, "The direct target Sketch output for a linear Edge is not a native Line.");
            double[] actualStart = Point(line.StartPoint);
            double[] actualEnd = Point(line.EndPoint);
            double[] expectedStart = GetNumbers(oracle, "expected_start_mm", 3);
            double[] expectedEnd = GetNumbers(oracle, "expected_end_mm", 3);
            double direct = Math.Max(Distance(actualStart, expectedStart), Distance(actualEnd, expectedEnd));
            double reverse = Math.Max(Distance(actualStart, expectedEnd), Distance(actualEnd, expectedStart));
            double residual = Math.Min(direct, reverse);
            result["actual_start_mm"] = actualStart;
            result["actual_end_mm"] = actualEnd;
            result["expected_start_mm"] = expectedStart;
            result["expected_end_mm"] = expectedEnd;
            result["endpoint_order_independent_max_residual_mm"] = residual;
            result["length_residual_mm"] = Math.Abs(line.GetLength() - GetDouble(oracle, "expected_length_mm"));
            result["matches"] = residual <= PositionToleranceMm &&
                GetDouble(result, "length_residual_mm") <= PositionToleranceMm;
        }
        else
        {
            Dictionary<string, object> actual = CurveCircularRecord(output);
            Require(actual != null, "The direct target Sketch output for a circular Edge is neither an Arc nor a Circle-like native curve.");
            double centerResidual = Distance(GetNumbers(actual, "center_mm", 3), GetNumbers(oracle, "expected_center_mm", 3));
            double radiusResidual = Math.Abs(GetDouble(actual, "radius_mm") - GetDouble(oracle, "expected_radius_mm"));
            double lengthResidual = Math.Abs(output.GetLength() - GetDouble(oracle, "expected_length_mm"));
            double[] actualNormal = GetNumbers(actual, "plane_normal_world", 3);
            double[] expectedNormal = GetNumbers(oracle, "target_normal_world", 3);
            double planeCross = Norm(Cross(Normalize(actualNormal), Normalize(expectedNormal)));
            double effectiveSweep = GetDouble(actual, "length_mm") / GetDouble(actual, "radius_mm");
            double sweepResidual = Math.Abs(effectiveSweep - GetDouble(oracle, "source_effective_sweep_rad"));
            result["actual_curve"] = actual;
            result["center_residual_mm"] = centerResidual;
            result["radius_residual_mm"] = radiusResidual;
            result["length_residual_mm"] = lengthResidual;
            result["plane_normal_cross_residual"] = planeCross;
            result["effective_sweep_rad"] = effectiveSweep;
            result["effective_sweep_residual_rad"] = sweepResidual;
            result["matches"] = centerResidual <= PositionToleranceMm &&
                radiusResidual <= PositionToleranceMm && lengthResidual <= PositionToleranceMm &&
                planeCross <= DirectionTolerance && sweepResidual <= DirectionTolerance;
        }
        return result;
    }

    private static Dictionary<string, object> CompareSourceGeometry(
        Dictionary<string, object> before, Dictionary<string, object> after, string sourceKind)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (sourceKind == "linear_edge")
        {
            double[] a1 = GetNumbers(before, "start_vertex_mm", 3);
            double[] a2 = GetNumbers(before, "end_vertex_mm", 3);
            double[] b1 = GetNumbers(after, "start_vertex_mm", 3);
            double[] b2 = GetNumbers(after, "end_vertex_mm", 3);
            double direct = Math.Max(Distance(a1, b1), Distance(a2, b2));
            double reverse = Math.Max(Distance(a1, b2), Distance(a2, b1));
            double residual = Math.Min(direct, reverse);
            result["endpoint_order_independent_max_residual_mm"] = residual;
            result["length_residual_mm"] = Math.Abs(GetDouble(before, "length_mm") - GetDouble(after, "length_mm"));
            result["matches"] = residual <= PositionToleranceMm && GetDouble(result, "length_residual_mm") <= PositionToleranceMm;
        }
        else
        {
            double centerResidual = Distance(GetNumbers(before, "center_mm", 3), GetNumbers(after, "center_mm", 3));
            double radiusResidual = Math.Abs(GetDouble(before, "radius_mm") - GetDouble(after, "radius_mm"));
            double normalCross = Norm(Cross(Normalize(GetNumbers(before, "plane_normal_world", 3)),
                Normalize(GetNumbers(after, "plane_normal_world", 3))));
            double lengthResidual = Math.Abs(GetDouble(before, "length_mm") - GetDouble(after, "length_mm"));
            result["center_residual_mm"] = centerResidual;
            result["radius_residual_mm"] = radiusResidual;
            result["plane_normal_cross_residual"] = normalCross;
            result["length_residual_mm"] = lengthResidual;
            result["matches"] = centerResidual <= PositionToleranceMm && radiusResidual <= PositionToleranceMm &&
                normalCross <= DirectionTolerance && lengthResidual <= PositionToleranceMm;
        }
        result["acceptance_position_tolerance_mm"] = PositionToleranceMm;
        return result;
    }

    private static Dictionary<string, object> FindLineMatchingOracle(Sketch sketch,
        Dictionary<string, object> oracle, double toleranceMm)
    {
        double[] expectedStart = GetNumbers(oracle, "expected_start_mm", 3);
        double[] expectedEnd = GetNumbers(oracle, "expected_end_mm", 3);
        List<object> candidates = new List<object>();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Line line = item as Line;
            if (line == null) continue;
            double[] a = Point(line.StartPoint), b = Point(line.EndPoint);
            double residual = Math.Min(Math.Max(Distance(a, expectedStart), Distance(b, expectedEnd)),
                Math.Max(Distance(a, expectedEnd), Distance(b, expectedStart)));
            Dictionary<string, object> row = CurveRecord(line);
            row["max_endpoint_residual_mm"] = residual;
            row["matches"] = residual <= toleranceMm;
            candidates.Add(row);
        }
        foreach (object candidate in candidates)
        {
            Dictionary<string, object> row = candidate as Dictionary<string, object>;
            if (row != null && Object.Equals(row["matches"], true))
            {
                Dictionary<string, object> match = new Dictionary<string, object>(row, StringComparer.Ordinal);
                match["all_reopened_line_candidates"] = candidates;
                match["oracle_expected_start_mm"] = expectedStart;
                match["oracle_expected_end_mm"] = expectedEnd;
                return match;
            }
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "matches", false }, { "all_reopened_line_candidates", candidates },
            { "oracle_expected_start_mm", expectedStart }, { "oracle_expected_end_mm", expectedEnd }
        };
    }

    private static Dictionary<string, object> ReadProjectionRelation(Part part, Feature feature)
    {
        SketchProjectBuilder builder = null;
        try
        {
            builder = part.Sketches.CreateProjectBuilder(feature);
            Require(builder != null && builder.Section != null,
                "NX did not provide a projection builder and Section for the associative projection feature.");
            SectionData[] sectionData;
            builder.Section.GetSectionData(out sectionData);
            Require(sectionData != null, "CPROJ Section.GetSectionData returned null.");
            List<object> inputElements = new List<object>();
            for (int sectionIndex = 0; sectionIndex < sectionData.Length; sectionIndex++)
            {
                SectionElementData[] elements;
                sectionData[sectionIndex].GetSectionElementsData(out elements);
                Require(elements != null, "CPROJ Section element data returned null.");
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
            NXObject[] sectionOutputs;
            builder.Section.GetOutputCurves(out sectionOutputs);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "relation_readback", "NXOpen Section.GetSectionData/GetSectionElementsData/GetSectionElementData1" },
                { "projection_feature_tag", feature.Tag.ToString() },
                { "projection_feature_journal_id", feature.JournalIdentifier },
                { "projection_feature_type", feature.FeatureType },
                { "section_count", sectionData.Length }, { "input_elements", inputElements },
                { "section_internal_output_records", ObjectRecords(sectionOutputs) },
                { "feature_parent_ids_are_source_oracle", false }
            };
        }
        finally { if (builder != null) builder.Destroy(); }
    }

    private static bool RelationMatchesSource(Dictionary<string, object> relation, Edge source)
    {
        if (relation == null || source == null) return false;
        List<object> inputs = relation["input_elements"] as List<object>;
        if (inputs == null || inputs.Count != 1) return false;
        Dictionary<string, object> input = inputs[0] as Dictionary<string, object>;
        return input != null && String.Equals(GetString(input, "input_tag"), source.Tag.ToString(), StringComparison.Ordinal) &&
            String.Equals(GetString(input, "input_journal_id"), source.JournalIdentifier, StringComparison.Ordinal) &&
            String.Equals(GetString(input, "input_type"), "Edge", StringComparison.Ordinal);
    }

    private static CircularData ReadCircularEdge(Edge edge)
    {
        IntPtr evaluator = IntPtr.Zero;
        try
        {
            UFSession.GetUFSession().Eval.Initialize2(edge.Tag, out evaluator);
            Require(evaluator != IntPtr.Zero, "UF evaluator initialization returned a null handle for a circular Edge.");
            UFEval.Arc arc;
            UFSession.GetUFSession().Eval.AskArc(evaluator, out arc);
            Require(arc.center != null && arc.center.Length >= 3 && arc.x_axis != null && arc.x_axis.Length >= 3 &&
                arc.y_axis != null && arc.y_axis.Length >= 3 && arc.limits != null && arc.limits.Length >= 2,
                "UF circular Edge evaluator returned incomplete center/axes/limits data.");
            double[] axisX = Normalize(new double[] { arc.x_axis[0], arc.x_axis[1], arc.x_axis[2] });
            double[] axisY = Normalize(new double[] { arc.y_axis[0], arc.y_axis[1], arc.y_axis[2] });
            double[] normal = Normalize(Cross(axisX, axisY));
            return new CircularData
            {
                Center = new double[] { arc.center[0], arc.center[1], arc.center[2] },
                AxisX = axisX, AxisY = axisY, Normal = normal,
                Radius = arc.radius, StartAngle = arc.limits[0], EndAngle = arc.limits[1],
                Periodic = arc.is_periodic, Length = edge.GetLength()
            };
        }
        finally
        {
            if (evaluator != IntPtr.Zero)
            {
                try { UFSession.GetUFSession().Eval.Free(evaluator); } catch { }
            }
        }
    }

    private static Dictionary<string, object> CurveCircularRecord(Curve curve)
    {
        Arc arc = curve as Arc;
        if (arc != null)
        {
            Point3d center;
            Vector3d axisX, axisY;
            arc.GetOrientation(out center, out axisX, out axisY);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "type", arc.GetType().Name }, { "center_mm", Point(center) }, { "radius_mm", arc.Radius },
                { "axis_x_world", Vector(axisX) }, { "axis_y_world", Vector(axisY) },
                { "plane_normal_world", Normalize(Cross(Vector(axisX), Vector(axisY))) },
                { "start_angle_rad", arc.StartAngle }, { "end_angle_rad", arc.EndAngle },
                { "effective_sweep_rad", Math.Abs(arc.EndAngle - arc.StartAngle) },
                { "length_mm", arc.GetLength() }
            };
        }
        // NX may expose a periodic result as a Circle rather than a 0..2pi Arc.
        // Read it reflectively to keep the source module compatible with either
        // NXOpen wrapper naming convention while retaining direct native values.
        Type type = curve == null ? null : curve.GetType();
        if (type == null || !String.Equals(type.Name, "Circle", StringComparison.Ordinal)) return null;
        object centerValue = ReadProperty(curve, "Center");
        if (centerValue == null) centerValue = ReadProperty(curve, "CenterPoint");
        object radiusValue = ReadProperty(curve, "Radius");
        if (!(radiusValue is double)) return null;
        double[] centerPoint = PointFromObject(centerValue);
        MethodInfo orientation = type.GetMethod("GetOrientation", BindingFlags.Public | BindingFlags.Instance);
        if (orientation == null) return null;
        object[] args = new object[] { new Point3d(), new Vector3d(), new Vector3d() };
        orientation.Invoke(curve, args);
        Vector3d circleAxisX = (Vector3d)args[1];
        Vector3d circleAxisY = (Vector3d)args[2];
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "type", type.Name }, { "center_mm", centerPoint }, { "radius_mm", Convert.ToDouble(radiusValue, CultureInfo.InvariantCulture) },
            { "axis_x_world", Vector(circleAxisX) }, { "axis_y_world", Vector(circleAxisY) },
            { "plane_normal_world", Normalize(Cross(Vector(circleAxisX), Vector(circleAxisY))) },
            { "effective_sweep_rad", curve.GetLength() / Convert.ToDouble(radiusValue, CultureInfo.InvariantCulture) },
            { "length_mm", curve.GetLength() }
        };
    }

    private static Dictionary<string, object> FeatureRecord(Feature feature)
    {
        if (feature == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", feature.Tag.ToString() }, { "journal_id", feature.JournalIdentifier },
            { "feature_type", feature.FeatureType }, { "object_type", feature.GetType().Name }
        };
    }

    private static Dictionary<string, object> BodyRecord(Body body)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", body.Tag.ToString() }, { "journal_id", body.JournalIdentifier },
            { "object_type", body.GetType().Name }, { "is_solid_body", body.IsSolidBody }
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

    private static Dictionary<string, object> SketchRecord(Sketch sketch)
    {
        Frame frame = ReadSketchFrame(sketch);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "tag", sketch.Tag.ToString() }, { "journal_id", sketch.JournalIdentifier },
            { "feature_tag", sketch.Feature == null ? null : sketch.Feature.Tag.ToString() },
            { "feature_journal_id", sketch.Feature == null ? null : sketch.Feature.JournalIdentifier },
            { "origin_mm", frame.Origin }, { "normal_world", frame.Normal },
            { "x_axis_world", frame.XAxis }, { "y_axis_world", frame.YAxis },
            { "geometry", SketchGeometryRecords(sketch) }
        };
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
        Dictionary<string, object> circle = CurveCircularRecord(curve);
        if (circle != null)
            foreach (KeyValuePair<string, object> item in circle) result[item.Key] = item.Value;
        return result;
    }

    private static List<object> SketchGeometryRecords(Sketch sketch)
    {
        List<object> records = new List<object>();
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Curve curve = item as Curve;
            records.Add(curve == null ? (object)new Dictionary<string, object>(StringComparer.Ordinal)
                { { "tag", item == null ? null : item.Tag.ToString() }, { "object_type", item == null ? null : item.GetType().Name } }
                : CurveRecord(curve));
        }
        return records;
    }

    private static List<object> ObjectRecords(IEnumerable<NXObject> objects)
    {
        List<object> result = new List<object>();
        if (objects == null) return result;
        foreach (NXObject item in objects)
            result.Add(item == null ? null : new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "tag", item.Tag.ToString() }, { "journal_id", item.JournalIdentifier },
                { "object_type", item.GetType().Name }
            });
        return result;
    }

    private static HashSet<string> SketchGeometryTags(Sketch sketch)
    {
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (NXObject item in sketch.GetAllGeometry())
        {
            Require(item != null, "Sketch.GetAllGeometry returned a null geometry object.");
            Require(tags.Add(item.Tag.ToString()), "Sketch.GetAllGeometry returned duplicate Tags.");
        }
        return tags;
    }

    private static List<NXObject> NewSketchGeometry(Sketch sketch, HashSet<string> before)
    {
        List<NXObject> result = new List<NXObject>();
        foreach (NXObject item in sketch.GetAllGeometry())
            if (item != null && !before.Contains(item.Tag.ToString())) result.Add(item);
        return result;
    }

    private static List<Feature> NewFeatures(Part part, HashSet<string> beforeTags)
    {
        List<Feature> result = new List<Feature>();
        foreach (Feature feature in part.Features.ToArray())
            if (feature != null && !beforeTags.Contains(feature.Tag.ToString())) result.Add(feature);
        return result;
    }

    private static HashSet<string> FeatureTags(Part part)
    {
        HashSet<string> tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (Feature feature in part.Features.ToArray())
        {
            Require(feature != null, "Part feature inventory contains null.");
            tags.Add(feature.Tag.ToString());
        }
        return tags;
    }

    private static List<object> FeatureRecords(IEnumerable<Feature> features)
    {
        List<object> records = new List<object>();
        if (features == null) return records;
        foreach (Feature feature in features) records.Add(FeatureRecord(feature));
        return records;
    }

    private static Edge FindEdgeByTag(Part part, string tag)
    {
        if (part == null || String.IsNullOrWhiteSpace(tag)) return null;
        Edge found = null;
        foreach (Body body in part.Bodies.ToArray())
        {
            if (body == null) continue;
            foreach (Edge edge in body.GetEdges())
            {
                if (edge == null || !String.Equals(edge.Tag.ToString(), tag, StringComparison.Ordinal)) continue;
                if (found != null && found.Tag != edge.Tag)
                    throw new InvalidOperationException("A native Edge Tag resolved to multiple Bodies.");
                found = edge;
            }
        }
        return found;
    }

    private static Sketch FindSketchByJournalId(Part part, string journalId)
    {
        if (part == null || String.IsNullOrWhiteSpace(journalId)) return null;
        foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null && String.Equals(sketch.JournalIdentifier, journalId, StringComparison.Ordinal)) return sketch;
        return null;
    }

    private static Sketch FindSketchByTag(Part part, string tag)
    {
        if (part == null || String.IsNullOrWhiteSpace(tag)) return null;
        Sketch found = null;
        foreach (Sketch sketch in part.Sketches.ToArray())
        {
            if (sketch == null || !String.Equals(sketch.Tag.ToString(), tag, StringComparison.Ordinal)) continue;
            if (found != null && !found.Tag.Equals(sketch.Tag))
                throw new InvalidOperationException("A target Sketch Tag resolved more than once.");
            found = sketch;
        }
        return found;
    }

    private static Feature FindFeatureByJournalId(Part part, string journalId)
    {
        if (part == null || String.IsNullOrWhiteSpace(journalId)) return null;
        foreach (Feature feature in part.Features.ToArray())
            if (feature != null && String.Equals(feature.JournalIdentifier, journalId, StringComparison.Ordinal)) return feature;
        return null;
    }

    private static Frame ReadSketchFrame(Sketch sketch)
    {
        Matrix3x3 axes = sketch.Orientation.Element;
        return new Frame
        {
            Origin = Point(sketch.Origin),
            XAxis = Normalize(new double[] { axes.Xx, axes.Xy, axes.Xz }),
            YAxis = Normalize(new double[] { axes.Yx, axes.Yy, axes.Yz }),
            Normal = Normalize(new double[] { axes.Zx, axes.Zy, axes.Zz })
        };
    }

    private static Frame MakeFrame(double[] origin, double[] normal, double[] xAxis)
    {
        double[] n = Normalize(normal);
        double[] x = Normalize(Subtract(xAxis, Scale(n, Dot(xAxis, n))));
        double[] y = Normalize(Cross(n, x));
        return new Frame { Origin = (double[])origin.Clone(), Normal = n, XAxis = x, YAxis = y };
    }

    private static Frame TranslateFrame(Frame source, double[] displacement)
    {
        return new Frame
        {
            Origin = Add(source.Origin, displacement),
            Normal = (double[])source.Normal.Clone(),
            XAxis = (double[])source.XAxis.Clone(),
            YAxis = (double[])source.YAxis.Clone()
        };
    }

    private static Dictionary<string, object> FrameRecord(Frame frame)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "origin_mm", frame.Origin }, { "normal_world", frame.Normal },
            { "x_axis_world", frame.XAxis }, { "y_axis_world", frame.YAxis }
        };
    }

    private static Dictionary<string, object> NewCase(string kind, bool associative, int sign)
    {
        string directionName = sign > 0 ? "positive_normal" : "negative_normal";
        string id = "F10-edge-" + kind + "-assoc_" + associative.ToString().ToLowerInvariant() + "-" + directionName;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "id", id },
            { "objective", "Project the native solid " + kind + " through the production adapter with associative=" + associative.ToString().ToLowerInvariant() + " and direction=" + directionName + "." },
            { "reproduction_steps", new string[] {
                "Open the unique source model path recorded in the report in the recorded NX configuration.",
                "Resolve the source Edge from the reported source Tag and Journal Identifier; create the tilted target Sketch using the recorded frame.",
                "Call PrepareSketchProjection with the recorded source ID, associative value, and signed Sketch normal; call ExecutePlan, then VerifyPlan.",
                "Read source geometry directly from Edge.GetVertices or UF Eval.AskArc, read the new target curve using Sketch.GetAllGeometry Tag delta, and read the CPROJ Section element data when associative=true." } },
            { "retest_criterion", "On a new unique saved millimetre part in idle NX2506, Prepare/Execute/Verify must pass; exactly one direct Sketch Tag delta must match the native Edge projection oracle within 1e-6 mm; associative=true must resolve one Section input whose Tag/JID/type equal the selected Edge, while associative=false must create no projection feature." },
            { "source_kind", kind }, { "associative", associative },
            { "direction_sign", sign }, { "direction_name", directionName }, { "passed", false }
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

    private static Dictionary<string, object> ExecutionRecord(M3ExecutionResult value)
    {
        if (value == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "state", value.State }, { "revision_id", value.RevisionId },
            { "result", value.Result }, { "error_code", value.ErrorCode }, { "error_message", value.ErrorMessage }
        };
    }

    private static Dictionary<string, object> SaveCopyRecord(M2SaveCopyResult value)
    {
        if (value == null) return null;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "state", value.State }, { "part_path", value.PartPath }, { "part_tag", value.PartTag },
            { "revision_id", value.RevisionId }, { "reopened", value.Reopened },
            { "checks", value.Checks }, { "evidence", value.Evidence },
            { "error_code", value.ErrorCode }, { "error_message", value.ErrorMessage }
        };
    }

    private static bool ChecksPass(object checks)
    {
        Dictionary<string, object> dictionary = checks as Dictionary<string, object>;
        if (dictionary == null || dictionary.Count == 0) return false;
        foreach (object value in dictionary.Values)
        {
            Dictionary<string, object> check = value as Dictionary<string, object>;
            if (check == null || !String.Equals(GetString(check, "status"), "VERIFIED", StringComparison.Ordinal))
                return false;
        }
        return true;
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

    private static void Persist(string path, Dictionary<string, object> report)
    {
        File.WriteAllText(path, M2Json.CanonicalText(report), new System.Text.UTF8Encoding(false));
    }

    private static void AddDefect(Dictionary<string, object> report,
        Dictionary<string, object> row, string retestCriterion)
    {
        List<object> defects = report["defects"] as List<object>;
        Dictionary<string, object> sourceRecord = row.ContainsKey("source_native_before")
            ? row["source_native_before"] as Dictionary<string, object> : null;
        object sourceTag = row.ContainsKey("source_tag") ? row["source_tag"] :
            (row.ContainsKey("source_tag_before") ? row["source_tag_before"] : (sourceRecord == null ? null : sourceRecord["tag"]));
        object sourceJournalId = row.ContainsKey("source_journal_id") ? row["source_journal_id"] :
            (row.ContainsKey("source_journal_id_before") ? row["source_journal_id_before"] : (sourceRecord == null ? null : sourceRecord["journal_id"]));
        Dictionary<string, object> defect = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "id", GetString(row, "id") ?? GetString(row, "case_id") },
            { "failure", row.ContainsKey("failure") ? row["failure"] : null },
            { "source_tag", sourceTag }, { "source_journal_id", sourceJournalId },
            { "request", row.ContainsKey("request") ? row["request"] : null },
            { "reproduction_steps", row["reproduction_steps"] },
            { "retest_criterion", retestCriterion }
        };
        defects.Add(defect);
    }

    private static Dictionary<string, object> ExceptionRecord(Exception exception)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "type", exception == null ? null : exception.GetType().FullName },
            { "message", exception == null ? null : exception.Message },
            { "stack_trace", exception == null ? null : exception.StackTrace }
        };
    }

    private static bool SafeModified(Part part)
    {
        try { return part != null && part.IsModified; }
        catch { return true; }
    }

    private static object ReadProperty(object target, string name)
    {
        if (target == null) return null;
        PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property == null) return null;
        try { return property.GetValue(target, null); } catch { return null; }
    }

    private static double[] PointFromObject(object value)
    {
        if (value == null) return null;
        Type type = value.GetType();
        PropertyInfo x = type.GetProperty("X"), y = type.GetProperty("Y"), z = type.GetProperty("Z");
        if (x == null || y == null || z == null) return null;
        return new double[] { Convert.ToDouble(x.GetValue(value, null), CultureInfo.InvariantCulture),
            Convert.ToDouble(y.GetValue(value, null), CultureInfo.InvariantCulture),
            Convert.ToDouble(z.GetValue(value, null), CultureInfo.InvariantCulture) };
    }

    private static double[] GetNumbers(Dictionary<string, object> dictionary, string key, int length)
    {
        object raw = dictionary == null || !dictionary.ContainsKey(key) ? null : dictionary[key];
        double[] direct = raw as double[];
        if (direct != null && direct.Length >= length) return direct;
        IList<object> list = raw as IList<object>;
        if (list != null && list.Count >= length)
        {
            double[] result = new double[length];
            for (int i = 0; i < length; i++) result[i] = Convert.ToDouble(list[i], CultureInfo.InvariantCulture);
            return result;
        }
        throw new InvalidOperationException("Expected numeric vector " + key + " is unavailable.");
    }

    private static double GetDouble(Dictionary<string, object> dictionary, string key)
    {
        if (dictionary == null || !dictionary.ContainsKey(key))
            throw new InvalidOperationException("Expected numeric value " + key + " is unavailable.");
        return Convert.ToDouble(dictionary[key], CultureInfo.InvariantCulture);
    }

    private static string GetString(Dictionary<string, object> dictionary, string key)
    {
        if (dictionary == null || !dictionary.ContainsKey(key) || dictionary[key] == null) return null;
        return Convert.ToString(dictionary[key], CultureInfo.InvariantCulture);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values != null)
            foreach (string value in values) if (!String.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    private static bool IsNx2506(string value)
    {
        return !String.IsNullOrWhiteSpace(value) && value.IndexOf("2506", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void RequireNewPath(string path, string label)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new InvalidOperationException("The unique " + label + " path already exists: " + path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string UtcNow() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
    private static double[] Point(Point3d point) { return new double[] { point.X, point.Y, point.Z }; }
    private static double[] Vector(Vector3d vector) { return new double[] { vector.X, vector.Y, vector.Z }; }
    private static Point3d ToPoint(double[] value) { return new Point3d(value[0], value[1], value[2]); }
    private static Vector3d ToVector(double[] value) { return new Vector3d(value[0], value[1], value[2]); }
    private static double[] PointAdd(double[] a, double[] b) { return Add(a, b); }
    private static double[] Add(double[] a, double[] b) { return new double[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
    private static double[] Subtract(double[] a, double[] b) { return new double[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    private static double[] Scale(double[] a, double scalar) { return new double[] { a[0] * scalar, a[1] * scalar, a[2] * scalar }; }
    private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    private static double[] Cross(double[] a, double[] b) { return new double[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
    private static double Norm(double[] a) { return Math.Sqrt(Dot(a, a)); }
    private static double Distance(double[] a, double[] b) { return Norm(Subtract(a, b)); }
    private static double[] Normalize(double[] a) { double n = Norm(a); if (n <= 0.0) throw new InvalidOperationException("Cannot normalize a zero vector."); return Scale(a, 1.0 / n); }

    private static double[] ProjectPoint(double[] point, double[] planeOrigin, double[] planeNormal, double[] direction)
    {
        double denominator = Dot(planeNormal, direction);
        Require(Math.Abs(denominator) > 1.0e-12, "Projection direction is parallel to the target Sketch plane.");
        double t = -Dot(planeNormal, Subtract(point, planeOrigin)) / denominator;
        return Add(point, Scale(direction, t));
    }

    private static bool SamePoint(double[] actual, double[] expected, double tolerance)
    {
        return Distance(actual, expected) <= tolerance;
    }

    private static bool SamePoint(Point3d actual, Point3d expected, double tolerance)
    {
        return Distance(Point(actual), Point(expected)) <= tolerance;
    }
}
