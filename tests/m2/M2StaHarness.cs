// NX-free STA WinForms host for the production M2 bridge assembly.
// It starts only the local IPC server and remains alive until stdin receives "stop".
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using NxSketchMcp.M2;

internal static class M2StaHarness
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static object bridgeServer;
    private static MethodInfo stopMethod;
    private static bool bridgeStarted;
    private static bool stopInvoked;
    private static int exitCode;
    private static FakeModelAdapter fakeModelAdapter;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("usage: M2StaHarness.exe <bridge-assembly> <pipe-name> <token-file>");
            return 64;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new Form())
        {
            form.Text = "M2 IPC Test Harness";
            form.ShowInTaskbar = false;
            form.WindowState = FormWindowState.Minimized;
            form.Shown += delegate
            {
                try
                {
                    if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                        throw new InvalidOperationException("WinForms message loop is not on an STA thread");

                    var assemblyPath = System.IO.Path.GetFullPath(args[0]);
                    var bridgeAssembly = typeof(M2BridgeServer).Assembly;

                    var startMethod = typeof(M2BridgeServer).GetMethod("Start", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(string), typeof(string) }, null);
                    stopMethod = typeof(M2BridgeServer).GetMethod("Stop", BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (startMethod == null || stopMethod == null)
                        throw new InvalidOperationException("expected public instance Start(string,string) and Stop() methods");

                    fakeModelAdapter = new FakeModelAdapter();
                    bridgeServer = new M2BridgeServer(fakeModelAdapter);
                    startMethod.Invoke(bridgeServer, new object[] { args[1], System.IO.Path.GetFullPath(args[2]) });
                    bridgeStarted = true;
                    bool referencesNxOpen = bridgeAssembly.GetReferencedAssemblies()
                        .Any(name => name.Name.StartsWith("NXOpen", StringComparison.OrdinalIgnoreCase));
                    WriteJson("{\"event\":\"ready\",\"pid\":" +
                        System.Diagnostics.Process.GetCurrentProcess().Id +
                        ",\"main_thread_id\":" + GetCurrentThreadId() +
                        ",\"apartment\":\"STA\",\"assembly_path\":" + JsonString(assemblyPath) +
                        ",\"assembly_version\":" + JsonString(bridgeAssembly.GetName().Version.ToString()) +
                        ",\"references_nxopen\":" + (referencesNxOpen ? "true" : "false") +
                        ",\"fake_model_adapter\":true" +
                        ",\"pipe_name\":" + JsonString(args[1]) + "}");
                }
                catch (Exception ex)
                {
                    exitCode = 2;
                    Console.Error.WriteLine("harness_start_error: " + Unwrap(ex).GetType().Name + ": " + Unwrap(ex).Message);
                    form.Close();
                }
            };

            form.FormClosing += delegate
            {
                if (!bridgeStarted || stopInvoked)
                    return;
                stopInvoked = true;
                try
                {
                    stopMethod.Invoke(bridgeServer, null);
                    WriteJson("{\"event\":\"stopped\",\"pid\":" +
                        System.Diagnostics.Process.GetCurrentProcess().Id +
                        ",\"main_thread_id\":" + GetCurrentThreadId() + "}");
                }
                catch (Exception ex)
                {
                    exitCode = 3;
                    Console.Error.WriteLine("harness_stop_error: " + Unwrap(ex).GetType().Name + ": " + Unwrap(ex).Message);
                }
            };

            var control = new Thread(delegate()
            {
                string line;
                while ((line = Console.ReadLine()) != null)
                {
                    string command = line.Trim();
                    if (String.Equals(command, "wait-inspect-start", StringComparison.Ordinal))
                    {
                        bool signaled = fakeModelAdapter != null && fakeModelAdapter.WaitForInspectStart(5000);
                        WriteJson("{\"event\":\"inspect-start\",\"observed\":" + (signaled ? "true" : "false") + "}");
                        continue;
                    }
                    if (String.Equals(command, "block-inspect", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.BlockInspection();
                        WriteJson("{\"event\":\"inspect-blocked\"}");
                        continue;
                    }
                    if (String.Equals(command, "release-inspect", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReleaseInspection();
                        WriteJson("{\"event\":\"inspect-released\"}");
                        continue;
                    }
                    if (String.Equals(command, "change-identity", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ChangeIdentity();
                        WriteJson("{\"event\":\"identity-changed\"}");
                        continue;
                    }
                    if (String.Equals(command, "block-operation", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.BlockOperation();
                        WriteJson("{\"event\":\"operation-blocked\"}");
                        continue;
                    }
                    if (String.Equals(command, "release-operation", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReleaseOperation();
                        WriteJson("{\"event\":\"operation-released\"}");
                        continue;
                    }
                    if (String.Equals(command, "age-running-m3-operation", StringComparison.Ordinal))
                    {
                        bool aged = AgeRunningM3Operation();
                        WriteJson("{\"event\":\"m3-operation-aged\",\"observed\":" + (aged ? "true" : "false") + "}");
                        continue;
                    }
                    if (String.Equals(command, "wait-operation-start", StringComparison.Ordinal))
                    {
                        bool signaled = fakeModelAdapter != null && fakeModelAdapter.WaitForOperationStart(5000);
                        WriteJson("{\"event\":\"operation-start\",\"observed\":" + (signaled ? "true" : "false") + "}");
                        continue;
                    }
                    if (String.Equals(command, "change-revision", StringComparison.Ordinal))
                    {
                        string revision = fakeModelAdapter == null ? "" : fakeModelAdapter.ChangeRevision();
                        WriteJson("{\"event\":\"revision-changed\",\"revision_id\":" + JsonString(revision) + "}");
                        continue;
                    }
                    if (String.Equals(command, "unknown-after-mutation", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReturnUnknownAfterMutation();
                        WriteJson("{\"event\":\"unknown-after-mutation\"}");
                        continue;
                    }
                    if (String.Equals(command, "rollback-after-write", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReturnRolledBackAfterWrite();
                        WriteJson("{\"event\":\"rollback-after-write\"}");
                        continue;
                    }
                    if (String.Equals(command, "rollback-unverified-after-write", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReturnRollbackUnverifiedAfterWrite();
                        WriteJson("{\"event\":\"rollback-unverified-after-write\"}");
                        continue;
                    }
                    if (String.Equals(command, "model-drift", StringComparison.Ordinal))
                    {
                        string revision = fakeModelAdapter == null ? "" : fakeModelAdapter.DriftModel();
                        WriteJson("{\"event\":\"model-drift\",\"revision_id\":" + JsonString(revision) + "}");
                        continue;
                    }
                    if (String.Equals(command, "move-f10-source", StringComparison.Ordinal))
                    {
                        bool moved = fakeModelAdapter != null && fakeModelAdapter.MoveF10Source();
                        WriteJson("{\"event\":\"f10-source-moved\",\"observed\":" + (moved ? "true" : "false") + "}");
                        continue;
                    }
                    if (String.Equals(command, "unknown-next-save", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReturnUnknownOnNextSave();
                        WriteJson("{\"event\":\"unknown-next-save\"}");
                        continue;
                    }
                    if (String.Equals(command, "unknown-next-m3-prepare", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ThrowUnknownOnNextM3Preparation();
                        WriteJson("{\"event\":\"unknown-next-m3-prepare\"}");
                        continue;
                    }
                    if (String.Equals(command, "unknown-next-m3-verification", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReturnUnknownOnNextM3Verification();
                        WriteJson("{\"event\":\"unknown-next-m3-verification\"}");
                        continue;
                    }
                    if (String.Equals(command, "null-next-m3-verification", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReturnNullOnNextM3Verification();
                        WriteJson("{\"event\":\"null-next-m3-verification\"}");
                        continue;
                    }
                    if (String.Equals(command, "mismatch-next-m3-verification", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReturnMismatchOnNextM3Verification();
                        WriteJson("{\"event\":\"mismatch-next-m3-verification\"}");
                        continue;
                    }
                    if (String.Equals(command, "block-save", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.BlockSave();
                        WriteJson("{\"event\":\"save-blocked\"}");
                        continue;
                    }
                    if (String.Equals(command, "wait-save-start", StringComparison.Ordinal))
                    {
                        bool signaled = fakeModelAdapter != null && fakeModelAdapter.WaitForSaveStart(5000);
                        WriteJson("{\"event\":\"save-start\",\"observed\":" + (signaled ? "true" : "false") + "}");
                        continue;
                    }
                    if (String.Equals(command, "release-save", StringComparison.Ordinal))
                    {
                        if (fakeModelAdapter != null) fakeModelAdapter.ReleaseSave();
                        WriteJson("{\"event\":\"save-released\"}");
                        continue;
                    }
                    if (String.Equals(command, "fake-counts", StringComparison.Ordinal))
                    {
                        WriteJson("{\"event\":\"fake-counts\",\"takeover_calls\":" +
                            (fakeModelAdapter == null ? 0 : fakeModelAdapter.TakeoverCalls) +
                            ",\"inspection_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.InspectionCalls) +
                            ",\"prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.PrepareCalls) +
                            ",\"execution_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.ExecutionCalls) +
                            ",\"verification_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.VerificationCalls) +
                            ",\"save_copy_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.SaveCopyCalls) +
                            ",\"saved_copy_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.SavedCopyCount) +
                            ",\"write_attempt_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.WriteAttemptCount) +
                            ",\"rollback_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.RollbackCount) +
                            ",\"mutation_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.MutationCount) +
                            ",\"m3_sketch_prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchPrepareCalls) +
                            ",\"m3_offset_prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchOffsetPrepareCalls) +
                            ",\"m3_pattern_prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchPatternPrepareCalls) +
                            ",\"m3_projection_prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchProjectionPrepareCalls) +
                            ",\"m3_projection_execution_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchProjectionExecutionCalls) +
                            ",\"m3_projection_verification_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchProjectionVerificationCalls) +
                            ",\"m3_reference_conversion_prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchReferenceConversionPrepareCalls) +
                            ",\"m3_reference_conversion_execution_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchReferenceConversionExecutionCalls) +
                            ",\"m3_reference_conversion_verification_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchReferenceConversionVerificationCalls) +
                            ",\"m3_reference_conversion_thread_id\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchReferenceConversionThreadId) +
                            ",\"m3_reference_conversion_thread_first_id\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchReferenceConversionThreadFirstId) +
                            ",\"m3_reference_conversion_thread_mismatch\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchReferenceConversionThreadMismatch) +
                            ",\"m4_face_query_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M4FaceQueryCalls) +
                            ",\"m4_last_selector\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.M4LastSelector) +
                            ",\"m4_last_body_id\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.M4LastBodyId) +
                            ",\"m4_face_query_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M4FaceQueryCalls) +
                            ",\"m4_last_selector\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.M4LastSelector) +
                            ",\"m4_last_body_id\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.M4LastBodyId) +
                            ",\"m3_extrude_prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3ExtrudePrepareCalls) +
                            ",\"m3_execution_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3ExecutionCalls) +
                            ",\"m3_verification_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3VerificationCalls) +
                            ",\"m3_revolve_prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3RevolvePrepareCalls) +
                            ",\"m3_hole_prepare_calls\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3HolePrepareCalls) +
                            ",\"m3_mutation_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3MutationCount) +
                            ",\"m3_sketch_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3SketchCount) +
                            ",\"m3_constraint_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3ConstraintCount) +
                            ",\"m3_body_count\":" + (fakeModelAdapter == null ? 0 : fakeModelAdapter.M3BodyCount) +
                            ",\"revision_id\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.RevisionId) +
                            ",\"current_value\":" + JsonString(fakeModelAdapter == null ? "" : fakeModelAdapter.CurrentValue) + "}");
                        continue;
                    }
                    if (!String.Equals(command, "stop", StringComparison.Ordinal)) continue;
                    try
                    {
                        form.BeginInvoke((MethodInvoker)delegate { form.Close(); });
                    }
                    catch (InvalidOperationException)
                    {
                        // The form already closed during startup failure or process cleanup.
                    }
                    return;
                }
            });
            control.IsBackground = true;
            control.Name = "M2 harness control reader";
            control.Start();

            Application.Run(form);
        }
        return exitCode;
    }

    private static Exception Unwrap(Exception ex)
    {
        var invocation = ex as TargetInvocationException;
        return invocation != null && invocation.InnerException != null ? invocation.InnerException : ex;
    }

    private static bool AgeRunningM3Operation()
    {
        if (bridgeServer == null) return false;
        Type serverType = bridgeServer.GetType();
        FieldInfo operationsField = serverType.GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic);
        IDictionary operations = operationsField == null ? null : operationsField.GetValue(bridgeServer) as IDictionary;
        if (operations == null) return false;
        foreach (DictionaryEntry entry in operations)
        {
            object operation = entry.Value;
            if (operation == null) continue;
            Type operationType = operation.GetType();
            FieldInfo kindField = operationType.GetField("Kind", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo stateField = operationType.GetField("State", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo timestampField = operationType.GetField("StartedTimestamp", BindingFlags.Instance | BindingFlags.NonPublic);
            string kind = kindField == null ? null : kindField.GetValue(operation) as string;
            string state = stateField == null ? null : stateField.GetValue(operation) as string;
            if (kind == null || (!kind.StartsWith("execute_create_", StringComparison.Ordinal) &&
                !String.Equals(kind, "execute_edit_sketch_corner", StringComparison.Ordinal) &&
                !String.Equals(kind, "execute_edit_sketch_offset", StringComparison.Ordinal) &&
                !String.Equals(kind, "execute_edit_sketch_pattern", StringComparison.Ordinal) &&
                !String.Equals(kind, "execute_edit_sketch_reference_conversion", StringComparison.Ordinal)) ||
                !String.Equals(state, "RUNNING", StringComparison.Ordinal) || timestampField == null) continue;
            long ageTicks = (long)(121000.0 * Stopwatch.Frequency / 1000.0);
            timestampField.SetValue(operation, Stopwatch.GetTimestamp() - ageTicks);
            return true;
        }
        return false;
    }

    private static void WriteJson(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    private static string JsonString(string value)
    {
        return "\"" + (value ?? String.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    }

    private sealed class FakeModelAdapter : IM2ModelAdapter, IM2OperationAdapter, IM2M27ModelAdapter, IM3OperationAdapter,
        IM3SketchCornerOperationAdapter, IM3SketchOffsetOperationAdapter, IM3SketchPatternOperationAdapter,
        IM3SketchProjectionOperationAdapter, IM3SketchReferenceConversionOperationAdapter,
        IM3RevolveOperationAdapter, IM3HoleOperationAdapter, IM4FaceQueryAdapter, IM7HelicalOperationAdapter
    {
        private sealed class FakeM3Geometry
        {
            internal string Id;
            internal string JournalId;
            internal string Type;
            internal double[] Start;
            internal double[] End;
            internal double[] Corner;
            internal double[] Center;
            internal double Width;
            internal double Height;
            internal double Diameter;
            internal double Radius;
            internal int ReferenceStatus = 1;
        }

        private sealed class FakeM3PatternPosition
        {
            internal int Index;
            internal int XIndex;
            internal int YIndex;
            internal bool HasRectangularIndices;
            internal string Kind;
            internal double[] Translation = new double[] { 0.0, 0.0 };
            internal double[] Center;
            internal double AngleRad;
            internal double[] AxisPoint;
            internal double[] AxisDirection;
        }

        private sealed class FakeM3Constraint
        {
            internal string Id;
            internal string GeometryId;
            internal string Role;
            internal string Type;
            internal string ExpressionName;
            internal double Value;
            internal bool IsDriving;
            internal FakeM3Sketch Sketch;
            internal FakeM3Geometry Geometry;
            internal List<string> GeometryRefs;
        }

        private sealed class FakeM3Sketch
        {
            internal string Id;
            internal string FeatureId;
            internal string ConstraintMode;
            internal string Plane;
            internal double[] Origin;
            internal double[] Normal;
            internal double[] XAxis;
            internal string SupportFaceSelector;
            internal string SupportBodyId;
            internal double[] FaceOriginOffsetMm;
            internal List<FakeM3Geometry> Geometries = new List<FakeM3Geometry>();
            internal List<FakeM3Constraint> Constraints = new List<FakeM3Constraint>();
        }

        private sealed class FakeM3Body
        {
            internal string Id;
            internal bool IsSolid;
            internal double Volume;
            internal double[] Bbox;
        }

        private sealed class FakeM3Feature
        {
            internal string Id;
            internal string Kind;
            internal string SketchId;
            internal string BodyId;
        }

    private sealed class FakeM3ProjectionRelation
    {
        internal FakeM3Geometry Source;
        internal FakeM3Geometry Output;
        internal bool Associative;
    }

        private sealed class FakeM3PlanState
        {
            internal string Kind;
            internal string FeatureId;
            internal string BodyId;
            internal FakeM3Sketch Sketch;
            internal M3ExtrudeRequest Extrude;
            internal M3RevolveRequest Revolve;
            internal string TargetBodyId;
            internal Dictionary<string, object> TargetBefore;
            internal List<Dictionary<string, object>> NonTargetBefore = new List<Dictionary<string, object>>();
            internal bool Executed;
            internal bool ExpectedUnknown;
            internal Dictionary<string, object> NativeDefinition;
            internal M3SketchCornerRequest CornerRequest;
            internal FakeM3Geometry CornerCurve1;
            internal FakeM3Geometry CornerCurve2;
            internal FakeM3Geometry CornerCurve1Before;
            internal FakeM3Geometry CornerCurve2Before;
            internal double[] CornerBefore;
            internal double[] CornerRay1;
            internal double[] CornerRay2;
            internal double CornerAngleRad;
            internal double[] CornerCut1;
            internal double[] CornerCut2;
            internal double[] CornerCenter;
            internal FakeM3Geometry CornerGeometry;
            internal List<FakeM3Geometry> CornerNonTargetsBefore = new List<FakeM3Geometry>();
            internal List<string> CornerConstraintIdsBefore = new List<string>();
            internal List<object> CornerConstraintsBefore = new List<object>();
            internal Dictionary<string, object> CornerEvidence;
            internal M3SketchOffsetRequest OffsetRequest;
            internal List<FakeM3Geometry> OffsetSourceBefore = new List<FakeM3Geometry>();
            internal List<FakeM3Geometry> OffsetOutput = new List<FakeM3Geometry>();
            internal List<FakeM3Geometry> OffsetNonTargetsBefore = new List<FakeM3Geometry>();
            internal List<object> OffsetConstraintsBefore = new List<object>();
            internal List<object> OffsetReferenceStatusBefore = new List<object>();
            internal Dictionary<string, object> OffsetFrameBefore;
            internal bool OffsetInputClosed;
            internal Dictionary<string, object> OffsetEvidence;
            internal M3SketchPatternRequest PatternRequest;
            internal List<FakeM3Geometry> PatternSourceBefore = new List<FakeM3Geometry>();
            internal List<FakeM3Geometry> PatternOutput = new List<FakeM3Geometry>();
            internal List<FakeM3Geometry> PatternNonTargetsBefore = new List<FakeM3Geometry>();
            internal List<object> PatternConstraintsBefore = new List<object>();
            internal List<object> PatternReferenceStatusBefore = new List<object>();
            internal Dictionary<string, object> PatternFrameBefore;
            internal List<object> PatternExpectedInstances = new List<object>();
            internal List<object> PatternPositionTransforms = new List<object>();
            internal string PatternAxisGeometryId;
            internal string PatternAxisJournalId;
            internal Dictionary<string, object> PatternEvidence;
        internal M3SketchProjectionRequest ProjectionRequest;
        internal List<FakeM3Sketch> ProjectionSourceSketches = new List<FakeM3Sketch>();
        internal List<FakeM3Geometry> ProjectionSources = new List<FakeM3Geometry>();
        internal List<FakeM3Geometry> ProjectionSourceBefore = new List<FakeM3Geometry>();
        internal List<FakeM3Geometry> ProjectionTargetBefore = new List<FakeM3Geometry>();
        internal List<FakeM3Geometry> ProjectionOutput = new List<FakeM3Geometry>();
        internal List<object> ProjectionConstraintsBefore = new List<object>();
        internal List<object> ProjectionReferenceStatusBefore = new List<object>();
        internal Dictionary<string, object> ProjectionFrameBefore;
        internal Dictionary<string, object> ProjectionEvidence;
        internal M3SketchReferenceConversionRequest ReferenceConversionRequest;
        internal string ReferenceConversionTargetState;
        internal List<FakeM3Geometry> ReferenceConversionSelectedCurves = new List<FakeM3Geometry>();
        internal List<int> ReferenceConversionCurveStatesBefore = new List<int>();
        internal List<FakeM3Constraint> ReferenceConversionSelectedDimensions = new List<FakeM3Constraint>();
        internal List<bool> ReferenceConversionDimensionStatesBefore = new List<bool>();
        internal List<object> ReferenceConversionObjectsBefore = new List<object>();
        internal List<object> ReferenceConversionGeometryBefore = new List<object>();
        internal List<object> ReferenceConversionConstraintsBefore = new List<object>();
        internal List<object> ReferenceConversionReferenceStatesBefore = new List<object>();
        internal List<object> ReferenceConversionCandidatesBefore = new List<object>();
        internal Dictionary<string, object> ReferenceConversionFrameBefore;
        internal List<string> ReferenceConversionFeatureIdsBefore = new List<string>();
        internal Dictionary<string, object> ReferenceConversionEvidence;
        internal int ReferenceConversionChangedCount;
        }

        private readonly System.Threading.ManualResetEvent inspectStarted = new System.Threading.ManualResetEvent(false);
        private readonly System.Threading.ManualResetEvent releaseInspect = new System.Threading.ManualResetEvent(false);
        private readonly System.Threading.ManualResetEvent operationStarted = new System.Threading.ManualResetEvent(false);
        private readonly System.Threading.ManualResetEvent releaseOperation = new System.Threading.ManualResetEvent(false);
        private readonly System.Threading.ManualResetEvent saveStarted = new System.Threading.ManualResetEvent(false);
        private readonly System.Threading.ManualResetEvent releaseSave = new System.Threading.ManualResetEvent(false);
        private int identityChanged;
        private int takeoverCalls;
        private int inspectionCalls;
        private int prepareCalls;
        private int executionCalls;
        private int verificationCalls;
        private int saveCopyCalls;
        private int writeAttemptCount;
        private int rollbackCount;
        private int mutationCount;
        private int blockExecution;
        private int blockInspection;
        private int blockSave;
        private int unknownAfterMutation;
        private int rolledBackAfterWrite;
        private int rollbackUnverifiedAfterWrite;
        private int unknownNextSave;
        private int unknownNextM3Preparation;
        private int unknownNextM3Verification;
        private int nullNextM3Verification;
        private int mismatchNextM3Verification;
        private int revisionNumber = 1;
        private int savedCopyNumber;
        private int m3SketchPrepareCalls;
        private int m3SketchOffsetPrepareCalls;
        private int m3ExtrudePrepareCalls;
        private int m3RevolvePrepareCalls;
        private int m3HolePrepareCalls;
        private int m3ExecutionCalls;
        private int m3VerificationCalls;
        private int m3MutationCount;
        private int m4FaceQueryCalls;
        private string m4LastSelector;
        private string m4LastBodyId;
        private int m3SketchNumber;
        private int m3FeatureNumber;
        private int m3ConstraintNumber;
        private int m3BodyNumber;
        private int m3SketchPatternPrepareCalls;
        private int m3SketchProjectionPrepareCalls;
        private int m3SketchProjectionExecutionCalls;
        private int m3SketchProjectionVerificationCalls;
        private int m3SketchReferenceConversionPrepareCalls;
        private int m3SketchReferenceConversionExecutionCalls;
        private int m3SketchReferenceConversionVerificationCalls;
        private int m3SketchReferenceConversionThreadId;
        private int m3SketchReferenceConversionThreadFirstId;
        private int m3SketchReferenceConversionThreadMismatch;
        private string currentValue = "10";
        private M2ModelBinding activeBinding;
        private readonly Dictionary<M2ParameterPlanSnapshot, string> expectedValues =
            new Dictionary<M2ParameterPlanSnapshot, string>();
        private readonly HashSet<string> savedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FakeM3Sketch> m3Sketches = new Dictionary<string, FakeM3Sketch>(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeM3Constraint> m3Constraints = new Dictionary<string, FakeM3Constraint>(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeM3Body> m3Bodies = new Dictionary<string, FakeM3Body>(StringComparer.Ordinal);
        private readonly List<FakeM3Feature> m3Features = new List<FakeM3Feature>();
        private readonly List<FakeM3ProjectionRelation> m3ProjectionRelations = new List<FakeM3ProjectionRelation>();

        internal int TakeoverCalls { get { return System.Threading.Interlocked.CompareExchange(ref takeoverCalls, 0, 0); } }
        internal int InspectionCalls { get { return System.Threading.Interlocked.CompareExchange(ref inspectionCalls, 0, 0); } }
        internal int PrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref prepareCalls, 0, 0); } }
        internal int ExecutionCalls { get { return System.Threading.Interlocked.CompareExchange(ref executionCalls, 0, 0); } }
        internal int VerificationCalls { get { return System.Threading.Interlocked.CompareExchange(ref verificationCalls, 0, 0); } }
        internal int SaveCopyCalls { get { return System.Threading.Interlocked.CompareExchange(ref saveCopyCalls, 0, 0); } }
        internal int SavedCopyCount { get { lock (savedTargets) return savedTargets.Count; } }
        internal int WriteAttemptCount { get { return System.Threading.Interlocked.CompareExchange(ref writeAttemptCount, 0, 0); } }
        internal int RollbackCount { get { return System.Threading.Interlocked.CompareExchange(ref rollbackCount, 0, 0); } }
        internal int MutationCount { get { return System.Threading.Interlocked.CompareExchange(ref mutationCount, 0, 0); } }
        internal int M3SketchPrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchPrepareCalls, 0, 0); } }
        internal int M3SketchOffsetPrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchOffsetPrepareCalls, 0, 0); } }
        internal int M3SketchPatternPrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchPatternPrepareCalls, 0, 0); } }
        internal int M3SketchProjectionPrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchProjectionPrepareCalls, 0, 0); } }
        internal int M3SketchProjectionExecutionCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchProjectionExecutionCalls, 0, 0); } }
        internal int M3SketchProjectionVerificationCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchProjectionVerificationCalls, 0, 0); } }
        internal int M3SketchReferenceConversionPrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchReferenceConversionPrepareCalls, 0, 0); } }
        internal int M3SketchReferenceConversionExecutionCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchReferenceConversionExecutionCalls, 0, 0); } }
        internal int M3SketchReferenceConversionVerificationCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchReferenceConversionVerificationCalls, 0, 0); } }
        internal int M3SketchReferenceConversionThreadId { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchReferenceConversionThreadId, 0, 0); } }
        internal int M3SketchReferenceConversionThreadFirstId { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchReferenceConversionThreadFirstId, 0, 0); } }
        internal int M3SketchReferenceConversionThreadMismatch { get { return System.Threading.Interlocked.CompareExchange(ref m3SketchReferenceConversionThreadMismatch, 0, 0); } }
        private void RecordM3SketchReferenceConversionThread()
        {
            int current = unchecked((int)GetCurrentThreadId());
            int first = System.Threading.Interlocked.CompareExchange(ref m3SketchReferenceConversionThreadFirstId, current, 0);
            if (first != 0 && first != current)
                System.Threading.Interlocked.Exchange(ref m3SketchReferenceConversionThreadMismatch, 1);
            System.Threading.Interlocked.Exchange(ref m3SketchReferenceConversionThreadId, current);
        }
        internal int M3ExtrudePrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3ExtrudePrepareCalls, 0, 0); } }
        internal int M3RevolvePrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3RevolvePrepareCalls, 0, 0); } }
        internal int M3HolePrepareCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3HolePrepareCalls, 0, 0); } }
        internal int M3ExecutionCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3ExecutionCalls, 0, 0); } }
        internal int M3VerificationCalls { get { return System.Threading.Interlocked.CompareExchange(ref m3VerificationCalls, 0, 0); } }
        internal int M3MutationCount { get { return System.Threading.Interlocked.CompareExchange(ref m3MutationCount, 0, 0); } }
        internal int M4FaceQueryCalls { get { return System.Threading.Interlocked.CompareExchange(ref m4FaceQueryCalls, 0, 0); } }
        internal string M4LastSelector { get { return System.Threading.Volatile.Read(ref m4LastSelector); } }
        internal string M4LastBodyId { get { return System.Threading.Volatile.Read(ref m4LastBodyId); } }
        internal int M3SketchCount { get { return m3Sketches.Count; } }
        internal int M3BodyCount { get { return m3Bodies.Count; } }
        internal int M3ConstraintCount { get { return m3Constraints.Count; } }
        internal string RevisionId { get { return System.Threading.Interlocked.CompareExchange(ref revisionNumber, 0, 0).ToString(); } }
        internal string CurrentValue { get { return System.Threading.Volatile.Read(ref currentValue); } }
        internal bool WaitForInspectStart(int milliseconds) { return inspectStarted.WaitOne(milliseconds); }
        internal bool WaitForOperationStart(int milliseconds) { return operationStarted.WaitOne(milliseconds); }
        internal bool WaitForSaveStart(int milliseconds) { return saveStarted.WaitOne(milliseconds); }
        internal void ChangeIdentity() { System.Threading.Interlocked.Exchange(ref identityChanged, 1); }
        internal void BlockInspection()
        {
            inspectStarted.Reset();
            releaseInspect.Reset();
            System.Threading.Interlocked.Exchange(ref blockInspection, 1);
        }
        internal void ReleaseInspection()
        {
            System.Threading.Interlocked.Exchange(ref blockInspection, 0);
            releaseInspect.Set();
        }
        internal void BlockOperation()
        {
            operationStarted.Reset();
            releaseOperation.Reset();
            System.Threading.Interlocked.Exchange(ref unknownAfterMutation, 0);
            System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 0);
            System.Threading.Interlocked.Exchange(ref rollbackUnverifiedAfterWrite, 0);
            System.Threading.Interlocked.Exchange(ref blockExecution, 1);
        }
        internal void ReleaseOperation() { releaseOperation.Set(); }
        internal void BlockSave()
        {
            saveStarted.Reset();
            releaseSave.Reset();
            System.Threading.Interlocked.Exchange(ref blockSave, 1);
        }
        internal void ReleaseSave()
        {
            System.Threading.Interlocked.Exchange(ref blockSave, 0);
            releaseSave.Set();
        }
        internal string ChangeRevision() { return System.Threading.Interlocked.Increment(ref revisionNumber).ToString(); }
        internal string DriftModel()
        {
            System.Threading.Volatile.Write(ref currentValue, "99");
            string revision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString();
            if (activeBinding != null) activeBinding.RevisionId = revision;
            return revision;
        }
        internal bool MoveF10Source()
        {
            FakeM3Geometry source = null;
            foreach (FakeM3Sketch sketch in m3Sketches.Values)
            {
                source = sketch.Geometries.Find(delegate(FakeM3Geometry geometry) {
                    return String.Equals(geometry.Id, "source-line", StringComparison.Ordinal);
                });
                if (source != null) break;
            }
            if (source == null || source.Type != "line" || source.Start == null || source.End == null) return false;

            const double moveMm = 2.0;
            source.Start[0] += moveMm;
            source.End[0] += moveMm;
            foreach (FakeM3ProjectionRelation relation in m3ProjectionRelations)
            {
                if (!relation.Associative || !Object.ReferenceEquals(relation.Source, source) || relation.Output == null) continue;
                if (relation.Output.Start != null) relation.Output.Start[0] += moveMm;
                if (relation.Output.End != null) relation.Output.End[0] += moveMm;
            }
            string revision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString(CultureInfo.InvariantCulture);
            if (activeBinding != null) activeBinding.RevisionId = revision;
            return true;
        }
        internal void ReturnUnknownOnNextSave() { System.Threading.Interlocked.Exchange(ref unknownNextSave, 1); }
        internal void ThrowUnknownOnNextM3Preparation()
        {
            System.Threading.Interlocked.Exchange(ref unknownNextM3Preparation, 1);
        }
        internal void ReturnUnknownOnNextM3Verification()
        {
            System.Threading.Interlocked.Exchange(ref unknownNextM3Verification, 1);
            System.Threading.Interlocked.Exchange(ref mismatchNextM3Verification, 0);
            System.Threading.Interlocked.Exchange(ref nullNextM3Verification, 0);
        }
        internal void ReturnNullOnNextM3Verification()
        {
            System.Threading.Interlocked.Exchange(ref nullNextM3Verification, 1);
            System.Threading.Interlocked.Exchange(ref unknownNextM3Verification, 0);
            System.Threading.Interlocked.Exchange(ref mismatchNextM3Verification, 0);
        }
        internal void ReturnMismatchOnNextM3Verification()
        {
            System.Threading.Interlocked.Exchange(ref mismatchNextM3Verification, 1);
            System.Threading.Interlocked.Exchange(ref unknownNextM3Verification, 0);
            System.Threading.Interlocked.Exchange(ref nullNextM3Verification, 0);
        }
        internal void ReturnUnknownAfterMutation()
        {
            System.Threading.Interlocked.Exchange(ref blockExecution, 0);
            System.Threading.Interlocked.Exchange(ref unknownAfterMutation, 1);
            System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 0);
            System.Threading.Interlocked.Exchange(ref rollbackUnverifiedAfterWrite, 0);
        }
        internal void ReturnRolledBackAfterWrite()
        {
            System.Threading.Interlocked.Exchange(ref blockExecution, 0);
            System.Threading.Interlocked.Exchange(ref unknownAfterMutation, 0);
            System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 1);
            System.Threading.Interlocked.Exchange(ref rollbackUnverifiedAfterWrite, 0);
        }
        internal void ReturnRollbackUnverifiedAfterWrite()
        {
            System.Threading.Interlocked.Exchange(ref blockExecution, 0);
            System.Threading.Interlocked.Exchange(ref unknownAfterMutation, 0);
            System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 0);
            System.Threading.Interlocked.Exchange(ref rollbackUnverifiedAfterWrite, 1);
        }

        public M2ModelBinding Takeover(string expectedPartPath, string artifactScope, string sessionEpoch, string loadInstanceId, int bindingEpoch)
        {
            System.Threading.Interlocked.Increment(ref takeoverCalls);
            if (!String.IsNullOrWhiteSpace(expectedPartPath) &&
                !String.Equals(expectedPartPath.Replace('/', '\\'), "C:\\M2Fake\\model.prt", StringComparison.OrdinalIgnoreCase))
                throw new M2ModelException("SESSION_MISMATCH", "fixture Work Part path mismatch");
            M2ModelBinding binding = new M2ModelBinding();
            binding.ArtifactScope = artifactScope;
            binding.ArtifactKind = "standalone_part";
            binding.ArtifactAssessment = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "status", "SUPPORTED_LIMITED" }, { "history_mode", "NATIVE_SKETCH_HISTORY" },
                { "history_feature_count", 1 }, { "sketch_count", 1 }, { "feature_backed_sketch_count", 1 },
                { "driving_edit_scope", "fixture supported Driving constraints" },
                { "append_scope", "limited M3 native sketch and extrusion operations" },
                { "unsupported_feature_parameter_count", 0 },
                { "dependencies", new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "external_file_reference_count", 0 }, { "interpart_expression_count", 0 },
                    { "external_feature_parent_count", 0 }, { "external_sketch_geometry_count", 0 },
                    { "wave_or_link_feature_count", 0 } } }
            };
            binding.IsReadOnly = false;
            binding.SessionEpoch = sessionEpoch;
            binding.LoadInstanceId = loadInstanceId;
            binding.BindingEpoch = bindingEpoch;
            binding.PartPath = "C:\\M2Fake\\model.prt";
            binding.PartTag = "fake-part-tag-1";
            binding.NxBuild = "NXOpen fixture build 1";
            binding.RevisionId = "1";
            binding.CanonicalSnapshot = "{\"schema\":\"m2-semantic-v1\"}";
            binding.AdapterState = this;
            binding.Identity = new Dictionary<string, object>(StringComparer.Ordinal);
            binding.Identity.Add("nx_build", binding.NxBuild);
            binding.Identity.Add("process_id", System.Diagnostics.Process.GetCurrentProcess().Id);
            binding.Identity.Add("part_path", binding.PartPath);
            binding.Identity.Add("part_tag", binding.PartTag);
            binding.Identity.Add("part_units", "Millimeters");
            binding.Identity.Add("is_modified_before_read", false);
            binding.Identity.Add("is_modified_after_read", false);
            binding.Identity.Add("dirty_changed_by_read", false);
            binding.Identity.Add("read_may_mark_modified", true);
            binding.Identity.Add("artifact_scope", binding.ArtifactScope);
            binding.Identity.Add("artifact_kind", binding.ArtifactKind);
            binding.Identity.Add("support_summary", binding.ArtifactAssessment);
            activeBinding = binding;
            return binding;
        }

        public bool IsBindingCurrent(M2ModelBinding binding, out string reason)
        {
            if (System.Threading.Interlocked.CompareExchange(ref identityChanged, 0, 0) == 0 &&
                binding != null && Object.ReferenceEquals(binding.AdapterState, this))
            {
                reason = null;
                return true;
            }
            reason = "fixture Work Part identity changed";
            return false;
        }

        public Dictionary<string, object> GetSession(M2ModelBinding binding)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("nx_build", binding.NxBuild);
            result.Add("part_path", binding.PartPath);
            result.Add("part_tag", binding.PartTag);
            result.Add("part_units", "Millimeters");
            result.Add("revision_id", binding.RevisionId);
            return result;
        }

        public Dictionary<string, object> InspectPart(M2ModelBinding binding)
        {
            System.Threading.Interlocked.Increment(ref inspectionCalls);
            inspectStarted.Set();
            if (System.Threading.Interlocked.CompareExchange(ref blockInspection, 0, 0) != 0)
            {
                if (!releaseInspect.WaitOne(15000))
                    throw new M2ModelException("FIXTURE_GATE_TIMEOUT", "fixture inspection release gate timed out");
            }
            else
                System.Threading.Thread.Sleep(1100);
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("schema", "m2-semantic-v1");
            result.Add("part_path", binding.PartPath);
            result.Add("part_tag", binding.PartTag);
            result.Add("part_units", "Millimeters");
            result.Add("features", new List<object> { new Dictionary<string, object> {
                { "journal_id", "feature-1" }, { "feature_type", "EXTRUDE" }, { "parameter_support", "SUPPORTED_METADATA" }
            }});
            result.Add("expressions", new List<object> { new Dictionary<string, object> {
                { "name", "p11" }, { "right_hand_side", CurrentValue }, { "value", CurrentValue }, { "unit", "mm" }
            }});
            result.Add("dependencies", new List<object>());
            result.Add("history", new List<object> { new Dictionary<string, object> {
                { "journal_id", "feature-1" }, { "feature_type", "EXTRUDE" }, { "expression_name", "p11" }
            }});
            result.Add("associations", new List<object> { new Dictionary<string, object> {
                { "sketch_id", "sketch-1" }, { "constraint_id", "constraint-1" },
                { "dimension_state", "Driving" }, { "expression_name", "p11" },
                { "geometry_refs", new List<string> { "line-1", "line-2" } }
            }});
            result.Add("bodies", M3BodyInventory());
            return result;
        }

        public Dictionary<string, object> InspectSketch(M2ModelBinding binding, string sketchId)
        {
            System.Threading.Interlocked.Increment(ref inspectionCalls);
            if (String.Equals(sketchId, "raise", StringComparison.Ordinal))
                throw new M2ModelException("MODEL_READ_FAILED", "fixture inspection failure");
            if (!String.Equals(sketchId, "sketch-1", StringComparison.Ordinal))
            {
                FakeM3Sketch m3Sketch;
                if (!m3Sketches.TryGetValue(sketchId, out m3Sketch))
                    throw new M2ModelException("SKETCH_NOT_FOUND", "fixture sketch was not found");
                Dictionary<string, object> m3SketchView = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "journal_id", m3Sketch.Id }, { "status", "FullyConstrained" },
                    { "degrees_of_freedom", "0" }, { "constraints", M3ConstraintViews(m3Sketch) },
                    { "geometry", M3GeometryViews(m3Sketch) }
                };
                return new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "sketch", m3SketchView }, { "part_path", binding.PartPath }
                };
            }
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("sketch", new Dictionary<string, object> {
                { "journal_id", "sketch-1" }, { "status", "FullyConstrained" }, { "degrees_of_freedom", "0" }
            });
            result.Add("part_path", binding.PartPath);
            return result;
        }

        public Dictionary<string, object> QueryFaces(M2ModelBinding binding, string revisionId,
            string selector, string bodyId)
        {
            System.Threading.Interlocked.Increment(ref m4FaceQueryCalls);
            if (!String.Equals(revisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture face query revision changed");
            System.Threading.Volatile.Write(ref m4LastSelector, selector);
            System.Threading.Volatile.Write(ref m4LastBodyId, bodyId);
            bool ambiguous = String.Equals(selector, "ambiguous", StringComparison.Ordinal);
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "selection_status", ambiguous ? "ambiguous" : "unique" },
                { "selector_normalized", selector }, { "candidate_count", ambiguous ? 2 : 1 },
                { "candidates", new List<object>() }, { "clarification_required", ambiguous },
                { "revision_id", revisionId }, { "body_id", bodyId }
            };
        }

        public M2ParameterPlanSnapshot PrepareParameterEdit(M2ModelBinding binding, M2ParameterEditRequest request)
        {
            System.Threading.Interlocked.Increment(ref prepareCalls);
            string currentRevision = RevisionId;
            if (!String.Equals(request.RevisionId, currentRevision, StringComparison.Ordinal))
                throw new M2ModelException("REVISION_MISMATCH", "fixture revision changed before plan preparation");
            if (!String.Equals(request.Unit, "mm", StringComparison.Ordinal))
                throw new M2ModelException("UNIT_MISMATCH", "fixture driving dimension is measured in mm");
            if (String.Equals(request.ConstraintId, "non-driving", StringComparison.Ordinal))
                throw new M2ModelException("NON_DRIVING_DIMENSION", "fixture constraint is reference-only");
            if (String.Equals(request.ConstraintId, "unsupported", StringComparison.Ordinal))
                throw new M2ModelException("UNSUPPORTED_DIMENSION_TYPE", "fixture dimension type is not supported for editing");
            if (String.Equals(request.ConstraintId, "ambiguous", StringComparison.Ordinal))
                throw new M2ModelException("AMBIGUOUS_CONSTRAINT", "fixture constraint id resolves to more than one dimension");
            FakeM3Constraint m3Constraint;
            if (m3Constraints.TryGetValue(request.ConstraintId, out m3Constraint))
            {
                if (!String.Equals(request.SketchId, m3Constraint.Sketch.Id, StringComparison.Ordinal))
                    throw new M2ModelException("CONSTRAINT_NOT_FOUND", "fixture constraint is not in the identified M3 sketch");
                if (!m3Constraint.IsDriving)
                    throw new M2ModelException("NON_DRIVING_DIMENSION", "fixture M3 constraint is not Driving");
                string dimensionValue = M3NumberText(m3Constraint.Value);
                return new M2ParameterPlanSnapshot {
                    RevisionId = currentRevision,
                    SketchId = request.SketchId,
                    ConstraintId = request.ConstraintId,
                    DimensionType = m3Constraint.Type,
                    DimensionState = "Driving",
                    ExpressionName = m3Constraint.ExpressionName,
                    ExpressionRightHandSide = dimensionValue,
                    CurrentValue = dimensionValue,
                    Unit = "mm",
                    GeometryRefs = new List<string>(m3Constraint.GeometryRefs),
                    AdapterState = m3Constraint
                };
            }
            if (!String.Equals(request.ConstraintId, "constraint-1", StringComparison.Ordinal))
                throw new M2ModelException("CONSTRAINT_NOT_FOUND", "fixture constraint was not found in the identified sketch");

            string value = CurrentValue;
            return new M2ParameterPlanSnapshot {
                RevisionId = currentRevision,
                SketchId = request.SketchId,
                ConstraintId = request.ConstraintId,
                DimensionType = "HorizontalDim",
                DimensionState = "Driving",
                ExpressionName = "p11",
                ExpressionRightHandSide = value,
                CurrentValue = value,
                Unit = "mm",
                GeometryRefs = new List<string> { "line-1", "line-2" },
                AdapterState = this
            };
        }

        public M2ParameterExecutionResult ExecuteParameterEdit(M2ModelBinding binding,
            M2ParameterPlanSnapshot prepared, string requestedValue, string requestedUnit)
        {
            System.Threading.Interlocked.Increment(ref executionCalls);
            operationStarted.Set();
            if (System.Threading.Interlocked.CompareExchange(ref blockExecution, 0, 0) != 0 && !releaseOperation.WaitOne(15000))
                throw new M2ModelException("FIXTURE_GATE_TIMEOUT", "fixture operation release gate timed out");

            FakeM3Constraint m3Constraint = prepared.AdapterState as FakeM3Constraint;
            if (m3Constraint != null)
                return ExecuteM3DimensionEdit(binding, prepared, m3Constraint, requestedValue, requestedUnit);

            string before = CurrentValue;
            string currentRevision = RevisionId;
            if (!String.Equals(currentRevision, prepared.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(before, prepared.CurrentValue, StringComparison.Ordinal))
            {
                if (!String.Equals(currentRevision, prepared.RevisionId, StringComparison.Ordinal))
                    binding.RevisionId = currentRevision;
                return new M2ParameterExecutionResult {
                    State = "UNCHANGED",
                    RevisionId = currentRevision,
                    ErrorCode = "REVISION_MISMATCH",
                    ErrorMessage = "fixture revision or parameter value changed after plan preparation",
                    Evidence = new Dictionary<string, object> {
                        { "changed", false },
                        { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(before) },
                        { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                        { "after_rhs", before }, { "after_value", before }, { "after_unit", "mm" },
                        { "geometry_before", new List<string> { "line-1:end-x=" + before + "mm", "line-2:origin-fixed" } },
                        { "geometry_after", new List<string> { "line-1:end-x=" + before + "mm", "line-2:origin-fixed" } },
                        { "geometry_changed", false }, { "readback_verified", false }, { "write_attempted", false }
                    }
                };
            }

            if (System.Threading.Interlocked.CompareExchange(ref rolledBackAfterWrite, 0, 0) != 0)
            {
                System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 0);
                System.Threading.Interlocked.Increment(ref writeAttemptCount);
                System.Threading.Volatile.Write(ref currentValue, requestedValue);
                System.Threading.Volatile.Write(ref currentValue, before);
                System.Threading.Interlocked.Increment(ref rollbackCount);
                List<string> geometry = new List<string> { "line-1:end-x=" + before + "mm", "line-2:origin-fixed" };
                return new M2ParameterExecutionResult {
                    State = "ROLLED_BACK",
                    RevisionId = currentRevision,
                    ErrorCode = "EDIT_NOT_VERIFIED",
                    ErrorMessage = "fixture restored the prior parameter and geometry after update failure",
                    Evidence = new Dictionary<string, object> {
                        { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(before) },
                        { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                        { "after_rhs", before }, { "after_value", before }, { "after_unit", "mm" },
                        { "geometry_before", geometry }, { "geometry_after", new List<string>(geometry) },
                        { "geometry_changed", false }, { "write_attempted", true },
                        { "update_error_count", 1 }, { "readback_verified", false },
                        { "rollback_readback_verified", true }
                    }
                };
            }

            if (System.Threading.Interlocked.Exchange(ref rollbackUnverifiedAfterWrite, 0) != 0)
            {
                System.Threading.Interlocked.Increment(ref writeAttemptCount);
                System.Threading.Volatile.Write(ref currentValue, requestedValue);
                expectedValues[prepared] = requestedValue;
                System.Threading.Interlocked.Increment(ref mutationCount);
                System.Threading.Interlocked.Increment(ref rollbackCount);
                currentRevision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString(CultureInfo.InvariantCulture);
                binding.RevisionId = currentRevision;
                List<string> geometry = new List<string> {
                    "line-1:end-x=" + requestedValue + "mm", "line-2:origin-fixed"
                };
                return new M2ParameterExecutionResult {
                    State = "UNKNOWN", RevisionId = currentRevision,
                    ErrorCode = "ROLLBACK_UNVERIFIED",
                    ErrorMessage = "fixture applied the edit but could not verify restoration after the update failure",
                    Evidence = new Dictionary<string, object> {
                        { "changed", null },
                        { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(requestedValue) },
                        { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                        { "after_rhs", requestedValue }, { "after_value", requestedValue }, { "after_unit", "mm" },
                        { "geometry_before", new List<string> { "line-1:end-x=" + before + "mm", "line-2:origin-fixed" } },
                        { "geometry_after", geometry }, { "geometry_changed", true },
                        { "write_attempted", true }, { "update_error_count", 1 },
                        { "rollback_attempted", true }, { "rollback_readback_verified", false },
                        { "rollback_error", "FIXTURE_UNDO_UNVERIFIED" }, { "readback_verified", false },
                        { "revision_id", currentRevision }
                    }
                };
            }

            System.Threading.Interlocked.Increment(ref writeAttemptCount);
            System.Threading.Volatile.Write(ref currentValue, requestedValue);
            expectedValues[prepared] = requestedValue;
            System.Threading.Interlocked.Increment(ref mutationCount);
            currentRevision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString();
            bool unknown = System.Threading.Interlocked.CompareExchange(ref unknownAfterMutation, 0, 0) != 0;
            string geometryBefore = "line-1:end-x=" + before + "mm";
            string geometryAfter = "line-1:end-x=" + requestedValue + "mm";
            return new M2ParameterExecutionResult {
                State = unknown ? "UNKNOWN" : "APPLIED",
                RevisionId = currentRevision,
                ErrorCode = unknown ? "UNKNOWN_RESULT" : null,
                ErrorMessage = unknown ? "fixture applied the edit but could not verify its result" : null,
                Evidence = new Dictionary<string, object> {
                    { "changed", unknown ? (object)null : true },
                    { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(requestedValue) },
                    { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                    { "after_rhs", requestedValue }, { "after_value", requestedValue }, { "after_unit", "mm" },
                    { "geometry_before", new List<string> { geometryBefore, "line-2:origin-fixed" } },
                    { "geometry_after", new List<string> { geometryAfter, "line-2:origin-fixed" } },
                    { "geometry_changed", true }, { "write_attempted", true },
                    { "update_error_count", unknown ? 1 : 0 },
                    { "readback_verified", !unknown },
                    { "revision_id", currentRevision }
                }
            };
        }

        public M2PartVerificationResult VerifyParameterEdit(M2ModelBinding binding,
            M2ParameterPlanSnapshot prepared)
        {
            System.Threading.Interlocked.Increment(ref verificationCalls);
            FakeM3Constraint m3Constraint = prepared.AdapterState as FakeM3Constraint;
            if (m3Constraint != null)
                return VerifyM3DimensionEdit(binding, prepared, m3Constraint);
            string expected;
            if (!expectedValues.TryGetValue(prepared, out expected)) expected = prepared.CurrentValue;
            string actual = CurrentValue;
            string sketchId = prepared.SketchId;
            string constraintId = prepared.ConstraintId;
            List<string> geometryRefs = new List<string>(prepared.GeometryRefs ?? new List<string>());
            bool expressionMatches = String.Equals(actual, expected, StringComparison.Ordinal);
            bool geometryMatches = String.Equals(actual, expected, StringComparison.Ordinal);

            Dictionary<string, object> expressionEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", sketchId }, { "constraint_id", constraintId },
                { "expression_name", prepared.ExpressionName }, { "expected_value", expected },
                { "actual_rhs", actual }, { "actual_value", actual }, { "unit", prepared.Unit }
            };
            Dictionary<string, object> geometryEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", sketchId }, { "constraint_id", constraintId },
                { "geometry_refs", geometryRefs }, { "expected_value", expected },
                { "actual_value", actual }, { "geometry", "line-1:end-x=" + actual + "mm" }
            };
            Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", sketchId }, { "feature_id", "feature-1" }, { "feature_type", "EXTRUDE" },
                { "parents", new List<string>() }, { "expressions", new List<string> { prepared.ExpressionName } },
                { "feature_count", 1 }
            };
            Dictionary<string, object> associationEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", sketchId }, { "constraint_id", constraintId },
                { "dimension_state", prepared.DimensionState }, { "expression_name", prepared.ExpressionName },
                { "geometry_refs", geometryRefs }
            };
            Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "expression", VerificationCheck(expressionMatches, expressionEvidence) },
                { "geometry", VerificationCheck(geometryMatches, geometryEvidence) },
                { "history", VerificationCheck(true, historyEvidence) },
                { "associations", VerificationCheck(true, associationEvidence) }
            };
            bool verified = expressionMatches && geometryMatches;
            return new M2PartVerificationResult {
                Verified = verified,
                RevisionId = binding.RevisionId,
                Checks = checks,
                ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
                ErrorMessage = verified ? null : "fixture model expression or geometry differs from the completed operation"
            };
        }

        public M3PlanSnapshot PrepareSketch(M2ModelBinding binding, M3SketchRequest request)
        {
            System.Threading.Interlocked.Increment(ref m3SketchPrepareCalls);
            if (!String.Equals(request.RevisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture revision changed before sketch planning");

            int number = System.Threading.Interlocked.Increment(ref m3SketchNumber);
            FakeM3Sketch sketch = new FakeM3Sketch();
            sketch.Id = "m3-sketch-" + number.ToString(CultureInfo.InvariantCulture);
            sketch.FeatureId = "m3-sketch-feature-" + number.ToString(CultureInfo.InvariantCulture);
            sketch.ConstraintMode = String.IsNullOrEmpty(request.ConstraintMode) ? "legacy_driving" : request.ConstraintMode;
            sketch.Plane = request.Plane;
            sketch.Origin = request.OriginMm == null ? null : (double[])request.OriginMm.Clone();
            sketch.Normal = request.Normal == null ? null : (double[])request.Normal.Clone();
            sketch.XAxis = request.XAxis == null ? null : (double[])request.XAxis.Clone();
            sketch.SupportFaceSelector = request.SupportFaceSelector;
            sketch.SupportBodyId = request.SupportBodyId;
            sketch.FaceOriginOffsetMm = request.FaceOriginOffsetMm == null ? null : (double[])request.FaceOriginOffsetMm.Clone();
            List<object> publicGeometries = new List<object>();
            List<object> drivingDimensions = new List<object>();
            foreach (M3SketchGeometryRequest geometryRequest in request.Geometries)
            {
                FakeM3Geometry geometry = CopyM3Geometry(sketch.Id, geometryRequest);
                sketch.Geometries.Add(geometry);
                publicGeometries.Add(M3GeometryView(geometry));
                if (sketch.ConstraintMode == "geometry_only")
                {
                    // Geometry-only plans intentionally have no dimensional or inferred relations.
                }
                else if (String.Equals(geometry.Type, "line", StringComparison.Ordinal))
                {
                    double dx = geometry.End[0] - geometry.Start[0];
                    double dy = geometry.End[1] - geometry.Start[1];
                    if (Math.Abs(dy) < 1e-9)
                        AddM3Constraint(sketch, geometry, "length_x", "HorizontalDim", Math.Abs(dx),
                            new List<string> { geometry.JournalId + "|StartPoint|0", geometry.JournalId + "|EndPoint|0" }, drivingDimensions);
                    else if (Math.Abs(dx) < 1e-9)
                        AddM3Constraint(sketch, geometry, "length_y", "VerticalDim", Math.Abs(dy),
                            new List<string> { geometry.JournalId + "|StartPoint|0", geometry.JournalId + "|EndPoint|0" }, drivingDimensions);
                    else
                    {
                        AddM3Constraint(sketch, geometry, "length_x", "HorizontalDim", Math.Abs(dx),
                            new List<string> { geometry.JournalId + "|StartPoint|0", geometry.JournalId + "|EndPoint|0" }, drivingDimensions);
                        AddM3Constraint(sketch, geometry, "length_y", "VerticalDim", Math.Abs(dy),
                            new List<string> { geometry.JournalId + "|StartPoint|0", geometry.JournalId + "|EndPoint|0" }, drivingDimensions);
                    }
                }
                else if (String.Equals(geometry.Type, "rectangle", StringComparison.Ordinal))
                {
                    AddM3Constraint(sketch, geometry, "width", "HorizontalDim", geometry.Width,
                        new List<string> { geometry.JournalId + "-top|StartPoint|0", geometry.JournalId + "-top|EndPoint|0" }, drivingDimensions);
                    AddM3Constraint(sketch, geometry, "height", "VerticalDim", geometry.Height,
                        new List<string> { geometry.JournalId + "-side|StartPoint|0", geometry.JournalId + "-side|EndPoint|0" }, drivingDimensions);
                }
                else if (String.Equals(geometry.Type, "circle", StringComparison.Ordinal))
                {
                    AddM3Constraint(sketch, geometry, "diameter", "DiameterDim", geometry.Diameter,
                        new List<string> { geometry.JournalId + "|None|0" }, drivingDimensions);
                    AddM3Constraint(sketch, geometry, "center_x", "HorizontalDim", Math.Abs(geometry.Center[0]),
                        new List<string> { geometry.JournalId + "|ArcCenter|0", sketch.Id + "-origin|None|0" }, drivingDimensions);
                    AddM3Constraint(sketch, geometry, "center_y", "VerticalDim", Math.Abs(geometry.Center[1]),
                        new List<string> { geometry.JournalId + "|ArcCenter|0", sketch.Id + "-origin|None|0" }, drivingDimensions);
                }
            }

            Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", "create_sketch" }, { "sketch_id", sketch.Id }, { "feature_id", sketch.FeatureId },
                { "geometries", publicGeometries }, { "driving_dimensions", drivingDimensions },
                { "sketch_status", sketch.ConstraintMode == "geometry_only" ? "UnderConstrained" : "FullyConstrained" },
                { "dof", sketch.ConstraintMode == "geometry_only" ? Math.Max(1, sketch.Geometries.Count * 2) : 0 },
                { "degrees_of_freedom", sketch.ConstraintMode == "geometry_only" ? Math.Max(1, sketch.Geometries.Count * 2) : 0 }
            };
            if (sketch.ConstraintMode == "geometry_only") publicView.Add("constraint_mode", sketch.ConstraintMode);
            if (!String.IsNullOrWhiteSpace(sketch.SupportFaceSelector))
            {
                publicView.Add("support_face_selector", sketch.SupportFaceSelector);
                publicView.Add("support_body_id", sketch.SupportBodyId);
                publicView.Add("face_origin_offset_mm", new List<double>(sketch.FaceOriginOffsetMm));
            }
            else
            {
                publicView.Add("plane", sketch.Plane);
                publicView.Add("origin_mm", new List<double>(sketch.Origin));
            }
            FakeM3PlanState state = new FakeM3PlanState {
                Kind = "create_sketch", FeatureId = sketch.FeatureId, Sketch = sketch
            };
            foreach (FakeM3Body body in SortedM3Bodies()) state.NonTargetBefore.Add(M3BodyView(body));
            return new M3PlanSnapshot {
                Kind = state.Kind, RevisionId = RevisionId, SketchId = sketch.Id,
                FeatureId = sketch.FeatureId, PublicView = publicView, AdapterState = state
            };
        }

        public M3PlanSnapshot PrepareSketchOffset(M2ModelBinding binding, M3SketchOffsetRequest request)
        {
            System.Threading.Interlocked.Increment(ref m3SketchOffsetPrepareCalls);
            if (request == null) throw new M2ModelException("INVALID_REQUEST", "fixture sketch-offset request is null");
            if (!String.Equals(request.RevisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture revision changed before sketch-offset planning");
            if (String.IsNullOrWhiteSpace(request.SketchId))
                throw new M2ModelException("INVALID_REQUEST", "fixture offset sketch_id is required");
            if (!M3FinitePositive(request.DistanceMm))
                throw new M2ModelException("INVALID_REQUEST", "fixture offset distance must be positive and finite");
            if (request.Side != "left" && request.Side != "right")
                throw new M2ModelException("INVALID_REQUEST", "fixture offset side must be left or right");
            if (request.JoinMode != "extension" && request.JoinMode != "arc")
                throw new M2ModelException("INVALID_REQUEST", "fixture offset join_mode must be extension or arc");
            if (!request.RetainOriginal)
                throw new M2ModelException("INVALID_REQUEST", "fixture adapter only supports retain_original=true");
            if (request.CurveChain == null || request.CurveChain.Count == 0)
                throw new M2ModelException("INVALID_REQUEST", "fixture offset curve_chain must be non-empty");

            FakeM3Sketch sketch;
            if (!m3Sketches.TryGetValue(request.SketchId, out sketch))
                throw new M2ModelException("SKETCH_NOT_FOUND", "fixture sketch-offset target Sketch was not found");

            List<FakeM3Geometry> sources = new List<FakeM3Geometry>();
            List<double[]> traversalStarts = new List<double[]>();
            List<double[]> traversalEnds = new List<double[]>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (M3SketchOffsetCurveRequest entry in request.CurveChain)
            {
                if (entry == null || String.IsNullOrWhiteSpace(entry.CurveId) ||
                    (entry.Direction != "forward" && entry.Direction != "reverse"))
                    throw new M2ModelException("INVALID_REQUEST", "fixture offset chain entry needs a curve_id and forward/reverse direction");
                if (!seen.Add(entry.CurveId))
                    throw new M2ModelException("INVALID_REQUEST", "fixture offset chain may not repeat a curve ID");
                FakeM3Geometry curve = sketch.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, entry.CurveId, StringComparison.Ordinal) ||
                        String.Equals(item.JournalId, entry.CurveId, StringComparison.Ordinal);
                });
                if (curve == null)
                    throw new M2ModelException("SKETCH_TARGET_MISMATCH", "fixture curve ID does not belong to the explicit sketch");
                if (!String.Equals(curve.Type, "line", StringComparison.Ordinal))
                    throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "fixture offset adapter supports Line chains only");
                double[] traversalStart = entry.Direction == "forward" ? curve.Start : curve.End;
                double[] traversalEnd = entry.Direction == "forward" ? curve.End : curve.Start;
                if (traversalStart == null || traversalEnd == null || FakeM3Distance(traversalStart, traversalEnd) <= 1e-12)
                    throw new M2ModelException("INVALID_OFFSET_CHAIN", "fixture offset chain contains a degenerate Line");
                if (sources.Count > 0 && FakeM3Distance(traversalEnds[traversalEnds.Count - 1], traversalStart) > 1e-9)
                    throw new M2ModelException("OFFSET_CHAIN_DISCONNECTED", "fixture offset chain entries are not connected in the requested direction");
                sources.Add(curve);
                traversalStarts.Add((double[])traversalStart.Clone());
                traversalEnds.Add((double[])traversalEnd.Clone());
            }

            bool closed = FakeM3Distance(traversalEnds[traversalEnds.Count - 1], traversalStarts[0]) <= 1e-9;
            List<FakeM3Geometry> lineOutputs = new List<FakeM3Geometry>();
            for (int i = 0; i < sources.Count; i++)
            {
                double[] tangent = FakeM3Unit(new double[] {
                    traversalEnds[i][0] - traversalStarts[i][0], traversalEnds[i][1] - traversalStarts[i][1]
                });
                double sideSign = request.Side == "left" ? 1.0 : -1.0;
                double[] shift = new double[] {
                    -tangent[1] * request.DistanceMm * sideSign,
                    tangent[0] * request.DistanceMm * sideSign
                };
                int outputNumber = System.Threading.Interlocked.Increment(ref m3FeatureNumber);
                lineOutputs.Add(new FakeM3Geometry {
                    Id = "f08-output-line-" + outputNumber.ToString(CultureInfo.InvariantCulture),
                    JournalId = "m3-offset-line-" + outputNumber.ToString(CultureInfo.InvariantCulture),
                    Type = "line",
                    Start = new double[] { traversalStarts[i][0] + shift[0], traversalStarts[i][1] + shift[1] },
                    End = new double[] { traversalEnds[i][0] + shift[0], traversalEnds[i][1] + shift[1] }
                });
            }

            int joinCount = closed ? lineOutputs.Count : lineOutputs.Count - 1;
            Dictionary<int, FakeM3Geometry> arcJoins = new Dictionary<int, FakeM3Geometry>();
            for (int i = 0; i < joinCount; i++)
            {
                int next = (i + 1) % lineOutputs.Count;
                FakeM3Geometry previousLine = lineOutputs[i];
                FakeM3Geometry nextLine = lineOutputs[next];
                double[] previousTangent = FakeM3Unit(new double[] {
                    traversalEnds[i][0] - traversalStarts[i][0], traversalEnds[i][1] - traversalStarts[i][1]
                });
                double[] nextTangent = FakeM3Unit(new double[] {
                    traversalEnds[next][0] - traversalStarts[next][0], traversalEnds[next][1] - traversalStarts[next][1]
                });
                double turn = previousTangent[0] * nextTangent[1] - previousTangent[1] * nextTangent[0];
                bool outerArc = request.JoinMode == "arc" &&
                    turn * (request.Side == "left" ? 1.0 : -1.0) < -1e-12;
                if (outerArc)
                {
                    int outputNumber = System.Threading.Interlocked.Increment(ref m3FeatureNumber);
                    FakeM3Geometry connector = new FakeM3Geometry {
                        Id = "offset-arc-" + outputNumber.ToString(CultureInfo.InvariantCulture),
                        JournalId = "m3-offset-arc-" + outputNumber.ToString(CultureInfo.InvariantCulture),
                        Type = "arc", Start = (double[])previousLine.End.Clone(),
                        End = (double[])nextLine.Start.Clone(),
                        Center = (double[])traversalEnds[i].Clone(), Radius = request.DistanceMm
                    };
                    arcJoins.Add(i, connector);
                }
                else
                {
                    double[] join = FakeM3LineIntersection(previousLine.Start, previousLine.End,
                        nextLine.Start, nextLine.End);
                    previousLine.End = (double[])join.Clone();
                    nextLine.Start = (double[])join.Clone();
                }
            }

            List<FakeM3Geometry> outputChain = new List<FakeM3Geometry>();
            for (int i = 0; i < lineOutputs.Count; i++)
            {
                outputChain.Add(lineOutputs[i]);
                FakeM3Geometry connector;
                if (arcJoins.TryGetValue(i, out connector)) outputChain.Add(connector);
            }

            FakeM3PlanState state = new FakeM3PlanState {
                Kind = "edit_sketch_offset", FeatureId = sketch.FeatureId, Sketch = sketch,
                OffsetRequest = request, OffsetOutput = outputChain, OffsetInputClosed = closed
            };
            state.OffsetFrameBefore = FakeM3SketchFrameView(sketch);
            state.OffsetReferenceStatusBefore = FakeM3ReferenceStatusViews(sketch.Geometries);
            foreach (FakeM3Geometry geometry in sketch.Geometries)
            {
                FakeM3Geometry copy = CopyFakeM3Geometry(geometry);
                state.OffsetSourceBefore.Add(copy);
                if (!sources.Contains(geometry)) state.OffsetNonTargetsBefore.Add(CopyFakeM3Geometry(geometry));
            }
            state.OffsetConstraintsBefore = M3ConstraintViews(sketch);
            foreach (FakeM3Body body in SortedM3Bodies()) state.NonTargetBefore.Add(M3BodyView(body));

            List<object> chainView = new List<object>();
            foreach (M3SketchOffsetCurveRequest entry in request.CurveChain)
                chainView.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "curve_id", entry.CurveId }, { "direction", entry.Direction }
                });
            Dictionary<string, object> definition = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", sketch.Id }, { "curve_chain", chainView },
                { "distance_mm", request.DistanceMm }, { "side", request.Side },
                { "retain_original", request.RetainOriginal }, { "join_mode", request.JoinMode },
                { "input_closed", closed }, { "output_geometry", M3GeometryViewList(outputChain) }
            };
            state.NativeDefinition = definition;
            Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", state.Kind }, { "sketch_id", sketch.Id }, { "feature_id", sketch.FeatureId },
                { "curve_chain", chainView }, { "distance_mm", request.DistanceMm },
                { "side", request.Side }, { "retain_original", request.RetainOriginal },
                { "join_mode", request.JoinMode }, { "input_closed", closed }
            };
            return new M3PlanSnapshot {
                Kind = state.Kind, RevisionId = request.RevisionId, SketchId = sketch.Id,
                FeatureId = sketch.FeatureId, PublicView = publicView, AdapterState = state
            };
        }

        private static double[] FakeM3LineIntersection(double[] aStart, double[] aEnd,
            double[] bStart, double[] bEnd)
        {
            double rx = aEnd[0] - aStart[0], ry = aEnd[1] - aStart[1];
            double sx = bEnd[0] - bStart[0], sy = bEnd[1] - bStart[1];
            double denominator = rx * sy - ry * sx;
            if (Math.Abs(denominator) <= 1e-12)
                throw new M2ModelException("OFFSET_JOIN_UNDEFINED", "fixture extension join has parallel support Lines");
            double qx = bStart[0] - aStart[0], qy = bStart[1] - aStart[1];
            double t = (qx * sy - qy * sx) / denominator;
            return new double[] { aStart[0] + t * rx, aStart[1] + t * ry };
        }

        public M3PlanSnapshot PrepareSketchPattern(M2ModelBinding binding, M3SketchPatternRequest request)
        {
            System.Threading.Interlocked.Increment(ref m3SketchPatternPrepareCalls);
            if (request == null || !String.Equals(request.RevisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture revision changed before F09 pattern planning");
            if (String.IsNullOrWhiteSpace(request.SketchId) || request.SourceCurveIds == null || request.SourceCurveIds.Count < 1)
                throw new M2ModelException("INVALID_REQUEST", "fixture F09 request requires sketch_id and source_curve_ids");
            if (request.SourceCurveIds.Count > 256)
                throw new M2ModelException("INVALID_REQUEST", "fixture F09 source count exceeds 256");
            if (request.Mode != "linear" && request.Mode != "rectangular" && request.Mode != "circular" && request.Mode != "mirror")
                throw new M2ModelException("INVALID_REQUEST", "fixture F09 mode is unsupported");

            FakeM3Sketch sketch;
            if (!m3Sketches.TryGetValue(request.SketchId, out sketch))
                throw new M2ModelException("SKETCH_NOT_FOUND", "fixture F09 target Sketch was not found");
            HashSet<string> sourceIds = new HashSet<string>(StringComparer.Ordinal);
            List<FakeM3Geometry> sources = new List<FakeM3Geometry>();
            foreach (string sourceId in request.SourceCurveIds)
            {
                if (String.IsNullOrWhiteSpace(sourceId) || !sourceIds.Add(sourceId))
                    throw new M2ModelException("INVALID_REQUEST", "fixture F09 sources must be unique nonempty IDs");
                FakeM3Geometry source = sketch.Geometries.Find(delegate(FakeM3Geometry geometry) {
                    return String.Equals(geometry.Id, sourceId, StringComparison.Ordinal) ||
                        String.Equals(geometry.JournalId, sourceId, StringComparison.Ordinal);
                });
                if (source == null)
                    throw new M2ModelException("CURVE_NOT_FOUND", "fixture F09 source is not in the identified Sketch");
                if (source.Type != "line" && source.Type != "arc" && source.Type != "circle")
                    throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "fixture F09 supports Line and circular Arc geometry");
                sources.Add(source);
            }

            FakeM3Geometry axis = null;
            List<FakeM3PatternPosition> positions = new List<FakeM3PatternPosition>();
            if (request.Mode == "linear")
            {
                if (!M3FinitePositive(request.SpacingMm) || request.InstanceCount < 2 || request.InstanceCount > 256)
                    throw new M2ModelException("INVALID_REQUEST", "fixture linear spacing/count is invalid");
                double[] direction = M3PatternUnit(request.DirectionXY, "direction_xy");
                for (int index = 0; index < request.InstanceCount; index++)
                {
                    FakeM3PatternPosition position = new FakeM3PatternPosition { Index = index, Kind = index == 0 ? "identity" : "translation" };
                    position.Translation = new double[] { direction[0] * request.SpacingMm * index,
                        direction[1] * request.SpacingMm * index };
                    positions.Add(position);
                }
            }
            else if (request.Mode == "rectangular")
            {
                if (!M3FinitePositive(request.SpacingXmm) || !M3FinitePositive(request.SpacingYmm) ||
                    request.CountX < 2 || request.CountY < 2 || (long)request.CountX * request.CountY > 256)
                    throw new M2ModelException("INVALID_REQUEST", "fixture rectangular spacing/count is invalid");
                double[] dx = M3PatternUnit(request.DirectionXXY, "direction_x_xy");
                double[] dy = M3PatternUnit(request.DirectionYXY, "direction_y_xy");
                if (Math.Abs(dx[0] * dy[0] + dx[1] * dy[1]) > 1e-9)
                    throw new M2ModelException("INVALID_REQUEST", "fixture rectangular directions must be orthogonal");
                for (int y = 0; y < request.CountY; y++)
                    for (int x = 0; x < request.CountX; x++)
                    {
                        FakeM3PatternPosition position = new FakeM3PatternPosition {
                            Index = y * request.CountX + x, XIndex = x, YIndex = y,
                            HasRectangularIndices = true,
                            Kind = x == 0 && y == 0 ? "identity" : "translation"
                        };
                        position.Translation = new double[] {
                            dx[0] * request.SpacingXmm * x + dy[0] * request.SpacingYmm * y,
                            dx[1] * request.SpacingXmm * x + dy[1] * request.SpacingYmm * y
                        };
                        positions.Add(position);
                    }
            }
            else if (request.Mode == "circular")
            {
                if (request.CenterXY == null || request.CenterXY.Length != 2 ||
                    !M3Finite(request.CenterXY[0]) || !M3Finite(request.CenterXY[1]) ||
                    !M3Finite(request.AngleStepDeg) || request.AngleStepDeg == 0.0 ||
                    request.InstanceCount < 2 || request.InstanceCount > 256)
                    throw new M2ModelException("INVALID_REQUEST", "fixture circular center/step/count is invalid");
                double normalizedStep = request.AngleStepDeg % 360.0;
                List<double> usedAngles = new List<double>();
                for (int index = 0; index < request.InstanceCount; index++)
                {
                    double angleDeg = (normalizedStep * index) % 360.0;
                    foreach (double prior in usedAngles)
                    {
                        double difference = Math.Abs(angleDeg - prior);
                        if (Math.Min(difference, 360.0 - difference) <= 1e-9)
                            throw new M2ModelException("INVALID_REQUEST", "fixture circular positions overlap");
                    }
                    usedAngles.Add(angleDeg);
                    double angle = angleDeg * Math.PI / 180.0;
                    positions.Add(new FakeM3PatternPosition {
                        Index = index, Kind = index == 0 ? "identity" : "rotation",
                        Center = (double[])request.CenterXY.Clone(), AngleRad = angle
                    });
                }
            }
            else
            {
                if (String.IsNullOrWhiteSpace(request.AxisCurveId) || sourceIds.Contains(request.AxisCurveId))
                    throw new M2ModelException("INVALID_REQUEST", "fixture mirror requires a distinct native axis Line");
                axis = sketch.Geometries.Find(delegate(FakeM3Geometry geometry) {
                    return String.Equals(geometry.Id, request.AxisCurveId, StringComparison.Ordinal) ||
                        String.Equals(geometry.JournalId, request.AxisCurveId, StringComparison.Ordinal);
                });
                if (axis == null || axis.Type != "line")
                    throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "fixture mirror axis must be a native Line in the same Sketch");
                double[] direction = M3PatternUnit(new double[] { axis.End[0] - axis.Start[0], axis.End[1] - axis.Start[1] }, "mirror axis");
                positions.Add(new FakeM3PatternPosition { Index = 0, Kind = "identity" });
                positions.Add(new FakeM3PatternPosition { Index = 1, Kind = "reflection",
                    AxisPoint = (double[])axis.Start.Clone(), AxisDirection = direction });
            }

            if (positions.Count < 2 || (long)(positions.Count - 1) * sources.Count > 4096)
                throw new M2ModelException("INVALID_REQUEST", "fixture F09 output count exceeds its supported limit");
            List<FakeM3Geometry> outputs = new List<FakeM3Geometry>();
            List<object> expectedInstances = new List<object>();
            List<object> transforms = new List<object>();
            foreach (FakeM3PatternPosition position in positions)
            {
                Dictionary<string, object> transformView = M3PatternTransformView(position, axis);
                transforms.Add(transformView);
                foreach (FakeM3Geometry source in sources)
                {
                    FakeM3Geometry transformed = position.Index == 0
                        ? CopyFakeM3Geometry(source)
                        : M3TransformPatternGeometry(source, position,
                            System.Threading.Interlocked.Increment(ref m3FeatureNumber));
                    Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "source_curve_id", source.Id }, { "position_index", position.Index },
                        { "position_x_index", position.HasRectangularIndices ? (object)position.XIndex : null },
                        { "position_y_index", position.HasRectangularIndices ? (object)position.YIndex : null },
                        { "is_source_instance", position.Index == 0 }, { "transform", transformView },
                        { "expected_curve_record", M3GeometryView(transformed) }
                    };
                    expectedInstances.Add(item);
                    if (position.Index == 0) continue;
                    foreach (FakeM3Geometry prior in outputs)
                        if (M3PatternGeometryEqual(prior, transformed))
                            throw new M2ModelException("DUPLICATE_PATTERN_INSTANCE", "fixture F09 generated coincident geometry");
                    foreach (FakeM3Geometry sourceBefore in sources)
                        if (M3PatternGeometryEqual(sourceBefore, transformed))
                            throw new M2ModelException("DUPLICATE_PATTERN_INSTANCE", "fixture F09 generated geometry overlapping its source");
                    outputs.Add(transformed);
                }
            }
            foreach (FakeM3Geometry geometry in outputs)
            {
                if (!M3GeometryFinite(geometry))
                    throw new M2ModelException("INVALID_REQUEST", "fixture F09 derived geometry is not finite");
            }

            int featureNumber = System.Threading.Interlocked.Increment(ref m3FeatureNumber);
            FakeM3PlanState state = new FakeM3PlanState {
                Kind = "edit_sketch_pattern", FeatureId = "m3-pattern-feature-" + featureNumber.ToString(CultureInfo.InvariantCulture),
                Sketch = sketch, PatternRequest = request, PatternOutput = outputs,
                PatternExpectedInstances = expectedInstances, PatternPositionTransforms = transforms,
                PatternAxisGeometryId = axis == null ? null : axis.Id,
                PatternAxisJournalId = axis == null ? null : axis.JournalId
            };
            state.PatternFrameBefore = FakeM3SketchFrameView(sketch);
            state.PatternReferenceStatusBefore = FakeM3ReferenceStatusViews(sketch.Geometries);
            state.PatternConstraintsBefore = M3ConstraintViews(sketch);
            foreach (FakeM3Geometry geometry in sketch.Geometries)
            {
                FakeM3Geometry copy = CopyFakeM3Geometry(geometry);
                state.PatternSourceBefore.Add(copy);
                if (!sources.Contains(geometry)) state.PatternNonTargetsBefore.Add(CopyFakeM3Geometry(geometry));
            }
            foreach (FakeM3Body body in SortedM3Bodies()) state.NonTargetBefore.Add(M3BodyView(body));
            Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", state.Kind }, { "sketch_id", sketch.Id }, { "feature_id", state.FeatureId },
                { "mode", request.Mode }, { "source_curve_ids", new List<string>(request.SourceCurveIds) },
                { "axis_curve_id", request.Mode == "mirror" ? request.AxisCurveId : null },
                { "expected_pattern_instance_count", positions.Count },
                { "expected_output_curve_count", outputs.Count }, { "expected_instances", expectedInstances }
            };
            return new M3PlanSnapshot {
                Kind = state.Kind, RevisionId = request.RevisionId, SketchId = sketch.Id,
                FeatureId = state.FeatureId, PublicView = publicView, AdapterState = state
            };
        }

        public M3PlanSnapshot PrepareSketchProjection(M2ModelBinding binding, M3SketchProjectionRequest request)
        {
            System.Threading.Interlocked.Increment(ref m3SketchProjectionPrepareCalls);
            if (request == null || !String.Equals(request.RevisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture revision changed before F10 projection planning");
            if (String.IsNullOrWhiteSpace(request.SketchId) || request.SourceCurveIds == null ||
                request.SourceCurveIds.Count < 1 || request.SourceCurveIds.Count > 128)
                throw new M2ModelException("INVALID_REQUEST", "fixture F10 request requires a target Sketch and 1 to 128 source IDs");

            FakeM3Sketch targetSketch;
            if (!m3Sketches.TryGetValue(request.SketchId, out targetSketch))
                throw new M2ModelException("SKETCH_NOT_FOUND", "fixture F10 target Sketch was not found");

            double[] direction = M3ProjectionUnit(request.DirectionXYZ, "direction_xyz");
            double[] targetOrigin, targetNormal, targetXAxis, targetYAxis;
            FakeM3SketchBasis(targetSketch, out targetOrigin, out targetNormal, out targetXAxis, out targetYAxis);
            double directionAlignment = Math.Abs(M3ProjectionDot(direction, targetNormal));
            if (directionAlignment < 1.0 - 1e-10)
                throw new M2ModelException("UNSUPPORTED_PROJECTION_DIRECTION",
                    "fixture F10 accepts only the positive or negative target Sketch normal");

            HashSet<string> sourceIds = new HashSet<string>(StringComparer.Ordinal);
            List<FakeM3Sketch> sourceSketches = new List<FakeM3Sketch>();
            List<FakeM3Geometry> sources = new List<FakeM3Geometry>();
            List<FakeM3Geometry> outputs = new List<FakeM3Geometry>();
            List<object> sourceRecords = new List<object>();
            List<object> outputRecords = new List<object>();
            foreach (string sourceId in request.SourceCurveIds)
            {
                if (String.IsNullOrWhiteSpace(sourceId) || !sourceIds.Add(sourceId))
                    throw new M2ModelException("INVALID_REQUEST", "fixture F10 source IDs must be unique nonempty strings");
                FakeM3Sketch sourceSketch;
                FakeM3Geometry source = FindFakeM3ProjectionSource(sourceId, out sourceSketch);
                if (source == null || sourceSketch == null)
                    throw new M2ModelException("CURVE_NOT_FOUND", "fixture F10 source Curve was not found in the active Part");
                if (source.Type != "line" && source.Type != "circle")
                    throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "fixture F10 fake supports Line and circular Curve sources only");

                int outputNumber = System.Threading.Interlocked.Increment(ref m3FeatureNumber);
                FakeM3Geometry output = M3ProjectFakeGeometry(source, sourceSketch, targetSketch,
                    direction, "f10-output-" + outputNumber.ToString(CultureInfo.InvariantCulture),
                    "m3-projection-output-" + outputNumber.ToString(CultureInfo.InvariantCulture));
                sources.Add(source);
                sourceSketches.Add(sourceSketch);
                outputs.Add(output);
                sourceRecords.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "source_curve_id", source.Id }, { "source_journal_id", source.JournalId },
                    { "source_sketch_id", sourceSketch.Id }, { "geometry", M3GeometryView(source) }
                });
                outputRecords.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "source_curve_id", source.Id }, { "output_curve_id", output.Id },
                    { "associative", request.Associative }, { "geometry", M3GeometryView(output) }
                });
            }

            int featureNumber = System.Threading.Interlocked.Increment(ref m3FeatureNumber);
            FakeM3PlanState state = new FakeM3PlanState {
                Kind = "edit_sketch_projection",
                FeatureId = "m3-projection-feature-" + featureNumber.ToString(CultureInfo.InvariantCulture),
                Sketch = targetSketch,
                ProjectionRequest = request,
                ProjectionSourceSketches = sourceSketches,
                ProjectionSources = sources,
                ProjectionOutput = outputs
            };
            state.ProjectionFrameBefore = FakeM3SketchFrameView(targetSketch);
            state.ProjectionConstraintsBefore = M3ConstraintViews(targetSketch);
            state.ProjectionReferenceStatusBefore = FakeM3ReferenceStatusViews(targetSketch.Geometries);
            foreach (FakeM3Geometry geometry in targetSketch.Geometries)
                state.ProjectionTargetBefore.Add(CopyFakeM3Geometry(geometry));
            foreach (FakeM3Geometry source in sources)
                state.ProjectionSourceBefore.Add(CopyFakeM3Geometry(source));
            foreach (FakeM3Body body in SortedM3Bodies()) state.NonTargetBefore.Add(M3BodyView(body));

            state.ProjectionEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "adapter", "M2StaHarness.FakeModelAdapter" },
                { "native_nx_invoked", false },
                { "associative", request.Associative },
                { "direction_xyz", new List<double>(direction) },
                { "source_records", sourceRecords },
                { "expected_output_curve_count", outputs.Count },
                { "output_curve_records", outputRecords }
            };
            Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", state.Kind }, { "sketch_id", targetSketch.Id }, { "feature_id", state.FeatureId },
                { "source_curve_ids", new List<string>(request.SourceCurveIds) },
                { "source_count", sources.Count }, { "associative", request.Associative },
                { "direction_xyz", new List<double>(direction) },
                { "expected_output_curve_count", outputs.Count },
                { "source_records", sourceRecords }, { "expected_output_curve_records", outputRecords }
            };
            return new M3PlanSnapshot {
                Kind = state.Kind, RevisionId = request.RevisionId, SketchId = targetSketch.Id,
                FeatureId = state.FeatureId, PublicView = publicView, AdapterState = state
            };
        }

        public M3PlanSnapshot PrepareSketchReferenceConversion(M2ModelBinding binding,
            M3SketchReferenceConversionRequest request)
        {
            System.Threading.Interlocked.Increment(ref m3SketchReferenceConversionPrepareCalls);
            RecordM3SketchReferenceConversionThread();
            if (request == null || !String.Equals(request.RevisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture revision changed before F11 reference conversion planning");
            if (String.IsNullOrWhiteSpace(request.SketchId) || request.ObjectIds == null ||
                request.ObjectIds.Count < 1 || request.ObjectIds.Count > 256)
                throw new M2ModelException("INVALID_ARGUMENT", "fixture F11 request requires a Sketch and 1 through 256 object IDs");
            if (request.TargetState != "reference" && request.TargetState != "active")
                throw new M2ModelException("INVALID_ARGUMENT", "fixture F11 target_state must be reference or active");

            FakeM3Sketch sketch;
            if (!m3Sketches.TryGetValue(request.SketchId, out sketch))
                throw new M2ModelException("SKETCH_NOT_FOUND", "fixture F11 Sketch was not found");

            bool targetReference = String.Equals(request.TargetState, "reference", StringComparison.Ordinal);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<object> objectRows = new List<object>();
            FakeM3PlanState state = new FakeM3PlanState {
                Kind = "edit_sketch_reference_conversion", FeatureId = sketch.FeatureId, Sketch = sketch,
                ReferenceConversionRequest = request,
                ReferenceConversionTargetState = request.TargetState
            };
            foreach (string objectId in request.ObjectIds)
            {
                if (String.IsNullOrWhiteSpace(objectId) || !seen.Add(objectId))
                    throw new M2ModelException("INVALID_ARGUMENT", "fixture F11 object IDs must be unique nonempty current Journal IDs");

                FakeM3Geometry curve = sketch.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.JournalId, objectId, StringComparison.Ordinal);
                });
                if (curve != null)
                {
                    if (curve.Type != "line" && curve.Type != "arc" && curve.Type != "circle")
                        throw new M2ModelException("UNSUPPORTED_GEOMETRY", "fixture F11 supports Line and circular curve geometry only");
                    bool willChange = (curve.ReferenceStatus == 0) != targetReference;
                    objectRows.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "object_id", objectId }, { "kind", "curve" }, { "tag", curve.Id },
                        { "journal_id", curve.JournalId },
                        { "current_state", curve.ReferenceStatus == 0 ? "reference" : "active" },
                        { "requested_state", request.TargetState }, { "expected_after", request.TargetState },
                        { "will_change", willChange }, { "geometry_before", M3GeometryView(curve) }
                    });
                    state.ReferenceConversionSelectedCurves.Add(curve);
                    state.ReferenceConversionCurveStatesBefore.Add(curve.ReferenceStatus);
                    if (willChange) state.ReferenceConversionChangedCount++;
                    continue;
                }

                FakeM3Constraint dimension = sketch.Constraints.Find(delegate(FakeM3Constraint item) {
                    return String.Equals(item.Id, objectId, StringComparison.Ordinal);
                });
                if (dimension == null)
                    throw new M2ModelException("OBJECT_NOT_FOUND", "fixture F11 object ID is not a current Curve or Sketch dimensional-constraint ID in the identified Sketch");
                if (dimension.Type != "HorizontalDim" && dimension.Type != "VerticalDim")
                    throw new M2ModelException("UNSUPPORTED_DIMENSION", "fixture F11 supports HorizontalDim and VerticalDim millimetre dimensions only");
                bool willChangeDimension = dimension.IsDriving != !targetReference;
                objectRows.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "object_id", objectId }, { "kind", "dimension" }, { "tag", dimension.Id },
                    { "journal_id", dimension.Id },
                    { "constraint_type", dimension.Type },
                    { "current_state", dimension.IsDriving ? "active" : "reference" },
                    { "requested_state", request.TargetState }, { "expected_after", request.TargetState },
                    { "will_change", willChangeDimension }, { "expression_name", dimension.ExpressionName },
                    { "expression_rhs", M3NumberText(dimension.Value) },
                    { "computed_size", dimension.Value }, { "geometry_refs", new List<string>(dimension.GeometryRefs) }
                });
                state.ReferenceConversionSelectedDimensions.Add(dimension);
                state.ReferenceConversionDimensionStatesBefore.Add(dimension.IsDriving);
                if (willChangeDimension) state.ReferenceConversionChangedCount++;
            }

            if (state.ReferenceConversionChangedCount == 0)
                throw new M2ModelException("INVALID_ARGUMENT", "every selected fixture F11 object is already at the requested target_state");

            state.ReferenceConversionObjectsBefore = objectRows;
            state.ReferenceConversionGeometryBefore = M3ReferenceConversionGeometryViews(sketch);
            state.ReferenceConversionConstraintsBefore = M3ConstraintViews(sketch);
            state.ReferenceConversionReferenceStatesBefore = FakeM3ReferenceStatusViews(sketch.Geometries);
            state.ReferenceConversionFrameBefore = FakeM3SketchFrameView(sketch);
            foreach (FakeM3Feature feature in m3Features) state.ReferenceConversionFeatureIdsBefore.Add(feature.Id);
            foreach (FakeM3Body body in SortedM3Bodies()) state.NonTargetBefore.Add(M3BodyView(body));

            List<object> candidates = new List<object>();
            foreach (FakeM3Geometry geometry in sketch.Geometries)
                if (geometry.ReferenceStatus == 1)
                    candidates.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "tag", geometry.Id }, { "journal_id", geometry.JournalId }, { "type", geometry.Type }
                    });
            state.ReferenceConversionCandidatesBefore = candidates;
            Dictionary<string, object> solver = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "status", "FIXTURE_ONLY" },
                { "degrees_of_freedom", Math.Max(0, sketch.Geometries.Count * 2 - sketch.Constraints.Count) },
                { "fixture_only", true }, { "native_nx_invoked", false }
            };
            Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", state.Kind }, { "sketch_id", sketch.Id }, { "feature_id", sketch.FeatureId },
                { "object_ids", new List<string>(request.ObjectIds) }, { "target_state", request.TargetState },
                { "objects", objectRows }, { "solver_before", solver },
                { "reference_states_before", state.ReferenceConversionReferenceStatesBefore },
                { "contour_candidates_before", new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "candidate_count", candidates.Count }, { "curves", candidates },
                    { "evidence_kind", "fixture UF-status model; not a native NX Section" }
                } },
                { "changed_object_count", state.ReferenceConversionChangedCount },
                { "native_nx_invoked", false }
            };
            return new M3PlanSnapshot {
                Kind = state.Kind, RevisionId = request.RevisionId, SketchId = sketch.Id,
                FeatureId = sketch.FeatureId, PublicView = publicView, AdapterState = state
            };
        }

        private FakeM3Geometry FindFakeM3ProjectionSource(string sourceId, out FakeM3Sketch ownerSketch)
        {
            ownerSketch = null;
            FakeM3Geometry found = null;
            foreach (FakeM3Sketch sketch in m3Sketches.Values)
            {
                foreach (FakeM3Geometry geometry in sketch.Geometries)
                {
                    if (!String.Equals(geometry.Id, sourceId, StringComparison.Ordinal) &&
                        !String.Equals(geometry.JournalId, sourceId, StringComparison.Ordinal)) continue;
                    if (found != null)
                        throw new M2ModelException("CURVE_ID_AMBIGUOUS", "fixture F10 source ID resolves to more than one fake Curve");
                    found = geometry;
                    ownerSketch = sketch;
                }
            }
            return found;
        }

        private static FakeM3Geometry M3ProjectFakeGeometry(FakeM3Geometry source,
            FakeM3Sketch sourceSketch, FakeM3Sketch targetSketch, double[] direction,
            string outputId, string outputJournalId)
        {
            FakeM3Geometry output = CopyFakeM3Geometry(source);
            output.Id = outputId;
            output.JournalId = outputJournalId;
            if (source.Type == "line")
            {
                output.Start = M3ProjectionPointToSketch(source.Start, sourceSketch, targetSketch, direction);
                output.End = M3ProjectionPointToSketch(source.End, sourceSketch, targetSketch, direction);
            }
            else if (source.Type == "circle")
            {
                double[] sourceOrigin, sourceNormal, sourceXAxis, sourceYAxis;
                double[] targetOrigin, targetNormal, targetXAxis, targetYAxis;
                FakeM3SketchBasis(sourceSketch, out sourceOrigin, out sourceNormal, out sourceXAxis, out sourceYAxis);
                FakeM3SketchBasis(targetSketch, out targetOrigin, out targetNormal, out targetXAxis, out targetYAxis);
                if (Math.Abs(M3ProjectionDot(sourceNormal, targetNormal)) < 1.0 - 1e-10)
                    throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "fixture F10 cannot preserve a Circle projected from a nonparallel plane");
                output.Center = M3ProjectionPointToSketch(source.Center, sourceSketch, targetSketch, direction);
            }
            if (!M3GeometryFinite(output))
                throw new M2ModelException("INVALID_REQUEST", "fixture F10 projected coordinates are not finite");
            return output;
        }

        private static double[] M3ProjectionPointToSketch(double[] point, FakeM3Sketch sourceSketch,
            FakeM3Sketch targetSketch, double[] direction)
        {
            if (point == null || point.Length != 2)
                throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "fixture F10 source point is not a 2D Sketch point");
            double[] sourceOrigin, sourceNormal, sourceXAxis, sourceYAxis;
            double[] targetOrigin, targetNormal, targetXAxis, targetYAxis;
            FakeM3SketchBasis(sourceSketch, out sourceOrigin, out sourceNormal, out sourceXAxis, out sourceYAxis);
            FakeM3SketchBasis(targetSketch, out targetOrigin, out targetNormal, out targetXAxis, out targetYAxis);
            double[] world = new double[3];
            for (int index = 0; index < 3; index++)
                world[index] = sourceOrigin[index] + point[0] * sourceXAxis[index] + point[1] * sourceYAxis[index];
            double denominator = M3ProjectionDot(targetNormal, direction);
            if (Math.Abs(denominator) <= 1e-12)
                throw new M2ModelException("UNSUPPORTED_PROJECTION_DIRECTION", "fixture F10 projection direction is parallel to the target Sketch plane");
            double distance = M3ProjectionDot(new double[] {
                targetOrigin[0] - world[0], targetOrigin[1] - world[1], targetOrigin[2] - world[2]
            }, targetNormal) / denominator;
            for (int index = 0; index < 3; index++) world[index] += distance * direction[index];
            double[] relative = new double[] {
                world[0] - targetOrigin[0], world[1] - targetOrigin[1], world[2] - targetOrigin[2]
            };
            double[] result = new double[] { M3ProjectionDot(relative, targetXAxis), M3ProjectionDot(relative, targetYAxis) };
            if (!M3Finite(result[0]) || !M3Finite(result[1]))
                throw new M2ModelException("INVALID_REQUEST", "fixture F10 projection produced non-finite local coordinates");
            return result;
        }

        private static void FakeM3SketchBasis(FakeM3Sketch sketch, out double[] origin,
            out double[] normal, out double[] xAxis, out double[] yAxis)
        {
            origin = sketch.Origin == null ? new double[] { 0.0, 0.0, 0.0 } : (double[])sketch.Origin.Clone();
            if (sketch.Normal != null && sketch.XAxis != null)
            {
                normal = M3ProjectionUnit(sketch.Normal, "sketch normal");
                xAxis = M3ProjectionUnit(sketch.XAxis, "sketch x_axis");
            }
            else if (String.Equals(sketch.Plane, "XZ", StringComparison.Ordinal))
            {
                normal = new double[] { 0.0, -1.0, 0.0 };
                xAxis = new double[] { 1.0, 0.0, 0.0 };
            }
            else if (String.Equals(sketch.Plane, "YZ", StringComparison.Ordinal))
            {
                normal = new double[] { 1.0, 0.0, 0.0 };
                xAxis = new double[] { 0.0, 1.0, 0.0 };
            }
            else
            {
                normal = new double[] { 0.0, 0.0, 1.0 };
                xAxis = new double[] { 1.0, 0.0, 0.0 };
            }
            double projection = M3ProjectionDot(normal, xAxis);
            for (int index = 0; index < 3; index++) xAxis[index] -= projection * normal[index];
            xAxis = M3ProjectionUnit(xAxis, "sketch x_axis in plane");
            yAxis = M3ProjectionUnit(M3ProjectionCross(normal, xAxis), "sketch y_axis");
        }

        private static double[] M3ProjectionUnit(double[] vector, string label)
        {
            if (vector == null || vector.Length != 3 || !M3Finite(vector[0]) || !M3Finite(vector[1]) || !M3Finite(vector[2]))
                throw new M2ModelException("INVALID_REQUEST", label + " must be a finite 3D vector");
            double scale = Math.Max(Math.Abs(vector[0]), Math.Max(Math.Abs(vector[1]), Math.Abs(vector[2])));
            if (scale <= 0.0) throw new M2ModelException("INVALID_REQUEST", label + " must be nonzero");
            double x = vector[0] / scale, y = vector[1] / scale, z = vector[2] / scale;
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (!M3Finite(length) || length <= 0.0) throw new M2ModelException("INVALID_REQUEST", label + " must be finite and nonzero");
            return new double[] { x / length, y / length, z / length };
        }

        private static double M3ProjectionDot(double[] first, double[] second)
        {
            return first[0] * second[0] + first[1] * second[1] + first[2] * second[2];
        }

        private static double[] M3ProjectionCross(double[] first, double[] second)
        {
            return new double[] {
                first[1] * second[2] - first[2] * second[1],
                first[2] * second[0] - first[0] * second[2],
                first[0] * second[1] - first[1] * second[0]
            };
        }

        private static double[] M3PatternUnit(double[] value, string label)
        {
            if (value == null || value.Length != 2 || !M3Finite(value[0]) || !M3Finite(value[1]))
                throw new M2ModelException("INVALID_REQUEST", label + " must be a finite 2D vector");
            double scale = Math.Max(Math.Abs(value[0]), Math.Abs(value[1]));
            if (scale <= 0.0) throw new M2ModelException("INVALID_REQUEST", label + " must be nonzero");
            double x = value[0] / scale, y = value[1] / scale;
            double norm = Math.Sqrt(x * x + y * y);
            return new double[] { x / norm, y / norm };
        }

        private static bool M3Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }

        private FakeM3Geometry M3TransformPatternGeometry(FakeM3Geometry source,
            FakeM3PatternPosition position, int outputNumber)
        {
            FakeM3Geometry result = CopyFakeM3Geometry(source);
            result.Id = "f09-output-" + outputNumber.ToString(CultureInfo.InvariantCulture) + "-" + position.Index.ToString(CultureInfo.InvariantCulture);
            result.JournalId = "m3-pattern-output-" + outputNumber.ToString(CultureInfo.InvariantCulture) + "-" + position.Index.ToString(CultureInfo.InvariantCulture);
            if (result.Start != null) result.Start = M3PatternTransformPoint(result.Start, position);
            if (result.End != null) result.End = M3PatternTransformPoint(result.End, position);
            if (result.Center != null) result.Center = M3PatternTransformPoint(result.Center, position);
            return result;
        }

        private static double[] M3PatternTransformPoint(double[] point, FakeM3PatternPosition position)
        {
            double x, y;
            if (position.Kind == "reflection")
            {
                double vx = point[0] - position.AxisPoint[0], vy = point[1] - position.AxisPoint[1];
                double projection = vx * position.AxisDirection[0] + vy * position.AxisDirection[1];
                x = position.AxisPoint[0] + 2.0 * projection * position.AxisDirection[0] - vx;
                y = position.AxisPoint[1] + 2.0 * projection * position.AxisDirection[1] - vy;
            }
            else if (position.Kind == "rotation")
            {
                double dx = point[0] - position.Center[0], dy = point[1] - position.Center[1];
                double cosine = Math.Cos(position.AngleRad), sine = Math.Sin(position.AngleRad);
                x = position.Center[0] + cosine * dx - sine * dy;
                y = position.Center[1] + sine * dx + cosine * dy;
            }
            else
            {
                x = point[0] + position.Translation[0];
                y = point[1] + position.Translation[1];
            }
            return new double[] { x, y };
        }

        private static Dictionary<string, object> M3PatternTransformView(FakeM3PatternPosition position, FakeM3Geometry axis)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", position.Kind }, { "translation_xy_mm", new List<double>(position.Translation) }
            };
            if (position.Kind == "rotation") {
                result["center_xy_mm"] = new List<double>(position.Center);
                result["angle_rad"] = position.AngleRad;
            }
            if (position.Kind == "reflection") {
                result["axis_curve_id"] = axis.Id;
                result["axis_start_xy_mm"] = new List<double>(axis.Start);
                result["axis_end_xy_mm"] = new List<double>(axis.End);
                result["axis_direction_xy"] = new List<double>(position.AxisDirection);
            }
            return result;
        }

        private static bool M3PatternGeometryEqual(FakeM3Geometry first, FakeM3Geometry second)
        {
            if (first == null || second == null || first.Type != second.Type) return false;
            if (first.Start != null && second.Start != null && first.End != null && second.End != null)
            {
                bool endpointsMatch = (FakeM3Distance(first.Start, second.Start) <= 1e-9 && FakeM3Distance(first.End, second.End) <= 1e-9) ||
                    (FakeM3Distance(first.Start, second.End) <= 1e-9 && FakeM3Distance(first.End, second.Start) <= 1e-9);
                if (!endpointsMatch) return false;
                if (first.Type == "arc")
                    return first.Center != null && second.Center != null && FakeM3Distance(first.Center, second.Center) <= 1e-9 &&
                        Math.Abs(first.Radius - second.Radius) <= 1e-9;
                return true;
            }
            if (first.Center != null && second.Center != null)
                return FakeM3Distance(first.Center, second.Center) <= 1e-9 && Math.Abs(first.Radius - second.Radius) <= 1e-9 &&
                    Math.Abs(first.Diameter - second.Diameter) <= 1e-9;
            return false;
        }

        private static bool M3GeometryFinite(FakeM3Geometry geometry)
        {
            if (geometry.Start != null && (!M3Finite(geometry.Start[0]) || !M3Finite(geometry.Start[1]))) return false;
            if (geometry.End != null && (!M3Finite(geometry.End[0]) || !M3Finite(geometry.End[1]))) return false;
            if (geometry.Center != null && (!M3Finite(geometry.Center[0]) || !M3Finite(geometry.Center[1]))) return false;
            if (geometry.Type == "arc" && (!M3Finite(geometry.Radius) || geometry.Radius <= 0.0)) return false;
            if (geometry.Type == "circle" && (!M3Finite(geometry.Diameter) || geometry.Diameter <= 0.0)) return false;
            return true;
        }

        public M3PlanSnapshot PrepareSketchCorner(M2ModelBinding binding, M3SketchCornerRequest request)
        {
            if (request == null) throw new M2ModelException("INVALID_REQUEST", "fixture corner request is null");
            if (!String.Equals(request.RevisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture revision changed before sketch-corner planning");
            if (String.IsNullOrWhiteSpace(request.SketchId))
                throw new M2ModelException("INVALID_REQUEST", "fixture corner sketch_id is required");
            if (String.Equals(request.Curve1Id, request.Curve2Id, StringComparison.Ordinal))
                throw new M2ModelException("INVALID_REQUEST", "fixture corner requires two distinct geometry IDs");
            if ((request.Curve1End != "start" && request.Curve1End != "end") ||
                (request.Curve2End != "start" && request.Curve2End != "end"))
                throw new M2ModelException("INVALID_REQUEST", "fixture endpoint must be start or end");

            FakeM3Sketch sketch;
            if (!m3Sketches.TryGetValue(request.SketchId, out sketch))
                throw new M2ModelException("SKETCH_NOT_FOUND", "fixture sketch-corner target Sketch was not found");
            FakeM3Geometry curve1 = sketch.Geometries.Find(delegate(FakeM3Geometry item) {
                return String.Equals(item.Id, request.Curve1Id, StringComparison.Ordinal) ||
                    String.Equals(item.JournalId, request.Curve1Id, StringComparison.Ordinal);
            });
            FakeM3Geometry curve2 = sketch.Geometries.Find(delegate(FakeM3Geometry item) {
                return String.Equals(item.Id, request.Curve2Id, StringComparison.Ordinal) ||
                    String.Equals(item.JournalId, request.Curve2Id, StringComparison.Ordinal);
            });
            if (curve1 == null || curve2 == null)
                throw new M2ModelException("SKETCH_TARGET_MISMATCH", "fixture curve ID does not belong to the explicit sketch");
            if (!String.Equals(curve1.Type, "line", StringComparison.Ordinal) ||
                !String.Equals(curve2.Type, "line", StringComparison.Ordinal))
                throw new M2ModelException("UNSUPPORTED_CURVE_TYPE", "fixture corner adapter supports Line-Line geometry only");
            if (request.Kind != "fillet" && request.Kind != "chamfer")
                throw new M2ModelException("INVALID_REQUEST", "fixture corner kind must be fillet or chamfer");

            double[] selected1 = FakeM3Endpoint(curve1, request.Curve1End);
            double[] selected2 = FakeM3Endpoint(curve2, request.Curve2End);
            if (FakeM3Distance(selected1, selected2) > 1e-9)
                throw new M2ModelException("CORNER_NOT_SHARED", "fixture selected endpoints do not share a corner");
            double[] far1 = FakeM3Endpoint(curve1, request.Curve1End == "start" ? "end" : "start");
            double[] far2 = FakeM3Endpoint(curve2, request.Curve2End == "start" ? "end" : "start");
            double[] e1 = FakeM3Unit(new double[] { far1[0] - selected1[0], far1[1] - selected1[1] });
            double[] e2 = FakeM3Unit(new double[] { far2[0] - selected2[0], far2[1] - selected2[1] });
            double theta = Math.Atan2(Math.Abs(e1[0] * e2[1] - e1[1] * e2[0]), e1[0] * e2[0] + e1[1] * e2[1]);
            if (theta <= 1e-12 || theta >= Math.PI - 1e-12)
                throw new M2ModelException("INVALID_CORNER", "fixture selected Lines do not form a non-degenerate corner");

            double distance1;
            double distance2;
            double[] cut1;
            double[] cut2;
            double[] center = null;
            if (request.Kind == "fillet")
            {
                if (!M3FinitePositive(request.RadiusMm))
                    throw new M2ModelException("INVALID_REQUEST", "fixture fillet radius must be positive and finite");
                distance1 = distance2 = request.RadiusMm / Math.Tan(theta / 2.0);
                if (distance1 >= FakeM3Distance(selected1, far1) || distance2 >= FakeM3Distance(selected2, far2))
                    throw new M2ModelException("INVALID_CORNER_SIZE", "fixture fillet exceeds a retained Line length");
                cut1 = new double[] { selected1[0] + distance1 * e1[0], selected1[1] + distance1 * e1[1] };
                cut2 = new double[] { selected2[0] + distance2 * e2[0], selected2[1] + distance2 * e2[1] };
                double[] bisector = FakeM3Unit(new double[] { e1[0] + e2[0], e1[1] + e2[1] });
                center = new double[] { selected1[0] + request.RadiusMm / Math.Sin(theta / 2.0) * bisector[0],
                    selected1[1] + request.RadiusMm / Math.Sin(theta / 2.0) * bisector[1] };
            }
            else
            {
                if (!M3FinitePositive(request.Distance1Mm))
                    throw new M2ModelException("INVALID_REQUEST", "fixture chamfer distance1 must be positive and finite");
                distance1 = request.Distance1Mm;
                if (request.Distance2Mm.HasValue)
                {
                    if (!M3FinitePositive(request.Distance2Mm.Value))
                        throw new M2ModelException("INVALID_REQUEST", "fixture chamfer distance2 must be positive and finite");
                    distance2 = request.Distance2Mm.Value;
                }
                else if (request.AngleDeg.HasValue && request.AngleDeg.Value > 0.0 && request.AngleDeg.Value < 180.0 - theta * 180.0 / Math.PI)
                    distance2 = distance1 * Math.Sin(request.AngleDeg.Value * Math.PI / 180.0) /
                        Math.Sin(theta + request.AngleDeg.Value * Math.PI / 180.0);
                else throw new M2ModelException("INVALID_REQUEST", "fixture chamfer requires a valid distance2 or distance-angle");
                if (distance1 >= FakeM3Distance(selected1, far1) || distance2 >= FakeM3Distance(selected2, far2))
                    throw new M2ModelException("INVALID_CORNER_SIZE", "fixture chamfer exceeds a retained Line length");
                cut1 = new double[] { selected1[0] + distance1 * e1[0], selected1[1] + distance1 * e1[1] };
                cut2 = new double[] { selected2[0] + distance2 * e2[0], selected2[1] + distance2 * e2[1] };
            }

            FakeM3PlanState state = new FakeM3PlanState {
                Kind = "edit_sketch_corner", FeatureId = sketch.FeatureId, Sketch = sketch,
                CornerRequest = request, CornerCurve1 = curve1, CornerCurve2 = curve2,
                CornerCurve1Before = CopyFakeM3Geometry(curve1), CornerCurve2Before = CopyFakeM3Geometry(curve2),
                CornerBefore = new double[] { selected1[0], selected1[1] },
                CornerCut1 = cut1, CornerCut2 = cut2, CornerCenter = center,
                CornerRay1 = e1, CornerRay2 = e2, CornerAngleRad = theta
            };
            foreach (FakeM3Geometry geometry in sketch.Geometries)
                if (!Object.ReferenceEquals(geometry, curve1) && !Object.ReferenceEquals(geometry, curve2))
                    state.CornerNonTargetsBefore.Add(CopyFakeM3Geometry(geometry));
            foreach (FakeM3Constraint constraint in sketch.Constraints) state.CornerConstraintIdsBefore.Add(constraint.Id);
            state.CornerConstraintsBefore = M3ConstraintViews(sketch);
            foreach (FakeM3Body body in SortedM3Bodies()) state.NonTargetBefore.Add(M3BodyView(body));
            Dictionary<string, object> definition = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", request.Kind }, { "sketch_id", sketch.Id },
                { "curve1_id", request.Curve1Id }, { "curve1_end", request.Curve1End },
                { "curve2_id", request.Curve2Id }, { "curve2_end", request.Curve2End },
                { "corner_mm", new List<double>(selected1) }, { "cut1_mm", new List<double>(cut1) },
                { "cut2_mm", new List<double>(cut2) }, { "distance1_mm", distance1 }, { "distance2_mm", distance2 }
            };
            if (request.Kind == "fillet") definition["radius_mm"] = request.RadiusMm;
            if (request.AngleDeg.HasValue) definition["angle_deg"] = request.AngleDeg.Value;
            state.NativeDefinition = definition;
            Dictionary<string, object> view = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", state.Kind }, { "sketch_id", sketch.Id }, { "feature_id", sketch.FeatureId },
                { "requested_definition", request.Kind }, { "targets_before", new List<object> { M3GeometryView(curve1), M3GeometryView(curve2) } },
                { "definition", definition }, { "constraints_before", M3ConstraintViews(sketch) },
                { "non_target_geometry_before", M3GeometryViewList(state.CornerNonTargetsBefore) }
            };
            return new M3PlanSnapshot {
                Kind = state.Kind, RevisionId = request.RevisionId, SketchId = sketch.Id,
                FeatureId = sketch.FeatureId, PublicView = view, AdapterState = state
            };
        }

        private static double[] FakeM3Endpoint(FakeM3Geometry geometry, string endpoint)
        {
            return endpoint == "start" ? geometry.Start : geometry.End;
        }

        private static double FakeM3Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double[] FakeM3Unit(double[] vector)
        {
            double length = Math.Sqrt(vector[0] * vector[0] + vector[1] * vector[1]);
            if (length <= 1e-15) throw new M2ModelException("INVALID_CORNER", "fixture corner has a zero-length retained line");
            return new double[] { vector[0] / length, vector[1] / length };
        }

        private static bool M3FinitePositive(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value) && value > 0.0;
        }

        private static FakeM3Geometry CopyFakeM3Geometry(FakeM3Geometry source)
        {
            return new FakeM3Geometry {
                Id = source.Id, JournalId = source.JournalId, Type = source.Type,
                Start = source.Start == null ? null : (double[])source.Start.Clone(),
                End = source.End == null ? null : (double[])source.End.Clone(),
                Corner = source.Corner == null ? null : (double[])source.Corner.Clone(),
                Center = source.Center == null ? null : (double[])source.Center.Clone(),
                Width = source.Width, Height = source.Height, Diameter = source.Diameter, Radius = source.Radius,
                ReferenceStatus = source.ReferenceStatus
            };
        }

        private static List<object> M3GeometryViewList(List<FakeM3Geometry> geometries)
        {
            List<object> result = new List<object>();
            foreach (FakeM3Geometry geometry in geometries) result.Add(M3GeometryView(geometry));
            return result;
        }

        private static Dictionary<string, object> FakeM3SketchFrameView(FakeM3Sketch sketch)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "plane", sketch == null ? null : sketch.Plane },
                { "origin_mm", sketch == null || sketch.Origin == null ? null : new List<double>(sketch.Origin) },
                { "normal", sketch == null || sketch.Normal == null ? null : new List<double>(sketch.Normal) },
                { "x_axis", sketch == null || sketch.XAxis == null ? null : new List<double>(sketch.XAxis) },
                { "support_face_selector", sketch == null ? null : sketch.SupportFaceSelector },
                { "support_body_id", sketch == null ? null : sketch.SupportBodyId },
                { "face_origin_offset_mm", sketch == null || sketch.FaceOriginOffsetMm == null ? null : new List<double>(sketch.FaceOriginOffsetMm) }
            };
        }

        private static List<object> FakeM3ReferenceStatusViews(List<FakeM3Geometry> geometries)
        {
            List<object> result = new List<object>();
            foreach (FakeM3Geometry geometry in geometries)
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "curve_id", geometry.Id }, { "journal_id", geometry.JournalId },
                    { "reference_status", geometry.ReferenceStatus }
                });
            return result;
        }

        private FakeM3Geometry CopyM3Geometry(string sketchId, M3SketchGeometryRequest request)
        {
            FakeM3Geometry geometry = new FakeM3Geometry {
                Id = request.Id,
                JournalId = "m3-geometry-" + sketchId + "-" + request.Id,
                Type = request.Type,
                Start = request.StartMm == null ? null : (double[])request.StartMm.Clone(),
                End = request.EndMm == null ? null : (double[])request.EndMm.Clone(),
                Corner = request.CornerMm == null ? null : (double[])request.CornerMm.Clone(),
                Center = request.CenterMm == null ? null : (double[])request.CenterMm.Clone(),
                Width = request.WidthMm,
                Height = request.HeightMm,
                Diameter = request.DiameterMm,
                Radius = request.RadiusMm
            };
            return geometry;
        }

        private void AddM3Constraint(FakeM3Sketch sketch, FakeM3Geometry geometry, string role,
            string type, double value, List<string> geometryRefs, List<object> publicDimensions)
        {
            int number = System.Threading.Interlocked.Increment(ref m3ConstraintNumber);
            FakeM3Constraint constraint = new FakeM3Constraint {
                Id = "m3-constraint-" + number.ToString(CultureInfo.InvariantCulture),
                GeometryId = geometry.Id,
                Role = role,
                Type = type,
                ExpressionName = "m3_expr_" + number.ToString(CultureInfo.InvariantCulture),
                Value = value,
                IsDriving = true,
                Sketch = sketch,
                Geometry = geometry,
                GeometryRefs = new List<string>(geometryRefs)
            };
            sketch.Constraints.Add(constraint);
            publicDimensions.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                { "geometry_id", geometry.Id }, { "role", role }, { "constraint_type", type },
                { "value", value }, { "unit", "mm" },
                { "geometry_refs_by_role", new List<string>(geometryRefs) }
            });
        }

        public M3PlanSnapshot PrepareThread(M2ModelBinding binding, M7ThreadRequest request)
        {
            return PrepareM7Fixture(binding, request.RevisionId, request.TargetBodyId, "create_thread",
                new Dictionary<string, object> { { "pitch_mm", request.PitchMm }, { "major_diameter_mm", request.MajorDiameterMm },
                    { "minor_diameter_mm", request.MinorDiameterMm }, { "handedness", request.Handedness }, { "direction", request.Direction },
                    { "axis", request.Axis }, { "axis_origin_mm", new List<double>(request.AxisOriginMm) },
                    { "start_mm", request.StartMm }, { "length_mm", request.LengthMm }, { "profile", "external_single_start_60deg_detailed" } });
        }

        public M3PlanSnapshot PrepareHole(M2ModelBinding binding, M3HoleRequest request)
        {
            // Synthetic state for bridge lifecycle only; no NX geometry or thread validation.
            System.Threading.Interlocked.Increment(ref m3HolePrepareCalls);
            if (System.Threading.Interlocked.Exchange(ref unknownNextM3Preparation, 0) != 0)
                throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN", "fixture hole preparation outcome unknown");
            M3PlanSnapshot plan = PrepareM7Fixture(binding, request.RevisionId, request.TargetBodyId, "create_hole",
                new Dictionary<string, object> { { "kind", request.Kind }, { "depth_mode", request.DepthMode },
                    { "position_mm", new List<double>(request.PositionMm) }, { "direction", new List<double>(request.Direction) },
                    { "fixture_only", true } });
            plan.PublicView["target_body_id"] = request.TargetBodyId;
            return plan;
        }

        public M3PlanSnapshot PrepareHelicalGroove(M2ModelBinding binding, M7HelicalGrooveRequest request)
        {
            return PrepareM7Fixture(binding, request.RevisionId, request.TargetBodyId, "create_helical_groove",
                new Dictionary<string, object> { { "lead_mm", request.LeadMm }, { "root_diameter_mm", request.RootDiameterMm },
                    { "reference_diameter_mm", request.ReferenceDiameterMm }, { "outer_diameter_mm", request.OuterDiameterMm },
                    { "space_width_mm", request.SpaceWidthMm }, { "flank_angle_deg", request.FlankAngleDeg }, { "phase_deg", request.PhaseDeg },
                    { "handedness", request.Handedness }, { "axis", request.Axis }, { "axis_origin_mm", new List<double>(request.AxisOriginMm) },
                    { "start_mm", request.StartMm }, { "length_mm", request.LengthMm } });
        }

        private M3PlanSnapshot PrepareM7Fixture(M2ModelBinding binding, string revision, string bodyId, string kind,
            Dictionary<string, object> definition)
        {
            if (revision != RevisionId) throw new M2ModelException("STALE_REVISION", "fixture revision changed");
            FakeM3Body target;
            if (!m3Bodies.TryGetValue(bodyId, out target)) throw new M2ModelException("TARGET_BODY_NOT_FOUND", "fixture helical body not found");
            string feature = "m7-feature-" + System.Threading.Interlocked.Increment(ref m3FeatureNumber).ToString(CultureInfo.InvariantCulture);
            FakeM3PlanState state = new FakeM3PlanState { Kind = kind, FeatureId = feature, BodyId = bodyId,
                TargetBodyId = bodyId, TargetBefore = M3BodyView(target), NativeDefinition = definition };
            foreach (FakeM3Body body in SortedM3Bodies())
                if (body.Id != bodyId) state.NonTargetBefore.Add(M3BodyView(body));
            return new M3PlanSnapshot { Kind = kind, RevisionId = revision, BodyId = bodyId, FeatureId = feature,
                PublicView = new Dictionary<string, object> { { "kind", kind }, { "body_id", bodyId }, { "parameters", definition } }, AdapterState = state };
        }

        public M3PlanSnapshot PrepareExtrude(M2ModelBinding binding, M3ExtrudeRequest request)
        {
            System.Threading.Interlocked.Increment(ref m3ExtrudePrepareCalls);
            if (System.Threading.Interlocked.Exchange(ref unknownNextM3Preparation, 0) != 0)
                throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN",
                    "fixture simulates an unverified NX Section cleanup state during extrusion prepare");
            if (!String.Equals(request.RevisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture revision changed before extrusion planning");
            FakeM3Sketch sketch;
            if (!m3Sketches.TryGetValue(request.SketchId, out sketch))
                throw new M2ModelException("SKETCH_NOT_FOUND", "fixture extrusion sketch was not found");

            FakeM3Body target = null;
            if (!String.Equals(request.Mode, "create", StringComparison.Ordinal))
            {
                if (String.IsNullOrEmpty(request.TargetBodyId) || !m3Bodies.TryGetValue(request.TargetBodyId, out target))
                    throw new M2ModelException("TARGET_BODY_NOT_FOUND", "fixture boolean target body was not found");
            }

            int number = System.Threading.Interlocked.Increment(ref m3FeatureNumber);
            string featureId = "m3-extrude-feature-" + number.ToString(CultureInfo.InvariantCulture);
            string bodyId = String.Equals(request.Mode, "create", StringComparison.Ordinal)
                ? "m3-body-" + System.Threading.Interlocked.Increment(ref m3BodyNumber).ToString(CultureInfo.InvariantCulture)
                : request.TargetBodyId;
            Dictionary<string, object> targetBefore = target == null ? null : M3BodyView(target);
            List<Dictionary<string, object>> nonTargetBefore = new List<Dictionary<string, object>>();
            foreach (FakeM3Body body in SortedM3Bodies())
                if (target == null || !String.Equals(body.Id, target.Id, StringComparison.Ordinal))
                    nonTargetBefore.Add(M3BodyView(body));

            FakeM3PlanState state = new FakeM3PlanState {
                Kind = "create_extrude", FeatureId = featureId, BodyId = bodyId, Sketch = sketch,
                Extrude = request, TargetBodyId = target == null ? null : target.Id,
                TargetBefore = targetBefore, NonTargetBefore = nonTargetBefore
            };
            List<object> nonTargetView = new List<object>();
            foreach (Dictionary<string, object> body in nonTargetBefore) nonTargetView.Add(body);
            Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", state.Kind }, { "sketch_id", sketch.Id }, { "feature_id", featureId },
                { "body_id", bodyId }, { "mode", request.Mode }, { "direction", request.Direction },
                { "extent", request.Extent }, { "profile_output", String.IsNullOrEmpty(request.ProfileOutput) ? "solid" : request.ProfileOutput },
                { "start_offset_mm", request.StartOffsetMm },
                { "end_offset_mm", request.Extent == "finite" ? (object)request.EndOffsetMm : null },
                { "span_mm", request.Extent == "finite" ? (object)(request.EndOffsetMm - request.StartOffsetMm) : null },
                { "depth_mm", request.Extent == "finite" && request.StartOffsetMm == 0.0 ? (object)request.EndOffsetMm : null },
                { "target_body_id", request.TargetBodyId }, { "target_body_before", targetBefore },
                { "non_target_bodies_before", nonTargetView },
                { "profile_area_mm2", M3ProfileArea(sketch) }
            };
            return new M3PlanSnapshot {
                Kind = state.Kind, RevisionId = RevisionId, SketchId = sketch.Id,
                FeatureId = featureId, BodyId = bodyId, PublicView = publicView, AdapterState = state
            };
        }

        public M3PlanSnapshot PrepareRevolve(M2ModelBinding binding, M3RevolveRequest request)
        {
            System.Threading.Interlocked.Increment(ref m3RevolvePrepareCalls);
            if (!String.Equals(request.RevisionId, RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "fixture revision changed before revolve planning");
            FakeM3Sketch sketch;
            if (!m3Sketches.TryGetValue(request.SketchId, out sketch))
                throw new M2ModelException("SKETCH_NOT_FOUND", "fixture revolve sketch was not found");

            FakeM3Body target = null;
            if (!String.Equals(request.Mode, "create", StringComparison.Ordinal))
            {
                if (String.IsNullOrEmpty(request.TargetBodyId) || !m3Bodies.TryGetValue(request.TargetBodyId, out target))
                    throw new M2ModelException("TARGET_BODY_NOT_FOUND", "fixture revolve target body was not found");
                if (!target.IsSolid)
                    throw new M2ModelException("TARGET_BODY_NOT_SOLID", "fixture revolve target body is not solid");
            }

            int number = System.Threading.Interlocked.Increment(ref m3FeatureNumber);
            string featureId = "m3-revolve-feature-" + number.ToString(CultureInfo.InvariantCulture);
            string bodyId = target == null
                ? "m3-revolve-body-" + System.Threading.Interlocked.Increment(ref m3BodyNumber).ToString(CultureInfo.InvariantCulture)
                : target.Id;
            Dictionary<string, object> targetBefore = target == null ? null : M3BodyView(target);
            List<Dictionary<string, object>> nonTargetBefore = new List<Dictionary<string, object>>();
            foreach (FakeM3Body body in SortedM3Bodies())
                if (target == null || !String.Equals(body.Id, target.Id, StringComparison.Ordinal))
                    nonTargetBefore.Add(M3BodyView(body));

            Dictionary<string, object> definition = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "mode", request.Mode }, { "profile_output", request.ProfileOutput },
                { "axis_point_mm", new List<double>(request.AxisPointMm) },
                { "axis_direction", new List<double>(request.AxisDirection) },
                { "start_angle_deg", request.StartAngleDeg }, { "end_angle_deg", request.EndAngleDeg },
                { "target_body_id", request.TargetBodyId }, { "fixture_only", true }
            };
            FakeM3PlanState state = new FakeM3PlanState {
                Kind = "create_revolve", FeatureId = featureId, BodyId = bodyId,
                Sketch = sketch, Revolve = request, TargetBodyId = target == null ? null : target.Id,
                TargetBefore = targetBefore, NonTargetBefore = nonTargetBefore, NativeDefinition = definition
            };
            List<object> nonTargetView = new List<object>();
            foreach (Dictionary<string, object> body in nonTargetBefore) nonTargetView.Add(body);
            Dictionary<string, object> publicView = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", state.Kind }, { "sketch_id", sketch.Id }, { "feature_id", featureId },
                { "body_id", bodyId }, { "mode", request.Mode }, { "profile_output", request.ProfileOutput },
                { "axis_point_mm", definition["axis_point_mm"] }, { "axis_direction", definition["axis_direction"] },
                { "start_angle_deg", request.StartAngleDeg }, { "end_angle_deg", request.EndAngleDeg },
                { "target_body_id", request.TargetBodyId }, { "target_body_before", targetBefore },
                { "non_target_bodies_before", nonTargetView }, { "fixture_only", true }
            };
            return new M3PlanSnapshot {
                Kind = state.Kind, RevisionId = RevisionId, SketchId = sketch.Id,
                FeatureId = featureId, BodyId = bodyId, PublicView = publicView, AdapterState = state
            };
        }

        public M3ExecutionResult ExecutePlan(M2ModelBinding binding, M3PlanSnapshot prepared)
        {
            System.Threading.Interlocked.Increment(ref m3ExecutionCalls);
            operationStarted.Set();
            if (System.Threading.Interlocked.CompareExchange(ref blockExecution, 0, 0) != 0 && !releaseOperation.WaitOne(15000))
                throw new M2ModelException("FIXTURE_GATE_TIMEOUT", "fixture M3 operation release gate timed out");
            FakeM3PlanState state = prepared == null ? null : prepared.AdapterState as FakeM3PlanState;
            if (state == null) throw new M2ModelException("INVALID_PLAN", "fixture M3 plan state is unavailable");
            if (state.Executed) throw new M2ModelException("PLAN_ALREADY_USED", "fixture M3 plan was already executed");
            state.Executed = true;
            bool projection = String.Equals(state.Kind, "edit_sketch_projection", StringComparison.Ordinal);
            bool referenceConversion = String.Equals(state.Kind, "edit_sketch_reference_conversion", StringComparison.Ordinal);
            if (projection) System.Threading.Interlocked.Increment(ref m3SketchProjectionExecutionCalls);
            if (referenceConversion)
            {
                System.Threading.Interlocked.Increment(ref m3SketchReferenceConversionExecutionCalls);
                RecordM3SketchReferenceConversionThread();
            }

            bool rollback = System.Threading.Interlocked.Exchange(ref rolledBackAfterWrite, 0) != 0;
            bool unknown = !rollback && System.Threading.Interlocked.Exchange(ref unknownAfterMutation, 0) != 0;
            if (rollback)
            {
                if (projection) ApplyAndRestoreFakeM3SketchProjection(state);
                if (referenceConversion) ApplyAndRestoreFakeM3SketchReferenceConversion(state);
                System.Threading.Interlocked.Increment(ref rollbackCount);
                string rollbackRevision = RevisionId;
                if (String.Equals(state.Kind, "create_revolve", StringComparison.Ordinal))
                {
                    // F13's known rollback invalidates other plans; preserve other fake rollback branches.
                    rollbackRevision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString(CultureInfo.InvariantCulture);
                    binding.RevisionId = rollbackRevision;
                }
                Dictionary<string, object> rejected = M3ExecutionView(state, prepared, "ROLLED_BACK", rollbackRevision);
                rejected["changed"] = false;
                rejected["mutation_state"] = "ROLLED_BACK";
                return new M3ExecutionResult {
                    State = "ROLLED_BACK", RevisionId = rollbackRevision, Result = rejected,
                    ErrorCode = "EDIT_NOT_VERIFIED", ErrorMessage = "fixture restored the prior body and sketch inventory"
                };
            }

            if (String.Equals(state.Kind, "create_sketch", StringComparison.Ordinal))
                ApplyM3Sketch(state.Sketch);
            else if (String.Equals(state.Kind, "edit_sketch_corner", StringComparison.Ordinal))
                ApplyM3Corner(state);
            else if (String.Equals(state.Kind, "edit_sketch_offset", StringComparison.Ordinal))
                ApplyM3SketchOffset(state);
            else if (String.Equals(state.Kind, "edit_sketch_pattern", StringComparison.Ordinal))
                ApplyM3SketchPattern(state);
            else if (projection)
                ApplyM3SketchProjection(state);
            else if (referenceConversion)
                ApplyM3SketchReferenceConversion(state);
            else if (state.Revolve != null)
                ApplyM3Revolve(state);
            else if (state.NativeDefinition != null)
            {
                m3Bodies[state.BodyId].Volume *= 0.98;
                m3Features.Add(new FakeM3Feature { Id = state.FeatureId, Kind = state.Kind == "create_thread" ? "THREADS" : "SKIN", BodyId = state.BodyId });
            }
            else
                ApplyM3Extrude(state);
            state.ExpectedUnknown = unknown;
            string revision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString(CultureInfo.InvariantCulture);
            binding.RevisionId = revision;
            System.Threading.Interlocked.Increment(ref m3MutationCount);
            Dictionary<string, object> result = M3ExecutionView(state, prepared, unknown ? "UNKNOWN" : "APPLIED", revision);
            result["changed"] = unknown ? (object)null : true;
            result["mutation_state"] = unknown ? "UNKNOWN" : "APPLIED";
            if (String.Equals(state.Kind, "edit_sketch_corner", StringComparison.Ordinal))
                AddFakeCornerExecutionEvidence(result, state, unknown ? "UNKNOWN" : "APPLIED");
            else if (String.Equals(state.Kind, "edit_sketch_offset", StringComparison.Ordinal))
                AddFakeSketchOffsetExecutionEvidence(result, state, unknown ? "UNKNOWN" : "APPLIED");
            else if (String.Equals(state.Kind, "edit_sketch_pattern", StringComparison.Ordinal))
                AddFakeSketchPatternExecutionEvidence(result, state, unknown ? "UNKNOWN" : "APPLIED");
            else if (projection)
                AddFakeSketchProjectionExecutionEvidence(result, state, unknown ? "UNKNOWN" : "APPLIED");
            else if (referenceConversion)
                AddFakeSketchReferenceConversionExecutionEvidence(result, state, unknown ? "UNKNOWN" : "APPLIED");
            return new M3ExecutionResult {
                State = unknown ? "UNKNOWN" : "APPLIED", RevisionId = revision, Result = result,
                ErrorCode = unknown ? "UNKNOWN_RESULT" : null,
                ErrorMessage = unknown ? "fixture applied the M3 operation but could not confirm its result" : null
            };
        }

        private void ApplyM3Corner(FakeM3PlanState state)
        {
            M3SketchCornerRequest request = state.CornerRequest;
            if (state.Sketch == null || state.CornerCurve1 == null || state.CornerCurve2 == null || state.CornerCut1 == null || state.CornerCut2 == null)
                throw new M2ModelException("INVALID_PLAN", "fixture corner plan is incomplete");
            if (request.Curve1End == "start") state.CornerCurve1.Start = (double[])state.CornerCut1.Clone();
            else state.CornerCurve1.End = (double[])state.CornerCut1.Clone();
            if (request.Curve2End == "start") state.CornerCurve2.Start = (double[])state.CornerCut2.Clone();
            else state.CornerCurve2.End = (double[])state.CornerCut2.Clone();
            int number = System.Threading.Interlocked.Increment(ref m3FeatureNumber);
            FakeM3Geometry added = new FakeM3Geometry {
                Id = "corner-geometry-" + number.ToString(CultureInfo.InvariantCulture),
                JournalId = "m3-corner-geometry-" + number.ToString(CultureInfo.InvariantCulture),
                Type = request.Kind == "fillet" ? "arc" : "line",
                Start = (double[])state.CornerCut1.Clone(), End = (double[])state.CornerCut2.Clone(),
                Center = state.CornerCenter == null ? null : (double[])state.CornerCenter.Clone(),
                Radius = request.Kind == "fillet" ? request.RadiusMm : 0.0
            };
            state.CornerGeometry = added;
            state.Sketch.Geometries.Add(added);
        }

        private void ApplyM3SketchOffset(FakeM3PlanState state)
        {
            if (state == null || state.Sketch == null || state.OffsetRequest == null || state.OffsetOutput.Count == 0)
                throw new M2ModelException("INVALID_PLAN", "fixture sketch-offset plan is incomplete");
            foreach (FakeM3Geometry geometry in state.OffsetOutput)
                state.Sketch.Geometries.Add(CopyFakeM3Geometry(geometry));
        }

        private void ApplyM3SketchPattern(FakeM3PlanState state)
        {
            if (state == null || state.Sketch == null || state.PatternRequest == null || state.PatternOutput.Count == 0)
                throw new M2ModelException("INVALID_PLAN", "fixture F09 pattern plan is incomplete");
            foreach (FakeM3Geometry geometry in state.PatternOutput)
                state.Sketch.Geometries.Add(CopyFakeM3Geometry(geometry));
            m3Features.Add(new FakeM3Feature {
                Id = state.FeatureId, Kind = "SKETCH_PATTERN", SketchId = state.Sketch.Id
            });
        }

        private void ApplyM3SketchProjection(FakeM3PlanState state)
        {
            if (state == null || state.Sketch == null || state.ProjectionRequest == null ||
                state.ProjectionOutput == null || state.ProjectionOutput.Count == 0 ||
                state.ProjectionSources.Count != state.ProjectionOutput.Count)
                throw new M2ModelException("INVALID_PLAN", "fixture F10 projection plan is incomplete");
            foreach (FakeM3Geometry output in state.ProjectionOutput)
                state.Sketch.Geometries.Add(output);
            for (int index = 0; index < state.ProjectionOutput.Count; index++)
                m3ProjectionRelations.Add(new FakeM3ProjectionRelation {
                    Source = state.ProjectionSources[index], Output = state.ProjectionOutput[index],
                    Associative = state.ProjectionRequest.Associative
                });
            m3Features.Add(new FakeM3Feature {
                Id = state.FeatureId, Kind = "SKETCH_PROJECTION", SketchId = state.Sketch.Id
            });
        }

        private void ApplyAndRestoreFakeM3SketchProjection(FakeM3PlanState state)
        {
            // The fixture simulates a successful transient create followed by a
            // deterministic restoration. This is software-only evidence, not NX Undo.
            ApplyM3SketchProjection(state);
            foreach (FakeM3Geometry output in state.ProjectionOutput)
            {
                state.Sketch.Geometries.Remove(output);
                m3ProjectionRelations.RemoveAll(delegate(FakeM3ProjectionRelation relation) {
                    return Object.ReferenceEquals(relation.Output, output);
                });
            }
            m3Features.RemoveAll(delegate(FakeM3Feature feature) {
                return String.Equals(feature.Id, state.FeatureId, StringComparison.Ordinal);
            });
        }

        private static void ApplyM3SketchReferenceConversion(FakeM3PlanState state)
        {
            if (state == null || state.Sketch == null || state.ReferenceConversionRequest == null ||
                state.ReferenceConversionChangedCount <= 0)
                throw new M2ModelException("INVALID_PLAN", "fixture F11 reference-conversion plan is incomplete");
            int targetReferenceStatus = state.ReferenceConversionTargetState == "reference" ? 0 : 1;
            bool targetDriving = state.ReferenceConversionTargetState == "active";
            foreach (FakeM3Geometry curve in state.ReferenceConversionSelectedCurves)
                curve.ReferenceStatus = targetReferenceStatus;
            foreach (FakeM3Constraint dimension in state.ReferenceConversionSelectedDimensions)
                dimension.IsDriving = targetDriving;
        }

        private static void ApplyAndRestoreFakeM3SketchReferenceConversion(FakeM3PlanState state)
        {
            // This is fake-only state restoration; native rollback evidence comes from the root-run harness.
            ApplyM3SketchReferenceConversion(state);
            for (int index = 0; index < state.ReferenceConversionSelectedCurves.Count; index++)
                state.ReferenceConversionSelectedCurves[index].ReferenceStatus = state.ReferenceConversionCurveStatesBefore[index];
            for (int index = 0; index < state.ReferenceConversionSelectedDimensions.Count; index++)
                state.ReferenceConversionSelectedDimensions[index].IsDriving = state.ReferenceConversionDimensionStatesBefore[index];
        }

        private static Dictionary<string, object> FakeM3ReferenceConversionSolver(FakeM3Sketch sketch)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "status", "FIXTURE_ONLY" },
                { "degrees_of_freedom", sketch == null ? -1 : Math.Max(0, sketch.Geometries.Count * 2 - sketch.Constraints.Count) },
                { "fixture_only", true }, { "native_nx_invoked", false }
            };
        }

        private static List<object> FakeM3ReferenceConversionObjectRows(FakeM3PlanState state)
        {
            List<object> result = new List<object>();
            if (state == null || state.ReferenceConversionRequest == null) return result;
            for (int index = 0; index < state.ReferenceConversionRequest.ObjectIds.Count; index++)
            {
                string objectId = state.ReferenceConversionRequest.ObjectIds[index];
                FakeM3Geometry curve = state.ReferenceConversionSelectedCurves.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.JournalId, objectId, StringComparison.Ordinal);
                });
                if (curve != null)
                {
                    result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "object_id", objectId }, { "kind", "curve" }, { "tag", curve.Id },
                        { "journal_id", curve.JournalId },
                        { "current_state", curve.ReferenceStatus == 0 ? "reference" : "active" },
                        { "requested_state", state.ReferenceConversionTargetState },
                        { "expected_after", state.ReferenceConversionTargetState },
                        { "geometry_after", M3GeometryView(curve) }
                    });
                    continue;
                }
                FakeM3Constraint dimension = state.ReferenceConversionSelectedDimensions.Find(delegate(FakeM3Constraint item) {
                    return String.Equals(item.Id, objectId, StringComparison.Ordinal);
                });
                if (dimension != null)
                    result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "object_id", objectId }, { "kind", "dimension" }, { "tag", dimension.Id },
                        { "journal_id", dimension.Id }, { "constraint_type", dimension.Type },
                        { "current_state", dimension.IsDriving ? "active" : "reference" },
                        { "requested_state", state.ReferenceConversionTargetState },
                        { "expected_after", state.ReferenceConversionTargetState },
                        { "expression_name", dimension.ExpressionName },
                        { "expression_rhs", M3NumberText(dimension.Value) },
                        { "computed_size", dimension.Value },
                        { "geometry_refs", new List<string>(dimension.GeometryRefs) }
                    });
            }
            return result;
        }

        private static List<object> FakeM3ReferenceConversionCandidates(FakeM3Sketch sketch)
        {
            List<object> result = new List<object>();
            if (sketch == null) return result;
            foreach (FakeM3Geometry geometry in sketch.Geometries)
                if (geometry.ReferenceStatus == 1)
                    result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "tag", geometry.Id }, { "journal_id", geometry.JournalId }, { "type", geometry.Type }
                    });
            return result;
        }

        private void AddFakeSketchReferenceConversionExecutionEvidence(Dictionary<string, object> result,
            FakeM3PlanState state, string mutationState)
        {
            List<object> objectsAfter = FakeM3ReferenceConversionObjectRows(state);
            List<object> candidatesAfter = FakeM3ReferenceConversionCandidates(state.Sketch);
            Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "adapter", "M2StaHarness.FakeModelAdapter" },
                { "native_nx_invoked", false },
                { "mutation_state", mutationState },
                { "target_state", state.ReferenceConversionTargetState },
                { "selected_object_count", state.ReferenceConversionRequest.ObjectIds.Count },
                { "changed_object_count", state.ReferenceConversionChangedCount },
                { "objects_before", state.ReferenceConversionObjectsBefore },
                { "objects_after", objectsAfter },
                { "solver_before", FakeM3ReferenceConversionSolver(state.Sketch) },
                { "solver_after", FakeM3ReferenceConversionSolver(state.Sketch) },
                { "contour_candidates_before", state.ReferenceConversionCandidatesBefore },
                { "contour_candidates_after", candidatesAfter },
                { "reference_states_before", state.ReferenceConversionReferenceStatesBefore },
                { "reference_states_after", FakeM3ReferenceStatusViews(state.Sketch.Geometries) },
                { "fixture_only", true }
            };
            state.ReferenceConversionEvidence = evidence;
            result["target_state"] = state.ReferenceConversionTargetState;
            result["objects_after"] = objectsAfter;
            result["solver_after"] = FakeM3ReferenceConversionSolver(state.Sketch);
            result["contour_candidates_after"] = candidatesAfter;
            result["evidence"] = evidence;
        }

        private void AddFakeSketchProjectionExecutionEvidence(Dictionary<string, object> result,
            FakeM3PlanState state, string mutationState)
        {
            List<object> outputRecords = M3GeometryViewList(state.ProjectionOutput);
            List<object> outputIds = new List<object>();
            for (int index = 0; index < state.ProjectionOutput.Count; index++)
                outputIds.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "source_curve_id", state.ProjectionSources[index].Id },
                    { "output_curve_id", state.ProjectionOutput[index].Id },
                    { "output_journal_id", state.ProjectionOutput[index].JournalId },
                    { "type", state.ProjectionOutput[index].Type }
                });
            Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "adapter", "M2StaHarness.FakeModelAdapter" }, { "native_nx_invoked", false },
                { "associative", state.ProjectionRequest.Associative },
                { "direction_xyz", new List<double>(M3ProjectionUnit(state.ProjectionRequest.DirectionXYZ, "direction_xyz")) },
                { "source_curve_ids", new List<string>(state.ProjectionRequest.SourceCurveIds) },
                { "expected_output_curve_count", state.ProjectionOutput.Count },
                { "actual_output_curve_count", state.ProjectionOutput.Count },
                { "output_curve_records", outputRecords }, { "output_curve_identities", outputIds },
                { "mutation_state", mutationState }
            };
            state.ProjectionEvidence = evidence;
            result["native_nx_invoked"] = false;
            result["associative"] = state.ProjectionRequest.Associative;
            result["expected_output_curve_count"] = state.ProjectionOutput.Count;
            result["actual_output_curve_count"] = state.ProjectionOutput.Count;
            result["output_curve_records"] = outputRecords;
            result["output_curve_identities"] = outputIds;
            result["projection_evidence"] = evidence;
        }

        private void AddFakeSketchPatternExecutionEvidence(Dictionary<string, object> result,
            FakeM3PlanState state, string mutationState)
        {
            List<object> outputRecords = M3GeometryViewList(state.PatternOutput);
            List<object> outputIds = new List<object>();
            foreach (FakeM3Geometry output in state.PatternOutput)
                outputIds.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "journal_id", output.JournalId }, { "curve_id", output.Id }, { "type", output.Type }
                });
            Dictionary<string, object> nativeOperation = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "native_operation_called", true }, { "commit_returned_null", false },
                { "expected_output_curve_count", state.PatternOutput.Count },
                { "actual_output_curve_count", state.PatternOutput.Count }, { "output_curve_records", outputRecords },
                { "output_curve_identities", outputIds }
            };
            state.PatternEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "mode", state.PatternRequest.Mode },
                { "expected_pattern_instance_count", state.PatternPositionTransforms.Count },
                { "expected_output_curve_count", state.PatternOutput.Count },
                { "actual_output_curve_count", state.PatternOutput.Count },
                { "expected_instances", state.PatternExpectedInstances },
                { "output_curve_records", outputRecords },
                { "native_operation", nativeOperation },
                { "mutation_state", mutationState }
            };
            result["expected_pattern_instance_count"] = state.PatternPositionTransforms.Count;
            result["expected_output_curve_count"] = state.PatternOutput.Count;
            result["output_curve_records"] = outputRecords;
            result["evidence"] = state.PatternEvidence;
        }

        private void AddFakeSketchOffsetExecutionEvidence(Dictionary<string, object> result,
            FakeM3PlanState state, string mutationState)
        {
            state.OffsetEvidence = BuildFakeSketchOffsetEvidence(state, mutationState);
            result["output_closed"] = state.OffsetEvidence["output_closed"];
            result["output_curve_records"] = state.OffsetEvidence["output_curve_records"];
            result["evidence"] = state.OffsetEvidence;
        }

        private Dictionary<string, object> BuildFakeSketchOffsetEvidence(FakeM3PlanState state,
            string mutationState)
        {
            Dictionary<string, object> offsetGeometry = BuildFakeOffsetGeometryEvidence(state);
            List<object> outputRecords = offsetGeometry["output_curve_records"] as List<object> ?? new List<object>();
            bool connected = M3OffsetOutputConnected(state);
            Dictionary<string, object> sourceEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "selected_source_curve_count", state.OffsetRequest.CurveChain.Count },
                { "preserved_count", state.OffsetRequest.CurveChain.Count },
                { "missing_tags", new List<string>() }, { "changed_tags", new List<string>() }
            };
            Dictionary<string, object> constraintEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "preserved", true }, { "before", state.OffsetConstraintsBefore },
                { "after", M3ConstraintViews(state.Sketch) }
            };
            Dictionary<string, object> frameEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "preserved", true }, { "before", state.OffsetFrameBefore },
                { "after", FakeM3SketchFrameView(state.Sketch) }
            };
            List<object> referenceEvidence = FakeM3ReferenceStatusViews(state.Sketch.Geometries);
            List<object> nonTargetEvidence = M3GeometryViewList(M3CurrentOffsetNonTargets(state));
            List<object> bodyEvidence = M3BodyInventory();
            Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "semantic_matches_execute", true }, { "history_matches_execute", true },
                { "feature_tags_match_execute", true }
            };
            Dictionary<string, object> solverEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "status", "UnderConstrained" }, { "degrees_of_freedom", Math.Max(1, state.Sketch.Geometries.Count * 2) },
                { "acceptable", true }
            };
            Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_identity", true },
                { "source_geometry", sourceEvidence },
                { "source_curves_resolved", true },
                { "output_curves_resolved", true },
                { "offset_geometry", offsetGeometry },
                { "added_geometry", new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "actual_added_count", state.OffsetOutput.Count },
                    { "expected_output_tags", OffsetGeometryTags(state.OffsetOutput) },
                    { "missing_output_tags", new List<string>() }, { "unexpected_added_tags", new List<string>() }
                } },
                { "constraints_preserved", true },
                { "sketch_frames_preserved", true },
                { "reference_status", referenceEvidence },
                { "non_target_geometry", nonTargetEvidence },
                { "bodies_preserved", bodyEvidence },
                { "feature_tags_preserved", true },
                { "history", historyEvidence },
                { "sketch_solver_status", solverEvidence },
                { "no_active_sketch", true }
            };
            Dictionary<string, object> nativeOperation = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "native_operation_called", true }, { "commit_returned_null", false },
                { "update_error_count", 0 }, { "output_curve_tags", OffsetGeometryTags(state.OffsetOutput) },
                { "output_curve_ids", OffsetGeometryJournalIds(state.OffsetOutput) }
            };
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "checks", checks }, { "native_operation", nativeOperation },
                { "offset_geometry", offsetGeometry },
                { "output_closed", state.OffsetInputClosed },
                { "output_curve_records", outputRecords },
                { "source_chain_closed", state.OffsetInputClosed },
                { "verified", mutationState == "APPLIED" && connected }
            };
        }

        private static Dictionary<string, object> BuildFakeOffsetGeometryEvidence(FakeM3PlanState state)
        {
            List<object> outputRecords = FakeM3OffsetCurveRecords(state.OffsetOutput);
            List<object> perSource = new List<object>();
            List<FakeM3Geometry> outputLines = state.OffsetOutput.FindAll(delegate(FakeM3Geometry item) {
                return String.Equals(item.Type, "line", StringComparison.Ordinal);
            });
            bool allMatched = outputLines.Count == state.OffsetRequest.CurveChain.Count;
            for (int i = 0; i < state.OffsetRequest.CurveChain.Count; i++)
            {
                M3SketchOffsetCurveRequest requestCurve = state.OffsetRequest.CurveChain[i];
                FakeM3Geometry source = state.OffsetSourceBefore.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, requestCurve.CurveId, StringComparison.Ordinal) ||
                        String.Equals(item.JournalId, requestCurve.CurveId, StringComparison.Ordinal);
                });
                Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "source_curve_id", requestCurve.CurveId },
                    { "source_curve_tag", source == null ? null : source.Id },
                    { "unique_output_match", source != null && i < outputLines.Count }
                };
                if (source != null && i < outputLines.Count)
                {
                    FakeM3Geometry output = outputLines[i];
                    double measured = FakeM3SignedOffset(source, output, requestCurve.Direction,
                        state.OffsetRequest.Side);
                    record["output_curve_index"] = state.OffsetOutput.IndexOf(output);
                    record["output_curve_tag"] = output.Id;
                    record["output_curve_id"] = output.JournalId;
                    record["measured_signed_offset_mm"] = measured;
                    record["offset_residual_mm"] = measured - state.OffsetRequest.DistanceMm;
                }
                perSource.Add(record);
                if (source == null || i >= outputLines.Count) allMatched = false;
            }

            bool connected = M3OffsetOutputConnected(state);
            bool outputClosed = state.OffsetInputClosed;
            Dictionary<string, object> topology = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "valid_chain", connected }, { "closed", outputClosed },
                { "open_end_count", outputClosed ? 0 : 2 },
                { "endpoint_count", outputClosed ? state.OffsetOutput.Count : state.OffsetOutput.Count + 1 },
                { "component_count", connected ? 1 : state.OffsetOutput.Count },
                { "self_intersection_count", 0 }
            };
            List<object> joinCurves = new List<object>();
            foreach (FakeM3Geometry item in state.OffsetOutput)
                if (String.Equals(item.Type, "arc", StringComparison.Ordinal))
                    joinCurves.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "is_join_curve", true }, { "journal_id", item.JournalId }, { "tag", item.Id },
                        { "object_type", "Arc" }, { "center_mm", FakeM3Point3(item.Center) },
                        { "radius_mm", item.Radius }, { "start_mm", FakeM3Point3(item.Start) },
                        { "end_mm", FakeM3Point3(item.End) }
                    });
            FakeM3Geometry first = state.OffsetOutput.Count == 0 ? null : state.OffsetOutput[0];
            FakeM3Geometry last = state.OffsetOutput.Count == 0 ? null : state.OffsetOutput[state.OffsetOutput.Count - 1];
            Dictionary<string, object> openEndpoints = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "applicable", !outputClosed }, { "valid", connected },
                { "expected_start_mm", first == null ? null : FakeM3Point3(first.Start) },
                { "expected_end_mm", last == null ? null : FakeM3Point3(last.End) },
                { "start_residual_mm", 0.0 }, { "end_residual_mm", 0.0 }
            };
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "native_output_count", state.OffsetOutput.Count }, { "topology", topology },
                { "per_source_curve", perSource }, { "join_curves", joinCurves },
                { "connectors_valid", connected }, { "open_endpoints", openEndpoints },
                { "output_closed", outputClosed }, { "requested_closed", state.OffsetInputClosed },
                { "requested_side", state.OffsetRequest.Side },
                { "distance_mm", state.OffsetRequest.DistanceMm },
                { "all_source_curves_matched", allMatched },
                { "output_curve_records", outputRecords }, { "valid", connected && allMatched }
            };
        }

        private static List<object> FakeM3OffsetCurveRecords(List<FakeM3Geometry> geometries)
        {
            List<object> result = new List<object>();
            foreach (FakeM3Geometry geometry in geometries)
            {
                Dictionary<string, object> record = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "journal_id", geometry.JournalId }, { "tag", geometry.Id },
                    { "object_type", String.Equals(geometry.Type, "arc", StringComparison.Ordinal) ? "Arc" : "Line" },
                    { "start_mm", FakeM3Point3(geometry.Start) }, { "end_mm", FakeM3Point3(geometry.End) }
                };
                if (String.Equals(geometry.Type, "arc", StringComparison.Ordinal))
                {
                    double start = Math.Atan2(geometry.Start[1] - geometry.Center[1], geometry.Start[0] - geometry.Center[0]);
                    double end = Math.Atan2(geometry.End[1] - geometry.Center[1], geometry.End[0] - geometry.Center[0]);
                    double sweep = end - start;
                    while (sweep <= -Math.PI) sweep += 2.0 * Math.PI;
                    while (sweep > Math.PI) sweep -= 2.0 * Math.PI;
                    record["center_mm"] = FakeM3Point3(geometry.Center);
                    record["radius_mm"] = geometry.Radius;
                    record["start_angle_rad"] = start;
                    record["end_angle_rad"] = end;
                    record["sweep_rad"] = sweep;
                    record["plane_x_axis"] = new List<double> { 1.0, 0.0, 0.0 };
                    record["plane_y_axis"] = new List<double> { 0.0, 1.0, 0.0 };
                }
                result.Add(record);
            }
            return result;
        }

        private static List<double> FakeM3Point3(double[] point)
        {
            return point == null ? null : new List<double> { point[0], point[1], 0.0 };
        }

        private static double FakeM3SignedOffset(FakeM3Geometry source, FakeM3Geometry output,
            string direction, string side)
        {
            double[] start = String.Equals(direction, "reverse", StringComparison.Ordinal) ? source.End : source.Start;
            double[] end = String.Equals(direction, "reverse", StringComparison.Ordinal) ? source.Start : source.End;
            double dx = end[0] - start[0], dy = end[1] - start[1];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-15) return Double.NaN;
            double sign = String.Equals(side, "left", StringComparison.Ordinal) ? 1.0 : -1.0;
            double sx = output.Start[0] - start[0], sy = output.Start[1] - start[1];
            return sign * (-dy / length * sx + dx / length * sy);
        }

        private static List<string> OffsetGeometryTags(List<FakeM3Geometry> geometries)
        {
            List<string> result = new List<string>();
            foreach (FakeM3Geometry geometry in geometries) result.Add(geometry.Id);
            return result;
        }

        private static List<string> OffsetGeometryJournalIds(List<FakeM3Geometry> geometries)
        {
            List<string> result = new List<string>();
            foreach (FakeM3Geometry geometry in geometries) result.Add(geometry.JournalId);
            return result;
        }

        private static List<object> FakeM3OffsetRequestedCurveChain(FakeM3PlanState state)
        {
            List<object> result = new List<object>();
            foreach (M3SketchOffsetCurveRequest curve in state.OffsetRequest.CurveChain)
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "curve_id", curve.CurveId }, { "direction", curve.Direction }
                });
            return result;
        }

        private static Dictionary<string, object> BuildFakeSketchOffsetPlanResult(FakeM3PlanState state,
            string revisionId, string mutationState, bool verified, Dictionary<string, object> measured,
            Dictionary<string, object> liveGeometry)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", "edit_sketch_offset" }, { "revision_id", revisionId },
                { "sketch_id", state.Sketch.Id }, { "distance_mm", state.OffsetRequest.DistanceMm },
                { "side", state.OffsetRequest.Side }, { "retain_original", state.OffsetRequest.RetainOriginal },
                { "join_mode", state.OffsetRequest.JoinMode },
                { "curve_chain", FakeM3OffsetRequestedCurveChain(state) },
                { "source_chain_closed", state.OffsetInputClosed },
                { "mutation_state", mutationState }, { "verified", verified },
                { "output_curve_tags", OffsetGeometryTags(state.OffsetOutput) },
                { "output_curve_ids", OffsetGeometryJournalIds(state.OffsetOutput) },
                { "output_closed", liveGeometry["output_closed"] },
                { "output_curve_records", liveGeometry["output_curve_records"] },
                { "evidence", measured }
            };
        }

        private void AddFakeCornerExecutionEvidence(Dictionary<string, object> result,
            FakeM3PlanState state, string mutationState)
        {
            M3SketchCornerRequest request = state.CornerRequest;
            string definition = request.Kind;
            string nativeMode = "Sketch.Fillet";
            double distance1 = request.Kind == "fillet" ? request.RadiusMm / Math.Tan(state.CornerAngleRad / 2.0) : request.Distance1Mm;
            double distance2 = distance1;
            double angleDeg = 0.0;
            if (request.Kind == "chamfer")
            {
                distance2 = request.Distance2Mm.HasValue ? request.Distance2Mm.Value : state.CornerRequest.Distance1Mm * 0.0;
                if (!request.Distance2Mm.HasValue)
                {
                    angleDeg = request.AngleDeg.Value;
                    distance2 = request.Distance1Mm * Math.Sin(request.AngleDeg.Value * Math.PI / 180.0) /
                        Math.Sin(state.CornerAngleRad + request.AngleDeg.Value * Math.PI / 180.0);
                    definition = "distance_angle";
                }
                else if (Math.Abs(request.Distance1Mm - request.Distance2Mm.Value) <= 1e-9)
                    definition = "equal_distance";
                else definition = "two_distances";
                nativeMode = definition == "equal_distance" ? "Symmetric" : "Asymmetric";
            }
            double[] corner = state.CornerBefore;
            double[] far1 = FakeM3Endpoint(state.CornerCurve1, request.Curve1End == "start" ? "end" : "start");
            double[] far2 = FakeM3Endpoint(state.CornerCurve2, request.Curve2End == "start" ? "end" : "start");
            double thetaActual = state.CornerAngleRad;
            double[] firstA = new double[] { state.CornerBefore[0] - state.CornerCut1[0], state.CornerBefore[1] - state.CornerCut1[1] };
            double[] chordAB = new double[] { state.CornerCut2[0] - state.CornerCut1[0], state.CornerCut2[1] - state.CornerCut1[1] };
            double angleActual = Math.Atan2(Math.Abs(firstA[0] * chordAB[1] - firstA[1] * chordAB[0]),
                firstA[0] * chordAB[0] + firstA[1] * chordAB[1]);
            if (definition == "distance_angle") angleDeg = angleActual * 180.0 / Math.PI;
            Dictionary<string, object> nativeOperation = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "api", request.Kind == "fillet" ? "NXOpen.Sketch.Fillet" : "NXOpen.SketchChamferBuilder" },
                { "native_operation_called", true }, { "native_mode", nativeMode },
                { "distance1_rhs_before_commit", request.Kind == "chamfer" ? (object)distance1.ToString("R", CultureInfo.InvariantCulture) : null },
                { "distance1_rhs_after_commit", request.Kind == "chamfer" ? (object)distance1.ToString("R", CultureInfo.InvariantCulture) : null },
                { "distance2_rhs_before_commit", request.Kind == "chamfer" ? (object)distance2.ToString("R", CultureInfo.InvariantCulture) : null },
                { "distance2_rhs_after_commit", request.Kind == "chamfer" ? (object)distance2.ToString("R", CultureInfo.InvariantCulture) : null },
                { "angle_rhs_before_commit", null }, { "angle_rhs_after_commit", null }, { "post_commit_readback_error", 0 }
            };
            Dictionary<string, object> measured = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "corner_mm", new List<double>(corner) },
                { "curve1_cut_point_mm", new List<double>(state.CornerCut1) },
                { "curve2_cut_point_mm", new List<double>(state.CornerCut2) },
                { "distance1_mm", request.Kind == "fillet" ? (object)distance1 : FakeM3Distance(corner, state.CornerCut1) },
                { "distance2_mm", request.Kind == "fillet" ? (object)distance2 : FakeM3Distance(corner, state.CornerCut2) },
                { "angle_deg", request.Kind == "chamfer" && definition == "distance_angle" ? (object)angleDeg : null },
                { "requested_angle_deg", request.AngleDeg },
                { "calculated_distance2_mm", definition == "distance_angle" ? (object)distance2 : null },
                { "corner_angle_deg", thetaActual * 180.0 / Math.PI },
                { "chamfer_segment_length_mm", request.Kind == "chamfer" ? (object)FakeM3Distance(state.CornerCut1, state.CornerCut2) : null }
            };
            result["requested_definition"] = definition;
            result["native_mode"] = nativeMode;
            result["measured_geometry"] = measured;
            nativeOperation["requested_definition"] = definition;
            state.CornerEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "requested_definition", definition }, { "native_mode", nativeMode },
                { "measured_geometry", measured }, { "native_operation", nativeOperation },
                { "targets_before", new List<object> { M3GeometryView(state.CornerCurve1Before), M3GeometryView(state.CornerCurve2Before) } },
                { "targets_after", new List<object> { M3GeometryView(state.CornerCurve1), M3GeometryView(state.CornerCurve2) } },
                { "added_geometry", M3GeometryView(state.CornerGeometry) },
                { "sketch_id", state.Sketch.Id }, { "mutation_state", mutationState }
            };
            result["evidence"] = state.CornerEvidence;
        }

        private void ApplyM3Sketch(FakeM3Sketch sketch)
        {
            m3Sketches.Add(sketch.Id, sketch);
            foreach (FakeM3Constraint constraint in sketch.Constraints) m3Constraints.Add(constraint.Id, constraint);
            m3Features.Add(new FakeM3Feature { Id = sketch.FeatureId, Kind = "SKETCH", SketchId = sketch.Id });
        }

        private void ApplyM3Extrude(FakeM3PlanState state)
        {
            double depth = state.Extrude.Extent == "through_all" ? 100.0 : state.Extrude.DepthMm;
            double volume = M3ProfileArea(state.Sketch) * depth;
            double[] bbox = M3ExtrudedBounds(state.Sketch, depth, state.Extrude.Direction);
            if (String.Equals(state.Extrude.Mode, "create", StringComparison.Ordinal))
            {
                m3Bodies.Add(state.BodyId, new FakeM3Body {
                    Id = state.BodyId, IsSolid = true, Volume = volume, Bbox = bbox
                });
            }
            else
            {
                FakeM3Body target;
                if (!m3Bodies.TryGetValue(state.TargetBodyId, out target))
                    throw new M2ModelException("TARGET_BODY_NOT_FOUND", "fixture boolean target disappeared before execution");
                if (String.Equals(state.Extrude.Mode, "unite", StringComparison.Ordinal))
                {
                    target.Volume += volume;
                    target.Bbox = UnionBounds(target.Bbox, bbox);
                }
                else
                    target.Volume = Math.Max(1.0, target.Volume - volume);
            }
            m3Features.Add(new FakeM3Feature {
                Id = state.FeatureId, Kind = "EXTRUDE", SketchId = state.Sketch.Id, BodyId = state.BodyId
            });
        }

        private void ApplyM3Revolve(FakeM3PlanState state)
        {
            if (state == null || state.Revolve == null || state.Sketch == null)
                throw new M2ModelException("INVALID_PLAN", "fixture revolve plan is incomplete");
            // This STA adapter is a lifecycle double only: its body measure does
            // not model NX geometry or serve as F13 geometry-validation evidence.
            double fraction = Math.Abs(state.Revolve.EndAngleDeg - state.Revolve.StartAngleDeg) / 360.0;
            double volume = Math.Max(1.0, M3ProfileArea(state.Sketch)) * fraction;
            bool solid = String.Equals(state.Revolve.ProfileOutput, "solid", StringComparison.Ordinal);
            if (String.Equals(state.Revolve.Mode, "create", StringComparison.Ordinal))
            {
                m3Bodies.Add(state.BodyId, new FakeM3Body {
                    Id = state.BodyId, IsSolid = solid, Volume = volume,
                    Bbox = new double[] { -1.0, 1.0, -1.0, 1.0, -1.0, 1.0 }
                });
            }
            else
            {
                FakeM3Body target;
                if (!m3Bodies.TryGetValue(state.TargetBodyId, out target) || !target.IsSolid)
                    throw new M2ModelException("TARGET_BODY_NOT_SOLID", "fixture revolve target disappeared or is not solid");
                if (String.Equals(state.Revolve.Mode, "unite", StringComparison.Ordinal))
                {
                    target.Volume += volume;
                    target.Bbox = UnionBounds(target.Bbox, new double[] { -1.0, 1.0, -1.0, 1.0, -1.0, 1.0 });
                }
                else
                    target.Volume = Math.Max(1.0, target.Volume - volume);
            }
            m3Features.Add(new FakeM3Feature {
                Id = state.FeatureId, Kind = "REVOLVE", SketchId = state.Sketch.Id, BodyId = state.BodyId
            });
        }

        private Dictionary<string, object> M3ExecutionView(FakeM3PlanState state, M3PlanSnapshot prepared,
            string mutationState, string revision)
        {
            bool geometryOnly = state.Sketch != null && state.Sketch.ConstraintMode == "geometry_only";
            int degreesOfFreedom = geometryOnly ? Math.Max(1, state.Sketch.Geometries.Count * 2) : 0;
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "kind", state.Kind }, { "revision_id", revision }, { "feature_id", state.FeatureId },
                { "sketch_id", state.Sketch == null ? null : state.Sketch.Id },
                { "body_id", state.BodyId }, { "constraint_ids", M3ConstraintIds(state.Sketch) },
                { "driving_dimensions", M3DrivingDimensionViews(state.Sketch) },
                { "body_inventory", M3BodyInventory() },
                { "sketch_status", geometryOnly ? "UnderConstrained" : "FullyConstrained" },
                { "constraint_mode", state.Sketch == null ? null : state.Sketch.ConstraintMode },
                { "dof", degreesOfFreedom }, { "degrees_of_freedom", degreesOfFreedom },
                { "mutation_state", mutationState }
            };
            return result;
        }

        private static List<string> M3ConstraintIds(FakeM3Sketch sketch)
        {
            List<string> result = new List<string>();
            if (sketch != null)
                foreach (FakeM3Constraint constraint in sketch.Constraints) result.Add(constraint.Id);
            return result;
        }

        private static List<object> M3DrivingDimensionViews(FakeM3Sketch sketch)
        {
            List<object> result = new List<object>();
            if (sketch == null) return result;
            foreach (FakeM3Constraint constraint in sketch.Constraints)
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "constraint_id", constraint.Id }, { "geometry_id", constraint.GeometryId },
                    { "role", constraint.Role }, { "constraint_type", constraint.Type },
                    { "value", constraint.Value }, { "unit", "mm" },
                    { "expression_name", constraint.ExpressionName },
                    { "geometry_refs", new List<string>(constraint.GeometryRefs) },
                    { "is_driving", constraint.IsDriving }
                });
            return result;
        }

        private static bool M3ReferenceConversionGeometryMatches(FakeM3PlanState state, FakeM3Sketch actual)
        {
            return state != null && actual != null &&
                String.Equals(M2Json.CanonicalText(state.ReferenceConversionGeometryBefore),
                    M2Json.CanonicalText(M3ReferenceConversionGeometryViews(actual)), StringComparison.Ordinal);
        }

        private static List<object> M3ReferenceConversionGeometryViews(FakeM3Sketch sketch)
        {
            List<object> result = new List<object>();
            if (sketch == null) return result;
            foreach (FakeM3Geometry geometry in sketch.Geometries)
            {
                Dictionary<string, object> view = new Dictionary<string, object>(M3GeometryView(geometry), StringComparer.Ordinal);
                view.Remove("reference_status");
                view.Remove("reference_status_value");
                view.Remove("reference_curve_solver_semantics");
                result.Add(view);
            }
            return result;
        }

        private static bool M3ReferenceConversionFrameMatches(FakeM3PlanState state, FakeM3Sketch actual)
        {
            return state != null && actual != null &&
                String.Equals(M2Json.CanonicalText(state.ReferenceConversionFrameBefore),
                    M2Json.CanonicalText(FakeM3SketchFrameView(actual)), StringComparison.Ordinal);
        }

        private static bool M3ReferenceConversionTargetsMatch(FakeM3PlanState state)
        {
            if (state == null || state.ReferenceConversionRequest == null) return false;
            bool targetReference = state.ReferenceConversionTargetState == "reference";
            foreach (FakeM3Geometry curve in state.ReferenceConversionSelectedCurves)
                if (curve.ReferenceStatus != (targetReference ? 0 : 1)) return false;
            foreach (FakeM3Constraint dimension in state.ReferenceConversionSelectedDimensions)
                if (dimension.IsDriving != !targetReference) return false;
            return state.ReferenceConversionSelectedCurves.Count + state.ReferenceConversionSelectedDimensions.Count ==
                state.ReferenceConversionRequest.ObjectIds.Count;
        }

        private static bool M3ReferenceConversionReferenceStatesMatch(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (state == null || actual == null || state.ReferenceConversionReferenceStatesBefore == null) return false;
            List<object> expected = new List<object>();
            int targetStatus = state.ReferenceConversionTargetState == "reference" ? 0 : 1;
            foreach (object raw in state.ReferenceConversionReferenceStatesBefore)
            {
                Dictionary<string, object> before = raw as Dictionary<string, object>;
                if (before == null) return false;
                Dictionary<string, object> copy = new Dictionary<string, object>(before, StringComparer.Ordinal);
                string journalId = Convert.ToString(before["journal_id"], CultureInfo.InvariantCulture);
                FakeM3Geometry selected = state.ReferenceConversionSelectedCurves.Find(delegate(FakeM3Geometry curve) {
                    return String.Equals(curve.JournalId, journalId, StringComparison.Ordinal);
                });
                if (selected != null) copy["reference_status"] = targetStatus;
                expected.Add(copy);
            }
            return String.Equals(M2Json.CanonicalText(expected),
                M2Json.CanonicalText(FakeM3ReferenceStatusViews(actual.Geometries)), StringComparison.Ordinal);
        }

        private static bool M3ReferenceConversionConstraintsMatch(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (state == null || actual == null ||
                state.ReferenceConversionConstraintsBefore.Count != actual.Constraints.Count) return false;
            List<object> expected = new List<object>();
            bool targetDriving = state.ReferenceConversionTargetState == "active";
            foreach (object raw in state.ReferenceConversionConstraintsBefore)
            {
                Dictionary<string, object> before = raw as Dictionary<string, object>;
                if (before == null) return false;
                Dictionary<string, object> copy = new Dictionary<string, object>(before, StringComparer.Ordinal);
                string id = Convert.ToString(before["journal_id"], CultureInfo.InvariantCulture);
                FakeM3Constraint selected = state.ReferenceConversionSelectedDimensions.Find(delegate(FakeM3Constraint dimension) {
                    return String.Equals(dimension.Id, id, StringComparison.Ordinal);
                });
                if (selected != null)
                {
                    copy["is_driving"] = targetDriving;
                    copy["dimension_state"] = targetDriving ? "Driving" : "Reference";
                }
                expected.Add(copy);
            }
            return String.Equals(M2Json.CanonicalText(expected), M2Json.CanonicalText(M3ConstraintViews(actual)),
                StringComparison.Ordinal);
        }

        private bool M3ReferenceConversionHistoryMatches(FakeM3PlanState state)
        {
            if (state == null || state.ReferenceConversionFeatureIdsBefore.Count != m3Features.Count) return false;
            for (int index = 0; index < m3Features.Count; index++)
                if (!String.Equals(state.ReferenceConversionFeatureIdsBefore[index], m3Features[index].Id,
                    StringComparison.Ordinal)) return false;
            return true;
        }

        private List<string> M3FeatureIds()
        {
            List<string> result = new List<string>();
            foreach (FakeM3Feature feature in m3Features) result.Add(feature.Id);
            return result;
        }

        private static bool M3ReferenceConversionCandidatesMatch(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (state == null || actual == null || state.ReferenceConversionEvidence == null) return false;
            List<object> expected = FakeM3ReferenceConversionCandidates(actual);
            List<object> observed = state.ReferenceConversionEvidence["contour_candidates_after"] as List<object>;
            return observed != null && String.Equals(M2Json.CanonicalText(expected),
                M2Json.CanonicalText(observed), StringComparison.Ordinal);
        }

        public M3VerificationResult VerifyPlan(M2ModelBinding binding, M3PlanSnapshot prepared)
        {
            System.Threading.Interlocked.Increment(ref m3VerificationCalls);
            FakeM3PlanState state = prepared == null ? null : prepared.AdapterState as FakeM3PlanState;
            if (state == null || !state.Executed || state.ExpectedUnknown)
                throw new M2ModelException("VERIFY_REQUIRES_EXECUTED_PLAN", "fixture M3 plan has no known completed execution");
            bool projection = String.Equals(state.Kind, "edit_sketch_projection", StringComparison.Ordinal);
            bool referenceConversion = String.Equals(state.Kind, "edit_sketch_reference_conversion", StringComparison.Ordinal);
            if (projection) System.Threading.Interlocked.Increment(ref m3SketchProjectionVerificationCalls);
            if (referenceConversion)
            {
                System.Threading.Interlocked.Increment(ref m3SketchReferenceConversionVerificationCalls);
                RecordM3SketchReferenceConversionThread();
            }
            if (System.Threading.Interlocked.Exchange(ref unknownNextM3Verification, 0) != 0)
                throw new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN", "fixture simulates an unverified NX Section cleanup state");
            if (System.Threading.Interlocked.Exchange(ref nullNextM3Verification, 0) != 0) return null;
            bool geometryMatch = true;
            bool constraintMatch = true;
            bool historyMatch = false;
            bool bodyMatch = true;
            if (String.Equals(state.Kind, "create_sketch", StringComparison.Ordinal))
            {
                FakeM3Sketch actual;
                geometryMatch = m3Sketches.TryGetValue(state.Sketch.Id, out actual) &&
                    M3GeometryViews(actual).Count == M3GeometryViews(state.Sketch).Count;
                if (String.Equals(state.Sketch.ConstraintMode, "geometry_only", StringComparison.Ordinal))
                    constraintMatch = geometryMatch && actual != null && actual.Constraints.Count == 0;
                else
                    constraintMatch = geometryMatch && state.Sketch.Constraints.Count > 0 &&
                        state.Sketch.Constraints.TrueForAll(delegate(FakeM3Constraint c) { return c.IsDriving; });
                historyMatch = m3Features.Exists(delegate(FakeM3Feature f) { return f.Id == state.FeatureId; });
                bodyMatch = M3NonTargetsUnchanged(state);
            }
            else if (String.Equals(state.Kind, "edit_sketch_corner", StringComparison.Ordinal))
            {
                FakeM3Sketch actual;
                bool sketchExists = m3Sketches.TryGetValue(state.Sketch.Id, out actual);
                geometryMatch = sketchExists && M3CornerGeometryMatches(state, actual);
                constraintMatch = sketchExists && M3CornerConstraintsPreserved(state, actual);
                historyMatch = sketchExists && m3Features.Exists(delegate(FakeM3Feature f) { return f.Id == state.Sketch.FeatureId; });
                bodyMatch = M3NonTargetsUnchanged(state);
            }
            else if (String.Equals(state.Kind, "edit_sketch_offset", StringComparison.Ordinal))
            {
                FakeM3Sketch actual;
                bool sketchExists = m3Sketches.TryGetValue(state.Sketch.Id, out actual);
                geometryMatch = sketchExists && M3OffsetGeometryMatches(state, actual);
                constraintMatch = sketchExists && String.Equals(M2Json.CanonicalText(state.OffsetConstraintsBefore),
                    M2Json.CanonicalText(M3ConstraintViews(actual)), StringComparison.Ordinal);
                historyMatch = sketchExists && m3Features.Exists(delegate(FakeM3Feature f) { return f.Id == state.Sketch.FeatureId; });
                bodyMatch = M3NonTargetsUnchanged(state);
            }
            else if (String.Equals(state.Kind, "edit_sketch_pattern", StringComparison.Ordinal))
            {
                FakeM3Sketch actual;
                bool sketchExists = m3Sketches.TryGetValue(state.Sketch.Id, out actual);
                geometryMatch = sketchExists && M3PatternGeometryMatches(state, actual);
                constraintMatch = sketchExists && String.Equals(M2Json.CanonicalText(state.PatternConstraintsBefore),
                    M2Json.CanonicalText(M3ConstraintViews(actual)), StringComparison.Ordinal);
                historyMatch = sketchExists && m3Features.Exists(delegate(FakeM3Feature f) {
                    return f.Id == state.FeatureId && f.Kind == "SKETCH_PATTERN" && f.SketchId == state.Sketch.Id;
                });
                bodyMatch = M3NonTargetsUnchanged(state);
            }
            else if (referenceConversion)
            {
                FakeM3Sketch actual;
                bool sketchExists = m3Sketches.TryGetValue(state.Sketch.Id, out actual);
                geometryMatch = sketchExists && M3ReferenceConversionGeometryMatches(state, actual);
                constraintMatch = sketchExists && M3ReferenceConversionConstraintsMatch(state, actual);
                historyMatch = sketchExists && M3ReferenceConversionHistoryMatches(state);
                bodyMatch = M3NonTargetsUnchanged(state);
            }
            else if (projection)
            {
                // Projection evidence is evaluated in its dedicated check block below.
            }
            else
            {
                FakeM3Body actual;
                bodyMatch = m3Bodies.TryGetValue(state.BodyId, out actual) && actual.IsSolid && actual.Volume > 0.0;
                geometryMatch = bodyMatch;
                constraintMatch = state.NativeDefinition != null ? state.NativeDefinition.Count > 0 : state.Sketch.Constraints.Count > 0;
                historyMatch = m3Features.Exists(delegate(FakeM3Feature f) { return f.Id == state.FeatureId; });
            }
            bool mismatchNext = System.Threading.Interlocked.Exchange(ref mismatchNextM3Verification, 0) != 0;
            if (mismatchNext) geometryMatch = false;
            if (referenceConversion)
            {
                FakeM3Sketch actual;
                bool sketchExists = m3Sketches.TryGetValue(state.Sketch.Id, out actual);
                bool targetsMatch = sketchExists && M3ReferenceConversionTargetsMatch(state) &&
                    M3ReferenceConversionReferenceStatesMatch(state, actual);
                bool conversionGeometryMatch = sketchExists && geometryMatch;
                bool constraintsMatch = sketchExists && constraintMatch;
                bool frameMatch = sketchExists && M3ReferenceConversionFrameMatches(state, actual);
                bool associationsMatch = constraintsMatch;
                bool nonTargetGeometryMatch = sketchExists && conversionGeometryMatch;
                bool conversionHistoryMatch = sketchExists && historyMatch;
                bool bodiesMatch = M3NonTargetsUnchanged(state);
                Dictionary<string, object> solver = FakeM3ReferenceConversionSolver(actual);
                int solverDof = solver.ContainsKey("degrees_of_freedom") ? Convert.ToInt32(
                    solver["degrees_of_freedom"], CultureInfo.InvariantCulture) : -1;
                bool solverMatch = solverDof >= 0 && Object.Equals(solver["fixture_only"], true);
                bool profileCandidatesMatch = sketchExists && M3ReferenceConversionCandidatesMatch(state, actual);
                bool dirtyStateMatch = true; // The software-only fixture has no NX dirty-state object.
                Dictionary<string, object> referenceChecks = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "reference_conversion_targets", VerificationCheck(targetsMatch, new Dictionary<string, object> {
                        { "target_state", state.ReferenceConversionTargetState },
                        { "objects_before", state.ReferenceConversionObjectsBefore },
                        { "objects_after", FakeM3ReferenceConversionObjectRows(state) },
                        { "changed_object_count", state.ReferenceConversionChangedCount }
                    }) },
                    { "sketch_geometry", VerificationCheck(conversionGeometryMatch, new Dictionary<string, object> {
                        { "before", state.ReferenceConversionGeometryBefore },
                        { "after", actual == null ? null : M3ReferenceConversionGeometryViews(actual) }
                    }) },
                    { "sketch_constraints", VerificationCheck(constraintsMatch, new Dictionary<string, object> {
                        { "before", state.ReferenceConversionConstraintsBefore },
                        { "after", actual == null ? null : M3ConstraintViews(actual) }
                    }) },
                    { "sketch_frames", VerificationCheck(frameMatch, new Dictionary<string, object> {
                        { "before", state.ReferenceConversionFrameBefore },
                        { "after", actual == null ? null : FakeM3SketchFrameView(actual) }
                    }) },
                    { "source_associations", VerificationCheck(associationsMatch, new Dictionary<string, object> {
                        { "all_constraint_operands_preserved", associationsMatch },
                        { "fixture_only", true }
                    }) },
                    { "non_target_geometry", VerificationCheck(nonTargetGeometryMatch, new Dictionary<string, object> {
                        { "before", state.ReferenceConversionGeometryBefore },
                        { "after", actual == null ? null : M3ReferenceConversionGeometryViews(actual) },
                        { "fixture_only", true }
                    }) },
                    { "history", VerificationCheck(conversionHistoryMatch, new Dictionary<string, object> {
                        { "feature_ids_before", state.ReferenceConversionFeatureIdsBefore },
                        { "feature_ids_after", M3FeatureIds() }
                    }) },
                    { "bodies", VerificationCheck(bodiesMatch, new Dictionary<string, object> {
                        { "body_inventory", M3BodyInventory() }, { "body_count", m3Bodies.Count },
                        { "fixture_only", true }
                    }) },
                    { "sketch_solver_status", VerificationCheck(solverMatch, solver) },
                    { "profile_candidates", VerificationCheck(profileCandidatesMatch, new Dictionary<string, object> {
                        { "before", state.ReferenceConversionCandidatesBefore },
                        { "after", FakeM3ReferenceConversionCandidates(actual) },
                        { "is_native_section", false }
                    }) },
                    { "dirty_state", VerificationCheck(dirtyStateMatch, new Dictionary<string, object> {
                        { "fixture_only", true }, { "native_dirty_state_available", false }
                    }) }
                };
                bool referenceVerified = targetsMatch && conversionGeometryMatch && constraintsMatch && frameMatch &&
                    associationsMatch && nonTargetGeometryMatch && conversionHistoryMatch && bodiesMatch &&
                    solverMatch && profileCandidatesMatch && dirtyStateMatch;
                Dictionary<string, object> referenceResult = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "verified", referenceVerified }, { "revision_id", binding.RevisionId }, { "checks", referenceChecks },
                    { "objects_after", FakeM3ReferenceConversionObjectRows(state) },
                    { "solver_after", solver },
                    { "contour_candidates_after", FakeM3ReferenceConversionCandidates(actual) },
                    { "fixture_only", true }, { "native_nx_invoked", false }
                };
                if (state.ReferenceConversionEvidence != null)
                    foreach (KeyValuePair<string, object> item in state.ReferenceConversionEvidence)
                        referenceResult[item.Key] = item.Value;
                return new M3VerificationResult {
                    Verified = referenceVerified, RevisionId = binding.RevisionId, Checks = referenceChecks,
                    Result = referenceResult, ErrorCode = referenceVerified ? null : "PART_VERIFICATION_FAILED",
                    ErrorMessage = referenceVerified ? null : "fixture F11 state, constraints, geometry, history, solver, or profile-candidate observation mismatched"
                };
            }
            if (String.Equals(state.Kind, "edit_sketch_offset", StringComparison.Ordinal))
            {
                FakeM3Sketch actual;
                bool sketchExists = m3Sketches.TryGetValue(state.Sketch.Id, out actual);
                bool offsetGeometryMatch = geometryMatch && M3OffsetOutputConnected(state);
                bool sourceCurvesMatch = sketchExists && M3OffsetSourceCurvesMatch(state, actual);
                bool constraintsMatch = sketchExists && String.Equals(M2Json.CanonicalText(state.OffsetConstraintsBefore),
                    M2Json.CanonicalText(M3ConstraintViews(actual)), StringComparison.Ordinal);
                bool frameMatch = sketchExists && String.Equals(M2Json.CanonicalText(state.OffsetFrameBefore),
                    M2Json.CanonicalText(FakeM3SketchFrameView(actual)), StringComparison.Ordinal);
                bool referenceStatusMatch = sketchExists && M3OffsetReferenceStatusesMatch(state, actual);
                bool nonTargetGeometryMatch = sketchExists && M3OffsetNonTargetsMatch(state, actual);
                bool offsetHistoryMatch = sketchExists && m3Features.Exists(delegate(FakeM3Feature f) {
                    return f.Id == state.Sketch.FeatureId && f.Kind == "SKETCH" && f.SketchId == state.Sketch.Id;
                });
                bool offsetBodiesMatch = M3NonTargetsUnchanged(state);
                string solverStatus = state.Sketch.ConstraintMode == "geometry_only" ? "UnderConstrained" : "FullyConstrained";
                int solverDof = state.Sketch.ConstraintMode == "geometry_only" ? Math.Max(1, state.Sketch.Geometries.Count * 2) : 0;
                bool solverMatch = solverDof >= 0 && !String.IsNullOrWhiteSpace(solverStatus);
                Dictionary<string, object> offsetChecks = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "offset_geometry", VerificationCheck(offsetGeometryMatch, state.OffsetEvidence == null
                        ? new Dictionary<string, object> { { "output_chain", M3GeometryViewList(state.OffsetOutput) } }
                        : state.OffsetEvidence) },
                    { "source_curves", VerificationCheck(sourceCurvesMatch, new Dictionary<string, object> {
                        { "before", M3GeometryViewList(state.OffsetSourceBefore) },
                        { "after", actual == null ? null : M3GeometryViewList(OriginalM3Geometries(state)) }
                    }) },
                    { "sketch_constraints", VerificationCheck(constraintsMatch, new Dictionary<string, object> {
                        { "before", state.OffsetConstraintsBefore },
                        { "after", actual == null ? null : M3ConstraintViews(actual) }
                    }) },
                    { "sketch_frames", VerificationCheck(frameMatch, new Dictionary<string, object> {
                        { "before", state.OffsetFrameBefore },
                        { "after", actual == null ? null : FakeM3SketchFrameView(actual) }
                    }) },
                    { "reference_status", VerificationCheck(referenceStatusMatch, new Dictionary<string, object> {
                        { "before", state.OffsetReferenceStatusBefore },
                        { "after", actual == null ? null : M3OffsetReferenceStatusAfter(state, actual) }
                    }) },
                    { "non_target_geometry", VerificationCheck(nonTargetGeometryMatch, new Dictionary<string, object> {
                        { "before", M3GeometryViewList(state.OffsetNonTargetsBefore) },
                        { "after", actual == null ? null : M3GeometryViewList(M3CurrentOffsetNonTargets(state)) }
                    }) },
                    { "history", VerificationCheck(offsetHistoryMatch, new Dictionary<string, object> {
                        { "feature_id", state.Sketch.FeatureId }, { "feature_count", m3Features.Count },
                        { "native_feature_tags", new List<string> { state.Sketch.FeatureId } }
                    }) },
                    { "bodies", VerificationCheck(offsetBodiesMatch, new Dictionary<string, object> {
                        { "body_inventory", M3BodyInventory() }, { "body_count", m3Bodies.Count }
                    }) },
                    { "sketch_solver_status", VerificationCheck(solverMatch, new Dictionary<string, object> {
                        { "status", solverStatus }, { "degrees_of_freedom", solverDof }, { "acceptable", true }
                    }) }
                };
                bool offsetVerified = offsetGeometryMatch && sourceCurvesMatch && constraintsMatch && frameMatch &&
                    referenceStatusMatch && nonTargetGeometryMatch && offsetHistoryMatch && offsetBodiesMatch && solverMatch;
                Dictionary<string, object> offsetResult = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "verified", offsetVerified }, { "revision_id", binding.RevisionId }, { "checks", offsetChecks }
                };
                if (state.OffsetEvidence != null)
                    foreach (KeyValuePair<string, object> item in state.OffsetEvidence) offsetResult[item.Key] = item.Value;
                return new M3VerificationResult {
                    Verified = offsetVerified, RevisionId = binding.RevisionId,
                    Checks = offsetChecks, Result = offsetResult,
                    ErrorCode = offsetVerified ? null : "PART_VERIFICATION_FAILED",
                    ErrorMessage = offsetVerified ? null : "fixture offset geometry, source, frame, reference, constraints, history, solver, or body evidence mismatched"
                };
            }

            if (String.Equals(state.Kind, "edit_sketch_pattern", StringComparison.Ordinal))
            {
                FakeM3Sketch actual;
                bool sketchExists = m3Sketches.TryGetValue(state.Sketch.Id, out actual);
                bool patternInstancesMatch = sketchExists && geometryMatch;
                bool sourceCurvesMatch = sketchExists && M3PatternSourceCurvesMatch(state, actual);
                bool constraintsMatch = sketchExists && String.Equals(M2Json.CanonicalText(state.PatternConstraintsBefore),
                    M2Json.CanonicalText(M3ConstraintViews(actual)), StringComparison.Ordinal);
                bool frameMatch = sketchExists && String.Equals(M2Json.CanonicalText(state.PatternFrameBefore),
                    M2Json.CanonicalText(FakeM3SketchFrameView(actual)), StringComparison.Ordinal);
                bool referenceStatusMatch = sketchExists && M3PatternReferenceStatusesMatch(state, actual);
                bool nonTargetGeometryMatch = sketchExists && M3PatternNonTargetsMatch(state, actual);
                bool patternHistoryMatch = sketchExists && m3Features.Exists(delegate(FakeM3Feature f) {
                    return f.Id == state.FeatureId && f.Kind == "SKETCH_PATTERN" && f.SketchId == state.Sketch.Id;
                });
                bool patternBodiesMatch = M3NonTargetsUnchanged(state);
                string solverStatus = state.Sketch.ConstraintMode == "geometry_only" ? "UnderConstrained" : "FullyConstrained";
                int solverDof = state.Sketch.ConstraintMode == "geometry_only" ? Math.Max(1, state.Sketch.Geometries.Count * 2) : 0;
                bool solverMatch = !String.IsNullOrWhiteSpace(solverStatus) && solverDof >= 0;
                Dictionary<string, object> patternChecks = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "pattern_instances", VerificationCheck(patternInstancesMatch, new Dictionary<string, object> {
                        { "expected_instance_count", state.PatternPositionTransforms.Count },
                        { "actual_instance_count", state.PatternPositionTransforms.Count },
                        { "expected_output_curve_count", state.PatternOutput.Count },
                        { "actual_output_curve_count", actual == null ? 0 : actual.Geometries.Count - state.PatternSourceBefore.Count },
                        { "expected_instances", state.PatternExpectedInstances },
                        { "actual_output_records", state.PatternEvidence == null ? null : state.PatternEvidence["output_curve_records"] }
                    }) },
                    { "source_curves", VerificationCheck(sourceCurvesMatch, new Dictionary<string, object> {
                        { "before", M3GeometryViewList(state.PatternSourceBefore) },
                        { "after", actual == null ? null : M3GeometryViewList(M3PatternOriginalGeometries(state, actual)) }
                    }) },
                    { "sketch_constraints", VerificationCheck(constraintsMatch, new Dictionary<string, object> {
                        { "before", state.PatternConstraintsBefore }, { "after", actual == null ? null : M3ConstraintViews(actual) }
                    }) },
                    { "sketch_frames", VerificationCheck(frameMatch, new Dictionary<string, object> {
                        { "before", state.PatternFrameBefore }, { "after", actual == null ? null : FakeM3SketchFrameView(actual) }
                    }) },
                    { "reference_status", VerificationCheck(referenceStatusMatch, new Dictionary<string, object> {
                        { "before", state.PatternReferenceStatusBefore },
                        { "after", actual == null ? null : M3PatternReferenceStatusAfter(state, actual) }
                    }) },
                    { "non_target_geometry", VerificationCheck(nonTargetGeometryMatch, new Dictionary<string, object> {
                        { "before", M3GeometryViewList(state.PatternNonTargetsBefore) },
                        { "after", actual == null ? null : M3PatternCurrentNonTargets(state, actual) }
                    }) },
                    { "history", VerificationCheck(patternHistoryMatch, new Dictionary<string, object> {
                        { "feature_id", state.FeatureId }, { "feature_count", m3Features.Count }
                    }) },
                    { "bodies", VerificationCheck(patternBodiesMatch, new Dictionary<string, object> {
                        { "body_inventory", M3BodyInventory() }, { "body_count", m3Bodies.Count }
                    }) },
                    { "sketch_solver_status", VerificationCheck(solverMatch, new Dictionary<string, object> {
                        { "status", solverStatus }, { "degrees_of_freedom", solverDof }, { "acceptable", true }
                    }) }
                };
                bool patternVerified = patternInstancesMatch && sourceCurvesMatch && constraintsMatch && frameMatch &&
                    referenceStatusMatch && nonTargetGeometryMatch && patternHistoryMatch && patternBodiesMatch && solverMatch;
                Dictionary<string, object> patternResult = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "verified", patternVerified }, { "revision_id", binding.RevisionId }, { "checks", patternChecks }
                };
                if (state.PatternEvidence != null)
                    foreach (KeyValuePair<string, object> item in state.PatternEvidence) patternResult[item.Key] = item.Value;
                return new M3VerificationResult {
                    Verified = patternVerified, RevisionId = binding.RevisionId, Checks = patternChecks,
                    Result = patternResult, ErrorCode = patternVerified ? null : "PART_VERIFICATION_FAILED",
                    ErrorMessage = patternVerified ? null : "fixture F09 pattern, source, constraint, frame, reference, history, solver, or body check mismatched"
                };
            }

            if (projection)
            {
                FakeM3Sketch actual;
                bool sketchExists = m3Sketches.TryGetValue(state.Sketch.Id, out actual);
                bool projectionGeometryMatches = sketchExists && M3ProjectionGeometryMatches(state, actual);
                bool projectionSourceCurvesMatch = M3ProjectionSourceCurvesMatch(state);
                bool projectionConstraintsMatch = sketchExists && String.Equals(M2Json.CanonicalText(state.ProjectionConstraintsBefore),
                    M2Json.CanonicalText(M3ConstraintViews(actual)), StringComparison.Ordinal);
                bool projectionFrameMatches = sketchExists && String.Equals(M2Json.CanonicalText(state.ProjectionFrameBefore),
                    M2Json.CanonicalText(FakeM3SketchFrameView(actual)), StringComparison.Ordinal);
                bool projectionReferenceStatusMatches = sketchExists && M3ProjectionReferenceStatusesMatch(state, actual);
                bool projectionNonTargetGeometryMatches = sketchExists && M3ProjectionNonTargetsMatch(state, actual);
                bool projectionHistoryMatch = sketchExists && m3Features.Exists(delegate(FakeM3Feature feature) {
                    return String.Equals(feature.Id, state.FeatureId, StringComparison.Ordinal) &&
                        String.Equals(feature.Kind, "SKETCH_PROJECTION", StringComparison.Ordinal) &&
                        String.Equals(feature.SketchId, state.Sketch.Id, StringComparison.Ordinal);
                });
                bool projectionBodiesMatch = M3NonTargetsUnchanged(state);
                string projectionSolverStatus = "UnderConstrained";
                int projectionSolverDof = Math.Max(1, state.ProjectionOutput.Count * 2);
                bool projectionSolverMatches = projectionSolverDof > 0 && !String.IsNullOrWhiteSpace(projectionSolverStatus);
                Dictionary<string, object> projectionChecks = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "projection_geometry", VerificationCheck(projectionGeometryMatches, new Dictionary<string, object> {
                        { "target_sketch_id", state.Sketch.Id },
                        { "expected_output_curve_count", state.ProjectionOutput.Count },
                        { "actual_output_curve_count", actual == null ? 0 : actual.Geometries.Count - state.ProjectionTargetBefore.Count },
                        { "expected_output_curve_records", M3GeometryViewList(state.ProjectionOutput) },
                        { "actual_output_curve_records", actual == null ? null : M3ProjectionOutputViews(state, actual) }
                    }) },
                    { "source_curves", VerificationCheck(projectionSourceCurvesMatch, new Dictionary<string, object> {
                        { "before", M3GeometryViewList(state.ProjectionSourceBefore) },
                        { "after", M3ProjectionSourceViews(state) }
                    }) },
                    { "sketch_constraints", VerificationCheck(projectionConstraintsMatch, new Dictionary<string, object> {
                        { "before", state.ProjectionConstraintsBefore },
                        { "after", actual == null ? null : M3ConstraintViews(actual) }
                    }) },
                    { "sketch_frames", VerificationCheck(projectionFrameMatches, new Dictionary<string, object> {
                        { "before", state.ProjectionFrameBefore },
                        { "after", actual == null ? null : FakeM3SketchFrameView(actual) }
                    }) },
                    { "reference_status", VerificationCheck(projectionReferenceStatusMatches, new Dictionary<string, object> {
                        { "before", state.ProjectionReferenceStatusBefore },
                        { "after", actual == null ? null : M3ProjectionReferenceStatusAfter(state, actual) }
                    }) },
                    { "non_target_geometry", VerificationCheck(projectionNonTargetGeometryMatches, new Dictionary<string, object> {
                        { "before", M3GeometryViewList(state.ProjectionTargetBefore) },
                        { "after", actual == null ? null : M3ProjectionNonTargets(state, actual) }
                    }) },
                    { "history", VerificationCheck(projectionHistoryMatch, new Dictionary<string, object> {
                        { "feature_id", state.FeatureId }, { "feature_count", m3Features.Count }
                    }) },
                    { "bodies", VerificationCheck(projectionBodiesMatch, new Dictionary<string, object> {
                        { "body_inventory", M3BodyInventory() }, { "body_count", m3Bodies.Count }
                    }) },
                    { "sketch_solver_status", VerificationCheck(projectionSolverMatches, new Dictionary<string, object> {
                        { "status", projectionSolverStatus }, { "degrees_of_freedom", projectionSolverDof }, { "fixture_only", true }
                    }) }
                };
                bool projectionVerified = projectionGeometryMatches && projectionSourceCurvesMatch && projectionConstraintsMatch &&
                    projectionFrameMatches && projectionReferenceStatusMatches && projectionNonTargetGeometryMatches &&
                    projectionHistoryMatch && projectionBodiesMatch && projectionSolverMatches;
                Dictionary<string, object> projectionResult = new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "verified", projectionVerified }, { "revision_id", binding.RevisionId }, { "checks", projectionChecks }
                };
                if (state.ProjectionEvidence != null)
                    foreach (KeyValuePair<string, object> item in state.ProjectionEvidence)
                        projectionResult[item.Key] = item.Value;
                return new M3VerificationResult {
                    Verified = projectionVerified, RevisionId = binding.RevisionId, Checks = projectionChecks,
                    Result = projectionResult, ErrorCode = projectionVerified ? null : "PART_VERIFICATION_FAILED",
                    ErrorMessage = projectionVerified ? null : "fixture F10 projection geometry, source, constraints, frame, references, history, solver, or body evidence mismatched"
                };
            }

            bool nonTargetsMatch = M3NonTargetsUnchanged(state);
            Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "geometry", VerificationCheck(geometryMatch, new Dictionary<string, object> {
                    { "sketch_id", state.Sketch == null ? null : state.Sketch.Id },
                    { "feature_id", state.FeatureId }, { "body_inventory", M3BodyInventory() } }) },
                { "constraints", VerificationCheck(constraintMatch, new Dictionary<string, object> {
                    { "driving_constraint_ids", M3ConstraintIds(state.Sketch) },
                    { "driving_dimensions", M3DrivingDimensionViews(state.Sketch) } }) },
                { "history", VerificationCheck(historyMatch, new Dictionary<string, object> {
                    { "feature_id", state.FeatureId }, { "feature_count", m3Features.Count } }) },
                { "bodies", VerificationCheck(bodyMatch, new Dictionary<string, object> {
                    { "body_inventory", M3BodyInventory() }, { "body_count", m3Bodies.Count } }) },
                { "non_target_bodies", VerificationCheck(nonTargetsMatch, new Dictionary<string, object> {
                    { "before", state.NonTargetBefore }, { "after", M3CurrentNonTargets(state) } }) }
            };
            if (String.Equals(state.Kind, "create_hole", StringComparison.Ordinal))
            {
                // Synthetic checks exercise bridge dispatch only; they do not prove NX hole behavior.
                checks["hole_definition"] = VerificationCheck(geometryMatch, new Dictionary<string, object> { { "fixture_only", true } });
                checks["model_preservation"] = VerificationCheck(nonTargetsMatch, new Dictionary<string, object> { { "fixture_only", true } });
                checks["body_effect"] = VerificationCheck(bodyMatch, new Dictionary<string, object> { { "fixture_only", true } });
                checks["hole_coverage"] = VerificationCheck(geometryMatch, new Dictionary<string, object> { { "fixture_only", true } });
                checks["support_associations"] = VerificationCheck(historyMatch, new Dictionary<string, object> { { "fixture_only", true } });
            }
            if (String.Equals(state.Kind, "edit_sketch_corner", StringComparison.Ordinal))
            {
                checks["sketch_solver_status"] = VerificationCheck(geometryMatch, new Dictionary<string, object> {
                    { "status", "UnderConstrained" }, { "degrees_of_freedom", 4 }, { "acceptable", true }
                });
                checks["sketch_ownership"] = VerificationCheck(geometryMatch, new Dictionary<string, object> {
                    { "requested_sketch_id", state.Sketch.Id }, { "native_sketch_id", state.Sketch.Id },
                    { "curve_count", M3GeometryViews(state.Sketch).Count }
                });
                checks["non_target_geometry"] = VerificationCheck(geometryMatch, new Dictionary<string, object> {
                    { "before", M3GeometryViewList(state.CornerNonTargetsBefore) },
                    { "after", M3CurrentCornerNonTargets(state) }
                });
            }
            bool verified = geometryMatch && constraintMatch && historyMatch && bodyMatch && nonTargetsMatch;
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "verified", verified }, { "revision_id", binding.RevisionId }, { "checks", checks }
            };
            if (String.Equals(state.Kind, "edit_sketch_corner", StringComparison.Ordinal) && state.CornerEvidence != null)
                foreach (KeyValuePair<string, object> item in state.CornerEvidence) result[item.Key] = item.Value;
            if (String.Equals(state.Kind, "edit_sketch_offset", StringComparison.Ordinal) && state.OffsetEvidence != null)
                foreach (KeyValuePair<string, object> item in state.OffsetEvidence) result[item.Key] = item.Value;
            return new M3VerificationResult {
                Verified = verified, RevisionId = binding.RevisionId, Checks = checks,
                Result = result,
                ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
                ErrorMessage = verified ? null : "fixture M3 geometry, constraint, history, or body observation mismatched"
            };
        }

        private static List<object> M3ProjectionOutputViews(FakeM3PlanState state, FakeM3Sketch actual)
        {
            List<object> result = new List<object>();
            if (state == null || actual == null) return result;
            foreach (FakeM3Geometry expected in state.ProjectionOutput)
            {
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry geometry) {
                    return String.Equals(geometry.Id, expected.Id, StringComparison.Ordinal);
                });
                if (found != null) result.Add(M3GeometryView(found));
            }
            return result;
        }

        private List<object> M3ProjectionSourceViews(FakeM3PlanState state)
        {
            List<object> result = new List<object>();
            if (state == null) return result;
            for (int index = 0; index < state.ProjectionSources.Count; index++)
            {
                FakeM3Sketch sourceSketch = state.ProjectionSourceSketches[index];
                FakeM3Geometry source = state.ProjectionSources[index];
                FakeM3Sketch actualSketch;
                FakeM3Geometry found = null;
                if (sourceSketch != null && m3Sketches.TryGetValue(sourceSketch.Id, out actualSketch))
                    found = actualSketch.Geometries.Find(delegate(FakeM3Geometry geometry) {
                        return String.Equals(geometry.Id, source.Id, StringComparison.Ordinal);
                    });
                result.Add(found == null ? null : M3GeometryView(found));
            }
            return result;
        }

        private static List<object> M3ProjectionNonTargets(FakeM3PlanState state, FakeM3Sketch actual)
        {
            List<object> result = new List<object>();
            if (state == null || actual == null) return result;
            HashSet<string> outputIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (FakeM3Geometry output in state.ProjectionOutput) outputIds.Add(output.Id);
            foreach (FakeM3Geometry geometry in actual.Geometries)
                if (!outputIds.Contains(geometry.Id)) result.Add(M3GeometryView(geometry));
            return result;
        }

        private static bool M3ProjectionGeometryMatches(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (state == null || actual == null || state.ProjectionOutput == null ||
                actual.Geometries.Count != state.ProjectionTargetBefore.Count + state.ProjectionOutput.Count) return false;
            HashSet<string> outputIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (FakeM3Geometry expected in state.ProjectionOutput)
            {
                if (!outputIds.Add(expected.Id)) return false;
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry geometry) {
                    return String.Equals(geometry.Id, expected.Id, StringComparison.Ordinal);
                });
                if (found == null || !String.Equals(M2Json.CanonicalText(M3GeometryView(expected)),
                    M2Json.CanonicalText(M3GeometryView(found)), StringComparison.Ordinal)) return false;
            }
            return String.Equals(M2Json.CanonicalText(M3GeometryViewList(state.ProjectionTargetBefore)),
                M2Json.CanonicalText(M3ProjectionNonTargets(state, actual)), StringComparison.Ordinal);
        }

        private bool M3ProjectionSourceCurvesMatch(FakeM3PlanState state)
        {
            if (state == null || state.ProjectionSources == null ||
                state.ProjectionSources.Count != state.ProjectionSourceBefore.Count ||
                state.ProjectionSources.Count != state.ProjectionSourceSketches.Count) return false;
            for (int index = 0; index < state.ProjectionSources.Count; index++)
            {
                FakeM3Sketch sourceSketch = state.ProjectionSourceSketches[index];
                FakeM3Geometry source = state.ProjectionSources[index];
                FakeM3Sketch actualSketch;
                if (sourceSketch == null || !m3Sketches.TryGetValue(sourceSketch.Id, out actualSketch)) return false;
                FakeM3Geometry found = actualSketch.Geometries.Find(delegate(FakeM3Geometry geometry) {
                    return String.Equals(geometry.Id, source.Id, StringComparison.Ordinal);
                });
                if (found == null || !String.Equals(M2Json.CanonicalText(M3GeometryView(state.ProjectionSourceBefore[index])),
                    M2Json.CanonicalText(M3GeometryView(found)), StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static List<object> M3ProjectionReferenceStatusAfter(FakeM3PlanState state, FakeM3Sketch actual)
        {
            List<object> result = new List<object>();
            HashSet<string> outputIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (FakeM3Geometry output in state.ProjectionOutput) outputIds.Add(output.Id);
            foreach (FakeM3Geometry geometry in actual.Geometries)
                if (!outputIds.Contains(geometry.Id))
                    result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "curve_id", geometry.Id }, { "journal_id", geometry.JournalId },
                        { "reference_status", geometry.ReferenceStatus }
                    });
            foreach (FakeM3Geometry output in state.ProjectionOutput)
            {
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry geometry) {
                    return String.Equals(geometry.Id, output.Id, StringComparison.Ordinal);
                });
                if (found != null)
                    result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "curve_id", found.Id }, { "journal_id", found.JournalId },
                        { "reference_status", found.ReferenceStatus }
                    });
            }
            return result;
        }

        private static bool M3ProjectionReferenceStatusesMatch(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (state == null || actual == null) return false;
            List<object> before = new List<object>();
            HashSet<string> outputIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (FakeM3Geometry output in state.ProjectionOutput) outputIds.Add(output.Id);
            foreach (FakeM3Geometry geometry in state.ProjectionTargetBefore)
                before.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "curve_id", geometry.Id }, { "journal_id", geometry.JournalId },
                    { "reference_status", geometry.ReferenceStatus }
                });
            List<object> afterOriginals = new List<object>();
            foreach (FakeM3Geometry geometry in actual.Geometries)
                if (!outputIds.Contains(geometry.Id))
                    afterOriginals.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "curve_id", geometry.Id }, { "journal_id", geometry.JournalId },
                        { "reference_status", geometry.ReferenceStatus }
                    });
            if (!String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(afterOriginals), StringComparison.Ordinal))
                return false;
            foreach (FakeM3Geometry output in state.ProjectionOutput)
            {
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry geometry) {
                    return String.Equals(geometry.Id, output.Id, StringComparison.Ordinal);
                });
                if (found == null || found.ReferenceStatus != 1) return false;
            }
            return true;
        }

        private static bool M3ProjectionNonTargetsMatch(FakeM3PlanState state, FakeM3Sketch actual)
        {
            return state != null && actual != null && String.Equals(
                M2Json.CanonicalText(M3GeometryViewList(state.ProjectionTargetBefore)),
                M2Json.CanonicalText(M3ProjectionNonTargets(state, actual)), StringComparison.Ordinal);
        }

        private static bool M3CornerGeometryMatches(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (actual == null || state.CornerCurve1 == null || state.CornerCurve2 == null || state.CornerGeometry == null) return false;
            if (FakeM3Distance(FakeM3Endpoint(state.CornerCurve1, state.CornerRequest.Curve1End), state.CornerCut1) > 1e-9 ||
                FakeM3Distance(FakeM3Endpoint(state.CornerCurve2, state.CornerRequest.Curve2End), state.CornerCut2) > 1e-9) return false;
            if (!actual.Geometries.Contains(state.CornerCurve1) || !actual.Geometries.Contains(state.CornerCurve2) ||
                !actual.Geometries.Contains(state.CornerGeometry)) return false;
            if (state.CornerRequest.Kind == "fillet" && Math.Abs(state.CornerGeometry.Radius - state.CornerRequest.RadiusMm) > 1e-9) return false;
            List<object> nonTargets = new List<object>();
            foreach (FakeM3Geometry geometry in actual.Geometries)
                if (!Object.ReferenceEquals(geometry, state.CornerCurve1) && !Object.ReferenceEquals(geometry, state.CornerCurve2) &&
                    !Object.ReferenceEquals(geometry, state.CornerGeometry)) nonTargets.Add(M3GeometryView(geometry));
            return String.Equals(M2Json.CanonicalText(M3GeometryViewList(state.CornerNonTargetsBefore)),
                M2Json.CanonicalText(nonTargets), StringComparison.Ordinal);
        }

        private static bool M3PatternGeometryMatches(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (actual == null || state.PatternSourceBefore == null || state.PatternOutput == null || state.PatternOutput.Count == 0)
                return false;
            if (actual.Geometries.Count != state.PatternSourceBefore.Count + state.PatternOutput.Count) return false;
            HashSet<string> matched = new HashSet<string>(StringComparer.Ordinal);
            foreach (FakeM3Geometry before in state.PatternSourceBefore)
            {
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, before.Id, StringComparison.Ordinal);
                });
                if (found == null || !matched.Add(found.Id) || !String.Equals(
                    M2Json.CanonicalText(M3GeometryView(before)), M2Json.CanonicalText(M3GeometryView(found)), StringComparison.Ordinal))
                    return false;
            }
            foreach (FakeM3Geometry expected in state.PatternOutput)
            {
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, expected.Id, StringComparison.Ordinal);
                });
                if (found == null || !matched.Add(found.Id) || !String.Equals(
                    M2Json.CanonicalText(M3GeometryView(expected)), M2Json.CanonicalText(M3GeometryView(found)), StringComparison.Ordinal))
                    return false;
            }
            return matched.Count == actual.Geometries.Count;
        }

        private static bool M3PatternSourceCurvesMatch(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (state == null || actual == null || state.PatternRequest == null) return false;
            foreach (string sourceId in state.PatternRequest.SourceCurveIds)
            {
                FakeM3Geometry before = state.PatternSourceBefore.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, sourceId, StringComparison.Ordinal) ||
                        String.Equals(item.JournalId, sourceId, StringComparison.Ordinal);
                });
                if (before == null) return false;
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, before.Id, StringComparison.Ordinal);
                });
                if (found == null || !String.Equals(M2Json.CanonicalText(M3GeometryView(before)),
                    M2Json.CanonicalText(M3GeometryView(found)), StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static List<FakeM3Geometry> M3PatternOriginalGeometries(FakeM3PlanState state, FakeM3Sketch actual)
        {
            List<FakeM3Geometry> result = new List<FakeM3Geometry>();
            if (state == null || actual == null) return result;
            foreach (FakeM3Geometry before in state.PatternSourceBefore)
            {
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, before.Id, StringComparison.Ordinal);
                });
                if (found != null) result.Add(found);
            }
            return result;
        }

        private static List<object> M3PatternCurrentNonTargets(FakeM3PlanState state, FakeM3Sketch actual)
        {
            List<object> result = new List<object>();
            if (state == null || actual == null) return result;
            foreach (FakeM3Geometry geometry in actual.Geometries)
            {
                bool source = state.PatternRequest.SourceCurveIds.Exists(delegate(string sourceId) {
                    FakeM3Geometry before = state.PatternSourceBefore.Find(delegate(FakeM3Geometry item) {
                        return String.Equals(item.Id, sourceId, StringComparison.Ordinal) ||
                            String.Equals(item.JournalId, sourceId, StringComparison.Ordinal);
                    });
                    return before != null && String.Equals(geometry.Id, before.Id, StringComparison.Ordinal);
                });
                bool output = state.PatternOutput.Exists(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, geometry.Id, StringComparison.Ordinal);
                });
                if (!source && !output) result.Add(M3GeometryView(geometry));
            }
            return result;
        }

        private static bool M3PatternNonTargetsMatch(FakeM3PlanState state, FakeM3Sketch actual)
        {
            List<object> after = M3PatternCurrentNonTargets(state, actual);
            return after.Count == state.PatternNonTargetsBefore.Count && String.Equals(
                M2Json.CanonicalText(M3GeometryViewList(state.PatternNonTargetsBefore)),
                M2Json.CanonicalText(after), StringComparison.Ordinal);
        }

        private static List<object> M3PatternReferenceStatusAfter(FakeM3PlanState state, FakeM3Sketch actual)
        {
            List<object> result = new List<object>();
            foreach (FakeM3Geometry geometry in M3PatternOriginalGeometries(state, actual))
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "curve_id", geometry.Id }, { "journal_id", geometry.JournalId },
                    { "reference_status", geometry.ReferenceStatus }
                });
            foreach (FakeM3Geometry geometry in state.PatternOutput)
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "curve_id", geometry.Id }, { "journal_id", geometry.JournalId },
                    { "reference_status", geometry.ReferenceStatus }
                });
            return result;
        }

        private static bool M3PatternReferenceStatusesMatch(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (state == null || actual == null) return false;
            List<object> before = M3PatternReferenceStatusBeforeOriginals(state);
            List<object> after = M3PatternReferenceStatusAfter(state, actual);
            if (before.Count != state.PatternSourceBefore.Count || after.Count != before.Count + state.PatternOutput.Count) return false;
            for (int index = 0; index < state.PatternOutput.Count; index++)
                if (state.PatternOutput[index].ReferenceStatus != 1) return false;
            return String.Equals(M2Json.CanonicalText(before),
                M2Json.CanonicalText(M3PatternReferenceStatusAfterOriginals(state, actual)), StringComparison.Ordinal);
        }

        private static List<object> M3PatternReferenceStatusBeforeOriginals(FakeM3PlanState state)
        {
            List<object> result = new List<object>();
            foreach (FakeM3Geometry geometry in state.PatternSourceBefore)
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "curve_id", geometry.Id }, { "journal_id", geometry.JournalId },
                    { "reference_status", geometry.ReferenceStatus }
                });
            return result;
        }

        private static List<object> M3PatternReferenceStatusAfterOriginals(FakeM3PlanState state, FakeM3Sketch actual)
        {
            List<object> result = new List<object>();
            foreach (FakeM3Geometry geometry in M3PatternOriginalGeometries(state, actual))
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "curve_id", geometry.Id }, { "journal_id", geometry.JournalId },
                    { "reference_status", geometry.ReferenceStatus }
                });
            return result;
        }

        private static bool M3OffsetGeometryMatches(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (actual == null || state.OffsetSourceBefore == null || state.OffsetOutput == null || state.OffsetOutput.Count == 0)
                return false;
            if (actual.Geometries.Count != state.OffsetSourceBefore.Count + state.OffsetOutput.Count) return false;
            foreach (FakeM3Geometry source in state.OffsetSourceBefore)
            {
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, source.Id, StringComparison.Ordinal);
                });
                if (found == null || !String.Equals(M2Json.CanonicalText(M3GeometryView(source)),
                    M2Json.CanonicalText(M3GeometryView(found)), StringComparison.Ordinal)) return false;
            }
            foreach (FakeM3Geometry expected in state.OffsetOutput)
            {
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, expected.Id, StringComparison.Ordinal);
                });
                if (found == null || !String.Equals(M2Json.CanonicalText(M3GeometryView(expected)),
                    M2Json.CanonicalText(M3GeometryView(found)), StringComparison.Ordinal)) return false;
                if (String.Equals(expected.Type, "arc", StringComparison.Ordinal) &&
                    Math.Abs(found.Radius - state.OffsetRequest.DistanceMm) > 1e-9) return false;
            }
            return true;
        }

        private static List<FakeM3Geometry> OriginalM3Geometries(FakeM3PlanState state)
        {
            List<FakeM3Geometry> result = new List<FakeM3Geometry>();
            if (state == null || state.Sketch == null) return result;
            foreach (FakeM3Geometry source in state.OffsetSourceBefore)
            {
                FakeM3Geometry found = state.Sketch.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, source.Id, StringComparison.Ordinal);
                });
                if (found != null) result.Add(found);
            }
            return result;
        }

        private static bool M3OffsetSourceCurvesMatch(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (state == null || actual == null || state.OffsetRequest == null) return false;
            foreach (M3SketchOffsetCurveRequest entry in state.OffsetRequest.CurveChain)
            {
                FakeM3Geometry source = state.OffsetSourceBefore.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, entry.CurveId, StringComparison.Ordinal) ||
                        String.Equals(item.JournalId, entry.CurveId, StringComparison.Ordinal);
                });
                if (source == null) return false;
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, source.Id, StringComparison.Ordinal);
                });
                if (found == null || !String.Equals(M2Json.CanonicalText(M3GeometryView(source)),
                    M2Json.CanonicalText(M3GeometryView(found)), StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static List<object> M3OffsetReferenceStatusAfter(FakeM3PlanState state, FakeM3Sketch actual)
        {
            List<FakeM3Geometry> original = OriginalM3Geometries(state);
            List<object> result = FakeM3ReferenceStatusViews(original);
            foreach (FakeM3Geometry output in state.OffsetOutput)
            {
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, output.Id, StringComparison.Ordinal);
                });
                if (found != null)
                    result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                        { "curve_id", found.Id }, { "journal_id", found.JournalId },
                        { "reference_status", found.ReferenceStatus }
                    });
            }
            return result;
        }

        private static bool M3OffsetReferenceStatusesMatch(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (state == null || actual == null) return false;
            List<object> currentOriginal = FakeM3ReferenceStatusViews(OriginalM3Geometries(state));
            if (!String.Equals(M2Json.CanonicalText(state.OffsetReferenceStatusBefore),
                M2Json.CanonicalText(currentOriginal), StringComparison.Ordinal)) return false;
            foreach (FakeM3Geometry output in state.OffsetOutput)
            {
                FakeM3Geometry found = actual.Geometries.Find(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, output.Id, StringComparison.Ordinal);
                });
                if (found == null || found.ReferenceStatus != 1) return false;
            }
            return true;
        }

        private static bool M3OffsetNonTargetsMatch(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (state == null || actual == null) return false;
            List<object> before = M3GeometryViewList(state.OffsetNonTargetsBefore);
            List<object> after = M3GeometryViewList(M3CurrentOffsetNonTargets(state));
            return String.Equals(M2Json.CanonicalText(before), M2Json.CanonicalText(after), StringComparison.Ordinal);
        }

        private static bool M3OffsetOutputConnected(FakeM3PlanState state)
        {
            if (state == null || state.OffsetOutput == null || state.OffsetOutput.Count == 0) return false;
            for (int i = 0; i < state.OffsetOutput.Count; i++)
            {
                FakeM3Geometry current = state.OffsetOutput[i];
                if (current.Start == null || current.End == null || FakeM3Distance(current.Start, current.End) <= 1e-9)
                    return false;
                if (i + 1 < state.OffsetOutput.Count && FakeM3Distance(current.End, state.OffsetOutput[i + 1].Start) > 1e-9)
                    return false;
            }
            return !state.OffsetInputClosed || FakeM3Distance(state.OffsetOutput[state.OffsetOutput.Count - 1].End,
                state.OffsetOutput[0].Start) <= 1e-9;
        }

        private static List<FakeM3Geometry> M3CurrentOffsetNonTargets(FakeM3PlanState state)
        {
            List<FakeM3Geometry> result = new List<FakeM3Geometry>();
            if (state == null || state.Sketch == null) return result;
            foreach (FakeM3Geometry geometry in state.Sketch.Geometries)
            {
                bool original = state.OffsetSourceBefore.Exists(delegate(FakeM3Geometry source) {
                    return String.Equals(source.Id, geometry.Id, StringComparison.Ordinal);
                });
                bool output = state.OffsetOutput.Exists(delegate(FakeM3Geometry item) {
                    return String.Equals(item.Id, geometry.Id, StringComparison.Ordinal);
                });
                if (original && !state.OffsetRequest.CurveChain.Exists(delegate(M3SketchOffsetCurveRequest entry) {
                    return String.Equals(entry.CurveId, geometry.Id, StringComparison.Ordinal) ||
                        String.Equals(entry.CurveId, geometry.JournalId, StringComparison.Ordinal);
                }) && !output) result.Add(geometry);
            }
            return result;
        }

        private static List<object> M3CurrentCornerNonTargets(FakeM3PlanState state)
        {
            List<object> result = new List<object>();
            if (state == null || state.Sketch == null) return result;
            foreach (FakeM3Geometry geometry in state.Sketch.Geometries)
                if (!Object.ReferenceEquals(geometry, state.CornerCurve1) && !Object.ReferenceEquals(geometry, state.CornerCurve2) &&
                    !Object.ReferenceEquals(geometry, state.CornerGeometry)) result.Add(M3GeometryView(geometry));
            return result;
        }

        private static bool M3CornerConstraintsPreserved(FakeM3PlanState state, FakeM3Sketch actual)
        {
            if (actual == null) return false;
            List<object> current = M3ConstraintViews(actual);
            foreach (string id in state.CornerConstraintIdsBefore)
                if (!actual.Constraints.Exists(delegate(FakeM3Constraint constraint) { return String.Equals(constraint.Id, id, StringComparison.Ordinal); })) return false;
            foreach (object before in state.CornerConstraintsBefore)
            {
                Dictionary<string, object> beforeRecord = before as Dictionary<string, object>;
                if (beforeRecord == null) return false;
                string id = Convert.ToString(beforeRecord["journal_id"], CultureInfo.InvariantCulture);
                bool found = false;
                foreach (object after in current)
                {
                    Dictionary<string, object> afterRecord = after as Dictionary<string, object>;
                    if (afterRecord != null && String.Equals(Convert.ToString(afterRecord["journal_id"], CultureInfo.InvariantCulture), id, StringComparison.Ordinal))
                    {
                        if (!String.Equals(M2Json.CanonicalText(beforeRecord), M2Json.CanonicalText(afterRecord), StringComparison.Ordinal)) return false;
                        found = true;
                        break;
                    }
                }
                if (!found) return false;
            }
            return true;
        }

        private List<FakeM3Body> SortedM3Bodies()
        {
            List<FakeM3Body> result = new List<FakeM3Body>(m3Bodies.Values);
            result.Sort(delegate(FakeM3Body a, FakeM3Body b) { return StringComparer.Ordinal.Compare(a.Id, b.Id); });
            return result;
        }

        private List<object> M3BodyInventory()
        {
            List<object> result = new List<object>();
            foreach (FakeM3Body body in SortedM3Bodies()) result.Add(M3BodyView(body));
            return result;
        }

        private static Dictionary<string, object> M3BodyView(FakeM3Body body)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "journal_id", body.Id }, { "is_solid", body.IsSolid },
                { "volume_mm3", body.Volume }, { "bbox_mm", new List<double>(body.Bbox) }
            };
        }

        private List<Dictionary<string, object>> M3CurrentNonTargets(FakeM3PlanState state)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (FakeM3Body body in SortedM3Bodies())
            {
                if (!String.IsNullOrEmpty(state.TargetBodyId) && String.Equals(body.Id, state.TargetBodyId, StringComparison.Ordinal))
                    continue;
                if ((String.Equals(state.Kind, "create_extrude", StringComparison.Ordinal) &&
                     String.Equals(state.Extrude.Mode, "create", StringComparison.Ordinal) ||
                     String.Equals(state.Kind, "create_revolve", StringComparison.Ordinal) &&
                     String.Equals(state.Revolve.Mode, "create", StringComparison.Ordinal)) &&
                    String.Equals(body.Id, state.BodyId, StringComparison.Ordinal))
                    continue;
                result.Add(M3BodyView(body));
            }
            return result;
        }

        private bool M3NonTargetsUnchanged(FakeM3PlanState state)
        {
            List<Dictionary<string, object>> actual = M3CurrentNonTargets(state);
            if (actual.Count != state.NonTargetBefore.Count) return false;
            for (int i = 0; i < actual.Count; i++)
            {
                Dictionary<string, object> expected = state.NonTargetBefore[i];
                Dictionary<string, object> found = actual[i];
                if (!Object.Equals(expected["journal_id"], found["journal_id"]) ||
                    !Object.Equals(expected["is_solid"], found["is_solid"]) ||
                    !Object.Equals(expected["volume_mm3"], found["volume_mm3"])) return false;
                List<double> expectedBounds = expected["bbox_mm"] as List<double>;
                List<double> actualBounds = found["bbox_mm"] as List<double>;
                if (expectedBounds == null || actualBounds == null || expectedBounds.Count != actualBounds.Count) return false;
                for (int j = 0; j < expectedBounds.Count; j++)
                    if (!expectedBounds[j].Equals(actualBounds[j])) return false;
            }
            return true;
        }

        private static List<object> M3ConstraintViews(FakeM3Sketch sketch)
        {
            List<object> result = new List<object>();
            foreach (FakeM3Constraint constraint in sketch.Constraints)
            {
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal) {
                    { "constraint_type", constraint.Type }, { "class", "DIMENSIONAL" },
                    { "geometry_refs", new List<string>(constraint.GeometryRefs) },
                    { "is_driving", constraint.IsDriving }, { "dimension_state", constraint.IsDriving ? "Driving" : "Reference" },
                    { "expression_name", constraint.ExpressionName },
                    { "value", M3NumberText(constraint.Value) }, { "journal_id", constraint.Id },
                    { "semantic_key", constraint.Type + "|" + String.Join("|", constraint.GeometryRefs.ToArray()) +
                        "|" + constraint.ExpressionName + "|" + M3NumberText(constraint.Value) }
                });
            }
            return result;
        }

        private static List<object> M3GeometryViews(FakeM3Sketch sketch)
        {
            List<object> result = new List<object>();
            foreach (FakeM3Geometry geometry in sketch.Geometries) result.Add(M3GeometryView(geometry));
            return result;
        }

        private static Dictionary<string, object> M3GeometryView(FakeM3Geometry geometry)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "id", geometry.Id }, { "journal_id", geometry.JournalId }, { "type", geometry.Type },
                { "reference_status", geometry.ReferenceStatus == 0 ? "Reference" : "Active" },
                { "reference_status_value", geometry.ReferenceStatus },
                { "reference_curve_solver_semantics", geometry.ReferenceStatus == 0
                    ? "reference_curve_can_still_participate_in_constraints" : "active_sketch_curve" }
            };
            if (geometry.Start != null) result.Add("start_mm", new List<double>(geometry.Start));
            if (geometry.End != null) result.Add("end_mm", new List<double>(geometry.End));
            if (geometry.Corner != null) result.Add("corner_mm", new List<double>(geometry.Corner));
            if (geometry.Center != null) result.Add("center_mm", new List<double>(geometry.Center));
            if (geometry.Type == "rectangle")
            {
                result.Add("width_mm", geometry.Width);
                result.Add("height_mm", geometry.Height);
            }
            if (geometry.Type == "circle") result.Add("diameter_mm", geometry.Diameter);
            if (geometry.Type == "arc") result.Add("radius_mm", geometry.Radius);
            return result;
        }

        private static double M3ProfileArea(FakeM3Sketch sketch)
        {
            double area = 0.0;
            foreach (FakeM3Geometry geometry in sketch.Geometries)
            {
                if (geometry.Type == "rectangle") area += geometry.Width * geometry.Height;
                else if (geometry.Type == "circle") area += Math.PI * geometry.Diameter * geometry.Diameter / 4.0;
            }
            return area;
        }

        private static double[] M3ExtrudedBounds(FakeM3Sketch sketch, double depth, string direction)
        {
            double minU = Double.PositiveInfinity, minV = Double.PositiveInfinity;
            double maxU = Double.NegativeInfinity, maxV = Double.NegativeInfinity;
            foreach (FakeM3Geometry geometry in sketch.Geometries)
            {
                if (geometry.Type == "line")
                {
                    minU = Math.Min(minU, Math.Min(geometry.Start[0], geometry.End[0]));
                    minV = Math.Min(minV, Math.Min(geometry.Start[1], geometry.End[1]));
                    maxU = Math.Max(maxU, Math.Max(geometry.Start[0], geometry.End[0]));
                    maxV = Math.Max(maxV, Math.Max(geometry.Start[1], geometry.End[1]));
                }
                else if (geometry.Type == "rectangle")
                {
                    minU = Math.Min(minU, geometry.Corner[0]);
                    minV = Math.Min(minV, geometry.Corner[1]);
                    maxU = Math.Max(maxU, geometry.Corner[0] + geometry.Width);
                    maxV = Math.Max(maxV, geometry.Corner[1] + geometry.Height);
                }
                else if (geometry.Type == "circle")
                {
                    double radius = geometry.Diameter / 2.0;
                    minU = Math.Min(minU, geometry.Center[0] - radius);
                    minV = Math.Min(minV, geometry.Center[1] - radius);
                    maxU = Math.Max(maxU, geometry.Center[0] + radius);
                    maxV = Math.Max(maxV, geometry.Center[1] + radius);
                }
            }
            if (Double.IsInfinity(minU)) { minU = 0.0; maxU = 0.0; minV = 0.0; maxV = 0.0; }
            double[] bounds = new double[6];
            if (sketch.Plane == "XY")
            {
                bounds[0] = sketch.Origin[0] + minU; bounds[1] = sketch.Origin[1] + minV;
                bounds[3] = sketch.Origin[0] + maxU; bounds[4] = sketch.Origin[1] + maxV;
                bounds[2] = sketch.Origin[2]; bounds[5] = sketch.Origin[2] + (direction == "negative" ? -depth : depth);
                if (bounds[2] > bounds[5]) Swap(ref bounds[2], ref bounds[5]);
            }
            else if (sketch.Plane == "XZ")
            {
                bounds[0] = sketch.Origin[0] + minU; bounds[2] = sketch.Origin[2] + minV;
                bounds[3] = sketch.Origin[0] + maxU; bounds[5] = sketch.Origin[2] + maxV;
                bounds[1] = sketch.Origin[1]; bounds[4] = sketch.Origin[1] + (direction == "negative" ? depth : -depth);
                if (bounds[1] > bounds[4]) Swap(ref bounds[1], ref bounds[4]);
            }
            else
            {
                bounds[1] = sketch.Origin[1] + minU; bounds[2] = sketch.Origin[2] + minV;
                bounds[4] = sketch.Origin[1] + maxU; bounds[5] = sketch.Origin[2] + maxV;
                bounds[0] = sketch.Origin[0]; bounds[3] = sketch.Origin[0] + (direction == "negative" ? -depth : depth);
                if (bounds[0] > bounds[3]) Swap(ref bounds[0], ref bounds[3]);
            }
            return bounds;
        }

        private static void Swap(ref double left, ref double right)
        {
            double temp = left; left = right; right = temp;
        }

        private static double[] UnionBounds(double[] left, double[] right)
        {
            return new double[] {
                Math.Min(left[0], right[0]), Math.Min(left[1], right[1]), Math.Min(left[2], right[2]),
                Math.Max(left[3], right[3]), Math.Max(left[4], right[4]), Math.Max(left[5], right[5])
            };
        }

        private M2ParameterExecutionResult ExecuteM3DimensionEdit(M2ModelBinding binding,
            M2ParameterPlanSnapshot prepared, FakeM3Constraint constraint, string requestedValue, string requestedUnit)
        {
            string before = M3NumberText(constraint.Value);
            List<string> geometryBefore = M3GeometrySummary(constraint.Sketch);
            if (!String.Equals(requestedUnit, "mm", StringComparison.Ordinal) ||
                !String.Equals(binding.RevisionId, prepared.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(before, prepared.CurrentValue, StringComparison.Ordinal))
            {
                return new M2ParameterExecutionResult {
                    State = "UNCHANGED", RevisionId = binding.RevisionId,
                    ErrorCode = !String.Equals(requestedUnit, "mm", StringComparison.Ordinal) ? "UNIT_MISMATCH" : "REVISION_MISMATCH",
                    ErrorMessage = "fixture M3 dimension or sketch revision changed after plan preparation",
                    Evidence = new Dictionary<string, object> {
                        { "changed", false }, { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(before) },
                        { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                        { "after_rhs", before }, { "after_value", before }, { "after_unit", "mm" },
                        { "geometry_before", geometryBefore }, { "geometry_after", new List<string>(geometryBefore) },
                        { "geometry_changed", false }, { "write_attempted", false }, { "readback_verified", false }
                    }
                };
            }
            double parsed;
            if (!Double.TryParse(requestedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
                Double.IsNaN(parsed) || Double.IsInfinity(parsed) || parsed <= 0.0)
                throw new M2ModelException("INVALID_REQUEST", "fixture M3 Driving dimension value must be positive and finite");

            System.Threading.Interlocked.Increment(ref writeAttemptCount);
            constraint.Value = parsed;
            ApplyM3Dimension(constraint, parsed);
            System.Threading.Interlocked.Increment(ref mutationCount);
            System.Threading.Interlocked.Increment(ref m3MutationCount);
            string revision = System.Threading.Interlocked.Increment(ref revisionNumber).ToString(CultureInfo.InvariantCulture);
            binding.RevisionId = revision;
            string after = M3NumberText(constraint.Value);
            List<string> geometryAfter = M3GeometrySummary(constraint.Sketch);
            expectedValues[prepared] = after;
            Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "changed", true }, { "before", ParameterEvidence(before) }, { "after", ParameterEvidence(after) },
                { "before_rhs", before }, { "before_value", before }, { "before_unit", "mm" },
                { "after_rhs", after }, { "after_value", after }, { "after_unit", "mm" },
                { "geometry_before", geometryBefore }, { "geometry_after", geometryAfter },
                { "geometry_changed", true }, { "write_attempted", true },
                { "update_error_count", 0 }, { "readback_verified", true }, { "revision_id", revision }
            };
            return new M2ParameterExecutionResult { State = "APPLIED", RevisionId = revision, Evidence = evidence };
        }

        private M2PartVerificationResult VerifyM3DimensionEdit(M2ModelBinding binding,
            M2ParameterPlanSnapshot prepared, FakeM3Constraint constraint)
        {
            string expected;
            if (!expectedValues.TryGetValue(prepared, out expected)) expected = prepared.CurrentValue;
            string actual = M3NumberText(constraint.Value);
            bool matches = String.Equals(expected, actual, StringComparison.Ordinal);
            List<string> geometryRefs = new List<string>(constraint.GeometryRefs);
            Dictionary<string, object> checks = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "expression", VerificationCheck(matches, new Dictionary<string, object> {
                    { "sketch_id", constraint.Sketch.Id }, { "constraint_id", constraint.Id },
                    { "expression_name", constraint.ExpressionName }, { "expected_value", expected },
                    { "actual_rhs", actual }, { "actual_value", actual }, { "unit", "mm" } }) },
                { "geometry", VerificationCheck(matches, new Dictionary<string, object> {
                    { "sketch_id", constraint.Sketch.Id }, { "constraint_id", constraint.Id },
                    { "geometry_refs", geometryRefs }, { "expected_value", expected },
                    { "actual_value", actual }, { "geometry", M3GeometryViews(constraint.Sketch) } }) },
                { "history", VerificationCheck(true, new Dictionary<string, object> {
                    { "sketch_id", constraint.Sketch.Id }, { "feature_id", constraint.Sketch.FeatureId },
                    { "feature_type", "SKETCH" }, { "feature_count", m3Features.Count } }) },
                { "associations", VerificationCheck(true, new Dictionary<string, object> {
                    { "sketch_id", constraint.Sketch.Id }, { "constraint_id", constraint.Id },
                    { "dimension_state", "Driving" }, { "expression_name", constraint.ExpressionName },
                    { "geometry_refs", geometryRefs } }) }
            };
            return new M2PartVerificationResult {
                Verified = matches, RevisionId = binding.RevisionId, Checks = checks,
                ErrorCode = matches ? null : "PART_VERIFICATION_FAILED",
                ErrorMessage = matches ? null : "fixture M3 Driving value differs from its post-edit geometry"
            };
        }

        private static List<string> M3GeometrySummary(FakeM3Sketch sketch)
        {
            List<string> result = new List<string>();
            foreach (FakeM3Geometry geometry in sketch.Geometries)
                result.Add(geometry.Id + ":" + M2Json.CanonicalText(M3GeometryView(geometry)));
            return result;
        }

        private static void ApplyM3Dimension(FakeM3Constraint constraint, double value)
        {
            FakeM3Geometry geometry = constraint.Geometry;
            if (constraint.Role == "diameter") geometry.Diameter = value;
            else if (constraint.Role == "width") geometry.Width = value;
            else if (constraint.Role == "height") geometry.Height = value;
            else if (constraint.Role == "length_x")
            {
                double sign = geometry.End[0] >= geometry.Start[0] ? 1.0 : -1.0;
                geometry.End[0] = geometry.Start[0] + sign * value;
            }
            else if (constraint.Role == "length_y")
            {
                double sign = geometry.End[1] >= geometry.Start[1] ? 1.0 : -1.0;
                geometry.End[1] = geometry.Start[1] + sign * value;
            }
            else if (constraint.Role == "center_x") geometry.Center[0] = geometry.Center[0] < 0.0 ? -value : value;
            else if (constraint.Role == "center_y") geometry.Center[1] = geometry.Center[1] < 0.0 ? -value : value;
        }

        private static string M3NumberText(double value)
        {
            return value.ToString("G17", CultureInfo.InvariantCulture);
        }

        public M2SaveCopyResult SaveCopy(M2ModelBinding binding, string targetPath, bool reopen)
        {
            System.Threading.Interlocked.Increment(ref saveCopyCalls);
            saveStarted.Set();
            if (System.Threading.Interlocked.CompareExchange(ref blockSave, 0, 0) != 0 && !releaseSave.WaitOne(15000))
                throw new M2ModelException("FIXTURE_GATE_TIMEOUT", "fixture save release gate timed out");
            string sourcePath = binding.PartPath;
            string normalizedTarget;
            try { normalizedTarget = System.IO.Path.GetFullPath(targetPath); }
            catch (Exception) { throw new M2ModelException("INVALID_SAVE_TARGET", "fixture target path is not an absolute path"); }
            if (!System.IO.Path.IsPathRooted(normalizedTarget) ||
                !String.Equals(System.IO.Path.GetExtension(normalizedTarget), ".prt", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(normalizedTarget, sourcePath, StringComparison.OrdinalIgnoreCase))
                throw new M2ModelException("INVALID_SAVE_TARGET", "fixture save target must be a distinct absolute .prt path");
            lock (savedTargets)
            {
                if (savedTargets.Contains(normalizedTarget))
                    throw new M2ModelException("TARGET_EXISTS", "fixture save target already exists and cannot be overwritten");
                savedTargets.Add(normalizedTarget);
            }

            int copyNumber = System.Threading.Interlocked.Increment(ref savedCopyNumber);
            string revision = RevisionId;
            string partTag = "fake-saved-part-tag-" + copyNumber.ToString();
            string value = CurrentValue;
            Dictionary<string, object> checks = BuildSaveChecks(binding, value);
            Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "source_path", sourcePath }, { "saved_path", normalizedTarget }, { "reopened", reopen },
                { "checks", checks },
                { "source_identity", SemanticIdentity(binding.PartPath, binding.PartTag, binding.RevisionId) },
                { "reopened_identity", SemanticIdentity(normalizedTarget, partTag, revision) }
            };

            bool unknown = System.Threading.Interlocked.Exchange(ref unknownNextSave, 0) != 0;
            if (unknown)
            {
                return new M2SaveCopyResult {
                    State = "UNKNOWN", PartPath = normalizedTarget, PartTag = partTag,
                    RevisionId = revision, Reopened = false, Checks = checks, Evidence = evidence,
                    ErrorCode = "SAVE_RESULT_UNKNOWN",
                    ErrorMessage = "fixture persisted the copy but could not confirm the result"
                };
            }

            if (reopen)
            {
                binding.PartPath = normalizedTarget;
                binding.PartTag = partTag;
                binding.RevisionId = revision;
                SetIdentity(binding.Identity, "part_path", normalizedTarget);
                SetIdentity(binding.Identity, "part_tag", partTag);
                SetIdentity(binding.Identity, "is_modified_before_read", false);
                SetIdentity(binding.Identity, "is_modified_after_read", false);
                SetIdentity(binding.Identity, "dirty_changed_by_read", false);
                activeBinding = binding;
            }
            return new M2SaveCopyResult {
                State = "SAVED", PartPath = normalizedTarget, PartTag = partTag,
                RevisionId = revision, Reopened = reopen, Checks = checks, Evidence = evidence
            };
        }

        private Dictionary<string, object> BuildSaveChecks(M2ModelBinding binding, string value)
        {
            Dictionary<string, object> expressionEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", "sketch-1" }, { "constraint_id", "constraint-1" },
                { "expression_name", "p11" }, { "expected_value", value },
                { "actual_rhs", value }, { "actual_value", value }, { "unit", "mm" }
            };
            Dictionary<string, object> geometryEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", "sketch-1" }, { "constraint_id", "constraint-1" },
                { "geometry_refs", new List<string> { "line-1", "line-2" } },
                { "expected_value", value }, { "actual_value", value },
                { "geometry", "line-1:end-x=" + value + "mm" }
            };
            Dictionary<string, object> historyEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", "sketch-1" }, { "feature_id", "feature-1" }, { "feature_type", "EXTRUDE" },
                { "parents", new List<string>() }, { "expressions", new List<string> { "p11" } }, { "feature_count", 1 }
            };
            Dictionary<string, object> associationEvidence = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "sketch_id", "sketch-1" }, { "constraint_id", "constraint-1" },
                { "dimension_state", "Driving" }, { "expression_name", "p11" },
                { "geometry_refs", new List<string> { "line-1", "line-2" } }
            };
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "expression", VerificationCheck(true, expressionEvidence) },
                { "geometry", VerificationCheck(true, geometryEvidence) },
                { "history", VerificationCheck(true, historyEvidence) },
                { "associations", VerificationCheck(true, associationEvidence) }
            };
        }

        private static Dictionary<string, object> VerificationCheck(bool verified, Dictionary<string, object> evidence)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "status", verified ? "VERIFIED" : "MISMATCH" }, { "evidence", evidence }
            };
        }

        private static Dictionary<string, object> SemanticIdentity(string partPath, string partTag, string revisionId)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "part_path", partPath }, { "part_tag", partTag }, { "revision_id", revisionId }
            };
        }

        private static void SetIdentity(Dictionary<string, object> identity, string key, object value)
        {
            if (identity == null) return;
            identity[key] = value;
        }

        private static Dictionary<string, object> ParameterEvidence(string value)
        {
            return new Dictionary<string, object> {
                { "expression_rhs", value }, { "value", value }, { "unit", "mm" }
            };
        }

        public void Release(M2ModelBinding binding)
        {
            if (binding != null) binding.AdapterState = null;
        }
    }
}
