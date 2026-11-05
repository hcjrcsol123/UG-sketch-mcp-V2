using System;
using System.Collections.Generic;
using NXOpen;
using NXOpen.Features;
using NxSketchMcp.M2;

public sealed partial class M2OpenModelAdapter
{
    public M3PlanSnapshot PrepareChamfer(M2ModelBinding binding, M3ChamferRequest source)
    {
        if(source==null || String.IsNullOrWhiteSpace(source.RevisionId) || String.IsNullOrWhiteSpace(source.TargetBodyId) || source.EdgeIds==null || source.EdgeIds.Length!=1 || !IsFinite(source.DistanceMm) || source.DistanceMm<=0 || (source.Mode!="equal_distance" && source.Mode!="two_distances" && source.Mode!="distance_angle") || (source.Mode=="two_distances" && (!IsFinite(source.SecondDistanceMm) || source.SecondDistanceMm<=0)) || (source.Mode=="distance_angle" && (!IsFinite(source.AngleDeg) || source.AngleDeg<=0 || source.AngleDeg>=90))) throw new M2ModelException("INVALID_REQUEST","invalid chamfer request");
        HashSet<string> unique=new HashSet<string>(StringComparer.Ordinal); foreach(string id in source.EdgeIds) if(String.IsNullOrWhiteSpace(id) || id.Length>4096 || !unique.Add(id)) throw new M2ModelException("INVALID_REQUEST","edge IDs must be distinct nonempty strings");
        BindingState state=RequireState(binding); RequireWritableSimplePart(state.WorkPart); RequireMillimeterPartAndUnit(state.WorkPart,"mm","mm");
        if(state.Session.ActiveSketch!=null) throw new M2ModelException("NX_BUSY","end the active Sketch before preparing an chamfer");
        SnapshotResult capture=Capture(binding,state); if(!String.Equals(source.RevisionId,binding.RevisionId,StringComparison.Ordinal)) throw new M2ModelException("STALE_REVISION","revision changed");
        Body target=FindBody(state.WorkPart,source.TargetBodyId); if(target==null || !target.IsSolidBody) throw new M2ModelException("UNSUPPORTED_BODY","target must be a solid box");
        double[] box; double second=source.Mode=="equal_distance" ? source.DistanceMm : source.Mode=="two_distances" ? source.SecondDistanceMm : source.DistanceMm*Math.Tan(source.AngleDeg*Math.PI/180.0);
        if (!IsFinite(second) || second<=0) throw new M2ModelException("INVALID_REQUEST","invalid chamfer second offset");
        List<F16Edge> selected=F16Preflight(target,source.EdgeIds,Math.Max(source.DistanceMm,second),out box);
        int axis=source.ReferenceAxis=="x"?0:source.ReferenceAxis=="y"?1:source.ReferenceAxis=="z"?2:-1;
        if(axis<0 || axis==selected[0].Axis) throw new M2ModelException("INVALID_REQUEST","reference axis must be transverse to the selected edge");
        M3PlanState plan=CreateM3PlanState(binding,state,"create_chamfer",capture,F12MassPropertyAccuracy);
        plan.ChamferRequest=new M3ChamferRequest { RevisionId=source.RevisionId,TargetBodyId=source.TargetBodyId,EdgeIds=(string[])source.EdgeIds.Clone(),DistanceMm=source.DistanceMm,Mode=source.Mode,ReferenceAxis=source.ReferenceAxis,SecondDistanceMm=source.SecondDistanceMm,AngleDeg=source.AngleDeg };
        int j=(selected[0].Axis+1)%3, k=(selected[0].Axis+2)%3;
        string otherAxis = new string[] { "x", "y", "z" }[axis==j ? k : j];
        plan.F17OffsetJ=axis==j?source.DistanceMm:second; plan.F17OffsetK=axis==j?second:source.DistanceMm;
        // NX does not provide a stable mapping from edge identity to its first side.
        // The complete result is checked and one proved swapped result may be corrected.
        plan.F17Reverse=false;
        plan.TargetBody=target; plan.TargetBodyId=source.TargetBodyId; plan.EdgeBlendInput=selected; plan.EdgeBlendBox=box; plan.EdgeBlendEdges=new Edge[selected.Count]; plan.EdgeBlendTags=new string[selected.Count];
        for(int i=0;i<selected.Count;i++) {plan.EdgeBlendEdges[i]=selected[i].Edge;plan.EdgeBlendTags[i]=selected[i].Tag;}
        plan.F12SemanticBefore=capture.Snapshot; plan.F12CurveInventoryBefore=M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart));
        plan.HoleHelpersBefore=F14CaptureHelpers(state.WorkPart); plan.HoleDefinitionsBefore=F14CapturePersistentHoleDefinitions(state.WorkPart,null); plan.BooleanDefinitionsBefore=F15CaptureDefinitions(state.WorkPart,null); plan.EdgeBlendDefinitionsBefore=F16CaptureDefinitions(state.WorkPart,null); plan.ChamferDefinitionsBefore=F17CaptureDefinitions(state.WorkPart,null);
        try {plan.BodyFingerprintsBefore=CaptureF12BodyFingerprints(state.WorkPart,plan.BodiesBefore);} catch(Exception ex) {ThrowIfF12AnalysisStateUnknown(ex,"chamfer prepare fingerprint cleanup failed");throw;}
        M3BodyMeasure prior=FindBodyMeasure(plan.BodiesBefore,source.TargetBodyId); double volume=(box[3]-box[0])*(box[4]-box[1])*(box[5]-box[2]);
        if(prior==null || !prior.VolumeMm3.HasValue || Math.Abs(prior.VolumeMm3.Value-volume)>Math.Max(1e-5,volume*1e-7)) throw new M2ModelException("UNSUPPORTED_EDGE_BLEND_GEOMETRY","target volume does not match a complete rectangular box");
        Dictionary<string,object> view=new Dictionary<string,object>{{"kind","create_chamfer"},{"revision_id",binding.RevisionId},{"target_body_id",source.TargetBodyId},{"edge_ids",source.EdgeIds},{"mode",source.Mode},{"reference_axis",source.ReferenceAxis},{"second_axis",otherAxis},{"distance_mm",source.DistanceMm},{"second_offset_mm",second},{"offset_j_mm",plan.F17OffsetJ},{"offset_k_mm",plan.F17OffsetK},{"j_axis",j},{"k_axis",k},{"angle_deg",source.Mode=="distance_angle"?(object)source.AngleDeg:null},{"target_body_before",prior.PublicRecord}};
        return new M3PlanSnapshot {Kind="create_chamfer",RevisionId=binding.RevisionId,PublicView=view,AdapterState=plan};
    }

    private M3ExecutionResult ExecuteF17ChamferPlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        if(binding==null || prepared==null || plan==null || plan.ChamferRequest==null || prepared.Kind!="create_chamfer") return M3Execution("UNCHANGED",binding==null?null:binding.RevisionId,null,"INVALID_PLAN","chamfer plan unavailable");
        BindingState state; try {state=RequireState(binding);} catch(M2ModelException ex) {return M3Execution("UNCHANGED",binding.RevisionId,null,ex.Code,ex.Message);} string reason;
        if(!Object.ReferenceEquals(state,plan.BindingState) || !String.Equals(plan.RevisionId,prepared.RevisionId,StringComparison.Ordinal) || !IsBindingCurrent(binding,out reason) || !String.Equals(binding.RevisionId,plan.RevisionId,StringComparison.Ordinal)) return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","binding or revision changed");
        try {
            RequireWritableSimplePart(state.WorkPart); RequireMillimeterPartAndUnit(state.WorkPart,"mm","mm"); if(state.Session.ActiveSketch!=null) throw new M2ModelException("NX_BUSY","end the active Sketch");
            SnapshotResult current=Capture(binding,state); List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy); Dictionary<string,object> ignored;
            if(!String.Equals(plan.SemanticBefore,CanonicalSemanticSnapshot(current.Snapshot),StringComparison.Ordinal) || !String.Equals(plan.GeometryBefore,CaptureFullGeometry(state.WorkPart),StringComparison.Ordinal) || !BodyMeasuresEqual(plan.BodiesBefore,bodies,out ignored) || !F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore,CaptureF12BodyFingerprints(state.WorkPart,bodies),out ignored) || !F17RelatedDefinitionsMatch(state.WorkPart,plan,null)) return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","chamfer inputs or model state changed");
        } catch(Exception ex) {M2ModelException unknown=F12AnalysisStateUnknownOrNull(ex,"chamfer precondition cleanup failed"); return unknown!=null?M3Execution("UNKNOWN",binding.RevisionId,null,unknown.Code,unknown.Message):M3Execution("UNCHANGED",binding.RevisionId,null,"MODEL_READ_FAILED","chamfer precondition read failed ("+ex.GetType().Name+")");}
        Session.UndoMarkId mark; try {mark=state.Session.SetUndoMark(Session.MarkVisibility.Invisible,"F17 native chamfer transaction");} catch(Exception ex) {return M3Execution("UNCHANGED",binding.RevisionId,null,"UPDATE_FAILED","undo mark failed ("+ex.GetType().Name+")");}
        try {
            Feature feature=V2ChamferFactory.Create(state.WorkPart,plan.EdgeBlendEdges,plan.ChamferRequest,plan.F17Reverse); plan.Feature=feature; plan.FeatureId=RequiredJournalId(feature,"chamfer feature");
            if(state.Session.UpdateManager.DoUpdate(mark)!=0) throw new M2ModelException("M3_UPDATE_FAILED","chamfer update failed");
            Dictionary<string,object> definition=V2ChamferFactory.Read(state.WorkPart,feature); if(!F17DefinitionMatches(plan,definition)) throw new M2ModelException("F17_DEFINITION_MISMATCH","native chamfer selection or parameters differ");
            SnapshotResult after=Capture(binding,state); plan.BodiesAfter=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy); plan.AffectedBodies=ResolveAffectedBodies(plan,plan.BodiesAfter,feature);
            plan.AffectedBodyIds=new List<string>(); foreach(M3BodyMeasure body in plan.AffectedBodies) plan.AffectedBodyIds.Add(body.Id); plan.AffectedBodyIds.Sort(StringComparer.Ordinal); plan.AffectedBodyId=plan.AffectedBodyIds.Count==1?plan.AffectedBodyIds[0]:null;
            plan.BodyFingerprintsAfter=CaptureF12BodyFingerprints(state.WorkPart,plan.BodiesAfter); plan.ChamferGeometry=CaptureF13RevolveOutputGeometry(state.WorkPart,plan.AffectedBodies);
            Dictionary<string,object> effect,preservation,coverage=null; bool ok=F17BodyEffect(plan,plan.AffectedBodies,plan.BodiesAfter,plan.BodyFingerprintsAfter,out effect) & F16ModelPreserved(plan,after.Snapshot,definition,out preservation);
            bool coverageOk=plan.AffectedBodies.Count==1 && F17Coverage(plan.AffectedBodies[0].Body,plan,out coverage); if(plan.AffectedBodies.Count!=1) coverage=new Dictionary<string,object>{{"status","MISMATCH"}};
            List<object> support; bool supportOk=ValidateM4SupportTracks(state,out support,null); bool related=F17RelatedDefinitionsMatch(state.WorkPart,plan,plan.FeatureId);
            Dictionary<string,object> swappedCoverage=null;
            bool corrected=false, reverseBefore=plan.F17Reverse;
            if (!coverageOk && ok && supportOk && related && plan.AffectedBodies.Count==1 && !F16Near(plan.F17OffsetJ,plan.F17OffsetK)) {
                double oldJ=plan.F17OffsetJ, oldK=plan.F17OffsetK;
                bool exactlySwapped;
                try {plan.F17OffsetJ=oldK; plan.F17OffsetK=oldJ; exactlySwapped=F17Coverage(plan.AffectedBodies[0].Body,plan,out swappedCoverage);}
                finally {plan.F17OffsetJ=oldJ; plan.F17OffsetK=oldK;}
                if (exactlySwapped) {
                    // Retain the actual original BRep and native settings as independent evidence.
                    plan.F17PreCorrectionGeometry=plan.ChamferGeometry;
                    plan.F17PreCorrectionDefinition=definition;
                    V2ChamferFactory.FlipOffsets(state.WorkPart,feature);
                    plan.F17Reverse=!plan.F17Reverse;
                    if(state.Session.UpdateManager.DoUpdate(mark)!=0) throw new M2ModelException("M3_UPDATE_FAILED","chamfer direction update failed");
                    definition=V2ChamferFactory.Read(state.WorkPart,feature);
                    if(!F17DefinitionMatches(plan,definition)) throw new M2ModelException("F17_DEFINITION_MISMATCH","corrected native chamfer definition differs");
                    after=Capture(binding,state); plan.BodiesAfter=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy); plan.AffectedBodies=ResolveAffectedBodies(plan,plan.BodiesAfter,feature);
                    plan.AffectedBodyIds=new List<string>(); foreach(M3BodyMeasure body in plan.AffectedBodies) plan.AffectedBodyIds.Add(body.Id); plan.AffectedBodyIds.Sort(StringComparer.Ordinal); plan.AffectedBodyId=plan.AffectedBodyIds.Count==1?plan.AffectedBodyIds[0]:null;
                    plan.BodyFingerprintsAfter=CaptureF12BodyFingerprints(state.WorkPart,plan.BodiesAfter); plan.ChamferGeometry=CaptureF13RevolveOutputGeometry(state.WorkPart,plan.AffectedBodies);
                    ok=F17BodyEffect(plan,plan.AffectedBodies,plan.BodiesAfter,plan.BodyFingerprintsAfter,out effect) & F16ModelPreserved(plan,after.Snapshot,definition,out preservation);
                    coverage=null; coverageOk=plan.AffectedBodies.Count==1 && F17Coverage(plan.AffectedBodies[0].Body,plan,out coverage);
                    if(plan.AffectedBodies.Count!=1) coverage=new Dictionary<string,object>{{"status","MISMATCH"}};
                    supportOk=ValidateM4SupportTracks(state,out support,null); related=F17RelatedDefinitionsMatch(state.WorkPart,plan,plan.FeatureId);
                    corrected=true;
                }
            }
            if(!ok || !coverageOk || !supportOk || !related) { plan.ExecutionFailureEvidence=new Dictionary<string,object>{{"body_effect",effect},{"model_preservation",preservation},{"coverage",coverage},{"existing_definitions_preserved",related},{"support_associations",support}}; throw new M2ModelException("F17_POSTCONDITION_FAILED","chamfer effect or model preservation could not be proven"); }
            plan.ExecutionFailureEvidence=new Dictionary<string,object>{{"coverage",coverage},{"swapped_pre_correction_coverage",swappedCoverage},{"pre_correction_geometry",plan.F17PreCorrectionGeometry},{"pre_correction_definition",plan.F17PreCorrectionDefinition},{"correction_count",corrected?1:0},{"reverse_before",reverseBefore},{"reverse_after",plan.F17Reverse}};
            if(plan.ForcePostCommitVerificationFailureForTesting) throw new M2ModelException("F17_FORCED_POST_COMMIT_FAILURE","test-only failure after chamfer verification and any direction correction");
            plan.F17ReverseBefore=reverseBefore; plan.F17Corrected=corrected; plan.F17SwappedCoverage=swappedCoverage;
            plan.ChamferDefinition=definition; plan.SemanticAfter=CanonicalSemanticSnapshot(after.Snapshot); plan.GeometryAfter=CaptureFullGeometry(state.WorkPart); plan.HistoryAfter=HistoryProjection(after.Snapshot); plan.F12CurveInventoryAfter=M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart));
            Dictionary<string,object> result=F17Result(binding,plan,definition,plan.AffectedBodies,plan.ChamferGeometry); result["coverage"]=coverage;result["body_effect"]=effect;result["model_preservation"]=preservation;result["direction_corrected"]=corrected;
            plan.Execution=M3Execution("APPLIED",binding.RevisionId,result,null,null);return plan.Execution;
        } catch(Exception ex) {Exception failure=(ex is V2ChamferBuilderCleanupException || ex is F12BodyGeometrySnapshot.CleanupException)?(Exception)new M2ModelException("M3_ANALYSIS_STATE_UNKNOWN","chamfer analysis resource cleanup failed"):ex; plan.Execution=RestoreM3State(binding,state,plan,mark,failure);return plan.Execution;} finally {TryDeleteUndoMark(state.Session,mark);}
    }

    private M3VerificationResult VerifyF17ChamferPlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        if(binding==null || prepared==null || plan==null || plan.ChamferRequest==null || plan.Execution==null || plan.Execution.State!="APPLIED") return M3VerificationFailure(binding==null?null:binding.RevisionId,"fresh verification requires an applied chamfer");
        BindingState state=RequireState(binding);string reason; if(!Object.ReferenceEquals(state,plan.BindingState) || !IsBindingCurrent(binding,out reason)) return M3VerificationFailure(binding.RevisionId,"binding changed");
        Feature feature=FindFeature(state.WorkPart,plan.FeatureId); Dictionary<string,object> definition; try {definition=V2ChamferFactory.Read(state.WorkPart,feature);} catch(V2ChamferBuilderCleanupException ex) {throw M3AnalysisStateUnknown("chamfer readback cleanup failed",ex);}
        SnapshotResult capture=Capture(binding,state);List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(state.WorkPart,F12MassPropertyAccuracy), affected=ResolveAffectedBodies(plan,bodies,feature);Dictionary<string,string> fingerprints=CaptureF12BodyFingerprints(state.WorkPart,bodies);
        Dictionary<string,object> geometry=CaptureF13RevolveOutputGeometry(state.WorkPart,affected),effect,preservation,bodyEvidence,fingerprintEvidence,coverage=null;
        bool native=F17DefinitionMatches(plan,definition) && String.Equals(M2Json.CanonicalText(plan.ChamferDefinition),M2Json.CanonicalText(definition),StringComparison.Ordinal);
        bool effectOk=F17BodyEffect(plan,affected,bodies,fingerprints,out effect); bool coverageOk; try {coverageOk=affected.Count==1 && F17Coverage(affected[0].Body,plan,out coverage);} catch(F12BodyGeometrySnapshot.CleanupException ex) {throw M3AnalysisStateUnknown("chamfer geometry audit cleanup failed",ex);} if(affected.Count!=1) coverage=new Dictionary<string,object>{{"status","MISMATCH"}};
        bool preserve=F16ModelPreserved(plan,capture.Snapshot,definition,out preservation) && F17RelatedDefinitionsMatch(state.WorkPart,plan,plan.FeatureId);
        bool sameBodies=BodyMeasurementRecordsEqual(BodyMeasureRecords(plan.BodiesAfter,null),BodyMeasureRecords(bodies,null),out bodyEvidence) && F12BodyFingerprintsEqual(plan.BodyFingerprintsAfter,fingerprints,out fingerprintEvidence);
        bool sameGeometry=String.Equals(M2Json.CanonicalText(plan.ChamferGeometry),M2Json.CanonicalText(geometry),StringComparison.Ordinal);
        bool history=String.Equals(plan.SemanticAfter,CanonicalSemanticSnapshot(capture.Snapshot),StringComparison.Ordinal) && String.Equals(plan.GeometryAfter,CaptureFullGeometry(state.WorkPart),StringComparison.Ordinal) && SemanticValuesEqual(plan.HistoryAfter,HistoryProjection(capture.Snapshot),"history") && String.Equals(plan.F12CurveInventoryAfter,M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart)),StringComparison.Ordinal);
        List<object> support; bool supportOk=ValidateM4SupportTracks(state,out support,null); bool verified=native && effectOk && coverageOk && preserve && sameBodies && sameGeometry && history && supportOk;
        Dictionary<string,object> checks=new Dictionary<string,object>{{"chamfer_definition",VerificationCheck(native?"VERIFIED":"MISMATCH",definition)},{"model_preservation",VerificationCheck(preserve?"VERIFIED":"MISMATCH",preservation)},{"body_effect",VerificationCheck(effectOk?"VERIFIED":"MISMATCH",effect)},{"geometry",VerificationCheck(sameGeometry&&coverageOk?"VERIFIED":"MISMATCH",coverage)},{"bodies",VerificationCheck(sameBodies?"VERIFIED":"MISMATCH",bodyEvidence)},{"history",VerificationCheck(history?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"matches_execute",history}})},{"support_associations",VerificationCheck(supportOk?"VERIFIED":"MISMATCH",new Dictionary<string,object>{{"associations",support}})}};
        Dictionary<string,object> result=F17Result(binding,plan,definition,affected,geometry);result["coverage"]=coverage;
        return new M3VerificationResult {Verified=verified,RevisionId=binding.RevisionId,Checks=checks,Result=result,ErrorCode=verified?null:"PART_VERIFICATION_FAILED",ErrorMessage=verified?null:"Fresh chamfer definition, output or preservation differs."};
    }

    private static bool F17DefinitionMatches(M3PlanState plan,Dictionary<string,object> d)
    {
        string[] tags=d["edge_tags"] as string[];
        string expected=plan.ChamferRequest.Mode=="equal_distance"?"SymmetricOffsets":plan.ChamferRequest.Mode=="two_distances"?"TwoOffsets":"OffsetAndAngle";
        Dictionary<string,object> first=(Dictionary<string,object>)d["first_offset"],second=(Dictionary<string,object>)d["second_offset"],angle=(Dictionary<string,object>)d["angle"];
        double expectedSecond=plan.ChamferRequest.Mode=="equal_distance"?plan.ChamferRequest.DistanceMm:plan.ChamferRequest.Mode=="two_distances"?plan.ChamferRequest.SecondDistanceMm:plan.ChamferRequest.DistanceMm*Math.Tan(plan.ChamferRequest.AngleDeg*Math.PI/180.0);
        return tags!=null && tags.Length==1 && tags[0]==plan.EdgeBlendTags[0] && Object.Equals(d["option"],expected) && Object.Equals(d["method"],"EdgesAlongFaces") && Object.Equals(d["reverse_offsets"],plan.F17Reverse) && Object.Equals(d["all_instances"],false) &&
            Object.Equals(first["units"],"MilliMeter") && Object.Equals(second["units"],"MilliMeter") && Object.Equals(angle["units"],"Degrees") &&
            F16Near((double)first["value"],plan.ChamferRequest.DistanceMm) && F16Near((double)second["value"],expectedSecond) &&
            (plan.ChamferRequest.Mode!="distance_angle" || Math.Abs((double)angle["value"]-plan.ChamferRequest.AngleDeg)<1e-6);
    }
    private static Dictionary<string,object> F17Result(M2ModelBinding binding,M3PlanState plan,Dictionary<string,object> definition,List<M3BodyMeasure> affected,Dictionary<string,object> geometry)
    {List<string> ids=new List<string>();foreach(M3BodyMeasure b in affected)ids.Add(b.Id);ids.Sort(StringComparer.Ordinal);int j=(plan.EdgeBlendInput[0].Axis+1)%3,k=(plan.EdgeBlendInput[0].Axis+2)%3;return new Dictionary<string,object>{{"kind","create_chamfer"},{"feature_id",plan.FeatureId},{"body_id",ids.Count==1?ids[0]:null},{"body_ids",StringListAsObjects(ids)},{"bodies",BodyMeasureRecords(affected,null)},{"target_body_id",plan.TargetBodyId},{"edge_ids",plan.ChamferRequest.EdgeIds},{"mode",plan.ChamferRequest.Mode},{"reference_axis",plan.ChamferRequest.ReferenceAxis},{"second_axis",new string[]{"x","y","z"}[plan.ChamferRequest.ReferenceAxis==new string[]{"x","y","z"}[j]?k:j]},{"distance_mm",plan.ChamferRequest.DistanceMm},{"second_offset_mm",plan.ChamferRequest.ReferenceAxis==new string[]{"x","y","z"}[j]?plan.F17OffsetK:plan.F17OffsetJ},{"offset_j_mm",plan.F17OffsetJ},{"offset_k_mm",plan.F17OffsetK},{"j_axis",j},{"k_axis",k},{"correction_count",plan.F17Corrected?1:0},{"reverse_before",plan.F17ReverseBefore},{"reverse_after",plan.F17Reverse},{"swapped_pre_correction_coverage",plan.F17SwappedCoverage},{"pre_correction_geometry",plan.F17PreCorrectionGeometry},{"pre_correction_definition",plan.F17PreCorrectionDefinition},{"native_definition",definition},{"result_geometry",geometry},{"revision_id",binding.RevisionId}};}

    private static bool F17BodyEffect(M3PlanState plan,List<M3BodyMeasure> affected,List<M3BodyMeasure> bodies,Dictionary<string,string> fingerprints,out Dictionary<string,object> evidence)
    {
        Dictionary<string,string> beforeOther=new Dictionary<string,string>(StringComparer.Ordinal),afterOther=new Dictionary<string,string>(StringComparer.Ordinal); string target=plan.TargetBody.Tag.ToString(); foreach(KeyValuePair<string,string> x in plan.BodyFingerprintsBefore)if(x.Key!=target)beforeOther.Add(x.Key,x.Value);
        HashSet<string> output=new HashSet<string>(StringComparer.Ordinal);foreach(M3BodyMeasure x in affected)output.Add(x.Body.Tag.ToString());foreach(KeyValuePair<string,string> x in fingerprints)if(!output.Contains(x.Key))afterOther.Add(x.Key,x.Value);
        Dictionary<string,object> comparison;bool others=F12BodyFingerprintsEqual(beforeOther,afterOther,out comparison);M3BodyMeasure prior=FindBodyMeasure(plan.BodiesBefore,plan.TargetBodyId);double? v=AggregateBodyVolume(affected);
        double removed=0;foreach(F16Edge e in plan.EdgeBlendInput) removed+=0.5*plan.F17OffsetJ*plan.F17OffsetK*Math.Abs(e.A[e.Axis]-e.B[e.Axis]);
        double expected=prior!=null && prior.VolumeMm3.HasValue?prior.VolumeMm3.Value-removed:Double.NaN;
        bool ok=affected.Count==1 && affected[0].IsSolid && bodies.Count==plan.BodiesBefore.Count && others && v.HasValue && IsFinite(expected) && Math.Abs(v.Value-expected)<=Math.Max(1e-5,Math.Abs(expected)*1e-7);
        evidence=new Dictionary<string,object>{{"status",ok?"VERIFIED":"MISMATCH"},{"expected_volume_mm3",expected},{"actual_volume_mm3",v},{"nonparticipating_bodies",comparison}};return ok;
    }

    private static bool F17RelatedDefinitionsMatch(Part part,M3PlanState plan,string excluded)
    {return F16RelatedDefinitionsMatch(part,plan,null) && F16DefinitionsEqual(plan.ChamferDefinitionsBefore,F17CaptureDefinitions(part,excluded));}

    private static Dictionary<string,object> F17CaptureDefinitions(Part part,string excluded)
    {
        Dictionary<string,object> result=new Dictionary<string,object>(StringComparer.Ordinal);
        foreach(Feature f in part.Features) {
            if(!(f is Chamfer) || String.Equals(f.JournalIdentifier,excluded,StringComparison.Ordinal))continue;
            Dictionary<string,object> raw;
            try {raw=V2ChamferFactory.Read(part,f);} catch(V2ChamferBuilderCleanupException ex){throw M3AnalysisStateUnknown("existing chamfer cleanup failed",ex);} catch(Exception ex){throw new M2ModelException("UNSUPPORTED_CHAMFER_PRESERVATION","existing chamfer cannot be read ("+ex.GetType().Name+")");}
            Dictionary<string,object> stable=new Dictionary<string,object>(StringComparer.Ordinal);
            foreach(string key in new string[]{"feature_id","option","method","reverse_offsets","all_instances","save_feature","tolerance","edge_ids","rule_types","rule_edges_match_collector"}) stable.Add(key,raw[key]);
            foreach(string key in new string[]{"first_offset","second_offset","angle"}) {Dictionary<string,object> exp=(Dictionary<string,object>)raw[key]; stable.Add(key,new Dictionary<string,object>{{"value",exp["value"]},{"units",exp["units"]},{"rhs",exp["rhs"]}});}
            result.Add(f.JournalIdentifier,stable);
        }
        return result;
    }
}
