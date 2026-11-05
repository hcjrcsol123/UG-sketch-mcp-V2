using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.Features;
using NxSketchMcp.M2;

public sealed partial class M2OpenModelAdapter
{
    public M3PlanSnapshot PrepareBoolean(M2ModelBinding binding, M3BooleanRequest source)
    {
        if (source == null || String.IsNullOrWhiteSpace(source.RevisionId) || source.RevisionId.Length > 128 ||
            String.IsNullOrWhiteSpace(source.TargetBodyId) || source.TargetBodyId.Length > 4096 ||
            (source.Operation != "unite" && source.Operation != "subtract" && source.Operation != "intersect") ||
            source.ToolBodyIds == null || source.ToolBodyIds.Length < 1 || source.ToolBodyIds.Length > 32)
            throw new M2ModelException("INVALID_REQUEST", "invalid Boolean request");
        HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal); ids.Add(source.TargetBodyId);
        foreach (string id in source.ToolBodyIds)
            if (String.IsNullOrWhiteSpace(id) || id.Length > 4096 || !ids.Add(id))
                throw new M2ModelException("INVALID_REQUEST", "Boolean input IDs must be distinct nonempty strings");
        BindingState state = RequireState(binding); RequireWritableSimplePart(state.WorkPart);
        RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
        if (state.Session.ActiveSketch != null) throw new M2ModelException("NX_BUSY", "end the active Sketch before preparing a Boolean");
        SnapshotResult capture = Capture(binding, state);
        if (!String.Equals(source.RevisionId, binding.RevisionId, StringComparison.Ordinal)) throw new M2ModelException("STALE_REVISION", "revision changed");
        Body target = FindBody(state.WorkPart, source.TargetBodyId);
        if (target == null || !target.IsSolidBody) throw new M2ModelException("UNSUPPORTED_BODY", "Boolean target must be a solid body");
        Body[] tools = new Body[source.ToolBodyIds.Length]; string[] tags = new string[tools.Length];
        HashSet<string> nativeTags = new HashSet<string>(StringComparer.Ordinal); nativeTags.Add(target.Tag.ToString());
        for (int i = 0; i < tools.Length; i++) {
            tools[i] = FindBody(state.WorkPart, source.ToolBodyIds[i]);
            if (tools[i] == null || !tools[i].IsSolidBody || !nativeTags.Add(tools[i].Tag.ToString()))
                throw new M2ModelException("UNSUPPORTED_BODY", "Boolean tools must identify distinct solid bodies");
            tags[i] = tools[i].Tag.ToString();
        }
        M3PlanState plan = CreateM3PlanState(binding, state, "create_boolean", capture, F12MassPropertyAccuracy);
        plan.BooleanRequest = new M3BooleanRequest { RevisionId = source.RevisionId, Operation = source.Operation,
            TargetBodyId = source.TargetBodyId, ToolBodyIds = (string[])source.ToolBodyIds.Clone(), RetainTools = source.RetainTools };
        plan.TargetBody = target; plan.TargetBodyId = source.TargetBodyId; plan.BooleanTools = tools;
        plan.BooleanTargetTag = target.Tag.ToString(); plan.BooleanToolTags = tags;
        plan.F12SemanticBefore = capture.Snapshot;
        plan.F12CurveInventoryBefore = M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart));
        plan.HoleHelpersBefore = F14CaptureHelpers(state.WorkPart);
        plan.HoleDefinitionsBefore = F14CapturePersistentHoleDefinitions(state.WorkPart, null);
        plan.BooleanDefinitionsBefore = F15CaptureDefinitions(state.WorkPart, null);
        try { plan.BodyFingerprintsBefore = CaptureF12BodyFingerprints(state.WorkPart, plan.BodiesBefore); }
        catch (Exception ex) { ThrowIfF12AnalysisStateUnknown(ex, "Boolean prepare fingerprint cleanup failed"); throw; }
        Dictionary<string, object> view = new Dictionary<string, object> {
            { "kind", "create_boolean" }, { "revision_id", binding.RevisionId }, { "operation", source.Operation },
            { "target_body_id", source.TargetBodyId }, { "tool_body_ids", source.ToolBodyIds }, { "retain_tools", source.RetainTools },
            { "target_body_before", FindBodyMeasure(plan.BodiesBefore, source.TargetBodyId).PublicRecord },
            { "body_inventory_before", BodyMeasureRecords(plan.BodiesBefore, null) }
        };
        return new M3PlanSnapshot { Kind = "create_boolean", RevisionId = binding.RevisionId, PublicView = view, AdapterState = plan };
    }

    private M3ExecutionResult ExecuteF15BooleanPlan(M2ModelBinding binding, M3PlanSnapshot prepared, M3PlanState plan)
    {
        BindingState state;
        if (binding == null || prepared == null || plan == null || plan.BooleanRequest == null || prepared.Kind != "create_boolean")
            return M3Execution("UNCHANGED", binding == null ? null : binding.RevisionId, null, "INVALID_PLAN", "Boolean plan is unavailable");
        try { state = RequireState(binding); }
        catch (M2ModelException ex) { return M3Execution("UNCHANGED", binding.RevisionId, null, ex.Code, ex.Message); }
        string reason;
        if (!Object.ReferenceEquals(state, plan.BindingState) || !String.Equals(plan.RevisionId, prepared.RevisionId, StringComparison.Ordinal) ||
            !IsBindingCurrent(binding, out reason) || !String.Equals(binding.RevisionId, plan.RevisionId, StringComparison.Ordinal))
            return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "Boolean binding or revision changed");
        try {
            RequireWritableSimplePart(state.WorkPart); RequireMillimeterPartAndUnit(state.WorkPart, "mm", "mm");
            if (state.Session.ActiveSketch != null) throw new M2ModelException("NX_BUSY", "end the active Sketch");
            SnapshotResult current = Capture(binding, state); Dictionary<string, object> check;
            List<M3BodyMeasure> bodies = CaptureM3BodyMeasures(state.WorkPart, F12MassPropertyAccuracy);
            if (!String.Equals(plan.SemanticBefore, CanonicalSemanticSnapshot(current.Snapshot), StringComparison.Ordinal) ||
                !String.Equals(plan.GeometryBefore, CaptureFullGeometry(state.WorkPart), StringComparison.Ordinal) ||
                !BodyMeasuresEqual(plan.BodiesBefore, bodies, out check) ||
                !F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore, CaptureF12BodyFingerprints(state.WorkPart, bodies), out check) ||
                !F14HelpersMatch(plan.HoleHelpersBefore, F14CaptureHelpers(state.WorkPart), null, null, null) ||
                !F14PersistentHoleDefinitionsEqual(plan.HoleDefinitionsBefore, F14CapturePersistentHoleDefinitions(state.WorkPart, null)) ||
                !F15DefinitionsEqual(plan.BooleanDefinitionsBefore, F15CaptureDefinitions(state.WorkPart, null)))
                return M3Execution("UNCHANGED", binding.RevisionId, null, "STALE_PLAN", "Boolean inputs or model state changed");
        } catch (Exception ex) {
            M2ModelException unknown = F12AnalysisStateUnknownOrNull(ex, "Boolean precondition cleanup could not be proven");
            return unknown != null ? M3Execution("UNKNOWN", binding.RevisionId, null, unknown.Code, unknown.Message) :
                M3Execution("UNCHANGED", binding.RevisionId, null, "MODEL_READ_FAILED", "Boolean precondition read failed (" + ex.GetType().Name + ")");
        }
        Session.UndoMarkId mark;
        try { mark = state.Session.SetUndoMark(Session.MarkVisibility.Invisible, "F15 native Boolean transaction"); }
        catch (Exception ex) { return M3Execution("UNCHANGED", binding.RevisionId, null, "UPDATE_FAILED", "Boolean undo mark failed (" + ex.GetType().Name + ")"); }
        try {
            Feature feature = V2BooleanFactory.Create(state.WorkPart, plan.BooleanRequest.Operation, plan.TargetBody, plan.BooleanTools, plan.BooleanRequest.RetainTools);
            plan.Feature = feature; plan.FeatureId = RequiredJournalId(feature, "Boolean feature");
            if (state.Session.UpdateManager.DoUpdate(mark) != 0) throw new M2ModelException("M3_UPDATE_FAILED", "Boolean update failed");
            if (plan.ForcePostCommitVerificationFailureForTesting) throw new M2ModelException("F15_FORCED_POST_COMMIT_FAILURE", "test-only failure after Boolean commit");
            Dictionary<string, object> definition = V2BooleanFactory.Read(state.WorkPart, feature);
            if (!F15DefinitionMatches(plan, definition)) throw new M2ModelException("F15_DEFINITION_MISMATCH", "native Boolean inputs or settings differ");
            SnapshotResult after = Capture(binding, state);
            plan.BodiesAfter = CaptureM3BodyMeasures(state.WorkPart, F12MassPropertyAccuracy);
            plan.AffectedBodies = ResolveAffectedBodies(plan, plan.BodiesAfter, feature);
            plan.AffectedBodyIds = new List<string>(); foreach (M3BodyMeasure body in plan.AffectedBodies) plan.AffectedBodyIds.Add(body.Id);
            plan.AffectedBodyIds.Sort(StringComparer.Ordinal); plan.AffectedBodyId = plan.AffectedBodyIds.Count == 1 ? plan.AffectedBodyIds[0] : null;
            plan.BodyFingerprintsAfter = CaptureF12BodyFingerprints(state.WorkPart, plan.BodiesAfter);
            plan.BooleanResultGeometry = CaptureF13RevolveOutputGeometry(state.WorkPart, plan.AffectedBodies);
            Dictionary<string, object> effect, preservation; List<object> support;
            bool ok = F15BodyEffectMatches(plan, plan.AffectedBodies, plan.BodiesAfter, plan.BodyFingerprintsAfter, out effect) &
                F15ModelPreserved(plan, after.Snapshot, out preservation);
            bool supportOk = ValidateM4SupportTracks(state, out support, null);
            bool related = F15RelatedDefinitionsMatch(state.WorkPart, plan, plan.FeatureId);
            if (!ok || !supportOk || !related) {
                plan.ExecutionFailureEvidence = new Dictionary<string, object> { { "body_effect", effect }, { "model_preservation", preservation },
                    { "support_associations", support }, { "existing_definitions_preserved", related } };
                throw new M2ModelException("F15_POSTCONDITION_FAILED", "Boolean effect or model preservation could not be proven");
            }
            plan.BooleanDefinition = definition; plan.SemanticAfter = CanonicalSemanticSnapshot(after.Snapshot);
            plan.GeometryAfter = CaptureFullGeometry(state.WorkPart); plan.HistoryAfter = HistoryProjection(after.Snapshot);
            plan.F12CurveInventoryAfter = M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart));
            Dictionary<string, object> result = F15Result(binding, plan, definition, plan.AffectedBodies, plan.BooleanResultGeometry);
            result["body_effect"] = effect; result["model_preservation"] = preservation;
            plan.Execution = M3Execution("APPLIED", binding.RevisionId, result, null, null); return plan.Execution;
        } catch (Exception ex) {
            Exception failure = ex is V2BooleanBuilderCleanupException ? (Exception)new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN", "Boolean builder cleanup failed") : ex;
            plan.Execution = RestoreM3State(binding, state, plan, mark, failure); return plan.Execution;
        } finally { TryDeleteUndoMark(state.Session, mark); }
    }

    private M3VerificationResult VerifyF15BooleanPlan(M2ModelBinding binding, M3PlanSnapshot prepared, M3PlanState plan)
    {
        if (binding == null || prepared == null || plan == null || plan.BooleanRequest == null || plan.Execution == null || plan.Execution.State != "APPLIED")
            return M3VerificationFailure(binding == null ? null : binding.RevisionId, "fresh Boolean verification requires an applied plan");
        BindingState state = RequireState(binding); string reason;
        if (!Object.ReferenceEquals(state, plan.BindingState) || !IsBindingCurrent(binding, out reason)) return M3VerificationFailure(binding.RevisionId, "Boolean binding changed");
        Dictionary<string, object> definition;
        try { definition = V2BooleanFactory.Read(state.WorkPart, FindFeature(state.WorkPart, plan.FeatureId)); }
        catch (V2BooleanBuilderCleanupException ex) { throw M3AnalysisStateUnknown("Boolean readback cleanup failed", ex); }
        SnapshotResult capture = Capture(binding, state);
        List<M3BodyMeasure> bodies = CaptureM3BodyMeasures(state.WorkPart, F12MassPropertyAccuracy);
        List<M3BodyMeasure> affected = ResolveAffectedBodies(plan, bodies, FindFeature(state.WorkPart, plan.FeatureId));
        Dictionary<string, string> fingerprints = CaptureF12BodyFingerprints(state.WorkPart, bodies);
        Dictionary<string, object> geometry = CaptureF13RevolveOutputGeometry(state.WorkPart, affected);
        Dictionary<string, object> effect, preservation, bodyEvidence, fingerprintEvidence;
        bool native = F15DefinitionMatches(plan, definition) && String.Equals(M2Json.CanonicalText(plan.BooleanDefinition), M2Json.CanonicalText(definition), StringComparison.Ordinal);
        bool effectOk = F15BodyEffectMatches(plan, affected, bodies, fingerprints, out effect);
        bool preserve = F15ModelPreserved(plan, capture.Snapshot, out preservation) && F15RelatedDefinitionsMatch(state.WorkPart, plan, plan.FeatureId);
        bool sameBodies = BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesAfter, null), BodyMeasureRecords(bodies, null), out bodyEvidence) &&
            F12BodyFingerprintsEqual(plan.BodyFingerprintsAfter, fingerprints, out fingerprintEvidence);
        bool sameGeometry = String.Equals(M2Json.CanonicalText(plan.BooleanResultGeometry), M2Json.CanonicalText(geometry), StringComparison.Ordinal);
        bool sameHistory = String.Equals(plan.SemanticAfter, CanonicalSemanticSnapshot(capture.Snapshot), StringComparison.Ordinal) &&
            String.Equals(plan.GeometryAfter, CaptureFullGeometry(state.WorkPart), StringComparison.Ordinal) &&
            SemanticValuesEqual(plan.HistoryAfter, HistoryProjection(capture.Snapshot), "history") &&
            String.Equals(plan.F12CurveInventoryAfter, M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart)), StringComparison.Ordinal);
        List<object> support; bool supportOk = ValidateM4SupportTracks(state, out support, null);
        bool verified = native && effectOk && preserve && sameBodies && sameGeometry && sameHistory && supportOk;
        Dictionary<string, object> checks = new Dictionary<string, object> {
            { "boolean_definition", VerificationCheck(native ? "VERIFIED" : "MISMATCH", definition) },
            { "model_preservation", VerificationCheck(preserve ? "VERIFIED" : "MISMATCH", preservation) },
            { "body_effect", VerificationCheck(effectOk ? "VERIFIED" : "MISMATCH", effect) },
            { "geometry", VerificationCheck(sameGeometry ? "VERIFIED" : "MISMATCH", new Dictionary<string, object> { { "matches_execute", sameGeometry } }) },
            { "bodies", VerificationCheck(sameBodies ? "VERIFIED" : "MISMATCH", bodyEvidence) },
            { "history", VerificationCheck(sameHistory ? "VERIFIED" : "MISMATCH", new Dictionary<string, object> { { "matches_execute", sameHistory } }) },
            { "support_associations", VerificationCheck(supportOk ? "VERIFIED" : "MISMATCH", new Dictionary<string, object> { { "associations", support } }) }
        };
        return new M3VerificationResult { Verified = verified, RevisionId = binding.RevisionId, Checks = checks,
            Result = F15Result(binding, plan, definition, affected, geometry), ErrorCode = verified ? null : "PART_VERIFICATION_FAILED",
            ErrorMessage = verified ? null : "Fresh Boolean definition, output or preservation differs from the executed plan." };
    }
    private static Dictionary<string, object> F15Result(M2ModelBinding binding, M3PlanState plan, Dictionary<string, object> definition,
        List<M3BodyMeasure> affected, Dictionary<string, object> geometry)
    {
        List<string> ids = new List<string>(); foreach (M3BodyMeasure body in affected) ids.Add(body.Id); ids.Sort(StringComparer.Ordinal);
        return new Dictionary<string, object> { { "kind", "create_boolean" }, { "feature_id", plan.FeatureId },
            { "body_id", ids.Count == 1 ? ids[0] : null }, { "body_ids", StringListAsObjects(ids) }, { "bodies", BodyMeasureRecords(affected, null) },
            { "target_body_id", plan.BooleanRequest.TargetBodyId }, { "tool_body_ids", plan.BooleanRequest.ToolBodyIds },
            { "operation", plan.BooleanRequest.Operation }, { "retain_tools", plan.BooleanRequest.RetainTools },
            { "native_definition", definition }, { "result_geometry", geometry }, { "revision_id", binding.RevisionId } };
    }

    private static bool F15DefinitionMatches(M3PlanState plan, Dictionary<string, object> d)
    {
        if (d == null || !d.ContainsKey("operation") || !Object.Equals(d["operation"], plan.BooleanRequest.Operation) ||
            !d.ContainsKey("retain_tools") || !Object.Equals(d["retain_tools"], plan.BooleanRequest.RetainTools)) return false;
        string[] targets = d.ContainsKey("target_body_tags") ? d["target_body_tags"] as string[] : null;
        string[] tools = d.ContainsKey("tool_body_tags") ? d["tool_body_tags"] as string[] : null;
        return targets != null && targets.Length == 1 && targets[0] == plan.BooleanTargetTag && tools != null &&
            tools.Length == plan.BooleanToolTags.Length && new HashSet<string>(tools, StringComparer.Ordinal).SetEquals(plan.BooleanToolTags);
    }

    private static bool F15BodyEffectMatches(M3PlanState plan, List<M3BodyMeasure> affected, List<M3BodyMeasure> after,
        Dictionary<string, string> fingerprints, out Dictionary<string, object> evidence)
    {
        HashSet<string> excludedBefore = new HashSet<string>(StringComparer.Ordinal); excludedBefore.Add(plan.BooleanTargetTag);
        if (!plan.BooleanRequest.RetainTools) foreach (string tag in plan.BooleanToolTags) excludedBefore.Add(tag);
        HashSet<string> outputTags = new HashSet<string>(StringComparer.Ordinal);
        bool outputsSolid = affected != null && affected.Count > 0;
        foreach (M3BodyMeasure b in affected) { if (!b.IsSolid || !outputTags.Add(b.Body.Tag.ToString())) outputsSolid = false; }
        Dictionary<string, string> beforeOther = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> afterOther = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in plan.BodyFingerprintsBefore) if (!excludedBefore.Contains(entry.Key)) beforeOther.Add(entry.Key, entry.Value);
        foreach (KeyValuePair<string, string> entry in fingerprints) if (!outputTags.Contains(entry.Key)) afterOther.Add(entry.Key, entry.Value);
        Dictionary<string, object> compared; bool others = F12BodyFingerprintsEqual(beforeOther, afterOther, out compared);
        bool consumed = true;
        if (!plan.BooleanRequest.RetainTools) foreach (string tag in plan.BooleanToolTags) if (fingerprints.ContainsKey(tag)) consumed = false;
        int expectedCount = plan.BodiesBefore.Count - 1 - (plan.BooleanRequest.RetainTools ? 0 : plan.BooleanToolTags.Length) + affected.Count;
        bool count = after.Count == expectedCount;
        M3BodyMeasure prior = FindBodyMeasure(plan.BodiesBefore, plan.TargetBodyId);
        double? resultVolume = AggregateBodyVolume(affected); double? oldVolume = prior == null ? null : prior.VolumeMm3;
        bool changed = false;
        if (oldVolume.HasValue && resultVolume.HasValue && resultVolume.Value > 0) {
            double tolerance = ValueTolerance(oldVolume.Value, resultVolume.Value);
            changed = plan.BooleanRequest.Operation == "unite" ? resultVolume.Value > oldVolume.Value + tolerance : resultVolume.Value < oldVolume.Value - tolerance;
        }
        bool matches = outputsSolid && count && consumed && others && changed;
        evidence = new Dictionary<string, object> { { "status", matches ? "VERIFIED" : "MISMATCH" },
            { "target_volume_before_mm3", oldVolume }, { "result_volume_mm3", resultVolume }, { "target_changed", changed },
            { "output_count", affected.Count }, { "body_count_before", plan.BodiesBefore.Count }, { "body_count_after", after.Count },
            { "expected_body_count_after", expectedCount }, { "body_count_matches", count }, { "tools_consumed_when_requested", consumed },
            { "nonparticipating_and_retained_bodies", compared }, { "all_outputs_solid", outputsSolid } };
        return matches;
    }

    private static bool F15ModelPreserved(M3PlanState plan, Dictionary<string, object> after, out Dictionary<string, object> evidence)
    {
        Dictionary<string, object> before = plan.F12SemanticBefore; List<object> errors = new List<object>();
        foreach (string key in new string[] { "sketches", "native_helical_definitions" }) if (!ExactProjection(before, after, key, null)) errors.Add(key);
        if (!ExactProjection(before, after, "part", new string[] { "is_modified" }) ||
            !ExactProjection(before, after, "identity", new string[] { "is_modified", "support_summary" }) ||
            !ExactSessionParts(before, after) || !PreserveExistingCurveIds(before, after)) errors.Add("part_session_or_curves");
        HashSet<string> owned = new HashSet<string>(StringComparer.Ordinal); owned.Add(plan.FeatureId);
        if (!F14SupportPreserved(before, after, owned)) errors.Add("support");
        List<object> oldFeatures = SnapshotList(before, "features"), newFeatures = SnapshotList(after, "features");
        HashSet<string> inputIds = new HashSet<string>(plan.BooleanRequest.ToolBodyIds, StringComparer.Ordinal); inputIds.Add(plan.TargetBodyId);
        HashSet<string> inputOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (object value in oldFeatures) {
            Dictionary<string, object> row = value as Dictionary<string, object>; if (row == null) { errors.Add("old_feature_record"); continue; }
            string id = row.ContainsKey("journal_id") ? row["journal_id"] as string : null;
            if (String.IsNullOrEmpty(id)) { errors.Add("old_feature_id"); continue; }
            foreach (string bodyId in ReadStringList(row, "body_ids")) if (inputIds.Contains(bodyId)) inputOwners.Add(id);
        }
        HashSet<string> added = new HashSet<string>(StringComparer.Ordinal);
        foreach (object value in newFeatures) {
            Dictionary<string, object> row = value as Dictionary<string, object>; string id = row != null && row.ContainsKey("journal_id") ? row["journal_id"] as string : null;
            if (String.IsNullOrEmpty(id)) { errors.Add("new_feature_id"); continue; }
            if (!ContainsFeatureId(oldFeatures, id)) {
                if (!added.Add(id) || id != plan.FeatureId) errors.Add("unexpected_feature:" + id);
                foreach (string parent in ReadStringList(row, "parent_ids")) if (!inputOwners.Contains(parent)) errors.Add("unexpected_parent:" + parent);
            }
        }
        if (added.Count != 1 || !added.Contains(plan.FeatureId)) errors.Add("new_feature_set");
        Dictionary<string, object> newFeature = FindSnapshotFeature(after, plan.FeatureId);
        if (!ExactProjection(before, after, "expressions", null)) errors.Add("expressions_changed");
        if (newFeature == null || ReadStringList(newFeature, "expression_ids").Count != 0) errors.Add("unexpected_boolean_expression");
        HashSet<string> actualParents = newFeature == null ? new HashSet<string>(StringComparer.Ordinal) :
            new HashSet<string>(ReadStringList(newFeature, "parent_ids"), StringComparer.Ordinal);
        Dictionary<string, int> edgeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (object edge in SnapshotList(after, "dependencies")) {
            string text = M2Json.CanonicalText(edge); int count; edgeCounts.TryGetValue(text, out count); edgeCounts[text] = count + 1;
        }
        foreach (object edge in SnapshotList(before, "dependencies")) {
            string text = M2Json.CanonicalText(edge); int count;
            if (!edgeCounts.TryGetValue(text, out count) || count == 0) errors.Add("existing_dependency_removed"); else edgeCounts[text] = count - 1;
        }
        HashSet<string> newEdges = new HashSet<string>(StringComparer.Ordinal);
        foreach (object value in SnapshotList(after, "dependencies")) {
            string text = M2Json.CanonicalText(value); int count;
            if (!edgeCounts.TryGetValue(text, out count) || count == 0) continue;
            edgeCounts[text] = count - 1;
            Dictionary<string, object> edge = value as Dictionary<string, object>;
            string kind = edge != null && edge.ContainsKey("kind") ? edge["kind"] as string : null;
            string from = edge != null && edge.ContainsKey("source_id") ? edge["source_id"] as string : null;
            string to = edge != null && edge.ContainsKey("target_id") ? edge["target_id"] as string : null;
            if (kind != "FEATURE_PARENT" || to != plan.FeatureId || from == null || !actualParents.Contains(from) || !newEdges.Add(from)) errors.Add("unexpected_dependency");
        }
        if (!newEdges.SetEquals(actualParents)) errors.Add("parent_dependencies_incomplete");
        foreach (string inputId in inputIds) {
            bool linked = false;
            foreach (object value in oldFeatures) {
                Dictionary<string, object> row = value as Dictionary<string, object>;
                string parentId = row != null && row.ContainsKey("journal_id") ? row["journal_id"] as string : null;
                if (parentId != null && actualParents.Contains(parentId) && ReadStringList(row, "body_ids").Contains(inputId)) { linked = true; break; }
            }
            if (!linked) errors.Add("input_parent_unproven:" + inputId);
        }
        foreach (object value in oldFeatures) {
            Dictionary<string, object> prior = value as Dictionary<string, object>; if (prior == null) continue;
            string id = prior.ContainsKey("journal_id") ? prior["journal_id"] as string : null; if (id == null) continue;
            Dictionary<string, object> current = FindSnapshotFeature(after, id); if (current == null) { errors.Add("missing_feature:" + id); continue; }
            bool owner = inputOwners.Contains(id);
            if (!SemanticValuesEqual(OmitFields(prior, owner ? new string[] { "child_ids", "body_ids" } : new string[] { "child_ids" }),
                OmitFields(current, owner ? new string[] { "child_ids", "body_ids" } : new string[] { "child_ids" }), "feature")) errors.Add("changed_feature:" + id);
            HashSet<string> oldChildren = new HashSet<string>(ReadStringList(prior, "child_ids"), StringComparer.Ordinal);
            HashSet<string> newChildren = new HashSet<string>(ReadStringList(current, "child_ids"), StringComparer.Ordinal);
            foreach (string child in oldChildren) if (!newChildren.Contains(child)) errors.Add("removed_child:" + id);
            foreach (string child in newChildren) if (!oldChildren.Contains(child) && (!owner || child != plan.FeatureId)) errors.Add("unexpected_child:" + id);
            if (owner) {
                HashSet<string> oldBodies = new HashSet<string>(ReadStringList(prior, "body_ids"), StringComparer.Ordinal);
                HashSet<string> newBodies = new HashSet<string>(ReadStringList(current, "body_ids"), StringComparer.Ordinal);
                foreach (string body in oldBodies) if (!inputIds.Contains(body) && !newBodies.Contains(body)) errors.Add("removed_unrelated_body:" + id);
                foreach (string body in newBodies) if (!oldBodies.Contains(body) && !plan.AffectedBodyIds.Contains(body)) errors.Add("unexpected_body:" + id);
            }
        }
        evidence = new Dictionary<string, object> { { "status", errors.Count == 0 ? "VERIFIED" : "MISMATCH" }, { "errors", errors } };
        return errors.Count == 0;
    }

    private static bool F15RelatedDefinitionsMatch(Part part, M3PlanState plan, string excluded)
    {
        return F14HelpersMatch(plan.HoleHelpersBefore, F14CaptureHelpers(part), null, null, null) &&
            F14PersistentHoleDefinitionsEqual(plan.HoleDefinitionsBefore, F14CapturePersistentHoleDefinitions(part, null)) &&
            F15DefinitionsEqual(plan.BooleanDefinitionsBefore, F15CaptureDefinitions(part, excluded)) &&
            String.Equals(plan.F12CurveInventoryBefore, M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(part)), StringComparison.Ordinal);
    }

    private static Dictionary<string, object> F15CaptureDefinitions(Part part, string excluded)
    {
        Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (Feature f in part.Features) {
            if (!(f is BooleanFeature) || String.Equals(f.JournalIdentifier, excluded, StringComparison.Ordinal)) continue;
            Dictionary<string, object> raw;
            try { raw = V2BooleanFactory.Read(part, f); }
            catch (V2BooleanBuilderCleanupException ex) { throw M3AnalysisStateUnknown("existing Boolean builder cleanup failed", ex); }
            catch (Exception ex) { throw new M2ModelException("UNSUPPORTED_BOOLEAN_PRESERVATION", "existing Boolean definition cannot be read (" + ex.GetType().Name + ")"); }
            Dictionary<string, object> stable = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string key in new string[] { "feature_id", "operation", "native_operation", "retain_tools", "retain_target", "copy_targets", "copy_tools", "target_body_ids", "tool_body_ids", "output_body_ids" })
                if (raw.ContainsKey(key)) stable.Add(key, raw[key]);
            string id = raw["feature_id"] as string;
            if (String.IsNullOrEmpty(id) || result.ContainsKey(id)) throw new M2ModelException("UNSUPPORTED_BOOLEAN_PRESERVATION", "Boolean feature IDs are not unique");
            result.Add(id, stable);
        }
        return result;
    }
    private static bool F15DefinitionsEqual(Dictionary<string, object> a, Dictionary<string, object> b)
    { return a != null && b != null && String.Equals(M2Json.CanonicalText(a), M2Json.CanonicalText(b), StringComparison.Ordinal); }
}
