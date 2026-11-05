// Real-NX acceptance entry for F04 angle datum planes. Compile offline with
// build-f04-acceptance.ps1. The journal is executed only by the NX operator.
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

public static class F04Acceptance
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    private const double PositionToleranceMm = 1e-6;
    private const double DirectionTolerance = 1e-7;
    private const double AngleToleranceDeg = 1e-6;
    private const double ParentEditAngleDeg = 10.0;

    private sealed class FaceReference
    {
        internal Face Face;
        internal string FaceId;
        internal string BodyId;
        internal string BodyFeatureId;
        internal double[] Point;
        internal double[] Normal;
        internal int UfType;
    }

    private sealed class AngleResult
    {
        internal string CaseName;
        internal string FeatureId;
        internal string ReferenceKind;
        internal string ReferenceId;
        internal string ExpectedParentFeatureId;
        internal string AxisLineId;
        internal Line AxisLine;
        internal double AngleDeg;
        internal M3PlanSnapshot Plan;
        internal Dictionary<string, object> Native;
    }

    private sealed class FixtureLines
    {
        internal Line Main;
        internal Line Reversed;
        internal Line OffPlane;
        internal Line Normal;
        internal Line Face;
        internal Dictionary<string, string> Ids;
    }

    private delegate void CaseBody(Dictionary<string, object> record);

    public static void Main(string[] args)
    {
        string runId = Guid.NewGuid().ToString("N");
        string modelDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "models");
        string evidenceDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "evidence");
        string sourcePath = Path.Combine(modelDirectory, "F04-acceptance-" + runId + ".prt");
        string savedPath = Path.Combine(modelDirectory, "F04-acceptance-" + runId + "-roundtrip.prt");
        string reportPath = Path.Combine(evidenceDirectory, "F04-acceptance-" + runId + ".json");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        RequireNewPath(sourcePath, "source model");
        RequireNewPath(savedPath, "roundtrip model");
        RequireNewPath(reportPath, "evidence report");

        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test", "F04 angle datum-plane NX acceptance" },
            { "test_version", "0.1.0" },
            { "started_utc", UtcNow() },
            { "run_id", runId },
            { "passed", false },
            { "source_model_path", sourcePath },
            { "roundtrip_model_path", savedPath },
            { "report_path", reportPath },
            { "position_tolerance_mm", PositionToleranceMm },
            { "direction_tolerance", DirectionTolerance },
            { "angle_tolerance_deg", AngleToleranceDeg },
            { "parent_edit_angle_deg", ParentEditAngleDeg },
            { "native_expression_sign_convention", "NX Plane.Expression angle in Degrees is -requested AngleDeg; model normal uses requested right-hand rotation about Line.StartPoint to Line.EndPoint." },
            { "cases", new List<object>() },
            { "defects", new List<object>() },
            { "configuration", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "test_layer", "real NXOpen subsystem validation through production M2OpenModelAdapter" },
                    { "traceability", "docs/F04.md; docs/V2.md" },
                    { "production_bridge_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Bridge.dll") },
                    { "production_adapter_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Entry.dll") },
                    { "acceptance_assembly", Path.Combine(evidenceDirectory, "F04Acceptance.dll") },
                    { "units", "mm, degrees" },
                    { "nx_started_by_harness", false },
                    { "single_fixture_instance", true },
                    { "physical_metrology_or_manufacturing_validation", false }
                }
            }
        };
        List<object> cases = (List<object>)evidence["cases"];
        List<object> defects = (List<object>)evidence["defects"];
        Persist(reportPath, evidence);

        Session session = null;
        M2OpenModelAdapter adapter = null;
        M2ModelBinding binding = null;
        Part testPart = null;
        Part originalPart = null;
        string datumReferenceId = null;
        string cylinderFeatureId = null;
        string cylinderBodyId = null;
        FaceReference tiltedFace = null;
        FixtureLines lines = null;
        AngleResult primaryDatumAngle = null;
        AngleResult faceAngle = null;
        string supportedSketchId = null;
        Dictionary<string, AngleResult> angleResults = new Dictionary<string, AngleResult>(StringComparer.Ordinal);

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
            Dictionary<string, object> cylinder = CreateTiltedCylinder(testPart);
            cylinderFeatureId = RequiredString(cylinder, "feature_id");
            cylinderBodyId = RequiredString(cylinder, "body_id");
            tiltedFace = FindTiltedPlanarFace(testPart, cylinderBodyId, cylinderFeatureId);
            evidence["tilted_face_fixture"] = FaceRecord(tiltedFace);
            evidence["tilted_cylinder_fixture"] = cylinder;
            SaveInitialFixture(testPart);

            adapter = new M2OpenModelAdapter();
            binding = adapter.Takeover(sourcePath, "saved_independent_copy", "f04-acceptance-session",
                "f04-acceptance-" + runId, 1);
            evidence["session"] = adapter.GetSession(binding);
            evidence["loaded_assemblies"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "bridge", AssemblyRecord(typeof(M2Json).Assembly) },
                { "production_adapter", AssemblyRecord(typeof(M2OpenModelAdapter).Assembly) },
                { "nxopen", AssemblyRecord(typeof(Session).Assembly) },
                { "acceptance", AssemblyRecord(Assembly.GetExecutingAssembly()) }
            };

            bool datumReady = RunCase(reportPath, evidence, "F04_F02_oblique_reference_setup",
                "Create the oblique F02 datum reference that anchors angle-plane and reference-edit checks.",
                new string[] { "Use a new saved millimeter fixture.", "Prepare, execute, and verify an F02 fixed datum plane through the production adapter.", "Independently read the native DatumPlane origin and unit normal." },
                "F02 returns APPLIED and verified; native origin and unit normal equal the fixture values within 1e-6 mm and 1e-7; prepare leaves the part definition, native IDs, modified state, and revision unchanged.",
                delegate(Dictionary<string, object> record)
                {
                    double[] origin = new double[] { 120.0, 230.0, 340.0 };
                    double[] normal = Normalize(new double[] { 1.0, 2.0, -1.0 });
                    double[] xAxis = Normalize(new double[] { 1.0, -1.0, -1.0 });
                    M3DatumPlaneRequest request = new M3DatumPlaneRequest
                    {
                        RevisionId = binding.RevisionId, Mode = "fixed", OriginMm = origin, Normal = normal, XAxis = xAxis
                    };
                    record["input"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "mode", "fixed" }, { "origin_mm", origin }, { "normal_world", normal }, { "x_axis_world", xAxis }
                    };
                    record["expected"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "origin_mm", origin }, { "normal_world", normal }, { "prepare_read_only", true }
                    };
                    record["tolerances"] = Tolerances();
                    Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, testPart);
                    M3PlanSnapshot plan = adapter.PrepareDatumPlane(binding, request);
                    Dictionary<string, object> after = ModelDefinitionRecord(adapter, binding, testPart);
                    RequireUnchangedPrepare(record, before, after);
                    record["plan"] = PlanRecord(plan);
                    M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
                    record["execution"] = ExecutionRecord(execution);
                    Require(execution != null && execution.State == "APPLIED", "F02 parent datum creation did not return APPLIED.");
                    datumReferenceId = ReadFeatureId(execution.Result);
                    M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
                    record["verification"] = VerificationRecord(verification);
                    Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
                        "F02 parent datum production verification did not pass.");
                    Dictionary<string, object> native = ReadNativeDatum(testPart, datumReferenceId);
                    record["native_raw"] = native;
                    RequireVectorClose(ReadVector(native, "origin_mm", 3), origin, PositionToleranceMm,
                        "Native F02 reference origin differs from the request.");
                    RequireVectorClose(Normalize(ReadVector(native, "normal_world", 3)), normal, DirectionTolerance,
                        "Native F02 reference normal differs from the request.");
                });

            Require(datumReady && !String.IsNullOrWhiteSpace(datumReferenceId),
                "F04 prerequisite F02 reference failed; angle cases cannot safely proceed.");
            Dictionary<string, object> referenceNative = ReadNativeDatum(testPart, datumReferenceId);
            double[] referencePoint = ReadVector(referenceNative, "origin_mm", 3);
            double[] referenceNormal = Normalize(ReadVector(referenceNative, "normal_world", 3));
            double[] tangent = Normalize(new double[] { 1.0, 0.0, 1.0 });
            Require(Math.Abs(Dot(tangent, referenceNormal)) <= DirectionTolerance,
                "Fixture tangent is not perpendicular to the F02 reference normal.");
            double[] faceTangent = TangentForNormal(tiltedFace.Normal);

            // Create every independent NXOpen.Line before reading any JournalIdentifier.
            // NX may renumber existing line identifiers when another line is inserted.
            lines = new FixtureLines();
            lines.Main = testPart.Curves.CreateLine(ToPoint(Add(referencePoint, Scale(tangent, -20.0))),
                ToPoint(Add(referencePoint, Scale(tangent, 20.0))));
            lines.Reversed = testPart.Curves.CreateLine(ToPoint(Add(referencePoint, Scale(tangent, 20.0))),
                ToPoint(Add(referencePoint, Scale(tangent, -20.0))));
            lines.OffPlane = testPart.Curves.CreateLine(ToPoint(Add(Add(referencePoint, Scale(tangent, -20.0)), Scale(referenceNormal, 2.0))),
                ToPoint(Add(Add(referencePoint, Scale(tangent, 20.0)), Scale(referenceNormal, 2.0))));
            lines.Normal = testPart.Curves.CreateLine(ToPoint(referencePoint),
                ToPoint(Add(referencePoint, Scale(referenceNormal, 20.0))));
            lines.Face = testPart.Curves.CreateLine(ToPoint(Add(tiltedFace.Point, Scale(faceTangent, -20.0))),
                ToPoint(Add(tiltedFace.Point, Scale(faceTangent, 20.0))));
            lines.Ids = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "main", lines.Main.JournalIdentifier }, { "reversed", lines.Reversed.JournalIdentifier },
                { "off_plane", lines.OffPlane.JournalIdentifier }, { "normal", lines.Normal.JournalIdentifier },
                { "face", lines.Face.JournalIdentifier }
            };
            SaveInitialFixture(testPart);
            adapter.InspectPart(binding); // Synchronize revision after native fixture curves were added.
            evidence["fixture"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "units", "mm" }, { "f02_reference_feature_id", datumReferenceId },
                { "reference_origin_mm", referencePoint }, { "reference_normal_world", referenceNormal },
                { "tilted_face", FaceRecord(tiltedFace) }, { "line_ids_captured_after_all_lines_created", StringMapRecord(lines.Ids) },
                { "lines", LineFixtureRecords(lines) },
                { "axis_has_parent_feature", false },
                { "purpose", "oblique F02 datum, explicit tilted planar face, directed/reversed/off-plane/normal raw NXOpen.Line fixtures" }
            };
            evidence["binding_revision_before_angle_tests"] = binding.RevisionId;
            Persist(reportPath, evidence);

            double[] angleCases = new double[] { 30.0, -30.0, 0.0, 90.0, -90.0, 180.0, -180.0 };
            for (int i = 0; i < angleCases.Length; i++)
            {
                double requestedAngle = angleCases[i];
                string angleKey = AngleKey(requestedAngle);
                AngleResult result = null;
                bool passed = RunAngleCase(reportPath, evidence, adapter, binding, testPart,
                    "F04_datum_reference_" + angleKey,
                    "Create an angle datum plane from the F02 datum reference and directed spatial axis line.",
                    datumReferenceId, "datum_plane", datumReferenceId, lines.Main,
                    lines.Ids["main"], referencePoint, referenceNormal, requestedAngle,
                    delegate(AngleResult value) { result = value; });
                if (passed && result != null)
                {
                    angleResults[angleKey] = result;
                    if (requestedAngle == 30.0) primaryDatumAngle = result;
                }
            }

            AngleResult reversedResult = null;
            bool reversedPassed = RunAngleCase(reportPath, evidence, adapter, binding, testPart,
                "F04_reversed_axis_positive_30",
                "Prove the line StartPoint to EndPoint direction controls the right-hand angle sign.",
                datumReferenceId, "datum_plane", datumReferenceId, lines.Reversed,
                lines.Ids["reversed"], referencePoint, referenceNormal, 30.0,
                delegate(AngleResult value) { reversedResult = value; });
            if (reversedPassed && reversedResult != null) angleResults["reversed"] = reversedResult;

            bool facePassed = RunAngleCase(reportPath, evidence, adapter, binding, testPart,
                "F04_explicit_tilted_planar_face_positive_30",
                "Create an angle datum plane from the explicit tilted planar Face and a coplanar spatial line.",
                tiltedFace.FaceId, "face", cylinderFeatureId, lines.Face,
                lines.Ids["face"], tiltedFace.Point, Normalize(tiltedFace.Normal), 30.0,
                delegate(AngleResult value) { faceAngle = value; });
            if (facePassed && faceAngle != null) angleResults["tilted_face"] = faceAngle;

            RunRejectedPrepare(reportPath, evidence, "F04_unknown_axis_rejected",
                "Reject an unknown axis identifier during prepare without changing native model state or IDs.",
                adapter, binding, testPart,
                AngleRequest(binding.RevisionId, 30.0, "missing-axis-journal-id", datumReferenceId, null),
                new string[] { "Use the saved F04 fixture and valid F02 reference.", "Set AxisLineId to a unique identifier that resolves to no native object.", "Call PrepareDatumPlane and compare the native part/ID/revision snapshots." });
            RunRejectedPrepare(reportPath, evidence, "F04_non_line_axis_rejected",
                "Reject a valid feature identifier whose native type is not NXOpen.Line.", adapter, binding, testPart,
                AngleRequest(binding.RevisionId, 30.0, datumReferenceId, datumReferenceId, null),
                new string[] { "Use the F02 datum feature JournalIdentifier as AxisLineId.", "Call PrepareDatumPlane and compare the before/after snapshots." });
            RunRejectedPrepare(reportPath, evidence, "F04_axis_not_coplanar_rejected",
                "Reject a directed NXOpen.Line parallel to the valid in-plane direction but displaced 2 mm off the support plane.", adapter, binding, testPart,
                AngleRequest(binding.RevisionId, 30.0, lines.Ids["off_plane"], datumReferenceId, null),
                new string[] { "Use the F04 off-plane line ID (both endpoints are 2 mm from the F02 reference plane).", "Prepare the angle plane and compare the snapshots." });
            RunRejectedPrepare(reportPath, evidence, "F04_axis_parallel_to_reference_normal_rejected",
                "Reject an NXOpen.Line whose direction is parallel to the reference-plane normal.", adapter, binding, testPart,
                AngleRequest(binding.RevisionId, 30.0, lines.Ids["normal"], datumReferenceId, null),
                new string[] { "Use the F04 normal-direction line through the F02 reference origin.", "Prepare the angle plane and compare the snapshots." });
            RunRejectedPrepare(reportPath, evidence, "F04_unknown_reference_rejected",
                "Reject an unresolved support datum-plane identifier.", adapter, binding, testPart,
                AngleRequest(binding.RevisionId, 30.0, lines.Ids["main"], "missing-reference-journal-id", null),
                new string[] { "Use a valid axis line and a unique unsupported support datum-plane ID.", "Prepare and compare the snapshots." });
            RunRejectedPrepare(reportPath, evidence, "F04_nonplanar_face_rejected",
                "Reject a curved cylindrical Face as the support of an angle datum plane.", adapter, binding, testPart,
                AngleRequest(binding.RevisionId, 30.0, lines.Ids["main"], null,
                    FindNonPlanarFace(testPart, cylinderBodyId).JournalIdentifier),
                new string[] { "Use the tilted cylinder's native nonplanar side Face JournalIdentifier.", "Prepare and compare the snapshots." });
            RunRejectedPrepare(reportPath, evidence, "F04_mixed_support_references_rejected",
                "Reject a request that supplies both the valid datum-plane and tilted planar-face support IDs.", adapter, binding, testPart,
                AngleRequest(binding.RevisionId, 30.0, lines.Ids["main"], datumReferenceId, tiltedFace.FaceId),
                new string[] { "Set both SupportDatumPlaneId and SupportFaceId on one angle request.", "Prepare and compare the snapshots." });
            M3DatumPlaneRequest mixedMode = AngleRequest(binding.RevisionId, 30.0, lines.Ids["main"], datumReferenceId, null);
            mixedMode.DistanceMm = 5.0;
            mixedMode.OriginMm = Copy(referencePoint);
            mixedMode.Normal = Copy(referenceNormal);
            mixedMode.XAxis = Normalize(new double[] { 1.0, -1.0, -1.0 });
            RunRejectedPrepare(reportPath, evidence, "F04_angle_mixed_with_distance_or_fixed_frame_rejected",
                "Reject angle mode combined with a nonzero distance and fixed-frame location/orientation fields.", adapter, binding, testPart,
                mixedMode,
                new string[] { "Set Mode=angle and valid reference/axis IDs.", "Also set DistanceMm=5 and fixed OriginMm/Normal/XAxis fields.", "Prepare and compare the snapshots." });
            RunRejectedPrepare(reportPath, evidence, "F04_angle_below_range_rejected",
                "Reject a finite angle below the inclusive -180..180 degree interval.", adapter, binding, testPart,
                AngleRequest(binding.RevisionId, -180.000001, lines.Ids["main"], datumReferenceId, null),
                new string[] { "Request AngleDeg=-180.000001 with valid reference and axis IDs.", "Prepare and compare the snapshots." });
            RunRejectedPrepare(reportPath, evidence, "F04_angle_above_range_rejected",
                "Reject a finite angle above the inclusive -180..180 degree interval.", adapter, binding, testPart,
                AngleRequest(binding.RevisionId, 180.000001, lines.Ids["main"], datumReferenceId, null),
                new string[] { "Request AngleDeg=180.000001 with valid reference and axis IDs.", "Prepare and compare the snapshots." });
            RunRejectedPrepare(reportPath, evidence, "F04_nonfinite_angle_rejected",
                "Reject a nonfinite angle during prepare.", adapter, binding, testPart,
                AngleRequest(binding.RevisionId, Double.NaN, lines.Ids["main"], datumReferenceId, null),
                new string[] { "Request AngleDeg=NaN with valid reference and axis IDs.", "Prepare and compare the snapshots; the input is reported as the string NaN." });
            RunRejectedPrepare(reportPath, evidence, "F04_infinite_angle_rejected",
                "Reject a positive infinite angle during prepare.", adapter, binding, testPart,
                AngleRequest(binding.RevisionId, Double.PositiveInfinity, lines.Ids["main"], datumReferenceId, null),
                new string[] { "Request AngleDeg=+Infinity with valid reference and axis IDs.", "Prepare and compare the snapshots; the input is reported as the string +Infinity." });

            AngleResult primary = primaryDatumAngle;
            bool hasSketchAngle = primary != null;
            if (hasSketchAngle)
            {
                RunCase(reportPath, evidence, "F04_angle_plane_supported_sketch",
                    "Verify an angle datum plane can support a sketch through the production adapter and native feature ancestry.",
                    new string[] { "Use the applied +30 degree angle datum ID.", "Prepare, execute, and verify one simple line sketch supported by that datum.", "Inspect the sketch and resolve its native Feature.GetParents ancestry." },
                    "Sketch is APPLIED and verified; InspectSketch identifies the exact angle datum support ID; native sketch feature ancestry reaches the angle datum feature.",
                    delegate(Dictionary<string, object> record)
                    {
                        double[] xAxis = ReadVector(primary.Native, "x_axis_world", 3);
                        M3SketchRequest request = new M3SketchRequest
                        {
                            RevisionId = binding.RevisionId,
                            SupportDatumPlaneId = primary.FeatureId,
                            XAxis = xAxis,
                            Geometries = new List<M3SketchGeometryRequest>
                            {
                                new M3SketchGeometryRequest { Id = "f04-angle-support-line", Type = "line", StartMm = new double[] { 1.0, 2.0 }, EndMm = new double[] { 9.0, 2.0 } }
                            }
                        };
                        record["input"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "support_datum_plane_id", primary.FeatureId }, { "x_axis_world", xAxis },
                            { "geometries", new object[] { new Dictionary<string, object>(StringComparer.Ordinal) { { "id", "f04-angle-support-line" }, { "type", "line" }, { "start_mm", new double[] { 1.0, 2.0 } }, { "end_mm", new double[] { 9.0, 2.0 } } } } }
                        };
                        record["expected"] = "APPLIED, verified, exact support datum ID and native ancestry";
                        record["tolerances"] = Tolerances();
                        Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, testPart);
                        M3PlanSnapshot plan = adapter.PrepareSketch(binding, request);
                        Dictionary<string, object> after = ModelDefinitionRecord(adapter, binding, testPart);
                        RequireUnchangedPrepare(record, before, after);
                        record["plan"] = PlanRecord(plan);
                        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
                        record["execution"] = ExecutionRecord(execution);
                        Require(execution != null && execution.State == "APPLIED", "Angle-supported sketch did not return APPLIED.");
                        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
                        record["verification"] = VerificationRecord(verification);
                        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
                            "Angle-supported sketch did not verify.");
                        supportedSketchId = ReadOptionalString(execution.Result, "sketch_id");
                        if (String.IsNullOrWhiteSpace(supportedSketchId)) supportedSketchId = plan.SketchId;
                        Require(!String.IsNullOrWhiteSpace(supportedSketchId), "Sketch result omitted sketch_id.");
                        Sketch sketch = FindSketch(testPart, supportedSketchId);
                        record["sketch_id"] = sketch.JournalIdentifier;
                        record["sketch_feature_id"] = sketch.Feature.JournalIdentifier;
                        List<string> ancestry = FeatureAncestorIds(sketch.Feature);
                        record["sketch_feature_ancestor_ids"] = ancestry;
                        Require(ancestry.Contains(primary.FeatureId), "Native sketch ancestry does not reach the requested angle datum feature.");
                        Dictionary<string, object> inspected = adapter.InspectSketch(binding, supportedSketchId);
                        record["inspect_sketch"] = inspected;
                        RequireAnyDictionaryPropertyValue(inspected,
                            new string[] { "datum_plane_id", "support_datum_plane_id" }, primary.FeatureId);
                        RefreshAngleLineIdentifiers(angleResults, lines);
                        record["fixture_line_ids_after_sketch_creation"] = StringMapRecord(lines.Ids);
                        Dictionary<string, object> postSketchNative = ReadNativeAngle(testPart, primary.FeatureId,
                            datumReferenceId, "datum_plane", primary.AxisLineId, primary.AxisLine,
                            referencePoint, referenceNormal, primary.AngleDeg, datumReferenceId);
                        record["primary_angle_native_after_sketch_creation"] = postSketchNative;
                        RequireAngleReadback(postSketchNative, testPart, datumReferenceId, "datum_plane",
                            primary.AxisLineId, primary.AxisLine, referencePoint, referenceNormal,
                            primary.AngleDeg, datumReferenceId);
                    });
            }
            else
            {
                RecordSkippedAsFailure(reportPath, evidence, "F04_angle_plane_supported_sketch",
                    "No verified positive angle feature exists; the sketch association prerequisite failed.");
            }

            bool roundtripReady = primary != null && faceAngle != null;
            if (roundtripReady)
            {
                RunCase(reportPath, evidence, "F04_save_copy_reopen_native_roundtrip",
                    "Save a separate native copy and independently re-read both datum-reference and tilted-face angle definitions.",
                    new string[] { "Call production SaveCopy with a fresh workspace/v2/models path and reopen=true.", "Resolve the angle features, reference features/faces, and exact raw axis Line IDs in the reopened Work Part.", "Read native Angle method, Plane.Expression converted to Degrees, Plane.GetGeometry, DatumPlane normal, parent ancestry, and axis endpoints directly from NXOpen." },
                    "SaveCopy is SAVED and reopened=true with production checks VERIFIED; native method is Angle; expression converts to -requested AngleDeg; both native reference and exact axis Line appear in Plane.GetGeometry; parent ancestry reaches the reference owner; Rodrigues normal and line-on-child-plane checks pass after reopen.",
                    delegate(Dictionary<string, object> record)
                    {
                        M2SaveCopyResult saved = adapter.SaveCopy(binding, savedPath, true);
                        record["save_copy"] = SaveCopyRecord(saved);
                        Require(saved != null && saved.State == "SAVED" && saved.Reopened,
                            "Production SaveCopy did not save and reopen the unique F04 target.");
                        Require(ChecksPass(saved.Checks), "Production SaveCopy roundtrip checks were not all VERIFIED.");
                        Require(String.Equals(Path.GetFullPath(savedPath), Path.GetFullPath(saved.PartPath), StringComparison.OrdinalIgnoreCase),
                            "SaveCopy returned a different target path.");
                        testPart = FindOwnedPart(session, savedPath);
                        Require(testPart != null, "Reopened F04 copy is not loaded in the NX session.");
                        Line reopenedDatumAxis = FindLineById(testPart, primary.AxisLineId);
                        Line reopenedFaceAxis = FindLineById(testPart, faceAngle.AxisLineId);
                        record["input"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "roundtrip_path", savedPath }, { "datum_angle_feature_id", primary.FeatureId },
                            { "datum_reference_id", datumReferenceId }, { "datum_axis_line_id", primary.AxisLineId },
                            { "face_angle_feature_id", faceAngle.FeatureId }, { "face_id", tiltedFace.FaceId },
                            { "face_axis_line_id", faceAngle.AxisLineId }
                        };
                        record["expected"] = "Native Angle method; expression=-requested degrees; exact native axis and reference geometry; Rodrigues normal; parent association";
                        record["tolerances"] = Tolerances();
                        record["datum_angle_native_reopened"] = ReadNativeAngle(testPart, primary.FeatureId,
                            datumReferenceId, "datum_plane", primary.AxisLineId, reopenedDatumAxis,
                            referencePoint, referenceNormal, primary.AngleDeg, datumReferenceId);
                        RequireAngleReadback((Dictionary<string, object>)record["datum_angle_native_reopened"],
                            testPart, datumReferenceId, "datum_plane", primary.AxisLineId, reopenedDatumAxis,
                            referencePoint, referenceNormal, primary.AngleDeg, datumReferenceId);
                        record["face_angle_native_reopened"] = ReadNativeAngle(testPart, faceAngle.FeatureId,
                            tiltedFace.FaceId, "face", faceAngle.AxisLineId, reopenedFaceAxis,
                            tiltedFace.Point, tiltedFace.Normal, faceAngle.AngleDeg, cylinderFeatureId);
                        RequireAngleReadback((Dictionary<string, object>)record["face_angle_native_reopened"],
                            testPart, tiltedFace.FaceId, "face", faceAngle.AxisLineId, reopenedFaceAxis,
                            tiltedFace.Point, tiltedFace.Normal, faceAngle.AngleDeg, cylinderFeatureId);
                        List<string> ancestry = FeatureAncestorIds(FindFeature(testPart, faceAngle.FeatureId));
                        record["face_angle_feature_ancestor_ids_reopened"] = ancestry;
                        Require(ancestry.Contains(cylinderFeatureId), "Reopened face angle feature ancestry lost its face owner feature.");
                        if (!String.IsNullOrWhiteSpace(supportedSketchId))
                        {
                            Sketch reopenedSketch = FindSketch(testPart, supportedSketchId);
                            List<string> sketchAncestry = FeatureAncestorIds(reopenedSketch.Feature);
                            record["reopened_sketch_feature_ancestor_ids"] = sketchAncestry;
                            Require(sketchAncestry.Contains(primaryDatumAngle.FeatureId),
                                "Saved/reopened sketch lost its native angle datum dependency.");
                            Dictionary<string, object> reopenedSketchInspection = adapter.InspectSketch(binding, supportedSketchId);
                            record["reopened_inspect_sketch"] = reopenedSketchInspection;
                            RequireAnyDictionaryPropertyValue(reopenedSketchInspection,
                                new string[] { "datum_plane_id", "support_datum_plane_id" }, primaryDatumAngle.FeatureId);
                        }
                        evidence["active_test_part_path_after_reopen"] = testPart.FullPath;
                    });
            }
            else
            {
                RecordSkippedAsFailure(reportPath, evidence, "F04_save_copy_reopen_native_roundtrip",
                    "The datum-reference angle or explicit tilted-face angle prerequisite did not pass.");
            }

            if (primary != null && datumReferenceId != null)
            {
                RunReferenceEditRevisionUndoCase(reportPath, evidence, session, adapter, binding, testPart,
                    datumReferenceId, primary, lines.Main, referencePoint, referenceNormal);
                RunAxisEditRevisionUndoCase(reportPath, evidence, session, adapter, binding, testPart,
                    datumReferenceId, primary, lines.Main, referencePoint, referenceNormal);
            }
            else
            {
                RecordSkippedAsFailure(reportPath, evidence, "F04_reference_edit_revision_invalidation_and_undo",
                    "No verified datum angle feature exists; reference edit prerequisite failed.");
                RecordSkippedAsFailure(reportPath, evidence, "F04_axis_edit_revision_invalidation_and_undo",
                    "No verified datum angle feature exists; axis edit prerequisite failed.");
            }
        }
        catch (Exception ex)
        {
            evidence["error_case"] = evidence.ContainsKey("current_case") ? evidence["current_case"] : "setup_or_harness";
            evidence["error"] = ex.ToString();
            evidence["passed"] = false;
            AddDefect(defects, Convert.ToString(evidence["error_case"], CultureInfo.InvariantCulture),
                new string[] { "Compile the production M2 assemblies and F04 acceptance harness.", "Load the journal only in the designated idle NX session.", "Review the saved F04 acceptance JSON and top-level exception." },
                ex.ToString(), "Correct the reported setup/harness fault and rerun the complete F04 matrix with a new unique fixture path.");
        }
        finally
        {
            try { if (binding != null && adapter != null) adapter.Release(binding); }
            catch (Exception ex) { evidence["release_error"] = ex.ToString(); }
            evidence["completed_utc"] = UtcNow();
            evidence["case_count"] = cases.Count;
            evidence["passed_case_count"] = CountCases(cases, "PASS");
            evidence["failed_case_count"] = CountCases(cases, "FAIL");
            evidence["skipped_case_count"] = CountCases(cases, "SKIPPED");
            evidence["passed"] = cases.Count > 0 && CountCases(cases, "PASS") == cases.Count && defects.Count == 0;
            Persist(reportPath, evidence);
        }
    }

    private static bool RunAngleCase(string reportPath, Dictionary<string, object> evidence,
        M2OpenModelAdapter adapter, M2ModelBinding binding, Part part, string name, string objective,
        string referenceId, string referenceKind, string expectedParentFeatureId, Line axis, string axisLineId,
        double[] referencePoint, double[] referenceNormal, double angleDeg,
        Action<AngleResult> resultSink)
    {
        AngleResult result = null;
        bool passed = RunCase(reportPath, evidence, name, objective,
            new string[] { "Record the requested reference, native NXOpen.Line ID and directed endpoints, and angle.", "Call prepare and prove the fixture snapshot and IDs did not change.", "Execute and verify through M2OpenModelAdapter.", "Directly read Plane.Method, Plane.Expression converted to Degrees, Plane.GetGeometry, DatumPlane normal, GetParents ancestry, and axis-line coplanarity." },
            "Execution is APPLIED and production verify passes; Plane.Method is Angle; native expression converts to -requested degrees within 1e-6 deg; direct normal equals the independent right-hand Rodrigues result within 1e-7; selected reference and exact axis Line are in native Plane.GetGeometry; reference owner is in GetParents ancestry; axis lies on both support and child planes within 1e-6 mm.",
            delegate(Dictionary<string, object> record)
            {
                record["input"] = AngleInputRecord(binding.RevisionId, angleDeg, referenceKind, referenceId, axisLineId,
                    axis, referencePoint, referenceNormal);
                record["expected"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "native_method", "Angle" }, { "native_expression_value_deg", -angleDeg },
                    { "right_hand_rotated_normal_world", Rodrigues(referenceNormal, Normalize(Subtract(Point(axis.EndPoint), Point(axis.StartPoint))), angleDeg) },
                    { "reference_id", referenceId }, { "axis_line_id", axisLineId },
                    { "reference_owner_feature_id", expectedParentFeatureId }, { "prepare_read_only", true }
                };
                record["tolerances"] = Tolerances();
                Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, part);
                M3DatumPlaneRequest request = AngleRequest(binding.RevisionId, angleDeg, axisLineId,
                    referenceKind == "datum_plane" ? referenceId : null, referenceKind == "face" ? referenceId : null);
                M3PlanSnapshot plan = adapter.PrepareDatumPlane(binding, request);
                Dictionary<string, object> after = ModelDefinitionRecord(adapter, binding, part);
                RequireUnchangedPrepare(record, before, after);
                record["plan"] = PlanRecord(plan);
                RequireAnglePlan(plan, angleDeg, axisLineId, referenceKind, referenceId);
                M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
                record["execution"] = ExecutionRecord(execution);
                Require(execution != null && execution.State == "APPLIED", "Angle datum-plane execution did not return APPLIED.");
                string featureId = ReadFeatureId(execution.Result);
                M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
                record["verification"] = VerificationRecord(verification);
                Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
                    "Angle datum-plane production verification did not pass.");
                Dictionary<string, object> native = ReadNativeAngle(part, featureId, referenceId,
                    referenceKind, axisLineId, axis, referencePoint, referenceNormal, angleDeg, expectedParentFeatureId);
                record["native_raw"] = native;
                RequireAngleReadback(native, part, referenceId, referenceKind, axisLineId, axis,
                    referencePoint, referenceNormal, angleDeg, expectedParentFeatureId);
                result = new AngleResult
                {
                    CaseName = name, FeatureId = featureId, ReferenceKind = referenceKind,
                    ReferenceId = referenceId, ExpectedParentFeatureId = expectedParentFeatureId,
                    AxisLineId = axisLineId, AxisLine = axis, AngleDeg = angleDeg, Plan = plan, Native = native
                };
            });
        if (passed && result != null && resultSink != null) resultSink(result);
        return passed;
    }

    private static void RunRejectedPrepare(string reportPath, Dictionary<string, object> evidence,
        string name, string objective, M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, M3DatumPlaneRequest request, string[] reproduction)
    {
        RunCase(reportPath, evidence, name, objective, reproduction,
            "Prepare rejects the request and before/after adapter snapshot, native feature/sketch/body IDs, modified state, and binding revision are identical.",
            delegate(Dictionary<string, object> record)
            {
                record["input"] = AngleRequestRecord(request);
                record["expected"] = "Prepare rejects without model, identifier, modified-state, or revision change";
                record["tolerances"] = Tolerances();
                Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, part);
                Exception rejected = null;
                try
                {
                    M3PlanSnapshot plan = adapter.PrepareDatumPlane(binding, request);
                    record["prepare_returned_plan"] = PlanRecord(plan);
                }
                catch (Exception ex) { rejected = ex; }
                record["rejection_exception"] = rejected == null ? null : ExceptionRecord(rejected);
                Dictionary<string, object> after = ModelDefinitionRecord(adapter, binding, part);
                record["before_snapshot"] = before;
                record["after_snapshot"] = after;
                bool unchanged = String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal);
                record["prepare_read_only_check"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "before", before }, { "after", after }, { "unchanged", unchanged }
                };
                Require(rejected != null, "Invalid angle request was accepted during prepare.");
                Require(unchanged, "Rejected angle prepare changed the native part definition, IDs, modified state, or revision.");
            });
    }

    private static void RunReferenceEditRevisionUndoCase(string reportPath, Dictionary<string, object> evidence,
        Session session, M2OpenModelAdapter adapter, M2ModelBinding binding, Part part,
        string referenceId, AngleResult applied, Line axis, double[] referencePoint, double[] referenceNormal)
    {
        RunCase(reportPath, evidence, "F04_reference_edit_revision_invalidation_and_undo",
            "Rotate the F02 reference datum plane around the shared in-plane axis, prove the angle child follows, invalidate stale state, and undo.",
            new string[] { "Prepare and retain a stale angle plan and one verified applied angle plan.", "Under one NX undo mark, rotate the reference fixed plane by +10 degrees about the shared coplanar axis line; do not translate it away from the axis.", "Update NX and the adapter revision, then directly read both datum normals and child/reference association.", "Execute the stale plan and re-verify the old applied plan; then undo and verify native geometry and the old plan recover." },
            "Reference normal rotates +10 degrees by the independently calculated right-hand Rodrigues result; axis remains coplanar; child normal equals Rodrigues(new reference normal, current directed axis, applied angle); association remains native; revision changes; stale execution is UNCHANGED and old verify fails; undo restores reference/child geometry and old verification.",
            delegate(Dictionary<string, object> record)
            {
                part = FindOwnedPart(session, part.FullPath) ?? part;
                axis = FindLineById(part, applied.AxisLineId);
                applied = CreateFreshAngleBaseline(record, adapter, binding, part, referenceId, axis, applied.AngleDeg);
                M3DatumPlaneRequest staleRequest = AngleRequest(binding.RevisionId, 7.5, applied.AxisLineId, referenceId, null);
                Dictionary<string, object> beforePrepare = ModelDefinitionRecord(adapter, binding, part);
                M3PlanSnapshot stalePlan = adapter.PrepareDatumPlane(binding, staleRequest);
                Dictionary<string, object> afterPrepare = ModelDefinitionRecord(adapter, binding, part);
                RequireUnchangedPrepare(record, beforePrepare, afterPrepare);
                record["input"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "reference_feature_id", referenceId }, { "axis_line_id", applied.AxisLineId },
                    { "reference_edit_angle_deg", ParentEditAngleDeg }, { "applied_angle_deg", applied.AngleDeg }
                };
                record["expected"] = "Reference rotation about shared axis preserves axis coplanarity; child normal follows; stale and undo semantics pass";
                record["tolerances"] = Tolerances();
                record["stale_plan"] = PlanRecord(stalePlan);
                M3VerificationResult baseline = adapter.VerifyPlan(binding, applied.Plan);
                record["baseline_applied_plan_verification"] = VerificationRecord(baseline);
                Require(baseline != null && baseline.Verified && ChecksPass(baseline.Checks),
                    "Applied angle plan did not verify before the reference edit.");
                Dictionary<string, object> parentBefore = ReadNativeDatum(part, referenceId);
                Dictionary<string, object> childBefore = ReadNativeAngle(part, applied.FeatureId, referenceId,
                    "datum_plane", applied.AxisLineId, axis, ReadVector(parentBefore, "origin_mm", 3),
                    Normalize(ReadVector(parentBefore, "normal_world", 3)), applied.AngleDeg, referenceId);
                record["reference_before_edit"] = parentBefore;
                record["child_before_edit"] = childBefore;
                string revisionBefore = binding.RevisionId;
                record["revision_before_edit"] = revisionBefore;
                Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible,
                    "F04 acceptance reference rotation revision test");
                bool markActive = true;
                try
                {
                    Dictionary<string, object> parentAfter = RotateFixedDatumPlane(part, referenceId,
                        axis, ParentEditAngleDeg, mark, session);
                    adapter.InspectPart(binding);
                    Dictionary<string, object> childAfter = ReadNativeAngle(part, applied.FeatureId, referenceId,
                        "datum_plane", applied.AxisLineId, axis, ReadVector(parentAfter, "origin_mm", 3),
                        Normalize(ReadVector(parentAfter, "normal_world", 3)), applied.AngleDeg, referenceId);
                    record["reference_after_edit"] = parentAfter;
                    record["child_after_edit"] = childAfter;
                    record["revision_after_edit"] = binding.RevisionId;
                    Require(!String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal),
                        "Reference edit did not advance the adapter revision.");
                    double[] axisDirection = Normalize(Subtract(Point(axis.EndPoint), Point(axis.StartPoint)));
                    double[] expectedParentNormal = Rodrigues(Normalize(ReadVector(parentBefore, "normal_world", 3)), axisDirection, ParentEditAngleDeg);
                    RequireVectorClose(Normalize(ReadVector(parentAfter, "normal_world", 3)), expectedParentNormal,
                        DirectionTolerance, "Reference plane did not rotate by the requested right-hand 10 degrees about the axis.");
                    double[] expectedChildNormal = Rodrigues(expectedParentNormal, axisDirection, applied.AngleDeg);
                    RequireVectorClose(Normalize(ReadVector(childAfter, "normal_world", 3)), expectedChildNormal,
                        DirectionTolerance, "Associative angle child did not follow the rotated reference normal.");
                    RequireAngleReadback(childAfter, part, referenceId, "datum_plane", applied.AxisLineId,
                        axis, ReadVector(parentAfter, "origin_mm", 3), expectedParentNormal,
                        applied.AngleDeg, referenceId);
                    M3ExecutionResult stale = adapter.ExecutePlan(binding, stalePlan);
                    record["stale_plan_execution_after_edit"] = ExecutionRecord(stale);
                    Require(stale != null && stale.State == "UNCHANGED",
                        "Stale plan did not return UNCHANGED after reference revision changed.");
                    RequireOldPlanVerificationFails(record, adapter, binding, applied.Plan,
                        "Old applied plan still verified after the reference was edited.");
                }
                finally
                {
                    if (markActive)
                    {
                        session.UndoToMark(mark, "F04 acceptance reference rotation revision test");
                        session.DeleteUndoMark(mark, "F04 acceptance reference rotation revision test");
                        markActive = false;
                    }
                }
                adapter.InspectPart(binding);
                Dictionary<string, object> parentRestored = ReadNativeDatum(part, referenceId);
                Dictionary<string, object> childRestored = ReadNativeAngle(part, applied.FeatureId, referenceId,
                    "datum_plane", applied.AxisLineId, axis, referencePoint, referenceNormal,
                    applied.AngleDeg, referenceId);
                record["revision_after_undo"] = binding.RevisionId;
                record["reference_after_undo"] = parentRestored;
                record["child_after_undo"] = childRestored;
                RequireVectorClose(ReadVector(parentRestored, "origin_mm", 3),
                    ReadVector(parentBefore, "origin_mm", 3), PositionToleranceMm,
                    "Undo did not restore the native reference origin.");
                RequireVectorClose(Normalize(ReadVector(parentRestored, "normal_world", 3)),
                    Normalize(ReadVector(parentBefore, "normal_world", 3)), DirectionTolerance,
                    "Undo did not restore the native reference normal.");
                RequireVectorClose(Normalize(ReadVector(childRestored, "normal_world", 3)),
                    Normalize(ReadVector(childBefore, "normal_world", 3)), DirectionTolerance,
                    "Undo did not restore the native angle child normal.");
                RequireAngleReadback(childRestored, part, referenceId, "datum_plane", applied.AxisLineId,
                    axis, referencePoint, referenceNormal, applied.AngleDeg, referenceId);
                M3VerificationResult restored = adapter.VerifyPlan(binding, applied.Plan);
                record["old_applied_plan_verification_after_undo"] = VerificationRecord(restored);
                Require(restored != null && restored.Verified && ChecksPass(restored.Checks),
                    "Previously applied angle plan did not verify after reference-edit undo.");
            });
    }

    private static void RunAxisEditRevisionUndoCase(string reportPath, Dictionary<string, object> evidence,
        Session session, M2OpenModelAdapter adapter, M2ModelBinding binding, Part part,
        string referenceId, AngleResult applied, Line axis, double[] referencePoint, double[] referenceNormal)
    {
        RunCase(reportPath, evidence, "F04_axis_edit_revision_invalidation_and_undo",
            "Rotate the raw NXOpen.Line direction within the reference plane, prove child association follows, invalidate stale state, and undo.",
            new string[] { "Prepare and retain a stale angle plan and one verified applied angle plan.", "Under one NX undo mark, rotate the raw axis line direction by +10 degrees within the unchanged reference plane while keeping it through the datum origin.", "Update NX and the adapter revision, then directly read the line, reference, child normal, and native association.", "Execute the stale plan and re-verify the old applied plan; then undo and verify native geometry and the old plan recover." },
            "The raw line JournalIdentifier is retained and its directed vector changes by +10 degrees in-plane; reference plane is unchanged; child normal equals Rodrigues(reference normal, new directed line, applied angle); native association remains; revision changes; stale execution is UNCHANGED and old verify fails; undo restores line/child geometry and old verification.",
            delegate(Dictionary<string, object> record)
            {
                part = FindOwnedPart(session, part.FullPath) ?? part;
                axis = FindLineById(part, applied.AxisLineId);
                applied = CreateFreshAngleBaseline(record, adapter, binding, part, referenceId, axis, applied.AngleDeg);
                M3DatumPlaneRequest staleRequest = AngleRequest(binding.RevisionId, 9.0, applied.AxisLineId, referenceId, null);
                Dictionary<string, object> beforePrepare = ModelDefinitionRecord(adapter, binding, part);
                M3PlanSnapshot stalePlan = adapter.PrepareDatumPlane(binding, staleRequest);
                Dictionary<string, object> afterPrepare = ModelDefinitionRecord(adapter, binding, part);
                RequireUnchangedPrepare(record, beforePrepare, afterPrepare);
                M3VerificationResult baseline = adapter.VerifyPlan(binding, applied.Plan);
                record["baseline_applied_plan_verification"] = VerificationRecord(baseline);
                Require(baseline != null && baseline.Verified && ChecksPass(baseline.Checks),
                    "Applied angle plan did not verify before the line edit.");
                Point3d startBefore = axis.StartPoint;
                Point3d endBefore = axis.EndPoint;
                double[] oldDirection = Normalize(Subtract(Point(endBefore), Point(startBefore)));
                double[] newDirection = Normalize(Rodrigues(oldDirection, referenceNormal, ParentEditAngleDeg));
                double[] center = referencePoint;
                double halfLength = VectorDistance(Point(startBefore), Point(endBefore)) / 2.0;
                double[] newStart = Add(center, Scale(newDirection, -halfLength));
                double[] newEnd = Add(center, Scale(newDirection, halfLength));
                record["input"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "reference_feature_id", referenceId }, { "axis_line_id", applied.AxisLineId },
                    { "line_edit_rotation_deg", ParentEditAngleDeg }, { "applied_angle_deg", applied.AngleDeg },
                    { "line_start_before_mm", Point(startBefore) }, { "line_end_before_mm", Point(endBefore) },
                    { "line_start_after_requested_mm", newStart }, { "line_end_after_requested_mm", newEnd }
                };
                record["expected"] = "Raw directed axis rotates +10 degrees in reference plane; child normal follows; stale and undo semantics pass";
                record["tolerances"] = Tolerances();
                record["stale_plan"] = PlanRecord(stalePlan);
                Dictionary<string, object> parentBefore = ReadNativeDatum(part, referenceId);
                Dictionary<string, object> childBefore = ReadNativeAngle(part, applied.FeatureId, referenceId,
                    "datum_plane", applied.AxisLineId, axis, referencePoint, referenceNormal,
                    applied.AngleDeg, referenceId);
                Dictionary<string, object> axisBefore = LineRecord(axis);
                record["reference_before_edit"] = parentBefore;
                record["child_before_edit"] = childBefore;
                record["axis_before_edit"] = axisBefore;
                string revisionBefore = binding.RevisionId;
                record["revision_before_edit"] = revisionBefore;
                Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible,
                    "F04 acceptance axis line revision test");
                bool markActive = true;
                try
                {
                    axis.SetEndpoints(ToPoint(newStart), ToPoint(newEnd));
                    int updateErrors = session.UpdateManager.DoUpdate(mark);
                    Require(updateErrors == 0, "NX update reported errors after the axis line edit.");
                    adapter.InspectPart(binding);
                    Dictionary<string, object> parentAfter = ReadNativeDatum(part, referenceId);
                    Dictionary<string, object> childAfter = ReadNativeAngle(part, applied.FeatureId, referenceId,
                        "datum_plane", applied.AxisLineId, axis, referencePoint, referenceNormal,
                        applied.AngleDeg, referenceId);
                    Dictionary<string, object> axisAfter = LineRecord(axis);
                    record["reference_after_edit"] = parentAfter;
                    record["child_after_edit"] = childAfter;
                    record["axis_after_edit"] = axisAfter;
                    record["revision_after_edit"] = binding.RevisionId;
                    record["axis_update_error_count"] = updateErrors;
                    Require(String.Equals(ReadRequiredString(axisAfter, "journal_id"), applied.AxisLineId, StringComparison.Ordinal),
                        "Axis line JournalIdentifier changed during endpoint edit.");
                    Require(!String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal),
                        "Axis line edit did not advance the adapter revision.");
                    RequireVectorClose(Normalize(ReadVector(parentAfter, "normal_world", 3)),
                        Normalize(ReadVector(parentBefore, "normal_world", 3)), DirectionTolerance,
                        "Axis edit unexpectedly changed the reference plane.");
                    RequireVectorClose(Normalize(ReadVector(childAfter, "normal_world", 3)),
                        Rodrigues(Normalize(ReadVector(parentBefore, "normal_world", 3)), newDirection, applied.AngleDeg),
                        DirectionTolerance, "Associative angle child did not follow the edited directed axis.");
                    RequireAngleReadback(childAfter, part, referenceId, "datum_plane", applied.AxisLineId,
                        axis, referencePoint, referenceNormal, applied.AngleDeg, referenceId);
                    M3ExecutionResult stale = adapter.ExecutePlan(binding, stalePlan);
                    record["stale_plan_execution_after_edit"] = ExecutionRecord(stale);
                    Require(stale != null && stale.State == "UNCHANGED",
                        "Stale plan did not return UNCHANGED after axis revision changed.");
                    RequireOldPlanVerificationFails(record, adapter, binding, applied.Plan,
                        "Old applied plan still verified after its axis line was edited.");
                }
                finally
                {
                    if (markActive)
                    {
                        session.UndoToMark(mark, "F04 acceptance axis line revision test");
                        session.DeleteUndoMark(mark, "F04 acceptance axis line revision test");
                        markActive = false;
                    }
                }
                adapter.InspectPart(binding);
                Dictionary<string, object> parentRestored = ReadNativeDatum(part, referenceId);
                Dictionary<string, object> childRestored = ReadNativeAngle(part, applied.FeatureId, referenceId,
                    "datum_plane", applied.AxisLineId, axis, referencePoint, referenceNormal,
                    applied.AngleDeg, referenceId);
                Dictionary<string, object> axisRestored = LineRecord(axis);
                record["revision_after_undo"] = binding.RevisionId;
                record["reference_after_undo"] = parentRestored;
                record["child_after_undo"] = childRestored;
                record["axis_after_undo"] = axisRestored;
                RequireVectorClose(ReadVector(axisRestored, "start_point_mm", 3), Point(startBefore),
                    PositionToleranceMm, "Undo did not restore the axis line start point.");
                RequireVectorClose(ReadVector(axisRestored, "end_point_mm", 3), Point(endBefore),
                    PositionToleranceMm, "Undo did not restore the axis line end point.");
                RequireVectorClose(Normalize(ReadVector(childRestored, "normal_world", 3)),
                    Normalize(ReadVector(childBefore, "normal_world", 3)), DirectionTolerance,
                    "Undo did not restore the angle child normal after axis edit.");
                RequireAngleReadback(childRestored, part, referenceId, "datum_plane", applied.AxisLineId,
                    axis, referencePoint, referenceNormal, applied.AngleDeg, referenceId);
                M3VerificationResult restored = adapter.VerifyPlan(binding, applied.Plan);
                record["old_applied_plan_verification_after_undo"] = VerificationRecord(restored);
                Require(restored != null && restored.Verified && ChecksPass(restored.Checks),
                    "Previously applied angle plan did not verify after axis-edit undo.");
            });
    }

    private static AngleResult CreateFreshAngleBaseline(Dictionary<string, object> record,
        M2OpenModelAdapter adapter, M2ModelBinding binding, Part part,
        string referenceId, Line axis, double angleDeg)
    {
        string axisId = axis.JournalIdentifier;
        Dictionary<string, object> reference = ReadNativeDatum(part, referenceId);
        double[] referencePoint = ReadVector(reference, "origin_mm", 3);
        double[] referenceNormal = Normalize(ReadVector(reference, "normal_world", 3));
        M3DatumPlaneRequest request = AngleRequest(binding.RevisionId, angleDeg, axisId, referenceId, null);
        Dictionary<string, object> before = ModelDefinitionRecord(adapter, binding, part);
        M3PlanSnapshot plan = adapter.PrepareDatumPlane(binding, request);
        Dictionary<string, object> after = ModelDefinitionRecord(adapter, binding, part);
        RequireUnchangedPrepare(record, before, after);
        record["fresh_applied_angle_input"] = AngleRequestRecord(request);
        record["fresh_applied_angle_plan"] = PlanRecord(plan);
        RequireAnglePlan(plan, angleDeg, axisId, "datum_plane", referenceId);
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        record["fresh_applied_angle_execution"] = ExecutionRecord(execution);
        Require(execution != null && execution.State == "APPLIED",
            "Fresh post-edit-case angle baseline did not return APPLIED.");
        string featureId = ReadFeatureId(execution.Result);
        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        record["fresh_applied_angle_verification"] = VerificationRecord(verification);
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "Fresh post-edit-case angle baseline did not verify.");
        Dictionary<string, object> native = ReadNativeAngle(part, featureId, referenceId,
            "datum_plane", axisId, axis, referencePoint, referenceNormal, angleDeg, referenceId);
        record["fresh_applied_angle_native_raw"] = native;
        RequireAngleReadback(native, part, referenceId, "datum_plane", axisId, axis,
            referencePoint, referenceNormal, angleDeg, referenceId);
        return new AngleResult
        {
            CaseName = "fresh_edit_baseline", FeatureId = featureId, ReferenceKind = "datum_plane",
            ReferenceId = referenceId, ExpectedParentFeatureId = referenceId, AxisLineId = axisId,
            AxisLine = axis, AngleDeg = angleDeg, Plan = plan, Native = native
        };
    }

    private static Dictionary<string, object> RotateFixedDatumPlane(Part part, string featureId,
        Line axis, double angleDeg, Session.UndoMarkId mark, Session session)
    {
        Feature feature = FindFeature(part, featureId);
        Dictionary<string, object> before = ReadNativeDatum(part, featureId);
        double[] origin = ReadVector(before, "origin_mm", 3);
        double[] normal = Normalize(ReadVector(before, "normal_world", 3));
        double[] xAxis = Normalize(ReadVector(before, "x_axis_world", 3));
        double[] axisDirection = Normalize(Subtract(Point(axis.EndPoint), Point(axis.StartPoint)));
        double[] newNormal = Normalize(Rodrigues(normal, axisDirection, angleDeg));
        double[] newXAxis = Normalize(Rodrigues(xAxis, axisDirection, angleDeg));
        double[] newYAxis = Normalize(Cross(newNormal, newXAxis));
        Matrix3x3 matrix = new Matrix3x3();
        matrix.Xx = newXAxis[0]; matrix.Xy = newXAxis[1]; matrix.Xz = newXAxis[2];
        matrix.Yx = newYAxis[0]; matrix.Yy = newYAxis[1]; matrix.Yz = newYAxis[2];
        matrix.Zx = newNormal[0]; matrix.Zy = newNormal[1]; matrix.Zz = newNormal[2];
        DatumPlaneBuilder builder = part.Features.CreateDatumPlaneBuilder(feature);
        Feature committed;
        try
        {
            Plane plane = builder.GetPlane();
            Require(plane != null, "Reference DatumPlaneBuilder.GetPlane returned null.");
            plane.SetMethod(PlaneTypes.MethodType.Fixed);
            plane.EditFixedPlane(ToPoint(origin), matrix);
            plane.Evaluate();
            committed = builder.CommitFeature();
        }
        finally { builder.Destroy(); }
        Require(committed != null && String.Equals(committed.JournalIdentifier, featureId, StringComparison.Ordinal),
            "Reference datum edit changed its native feature identifier.");
        int errors = session.UpdateManager.DoUpdate(mark);
        Require(errors == 0, "NX update reported errors after rotating the reference datum plane.");
        Dictionary<string, object> after = ReadNativeDatum(part, featureId);
        RequireVectorClose(ReadVector(after, "origin_mm", 3), origin, PositionToleranceMm,
            "Reference rotation translated the datum origin instead of rotating around the shared axis.");
        after["requested_rotation_deg"] = angleDeg;
        after["expected_normal_world"] = newNormal;
        after["update_error_count"] = errors;
        return after;
    }

    private static Dictionary<string, object> ReadNativeAngle(Part part, string featureId,
        string referenceId, string referenceKind, string axisLineId, Line axis,
        double[] referencePoint, double[] referenceNormal, double angleDeg, string expectedParentFeatureId)
    {
        Feature feature = FindFeature(part, featureId);
        DatumPlane datum = FindDatumEntity(feature);
        DatumPlaneBuilder builder = part.Features.CreateDatumPlaneBuilder(feature);
        try
        {
            Plane definingPlane = builder.GetPlane();
            Require(definingPlane != null, "DatumPlaneBuilder.GetPlane returned null for the angle feature.");
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
            Vector3d datumNormal = datum.Normal;
            Point3d datumOrigin = datum.Origin;
            Expression expression = definingPlane.Expression;
            Require(expression != null, "Native angle Plane.Expression is null.");
            Require(expression.Units != null, "Native angle Plane.Expression has no units.");
            Unit degree = FindDegreeUnit(part.UnitCollection);
            double expressionValueDeg = part.UnitCollection.Convert(expression.Units, degree, expression.Value);
            Point3d axisStart = axis.StartPoint;
            Point3d axisEnd = axis.EndPoint;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "feature_id", feature.JournalIdentifier }, { "datum_entity_id", datum.JournalIdentifier },
                { "datum_entity_feature_id", datum.Feature == null ? null : datum.Feature.JournalIdentifier },
                { "entity_type", datum.GetType().FullName }, { "origin_mm", Point(datumOrigin) },
                { "normal_world", new double[] { datumNormal.X, datumNormal.Y, datumNormal.Z } },
                { "x_axis_world", Normalize(Subtract(Point(p1), Point(p0))) },
                { "y_axis_world", Normalize(Subtract(Point(p3), Point(p0))) },
                { "feature_parent_ids", FeatureIds(feature.GetParents()) },
                { "feature_ancestor_ids", FeatureAncestorIds(feature) },
                { "plane_method", definingPlane.Method.ToString() },
                { "plane_method_numeric", Convert.ToInt32(definingPlane.Method, CultureInfo.InvariantCulture) },
                { "native_expression_api", "Plane.Expression" },
                { "native_expression_value_raw", expression.Value },
                { "native_expression_value_deg", expressionValueDeg },
                { "native_expression_right_hand_side", expression.RightHandSide },
                { "native_expression_units", expression.Units.ToString() },
                { "native_degree_unit_name", "Degrees" },
                { "native_expression_unit_symbol", expression.Units.Symbol },
                { "native_plane_flip", definingPlane.Flip },
                { "native_plane_reverse_side", definingPlane.GetReverseSide() },
                { "geometry_ids", geometryIds }, { "geometry_types", geometryTypes },
                { "geometry_owner_feature_ids", geometryOwnerFeatureIds },
                { "reference_kind", referenceKind }, { "requested_reference_id", referenceId },
                { "expected_reference_owner_feature_id", expectedParentFeatureId },
                { "axis_line_id", axisLineId }, { "axis_line_type", axis.GetType().FullName },
                { "axis_line_start_mm", Point(axisStart) }, { "axis_line_end_mm", Point(axisEnd) },
                { "axis_line_feature_parent_not_required", true },
                { "expected_reference_point_mm", Copy(referencePoint) },
                { "expected_reference_normal_world", Normalize(Copy(referenceNormal)) },
                { "expected_angle_deg", angleDeg },
                { "expected_native_expression_value_deg", -angleDeg },
                { "expected_normal_world", Rodrigues(Normalize(Copy(referenceNormal)),
                    Normalize(Subtract(Point(axisEnd), Point(axisStart))), angleDeg) }
            };
        }
        finally { builder.Destroy(); }
    }

    private static void RequireAngleReadback(Dictionary<string, object> native, Part part,
        string referenceId, string referenceKind, string axisLineId, Line axis,
        double[] referencePoint, double[] referenceNormal, double angleDeg, string expectedParentFeatureId)
    {
        Require(String.Equals(ReadRequiredString(native, "plane_method"), PlaneTypes.MethodType.Angle.ToString(), StringComparison.Ordinal),
            "Direct native Plane.Method is not Angle.");
        double expressionDeg = ReadRequiredDouble(native, "native_expression_value_deg");
        Require(Math.Abs(expressionDeg - (-angleDeg)) <= AngleToleranceDeg,
            "Plane.Expression converted to Degrees does not preserve NX's negative native angle convention.");
        Require(String.Equals(ReadRequiredString(native, "axis_line_id"), axisLineId, StringComparison.Ordinal),
            "Native axis line ID differs from the selected request line.");
        Require(String.Equals(ReadRequiredString(native, "axis_line_type"), typeof(Line).FullName, StringComparison.Ordinal),
            "Selected native axis object is not NXOpen.Line.");
        List<object> geometryIds = ReadList(native, "geometry_ids");
        Require(ContainsString(geometryIds, axisLineId),
            "Plane.GetGeometry does not contain the exact selected axis NXOpen.Line.");
        List<object> geometryOwners = ReadList(native, "geometry_owner_feature_ids");
        List<string> parents = ReadStringList(native, "feature_parent_ids");
        List<string> ancestors = ReadStringList(native, "feature_ancestor_ids");
        bool referenceInGeometry = ContainsString(geometryIds, referenceId) ||
            ContainsString(geometryOwners, referenceId);
        if (referenceKind == "datum_plane")
        {
            Dictionary<string, object> referenceNative = ReadNativeDatum(part, referenceId);
            string datumEntityId = ReadRequiredString(referenceNative, "datum_entity_id");
            referenceInGeometry = referenceInGeometry || ContainsString(geometryIds, datumEntityId) ||
                ContainsString(geometryOwners, expectedParentFeatureId);
        }
        else
        {
            Face nativeFace = FindFaceById(part, referenceId);
            int faceType;
            double[] facePoint, faceNormal;
            ReadFaceData(nativeFace, out faceType, out facePoint, out faceNormal);
            Require(faceType == UFConstants.UF_MODL_PLANAR_FACE,
                "Native selected support Face is not planar during direct readback.");
            RequireVectorClose(facePoint, referencePoint, PositionToleranceMm,
                "Direct native support Face plane point differs from the captured tilted face fixture.");
            RequireVectorClose(Normalize(faceNormal), Normalize(Copy(referenceNormal)), DirectionTolerance,
                "Direct native support Face normal differs from the captured tilted face fixture.");
        }
        Require(referenceInGeometry, "Plane.GetGeometry does not contain the selected native support reference or its owning feature.");
        Require(parents.Contains(expectedParentFeatureId) || ancestors.Contains(expectedParentFeatureId),
            "Angle feature GetParents ancestry does not reach the native reference owner feature.");
        double[] actualNormal = Normalize(ReadVector(native, "normal_world", 3));
        double[] axisDirection = Normalize(Subtract(Point(axis.EndPoint), Point(axis.StartPoint)));
        double[] expectedNormal = Rodrigues(Normalize(Copy(referenceNormal)), axisDirection, angleDeg);
        RequireVectorClose(actualNormal, expectedNormal, DirectionTolerance,
            "Native DatumPlane normal differs from the independent right-hand Rodrigues rotation.");
        double[] axisStart = Point(axis.StartPoint);
        double[] axisEnd = Point(axis.EndPoint);
        double[] actualOrigin = ReadVector(native, "origin_mm", 3);
        double startChildError = Math.Abs(Dot(Subtract(axisStart, actualOrigin), actualNormal));
        double endChildError = Math.Abs(Dot(Subtract(axisEnd, actualOrigin), actualNormal));
        double startReferenceError = Math.Abs(Dot(Subtract(axisStart, referencePoint), Normalize(Copy(referenceNormal))));
        double endReferenceError = Math.Abs(Dot(Subtract(axisEnd, referencePoint), Normalize(Copy(referenceNormal))));
        double axisNormalDot = Math.Abs(Dot(axisDirection, actualNormal));
        native["axis_start_to_child_plane_error_mm"] = startChildError;
        native["axis_end_to_child_plane_error_mm"] = endChildError;
        native["axis_start_to_reference_plane_error_mm"] = startReferenceError;
        native["axis_end_to_reference_plane_error_mm"] = endReferenceError;
        native["axis_direction_dot_child_normal"] = axisNormalDot;
        Require(startChildError <= PositionToleranceMm && endChildError <= PositionToleranceMm,
            "Selected axis line does not lie on the generated angle datum plane.");
        Require(startReferenceError <= PositionToleranceMm && endReferenceError <= PositionToleranceMm,
            "Selected axis line is not coplanar with the support reference plane.");
        Require(axisNormalDot <= DirectionTolerance,
            "Selected axis direction is not parallel to the generated angle plane.");
        native["geometry_matches_axis_and_reference"] = true;
        native["parent_ancestry_matches_reference_owner"] = true;
        native["right_hand_rodrigues_check"] = true;
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
            { "length_mm", 40.0 }, { "purpose", "tilted analytic planar cap and curved side Face for angle support validation" }
        };
    }

    private static FaceReference FindTiltedPlanarFace(Part part, string bodyId, string bodyFeatureId)
    {
        double invRoot3 = 1.0 / Math.Sqrt(3.0);
        double[] target = new double[] { invRoot3, invRoot3, invRoot3 };
        FaceReference selected = null;
        foreach (Body body in part.Bodies.ToArray())
        {
            if (body == null || !String.Equals(body.JournalIdentifier, bodyId, StringComparison.Ordinal)) continue;
            foreach (Face face in body.GetFaces())
            {
                int type;
                double[] point, normal;
                ReadFaceData(face, out type, out point, out normal);
                if (type != UFConstants.UF_MODL_PLANAR_FACE) continue;
                double[] unit = Normalize(normal);
                if (Dot(unit, target) < 1.0 - 1e-6) continue;
                Require(selected == null, "Tilted-cylinder fixture has multiple matching positive planar caps.");
                selected = new FaceReference
                {
                    Face = face, FaceId = face.JournalIdentifier, BodyId = bodyId,
                    BodyFeatureId = bodyFeatureId, Point = point, Normal = unit, UfType = type
                };
            }
        }
        Require(selected != null, "Tilted cylinder did not produce the expected explicitly identified planar cap Face.");
        return selected;
    }

    private static Face FindNonPlanarFace(Part part, string bodyId)
    {
        foreach (Body body in part.Bodies.ToArray())
        {
            if (body == null || !String.Equals(body.JournalIdentifier, bodyId, StringComparison.Ordinal)) continue;
            foreach (Face face in body.GetFaces())
            {
                int type;
                double[] point, normal;
                ReadFaceData(face, out type, out point, out normal);
                if (type != UFConstants.UF_MODL_PLANAR_FACE) return face;
            }
        }
        throw new InvalidOperationException("Tilted-cylinder fixture has no native nonplanar side Face.");
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
            { "feature_id", feature.JournalIdentifier }, { "datum_entity_id", datum.JournalIdentifier },
            { "datum_entity_feature_id", datum.Feature == null ? null : datum.Feature.JournalIdentifier },
            { "entity_type", datum.GetType().FullName }, { "origin_mm", Point(origin) },
            { "normal_world", new double[] { normal.X, normal.Y, normal.Z } },
            { "x_axis_world", xAxis }, { "y_axis_world", yAxis },
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
        throw new InvalidOperationException("Feature JournalIdentifier does not resolve to a native DatumPlane: " + feature.JournalIdentifier);
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

    private static Face FindFaceById(Part part, string faceId)
    {
        foreach (Body body in part.Bodies.ToArray())
            if (body != null)
                foreach (Face face in body.GetFaces())
                    if (face != null && String.Equals(face.JournalIdentifier, faceId, StringComparison.Ordinal)) return face;
        throw new InvalidOperationException("Native NX face JournalIdentifier could not be resolved: " + faceId);
    }

    private static Line FindLineById(Part part, string lineId)
    {
        foreach (Curve curve in part.Curves.ToArray())
        {
            Line line = curve as Line;
            if (line != null && String.Equals(line.JournalIdentifier, lineId, StringComparison.Ordinal)) return line;
        }
        throw new InvalidOperationException("Native NXOpen.Line JournalIdentifier could not be resolved: " + lineId);
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
        List<string> curves = new List<string>();
        foreach (Curve curve in part.Curves.ToArray()) if (curve != null) curves.Add(curve.JournalIdentifier);
        features.Sort(StringComparer.Ordinal); sketches.Sort(StringComparer.Ordinal); bodies.Sort(StringComparer.Ordinal); curves.Sort(StringComparer.Ordinal);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "features", features }, { "sketches", sketches }, { "bodies", bodies }, { "curves", curves }, { "is_modified", part.IsModified }
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

    private static Dictionary<string, object> AngleInputRecord(string revisionId, double angleDeg,
        string referenceKind, string referenceId, string axisLineId, Line axis,
        double[] referencePoint, double[] referenceNormal)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", revisionId }, { "mode", "angle" }, { "angle_deg", ReportNumber(angleDeg) },
            { "axis_line_id", axisLineId },
            { "support_datum_plane_id", referenceKind == "datum_plane" ? referenceId : null },
            { "support_face_id", referenceKind == "face" ? referenceId : null },
            { "axis_object_type", axis.GetType().FullName },
            { "axis_start_mm", Point(axis.StartPoint) }, { "axis_end_mm", Point(axis.EndPoint) },
            { "axis_start_to_reference_plane_error_mm", Math.Abs(Dot(Subtract(Point(axis.StartPoint), referencePoint), Normalize(referenceNormal))) },
            { "axis_end_to_reference_plane_error_mm", Math.Abs(Dot(Subtract(Point(axis.EndPoint), referencePoint), Normalize(referenceNormal))) }
        };
    }

    private static Dictionary<string, object> AngleRequestRecord(M3DatumPlaneRequest request)
    {
        string angle = Double.IsNaN(request.AngleDeg) ? "NaN" :
            Double.IsPositiveInfinity(request.AngleDeg) ? "+Infinity" :
            Double.IsNegativeInfinity(request.AngleDeg) ? "-Infinity" : request.AngleDeg.ToString("R", CultureInfo.InvariantCulture);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "revision_id", request.RevisionId }, { "mode", request.Mode }, { "angle_deg", angle },
            { "axis_line_id", request.AxisLineId }, { "support_datum_plane_id", request.SupportDatumPlaneId },
            { "support_face_id", request.SupportFaceId }, { "distance_mm", request.DistanceMm },
            { "origin_mm", request.OriginMm }, { "normal", request.Normal }, { "x_axis", request.XAxis }
        };
    }

    private static M3DatumPlaneRequest AngleRequest(string revisionId, double angleDeg,
        string axisLineId, string supportDatumId, string supportFaceId)
    {
        return new M3DatumPlaneRequest
        {
            RevisionId = revisionId, Mode = "angle", AngleDeg = angleDeg,
            AxisLineId = axisLineId, SupportDatumPlaneId = supportDatumId, SupportFaceId = supportFaceId
        };
    }

    private static void RequireAnglePlan(M3PlanSnapshot plan, double angleDeg,
        string axisLineId, string referenceKind, string referenceId)
    {
        Require(plan != null, "PrepareDatumPlane returned no angle plan.");
        Require(plan.PublicView != null, "Angle plan omitted its public view.");
        RequireDictionaryContains(plan.PublicView, "mode", "angle");
        RequireDictionaryContains(plan.PublicView, "angle_deg", angleDeg, AngleToleranceDeg);
        RequireDictionaryContains(plan.PublicView, "axis_line_id", axisLineId);
        RequireDictionaryContains(plan.PublicView, referenceKind == "datum_plane" ? "support_datum_plane_id" : "support_face_id", referenceId);
    }

    private static Dictionary<string, object> Tolerances()
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "position_mm", PositionToleranceMm }, { "direction_vector", DirectionTolerance },
            { "angle_deg", AngleToleranceDeg }, { "source", "NX2506 model-value comparison tolerance; not physical metrology tolerance" }
        };
    }

    private static Dictionary<string, object> LineRecord(Line line)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "journal_id", line.JournalIdentifier }, { "object_type", line.GetType().FullName },
            { "start_point_mm", Point(line.StartPoint) }, { "end_point_mm", Point(line.EndPoint) },
            { "direction_unit_start_to_end", Normalize(Subtract(Point(line.EndPoint), Point(line.StartPoint))) }
        };
    }

    private static Dictionary<string, object> LineFixtureRecords(FixtureLines lines)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "main", LineRecord(lines.Main) }, { "reversed", LineRecord(lines.Reversed) },
            { "off_plane", LineRecord(lines.OffPlane) }, { "normal", LineRecord(lines.Normal) },
            { "tilted_face", LineRecord(lines.Face) }
        };
    }

    private static void RefreshAngleLineIdentifiers(Dictionary<string, AngleResult> results, FixtureLines lines)
    {
        lines.Ids["main"] = lines.Main.JournalIdentifier;
        lines.Ids["reversed"] = lines.Reversed.JournalIdentifier;
        lines.Ids["off_plane"] = lines.OffPlane.JournalIdentifier;
        lines.Ids["normal"] = lines.Normal.JournalIdentifier;
        lines.Ids["face"] = lines.Face.JournalIdentifier;
        foreach (AngleResult result in results.Values)
        {
            if (result.AxisLine == null) continue;
            result.AxisLineId = result.AxisLine.JournalIdentifier;
        }
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

    private static Dictionary<string, object> StringMapRecord(Dictionary<string, string> source)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (source != null)
            foreach (KeyValuePair<string, string> item in source)
                result[item.Key] = item.Value;
        return result;
    }

    private static M3DatumPlaneRequest MakeFixedRequest(string revisionId, double[] origin, double[] normal, double[] xAxis)
    {
        return new M3DatumPlaneRequest { RevisionId = revisionId, Mode = "fixed", OriginMm = origin, Normal = normal, XAxis = xAxis };
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
                "Correct the observed failure and rerun the complete F04 acceptance with a new unique model path; every required positive, rejection, roundtrip, edit, stale-plan, and undo case must pass.");
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
        string[] reproduction = new string[] { "Complete the named prerequisite case.", "Rerun the complete F04 acceptance with a new unique fixture." };
        cases.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "objective", "Required downstream F04 acceptance coverage." },
            { "procedure", reproduction }, { "pass_criteria", "The prerequisite passes and this case completes all stated checks." },
            { "status", "SKIPPED" }, { "skip_reason", reason }, { "completed_utc", UtcNow() }
        });
        evidence["passed"] = false;
        AddDefect(defects, name, reproduction, reason,
            "Correct the prerequisite failure and rerun the complete F04 matrix with a new unique model path; this case must execute and pass.");
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
                "Isolated F04 fixture did not save cleanly before adapter/model operations.");
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

    private static Unit FindDegreeUnit(UnitCollection units)
    {
        string[] names = new string[] { "Degrees", "Degree", "degree" };
        for (int i = 0; i < names.Length; i++)
        {
            try
            {
                Unit unit = units.FindObject(names[i]);
                if (unit != null) return unit;
            }
            catch { }
        }
        throw new InvalidOperationException("NX unit collection did not resolve a Degree unit (tried Degrees/Degree/degree).");
    }

    private static string AngleKey(double value)
    {
        return value < 0.0 ? "minus_" + Math.Abs(value).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', '_') :
            value == 0.0 ? "zero" : "plus_" + value.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', '_');
    }

    private static void RequireDictionaryContains(Dictionary<string, object> record, string key, object expected)
    {
        object actual = null;
        Require(record != null && record.TryGetValue(key, out actual), "Plan public view omitted field: " + key);
        Require(String.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture),
            Convert.ToString(expected, CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "Plan public view differs from the requested value for " + key + ".");
    }

    private static void RequireDictionaryContains(Dictionary<string, object> record, string key, double expected, double tolerance)
    {
        object actual = null;
        Require(record != null && record.TryGetValue(key, out actual), "Plan public view omitted field: " + key);
        double actualNumber = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
        Require(Math.Abs(actualNumber - expected) <= tolerance,
            "Plan public view differs from the requested numeric value for " + key + ".");
    }

    private static List<object> ReadList(Dictionary<string, object> record, string key)
    {
        object value = null;
        Require(record != null && record.TryGetValue(key, out value), "Expected list field is missing: " + key);
        List<object> result = value as List<object>;
        if (result != null) return result;
        IList<string> strings = value as IList<string>;
        if (strings != null)
        {
            result = new List<object>();
            foreach (string item in strings) result.Add(item);
            return result;
        }
        string[] array = value as string[];
        if (array != null)
        {
            result = new List<object>();
            foreach (string item in array) result.Add(item);
            return result;
        }
        object[] objects = value as object[];
        if (objects != null) return new List<object>(objects);
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

    private static void RequireAnyDictionaryPropertyValue(Dictionary<string, object> record, string[] keys, string expected)
    {
        Require(ContainsAnyDictionaryPropertyValue(record, keys, expected),
            "Inspection result omitted all expected support-ID fields: " + String.Join(",", keys));
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
            foreach (object child in list) if (ContainsAnyDictionaryPropertyValue(child, keys, expected)) return true;
        object[] array = value as object[];
        if (array != null)
            foreach (object child in array) if (ContainsAnyDictionaryPropertyValue(child, keys, expected)) return true;
        return false;
    }

    private static double ReportNumber(double value) { return Double.IsNaN(value) || Double.IsInfinity(value) ? 0.0 : value; }
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
    private static double[] Normalize(double[] a)
    {
        double length = Math.Sqrt(Dot(a, a));
        Require(length > 1e-15, "Cannot normalize a near-zero vector.");
        return Scale(a, 1.0 / length);
    }
    private static double VectorDistance(double[] a, double[] b)
    {
        double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
        return Math.Sqrt(x * x + y * y + z * z);
    }
    private static double[] Rodrigues(double[] vector, double[] axis, double angleDeg)
    {
        double[] u = Normalize(axis);
        double theta = angleDeg * Math.PI / 180.0;
        double c = Math.Cos(theta), s = Math.Sin(theta);
        return Add(Add(Scale(vector, c), Scale(Cross(u, vector), s)), Scale(u, Dot(u, vector) * (1.0 - c)));
    }
    private static double[] TangentForNormal(double[] normal)
    {
        double[] n = Normalize(normal);
        double[] seed = Math.Abs(n[0]) < 0.8 ? new double[] { 1.0, 0.0, 0.0 } : new double[] { 0.0, 1.0, 0.0 };
        return Normalize(Cross(n, seed));
    }
    private static void RequireVectorClose(double[] actual, double[] expected, double tolerance, string message)
    {
        double error = VectorDistance(actual, expected);
        Require(error <= tolerance, message + " Actual=" + VectorString(actual) + "; expected=" + VectorString(expected) + "; error=" + error.ToString("R", CultureInfo.InvariantCulture));
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
