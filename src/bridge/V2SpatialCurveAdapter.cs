using System;
using System.Collections.Generic;
using NXOpen;
using NxSketchMcp.M2;

public sealed partial class M2OpenModelAdapter
{
    // A Part.Curves member is spatial only when it is not owned by any Sketch.
    private static List<object> F20SpatialRecords(Part part, bool includeTag)
    {
        HashSet<string> sketchTags=new HashSet<string>(StringComparer.Ordinal);
        foreach(Sketch sketch in part.Sketches.ToArray())
        {
            if(sketch==null)throw new M2ModelException("MODEL_READ_FAILED","null sketch in spatial curve inventory");
            NXObject[] members=sketch.GetAllGeometry();
            if(members==null)throw new M2ModelException("MODEL_READ_FAILED","sketch membership unavailable");
            foreach(NXObject item in members)if(item!=null)sketchTags.Add(item.Tag.ToString());
        }
        List<Dictionary<string,object>> rows=new List<Dictionary<string,object>>();
        HashSet<string> ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(Curve curve in part.Curves.ToArray())
        {
            if(curve==null)throw new M2ModelException("MODEL_READ_FAILED","null part curve");
            if(sketchTags.Contains(curve.Tag.ToString()))continue;
            string id=RequiredJournalId(curve,"spatial curve");
            if(!ids.Add(id))throw new M2ModelException("MODEL_READ_FAILED","spatial curve JournalIdentifier is ambiguous");
            List<object> records=BuildSketchGeometry(new NXObject[]{curve},"mm");
            if(records==null || records.Count!=1 || !(records[0] is Dictionary<string,object>))throw new M2ModelException("MODEL_READ_FAILED","spatial curve readback failed");
            Dictionary<string,object> row=(Dictionary<string,object>)records[0];
            row["journal_id"]=id; row["native_type"]=curve.GetType().Name;
            if(includeTag)row["tag"]=curve.Tag.ToString();
            rows.Add(row);
        }
        SortRecords(rows,"journal_id","spatial curve"); return ToObjectList(rows);
    }

    private static List<object> F20PersistentRecords(List<object> input)
    {
        List<object> result=new List<object>();
        foreach(object value in input)
        {
            Dictionary<string,object> row=DeepCloneValue(value) as Dictionary<string,object>;
            if(row==null || !Object.Equals(row["detail_status"],"SUPPORTED"))throw new M2ModelException("UNSUPPORTED_PART_GEOMETRY","spatial curve lacks complete persistent geometry readback");
            string id=row["journal_id"] as string, nativeType=row["native_type"] as string;
            if(String.IsNullOrEmpty(id) || (nativeType!="Line" && nativeType!="Arc"))
                throw new M2ModelException("UNSUPPORTED_PART_GEOMETRY","SaveCopy spatial curve association supports uniquely identified native Line and Arc only");
            row.Remove("tag");row.Remove("journal_id");
            foreach(object existing in result)
            {
                Dictionary<string,object> prior=(Dictionary<string,object>)existing;
                if(SemanticValuesEqual(prior["geometry"],row,"spatial_curve"))
                    throw new M2ModelException("UNSUPPORTED_ASSOCIATION_PERSISTENCE","geometrically indistinguishable spatial curves cannot be uniquely reidentified after reopen");
            }
            result.Add(new Dictionary<string,object>{{"journal_id",id},{"geometry",row}});
        }
        return result;
    }

    private static bool F20PersistentRecordsMatch(List<object> before,List<object> after,out List<object> mappings)
    {
        mappings=new List<object>();if(before.Count!=after.Count)return false;
        bool[] consumed=new bool[after.Count];bool ok=true;
        foreach(object value in before)
        {
            Dictionary<string,object> source=(Dictionary<string,object>)value;int match=-1,count=0;
            for(int i=0;i<after.Count;i++)
            {
                Dictionary<string,object> candidate=(Dictionary<string,object>)after[i];
                if(!consumed[i] && SemanticValuesEqual(source["geometry"],candidate["geometry"],"spatial_curve")){match=i;count++;}
            }
            if(count!=1){ok=false;mappings.Add(new Dictionary<string,object>{{"before_id",source["journal_id"]},{"matching_after_count",count}});continue;}
            consumed[match]=true;Dictionary<string,object> target=(Dictionary<string,object>)after[match];
            mappings.Add(new Dictionary<string,object>{{"before_id",source["journal_id"]},{"after_id",target["journal_id"]},
                {"native_type",((Dictionary<string,object>)source["geometry"])["native_type"]},{"geometry_matches",true}});
        }
        foreach(bool used in consumed)if(!used)ok=false;
        return ok;
    }

