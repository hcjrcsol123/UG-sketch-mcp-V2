using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.Features;
using NxSketchMcp.M2;

public sealed partial class M2OpenModelAdapter
{
    public M3PlanSnapshot PrepareEdgeBlend(M2ModelBinding binding, M3EdgeBlendRequest source)
    {
        if(source==null || String.IsNullOrWhiteSpace(source.RevisionId) || String.IsNullOrWhiteSpace(source.TargetBodyId) || source.EdgeIds==null || source.EdgeIds.Length<1 || source.EdgeIds.Length>32 || !IsFinite(source.RadiusMm) || source.RadiusMm<=0) throw new M2ModelException("INVALID_REQUEST","invalid edge blend request");
        HashSet<string> unique=new HashSet<string>(StringComparer.Ordinal); foreach(string id in source.EdgeIds) if(String.IsNullOrWhiteSpace(id) || id.Length>4096 || !unique.Add(id)) throw new M2ModelException("INVALID_REQUEST","edge IDs must be distinct nonempty strings");
        BindingState state=RequireState(binding); RequireWritableSimplePart(state.WorkPart); RequireMillimeterPartAndUnit(state.WorkPart,"mm","mm");
        if(state.Session.ActiveSketch!=null) throw new M2ModelException("NX_BUSY","end the active Sketch before preparing an edge blend");
        SnapshotResult capture=Capture(binding,state); if(!String.Equals(source.RevisionId,binding.RevisionId,StringComparison.Ordinal)) throw new M2ModelException("STALE_REVISION","revision changed");
        Body target=FindBody(state.WorkPart,source.TargetBodyId); if(target==null || !target.IsSolidBody) throw new M2ModelException("UNSUPPORTED_BODY","target must be a solid box");
        double[] box; List<F16Edge> selected=F16Preflight(target,source.EdgeIds,source.RadiusMm,out box);
        M3PlanState plan=CreateM3PlanState(binding,state,"create_edge_blend",capture,F12MassPropertyAccuracy);
        plan.EdgeBlendRequest=new M3EdgeBlendRequest { RevisionId=source.RevisionId,TargetBodyId=source.TargetBodyId,EdgeIds=(string[])source.EdgeIds.Clone(),RadiusMm=source.RadiusMm };
        plan.TargetBody=target; plan.TargetBodyId=source.TargetBodyId; plan.EdgeBlendInput=selected; plan.EdgeBlendBox=box; plan.EdgeBlendEdges=new Edge[selected.Count]; plan.EdgeBlendTags=new string[selected.Count];
        for(int i=0;i<selected.Count;i++) {plan.EdgeBlendEdges[i]=selected[i].Edge;plan.EdgeBlendTags[i]=selected[i].Tag;}
        plan.F12SemanticBefore=capture.Snapshot; plan.F12CurveInventoryBefore=M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart));
        plan.HoleHelpersBefore=F14CaptureHelpers(state.WorkPart); plan.HoleDefinitionsBefore=F14CapturePersistentHoleDefinitions(state.WorkPart,null); plan.BooleanDefinitionsBefore=F15CaptureDefinitions(state.WorkPart,null); plan.EdgeBlendDefinitionsBefore=F16CaptureDefinitions(state.WorkPart,null);
        try {plan.BodyFingerprintsBefore=CaptureF12BodyFingerprints(state.WorkPart,plan.BodiesBefore);} catch(Exception ex) {ThrowIfF12AnalysisStateUnknown(ex,"edge blend prepare fingerprint cleanup failed");throw;}
        M3BodyMeasure prior=FindBodyMeasure(plan.BodiesBefore,source.TargetBodyId); double volume=(box[3]-box[0])*(box[4]-box[1])*(box[5]-box[2]);
        if(prior==null || !prior.VolumeMm3.HasValue || Math.Abs(prior.VolumeMm3.Value-volume)>Math.Max(1e-5,volume*1e-7)) throw new M2ModelException("UNSUPPORTED_EDGE_BLEND_GEOMETRY","target volume does not match a complete rectangular box");
        Dictionary<string,object> view=new Dictionary<string,object>{{"kind","create_edge_blend"},{"revision_id",binding.RevisionId},{"target_body_id",source.TargetBodyId},{"edge_ids",source.EdgeIds},{"radius_mm",source.RadiusMm},{"target_body_before",prior.PublicRecord}};
        return new M3PlanSnapshot {Kind="create_edge_blend",RevisionId=binding.RevisionId,PublicView=view,AdapterState=plan};
    }

    private M3ExecutionResult ExecuteF16EdgeBlendPlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        if(binding==null || prepared==null || plan==null || plan.EdgeBlendRequest==null || prepared.Kind!="create_edge_blend") return M3Execution("UNCHANGED",binding==null?null:binding.RevisionId,null,"INVALID_PLAN","edge blend plan unavailable");
        BindingState state; try {state=RequireState(binding);} catch(M2ModelException ex) {return M3Execution("UNCHANGED",binding.RevisionId,null,ex.Code,ex.Message);} string reason;
        if(!Object.ReferenceEquals(state,plan.BindingState) || !String.Equals(plan.RevisionId,prepared.RevisionId,StringComparison.Ordinal) || !IsBindingCurrent(binding,out reason) || !String.Equals(binding.RevisionId,plan.RevisionId,StringComparison.Ordinal)) return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","binding or revision changed");
        try {
            RequireWritableSimplePart(state.WorkPart); RequireMillimeterPartAndUnit(state.WorkPart,"mm","mm"); if(state.Session.ActiveSketch!=null) throw new M2ModelException("NX_BUSY","end the active Sketch");
            SnapshotResult current=Capture(binding,state); List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy); Dictionary<string,object> ignored;
            if(!String.Equals(plan.SemanticBefore,CanonicalSemanticSnapshot(current.Snapshot),StringComparison.Ordinal) || !String.Equals(plan.GeometryBefore,CaptureFullGeometry(state.WorkPart),StringComparison.Ordinal) || !BodyMeasuresEqual(plan.BodiesBefore,bodies,out ignored) || !F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore,CaptureF12BodyFingerprints(state.WorkPart,bodies),out ignored) || !F16RelatedDefinitionsMatch(state.WorkPart,plan,null)) return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","edge blend inputs or model state changed");
        } catch(Exception ex) {M2ModelException unknown=F12AnalysisStateUnknownOrNull(ex,"edge blend precondition cleanup failed"); return unknown!=null?M3Execution("UNKNOWN",binding.RevisionId,null,unknown.Code,unknown.Message):M3Execution("UNCHANGED",binding.RevisionId,null,"MODEL_READ_FAILED","edge blend precondition read failed ("+ex.GetType().Name+")");}
        Session.UndoMarkId mark; try {mark=state.Session.SetUndoMark(Session.MarkVisibility.Invisible,"F16 native edge blend transaction");} catch(Exception ex) {return M3Execution("UNCHANGED",binding.RevisionId,null,"UPDATE_FAILED","undo mark failed ("+ex.GetType().Name+")");}
        try {
            Feature feature=V2EdgeBlendFactory.Create(state.WorkPart,plan.EdgeBlendEdges,plan.EdgeBlendRequest.RadiusMm); plan.Feature=feature; plan.FeatureId=RequiredJournalId(feature,"edge blend feature");
            if(state.Session.UpdateManager.DoUpdate(mark)!=0) throw new M2ModelException("M3_UPDATE_FAILED","edge blend update failed");
            if(plan.ForcePostCommitVerificationFailureForTesting) throw new M2ModelException("F16_FORCED_POST_COMMIT_FAILURE","test-only failure after edge blend commit");
            Dictionary<string,object> definition=V2EdgeBlendFactory.Read(state.WorkPart,feature); if(!F16DefinitionMatches(plan,definition)) throw new M2ModelException("F16_DEFINITION_MISMATCH","native edge selection or radius differs");
            SnapshotResult after=Capture(binding,state); plan.BodiesAfter=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy); plan.AffectedBodies=ResolveAffectedBodies(plan,plan.BodiesAfter,feature);
            plan.AffectedBodyIds=new List<string>(); foreach(M3BodyMeasure body in plan.AffectedBodies) plan.AffectedBodyIds.Add(body.Id); plan.AffectedBodyIds.Sort(StringComparer.Ordinal); plan.AffectedBodyId=plan.AffectedBodyIds.Count==1?plan.AffectedBodyIds[0]:null;
            plan.BodyFingerprintsAfter=CaptureF12BodyFingerprints(state.WorkPart,plan.BodiesAfter); plan.EdgeBlendGeometry=CaptureF13RevolveOutputGeometry(state.WorkPart,plan.AffectedBodies);
            Dictionary<string,object> effect,preservation,coverage=null; bool ok=F16BodyEffect(plan,plan.AffectedBodies,plan.BodiesAfter,plan.BodyFingerprintsAfter,out effect) & F16ModelPreserved(plan,after.Snapshot,definition,out preservation);
            bool coverageOk=plan.AffectedBodies.Count==1 && F16Coverage(plan.AffectedBodies[0].Body,plan,out coverage); if(plan.AffectedBodies.Count!=1) coverage=new Dictionary<string,object>{{"status","MISMATCH"}};
            List<object> support; bool supportOk=ValidateM4SupportTracks(state,out support,null); bool related=F16RelatedDefinitionsMatch(state.WorkPart,plan,plan.FeatureId);
            if(!ok || !coverageOk || !supportOk || !related) { plan.ExecutionFailureEvidence=new Dictionary<string,object>{{"body_effect",effect},{"model_preservation",preservation},{"coverage",coverage},{"existing_definitions_preserved",related},{"support_associations",support}}; throw new M2ModelException("F16_POSTCONDITION_FAILED","edge blend effect or model preservation could not be proven"); }
            plan.EdgeBlendDefinition=definition; plan.SemanticAfter=CanonicalSemanticSnapshot(after.Snapshot); plan.GeometryAfter=CaptureFullGeometry(state.WorkPart); plan.HistoryAfter=HistoryProjection(after.Snapshot); plan.F12CurveInventoryAfter=M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart));
            Dictionary<string,object> result=F16Result(binding,plan,definition,plan.AffectedBodies,plan.EdgeBlendGeometry); result["coverage"]=coverage;result["body_effect"]=effect;result["model_preservation"]=preservation;
            plan.Execution=M3Execution("APPLIED",binding.RevisionId,result,null,null);return plan.Execution;
        } catch(Exception ex) {Exception failure=(ex is V2EdgeBlendBuilderCleanupException || ex is F12BodyGeometrySnapshot.CleanupException)?(Exception)new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN","edge blend analysis resource cleanup failed"):ex; plan.Execution=RestoreM3State(binding,state,plan,mark,failure);return plan.Execution;} finally {TryDeleteUndoMark(state.Session,mark);}
    }

    private M3VerificationResult VerifyF16EdgeBlendPlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        if(binding==null || prepared==null || plan==null || plan.EdgeBlendRequest==null || plan.Execution==null || plan.Execution.State!="APPLIED") return M3VerificationFailure(binding==null?null:binding.RevisionId,"fresh verification requires an applied edge blend");
        BindingState state=RequireState(binding);string reason; if(!Object.ReferenceEquals(state,plan.BindingState) || !IsBindingCurrent(binding,out reason)) return M3VerificationFailure(binding.RevisionId,"binding changed");
        Feature feature=FindFeature(state.WorkPart,plan.FeatureId); Dictionary<string,object> definition; try {definition=V2EdgeBlendFactory.Read(state.WorkPart,feature);} catch(V2EdgeBlendBuilderCleanupException ex) {throw M3AnalysisStateUnknown("edge blend readback cleanup failed",ex);}
        SnapshotResult capture=Capture(binding,state);List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy), affected=ResolveAffectedBodies(plan,bodies,feature);Dictionary<string,string> fingerprints=CaptureF12BodyFingerprints(state.WorkPart,bodies);
        Dictionary<string,object> geometry=CaptureF13RevolveOutputGeometry(state.WorkPart,affected),effect,preservation,bodyEvidence,fingerprintEvidence,coverage=null;
        bool native=F16DefinitionMatches(plan,definition) && String.Equals(M2Json.CanonicalText(plan.EdgeBlendDefinition),M2Json.CanonicalText(definition),StringComparison.Ordinal);
        bool effectOk=F16BodyEffect(plan,affected,bodies,fingerprints,out effect); bool coverageOk; try {coverageOk=affected.Count==1 && F16Coverage(affected[0].Body,plan,out coverage);} catch(F12BodyGeometrySnapshot.CleanupException ex) {throw M3AnalysisStateUnknown("edge blend geometry audit cleanup failed",ex);} if(affected.Count!=1) coverage=new Dictionary<string,object>{{"status","MISMATCH"}};
        bool preserve=F16ModelPreserved(plan,capture.Snapshot,definition,out preservation) && F16RelatedDefinitionsMatch(state.WorkPart,plan,plan.FeatureId);
        bool sameBodies=BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesAfter,null),BodyMeasureRecords(bodies,null),out bodyEvidence) && F12BodyFingerprintsEqual(plan.BodyFingerprintsAfter,fingerprints,out fingerprintEvidence);
        bool sameGeometry=String.Equals(M2Json.CanonicalText(plan.EdgeBlendGeometry),M2Json.CanonicalText(geometry),StringComparison.Ordinal);
        bool history=String.Equals(plan.SemanticAfter,CanonicalSemanticSnapshot(capture.Snapshot),StringComparison.Ordinal) && String.Equals(plan.GeometryAfter,CaptureFullGeometry(state.WorkPart),StringComparison.Ordinal) && SemanticValuesEqual(plan.HistoryAfter,HistoryProjection(capture.Snapshot),"history") && String.Equals(plan.F12CurveInventoryAfter,M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart)),StringComparison.Ordinal);
        List<object> support; bool supportOk=ValidateM4SupportTracks(state,out support,null); bool verified=native && effectOk && coverageOk && preserve && sameBodies && sameGeometry && history && supportOk;
        Dictionary<string,object> checks=new Dictionary<string,object>{{"edge_blend_definition",VerificationCheck(native?"VERIFIED":"MISMATCH",definition)},{"model_preservation",VerificationCheck(preserve?"VERIFIED":"MISMATCH",preservation)},{"body_effect",VerificationCheck(effectOk?"VERIFIED":"MISMATCH",effect)},{"geometry",VerificationCheck(sameGeometry&&coverageOk?"VERIFIED":"MISMATCH",coverage)},{"bodies",VerificationCheck(sameBodies?"VERIFIED":"MISMATCH",bodyEvidence)},{"history",VerificationCheck(history?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"matches_execute",history}})},{"support_associations",VerificationCheck(supportOk?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"associations",support}})}};
        Dictionary<string,object> result=F16Result(binding,plan,definition,affected,geometry);result["coverage"]=coverage;
        return new M3VerificationResult {Verified=verified,RevisionId=binding.RevisionId,Checks=checks,Result=result,ErrorCode=verified?null:"PART_VERIFICATION_FAILED",ErrorMessage=verified?null:"Fresh edge blend definition, output or preservation differs."};
    }

    private static bool F16DefinitionMatches(M3PlanState plan,Dictionary<string,object> d)
    { string[] tags=d["edge_tags"] as string[];return tags!=null && tags.Length==plan.EdgeBlendTags.Length && new HashSet<string>(tags,StringComparer.Ordinal).SetEquals(plan.EdgeBlendTags) && F16Near((double)d["radius_mm"],plan.EdgeBlendRequest.RadiusMm) && Object.Equals(d["section"],"Circular") && Object.Equals(d["radius_units"],"MilliMeter"); }
    private static Dictionary<string,object> F16Result(M2ModelBinding binding,M3PlanState plan,Dictionary<string,object> definition,List<M3BodyMeasure> affected,Dictionary<string,object> geometry)
    {List<string> ids=new List<string>();foreach(M3BodyMeasure b in affected)ids.Add(b.Id);ids.Sort(StringComparer.Ordinal);return new Dictionary<string,object>{{"kind","create_edge_blend"},{"feature_id",plan.FeatureId},{"body_id",ids.Count==1?ids[0]:null},{"body_ids",StringListAsObjects(ids)},{"bodies",BodyMeasureRecords(affected,null)},{"target_body_id",plan.TargetBodyId},{"edge_ids",plan.EdgeBlendRequest.EdgeIds},{"radius_mm",plan.EdgeBlendRequest.RadiusMm},{"native_definition",definition},{"result_geometry",geometry},{"revision_id",binding.RevisionId}};}

    private static bool F16BodyEffect(M3PlanState plan,List<M3BodyMeasure> affected,List<M3BodyMeasure> bodies,Dictionary<string,string> fingerprints,out Dictionary<string,object> evidence)
    {
        Dictionary<string,string> beforeOther=new Dictionary<string,string>(StringComparer.Ordinal),afterOther=new Dictionary<string,string>(StringComparer.Ordinal); string target=plan.TargetBody.Tag.ToString(); foreach(KeyValuePair<string,string> x in plan.BodyFingerprintsBefore)if(x.Key!=target)beforeOther.Add(x.Key,x.Value);
        HashSet<string> output=new HashSet<string>(StringComparer.Ordinal);foreach(M3BodyMeasure x in affected)output.Add(x.Body.Tag.ToString());foreach(KeyValuePair<string,string> x in fingerprints)if(!output.Contains(x.Key))afterOther.Add(x.Key,x.Value);
        Dictionary<string,object> comparison;bool others=F12BodyFingerprintsEqual(beforeOther,afterOther,out comparison);M3BodyMeasure prior=FindBodyMeasure(plan.BodiesBefore,plan.TargetBodyId);double? v=AggregateBodyVolume(affected);
        double removed=0;foreach(F16Edge e in plan.EdgeBlendInput) removed+=(1-Math.PI/4)*plan.EdgeBlendRequest.RadiusMm*plan.EdgeBlendRequest.RadiusMm*Math.Abs(e.A[e.Axis]-e.B[e.Axis]);
        double expected=prior!=null && prior.VolumeMm3.HasValue?prior.VolumeMm3.Value-removed:Double.NaN;
        bool ok=affected.Count==1 && affected[0].IsSolid && bodies.Count==plan.BodiesBefore.Count && others && v.HasValue && IsFinite(expected) && Math.Abs(v.Value-expected)<=Math.Max(1e-5,Math.Abs(expected)*1e-7);
        evidence=new Dictionary<string,object>{{"status",ok?"VERIFIED":"MISMATCH"},{"expected_volume_mm3",expected},{"actual_volume_mm3",v},{"nonparticipating_bodies",comparison}};return ok;
    }

    private static bool F16RelatedDefinitionsMatch(Part part,M3PlanState plan,string excluded)
    {return F14HelpersMatch(plan.HoleHelpersBefore,F14CaptureHelpers(part),null,null,null) && F14PersistentHoleDefinitionsEqual(plan.HoleDefinitionsBefore,F14CapturePersistentHoleDefinitions(part,null)) && F15DefinitionsEqual(plan.BooleanDefinitionsBefore,F15CaptureDefinitions(part,null)) && F16DefinitionsEqual(plan.EdgeBlendDefinitionsBefore,F16CaptureDefinitions(part,excluded)) && String.Equals(plan.F12CurveInventoryBefore,M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(part)),StringComparison.Ordinal);}
    private static bool F16DefinitionsEqual(Dictionary<string,object> a,Dictionary<string,object> b) {return a!=null && b!=null && String.Equals(M2Json.CanonicalText(a),M2Json.CanonicalText(b),StringComparison.Ordinal);}
    private static Dictionary<string,object> F16CaptureDefinitions(Part part,string excluded)
    {Dictionary<string,object> result=new Dictionary<string,object>(StringComparer.Ordinal);foreach(Feature f in part.Features) {if(!(f is EdgeBlend) || String.Equals(f.JournalIdentifier,excluded,StringComparison.Ordinal))continue;Dictionary<string,object> raw;try {raw=V2EdgeBlendFactory.Read(part,f);}catch(V2EdgeBlendBuilderCleanupException ex){throw M3AnalysisStateUnknown("existing edge blend cleanup failed",ex);}catch(Exception ex){throw new M2ModelException("UNSUPPORTED_EDGE_BLEND_PRESERVATION","existing edge blend cannot be read ("+ex.GetType().Name+")");} string[] edgeIds=raw["edge_ids"] as string[];
        if(edgeIds==null || new HashSet<string>(edgeIds,StringComparer.Ordinal).Count!=edgeIds.Length) throw new M2ModelException("UNSUPPORTED_EDGE_BLEND_PRESERVATION","existing edge identities are ambiguous");
        Array.Sort(edgeIds,StringComparer.Ordinal); Dictionary<string,object> stable=new Dictionary<string,object>(StringComparer.Ordinal);
        foreach(string key in new string[]{"feature_id","chainset_count","radius_mm","radius_formula","radius_units","section","edge_ids","rule_types","variable_radius_counts","all_instances","roll_onto_edge","roll_over_smooth_edge","limit_failing_areas","patch_complex_geometry_areas","move_sharp_edge","remove_self_intersection","overlap_option","blend_order","blend_face_continuity","convex_concave_y","segment_blend_faces","setback_option","setback_count","trimming_option","tolerance","zero_slope_radius_function_at_chain_ends"})stable.Add(key,raw[key]);
        result.Add(f.JournalIdentifier,stable);}return result;}

    private static bool F16ModelPreserved(M3PlanState plan, Dictionary<string, object> after,
        Dictionary<string, object> definition, out Dictionary<string, object> evidence)
    {
        Dictionary<string, object> before = plan.F12SemanticBefore;
        List<object> errors = new List<object>();
        string[] unchanged = new string[] { "sketches", "native_helical_definitions" };
        foreach (string key in unchanged) if (!ExactProjection(before, after, key, null)) errors.Add(key);
        if (!ExactProjection(before, after, "part", new string[] { "is_modified" })) errors.Add("part");
        if (!ExactProjection(before, after, "identity", new string[] { "is_modified", "support_summary" })) errors.Add("identity");
        if (!ExactSessionParts(before, after) || !PreserveExistingCurveIds(before, after)) errors.Add("session_or_curves");

        HashSet<string> owned = new HashSet<string>(StringComparer.Ordinal);
        owned.Add(plan.FeatureId);
        if (!F14SupportPreserved(before, after, owned)) errors.Add("support");

        List<object> oldFeatures = SnapshotList(before, "features"), newFeatures = SnapshotList(after, "features");
        HashSet<string> targetParents = F12AllowedParentFeatureIds(before, plan);
        HashSet<string> observedNew = new HashSet<string>(StringComparer.Ordinal);
        foreach (object value in newFeatures)
        {
            Dictionary<string, object> record = value as Dictionary<string, object>;
            string id = record == null || !record.ContainsKey("journal_id") ? null : record["journal_id"] as string;
            if (String.IsNullOrEmpty(id)) { errors.Add("feature_identity"); continue; }
            if (!ContainsFeatureId(oldFeatures, id))
            {
                if (!observedNew.Add(id) || !owned.Contains(id)) errors.Add("unexpected_new_feature:" + id);
                foreach (string parent in ReadStringList(record, "parent_ids"))
                    if (!owned.Contains(parent) && !targetParents.Contains(parent)) errors.Add("unexpected_parent:" + id);
            }
        }
        if (!SetEquals(observedNew, owned)) errors.Add("new_feature_set");
        Dictionary<string,object> newFeature=FindSnapshotFeature(after,plan.FeatureId);
        HashSet<string> actualParents=new HashSet<string>(newFeature==null?new List<string>():ReadStringList(newFeature,"parent_ids"),StringComparer.Ordinal);
        bool targetLinked=false; foreach(string parent in actualParents) if(targetParents.Contains(parent)) targetLinked=true;
        if(!targetLinked) errors.Add("target_parent_unproven");
        HashSet<string> parentDependencies=new HashSet<string>(StringComparer.Ordinal);
        foreach(object value in SnapshotList(after,"dependencies")) {
            Dictionary<string,object> edge=value as Dictionary<string,object>; if(edge==null)continue;
            if(Object.Equals(edge["kind"],"FEATURE_PARENT") && Object.Equals(edge["target_id"],plan.FeatureId)) {
                string parent=edge["source_id"] as string;if(parent==null || !parentDependencies.Add(parent))errors.Add("parent_dependency_duplicate");
            }
        }
        if(!parentDependencies.SetEquals(actualParents))errors.Add("parent_dependencies_incomplete");
        foreach (object value in oldFeatures)
        {
            Dictionary<string, object> prior = value as Dictionary<string, object>;
            string id = prior == null || !prior.ContainsKey("journal_id") ? null : prior["journal_id"] as string;
            Dictionary<string, object> current = FindSnapshotFeature(after, id);
            if (current == null) { errors.Add("missing_existing_feature:" + id); continue; }
            bool owner = targetParents.Contains(id);
            string[] omitted = owner ? new string[] { "child_ids", "body_ids" } : new string[] { "child_ids" };
            if (!SemanticValuesEqual(OmitFields(prior, omitted), OmitFields(current, omitted), "feature")) errors.Add("existing_feature_changed:" + id);
            HashSet<string> oldChildren = new HashSet<string>(ReadStringList(prior, "child_ids"), StringComparer.Ordinal);
            HashSet<string> currentChildren = new HashSet<string>(ReadStringList(current, "child_ids"), StringComparer.Ordinal);
            foreach (string child in oldChildren) if (!currentChildren.Contains(child)) errors.Add("existing_child_removed:" + id);
            foreach (string child in currentChildren)
            {
                if (oldChildren.Contains(child)) continue;
                Dictionary<string, object> added = FindSnapshotFeature(after, child);
                if (!owner || !owned.Contains(child) || added == null || !ReadStringList(added, "parent_ids").Contains(id))
                    errors.Add("unexpected_child:" + id);
            }
            if (owner)
            {
                HashSet<string> oldBodies = new HashSet<string>(ReadStringList(prior, "body_ids"), StringComparer.Ordinal);
                HashSet<string> currentBodies = new HashSet<string>(ReadStringList(current, "body_ids"), StringComparer.Ordinal);
                foreach (string body in oldBodies) if (body != plan.TargetBodyId && !currentBodies.Contains(body)) errors.Add("other_body_removed:" + id);
                foreach (string body in currentBodies) if (!oldBodies.Contains(body) && !plan.AffectedBodyIds.Contains(body)) errors.Add("unexpected_body:" + id);
            }
        }

        Dictionary<string, object> newExpressionRows = new Dictionary<string, object>(StringComparer.Ordinal);
        HashSet<string> oldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (object value in SnapshotList(before, "expressions"))
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            if (row == null || !(row["name"] is string) || !oldNames.Add((string)row["name"])) errors.Add("old_expression_identity");
        }
        foreach (object value in SnapshotList(after, "expressions"))
        {
            Dictionary<string, object> row = value as Dictionary<string, object>;
            string name = row == null || !row.ContainsKey("name") ? null : row["name"] as string;
            if (String.IsNullOrEmpty(name)) { errors.Add("expression_identity"); continue; }
            if (!oldNames.Contains(name)) newExpressionRows[name] = row;
        }
        List<Dictionary<string, object>> nativeExpressions = definition.ContainsKey("feature_expressions")
            ? definition["feature_expressions"] as List<Dictionary<string, object>> : null;
        HashSet<string> provedNames = new HashSet<string>(StringComparer.Ordinal);
        if (nativeExpressions == null) errors.Add("native_expression_evidence");
        else foreach (Dictionary<string, object> row in nativeExpressions)
        {
            string name = row.ContainsKey("name") ? row["name"] as string : null;
            if (String.IsNullOrEmpty(name) || !newExpressionRows.ContainsKey(name)) continue;
            string[] users = row.ContainsKey("user_feature_ids") ? row["user_feature_ids"] as string[] : null;
            string owner = row.ContainsKey("owner_feature_id") ? row["owner_feature_id"] as string : null;
            if (users == null || users.Length != 1 || users[0] != plan.FeatureId || (owner != null && !owned.Contains(owner)))
                errors.Add("expression_ownership:" + name);
            else provedNames.Add(name);
        }
        if (!SetEquals(provedNames, new HashSet<string>(newExpressionRows.Keys, StringComparer.Ordinal))) errors.Add("new_expression_set");
        List<object> expectedExpressions = new List<object>();
        foreach (string name in provedNames) expectedExpressions.Add(newExpressionRows[name]);
        bool expressionPreserved; List<object> additions;
        Dictionary<string, object> expressionsEvidence = PreserveExpressionRecords(before, after, expectedExpressions, out expressionPreserved, out additions);
        if (!expressionPreserved) errors.Add("existing_expressions");

        List<object> oldDependencies = SnapshotList(before, "dependencies"), currentDependencies = SnapshotList(after, "dependencies");
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (object edge in currentDependencies) { string k = M2Json.CanonicalText(edge); int n; counts.TryGetValue(k, out n); counts[k] = n + 1; }
        foreach (object edge in oldDependencies) { string k = M2Json.CanonicalText(edge); int n; if (!counts.TryGetValue(k, out n) || n == 0) errors.Add("existing_dependency_removed"); else counts[k] = n - 1; }
        foreach (object value in currentDependencies)
        {
            string key = M2Json.CanonicalText(value); int n = counts[key]; if (n <= 0) continue; counts[key] = n - 1;
            Dictionary<string, object> edge = value as Dictionary<string, object>;
            string kind = edge == null ? null : edge["kind"] as string, source = edge == null ? null : edge["source_id"] as string, target = edge == null ? null : edge["target_id"] as string;
            if (kind == "FEATURE_PARENT" && owned.Contains(target) && (owned.Contains(source) || targetParents.Contains(source))) continue;
            if (kind == "FEATURE_EXPRESSION" && owned.Contains(source) && provedNames.Contains(target)) continue;
            errors.Add("unexpected_dependency");
        }
        evidence = new Dictionary<string, object>(StringComparer.Ordinal) {
            { "status", errors.Count == 0 ? "VERIFIED" : "MISMATCH" }, { "errors", errors },
            { "owned_feature_ids", StringListAsObjects(SortedStrings(owned)) },
            { "owned_expression_names", StringListAsObjects(SortedStrings(provedNames)) },
            { "expression_preservation", expressionsEvidence }
        };
        return errors.Count == 0;
    }


}
