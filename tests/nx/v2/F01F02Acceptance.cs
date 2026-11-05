// Real-NX acceptance entry point for F01/F02. Compile offline with build-acceptance.ps1,
// then invoke through RunAcceptance.cs inside an idle user NX session.
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

public static class F01F02Acceptance
{
    private const string ProjectRoot = @"C:\0sf\1PROJECT\AI\design\nx-sketch-mcp";
    private const double PositionToleranceMm = 1e-6;
    private const double FrameComponentTolerance = 1e-7;
    private const double SupportPlaneToleranceMm = 1e-5;
    private const double Pi = Math.PI;

    private sealed class Frame
    {
        internal double[] Origin;
        internal double[] Normal;
        internal double[] XAxis;
        internal double[] YAxis;
    }

    private delegate void CaseBody(Dictionary<string, object> record);

    public static void Main(string[] args)
    {
        string runId = Guid.NewGuid().ToString("N");
        string modelDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "models");
        string evidenceDirectory = Path.Combine(ProjectRoot, "workspace", "v2", "evidence");
        string sourcePath = Path.Combine(modelDirectory, "F01F02-acceptance-" + runId + ".prt");
        string savedPath = Path.Combine(modelDirectory, "F01F02-acceptance-" + runId + "-roundtrip.prt");
        string reportPath = Path.Combine(evidenceDirectory, "F01F02-acceptance-" + runId + ".json");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(evidenceDirectory);
        Require(!File.Exists(sourcePath) && !Directory.Exists(sourcePath), "The unique source model path already exists; refusing to overwrite it.");
        Require(!File.Exists(savedPath) && !Directory.Exists(savedPath), "The unique roundtrip model path already exists; refusing to overwrite it.");
        Require(!File.Exists(reportPath) && !Directory.Exists(reportPath), "The unique evidence report path already exists; refusing to overwrite it.");

        Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
        List<object> cases = new List<object>();
        List<object> defects = new List<object>();
        evidence.Add("test", "F01/F02 NX acceptance");
        evidence.Add("test_version", "0.4.0");
        evidence.Add("started_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        evidence.Add("run_id", runId);
        evidence.Add("passed", false);
        evidence.Add("source_model_path", sourcePath);
        evidence.Add("roundtrip_model_path", savedPath);
        evidence.Add("report_path", reportPath);
        evidence.Add("cases", cases);
        evidence.Add("defects", defects);
        evidence.Add("scope_limitations", new List<object>
        {
            "No nondeterministic NX feature-creation failure is injected in this acceptance entry; native create/commit rollback is not independently validated here."
        });
        evidence.Add("configuration", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "test_layer", "real NXOpen session with production M2OpenModelAdapter and production M3NxFeatureFactory" },
            { "project_version", "0.4.0" },
            { "production_bridge_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Bridge.dll") },
            { "production_adapter_assembly", Path.Combine(ProjectRoot, "workspace", "build", "current", "M2", "M2Entry.dll") },
            { "acceptance_assembly", Path.Combine(evidenceDirectory, "F01F02Acceptance.dll") },
            { "position_tolerance_mm", PositionToleranceMm },
            { "frame_component_tolerance", FrameComponentTolerance },
            { "support_plane_tolerance_mm", SupportPlaneToleranceMm }
        });
        Persist(reportPath, evidence);

        Session session = null;
        Part originalPart = null;
        Part testPart = null;
        M2OpenModelAdapter adapter = null;
        M2ModelBinding binding = null;
        M3PlanSnapshot datumSketchPlan = null;
        string cylinderFeatureId = null;
        string cylinderBodyId = null;
        string datumPlaneId = null;
        string datumSketchId = null;
        string emptySketchId = null;
        string freeSketchId = null;
        string faceSketchId = null;
        string supportFaceId = null;
        Frame faceTestFrame = null;
        Dictionary<string, string> legacySketchIds = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            session = Session.GetSession();
            originalPart = session.Parts.Work;
            evidence["original_work_part_path"] = originalPart == null ? null : originalPart.FullPath;
            evidence["original_work_part_was_modified"] = originalPart != null && originalPart.IsModified;
            evidence["session_was_in_task_environment"] = session.IsInTaskEnvironment;
            if (session.IsInTaskEnvironment)
                throw new InvalidOperationException("Acceptance requires an idle NX session outside a task environment.");
            if (originalPart != null && originalPart.IsModified)
                throw new InvalidOperationException("Acceptance requires a saved, unmodified original Work Part.");

            Part created = session.Parts.NewDisplay(sourcePath, Part.Units.Millimeters);
            testPart = created;
            evidence["fixture"] = CreateTiltedCylinder(created);
            cylinderFeatureId = Convert.ToString(((Dictionary<string, object>)evidence["fixture"])["feature_id"], CultureInfo.InvariantCulture);
            cylinderBodyId = Convert.ToString(((Dictionary<string, object>)evidence["fixture"])["body_id"], CultureInfo.InvariantCulture);
            PartSaveStatus initialSave = created.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
            try
            {
                if (initialSave == null || initialSave.NumberUnsavedParts != 0 || initialSave.NumberUnsavedObjects != 0)
                    throw new InvalidOperationException("The isolated fixture part did not save cleanly before adapter takeover.");
            }
            finally { if (initialSave != null) initialSave.Dispose(); }
            evidence["source_model_created"] = File.Exists(sourcePath);

            adapter = new M2OpenModelAdapter();
            binding = adapter.Takeover(sourcePath, "saved_independent_copy", "f01-f02-acceptance-session",
                "f01-f02-acceptance-" + runId, 1);
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

            RunCase(reportPath, evidence, cases, defects, "F01_invalid_frame_rejected_without_model_change",
                "A parallel normal/x-axis frame is rejected during prepare before NX features or sketch objects are added.",
                new string[] {
                    "Load the unique saved acceptance fixture into an idle NX session.",
                    "Submit a free-frame sketch with origin [10,20,30], normal [1,1,1], x_axis [2,2,2], and one line.",
                    "Compare feature/sketch/body JournalIdentifier sets and modified state before and after prepare."
                },
                "After the implementation is corrected, the production adapter must return INVALID_REQUEST and leave all recorded model identifiers, counts, binding revision, and modified state unchanged.",
                delegate(Dictionary<string, object> record)
                {
                    string before = ModelFingerprint(testPart, binding);
                    string revisionBefore = binding.RevisionId;
                    M3SketchRequest request = new M3SketchRequest
                    {
                        RevisionId = binding.RevisionId,
                        OriginMm = new double[] { 10.0, 20.0, 30.0 },
                        Normal = new double[] { 1.0, 1.0, 1.0 },
                        XAxis = new double[] { 2.0, 2.0, 2.0 },
                        Geometries = new List<M3SketchGeometryRequest> { LineGeometry("invalid-frame-line", 1.0, 1.0, 5.0, 1.0) }
                    };
                    Exception observed = null;
                    try { adapter.PrepareSketch(binding, request); }
                    catch (Exception ex) { observed = ex; }
                    record["observed_exception"] = observed == null ? null : observed.ToString();
                    record["observed_error_code"] = observed is M2ModelException ? ((M2ModelException)observed).Code : null;
                    string after = ModelFingerprint(testPart, binding);
                    record["model_fingerprint_before"] = before;
                    record["model_fingerprint_after"] = after;
                    record["binding_revision_before"] = revisionBefore;
                    record["binding_revision_after"] = binding.RevisionId;
                    Require(observed is M2ModelException && ((M2ModelException)observed).Code == "INVALID_REQUEST",
                        "Invalid frame prepare did not return M2ModelException(INVALID_REQUEST).");
                    Require(String.Equals(before, after, StringComparison.Ordinal),
                        "Invalid frame prepare changed feature/sketch/body IDs or modified state.");
                    Require(String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal),
                        "Invalid frame prepare changed the model binding revision.");
                });

            Frame datumFrame = BuildFrame(new double[] { 10.0, 20.0, 30.0 },
                new double[] { 1.0, 1.0, 1.0 }, new double[] { 1.0, -1.0, 0.0 });
            M3PlanSnapshot datumPlan = null;
            RunCase(reportPath, evidence, cases, defects, "F02_native_spatial_datum_plane",
                "Create and verify a native spatial DatumPlane with independent normalized frame checks and feature inventory readback.",
                new string[] {
                    "Prepare a datum plane at [10,20,30] mm with normal [1,1,1] and x_axis [1,-1,0].",
                    "Execute and verify the production plan.",
                    "Read the native datum feature, plane vectors, feature inventory definition, and parent/child links directly from NXOpen."
                },
                "The plan must be APPLIED and verified; the datum_plane_id must equal the native feature JournalIdentifier; origin must be within 1e-6 mm; unit normal/x/y axes must match the independent right-handed frame within 1e-7; the part feature inventory must contain the same native datum-plane definition.",
                delegate(Dictionary<string, object> record)
                {
                    M3DatumPlaneRequest request = new M3DatumPlaneRequest
                    {
                        RevisionId = binding.RevisionId,
                        OriginMm = new double[] { 10.0, 20.0, 30.0 },
                        Normal = new double[] { 1.0, 1.0, 1.0 },
                        XAxis = new double[] { 1.0, -1.0, 0.0 }
                    };
                    Dictionary<string, object> beforePrepare = ModelDefinitionRecord(adapter, binding, testPart);
                    datumPlan = adapter.PrepareDatumPlane(binding, request);
                    Dictionary<string, object> afterPrepare = ModelDefinitionRecord(adapter, binding, testPart);
                    RequireUnchangedPrepare(record, beforePrepare, afterPrepare);
                    record["prepare_public_view"] = datumPlan.PublicView;
                    Require(datumPlan.Kind == "create_datum_plane", "PrepareDatumPlane returned the wrong plan kind.");
                    M3ExecutionResult execution = adapter.ExecutePlan(binding, datumPlan);
                    record["execution"] = ExecutionRecord(execution);
                    Require(execution != null && execution.State == "APPLIED", "Datum plane execution was not APPLIED.");
                    datumPlaneId = ReadRequiredString(execution.Result, "datum_plane_id");
                    record["datum_plane_id"] = datumPlaneId;
                    Require(!String.IsNullOrWhiteSpace(datumPlaneId), "Datum plane execution returned no datum_plane_id.");
                    if (!String.IsNullOrWhiteSpace(datumPlan.FeatureId))
                        Require(String.Equals(datumPlan.FeatureId, datumPlaneId, StringComparison.Ordinal),
                            "Plan feature id differs from the returned datum_plane_id.");
                    M3VerificationResult verification = adapter.VerifyPlan(binding, datumPlan);
                    record["verification"] = VerificationRecord(verification);
                    Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
                        "Datum plane verification did not pass all checks.");
                    RequireResultIdAndDefinition(execution.Result, datumPlaneId, "datum_plane");
                    RequireResultIdAndDefinition(verification.Result, datumPlaneId, "datum_plane");
                    Dictionary<string, object> nativeReadback = ReadNativeDatumPlane(testPart, datumPlaneId);
                    record["direct_nx_datum_plane"] = nativeReadback;
                    RequireDatumPlaneReadback(nativeReadback, datumFrame);
                    Dictionary<string, object> partInventory = adapter.InspectPart(binding);
                    record["part_inventory_readback"] = partInventory;
                    RequireFeatureRecord(partInventory, datumPlaneId, "datum_plane");
                });

            Frame freeFrame = BuildFrame(new double[] { 10.0, 20.0, 30.0 },
                new double[] { 1.0, 1.0, 1.0 }, new double[] { 1.0, 0.0, 0.0 });
            RunCase(reportPath, evidence, cases, defects, "F01_free_frame_empty_sketch",
                "Create and verify an empty sketch on an arbitrary non-principal world frame without requiring curves or full constraint status.",
                new string[] {
                    "Prepare a free-frame sketch at [10,20,30] mm with normal [1,1,1], x_axis [1,0,0], and geometries [].",
                    "Execute and verify the production adapter plan.",
                    "Read the sketch frame and confirm NXOpen reports no authored curves."
                },
                "The empty sketch must execute and verify successfully, have the requested normalized world frame within 1e-6 mm/1e-7 component tolerance, and contain zero NXOpen Curve objects; an empty/underconstrained status alone is not a failure.",
                delegate(Dictionary<string, object> record)
                {
                    M3SketchRequest request = new M3SketchRequest
                    {
                        RevisionId = binding.RevisionId,
                        OriginMm = Copy(freeFrame.Origin),
                        Normal = new double[] { 1.0, 1.0, 1.0 },
                        XAxis = new double[] { 1.0, 0.0, 0.0 },
                        Geometries = new List<M3SketchGeometryRequest>()
                    };
                    M3PlanSnapshot plan;
                    Sketch sketch = RunSketch(adapter, binding, testPart, request, freeFrame,
                        request.Geometries, record, out plan);
                    emptySketchId = sketch.JournalIdentifier;
                    int curves = CountCurves(sketch, record);
                    Require(curves == 0, "An empty frame sketch contains a curve.");
                    record["curve_count"] = curves;
                });

            RunCase(reportPath, evidence, cases, defects, "F01_free_frame_line_circle_world_coordinates",
                "Check normal normalization, x-axis projection/normalization, and independent local-to-world positions for a line and circle.",
                new string[] {
                    "Prepare a free-frame sketch at [10,20,30] mm with normal [1,1,1] and deliberately non-orthogonal x_axis [1,0,0].",
                    "Create local line [(2,3),(8,3)] mm and circle center (5,-4) mm, diameter 6 mm.",
                    "Compare raw NXOpen line endpoints and circle center/radius with independently calculated world coordinates."
                },
                "NXOpen sketch origin and basis must match the independently normalized right-handed frame; every line endpoint and circle center must be within 1e-6 mm, and circle radius within 1e-6 mm.",
                delegate(Dictionary<string, object> record)
                {
                    List<M3SketchGeometryRequest> geometries = new List<M3SketchGeometryRequest>
                    {
                        LineGeometry("free-line", 2.0, 3.0, 8.0, 3.0),
                        CircleGeometry("free-circle", 5.0, -4.0, 6.0)
                    };
                    M3SketchRequest request = new M3SketchRequest
                    {
                        RevisionId = binding.RevisionId,
                        OriginMm = new double[] { 10.0, 20.0, 30.0 },
                        Normal = new double[] { 1.0, 1.0, 1.0 },
                        XAxis = new double[] { 1.0, 0.0, 0.0 },
                        Geometries = geometries
                    };
                    M3PlanSnapshot plan;
                    Sketch sketch = RunSketch(adapter, binding, testPart, request, freeFrame,
                        geometries, record, out plan);
                    freeSketchId = sketch.JournalIdentifier;
                });

            RunCase(reportPath, evidence, cases, defects, "F01_existing_native_datum_plane_support",
                "Create a line and circle sketch on the already-created native DatumPlane and verify the live parent feature dependency.",
                new string[] {
                    "Use the F02 datum_plane_id returned by the production plan as SupportDatumPlaneId.",
                    "Prepare/execute a sketch with the datum plane origin and spatial normal/x-axis.",
                    "Verify support_association, sketch feature GetParents(), direct geometry, and later saved-copy roundtrip."
                },
                "The sketch must be APPLIED and verified; the sketch feature must have the datum-plane feature as an immediate NXOpen parent; InspectSketch support_kind and support object ID must identify the same native datum plane.",
                delegate(Dictionary<string, object> record)
                {
                    Require(!String.IsNullOrWhiteSpace(datumPlaneId), "No F02 datum plane is available for the support test.");
                    List<M3SketchGeometryRequest> geometries = new List<M3SketchGeometryRequest>
                    {
                        LineGeometry("datum-line", 2.0, 3.0, 8.0, 3.0),
                        CircleGeometry("datum-circle", 5.0, -4.0, 6.0)
                    };
                    M3SketchRequest request = new M3SketchRequest
                    {
                        RevisionId = binding.RevisionId,
                        XAxis = Copy(datumFrame.XAxis),
                        SupportDatumPlaneId = datumPlaneId,
                        Geometries = geometries
                    };
                    M3PlanSnapshot prepared;
                    Sketch sketch = RunSketch(adapter, binding, testPart, request, datumFrame,
                        geometries, record, out prepared);
                    datumSketchPlan = prepared;
                    datumSketchId = sketch.JournalIdentifier;
                    Feature sketchFeature = sketch.Feature;
                    List<string> parentIds = FeatureAncestorIds(sketchFeature);
                    record["sketch_feature_id"] = sketchFeature.JournalIdentifier;
                    record["sketch_parent_feature_ids"] = parentIds;
                    Require(parentIds.Contains(datumPlaneId), "Native sketch feature does not depend on the requested DatumPlane feature.");
                    Dictionary<string, object> inspected = adapter.InspectSketch(binding, datumSketchId);
                    record["inspect_sketch"] = inspected;
                    RequireDictionaryPropertyValue(inspected, "support_kind", "DATUM_PLANE");
                    RequireAnyDictionaryPropertyValue(inspected, new string[] { "datum_plane_id" }, datumPlaneId);
                });

            RunCase(reportPath, evidence, cases, defects, "F01_tilted_non_axis_face_support",
                "Create a rectangular sketch on a queried, non-axis-aligned planar cylinder cap using its explicit Face JournalIdentifier.",
                new string[] {
                    "Query all planar faces on the isolated tilted cylinder body.",
                    "Select a cap whose world normal has all three components materially nonzero and whose boundary is supported.",
                    "Use the returned face_journal_id and interior cap centroid with a rectangular sketch profile.",
                    "Read the support face directly with NXOpen/UF and compare all world geometry coordinates."
                },
                "The chosen face must be planar and non-axis-aligned; the requested frame origin must lie inside its plane and domain; the production verification must report native FACE support with the same face ID; direct NXOpen geometry and frame must meet the stated tolerances.",
                delegate(Dictionary<string, object> record)
                {
                    Dictionary<string, object> query = adapter.QueryFaces(binding, binding.RevisionId, "all_planar", cylinderBodyId);
                    record["all_planar_face_query"] = query;
                    Require(String.Equals(ReadOptionalString(query, "selection_status"), "all", StringComparison.Ordinal),
                        "all_planar query did not return selection_status=all.");
                    List<object> candidates = query.ContainsKey("candidates") ? query["candidates"] as List<object> : null;
                    Require(candidates != null && candidates.Count > 0, "all_planar returned no planar faces for the tilted cylinder fixture.");
                    Dictionary<string, object> selected = SelectTiltedCylinderCap(candidates);
                    record["selected_face_candidate"] = selected;
                    string faceId = ReadRequiredString(selected, "face_journal_id");
                    supportFaceId = faceId;
                    double[] faceCentroid = ReadVector(selected, "centroid_mm", 3);
                    double[] candidateNormal = ReadVector(selected, "normal_world", 3);
                    Dictionary<string, object> candidateFrame = ReadDictionary(selected, "frame");
                    double[] faceXAxis = ReadVector(candidateFrame, "x_axis_world", 3);
                    Frame faceFrame = BuildFrame(faceCentroid, candidateNormal, faceXAxis);
                    faceTestFrame = faceFrame;
                    Dictionary<string, object> rawFace = ReadFaceDirect(testPart, cylinderBodyId, faceId);
                    record["direct_nx_face_readback"] = rawFace;
                    Require(Convert.ToInt32(rawFace["uf_face_type"], CultureInfo.InvariantCulture) == UFConstants.UF_MODL_PLANAR_FACE,
                        "Selected face JournalIdentifier did not resolve to a native planar Face.");
                    double[] rawNormal = ReadVector(rawFace, "normal_world", 3);
                    Require(MaxAbs(candidateNormal) < 0.95, "Selected Face normal is aligned with a principal model axis.");
                    Require(Dot(Normalize(rawNormal), faceFrame.Normal) >= 1.0 - FrameComponentTolerance,
                        "Query face normal differs from independent UF face normal.");
                    Require(PlaneDistance(faceFrame.Origin, ReadVector(rawFace, "plane_point_mm", 3), rawNormal) <= SupportPlaneToleranceMm,
                        "The selected face centroid is outside the native plane tolerance.");

                    List<M3SketchGeometryRequest> geometries = new List<M3SketchGeometryRequest>
                    {
                        RectangleGeometry("face-rectangle", 20.0, -10.0, 15.0, 10.0)
                    };
                    M3SketchRequest request = new M3SketchRequest
                    {
                        RevisionId = binding.RevisionId,
                        SupportFaceId = faceId,
                        SupportBodyId = cylinderBodyId,
                        OriginMm = Copy(faceFrame.Origin),
                        XAxis = Copy(faceFrame.XAxis),
                        Geometries = geometries
                    };
                    M3PlanSnapshot facePlan;
                    Sketch sketch = RunSketch(adapter, binding, testPart, request, faceFrame,
                        geometries, record, out facePlan);
                    faceSketchId = sketch.JournalIdentifier;
                    Dictionary<string, object> inspected = adapter.InspectSketch(binding, faceSketchId);
                    record["inspect_sketch"] = inspected;
                    RequireDictionaryPropertyValue(inspected, "support_kind", "FACE");
                    RequireAnyDictionaryPropertyValue(inspected, new string[] { "support_face_id" }, faceId);
                    RequireDictionaryPropertyValue(inspected, "native", true);
                });

            AddLegacyPlaneCase(reportPath, evidence, cases, defects, adapter, binding, testPart,
                "F01_legacy_XY_regression", "XY", new Frame
                {
                    Origin = new double[] { 0.0, 0.0, 0.0 }, Normal = new double[] { 0.0, 0.0, 1.0 },
                    XAxis = new double[] { 1.0, 0.0, 0.0 }, YAxis = new double[] { 0.0, 1.0, 0.0 }
                }, legacySketchIds);
            AddLegacyPlaneCase(reportPath, evidence, cases, defects, adapter, binding, testPart,
                "F01_legacy_XZ_regression", "XZ", new Frame
                {
                    Origin = new double[] { 0.0, 0.0, 0.0 }, Normal = new double[] { 0.0, -1.0, 0.0 },
                    XAxis = new double[] { 1.0, 0.0, 0.0 }, YAxis = new double[] { 0.0, 0.0, 1.0 }
                }, legacySketchIds);
            AddLegacyPlaneCase(reportPath, evidence, cases, defects, adapter, binding, testPart,
                "F01_legacy_YZ_regression", "YZ", new Frame
                {
                    Origin = new double[] { 0.0, 0.0, 0.0 }, Normal = new double[] { 1.0, 0.0, 0.0 },
                    XAxis = new double[] { 0.0, 1.0, 0.0 }, YAxis = new double[] { 0.0, 0.0, 1.0 }
                }, legacySketchIds);

            RunCase(reportPath, evidence, cases, defects, "F01F02_saved_copy_reopen_and_association_roundtrip",
                "Save a new independent .prt copy through the production adapter, reopen it, and recheck native DatumPlane and supported sketch parent association.",
                new string[] {
                    "Call M2OpenModelAdapter.SaveCopy with a fresh workspace/v2/models target and reopen=true.",
                    "Confirm SAVED, reopened=true, and all production roundtrip checks report VERIFIED.",
                    "Re-resolve the datum plane feature and sketch by JournalIdentifier after reopening.",
                    "Recheck native feature readback, feature inventory definition, sketch support evidence, and GetParents()."
                },
                "SaveCopy must return SAVED and reopened=true; roundtrip checks must all be VERIFIED; the DatumPlane must remain native with the same JournalIdentifier and frame; the supported sketch feature must retain the DatumPlane feature as a native parent; InspectSketch must still report DATUM_PLANE support for that ID.",
                delegate(Dictionary<string, object> record)
                {
                    M2SaveCopyResult roundtrip = adapter.SaveCopy(binding, savedPath, true);
                    record["save_copy"] = SaveCopyRecord(roundtrip);
                    Require(roundtrip != null && roundtrip.State == "SAVED" && roundtrip.Reopened,
                        "Production SaveCopy did not save and reopen the isolated native part.");
                    Require(ChecksPass(roundtrip.Checks), "SaveCopy roundtrip checks did not all report VERIFIED.");
                    evidence["saved_copy_created"] = File.Exists(savedPath);
                    testPart = session.Parts.Work;
                    Require(testPart != null && String.Equals(Path.GetFullPath(testPart.FullPath), Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase),
                        "The reopened Work Part path is not the unique SaveCopy target.");
                    Dictionary<string, object> nativePlane = ReadNativeDatumPlane(testPart, datumPlaneId);
                    record["reopened_native_datum_plane"] = nativePlane;
                    RequireDatumPlaneReadback(nativePlane, datumFrame);
                    Dictionary<string, object> inventory = adapter.InspectPart(binding);
                    record["reopened_part_inventory"] = inventory;
                    RequireFeatureRecord(inventory, datumPlaneId, "datum_plane");
                    Sketch supportedSketch = FindSketch(testPart, datumSketchId);
                    List<string> parents = FeatureAncestorIds(supportedSketch.Feature);
                    record["reopened_datum_sketch_parent_feature_ids"] = parents;
                    Require(parents.Contains(datumPlaneId), "DatumPlane feature is no longer a native immediate parent of the sketch after reopen.");
                    Dictionary<string, object> inspectedSketch = adapter.InspectSketch(binding, datumSketchId);
                    record["reopened_datum_sketch"] = inspectedSketch;
                    RequireDictionaryPropertyValue(inspectedSketch, "support_kind", "DATUM_PLANE");
                    RequireDictionaryPropertyValue(inspectedSketch, "datum_plane_id", datumPlaneId);
                    RequireSketchGeometry(supportedSketch, datumFrame,
                        new List<M3SketchGeometryRequest> { LineGeometry("datum-line", 2.0, 3.0, 8.0, 3.0), CircleGeometry("datum-circle", 5.0, -4.0, 6.0) },
                        record);
                    if (!String.IsNullOrWhiteSpace(emptySketchId))
                    {
                        Sketch empty = FindSketch(testPart, emptySketchId);
                        RequireFrame(empty, freeFrame, record, "reopened_empty_sketch_frame");
                        Require(CountCurveObjects(empty) == 0, "Empty sketch gained curves after save/reopen.");
                    }
                    if (!String.IsNullOrWhiteSpace(freeSketchId))
                    {
                        Sketch free = FindSketch(testPart, freeSketchId);
                        RequireSketchGeometry(free, freeFrame,
                            new List<M3SketchGeometryRequest> { LineGeometry("free-line", 2.0, 3.0, 8.0, 3.0), CircleGeometry("free-circle", 5.0, -4.0, 6.0) },
                            record);
                    }
                    if (!String.IsNullOrWhiteSpace(faceSketchId))
                    {
                        Sketch faceSketch = FindSketch(testPart, faceSketchId);
                        RequireSketchGeometry(faceSketch, faceTestFrame,
                            new List<M3SketchGeometryRequest> { RectangleGeometry("face-rectangle", 20.0, -10.0, 15.0, 10.0) },
                            record);
                        Dictionary<string, object> faceInspection = adapter.InspectSketch(binding, faceSketchId);
                        record["reopened_face_sketch"] = faceInspection;
                        RequireDictionaryPropertyValue(faceInspection, "support_kind", "FACE");
                        RequireAnyDictionaryPropertyValue(faceInspection,
                            new string[] { "support_face_id" }, supportFaceId);
                    }
                });

            RunCase(reportPath, evidence, cases, defects, "F01_native_datum_support_revision_invalidation",
                "Change the test DatumPlane through a native builder, verify an old plan and old verification are invalidated, then undo and confirm the original model recovers.",
                new string[] {
                    "Prepare a datum-supported sketch plan and retain the already-applied/verified datum-supported sketch plan.",
                    "Move the native DatumPlane origin by 2 mm using NXOpen DatumPlaneBuilder inside one undo mark.",
                    "Capture the model revision, attempt to execute the old prepared plan, and verify the old applied plan.",
                    "Undo the builder edit, re-read the native plane, parent dependency, and the original plan verification."
                },
                "The datum edit must change the captured revision; the unexecuted old plan must return UNCHANGED; the old applied plan must not verify while the support is changed; after undo, native plane origin/frame and sketch parent must recover and the original plan must verify again.",
                delegate(Dictionary<string, object> record)
                {
                    Require(datumSketchPlan != null && !String.IsNullOrWhiteSpace(datumPlaneId),
                        "No applied DatumPlane-supported sketch plan is available for revision invalidation.");

                    List<M3SketchGeometryRequest> anchorGeometry = new List<M3SketchGeometryRequest>
                    {
                        LineGeometry("revision-anchor-line", 2.0, 3.0, 8.0, 3.0)
                    };
                    M3SketchRequest anchorRequest = new M3SketchRequest
                    {
                        RevisionId = binding.RevisionId,
                        SupportDatumPlaneId = datumPlaneId,
                        XAxis = Copy(datumFrame.XAxis),
                        Geometries = anchorGeometry
                    };
                    Sketch anchorSketch = RunSketch(adapter, binding, testPart, anchorRequest,
                        datumFrame, anchorGeometry, record, out datumSketchPlan);
                    datumSketchId = anchorSketch.JournalIdentifier;
                    List<string> anchorParents = FeatureAncestorIds(anchorSketch.Feature);
                    record["fresh_anchor_sketch_id"] = datumSketchId;
                    record["fresh_anchor_parent_feature_ids"] = anchorParents;
                    Require(anchorParents.Contains(datumPlaneId), "Fresh anchor sketch does not depend on the native DatumPlane feature.");
                    M3VerificationResult baselineVerification = adapter.VerifyPlan(binding, datumSketchPlan);
                    record["fresh_plan_verification_before_edit"] = VerificationRecord(baselineVerification);
                    Require(baselineVerification != null && baselineVerification.Verified && ChecksPass(baselineVerification.Checks),
                        "Fresh native DatumPlane-supported sketch plan did not verify before the edit.");

                    string revisionBefore = binding.RevisionId;
                    Dictionary<string, object> beforePrepare = ModelDefinitionRecord(adapter, binding, testPart);
                    M3SketchRequest staleRequest = new M3SketchRequest
                    {
                        RevisionId = binding.RevisionId,
                        SupportDatumPlaneId = datumPlaneId,
                        XAxis = Copy(datumFrame.XAxis),
                        Geometries = new List<M3SketchGeometryRequest> { LineGeometry("stale-plan-line", 2.0, 3.0, 8.0, 3.0) }
                    };
                    M3PlanSnapshot stalePlan = adapter.PrepareSketch(binding, staleRequest);
                    Dictionary<string, object> afterPrepare = ModelDefinitionRecord(adapter, binding, testPart);
                    RequireUnchangedPrepare(record, beforePrepare, afterPrepare);
                    record["old_plan_public_view"] = stalePlan.PublicView;
                    record["revision_before_edit"] = revisionBefore;
                    Dictionary<string, object> nativeBefore = ReadNativeDatumPlane(testPart, datumPlaneId);
                    record["native_datum_plane_before_edit"] = nativeBefore;

                    Session.UndoMarkId mark = session.SetUndoMark(Session.MarkVisibility.Invisible,
                        "F01/F02 acceptance datum support revision test");
                    bool markActive = true;
                    try
                    {
                        Dictionary<string, object> edited = MoveDatumPlane(testPart, datumPlaneId, 2.0, mark, session);
                        record["native_datum_plane_after_edit"] = edited;
                        Dictionary<string, object> changedPart = adapter.InspectPart(binding);
                        record["part_after_datum_edit"] = changedPart;
                        record["revision_after_edit"] = binding.RevisionId;
                        Require(!String.Equals(revisionBefore, binding.RevisionId, StringComparison.Ordinal),
                            "Changing the native DatumPlane did not advance the adapter's captured model revision.");

                        M3ExecutionResult staleExecution = adapter.ExecutePlan(binding, stalePlan);
                        record["old_plan_execution"] = ExecutionRecord(staleExecution);
                        Require(staleExecution != null && staleExecution.State == "UNCHANGED",
                            "Old prepared sketch plan executed or left the model outcome uncertain after its support changed.");

                        bool oldVerificationStillPassed = false;
                        try
                        {
                            M3VerificationResult changedVerification = adapter.VerifyPlan(binding, datumSketchPlan);
                            record["old_applied_plan_verification_after_edit"] = VerificationRecord(changedVerification);
                            oldVerificationStillPassed = changedVerification != null && changedVerification.Verified;
                        }
                        catch (Exception verifyError)
                        {
                            record["old_applied_plan_verification_exception_after_edit"] = verifyError.ToString();
                        }
                        Require(!oldVerificationStillPassed,
                            "The applied sketch plan continued to verify after its native DatumPlane support changed.");
                    }
                    finally
                    {
                        if (markActive)
                        {
                            session.UndoToMark(mark, "F01/F02 acceptance datum support revision test");
                            session.DeleteUndoMark(mark, "F01/F02 acceptance datum support revision test");
                            markActive = false;
                        }
                    }

                    Dictionary<string, object> restoredPart = adapter.InspectPart(binding);
                    record["part_after_undo"] = restoredPart;
                    record["revision_after_undo"] = binding.RevisionId;
                    Dictionary<string, object> nativeRestored = ReadNativeDatumPlane(testPart, datumPlaneId);
                    record["native_datum_plane_after_undo"] = nativeRestored;
                    RequireDatumPlaneReadback(nativeRestored, datumFrame);
                    Sketch supported = FindSketch(testPart, datumSketchId);
                    List<string> restoredParents = FeatureAncestorIds(supported.Feature);
                    record["datum_sketch_parent_ids_after_undo"] = restoredParents;
                    Require(restoredParents.Contains(datumPlaneId), "Undo did not restore the native DatumPlane parent dependency.");
                    M3VerificationResult restoredVerification = adapter.VerifyPlan(binding, datumSketchPlan);
                    record["original_plan_verification_after_undo"] = VerificationRecord(restoredVerification);
                    Require(restoredVerification != null && restoredVerification.Verified && ChecksPass(restoredVerification.Checks),
                        "The previously applied sketch plan did not verify again after the native DatumPlane edit was undone.");
                });

            evidence["all_cases_passed"] = true;
        }
        catch (Exception ex)
        {
            evidence["error_case"] = evidence.ContainsKey("current_case") ? evidence["current_case"] : null;
            evidence["error"] = ex.ToString();
            evidence["passed"] = false;
            if (!evidence.ContainsKey("current_case"))
            {
                defects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "test_case", "setup_or_harness" },
                    { "reproduction_steps", new string[] {
                        "Build the production NX adapter and offline acceptance DLL.",
                        "Run RunAcceptance.cs with the configured run_journal.exe in an idle NX session.",
                        "Review the harness exception recorded at the top level of this report."
                    } },
                    { "observed_exception", ex.ToString() },
                    { "retest_criterion", "Correct the reported setup/harness issue, preserve a clean idle NX session, and rerun the complete acceptance on a new unique model path." }
                });
            }
        }
        finally
        {
            try
            {
                if (binding != null && adapter != null) adapter.Release(binding);
            }
            catch (Exception ex) { evidence["release_error"] = ex.ToString(); }

            try
            {
                if (session != null)
                {
                    List<Part> ownedParts = FindOwnedParts(session, sourcePath, savedPath);
                    if (originalPart != null)
                    {
                        PartLoadStatus displayStatus;
                        session.Parts.SetDisplay(originalPart, false, false, out displayStatus);
                        if (displayStatus != null) displayStatus.Dispose();
                        session.Parts.SetWork(originalPart);
                        foreach (Part owned in ownedParts)
                        {
                            if (owned.Tag == originalPart.Tag) continue;
                            try { owned.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null); }
                            catch (Exception closeError) { AddRestoreError(evidence, closeError); }
                        }
                        evidence["original_part_restored"] = session.Parts.Work != null &&
                            session.Parts.Work.Tag == originalPart.Tag && !originalPart.IsModified;
                    }
                    else
                    {
                        foreach (Part owned in ownedParts)
                        {
                            try { owned.Close(BasePart.CloseWholeTree.False, BasePart.CloseModified.CloseModified, null); }
                            catch (Exception closeError) { AddRestoreError(evidence, closeError); }
                        }
                        evidence["empty_original_session_restored"] = session.Parts.Work == null;
                    }
                }
            }
            catch (Exception ex) { AddRestoreError(evidence, ex); }

            evidence["finished_utc"] = evidence.ContainsKey("finished_utc")
                ? evidence["finished_utc"] : DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            evidence["final_work_part_path"] = session == null || session.Parts.Work == null ? null : session.Parts.Work.FullPath;
            bool allCasesPassed = cases.Count > 0;
            foreach (object item in cases)
            {
                Dictionary<string, object> caseRecord = item as Dictionary<string, object>;
                if (caseRecord == null || !caseRecord.ContainsKey("status") ||
                    !String.Equals(Convert.ToString(caseRecord["status"], CultureInfo.InvariantCulture), "PASSED", StringComparison.Ordinal))
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

    private static Dictionary<string, object> CreateTiltedCylinder(Part part)
    {
        double invRoot3 = 1.0 / Math.Sqrt(3.0);
        double[] origin = new double[] { 500.0, 0.0, 0.0 };
        double[] direction = new double[] { invRoot3, invRoot3, invRoot3 };
        Tag feature;
        UFSession.GetUFSession().Modl.CreateCylinder(FeatureSigns.Nullsign, Tag.Null, origin,
            "40", "200", direction, out feature);
        Tag bodyTag;
        UFSession.GetUFSession().Modl.AskFeatBody(feature, out bodyTag);
        NXObject featureObject = (NXObject)NXOpen.Utilities.NXObjectManager.Get(feature);
        Body body = NXOpen.Utilities.NXObjectManager.Get(bodyTag) as Body;
        Require(featureObject != null && body != null, "UF cylinder fixture did not return a native feature and Body.");
        Require(body.IsSolidBody, "Tilted cylinder fixture is not a solid Body.");
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "feature_id", featureObject.JournalIdentifier },
            { "body_id", body.JournalIdentifier },
            { "origin_mm", origin },
            { "axis_unit", direction },
            { "diameter_mm", 200.0 },
            { "length_mm", 40.0 },
            { "purpose", "large planar cap for the non-axis-aligned Face acceptance" }
        };
    }

    private static void AddLegacyPlaneCase(string reportPath, Dictionary<string, object> evidence,
        List<object> cases, List<object> defects, M2OpenModelAdapter adapter, M2ModelBinding binding,
        Part part, string name, string plane, Frame expected, Dictionary<string, string> sketchIds)
    {
        RunCase(reportPath, evidence, cases, defects, name,
            "Preserve one of the previously supported principal-plane sketch modes.",
            new string[] {
                "Prepare a sketch with plane='" + plane + "', origin [0,0,0] mm, and one line.",
                "Execute and verify through the production adapter.",
                "Read NXOpen sketch origin, orientation, and line endpoints."
            },
            "The sketch must be APPLIED and verified, retain the expected legacy plane orientation, and place both line endpoints within 1e-6 mm.",
            delegate(Dictionary<string, object> record)
            {
                List<M3SketchGeometryRequest> geometries = new List<M3SketchGeometryRequest>
                {
                    LineGeometry(plane.ToLowerInvariant() + "-line", 1.0, 2.0, 6.0, 2.0)
                };
                M3SketchRequest request = new M3SketchRequest
                {
                    RevisionId = binding.RevisionId,
                    Plane = plane,
                    OriginMm = new double[] { 0.0, 0.0, 0.0 },
                    Geometries = geometries
                };
                M3PlanSnapshot plan;
                Sketch sketch = RunSketch(adapter, binding, part, request, expected, geometries, record, out plan);
                sketchIds[plane] = sketch.JournalIdentifier;
            });
    }

    private static Sketch RunSketch(M2OpenModelAdapter adapter, M2ModelBinding binding, Part part,
        M3SketchRequest request, Frame expectedFrame, List<M3SketchGeometryRequest> geometries,
        Dictionary<string, object> record, out M3PlanSnapshot plan)
    {
        Dictionary<string, object> beforePrepare = ModelDefinitionRecord(adapter, binding, part);
        plan = adapter.PrepareSketch(binding, request);
        Dictionary<string, object> afterPrepare = ModelDefinitionRecord(adapter, binding, part);
        RequireUnchangedPrepare(record, beforePrepare, afterPrepare);
        record["prepare_public_view"] = plan.PublicView;
        Require(plan != null && plan.Kind == "create_sketch", "PrepareSketch returned an invalid plan.");
        M3ExecutionResult execution = adapter.ExecutePlan(binding, plan);
        record["execution"] = ExecutionRecord(execution);
        Require(execution != null && execution.State == "APPLIED",
            "Sketch execution was not APPLIED (" + (execution == null ? "null result" : execution.ErrorCode + ": " + execution.ErrorMessage) + ").");
        string sketchId = ReadRequiredString(execution.Result, "sketch_id");
        record["sketch_id"] = sketchId;
        M3VerificationResult verification = adapter.VerifyPlan(binding, plan);
        record["verification"] = VerificationRecord(verification);
        Require(verification != null && verification.Verified && ChecksPass(verification.Checks),
            "Sketch verification did not pass all checks.");
        Sketch sketch = FindSketch(part, sketchId);
        RequireFrame(sketch, expectedFrame, record, "direct_nx_frame");
        RequireSketchGeometry(sketch, expectedFrame, geometries, record);
        Dictionary<string, object> inspected = adapter.InspectSketch(binding, sketchId);
        record["inspect_sketch"] = inspected;
        RequireFrameEvidence(inspected, expectedFrame);
        return sketch;
    }

    private static void RequireSketchGeometry(Sketch sketch, Frame expectedFrame,
        List<M3SketchGeometryRequest> geometries, Dictionary<string, object> record)
    {
        RequireFrame(sketch, expectedFrame, record, "direct_nx_frame");
        NXObject[] objects = sketch.GetAllGeometry();
        Require(objects != null, "NXOpen Sketch.GetAllGeometry returned null.");
        List<Line> actualLines = new List<Line>();
        List<Arc> actualArcs = new List<Arc>();
        List<object> raw = new List<object>();
        foreach (NXObject item in objects)
        {
            if (item == null) throw new InvalidOperationException("NXOpen Sketch.GetAllGeometry contains null.");
            Line line = item as Line;
            Arc arc = item as Arc;
            if (line != null)
            {
                actualLines.Add(line);
                raw.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "kind", "line" }, { "journal_id", line.JournalIdentifier },
                    { "start_world_mm", Point(line.StartPoint) }, { "end_world_mm", Point(line.EndPoint) }
                });
            }
            else if (arc != null)
            {
                actualArcs.Add(arc);
                raw.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "kind", "arc" }, { "journal_id", arc.JournalIdentifier },
                    { "center_world_mm", Point(arc.CenterPoint) }, { "radius_mm", arc.Radius },
                    { "sweep_radians", Math.Abs(arc.EndAngle - arc.StartAngle) }
                });
            }
            else if (item is Curve)
                throw new InvalidOperationException("NXOpen sketch contains an unsupported curve type " + item.GetType().FullName + ".");
        }
        record["direct_nx_geometry"] = raw;
        int expectedLines = 0;
        int expectedArcs = 0;
        foreach (M3SketchGeometryRequest geometry in geometries)
        {
            if (geometry.Type == "line") expectedLines++;
            else if (geometry.Type == "rectangle") expectedLines += 4;
            else if (geometry.Type == "circle") expectedArcs++;
            else throw new InvalidOperationException("Test geometry type is outside the authorized line/rectangle/circle scope.");
        }
        Require(actualLines.Count == expectedLines,
            "NXOpen line count differs from the requested line/rectangle geometry count.");
        Require(actualArcs.Count == expectedArcs,
            "NXOpen full-circle/arc count differs from the requested circle geometry count.");
        bool[] usedLines = new bool[actualLines.Count];
        bool[] usedArcs = new bool[actualArcs.Count];
        List<object> expectedWorldGeometry = new List<object>();
        foreach (M3SketchGeometryRequest geometry in geometries)
        {
            if (geometry.Type == "line")
            {
                Point3d a = WorldPoint(expectedFrame, geometry.StartMm[0], geometry.StartMm[1]);
                Point3d b = WorldPoint(expectedFrame, geometry.EndMm[0], geometry.EndMm[1]);
                expectedWorldGeometry.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "geometry_id", geometry.Id }, { "kind", "line" },
                    { "start_world_mm", Point(a) }, { "end_world_mm", Point(b) }
                });
                MatchLine(actualLines, usedLines, a, b, geometry.Id);
            }
            else if (geometry.Type == "rectangle")
            {
                double x = geometry.CornerMm[0];
                double y = geometry.CornerMm[1];
                Point3d[] corners = new Point3d[] {
                    WorldPoint(expectedFrame, x, y),
                    WorldPoint(expectedFrame, x + geometry.WidthMm, y),
                    WorldPoint(expectedFrame, x + geometry.WidthMm, y + geometry.HeightMm),
                    WorldPoint(expectedFrame, x, y + geometry.HeightMm)
                };
                expectedWorldGeometry.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "geometry_id", geometry.Id }, { "kind", "rectangle" },
                    { "corners_world_mm", new List<object> { Point(corners[0]), Point(corners[1]), Point(corners[2]), Point(corners[3]) } }
                });
                for (int i = 0; i < 4; i++) MatchLine(actualLines, usedLines, corners[i], corners[(i + 1) % 4], geometry.Id + "-edge-" + i.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                Point3d center = WorldPoint(expectedFrame, geometry.CenterMm[0], geometry.CenterMm[1]);
                expectedWorldGeometry.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "geometry_id", geometry.Id }, { "kind", "circle" },
                    { "center_world_mm", Point(center) }, { "radius_mm", geometry.DiameterMm / 2.0 },
                    { "diameter_mm", geometry.DiameterMm }
                });
                MatchCircle(actualArcs, usedArcs, center, geometry.DiameterMm / 2.0, geometry.Id);
            }
        }
        record["expected_world_geometry"] = expectedWorldGeometry;
    }

    private static int CountCurves(Sketch sketch, Dictionary<string, object> record)
    {
        int count = CountCurveObjects(sketch);
        List<string> types = new List<string>();
        foreach (NXObject item in sketch.GetAllGeometry()) if (item is Curve) types.Add(item.GetType().Name);
        record["curve_object_types"] = types;
        return count;
    }

    private static int CountCurveObjects(Sketch sketch)
    {
        int count = 0;
        NXObject[] objects = sketch.GetAllGeometry();
        Require(objects != null, "NXOpen Sketch.GetAllGeometry returned null.");
        foreach (NXObject item in objects) if (item is Curve) count++;
        return count;
    }

    private static void RequireFrame(Sketch sketch, Frame expected, Dictionary<string, object> record, string key)
    {
        Point3d origin = sketch.Origin;
        Matrix3x3 matrix = sketch.Orientation.Element;
        Frame actual = new Frame
        {
            Origin = new double[] { origin.X, origin.Y, origin.Z },
            XAxis = new double[] { matrix.Xx, matrix.Xy, matrix.Xz },
            YAxis = new double[] { matrix.Yx, matrix.Yy, matrix.Yz },
            Normal = new double[] { matrix.Zx, matrix.Zy, matrix.Zz }
        };
        record[key] = FrameRecord(actual);
        Require(VectorDistance(actual.Origin, expected.Origin) <= PositionToleranceMm,
            "NXOpen Sketch origin differs from the independently calculated requested origin.");
        Require(VectorDistance(actual.XAxis, expected.XAxis) <= FrameComponentTolerance,
            "NXOpen Sketch X axis differs from the independently projected and normalized x_axis.");
        Require(VectorDistance(actual.YAxis, expected.YAxis) <= FrameComponentTolerance,
            "NXOpen Sketch Y axis is not the expected right-handed normal cross x_axis.");
        Require(VectorDistance(actual.Normal, expected.Normal) <= FrameComponentTolerance,
            "NXOpen Sketch normal differs from the independently normalized normal.");
        Require(Math.Abs(Dot(Cross(actual.XAxis, actual.YAxis), actual.Normal) - 1.0) <= FrameComponentTolerance,
            "NXOpen Sketch frame is not right-handed and orthonormal.");
    }

    private static Frame ReadSketchFrame(Sketch sketch)
    {
        Point3d origin = sketch.Origin;
        Matrix3x3 matrix = sketch.Orientation.Element;
        return new Frame
        {
            Origin = new double[] { origin.X, origin.Y, origin.Z },
            XAxis = new double[] { matrix.Xx, matrix.Xy, matrix.Xz },
            YAxis = new double[] { matrix.Yx, matrix.Yy, matrix.Yz },
            Normal = new double[] { matrix.Zx, matrix.Zy, matrix.Zz }
        };
    }

    private static void RequireFrameEvidence(Dictionary<string, object> inspected, Frame expected)
    {
        RequireVectorEvidence(inspected, new string[] { "origin_mm", "origin_world_mm", "origin" }, expected.Origin, PositionToleranceMm,
            "InspectSketch omitted or mismatched world frame origin.");
        RequireVectorEvidence(inspected, new string[] { "normal", "normal_world" }, expected.Normal, FrameComponentTolerance,
            "InspectSketch omitted or mismatched world frame normal.");
        RequireVectorEvidence(inspected, new string[] { "x_axis", "x_axis_world" }, expected.XAxis, FrameComponentTolerance,
            "InspectSketch omitted or mismatched world x_axis.");
    }

    private static void RequireDatumPlaneReadback(Dictionary<string, object> nativeReadback, Frame expected)
    {
        double[] origin = ReadVector(nativeReadback, "origin_mm", 3);
        double[] normal = Normalize(ReadVector(nativeReadback, "normal_world", 3));
        double[] xAxis = Normalize(ReadVector(nativeReadback, "x_axis_world", 3));
        double[] yAxis = Normalize(ReadVector(nativeReadback, "y_axis_world", 3));
        Require(VectorDistance(origin, expected.Origin) <= PositionToleranceMm, "Native DatumPlane origin differs from its requested world origin.");
        Require(VectorDistance(normal, expected.Normal) <= FrameComponentTolerance, "Native DatumPlane normal differs from the normalized request.");
        Require(VectorDistance(xAxis, expected.XAxis) <= FrameComponentTolerance, "Native DatumPlane x_axis differs from the projected normalized request.");
        Require(VectorDistance(yAxis, expected.YAxis) <= FrameComponentTolerance, "Native DatumPlane y_axis is not the right-handed normal cross x_axis.");
        Require(Math.Abs(Dot(Cross(xAxis, yAxis), normal) - 1.0) <= FrameComponentTolerance,
            "Native DatumPlane frame is not right-handed and orthonormal.");
    }

    private static Dictionary<string, object> ReadNativeDatumPlane(Part part, string featureId)
    {
        Feature feature = FindFeature(part, featureId);
        NXObject[] entities = feature.GetEntities();
        Require(entities != null, "DatumPlane feature returned no entity array.");
        DatumPlane datum = null;
        foreach (NXObject entity in entities)
        {
            datum = entity as DatumPlane;
            if (datum != null) break;
        }
        Require(datum != null, "Feature JournalIdentifier does not resolve to a native NXOpen.DatumPlane entity.");
        Point3d p0, p1, p2, p3;
        datum.GetCornerPoints(out p0, out p1, out p2, out p3);
        double[] x = Normalize(Subtract(Point(p1), Point(p0)));
        double[] y = Normalize(Subtract(Point(p3), Point(p0)));
        Vector3d normal = datum.Normal;
        Point3d origin = datum.Origin;
        Feature ownerFeature = datum.Feature;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "datum_feature_id", feature.JournalIdentifier },
            { "datum_entity_feature_id", ownerFeature == null ? null : ownerFeature.JournalIdentifier },
            { "entity_type", datum.GetType().FullName },
            { "origin_mm", Point(origin) },
            { "normal_world", new double[] { normal.X, normal.Y, normal.Z } },
            { "x_axis_world", x },
            { "y_axis_world", y },
            { "corner_points_world_mm", new List<object> { Point(p0), Point(p1), Point(p2), Point(p3) } },
            { "feature_parent_ids", FeatureIds(feature.GetParents()) },
            { "feature_child_ids", FeatureIds(feature.GetChildren()) }
        };
    }

    private static Dictionary<string, object> MoveDatumPlane(Part part, string featureId,
        double xShiftMm, Session.UndoMarkId mark, Session session)
    {
        Feature feature = FindFeature(part, featureId);
        Dictionary<string, object> before = ReadNativeDatumPlane(part, featureId);
        double[] oldOrigin = ReadVector(before, "origin_mm", 3);
        double[] normal = Normalize(ReadVector(before, "normal_world", 3));
        double[] xAxis = Normalize(ReadVector(before, "x_axis_world", 3));
        double[] yAxis = Normalize(ReadVector(before, "y_axis_world", 3));
        double[] movedOrigin = new double[] { oldOrigin[0] + xShiftMm, oldOrigin[1], oldOrigin[2] };
        Point3d origin = new Point3d(movedOrigin[0], movedOrigin[1], movedOrigin[2]);
        Matrix3x3 orientation = new Matrix3x3();
        orientation.Xx = xAxis[0]; orientation.Xy = xAxis[1]; orientation.Xz = xAxis[2];
        orientation.Yx = yAxis[0]; orientation.Yy = yAxis[1]; orientation.Yz = yAxis[2];
        orientation.Zx = normal[0]; orientation.Zy = normal[1]; orientation.Zz = normal[2];
        DatumPlaneBuilder builder = part.Features.CreateDatumPlaneBuilder(feature);
        Feature updated;
        try
        {
            Plane editPlane = builder.GetPlane();
            Require(editPlane != null, "DatumPlaneBuilder.GetPlane returned null for the native datum feature.");
            editPlane.SetMethod(PlaneTypes.MethodType.Fixed);
            editPlane.EditFixedPlane(origin, orientation);
            editPlane.Evaluate();
            updated = builder.CommitFeature();
        }
        finally { builder.Destroy(); }
        Require(updated != null && String.Equals(updated.JournalIdentifier, featureId, StringComparison.Ordinal),
            "NX DatumPlane edit did not preserve the native feature JournalIdentifier.");
        int updateErrors = session.UpdateManager.DoUpdate(mark);
        Require(updateErrors == 0, "NX reported update errors after the DatumPlane edit.");
        Dictionary<string, object> after = ReadNativeDatumPlane(part, featureId);
        Require(VectorDistance(ReadVector(after, "origin_mm", 3), movedOrigin) <= PositionToleranceMm,
            "Direct NXOpen readback did not show the requested DatumPlane edit.");
        after["requested_x_shift_mm"] = xShiftMm;
        after["update_error_count"] = updateErrors;
        return after;
    }

    private static Dictionary<string, object> ReadFaceDirect(Part part, string bodyId, string faceId)
    {
        Face face = FindFace(part, bodyId, faceId);
        int type;
        double[] point = new double[3];
        double[] direction = new double[3];
        double[] bbox = new double[6];
        double radius, radiusData;
        int normalDirection;
        UFSession.GetUFSession().Modl.AskFaceData(face.Tag, out type, point, direction,
            bbox, out radius, out radiusData, out normalDirection);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "face_journal_id", face.JournalIdentifier },
            { "body_journal_id", ((Body)face.GetBody()).JournalIdentifier },
            { "face_tag", face.Tag.ToString() },
            { "uf_face_type", type },
            { "plane_point_mm", point },
            { "normal_world", direction },
            { "bbox_mm", bbox },
            { "normal_direction", normalDirection }
        };
    }

    private static Dictionary<string, object> SelectTiltedCylinderCap(List<object> candidates)
    {
        Dictionary<string, object> selected = null;
        double invRoot3 = 1.0 / Math.Sqrt(3.0);
        double[] expectedAxis = new double[] { invRoot3, invRoot3, invRoot3 };
        foreach (object item in candidates)
        {
            Dictionary<string, object> candidate = item as Dictionary<string, object>;
            if (candidate == null) continue;
            if (!String.Equals(ReadOptionalString(candidate, "surface_type"), "planar", StringComparison.Ordinal)) continue;
            object boundary;
            if (!candidate.TryGetValue("boundary_supported", out boundary) || !(boundary is bool) || !(bool)boundary) continue;
            double[] normal;
            try { normal = Normalize(ReadVector(candidate, "normal_world", 3)); }
            catch { continue; }
            if (MaxAbs(normal) >= 0.95 || Dot(normal, expectedAxis) < 1.0 - 1e-6) continue;
            if (selected != null) throw new InvalidOperationException("The tilted cylinder fixture returned more than one positive non-axis planar cap candidate.");
            selected = candidate;
        }
        Require(selected != null, "The all_planar query did not return a supported non-axis-aligned positive cylinder cap.");
        return selected;
    }

    private static Dictionary<string, object> ModelIdRecord(Part part)
    {
        List<string> features = new List<string>();
        foreach (Feature item in part.Features.GetFeatures()) if (item != null) features.Add(item.JournalIdentifier);
        List<string> sketches = new List<string>();
        foreach (Sketch item in part.Sketches.ToArray()) if (item != null) sketches.Add(item.JournalIdentifier);
        List<string> bodies = new List<string>();
        foreach (Body item in part.Bodies.ToArray()) if (item != null) bodies.Add(item.JournalIdentifier);
        features.Sort(StringComparer.Ordinal); sketches.Sort(StringComparer.Ordinal); bodies.Sort(StringComparer.Ordinal);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "features", features }, { "sketches", sketches }, { "bodies", bodies }, { "is_modified", part.IsModified }
        };
    }

    private static string ModelFingerprint(Part part, M2ModelBinding binding)
    {
        Dictionary<string, object> record = ModelIdRecord(part);
        record["binding_revision"] = binding.RevisionId;
        return M2Json.CanonicalText(record);
    }

    private static Dictionary<string, object> ModelDefinitionRecord(M2OpenModelAdapter adapter,
        M2ModelBinding binding, Part part)
    {
        Dictionary<string, object> inspected = adapter.InspectPart(binding);
        Dictionary<string, object> definition = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "binding_revision", binding.RevisionId },
            { "direct_nx_ids", ModelIdRecord(part) }
        };
        string[] keys = new string[] { "identity", "part", "features", "sketches", "bodies", "expressions", "datum_planes" };
        foreach (string key in keys)
        {
            object value;
            if (inspected.TryGetValue(key, out value)) definition[key] = value;
        }
        return definition;
    }

    private static void RequireUnchangedPrepare(Dictionary<string, object> record,
        Dictionary<string, object> before, Dictionary<string, object> after)
    {
        record["prepare_read_only_check"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "before", before }, { "after", after },
            { "unchanged", String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal) }
        };
        Require(String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal),
            "Valid prepare changed the model definition, feature/sketch/body identifiers, revision, or modified state.");
    }

    private static void RequireFeatureRecord(Dictionary<string, object> partInventory, string featureId, string definitionKey)
    {
        List<object> features = FindListValue(partInventory, "features");
        Require(features != null, "InspectPart did not expose the native feature inventory.");
        Dictionary<string, object> featureRecord = null;
        foreach (object item in features)
        {
            Dictionary<string, object> candidate = item as Dictionary<string, object>;
            if (candidate != null && RecordHasId(candidate, featureId)) { featureRecord = candidate; break; }
        }
        Require(featureRecord != null, "Part feature inventory does not contain datum_plane_id " + featureId + ".");
        Require(ContainsKeyRecursive(featureRecord, definitionKey),
            "DatumPlane feature inventory record does not include its native datum_plane definition.");
    }

    private static bool RecordHasId(Dictionary<string, object> record, string id)
    {
        string[] keys = new string[] { "journal_id", "feature_id", "datum_plane_id", "id" };
        foreach (string key in keys)
        {
            object value;
            if (record.TryGetValue(key, out value) && String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), id, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void RequireResultIdAndDefinition(Dictionary<string, object> result, string id, string definitionKey)
    {
        Require(result != null, "Datum plane result dictionary is missing.");
        RequireDictionaryPropertyValue(result, "datum_plane_id", id);
        Require(ContainsKeyRecursive(result, definitionKey), "Datum plane result is missing native definition readback.");
        Dictionary<string, object> plane = FindDictionary(result, definitionKey);
        Require(plane != null, "Datum plane result has no datum_plane object.");
        RequireAnyDictionaryPropertyValue(plane,
            new string[] { "id", "journal_id", "feature_id", "datum_plane_id" }, id);
    }

    private static bool ContainsKeyRecursive(object value, string key)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            if (dictionary.ContainsKey(key)) return true;
            foreach (object child in dictionary.Values) if (ContainsKeyRecursive(child, key)) return true;
        }
        List<object> list = value as List<object>;
        if (list != null) foreach (object child in list) if (ContainsKeyRecursive(child, key)) return true;
        return false;
    }

    private static bool ContainsPropertyValue(object value, string key, object expected)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            object actual;
            if (dictionary.TryGetValue(key, out actual) && ScalarEquals(actual, expected)) return true;
            foreach (object child in dictionary.Values) if (ContainsPropertyValue(child, key, expected)) return true;
        }
        List<object> list = value as List<object>;
        if (list != null) foreach (object child in list) if (ContainsPropertyValue(child, key, expected)) return true;
        return false;
    }

    private static bool ScalarEquals(object actual, object expected)
    {
        if (actual == null || expected == null) return actual == expected;
        return String.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture),
            Convert.ToString(expected, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireDictionaryPropertyValue(object value, string key, object expected)
    {
        Require(ContainsPropertyValue(value, key, expected),
            "Evidence does not contain " + key + "=" + Convert.ToString(expected, CultureInfo.InvariantCulture) + ".");
    }

    private static void RequireAnyDictionaryPropertyValue(object value, string[] keys, object expected)
    {
        foreach (string key in keys) if (ContainsPropertyValue(value, key, expected)) return;
        throw new InvalidOperationException("Evidence does not contain any of [" + String.Join(",", keys) + "]=" +
            Convert.ToString(expected, CultureInfo.InvariantCulture) + ".");
    }

    private static void RequireVectorEvidence(object root, string[] keys, double[] expected, double tolerance, string message)
    {
        foreach (string key in keys)
        {
            List<double[]> vectors = new List<double[]>();
            FindVectors(root, key, vectors);
            foreach (double[] vector in vectors)
                if (vector.Length == expected.Length && VectorDistance(vector, expected) <= tolerance) return;
        }
        throw new InvalidOperationException(message);
    }

    private static void FindVectors(object value, string key, List<double[]> found)
    {
        Dictionary<string, object> dictionary = value as Dictionary<string, object>;
        if (dictionary != null)
        {
            object child;
            if (dictionary.TryGetValue(key, out child))
            {
                double[] vector = AsVector(child);
                if (vector != null) found.Add(vector);
            }
            foreach (object nested in dictionary.Values) FindVectors(nested, key, found);
        }
        List<object> list = value as List<object>;
        if (list != null) foreach (object nested in list) FindVectors(nested, key, found);
    }

    private static double[] AsVector(object value)
    {
        double[] doubles = value as double[];
        if (doubles != null) return Copy(doubles);
        List<object> list = value as List<object>;
        if (list == null) return null;
        double[] vector = new double[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            try { vector[i] = Convert.ToDouble(list[i], CultureInfo.InvariantCulture); }
            catch { return null; }
        }
        return vector;
    }

    private static void MatchLine(List<Line> lines, bool[] used, Point3d expectedA, Point3d expectedB, string geometryId)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (used[i]) continue;
            Point3d a = lines[i].StartPoint;
            Point3d b = lines[i].EndPoint;
            if ((PointDistance(a, expectedA) <= PositionToleranceMm && PointDistance(b, expectedB) <= PositionToleranceMm) ||
                (PointDistance(a, expectedB) <= PositionToleranceMm && PointDistance(b, expectedA) <= PositionToleranceMm))
            {
                used[i] = true;
                return;
            }
        }
        throw new InvalidOperationException("NXOpen line endpoints do not match requested local-to-world geometry " + geometryId + ".");
    }

    private static void MatchCircle(List<Arc> arcs, bool[] used, Point3d expectedCenter, double expectedRadius, string geometryId)
    {
        for (int i = 0; i < arcs.Count; i++)
        {
            if (used[i]) continue;
            Arc arc = arcs[i];
            if (PointDistance(arc.CenterPoint, expectedCenter) <= PositionToleranceMm &&
                Math.Abs(arc.Radius - expectedRadius) <= PositionToleranceMm &&
                Math.Abs(Math.Abs(arc.EndAngle - arc.StartAngle) - 2.0 * Pi) <= 1e-7)
            {
                used[i] = true;
                return;
            }
        }
        throw new InvalidOperationException("NXOpen full circle center/radius does not match local-to-world geometry " + geometryId + ".");
    }

    private static void RunCase(string reportPath, Dictionary<string, object> evidence,
        List<object> cases, List<object> defects, string name, string objective,
        string[] reproductionSteps, string retestCriterion, CaseBody body)
    {
        Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", name }, { "objective", objective }, { "status", "RUNNING" },
            { "reproduction_steps", reproductionSteps }, { "retest_criterion", retestCriterion },
            { "started_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
        };
        cases.Add(record);
        evidence["current_case"] = name;
        Persist(reportPath, evidence);
        try
        {
            body(record);
            record["status"] = "PASSED";
            record["finished_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Persist(reportPath, evidence);
        }
        catch (Exception ex)
        {
            record["status"] = "FAILED";
            record["observed_exception"] = ex.ToString();
            record["finished_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            defects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "test_case", name }, { "reproduction_steps", reproductionSteps },
                { "observed_exception", ex.ToString() }, { "retest_criterion", retestCriterion }
            });
            evidence["passed"] = false;
            Persist(reportPath, evidence);
            throw;
        }
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

    private static bool ChecksPass(Dictionary<string, object> checks)
    {
        if (checks == null || checks.Count == 0) return false;
        foreach (object value in checks.Values)
        {
            Dictionary<string, object> check = value as Dictionary<string, object>;
            if (check == null || !check.ContainsKey("status") || !String.Equals(Convert.ToString(check["status"], CultureInfo.InvariantCulture), "VERIFIED", StringComparison.Ordinal))
                return false;
        }
        return true;
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

    private static List<string> FeatureAncestorIds(Feature feature)
    {
        List<string> result = new List<string>();
        Queue<Feature> pending = new Queue<Feature>();
        foreach (Feature parent in feature.GetParents()) if (parent != null) pending.Enqueue(parent);
        while (pending.Count != 0)
        {
            Feature parent = pending.Dequeue();
            if (result.Contains(parent.JournalIdentifier)) continue;
            result.Add(parent.JournalIdentifier);
            foreach (Feature ancestor in parent.GetParents()) if (ancestor != null) pending.Enqueue(ancestor);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static List<string> FeatureIds(Feature[] features)
    {
        List<string> result = new List<string>();
        if (features != null) foreach (Feature feature in features) if (feature != null) result.Add(feature.JournalIdentifier);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static List<Part> FindOwnedParts(Session session, string sourcePath, string savedPath)
    {
        List<Part> result = new List<Part>();
        foreach (BasePart loaded in session.Parts)
        {
            Part part = loaded as Part;
            if (part == null || String.IsNullOrWhiteSpace(part.FullPath)) continue;
            string full = Path.GetFullPath(part.FullPath);
            if (String.Equals(full, Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase) ||
                String.Equals(full, Path.GetFullPath(savedPath), StringComparison.OrdinalIgnoreCase)) result.Add(part);
        }
        return result;
    }

    private static Feature FindFeature(Part part, string featureId)
    {
        foreach (Feature feature in part.Features.GetFeatures())
            if (feature != null && String.Equals(feature.JournalIdentifier, featureId, StringComparison.Ordinal)) return feature;
        throw new InvalidOperationException("NXOpen feature JournalIdentifier could not be resolved: " + featureId);
    }

    private static Sketch FindSketch(Part part, string sketchId)
    {
        foreach (Sketch sketch in part.Sketches.ToArray())
            if (sketch != null && String.Equals(sketch.JournalIdentifier, sketchId, StringComparison.Ordinal)) return sketch;
        throw new InvalidOperationException("NXOpen sketch JournalIdentifier could not be resolved: " + sketchId);
    }

    private static Face FindFace(Part part, string bodyId, string faceId)
    {
        foreach (Body body in part.Bodies.ToArray())
        {
            if (body == null || !String.Equals(body.JournalIdentifier, bodyId, StringComparison.Ordinal)) continue;
            foreach (Face face in body.GetFaces())
                if (face != null && String.Equals(face.JournalIdentifier, faceId, StringComparison.Ordinal)) return face;
        }
        throw new InvalidOperationException("NXOpen face JournalIdentifier could not be resolved: " + faceId);
    }

    private static Dictionary<string, object> ReadDictionary(Dictionary<string, object> record, string key)
    {
        object value = null;
        Require(record != null && record.TryGetValue(key, out value), "Expected dictionary field is missing: " + key);
        Dictionary<string, object> result = value as Dictionary<string, object>;
        Require(result != null, "Expected dictionary field is not an object: " + key);
        return result;
    }

    private static string ReadRequiredString(Dictionary<string, object> record, string key)
    {
        object value = null;
        Require(record != null && record.TryGetValue(key, out value), "Expected string field is missing: " + key);
        string result = Convert.ToString(value, CultureInfo.InvariantCulture);
        Require(!String.IsNullOrWhiteSpace(result), "Required string field is empty: " + key);
        return result;
    }

    private static string ReadOptionalString(Dictionary<string, object> record, string key)
    {
        object value;
        return record != null && record.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    private static double[] ReadVector(Dictionary<string, object> record, string key, int length)
    {
        object value = null;
        Require(record != null && record.TryGetValue(key, out value), "Expected vector field is missing: " + key);
        double[] vector = AsVector(value);
        Require(vector != null && vector.Length == length, "Expected vector field has the wrong length: " + key);
        return vector;
    }

    private static M3SketchGeometryRequest LineGeometry(string id, double x1, double y1, double x2, double y2)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "line", StartMm = new double[] { x1, y1 }, EndMm = new double[] { x2, y2 } };
    }

    private static M3SketchGeometryRequest RectangleGeometry(string id, double x, double y, double width, double height)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "rectangle", CornerMm = new double[] { x, y }, WidthMm = width, HeightMm = height };
    }

    private static M3SketchGeometryRequest CircleGeometry(string id, double x, double y, double diameter)
    {
        return new M3SketchGeometryRequest { Id = id, Type = "circle", CenterMm = new double[] { x, y }, DiameterMm = diameter };
    }

    private static Frame BuildFrame(double[] origin, double[] normal, double[] xAxis)
    {
        double[] n = Normalize(normal);
        double[] x = Subtract(xAxis, Scale(n, Dot(xAxis, n)));
        x = Normalize(x);
        double[] y = Normalize(Cross(n, x));
        return new Frame { Origin = Copy(origin), Normal = n, XAxis = x, YAxis = y };
    }

    private static Dictionary<string, object> FrameRecord(Frame frame)
    {
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "origin_mm", frame.Origin }, { "normal_world", frame.Normal },
            { "x_axis_world", frame.XAxis }, { "y_axis_world", frame.YAxis }
        };
    }

    private static Point3d WorldPoint(Frame frame, double localX, double localY)
    {
        return new Point3d(frame.Origin[0] + frame.XAxis[0] * localX + frame.YAxis[0] * localY,
            frame.Origin[1] + frame.XAxis[1] * localX + frame.YAxis[1] * localY,
            frame.Origin[2] + frame.XAxis[2] * localX + frame.YAxis[2] * localY);
    }

    private static double[] Point(Point3d point) { return new double[] { point.X, point.Y, point.Z }; }
    private static double[] Point(Vector3d vector) { return new double[] { vector.X, vector.Y, vector.Z }; }

    private static double[] Normalize(double[] vector)
    {
        Require(vector != null && vector.Length == 3, "Frame vector must contain exactly 3 components.");
        double length = Math.Sqrt(Dot(vector, vector));
        Require(length > 1e-12 && !Double.IsNaN(length) && !Double.IsInfinity(length), "Frame vector is zero or non-finite.");
        return Scale(vector, 1.0 / length);
    }

    private static double Dot(double[] left, double[] right)
    {
        return left[0] * right[0] + left[1] * right[1] + left[2] * right[2];
    }

    private static double[] Cross(double[] left, double[] right)
    {
        return new double[] { left[1] * right[2] - left[2] * right[1],
            left[2] * right[0] - left[0] * right[2], left[0] * right[1] - left[1] * right[0] };
    }

    private static double[] Subtract(double[] left, double[] right)
    {
        return new double[] { left[0] - right[0], left[1] - right[1], left[2] - right[2] };
    }

    private static double[] Scale(double[] vector, double factor)
    {
        return new double[] { vector[0] * factor, vector[1] * factor, vector[2] * factor };
    }

    private static double VectorDistance(double[] left, double[] right)
    {
        double[] d = Subtract(left, right);
        return Math.Sqrt(Dot(d, d));
    }

    private static double PointDistance(Point3d left, Point3d right)
    {
        return VectorDistance(Point(left), Point(right));
    }

    private static double PlaneDistance(double[] point, double[] planePoint, double[] normal)
    {
        return Math.Abs(Dot(Subtract(point, planePoint), Normalize(normal)));
    }

    private static double MaxAbs(double[] vector)
    {
        return Math.Max(Math.Abs(vector[0]), Math.Max(Math.Abs(vector[1]), Math.Abs(vector[2])));
    }

    private static double[] Copy(double[] value)
    {
        double[] copy = new double[value.Length];
        Array.Copy(value, copy, value.Length);
        return copy;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AddRestoreError(Dictionary<string, object> evidence, Exception ex)
    {
        List<object> errors;
        if (!evidence.ContainsKey("restore_errors")) evidence["restore_errors"] = new List<object>();
        errors = evidence["restore_errors"] as List<object>;
        errors.Add(ex.ToString());
        List<object> defects = evidence.ContainsKey("defects") ? evidence["defects"] as List<object> : null;
        if (defects != null)
            defects.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "test_case", "session_restore" },
                { "reproduction_steps", new string[] {
                    "Run the acceptance through its final session restoration phase.",
                    "Inspect the restore_errors field and compare the final Work Part/session state with the recorded original state."
                } },
                { "observed_exception", ex.ToString() },
                { "retest_criterion", "Restore/close the isolated test parts without changing the original Work Part, then rerun the full acceptance and confirm the matching restored-session flag is true." }
            });
    }

    private static void Persist(string reportPath, Dictionary<string, object> evidence)
    {
        File.WriteAllText(reportPath, M2Json.CanonicalText(evidence), new UTF8Encoding(false));
    }

    private static Dictionary<string, object> AssemblyRecord(Assembly assembly)
    {
        string path = assembly == null ? null : assembly.Location;
        string fileVersion = null;
        if (!String.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            fileVersion = info == null ? null : info.FileVersion;
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "path", path },
            { "assembly_version", assembly == null ? null : assembly.GetName().Version.ToString() },
            { "file_version", fileVersion }
        };
    }

    public static int GetUnloadOption(string dummy)
    {
        return (int)Session.LibraryUnloadOption.Explicitly;
    }
}