    private static string F20SemanticWithoutCurves(Dictionary<string,object> snapshot)
    {
        Dictionary<string,object> copy=DeepCloneValue(snapshot) as Dictionary<string,object>;
        copy.Remove("curve_ids");copy.Remove("spatial_curves");return CanonicalSemanticSnapshot(copy);
    }

    public M3PlanSnapshot PrepareSpatialCurve(M2ModelBinding binding,M3SpatialCurveRequest request)
    {
        F20SpatialGeometry geometry;
        try { geometry=F20SpatialGeometry.FromRequest(request); }
        catch(ArgumentException ex) { throw new M2ModelException("INVALID_REQUEST",ex.Message); }
        BindingState state=RequireState(binding); RequireWritableSimplePart(state.WorkPart);RequireMillimeterPartAndUnit(state.WorkPart,"mm","mm");
        if(state.Session.ActiveSketch!=null)throw new M2ModelException("NX_BUSY","end active sketch before creating independent spatial curves");
        bool dirty=state.WorkPart.IsModified; string inventory=F19Inventory(state.WorkPart);
        SnapshotResult capture=Capture(binding,state);
        if(binding.RevisionId!=request.RevisionId)throw new M2ModelException("STALE_REVISION","revision changed");
        M3PlanState plan=CreateM3PlanState(binding,state,"create_spatial_curve",capture);
        plan.SpatialCurveRequest=new M3SpatialCurveRequest { RevisionId=request.RevisionId, CurveType=request.CurveType, DefinitionMode=request.DefinitionMode };
        plan.F20Geometry=geometry;plan.F20InventoryBefore=inventory;
        plan.F20SemanticBefore=F20SemanticWithoutCurves(capture.Snapshot);
        plan.F20CurvesBefore=CaptureM3SketchProjectionCurveInventory(state.WorkPart);
        plan.F12CurveInventoryBefore=M3SketchProjectionCurveInventoryText(plan.F20CurvesBefore);
        plan.BodyFingerprintsBefore=CaptureF12BodyFingerprints(state.WorkPart,plan.BodiesBefore);
        if(state.WorkPart.IsModified!=dirty || F19Inventory(state.WorkPart)!=inventory)throw M3AnalysisStateUnknown("spatial curve preparation changed model state",null);
        Dictionary<string,object> view=new Dictionary<string,object>{{"kind","create_spatial_curve"},{"curve_type",request.CurveType},{"definition_mode",request.DefinitionMode},
            {"revision_id",binding.RevisionId},{"start_mm",F20Copy(geometry.Start)},{"end_mm",F20Copy(geometry.End)},{"through_mm",F20Copy(geometry.Through)},
            {"center_mm",F20Copy(geometry.Center)},{"normal",F20Copy(geometry.Normal)},{"x_axis",F20Copy(geometry.X)},{"radius_mm",geometry.Radius},{"signed_sweep_rad",geometry.SweepRadians}};
        return new M3PlanSnapshot{Kind="create_spatial_curve",RevisionId=binding.RevisionId,PublicView=view,AdapterState=plan};
    }

