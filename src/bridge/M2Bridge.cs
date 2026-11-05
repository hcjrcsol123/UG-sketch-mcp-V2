// M2 bridge service. This assembly intentionally has no NXOpen dependency.
// Diagnostics use cached CLR data or a CLR-only callback. Ownership/model commands
// are admitted under the lifecycle lock and serialized on the Start STA; the
// NXOpen implementation is supplied through IM2ModelAdapter by M2Entry.dll.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Ipc;
using System.Runtime.Serialization.Formatters;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NxSketchMcp.M2
{
    public interface IM2Bridge
    {
        string Invoke(string token, string envelopeJson);
    }

    public sealed class M2BridgeServer : MarshalByRefObject, IM2Bridge, IDisposable
    {
        private const int MaxEnvelopeChars = 16384;
        private const int MaxIdChars = 128;
        private const int MaxOperationIdChars = 128;
        private const int MaxNonceChars = 128;
        private const int DispatchTimeoutMs = 120000;
        private const int MaxOperations = 512;
        private const int MaxPreparedPlans = 256;

        private sealed class DispatchWork
        {
            internal Func<Dictionary<string, object>> Execute;
            internal TaskCompletionSource<Dictionary<string, object>> Done =
                new TaskCompletionSource<Dictionary<string, object>>();
            internal bool IsTakeover;
            internal bool IsReturn;
            internal bool IsModelRead;
            internal bool Started;
            internal volatile bool Abandoned;
            internal Action OnQueueFailure;
            internal OperationRecord Operation;
        }

        private sealed class OperationRecord
        {
            internal string Id;
            internal string Kind;
            internal string RequestKey;
            internal string State;
            internal bool Cancellable;
            internal Dictionary<string, object> Result;
            internal Dictionary<string, object> Error;
            internal Dictionary<string, object> LateResult;
            internal long StartedTimestamp;
            internal bool ExecutionCompleted;
            internal bool ModelCallStarted;
            internal DispatchWork Work;
        }

        private sealed class PreparedParameterPlan
        {
            internal readonly string Id;
            internal readonly string OwnershipId;
            internal readonly int BindingEpoch;
            internal readonly string SessionEpoch;
            internal readonly string LoadInstanceId;
            internal readonly string RevisionId;
            internal readonly string SketchId;
            internal readonly string ConstraintId;
            internal readonly string DimensionType;
            internal readonly string SketchFeatureId;
            internal readonly string ExpressionName;
            internal readonly string ExpressionRightHandSide;
            internal readonly string CurrentValue;
            internal readonly string Unit;
            internal readonly List<string> GeometryRefs;
            internal readonly M2ParameterPlanSnapshot AdapterSnapshot;
            internal readonly string RequestedValue;
            internal readonly string RequestedUnit;
            internal readonly Dictionary<string, object> PublicView;
            internal readonly M2ModelBinding Binding;
            internal bool Consumed;

            internal PreparedParameterPlan(string id, string ownershipId, int bindingEpoch,
                string sessionEpoch, string loadInstanceId, M2ModelBinding binding, M2ParameterPlanSnapshot adapterSnapshot,
                string requestedValue, string requestedUnit)
            {
                Id = id;
                OwnershipId = ownershipId;
                BindingEpoch = bindingEpoch;
                SessionEpoch = sessionEpoch;
                LoadInstanceId = loadInstanceId;
                Binding = binding;
                RevisionId = adapterSnapshot.RevisionId;
                SketchId = adapterSnapshot.SketchId;
                ConstraintId = adapterSnapshot.ConstraintId;
                DimensionType = adapterSnapshot.DimensionType;
                SketchFeatureId = adapterSnapshot.SketchFeatureId;
                ExpressionName = adapterSnapshot.ExpressionName;
                ExpressionRightHandSide = adapterSnapshot.ExpressionRightHandSide;
                CurrentValue = adapterSnapshot.CurrentValue;
                Unit = adapterSnapshot.Unit;
                GeometryRefs = adapterSnapshot.GeometryRefs == null
                    ? new List<string>() : new List<string>(adapterSnapshot.GeometryRefs);
                AdapterSnapshot = adapterSnapshot;
                RequestedValue = requestedValue;
                RequestedUnit = requestedUnit;
                PublicView = CreatePlanView(this);
            }
        }

        private sealed class PreparedM3Plan
        {
            internal readonly string Id;
            internal readonly string OwnershipId;
            internal readonly int BindingEpoch;
            internal readonly string SessionEpoch;
            internal readonly string LoadInstanceId;
            internal readonly M2ModelBinding Binding;
            internal readonly M3PlanSnapshot AdapterSnapshot;
            internal readonly Dictionary<string, object> PublicView;
            internal bool Consumed;

            internal PreparedM3Plan(string id, string ownershipId, int bindingEpoch,
                string sessionEpoch, string loadInstanceId, M2ModelBinding binding,
                M3PlanSnapshot adapterSnapshot)
            {
                Id = id;
                OwnershipId = ownershipId;
                BindingEpoch = bindingEpoch;
                SessionEpoch = sessionEpoch;
                LoadInstanceId = loadInstanceId;
                Binding = binding;
                AdapterSnapshot = adapterSnapshot;
                PublicView = adapterSnapshot.PublicView == null
                    ? new Dictionary<string, object>(StringComparer.Ordinal)
                    : Clone(adapterSnapshot.PublicView);
                PublicView["plan_id"] = id;
                PublicView["kind"] = adapterSnapshot.Kind;
                PublicView["revision_id"] = adapterSnapshot.RevisionId;
            }
        }

        private readonly object _lifecycle = new object();
        private readonly Queue<DispatchWork> _dispatchQueue = new Queue<DispatchWork>();
        private readonly Dictionary<string, OperationRecord> _operations =
            new Dictionary<string, OperationRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, PreparedParameterPlan> _plans =
            new Dictionary<string, PreparedParameterPlan>(StringComparer.Ordinal);
        private readonly Dictionary<string, PreparedM3Plan> _m3Plans =
            new Dictionary<string, PreparedM3Plan>(StringComparer.Ordinal);
        private readonly IM2ModelAdapter _modelAdapter;
        private Control _dispatcher;
        private IpcServerChannel _channel;
        private TaskCompletionSource<bool> _stopped = new TaskCompletionSource<bool>();
        private TaskCompletionSource<Dictionary<string, object>> _returnCompletion;
        private string _token;
        private string _tokenFilePath;
        private string _pipeName;
        private string _authorizedGroup;
        private string _sessionEpoch;
        private string _loadInstanceId;
        private string _ownershipId;
        private int _bindingEpoch;
        private int _mainThreadOsId;
        private int _processId;
        private int _inFlight;
        private bool _tokenFileAclVerified;
        private bool _exclusiveAddressUse;
        private bool _pumpPosted;
        private bool _acceptModelReads;
        private bool _writesHalted;
        private string _writeHaltReason;
        private string _ownershipState = "HUMAN_OWNED";
        private M2ModelBinding _binding;
        private volatile bool _started;
        private bool _everStarted;
        private volatile bool _stopping;

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        public M2BridgeServer() : this(null) { }

        public M2BridgeServer(IM2ModelAdapter modelAdapter)
        {
            _modelAdapter = modelAdapter;
        }

        public string PipeName { get { return _pipeName; } }
        public string TokenFilePath { get { return _tokenFilePath; } }
        public int MainThreadId { get { return _mainThreadOsId; } }
        public string AuthorizedGroup { get { return _authorizedGroup; } }
        public bool ExclusiveAddressUse { get { return _exclusiveAddressUse; } }
        public bool TokenFileAclVerified { get { return _tokenFileAclVerified; } }

        // Start captures only process/thread metadata. It does not call NXOpen or
        // query the active Session/Part; the first such access is an authorized takeover.
        public void Start(string pipeName, string tokenFilePath)
        {
            lock (_lifecycle)
            {
                if (_started || _everStarted) throw new InvalidOperationException("bridge instance has already been started");
                if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                    throw new InvalidOperationException("Start must run on an STA thread");
                if (String.IsNullOrEmpty(pipeName) || pipeName.Length > 200 || !IsSafePipeName(pipeName))
                    throw new ArgumentException("pipe name must be 1-200 ASCII letters, digits, dot, underscore, or hyphen", "pipeName");
                if (String.IsNullOrEmpty(tokenFilePath)) throw new ArgumentException("token file path is required", "tokenFilePath");

                _pipeName = pipeName;
                _tokenFilePath = Path.GetFullPath(tokenFilePath);
                _mainThreadOsId = unchecked((int)GetCurrentThreadId());
                Process process = Process.GetCurrentProcess();
                _processId = process.Id;
                _sessionEpoch = _processId.ToString(CultureInfo.InvariantCulture) + ":" +
                    process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                _loadInstanceId = Guid.NewGuid().ToString("N");

                _dispatcher = new Control();
                IntPtr handle = _dispatcher.Handle;
                if (!_dispatcher.IsHandleCreated || _mainThreadOsId == 0)
                    throw new InvalidOperationException("WinForms dispatcher handle was not created on the owner thread");
                int handleProcessId;
                uint handleThreadId = GetWindowThreadProcessId(handle, out handleProcessId);
                if (handleThreadId != unchecked((uint)_mainThreadOsId) || handleProcessId != _processId)
                    throw new InvalidOperationException("WinForms dispatcher is not owned by the Start thread");

                _token = CreatePrivateTokenFile(_tokenFilePath);
                _tokenFileAclVerified = true;
                try
                {
                    System.Collections.IDictionary options = new System.Collections.Hashtable();
                    options["name"] = "nx-sketch-mcp-m2-" + Guid.NewGuid().ToString("N");
                    options["portName"] = _pipeName;
                    _authorizedGroup = WindowsIdentity.GetCurrent().Name;
                    if (String.IsNullOrEmpty(_authorizedGroup)) throw new UnauthorizedAccessException("current account name is unavailable");
                    _exclusiveAddressUse = true;
                    options["exclusiveAddressUse"] = _exclusiveAddressUse;
                    options["authorizedGroup"] = _authorizedGroup;

                    BinaryServerFormatterSinkProvider formatter = new BinaryServerFormatterSinkProvider();
                    formatter.TypeFilterLevel = TypeFilterLevel.Low;
                    _channel = new IpcServerChannel(options, formatter);
                    ChannelServices.RegisterChannel(_channel, true);
                    RemotingServices.Marshal(this, "M2Bridge", typeof(M2BridgeServer));
                    _started = true;
                    _everStarted = true;
                }
                catch
                {
                    if (_channel != null)
                    {
                        try { ChannelServices.UnregisterChannel(_channel); } catch { }
                        _channel = null;
                    }
                    try { _dispatcher.Dispose(); } catch { }
                    _dispatcher = null;
                    DeleteTokenFile();
                    _token = null;
                    _tokenFileAclVerified = false;
                    _authorizedGroup = null;
                    _exclusiveAddressUse = false;
                    throw;
                }
            }
        }

        public override object InitializeLifetimeService()
        {
            return null;
        }

        public string Invoke(string token, string envelopeJson)
        {
            object requestId = null;
            try
            {
                if (envelopeJson == null || envelopeJson.Length == 0 || envelopeJson.Length > MaxEnvelopeChars)
                    throw new BridgeException("INVALID_JSON", "request size is invalid");

                Dictionary<string, object> root;
                try { root = M2Json.ParseObject(envelopeJson); }
                catch (Exception) { throw new BridgeException("INVALID_JSON", "request must be a strict JSON object"); }

                object idValue;
                if (root.TryGetValue("id", out idValue) && idValue is string && ((string)idValue).Length <= MaxIdChars)
                    requestId = idValue;

                if (!_started || _stopping)
                    throw new BridgeException("SERVICE_NOT_READY", "bridge service is not accepting requests");
                if (!FixedTimeEquals(_token, token))
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "invalid bridge token");
                RequireExactKeys(root, new string[] { "id", "command", "params" }, "request");
                if (!(requestId is string) || ((string)requestId).Length == 0)
                    throw new BridgeException("INVALID_REQUEST", "id must be a non-empty string of at most 128 characters");

                object commandValue;
                if (!root.TryGetValue("command", out commandValue) || !(commandValue is string))
                    throw new BridgeException("INVALID_REQUEST", "command must be a string");
                object paramsValue;
                if (!root.TryGetValue("params", out paramsValue) || !(paramsValue is Dictionary<string, object>))
                    throw new BridgeException("INVALID_REQUEST", "params must be an object");

                string command = (string)commandValue;
                Dictionary<string, object> args = (Dictionary<string, object>)paramsValue;
                if (String.Equals(command, "bridge.status", StringComparison.Ordinal))
                {
                    if (args.Count != 0) throw new BridgeException("INVALID_REQUEST", "bridge.status params must be empty");
                    return M2Json.ResultEnvelope(requestId, StatusResult());
                }
                if (String.Equals(command, "bridge.dispatch_probe", StringComparison.Ordinal))
                    return M2Json.ResultEnvelope(requestId, DispatchProbe(args));
                if (String.Equals(command, "ownership.takeover", StringComparison.Ordinal))
                    return Takeover(requestId, args);
                if (String.Equals(command, "ownership.return", StringComparison.Ordinal))
                    return ReturnOwnership(requestId, args);
                if (String.Equals(command, "model.get_session", StringComparison.Ordinal) ||
                    String.Equals(command, "model.inspect_part", StringComparison.Ordinal) ||
                    String.Equals(command, "model.inspect_sketch", StringComparison.Ordinal) ||
                    String.Equals(command, "model.query_faces", StringComparison.Ordinal))
                    return ModelRead(requestId, command, args);
                if (String.Equals(command, "operation.get", StringComparison.Ordinal))
                    return GetOperation(requestId, args);
                if (String.Equals(command, "operation.cancel", StringComparison.Ordinal))
                    return CancelOperation(requestId, args);
                if (String.Equals(command, "operation.prepare", StringComparison.Ordinal))
                    return PrepareParameterOperation(requestId, args);
                if (String.Equals(command, "operation.prepare_sketch", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "sketch");
                if (String.Equals(command, "operation.prepare_sketch_corner", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "sketch_corner");
                if (String.Equals(command, "operation.edit_sketch_corner", StringComparison.Ordinal))
                    return EditSketchCorner(requestId, args);
                if (String.Equals(command, "operation.prepare_sketch_offset", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "sketch_offset");
                if (String.Equals(command, "operation.edit_sketch_offset", StringComparison.Ordinal))
                    return EditSketchOffset(requestId, args);
                if (String.Equals(command, "operation.prepare_sketch_pattern", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "sketch_pattern");
                if (String.Equals(command, "operation.edit_sketch_pattern", StringComparison.Ordinal))
                    return EditSketchPattern(requestId, args);
                if (String.Equals(command, "operation.prepare_sketch_projection", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "sketch_projection");
                if (String.Equals(command, "operation.edit_sketch_projection", StringComparison.Ordinal))
                    return EditSketchProjection(requestId, args);
                if (String.Equals(command, "operation.prepare_sketch_reference_conversion", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "sketch_reference_conversion");
                if (String.Equals(command, "operation.edit_sketch_reference_conversion", StringComparison.Ordinal))
                    return EditSketchReferenceConversion(requestId, args);
                if (String.Equals(command, "operation.prepare_datum_plane", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "datum_plane");
                if (String.Equals(command, "operation.prepare_extrude", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "extrude");
                if (String.Equals(command, "operation.prepare_sweep", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "sweep");
                if (String.Equals(command, "operation.prepare_revolve", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "revolve");
                if (String.Equals(command, "operation.prepare_boolean", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "boolean");
                if (String.Equals(command, "operation.prepare_chamfer", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "chamfer");
                if (String.Equals(command, "operation.prepare_feature_pattern", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "feature_pattern");
                if (String.Equals(command, "operation.prepare_feature_mirror", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "feature_mirror");
                if (String.Equals(command, "operation.prepare_spatial_curve", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "spatial_curve");
                if (String.Equals(command, "operation.prepare_edge_blend", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "edge_blend");
                if (String.Equals(command, "operation.prepare_hole", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "hole");
                if (String.Equals(command, "operation.prepare_thread", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "thread");
                if (String.Equals(command, "operation.prepare_helical_groove", StringComparison.Ordinal))
                    return PrepareM3Operation(requestId, args, "helical_groove");
                if (String.Equals(command, "operation.execute_plan", StringComparison.Ordinal))
                    return ExecuteParameterPlan(requestId, args);
                if (String.Equals(command, "operation.verify_part", StringComparison.Ordinal))
                    return VerifyPart(requestId, args);
                if (String.Equals(command, "operation.save_copy", StringComparison.Ordinal))
                    return SaveCopy(requestId, args);

                throw new BridgeException("UNKNOWN_COMMAND", "command is not supported");
            }
            catch (BridgeException ex)
            {
                return M2Json.ErrorEnvelope(requestId, ex.Code, ex.Message);
            }
            catch (M2ModelException ex)
            {
                return M2Json.ErrorEnvelope(requestId, ex.Code, ex.Message);
            }
            catch (Exception)
            {
                return M2Json.ErrorEnvelope(requestId, "INTERNAL", "bridge request failed");
            }
        }

        private Dictionary<string, object> StatusResult()
        {
            // Cached CLR state only. This method never calls the model adapter.
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
                result.Add("ok", true);
                result.Add("service", "nx-sketch-mcp-m2");
                result.Add("state", _stopping ? "stopping" : "loaded");
                result.Add("pid", _processId);
                result.Add("main_thread_id", _mainThreadOsId.ToString(CultureInfo.InvariantCulture));
                result.Add("ownership_state", _ownershipState);
                result.Add("model_read_available", _ownershipState == "AI_OWNED" && _acceptModelReads);
                result.Add("model_write_enabled", !_writesHalted && _ownershipState == "AI_OWNED" &&
                    _binding != null && !_binding.IsReadOnly && (_modelAdapter is IM2OperationAdapter || _modelAdapter is IM3OperationAdapter));
                result.Add("model_write_adapter_available", _modelAdapter is IM2OperationAdapter || _modelAdapter is IM3OperationAdapter);
                result.Add("model_datum_plane_adapter_available", _modelAdapter is IM3DatumPlaneOperationAdapter);
                result.Add("model_sketch_offset_adapter_available", _modelAdapter is IM3SketchOffsetOperationAdapter);
                result.Add("model_sketch_offset_write_enabled", _modelAdapter is IM3SketchOffsetOperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_sketch_pattern_adapter_available", _modelAdapter is IM3SketchPatternOperationAdapter);
                result.Add("model_sketch_pattern_write_enabled", _modelAdapter is IM3SketchPatternOperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_sketch_projection_adapter_available", _modelAdapter is IM3SketchProjectionOperationAdapter);
                result.Add("model_sketch_projection_write_enabled", _modelAdapter is IM3SketchProjectionOperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_sketch_reference_conversion_adapter_available", _modelAdapter is IM3SketchReferenceConversionOperationAdapter);
                result.Add("model_sketch_reference_conversion_write_enabled", _modelAdapter is IM3SketchReferenceConversionOperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_sweep_adapter_available", _modelAdapter is IM3SweepOperationAdapter);
                result.Add("model_sweep_write_enabled", _modelAdapter is IM3SweepOperationAdapter && _modelAdapter is IM3OperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_revolve_adapter_available", _modelAdapter is IM3RevolveOperationAdapter);
                result.Add("model_boolean_adapter_available", _modelAdapter is IM3BooleanOperationAdapter);
                result.Add("model_boolean_write_enabled", _modelAdapter is IM3BooleanOperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_chamfer_adapter_available", _modelAdapter is IM3ChamferOperationAdapter);
                result.Add("model_chamfer_write_enabled", _modelAdapter is IM3ChamferOperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_feature_pattern_adapter_available", _modelAdapter is IM3FeaturePatternOperationAdapter);
                result.Add("model_feature_pattern_write_enabled", _modelAdapter is IM3FeaturePatternOperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_feature_mirror_adapter_available", _modelAdapter is IM3FeatureMirrorOperationAdapter);
                result.Add("model_spatial_curve_adapter_available", _modelAdapter is IM3SpatialCurveOperationAdapter);
                result.Add("model_spatial_curve_write_enabled", _modelAdapter is IM3SpatialCurveOperationAdapter && _modelAdapter is IM3OperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_feature_mirror_write_enabled", _modelAdapter is IM3FeatureMirrorOperationAdapter &&
                    _modelAdapter is IM3OperationAdapter && _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_edge_blend_adapter_available", _modelAdapter is IM3EdgeBlendOperationAdapter);
                result.Add("model_edge_blend_write_enabled", _modelAdapter is IM3EdgeBlendOperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_hole_adapter_available", _modelAdapter is IM3HoleOperationAdapter);
                result.Add("model_hole_write_enabled", _modelAdapter is IM3HoleOperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_revolve_write_enabled", _modelAdapter is IM3RevolveOperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_helical_adapter_available", _modelAdapter is IM7HelicalOperationAdapter);
                result.Add("model_helical_write_enabled", _modelAdapter is IM7HelicalOperationAdapter &&
                    _ownershipState == "AI_OWNED" && !_writesHalted && _binding != null && !_binding.IsReadOnly);
                result.Add("model_save_copy_available", _modelAdapter is IM2M27ModelAdapter);
                result.Add("writes_halted", _writesHalted);
                result.Add("write_halt_reason", _writeHaltReason);
                result.Add("active_operations", ActiveOperationCountLocked());
                result.Add("in_flight", _inFlight.ToString(CultureInfo.InvariantCulture));
                result.Add("binding_epoch", _bindingEpoch);
                // The token and Windows user-scoped IPC already authorize this caller.
                // Returning the cached owner id lets a fresh helper recover after a
                // takeover response was lost and explicitly return ownership.
                result.Add("ownership_id", _ownershipId);
                result.Add("binding_identity", _binding == null ? null : Clone(_binding.Identity));
                result.Add("session_epoch", _sessionEpoch);
                result.Add("load_instance_id", _loadInstanceId);
                result.Add("nx_api_calls", 0);
                return result;
            }
        }

        private Dictionary<string, object> DispatchProbe(Dictionary<string, object> args)
        {
            RequireExactKeys(args, new string[] { "nonce" }, "bridge.dispatch_probe params");
            string nonce = RequiredString(args, "nonce");
            if (CountCodePoints(nonce) == 0 || CountCodePoints(nonce) > MaxNonceChars)
                throw new BridgeException("INVALID_REQUEST", "nonce must be a non-empty string of at most 128 Unicode code points");

            int requestThreadId = unchecked((int)GetCurrentThreadId());
            Dictionary<string, object> result = WaitForDispatch(new DispatchWork
            {
                Execute = delegate
                {
                    int callbackThreadId = unchecked((int)GetCurrentThreadId());
                    Dictionary<string, object> probe = new Dictionary<string, object>(StringComparer.Ordinal);
                    probe.Add("ok", true);
                    probe.Add("nonce", nonce);
                    probe.Add("request_thread_id", requestThreadId.ToString(CultureInfo.InvariantCulture));
                    probe.Add("callback_thread_id", callbackThreadId.ToString(CultureInfo.InvariantCulture));
                    probe.Add("main_thread_id", _mainThreadOsId.ToString(CultureInfo.InvariantCulture));
                    probe.Add("on_main_thread", callbackThreadId == _mainThreadOsId);
                    probe.Add("nx_api_calls", 0);
                    return probe;
                }
            }, "DISPATCH_FAILED");
            return result;
        }

        private string Takeover(object requestId, Dictionary<string, object> args)
        {
            RequireExactKeys(args, new string[] { "expected_pid", "expected_part_path", "artifact_scope", "authorize" }, "ownership.takeover params");
            int expectedPid;
            if (!M2Json.TryGetInt(args, "expected_pid", out expectedPid) || expectedPid <= 0)
                throw new BridgeException("INVALID_REQUEST", "expected_pid must be a positive integer");
            object expectedPathValue;
            if (!args.TryGetValue("expected_part_path", out expectedPathValue) || !(expectedPathValue is string))
                throw new BridgeException("INVALID_REQUEST", "expected_part_path must be a string");
            string expectedPath = (string)expectedPathValue;
            if (expectedPath.Length > 4096)
                throw new BridgeException("INVALID_REQUEST", "expected_part_path must not exceed 4096 characters");
            string artifactScope = RequiredString(args, "artifact_scope");
            if (!String.Equals(artifactScope, "saved_independent_copy", StringComparison.Ordinal) &&
                !String.Equals(artifactScope, "current_work_part", StringComparison.Ordinal))
                throw new BridgeException("INVALID_REQUEST", "artifact_scope must be saved_independent_copy or current_work_part");
            if (String.IsNullOrWhiteSpace(expectedPath) && !String.Equals(artifactScope, "current_work_part", StringComparison.Ordinal))
                throw new BridgeException("INVALID_REQUEST", "expected_part_path may be empty only for current_work_part");
            object authorize;
            if (!args.TryGetValue("authorize", out authorize) || !(authorize is bool) || !(bool)authorize)
                throw new BridgeException("AUTHORIZATION_REQUIRED", "authorize must be explicitly true");
            if (_modelAdapter == null) throw new BridgeException("MODEL_ADAPTER_UNAVAILABLE", "NX model adapter is not loaded");

            DispatchWork work = new DispatchWork { IsTakeover = true };
            lock (_lifecycle)
            {
                if (_stopping) throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                if (_writesHalted) throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "ownership takeover is blocked because an operation outcome is unknown");
                if (_ownershipState != "HUMAN_OWNED")
                    throw new BridgeException("OWNERSHIP_BUSY", "NX session is not in HUMAN_OWNED state");
                if (expectedPid != _processId)
                    throw new BridgeException("SESSION_MISMATCH", "expected_pid does not match the loaded NX process");
                _ownershipState = "TAKEOVER_CHECK";
                _acceptModelReads = false;
                _bindingEpoch++;
                int bindingEpoch = _bindingEpoch;
                string sessionEpoch = _sessionEpoch;
                string loadInstanceId = _loadInstanceId;
                work.Execute = delegate
                {
                    try
                    {
                        M2ModelBinding binding = _modelAdapter.Takeover(expectedPath, artifactScope, sessionEpoch, loadInstanceId, bindingEpoch);
                        if (binding == null) throw new M2ModelException("MODEL_READ_FAILED", "NX adapter returned no model binding");
                        if (!String.Equals(binding.ArtifactScope, artifactScope, StringComparison.Ordinal) ||
                            !String.Equals(binding.ArtifactKind, "standalone_part", StringComparison.Ordinal))
                        {
                            try { _modelAdapter.Release(binding); } catch { }
                            throw new M2ModelException("UNSUPPORTED_ARTIFACT_SCOPE", "NX adapter did not confirm the requested scope as a standalone part");
                        }
                        if (work.Abandoned)
                        {
                            try { _modelAdapter.Release(binding); } catch { }
                            lock (_lifecycle) { if (_ownershipState == "TAKEOVER_CHECK") _ownershipState = "HUMAN_OWNED"; }
                            throw new BridgeException("TAKEOVER_CANCELLED", "takeover caller timed out before ownership was granted");
                        }
                        string owner = Guid.NewGuid().ToString("N");
                        lock (_lifecycle)
                        {
                            if (_writesHalted)
                            {
                                try { _modelAdapter.Release(binding); } catch { }
                                _ownershipState = "UNKNOWN_FROZEN";
                                throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "takeover completed after the bridge entered a frozen state");
                            }
                            if (_stopping || work.Abandoned || _ownershipState != "TAKEOVER_CHECK")
                            {
                                try { _modelAdapter.Release(binding); } catch { }
                                _ownershipState = "HUMAN_OWNED";
                                throw new BridgeException("TAKEOVER_CANCELLED", "takeover could not be completed");
                            }
                            _binding = binding;
                            _ownershipId = owner;
                            _bindingEpoch = bindingEpoch;
                            _ownershipState = "AI_OWNED";
                            _acceptModelReads = true;
                        }
                        Dictionary<string, object> result = Clone(binding.Identity);
                        result["ok"] = true;
                        result["ownership_state"] = "AI_OWNED";
                        result["ownership_id"] = owner;
                        result["binding_epoch"] = bindingEpoch;
                        result["session_epoch"] = sessionEpoch;
                        result["load_instance_id"] = loadInstanceId;
                        result["revision_id"] = binding.RevisionId;
                        result["artifact_scope"] = binding.ArtifactScope;
                        result["artifact_kind"] = binding.ArtifactKind;
                        result["artifact_assessment"] = Clone(binding.ArtifactAssessment);
                        return result;
                    }
                    catch
                    {
                        lock (_lifecycle)
                        {
                            if (_ownershipState == "TAKEOVER_CHECK")
                            {
                                _ownershipState = "HUMAN_OWNED";
                                _acceptModelReads = false;
                            }
                        }
                        throw;
                    }
                };
            }

            Dictionary<string, object> takeover = WaitForDispatch(work, "DISPATCH_TIMEOUT");
            return M2Json.ResultEnvelope(requestId, takeover);
        }

        private string ReturnOwnership(object requestId, Dictionary<string, object> args)
        {
            RequireExactKeys(args, new string[] { "ownership_id", "binding_epoch" }, "ownership.return params");
            string owner = RequiredString(args, "ownership_id");
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            TaskCompletionSource<Dictionary<string, object>> completion;

            lock (_lifecycle)
            {
                if (_writesHalted)
                    throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "ownership cannot be returned while an operation outcome is unknown");
                if (ActiveSaveCopyLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "a save-copy operation is queued or running; return ownership after querying its operation_id");
                if (String.Equals(_ownershipState, "RETURNING", StringComparison.Ordinal))
                {
                    if (!String.Equals(owner, _ownershipId, StringComparison.Ordinal) || epoch != _bindingEpoch)
                        throw new BridgeException("AUTHORIZATION_REQUIRED", "ownership id or binding epoch is stale");
                    completion = _returnCompletion;
                }
                else
                {
                    if (_ownershipState != "AI_OWNED" || !String.Equals(owner, _ownershipId, StringComparison.Ordinal) || epoch != _bindingEpoch)
                        throw new BridgeException("AUTHORIZATION_REQUIRED", "ownership id or binding epoch is stale");

                    _ownershipState = "RETURNING";
                    _acceptModelReads = false;
                    completion = new TaskCompletionSource<Dictionary<string, object>>();
                    _returnCompletion = completion;
                    M2ModelBinding oldBinding = _binding;
                    DispatchWork work = new DispatchWork { IsReturn = true };
                    work.Execute = delegate
                    {
                        if (oldBinding != null && _modelAdapter != null)
                        {
                            try { _modelAdapter.Release(oldBinding); }
                            catch (Exception) { /* Clearing the local reference is best effort; no model API is called. */ }
                        }
                        Dictionary<string, object> result;
                        lock (_lifecycle)
                        {
                            if (String.Equals(owner, _ownershipId, StringComparison.Ordinal))
                            {
                                _binding = null;
                                _ownershipId = null;
                                _bindingEpoch++;
                                _ownershipState = "HUMAN_OWNED";
                                _acceptModelReads = false;
                            }
                            result = OwnershipResultLocked("RETURNED");
                        }
                        completion.TrySetResult(result);
                        return result;
                    };
                    work.Done.Task.ContinueWith(delegate(Task<Dictionary<string, object>> task)
                    {
                        if (task.IsFaulted) completion.TrySetException(task.Exception.InnerException ?? task.Exception);
                        else if (task.IsCanceled) completion.TrySetCanceled();
                    });
                    QueueDispatchLocked(work);
                }
            }

            if (Task.WaitAny(new Task[] { completion.Task }, DispatchTimeoutMs) < 0)
                throw new BridgeException("RETURN_IN_PROGRESS", "ownership return is draining accepted requests; retry ownership.return or read bridge.status");
            if (completion.Task.IsFaulted)
                throw new BridgeException("RETURN_FAILED", "ownership return did not complete");
            return M2Json.ResultEnvelope(requestId, completion.Task.Result);
        }

        private string ModelRead(object requestId, string command, Dictionary<string, object> args)
        {
            bool queryFaces = String.Equals(command, "model.query_faces", StringComparison.Ordinal);
            string[] allowed = String.Equals(command, "model.inspect_sketch", StringComparison.Ordinal)
                ? new string[] { "ownership_id", "binding_epoch", "sketch_id" }
                : (queryFaces
                    ? new string[] { "ownership_id", "binding_epoch", "revision_id", "selector", "body_id" }
                    : new string[] { "ownership_id", "binding_epoch" });
            if (queryFaces)
            {
                foreach (string key in args.Keys)
                    if (Array.IndexOf(allowed, key) < 0)
                        throw new BridgeException("INVALID_REQUEST", command + " params contains an unsupported field");
                string[] required = new string[] { "ownership_id", "binding_epoch", "revision_id", "selector" };
                foreach (string key in required)
                    if (!args.ContainsKey(key)) throw new BridgeException("INVALID_REQUEST", command + " params is missing " + key);
            }
            else RequireExactKeys(args, allowed, command + " params");
            string owner = RequiredString(args, "ownership_id");
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            string sketchId = String.Equals(command, "model.inspect_sketch", StringComparison.Ordinal)
                ? RequiredString(args, "sketch_id") : null;
            string revisionId = queryFaces ? RequiredString(args, "revision_id") : null;
            string selector = queryFaces ? NormalizeRequiredString(RequiredString(args, "selector"), "selector", 256) : null;
            string bodyId = null;
            object rawBodyId;
            if (queryFaces && args.TryGetValue("body_id", out rawBodyId))
                bodyId = NormalizeRequiredString(rawBodyId as string, "body_id", MaxOperationIdChars);
            M2ModelBinding binding;
            DispatchWork work = new DispatchWork { IsModelRead = true };
            lock (_lifecycle)
            {
                if (ActiveSaveCopyLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "model reads are paused while the active Work Part is being saved and rebound");
                if (_writesHalted || _ownershipState != "AI_OWNED" || !_acceptModelReads ||
                    !String.Equals(owner, _ownershipId, StringComparison.Ordinal) || epoch != _bindingEpoch || _binding == null)
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "model reads require the current AI ownership and binding epoch");
                binding = _binding;
                _inFlight++;
                work.OnQueueFailure = delegate { lock (_lifecycle) { if (_inFlight > 0) _inFlight--; } };
                work.Execute = delegate
                {
                    try
                    {
                        RequireCurrentBinding(binding);
                        Dictionary<string, object> result;
                        if (String.Equals(command, "model.get_session", StringComparison.Ordinal))
                            result = _modelAdapter.GetSession(binding);
                        else if (String.Equals(command, "model.inspect_part", StringComparison.Ordinal))
                            result = _modelAdapter.InspectPart(binding);
                        else if (String.Equals(command, "model.inspect_sketch", StringComparison.Ordinal))
                            result = _modelAdapter.InspectSketch(binding, sketchId);
                        else
                        {
                            if (!String.Equals(revisionId, binding.RevisionId, StringComparison.Ordinal))
                                throw new BridgeException("STALE_REVISION", "revision_id does not match the current captured model revision");
                            IM4FaceQueryAdapter faceAdapter = _modelAdapter as IM4FaceQueryAdapter;
                            if (faceAdapter == null)
                                throw new BridgeException("MODEL_ADAPTER_UNAVAILABLE", "NX M4 face query adapter is not loaded");
                            result = faceAdapter.QueryFaces(binding, revisionId, selector, bodyId);
                        }
                        RequireCurrentBinding(binding);
                        if (result == null) throw new M2ModelException("MODEL_READ_FAILED", "NX adapter returned no inspection result");
                        lock (_lifecycle)
                        {
                            if (!Object.ReferenceEquals(_binding, binding) || !String.Equals(_ownershipId, owner, StringComparison.Ordinal))
                                throw new BridgeException("STALE_BINDING", "ownership or model binding changed during the read");
                            result = Clone(result);
                            result["ok"] = true;
                            result["ownership_state"] = _ownershipState;
                            result["ownership_id"] = owner;
                            result["binding_epoch"] = epoch;
                            result["session_epoch"] = binding.SessionEpoch;
                            result["load_instance_id"] = binding.LoadInstanceId;
                            result["revision_id"] = binding.RevisionId;
                            return result;
                        }
                    }
                    catch (BridgeException) { throw; }
                    catch (M2ModelException) { throw; }
                    catch (Exception ex)
                    {
                        throw new M2ModelException("MODEL_READ_FAILED", "NX model read failed (" + ex.GetType().Name + ")");
                    }
                    finally
                    {
                        lock (_lifecycle) { if (_inFlight > 0) _inFlight--; }
                    }
                };
                QueueDispatchLocked(work);
            }

            Dictionary<string, object> resultRead = AwaitDispatch(work, "MODEL_READ_TIMEOUT");
            return M2Json.ResultEnvelope(requestId, resultRead);
        }

        private string GetOperation(object requestId, Dictionary<string, object> args)
        {
            RequireExactKeys(args, new string[] { "operation_id" }, "operation.get params");
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            Dictionary<string, object> result;
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                OperationRecord operation;
                if (_operations.TryGetValue(operationId, out operation))
                    result = OperationResultLocked(operation);
                else
                {
                    FreezeWritesLocked("OPERATION_NOT_FOUND", null);
                    result = UnknownOperationResultLocked(operationId);
                }
            }
            return M2Json.ResultEnvelope(requestId, result);
        }

        private string CancelOperation(object requestId, Dictionary<string, object> args)
        {
            RequireExactKeys(args, new string[] { "operation_id" }, "operation.cancel params");
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            Dictionary<string, object> result;
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                OperationRecord operation;
                if (!_operations.TryGetValue(operationId, out operation))
                {
                    FreezeWritesLocked("OPERATION_NOT_FOUND", null);
                    result = UnknownOperationResultLocked(operationId);
                }
                else
                {
                    if (String.Equals(operation.State, "QUEUED", StringComparison.Ordinal) && operation.Work != null &&
                        RemoveDispatchWorkLocked(operation.Work))
                    {
                        operation.State = "CANCELLED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "cancelled_before_start", true }
                        };
                        operation.Error = null;
                        DispatchWork cancelledWork = operation.Work;
                        operation.Work = null;
                        cancelledWork.Done.TrySetException(new BridgeException("OPERATION_CANCELLED", "operation was cancelled before NX execution started"));
                    }
                    result = OperationResultLocked(operation);
                }
            }
            return M2Json.ResultEnvelope(requestId, result);
        }

        private string PrepareParameterOperation(object requestId, Dictionary<string, object> args)
        {
            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id", "constraint_id", "value", "unit" },
                "operation.prepare params");
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            string owner = NormalizeRequiredString(RequiredString(args, "ownership_id"), "ownership_id", MaxOperationIdChars);
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            string revisionId = NormalizeRequiredString(RequiredString(args, "revision_id"), "revision_id", 128);
            string sketchId = NormalizeRequiredString(RequiredString(args, "sketch_id"), "sketch_id", 4096);
            string constraintId = NormalizeRequiredString(RequiredString(args, "constraint_id"), "constraint_id", 4096);
            string valueText = NormalizeRequiredString(RequiredString(args, "value"), "value", 128);
            string unit = NormalizeRequiredString(RequiredString(args, "unit"), "unit", 64);
            string normalizedValue = NormalizeFiniteDecimal(valueText, "value");

            Dictionary<string, object> normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            normalized.Add("ownership_id", owner);
            normalized.Add("binding_epoch", epoch);
            normalized.Add("revision_id", revisionId);
            normalized.Add("sketch_id", sketchId);
            normalized.Add("constraint_id", constraintId);
            normalized.Add("value", normalizedValue);
            normalized.Add("unit", unit);
            string requestKey = OperationRequestKey("prepare", normalized);
            OperationRecord operation;
            DispatchWork work;
            M2ModelBinding binding;
            M2ParameterEditRequest request = new M2ParameterEditRequest();
            request.RevisionId = revisionId;
            request.SketchId = sketchId;
            request.ConstraintId = constraintId;
            request.ValueText = valueText;
            request.Unit = unit;

            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                if (_operations.TryGetValue(operationId, out operation))
                {
                    if (!String.Equals(operation.RequestKey, requestKey, StringComparison.Ordinal))
                        throw new BridgeException("OPERATION_ID_CONFLICT", "operation_id was already used with a different normalized request");
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
                }
                if (_stopping) throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                if (ActiveSaveCopyLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "operation preparation is paused while the active Work Part is being saved and rebound");
                if (_ownershipState != "AI_OWNED" || !_acceptModelReads || !String.Equals(owner, _ownershipId, StringComparison.Ordinal) ||
                    epoch != _bindingEpoch || _binding == null)
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "operation preparation requires current AI ownership and binding epoch");
                if (!(_modelAdapter is IM2OperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX parameter-edit adapter is not loaded");
                if (_operations.Count >= MaxOperations)
                    throw new BridgeException("OPERATION_LIMIT_REACHED", "operation record capacity is full for this bridge load");
                binding = _binding;
                operation = new OperationRecord();
                operation.Id = operationId;
                operation.Kind = "prepare_parameter_edit";
                operation.RequestKey = requestKey;
                operation.State = "QUEUED";
                operation.Cancellable = true;
                _operations.Add(operationId, operation);

                work = new DispatchWork();
                work.Operation = operation;
                work.Execute = delegate { return PerformPrepareParameterOperation(operation, binding, request, owner); };
                operation.Work = work;
                QueueDispatchLocked(work);
            }

            Task.WaitAny(new Task[] { work.Done.Task }, DispatchTimeoutMs);
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
            }
        }

        private string PrepareM3Operation(object requestId, Dictionary<string, object> args, string kind)
        {
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            string owner = NormalizeRequiredString(RequiredString(args, "ownership_id"), "ownership_id", MaxOperationIdChars);
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            string revisionId = NormalizeRequiredString(RequiredString(args, "revision_id"), "revision_id", 128);
            Dictionary<string, object> normalized = null;
            M3SketchRequest sketchRequest = null;
            M3SketchCornerRequest sketchCornerRequest = null;
            M3SketchOffsetRequest sketchOffsetRequest = null;
            M3SketchPatternRequest sketchPatternRequest = null;
            M3SketchProjectionRequest sketchProjectionRequest = null;
            M3SketchReferenceConversionRequest sketchReferenceConversionRequest = null;
            M3DatumPlaneRequest datumPlaneRequest = null;
            M3ExtrudeRequest extrudeRequest = null;
            M3RevolveRequest revolveRequest = null;
            M3SweepRequest sweepRequest = null;
            M3HoleRequest holeRequest = null;
            M3BooleanRequest booleanRequest = null;
            M3EdgeBlendRequest edgeBlendRequest = null;
            M3ChamferRequest chamferRequest = null;
            M3FeaturePatternRequest featurePatternRequest = null;
            M3FeatureMirrorRequest featureMirrorRequest = null;
            M3SpatialCurveRequest spatialCurveRequest = null;
            M7ThreadRequest threadRequest = null;
            M7HelicalGrooveRequest grooveRequest = null;
            if (kind == "sketch") sketchRequest = ParseM3SketchRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "sketch_corner") sketchCornerRequest = ParseM3SketchCornerRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "sketch_offset") sketchOffsetRequest = ParseM3SketchOffsetRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "sketch_pattern") sketchPatternRequest = ParseM3SketchPatternRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "sketch_projection") sketchProjectionRequest = ParseM3SketchProjectionRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "sketch_reference_conversion") sketchReferenceConversionRequest = ParseM3SketchReferenceConversionRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "datum_plane") datumPlaneRequest = ParseM3DatumPlaneRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "extrude") extrudeRequest = ParseM3ExtrudeRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "revolve") revolveRequest = ParseM3RevolveRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "sweep") sweepRequest = ParseM3SweepRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "boolean") booleanRequest = ParseM3BooleanRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "chamfer") chamferRequest = ParseM3ChamferRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "feature_pattern") featurePatternRequest = ParseM3FeaturePatternRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "feature_mirror") featureMirrorRequest = ParseM3FeatureMirrorRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "spatial_curve") spatialCurveRequest = ParseM3SpatialCurveRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "edge_blend") edgeBlendRequest = ParseM3EdgeBlendRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "hole") holeRequest = ParseM3HoleRequest(args, revisionId, owner, epoch, out normalized);
            else if (kind == "thread") threadRequest = ParseM7ThreadRequest(args, revisionId, owner, epoch, out normalized);
            else grooveRequest = ParseM7GrooveRequest(args, revisionId, owner, epoch, out normalized);
            string requestKey = OperationRequestKey("prepare_" + kind, normalized);

            OperationRecord operation;
            DispatchWork work;
            M2ModelBinding binding;
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                if (_operations.TryGetValue(operationId, out operation))
                {
                    if (!String.Equals(operation.RequestKey, requestKey, StringComparison.Ordinal))
                        throw new BridgeException("OPERATION_ID_CONFLICT", "operation_id was already used with a different normalized request");
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
                }
                if (_stopping) throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                if (_writesHalted) throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "model writes are halted until the unknown operation outcome is resolved");
                if (ActiveSaveCopyLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "plan preparation is paused while the active Work Part is being saved and rebound");
                if (_ownershipState != "AI_OWNED" || !_acceptModelReads || !String.Equals(owner, _ownershipId, StringComparison.Ordinal) ||
                    epoch != _bindingEpoch || _binding == null)
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "M3 plan preparation requires current AI ownership and binding epoch");
                if (kind == "datum_plane" && !(_modelAdapter is IM3DatumPlaneOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX datum plane adapter is not loaded");
                if (kind == "sketch_corner" && !(_modelAdapter is IM3SketchCornerOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch corner adapter is not loaded");
                if (kind == "sketch_offset" && !(_modelAdapter is IM3SketchOffsetOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch offset adapter is not loaded");
                if (kind == "sketch_pattern" && !(_modelAdapter is IM3SketchPatternOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch pattern adapter is not loaded");
                if (kind == "sketch_projection" && !(_modelAdapter is IM3SketchProjectionOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch projection adapter is not loaded");
                if (kind == "sketch_reference_conversion" && !(_modelAdapter is IM3SketchReferenceConversionOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch reference conversion adapter is not loaded");
                if (kind == "sweep" && !(_modelAdapter is IM3SweepOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sweep adapter is not loaded");
                if (kind == "revolve" && !(_modelAdapter is IM3RevolveOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX revolve adapter is not loaded");
                if (kind == "boolean" && !(_modelAdapter is IM3BooleanOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX boolean adapter is not loaded");
                if (kind == "chamfer" && !(_modelAdapter is IM3ChamferOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX chamfer adapter is not loaded");
                if (kind == "feature_pattern" && !(_modelAdapter is IM3FeaturePatternOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX feature pattern adapter is not loaded");
                if (kind == "feature_mirror" && !(_modelAdapter is IM3FeatureMirrorOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX feature mirror adapter is not loaded");
                if (kind == "spatial_curve" && !(_modelAdapter is IM3SpatialCurveOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX spatial curve adapter is not loaded");
                if (kind == "edge_blend" && !(_modelAdapter is IM3EdgeBlendOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX edge blend adapter is not loaded");
                if (kind == "hole" && !(_modelAdapter is IM3HoleOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX hole adapter is not loaded");
                if (kind != "datum_plane" && !(_modelAdapter is IM3OperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX M3 sketch/extrude adapter is not loaded");
                if ((kind == "thread" || kind == "helical_groove") && !(_modelAdapter is IM7HelicalOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX native thread/helical groove adapter is not loaded");
                if (_operations.Count >= MaxOperations)
                    throw new BridgeException("OPERATION_LIMIT_REACHED", "operation record capacity is full for this bridge load");
                if (_plans.Count + _m3Plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                binding = _binding;
                operation = new OperationRecord();
                operation.Id = operationId;
                operation.Kind = "prepare_m3_" + kind;
                operation.RequestKey = requestKey;
                operation.State = "QUEUED";
                operation.Cancellable = true;
                _operations.Add(operationId, operation);
                work = new DispatchWork();
                work.Operation = operation;
                work.Execute = delegate { return PerformPrepareM3Operation(operation, binding, owner, sketchRequest, sketchCornerRequest, sketchOffsetRequest, sketchPatternRequest, sketchProjectionRequest, sketchReferenceConversionRequest, datumPlaneRequest, extrudeRequest, revolveRequest, sweepRequest, holeRequest, booleanRequest, edgeBlendRequest, chamferRequest, featurePatternRequest, featureMirrorRequest, spatialCurveRequest, threadRequest, grooveRequest); };
                operation.Work = work;
                QueueDispatchLocked(work);
                // Sweep preparation can require substantial native readback as
                // prior Swept features accumulate. The record is registered and
                // queued under this lock; return its real pending state now.
                if (kind == "sweep") return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
            }

            Task.WaitAny(new Task[] { work.Done.Task }, DispatchTimeoutMs);
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
            }
        }

        private string EditSketchCorner(object requestId, Dictionary<string, object> args)
        {
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            string owner = NormalizeRequiredString(RequiredString(args, "ownership_id"), "ownership_id", MaxOperationIdChars);
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            string revisionId = NormalizeRequiredString(RequiredString(args, "revision_id"), "revision_id", 128);
            Dictionary<string, object> normalized;
            M3SketchCornerRequest request = ParseM3SketchCornerRequest(args, revisionId, owner, epoch, out normalized);
            string requestKey = OperationRequestKey("edit_sketch_corner", normalized);
            OperationRecord operation;
            DispatchWork work;
            M2ModelBinding binding;
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                if (_operations.TryGetValue(operationId, out operation))
                {
                    if (!String.Equals(operation.RequestKey, requestKey, StringComparison.Ordinal))
                        throw new BridgeException("OPERATION_ID_CONFLICT", "operation_id was already used with a different normalized request");
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
                }
                if (_stopping) throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                if (_writesHalted)
                    throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "model writes are halted until the unknown operation outcome is resolved");
                if (ActiveSaveCopyLocked() || ActiveParameterExecutionLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "another model mutation or save is queued or running; query its operation_id before starting F07");
                if (_ownershipState != "AI_OWNED" || !_acceptModelReads || !String.Equals(owner, _ownershipId, StringComparison.Ordinal) ||
                    epoch != _bindingEpoch || _binding == null)
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "sketch corner edit requires current AI ownership and binding epoch");
                if (!(_modelAdapter is IM3OperationAdapter) || !(_modelAdapter is IM3SketchCornerOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch corner adapter is not loaded");
                if (_operations.Count >= MaxOperations)
                    throw new BridgeException("OPERATION_LIMIT_REACHED", "operation record capacity is full for this bridge load");
                if (_plans.Count + _m3Plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                binding = _binding;
                operation = new OperationRecord();
                operation.Id = operationId;
                // The execute_ prefix inherits timeout freezing and write serialization.
                operation.Kind = "execute_edit_sketch_corner";
                operation.RequestKey = requestKey;
                operation.State = "QUEUED";
                operation.Cancellable = true;
                _operations.Add(operationId, operation);
                work = new DispatchWork();
                work.Operation = operation;
                work.Execute = delegate { return PerformEditSketchCornerOperation(operation, binding, owner, request); };
                operation.Work = work;
                QueueDispatchLocked(work);
            }

            Task.WaitAny(new Task[] { work.Done.Task }, DispatchTimeoutMs);
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
            }
        }

        private Dictionary<string, object> PerformEditSketchCornerOperation(OperationRecord operation,
            M2ModelBinding binding, string owner, M3SketchCornerRequest request)
        {
            RequireCurrentBinding(binding);
            if (!String.Equals(binding.RevisionId, request.RevisionId, StringComparison.Ordinal))
                throw new BridgeException("STALE_REVISION", "revision_id does not match the current captured model revision");
            IM3OperationAdapter adapter = _modelAdapter as IM3OperationAdapter;
            IM3SketchCornerOperationAdapter cornerAdapter = _modelAdapter as IM3SketchCornerOperationAdapter;
            if (adapter == null || cornerAdapter == null)
                throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch corner adapter is not loaded");

            // Preparation remains a read-only phase. The one external operation id
            // owns the complete prepare/execute/verify lifecycle and is idempotent.
            M3PlanSnapshot snapshot = cornerAdapter.PrepareSketchCorner(binding, request);
            if (snapshot == null || snapshot.AdapterState == null || snapshot.PublicView == null ||
                !String.Equals(snapshot.Kind, "edit_sketch_corner", StringComparison.Ordinal) ||
                !String.Equals(snapshot.RevisionId, request.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(snapshot.SketchId, request.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX sketch corner adapter returned an incomplete or mismatched plan");

            string planId = Guid.NewGuid().ToString("N");
            PreparedM3Plan retainedPlan = new PreparedM3Plan(planId, owner, binding.BindingEpoch,
                binding.SessionEpoch, binding.LoadInstanceId, binding, snapshot);
            retainedPlan.Consumed = true;
            lock (_lifecycle)
            {
                if (_writesHalted || _ownershipState != "AI_OWNED" || !Object.ReferenceEquals(_binding, binding) ||
                    !String.Equals(owner, _ownershipId, StringComparison.Ordinal))
                    throw new BridgeException("OWNERSHIP_CHANGED", "ownership or binding changed during sketch corner preparation");
                if (!String.Equals(binding.RevisionId, snapshot.RevisionId, StringComparison.Ordinal))
                    throw new BridgeException("STALE_REVISION", "model revision changed during sketch corner preparation");
                if (_m3Plans.Count + _plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                _m3Plans.Add(planId, retainedPlan);
                operation.ModelCallStarted = true;
            }

            M3ExecutionResult execution;
            try { execution = adapter.ExecutePlan(binding, snapshot); }
            catch (Exception ex)
            {
                Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal);
                unknown.Add("plan_id", planId);
                unknown.Add("kind", "edit_sketch_corner");
                unknown.Add("mutation_state", "UNKNOWN");
                unknown.Add("verified", false);
                unknown.Add("combined_edit", true);
                unknown.Add("adapter_exception", ex.GetType().FullName);
                unknown.Add("prepare_plan", Clone(snapshot.PublicView));
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(unknown);
                    else SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX sketch corner adapter threw after the mutation phase began; do not retry this operation id", unknown);
                    operation.ExecutionCompleted = true;
                }
                return unknown;
            }

            if (execution == null || (execution.State != "APPLIED" && execution.State != "UNCHANGED" &&
                execution.State != "ROLLED_BACK" && execution.State != "UNKNOWN"))
            {
                Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "plan_id", planId }, { "kind", "edit_sketch_corner" }, { "mutation_state", "UNKNOWN" },
                    { "verified", false }, { "combined_edit", true }, { "adapter_state", execution == null ? null : execution.State },
                    { "prepare_plan", Clone(snapshot.PublicView) }
                };
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(unknown);
                    else SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX sketch corner adapter returned no recognized mutation state; do not retry", unknown);
                    operation.ExecutionCompleted = true;
                }
                return unknown;
            }

            Dictionary<string, object> result = execution.Result == null
                ? new Dictionary<string, object>(StringComparer.Ordinal) : Clone(execution.Result);
            result["plan_id"] = planId;
            result["kind"] = snapshot.Kind;
            result["mutation_state"] = execution.State;
            result["combined_edit"] = true;
            result["revision_id"] = execution.RevisionId ?? binding.RevisionId;
            result["prepare_plan"] = Clone(snapshot.PublicView);

            if (execution.State == "UNKNOWN")
            {
                result["verified"] = false;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(result);
                    else SetOperationUnknownLocked(operation,
                            String.IsNullOrEmpty(execution.ErrorCode) ? "OPERATION_OUTCOME_UNKNOWN" : execution.ErrorCode,
                            String.IsNullOrEmpty(execution.ErrorMessage) ? "NX sketch corner outcome is unknown; do not retry" : execution.ErrorMessage,
                            result);
                    operation.ExecutionCompleted = true;
                }
                return result;
            }
            if (execution.State != "APPLIED")
            {
                result["verified"] = false;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        if (execution.RevisionId != null) binding.RevisionId = execution.RevisionId;
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(execution.ErrorCode) ? "SKETCH_CORNER_NOT_APPLIED" : execution.ErrorCode,
                            String.IsNullOrEmpty(execution.ErrorMessage) ? "NX did not apply the sketch corner operation" : execution.ErrorMessage);
                    }
                }
                return result;
            }

            lock (_lifecycle)
            {
                if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                {
                    operation.LateResult = Clone(result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "STALE_BINDING",
                        "binding changed while the sketch corner write was executing", result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (execution.RevisionId != null) binding.RevisionId = execution.RevisionId;
            }

            M3VerificationResult verification;
            try { verification = adapter.VerifyPlan(binding, snapshot); }
            catch (Exception ex)
            {
                Dictionary<string, object> verifyError = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "exception_type", ex.GetType().FullName }, { "message", SafeExceptionMessage(ex) }
                };
                result["verified"] = false;
                result["verification"] = verifyError;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else if (IsUnknownVerifyException(ex))
                    {
                        SetOperationUnknownLocked(operation, ExceptionCode(ex),
                            SafeExceptionMessage(ex), result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        result["mutation_state"] = "APPLIED";
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject("VERIFY_FAILED", "NX applied the corner but the independent VerifyPlan readback failed");
                    }
                }
                return result;
            }
            if (verification == null)
            {
                result["verified"] = false;
                result["mutation_state"] = "APPLIED";
                result["verification"] = ErrorObject("VERIFY_RESULT_MISSING", "NX VerifyPlan returned no result after the corner was applied");
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        SetOperationUnknownLocked(operation, "VERIFY_RESULT_MISSING",
                            "NX applied the corner but independent VerifyPlan returned no result", result);
                        operation.ExecutionCompleted = true;
                    }
                }
                return result;
            }
            bool checksPassed = M3VerificationChecksPassed(verification.Checks, snapshot.Kind);
            bool verified = verification.Verified && checksPassed;
            result["verified"] = verified;
            result["revision_id"] = verification.RevisionId ?? binding.RevisionId;
            result["verification"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "verified", verified },
                { "checks", verification.Checks },
                { "evidence", verification.Result },
                { "error", String.IsNullOrEmpty(verification.ErrorCode) ? null : ErrorObject(verification.ErrorCode, verification.ErrorMessage) }
            };
            if (verification.Result != null) result["evidence"] = Clone(verification.Result);

            lock (_lifecycle)
            {
                if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                {
                    operation.LateResult = Clone(result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "STALE_BINDING",
                        "binding changed after the sketch corner write and independent verification", result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!verified)
                {
                    result["mutation_state"] = "APPLIED";
                    if (IsUnknownVerificationResult(verification))
                    {
                        SetOperationUnknownLocked(operation,
                            String.IsNullOrEmpty(verification.ErrorCode) ? "VERIFY_OUTCOME_UNKNOWN" : verification.ErrorCode,
                            String.IsNullOrEmpty(verification.ErrorMessage) ? "NX sketch corner verification reports an unknown state" : verification.ErrorMessage,
                            result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(verification.ErrorCode) ? "VERIFY_MISMATCH" : verification.ErrorCode,
                            String.IsNullOrEmpty(verification.ErrorMessage) ? "NX applied the corner but independent verification did not match" : verification.ErrorMessage);
                    }
                    return result;
                }
            }
            return result;
        }

        private string EditSketchOffset(object requestId, Dictionary<string, object> args)
        {
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            string owner = NormalizeRequiredString(RequiredString(args, "ownership_id"), "ownership_id", MaxOperationIdChars);
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            string revisionId = NormalizeRequiredString(RequiredString(args, "revision_id"), "revision_id", 128);
            Dictionary<string, object> normalized;
            M3SketchOffsetRequest request = ParseM3SketchOffsetRequest(args, revisionId, owner, epoch, out normalized);
            string requestKey = OperationRequestKey("edit_sketch_offset", normalized);
            OperationRecord operation;
            DispatchWork work;
            M2ModelBinding binding;
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                if (_operations.TryGetValue(operationId, out operation))
                {
                    if (!String.Equals(operation.RequestKey, requestKey, StringComparison.Ordinal))
                        throw new BridgeException("OPERATION_ID_CONFLICT", "operation_id was already used with a different normalized request");
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
                }
                if (_stopping) throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                if (_writesHalted)
                    throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "model writes are halted until the unknown operation outcome is resolved");
                if (ActiveSaveCopyLocked() || ActiveParameterExecutionLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "another model mutation or save is queued or running; query its operation_id before starting F08");
                if (_ownershipState != "AI_OWNED" || !_acceptModelReads || !String.Equals(owner, _ownershipId, StringComparison.Ordinal) ||
                    epoch != _bindingEpoch || _binding == null)
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "sketch offset edit requires current AI ownership and binding epoch");
                if (!(_modelAdapter is IM3OperationAdapter) || !(_modelAdapter is IM3SketchOffsetOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch offset adapter is not loaded");
                if (_operations.Count >= MaxOperations)
                    throw new BridgeException("OPERATION_LIMIT_REACHED", "operation record capacity is full for this bridge load");
                if (_plans.Count + _m3Plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                binding = _binding;
                operation = new OperationRecord();
                operation.Id = operationId;
                // The execute_ prefix inherits timeout freezing and write serialization.
                operation.Kind = "execute_edit_sketch_offset";
                operation.RequestKey = requestKey;
                operation.State = "QUEUED";
                operation.Cancellable = true;
                _operations.Add(operationId, operation);
                work = new DispatchWork();
                work.Operation = operation;
                work.Execute = delegate { return PerformEditSketchOffsetOperation(operation, binding, owner, request); };
                operation.Work = work;
                QueueDispatchLocked(work);
            }

            Task.WaitAny(new Task[] { work.Done.Task }, DispatchTimeoutMs);
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
            }
        }

        private Dictionary<string, object> PerformEditSketchOffsetOperation(OperationRecord operation,
            M2ModelBinding binding, string owner, M3SketchOffsetRequest request)
        {
            RequireCurrentBinding(binding);
            if (!String.Equals(binding.RevisionId, request.RevisionId, StringComparison.Ordinal))
                throw new BridgeException("STALE_REVISION", "revision_id does not match the current captured model revision");
            IM3OperationAdapter adapter = _modelAdapter as IM3OperationAdapter;
            IM3SketchOffsetOperationAdapter offsetAdapter = _modelAdapter as IM3SketchOffsetOperationAdapter;
            if (adapter == null || offsetAdapter == null)
                throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch offset adapter is not loaded");

            // Preparation remains a read-only phase. The one external operation id
            // owns the complete prepare/execute/verify lifecycle and is idempotent.
            M3PlanSnapshot snapshot = offsetAdapter.PrepareSketchOffset(binding, request);
            if (snapshot == null || snapshot.AdapterState == null || snapshot.PublicView == null ||
                !String.Equals(snapshot.Kind, "edit_sketch_offset", StringComparison.Ordinal) ||
                !String.Equals(snapshot.RevisionId, request.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(snapshot.SketchId, request.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX sketch offset adapter returned an incomplete or mismatched plan");

            string planId = Guid.NewGuid().ToString("N");
            PreparedM3Plan retainedPlan = new PreparedM3Plan(planId, owner, binding.BindingEpoch,
                binding.SessionEpoch, binding.LoadInstanceId, binding, snapshot);
            retainedPlan.Consumed = true;
            lock (_lifecycle)
            {
                if (_writesHalted || _ownershipState != "AI_OWNED" || !Object.ReferenceEquals(_binding, binding) ||
                    !String.Equals(owner, _ownershipId, StringComparison.Ordinal))
                    throw new BridgeException("OWNERSHIP_CHANGED", "ownership or binding changed during sketch offset preparation");
                if (!String.Equals(binding.RevisionId, snapshot.RevisionId, StringComparison.Ordinal))
                    throw new BridgeException("STALE_REVISION", "model revision changed during sketch offset preparation");
                if (_m3Plans.Count + _plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                _m3Plans.Add(planId, retainedPlan);
                operation.ModelCallStarted = true;
            }

            M3ExecutionResult execution;
            try { execution = adapter.ExecutePlan(binding, snapshot); }
            catch (Exception ex)
            {
                Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal);
                unknown.Add("plan_id", planId);
                unknown.Add("kind", "edit_sketch_offset");
                unknown.Add("mutation_state", "UNKNOWN");
                unknown.Add("verified", false);
                unknown.Add("combined_edit", true);
                unknown.Add("adapter_exception", ex.GetType().FullName);
                unknown.Add("prepare_plan", Clone(snapshot.PublicView));
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(unknown);
                    else SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX sketch offset adapter threw after the mutation phase began; do not retry this operation id", unknown);
                    operation.ExecutionCompleted = true;
                }
                return unknown;
            }

            if (execution == null || (execution.State != "APPLIED" && execution.State != "UNCHANGED" &&
                execution.State != "ROLLED_BACK" && execution.State != "UNKNOWN"))
            {
                Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "plan_id", planId }, { "kind", "edit_sketch_offset" }, { "mutation_state", "UNKNOWN" },
                    { "verified", false }, { "combined_edit", true }, { "adapter_state", execution == null ? null : execution.State },
                    { "prepare_plan", Clone(snapshot.PublicView) }
                };
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(unknown);
                    else SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX sketch offset adapter returned no recognized mutation state; do not retry", unknown);
                    operation.ExecutionCompleted = true;
                }
                return unknown;
            }

            Dictionary<string, object> result = execution.Result == null
                ? new Dictionary<string, object>(StringComparer.Ordinal) : Clone(execution.Result);
            result["plan_id"] = planId;
            result["kind"] = snapshot.Kind;
            result["mutation_state"] = execution.State;
            result["combined_edit"] = true;
            result["revision_id"] = execution.RevisionId ?? binding.RevisionId;
            result["prepare_plan"] = Clone(snapshot.PublicView);

            if (execution.State == "UNKNOWN")
            {
                result["verified"] = false;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(result);
                    else SetOperationUnknownLocked(operation,
                            String.IsNullOrEmpty(execution.ErrorCode) ? "OPERATION_OUTCOME_UNKNOWN" : execution.ErrorCode,
                            String.IsNullOrEmpty(execution.ErrorMessage) ? "NX sketch offset outcome is unknown; do not retry" : execution.ErrorMessage,
                            result);
                    operation.ExecutionCompleted = true;
                }
                return result;
            }
            if (execution.State != "APPLIED")
            {
                result["verified"] = false;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        if (execution.RevisionId != null) binding.RevisionId = execution.RevisionId;
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(execution.ErrorCode) ? "SKETCH_OFFSET_NOT_APPLIED" : execution.ErrorCode,
                            String.IsNullOrEmpty(execution.ErrorMessage) ? "NX did not apply the sketch offset operation" : execution.ErrorMessage);
                    }
                }
                return result;
            }

            lock (_lifecycle)
            {
                if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                {
                    operation.LateResult = Clone(result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "STALE_BINDING",
                        "binding changed while the sketch offset write was executing", result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (execution.RevisionId != null) binding.RevisionId = execution.RevisionId;
            }

            M3VerificationResult verification;
            try { verification = adapter.VerifyPlan(binding, snapshot); }
            catch (Exception ex)
            {
                Dictionary<string, object> verifyError = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "exception_type", ex.GetType().FullName }, { "message", SafeExceptionMessage(ex) }
                };
                result["verified"] = false;
                result["verification"] = verifyError;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else if (IsUnknownVerifyException(ex))
                    {
                        SetOperationUnknownLocked(operation, ExceptionCode(ex),
                            SafeExceptionMessage(ex), result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        result["mutation_state"] = "APPLIED";
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject("VERIFY_FAILED", "NX applied the offset but the independent VerifyPlan readback failed");
                    }
                }
                return result;
            }
            if (verification == null)
            {
                result["verified"] = false;
                result["mutation_state"] = "APPLIED";
                result["verification"] = ErrorObject("VERIFY_RESULT_MISSING", "NX VerifyPlan returned no result after the offset was applied");
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        SetOperationUnknownLocked(operation, "VERIFY_RESULT_MISSING",
                            "NX applied the offset but independent VerifyPlan returned no result", result);
                        operation.ExecutionCompleted = true;
                    }
                }
                return result;
            }
            bool checksPassed = M3VerificationChecksPassed(verification.Checks, snapshot.Kind);
            bool verified = verification.Verified && checksPassed;
            result["verified"] = verified;
            result["revision_id"] = verification.RevisionId ?? binding.RevisionId;
            result["verification"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "verified", verified },
                { "checks", verification.Checks },
                { "evidence", verification.Result },
                { "error", String.IsNullOrEmpty(verification.ErrorCode) ? null : ErrorObject(verification.ErrorCode, verification.ErrorMessage) }
            };
            if (verification.Result != null) result["evidence"] = Clone(verification.Result);

            lock (_lifecycle)
            {
                if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                {
                    operation.LateResult = Clone(result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "STALE_BINDING",
                        "binding changed after the sketch offset write and independent verification", result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!verified)
                {
                    result["mutation_state"] = "APPLIED";
                    if (IsUnknownVerificationResult(verification))
                    {
                        SetOperationUnknownLocked(operation,
                            String.IsNullOrEmpty(verification.ErrorCode) ? "VERIFY_OUTCOME_UNKNOWN" : verification.ErrorCode,
                            String.IsNullOrEmpty(verification.ErrorMessage) ? "NX sketch offset verification reports an unknown state" : verification.ErrorMessage,
                            result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(verification.ErrorCode) ? "VERIFY_MISMATCH" : verification.ErrorCode,
                            String.IsNullOrEmpty(verification.ErrorMessage) ? "NX applied the corner but independent verification did not match" : verification.ErrorMessage);
                    }
                    return result;
                }
            }
            return result;
        }

        private string EditSketchPattern(object requestId, Dictionary<string, object> args)
        {
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            string owner = NormalizeRequiredString(RequiredString(args, "ownership_id"), "ownership_id", MaxOperationIdChars);
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            string revisionId = NormalizeRequiredString(RequiredString(args, "revision_id"), "revision_id", 128);
            Dictionary<string, object> normalized;
            M3SketchPatternRequest request = ParseM3SketchPatternRequest(args, revisionId, owner, epoch, out normalized);
            string requestKey = OperationRequestKey("edit_sketch_pattern", normalized);
            OperationRecord operation;
            DispatchWork work;
            M2ModelBinding binding;
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                if (_operations.TryGetValue(operationId, out operation))
                {
                    if (!String.Equals(operation.RequestKey, requestKey, StringComparison.Ordinal))
                        throw new BridgeException("OPERATION_ID_CONFLICT", "operation_id was already used with a different normalized request");
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
                }
                if (_stopping) throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                if (_writesHalted)
                    throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "model writes are halted until the unknown operation outcome is resolved");
                if (ActiveSaveCopyLocked() || ActiveParameterExecutionLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "another model mutation or save is queued or running; query its operation_id before starting F10");
                if (_ownershipState != "AI_OWNED" || !_acceptModelReads || !String.Equals(owner, _ownershipId, StringComparison.Ordinal) ||
                    epoch != _bindingEpoch || _binding == null)
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "sketch pattern edit requires current AI ownership and binding epoch");
                if (!(_modelAdapter is IM3OperationAdapter) || !(_modelAdapter is IM3SketchPatternOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch pattern adapter is not loaded");
                if (_operations.Count >= MaxOperations)
                    throw new BridgeException("OPERATION_LIMIT_REACHED", "operation record capacity is full for this bridge load");
                if (_plans.Count + _m3Plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                binding = _binding;
                operation = new OperationRecord();
                operation.Id = operationId;
                // The execute_ prefix inherits timeout freezing and write serialization.
                operation.Kind = "execute_edit_sketch_pattern";
                operation.RequestKey = requestKey;
                operation.State = "QUEUED";
                operation.Cancellable = true;
                _operations.Add(operationId, operation);
                work = new DispatchWork();
                work.Operation = operation;
                work.Execute = delegate { return PerformEditSketchPatternOperation(operation, binding, owner, request); };
                operation.Work = work;
                QueueDispatchLocked(work);
            }

            Task.WaitAny(new Task[] { work.Done.Task }, DispatchTimeoutMs);
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
            }
        }

        private Dictionary<string, object> PerformEditSketchPatternOperation(OperationRecord operation,
            M2ModelBinding binding, string owner, M3SketchPatternRequest request)
        {
            RequireCurrentBinding(binding);
            if (!String.Equals(binding.RevisionId, request.RevisionId, StringComparison.Ordinal))
                throw new BridgeException("STALE_REVISION", "revision_id does not match the current captured model revision");
            IM3OperationAdapter adapter = _modelAdapter as IM3OperationAdapter;
            IM3SketchPatternOperationAdapter patternAdapter = _modelAdapter as IM3SketchPatternOperationAdapter;
            if (adapter == null || patternAdapter == null)
                throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch pattern adapter is not loaded");

            // Preparation remains a read-only phase. The one external operation id
            // owns the complete prepare/execute/verify lifecycle and is idempotent.
            M3PlanSnapshot snapshot = patternAdapter.PrepareSketchPattern(binding, request);
            if (snapshot == null || snapshot.AdapterState == null || snapshot.PublicView == null ||
                !String.Equals(snapshot.Kind, "edit_sketch_pattern", StringComparison.Ordinal) ||
                !String.Equals(snapshot.RevisionId, request.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(snapshot.SketchId, request.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX sketch pattern adapter returned an incomplete or mismatched plan");

            string planId = Guid.NewGuid().ToString("N");
            PreparedM3Plan retainedPlan = new PreparedM3Plan(planId, owner, binding.BindingEpoch,
                binding.SessionEpoch, binding.LoadInstanceId, binding, snapshot);
            retainedPlan.Consumed = true;
            lock (_lifecycle)
            {
                if (_writesHalted || _ownershipState != "AI_OWNED" || !Object.ReferenceEquals(_binding, binding) ||
                    !String.Equals(owner, _ownershipId, StringComparison.Ordinal))
                    throw new BridgeException("OWNERSHIP_CHANGED", "ownership or binding changed during sketch pattern preparation");
                if (!String.Equals(binding.RevisionId, snapshot.RevisionId, StringComparison.Ordinal))
                    throw new BridgeException("STALE_REVISION", "model revision changed during sketch pattern preparation");
                if (_m3Plans.Count + _plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                _m3Plans.Add(planId, retainedPlan);
                operation.ModelCallStarted = true;
            }

            M3ExecutionResult execution;
            try { execution = adapter.ExecutePlan(binding, snapshot); }
            catch (Exception ex)
            {
                Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal);
                unknown.Add("plan_id", planId);
                unknown.Add("kind", "edit_sketch_pattern");
                unknown.Add("mutation_state", "UNKNOWN");
                unknown.Add("verified", false);
                unknown.Add("combined_edit", true);
                unknown.Add("adapter_exception", ex.GetType().FullName);
                unknown.Add("prepare_plan", Clone(snapshot.PublicView));
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(unknown);
                    else SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX sketch pattern adapter threw after the mutation phase began; do not retry this operation id", unknown);
                    operation.ExecutionCompleted = true;
                }
                return unknown;
            }

            if (execution == null || (execution.State != "APPLIED" && execution.State != "UNCHANGED" &&
                execution.State != "ROLLED_BACK" && execution.State != "UNKNOWN"))
            {
                Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "plan_id", planId }, { "kind", "edit_sketch_pattern" }, { "mutation_state", "UNKNOWN" },
                    { "verified", false }, { "combined_edit", true }, { "adapter_state", execution == null ? null : execution.State },
                    { "prepare_plan", Clone(snapshot.PublicView) }
                };
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(unknown);
                    else SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX sketch pattern adapter returned no recognized mutation state; do not retry", unknown);
                    operation.ExecutionCompleted = true;
                }
                return unknown;
            }

            Dictionary<string, object> result = execution.Result == null
                ? new Dictionary<string, object>(StringComparer.Ordinal) : Clone(execution.Result);
            result["plan_id"] = planId;
            result["kind"] = snapshot.Kind;
            result["mutation_state"] = execution.State;
            result["combined_edit"] = true;
            result["revision_id"] = execution.RevisionId ?? binding.RevisionId;
            result["prepare_plan"] = Clone(snapshot.PublicView);

            if (execution.State == "UNKNOWN")
            {
                result["verified"] = false;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(result);
                    else SetOperationUnknownLocked(operation,
                            String.IsNullOrEmpty(execution.ErrorCode) ? "OPERATION_OUTCOME_UNKNOWN" : execution.ErrorCode,
                            String.IsNullOrEmpty(execution.ErrorMessage) ? "NX sketch pattern outcome is unknown; do not retry" : execution.ErrorMessage,
                            result);
                    operation.ExecutionCompleted = true;
                }
                return result;
            }
            if (execution.State != "APPLIED")
            {
                result["verified"] = false;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        if (execution.RevisionId != null) binding.RevisionId = execution.RevisionId;
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(execution.ErrorCode) ? "SKETCH_PATTERN_NOT_APPLIED" : execution.ErrorCode,
                            String.IsNullOrEmpty(execution.ErrorMessage) ? "NX did not apply the sketch pattern operation" : execution.ErrorMessage);
                    }
                }
                return result;
            }

            lock (_lifecycle)
            {
                if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                {
                    operation.LateResult = Clone(result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "STALE_BINDING",
                        "binding changed while the sketch pattern write was executing", result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (execution.RevisionId != null) binding.RevisionId = execution.RevisionId;
            }

            M3VerificationResult verification;
            try { verification = adapter.VerifyPlan(binding, snapshot); }
            catch (Exception ex)
            {
                Dictionary<string, object> verifyError = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "exception_type", ex.GetType().FullName }, { "message", SafeExceptionMessage(ex) }
                };
                result["verified"] = false;
                result["verification"] = verifyError;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else if (IsUnknownVerifyException(ex))
                    {
                        SetOperationUnknownLocked(operation, ExceptionCode(ex),
                            SafeExceptionMessage(ex), result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        result["mutation_state"] = "APPLIED";
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject("VERIFY_FAILED", "NX applied the sketch pattern but the independent VerifyPlan readback failed");
                    }
                }
                return result;
            }
            if (verification == null)
            {
                result["verified"] = false;
                result["mutation_state"] = "APPLIED";
                result["verification"] = ErrorObject("VERIFY_RESULT_MISSING", "NX VerifyPlan returned no result after the sketch pattern was applied");
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        SetOperationUnknownLocked(operation, "VERIFY_RESULT_MISSING",
                            "NX applied the sketch pattern but independent VerifyPlan returned no result", result);
                        operation.ExecutionCompleted = true;
                    }
                }
                return result;
            }
            bool checksPassed = M3VerificationChecksPassed(verification.Checks, snapshot.Kind);
            bool verified = verification.Verified && checksPassed;
            result["verified"] = verified;
            result["revision_id"] = verification.RevisionId ?? binding.RevisionId;
            result["verification"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "verified", verified },
                { "checks", verification.Checks },
                { "evidence", verification.Result },
                { "error", String.IsNullOrEmpty(verification.ErrorCode) ? null : ErrorObject(verification.ErrorCode, verification.ErrorMessage) }
            };
            if (verification.Result != null) result["evidence"] = Clone(verification.Result);

            lock (_lifecycle)
            {
                if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                {
                    operation.LateResult = Clone(result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "STALE_BINDING",
                        "binding changed after the sketch pattern write and independent verification", result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!verified)
                {
                    result["mutation_state"] = "APPLIED";
                    if (IsUnknownVerificationResult(verification))
                    {
                        SetOperationUnknownLocked(operation,
                            String.IsNullOrEmpty(verification.ErrorCode) ? "VERIFY_OUTCOME_UNKNOWN" : verification.ErrorCode,
                            String.IsNullOrEmpty(verification.ErrorMessage) ? "NX sketch pattern verification reports an unknown state" : verification.ErrorMessage,
                            result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(verification.ErrorCode) ? "VERIFY_MISMATCH" : verification.ErrorCode,
                            String.IsNullOrEmpty(verification.ErrorMessage) ? "NX applied the sketch pattern but independent verification did not match" : verification.ErrorMessage);
                    }
                    return result;
                }
            }
            return result;
        }


        private string EditSketchProjection(object requestId, Dictionary<string, object> args)
        {
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            string owner = NormalizeRequiredString(RequiredString(args, "ownership_id"), "ownership_id", MaxOperationIdChars);
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            string revisionId = NormalizeRequiredString(RequiredString(args, "revision_id"), "revision_id", 128);
            Dictionary<string, object> normalized;
            M3SketchProjectionRequest request = ParseM3SketchProjectionRequest(args, revisionId, owner, epoch, out normalized);
            string requestKey = OperationRequestKey("edit_sketch_projection", normalized);
            OperationRecord operation;
            DispatchWork work;
            M2ModelBinding binding;
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                if (_operations.TryGetValue(operationId, out operation))
                {
                    if (!String.Equals(operation.RequestKey, requestKey, StringComparison.Ordinal))
                        throw new BridgeException("OPERATION_ID_CONFLICT", "operation_id was already used with a different normalized request");
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
                }
                if (_stopping) throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                if (_writesHalted)
                    throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "model writes are halted until the unknown operation outcome is resolved");
                if (ActiveSaveCopyLocked() || ActiveParameterExecutionLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "another model mutation or save is queued or running; query its operation_id before starting F09");
                if (_ownershipState != "AI_OWNED" || !_acceptModelReads || !String.Equals(owner, _ownershipId, StringComparison.Ordinal) ||
                    epoch != _bindingEpoch || _binding == null)
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "sketch projection edit requires current AI ownership and binding epoch");
                if (!(_modelAdapter is IM3OperationAdapter) || !(_modelAdapter is IM3SketchProjectionOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch projection adapter is not loaded");
                if (_operations.Count >= MaxOperations)
                    throw new BridgeException("OPERATION_LIMIT_REACHED", "operation record capacity is full for this bridge load");
                if (_plans.Count + _m3Plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                binding = _binding;
                operation = new OperationRecord();
                operation.Id = operationId;
                // The execute_ prefix inherits timeout freezing and write serialization.
                operation.Kind = "execute_edit_sketch_projection";
                operation.RequestKey = requestKey;
                operation.State = "QUEUED";
                operation.Cancellable = true;
                _operations.Add(operationId, operation);
                work = new DispatchWork();
                work.Operation = operation;
                work.Execute = delegate { return PerformEditSketchProjectionOperation(operation, binding, owner, request); };
                operation.Work = work;
                QueueDispatchLocked(work);
            }

            Task.WaitAny(new Task[] { work.Done.Task }, DispatchTimeoutMs);
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
            }
        }

        private Dictionary<string, object> PerformEditSketchProjectionOperation(OperationRecord operation,
            M2ModelBinding binding, string owner, M3SketchProjectionRequest request)
        {
            RequireCurrentBinding(binding);
            if (!String.Equals(binding.RevisionId, request.RevisionId, StringComparison.Ordinal))
                throw new BridgeException("STALE_REVISION", "revision_id does not match the current captured model revision");
            IM3OperationAdapter adapter = _modelAdapter as IM3OperationAdapter;
            IM3SketchProjectionOperationAdapter projectionAdapter = _modelAdapter as IM3SketchProjectionOperationAdapter;
            if (adapter == null || projectionAdapter == null)
                throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch projection adapter is not loaded");

            // Preparation remains a read-only phase. The one external operation id
            // owns the complete prepare/execute/verify lifecycle and is idempotent.
            M3PlanSnapshot snapshot = projectionAdapter.PrepareSketchProjection(binding, request);
            if (snapshot == null || snapshot.AdapterState == null || snapshot.PublicView == null ||
                !String.Equals(snapshot.Kind, "edit_sketch_projection", StringComparison.Ordinal) ||
                !String.Equals(snapshot.RevisionId, request.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(snapshot.SketchId, request.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX sketch projection adapter returned an incomplete or mismatched plan");

            string planId = Guid.NewGuid().ToString("N");
            PreparedM3Plan retainedPlan = new PreparedM3Plan(planId, owner, binding.BindingEpoch,
                binding.SessionEpoch, binding.LoadInstanceId, binding, snapshot);
            retainedPlan.Consumed = true;
            lock (_lifecycle)
            {
                if (_writesHalted || _ownershipState != "AI_OWNED" || !Object.ReferenceEquals(_binding, binding) ||
                    !String.Equals(owner, _ownershipId, StringComparison.Ordinal))
                    throw new BridgeException("OWNERSHIP_CHANGED", "ownership or binding changed during sketch projection preparation");
                if (!String.Equals(binding.RevisionId, snapshot.RevisionId, StringComparison.Ordinal))
                    throw new BridgeException("STALE_REVISION", "model revision changed during sketch projection preparation");
                if (_m3Plans.Count + _plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                _m3Plans.Add(planId, retainedPlan);
                operation.ModelCallStarted = true;
            }

            M3ExecutionResult execution;
            try { execution = adapter.ExecutePlan(binding, snapshot); }
            catch (Exception ex)
            {
                Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal);
                unknown.Add("plan_id", planId);
                unknown.Add("kind", "edit_sketch_projection");
                unknown.Add("mutation_state", "UNKNOWN");
                unknown.Add("verified", false);
                unknown.Add("combined_edit", true);
                unknown.Add("adapter_exception", ex.GetType().FullName);
                unknown.Add("prepare_plan", Clone(snapshot.PublicView));
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(unknown);
                    else SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX sketch projection adapter threw after the mutation phase began; do not retry this operation id", unknown);
                    operation.ExecutionCompleted = true;
                }
                return unknown;
            }

            if (execution == null || (execution.State != "APPLIED" && execution.State != "UNCHANGED" &&
                execution.State != "ROLLED_BACK" && execution.State != "UNKNOWN"))
            {
                Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "plan_id", planId }, { "kind", "edit_sketch_projection" }, { "mutation_state", "UNKNOWN" },
                    { "verified", false }, { "combined_edit", true }, { "adapter_state", execution == null ? null : execution.State },
                    { "prepare_plan", Clone(snapshot.PublicView) }
                };
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(unknown);
                    else SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX sketch projection adapter returned no recognized mutation state; do not retry", unknown);
                    operation.ExecutionCompleted = true;
                }
                return unknown;
            }

            Dictionary<string, object> result = execution.Result == null
                ? new Dictionary<string, object>(StringComparer.Ordinal) : Clone(execution.Result);
            result["plan_id"] = planId;
            result["kind"] = snapshot.Kind;
            result["mutation_state"] = execution.State;
            result["combined_edit"] = true;
            result["revision_id"] = execution.RevisionId ?? binding.RevisionId;
            result["prepare_plan"] = Clone(snapshot.PublicView);

            if (execution.State == "UNKNOWN")
            {
                result["verified"] = false;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(result);
                    else SetOperationUnknownLocked(operation,
                            String.IsNullOrEmpty(execution.ErrorCode) ? "OPERATION_OUTCOME_UNKNOWN" : execution.ErrorCode,
                            String.IsNullOrEmpty(execution.ErrorMessage) ? "NX sketch projection outcome is unknown; do not retry" : execution.ErrorMessage,
                            result);
                    operation.ExecutionCompleted = true;
                }
                return result;
            }
            if (execution.State != "APPLIED")
            {
                result["verified"] = false;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        if (execution.RevisionId != null) binding.RevisionId = execution.RevisionId;
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(execution.ErrorCode) ? "SKETCH_PROJECTION_NOT_APPLIED" : execution.ErrorCode,
                            String.IsNullOrEmpty(execution.ErrorMessage) ? "NX did not apply the sketch projection operation" : execution.ErrorMessage);
                    }
                }
                return result;
            }

            lock (_lifecycle)
            {
                if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                {
                    operation.LateResult = Clone(result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "STALE_BINDING",
                        "binding changed while the sketch projection write was executing", result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (execution.RevisionId != null) binding.RevisionId = execution.RevisionId;
            }

            M3VerificationResult verification;
            try { verification = adapter.VerifyPlan(binding, snapshot); }
            catch (Exception ex)
            {
                Dictionary<string, object> verifyError = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "exception_type", ex.GetType().FullName }, { "message", SafeExceptionMessage(ex) }
                };
                result["verified"] = false;
                result["verification"] = verifyError;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else if (IsUnknownVerifyException(ex))
                    {
                        SetOperationUnknownLocked(operation, ExceptionCode(ex),
                            SafeExceptionMessage(ex), result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        result["mutation_state"] = "APPLIED";
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject("VERIFY_FAILED", "NX applied the sketch projection but the independent VerifyPlan readback failed");
                    }
                }
                return result;
            }
            if (verification == null)
            {
                result["verified"] = false;
                result["mutation_state"] = "APPLIED";
                result["verification"] = ErrorObject("VERIFY_RESULT_MISSING", "NX VerifyPlan returned no result after the sketch projection was applied");
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        SetOperationUnknownLocked(operation, "VERIFY_RESULT_MISSING",
                            "NX applied the sketch projection but independent VerifyPlan returned no result", result);
                        operation.ExecutionCompleted = true;
                    }
                }
                return result;
            }
            bool checksPassed = M3VerificationChecksPassed(verification.Checks, snapshot.Kind);
            bool verified = verification.Verified && checksPassed;
            result["verified"] = verified;
            result["revision_id"] = verification.RevisionId ?? binding.RevisionId;
            result["verification"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "verified", verified },
                { "checks", verification.Checks },
                { "evidence", verification.Result },
                { "error", String.IsNullOrEmpty(verification.ErrorCode) ? null : ErrorObject(verification.ErrorCode, verification.ErrorMessage) }
            };
            if (verification.Result != null) result["evidence"] = Clone(verification.Result);

            lock (_lifecycle)
            {
                if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                {
                    operation.LateResult = Clone(result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "STALE_BINDING",
                        "binding changed after the sketch projection write and independent verification", result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!verified)
                {
                    result["mutation_state"] = "APPLIED";
                    if (IsUnknownVerificationResult(verification))
                    {
                        SetOperationUnknownLocked(operation,
                            String.IsNullOrEmpty(verification.ErrorCode) ? "VERIFY_OUTCOME_UNKNOWN" : verification.ErrorCode,
                            String.IsNullOrEmpty(verification.ErrorMessage) ? "NX sketch projection verification reports an unknown state" : verification.ErrorMessage,
                            result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(verification.ErrorCode) ? "VERIFY_MISMATCH" : verification.ErrorCode,
                            String.IsNullOrEmpty(verification.ErrorMessage) ? "NX applied the sketch projection but independent verification did not match" : verification.ErrorMessage);
                    }
                    return result;
                }
            }
            return result;
        }


        private string EditSketchReferenceConversion(object requestId, Dictionary<string, object> args)
        {
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            string owner = NormalizeRequiredString(RequiredString(args, "ownership_id"), "ownership_id", MaxOperationIdChars);
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            string revisionId = NormalizeRequiredString(RequiredString(args, "revision_id"), "revision_id", 128);
            Dictionary<string, object> normalized;
            M3SketchReferenceConversionRequest request = ParseM3SketchReferenceConversionRequest(
                args, revisionId, owner, epoch, out normalized);
            string requestKey = OperationRequestKey("edit_sketch_reference_conversion", normalized);
            OperationRecord operation;
            DispatchWork work;
            M2ModelBinding binding;
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                if (_operations.TryGetValue(operationId, out operation))
                {
                    if (!String.Equals(operation.RequestKey, requestKey, StringComparison.Ordinal))
                        throw new BridgeException("OPERATION_ID_CONFLICT", "operation_id was already used with a different normalized request");
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
                }
                if (_stopping) throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                if (_writesHalted)
                    throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "model writes are halted until the unknown operation outcome is resolved");
                if (ActiveSaveCopyLocked() || ActiveParameterExecutionLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "another model mutation or save is queued or running; query its operation_id before starting F11");
                if (_ownershipState != "AI_OWNED" || !_acceptModelReads || !String.Equals(owner, _ownershipId, StringComparison.Ordinal) ||
                    epoch != _bindingEpoch || _binding == null)
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "sketch reference conversion requires current AI ownership and binding epoch");
                if (!(_modelAdapter is IM3OperationAdapter) || !(_modelAdapter is IM3SketchReferenceConversionOperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch reference conversion adapter is not loaded");
                if (_operations.Count >= MaxOperations)
                    throw new BridgeException("OPERATION_LIMIT_REACHED", "operation record capacity is full for this bridge load");
                if (_plans.Count + _m3Plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                binding = _binding;
                operation = new OperationRecord();
                operation.Id = operationId;
                operation.Kind = "execute_edit_sketch_reference_conversion";
                operation.RequestKey = requestKey;
                operation.State = "QUEUED";
                operation.Cancellable = true;
                _operations.Add(operationId, operation);
                work = new DispatchWork();
                work.Operation = operation;
                work.Execute = delegate { return PerformEditSketchReferenceConversionOperation(operation, binding, owner, request); };
                operation.Work = work;
                QueueDispatchLocked(work);
            }

            Task.WaitAny(new Task[] { work.Done.Task }, DispatchTimeoutMs);
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
            }
        }

        private Dictionary<string, object> PerformEditSketchReferenceConversionOperation(OperationRecord operation,
            M2ModelBinding binding, string owner, M3SketchReferenceConversionRequest request)
        {
            RequireCurrentBinding(binding);
            if (!String.Equals(binding.RevisionId, request.RevisionId, StringComparison.Ordinal))
                throw new BridgeException("STALE_REVISION", "revision_id does not match the current captured model revision");
            IM3OperationAdapter adapter = _modelAdapter as IM3OperationAdapter;
            IM3SketchReferenceConversionOperationAdapter referenceAdapter =
                _modelAdapter as IM3SketchReferenceConversionOperationAdapter;
            if (adapter == null || referenceAdapter == null)
                throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch reference conversion adapter is not loaded");

            M3PlanSnapshot snapshot = referenceAdapter.PrepareSketchReferenceConversion(binding, request);
            if (snapshot == null || snapshot.AdapterState == null || snapshot.PublicView == null ||
                !String.Equals(snapshot.Kind, "edit_sketch_reference_conversion", StringComparison.Ordinal) ||
                !String.Equals(snapshot.RevisionId, request.RevisionId, StringComparison.Ordinal) ||
                !String.Equals(snapshot.SketchId, request.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX sketch reference conversion adapter returned an incomplete or mismatched plan");

            string planId = Guid.NewGuid().ToString("N");
            PreparedM3Plan retainedPlan = new PreparedM3Plan(planId, owner, binding.BindingEpoch,
                binding.SessionEpoch, binding.LoadInstanceId, binding, snapshot);
            retainedPlan.Consumed = true;
            lock (_lifecycle)
            {
                if (_writesHalted || _ownershipState != "AI_OWNED" || !Object.ReferenceEquals(_binding, binding) ||
                    !String.Equals(owner, _ownershipId, StringComparison.Ordinal))
                    throw new BridgeException("OWNERSHIP_CHANGED", "ownership or binding changed during sketch reference conversion preparation");
                if (!String.Equals(binding.RevisionId, snapshot.RevisionId, StringComparison.Ordinal))
                    throw new BridgeException("STALE_REVISION", "model revision changed during sketch reference conversion preparation");
                if (_m3Plans.Count + _plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                _m3Plans.Add(planId, retainedPlan);
                operation.ModelCallStarted = true;
            }

            M3ExecutionResult execution;
            try { execution = adapter.ExecutePlan(binding, snapshot); }
            catch (Exception ex)
            {
                Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "plan_id", planId }, { "kind", "edit_sketch_reference_conversion" },
                    { "mutation_state", "UNKNOWN" }, { "verified", false }, { "combined_edit", true },
                    { "adapter_exception", ex.GetType().FullName }, { "prepare_plan", Clone(snapshot.PublicView) }
                };
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(unknown);
                    else SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX sketch reference conversion adapter threw after the mutation phase began; do not retry this operation id", unknown);
                    operation.ExecutionCompleted = true;
                }
                return unknown;
            }

            if (execution == null || (execution.State != "APPLIED" && execution.State != "UNCHANGED" &&
                execution.State != "ROLLED_BACK" && execution.State != "UNKNOWN"))
            {
                Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "plan_id", planId }, { "kind", "edit_sketch_reference_conversion" },
                    { "mutation_state", "UNKNOWN" }, { "verified", false }, { "combined_edit", true },
                    { "adapter_state", execution == null ? null : execution.State },
                    { "prepare_plan", Clone(snapshot.PublicView) }
                };
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(unknown);
                    else SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX sketch reference conversion adapter returned no recognized mutation state; do not retry", unknown);
                    operation.ExecutionCompleted = true;
                }
                return unknown;
            }

            Dictionary<string, object> result = execution.Result == null
                ? new Dictionary<string, object>(StringComparer.Ordinal) : Clone(execution.Result);
            result["plan_id"] = planId;
            result["kind"] = snapshot.Kind;
            result["mutation_state"] = execution.State;
            result["combined_edit"] = true;
            result["revision_id"] = execution.RevisionId ?? binding.RevisionId;
            result["prepare_plan"] = Clone(snapshot.PublicView);

            if (execution.State == "UNKNOWN")
            {
                result["verified"] = false;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                        operation.LateResult = Clone(result);
                    else SetOperationUnknownLocked(operation,
                        String.IsNullOrEmpty(execution.ErrorCode) ? "OPERATION_OUTCOME_UNKNOWN" : execution.ErrorCode,
                        String.IsNullOrEmpty(execution.ErrorMessage) ? "NX sketch reference conversion outcome is unknown; do not retry" : execution.ErrorMessage,
                        result);
                    operation.ExecutionCompleted = true;
                }
                return result;
            }
            if (execution.State != "APPLIED")
            {
                result["verified"] = false;
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        if (execution.RevisionId != null) binding.RevisionId = execution.RevisionId;
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(execution.ErrorCode) ? "SKETCH_REFERENCE_CONVERSION_NOT_APPLIED" : execution.ErrorCode,
                            String.IsNullOrEmpty(execution.ErrorMessage) ? "NX did not apply the sketch reference conversion" : execution.ErrorMessage);
                    }
                }
                return result;
            }

            lock (_lifecycle)
            {
                if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                {
                    operation.LateResult = Clone(result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "STALE_BINDING",
                        "binding changed while the sketch reference conversion was executing", result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (execution.RevisionId != null) binding.RevisionId = execution.RevisionId;
            }

            M3VerificationResult verification;
            try { verification = adapter.VerifyPlan(binding, snapshot); }
            catch (Exception ex)
            {
                result["verified"] = false;
                result["verification"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "exception_type", ex.GetType().FullName }, { "message", SafeExceptionMessage(ex) }
                };
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else if (IsUnknownVerifyException(ex))
                    {
                        SetOperationUnknownLocked(operation, ExceptionCode(ex), SafeExceptionMessage(ex), result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject("PART_VERIFICATION_FAILED",
                            "NX sketch reference conversion applied but independent verification failed");
                    }
                }
                return result;
            }
            if (verification == null)
            {
                result["verified"] = false;
                result["mutation_state"] = "APPLIED";
                result["verification"] = ErrorObject("VERIFY_RESULT_MISSING",
                    "NX VerifyPlan returned no result after the sketch reference conversion was applied");
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                    {
                        operation.LateResult = Clone(result);
                        operation.ExecutionCompleted = true;
                    }
                    else
                    {
                        SetOperationUnknownLocked(operation, "VERIFY_RESULT_MISSING",
                            "NX applied the sketch reference conversion but independent VerifyPlan returned no result", result);
                        operation.ExecutionCompleted = true;
                    }
                }
                return result;
            }
            bool checksPassed = M3VerificationChecksPassed(verification.Checks, snapshot.Kind);
            bool verified = verification.Verified && checksPassed;
            result["verified"] = verified;
            result["revision_id"] = verification.RevisionId ?? binding.RevisionId;
            result["verification"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "verified", verified }, { "checks", verification.Checks }, { "evidence", verification.Result },
                { "error", String.IsNullOrEmpty(verification.ErrorCode) ? null : ErrorObject(verification.ErrorCode, verification.ErrorMessage) }
            };
            if (verification.Result != null) result["evidence"] = Clone(verification.Result);
            lock (_lifecycle)
            {
                if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) || _writesHalted)
                {
                    operation.LateResult = Clone(result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "STALE_BINDING",
                        "binding changed after the sketch reference conversion and verification", result);
                    operation.ExecutionCompleted = true;
                    return result;
                }
                if (!verified)
                {
                    if (IsUnknownVerificationResult(verification))
                        SetOperationUnknownLocked(operation,
                            String.IsNullOrEmpty(verification.ErrorCode) ? "VERIFY_OUTCOME_UNKNOWN" : verification.ErrorCode,
                            String.IsNullOrEmpty(verification.ErrorMessage) ? "NX sketch reference conversion verification has an unknown state" : verification.ErrorMessage,
                            result);
                    else
                    {
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.ExecutionCompleted = true;
                        operation.Result = result;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(verification.ErrorCode) ? "VERIFY_MISMATCH" : verification.ErrorCode,
                            String.IsNullOrEmpty(verification.ErrorMessage) ? "NX sketch reference conversion verification did not match" : verification.ErrorMessage);
                    }
                    operation.ExecutionCompleted = true;
                    return result;
                }
                operation.State = "SUCCEEDED";
                operation.Cancellable = false;
                operation.ExecutionCompleted = true;
                operation.Result = result;
            }
            return result;
        }

        private Dictionary<string, object> PerformPrepareM3Operation(OperationRecord operation,
            M2ModelBinding binding, string owner, M3SketchRequest sketchRequest, M3SketchCornerRequest sketchCornerRequest,
            M3SketchOffsetRequest sketchOffsetRequest, M3SketchPatternRequest sketchPatternRequest,
            M3SketchProjectionRequest sketchProjectionRequest,
            M3SketchReferenceConversionRequest sketchReferenceConversionRequest,
            M3DatumPlaneRequest datumPlaneRequest,
            M3ExtrudeRequest extrudeRequest,
            M3RevolveRequest revolveRequest,
            M3SweepRequest sweepRequest,
            M3HoleRequest holeRequest,
            M3BooleanRequest booleanRequest,
            M3EdgeBlendRequest edgeBlendRequest,
            M3ChamferRequest chamferRequest,
            M3FeaturePatternRequest featurePatternRequest,
            M3FeatureMirrorRequest featureMirrorRequest,
            M3SpatialCurveRequest spatialCurveRequest,
            M7ThreadRequest threadRequest, M7HelicalGrooveRequest grooveRequest)
        {
            RequireCurrentBinding(binding);
            IM3OperationAdapter adapter = _modelAdapter as IM3OperationAdapter;
            if (adapter == null && datumPlaneRequest == null)
                throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX M3 sketch/extrude adapter is not loaded");
            string revisionId = sketchRequest != null ? sketchRequest.RevisionId : sketchCornerRequest != null ? sketchCornerRequest.RevisionId :
                sketchOffsetRequest != null ? sketchOffsetRequest.RevisionId : sketchPatternRequest != null ? sketchPatternRequest.RevisionId :
                sketchProjectionRequest != null ? sketchProjectionRequest.RevisionId :
                sketchReferenceConversionRequest != null ? sketchReferenceConversionRequest.RevisionId :
                datumPlaneRequest != null ? datumPlaneRequest.RevisionId :
                extrudeRequest != null ? extrudeRequest.RevisionId :
                revolveRequest != null ? revolveRequest.RevisionId :
                sweepRequest != null ? sweepRequest.RevisionId :
                holeRequest != null ? holeRequest.RevisionId :
                booleanRequest != null ? booleanRequest.RevisionId :
                edgeBlendRequest != null ? edgeBlendRequest.RevisionId :
                chamferRequest != null ? chamferRequest.RevisionId :
                featurePatternRequest != null ? featurePatternRequest.RevisionId :
                featureMirrorRequest != null ? featureMirrorRequest.RevisionId :
                spatialCurveRequest != null ? spatialCurveRequest.RevisionId :
                threadRequest != null ? threadRequest.RevisionId : grooveRequest.RevisionId;
            if (!String.Equals(binding.RevisionId, revisionId, StringComparison.Ordinal))
                throw new BridgeException("STALE_REVISION", "revision_id does not match the current captured model revision");
            M3PlanSnapshot snapshot;
            try
            {
                if (sketchRequest != null) snapshot = adapter.PrepareSketch(binding, sketchRequest);
                else if (sketchCornerRequest != null)
                {
                    IM3SketchCornerOperationAdapter cornerAdapter = _modelAdapter as IM3SketchCornerOperationAdapter;
                    if (cornerAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch corner adapter is not loaded");
                    snapshot = cornerAdapter.PrepareSketchCorner(binding, sketchCornerRequest);
                }
                else if (sketchOffsetRequest != null)
                {
                    IM3SketchOffsetOperationAdapter offsetAdapter = _modelAdapter as IM3SketchOffsetOperationAdapter;
                    if (offsetAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch offset adapter is not loaded");
                    snapshot = offsetAdapter.PrepareSketchOffset(binding, sketchOffsetRequest);
                }
                else if (sketchPatternRequest != null)
                {
                    IM3SketchPatternOperationAdapter patternAdapter = _modelAdapter as IM3SketchPatternOperationAdapter;
                    if (patternAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch pattern adapter is not loaded");
                    snapshot = patternAdapter.PrepareSketchPattern(binding, sketchPatternRequest);
                }
                else if (sketchProjectionRequest != null)
                {
                    IM3SketchProjectionOperationAdapter projectionAdapter = _modelAdapter as IM3SketchProjectionOperationAdapter;
                    if (projectionAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch projection adapter is not loaded");
                    snapshot = projectionAdapter.PrepareSketchProjection(binding, sketchProjectionRequest);
                }
                else if (sketchReferenceConversionRequest != null)
                {
                    IM3SketchReferenceConversionOperationAdapter referenceAdapter =
                        _modelAdapter as IM3SketchReferenceConversionOperationAdapter;
                    if (referenceAdapter == null)
                        throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX sketch reference conversion adapter is not loaded");
                    snapshot = referenceAdapter.PrepareSketchReferenceConversion(binding, sketchReferenceConversionRequest);
                }
                else if (datumPlaneRequest != null)
                {
                    IM3DatumPlaneOperationAdapter datumAdapter = _modelAdapter as IM3DatumPlaneOperationAdapter;
                    if (datumAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX datum plane adapter is not loaded");
                    snapshot = datumAdapter.PrepareDatumPlane(binding, datumPlaneRequest);
                }
                else if (extrudeRequest != null) snapshot = adapter.PrepareExtrude(binding, extrudeRequest);
                else if (revolveRequest != null)
                {
                    IM3RevolveOperationAdapter revolveAdapter = _modelAdapter as IM3RevolveOperationAdapter;
                    if (revolveAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX revolve adapter is not loaded");
                    snapshot = revolveAdapter.PrepareRevolve(binding, revolveRequest);
                }
                else if (sweepRequest != null)
                {
                    IM3SweepOperationAdapter sweepAdapter = _modelAdapter as IM3SweepOperationAdapter;
                    if (sweepAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX sweep adapter is not loaded");
                    snapshot = sweepAdapter.PrepareSweep(binding, sweepRequest);
                }
                else if (booleanRequest != null)
                {
                    IM3BooleanOperationAdapter booleanAdapter = _modelAdapter as IM3BooleanOperationAdapter;
                    if (booleanAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX boolean adapter is not loaded");
                    snapshot = booleanAdapter.PrepareBoolean(binding, booleanRequest);
                }
                else if (chamferRequest != null)
                {
                    IM3ChamferOperationAdapter chamferAdapter = _modelAdapter as IM3ChamferOperationAdapter;
                    if (chamferAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX chamfer adapter is not loaded");
                    snapshot = chamferAdapter.PrepareChamfer(binding, chamferRequest);
                }
                else if (featurePatternRequest != null)
                {
                    IM3FeaturePatternOperationAdapter patternAdapter = _modelAdapter as IM3FeaturePatternOperationAdapter;
                    if (patternAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX feature pattern adapter is not loaded");
                    snapshot = patternAdapter.PrepareFeaturePattern(binding, featurePatternRequest);
                }
                else if (spatialCurveRequest != null)
                {
                    IM3SpatialCurveOperationAdapter curveAdapter = _modelAdapter as IM3SpatialCurveOperationAdapter;
                    if (curveAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX spatial curve adapter is not loaded");
                    snapshot = curveAdapter.PrepareSpatialCurve(binding, spatialCurveRequest);
                }
                else if (featureMirrorRequest != null)
                {
                    IM3FeatureMirrorOperationAdapter mirrorAdapter = _modelAdapter as IM3FeatureMirrorOperationAdapter;
                    if (mirrorAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX feature mirror adapter is not loaded");
                    snapshot = mirrorAdapter.PrepareFeatureMirror(binding, featureMirrorRequest);
                }
                else if (edgeBlendRequest != null)
                {
                    IM3EdgeBlendOperationAdapter edgeBlendAdapter = _modelAdapter as IM3EdgeBlendOperationAdapter;
                    if (edgeBlendAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX edge blend adapter is not loaded");
                    snapshot = edgeBlendAdapter.PrepareEdgeBlend(binding, edgeBlendRequest);
                }
                else if (holeRequest != null)
                {
                    IM3HoleOperationAdapter holeAdapter = _modelAdapter as IM3HoleOperationAdapter;
                    if (holeAdapter == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "NX hole adapter is not loaded");
                    snapshot = holeAdapter.PrepareHole(binding, holeRequest);
                }
                else
                {
                    IM7HelicalOperationAdapter helical = _modelAdapter as IM7HelicalOperationAdapter;
                    if (helical == null) throw new M2ModelException("OPERATION_ADAPTER_UNAVAILABLE", "native helical adapter is not loaded");
                    snapshot = threadRequest != null ? helical.PrepareThread(binding, threadRequest) : helical.PrepareHelicalGroove(binding, grooveRequest);
                }
            }
            catch (M2ModelException ex)
            {
                if (String.Equals(ex.Code, "M3_ANALYSIS_STATE_UNKNOWN", StringComparison.Ordinal))
                {
                    Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal);
                    unknown.Add("preparation_state", "UNKNOWN");
                    unknown.Add("kind", extrudeRequest == null ? operation.Kind : "create_extrude");
                    unknown.Add("part_path", binding.PartPath);
                    unknown.Add("revision_id", binding.RevisionId);
                    unknown.Add("error", ErrorObject(ex.Code, ex.Message));
                    lock (_lifecycle)
                    {
                        if (String.Equals(operation.State, "RUNNING", StringComparison.Ordinal))
                            SetOperationUnknownLocked(operation, ex.Code, ex.Message, unknown);
                        else if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal))
                            operation.LateResult = Clone(unknown);
                        operation.ExecutionCompleted = true;
                    }
                    return unknown;
                }
                throw;
            }
            catch (Exception ex)
            {
                throw new M2ModelException("PLAN_PREPARATION_FAILED", "NX M3 plan preparation failed (" + ex.GetType().Name + ")");
            }
            if (snapshot == null || snapshot.AdapterState == null || snapshot.PublicView == null)
                throw new M2ModelException("PLAN_PREPARATION_FAILED", "NX M3 adapter returned an incomplete plan snapshot");
            string expectedKind = sketchRequest != null ? "create_sketch" : sketchCornerRequest != null ? "edit_sketch_corner" :
                sketchOffsetRequest != null ? "edit_sketch_offset" : sketchPatternRequest != null ? "edit_sketch_pattern" :
                sketchProjectionRequest != null ? "edit_sketch_projection" :
                sketchReferenceConversionRequest != null ? "edit_sketch_reference_conversion" :
                datumPlaneRequest != null ? "create_datum_plane" :
                extrudeRequest != null ? "create_extrude" :
                revolveRequest != null ? "create_revolve" :
                sweepRequest != null ? "create_sweep" :
                holeRequest != null ? "create_hole" :
                booleanRequest != null ? "create_boolean" :
                edgeBlendRequest != null ? "create_edge_blend" :
                chamferRequest != null ? "create_chamfer" :
                featurePatternRequest != null ? "create_feature_pattern" :
                featureMirrorRequest != null ? "create_feature_mirror" :
                spatialCurveRequest != null ? "create_spatial_curve" :
                threadRequest != null ? "create_thread" : "create_helical_groove";
            if (!String.Equals(snapshot.RevisionId, revisionId, StringComparison.Ordinal) ||
                !String.Equals(snapshot.Kind, expectedKind, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX M3 adapter returned a plan for a different revision or operation kind");
            if (extrudeRequest != null && !String.Equals(snapshot.SketchId, extrudeRequest.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX M3 adapter resolved a different sketch than requested");
            if (revolveRequest != null && !String.Equals(snapshot.SketchId, revolveRequest.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX revolve adapter resolved a different Sketch than requested");
            if (sweepRequest != null && !String.Equals(snapshot.SketchId, sweepRequest.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX sweep adapter resolved a different Sketch than requested");
            object booleanTarget;
            if (booleanRequest != null && (!snapshot.PublicView.TryGetValue("target_body_id", out booleanTarget) ||
                !String.Equals(booleanTarget as string, booleanRequest.TargetBodyId, StringComparison.Ordinal)))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX boolean adapter resolved a different target body than requested");
            object chamferTarget;
            if (chamferRequest != null && (!snapshot.PublicView.TryGetValue("target_body_id", out chamferTarget) ||
                !String.Equals(chamferTarget as string, chamferRequest.TargetBodyId, StringComparison.Ordinal)))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX chamfer adapter resolved a different target body than requested");
            object edgeBlendTarget;
            if (edgeBlendRequest != null && (!snapshot.PublicView.TryGetValue("target_body_id", out edgeBlendTarget) ||
                !String.Equals(edgeBlendTarget as string, edgeBlendRequest.TargetBodyId, StringComparison.Ordinal)))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX edge blend adapter resolved a different target body than requested");
            object holeTarget;
            if (holeRequest != null && (!snapshot.PublicView.TryGetValue("target_body_id", out holeTarget) ||
                !String.Equals(holeTarget as string, holeRequest.TargetBodyId, StringComparison.Ordinal)))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX hole adapter resolved a different target body than requested");
            if (sketchCornerRequest != null && !String.Equals(snapshot.SketchId, sketchCornerRequest.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX sketch corner adapter resolved a different Sketch than requested");
            if (sketchOffsetRequest != null && !String.Equals(snapshot.SketchId, sketchOffsetRequest.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX sketch offset adapter resolved a different Sketch than requested");
            if (sketchPatternRequest != null && !String.Equals(snapshot.SketchId, sketchPatternRequest.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX sketch pattern adapter resolved a different Sketch than requested");
            if (sketchProjectionRequest != null && !String.Equals(snapshot.SketchId, sketchProjectionRequest.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX sketch projection adapter resolved a different Sketch than requested");
            if (sketchReferenceConversionRequest != null && !String.Equals(snapshot.SketchId,
                sketchReferenceConversionRequest.SketchId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX sketch reference conversion adapter resolved a different Sketch than requested");
            if ((threadRequest != null || grooveRequest != null) && !String.Equals(snapshot.BodyId,
                threadRequest != null ? threadRequest.TargetBodyId : grooveRequest.TargetBodyId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX native adapter resolved a different target body than requested");

            string planId = Guid.NewGuid().ToString("N");
            PreparedM3Plan plan = new PreparedM3Plan(planId, owner, binding.BindingEpoch,
                binding.SessionEpoch, binding.LoadInstanceId, binding, snapshot);
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("plan", Clone(plan.PublicView));
            lock (_lifecycle)
            {
                if (_writesHalted || _ownershipState != "AI_OWNED" || !Object.ReferenceEquals(_binding, binding))
                    throw new BridgeException("OWNERSHIP_CHANGED", "ownership changed while preparing the M3 plan");
                if (_plans.Count + _m3Plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                _m3Plans.Add(planId, plan);
            }
            return result;
        }

        private Dictionary<string, object> PerformExecuteM3Plan(OperationRecord operation,
            PreparedM3Plan plan, M2ModelBinding binding)
        {
            RequireCurrentBinding(binding);
            if (!String.Equals(binding.RevisionId, plan.AdapterSnapshot.RevisionId, StringComparison.Ordinal))
                throw new BridgeException("STALE_PLAN", "model revision changed after M3 plan preparation");
            IM3OperationAdapter adapter = _modelAdapter as IM3OperationAdapter;
            if (adapter == null) throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX M3 sketch/extrude adapter is not loaded");
            lock (_lifecycle)
            {
                if (_writesHalted || String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal))
                    throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "M3 operation was stopped before its NX call because writes are frozen");
                operation.ModelCallStarted = true;
            }

            M3ExecutionResult execution;
            try { execution = adapter.ExecutePlan(binding, plan.AdapterSnapshot); }
            catch (Exception ex)
            {
                Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
                evidence.Add("adapter_exception", ex.GetType().Name);
                lock (_lifecycle)
                {
                    SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX M3 mutation returned without a verifiable final state", evidence);
                    operation.ExecutionCompleted = true;
                }
                return operation.Result;
            }

            if (execution == null || (execution.State != "APPLIED" && execution.State != "UNCHANGED" &&
                execution.State != "ROLLED_BACK" && execution.State != "UNKNOWN"))
            {
                Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
                evidence.Add("adapter_result_state", execution == null ? null : execution.State);
                lock (_lifecycle)
                {
                    SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "NX M3 adapter returned no recognized final mutation state", evidence);
                    operation.ExecutionCompleted = true;
                }
                return operation.Result;
            }

            Dictionary<string, object> result = execution.Result == null
                ? new Dictionary<string, object>(StringComparer.Ordinal) : Clone(execution.Result);
            result["plan_id"] = plan.Id;
            result["kind"] = plan.AdapterSnapshot.Kind;
            result["mutation_state"] = execution.State;
            result["revision_id"] = execution.RevisionId ?? binding.RevisionId;
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal))
                {
                    operation.LateResult = Clone(result);
                    operation.ExecutionCompleted = true;
                    return operation.Result;
                }
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN",
                        "Work Part binding changed while the M3 operation was executing", result);
                    operation.ExecutionCompleted = true;
                    return operation.Result;
                }
                if (execution.RevisionId != null) binding.RevisionId = execution.RevisionId;
                operation.ExecutionCompleted = true;
                operation.Cancellable = false;
                operation.Result = result;
                if (execution.State == "APPLIED")
                {
                    operation.State = "SUCCEEDED";
                    operation.Error = null;
                }
                else if (execution.State == "UNKNOWN")
                {
                    SetOperationUnknownLocked(operation,
                        String.IsNullOrEmpty(execution.ErrorCode) ? "OPERATION_OUTCOME_UNKNOWN" : execution.ErrorCode,
                        String.IsNullOrEmpty(execution.ErrorMessage) ? "NX M3 mutation outcome is unknown; do not retry" : execution.ErrorMessage,
                        result);
                }
                else
                {
                    operation.State = "FAILED";
                    operation.Error = ErrorObject(
                        String.IsNullOrEmpty(execution.ErrorCode) ? "M3_OPERATION_FAILED" : execution.ErrorCode,
                        String.IsNullOrEmpty(execution.ErrorMessage)
                            ? (execution.State == "ROLLED_BACK" ? "NX M3 operation failed and the prior model state was restored" : "NX M3 operation made no model change")
                            : execution.ErrorMessage);
                }
            }
            return result;
        }

        private static M3SketchRequest ParseM3SketchRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            string[] common = new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id" };
            bool constraintModeSpecified = args.ContainsKey("constraint_mode");
            string constraintMode = constraintModeSpecified
                ? NormalizeRequiredString(RequiredString(args, "constraint_mode"), "constraint_mode", 32)
                : "legacy_driving";
            if (constraintMode != "legacy_driving" && constraintMode != "geometry_only")
                throw new BridgeException("INVALID_REQUEST", "constraint_mode must be legacy_driving or geometry_only");
            bool selectorFaceMode = args.ContainsKey("support_face_selector");
            bool explicitFaceMode = args.ContainsKey("support_face_id");
            bool supportFaceTagSpecified = args.ContainsKey("support_face_tag");
            bool datumPlaneMode = args.ContainsKey("support_datum_plane_id");
            if (supportFaceTagSpecified && !explicitFaceMode)
                throw new BridgeException("INVALID_REQUEST", "support_face_tag requires support_face_id");
            bool customFrameMode = (args.ContainsKey("normal") || args.ContainsKey("x_axis")) &&
                !selectorFaceMode && !explicitFaceMode && !datumPlaneMode;
            int frameModes = (selectorFaceMode ? 1 : 0) + (explicitFaceMode ? 1 : 0) +
                (datumPlaneMode ? 1 : 0) + (customFrameMode ? 1 : 0);
            if (frameModes > 1)
                throw new BridgeException("INVALID_REQUEST", "sketch request must select exactly one frame/support mode");
            string plane = null;
            double[] origin = null;
            double[] normal = null;
            double[] xAxis = null;
            string faceSelector = null;
            string faceId = null;
            string faceTag = null;
            string datumPlaneId = null;
            string supportBodyId = null;
            double[] faceOriginOffset = null;
            if (selectorFaceMode)
            {
                List<string> allowedFace = new List<string>(common);
                allowedFace.Add("support_face_selector");
                if (args.ContainsKey("face_origin_offset_mm")) allowedFace.Add("face_origin_offset_mm");
                if (args.ContainsKey("support_body_id")) allowedFace.Add("support_body_id");
                if (args.ContainsKey("geometries")) allowedFace.Add("geometries");
                if (constraintModeSpecified) allowedFace.Add("constraint_mode");
                RequireExactKeys(args, allowedFace.ToArray(), "operation.prepare_sketch face params");
                faceSelector = NormalizeRequiredString(RequiredString(args, "support_face_selector"), "support_face_selector", 256);
                faceOriginOffset = args.ContainsKey("face_origin_offset_mm")
                    ? RequiredNumberArray(args, "face_origin_offset_mm", 2) : new double[] { 0.0, 0.0 };
                object rawSupportBody;
                if (args.TryGetValue("support_body_id", out rawSupportBody))
                    supportBodyId = NormalizeRequiredString(rawSupportBody as string, "support_body_id", MaxOperationIdChars);
            }
            else if (explicitFaceMode)
            {
                List<string> allowed = new List<string>(common);
                allowed.AddRange(new string[] { "support_face_id", "origin_mm", "x_axis" });
                if (supportFaceTagSpecified) allowed.Add("support_face_tag");
                if (args.ContainsKey("support_body_id")) allowed.Add("support_body_id");
                if (args.ContainsKey("geometries")) allowed.Add("geometries");
                if (constraintModeSpecified) allowed.Add("constraint_mode");
                RequireExactKeys(args, allowed.ToArray(), "operation.prepare_sketch explicit face params");
                faceId = NormalizeRequiredString(RequiredString(args, "support_face_id"), "support_face_id", MaxOperationIdChars);
                if (supportFaceTagSpecified)
                    faceTag = NormalizeRequiredString(RequiredString(args, "support_face_tag"), "support_face_tag", MaxOperationIdChars);
                origin = RequiredNumberArray(args, "origin_mm", 3);
                xAxis = RequiredNumberArray(args, "x_axis", 3);
                object rawSupportBody;
                if (args.TryGetValue("support_body_id", out rawSupportBody))
                    supportBodyId = NormalizeRequiredString(rawSupportBody as string, "support_body_id", MaxOperationIdChars);
            }
            else if (datumPlaneMode)
            {
                List<string> allowed = new List<string>(common);
                allowed.AddRange(new string[] { "support_datum_plane_id", "x_axis" });
                if (args.ContainsKey("origin_mm")) allowed.Add("origin_mm");
                if (args.ContainsKey("geometries")) allowed.Add("geometries");
                if (constraintModeSpecified) allowed.Add("constraint_mode");
                RequireExactKeys(args, allowed.ToArray(), "operation.prepare_sketch datum plane params");
                datumPlaneId = NormalizeRequiredString(RequiredString(args, "support_datum_plane_id"),
                    "support_datum_plane_id", MaxOperationIdChars);
                if (args.ContainsKey("origin_mm")) origin = RequiredNumberArray(args, "origin_mm", 3);
                xAxis = RequiredNumberArray(args, "x_axis", 3);
            }
            else if (customFrameMode)
            {
                List<string> allowed = new List<string>(common);
                allowed.AddRange(new string[] { "origin_mm", "normal", "x_axis" });
                if (args.ContainsKey("geometries")) allowed.Add("geometries");
                if (constraintModeSpecified) allowed.Add("constraint_mode");
                RequireExactKeys(args, allowed.ToArray(), "operation.prepare_sketch custom frame params");
                origin = RequiredNumberArray(args, "origin_mm", 3);
                normal = RequiredNumberArray(args, "normal", 3);
                xAxis = RequiredNumberArray(args, "x_axis", 3);
            }
            else
            {
                List<string> allowed = new List<string>(common);
                allowed.AddRange(new string[] { "plane", "origin_mm" });
                if (args.ContainsKey("geometries")) allowed.Add("geometries");
                if (constraintModeSpecified) allowed.Add("constraint_mode");
                RequireExactKeys(args, allowed.ToArray(), "operation.prepare_sketch params");
                plane = RequiredString(args, "plane");
                if (plane != "XY" && plane != "XZ" && plane != "YZ")
                    throw new BridgeException("INVALID_REQUEST", "plane must be XY, XZ, or YZ");
                origin = RequiredNumberArray(args, "origin_mm", 3);
            }
            object rawGeometries;
            List<object> inputGeometries;
            if (!args.TryGetValue("geometries", out rawGeometries)) inputGeometries = new List<object>();
            else if ((inputGeometries = rawGeometries as List<object>) == null || inputGeometries.Count > 32)
                throw new BridgeException("INVALID_REQUEST", "geometries must contain at most 32 geometry records");

            M3SketchRequest request = new M3SketchRequest();
            request.RevisionId = revisionId;
            request.Plane = plane;
            request.OriginMm = origin;
            request.Normal = normal;
            request.XAxis = xAxis;
            request.SupportFaceSelector = faceSelector;
            request.SupportFaceId = faceId;
            request.SupportFaceTag = faceTag;
            request.SupportDatumPlaneId = datumPlaneId;
            request.SupportBodyId = supportBodyId;
            request.FaceOriginOffsetMm = faceOriginOffset;
            request.ConstraintMode = constraintMode;
            request.Geometries = new List<M3SketchGeometryRequest>();
            List<object> canonicalGeometries = new List<object>();
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < inputGeometries.Count; i++)
            {
                Dictionary<string, object> item = inputGeometries[i] as Dictionary<string, object>;
                if (item == null) throw new BridgeException("INVALID_REQUEST", "each geometries entry must be an object");
                string type = RequiredString(item, "type");
                string id = NormalizeRequiredString(RequiredString(item, "id"), "geometry id", 128);
                if (!ids.Add(id)) throw new BridgeException("INVALID_REQUEST", "geometry ids must be unique within the sketch request");
                M3SketchGeometryRequest geometry = new M3SketchGeometryRequest();
                geometry.Id = id;
                geometry.Type = type;
                Dictionary<string, object> canonical = new Dictionary<string, object>(StringComparer.Ordinal);
                canonical.Add("id", id);
                canonical.Add("type", type);
                if (type == "line")
                {
                    RequireExactKeys(item, new string[] { "id", "type", "start_mm", "end_mm" }, "line geometry");
                    geometry.StartMm = RequiredNumberArray(item, "start_mm", 2);
                    geometry.EndMm = RequiredNumberArray(item, "end_mm", 2);
                    if (geometry.StartMm[0] == geometry.EndMm[0] && geometry.StartMm[1] == geometry.EndMm[1])
                        throw new BridgeException("INVALID_REQUEST", "line start_mm and end_mm must differ");
                    canonical.Add("start_mm", CanonicalNumberList(item["start_mm"], 2, "start_mm"));
                    canonical.Add("end_mm", CanonicalNumberList(item["end_mm"], 2, "end_mm"));
                }
                else if (type == "rectangle")
                {
                    RequireExactKeys(item, new string[] { "id", "type", "corner_mm", "width_mm", "height_mm" }, "rectangle geometry");
                    geometry.CornerMm = RequiredNumberArray(item, "corner_mm", 2);
                    geometry.WidthMm = RequiredPositiveNumber(item, "width_mm");
                    geometry.HeightMm = RequiredPositiveNumber(item, "height_mm");
                    canonical.Add("corner_mm", CanonicalNumberList(item["corner_mm"], 2, "corner_mm"));
                    canonical.Add("width_mm", NormalizeNumberField(item["width_mm"], "width_mm"));
                    canonical.Add("height_mm", NormalizeNumberField(item["height_mm"], "height_mm"));
                }
                else if (type == "circle")
                {
                    RequireExactKeys(item, new string[] { "id", "type", "center_mm", "diameter_mm" }, "circle geometry");
                    geometry.CenterMm = RequiredNumberArray(item, "center_mm", 2);
                    geometry.DiameterMm = RequiredPositiveNumber(item, "diameter_mm");
                    canonical.Add("center_mm", CanonicalNumberList(item["center_mm"], 2, "center_mm"));
                    canonical.Add("diameter_mm", NormalizeNumberField(item["diameter_mm"], "diameter_mm"));
                }
                else if (type == "arc")
                {
                    RequireExactKeys(item, new string[] { "id", "type", "center_mm", "radius_mm", "start_angle_deg", "sweep_deg" }, "arc geometry");
                    geometry.CenterMm = RequiredNumberArray(item, "center_mm", 2);
                    geometry.RadiusMm = RequiredPositiveNumber(item, "radius_mm");
                    geometry.StartAngleDeg = RequiredFiniteNumber(item, "start_angle_deg");
                    geometry.SweepDeg = RequiredFiniteNumber(item, "sweep_deg");
                    ValidateM3ArcAngles(geometry.StartAngleDeg, geometry.SweepDeg, "arc");
                    canonical.Add("center_mm", CanonicalNumberList(item["center_mm"], 2, "center_mm"));
                    canonical.Add("radius_mm", NormalizeNumberField(item["radius_mm"], "radius_mm"));
                    canonical.Add("start_angle_deg", NormalizeNumberField(item["start_angle_deg"], "start_angle_deg"));
                    canonical.Add("sweep_deg", NormalizeNumberField(item["sweep_deg"], "sweep_deg"));
                }
                else if (type == "ellipse" || type == "elliptical_arc")
                {
                    string[] keys = type == "ellipse"
                        ? new string[] { "id", "type", "center_mm", "major_radius_mm", "minor_radius_mm", "rotation_deg" }
                        : new string[] { "id", "type", "center_mm", "major_radius_mm", "minor_radius_mm", "rotation_deg", "start_angle_deg", "sweep_deg" };
                    RequireExactKeys(item, keys, type + " geometry");
                    geometry.CenterMm = RequiredNumberArray(item, "center_mm", 2);
                    geometry.MajorRadiusMm = RequiredPositiveNumber(item, "major_radius_mm");
                    geometry.MinorRadiusMm = RequiredPositiveNumber(item, "minor_radius_mm");
                    geometry.RotationDeg = RequiredFiniteNumber(item, "rotation_deg");
                    if (geometry.MajorRadiusMm < geometry.MinorRadiusMm)
                        throw new BridgeException("INVALID_REQUEST", "major_radius_mm must be greater than or equal to minor_radius_mm");
                    if (geometry.RotationDeg < 0.0 || geometry.RotationDeg >= 360.0)
                        throw new BridgeException("INVALID_REQUEST", "rotation_deg must be in [0, 360)");
                    canonical.Add("center_mm", CanonicalNumberList(item["center_mm"], 2, "center_mm"));
                    canonical.Add("major_radius_mm", NormalizeNumberField(item["major_radius_mm"], "major_radius_mm"));
                    canonical.Add("minor_radius_mm", NormalizeNumberField(item["minor_radius_mm"], "minor_radius_mm"));
                    canonical.Add("rotation_deg", NormalizeNumberField(item["rotation_deg"], "rotation_deg"));
                    if (type == "elliptical_arc")
                    {
                        geometry.StartAngleDeg = RequiredFiniteNumber(item, "start_angle_deg");
                        geometry.SweepDeg = RequiredFiniteNumber(item, "sweep_deg");
                        ValidateM3ArcAngles(geometry.StartAngleDeg, geometry.SweepDeg, "elliptical_arc");
                        canonical.Add("start_angle_deg", NormalizeNumberField(item["start_angle_deg"], "start_angle_deg"));
                        canonical.Add("sweep_deg", NormalizeNumberField(item["sweep_deg"], "sweep_deg"));
                    }
                }
                else if (type == "spline")
                {
                    RequireExactKeys(item, new string[] { "id", "type", "interpolation_points_mm", "degree", "endpoint_condition" }, "spline geometry");
                    object rawPoints;
                    List<object> points;
                    if (!item.TryGetValue("interpolation_points_mm", out rawPoints) ||
                        (points = rawPoints as List<object>) == null || points.Count < 4 || points.Count > 32)
                        throw new BridgeException("INVALID_REQUEST", "spline interpolation_points_mm must contain 4 through 32 points");
                    int degree;
                    if (!M2Json.TryGetInt(item, "degree", out degree) || degree != 3)
                        throw new BridgeException("INVALID_REQUEST", "spline degree must be 3");
                    string endpointCondition = RequiredString(item, "endpoint_condition");
                    if (endpointCondition != "unconstrained")
                        throw new BridgeException("INVALID_REQUEST", "spline endpoint_condition must be unconstrained");
                    geometry.InterpolationPointsMm = new double[points.Count][];
                    List<object> canonicalPoints = new List<object>();
                    for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                    {
                        List<object> point = points[pointIndex] as List<object>;
                        if (point == null || point.Count != 2)
                            throw new BridgeException("INVALID_REQUEST", "each spline interpolation point must contain two coordinates");
                        geometry.InterpolationPointsMm[pointIndex] = new double[2];
                        if (!M2Json.TryGetFiniteNumber(point[0], out geometry.InterpolationPointsMm[pointIndex][0]) ||
                            !M2Json.TryGetFiniteNumber(point[1], out geometry.InterpolationPointsMm[pointIndex][1]))
                            throw new BridgeException("INVALID_REQUEST", "spline interpolation points must contain only finite numbers");
                        if (pointIndex > 0)
                        {
                            double dx = geometry.InterpolationPointsMm[pointIndex][0] - geometry.InterpolationPointsMm[pointIndex - 1][0];
                            double dy = geometry.InterpolationPointsMm[pointIndex][1] - geometry.InterpolationPointsMm[pointIndex - 1][1];
                            if (Math.Sqrt(dx * dx + dy * dy) <= 1e-9)
                                throw new BridgeException("INVALID_REQUEST", "consecutive spline interpolation points must differ");
                        }
                        canonicalPoints.Add(CanonicalNumberList(point, 2, "interpolation_points_mm"));
                    }
                    geometry.Degree = degree;
                    geometry.EndpointCondition = endpointCondition;
                    canonical.Add("interpolation_points_mm", canonicalPoints);
                    canonical.Add("degree", degree);
                    canonical.Add("endpoint_condition", endpointCondition);
                }
                else throw new BridgeException("UNSUPPORTED_GEOMETRY", "geometry type must be line, rectangle, circle, arc, ellipse, elliptical_arc, or spline");
                request.Geometries.Add(geometry);
                canonicalGeometries.Add(canonical);
            }
            normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            normalized.Add("ownership_id", owner);
            normalized.Add("binding_epoch", epoch);
            normalized.Add("revision_id", revisionId);
            if (selectorFaceMode)
            {
                normalized.Add("support_face_selector", faceSelector);
                normalized.Add("face_origin_offset_mm", faceOriginOffset == null
                    ? new List<object> { 0.0, 0.0 }
                    : CanonicalNumberList(args.ContainsKey("face_origin_offset_mm") ? args["face_origin_offset_mm"] : new List<object> { 0.0, 0.0 }, 2, "face_origin_offset_mm"));
                if (supportBodyId != null) normalized.Add("support_body_id", supportBodyId);
            }
            else if (explicitFaceMode)
            {
                normalized.Add("support_face_id", faceId);
                if (faceTag != null) normalized.Add("support_face_tag", faceTag);
                normalized.Add("origin_mm", CanonicalNumberList(args["origin_mm"], 3, "origin_mm"));
                normalized.Add("x_axis", CanonicalNumberList(args["x_axis"], 3, "x_axis"));
                if (supportBodyId != null) normalized.Add("support_body_id", supportBodyId);
            }
            else if (datumPlaneMode)
            {
                normalized.Add("support_datum_plane_id", datumPlaneId);
                if (args.ContainsKey("origin_mm")) normalized.Add("origin_mm", CanonicalNumberList(args["origin_mm"], 3, "origin_mm"));
                normalized.Add("x_axis", CanonicalNumberList(args["x_axis"], 3, "x_axis"));
            }
            else if (customFrameMode)
            {
                normalized.Add("origin_mm", CanonicalNumberList(args["origin_mm"], 3, "origin_mm"));
                normalized.Add("normal", CanonicalNumberList(args["normal"], 3, "normal"));
                normalized.Add("x_axis", CanonicalNumberList(args["x_axis"], 3, "x_axis"));
            }
            else
            {
                normalized.Add("plane", plane);
                normalized.Add("origin_mm", CanonicalNumberList(args["origin_mm"], 3, "origin_mm"));
            }
            if (constraintMode == "geometry_only") normalized.Add("constraint_mode", constraintMode);
            normalized.Add("geometries", canonicalGeometries);
            return request;
        }

        private static M3SketchCornerRequest ParseM3SketchCornerRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            string kind = RequiredString(args, "kind");
            if (kind != "fillet" && kind != "chamfer")
                throw new BridgeException("INVALID_REQUEST", "kind must be fillet or chamfer");
            M3SketchCornerRequest request = new M3SketchCornerRequest();
            request.RevisionId = revisionId;
            request.Kind = kind;
            request.SketchId = NormalizeRequiredString(RequiredString(args, "sketch_id"), "sketch_id", 4096);
            request.Curve1Id = NormalizeRequiredString(RequiredString(args, "curve1_id"), "curve1_id", 4096);
            request.Curve2Id = NormalizeRequiredString(RequiredString(args, "curve2_id"), "curve2_id", 4096);
            request.Curve1End = RequiredString(args, "curve1_end");
            request.Curve2End = RequiredString(args, "curve2_end");
            if (request.Curve1End != "start" && request.Curve1End != "end")
                throw new BridgeException("INVALID_REQUEST", "curve1_end must be start or end");
            if (request.Curve2End != "start" && request.Curve2End != "end")
                throw new BridgeException("INVALID_REQUEST", "curve2_end must be start or end");
            if (String.Equals(request.Curve1Id, request.Curve2Id, StringComparison.Ordinal))
                throw new BridgeException("INVALID_REQUEST", "curve1_id and curve2_id must identify distinct curves");

            string[] common = new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id",
                "sketch_id", "kind", "curve1_id", "curve2_id", "curve1_end", "curve2_end" };
            List<string> exact = new List<string>(common);
            if (kind == "fillet")
            {
                if (args.ContainsKey("distance1_mm") || args.ContainsKey("distance2_mm") || args.ContainsKey("angle_deg"))
                    throw new BridgeException("INVALID_REQUEST", "fillet accepts radius_mm only; chamfer fields cannot be mixed");
                exact.Add("radius_mm");
                RequireExactKeys(args, exact.ToArray(), "operation.prepare_sketch_corner fillet params");
                request.RadiusMm = RequiredPositiveNumber(args, "radius_mm");
            }
            else
            {
                if (args.ContainsKey("radius_mm"))
                    throw new BridgeException("INVALID_REQUEST", "chamfer accepts distance fields only; radius_mm cannot be mixed");
                bool hasSecondDistance = args.ContainsKey("distance2_mm");
                bool hasAngle = args.ContainsKey("angle_deg");
                if (hasSecondDistance == hasAngle)
                    throw new BridgeException("INVALID_REQUEST", "chamfer requires distance1_mm and exactly one of distance2_mm or angle_deg");
                exact.Add("distance1_mm");
                exact.Add(hasSecondDistance ? "distance2_mm" : "angle_deg");
                RequireExactKeys(args, exact.ToArray(), "operation.prepare_sketch_corner chamfer params");
                request.Distance1Mm = RequiredPositiveNumber(args, "distance1_mm");
                if (hasSecondDistance) request.Distance2Mm = RequiredPositiveNumber(args, "distance2_mm");
                else
                {
                    request.AngleDeg = RequiredFiniteNumber(args, "angle_deg");
                    if (request.AngleDeg.Value <= 0.0 || request.AngleDeg.Value >= 180.0)
                        throw new BridgeException("INVALID_REQUEST", "angle_deg must be greater than 0 and less than 180 degrees");
                }
            }

            normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            normalized.Add("ownership_id", owner);
            normalized.Add("binding_epoch", epoch);
            normalized.Add("revision_id", revisionId);
            normalized.Add("sketch_id", request.SketchId);
            normalized.Add("kind", kind);
            normalized.Add("curve1_id", request.Curve1Id);
            normalized.Add("curve1_end", request.Curve1End);
            normalized.Add("curve2_id", request.Curve2Id);
            normalized.Add("curve2_end", request.Curve2End);
            if (kind == "fillet") normalized.Add("radius_mm", NormalizeNumberField(args["radius_mm"], "radius_mm"));
            else
            {
                normalized.Add("distance1_mm", NormalizeNumberField(args["distance1_mm"], "distance1_mm"));
                if (request.Distance2Mm.HasValue)
                    normalized.Add("distance2_mm", NormalizeNumberField(args["distance2_mm"], "distance2_mm"));
                else normalized.Add("angle_deg", NormalizeNumberField(args["angle_deg"], "angle_deg"));
            }
            return request;
        }

        private static M3SketchOffsetRequest ParseM3SketchOffsetRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            M3SketchOffsetRequest request = new M3SketchOffsetRequest();
            request.RevisionId = revisionId;
            request.SketchId = NormalizeRequiredString(RequiredString(args, "sketch_id"), "sketch_id", 4096);
            request.DistanceMm = RequiredPositiveNumber(args, "distance_mm");
            request.Side = RequiredString(args, "side");
            if (request.Side != "left" && request.Side != "right")
                throw new BridgeException("INVALID_REQUEST", "side must be left or right");
            request.JoinMode = RequiredString(args, "join_mode");
            if (request.JoinMode != "extension" && request.JoinMode != "arc")
                throw new BridgeException("INVALID_REQUEST", "join_mode must be extension or arc");
            object retainValue;
            if (!args.TryGetValue("retain_original", out retainValue) || !(retainValue is bool))
                throw new BridgeException("INVALID_REQUEST", "retain_original must be the boolean true");
            request.RetainOriginal = (bool)retainValue;
            if (!request.RetainOriginal)
                throw new BridgeException("UNSUPPORTED_REQUEST", "F08 requires retain_original=true; replacing or deleting source curves is not supported");

            object chainValue;
            List<object> chain = args.TryGetValue("curve_chain", out chainValue) ? chainValue as List<object> : null;
            if (chain == null || chain.Count < 1 || chain.Count > 128)
                throw new BridgeException("INVALID_REQUEST", "curve_chain must contain 1 to 128 ordered curve entries");
            request.CurveChain = new List<M3SketchOffsetCurveRequest>();
            HashSet<string> seenCurveIds = new HashSet<string>(StringComparer.Ordinal);
            List<object> canonicalChain = new List<object>();
            foreach (object entryValue in chain)
            {
                Dictionary<string, object> entry = entryValue as Dictionary<string, object>;
                if (entry == null) throw new BridgeException("INVALID_REQUEST", "each curve_chain entry must be an object");
                RequireExactKeys(entry, new string[] { "curve_id", "direction" }, "curve_chain entry");
                M3SketchOffsetCurveRequest curve = new M3SketchOffsetCurveRequest();
                curve.CurveId = NormalizeRequiredString(RequiredString(entry, "curve_id"), "curve_id", 4096);
                curve.Direction = RequiredString(entry, "direction");
                if (curve.Direction != "forward" && curve.Direction != "reverse")
                    throw new BridgeException("INVALID_REQUEST", "curve_chain direction must be forward or reverse");
                if (!seenCurveIds.Add(curve.CurveId))
                    throw new BridgeException("INVALID_REQUEST", "curve_chain cannot contain duplicate curve_id values");
                request.CurveChain.Add(curve);
                canonicalChain.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "curve_id", curve.CurveId }, { "direction", curve.Direction }
                });
            }

            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id",
                "sketch_id", "curve_chain", "distance_mm", "side", "retain_original", "join_mode" },
                "operation.prepare_sketch_offset params");
            normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            normalized.Add("ownership_id", owner);
            normalized.Add("binding_epoch", epoch);
            normalized.Add("revision_id", revisionId);
            normalized.Add("sketch_id", request.SketchId);
            normalized.Add("curve_chain", canonicalChain);
            normalized.Add("distance_mm", NormalizeNumberField(args["distance_mm"], "distance_mm"));
            normalized.Add("side", request.Side);
            normalized.Add("retain_original", request.RetainOriginal);
            normalized.Add("join_mode", request.JoinMode);
            return request;
        }

        private static M3SketchPatternRequest ParseM3SketchPatternRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            M3SketchPatternRequest request = new M3SketchPatternRequest();
            request.RevisionId = revisionId;
            request.SketchId = NormalizeRequiredString(RequiredString(args, "sketch_id"), "sketch_id", 4096);
            request.Mode = RequiredString(args, "mode");
            if (request.Mode != "linear" && request.Mode != "rectangular" && request.Mode != "circular" && request.Mode != "mirror")
                throw new BridgeException("INVALID_REQUEST", "mode must be linear, rectangular, circular, or mirror");

            object sourceValue;
            List<object> sourceIds = args.TryGetValue("source_curve_ids", out sourceValue) ? sourceValue as List<object> : null;
            if (sourceIds == null || sourceIds.Count < 1 || sourceIds.Count > 256)
                throw new BridgeException("INVALID_REQUEST", "source_curve_ids must contain 1 to 256 explicit curve IDs");
            request.SourceCurveIds = new List<string>();
            List<object> canonicalSourceIds = new List<object>();
            HashSet<string> seenSources = new HashSet<string>(StringComparer.Ordinal);
            foreach (object sourceValueItem in sourceIds)
            {
                string sourceId = NormalizeRequiredString(sourceValueItem as string, "source_curve_id", 4096);
                if (!seenSources.Add(sourceId))
                    throw new BridgeException("INVALID_REQUEST", "source_curve_ids must be unique");
                request.SourceCurveIds.Add(sourceId);
                canonicalSourceIds.Add(sourceId);
            }

            normalized = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "ownership_id", owner },
                { "binding_epoch", epoch },
                { "revision_id", revisionId },
                { "sketch_id", request.SketchId },
                { "source_curve_ids", canonicalSourceIds },
                { "mode", request.Mode }
            };
            string[] expected;
            int totalPositions;
            if (request.Mode == "linear")
            {
                expected = new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                    "source_curve_ids", "mode", "direction_xy", "spacing_mm", "instance_count" };
                request.DirectionXY = RequiredNumberArray(args, "direction_xy", 2);
                if (request.DirectionXY[0] == 0.0 && request.DirectionXY[1] == 0.0)
                    throw new BridgeException("INVALID_REQUEST", "direction_xy must be nonzero");
                request.SpacingMm = RequiredPositiveNumber(args, "spacing_mm");
                request.InstanceCount = RequiredPositiveInt(args, "instance_count");
                if (request.InstanceCount < 2 || request.InstanceCount > 256)
                    throw new BridgeException("INVALID_REQUEST", "instance_count must be between 2 and 256 including the source position");
                totalPositions = request.InstanceCount;
                normalized.Add("direction_xy", request.DirectionXY);
                normalized.Add("spacing_mm", NormalizeNumberField(args["spacing_mm"], "spacing_mm"));
                normalized.Add("instance_count", request.InstanceCount);
            }
            else if (request.Mode == "rectangular")
            {
                expected = new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                    "source_curve_ids", "mode", "direction_x_xy", "direction_y_xy", "spacing_x_mm", "spacing_y_mm", "count_x", "count_y" };
                request.DirectionXXY = RequiredNumberArray(args, "direction_x_xy", 2);
                request.DirectionYXY = RequiredNumberArray(args, "direction_y_xy", 2);
                if ((request.DirectionXXY[0] == 0.0 && request.DirectionXXY[1] == 0.0) ||
                    (request.DirectionYXY[0] == 0.0 && request.DirectionYXY[1] == 0.0) ||
                    !AreOrthogonalVectors2D(request.DirectionXXY, request.DirectionYXY))
                    throw new BridgeException("INVALID_REQUEST", "direction_x_xy and direction_y_xy must be nonzero and orthogonal");
                request.SpacingXmm = RequiredPositiveNumber(args, "spacing_x_mm");
                request.SpacingYmm = RequiredPositiveNumber(args, "spacing_y_mm");
                request.CountX = RequiredPositiveInt(args, "count_x");
                request.CountY = RequiredPositiveInt(args, "count_y");
                if (request.CountX < 2 || request.CountY < 2)
                    throw new BridgeException("INVALID_REQUEST", "count_x and count_y must each be at least 2 including the source position");
                long positionProduct = (long)request.CountX * request.CountY;
                if (positionProduct > 256)
                    throw new BridgeException("INVALID_REQUEST", "count_x * count_y must not exceed 256 total positions");
                totalPositions = (int)positionProduct;
                normalized.Add("direction_x_xy", request.DirectionXXY);
                normalized.Add("direction_y_xy", request.DirectionYXY);
                normalized.Add("spacing_x_mm", NormalizeNumberField(args["spacing_x_mm"], "spacing_x_mm"));
                normalized.Add("spacing_y_mm", NormalizeNumberField(args["spacing_y_mm"], "spacing_y_mm"));
                normalized.Add("count_x", request.CountX);
                normalized.Add("count_y", request.CountY);
            }
            else if (request.Mode == "circular")
            {
                expected = new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                    "source_curve_ids", "mode", "center_xy", "angle_step_deg", "instance_count" };
                request.CenterXY = RequiredNumberArray(args, "center_xy", 2);
                request.AngleStepDeg = RequiredFiniteNumber(args, "angle_step_deg");
                if (request.AngleStepDeg == 0.0)
                    throw new BridgeException("INVALID_REQUEST", "angle_step_deg must be nonzero");
                if (Math.Abs(request.AngleStepDeg) >= 360.0)
                    throw new BridgeException("INVALID_REQUEST", "angle_step_deg must satisfy 0 < abs(angle_step_deg) < 360 degrees");
                request.InstanceCount = RequiredPositiveInt(args, "instance_count");
                if (request.InstanceCount < 2 || request.InstanceCount > 256)
                    throw new BridgeException("INVALID_REQUEST", "instance_count must be between 2 and 256 including the source position");
                double normalizedStep = request.AngleStepDeg % 360.0;
                List<double> angles = new List<double>();
                for (int index = 0; index < request.InstanceCount; index++)
                {
                    double angle = (normalizedStep * index) % 360.0;
                    foreach (double prior in angles)
                    {
                        double difference = Math.Abs(angle - prior);
                        if (Math.Min(difference, 360.0 - difference) <= 1e-9)
                            throw new BridgeException("INVALID_REQUEST", "angle_step_deg and instance_count produce duplicate circular positions");
                    }
                    angles.Add(angle);
                }
                totalPositions = request.InstanceCount;
                normalized.Add("center_xy", request.CenterXY);
                normalized.Add("angle_step_deg", NormalizeNumberField(args["angle_step_deg"], "angle_step_deg"));
                normalized.Add("instance_count", request.InstanceCount);
            }
            else
            {
                expected = new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                    "source_curve_ids", "mode", "axis_curve_id" };
                request.AxisCurveId = NormalizeRequiredString(RequiredString(args, "axis_curve_id"), "axis_curve_id", 4096);
                if (seenSources.Contains(request.AxisCurveId))
                    throw new BridgeException("INVALID_REQUEST", "axis_curve_id must not occur among source_curve_ids");
                totalPositions = 2;
                normalized.Add("axis_curve_id", request.AxisCurveId);
            }
            RequireExactKeys(args, expected, "operation.prepare_sketch_pattern params");
            if ((long)totalPositions * request.SourceCurveIds.Count > 4096)
                throw new BridgeException("INVALID_REQUEST", "total pattern positions times source_curve_ids count must not exceed 4096");
            return request;
        }

        private static bool AreOrthogonalVectors2D(double[] first, double[] second)
        {
            double firstScale = Math.Max(Math.Abs(first[0]), Math.Abs(first[1]));
            double secondScale = Math.Max(Math.Abs(second[0]), Math.Abs(second[1]));
            if (firstScale == 0.0 || secondScale == 0.0) return false;
            double firstX = first[0] / firstScale;
            double firstY = first[1] / firstScale;
            double secondX = second[0] / secondScale;
            double secondY = second[1] / secondScale;
            double firstNorm = Math.Sqrt(firstX * firstX + firstY * firstY);
            double secondNorm = Math.Sqrt(secondX * secondX + secondY * secondY);
            double normalizedDot = firstX / firstNorm * (secondX / secondNorm) +
                firstY / firstNorm * (secondY / secondNorm);
            return Math.Abs(normalizedDot) <= 1e-9;
        }

        private static M3SketchProjectionRequest ParseM3SketchProjectionRequest(
            Dictionary<string, object> args, string revisionId, string owner, int epoch,
            out Dictionary<string, object> normalized)
        {
            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id",
                "sketch_id", "source_curve_ids", "direction_xyz", "associative" },
                "operation.prepare_sketch_projection params");

            M3SketchProjectionRequest request = new M3SketchProjectionRequest();
            request.RevisionId = revisionId;
            request.SketchId = NormalizeRequiredString(RequiredString(args, "sketch_id"), "sketch_id", 4096);
            object sourceValue;
            List<object> sourceIds = args.TryGetValue("source_curve_ids", out sourceValue) ? sourceValue as List<object> : null;
            if (sourceIds == null || sourceIds.Count < 1 || sourceIds.Count > 128)
                throw new BridgeException("INVALID_REQUEST", "source_curve_ids must contain 1 to 128 explicit curve or edge IDs");
            request.SourceCurveIds = new List<string>();
            List<object> canonicalSourceIds = new List<object>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in sourceIds)
            {
                string sourceId = NormalizeRequiredString(value as string, "source_curve_id", 4096);
                if (!seen.Add(sourceId)) throw new BridgeException("INVALID_REQUEST", "source_curve_ids must be unique");
                request.SourceCurveIds.Add(sourceId);
                canonicalSourceIds.Add(sourceId);
            }

            double[] direction = RequiredNumberArray(args, "direction_xyz", 3);
            double scale = Math.Max(Math.Abs(direction[0]), Math.Max(Math.Abs(direction[1]), Math.Abs(direction[2])));
            if (scale <= 0.0) throw new BridgeException("INVALID_REQUEST", "direction_xyz must be nonzero");
            double x = direction[0] / scale;
            double y = direction[1] / scale;
            double z = direction[2] / scale;
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (length <= 0.0 || Double.IsNaN(length) || Double.IsInfinity(length))
                throw new BridgeException("INVALID_REQUEST", "direction_xyz must be a finite nonzero world vector");
            request.DirectionXYZ = new double[] { x / length, y / length, z / length };

            object associativeValue;
            if (!args.TryGetValue("associative", out associativeValue) || !(associativeValue is bool))
                throw new BridgeException("INVALID_REQUEST", "associative must be a required JSON boolean");
            request.Associative = (bool)associativeValue;

            normalized = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "ownership_id", owner },
                { "binding_epoch", epoch },
                { "revision_id", revisionId },
                { "sketch_id", request.SketchId },
                { "source_curve_ids", canonicalSourceIds },
                { "direction_xyz", request.DirectionXYZ },
                { "associative", request.Associative }
            };
            return request;
        }

        private static M3SketchReferenceConversionRequest ParseM3SketchReferenceConversionRequest(
            Dictionary<string, object> args, string revisionId, string owner, int epoch,
            out Dictionary<string, object> normalized)
        {
            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id",
                "sketch_id", "object_ids", "target_state" },
                "operation.prepare_sketch_reference_conversion params");

            M3SketchReferenceConversionRequest request = new M3SketchReferenceConversionRequest();
            request.RevisionId = revisionId;
            request.SketchId = NormalizeRequiredString(RequiredString(args, "sketch_id"), "sketch_id", 4096);
            object objectValue;
            List<object> objectIds = args.TryGetValue("object_ids", out objectValue) ? objectValue as List<object> : null;
            if (objectIds == null || objectIds.Count < 1 || objectIds.Count > 256)
                throw new BridgeException("INVALID_REQUEST", "object_ids must contain 1 to 256 explicit Sketch curve or dimension IDs");
            request.ObjectIds = new List<string>();
            List<object> canonicalObjectIds = new List<object>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in objectIds)
            {
                string objectId = NormalizeRequiredString(value as string, "object_id", 4096);
                if (!seen.Add(objectId)) throw new BridgeException("INVALID_REQUEST", "object_ids must be unique");
                request.ObjectIds.Add(objectId);
                canonicalObjectIds.Add(objectId);
            }

            request.TargetState = NormalizeRequiredString(RequiredString(args, "target_state"), "target_state", 16);
            if (request.TargetState != "reference" && request.TargetState != "active")
                throw new BridgeException("INVALID_REQUEST", "target_state must be reference or active");

            normalized = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "ownership_id", owner },
                { "binding_epoch", epoch },
                { "revision_id", revisionId },
                { "sketch_id", request.SketchId },
                { "object_ids", canonicalObjectIds },
                { "target_state", request.TargetState }
            };
            return request;
        }

        private static M3DatumPlaneRequest ParseM3DatumPlaneRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            M3DatumPlaneRequest request = new M3DatumPlaneRequest();
            request.RevisionId = revisionId;
            normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            normalized.Add("ownership_id", owner);
            normalized.Add("binding_epoch", epoch);
            normalized.Add("revision_id", revisionId);

            bool hasDatumReference = args.ContainsKey("support_datum_plane_id");
            bool hasFaceReference = args.ContainsKey("support_face_id");
            bool supportFaceTagSpecified = args.ContainsKey("support_face_tag");
            if (supportFaceTagSpecified && !hasFaceReference)
                throw new BridgeException("INVALID_REQUEST", "support_face_tag requires support_face_id");
            bool hasDistance = args.ContainsKey("distance_mm");
            bool hasAxisLine = args.ContainsKey("axis_line_id");
            bool hasAngle = args.ContainsKey("angle_deg");
            bool hasLine1 = args.ContainsKey("line1_id");
            bool hasLine2 = args.ContainsKey("line2_id");
            bool hasAnyAngleField = hasAxisLine || hasAngle;
            bool hasAnyOffsetField = hasDatumReference || hasFaceReference || hasDistance;
            bool hasAnyFixedField = args.ContainsKey("origin_mm") || args.ContainsKey("normal") || args.ContainsKey("x_axis");
            bool hasAnyTwoLineField = hasLine1 || hasLine2;
            if (hasAnyTwoLineField)
            {
                if (!hasLine1 || !hasLine2 || hasAnyAngleField || hasAnyOffsetField || hasAnyFixedField)
                    throw new BridgeException("INVALID_REQUEST", "two-line datum plane requires line1_id and line2_id only; other modes cannot be mixed");
                RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id",
                    "line1_id", "line2_id" }, "operation.prepare_datum_plane two_lines params");
                request.Mode = "two_lines";
                request.Line1Id = NormalizeRequiredString(RequiredString(args, "line1_id"),
                    "line1_id", MaxOperationIdChars);
                request.Line2Id = NormalizeRequiredString(RequiredString(args, "line2_id"),
                    "line2_id", MaxOperationIdChars);
                if (String.Equals(request.Line1Id, request.Line2Id, StringComparison.Ordinal))
                    throw new BridgeException("INVALID_REQUEST", "line1_id and line2_id must identify distinct native Lines");
                normalized.Add("line1_id", request.Line1Id);
                normalized.Add("line2_id", request.Line2Id);
                normalized.Add("mode", "two_lines");
                return request;
            }
            if (hasAnyAngleField)
            {
                if (hasDatumReference == hasFaceReference || !hasAxisLine || !hasAngle || hasDistance || hasAnyFixedField)
                    throw new BridgeException("INVALID_REQUEST", "angle datum plane requires angle_deg, axis_line_id, and exactly one support reference; other modes cannot be mixed");
                string referenceKey = hasDatumReference ? "support_datum_plane_id" : "support_face_id";
                List<string> allowed = new List<string>(new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id",
                    referenceKey, "axis_line_id", "angle_deg" });
                if (hasFaceReference && supportFaceTagSpecified) allowed.Add("support_face_tag");
                RequireExactKeys(args, allowed.ToArray(), "operation.prepare_datum_plane angle params");
                request.Mode = "angle";
                if (hasDatumReference)
                {
                    request.SupportDatumPlaneId = NormalizeRequiredString(RequiredString(args, referenceKey),
                        referenceKey, MaxOperationIdChars);
                    normalized.Add(referenceKey, request.SupportDatumPlaneId);
                }
                else
                {
                    request.SupportFaceId = NormalizeRequiredString(RequiredString(args, referenceKey),
                        referenceKey, MaxOperationIdChars);
                    normalized.Add(referenceKey, request.SupportFaceId);
                    if (supportFaceTagSpecified)
                    {
                        request.SupportFaceTag = NormalizeRequiredString(RequiredString(args, "support_face_tag"),
                            "support_face_tag", MaxOperationIdChars);
                        normalized.Add("support_face_tag", request.SupportFaceTag);
                    }
                }
                request.AxisLineId = NormalizeRequiredString(RequiredString(args, "axis_line_id"),
                    "axis_line_id", MaxOperationIdChars);
                object angleValue;
                if (!args.TryGetValue("angle_deg", out angleValue) ||
                    !M2Json.TryGetFiniteNumber(angleValue, out request.AngleDeg) ||
                    request.AngleDeg < -180.0 || request.AngleDeg > 180.0)
                    throw new BridgeException("INVALID_REQUEST", "angle_deg must be a finite number between -180 and 180 degrees, inclusive");
                normalized.Add("axis_line_id", request.AxisLineId);
                normalized.Add("angle_deg", NormalizeNumberField(angleValue, "angle_deg"));
                normalized.Add("mode", "angle");
                return request;
            }
            if (hasAnyOffsetField)
            {
                if (hasAnyFixedField)
                    throw new BridgeException("INVALID_REQUEST", "offset datum plane fields cannot be mixed with origin_mm, normal, or x_axis");
                if (hasDatumReference == hasFaceReference || !hasDistance)
                    throw new BridgeException("INVALID_REQUEST", "offset datum plane requires distance_mm and exactly one support_datum_plane_id or support_face_id");
                List<string> allowed = new List<string>(new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id",
                    "distance_mm", hasDatumReference ? "support_datum_plane_id" : "support_face_id" });
                if (hasFaceReference && supportFaceTagSpecified) allowed.Add("support_face_tag");
                RequireExactKeys(args, allowed.ToArray(), "operation.prepare_datum_plane offset params");
                object distanceValue;
                if (!args.TryGetValue("distance_mm", out distanceValue) || !M2Json.TryGetFiniteNumber(distanceValue, out request.DistanceMm))
                    throw new BridgeException("INVALID_REQUEST", "distance_mm must be a finite JSON number in millimeters");
                request.Mode = "offset";
                if (hasDatumReference)
                {
                    request.SupportDatumPlaneId = NormalizeRequiredString(RequiredString(args, "support_datum_plane_id"),
                        "support_datum_plane_id", MaxOperationIdChars);
                    normalized.Add("support_datum_plane_id", request.SupportDatumPlaneId);
                }
                else
                {
                    request.SupportFaceId = NormalizeRequiredString(RequiredString(args, "support_face_id"),
                        "support_face_id", MaxOperationIdChars);
                    normalized.Add("support_face_id", request.SupportFaceId);
                    if (supportFaceTagSpecified)
                    {
                        request.SupportFaceTag = NormalizeRequiredString(RequiredString(args, "support_face_tag"),
                            "support_face_tag", MaxOperationIdChars);
                        normalized.Add("support_face_tag", request.SupportFaceTag);
                    }
                }
                normalized.Add("mode", "offset");
                normalized.Add("distance_mm", NormalizeNumberField(distanceValue, "distance_mm"));
            }
            else
            {
                RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id",
                    "origin_mm", "normal", "x_axis" }, "operation.prepare_datum_plane params");
                request.Mode = "fixed";
                request.OriginMm = RequiredNumberArray(args, "origin_mm", 3);
                request.Normal = RequiredNumberArray(args, "normal", 3);
                request.XAxis = RequiredNumberArray(args, "x_axis", 3);
                normalized.Add("origin_mm", CanonicalNumberList(args["origin_mm"], 3, "origin_mm"));
                normalized.Add("normal", CanonicalNumberList(args["normal"], 3, "normal"));
                normalized.Add("x_axis", CanonicalNumberList(args["x_axis"], 3, "x_axis"));
            }
            return request;
        }

        private static M3ExtrudeRequest ParseM3ExtrudeRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            string mode = RequiredString(args, "mode");
            string direction = RequiredString(args, "direction");
            string extent = RequiredString(args, "extent");
            string profileOutput = args.ContainsKey("profile_output")
                ? RequiredString(args, "profile_output") : "solid";
            if (mode != "create" && mode != "unite" && mode != "subtract")
                throw new BridgeException("INVALID_REQUEST", "mode must be create, unite, or subtract");
            if (direction != "positive" && direction != "negative")
                throw new BridgeException("INVALID_REQUEST", "direction must be positive or negative");
            if (extent != "finite" && extent != "through_all")
                throw new BridgeException("INVALID_REQUEST", "extent must be finite or through_all");
            if (profileOutput != "solid" && profileOutput != "sheet")
                throw new BridgeException("INVALID_REQUEST", "profile_output must be solid or sheet");
            if (mode == "create" && extent != "finite")
                throw new BridgeException("INVALID_REQUEST", "create extrusion supports only finite extent");
            if (profileOutput == "sheet" && (mode != "create" || extent != "finite"))
                throw new BridgeException("INVALID_REQUEST", "sheet output is supported only for finite create extrusion");

            List<string> keys = new List<string>(new string[] {
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id", "mode", "direction", "extent"
            });
            if (args.ContainsKey("profile_output")) keys.Add("profile_output");
            bool hasDepth = args.ContainsKey("depth_mm");
            bool hasStart = args.ContainsKey("start_offset_mm");
            bool hasEnd = args.ContainsKey("end_offset_mm");
            if (hasDepth) keys.Add("depth_mm");
            if (hasStart) keys.Add("start_offset_mm");
            if (hasEnd) keys.Add("end_offset_mm");
            if (mode != "create") keys.Add("target_body_id");
            RequireExactKeys(args, keys.ToArray(), "operation.prepare_extrude params");

            double startOffsetMm;
            double endOffsetMm = 0.0;
            double depthMm = 0.0;
            if (extent == "finite")
            {
                if (hasDepth && (hasStart || hasEnd))
                    throw new BridgeException("INVALID_REQUEST", "depth_mm cannot be combined with start_offset_mm or end_offset_mm");
                if (hasDepth)
                {
                    depthMm = RequiredPositiveNumber(args, "depth_mm");
                    startOffsetMm = 0.0;
                    endOffsetMm = depthMm;
                }
                else if (hasStart && hasEnd)
                {
                    startOffsetMm = RequiredFiniteNumber(args, "start_offset_mm");
                    endOffsetMm = RequiredFiniteNumber(args, "end_offset_mm");
                    double spanMm = endOffsetMm - startOffsetMm;
                    if (Double.IsNaN(spanMm) || Double.IsInfinity(spanMm) || spanMm <= 0.0)
                        throw new BridgeException("INVALID_REQUEST", "end_offset_mm must be greater than start_offset_mm with a finite span");
                    depthMm = endOffsetMm - startOffsetMm;
                }
                else
                    throw new BridgeException("INVALID_REQUEST", "finite extent requires depth_mm or both start_offset_mm and end_offset_mm");
            }
            else
            {
                if (hasDepth || hasEnd)
                    throw new BridgeException("INVALID_REQUEST", "through_all extent does not accept depth_mm or end_offset_mm");
                if (profileOutput != "solid")
                    throw new BridgeException("INVALID_REQUEST", "through_all extent supports solid output only");
                startOffsetMm = hasStart ? RequiredFiniteNumber(args, "start_offset_mm") : 0.0;
            }

            M3ExtrudeRequest request = new M3ExtrudeRequest();
            request.RevisionId = revisionId;
            request.SketchId = NormalizeRequiredString(RequiredString(args, "sketch_id"), "sketch_id", 4096);
            request.Mode = mode;
            request.Direction = direction;
            request.Extent = extent;
            request.ProfileOutput = profileOutput;
            request.DepthMm = depthMm;
            request.StartOffsetMm = startOffsetMm;
            request.EndOffsetMm = endOffsetMm;
            request.TargetBodyId = mode == "create" ? null : NormalizeRequiredString(RequiredString(args, "target_body_id"), "target_body_id", 4096);
            normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            normalized.Add("ownership_id", owner);
            normalized.Add("binding_epoch", epoch);
            normalized.Add("revision_id", revisionId);
            normalized.Add("sketch_id", request.SketchId);
            normalized.Add("mode", mode);
            normalized.Add("direction", direction);
            normalized.Add("extent", extent);
            normalized.Add("profile_output", profileOutput);
            normalized.Add("start_offset_mm", NormalizeNumberField(startOffsetMm, "start_offset_mm"));
            if (extent == "finite") normalized.Add("end_offset_mm", NormalizeNumberField(endOffsetMm, "end_offset_mm"));
            if (request.TargetBodyId != null) normalized.Add("target_body_id", request.TargetBodyId);
            return request;
        }

        private static M3RevolveRequest ParseM3RevolveRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            string mode = RequiredString(args, "mode");
            string profileOutput = args.ContainsKey("profile_output")
                ? RequiredString(args, "profile_output") : "solid";
            if (mode != "create" && mode != "unite" && mode != "subtract")
                throw new BridgeException("INVALID_REQUEST", "mode must be create, unite, or subtract");
            if (profileOutput != "solid" && profileOutput != "sheet")
                throw new BridgeException("INVALID_REQUEST", "profile_output must be solid or sheet");
            if (profileOutput == "sheet" && mode != "create")
                throw new BridgeException("INVALID_REQUEST", "sheet output is supported only for create revolve operations");

            List<string> allowed = new List<string>(new string[] {
                "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id", "mode",
                "axis_point_mm", "axis_direction", "start_angle_deg", "end_angle_deg"
            });
            if (args.ContainsKey("profile_output")) allowed.Add("profile_output");
            if (mode != "create") allowed.Add("target_body_id");
            RequireExactKeys(args, allowed.ToArray(), "operation.prepare_revolve params");

            double[] point = RequiredNumberArray(args, "axis_point_mm", 3);
            for (int i = 0; i < point.Length; i++)
                if (Math.Abs(point[i]) > 1000000.0)
                    throw new BridgeException("INVALID_REQUEST", "axis_point_mm coordinates must have an absolute value no greater than 1000000 mm");

            double[] direction = RequiredNumberArray(args, "axis_direction", 3);
            double scale = Math.Max(Math.Abs(direction[0]), Math.Max(Math.Abs(direction[1]), Math.Abs(direction[2])));
            if (scale <= 0.0) throw new BridgeException("INVALID_REQUEST", "axis_direction must be nonzero");
            double x = direction[0] / scale;
            double y = direction[1] / scale;
            double z = direction[2] / scale;
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (length <= 0.0 || Double.IsNaN(length) || Double.IsInfinity(length))
                throw new BridgeException("INVALID_REQUEST", "axis_direction must be a finite nonzero world vector");
            direction = new double[] { x / length, y / length, z / length };

            double start = RequiredFiniteNumber(args, "start_angle_deg");
            double end = RequiredFiniteNumber(args, "end_angle_deg");
            if (start < -360.0 || start > 360.0 || end < -360.0 || end > 360.0)
                throw new BridgeException("INVALID_REQUEST", "start_angle_deg and end_angle_deg must be in [-360, 360]");
            double span = end - start;
            if (Double.IsNaN(span) || Double.IsInfinity(span) || Math.Abs(span) <= 1.0e-9 || Math.Abs(span) > 360.0)
                throw new BridgeException("INVALID_REQUEST", "signed angle span must be nonzero and have magnitude at most 360 degrees");

            M3RevolveRequest request = new M3RevolveRequest();
            request.RevisionId = revisionId;
            request.SketchId = NormalizeRequiredString(RequiredString(args, "sketch_id"), "sketch_id", 4096);
            request.Mode = mode;
            request.ProfileOutput = profileOutput;
            request.AxisPointMm = point;
            request.AxisDirection = direction;
            request.StartAngleDeg = start;
            request.EndAngleDeg = end;
            request.TargetBodyId = mode == "create" ? null : NormalizeRequiredString(RequiredString(args, "target_body_id"), "target_body_id", 4096);
            normalized = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "ownership_id", owner },
                { "binding_epoch", epoch },
                { "revision_id", revisionId },
                { "sketch_id", request.SketchId },
                { "mode", mode },
                { "profile_output", profileOutput },
                { "axis_point_mm", CanonicalNumberList(point, 3, "axis_point_mm") },
                { "axis_direction", CanonicalNumberList(direction, 3, "axis_direction") },
                { "start_angle_deg", NormalizeNumberField(start, "start_angle_deg") },
                { "end_angle_deg", NormalizeNumberField(end, "end_angle_deg") }
            };
            if (request.TargetBodyId != null) normalized.Add("target_body_id", request.TargetBodyId);
            return request;
        }

        private static M3SweepRequest ParseM3SweepRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "sketch_id",
                "path_curve_ids", "profile_output", "orientation_mode", "twist_angle_deg" }, "operation.prepare_sweep params");
            string sketch = NormalizeRequiredString(RequiredString(args, "sketch_id"), "sketch_id", 4096);
            object raw;
            if (!args.TryGetValue("path_curve_ids", out raw)) throw new BridgeException("INVALID_REQUEST", "path_curve_ids is required");
            System.Collections.IList values = raw as System.Collections.IList;
            if (values == null || values.Count < 1 || values.Count > 32)
                throw new BridgeException("INVALID_REQUEST", "path_curve_ids must contain 1 to 32 IDs");
            List<string> ids = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in values)
            {
                string id = value as string;
                if (id == null) throw new BridgeException("INVALID_REQUEST", "path curve ID must be a string");
                id = NormalizeRequiredString(id, "path_curve_ids", 4096);
                if (!seen.Add(id)) throw new BridgeException("INVALID_REQUEST", "path_curve_ids must be unique");
                ids.Add(id);
            }
            string profile = RequiredString(args, "profile_output");
            if (profile != "solid" && profile != "sheet") throw new BridgeException("INVALID_REQUEST", "profile_output must be solid or sheet");
            string orientation = RequiredString(args, "orientation_mode");
            if (orientation != "transport" && orientation != "linear_twist") throw new BridgeException("INVALID_REQUEST", "orientation_mode must be transport or linear_twist");
            double twist = RequiredFiniteNumber(args, "twist_angle_deg");
            if (twist < -90.0 || twist > 90.0) throw new BridgeException("INVALID_REQUEST", "twist_angle_deg must be in [-90, 90]");
            if (orientation == "transport" && twist != 0.0) throw new BridgeException("INVALID_REQUEST", "transport orientation requires twist_angle_deg to be zero");
            M3SweepRequest request = new M3SweepRequest { RevisionId = revisionId, SketchId = sketch, PathCurveIds = ids.ToArray(),
                ProfileOutput = profile, OrientationMode = orientation, TwistAngleDeg = twist };
            normalized = new Dictionary<string, object>(StringComparer.Ordinal) { { "ownership_id", owner }, { "binding_epoch", epoch },
                { "revision_id", revisionId }, { "sketch_id", sketch }, { "path_curve_ids", ids.ToArray() },
                { "profile_output", profile }, { "orientation_mode", orientation },
                { "twist_angle_deg", NormalizeNumberField(args["twist_angle_deg"], "twist_angle_deg") } };
            return request;
        }

        private static M3BooleanRequest ParseM3BooleanRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "operation", "target_body_id", "tool_body_ids", "retain_tools" }, "operation.prepare_boolean params");
            string operation = RequiredString(args, "operation");
            if (operation != "unite" && operation != "subtract" && operation != "intersect")
                throw new BridgeException("INVALID_REQUEST", "unsupported boolean operation");
            string target = NormalizeRequiredString(RequiredString(args, "target_body_id"), "target_body_id", 4096);
            object raw; if (!args.TryGetValue("tool_body_ids", out raw)) throw new BridgeException("INVALID_REQUEST", "tool_body_ids is required");
            System.Collections.IList values = raw as System.Collections.IList;
            if (values == null || values.Count < 1 || values.Count > 32) throw new BridgeException("INVALID_REQUEST", "tool_body_ids must contain 1 to 32 IDs");
            List<string> tools = new List<string>(); HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in values) {
                string id = value as string;
                if (id == null) throw new BridgeException("INVALID_REQUEST", "tool body ID must be a string");
                id = NormalizeRequiredString(id, "tool_body_ids", 4096);
                if (id == target || !seen.Add(id)) throw new BridgeException("INVALID_REQUEST", "tool body IDs must be unique and distinct from target");
                tools.Add(id);
            }
            object retain; if (!args.TryGetValue("retain_tools", out retain) || !(retain is bool))
                throw new BridgeException("INVALID_REQUEST", "retain_tools must be a boolean");
            M3BooleanRequest request = new M3BooleanRequest { RevisionId = revisionId, Operation = operation,
                TargetBodyId = target, ToolBodyIds = tools.ToArray(), RetainTools = (bool)retain };
            normalized = new Dictionary<string, object>(StringComparer.Ordinal) { { "ownership_id", owner }, { "binding_epoch", epoch },
                { "revision_id", revisionId }, { "operation", operation }, { "target_body_id", target },
                { "tool_body_ids", tools.ToArray() }, { "retain_tools", (bool)retain } };
            return request;
        }

        private static M3SpatialCurveRequest ParseM3SpatialCurveRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            string type = RequiredString(args, "curve_type");
            string mode = type == "arc" ? RequiredString(args, "definition_mode") : null;
            List<string> keys = new List<string>(new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "curve_type" });
            if (type == "line") keys.AddRange(new string[] { "start_mm", "end_mm" });
            else if (type == "arc" && mode == "three_points") keys.AddRange(new string[] { "definition_mode", "start_mm", "through_mm", "end_mm" });
            else if (type == "circle" || (type == "arc" && mode == "center"))
            {
                if (type == "arc") keys.Add("definition_mode");
                keys.AddRange(new string[] { "center_mm", "normal", "x_axis", "radius_mm" });
                if (type == "arc") keys.AddRange(new string[] { "start_angle_deg", "end_angle_deg" });
            }
            else throw new BridgeException("INVALID_REQUEST", "unsupported spatial curve type or definition mode");
            RequireExactKeys(args, keys.ToArray(), "operation.prepare_spatial_curve params");
            M3SpatialCurveRequest r = new M3SpatialCurveRequest { RevisionId=revisionId, CurveType=type, DefinitionMode=mode };
            normalized = new Dictionary<string, object>(StringComparer.Ordinal) { { "ownership_id", owner }, { "binding_epoch", epoch }, { "revision_id", revisionId }, { "curve_type", type } };
            if (mode != null) normalized.Add("definition_mode", mode);
            foreach (string key in new string[] { "start_mm", "through_mm", "end_mm", "center_mm", "normal", "x_axis" })
            {
                if (!args.ContainsKey(key)) continue;
                double[] v=RequiredNumberArray(args,key,3); normalized.Add(key,CanonicalNumberList(args[key],3,key));
                if(key=="start_mm")r.StartMm=v; else if(key=="through_mm")r.ThroughMm=v; else if(key=="end_mm")r.EndMm=v;
                else if(key=="center_mm")r.CenterMm=v; else if(key=="normal")r.Normal=v; else r.XAxis=v;
            }
            foreach (string key in new string[] { "radius_mm", "start_angle_deg", "end_angle_deg" })
            {
                if (!args.ContainsKey(key)) continue;
                double v=RequiredFiniteNumber(args,key); normalized.Add(key,NormalizeNumberField(args[key],key));
                if(key=="radius_mm")r.RadiusMm=v; else if(key=="start_angle_deg")r.StartAngleDeg=v; else r.EndAngleDeg=v;
            }
            try { F20SpatialGeometry.FromRequest(r); }
            catch (ArgumentException ex) { throw new BridgeException("INVALID_REQUEST", ex.Message); }
            return r;
        }

        private static M3FeatureMirrorRequest ParseM3FeatureMirrorRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            bool datum = args.ContainsKey("datum_plane_id");
            bool origin = args.ContainsKey("plane_origin_mm");
            bool normal = args.ContainsKey("plane_normal");
            if ((datum && (origin || normal)) || (!datum && !(origin && normal)))
                throw new BridgeException("INVALID_REQUEST", "supply datum_plane_id or both plane_origin_mm and plane_normal");
            RequireExactKeys(args, datum
                ? new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "seed_feature_id", "datum_plane_id" }
                : new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "seed_feature_id", "plane_origin_mm", "plane_normal" },
                "operation.prepare_feature_mirror params");
            string seed = NormalizeRequiredString(RequiredString(args, "seed_feature_id"), "seed_feature_id", 4096);
            M3FeatureMirrorRequest request = new M3FeatureMirrorRequest { RevisionId = revisionId, SeedFeatureId = seed };
            normalized = new Dictionary<string, object>(StringComparer.Ordinal) { { "ownership_id", owner }, { "binding_epoch", epoch },
                { "revision_id", revisionId }, { "seed_feature_id", seed } };
            if (datum)
            {
                request.DatumPlaneId = NormalizeRequiredString(RequiredString(args, "datum_plane_id"), "datum_plane_id", 4096);
                normalized.Add("datum_plane_id", request.DatumPlaneId);
            }
            else
            {
                request.PlaneOriginMm = RequiredNumberArray(args, "plane_origin_mm", 3);
                request.PlaneNormal = RequiredNumberArray(args, "plane_normal", 3);
                F18RequireUnit(request.PlaneNormal, "plane_normal");
                normalized.Add("plane_origin_mm", CanonicalNumberList(args["plane_origin_mm"], 3, "plane_origin_mm"));
                normalized.Add("plane_normal", CanonicalNumberList(args["plane_normal"], 3, "plane_normal"));
            }
            return request;
        }

        private static M3FeaturePatternRequest ParseM3FeaturePatternRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            string mode = RequiredString(args, "mode");
            List<string> keys = new List<string>(new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "seed_feature_id", "mode", "count1" });
            if (mode == "linear" || mode == "rectangular")
            {
                keys.Add("direction1"); keys.Add("pitch1_mm");
                if (mode == "rectangular") { keys.Add("direction2"); keys.Add("pitch2_mm"); keys.Add("count2"); }
            }
            else if (mode == "circular") { keys.Add("axis_origin_mm"); keys.Add("axis_direction"); keys.Add("angle_deg"); }
            else throw new BridgeException("INVALID_REQUEST", "unsupported feature pattern mode");
            RequireExactKeys(args, keys.ToArray(), "operation.prepare_feature_pattern params");
            string seed = NormalizeRequiredString(RequiredString(args, "seed_feature_id"), "seed_feature_id", 4096);
            int count1 = RequiredPositiveInt(args, "count1");
            if (count1 < 2 || count1 > 32) throw new BridgeException("INVALID_REQUEST", "count1 must be between 2 and 32");
            M3FeaturePatternRequest request = new M3FeaturePatternRequest { RevisionId = revisionId, SeedFeatureId = seed, Mode = mode, Count1 = count1 };
            normalized = new Dictionary<string, object>(StringComparer.Ordinal) { { "ownership_id", owner }, { "binding_epoch", epoch },
                { "revision_id", revisionId }, { "seed_feature_id", seed }, { "mode", mode }, { "count1", count1 } };
            if (mode == "linear" || mode == "rectangular")
            {
                request.Direction1 = RequiredNumberArray(args, "direction1", 3); F18RequireUnit(request.Direction1, "direction1");
                request.Pitch1Mm = RequiredPositiveNumber(args, "pitch1_mm");
                normalized.Add("direction1", CanonicalNumberList(args["direction1"], 3, "direction1"));
                normalized.Add("pitch1_mm", NormalizeNumberField(args["pitch1_mm"], "pitch1_mm"));
                if (mode == "rectangular")
                {
                    request.Direction2 = RequiredNumberArray(args, "direction2", 3); F18RequireUnit(request.Direction2, "direction2");
                    double dot = 0; for (int i = 0; i < 3; i++) dot += request.Direction1[i] * request.Direction2[i];
                    if (Math.Abs(dot) > 1e-9) throw new BridgeException("INVALID_REQUEST", "rectangular directions must be orthogonal");
                    request.Count2 = RequiredPositiveInt(args, "count2");
                    if (request.Count2 < 2 || request.Count2 > 32 || count1 * request.Count2 > 32)
                        throw new BridgeException("INVALID_REQUEST", "count2 must be 2 to 32 and count1 * count2 at most 32");
                    request.Pitch2Mm = RequiredPositiveNumber(args, "pitch2_mm");
                    normalized.Add("direction2", CanonicalNumberList(args["direction2"], 3, "direction2"));
                    normalized.Add("pitch2_mm", NormalizeNumberField(args["pitch2_mm"], "pitch2_mm")); normalized.Add("count2", request.Count2);
                }
            }
            else
            {
                request.AxisOriginMm = RequiredNumberArray(args, "axis_origin_mm", 3);
                request.AxisDirection = RequiredNumberArray(args, "axis_direction", 3); F18RequireUnit(request.AxisDirection, "axis_direction");
                request.AngleDeg = RequiredFiniteNumber(args, "angle_deg");
                if (request.AngleDeg == 0 || Math.Abs(request.AngleDeg) * (count1 - 1) >= 360)
                    throw new BridgeException("INVALID_REQUEST", "absolute total step angle must be nonzero and less than 360 degrees");
                normalized.Add("axis_origin_mm", CanonicalNumberList(args["axis_origin_mm"], 3, "axis_origin_mm"));
                normalized.Add("axis_direction", CanonicalNumberList(args["axis_direction"], 3, "axis_direction"));
                normalized.Add("angle_deg", NormalizeNumberField(args["angle_deg"], "angle_deg"));
            }
            return request;
        }

        private static void F18RequireUnit(double[] v, string name)
        {
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (Double.IsInfinity(length) || Double.IsNaN(length) || Math.Abs(length - 1) > 1e-9)
                throw new BridgeException("INVALID_REQUEST", name + " must be a unit vector");
        }

        private static M3ChamferRequest ParseM3ChamferRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            string mode = RequiredString(args, "mode");
            List<string> fields = new List<string>(new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id", "edge_ids", "mode", "reference_axis", "distance_mm" });
            if (mode == "two_distances") fields.Add("second_distance_mm");
            else if (mode == "distance_angle") fields.Add("angle_deg");
            else if (mode != "equal_distance") throw new BridgeException("INVALID_REQUEST", "unsupported chamfer mode");
            RequireExactKeys(args, fields.ToArray(), "operation.prepare_chamfer params");
            string target = NormalizeRequiredString(RequiredString(args, "target_body_id"), "target_body_id", 4096);
            string axis = RequiredString(args, "reference_axis");
            if (axis != "x" && axis != "y" && axis != "z") throw new BridgeException("INVALID_REQUEST", "reference_axis must be x, y or z");
            object raw; args.TryGetValue("edge_ids", out raw);
            System.Collections.IList values = raw as System.Collections.IList;
            if (values == null || values.Count != 1 || !(values[0] is string)) throw new BridgeException("INVALID_REQUEST", "exactly one edge ID is required");
            string edge = NormalizeRequiredString((string)values[0], "edge_ids", 4096);
            double distance = RequiredPositiveNumber(args, "distance_mm");
            double second = mode == "two_distances" ? RequiredPositiveNumber(args, "second_distance_mm") : 0;
            double angle = mode == "distance_angle" ? RequiredPositiveNumber(args, "angle_deg") : 0;
            if (mode == "distance_angle" && angle >= 90) throw new BridgeException("INVALID_REQUEST", "angle_deg must be less than 90");
            M3ChamferRequest request = new M3ChamferRequest { RevisionId = revisionId, TargetBodyId = target,
                EdgeIds = new string[] { edge }, Mode = mode, ReferenceAxis = axis, DistanceMm = distance, SecondDistanceMm = second, AngleDeg = angle };
            normalized = new Dictionary<string, object>(StringComparer.Ordinal) { { "ownership_id", owner }, { "binding_epoch", epoch },
                { "revision_id", revisionId }, { "target_body_id", target }, { "edge_ids", request.EdgeIds },
                { "mode", mode }, { "reference_axis", axis }, { "distance_mm", NormalizeNumberField(args["distance_mm"], "distance_mm") } };
            if (mode == "two_distances") normalized.Add("second_distance_mm", NormalizeNumberField(args["second_distance_mm"], "second_distance_mm"));
            if (mode == "distance_angle") normalized.Add("angle_deg", NormalizeNumberField(args["angle_deg"], "angle_deg"));
            return request;
        }

        private static M3EdgeBlendRequest ParseM3EdgeBlendRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id", "edge_ids", "radius_mm" }, "operation.prepare_edge_blend params");
            string target = NormalizeRequiredString(RequiredString(args, "target_body_id"), "target_body_id", 4096);
            object raw;
            if (!args.TryGetValue("edge_ids", out raw)) throw new BridgeException("INVALID_REQUEST", "edge_ids is required");
            System.Collections.IList values = raw as System.Collections.IList;
            if (values == null || values.Count < 1 || values.Count > 32)
                throw new BridgeException("INVALID_REQUEST", "edge_ids must contain 1 to 32 IDs");
            List<string> edges = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in values)
            {
                string id = value as string;
                if (id == null) throw new BridgeException("INVALID_REQUEST", "edge ID must be a string");
                id = NormalizeRequiredString(id, "edge_ids", 4096);
                if (!seen.Add(id)) throw new BridgeException("INVALID_REQUEST", "edge IDs must be unique");
                edges.Add(id);
            }
            double radius = RequiredPositiveNumber(args, "radius_mm");
            M3EdgeBlendRequest request = new M3EdgeBlendRequest { RevisionId = revisionId,
                TargetBodyId = target, EdgeIds = edges.ToArray(), RadiusMm = radius };
            normalized = new Dictionary<string, object>(StringComparer.Ordinal) { { "ownership_id", owner }, { "binding_epoch", epoch },
                { "revision_id", revisionId }, { "target_body_id", target }, { "edge_ids", request.EdgeIds },
                { "radius_mm", NormalizeNumberField(args["radius_mm"], "radius_mm") } };
            return request;
        }

        private static M3HoleRequest ParseM3HoleRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            string kind = RequiredString(args, "kind");
            string depthMode = RequiredString(args, "depth_mode");
            if (kind != "simple" && kind != "counterbore" && kind != "countersink" && kind != "threaded")
                throw new BridgeException("INVALID_REQUEST", "unsupported hole kind");
            if (depthMode != "blind" && depthMode != "through")
                throw new BridgeException("INVALID_REQUEST", "depth_mode must be blind or through");
            List<string> fields = new List<string>(new string[] { "operation_id", "ownership_id", "binding_epoch",
                "revision_id", "target_body_id", "position_mm", "direction", "kind", "depth_mode" });
            if (depthMode == "blind") fields.Add("hole_depth_mm");
            if (kind != "threaded") fields.Add("diameter_mm");
            if (kind == "counterbore") fields.AddRange(new string[] { "counterbore_diameter_mm", "counterbore_depth_mm" });
            if (kind == "countersink") fields.AddRange(new string[] { "countersink_diameter_mm", "countersink_angle_deg" });
            if (kind == "threaded") fields.AddRange(new string[] { "thread_standard", "thread_size", "radial_engage", "pitch_mm", "hand", "thread_depth_mm" });
            RequireExactKeys(args, fields.ToArray(), "operation.prepare_hole params");
            double[] point = RequiredNumberArray(args, "position_mm", 3);
            for (int i = 0; i < 3; i++) if (Math.Abs(point[i]) > 1000000.0)
                throw new BridgeException("INVALID_REQUEST", "position_mm coordinates exceed 1000000 mm");
            double[] direction = RequiredNumberArray(args, "direction", 3);
            double scale = Math.Max(Math.Abs(direction[0]), Math.Max(Math.Abs(direction[1]), Math.Abs(direction[2])));
            if (scale <= 0) throw new BridgeException("INVALID_REQUEST", "direction must be nonzero");
            double x = direction[0] / scale, y = direction[1] / scale, z = direction[2] / scale;
            double length = Math.Sqrt(x*x + y*y + z*z);
            direction = new double[] { x/length, y/length, z/length };
            M3HoleRequest request = new M3HoleRequest();
            request.RevisionId = revisionId;
            request.TargetBodyId = NormalizeRequiredString(RequiredString(args, "target_body_id"), "target_body_id", 4096);
            request.Kind = kind; request.DepthMode = depthMode; request.PositionMm = point; request.Direction = direction;
            if (depthMode == "blind") request.HoleDepthMm = HolePositiveNumber(args, "hole_depth_mm");
            if (kind != "threaded") request.DiameterMm = HolePositiveNumber(args, "diameter_mm");
            if (kind == "counterbore") {
                request.CounterboreDiameterMm = HolePositiveNumber(args, "counterbore_diameter_mm");
                request.CounterboreDepthMm = HolePositiveNumber(args, "counterbore_depth_mm");
                if (request.CounterboreDiameterMm <= request.DiameterMm || (depthMode == "blind" && request.CounterboreDepthMm >= request.HoleDepthMm))
                    throw new BridgeException("INVALID_REQUEST", "counterbore must be wider and shallower than the bore");
            }
            if (kind == "countersink") {
                request.CountersinkDiameterMm = HolePositiveNumber(args, "countersink_diameter_mm");
                request.CountersinkAngleDeg = RequiredFiniteNumber(args, "countersink_angle_deg");
                if (request.CountersinkDiameterMm <= request.DiameterMm || request.CountersinkAngleDeg <= 0 || request.CountersinkAngleDeg >= 180)
                    throw new BridgeException("INVALID_REQUEST", "invalid countersink diameter or included angle");
                double coneAngle = Math.Atan2(request.CountersinkDiameterMm / 2 - request.DiameterMm / 2, request.HoleDepthMm);
                if (depthMode == "blind" && coneAngle >= request.CountersinkAngleDeg * Math.PI / 360)
                    throw new BridgeException("INVALID_REQUEST", "countersink must be shallower than the blind bore");
            }
            if (kind == "threaded") {
                request.ThreadStandard = NormalizeRequiredString(RequiredString(args, "thread_standard"), "thread_standard", 128);
                request.ThreadSize = NormalizeRequiredString(RequiredString(args, "thread_size"), "thread_size", 128);
                request.RadialEngage = NormalizeRequiredString(RequiredString(args, "radial_engage"), "radial_engage", 128);
                request.Hand = RequiredString(args, "hand");
                if (request.Hand != "right" && request.Hand != "left") throw new BridgeException("INVALID_REQUEST", "hand must be right or left");
                request.PitchMm = HolePositiveNumber(args, "pitch_mm");
                request.ThreadDepthMm = HolePositiveNumber(args, "thread_depth_mm");
                if (depthMode == "blind" && request.ThreadDepthMm > request.HoleDepthMm)
                    throw new BridgeException("INVALID_REQUEST", "thread depth exceeds bore depth");
            }
            normalized = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "ownership_id", owner }, { "binding_epoch", epoch }, { "revision_id", revisionId },
                { "target_body_id", request.TargetBodyId }, { "kind", kind }, { "depth_mode", depthMode },
                { "position_mm", CanonicalNumberList(point, 3, "position_mm") },
                { "direction", CanonicalNumberList(direction, 3, "direction") }
            };
            foreach (string field in fields) {
                if (field == "hole_depth_mm" || field == "diameter_mm" || field == "counterbore_diameter_mm" ||
                    field == "counterbore_depth_mm" || field == "countersink_diameter_mm" || field == "countersink_angle_deg" ||
                    field == "pitch_mm" || field == "thread_depth_mm") normalized.Add(field, NormalizeNumberField(args[field], field));
            }
            if (kind == "threaded") { normalized.Add("thread_standard", request.ThreadStandard); normalized.Add("thread_size", request.ThreadSize); normalized.Add("radial_engage", request.RadialEngage); normalized.Add("hand", request.Hand); }
            return request;
        }

        private static double HolePositiveNumber(Dictionary<string, object> args, string name)
        {
            double value = RequiredPositiveNumber(args, name);
            if (value > 1000000.0) throw new BridgeException("INVALID_REQUEST", name + " exceeds 1000000 mm");
            return value;
        }

        private static M7ThreadRequest ParseM7ThreadRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id",
                "axis", "axis_origin_mm", "start_mm", "length_mm", "handedness", "direction", "major_diameter_mm", "minor_diameter_mm", "pitch_mm" },
                "operation.prepare_thread params");
            normalized = NormalizeM7Common(args, revisionId, owner, epoch);
            M7ThreadRequest request = new M7ThreadRequest {
                RevisionId = revisionId, TargetBodyId = (string)normalized["target_body_id"], Axis = (string)normalized["axis"],
                AxisOriginMm = RequiredNumberArray(args, "axis_origin_mm", 3), StartMm = RequiredM7Number(args, "start_mm", false),
                LengthMm = RequiredM7Number(args, "length_mm", true), Handedness = (string)normalized["handedness"],
                Direction = RequiredString(args, "direction"), MajorDiameterMm = RequiredM7Number(args, "major_diameter_mm", true),
                MinorDiameterMm = RequiredM7Number(args, "minor_diameter_mm", true), PitchMm = RequiredM7Number(args, "pitch_mm", true)
            };
            if (request.Direction != "positive" && request.Direction != "negative")
                throw new BridgeException("INVALID_REQUEST", "direction must be positive or negative");
            if (request.MinorDiameterMm >= request.MajorDiameterMm || request.LengthMm / request.PitchMm > 500.0)
                throw new BridgeException("INVALID_REQUEST", "minor diameter must be below major diameter; thread length/pitch must not exceed 500");
            normalized.Add("direction", request.Direction);
            foreach (string key in new string[] { "major_diameter_mm", "minor_diameter_mm", "pitch_mm" })
                normalized.Add(key, NormalizeNumberField(args[key], key));
            return request;
        }

        private static M7HelicalGrooveRequest ParseM7GrooveRequest(Dictionary<string, object> args,
            string revisionId, string owner, int epoch, out Dictionary<string, object> normalized)
        {
            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "revision_id", "target_body_id", "axis", "axis_origin_mm",
                "start_mm", "length_mm", "handedness", "lead_mm", "root_diameter_mm", "reference_diameter_mm", "outer_diameter_mm", "space_width_mm", "flank_angle_deg", "phase_deg" },
                "operation.prepare_helical_groove params");
            normalized = NormalizeM7Common(args, revisionId, owner, epoch);
            M7HelicalGrooveRequest request = new M7HelicalGrooveRequest {
                RevisionId = revisionId, TargetBodyId = (string)normalized["target_body_id"], Axis = (string)normalized["axis"],
                AxisOriginMm = RequiredNumberArray(args, "axis_origin_mm", 3), StartMm = RequiredM7Number(args, "start_mm", false),
                LengthMm = RequiredM7Number(args, "length_mm", true), Handedness = (string)normalized["handedness"],
                LeadMm = RequiredM7Number(args, "lead_mm", true), RootDiameterMm = RequiredM7Number(args, "root_diameter_mm", true),
                ReferenceDiameterMm = RequiredM7Number(args, "reference_diameter_mm", true), OuterDiameterMm = RequiredM7Number(args, "outer_diameter_mm", true),
                SpaceWidthMm = RequiredM7Number(args, "space_width_mm", true), FlankAngleDeg = RequiredM7Number(args, "flank_angle_deg", true),
                PhaseDeg = RequiredM7Number(args, "phase_deg", false)
            };
            if (!(request.RootDiameterMm < request.ReferenceDiameterMm && request.ReferenceDiameterMm < request.OuterDiameterMm) ||
                request.FlankAngleDeg >= 45.0 || Math.Abs(request.PhaseDeg) > 360.0 || request.LengthMm / request.LeadMm > 50.0)
                throw new BridgeException("INVALID_REQUEST", "groove diameters must increase; angle <45 degrees; |phase| <=360 degrees; length/lead <=50");
            double tangent = Math.Tan(request.FlankAngleDeg * Math.PI / 180.0);
            double rootHalf = request.SpaceWidthMm / 2.0 + (request.RootDiameterMm - request.ReferenceDiameterMm) / 2.0 * tangent;
            double outerWidth = request.SpaceWidthMm + (request.OuterDiameterMm - request.ReferenceDiameterMm) * tangent;
            if (rootHalf <= 0.0 || outerWidth >= request.LeadMm)
                throw new BridgeException("INVALID_REQUEST", "groove profile must have a positive root width and outer width smaller than its lead");
            foreach (string key in new string[] { "lead_mm", "root_diameter_mm", "reference_diameter_mm", "outer_diameter_mm", "space_width_mm", "flank_angle_deg", "phase_deg" })
                normalized.Add(key, NormalizeNumberField(args[key], key));
            return request;
        }

        private static Dictionary<string, object> NormalizeM7Common(Dictionary<string, object> args, string revisionId, string owner, int epoch)
        {
            string axis = RequiredString(args, "axis"), handedness = RequiredString(args, "handedness");
            if (axis != "X" && axis != "Y" && axis != "Z") throw new BridgeException("INVALID_REQUEST", "axis must be X, Y, or Z");
            if (handedness != "right" && handedness != "left") throw new BridgeException("INVALID_REQUEST", "handedness must be right or left");
            double[] origin = RequiredNumberArray(args, "axis_origin_mm", 3);
            foreach (double value in origin) if (Math.Abs(value) > 1000000.0)
                throw new BridgeException("INVALID_REQUEST", "axis_origin_mm coordinates must be within +/-1000000 mm");
            RequiredM7Number(args, "start_mm", false); RequiredM7Number(args, "length_mm", true);
            return new Dictionary<string, object>(StringComparer.Ordinal) {
                { "ownership_id", owner }, { "binding_epoch", epoch }, { "revision_id", revisionId },
                { "target_body_id", NormalizeRequiredString(RequiredString(args, "target_body_id"), "target_body_id", 4096) },
                { "axis", axis }, { "axis_origin_mm", CanonicalNumberList(args["axis_origin_mm"], 3, "axis_origin_mm") },
                { "start_mm", NormalizeNumberField(args["start_mm"], "start_mm") },
                { "length_mm", NormalizeNumberField(args["length_mm"], "length_mm") }, { "handedness", handedness }
            };
        }

        private static double RequiredM7Number(Dictionary<string, object> args, string name, bool positive)
        {
            object value; double result;
            if (!args.TryGetValue(name, out value) || !M2Json.TryGetFiniteNumber(value, out result) ||
                Math.Abs(result) > 1000000.0 || (positive && result <= 0.0))
                throw new BridgeException("INVALID_REQUEST", name + " must be a finite JSON number within +/-1000000" + (positive ? " and positive" : ""));
            return result;
        }

        private static List<object> CanonicalNumberList(double[] value, int expectedLength, string name)
        {
            if (value == null || value.Length != expectedLength)
                throw new BridgeException("INVALID_REQUEST", name + " must be an array of exactly " + expectedLength.ToString(CultureInfo.InvariantCulture) + " finite numbers");
            List<object> normalized = new List<object>();
            foreach (double item in value) normalized.Add(NormalizeNumberField(item, name));
            return normalized;
        }

        private static List<object> CanonicalNumberList(object value, int expectedLength, string name)
        {
            List<object> input = value as List<object>;
            if (input == null || input.Count != expectedLength)
                throw new BridgeException("INVALID_REQUEST", name + " must be an array of exactly " + expectedLength.ToString(CultureInfo.InvariantCulture) + " finite numbers");
            List<object> normalized = new List<object>();
            foreach (object item in input) normalized.Add(NormalizeNumberField(item, name));
            return normalized;
        }

        private static double[] RequiredNumberArray(Dictionary<string, object> args, string name, int expectedLength)
        {
            object value;
            List<object> items;
            if (!args.TryGetValue(name, out value) || (items = value as List<object>) == null || items.Count != expectedLength)
                throw new BridgeException("INVALID_REQUEST", name + " must be an array of exactly " + expectedLength.ToString(CultureInfo.InvariantCulture) + " finite numbers");
            double[] result = new double[expectedLength];
            for (int i = 0; i < expectedLength; i++)
                if (!M2Json.TryGetFiniteNumber(items[i], out result[i]))
                    throw new BridgeException("INVALID_REQUEST", name + " must contain only finite numbers");
            return result;
        }

        private static double RequiredPositiveNumber(Dictionary<string, object> args, string name)
        {
            object value;
            double result;
            if (!args.TryGetValue(name, out value) || !M2Json.TryGetFiniteNumber(value, out result) || result <= 0.0)
                throw new BridgeException("INVALID_REQUEST", name + " must be a positive finite number in mm");
            return result;
        }

        private static double RequiredFiniteNumber(Dictionary<string, object> args, string name)
        {
            object value;
            double result;
            if (!args.TryGetValue(name, out value) || !M2Json.TryGetFiniteNumber(value, out result))
                throw new BridgeException("INVALID_REQUEST", name + " must be a finite JSON number");
            return result;
        }

        private static void ValidateM3ArcAngles(double startAngleDeg, double sweepDeg, string label)
        {
            if (startAngleDeg < 0.0 || startAngleDeg >= 360.0)
                throw new BridgeException("INVALID_REQUEST", label + " start_angle_deg must be in [0, 360)");
            if (Math.Abs(sweepDeg) <= 1e-9 || Math.Abs(sweepDeg) >= 360.0)
                throw new BridgeException("INVALID_REQUEST", label + " sweep_deg must be nonzero and have magnitude less than 360 degrees");
        }

        private static string NormalizeNumberField(object value, string name)
        {
            string text;
            if (!M2Json.TryGetNumberText(value, out text))
                throw new BridgeException("INVALID_REQUEST", name + " must be a finite JSON number");
            return NormalizeFiniteDecimal(text, name);
        }

        private string ExecuteParameterPlan(object requestId, Dictionary<string, object> args)
        {
            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "plan_id" },
                "operation.execute_plan params");
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            string owner = NormalizeRequiredString(RequiredString(args, "ownership_id"), "ownership_id", MaxOperationIdChars);
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            string planId = NormalizeRequiredString(RequiredString(args, "plan_id"), "plan_id", MaxOperationIdChars);

            Dictionary<string, object> normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            normalized.Add("ownership_id", owner);
            normalized.Add("binding_epoch", epoch);
            normalized.Add("plan_id", planId);
            string requestKey = OperationRequestKey("execute_plan", normalized);
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                OperationRecord existing;
                if (_operations.TryGetValue(operationId, out existing))
                {
                    if (!String.Equals(existing.RequestKey, requestKey, StringComparison.Ordinal))
                        throw new BridgeException("OPERATION_ID_CONFLICT", "operation_id was already used with a different normalized request");
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(existing));
                }
                if (_writesHalted)
                    throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "model writes are halted until the unknown operation outcome is resolved");
                if (_stopping) throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                if (ActiveSaveCopyLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "plan execution is paused while a saved copy is being rebound");
                if (ActiveParameterExecutionLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "a parameter edit is already queued or running; query its operation_id before starting another");
                if (_ownershipState != "AI_OWNED" || !_acceptModelReads || !String.Equals(owner, _ownershipId, StringComparison.Ordinal) ||
                    epoch != _bindingEpoch || _binding == null)
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "plan execution requires current AI ownership and binding epoch");
                PreparedM3Plan m3Plan;
                bool hasM3Plan = _m3Plans.TryGetValue(planId, out m3Plan);
                if (hasM3Plan && !(_modelAdapter is IM3OperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX M3 sketch/extrude adapter is not loaded");
                if (!hasM3Plan && !(_modelAdapter is IM2OperationAdapter))
                    throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX parameter-edit adapter is not loaded");
                if (_operations.Count >= MaxOperations)
                    throw new BridgeException("OPERATION_LIMIT_REACHED", "operation record capacity is full for this bridge load");
                if (hasM3Plan)
                {
                    if (m3Plan.Consumed)
                        throw new BridgeException("PLAN_ALREADY_USED", "M3 sketch/extrude plan can be executed only once");
                    if (!String.Equals(m3Plan.OwnershipId, owner, StringComparison.Ordinal) || m3Plan.BindingEpoch != epoch ||
                        !Object.ReferenceEquals(m3Plan.Binding, _binding) || !String.Equals(m3Plan.SessionEpoch, _sessionEpoch, StringComparison.Ordinal) ||
                        !String.Equals(m3Plan.LoadInstanceId, _loadInstanceId, StringComparison.Ordinal))
                        throw new BridgeException("STALE_PLAN", "M3 plan ownership or bridge binding identity is stale");
                    M2ModelBinding m3Binding = _binding;
                    OperationRecord m3Operation = new OperationRecord();
                    m3Operation.Id = operationId;
                    m3Operation.Kind = "execute_" + m3Plan.AdapterSnapshot.Kind;
                    m3Operation.RequestKey = requestKey;
                    m3Operation.State = "QUEUED";
                    m3Operation.Cancellable = true;
                    m3Operation.Work = new DispatchWork();
                    m3Operation.Work.Operation = m3Operation;
                    m3Operation.Work.Execute = delegate { return PerformExecuteM3Plan(m3Operation, m3Plan, m3Binding); };
                    m3Plan.Consumed = true;
                    _operations.Add(operationId, m3Operation);
                    QueueDispatchLocked(m3Operation.Work);
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(m3Operation));
                }
                PreparedParameterPlan plan;
                if (!_plans.TryGetValue(planId, out plan))
                    throw new BridgeException("PLAN_NOT_FOUND", "plan_id is unknown to this bridge load");
                if (plan.Consumed)
                    throw new BridgeException("PLAN_ALREADY_USED", "parameter-edit plan can be executed only once");
                if (!String.Equals(plan.OwnershipId, owner, StringComparison.Ordinal) || plan.BindingEpoch != epoch ||
                    !Object.ReferenceEquals(plan.Binding, _binding) || !String.Equals(plan.SessionEpoch, _sessionEpoch, StringComparison.Ordinal) ||
                    !String.Equals(plan.LoadInstanceId, _loadInstanceId, StringComparison.Ordinal))
                    throw new BridgeException("STALE_PLAN", "plan ownership or bridge binding identity is stale");

                M2ModelBinding binding = _binding;
                OperationRecord operation = new OperationRecord();
                operation.Id = operationId;
                operation.Kind = "execute_parameter_edit";
                operation.RequestKey = requestKey;
                operation.State = "QUEUED";
                operation.Cancellable = true;
                operation.Work = new DispatchWork();
                operation.Work.Operation = operation;
                operation.Work.Execute = delegate { return PerformExecuteParameterPlan(operation, plan, binding); };
                plan.Consumed = true;
                _operations.Add(operationId, operation);
                QueueDispatchLocked(operation.Work);
                return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
            }
        }

        private string VerifyPart(object requestId, Dictionary<string, object> args)
        {
            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "execute_operation_id" },
                "operation.verify_part params");
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            string owner = NormalizeRequiredString(RequiredString(args, "ownership_id"), "ownership_id", MaxOperationIdChars);
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            string executeOperationId = NormalizeOperationId(RequiredString(args, "execute_operation_id"));
            Dictionary<string, object> normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            normalized.Add("ownership_id", owner);
            normalized.Add("binding_epoch", epoch);
            normalized.Add("execute_operation_id", executeOperationId);
            string requestKey = OperationRequestKey("verify_part", normalized);

            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                OperationRecord existing;
                if (_operations.TryGetValue(operationId, out existing))
                {
                    if (!String.Equals(existing.RequestKey, requestKey, StringComparison.Ordinal))
                        throw new BridgeException("OPERATION_ID_CONFLICT", "operation_id was already used with a different normalized request");
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(existing));
                }
                if (_writesHalted) throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "model operations are halted until the unknown outcome is resolved");
                if (_stopping) throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                if (ActiveSaveCopyLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "part verification is paused while the active Work Part is being saved and rebound");
                if (_ownershipState != "AI_OWNED" || !_acceptModelReads || !String.Equals(owner, _ownershipId, StringComparison.Ordinal) ||
                    epoch != _bindingEpoch || _binding == null)
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "part verification requires current AI ownership and binding epoch");
                if (_operations.Count >= MaxOperations)
                    throw new BridgeException("OPERATION_LIMIT_REACHED", "operation record capacity is full for this bridge load");
                OperationRecord executed;
                if (!_operations.TryGetValue(executeOperationId, out executed) ||
                    (!String.Equals(executed.Kind, "execute_parameter_edit", StringComparison.Ordinal) &&
                     !executed.Kind.StartsWith("execute_create_", StringComparison.Ordinal) &&
                     !String.Equals(executed.Kind, "execute_edit_sketch_corner", StringComparison.Ordinal) &&
                     !String.Equals(executed.Kind, "execute_edit_sketch_offset", StringComparison.Ordinal) &&
                     !String.Equals(executed.Kind, "execute_edit_sketch_pattern", StringComparison.Ordinal) &&
                     !String.Equals(executed.Kind, "execute_edit_sketch_projection", StringComparison.Ordinal) &&
                     !String.Equals(executed.Kind, "execute_edit_sketch_reference_conversion", StringComparison.Ordinal)) ||
                    !String.Equals(executed.State, "SUCCEEDED", StringComparison.Ordinal) || executed.Result == null)
                    throw new BridgeException("VERIFY_REQUIRES_SUCCESSFUL_EXECUTE", "execute_operation_id must identify a succeeded supported model operation in this bridge load");
                object planIdValue;
                string planId = executed.Result.TryGetValue("plan_id", out planIdValue) ? planIdValue as string : null;
                PreparedM3Plan m3Plan;
                if (!String.IsNullOrEmpty(planId) && _m3Plans.TryGetValue(planId, out m3Plan))
                {
                    if (!Object.ReferenceEquals(m3Plan.Binding, _binding) || m3Plan.BindingEpoch != epoch || !String.Equals(m3Plan.OwnershipId, owner, StringComparison.Ordinal))
                        throw new BridgeException("VERIFY_PLAN_UNAVAILABLE", "the successful M3 execute plan is unavailable for verification in this bridge load");
                    IM3OperationAdapter m3Adapter = _modelAdapter as IM3OperationAdapter;
                    if (m3Adapter == null) throw new BridgeException("M3_ADAPTER_UNAVAILABLE", "NX M3 verification adapter is not loaded");
                    M2ModelBinding m3Binding = _binding;
                    OperationRecord verifyOperation = new OperationRecord();
                    verifyOperation.Id = operationId;
                    verifyOperation.Kind = "verify_part";
                    verifyOperation.RequestKey = requestKey;
                    verifyOperation.State = "QUEUED";
                    verifyOperation.Cancellable = true;
                    verifyOperation.Work = new DispatchWork();
                    verifyOperation.Work.Operation = verifyOperation;
                    verifyOperation.Work.Execute = delegate { return PerformM3PartVerification(verifyOperation, m3Adapter, m3Binding, m3Plan, executeOperationId); };
                    _operations.Add(operationId, verifyOperation);
                    QueueDispatchLocked(verifyOperation.Work);
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(verifyOperation));
                }
                IM2M27ModelAdapter adapter = _modelAdapter as IM2M27ModelAdapter;
                if (adapter == null) throw new BridgeException("M27_ADAPTER_UNAVAILABLE", "NX verification adapter is not loaded");
                PreparedParameterPlan plan;
                if (String.IsNullOrEmpty(planId) || !_plans.TryGetValue(planId, out plan) ||
                    !Object.ReferenceEquals(plan.Binding, _binding) || plan.BindingEpoch != epoch || !String.Equals(plan.OwnershipId, owner, StringComparison.Ordinal))
                    throw new BridgeException("VERIFY_PLAN_UNAVAILABLE", "the successful execute plan is unavailable for verification in this bridge load");

                M2ModelBinding binding = _binding;
                OperationRecord operation = new OperationRecord();
                operation.Id = operationId;
                operation.Kind = "verify_part";
                operation.RequestKey = requestKey;
                operation.State = "QUEUED";
                operation.Cancellable = true;
                operation.Work = new DispatchWork();
                operation.Work.Operation = operation;
                operation.Work.Execute = delegate { return PerformPartVerification(operation, adapter, binding, plan); };
                _operations.Add(operationId, operation);
                QueueDispatchLocked(operation.Work);
                return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
            }
        }

        private Dictionary<string, object> PerformPartVerification(OperationRecord operation,
            IM2M27ModelAdapter adapter, M2ModelBinding binding, PreparedParameterPlan plan)
        {
            RequireCurrentBinding(binding);
            M2PartVerificationResult verification = adapter.VerifyParameterEdit(binding, plan.AdapterSnapshot);
            if (verification == null) throw new M2ModelException("PART_VERIFICATION_FAILED", "NX verification adapter returned no result");
            bool checksPassed = VerificationChecksPassed(verification.Checks);
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("verified", verification.Verified && checksPassed);
            result.Add("part_path", binding.PartPath);
            result.Add("revision_id", verification.RevisionId ?? binding.RevisionId);
            result.Add("execute_operation_id", FindExecutedOperationId(plan.Id));
            result.Add("checks", verification.Checks ?? new Dictionary<string, object>(StringComparer.Ordinal));
            lock (_lifecycle)
            {
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                    throw new BridgeException("STALE_BINDING", "ownership or model binding changed during verification");
                if (!verification.Verified || !checksPassed)
                {
                    operation.State = "FAILED";
                    operation.Cancellable = false;
                    operation.Result = result;
                    operation.Error = ErrorObject(
                        String.IsNullOrEmpty(verification.ErrorCode) ? "PART_VERIFICATION_FAILED" : verification.ErrorCode,
                        String.IsNullOrEmpty(verification.ErrorMessage) ? "live NX expression, geometry, history, or association evidence did not match the successful execute plan" : verification.ErrorMessage);
                }
            }
            return result;
        }

        private Dictionary<string, object> PerformM3PartVerification(OperationRecord operation,
            IM3OperationAdapter adapter, M2ModelBinding binding, PreparedM3Plan plan, string executeOperationId)
        {
            RequireCurrentBinding(binding);
            M3VerificationResult verification;
            try { verification = adapter.VerifyPlan(binding, plan.AdapterSnapshot); }
            catch (M2ModelException ex)
            {
                if (String.Equals(ex.Code, "M3_ANALYSIS_STATE_UNKNOWN", StringComparison.Ordinal))
                {
                    Dictionary<string, object> unknownResult = new Dictionary<string, object>(StringComparer.Ordinal);
                    unknownResult.Add("verified", false);
                    unknownResult.Add("part_path", binding.PartPath);
                    unknownResult.Add("revision_id", binding.RevisionId);
                    unknownResult.Add("execute_operation_id", executeOperationId);
                    unknownResult.Add("plan_id", plan.Id);
                    unknownResult.Add("kind", plan.AdapterSnapshot.Kind);
                    unknownResult.Add("verification_state", "UNKNOWN");
                    unknownResult.Add("error", ErrorObject(ex.Code, ex.Message));
                    lock (_lifecycle)
                    {
                        if (String.Equals(operation.State, "RUNNING", StringComparison.Ordinal))
                            SetOperationUnknownLocked(operation, ex.Code, ex.Message, unknownResult);
                    }
                }
                throw;
            }
            catch (Exception ex)
            {
                throw new M2ModelException("PART_VERIFICATION_FAILED",
                    "NX M3 VerifyPlan failed (" + ex.GetType().FullName + "): " + ex.Message);
            }
            if (verification == null) throw new M2ModelException("PART_VERIFICATION_FAILED", "NX M3 verification adapter returned no result");
            bool checksPassed = M3VerificationChecksPassed(verification.Checks, plan.AdapterSnapshot.Kind);
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("verified", verification.Verified && checksPassed);
            result.Add("part_path", binding.PartPath);
            result.Add("revision_id", verification.RevisionId ?? binding.RevisionId);
            result.Add("execute_operation_id", executeOperationId);
            result.Add("plan_id", plan.Id);
            result.Add("kind", plan.AdapterSnapshot.Kind);
            result.Add("checks", verification.Checks ?? new Dictionary<string, object>(StringComparer.Ordinal));
            if (verification.Result != null) result.Add("evidence", Clone(verification.Result));
            lock (_lifecycle)
            {
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                    throw new BridgeException("STALE_BINDING", "ownership or model binding changed during M3 verification");
                if (!verification.Verified || !checksPassed)
                {
                    operation.State = "FAILED";
                    operation.Cancellable = false;
                    operation.Result = result;
                    operation.Error = ErrorObject(
                        String.IsNullOrEmpty(verification.ErrorCode) ? "PART_VERIFICATION_FAILED" : verification.ErrorCode,
                        String.IsNullOrEmpty(verification.ErrorMessage) ? "live M3 geometry, constraints, history, body, or non-target evidence did not match the executed plan" : verification.ErrorMessage);
                }
            }
            return result;
        }

        private static bool M3VerificationChecksPassed(Dictionary<string, object> checks, string kind)
        {
            if (checks == null) return false;
            string[] names = kind == "create_datum_plane"
                ? new string[] { "native_feature", "definition", "history", "bodies" }
                : kind == "create_boolean"
                ? new string[] { "boolean_definition", "model_preservation", "body_effect", "geometry", "bodies", "history", "support_associations" }
                : kind == "create_chamfer"
                ? new string[] { "chamfer_definition", "model_preservation", "body_effect", "geometry", "bodies", "history", "support_associations" }
                : kind == "create_feature_pattern"
                ? new string[] { "pattern_definition", "pattern_instances", "model_preservation", "body_effect", "geometry", "bodies", "history", "support_associations" }
                : kind == "create_spatial_curve"
                ? new string[] { "curve_geometry", "model_preservation" }
                : kind == "create_sweep"
                ? new string[] { "sweep_definition", "sweep_geometry", "model_preservation", "body_effect", "geometry", "bodies", "history", "support_associations" }
                : kind == "create_feature_mirror"
                ? new string[] { "mirror_definition", "mirror_geometry", "model_preservation", "body_effect", "geometry", "bodies", "history", "support_associations" }
                : kind == "create_edge_blend"
                ? new string[] { "edge_blend_definition", "model_preservation", "body_effect", "geometry", "bodies", "history", "support_associations" }
                : kind == "create_hole"
                ? new string[] { "hole_definition", "hole_coverage", "model_preservation", "body_effect", "geometry", "bodies", "history", "support_associations" }
                : kind == "edit_sketch_corner"
                ? new string[] { "geometry", "sketch_solver_status", "constraints", "sketch_ownership", "non_target_geometry", "history", "bodies" }
                : kind == "edit_sketch_offset"
                ? new string[] { "offset_geometry", "source_curves", "sketch_constraints", "sketch_frames",
                    "reference_status", "non_target_geometry", "history", "bodies", "sketch_solver_status" }
                : kind == "edit_sketch_pattern"
                ? new string[] { "pattern_instances", "source_curves", "sketch_constraints", "sketch_frames",
                    "reference_status", "non_target_geometry", "history", "bodies", "sketch_solver_status" }
                : kind == "edit_sketch_projection"
                ? new string[] { "projection_geometry", "source_curves", "sketch_constraints", "sketch_frames",
                    "reference_status", "non_target_geometry", "history", "bodies", "sketch_solver_status" }
                : kind == "edit_sketch_reference_conversion"
                ? new string[] { "reference_conversion_targets", "sketch_geometry", "sketch_constraints", "sketch_frames",
                    "source_associations", "non_target_geometry", "history", "bodies", "sketch_solver_status",
                    "profile_candidates", "dirty_state" }
                : new string[] { "geometry", "constraints", "history", "bodies", "non_target_bodies" };
            foreach (string name in names)
            {
                object value;
                Dictionary<string, object> check;
                if (!checks.TryGetValue(name, out value) || (check = value as Dictionary<string, object>) == null) return false;
                object status;
                if (!check.TryGetValue("status", out status) || !String.Equals(status as string, "VERIFIED", StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static bool IsUnknownVerifyException(Exception exception)
        {
            string code = ExceptionCode(exception);
            return String.Equals(code, "M3_ANALYSIS_STATE_UNKNOWN", StringComparison.Ordinal) ||
                code.IndexOf("ROLLBACK_UNVERIFIED", StringComparison.Ordinal) >= 0 ||
                code.IndexOf("OUTCOME_UNKNOWN", StringComparison.Ordinal) >= 0;
        }

        private static bool IsUnknownVerificationResult(M3VerificationResult verification)
        {
            if (verification == null) return true;
            string code = verification.ErrorCode ?? String.Empty;
            if (String.Equals(code, "M3_ANALYSIS_STATE_UNKNOWN", StringComparison.Ordinal) ||
                code.IndexOf("ROLLBACK_UNVERIFIED", StringComparison.Ordinal) >= 0 ||
                code.IndexOf("OUTCOME_UNKNOWN", StringComparison.Ordinal) >= 0) return true;
            if (verification.Checks == null) return false;
            foreach (KeyValuePair<string, object> pair in verification.Checks)
            {
                Dictionary<string, object> check = pair.Value as Dictionary<string, object>;
                object status;
                if (check != null && check.TryGetValue("status", out status) &&
                    String.Equals(status as string, "UNKNOWN", StringComparison.Ordinal)) return true;
            }
            if (verification.Result != null)
            {
                object state;
                if (verification.Result.TryGetValue("verification_state", out state) &&
                    String.Equals(state as string, "UNKNOWN", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private string SaveCopy(object requestId, Dictionary<string, object> args)
        {
            RequireExactKeys(args, new string[] { "operation_id", "ownership_id", "binding_epoch", "target_path", "reopen" },
                "operation.save_copy params");
            string operationId = NormalizeOperationId(RequiredString(args, "operation_id"));
            string owner = NormalizeRequiredString(RequiredString(args, "ownership_id"), "ownership_id", MaxOperationIdChars);
            int epoch = RequiredPositiveInt(args, "binding_epoch");
            string targetText = NormalizeRequiredString(RequiredString(args, "target_path"), "target_path", 4096);
            object reopenValue;
            if (!args.TryGetValue("reopen", out reopenValue) || !(reopenValue is bool))
                throw new BridgeException("INVALID_REQUEST", "reopen must be a boolean");
            bool reopen = (bool)reopenValue;
            string targetPath;
            try { targetPath = Path.GetFullPath(targetText); }
            catch (Exception) { throw new BridgeException("INVALID_TARGET_PATH", "target_path must be a valid absolute .prt path"); }
            if (!Path.IsPathRooted(targetText) || !String.Equals(Path.GetExtension(targetPath), ".prt", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("INVALID_TARGET_PATH", "target_path must be an absolute .prt path");
            Dictionary<string, object> normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            normalized.Add("ownership_id", owner);
            normalized.Add("binding_epoch", epoch);
            normalized.Add("target_path", targetPath);
            normalized.Add("reopen", reopen);
            string requestKey = OperationRequestKey("save_copy", normalized);
            lock (_lifecycle)
            {
                FreezeTimedOutOperationsLocked();
                OperationRecord existing;
                if (_operations.TryGetValue(operationId, out existing))
                {
                    if (!String.Equals(existing.RequestKey, requestKey, StringComparison.Ordinal))
                        throw new BridgeException("OPERATION_ID_CONFLICT", "operation_id was already used with a different normalized request");
                    return M2Json.ResultEnvelope(requestId, OperationResultLocked(existing));
                }
                if (_writesHalted) throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "model writes are halted until the unknown outcome is resolved");
                if (_stopping) throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                if (ActiveSaveCopyLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "another save-copy operation is queued or running");
                if (_ownershipState != "AI_OWNED" || !_acceptModelReads || !String.Equals(owner, _ownershipId, StringComparison.Ordinal) ||
                    epoch != _bindingEpoch || _binding == null)
                    throw new BridgeException("AUTHORIZATION_REQUIRED", "saving a copy requires current AI ownership and binding epoch");
                if (ActiveParameterExecutionLocked())
                    throw new BridgeException("OPERATION_IN_PROGRESS", "a parameter edit is queued or running; query its operation_id before saving a copy");
                if (_operations.Count >= MaxOperations)
                    throw new BridgeException("OPERATION_LIMIT_REACHED", "operation record capacity is full for this bridge load");
                string sourcePath;
                if (_binding.IsReadOnly)
                    return FailedSaveCopy(requestId, operationId, requestKey, _binding.PartPath ?? String.Empty,
                        targetPath, "READ_ONLY_PART", "saving is not allowed for a read-only Work Part");
                if (String.Equals(_binding.ArtifactScope, "current_work_part", StringComparison.Ordinal))
                    return FailedSaveCopy(requestId, operationId, requestKey, _binding.PartPath ?? String.Empty,
                        targetPath, "CURRENT_WORK_PART_SAVE_UNSUPPORTED", "nx_save_copy cannot preserve current_work_part identity because it uses SaveAs and reopen");
                try { sourcePath = Path.GetFullPath(_binding.PartPath); }
                catch (Exception) { throw new BridgeException("MODEL_IDENTITY_UNAVAILABLE", "bound Work Part path is unavailable"); }
                if (String.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                    return FailedSaveCopy(requestId, operationId, requestKey, sourcePath, targetPath, "TARGET_IS_SOURCE_PART", "target_path cannot overwrite the takeover source part");
                if (!Directory.Exists(Path.GetDirectoryName(targetPath)))
                    return FailedSaveCopy(requestId, operationId, requestKey, sourcePath, targetPath, "TARGET_DIRECTORY_NOT_FOUND", "target_path directory does not exist");
                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                    return FailedSaveCopy(requestId, operationId, requestKey, sourcePath, targetPath, "TARGET_ALREADY_EXISTS", "target_path already exists; save copy never overwrites files");
                IM2M27ModelAdapter adapter = _modelAdapter as IM2M27ModelAdapter;
                if (adapter == null) throw new BridgeException("M27_ADAPTER_UNAVAILABLE", "NX save-copy adapter is not loaded");
                M2ModelBinding binding = _binding;
                OperationRecord operation = new OperationRecord();
                operation.Id = operationId;
                operation.Kind = "save_copy";
                operation.RequestKey = requestKey;
                operation.State = "QUEUED";
                operation.Cancellable = true;
                operation.Work = new DispatchWork();
                operation.Work.Operation = operation;
                operation.Work.Execute = delegate { return PerformSaveCopy(operation, adapter, binding, targetPath, reopen, sourcePath); };
                _operations.Add(operationId, operation);
                QueueDispatchLocked(operation.Work);
                return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
            }
        }

        private string FailedSaveCopy(object requestId, string operationId, string requestKey,
            string sourcePath, string targetPath, string code, string message)
        {
            OperationRecord operation = new OperationRecord();
            operation.Id = operationId;
            operation.Kind = "save_copy";
            operation.RequestKey = requestKey;
            operation.State = "FAILED";
            operation.Cancellable = false;
            operation.ExecutionCompleted = true;
            operation.Result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "saved", false },
                { "source_path", sourcePath },
                { "saved_path", targetPath },
                { "reopened", false },
                { "plans_invalidated", false },
                { "checks", new Dictionary<string, object>(StringComparer.Ordinal) },
                { "evidence", new Dictionary<string, object>(StringComparer.Ordinal) { { "save_attempted", false } } }
            };
            operation.Error = ErrorObject(code, message);
            _operations.Add(operationId, operation);
            return M2Json.ResultEnvelope(requestId, OperationResultLocked(operation));
        }

        private Dictionary<string, object> PerformSaveCopy(OperationRecord operation,
            IM2M27ModelAdapter adapter, M2ModelBinding binding, string targetPath, bool reopen, string sourcePath)
        {
            RequireCurrentBinding(binding);
            lock (_lifecycle)
            {
                if (_writesHalted || !Object.ReferenceEquals(_binding, binding))
                    throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "save copy was stopped before its NX call because writes are frozen");
                operation.ModelCallStarted = true;
            }
            M2SaveCopyResult saved;
            try { saved = adapter.SaveCopy(binding, targetPath, reopen); }
            catch (Exception ex)
            {
                Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
                evidence.Add("adapter_exception", ex.GetType().Name);
                evidence.Add("adapter_exception_message", ex.Message);
                lock (_lifecycle)
                    SetOperationUnknownLocked(operation, "SAVE_RESULT_UNKNOWN", "NX SaveAs or reopen did not return a verifiable result; do not retry with a new operation id", evidence);
                return operation.Result;
            }
            if (saved != null && String.Equals(saved.State, "UNCHANGED", StringComparison.Ordinal))
            {
                Dictionary<string, object> rejected = new Dictionary<string, object>(StringComparer.Ordinal);
                rejected.Add("saved", false);
                rejected.Add("source_path", sourcePath);
                rejected.Add("saved_path", targetPath);
                rejected.Add("reopened", false);
                rejected.Add("plans_invalidated", false);
                rejected.Add("checks", saved.Checks ?? new Dictionary<string, object>(StringComparer.Ordinal));
                rejected.Add("evidence", saved.Evidence ?? new Dictionary<string, object>(StringComparer.Ordinal));
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal))
                        operation.LateResult = Clone(rejected);
                    else
                    {
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.Result = rejected;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(saved.ErrorCode) ? "SAVE_COPY_REJECTED" : saved.ErrorCode,
                            String.IsNullOrEmpty(saved.ErrorMessage) ? "NX rejected the save-copy request before SaveAs started" : saved.ErrorMessage);
                    }
                }
                return rejected;
            }
            if (saved == null || !String.Equals(saved.State, "SAVED", StringComparison.Ordinal))
            {
                Dictionary<string, object> evidence = saved == null ? null : saved.Evidence;
                lock (_lifecycle)
                    SetOperationUnknownLocked(operation,
                        "SAVE_RESULT_UNKNOWN",
                        saved == null || String.IsNullOrEmpty(saved.ErrorMessage) ? "NX SaveAs or reopen outcome is unknown; do not retry with a new operation id" : saved.ErrorMessage,
                        evidence);
                return operation.Result;
            }
            bool checksPassed = !reopen || VerificationChecksPassed(saved.Checks);
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("saved", true);
            result.Add("source_path", sourcePath);
            result.Add("saved_path", saved.PartPath ?? targetPath);
            result.Add("reopened", saved.Reopened);
            result.Add("new_ownership_id", null);
            result.Add("new_binding_epoch", null);
            result.Add("revision_id", saved.RevisionId);
            result.Add("plans_invalidated", true);
            result.Add("checks", saved.Checks ?? new Dictionary<string, object>(StringComparer.Ordinal));
            result.Add("evidence", saved.Evidence ?? new Dictionary<string, object>(StringComparer.Ordinal));
            if (reopen && (!saved.Reopened || !checksPassed))
            {
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal))
                        operation.LateResult = Clone(result);
                    else
                    {
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.Result = result;
                        operation.Error = ErrorObject("SAVE_COPY_VERIFICATION_FAILED", "saved copy reopened, but expression, geometry, history, or association evidence did not match the pre-save model");
                        FreezeWritesLocked("SAVE_COPY_VERIFICATION_FAILED", operation);
                    }
                }
                return result;
            }

            string newOwner = Guid.NewGuid().ToString("N");
            lock (_lifecycle)
            {
                if (!Object.ReferenceEquals(_binding, binding) || _ownershipState != "AI_OWNED")
                {
                    SetOperationUnknownLocked(operation, "SAVE_RESULT_UNKNOWN", "saved copy changed the NX Work Part but the bridge binding changed before it could be rotated", result);
                    return operation.Result;
                }
                binding.PartPath = saved.PartPath ?? targetPath;
                binding.PartTag = saved.PartTag ?? binding.PartTag;
                binding.RevisionId = saved.RevisionId ?? binding.RevisionId;
                if (saved.PartTag != null && saved.PartTag.Length != 0 && saved.RevisionId != null)
                {
                    _bindingEpoch++;
                    binding.BindingEpoch = _bindingEpoch;
                    _ownershipId = newOwner;
                    _plans.Clear();
                    _m3Plans.Clear();
                    binding.Identity = saved.Identity == null
                        ? (binding.Identity == null ? new Dictionary<string, object>(StringComparer.Ordinal) : Clone(binding.Identity))
                        : Clone(saved.Identity);
                    binding.Identity["part_path"] = binding.PartPath;
                    binding.Identity["part_tag"] = binding.PartTag;
                    binding.Identity["revision_id"] = binding.RevisionId;
                    binding.Identity["binding_epoch"] = _bindingEpoch;
                }
                else
                {
                    SetOperationUnknownLocked(operation, "SAVE_RESULT_UNKNOWN", "saved copy returned without a refreshed NX binding identity", result);
                    return operation.Result;
                }
                result["new_ownership_id"] = _ownershipId;
                result["new_binding_epoch"] = _bindingEpoch;
                result["revision_id"] = binding.RevisionId;
                result["binding_identity"] = binding.Identity == null ? null : Clone(binding.Identity);
            }
            return result;
        }

        private string FindExecutedOperationId(string planId)
        {
            lock (_lifecycle)
            {
                foreach (OperationRecord operation in _operations.Values)
                {
                    if (!String.Equals(operation.Kind, "execute_parameter_edit", StringComparison.Ordinal) ||
                        !String.Equals(operation.State, "SUCCEEDED", StringComparison.Ordinal) || operation.Result == null) continue;
                    object recordedPlanId;
                    if (operation.Result.TryGetValue("plan_id", out recordedPlanId) &&
                        String.Equals(recordedPlanId as string, planId, StringComparison.Ordinal)) return operation.Id;
                }
            }
            return null;
        }

        private static bool VerificationChecksPassed(Dictionary<string, object> checks)
        {
            if (checks == null) return false;
            string[] names = new string[] { "expression", "geometry", "history", "associations" };
            foreach (string name in names)
            {
                object value;
                Dictionary<string, object> check;
                if (!checks.TryGetValue(name, out value) || (check = value as Dictionary<string, object>) == null) return false;
                object status;
                if (!check.TryGetValue("status", out status) || !String.Equals(status as string, "VERIFIED", StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private Dictionary<string, object> PerformPrepareParameterOperation(OperationRecord operation,
            M2ModelBinding binding, M2ParameterEditRequest request, string owner)
        {
            RequireCurrentBinding(binding);
            IM2OperationAdapter adapter = _modelAdapter as IM2OperationAdapter;
            if (adapter == null) throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX parameter-edit adapter is not loaded");
            M2ParameterPlanSnapshot snapshot;
            try { snapshot = adapter.PrepareParameterEdit(binding, request); }
            catch (M2ModelException) { throw; }
            catch (Exception ex)
            {
                throw new M2ModelException("PLAN_PREPARATION_FAILED", "NX parameter plan preparation failed (" + ex.GetType().Name + ")");
            }
            if (snapshot == null) throw new M2ModelException("PLAN_PREPARATION_FAILED", "NX parameter adapter returned no plan snapshot");
            RequireCurrentBinding(binding);
            if (!String.Equals(snapshot.RevisionId, request.RevisionId, StringComparison.Ordinal))
                throw new M2ModelException("STALE_REVISION", "model revision changed while preparing the parameter-edit plan");
            if (!String.Equals(snapshot.SketchId, request.SketchId, StringComparison.Ordinal) ||
                !String.Equals(snapshot.ConstraintId, request.ConstraintId, StringComparison.Ordinal))
                throw new M2ModelException("PLAN_TARGET_MISMATCH", "NX parameter adapter resolved a different sketch or constraint");
            if (!String.Equals(snapshot.Unit, request.Unit, StringComparison.Ordinal))
                throw new M2ModelException("UNIT_MISMATCH", "requested unit must exactly match the associated expression unit");
            if (String.IsNullOrEmpty(snapshot.DimensionType) || String.IsNullOrEmpty(snapshot.ExpressionName) ||
                !String.Equals(snapshot.DimensionState, "Driving", StringComparison.Ordinal) ||
                String.IsNullOrEmpty(snapshot.Unit) || snapshot.GeometryRefs == null || snapshot.GeometryRefs.Count == 0 || snapshot.AdapterState == null)
                throw new M2ModelException("UNSUPPORTED_PARAMETER", "target is not a supported Driving dimension with an explicit expression, unit, and geometry relationship");

            string planId = Guid.NewGuid().ToString("N");
            PreparedParameterPlan plan = new PreparedParameterPlan(planId, owner, binding.BindingEpoch,
                binding.SessionEpoch, binding.LoadInstanceId, binding, snapshot, request.ValueText, request.Unit);
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("plan", Clone(plan.PublicView));
            lock (_lifecycle)
            {
                if (_writesHalted || _ownershipState != "AI_OWNED" || !Object.ReferenceEquals(_binding, binding))
                    throw new BridgeException("OWNERSHIP_CHANGED", "ownership changed while preparing the parameter-edit plan");
                if (_plans.Count + _m3Plans.Count >= MaxPreparedPlans)
                    throw new BridgeException("PLAN_LIMIT_REACHED", "prepared plan capacity is full for this bridge load");
                _plans.Add(planId, plan);
            }
            return result;
        }

        private Dictionary<string, object> PerformExecuteParameterPlan(OperationRecord operation,
            PreparedParameterPlan plan, M2ModelBinding binding)
        {
            RequireCurrentBinding(binding);
            if (!String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
                throw new BridgeException("STALE_PLAN", "model revision changed after plan preparation");
            IM2OperationAdapter adapter = _modelAdapter as IM2OperationAdapter;
            if (adapter == null) throw new BridgeException("OPERATION_ADAPTER_UNAVAILABLE", "NX parameter-edit adapter is not loaded");

            M2ParameterExecutionResult execution;
            lock (_lifecycle)
            {
                if (_writesHalted || String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal))
                    throw new BridgeException("OPERATION_OUTCOME_UNKNOWN", "NX parameter execution was stopped before the adapter call because writes are frozen");
                operation.ModelCallStarted = true;
            }
            try { execution = adapter.ExecuteParameterEdit(binding, plan.AdapterSnapshot, plan.RequestedValue, plan.RequestedUnit); }
            catch (Exception ex)
            {
                lock (_lifecycle)
                {
                    Dictionary<string, object> evidence = new Dictionary<string, object>(StringComparer.Ordinal);
                    evidence.Add("adapter_exception", ex.GetType().Name);
                    SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN", "NX parameter edit returned without a verifiable mutation state", evidence);
                }
                return operation.Result;
            }
            if (execution == null)
            {
                lock (_lifecycle)
                    SetOperationUnknownLocked(operation, "OPERATION_OUTCOME_UNKNOWN", "NX parameter adapter returned no mutation state", null);
                return operation.Result;
            }

            bool bindingCurrent;
            string bindingReason;
            try { bindingCurrent = _modelAdapter.IsBindingCurrent(binding, out bindingReason); }
            catch (Exception ex)
            {
                bindingCurrent = false;
                bindingReason = "NX identity verification failed (" + ex.GetType().Name + ")";
            }
            if (!bindingCurrent)
            {
                Dictionary<string, object> evidence = CopyEvidence(execution.Evidence);
                evidence["binding_check"] = String.IsNullOrEmpty(bindingReason) ? "binding changed after parameter edit" : bindingReason;
                lock (_lifecycle)
                    SetOperationUnknownLocked(operation, "STALE_BINDING_AFTER_WRITE", "model binding changed while verifying the parameter edit", evidence);
                return operation.Result;
            }

            Dictionary<string, object> operationResult = BuildExecutionResult(plan, execution);
            string state = execution.State ?? String.Empty;
            if (String.Equals(state, "APPLIED", StringComparison.Ordinal))
            {
                object verified;
                object beforeGeometry;
                object afterGeometry;
                bool hasVerification = execution.Evidence != null &&
                    execution.Evidence.TryGetValue("readback_verified", out verified) && verified is bool && (bool)verified &&
                    execution.Evidence.TryGetValue("geometry_before", out beforeGeometry) &&
                    execution.Evidence.TryGetValue("geometry_after", out afterGeometry) &&
                    !String.Equals(M2Json.CanonicalText(beforeGeometry), M2Json.CanonicalText(afterGeometry), StringComparison.Ordinal);
                if (!hasVerification)
                {
                    lock (_lifecycle)
                        SetOperationUnknownLocked(operation, "READBACK_NOT_VERIFIED", "NX parameter adapter did not prove that the supported geometry changed", operationResult);
                    return operation.Result;
                }
                if (!String.IsNullOrEmpty(execution.RevisionId)) binding.RevisionId = execution.RevisionId;
                return operationResult;
            }
            if (String.Equals(state, "UNCHANGED", StringComparison.Ordinal))
            {
                if (!String.IsNullOrEmpty(execution.ErrorCode))
                {
                    lock (_lifecycle)
                    {
                        if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal))
                            operation.LateResult = Clone(operationResult);
                        else
                        {
                            operation.State = "FAILED";
                            operation.Cancellable = false;
                            operation.Result = operationResult;
                            operation.Error = ErrorObject(execution.ErrorCode,
                                String.IsNullOrEmpty(execution.ErrorMessage) ? "NX parameter edit was rejected without changing the model" : execution.ErrorMessage);
                        }
                    }
                }
                return operationResult;
            }
            if (String.Equals(state, "ROLLED_BACK", StringComparison.Ordinal))
            {
                lock (_lifecycle)
                {
                    if (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal))
                        operation.LateResult = Clone(operationResult);
                    else
                    {
                        operation.State = "FAILED";
                        operation.Cancellable = false;
                        operation.Result = operationResult;
                        operation.Error = ErrorObject(
                            String.IsNullOrEmpty(execution.ErrorCode) ? "OPERATION_ROLLED_BACK" : execution.ErrorCode,
                            String.IsNullOrEmpty(execution.ErrorMessage) ? "NX parameter edit failed and the prior expression was restored" : execution.ErrorMessage);
                    }
                }
                return operationResult;
            }

            lock (_lifecycle)
                SetOperationUnknownLocked(operation,
                    String.IsNullOrEmpty(execution.ErrorCode) ? "OPERATION_OUTCOME_UNKNOWN" : execution.ErrorCode,
                    String.IsNullOrEmpty(execution.ErrorMessage) ? "NX parameter edit could not verify the final model state" : execution.ErrorMessage,
                    operationResult);
            return operation.Result;
        }

        private static string NormalizeOperationId(string value)
        {
            return NormalizeRequiredString(value, "operation_id", MaxOperationIdChars);
        }

        private static string NormalizeRequiredString(string value, string name, int maxChars)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > maxChars)
                throw new BridgeException("INVALID_REQUEST", name + " must be a non-empty string of at most " + maxChars.ToString(CultureInfo.InvariantCulture) + " characters");
            string normalized;
            try { normalized = value.Normalize(NormalizationForm.FormC); }
            catch (ArgumentException) { throw new BridgeException("INVALID_REQUEST", name + " must contain valid Unicode text"); }
            if (String.IsNullOrWhiteSpace(normalized) || normalized.Length > maxChars)
                throw new BridgeException("INVALID_REQUEST", name + " must be a non-empty normalized string of at most " + maxChars.ToString(CultureInfo.InvariantCulture) + " characters");
            return normalized;
        }

        private static string NormalizeFiniteDecimal(string value, string name)
        {
            double parsed;
            if (String.IsNullOrEmpty(value) || value.Length > 128 ||
                !IsStrictDecimalText(value) ||
                !Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
                Double.IsNaN(parsed) || Double.IsInfinity(parsed))
                throw new BridgeException("INVALID_REQUEST", name + " must be a finite decimal string");

            // Normalize equivalent decimal spellings without rounding through a
            // binary float. The normalized key is used only for deduplication;
            // NX receives the first accepted request's original decimal text.
            int exponent = 0;
            int exponentAt = value.IndexOfAny(new char[] { 'e', 'E' });
            string mantissa = exponentAt < 0 ? value : value.Substring(0, exponentAt);
            if (exponentAt >= 0 && !Int32.TryParse(value.Substring(exponentAt + 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent))
                throw new BridgeException("INVALID_REQUEST", name + " has an invalid decimal exponent");
            bool negative = mantissa.StartsWith("-", StringComparison.Ordinal);
            if (mantissa.StartsWith("+", StringComparison.Ordinal) || negative) mantissa = mantissa.Substring(1);
            int dot = mantissa.IndexOf('.');
            string whole = dot < 0 ? mantissa : mantissa.Substring(0, dot);
            string fraction = dot < 0 ? String.Empty : mantissa.Substring(dot + 1);
            string digits = (whole + fraction).TrimStart('0');
            if (digits.Length == 0) return "0";
            int scale = fraction.Length - exponent;
            while (digits.Length > 0 && digits[digits.Length - 1] == '0')
            {
                digits = digits.Substring(0, digits.Length - 1);
                scale--;
            }
            int decimalPosition = digits.Length - scale;
            string normalized;
            if (decimalPosition <= 0)
            {
                if (-decimalPosition > 400) normalized = digits[0] +
                    (digits.Length > 1 ? "." + digits.Substring(1) : String.Empty) +
                    "e" + (decimalPosition - 1).ToString(CultureInfo.InvariantCulture);
                else normalized = "0." + new String('0', -decimalPosition) + digits;
            }
            else if (decimalPosition >= digits.Length)
            {
                if (decimalPosition - digits.Length > 400) normalized = digits[0] +
                    (digits.Length > 1 ? "." + digits.Substring(1) : String.Empty) +
                    "e" + (decimalPosition - 1).ToString(CultureInfo.InvariantCulture);
                else normalized = digits + new String('0', decimalPosition - digits.Length);
            }
            else normalized = digits.Substring(0, decimalPosition) + "." + digits.Substring(decimalPosition);
            return negative ? "-" + normalized : normalized;
        }

        private static bool IsStrictDecimalText(string value)
        {
            int index = 0;
            if (index < value.Length && (value[index] == '+' || value[index] == '-')) index++;
            if (index >= value.Length) return false;
            int integerStart = index;
            while (index < value.Length && value[index] >= '0' && value[index] <= '9') index++;
            int integerDigits = index - integerStart;
            if (integerDigits > 1 && value[integerStart] == '0') return false;
            int fractionDigits = 0;
            if (index < value.Length && value[index] == '.')
            {
                index++;
                int fractionStart = index;
                while (index < value.Length && value[index] >= '0' && value[index] <= '9') index++;
                fractionDigits = index - fractionStart;
                if (fractionDigits == 0) return false;
            }
            if (integerDigits == 0 && fractionDigits == 0) return false;
            if (index < value.Length && (value[index] == 'e' || value[index] == 'E'))
            {
                index++;
                if (index < value.Length && (value[index] == '+' || value[index] == '-')) index++;
                int exponentStart = index;
                while (index < value.Length && value[index] >= '0' && value[index] <= '9') index++;
                if (index == exponentStart) return false;
            }
            return index == value.Length;
        }

        private static string OperationRequestKey(string kind, Dictionary<string, object> normalizedArguments)
        {
            return kind + "\n" + M2Json.CanonicalText(normalizedArguments);
        }

        private Dictionary<string, object> OperationResultLocked(OperationRecord operation)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("ok", true);
            result.Add("operation_id", operation.Id);
            result.Add("kind", operation.Kind);
            result.Add("state", operation.State);
            result.Add("cancellable", operation.Cancellable && String.Equals(operation.State, "QUEUED", StringComparison.Ordinal));
            result.Add("result", operation.Result == null ? null : Clone(operation.Result));
            result.Add("error", operation.Error == null ? null : Clone(operation.Error));
            result.Add("writes_halted", _writesHalted);
            result.Add("write_halt_reason", _writeHaltReason);
            if (operation.LateResult != null) result.Add("late_completion", Clone(operation.LateResult));
            return result;
        }

        private Dictionary<string, object> UnknownOperationResultLocked(string operationId)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("ok", true);
            result.Add("operation_id", operationId);
            result.Add("kind", "unknown");
            result.Add("state", "UNKNOWN");
            result.Add("cancellable", false);
            result.Add("result", null);
            result.Add("error", ErrorObject("OPERATION_NOT_FOUND", "operation outcome is unknown to this bridge load"));
            result.Add("writes_halted", true);
            result.Add("write_halt_reason", _writeHaltReason);
            return result;
        }

        private static Dictionary<string, object> ErrorObject(string code, string message)
        {
            Dictionary<string, object> error = new Dictionary<string, object>(StringComparer.Ordinal);
            error.Add("code", code);
            error.Add("message", message);
            return error;
        }

        private static Dictionary<string, object> CopyEvidence(Dictionary<string, object> source)
        {
            return source == null ? new Dictionary<string, object>(StringComparer.Ordinal) : Clone(source);
        }

        private static Dictionary<string, object> CreatePlanView(PreparedParameterPlan plan)
        {
            Dictionary<string, object> target = new Dictionary<string, object>(StringComparer.Ordinal);
            target.Add("sketch_id", plan.SketchId);
            target.Add("constraint_id", plan.ConstraintId);
            target.Add("dimension_type", plan.DimensionType);
            target.Add("feature_journal_id", plan.SketchFeatureId);
            target.Add("expression_name", plan.ExpressionName);
            List<object> geometryRefs = new List<object>();
            foreach (string reference in plan.GeometryRefs) geometryRefs.Add(reference);
            target.Add("geometry_refs", geometryRefs);

            Dictionary<string, object> before = new Dictionary<string, object>(StringComparer.Ordinal);
            before.Add("expression_rhs", plan.ExpressionRightHandSide);
            before.Add("value", plan.CurrentValue);
            before.Add("unit", plan.Unit);
            Dictionary<string, object> requested = new Dictionary<string, object>(StringComparer.Ordinal);
            requested.Add("value", plan.RequestedValue);
            requested.Add("unit", plan.RequestedUnit);

            Dictionary<string, object> view = new Dictionary<string, object>(StringComparer.Ordinal);
            view.Add("schema", "m2-parameter-edit-v1");
            view.Add("plan_id", plan.Id);
            view.Add("plan_kind", "set_driving_dimension_value");
            view.Add("ownership_id", plan.OwnershipId);
            view.Add("binding_epoch", plan.BindingEpoch);
            view.Add("session_epoch", plan.SessionEpoch);
            view.Add("load_instance_id", plan.LoadInstanceId);
            view.Add("revision_id", plan.RevisionId);
            view.Add("target", target);
            view.Add("before", before);
            view.Add("requested", requested);
            return view;
        }

        private static Dictionary<string, object> BuildExecutionResult(PreparedParameterPlan plan,
            M2ParameterExecutionResult execution)
        {
            Dictionary<string, object> evidence = execution.Evidence ?? new Dictionary<string, object>(StringComparer.Ordinal);
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("plan_id", plan.Id);
            result.Add("mutation_state", execution.State);
            result.Add("changed", String.Equals(execution.State, "APPLIED", StringComparison.Ordinal) ? (object)true :
                String.Equals(execution.State, "UNKNOWN", StringComparison.Ordinal) ? null : (object)false);
            result.Add("before", ReadBeforeAfter(evidence, "before"));
            result.Add("after", ReadBeforeAfter(evidence, "after"));
            object value;
            result.Add("geometry_before", evidence.TryGetValue("geometry_before", out value) ? value : null);
            result.Add("geometry_after", evidence.TryGetValue("geometry_after", out value) ? value : null);
            result.Add("update_error_count", evidence.TryGetValue("update_error_count", out value) ? value : null);
            result.Add("readback_verified", evidence.TryGetValue("readback_verified", out value) ? value : false);
            result.Add("revision_id", execution.RevisionId);
            result.Add("evidence", Clone(evidence));
            return result;
        }

        private static Dictionary<string, object> ReadBeforeAfter(Dictionary<string, object> evidence, string prefix)
        {
            object nested;
            Dictionary<string, object> value = new Dictionary<string, object>(StringComparer.Ordinal);
            if (evidence.TryGetValue(prefix, out nested) && nested is Dictionary<string, object>)
                return Clone((Dictionary<string, object>)nested);
            object field;
            string[] suffixes = new string[] { "rhs", "value", "unit" };
            string[] names = new string[] { "expression_rhs", "value", "unit" };
            bool any = false;
            for (int i = 0; i < suffixes.Length; i++)
            {
                if (evidence.TryGetValue(prefix + "_" + suffixes[i], out field))
                {
                    value.Add(names[i], field);
                    any = true;
                }
            }
            return any ? value : null;
        }

        private int ActiveOperationCountLocked()
        {
            int count = 0;
            foreach (OperationRecord operation in _operations.Values)
            {
                if (String.Equals(operation.State, "QUEUED", StringComparison.Ordinal) ||
                    String.Equals(operation.State, "RUNNING", StringComparison.Ordinal) ||
                    (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) && !operation.ExecutionCompleted)) count++;
            }
            return count;
        }

        private bool ActiveParameterExecutionLocked()
        {
            foreach (OperationRecord operation in _operations.Values)
            {
                if (!operation.Kind.StartsWith("execute_", StringComparison.Ordinal)) continue;
                if (String.Equals(operation.State, "QUEUED", StringComparison.Ordinal) ||
                    String.Equals(operation.State, "RUNNING", StringComparison.Ordinal) ||
                    (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) && !operation.ExecutionCompleted)) return true;
            }
            return false;
        }

        private bool ActiveSaveCopyLocked()
        {
            foreach (OperationRecord operation in _operations.Values)
            {
                if (!String.Equals(operation.Kind, "save_copy", StringComparison.Ordinal)) continue;
                if (String.Equals(operation.State, "QUEUED", StringComparison.Ordinal) ||
                    String.Equals(operation.State, "RUNNING", StringComparison.Ordinal) ||
                    (String.Equals(operation.State, "UNKNOWN", StringComparison.Ordinal) && !operation.ExecutionCompleted)) return true;
            }
            return false;
        }

        private bool RemoveDispatchWorkLocked(DispatchWork target)
        {
            bool removed = false;
            Queue<DispatchWork> kept = new Queue<DispatchWork>();
            while (_dispatchQueue.Count != 0)
            {
                DispatchWork work = _dispatchQueue.Dequeue();
                if (!removed && Object.ReferenceEquals(work, target)) removed = true;
                else kept.Enqueue(work);
            }
            while (kept.Count != 0) _dispatchQueue.Enqueue(kept.Dequeue());
            return removed;
        }

        private void FreezeTimedOutOperationsLocked()
        {
            foreach (OperationRecord operation in _operations.Values)
            {
                if ((!operation.Kind.StartsWith("execute_", StringComparison.Ordinal) &&
                    !String.Equals(operation.Kind, "save_copy", StringComparison.Ordinal)) ||
                    !String.Equals(operation.State, "RUNNING", StringComparison.Ordinal) || operation.StartedTimestamp <= 0) continue;
                double elapsedMilliseconds = OperationElapsedMilliseconds(operation);
                if (elapsedMilliseconds >= DispatchTimeoutMs)
                {
                    SetOperationUnknownLocked(operation, "OPERATION_EXECUTION_TIMEOUT",
                        "NX operation exceeded the execution timeout; NX was not interrupted and the final outcome is not yet known", null);
                }
            }
        }

        private static double OperationElapsedMilliseconds(OperationRecord operation)
        {
            if (operation == null || operation.StartedTimestamp <= 0) return 0.0;
            return (Stopwatch.GetTimestamp() - operation.StartedTimestamp) * 1000.0 / Stopwatch.Frequency;
        }

        private void SetOperationUnknownLocked(OperationRecord operation, string code, string message,
            Dictionary<string, object> result)
        {
            operation.State = "UNKNOWN";
            operation.Cancellable = false;
            if (result != null) operation.Result = result;
            operation.Error = ErrorObject(code, message);
            FreezeWritesLocked(code, operation);
        }

        private void FreezeWritesLocked(string reason, OperationRecord cause)
        {
            _writesHalted = true;
            if (String.IsNullOrEmpty(_writeHaltReason)) _writeHaltReason = reason;
            _acceptModelReads = false;
            if (!_stopping) _ownershipState = "UNKNOWN_FROZEN";

            Queue<DispatchWork> kept = new Queue<DispatchWork>();
            while (_dispatchQueue.Count != 0)
            {
                DispatchWork work = _dispatchQueue.Dequeue();
                bool block = work.IsTakeover || work.IsReturn || work.IsModelRead || work.Operation != null;
                if (!block)
                {
                    kept.Enqueue(work);
                    continue;
                }
                if (work.OnQueueFailure != null) work.OnQueueFailure();
                if (work.Operation != null && !Object.ReferenceEquals(work.Operation, cause))
                {
                    work.Operation.State = "CANCELLED";
                    work.Operation.Cancellable = false;
                    work.Operation.ExecutionCompleted = true;
                    work.Operation.Error = ErrorObject("OPERATION_OUTCOME_UNKNOWN", "queued NX work was stopped because a prior outcome is unknown");
                    work.Operation.Result = null;
                    work.Operation.Work = null;
                }
                if (work.IsTakeover)
                {
                    work.Abandoned = true;
                    work.Done.TrySetException(new BridgeException("OPERATION_OUTCOME_UNKNOWN", "NX work is frozen because an operation outcome is unknown"));
                }
                else if (work.IsReturn)
                    work.Done.TrySetException(new BridgeException("OPERATION_OUTCOME_UNKNOWN", "ownership cannot be returned while an operation outcome is unknown"));
                else if (work.Operation != null)
                    work.Done.TrySetException(new BridgeException("OPERATION_OUTCOME_UNKNOWN", "queued operation was stopped because a prior outcome is unknown"));
                else
                    work.Done.TrySetException(new BridgeException("OPERATION_OUTCOME_UNKNOWN", "queued model read was stopped because an operation outcome is unknown"));
            }
            while (kept.Count != 0) _dispatchQueue.Enqueue(kept.Dequeue());
        }

        private void RequireCurrentBinding(M2ModelBinding binding)
        {
            if (_modelAdapter == null) throw new BridgeException("MODEL_ADAPTER_UNAVAILABLE", "NX model adapter is not loaded");
            string reason;
            bool current;
            try { current = _modelAdapter.IsBindingCurrent(binding, out reason); }
            catch (Exception ex)
            {
                throw new M2ModelException("MODEL_IDENTITY_CHECK_FAILED", "NX identity check failed (" + ex.GetType().Name + ")");
            }
            if (current) return;
            lock (_lifecycle)
            {
                if (Object.ReferenceEquals(_binding, binding))
                {
                    _binding = null;
                    _ownershipId = null;
                    _bindingEpoch++;
                    _ownershipState = _writesHalted ? "UNKNOWN_FROZEN" : "HUMAN_OWNED";
                    _acceptModelReads = false;
                    try { _modelAdapter.Release(binding); } catch { }
                }
            }
            string text = String.IsNullOrEmpty(reason) ? "NX Session or Work Part identity no longer matches the binding" : reason;
            throw new BridgeException("STALE_BINDING", text);
        }

        private Dictionary<string, object> WaitForDispatch(DispatchWork work, string timeoutCode)
        {
            lock (_lifecycle)
            {
                if (_stopping || _dispatcher == null || !_dispatcher.IsHandleCreated)
                    throw new BridgeException("SERVICE_STOPPING", "bridge service is stopping");
                QueueDispatchLocked(work);
            }
            return AwaitDispatch(work, timeoutCode);
        }

        private Dictionary<string, object> AwaitDispatch(DispatchWork work, string timeoutCode)
        {
            if (Task.WaitAny(new Task[] { work.Done.Task }, DispatchTimeoutMs) < 0)
            {
                work.Abandoned = work.IsTakeover;
                throw new BridgeException(timeoutCode, "main-thread request did not complete before timeout");
            }
            if (work.Done.Task.IsFaulted)
            {
                Exception error = work.Done.Task.Exception.InnerException ?? work.Done.Task.Exception;
                BridgeException bridgeError = error as BridgeException;
                if (bridgeError != null) throw bridgeError;
                M2ModelException modelError = error as M2ModelException;
                if (modelError != null) throw new BridgeException(modelError.Code, modelError.Message);
                throw new BridgeException("MODEL_READ_FAILED", "main-thread model request failed");
            }
            return work.Done.Task.Result;
        }

        private void QueueDispatchLocked(DispatchWork work)
        {
            _dispatchQueue.Enqueue(work);
            if (_pumpPosted) return;
            _pumpPosted = true;
            try { _dispatcher.BeginInvoke((MethodInvoker)ProcessDispatchQueue); }
            catch (Exception)
            {
                _pumpPosted = false;
                _dispatchQueue.Dequeue();
                if (work.OnQueueFailure != null) work.OnQueueFailure();
                if (work.Operation != null)
                {
                    work.Operation.State = "FAILED";
                    work.Operation.Cancellable = false;
                    work.Operation.ExecutionCompleted = true;
                    work.Operation.Work = null;
                    work.Operation.Error = ErrorObject("DISPATCH_FAILED", "main-thread callback could not be queued");
                }
                if (work.IsTakeover && _ownershipState == "TAKEOVER_CHECK")
                {
                    _ownershipState = "HUMAN_OWNED";
                    _acceptModelReads = false;
                }
                if (work.IsReturn)
                {
                    _ownershipState = "UNKNOWN_FROZEN";
                    _ownershipId = null;
                    _binding = null;
                    _bindingEpoch++;
                    _acceptModelReads = false;
                }
                work.Done.TrySetException(new BridgeException("DISPATCH_FAILED", "main-thread callback could not be queued"));
            }
        }

        private void ProcessDispatchQueue()
        {
            while (true)
            {
                DispatchWork work;
                BridgeException blocked = null;
                lock (_lifecycle)
                {
                    FreezeTimedOutOperationsLocked();
                    if (_dispatchQueue.Count == 0)
                    {
                        _pumpPosted = false;
                        return;
                    }
                    work = _dispatchQueue.Dequeue();
                    work.Started = true;
                    if (work.Operation != null && String.Equals(work.Operation.State, "QUEUED", StringComparison.Ordinal))
                    {
                        work.Operation.State = "RUNNING";
                        work.Operation.Cancellable = false;
                        work.Operation.StartedTimestamp = Stopwatch.GetTimestamp();
                    }
                    if (_writesHalted && (work.IsTakeover || work.IsReturn || work.IsModelRead || work.Operation != null))
                        blocked = new BridgeException("OPERATION_OUTCOME_UNKNOWN", "NX work is frozen because an operation outcome is unknown");
                }

                if (blocked != null)
                {
                    CompleteDispatchFailure(work, blocked);
                    continue;
                }
                if (work.Abandoned)
                {
                    if (work.IsTakeover)
                    {
                        lock (_lifecycle)
                        {
                            if (_ownershipState == "TAKEOVER_CHECK") _ownershipState = "HUMAN_OWNED";
                        }
                    }
                    work.Done.TrySetException(new BridgeException("TAKEOVER_CANCELLED", "takeover caller timed out before dispatch"));
                    continue;
                }
                try
                {
                    Dictionary<string, object> result = work.Execute();
                    lock (_lifecycle)
                    {
                        if (work.Operation != null)
                        {
                            if (String.Equals(work.Operation.State, "RUNNING", StringComparison.Ordinal) &&
                                (work.Operation.Kind.StartsWith("execute_", StringComparison.Ordinal) ||
                                 String.Equals(work.Operation.Kind, "save_copy", StringComparison.Ordinal)) &&
                                OperationElapsedMilliseconds(work.Operation) >= DispatchTimeoutMs)
                            {
                                SetOperationUnknownLocked(work.Operation, "OPERATION_EXECUTION_TIMEOUT",
                                    "NX operation exceeded the execution timeout; its completion arrived later and writes remain halted", null);
                                work.Operation.LateResult = result == null ? null : Clone(result);
                            }
                            else if (String.Equals(work.Operation.State, "RUNNING", StringComparison.Ordinal))
                            {
                                work.Operation.State = "SUCCEEDED";
                                work.Operation.Cancellable = false;
                                work.Operation.Result = result == null ? null : Clone(result);
                                work.Operation.Error = null;
                            }
                            else if (String.Equals(work.Operation.State, "UNKNOWN", StringComparison.Ordinal) && result != null)
                                work.Operation.LateResult = Clone(result);
                            work.Operation.ExecutionCompleted = true;
                            work.Operation.Work = null;
                        }
                    }
                    work.Done.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    lock (_lifecycle)
                    {
                        if (work.Operation != null)
                        {
                            if (String.Equals(work.Operation.State, "RUNNING", StringComparison.Ordinal) &&
                                (work.Operation.Kind.StartsWith("execute_", StringComparison.Ordinal) ||
                                 String.Equals(work.Operation.Kind, "save_copy", StringComparison.Ordinal)) &&
                                OperationElapsedMilliseconds(work.Operation) >= DispatchTimeoutMs)
                            {
                                Dictionary<string, object> lateError = new Dictionary<string, object>(StringComparer.Ordinal);
                                lateError.Add("code", ExceptionCode(ex));
                                lateError.Add("message", SafeExceptionMessage(ex));
                                SetOperationUnknownLocked(work.Operation, "OPERATION_EXECUTION_TIMEOUT",
                                    "NX operation exceeded the execution timeout and its late failure cannot establish a safe final state", lateError);
                            }
                            else if (String.Equals(work.Operation.State, "RUNNING", StringComparison.Ordinal))
                            {
                                work.Operation.State = "FAILED";
                                work.Operation.Cancellable = false;
                                work.Operation.Error = ErrorObject(ExceptionCode(ex), SafeExceptionMessage(ex));
                            }
                            else if (String.Equals(work.Operation.State, "UNKNOWN", StringComparison.Ordinal))
                            {
                                Dictionary<string, object> lateError = new Dictionary<string, object>(StringComparer.Ordinal);
                                lateError.Add("code", ExceptionCode(ex));
                                lateError.Add("message", SafeExceptionMessage(ex));
                                work.Operation.LateResult = lateError;
                            }
                            work.Operation.ExecutionCompleted = true;
                            work.Operation.Work = null;
                        }
                    }
                    work.Done.TrySetException(ex);
                }
            }
        }

        private void CompleteDispatchFailure(DispatchWork work, Exception exception)
        {
            lock (_lifecycle)
            {
                if (work.OnQueueFailure != null) work.OnQueueFailure();
                if (work.Operation != null)
                {
                    work.Operation.State = "CANCELLED";
                    work.Operation.Cancellable = false;
                    work.Operation.ExecutionCompleted = true;
                    work.Operation.Work = null;
                    work.Operation.Error = ErrorObject(ExceptionCode(exception), SafeExceptionMessage(exception));
                }
            }
            if (work.IsReturn)
            {
                // A frozen operation outcome keeps ownership closed; the cached
                // state must never claim HUMAN_OWNED merely because return queued.
                work.Done.TrySetException(exception);
            }
            else work.Done.TrySetException(exception);
        }

        private static string ExceptionCode(Exception exception)
        {
            BridgeException bridgeError = exception as BridgeException;
            if (bridgeError != null) return bridgeError.Code;
            M2ModelException modelError = exception as M2ModelException;
            if (modelError != null) return modelError.Code;
            return "MODEL_READ_FAILED";
        }

        private static string SafeExceptionMessage(Exception exception)
        {
            BridgeException bridgeError = exception as BridgeException;
            if (bridgeError != null) return bridgeError.Message;
            M2ModelException modelError = exception as M2ModelException;
            if (modelError != null) return modelError.Message;
            return "main-thread operation failed";
        }

        private Dictionary<string, object> OwnershipResultLocked(string code)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            result.Add("ok", true);
            result.Add("code", code);
            result.Add("ownership_state", _ownershipState);
            result.Add("binding_epoch", _bindingEpoch);
            result.Add("in_flight", _inFlight.ToString(CultureInfo.InvariantCulture));
            result.Add("writes_halted", _writesHalted);
            result.Add("safe_for_human", !_writesHalted && _ownershipState == "HUMAN_OWNED" && _ownershipId == null && _binding == null && _inFlight == 0);
            return result;
        }

        private static Dictionary<string, object> Clone(Dictionary<string, object> source)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (source != null) foreach (KeyValuePair<string, object> pair in source) result.Add(pair.Key, pair.Value);
            return result;
        }

        public void Stop()
        {
            lock (_lifecycle)
            {
                if (!_started) return;
                if (unchecked((int)GetCurrentThreadId()) != _mainThreadOsId)
                    throw new InvalidOperationException("Stop must run on the dispatcher owner thread");
                _stopping = true;
                _acceptModelReads = false;
                _ownershipState = "STOPPING";
                _stopped.TrySetResult(true);
                while (_dispatchQueue.Count != 0)
                    _dispatchQueue.Dequeue().Done.TrySetException(new BridgeException("SERVICE_STOPPING", "bridge service stopped before callback completion"));
                _pumpPosted = false;
                if (_binding != null && _modelAdapter != null)
                {
                    try { _modelAdapter.Release(_binding); } catch { }
                    _binding = null;
                }
                _ownershipId = null;
                try { RemotingServices.Disconnect(this); } catch { }
                if (_channel != null)
                {
                    try { ChannelServices.UnregisterChannel(_channel); } catch { }
                    _channel = null;
                }
                if (_dispatcher != null)
                {
                    try { _dispatcher.Dispose(); } catch { }
                    _dispatcher = null;
                }
                DeleteTokenFile();
                _token = null;
                _tokenFileAclVerified = false;
                _started = false;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void DeleteTokenFile()
        {
            try { if (!String.IsNullOrEmpty(_tokenFilePath) && File.Exists(_tokenFilePath)) File.Delete(_tokenFilePath); }
            catch { }
        }

        private static string CreatePrivateTokenFile(string path)
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (identity == null || identity.User == null)
                throw new UnauthorizedAccessException("current user SID is unavailable");

            string parent = Path.GetDirectoryName(path);
            if (String.IsNullOrEmpty(parent)) throw new ArgumentException("token file path must have a parent directory", "path");
            Directory.CreateDirectory(parent);

            byte[] secret = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
            string token = ToHex(secret);
            Array.Clear(secret, 0, secret.Length);

            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(identity.User);
            security.SetAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileSystemRights.WriteData | FileSystemRights.ReadAttributes,
                FileShare.None, 4096, FileOptions.None, security))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(token);
                writer.Write('\n');
                writer.Flush();
                stream.Flush(true);
            }

            FileSecurity verified = File.GetAccessControl(path);
            if (!verified.AreAccessRulesProtected || !identity.User.Equals(verified.GetOwner(typeof(SecurityIdentifier))))
            {
                try { File.Delete(path); } catch { }
                throw new UnauthorizedAccessException("token file ACL verification failed");
            }
            AuthorizationRuleCollection rules = verified.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                SecurityIdentifier sid = (SecurityIdentifier)rule.IdentityReference;
                if (rule.AccessControlType == AccessControlType.Allow && !sid.Equals(identity.User))
                {
                    try { File.Delete(path); } catch { }
                    throw new UnauthorizedAccessException("token file grants access to another identity");
                }
            }
            return token;
        }

        private static bool FixedTimeEquals(string expected, string supplied)
        {
            if (expected == null || supplied == null) return false;
            // Token format is always 64 lowercase hex chars. Reject malformed
            // lengths, then compare every character without early exit.
            if (expected.Length != 64 || supplied.Length != 64) return false;
            int diff = 0;
            for (int i = 0; i < 64; i++)
            {
                diff |= expected[i] ^ supplied[i];
            }
            return diff == 0;
        }

        private static int CountCodePoints(string value)
        {
            int count = 0;
            for (int i = 0; i < value.Length; i++, count++)
            {
                if (Char.IsHighSurrogate(value[i])) i++; // StrictJson has already required its low surrogate.
            }
            return count;
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder text = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private static bool IsSafePipeName(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return false;
            }
            return true;
        }

        private static void RequireExactKeys(Dictionary<string, object> value, string[] keys, string subject)
        {
            if (value.Count != keys.Length) throw new BridgeException("INVALID_REQUEST", subject + " has unsupported or missing fields");
            for (int i = 0; i < keys.Length; i++)
                if (!value.ContainsKey(keys[i])) throw new BridgeException("INVALID_REQUEST", subject + " has unsupported or missing fields");
        }

        private static string RequiredString(Dictionary<string, object> value, string key)
        {
            object item;
            if (!value.TryGetValue(key, out item) || !(item is string) || String.IsNullOrEmpty((string)item))
                throw new BridgeException("INVALID_REQUEST", key + " must be a non-empty string");
            return (string)item;
        }

        private static int RequiredPositiveInt(Dictionary<string, object> value, string key)
        {
            int result;
            if (!M2Json.TryGetInt(value, key, out result) || result <= 0)
                throw new BridgeException("INVALID_REQUEST", key + " must be a positive integer");
            return result;
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        private sealed class BridgeException : Exception
        {
            internal readonly string Code;
            internal BridgeException(string code, string message) : base(message) { Code = code; }
        }
    }

    public static class M2Json
    {
        private const int MaxEnvelopeChars = 16384;

        public static string TransportError(string requestJson)
        {
            object id = null;
            try
            {
                Dictionary<string, object> request = StrictJson.ParseObject(requestJson);
                object value;
                if (request.TryGetValue("id", out value) && value is string && ((string)value).Length <= 128) id = value;
            }
            catch { }
            return ErrorEnvelope(id, "TRANSPORT_ERROR", "bridge call failed");
        }

        internal static string ResultEnvelope(object id, Dictionary<string, object> result)
        {
            return "{\"id\":" + WriteValue(id) + ",\"result\":" + WriteValue(result) + "}";
        }

        public static string ErrorEnvelope(object id, string code, string message)
        {
            Dictionary<string, object> error = new Dictionary<string, object>(StringComparer.Ordinal);
            error.Add("code", code);
            error.Add("message", message);
            return "{\"id\":" + WriteValue(id) + ",\"error\":" + WriteValue(error) + "}";
        }

        internal static Dictionary<string, object> ParseObject(string text)
        {
            return StrictJson.ParseObject(text);
        }

        internal static bool TryGetInt(Dictionary<string, object> value, string key, out int result)
        {
            result = 0;
            object raw;
            if (value == null || !value.TryGetValue(key, out raw)) return false;
            if (raw is int) { result = (int)raw; return true; }
            JsonNumber number = raw as JsonNumber;
            return number != null && Int32.TryParse(number.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out result);
        }

        internal static bool TryGetNumberText(object raw, out string text)
        {
            text = null;
            if (raw is JsonNumber) { text = ((JsonNumber)raw).Text; return true; }
            if (raw is int) { text = ((int)raw).ToString(CultureInfo.InvariantCulture); return true; }
            if (raw is long) { text = ((long)raw).ToString(CultureInfo.InvariantCulture); return true; }
            if (raw is double) { text = ((double)raw).ToString("R", CultureInfo.InvariantCulture); return true; }
            if (raw is float) { text = ((float)raw).ToString("R", CultureInfo.InvariantCulture); return true; }
            if (raw is decimal) { text = ((decimal)raw).ToString(CultureInfo.InvariantCulture); return true; }
            return false;
        }

        internal static bool TryGetFiniteNumber(object raw, out double result)
        {
            result = 0.0;
            string text;
            return TryGetNumberText(raw, out text) && Double.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out result) && !Double.IsNaN(result) && !Double.IsInfinity(result);
        }

        public static string CanonicalText(object value)
        {
            return WriteValue(value);
        }

        private static string WriteValue(object value)
        {
            if (value == null) return "null";
            if (value is string) return WriteString((string)value);
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is int) return ((int)value).ToString(CultureInfo.InvariantCulture);
            if (value is long) return ((long)value).ToString(CultureInfo.InvariantCulture);
            if (value is double)
            {
                double number = (double)value;
                if (Double.IsNaN(number) || Double.IsInfinity(number)) throw new InvalidOperationException("non-finite JSON number");
                return number.ToString("R", CultureInfo.InvariantCulture);
            }
            if (value is float)
            {
                float number = (float)value;
                if (Single.IsNaN(number) || Single.IsInfinity(number)) throw new InvalidOperationException("non-finite JSON number");
                return number.ToString("R", CultureInfo.InvariantCulture);
            }
            if (value is decimal) return ((decimal)value).ToString(CultureInfo.InvariantCulture);
            Dictionary<string, object> map = value as Dictionary<string, object>;
            if (map != null)
            {
                List<string> keys = new List<string>(map.Keys);
                keys.Sort(StringComparer.Ordinal);
                StringBuilder obj = new StringBuilder("{");
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i != 0) obj.Append(',');
                    obj.Append(WriteString(keys[i])).Append(':').Append(WriteValue(map[keys[i]]));
                }
                return obj.Append('}').ToString();
            }
            System.Collections.IList list = value as System.Collections.IList;
            if (list != null)
            {
                StringBuilder array = new StringBuilder("[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i != 0) array.Append(',');
                    array.Append(WriteValue(list[i]));
                }
                return array.Append(']').ToString();
            }
            throw new InvalidOperationException("unsupported JSON value");
        }

        private static string WriteString(string value)
        {
            StringBuilder output = new StringBuilder(value.Length + 2);
            output.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': output.Append("\\\""); break;
                    case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break;
                    case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break;
                    case '\r': output.Append("\\r"); break;
                    case '\t': output.Append("\\t"); break;
                    default:
                        if (c < 0x20) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else output.Append(c);
                        break;
                }
            }
            output.Append('"');
            return output.ToString();
        }

        private sealed class JsonNumber
        {
            internal readonly string Text;
            internal JsonNumber(string text) { Text = text; }
        }

        private static class StrictJson
        {
            internal static Dictionary<string, object> ParseObject(string text)
            {
                if (text == null || text.Length == 0 || text.Length > MaxEnvelopeChars) throw new FormatException();
                Parser parser = new Parser(text);
                object root = parser.ParseValue(0);
                parser.SkipWhitespace();
                if (!parser.AtEnd) throw new FormatException();
                Dictionary<string, object> map = root as Dictionary<string, object>;
                if (map == null) throw new FormatException();
                return map;
            }

            private sealed class Parser
            {
                private readonly string _text;
                private int _at;
                internal Parser(string text) { _text = text; }
                internal bool AtEnd { get { return _at >= _text.Length; } }

                internal void SkipWhitespace()
                {
                    while (!AtEnd)
                    {
                        char c = _text[_at];
                        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _at++;
                        else break;
                    }
                }

                internal object ParseValue(int depth)
                {
                    if (depth > 16) throw new FormatException();
                    SkipWhitespace();
                    if (AtEnd) throw new FormatException();
                    char c = _text[_at];
                    if (c == '{') return ParseObject(depth + 1);
                    if (c == '[') return ParseArray(depth + 1);
                    if (c == '"') return ParseString();
                    if (c == 't') { ReadLiteral("true"); return true; }
                    if (c == 'f') { ReadLiteral("false"); return false; }
                    if (c == 'n') { ReadLiteral("null"); return null; }
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                    throw new FormatException();
                }

                private Dictionary<string, object> ParseObject(int depth)
                {
                    Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
                    _at++;
                    SkipWhitespace();
                    if (Take('}')) return result;
                    while (true)
                    {
                        SkipWhitespace();
                        if (AtEnd || _text[_at] != '"') throw new FormatException();
                        string key = ParseString();
                        if (result.ContainsKey(key)) throw new FormatException();
                        SkipWhitespace();
                        if (!Take(':')) throw new FormatException();
                        result.Add(key, ParseValue(depth));
                        SkipWhitespace();
                        if (Take('}')) return result;
                        if (!Take(',')) throw new FormatException();
                    }
                }

                private List<object> ParseArray(int depth)
                {
                    List<object> result = new List<object>();
                    _at++;
                    SkipWhitespace();
                    if (Take(']')) return result;
                    while (true)
                    {
                        result.Add(ParseValue(depth));
                        SkipWhitespace();
                        if (Take(']')) return result;
                        if (!Take(',')) throw new FormatException();
                    }
                }

                private string ParseString()
                {
                    if (!Take('"')) throw new FormatException();
                    StringBuilder value = new StringBuilder();
                    while (!AtEnd)
                    {
                        char c = _text[_at++];
                        if (c == '"') return value.ToString();
                        if (c < 0x20) throw new FormatException();
                        if (c == '\\')
                        {
                            if (AtEnd) throw new FormatException();
                            char esc = _text[_at++];
                            switch (esc)
                            {
                                case '"': value.Append('"'); break;
                                case '\\': value.Append('\\'); break;
                                case '/': value.Append('/'); break;
                                case 'b': value.Append('\b'); break;
                                case 'f': value.Append('\f'); break;
                                case 'n': value.Append('\n'); break;
                                case 'r': value.Append('\r'); break;
                                case 't': value.Append('\t'); break;
                                case 'u': AppendUnicodeEscape(value); break;
                                default: throw new FormatException();
                            }
                        }
                        else if (Char.IsHighSurrogate(c))
                        {
                            if (AtEnd || !Char.IsLowSurrogate(_text[_at])) throw new FormatException();
                            value.Append(c).Append(_text[_at++]);
                        }
                        else if (Char.IsLowSurrogate(c)) throw new FormatException();
                        else value.Append(c);
                    }
                    throw new FormatException();
                }

                private void AppendUnicodeEscape(StringBuilder value)
                {
                    char first = ReadHexCodeUnit();
                    if (Char.IsHighSurrogate(first))
                    {
                        if (_at + 1 >= _text.Length || _text[_at] != '\\' || _text[_at + 1] != 'u') throw new FormatException();
                        _at += 2;
                        char second = ReadHexCodeUnit();
                        if (!Char.IsLowSurrogate(second)) throw new FormatException();
                        value.Append(first).Append(second);
                    }
                    else if (Char.IsLowSurrogate(first)) throw new FormatException();
                    else value.Append(first);
                }

                private char ReadHexCodeUnit()
                {
                    if (_at + 4 > _text.Length) throw new FormatException();
                    int value = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        char c = _text[_at++];
                        int digit;
                        if (c >= '0' && c <= '9') digit = c - '0';
                        else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                        else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
                        else throw new FormatException();
                        value = (value << 4) | digit;
                    }
                    return (char)value;
                }

                private JsonNumber ParseNumber()
                {
                    int start = _at;
                    Take('-');
                    if (AtEnd) throw new FormatException();
                    if (Take('0'))
                    {
                        if (!AtEnd && IsDigit(_text[_at])) throw new FormatException();
                    }
                    else
                    {
                        if (!IsOneToNine(_text[_at])) throw new FormatException();
                        _at++;
                        while (!AtEnd && IsDigit(_text[_at])) _at++;
                    }
                    if (Take('.'))
                    {
                        if (AtEnd || !IsDigit(_text[_at])) throw new FormatException();
                        while (!AtEnd && IsDigit(_text[_at])) _at++;
                    }
                    if (Take('e') || Take('E'))
                    {
                        if (!AtEnd && (_text[_at] == '+' || _text[_at] == '-')) _at++;
                        if (AtEnd || !IsDigit(_text[_at])) throw new FormatException();
                        while (!AtEnd && IsDigit(_text[_at])) _at++;
                    }
                    return new JsonNumber(_text.Substring(start, _at - start));
                }

                private void ReadLiteral(string expected)
                {
                    if (_at + expected.Length > _text.Length || String.CompareOrdinal(_text, _at, expected, 0, expected.Length) != 0)
                        throw new FormatException();
                    _at += expected.Length;
                }

                private bool Take(char expected)
                {
                    if (!AtEnd && _text[_at] == expected) { _at++; return true; }
                    return false;
                }

                private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
                private static bool IsOneToNine(char c) { return c >= '1' && c <= '9'; }
            }
        }
    }
}
