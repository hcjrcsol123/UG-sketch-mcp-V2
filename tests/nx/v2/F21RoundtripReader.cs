// F21 independent reopened-part collector. The sole authorized operator runs it
// only in an independent session on the installed SaveCopy. No product imports.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;

public static class F21RoundtripReader
{
    static JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};
    static string output;static Dictionary<string,object> report;
    static void Save(){File.WriteAllText(output,json.Serialize(report),new UTF8Encoding(false));}
    static void Need(bool x,string message){if(!x)throw new InvalidOperationException(message);}
    static double[] P(Point3d p){return new double[]{p.X,p.Y,p.Z};}
    static List<object> Sketches(Part p)
    {
        List<object> rows=new List<object>();foreach(Sketch sketch in p.Sketches){List<object> geometry=new List<object>();
            foreach(NXObject item in sketch.GetAllGeometry()){Line line=item as Line;Need(line!=null,"unsupported sketch member");UFSket.ReferenceStatus status;
                UFSession.GetUFSession().Sket.AskReferenceStatus(sketch.Tag,line.Tag,out status);
                geometry.Add(new Dictionary<string,object>{{"id",line.JournalIdentifier},{"tag",line.Tag.ToString()},
                    {"start_mm",P(line.StartPoint)},{"end_mm",P(line.EndPoint)},{"reference_status",status.ToString()}});}
            rows.Add(new Dictionary<string,object>{{"id",sketch.JournalIdentifier},{"tag",sketch.Tag.ToString()},
                {"feature_id",sketch.Feature.JournalIdentifier},{"feature_tag",sketch.Feature.Tag.ToString()},{"origin_mm",P(sketch.Origin)},{"lines",geometry}});}
        return rows;
    }
    static List<object> Curves(Part p)
    {
        List<object> rows=new List<object>();foreach(Curve curve in p.Curves.ToArray()){
            Dictionary<string,object> row=new Dictionary<string,object>{{"id",curve.JournalIdentifier},{"tag",curve.Tag.ToString()}};
            Line line=curve as Line;Arc arc=curve as Arc;
            if(line!=null){row["kind"]="LINE";row["start_mm"]=P(line.StartPoint);row["end_mm"]=P(line.EndPoint);}
            else if(arc!=null){Point3d center;Vector3d x,y;arc.GetOrientation(out center,out x,out y);row["kind"]="ARC";row["center_mm"]=P(center);
                row["x_axis"]=new double[]{x.X,x.Y,x.Z};row["y_axis"]=new double[]{y.X,y.Y,y.Z};row["radius_mm"]=arc.Radius;row["start_rad"]=arc.StartAngle;row["end_rad"]=arc.EndAngle;
                UFSession uf=UFSession.GetUFSession();IntPtr ev=IntPtr.Zero;try{uf.Eval.Initialize(arc.Tag,out ev);Need(ev!=IntPtr.Zero,"missing arc evaluator");
                    double[] limits=new double[2];uf.Eval.AskLimits(ev,limits);List<double[]> points=new List<double[]>();
                    for(int i=0;i<3;i++){double[] point=new double[3];uf.Curve.EvaluateCurve(arc.Tag,limits[0]+(limits[1]-limits[0])*i/2,0,point);points.Add(point);}row["points_mm"]=points;}
                finally{if(ev!=IntPtr.Zero)uf.Eval.Free(ev);}}
            else throw new InvalidOperationException("unsupported curve type in fixture");rows.Add(row);}
        return rows;
    }
    sealed class InventoryState { public string Raw; public Dictionary<string,object> Maps; }
    static InventoryState Inventory(Part p,Session session)
    {
        bool dirtyBefore=p.IsModified;
        List<string> rows=new List<string>(); Dictionary<string,object> maps=new Dictionary<string,object>();
        foreach(Feature f in p.Features){List<string> parents=new List<string>();foreach(Feature a in f.GetParents())parents.Add(a.Tag.ToString());parents.Sort();rows.Add("F|"+f.Tag+"|"+f.JournalIdentifier+"|"+f.FeatureType+"|"+String.Join(",",parents.ToArray()));}
        foreach(Body b in p.Bodies){
            Dictionary<string,object> structure=F21SurfaceStructureReader.Read(b);string bodyJson=json.Serialize(structure);
            rows.Add("B|"+b.Tag+"|"+b.JournalIdentifier+"|"+b.IsSolidBody+"|"+bodyJson);
            if(p.IsModified!=dirtyBefore)throw new InvalidOperationException("UNKNOWN body inventory changed dirty state");
            Dictionary<string,object> parsed=(Dictionary<string,object>)json.DeserializeObject(bodyJson);
            Dictionary<string,Face> faces=new Dictionary<string,Face>();foreach(Face f in b.GetFaces())faces.Add(f.Tag.ToString(),f);
            foreach(object value in (object[])parsed["faces"]){Dictionary<string,object> item=(Dictionary<string,object>)value;
                if(!(bool)item["is_planar"])continue;string tag=(string)item["face_tag"];Need(faces.ContainsKey(tag)&&!maps.ContainsKey(tag),"UNKNOWN plane face identity");
                object[] uv=(object[])item["uv_limits"];Need(uv.Length==4,"UNKNOWN plane UV limits");double u0=F21InventoryComparer.Number(uv[0]),u1=F21InventoryComparer.Number(uv[1]),v0=F21InventoryComparer.Number(uv[2]),v1=F21InventoryComparer.Number(uv[3]);
                double[][] grid={new double[]{u0,v0},new double[]{u0,v1},new double[]{u1,v0},new double[]{u1,v1},new double[]{u0/2+u1/2,v0/2+v1/2}};
                List<object> samples=new List<object>();foreach(double[] param in grid){double[] point=new double[3],du=new double[3],dv=new double[3],duu=new double[3],dvv=new double[3],normal=new double[3],radii=new double[2];
                    UFSession.GetUFSession().Modl.AskFaceProps(faces[tag].Tag,param,point,du,dv,duu,dvv,normal,radii);
                    if(p.IsModified!=dirtyBefore)throw new InvalidOperationException("UNKNOWN physical mapping changed dirty state");
                    samples.Add(new Dictionary<string,object>{{"uv",param},{"point_mm",point},{"du",du},{"dv",dv}});}
                Dictionary<string,object> map=new Dictionary<string,object>{{"face_tag",tag},{"uv_limits",uv},{"samples",samples}};
                object normalized=json.DeserializeObject(json.Serialize(map));F21InventoryComparer.ValidateMap(normalized,tag,item["uv_limits"],item["direction"],item["origin_mm"]);maps.Add(tag,normalized);
            }
            if(p.IsModified!=dirtyBefore)throw new InvalidOperationException("UNKNOWN mapping inventory changed dirty state");}
        foreach(Expression e in p.Expressions)rows.Add("E|"+e.Tag+"|"+e.JournalIdentifier+"|"+e.RightHandSide+"|"+e.Value.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        foreach(Curve c in p.Curves.ToArray()){Line line=c as Line;if(line!=null)rows.Add("L|"+c.Tag+"|"+json.Serialize(new double[][]{P(line.StartPoint),P(line.EndPoint)}));
            else {Arc arc=c as Arc;if(arc!=null){Point3d center;Vector3d x,y;arc.GetOrientation(out center,out x,out y);rows.Add("A|"+c.Tag+"|"+json.Serialize(new object[]{P(center),x.X,x.Y,x.Z,y.X,y.Y,y.Z,arc.Radius,arc.StartAngle,arc.EndAngle}));}}}
        foreach(Sketch s in p.Sketches){List<string> members=new List<string>();foreach(NXObject c in s.GetAllGeometry())members.Add(c.Tag.ToString());members.Sort();rows.Add("S|"+s.Tag+"|"+s.JournalIdentifier+"|"+String.Join(",",members.ToArray()));}
        if(p.IsModified!=dirtyBefore)throw new InvalidOperationException("UNKNOWN inventory changed dirty state");
        rows.Sort(StringComparer.Ordinal);return new InventoryState{Raw=json.Serialize(new object[]{rows,p.IsModified,session.ActiveSketch==null}),Maps=maps};
    }
    static void CompareInventory(InventoryState before,InventoryState after,string label)
    {
        Dictionary<string,object> result=F21InventoryComparer.Compare(before.Raw,after.Raw,before.Maps,after.Maps);
        List<object> guards=(List<object>)report["inventory_comparisons"];
        guards.Add(new Dictionary<string,object>{{"label",label},{"before",before.Raw},{"after",after.Raw},{"maps_before",before.Maps},{"maps_after",after.Maps},{"result",result}});Save();
        if(!(bool)result["passed"])throw new InvalidOperationException("UNKNOWN inventory comparison "+result["first_difference"]);
    }
    static object ProfileRules(Section section)
    {
        List<object> records=new List<object>();SectionData[] entries;section.GetSectionData(out entries);Need(entries!=null&&entries.Length>0,"no profile data");
        try{foreach(SectionData entry in entries){SelectionIntentRule[] rules;entry.GetRules(out rules);Need(rules!=null,"no rules");
            foreach(SelectionIntentRule rule in rules){CurveFeatureRule featureRule=rule as CurveFeatureRule;Need(featureRule!=null,"profile rule not CurveFeatureRule");Feature[] features;featureRule.GetData(out features);
                Need(features!=null&&features.Length==1,"profile feature rule not unique");records.Add(new Dictionary<string,object>{{"rule_type","CurveFeatureRule"},{"feature_id",features[0].JournalIdentifier},{"feature_tag",features[0].Tag.ToString()}});}}}
        finally{foreach(SectionData e in entries)if(e!=null)e.Dispose();}
        return records;
    }
    static object ProfileOutput(Section section)
    {
        NXObject[] output;section.GetOutputCurves(out output);Need(output!=null,"missing profile output");List<object> result=new List<object>();
        foreach(NXObject item in output){Line line=item as Line;Need(line!=null,"unsupported native profile output curve");
            result.Add(new Dictionary<string,object>{{"id",line.JournalIdentifier},{"tag",line.Tag.ToString()},
                {"start_mm",P(line.StartPoint)},{"end_mm",P(line.EndPoint)}});}return result;
    }
    static object Guide(Section section)
    {
        List<object> records=new List<object>();SectionData[] entries;section.GetSectionData(out entries);Need(entries!=null&&entries.Length>0,"no guide data");
        try{foreach(SectionData entry in entries){SelectionIntentRule[] rules;entry.GetRules(out rules);Need(rules!=null,"no guide rules");List<string> members=new List<string>();
            foreach(SelectionIntentRule rule in rules){CurveDumbRule dumb=rule as CurveDumbRule;Need(dumb!=null,"guide rule not CurveDumbRule");Curve[] curves;dumb.GetData(out curves);foreach(Curve c in curves)members.Add(c.Tag.ToString());}
            SectionElementData[] elements;entry.GetSectionElementsData(out elements);Need(elements!=null,"no guide elements");try{foreach(SectionElementData element in elements){DisplayableObject selected,start,end;Point3d a,b;
                element.GetSectionElementData1(out selected,out start,out a,out end,out b);Need(selected!=null,"null guide element");
                records.Add(new Dictionary<string,object>{{"id",selected.JournalIdentifier},{"tag",selected.Tag.ToString()},{"type",selected.GetType().Name},
                    {"rule_member_tags",members.ToArray()},{"start_mm",P(a)},{"end_mm",P(b)}});}}
            finally{foreach(SectionElementData el in elements)if(el!=null)el.Dispose();}}}
        finally{foreach(SectionData e in entries)if(e!=null)e.Dispose();}return records;
    }
    static Dictionary<string,object> Definition(Session session,Part part,Swept feature)
    {
        InventoryState before=Inventory(part,session);bool dirty=part.IsModified;Session.UndoMarkId mark=session.SetUndoMark(Session.MarkVisibility.Invisible,"F21 independent read");
        Dictionary<string,object> result=null;Exception failure=null;SweptBuilder builder=null;bool builderCreated=false;
        try{
            builder=part.Features.CreateSweptBuilder(feature);Need(builder!=null,"builder unavailable");builderCreated=true;Section[] profiles=builder.SectionList.GetContents(),guides=builder.GuideList.GetContents();
            Need(profiles!=null&&profiles.Length==1&&guides!=null&&guides.Length==1,"wrong Section count");
            List<string> parents=new List<string>();foreach(Feature p in feature.GetParents())parents.Add(p.Tag.ToString());
            Expression start=null,end=null;string orientation=builder.OrientationMethod.OrientationOption.ToString();
            if(orientation=="ByAngularLaw"){start=builder.OrientationMethod.AngularLaw.StartValue;end=builder.OrientationMethod.AngularLaw.EndValue;}
            result=new Dictionary<string,object>{{"feature_id",feature.JournalIdentifier},{"feature_tag",feature.Tag.ToString()},
                {"feature_type",feature.FeatureType},{"parent_feature_tags",parents},{"profile_rules",ProfileRules(profiles[0])},{"profile_output",ProfileOutput(profiles[0])},
                {"guide_elements",Guide(guides[0])},{"native_orientation",orientation},
                {"native_law",builder.OrientationMethod.AngularLaw.LawType.ToString()},
                {"native_start_deg",start==null?null:(object)start.Value},{"native_end_deg",end==null?null:(object)end.Value},
                {"start_formula",start==null?null:start.RightHandSide},{"end_formula",end==null?null:end.RightHandSide},
                {"start_unit",start==null||start.Units==null?null:start.Units.Name},{"end_unit",end==null||end.Units==null?null:end.Units.Name},
                {"preserve_shape",builder.PreserveShapeOption},{"preserve_guide_shape",builder.PreserveGuideShapeOption},
                {"g0",builder.G0Tolerance},{"g1",builder.G1Tolerance}};
        }catch(Exception e){failure=builderCreated?e:new InvalidOperationException("UNKNOWN builder creation",e);}
        finally{if(builder!=null)try{builder.Destroy();}catch(Exception ex){failure=new InvalidOperationException("UNKNOWN builder destroy",ex);}}
        if(failure!=null && failure.Message.IndexOf("UNKNOWN",StringComparison.OrdinalIgnoreCase)>=0)throw failure;
        try{session.UndoToMarkWithStatus(mark,"F21 independent read");int count=session.UpdateManager.DoUpdate(mark);session.DeleteUndoMark(mark,"F21 independent read");
            if(count!=0||part.IsModified!=dirty)throw new InvalidOperationException("dirty or update changed");
            InventoryState after=Inventory(part,session);CompareInventory(before,after,"definition:"+feature.Tag.ToString());}
        catch(Exception ex){throw new InvalidOperationException("UNKNOWN independent read restoration",ex);}
        if(failure!=null)throw failure;return result;
    }
    public static int Run()
    {
        output=Environment.GetEnvironmentVariable("F21_REOPEN_RAW");string manifestPath=Environment.GetEnvironmentVariable("F21_ROUNDTRIP_MANIFEST");
        Need(!String.IsNullOrEmpty(output)&&!String.IsNullOrEmpty(manifestPath)&&!File.Exists(output),"unique output and manifest required");
        Dictionary<string,object> manifest=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(manifestPath,Encoding.UTF8));
        Session session=Session.GetSession();Part part=session.Parts.Work;Need(part!=null&&!part.IsModified&&session.ActiveSketch==null,"clean reopened part required");
        List<object> rows=new List<object>();report=new Dictionary<string,object>{{"scope","independent reopened raw collector; external oracle pending"},
            {"model_path",part.FullPath},{"dirty_before",part.IsModified},{"cases",rows},{"inventory_comparisons",new List<object>()},{"passed",false}};
        using(FileStream stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(StreamWriter writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(json.Serialize(report));
        try{
            InventoryState before=Inventory(part,session);report["inventory_before"]=before.Raw;report["sketches"]=Sketches(part);
            if(part.IsModified)throw new InvalidOperationException("UNKNOWN sketch read changed dirty state");report["curves"]=Curves(part);
            if(part.IsModified)throw new InvalidOperationException("UNKNOWN curve read changed dirty state");
            List<Body> sentinel=new List<Body>();foreach(Body b in part.Bodies){double[] box=new double[6];UFSession.GetUFSession().Modl.AskBoundingBox(b.Tag,box);
                if(Math.Abs(box[0]+200)<1e-6 && Math.Abs(box[1]+200)<1e-6 && Math.Abs(box[2])<1e-6 &&
                   Math.Abs(box[3]+197)<1e-6 && Math.Abs(box[4]+196)<1e-6 && Math.Abs(box[5]-5)<1e-6)sentinel.Add(b);}
            Need(sentinel.Count==1,"sentinel solid is not uniquely identified");Dictionary<string,object> sentinelMeasurement;
            report["sentinel_geometry"]=F21StraightGeometryReader.Read(part,sentinel[0],out sentinelMeasurement);report["sentinel_measurement"]=sentinelMeasurement;Save();
            if(part.IsModified)throw new InvalidOperationException("UNKNOWN sentinel read changed dirty state");
            object[] cases=(object[])manifest["cases"];
            foreach(object value in cases){Dictionary<string,object> item=(Dictionary<string,object>)value;object[] rawOffset=(object[])item["offset_mm"];
                double[] offset=rawOffset.Select(x=>Convert.ToDouble(x,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                Dictionary<string,object> row=new Dictionary<string,object>{{"name",item["name"]},{"status","RUNNING"}};rows.Add(row);Save();
                List<Swept> hits=new List<Swept>();foreach(Swept f in part.Features.GetFeatures().OfType<Swept>()){Body[] bodies=f.GetBodies();if(bodies==null||bodies.Length!=1)continue;
                    double[] box=new double[6];UFSession.GetUFSession().Modl.AskBoundingBox(bodies[0].Tag,box);if(box[0]>=offset[0]-5&&box[3]<=offset[0]+20)hits.Add(f);}
                Need(hits.Count==1,"feature is not uniquely matched by region");Swept feature=hits[0];Body body=feature.GetBodies()[0];
                row["definition"]=Definition(session,part,feature);
                if((bool)item["sheet"]){Dictionary<string,object> measurement;row["sheet_geometry"]=F21StraightGeometryReader.Read(part,body,out measurement);row["measurement"]=measurement;}
                else row["body_capture"]=F21IndependentBodyCapture.Capture(body,(string)item["path"],(string)item["mode"],Convert.ToDouble(item["angle"]),offset);
                if(part.IsModified)throw new InvalidOperationException("UNKNOWN independent capture changed dirty state");
                row["status"]="CAPTURED_PENDING_ORACLE";Save();
            }
            InventoryState after=Inventory(part,session);report["inventory_after"]=after.Raw;report["dirty_after"]=part.IsModified;
            Need(!part.IsModified,"UNKNOWN independent reopen read changed dirty state");CompareInventory(before,after,"outer");
            report["capture_completed"]=true;Save();return 0;
        }catch(Exception ex){report["failure"]=ex.ToString();report["capture_completed"]=false;report["unknown_or_stopped"]=true;Save();return 1;}
    }
    public static int GetUnloadOption(string dummy){return (int)Session.LibraryUnloadOption.Explicitly;}
}
