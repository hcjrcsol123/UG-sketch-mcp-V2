// Real-NX acceptance entry for F03 datum-plane offsets. Compile offline with
// build-f03-acceptance.ps1; invoke only in the designated idle NX session.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;
using NxSketchMcp.M2;

public static class F03Acceptance
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    private const double PositionToleranceMm = 1e-6;
    private const double DirectionTolerance = 1e-7;
    private const double ParentMoveMm = 5.0;

    private sealed class FaceReference
    {
        internal string FaceId;
        internal string BodyId;
        internal string BodyFeatureId;
        internal double[] Point;
        internal double[] Normal;
        internal int UfType;
    }

    private sealed class OffsetResult
    {
        internal string CaseName;
        internal string FeatureId;
        internal M3PlanSnapshot Plan;
        internal double DistanceMm;
        internal string ReferenceKind;
        internal string ReferenceId;
        internal Dictionary<string, object> Native;
    }

    private delegate void CaseBody(Dictionary<string, object> record);

    public static void Main(string[] args)
    {
        string runId = Guid.NewGuid().ToString("N");
        string modelDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "models");
        string evidenceDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "evidence");
        string sourcePath = Path.Combine(modelDirectory, "F03-acceptance-" + runId + ".prt");
        string savedPath = Path.Combine(modelDirectory, "F03-acceptance-" + runId + "-roundtrip.prt");
        string reportPath = Path.Combine(evidenceDirectory, "F03-acceptance-" + runId + ".json");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        RequireNewPath(sourcePath, "source model");
        RequireNewPath(savedPath, "roundtrip model");
        RequireNewPath(reportPath, "evidence report");

        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test", "F03 datum-plane offset NX acceptance" },
            { "test_version", "0.4.1" },
            { "started_utc", UtcNow() },
            { "run_id", runId },
            { "passed", false },
            { "source_model_path", sourcePath },
            { "roundtrip_model_path", savedPath },
            { "report_path", reportPath },
            { "position_tolerance_mm", PositionToleranceMm },
            { "direction_tolerance", DirectionTolerance },
            { "parent_move_mm", ParentMoveMm },
            { "cases", new List<object>() },
            { "defects", new List<object>() },
            { "configuration", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "test_layer", "real NXOpen subsystem validation through production M2OpenModelAdapter" },
                    { "production_bridge_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Bridge.dll") },
                    { "production_adapter_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Entry.dll") },
                    { "acceptance_assembly", Path.Combine(evidenceDirectory, "F03Acceptance.dll") },
                    { "units", "mm" },
                    { "nx_started_by_harness", false }
                }
            }
        };
        List<object> cases = (List<object>)evidence["cases"];
        List<object> defects = (List<object>)evidence["defects"];
        Persist(reportPath, evidence);

        Session session = null;
        Part originalPart = null;
        Part testPart = null;
        M2OpenModelAdapter adapter = null;
        M2ModelBinding binding = null;
        string parentDatumId = null;
        string cylinderFeatureId = null;
        string cylinderBodyId = null;
        FaceReference tiltedFace = null;
        OffsetResult primaryDatumOffset = null;
        string supportedSketchId = null;

        try
        {
            session = Session.GetSession();
            originalPart = session.Parts.Work;
            evidence["original_work_part_path"] = originalPart == null ? null : originalPart.FullPath;
            evidence["original_work_part_was_modified"] = originalPart != null && originalPart.IsModified;
            evidence["session_was_in_task_environment"] = session.IsInTaskEnvironment;
            Require(!session.IsInTaskEnvironment, "Acceptance requires an idle NX session outside a task environment.");
            Require(originalPart == null || !originalPart.IsModified,
                "Acceptance requires a saved, unmodified original Work Part.");

            Part created = session.Parts.NewDisplay(sourcePath, Part.Units.Millimeters);
            testPart = created;
            Dictionary<string, object> cylinder = CreateTiltedCylinder(created);
            cylinderFeatureId = RequiredString(cylinder, "feature_id");
            cylinderBodyId = RequiredString(cylinder, "body_id");
            evidence["fixture"] = cylinder;
            SaveInitialFixture(created);
            evidence["source_model_created"] = File.Exists(sourcePath);

            adapter = new M2OpenModelAdapter();
            binding = adapter.Takeover(sourcePath, "saved_independent_copy", "f03-acceptance-session",
                "f03-acceptance-" + runId, 1);
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

            bool parentReady = RunCase(reportPath, evidence, "F03_F02_parent_datum_setup",
                "Create the explicit F02 datum-plane reference that anchors offset associativity and parent-edit validation.",
                new string[] { "Create a unique millimeter part.", "Prepare/execute/verify a fixed datum plane through the production adapter.",
                    "Resolve its native DatumPlane feature and independently record origin and normal." },
                "F02 prerequisite is APPLIED and verified; native datum feature ID is stable; independently read origin and unit normal match the requested oblique frame within 1e-6 mm and 1e-7.",
                delegate(Dictionary<string, object> record)
                {
                    double[] parentOrigin = new double[] { 120.0, 230.0, 340.0 };
                    double[] parentNormal = Normalize(new double[] { 1.0, 2.0, -1.0 });
                    double[] parentXAxis = Normalize(new double[] { 1.0, -1.0, -1.0 });
                    M3DatumPlaneRequest request = new M3DatumPlaneRequest
                    {
                        RevisionId = binding.RevisionId,
                        Mode = "fixed",
                        OriginMm = parentOrigin,
                        Normal = parentNormal,
                        XAxis = parentXAxis
                    };
                    Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, testPart);
                    M3PlanSnapshot plan = adapter.PrepareDatumPlane(binding, request);
                    Dictionary<string, object> after = ModelDefinitionRecord(adapter, binding, testPart);
                    RequireUnchangedPrepare(record, before, after);
                    record["plan"] = PlanRecord(plan);
                    M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
                    record["execution"] = ExecutionRecord(execution);
                    Require(execution != null && execution.State == "APPLIED", "Parent datum-plane create did not return APPLIED.");
                    parentDatumId = RequiredString(execution.Result, "datum_plane_id");
                    string executedFeatureId = RequiredString(execution.Result, "feature_id");
                    record["execution_feature_id"] = executedFeatureId;
                    Require(String.Equals(parentDatumId, executedFeatureId, StringComparison.Ordinal),
                        "Execution result datum_plane_id differs from execution result feature_id.");
                    M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
                    record["verification"] = VerificationRecord(verification);
                    Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
                        "Parent datum-plane production verification did not pass.");
                    Dictionary<string, object> native = ReadNativeDatum(testPart, parentDatumId);
                    record["native_readback"] = native;
                    RequireVectorClose(ReadVector(native, "origin_mm", 3), parentOrigin, PositionToleranceMm,
                        "F02 parent native origin differs from the request.");
                    RequireVectorClose(Normalize(ReadVector(native, "normal_world", 3)), parentNormal,
                        DirectionTolerance, "F02 parent native normal differs from the request.");
                });

            if (parentReady)
            {
                RunCase(reportPath, evidence, "F03_explicit_tilted_planar_face_fixture",
                    "Identify exactly one tilted planar face by its explicit face JournalIdentifier.",
                    new string[] { "Query all planar faces on the tilted solid-cylinder fixture.",
                        "Select the positive cylinder cap by normalized native normal and query result ID.",
                        "Re-resolve the exact face ID and independently classify it with UF AskFaceData." },
                    "A single planar cap is selected; its ID is nonempty and exact; UF classification is planar; its unit normal has all components materially nonzero; the recorded owner body and cylinder feature are the fixture IDs.",
                    delegate(Dictionary<string, object> record)
                    {
                        Dictionary<string, object> query = adapter.QueryFaces(binding, binding.RevisionId,
                            "all_planar", cylinderBodyId);
                        record["planar_face_query"] = query;
                        List<object> candidates = ReadList(query, "candidates");
                        FaceReference selected = SelectTiltedCap(candidates);
                        string selectedId = selected.FaceId;
                        double[] queryNormal = Copy(selected.Normal);
                        Face face = FindFace(testPart, cylinderBodyId, selectedId);
                        int ufType;
                        double[] point, normal;
                        ReadFaceData(face, out ufType, out point, out normal);
                        Require(ufType == UFConstants.UF_MODL_PLANAR_FACE, "Explicit reference face is not planar according to UF.");
                        Require(MaxAbs(Normalize(normal)) < 0.95, "Explicit face normal is not tilted away from principal axes.");
                        RequireVectorClose(Normalize(normal), queryNormal, DirectionTolerance,
                            "Independent UF face normal differs from the production query normal.");
                        tiltedFace = new FaceReference
                        {
                            FaceId = face.JournalIdentifier,
                            BodyId = ((Body)face.GetBody()).JournalIdentifier,
                            BodyFeatureId = cylinderFeatureId,
                            Point = point,
                            Normal = Normalize(normal),
                            UfType = ufType
                        };
                        record["explicit_face_reference"] = FaceRecord(tiltedFace);
                        Require(tiltedFace.BodyId == cylinderBodyId,
                            "Explicit tilted face does not belong to the fixture body.");
                    });

                if (tiltedFace != null)
                {
                    double[] distances = new double[] { 12.5, -7.25, 0.0 };
                    string[] kinds = new string[] { "datum_plane", "face" };
                    foreach (string kind in kinds)
                    {
                        foreach (double distance in distances)
                        {
                            string caseName = "F03_" + kind + "_offset_" + DistanceName(distance);
                            string referenceId = kind == "datum_plane" ? parentDatumId : tiltedFace.FaceId;
                            string expectedParentFeatureId = kind == "datum_plane" ? parentDatumId : tiltedFace.BodyFeatureId;
                            FaceReference faceReference = kind == "face" ? tiltedFace : null;
                            double[] referencePoint = kind == "datum_plane"
                                ? ReadVector(ReadNativeDatum(testPart, parentDatumId), "origin_mm", 3)
                                : Copy(tiltedFace.Point);
                            double[] referenceNormal = kind == "datum_plane"
                                ? Normalize(ReadVector(ReadNativeDatum(testPart, parentDatumId), "normal_world", 3))
                                : Copy(tiltedFace.Normal);
                            OffsetResult made = null;
                            bool passed = RunCase(reportPath, evidence, caseName,
                                "Apply a signed offset from the explicit " + kind + " reference and independently verify native position, normal, method, expression, and association.",
                                new string[] { "Use the exact reference ID " + referenceId + ".",
                                    "Prepare distance_mm=" + distance.ToString("R", CultureInfo.InvariantCulture) + " on the production adapter.",
                                    "Confirm prepare did not alter the part, then execute/verify and read the native DatumPlaneBuilder Plane, Geometry, and feature parents." },
                                "Execution is APPLIED and production verification passes; direct native signed distance and normal meet 1e-6 mm/1e-7; Plane.Method is Distance; Plane.Expression.Value (converted to mm without changing sign) equals the signed request within 1e-6 mm; Plane.GetGeometry resolves to the exact requested native reference; native feature parents/ancestry reach the referenced datum feature or face owner.",
                                delegate(Dictionary<string, object> record)
                                {
                                    M3DatumPlaneRequest request = OffsetRequest(binding.RevisionId, distance,
                                        kind == "datum_plane" ? parentDatumId : null,
                                        kind == "face" ? tiltedFace.FaceId : null);
                                    record["request"] = RequestRecord(request);
                                    Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, testPart);
                                    M3PlanSnapshot plan = adapter.PrepareDatumPlane(binding, request);
                                    Dictionary<string, object> after = ModelDefinitionRecord(adapter, binding, testPart);
                                    RequireUnchangedPrepare(record, before, after);
                                    record["plan"] = PlanRecord(plan);
                                    RequirePlanReference(plan, kind, referenceId, distance);
                                    M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
                                    record["execution"] = ExecutionRecord(execution);
                                    Require(execution != null && execution.State == "APPLIED",
                                        "Signed offset execution did not return APPLIED.");
                                    string newId = RequiredString(execution.Result, "datum_plane_id");
                                    Require(!String.Equals(newId, referenceId, StringComparison.Ordinal),
                                        "Offset feature reused the reference identifier.");
                                    M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
                                    record["verification"] = VerificationRecord(verification);
                                    Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
                                        "Production verification did not pass for signed offset.");
                                    Dictionary<string, object> native = ReadNativeOffset(testPart, newId);
                                    record["native_readback"] = native;
                                    RequireOffsetReadback(native, testPart, newId, kind, referenceId,
                                        expectedParentFeatureId, referencePoint, referenceNormal, distance, faceReference);
                                    made = new OffsetResult
                                    {
                                        CaseName = caseName, FeatureId = newId, Plan = plan,
                                        DistanceMm = distance, ReferenceKind = kind,
                                        ReferenceId = referenceId, Native = native
                                    };
                                });
                            if (made != null)
                            {
                                if (kind == "datum_plane" && Math.Abs(distance - 12.5) < 1e-12)
                                {
                                    primaryDatumOffset = made;
                                }
                            }
                            if (!passed) evidence["offset_case_failure_seen"] = true;
                        }
                    }

                    RunRejectedPrepare(reportPath, evidence, "F03_invalid_unknown_reference_rejected",
                        "Reject an unknown reference ID without changing the model.", binding, adapter, testPart,
                        OffsetRequest(binding.RevisionId, 2.0, "NX-JOURNAL-ID-DOES-NOT-EXIST", null));

                    Face nonPlanar = FindNonPlanarFace(testPart, cylinderBodyId);
                    Require(nonPlanar != null, "Tilted-cylinder fixture has no native nonplanar face for rejection coverage.");
                    RunRejectedPrepare(reportPath, evidence, "F03_nonplanar_face_reference_rejected",
                        "Reject a native cylindrical face ID as an offset plane reference.", binding, adapter, testPart,
                        OffsetRequest(binding.RevisionId, 2.0, null, nonPlanar.JournalIdentifier));

                    RunRejectedPrepare(reportPath, evidence, "F03_ambiguous_dual_reference_rejected",
                        "Reject a request that supplies both valid datum-plane and face IDs.", binding, adapter, testPart,
                        OffsetRequest(binding.RevisionId, 2.0, parentDatumId, tiltedFace.FaceId));

                    if (primaryDatumOffset != null)
                    {
                        RunCase(reportPath, evidence, "F03_offset_supported_sketch_association",
                            "Create a sketch supported by the F03 offset datum plane and verify the native support dependency.",
                            new string[] { "Use the generated +12.5 mm datum-referenced offset feature ID.",
                                "Prepare and execute a sketch with SupportDatumPlaneId set to that exact ID.",
                                "Read Sketch.Feature.GetParents() and the adapter sketch inspection result." },
                            "Sketch is APPLIED and verified; InspectSketch identifies the same datum support ID; native sketch parent ancestry reaches the generated offset feature.",
                            delegate(Dictionary<string, object> record)
                            {
                                Dictionary<string, object> offsetNative = ReadNativeDatum(testPart, primaryDatumOffset.FeatureId);
                                double[] xAxis = ReadVector(offsetNative, "x_axis_world", 3);
                                List<M3SketchGeometryRequest> geometries = new List<M3SketchGeometryRequest>
                                {
                                    LineGeometry("f03-support-line", 1.0, 2.0, 9.0, 2.0)
                                };
                                M3SketchRequest request = new M3SketchRequest
                                {
                                    RevisionId = binding.RevisionId,
                                    SupportDatumPlaneId = primaryDatumOffset.FeatureId,
                                    XAxis = xAxis,
                                    Geometries = geometries
                                };
                                Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, testPart);
                                M3PlanSnapshot plan = adapter.PrepareSketch(binding, request);
                                Dictionary<string, object> after = ModelDefinitionRecord(adapter, binding, testPart);
                                RequireUnchangedPrepare(record, before, after);
                                record["plan"] = PlanRecord(plan);
                                M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
                                record["execution"] = ExecutionRecord(execution);
                                Require(execution != null && execution.State == "APPLIED", "Offset-supported sketch did not return APPLIED.");
                                M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
                                record["verification"] = VerificationRecord(verification);
                                Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
                                    "Offset-supported sketch did not verify.");
                                supportedSketchId = ReadOptionalString(execution.Result, "sketch_id");
                                if (String.IsNullOrWhiteSpace(supportedSketchId)) supportedSketchId = plan.SketchId;
                                Require(!String.IsNullOrWhiteSpace(supportedSketchId), "Sketch result omitted sketch_id.");
                                Sketch sketch = FindSketch(testPart, supportedSketchId);
                                record["sketch_feature_id"] = sketch.Feature.JournalIdentifier;
                                record["sketch_feature_parent_ids"] = FeatureAncestorIds(sketch.Feature);
                                Require(FeatureAncestorIds(sketch.Feature).Contains(primaryDatumOffset.FeatureId),
                                    "Native sketch feature ancestry does not reach the requested offset datum plane.");
                                Dictionary<string, object> inspect = adapter.InspectSketch(binding, supportedSketchId);
                                record["inspect_sketch"] = inspect;
                                RequireAnyDictionaryPropertyValue(inspect,
                                    new string[] { "datum_plane_id", "support_datum_plane_id" }, primaryDatumOffset.FeatureId);
                            });

                        RunCase(reportPath, evidence, "F03_saved_copy_reopen_and_support_roundtrip",
                            "Save and reopen a separate copy, then independently recheck the offset definition and supported-sketch association.",
                            new string[] { "Call production SaveCopy with a fresh workspace/v2/models target and reopen=true.",
                                "Re-resolve the offset feature and sketch in the reopened Work Part.",
                                "Read Plane.Method, Plane.Expression.Value, Geometry, DatumPlane origin/normal, and sketch parent ancestry directly from NXOpen." },
                            "SaveCopy returns SAVED and reopened=true; every production roundtrip check is VERIFIED; the offset feature retains its explicit parent association, signed expression, origin/normal, and the sketch retains the offset feature in native parent ancestry.",
                            delegate(Dictionary<string, object> record)
                            {
                                M2SaveCopyResult saved = adapter.SaveCopy(binding, savedPath, true);
                                record["save_copy"] = SaveCopyRecord(saved);
                                Require(saved != null && saved.State == "SAVED" && saved.Reopened,
                                    "Production SaveCopy did not save and reopen the unique target.");
                                Require(ChecksPass(saved.Checks), "SaveCopy roundtrip checks did not all report VERIFIED.");
                                Require(String.Equals(Path.GetFullPath(savedPath), Path.GetFullPath(saved.PartPath), StringComparison.OrdinalIgnoreCase),
                                    "SaveCopy returned a different target path.");
                                testPart = FindOwnedPart(session, savedPath);
                                Require(testPart != null, "Reopened roundtrip copy is not loaded in the NX session.");
                                Dictionary<string, object> native = ReadNativeOffset(testPart,
                                    primaryDatumOffset.FeatureId);
                                record["reopened_offset_native_readback"] = native;
                                RequireOffsetReadback(native, testPart, primaryDatumOffset.FeatureId, "datum_plane", parentDatumId,
                                    parentDatumId, ReadVector(ReadNativeDatum(testPart, parentDatumId), "origin_mm", 3),
                                    Normalize(ReadVector(ReadNativeDatum(testPart, parentDatumId), "normal_world", 3)),
                                    primaryDatumOffset.DistanceMm, null);
                                Sketch sketch = FindSketch(testPart, supportedSketchId);
                                List<string> parents = FeatureAncestorIds(sketch.Feature);
                                record["reopened_sketch_parent_feature_ids"] = parents;
                                Require(parents.Contains(primaryDatumOffset.FeatureId),
                                    "The reopened sketch no longer depends on the F03 offset datum feature.");
                                Dictionary<string, object> inspect = adapter.InspectSketch(binding, supportedSketchId);
                                record["reopened_inspect_sketch"] = inspect;
                                RequireAnyDictionaryPropertyValue(inspect,
                                    new string[] { "datum_plane_id", "support_datum_plane_id" }, primaryDatumOffset.FeatureId);
                                evidence["active_test_part_path_after_reopen"] = testPart.FullPath;
                            });

                        RunParentEditRevisionUndoCase(reportPath, evidence, session, adapter, binding,
                            testPart, parentDatumId, primaryDatumOffset);
                    }
                    else
                    {
                        RecordSkipped(reportPath, evidence, "F03_offset_supported_sketch_association",
                            "No +12.5 mm datum-plane offset feature was created, so dependent sketch and roundtrip checks cannot run.");
                        RecordSkipped(reportPath, evidence, "F03_saved_copy_reopen_and_support_roundtrip",
                            "No +12.5 mm datum-plane offset feature was created, so roundtrip checks cannot run.");
                        RecordSkipped(reportPath, evidence, "F03_parent_edit_revision_invalidation_and_undo",
                            "No +12.5 mm datum-plane offset feature was created, so parent edit checks cannot run.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            evidence["error_case"] = evidence.ContainsKey("current_case") ? evidence["current_case"] : "setup_or_harness";
            evidence["error"] = ex.ToString();
            evidence["passed"] = false;
            if (!evidence.ContainsKey("current_case"))
                AddDefect(defects, "setup_or_harness",
                    new string[] { "Compile production M2 assemblies and the F03 acceptance harness.",
                        "Run the acceptance journal in an idle NX session.", "Review the top-level setup/harness exception in the report." },
                    ex.ToString(), "Correct the reported setup or harness fault and rerun the full acceptance on a new unique model path.");
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
                Dictionary<string, object> one = item as Dictionary<string, object>;
                if (one == null || !String.Equals(ReadOptionalString(one, "status"), "PASSED", StringComparison.Ordinal))
                    allCasesPassed = false;
            }
            bool sessionRestored = originalPart == null
                ? evidence.ContainsKey("empty_original_session_restored") && Convert.ToBoolean(evidence["empty_original_session_restored"], CultureInfo.InvariantCulture)
                : evidence.ContainsKey("original_part_restored") && Convert.ToBoolean(evidence["original_part_restored"], CultureInfo.InvariantCulture);
            bool noErrors = !evidence.ContainsKey("error") && !evidence.ContainsKey("restore_errors") && !evidence.ContainsKey("release_error");
            evidence["passed"] = allCasesPassed && sessionRestored && noErrors;
            Persist(reportPath, evidence);
        }
    }

    private static bool RunCase(string reportPath, Dictionary<string, object> evidence, string name,
        string objective, string[] reproduction, string retest, CaseBody body)
    {
        List<object> cases = (List<object>)evidence["cases"];
        List<object> defects = (List<object>)evidence["defects"];
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "objective", objective }, { "status", "RUNNING" },
            { "reproduction_steps", reproduction }, { "retest_criterion", retest }, { "started_utc", UtcNow() }
        };
        cases.Add(record);
        evidence["current_case"] = name;
        Persist(reportPath, evidence);
        try
        {
            body(record);
            record["status"] = "PASSED";
            record["finished_utc"] = UtcNow();
            evidence.Remove("current_case");
            Persist(reportPath, evidence);
            return true;
        }
        catch (Exception ex)
        {
            record["status"] = "FAILED";
            record["observed_exception"] = ex.ToString();
            record["finished_utc"] = UtcNow();
            AddDefect(defects, name, reproduction, ex.ToString(), retest);
            evidence["passed"] = false;
            evidence.Remove("current_case");
            Persist(reportPath, evidence);
            return false;
        }
    }

    private static void RecordSkipped(string reportPath, Dictionary<string, object> evidence, string name, string reason)
    {
        List<object> cases = (List<object>)evidence["cases"];
        cases.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "objective", "Dependent F03 acceptance" }, { "status", "NOT_RUN" },
            { "skip_reason", reason }, { "reproduction_steps", new string[] { reason } },
            { "retest_criterion", "Correct the prerequisite failure and rerun the complete acceptance on a new unique model path." },
            { "finished_utc", UtcNow() }
        });
        Persist(reportPath, evidence);
    }

    private static void RunRejectedPrepare(string reportPath, Dictionary<string, object> evidence,
        string name, string objective, M2ModelBinding binding, M2OpenModelAdapter adapter,
        Part part, M3DatumPlaneRequest request)
    {
        RunCase(reportPath, evidence, name, objective,
            new string[] { "Submit the recorded invalid F03 request to PrepareDatumPlane.",
                "Capture the adapter error, then compare the native IDs, part definition, modified state, and revision before/after." },
            "Prepare rejects the request and the full native/adapter snapshot and binding revision are unchanged; after fixing validation, rerun all F03 acceptance cases on a new unique model path.",
            delegate(Dictionary<string, object> record)
            {
                record["request"] = RequestRecord(request);
                Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, part);
                Exception rejected = null;
                M3PlanSnapshot unexpected = null;
                try { unexpected = adapter.PrepareDatumPlane(binding, request); }
                catch (Exception ex) { rejected = ex; }
                record["rejection_exception"] = rejected == null ? null : ExceptionRecord(rejected);
                if (unexpected != null) record["unexpected_plan"] = PlanRecord(unexpected);
                Dictionary<string, object> after = ModelDefinitionRecord(adapter, binding, part);
                record["before"] = before;
                record["after"] = after;
                Require(rejected != null, "Invalid F03 reference was not rejected during prepare.");
                Require(String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal),
                    "Rejected F03 prepare changed the model definition, IDs, modified state, or revision.");
            });
    }

    private static void RunParentEditRevisionUndoCase(string reportPath, Dictionary<string, object> evidence,
        Session session, M2OpenModelAdapter adapter, M2ModelBinding binding, Part part,
        string parentDatumId, OffsetResult appliedOffset)
    {
        if (String.IsNullOrWhiteSpace(parentDatumId) || appliedOffset == null || String.IsNullOrWhiteSpace(appliedOffset.FeatureId))
        {
            RecordSkipped(reportPath, evidence, "F03_parent_edit_revision_invalidation_and_undo",
                "The applied datum-referenced child offset is unavailable.");
            return;
        }

        RunCase(reportPath, evidence, "F03_parent_edit_revision_invalidation_and_undo",
            "Edit the parent datum plane, prove the associative child follows it, invalidate stale state, and undo back to a verifiable model.",
            new string[] { "Prepare but do not apply a second offset plan from the parent datum plane.",
                "Retain the verified +12.5 mm child offset plan.",
                "Move the parent plane 5 mm along its own native normal inside one NX undo mark and update.",
                "Read parent/child geometry and association directly, test the stale plan and old verification, then undo and recheck." },
            "The child origin follows the measured parent delta within 1e-6 mm, keeps its signed expression and native association, and retains its normal within 1e-7. The revision changes; stale execution is UNCHANGED and re-verification of the old applied plan fails. Undo restores parent/child frames, association, and old-plan verification.",
            delegate(Dictionary<string, object> record)
            {
                M3DatumPlaneRequest freshChildRequest = OffsetRequest(binding.RevisionId,
                    appliedOffset.DistanceMm, parentDatumId, null);
                Dictionary<string, object> beforeChildPrepare = ModelDefinitionRecord(adapter, binding, part);
                M3PlanSnapshot freshChildPlan = adapter.PrepareDatumPlane(binding, freshChildRequest);
                Dictionary<string, object> afterChildPrepare = ModelDefinitionRecord(adapter, binding, part);
                RequireUnchangedPrepare(record, beforeChildPrepare, afterChildPrepare);
                record["fresh_child_plan"] = PlanRecord(freshChildPlan);
                M3ExecutionResult freshChildExecution = adapter.ExecutePlan(binding, freshChildPlan);
                record["fresh_child_execution"] = ExecutionRecord(freshChildExecution);
                Require(freshChildExecution != null && freshChildExecution.State == "APPLIED",
                    "Fresh post-reopen parent-child offset did not return APPLIED.");
                string freshChildId = RequiredString(freshChildExecution.Result, "datum_plane_id");
                M3VerificationResult freshChildVerification = adapter.VerifyPlan(binding, freshChildPlan);
                record["fresh_child_verification"] = VerificationRecord(freshChildVerification);
                Require(freshChildVerification != null && freshChildVerification.Verified && ChecksPass(freshChildVerification.Checks),
                    "Fresh post-reopen parent-child offset did not verify.");
                Dictionary<string, object> freshChildNative = ReadNativeOffset(part, freshChildId);
                RequireOffsetReadback(freshChildNative, part, freshChildId, "datum_plane", parentDatumId,
                    parentDatumId, ReadVector(ReadNativeDatum(part, parentDatumId), "origin_mm", 3),
                    Normalize(ReadVector(ReadNativeDatum(part, parentDatumId), "normal_world", 3)),
                    appliedOffset.DistanceMm, null);

                M3DatumPlaneRequest staleRequest = OffsetRequest(binding.RevisionId, -3.5, parentDatumId, null);
                Dictionary<string, object> beforePrepare = ModelDefinitionRecord(adapter, binding, part);
                M3PlanSnapshot stalePlan = adapter.PrepareDatumPlane(binding, staleRequest);
                Dictionary<string, object> afterPrepare = ModelDefinitionRecord(adapter, binding, part);
                RequireUnchangedPrepare(record, beforePrepare, afterPrepare);
                record["stale_plan"] = PlanRecord(stalePlan);

                M3VerificationResult baseline = adapter.VerifyPlan(binding, freshChildPlan);
                record["baseline_applied_plan_verification"] = VerificationRecord(baseline);
                Require(baseline != null && baseline.Verified && ChecksPass(baseline.Checks),
                    "Applied parent-child offset did not verify before parent edit.");

                Dictionary<string, object> parentBefore = ReadNativeDatum(part, parentDatumId);
                Dictionary<string, object> childBefore = ReadNativeOffset(part, freshChildId);
                string revisionBefore = binding.RevisionId;
                record["revision_before_edit"] = revisionBefore;
                record["parent_before_edit"] = parentBefore;
                record["child_before_edit"] = childBefore;
                Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible,
                    "F03 acceptance parent datum edit revision test");
                bool markActive = true;
                try
                {
                    Dictionary<string, object> parentAfter = MoveDatumPlaneAlongNormal(part, parentDatumId, ParentMoveMm, mark, session);
                    adapter.InspectPart(binding);
                    Dictionary<string, object> childAfter = ReadNativeOffset(part, freshChildId);
                    record["parent_after_edit"] = parentAfter;
                    record["child_after_edit"] = childAfter;
                    record["revision_after_edit"] = binding.RevisionId;
                    Require(!String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal),
                        "Parent edit did not advance the adapter's model revision.");
                    double[] beforeParentOrigin = ReadVector(parentBefore, "origin_mm", 3);
                    double[] afterParentOrigin = ReadVector(parentAfter, "origin_mm", 3);
                    double[] parentDelta = Subtract(afterParentOrigin, beforeParentOrigin);
                    double[] beforeChildOrigin = ReadVector(childBefore, "origin_mm", 3);
                    double[] afterChildOrigin = ReadVector(childAfter, "origin_mm", 3);
                    double[] childDelta = Subtract(afterChildOrigin, beforeChildOrigin);
                    record["parent_delta_mm"] = parentDelta;
                    record["child_delta_mm"] = childDelta;
                    RequireVectorClose(childDelta, parentDelta, PositionToleranceMm,
                        "Associative offset child did not follow the parent edit by the same world displacement.");
                    Require(Math.Abs(NativeExpressionValue(childAfter) - appliedOffset.DistanceMm) <= PositionToleranceMm,
                        "Parent edit changed the child's signed Plane.Expression value.");
                    double childSpacingAfterEdit = Dot(Subtract(ReadVector(childAfter, "origin_mm", 3),
                        ReadVector(parentAfter, "origin_mm", 3)), Normalize(ReadVector(parentAfter, "normal_world", 3)));
                    record["child_signed_distance_after_edit_mm"] = childSpacingAfterEdit;
                    Require(Math.Abs(childSpacingAfterEdit - appliedOffset.DistanceMm) <= PositionToleranceMm,
                        "Child did not preserve its signed distance from the moved parent datum plane.");
                    RequireVectorClose(Normalize(ReadVector(childAfter, "normal_world", 3)),
                        Normalize(ReadVector(parentAfter, "normal_world", 3)), DirectionTolerance,
                        "Parent edit changed the child's reference normal.");
                    RequireNativeAssociation(childAfter, parentDatumId, parentDatumId, "datum_plane");

                    M3ExecutionResult staleExecution = adapter.ExecutePlan(binding, stalePlan);
                    record["stale_plan_execution_after_edit"] = ExecutionRecord(staleExecution);
                    Require(staleExecution != null && staleExecution.State == "UNCHANGED",
                        "Old offset plan did not return UNCHANGED after the parent model revision changed.");
                    bool staleVerifyPassed = false;
                    try
                    {
                        M3VerificationResult changed = adapter.VerifyPlan(binding, freshChildPlan);
                        record["old_applied_plan_verification_after_edit"] = VerificationRecord(changed);
                        staleVerifyPassed = changed != null && changed.Verified;
                    }
                    catch (Exception ex)
                    {
                        record["old_applied_plan_verification_exception_after_edit"] = ExceptionRecord(ex);
                    }
                    Require(!staleVerifyPassed,
                        "Old applied offset plan continued to verify after its native reference changed.");
                }
                finally
                {
                    if (markActive)
                    {
                        session.UndoToMark(mark, "F03 acceptance parent datum edit revision test");
                        session.DeleteUndoMark(mark, "F03 acceptance parent datum edit revision test");
                        markActive = false;
                    }
                }

                Dictionary<string, object> parentRestored = ReadNativeDatum(part, parentDatumId);
                adapter.InspectPart(binding);
                Dictionary<string, object> childRestored = ReadNativeOffset(part, freshChildId);
                record["revision_after_undo"] = binding.RevisionId;
                record["parent_after_undo"] = parentRestored;
                record["child_after_undo"] = childRestored;
                RequireVectorClose(ReadVector(parentRestored, "origin_mm", 3),
                    ReadVector(parentBefore, "origin_mm", 3), PositionToleranceMm,
                    "Undo did not restore the native parent datum origin.");
                RequireVectorClose(ReadVector(childRestored, "origin_mm", 3),
                    ReadVector(childBefore, "origin_mm", 3), PositionToleranceMm,
                    "Undo did not restore the native offset child origin.");
                RequireVectorClose(Normalize(ReadVector(childRestored, "normal_world", 3)),
                    Normalize(ReadVector(childBefore, "normal_world", 3)), DirectionTolerance,
                    "Undo did not restore the native offset child normal.");
                Require(Math.Abs(NativeExpressionValue(childRestored) - appliedOffset.DistanceMm) <= PositionToleranceMm,
                    "Undo did not restore the child's signed Plane.Expression value.");
                RequireNativeAssociation(childRestored, parentDatumId, parentDatumId, "datum_plane");
                M3VerificationResult restored = adapter.VerifyPlan(binding, freshChildPlan);
                record["old_applied_plan_verification_after_undo"] = VerificationRecord(restored);
                Require(restored != null && restored.Verified && ChecksPass(restored.Checks),
                    "Previously applied offset plan did not verify again after undo restored its parent.");
            });
    }

    private static M3DatumPlaneRequest OffsetRequest(string revisionId, double distance,
        string supportDatumId, string supportFaceId)
    {
        return new M3DatumPlaneRequest
        {
            RevisionId = revisionId,
            Mode = "offset",
            DistanceMm = distance,
            SupportDatumPlaneId = supportDatumId,
            SupportFaceId = supportFaceId
        };
    }

    private static M3SketchGeometryRequest LineGeometry(string id, double x1, double y1, double x2, double y2)
    {
        return new M3SketchGeometryRequest
        {
            Id = id, Type = "line", StartMm = new double[] { x1, y1 }, EndMm = new double[] { x2, y2 }
        };
    }

    private static Dictionary<string, object> RequestRecord(M3DatumPlaneRequest request)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", request.RevisionId }, { "mode", "offset" },
            { "distance_mm", request.DistanceMm }, { "support_datum_plane_id", request.SupportDatumPlaneId },
            { "support_face_id", request.SupportFaceId }
        };
    }

    private static void RequirePlanReference(M3PlanSnapshot plan, string kind, string id, double distance)
    {
        Require(plan != null, "PrepareDatumPlane returned no F03 plan.");
        Require(plan.PublicView != null, "F03 plan omitted its public view.");
        RequireDictionaryContains(plan.PublicView, "mode", "offset");
        RequireDictionaryContains(plan.PublicView, "support_kind", kind == "datum_plane" ? "DATUM_PLANE" : "FACE");
        RequireDictionaryContains(plan.PublicView, "distance_mm", distance);
        RequireDictionaryContains(plan.PublicView, kind == "datum_plane" ? "support_datum_plane_id" : "support_face_id", id);
    }

    private static Dictionary<string, object> ReadNativeDatum(Part part, string featureId)
    {
        Feature feature = FindFeature(part, featureId);
        DatumPlane datum = FindDatumEntity(feature);
        Point3d p0, p1, p2, p3;
        datum.GetCornerPoints(out p0, out p1, out p2, out p3);
        double[] xAxis = Normalize(Subtract(Point(p1), Point(p0)));
        double[] yAxis = Normalize(Subtract(Point(p3), Point(p0)));
        Vector3d normal = datum.Normal;
        Point3d origin = datum.Origin;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "feature_id", feature.JournalIdentifier },
            { "datum_entity_id", datum.JournalIdentifier },
            { "datum_entity_feature_id", datum.Feature == null ? null : datum.Feature.JournalIdentifier },
            { "entity_type", datum.GetType().FullName },
            { "origin_mm", Point(origin) },
            { "normal_world", new double[] { normal.X, normal.Y, normal.Z } },
            { "x_axis_world", xAxis }, { "y_axis_world", yAxis },
            { "corner_points_world_mm", new List<object> { Point(p0), Point(p1), Point(p2), Point(p3) } },
            { "feature_parent_ids", FeatureIds(feature.GetParents()) },
            { "feature_ancestor_ids", FeatureAncestorIds(feature) },
            { "feature_child_ids", FeatureIds(feature.GetChildren()) }
        };
    }

    private static Dictionary<string, object> ReadNativeOffset(Part part, string featureId)
    {
        Feature feature = FindFeature(part, featureId);
        DatumPlane datum = FindDatumEntity(feature);
        DatumPlaneBuilder builder = part.Features.CreateDatumPlaneBuilder(feature);
        Plane definingPlane;
        try
        {
            definingPlane = builder.GetPlane();
            Require(definingPlane != null, "DatumPlaneBuilder.GetPlane returned null for the offset feature.");
            NXObject[] geometry = definingPlane.GetGeometry();
            List<string> geometryIds = new List<string>();
            List<string> geometryTypes = new List<string>();
            List<string> geometryOwnerFeatureIds = new List<string>();
            if (geometry != null)
            {
                foreach (NXObject item in geometry)
                {
                    if (item == null) continue;
                    geometryIds.Add(item.JournalIdentifier);
                    geometryTypes.Add(item.GetType().FullName);
                    Feature owner = NativeOwnerFeature(item);
                    if (owner != null) geometryOwnerFeatureIds.Add(owner.JournalIdentifier);
                }
            }
            geometryIds.Sort(StringComparer.Ordinal);
            geometryTypes.Sort(StringComparer.Ordinal);
            geometryOwnerFeatureIds.Sort(StringComparer.Ordinal);
            Point3d p0, p1, p2, p3;
            datum.GetCornerPoints(out p0, out p1, out p2, out p3);
            Vector3d normal = datum.Normal;
            Point3d origin = datum.Origin;
            Expression planeExpression = definingPlane.Expression;
            Require(planeExpression != null, "NX native associative Plane has no Plane.Expression.");
            Require(planeExpression.Units != null, "NX native Plane.Expression has no units.");
            Unit millimeter = part.UnitCollection.FindObject("MilliMeter");
            Require(millimeter != null, "NX millimeter unit is unavailable for Plane.Expression readback.");
            double expressionValueMm = part.UnitCollection.Convert(planeExpression.Units, millimeter, planeExpression.Value);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "feature_id", feature.JournalIdentifier },
                { "datum_entity_id", datum.JournalIdentifier },
                { "origin_mm", Point(origin) },
                { "normal_world", new double[] { normal.X, normal.Y, normal.Z } },
                { "x_axis_world", Normalize(Subtract(Point(p1), Point(p0))) },
                { "y_axis_world", Normalize(Subtract(Point(p3), Point(p0))) },
                { "feature_parent_ids", FeatureIds(feature.GetParents()) },
                { "feature_ancestor_ids", FeatureAncestorIds(feature) },
                { "plane_method", definingPlane.Method.ToString() },
                { "native_expression_api", "Plane.Expression" },
                { "native_expression_value_raw", planeExpression.Value },
                { "native_expression_value_mm", expressionValueMm },
                { "native_expression_right_hand_side", planeExpression.RightHandSide },
                { "native_expression_units", planeExpression.Units.ToString() },
                { "native_plane_flip", definingPlane.Flip },
                { "native_plane_reverse_side", definingPlane.GetReverseSide() },
                // Keep the prior report keys as aliases; they now contain the
                // generic Plane.Expression readback, not OffsetExpression.
                { "offset_expression_value_mm", expressionValueMm },
                { "offset_expression_right_hand_side", planeExpression.RightHandSide },
                { "offset_expression_units", planeExpression.Units.ToString() },
                { "plane_origin_mm", Point(definingPlane.Origin) },
                { "plane_normal_world", new double[] { definingPlane.Normal.X, definingPlane.Normal.Y, definingPlane.Normal.Z } },
                { "geometry_ids", geometryIds }, { "geometry_types", geometryTypes },
                { "geometry_owner_feature_ids", geometryOwnerFeatureIds }
            };
        }
        finally { builder.Destroy(); }
    }

    private static void RequireOffsetReadback(Dictionary<string, object> native, Part part, string featureId,
        string referenceKind, string referenceId, string expectedParentFeatureId,
        double[] referencePoint, double[] referenceNormal, double distance,
        FaceReference faceReference)
    {
        if (referenceKind == "datum_plane")
        {
            Dictionary<string, object> reference = ReadNativeDatum(part, referenceId);
            RequireVectorClose(ReadVector(reference, "origin_mm", 3), referencePoint, PositionToleranceMm,
                "Direct native parent datum origin changed during offset verification.");
            RequireVectorClose(Normalize(ReadVector(reference, "normal_world", 3)), Normalize(referenceNormal),
                DirectionTolerance, "Direct native parent datum normal changed during offset verification.");
        }
        else
        {
            Face referenceFace = FindFaceById(part, referenceId);
            int faceType;
            double[] directPoint, directNormal;
            ReadFaceData(referenceFace, out faceType, out directPoint, out directNormal);
            Require(faceType == UFConstants.UF_MODL_PLANAR_FACE, "Direct native reference Face is no longer planar.");
            RequireVectorClose(directPoint, referencePoint, PositionToleranceMm,
                "Direct native reference face plane point differs from the captured reference.");
            RequireVectorClose(Normalize(directNormal), Normalize(referenceNormal), DirectionTolerance,
                "Direct native reference face normal differs from the captured reference.");
        }
        Require(String.Equals(ReadRequiredString(native, "feature_id"), featureId, StringComparison.Ordinal),
            "Native offset feature identifier differs from the requested feature ID.");
        double[] childOrigin = ReadVector(native, "origin_mm", 3);
        if (referenceKind == "datum_plane")
        {
            double[] expectedOrigin = Add(referencePoint, Scale(referenceNormal, distance));
            RequireVectorClose(childOrigin, expectedOrigin, PositionToleranceMm,
                "Native offset DatumPlane origin does not equal reference point + signed offset × reference normal.");
        }
        RequireVectorClose(Normalize(ReadVector(native, "normal_world", 3)), Normalize(referenceNormal),
            DirectionTolerance, "Native offset DatumPlane normal differs from its native reference normal.");
        Require(String.Equals(ReadRequiredString(native, "plane_method"), PlaneTypes.MethodType.Distance.ToString(), StringComparison.Ordinal),
            "Native Plane.Method is not Distance.");
        double signedDefinitionValue = NativeExpressionValue(native);
        double geometricSignedDistance = Dot(Subtract(childOrigin, referencePoint), Normalize(referenceNormal));
        native["native_expression_signed_value_mm"] = signedDefinitionValue;
        native["geometry_signed_distance_mm"] = geometricSignedDistance;
        Require(Math.Abs(signedDefinitionValue - distance) <= PositionToleranceMm,
            "Native Plane.Expression.Value does not preserve the requested signed offset.");
        Require(Math.Abs(geometricSignedDistance - distance) <= PositionToleranceMm,
            "Native child geometry signed projection does not equal the requested distance.");
        RequireNativeAssociation(native, referenceId, expectedParentFeatureId, referenceKind);
        if (referenceKind == "face")
        {
            Require(faceReference != null, "Direct tilted-face reference evidence was not supplied.");
            Require(faceReference.UfType == UFConstants.UF_MODL_PLANAR_FACE,
                "Reference Face is no longer classified as planar by UF.");
            Require(ContainsString(ReadList(native, "geometry_ids"), referenceId),
                "Plane.GetGeometry did not return the exact requested Face JournalIdentifier.");
        }
    }

    private static double NativeExpressionValue(Dictionary<string, object> native)
    {
        return ReadRequiredDouble(native, "native_expression_value_mm");
    }

    private static void RequireNativeAssociation(Dictionary<string, object> native,
        string referenceId, string expectedParentFeatureId, string referenceKind)
    {
        List<object> geometryIds = ReadList(native, "geometry_ids");
        List<object> geometryOwnerIds = ReadList(native, "geometry_owner_feature_ids");
        List<string> parents = ReadStringList(native, "feature_parent_ids");
        List<string> ancestors = ReadStringList(native, "feature_ancestor_ids");
        bool geometryMatches = ContainsString(geometryIds, referenceId) || ContainsString(geometryOwnerIds, referenceId);
        if (referenceKind == "datum_plane")
        {
            geometryMatches = geometryMatches || ContainsString(geometryOwnerIds, expectedParentFeatureId);
        }
        Require(geometryMatches, "Plane.GetGeometry did not independently resolve the requested native reference.");
        Require(parents.Contains(expectedParentFeatureId) || ancestors.Contains(expectedParentFeatureId),
            "Offset feature GetParents ancestry does not reach the expected native reference feature.");
        native["geometry_matches_reference"] = geometryMatches;
        native["parent_ancestry_matches_reference"] = parents.Contains(expectedParentFeatureId) || ancestors.Contains(expectedParentFeatureId);
    }

    private static Dictionary<string, object> MoveDatumPlaneAlongNormal(Part part, string featureId,
        double distanceMm, Session.UndoMarkId mark, Session session)
    {
        Feature feature = FindFeature(part, featureId);
        Dictionary<string, object> before = ReadNativeDatum(part, featureId);
        double[] originArray = ReadVector(before, "origin_mm", 3);
        double[] normal = Normalize(ReadVector(before, "normal_world", 3));
        double[] xAxis = Normalize(ReadVector(before, "x_axis_world", 3));
        double[] yAxis = Normalize(ReadVector(before, "y_axis_world", 3));
        double[] moved = Add(originArray, Scale(normal, distanceMm));
        Matrix3x3 matrix = new Matrix3x3();
        matrix.Xx = xAxis[0]; matrix.Xy = xAxis[1]; matrix.Xz = xAxis[2];
        matrix.Yx = yAxis[0]; matrix.Yy = yAxis[1]; matrix.Yz = yAxis[2];
        matrix.Zx = normal[0]; matrix.Zy = normal[1]; matrix.Zz = normal[2];
        DatumPlaneBuilder builder = part.Features.CreateDatumPlaneBuilder(feature);
        Feature committed;
        try
        {
            Plane plane = builder.GetPlane();
            Require(plane != null, "Parent DatumPlaneBuilder.GetPlane returned null.");
            plane.SetMethod(PlaneTypes.MethodType.Fixed);
            plane.EditFixedPlane(new Point3d(moved[0], moved[1], moved[2]), matrix);
            plane.Evaluate();
            committed = builder.CommitFeature();
        }
        finally { builder.Destroy(); }
        Require(committed != null && String.Equals(committed.JournalIdentifier, featureId, StringComparison.Ordinal),
            "Parent datum edit did not preserve its native feature identifier.");
        int errors = session.UpdateManager.DoUpdate(mark);
        Require(errors == 0, "NX update reported errors after parent datum edit.");
        Dictionary<string, object> after = ReadNativeDatum(part, featureId);
        RequireVectorClose(ReadVector(after, "origin_mm", 3), moved, PositionToleranceMm,
            "Direct native readback does not show the requested parent datum move.");
        after["requested_move_mm"] = distanceMm;
        after["update_error_count"] = errors;
        return after;
    }

    private static Dictionary<string, object> CreateTiltedCylinder(Part part)
    {
        double invRoot3 = 1.0 / Math.Sqrt(3.0);
        double[] origin = new double[] { 500.0, 0.0, 0.0 };
        double[] direction = new double[] { invRoot3, invRoot3, invRoot3 };
        Tag featureTag;
        UFSession.GetUFSession().Modl.CreateCylinder(FeatureSigns.Nullsign, Tag.Null,
            origin, "40", "200", direction, out featureTag);
        Tag bodyTag;
        UFSession.GetUFSession().Modl.AskFeatBody(featureTag, out bodyTag);
        NXObject featureObject = (NXObject)NXOpen.Utilities.NXObjectManager.Get(featureTag);
        Body body = NXOpen.Utilities.NXObjectManager.Get(bodyTag) as Body;
        Require(featureObject != null && body != null && body.IsSolidBody,
            "UF tilted-cylinder fixture did not return a solid Body and native feature.");
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "feature_id", featureObject.JournalIdentifier }, { "body_id", body.JournalIdentifier },
            { "origin_mm", origin }, { "axis_unit", direction }, { "diameter_mm", 200.0 },
            { "length_mm", 40.0 }, { "purpose", "tilted analytic planar face reference for F03" }
        };
    }

    private static FaceReference SelectTiltedCap(List<object> candidates)
    {
        FaceReference selected = null;
        double invRoot3 = 1.0 / Math.Sqrt(3.0);
        double[] expected = new double[] { invRoot3, invRoot3, invRoot3 };
        foreach (object raw in candidates)
        {
            Dictionary<string, object> candidate = raw as Dictionary<string, object>;
            if (candidate == null || !String.Equals(ReadOptionalString(candidate, "surface_type"), "planar", StringComparison.Ordinal)) continue;
            string faceId = ReadOptionalString(candidate, "face_journal_id");
            if (String.IsNullOrWhiteSpace(faceId)) continue;
            double[] queryNormal;
            try { queryNormal = Normalize(ReadVector(candidate, "normal_world", 3)); }
            catch { continue; }
            if (MaxAbs(queryNormal) >= 0.95 || Dot(queryNormal, expected) < 1.0 - 1e-6) continue;
            if (selected != null) throw new InvalidOperationException("Tilted cylinder fixture returned multiple matching planar cap candidates.");
            selected = new FaceReference { FaceId = faceId, Normal = queryNormal };
        }
        Require(selected != null, "Production all_planar query did not find one positive tilted cap.");
        return selected;
    }

    private static Dictionary<string, object> FaceRecord(FaceReference face)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "face_journal_id", face.FaceId }, { "body_journal_id", face.BodyId },
            { "body_feature_id", face.BodyFeatureId }, { "uf_face_type", face.UfType },
            { "plane_point_mm", face.Point }, { "normal_world", face.Normal }
        };
    }

    private static void ReadFaceData(Face face, out int type, out double[] point, out double[] normal)
    {
        type = 0;
        point = new double[3];
        normal = new double[3];
        double[] box = new double[6];
        double radius, radiusData;
        int normalDirection;
        UFSession.GetUFSession().Modl.AskFaceData(face.Tag, out type, point, normal, box,
            out radius, out radiusData, out normalDirection);
    }

    private static Face FindNonPlanarFace(Part part, string bodyId)
    {
        foreach (Body body in part.Bodies.ToArray())
        {
            if (body == null || body.JournalIdentifier != bodyId) continue;
            foreach (Face face in body.GetFaces())
            {
                int type;
                double[] point, normal;
                ReadFaceData(face, out type, out point, out normal);
                if (type != UFConstants.UF_MODL_PLANAR_FACE) return face;
            }
        }
        return null;
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
        throw new InvalidOperationException("Feature JournalIdentifier does not resolve to a native DatumPlane entity: " + feature.JournalIdentifier);
    }

    private static Feature NativeOwnerFeature(NXObject item)
    {
        Feature feature = item as Feature;
        if (feature != null) return feature;
        DatumPlane datum = item as DatumPlane;
        if (datum != null) return datum.Feature;
        return null;
    }

    private static Feature FindFeature(Part part, string featureId)
    {
        foreach (Feature feature in part.Features.GetFeatures())
            if (feature != null && String.Equals(feature.JournalIdentifier, featureId, StringComparison.Ordinal)) return feature;
        throw new InvalidOperationException("Native NX feature JournalIdentifier could not be resolved: " + featureId);
    }

    private static Face FindFace(Part part, string bodyId, string faceId)
    {
        foreach (Body body in part.Bodies.ToArray())
        {
            if (body == null || !String.Equals(body.JournalIdentifier, bodyId, StringComparison.Ordinal)) continue;
            foreach (Face face in body.GetFaces())
                if (face != null && String.Equals(face.JournalIdentifier, faceId, StringComparison.Ordinal)) return face;
        }
        throw new InvalidOperationException("Native NX face JournalIdentifier could not be resolved: " + faceId);
    }

    private static Face FindFaceById(Part part, string faceId)
    {
        foreach (Body body in part.Bodies.ToArray())
            if (body != null)
                foreach (Face face in body.GetFaces())
                    if (face != null && String.Equals(face.JournalIdentifier, faceId, StringComparison.Ordinal)) return face;
        throw new InvalidOperationException("Native NX face JournalIdentifier could not be resolved: " + faceId);
    }

    private static Sketch FindSketch(Part part, string sketchId)
    {
        foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null && String.Equals(sketch.JournalIdentifier, sketchId, StringComparison.Ordinal)) return sketch;
        throw new InvalidOperationException("Native NX sketch JournalIdentifier could not be resolved: " + sketchId);
    }

    private static List<string> FeatureAncestorIds(Feature feature)
    {
        List<string> result = new List<string>();
        Queue<Feature> pending = new Queue<Feature>();
        foreach (Feature parent in feature.GetParents()) if (parent != null) pending.Enqueue(parent);
        while (pending.Count != 0)
        {
            Feature current = pending.Dequeue();
            if (result.Contains(current.JournalIdentifier)) continue;
            result.Add(current.JournalIdentifier);
            foreach (Feature parent in current.GetParents()) if (parent != null) pending.Enqueue(parent);
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

    private static Dictionary<string, object> ModelIdRecord(Part part)
    {
        List<string> features = new List<string>();
        foreach (Feature feature in part.Features.GetFeatures()) if (feature != null) features.Add(feature.JournalIdentifier);
        List<string> sketches = new List<string>();
        foreach (Sketch sketch in part.Sketches.ToArray()) if (sketch != null) sketches.Add(sketch.JournalIdentifier);
        List<string> bodies = new List<string>();
        foreach (Body body in part.Bodies.ToArray()) if (body != null) bodies.Add(body.JournalIdentifier);
        features.Sort(StringComparer.Ordinal); sketches.Sort(StringComparer.Ordinal); bodies.Sort(StringComparer.Ordinal);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "features", features }, { "sketches", sketches }, { "bodies", bodies }, { "is_modified", part.IsModified }
        };
    }

    private static Dictionary<string, object> ModelDefinitionRecord(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part)
    {
        Dictionary<string, object> inspected = adapter.InspectPart(binding);
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "binding_revision", binding.RevisionId }, { "direct_nx_ids", ModelIdRecord(part) }
        };
        string[] keys = new string[] { "identity", "part", "features", "sketches", "bodies", "expressions", "datum_planes" };
        foreach (string key in keys)
        {
            object value;
            if (inspected.TryGetValue(key, out value)) record[key] = value;
        }
        return record;
    }

    private static void RequireUnchangedPrepare(Dictionary<string, object> record,
        Dictionary<string, object> before, Dictionary<string, object> after)
    {
        bool unchanged = String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal);
        record["prepare_read_only_check"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "before", before }, { "after", after }, { "unchanged", unchanged }
        };
        Require(unchanged, "Prepare changed the native part definition, object IDs, modified state, or binding revision.");
    }

    private static List<object> ReadList(Dictionary<string, object> record, string key)
    {
        object value = null;
        Require(record != null && record.TryGetValue(key, out value), "Expected list field is missing: " + key);
        List<object> result = value as List<object>;
        if (result != null) return result;
        IList<string> stringList = value as IList<string>;
        if (stringList != null)
        {
            result = new List<object>();
            foreach (string item in stringList) result.Add(item);
            return result;
        }
        string[] stringArray = value as string[];
        if (stringArray != null)
        {
            result = new List<object>();
            foreach (string item in stringArray) result.Add(item);
            return result;
        }
        object[] array = value as object[];
        if (array != null) return new List<object>(array);
        throw new InvalidOperationException("Expected list field has wrong type: " + key);
    }

    private static List<string> ReadStringList(Dictionary<string, object> record, string key)
    {
        List<object> values = ReadList(record, key);
        List<string> result = new List<string>();
        foreach (object value in values) result.Add(Convert.ToString(value, CultureInfo.InvariantCulture));
        return result;
    }

    private static string RequiredString(Dictionary<string, object> record, string key)
    {
        string value = ReadOptionalString(record, key);
        Require(!String.IsNullOrWhiteSpace(value), "Required string field is missing or empty: " + key);
        return value;
    }

    private static string ReadRequiredString(Dictionary<string, object> record, string key)
    {
        return RequiredString(record, key);
    }

    private static string ReadOptionalString(Dictionary<string, object> record, string key)
    {
        object value = null;
        return record != null && record.TryGetValue(key, out value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
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
            IList<object> list = value as IList<object>;
            if (list != null)
            {
                result = new double[list.Count];
                for (int i = 0; i < list.Count; i++) result[i] = Convert.ToDouble(list[i], CultureInfo.InvariantCulture);
            }
        }
        Require(result != null && result.Length == count, "Expected vector has the wrong length: " + key);
        return result;
    }

    private static bool ContainsString(List<object> values, string expected)
    {
        if (values == null) return false;
        foreach (object value in values)
            if (String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), expected, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void RequireDictionaryContains(Dictionary<string, object> record, string key, object expected)
    {
        object actual = null;
        Require(record != null && record.TryGetValue(key, out actual), "Plan public view omitted field: " + key);
        if (expected is double)
            Require(Math.Abs(Convert.ToDouble(actual, CultureInfo.InvariantCulture) - (double)expected) <= PositionToleranceMm,
                "Plan public view differs from the requested value for " + key + ".");
        else
            Require(String.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture),
                Convert.ToString(expected, CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "Plan public view differs from the requested value for " + key + ".");
    }

    private static void RequireAnyDictionaryPropertyValue(Dictionary<string, object> record, string[] keys, string expected)
    {
        Require(ContainsAnyDictionaryPropertyValue(record, keys, expected),
            "Dictionary tree omitted all expected ID fields: " + String.Join(",", keys));
    }

    private static bool ContainsAnyDictionaryPropertyValue(object value, string[] keys, string expected)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            foreach (string key in keys)
            {
                object property;
                if (dictionary.TryGetValue(key, out property) &&
                    String.Equals(Convert.ToString(property, CultureInfo.InvariantCulture), expected, StringComparison.Ordinal)) return true;
            }
            foreach (object child in dictionary.Values)
                if (ContainsAnyDictionaryPropertyValue(child, keys, expected)) return true;
        }
        List<object> list = value as List<object>;
        if (list != null)
            foreach (object child in list)
                if (ContainsAnyDictionaryPropertyValue(child, keys, expected)) return true;
        object[] array = value as object[];
        if (array != null)
            foreach (object child in array)
                if (ContainsAnyDictionaryPropertyValue(child, keys, expected)) return true;
        return false;
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

    private static Part FindOwnedPart(Session session, string path)
    {
        string full = Path.GetFullPath(path);
        foreach (BasePart candidate in session.Parts)
        {
            Part part = candidate as Part;
            if (part != null && !String.IsNullOrWhiteSpace(part.FullPath) &&
                String.Equals(Path.GetFullPath(part.FullPath), full, StringComparison.OrdinalIgnoreCase)) return part;
        }
        return null;
    }

    private static List<Part> FindOwnedParts(Session session, string sourcePath, string savedPath)
    {
        List<Part> result = new List<Part>();
        foreach (BasePart item in session.Parts)
        {
            Part part = item as Part;
            if (part == null || String.IsNullOrWhiteSpace(part.FullPath)) continue;
            string full = Path.GetFullPath(part.FullPath);
            if (String.Equals(full, Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase) ||
                String.Equals(full, Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase)) result.Add(part);
        }
        return result;
    }

    private static void SaveInitialFixture(Part part)
    {
        PartSaveStatus status = part.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
        try
        {
            Require(status != null && status.NumberUnsavedParts == 0 && status.NumberUnsavedObjects == 0,
                "Isolated fixture part did not save cleanly before adapter takeover.");
        }
        finally { if (status != null) status.Dispose(); }
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

    private static void AddRestoreError(Dictionary<string, object> evidence, Exception ex)
    {
        List<object> errors;
        object value;
        if (!evidence.TryGetValue("restore_errors", out value) || (errors = value as List<object>) == null)
        {
            errors = new List<object>();
            evidence["restore_errors"] = errors;
        }
        errors.Add(ex.ToString());
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

    private static string DistanceName(double value)
    {
        return value < 0.0 ? "minus" + Math.Abs(value).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', '_') :
            value == 0.0 ? "zero" : "plus" + value.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', '_');
    }

    private static string UtcNow() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }

    private static double[] Point(Point3d point) { return new double[] { point.X, point.Y, point.Z }; }
    private static double[] Copy(double[] value) { return value == null ? null : (double[])value.Clone(); }
    private static double[] Add(double[] a, double[] b) { return new double[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
    private static double[] Subtract(double[] a, double[] b) { return new double[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
    private static double[] Scale(double[] a, double s) { return new double[] { a[0] * s, a[1] * s, a[2] * s }; }
    private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    private static double[] Normalize(double[] a)
    {
        double length = Math.Sqrt(Dot(a, a));
        Require(length > 1e-15, "Cannot normalize a near-zero vector.");
        return Scale(a, 1.0 / length);
    }
    private static double MaxAbs(double[] a) { return Math.Max(Math.Abs(a[0]), Math.Max(Math.Abs(a[1]), Math.Abs(a[2]))); }
    private static double VectorDistance(double[] a, double[] b)
    {
        double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
        return Math.Sqrt(x * x + y * y + z * z);
    }
    private static void RequireVectorClose(double[] actual, double[] expected, double tolerance, string message)
    {
        Require(VectorDistance(actual, expected) <= tolerance,
            message + " Actual=" + VectorString(actual) + "; expected=" + VectorString(expected) + "; error=" + VectorDistance(actual, expected).ToString("R", CultureInfo.InvariantCulture));
    }
    private static string VectorString(double[] value)
    {
        return value == null ? "null" : "[" + value[0].ToString("R", CultureInfo.InvariantCulture) + "," +
            value[1].ToString("R", CultureInfo.InvariantCulture) + "," + value[2].ToString("R", CultureInfo.InvariantCulture) + "]";
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static int GetUnloadOption(string dummy) { return (int)Session.LibraryUnloadOption.Explicitly; }
}