    private static Point3d F20Point(double[] p){return new Point3d(p[0],p[1],p[2]);}
    private static double[] F20Copy(double[] p){return p==null?null:(double[])p.Clone();}
    private static Vector3d F20Vector(double[] p){return new Vector3d(p[0],p[1],p[2]);}
    private static double F20Distance(Point3d p,double[] q){double x=p.X-q[0],y=p.Y-q[1],z=p.Z-q[2];return Math.Sqrt(x*x+y*y+z*z);}
    private static double F20VectorDistance(Vector3d p,double[] q){double x=p.X-q[0],y=p.Y-q[1],z=p.Z-q[2];return Math.Sqrt(x*x+y*y+z*z);}
    private static bool F20ReadGeometry(Curve curve,F20SpatialGeometry expected,out Dictionary<string,object> evidence)
    {
        bool ok=false; evidence=new Dictionary<string,object>{{"curve_id",curve==null?null:curve.JournalIdentifier},{"native_curve_type",curve==null?null:curve.GetType().Name}};
        if(curve!=null)evidence["native_length_mm"]=curve.GetLength();
        Line line=curve as Line;Arc arc=curve as Arc;
        if(expected.Type=="line" && line!=null)
        {
            ok=F20Distance(line.StartPoint,expected.Start)<=1e-6 && F20Distance(line.EndPoint,expected.End)<=1e-6;
            evidence["start_mm"]=Point(line.StartPoint);evidence["end_mm"]=Point(line.EndPoint);
        }
        else if(expected.Type!="line" && arc!=null)
        {
            Point3d center;Vector3d x,y;arc.GetOrientation(out center,out x,out y);
            Vector3d normal=Cross(x,y); double sign=expected.SweepRadians<0?-1:1;
            double[] nativeNormal=new double[]{sign*expected.Normal[0],sign*expected.Normal[1],sign*expected.Normal[2]};
            Point3d start=ConicPoint(center,x,y,arc.Radius,arc.Radius,0,arc.StartAngle);
            Point3d end=ConicPoint(center,x,y,arc.Radius,arc.Radius,0,arc.EndAngle);
            Point3d mid=ConicPoint(center,x,y,arc.Radius,arc.Radius,0,(arc.StartAngle+arc.EndAngle)/2);
            double sweep=arc.EndAngle-arc.StartAngle;
            ok=F20Distance(center,expected.Center)<=1e-6 && Math.Abs(arc.Radius-expected.Radius)<=1e-6 &&
                F20VectorDistance(normal,nativeNormal)<=1e-7 && F20Distance(start,expected.Start)<=1e-6 &&
                F20Distance(end,expected.End)<=1e-6 && F20Distance(mid,expected.Point(expected.StartRadians+expected.SweepRadians/2))<=1e-6 &&
                Math.Abs(sweep-Math.Abs(expected.SweepRadians))<=1e-7;
            if(expected.Through!=null)
            {
                double[] t=new double[]{expected.Through[0]-center.X,expected.Through[1]-center.Y,expected.Through[2]-center.Z};
                double angle=Math.Atan2(t[0]*y.X+t[1]*y.Y+t[2]*y.Z,t[0]*x.X+t[1]*x.Y+t[2]*x.Z);
                while(angle<arc.StartAngle)angle+=2*Math.PI;while(angle>arc.StartAngle+2*Math.PI)angle-=2*Math.PI;
                Point3d actual=ConicPoint(center,x,y,arc.Radius,arc.Radius,0,angle);
                ok=ok && angle>arc.StartAngle && angle<arc.EndAngle && F20Distance(actual,expected.Through)<=1e-6;
                evidence["through_mm"]=Point(actual);evidence["requested_through_mm"]=F20Copy(expected.Through);evidence["through_parameter_rad"]=angle;
            }
            evidence["center_mm"]=Point(center);evidence["radius_mm"]=arc.Radius;evidence["start_mm"]=Point(start);evidence["end_mm"]=Point(end);
            evidence["mid_mm"]=Point(mid);evidence["native_normal"]=Vector(normal.X,normal.Y,normal.Z);evidence["requested_normal"]=expected.Normal;
            evidence["native_sweep_rad"]=sweep;evidence["requested_signed_sweep_rad"]=expected.SweepRadians;
            evidence["native_direction_mapping"]=sign<0?"reversed_native_normal_for_negative_sweep":"same_normal";
            evidence["curve_type"]=expected.Type;
        }
        evidence["status"]=ok?"VERIFIED":"MISMATCH";return ok;
    }

    private static bool F20Preserved(Part part,M3PlanState plan,SnapshotResult capture,out Dictionary<string,object> evidence)
    {
        Dictionary<string,M3SketchProjectionCurveState> now=CaptureM3SketchProjectionCurveInventory(part);
        bool old=now.Count==plan.F20CurvesBefore.Count+1;
        List<object> priorDifferences=new List<object>(), priorIdentityMap=new List<object>();
        HashSet<string> currentIds=new HashSet<string>(StringComparer.Ordinal);bool uniqueIds=true;
        foreach(M3SketchProjectionCurveState currentRow in now.Values)
            if(String.IsNullOrEmpty(currentRow.Id) || !currentIds.Add(currentRow.Id))uniqueIds=false;
        foreach(KeyValuePair<string,M3SketchProjectionCurveState> row in plan.F20CurvesBefore)
        {
            M3SketchProjectionCurveState current;
            bool found=now.TryGetValue(row.Key,out current);
            bool sameObject=found && row.Value.Curve.Equals(current.Curve) && row.Value.Curve.Tag.Equals(current.Curve.Tag);
            bool sameSignature=found && current.Signature==row.Value.Signature;
            priorIdentityMap.Add(new Dictionary<string,object>{{"tag",row.Key},{"before_id",row.Value.Id},{"after_id",found?current.Id:null},
                {"same_native_object",sameObject},{"same_native_signature",sameSignature},{"journal_id_changed",found && current.Id!=row.Value.Id}});
            if(!sameObject || !sameSignature)
            {
                old=false;
                priorDifferences.Add(new Dictionary<string,object>{{"tag",row.Key},{"before_id",row.Value.Id},{"after_id",current==null?null:current.Id},
                    {"before_signature",row.Value.Signature},{"after_signature",current==null?null:current.Signature}});
            }
        }
        M3SketchProjectionCurveState created;bool unique=now.TryGetValue(plan.F20CurveTag,out created) && !plan.F20CurvesBefore.ContainsKey(plan.F20CurveTag) && created.Id==plan.F20CurveId;
        bool standalone=false;foreach(object obj in F20SpatialRecords(part,true)){Dictionary<string,object> row=(Dictionary<string,object>)obj;if(Object.Equals(row["tag"],plan.F20CurveTag) && Object.Equals(row["journal_id"],plan.F20CurveId))standalone=true;}
        old=old && uniqueIds;
        bool semantic=plan.F20SemanticBefore==F20SemanticWithoutCurves(capture.Snapshot);
        bool inventory=plan.F20InventoryBefore==F19Inventory(part,plan.F20CurveTag);
        List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(part,M3LegacyMassPropertyAccuracy);Dictionary<string,object> measurements,prints;
        bool body=BodyMeasuresEqual(plan.BodiesBefore,bodies,out measurements) && F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore,CaptureF12BodyFingerprints(part,bodies),out prints);
        bool sketches=plan.GeometryBefore==CaptureFullGeometry(part);
        evidence=new Dictionary<string,object>{{"prior_curves",old},{"unique_new_curve",unique},{"independent_of_sketch",standalone},
            {"prior_curve_differences",priorDifferences},{"prior_curve_identity_map",priorIdentityMap},{"current_curve_ids_unique",uniqueIds},
            {"identity_rule","JournalIdentifiers can be renumbered on insertion; re-inspect current IDs after the revision changes; Tags are session-local"},
            {"other_semantic_state",semantic},{"inventory",inventory},{"body_geometry",body},{"sketch_geometry",sketches}};
        bool ok=old && unique && standalone && semantic && inventory && body && sketches;evidence["status"]=ok?"VERIFIED":"MISMATCH";return ok;
    }

    private M3ExecutionResult ExecuteF20SpatialCurvePlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        BindingState state;try{state=RequireState(binding);}catch(M2ModelException ex){return M3Execution("UNCHANGED",binding.RevisionId,null,ex.Code,ex.Message);}
        string reason;if(prepared.Kind!="create_spatial_curve" || !Object.ReferenceEquals(state,plan.BindingState) || !IsBindingCurrent(binding,out reason) || binding.RevisionId!=plan.RevisionId)
            return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","binding or revision changed");
        try
        {
            RequireWritableSimplePart(state.WorkPart);RequireMillimeterPartAndUnit(state.WorkPart,"mm","mm");
            if(state.Session.ActiveSketch!=null)throw new M2ModelException("NX_BUSY","end active sketch");
            SnapshotResult current=Capture(binding,state);Dictionary<string,object> ignored;
            List<M3BodyMeasure> bodies=CaptureM3BodyMeasures(state.WorkPart,M3LegacyMassPropertyAccuracy);
            if(binding.RevisionId!=plan.RevisionId || state.WorkPart.IsModified!=plan.WorkPartModifiedBefore || CanonicalSemanticSnapshot(current.Snapshot)!=plan.SemanticBefore ||
                F19Inventory(state.WorkPart)!=plan.F20InventoryBefore || M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart))!=plan.F12CurveInventoryBefore ||
                !BodyMeasuresEqual(plan.BodiesBefore,bodies,out ignored) || !F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore,CaptureF12BodyFingerprints(state.WorkPart,bodies),out ignored))
                return M3Execution("UNCHANGED",binding.RevisionId,null,"STALE_PLAN","model changed since preparation");
        }
        catch(Exception ex){M2ModelException unknown=F12AnalysisStateUnknownOrNull(ex,"spatial curve precondition cleanup failed");return unknown!=null?M3Execution("UNKNOWN",binding.RevisionId,null,unknown.Code,unknown.Message):M3Execution("UNCHANGED",binding.RevisionId,null,"MODEL_READ_FAILED","spatial curve precondition failed ("+ex.GetType().Name+")");}
        Session.UndoMarkId mark;try{mark=state.Session.SetUndoMark(Session.MarkVisibility.Invisible,"F20 spatial curve transaction");}catch(Exception ex){return M3Execution("UNCHANGED",binding.RevisionId,null,"UPDATE_FAILED","undo mark failed ("+ex.GetType().Name+")");}
        try
        {
            F20SpatialGeometry g=plan.F20Geometry;Curve created;
            if(g.Type=="line")created=state.WorkPart.Curves.CreateLine(F20Point(g.Start),F20Point(g.End));
            else
            {
                double sign=g.SweepRadians<0?-1:1;double[] y=new double[]{sign*g.Y[0],sign*g.Y[1],sign*g.Y[2]};
                double start=sign*g.StartRadians;while(start<0)start+=2*Math.PI;while(start>=2*Math.PI)start-=2*Math.PI;
                created=state.WorkPart.Curves.CreateArc(F20Point(g.Center),F20Vector(g.X),F20Vector(y),g.Radius,start,start+Math.Abs(g.SweepRadians));
            }
            if(created==null)throw new M2ModelException("F20_CREATE_FAILED","NX returned no curve");
            plan.F20CurveId=RequiredJournalId(created,"created spatial curve");plan.F20CurveTag=created.Tag.ToString();
            if(state.Session.UpdateManager.DoUpdate(mark)!=0)throw new M2ModelException("F20_UPDATE_FAILED","NX update failed");
            SnapshotResult after=Capture(binding,state);Dictionary<string,object> geometry,preservation;
            bool measured=F20ReadGeometry(created,g,out geometry), preserved=F20Preserved(state.WorkPart,plan,after,out preservation);
            plan.ExecutionFailureEvidence=new Dictionary<string,object>{{"geometry",geometry},{"model_preservation",preservation}};
            if(!measured || !preserved)throw new M2ModelException("F20_POSTCONDITION_FAILED","spatial curve geometry or preservation failed");
            if(plan.ForcePostCommitVerificationFailureForTesting)throw new M2ModelException("F20_FORCED_POST_COMMIT_FAILURE","test-only failure after verification");
            plan.F20GeometryAfter=M2Json.CanonicalText(geometry);plan.SemanticAfter=CanonicalSemanticSnapshot(after.Snapshot);
            Dictionary<string,object> result=new Dictionary<string,object>{{"kind","create_spatial_curve"},{"curve_id",plan.F20CurveId},{"native_curve_type",created.GetType().Name},
                {"geometry",geometry},{"model_preservation",preservation},{"revision_id",binding.RevisionId}};
            plan.Execution=M3Execution("APPLIED",binding.RevisionId,result,null,null);return plan.Execution;
        }
        catch(Exception ex)
        {
            plan.Execution=RestoreM3State(binding,state,plan,mark,ex);
            if(plan.Execution.State=="ROLLED_BACK")
            {
                Dictionary<string,object> rollbackBodyEvidence;
                try{if(F19Inventory(state.WorkPart)!=plan.F20InventoryBefore || M3SketchProjectionCurveInventoryText(CaptureM3SketchProjectionCurveInventory(state.WorkPart))!=plan.F12CurveInventoryBefore ||
                    !F12BodyFingerprintsEqual(plan.BodyFingerprintsBefore,CaptureF12BodyFingerprints(state.WorkPart,CaptureM3BodyMeasures(state.WorkPart,M3LegacyMassPropertyAccuracy)),out rollbackBodyEvidence))
                    return plan.Execution=M3Execution("UNKNOWN",binding.RevisionId,null,"ROLLBACK_UNVERIFIED","spatial curve rollback inventory differs");}
                catch(Exception){return plan.Execution=M3Execution("UNKNOWN",binding.RevisionId,null,"ROLLBACK_UNVERIFIED","spatial curve rollback inventory unavailable");}
                try
                {
                    plan.Execution.Result["rollback_revision_advance"]=AdvanceVerifiedF13RevolveRollbackRevision(binding,state,plan);
                    plan.Execution.RevisionId=binding.RevisionId;
                }
                catch(Exception){return plan.Execution=M3Execution("UNKNOWN",binding.RevisionId,null,"ROLLBACK_UNVERIFIED","restored revision could not be advanced safely");}
            }
            return plan.Execution;
        }
        finally{try{state.Session.DeleteUndoMark(mark,"F20 spatial curve transaction");}catch(Exception ex){throw M3AnalysisStateUnknown("spatial curve transaction mark cleanup failed",ex);}}
    }

    private M3VerificationResult VerifyF20SpatialCurvePlan(M2ModelBinding binding,M3PlanSnapshot prepared,M3PlanState plan)
    {
        if(plan.Execution==null || plan.Execution.State!="APPLIED")return M3VerificationFailure(binding.RevisionId,"spatial curve is not applied");
        BindingState state=RequireState(binding);string reason;
        if(!Object.ReferenceEquals(state,plan.BindingState)||!IsBindingCurrent(binding,out reason))return M3VerificationFailure(binding.RevisionId,"binding changed");
        Curve curve=null;foreach(Curve c in state.WorkPart.Curves.ToArray())if(c.Tag.ToString()==plan.F20CurveTag && c.JournalIdentifier==plan.F20CurveId){if(curve!=null)return M3VerificationFailure(binding.RevisionId,"curve identity is ambiguous");curve=c;}
        Dictionary<string,object> geometry,preservation;bool measured=F20ReadGeometry(curve,plan.F20Geometry,out geometry);
        SnapshotResult capture=Capture(binding,state);bool preserved=F20Preserved(state.WorkPart,plan,capture,out preservation);
        bool same=plan.F20GeometryAfter==M2Json.CanonicalText(geometry) && plan.SemanticAfter==CanonicalSemanticSnapshot(capture.Snapshot);
        bool verified=measured && preserved && same;
        Dictionary<string,object> checks=new Dictionary<string,object>{{"curve_geometry",VerificationCheck(measured && same?"VERIFIED":"MISMATCH",geometry)},
            {"model_preservation",VerificationCheck(preserved?"VERIFIED":"MISMATCH",preservation)}};
        return new M3VerificationResult{Verified=verified,RevisionId=binding.RevisionId,Checks=checks,
            Result=new Dictionary<string,object>{{"kind","create_spatial_curve"},{"curve_id",plan.F20CurveId},{"native_curve_type",curve==null?null:curve.GetType().Name},{"geometry",geometry},{"model_preservation",preservation}},
            ErrorCode=verified?null:"PART_VERIFICATION_FAILED",ErrorMessage=verified?null:"spatial curve geometry or preservation differs"};
    }
}
